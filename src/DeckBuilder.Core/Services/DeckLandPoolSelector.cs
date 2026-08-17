using DeckBuilder.Core.Models;

namespace DeckBuilder.Core.Services;

/// <summary>
/// Magic 2014 deck WADs carry a deterministic hidden land pool. Keep its selection in one place so
/// the exporter and portable dependency planner cannot disagree about which CARD_V2 files are used.
/// </summary>
public static class DeckLandPoolSelector
{
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
            CardRecord[] choices = catalog
                .Where(card => card.FileName.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase))
                .OrderBy(card => card.FileName, StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToArray();
            if (choices.Length == 0)
                throw new InvalidOperationException($"No {prefix.ToLowerInvariant()} cards were found for the automatic land pool.");

            result.AddRange(choices);
        }

        return result;
    }
}
