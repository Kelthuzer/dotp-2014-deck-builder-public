using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DeckBuilder.Modern;

public partial class MainWindow
{
    private bool _adaptiveWorkspaceInstalled;
    private Grid? _adaptiveWorkspaceGrid;
    private Grid? _adaptiveRightWorkspace;
    private RowDefinition? _adaptivePreviewRow;
    private RowDefinition? _adaptiveDeckRow;

    internal void InstallAdaptiveWorkspaceLayout()
    {
        if (_adaptiveWorkspaceInstalled)
            return;

        Grid? root = FindAncestorGrid(AvailableCardsGrid, grid => grid.ColumnDefinitions.Count == 5);
        if (root is null)
            return;

        Border? catalogPanel = FindDirectChildAncestor<Border>(AvailableCardsGrid, root);
        Border? previewPanel = FindDirectChildAncestor<Border>(CardPreviewViewbox, root);
        Border? deckPanel = FindDirectChildAncestor<Border>(MainDeckGrid, root);
        if (catalogPanel is null || previewPanel is null || deckPanel is null)
            return;

        _adaptiveWorkspaceInstalled = true;
        _adaptiveWorkspaceGrid = root;

        // Retire the old three-column editor arrangement. The catalog remains on the left;
        // preview and deck editing share a much wider workspace on the right.
        root.Children.Clear();
        root.ColumnDefinitions.Clear();
        root.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(0.92, GridUnitType.Star),
            MinWidth = 390
        });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        root.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1.78, GridUnitType.Star),
            MinWidth = 680
        });

        Grid.SetColumn(catalogPanel, 0);
        root.Children.Add(catalogPanel);

        Grid verticalSplitterHost = new();
        Grid.SetColumn(verticalSplitterHost, 1);
        verticalSplitterHost.Children.Add(new GridSplitter
        {
            Width = 10,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.Transparent,
            ResizeDirection = GridResizeDirection.Columns,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext
        });
        root.Children.Add(verticalSplitterHost);

        _adaptiveRightWorkspace = new Grid();
        _adaptivePreviewRow = new RowDefinition();
        _adaptiveDeckRow = new RowDefinition();
        double previewRatio = Math.Clamp(AppSettingsService.Current.WorkspacePreviewRatio, 0.42, 0.72);
        _adaptivePreviewRow.Height = new GridLength(previewRatio, GridUnitType.Star);
        _adaptiveRightWorkspace.RowDefinitions.Add(_adaptivePreviewRow);
        _adaptiveRightWorkspace.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
        _adaptiveDeckRow.Height = new GridLength(1.0 - previewRatio, GridUnitType.Star);
        _adaptiveRightWorkspace.RowDefinitions.Add(_adaptiveDeckRow);
        Grid.SetColumn(_adaptiveRightWorkspace, 2);
        root.Children.Add(_adaptiveRightWorkspace);

        Grid.SetRow(previewPanel, 0);
        previewPanel.Margin = new Thickness(0, 0, 0, 0);
        _adaptiveRightWorkspace.Children.Add(previewPanel);

        GridSplitter horizontalSplitter = new()
        {
            Height = 10,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = Brushes.Transparent,
            ResizeDirection = GridResizeDirection.Rows,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext
        };
        Grid.SetRow(horizontalSplitter, 1);
        _adaptiveRightWorkspace.Children.Add(horizontalSplitter);

        Grid.SetRow(deckPanel, 2);
        deckPanel.Margin = new Thickness(0);
        _adaptiveRightWorkspace.Children.Add(deckPanel);
        InstallPackageDeckButton(deckPanel);

        // The wider preview is the primary reason for the layout change. Re-render after WPF
        // has measured the new workspace so multi-card previews immediately use the new area.
        _adaptiveRightWorkspace.SizeChanged += (_, _) =>
        {
            if (_previewCountBox?.SelectedItem is int count && count > 1)
                _ = RefreshUnifiedMultiPreviewAsync();
        };

        Closing += (_, _) => SaveAdaptiveWorkspaceRatio();
        Dispatcher.BeginInvoke(() =>
        {
            AppThemeService.ApplyCurrent();
            if (_previewCountBox?.SelectedItem is int count && count > 1)
                _ = RefreshUnifiedMultiPreviewAsync();
        });
    }

    private void SaveAdaptiveWorkspaceRatio()
    {
        if (_adaptivePreviewRow is null || _adaptiveDeckRow is null)
            return;

        double preview = _adaptivePreviewRow.ActualHeight;
        double deck = _adaptiveDeckRow.ActualHeight;
        double total = preview + deck;
        if (total <= 1)
            return;

        AppSettingsService.Current.WorkspacePreviewRatio = Math.Clamp(preview / total, 0.42, 0.72);
        try
        {
            AppSettingsService.Save();
        }
        catch
        {
            // Layout persistence should never block shutdown.
        }
    }

    private static T? FindDirectChildAncestor<T>(DependencyObject start, DependencyObject parent)
        where T : DependencyObject
    {
        DependencyObject? current = start;
        while (current is not null)
        {
            DependencyObject? visualParent = VisualTreeHelper.GetParent(current);
            if (ReferenceEquals(visualParent, parent) && current is T match)
                return match;
            current = visualParent;
        }
        return null;
    }
}
