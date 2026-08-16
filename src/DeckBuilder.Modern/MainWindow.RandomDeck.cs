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

        RandomDeckColorDialog dialog = new(_catalog) { Owner = this };
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
            GenerateRandomDeck(
                dialog.SelectedColors,
                dialog.IncludeColorless,
                dialog.SelectedCreatureType,
                dialog.ArtifactsOnly,
                dialog.SelectedRarities);
        }
        catch (Exception exception)
        {
            ShowError(ru ? "Не удалось создать случайную колоду" : "Could not create the random deck", exception);
        }
    }

    private void GenerateRandomDeck(
        IReadOnlySet<char> selectedColors,
        bool includeColorless,
        string? selectedCreatureType,
        bool artifactsOnly,
        IReadOnlySet<string> selectedRarities)
    {
        if (selectedColors.Count == 0 && !includeColorless)
            throw new InvalidOperationException(AppLocalization.IsRussian
                ? "Выберите хотя бы один цвет маны или разрешите бесцветные карты."
                : "Choose at least one mana color or allow colorless cards.");

        if (selectedRarities.Count == 0)
            throw new InvalidOperationException(AppLocalization.IsRussian
                ? "Выберите хотя бы одну редкость карт."
                : "Choose at least one card rarity.");

        const int spellCount = 36;
        const int landCount = 24;

        List<CardRecord> eligible = _catalog
            .Where(card => !card.IsToken && !card.IsMissingDefinition && !IsLand(card))
            .Where(card => !artifactsOnly || IsArtifact(card))
            .Where(card => MatchesCreatureTypeFilter(card, selectedCreatureType))
            .Where(card => MatchesRarityFilter(card, selectedRarities))
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

        if (!artifactsOnly && colored.Count < selectedColors.Count)
            throw new InvalidOperationException(AppLocalization.IsRussian
                ? "В загруженном каталоге недостаточно карт выбранных цветов с текущими фильтрами."
                : "The loaded catalog does not contain enough cards in the selected colors with the current filters.");

        List<CardRecord> chosenSpells = new();
        HashSet<string> chosenNames = new(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(selectedCreatureType))
        {
            List<CardRecord> tribalCandidates = eligible
                .Where(card => IsCreature(card) && HasCreatureSubtype(card, selectedCreatureType))
                .ToList();
            if (tribalCandidates.Count == 0)
                throw new InvalidOperationException(AppLocalization.IsRussian
                    ? $"Не найдено существ типа «{selectedCreatureType}» с текущими фильтрами."
                    : $"No {selectedCreatureType} creatures matched the current filters.");

            CardRecord tribalCard = PickRandom(tribalCandidates);
            chosenSpells.Add(tribalCard);
            chosenNames.Add(CardIdentity(tribalCard));
        }

        // Guarantee that every requested color actually appears in the generated spell suite.
        // Artifact-only and fully colorless modes may legitimately have no selected mana colors.
        if (!artifactsOnly)
        {
            foreach (char color in selectedColors.OrderBy(color => "WUBRG".IndexOf(color)))
            {
                List<CardRecord> candidates = colored
                    .Where(card => ExtractSpellColors(card).Contains(color))
                    .Where(card => !chosenNames.Contains(CardIdentity(card)))
                    .ToList();
                if (candidates.Count == 0)
                    throw new InvalidOperationException(AppLocalization.IsRussian
                        ? $"Не удалось найти отдельную карту для цвета {color} с текущими фильтрами."
                        : $"Could not find a distinct spell for color {color} with the current filters.");

                CardRecord card = PickRandom(candidates);
                chosenSpells.Add(card);
                chosenNames.Add(CardIdentity(card));
            }
        }

        int desiredColorless = includeColorless ? Math.Min(6, Math.Max(0, spellCount - chosenSpells.Count)) : 0;
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
                ? $"С текущими фильтрами найдено только {chosenSpells.Count} уникальных не-земель. Нужно минимум {spellCount}."
                : $"Only {chosenSpells.Count} unique nonland cards matched the current filters; at least {spellCount} are required.");

        _deck = new DeckDocument();
        _editor = new DeckEditor(_deck);
        _projectPath = null;

        string colorCode = string.Concat("WUBRG".Where(selectedColors.Contains));
        if (colorCode.Length == 0)
            colorCode = AppLocalization.IsRussian ? "бесцветная" : "colorless";
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

        if (selectedColors.Count == 0)
        {
            List<CardRecord> basicLands = _catalog
                .Where(IsBasicLand)
                .Where(card => BasicLandColors(card).Count == 0)
                .GroupBy(card => card.FileName, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            // DotP 2014 normally has no Wastes. In a colorless deck any normal basic land can
            // still pay generic artifact costs, so fall back to the available five basics.
            if (basicLands.Count == 0)
            {
                basicLands = _catalog
                    .Where(IsBasicLand)
                    .GroupBy(card => card.FileName, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();
            }

            if (basicLands.Count == 0)
                throw new InvalidOperationException(AppLocalization.IsRussian
                    ? "В загруженном каталоге не найдены базовые земли для бесцветной колоды."
                    : "No basic lands were found for the colorless deck.");

            Shuffle(basicLands);
            for (int index = 0; index < landCount; index++)
                _editor.Add(basicLands[index % basicLands.Count], DeckSection.MainDeck);
        }
        else
        {
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
        }

        SetDirty(true);
        RefreshCollections();
        UpdateDeckAssistantDashboard();

        string mana = selectedColors.Count == 0
            ? (AppLocalization.IsRussian ? "бесцветная" : "colorless")
            : string.Join("/", "WUBRG".Where(selectedColors.Contains));
        List<string> activeFilters = new();
        if (!string.IsNullOrWhiteSpace(selectedCreatureType))
            activeFilters.Add(AppLocalization.IsRussian ? $"тип {selectedCreatureType}" : $"type {selectedCreatureType}");
        if (artifactsOnly)
            activeFilters.Add(AppLocalization.IsRussian ? "только артефакты" : "artifacts only");
        if (selectedRarities.Count < 4)
            activeFilters.Add((AppLocalization.IsRussian ? "редкость " : "rarity ") + string.Join('/', selectedRarities.OrderBy(value => value)));
        string suffix = activeFilters.Count == 0 ? string.Empty : $" · {string.Join(", ", activeFilters)}";

        Status(AppLocalization.IsRussian
            ? $"Создана случайная колода: 60 карт, цвета {mana}, 36 не-земель + 24 базовые земли{suffix}."
            : $"Random deck created: 60 cards, {mana}, 36 nonlands + 24 basic lands{suffix}.");
    }

    private static bool MatchesCreatureTypeFilter(CardRecord card, string? selectedCreatureType)
    {
        if (string.IsNullOrWhiteSpace(selectedCreatureType) || !IsCreature(card))
            return true;

        return HasCreatureSubtype(card, selectedCreatureType);
    }

    private static bool HasCreatureSubtype(CardRecord card, string subtype)
    {
        return CreatureSubtypes(card)
            .Any(value => value.Equals(subtype.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> CreatureSubtypes(CardRecord card)
    {
        string[] tokens = card.TypeLine
            .Replace('—', ' ')
            .Replace('-', ' ')
            .Split([' ', '\t', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int creatureIndex = Array.FindIndex(tokens,
            value => value.Equals("CREATURE", StringComparison.OrdinalIgnoreCase));
        if (creatureIndex < 0 || creatureIndex == tokens.Length - 1)
            return Array.Empty<string>();

        return tokens[(creatureIndex + 1)..]
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsCreature(CardRecord card) =>
        card.TypeLine.Contains("CREATURE", StringComparison.OrdinalIgnoreCase);

    private static bool IsArtifact(CardRecord card) =>
        card.TypeLine.Contains("ARTIFACT", StringComparison.OrdinalIgnoreCase);

    private static bool MatchesRarityFilter(CardRecord card, IReadOnlySet<string> selectedRarities)
    {
        // With all standard rarities enabled preserve the old generator behavior, including
        // cards whose modded definitions use an unknown/non-standard rarity code.
        if (selectedRarities.Count >= 4)
            return true;

        string rarity = NormalizeRarity(card.Rarity);
        return rarity.Length > 0 && selectedRarities.Contains(rarity);
    }

    private static string NormalizeRarity(string? rarity) => rarity?.Trim().ToUpperInvariant() switch
    {
        "C" or "COMMON" => "C",
        "U" or "UNCOMMON" => "U",
        "R" or "RARE" => "R",
        "M" or "MYTHIC" or "MYTHIC RARE" => "M",
        _ => string.Empty
    };

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
    private readonly Dictionary<string, CheckBox> _rarityBoxes = new(StringComparer.OrdinalIgnoreCase);
    private readonly CheckBox _colorlessBox;
    private readonly CheckBox _artifactsOnlyBox;
    private readonly ComboBox _creatureTypeBox;
    private readonly string _anyCreatureTypeLabel;

    public HashSet<char> SelectedColors { get; } = new();
    public HashSet<string> SelectedRarities { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IncludeColorless => _colorlessBox.IsChecked == true;
    public bool ArtifactsOnly => _artifactsOnlyBox.IsChecked == true;
    public string? SelectedCreatureType { get; private set; }

    public RandomDeckColorDialog(IReadOnlyList<CardRecord> catalog)
    {
        bool ru = AppLocalization.IsRussian;
        Title = ru ? "Случайная колода" : "Random deck";
        Width = 500;
        Height = 660;
        MinWidth = 450;
        MinHeight = 560;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        ScrollViewer scroll = new()
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        StackPanel root = new() { Margin = new Thickness(20) };
        scroll.Content = root;
        Content = scroll;

        root.Children.Add(new TextBlock
        {
            Text = ru
                ? "Выберите цвета и дополнительные фильтры. Генератор создаст новую колоду из 60 карт: 36 случайных не-земель и 24 базовые земли."
                : "Choose mana colors and optional filters. The generator will create a new 60-card deck with 36 random nonlands and 24 basic lands.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });

        root.Children.Add(SectionTitle(ru ? "Цвета маны" : "Mana colors"));
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
                Margin = new Thickness(4, 3, 4, 3),
                FontSize = 15
            };
            _colorBoxes[code] = box;
            root.Children.Add(box);
        }

        _colorlessBox = new CheckBox
        {
            Content = ru ? "Добавлять бесцветные карты" : "Include colorless cards",
            IsChecked = true,
            Margin = new Thickness(4, 8, 4, 4)
        };
        root.Children.Add(_colorlessBox);

        root.Children.Add(SectionTitle(ru ? "Тип существ" : "Creature type"));
        _anyCreatureTypeLabel = ru ? "Любой тип" : "Any type";
        List<string> creatureTypes = catalog
            .Where(card => !card.IsToken && !card.IsMissingDefinition && IsCreatureCard(card))
            .SelectMany(ExtractCreatureSubtypes)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        creatureTypes.Insert(0, _anyCreatureTypeLabel);

        _creatureTypeBox = new ComboBox
        {
            ItemsSource = creatureTypes,
            SelectedIndex = 0,
            IsEditable = true,
            IsTextSearchEnabled = true,
            Margin = new Thickness(4, 4, 4, 4),
            MinWidth = 220,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        root.Children.Add(_creatureTypeBox);
        root.Children.Add(new TextBlock
        {
            Text = ru
                ? "Если выбран тип, существа других типов исключаются; заклинания, не являющиеся существами, остаются доступными. Например: Elf, Goblin, Sliver."
                : "When a type is selected, creatures of other types are excluded; noncreature spells remain eligible. Examples: Elf, Goblin, Sliver.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72,
            Margin = new Thickness(4, 2, 4, 8)
        });

        root.Children.Add(SectionTitle(ru ? "Тип карт" : "Card type"));
        _artifactsOnlyBox = new CheckBox
        {
            Content = ru ? "Только артефакты" : "Artifacts only",
            Margin = new Thickness(4, 4, 4, 8)
        };
        root.Children.Add(_artifactsOnlyBox);

        root.Children.Add(SectionTitle(ru ? "Редкость" : "Rarity"));
        WrapPanel rarityPanel = new() { Margin = new Thickness(0, 2, 0, 8) };
        foreach ((string code, string en, string ruName) in new[]
                 {
                     ("C", "Common", "Обычная"),
                     ("U", "Uncommon", "Необычная"),
                     ("R", "Rare", "Редкая"),
                     ("M", "Mythic", "Мифическая")
                 })
        {
            CheckBox box = new()
            {
                Content = $"{(ru ? ruName : en)} ({code})",
                IsChecked = true,
                Margin = new Thickness(4, 3, 12, 3)
            };
            _rarityBoxes[code] = box;
            rarityPanel.Children.Add(box);
        }
        root.Children.Add(rarityPanel);

        TextBlock hint = new()
        {
            Text = ru
                ? "Фильтры объединяются: например, «Elf» + «Только артефакты» оставит только артефактных существ-эльфов среди существ, а остальные не-существа тоже должны быть артефактами."
                : "Filters are combined. For example, Elf + Artifacts only permits only artifact Elf creatures among creatures, while every noncreature spell must also be an artifact.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            Margin = new Thickness(0, 6, 0, 14)
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

        _colorBoxes['W'].IsChecked = true;
    }

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        FontSize = 15,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 10, 0, 4)
    };

    private static bool IsCreatureCard(CardRecord card) =>
        card.TypeLine.Contains("CREATURE", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> ExtractCreatureSubtypes(CardRecord card)
    {
        string[] tokens = card.TypeLine
            .Replace('—', ' ')
            .Replace('-', ' ')
            .Split([' ', '\t', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int creatureIndex = Array.FindIndex(tokens,
            value => value.Equals("CREATURE", StringComparison.OrdinalIgnoreCase));
        if (creatureIndex < 0)
            yield break;

        for (int index = creatureIndex + 1; index < tokens.Length; index++)
        {
            if (!string.IsNullOrWhiteSpace(tokens[index]))
                yield return tokens[index];
        }
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

        if (SelectedColors.Count == 0 && _colorlessBox.IsChecked != true)
        {
            MessageBox.Show(
                this,
                AppLocalization.IsRussian
                    ? "Выберите хотя бы один цвет маны или включите бесцветные карты."
                    : "Choose at least one mana color or enable colorless cards.",
                AppLocalization.IsRussian ? "Цвета маны" : "Mana colors",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        SelectedRarities.Clear();
        foreach ((string rarity, CheckBox box) in _rarityBoxes)
        {
            if (box.IsChecked == true)
                SelectedRarities.Add(rarity);
        }

        if (SelectedRarities.Count == 0)
        {
            MessageBox.Show(
                this,
                AppLocalization.IsRussian ? "Выберите хотя бы одну редкость карт." : "Choose at least one card rarity.",
                AppLocalization.IsRussian ? "Редкость" : "Rarity",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        string creatureType = _creatureTypeBox.Text.Trim();
        SelectedCreatureType = creatureType.Length == 0
            || creatureType.Equals(_anyCreatureTypeLabel, StringComparison.OrdinalIgnoreCase)
            ? null
            : creatureType;

        DialogResult = true;
    }
}