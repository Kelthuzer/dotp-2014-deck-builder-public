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
/// Cards and illustrations are deliberately excluded: this archive contains only the runtime
/// reached by card definitions (LOL/functions, specs, permanent text and concrete runtime assets).
/// </summary>
public sealed class WorkspaceAllCardRuntimeBuilder
{
    private const int CardBatchSize = 48;
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
        Report(progress, 10, "Общий runtime", $"Индексирую runtime workspace для {effectiveCards.Length:N0} CARD_V2…");
        WorkspacePortableRuntimeIndex runtimeIndex = WorkspacePortableRuntimeIndex.Load(
            workspace,
            warnings,
            warningKeys,
            cancellationToken);
        if (runtimeIndex.IsEmpty)
            throw new InvalidDataException("No DATA_ALL_PLATFORMS runtime resources were found in the extracted workspace.");

        // Every CARD_V2 in the workspace is already a root. Runtime-discovered card references do not
        // need to expand the card closure here, so an empty alias map intentionally disables that path.
        IReadOnlyDictionary<string, string> noCardAliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> runtimePaths = new(StringComparer.OrdinalIgnoreCase);
        int processedCards = 0;

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
                int percent = 15 + (int)Math.Round(48d * processedCards / effectiveCards.Length);
                Report(
                    progress,
                    Math.Clamp(percent, 15, 63),
                    "Анализ всех карт",
                    $"{processedCards:N0}/{effectiveCards.Length:N0} CARD_V2; уникальных runtime-ресурсов: {runtimePaths.Count:N0}");
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
                formatVersion = 1,
                createdUtc = DateTime.UtcNow,
                workspace,
                wad = output,
                order,
                cardRootCount = effectiveCards.Length,
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
