using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeckBuilder.GameData;

namespace DeckBuilder.Modern;

public enum DeckLibraryAction
{
    Open,
    Copy
}

public partial class DeckLibraryWindow : Window
{
    private readonly IReadOnlyList<InstalledDeckRecord> _allDecks;
    private readonly GameCardImageLoader? _imageLoader;
    private readonly string? _workspaceDirectory;
    private int _previewVersion;

    public DeckLibraryWindow(
        IReadOnlyList<InstalledDeckRecord> decks,
        GameCardImageLoader? imageLoader = null,
        string? workspaceDirectory = null)
    {
        InitializeComponent();
        _allDecks = decks ?? Array.Empty<InstalledDeckRecord>();
        _imageLoader = imageLoader;
        _workspaceDirectory = string.IsNullOrWhiteSpace(workspaceDirectory) ? null : Path.GetFullPath(workspaceDirectory);
        CleanupButton.IsEnabled = _workspaceDirectory is not null && Directory.Exists(_workspaceDirectory);
        RefreshResults();
    }

    public InstalledDeckRecord? SelectedDeck { get; private set; }

    public DeckLibraryAction RequestedAction { get; private set; } = DeckLibraryAction.Open;

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshResults();

    private void RefreshResults()
    {
        string query = SearchBox.Text.Trim();
        IEnumerable<InstalledDeckRecord> result = _allDecks;
        if (query.Length > 0)
        {
            result = result.Where(deck =>
                deck.FriendlyName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || deck.TechnicalName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || deck.Source.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || deck.Uid.ToString().Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        InstalledDeckRecord[] visible = result
            .OrderBy(deck => deck.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(deck => deck.Source, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(deck => deck.Uid)
            .ToArray();
        DeckGrid.ItemsSource = visible;
        ResultCountText.Text = $"{visible.Length:N0} deck variant(s)";
        if (visible.Length > 0 && DeckGrid.SelectedItem is null)
        {
            DeckGrid.SelectedIndex = 0;
        }
        SetActionButtonsEnabled(DeckGrid.SelectedItem is InstalledDeckRecord);
    }

    private async void DeckGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        InstalledDeckRecord? selected = DeckGrid.SelectedItem as InstalledDeckRecord;
        SetActionButtonsEnabled(selected is not null);
        await UpdatePreviewAsync(selected);
    }

    private async Task UpdatePreviewAsync(InstalledDeckRecord? deck)
    {
        int version = ++_previewVersion;
        DeckCoverPreview.Source = null;
        DeckCoverPlaceholder.Visibility = Visibility.Visible;

        if (deck is null)
        {
            PreviewGameName.Text = "Select a deck";
            PreviewTechnicalName.Text = string.Empty;
            PreviewDeckBoxId.Text = string.Empty;
            PreviewStats.Text = string.Empty;
            PreviewSource.Text = string.Empty;
            return;
        }

        PreviewGameName.Text = deck.FriendlyName;
        PreviewTechnicalName.Text = deck.TechnicalName;
        PreviewDeckBoxId.Text = string.IsNullOrWhiteSpace(deck.Deck.DeckBoxImage)
            ? "Deck box: —"
            : $"Deck box: {deck.Deck.DeckBoxImage}";
        PreviewStats.Text = $"UID {deck.Uid}  •  {deck.CardCount} cards  •  {deck.RegularUnlockCount} unlocks  •  {deck.PromoUnlockCount} promo";
        PreviewSource.Text = deck.Source;

        if (_imageLoader is null || string.IsNullOrWhiteSpace(deck.Deck.DeckBoxImage))
        {
            return;
        }

        try
        {
            CardImageData? image = await _imageLoader.LoadAsync(deck.Deck.DeckBoxImage, GameImageKind.Deck);
            if (version != _previewVersion || image is null)
            {
                return;
            }

            BitmapSource bitmap = BitmapSource.Create(
                image.Width,
                image.Height,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                image.BgraPixels,
                checked(image.Width * 4));
            bitmap.Freeze();
            DeckCoverPreview.Source = bitmap;
            DeckCoverPlaceholder.Visibility = Visibility.Collapsed;
        }
        catch
        {
            if (version == _previewVersion)
            {
                DeckCoverPreview.Source = null;
                DeckCoverPlaceholder.Visibility = Visibility.Visible;
            }
        }
    }

    private void CleanupDuplicates_Click(object sender, RoutedEventArgs e)
    {
        string? workspaceDirectory = _workspaceDirectory;
        if (string.IsNullOrWhiteSpace(workspaceDirectory) || !Directory.Exists(workspaceDirectory))
        {
            MessageBox.Show(this,
                "Load an unpacked workspace first. Duplicate cleanup edits loose workspace files, never packed game WADs.",
                "Workspace required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        WorkspaceDuplicateCleanupWindow dialog = new(workspaceDirectory) { Owner = this };
        dialog.ShowDialog();
    }

    private void SetActionButtonsEnabled(bool enabled)
    {
        OpenButton.IsEnabled = enabled;
        CopyButton.IsEnabled = enabled;
    }

    private void DeckGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DeckGrid.SelectedItem is InstalledDeckRecord)
        {
            AcceptSelection(DeckLibraryAction.Open);
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e) => AcceptSelection(DeckLibraryAction.Open);

    private void Copy_Click(object sender, RoutedEventArgs e) => AcceptSelection(DeckLibraryAction.Copy);

    private void AcceptSelection(DeckLibraryAction action)
    {
        if (DeckGrid.SelectedItem is not InstalledDeckRecord selected)
        {
            return;
        }

        SelectedDeck = selected;
        RequestedAction = action;
        DialogResult = true;
    }
}
