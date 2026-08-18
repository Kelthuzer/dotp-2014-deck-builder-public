using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using DeckBuilder.GameData;

namespace DeckBuilder.Modern;

internal sealed class DeckBuildProgressWindow : Window
{
    private readonly ProgressBar _progressBar = new()
    {
        Minimum = 0,
        Maximum = 100,
        Height = 20
    };
    private readonly TextBlock _stageText = new()
    {
        FontSize = 18,
        FontWeight = FontWeights.SemiBold,
        TextWrapping = TextWrapping.Wrap
    };
    private readonly TextBlock _detailText = new()
    {
        Margin = new Thickness(0, 8, 0, 12),
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.8
    };
    private readonly TextBlock _percentText = new()
    {
        HorizontalAlignment = HorizontalAlignment.Right,
        Margin = new Thickness(0, 6, 0, 0),
        FontWeight = FontWeights.SemiBold
    };
    private readonly Button _cancelButton = new()
    {
        Content = "Отмена",
        MinWidth = 95,
        Margin = new Thickness(0, 16, 0, 0),
        HorizontalAlignment = HorizontalAlignment.Right
    };
    private readonly CancellationTokenSource _cancellation = new();
    private bool _completed;

    public CancellationToken CancellationToken => _cancellation.Token;

    public DeckBuildProgressWindow()
    {
        Title = "Сборка колоды";
        Width = 640;
        Height = 245;
        MinWidth = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        Grid root = new() { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(_stageText);
        Grid.SetRow(_detailText, 1);
        root.Children.Add(_detailText);
        Grid.SetRow(_progressBar, 2);
        root.Children.Add(_progressBar);
        Grid.SetRow(_percentText, 3);
        root.Children.Add(_percentText);
        Grid.SetRow(_cancelButton, 4);
        root.Children.Add(_cancelButton);

        _cancelButton.Click += (_, _) => RequestCancel();
        Closing += OnClosing;
        Content = root;

        SetProgress(0, "Подготовка", "Сборка ещё не началась.");
        AppThemeService.ApplyCurrent();
    }

    public void Report(WorkspaceSelectedCardsProgress value) =>
        SetProgress(value.Percent, value.Stage, value.Detail);

    public void SetProgress(int percent, string stage, string detail)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetProgress(percent, stage, detail));
            return;
        }

        int bounded = Math.Clamp(percent, 0, 100);
        _progressBar.IsIndeterminate = false;
        _progressBar.Value = bounded;
        _stageText.Text = stage;
        _detailText.Text = detail;
        _percentText.Text = $"{bounded}%";
    }

    public void SetIndeterminate(string stage, string detail)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetIndeterminate(stage, detail));
            return;
        }

        _progressBar.IsIndeterminate = true;
        _stageText.Text = stage;
        _detailText.Text = detail;
        _percentText.Text = string.Empty;
    }

    public void MarkCompleted()
    {
        _completed = true;
        _cancelButton.IsEnabled = false;
    }

    private void RequestCancel()
    {
        if (_completed || _cancellation.IsCancellationRequested)
            return;

        _cancellation.Cancel();
        _cancelButton.IsEnabled = false;
        _stageText.Text = "Отмена…";
        _detailText.Text = "Завершаю текущую безопасную операцию и удаляю временные файлы.";
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_completed)
            return;

        RequestCancel();
        e.Cancel = true;
    }
}
