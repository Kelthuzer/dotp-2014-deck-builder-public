using DeckBuilder.Core.Models;

namespace DeckBuilder.GameData;

public sealed record InstalledDeckRecord(string FileName, string Source, DeckDocument Deck)
{
    public int Uid => Deck.Uid;

    internal string? ResolvedGameName { get; set; }

    public string DisplayName => FriendlyName;

    public string FriendlyName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ResolvedGameName)
                && !LooksTechnical(ResolvedGameName))
            {
                return ResolvedGameName.Trim();
            }

            if (!string.IsNullOrWhiteSpace(Deck.Name)
                && !LooksTechnical(Deck.Name))
            {
                return Deck.Name.Trim();
            }

            return PrettifyTechnicalName(FileName);
        }
    }

    public string TechnicalName => FileName;

    public int CardCount => Deck.MainDeckCardCount;

    public int RegularUnlockCount => Deck.RegularUnlocks.Count;

    public int PromoUnlockCount => Deck.PromoUnlocks.Count;

    public IReadOnlyList<string> MissingCardReferences => Deck.MainDeck
        .Concat(Deck.RegularUnlocks)
        .Concat(Deck.PromoUnlocks)
        .Where(entry => entry.Card.IsMissingDefinition)
        .Select(entry => entry.Card.FileName)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(reference => reference, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public int MissingCardCount => MissingCardReferences.Count;

    public string MissingReferencesText => MissingCardCount == 0
        ? string.Empty
        : $"Missing card definitions: {string.Join(", ", MissingCardReferences)}";

    internal static bool LooksTechnical(string value)
    {
        string text = value.Trim();
        return text.StartsWith("DECK_", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("D14_", StringComparison.OrdinalIgnoreCase)
            || text.All(ch => char.IsUpper(ch) || char.IsDigit(ch) || ch is '_' or '-' or ' ');
    }

    private static string PrettifyTechnicalName(string value)
    {
        string text = Path.GetFileNameWithoutExtension(value) ?? value;
        string[] parts = text.Split('_', StringSplitOptions.RemoveEmptyEntries);
        List<string> words = new();
        foreach (string part in parts)
        {
            if (part.Equals("D14", StringComparison.OrdinalIgnoreCase)
                || part.Equals("DECK", StringComparison.OrdinalIgnoreCase)
                || part.All(char.IsDigit))
            {
                continue;
            }

            words.Add(part.Length <= 3
                ? part.ToUpperInvariant()
                : char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant());
        }

        return words.Count == 0 ? text : string.Join(' ', words);
    }
}

public sealed record GameDeckCatalogLoadResult(
    IReadOnlyList<InstalledDeckRecord> Decks,
    IReadOnlyList<string> Warnings);
