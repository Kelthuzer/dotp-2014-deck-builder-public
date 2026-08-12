using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeckBuilder.GameData;

namespace DeckBuilder.Modern;

public partial class GameImagePickerWindow : Window
{
    private readonly GameCardImageLoader _loader;
    private readonly GameImageKind _kind;
    private readonly Func<string, bool>? _idFilter;
    private IReadOnlyList<string> _allIds = Array.Empty<string>();
    private int _previewVersion;

    public string SelectedImageId { get; private set; } = string.Empty;

    public GameImagePickerWindow(
        GameCardImageLoader loader,
        GameImageKind kind,
        string title,
        string? currentImageId = null,
        Func<string, bool>? idFilter = null)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _kind = kind;
        _idFilter = idFilter;
        InitializeComponent();
        Title = title;
        SelectedImageId = currentImageId?.Trim() ?? string.Empty;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        IsEnabled = false;
        StatusText.Text = "Indexing game images…";
        try
        {
            IReadOnlyList<string> indexed = await _loader.GetImageIdsAsync(_kind);
            _allIds = _idFilter is null
                ? indexed
                : indexed.Where(_idFilter).ToArray();
            ApplyFilter();

            if (!string.IsNullOrWhiteSpace(SelectedImageId))
            {
                string? match = _allIds.FirstOrDefault(id =>
                    id.Equals(SelectedImageId, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    ImageList.SelectedItem = match;
                    ImageList.ScrollIntoView(match);
                }
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(this,
                $"Could not index game images.\n\n{exception.Message}",
                "Game image picker",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            DialogResult = false;
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (ImageList is null || SearchBox is null)
        {
            return;
        }

        string query = SearchBox.Text.Trim();
        IReadOnlyList<string> visible = string.IsNullOrWhiteSpace(query)
            ? _allIds
            : _allIds.Where(id => id.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();

        ImageList.ItemsSource = visible;
        StatusText.Text = $"{visible.Count:N0} of {_allIds.Count:N0} images";
    }

    private async void ImageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        string? id = ImageList.SelectedItem as string;
        ChooseButton.IsEnabled = !string.IsNullOrWhiteSpace(id);
        PreviewImage.Source = null;

        if (string.IsNullOrWhiteSpace(id))
        {
            PreviewMessage.Text = "Select an image";
            PreviewMessage.Visibility = Visibility.Visible;
            return;
        }

        int version = ++_previewVersion;
        PreviewMessage.Text = "Loading preview…";
        PreviewMessage.Visibility = Visibility.Visible;
        try
        {
            CardImageData? image = await _loader.LoadAsync(id, _kind);
            if (version != _previewVersion)
            {
                return;
            }

            if (image is null)
            {
                PreviewMessage.Text = "Image data was not found";
                return;
            }

            PreviewImage.Source = CreateBitmapSource(image);
            PreviewMessage.Visibility = Visibility.Collapsed;
        }
        catch (Exception exception)
        {
            if (version == _previewVersion)
            {
                PreviewMessage.Text = $"Preview failed:\n{exception.Message}";
                PreviewMessage.Visibility = Visibility.Visible;
            }
        }
    }

    private void ImageList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ImageList.SelectedItem is string)
        {
            ChooseCurrent();
        }
    }

    private void Choose_Click(object sender, RoutedEventArgs e) => ChooseCurrent();

    private void ChooseCurrent()
    {
        if (ImageList.SelectedItem is not string id || string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        SelectedImageId = id;
        DialogResult = true;
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
}
