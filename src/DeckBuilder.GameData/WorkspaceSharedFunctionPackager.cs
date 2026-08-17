namespace DeckBuilder.GameData;

/// <summary>
/// Copies the effective DATA_ALL_PLATFORMS/FUNCTIONS payload from an extracted workspace into a
/// support-WAD staging tree. Community WAD cards frequently delegate mechanics to CW_*/RSN_* LOL
/// functions; packaging the CARD_V2 alone is therefore not sufficient for a portable deck.
/// </summary>
internal static class WorkspaceSharedFunctionPackager
{
    public static int CopyIntoStaging(
        string? workspaceDirectory,
        string stagingDirectory,
        ICollection<string> warnings,
        ISet<string> warningKeys,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workspaceDirectory)
            || !Directory.Exists(workspaceDirectory))
        {
            return 0;
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
            return 0;
        }

        GameVersionPackageService packageService = new();
        List<FunctionCandidate> candidates = new();

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
                    $"Could not inspect shared FUNCTIONS in {packageDirectory}: {exception.Message}");
                continue;
            }

            foreach (DotpWadPackageManifest wad in manifest.Wads)
            {
                string wadDirectory = Path.Combine(packageDirectory, "wads", SafeDirectoryName(wad.Name));
                foreach (DotpWadFileManifest file in wad.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string? relative = AllPlatformsRelative(file.ArchivePath);
                    if (relative is null
                        || !StartsWithDirectory(relative, "FUNCTIONS"))
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
                            $"Shared function payload {relative} from {manifest.VersionName} / {wad.Name} is missing from the extracted workspace.");
                        continue;
                    }

                    candidates.Add(new FunctionCandidate(
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
            return 0;
        }

        // Match the rest of the workspace merger: later WAD order/name/package wins. Copy the
        // complete effective FUNCTIONS tree instead of trying to parse Lua-to-Lua dependencies;
        // CW_TOKENS.LOL, for example, relies on shared tables/constants maintained in other LOLs.
        FunctionCandidate[] selected = candidates
            .GroupBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(item => item.WadOrder)
                .ThenBy(item => item.WadName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.PackageName, StringComparer.OrdinalIgnoreCase)
                .Last())
            .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (FunctionCandidate function in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string target = Path.Combine(
                stagingDirectory,
                "DATA_ALL_PLATFORMS",
                function.RelativePath.Replace('\\', Path.DirectorySeparatorChar));
            string? targetDirectory = Path.GetDirectoryName(target);
            if (!string.IsNullOrWhiteSpace(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }
            File.Copy(function.StoragePath, target, overwrite: true);
        }

        return selected.Length;
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

    private sealed record FunctionCandidate(
        string RelativePath,
        string PackageName,
        string WadName,
        int WadOrder,
        string StoragePath);
}
