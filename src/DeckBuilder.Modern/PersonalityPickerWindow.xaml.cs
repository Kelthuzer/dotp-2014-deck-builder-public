using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeckBuilder.GameData;

namespace DeckBuilder.Modern;

public partial class PersonalityPickerWindow : Window
{
    private readonly IReadOnlyList<InstalledPersonalityRecord> _personalities;
    private readonly GameCardImageLoader? _imageLoader;
    private int _previewVersion;

    public InstalledPersonalityRecord? SelectedPersonality { get; private set; }

    public PersonalityPickerWindow(
        IReadOnlyList<InstalledPersonalityRecord> personalities,
        GameCardImageLoader? imageLoader,
        string? currentFileName = null)
    {
        _personalities = personalities ?? throw new ArgumentNullException(nameof(personalities));
        _imageLoader = imageLoader;
        InitializeComponent();
        ApplyFilter();

        if (!string.IsNullOrWhiteSpace(currentFileName))
        {
            InstalledPersonalityRecord? current = _personalities.FirstOrDefault(personality =>
                personality.FileName.Equals(currentFileName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (current is not null)
            {
                PersonalityGrid.SelectedItem = current;
                PersonalityGrid.ScrollIntoView(current);
            }
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (PersonalityGrid is null || SearchBox is null)
        {
            return;
        }

        string query = SearchBox.Text.Trim();
        IReadOnlyList<InstalledPersonalityRecord> visible = string.IsNullOrWhiteSpace(query)
            ? _personalities
            : _personalities.Where(personality =>
                    personality.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                    || personality.FileName.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || personality.NameTag.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || personality.Source.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToArray();

        PersonalityGrid.ItemsSource = visible;
        StatusText.Text = $"{visible.Count:N0} of {_personalities.Count:N0} personalities";
    }

    private async void PersonalityGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        InstalledPersonalityRecord? personality = PersonalityGrid.SelectedItem as InstalledPersonalityRecord;
        ChooseButton.IsEnabled = personality is not null;
        PreviewImage.Source = null;
        DetailsText.Text = personality is null
            ? string.Empty
            : $"File: {personality.FileName}\nName tag: {personality.NameTag}\nMusic: {personality.Music}\nSource: {personality.Source}";

        if (personality is null)
        {
            PreviewMessage.Text = "Select a personality";
            PreviewMessage.Visibility = Visibility.Visible;
            return;
        }

        string imageId = !string.IsNullOrWhiteSpace(personality.LargeAvatarImage)
            ? personality.LargeAvatarImage
            : personality.SmallAvatarImage;
        if (_imageLoader is null || string.IsNullOrWhiteSpace(imageId))
        {
            PreviewMessage.Text = "No avatar preview is available";
            PreviewMessage.Visibility = Visibility.Visible;
            return;
        }

        int version = ++_previewVersion;
        PreviewMessage.Text = "Loading avatar…";
        PreviewMessage.Visibility = Visibility.Visible;
        try
        {
            CardImageData? image = await _imageLoader.LoadAsync(imageId, GameImageKind.Personality);
            if (version != _previewVersion)
            {
                return;
            }

            if (image is null)
            {
                PreviewMessage.Text = $"Avatar {imageId} was not found";
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

    private void PersonalityGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PersonalityGrid.SelectedItem is InstalledPersonalityRecord)
        {
            ChooseCurrent();
        }
    }

    private void Choose_Click(object sender, RoutedEventArgs e) => ChooseCurrent();

    private void ChooseCurrent()
    {
        if (PersonalityGrid.SelectedItem is not InstalledPersonalityRecord personality)
        {
            return;
        }

        SelectedPersonality = personality;
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
