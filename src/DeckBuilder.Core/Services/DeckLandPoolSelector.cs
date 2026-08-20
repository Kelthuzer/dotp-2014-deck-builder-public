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

    private static readonly (DeckColourFlags Flag, string Prefix)[] LandTypes =
    {
        (DeckColourFlags.Green, "FOREST"),
        (DeckColourFlags.Blue, "ISLAND"),
        (DeckColourFlags.Red, "MOUNTAIN"),
        (DeckColourFlags.White, "PLAINS"),
        (DeckColourFlags.Black, "SWAMP")
    };

    public static IReadOnlyList<CardRecord> Select(
        DeckDocument deck,
        IReadOnlyList<CardRecord> catalog)
    {
        ArgumentNullException.ThrowIfNull(deck);
        ArgumentNullException.ThrowIfNull(catalog);

        DeckColourFlags colour = DeckColourCalculator.Calculate(deck);
        string[] prefixes = LandTypes
            .Where(type => DeckColourCalculator.Has(colour, type.Flag))
            .Select(type => type.Prefix)
            .ToArray();
        if (prefixes.Length == 0)
            prefixes = LandTypes.Select(type => type.Prefix).ToArray();

        List<CardRecord> result = new();
        foreach (string prefix in prefixes)
        {
            DeckEntry? existing = deck.MainDeck
                .Where(entry => entry.Card.FileName.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(entry => entry.Quantity)
                .ThenBy(entry => entry.Card.FileName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            CardRecord? preferred = existing?.Card ?? catalog
                .Where(card => card.FileName.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase))
                .OrderBy(card => card.FileName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (preferred is null)
                throw new InvalidOperationException($"No {prefix.ToLowerInvariant()} cards were found for the automatic land pool.");

            for (int index = 0; index < EntriesPerColour; index++)
                result.Add(preferred);
        }

        return result;
    }
}
