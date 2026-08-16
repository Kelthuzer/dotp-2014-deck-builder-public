using System.Security.Cryptography;
using System.Text.Json;

namespace DeckBuilder.GameData;

public sealed record WorkspacePayloadRefreshResult(
    int PackageCount,
    int WadCount,
    int FilesScanned,
    int FilesAdded,
    int FilesRemoved,
    int FilesUpdated,
    int ManifestsUpdated);

/// <summary>
/// Reconciles extracted WAD payload directories with dotp-version.json when the user chooses
/// Reload unpacked. The payload on disk is authoritative: new files are added, missing files are
/// removed, and existing file metadata is refreshed from the current bytes before the manifest is
/// rewritten atomically. Unknown0C is preserved for known archive paths and inferred for new files.
/// Editor backup/temp files are deliberately excluded so they are never packed into a WAD.
/// </summary>
public sealed class WorkspacePayloadManifestRefresher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly GameVersionPackageService _packageService = new();

    public Task<WorkspacePayloadRefreshResult> RefreshAsync(
        string workspaceDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceDirectory);
        return Task.Run(() => Refresh(workspaceDirectory, cancellationToken), cancellationToken);
    }

    private WorkspacePayloadRefreshResult Refresh(
        string workspaceDirectory,
        CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(workspaceDirectory);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(root);
        }

        string[] manifestPaths = Directory.EnumerateFiles(
                root,
                GameVersionPackageService.ManifestFileName,
                SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (manifestPaths.Length == 0)
        {
            throw new InvalidDataException($"No extracted version packages were found below {root}.");
        }

        int wadCount = 0;
        int filesScanned = 0;
        int filesAdded = 0;
        int filesRemoved = 0;
        int filesUpdated = 0;
        int manifestsUpdated = 0;

        foreach (string manifestPath in manifestPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string packageDirectory = Path.GetDirectoryName(manifestPath)!;
            DotpVersionPackageManifest manifest = _packageService.ReadManifest(packageDirectory);
            List<DotpWadPackageManifest> updatedWads = new(manifest.Wads.Count);

            foreach (DotpWadPackageManifest wad in manifest.Wads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                wadCount++;

                string wadDirectory = Path.Combine(packageDirectory, "wads", SafeDirectoryName(wad.Name));
                string payloadDirectory = Path.Combine(wadDirectory, "payload");

                Dictionary<string, DotpWadFileManifest> existingByArchivePath = wad.Files
                    .GroupBy(file => NormalizeArchivePath(file.ArchivePath), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Last(),
                        StringComparer.OrdinalIgnoreCase);

                List<DotpWadFileManifest> refreshedFiles = new();
                HashSet<string> seenArchivePaths = new(StringComparer.OrdinalIgnoreCase);

                if (Directory.Exists(payloadDirectory))
                {
                    foreach (string storagePath in Directory.EnumerateFiles(
                                 payloadDirectory,
                                 "*",
                                 SearchOption.AllDirectories)
                             .Where(path => !IsWorkspaceMaintenanceFile(path))
                             .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        filesScanned++;

                        string archivePath = NormalizeArchivePath(
                            Path.GetRelativePath(payloadDirectory, storagePath)
                                .Replace(Path.DirectorySeparatorChar, '\\')
                                .Replace(Path.AltDirectorySeparatorChar, '\\'));
                        if (!seenArchivePaths.Add(archivePath))
                        {
                            continue;
                        }

                        string storageRelative = Path.GetRelativePath(wadDirectory, storagePath)
                            .Replace(Path.DirectorySeparatorChar, '/')
                            .Replace(Path.AltDirectorySeparatorChar, '/');
                        FileInfo info = new(storagePath);
                        string hash = HashFile(storagePath);

                        if (existingByArchivePath.TryGetValue(archivePath, out DotpWadFileManifest? existing))
                        {
                            if (!StoragePathEquals(existing.StoragePath, storageRelative)
                                || existing.OriginalSize != info.Length
                                || !existing.OriginalSha256.Equals(hash, StringComparison.OrdinalIgnoreCase)
                                || !existing.ArchivePath.Equals(archivePath, StringComparison.Ordinal))
                            {
                                filesUpdated++;
                            }

                            refreshedFiles.Add(new DotpWadFileManifest(
                                archivePath,
                                storageRelative,
                                info.Length,
                                hash,
                                existing.Unknown0C));
                        }
                        else
                        {
                            refreshedFiles.Add(new DotpWadFileManifest(
                                archivePath,
                                storageRelative,
                                info.Length,
                                hash,
                                GuessUnknown0C(wad.Files, archivePath)));
                            filesAdded++;
                        }
                    }
                }

                filesRemoved += existingByArchivePath.Keys.Count(path => !seenArchivePaths.Contains(path));

                updatedWads.Add(wad with
                {
                    Files = refreshedFiles
                        .OrderBy(file => file.ArchivePath, StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                });
            }

            DotpVersionPackageManifest updatedManifest = manifest with { Wads = updatedWads.ToArray() };
            WriteManifestAtomic(manifestPath, updatedManifest);
            manifestsUpdated++;
        }

        return new WorkspacePayloadRefreshResult(
            manifestPaths.Length,
            wadCount,
            filesScanned,
            filesAdded,
            filesRemoved,
            filesUpdated,
            manifestsUpdated);
    }

    private static uint GuessUnknown0C(
        IReadOnlyList<DotpWadFileManifest> existing,
        string archivePath)
    {
        string extension = Path.GetExtension(archivePath);
        string directory = Path.GetDirectoryName(archivePath) ?? string.Empty;

        DotpWadFileManifest? sameDirectory = existing.LastOrDefault(file =>
            Path.GetExtension(file.ArchivePath).Equals(extension, StringComparison.OrdinalIgnoreCase)
            && (Path.GetDirectoryName(file.ArchivePath) ?? string.Empty)
                .Equals(directory, StringComparison.OrdinalIgnoreCase));
        if (sameDirectory is not null)
        {
            return sameDirectory.Unknown0C;
        }

        DotpWadFileManifest? sameExtension = existing.LastOrDefault(file =>
            Path.GetExtension(file.ArchivePath).Equals(extension, StringComparison.OrdinalIgnoreCase));
        return sameExtension?.Unknown0C ?? 0;
    }

    private static void WriteManifestAtomic(
        string manifestPath,
        DotpVersionPackageManifest manifest)
    {
        string temporary = $"{manifestPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(manifest, JsonOptions));
            File.Move(temporary, manifestPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static bool IsWorkspaceMaintenanceFile(string path)
    {
        string name = Path.GetFileName(path);
        return name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("~", StringComparison.Ordinal);
    }

    private static bool StoragePathEquals(string left, string right) =>
        left.Replace('\\', '/').Equals(right.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeArchivePath(string value) =>
        value.Replace('/', '\\').TrimStart('\\');

    private static string HashFile(string path)
    {
        using FileStream input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private static string SafeDirectoryName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string result = new(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return result.Trim().TrimEnd('.');
    }
}
