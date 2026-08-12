using System.Xml.Linq;
using DeckBuilder.Core.Models;

namespace DeckBuilder.Core.Services;

public static class AiPersonalityXmlSerializer
{
    public static AiPersonalityDefinition NormalizeIdentifiers(AiPersonalityDefinition source, int? idBlock = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        AiPersonalityDefinition result = source.Clone();
        string code = Codify(result.DisplayName);
        if (idBlock is > 0)
        {
            code = $"{idBlock.Value}_{code}";
        }

        if (string.IsNullOrWhiteSpace(result.FileName))
        {
            result.FileName = $"D14_PERSONALITY_{code}.XML";
        }
        else if (!Path.GetExtension(result.FileName).Equals(".xml", StringComparison.OrdinalIgnoreCase))
        {
            result.FileName = $"{Path.GetFileNameWithoutExtension(result.FileName)}.XML";
        }

        if (string.IsNullOrWhiteSpace(result.NameTag))
        {
            result.NameTag = $"PLAYER_NAME_{code}";
        }

        result.FileName = Path.GetFileName(result.FileName) ?? result.FileName;
        return result;
    }

    public static XDocument CreateStandaloneDocument(AiPersonalityDefinition definition)
    {
        AiPersonalityDefinition normalized = NormalizeIdentifiers(definition);
        return new XDocument(CreateConfig(normalized, includeLocalizedNames: false));
    }

    public static XElement CreateEmbeddedElement(AiPersonalityDefinition definition)
    {
        AiPersonalityDefinition normalized = NormalizeIdentifiers(definition);
        XElement element = new("AiPersonality");
        AddFields(element, normalized, includeLocalizedNames: true);
        return element;
    }

    public static AiPersonalityDefinition? ParseEmbedded(XElement? element, string? fileName = null)
    {
        if (element is null)
        {
            return null;
        }

        XElement root = element.Name.LocalName.Equals("CONFIG", StringComparison.OrdinalIgnoreCase)
            ? element
            : element;
        string displayName = LocalizedValue(Child(root, "LocalizedNames"))
            ?? StringValue(root, "PLANESWALKER_NAME_TAG")
            ?? "New Personality";
        AiPersonalityDefinition definition = new()
        {
            FileName = fileName?.Trim() ?? string.Empty,
            DisplayName = displayName,
            NameTag = StringValue(root, "PLANESWALKER_NAME_TAG") ?? string.Empty,
            LargeAvatarImage = StringValue(root, "LARGE_AVATAR_IMAGE") ?? string.Empty,
            SmallAvatarImage = StringValue(root, "SMALL_AVATAR_IMAGE")
                ?? StringValue(root, "MEDIUM_AVATAR_IMAGE")
                ?? string.Empty,
            SmallAvatarLockedImage = StringValue(root, "SMALL_AVATAR_IMAGE_LOCKED") ?? string.Empty,
            LobbyImage = StringValue(root, "LOBBY_IMAGE") ?? string.Empty,
            Music = StringValue(root, "MUSIC") ?? string.Empty
        };
        return NormalizeIdentifiers(definition);
    }

    public static string Codify(string? value)
    {
        string text = string.IsNullOrWhiteSpace(value) ? "CUSTOM" : value.Trim().ToUpperInvariant();
        Span<char> buffer = stackalloc char[text.Length];
        int length = 0;
        bool separator = false;
        foreach (char character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                if (separator && length > 0)
                {
                    buffer[length++] = '_';
                }

                buffer[length++] = character;
                separator = false;
            }
            else
            {
                separator = true;
            }
        }

        return length == 0 ? "CUSTOM" : new string(buffer[..length]);
    }

    private static XElement CreateConfig(AiPersonalityDefinition definition, bool includeLocalizedNames)
    {
        XElement root = new("CONFIG");
        AddFields(root, definition, includeLocalizedNames);
        return root;
    }

    private static void AddFields(XElement root, AiPersonalityDefinition definition, bool includeLocalizedNames)
    {
        root.Add(StringElement("PLANESWALKER_NAME_TAG", definition.NameTag));
        root.Add(StringElement("LARGE_AVATAR_IMAGE", definition.LargeAvatarImage));
        root.Add(StringElement("MEDIUM_AVATAR_IMAGE", definition.SmallAvatarImage));
        root.Add(StringElement("SMALL_AVATAR_IMAGE", definition.SmallAvatarImage));
        root.Add(StringElement("SMALL_AVATAR_IMAGE_LOCKED", definition.SmallAvatarLockedImage));
        root.Add(StringElement("LOBBY_IMAGE", definition.LobbyImage));
        root.Add(StringElement("MUSIC", definition.Music));

        if (includeLocalizedNames && !string.IsNullOrWhiteSpace(definition.DisplayName))
        {
            root.Add(new XElement("LocalizedNames",
                new XElement("LOCALISED_TEXT", new XAttribute("LanguageCode", "ru-RU"), new XCData(definition.DisplayName)),
                new XElement("LOCALISED_TEXT", new XAttribute("LanguageCode", "en-US"), new XCData(definition.DisplayName))));
        }
    }

    private static XElement StringElement(string name, string? value) =>
        new(name, new XAttribute("string", value?.Trim() ?? string.Empty));

    private static string? StringValue(XElement root, string name)
    {
        XElement? element = Child(root, name);
        string? value = element?.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName.Equals("string", StringComparison.OrdinalIgnoreCase))
            ?.Value.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? LocalizedValue(XElement? element)
    {
        if (element is null)
        {
            return null;
        }

        XElement? localized = element.Elements()
            .Where(child => child.Name.LocalName.Equals("LOCALISED_TEXT", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(child => Language(child, "ru-RU"))
            ?? element.Elements().FirstOrDefault(child =>
                child.Name.LocalName.Equals("LOCALISED_TEXT", StringComparison.OrdinalIgnoreCase) && Language(child, "en-US"))
            ?? element.Elements().FirstOrDefault(child => child.Name.LocalName.Equals("LOCALISED_TEXT", StringComparison.OrdinalIgnoreCase));
        string? value = localized?.Value.Trim();
        return IsUsableText(value) ? value : null;
    }

    private static bool Language(XElement element, string language) => element.Attributes()
        .Any(attribute => attribute.Name.LocalName.Equals("LanguageCode", StringComparison.OrdinalIgnoreCase)
            && attribute.Value.Equals(language, StringComparison.OrdinalIgnoreCase));

    private static bool IsUsableText(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Any(character => !char.IsWhiteSpace(character) && character != '?' && character != '\uFFFD');

    private static XElement? Child(XElement parent, string name) => parent.Elements().FirstOrDefault(element =>
        element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
}
