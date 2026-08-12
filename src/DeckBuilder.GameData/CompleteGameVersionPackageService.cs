using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Wad = Gibbed.Duels.FileFormats.Wad;

namespace DeckBuilder.GameData;

/// <summary>
/// Extends normal version extraction with the loose WAD-directory layout supported by the
/// original Deck Builder. Modded Magic 2014 installations can keep a loadable content source as
/// Data_...\header.xml + DATA_ALL_PLATFORMS\... beside ordinary .wad files. Those directories
/// must be captured as well or an extracted workspace silently loses cards/art/decks.
/// </summary>
public sealed class CompleteGameVersionPackageService
{
    private readonly GameVersionPackageService _inner = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public IReadOnlyList<VersionPackageInfo> FindPackages(string workspaceDirectory) =>
        _inner.FindPackages(workspaceDirectory);

    public DotpVersionPackageManifest ReadManifest(string packageDirectory) =>
        _inner.ReadManifest(packageDirectory);

    public Task<VersionPackageBuildResult> BuildAsync(
        VersionPackageBuildOptions options,
        IProgress<VersionPackageProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        _inner.BuildAsync(options, progress, cancellationToken);

    public async Task<VersionPackageExtractResult> ExtractAsync(
        VersionPackageExtractOptions options,
        IProgress<VersionPackageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        VersionPackageExtractResult extracted = await _inner.ExtractAsync(options, progress, cancellationToken);
        return await Task.Run(
            () => AddLooseWadDirectories(options, extracted, progress, cancellationToken),
            cancellationToken);
    }

    private static VersionPackageExtractResult AddLooseWadDirectories(
        VersionPackageExtractOptions options,
        VersionPackageExtractResult extracted,
        IProgress<VersionPackageProgress>? progress,
        CancellationToken cancellationToken)
    {
        string source = Path.GetFullPath(options.SourceGameDirectory);
        string[] looseDirectories = Directory.EnumerateDirectories(source, "*", SearchOption.TopDirectoryOnly)
            .Where(GameWadSelection.IsSupported)
            .Where(IsLooseWadDirectory)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (looseDirectories.Length == 0)
        {
            return extracted;
        }

        string packageDirectory = extracted.PackageDirectory;
        string manifestPath = Path.Combine(packageDirectory, GameVersionPackageService.ManifestFileName);
        DotpVersionPackageManifest manifest = JsonSerializer.Deserialize<DotpVersionPackageManifest>(
            File.ReadAllText(manifestPath),
            JsonOptions) ?? throw new InvalidDataException("The freshly extracted version manifest is invalid.");

        List<DotpWadPackageManifest> sources = manifest.Wads.ToList();
        HashSet<string> usedNames = sources
            .Select(item => item.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        int addedFiles = 0;
        long addedBytes = 0;

        for (int index = 0; index < looseDirectories.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string looseDirectory = looseDirectories[index];
            string sourceName = Path.GetFileName(looseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string packageName = sourceName.EndsWith(".wad", StringComparison.OrdinalIgnoreCase)
                ? sourceName
                : sourceName + ".wad";
            if (!usedNames.Add(packageName))
            {
                packageName = UniqueLooseName(sourceName, usedNames);
                usedNames.Add(packageName);
            }

            progress?.Report(new VersionPackageProgress(
                "Importing unpacked WAD",
                sourceName,
                index,
                looseDirectories.Length));

            DotpWadPackageManifest loose = ImportLooseWadDirectory(
                looseDirectory,
                packageDirectory,
                packageName,
                cancellationToken);
            sources.Add(loose);
            addedFiles += loose.Files.Count;
            addedBytes += loose.Files.Sum(file => file.OriginalSize);
        }

        DotpVersionPackageManifest updated = new(
            manifest.FormatVersion,
            manifest.VersionName,
            manifest.CreatedUtc,
            manifest.SourceGameDirectory,
            sources
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray());
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(updated, JsonOptions));

        progress?.Report(new VersionPackageProgress(
            "Done including unpacked WADs",
            manifest.VersionName,
            looseDirectories.Length,
            looseDirectories.Length));
        return new VersionPackageExtractResult(
            packageDirectory,
            manifestPath,
            sources.Count,
            extracted.FileCount + addedFiles,
            extracted.PayloadBytes + addedBytes);
    }

    private static DotpWadPackageManifest ImportLooseWadDirectory(
        string sourceDirectory,
        string packageDirectory,
        string packageName,
        CancellationToken cancellationToken)
    {
        string rootName = Path.GetFileNameWithoutExtension(packageName);
        if (rootName.EndsWith("__LOOSE", StringComparison.OrdinalIgnoreCase))
        {
            rootName = rootName[..^"__LOOSE".Length];
        }

        string packageWadDirectory = Path.Combine(packageDirectory, "wads", SafeDirectoryName(packageName));
        Directory.CreateDirectory(packageWadDirectory);

        string sourceHeader = FindHeader(sourceDirectory)
            ?? throw new InvalidDataException($"{sourceDirectory} looks like an unpacked WAD but has no header.xml.");
        byte[] header = File.ReadAllBytes(sourceHeader);
        File.WriteAllBytes(Path.Combine(packageWadDirectory, "header.xml"), header);
        string headerSha = Convert.ToHexString(SHA256.HashData(header));

        string[] sourceFiles = Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            .OrderBy(path => Path.GetRelativePath(sourceDirectory, path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        List<DotpWadFileManifest> files = new(sourceFiles.Length);
        long sourceBytes = 0;
        using IncrementalHash sourceHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        foreach (string sourcePath in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(sourceDirectory, sourcePath)
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            string normalizedRelative = relative.Replace(Path.DirectorySeparatorChar, '\\');
            string archivePath = $"{rootName}\\{normalizedRelative}";
            string storageRelative = Path.Combine(
                "payload",
                archivePath.Replace('\\', Path.DirectorySeparatorChar));
            string outputPath = SafeCombine(packageWadDirectory, storageRelative);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.Copy(sourcePath, outputPath, overwrite: true);

            long size = new FileInfo(sourcePath).Length;
            string fileSha;
            using (FileStream input = File.OpenRead(sourcePath))
            {
                fileSha = Convert.ToHexString(SHA256.HashData(input));
            }

            sourceBytes += size;
            byte[] pathBytes = Encoding.UTF8.GetBytes(normalizedRelative.ToUpperInvariant());
            sourceHash.AppendData(pathBytes);
            sourceHash.AppendData(new byte[] { 0 });
            sourceHash.AppendData(Convert.FromHexString(fileSha));

            files.Add(new DotpWadFileManifest(
                archivePath,
                storageRelative.Replace(Path.DirectorySeparatorChar, '/'),
                size,
                fileSha,
                0));
        }

        string sourceSha = Convert.ToHexString(sourceHash.GetHashAndReset());
        Wad.ArchiveFlags flags = Wad.ArchiveFlags.Unknown6Observed
            | Wad.ArchiveFlags.HasDataTypes
            | Wad.ArchiveFlags.HasCompressedFiles;
        return new DotpWadPackageManifest(
            packageName,
            sourceBytes,
            sourceSha,
            true,
            ReadPrimaryOrder(header),
            0x202,
            (uint)flags,
            "header.xml",
            headerSha,
            files);
    }

    private static bool IsLooseWadDirectory(string path)
    {
        return FindHeader(path) is not null
            && Directory.EnumerateDirectories(path, "*", SearchOption.TopDirectoryOnly)
                .Any(directory => Path.GetFileName(directory)
                    .Equals("DATA_ALL_PLATFORMS", StringComparison.OrdinalIgnoreCase));
    }

    private static string? FindHeader(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => Path.GetFileName(path).Equals("header.xml", StringComparison.OrdinalIgnoreCase));

    private static string UniqueLooseName(string sourceName, IReadOnlySet<string> usedNames)
    {
        string baseName = sourceName.EndsWith(".wad", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(sourceName)
            : sourceName;
        string candidate = baseName + "__LOOSE.wad";
        int suffix = 2;
        while (usedNames.Contains(candidate))
        {
            candidate = $"{baseName}__LOOSE_{suffix++}.wad";
        }

        return candidate;
    }

    private static int ReadPrimaryOrder(byte[] header)
    {
        try
        {
            using MemoryStream stream = new(header, writable: false);
            XDocument document = XDocument.Load(stream);
            int[] orders = document.Descendants()
                .Where(element => element.Name.LocalName.Equals("ENTRY", StringComparison.OrdinalIgnoreCase))
                .Select(element => element.Attributes()
                    .FirstOrDefault(attribute => attribute.Name.LocalName.Equals("order", StringComparison.OrdinalIgnoreCase))
                    ?.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => int.TryParse(value, out int order) ? order : int.MinValue)
                .Where(value => value != int.MinValue)
                .ToArray();
            return orders.Length == 0 ? 0 : orders.Max();
        }
        catch
        {
            return 0;
        }
    }

    private static string SafeDirectoryName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string result = new(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return result.Trim().TrimEnd('.');
    }

    private static string SafeCombine(string root, string relative)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(Path.Combine(root, relative));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Unsafe extracted path: {relative}");
        }

        return fullPath;
    }
}
