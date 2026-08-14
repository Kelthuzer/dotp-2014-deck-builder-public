using System.Windows;
using System.Windows.Controls;
using DeckBuilder.Core.Models;
using DeckBuilder.Core.Services;

namespace DeckBuilder.Modern;

public partial class MainWindow
{
    private bool _randomDeckMenuInstalled;

    internal void InstallRandomDeckMenu()
    {
        if (_randomDeckMenuInstalled || Content is not DockPanel root)
            return;

        Menu? menu = root.Children.OfType<Menu>().FirstOrDefault();
        MenuItem? deckMenu = menu?.Items.OfType<MenuItem>()
            .FirstOrDefault(item => (item.Header?.ToString() ?? string.Empty)
                .Replace("_", string.Empty, StringComparison.Ordinal)
                .Equals("Deck", StringComparison.OrdinalIgnoreCase));
        if (deckMenu is null)
            return;

        MenuItem randomDeck = new() { Header = "_Random deck…" };
        randomDeck.Click += RandomDeck_Click;

        int insertIndex = Math.Min(2, deckMenu.Items.Count);
        deckMenu.Items.Insert(insertIndex, randomDeck);
        _randomDeckMenuInstalled = true;
    }

    private void RandomDeck_Click(object sender, RoutedEventArgs e)
    {
        bool ru = AppLocalization.IsRussian;
        if (_catalog.Count == 0)
        {
            MessageBox.Show(
                this,
                ru
                    ? "Сначала загрузите распакованные ресурсы Magic 2014, чтобы генератор видел карты и базовые земли."
                    : "Load Magic 2014 game data or an unpacked workspace first so the generator can see cards and basic lands.",
                ru ? "Нет карт" : "No cards loaded",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        RandomDeckColorDialog dialog = new() { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        if (_dirty && MessageBox.Show(
                this,
                ru
                    ? "Случайная колода заменит текущую несохранённую колоду. Продолжить?"
                    : "The random deck will replace the current unsaved deck. Continue?",
                ru ? "Несохранённая колода" : "Unsaved deck",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            GenerateRandomDeck(dialog.SelectedColors, dialog.IncludeColorless);
        }
        catch (Exception exception)
        {
            ShowError(ru ? "Не удалось создать случайную колоду" : "Could not create the random deck", exception);
        }
    }

    private void GenerateRandomDeck(IReadOnlySet<char> selectedColors, bool includeColorless)
    {
        if (selectedColors.Count == 0)
            throw new InvalidOperationException(AppLocalization.IsRussian
                ? "Выберите хотя бы один цвет маны."
                : "Choose at least one mana color.");

        const int spellCount = 36;
        const int landCount = 24;

        List<CardRecord> eligible = _catalog
            .Where(card => !card.IsToken && !card.IsMissingDefinition && !IsLand(card))
            .Where(card =>
            {
                HashSet<char> colors = ExtractSpellColors(card);
                return colors.Count == 0
                    ? includeColorless
                    : colors.All(selectedColors.Contains);
            })
            .GroupBy(CardIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(group => PickRandom(group.ToList()))
            .ToList();

        List<CardRecord> colored = eligible
            .Where(card => ExtractSpellColors(card).Count > 0)
            .ToList();
        List<CardRecord> colorless = eligible
            .Where(card => ExtractSpellColors(card).Count == 0)
            .ToList();

        if (colored.Count < selectedColors.Count)
            throw new InvalidOperationException(AppLocalization.IsRussian
                ? "В загруженном каталоге недостаточно карт выбранных цветов."
                : "The loaded catalog does not contain enough cards in the selected colors.");

        List<CardRecord> chosenSpells = new();
        HashSet<string> chosenNames = new(StringComparer.OrdinalIgnoreCase);

        // Guarantee that every requested color actually appears in the generated spell suite.
        foreach (char color in selectedColors.OrderBy(color => "WUBRG".IndexOf(color)))
        {
            List<CardRecord> candidates = colored
                .Where(card => ExtractSpellColors(card).Contains(color))
                .Where(card => !chosenNames.Contains(CardIdentity(card)))
                .ToList();
            if (candidates.Count == 0)
                throw new InvalidOperationException(AppLocalization.IsRussian
                    ? $"Не удалось найти отдельную карту для цвета {color}."
                    : $"Could not find a distinct spell for color {color}.");

            CardRecord card = PickRandom(candidates);
            chosenSpells.Add(card);
            chosenNames.Add(CardIdentity(card));
        }

        int desiredColorless = includeColorless ? Math.Min(6, Math.Max(0, spellCount - selectedColors.Count)) : 0;
        int desiredColored = spellCount - desiredColorless;

        AddRandomDistinct(colored, chosenSpells, chosenNames, desiredColored);
        if (chosenSpells.Count < spellCount && desiredColorless > 0)
            AddRandomDistinct(colorless, chosenSpells, chosenNames, spellCount);
        if (chosenSpells.Count < spellCount)
            AddRandomDistinct(colored, chosenSpells, chosenNames, spellCount);
        if (chosenSpells.Count < spellCount)
            AddRandomDistinct(colorless, chosenSpells, chosenNames, spellCount);

        if (chosenSpells.Count < spellCount)
            throw new InvalidOperationException(AppLocalization.IsRussian
                ? $"Для выбранных цветов найдено только {chosenSpells.Count} уникальных не-земель. Нужно минимум {spellCount}."
                : $"Only {chosenSpells.Count} unique nonland cards were found for the selected colors; at least {spellCount} are required.");

        _deck = new DeckDocument();
        _editor = new DeckEditor(_deck);
        _projectPath = null;

        string colorCode = string.Concat("WUBRG".Where(selectedColors.Contains));
        _projectName = AppLocalization.IsRussian
            ? $"Случайная {colorCode} колода"
            : $"Random {colorCode} deck";
        _deck.Name = _projectName;

        _assistantColors.Clear();
        foreach (char color in selectedColors)
            _assistantColors.Add(color);

        foreach (CardRecord card in chosenSpells.Take(spellCount))
            _editor.Add(card, DeckSection.MainDeck);

        Dictionary<char, int> demand = "WUBRG".ToDictionary(color => color, _ => 0);
        foreach (CardRecord card in chosenSpells.Take(spellCount))
        {
            foreach (char color in card.CastingCost.ToUpperInvariant())
            {
                if (demand.ContainsKey(color))
                    demand[color]++;
            }
        }
        foreach (char color in selectedColors)
        {
            if (demand[color] == 0)
                demand[color] = 1;
        }

        Dictionary<char, List<CardRecord>> landsByColor = new();
        foreach (char color in selectedColors)
        {
            List<CardRecord> variants = _catalog
                .Where(card => IsBasicLand(card) && BasicLandColors(card).Contains(color))
                .GroupBy(card => card.FileName, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            if (variants.Count == 0)
                throw new InvalidOperationException(AppLocalization.IsRussian
                    ? $"Не найдена базовая земля для цвета {color}."
                    : $"No basic land was found for color {color}.");

            Shuffle(variants);
            landsByColor[color] = variants;
        }

        int totalDemand = selectedColors.Sum(color => demand[color]);
        Dictionary<char, int> landTargets = selectedColors.ToDictionary(
            color => color,
            color => (int)Math.Floor(landCount * demand[color] / (double)totalDemand));
        int assigned = landTargets.Values.Sum();
        foreach (char color in selectedColors
                     .OrderByDescending(color => landCount * demand[color] / (double)totalDemand - landTargets[color]))
        {
            if (assigned >= landCount)
                break;
            landTargets[color]++;
            assigned++;
        }

        foreach (char color in "WUBRG".Where(selectedColors.Contains))
        {
            List<CardRecord> variants = landsByColor[color];
            for (int index = 0; index < landTargets[color]; index++)
                _editor.Add(variants[index % variants.Count], DeckSection.MainDeck);
        }

        SetDirty(true);
        RefreshCollections();
        UpdateDeckAssistantDashboard();

        string mana = string.Join("/", "WUBRG".Where(selectedColors.Contains));
        Status(AppLocalization.IsRussian
            ? $"Создана случайная колода: 60 карт, цвета {mana}, 36 не-земель + 24 базовые земли."
            : $"Random deck created: 60 cards, {mana}, 36 nonlands + 24 basic lands.");
    }

    private static void AddRandomDistinct(
        IEnumerable<CardRecord> source,
        ICollection<CardRecord> target,
        ISet<string> chosenNames,
        int targetCount)
    {
        List<CardRecord> candidates = source
            .Where(card => !chosenNames.Contains(CardIdentity(card)))
            .ToList();
        Shuffle(candidates);
        foreach (CardRecord card in candidates)
        {
            if (target.Count >= targetCount)
                break;
            target.Add(card);
            chosenNames.Add(CardIdentity(card));
        }
    }

    private static T PickRandom<T>(IReadOnlyList<T> items)
    {
        if (items.Count == 0)
            throw new InvalidOperationException("Cannot choose from an empty collection.");
        return items[Random.Shared.Next(items.Count)];
    }

    private static void Shuffle<T>(IList<T> items)
    {
        for (int i = items.Count - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }
}

internal sealed class RandomDeckColorDialog : Window
{
    private readonly Dictionary<char, CheckBox> _colorBoxes = new();
    private readonly CheckBox _colorlessBox;

    public HashSet<char> SelectedColors { get; } = new();
    public bool IncludeColorless => _colorlessBox.IsChecked == true;

    public RandomDeckColorDialog()
    {
        bool ru = AppLocalization.IsRussian;
        Title = ru ? "Случайная колода" : "Random deck";
        Width = 430;
        Height = 420;
        MinWidth = 390;
        MinHeight = 390;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        StackPanel root = new() { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock
        {
            Text = ru
                ? "Выберите цвета маны. Генератор создаст новую колоду из 60 карт: 36 случайных не-земель и 24 базовые земли."
                : "Choose mana colors. The generator will create a new 60-card deck with 36 random nonlands and 24 basic lands.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });

        foreach ((char code, string en, string ruName) in new[]
                 {
                     ('W', "White", "Белый"),
                     ('U', "Blue", "Синий"),
                     ('B', "Black", "Чёрный"),
                     ('R', "Red", "Красный"),
                     ('G', "Green", "Зелёный")
                 })
        {
            CheckBox box = new()
            {
                Content = $"{(ru ? ruName : en)} ({code})",
                Margin = new Thickness(4, 4, 4, 4),
                FontSize = 15
            };
            _colorBoxes[code] = box;
            root.Children.Add(box);
        }

        _colorlessBox = new CheckBox
        {
            Content = ru ? "Добавлять бесцветные карты / артефакты" : "Include colorless cards / artifacts",
            IsChecked = true,
            Margin = new Thickness(4, 12, 4, 4)
        };
        root.Children.Add(_colorlessBox);

        TextBlock hint = new()
        {
            Text = ru
                ? "Многоцветные карты попадут в колоду только если все их цвета входят в выбранные."
                : "Multicolor cards are eligible only when all of their colors are selected.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            Margin = new Thickness(0, 10, 0, 14)
        };
        root.Children.Add(hint);

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        Button randomColors = new()
        {
            Content = ru ? "Случайные цвета" : "Random colors",
            MinWidth = 115,
            Margin = new Thickness(0, 0, 8, 0)
        };
        randomColors.Click += (_, _) => SelectRandomColors();
        Button cancel = new()
        {
            Content = ru ? "Отмена" : "Cancel",
            MinWidth = 80,
            Margin = new Thickness(0, 0, 8, 0)
        };
        cancel.Click += (_, _) => DialogResult = false;
        Button create = new()
        {
            Content = ru ? "Создать" : "Create",
            MinWidth = 90,
            IsDefault = true
        };
        create.Click += (_, _) => Accept();
        buttons.Children.Add(randomColors);
        buttons.Children.Add(cancel);
        buttons.Children.Add(create);
        root.Children.Add(buttons);

        Content = root;
        _colorBoxes['W'].IsChecked = true;
    }

    private void SelectRandomColors()
    {
        foreach (CheckBox box in _colorBoxes.Values)
            box.IsChecked = false;

        int count = Random.Shared.Next(1, 4);
        List<char> colors = _colorBoxes.Keys.ToList();
        for (int i = colors.Count - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (colors[i], colors[j]) = (colors[j], colors[i]);
        }
        foreach (char color in colors.Take(count))
            _colorBoxes[color].IsChecked = true;
    }

    private void Accept()
    {
        SelectedColors.Clear();
        foreach ((char color, CheckBox box) in _colorBoxes)
        {
            if (box.IsChecked == true)
                SelectedColors.Add(color);
        }

        if (SelectedColors.Count == 0)
        {
            MessageBox.Show(
                this,
                AppLocalization.IsRussian ? "Выберите хотя бы один цвет маны." : "Choose at least one mana color.",
                AppLocalization.IsRussian ? "Цвета маны" : "Mana colors",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }
}
