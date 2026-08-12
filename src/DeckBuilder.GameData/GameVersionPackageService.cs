using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Gibbed.Duels.FileFormats;
using Gibbed.IO;
using ICSharpCode.SharpZipLib.Zip.Compression;
using ICSharpCode.SharpZipLib.Zip.Compression.Streams;
using Wad = Gibbed.Duels.FileFormats.Wad;

namespace DeckBuilder.GameData;

public sealed record VersionPackageProgress(
    string Stage,
    string Source,
    int Completed,
    int Total);

public sealed record VersionPackageExtractOptions(
    string SourceGameDirectory,
    string WorkspaceDirectory,
    string VersionName,
    bool ReplaceExisting = false);

public sealed record VersionPackageBuildOptions(
    string PackageDirectory,
    string OutputRootDirectory,
    bool IncludeIgnoredWads = true,
    bool ReplaceExisting = true);

public sealed record VersionPackageExtractResult(
    string PackageDirectory,
    string ManifestPath,
    int WadCount,
    int FileCount,
    long PayloadBytes);

public sealed record VersionPackageBuildResult(
    string OutputDirectory,
    int WadCount,
    int FileCount,
    int ModifiedFiles,
    long PayloadBytes);

public sealed record VersionPackageInfo(
    string PackageDirectory,
    string VersionName,
    DateTime CreatedUtc,
    string SourceGameDirectory,
    int WadCount,
    int FileCount,
    long SourceBytes)
{
    public string CreatedText => CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string SizeText => $"{SourceBytes / 1024d / 1024d / 1024d:N2} GiB";
}

public sealed record DotpVersionPackageManifest(
    int FormatVersion,
    string VersionName,
    DateTime CreatedUtc,
    string SourceGameDirectory,
    IReadOnlyList<DotpWadPackageManifest> Wads);

public sealed record DotpWadPackageManifest(
    string Name,
    long SourceSize,
    string SourceSha256,
    bool IsGameLoadable,
    int PrimaryOrder,
    ushort ArchiveVersion,
    uint ArchiveFlags,
    string HeaderFile,
    string HeaderSha256,
    IReadOnlyList<DotpWadFileManifest> Files);

public sealed record DotpWadFileManifest(
    string ArchivePath,
    string StoragePath,
    long OriginalSize,
    string OriginalSha256,
    uint Unknown0C);

/// <summary>
/// Creates editable, versioned snapshots of a Magic 2014 WAD directory and rebuilds
/// verified WAD files from those snapshots. Package payloads remain ordinary files so
/// multiple game versions can be compared with external diff/hash tools before rebuild.
/// </summary>
public sealed class GameVersionPackageService
{
    public const int CurrentFormatVersion = 1;
    public const string ManifestFileName = "dotp-version.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public Task<VersionPackageExtractResult> ExtractAsync(
        VersionPackageExtractOptions options,
        IProgress<VersionPackageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Task.Run(() => Extract(options, progress, cancellationToken), cancellationToken);
    }

    public Task<VersionPackageBuildResult> BuildAsync(
        VersionPackageBuildOptions options,
        IProgress<VersionPackageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Task.Run(() => Build(options, progress, cancellationToken), cancellationToken);
    }

    public IReadOnlyList<VersionPackageInfo> FindPackages(string workspaceDirectory)
    {
        if (string.IsNullOrWhiteSpace(workspaceDirectory) || !Directory.Exists(workspaceDirectory))
        {
            return Array.Empty<VersionPackageInfo>();
        }

        List<VersionPackageInfo> result = new();
        foreach (string directory in Directory.EnumerateDirectories(workspaceDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            string manifestPath = Path.Combine(directory, ManifestFileName);
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                DotpVersionPackageManifest manifest = ReadManifest(directory);
                result.Add(new VersionPackageInfo(
                    directory,
                    manifest.VersionName,
                    manifest.CreatedUtc,
                    manifest.SourceGameDirectory,
                    manifest.Wads.Count,
                    manifest.Wads.Sum(wad => wad.Files.Count),
                    manifest.Wads.Sum(wad => wad.SourceSize)));
            }
            catch
            {
                // A malformed directory is not a usable version package; keep discovery resilient.
            }
        }

        return result
            .OrderBy(item => item.VersionName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.CreatedUtc)
            .ToArray();
    }

    public DotpVersionPackageManifest ReadManifest(string packageDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        string path = Path.Combine(packageDirectory, ManifestFileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The selected directory is not a DotP version package.", path);
        }

        DotpVersionPackageManifest? manifest = JsonSerializer.Deserialize<DotpVersionPackageManifest>(
            File.ReadAllText(path),
            JsonOptions);
        if (manifest is null)
        {
            throw new InvalidDataException("The version package manifest is empty or invalid.");
        }

        if (manifest.FormatVersion != CurrentFormatVersion)
        {
            throw new InvalidDataException(
                $"Unsupported version package format {manifest.FormatVersion}; expected {CurrentFormatVersion}.");
        }

        return manifest;
    }

    private static VersionPackageExtractResult Extract(
        VersionPackageExtractOptions options,
        IProgress<VersionPackageProgress>? progress,
        CancellationToken cancellationToken)
    {
        string source = Path.GetFullPath(options.SourceGameDirectory);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException(source);
        }

        string versionName = options.VersionName.Trim();
        if (versionName.Length == 0)
        {
            throw new ArgumentException("A version name is required.", nameof(options));
        }

        string workspace = Path.GetFullPath(options.WorkspaceDirectory);
        Directory.CreateDirectory(workspace);
        string target = Path.Combine(workspace, SafeDirectoryName(versionName));
        if (Directory.Exists(target) && !options.ReplaceExisting)
        {
            throw new IOException($"Version package already exists: {target}");
        }

        string temporary = Path.Combine(workspace, $".{SafeDirectoryName(versionName)}.{Guid.NewGuid():N}.extracting");
        Directory.CreateDirectory(temporary);
        try
        {
            string[] wadPaths = Directory.EnumerateFiles(source, "*.wad", SearchOption.TopDirectoryOnly)
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (wadPaths.Length == 0)
            {
                throw new InvalidDataException("No WAD files were found in the selected game directory.");
            }

            List<DotpWadPackageManifest> wads = new();
            long payloadBytes = 0;
            int fileCount = 0;
            for (int index = 0; index < wadPaths.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string wadPath = wadPaths[index];
                string wadName = Path.GetFileName(wadPath);
                progress?.Report(new VersionPackageProgress(
                    "Extracting WAD",
                    wadName,
                    index,
                    wadPaths.Length));

                DotpWadPackageManifest manifest = ExtractWad(wadPath, temporary, cancellationToken);
                wads.Add(manifest);
                payloadBytes += manifest.Files.Sum(file => file.OriginalSize);
                fileCount += manifest.Files.Count;
            }

            DotpVersionPackageManifest package = new(
                CurrentFormatVersion,
                versionName,
                DateTime.UtcNow,
                source,
                wads);
            File.WriteAllText(
                Path.Combine(temporary, ManifestFileName),
                JsonSerializer.Serialize(package, JsonOptions));

            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }

            Directory.Move(temporary, target);
            progress?.Report(new VersionPackageProgress("Done", versionName, wadPaths.Length, wadPaths.Length));
            return new VersionPackageExtractResult(
                target,
                Path.Combine(target, ManifestFileName),
                wads.Count,
                fileCount,
                payloadBytes);
        }
        finally
        {
            if (Directory.Exists(temporary))
            {
                Directory.Delete(temporary, recursive: true);
            }
        }
    }

    private static DotpWadPackageManifest ExtractWad(
        string wadPath,
        string packageRoot,
        CancellationToken cancellationToken)
    {
        string wadName = Path.GetFileName(wadPath);
        string wadDirectory = Path.Combine(packageRoot, "wads", SafeDirectoryName(wadName));
        string payloadDirectory = Path.Combine(wadDirectory, "payload");
        Directory.CreateDirectory(payloadDirectory);

        string sourceSha;
        using (FileStream hashInput = File.OpenRead(wadPath))
        {
            sourceSha = Convert.ToHexString(SHA256.HashData(hashInput));
        }

        using FileStream input = File.OpenRead(wadPath);
        if (WadFile.IsBadHeader(input, out _, out _, out string reason))
        {
            throw new InvalidDataException($"{wadName}: {reason}");
        }

        input.Position = 0;
        WadFile archive = new();
        archive.Deserialize(input);
        bool compressed = HasFlag(archive.Flags, Wad.ArchiveFlags.HasCompressedFiles);
        byte[] header = archive.HeaderXml ?? Array.Empty<byte>();
        string headerPath = Path.Combine(wadDirectory, "header.xml");
        File.WriteAllBytes(headerPath, header);
        string headerSha = Convert.ToHexString(SHA256.HashData(header));

        List<(string ArchivePath, Wad.FileEntry File)> archiveFiles = new();
        foreach (Wad.DirectoryEntry root in archive.Directories)
        {
            CollectFiles(root, root.Name, archiveFiles);
        }

        List<DotpWadFileManifest> files = new();
        foreach ((string archivePath, Wad.FileEntry file) in archiveFiles
                     .OrderBy(item => item.ArchivePath, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] data = ReadFile(input, archive, file, compressed);
            string storageRelative = StoragePathFor(archivePath);
            string outputPath = SafeCombine(wadDirectory, storageRelative);
            string? outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            File.WriteAllBytes(outputPath, data);
            files.Add(new DotpWadFileManifest(
                archivePath,
                storageRelative.Replace(Path.DirectorySeparatorChar, '/'),
                data.LongLength,
                Convert.ToHexString(SHA256.HashData(data)),
                file.Unknown0C));
        }

        return new DotpWadPackageManifest(
            wadName,
            new FileInfo(wadPath).Length,
            sourceSha,
            GameWadSelection.IsSupported(wadPath),
            ReadPrimaryOrder(header),
            archive.Version,
            (uint)archive.Flags,
            "header.xml",
            headerSha,
            files);
    }

    private static VersionPackageBuildResult Build(
        VersionPackageBuildOptions options,
        IProgress<VersionPackageProgress>? progress,
        CancellationToken cancellationToken)
    {
        string packageDirectory = Path.GetFullPath(options.PackageDirectory);
        DotpVersionPackageManifest manifest = ReadManifestStatic(packageDirectory);
        string outputRoot = Path.GetFullPath(options.OutputRootDirectory);
        Directory.CreateDirectory(outputRoot);
        string target = Path.Combine(outputRoot, SafeDirectoryName(manifest.VersionName));
        if (Directory.Exists(target) && !options.ReplaceExisting)
        {
            throw new IOException($"Build output already exists: {target}");
        }

        string temporary = Path.Combine(outputRoot, $".{SafeDirectoryName(manifest.VersionName)}.{Guid.NewGuid():N}.building");
        Directory.CreateDirectory(temporary);
        try
        {
            DotpWadPackageManifest[] selected = manifest.Wads
                .Where(wad => options.IncludeIgnoredWads || wad.IsGameLoadable)
                .OrderBy(wad => wad.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            int modifiedFiles = 0;
            int totalFiles = 0;
            long payloadBytes = 0;

            for (int index = 0; index < selected.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DotpWadPackageManifest wad = selected[index];
                progress?.Report(new VersionPackageProgress(
                    "Building WAD",
                    wad.Name,
                    index,
                    selected.Length));

                string wadPackageDirectory = Path.Combine(packageDirectory, "wads", SafeDirectoryName(wad.Name));
                string headerPath = SafeCombine(wadPackageDirectory, wad.HeaderFile);
                byte[] header = File.ReadAllBytes(headerPath);
                List<BuildPayload> payloads = new();
                foreach (DotpWadFileManifest file in wad.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string path = SafeCombine(
                        wadPackageDirectory,
                        file.StoragePath.Replace('/', Path.DirectorySeparatorChar));
                    byte[] data = File.ReadAllBytes(path);
                    string currentHash = Convert.ToHexString(SHA256.HashData(data));
                    if (!currentHash.Equals(file.OriginalSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        modifiedFiles++;
                    }

                    payloads.Add(new BuildPayload(file.ArchivePath, data, file.Unknown0C, currentHash));
                    totalFiles++;
                    payloadBytes += data.LongLength;
                }

                string outputPath = Path.Combine(temporary, wad.Name);
                WriteWad(outputPath, wad.ArchiveVersion, (Wad.ArchiveFlags)wad.ArchiveFlags, header, payloads);
                ValidateWad(outputPath, payloads);
            }

            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }

            Directory.Move(temporary, target);
            progress?.Report(new VersionPackageProgress("Done", manifest.VersionName, selected.Length, selected.Length));
            return new VersionPackageBuildResult(target, selected.Length, totalFiles, modifiedFiles, payloadBytes);
        }
        finally
        {
            if (Directory.Exists(temporary))
            {
                Directory.Delete(temporary, recursive: true);
            }
        }
    }

    private static DotpVersionPackageManifest ReadManifestStatic(string packageDirectory)
    {
        string path = Path.Combine(packageDirectory, ManifestFileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The selected directory is not a DotP version package.", path);
        }

        DotpVersionPackageManifest? manifest = JsonSerializer.Deserialize<DotpVersionPackageManifest>(
            File.ReadAllText(path),
            JsonOptions);
        if (manifest is null || manifest.FormatVersion != CurrentFormatVersion)
        {
            throw new InvalidDataException("The DotP version package manifest is invalid or unsupported.");
        }

        return manifest;
    }

    private static void WriteWad(
        string outputPath,
        ushort version,
        Wad.ArchiveFlags flags,
        byte[] header,
        IReadOnlyList<BuildPayload> payloads)
    {
        WadFile archive = new()
        {
            Version = version,
            Flags = flags,
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

        bool compressed = HasFlag(flags, Wad.ArchiveFlags.HasCompressedFiles);
        using FileStream output = File.Create(outputPath);
        archive.Serialize(output);
        foreach (OutputFileEntry entry in entries)
        {
            archive.DataOffsets[entry.OffsetIndex] = checked((uint)output.Position);
            if (!compressed)
            {
                entry.Size = checked((uint)entry.Data.Length);
                output.Write(entry.Data, 0, entry.Data.Length);
                continue;
            }

            using MemoryStream compressedData = new();
            DeflaterOutputStream deflater = new(compressedData, new Deflater(Deflater.BEST_COMPRESSION));
            deflater.Write(entry.Data, 0, entry.Data.Length);
            deflater.Finish();
            if (compressedData.Length < entry.Data.Length)
            {
                entry.Size = checked((uint)(4 + compressedData.Length));
                output.WriteValueU32(checked((uint)entry.Data.Length));
                compressedData.Position = 0;
                compressedData.CopyTo(output);
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
        bool compressed = HasFlag(archive.Flags, Wad.ArchiveFlags.HasCompressedFiles);
        Dictionary<string, string> actual = new(StringComparer.OrdinalIgnoreCase);
        List<(string ArchivePath, Wad.FileEntry File)> files = new();
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
            if (!actual.TryGetValue(payload.ArchivePath, out string? hash)
                || !hash.Equals(payload.CurrentSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Generated {Path.GetFileName(path)} failed payload verification for {payload.ArchivePath}.");
            }
        }
    }

    private static void CollectFiles(
        Wad.DirectoryEntry directory,
        string path,
        ICollection<(string ArchivePath, Wad.FileEntry File)> output)
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

    private static int ReadPrimaryOrder(byte[] header)
    {
        if (header.Length == 0)
        {
            return 0;
        }

        try
        {
            string text = DecodeText(header);
            XDocument document = XDocument.Parse(text);
            XElement? entry = document.Descendants().FirstOrDefault(element =>
                element.Name.LocalName.Equals("ENTRY", StringComparison.OrdinalIgnoreCase)
                && Attribute(element, "platform").Equals("ALL", StringComparison.OrdinalIgnoreCase));
            return entry is not null && int.TryParse(Attribute(entry, "order"), out int order) ? order : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string DecodeText(byte[] data)
    {
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
        {
            return System.Text.Encoding.UTF8.GetString(data, 3, data.Length - 3);
        }

        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
        {
            return System.Text.Encoding.Unicode.GetString(data, 2, data.Length - 2);
        }

        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
        {
            return System.Text.Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
        }

        return System.Text.Encoding.UTF8.GetString(data);
    }

    private static string Attribute(XElement element, string name) => element.Attributes()
        .FirstOrDefault(attribute => attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?.Value.Trim() ?? string.Empty;

    private static string StoragePathFor(string archivePath)
    {
        string[] parts = archivePath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(part => part is "." or ".."))
        {
            throw new InvalidDataException($"Unsafe archive path: {archivePath}");
        }

        string result = "payload";
        foreach (string part in parts)
        {
            if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new InvalidDataException($"Archive path contains a filename that Windows cannot extract: {archivePath}");
            }

            result = Path.Combine(result, part);
        }

        return result;
    }

    private static string SafeCombine(string root, string relativePath)
    {
        string normalizedRelative = relativePath.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalizedRelative))
        {
            throw new InvalidDataException($"Rooted package path is not allowed: {relativePath}");
        }

        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(Path.Combine(root, normalizedRelative));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Package path escapes its root: {relativePath}");
        }

        return fullPath;
    }

    private static string SafeDirectoryName(string value)
    {
        string trimmed = value.Trim();
        char[] invalid = Path.GetInvalidFileNameChars();
        string result = new(trimmed.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        result = result.Trim().TrimEnd('.');
        return result.Length == 0 ? "version" : result;
    }

    private static (string DirectoryPath, string FileName) SplitArchivePath(string archivePath)
    {
        int separator = archivePath.LastIndexOf('\\');
        if (separator <= 0 || separator == archivePath.Length - 1)
        {
            throw new InvalidDataException($"Invalid archive path in manifest: {archivePath}");
        }

        return (archivePath[..separator], archivePath[(separator + 1)..]);
    }

    private static Wad.DirectoryEntry GetOrCreateDirectory(WadFile archive, string archivePath)
    {
        string[] parts = archivePath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            throw new InvalidDataException("A WAD file entry has no directory path.");
        }

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

    private static bool HasFlag(Wad.ArchiveFlags value, Wad.ArchiveFlags flag) => (value & flag) == flag;

    private sealed record BuildPayload(string ArchivePath, byte[] Data, uint Unknown0C, string CurrentSha256);

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
