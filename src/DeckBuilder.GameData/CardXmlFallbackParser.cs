using System.Text.RegularExpressions;
using DeckBuilder.Core.Models;

namespace DeckBuilder.GameData;

/// <summary>
/// Recovers the identity of legacy/community CARD_V2 files that are playable by the game but are
/// not strict XML. The full parser remains preferred; this fallback exists so portable packaging
/// does not silently lose token cards because one unrelated field contains malformed markup.
/// </summary>
internal static class CardXmlFallbackParser
{
    private static readonly Regex CardMarkerRegex = new(
        @"<\s*CARD_V2\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static CardRecord? TryParse(string xml, string source)
    {
        if (string.IsNullOrWhiteSpace(xml) || !CardMarkerRegex.IsMatch(xml))
            return null;

        string fileName = Attribute(xml, "FILENAME", "text");
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        string cardName = Attribute(xml, "CARDNAME", "text");
        string imageId = Attribute(xml, "ARTID", "value");
        if (string.IsNullOrWhiteSpace(imageId))
            imageId = Attribute(xml, "ARTID", "text");

        string power = Attribute(xml, "POWER", "value");
        string toughness = Attribute(xml, "TOUGHNESS", "value");
        string castingCost = Attribute(xml, "CASTING_COST", "cost");
        string colour = Attribute(xml, "COLOUR", "value");
        string rarity = Attribute(xml, "RARITY", "metaname");
        string expansion = Attribute(xml, "EXPANSION", "value");
        string artist = Attribute(xml, "ARTIST", "name");
        string frameType = Attribute(xml, "FRAME_TYPE", "type");
        bool isToken = Regex.IsMatch(
            xml,
            @"<\s*TOKEN(?:\s|/|>)",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        string displayName = string.IsNullOrWhiteSpace(cardName) ? fileName : cardName;
        return new CardRecord(
            fileName,
            displayName,
            displayName,
            string.Empty,
            expansion,
            artist,
            castingCost,
            colour,
            rarity,
            power,
            toughness,
            source,
            imageId,
            string.Empty,
            string.Empty,
            frameType,
            isToken);
    }

    private static string Attribute(string xml, string elementName, string attributeName)
    {
        string pattern = $"<\\s*{Regex.Escape(elementName)}\\b[^>]*?\\b{Regex.Escape(attributeName)}\\s*=\\s*[\\\"'](?<value>[^\\\"']+)[\\\"']";
        Match match = Regex.Match(
            xml,
            pattern,
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? match.Groups["value"].Value.Trim() : string.Empty;
    }
}
