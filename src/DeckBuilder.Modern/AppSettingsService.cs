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

    // Central data locations used by loading and portable WAD packaging.
    public string GameDirectory { get; set; } = string.Empty;
    public string WorkspaceDirectory { get; set; } = string.Empty;
    public string WadOutputDirectory { get; set; } = string.Empty;
}

internal static class AppSettingsService
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DotP2014DeckBuilder");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");
    private static readonly string LegacyGameDirectoryPath = Path.Combine(SettingsDirectory, "game-directory.txt");

    public static ModernAppSettings Current { get; private set; } = Load();

    public static void Save()
    {
        Directory.CreateDirectory(SettingsDirectory);
        NormalizeDirectories(Current);
        string json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);

        // Keep old builds and the legacy MainWindow loader compatible while paths migrate to settings.json.
        if (!string.IsNullOrWhiteSpace(Current.GameDirectory))
            File.WriteAllText(LegacyGameDirectoryPath, Current.GameDirectory);
        else if (File.Exists(LegacyGameDirectoryPath))
            File.Delete(LegacyGameDirectoryPath);
    }

    private static ModernAppSettings Load()
    {
        try
        {
            ModernAppSettings settings;
            string? legacyWorkspace = null;

            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                settings = JsonSerializer.Deserialize<ModernAppSettings>(json) ?? new ModernAppSettings();

                // Migrate the pre-centralized workspace setting without keeping it in the new model.
                using JsonDocument document = JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty("LastWorkspaceDirectory", out JsonElement oldWorkspace)
                    && oldWorkspace.ValueKind == JsonValueKind.String)
                {
                    legacyWorkspace = oldWorkspace.GetString();
                }
            }
            else
            {
                settings = new ModernAppSettings();
            }

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

            if (string.IsNullOrWhiteSpace(settings.GameDirectory) && File.Exists(LegacyGameDirectoryPath))
                settings.GameDirectory = File.ReadAllText(LegacyGameDirectoryPath).Trim();
            if (string.IsNullOrWhiteSpace(settings.WorkspaceDirectory) && !string.IsNullOrWhiteSpace(legacyWorkspace))
                settings.WorkspaceDirectory = legacyWorkspace;
            if (string.IsNullOrWhiteSpace(settings.WadOutputDirectory))
                settings.WadOutputDirectory = settings.GameDirectory;

            NormalizeDirectories(settings);
            return settings;
        }
        catch
        {
            return new ModernAppSettings();
        }
    }

    private static void NormalizeDirectories(ModernAppSettings settings)
    {
        settings.GameDirectory = NormalizeDirectory(settings.GameDirectory);
        settings.WorkspaceDirectory = NormalizeDirectory(settings.WorkspaceDirectory);
        settings.WadOutputDirectory = NormalizeDirectory(settings.WadOutputDirectory);
    }

    public static string NormalizeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        try
        {
            return Path.GetFullPath(path.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim();
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
