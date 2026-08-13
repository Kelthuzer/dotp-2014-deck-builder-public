using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeckBuilder.GameData;
using Microsoft.VisualBasic.FileIO;

namespace DeckBuilder.Modern;

public enum DeckLibraryAction
{
    Open,
    Copy
}

public partial class DeckLibraryWindow : Window
{
    private readonly List<InstalledDeckRecord> _allDecks;
    private readonly GameCardImageLoader? _imageLoader;
    private readonly string? _workspaceDirectory;
    private int _previewVersion;

    public DeckLibraryWindow(
        IReadOnlyList<InstalledDeckRecord> decks,
        GameCardImageLoader? imageLoader = null,
        string? workspaceDirectory = null)
    {
        InitializeComponent();
        _allDecks = decks?.ToList() ?? new List<InstalledDeckRecord>();
        _imageLoader = imageLoader;
        _workspaceDirectory = string.IsNullOrWhiteSpace(workspaceDirectory) ? null : Path.GetFullPath(workspaceDirectory);
        bool workspaceAvailable = _workspaceDirectory is not null && Directory.Exists(_workspaceDirectory);
        CleanupButton.IsEnabled = workspaceAvailable;
        DeleteButton.IsEnabled = false;
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
        UpdateActionButtons();
    }

    private async void DeckGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        InstalledDeckRecord? selected = DeckGrid.SelectedItem as InstalledDeckRecord;
        UpdateActionButtons();
        await UpdatePreviewAsync(selected);
    }

    private void UpdateActionButtons()
    {
        bool hasSingle = DeckGrid.SelectedItems.Count == 1 && DeckGrid.SelectedItem is InstalledDeckRecord;
        OpenButton.IsEnabled = hasSingle;
        CopyButton.IsEnabled = hasSingle;
        DeleteButton.IsEnabled = DeckGrid.SelectedItems.Count > 0
            && _workspaceDirectory is not null
            && Directory.Exists(_workspaceDirectory);
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

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        string? workspaceDirectory = _workspaceDirectory;
        if (string.IsNullOrWhiteSpace(workspaceDirectory) || !Directory.Exists(workspaceDirectory))
        {
            MessageBox.Show(this,
                "Load an unpacked workspace first. Deck deletion only removes loose workspace deck XML files; packed game WADs are never modified.",
                "Workspace required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        InstalledDeckRecord[] selected = DeckGrid.SelectedItems
            .OfType<InstalledDeckRecord>()
            .ToArray();
        if (selected.Length == 0)
        {
            return;
        }

        List<(InstalledDeckRecord Deck, string Path)> resolved = new();
        List<string> unresolved = new();
        foreach (InstalledDeckRecord deck in selected)
        {
            string? path = ResolveLooseDeckPath(workspaceDirectory, deck);
            if (path is null)
            {
                unresolved.Add($"{deck.FriendlyName} [{deck.TechnicalName}] from {deck.Source}");
            }
            else
            {
                resolved.Add((deck, path));
            }
        }

        if (unresolved.Count > 0)
        {
            MessageBox.Show(this,
                "These selected decks could not be mapped to one unique loose XML file and were not touched:\n\n" +
                string.Join("\n", unresolved.Take(12)) +
                (unresolved.Count > 12 ? $"\n… and {unresolved.Count - 12} more" : string.Empty),
                "Some decks cannot be deleted safely", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        if (resolved.Count == 0)
        {
            return;
        }

        string sample = string.Join("\n", resolved.Take(8).Select(item =>
            $"• {item.Deck.FriendlyName} ({item.Deck.CardCount} cards) — {Path.GetRelativePath(workspaceDirectory, item.Path)}"));
        if (resolved.Count > 8)
        {
            sample += $"\n… and {resolved.Count - 8} more";
        }

        MessageBoxResult confirmation = MessageBox.Show(this,
            $"Delete {resolved.Count} selected deck(s) from the unpacked workspace?\n\n{sample}\n\n" +
            "Only the selected deck XML files are removed. Related card art, scripts and other shared resources are left untouched.\n" +
            "Files are sent to the Windows Recycle Bin.",
            "Confirm deck deletion", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        List<string> failures = new();
        List<InstalledDeckRecord> deleted = new();
        foreach ((InstalledDeckRecord deck, string path) in resolved)
        {
            try
            {
                FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                deleted.Add(deck);
            }
            catch (Exception exception)
            {
                failures.Add($"{Path.GetRelativePath(workspaceDirectory, path)}: {exception.Message}");
            }
        }

        foreach (InstalledDeckRecord deck in deleted)
        {
            _allDecks.Remove(deck);
        }
        RefreshResults();

        if (failures.Count > 0)
        {
            MessageBox.Show(this,
                "Some deck files could not be deleted:\n\n" + string.Join("\n", failures.Take(12)),
                "Deck deletion completed with errors", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string? ResolveLooseDeckPath(string workspaceDirectory, InstalledDeckRecord deck)
    {
        string expectedFile = deck.TechnicalName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
            ? deck.TechnicalName
            : deck.TechnicalName + ".xml";

        string[] candidates = Directory.EnumerateFiles(workspaceDirectory, expectedFile, System.IO.SearchOption.AllDirectories)
            .Where(path => path.Replace('/', '\\').Contains("\\DATA_ALL_PLATFORMS\\DECKS\\", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (candidates.Length == 1)
        {
            return candidates[0];
        }

        if (candidates.Length == 0)
        {
            return null;
        }

        string[] sourceMatches = candidates
            .Where(path => PathMatchesSource(workspaceDirectory, path, deck.Source))
            .ToArray();
        return sourceMatches.Length == 1 ? sourceMatches[0] : null;
    }

    private static bool PathMatchesSource(string workspaceDirectory, string path, string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        string relative = Path.GetRelativePath(workspaceDirectory, path).Replace('/', '\\');
        string normalizedSource = Path.GetFileNameWithoutExtension(source)?.Trim() ?? source.Trim();
        return relative.StartsWith(normalizedSource + "\\", StringComparison.OrdinalIgnoreCase)
            || relative.Contains("\\" + normalizedSource + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private void DeckGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DeckGrid.SelectedItems.Count == 1 && DeckGrid.SelectedItem is InstalledDeckRecord)
        {
            AcceptSelection(DeckLibraryAction.Open);
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e) => AcceptSelection(DeckLibraryAction.Open);

    private void Copy_Click(object sender, RoutedEventArgs e) => AcceptSelection(DeckLibraryAction.Copy);

    private void AcceptSelection(DeckLibraryAction action)
    {
        if (DeckGrid.SelectedItems.Count != 1 || DeckGrid.SelectedItem is not InstalledDeckRecord selected)
        {
            return;
        }

        SelectedDeck = selected;
        RequestedAction = action;
        DialogResult = true;
    }
}
