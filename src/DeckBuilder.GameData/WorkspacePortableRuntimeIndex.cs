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
/// Immutable view of the effective DATA_ALL_PLATFORMS runtime in an extracted workspace.
/// Direct resource/function aliases are intentionally separated from LOL global symbols: CARD_V2
/// may select a declared function or concrete file, while global constants are only followed after
/// a runtime file has already become reachable. This prevents unrelated global registries from
/// turning one portable deck into a copy of the whole workspace.
/// </summary>
internal sealed class WorkspacePortableRuntimeIndex
{
    private const int MaxRuntimeDiscoveredCardReferences = 128;

    // SPECS and permanent text contain small shared tables that are often addressed through generated
    // ids. FUNCTIONS are deliberately NOT copied wholesale: only functions reachable from a card or
    // another selected runtime file are included.
    private static readonly string[] AlwaysIncludeTrees =
    {
        "SPECS",
        "TEXT_PERMANENT"
    };

    // These are game-content roots, not support runtime. CARD_V2 is handled by the card closure;
    // deck/unlock/personality definitions belong to the deck WAD and must never leak in here.
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

    private static readonly Regex LuaFunctionRegex = new(
        @"\bfunction\s+([A-Za-z_][A-Za-z0-9_.:]*)|\b([A-Za-z_][A-Za-z0-9_.:]*)\s*=\s*function\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex LuaGlobalAssignmentRegex = new(
        @"(?m)^\s*([A-Za-z_][A-Za-z0-9_.:]*)\s*=",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Runtime CARD_V2 discovery is deliberately semantic instead of "every token that happens to
    // equal a card alias". CW/RSN helpers commonly store card ids in CARD/TOKEN variables or pass
    // them to card/token functions; broad identifier matching caused thousands of false dependencies.
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
    private readonly IReadOnlyDictionary<string, RuntimeResource[]> _resourcesByDirectAlias;
    private readonly IReadOnlyDictionary<string, RuntimeResource[]> _resourcesByGlobalAlias;
    private readonly IReadOnlyList<string> _alwaysIncludedPaths;

    private WorkspacePortableRuntimeIndex(
        IReadOnlyDictionary<string, RuntimeResource> resourcesByPath,
        IReadOnlyDictionary<string, RuntimeResource[]> resourcesByDirectAlias,
        IReadOnlyDictionary<string, RuntimeResource[]> resourcesByGlobalAlias,
        IReadOnlyList<string> alwaysIncludedPaths)
    {
        _resourcesByPath = resourcesByPath;
        _resourcesByDirectAlias = resourcesByDirectAlias;
        _resourcesByGlobalAlias = resourcesByGlobalAlias;
        _alwaysIncludedPaths = alwaysIncludedPaths;
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
                AddWarning(
                    warnings,
                    warningKeys,
                    $"Could not read runtime manifest {manifestPath}: {exception.Message}");
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

                    string storagePath = Path.Combine(
                        wadDirectory,
                        file.StoragePath.Replace('/', Path.DirectorySeparatorChar));
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
        string[] alwaysIncludedPaths = effective
            .Where(resource => AlwaysIncludeTrees.Any(tree => StartsWithTree(resource.RelativePath, tree)))
            .Select(resource => resource.RelativePath)
            .ToArray();

        return new WorkspacePortableRuntimeIndex(
            resourcesByPath,
            aliasIndexes.Direct,
            aliasIndexes.Globals,
            alwaysIncludedPaths);
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

        HashSet<string> selectedPaths = _alwaysIncludedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> cardReferences = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> missingRoots = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> scannedTextFiles = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> queuedRuntimePaths = new(StringComparer.OrdinalIgnoreCase);
        Queue<RuntimeResource> pendingText = new();

        void Select(RuntimeResource resource)
        {
            selectedPaths.Add(resource.RelativePath);
            if (IsTextResource(resource) && queuedRuntimePaths.Add(resource.RelativePath))
                pendingText.Enqueue(resource);
        }

        foreach (string identifier in rootIdentifiers ?? Array.Empty<string>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(identifier))
                continue;

            // Explicit packaging roots are paths/names, never arbitrary LOL globals.
            if (TryResolveResource(identifier, allowGlobals: false, out RuntimeResource resource))
                Select(resource);
            else
                missingRoots.Add(identifier.Trim());
        }

        foreach (string cardXmlPath in cardXmlPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanTextFile(
                cardXmlPath,
                cardAliases,
                cardReferences,
                Select,
                scannedTextFiles,
                allowGlobalResourceAliases: false,
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
                cardAliases,
                cardReferences,
                Select,
                scannedTextFiles,
                allowGlobalResourceAliases: true,
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
        IReadOnlyDictionary<string, string> cardAliases,
        ISet<string> cardReferences,
        Action<RuntimeResource> selectResource,
        ISet<string> scannedTextFiles,
        bool allowGlobalResourceAliases,
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
            if (TryResolveResource(token, allowGlobalResourceAliases, out RuntimeResource resource))
                selectResource(resource);
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

    private bool TryResolveResource(string token, bool allowGlobals, out RuntimeResource resource)
    {
        foreach (string alias in EnumerateTokenAliases(token))
        {
            if (TryResolveUnique(_resourcesByDirectAlias, alias, out resource))
                return true;
            if (allowGlobals && TryResolveUnique(_resourcesByGlobalAlias, alias, out resource))
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
        Dictionary<string, List<RuntimeResource>> direct = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<RuntimeResource>> globals = new(StringComparer.OrdinalIgnoreCase);

        foreach (RuntimeResource resource in resources)
        {
            foreach (string alias in EnumerateResourceAliases(resource.RelativePath))
                AddAlias(direct, alias, resource);

            if (!StartsWithTree(resource.RelativePath, "FUNCTIONS") || !IsTextResource(resource))
                continue;

            try
            {
                string text = File.ReadAllText(resource.StoragePath);
                foreach (Match match in LuaFunctionRegex.Matches(text))
                {
                    string symbol = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                    AddSymbolAliases(direct, symbol, resource);
                }

                foreach (Match match in LuaGlobalAssignmentRegex.Matches(text))
                    AddSymbolAliases(globals, match.Groups[1].Value, resource);
            }
            catch
            {
                // A malformed unrelated function file must not make every portable deck unbuildable.
            }
        }

        return new RuntimeAliasIndexes(ToImmutableAliasIndex(direct), ToImmutableAliasIndex(globals));
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

    private static bool IsTextResource(RuntimeResource resource) =>
        TextExtensions.Contains(Path.GetExtension(resource.RelativePath));

    private static bool IsAllowedRuntimeResource(string relativePath) =>
        !ForbiddenTrees.Any(tree => StartsWithTree(relativePath, tree));

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
        Array.Empty<string>());

    private sealed record RuntimeAliasIndexes(
        IReadOnlyDictionary<string, RuntimeResource[]> Direct,
        IReadOnlyDictionary<string, RuntimeResource[]> Globals);

    private sealed record RuntimeResource(
        string RelativePath,
        string PackageName,
        string WadName,
        int WadOrder,
        string StoragePath);
}
