using System.Windows;
using System.Windows.Controls;

namespace DeckBuilder.Modern;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        LanguageBox.SelectedValue = AppSettingsService.Current.Language;
        ThemeBox.SelectedValue = AppSettingsService.Current.Theme;
        SmoothPreviewTransitionsBox.IsChecked = AppSettingsService.Current.SmoothPreviewTransitions;
        AppLocalization.Apply(this);
        AppThemeService.ApplyCurrent();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        string language = (LanguageBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "ru";
        string theme = (ThemeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "system";

        AppSettingsService.Current.Language = AppSettingsService.NormalizeLanguage(language);
        AppSettingsService.Current.Theme = AppSettingsService.NormalizeTheme(theme);
        AppSettingsService.Current.SmoothPreviewTransitions = SmoothPreviewTransitionsBox.IsChecked != false;
        AppSettingsService.Save();

        AppLocalization.ApplyToOpenWindows();
        AppThemeService.ApplyCurrent();
        DialogResult = true;
    }
}
