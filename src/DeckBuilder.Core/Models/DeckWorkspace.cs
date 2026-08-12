namespace DeckBuilder.Core.Models;

public sealed record DeckWorkspace(
    string Name,
    DeckDocument Deck,
    IReadOnlyList<CardRecord> Catalog);
