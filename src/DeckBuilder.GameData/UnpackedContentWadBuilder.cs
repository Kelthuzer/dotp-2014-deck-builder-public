using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Gibbed.Duels.FileFormats;
using Gibbed.IO;
using ICSharpCode.SharpZipLib.Zip.Compression;
using ICSharpCode.SharpZipLib.Zip.Compression.Streams;
using Wad = Gibbed.Duels.FileFormats.Wad;

namespace DeckBuilder.GameData;

public enum UnpackedContentKind
{
    Cards,
    Decks
}

public sealed record UnpackedContentBuildOptions(
    string SourceDirectory,
    string OutputPath,
    UnpackedContentKind Kind,
    int Order = 50,
    bool ReplaceExisting = true);

public sealed record UnpackedContentBuildResult(
    string OutputPath,
    UnpackedContentKind Kind,
    int FileCount,
    long PayloadBytes,
    int OverriddenFiles,
    bool UsedVersionManifest);

/// <summary>
/// Builds a standalone WAD from unpacked Magic 2014 content. When the source is an
/// extracted version package, WAD_HEADER order and WAD-name ordering are applied before
/// the selected card/deck resources are merged. A plain unpacked CARDS/DECKS tree is also
/// accepted and does not require dotp-version.json.
/// </summary>
public sealed class UnpackedContentWadBuilder
{
    private const ushort WadVersion = 0x202;
    private static readonly Wad.ArchiveFlags WadFlags = Wad.ArchiveFlags.Unknown6Observed
        | Wad.ArchiveFlags.HasDataTypes
        | Wad.ArchiveFlags.HasCompressedFiles;

    public Task<UnpackedContentBuildResult> BuildAsync(
        UnpackedContentBuildOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Task.Run(() => Build(options, cancellationToken), cancellationToken);
    }

    private static UnpackedContentBuildResult Build(
        UnpackedContentBuildOptions options,
        CancellationToken cancellationToken)
    {
        string source = Path.GetFullPath(options.SourceDirectory);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException(source);
        }

        string outputPath = Path.GetFullPath(options.OutputPath);
        string? outputDirectory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new DirectoryNotFoundException("The WAD output directory is missing.");
        }

        Directory.CreateDirectory(outputDirectory);
        if (File.Exists(outputPath) && !options.ReplaceExisting)
        {
            throw new IOException($"Output WAD already exists: {outputPath}");
        }

        (Dictionary<string, SourcePayload> content, int overrides, bool usedManifest) =
            CollectContent(source, options.Kind, cancellationToken);
        if (content.Count == 0)
        {
            throw new InvalidDataException(
                $"No {options.Kind.ToString().ToLowerInvariant()} content was found under {source}.");
        }

        string rootName = Path.GetFileNameWithoutExtension(outputPath).ToUpperInvariant();
        byte[] header = CreateHeader(rootName, options.Order);
        List<BuildPayload> payloads = content
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new BuildPayload(
                $"{rootName}\\DATA_ALL_PLATFORMS\\{pair.Key}",
                File.ReadAllBytes(pair.Value.Path),
                pair.Value.Unknown0C))
            .ToList();
        payloads.Add(new BuildPayload($"{rootName}\\HEADER.XML", header, 0));

        string temporary = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            WriteWad(temporary, header, payloads);
            ValidateWad(temporary, payloads);
            File.Move(temporary, outputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        return new UnpackedContentBuildResult(
            outputPath,
            options.Kind,
            content.Count,
            content.Values.Sum(value => new FileInfo(value.Path).Length),
            overrides,
            usedManifest);
    }

    private static (Dictionary<string, SourcePayload> Content, int Overrides, bool UsedManifest) CollectContent(
        string source,
        UnpackedContentKind kind,
        CancellationToken cancellationToken)
    {
        string manifestPath = Path.Combine(source, GameVersionPackageService.ManifestFileName);
        if (File.Exists(manifestPath))
        {
            return CollectFromPackage(source, kind, cancellationToken);
        }

        Dictionary<string, SourcePayload> content = new(StringComparer.OrdinalIgnoreCase);
        int overrides = 0;
        foreach (string path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? relative = GetContentRelativePath(source, path, kind);
            if (relative is null)
            {
                continue;
            }

            SourcePayload candidate = new(path, 0, string.Empty, 0);
            if (content.TryGetValue(relative, out SourcePayload? previous))
            {
                if (FilesEqual(previous.Path, path))
                {
                    continue;
                }

                throw new InvalidDataException(
                    $"Two different unpacked files map to the same WAD path '{relative}'.\n" +
                    $"First: {previous.Path}\nSecond: {path}\n" +
                    "Select a narrower source folder, or use an extracted version package so WAD load priority can resolve the override.");
            }

            content.Add(relative, candidate);
        }

        return (content, overrides, false);
    }

    private static (Dictionary<string, SourcePayload> Content, int Overrides, bool UsedManifest) CollectFromPackage(
        string packageDirectory,
        UnpackedContentKind kind,
        CancellationToken cancellationToken)
    {
        GameVersionPackageService packageService = new();
        DotpVersionPackageManifest manifest = packageService.ReadManifest(packageDirectory);
        Dictionary<string, SourcePayload> content = new(StringComparer.OrdinalIgnoreCase);
        int overrides = 0;

        // Game priority: greater WAD_HEADER order wins; at equal order the WAD that sorts
        // later by name loads later. Iterate low -> high and overwrite the effective entry.
        foreach (DotpWadPackageManifest wad in manifest.Wads
                     .OrderBy(wad => wad.PrimaryOrder)
                     .ThenBy(wad => wad.Name, StringComparer.OrdinalIgnoreCase))
        {
            string wadDirectory = Path.Combine(packageDirectory, "wads", SafeDirectoryName(wad.Name));
            foreach (DotpWadFileManifest file in wad.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? relative = ContentRelativeFromArchivePath(file.ArchivePath, kind);
                if (relative is null)
                {
                    continue;
                }

                string storagePath = Path.Combine(
                    wadDirectory,
                    file.StoragePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(storagePath))
                {
                    throw new FileNotFoundException(
                        $"Extracted payload listed by the package manifest is missing: {file.StoragePath}",
                        storagePath);
                }

                if (content.ContainsKey(relative))
                {
                    overrides++;
                }

                content[relative] = new SourcePayload(storagePath, file.Unknown0C, wad.Name, wad.PrimaryOrder);
            }
        }

        return (content, overrides, true);
    }

    private static string? GetContentRelativePath(string sourceRoot, string filePath, UnpackedContentKind kind)
    {
        string relative = Path.GetRelativePath(sourceRoot, filePath).Replace('/', '\\');
        string marker = "DATA_ALL_PLATFORMS\\";
        int markerIndex = relative.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
        {
            string after = relative[(markerIndex + marker.Length)..];
            return MatchesKind(after, kind) ? after : null;
        }

        string[] parts = relative.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < parts.Length; index++)
        {
            string candidate = string.Join('\\', parts.Skip(index));
            if (MatchesKind(candidate, kind))
            {
                return candidate;
            }
        }

        string sourceName = Path.GetFileName(sourceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (kind == UnpackedContentKind.Cards && sourceName.Equals("CARDS", StringComparison.OrdinalIgnoreCase))
        {
            return $"CARDS\\{relative}";
        }

        if (kind == UnpackedContentKind.Decks && sourceName.Equals("DECKS", StringComparison.OrdinalIgnoreCase))
        {
            return $"DECKS\\{relative}";
        }

        return null;
    }

    private static string? ContentRelativeFromArchivePath(string archivePath, UnpackedContentKind kind)
    {
        string normalized = archivePath.Replace('/', '\\');
        const string marker = "\\DATA_ALL_PLATFORMS\\";
        int index = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        string relative = normalized[(index + marker.Length)..];
        return MatchesKind(relative, kind) ? relative : null;
    }

    private static bool MatchesKind(string relativePath, UnpackedContentKind kind)
    {
        string path = relativePath.Replace('/', '\\');
        if (kind == UnpackedContentKind.Cards)
        {
            // Card support WADs may also carry the selected deck-box texture. This keeps the
            // exported deck self-contained on a clean install while still excluding unrelated
            // deck XML/unlocks/text resources.
            return StartsWithDirectory(path, "CARDS")
                || StartsWithDirectory(path, "ART_ASSETS\\ILLUSTRATIONS")
                || StartsWithDirectory(path, "ART_ASSETS\\TEXTURES\\CARDS")
                || StartsWithDirectory(path, "ART_ASSETS\\TEXTURES\\DECKS");
        }

        return StartsWithDirectory(path, "DECKS")
            || StartsWithDirectory(path, "UNLOCKS")
            || StartsWithDirectory(path, "AI_PERSONALITIES")
            || StartsWithDirectory(path, "TEXT_PERMANENT")
            || StartsWithDirectory(path, "ART_ASSETS\\TEXTURES\\DECKS");
    }

    private static bool StartsWithDirectory(string path, string directory) =>
        path.Equals(directory, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(directory + "\\", StringComparison.OrdinalIgnoreCase);

    private static bool FilesEqual(string first, string second)
    {
        FileInfo firstInfo = new(first);
        FileInfo secondInfo = new(second);
        if (firstInfo.Length != secondInfo.Length)
        {
            return false;
        }

        using FileStream firstStream = File.OpenRead(first);
        using FileStream secondStream = File.OpenRead(second);
        return SHA256.HashData(firstStream).AsSpan().SequenceEqual(SHA256.HashData(secondStream));
    }

    private static byte[] CreateHeader(string rootName, int order)
    {
        XDocument document = new(
            new XDeclaration("1.0", null, null),
            new XElement("WAD_HEADER",
                new XElement("ENTRY",
                    new XAttribute("platform", "ALL"),
                    new XAttribute("source", $"{rootName}/DATA_ALL_PLATFORMS/"),
                    new XAttribute("alias", "Content"),
                    new XAttribute("order", order))));
        return Encoding.UTF8.GetBytes(document.Declaration + Environment.NewLine + document);
    }

    private static void WriteWad(string path, byte[] header, IReadOnlyList<BuildPayload> payloads)
    {
        WadFile archive = new()
        {
            Version = WadVersion,
            Flags = WadFlags,
            HeaderXml = header
        };
        List<OutputFileEntry> entries = new();
        foreach (BuildPayload payload in payloads.OrderBy(item => item.ArchivePath, StringComparer.OrdinalIgnoreCase))
        {
            (string directoryPath, string fileName) = SplitArchivePath(payload.ArchivePath);
            Wad.DirectoryEntry directory = GetOrCreateDirectory(archive, directoryPath);
            OutputFileEntry entry = new(directory, payload.Data)
            {
                Name = fileName,
                Size = 0,
                Unknown0C = payload.Unknown0C,
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

    private static void ValidateWad(string path, IReadOnlyList<BuildPayload> expected)
    {
        using FileStream input = File.OpenRead(path);
        if (WadFile.IsBadHeader(input, out _, out _, out string reason))
        {
            throw new InvalidDataException($"Generated {Path.GetFileName(path)} has an invalid WAD header: {reason}");
        }

        input.Position = 0;
        WadFile archive = new();
        archive.Deserialize(input);
        bool compressed = (archive.Flags & Wad.ArchiveFlags.HasCompressedFiles) != 0;
        Dictionary<string, string> actual = new(StringComparer.OrdinalIgnoreCase);
        List<(string Path, Wad.FileEntry File)> files = new();
        foreach (Wad.DirectoryEntry root in archive.Directories)
        {
            CollectFiles(root, root.Name, files);
        }

        foreach ((string archivePath, Wad.FileEntry file) in files)
        {
            byte[] data = ReadFile(input, archive, file, compressed);
            actual[archivePath] = Convert.ToHexString(SHA256.HashData(data));
        }

        if (actual.Count != expected.Count)
        {
            throw new InvalidDataException(
                $"Generated {Path.GetFileName(path)} contains {actual.Count} files; {expected.Count} were expected.");
        }

        foreach (BuildPayload payload in expected)
        {
            string expectedHash = Convert.ToHexString(SHA256.HashData(payload.Data));
            if (!actual.TryGetValue(payload.ArchivePath, out string? actualHash)
                || !actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Generated {Path.GetFileName(path)} failed payload verification for {payload.ArchivePath}.");
            }
        }
    }

    private static void CollectFiles(
        Wad.DirectoryEntry directory,
        string path,
        ICollection<(string Path, Wad.FileEntry File)> output)
    {
        foreach (Wad.FileEntry file in directory.Files)
        {
            output.Add(($"{path}\\{file.Name}", file));
        }

        foreach (Wad.DirectoryEntry child in directory.Directories)
        {
            CollectFiles(child, $"{path}\\{child.Name}", output);
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

    private static (string DirectoryPath, string FileName) SplitArchivePath(string archivePath)
    {
        int separator = archivePath.LastIndexOf('\\');
        if (separator <= 0 || separator == archivePath.Length - 1)
        {
            throw new InvalidDataException($"Invalid archive path: {archivePath}");
        }

        return (archivePath[..separator], archivePath[(separator + 1)..]);
    }

    private static Wad.DirectoryEntry GetOrCreateDirectory(WadFile archive, string archivePath)
    {
        string[] parts = archivePath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        Wad.DirectoryEntry? current = archive.Directories.FirstOrDefault(directory =>
            directory.Name.Equals(parts[0], StringComparison.OrdinalIgnoreCase));
        if (current is null)
        {
            current = new Wad.DirectoryEntry(null) { Name = parts[0] };
            archive.Directories.Add(current);
        }

        foreach (string part in parts.Skip(1))
        {
            Wad.DirectoryEntry? child = current.Directories.FirstOrDefault(directory =>
                directory.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (child is null)
            {
                child = new Wad.DirectoryEntry(current) { Name = part };
                current.Directories.Add(child);
            }

            current = child;
        }

        return current;
    }

    private static string SafeDirectoryName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string result = new(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return result.Trim().TrimEnd('.');
    }

    private sealed record SourcePayload(string Path, uint Unknown0C, string WadName, int Order);
    private sealed record BuildPayload(string ArchivePath, byte[] Data, uint Unknown0C);

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
