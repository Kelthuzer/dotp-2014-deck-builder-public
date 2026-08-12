using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DeckBuilder.GameData;

namespace DeckBuilder.Modern;

public partial class ExistingDeckPickerWindow : Window
{
    private readonly IReadOnlyList<InstalledDeckRecord> _allDecks;

    public ExistingDeckPickerWindow(IReadOnlyList<InstalledDeckRecord> decks)
    {
        ArgumentNullException.ThrowIfNull(decks);
        _allDecks = decks
            .Where(deck => !deck.Source.Contains("HideOfficialDecks", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        InitializeComponent();
        ShowResults();
    }

    public ObservableCollection<InstalledDeckRecord> Decks { get; } = new();

    public InstalledDeckRecord? SelectedDeck { get; private set; }

    public string NewDeckName => NewNameBox.Text.Trim();

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ShowResults();

    private void ShowResults()
    {
        string query = SearchBox.Text.Trim();
        IEnumerable<InstalledDeckRecord> matches = string.IsNullOrWhiteSpace(query)
            ? _allDecks
            : _allDecks.Where(deck =>
                deck.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || deck.FileName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || deck.Source.Contains(query, StringComparison.OrdinalIgnoreCase)
                || deck.Uid.ToString().Contains(query, StringComparison.OrdinalIgnoreCase));

        Decks.Clear();
        foreach (InstalledDeckRecord deck in matches)
        {
            Decks.Add(deck);
        }

        DeckGrid.ItemsSource = Decks;
        ResultCountText.Text = $"Decks: {Decks.Count:N0}";
        if (Decks.Count > 0)
        {
            DeckGrid.SelectedIndex = 0;
        }
    }

    private void DeckGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        InstalledDeckRecord? selected = DeckGrid.SelectedItem as InstalledDeckRecord;
        CreateButton.IsEnabled = selected is not null;
        if (selected is not null)
        {
            NewNameBox.Text = $"{selected.DisplayName} Copy";
            NewNameBox.SelectAll();
        }
    }

    private void DeckGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DeckGrid.SelectedItem is InstalledDeckRecord)
        {
            Create_Click(sender, e);
        }
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (DeckGrid.SelectedItem is not InstalledDeckRecord selected)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(NewDeckName))
        {
            MessageBox.Show(this, "Enter a name for the new deck.", "Deck name required",
                MessageBoxButton.OK, MessageBoxImage.Information);
            NewNameBox.Focus();
            return;
        }

        SelectedDeck = selected;
        DialogResult = true;
    }
}
