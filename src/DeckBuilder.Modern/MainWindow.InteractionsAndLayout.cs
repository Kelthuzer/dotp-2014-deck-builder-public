using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DeckBuilder.Core.Models;

namespace DeckBuilder.Modern;

public partial class MainWindow
{
    private bool _interactionLayoutInstalled;
    private bool _previewInteractionsConfigured;
    private bool _fittingNeighborPreviews;
    private Grid? _mainLayoutGrid;
    private Grid? _rightLayoutGrid;
    private readonly DispatcherTimer _previewWheelThrottleTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(190)
    };

    internal void InstallInteractionsAndLayoutPersistence()
    {
        if (_interactionLayoutInstalled)
            return;

        _interactionLayoutInstalled = true;
        _previewWheelThrottleTimer.Tick += (_, _) => _previewWheelThrottleTimer.Stop();
        WindowState = AppSettingsService.Current.StartMaximized
            ? WindowState.Maximized
            : WindowState.Normal;

        _mainLayoutGrid = FindAncestorGrid(
            AvailableCardsGrid,
            grid => grid.ColumnDefinitions.Count is 3 or 5);
        _rightLayoutGrid = FindAncestorGrid(MainDeckGrid, grid => grid.RowDefinitions.Count == 6);

        // App.Activated installs this feature after the main window may already have fired Loaded.
        // Configure immediately in that case; otherwise wait for the normal Loaded event.
        if (IsLoaded)
        {
            RestoreSavedLayout();
            ConfigurePreviewInteractions();
            Dispatcher.BeginInvoke(FitNeighborPreviews, DispatcherPriority.Loaded);
        }
        else
        {
            Loaded += (_, _) =>
            {
                RestoreSavedLayout();
                ConfigurePreviewInteractions();
                Dispatcher.BeginInvoke(FitNeighborPreviews, DispatcherPriority.Loaded);
            };
        }

        Closing += (_, _) => SaveCurrentLayout();
        SizeChanged += (_, _) => Dispatcher.BeginInvoke(FitNeighborPreviews, DispatcherPriority.Background);
        AvailableCardsGrid.PreviewKeyDown += AvailableCardsGrid_QuickAddKeyDown;
    }

    private void ConfigurePreviewInteractions()
    {
        if (_neighborPreviewPanel is not null)
        {
            _neighborPreviewPanel.Orientation = Orientation.Vertical;
            _neighborPreviewPanel.HorizontalAlignment = HorizontalAlignment.Center;
            _neighborPreviewPanel.VerticalAlignment = VerticalAlignment.Top;
            _neighborPreviewPanel.LayoutUpdated -= NeighborPreviewPanel_LayoutUpdated;
            _neighborPreviewPanel.LayoutUpdated += NeighborPreviewPanel_LayoutUpdated;
        }

        // Use handledEventsToo=true. Viewbox/ScrollViewer descendants may mark the wheel event
        // handled before an ordinary PreviewMouseWheel subscription gets a useful chance to react.
        if (!_previewInteractionsConfigured && PreviewName.Parent is UIElement previewRoot)
        {
            previewRoot.AddHandler(
                Mouse.PreviewMouseWheelEvent,
                new MouseWheelEventHandler(PreviewArea_MouseWheel),
                true);
            _previewInteractionsConfigured = true;
        }

        if (_previewCountBox is not null)
        {
            int wanted = Math.Clamp(AppSettingsService.Current.PreviewCount, 1, 5);
            _previewCountBox.SelectedItem = wanted;
            _previewCountBox.SelectionChanged += (_, _) =>
            {
                if (_previewCountBox.SelectedItem is int selected)
                    AppSettingsService.Current.PreviewCount = selected;
                Dispatcher.BeginInvoke(FitNeighborPreviews, DispatcherPriority.Background);
            };
        }
    }

    private void NeighborPreviewPanel_LayoutUpdated(object? sender, EventArgs e) => FitNeighborPreviews();

    private void FitNeighborPreviews()
    {
        if (_fittingNeighborPreviews || _neighborPreviewPanel is null || _neighborPreviewPanel.Children.Count == 0)
            return;
        if (PreviewName.Parent is not FrameworkElement previewRoot || previewRoot.ActualHeight <= 0)
            return;

        _fittingNeighborPreviews = true;
        try
        {
            int count = _neighborPreviewPanel.Children.Count;
            double totalHeight = Math.Max(150, previewRoot.ActualHeight * 0.43);
            double spacing = 4.0;
            double availablePerCard = Math.Max(64, (totalHeight - spacing * count) / count);
            double widthFromHeight = availablePerCard * 356.0 / 512.0;
            double widthFromColumn = Math.Max(50, previewRoot.ActualWidth - 18);
            double width = Math.Clamp(Math.Min(widthFromHeight, widthFromColumn), 50, 145);
            double height = width * 512.0 / 356.0;
            double actualPanelHeight = count * (height + spacing);

            _neighborPreviewPanel.Width = width + 4;
            _neighborPreviewPanel.Height = Math.Max(actualPanelHeight, 1);
            _neighborPreviewPanel.MaxHeight = totalHeight;
            _neighborPreviewPanel.Orientation = Orientation.Vertical;

            foreach (FrameworkElement child in _neighborPreviewPanel.Children.OfType<FrameworkElement>())
            {
                child.Width = width;
                child.Height = height;
                child.Margin = new Thickness(2);
            }
        }
        finally
        {
            _fittingNeighborPreviews = false;
        }
    }

    private void PreviewArea_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0)
            return;

        // Calm scrolling: while the short cooldown is active, discard additional wheel/touchpad
        // events instead of queueing them. Nothing keeps "catching up" after the user stops.
        if (!_previewWheelThrottleTimer.IsEnabled)
        {
            MovePreviewSelection(e.Delta < 0 ? 1 : -1);
            _previewWheelThrottleTimer.Start();
        }

        e.Handled = true;
    }

    private void MovePreviewSelection(int direction)
    {
        DataGrid grid = _previewFromMainDeck && MainDeckGrid.Items.Count > 0
            ? MainDeckGrid
            : AvailableCardsGrid;
        if (grid.Items.Count == 0)
            return;

        int current = grid.SelectedIndex;
        if (current < 0)
            current = 0;

        int next = Math.Clamp(current + direction, 0, grid.Items.Count - 1);
        if (next == current)
            return;

        grid.SelectedIndex = next;
        grid.ScrollIntoView(grid.SelectedItem);
    }

    private void AvailableCardsGrid_QuickAddKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Right or Key.Enter or Key.Space))
            return;
        if (Keyboard.Modifiers != ModifierKeys.None)
            return;
        if (AvailableCardsGrid.SelectedItems.Count == 0)
            return;

        AddSelectedCards(DeckSection.MainDeck);
        e.Handled = true;
    }

    private void RestoreSavedLayout()
    {
        ModernAppSettings settings = AppSettingsService.Current;
        RestoreGridColumns(_mainLayoutGrid, settings.MainLayoutWidths);
        RestoreRightRows(_rightLayoutGrid, settings.RightLayoutRowHeights);
        RestoreDataGridColumns(AvailableCardsGrid, settings.CatalogColumnWidths);
        RestoreDataGridColumns(MainDeckGrid, settings.MainDeckColumnWidths);
        RestoreDataGridColumns(RegularUnlocksGrid, settings.RegularUnlockColumnWidths);
        RestoreDataGridColumns(PromoUnlocksGrid, settings.PromoUnlockColumnWidths);
    }

    private void SaveCurrentLayout()
    {
        ModernAppSettings settings = AppSettingsService.Current;
        settings.StartMaximized = WindowState == WindowState.Maximized;
        settings.MainLayoutWidths = _mainLayoutGrid?.ColumnDefinitions.Select(column => column.ActualWidth).ToList() ?? new();
        settings.RightLayoutRowHeights = _rightLayoutGrid is null
            ? new()
            : new List<double>
            {
                _rightLayoutGrid.RowDefinitions[1].ActualHeight,
                _rightLayoutGrid.RowDefinitions[5].ActualHeight
            };
        settings.CatalogColumnWidths = CaptureDataGridColumns(AvailableCardsGrid);
        settings.MainDeckColumnWidths = CaptureDataGridColumns(MainDeckGrid);
        settings.RegularUnlockColumnWidths = CaptureDataGridColumns(RegularUnlocksGrid);
        settings.PromoUnlockColumnWidths = CaptureDataGridColumns(PromoUnlocksGrid);
        if (_previewCountBox?.SelectedItem is int selected)
            settings.PreviewCount = selected;

        try
        {
            AppSettingsService.Save();
        }
        catch
        {
            // Layout persistence must never prevent the editor from closing.
        }
    }

    private static List<double> CaptureDataGridColumns(DataGrid grid) =>
        grid.Columns.Select(column => column.ActualWidth).Where(width => width > 0).ToList();

    private static void RestoreDataGridColumns(DataGrid grid, IReadOnlyList<double> widths)
    {
        if (widths.Count != grid.Columns.Count)
            return;

        for (int index = 0; index < widths.Count; index++)
        {
            if (widths[index] >= 24)
                grid.Columns[index].Width = new DataGridLength(widths[index], DataGridLengthUnitType.Pixel);
        }
    }

    private static void RestoreGridColumns(Grid? grid, IReadOnlyList<double> widths)
    {
        if (grid is null || widths.Count != grid.ColumnDefinitions.Count)
            return;

        for (int index = 0; index < widths.Count; index++)
        {
            if (widths[index] > 0)
                grid.ColumnDefinitions[index].Width = new GridLength(widths[index], GridUnitType.Pixel);
        }
    }

    private static void RestoreRightRows(Grid? grid, IReadOnlyList<double> heights)
    {
        if (grid is null || heights.Count != 2)
            return;
        if (heights[0] > 80)
            grid.RowDefinitions[1].Height = new GridLength(heights[0], GridUnitType.Pixel);
        if (heights[1] > 80)
            grid.RowDefinitions[5].Height = new GridLength(heights[1], GridUnitType.Pixel);
    }

    private static Grid? FindAncestorGrid(DependencyObject start, Func<Grid, bool> predicate)
    {
        DependencyObject? current = start;
        while (current is not null)
        {
            if (current is Grid grid && predicate(grid))
                return grid;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}
