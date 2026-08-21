using DeckBuilder.Core.Models;

namespace DeckBuilder.Core.Services;

/// <summary>
/// Authoritative land classification for the modern editor and exporter.
/// DotP CARD_V2 metadata is authoritative whenever it explicitly identifies a land:
/// a normal basic land is Basic + Land + one of the five basic land subtypes.
/// Exact card-name fallbacks exist only for incomplete legacy/community definitions.
/// </summary>
public static class CardLandClassification
{
    private static readonly (string English, string Russian, char Color)[] BasicLandTypes =
    {
        ("Plains", "Равнина", 'W'),
        ("Island", "Остров", 'U'),
        ("Swamp", "Болото", 'B'),
        ("Mountain", "Гора", 'R'),
        ("Forest", "Лес", 'G')
    };

    public static bool IsLand(CardRecord card)
    {
        ArgumentNullException.ThrowIfNull(card);
        TypeTokens tokens = ParseTypeLine(card.TypeLine);
        if (tokens.IsLand)
            return true;

        return tokens.HasAnyTypeMetadata
            ? false
            : BasicLandColors(card).Count > 0;
    }

    public static bool IsBasicLand(CardRecord card)
    {
        ArgumentNullException.ThrowIfNull(card);
        TypeTokens tokens = ParseTypeLine(card.TypeLine);

        // Explicit CARD_V2 type metadata wins. This is what distinguishes a real XMAS basic
        // such as "Basic Land Plains" from a nonbasic card such as Island of Wak-Wak ("Land").
        if (tokens.IsLand)
            return tokens.IsBasic;

        if (tokens.HasAnyTypeMetadata)
            return false;

        return BasicLandColors(card).Count > 0;
    }

    public static HashSet<char> BasicLandColors(CardRecord card)
    {
        ArgumentNullException.ThrowIfNull(card);
        HashSet<char> colors = new();
        TypeTokens tokens = ParseTypeLine(card.TypeLine);

        if (tokens.IsLand)
        {
            // Never promote an explicitly nonbasic land from its filename or title.
            if (!tokens.IsBasic)
                return colors;

            foreach ((string english, _, char color) in BasicLandTypes)
            {
                if (tokens.Contains(english))
                    colors.Add(color);
            }

            if (colors.Count > 0)
                return colors;

            // A malformed/basic custom definition may say Basic Land but omit its subtype.
            // In that narrow case an exact canonical name is still safe as a fallback.
            AddExactNameColors(colors, card);
            return colors;
        }

        if (tokens.HasAnyTypeMetadata)
            return colors;

        // Legacy/community CARD_V2 data can occasionally lose type fields. Do not guess from
        // prefixes such as ISLAND_*: exact canonical names are enough and cannot turn
        // Island of Wak-Wak into a basic Island.
        AddExactNameColors(colors, card);
        return colors;
    }

    private static void AddExactNameColors(ISet<char> colors, CardRecord card)
    {
        string englishName = card.EnglishName.Trim();
        string localizedName = card.LocalizedName.Trim();
        string fileName = card.FileName.Trim();

        foreach ((string english, string russian, char color) in BasicLandTypes)
        {
            if (englishName.Equals(english, StringComparison.OrdinalIgnoreCase)
                || localizedName.Equals(english, StringComparison.OrdinalIgnoreCase)
                || localizedName.Equals(russian, StringComparison.OrdinalIgnoreCase)
                || fileName.Equals(english, StringComparison.OrdinalIgnoreCase))
            {
                colors.Add(color);
            }
        }
    }

    private static TypeTokens ParseTypeLine(string value)
    {
        string[] tokens = (value ?? string.Empty)
            .Split(
                new[] { ' ', '\t', '\r', '\n', '-', '—', '–', '/', ',' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        bool isLand = tokens.Any(token =>
            token.Equals("Land", StringComparison.OrdinalIgnoreCase)
            || token.StartsWith("Земл", StringComparison.OrdinalIgnoreCase));
        bool isBasic = tokens.Any(token =>
            token.Equals("Basic", StringComparison.OrdinalIgnoreCase)
            || token.StartsWith("Базов", StringComparison.OrdinalIgnoreCase));
        return new TypeTokens(tokens, isLand, isBasic, tokens.Length > 0);
    }

    private readonly record struct TypeTokens(
        IReadOnlyList<string> Tokens,
        bool IsLand,
        bool IsBasic,
        bool HasAnyTypeMetadata)
    {
        public bool Contains(string value) => Tokens.Any(token =>
            token.Equals(value, StringComparison.OrdinalIgnoreCase));
    }
}
