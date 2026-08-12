using System.Collections.Concurrent;
using System.Xml.Linq;
using DeckBuilder.Core.Models;
using DeckBuilder.Core.Services;

namespace DeckBuilder.GameData;

public sealed record WorkspacePoolLoadResult(
    IReadOnlyList<CardRecord> Cards,
    IReadOnlyList<InstalledDeckRecord> Decks,
    WorkspaceContentVariantScanResult CardVariants,
    IReadOnlyList<string> Warnings,
    int PackageCount,
    int WadCount);

/// <summary>
/// Loads the editor directly from extracted version packages. No intermediate WAD is built:
/// every logical card reference is represented once in the catalog, while all physical variants
/// remain available in CardVariants for an explicit choice at final WAD export time.
/// Existing decks are exposed from every extracted package so the user can copy/import them.
/// </summary>
public sealed class WorkspacePoolLoader
{
    private readonly WorkspaceContentVariantScanner _variantScanner = new();
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

        WorkspaceContentVariantScanResult variants = await _variantScanner.ScanAsync(
            workspaceDirectory,
            UnpackedContentKind.Cards,
            cancellationToken);

        return await Task.Run(
            () => Load(workspaceDirectory, variants, progress, cancellationToken),
            cancellationToken);
    }

    private WorkspacePoolLoadResult Load(
        string workspaceDirectory,
        WorkspaceContentVariantScanResult variants,
        IProgress<CatalogLoadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ConcurrentBag<string> warnings = new();
        Dictionary<string, CardRecord> cards = new(StringComparer.OrdinalIgnoreCase);

        foreach (IGrouping<string, WorkspaceContentVariant> group in variants.CardVariants
                     .Where(item => !string.IsNullOrWhiteSpace(item.Reference))
                     .GroupBy(item => item.Reference, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            WorkspaceContentVariant selected = group
                .OrderBy(item => item.IsRecommended ? 1 : 0)
                .ThenBy(item => item.WadOrder)
                .ThenBy(item => item.WadName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.PackageName, StringComparer.OrdinalIgnoreCase)
                .Last();

            try
            {
                CardRecord? parsed = CardXmlParser.Parse(
                    File.ReadAllText(selected.StoragePath),
                    $"Workspace: {selected.PackageName} / {selected.WadName}");
                if (parsed is not null)
                {
                    cards[group.Key] = EnsureImageReference(parsed);
                }
            }
            catch (Exception exception)
            {
                warnings.Add($"{selected.PackageName} / {selected.WadName} / {selected.RelativePath}: {exception.Message}");
            }
        }

        CardRecord[] catalog = cards.Values
            .OrderBy(card => card.LocalizedName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(card => card.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        progress?.Report(new CatalogLoadProgress("workspace cards", 1, catalog.Length));

        string root = Path.GetFullPath(workspaceDirectory);
        string[] manifestPaths = Directory.EnumerateFiles(
                root,
                GameVersionPackageService.ManifestFileName,
                SearchOption.AllDirectories)
            .OrderBy(path => Path.GetDirectoryName(path), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        List<DeckCandidate> deckCandidates = new();
        List<UnlockCandidate> unlockCandidates = new();
        int wadCount = 0;

        foreach (string manifestPath in manifestPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string packageDirectory = Path.GetDirectoryName(manifestPath)!;
            DotpVersionPackageManifest manifest;
            try
            {
                manifest = _packageService.ReadManifest(packageDirectory);
            }
            catch (Exception exception)
            {
                warnings.Add($"{packageDirectory}: {exception.Message}");
                continue;
            }

            foreach (DotpWadPackageManifest wad in manifest.Wads)
            {
                wadCount++;
                string wadDirectory = Path.Combine(packageDirectory, "wads", SafeDirectoryName(wad.Name));
                foreach (DotpWadFileManifest file in wad.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string? relative = AllPlatformsRelative(file.ArchivePath);
                    if (relative is null || !relative.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string storagePath = Path.Combine(
                        wadDirectory,
                        file.StoragePath.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(storagePath))
                    {
                        warnings.Add($"Missing extracted payload: {storagePath}");
                        continue;
                    }

                    if (relative.StartsWith("DECKS\\", StringComparison.OrdinalIgnoreCase))
                    {
                        string fileName = Path.GetFileNameWithoutExtension(relative) ?? relative;
                        if (!fileName.Contains("_LAND_POOL", StringComparison.OrdinalIgnoreCase))
                        {
                            deckCandidates.Add(new DeckCandidate(
                                manifest.VersionName,
                                wad.Name,
                                wad.PrimaryOrder,
                                relative,
                                storagePath,
                                fileName.ToUpperInvariant()));
                        }
                    }
                    else if (relative.StartsWith("UNLOCKS\\", StringComparison.OrdinalIgnoreCase))
                    {
                        TryReadUnlockCandidate(
                            manifest.VersionName,
                            wad.Name,
                            wad.PrimaryOrder,
                            relative,
                            storagePath,
                            catalog,
                            unlockCandidates,
                            warnings);
                    }
                }
            }
        }

        List<InstalledDeckRecord> decks = new();
        foreach (DeckCandidate candidate in deckCandidates
                     .OrderBy(item => item.PackageName, StringComparer.CurrentCultureIgnoreCase)
                     .ThenBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.WadOrder)
                     .ThenBy(item => item.WadName, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                DeckDocument deck = DotpDeckXmlSerializer.Load(candidate.StoragePath, catalog);
                AttachUnlocks(deck, candidate.PackageName, unlockCandidates);
                decks.Add(new InstalledDeckRecord(
                    candidate.FileName,
                    $"{candidate.PackageName} / {candidate.WadName}",
                    deck));
            }
            catch (Exception exception)
            {
                warnings.Add($"{candidate.PackageName} / {candidate.WadName} / {candidate.RelativePath}: {exception.Message}");
            }
        }

        InstalledDeckRecord[] installedDecks = decks
            .OrderBy(deck => deck.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(deck => deck.Source, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(deck => deck.Uid)
            .ToArray();
        progress?.Report(new CatalogLoadProgress("workspace decks", 2, catalog.Length));

        return new WorkspacePoolLoadResult(
            catalog,
            installedDecks,
            variants,
            warnings.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
            manifestPaths.Length,
            wadCount);
    }

    private static void TryReadUnlockCandidate(
        string packageName,
        string wadName,
        int wadOrder,
        string relativePath,
        string storagePath,
        IReadOnlyList<CardRecord> catalog,
        ICollection<UnlockCandidate> output,
        ConcurrentBag<string> warnings)
    {
        try
        {
            string xml = File.ReadAllText(storagePath);
            XDocument document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            XElement? root = document.Root?.Name.LocalName.Equals("UNLOCKS", StringComparison.OrdinalIgnoreCase) == true
                ? document.Root
                : document.Descendants().FirstOrDefault(element =>
                    element.Name.LocalName.Equals("UNLOCKS", StringComparison.OrdinalIgnoreCase));
            if (root is null || !int.TryParse(Attribute(root, "deck_uid"), out int deckUid))
            {
                return;
            }

            bool promo = Attribute(root, "game_mode") == "2";
            DeckDocument parsed = DotpDeckXmlSerializer.Parse(xml, catalog);
            IReadOnlyList<DeckEntry> entries = (promo ? parsed.PromoUnlocks : parsed.RegularUnlocks)
                .Select(entry => new DeckEntry(entry.Card, 1, entry.Bias, entry.Promo, entry.OrderId))
                .ToArray();
            output.Add(new UnlockCandidate(
                packageName,
                wadName,
                wadOrder,
                relativePath,
                deckUid,
                promo,
                entries));
        }
        catch (Exception exception)
        {
            warnings.Add($"{packageName} / {wadName} / {relativePath}: {exception.Message}");
        }
    }

    private static void AttachUnlocks(
        DeckDocument deck,
        string packageName,
        IEnumerable<UnlockCandidate> allUnlocks)
    {
        UnlockCandidate[] matching = allUnlocks
            .Where(candidate => candidate.PackageName.Equals(packageName, StringComparison.OrdinalIgnoreCase)
                                && candidate.DeckUid == deck.Uid)
            .GroupBy(candidate => candidate.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(candidate => candidate.WadOrder)
                .ThenBy(candidate => candidate.WadName, StringComparer.OrdinalIgnoreCase)
                .Last())
            .OrderBy(candidate => candidate.Promo)
            .ThenBy(candidate => candidate.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (UnlockCandidate unlock in matching)
        {
            IList<DeckEntry> target = unlock.Promo ? deck.PromoUnlocks : deck.RegularUnlocks;
            foreach (DeckEntry entry in unlock.Entries)
            {
                target.Add(new DeckEntry(entry.Card, 1, entry.Bias, entry.Promo, entry.OrderId));
            }
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
        return index < 0 ? null : normalized[(index + marker.Length)..];
    }

    private static string Attribute(XElement element, string name) => element.Attributes()
        .FirstOrDefault(attribute => attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?.Value.Trim() ?? string.Empty;

    private static string SafeDirectoryName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string result = new(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return result.Trim().TrimEnd('.');
    }

    private sealed record DeckCandidate(
        string PackageName,
        string WadName,
        int WadOrder,
        string RelativePath,
        string StoragePath,
        string FileName);

    private sealed record UnlockCandidate(
        string PackageName,
        string WadName,
        int WadOrder,
        string RelativePath,
        int DeckUid,
        bool Promo,
        IReadOnlyList<DeckEntry> Entries);
}
