using System.Text.RegularExpressions;

namespace DeckBuilder.GameData;

/// <summary>
/// Keeps Community WAD token helpers coherent when a workspace contains several extracted
/// versions. CW_Tokens uses a token archetype table stored in FUNCTIONS; choosing an older
/// duplicate of that table can leave a perfectly valid CARD_V2 unable to create its tokens.
/// </summary>
internal static class WorkspaceRuntimeCompatibility
{
    private static readonly Regex CwTokensCallRegex = new(
        "(?i)\\bCW_Tokens\\s*\\(\\s*[\"']([^\"'\\r\\n]+)[\"']",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CwGeneratedTokenRegex = new(
        @"(?i)\bTOKEN_([A-Za-z0-9_]+)_CW_[0-9]+\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlySet<string> ScanWorkspaceCwTokenKeys(
        string workspaceDirectory,
        CancellationToken cancellationToken)
    {
        string workspace = Path.GetFullPath(workspaceDirectory);
        HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(workspace))
            return keys;

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
            catch
            {
                continue;
            }

            foreach (DotpWadPackageManifest wad in manifest.Wads)
            {
                string wadDirectory = Path.Combine(packageDirectory, "wads", SafeDirectoryName(wad.Name));
                foreach (DotpWadFileManifest file in wad.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string? relative = GetAllPlatformsRelativePath(file.ArchivePath);
                    if (relative is null
                        || !StartsWithTree(relative, "CARDS")
                        || !relative.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string storagePath = Path.Combine(
                        wadDirectory,
                        file.StoragePath.Replace('/', Path.DirectorySeparatorChar));
                    AddCardFileKeys(storagePath, keys);
                }
            }
        }

        return keys;
    }

    /// <summary>
    /// Returns CW_Tokens requirements from exactly one effective CARD_V2 per reference. The winner
    /// ordering is intentionally identical to WorkspaceCardIndex's default resolution so every
    /// runtime subsystem agrees on which historical/overridden card version is active.
    /// </summary>
    public static IReadOnlySet<string> ScanEffectiveCardSetCwTokenKeys(
        WorkspaceContentVariantScanResult scan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scan);

        string[] effectiveCardPaths = scan.CardVariants
            .Where(variant => !string.IsNullOrWhiteSpace(variant.Reference))
            .GroupBy(variant => variant.Reference.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(candidate => candidate.IsRecommended ? 1 : 0)
                .ThenBy(candidate => candidate.WadOrder)
                .ThenBy(candidate => candidate.WadName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.PackageName, StringComparer.OrdinalIgnoreCase)
                .Last())
            .Select(variant => variant.StoragePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return ScanCardFilesCwTokenKeys(effectiveCardPaths, cancellationToken);
    }

    /// <summary>
    /// Scans only the supplied CARD_V2 files. This is used for effective-set and per-deck checks so
    /// stale copies elsewhere in a multi-version workspace cannot create false requirements.
    /// </summary>
    public static IReadOnlySet<string> ScanCardFilesCwTokenKeys(
        IEnumerable<string> cardPaths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cardPaths);

        HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);
        foreach (string storagePath in cardPaths
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Select(Path.GetFullPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddCardFileKeys(storagePath, keys);
        }

        return keys;
    }

    public static IReadOnlyList<string> ExtractCwTokenKeys(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<string>();

        HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in CwTokensCallRegex.Matches(text))
        {
            string key = match.Groups[1].Value.Trim();
            if (key.Length > 0)
                keys.Add(key);
        }

        // TOKEN_<archetype>_CW_N is the concrete token name generated by CW_Tokens. Cards may call
        // a shared helper instead of spelling CW_Tokens("...") locally, but TOKEN_REGISTRATION still
        // gives us the exact archetype that must exist in the Community WAD token table.
        foreach (Match match in CwGeneratedTokenRegex.Matches(text))
        {
            string key = match.Groups[1].Value.Trim();
            if (key.Length > 0)
                keys.Add(key);
        }

        return keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static int CountCwTokenCoverage(
        string relativePath,
        string storagePath,
        IReadOnlySet<string> requiredKeys)
    {
        if (requiredKeys.Count == 0
            || !IsCwTokensRuntimeResource(relativePath)
            || !IsTextRuntimeFile(relativePath)
            || !File.Exists(storagePath))
        {
            return 0;
        }

        try
        {
            string text = File.ReadAllText(storagePath);
            return requiredKeys.Count(key => ContainsTokenKey(text, key));
        }
        catch
        {
            return 0;
        }
    }

    public static IReadOnlySet<string> FindAvailableCwTokenKeys(
        IEnumerable<(string RelativePath, string StoragePath)> resources,
        IReadOnlySet<string> requiredKeys)
    {
        HashSet<string> available = new(StringComparer.OrdinalIgnoreCase);
        if (requiredKeys.Count == 0)
            return available;

        foreach ((string relativePath, string storagePath) in resources)
        {
            if (!IsCwTokensRuntimeResource(relativePath)
                || !IsTextRuntimeFile(relativePath)
                || !File.Exists(storagePath))
            {
                continue;
            }

            string text;
            try
            {
                text = File.ReadAllText(storagePath);
            }
            catch
            {
                continue;
            }

            foreach (string key in requiredKeys)
            {
                if (!available.Contains(key) && ContainsTokenKey(text, key))
                    available.Add(key);
            }

            if (available.Count == requiredKeys.Count)
                break;
        }

        return available;
    }

    public static bool IsDynamicCwRegistration(string registeredToken, IEnumerable<string> cwTokenKeys)
    {
        if (string.IsNullOrWhiteSpace(registeredToken))
            return false;

        foreach (string key in cwTokenKeys)
        {
            string prefix = "TOKEN_" + key + "_CW_";
            if (!registeredToken.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            string suffix = registeredToken[prefix.Length..];
            if (suffix.Length > 0 && suffix.All(char.IsDigit))
                return true;
        }

        return false;
    }

    private static void AddCardFileKeys(string storagePath, ISet<string> keys)
    {
        if (!File.Exists(storagePath))
            return;

        try
        {
            foreach (string key in ExtractCwTokenKeys(File.ReadAllText(storagePath)))
                keys.Add(key);
        }
        catch
        {
            // A malformed/unreadable unrelated card is handled by the normal scanners.
        }
    }

    private static bool IsCwTokensRuntimeResource(string relativePath)
    {
        if (!StartsWithTree(relativePath, "FUNCTIONS"))
            return false;

        return Path.GetFileNameWithoutExtension(relativePath)
            .Equals("CW_TOKENS", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsTokenKey(string text, string key)
    {
        int start = 0;
        while (start < text.Length)
        {
            int index = text.IndexOf(key, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return false;

            int before = index - 1;
            int after = index + key.Length;
            bool leftBoundary = before < 0 || !IsIdentifierCharacter(text[before]);
            bool rightBoundary = after >= text.Length || !IsIdentifierCharacter(text[after]);
            if (leftBoundary && rightBoundary)
                return true;

            start = index + key.Length;
        }

        return false;
    }

    private static bool IsIdentifierCharacter(char value) =>
        char.IsLetterOrDigit(value) || value == '_';

    private static bool IsTextRuntimeFile(string relativePath)
    {
        string extension = Path.GetExtension(relativePath);
        return extension.Equals(".LOL", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".LUA", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".TXT", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".XML", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".CSV", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".INI", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".JSON", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetAllPlatformsRelativePath(string archivePath)
    {
        string normalized = archivePath.Replace('/', '\\');
        const string marker = "DATA_ALL_PLATFORMS\\";
        int index = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? null : normalized[(index + marker.Length)..];
    }

    private static bool StartsWithTree(string path, string tree) =>
        path.Equals(tree, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(tree + "\\", StringComparison.OrdinalIgnoreCase);

    private static string SafeDirectoryName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string safe = new(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return safe.Trim().TrimEnd('.');
    }
}
