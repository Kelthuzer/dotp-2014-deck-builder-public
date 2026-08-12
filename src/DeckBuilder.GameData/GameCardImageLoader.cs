using System.Collections.Concurrent;
using Gibbed.Duels.FileFormats;
using Gibbed.IO;
using ICSharpCode.SharpZipLib.Zip.Compression.Streams;
using Wad = Gibbed.Duels.FileFormats.Wad;

namespace DeckBuilder.GameData;

public enum GameImageKind
{
    Illustration,
    Frame,
    Texture,
    Mana,
    Deck,
    Personality
}

public sealed class GameCardImageLoader
{
    private static readonly IReadOnlyDictionary<GameImageKind, string> ImageDirectories =
        new Dictionary<GameImageKind, string>
        {
            [GameImageKind.Illustration] = "DATA_ALL_PLATFORMS\\ART_ASSETS\\ILLUSTRATIONS",
            [GameImageKind.Frame] = "DATA_ALL_PLATFORMS\\ART_ASSETS\\TEXTURES\\CARD_FRAMES",
            [GameImageKind.Texture] = "DATA_ALL_PLATFORMS\\ART_ASSETS\\TEXTURES",
            [GameImageKind.Mana] = "DATA_ALL_PLATFORMS\\ART_ASSETS\\TEXTURES\\MANA",
            [GameImageKind.Deck] = "DATA_ALL_PLATFORMS\\ART_ASSETS\\TEXTURES\\DECKS",
            [GameImageKind.Personality] = "DATA_ALL_PLATFORMS\\ART_ASSETS\\TEXTURES\\PLANESWALKERS"
        };

    private readonly string _gameDirectory;
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private readonly ConcurrentDictionary<ImageKey, Task<CardImageData?>> _cache = new();
    private Dictionary<ImageKey, ImageLocation>? _locations;

    public GameCardImageLoader(string gameDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        _gameDirectory = gameDirectory;
    }

    public string GameDirectory => _gameDirectory;

    public Task<CardImageData?> LoadAsync(string? imageId) => LoadAsync(imageId, GameImageKind.Illustration);

    public Task<CardImageData?> LoadAsync(string? imageId, GameImageKind kind)
    {
        string id = NormalizeImageId(imageId);
        ImageKey key = new(kind, id);
        return id.Length == 0
            ? Task.FromResult<CardImageData?>(null)
            : _cache.GetOrAdd(key, LoadCoreAsync);
    }

    public async Task<IReadOnlyList<string>> GetImageIdsAsync(GameImageKind kind)
    {
        IReadOnlyDictionary<ImageKey, ImageLocation> locations = await GetLocationsAsync();
        if (kind == GameImageKind.Deck)
        {
            return locations.Keys
                .Where(key => key.Kind == GameImageKind.Deck
                    || (key.Kind == GameImageKind.Texture && IsDeckImageCandidate(key.Id)))
                .Select(key => key.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return locations.Keys
            .Where(key => key.Kind == kind)
            .Select(key => key.Id)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<CardImageData?> LoadCoreAsync(ImageKey key)
    {
        IReadOnlyDictionary<ImageKey, ImageLocation> locations = await GetLocationsAsync();
        if (!locations.TryGetValue(key, out ImageLocation? location))
        {
            if (key.Kind != GameImageKind.Deck
                || !IsDeckImageCandidate(key.Id)
                || !locations.TryGetValue(new ImageKey(GameImageKind.Texture, key.Id), out location))
            {
                return null;
            }
        }

        byte[] data;
        try
        {
            data = await Task.Run(location.Read);
        }
        catch (Exception exception)
        {
            throw new IOException(
                $"Could not read {key.Kind.ToString().ToLowerInvariant()} {key.Id} from {location.Description}.",
                exception);
        }

        try
        {
            return await Task.Run(() => TdxImageDecoder.Decode(data));
        }
        catch (Exception exception)
        {
            throw new InvalidDataException(
                $"Could not decode {key.Kind.ToString().ToLowerInvariant()} {key.Id} from {location.Description}.",
                exception);
        }
    }

    private async Task<IReadOnlyDictionary<ImageKey, ImageLocation>> GetLocationsAsync()
    {
        if (_locations is not null)
        {
            return _locations;
        }

        await _indexLock.WaitAsync();
        try
        {
            _locations ??= await Task.Run(BuildIndex);
            return _locations;
        }
        finally
        {
            _indexLock.Release();
        }
    }

    private Dictionary<ImageKey, ImageLocation> BuildIndex()
    {
        Dictionary<ImageKey, ImageLocation> result = new();

        if (WorkspaceContentWadBuilder.IsWorkspaceRoot(_gameDirectory))
        {
            IndexWorkspace(_gameDirectory, result);
            return result;
        }

        foreach (string wadPath in Directory.EnumerateFiles(_gameDirectory, "*.wad", SearchOption.TopDirectoryOnly)
                     .Where(IsGameWad)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                IndexWad(wadPath, result);
            }
            catch
            {
            }
        }

        foreach (string directory in FindUnpackedWads(_gameDirectory))
        {
            IndexLooseWad(directory, result);
        }

        return result;
    }

    private static void IndexLooseWad(string directory, IDictionary<ImageKey, ImageLocation> result)
    {
        HashSet<string> classified = new(StringComparer.OrdinalIgnoreCase);
        foreach ((GameImageKind kind, string relativeDirectory) in ImageDirectories)
        {
            string imageDirectory = Path.Combine(
                directory,
                relativeDirectory.Replace('\\', Path.DirectorySeparatorChar));
            if (!Directory.Exists(imageDirectory))
            {
                continue;
            }

            foreach (string path in Directory.EnumerateFiles(imageDirectory, "*.tdx", SearchOption.AllDirectories))
            {
                string id = NormalizeImageId(path);
                result[new ImageKey(kind, id)] = new LooseImageLocation(path);
                if (kind == GameImageKind.Texture && IsDeckImageCandidate(id))
                {
                    result.TryAdd(new ImageKey(GameImageKind.Deck, id), new LooseImageLocation(path));
                }
                classified.Add(Path.GetFullPath(path));
            }
        }

        foreach (string path in Directory.EnumerateFiles(directory, "*.tdx", SearchOption.AllDirectories))
        {
            if (classified.Contains(Path.GetFullPath(path)))
            {
                continue;
            }

            string id = NormalizeImageId(path);
            LooseImageLocation location = new(path);
            result.TryAdd(new ImageKey(GameImageKind.Illustration, id), location);
            if (IsDeckImageCandidate(id))
            {
                result.TryAdd(new ImageKey(GameImageKind.Deck, id), location);
            }
        }
    }

    private static void IndexWorkspace(string workspaceDirectory, IDictionary<ImageKey, ImageLocation> result)
    {
        GameVersionPackageService packageService = new();
        List<WorkspaceImageCandidate> candidates = new();
        foreach (string manifestPath in Directory.EnumerateFiles(
                     workspaceDirectory,
                     GameVersionPackageService.ManifestFileName,
                     SearchOption.AllDirectories))
        {
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
                    string normalizedArchivePath = file.ArchivePath.Replace('/', '\\');
                    if (!normalizedArchivePath.EndsWith(".tdx", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    ImageKey key = TryWorkspaceImageKey(file.ArchivePath, out ImageKey classifiedKey)
                        ? classifiedKey
                        : new ImageKey(GameImageKind.Illustration, NormalizeImageId(file.ArchivePath));

                    string storagePath = Path.Combine(
                        wadDirectory,
                        file.StoragePath.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(storagePath))
                    {
                        continue;
                    }

                    candidates.Add(new WorkspaceImageCandidate(
                        key,
                        wad.PrimaryOrder,
                        wad.Name,
                        manifest.VersionName,
                        storagePath));

                    string imageId = NormalizeImageId(file.ArchivePath);
                    if (key.Kind != GameImageKind.Deck
                        && IsWorkspaceDeckImageCandidate(normalizedArchivePath, imageId))
                    {
                        candidates.Add(new WorkspaceImageCandidate(
                            new ImageKey(GameImageKind.Deck, imageId),
                            wad.PrimaryOrder,
                            wad.Name,
                            manifest.VersionName,
                            storagePath));
                    }
                }
            }
        }

        foreach (WorkspaceImageCandidate candidate in candidates
                     .OrderBy(item => item.Order)
                     .ThenBy(item => item.WadName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.PackageName, StringComparer.OrdinalIgnoreCase))
        {
            result[candidate.Key] = new LooseImageLocation(candidate.StoragePath);
        }
    }

    private static bool TryWorkspaceImageKey(string archivePath, out ImageKey key)
    {
        key = default;
        string normalized = archivePath.Replace('/', '\\').TrimStart('\\');
        if (!normalized.EndsWith(".tdx", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        const string allPlatforms = "DATA_ALL_PLATFORMS\\";
        int markerIndex = normalized.IndexOf(allPlatforms, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return false;
        }

        string relative = normalized[(markerIndex + allPlatforms.Length)..];
        int slash = relative.LastIndexOf('\\');
        if (slash <= 0)
        {
            return false;
        }

        string parent = relative[..slash].TrimEnd('\\');
        foreach ((GameImageKind kind, string directoryPath) in ImageDirectories
                     .OrderByDescending(item => item.Value.Length))
        {
            string expected = directoryPath.StartsWith(allPlatforms, StringComparison.OrdinalIgnoreCase)
                ? directoryPath[allPlatforms.Length..]
                : directoryPath;
            expected = expected.TrimEnd('\\');

            if (!parent.Equals(expected, StringComparison.OrdinalIgnoreCase)
                && !parent.StartsWith(expected + "\\", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            key = new ImageKey(kind, NormalizeImageId(relative[(slash + 1)..]));
            return key.Id.Length > 0;
        }

        return false;
    }

    private static bool IsWorkspaceDeckImageCandidate(string archivePath, string id)
    {
        string normalized = archivePath.Replace('/', '\\').ToUpperInvariant();
        if (!normalized.Contains("\\ART_ASSETS\\TEXTURES\\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return IsDeckImageCandidate(id);
    }

    private static bool IsDeckImageCandidate(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        string normalized = id.ToUpperInvariant();
        string[] excludedPrefixes =
        {
            "DECKSTATS_",
            "DECKS_PIPS_",
            "DECK_MANAGER_",
            "LOCKED",
            "UNLOCK_DECK_"
        };
        if (excludedPrefixes.Any(prefix => normalized.StartsWith(prefix, StringComparison.Ordinal)))
        {
            return false;
        }

        if (normalized.StartsWith("DECKBOX_PERSONA_", StringComparison.Ordinal))
        {
            return false;
        }

        return normalized.Contains("DECK_IMAGE", StringComparison.Ordinal)
            || normalized.Contains("DECKIMAGE", StringComparison.Ordinal)
            || normalized.EndsWith("_DECK", StringComparison.Ordinal);
    }

    private static void IndexWad(string wadPath, IDictionary<ImageKey, ImageLocation> result)
    {
        using FileStream input = File.OpenRead(wadPath);
        if (WadFile.IsBadHeader(input, out _, out _, out _))
        {
            return;
        }

        input.Position = 0;
        WadFile archive = new();
        archive.Deserialize(input);
        bool compressed = (archive.Flags & Wad.ArchiveFlags.HasCompressedFiles) == Wad.ArchiveFlags.HasCompressedFiles;

        foreach ((GameImageKind kind, string directoryPath) in ImageDirectories)
        {
            Wad.DirectoryEntry? directory = FindDirectory(archive.Directories, directoryPath);
            if (directory is null)
            {
                continue;
            }

            IndexWadDirectory(wadPath, archive, directory, kind, compressed, result);
        }

        foreach (Wad.DirectoryEntry root in archive.Directories)
        {
            IndexWadIllustrationFallback(wadPath, archive, root, compressed, result);
        }
    }

    private static void IndexWadDirectory(
        string wadPath,
        WadFile archive,
        Wad.DirectoryEntry directory,
        GameImageKind kind,
        bool compressed,
        IDictionary<ImageKey, ImageLocation> result)
    {
        foreach (Wad.FileEntry file in directory.Files.Where(file =>
                     Path.GetExtension(file.Name).Equals(".tdx", StringComparison.OrdinalIgnoreCase)))
        {
            uint offset = archive.DataOffsets[file.OffsetIndex];
            string id = NormalizeImageId(file.Name);
            WadImageLocation location = new(wadPath, offset, checked((int)file.Size), compressed, archive.Endian);
            result.TryAdd(new ImageKey(kind, id), location);
            if (kind == GameImageKind.Texture && IsDeckImageCandidate(id))
            {
                result.TryAdd(new ImageKey(GameImageKind.Deck, id), location);
            }
        }

        foreach (Wad.DirectoryEntry child in directory.Directories)
        {
            IndexWadDirectory(wadPath, archive, child, kind, compressed, result);
        }
    }

    private static void IndexWadIllustrationFallback(
        string wadPath,
        WadFile archive,
        Wad.DirectoryEntry directory,
        bool compressed,
        IDictionary<ImageKey, ImageLocation> result)
    {
        foreach (Wad.FileEntry file in directory.Files.Where(file =>
                     Path.GetExtension(file.Name).Equals(".tdx", StringComparison.OrdinalIgnoreCase)))
        {
            string id = NormalizeImageId(file.Name);
            ImageKey key = new(GameImageKind.Illustration, id);
            if (!result.ContainsKey(key))
            {
                uint offset = archive.DataOffsets[file.OffsetIndex];
                result[key] = new WadImageLocation(
                    wadPath,
                    offset,
                    checked((int)file.Size),
                    compressed,
                    archive.Endian);
            }
        }

        foreach (Wad.DirectoryEntry child in directory.Directories)
        {
            IndexWadIllustrationFallback(wadPath, archive, child, compressed, result);
        }
    }

    private static IEnumerable<string> FindUnpackedWads(string gameDirectory)
    {
        if (Directory.Exists(Path.Combine(gameDirectory, "DATA_ALL_PLATFORMS")))
        {
            yield return gameDirectory;
        }

        foreach (string directory in Directory.EnumerateDirectories(gameDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            if (IsGameWad(directory)
                && Directory.Exists(Path.Combine(directory, "DATA_ALL_PLATFORMS")))
            {
                yield return directory;
            }
        }
    }

    private static bool IsGameWad(string path) => GameWadSelection.IsSupported(path);

    private static string NormalizeImageId(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : Path.GetFileNameWithoutExtension(value.Trim()).ToUpperInvariant();

    private static string SafeDirectoryName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string result = new(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return result.Trim().TrimEnd('.');
    }

    private readonly record struct ImageKey(GameImageKind Kind, string Id)
    {
        public bool Equals(ImageKey other) => Kind == other.Kind
            && Id.Equals(other.Id, StringComparison.OrdinalIgnoreCase);

        public override int GetHashCode() => HashCode.Combine(Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(Id));
    }

    private sealed record WorkspaceImageCandidate(
        ImageKey Key,
        int Order,
        string WadName,
        string PackageName,
        string StoragePath);

    private static Wad.DirectoryEntry? FindDirectory(IEnumerable<Wad.DirectoryEntry> directories, string path)
    {
        string normalized = path.TrimEnd('\\');
        string[] parts = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        string target = parts[^1];
        foreach (Wad.DirectoryEntry directory in directories)
        {
            if (directory.Name.Equals(target, StringComparison.OrdinalIgnoreCase))
            {
                bool found = true;
                Wad.DirectoryEntry? parent = directory.ParentDirectory;
                for (int index = parts.Length - 2; index >= 0; index--)
                {
                    if (parent is null || !parent.Name.Equals(parts[index], StringComparison.OrdinalIgnoreCase))
                    {
                        found = false;
                        break;
                    }

                    parent = parent.ParentDirectory;
                }

                if (found)
                {
                    return directory;
                }
            }

            Wad.DirectoryEntry? nested = FindDirectory(directory.Directories, normalized);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private abstract record ImageLocation
    {
        public abstract string Description { get; }

        public abstract byte[] Read();
    }

    private sealed record LooseImageLocation(string Path) : ImageLocation
    {
        public override string Description => $"loose TDX '{Path}'";

        public override byte[] Read() => File.ReadAllBytes(Path);
    }

    private sealed record WadImageLocation(
        string Path,
        uint Offset,
        int StoredLength,
        bool Compressed,
        Endian Endian) : ImageLocation
    {
        public override string Description => $"WAD '{Path}' at offset {Offset}";

        public override byte[] Read()
        {
            using FileStream input = File.OpenRead(Path);
            input.Position = Offset;
            if (!Compressed)
            {
                return ReadExactly(input, StoredLength);
            }

            int inflatedLength = input.ReadValueS32(Endian);
            int payloadLength = StoredLength - 4;
            if (inflatedLength == -1)
            {
                return ReadExactly(input, payloadLength);
            }

            using MemoryStream compressedData = new(ReadExactly(input, payloadLength), writable: false);
            using InflaterInputStream inflater = new(compressedData);
            return ReadExactly(inflater, inflatedLength);
        }

        private static byte[] ReadExactly(Stream input, int length)
        {
            byte[] result = new byte[length];
            int offset = 0;
            while (offset < result.Length)
            {
                int read = input.Read(result, offset, result.Length - offset);
                if (read == 0)
                {
                    throw new EndOfStreamException($"Expected {length} bytes, received {offset}.");
                }

                offset += read;
            }

            return result;
        }
    }
}
