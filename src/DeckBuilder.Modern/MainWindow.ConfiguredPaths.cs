using System.IO;

namespace DeckBuilder.Modern;

public partial class MainWindow
{
    private bool _configuredPathsApplied;

    internal void InstallConfiguredDataPaths()
    {
        if (_configuredPathsApplied)
            return;

        _configuredPathsApplied = true;
        _ = ApplyConfiguredDataPathsAsync();
    }

    internal Task ReloadConfiguredDataPathsAsync() => ApplyConfiguredDataPathsAsync();

    private async Task ApplyConfiguredDataPathsAsync()
    {
        string gameDirectory = AppSettingsService.Current.GameDirectory;
        if (!string.IsNullOrWhiteSpace(gameDirectory)
            && Directory.Exists(gameDirectory)
            && !string.Equals(_gameDirectory, gameDirectory, StringComparison.OrdinalIgnoreCase))
        {
            while (_loading)
                await Task.Delay(100);
            await LoadCatalogAsync(gameDirectory);
        }

        string workspaceDirectory = AppSettingsService.Current.WorkspaceDirectory;
        if (!string.IsNullOrWhiteSpace(workspaceDirectory)
            && Directory.Exists(workspaceDirectory)
            && (!string.Equals(_workspaceDirectory, workspaceDirectory, StringComparison.OrdinalIgnoreCase)
                || _workspaceCardVariants is null))
        {
            while (_loading)
                await Task.Delay(100);
            await LoadWorkspaceAsync(workspaceDirectory);
        }
    }

    private static string ConfiguredWadOutputDirectory(string? fallback)
    {
        string configured = AppSettingsService.Current.WadOutputDirectory;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                Directory.CreateDirectory(configured);
                return Path.GetFullPath(configured);
            }
            catch
            {
                // Fall back to the caller-provided location; validation will report real write errors later.
            }
        }

        if (!string.IsNullOrWhiteSpace(fallback))
            return Path.GetFullPath(fallback);

        return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    }
}
