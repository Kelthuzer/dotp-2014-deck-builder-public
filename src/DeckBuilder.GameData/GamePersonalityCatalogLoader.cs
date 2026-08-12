using System.Collections.Concurrent;
using System.Text;
using System.Xml.Linq;
using Gibbed.Duels.FileFormats;
using Gibbed.IO;
using ICSharpCode.SharpZipLib.Zip.Compression.Streams;
using Wad = Gibbed.Duels.FileFormats.Wad;

namespace DeckBuilder.GameData;

public sealed record InstalledPersonalityRecord(
    string FileName,
    string Source,
    string NameTag,
    string DisplayName,
    string LargeAvatarImage,
    string SmallAvatarImage,
    string SmallAvatarLockedImage,
    string LobbyImage,
    string Music);

public sealed record GamePersonalityCatalogLoadResult(
    IReadOnlyList<InstalledPersonalityRecord> Personalities,
    IReadOnlyList<string> Warnings);

public sealed class GamePersonalityCatalogLoader
{
    private const string PersonalityDirectory = "DATA_ALL_PLATFORMS\\AI_PERSONALITIES";

    public async Task<GamePersonalityCatalogLoadResult> LoadAsync(
        string gameDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        if (!Directory.Exists(gameDirectory))
        {
            throw new DirectoryNotFoundException(gameDirectory);
        }

        return await Task.Run(() => Load(gameDirectory, cancellationToken), cancellationToken);
    }

    private static GamePersonalityCatalogLoadResult Load(string gameDirectory, CancellationToken cancellationToken)
    {
        Dictionary<string, InstalledPersonalityRecord> personalities = new(StringComparer.OrdinalIgnoreCase);
        ConcurrentBag<string> warnings = new();

        foreach (string wadPath in Directory.EnumerateFiles(gameDirectory, "*.wad", SearchOption.TopDirectoryOnly)
                     .Where(IsGameWad)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                ReadWad(wadPath, personalities, warnings, cancellationToken);
            }
            catch (Exception exception)
            {
                warnings.Add($"{FileName(wadPath)} personalities: {exception.Message}");
            }
        }

        foreach (string directory in FindUnpackedWads(gameDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string personalityDirectory = Path.Combine(directory, "DATA_ALL_PLATFORMS", "AI_PERSONALITIES");
            foreach (string path in Directory.EnumerateFiles(personalityDirectory, "*.xml", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    InstalledPersonalityRecord? personality = Parse(
                        File.ReadAllText(path),
                        Path.GetFileName(path) ?? path,
                        FileName(directory));
                    AddOrPrefer(personalities, personality);
                }
                catch (Exception exception)
                {
                    warnings.Add($"{path}: {exception.Message}");
                }
            }
        }

        InstalledPersonalityRecord[] result = personalities.Values
            .OrderBy(personality => personality.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(personality => personality.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new GamePersonalityCatalogLoadResult(
            result,
            warnings.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static void ReadWad(
        string wadPath,
        IDictionary<string, InstalledPersonalityRecord> personalities,
        ConcurrentBag<string> warnings,
        CancellationToken cancellationToken)
    {
        using FileStream input = File.OpenRead(wadPath);
        if (WadFile.IsBadHeader(input, out _, out _, out string reason))
        {
            throw new InvalidDataException(reason);
        }

        input.Position = 0;
        WadFile archive = new();
        archive.Deserialize(input);
        bool compressed = (archive.Flags & Wad.ArchiveFlags.HasCompressedFiles) == Wad.ArchiveFlags.HasCompressedFiles;
        Wad.DirectoryEntry? directory = FindDirectory(archive.Directories, PersonalityDirectory);
        if (directory is null)
        {
            return;
        }

        string source = Path.GetFileNameWithoutExtension(wadPath) ?? wadPath;
        foreach (Wad.FileEntry file in directory.Files.Where(file =>
                     Path.GetExtension(file.Name).Equals(".xml", StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                InstalledPersonalityRecord? personality = Parse(
                    DecodeText(ReadFile(input, archive, file, compressed)),
                    file.Name,
                    source);
                AddOrPrefer(personalities, personality);
            }
            catch (Exception exception)
            {
                warnings.Add($"{source}\\{file.Name}: {exception.Message}");
            }
        }
    }

    private static InstalledPersonalityRecord? Parse(string xml, string fileName, string source)
    {
        XDocument document = XDocument.Parse(xml, LoadOptions.None);
        XElement? root = document.Root?.Name.LocalName.Equals("CONFIG", StringComparison.OrdinalIgnoreCase) == true
            ? document.Root
            : document.Descendants().FirstOrDefault(element =>
                element.Name.LocalName.Equals("CONFIG", StringComparison.OrdinalIgnoreCase));
        if (root is null)
        {
            return null;
        }

        string cleanFileName = Path.GetFileName(fileName) ?? fileName;
        string nameTag = ChildString(root, "PLANESWALKER_NAME_TAG");
        string fallbackName = Path.GetFileNameWithoutExtension(cleanFileName) ?? cleanFileName;
        string displayName = Localized(root, "ru-RU")
            ?? Localized(root, "en-US")
            ?? (string.IsNullOrWhiteSpace(nameTag) ? fallbackName : nameTag);

        return new InstalledPersonalityRecord(
            cleanFileName,
            source,
            nameTag,
            displayName,
            ChildString(root, "LARGE_AVATAR_IMAGE"),
            ChildString(root, "SMALL_AVATAR_IMAGE"),
            ChildString(root, "SMALL_AVATAR_IMAGE_LOCKED"),
            ChildString(root, "LOBBY_IMAGE"),
            ChildString(root, "MUSIC"));
    }

    private static void AddOrPrefer(
        IDictionary<string, InstalledPersonalityRecord> personalities,
        InstalledPersonalityRecord? candidate)
    {
        if (candidate is null || string.IsNullOrWhiteSpace(candidate.FileName))
        {
            return;
        }

        if (!personalities.TryGetValue(candidate.FileName, out InstalledPersonalityRecord? existing)
            || DefinitionScore(candidate) > DefinitionScore(existing))
        {
            personalities[candidate.FileName] = candidate;
        }
    }

    private static int DefinitionScore(InstalledPersonalityRecord personality)
    {
        int score = 0;
        if (!string.IsNullOrWhiteSpace(personality.LargeAvatarImage)) score += 20;
        if (!string.IsNullOrWhiteSpace(personality.SmallAvatarImage)) score += 15;
        if (!string.IsNullOrWhiteSpace(personality.SmallAvatarLockedImage)) score += 5;
        if (!string.IsNullOrWhiteSpace(personality.LobbyImage)) score += 5;
        if (!string.IsNullOrWhiteSpace(personality.NameTag)) score += 5;
        if (!string.IsNullOrWhiteSpace(personality.Music)) score += 2;
        return score;
    }

    private static string ChildString(XElement root, string name)
    {
        XElement? element = root.Elements().FirstOrDefault(child =>
            child.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
        return Attribute(element, "string");
    }

    private static string? Localized(XElement root, string language)
    {
        XElement? names = root.Elements().FirstOrDefault(child =>
            child.Name.LocalName.Equals("LocalizedNames", StringComparison.OrdinalIgnoreCase));
        string? value = names?.Elements()
            .Where(element => element.Name.LocalName.Equals("LOCALISED_TEXT", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(element => Attribute(element, "LanguageCode").Equals(language, StringComparison.OrdinalIgnoreCase))
            ?.Value.Trim();
        return IsUsableText(value) ? value : null;
    }

    private static bool IsUsableText(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Any(character => !char.IsWhiteSpace(character) && character != '?' && character != '\uFFFD');

    private static string Attribute(XElement? element, string name) => element?.Attributes()
        .FirstOrDefault(attribute => attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?.Value.Trim() ?? string.Empty;

    private static IEnumerable<string> FindUnpackedWads(string gameDirectory)
    {
        if (Directory.Exists(Path.Combine(gameDirectory, "DATA_ALL_PLATFORMS", "AI_PERSONALITIES")))
        {
            yield return gameDirectory;
        }

        foreach (string directory in Directory.EnumerateDirectories(gameDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            if (IsGameWad(directory)
                && Directory.Exists(Path.Combine(directory, "DATA_ALL_PLATFORMS", "AI_PERSONALITIES")))
            {
                yield return directory;
            }
        }
    }

    private static bool IsGameWad(string path)
    {
        string name = Path.GetFileName(path) ?? path;
        return name.StartsWith("data_core", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("data_dlc_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("data_decks_", StringComparison.OrdinalIgnoreCase);
    }

    private static string FileName(string path) => Path.GetFileName(path) ?? path;

    private static byte[] ReadFile(FileStream input, WadFile archive, Wad.FileEntry file, bool compressed)
    {
        input.Position = archive.DataOffsets[file.OffsetIndex];
        if (!compressed)
        {
            return ReadExactly(input, checked((int)file.Size));
        }

        int inflatedLength = input.ReadValueS32(archive.Endian);
        int storedLength = checked((int)file.Size) - 4;
        if (inflatedLength == -1)
        {
            return ReadExactly(input, storedLength);
        }

        using MemoryStream compressedData = new(ReadExactly(input, storedLength), writable: false);
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

    private static string DecodeText(byte[] data)
    {
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(data, 3, data.Length - 3);
        }

        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(data, 2, data.Length - 2);
        }

        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
        }

        return Encoding.UTF8.GetString(data);
    }

    private static Wad.DirectoryEntry? FindDirectory(IEnumerable<Wad.DirectoryEntry> directories, string path)
    {
        string[] parts = path.TrimEnd('\\').Split('\\');
        foreach (Wad.DirectoryEntry root in directories)
        {
            if (!root.Name.Equals(parts[0], StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Wad.DirectoryEntry? current = root;
            for (int index = 1; index < parts.Length && current is not null; index++)
            {
                current = current.Directories.FirstOrDefault(directory =>
                    directory.Name.Equals(parts[index], StringComparison.OrdinalIgnoreCase));
            }

            if (current is not null)
            {
                return current;
            }
        }

        foreach (Wad.DirectoryEntry directory in directories)
        {
            Wad.DirectoryEntry? found = FindDirectory(directory.Directories, path);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }
}
