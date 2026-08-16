using System.Security;
using System.Text;
using System.Xml.Linq;
using DeckBuilder.Core.Models;
using DeckBuilder.Core.Services;
using Gibbed.Duels.FileFormats;
using Gibbed.IO;
using ICSharpCode.SharpZipLib.Zip.Compression;
using ICSharpCode.SharpZipLib.Zip.Compression.Streams;
using Wad = Gibbed.Duels.FileFormats.Wad;

namespace DeckBuilder.GameData;

public sealed record ModernWadExportOptions(
    string OutputPath,
    int Slot,
    string DeckName,
    string Description,
    int IdBlock = 1000,
    int SteamAppId = 213850);

public sealed record ModernWadExportResult(
    string WadPath,
    string? BackupPath,
    string ContentPackEnablerPath,
    bool ContentPackEnablerCreated,
    int DeckUid,
    int LandPoolUid,
    int RegularUnlockUid,
    int PromoUnlockUid,
    string DeckFileName);

public static class ModernWadExporter
{
    private const ushort WadVersion = 0x202;

    public static int SuggestSlot(string gameDirectory, int preferredDeckUid = -1)
    {
        int preferred = SlotFromDeckUid(preferredDeckUid);
        if (preferred >= 0)
        {
            return preferred;
        }

        HashSet<int> used = FindUsedSlots(gameDirectory);
        return Enumerable.Range(0, 100).FirstOrDefault(slot => !used.Contains(slot), -1);
    }

    public static ModernWadExportResult Export(
        DeckDocument deck,
        IReadOnlyList<CardRecord> catalog,
        ModernWadExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(deck);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(options);

        if (options.Slot is < 0 or > 99)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The default DotP slot must be between 0 and 99.");
        }

        MultiplayerDeckIdPlanner.ValidateContentPackId(options.IdBlock);

        if (deck.PromoUnlocks.Count > DeckDocument.MaximumPromoUnlocks)
        {
            throw new InvalidOperationException(
                $"Promo unlocks contain {deck.PromoUnlocks.Count} cards; Magic 2014 supports at most {DeckDocument.MaximumPromoUnlocks}.");
        }

        string outputPath = Path.GetFullPath(options.OutputPath);
        string outputDirectory = Path.GetDirectoryName(outputPath)
            ?? throw new DirectoryNotFoundException("The WAD output directory is missing.");
        Directory.CreateDirectory(outputDirectory);

        int deckUid = PrefixId(options.IdBlock, options.Slot);
        int landPoolUid = PrefixId((options.IdBlock * 10) + 1, options.Slot);
        int regularUnlockUid = PrefixId((options.IdBlock * 10) + 2, options.Slot);
        int promoUnlockUid = PrefixId((options.IdBlock * 10) + 3, options.Slot);
        string codeName = Codify(options.DeckName);
        string deckFileName = $"D14_{deckUid}_{codeName}";
        string rootName = Path.GetFileNameWithoutExtension(outputPath).ToUpperInvariant();

        AiPersonalityDefinition? customPersonality = deck.CustomPersonality is null
            ? null
            : AiPersonalityXmlSerializer.NormalizeIdentifiers(deck.CustomPersonality);
        string personalityReference = customPersonality?.FileName ?? deck.Personality;

        Dictionary<string, byte[]> files = new(StringComparer.OrdinalIgnoreCase)
        {
            [$"DATA_ALL_PLATFORMS\\DECKS\\{deckFileName}.XML"] = XmlBytes(
                CreateDeckXml(deck, deckUid, options.IdBlock, deckFileName, personalityReference)),
            [$"DATA_ALL_PLATFORMS\\DECKS\\{deckFileName}_LAND_POOL.XML"] = XmlBytes(
                CreateLandPoolXml(deck, catalog, landPoolUid, options.IdBlock)),
            [$"DATA_ALL_PLATFORMS\\TEXT_PERMANENT\\{deckFileName}_TEXT.XML"] = CreateLegacyStringTableBytes(
                deckFileName, options.DeckName, options.Description, customPersonality)
        };

        if (customPersonality is not null)
        {
            files[$"DATA_ALL_PLATFORMS\\AI_PERSONALITIES\\{customPersonality.FileName}"] = XmlBytes(
                AiPersonalityXmlSerializer.CreateStandaloneDocument(customPersonality));
        }

        int nextOrder = deck.MainDeckCardCount;
        if (deck.RegularUnlocks.Count > 0)
        {
            files[$"DATA_ALL_PLATFORMS\\UNLOCKS\\{deckFileName}_UNLOCK.XML"] = XmlBytes(
                CreateUnlockXml(deck.RegularUnlocks, regularUnlockUid, deckUid, promo: false, nextOrder));
            nextOrder += deck.RegularUnlocks.Count;
        }

        if (deck.PromoUnlocks.Count > 0)
        {
            files[$"DATA_ALL_PLATFORMS\\UNLOCKS\\{deckFileName}_PROMO.XML"] = XmlBytes(
                CreateUnlockXml(deck.PromoUnlocks, promoUnlockUid, deckUid, promo: true, nextOrder));
        }

        byte[] header = XmlBytes(CreateHeader(rootName));
        files["HEADER.XML"] = header;
        string? backupPath = WriteAtomically(outputPath, rootName, header, files);

        string enablerPath = Path.Combine(outputDirectory, $"Data_DLC_{options.IdBlock}_Content_Pack_Enabler.wad");
        bool enablerCreated = false;
        if (!File.Exists(enablerPath))
        {
            string enablerRoot = Path.GetFileNameWithoutExtension(enablerPath).ToUpperInvariant();
            byte[] enablerHeader = XmlBytes(CreateContentPackHeader(enablerRoot, options.IdBlock, options.SteamAppId));
            WriteAtomically(
                enablerPath,
                enablerRoot,
                enablerHeader,
                new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase) { ["HEADER.XML"] = enablerHeader });
            enablerCreated = true;
        }

        return new ModernWadExportResult(
            outputPath,
            backupPath,
            enablerPath,
            enablerCreated,
            deckUid,
            landPoolUid,
            regularUnlockUid,
            promoUnlockUid,
            deckFileName);
    }

    private static XDocument CreateDeckXml(
        DeckDocument deck,
        int deckUid,
        int contentPack,
        string fileName,
        string personalityReference)
    {
        XElement root = CreateDeckRoot(deckUid, contentPack, isLandPool: false);
        root.SetAttributeValue("personality", personalityReference);
        root.SetAttributeValue("deck_box_image", deck.DeckBoxImage);
        root.SetAttributeValue("deck_box_image_locked", deck.DeckBoxImageLocked);
        root.SetAttributeValue("name_tag", fileName);
        root.SetAttributeValue("description_tag", $"{fileName}_DESCRIPTION");
        ApplyAvailability(root, deck.Availability);
        AddDeckColours(root, deck);
        root.Add(new XElement("DECKSTATISTICS",
            new XAttribute("Size", NormalizeStatistic(deck.CreatureSize)),
            new XAttribute("Speed", NormalizeStatistic(deck.DeckSpeed)),
            new XAttribute("Flex", NormalizeStatistic(deck.Flexibility)),
            new XAttribute("Syn", NormalizeStatistic(deck.Synergy))));

        XElement? landConfig = CreateLandConfig(deck);
        if (landConfig is not null)
        {
            root.Add(landConfig);
        }

        int order = 0;
        foreach (DeckEntry entry in deck.MainDeck.OrderBy(entry => entry.CardReference, StringComparer.OrdinalIgnoreCase))
        {
            for (int copy = 0; copy < entry.Quantity; copy++)
            {
                root.Add(CardElement(entry.CardReference, order++));
            }
        }

        return new XDocument(root);
    }

    private static XElement? CreateLandConfig(DeckDocument deck)
    {
        if (deck.IgnoreCmcOver <= -1
            && deck.MinForests <= 0
            && deck.MinIslands <= 0
            && deck.MinMountains <= 0
            && deck.MinPlains <= 0
            && deck.MinSwamps <= 0
            && deck.NumberOfSpellsThatCountAsLand <= 0)
        {
            return null;
        }

        XElement element = new("LandConfig");
        if (deck.IgnoreCmcOver > -1) element.SetAttributeValue("ignoreCmcOver", deck.IgnoreCmcOver);
        if (deck.MinForests > 0) element.SetAttributeValue("minForest", deck.MinForests);
        if (deck.MinIslands > 0) element.SetAttributeValue("minIsland", deck.MinIslands);
        if (deck.MinMountains > 0) element.SetAttributeValue("minMountain", deck.MinMountains);
        if (deck.MinPlains > 0) element.SetAttributeValue("minPlains", deck.MinPlains);
        if (deck.MinSwamps > 0) element.SetAttributeValue("minSwamp", deck.MinSwamps);
        if (deck.NumberOfSpellsThatCountAsLand > 0)
        {
            element.SetAttributeValue("numSpellsThatCountAsLand", deck.NumberOfSpellsThatCountAsLand);
        }

        return element;
    }

    private static XDocument CreateLandPoolXml(
        DeckDocument deck,
        IReadOnlyList<CardRecord> catalog,
        int landPoolUid,
        int contentPack)
    {
        XElement root = CreateDeckRoot(landPoolUid, contentPack, isLandPool: true);
        IReadOnlyList<CardRecord> lands = SelectLandPool(deck, catalog);
        for (int index = 0; index < lands.Count; index++)
        {
            root.Add(CardElement(lands[index].FileName, index));
        }

        return new XDocument(root);
    }

    private static XDocument CreateUnlockXml(
        IList<DeckEntry> entries,
        int uid,
        int deckUid,
        bool promo,
        int startOrder)
    {
        XElement root = new("UNLOCKS",
            new XAttribute("uid", uid),
            new XAttribute("deck_uid", deckUid),
            new XAttribute("content_pack", 0),
            new XAttribute("game_mode", promo ? 2 : 0));

        for (int index = 0; index < entries.Count; index++)
        {
            root.Add(new XElement("CARD",
                new XAttribute("name", entries[index].CardReference),
                new XAttribute("deckOrderId", startOrder + index),
                new XAttribute("unlockOrderId", index),
                new XAttribute("quantity", 1)));
        }

        return new XDocument(root);
    }

    private static XElement CreateDeckRoot(int uid, int contentPack, bool isLandPool)
    {
        return new XElement("DECK",
            new XAttribute("uid", uid),
            new XAttribute("personality", string.Empty),
            new XAttribute("deck_box_image", string.Empty),
            new XAttribute("deck_box_image_locked", "locked"),
            new XAttribute("content_pack", contentPack),
            new XAttribute(isLandPool ? "never_available" : "always_available", "true"),
            new XAttribute("cheat_menu_filter_deck_type", isLandPool ? "Utility" : "Standard"),
            new XAttribute("tus_save_data_id", uid),
            new XAttribute("ios_id_1", "D14_DECK_UNLOCK_1"),
            new XAttribute("ios_id_2", "D14_DECK_FOIL_1"),
            new XAttribute("steam_id_1", "213850"),
            new XAttribute("steam_id_2", "213850"),
            new XAttribute("android_id_1", "d14_deck_unlock_01"),
            new XAttribute("android_id_2", "d14_deck_foil_01"),
            new XAttribute("cheat_menu_filter_datapool", "D14"),
            new XAttribute("name_tag", string.Empty),
            new XAttribute("description_tag", string.Empty));
    }

    private static void ApplyAvailability(XElement root, DeckAvailability availability)
    {
        root.SetAttributeValue("always_available", null);
        root.SetAttributeValue("never_available", null);
        switch (availability)
        {
            case DeckAvailability.AlwaysAvailable:
                root.SetAttributeValue("always_available", "true");
                break;
            case DeckAvailability.NeverAvailable:
                root.SetAttributeValue("never_available", "true");
                break;
            case DeckAvailability.Locked:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(availability), availability, null);
        }
    }

    private static string NormalizeStatistic(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "?" : value.Trim();

    private static XElement CardElement(string reference, int order) => new("CARD",
        new XAttribute("name", reference),
        new XAttribute("deckOrderId", order));

    private static void AddDeckColours(XElement root, DeckDocument deck)
    {
        DeckColourFlags colour = deck.OverrideColours
            ? deck.OverrideColour
            : DeckColourCalculator.Calculate(deck);

        foreach ((DeckColourFlags flag, string attribute) in new[]
                 {
                     (DeckColourFlags.Black, "is_black"),
                     (DeckColourFlags.Blue, "is_blue"),
                     (DeckColourFlags.Green, "is_green"),
                     (DeckColourFlags.Red, "is_red"),
                     (DeckColourFlags.White, "is_white")
                 })
        {
            root.SetAttributeValue(attribute, DeckColourCalculator.Has(colour, flag) ? "true" : null);
        }
    }

    private static IReadOnlyList<CardRecord> SelectLandPool(
        DeckDocument deck,
        IReadOnlyList<CardRecord> catalog)
    {
        DeckColourFlags colour = DeckColourCalculator.Calculate(deck);
        List<string> landNames = new();
        if (DeckColourCalculator.Has(colour, DeckColourFlags.Green)) landNames.Add("FOREST");
        if (DeckColourCalculator.Has(colour, DeckColourFlags.Blue)) landNames.Add("ISLAND");
        if (DeckColourCalculator.Has(colour, DeckColourFlags.Red)) landNames.Add("MOUNTAIN");
        if (DeckColourCalculator.Has(colour, DeckColourFlags.White)) landNames.Add("PLAINS");
        if (DeckColourCalculator.Has(colour, DeckColourFlags.Black)) landNames.Add("SWAMP");
        if (landNames.Count == 0)
        {
            landNames.AddRange(["FOREST", "ISLAND", "MOUNTAIN", "PLAINS", "SWAMP"]);
        }

        List<CardRecord> result = new();
        foreach (string land in landNames)
        {
            CardRecord[] choices = catalog
                .Where(card => card.FileName.StartsWith(land + "_", StringComparison.OrdinalIgnoreCase))
                .OrderBy(card => card.FileName, StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToArray();
            if (choices.Length == 0)
            {
                throw new InvalidOperationException($"No {land.ToLowerInvariant()} cards were found for the automatic land pool.");
            }

            result.AddRange(choices);
        }

        return result;
    }

    private static XDocument CreateHeader(string rootName) => new(
        new XDeclaration("1.0", null, null),
        new XElement("WAD_HEADER",
            new XElement("ENTRY",
                new XAttribute("platform", "ALL"),
                new XAttribute("source", $"{rootName}/DATA_ALL_PLATFORMS/"),
                new XAttribute("alias", "Content"),
                new XAttribute("order", 3))));

    private static XDocument CreateContentPackHeader(string rootName, int contentPack, int steamAppId) => new(
        new XDeclaration("1.0", null, null),
        new XElement("WAD_HEADER",
            new XElement("ENTRY",
                new XAttribute("platform", "ALL"),
                new XAttribute("source", $"{rootName}/DATA_ALL_PLATFORMS/"),
                new XAttribute("alias", "Content"),
                new XAttribute("order", 3)),
            new XElement("CONTENTPACK",
                new XAttribute("UID", contentPack),
                new XElement("PD_SECTION", new XElement("APP_ID", new XAttribute("ID", steamAppId))),
                new XElement("CONTENTFLAGS",
                    new XElement("AVATAR_CONTENT"),
                    new XElement("DECK_CONTENT"),
                    new XElement("GLOSSARY_CONTENT"),
                    new XElement("UNLOCK_CONTENT")))));

    private static byte[] CreateLegacyStringTableBytes(
        string id,
        string name,
        string description,
        AiPersonalityDefinition? personality)
    {
        // Magic 2014's text-table loader is sensitive to the lexical SpreadsheetML layout.
        // Match the original Deck Builder exactly: spreadsheet elements use the default namespace,
        // while only SpreadsheetML attributes carry the ss: prefix.
        const string spreadsheetNs = "urn:schemas-microsoft-com:office:spreadsheet";
        const string officeNs = "urn:schemas-microsoft-com:office:office";

        StringBuilder xml = new();
        xml.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        xml.Append("<?mso-application progid=\"Excel.Sheet\"?>");
        xml.Append("<Workbook xmlns=\"").Append(spreadsheetNs)
            .Append("\" xmlns:ss=\"").Append(spreadsheetNs).Append("\">");
        xml.Append("<DocumentProperties xmlns=\"").Append(officeNs).Append("\">");
        xml.Append("<Author>DotP 2014 Deck Builder Modern</Author>");
        xml.Append("<Created>").Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")).Append("</Created>");
        xml.Append("<Company></Company></DocumentProperties>");
        xml.Append("<Worksheet ss:Name=\"Sheet4\"><Table>");

        AppendStringRow(xml,
            ["Ident", "Comment", "Master Text", "French", "Spanish", "German", "Italian", null,
             "Japanese", "Korean", "Russian", "Portuguese (Brazil)", null, null,
             "Chinese Simplified", "Chinese Traditional"]);
        AppendStringRow(xml,
            [id, null, name, name, name, name, name, null, name, name, name, name, null, null, name, name]);
        AppendStringRow(xml,
            [$"{id}_DESCRIPTION", null, description, description, description, description, description, null,
             description, description, description, description, null, null, description, description]);

        if (personality is not null && !string.IsNullOrWhiteSpace(personality.NameTag))
        {
            string personalityName = string.IsNullOrWhiteSpace(personality.DisplayName)
                ? "New Personality"
                : personality.DisplayName;
            AppendStringRow(xml,
                [personality.NameTag, null, personalityName, personalityName, personalityName, personalityName,
                 personalityName, null, personalityName, personalityName, personalityName, personalityName,
                 null, null, personalityName, personalityName]);
        }

        xml.Append("</Table></Worksheet></Workbook>");
        return Encoding.UTF8.GetBytes(xml.ToString());
    }

    private static void AppendStringRow(StringBuilder xml, IReadOnlyList<string?> values)
    {
        xml.Append("<Row>");
        int emittedCellPosition = 0;
        for (int index = 0; index < values.Count; index++)
        {
            string? value = values[index];
            if (value is null)
            {
                continue;
            }

            int targetPosition = index + 1;
            xml.Append("<Cell");
            if (emittedCellPosition + 1 != targetPosition)
            {
                xml.Append(" ss:Index=\"").Append(targetPosition).Append('"');
            }

            xml.Append("><Data ss:Type=\"String\">")
                .Append(SecurityElement.Escape(value) ?? string.Empty)
                .Append("</Data></Cell>");
            emittedCellPosition = targetPosition;
        }

        xml.Append("</Row>");
    }

    private static string? WriteAtomically(
        string outputPath,
        string rootName,
        byte[] header,
        IReadOnlyDictionary<string, byte[]> files)
    {
        string tempPath = Path.Combine(
            Path.GetDirectoryName(outputPath)!,
            $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            WriteWad(tempPath, rootName, header, files);
            ValidateWad(tempPath, files.Count);
            string? backupPath = null;
            if (File.Exists(outputPath))
            {
                backupPath = $"{outputPath}.backup-{DateTime.Now:yyyyMMdd-HHmmss}";
                File.Copy(outputPath, backupPath, overwrite: false);
            }

            File.Move(tempPath, outputPath, overwrite: true);
            return backupPath;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static void WriteWad(
        string path,
        string rootName,
        byte[] header,
        IReadOnlyDictionary<string, byte[]> files)
    {
        WadFile archive = new()
        {
            Version = WadVersion,
            Flags = Wad.ArchiveFlags.Unknown6Observed
                | Wad.ArchiveFlags.HasDataTypes
                | Wad.ArchiveFlags.HasCompressedFiles,
            HeaderXml = header
        };

        List<OutputFileEntry> entries = new();
        foreach ((string relativePath, byte[] data) in files.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            string fullPath = $"{rootName}\\{relativePath}";
            Wad.DirectoryEntry directory = GetOrCreateDirectory(archive, Path.GetDirectoryName(fullPath)!);
            OutputFileEntry entry = new(directory, data)
            {
                Name = Path.GetFileName(fullPath),
                Size = 0,
                Unknown0C = 0,
                OffsetIndex = archive.DataOffsets.Count,
                OffsetCount = 1
            };
            archive.DataOffsets.Add(0);
            directory.Files.Add(entry);
            entries.Add(entry);
        }

        using FileStream output = File.Create(path);
        archive.Serialize(output);
        foreach (OutputFileEntry entry in entries)
        {
            archive.DataOffsets[entry.OffsetIndex] = checked((uint)output.Position);
            using MemoryStream compressed = new();
            DeflaterOutputStream deflater = new(compressed, new Deflater(Deflater.BEST_COMPRESSION));
            deflater.Write(entry.Data, 0, entry.Data.Length);
            deflater.Finish();
            if (compressed.Length < entry.Data.Length)
            {
                entry.Size = checked((uint)(4 + compressed.Length));
                output.WriteValueU32(checked((uint)entry.Data.Length));
                compressed.Position = 0;
                compressed.CopyTo(output);
            }
            else
            {
                entry.Size = checked((uint)(4 + entry.Data.Length));
                output.WriteValueU32(uint.MaxValue);
                output.Write(entry.Data, 0, entry.Data.Length);
            }
        }

        output.Position = 0;
        archive.Serialize(output);
    }

    private static void ValidateWad(string path, int expectedFiles)
    {
        using FileStream input = File.OpenRead(path);
        if (WadFile.IsBadHeader(input, out _, out _, out string reason))
        {
            throw new InvalidDataException($"The generated WAD has an invalid header: {reason}");
        }

        input.Position = 0;
        WadFile archive = new();
        archive.Deserialize(input);
        if (archive.AllFiles.Count() != expectedFiles)
        {
            throw new InvalidDataException(
                $"The generated WAD contains {archive.AllFiles.Count()} files; {expectedFiles} were expected.");
        }
    }

    private static Wad.DirectoryEntry GetOrCreateDirectory(WadFile archive, string path)
    {
        string[] parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Wad.DirectoryEntry? current = archive.Directories.FirstOrDefault(directory => directory.Name == parts[0]);
        if (current is null)
        {
            current = new Wad.DirectoryEntry(null) { Name = parts[0] };
            archive.Directories.Add(current);
        }

        foreach (string part in parts.Skip(1))
        {
            Wad.DirectoryEntry? child = current.Directories.FirstOrDefault(directory => directory.Name == part);
            if (child is null)
            {
                child = new Wad.DirectoryEntry(current) { Name = part };
                current.Directories.Add(child);
            }

            current = child;
        }

        return current;
    }

    private static HashSet<int> FindUsedSlots(string gameDirectory)
    {
        HashSet<int> used = new();
        if (!Directory.Exists(gameDirectory))
        {
            return used;
        }

        foreach (string path in Directory.EnumerateFiles(gameDirectory, "*.wad", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (!name.StartsWith("Data_Decks_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string remainder = name["Data_Decks_".Length..];
            string uidText = remainder.Split('_')[0];
            if (int.TryParse(uidText, out int uid))
            {
                int slot = SlotFromDeckUid(uid);
                if (slot >= 0)
                {
                    used.Add(slot);
                }
            }
        }

        return used;
    }

    private static int SlotFromDeckUid(int uid)
    {
        string value = uid.ToString();
        return value.Length == 6
               && value.StartsWith("1000", StringComparison.Ordinal)
               && int.TryParse(value[4..], out int slot)
            ? slot
            : -1;
    }

    private static int PrefixId(int prefix, int slot) => int.Parse($"{prefix}{slot:00}");

    private static string Codify(string value)
    {
        StringBuilder result = new();
        foreach (char character in value.Trim().ToUpperInvariant())
        {
            if (character is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                result.Append(character);
            }
            else if (result.Length > 0 && result[^1] != '_')
            {
                result.Append('_');
            }
        }

        string code = result.ToString().Trim('_');
        return code.Length == 0 ? "CUSTOM_DECK" : code;
    }

    private static byte[] XmlBytes(XDocument document) => Encoding.UTF8.GetBytes(
        document.Declaration is null
            ? document.ToString(SaveOptions.DisableFormatting)
            : $"{document.Declaration}{document.ToString(SaveOptions.DisableFormatting)}");

    private sealed class OutputFileEntry : Wad.FileEntry
    {
        public OutputFileEntry(Wad.DirectoryEntry directory, byte[] data)
            : base(directory)
        {
            Data = data;
        }

        public byte[] Data { get; }
    }
}
