using System.Collections.Concurrent;
using System.Security.Cryptography;
using DeckBuilder.Core.Models;

namespace DeckBuilder.GameData;

/// <summary>
/// Workspace loader for modded DotP installations. Mod WADs do not always keep CARD_V2 XML
/// below DATA_ALL_PLATFORMS\CARDS, so the editor must identify cards by XML content/internal
/// FILENAME rather than by the physical archive directory. Existing deck parsing is delegated
/// to WorkspacePoolLoader and then rebound to the complete card catalog.
/// </summary>
public sealed class WorkspaceDeepPoolLoader
{
    private readonly WorkspacePoolLoader _baseLoader = new();
    private readonly GameVersionPackageService _packageService = new();

    public async Task<WorkspacePoolLoadResult> LoadAsync(
        string workspaceDirectory,
        IProgress<CatalogLoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceDirectory);
        if (!Directory.Exists(workspaceDirectory))
        {
            throw new DirectoryNotFoundException(workspaceDirectory);
        }

        WorkspacePoolLoadResult baseResult = await _baseLoader.LoadAsync(
            workspaceDirectory,
            progress,
            cancellationToken);

        DeepScanResult deep = await Task.Run(
            () => ScanAllCardXml(workspaceDirectory, cancellationToken),
            cancellationToken);

        Dictionary<string, CardRecord> catalogByReference = new(StringComparer.OrdinalIgnoreCase);
        foreach (WorkspaceContentVariant variant in deep.Scan.CardVariants
                     .GroupBy(item => item.Reference, StringComparer.OrdinalIgnoreCase)
                     .Select(group => group
                         .OrderBy(item => item.IsRecommended ? 1 : 0)
                         .ThenBy(item => item.WadOrder)
                         .ThenBy(item => item.WadName, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(item => item.PackageName, StringComparer.OrdinalIgnoreCase)
                         .Last()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                CardRecord? parsed = CardXmlParser.Parse(
                    File.ReadAllText(variant.StoragePath),
                    $"Workspace: {variant.PackageName} / {variant.WadName}");
                if (parsed is not null)
                {
                    catalogByReference[parsed.FileName] = EnsureImageReference(parsed);
                }
            }
            catch (Exception exception)
            {
                deep.Warnings.Add($"{variant.PackageName} / {variant.WadName} / {variant.RelativePath}: {exception.Message}");
            }
        }

        // Keep any definition recovered by the older loader as a fallback. The deep scan wins
        // when both know the same exact reference because it preserves workspace variant data.
        foreach (CardRecord card in baseResult.Cards)
        {
            catalogByReference.TryAdd(card.FileName, card);
        }

        CardRecord[] catalog = catalogByReference.Values
            .OrderBy(card => card.LocalizedName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(card => card.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        progress?.Report(new CatalogLoadProgress("all workspace CARD_V2 XML", 3, catalog.Length));

        InstalledDeckRecord[] decks = baseResult.Decks
            .Select(deck => new InstalledDeckRecord(
                deck.FileName,
                deck.Source,
                RebindDeck(deck.Deck, catalogByReference)))
            .OrderBy(deck => deck.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(deck => deck.Source, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(deck => deck.Uid)
            .ToArray();

        string[] warnings = baseResult.Warnings
            .Concat(deep.Warnings)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new WorkspacePoolLoadResult(
            catalog,
            decks,
            deep.Scan,
            warnings,
            deep.Scan.PackageCount,
            deep.Scan.WadCount);
    }

    private DeepScanResult ScanAllCardXml(string workspaceDirectory, CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(workspaceDirectory);
        string[] manifestPaths = Directory.EnumerateFiles(
                root,
                GameVersionPackageService.ManifestFileName,
                SearchOption.AllDirectories)
            .OrderBy(path => Path.GetDirectoryName(path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (manifestPaths.Length == 0)
        {
            throw new InvalidDataException($"No extracted version packages were found below {root}.");
        }

        ConcurrentBag<string> warnings = new();
        List<CardCandidate> cards = new();
        List<ArtCandidate> art = new();
        int wadCount = 0;

        foreach (string manifestPath in manifestPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string packageDirectory = Path.GetDirectoryName(manifestPath)!;
            DotpVersionPackageManifest manifest = _packageService.ReadManifest(packageDirectory);

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
                        warnings.Add($"Missing extracted payload: {storagePath}");
                        continue;
                    }

                    string relative = AllPlatformsRelative(file.ArchivePath) ?? file.ArchivePath.Replace('/', '\\');
                    if (Path.GetExtension(relative).Equals(".tdx", StringComparison.OrdinalIgnoreCase)
                        && TryIllustrationId(relative, out string imageId))
                    {
                        art.Add(new ArtCandidate(
                            manifest.VersionName,
                            wad.Name,
                            wad.PrimaryOrder,
                            relative,
                            imageId,
                            storagePath,
                            HashFile(storagePath)));
                        continue;
                    }

                    if (!Path.GetExtension(relative).Equals(".xml", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        CardRecord? card = CardXmlParser.Parse(
                            File.ReadAllText(storagePath),
                            $"{manifest.VersionName} / {wad.Name}");
                        if (card is null)
                        {
                            continue;
                        }

                        cards.Add(new CardCandidate(
                            manifest.VersionName,
                            wad.Name,
                            wad.PrimaryOrder,
                            relative,
                            storagePath,
                            HashFile(storagePath),
                            card));
                    }
                    catch (Exception exception)
                    {
                        // Most XML files in a WAD are not card definitions; only report files that
                        // actually look like CARD_V2 so unrelated XML does not flood diagnostics.
                        try
                        {
                            string text = File.ReadAllText(storagePath);
                            if (text.IndexOf("CARD_V2", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                warnings.Add($"{manifest.VersionName} / {wad.Name} / {relative}: {exception.Message}");
                            }
                        }
                        catch
                        {
                            // Ignore secondary diagnostic read failure.
                        }
                    }
                }
            }
        }

        Dictionary<string, ArtCandidate> artByPackageAndId = art
            .GroupBy(item => $"{item.PackageName}\u001f{item.ImageId}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(item => item.Order)
                    .ThenBy(item => item.WadName, StringComparer.OrdinalIgnoreCase)
                    .Last(),
                StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ArtCandidate> artById = art
            .GroupBy(item => item.ImageId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(item => item.Order)
                    .ThenBy(item => item.WadName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.PackageName, StringComparer.OrdinalIgnoreCase)
                    .Last(),
                StringComparer.OrdinalIgnoreCase);

        List<WorkspaceContentVariant> variants = new();
        List<WorkspaceContentVariantConflict> conflicts = new();
        int identicalCopies = 0;

        foreach (IGrouping<string, CardCandidate> group in cards
                     .GroupBy(item => item.Card.FileName, StringComparer.OrdinalIgnoreCase))
        {
            CardCandidate[] ordered = group
                .OrderBy(item => item.Order)
                .ThenBy(item => item.WadName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.PackageName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            CardCandidate recommended = ordered[^1];
            WorkspaceContentVariant[] groupVariants = ordered
                .Select(item => ToVariant(item, item == recommended, artByPackageAndId, artById))
                .ToArray();
            variants.AddRange(groupVariants);

            string[] signatures = groupVariants
                .Select(item => $"{item.Sha256}|{item.ArtId}|{item.ArtSha256 ?? string.Empty}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (signatures.Length <= 1)
            {
                identicalCopies += Math.Max(0, groupVariants.Length - 1);
                continue;
            }

            WorkspaceContentVariant recommendedVariant = groupVariants[^1];
            conflicts.Add(new WorkspaceContentVariantConflict(
                $"@CARD_REFERENCE:{group.Key.ToUpperInvariant()}",
                recommendedVariant.RelativePath,
                recommendedVariant.DisplayName,
                true,
                recommendedVariant.SelectionKey,
                groupVariants));
        }

        WorkspaceContentVariantScanResult scan = new(
            UnpackedContentKind.Cards,
            manifestPaths.Length,
            wadCount,
            cards.Count,
            identicalCopies,
            conflicts
                .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.ConflictKey, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            variants
                .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Reference, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.PackageName, StringComparer.OrdinalIgnoreCase)
                .ToArray());
        return new DeepScanResult(scan, warnings.ToList());
    }

    private static WorkspaceContentVariant ToVariant(
        CardCandidate candidate,
        bool recommended,
        IReadOnlyDictionary<string, ArtCandidate> artByPackageAndId,
        IReadOnlyDictionary<string, ArtCandidate> artById)
    {
        CardRecord card = candidate.Card;
        ArtCandidate? chosenArt = null;
        if (!string.IsNullOrWhiteSpace(card.ImageId))
        {
            string packageKey = $"{candidate.PackageName}\u001f{card.ImageId}";
            if (!artByPackageAndId.TryGetValue(packageKey, out chosenArt))
            {
                artById.TryGetValue(card.ImageId, out chosenArt);
            }
        }

        string selectionKey = WorkspaceContentWadBuilder.CreateSelectionKey(
            candidate.PackageName,
            candidate.WadName,
            candidate.RelativePath,
            candidate.Sha256);
        string? artSelectionKey = chosenArt is null
            ? null
            : WorkspaceContentWadBuilder.CreateSelectionKey(
                chosenArt.PackageName,
                chosenArt.WadName,
                chosenArt.RelativePath,
                chosenArt.Sha256);

        return new WorkspaceContentVariant(
            selectionKey,
            candidate.RelativePath,
            candidate.PackageName,
            candidate.WadName,
            candidate.Order,
            candidate.Sha256,
            candidate.StoragePath,
            recommended,
            true,
            card.LocalizedName,
            card.FileName,
            card.CastingCost,
            card.TypeLine,
            string.IsNullOrWhiteSpace(card.Power) && string.IsNullOrWhiteSpace(card.Toughness)
                ? string.Empty
                : $"{card.Power}/{card.Toughness}",
            card.RulesText,
            card.Expansion,
            card.Artist,
            card.ImageId,
            chosenArt?.StoragePath,
            chosenArt?.Sha256,
            chosenArt?.RelativePath,
            artSelectionKey);
    }

    private static DeckDocument RebindDeck(
        DeckDocument source,
        IReadOnlyDictionary<string, CardRecord> catalog)
    {
        DeckDocument result = new()
        {
            Uid = source.Uid,
            Name = source.Name,
            Description = source.Description,
            Personality = source.Personality,
            CustomPersonality = source.CustomPersonality?.Clone(),
            DeckBoxImage = source.DeckBoxImage,
            DeckBoxImageLocked = source.DeckBoxImageLocked,
            ContentPack = source.ContentPack,
            Availability = source.Availability,
            OverrideColours = source.OverrideColours,
            OverrideColour = source.OverrideColour,
            CreatureSize = source.CreatureSize,
            DeckSpeed = source.DeckSpeed,
            Flexibility = source.Flexibility,
            Synergy = source.Synergy,
            IgnoreCmcOver = source.IgnoreCmcOver,
            MinForests = source.MinForests,
            MinIslands = source.MinIslands,
            MinMountains = source.MinMountains,
            MinPlains = source.MinPlains,
            MinSwamps = source.MinSwamps,
            NumberOfSpellsThatCountAsLand = source.NumberOfSpellsThatCountAsLand
        };
        CopySection(source.MainDeck, result.MainDeck, catalog);
        CopySection(source.RegularUnlocks, result.RegularUnlocks, catalog);
        CopySection(source.PromoUnlocks, result.PromoUnlocks, catalog);
        return result;
    }

    private static void CopySection(
        IEnumerable<DeckEntry> source,
        IList<DeckEntry> target,
        IReadOnlyDictionary<string, CardRecord> catalog)
    {
        foreach (DeckEntry entry in source)
        {
            CardRecord card = catalog.TryGetValue(entry.Card.FileName, out CardRecord? resolved)
                ? resolved
                : entry.Card;
            target.Add(new DeckEntry(card, entry.Quantity, entry.Bias, entry.Promo, entry.OrderId));
        }
    }

    private static CardRecord EnsureImageReference(CardRecord card)
    {
        if (!string.IsNullOrWhiteSpace(card.ImageId))
        {
            return card;
        }

        return new CardRecord(
            card.FileName,
            card.LocalizedName,
            card.EnglishName,
            card.TypeLine,
            card.Expansion,
            card.Artist,
            card.CastingCost,
            card.Colour,
            card.Rarity,
            card.Power,
            card.Toughness,
            card.Source,
            card.FileName,
            card.RulesText,
            card.FlavorText,
            card.FrameType,
            card.IsToken);
    }

    private static string? AllPlatformsRelative(string archivePath)
    {
        string normalized = archivePath.Replace('/', '\\');
        const string marker = "\\DATA_ALL_PLATFORMS\\";
        int index = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            return normalized[(index + marker.Length)..];
        }

        const string prefix = "DATA_ALL_PLATFORMS\\";
        return normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? normalized[prefix.Length..]
            : null;
    }

    private static bool TryIllustrationId(string relativePath, out string imageId)
    {
        string normalized = relativePath.Replace('/', '\\');
        const string marker = "ART_ASSETS\\ILLUSTRATIONS\\";
        int index = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0 || !normalized.EndsWith(".tdx", StringComparison.OrdinalIgnoreCase))
        {
            imageId = string.Empty;
            return false;
        }

        imageId = Path.GetFileNameWithoutExtension(normalized);
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

    private sealed record CardCandidate(
        string PackageName,
        string WadName,
        int Order,
        string RelativePath,
        string StoragePath,
        string Sha256,
        CardRecord Card);

    private sealed record ArtCandidate(
        string PackageName,
        string WadName,
        int Order,
        string RelativePath,
        string ImageId,
        string StoragePath,
        string Sha256);

    private sealed record DeepScanResult(
        WorkspaceContentVariantScanResult Scan,
        List<string> Warnings);
}
