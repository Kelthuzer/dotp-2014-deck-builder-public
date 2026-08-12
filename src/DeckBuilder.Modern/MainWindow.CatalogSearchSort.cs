using System.Windows;
using System.Windows.Controls;
using DeckBuilder.Core.Models;

namespace DeckBuilder.Modern;

public partial class MainWindow
{
    private ComboBox? _catalogSearchModeComboBox;
    private ComboBox? _catalogSortComboBox;
    private Button? _catalogSortDirectionButton;
    private bool _catalogSortDescending;

    internal void InstallCatalogSearchAndSort()
    {
        if (SearchBox.Parent is Grid searchGrid)
            InstallCatalogSearchAndSort(searchGrid);
    }

    private void InstallCatalogSearchAndSort(Grid searchGrid)
    {
        if (_catalogSearchModeComboBox is not null)
            return;

        SearchBox.Margin = new Thickness(150, 0, 265, 8);
        SearchBox.TextChanged += CatalogSearchText_Changed;

        _catalogSearchModeComboBox = new ComboBox
        {
            Width = 140,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, 0, 8, 8),
            ToolTip = "Choose what the search box searches"
        };
        _catalogSearchModeComboBox.Items.Add(new ComboBoxItem { Content = "Card name", Tag = "name" });
        _catalogSearchModeComboBox.Items.Add(new ComboBoxItem { Content = "Type / tag", Tag = "type-tag" });
        _catalogSearchModeComboBox.SelectedIndex = 0;
        _catalogSearchModeComboBox.SelectionChanged += CatalogSearchMode_Changed;
        Grid.SetRow(_catalogSearchModeComboBox, 1);
        searchGrid.Children.Add(_catalogSearchModeComboBox);

        StackPanel sortPanel = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(8, 0, 0, 8)
        };

        TextBlock sortLabel = new()
        {
            Text = "Sort:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        sortPanel.Children.Add(sortLabel);

        _catalogSortComboBox = new ComboBox
        {
            Width = 150,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _catalogSortComboBox.Items.Add(new ComboBoxItem { Content = "Default", Tag = "default" });
        _catalogSortComboBox.Items.Add(new ComboBoxItem { Content = "Mana value", Tag = "mana-value" });
        _catalogSortComboBox.Items.Add(new ComboBoxItem { Content = "Rarity", Tag = "rarity" });
        _catalogSortComboBox.Items.Add(new ComboBoxItem { Content = "Type", Tag = "type" });
        _catalogSortComboBox.Items.Add(new ComboBoxItem { Content = "Mana cost", Tag = "mana-cost" });
        _catalogSortComboBox.SelectedIndex = 0;
        _catalogSortComboBox.SelectionChanged += CatalogSort_Changed;
        sortPanel.Children.Add(_catalogSortComboBox);

        _catalogSortDirectionButton = new Button
        {
            Content = "↑",
            Width = 32,
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "Ascending / descending"
        };
        _catalogSortDirectionButton.Click += CatalogSortDirection_Click;
        sortPanel.Children.Add(_catalogSortDirectionButton);

        Grid.SetRow(sortPanel, 1);
        searchGrid.Children.Add(sortPanel);
        AppLocalization.Apply(this);
        RefreshCatalogSearchResults();
    }

    private void RefreshCatalogSearchResults()
    {
        IReadOnlyList<CardRecord> result = SearchCatalogBySelectedField(SearchBox.Text);
        AvailableCards.Clear();
        foreach (CardRecord card in result)
            AvailableCards.Add(card);

        RefreshCatalogFilters();
        UpdateCounts();
    }

    private IReadOnlyList<CardRecord> SearchCatalogBySelectedField(string query)
    {
        string text = query?.Trim() ?? string.Empty;
        string mode = SelectedComboTag(_catalogSearchModeComboBox, "name");

        IEnumerable<CardRecord> cards;
        if (string.IsNullOrWhiteSpace(text))
        {
            cards = _catalog;
        }
        else if (mode == "type-tag")
        {
            // Search the complete TypeLine. Every type/supertype/subtype is a valid tag:
            // Creature, Artifact, Legendary, Elf, Goblin, Zombie, Knight, Instant, Land, etc.
            cards = _catalog.Where(card =>
                card.TypeLine.Contains(text, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            cards = _catalog.Where(card =>
                card.LocalizedName.Contains(text, StringComparison.OrdinalIgnoreCase)
                || card.EnglishName.Contains(text, StringComparison.OrdinalIgnoreCase)
                || card.FileName.Contains(text, StringComparison.OrdinalIgnoreCase));
        }

        return SortCatalogResults(cards).ToArray();
    }

    private IEnumerable<CardRecord> SortCatalogResults(IEnumerable<CardRecord> cards)
    {
        string sort = SelectedComboTag(_catalogSortComboBox, "default");
        Func<CardRecord, object> keySelector = sort switch
        {
            "mana-value" => card => EstimateManaValue(card.CastingCost),
            "rarity" => card => RarityRank(card.Rarity),
            "type" => card => card.TypeLine,
            "mana-cost" => card => card.CastingCost,
            _ => card => card.LocalizedName
        };

        return _catalogSortDescending
            ? cards.OrderByDescending(keySelector).ThenBy(card => card.LocalizedName, StringComparer.CurrentCultureIgnoreCase)
            : cards.OrderBy(keySelector).ThenBy(card => card.LocalizedName, StringComparer.CurrentCultureIgnoreCase);
    }

    private static int RarityRank(string rarity)
    {
        string value = rarity?.Trim().ToUpperInvariant() ?? string.Empty;
        if (value.Contains("MYTHIC") || value is "M") return 4;
        if (value.Contains("RARE") || value is "R") return 3;
        if (value.Contains("UNCOMMON") || value is "U") return 2;
        if (value.Contains("COMMON") || value is "C") return 1;
        return 0;
    }

    private static string SelectedComboTag(ComboBox? comboBox, string fallback) =>
        comboBox?.SelectedItem is ComboBoxItem item && item.Tag is string tag ? tag : fallback;

    private void CatalogSearchText_Changed(object sender, TextChangedEventArgs e) => RefreshCatalogSearchResults();

    private void CatalogSearchMode_Changed(object sender, SelectionChangedEventArgs e) => RefreshCatalogSearchResults();

    private void CatalogSort_Changed(object sender, SelectionChangedEventArgs e) => RefreshCatalogSearchResults();

    private void CatalogSortDirection_Click(object sender, RoutedEventArgs e)
    {
        _catalogSortDescending = !_catalogSortDescending;
        if (_catalogSortDirectionButton is not null)
            _catalogSortDirectionButton.Content = _catalogSortDescending ? "↓" : "↑";
        RefreshCatalogSearchResults();
    }
}
