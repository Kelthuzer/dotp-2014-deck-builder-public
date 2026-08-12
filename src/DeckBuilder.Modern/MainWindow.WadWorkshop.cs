using System.IO;
using System.Windows;
using DeckBuilder.Core.Services;
using DeckBuilder.GameData;

namespace DeckBuilder.Modern;

public partial class MainWindow
{
    private async void WadWorkshop_Click(object sender, RoutedEventArgs e)
    {
        string? gameDirectory = _gameDirectory;
        if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory))
        {
            MessageBox.Show(
                "Load the Magic 2014 folder first.",
                "Game data required",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        WadWorkshopWindow dialog = new(gameDirectory) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedDeck is null)
        {
            return;
        }

        if (_dirty && MessageBox.Show(
                "Opening this deck from WAD Workshop will replace the current unsaved project. Continue?",
                "Unsaved work",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        InstalledDeckRecord selected = dialog.SelectedDeck;
        int slot;
        try
        {
            slot = await Task.Run(() => ModernWadExporter.SuggestSlot(gameDirectory, -1));
        }
        catch (Exception exception)
        {
            ShowError("Could not choose a free deck UID", exception);
            return;
        }

        int uid = slot >= 0 ? int.Parse($"1000{slot:00}") : -1;
        string projectName = $"Copy of {selected.DisplayName}";
        _deck = DeckDocumentCloner.Clone(selected.Deck, uid, projectName);
        _editor = new DeckEditor(_deck);
        _projectName = projectName;
        _projectPath = null;
        MergeDeckCardsIntoCatalog();
        SetDirty(true);
        RefreshCollections();
        Status($"Opened {selected.DisplayName} from {selected.Source} through WAD Workshop.");
    }
}
