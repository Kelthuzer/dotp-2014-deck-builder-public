namespace DeckBuilder.Core.Models;

/// <summary>
/// UI-independent card data used by the modern editor.
/// Images and legacy WAD handles deliberately do not belong in this model.
/// </summary>
public sealed class CardRecord
{
    private readonly string _localizedName;
    private readonly string _source;
    private readonly string _imageId;

    public CardRecord(
        string fileName,
        string? localizedName,
        string? englishName,
        string? typeLine,
        string? expansion,
        string? artist,
        string? castingCost = null,
        string? colour = null,
        string? rarity = null,
        string? power = null,
        string? toughness = null,
        string? source = null,
        string? imageId = null,
        string? rulesText = null,
        string? flavorText = null,
        string? frameType = null,
        bool isToken = false)
    {
        FileName = Require(fileName, nameof(fileName));
        _localizedName = localizedName ?? string.Empty;
        EnglishName = englishName ?? string.Empty;
        TypeLine = typeLine ?? string.Empty;
        Expansion = expansion ?? string.Empty;
        Artist = artist ?? string.Empty;
        CastingCost = castingCost ?? string.Empty;
        Colour = colour ?? string.Empty;
        Rarity = rarity ?? string.Empty;
        Power = power ?? string.Empty;
        Toughness = toughness ?? string.Empty;
        _source = source ?? string.Empty;
        _imageId = imageId ?? string.Empty;
        RulesText = rulesText ?? string.Empty;
        FlavorText = flavorText ?? string.Empty;
        FrameType = frameType ?? string.Empty;
        IsToken = isToken;
    }

    public string FileName { get; }

    /// <summary>
    /// Placeholders created from a deck reference with no matching CARD_V2 definition are
    /// deliberately kept in the deck so the original DotP reference is never lost. Make
    /// that state explicit in the UI instead of pretending the raw reference is a normal card name.
    /// </summary>
    public bool IsMissingDefinition =>
        string.IsNullOrWhiteSpace(TypeLine)
        && string.IsNullOrWhiteSpace(Expansion)
        && string.IsNullOrWhiteSpace(Artist)
        && string.IsNullOrWhiteSpace(CastingCost)
        && string.IsNullOrWhiteSpace(Colour)
        && string.IsNullOrWhiteSpace(Rarity)
        && string.IsNullOrWhiteSpace(Power)
        && string.IsNullOrWhiteSpace(Toughness)
        && string.IsNullOrWhiteSpace(_imageId)
        && string.IsNullOrWhiteSpace(RulesText)
        && string.IsNullOrWhiteSpace(FlavorText)
        && string.IsNullOrWhiteSpace(FrameType)
        && !IsToken;

    public string LocalizedName
    {
        get
        {
            if (!IsMissingDefinition)
            {
                return _localizedName;
            }

            if (_localizedName.StartsWith("[Missing definition]", StringComparison.OrdinalIgnoreCase))
            {
                return _localizedName;
            }

            return $"[Missing definition] {FileName}";
        }
    }

    public string EnglishName { get; }
    public string TypeLine { get; }
    public string Expansion { get; }
    public string Artist { get; }
    public string CastingCost { get; }
    public string Colour { get; }
    public string Rarity { get; }
    public string Power { get; }
    public string Toughness { get; }
    public string Source => IsMissingDefinition && string.IsNullOrWhiteSpace(_source)
        ? "Missing game definition"
        : _source;

    /// <summary>
    /// Normal cards use the ARTID read from CARD_V2. A built-in stock deck can reference a
    /// playable card for which no CARD_V2 XML is present in the editor-visible WAD data. In that
    /// case probe the illustration index with the exact game reference. This is read-only UI
    /// fallback; the original deck reference is never rewritten.
    /// </summary>
    public string ImageId => IsMissingDefinition ? FileName : _imageId;

    public string RulesText { get; }
    public string FlavorText { get; }
    public string FrameType { get; }
    public bool IsToken { get; }

    private static string Require(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A card filename is required.", parameterName);
        }

        return value.Trim();
    }
}
