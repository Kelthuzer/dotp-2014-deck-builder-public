using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace DeckBuilder.Modern;

public partial class MainWindow
{
    private bool _loadingIndicatorInstalled;
    private Border? _loadingOverlay;
    private TextBlock? _loadingOverlayTitle;
    private TextBlock? _loadingOverlayText;
    private DispatcherTimer? _loadingIndicatorTimer;
    private string _loadingIndicatorTitle = "Loading game data…";
    private string? _loadingIndicatorInitialDetail;

    internal void InstallLoadingIndicator()
    {
        if (_loadingIndicatorInstalled || Content is not DockPanel root)
        {
            return;
        }

        Grid? workspaceGrid = root.Children.OfType<Grid>().LastOrDefault();
        if (workspaceGrid is null)
        {
            return;
        }

        _loadingIndicatorInstalled = true;

        StackPanel panel = new()
        {
            Width = 420,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        _loadingOverlayTitle = new TextBlock
        {
            Text = _loadingIndicatorTitle,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12)
        };
        panel.Children.Add(_loadingOverlayTitle);

        ProgressBar progress = new()
        {
            Height = 8,
            IsIndeterminate = true,
            Margin = new Thickness(0, 0, 0, 10)
        };
        panel.Children.Add(progress);

        _loadingOverlayText = new TextBlock
        {
            Text = "Preparing…",
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105))
        };
        panel.Children.Add(_loadingOverlayText);

        Border card = new()
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(24),
            Child = panel,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        _loadingOverlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(150, 241, 245, 249)),
            Child = card,
            Visibility = Visibility.Collapsed
        };
        Grid.SetColumnSpan(_loadingOverlay, Math.Max(1, workspaceGrid.ColumnDefinitions.Count));
        Panel.SetZIndex(_loadingOverlay, 10000);
        workspaceGrid.Children.Add(_loadingOverlay);

        _loadingIndicatorTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _loadingIndicatorTimer.Tick += LoadingIndicatorTimer_Tick;
        _loadingIndicatorTimer.Start();
        Closed += (_, _) => _loadingIndicatorTimer?.Stop();
    }

    private void SetLoadingIndicatorContext(string title, string? initialDetail = null)
    {
        _loadingIndicatorTitle = title;
        _loadingIndicatorInitialDetail = initialDetail;
        if (_loadingOverlayTitle is not null)
        {
            _loadingOverlayTitle.Text = title;
        }
        if (_loadingOverlayText is not null && !string.IsNullOrWhiteSpace(initialDetail))
        {
            _loadingOverlayText.Text = initialDetail;
        }
    }

    private void LoadingIndicatorTimer_Tick(object? sender, EventArgs e)
    {
        if (_loadingOverlay is null || _loadingOverlayText is null)
        {
            return;
        }

        Visibility target = _loading ? Visibility.Visible : Visibility.Collapsed;
        if (_loadingOverlay.Visibility != target)
        {
            _loadingOverlay.Visibility = target;
        }

        if (_loadingOverlayTitle is not null)
        {
            _loadingOverlayTitle.Text = _loadingIndicatorTitle;
        }

        if (_loading)
        {
            string stage = StatusText.Text?.Trim() ?? string.Empty;
            _loadingOverlayText.Text = string.IsNullOrWhiteSpace(stage)
                ? (_loadingIndicatorInitialDetail ?? "Working…")
                : stage;
        }
    }
}
