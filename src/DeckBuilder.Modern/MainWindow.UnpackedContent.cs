using System.IO;
using System.Windows;

namespace DeckBuilder.Modern;

public partial class MainWindow
{
    private void BuildFromUnpackedContent_Click(object sender, RoutedEventArgs e)
    {
        string? initialOutput = !string.IsNullOrWhiteSpace(_gameDirectory) && Directory.Exists(_gameDirectory)
            ? _gameDirectory
            : null;

        UnpackedContentBuilderWindow dialog = new(initialSource: null, initialOutput: initialOutput)
        {
            Owner = this
        };
        dialog.ShowDialog();
    }
}
