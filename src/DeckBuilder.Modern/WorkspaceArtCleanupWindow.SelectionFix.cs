using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeckBuilder.GameData;

namespace DeckBuilder.Modern;

public partial class WorkspaceArtCleanupWindow
{
    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        // The XAML handler used the old preview routine. It counted copies by calling
        // candidate.GroupKey.Equals(...), but non-duplicate rows have a null GroupKey.
        // With the new all-TDX view that caused a NullReferenceException whenever a
        // duplicate row was selected. Replace that handler with the null-safe version.
        ArtGrid.SelectionChanged -= ArtGrid_SelectionChanged;
        ArtGrid.SelectionChanged += ArtGrid_SelectionChanged_NullSafe;
    }

    private async void ArtGrid_SelectionChanged_NullSafe(object sender, SelectionChangedEventArgs e)
    {
        int version = ++_previewVersion;
        PreviewImage.Source = null;
        PreviewPlaceholder.Visibility = Visibility.Visible;
        PreviewPlaceholder.Text = "No preview";

        if (ArtGrid.SelectedItem is not ArtCleanupRow row)
        {
            PreviewTitle.Text = "Select art";
            PreviewSubtitle.Text = string.Empty;
            PreviewInfo.Text = string.Empty;
            PreviewUsage.Text = string.Empty;
            PreviewPath.Text = string.Empty;
            return;
        }

        PreviewTitle.Text = row.FileName;
        PreviewSubtitle.Text = row.IsDuplicate
            ? $"{row.UsageStatus}  •  duplicate group {row.GroupNumber}  •  {row.Folder}"
            : $"{row.UsageStatus}  •  {row.Folder}";

        int copies = row.IsDuplicate
            ? _allRows.Count(candidate => string.Equals(
                candidate.GroupKey,
                row.GroupKey,
                StringComparison.OrdinalIgnoreCase))
            : 1;

        PreviewInfo.Text = $"Kind: {row.Kind}\nCopies: {copies}\nSize: {row.SizeText}";
        PreviewUsage.Text = row.Kind == GameImageKind.Illustration
            ? row.IsUnusedIllustration
                ? "Usage: no workspace card references this illustration ID."
                : "Usage: referenced by a workspace card."
            : "Usage: not automatically classified. Frames, mana symbols, deck textures and shared UI textures may be referenced outside card ImageId fields.";
        PreviewPath.Text = row.RelativePath;

        if (_imageLoader is null)
        {
            PreviewPlaceholder.Text = "Preview loader unavailable";
            return;
        }

        try
        {
            CardImageData? image = await _imageLoader.LoadAsync(row.ImageId, row.Kind);
            if (version != _previewVersion || image is null)
            {
                if (version == _previewVersion)
                    PreviewPlaceholder.Text = "Preview not available";
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
                PreviewPlaceholder.Text = "Preview not available";
        }
    }
}
