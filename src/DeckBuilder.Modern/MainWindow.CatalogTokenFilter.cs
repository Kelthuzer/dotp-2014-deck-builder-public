using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using DeckBuilder.Core.Models;
using DeckBuilder.GameData;

namespace DeckBuilder.Modern;

public partial class MainWindow
{
    private bool _catalogTokenFilterInstalled;
    private CheckBox? _hideTokensCheckBox;
    private CheckBox? _hideLandsCheckBox;
    private CheckBox? _whiteManaFilterCheckBox;
    private CheckBox? _blueManaFilterCheckBox;
    private CheckBox? _blackManaFilterCheckBox;
    private CheckBox? _redManaFilterCheckBox;
    private CheckBox? _greenManaFilterCheckBox;
    private CheckBox? _colorlessManaFilterCheckBox;
    private CheckBox? _unitsFilterCheckBox;
    private CheckBox? _spellsFilterCheckBox;
    private CheckBox? _instantFilterCheckBox;
    private CheckBox? _sorceryFilterCheckBox;
    private CheckBox? _enchantmentsFilterCheckBox;
    private CheckBox? _artifactsFilterCheckBox;
    private ICollectionView? _availableCardsView;

    internal void InstallCatalogTokenFilter()
    {
        if (_catalogTokenFilterInstalled || SearchBox.Parent is not Grid searchGrid)
            return;

        _catalogTokenFilterInstalled = true;
        _availableCardsView = CollectionViewSource.GetDefaultView(AvailableCards);
        _availableCardsView.Filter = FilterAvailableCard;

        if (AvailableCardsGrid.Columns.Count >= 5 && AvailableCardsGrid.Columns[^1] is DataGridTextColumn lastColumn)
        {
            lastColumn.Header = "Rarity";
            lastColumn.Binding = new Binding(nameof(CardRecord.Rarity));
            lastColumn.Width = new DataGridLength(82);
        }

        SearchBox.Margin = new Thickness(0, 0, 0, 8);

        const int manaRow = 2;
        const int typeRow = 3;
        searchGrid.RowDefinitions.Insert(manaRow, new RowDefinition { Height = GridLength.Auto });
        searchGrid.RowDefinitions.Insert(typeRow, new RowDefinition { Height = GridLength.Auto });
        foreach (UIElement child in searchGrid.Children.Cast<UIElement>().ToArray())
        {
            int row = Grid.GetRow(child);
            if (row >= manaRow)
                Grid.SetRow(child, row + 2);
        }

        DockPanel manaFilters = new()
        {
            Margin = new Thickness(0, 0, 0, 6),
            LastChildFill = true
        };

        Button resetButton = new()
        {
            Content = "Reset filters",
            Padding = new Thickness(10, 2, 10, 2),
            Margin = new Thickness(12, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            ToolTip = "Clear mana and card-type filters and restore default land/token hiding"
        };
        resetButton.Click += ResetCatalogFilters_Click;
        DockPanel.SetDock(resetButton, Dock.Right);
        manaFilters.Children.Add(resetButton);

        WrapPanel manaChoices = new() { VerticalAlignment = VerticalAlignment.Center };
        manaChoices.Children.Add(CreateFilterLabel("Mana:"));
        _whiteManaFilterCheckBox = CreateManaFilter("W", "White mana");
        _blueManaFilterCheckBox = CreateManaFilter("U", "Blue mana");
        _blackManaFilterCheckBox = CreateManaFilter("B", "Black mana");
        _redManaFilterCheckBox = CreateManaFilter("R", "Red mana");
        _greenManaFilterCheckBox = CreateManaFilter("G", "Green mana");
        _colorlessManaFilterCheckBox = CreateColorlessManaFilter();
        manaChoices.Children.Add(_whiteManaFilterCheckBox);
        manaChoices.Children.Add(_blueManaFilterCheckBox);
        manaChoices.Children.Add(_blackManaFilterCheckBox);
        manaChoices.Children.Add(_redManaFilterCheckBox);
        manaChoices.Children.Add(_greenManaFilterCheckBox);
        manaChoices.Children.Add(_colorlessManaFilterCheckBox);
        manaFilters.Children.Add(manaChoices);

        WrapPanel typeFilters = new()
        {
            Margin = new Thickness(0, 0, 0, 8),
            VerticalAlignment = VerticalAlignment.Center
        };
        typeFilters.Children.Add(CreateFilterLabel("Type:"));

        _unitsFilterCheckBox = CreateTypeFilter("Units");
        _spellsFilterCheckBox = CreateTypeFilter("Spells");
        _instantFilterCheckBox = CreateSubtypeFilter("Instants");
        _sorceryFilterCheckBox = CreateSubtypeFilter("Sorceries");
        _enchantmentsFilterCheckBox = CreateTypeFilter("Enchantments");
        _artifactsFilterCheckBox = CreateTypeFilter("Artifacts");
        _hideLandsCheckBox = CreateFilterCheckBox(
            "Hide lands",
            true,
            "Hide land cards from the main catalog",
            new Thickness(8, 0, 0, 0));
        _hideTokensCheckBox = CreateFilterCheckBox(
            "Hide tokens",
            true,
            "Hide TOKEN_* cards from the main catalog",
            new Thickness(12, 0, 0, 0));

        _spellsFilterCheckBox.Checked += SpellFilterState_Changed;
        _spellsFilterCheckBox.Unchecked += SpellFilterState_Changed;

        typeFilters.Children.Add(_unitsFilterCheckBox);
        typeFilters.Children.Add(_spellsFilterCheckBox);
        typeFilters.Children.Add(_instantFilterCheckBox);
        typeFilters.Children.Add(_sorceryFilterCheckBox);
        typeFilters.Children.Add(_enchantmentsFilterCheckBox);
        typeFilters.Children.Add(_artifactsFilterCheckBox);
        typeFilters.Children.Add(_hideLandsCheckBox);
        typeFilters.Children.Add(_hideTokensCheckBox);

        Grid.SetRow(manaFilters, manaRow);
        Grid.SetRow(typeFilters, typeRow);
        searchGrid.Children.Add(manaFilters);
        searchGrid.Children.Add(typeFilters);

        UpdateSpellSubtypeAvailability();
        AppLocalization.Apply(this);
        _availableCardsView.Refresh();
    }

    private static TextBlock CreateFilterLabel(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 10, 0)
    };

    private CheckBox CreateManaFilter(string symbol, string toolTip)
    {
        CheckBox checkBox = CreateFilterCheckBox(string.Empty, false, toolTip, new Thickness(0, 0, 12, 0));
        string? imageId = DotpSymbolMap.CostTokenImageId($"{{{symbol}}}");
        var source = EmbeddedManaSymbols.TryGet(imageId);
        if (source is not null)
        {
            Image image = new()
            {
                Source = source,
                Width = 20,
                Height = 20,
                Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true,
                ToolTip = toolTip
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            checkBox.Content = image;
        }
        else
        {
            checkBox.Content = symbol;
        }
        return checkBox;
    }

    private CheckBox CreateColorlessManaFilter()
    {
        CheckBox checkBox = CreateFilterCheckBox(string.Empty, false, "Colorless", new Thickness(0, 0, 14, 0));
        checkBox.Content = new TextBlock
        {
            Text = "◇",
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Colorless"
        };
        return checkBox;
    }

    private CheckBox CreateTypeFilter(string text) => CreateFilterCheckBox(
        text, false, "Select one or more card categories; selected categories are combined", new Thickness(0, 0, 16, 0));

    private CheckBox CreateSubtypeFilter(string text) => CreateFilterCheckBox(
        text, false, "Narrow the Spells category to this spell type", new Thickness(0, 0, 12, 0));

    private CheckBox CreateFilterCheckBox(string text, bool isChecked, string toolTip, Thickness margin)
    {
        CheckBox checkBox = new()
        {
            Content = text,
            IsChecked = isChecked,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = margin,
            ToolTip = toolTip
        };
        checkBox.Checked += CatalogFilterCheckBox_Changed;
        checkBox.Unchecked += CatalogFilterCheckBox_Changed;
        return checkBox;
    }

    private bool FilterAvailableCard(object item)
    {
        if (item is not CardRecord card)
            return true;

        if (_hideTokensCheckBox?.IsChecked != false && card.IsToken)
            return false;
        if (_hideLandsCheckBox?.IsChecked != false && IsLand(card))
            return false;
        if (!MatchesSelectedManaColors(card))
            return false;
        return MatchesSelectedCardTypes(card);
    }

    private bool MatchesSelectedManaColors(CardRecord card)
    {
        HashSet<char> allowedColors = new();
        if (_whiteManaFilterCheckBox?.IsChecked == true) allowedColors.Add('W');
        if (_blueManaFilterCheckBox?.IsChecked == true) allowedColors.Add('U');
        if (_blackManaFilterCheckBox?.IsChecked == true) allowedColors.Add('B');
        if (_redManaFilterCheckBox?.IsChecked == true) allowedColors.Add('R');
        if (_greenManaFilterCheckBox?.IsChecked == true) allowedColors.Add('G');
        bool allowColorless = _colorlessManaFilterCheckBox?.IsChecked == true;

        if (allowedColors.Count == 0 && !allowColorless)
            return true;

        HashSet<char> cardColors = IsLand(card) ? BasicLandColors(card) : ExtractSpellColors(card);
        if (cardColors.Count == 0)
            return allowColorless;
        return cardColors.All(allowedColors.Contains);
    }

    private bool MatchesSelectedCardTypes(CardRecord card)
    {
        bool units = _unitsFilterCheckBox?.IsChecked == true;
        bool spells = _spellsFilterCheckBox?.IsChecked == true;
        bool enchantments = _enchantmentsFilterCheckBox?.IsChecked == true;
        bool artifacts = _artifactsFilterCheckBox?.IsChecked == true;

        if (!units && !spells && !enchantments && !artifacts)
            return true;

        string type = card.TypeLine ?? string.Empty;
        bool matchesSpell = spells && MatchesSelectedSpellSubtype(type);

        return (units && ContainsType(type, "Creature", "Существо"))
            || matchesSpell
            || (enchantments && ContainsType(type, "Enchantment", "Чары", "Зачарован"))
            || (artifacts && ContainsType(type, "Artifact", "Артефакт"));
    }

    private bool MatchesSelectedSpellSubtype(string typeLine)
    {
        bool instant = _instantFilterCheckBox?.IsChecked == true;
        bool sorcery = _sorceryFilterCheckBox?.IsChecked == true;
        if (!instant && !sorcery)
            return ContainsType(typeLine, "Instant", "Sorcery", "Мгнов", "Волшебств");
        return (instant && ContainsType(typeLine, "Instant", "Мгнов"))
            || (sorcery && ContainsType(typeLine, "Sorcery", "Волшебств"));
    }

    private static bool ContainsType(string typeLine, params string[] markers) =>
        markers.Any(marker => typeLine.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private void CatalogFilterCheckBox_Changed(object sender, RoutedEventArgs e) => RefreshCatalogFilters();

    private void SpellFilterState_Changed(object sender, RoutedEventArgs e)
    {
        UpdateSpellSubtypeAvailability();
        RefreshCatalogFilters();
    }

    private void UpdateSpellSubtypeAvailability()
    {
        bool enabled = _spellsFilterCheckBox?.IsChecked == true;
        if (_instantFilterCheckBox is not null) _instantFilterCheckBox.IsEnabled = enabled;
        if (_sorceryFilterCheckBox is not null) _sorceryFilterCheckBox.IsEnabled = enabled;
    }

    private void ResetCatalogFilters_Click(object sender, RoutedEventArgs e)
    {
        foreach (CheckBox? filter in new CheckBox?[]
                 {
                     _whiteManaFilterCheckBox, _blueManaFilterCheckBox, _blackManaFilterCheckBox,
                     _redManaFilterCheckBox, _greenManaFilterCheckBox, _colorlessManaFilterCheckBox,
                     _unitsFilterCheckBox, _spellsFilterCheckBox, _instantFilterCheckBox,
                     _sorceryFilterCheckBox, _enchantmentsFilterCheckBox, _artifactsFilterCheckBox
                 })
        {
            if (filter is not null) filter.IsChecked = false;
        }
        if (_hideLandsCheckBox is not null) _hideLandsCheckBox.IsChecked = true;
        if (_hideTokensCheckBox is not null) _hideTokensCheckBox.IsChecked = true;
        UpdateSpellSubtypeAvailability();
        RefreshCatalogFilters();
    }

    internal void RefreshCatalogFilters() => _availableCardsView?.Refresh();
}
