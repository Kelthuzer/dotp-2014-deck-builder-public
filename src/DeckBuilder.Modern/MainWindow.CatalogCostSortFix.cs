using System.Windows.Controls;
using DeckBuilder.Core.Models;

namespace DeckBuilder.Modern;

public partial class MainWindow
{
    private bool _catalogCostSortFixInstalled;

    internal void InstallCatalogCostSortFix()
    {
        if (_catalogCostSortFixInstalled)
            return;

        DataGridColumn? costColumn = AvailableCardsGrid.Columns
            .FirstOrDefault(column => string.Equals(
                column.Header?.ToString(),
                "Cost",
                StringComparison.OrdinalIgnoreCase));

        if (costColumn is not null)
        {
            // DataGridTemplateColumn is non-sortable until SortMemberPath is supplied.
            // The existing Sorting handler intercepts this column and sorts by numeric mana value.
            costColumn.CanUserSort = true;
            costColumn.SortMemberPath = nameof(CardRecord.CastingCost);
        }

        if (_catalogSortComboBox is not null)
        {
            ComboBoxItem? duplicateManaCost = _catalogSortComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag as string,
                    "mana-cost",
                    StringComparison.Ordinal));

            if (duplicateManaCost is not null)
                _catalogSortComboBox.Items.Remove(duplicateManaCost);
        }

        _catalogCostSortFixInstalled = true;
    }
}
