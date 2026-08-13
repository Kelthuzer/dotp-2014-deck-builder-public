using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using DeckBuilder.GameData;
using Microsoft.VisualBasic.FileIO;

namespace DeckBuilder.Modern;

public partial class WorkspaceDuplicateCleanupWindow : Window
{
    private readonly string _workspaceDirectory;
    private readonly IReadOnlyList<InstalledDeckRecord> _decks;
    private readonly ObservableCollection<CleanupFileRow> _rows = new();
    private IReadOnlyList<CleanupFileRow> _allRows = Array.Empty<CleanupFileRow>();

    public WorkspaceDuplicateCleanupWindow(
        string workspaceDirectory,
        IReadOnlyList<InstalledDeckRecord>? decks = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceDirectory);
        _workspaceDirectory = Path.GetFullPath(workspaceDirectory);
        _decks = decks ?? Array.Empty<InstalledDeckRecord>();
        InitializeComponent();
        DuplicateGrid.ItemsSource = _rows;
        Loaded += async (_, _) => await ScanAsync();
    }

    private async Task ScanAsync()
    {
        IsEnabled = false;
        SummaryText.Text = "Scanning workspace decks and exact duplicates…";
        try
        {
            _allRows = await Task.Run(() => FindFiles(_workspaceDirectory, _decks));
            ApplyFilter();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Deck cleanup scan failed", MessageBoxButton.OK, MessageBoxImage.Error);
            SummaryText.Text = "Scan failed.";
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private static IReadOnlyList<CleanupFileRow> FindFiles(
        string root,
        IReadOnlyList<InstalledDeckRecord> decks)
    {
        List<FileCandidate> deckCandidates = new();
        List<FileCandidate> artCandidates = new();

        foreach (string path in Directory.EnumerateFiles(root, "*", System.IO.SearchOption.AllDirectories))
        {
            string normalized = path.Replace('/', '\\');
            string extension = Path.GetExtension(path);
            if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase)
                && normalized.Contains("\\DATA_ALL_PLATFORMS\\DECKS\\", StringComparison.OrdinalIgnoreCase))
            {
                FileInfo info = new(path);
                deckCandidates.Add(new FileCandidate(path, "Deck", info.Length));
            }
            else if (extension.Equals(".tdx", StringComparison.OrdinalIgnoreCase)
                     && normalized.Contains("\\DATA_ALL_PLATFORMS\\ART_ASSETS\\", StringComparison.OrdinalIgnoreCase))
            {
                FileInfo info = new(path);
                artCandidates.Add(new FileCandidate(path, "Art", info.Length));
            }
        }

        Dictionary<string, DuplicateInfo> duplicateInfo = BuildDuplicateInfo(
            deckCandidates.Concat(artCandidates).ToArray());

        List<CleanupFileRow> result = new();
        foreach (FileCandidate candidate in deckCandidates.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
        {
            duplicateInfo.TryGetValue(candidate.Path, out DuplicateInfo? duplicate);
            InstalledDeckRecord? deck = MatchDeck(root, candidate.Path, decks);
            result.Add(new CleanupFileRow(
                duplicate?.GroupKey,
                duplicate?.GroupNumber,
                candidate.Category,
                candidate.Path,
                Path.GetRelativePath(root, candidate.Path),
                candidate.Length,
                deck?.FriendlyName ?? string.Empty,
                deck?.Uid,
                deck?.CardCount));
        }

        foreach (FileCandidate candidate in artCandidates
                     .Where(candidate => duplicateInfo.ContainsKey(candidate.Path))
                     .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
        {
            DuplicateInfo duplicate = duplicateInfo[candidate.Path];
            result.Add(new CleanupFileRow(
                duplicate.GroupKey,
                duplicate.GroupNumber,
                candidate.Category,
                candidate.Path,
                Path.GetRelativePath(root, candidate.Path),
                candidate.Length,
                string.Empty,
                null,
                null));
        }

        return result;
    }

    private static Dictionary<string, DuplicateInfo> BuildDuplicateInfo(IReadOnlyList<FileCandidate> candidates)
    {
        Dictionary<string, DuplicateInfo> result = new(StringComparer.OrdinalIgnoreCase);
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
                foreach (FileCandidate candidate in exactGroup)
                {
                    result[candidate.Path] = new DuplicateInfo(groupKey, groupNumber);
                }
                groupNumber++;
            }
        }

        return result;
    }

    private static InstalledDeckRecord? MatchDeck(
        string workspaceRoot,
        string deckPath,
        IReadOnlyList<InstalledDeckRecord> decks)
    {
        string technicalName = Path.GetFileNameWithoutExtension(deckPath)?.ToUpperInvariant() ?? string.Empty;
        InstalledDeckRecord[] matches = decks
            .Where(deck => deck.TechnicalName.Equals(technicalName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 0)
        {
            return null;
        }
        if (matches.Length == 1)
        {
            return matches[0];
        }

        string relative = Path.GetRelativePath(workspaceRoot, deckPath).Replace('/', '\\');
        InstalledDeckRecord[] sourceMatches = matches
            .Where(deck => PathContainsSource(relative, deck.Source))
            .ToArray();
        return sourceMatches.Length == 1 ? sourceMatches[0] : matches[0];
    }

    private static bool PathContainsSource(string relativePath, string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        string normalizedSource = Path.GetFileNameWithoutExtension(source)?.Trim() ?? source.Trim();
        return relativePath.StartsWith(normalizedSource + "\\", StringComparison.OrdinalIgnoreCase)
            || relativePath.Contains("\\" + normalizedSource + "\\", StringComparison.OrdinalIgnoreCase);
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

        string mode = (CategoryBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "AllDecks";
        string query = SearchBox.Text.Trim();
        IEnumerable<CleanupFileRow> visible = mode switch
        {
            "DuplicateDecks" => _allRows.Where(row => row.Category == "Deck" && row.IsDuplicate),
            "DuplicateArt" => _allRows.Where(row => row.Category == "Art" && row.IsDuplicate),
            _ => _allRows.Where(row => row.Category == "Deck")
        };

        if (query.Length > 0)
        {
            visible = visible.Where(row =>
                row.FriendlyName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || row.FileName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || row.RelativePath.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || row.UidText.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        _rows.Clear();
        foreach (CleanupFileRow row in visible)
        {
            _rows.Add(row);
        }

        int marked = _allRows.Count(row => row.Delete);
        int duplicateDeckGroups = _allRows
            .Where(row => row.Category == "Deck" && row.IsDuplicate)
            .Select(row => row.GroupKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        int duplicateArtGroups = _allRows
            .Where(row => row.Category == "Art" && row.IsDuplicate)
            .Select(row => row.GroupKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        SummaryText.Text = $"Visible: {_rows.Count:N0}  •  decks: {_allRows.Count(row => row.Category == "Deck"):N0}  •  duplicate deck groups: {duplicateDeckGroups:N0}  •  duplicate art groups: {duplicateArtGroups:N0}  •  marked: {marked:N0}";
    }

    private void MarkDuplicates_Click(object sender, RoutedEventArgs e)
    {
        foreach (IGrouping<string, CleanupFileRow> group in VisibleRows()
                     .Where(row => row.IsDuplicate)
                     .GroupBy(row => row.GroupKey!, StringComparer.OrdinalIgnoreCase))
        {
            bool keep = true;
            foreach (CleanupFileRow row in group.OrderBy(row => row.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                row.Delete = !keep;
                keep = false;
            }
        }
        ApplyFilter();
    }

    private void ClearMarks_Click(object sender, RoutedEventArgs e)
    {
        foreach (CleanupFileRow row in _allRows)
        {
            row.Delete = false;
        }
        ApplyFilter();
    }

    private IEnumerable<CleanupFileRow> VisibleRows() => _rows.ToArray();

    private async void Rescan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        CleanupFileRow[] selected = _allRows.Where(row => row.Delete).ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show(this, "Mark one or more files first.", "Nothing selected",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        foreach (IGrouping<string, CleanupFileRow> group in _allRows
                     .Where(row => row.Category == "Art" && row.IsDuplicate)
                     .GroupBy(row => row.GroupKey!, StringComparer.OrdinalIgnoreCase))
        {
            if (group.All(row => row.Delete))
            {
                MessageBox.Show(this,
                    $"Duplicate art group {group.First().GroupNumber} has every copy marked. Keep at least one copy of duplicate art.",
                    "One art copy must remain", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        int deckCount = selected.Count(row => row.Category == "Deck");
        int artCount = selected.Count(row => row.Category == "Art");
        long bytes = selected.Sum(row => row.Length);
        MessageBoxResult confirmation = MessageBox.Show(this,
            $"Delete {selected.Length:N0} marked file(s) ({FormatBytes(bytes)})?\n\n" +
            $"Deck XML: {deckCount:N0}\nTDX art: {artCount:N0}\n\n" +
            "Any marked deck may be removed, even if it is unique or contains only a few cards.\n" +
            "Related scripts and other resources are not removed automatically.\n" +
            "Files are sent to the Windows Recycle Bin.",
            "Confirm Deck Cleanup", MessageBoxButton.YesNo, MessageBoxImage.Warning);
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
                foreach (CleanupFileRow row in selected)
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
                    "Deck Cleanup completed with errors", MessageBoxButton.OK, MessageBoxImage.Warning);
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
    private sealed record DuplicateInfo(string GroupKey, int GroupNumber);

    private sealed class CleanupFileRow : INotifyPropertyChanged
    {
        private bool _delete;

        public CleanupFileRow(
            string? groupKey,
            int? groupNumber,
            string category,
            string fullPath,
            string relativePath,
            long length,
            string friendlyName,
            int? uid,
            int? cardCount)
        {
            GroupKey = groupKey;
            GroupNumber = groupNumber;
            Category = category;
            FullPath = fullPath;
            RelativePath = relativePath;
            Length = length;
            FriendlyName = friendlyName;
            Uid = uid;
            CardCount = cardCount;
        }

        public string? GroupKey { get; }
        public int? GroupNumber { get; }
        public bool IsDuplicate => GroupNumber.HasValue;
        public string GroupText => GroupNumber?.ToString() ?? string.Empty;
        public string Category { get; }
        public string FullPath { get; }
        public string RelativePath { get; }
        public long Length { get; }
        public string FriendlyName { get; }
        public int? Uid { get; }
        public int? CardCount { get; }
        public string UidText => Uid?.ToString() ?? string.Empty;
        public string CardsText => CardCount?.ToString() ?? string.Empty;
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
