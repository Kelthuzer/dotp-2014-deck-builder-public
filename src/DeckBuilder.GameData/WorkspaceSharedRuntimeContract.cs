using System.Text.Json;

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
        string manifestPath = wad + ".runtime.json";
        if (!File.Exists(wad) || new FileInfo(wad).Length == 0)
            return Invalid("общий runtime WAD отсутствует или пуст");
        if (!File.Exists(manifestPath))
            return Invalid("рядом с общим runtime WAD нет .runtime.json manifest");

        int currentSourceMaxOrder;
        try
        {
            currentSourceMaxOrder = FindHighestWorkspaceWadOrder(workspaceDirectory, cancellationToken);
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
                .Select(value => value!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (resources.Count == 0)
                return Invalid("список resources общего runtime пуст");
            if (declaredResourceCount != resources.Count)
            {
                return Invalid(
                    $"manifest общего runtime объявляет {declaredResourceCount:N0} ресурсов, но перечисляет {resources.Count:N0}");
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
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        GameVersionPackageService packageService = new();
        foreach (string manifestPath in manifests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DotpVersionPackageManifest manifest = packageService.ReadManifest(Path.GetDirectoryName(manifestPath)!);
            foreach (DotpWadPackageManifest wad in manifest.Wads)
                maxOrder = Math.Max(maxOrder, wad.PrimaryOrder);
        }

        return maxOrder;
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

    private static string NormalizeResourcePath(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().Replace('/', '\\');

    private static WorkspaceSharedRuntimeInspection Invalid(string reason) => new(null, reason);
}
