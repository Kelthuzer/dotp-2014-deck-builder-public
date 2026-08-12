using System.Windows;

namespace DeckBuilder.Modern;

public partial class MainWindow
{
    private void DeckInformation_Click(object sender, RoutedEventArgs e)
    {
        string previousName = _deck.Name;
        DeckInformationWindow dialog = new(_deck, _cardImageLoader, _gameDirectory) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        dialog.ApplyTo(_deck);
        if (string.IsNullOrWhiteSpace(_projectName)
            || _projectName.Equals("Untitled deck", StringComparison.Ordinal)
            || _projectName.Equals(previousName, StringComparison.Ordinal))
        {
            _projectName = _deck.Name;
        }

        SetDirty(true);
        Status($"Deck information updated: {_deck.Name}.");
    }
}
