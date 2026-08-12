using DeckBuilder.Core.Models;

namespace DeckBuilder.Core.Services;

public static class DeckDocumentCloner
{
    public static DeckDocument Clone(DeckDocument source, int uid = -1, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        DeckDocument copy = new()
        {
            Uid = uid,
            Name = name ?? source.Name,
            Description = source.Description,
            Personality = source.Personality,
            CustomPersonality = source.CustomPersonality?.Clone(),
            DeckBoxImage = source.DeckBoxImage,
            DeckBoxImageLocked = source.DeckBoxImageLocked,
            ContentPack = source.ContentPack,
            Availability = source.Availability,
            OverrideColours = source.OverrideColours,
            OverrideColour = source.OverrideColour,
            CreatureSize = source.CreatureSize,
            DeckSpeed = source.DeckSpeed,
            Flexibility = source.Flexibility,
            Synergy = source.Synergy,
            IgnoreCmcOver = source.IgnoreCmcOver,
            MinForests = source.MinForests,
            MinIslands = source.MinIslands,
            MinMountains = source.MinMountains,
            MinPlains = source.MinPlains,
            MinSwamps = source.MinSwamps,
            NumberOfSpellsThatCountAsLand = source.NumberOfSpellsThatCountAsLand
        };

        CopySection(source.MainDeck, copy.MainDeck);
        CopySection(source.RegularUnlocks, copy.RegularUnlocks);
        CopySection(source.PromoUnlocks, copy.PromoUnlocks);
        return copy;
    }

    private static void CopySection(IEnumerable<DeckEntry> source, IList<DeckEntry> target)
    {
        foreach (DeckEntry entry in source)
        {
            target.Add(new DeckEntry(entry.Card, entry.Quantity, entry.Bias, entry.Promo, entry.OrderId));
        }
    }
}
