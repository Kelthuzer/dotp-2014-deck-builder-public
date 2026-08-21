using System.IO;
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

        if (target == DeckSection.MainDeck && !IsBasicLand(card))
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

    private static bool IsBasicLand(CardRecord card)
    {
        if (IsCanonicalBasicLandFileName(card.FileName))
            return true;

        string english = card.EnglishName.Trim();
        if (english.Equals("Plains", StringComparison.OrdinalIgnoreCase)
            || english.Equals("Island", StringComparison.OrdinalIgnoreCase)
            || english.Equals("Swamp", StringComparison.OrdinalIgnoreCase)
            || english.Equals("Mountain", StringComparison.OrdinalIgnoreCase)
            || english.Equals("Forest", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string localized = card.LocalizedName.Trim();
        if (localized.Equals("Равнина", StringComparison.OrdinalIgnoreCase)
            || localized.Equals("Остров", StringComparison.OrdinalIgnoreCase)
            || localized.Equals("Болото", StringComparison.OrdinalIgnoreCase)
            || localized.Equals("Гора", StringComparison.OrdinalIgnoreCase)
            || localized.Equals("Лес", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        bool isLand = card.TypeLine.Contains("Land", StringComparison.OrdinalIgnoreCase)
            || card.TypeLine.Contains("Земл", StringComparison.OrdinalIgnoreCase);
        if (!isLand)
            return false;

        return card.TypeLine.Contains("Basic", StringComparison.OrdinalIgnoreCase)
            || card.TypeLine.Contains("Базов", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCanonicalBasicLandFileName(string fileName)
    {
        string stem = (Path.GetFileNameWithoutExtension(fileName) ?? fileName).Trim();
        foreach (string basicName in new[] { "PLAINS", "ISLAND", "SWAMP", "MOUNTAIN", "FOREST" })
        {
            if (stem.Equals(basicName, StringComparison.OrdinalIgnoreCase))
                return true;

            string normalPrefix = basicName + "_";
            if (stem.StartsWith(normalPrefix, StringComparison.OrdinalIgnoreCase)
                && IsCanonicalBasicLandSuffix(stem[normalPrefix.Length..]))
            {
                return true;
            }

            string explicitBasicPrefix = "BASIC_" + basicName + "_";
            if (stem.StartsWith(explicitBasicPrefix, StringComparison.OrdinalIgnoreCase)
                && IsCanonicalBasicLandSuffix(stem[explicitBasicPrefix.Length..]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCanonicalBasicLandSuffix(string suffix)
    {
        if (suffix.Length > 0 && char.IsDigit(suffix[0]))
            return true;

        const string xmasCommunityPrefix = "CW_";
        return suffix.StartsWith(xmasCommunityPrefix, StringComparison.OrdinalIgnoreCase)
            && suffix.Length > xmasCommunityPrefix.Length
            && char.IsDigit(suffix[xmasCommunityPrefix.Length]);
    }
}
