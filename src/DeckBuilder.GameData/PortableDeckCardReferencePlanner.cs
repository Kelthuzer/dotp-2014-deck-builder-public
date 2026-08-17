using DeckBuilder.Core.Models;
using DeckBuilder.Core.Services;

namespace DeckBuilder.GameData;

/// <summary>
/// Computes every CARD_V2 reference written directly or indirectly by ModernWadExporter before
/// recursive card/runtime closure starts. In particular, Magic 2014 deck WADs contain an automatic
/// LAND_POOL whose cards are not necessarily present in the visible 60-card main deck.
/// </summary>
public static class PortableDeckCardReferencePlanner
{
    public static IReadOnlyList<string> GetRequiredReferences(
        DeckDocument deck,
        IReadOnlyList<CardRecord> catalog)
    {
        ArgumentNullException.ThrowIfNull(deck);
        ArgumentNullException.ThrowIfNull(catalog);

        HashSet<string> references = new(StringComparer.OrdinalIgnoreCase);
        foreach (DeckEntry entry in deck.MainDeck.Concat(deck.RegularUnlocks).Concat(deck.PromoUnlocks))
        {
            // DeckEntry.CardReference contains deck-XML modifiers such as # and @2. Dependency
            // lookup must use the canonical CARD_V2 FILENAME, not that serialized deck syntax.
            if (!string.IsNullOrWhiteSpace(entry.Card.FileName))
                references.Add(entry.Card.FileName.Trim());
        }

        foreach (CardRecord land in SelectAutomaticLandPool(deck, catalog))
        {
            if (!string.IsNullOrWhiteSpace(land.FileName))
                references.Add(land.FileName.Trim());
        }

        return references
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Mirrors ModernWadExporter's LAND_POOL selection: four deterministic basic-land variants for
    /// every deck colour, or all five basic land types for a colourless deck.
    /// </summary>
    internal static IReadOnlyList<CardRecord> SelectAutomaticLandPool(
        DeckDocument deck,
        IReadOnlyList<CardRecord> catalog)
    {
        DeckColourFlags colour = DeckColourCalculator.Calculate(deck);
        List<string> landNames = new();
        if (DeckColourCalculator.Has(colour, DeckColourFlags.Green)) landNames.Add("FOREST");
        if (DeckColourCalculator.Has(colour, DeckColourFlags.Blue)) landNames.Add("ISLAND");
        if (DeckColourCalculator.Has(colour, DeckColourFlags.Red)) landNames.Add("MOUNTAIN");
        if (DeckColourCalculator.Has(colour, DeckColourFlags.White)) landNames.Add("PLAINS");
        if (DeckColourCalculator.Has(colour, DeckColourFlags.Black)) landNames.Add("SWAMP");
        if (landNames.Count == 0)
            landNames.AddRange(["FOREST", "ISLAND", "MOUNTAIN", "PLAINS", "SWAMP"]);

        List<CardRecord> result = new();
        foreach (string land in landNames)
        {
            CardRecord[] choices = catalog
                .Where(card => card.FileName.StartsWith(land + "_", StringComparison.OrdinalIgnoreCase))
                .OrderBy(card => card.FileName, StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToArray();
            if (choices.Length == 0)
                throw new InvalidOperationException($"No {land.ToLowerInvariant()} cards were found for the automatic land pool.");

            result.AddRange(choices);
        }

        return result;
    }
}
