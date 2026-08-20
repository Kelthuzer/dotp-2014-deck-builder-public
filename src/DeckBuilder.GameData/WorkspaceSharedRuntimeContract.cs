using System.Text.Json;
using System.Xml.Linq;

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
/// Contract for the runtime that supplies shared non-card resources while packaging a deck.
/// A normal extracted workspace can still use the generated complete merged runtime. When the
/// target installation is a single-version XMAS game with its native unpacked
/// DATA_DLC_DECK_BUILDER_CUSTOM directory, that already-loaded game runtime is authoritative and
/// must not be shadowed by a generated Data_DLC_8000 runtime.
/// </summary>
internal static class WorkspaceSharedRuntimeContract
{
    public const string WadFileName = "Data_DLC_8000_DeckBuilder_Runtime.wad";
    public const int ManifestFormatVersion = 4;
    private const string NativeRuntimeDirectoryName = "DATA_DLC_DECK_BUILDER_CUSTOM";

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

        WorkspaceSharedRuntimeInspection? nativeRuntime = TryInspectNativeGameRuntime(
            wad,
            workspace,
            scan,
            cancellationToken);
        if (nativeRuntime is not null)
            return nativeRuntime;

        string manifestPath = wad + ".runtime.json";
        if (!File.Exists(wad) || new FileInfo(wad).Length == 0)
            return Invalid("общий runtime WAD отсутствует или пуст");
        if (!File.Exists(manifestPath))
            return Invalid("рядом с общим runtime WAD нет .runtime.json manifest");

        int currentCardRootCount = CountCardRoots(scan);

        WorkspaceMergedRuntimeCatalogSnapshot catalog;
        try
        {
            catalog = LoadCatalog(workspace, scan, cancellationToken);
        }
        catch (Exception exception)
        {
            return Invalid($"не удалось построить текущий merged runtime catalog: {exception.Message}");
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
            long declaredWadLength = RequiredLong(root, "wadLength");
            long declaredWadLastWriteUtcTicks = RequiredLong(root, "wadLastWriteUtcTicks");

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

            FileInfo wadInfo = new(wad);
            if (wadInfo.Length != declaredWadLength
                || wadInfo.LastWriteTimeUtc.Ticks != declaredWadLastWriteUtcTicks)
            {
                return Invalid("общий runtime WAD изменён после сборки");
            }

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

    private static WorkspaceSharedRuntimeInspection? TryInspectNativeGameRuntime(
        string requestedRuntimeWad,
        string workspace,
        WorkspaceContentVariantScanResult scan,
        CancellationToken cancellationToken)
    {
        // A mixed multi-version workspace must never be mistaken for a native XMAS installation.
        // Unified XMAS + Goblin will use its own explicit compatibility layer later.
        if (scan.PackageCount != 1)
            return null;

        string? outputDirectory = Path.GetDirectoryName(requestedRuntimeWad);
        if (string.IsNullOrWhiteSpace(outputDirectory))
            return null;

        DirectoryInfo output = new(outputDirectory);
        DirectoryInfo? gameDirectory = output.Name.Equals("MyDecks", StringComparison.OrdinalIgnoreCase)
            ? output.Parent
            : null;
        if (gameDirectory is null)
            return null;

        string nativeRoot = Path.Combine(gameDirectory.FullName, NativeRuntimeDirectoryName);
        string headerPath = Path.Combine(nativeRoot, "HEADER.XML");
        string dataRoot = Path.Combine(nativeRoot, "DATA_ALL_PLATFORMS");
        if (!Directory.Exists(nativeRoot)
            || !File.Exists(headerPath)
            || !Directory.Exists(dataRoot))
        {
            return null;
        }

        // Do not activate native mode merely because an unrelated directory has the same name.
        if (!File.Exists(Path.Combine(gameDirectory.FullName, "DotP_D14.exe"))
            && !File.Exists(Path.Combine(gameDirectory.FullName, "DotP_D13.exe")))
        {
            return null;
        }

        WorkspaceMergedRuntimeCatalogSnapshot catalog;
        try
        {
            catalog = LoadCatalog(workspace, scan, cancellationToken);
        }
        catch (Exception exception)
        {
            return Invalid($"найден native XMAS runtime, но workspace runtime catalog не читается: {exception.Message}");
        }

        int nativeOrder = ReadNativeRuntimeOrder(headerPath, catalog.SourceMaxOrder);
        int effectiveOrder = Math.Max(nativeOrder, catalog.SourceMaxOrder);
        HashSet<string> resources = catalog.Resources
            .Select(resource => NormalizeResourcePath(resource.RelativePath))
            .Where(resource => resource.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new WorkspaceSharedRuntimeInspection(
            new WorkspaceSharedRuntimeSnapshot(
                nativeRoot,
                headerPath,
                catalog.SourceMaxOrder,
                effectiveOrder,
                CountCardRoots(scan),
                resources),
            $"используется native XMAS runtime {NativeRuntimeDirectoryName} (order {nativeOrder}); Data_DLC_8000 не нужен");
    }

    private static WorkspaceMergedRuntimeCatalogSnapshot LoadCatalog(
        string workspace,
        WorkspaceContentVariantScanResult scan,
        CancellationToken cancellationToken)
    {
        List<string> warnings = new();
        HashSet<string> warningKeys = new(StringComparer.OrdinalIgnoreCase);
        return WorkspaceMergedRuntimeCatalog.Load(
            workspace,
            scan,
            warnings,
            warningKeys,
            cancellationToken);
    }

    private static int CountCardRoots(WorkspaceContentVariantScanResult scan) =>
        scan.CardVariants
            .Select(variant => variant.Reference)
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Select(reference => reference.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

    private static int ReadNativeRuntimeOrder(string headerPath, int fallback)
    {
        try
        {
            XDocument document = XDocument.Load(headerPath, LoadOptions.None);
            IEnumerable<XElement> elements = document.Root is null
                ? Enumerable.Empty<XElement>()
                : document.Root.DescendantsAndSelf();
            foreach (XAttribute attribute in elements.Attributes())
            {
                if (attribute.Name.LocalName.Equals("order", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(attribute.Value, out int order))
                {
                    return order;
                }
            }

            foreach (XElement element in document.Descendants())
            {
                if (element.Name.LocalName.Equals("order", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(element.Value, out int order))
                {
                    return order;
                }
            }
        }
        catch (Exception) when (File.Exists(headerPath))
        {
            // A valid native directory is still preferable to shadowing it with a generated runtime.
        }

        return fallback;
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

    private static long RequiredLong(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement element)
            || element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt64(out long value))
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

    private static string NormalizeResourcePath(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().Replace('/', '\\');

    private static WorkspaceSharedRuntimeInspection Invalid(string reason) => new(null, reason);
}
