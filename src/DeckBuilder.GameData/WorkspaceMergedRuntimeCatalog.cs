using System.Security.Cryptography;
using System.Text;

namespace DeckBuilder.GameData;

internal sealed record WorkspaceMergedRuntimeResource(
    string RelativePath,
    string PackageName,
    string WadName,
    int WadOrder,
    string StoragePath,
    string Sha256);

internal sealed record WorkspaceMergedRuntimeCatalogSnapshot(
    int SourceMaxOrder,
    IReadOnlyList<WorkspaceMergedRuntimeResource> Resources,
    IReadOnlySet<string> MissingCwTokenKeys,
    string Fingerprint)
{
    public int ResourceCount => Resources.Count;
}

/// <summary>
/// Builds the effective shared runtime view of an extracted workspace. Every shared non-card
/// runtime resource is included once. Duplicate paths from several WADs are collapsed to the
/// effective source, with CW_TOKENS compatibility taking precedence before normal WAD order.
/// Card/deck payloads, personalities and ordinary card illustrations stay deck-specific.
/// </summary>
internal static class WorkspaceMergedRuntimeCatalog
{
    public const string CoverageMode = "all-effective-runtime-v1";

    private static readonly string[] ForbiddenTrees =
    {
        "CARDS",
        "DECKS",
        "UNLOCKS",
        "AI_PERSONALITIES"
    };

    private const string IllustrationTree = "ART_ASSETS\\ILLUSTRATIONS";

    private static readonly HashSet<string> EditorExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".AHK",
        ".PS1",
        ".CS",
        ".CSX",
        ".SLN",
        ".CSPROJ",
        ".USER",
        ".IML",
        ".MD",
        ".BAK",
        ".TMP"
    };

    public static WorkspaceMergedRuntimeCatalogSnapshot Load(
        string workspaceDirectory,
        ICollection<string> warnings,
        ISet<string> warningKeys,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceDirectory);
        ArgumentNullException.ThrowIfNull(warnings);
        ArgumentNullException.ThrowIfNull(warningKeys);

        string workspace = Path.GetFullPath(workspaceDirectory);
        if (!Directory.Exists(workspace))
            throw new DirectoryNotFoundException(workspace);

        IReadOnlySet<string> workspaceCwTokenKeys =
            WorkspaceRuntimeCompatibility.ScanWorkspaceCwTokenKeys(workspace, cancellationToken);
        List<RuntimeCandidate> candidates = new();
        int sourceMaxOrder = -1;

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
            DotpVersionPackageManifest manifest;
            try
            {
                manifest = packageService.ReadManifest(packageDirectory);
            }
            catch (Exception exception)
            {
                AddWarning(warnings, warningKeys, $"Could not read runtime manifest {manifestPath}: {exception.Message}");
                continue;
            }

            foreach (DotpWadPackageManifest wad in manifest.Wads)
            {
                sourceMaxOrder = Math.Max(sourceMaxOrder, wad.PrimaryOrder);
                string wadDirectory = Path.Combine(packageDirectory, "wads", SafeDirectoryName(wad.Name));
                foreach (DotpWadFileManifest file in wad.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string? relativePath = GetAllPlatformsRelativePath(file.ArchivePath);
                    if (relativePath is null || !IsSharedRuntimeResource(relativePath))
                        continue;

                    string storagePath = Path.Combine(
                        wadDirectory,
                        file.StoragePath.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(storagePath))
                    {
                        AddWarning(
                            warnings,
                            warningKeys,
                            $"Merged runtime payload {relativePath} from {manifest.VersionName} / {wad.Name} is missing from the workspace.");
                        continue;
                    }

                    candidates.Add(new RuntimeCandidate(
                        relativePath,
                        manifest.VersionName,
                        wad.Name,
                        wad.PrimaryOrder,
                        storagePath,
                        file.Sha256));
                }
            }
        }

        RuntimeCandidate[] effectiveCandidates = candidates
            .GroupBy(candidate => candidate.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(candidate => WorkspaceRuntimeCompatibility.CountCwTokenCoverage(
                    candidate.RelativePath,
                    candidate.StoragePath,
                    workspaceCwTokenKeys))
                .ThenBy(candidate => candidate.WadOrder)
                .ThenBy(candidate => candidate.WadName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.PackageName, StringComparer.OrdinalIgnoreCase)
                .Last())
            .OrderBy(candidate => candidate.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        IReadOnlySet<string> availableCwTokenKeys = WorkspaceRuntimeCompatibility.FindAvailableCwTokenKeys(
            effectiveCandidates.Select(candidate => (candidate.RelativePath, candidate.StoragePath)),
            workspaceCwTokenKeys);
        HashSet<string> missingCwTokenKeys = workspaceCwTokenKeys
            .Where(key => !availableCwTokenKeys.Contains(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<WorkspaceMergedRuntimeResource> resources = new(effectiveCandidates.Length);
        foreach (RuntimeCandidate candidate in effectiveCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string hash = string.IsNullOrWhiteSpace(candidate.ManifestSha256)
                ? HashFile(candidate.StoragePath)
                : candidate.ManifestSha256.Trim().ToUpperInvariant();
            resources.Add(new WorkspaceMergedRuntimeResource(
                candidate.RelativePath,
                candidate.PackageName,
                candidate.WadName,
                candidate.WadOrder,
                candidate.StoragePath,
                hash));
        }

        string fingerprint = ComputeFingerprint(resources);
        return new WorkspaceMergedRuntimeCatalogSnapshot(
            sourceMaxOrder,
            resources,
            missingCwTokenKeys,
            fingerprint);
    }

    public static bool IsSharedRuntimeResource(string relativePath)
    {
        string normalized = relativePath.Trim().Replace('/', '\\').TrimStart('\\');
        if (normalized.Length == 0)
            return false;

        if (ForbiddenTrees.Any(tree => StartsWithTree(normalized, tree)))
            return false;
        if (StartsWithTree(normalized, IllustrationTree))
            return false;

        string[] parts = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part.StartsWith(".", StringComparison.Ordinal)))
            return false;

        string extension = Path.GetExtension(normalized);
        return !EditorExtensions.Contains(extension);
    }

    private static string ComputeFingerprint(IEnumerable<WorkspaceMergedRuntimeResource> resources)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (WorkspaceMergedRuntimeResource resource in resources.OrderBy(
                     resource => resource.RelativePath,
                     StringComparer.OrdinalIgnoreCase))
        {
            byte[] line = Encoding.UTF8.GetBytes(
                resource.RelativePath.ToUpperInvariant() + "\0" + resource.Sha256 + "\n");
            hash.AppendData(line);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string HashFile(string path)
    {
        using FileStream input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private static string? GetAllPlatformsRelativePath(string archivePath)
    {
        string normalized = archivePath.Replace('/', '\\');
        const string marker = "DATA_ALL_PLATFORMS\\";
        int index = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? null : normalized[(index + marker.Length)..];
    }

    private static bool StartsWithTree(string path, string tree) =>
        path.Equals(tree, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(tree + "\\", StringComparison.OrdinalIgnoreCase);

    private static string SafeDirectoryName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string safe = new(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return safe.Trim().TrimEnd('.');
    }

    private static void AddWarning(
        ICollection<string> warnings,
        ISet<string> warningKeys,
        string warning)
    {
        if (warningKeys.Add(warning))
            warnings.Add(warning);
    }

    private sealed record RuntimeCandidate(
        string RelativePath,
        string PackageName,
        string WadName,
        int WadOrder,
        string StoragePath,
        string ManifestSha256);
}
