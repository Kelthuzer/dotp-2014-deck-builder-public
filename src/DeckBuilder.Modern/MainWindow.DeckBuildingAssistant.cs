using System.Text;
using System.Windows;
using DeckBuilder.Core.Models;

namespace DeckBuilder.Modern;

public partial class MainWindow
{
    // Kept only as an internal compatibility fallback for auto-land logic.
    // The assistant UI no longer exposes or modifies manual deck colors.
    private readonly HashSet<char> _assistantColors = new();

    private void DeckBuildingAssistant_Click(object sender, RoutedEventArgs e)
    {
        DeckBuildingAssistantWindow dialog = new(BuildDeckAssistantGuidance())
        {
            Owner = this
        };
        dialog.ShowDialog();
    }

    private string BuildDeckAssistantGuidance()
    {
        int totalCards = _deck.MainDeck.Sum(entry => entry.Quantity);
        int lands = _deck.MainDeck.Where(entry => IsLand(entry.Card)).Sum(entry => entry.Quantity);
        int spells = totalCards - lands;
        int artifacts = _deck.MainDeck
            .Where(entry => !IsLand(entry.Card) && IsArtifact(entry.Card))
            .Sum(entry => entry.Quantity);

        double averageManaValue = EstimateAverageManaValue();
        int suggestedLands = averageManaValue switch
        {
            > 0 and <= 2.2 => 23,
            >= 3.8 => 25,
            _ => 24
        };

        Dictionary<char, int> pips = CountColoredManaPips();
        int coloredPips = pips.Values.Sum();
        StringBuilder text = new();

        if (AppLocalization.IsRussian)
        {
            text.AppendLine($"Основная колода: {totalCards} карт ({lands} земель / {spells} не-земель)");
            text.AppendLine($"Артефактов/бесцветных перманентов: {artifacts}");
            text.AppendLine($"Оценочная средняя мана-стоимость: {(averageManaValue > 0 ? averageManaValue.ToString("0.0") : "—")}");
            text.AppendLine();
            text.AppendLine("Стартовый ориентир для колоды из 60 карт:");
            text.AppendLine("  • целевой размер: 60 карт");
            text.AppendLine($"  • земель по текущей кривой: примерно {suggestedLands}");
            text.AppendLine("  • потребность в цветных источниках определяется символами маны в самих картах");
            text.AppendLine();

            if (coloredPips == 0)
            {
                text.AppendLine("Потребность в цветной мане пока не обнаружена.");
                text.AppendLine("Для артефактной/бесцветной основы цветные источники не требуются, пока в колоде нет цветных символов маны.");
            }
            else
            {
                text.AppendLine("Цветные символы маны в текущей колоде:");
                AppendColoredSourceGuidance(text, pips, coloredPips, suggestedLands, true);
            }

            text.AppendLine();
            text.AppendLine("Помощник ничего не фильтрует и не изменяет автоматически. Это шпаргалка и проверка адекватности колоды: размер, земли, кривая и цветные источники.");
        }
        else
        {
            text.AppendLine($"Main deck: {totalCards} cards ({lands} lands / {spells} nonlands)");
            text.AppendLine($"Artifacts/colorless permanents in main deck: {artifacts}");
            text.AppendLine($"Estimated average mana value: {(averageManaValue > 0 ? averageManaValue.ToString("0.0") : "—")}");
            text.AppendLine();
            text.AppendLine("Starting point for a 60-card deck:");
            text.AppendLine("  • target size: 60 cards");
            text.AppendLine($"  • suggested lands from current curve: about {suggestedLands}");
            text.AppendLine("  • colored source demand follows the mana symbols present in the cards");
            text.AppendLine();

            if (coloredPips == 0)
            {
                text.AppendLine("Colored mana demand: none detected yet.");
                text.AppendLine("An artifact-heavy/colorless shell needs no colored sources until colored mana symbols appear in the deck.");
            }
            else
            {
                text.AppendLine("Colored mana symbols currently in the deck:");
                AppendColoredSourceGuidance(text, pips, coloredPips, suggestedLands, false);
            }

            text.AppendLine();
            text.AppendLine("The assistant does not filter or modify anything automatically. It is a reference and sanity check for deck size, lands, curve and colored sources.");
        }

        return text.ToString();
    }

    private static void AppendColoredSourceGuidance(
        StringBuilder text,
        IReadOnlyDictionary<char, int> pips,
        int coloredPips,
        int suggestedLands,
        bool russian)
    {
        foreach (char color in "WUBRG")
        {
            int count = pips[color];
            if (count == 0)
                continue;

            double share = count / (double)coloredPips;
            int approximateSources = Math.Max(1, (int)Math.Round(suggestedLands * share));
            text.AppendLine(russian
                ? $"  • {ColorName(color, true)}: {count} символов ({share:P0}) → ориентировочно {approximateSources} из {suggestedLands} земельных слотов"
                : $"  • {ColorName(color, false)}: {count} pips ({share:P0}) → roughly {approximateSources} of {suggestedLands} land slots as a first pass");
        }
    }

    private double EstimateAverageManaValue()
    {
        int quantity = 0;
        int value = 0;
        foreach (DeckEntry entry in _deck.MainDeck.Where(entry => !IsLand(entry.Card)))
        {
            int mv = EstimateManaValue(entry.Card.CastingCost);
            value += mv * entry.Quantity;
            quantity += entry.Quantity;
        }

        return quantity == 0 ? 0 : value / (double)quantity;
    }

    private static int EstimateManaValue(string cost)
    {
        if (string.IsNullOrWhiteSpace(cost))
            return 0;

        string normalized = cost.Trim().ToUpperInvariant();
        if (normalized.Contains('{'))
        {
            int total = 0;
            int index = 0;
            while (index < normalized.Length)
            {
                int open = normalized.IndexOf('{', index);
                if (open < 0)
                    break;

                int close = normalized.IndexOf('}', open + 1);
                if (close < 0)
                    break;

                string symbol = normalized[(open + 1)..close].Trim();
                total += ManaSymbolValue(symbol);
                index = close + 1;
            }

            return total;
        }

        int fallbackTotal = 0;
        for (int i = 0; i < normalized.Length;)
        {
            char c = normalized[i];
            if (char.IsDigit(c))
            {
                int number = 0;
                while (i < normalized.Length && char.IsDigit(normalized[i]))
                {
                    number = checked(number * 10 + (normalized[i] - '0'));
                    i++;
                }
                fallbackTotal += number;
                continue;
            }

            if (c == 'X')
            {
                i++;
                continue;
            }

            if ("WUBRGC".Contains(c))
            {
                if (i + 2 < normalized.Length
                    && normalized[i + 1] == '/'
                    && "WUBRGCP".Contains(normalized[i + 2]))
                {
                    fallbackTotal++;
                    i += 3;
                    continue;
                }

                fallbackTotal++;
            }

            i++;
        }

        return fallbackTotal;
    }

    private static int ManaSymbolValue(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol) || symbol == "X")
            return 0;

        if (int.TryParse(symbol, out int generic))
            return generic;

        if (symbol.Contains('/'))
        {
            string[] parts = symbol.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length > 0 && int.TryParse(parts[0], out int hybridGeneric))
                return hybridGeneric;
            return 1;
        }

        return "WUBRGCSP".Contains(symbol[0]) ? 1 : 0;
    }

    private Dictionary<char, int> CountColoredManaPips()
    {
        Dictionary<char, int> result = "WUBRG".ToDictionary(color => color, _ => 0);
        foreach (DeckEntry entry in _deck.MainDeck.Where(entry => !IsLand(entry.Card)))
        {
            foreach (char c in entry.Card.CastingCost.ToUpperInvariant())
            {
                if (result.ContainsKey(c))
                    result[c] += entry.Quantity;
            }
        }
        return result;
    }

    internal static HashSet<char> ExtractSpellColors(CardRecord card)
    {
        HashSet<char> colors = new();
        foreach (char c in card.CastingCost.ToUpperInvariant())
        {
            if ("WUBRG".Contains(c)) colors.Add(c);
        }

        if (colors.Count > 0) return colors;

        string colour = card.Colour.ToUpperInvariant();
        if (colour.Contains("WHITE")) colors.Add('W');
        if (colour.Contains("BLUE")) colors.Add('U');
        if (colour.Contains("BLACK")) colors.Add('B');
        if (colour.Contains("RED")) colors.Add('R');
        if (colour.Contains("GREEN")) colors.Add('G');
        if (colour.Length <= 5)
        {
            foreach (char c in colour)
                if ("WUBRG".Contains(c)) colors.Add(c);
        }
        return colors;
    }

    internal static HashSet<char> BasicLandColors(CardRecord card)
    {
        string name = card.FileName.ToUpperInvariant();
        HashSet<char> colors = new();
        if (name.Contains("PLAINS")) colors.Add('W');
        if (name.Contains("ISLAND")) colors.Add('U');
        if (name.Contains("SWAMP")) colors.Add('B');
        if (name.Contains("MOUNTAIN")) colors.Add('R');
        if (name.Contains("FOREST")) colors.Add('G');
        return colors;
    }

    internal static bool IsLand(CardRecord card) =>
        card.TypeLine.Contains("Land", StringComparison.OrdinalIgnoreCase)
        || card.TypeLine.Contains("Земл", StringComparison.OrdinalIgnoreCase);

    private static bool IsArtifact(CardRecord card) =>
        card.TypeLine.Contains("Artifact", StringComparison.OrdinalIgnoreCase)
        || card.TypeLine.Contains("Артефакт", StringComparison.OrdinalIgnoreCase);

    private static string ColorName(char color, bool russian) => color switch
    {
        'W' => russian ? "Белый" : "White",
        'U' => russian ? "Синий" : "Blue",
        'B' => russian ? "Чёрный" : "Black",
        'R' => russian ? "Красный" : "Red",
        'G' => russian ? "Зелёный" : "Green",
        _ => color.ToString()
    };
}
