using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using DeckBuilder.GameData;

namespace DeckBuilder.Modern;

public partial class CardReferenceScannerWindow : Window
{
    private readonly string _workspaceRoot;
    private readonly CardReferenceScanService _scanner = new();
    private readonly ObservableCollection<CardReferenceScanRow> _rows = new();
    private readonly ICollectionView _view;
    private CardReferenceScanResult? _result;
    private bool _scanning;

    public CardReferenceScannerWindow(string workspaceRoot)
    {
        InitializeComponent();
        _workspaceRoot = workspaceRoot;
        WorkspaceText.Text = workspaceRoot;
        ResultsGrid.ItemsSource = _rows;
        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = MatchesFilter;
        FilterBox.SelectionChanged += FilterBox_SelectionChanged;
        SearchBox.TextChanged += SearchBox_TextChanged;
        ResultsGrid.MouseDoubleClick += ResultsGrid_MouseDoubleClick;
        Loaded += CardReferenceScannerWindow_Loaded;
    }

    private async void CardReferenceScannerWindow_Loaded(object sender, RoutedEventArgs e) => await ScanAsync();
    private async void Rescan_Click(object sender, RoutedEventArgs e) => await ScanAsync();
    private void FilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => _view.Refresh();
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => _view.Refresh();

    private void ResultsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsGrid.SelectedItem is not CardReferenceScanRow row)
            return;

        string usedBy = string.IsNullOrWhiteSpace(row.UsedBy) ? "(none)" : row.UsedBy;
        string candidates = string.IsNullOrWhiteSpace(row.ArtCandidates) ? "(none)" : row.ArtCandidates;
        string matches = string.IsNullOrWhiteSpace(row.ArtMatches) ? "(none)" : row.ArtMatches;
        string artPath = string.IsNullOrWhiteSpace(row.ArtPath) ? "(not found)" : row.ArtPath;

        MessageBox.Show(
            this,
            $"Card: {row.FileName}\n" +
            $"Token: {(row.IsToken ? "yes" : "no")}\n\n" +
            $"ARTID: {ValueOrNone(row.ArtId)}\n" +
            $"MULTIVERSEID: {ValueOrNone(row.MultiverseId)}\n" +
            $"Inbound references: {row.InboundReferenceCount}\n" +
            $"Art found: {(row.ArtFound ? "yes" : "no")}\n\n" +
            $"Used by:\n{usedBy}\n\n" +
            $"Art lookup keys tried:\n{candidates}\n\n" +
            $"Matching TDX files:\n{matches}\n\n" +
            $"Selected art path:\n{artPath}\n\n" +
            $"Card XML source:\n{row.Source}",
            "Card diagnostics",
            MessageBoxButton.OK,
            row.ArtFound ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private static string ValueOrNone(string value) => string.IsNullOrWhiteSpace(value) ? "(none)" : value;

    private async Task ScanAsync()
    {
        if (_scanning) return;
        _scanning = true;
        Mouse.OverrideCursor = Cursors.Wait;
        StatusText.Text = "Scanning…";
        try
        {
            Progress<string> progress = new(text => StatusText.Text = text);
            _result = await _scanner.ScanAsync(_workspaceRoot, progress);
            _rows.Clear();
            foreach (CardReferenceScanRow row in _result.Rows)
                _rows.Add(row);
            UpdateSummary();
            StatusText.Text = $"Complete — {_rows.Count:N0} card records · double-click a row for diagnostics";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Card reference scan failed", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Scan failed";
        }
        finally
        {
            Mouse.OverrideCursor = null;
            _scanning = false;
        }
    }

    private bool MatchesFilter(object item)
    {
        if (item is not CardReferenceScanRow row) return false;
        string mode = (FilterBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All cards";
        bool modeMatch = mode switch
        {
            "All tokens" => row.IsToken,
            "Used tokens" => row.IsToken && row.InboundReferenceCount > 0,
            "Orphan tokens" => row.IsToken && row.InboundReferenceCount == 0,
            "Missing art — all cards" => !row.ArtFound,
            "Used + missing art" => row.InboundReferenceCount > 0 && !row.ArtFound,
            _ => true
        };
        if (!modeMatch) return false;

        string search = SearchBox.Text.Trim();
        if (search.Length == 0) return true;
        return row.FileName.Contains(search, StringComparison.OrdinalIgnoreCase)
            || row.ArtId.Contains(search, StringComparison.OrdinalIgnoreCase)
            || row.MultiverseId.Contains(search, StringComparison.OrdinalIgnoreCase)
            || row.Source.Contains(search, StringComparison.OrdinalIgnoreCase)
            || row.UsedBy.Contains(search, StringComparison.OrdinalIgnoreCase)
            || row.ArtCandidates.Contains(search, StringComparison.OrdinalIgnoreCase)
            || row.ArtMatches.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateSummary()
    {
        if (_result is null) return;
        int tokens = _rows.Count(row => row.IsToken);
        int usedTokens = _rows.Count(row => row.IsToken && row.InboundReferenceCount > 0);
        int orphanTokens = _rows.Count(row => row.IsToken && row.InboundReferenceCount == 0);
        int missingAll = _rows.Count(row => !row.ArtFound);
        int missingRegular = _rows.Count(row => !row.IsToken && !row.ArtFound);
        int usedMissing = _rows.Count(row => row.InboundReferenceCount > 0 && !row.ArtFound);
        SummaryText.Text = $"Cards {_result.CardRecords:N0} · TDX {_result.TdxFiles:N0} · Tokens {tokens:N0} · Used tokens {usedTokens:N0} · Orphan tokens {orphanTokens:N0} · Missing art {missingAll:N0} (regular {missingRegular:N0}) · Used + missing {usedMissing:N0}";
    }
}
