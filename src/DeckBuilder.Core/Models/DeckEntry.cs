using DeckBuilder.Core.Formats;

namespace DeckBuilder.Core.Models;

public sealed class DeckEntry
{
    public DeckEntry(CardRecord card, int quantity = 1, int bias = 1, bool promo = false, int orderId = -1)
    {
        Card = card ?? throw new ArgumentNullException(nameof(card));
        Quantity = quantity;
        Bias = bias;
        Promo = promo;
        OrderId = orderId;
    }

    public CardRecord Card { get; }

    public int Quantity
    {
        get => _quantity;
        set => _quantity = Math.Max(1, value);
    }

    public int Bias { get; set; }

    public bool Promo { get; set; }

    public int OrderId { get; set; }

    public string CardReference => DeckCardReference.Format(Card.FileName, Promo, Bias);

    private int _quantity;
}
