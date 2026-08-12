namespace DeckBuilder.GameData;

public static class GameMusicCatalogLoader
{
    public static IReadOnlyList<string> Load(string gameDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        string musicDirectory = Path.Combine(gameDirectory, "Audio", "Music");
        if (!Directory.Exists(musicDirectory))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateFiles(musicDirectory, "*.mp3", SearchOption.TopDirectoryOnly)
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
