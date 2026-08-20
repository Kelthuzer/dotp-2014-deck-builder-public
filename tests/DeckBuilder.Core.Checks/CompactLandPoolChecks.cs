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
        CardRecord plainsA = BasicLand("PLAINS_100", "Plains");
        CardRecord plainsB = BasicLand("PLAINS_200", "Plains");
        CardRecord plainsC = BasicLand("PLAINS_300", "Plains");
        CardRecord forest = BasicLand("FOREST_100", "Forest");
        CardRecord[] catalog = [plainsA, plainsB, plainsC, forest, whiteSpell];

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
        True(fallbackPool.All(card => ReferenceEquals(card, plainsA)),
            "Without an existing basic land, the selector should deterministically repeat the first catalog variant.");

        Console.WriteLine("PASS: compact duplicate basic-land pool");
    }

    private static CardRecord BasicLand(string fileName, string englishName) => new(
        fileName,
        englishName,
        englishName,
        $"Basic Land — {englishName}",
        "TEST",
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
