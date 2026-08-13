using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DeckBuilder.Core.Models;

namespace DeckBuilder.Modern;

public partial class MainWindow
{
    private bool _catalogCostClickFixInstalled;

    internal void InstallCatalogCostClickFix()
    {
        if (_catalogCostClickFixInstalled)
            return;

        _catalogCostClickFixInstalled = true;

        // Cost is a template column. Make it explicitly sortable and identify it by
        // stable column position/property rather than localized Header text.
        if (AvailableCardsGrid.Columns.Count > 1)
        {
            DataGridColumn costColumn = AvailableCardsGrid.Columns[1];
            costColumn.CanUserSort = true;
            costColumn.SortMemberPath = nameof(CardRecord.CastingCost);
        }

        AvailableCardsGrid.AddHandler(
            ButtonBase.ClickEvent,
            new RoutedEventHandler(CatalogHeader_Click),
            handledEventsToo: true);

        // "Mana cost" and "Mana value" represented the same useful sort to the user.
        if (_catalogSortComboBox is not null)
        {
            ComboBoxItem? duplicate = _catalogSortComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, "mana-cost", StringComparison.Ordinal));
            if (duplicate is not null)
                _catalogSortComboBox.Items.Remove(duplicate);
        }
    }

    private void CatalogHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
            return;

        DataGridColumnHeader? header = FindVisualAncestor<DataGridColumnHeader>(source);
        if (header?.Column is null || AvailableCardsGrid.Columns.Count <= 1)
            return;

        DataGridColumn costColumn = AvailableCardsGrid.Columns[1];
        if (!ReferenceEquals(header.Column, costColumn))
            return;

        string currentSort = SelectedComboTag(_catalogSortComboBox, "default");
        _catalogSortDescending = currentSort == "mana-value" && !_catalogSortDescending;
        SelectCatalogSort("mana-value");

        foreach (DataGridColumn column in AvailableCardsGrid.Columns)
            column.SortDirection = null;
        costColumn.SortDirection = _catalogSortDescending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;

        if (_catalogSortDirectionButton is not null)
            _catalogSortDirectionButton.Content = _catalogSortDescending ? "↓" : "↑";

        RefreshCatalogSearchResults();
        e.Handled = true;
    }
}
