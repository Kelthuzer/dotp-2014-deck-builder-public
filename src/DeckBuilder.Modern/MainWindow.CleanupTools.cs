using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace DeckBuilder.Modern;

public partial class MainWindow
{
    private bool _cleanupToolsMenuInstalled;

    internal void InstallCleanupToolsMenu()
    {
        if (_cleanupToolsMenuInstalled || Content is not DockPanel root)
        {
            return;
        }

        Menu? menu = root.Children.OfType<Menu>().FirstOrDefault();
        if (menu is null)
        {
            return;
        }

        MenuItem cleanup = new() { Header = "_Cleanup" };

        MenuItem decks = new() { Header = "Cleanup _decks…" };
        decks.Click += CleanupDecksMenu_Click;
        cleanup.Items.Add(decks);

        MenuItem cards = new() { Header = "Cleanup _cards…" };
        cards.Click += CleanupCardsMenu_Click;
        cleanup.Items.Add(cards);

        MenuItem art = new() { Header = "Cleanup duplicate _art…" };
        art.Click += CleanupArtMenu_Click;
        cleanup.Items.Add(art);

        MenuItem? help = menu.Items.OfType<MenuItem>()
            .FirstOrDefault(item => (item.Header?.ToString() ?? string.Empty)
                .Replace("_", string.Empty, StringComparison.Ordinal)
                .Equals("Help", StringComparison.OrdinalIgnoreCase));
        int index = help is null ? menu.Items.Count : menu.Items.IndexOf(help);
        menu.Items.Insert(index, cleanup);
        _cleanupToolsMenuInstalled = true;
    }

    private bool TryGetCleanupWorkspace(out string workspaceDirectory)
    {
        workspaceDirectory = _workspaceDirectory ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(workspaceDirectory) && Directory.Exists(workspaceDirectory))
        {
            return true;
        }

        MessageBox.Show(
            this,
            "Load an unpacked workspace first. Cleanup tools only edit loose workspace files and never modify packed game WADs.",
            "Workspace required",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        return false;
    }

    private void CleanupDecksMenu_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetCleanupWorkspace(out string workspaceDirectory))
        {
            return;
        }

        WorkspaceDuplicateCleanupWindow dialog = new(
            workspaceDirectory,
            _installedDecks,
            _cardImageLoader)
        {
            Owner = this
        };
        dialog.ShowDialog();
    }

    private void CleanupCardsMenu_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetCleanupWorkspace(out string workspaceDirectory))
        {
            return;
        }

        WorkspaceCardCleanupWindow dialog = new(
            workspaceDirectory,
            _catalog,
            _installedDecks,
            _cardImageLoader)
        {
            Owner = this
        };
        dialog.ShowDialog();
    }

    private void CleanupArtMenu_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetCleanupWorkspace(out string workspaceDirectory))
        {
            return;
        }

        WorkspaceArtCleanupWindow dialog = new(workspaceDirectory, _cardImageLoader)
        {
            Owner = this
        };
        dialog.ShowDialog();
    }
}
