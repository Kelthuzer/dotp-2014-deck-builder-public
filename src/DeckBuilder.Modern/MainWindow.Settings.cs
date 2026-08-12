using System.Windows;
using System.Windows.Controls;

namespace DeckBuilder.Modern;

public partial class MainWindow
{
    private bool _settingsMenuInstalled;

    internal void InstallSettingsMenu()
    {
        if (_settingsMenuInstalled || Content is not DockPanel root)
            return;

        Menu? menu = root.Children.OfType<Menu>().FirstOrDefault();
        MenuItem? fileMenu = menu?.Items.OfType<MenuItem>().FirstOrDefault(item =>
        {
            string header = (item.Header?.ToString() ?? string.Empty).Replace("_", string.Empty, StringComparison.Ordinal);
            return header.Equals("File", StringComparison.OrdinalIgnoreCase)
                   || header.Equals("Файл", StringComparison.OrdinalIgnoreCase);
        });
        if (fileMenu is null)
            return;

        int exitIndex = -1;
        for (int i = 0; i < fileMenu.Items.Count; i++)
        {
            if (fileMenu.Items[i] is not MenuItem item)
                continue;
            string header = (item.Header?.ToString() ?? string.Empty).Replace("_", string.Empty, StringComparison.Ordinal);
            if (header.Equals("Exit", StringComparison.OrdinalIgnoreCase)
                || header.Equals("Выход", StringComparison.OrdinalIgnoreCase))
            {
                exitIndex = i;
                break;
            }
        }

        MenuItem settings = new() { Header = "Settings…" };
        settings.Click += Settings_Click;
        if (exitIndex >= 0)
        {
            fileMenu.Items.Insert(exitIndex, settings);
            fileMenu.Items.Insert(exitIndex, new Separator());
        }
        else
        {
            fileMenu.Items.Add(new Separator());
            fileMenu.Items.Add(settings);
        }

        _settingsMenuInstalled = true;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        SettingsWindow dialog = new() { Owner = this };
        dialog.ShowDialog();
        AppLocalization.Apply(this);
    }
}
