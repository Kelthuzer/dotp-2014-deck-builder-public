using System.Text.RegularExpressions;

namespace DeckBuilder.GameData;

internal sealed record WorkspaceSharedRuntimePackResult(
    int ResourceCount,
    IReadOnlyDictionary<string, int> ResourceCounts,
    IReadOnlyList<string> CardReferences)
{
    public static WorkspaceSharedRuntimePackResult Empty { get; } = new(
        0,
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
        Array.Empty<string>());
}

/// <summary>
/// Builds the portable, non-deck runtime that CARD_V2 definitions need when moved to another
/// Magic 2014 installation. Some community cards are not self-contained XML: they call CW/RSN
/// functions, read SPECS tables, resolve TEXT_PERMANENT ids, use custom mana/frame/UI textures,
/// and may reach helper CARD_V2 definitions from the runtime itself.
///
/// The packager therefore has two layers:
///  1. copy the complete effective shared runtime trees that can be addressed dynamically;
///  2. recursively follow concrete resource/card identifiers found in the selected CARD_V2 and
///     in the particular runtime files those cards call.
///
/// Decks, unlocks and AI personalities are deliberately excluded from resource closure: importing
/// them would change the recipient's game content rather than merely making the selected deck
/// portable.
/// </summary>
internal static class WorkspaceSharedFunctionPackager
{
    private static readonly string[] AlwaysPackDirectories =
    {
        "FUNCTIONS",
        "SPECS",
        "TEXT_PERMANENT",
        "ART_ASSETS\\TEXTURES\\MANA",
        "ART_ASSETS\\TEXTURES\\CARD_FRAMES",
        "ART_ASSETS\\FRONTEND"
    };

    private static readonly string[] ForbiddenDependencyDirectories =
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
        @"\bfunction\s+([A-Za-z_][A-Za-z0-9_]*)|\b([A-Za-z_][A-Za-z0-9_]*)\s*=\s*function\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static WorkspaceSharedRuntimePackResult CopyIntoStaging(
        string? workspaceDirectory,
        string stagingDirectory,
        IReadOnlyDictionary<string, string> referenceAliases,
        ICollection<string> warnings,
        ISet<string> warningKeys,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(referenceAliases);
        if (string.IsNullOrWhiteSpace(workspaceDirectory)
            || !Directory.Exists(workspaceDirectory))
        {
            return WorkspaceSharedRuntimePackResult.Empty;
        }

        string workspace = Path.GetFullPath(workspaceDirectory);
        string[] manifestPaths = Directory.EnumerateFiles(
                workspace,
                GameVersionPackageService.ManifestFileName,
                SearchOption.AllDirectories)
            .OrderBy(path => Path.GetDirectoryName(path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (manifestPaths.Length == 0)
        {
            return WorkspaceSharedRuntimePackResult.Empty;
        }

        GameVersionPackageService packageService = new();
        List<RuntimeCandidate> candidates = new();

        foreach (string manifestPath in manifestPaths)
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
                    $"Could not inspect portable card runtime in {packageDirectory}: {exception.Message}");
                continue;
            }

            foreach (DotpWadPackageManifest wad in manifest.Wads)
            {
                string wadDirectory = Path.Combine(packageDirectory, "wads", SafeDirectoryName(wad.Name));
                foreach (DotpWadFileManifest file in wad.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string? relative = AllPlatformsRelative(file.ArchivePath);
                    if (relative is null)
                    {
                        continue;
                    }

                    string storagePath = Path.Combine(
                        wadDirectory,
                        file.StoragePath.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(storagePath))
                    {
                        AddWarning(
                            warnings,
                            warningKeys,
                            $"Runtime payload {relative} from {manifest.VersionName} / {wad.Name} is missing from the extracted workspace.");
                        continue;
                    }

                    candidates.Add(new RuntimeCandidate(
                        relative,
                        manifest.VersionName,
                        wad.Name,
                        wad.PrimaryOrder,
                        storagePath));
                }
            }
        }

        if (candidates.Count == 0)
        {
            return WorkspaceSharedRuntimePackResult.Empty;
        }

        RuntimeCandidate[] effective = candidates
            .GroupBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(item => item.WadOrder)
                .ThenBy(item => item.WadName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.PackageName, StringComparer.OrdinalIgnoreCase)
                .Last())
            .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Dictionary<string, RuntimeCandidate[]> resourceAliases = BuildResourceAliasIndex(effective);
        HashSet<string> selectedPaths = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> discoveredCardReferences = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> scannedTextPaths = new(StringComparer.OrdinalIgnoreCase);
        Queue<RuntimeCandidate> textQueue = new();

        foreach (RuntimeCandidate resource in effective)
        {
            if (AlwaysPackDirectories.Any(directory => StartsWithDirectory(resource.RelativePath, directory)))
            {
                selectedPaths.Add(resource.RelativePath);
            }
        }

        string stagedCards = Path.Combine(stagingDirectory, "DATA_ALL_PLATFORMS", "CARDS");
        if (Directory.Exists(stagedCards))
        {
            foreach (string cardPath in Directory.EnumerateFiles(stagedCards, "*", SearchOption.AllDirectories)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsTextPath(cardPath))
                {
                    ScanText(
                        cardPath,
                        referenceAliases,
                        resourceAliases,
                        selectedPaths,
                        discoveredCardReferences,
                        textQueue,
                        scannedTextPaths,
                        warnings,
                        warningKeys,
                        cancellationToken);
                }
            }
        }

        while (textQueue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RuntimeCandidate resource = textQueue.Dequeue();
            ScanText(
                resource.StoragePath,
                referenceAliases,
                resourceAliases,
                selectedPaths,
                discoveredCardReferences,
                textQueue,
                scannedTextPaths,
                warnings,
                warningKeys,
                cancellationToken);
        }

        RuntimeCandidate[] selected = effective
            .Where(resource => selectedPaths.Contains(resource.RelativePath))
            .OrderBy(resource => resource.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (RuntimeCandidate resource in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string target = Path.Combine(
                stagingDirectory,
                "DATA_ALL_PLATFORMS",
                resource.RelativePath.Replace('\\', Path.DirectorySeparatorChar));
            string? targetDirectory = Path.GetDirectoryName(target);
            if (!string.IsNullOrWhiteSpace(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            if (!File.Exists(target))
            {
                File.Copy(resource.StoragePath, target, overwrite: false);
            }
        }

        IReadOnlyDictionary<string, int> counts = selected
            .GroupBy(resource => ResourceGroup(resource.RelativePath), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        return new WorkspaceSharedRuntimePackResult(
            selected.Length,
            counts,
            discoveredCardReferences.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static void ScanText(
        string path,
        IReadOnlyDictionary<string, string> referenceAliases,
        IReadOnlyDictionary<string, RuntimeCandidate[]> resourceAliases,
        ISet<string> selectedPaths,
        ISet<string> discoveredCardReferences,
        Queue<RuntimeCandidate> textQueue,
        ISet<string> scannedTextPaths,
        ICollection<string> warnings,
        ISet<string> warningKeys,
        CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(path);
        if (!scannedTextPaths.Add(fullPath))
        {
            return;
        }

        string text;
        try
        {
            text = File.ReadAllText(fullPath);
        }
        catch (Exception exception)
        {
            AddWarning(warnings, warningKeys, $"Could not inspect runtime dependency text {fullPath}: {exception.Message}");
            return;
        }

        foreach (Match match in IdentifierRegex.Matches(text))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string token = match.Value;

            if (WorkspaceCardDependencyResolver.TryResolveReferenceAlias(
                    token,
                    referenceAliases,
                    out string canonicalReference))
            {
                discoveredCardReferences.Add(canonicalReference);
            }

            if (!TryResolveResource(token, resourceAliases, out RuntimeCandidate resource)
                || !IsAllowedDependencyResource(resource.RelativePath))
            {
                continue;
            }

            selectedPaths.Add(resource.RelativePath);
            if (IsTextPath(resource.StoragePath)
                && !scannedTextPaths.Contains(Path.GetFullPath(resource.StoragePath)))
            {
                textQueue.Enqueue(resource);
            }
        }
    }

    private static Dictionary<string, RuntimeCandidate[]> BuildResourceAliasIndex(
        IReadOnlyList<RuntimeCandidate> effective)
    {
        Dictionary<string, List<RuntimeCandidate>> aliases = new(StringComparer.OrdinalIgnoreCase);
        foreach (RuntimeCandidate resource in effective.Where(resource => IsAllowedDependencyResource(resource.RelativePath)))
        {
            AddResourceAliases(aliases, resource, ResourceAliases(resource.RelativePath));

            if (StartsWithDirectory(resource.RelativePath, "FUNCTIONS") && IsTextPath(resource.StoragePath))
            {
                try
                {
                    string text = File.ReadAllText(resource.StoragePath);
                    foreach (Match match in LuaFunctionRegex.Matches(text))
                    {
                        string symbol = match.Groups[1].Success
                            ? match.Groups[1].Value
                            : match.Groups[2].Value;
                        if (!string.IsNullOrWhiteSpace(symbol))
                        {
                            AddResourceAlias(aliases, symbol, resource);
                        }
                    }
                }
                catch
                {
                    // An unrelated malformed function file must not abort package indexing.
                }
            }
        }

        return aliases.ToDictionary(
            pair => pair.Key,
            pair => pair.Value
                .GroupBy(resource => resource.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(resource => resource.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryResolveResource(
        string token,
        IReadOnlyDictionary<string, RuntimeCandidate[]> aliases,
        out RuntimeCandidate resource)
    {
        foreach (string alias in TokenAliases(token))
        {
            if (!aliases.TryGetValue(alias, out RuntimeCandidate[]? matches)
                || matches.Length != 1)
            {
                continue;
            }

            resource = matches[0];
            return true;
        }

        resource = null!;
        return false;
    }

    private static IEnumerable<string> TokenAliases(string token)
    {
        string normalized = token.Trim().Replace('/', '\\');
        if (normalized.Length == 0)
            yield break;

        const string allPlatforms = "DATA_ALL_PLATFORMS\\";
        int allPlatformsIndex = normalized.IndexOf(allPlatforms, StringComparison.OrdinalIgnoreCase);
        if (allPlatformsIndex >= 0)
        {
            normalized = normalized[(allPlatformsIndex + allPlatforms.Length)..];
        }
        else if (normalized.StartsWith("CONTENT\\", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["CONTENT\\".Length..];
        }

        normalized = normalized.TrimStart('.', '\\');
        if (normalized.Length == 0)
            yield break;

        yield return normalized;
        string withoutExtension = RemoveExtension(normalized);
        if (!withoutExtension.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            yield return withoutExtension;

        string fileName = normalized.Contains('\\') ? normalized[(normalized.LastIndexOf('\\') + 1)..] : normalized;
        yield return fileName;
        string fileWithoutExtension = RemoveExtension(fileName);
        if (!fileWithoutExtension.Equals(fileName, StringComparison.OrdinalIgnoreCase))
            yield return fileWithoutExtension;
    }

    private static IEnumerable<string> ResourceAliases(string relativePath)
    {
        string normalized = relativePath.Replace('/', '\\');
        yield return normalized;
        yield return RemoveExtension(normalized);

        string fileName = normalized.Contains('\\') ? normalized[(normalized.LastIndexOf('\\') + 1)..] : normalized;
        yield return fileName;
        yield return RemoveExtension(fileName);
    }

    private static string RemoveExtension(string value)
    {
        int slash = value.LastIndexOf('\\');
        int dot = value.LastIndexOf('.');
        return dot > slash ? value[..dot] : value;
    }

    private static void AddResourceAliases(
        IDictionary<string, List<RuntimeCandidate>> aliases,
        RuntimeCandidate resource,
        IEnumerable<string> values)
    {
        foreach (string value in values.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            AddResourceAlias(aliases, value, resource);
        }
    }

    private static void AddResourceAlias(
        IDictionary<string, List<RuntimeCandidate>> aliases,
        string alias,
        RuntimeCandidate resource)
    {
        if (string.IsNullOrWhiteSpace(alias))
            return;

        if (!aliases.TryGetValue(alias, out List<RuntimeCandidate>? resources))
        {
            resources = new List<RuntimeCandidate>();
            aliases[alias] = resources;
        }

        resources.Add(resource);
    }

    private static bool IsAllowedDependencyResource(string relativePath) =>
        !ForbiddenDependencyDirectories.Any(directory => StartsWithDirectory(relativePath, directory));

    private static bool IsTextPath(string path) =>
        TextExtensions.Contains(Path.GetExtension(path));

    private static string ResourceGroup(string relativePath)
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

    private static string? AllPlatformsRelative(string archivePath)
    {
        string normalized = archivePath.Replace('/', '\\');
        const string marker = "\\DATA_ALL_PLATFORMS\\";
        int index = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? null : normalized[(index + marker.Length)..];
    }

    private static bool StartsWithDirectory(string path, string directory) =>
        path.Equals(directory, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(directory + "\\", StringComparison.OrdinalIgnoreCase);

    private static string SafeDirectoryName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string result = new(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return result.Trim().TrimEnd('.');
    }

    private static void AddWarning(
        ICollection<string> warnings,
        ISet<string> warningKeys,
        string warning)
    {
        if (warningKeys.Add(warning))
        {
            warnings.Add(warning);
        }
    }

    private sealed record RuntimeCandidate(
        string RelativePath,
        string PackageName,
        string WadName,
        int WadOrder,
        string StoragePath);
}
