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
        if (menu is null)
            return;

        RemoveLegacyPathMenuItems(menu);

        MenuItem? fileMenu = FindTopLevelMenu(menu, "File", "Файл");
        if (fileMenu is null)
            return;

        int exitIndex = -1;
        for (int i = 0; i < fileMenu.Items.Count; i++)
        {
            if (fileMenu.Items[i] is not MenuItem item)
                continue;

            string header = NormalizeMenuHeader(item.Header);
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

    private static void RemoveLegacyPathMenuItems(Menu menu)
    {
        MenuItem? gameDataMenu = FindTopLevelMenu(menu, "Game data", "Данные игры");
        if (gameDataMenu is null)
            return;

        string[] obsoleteHeaders =
        [
            "Load Magic 2014 folder…",
            "Reload current folder",
            "Load unpacked workspace…",
            "Reload unpacked workspace",
            "Загрузить папку Magic 2014…",
            "Перезагрузить текущую папку",
            "Загрузить распакованный workspace…",
            "Перезагрузить распакованный workspace"
        ];

        for (int i = gameDataMenu.Items.Count - 1; i >= 0; i--)
        {
            if (gameDataMenu.Items[i] is not MenuItem item)
                continue;

            string header = NormalizeMenuHeader(item.Header);
            if (obsoleteHeaders.Any(value => header.Equals(value, StringComparison.OrdinalIgnoreCase)))
                gameDataMenu.Items.RemoveAt(i);
        }

        RemoveRedundantSeparators(gameDataMenu);
    }

    private static MenuItem? FindTopLevelMenu(Menu menu, params string[] names) =>
        menu.Items
            .OfType<MenuItem>()
            .FirstOrDefault(item => names.Any(name =>
                NormalizeMenuHeader(item.Header).Equals(name, StringComparison.OrdinalIgnoreCase)));

    private static string NormalizeMenuHeader(object? header) =>
        (header?.ToString() ?? string.Empty)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Trim();

    private static void RemoveRedundantSeparators(MenuItem menu)
    {
        for (int i = menu.Items.Count - 1; i >= 0; i--)
        {
            if (menu.Items[i] is not Separator)
                continue;

            bool atStart = i == 0;
            bool atEnd = i == menu.Items.Count - 1;
            bool nextIsSeparator = !atEnd && menu.Items[i + 1] is Separator;
            if (atStart || atEnd || nextIsSeparator)
                menu.Items.RemoveAt(i);
        }
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        SettingsWindow dialog = new() { Owner = this };
        bool applied = dialog.ShowDialog() == true;
        AppLocalization.Apply(this);
        if (!applied)
            return;

        Status("Применяю пути из настроек…");
        await ReloadConfiguredDataPathsAsync();
        Status("Настройки применены.");
    }
}
