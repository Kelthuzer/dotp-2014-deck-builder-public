using System.Collections.Concurrent;
using System.Text;
using DeckBuilder.Core.Models;
using Gibbed.Duels.FileFormats;
using Gibbed.IO;
using ICSharpCode.SharpZipLib.Zip.Compression.Streams;
using Wad = Gibbed.Duels.FileFormats.Wad;

namespace DeckBuilder.GameData;

public sealed record MissingCardResolutionResult(
    IReadOnlyList<CardRecord> Cards,
    IReadOnlyList<string> UnresolvedReferences,
    IReadOnlyList<string> Warnings);

public sealed class MissingCardReferenceResolver
{
    public async Task<MissingCardResolutionResult> ResolveAsync(
        string gameDirectory,
        IEnumerable<string> references,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        ArgumentNullException.ThrowIfNull(references);
        if (!Directory.Exists(gameDirectory))
        {
            throw new DirectoryNotFoundException(gameDirectory);
        }

        string[] requested = references
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (requested.Length == 0)
        {
            return new MissingCardResolutionResult(
                Array.Empty<CardRecord>(),
                Array.Empty<string>(),
                Array.Empty<string>());
        }

        return await Task.Run(() => Resolve(gameDirectory, requested, cancellationToken), cancellationToken);
    }

    private static MissingCardResolutionResult Resolve(
        string gameDirectory,
        IReadOnlyList<string> requested,
        CancellationToken cancellationToken)
    {
        HashSet<string> wanted = requested.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, CardRecord> found = new(StringComparer.OrdinalIgnoreCase);
        ConcurrentBag<string> warnings = new();

        foreach (string wadPath in Directory.EnumerateFiles(gameDirectory, "*.wad", SearchOption.TopDirectoryOnly)
                     .Where(GameWadSelection.IsSupported)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                ResolveFromWad(wadPath, wanted, found, cancellationToken, warnings);
            }
            catch (Exception exception)
            {
                warnings.Add($"{Path.GetFileName(wadPath)} referenced-card scan: {exception.Message}");
            }

            if (found.Count == wanted.Count)
            {
                break;
            }
        }

        if (found.Count < wanted.Count)
        {
            foreach (string directory in FindUnpackedWads(gameDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    ResolveFromDirectory(directory, wanted, found, cancellationToken, warnings);
                }
                catch (Exception exception)
                {
                    warnings.Add($"{Path.GetFileName(directory)} referenced-card scan: {exception.Message}");
                }

                if (found.Count == wanted.Count)
                {
                    break;
                }
            }
        }

        string[] unresolved = requested
            .Where(reference => !found.ContainsKey(reference))
            .ToArray();
        return new MissingCardResolutionResult(
            found.Values.OrderBy(card => card.FileName, StringComparer.OrdinalIgnoreCase).ToArray(),
            unresolved,
            warnings.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static void ResolveFromWad(
        string wadPath,
        IReadOnlySet<string> wanted,
        IDictionary<string, CardRecord> found,
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
        Wad.FileEntry[] xmlFiles = archive.AllFiles
            .Where(file => Path.GetExtension(file.Name).Equals(".xml", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        // Fast path: most card definitions have a physical XML filename matching their
        // internal CARD_V2/FILENAME value, so avoid opening unrelated files first.
        foreach (Wad.FileEntry file in xmlFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string reference = Path.GetFileNameWithoutExtension(file.Name) ?? string.Empty;
            if (!wanted.Contains(reference) || found.ContainsKey(reference))
            {
                continue;
            }

            TryResolveWadXml(input, archive, file, compressed, source, wanted, found, warnings, requireTextHint: false);
        }

        if (found.Count == wanted.Count)
        {
            return;
        }

        // Legacy DotP data distinguishes the physical XML filename from the FILENAME tag
        // inside CARD_V2.  The old CardInfo class calls these ActualFilename and Filename
        // and even warns when they differ.  Official/legacy decks can therefore reference
        // a valid internal FILENAME that cannot be discovered by looking for
        // <reference>.xml.  For still-unresolved references scan XML contents and accept
        // only a CARD_V2 whose parsed FILENAME exactly matches a requested reference.
        foreach (Wad.FileEntry file in xmlFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (found.Count == wanted.Count)
            {
                break;
            }

            string physicalReference = Path.GetFileNameWithoutExtension(file.Name) ?? string.Empty;
            if (wanted.Contains(physicalReference) && found.ContainsKey(physicalReference))
            {
                continue;
            }

            TryResolveWadXml(input, archive, file, compressed, source, wanted, found, warnings, requireTextHint: true);
        }
    }

    private static void TryResolveWadXml(
        FileStream input,
        WadFile archive,
        Wad.FileEntry file,
        bool compressed,
        string source,
        IReadOnlySet<string> wanted,
        IDictionary<string, CardRecord> found,
        ConcurrentBag<string> warnings,
        bool requireTextHint)
    {
        try
        {
            string xml = DecodeText(ReadFile(input, archive, file, compressed));
            if (requireTextHint && !CouldContainUnresolvedCard(xml, wanted, found))
            {
                return;
            }

            CardRecord? card = CardXmlParser.Parse(xml, source);
            if (card is not null && wanted.Contains(card.FileName) && !found.ContainsKey(card.FileName))
            {
                found[card.FileName] = card;
            }
        }
        catch (Exception exception)
        {
            warnings.Add($"{source}\\{file.Name}: {exception.Message}");
        }
    }

    private static void ResolveFromDirectory(
        string directory,
        IReadOnlySet<string> wanted,
        IDictionary<string, CardRecord> found,
        CancellationToken cancellationToken,
        ConcurrentBag<string> warnings)
    {
        string source = Path.GetFileName(directory) ?? directory;
        string[] xmlFiles = Directory.EnumerateFiles(directory, "*.xml", SearchOption.AllDirectories).ToArray();

        foreach (string path in xmlFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string reference = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
            if (!wanted.Contains(reference) || found.ContainsKey(reference))
            {
                continue;
            }

            TryResolveDirectoryXml(path, source, wanted, found, warnings, requireTextHint: false);
        }

        if (found.Count == wanted.Count)
        {
            return;
        }

        foreach (string path in xmlFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (found.Count == wanted.Count)
            {
                break;
            }

            string physicalReference = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
            if (wanted.Contains(physicalReference) && found.ContainsKey(physicalReference))
            {
                continue;
            }

            TryResolveDirectoryXml(path, source, wanted, found, warnings, requireTextHint: true);
        }
    }

    private static void TryResolveDirectoryXml(
        string path,
        string source,
        IReadOnlySet<string> wanted,
        IDictionary<string, CardRecord> found,
        ConcurrentBag<string> warnings,
        bool requireTextHint)
    {
        try
        {
            string xml = File.ReadAllText(path);
            if (requireTextHint && !CouldContainUnresolvedCard(xml, wanted, found))
            {
                return;
            }

            CardRecord? card = CardXmlParser.Parse(xml, source);
            if (card is not null && wanted.Contains(card.FileName) && !found.ContainsKey(card.FileName))
            {
                found[card.FileName] = card;
            }
        }
        catch (Exception exception)
        {
            warnings.Add($"{path}: {exception.Message}");
        }
    }

    private static bool CouldContainUnresolvedCard(
        string xml,
        IReadOnlySet<string> wanted,
        IDictionary<string, CardRecord> found)
    {
        if (xml.IndexOf("CARD_V2", StringComparison.OrdinalIgnoreCase) < 0
            || xml.IndexOf("FILENAME", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        foreach (string reference in wanted)
        {
            if (!found.ContainsKey(reference)
                && xml.IndexOf(reference, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> FindUnpackedWads(string gameDirectory)
    {
        if (Directory.Exists(Path.Combine(gameDirectory, "DATA_ALL_PLATFORMS")))
        {
            yield return gameDirectory;
        }

        foreach (string directory in Directory.EnumerateDirectories(gameDirectory, "*", SearchOption.TopDirectoryOnly)
                     .Where(GameWadSelection.IsSupported)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(Path.Combine(directory, "DATA_ALL_PLATFORMS")))
            {
                yield return directory;
            }
        }
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
}
