using System.Text.RegularExpressions;
using DeckBuilder.Core.Models;

namespace DeckBuilder.GameData;

public sealed record CardVisualSpec(
    string FrameId,
    string? PowerBoxId,
    string CreditId,
    string? RarityId,
    bool FullBleedArt,
    bool ShowsPower,
    IReadOnlyList<string> ManaImageIds);

public static partial class CardVisualMetadata
{
    private static readonly char[] ColourOrder = ['B', 'U', 'G', 'R', 'W'];

    public static CardVisualSpec FromCard(CardRecord card)
    {
        ArgumentNullException.ThrowIfNull(card);

        string types = card.TypeLine.ToUpperInvariant();
        bool artifact = types.Contains("ARTIFACT", StringComparison.Ordinal);
        bool land = types.Contains("LAND", StringComparison.Ordinal);
        bool creature = types.Contains("CREATURE", StringComparison.Ordinal);
        bool basic = types.Contains("BASIC", StringComparison.Ordinal) && land;
        HashSet<char> colours = DetermineColours(card.CastingCost, card.Colour);

        string frame;
        string? powerBox = null;
        if (basic)
        {
            frame = types.Contains("FOREST", StringComparison.Ordinal) ? "G_BASIC_LAND"
                : types.Contains("ISLAND", StringComparison.Ordinal) ? "U_BASIC_LAND"
                : types.Contains("MOUNTAIN", StringComparison.Ordinal) ? "R_BASIC_LAND"
                : types.Contains("PLAINS", StringComparison.Ordinal) ? "W_BASIC_LAND"
                : types.Contains("SWAMP", StringComparison.Ordinal) ? "B_BASIC_LAND"
                : "C_LAND";
        }
        else
        {
            (frame, powerBox) = DetermineColourFrame(colours, land, artifact);
            if (artifact)
            {
                frame += "_ARTIFACT";
            }

            if (card.IsToken)
            {
                frame += "_TOKEN2";
            }

            if (!card.IsToken && card.CastingCost.Contains('/', StringComparison.Ordinal) && colours.Count == 2)
            {
                frame += "_HYBRID";
            }

            if (!string.IsNullOrWhiteSpace(card.FrameType))
            {
                frame += $"_{card.FrameType.Trim().ToUpperInvariant()}";
            }
        }

        string credit = frame.StartsWith('B') || frame.Equals("C_LAND", StringComparison.OrdinalIgnoreCase)
            ? "CREDIT_WHITE"
            : "CREDIT_BLACK";
        string? rarity = card.Rarity.Trim().ToUpperInvariant() switch
        {
            "C" or "COMMON" => "EXPANSION_COMMON",
            "U" or "UNCOMMON" => "EXPANSION_UNCOMMON",
            "R" or "RARE" => "EXPANSION_RARE",
            "M" or "MYTHIC" => "EXPANSION_MYTHIC",
            _ => null
        };
        bool fullBleed = card.IsToken || (colours.Count == 0 && !artifact && !land);

        return new CardVisualSpec(
            frame,
            powerBox,
            credit,
            rarity,
            fullBleed,
            creature && (!string.IsNullOrWhiteSpace(card.Power) || !string.IsNullOrWhiteSpace(card.Toughness)),
            ManaTokens(card.CastingCost));
    }

    public static IReadOnlyList<string> ManaTokens(string? castingCost)
    {
        if (string.IsNullOrWhiteSpace(castingCost))
        {
            return Array.Empty<string>();
        }

        List<string> result = new();
        foreach (Match match in ManaTokenRegex().Matches(castingCost))
        {
            string? imageId = ManaImageId(match.Value);
            if (!string.IsNullOrWhiteSpace(imageId))
            {
                result.Add(imageId);
            }
        }

        return result;
    }

    /// <summary>
    /// Casting-cost token mapping from the original DotP 2014 Deck Builder Tools.CostTokenToImageName.
    /// Rules-text action/special symbols use DotpSymbolMap.TextTokenImageId instead.
    /// </summary>
    public static string? ManaImageId(string? token) => DotpSymbolMap.CostTokenImageId(token);

    private static (string Frame, string PowerBox) DetermineColourFrame(
        IReadOnlySet<char> colours,
        bool land,
        bool artifact)
    {
        if (colours.Count > 1)
        {
            string pair = colours.Count == 2 ? PairFrameId(colours) : "Z";
            return (pair, "PTBOX_GOLD");
        }

        if (colours.Count == 0)
        {
            if (land)
            {
                return artifact ? ("C", "PTBOX_A") : ("C_LAND", "PTBOX_C");
            }

            return ("C", "PTBOX_C");
        }

        return colours.Single() switch
        {
            'B' => ("B", "PTBOX_B"),
            'U' => ("U", "PTBOX_U"),
            'G' => ("G", "PTBOX_G"),
            'R' => ("R", "PTBOX_R"),
            'W' => ("W", "PTBOX_W"),
            _ => ("C", "PTBOX_C")
        };
    }

    private static HashSet<char> DetermineColours(string castingCost, string colour)
    {
        string value = $"{castingCost}{colour}".ToUpperInvariant();
        return ColourOrder.Where(value.Contains).ToHashSet();
    }

    private static string PairFrameId(IReadOnlySet<char> colours)
    {
        if (colours.SetEquals(['B', 'G'])) return "BG";
        if (colours.SetEquals(['B', 'R'])) return "BR";
        if (colours.SetEquals(['B', 'U'])) return "UB";
        if (colours.SetEquals(['B', 'W'])) return "WB";
        if (colours.SetEquals(['G', 'R'])) return "RG";
        if (colours.SetEquals(['G', 'U'])) return "UG";
        if (colours.SetEquals(['G', 'W'])) return "WG";
        if (colours.SetEquals(['R', 'U'])) return "UR";
        if (colours.SetEquals(['R', 'W'])) return "WR";
        if (colours.SetEquals(['U', 'W'])) return "WU";
        return "Z";
    }

    [GeneratedRegex(@"\{([^}]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex ManaTokenRegex();
}
