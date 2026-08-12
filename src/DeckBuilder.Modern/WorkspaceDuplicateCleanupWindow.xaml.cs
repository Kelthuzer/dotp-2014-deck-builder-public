using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualBasic.FileIO;

namespace DeckBuilder.Modern;

public partial class WorkspaceDuplicateCleanupWindow : Window
{
    private readonly string _workspaceDirectory;
    private readonly ObservableCollection<DuplicateFileRow> _rows = new();
    private IReadOnlyList<DuplicateFileRow> _allRows = Array.Empty<DuplicateFileRow>();

    public WorkspaceDuplicateCleanupWindow(string workspaceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceDirectory);
        _workspaceDirectory = Path.GetFullPath(workspaceDirectory);
        InitializeComponent();
        DuplicateGrid.ItemsSource = _rows;
        Loaded += async (_, _) => await ScanAsync();
    }

    private async Task ScanAsync()
    {
        IsEnabled = false;
        SummaryText.Text = "Scanning exact duplicate deck XML and TDX art…";
        try
        {
            _allRows = await Task.Run(() => FindDuplicates(_workspaceDirectory));
            ApplyFilter();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Duplicate scan failed", MessageBoxButton.OK, MessageBoxImage.Error);
            SummaryText.Text = "Scan failed.";
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private static IReadOnlyList<DuplicateFileRow> FindDuplicates(string root)
    {
        List<FileCandidate> candidates = new();
        foreach (string path in Directory.EnumerateFiles(root, "*", System.IO.SearchOption.AllDirectories))
        {
            string normalized = path.Replace('/', '\\');
            string extension = Path.GetExtension(path);
            string? category = null;

            if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase)
                && normalized.Contains("\\DATA_ALL_PLATFORMS\\DECKS\\", StringComparison.OrdinalIgnoreCase))
            {
                category = "Deck";
            }
            else if (extension.Equals(".tdx", StringComparison.OrdinalIgnoreCase)
                     && normalized.Contains("\\DATA_ALL_PLATFORMS\\ART_ASSETS\\", StringComparison.OrdinalIgnoreCase))
            {
                category = "Art";
            }

            if (category is null)
            {
                continue;
            }

            FileInfo info = new(path);
            candidates.Add(new FileCandidate(path, category, info.Length));
        }

        List<DuplicateFileRow> result = new();
        int groupNumber = 1;
        foreach (IGrouping<(string Category, long Length), FileCandidate> sizeGroup in candidates
                     .GroupBy(candidate => (candidate.Category, candidate.Length))
                     .Where(group => group.Count() > 1)
                     .OrderBy(group => group.Key.Category, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(group => group.Key.Length))
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

            foreach (List<FileCandidate> exactGroup in hashes.Values
                         .Where(group => group.Count > 1)
                         .OrderBy(group => group[0].Path, StringComparer.OrdinalIgnoreCase))
            {
                string groupKey = $"{exactGroup[0].Category}:{groupNumber}";
                foreach (FileCandidate candidate in exactGroup.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(new DuplicateFileRow(
                        groupKey,
                        groupNumber,
                        candidate.Category,
                        candidate.Path,
                        Path.GetRelativePath(root, candidate.Path),
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

    private void Filter_Changed(object sender, EventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (!IsLoaded)
        {
            return;
        }

        string category = (CategoryBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "All";
        string query = SearchBox.Text.Trim();
        IEnumerable<DuplicateFileRow> visible = _allRows;
        if (!category.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            visible = visible.Where(row => row.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
        }
        if (query.Length > 0)
        {
            visible = visible.Where(row =>
                row.FileName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || row.RelativePath.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        }

        _rows.Clear();
        foreach (DuplicateFileRow row in visible)
        {
            _rows.Add(row);
        }

        int groups = _allRows.Select(row => row.GroupKey).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int marked = _allRows.Count(row => row.Delete);
        SummaryText.Text = $"Exact duplicate groups: {groups:N0}  •  files: {_allRows.Count:N0}  •  marked for deletion: {marked:N0}";
    }

    private void MarkDuplicates_Click(object sender, RoutedEventArgs e)
    {
        foreach (IGrouping<string, DuplicateFileRow> group in VisibleRows().GroupBy(row => row.GroupKey, StringComparer.OrdinalIgnoreCase))
        {
            bool keep = true;
            foreach (DuplicateFileRow row in group.OrderBy(row => row.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                row.Delete = !keep;
                keep = false;
            }
        }
        ApplyFilter();
    }

    private void ClearMarks_Click(object sender, RoutedEventArgs e)
    {
        foreach (DuplicateFileRow row in _allRows)
        {
            row.Delete = false;
        }
        ApplyFilter();
    }

    private IEnumerable<DuplicateFileRow> VisibleRows() => _rows.ToArray();

    private async void Rescan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        DuplicateFileRow[] selected = _allRows.Where(row => row.Delete).ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show(this, "Mark one or more duplicate files first.", "Nothing selected",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        foreach (IGrouping<string, DuplicateFileRow> group in _allRows.GroupBy(row => row.GroupKey, StringComparer.OrdinalIgnoreCase))
        {
            if (group.All(row => row.Delete))
            {
                MessageBox.Show(this,
                    $"Group {group.First().GroupNumber} has every copy marked. Keep at least one copy in each duplicate group.",
                    "One copy must remain", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        long bytes = selected.Sum(row => row.Length);
        MessageBoxResult confirmation = MessageBox.Show(this,
            $"Delete {selected.Length:N0} selected duplicate file(s) ({FormatBytes(bytes)})?\n\n" +
            "Files are sent to the Windows Recycle Bin, not permanently erased.\n" +
            "The builder will rescan the workspace afterwards.",
            "Confirm duplicate cleanup", MessageBoxButton.YesNo, MessageBoxImage.Warning);
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
                foreach (DuplicateFileRow row in selected)
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
                    $"Some files could not be deleted:\n\n{string.Join("\n", failures.Take(12))}",
                    "Cleanup completed with errors", MessageBoxButton.OK, MessageBoxImage.Warning);
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

    private sealed record FileCandidate(string Path, string Category, long Length);

    private sealed class DuplicateFileRow : INotifyPropertyChanged
    {
        private bool _delete;

        public DuplicateFileRow(string groupKey, int groupNumber, string category, string fullPath, string relativePath, long length)
        {
            GroupKey = groupKey;
            GroupNumber = groupNumber;
            Category = category;
            FullPath = fullPath;
            RelativePath = relativePath;
            Length = length;
        }

        public string GroupKey { get; }
        public int GroupNumber { get; }
        public string Category { get; }
        public string FullPath { get; }
        public string RelativePath { get; }
        public long Length { get; }
        public string FileName => Path.GetFileName(FullPath);
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
