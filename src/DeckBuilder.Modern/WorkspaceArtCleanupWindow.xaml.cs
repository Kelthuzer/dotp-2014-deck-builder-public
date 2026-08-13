using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeckBuilder.GameData;
using Microsoft.VisualBasic.FileIO;

namespace DeckBuilder.Modern;

public partial class WorkspaceArtCleanupWindow : Window
{
    private readonly string _workspaceDirectory;
    private readonly GameCardImageLoader? _imageLoader;
    private readonly ObservableCollection<ArtDuplicateRow> _rows = new();
    private IReadOnlyList<ArtDuplicateRow> _allRows = Array.Empty<ArtDuplicateRow>();
    private int _previewVersion;

    public WorkspaceArtCleanupWindow(string workspaceDirectory, GameCardImageLoader? imageLoader = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceDirectory);
        _workspaceDirectory = Path.GetFullPath(workspaceDirectory);
        _imageLoader = imageLoader;
        InitializeComponent();
        ArtGrid.ItemsSource = _rows;
        Loaded += async (_, _) => await ScanAsync();
    }

    private async Task ScanAsync()
    {
        IsEnabled = false;
        SummaryText.Text = "Scanning TDX art for exact duplicates…";
        try
        {
            _allRows = await Task.Run(() => FindDuplicates(_workspaceDirectory));
            ApplyFilter();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Art cleanup scan failed", MessageBoxButton.OK, MessageBoxImage.Error);
            SummaryText.Text = "Scan failed.";
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private static IReadOnlyList<ArtDuplicateRow> FindDuplicates(string root)
    {
        List<FileCandidate> candidates = Directory
            .EnumerateFiles(root, "*.tdx", System.IO.SearchOption.AllDirectories)
            .Where(path => path.Replace('/', '\\').Contains("\\DATA_ALL_PLATFORMS\\ART_ASSETS\\", StringComparison.OrdinalIgnoreCase))
            .Select(path => new FileCandidate(path, new FileInfo(path).Length))
            .ToList();

        List<ArtDuplicateRow> result = new();
        int groupNumber = 1;

        foreach (IGrouping<long, FileCandidate> sizeGroup in candidates
                     .GroupBy(candidate => candidate.Length)
                     .Where(group => group.Count() > 1)
                     .OrderBy(group => group.Key))
        {
            Dictionary<string, List<FileCandidate>> hashes = new(StringComparer.OrdinalIgnoreCase);
            foreach (FileCandidate candidate in sizeGroup)
            {
                string hash = ComputeSha256(candidate.Path);
                if (!hashes.TryGetValue(hash, out List<FileCandidate>? same))
                {
                    same = new List<FileCandidate>();
                    hashes.Add(hash, same);
                }
                same.Add(candidate);
            }

            foreach (List<FileCandidate> duplicateGroup in hashes.Values
                         .Where(group => group.Count > 1)
                         .OrderBy(group => group[0].Path, StringComparer.OrdinalIgnoreCase))
            {
                string groupKey = $"Art:{groupNumber}";
                foreach (FileCandidate candidate in duplicateGroup.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
                {
                    string relative = Path.GetRelativePath(root, candidate.Path);
                    result.Add(new ArtDuplicateRow(
                        groupKey,
                        groupNumber,
                        candidate.Path,
                        relative,
                        candidate.Length));
                }
                groupNumber++;
            }
        }

        return result;
    }

    private static string ComputeSha256(string path)
    {
        using FileStream input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private void Filter_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (!IsLoaded)
        {
            return;
        }

        string query = SearchBox.Text.Trim();
        IEnumerable<ArtDuplicateRow> visible = _allRows;
        if (query.Length > 0)
        {
            visible = visible.Where(row =>
                row.FileName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || row.Folder.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || row.RelativePath.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || row.GroupNumber.ToString().Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        ArtDuplicateRow? previous = ArtGrid.SelectedItem as ArtDuplicateRow;
        _rows.Clear();
        foreach (ArtDuplicateRow row in visible
                     .OrderBy(row => row.GroupNumber)
                     .ThenBy(row => row.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            _rows.Add(row);
        }

        if (previous is not null && _rows.Contains(previous))
        {
            ArtGrid.SelectedItem = previous;
        }
        else if (_rows.Count > 0)
        {
            ArtGrid.SelectedIndex = 0;
        }

        int groups = _allRows.Select(row => row.GroupKey).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int marked = _allRows.Count(row => row.Delete);
        long reclaimable = _allRows
            .GroupBy(row => row.GroupKey, StringComparer.OrdinalIgnoreCase)
            .Sum(group => group.Skip(1).Sum(row => row.Length));
        SummaryText.Text = $"Duplicate groups: {groups:N0}  •  files: {_allRows.Count:N0}  •  marked: {marked:N0}  •  reclaimable: {FormatBytes(reclaimable)}";
    }

    private void MarkDuplicates_Click(object sender, RoutedEventArgs e)
    {
        int marked = 0;
        foreach (IGrouping<string, ArtDuplicateRow> group in _allRows.GroupBy(row => row.GroupKey, StringComparer.OrdinalIgnoreCase))
        {
            ArtDuplicateRow[] ordered = group.OrderBy(row => row.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
            for (int index = 0; index < ordered.Length; index++)
            {
                ordered[index].Delete = index > 0;
                if (index > 0)
                {
                    marked++;
                }
            }
        }

        ApplyFilter();
        if (marked == 0)
        {
            MessageBox.Show(this, "No exact duplicate TDX files were found.", "No duplicate art",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void ClearMarks_Click(object sender, RoutedEventArgs e)
    {
        foreach (ArtDuplicateRow row in _allRows)
        {
            row.Delete = false;
        }
        ApplyFilter();
    }

    private async void Rescan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private async void ArtGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        await UpdatePreviewAsync(ArtGrid.SelectedItem as ArtDuplicateRow);

    private async Task UpdatePreviewAsync(ArtDuplicateRow? row)
    {
        int version = ++_previewVersion;
        PreviewImage.Source = null;
        PreviewPlaceholder.Visibility = Visibility.Visible;
        PreviewPlaceholder.Text = "No preview";

        if (row is null)
        {
            PreviewTitle.Text = "Select duplicate art";
            PreviewSubtitle.Text = string.Empty;
            PreviewInfo.Text = string.Empty;
            PreviewPath.Text = string.Empty;
            return;
        }

        PreviewTitle.Text = row.FileName;
        PreviewSubtitle.Text = $"Duplicate group {row.GroupNumber}  •  {row.Folder}";
        int copies = _allRows.Count(candidate => candidate.GroupKey.Equals(row.GroupKey, StringComparison.OrdinalIgnoreCase));
        PreviewInfo.Text = $"Copies: {copies}\nSize per copy: {row.SizeText}";
        PreviewPath.Text = row.RelativePath;

        if (_imageLoader is null)
        {
            PreviewPlaceholder.Text = "Preview loader unavailable";
            return;
        }

        string imageId = Path.GetFileNameWithoutExtension(row.FullPath);
        GameImageKind kind = InferArtKind(row.RelativePath);
        try
        {
            CardImageData? image = await _imageLoader.LoadAsync(imageId, kind);
            if (version != _previewVersion || image is null)
            {
                if (version == _previewVersion)
                {
                    PreviewPlaceholder.Text = "Preview not available";
                }
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
            PreviewImage.Source = bitmap;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
        }
        catch
        {
            if (version == _previewVersion)
            {
                PreviewPlaceholder.Text = "Preview not available";
            }
        }
    }

    private static GameImageKind InferArtKind(string relativePath)
    {
        string normalized = relativePath.Replace('/', '\\');
        if (normalized.Contains("\\ILLUSTRATION\\", StringComparison.OrdinalIgnoreCase)) return GameImageKind.Illustration;
        if (normalized.Contains("\\FRAME\\", StringComparison.OrdinalIgnoreCase)) return GameImageKind.Frame;
        if (normalized.Contains("\\MANA\\", StringComparison.OrdinalIgnoreCase)) return GameImageKind.Mana;
        if (normalized.Contains("\\DECKS\\", StringComparison.OrdinalIgnoreCase)) return GameImageKind.Deck;
        if (normalized.Contains("\\PERSONALITY\\", StringComparison.OrdinalIgnoreCase)) return GameImageKind.Personality;
        return GameImageKind.Texture;
    }

    private async void DeleteMarked_Click(object sender, RoutedEventArgs e)
    {
        ArtDuplicateRow[] selected = _allRows.Where(row => row.Delete).ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show(this, "Mark one or more duplicate art files first.", "Nothing selected",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        foreach (IGrouping<string, ArtDuplicateRow> group in _allRows.GroupBy(row => row.GroupKey, StringComparer.OrdinalIgnoreCase))
        {
            if (group.All(row => row.Delete))
            {
                MessageBox.Show(this,
                    $"Duplicate group {group.First().GroupNumber} has every copy marked. Keep at least one copy.",
                    "One copy must remain", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        long bytes = selected.Sum(row => row.Length);
        MessageBoxResult confirmation = MessageBox.Show(this,
            $"Send {selected.Length:N0} duplicate TDX file(s) ({FormatBytes(bytes)}) to the Windows Recycle Bin?\n\n" +
            "At least one byte-identical copy is preserved in every duplicate group.",
            "Confirm Art Cleanup", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        IsEnabled = false;
        try
        {
            List<string> failures = new();
            await Task.Run(() =>
            {
                foreach (ArtDuplicateRow row in selected)
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
                MessageBox.Show(this,
                    "Some art files could not be deleted:\n\n" + string.Join("\n", failures.Take(12)),
                    "Art Cleanup completed with errors", MessageBoxButton.OK, MessageBoxImage.Warning);
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

    private sealed class ArtDuplicateRow : INotifyPropertyChanged
    {
        private bool _delete;

        public ArtDuplicateRow(string groupKey, int groupNumber, string fullPath, string relativePath, long length)
        {
            GroupKey = groupKey;
            GroupNumber = groupNumber;
            FullPath = fullPath;
            RelativePath = relativePath;
            Length = length;
        }

        public string GroupKey { get; }
        public int GroupNumber { get; }
        public string FullPath { get; }
        public string RelativePath { get; }
        public long Length { get; }
        public string FileName => Path.GetFileName(FullPath);
        public string Folder => Path.GetFileName(Path.GetDirectoryName(FullPath)) ?? string.Empty;
        public string SizeText => FormatBytes(Length);

        public bool Delete
        {
            get => _delete;
            set
            {
                if (_delete == value)
                {
                    return;
                }
                _delete = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
