namespace DeckBuilder.GameData;

/// <summary>
/// Exact symbol-name mappings from the original DotP 2014 Deck Builder Tools.cs.
/// Casting-cost and rules-text tokens deliberately use different tables and preserve case:
/// in rules text {q} is untap while {Q} is the chaos unlock symbol.
/// </summary>
public static class DotpSymbolMap
{
    public static string? CostTokenImageId(string? token)
    {
        string? value = NormalizeToken(token);
        return value switch
        {
            "{0}" => "MANA_0",
            "{1}" => "MANA_1",
            "{2}" => "MANA_2",
            "{3}" => "MANA_3",
            "{4}" => "MANA_4",
            "{5}" => "MANA_5",
            "{6}" => "MANA_6",
            "{7}" => "MANA_7",
            "{8}" => "MANA_8",
            "{9}" => "MANA_9",
            "{10}" => "MANA_10",
            "{11}" => "MANA_11",
            "{12}" => "MANA_12",
            "{13}" => "MANA_13",
            "{14}" => "MANA_14",
            "{15}" => "MANA_15",
            "{16}" => "MANA_16",
            "{X}" => "MANA_X",
            "{B}" => "MANA_B",
            "{U}" => "MANA_U",
            "{G}" => "MANA_G",
            "{R}" => "MANA_R",
            "{W}" => "MANA_W",
            "{B/G}" => "MANA_BG",
            "{B/R}" => "MANA_BR",
            "{B/U}" => "MANA_UB",
            "{B/W}" => "MANA_WB",
            "{G/B}" => "MANA_BG",
            "{G/R}" => "MANA_RG",
            "{G/U}" => "MANA_GU",
            "{G/W}" => "MANA_GW",
            "{R/B}" => "MANA_BR",
            "{R/G}" => "MANA_RG",
            "{R/U}" => "MANA_UR",
            "{R/W}" => "MANA_RW",
            "{U/B}" => "MANA_UB",
            "{U/G}" => "MANA_GU",
            "{U/R}" => "MANA_UR",
            "{U/W}" => "MANA_WU",
            "{W/B}" => "MANA_WB",
            "{W/G}" => "MANA_GW",
            "{W/R}" => "MANA_RW",
            "{W/U}" => "MANA_WU",
            "{B/P}" => "PHYREXIAN_BLACK_MANA",
            "{U/P}" => "PHYREXIAN_BLUE_MANA",
            "{G/P}" => "PHYREXIAN_GREEN_MANA",
            "{R/P}" => "PHYREXIAN_RED_MANA",
            "{W/P}" => "PHYREXIAN_WHITE_MANA",
            _ => null
        };
    }

    public static string? TextTokenImageId(string? token)
    {
        string? value = NormalizeToken(token);
        return value switch
        {
            "{0}" => "MANA_0",
            "{1}" => "MANA_1",
            "{2}" => "MANA_2",
            "{3}" => "MANA_3",
            "{4}" => "MANA_4",
            "{5}" => "MANA_5",
            "{6}" => "MANA_6",
            "{7}" => "MANA_7",
            "{8}" => "MANA_8",
            "{9}" => "MANA_9",
            "{10}" => "MANA_10",
            "{11}" => "MANA_11",
            "{12}" => "MANA_12",
            "{13}" => "MANA_13",
            "{14}" => "MANA_14",
            "{15}" => "MANA_15",
            "{16}" => "MANA_16",
            "{X}" => "MANA_X",
            "{B}" => "MANA_B",
            "{U}" => "MANA_U",
            "{G}" => "MANA_G",
            "{R}" => "MANA_R",
            "{W}" => "MANA_W",
            "{T}" => "MANA_T",
            "{q}" => "MANA_Q",
            "{a}" => "MANA_BG",
            "{b}" => "MANA_BR",
            "{c}" => "MANA_GU",
            "{d}" => "MANA_GW",
            "{e}" => "MANA_RG",
            "{f}" => "MANA_RW",
            "{g}" => "MANA_UB",
            "{h}" => "MANA_UR",
            "{i}" => "MANA_WB",
            "{j}" => "MANA_WU",
            "{J}" => "PHYREXIAN_BLACK_MANA",
            "{K}" => "PHYREXIAN_BLUE_MANA",
            "{L}" => "PHYREXIAN_GREEN_MANA",
            "{I}" => "PHYREXIAN_RED_MANA",
            "{O}" => "PHYREXIAN_WHITE_MANA",
            "{Q}" => "CHAOS_UNLOCK_SYMBOL",
            _ => null
        };
    }

    private static string? NormalizeToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        return AddBraces(token.Trim());
    }

    private static string AddBraces(string value)
    {
        if (!value.StartsWith('{'))
        {
            value = "{" + value;
        }

        if (!value.EndsWith('}'))
        {
            value += "}";
        }

        return value;
    }
}
