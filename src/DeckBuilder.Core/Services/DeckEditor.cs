using DeckBuilder.Core.Models;

namespace DeckBuilder.Core.Services;

public sealed class DeckEditor
{
    private const int MaximumConstructedCopies = 4;
    private readonly DeckDocument _deck;

    public DeckEditor(DeckDocument deck)
    {
        _deck = deck ?? throw new ArgumentNullException(nameof(deck));
    }

    public DeckEntry Add(CardRecord card, DeckSection target, int bias = 1, bool promo = false)
    {
        ArgumentNullException.ThrowIfNull(card);
        IList<DeckEntry> entries = _deck.GetSection(target);

        if (target == DeckSection.PromoUnlocks && entries.Count >= DeckDocument.MaximumPromoUnlocks)
        {
            throw new InvalidOperationException("A DotP 2014 deck can contain at most 10 promo unlocks.");
        }

        if (target == DeckSection.MainDeck && !CardLandClassification.IsBasicLand(card))
        {
            string identity = CardIdentity(card);
            int copies = entries
                .Where(entry => CardIdentity(entry.Card).Equals(identity, StringComparison.OrdinalIgnoreCase))
                .Sum(entry => entry.Quantity);

            if (copies >= MaximumConstructedCopies)
            {
                string name = string.IsNullOrWhiteSpace(card.LocalizedName)
                    ? identity
                    : card.LocalizedName;
                throw new InvalidOperationException(
                    $"'{name}' is already at the constructed limit of {MaximumConstructedCopies} copies.");
            }
        }

        if (target == DeckSection.MainDeck)
        {
            DeckEntry? existing = entries.FirstOrDefault(entry =>
                ReferenceEquals(entry.Card, card)
                && entry.Bias == bias
                && entry.Promo == promo);
            if (existing is not null)
            {
                existing.Quantity++;
                return existing;
            }
        }

        DeckEntry added = new(card, 1, bias, promo);
        entries.Add(added);
        return added;
    }

    public bool Remove(DeckEntry entry, DeckSection source, int quantity = 1)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (quantity == 0)
        {
            return false;
        }

        IList<DeckEntry> entries = _deck.GetSection(source);
        int index = entries.IndexOf(entry);
        if (index < 0)
        {
            return false;
        }

        if (source == DeckSection.MainDeck && quantity > 0 && entry.Quantity > quantity)
        {
            entry.Quantity -= quantity;
        }
        else
        {
            entries.RemoveAt(index);
        }

        return true;
    }

    public DeckEntry Move(DeckEntry entry, DeckSection source, DeckSection target)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (source == target)
        {
            return entry;
        }

        DeckEntry added = Add(entry.Card, target, entry.Bias, entry.Promo);
        Remove(entry, source);
        return added;
    }

    public void Reorder(DeckEntry entry, DeckSection section, int targetIndex)
    {
        IList<DeckEntry> entries = _deck.GetSection(section);
        int oldIndex = entries.IndexOf(entry);
        if (oldIndex < 0)
        {
            throw new ArgumentException("The entry does not belong to this deck section.", nameof(entry));
        }

        entries.RemoveAt(oldIndex);
        int boundedIndex = Math.Clamp(targetIndex, 0, entries.Count);
        entries.Insert(boundedIndex, entry);
    }

    private static string CardIdentity(CardRecord card)
    {
        if (!string.IsNullOrWhiteSpace(card.EnglishName))
            return card.EnglishName.Trim();
        if (!string.IsNullOrWhiteSpace(card.LocalizedName))
            return card.LocalizedName.Trim();
        return card.FileName.Trim();
    }
}
