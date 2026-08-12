using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeckBuilder.GameData;

namespace DeckBuilder.Modern;

public partial class WorkspaceVariantResolverWindow : Window
{
    private readonly List<ConflictRow> _conflicts;
    private readonly Dictionary<string, string> _selections = new(StringComparer.OrdinalIgnoreCase);
    private int _previewVersion;

    public IReadOnlyDictionary<string, string> Selections => _selections;

    public WorkspaceVariantResolverWindow(WorkspaceContentVariantScanResult scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        InitializeComponent();
        _conflicts = scan.Conflicts.Select(conflict => new ConflictRow(conflict)).ToList();
        ConflictsGrid.ItemsSource = _conflicts;
        Title = $"Resolve {scan.Kind} variants — {scan.Conflicts.Count:N0} conflict(s)";
        UpdateStatus();
        if (_conflicts.Count > 0)
        {
            ConflictsGrid.SelectedIndex = 0;
        }
    }

    private void ConflictsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConflictsGrid.SelectedItem is not ConflictRow row)
        {
            VariantsGrid.ItemsSource = null;
            ConflictTitle.Text = "Select a conflict";
            ClearDetails();
            return;
        }

        ConflictTitle.Text = $"{row.DisplayName} — {row.Conflict.RelativePath}";
        VariantRow[] variants = row.Conflict.Variants
            .Select(variant => new VariantRow(variant))
            .ToArray();
        VariantsGrid.ItemsSource = variants;

        string preferredKey = _selections.TryGetValue(row.Conflict.ConflictKey, out string? selected)
            ? selected
            : row.Conflict.RecommendedSelectionKey;
        VariantRow? preferred = variants.FirstOrDefault(item =>
            item.Variant.SelectionKey.Equals(preferredKey, StringComparison.Ordinal));
        VariantsGrid.SelectedItem = preferred ?? variants.FirstOrDefault();
        if (VariantsGrid.SelectedItem is not null)
        {
            VariantsGrid.ScrollIntoView(VariantsGrid.SelectedItem);
        }
    }

    private async void VariantsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VariantsGrid.SelectedItem is not VariantRow row)
        {
            UseVariantButton.IsEnabled = false;
            ClearDetails();
            return;
        }

        UseVariantButton.IsEnabled = true;
        WorkspaceContentVariant variant = row.Variant;
        DetailName.Text = variant.DisplayName;
        DetailReference.Text = variant.Reference;
        DetailStats.Text = string.Join("   ", new[]
        {
            variant.CastingCost,
            variant.TypeLine,
            variant.PowerToughness
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
        DetailExpansion.Text = string.IsNullOrWhiteSpace(variant.Expansion)
            ? string.Empty
            : $"Set: {variant.Expansion}   Artist: {variant.Artist}";
        DetailArt.Text = string.IsNullOrWhiteSpace(variant.ArtId)
            ? "ARTID: —"
            : $"ARTID: {variant.ArtId}";
        DetailSource.Text = $"Source: {variant.PackageName} / {variant.WadName} / order {variant.WadOrder}";
        DetailRules.Text = variant.RulesText;
        DetailPath.Text = $"{variant.RelativePath}\n{variant.StoragePath}\nSHA256 {variant.Sha256}";

        int previewVersion = ++_previewVersion;
        PreviewImage.Source = null;
        string? artPath = variant.ArtStoragePath;
        if (string.IsNullOrWhiteSpace(artPath) || !File.Exists(artPath))
        {
            PreviewMessage.Text = string.IsNullOrWhiteSpace(variant.ArtId)
                ? "No ARTID in this definition"
                : $"Art {variant.ArtId} was not found in extracted packages";
            PreviewMessage.Visibility = Visibility.Visible;
            return;
        }

        PreviewMessage.Text = "Loading art preview…";
        PreviewMessage.Visibility = Visibility.Visible;
        try
        {
            CardImageData image = await TdxFileImageLoader.LoadAsync(artPath);
            if (previewVersion != _previewVersion)
            {
                return;
            }

            PreviewImage.Source = CreateBitmapSource(image);
            PreviewMessage.Visibility = Visibility.Collapsed;
        }
        catch (Exception exception)
        {
            if (previewVersion == _previewVersion)
            {
                PreviewMessage.Text = $"Art preview failed:\n{exception.Message}";
                PreviewMessage.Visibility = Visibility.Visible;
            }
        }
    }

    private void VariantsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (VariantsGrid.SelectedItem is VariantRow)
        {
            UseCurrentVariant();
        }
    }

    private void UseVariant_Click(object sender, RoutedEventArgs e) => UseCurrentVariant();

    private void UseCurrentVariant()
    {
        if (ConflictsGrid.SelectedItem is not ConflictRow conflictRow
            || VariantsGrid.SelectedItem is not VariantRow variantRow)
        {
            return;
        }

        ApplySelection(conflictRow, variantRow.Variant);
        UpdateStatus();

        ConflictRow? next = _conflicts.FirstOrDefault(row => row.Status != "Chosen");
        if (next is not null)
        {
            ConflictsGrid.SelectedItem = next;
            ConflictsGrid.ScrollIntoView(next);
        }
    }

    private void UseSuggestedAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (ConflictRow row in _conflicts.Where(row => row.Status != "Chosen"))
        {
            WorkspaceContentVariant? suggested = row.Conflict.Variants.FirstOrDefault(variant =>
                variant.SelectionKey.Equals(row.Conflict.RecommendedSelectionKey, StringComparison.Ordinal));
            if (suggested is not null)
            {
                ApplySelection(row, suggested);
            }
        }

        UpdateStatus();
    }

    private void ApplySelection(ConflictRow row, WorkspaceContentVariant variant)
    {
        _selections[row.Conflict.ConflictKey] = variant.SelectionKey;

        // A card choice is a coherent definition+art choice. If the same ARTID has different TDX
        // payloads across extracted versions, pass the matching art source to the WAD builder too.
        if (!string.IsNullOrWhiteSpace(variant.ArtSelectionIdentity)
            && !string.IsNullOrWhiteSpace(variant.ArtSelectionKey))
        {
            _selections[variant.ArtSelectionIdentity] = variant.ArtSelectionKey;
        }

        row.Status = "Chosen";
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (_conflicts.Any(row => row.Status != "Chosen"))
        {
            MessageBox.Show(this,
                "Choose a variant for every differing card/deck, or explicitly use the suggested variants for the unresolved items.",
                "Variant selection incomplete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void UpdateStatus()
    {
        int resolved = _conflicts.Count(row => row.Status == "Chosen");
        int remaining = _conflicts.Count - resolved;
        StatusText.Text = $"Resolved: {resolved:N0} / {_conflicts.Count:N0}; remaining: {remaining:N0}";
        ContinueButton.IsEnabled = remaining == 0;
    }

    private void ClearDetails()
    {
        ++_previewVersion;
        PreviewImage.Source = null;
        PreviewMessage.Text = "Select a variant";
        PreviewMessage.Visibility = Visibility.Visible;
        DetailName.Text = string.Empty;
        DetailReference.Text = string.Empty;
        DetailStats.Text = string.Empty;
        DetailExpansion.Text = string.Empty;
        DetailArt.Text = string.Empty;
        DetailSource.Text = string.Empty;
        DetailRules.Text = string.Empty;
        DetailPath.Text = string.Empty;
    }

    private static BitmapSource CreateBitmapSource(CardImageData image)
    {
        BitmapSource source = BitmapSource.Create(
            image.Width,
            image.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            image.BgraPixels,
            checked(image.Width * 4));
        source.Freeze();
        return source;
    }

    private sealed class ConflictRow : INotifyPropertyChanged
    {
        private string _status = "Needs choice";

        public ConflictRow(WorkspaceContentVariantConflict conflict)
        {
            Conflict = conflict;
        }

        public WorkspaceContentVariantConflict Conflict { get; }
        public string DisplayName => Conflict.DisplayName;
        public int VariantCount => Conflict.VariantCount;

        public string Status
        {
            get => _status;
            set
            {
                if (_status == value)
                {
                    return;
                }

                _status = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed record VariantRow(WorkspaceContentVariant Variant)
    {
        public string SuggestedText => Variant.IsRecommended ? "Yes" : string.Empty;
    }
}
