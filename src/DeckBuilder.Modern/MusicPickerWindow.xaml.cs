using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DeckBuilder.Modern;

public partial class MusicPickerWindow : Window
{
    private readonly IReadOnlyList<string> _allMusic;

    public string SelectedMusic { get; private set; } = string.Empty;

    public MusicPickerWindow(IReadOnlyList<string> music, string? currentMusic = null)
    {
        _allMusic = music ?? throw new ArgumentNullException(nameof(music));
        InitializeComponent();
        ApplyFilter();

        if (!string.IsNullOrWhiteSpace(currentMusic))
        {
            string? match = _allMusic.FirstOrDefault(item =>
                item.Equals(currentMusic.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                MusicList.SelectedItem = match;
                MusicList.ScrollIntoView(match);
            }
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (MusicList is null || SearchBox is null)
        {
            return;
        }

        string query = SearchBox.Text.Trim();
        IReadOnlyList<string> visible = string.IsNullOrWhiteSpace(query)
            ? _allMusic
            : _allMusic.Where(item => item.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        MusicList.ItemsSource = visible;
        StatusText.Text = $"{visible.Count:N0} of {_allMusic.Count:N0} music mixes";
    }

    private void MusicList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ChooseButton.IsEnabled = MusicList.SelectedItem is string;

    private void MusicList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (MusicList.SelectedItem is string)
        {
            ChooseCurrent();
        }
    }

    private void Choose_Click(object sender, RoutedEventArgs e) => ChooseCurrent();

    private void ChooseCurrent()
    {
        if (MusicList.SelectedItem is not string value)
        {
            return;
        }

        SelectedMusic = value;
        DialogResult = true;
    }
}
