using DeckBuilder.Core.Models;

namespace DeckBuilder.Core.Services;

/// <summary>
/// Builds searchable text once, instead of recomputing it for every key press.
/// Every word in a query must occur, matching the behavior of the modernized legacy UI.
/// </summary>
public sealed class CardSearchIndex
{
    private readonly IReadOnlyList<IndexedCard> _cards;

    public CardSearchIndex(IEnumerable<CardRecord> cards)
    {
        ArgumentNullException.ThrowIfNull(cards);
        _cards = cards.Select(card => new IndexedCard(card, BuildSearchText(card))).ToArray();
    }

    public IReadOnlyList<CardRecord> Search(string? query)
    {
        string[] words = (query ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (words.Length == 0)
        {
            return _cards.Select(item => item.Card).ToArray();
        }

        return _cards
            .Where(item => words.All(word => item.SearchText.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .Select(item => item.Card)
            .ToArray();
    }

    private static string BuildSearchText(CardRecord card) => string.Join(" ", new[]
    {
        card.LocalizedName,
        card.EnglishName,
        card.FileName,
        card.TypeLine,
        card.Expansion,
        card.Artist,
        card.Source
    });

    private sealed record IndexedCard(CardRecord Card, string SearchText);
}
