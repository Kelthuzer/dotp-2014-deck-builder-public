using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DeckBuilder.Modern;

internal static class AppThemeService
{
    public static bool IsDark => ResolveTheme(AppSettingsService.Current.Theme) == "dark";

    public static void ApplyCurrent()
    {
        string theme = ResolveTheme(AppSettingsService.Current.Theme);
        Palette palette = theme == "dark" ? Palette.Dark : Palette.Light;
        ResourceDictionary resources = Application.Current.Resources;

        SetBrush(resources, "WindowBrush", palette.Window);
        SetBrush(resources, "PanelBrush", palette.Panel);
        SetBrush(resources, "ControlBrush", palette.Control);
        SetBrush(resources, "HeaderBrush", palette.Header);
        SetBrush(resources, "BorderBrush", palette.Border);
        SetBrush(resources, "TextBrush", palette.Text);
        SetBrush(resources, "MutedTextBrush", palette.MutedText);
        SetBrush(resources, "GridLineBrush", palette.GridLine);
        SetBrush(resources, "AlternateRowBrush", palette.AlternateRow);
        SetBrush(resources, "SelectionBrush", palette.Selection);
        SetBrush(resources, "SelectionTextBrush", palette.SelectionText);

        foreach (Window window in Application.Current.Windows)
            ApplyToWindow(window);
    }

    public static void ApplyToWindow(Window window)
    {
        window.SetResourceReference(Control.BackgroundProperty, "WindowBrush");
        window.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        ApplyLegacySurfaces(window);
        window.Dispatcher.BeginInvoke(() => ApplyLegacySurfaces(window));
    }

    public static string ResolveTheme(string? requested) => AppSettingsService.NormalizeTheme(requested) switch
    {
        "light" => "light",
        "dark" => "dark",
        _ => IsWindowsDarkMode() ? "dark" : "light"
    };

    private static void SetBrush(ResourceDictionary resources, string key, string color) =>
        resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));

    private static bool IsWindowsDarkMode()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            object? value = key?.GetValue("AppsUseLightTheme");
            return value is int useLightTheme && useLightTheme == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void ApplyLegacySurfaces(DependencyObject root)
    {
        switch (root)
        {
            case Panel panel when IsLegacyLightBrush(panel.Background):
                panel.SetResourceReference(Panel.BackgroundProperty, "PanelBrush");
                break;
            case Border border when IsLegacyLightBrush(border.Background):
                border.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
                break;
            case Control control when IsLegacyLightBrush(control.Background):
                control.SetResourceReference(Control.BackgroundProperty, "ControlBrush");
                break;
        }

        switch (root)
        {
            case TextBlock textBlock when IsLegacyTextBrush(textBlock.Foreground):
                textBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                break;
            case Control control when IsLegacyTextBrush(control.Foreground):
                control.SetResourceReference(Control.ForegroundProperty, "TextBrush");
                break;
        }

        if (root is TextBlock muted && IsLegacyMutedBrush(muted.Foreground))
            muted.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
            ApplyLegacySurfaces(VisualTreeHelper.GetChild(root, index));
    }

    private static bool IsLegacyLightBrush(Brush? brush)
    {
        if (brush is not SolidColorBrush solid)
            return false;

        Color c = solid.Color;
        return c == Colors.White
            || c == Color.FromRgb(248, 250, 252)
            || c == Color.FromRgb(244, 246, 248)
            || c == Color.FromRgb(241, 245, 249)
            || c == Color.FromRgb(226, 232, 240);
    }

    private static bool IsLegacyTextBrush(Brush? brush)
    {
        if (brush is not SolidColorBrush solid)
            return false;

        Color c = solid.Color;
        return c == Colors.Black || c == Color.FromRgb(17, 24, 39);
    }

    private static bool IsLegacyMutedBrush(Brush? brush)
    {
        if (brush is not SolidColorBrush solid)
            return false;

        Color c = solid.Color;
        return c == Color.FromRgb(85, 85, 85)
            || c == Color.FromRgb(102, 102, 102)
            || c == Color.FromRgb(85, 96, 112);
    }

    private sealed record Palette(
        string Window,
        string Panel,
        string Control,
        string Header,
        string Border,
        string Text,
        string MutedText,
        string GridLine,
        string AlternateRow,
        string Selection,
        string SelectionText)
    {
        public static readonly Palette Light = new(
            "#F1F5F9", "#FFFFFF", "#F8FAFC", "#E2E8F0", "#CBD5E1",
            "#111827", "#556070", "#E5E7EB", "#F8FAFC", "#DBEAFE", "#111827");

        public static readonly Palette Dark = new(
            "#161A20", "#1E232B", "#272D36", "#303742", "#46505E",
            "#E5E7EB", "#AEB7C4", "#343C47", "#232932", "#344D70", "#F8FAFC");
    }
}
