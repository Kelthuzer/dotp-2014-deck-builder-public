using System.Security.Cryptography;
using System.Text.Json;
using Gibbed.Duels.FileFormats;
using Gibbed.IO;
using ICSharpCode.SharpZipLib.Zip.Compression.Streams;
using Wad = Gibbed.Duels.FileFormats.Wad;

namespace DeckBuilder.GameData;

internal sealed record WorkspaceSharedRuntimeSnapshot(
    string WadPath,
    string ManifestPath,
    int SourceMaxOrder,
    int Order,
    int CardRootCount,
    IReadOnlySet<string> Resources)
{
    public int ResourceCount => Resources.Count;
}

internal sealed record WorkspaceSharedRuntimeInspection(
    WorkspaceSharedRuntimeSnapshot? Runtime,
    string Reason)
{
    public bool IsUsable => Runtime is not null;
}

/// <summary>
/// Defines the contract between the one-per-workspace card runtime WAD and per-deck packaging.
/// The deck packager may depend on shared FUNCTIONS/SPECS/TEXT and card-driven runtime assets,
/// but it must not duplicate them into every deck WAD.
/// </summary>
internal static class WorkspaceSharedRuntimeContract
{
    public const string WadFileName = "Data_DLC_8000_DeckBuilder_Runtime.wad";
    public const int ManifestFormatVersion = 3;

    private static readonly string[] RequiredSharedTrees =
    {
        "FUNCTIONS",
        "SPECS",
        "TEXT_PERMANENT"
    };

    public static WorkspaceSharedRuntimeInspection Inspect(
        string wadPath,
        string workspaceDirectory,
        WorkspaceContentVariantScanResult scan,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wadPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceDirectory);
        ArgumentNullException.ThrowIfNull(scan);

        string wad = Path.GetFullPath(wadPath);
        string workspace = Path.GetFullPath(workspaceDirectory);
        string manifestPath = wad + ".runtime.json";
        if (!File.Exists(wad) || new FileInfo(wad).Length == 0)
            return Invalid("общий runtime WAD отсутствует или пуст");
        if (!File.Exists(manifestPath))
            return Invalid("рядом с общим runtime WAD нет .runtime.json manifest");

        int currentSourceMaxOrder;
        try
        {
            currentSourceMaxOrder = FindHighestWorkspaceWadOrder(workspace, cancellationToken);
        }
        catch (Exception exception)
        {
            return Invalid($"не удалось проверить order исходных WAD: {exception.Message}");
        }

        int currentCardRootCount = scan.CardVariants
            .Select(variant => variant.Reference)
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Select(reference => reference.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            JsonElement root = document.RootElement;

            int formatVersion = RequiredInt(root, "formatVersion");
            if (formatVersion != ManifestFormatVersion)
            {
                return Invalid(
                    $"manifest общего runtime имеет формат {formatVersion}, ожидается {ManifestFormatVersion}");
            }

            int sourceMaxOrder = RequiredInt(root, "sourceMaxOrder");
            int order = RequiredInt(root, "order");
            int cardRootCount = RequiredInt(root, "cardRootCount");
            int declaredResourceCount = RequiredInt(root, "runtimeResourceCount");

            if (sourceMaxOrder != currentSourceMaxOrder)
            {
                return Invalid(
                    $"runtime собран для sourceMaxOrder {sourceMaxOrder}, а текущий workspace требует {currentSourceMaxOrder}");
            }

            if (order <= currentSourceMaxOrder)
            {
                return Invalid(
                    $"runtime order {order} не загружается после исходных WAD (максимум {currentSourceMaxOrder})");
            }

            if (cardRootCount != currentCardRootCount)
            {
                return Invalid(
                    $"runtime рассчитан на {cardRootCount:N0} CARD_V2, а в текущем workspace {currentCardRootCount:N0}");
            }

            if (!root.TryGetProperty("sharedRuntimeTrees", out JsonElement treesElement)
                || treesElement.ValueKind != JsonValueKind.Array)
            {
                return Invalid("manifest общего runtime не содержит sharedRuntimeTrees");
            }

            HashSet<string> trees = treesElement.EnumerateArray()
                .Where(element => element.ValueKind == JsonValueKind.String)
                .Select(element => element.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            string[] missingTrees = RequiredSharedTrees
                .Where(tree => !trees.Contains(tree))
                .ToArray();
            if (missingTrees.Length > 0)
            {
                return Invalid(
                    $"runtime не содержит обязательные общие деревья: {string.Join(", ", missingTrees)}");
            }

            if (!root.TryGetProperty("resources", out JsonElement resourcesElement)
                || resourcesElement.ValueKind != JsonValueKind.Array)
            {
                return Invalid("manifest общего runtime не содержит список resources");
            }

            HashSet<string> resources = resourcesElement.EnumerateArray()
                .Where(element => element.ValueKind == JsonValueKind.String)
                .Select(element => NormalizeResourcePath(element.GetString()))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (resources.Count == 0)
                return Invalid("список resources общего runtime пуст");
            if (declaredResourceCount != resources.Count)
            {
                return Invalid(
                    $"manifest общего runtime объявляет {declaredResourceCount:N0} ресурсов, но перечисляет {resources.Count:N0}");
            }

            IReadOnlyDictionary<string, string> actualRuntimeHashes;
            try
            {
                actualRuntimeHashes = ReadRuntimeResourceHashes(wad, cancellationToken);
            }
            catch (Exception exception)
            {
                return Invalid($"общий runtime WAD не читается: {exception.Message}");
            }

            if (!resources.SetEquals(actualRuntimeHashes.Keys))
            {
                string[] missingFromWad = resources
                    .Where(resource => !actualRuntimeHashes.ContainsKey(resource))
                    .Take(8)
                    .ToArray();
                string[] unexpectedInWad = actualRuntimeHashes.Keys
                    .Where(resource => !resources.Contains(resource))
                    .Take(8)
                    .ToArray();
                string details = string.Empty;
                if (missingFromWad.Length > 0)
                    details += $" отсутствуют в WAD: {string.Join(", ", missingFromWad)};";
                if (unexpectedInWad.Length > 0)
                    details += $" лишние в WAD: {string.Join(", ", unexpectedInWad)};";
                return Invalid(
                    $"фактический состав runtime WAD ({actualRuntimeHashes.Count:N0}) не совпадает с manifest ({resources.Count:N0});{details}");
            }

            IReadOnlyDictionary<string, WorkspaceRuntimeSource> currentSources;
            try
            {
                currentSources = ResolveEffectiveWorkspaceResources(workspace, resources, cancellationToken);
            }
            catch (Exception exception)
            {
                return Invalid($"не удалось сверить runtime с текущим workspace: {exception.Message}");
            }

            string[] missingWorkspaceResources = resources
                .Where(resource => !currentSources.ContainsKey(resource))
                .Take(8)
                .ToArray();
            if (missingWorkspaceResources.Length > 0)
            {
                return Invalid(
                    $"в текущем workspace больше нет runtime-ресурсов: {string.Join(", ", missingWorkspaceResources)}");
            }

            foreach (string resource in resources.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                WorkspaceRuntimeSource source = currentSources[resource];
                string workspaceHash = HashFile(source.StoragePath);
                if (!actualRuntimeHashes[resource].Equals(workspaceHash, StringComparison.OrdinalIgnoreCase))
                {
                    return Invalid(
                        $"runtime-ресурс {resource} отличается от текущего workspace " +
                        $"({source.PackageName} / {source.WadName}, order {source.WadOrder})");
                }
            }

            return new WorkspaceSharedRuntimeInspection(
                new WorkspaceSharedRuntimeSnapshot(
                    wad,
                    manifestPath,
                    sourceMaxOrder,
                    order,
                    cardRootCount,
                    resources),
                "общий runtime подходит текущему workspace");
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidDataException)
        {
            return Invalid($"manifest общего runtime повреждён или несовместим: {exception.Message}");
        }
    }

    public static string[] MissingResources(
        WorkspaceSharedRuntimeSnapshot runtime,
        IEnumerable<string> requiredResources)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(requiredResources);

        return requiredResources
            .Where(resource => !string.IsNullOrWhiteSpace(resource))
            .Select(NormalizeResourcePath)
            .Where(resource => !runtime.Resources.Contains(resource))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(resource => resource, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int FindHighestWorkspaceWadOrder(
        string workspaceDirectory,
        CancellationToken cancellationToken)
    {
        string workspace = Path.GetFullPath(workspaceDirectory);
        if (!Directory.Exists(workspace))
            throw new DirectoryNotFoundException(workspace);

        int maxOrder = -1;
        string[] manifests = Directory.EnumerateFiles(
                workspace,
                GameVersionPackageService.ManifestFileName,
                SearchOption.AllDirectories)
            .OrderBy(path => Path.GetDirectoryName(path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        GameVersionPackageService packageService = new();
        foreach (string manifestPath in manifests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DotpVersionPackageManifest manifest = packageService.ReadManifest(Path.GetDirectoryName(manifestPath)!);
            foreach (DotpWadPackageManifest sourceWad in manifest.Wads)
                maxOrder = Math.Max(maxOrder, sourceWad.PrimaryOrder);
        }

        return maxOrder;
    }

    private static IReadOnlyDictionary<string, WorkspaceRuntimeSource> ResolveEffectiveWorkspaceResources(
        string workspace,
        IReadOnlySet<string> requiredResources,
        CancellationToken cancellationToken)
    {
        IReadOnlySet<string> workspaceCwTokenKeys =
            WorkspaceRuntimeCompatibility.ScanWorkspaceCwTokenKeys(workspace, cancellationToken);
        List<WorkspaceRuntimeSource> candidates = new();
        string[] manifests = Directory.EnumerateFiles(
                workspace,
                GameVersionPackageService.ManifestFileName,
                SearchOption.AllDirectories)
            .OrderBy(path => Path.GetDirectoryName(path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        GameVersionPackageService packageService = new();

        foreach (string manifestPath in manifests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string packageDirectory = Path.GetDirectoryName(manifestPath)!;
            DotpVersionPackageManifest manifest = packageService.ReadManifest(packageDirectory);
            foreach (DotpWadPackageManifest sourceWad in manifest.Wads)
            {
                string wadDirectory = Path.Combine(packageDirectory, "wads", SafeDirectoryName(sourceWad.Name));
                foreach (DotpWadFileManifest file in sourceWad.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string? relativePath = GetAllPlatformsRelativePath(file.ArchivePath);
                    if (relativePath is null || !requiredResources.Contains(relativePath))
                        continue;

                    string storagePath = Path.Combine(
                        wadDirectory,
                        file.StoragePath.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(storagePath))
                        throw new FileNotFoundException($"Workspace runtime payload is missing: {relativePath}", storagePath);

                    candidates.Add(new WorkspaceRuntimeSource(
                        relativePath,
                        manifest.VersionName,
                        sourceWad.Name,
                        sourceWad.PrimaryOrder,
                        storagePath));
                }
            }
        }

        return candidates
            .GroupBy(source => source.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(source => WorkspaceRuntimeCompatibility.CountCwTokenCoverage(
                    source.RelativePath,
                    source.StoragePath,
                    workspaceCwTokenKeys))
                .ThenBy(source => source.WadOrder)
                .ThenBy(source => source.WadName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(source => source.PackageName, StringComparer.OrdinalIgnoreCase)
                .Last())
            .ToDictionary(source => source.RelativePath, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, string> ReadRuntimeResourceHashes(
        string wadPath,
        CancellationToken cancellationToken)
    {
        using FileStream input = File.OpenRead(wadPath);
        if (WadFile.IsBadHeader(input, out _, out _, out string reason))
            throw new InvalidDataException(reason);

        input.Position = 0;
        WadFile wad = new();
        wad.Deserialize(input);
        bool compressed = (wad.Flags & Wad.ArchiveFlags.HasCompressedFiles) != 0;

        List<(string Path, Wad.FileEntry File)> files = new();
        foreach (Wad.DirectoryEntry directory in wad.Directories)
            Collect(directory, directory.Name, files);

        Dictionary<string, string> hashes = new(StringComparer.OrdinalIgnoreCase);
        const string marker = "DATA_ALL_PLATFORMS\\";
        foreach ((string archivePath, Wad.FileEntry file) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int markerIndex = archivePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
                continue;

            string relative = NormalizeResourcePath(archivePath[(markerIndex + marker.Length)..]);
            if (relative.Length == 0)
                continue;

            byte[] data = ReadFile(input, wad, file, compressed);
            if (!hashes.TryAdd(relative, Convert.ToHexString(SHA256.HashData(data))))
                throw new InvalidDataException($"Duplicate runtime resource in WAD: {relative}");
        }

        return hashes;
    }

    private static void Collect(
        Wad.DirectoryEntry directory,
        string path,
        ICollection<(string Path, Wad.FileEntry File)> output)
    {
        foreach (Wad.FileEntry file in directory.Files)
            output.Add(($"{path}\\{file.Name}", file));
        foreach (Wad.DirectoryEntry child in directory.Directories)
            Collect(child, $"{path}\\{child.Name}", output);
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

    private static string HashFile(string path)
    {
        using FileStream input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private static int RequiredInt(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement element)
            || element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out int value))
        {
            throw new InvalidDataException($"Required integer property '{propertyName}' is missing.");
        }

        return value;
    }

    private static string? GetAllPlatformsRelativePath(string archivePath)
    {
        string normalized = archivePath.Replace('/', '\\');
        const string marker = "DATA_ALL_PLATFORMS\\";
        int index = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? null : NormalizeResourcePath(normalized[(index + marker.Length)..]);
    }

    private static string NormalizeResourcePath(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().Replace('/', '\\');

    private static string SafeDirectoryName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string safe = new(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return safe.Trim().TrimEnd('.');
    }

    private static WorkspaceSharedRuntimeInspection Invalid(string reason) => new(null, reason);

    private sealed record WorkspaceRuntimeSource(
        string RelativePath,
        string PackageName,
        string WadName,
        int WadOrder,
        string StoragePath);
}
