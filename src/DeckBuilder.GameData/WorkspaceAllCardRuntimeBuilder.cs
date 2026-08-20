using System.Security.Cryptography;
using System.Text.Json;

namespace DeckBuilder.GameData;

public sealed record WorkspaceAllCardRuntimeBuildResult(
    string WadPath,
    string ManifestPath,
    int CardRootCount,
    int RuntimeResourceCount,
    IReadOnlyDictionary<string, int> RuntimeResourceCounts,
    UnpackedContentBuildResult BuildResult,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Builds one complete shared runtime WAD for an extracted workspace. Every effective non-card
/// runtime resource is included once. Duplicate paths from multiple source WADs are collapsed by
/// the merged runtime catalog, so per-deck packaging no longer has to guess which mechanics/assets
/// a card might reach dynamically.
/// </summary>
public sealed class WorkspaceAllCardRuntimeBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly UnpackedContentWadBuilder _wadBuilder = new();

    public Task<WorkspaceAllCardRuntimeBuildResult> BuildAsync(
        string outputPath,
        string workspaceDirectory,
        WorkspaceContentVariantScanResult scan,
        int order = 40,
        CancellationToken cancellationToken = default,
        IProgress<WorkspaceSelectedCardsProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceDirectory);
        ArgumentNullException.ThrowIfNull(scan);

        return Task.Run(
            () => Build(outputPath, workspaceDirectory, scan, order, cancellationToken, progress),
            cancellationToken);
    }

    internal WorkspaceAllCardRuntimeBuildResult Build(
        string outputPath,
        string workspaceDirectory,
        WorkspaceContentVariantScanResult scan,
        int order,
        CancellationToken cancellationToken,
        IProgress<WorkspaceSelectedCardsProgress>? progress = null)
    {
        string workspace = Path.GetFullPath(workspaceDirectory);
        if (!Directory.Exists(workspace))
            throw new DirectoryNotFoundException(workspace);

        string output = Path.GetFullPath(outputPath);
        string outputDirectory = Path.GetDirectoryName(output)
            ?? throw new DirectoryNotFoundException("The runtime WAD output directory is missing.");
        Directory.CreateDirectory(outputDirectory);

        int cardRootCount = scan.CardVariants
            .Select(variant => variant.Reference)
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Select(reference => reference.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        if (cardRootCount == 0)
            throw new InvalidDataException("The workspace does not contain any effective CARD_V2 definitions.");

        List<string> warnings = new();
        HashSet<string> warningKeys = new(StringComparer.OrdinalIgnoreCase);

        Report(progress, 4, "Общий runtime", $"Склеиваю полный runtime workspace для {cardRootCount:N0} CARD_V2…");
        WorkspaceMergedRuntimeCatalogSnapshot catalog = WorkspaceMergedRuntimeCatalog.Load(
            workspace,
            warnings,
            warningKeys,
            cancellationToken);
        if (catalog.ResourceCount == 0)
            throw new InvalidDataException("No shared DATA_ALL_PLATFORMS runtime resources were found in the extracted workspace.");
        if (catalog.SourceMaxOrder == int.MaxValue)
            throw new InvalidDataException("The workspace contains a WAD with the maximum possible order; the shared runtime cannot be placed after it safely.");

        if (catalog.MissingCwTokenKeys.Count > 0)
        {
            string shown = string.Join(", ", catalog.MissingCwTokenKeys.Take(20));
            string more = catalog.MissingCwTokenKeys.Count > 20
                ? $" и ещё {catalog.MissingCwTokenKeys.Count - 20:N0}"
                : string.Empty;
            throw new InvalidDataException(
                $"The merged runtime cannot cover CW_Tokens archetypes used by the workspace: {shown}{more}. " +
                "Refresh/re-extract the matching Community WAD runtime before packaging.");
        }

        int effectiveOrder = Math.Max(order, catalog.SourceMaxOrder + 1);
        string[] runtimePaths = catalog.Resources
            .Select(resource => resource.RelativePath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        IReadOnlyDictionary<string, int> resourceCounts = BuildResourceCounts(runtimePaths);

        int functionCount = CountGroup(resourceCounts, "FUNCTIONS");
        int specsCount = CountGroup(resourceCounts, "SPECS");
        int permanentTextCount = CountGroup(resourceCounts, "TEXT_PERMANENT");
        if (functionCount == 0)
            throw new InvalidDataException("The workspace contains no FUNCTIONS runtime tree; a complete shared runtime cannot be built.");
        if (specsCount == 0)
            throw new InvalidDataException("The workspace contains no SPECS runtime tree; a complete shared runtime cannot be built.");
        if (permanentTextCount == 0)
            throw new InvalidDataException("The workspace contains no TEXT_PERMANENT runtime tree; a complete shared runtime cannot be built.");

        Report(
            progress,
            18,
            "Полный merged runtime",
            $"Эффективных ресурсов: {catalog.ResourceCount:N0}. FUNCTIONS {functionCount:N0}, SPECS {specsCount:N0}, TEXT_PERMANENT {permanentTextCount:N0}.");

        string staging = Path.Combine(outputDirectory, $".workspace-full-runtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            int copied = 0;
            foreach (WorkspaceMergedRuntimeResource resource in catalog.Resources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string target = Path.Combine(
                    staging,
                    "DATA_ALL_PLATFORMS",
                    resource.RelativePath.Replace('\\', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(resource.StoragePath, target, overwrite: true);

                copied++;
                if (copied % 128 == 0 || copied == catalog.ResourceCount)
                {
                    int percent = 18 + (int)Math.Round(52d * copied / catalog.ResourceCount);
                    Report(
                        progress,
                        Math.Clamp(percent, 18, 70),
                        "Копирование полного runtime",
                        $"{copied:N0}/{catalog.ResourceCount:N0} ресурсов…");
                }
            }

            int stagedFileCount = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).Count();
            if (stagedFileCount != catalog.ResourceCount)
            {
                throw new InvalidDataException(
                    $"Merged runtime staging contains {stagedFileCount:N0} files, but {catalog.ResourceCount:N0} effective resources were expected.");
            }

            Report(
                progress,
                74,
                "Сборка полного Runtime WAD",
                $"Упаковываю {catalog.ResourceCount:N0} склеенных ресурсов с order {effectiveOrder}…");
            UnpackedContentBuildResult wadResult = _wadBuilder.Build(
                new UnpackedContentBuildOptions(
                    staging,
                    output,
                    UnpackedContentKind.PortableCards,
                    effectiveOrder),
                cancellationToken);

            string wadSha256 = HashFile(output);
            string manifestPath = output + ".runtime.json";
            Report(progress, 95, "Проверка", "WAD проверен. Записываю fingerprint полного runtime…");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
            {
                formatVersion = WorkspaceSharedRuntimeContract.ManifestFormatVersion,
                coverageMode = WorkspaceMergedRuntimeCatalog.CoverageMode,
                createdUtc = DateTime.UtcNow,
                workspace,
                wad = output,
                wadSha256,
                workspaceRuntimeFingerprint = catalog.Fingerprint,
                requestedOrder = order,
                sourceMaxOrder = catalog.SourceMaxOrder,
                order = effectiveOrder,
                cardRootCount,
                sharedRuntimeTrees = new[] { "FUNCTIONS", "SPECS", "TEXT_PERMANENT", "ALL_SHARED_RUNTIME" },
                runtimeResourceCount = catalog.ResourceCount,
                runtimeResourceCounts = resourceCounts,
                excludedTrees = new[] { "CARDS", "DECKS", "UNLOCKS", "ART_ASSETS\\ILLUSTRATIONS" },
                resources = runtimePaths,
                warnings
            }, JsonOptions));

            Report(
                progress,
                100,
                "Готово",
                $"Полный merged runtime: {catalog.ResourceCount:N0} ресурсов для {cardRootCount:N0} CARD_V2; order {effectiveOrder}.");
            return new WorkspaceAllCardRuntimeBuildResult(
                output,
                manifestPath,
                cardRootCount,
                catalog.ResourceCount,
                resourceCounts,
                wadResult,
                warnings);
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }

    private static int CountGroup(IReadOnlyDictionary<string, int> counts, string key) =>
        counts.TryGetValue(key, out int count) ? count : 0;

    private static IReadOnlyDictionary<string, int> BuildResourceCounts(IEnumerable<string> paths) =>
        paths.GroupBy(GetResourceGroup, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

    private static string GetResourceGroup(string relativePath)
    {
        string[] parts = relativePath.Replace('/', '\\')
            .Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return "OTHER";

        if (parts[0].Equals("ART_ASSETS", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length >= 3 && parts[1].Equals("TEXTURES", StringComparison.OrdinalIgnoreCase))
                return $"ART_ASSETS\\TEXTURES\\{parts[2]}";
            if (parts.Length >= 2)
                return $"ART_ASSETS\\{parts[1]}";
        }

        return parts[0];
    }

    private static string HashFile(string path)
    {
        using FileStream input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private static void Report(
        IProgress<WorkspaceSelectedCardsProgress>? progress,
        int percent,
        string stage,
        string detail) =>
        progress?.Report(new WorkspaceSelectedCardsProgress(Math.Clamp(percent, 0, 100), stage, detail));
}
