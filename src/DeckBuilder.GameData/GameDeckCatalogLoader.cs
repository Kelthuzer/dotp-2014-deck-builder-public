using System.Collections.Concurrent;
using System.Text;
using System.Xml.Linq;
using DeckBuilder.Core.Models;
using DeckBuilder.Core.Services;
using Gibbed.Duels.FileFormats;
using Gibbed.IO;
using ICSharpCode.SharpZipLib.Zip.Compression.Streams;
using Wad = Gibbed.Duels.FileFormats.Wad;

namespace DeckBuilder.GameData;

public sealed class GameDeckCatalogLoader
{
    private const string DeckDirectory = "DATA_ALL_PLATFORMS\\DECKS";
    private const string UnlockDirectory = "DATA_ALL_PLATFORMS\\UNLOCKS";

    public async Task<GameDeckCatalogLoadResult> LoadAsync(
        string gameDirectory,
        IReadOnlyList<CardRecord> catalog,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        ArgumentNullException.ThrowIfNull(catalog);
        if (!Directory.Exists(gameDirectory))
        {
            throw new DirectoryNotFoundException(gameDirectory);
        }

        return await Task.Run(() => Load(gameDirectory, catalog, cancellationToken), cancellationToken);
    }

    private static GameDeckCatalogLoadResult Load(
        string gameDirectory,
        IReadOnlyList<CardRecord> catalog,
        CancellationToken cancellationToken)
    {
        List<InstalledDeckRecord> decks = new();
        List<PendingUnlock> unlocks = new();
        ConcurrentBag<string> warnings = new();

        foreach (string wadPath in Directory.EnumerateFiles(gameDirectory, "*.wad", SearchOption.TopDirectoryOnly)
                     .Where(IsGameWad)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                ReadWad(wadPath, catalog, decks, unlocks, cancellationToken, warnings);
            }
            catch (Exception exception)
            {
                warnings.Add($"{FileName(wadPath)} decks: {exception.Message}");
            }
        }

        foreach (string directory in FindUnpackedWads(gameDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                ReadDirectory(directory, catalog, decks, unlocks, cancellationToken, warnings);
            }
            catch (Exception exception)
            {
                warnings.Add($"{FileName(directory)} decks: {exception.Message}");
            }
        }

        AttachUnlocks(decks, unlocks);
        InstalledDeckRecord[] result = decks
            .Where(deck => !deck.FileName.Contains("_LAND_POOL", StringComparison.OrdinalIgnoreCase))
            .OrderBy(deck => deck.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(deck => deck.Uid)
            .ThenBy(deck => deck.Source, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new GameDeckCatalogLoadResult(
            result,
            warnings.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static void ReadWad(
        string wadPath,
        IReadOnlyList<CardRecord> catalog,
        ICollection<InstalledDeckRecord> decks,
        ICollection<PendingUnlock> unlocks,
        CancellationToken cancellationToken,
        ConcurrentBag<string> warnings)
    {
        using FileStream input = File.OpenRead(wadPath);
        if (WadFile.IsBadHeader(input, out _, out _, out string reason))
        {
            throw new InvalidDataException(reason);
        }

        input.Position = 0;
        WadFile archive = new();
        archive.Deserialize(input);
        bool compressed = (archive.Flags & Wad.ArchiveFlags.HasCompressedFiles) == Wad.ArchiveFlags.HasCompressedFiles;
        string source = Path.GetFileNameWithoutExtension(wadPath) ?? wadPath;

        Wad.DirectoryEntry? deckDirectory = FindDirectory(archive.Directories, DeckDirectory);
        if (deckDirectory is not null)
        {
            foreach (Wad.FileEntry file in deckDirectory.Files.Where(IsXml))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    string fileName = (Path.GetFileNameWithoutExtension(file.Name) ?? file.Name).ToUpperInvariant();
                    DeckDocument deck = DotpDeckXmlSerializer.Parse(
                        DecodeText(ReadFile(input, archive, file, compressed)),
                        catalog);
                    decks.Add(new InstalledDeckRecord(fileName, source, deck));
                }
                catch (Exception exception)
                {
                    warnings.Add($"{source}\\{file.Name}: {exception.Message}");
                }
            }
        }

        Wad.DirectoryEntry? unlockDirectory = FindDirectory(archive.Directories, UnlockDirectory);
        if (unlockDirectory is not null)
        {
            foreach (Wad.FileEntry file in unlockDirectory.Files.Where(IsXml))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    PendingUnlock? unlock = ParseUnlock(
                        DecodeText(ReadFile(input, archive, file, compressed)),
                        source,
                        catalog);
                    if (unlock is not null)
                    {
                        unlocks.Add(unlock);
                    }
                }
                catch (Exception exception)
                {
                    warnings.Add($"{source}\\{file.Name}: {exception.Message}");
                }
            }
        }
    }

    private static void ReadDirectory(
        string directory,
        IReadOnlyList<CardRecord> catalog,
        ICollection<InstalledDeckRecord> decks,
        ICollection<PendingUnlock> unlocks,
        CancellationToken cancellationToken,
        ConcurrentBag<string> warnings)
    {
        string source = FileName(directory);
        string deckDirectory = Path.Combine(directory, "DATA_ALL_PLATFORMS", "DECKS");
        if (Directory.Exists(deckDirectory))
        {
            foreach (string path in Directory.EnumerateFiles(deckDirectory, "*.xml", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    string fileName = (Path.GetFileNameWithoutExtension(path) ?? path).ToUpperInvariant();
                    decks.Add(new InstalledDeckRecord(
                        fileName,
                        source,
                        DotpDeckXmlSerializer.Load(path, catalog)));
                }
                catch (Exception exception)
                {
                    warnings.Add($"{path}: {exception.Message}");
                }
            }
        }

        string unlockDirectory = Path.Combine(directory, "DATA_ALL_PLATFORMS", "UNLOCKS");
        if (Directory.Exists(unlockDirectory))
        {
            foreach (string path in Directory.EnumerateFiles(unlockDirectory, "*.xml", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    PendingUnlock? unlock = ParseUnlock(File.ReadAllText(path), source, catalog);
                    if (unlock is not null)
                    {
                        unlocks.Add(unlock);
                    }
                }
                catch (Exception exception)
                {
                    warnings.Add($"{path}: {exception.Message}");
                }
            }
        }
    }

    private static PendingUnlock? ParseUnlock(
        string xml,
        string source,
        IReadOnlyList<CardRecord> catalog)
    {
        XDocument document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        XElement? root = document.Root?.Name.LocalName.Equals("UNLOCKS", StringComparison.OrdinalIgnoreCase) == true
            ? document.Root
            : document.Descendants().FirstOrDefault(element =>
                element.Name.LocalName.Equals("UNLOCKS", StringComparison.OrdinalIgnoreCase));
        if (root is null || !int.TryParse(Attribute(root, "deck_uid"), out int deckUid))
        {
            return null;
        }

        bool promo = Attribute(root, "game_mode") == "2";
        DeckDocument parsed = DotpDeckXmlSerializer.Parse(xml, catalog);
        IReadOnlyList<DeckEntry> entries = (promo ? parsed.PromoUnlocks : parsed.RegularUnlocks).ToArray();
        return new PendingUnlock(deckUid, promo, source, entries);
    }

    private static void AttachUnlocks(
        IReadOnlyList<InstalledDeckRecord> decks,
        IEnumerable<PendingUnlock> unlocks)
    {
        foreach (PendingUnlock unlock in unlocks)
        {
            InstalledDeckRecord? deck = decks.FirstOrDefault(candidate =>
                candidate.Uid == unlock.DeckUid
                && candidate.Source.Equals(unlock.Source, StringComparison.OrdinalIgnoreCase))
                ?? decks.FirstOrDefault(candidate => candidate.Uid == unlock.DeckUid);
            if (deck is null)
            {
                continue;
            }

            IList<DeckEntry> target = unlock.Promo ? deck.Deck.PromoUnlocks : deck.Deck.RegularUnlocks;
            foreach (DeckEntry entry in unlock.Entries)
            {
                target.Add(new DeckEntry(entry.Card, 1, entry.Bias, entry.Promo, entry.OrderId));
            }
        }
    }

    private static IEnumerable<string> FindUnpackedWads(string gameDirectory)
    {
        if (HasDeckData(gameDirectory))
        {
            yield return gameDirectory;
        }

        foreach (string directory in Directory.EnumerateDirectories(gameDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            if (IsGameWad(directory) && File.Exists(Path.Combine(directory, "header.xml")) && HasDeckData(directory))
            {
                yield return directory;
            }
        }
    }

    private static bool HasDeckData(string directory) =>
        Directory.Exists(Path.Combine(directory, "DATA_ALL_PLATFORMS", "DECKS"));

    private static bool IsGameWad(string path)
    {
        string name = FileName(path);
        return name.StartsWith("data_core", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("data_dlc_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("data_decks_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsXml(Wad.FileEntry file) =>
        Path.GetExtension(file.Name).Equals(".xml", StringComparison.OrdinalIgnoreCase);

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

    private static string FileName(string path) => Path.GetFileName(path) ?? path;

    private sealed record PendingUnlock(
        int DeckUid,
        bool Promo,
        string Source,
        IReadOnlyList<DeckEntry> Entries);
}
