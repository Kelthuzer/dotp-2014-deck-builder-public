using System.Xml.Linq;
using DeckBuilder.Core.Models;

namespace DeckBuilder.GameData;

internal static class CardXmlParser
{
    public static CardRecord? Parse(string xml, string source)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml, LoadOptions.None);
        }
        catch
        {
            return CardXmlFallbackParser.TryParse(xml, source);
        }

        XElement? card = document.Root;
        if (card is null || !card.Name.LocalName.Equals("CARD_V2", StringComparison.OrdinalIgnoreCase))
        {
            card = document.Descendants().FirstOrDefault(element =>
                element.Name.LocalName.Equals("CARD_V2", StringComparison.OrdinalIgnoreCase));
        }

        if (card is null)
        {
            return CardXmlFallbackParser.TryParse(xml, source);
        }

        string fileName = Attribute(Child(card, "FILENAME"), "text");
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return CardXmlFallbackParser.TryParse(xml, source);
        }

        XElement? title = Child(card, "TITLE");
        string englishName = Attribute(Child(card, "CARDNAME"), "text");
        string localizedName = Localized(title, "ru-RU")
            ?? Localized(title, "en-US")
            ?? (string.IsNullOrWhiteSpace(englishName) ? fileName : englishName);
        if (string.IsNullOrWhiteSpace(englishName))
        {
            englishName = Localized(title, "en-US") ?? localizedName;
        }

        string typeLine = BuildTypeLine(card);
        string rulesText = BuildRulesText(card);
        XElement? flavor = Child(card, "FLAVOURTEXT");
        return new CardRecord(
            fileName,
            localizedName,
            englishName,
            typeLine,
            Attribute(Child(card, "EXPANSION"), "value"),
            Attribute(Child(card, "ARTIST"), "name"),
            Attribute(Child(card, "CASTING_COST"), "cost"),
            Attribute(Child(card, "COLOUR"), "value"),
            Attribute(Child(card, "RARITY"), "metaname"),
            Attribute(Child(card, "POWER"), "value"),
            Attribute(Child(card, "TOUGHNESS"), "value"),
            source,
            Attribute(Child(card, "ARTID"), "value"),
            rulesText,
            Localized(flavor, "ru-RU") ?? Localized(flavor, "en-US"),
            Attribute(Child(card, "FRAME_TYPE"), "type"),
            Child(card, "TOKEN") is not null);
    }

    private static string BuildRulesText(XElement card)
    {
        List<string> lines = new();
        foreach (XElement ability in card.Elements().Where(element =>
                     element.Name.LocalName.Contains("_ABILITY", StringComparison.OrdinalIgnoreCase)))
        {
            if (int.TryParse(Attribute(ability, "resource_id"), out int resourceId) && resourceId >= 0)
            {
                continue;
            }

            string? text = Localized(ability, "ru-RU") ?? Localized(ability, "en-US");
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (lines.Count > 0 && Attribute(ability, "commaspace") == "1")
            {
                lines[^1] = $"{lines[^1]}, {text.Trim()}";
            }
            else
            {
                lines.Add(text.Trim());
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildTypeLine(XElement card)
    {
        IEnumerable<string> values = card.Elements()
            .Where(element =>
                element.Name.LocalName.Equals("SUPERTYPE", StringComparison.OrdinalIgnoreCase)
                || element.Name.LocalName.Equals("TYPE", StringComparison.OrdinalIgnoreCase)
                || element.Name.LocalName.Equals("SUB_TYPE", StringComparison.OrdinalIgnoreCase))
            .Select(element => Attribute(element, "metaname"))
            .Where(value => !string.IsNullOrWhiteSpace(value));
        return string.Join(" ", values);
    }

    private static XElement? Child(XElement parent, string name) => parent.Elements().FirstOrDefault(element =>
        element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string Attribute(XElement? element, string name) => element?.Attributes()
        .FirstOrDefault(attribute => attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?.Value.Trim() ?? string.Empty;

    private static string? Localized(XElement? parent, string language)
    {
        string? value = parent?.Elements()
            .Where(element => element.Name.LocalName.Equals("LOCALISED_TEXT", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(element => Attribute(element, "LanguageCode").Equals(language, StringComparison.OrdinalIgnoreCase))
            ?.Value.Trim();

        return IsUsableLocalizedText(value) ? value : null;
    }

    private static bool IsUsableLocalizedText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        // Some official/custom Magic 2014 card XMLs ship untranslated locale entries as
        // strings such as "????? ???????". Treat those as missing so the normal en-US
        // fallback is used instead of displaying a row made entirely from question marks.
        // U+FFFD is handled the same way because it is the Unicode replacement character.
        return value.Any(character =>
            !char.IsWhiteSpace(character)
            && character != '?'
            && character != '\uFFFD');
    }
}
