using DeckBuilder.Core.Models;

namespace DeckBuilder.Core.Services;

/// <summary>
/// Magic 2014 deck WADs carry a deterministic hidden land pool. Keep its selection in one place so
/// the exporter and portable dependency planner cannot disagree about which CARD_V2 files are used.
/// The pool keeps four entries per active color, but repeated basic lands are valid and deliberately
/// reuse one preferred variant instead of pulling several different artwork/card definitions.
/// </summary>
public static class DeckLandPoolSelector
{
    private const int EntriesPerColour = 4;

    private static readonly (DeckColourFlags Flag, char Color, string Name)[] LandTypes =
    {
        (DeckColourFlags.Green, 'G', "forest"),
        (DeckColourFlags.Blue, 'U', "island"),
        (DeckColourFlags.Red, 'R', "mountain"),
        (DeckColourFlags.White, 'W', "plains"),
        (DeckColourFlags.Black, 'B', "swamp")
    };

    public static IReadOnlyList<CardRecord> Select(
        DeckDocument deck,
        IReadOnlyList<CardRecord> catalog)
    {
        ArgumentNullException.ThrowIfNull(deck);
        ArgumentNullException.ThrowIfNull(catalog);

        DeckColourFlags colour = DeckColourCalculator.Calculate(deck);
        (DeckColourFlags Flag, char Color, string Name)[] types = LandTypes
            .Where(type => DeckColourCalculator.Has(colour, type.Flag))
            .ToArray();
        if (types.Length == 0)
            types = LandTypes;

        List<CardRecord> result = new();
        foreach ((_, char color, string name) in types)
        {
            DeckEntry? existing = deck.MainDeck
                .Where(entry => IsBasicLandOfColor(entry.Card, color))
                .OrderByDescending(entry => entry.Quantity)
                .ThenBy(entry => entry.Card.FileName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            CardRecord? preferred = existing?.Card ?? catalog
                .Where(card => IsBasicLandOfColor(card, color))
                .OrderBy(card => card.FileName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (preferred is null)
                throw new InvalidOperationException($"No {name} cards were found for the automatic land pool.");

            for (int index = 0; index < EntriesPerColour; index++)
                result.Add(preferred);
        }

        return result;
    }

    private static bool IsBasicLandOfColor(CardRecord card, char color) =>
        CardLandClassification.IsBasicLand(card)
        && CardLandClassification.BasicLandColors(card).Contains(color);
}
