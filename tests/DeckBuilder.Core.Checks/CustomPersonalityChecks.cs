using System.Runtime.CompilerServices;
using DeckBuilder.Core.Models;
using DeckBuilder.GameData;

internal static class CustomPersonalityChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"deck-builder-personality-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            CardRecord spell = new(
                "RED_TEST_SPELL",
                "Red test spell",
                "Red test spell",
                "Sorcery",
                "TEST",
                "Tester",
                "{R}",
                "R");
            CardRecord mountain = new(
                "MOUNTAIN_TEST",
                "Mountain",
                "Mountain",
                "Basic Land Mountain",
                "TEST",
                "Tester");
            AiPersonalityDefinition custom = new()
            {
                FileName = "D14_PERSONALITY_TEST_CUSTOM.XML",
                DisplayName = "Test Custom AI",
                NameTag = "PLAYER_NAME_TEST_CUSTOM",
                LargeAvatarImage = "TEST_FULL",
                SmallAvatarImage = "TEST_SMALL",
                SmallAvatarLockedImage = "TEST_LOCKED",
                LobbyImage = "TEST_LOBBY",
                Music = "TEST_MUSIC"
            };
            DeckDocument deck = new()
            {
                Name = "Custom AI deck",
                Description = "Personality export test",
                Personality = custom.FileName,
                CustomPersonality = custom
            };
            deck.MainDeck.Add(new DeckEntry(spell));

            string wadPath = Path.Combine(directory, "Data_Decks_100042_Custom_AI.wad");
            ModernWadExporter.Export(
                deck,
                new[] { spell, mountain },
                new ModernWadExportOptions(wadPath, 42, deck.Name, deck.Description));

            InstalledPersonalityRecord exportedPersonality = new GamePersonalityCatalogLoader()
                .LoadAsync(directory)
                .GetAwaiter()
                .GetResult()
                .Personalities
                .Single(personality => personality.FileName.Equals(custom.FileName, StringComparison.OrdinalIgnoreCase));
            Equal(custom.NameTag, exportedPersonality.NameTag);
            Equal(custom.LargeAvatarImage, exportedPersonality.LargeAvatarImage);
            Equal(custom.SmallAvatarImage, exportedPersonality.SmallAvatarImage);
            Equal(custom.SmallAvatarLockedImage, exportedPersonality.SmallAvatarLockedImage);
            Equal(custom.LobbyImage, exportedPersonality.LobbyImage);
            Equal(custom.Music, exportedPersonality.Music);

            InstalledDeckRecord exportedDeck = new GameDeckCatalogLoader()
                .LoadAsync(directory, new[] { spell, mountain })
                .GetAwaiter()
                .GetResult()
                .Decks
                .Single(item => item.Uid == 100042);
            Equal(custom.FileName, exportedDeck.Deck.Personality);
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
}
