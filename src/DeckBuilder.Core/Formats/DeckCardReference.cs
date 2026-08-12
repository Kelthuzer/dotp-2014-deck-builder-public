namespace DeckBuilder.Core.Formats;

/// <summary>
/// Parses and writes the card reference syntax used by DotP 2014 deck XML:
/// CARD_NAME, CARD_NAME#, CARD_NAME@2, or CARD_NAME#@2.
/// </summary>
public readonly record struct DeckCardReference(string FileName, bool Promo, int Bias)
{
    public static DeckCardReference Parse(string value)
    {
        if (!TryParse(value, out DeckCardReference result))
        {
            throw new FormatException($"Invalid DotP card reference: '{value}'.");
        }

        return result;
    }

    public static bool TryParse(string? value, out DeckCardReference result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string working = value.Trim();
        int bias = 1;
        int biasMarker = working.IndexOf('@');
        if (biasMarker >= 0)
        {
            string biasText = working[(biasMarker + 1)..].Trim();
            working = working[..biasMarker].Trim();
            if (!int.TryParse(biasText, out bias))
            {
                return false;
            }
        }

        int promoMarker = working.IndexOf('#');
        bool promo = promoMarker >= 0;
        if (promo)
        {
            working = working[..promoMarker].Trim();
        }

        if (working.Length == 0)
        {
            return false;
        }

        result = new DeckCardReference(working, promo, bias);
        return true;
    }

    public static string Format(string fileName, bool promo = false, int bias = 1)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("A card filename is required.", nameof(fileName));
        }

        return fileName.Trim()
            + (promo ? "#" : string.Empty)
            + (bias > 1 ? "@" + bias : string.Empty);
    }

    public override string ToString() => Format(FileName, Promo, Bias);
}
