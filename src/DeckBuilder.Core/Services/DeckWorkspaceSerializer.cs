using System.Text.Json;
using DeckBuilder.Core.Formats;
using DeckBuilder.Core.Models;

namespace DeckBuilder.Core.Services;

public static class DeckWorkspaceSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static async Task SaveAsync(string path, DeckWorkspace workspace, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(workspace);

        WorkspaceFile file = new(
            1,
            workspace.Name,
            workspace.Catalog.Select(CardFile.FromCard).ToArray(),
            workspace.Deck.MainDeck.Select(EntryFile.FromEntry).ToArray(),
            workspace.Deck.RegularUnlocks.Select(EntryFile.FromEntry).ToArray(),
            workspace.Deck.PromoUnlocks.Select(EntryFile.FromEntry).ToArray(),
            DeckMetadataFile.FromDeck(workspace.Deck));

        await using FileStream output = File.Create(path);
        await JsonSerializer.SerializeAsync(output, file, JsonOptions, cancellationToken);
    }

    public static async Task<DeckWorkspace> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using FileStream input = File.OpenRead(path);
        WorkspaceFile file = await JsonSerializer.DeserializeAsync<WorkspaceFile>(input, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The deck project is empty or invalid.");

        if (file.Version != 1)
        {
            throw new InvalidDataException($"Unsupported deck project version: {file.Version}.");
        }

        CardRecord[] catalog = file.Catalog.Select(card => card.ToCard()).ToArray();
        Dictionary<string, CardRecord> cards = catalog.ToDictionary(card => card.FileName, StringComparer.OrdinalIgnoreCase);
        DeckDocument deck = new();
        LoadEntries(file.MainDeck, deck.MainDeck, cards);
        LoadEntries(file.RegularUnlocks, deck.RegularUnlocks, cards);
        LoadEntries(file.PromoUnlocks, deck.PromoUnlocks, cards);
        file.Metadata?.ApplyTo(deck);
        return new DeckWorkspace(file.Name, deck, cards.Values.ToArray());
    }

    private static void LoadEntries(IEnumerable<EntryFile> source, IList<DeckEntry> target, IDictionary<string, CardRecord> cards)
    {
        foreach (EntryFile saved in source)
        {
            DeckCardReference reference = DeckCardReference.Parse(saved.CardReference);
            if (!cards.TryGetValue(reference.FileName, out CardRecord? card))
            {
                card = new CardRecord(reference.FileName, reference.FileName, reference.FileName, null, null, null);
                cards[reference.FileName] = card;
            }

            target.Add(new DeckEntry(card, saved.Quantity, reference.Bias, reference.Promo, saved.OrderId));
        }
    }

    private sealed record WorkspaceFile(
        int Version,
        string Name,
        IReadOnlyList<CardFile> Catalog,
        IReadOnlyList<EntryFile> MainDeck,
        IReadOnlyList<EntryFile> RegularUnlocks,
        IReadOnlyList<EntryFile> PromoUnlocks,
        DeckMetadataFile? Metadata);

    private sealed record DeckMetadataFile(
        int Uid,
        string? Name,
        string? Description,
        string? Personality,
        string? DeckBoxImage,
        string? DeckBoxImageLocked,
        int ContentPack,
        bool AlwaysAvailable,
        string? Availability,
        bool OverrideColours,
        int? OverrideColour,
        string? CreatureSize,
        string? DeckSpeed,
        string? Flexibility,
        string? Synergy,
        int? IgnoreCmcOver,
        int? MinForests,
        int? MinIslands,
        int? MinMountains,
        int? MinPlains,
        int? MinSwamps,
        int? NumberOfSpellsThatCountAsLand,
        PersonalityFile? CustomPersonality)
    {
        public static DeckMetadataFile FromDeck(DeckDocument deck) => new(
            deck.Uid,
            deck.Name,
            deck.Description,
            deck.Personality,
            deck.DeckBoxImage,
            deck.DeckBoxImageLocked,
            deck.ContentPack,
            deck.AlwaysAvailable,
            deck.Availability.ToString(),
            deck.OverrideColours,
            (int)deck.OverrideColour,
            deck.CreatureSize,
            deck.DeckSpeed,
            deck.Flexibility,
            deck.Synergy,
            deck.IgnoreCmcOver,
            deck.MinForests,
            deck.MinIslands,
            deck.MinMountains,
            deck.MinPlains,
            deck.MinSwamps,
            deck.NumberOfSpellsThatCountAsLand,
            PersonalityFile.FromPersonality(deck.CustomPersonality));

        public void ApplyTo(DeckDocument deck)
        {
            deck.Uid = Uid;
            deck.Name = Name ?? string.Empty;
            deck.Description = Description ?? string.Empty;
            deck.Personality = Personality ?? string.Empty;
            deck.CustomPersonality = CustomPersonality?.ToPersonality();
            deck.DeckBoxImage = DeckBoxImage ?? string.Empty;
            deck.DeckBoxImageLocked = string.IsNullOrWhiteSpace(DeckBoxImageLocked) ? "locked" : DeckBoxImageLocked;
            deck.ContentPack = ContentPack;
            if (!string.IsNullOrWhiteSpace(Availability)
                && Enum.TryParse(Availability, ignoreCase: true, out DeckAvailability parsedAvailability))
            {
                deck.Availability = parsedAvailability;
            }
            else
            {
                deck.AlwaysAvailable = AlwaysAvailable;
            }

            deck.OverrideColours = OverrideColours;
            deck.OverrideColour = OverrideColour.HasValue
                ? (DeckColourFlags)OverrideColour.Value
                : DeckColourFlags.NotDefined;
            deck.CreatureSize = string.IsNullOrWhiteSpace(CreatureSize) ? "?" : CreatureSize;
            deck.DeckSpeed = string.IsNullOrWhiteSpace(DeckSpeed) ? "?" : DeckSpeed;
            deck.Flexibility = string.IsNullOrWhiteSpace(Flexibility) ? "?" : Flexibility;
            deck.Synergy = string.IsNullOrWhiteSpace(Synergy) ? "?" : Synergy;
            deck.IgnoreCmcOver = IgnoreCmcOver ?? -1;
            deck.MinForests = MinForests ?? 0;
            deck.MinIslands = MinIslands ?? 0;
            deck.MinMountains = MinMountains ?? 0;
            deck.MinPlains = MinPlains ?? 0;
            deck.MinSwamps = MinSwamps ?? 0;
            deck.NumberOfSpellsThatCountAsLand = NumberOfSpellsThatCountAsLand ?? 0;
        }
    }

    private sealed record PersonalityFile(
        string? FileName,
        string? DisplayName,
        string? NameTag,
        string? LargeAvatarImage,
        string? SmallAvatarImage,
        string? SmallAvatarLockedImage,
        string? LobbyImage,
        string? Music)
    {
        public static PersonalityFile? FromPersonality(AiPersonalityDefinition? personality) => personality is null
            ? null
            : new PersonalityFile(
                personality.FileName,
                personality.DisplayName,
                personality.NameTag,
                personality.LargeAvatarImage,
                personality.SmallAvatarImage,
                personality.SmallAvatarLockedImage,
                personality.LobbyImage,
                personality.Music);

        public AiPersonalityDefinition ToPersonality() => AiPersonalityXmlSerializer.NormalizeIdentifiers(new AiPersonalityDefinition
        {
            FileName = FileName ?? string.Empty,
            DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? "New Personality" : DisplayName,
            NameTag = NameTag ?? string.Empty,
            LargeAvatarImage = LargeAvatarImage ?? string.Empty,
            SmallAvatarImage = SmallAvatarImage ?? string.Empty,
            SmallAvatarLockedImage = SmallAvatarLockedImage ?? string.Empty,
            LobbyImage = LobbyImage ?? string.Empty,
            Music = Music ?? string.Empty
        });
    }

    private sealed record CardFile(
        string FileName,
        string LocalizedName,
        string EnglishName,
        string TypeLine,
        string Expansion,
        string Artist,
        string CastingCost,
        string Colour,
        string Rarity,
        string Power,
        string Toughness,
        string Source,
        string? ImageId,
        string? RulesText,
        string? FlavorText,
        string? FrameType,
        bool IsToken)
    {
        public static CardFile FromCard(CardRecord card) => new(
            card.FileName,
            card.LocalizedName,
            card.EnglishName,
            card.TypeLine,
            card.Expansion,
            card.Artist,
            card.CastingCost,
            card.Colour,
            card.Rarity,
            card.Power,
            card.Toughness,
            card.Source,
            card.ImageId,
            card.RulesText,
            card.FlavorText,
            card.FrameType,
            card.IsToken);

        public CardRecord ToCard() => new(
            FileName,
            LocalizedName,
            EnglishName,
            TypeLine,
            Expansion,
            Artist,
            CastingCost,
            Colour,
            Rarity,
            Power,
            Toughness,
            Source,
            ImageId,
            RulesText,
            FlavorText,
            FrameType,
            IsToken);
    }

    private sealed record EntryFile(string CardReference, int Quantity, int OrderId)
    {
        public static EntryFile FromEntry(DeckEntry entry) => new(entry.CardReference, entry.Quantity, entry.OrderId);
    }
}
