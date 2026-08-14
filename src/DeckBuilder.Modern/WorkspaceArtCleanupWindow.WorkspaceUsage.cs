using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using DeckBuilder.Core.Models;
using DeckBuilder.GameData;

namespace DeckBuilder.Modern;

public partial class WorkspaceArtCleanupWindow
{
    public WorkspaceArtCleanupWindow(string workspaceDirectory, GameCardImageLoader? imageLoader)
        : this(workspaceDirectory, ReadWorkspaceCardUsage(workspaceDirectory), imageLoader)
    {
        // Art Cleanup is a file-level tool. Preview the exact selected TDX instead of asking
        // GameCardImageLoader to resolve an ID through its package-precedence index. The same
        // image ID can exist in several game-version packages, and the indexed copy may not be
        // the row the user selected.
        ArtGrid.SelectionChanged -= ArtGrid_SelectionChanged;
        ArtGrid.SelectionChanged += ArtGrid_DirectPreviewSelectionChanged;
    }

    private async void ArtGrid_DirectPreviewSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ArtCleanupRow? row = ArtGrid.SelectedItem as ArtCleanupRow;
        int version = ++_previewVersion;

        PreviewImage.Source = null;
        PreviewPlaceholder.Visibility = Visibility.Visible;
        PreviewPlaceholder.Text = "No preview";

        if (row is null)
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
            ? _allRows.Count(candidate => string.Equals(candidate.GroupKey, row.GroupKey, StringComparison.OrdinalIgnoreCase))
            : 1;
        PreviewInfo.Text = $"Kind: {row.Kind}\nCopies: {copies}\nSize: {row.SizeText}";
        PreviewUsage.Text = row.Kind == GameImageKind.Illustration
            ? row.IsUnusedIllustration
                ? "Usage: no workspace card references this illustration ID."
                : "Usage: referenced by a workspace card."
            : "Usage: not automatically classified. Shared textures may be referenced outside card ARTID fields.";
        PreviewPath.Text = row.RelativePath;

        try
        {
            CardImageData? image = await TdxPreviewLoader.LoadFileAsync(row.FullPath);
            if (version != _previewVersion)
                return;

            if (image is null)
            {
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
        catch (Exception exception)
        {
            if (version == _previewVersion)
            {
                PreviewPlaceholder.Text = $"Preview failed: {exception.Message}";
            }
        }
    }

    private static IReadOnlyList<CardRecord> ReadWorkspaceCardUsage(string workspaceDirectory)
    {
        List<CardRecord> cards = new();
        if (!Directory.Exists(workspaceDirectory))
            return cards;

        foreach (string path in Directory.EnumerateFiles(workspaceDirectory, "*.xml", System.IO.SearchOption.AllDirectories))
        {
            string normalized = path.Replace('/', '\\');
            if (!normalized.Contains("\\DATA_ALL_PLATFORMS\\CARDS\\", StringComparison.OrdinalIgnoreCase)
                && !normalized.Contains("\\CARDS\\", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                XDocument document = XDocument.Load(path, LoadOptions.None);
                XElement? card = document.Root?.Name.LocalName.Equals("CARD_V2", StringComparison.OrdinalIgnoreCase) == true
                    ? document.Root
                    : document.Descendants().FirstOrDefault(element =>
                        element.Name.LocalName.Equals("CARD_V2", StringComparison.OrdinalIgnoreCase));
                if (card is null)
                    continue;

                string fileName = ChildAttribute(card, "FILENAME", "text");
                string artId = ChildAttribute(card, "ARTID", "value");
                if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(artId))
                    continue;

                cards.Add(new CardRecord(
                    fileName,
                    fileName,
                    fileName,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    path,
                    artId,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    false));
            }
            catch
            {
                // Usage analysis is best-effort; one malformed XML must not block the cleanup window.
            }
        }

        return cards;
    }

    private static string ChildAttribute(XElement parent, string childName, string attributeName)
    {
        XElement? child = parent.Elements().FirstOrDefault(element =>
            element.Name.LocalName.Equals(childName, StringComparison.OrdinalIgnoreCase));
        return child?.Attributes().FirstOrDefault(attribute =>
            attribute.Name.LocalName.Equals(attributeName, StringComparison.OrdinalIgnoreCase))?.Value.Trim()
            ?? string.Empty;
    }
}
