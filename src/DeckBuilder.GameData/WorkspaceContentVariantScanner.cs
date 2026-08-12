using System.Security.Cryptography;
using System.Xml.Linq;
using DeckBuilder.Core.Models;

namespace DeckBuilder.GameData;

public sealed record WorkspaceContentVariant(
    string SelectionKey,
    string RelativePath,
    string PackageName,
    string WadName,
    int WadOrder,
    string Sha256,
    string StoragePath,
    bool IsRecommended,
    bool IsCardDefinition,
    string DisplayName,
    string Reference,
    string CastingCost,
    string TypeLine,
    string PowerToughness,
    string RulesText,
    string Expansion,
    string Artist,
    string ArtId,
    string? ArtStoragePath,
    string? ArtSha256,
    string? ArtSelectionIdentity,
    string? ArtSelectionKey)
{
    public string SourceText => $"{PackageName} / {WadName} / order {WadOrder}";
    public string VariantText => string.IsNullOrWhiteSpace(PowerToughness)
        ? $"{CastingCost}  {TypeLine}".Trim()
        : $"{CastingCost}  {TypeLine}  {PowerToughness}".Trim();
}

public sealed record WorkspaceContentVariantConflict(
    string ConflictKey,
    string RelativePath,
    string DisplayName,
    bool IsCardDefinition,
    string RecommendedSelectionKey,
    IReadOnlyList<WorkspaceContentVariant> Variants)
{
    public int VariantCount => Variants.Count;
}

public sealed record WorkspaceContentVariantScanResult(
    UnpackedContentKind Kind,
    int PackageCount,
    int WadCount,
    int SourceInstances,
    int IdenticalCopies,
    IReadOnlyList<WorkspaceContentVariantConflict> Conflicts,
    IReadOnlyList<WorkspaceContentVariant> CardVariants);

/// <summary>
/// Scans extracted DotP version packages and reports human-level conflicts. Card conflicts are
/// grouped by the internal CARD_V2 FILENAME and include the effective illustration from the same
/// extracted version. Raw illustration/frame payload differences remain in provenance and follow
/// game priority, but do not create hundreds of blocking file-level questions by themselves.
/// </summary>
public sealed class WorkspaceContentVariantScanner
{
    public Task<WorkspaceContentVariantScanResult> ScanAsync(
        string workspaceDirectory,
        UnpackedContentKind kind,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Scan(workspaceDirectory, kind, cancellationToken), cancellationToken);

    private static WorkspaceContentVariantScanResult Scan(
        string workspaceDirectory,
        UnpackedContentKind kind,
        CancellationToken cancellationToken)
    {
        string workspace = Path.GetFullPath(workspaceDirectory);
        string[] manifestPaths = Directory.EnumerateFiles(
                workspace,
                GameVersionPackageService.ManifestFileName,
                SearchOption.AllDirectories)
            .OrderBy(path => Path.GetDirectoryName(path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (manifestPaths.Length == 0)
        {
            throw new InvalidDataException($"No extracted version packages were found below {workspace}.");
        }

        GameVersionPackageService packageService = new();
        List<RawCandidate> raw = new();
        List<RawArt> art = new();
        int wadCount = 0;

        foreach (string manifestPath in manifestPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string packageDirectory = Path.GetDirectoryName(manifestPath)!;
            DotpVersionPackageManifest manifest = packageService.ReadManifest(packageDirectory);

            foreach (DotpWadPackageManifest wad in manifest.Wads)
            {
                wadCount++;
                string wadDirectory = Path.Combine(packageDirectory, "wads", SafeDirectoryName(wad.Name));
                foreach (DotpWadFileManifest file in wad.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string storagePath = Path.Combine(
                        wadDirectory,
                        file.StoragePath.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(storagePath))
                    {
                        continue;
                    }

                    string? relative = AllPlatformsRelative(file.ArchivePath);
                    if (relative is null)
                    {
                        continue;
                    }

                    string sha256 = HashFile(storagePath);
                    if (TryIllustrationId(relative, out string? imageId))
                    {
                        art.Add(new RawArt(
                            relative,
                            manifest.VersionName,
                            wad.Name,
                            wad.PrimaryOrder,
                            imageId,
                            storagePath,
                            sha256));
                    }

                    if (!WorkspaceContentWadBuilder.MatchesKind(relative, kind))
                    {
                        continue;
                    }

                    raw.Add(new RawCandidate(
                        WorkspaceContentWadBuilder.GetContentIdentity(relative, storagePath, kind),
                        relative,
                        manifest.VersionName,
                        wad.Name,
                        wad.PrimaryOrder,
                        storagePath,
                        sha256));
                }
            }
        }

        Dictionary<string, RawArt> preferredArtByPackageAndId = art
            .GroupBy(item => $"{item.PackageName}\u001f{item.ImageId}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(item => item.Order)
                    .ThenBy(item => item.WadName, StringComparer.OrdinalIgnoreCase)
                    .Last(),
                StringComparer.OrdinalIgnoreCase);
        Dictionary<string, RawArt> preferredArtById = art
            .GroupBy(item => item.ImageId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(item => item.Order)
                    .ThenBy(item => item.WadName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.PackageName, StringComparer.OrdinalIgnoreCase)
                    .Last(),
                StringComparer.OrdinalIgnoreCase);

        List<ParsedCard> parsedCards = new();
        foreach (RawCandidate candidate in raw.Where(IsCardXml))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CardRecord? card = TryParseCard(candidate);
            if (card is not null)
            {
                parsedCards.Add(new ParsedCard(candidate, card));
            }
        }

        Dictionary<string, ParsedCard> cardByStoragePath = parsedCards.ToDictionary(
            item => item.Source.StoragePath,
            StringComparer.OrdinalIgnoreCase);

        List<WorkspaceContentVariantConflict> conflicts = new();
        List<WorkspaceContentVariant> allCardVariants = new();
        int identicalCopies = 0;

        foreach (IGrouping<string, RawCandidate> group in raw.GroupBy(
                     item => item.Identity,
                     StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RawCandidate[] ordered = group
                .OrderBy(item => item.Order)
                .ThenBy(item => item.WadName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.PackageName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            RawCandidate recommended = ordered[^1];

            WorkspaceContentVariant[] variants = ordered
                .Select(item => ToVariant(
                    item,
                    item == recommended,
                    preferredArtByPackageAndId,
                    preferredArtById,
                    cardByStoragePath))
                .ToArray();

            allCardVariants.AddRange(variants.Where(item => item.IsCardDefinition));

            bool interactive = kind == UnpackedContentKind.Cards
                ? variants.Any(item => item.IsCardDefinition)
                : IsInteractiveDeckResource(recommended.RelativePath);

            bool differs = variants
                .Select(VariantSignature)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Skip(1)
                .Any();

            if (!differs)
            {
                identicalCopies += Math.Max(0, ordered.Length - 1);
                continue;
            }

            // Supporting textures and orphaned illustrations still get merged and recorded in
            // .sources.json, but they do not block a build with a raw-file decision dialog.
            if (!interactive)
            {
                continue;
            }

            WorkspaceContentVariant recommendedVariant = variants[^1];
            conflicts.Add(new WorkspaceContentVariantConflict(
                group.Key,
                recommendedVariant.RelativePath,
                recommendedVariant.DisplayName,
                recommendedVariant.IsCardDefinition,
                recommendedVariant.SelectionKey,
                variants));
        }

        return new WorkspaceContentVariantScanResult(
            kind,
            manifestPaths.Length,
            wadCount,
            raw.Count,
            identicalCopies,
            conflicts
                .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            allCardVariants
                .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Reference, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.PackageName, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    private static string VariantSignature(WorkspaceContentVariant variant)
    {
        if (!variant.IsCardDefinition)
        {
            return variant.Sha256;
        }

        // Same CARD_V2 XML with a different effective illustration is still a real card variant.
        return $"{variant.Sha256}|{variant.ArtId}|{variant.ArtSha256 ?? string.Empty}";
    }

    private static WorkspaceContentVariant ToVariant(
        RawCandidate candidate,
        bool recommended,
        IReadOnlyDictionary<string, RawArt> artByPackageAndId,
        IReadOnlyDictionary<string, RawArt> artById,
        IReadOnlyDictionary<string, ParsedCard> cardByStoragePath)
    {
        bool isCardDefinition = IsCardXml(candidate);
        string displayName = Path.GetFileNameWithoutExtension(candidate.RelativePath);
        string reference = displayName;
        string castingCost = string.Empty;
        string typeLine = string.Empty;
        string powerToughness = string.Empty;
        string rulesText = string.Empty;
        string expansion = string.Empty;
        string artist = string.Empty;
        string artId = string.Empty;
        string? artStoragePath = null;
        string? artSha256 = null;
        string? artSelectionIdentity = null;
        string? artSelectionKey = null;

        if (isCardDefinition && cardByStoragePath.TryGetValue(candidate.StoragePath, out ParsedCard? parsed))
        {
            ApplyCardMetadata(
                parsed.Card,
                ref displayName,
                ref reference,
                ref castingCost,
                ref typeLine,
                ref powerToughness,
                ref rulesText,
                ref expansion,
                ref artist,
                ref artId);

            RawArt? chosenArt = null;
            if (!string.IsNullOrWhiteSpace(artId))
            {
                string packageKey = $"{candidate.PackageName}\u001f{artId}";
                if (artByPackageAndId.TryGetValue(packageKey, out RawArt? packageArt))
                {
                    chosenArt = packageArt;
                }
                else if (artById.TryGetValue(artId, out RawArt? fallbackArt))
                {
                    chosenArt = fallbackArt;
                }
            }

            if (chosenArt is not null)
            {
                artStoragePath = chosenArt.StoragePath;
                artSha256 = chosenArt.Sha256;
                artSelectionIdentity = chosenArt.RelativePath;
                artSelectionKey = WorkspaceContentWadBuilder.CreateSelectionKey(
                    chosenArt.PackageName,
                    chosenArt.WadName,
                    chosenArt.RelativePath,
                    chosenArt.Sha256);
            }
        }
        else if (candidate.RelativePath.StartsWith("DECKS\\", StringComparison.OrdinalIgnoreCase)
                 && candidate.RelativePath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            TryReadDeckSummary(candidate.StoragePath, ref displayName, ref reference, ref rulesText);
        }

        string key = WorkspaceContentWadBuilder.CreateSelectionKey(
            candidate.PackageName,
            candidate.WadName,
            candidate.RelativePath,
            candidate.Sha256);
        return new WorkspaceContentVariant(
            key,
            candidate.RelativePath,
            candidate.PackageName,
            candidate.WadName,
            candidate.Order,
            candidate.Sha256,
            candidate.StoragePath,
            recommended,
            isCardDefinition,
            displayName,
            reference,
            castingCost,
            typeLine,
            powerToughness,
            rulesText,
            expansion,
            artist,
            artId,
            artStoragePath,
            artSha256,
            artSelectionIdentity,
            artSelectionKey);
    }

    private static void ApplyCardMetadata(
        CardRecord card,
        ref string displayName,
        ref string reference,
        ref string castingCost,
        ref string typeLine,
        ref string powerToughness,
        ref string rulesText,
        ref string expansion,
        ref string artist,
        ref string artId)
    {
        displayName = card.LocalizedName;
        reference = card.FileName;
        castingCost = card.CastingCost;
        typeLine = card.TypeLine;
        powerToughness = string.IsNullOrWhiteSpace(card.Power) && string.IsNullOrWhiteSpace(card.Toughness)
            ? string.Empty
            : $"{card.Power}/{card.Toughness}";
        rulesText = card.RulesText;
        expansion = card.Expansion;
        artist = card.Artist;
        artId = card.ImageId;
    }

    private static CardRecord? TryParseCard(RawCandidate candidate)
    {
        try
        {
            return CardXmlParser.Parse(
                File.ReadAllText(candidate.StoragePath),
                $"{candidate.PackageName} / {candidate.WadName}");
        }
        catch
        {
            return null;
        }
    }

    private static bool IsCardXml(RawCandidate candidate) =>
        candidate.RelativePath.StartsWith("CARDS\\", StringComparison.OrdinalIgnoreCase)
        && candidate.RelativePath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);

    private static bool IsInteractiveDeckResource(string relativePath) =>
        (relativePath.StartsWith("DECKS\\", StringComparison.OrdinalIgnoreCase)
         || relativePath.StartsWith("UNLOCKS\\", StringComparison.OrdinalIgnoreCase))
        && relativePath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);

    private static void TryReadDeckSummary(
        string path,
        ref string displayName,
        ref string reference,
        ref string summary)
    {
        try
        {
            XDocument document = XDocument.Load(path);
            XElement? deck = document.Root;
            if (deck is null || !deck.Name.LocalName.Equals("DECK", StringComparison.OrdinalIgnoreCase))
            {
                deck = document.Descendants().FirstOrDefault(element =>
                    element.Name.LocalName.Equals("DECK", StringComparison.OrdinalIgnoreCase));
            }

            if (deck is null)
            {
                return;
            }

            string nameTag = Attribute(deck, "name_tag");
            if (!string.IsNullOrWhiteSpace(nameTag))
            {
                displayName = nameTag;
            }

            string uid = Attribute(deck, "uid");
            reference = string.IsNullOrWhiteSpace(uid) ? displayName : $"{displayName} (uid {uid})";
            int cards = deck.Elements().Count(element =>
                element.Name.LocalName.Equals("CARD", StringComparison.OrdinalIgnoreCase));
            summary = $"Deck cards: {cards}";
        }
        catch
        {
            // Keep filename metadata.
        }
    }

    private static string Attribute(XElement element, string name) => element.Attributes()
        .FirstOrDefault(attribute => attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?.Value.Trim() ?? string.Empty;

    private static string? AllPlatformsRelative(string archivePath)
    {
        string normalized = archivePath.Replace('/', '\\');
        const string marker = "\\DATA_ALL_PLATFORMS\\";
        int index = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? null : normalized[(index + marker.Length)..];
    }

    private static bool TryIllustrationId(string relativePath, out string imageId)
    {
        const string prefix = "ART_ASSETS\\ILLUSTRATIONS\\";
        if (!relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !relativePath.EndsWith(".tdx", StringComparison.OrdinalIgnoreCase))
        {
            imageId = string.Empty;
            return false;
        }

        imageId = Path.GetFileNameWithoutExtension(relativePath);
        return !string.IsNullOrWhiteSpace(imageId);
    }

    private static string HashFile(string path)
    {
        using FileStream input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private static string SafeDirectoryName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string result = new(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return result.Trim().TrimEnd('.');
    }

    private sealed record RawCandidate(
        string Identity,
        string RelativePath,
        string PackageName,
        string WadName,
        int Order,
        string StoragePath,
        string Sha256);

    private sealed record RawArt(
        string RelativePath,
        string PackageName,
        string WadName,
        int Order,
        string ImageId,
        string StoragePath,
        string Sha256);

    private sealed record ParsedCard(RawCandidate Source, CardRecord Card);
}
