using System.Runtime.CompilerServices;
using System.Xml.Linq;
using DeckBuilder.Core.Models;
using DeckBuilder.Core.Services;

internal static class AdvancedDeckMetadataChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        CheckWorkspaceRoundTrip();
        CheckLegacyWorkspaceFallback();
        CheckDotpXmlRoundTrip();
        CheckColourCalculation();
    }

    private static void CheckWorkspaceRoundTrip()
    {
        DeckDocument deck = CreateMetadataDeck();
        DeckWorkspace workspace = new("Metadata test", deck, Array.Empty<CardRecord>());
        string path = Path.Combine(Path.GetTempPath(), $"deck-builder-metadata-{Guid.NewGuid():N}.dotpdeck");
        try
        {
            DeckWorkspaceSerializer.SaveAsync(path, workspace).GetAwaiter().GetResult();
            DeckDocument loaded = DeckWorkspaceSerializer.LoadAsync(path).GetAwaiter().GetResult().Deck;
            AssertMetadata(loaded);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void CheckLegacyWorkspaceFallback()
    {
        string path = Path.Combine(Path.GetTempPath(), $"deck-builder-legacy-metadata-{Guid.NewGuid():N}.dotpdeck");
        try
        {
            File.WriteAllText(path, """
                {
                  "version": 1,
                  "name": "Legacy metadata",
                  "catalog": [],
                  "mainDeck": [],
                  "regularUnlocks": [],
                  "promoUnlocks": [],
                  "metadata": {
                    "uid": 100001,
                    "name": "Legacy metadata",
                    "description": "",
                    "personality": "",
                    "deckBoxImage": "",
                    "deckBoxImageLocked": "locked",
                    "contentPack": 0,
                    "alwaysAvailable": false
                  }
                }
                """);
            DeckDocument loaded = DeckWorkspaceSerializer.LoadAsync(path).GetAwaiter().GetResult().Deck;
            Equal(DeckAvailability.NeverAvailable, loaded.Availability);
            True(!loaded.OverrideColours, "Old workspace files must keep colour override disabled by default.");
            Equal(DeckColourFlags.NotDefined, loaded.OverrideColour);
            Equal("?", loaded.CreatureSize);
            Equal(-1, loaded.IgnoreCmcOver);
            True(loaded.CustomPersonality is null, "Old workspace files must not invent a custom personality.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void CheckDotpXmlRoundTrip()
    {
        DeckDocument deck = CreateMetadataDeck();
        string path = Path.Combine(Path.GetTempPath(), $"deck-builder-metadata-{Guid.NewGuid():N}.xml");
        try
        {
            DotpDeckXmlSerializer.Save(path, deck, 100042);
            XDocument xml = XDocument.Load(path);
            XElement root = xml.Root ?? throw new InvalidOperationException("The generated deck XML has no root element.");
            True(root.Attribute("always_available") is null, "Locked decks must not export always_available.");
            True(root.Attribute("never_available") is null, "Locked decks must not export never_available.");
            Equal("true", (string?)root.Attribute("is_black"));
            Equal("true", (string?)root.Attribute("is_blue"));
            True(root.Attribute("is_green") is null, "Unused override colours must not be exported.");
            True(root.Attribute("is_red") is null, "Unused override colours must not be exported.");
            True(root.Attribute("is_white") is null, "Unused override colours must not be exported.");
            Equal("D14_PERSONALITY_METADATA_CUSTOM.XML", (string?)root.Attribute("personality"));

            XElement colourOverride = root.Element("ColourOverride")
                ?? throw new InvalidOperationException("ColourOverride was not preserved in project XML.");
            Equal(((int)deck.OverrideColour).ToString(), (string?)colourOverride.Attribute("Value"));

            XElement statistics = root.Element("DECKSTATISTICS")
                ?? throw new InvalidOperationException("DECKSTATISTICS was not exported.");
            Equal("4", (string?)statistics.Attribute("Size"));
            Equal("5", (string?)statistics.Attribute("Syn"));
            XElement land = root.Element("LandConfig")
                ?? throw new InvalidOperationException("LandConfig was not exported.");
            Equal("7", (string?)land.Attribute("ignoreCmcOver"));
            Equal("5", (string?)land.Attribute("minSwamp"));
            Equal("2", (string?)land.Attribute("numSpellsThatCountAsLand"));

            XElement personality = root.Element("AiPersonality")
                ?? throw new InvalidOperationException("Custom personality was not embedded in unfinished XML.");
            Equal("PLAYER_NAME_METADATA_CUSTOM", (string?)personality.Element("PLANESWALKER_NAME_TAG")?.Attribute("string"));
            Equal("CHANDRA_SMALL", (string?)personality.Element("SMALL_AVATAR_IMAGE")?.Attribute("string"));
            Equal("CHANDRA_MUSIC", (string?)personality.Element("MUSIC")?.Attribute("string"));

            DeckDocument loaded = DotpDeckXmlSerializer.Load(path, Array.Empty<CardRecord>());
            AssertMetadata(loaded);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void CheckColourCalculation()
    {
        CardRecord black = new(
            "BLACK_CARD",
            "Black card",
            "Black card",
            "Creature",
            "TEST",
            "Tester",
            "{1}{B}",
            "B");
        CardRecord white = new(
            "WHITE_UNLOCK",
            "White unlock",
            "White unlock",
            "Creature",
            "TEST",
            "Tester",
            "{W}",
            "W");
        DeckDocument deck = new();
        deck.MainDeck.Add(new DeckEntry(black, 1, 1, false, 0));
        deck.RegularUnlocks.Add(new DeckEntry(white, 1, 1, false, 1));

        DeckColourFlags calculated = DeckColourCalculator.Calculate(deck);
        True(DeckColourCalculator.Has(calculated, DeckColourFlags.Black), "Main-deck colour must be detected.");
        True(DeckColourCalculator.Has(calculated, DeckColourFlags.White), "Unlock colour must participate in deck colour detection.");
        True(DeckColourCalculator.Has(calculated, DeckColourFlags.MultiColour), "Two colours must set the MultiColour flag.");

        DeckColourFlags colourless = DeckColourCalculator.FromSelections(false, false, false, false, false);
        Equal(DeckColourFlags.Colourless, colourless);
    }

    private static DeckDocument CreateMetadataDeck() => new()
    {
        Uid = 100042,
        Name = "Metadata test",
        Personality = "D14_PERSONALITY_METADATA_CUSTOM.XML",
        CustomPersonality = new AiPersonalityDefinition
        {
            FileName = "D14_PERSONALITY_METADATA_CUSTOM.XML",
            DisplayName = "Metadata AI",
            NameTag = "PLAYER_NAME_METADATA_CUSTOM",
            LargeAvatarImage = "CHANDRA_FULL",
            SmallAvatarImage = "CHANDRA_SMALL",
            SmallAvatarLockedImage = "CHANDRA_LOCKED",
            LobbyImage = "CHANDRA_LOBBY",
            Music = "CHANDRA_MUSIC"
        },
        Availability = DeckAvailability.Locked,
        OverrideColours = true,
        OverrideColour = DeckColourFlags.Black | DeckColourFlags.Blue | DeckColourFlags.MultiColour,
        CreatureSize = "4",
        DeckSpeed = "3",
        Flexibility = "2",
        Synergy = "5",
        IgnoreCmcOver = 7,
        MinForests = 2,
        MinIslands = 1,
        MinMountains = 3,
        MinPlains = 4,
        MinSwamps = 5,
        NumberOfSpellsThatCountAsLand = 2
    };

    private static void AssertMetadata(DeckDocument deck)
    {
        Equal(DeckAvailability.Locked, deck.Availability);
        True(deck.OverrideColours, "Colour override flag was not preserved.");
        Equal(DeckColourFlags.Black | DeckColourFlags.Blue | DeckColourFlags.MultiColour, deck.OverrideColour);
        Equal("4", deck.CreatureSize);
        Equal("3", deck.DeckSpeed);
        Equal("2", deck.Flexibility);
        Equal("5", deck.Synergy);
        Equal(7, deck.IgnoreCmcOver);
        Equal(2, deck.MinForests);
        Equal(1, deck.MinIslands);
        Equal(3, deck.MinMountains);
        Equal(4, deck.MinPlains);
        Equal(5, deck.MinSwamps);
        Equal(2, deck.NumberOfSpellsThatCountAsLand);
        Equal("D14_PERSONALITY_METADATA_CUSTOM.XML", deck.Personality);
        AiPersonalityDefinition personality = deck.CustomPersonality
            ?? throw new InvalidOperationException("Custom personality was not preserved.");
        Equal("D14_PERSONALITY_METADATA_CUSTOM.XML", personality.FileName);
        Equal("Metadata AI", personality.DisplayName);
        Equal("PLAYER_NAME_METADATA_CUSTOM", personality.NameTag);
        Equal("CHANDRA_FULL", personality.LargeAvatarImage);
        Equal("CHANDRA_SMALL", personality.SmallAvatarImage);
        Equal("CHANDRA_LOCKED", personality.SmallAvatarLockedImage);
        Equal("CHANDRA_LOBBY", personality.LobbyImage);
        Equal("CHANDRA_MUSIC", personality.Music);
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
