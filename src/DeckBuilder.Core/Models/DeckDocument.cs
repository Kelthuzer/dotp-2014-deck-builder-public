namespace DeckBuilder.Core.Models;

public sealed class DeckDocument
{
    public const int MaximumPromoUnlocks = 10;
    public const string DefaultPersonality = "D14_SISTERS.XML";

    private string _personality = DefaultPersonality;

    public IList<DeckEntry> MainDeck { get; } = new List<DeckEntry>();

    public IList<DeckEntry> RegularUnlocks { get; } = new List<DeckEntry>();

    public IList<DeckEntry> PromoUnlocks { get; } = new List<DeckEntry>();

    public int Uid { get; set; } = -1;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Personality
    {
        get => string.IsNullOrWhiteSpace(_personality) ? DefaultPersonality : _personality;
        set => _personality = value ?? string.Empty;
    }

    public AiPersonalityDefinition? CustomPersonality { get; set; }

    public string DeckBoxImage { get; set; } = string.Empty;

    public string DeckBoxImageLocked { get; set; } = "locked";

    public int ContentPack { get; set; }

    public DeckAvailability Availability { get; set; } = DeckAvailability.AlwaysAvailable;

    public bool AlwaysAvailable
    {
        get => Availability == DeckAvailability.AlwaysAvailable;
        set => Availability = value ? DeckAvailability.AlwaysAvailable : DeckAvailability.NeverAvailable;
    }

    public bool OverrideColours { get; set; }

    public DeckColourFlags OverrideColour { get; set; } = DeckColourFlags.NotDefined;

    public string CreatureSize { get; set; } = "?";

    public string DeckSpeed { get; set; } = "?";

    public string Flexibility { get; set; } = "?";

    public string Synergy { get; set; } = "?";

    public int IgnoreCmcOver { get; set; } = -1;

    public int MinForests { get; set; }

    public int MinIslands { get; set; }

    public int MinMountains { get; set; }

    public int MinPlains { get; set; }

    public int MinSwamps { get; set; }

    public int NumberOfSpellsThatCountAsLand { get; set; }

    public int MainDeckCardCount => MainDeck.Sum(entry => entry.Quantity);

    public IList<DeckEntry> GetSection(DeckSection section) => section switch
    {
        DeckSection.MainDeck => MainDeck,
        DeckSection.RegularUnlocks => RegularUnlocks,
        DeckSection.PromoUnlocks => PromoUnlocks,
        _ => throw new ArgumentOutOfRangeException(nameof(section), section, null)
    };
}
