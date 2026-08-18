using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DeckBuilder.Core.Models;

namespace DeckBuilder.Modern;

public partial class MainWindow
{
    private bool _mainDeckColumnsConfigured;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        ConfigureMainDeckColumns();
    }

    private void ConfigureMainDeckColumns()
    {
        if (_mainDeckColumnsConfigured || MainDeckGrid.Columns.Count < 4)
            return;

        _mainDeckColumnsConfigured = true;
        MainDeckGrid.CanUserSortColumns = true;

        // Existing XAML order: Qty, Card, Cost, Reference. Give every displayed value an explicit
        // top-level sort key so WPF never has to sort through a nested Card.* property path.
        MainDeckGrid.Columns[0].SortMemberPath = nameof(DeckEntry.Quantity);
        MainDeckGrid.Columns[1].SortMemberPath = nameof(DeckEntry.CardName);
        MainDeckGrid.Columns[2].SortMemberPath = nameof(DeckEntry.CardManaValue);
        MainDeckGrid.Columns[3].SortMemberPath = nameof(DeckEntry.CardReference);

        MainDeckGrid.Columns[1].Width = new DataGridLength(1.2, DataGridLengthUnitType.Star);
        MainDeckGrid.Columns[3].Width = new DataGridLength(1.0, DataGridLengthUnitType.Star);

        DataGridTextColumn typeColumn = new()
        {
            Header = AppLocalization.Text("Type"),
            Binding = new Binding(nameof(DeckEntry.CardTypeLine)),
            SortMemberPath = nameof(DeckEntry.CardTypeLine),
            IsReadOnly = true,
            Width = new DataGridLength(1.0, DataGridLengthUnitType.Star)
        };

        DataGridTextColumn rarityColumn = new()
        {
            Header = AppLocalization.Text("Rarity"),
            Binding = new Binding(nameof(DeckEntry.CardRarity)),
            SortMemberPath = nameof(DeckEntry.CardRarityOrder),
            IsReadOnly = true,
            Width = new DataGridLength(72)
        };

        // Keep Reference at the right edge: Qty | Card | Cost | Type | Rarity | Reference.
        MainDeckGrid.Columns.Insert(3, typeColumn);
        MainDeckGrid.Columns.Insert(4, rarityColumn);
    }
}
