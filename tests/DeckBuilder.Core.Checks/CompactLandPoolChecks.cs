using System.Runtime.CompilerServices;
using DeckBuilder.Core.Models;
using DeckBuilder.Core.Services;
using DeckBuilder.GameData;

internal static class CompactLandPoolChecks
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        CardRecord whiteSpell = new(
            "WHITE_TEST_SPELL",
            "White test spell",
            "White test spell",
            "Creature",
            "TEST",
            "Tester",
            castingCost: "{W}",
            colour: "W");
        CardRecord blueSpell = new(
            "BLUE_TEST_SPELL",
            "Blue test spell",
            "Blue test spell",
            "Creature",
            "TEST",
            "Tester",
            castingCost: "{U}",
            colour: "U");
        CardRecord plainsA = BasicLand("PLAINS_CW_10581", "PLAINS", "Plains");
        CardRecord plainsB = BasicLand("PLAINS_CW_NEG_11", "PLAINS", "Plains");
        CardRecord plainsC = BasicLand("PLAINS_357845", "PLAINS", "Plains");
        CardRecord island = BasicLand("ISLAND_CW_10551", "ISLAND", "Island");
        CardRecord wakWak = new(
            "ISLAND_OF_WAKWAK_CW_989",
            "Island of Wak-Wak",
            "ISLAND_OF_WAKWAK",
            "Land",
            "XMAS",
            "Tester");
        CardRecord forest = BasicLand("FOREST_CW_NEG_29", "FOREST", "Forest");
        CardRecord[] catalog = [wakWak, plainsA, plainsB, plainsC, island, forest, whiteSpell, blueSpell];

        DeckDocument existingVariantDeck = new();
        existingVariantDeck.MainDeck.Add(new DeckEntry(whiteSpell, 4));
        existingVariantDeck.MainDeck.Add(new DeckEntry(plainsB, 7));

        IReadOnlyList<CardRecord> existingPool = DeckLandPoolSelector.Select(existingVariantDeck, catalog);
        Equal(4, existingPool.Count, "A mono-colour hidden land pool should keep four entries.");
        True(existingPool.All(card => ReferenceEquals(card, plainsB)),
            "The hidden land pool should repeat the most-used existing basic-land variant instead of importing extra art variants.");

        IReadOnlyList<string> portableReferences = PortableDeckCardReferencePlanner
            .GetRequiredReferences(existingVariantDeck, catalog);
        Equal(1, portableReferences.Count(reference => reference.StartsWith("PLAINS_", StringComparison.OrdinalIgnoreCase)),
            "Portable packaging should require only one Plains CARD_V2 when duplicate land-pool entries can reuse it.");

        DeckDocument noExistingVariantDeck = new();
        noExistingVariantDeck.MainDeck.Add(new DeckEntry(whiteSpell, 4));
        IReadOnlyList<CardRecord> fallbackPool = DeckLandPoolSelector.Select(noExistingVariantDeck, catalog);
        Equal(4, fallbackPool.Count, "Fallback hidden land pool should keep four entries.");
        True(fallbackPool.All(card => ReferenceEquals(card, plainsC)),
            "Without an existing basic land, the selector should deterministically choose a real metadata-defined Plains variant.");

        DeckDocument blueDeck = new();
        blueDeck.MainDeck.Add(new DeckEntry(blueSpell, 4));
        IReadOnlyList<CardRecord> bluePool = DeckLandPoolSelector.Select(blueDeck, catalog);
        Equal(4, bluePool.Count, "A mono-blue hidden land pool should keep four entries.");
        True(bluePool.All(card => ReferenceEquals(card, island)),
            "The hidden land pool must ignore Island of Wak-Wak even when its filename sorts before a real Island.");
        True(bluePool.All(CardLandClassification.IsBasicLand),
            "Every hidden automatic land-pool entry must be a real basic land.");

        Console.WriteLine("PASS: compact duplicate basic-land pool");
    }

    private static CardRecord BasicLand(string fileName, string englishName, string subtype) => new(
        fileName,
        subtype,
        englishName,
        $"Basic Land {subtype}",
        "XMAS",
        "Tester");

    private static void True(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message) where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message} Expected {expected}, got {actual}.");
    }
}
