using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DeckBuilder.GameData;

namespace DeckBuilder.Modern;

public partial class WadWorkshopWindow : Window
{
    private readonly string _gameDirectory;
    private readonly WadWorkshopScanner _scanner = new();
    private WadWorkshopSnapshot? _snapshot;
    private bool _scanning;

    public WadWorkshopWindow(string gameDirectory)
    {
        InitializeComponent();
        _gameDirectory = gameDirectory;
        PathText.Text = gameDirectory;
        AddVersionPackageButton();
        Loaded += WadWorkshopWindow_Loaded;
    }

    public InstalledDeckRecord? SelectedDeck { get; private set; }

    private void AddVersionPackageButton()
    {
        if (RescanButton.Parent is not Grid headerGrid)
        {
            return;
        }

        headerGrid.Children.Remove(RescanButton);
        StackPanel actions = new()
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(actions, 1);
        RescanButton.Margin = new Thickness(0, 0, 0, 6);
        actions.Children.Add(RescanButton);
        Button packageButton = new()
        {
            Content = "Version extractor / builder…",
            Padding = new Thickness(12, 6, 12, 6)
        };
        packageButton.Click += VersionPackages_Click;
        actions.Children.Add(packageButton);
        headerGrid.Children.Add(actions);
    }

    private void VersionPackages_Click(object sender, RoutedEventArgs e)
    {
        VersionPackagesWindow dialog = new(_gameDirectory) { Owner = this };
        dialog.ShowDialog();
    }

    private async void WadWorkshopWindow_Loaded(object sender, RoutedEventArgs e) => await ScanAsync();

    private async void Rescan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        if (_scanning)
        {
            return;
        }

        _scanning = true;
        RescanButton.IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            Progress<WadWorkshopProgress> progress = new(value =>
                StatusText.Text = $"{value.Stage}: {value.Source} ({value.Completed}/{value.Total})");
            _snapshot = await _scanner.ScanAsync(_gameDirectory, progress);
            WadsGrid.ItemsSource = _snapshot.Wads;
            ApplyFilters();
            SummaryText.Text =
                $"{_snapshot.Wads.Count:N0} WADs · {_snapshot.CardPool.Count:N0} card references · " +
                $"{_snapshot.Decks.Count:N0} decks · {_snapshot.Conflicts.Count:N0} pool conflicts/issues · " +
                $"{_snapshot.Warnings.Count:N0} scan warnings";
            StatusText.Text = "WAD Workshop scan complete.";
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "WAD Workshop scan failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            StatusText.Text = "Scan failed.";
        }
        finally
        {
            Mouse.OverrideCursor = null;
            RescanButton.IsEnabled = true;
            _scanning = false;
        }
    }

    private void CardPoolSearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyCardPoolFilter();

    private void DeckSearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyDeckFilter();

    private void ConflictSearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyConflictFilter();

    private void ApplyFilters()
    {
        ApplyCardPoolFilter();
        ApplyDeckFilter();
        ApplyConflictFilter();
    }

    private void ApplyCardPoolFilter()
    {
        if (_snapshot is null)
        {
            return;
        }

        string query = CardPoolSearchBox.Text.Trim();
        CardPoolGrid.ItemsSource = string.IsNullOrWhiteSpace(query)
            ? _snapshot.CardPool
            : _snapshot.CardPool.Where(card => CardMatches(card, query)).ToArray();
    }

    private void ApplyDeckFilter()
    {
        if (_snapshot is null)
        {
            return;
        }

        string query = DeckSearchBox.Text.Trim();
        DecksGrid.ItemsSource = string.IsNullOrWhiteSpace(query)
            ? _snapshot.Decks
            : _snapshot.Decks.Where(deck => DeckMatches(deck, query)).ToArray();
    }

    private void ApplyConflictFilter()
    {
        if (_snapshot is null)
        {
            return;
        }

        string query = ConflictSearchBox.Text.Trim();
        ConflictsGrid.ItemsSource = string.IsNullOrWhiteSpace(query)
            ? _snapshot.Conflicts
            : _snapshot.Conflicts.Where(card => CardMatches(card, query)).ToArray();
    }

    private static bool CardMatches(CardPoolRecord card, string query) =>
        Contains(card.DisplayName, query)
        || Contains(card.Reference, query)
        || Contains(card.Status, query)
        || Contains(card.EffectiveSource, query)
        || Contains(card.DefinitionsText, query)
        || Contains(card.ArtSourcesText, query);

    private static bool DeckMatches(DeckHealthRecord deck, string query) =>
        Contains(deck.DisplayName, query)
        || Contains(deck.Source, query)
        || Contains(deck.Uid.ToString(), query)
        || Contains(deck.Status, query)
        || Contains(deck.ProblemsText, query);

    private static bool Contains(string? value, string query) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains(query, StringComparison.OrdinalIgnoreCase);

    private void CardPoolGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CardDetailsText.Text = CardPoolGrid.SelectedItem is CardPoolRecord card
            ? Details(card)
            : string.Empty;
    }

    private void ConflictsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ConflictDetailsText.Text = ConflictsGrid.SelectedItem is CardPoolRecord card
            ? Details(card)
            : string.Empty;
    }

    private void DecksGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DeckProblemsText.Text = DecksGrid.SelectedItem is DeckHealthRecord deck
            ? string.IsNullOrWhiteSpace(deck.ProblemsText)
                ? "No unresolved card-definition or illustration problems were found."
                : deck.ProblemsText
            : string.Empty;
    }

    private void DecksGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedDeck();

    private void OpenSelectedDeck_Click(object sender, RoutedEventArgs e) => OpenSelectedDeck();

    private void OpenSelectedDeck()
    {
        if (DecksGrid.SelectedItem is not DeckHealthRecord health)
        {
            return;
        }

        SelectedDeck = health.Deck;
        DialogResult = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static string Details(CardPoolRecord card)
    {
        List<string> lines = new()
        {
            $"Status: {card.Status}",
            $"Effective: {(string.IsNullOrWhiteSpace(card.EffectiveSource) ? "none" : card.EffectiveSource)} (order {card.EffectiveOrder})"
        };
        if (!string.IsNullOrWhiteSpace(card.DefinitionsText))
        {
            lines.Add(card.DefinitionsText);
        }

        lines.Add(string.IsNullOrWhiteSpace(card.ArtSourcesText)
            ? "Illustration: not found in the selected WAD pool"
            : $"Illustration WADs: {card.ArtSourcesText}");
        return string.Join(Environment.NewLine, lines);
    }
}
