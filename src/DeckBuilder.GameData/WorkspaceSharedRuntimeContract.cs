using System.Security.Cryptography;
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
/// Contract for the one-per-workspace complete merged runtime. A usable runtime must represent
/// exactly the current effective non-card workspace resources and must match the WAD produced by
/// the builder byte-for-byte. Older dependency-pruned runtime manifests are intentionally rejected.
/// </summary>
internal static class WorkspaceSharedRuntimeContract
{
    public const string WadFileName = "Data_DLC_8000_DeckBuilder_Runtime.wad";
    public const int ManifestFormatVersion = 4;

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

        int currentCardRootCount = scan.CardVariants
            .Select(variant => variant.Reference)
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Select(reference => reference.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        WorkspaceMergedRuntimeCatalogSnapshot catalog;
        try
        {
            List<string> warnings = new();
            HashSet<string> warningKeys = new(StringComparer.OrdinalIgnoreCase);
            catalog = WorkspaceMergedRuntimeCatalog.Load(
                workspace,
                warnings,
                warningKeys,
                cancellationToken);
        }
        catch (Exception exception)
        {
            return Invalid($"не удалось построить текущий merged runtime catalog: {exception.Message}");
        }

        if (catalog.MissingCwTokenKeys.Count > 0)
        {
            return Invalid(
                $"текущий workspace не содержит совместимый CW_TOKENS runtime для: " +
                string.Join(", ", catalog.MissingCwTokenKeys.Take(12)));
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            JsonElement root = document.RootElement;

            int formatVersion = RequiredInt(root, "formatVersion");
            if (formatVersion != ManifestFormatVersion)
            {
                return Invalid(
                    $"manifest общего runtime имеет формат {formatVersion}, ожидается {ManifestFormatVersion}; требуется полный merged runtime");
            }

            string coverageMode = RequiredString(root, "coverageMode");
            if (!coverageMode.Equals(WorkspaceMergedRuntimeCatalog.CoverageMode, StringComparison.Ordinal))
            {
                return Invalid(
                    $"runtime coverage mode '{coverageMode}' устарел; ожидается '{WorkspaceMergedRuntimeCatalog.CoverageMode}'");
            }

            int sourceMaxOrder = RequiredInt(root, "sourceMaxOrder");
            int order = RequiredInt(root, "order");
            int cardRootCount = RequiredInt(root, "cardRootCount");
            int declaredResourceCount = RequiredInt(root, "runtimeResourceCount");
            string declaredFingerprint = RequiredString(root, "workspaceRuntimeFingerprint");
            string declaredWadSha256 = RequiredString(root, "wadSha256");

            if (sourceMaxOrder != catalog.SourceMaxOrder)
            {
                return Invalid(
                    $"runtime собран для sourceMaxOrder {sourceMaxOrder}, а текущий workspace требует {catalog.SourceMaxOrder}");
            }

            if (order <= catalog.SourceMaxOrder)
            {
                return Invalid(
                    $"runtime order {order} не загружается после исходных WAD (максимум {catalog.SourceMaxOrder})");
            }

            if (cardRootCount != currentCardRootCount)
            {
                return Invalid(
                    $"runtime рассчитан на {cardRootCount:N0} CARD_V2, а в текущем workspace {currentCardRootCount:N0}");
            }

            if (declaredResourceCount != catalog.ResourceCount)
            {
                return Invalid(
                    $"runtime содержит {declaredResourceCount:N0} ресурсов, а полный merged catalog требует {catalog.ResourceCount:N0}");
            }

            if (!declaredFingerprint.Equals(catalog.Fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return Invalid(
                    "fingerprint runtime отличается от текущего workspace; общий runtime будет пересобран");
            }

            if (!root.TryGetProperty("resources", out JsonElement resourcesElement)
                || resourcesElement.ValueKind != JsonValueKind.Array)
            {
                return Invalid("manifest общего runtime не содержит список resources");
            }

            HashSet<string> resources = resourcesElement.EnumerateArray()
                .Where(element => element.ValueKind == JsonValueKind.String)
                .Select(element => NormalizeResourcePath(element.GetString()))
                .Where(value => value.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            HashSet<string> expectedResources = catalog.Resources
                .Select(resource => NormalizeResourcePath(resource.RelativePath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!resources.SetEquals(expectedResources))
            {
                string[] missing = expectedResources.Where(resource => !resources.Contains(resource)).Take(8).ToArray();
                string[] extra = resources.Where(resource => !expectedResources.Contains(resource)).Take(8).ToArray();
                string details = string.Empty;
                if (missing.Length > 0)
                    details += $" отсутствуют: {string.Join(", ", missing)};";
                if (extra.Length > 0)
                    details += $" лишние: {string.Join(", ", extra)};";
                return Invalid($"manifest runtime не совпадает с полным merged catalog;{details}");
            }

            string actualWadSha256 = HashFile(wad);
            if (!actualWadSha256.Equals(declaredWadSha256, StringComparison.OrdinalIgnoreCase))
                return Invalid("общий runtime WAD изменён или повреждён после сборки");

            return new WorkspaceSharedRuntimeInspection(
                new WorkspaceSharedRuntimeSnapshot(
                    wad,
                    manifestPath,
                    sourceMaxOrder,
                    order,
                    cardRootCount,
                    resources),
                "полный merged runtime подходит текущему workspace");
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

    private static string RequiredString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement element)
            || element.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(element.GetString()))
        {
            throw new InvalidDataException($"Required string property '{propertyName}' is missing.");
        }

        return element.GetString()!;
    }

    private static string HashFile(string path)
    {
        using FileStream input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private static string NormalizeResourcePath(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().Replace('/', '\\');

    private static WorkspaceSharedRuntimeInspection Invalid(string reason) => new(null, reason);
}
