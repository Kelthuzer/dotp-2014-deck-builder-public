using System.Runtime.CompilerServices;
using DeckBuilder.GameData;

internal static class MusicCatalogChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"deck-builder-music-{Guid.NewGuid():N}");
        string musicDirectory = Path.Combine(directory, "Audio", "Music");
        Directory.CreateDirectory(musicDirectory);
        try
        {
            File.WriteAllBytes(Path.Combine(musicDirectory, "D14_Chandra.mp3"), Array.Empty<byte>());
            File.WriteAllBytes(Path.Combine(musicDirectory, "D14_Jace.MP3"), Array.Empty<byte>());
            File.WriteAllBytes(Path.Combine(musicDirectory, "ignore.wav"), Array.Empty<byte>());

            IReadOnlyList<string> music = GameMusicCatalogLoader.Load(directory);
            Equal(2, music.Count);
            True(music.Any(item => item.Equals("D14_Chandra", StringComparison.OrdinalIgnoreCase)), "Chandra music was not found.");
            True(music.Any(item => item.Equals("D14_Jace", StringComparison.OrdinalIgnoreCase)), "Jace music was not found.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
