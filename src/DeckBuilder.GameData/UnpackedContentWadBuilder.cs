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
    PortableCards,
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
/// Builds a Magic 2014 WAD from either a plain DATA_ALL_PLATFORMS tree or one extracted version
/// package. PortableCards is intentionally permissive because its staging tree has already been
/// curated by WorkspaceSelectedCardsBuilder.
/// </summary>
public sealed class UnpackedContentWadBuilder
{
    private const ushort WadVersion = 0x202;
    private const int CompressionLevel = Deflater.DEFAULT_COMPRESSION;

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

    internal UnpackedContentBuildResult Build(
        UnpackedContentBuildOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        string source = Path.GetFullPath(options.SourceDirectory);
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException(source);

        string outputPath = Path.GetFullPath(options.OutputPath);
        string outputDirectory = Path.GetDirectoryName(outputPath)
            ?? throw new DirectoryNotFoundException("The WAD output directory is missing.");
        Directory.CreateDirectory(outputDirectory);

        if (File.Exists(outputPath) && !options.ReplaceExisting)
            throw new IOException($"Output WAD already exists: {outputPath}");

        ContentCollection collection = CollectContent(source, options.Kind, cancellationToken);
        if (collection.Content.Count == 0)
        {
            throw new InvalidDataException(
                $"No {options.Kind.ToString().ToLowerInvariant()} content was found under {source}.");
        }

        string rootName = Path.GetFileNameWithoutExtension(outputPath).ToUpperInvariant();
        byte[] header = CreateHeader(rootName, options.Order);
        List<WadPayload> payloads = collection.Content
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new WadPayload(
                $"{rootName}\\DATA_ALL_PLATFORMS\\{pair.Key}",
                File.ReadAllBytes(pair.Value.Path),
                pair.Value.Unknown0C))
            .ToList();
        payloads.Add(new WadPayload($"{rootName}\\HEADER.XML", header, 0));

        string temporaryPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            WriteWad(temporaryPath, header, payloads, cancellationToken);
            ValidateWad(temporaryPath, payloads, cancellationToken);
            File.Move(temporaryPath, outputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }

        return new UnpackedContentBuildResult(
            outputPath,
            options.Kind,
            collection.Content.Count,
            collection.Content.Values.Sum(payload => new FileInfo(payload.Path).Length),
            collection.OverrideCount,
            collection.UsedVersionManifest);
    }

    private static ContentCollection CollectContent(
        string source,
        UnpackedContentKind kind,
        CancellationToken cancellationToken)
    {
        return File.Exists(Path.Combine(source, GameVersionPackageService.ManifestFileName))
            ? CollectFromVersionPackage(source, kind, cancellationToken)
            : CollectFromPlainTree(source, kind, cancellationToken);
    }

    private static ContentCollection CollectFromPlainTree(
        string source,
        UnpackedContentKind kind,
        CancellationToken cancellationToken)
    {
        Dictionary<string, SourcePayload> content = new(StringComparer.OrdinalIgnoreCase);
        foreach (string filePath in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? relativePath = GetContentRelativePath(source, filePath, kind);
            if (relativePath is null)
                continue;

            if (content.TryGetValue(relativePath, out SourcePayload? existing))
            {
                if (FilesEqual(existing.Path, filePath))
                    continue;

                throw new InvalidDataException(
                    $"Two different files map to the same WAD path '{relativePath}'.\n" +
                    $"First: {existing.Path}\nSecond: {filePath}\n" +
                    "Use a narrower folder or an extracted version package so WAD priority can resolve the override.");
            }

            content[relativePath] = new SourcePayload(filePath, 0);
        }

        return new ContentCollection(content, 0, false);
    }

    private static ContentCollection CollectFromVersionPackage(
        string packageDirectory,
        UnpackedContentKind kind,
        CancellationToken cancellationToken)
    {
        DotpVersionPackageManifest manifest = new GameVersionPackageService().ReadManifest(packageDirectory);
        Dictionary<string, SourcePayload> content = new(StringComparer.OrdinalIgnoreCase);
        int overrides = 0;

        foreach (DotpWadPackageManifest wad in manifest.Wads
                     .OrderBy(wad => wad.PrimaryOrder)
                     .ThenBy(wad => wad.Name, StringComparer.OrdinalIgnoreCase))
        {
            string wadDirectory = Path.Combine(packageDirectory, "wads", SafeDirectoryName(wad.Name));
            foreach (DotpWadFileManifest file in wad.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? relativePath = ContentRelativeFromArchivePath(file.ArchivePath, kind);
                if (relativePath is null)
                    continue;

                string storagePath = Path.Combine(
                    wadDirectory,
                    file.StoragePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(storagePath))
                {
                    throw new FileNotFoundException(
                        $"Extracted payload listed by the package manifest is missing: {file.StoragePath}",
                        storagePath);
                }

                if (content.ContainsKey(relativePath))
                    overrides++;

                // Manifest order is already sorted from low to high priority; later WADs win.
                content[relativePath] = new SourcePayload(storagePath, file.Unknown0C);
            }
        }

        return new ContentCollection(content, overrides, true);
    }

    private static string? GetContentRelativePath(
        string sourceRoot,
        string filePath,
        UnpackedContentKind kind)
    {
        string relative = Path.GetRelativePath(sourceRoot, filePath).Replace('/', '\\');
        const string marker = "DATA_ALL_PLATFORMS\\";
        int markerIndex = relative.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
        {
            string contentPath = relative[(markerIndex + marker.Length)..];
            return MatchesKind(contentPath, kind) ? contentPath : null;
        }

        string[] parts = relative.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < parts.Length; index++)
        {
            string candidate = string.Join('\\', parts.Skip(index));
            if (MatchesKind(candidate, kind))
                return candidate;
        }

        string sourceName = Path.GetFileName(
            sourceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if ((kind == UnpackedContentKind.Cards || kind == UnpackedContentKind.PortableCards)
            && sourceName.Equals("CARDS", StringComparison.OrdinalIgnoreCase))
        {
            return $"CARDS\\{relative}";
        }

        if (kind == UnpackedContentKind.Decks && sourceName.Equals("DECKS", StringComparison.OrdinalIgnoreCase))
            return $"DECKS\\{relative}";

        return null;
    }

    private static string? ContentRelativeFromArchivePath(
        string archivePath,
        UnpackedContentKind kind)
    {
        string normalized = archivePath.Replace('/', '\\');
        const string marker = "DATA_ALL_PLATFORMS\\";
        int index = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return null;

        string relativePath = normalized[(index + marker.Length)..];
        return MatchesKind(relativePath, kind) ? relativePath : null;
    }

    private static bool MatchesKind(string relativePath, UnpackedContentKind kind)
    {
        string path = relativePath.Replace('/', '\\');
        return kind switch
        {
            UnpackedContentKind.PortableCards =>
                !StartsWithTree(path, "DECKS")
                && !StartsWithTree(path, "UNLOCKS")
                && !StartsWithTree(path, "AI_PERSONALITIES"),

            UnpackedContentKind.Cards =>
                StartsWithTree(path, "CARDS")
                || StartsWithTree(path, "FUNCTIONS")
                || StartsWithTree(path, "SPECS")
                || StartsWithTree(path, "TEXT_PERMANENT")
                || StartsWithTree(path, "ART_ASSETS\\ILLUSTRATIONS")
                || StartsWithTree(path, "ART_ASSETS\\TEXTURES")
                || StartsWithTree(path, "ART_ASSETS\\FRONTEND"),

            UnpackedContentKind.Decks =>
                StartsWithTree(path, "DECKS")
                || StartsWithTree(path, "UNLOCKS")
                || StartsWithTree(path, "AI_PERSONALITIES")
                || StartsWithTree(path, "TEXT_PERMANENT")
                || StartsWithTree(path, "ART_ASSETS\\TEXTURES\\DECKS"),

            _ => false
        };
    }

    private static bool StartsWithTree(string path, string tree) =>
        path.Equals(tree, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(tree + "\\", StringComparison.OrdinalIgnoreCase);

    private static bool FilesEqual(string first, string second)
    {
        FileInfo firstInfo = new(first);
        FileInfo secondInfo = new(second);
        if (firstInfo.Length != secondInfo.Length)
            return false;

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

    private static void WriteWad(
        string path,
        byte[] header,
        IReadOnlyList<WadPayload> payloads,
        CancellationToken cancellationToken)
    {
        WadFile archive = new()
        {
            Version = WadVersion,
            Flags = WadFlags,
            HeaderXml = header
        };

        List<OutputFileEntry> entries = new();
        foreach (WadPayload payload in payloads.OrderBy(item => item.ArchivePath, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            cancellationToken.ThrowIfCancellationRequested();
            archive.DataOffsets[entry.OffsetIndex] = checked((uint)output.Position);

            using MemoryStream compressed = new();
            DeflaterOutputStream deflater = new(compressed, new Deflater(CompressionLevel));
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

    private static void ValidateWad(
        string path,
        IReadOnlyList<WadPayload> expected,
        CancellationToken cancellationToken)
    {
        using FileStream input = File.OpenRead(path);
        if (WadFile.IsBadHeader(input, out _, out _, out string reason))
            throw new InvalidDataException($"Generated {Path.GetFileName(path)} has an invalid WAD header: {reason}");

        input.Position = 0;
        WadFile archive = new();
        archive.Deserialize(input);
        bool compressed = (archive.Flags & Wad.ArchiveFlags.HasCompressedFiles) != 0;

        List<(string Path, Wad.FileEntry File)> files = new();
        foreach (Wad.DirectoryEntry root in archive.Directories)
            CollectFiles(root, root.Name, files);

        Dictionary<string, string> actualHashes = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string archivePath, Wad.FileEntry file) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] data = ReadFile(input, archive, file, compressed);
            actualHashes[archivePath] = Convert.ToHexString(SHA256.HashData(data));
        }

        if (actualHashes.Count != expected.Count)
        {
            throw new InvalidDataException(
                $"Generated {Path.GetFileName(path)} contains {actualHashes.Count} files; {expected.Count} were expected.");
        }

        foreach (WadPayload payload in expected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string expectedHash = Convert.ToHexString(SHA256.HashData(payload.Data));
            if (!actualHashes.TryGetValue(payload.ArchivePath, out string? actualHash)
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
            output.Add(($"{path}\\{file.Name}", file));
        foreach (Wad.DirectoryEntry child in directory.Directories)
            CollectFiles(child, $"{path}\\{child.Name}", output);
    }

    private static byte[] ReadFile(FileStream input, WadFile archive, Wad.FileEntry file, bool compressed)
    {
        input.Position = archive.DataOffsets[file.OffsetIndex];
        if (!compressed)
            return ReadExactly(input, checked((int)file.Size));

        int inflatedLength = input.ReadValueS32(archive.Endian);
        int storedLength = checked((int)file.Size) - 4;
        if (inflatedLength == -1)
            return ReadExactly(input, storedLength);

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
                throw new EndOfStreamException($"Expected {length} bytes, received {offset}.");
            offset += read;
        }
        return result;
    }

    private static (string DirectoryPath, string FileName) SplitArchivePath(string archivePath)
    {
        int separator = archivePath.LastIndexOf('\\');
        if (separator <= 0 || separator == archivePath.Length - 1)
            throw new InvalidDataException($"Invalid archive path: {archivePath}");
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
        string safe = new(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return safe.Trim().TrimEnd('.');
    }

    private sealed record ContentCollection(
        Dictionary<string, SourcePayload> Content,
        int OverrideCount,
        bool UsedVersionManifest);

    private sealed record SourcePayload(string Path, uint Unknown0C);
    private sealed record WadPayload(string ArchivePath, byte[] Data, uint Unknown0C);

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
