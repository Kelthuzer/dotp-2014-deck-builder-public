using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using DeckBuilder.GameData;
using Microsoft.Win32;

namespace DeckBuilder.Modern;

public partial class VersionPackagesWindow : Window
{
    private readonly CompleteGameVersionPackageService _service = new();
    private bool _busy;

    public VersionPackagesWindow(string? initialGameDirectory)
    {
        InitializeComponent();
        SourceFolderText.Text = initialGameDirectory ?? string.Empty;
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string workspace = Path.Combine(documents, "DotP2014-WAD-Workspace");
        WorkspaceText.Text = workspace;
        BuildRootText.Text = Path.Combine(workspace, "built");
        VersionNameText.Text = string.IsNullOrWhiteSpace(initialGameDirectory)
            ? string.Empty
            : new DirectoryInfo(initialGameDirectory).Name;
        Loaded += VersionPackagesWindow_Loaded;
    }

    private void VersionPackagesWindow_Loaded(object sender, RoutedEventArgs e) => RefreshPackages();

    private void BrowseSource_Click(object sender, RoutedEventArgs e)
    {
        OpenFolderDialog dialog = new()
        {
            Title = "Select a Magic 2014 game version folder",
            Multiselect = false,
            InitialDirectory = Directory.Exists(SourceFolderText.Text) ? SourceFolderText.Text : null
        };
        if (dialog.ShowDialog(this) == true)
        {
            SourceFolderText.Text = dialog.FolderName;
            if (string.IsNullOrWhiteSpace(VersionNameText.Text))
            {
                VersionNameText.Text = new DirectoryInfo(dialog.FolderName).Name;
            }
        }
    }

    private void BrowseWorkspace_Click(object sender, RoutedEventArgs e)
    {
        OpenFolderDialog dialog = new()
        {
            Title = "Select the version package workspace",
            Multiselect = false,
            InitialDirectory = Directory.Exists(WorkspaceText.Text) ? WorkspaceText.Text : null
        };
        if (dialog.ShowDialog(this) == true)
        {
            WorkspaceText.Text = dialog.FolderName;
            if (string.IsNullOrWhiteSpace(BuildRootText.Text)
                || BuildRootText.Text.Contains("DotP2014-WAD-Workspace", StringComparison.OrdinalIgnoreCase))
            {
                BuildRootText.Text = Path.Combine(dialog.FolderName, "built");
            }

            RefreshPackages();
        }
    }

    private void BrowseBuildRoot_Click(object sender, RoutedEventArgs e)
    {
        OpenFolderDialog dialog = new()
        {
            Title = "Select the root folder for rebuilt versions",
            Multiselect = false,
            InitialDirectory = Directory.Exists(BuildRootText.Text) ? BuildRootText.Text : null
        };
        if (dialog.ShowDialog(this) == true)
        {
            BuildRootText.Text = dialog.FolderName;
        }
    }

    private async void Extract_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        string source = SourceFolderText.Text.Trim();
        string workspace = WorkspaceText.Text.Trim();
        string versionName = VersionNameText.Text.Trim();
        if (!Directory.Exists(source))
        {
            MessageBox.Show(this, "Select an existing Magic 2014 folder.", "Source folder required",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(workspace) || string.IsNullOrWhiteSpace(versionName))
        {
            MessageBox.Show(this, "Workspace and version name are required.", "Version package",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        bool replace = _service.FindPackages(workspace).Any(package =>
            package.VersionName.Equals(versionName, StringComparison.OrdinalIgnoreCase));
        if (replace && MessageBox.Show(
                this,
                $"Version package '{versionName}' already exists. Replace it after a successful extraction?",
                "Replace extracted version",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            Progress<VersionPackageProgress> progress = new(UpdateProgress);
            VersionPackageExtractResult result = await _service.ExtractAsync(
                new VersionPackageExtractOptions(source, workspace, versionName, replace),
                progress);
            AppendLog(
                $"EXTRACT OK  {versionName}\r\n" +
                $"  package: {result.PackageDirectory}\r\n" +
                $"  WADs/sources: {result.WadCount:N0}; files: {result.FileCount:N0}; payload: {FormatBytes(result.PayloadBytes)}");
            RefreshPackages();
        }, "Extraction failed");
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshPackages();

    private void RefreshPackages()
    {
        string workspace = WorkspaceText.Text.Trim();
        IReadOnlyList<VersionPackageInfo> packages = _service.FindPackages(workspace);
        PackagesGrid.ItemsSource = packages;
        StatusText.Text = $"{packages.Count:N0} extracted version package(s).";
    }

    private void BuildUnpacked_Click(object sender, RoutedEventArgs e)
    {
        string? initialSource = (PackagesGrid.SelectedItem as VersionPackageInfo)?.PackageDirectory;
        string output = BuildRootText.Text.Trim();
        UnpackedContentBuilderWindow window = new(initialSource, output)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private async void BuildSelected_Click(object sender, RoutedEventArgs e)
    {
        if (PackagesGrid.SelectedItem is not VersionPackageInfo package)
        {
            MessageBox.Show(this, "Select an extracted version first.", "Build version",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await BuildPackagesAsync(new[] { package });
    }

    private async void BuildAll_Click(object sender, RoutedEventArgs e)
    {
        VersionPackageInfo[] packages = (PackagesGrid.ItemsSource as IEnumerable<VersionPackageInfo>)?.ToArray()
            ?? Array.Empty<VersionPackageInfo>();
        if (packages.Length == 0)
        {
            MessageBox.Show(this, "There are no extracted versions in this workspace.", "Build versions",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await BuildPackagesAsync(packages);
    }

    private async Task BuildPackagesAsync(IReadOnlyList<VersionPackageInfo> packages)
    {
        if (_busy)
        {
            return;
        }

        string outputRoot = BuildRootText.Text.Trim();
        if (outputRoot.Length == 0)
        {
            MessageBox.Show(this, "Select a build output root.", "Build versions",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        bool includeIgnored = IncludeIgnoredCheckBox.IsChecked == true;
        await RunBusyAsync(async () =>
        {
            for (int index = 0; index < packages.Count; index++)
            {
                VersionPackageInfo package = packages[index];
                AppendLog($"BUILD     {package.VersionName} ({index + 1}/{packages.Count})");
                Progress<VersionPackageProgress> progress = new(value =>
                {
                    StatusText.Text = $"{package.VersionName}: {value.Stage} — {value.Source} ({value.Completed}/{value.Total})";
                });
                VersionPackageBuildResult result = await _service.BuildAsync(
                    new VersionPackageBuildOptions(
                        package.PackageDirectory,
                        outputRoot,
                        includeIgnored,
                        ReplaceExisting: true),
                    progress);
                AppendLog(
                    $"BUILD OK  {package.VersionName}\r\n" +
                    $"  output: {result.OutputDirectory}\r\n" +
                    $"  WADs: {result.WadCount:N0}; files: {result.FileCount:N0}; modified: {result.ModifiedFiles:N0}; " +
                    $"payload: {FormatBytes(result.PayloadBytes)}\r\n" +
                    "  verification: all rebuilt payload hashes match the package files");
            }

            StatusText.Text = $"Built and verified {packages.Count:N0} version package(s).";
        }, "Version build failed");
    }

    private void OpenPackage_Click(object sender, RoutedEventArgs e)
    {
        if (PackagesGrid.SelectedItem is not VersionPackageInfo package || !Directory.Exists(package.PackageDirectory))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{package.PackageDirectory}\"",
            UseShellExecute = true
        });
    }

    private async Task RunBusyAsync(Func<Task> action, string errorTitle)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        SetButtonsEnabled(false);
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            AppendLog($"ERROR     {exception}");
            MessageBox.Show(this, exception.Message, errorTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = errorTitle + ".";
        }
        finally
        {
            Mouse.OverrideCursor = null;
            SetButtonsEnabled(true);
            _busy = false;
        }
    }

    private void SetButtonsEnabled(bool enabled)
    {
        ExtractButton.IsEnabled = enabled;
        BuildSelectedButton.IsEnabled = enabled;
        BuildAllButton.IsEnabled = enabled;
    }

    private void UpdateProgress(VersionPackageProgress value)
    {
        StatusText.Text = $"{value.Stage} — {value.Source} ({value.Completed}/{value.Total})";
    }

    private void AppendLog(string text)
    {
        if (LogText.Text.Length > 0)
        {
            LogText.AppendText(Environment.NewLine + Environment.NewLine);
        }

        LogText.AppendText(text);
        LogText.ScrollToEnd();
    }

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double number = value;
        int unit = 0;
        while (number >= 1024 && unit < units.Length - 1)
        {
            number /= 1024;
            unit++;
        }

        return $"{number:N2} {units[unit]}";
    }
}
