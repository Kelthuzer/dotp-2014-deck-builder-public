using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeckBuilder.Core.Models;
using DeckBuilder.GameData;
using Microsoft.VisualBasic.FileIO;

namespace DeckBuilder.Modern;

public partial class WorkspaceCardCleanupWindow : Window
{
    private readonly string _workspaceDirectory;
    private readonly IReadOnlyList<CardRecord> _catalog;
    private readonly GameCardImageLoader? _imageLoader;
    private readonly ObservableCollection<CardCleanupRow> _rows = new();
    private IReadOnlyList<CardCleanupRow> _allRows = Array.Empty<CardCleanupRow>();
    private int _previewVersion;

    public WorkspaceCardCleanupWindow(
        string workspaceDirectory,
        IReadOnlyList<CardRecord>? catalog = null,
        GameCardImageLoader? imageLoader = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceDirectory);
        _workspaceDirectory = Path.GetFullPath(workspaceDirectory);
        _catalog = catalog ?? Array.Empty<CardRecord>();
        _imageLoader = imageLoader;
        InitializeComponent();
        CardGrid.ItemsSource = _rows;
        Loaded += async (_, _) => await ScanAsync();
    }

    private async Task ScanAsync()
    {
        IsEnabled = false;
        SummaryText.Text = "Scanning workspace card XML files…";
        try
        {
            _allRows = await Task.Run(() => FindCards(_workspaceDirectory, _catalog));
            ApplyFilter();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Card cleanup scan failed", MessageBoxButton.OK, MessageBoxImage.Error);
            SummaryText.Text = "Scan failed.";
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private static IReadOnlyList<CardCleanupRow> FindCards(string root, IReadOnlyList<CardRecord> catalog)
    {
        Dictionary<string, CardRecord> byFile = catalog
            .GroupBy(card => card.FileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        List<FileCandidate> candidates = new();
        foreach (string path in Directory.EnumerateFiles(root, "*.xml", System.IO.SearchOption.AllDirectories))
        {
            string normalized = path.Replace('/', '\\');
            if (!normalized.Contains("\\DATA_ALL_PLATFORMS\\CARDS\\", StringComparison.OrdinalIgnoreCase)
                && !normalized.Contains("\\CARDS\\", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            FileInfo info = new(path);
            candidates.Add(new FileCandidate(path, info.Length));
        }

        Dictionary<string, DuplicateInfo> duplicateInfo = BuildDuplicateInfo(candidates);
        List<CardCleanupRow> result = new();
        foreach (FileCandidate candidate in candidates.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
        {
            duplicateInfo.TryGetValue(candidate.Path, out DuplicateInfo? duplicate);
            string id = Path.GetFileNameWithoutExtension(candidate.Path) ?? string.Empty;
            byFile.TryGetValue(id, out CardRecord? card);
            result.Add(new CardCleanupRow(
                duplicate?.GroupKey,
                duplicate?.GroupNumber,
                candidate.Path,
                Path.GetRelativePath(root, candidate.Path),
                candidate.Length,
                card));
        }

        return result;
    }

    private static Dictionary<string, DuplicateInfo> BuildDuplicateInfo(IReadOnlyList<FileCandidate> candidates)
    {
        Dictionary<string, DuplicateInfo> result = new(StringComparer.OrdinalIgnoreCase);
        int groupNumber = 1;
        foreach (IGrouping<long, FileCandidate> sizeGroup in candidates
                     .GroupBy(candidate => candidate.Length)
                     .Where(group => group.Count() > 1)
                     .OrderBy(group => group.Key))
        {
            Dictionary<string, List<FileCandidate>> hashes = new(StringComparer.OrdinalIgnoreCase);
            foreach (FileCandidate candidate in sizeGroup)
            {
                using FileStream input = File.OpenRead(candidate.Path);
                string hash = Convert.ToHexString(SHA256.HashData(input));
                if (!hashes.TryGetValue(hash, out List<FileCandidate>? same))
                {
                    same = new List<FileCandidate>();
                    hashes.Add(hash, same);
                }
                same.Add(candidate);
            }

            foreach (List<FileCandidate> exact in hashes.Values.Where(group => group.Count > 1))
            {
                string key = $"Card:{groupNumber}";
                foreach (FileCandidate candidate in exact)
                {
                    result[candidate.Path] = new DuplicateInfo(key, groupNumber);
                }
                groupNumber++;
            }
        }
        return result;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        string query = SearchBox.Text.Trim();
        IEnumerable<CardCleanupRow> visible = _allRows;
        if (query.Length > 0)
        {
            visible = visible.Where(row =>
                row.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || row.EnglishName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || row.FileName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || row.TypeLine.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || row.Expansion.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || row.RelativePath.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        }

        CardCleanupRow? previous = CardGrid.SelectedItem as CardCleanupRow;
        _rows.Clear();
        foreach (CardCleanupRow row in visible
                     .OrderBy(row => string.IsNullOrWhiteSpace(row.DisplayName))
                     .ThenBy(row => row.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                     .ThenBy(row => row.FileName, StringComparer.OrdinalIgnoreCase))
        {
            _rows.Add(row);
        }

        if (previous is not null && _rows.Contains(previous))
            CardGrid.SelectedItem = previous;
        else if (_rows.Count > 0)
            CardGrid.SelectedIndex = 0;

        int duplicateGroups = _allRows.Where(row => row.IsDuplicate)
            .Select(row => row.GroupKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        int marked = _allRows.Count(row => row.Delete);
        SummaryText.Text = $"Visible: {_rows.Count:N0}  •  card XML: {_allRows.Count:N0}  •  duplicate groups: {duplicateGroups:N0}  •  marked: {marked:N0}";
    }

    private void MarkDuplicates_Click(object sender, RoutedEventArgs e)
    {
        int marked = 0;
        foreach (IGrouping<string, CardCleanupRow> group in _allRows
                     .Where(row => row.IsDuplicate)
                     .GroupBy(row => row.GroupKey!, StringComparer.OrdinalIgnoreCase))
        {
            CardCleanupRow[] ordered = group.OrderBy(row => row.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
            for (int index = 0; index < ordered.Length; index++)
            {
                ordered[index].Delete = index > 0;
                if (index > 0) marked++;
            }
        }
        ApplyFilter();
        if (marked == 0)
        {
            MessageBox.Show(this, "No exact duplicate card XML groups were found.", "No duplicates",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void ClearMarks_Click(object sender, RoutedEventArgs e)
    {
        foreach (CardCleanupRow row in _allRows) row.Delete = false;
        ApplyFilter();
    }

    private async void CardGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        await UpdatePreviewAsync(CardGrid.SelectedItem as CardCleanupRow);

    private async Task UpdatePreviewAsync(CardCleanupRow? row)
    {
        int version = ++_previewVersion;
        PreviewImage.Source = null;
        PreviewPlaceholder.Visibility = Visibility.Visible;
        PreviewPlaceholder.Text = "No preview";

        if (row is null)
        {
            PreviewTitle.Text = "Select a card";
            PreviewSubtitle.Text = string.Empty;
            PreviewInfo.Text = string.Empty;
            PreviewPath.Text = string.Empty;
            return;
        }

        PreviewTitle.Text = string.IsNullOrWhiteSpace(row.DisplayName) ? row.FileName : row.DisplayName;
        PreviewSubtitle.Text = row.IsDuplicate ? $"Exact duplicate group {row.GroupNumber}" : "Card XML";
        PreviewInfo.Text = string.Join("\n", new[]
        {
            string.IsNullOrWhiteSpace(row.EnglishName) ? null : $"English: {row.EnglishName}",
            string.IsNullOrWhiteSpace(row.TypeLine) ? null : $"Type: {row.TypeLine}",
            string.IsNullOrWhiteSpace(row.Expansion) ? null : $"Set: {row.Expansion}",
            $"Size: {FormatBytes(row.Length)}"
        }.Where(value => value is not null));
        PreviewPath.Text = row.RelativePath;

        if (_imageLoader is null || row.Card is null || string.IsNullOrWhiteSpace(row.Card.ImageId))
        {
            PreviewPlaceholder.Text = "Card art unavailable";
            return;
        }

        try
        {
            CardImageData? image = await _imageLoader.LoadAsync(row.Card.ImageId, GameImageKind.Illustration);
            if (version != _previewVersion || image is null)
            {
                if (version == _previewVersion) PreviewPlaceholder.Text = "Preview not available";
                return;
            }

            BitmapSource bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32,
                null, image.BgraPixels, checked(image.Width * 4));
            bitmap.Freeze();
            PreviewImage.Source = bitmap;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
        }
        catch
        {
            if (version == _previewVersion) PreviewPlaceholder.Text = "Preview not available";
        }
    }

    private async void Rescan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        CardCleanupRow[] selected = _allRows.Where(row => row.Delete).ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show(this, "Mark one or more card XML files first.", "Nothing selected",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        MessageBoxResult confirmation = MessageBox.Show(this,
            $"Delete {selected.Length:N0} card XML file(s)?\n\n" +
            "This removes only the selected loose card definitions. Related TDX art and other resources are not removed automatically.\n" +
            "Files are sent to the Windows Recycle Bin.",
            "Confirm Card Cleanup", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes) return;

        IsEnabled = false;
        try
        {
            List<string> failures = new();
            await Task.Run(() =>
            {
                foreach (CardCleanupRow row in selected)
                {
                    try
                    {
                        FileSystem.DeleteFile(row.FullPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                    }
                    catch (Exception exception)
                    {
                        failures.Add($"{row.RelativePath}: {exception.Message}");
                    }
                }
            });

            if (failures.Count > 0)
            {
                MessageBox.Show(this, $"Some files could not be deleted:\n\n{string.Join("\n", failures.Take(12))}",
                    "Card Cleanup completed with errors", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            await ScanAsync();
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }

    private sealed record FileCandidate(string Path, long Length);
    private sealed record DuplicateInfo(string GroupKey, int GroupNumber);

    private sealed class CardCleanupRow : INotifyPropertyChanged
    {
        private bool _delete;

        public CardCleanupRow(string? groupKey, int? groupNumber, string fullPath, string relativePath, long length, CardRecord? card)
        {
            GroupKey = groupKey;
            GroupNumber = groupNumber;
            FullPath = fullPath;
            RelativePath = relativePath;
            Length = length;
            Card = card;
        }

        public string? GroupKey { get; }
        public int? GroupNumber { get; }
        public bool IsDuplicate => GroupNumber.HasValue;
        public string GroupText => GroupNumber?.ToString() ?? string.Empty;
        public string FullPath { get; }
        public string RelativePath { get; }
        public long Length { get; }
        public CardRecord? Card { get; }
        public string FileName => Path.GetFileName(FullPath);
        public string DisplayName => Card?.LocalizedName ?? Path.GetFileNameWithoutExtension(FullPath) ?? string.Empty;
        public string EnglishName => Card?.EnglishName ?? string.Empty;
        public string TypeLine => Card?.TypeLine ?? string.Empty;
        public string Expansion => Card?.Expansion ?? string.Empty;

        public bool Delete
        {
            get => _delete;
            set
            {
                if (_delete == value) return;
                _delete = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Delete)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
