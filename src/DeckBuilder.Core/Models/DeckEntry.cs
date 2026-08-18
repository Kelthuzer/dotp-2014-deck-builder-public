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

    // Flat properties keep WPF DataGrid sorting deterministic. SortMemberPath does not need to
    // traverse Card.* and template columns (mana symbols) can sort by a numeric key.
    public string CardName => Card.LocalizedName;
    public string CardTypeLine => Card.TypeLine;
    public string CardRarity => Card.Rarity;
    public string CardCastingCost => Card.CastingCost;
    public int CardManaValue => CalculateManaValue(Card.CastingCost);
    public int CardRarityOrder => RarityOrder(Card.Rarity);

    private static int CalculateManaValue(string? cost)
    {
        if (string.IsNullOrWhiteSpace(cost))
            return 0;

        int total = 0;
        int position = 0;
        while (position < cost.Length)
        {
            int open = cost.IndexOf('{', position);
            if (open < 0)
                break;
            int close = cost.IndexOf('}', open + 1);
            if (close < 0)
                break;

            string symbol = cost[(open + 1)..close].Trim();
            if (int.TryParse(symbol, out int generic))
            {
                total += Math.Max(0, generic);
            }
            else if (!symbol.Equals("X", StringComparison.OrdinalIgnoreCase)
                     && !symbol.Equals("Y", StringComparison.OrdinalIgnoreCase)
                     && !symbol.Equals("Z", StringComparison.OrdinalIgnoreCase))
            {
                // Hybrid symbols such as {2/W} have mana value 2; ordinary colored, phyrexian,
                // snow and one-mana hybrid symbols contribute 1.
                string[] hybrid = symbol.Split('/');
                int hybridNumeric = hybrid
                    .Select(part => int.TryParse(part, out int value) ? value : 0)
                    .DefaultIfEmpty(0)
                    .Max();
                total += hybridNumeric > 0 ? hybridNumeric : 1;
            }

            position = close + 1;
        }

        return total;
    }

    private static int RarityOrder(string? rarity) => rarity?.Trim().ToUpperInvariant() switch
    {
        "C" or "COMMON" => 0,
        "U" or "UNCOMMON" => 1,
        "R" or "RARE" => 2,
        "M" or "MYTHIC" or "MYTHIC RARE" => 3,
        "T" or "TOKEN" => 4,
        _ => 5
    };

    private int _quantity;
}
