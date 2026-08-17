using DeckBuilder.Core.Models;
using DeckBuilder.Core.Services;

namespace DeckBuilder.GameData;

/// <summary>
/// Lists every CARD_V2 that the generated deck WAD names directly: main deck, unlocks and the
/// hidden automatic land pool. Runtime/card recursion starts from this stable root set.
/// </summary>
public static class PortableDeckCardReferencePlanner
{
    public static IReadOnlyList<string> GetRequiredReferences(
        DeckDocument deck,
        IReadOnlyList<CardRecord> catalog)
    {
        ArgumentNullException.ThrowIfNull(deck);
        ArgumentNullException.ThrowIfNull(catalog);

        HashSet<string> references = new(StringComparer.OrdinalIgnoreCase);
        foreach (DeckEntry entry in deck.MainDeck.Concat(deck.RegularUnlocks).Concat(deck.PromoUnlocks))
        {
            // CardReference may contain deck syntax (#/@2); portable lookup always uses CARD_V2 FILENAME.
            if (!string.IsNullOrWhiteSpace(entry.Card.FileName))
                references.Add(entry.Card.FileName.Trim());
        }

        foreach (CardRecord land in DeckLandPoolSelector.Select(deck, catalog))
            references.Add(land.FileName.Trim());

        return references
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
