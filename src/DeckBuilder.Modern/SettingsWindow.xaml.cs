using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace DeckBuilder.Modern;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        LanguageBox.SelectedValue = AppSettingsService.Current.Language;
        ThemeBox.SelectedValue = AppSettingsService.Current.Theme;
        SmoothPreviewTransitionsBox.IsChecked = AppSettingsService.Current.SmoothPreviewTransitions;
        GameDirectoryBox.Text = AppSettingsService.Current.GameDirectory;
        WorkspaceDirectoryBox.Text = AppSettingsService.Current.WorkspaceDirectory;
        WadOutputDirectoryBox.Text = AppSettingsService.Current.WadOutputDirectory;
        AppLocalization.Apply(this);
        AppThemeService.ApplyCurrent();
    }

    private void BrowseGameDirectory_Click(object sender, RoutedEventArgs e) =>
        BrowseDirectory(GameDirectoryBox, "Выберите папку Magic 2014");

    private void BrowseWorkspaceDirectory_Click(object sender, RoutedEventArgs e) =>
        BrowseDirectory(WorkspaceDirectoryBox, "Выберите корень распакованного workspace");

    private void BrowseWadOutputDirectory_Click(object sender, RoutedEventArgs e) =>
        BrowseDirectory(WadOutputDirectoryBox, "Выберите папку для готовых WAD");

    private void BrowseDirectory(TextBox target, string title)
    {
        OpenFolderDialog dialog = new()
        {
            Title = title,
            Multiselect = false,
            InitialDirectory = Directory.Exists(target.Text) ? target.Text : null
        };
        if (dialog.ShowDialog(this) == true)
            target.Text = dialog.FolderName;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        string language = (LanguageBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "ru";
        string theme = (ThemeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "system";

        string gameDirectory = NormalizePath(GameDirectoryBox.Text);
        string workspaceDirectory = NormalizePath(WorkspaceDirectoryBox.Text);
        string wadOutputDirectory = NormalizePath(WadOutputDirectoryBox.Text);

        if (!ValidateExistingDirectory(gameDirectory, "Папка Magic 2014")
            || !ValidateExistingDirectory(workspaceDirectory, "Распакованный workspace"))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(wadOutputDirectory))
        {
            try
            {
                Directory.CreateDirectory(wadOutputDirectory);
            }
            catch (Exception exception)
            {
                MessageBox.Show(this,
                    $"Не удалось создать/открыть папку для WAD:\n{wadOutputDirectory}\n\n{exception.Message}",
                    "Неверный путь",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }
        }

        AppSettingsService.Current.Language = AppSettingsService.NormalizeLanguage(language);
        AppSettingsService.Current.Theme = AppSettingsService.NormalizeTheme(theme);
        AppSettingsService.Current.SmoothPreviewTransitions = SmoothPreviewTransitionsBox.IsChecked != false;
        AppSettingsService.Current.GameDirectory = gameDirectory;
        AppSettingsService.Current.WorkspaceDirectory = workspaceDirectory;
        AppSettingsService.Current.WadOutputDirectory = wadOutputDirectory;
        AppSettingsService.Save();

        AppLocalization.ApplyToOpenWindows();
        AppThemeService.ApplyCurrent();
        DialogResult = true;
    }

    private static string NormalizePath(string? value) =>
        AppSettingsService.NormalizeDirectory(value);

    private bool ValidateExistingDirectory(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || Directory.Exists(path))
            return true;

        MessageBox.Show(this,
            $"{label} не существует:\n{path}",
            "Неверный путь",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        return false;
    }
}
