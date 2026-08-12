using System.Text;
using System.Text.Json;
using DeckBuilder.Core.Models;
using Gibbed.Duels.FileFormats;
using Gibbed.IO;
using ICSharpCode.SharpZipLib.Zip.Compression.Streams;
using Wad = Gibbed.Duels.FileFormats.Wad;

namespace DeckBuilder.GameData;

internal static class WadCardCatalogReader
{
    private const string CardDirectory = "DATA_ALL_PLATFORMS\\CARDS";

    public static IReadOnlyList<CardRecord> Read(string wadPath, CancellationToken cancellationToken)
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
        Wad.DirectoryEntry? directory = FindDirectory(archive.Directories, CardDirectory);
        if (directory is null)
        {
            return Array.Empty<CardRecord>();
        }

        IReadOnlyDictionary<string, WorkspaceContentProvenanceEntry> provenance = LoadProvenance(wadPath);
        List<CardRecord> cards = new(directory.Files.Count);
        foreach (Wad.FileEntry file in directory.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Path.GetExtension(file.Name).Equals(".xml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            byte[] data = ReadFile(input, archive, file, compressed);
            string relativePath = $"CARDS\\{file.Name}";
            string source = DescribeSource(wadPath, relativePath, provenance);
            CardRecord? card = CardXmlParser.Parse(DecodeText(data), source);
            if (card is not null)
            {
                cards.Add(card);
            }
        }

        return cards;
    }

    private static IReadOnlyDictionary<string, WorkspaceContentProvenanceEntry> LoadProvenance(string wadPath)
    {
        string path = wadPath + ".sources.json";
        if (!File.Exists(path))
        {
            return new Dictionary<string, WorkspaceContentProvenanceEntry>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            WorkspaceContentProvenanceManifest? manifest = JsonSerializer.Deserialize<WorkspaceContentProvenanceManifest>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (manifest is null)
            {
                return new Dictionary<string, WorkspaceContentProvenanceEntry>(StringComparer.OrdinalIgnoreCase);
            }

            return manifest.Entries.ToDictionary(
                entry => entry.RelativePath,
                StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // Provenance is supplemental. A malformed/missing sidecar must never make a valid WAD unreadable.
            return new Dictionary<string, WorkspaceContentProvenanceEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string DescribeSource(
        string wadPath,
        string relativePath,
        IReadOnlyDictionary<string, WorkspaceContentProvenanceEntry> provenance)
    {
        if (!provenance.TryGetValue(relativePath, out WorkspaceContentProvenanceEntry? entry))
        {
            return Path.GetFileNameWithoutExtension(wadPath) ?? wadPath;
        }

        int sources = entry.Sources.Count;
        int variants = entry.Sources
            .Select(source => source.Sha256)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        string suffix = sources <= 1
            ? string.Empty
            : variants <= 1
                ? $" · {sources} identical sources"
                : $" · selected from {sources} sources / {variants} variants";
        return $"{entry.SelectedPackage} / {entry.SelectedWad}{suffix}";
    }

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
}
