using System.IO;

namespace DeckBuilder.Modern;

public partial class MainWindow
{
    private bool _autoWorkspaceLoadAttempted;

    private async Task AutoLoadRememberedWorkspaceAsync()
    {
        if (_autoWorkspaceLoadAttempted)
            return;

        _autoWorkspaceLoadAttempted = true;

        string configured = AppSettingsService.Current.WorkspaceDirectory;
        if (string.IsNullOrWhiteSpace(configured) || !Directory.Exists(configured))
            return;

        string fullPath = Path.GetFullPath(configured);
        if (string.Equals(_workspaceDirectory, fullPath, StringComparison.OrdinalIgnoreCase)
            && _workspaceCardVariants is not null)
        {
            return;
        }

        if (_loading)
        {
            Status("Workspace: жду завершения загрузки папки игры…");
            while (_loading)
                await Task.Delay(100);
        }

        Status($"Workspace: автоматически загружаю {fullPath}…");
        await LoadWorkspaceAsync(fullPath);

        if (string.Equals(_workspaceDirectory, fullPath, StringComparison.OrdinalIgnoreCase)
            && _workspaceCardVariants is not null)
        {
            Status($"Workspace автоматически загружен: {fullPath}");
        }
    }
}
