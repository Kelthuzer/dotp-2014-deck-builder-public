using System.IO;
using System.Windows;
using System.Windows.Controls;
using DeckBuilder.Core.Models;
using DeckBuilder.Core.Services;
using DeckBuilder.GameData;

namespace DeckBuilder.Modern;

public partial class PersonalityEditorWindow : Window
{
    private readonly GameCardImageLoader? _imageLoader;

    public AiPersonalityDefinition Result { get; private set; }

    public PersonalityEditorWindow(AiPersonalityDefinition personality, GameCardImageLoader? imageLoader)
    {
        ArgumentNullException.ThrowIfNull(personality);
        _imageLoader = imageLoader;
        Result = personality.Clone();
        InitializeComponent();

        DisplayNameBox.Text = Result.DisplayName;
        FileNameBox.Text = Result.FileName;
        NameTagBox.Text = Result.NameTag;
        LargeAvatarBox.Text = Result.LargeAvatarImage;
        SmallAvatarBox.Text = Result.SmallAvatarImage;
        LockedAvatarBox.Text = Result.SmallAvatarLockedImage;
        LobbyImageBox.Text = Result.LobbyImage;
        MusicBox.Text = Result.Music;
    }

    private void Regenerate_Click(object sender, RoutedEventArgs e)
    {
        string code = AiPersonalityXmlSerializer.Codify(DisplayNameBox.Text);
        FileNameBox.Text = $"D14_PERSONALITY_{code}_CUSTOM.XML";
        NameTagBox.Text = $"PLAYER_NAME_{code}_CUSTOM";
    }

    private void LargeAvatarBrowse_Click(object sender, RoutedEventArgs e) =>
        PickImage(LargeAvatarBox, "Choose full personality image");

    private void SmallAvatarBrowse_Click(object sender, RoutedEventArgs e) =>
        PickImage(SmallAvatarBox, "Choose small personality image");

    private void LockedAvatarBrowse_Click(object sender, RoutedEventArgs e) =>
        PickImage(LockedAvatarBox, "Choose locked personality image");

    private void LobbyImageBrowse_Click(object sender, RoutedEventArgs e) =>
        PickImage(LobbyImageBox, "Choose personality lobby/backplate image");

    private void PickImage(TextBox target, string title)
    {
        if (_imageLoader is null)
        {
            MessageBox.Show(this,
                "Load the Magic 2014 game folder before browsing personality images.",
                "Game data required",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        GameImagePickerWindow dialog = new(_imageLoader, GameImageKind.Personality, title, target.Text)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            target.Text = dialog.SelectedImageId;
        }
    }

    private void MusicBrowse_Click(object sender, RoutedEventArgs e)
    {
        string? gameDirectory = _imageLoader?.GameDirectory;
        if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory))
        {
            MessageBox.Show(this,
                "Load the Magic 2014 game folder before browsing music mixes.",
                "Game data required",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        IReadOnlyList<string> music = GameMusicCatalogLoader.Load(gameDirectory);
        if (music.Count == 0)
        {
            MessageBox.Show(this,
                "No MP3 music mixes were found in Audio\\Music.",
                "No music mixes",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        MusicPickerWindow dialog = new(music, MusicBox.Text) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            MusicBox.Text = dialog.SelectedMusic;
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        string displayName = DisplayNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(displayName))
        {
            MessageBox.Show(this,
                "Enter a personality name before applying the changes.",
                "Personality name required",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            DisplayNameBox.Focus();
            return;
        }

        Result = AiPersonalityXmlSerializer.NormalizeIdentifiers(new AiPersonalityDefinition
        {
            DisplayName = displayName,
            FileName = FileNameBox.Text.Trim(),
            NameTag = NameTagBox.Text.Trim(),
            LargeAvatarImage = LargeAvatarBox.Text.Trim(),
            SmallAvatarImage = SmallAvatarBox.Text.Trim(),
            SmallAvatarLockedImage = LockedAvatarBox.Text.Trim(),
            LobbyImage = LobbyImageBox.Text.Trim(),
            Music = MusicBox.Text.Trim()
        });
        DialogResult = true;
    }
}
