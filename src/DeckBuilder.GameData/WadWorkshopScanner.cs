using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using DeckBuilder.Core.Models;
using Gibbed.Duels.FileFormats;
using Gibbed.IO;
using ICSharpCode.SharpZipLib.Zip.Compression.Streams;
using Wad = Gibbed.Duels.FileFormats.Wad;

namespace DeckBuilder.GameData;

public sealed record WadWorkshopProgress(string Source, string Stage, int Completed, int Total);

public sealed record WadInventoryRecord(
    string Path,
    string Name,
    long Size,
    string Sha256,
    bool IsLoadable,
    int PrimaryOrder,
    int CardDefinitions,
    int Illustrations,
    int DeckImages,
    int DeckFiles,
    int UnlockFiles,
    int Personalities,
    string Notes)
{
    public string SizeText => $"{Size / 1024d / 1024d:N1} MiB";
    public string ShaShort => Sha256.Length <= 12 ? Sha256 : Sha256[..12];
    public string LoadState => IsLoadable ? "Loaded" : "Ignored";
}

public sealed record CardPoolRecord(
    string Reference,
    CardRecord? EffectiveCard,
    string EffectiveSource,
    int EffectiveOrder,
    int DefinitionCount,
    int ArtSourceCount,
    string Status,
    string DefinitionsText,
    string ArtSourcesText)
{
    public string DisplayName => EffectiveCard is null
        ? Reference
        : string.IsNullOrWhiteSpace(EffectiveCard.LocalizedName)
            ? Reference
            : EffectiveCard.LocalizedName;

    public bool HasDefinition => DefinitionCount > 0;
    public bool HasArt => ArtSourceCount > 0;
}

public sealed record DeckHealthRecord(
    InstalledDeckRecord Deck,
    int MissingDefinitions,
    int MissingArt,
    int AmbiguousReferences,
    int OverriddenReferences,
    string ProblemsText)
{
    public string DisplayName => Deck.DisplayName;
    public int Uid => Deck.Uid;
    public string Source => Deck.Source;
    public int MainCards => Deck.CardCount;
    public int RegularUnlocks => Deck.RegularUnlockCount;
    public int PromoUnlocks => Deck.PromoUnlockCount;

    public string Status => MissingDefinitions > 0
        ? "Missing definitions"
        : AmbiguousReferences > 0
            ? "Ambiguous"
            : MissingArt > 0
                ? "Missing art"
                : OverriddenReferences > 0
                    ? "Overrides"
                    : "OK";
}

public sealed record WadWorkshopSnapshot(
    string GameDirectory,
    IReadOnlyList<WadInventoryRecord> Wads,
    IReadOnlyList<CardPoolRecord> CardPool,
    IReadOnlyList<CardPoolRecord> Conflicts,
    IReadOnlyList<DeckHealthRecord> Decks,
    IReadOnlyList<string> Warnings);

public sealed class WadWorkshopScanner
{
    private const string CardDirectory = "DATA_ALL_PLATFORMS\\CARDS";
    private const string IllustrationDirectory = "DATA_ALL_PLATFORMS\\ART_ASSETS\\ILLUSTRATIONS";
    private const string DeckImageDirectory = "DATA_ALL_PLATFORMS\\ART_ASSETS\\TEXTURES\\DECKS";
    private const string DeckDirectory = "DATA_ALL_PLATFORMS\\DECKS";
    private const string UnlockDirectory = "DATA_ALL_PLATFORMS\\UNLOCKS";
    private const string PersonalityDirectory = "DATA_ALL_PLATFORMS\\AI_PERSONALITIES";

    public async Task<WadWorkshopSnapshot> ScanAsync(
        string gameDirectory,
        IProgress<WadWorkshopProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        if (!Directory.Exists(gameDirectory))
        {
            throw new DirectoryNotFoundException(gameDirectory);
        }

        return await Task.Run(
            () => Scan(gameDirectory, progress, cancellationToken),
            cancellationToken);
    }

    private static WadWorkshopSnapshot Scan(
        string gameDirectory,
        IProgress<WadWorkshopProgress>? progress,
        CancellationToken cancellationToken)
    {
        string[] wadPaths = Directory.EnumerateFiles(gameDirectory, "*.wad", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        List<WadInventoryRecord> inventory = new();
        List<CardDefinitionCandidate> definitions = new();
        List<ArtCandidate> illustrations = new();
        List<string> warnings = new();

        for (int index = 0; index < wadPaths.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string wadPath = wadPaths[index];
            progress?.Report(new WadWorkshopProgress(
                Path.GetFileName(wadPath) ?? wadPath,
                "Reading WAD inventory",
                index,
                wadPaths.Length));

            try
            {
                WadScanResult result = ScanWad(wadPath, cancellationToken);
                inventory.Add(result.Inventory);
                definitions.AddRange(result.Definitions);
                if (result.Inventory.IsLoadable)
                {
                    illustrations.AddRange(result.IllustrationIds.Select(id =>
                        new ArtCandidate(id, result.Inventory.Name, result.Inventory.PrimaryOrder)));
                }

                warnings.AddRange(result.Warnings);
            }
            catch (Exception exception)
            {
                string name = Path.GetFileName(wadPath) ?? wadPath;
                inventory.Add(new WadInventoryRecord(
                    wadPath,
                    name,
                    new FileInfo(wadPath).Length,
                    string.Empty,
                    GameWadSelection.IsSupported(wadPath),
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    exception.Message));
                warnings.Add($"{name}: {exception.Message}");
            }
        }

        Dictionary<string, ArtCandidate[]> artIndex = BuildArtIndex(illustrations);
        Dictionary<string, CardPoolRecord> pool = BuildCardPool(definitions, artIndex);
        CardRecord[] effectiveCatalog = pool.Values
            .Where(record => record.EffectiveCard is not null)
            .Select(record => record.EffectiveCard!)
            .ToArray();

        progress?.Report(new WadWorkshopProgress(
            "Decks",
            "Checking installed decks",
            wadPaths.Length,
            wadPaths.Length));
        GameDeckCatalogLoadResult deckResult = new GameDeckCatalogLoader()
            .LoadAsync(gameDirectory, effectiveCatalog, cancellationToken)
            .GetAwaiter()
            .GetResult();
        warnings.AddRange(deckResult.Warnings);

        foreach (InstalledDeckRecord deck in deckResult.Decks)
        {
            foreach (string reference in DeckReferences(deck))
            {
                if (pool.ContainsKey(reference))
                {
                    continue;
                }

                ArtCandidate[] art = GetArtCandidates(artIndex, reference);
                pool.Add(reference, new CardPoolRecord(
                    reference,
                    null,
                    string.Empty,
                    0,
                    0,
                    art.Select(candidate => candidate.Source)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count(),
                    "Referenced without definition",
                    string.Empty,
                    FormatArtSources(art)));
            }
        }

        DeckHealthRecord[] health = deckResult.Decks
            .Select(deck => BuildDeckHealth(deck, pool, artIndex))
            .OrderBy(deck => deck.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(deck => deck.Uid)
            .ThenBy(deck => deck.Source, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        CardPoolRecord[] poolRows = pool.Values
            .OrderBy(card => card.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(card => card.Reference, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        CardPoolRecord[] conflictRows = poolRows
            .Where(card => !card.Status.Equals("OK", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        progress?.Report(new WadWorkshopProgress(
            "Done",
            "Complete",
            wadPaths.Length,
            wadPaths.Length));
        return new WadWorkshopSnapshot(
            gameDirectory,
            inventory.OrderBy(wad => wad.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            poolRows,
            conflictRows,
            health,
            warnings.Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    private static WadScanResult ScanWad(string wadPath, CancellationToken cancellationToken)
    {
        bool loadable = GameWadSelection.IsSupported(wadPath);
        string source = Path.GetFileNameWithoutExtension(wadPath) ?? wadPath;
        List<CardDefinitionCandidate> definitions = new();
        List<string> illustrations = new();
        List<string> warnings = new();

        string sha256;
        using (FileStream hashInput = File.OpenRead(wadPath))
        {
            sha256 = Convert.ToHexString(SHA256.HashData(hashInput));
        }

        using FileStream input = File.OpenRead(wadPath);
        if (WadFile.IsBadHeader(input, out _, out _, out string reason))
        {
            throw new InvalidDataException(reason);
        }

        input.Position = 0;
        WadFile archive = new();
        archive.Deserialize(input);
        bool compressed = (archive.Flags & Wad.ArchiveFlags.HasCompressedFiles) == Wad.ArchiveFlags.HasCompressedFiles;
        int primaryOrder = ReadPrimaryOrder(archive.HeaderXml);

        Wad.DirectoryEntry? cardDirectory = FindDirectory(archive.Directories, CardDirectory);
        int cardCount = CountFiles(cardDirectory, ".xml");
        if (loadable && cardDirectory is not null)
        {
            foreach (Wad.FileEntry file in cardDirectory.Files.Where(file => HasExtension(file, ".xml")))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    byte[] data = ReadFile(input, archive, file, compressed);
                    CardRecord? card = CardXmlParser.Parse(DecodeText(data), source);
                    if (card is not null)
                    {
                        definitions.Add(new CardDefinitionCandidate(
                            card,
                            source,
                            primaryOrder,
                            Convert.ToHexString(SHA256.HashData(data)),
                            file.Name));
                    }
                }
                catch (Exception exception)
                {
                    warnings.Add($"{source}\\{file.Name}: {exception.Message}");
                }
            }
        }

        Wad.DirectoryEntry? artDirectory = FindDirectory(archive.Directories, IllustrationDirectory);
        if (loadable && artDirectory is not null)
        {
            illustrations.AddRange(artDirectory.Files
                .Where(file => HasExtension(file, ".tdx"))
                .Select(file => NormalizeId(file.Name))
                .Where(value => value.Length > 0));
        }

        int artCount = CountFiles(artDirectory, ".tdx");
        int deckImageCount = CountFiles(FindDirectory(archive.Directories, DeckImageDirectory), ".tdx");
        int deckCount = CountFiles(FindDirectory(archive.Directories, DeckDirectory), ".xml");
        int unlockCount = CountFiles(FindDirectory(archive.Directories, UnlockDirectory), ".xml");
        int personalityCount = CountFiles(FindDirectory(archive.Directories, PersonalityDirectory), ".xml");
        string notes = loadable
            ? string.Empty
            : Path.GetFileName(wadPath)?.Contains("HideOfficialDecks", StringComparison.OrdinalIgnoreCase) == true
                ? "Helper WAD ignored by the editor"
                : "Name does not match a game-loadable DATA_CORE / DATA_DLC_ / DATA_DECKS_ WAD";

        return new WadScanResult(
            new WadInventoryRecord(
                wadPath,
                Path.GetFileName(wadPath) ?? wadPath,
                new FileInfo(wadPath).Length,
                sha256,
                loadable,
                primaryOrder,
                cardCount,
                artCount,
                deckImageCount,
                deckCount,
                unlockCount,
                personalityCount,
                notes),
            definitions,
            illustrations,
            warnings);
    }

    private static Dictionary<string, ArtCandidate[]> BuildArtIndex(IEnumerable<ArtCandidate> illustrations) =>
        illustrations
            .GroupBy(candidate => candidate.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(candidate => candidate.Order)
                    .ThenByDescending(candidate => candidate.Source, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, CardPoolRecord> BuildCardPool(
        IEnumerable<CardDefinitionCandidate> definitions,
        IReadOnlyDictionary<string, ArtCandidate[]> artIndex)
    {
        Dictionary<string, CardPoolRecord> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, CardDefinitionCandidate> group in definitions.GroupBy(
                     definition => definition.Card.FileName,
                     StringComparer.OrdinalIgnoreCase))
        {
            // DotP 2014 gives a higher WAD_HEADER ENTRY order a higher priority. If order is equal,
            // the WAD that sorts later by name loads later and overrides the earlier WAD.
            CardDefinitionCandidate[] candidates = group
                .OrderByDescending(definition => definition.Order)
                .ThenByDescending(definition => definition.Source, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(definition => definition.PhysicalFileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            CardDefinitionCandidate effective = candidates[0];
            int uniqueHashes = candidates.Select(candidate => candidate.XmlHash)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            CardDefinitionCandidate[] effectiveWadCandidates = candidates.Where(candidate =>
                    candidate.Order == effective.Order
                    && candidate.Source.Equals(effective.Source, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            int effectiveWadHashes = effectiveWadCandidates.Select(candidate => candidate.XmlHash)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            string status;
            if (candidates.Length == 1)
            {
                status = "OK";
            }
            else if (uniqueHashes == 1)
            {
                status = "Duplicate identical";
            }
            else if (effectiveWadHashes > 1)
            {
                // Multiple different physical XML files in the same winning WAD claim the same
                // internal FILENAME. Header order and WAD name cannot resolve that deterministically.
                status = "Ambiguous";
            }
            else
            {
                status = "Overridden";
            }

            string imageId = string.IsNullOrWhiteSpace(effective.Card.ImageId)
                ? effective.Card.FileName
                : effective.Card.ImageId;
            ArtCandidate[] art = GetArtCandidates(artIndex, imageId);
            string definitionText = string.Join(
                Environment.NewLine,
                candidates.Select((candidate, index) =>
                    $"{(index == 0 ? "*" : " ")} order {candidate.Order}: " +
                    $"{candidate.Source}\\{candidate.PhysicalFileName} [{candidate.XmlHash[..12]}]"));

            result[group.Key] = new CardPoolRecord(
                group.Key,
                effective.Card,
                effective.Source,
                effective.Order,
                candidates.Length,
                art.Select(candidate => candidate.Source)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count(),
                status,
                definitionText,
                FormatArtSources(art));
        }

        return result;
    }

    private static DeckHealthRecord BuildDeckHealth(
        InstalledDeckRecord deck,
        IReadOnlyDictionary<string, CardPoolRecord> pool,
        IReadOnlyDictionary<string, ArtCandidate[]> artIndex)
    {
        string[] references = DeckReferences(deck)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        List<string> problems = new();
        int missingDefinitions = 0;
        int missingArt = 0;
        int ambiguous = 0;
        int overridden = 0;

        foreach (string reference in references)
        {
            pool.TryGetValue(reference, out CardPoolRecord? card);
            if (card is null || !card.HasDefinition)
            {
                missingDefinitions++;
                problems.Add($"No definition: {reference}");
            }
            else if (card.Status.Equals("Ambiguous", StringComparison.OrdinalIgnoreCase))
            {
                ambiguous++;
                problems.Add($"Ambiguous definition: {reference}");
            }
            else if (card.Status.Equals("Overridden", StringComparison.OrdinalIgnoreCase))
            {
                overridden++;
            }

            string imageId = card?.EffectiveCard is null || string.IsNullOrWhiteSpace(card.EffectiveCard.ImageId)
                ? reference
                : card.EffectiveCard.ImageId;
            if (GetArtCandidates(artIndex, imageId).Length == 0)
            {
                missingArt++;
                problems.Add($"No illustration: {reference} -> {imageId}");
            }
        }

        return new DeckHealthRecord(
            deck,
            missingDefinitions,
            missingArt,
            ambiguous,
            overridden,
            string.Join(Environment.NewLine, problems));
    }

    private static IEnumerable<string> DeckReferences(InstalledDeckRecord deck) => deck.Deck.MainDeck
        .Concat(deck.Deck.RegularUnlocks)
        .Concat(deck.Deck.PromoUnlocks)
        .Select(entry => entry.Card.FileName);

    private static ArtCandidate[] GetArtCandidates(
        IReadOnlyDictionary<string, ArtCandidate[]> artIndex,
        string imageId) => artIndex.TryGetValue(imageId, out ArtCandidate[]? candidates)
        ? candidates
        : Array.Empty<ArtCandidate>();

    private static string FormatArtSources(IReadOnlyList<ArtCandidate> art)
    {
        if (art.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(
            " -> ",
            art.GroupBy(candidate => candidate.Source, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Select((candidate, index) =>
                    $"{(index == 0 ? "* " : string.Empty)}{candidate.Source} (order {candidate.Order})"));
    }

    private static int ReadPrimaryOrder(byte[]? header)
    {
        if (header is null || header.Length == 0)
        {
            return 0;
        }

        try
        {
            XDocument xml = XDocument.Parse(DecodeText(header));
            XElement? entry = xml.Descendants().FirstOrDefault(element =>
                element.Name.LocalName.Equals("ENTRY", StringComparison.OrdinalIgnoreCase)
                && Attribute(element, "platform").Equals("ALL", StringComparison.OrdinalIgnoreCase));
            return entry is not null && int.TryParse(Attribute(entry, "order"), out int order) ? order : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static int CountFiles(Wad.DirectoryEntry? directory, string extension) => directory?.Files.Count(file =>
        HasExtension(file, extension)) ?? 0;

    private static bool HasExtension(Wad.FileEntry file, string extension) =>
        Path.GetExtension(file.Name).Equals(extension, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeId(string value) =>
        (Path.GetFileNameWithoutExtension(value) ?? string.Empty).Trim().ToUpperInvariant();

    private static byte[] ReadFile(FileStream input, WadFile archive, Wad.FileEntry file, bool compressed)
    {
        input.Position = archive.DataOffsets[file.OffsetIndex];
        if (!compressed)
        {
            return ReadExactly(input, checked((int)file.Size));
        }

        int inflatedLength = input.ReadValueS32(archive.Endian);
        int storedLength = checked((int)file.Size) - 4;
        if (inflatedLength == -1)
        {
            return ReadExactly(input, storedLength);
        }

        using MemoryStream compressedData = new(ReadExactly(input, storedLength), writable: false);
        using InflaterInputStream inflater = new(compressedData);
        return ReadExactly(inflater, inflatedLength);
    }

    private static byte[] ReadExactly(Stream input, int length)
    {
        byte[] result = new byte[length];
        int offset = 0;
        while (offset < result.Length)
        {
            int read = input.Read(result, offset, result.Length - offset);
            if (read == 0)
            {
                throw new EndOfStreamException($"Expected {length} bytes, received {offset}.");
            }

            offset += read;
        }

        return result;
    }

    private static string DecodeText(byte[] data)
    {
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(data, 3, data.Length - 3);
        }

        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(data, 2, data.Length - 2);
        }

        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
        }

        return Encoding.UTF8.GetString(data);
    }

    private static Wad.DirectoryEntry? FindDirectory(IEnumerable<Wad.DirectoryEntry> directories, string path)
    {
        string[] parts = path.TrimEnd('\\').Split('\\');
        foreach (Wad.DirectoryEntry root in directories)
        {
            if (!root.Name.Equals(parts[0], StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Wad.DirectoryEntry? current = root;
            for (int index = 1; index < parts.Length && current is not null; index++)
            {
                current = current.Directories.FirstOrDefault(directory =>
                    directory.Name.Equals(parts[index], StringComparison.OrdinalIgnoreCase));
            }

            if (current is not null)
            {
                return current;
            }
        }

        foreach (Wad.DirectoryEntry directory in directories)
        {
            Wad.DirectoryEntry? found = FindDirectory(directory.Directories, path);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static string Attribute(XElement element, string name) => element.Attributes()
        .FirstOrDefault(attribute => attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?.Value.Trim() ?? string.Empty;

    private sealed record CardDefinitionCandidate(
        CardRecord Card,
        string Source,
        int Order,
        string XmlHash,
        string PhysicalFileName);

    private sealed record ArtCandidate(string Id, string Source, int Order);

    private sealed record WadScanResult(
        WadInventoryRecord Inventory,
        IReadOnlyList<CardDefinitionCandidate> Definitions,
        IReadOnlyList<string> IllustrationIds,
        IReadOnlyList<string> Warnings);
}
