using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeckBuilder.Core.Models;

namespace DeckBuilder.Modern;

public partial class MainWindow
{
    private bool _safeGridDragHandlersInstalled;

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        InstallSafeGridDragHandlers();
        InstallEnhancedWorkspaceUi();
        await AutoLoadRememberedWorkspaceAsync();
    }

    private void InstallSafeGridDragHandlers()
    {
        if (_safeGridDragHandlersInstalled)
        {
            return;
        }

        _safeGridDragHandlersInstalled = true;
        foreach (DataGrid grid in new[]
                 {
                     AvailableCardsGrid,
                     MainDeckGrid,
                     RegularUnlocksGrid,
                     PromoUnlocksGrid
                 })
        {
            // The original handler was attached at DataGrid level. PreviewMouseMove also fires
            // when the user drags the vertical/horizontal ScrollBar thumb, so that handler could
            // accidentally start a card DragDrop operation and turn the cursor into the WPF
            // "not allowed" symbol. Replace it with a row-only handler.
            grid.PreviewMouseMove -= Grid_PreviewMouseMove;
            grid.PreviewMouseMove += SafeGrid_PreviewMouseMove;
        }

        foreach (DataGrid grid in new[]
                 {
                     MainDeckGrid,
                     RegularUnlocksGrid,
                     PromoUnlocksGrid
                 })
        {
            // Catalog cards start a COPY operation, while cards already in a deck start a MOVE.
            // The old DragOver handler always advertised MOVE, which has no overlap with a
            // catalog drag whose allowed effect is COPY. WPF therefore rejected the drop.
            grid.DragOver -= SectionGrid_DragOver;
            grid.DragOver += SafeSectionGrid_DragOver;
        }
    }

    private void SafeGrid_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || sender is not DataGrid grid)
        {
            return;
        }

        // A drag may start only from an actual data row. ScrollBars, column headers, empty grid
        // space and resize handles have no DataGridRow ancestor and must retain normal WPF input.
        DependencyObject? original = e.OriginalSource as DependencyObject;
        DataGridRow? row = FindVisualAncestor<DataGridRow>(original);
        if (row is null || !ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(row), grid))
        {
            return;
        }

        Point current = e.GetPosition(null);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        DragPayload? payload = grid == AvailableCardsGrid
            ? new DragPayload(grid.SelectedItems.Cast<CardRecord>().ToArray(), null, null)
            : grid.Tag is string sectionName
                ? new DragPayload(null, grid.SelectedItems.Cast<DeckEntry>().ToArray(), Enum.Parse<DeckSection>(sectionName))
                : null;
        if (payload is null || payload.Count == 0)
        {
            return;
        }

        DragDrop.DoDragDrop(grid, payload, payload.Source is null ? DragDropEffects.Copy : DragDropEffects.Move);
    }

    private void SafeSectionGrid_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(DragPayload)) is not DragPayload payload)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = payload.Cards is not null
            ? DragDropEffects.Copy
            : payload.Entries is not null
                ? DragDropEffects.Move
                : DragDropEffects.None;
        e.Handled = true;
    }

    private static T? FindVisualAncestor<T>(DependencyObject? value) where T : DependencyObject
    {
        DependencyObject? current = value;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
