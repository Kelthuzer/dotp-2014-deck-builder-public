using System.Text.RegularExpressions;

namespace DeckBuilder.GameData;

internal sealed record WorkspacePortableRuntimeResolution(
    IReadOnlyList<string> ResourcePaths,
    IReadOnlyDictionary<string, int> ResourceCounts,
    IReadOnlyList<string> CardReferences,
    IReadOnlyList<string> MissingRootIdentifiers)
{
    public static WorkspacePortableRuntimeResolution Empty { get; } = new(
        Array.Empty<string>(),
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
        Array.Empty<string>(),
        Array.Empty<string>());

    public int ResourceCount => ResourcePaths.Count;
}

/// <summary>
/// Immutable index of the effective DATA_ALL_PLATFORMS runtime in an extracted workspace.
/// Concrete files, declared LOL functions and LOL globals are separate namespaces. A bare word in
/// CARD_V2/LOL code is never treated as an arbitrary file basename; doing that pulled unrelated
/// animation binaries and textures into portable decks and produced gigabyte-scale WADs.
/// </summary>
internal sealed class WorkspacePortableRuntimeIndex
{
    private const int MaxRuntimeDiscoveredCardReferences = 128;
    private const int MaxSelectedRuntimeResources = 1024;

    private static readonly string[] ImplicitSharedResourcePaths =
    {
        "SPECS\\CREATURE_TYPES.TXT"
    };

    private const string CreatureTypeTextPrefix = "TEXT_PERMANENT\\CREATURE_TYPE_TEXT_";

    private static readonly string[] ForbiddenTrees =
    {
        "CARDS",
        "DECKS",
        "UNLOCKS",
        "AI_PERSONALITIES"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".XML",
        ".LOL",
        ".LUA",
        ".TXT",
        ".CSV",
        ".INI",
        ".JSON"
    };

    private static readonly Regex IdentifierRegex = new(
        @"[A-Za-z0-9_./\\:-]{3,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex QuotedValueRegex = new(
        "[\\\"']([^\\\"'\\r\\n]{3,})[\\\"']",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PathReferenceRegex = new(
        @"[A-Za-z0-9_.:-]+(?:[\\/][A-Za-z0-9_.:-]+)+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LuaFunctionRegex = new(
        @"\bfunction\s+([A-Za-z_][A-Za-z0-9_.:]*)|\b([A-Za-z_][A-Za-z0-9_.:]*)\s*=\s*function\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex LuaGlobalAssignmentRegex = new(
        @"(?m)^\s*([A-Za-z_][A-Za-z0-9_.:]*)\s*=",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RuntimeResourceAssignmentRegex = new(
        @"(?im)\b[A-Za-z_][A-Za-z0-9_.:]*(?:TEXTURE|IMAGE|FRAME|MANA|EFFECT|ASSET|FRONTEND|SPEC|TEXT)[A-Za-z0-9_.:]*\s*=\s*[""']?([A-Za-z0-9_.:-]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RuntimeCardAssignmentRegex = new(
        @"(?im)^\s*([A-Za-z_][A-Za-z0-9_.:]*(?:CARD|TOKEN|MULTIVERSE)[A-Za-z0-9_.:]*)\s*=\s*[""']?([A-Za-z0-9_]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RuntimeCardCallRegex = new(
        @"(?i)\b[A-Za-z_][A-Za-z0-9_.:]*(?:CARD|TOKEN)[A-Za-z0-9_.:]*\s*\(\s*[""']?([A-Za-z0-9_]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RuntimeCardAttributeRegex = new(
        @"(?i)\b[A-Za-z0-9_.:-]*(?:CARD|TOKEN|MULTIVERSE)[A-Za-z0-9_.:-]*\s*=\s*[""']([A-Za-z0-9_]+)[""']",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IReadOnlyDictionary<string, RuntimeResource> _resourcesByPath;
    private readonly IReadOnlyDictionary<string, RuntimeResource[]> _resourcesByConcreteAlias;
    private readonly IReadOnlyDictionary<string, RuntimeResource[]> _resourcesByFunctionAlias;
    private readonly IReadOnlyDictionary<string, RuntimeResource[]> _resourcesByGlobalAlias;
    private readonly IReadOnlyList<string> _implicitSharedPaths;

    private WorkspacePortableRuntimeIndex(
        IReadOnlyDictionary<string, RuntimeResource> resourcesByPath,
        IReadOnlyDictionary<string, RuntimeResource[]> resourcesByConcreteAlias,
        IReadOnlyDictionary<string, RuntimeResource[]> resourcesByFunctionAlias,
        IReadOnlyDictionary<string, RuntimeResource[]> resourcesByGlobalAlias,
        IReadOnlyList<string> implicitSharedPaths)
    {
        _resourcesByPath = resourcesByPath;
        _resourcesByConcreteAlias = resourcesByConcreteAlias;
        _resourcesByFunctionAlias = resourcesByFunctionAlias;
        _resourcesByGlobalAlias = resourcesByGlobalAlias;
        _implicitSharedPaths = implicitSharedPaths;
    }

    public bool IsEmpty => _resourcesByPath.Count == 0;

    public static WorkspacePortableRuntimeIndex Load(
        string? workspaceDirectory,
        ICollection<string> warnings,
        ISet<string> warningKeys,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(warnings);
        ArgumentNullException.ThrowIfNull(warningKeys);

        if (string.IsNullOrWhiteSpace(workspaceDirectory) || !Directory.Exists(workspaceDirectory))
            return Empty();

        string workspace = Path.GetFullPath(workspaceDirectory);
        string[] manifests = Directory.EnumerateFiles(
                workspace,
                GameVersionPackageService.ManifestFileName,
                SearchOption.AllDirectories)
            .OrderBy(path => Path.GetDirectoryName(path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (manifests.Length == 0)
            return Empty();

        GameVersionPackageService packageService = new();
        List<RuntimeResource> candidates = new();

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
                string wadDirectory = Path.Combine(packageDirectory, "wads", SafeDirectoryName(wad.Name));
                foreach (DotpWadFileManifest file in wad.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string? relativePath = GetAllPlatformsRelativePath(file.ArchivePath);
                    if (relativePath is null || !IsAllowedRuntimeResource(relativePath))
                        continue;

                    string storagePath = Path.Combine(wadDirectory, file.StoragePath.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(storagePath))
                    {
                        AddWarning(
                            warnings,
                            warningKeys,
                            $"Runtime payload {relativePath} from {manifest.VersionName} / {wad.Name} is missing from the workspace.");
                        continue;
                    }

                    candidates.Add(new RuntimeResource(
                        relativePath,
                        manifest.VersionName,
                        wad.Name,
                        wad.PrimaryOrder,
                        storagePath));
                }
            }
        }

        RuntimeResource[] effective = candidates
            .GroupBy(resource => resource.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(resource => resource.WadOrder)
                .ThenBy(resource => resource.WadName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(resource => resource.PackageName, StringComparer.OrdinalIgnoreCase)
                .Last())
            .OrderBy(resource => resource.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Dictionary<string, RuntimeResource> resourcesByPath = effective.ToDictionary(
            resource => resource.RelativePath,
            StringComparer.OrdinalIgnoreCase);
        RuntimeAliasIndexes aliasIndexes = BuildAliasIndexes(effective);
        string[] implicitSharedPaths = effective
            .Where(resource => IsImplicitSharedResource(resource.RelativePath))
            .Select(resource => resource.RelativePath)
            .ToArray();

        return new WorkspacePortableRuntimeIndex(
            resourcesByPath,
            aliasIndexes.Concrete,
            aliasIndexes.Functions,
            aliasIndexes.Globals,
            implicitSharedPaths);
    }

    public WorkspacePortableRuntimeResolution Resolve(
        IEnumerable<string> cardXmlPaths,
        IEnumerable<string>? rootIdentifiers,
        IReadOnlyDictionary<string, string> cardAliases,
        ICollection<string> warnings,
        ISet<string> warningKeys,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cardXmlPaths);
        ArgumentNullException.ThrowIfNull(cardAliases);
        ArgumentNullException.ThrowIfNull(warnings);
        ArgumentNullException.ThrowIfNull(warningKeys);

        if (IsEmpty)
            return WorkspacePortableRuntimeResolution.Empty;

        HashSet<string> selectedPaths = _implicitSharedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selectedPaths.Count > MaxSelectedRuntimeResources)
        {
            throw new InvalidDataException(
                $"The implicit portable runtime contains {selectedPaths.Count:N0} resources, exceeding the safety limit of {MaxSelectedRuntimeResources:N0}.");
        }

        HashSet<string> cardReferences = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> missingRoots = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> scannedTextFiles = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> queuedRuntimePaths = new(StringComparer.OrdinalIgnoreCase);
        Queue<RuntimeResource> pendingText = new();

        void Select(RuntimeResource resource, string reason)
        {
            if (!selectedPaths.Add(resource.RelativePath))
                return;

            if (selectedPaths.Count > MaxSelectedRuntimeResources)
            {
                throw new InvalidDataException(
                    $"Portable runtime dependency closure exceeded {MaxSelectedRuntimeResources:N0} resources while adding {resource.RelativePath}. " +
                    $"Dependency: {reason}. Packaging was stopped before producing an oversized WAD.");
            }

            if (IsTextResource(resource) && queuedRuntimePaths.Add(resource.RelativePath))
                pendingText.Enqueue(resource);
        }

        foreach (string identifier in rootIdentifiers ?? Array.Empty<string>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(identifier))
                continue;

            if (TryResolveExplicitRoot(identifier, out RuntimeResource resource))
                Select(resource, $"explicit packaging root '{identifier.Trim()}'");
            else
                missingRoots.Add(identifier.Trim());
        }

        foreach (string cardXmlPath in cardXmlPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanTextFile(
                cardXmlPath,
                $"CARD_V2 {Path.GetFileName(cardXmlPath)}",
                cardAliases,
                cardReferences,
                Select,
                scannedTextFiles,
                allowGlobalSymbols: false,
                discoverCardReferences: false,
                warnings,
                warningKeys,
                cancellationToken);
        }

        while (pendingText.TryDequeue(out RuntimeResource? resource))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanTextFile(
                resource.StoragePath,
                resource.RelativePath,
                cardAliases,
                cardReferences,
                Select,
                scannedTextFiles,
                allowGlobalSymbols: true,
                discoverCardReferences: true,
                warnings,
                warningKeys,
                cancellationToken);
        }

        string[] paths = selectedPaths
            .Where(_resourcesByPath.ContainsKey)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        IReadOnlyDictionary<string, int> counts = paths
            .GroupBy(GetResourceGroup, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        return new WorkspacePortableRuntimeResolution(
            paths,
            counts,
            cardReferences.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
            missingRoots.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public void CopyIntoStaging(
        string stagingDirectory,
        WorkspacePortableRuntimeResolution resolution,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        ArgumentNullException.ThrowIfNull(resolution);

        foreach (string relativePath in resolution.ResourcePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_resourcesByPath.TryGetValue(relativePath, out RuntimeResource? resource))
                continue;

            string target = Path.Combine(
                stagingDirectory,
                "DATA_ALL_PLATFORMS",
                relativePath.Replace('\\', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!File.Exists(target))
                File.Copy(resource.StoragePath, target, overwrite: false);
        }
    }

    private void ScanTextFile(
        string path,
        string sourceLabel,
        IReadOnlyDictionary<string, string> cardAliases,
        ISet<string> cardReferences,
        Action<RuntimeResource, string> selectResource,
        ISet<string> scannedTextFiles,
        bool allowGlobalSymbols,
        bool discoverCardReferences,
        ICollection<string> warnings,
        ISet<string> warningKeys,
        CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(path);
        if (!scannedTextFiles.Add(fullPath))
            return;

        string text;
        try
        {
            text = File.ReadAllText(fullPath);
        }
        catch (Exception exception)
        {
            AddWarning(warnings, warningKeys, $"Could not inspect dependency text {fullPath}: {exception.Message}");
            return;
        }

        if (discoverCardReferences)
            ScanRuntimeCardReferences(text, cardAliases, cardReferences);

        foreach (Match match in IdentifierRegex.Matches(text))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string token = match.Value;
            if (TryResolveSymbol(token, allowGlobalSymbols, out RuntimeResource resource))
                selectResource(resource, $"{sourceLabel} -> symbol '{token}'");
        }

        foreach (Match match in QuotedValueRegex.Matches(text))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string token = match.Groups[1].Value.Trim();
            if (TryResolveConcreteReference(token, allowBareSymbolicAsset: true, out RuntimeResource resource))
                selectResource(resource, $"{sourceLabel} -> quoted resource '{token}'");
        }

        foreach (Match match in RuntimeResourceAssignmentRegex.Matches(text))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string token = match.Groups[1].Value.Trim();
            if (TryResolveConcreteReference(token, allowBareSymbolicAsset: true, out RuntimeResource resource))
                selectResource(resource, $"{sourceLabel} -> resource assignment '{token}'");
        }

        foreach (Match match in PathReferenceRegex.Matches(text))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string token = match.Value;
            if (TryResolveConcreteReference(token, allowBareSymbolicAsset: false, out RuntimeResource resource))
                selectResource(resource, $"{sourceLabel} -> path '{token}'");
        }
    }

    private static void ScanRuntimeCardReferences(
        string text,
        IReadOnlyDictionary<string, string> cardAliases,
        ISet<string> cardReferences)
    {
        void Resolve(string token)
        {
            if (!WorkspaceCardDependencyResolver.TryResolveReferenceAlias(token, cardAliases, out string cardReference))
                return;
            if (!cardReferences.Add(cardReference))
                return;
            if (cardReferences.Count > MaxRuntimeDiscoveredCardReferences)
            {
                throw new InvalidDataException(
                    $"Portable runtime exposed more than {MaxRuntimeDiscoveredCardReferences} CARD_V2 references. " +
                    "This usually means a shared card/token registry was treated as a deck dependency; packaging was stopped before producing an oversized WAD.");
            }
        }

        foreach (Match match in RuntimeCardAssignmentRegex.Matches(text))
            Resolve(match.Groups[2].Value);
        foreach (Match match in RuntimeCardCallRegex.Matches(text))
            Resolve(match.Groups[1].Value);
        foreach (Match match in RuntimeCardAttributeRegex.Matches(text))
            Resolve(match.Groups[1].Value);
    }

    private bool TryResolveExplicitRoot(string token, out RuntimeResource resource)
    {
        if (TryResolveConcreteReference(token, allowBareSymbolicAsset: true, out resource))
            return true;

        return TryResolveUniqueAliases(_resourcesByFunctionAlias, token, out resource);
    }

    private bool TryResolveSymbol(string token, bool allowGlobals, out RuntimeResource resource)
    {
        foreach (string alias in EnumerateTokenAliases(token))
        {
            if (TryResolveUnique(_resourcesByFunctionAlias, alias, out resource))
                return true;
            if (allowGlobals && TryResolveUnique(_resourcesByGlobalAlias, alias, out resource))
                return true;
        }

        resource = null!;
        return false;
    }

    private bool TryResolveConcreteReference(string token, bool allowBareSymbolicAsset, out RuntimeResource resource)
    {
        bool explicitReference = IsExplicitResourceReference(token);
        foreach (string alias in EnumerateTokenAliases(token))
        {
            if (!TryResolveUnique(_resourcesByConcreteAlias, alias, out RuntimeResource candidate))
                continue;

            if (explicitReference || (allowBareSymbolicAsset && IsBareSymbolicResource(candidate.RelativePath)))
            {
                resource = candidate;
                return true;
            }
        }

        resource = null!;
        return false;
    }

    private static bool TryResolveUniqueAliases(
        IReadOnlyDictionary<string, RuntimeResource[]> index,
        string token,
        out RuntimeResource resource)
    {
        foreach (string alias in EnumerateTokenAliases(token))
        {
            if (TryResolveUnique(index, alias, out resource))
                return true;
        }

        resource = null!;
        return false;
    }

    private static bool TryResolveUnique(
        IReadOnlyDictionary<string, RuntimeResource[]> index,
        string alias,
        out RuntimeResource resource)
    {
        if (index.TryGetValue(alias, out RuntimeResource[]? matches) && matches.Length == 1)
        {
            resource = matches[0];
            return true;
        }

        resource = null!;
        return false;
    }

    private static RuntimeAliasIndexes BuildAliasIndexes(IReadOnlyList<RuntimeResource> resources)
    {
        Dictionary<string, List<RuntimeResource>> concrete = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<RuntimeResource>> functions = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<RuntimeResource>> globals = new(StringComparer.OrdinalIgnoreCase);

        foreach (RuntimeResource resource in resources)
        {
            foreach (string alias in EnumerateResourceAliases(resource.RelativePath))
                AddAlias(concrete, alias, resource);

            if (!StartsWithTree(resource.RelativePath, "FUNCTIONS") || !IsTextResource(resource))
                continue;

            try
            {
                string text = File.ReadAllText(resource.StoragePath);
                foreach (Match match in LuaFunctionRegex.Matches(text))
                {
                    string symbol = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                    AddSymbolAliases(functions, symbol, resource);
                }

                foreach (Match match in LuaGlobalAssignmentRegex.Matches(text))
                    AddSymbolAliases(globals, match.Groups[1].Value, resource);
            }
            catch
            {
                // A malformed unrelated function file must not make every portable deck unbuildable.
            }
        }

        return new RuntimeAliasIndexes(
            ToImmutableAliasIndex(concrete),
            ToImmutableAliasIndex(functions),
            ToImmutableAliasIndex(globals));
    }

    private static IReadOnlyDictionary<string, RuntimeResource[]> ToImmutableAliasIndex(
        IDictionary<string, List<RuntimeResource>> aliases) =>
        aliases.ToDictionary(
            pair => pair.Key,
            pair => pair.Value
                .GroupBy(resource => resource.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(resource => resource.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            StringComparer.OrdinalIgnoreCase);

    private static void AddSymbolAliases(
        IDictionary<string, List<RuntimeResource>> aliases,
        string symbol,
        RuntimeResource resource)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return;

        AddAlias(aliases, symbol, resource);
        int separator = Math.Max(symbol.LastIndexOf('.'), symbol.LastIndexOf(':'));
        if (separator >= 0 && separator < symbol.Length - 1)
            AddAlias(aliases, symbol[(separator + 1)..], resource);
    }

    private static void AddAlias(
        IDictionary<string, List<RuntimeResource>> aliases,
        string? alias,
        RuntimeResource resource)
    {
        if (string.IsNullOrWhiteSpace(alias))
            return;

        string key = alias.Trim();
        if (!aliases.TryGetValue(key, out List<RuntimeResource>? matches))
        {
            matches = new List<RuntimeResource>();
            aliases[key] = matches;
        }

        matches.Add(resource);
    }

    private static IEnumerable<string> EnumerateTokenAliases(string token)
    {
        string normalized = token.Trim().Replace('/', '\\');
        if (normalized.Length == 0)
            yield break;

        const string allPlatforms = "DATA_ALL_PLATFORMS\\";
        int allPlatformsIndex = normalized.IndexOf(allPlatforms, StringComparison.OrdinalIgnoreCase);
        if (allPlatformsIndex >= 0)
            normalized = normalized[(allPlatformsIndex + allPlatforms.Length)..];
        else if (normalized.StartsWith("CONTENT\\", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["CONTENT\\".Length..];

        normalized = normalized.TrimStart('.', '\\');
        if (normalized.Length == 0)
            yield break;

        yield return normalized;
        string withoutExtension = RemoveExtension(normalized);
        if (!withoutExtension.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            yield return withoutExtension;

        string fileName = normalized.Contains('\\')
            ? normalized[(normalized.LastIndexOf('\\') + 1)..]
            : normalized;
        yield return fileName;
        string fileWithoutExtension = RemoveExtension(fileName);
        if (!fileWithoutExtension.Equals(fileName, StringComparison.OrdinalIgnoreCase))
            yield return fileWithoutExtension;
    }

    private static IEnumerable<string> EnumerateResourceAliases(string relativePath)
    {
        string normalized = relativePath.Replace('/', '\\');
        yield return normalized;
        yield return RemoveExtension(normalized);

        string fileName = normalized.Contains('\\')
            ? normalized[(normalized.LastIndexOf('\\') + 1)..]
            : normalized;
        yield return fileName;
        yield return RemoveExtension(fileName);
    }

    private static string RemoveExtension(string value)
    {
        int slash = value.LastIndexOf('\\');
        int dot = value.LastIndexOf('.');
        return dot > slash ? value[..dot] : value;
    }

    private static bool IsExplicitResourceReference(string token)
    {
        string value = token.Trim();
        if (value.IndexOfAny(['\\', '/']) >= 0)
            return true;

        string extension = Path.GetExtension(value);
        return !string.IsNullOrWhiteSpace(extension);
    }

    private static bool IsBareSymbolicResource(string relativePath) =>
        StartsWithTree(relativePath, "ART_ASSETS\\TEXTURES")
        || StartsWithTree(relativePath, "ART_ASSETS\\FRONTEND")
        || StartsWithTree(relativePath, "SPECS")
        || StartsWithTree(relativePath, "TEXT_PERMANENT");

    private static bool IsTextResource(RuntimeResource resource) =>
        TextExtensions.Contains(Path.GetExtension(resource.RelativePath));

    private static bool IsAllowedRuntimeResource(string relativePath) =>
        !ForbiddenTrees.Any(tree => StartsWithTree(relativePath, tree));

    private static bool IsImplicitSharedResource(string relativePath)
    {
        if (ImplicitSharedResourcePaths.Any(path => relativePath.Equals(path, StringComparison.OrdinalIgnoreCase)))
            return true;

        return relativePath.StartsWith(CreatureTypeTextPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool StartsWithTree(string path, string tree) =>
        path.Equals(tree, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(tree + "\\", StringComparison.OrdinalIgnoreCase);

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

    private static WorkspacePortableRuntimeIndex Empty() => new(
        new Dictionary<string, RuntimeResource>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, RuntimeResource[]>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, RuntimeResource[]>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, RuntimeResource[]>(StringComparer.OrdinalIgnoreCase),
        Array.Empty<string>());

    private sealed record RuntimeAliasIndexes(
        IReadOnlyDictionary<string, RuntimeResource[]> Concrete,
        IReadOnlyDictionary<string, RuntimeResource[]> Functions,
        IReadOnlyDictionary<string, RuntimeResource[]> Globals);

    private sealed record RuntimeResource(
        string RelativePath,
        string PackageName,
        string WadName,
        int WadOrder,
        string StoragePath);
}
