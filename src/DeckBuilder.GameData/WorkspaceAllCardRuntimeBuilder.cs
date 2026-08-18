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
/// Builds one shared runtime WAD for every effective CARD_V2 in an extracted workspace.
/// Cards and normal illustrations are deliberately excluded. Unlike per-deck portable packaging,
/// the shared WAD is intentionally conservative: all common card-logic trees are included in full,
/// while static dependency analysis is used to add concrete assets referenced by cards/functions.
/// </summary>
public sealed class WorkspaceAllCardRuntimeBuilder
{
    private const int CardBatchSize = 48;

    // These trees are small/shared runtime data and frequently contain dynamic lookups that cannot
    // be proven with static analysis (for example creature-type text tables assembled at runtime).
    // A global runtime WAD is built once, so completeness is more important here than per-deck minimality.
    private static readonly string[] SharedRuntimeTrees =
    {
        "FUNCTIONS",
        "SPECS",
        "TEXT_PERMANENT"
    };

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

        List<string> warnings = new();
        HashSet<string> warningKeys = new(StringComparer.OrdinalIgnoreCase);

        Report(progress, 3, "Общий runtime", "Строю эффективный индекс CARD_V2…");
        WorkspaceCardIndex cardIndex = WorkspaceCardIndex.Create(scan);
        WorkspaceContentVariant[] effectiveCards = scan.CardVariants
            .Where(variant => !string.IsNullOrWhiteSpace(variant.Reference))
            .Select(variant => variant.Reference.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(reference => cardIndex.TryResolve(reference, selections: null, out WorkspaceContentVariant variant)
                ? variant
                : null)
            .Where(variant => variant is not null)
            .Cast<WorkspaceContentVariant>()
            .OrderBy(variant => variant.Reference, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (effectiveCards.Length == 0)
            throw new InvalidDataException("The workspace does not contain any effective CARD_V2 definitions.");

        cancellationToken.ThrowIfCancellationRequested();
        Report(progress, 9, "Общий runtime", $"Индексирую runtime workspace для {effectiveCards.Length:N0} CARD_V2…");
        WorkspacePortableRuntimeIndex runtimeIndex = WorkspacePortableRuntimeIndex.Load(
            workspace,
            warnings,
            warningKeys,
            cancellationToken);
        if (runtimeIndex.IsEmpty)
            throw new InvalidDataException("No DATA_ALL_PLATFORMS runtime resources were found in the extracted workspace.");

        Report(progress, 12, "Общий runtime", "Собираю полные FUNCTIONS / SPECS / TEXT_PERMANENT…");
        HashSet<string> sharedRuntimePaths = LoadSharedRuntimePaths(
            workspace,
            warnings,
            warningKeys,
            cancellationToken);

        int functionCount = sharedRuntimePaths.Count(path => StartsWithTree(path, "FUNCTIONS"));
        int specsCount = sharedRuntimePaths.Count(path => StartsWithTree(path, "SPECS"));
        int permanentTextCount = sharedRuntimePaths.Count(path => StartsWithTree(path, "TEXT_PERMANENT"));

        if (functionCount == 0)
            throw new InvalidDataException("The workspace contains no FUNCTIONS runtime tree; a complete shared card runtime cannot be built.");
        if (specsCount == 0)
            throw new InvalidDataException("The workspace contains no SPECS runtime tree; a complete shared card runtime cannot be built.");
        if (permanentTextCount == 0)
        {
            throw new InvalidDataException(
                "The workspace contains no TEXT_PERMANENT runtime resources. " +
                "The shared runtime would be incomplete for mechanics that resolve text dynamically (for example creature-type selection). " +
                "Re-extract/refresh the workspace before building the global runtime WAD.");
        }

        // Every CARD_V2 in the workspace is already a root. Runtime-discovered card references do not
        // need to expand the card closure here, so an empty alias map intentionally disables that path.
        IReadOnlyDictionary<string, string> noCardAliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Full shared logic/text trees are the baseline. Dependency analysis only has to add concrete
        // assets outside those trees (TDX/CNT/BIK/SOUND/etc.). Normal card illustrations stay per-deck.
        HashSet<string> runtimePaths = new(sharedRuntimePaths, StringComparer.OrdinalIgnoreCase);
        int processedCards = 0;

        Report(
            progress,
            15,
            "Общий runtime",
            $"Общие деревья: FUNCTIONS {functionCount:N0}, SPECS {specsCount:N0}, TEXT_PERMANENT {permanentTextCount:N0}. Ищу внешние assets…");

        void ResolveBatch(IReadOnlyList<WorkspaceContentVariant> cards)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                WorkspacePortableRuntimeResolution resolution = runtimeIndex.Resolve(
                    cards.Select(card => card.StoragePath),
                    rootIdentifiers: null,
                    noCardAliases,
                    warnings,
                    warningKeys,
                    cancellationToken);

                foreach (string path in resolution.ResourcePaths)
                {
                    // Illustrations belong to per-deck/card payloads, not to the shared runtime WAD.
                    if (!StartsWithTree(path, "ART_ASSETS\\ILLUSTRATIONS"))
                        runtimePaths.Add(path);
                }

                processedCards += cards.Count;
                int percent = 17 + (int)Math.Round(46d * processedCards / effectiveCards.Length);
                Report(
                    progress,
                    Math.Clamp(percent, 17, 63),
                    "Анализ всех карт",
                    $"{processedCards:N0}/{effectiveCards.Length:N0} CARD_V2; runtime-ресурсов: {runtimePaths.Count:N0}");
            }
            catch (InvalidDataException) when (cards.Count > 1)
            {
                // A mixed batch can exceed the normal per-deck safety ceiling even when each card is
                // individually valid. Split recursively; a single-card overflow is still a real error.
                int middle = cards.Count / 2;
                ResolveBatch(cards.Take(middle).ToArray());
                ResolveBatch(cards.Skip(middle).ToArray());
            }
            catch (InvalidDataException exception) when (cards.Count == 1)
            {
                throw new InvalidDataException(
                    $"Runtime closure for CARD_V2 '{cards[0].Reference}' is abnormal: {exception.Message}",
                    exception);
            }
        }

        for (int offset = 0; offset < effectiveCards.Length; offset += CardBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResolveBatch(effectiveCards.Skip(offset).Take(CardBatchSize).ToArray());
        }

        if (runtimePaths.Count == 0)
            throw new InvalidDataException("No runtime dependencies were discovered from the workspace CARD_V2 definitions.");

        string[] sortedRuntimePaths = runtimePaths
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        IReadOnlyDictionary<string, int> resourceCounts = BuildResourceCounts(sortedRuntimePaths);
        WorkspacePortableRuntimeResolution allRuntime = new(
            sortedRuntimePaths,
            resourceCounts,
            Array.Empty<string>(),
            Array.Empty<string>());

        string staging = Path.Combine(outputDirectory, $".workspace-all-runtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, 68, "Подготовка runtime", $"Копирую {sortedRuntimePaths.Length:N0} runtime-ресурсов…");
            runtimeIndex.CopyIntoStaging(staging, allRuntime, cancellationToken);

            int stagedFileCount = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).Count();
            if (stagedFileCount != sortedRuntimePaths.Length)
            {
                throw new InvalidDataException(
                    $"Shared runtime staging contains {stagedFileCount:N0} files, but {sortedRuntimePaths.Length:N0} effective resources were expected. " +
                    "The workspace runtime index is inconsistent; packaging was stopped.");
            }

            Report(progress, 78, "Сборка общего Runtime WAD", $"Упаковываю {sortedRuntimePaths.Length:N0} ресурсов…");
            UnpackedContentBuildResult wadResult = _wadBuilder.Build(
                new UnpackedContentBuildOptions(
                    staging,
                    output,
                    UnpackedContentKind.PortableCards,
                    order),
                cancellationToken);

            string manifestPath = output + ".runtime.json";
            Report(progress, 96, "Проверка", "WAD проверен. Записываю состав runtime…");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
            {
                formatVersion = 2,
                createdUtc = DateTime.UtcNow,
                workspace,
                wad = output,
                order,
                cardRootCount = effectiveCards.Length,
                sharedRuntimeTrees = SharedRuntimeTrees,
                sharedRuntimeResourceCount = sharedRuntimePaths.Count,
                dependencyResolvedResourceCount = sortedRuntimePaths.Length - sharedRuntimePaths.Count,
                runtimeResourceCount = sortedRuntimePaths.Length,
                runtimeResourceCounts = resourceCounts,
                resources = sortedRuntimePaths,
                warnings
            }, JsonOptions));

            Report(progress, 100, "Готово", $"Общий runtime: {sortedRuntimePaths.Length:N0} ресурсов для {effectiveCards.Length:N0} CARD_V2.");
            return new WorkspaceAllCardRuntimeBuildResult(
                output,
                manifestPath,
                effectiveCards.Length,
                sortedRuntimePaths.Length,
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

    private static HashSet<string> LoadSharedRuntimePaths(
        string workspace,
        ICollection<string> warnings,
        ISet<string> warningKeys,
        CancellationToken cancellationToken)
    {
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
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
                AddWarning(warnings, warningKeys, $"Could not read shared-runtime manifest {manifestPath}: {exception.Message}");
                continue;
            }

            foreach (DotpWadPackageManifest wad in manifest.Wads)
            {
                string wadDirectory = Path.Combine(packageDirectory, "wads", SafeDirectoryName(wad.Name));
                foreach (DotpWadFileManifest file in wad.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string? relativePath = GetAllPlatformsRelativePath(file.ArchivePath);
                    if (relativePath is null || !SharedRuntimeTrees.Any(tree => StartsWithTree(relativePath, tree)))
                        continue;

                    string storagePath = Path.Combine(wadDirectory, file.StoragePath.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(storagePath))
                    {
                        AddWarning(
                            warnings,
                            warningKeys,
                            $"Shared runtime payload {relativePath} from {manifest.VersionName} / {wad.Name} is missing from the workspace.");
                        continue;
                    }

                    paths.Add(relativePath);
                }
            }
        }

        return paths;
    }

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

    private static string? GetAllPlatformsRelativePath(string archivePath)
    {
        string normalized = archivePath.Replace('/', '\\');
        const string marker = "DATA_ALL_PLATFORMS\\";
        int index = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? null : normalized[(index + marker.Length)..];
    }

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

    private static bool StartsWithTree(string path, string tree) =>
        path.Equals(tree, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(tree + "\\", StringComparison.OrdinalIgnoreCase);

    private static void Report(
        IProgress<WorkspaceSelectedCardsProgress>? progress,
        int percent,
        string stage,
        string detail) =>
        progress?.Report(new WorkspaceSelectedCardsProgress(Math.Clamp(percent, 0, 100), stage, detail));
}
