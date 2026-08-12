using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace DeckBuilder.Modern;

public partial class MainWindow
{
    private bool _cardReferenceScannerMenuInstalled;

    internal void InstallCardReferenceScannerMenu()
    {
        if (_cardReferenceScannerMenuInstalled || Content is not DockPanel root)
            return;

        Menu? menu = root.Children.OfType<Menu>().FirstOrDefault();
        MenuItem? gameData = menu?.Items.OfType<MenuItem>()
            .FirstOrDefault(item => (item.Header?.ToString() ?? string.Empty)
                .Replace("_", string.Empty, StringComparison.Ordinal)
                .Equals("Game data", StringComparison.OrdinalIgnoreCase));
        if (gameData is null)
            return;

        gameData.Items.Add(new Separator());
        MenuItem scanner = new() { Header = "Card _reference scanner…" };
        scanner.Click += CardReferenceScanner_Click;
        gameData.Items.Add(scanner);
        _cardReferenceScannerMenuInstalled = true;
    }

    private void CardReferenceScanner_Click(object sender, RoutedEventArgs e)
    {
        string? workspaceRoot = !string.IsNullOrWhiteSpace(_workspaceDirectory)
            && Directory.Exists(_workspaceDirectory)
                ? _workspaceDirectory
                : _gameDirectory;

        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
        {
            MessageBox.Show(this,
                "Load an unpacked workspace or Magic 2014 folder first.",
                "Game data required",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        CardReferenceScannerWindow window = new(workspaceRoot) { Owner = this };
        window.Show();
    }
}
