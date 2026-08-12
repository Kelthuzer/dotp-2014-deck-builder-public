using DeckBuilder.Core.Models;

namespace DeckBuilder.Core.Services;

public static class DeckColourCalculator
{
    public static DeckColourFlags Calculate(DeckDocument deck)
    {
        ArgumentNullException.ThrowIfNull(deck);
        IEnumerable<DeckEntry> entries = deck.MainDeck
            .Concat(deck.RegularUnlocks)
            .Concat(deck.PromoUnlocks);

        DeckColourFlags colour = DeckColourFlags.NotDefined;
        foreach (DeckEntry entry in entries)
        {
            string value = (entry.Card.CastingCost + entry.Card.Colour).ToUpperInvariant();
            if (value.Contains('B')) colour |= DeckColourFlags.Black;
            if (value.Contains('U')) colour |= DeckColourFlags.Blue;
            if (value.Contains('G')) colour |= DeckColourFlags.Green;
            if (value.Contains('R')) colour |= DeckColourFlags.Red;
            if (value.Contains('W')) colour |= DeckColourFlags.White;
        }

        return Normalize(colour);
    }

    public static DeckColourFlags FromSelections(
        bool black,
        bool blue,
        bool green,
        bool red,
        bool white)
    {
        DeckColourFlags colour = DeckColourFlags.NotDefined;
        if (black) colour |= DeckColourFlags.Black;
        if (blue) colour |= DeckColourFlags.Blue;
        if (green) colour |= DeckColourFlags.Green;
        if (red) colour |= DeckColourFlags.Red;
        if (white) colour |= DeckColourFlags.White;
        return Normalize(colour);
    }

    public static DeckColourFlags Normalize(DeckColourFlags colour)
    {
        DeckColourFlags colours = colour & (
            DeckColourFlags.Black
            | DeckColourFlags.Blue
            | DeckColourFlags.Green
            | DeckColourFlags.Red
            | DeckColourFlags.White);
        if (colours == DeckColourFlags.NotDefined)
        {
            return DeckColourFlags.Colourless;
        }

        int count = 0;
        foreach (DeckColourFlags flag in new[]
                 {
                     DeckColourFlags.Black,
                     DeckColourFlags.Blue,
                     DeckColourFlags.Green,
                     DeckColourFlags.Red,
                     DeckColourFlags.White
                 })
        {
            if ((colours & flag) != 0)
            {
                count++;
            }
        }

        return count > 1 ? colours | DeckColourFlags.MultiColour : colours;
    }

    public static bool Has(DeckColourFlags value, DeckColourFlags flag) => (value & flag) == flag;
}
