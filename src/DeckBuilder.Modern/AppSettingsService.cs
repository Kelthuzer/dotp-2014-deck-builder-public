using System.IO;
using System.Text.Json;

namespace DeckBuilder.Modern;

internal sealed class ModernAppSettings
{
    public string Language { get; set; } = "ru";
    public string Theme { get; set; } = "system";
    public bool StartMaximized { get; set; } = true;
    public bool SmoothPreviewTransitions { get; set; } = true;
    public List<double> MainLayoutWidths { get; set; } = new();
    public List<double> RightLayoutRowHeights { get; set; } = new();
    public List<double> CatalogColumnWidths { get; set; } = new();
    public List<double> MainDeckColumnWidths { get; set; } = new();
    public List<double> RegularUnlockColumnWidths { get; set; } = new();
    public List<double> PromoUnlockColumnWidths { get; set; } = new();
    public int PreviewCount { get; set; } = 1;
    public double WorkspacePreviewRatio { get; set; } = 0.58;
    public string LastWorkspaceDirectory { get; set; } = string.Empty;
}

internal static class AppSettingsService
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DotP2014DeckBuilder");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    public static ModernAppSettings Current { get; private set; } = Load();

    public static void Save()
    {
        Directory.CreateDirectory(SettingsDirectory);
        string json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }

    private static ModernAppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new ModernAppSettings();

            string json = File.ReadAllText(SettingsPath);
            ModernAppSettings? settings = JsonSerializer.Deserialize<ModernAppSettings>(json);
            if (settings is null)
                return new ModernAppSettings();

            settings.Language = NormalizeLanguage(settings.Language);
            settings.Theme = NormalizeTheme(settings.Theme);
            settings.MainLayoutWidths ??= new();
            settings.RightLayoutRowHeights ??= new();
            settings.CatalogColumnWidths ??= new();
            settings.MainDeckColumnWidths ??= new();
            settings.RegularUnlockColumnWidths ??= new();
            settings.PromoUnlockColumnWidths ??= new();
            settings.PreviewCount = Math.Clamp(settings.PreviewCount, 1, 5);
            settings.WorkspacePreviewRatio = Math.Clamp(settings.WorkspacePreviewRatio, 0.42, 0.72);
            settings.LastWorkspaceDirectory ??= string.Empty;
            return settings;
        }
        catch
        {
            return new ModernAppSettings();
        }
    }

    public static string NormalizeLanguage(string? language) =>
        string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "ru";

    public static string NormalizeTheme(string? theme) => theme?.ToLowerInvariant() switch
    {
        "light" => "light",
        "dark" => "dark",
        _ => "system"
    };
}
