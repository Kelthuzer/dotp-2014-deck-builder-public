using System.Xml.Linq;
using DeckBuilder.Core.Formats;
using DeckBuilder.Core.Models;

namespace DeckBuilder.Core.Services;

public static class DotpDeckXmlSerializer
{
    public static DeckDocument Load(string path, IEnumerable<CardRecord> catalog)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(catalog);

        XDocument xml = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        return ParseDocument(xml, catalog);
    }

    public static DeckDocument Parse(string xml, IEnumerable<CardRecord> catalog)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);
        ArgumentNullException.ThrowIfNull(catalog);

        return ParseDocument(XDocument.Parse(xml, LoadOptions.PreserveWhitespace), catalog);
    }

    private static DeckDocument ParseDocument(XDocument xml, IEnumerable<CardRecord> catalog)
    {
        Dictionary<string, CardRecord> cards = catalog.ToDictionary(card => card.FileName, StringComparer.OrdinalIgnoreCase);
        XElement? documentRoot = xml.Root;
        XElement? foundRoot = documentRoot?.Name.LocalName.Equals("DECK", StringComparison.OrdinalIgnoreCase) == true
            ? documentRoot
            : xml.Descendants().FirstOrDefault(element =>
                element.Name.LocalName.Equals("DECK", StringComparison.OrdinalIgnoreCase));
        DeckDocument deck = new();
        if (foundRoot is null)
        {
            XElement? unlockRoot = documentRoot?.Name.LocalName.Equals("UNLOCKS", StringComparison.OrdinalIgnoreCase) == true
                ? documentRoot
                : xml.Descendants().FirstOrDefault(element =>
                    element.Name.LocalName.Equals("UNLOCKS", StringComparison.OrdinalIgnoreCase));
            if (unlockRoot is null)
            {
                throw new InvalidDataException(
                    documentRoot is null
                        ? "The XML file is empty and does not contain a DECK or UNLOCKS element."
                        : $"This is a <{documentRoot.Name.LocalName}> XML file, not a deck or unlock list.");
            }

            bool promo = Attribute(unlockRoot, "game_mode") == "2";
            ReadCards(
                Children(unlockRoot, "CARD"),
                promo ? deck.PromoUnlocks : deck.RegularUnlocks,
                cards,
                mergeQuantities: false);
            return deck;
        }

        XElement root = foundRoot;
        ReadMetadata(root, deck);
        ReadCards(Children(root, "CARD"), deck.MainDeck, cards, mergeQuantities: true);
        ReadCards(Children(Child(root, "RegularUnlocks"), "CARD"), deck.RegularUnlocks, cards, mergeQuantities: false);
        ReadCards(Children(Child(root, "PromoUnlocks"), "CARD"), deck.PromoUnlocks, cards, mergeQuantities: false);
        foreach (XElement unlocks in xml.Descendants().Where(element =>
                     element.Name.LocalName.Equals("UNLOCKS", StringComparison.OrdinalIgnoreCase)))
        {
            bool promo = Attribute(unlocks, "game_mode") == "2";
            ReadCards(
                Children(unlocks, "CARD"),
                promo ? deck.PromoUnlocks : deck.RegularUnlocks,
                cards,
                mergeQuantities: false);
        }

        return deck;
    }

    private static void ReadMetadata(XElement root, DeckDocument deck)
    {
        deck.Uid = int.TryParse(Attribute(root, "uid"), out int uid) ? uid : -1;
        deck.Personality = Attribute(root, "personality");
        deck.CustomPersonality = AiPersonalityXmlSerializer.ParseEmbedded(Child(root, "AiPersonality"), deck.Personality);
        deck.DeckBoxImage = Attribute(root, "deck_box_image");
        string lockedImage = Attribute(root, "deck_box_image_locked");
        deck.DeckBoxImageLocked = string.IsNullOrWhiteSpace(lockedImage) ? "locked" : lockedImage;
        deck.ContentPack = int.TryParse(Attribute(root, "content_pack"), out int contentPack) ? contentPack : 0;

        bool neverAvailable = Attribute(root, "never_available").Equals("true", StringComparison.OrdinalIgnoreCase);
        bool alwaysAvailable = Attribute(root, "always_available").Equals("true", StringComparison.OrdinalIgnoreCase);
        deck.Availability = neverAvailable
            ? DeckAvailability.NeverAvailable
            : alwaysAvailable
                ? DeckAvailability.AlwaysAvailable
                : DeckAvailability.Locked;

        XElement? colourOverride = Child(root, "ColourOverride");
        if (colourOverride is not null && int.TryParse(Attribute(colourOverride, "Value"), out int colourValue))
        {
            deck.OverrideColours = true;
            deck.OverrideColour = (DeckColourFlags)colourValue;
        }

        XElement? statistics = Child(root, "DECKSTATISTICS");
        deck.CreatureSize = MetadataText(statistics, "Size", "?");
        deck.DeckSpeed = MetadataText(statistics, "Speed", "?");
        deck.Flexibility = MetadataText(statistics, "Flex", "?");
        deck.Synergy = MetadataText(statistics, "Syn", "?");

        XElement? landConfig = Child(root, "LandConfig");
        deck.IgnoreCmcOver = IntAttribute(landConfig, "ignoreCmcOver", -1);
        deck.MinForests = IntAttribute(landConfig, "minForest", 0);
        deck.MinIslands = IntAttribute(landConfig, "minIsland", 0);
        deck.MinMountains = IntAttribute(landConfig, "minMountain", 0);
        deck.MinPlains = IntAttribute(landConfig, "minPlains", 0);
        deck.MinSwamps = IntAttribute(landConfig, "minSwamp", 0);
        deck.NumberOfSpellsThatCountAsLand = IntAttribute(landConfig, "numSpellsThatCountAsLand", 0);

        deck.Name = LocalizedValue(Child(root, "LocalizedDeckNames"))
            ?? Attribute(root, "name_tag");
        deck.Description = LocalizedValue(Child(root, "LocalizedDescriptions")) ?? string.Empty;
    }

    public static void Save(string path, DeckDocument deck, int? uid = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(deck);

        int finalUid = uid ?? (deck.Uid >= 0 ? deck.Uid : 999000);
        string nameTag = $"DECK_{finalUid}_NAME";
        string descriptionTag = $"DECK_{finalUid}_DESCRIPTION";
        AiPersonalityDefinition? customPersonality = deck.CustomPersonality is null
            ? null
            : AiPersonalityXmlSerializer.NormalizeIdentifiers(deck.CustomPersonality);
        string personalityReference = customPersonality?.FileName ?? deck.Personality;
        XElement root = new("DECK",
            new XAttribute("uid", finalUid),
            new XAttribute("personality", personalityReference),
            new XAttribute("deck_box_image", deck.DeckBoxImage),
            new XAttribute("deck_box_image_locked", deck.DeckBoxImageLocked),
            new XAttribute("content_pack", deck.ContentPack),
            new XAttribute("cheat_menu_filter_deck_type", "Standard"),
            new XAttribute("tus_save_data_id", finalUid),
            new XAttribute("cheat_menu_filter_datapool", "D14"),
            new XAttribute("name_tag", nameTag),
            new XAttribute("description_tag", descriptionTag));
        ApplyAvailability(root, deck.Availability);
        AddDeckColours(root, deck.OverrideColours ? deck.OverrideColour : DeckColourCalculator.Calculate(deck));

        root.Add(CreateDeckStatistics(deck));
        XElement? landConfig = CreateLandConfig(deck);
        if (landConfig is not null)
        {
            root.Add(landConfig);
        }

        int order = 0;
        foreach (DeckEntry entry in deck.MainDeck.OrderBy(entry => entry.CardReference, StringComparer.OrdinalIgnoreCase))
        {
            for (int copy = 0; copy < entry.Quantity; copy++)
            {
                root.Add(CreateCard(entry, order++));
            }
        }

        XElement regular = new("RegularUnlocks");
        int unlockOrder = 0;
        foreach (DeckEntry entry in deck.RegularUnlocks)
        {
            regular.Add(CreateUnlockCard(entry, order++, unlockOrder++));
        }

        XElement promo = new("PromoUnlocks");
        unlockOrder = 0;
        foreach (DeckEntry entry in deck.PromoUnlocks)
        {
            promo.Add(CreateUnlockCard(entry, order++, unlockOrder++));
        }

        if (regular.HasElements)
        {
            root.Add(regular);
        }

        if (promo.HasElements)
        {
            root.Add(promo);
        }

        if (!string.IsNullOrWhiteSpace(deck.Name))
        {
            root.Add(new XElement("LocalizedDeckNames",
                new XElement("LOCALISED_TEXT", new XAttribute("LanguageCode", "ru-RU"), new XCData(deck.Name)),
                new XElement("LOCALISED_TEXT", new XAttribute("LanguageCode", "en-US"), new XCData(deck.Name))));
        }

        if (!string.IsNullOrWhiteSpace(deck.Description))
        {
            root.Add(new XElement("LocalizedDescriptions",
                new XElement("LOCALISED_TEXT", new XAttribute("LanguageCode", "ru-RU"), new XCData(deck.Description)),
                new XElement("LOCALISED_TEXT", new XAttribute("LanguageCode", "en-US"), new XCData(deck.Description))));
        }

        if (customPersonality is not null)
        {
            root.Add(AiPersonalityXmlSerializer.CreateEmbeddedElement(customPersonality));
        }

        if (deck.OverrideColours)
        {
            root.Add(new XElement("ColourOverride", new XAttribute("Value", (int)deck.OverrideColour)));
        }

        new XDocument(root).Save(path, SaveOptions.DisableFormatting);
    }

    private static XElement CreateDeckStatistics(DeckDocument deck) => new("DECKSTATISTICS",
        new XAttribute("Size", NormalizeStatistic(deck.CreatureSize)),
        new XAttribute("Speed", NormalizeStatistic(deck.DeckSpeed)),
        new XAttribute("Flex", NormalizeStatistic(deck.Flexibility)),
        new XAttribute("Syn", NormalizeStatistic(deck.Synergy)));

    private static XElement? CreateLandConfig(DeckDocument deck)
    {
        if (deck.IgnoreCmcOver <= -1
            && deck.MinForests <= 0
            && deck.MinIslands <= 0
            && deck.MinMountains <= 0
            && deck.MinPlains <= 0
            && deck.MinSwamps <= 0
            && deck.NumberOfSpellsThatCountAsLand <= 0)
        {
            return null;
        }

        XElement element = new("LandConfig");
        if (deck.IgnoreCmcOver > -1) element.SetAttributeValue("ignoreCmcOver", deck.IgnoreCmcOver);
        if (deck.MinForests > 0) element.SetAttributeValue("minForest", deck.MinForests);
        if (deck.MinIslands > 0) element.SetAttributeValue("minIsland", deck.MinIslands);
        if (deck.MinMountains > 0) element.SetAttributeValue("minMountain", deck.MinMountains);
        if (deck.MinPlains > 0) element.SetAttributeValue("minPlains", deck.MinPlains);
        if (deck.MinSwamps > 0) element.SetAttributeValue("minSwamp", deck.MinSwamps);
        if (deck.NumberOfSpellsThatCountAsLand > 0)
        {
            element.SetAttributeValue("numSpellsThatCountAsLand", deck.NumberOfSpellsThatCountAsLand);
        }

        return element;
    }

    private static void ApplyAvailability(XElement root, DeckAvailability availability)
    {
        root.SetAttributeValue("always_available", null);
        root.SetAttributeValue("never_available", null);
        switch (availability)
        {
            case DeckAvailability.AlwaysAvailable:
                root.SetAttributeValue("always_available", "true");
                break;
            case DeckAvailability.NeverAvailable:
                root.SetAttributeValue("never_available", "true");
                break;
            case DeckAvailability.Locked:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(availability), availability, null);
        }
    }

    private static void AddDeckColours(XElement root, DeckColourFlags colour)
    {
        foreach ((DeckColourFlags flag, string attribute) in new[]
                 {
                     (DeckColourFlags.Black, "is_black"),
                     (DeckColourFlags.Blue, "is_blue"),
                     (DeckColourFlags.Green, "is_green"),
                     (DeckColourFlags.Red, "is_red"),
                     (DeckColourFlags.White, "is_white")
                 })
        {
            root.SetAttributeValue(attribute, DeckColourCalculator.Has(colour, flag) ? "true" : null);
        }
    }

    private static void ReadCards(
        IEnumerable<XElement> elements,
        IList<DeckEntry> target,
        IDictionary<string, CardRecord> cards,
        bool mergeQuantities)
    {
        foreach (XElement element in elements)
        {
            string value = Attribute(element, "name");
            if (!DeckCardReference.TryParse(value, out DeckCardReference reference))
            {
                continue;
            }

            if (!cards.TryGetValue(reference.FileName, out CardRecord? card))
            {
                card = new CardRecord(reference.FileName, reference.FileName, reference.FileName, null, null, null);
                cards[reference.FileName] = card;
            }

            int order = int.TryParse(Attribute(element, "deckOrderId"), out int parsedOrder)
                ? parsedOrder
                : -1;
            int quantity = int.TryParse(Attribute(element, "quantity"), out int parsedQuantity)
                ? Math.Max(1, parsedQuantity)
                : 1;
            if (mergeQuantities)
            {
                DeckEntry? existing = target.FirstOrDefault(entry =>
                    entry.Card.FileName.Equals(reference.FileName, StringComparison.OrdinalIgnoreCase)
                    && entry.Bias == reference.Bias
                    && entry.Promo == reference.Promo);
                if (existing is not null)
                {
                    existing.Quantity += quantity;
                    continue;
                }
            }

            if (mergeQuantities)
            {
                target.Add(new DeckEntry(card, quantity, reference.Bias, reference.Promo, order));
            }
            else
            {
                for (int copy = 0; copy < quantity; copy++)
                {
                    target.Add(new DeckEntry(card, 1, reference.Bias, reference.Promo, order < 0 ? -1 : order + copy));
                }
            }
        }
    }

    private static string? LocalizedValue(XElement? element)
    {
        if (element is null)
        {
            return null;
        }

        XElement? localized = element.Elements()
            .Where(child => child.Name.LocalName.Equals("LOCALISED_TEXT", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(child => Attribute(child, "LanguageCode").Equals("ru-RU", StringComparison.OrdinalIgnoreCase))
            ?? element.Elements().FirstOrDefault(child =>
                child.Name.LocalName.Equals("LOCALISED_TEXT", StringComparison.OrdinalIgnoreCase)
                && Attribute(child, "LanguageCode").Equals("en-US", StringComparison.OrdinalIgnoreCase))
            ?? element.Elements().FirstOrDefault(child =>
                child.Name.LocalName.Equals("LOCALISED_TEXT", StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(localized?.Value) ? null : localized.Value.Trim();
    }

    private static string MetadataText(XElement? element, string attribute, string fallback)
    {
        if (element is null)
        {
            return fallback;
        }

        string value = Attribute(element, attribute);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static int IntAttribute(XElement? element, string attribute, int fallback)
    {
        if (element is null)
        {
            return fallback;
        }

        return int.TryParse(Attribute(element, attribute), out int value) ? value : fallback;
    }

    private static string NormalizeStatistic(string? value) => string.IsNullOrWhiteSpace(value) ? "?" : value.Trim();

    private static XElement CreateCard(DeckEntry entry, int order) => new("CARD",
        new XAttribute("name", entry.CardReference),
        new XAttribute("deckOrderId", order));

    private static XElement CreateUnlockCard(DeckEntry entry, int order, int unlockOrder) => new("CARD",
        new XAttribute("name", entry.CardReference),
        new XAttribute("deckOrderId", order),
        new XAttribute("unlockOrderId", unlockOrder),
        new XAttribute("quantity", 1));

    private static XElement? Child(XElement parent, string name) => parent.Elements().FirstOrDefault(element =>
        element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<XElement> Children(XElement? parent, string name) =>
        parent?.Elements().Where(element => element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? Enumerable.Empty<XElement>();

    private static string Attribute(XElement element, string name) => element.Attributes()
        .FirstOrDefault(attribute => attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?.Value.Trim() ?? string.Empty;
}
