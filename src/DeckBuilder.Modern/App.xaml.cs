using System.Windows;
using System.Windows.Threading;

namespace DeckBuilder.Modern;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += HandleUnhandledException;
        Activated += App_Activated;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppThemeService.ApplyCurrent();
    }

    private static void App_Activated(object? sender, EventArgs e)
    {
        if (Current.MainWindow is MainWindow mainWindow)
        {
            mainWindow.InstallCardReferenceScannerMenu();
            mainWindow.InstallCardTranslationEditor();
            mainWindow.InstallDeckBuildingAssistantMenu();
            mainWindow.InstallDeckAssistantDashboard();
            mainWindow.InstallCatalogTokenFilter();
            mainWindow.InstallCatalogSearchAndSort();
            mainWindow.InstallSettingsMenu();
            mainWindow.InstallMultiCardPreview();
            mainWindow.InstallAdaptiveWorkspaceLayout();
            mainWindow.InstallUnifiedMultiPreview();
            mainWindow.InstallLoadingIndicator();
            mainWindow.InstallInteractionsAndLayoutPersistence();
            mainWindow.InstallPreviewVisibilityGuard();
            AppLocalization.Apply(mainWindow);
            AppThemeService.ApplyCurrent();
            mainWindow.UpdateDeckAssistantDashboard();
        }
    }

    private static void HandleUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            e.Exception.Message,
            "DotP 2014 Deck Builder",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
