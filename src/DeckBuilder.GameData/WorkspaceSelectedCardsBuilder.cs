using System.Text.Json;

namespace DeckBuilder.GameData;

public sealed record WorkspaceSelectedCardSource(
    string Reference,
    string PackageName,
    string WadName,
    int WadOrder,
    string CardPath,
    string CardSha256,
    string? ArtId,
    string? ArtPath,
    string? ArtSha256);

public sealed record WorkspaceSelectedCardsBuildResult(
    string WadPath,
    string SourcesPath,
    int CardCount,
    int ArtCount,
    int RuntimeResourceCount,
    string SharedRuntimeWadPath,
    int SharedRuntimeResourceCount,
    UnpackedContentBuildResult BuildResult,
    IReadOnlyList<string> Warnings);

public sealed record WorkspaceSelectedCardsProgress(
    int Percent,
    string Stage,
    string Detail);

/// <summary>
/// Builds the per-deck support WAD: selected CARD_V2 files, recursive card/token dependencies,
/// illustrations and deck-specific assets. Shared card logic is supplied by one verified
/// Data_DLC_8000_DeckBuilder_Runtime.wad beside the packaged deck instead of being duplicated.
/// </summary>
public sealed class WorkspaceSelectedCardsBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly UnpackedContentWadBuilder _wadBuilder = new();

    public Task<WorkspaceSelectedCardsBuildResult> BuildAsync(
        string outputPath,
        IEnumerable<string> references,
        WorkspaceContentVariantScanResult scan,
        IReadOnlyDictionary<string, string>? selections,
        string? workspaceDirectory = null,
        string? deckBoxImageId = null,
        string? deckBoxTexturePath = null,
        IEnumerable<string>? runtimeRootIdentifiers = null,
        int order = 50,
        CancellationToken cancellationToken = default,
        IProgress<WorkspaceSelectedCardsProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(references);
        ArgumentNullException.ThrowIfNull(scan);

        // Keep indexing, dependency expansion, compression and validation off the WPF UI thread.
        return Task.Run(
            () => Build(
                outputPath,
                references,
                scan,
                selections,
                workspaceDirectory,
                deckBoxImageId,
                deckBoxTexturePath,
                runtimeRootIdentifiers,
                order,
                cancellationToken,
                progress),
            cancellationToken);
    }

    internal WorkspaceSelectedCardsBuildResult Build(
        string outputPath,
        IEnumerable<string> references,
        WorkspaceContentVariantScanResult scan,
        IReadOnlyDictionary<string, string>? selections,
        string? workspaceDirectory,
        string? deckBoxImageId,
        string? deckBoxTexturePath,
        IEnumerable<string>? runtimeRootIdentifiers,
        int order,
        CancellationToken cancellationToken,
        IProgress<WorkspaceSelectedCardsProgress>? progress = null)
    {
        Report(progress, 2, "Подготовка", "Проверяю список карт и папку назначения…");
        string[] rootReferences = NormalizeReferences(references);
        if (rootReferences.Length == 0)
            throw new InvalidDataException("No CARD_V2 references were supplied for portable packaging.");
        if (string.IsNullOrWhiteSpace(workspaceDirectory) || !Directory.Exists(workspaceDirectory))
            throw new DirectoryNotFoundException("A valid extracted workspace is required for shared-runtime packaging.");

        string workspace = Path.GetFullPath(workspaceDirectory);
        string output = Path.GetFullPath(outputPath);
        string outputDirectory = Path.GetDirectoryName(output)
            ?? throw new DirectoryNotFoundException("The support WAD output directory is missing.");
        Directory.CreateDirectory(outputDirectory);

        cancellationToken.ThrowIfCancellationRequested();
        Report(progress, 7, "Индекс карт", $"Индексирую CARD_V2: {scan.CardVariants.Count:N0} вариантов…");
        WorkspaceCardIndex cardIndex = WorkspaceCardIndex.Create(scan);
        HashSet<string> rootReferenceSet = rootReferences.ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<string> warnings = new();
        HashSet<string> warningKeys = new(StringComparer.OrdinalIgnoreCase);

        Report(progress, 12, "Индекс runtime", "Читаю FUNCTIONS, SPECS, TEXT и связанные ресурсы workspace…");
        WorkspacePortableRuntimeIndex runtimeIndex = WorkspacePortableRuntimeIndex.Load(
            workspace,
            scan,
            warnings,
            warningKeys,
            cancellationToken);
        Report(progress, 22, "Индекс runtime", "Runtime-индекс готов. Начинаю замыкание зависимостей карт…");

        string? requiredDeckTexture = BuildDeckTextureResourcePath(deckBoxImageId, deckBoxTexturePath);
        string[] runtimeRoots = (runtimeRootIdentifiers ?? Array.Empty<string>())
            .Append(requiredDeckTexture)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string staging = Path.Combine(outputDirectory, $".workspace-cards-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            string cardDirectory = Path.Combine(staging, "DATA_ALL_PLATFORMS", "CARDS");
            string artDirectory = Path.Combine(staging, "DATA_ALL_PLATFORMS", "ART_ASSETS", "ILLUSTRATIONS");
            Directory.CreateDirectory(cardDirectory);
            Directory.CreateDirectory(artDirectory);

            List<WorkspaceSelectedCardSource> sources = new();
            List<string> cardXmlPaths = new();
            HashSet<string> copiedArt = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> scheduledReferences = new(StringComparer.OrdinalIgnoreCase);
            Queue<CardRequest> pendingCards = new();

            void QueueCard(string reference, string? requestedBy)
            {
                if (string.IsNullOrWhiteSpace(reference))
                    return;

                string normalized = reference.Trim();
                if (scheduledReferences.Add(normalized))
                    pendingCards.Enqueue(new CardRequest(normalized, requestedBy));
            }

            foreach (string reference in rootReferences)
                QueueCard(reference, null);

            WorkspacePortableRuntimeResolution runtime = WorkspacePortableRuntimeResolution.Empty;
            int closurePass = 0;
            while (true)
            {
                closurePass++;
                while (pendingCards.TryDequeue(out CardRequest? request))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int knownTotal = Math.Max(scheduledReferences.Count, rootReferences.Length);
                    int cardPercent = 24 + Math.Min(22, (int)Math.Round(22d * sources.Count / Math.Max(1, knownTotal)));
                    Report(
                        progress,
                        cardPercent,
                        "Карты и токены",
                        $"{sources.Count:N0}/{knownTotal:N0}: {request.Reference}");

                    if (!cardIndex.TryResolve(request.Reference, selections, out WorkspaceContentVariant selected))
                    {
                        Warn(
                            warnings,
                            warningKeys,
                            request.RequestedBy is null
                                ? $"No extracted CARD_V2 definition was found for {request.Reference}; the deck keeps the exact reference."
                                : $"Referenced CARD_V2 dependency {request.Reference} required by {request.RequestedBy} was not found in the workspace.");
                        continue;
                    }

                    string canonicalReference = string.IsNullOrWhiteSpace(selected.Reference)
                        ? request.Reference
                        : selected.Reference.Trim();
                    string extension = Path.GetExtension(selected.RelativePath);
                    string cardFileName = SanitizeFileName(canonicalReference)
                        + (string.IsNullOrWhiteSpace(extension) ? ".XML" : extension);
                    File.Copy(selected.StoragePath, Path.Combine(cardDirectory, cardFileName), overwrite: true);
                    cardXmlPaths.Add(selected.StoragePath);

                    CopyIllustration(selected, artDirectory, copiedArt);
                    sources.Add(ToSource(canonicalReference, selected));

                    string rawXml;
                    try
                    {
                        rawXml = File.ReadAllText(selected.StoragePath);
                    }
                    catch (Exception exception)
                    {
                        Warn(
                            warnings,
                            warningKeys,
                            $"Could not inspect CARD_V2 dependencies for {canonicalReference}: {exception.Message}");
                        continue;
                    }

                    WorkspaceCardDependencyScanResult dependencyScan = WorkspaceCardDependencyResolver.Scan(
                        rawXml,
                        cardIndex.Aliases,
                        canonicalReference);
                    foreach (string missingToken in dependencyScan.MissingTokenReferences)
                    {
                        Warn(
                            warnings,
                            warningKeys,
                            $"Token dependency {missingToken} referenced by {canonicalReference} was not found in the workspace.");
                    }

                    foreach (string dependency in dependencyScan.References)
                        QueueCard(dependency, canonicalReference);
                }

                Report(
                    progress,
                    49,
                    "Runtime-зависимости",
                    $"Проход {closurePass}: анализирую {cardXmlPaths.Count:N0} CARD_V2 и общие функции…");

                // Runtime may name another CARD_V2 by FILENAME, ARTID or MULTIVERSEID. Re-resolve
                // only after the card queue is empty; the expensive workspace index is reused.
                runtime = runtimeIndex.Resolve(
                    cardXmlPaths,
                    runtimeRoots,
                    cardIndex.Aliases,
                    warnings,
                    warningKeys,
                    cancellationToken);

                bool addedRuntimeCard = false;
                foreach (string dependency in runtime.CardReferences)
                {
                    if (scheduledReferences.Add(dependency))
                    {
                        pendingCards.Enqueue(new CardRequest(dependency, "portable runtime"));
                        addedRuntimeCard = true;
                    }
                }

                Report(
                    progress,
                    58,
                    "Runtime-зависимости",
                    $"Найдено runtime-ресурсов: {runtime.ResourceCount:N0}; CARD_V2 через runtime: {runtime.CardReferences.Count:N0}.");

                if (!addedRuntimeCard)
                    break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            WorkspacePortableRuntimeResolution cardRuntime = runtimeIndex.Resolve(
                cardXmlPaths,
                rootIdentifiers: null,
                cardIndex.Aliases,
                warnings,
                warningKeys,
                cancellationToken);

            WorkspaceSharedRuntimeSnapshot sharedRuntime = EnsureSharedRuntime(
                outputDirectory,
                workspace,
                scan,
                cancellationToken,
                progress,
                forceRebuild: false);
            string[] missingSharedResources = WorkspaceSharedRuntimeContract.MissingResources(
                sharedRuntime,
                cardRuntime.ResourcePaths);
            if (missingSharedResources.Length > 0)
            {
                Report(
                    progress,
                    61,
                    "Общий runtime",
                    $"В существующем runtime не хватает {missingSharedResources.Length:N0} ресурсов этой колоды; пересобираю его…");
                sharedRuntime = EnsureSharedRuntime(
                    outputDirectory,
                    workspace,
                    scan,
                    cancellationToken,
                    progress,
                    forceRebuild: true);
                missingSharedResources = WorkspaceSharedRuntimeContract.MissingResources(
                    sharedRuntime,
                    cardRuntime.ResourcePaths);
            }

            if (missingSharedResources.Length > 0)
            {
                string shown = string.Join(", ", missingSharedResources.Take(12));
                string more = missingSharedResources.Length > 12
                    ? $" и ещё {missingSharedResources.Length - 12:N0}"
                    : string.Empty;
                throw new InvalidDataException(
                    $"Shared runtime is incomplete even after rebuild. Missing resources: {shown}{more}. " +
                    "Packaging stopped instead of creating a deck with silently broken mechanics.");
            }

            HashSet<string> cardRuntimePaths = cardRuntime.ResourcePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            string[] packagedRuntimePaths = runtime.ResourcePaths
                .Where(path => !cardRuntimePaths.Contains(path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            WorkspacePortableRuntimeResolution packagedRuntime = new(
                packagedRuntimePaths,
                BuildResourceCounts(packagedRuntimePaths),
                runtime.CardReferences,
                runtime.MissingRootIdentifiers);

            Report(
                progress,
                74,
                "Подготовка ресурсов колоды",
                $"Общая механика: {cardRuntime.ResourceCount:N0} ресурсов в shared runtime; " +
                $"в WAD колоды добавляю только {packagedRuntime.ResourceCount:N0} специфичных ресурсов.");
            ValidateRequiredRuntimeRoots(requiredDeckTexture, runtime, warnings, warningKeys);
            runtimeIndex.CopyIntoStaging(staging, packagedRuntime, cancellationToken);
            StageCustomDeckTexture(staging, deckBoxImageId, deckBoxTexturePath);

            int packagedRootCards = sources.Count(source => rootReferenceSet.Contains(source.Reference));
            if (packagedRootCards == 0)
                throw new InvalidDataException("None of the deck's CARD_V2 definitions could be packaged.");
            if (sharedRuntime.Order == int.MaxValue)
                throw new InvalidDataException("Shared runtime uses the maximum WAD order; the deck payload cannot be placed after it safely.");
            int effectiveOrder = Math.Max(order, sharedRuntime.Order + 1);

            cancellationToken.ThrowIfCancellationRequested();
            Report(
                progress,
                82,
                "Сборка Cards/art WAD",
                $"Упаковываю {sources.Count:N0} CARD_V2, {copiedArt.Count:N0} иллюстраций и " +
                $"{packagedRuntime.ResourceCount:N0} специфичных ресурсов с order {effectiveOrder}…");
            UnpackedContentBuildResult wadResult = _wadBuilder.Build(
                new UnpackedContentBuildOptions(
                    staging,
                    output,
                    UnpackedContentKind.PortableCards,
                    effectiveOrder),
                cancellationToken);

            Report(progress, 94, "Проверка Cards/art WAD", $"WAD собран: {wadResult.FileCount:N0} файлов. Записываю provenance…");
            int sharedFunctionCount = cardRuntime.ResourceCounts.TryGetValue("FUNCTIONS", out int functionCount)
                ? functionCount
                : 0;
            string sourcesPath = output + ".sources.json";
            File.WriteAllText(sourcesPath, JsonSerializer.Serialize(new
            {
                formatVersion = 4,
                createdUtc = DateTime.UtcNow,
                wad = output,
                requestedOrder = order,
                order = effectiveOrder,
                deckBoxImage = string.IsNullOrWhiteSpace(deckBoxImageId) ? null : deckBoxImageId,
                customDeckBoxTexture = string.IsNullOrWhiteSpace(deckBoxTexturePath) ? null : Path.GetFullPath(deckBoxTexturePath),
                rootCards = rootReferences,
                dependencyCardCount = Math.Max(0, sources.Count - packagedRootCards),
                sharedFunctionCount,
                sharedRuntime = new
                {
                    wad = sharedRuntime.WadPath,
                    manifest = sharedRuntime.ManifestPath,
                    order = sharedRuntime.Order,
                    totalResourceCount = sharedRuntime.ResourceCount,
                    requiredResourceCount = cardRuntime.ResourceCount
                },
                runtimeRootIdentifiers = runtimeRoots,
                resolvedRuntimeResourceCount = runtime.ResourceCount,
                runtimeResourceCount = packagedRuntime.ResourceCount,
                runtimeResourceCounts = packagedRuntime.ResourceCounts,
                runtimeCardReferences = runtime.CardReferences,
                missingRuntimeRootIdentifiers = runtime.MissingRootIdentifiers,
                cards = sources
            }, JsonOptions));

            Report(progress, 96, "Cards/art WAD готов", $"{wadResult.FileCount:N0} файлов проверено; перехожу к Deck WAD и CPE.");
            return new WorkspaceSelectedCardsBuildResult(
                output,
                sourcesPath,
                sources.Count,
                copiedArt.Count,
                packagedRuntime.ResourceCount,
                sharedRuntime.WadPath,
                cardRuntime.ResourceCount,
                wadResult,
                warnings);
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }

    private static WorkspaceSharedRuntimeSnapshot EnsureSharedRuntime(
        string outputDirectory,
        string workspace,
        WorkspaceContentVariantScanResult scan,
        CancellationToken cancellationToken,
        IProgress<WorkspaceSelectedCardsProgress>? progress,
        bool forceRebuild)
    {
        string runtimePath = Path.Combine(outputDirectory, WorkspaceSharedRuntimeContract.WadFileName);
        WorkspaceSharedRuntimeInspection inspection = WorkspaceSharedRuntimeContract.Inspect(
            runtimePath,
            workspace,
            scan,
            cancellationToken);
        if (!forceRebuild && inspection.IsUsable)
        {
            Report(progress, 61, "Общий runtime", $"Проверен существующий shared runtime: {inspection.Runtime!.ResourceCount:N0} ресурсов.");
            return inspection.Runtime!;
        }

        string reason = forceRebuild ? "принудительное обновление" : inspection.Reason;
        Report(progress, 60, "Общий runtime", $"{reason}. Собираю единый runtime для текущего workspace…");
        IProgress<WorkspaceSelectedCardsProgress>? runtimeProgress = progress is null
            ? null
            : new ScaledProgress(progress, 60, 72);
        new WorkspaceAllCardRuntimeBuilder().Build(
            runtimePath,
            workspace,
            scan,
            order: 40,
            cancellationToken,
            runtimeProgress);

        WorkspaceSharedRuntimeInspection rebuilt = WorkspaceSharedRuntimeContract.Inspect(
            runtimePath,
            workspace,
            scan,
            cancellationToken);
        if (!rebuilt.IsUsable)
        {
            throw new InvalidDataException(
                $"Shared runtime failed post-build validation: {rebuilt.Reason}");
        }

        return rebuilt.Runtime!;
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

    private static void Report(
        IProgress<WorkspaceSelectedCardsProgress>? progress,
        int percent,
        string stage,
        string detail) =>
        progress?.Report(new WorkspaceSelectedCardsProgress(Math.Clamp(percent, 0, 100), stage, detail));

    private static string[] NormalizeReferences(IEnumerable<string> references) =>
        references
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static WorkspaceSelectedCardSource ToSource(
        string canonicalReference,
        WorkspaceContentVariant selected) => new(
        canonicalReference,
        selected.PackageName,
        selected.WadName,
        selected.WadOrder,
        selected.StoragePath,
        selected.Sha256,
        string.IsNullOrWhiteSpace(selected.ArtId) ? null : selected.ArtId,
        selected.ArtStoragePath,
        selected.ArtSha256);

    private static void CopyIllustration(
        WorkspaceContentVariant selected,
        string artDirectory,
        ISet<string> copiedArt)
    {
        if (string.IsNullOrWhiteSpace(selected.ArtStoragePath)
            || !File.Exists(selected.ArtStoragePath)
            || string.IsNullOrWhiteSpace(selected.ArtId))
        {
            return;
        }

        string artFileName = SanitizeFileName(selected.ArtId) + ".TDX";
        if (copiedArt.Add(artFileName))
            File.Copy(selected.ArtStoragePath, Path.Combine(artDirectory, artFileName), overwrite: true);
    }

    private static string? BuildDeckTextureResourcePath(string? deckBoxImageId, string? customTexturePath)
    {
        if (!string.IsNullOrWhiteSpace(customTexturePath) || string.IsNullOrWhiteSpace(deckBoxImageId))
            return null;

        string imageId = Path.GetFileNameWithoutExtension(deckBoxImageId.Trim());
        return $"ART_ASSETS\\TEXTURES\\DECKS\\{imageId}.TDX";
    }

    private static void ValidateRequiredRuntimeRoots(
        string? requiredDeckTexture,
        WorkspacePortableRuntimeResolution runtime,
        ICollection<string> warnings,
        ISet<string> warningKeys)
    {
        foreach (string missing in runtime.MissingRootIdentifiers)
        {
            if (requiredDeckTexture is not null
                && missing.Equals(requiredDeckTexture, StringComparison.OrdinalIgnoreCase))
            {
                throw new FileNotFoundException(
                    $"Deck texture '{Path.GetFileNameWithoutExtension(requiredDeckTexture)}' was not found in the effective workspace runtime.");
            }

            Warn(warnings, warningKeys, $"Requested portable runtime resource {missing} was not found in the workspace.");
        }
    }

    private static void StageCustomDeckTexture(
        string staging,
        string? deckBoxImageId,
        string? deckBoxTexturePath)
    {
        if (string.IsNullOrWhiteSpace(deckBoxTexturePath))
            return;
        if (string.IsNullOrWhiteSpace(deckBoxImageId))
            throw new InvalidDataException("A custom deck texture requires a deck-box image id.");

        string texture = Path.GetFullPath(deckBoxTexturePath);
        if (!File.Exists(texture))
            throw new FileNotFoundException("The generated custom deck-cover TDX was not found.", texture);

        string targetDirectory = Path.Combine(
            staging,
            "DATA_ALL_PLATFORMS",
            "ART_ASSETS",
            "TEXTURES",
            "DECKS");
        Directory.CreateDirectory(targetDirectory);
        File.Copy(
            texture,
            Path.Combine(targetDirectory, SanitizeFileName(Path.GetFileNameWithoutExtension(deckBoxImageId)) + ".TDX"),
            overwrite: true);
    }

    private static void Warn(ICollection<string> warnings, ISet<string> warningKeys, string message)
    {
        if (warningKeys.Add(message))
            warnings.Add(message);
    }

    private static string SanitizeFileName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string safe = new(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "CARD" : safe.Trim();
    }

    private sealed class ScaledProgress(
        IProgress<WorkspaceSelectedCardsProgress> target,
        int startPercent,
        int endPercent) : IProgress<WorkspaceSelectedCardsProgress>
    {
        public void Report(WorkspaceSelectedCardsProgress value)
        {
            int mapped = startPercent + (int)Math.Round(
                (endPercent - startPercent) * Math.Clamp(value.Percent, 0, 100) / 100d);
            target.Report(value with { Percent = Math.Clamp(mapped, startPercent, endPercent) });
        }
    }

    private sealed record CardRequest(string Reference, string? RequestedBy);
}
