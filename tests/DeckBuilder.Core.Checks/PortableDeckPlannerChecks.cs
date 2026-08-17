using System.Runtime.CompilerServices;
using DeckBuilder.Core.Models;
using DeckBuilder.GameData;

internal static class PortableDeckPlannerChecks
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        DeckDocument deck = new();
        CardRecord redSpell = Card("RED_SPELL", "R");
        CardRecord promoSpell = Card("PROMO_CARD", "R");
        deck.MainDeck.Add(new DeckEntry(redSpell));
        deck.PromoUnlocks.Add(new DeckEntry(promoSpell, promo: true, bias: 2));

        List<CardRecord> catalog =
        [
            redSpell,
            promoSpell,
            Card("MOUNTAIN_001", string.Empty, "Land"),
            Card("MOUNTAIN_002", string.Empty, "Land"),
            Card("MOUNTAIN_003", string.Empty, "Land"),
            Card("MOUNTAIN_004", string.Empty, "Land"),
            Card("MOUNTAIN_005", string.Empty, "Land"),
            Card("FOREST_001", string.Empty, "Land")
        ];

        IReadOnlyList<string> references = PortableDeckCardReferencePlanner.GetRequiredReferences(deck, catalog);
        Require(references.Contains("RED_SPELL", StringComparer.OrdinalIgnoreCase), "Main-deck CARD_V2 is missing.");
        Require(references.Contains("PROMO_CARD", StringComparer.OrdinalIgnoreCase), "Promo CARD_V2 is missing.");
        Require(!references.Any(reference => reference.Contains('#') || reference.Contains('@')),
            "Portable dependencies must use canonical CARD_V2 names, not deck #/@ modifiers.");

        foreach (string land in new[] { "MOUNTAIN_001", "MOUNTAIN_002", "MOUNTAIN_003", "MOUNTAIN_004" })
            Require(references.Contains(land, StringComparer.OrdinalIgnoreCase), $"Automatic land-pool dependency {land} is missing.");

        Require(!references.Contains("MOUNTAIN_005", StringComparer.OrdinalIgnoreCase),
            "Portable planner must mirror the exporter's four-variant land-pool limit.");
        Require(!references.Contains("FOREST_001", StringComparer.OrdinalIgnoreCase),
            "A red deck must not pull unrelated forest land-pool cards.");

        Console.WriteLine("PASS: portable deck + unlock + land-pool reference planning");
    }

    private static CardRecord Card(string fileName, string castingCost, string typeLine = "Sorcery") => new(
        fileName,
        fileName,
        fileName,
        typeLine,
        "TST",
        "Test",
        castingCost: castingCost,
        colour: castingCost,
        rarity: "C",
        imageId: fileName);

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
