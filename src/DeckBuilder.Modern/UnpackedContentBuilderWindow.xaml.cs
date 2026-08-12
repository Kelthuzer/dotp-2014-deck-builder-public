using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using DeckBuilder.GameData;
using Microsoft.Win32;

namespace DeckBuilder.Modern;

public partial class UnpackedContentBuilderWindow : Window
{
    private readonly UnpackedContentWadBuilder _builder = new();
    private readonly WorkspaceContentWadBuilder _workspaceBuilder = new();
    private readonly WorkspaceContentVariantScanner _variantScanner = new();
    private bool _busy;

    public UnpackedContentBuilderWindow(string? initialSource, string? initialOutput)
    {
        InitializeComponent();

        string source = initialSource ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(source)
            && File.Exists(Path.Combine(source, GameVersionPackageService.ManifestFileName)))
        {
            string? parent = Directory.GetParent(source)?.FullName;
            if (!string.IsNullOrWhiteSpace(parent) && WorkspaceContentWadBuilder.IsWorkspaceRoot(parent))
            {
                source = parent;
            }
        }

        SourceFolderText.Text = source;
        OutputFolderText.Text = initialOutput ?? string.Empty;
    }

    private void BrowseSource_Click(object sender, RoutedEventArgs e)
    {
        OpenFolderDialog dialog = new()
        {
            Title = "Select workspace root, extracted version package, or unpacked content folder",
            Multiselect = false,
            InitialDirectory = Directory.Exists(SourceFolderText.Text) ? SourceFolderText.Text : null
        };
        if (dialog.ShowDialog(this) == true)
        {
            SourceFolderText.Text = dialog.FolderName;
        }
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        OpenFolderDialog dialog = new()
        {
            Title = "Select WAD output folder",
            Multiselect = false,
            InitialDirectory = Directory.Exists(OutputFolderText.Text) ? OutputFolderText.Text : null
        };
        if (dialog.ShowDialog(this) == true)
        {
            OutputFolderText.Text = dialog.FolderName;
        }
    }

    private async void Build_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        string source = SourceFolderText.Text.Trim();
        string output = OutputFolderText.Text.Trim();
        if (!Directory.Exists(source))
        {
            MessageBox.Show(this, "Select an existing unpacked source folder.", "Build from unpacked content",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (output.Length == 0)
        {
            MessageBox.Show(this, "Select an output folder.", "Build from unpacked content",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!int.TryParse(OrderText.Text.Trim(), out int order))
        {
            MessageBox.Show(this, "WAD order must be an integer.", "Build from unpacked content",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        bool buildCards = BuildCardsCheckBox.IsChecked == true;
        bool buildDecks = BuildDecksCheckBox.IsChecked == true;
        if (!buildCards && !buildDecks)
        {
            MessageBox.Show(this, "Select Cards, Decks, or both.", "Build from unpacked content",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        bool aggregateWorkspace = WorkspaceContentWadBuilder.IsWorkspaceRoot(source);
        Directory.CreateDirectory(output);
        await RunBusyAsync(async () =>
        {
            if (aggregateWorkspace)
            {
                AppendLog($"WORKSPACE  {source}\r\n  scanning every extracted version package below this folder");
            }

            if (buildCards)
            {
                string cardsName = NormalizeWadName(CardsWadNameText.Text, "Data_DLC_9000_Cards.wad");
                bool built = await BuildKindAsync(
                    source,
                    Path.Combine(output, cardsName),
                    UnpackedContentKind.Cards,
                    order,
                    aggregateWorkspace);
                if (!built)
                {
                    StatusText.Text = "Build cancelled while choosing card variants.";
                    return;
                }
            }

            if (buildDecks)
            {
                string decksName = NormalizeWadName(DecksWadNameText.Text, "Data_Decks_9000_Custom.wad");
                bool built = await BuildKindAsync(
                    source,
                    Path.Combine(output, decksName),
                    UnpackedContentKind.Decks,
                    order,
                    aggregateWorkspace);
                if (!built)
                {
                    StatusText.Text = "Build cancelled while choosing deck variants.";
                    return;
                }
            }

            StatusText.Text = aggregateWorkspace
                ? "Workspace content aggregated, built, and verified."
                : "Selected unpacked content built and verified.";
        });
    }

    private async Task<bool> BuildKindAsync(
        string source,
        string outputPath,
        UnpackedContentKind kind,
        int order,
        bool aggregateWorkspace)
    {
        if (aggregateWorkspace)
        {
            StatusText.Text = $"Scanning {kind.ToString().ToLowerInvariant()} variants across workspace…";
            WorkspaceContentVariantScanResult scan = await _variantScanner.ScanAsync(source, kind);
            IReadOnlyDictionary<string, string>? selections = null;
            if (scan.Conflicts.Count > 0)
            {
                AppendLog(
                    $"VARIANTS  {kind}\r\n" +
                    $"  packages: {scan.PackageCount:N0}; source WADs: {scan.WadCount:N0}; source instances: {scan.SourceInstances:N0}\r\n" +
                    $"  identical copies: {scan.IdenticalCopies:N0}; differing paths requiring choice: {scan.Conflicts.Count:N0}");
                WorkspaceVariantResolverWindow resolver = new(scan) { Owner = this };
                if (resolver.ShowDialog() != true)
                {
                    return false;
                }

                selections = resolver.Selections;
            }

            StatusText.Text = $"Aggregating {kind.ToString().ToLowerInvariant()} from all workspace WADs…";
            WorkspaceContentBuildResult result = await _workspaceBuilder.BuildAsync(
                source,
                outputPath,
                kind,
                order,
                selections);
            AppendResult(result);
            return true;
        }

        StatusText.Text = $"Building {kind.ToString().ToLowerInvariant()} WAD…";
        UnpackedContentBuildResult normal = await _builder.BuildAsync(new UnpackedContentBuildOptions(
            source,
            outputPath,
            kind,
            order));
        AppendResult(normal);
        return true;
    }

    private void AppendResult(UnpackedContentBuildResult result)
    {
        AppendLog(
            $"BUILD OK  {result.Kind}\r\n" +
            $"  output: {result.OutputPath}\r\n" +
            $"  files: {result.FileCount:N0}; payload: {FormatBytes(result.PayloadBytes)}; " +
            $"resolved overrides: {result.OverriddenFiles:N0}\r\n" +
            $"  source mode: {(result.UsedVersionManifest ? "version package / game priority" : "plain unpacked folder")}\r\n" +
            "  verification: rebuilt payload hashes match source files");
    }

    private void AppendResult(WorkspaceContentBuildResult result)
    {
        AppendLog(
            $"BUILD OK  {result.Kind} / ALL WORKSPACE PACKAGES\r\n" +
            $"  output: {result.OutputPath}\r\n" +
            $"  sources: {result.ProvenancePath}\r\n" +
            $"  packages: {result.PackageCount:N0}; source WADs: {result.WadCount:N0}; source instances: {result.SourceInstances:N0}\r\n" +
            $"  effective files: {result.FileCount:N0}; payload: {FormatBytes(result.PayloadBytes)}\r\n" +
            $"  identical alternate sources: {result.DuplicateFiles:N0}; differing alternate sources: {result.ConflictingFiles:N0}\r\n" +
            "  every source is retained in the .sources.json provenance manifest\r\n" +
            "  verification: rebuilt payload hashes match selected source files");
    }

    private void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        string path = OutputFolderText.Text.Trim();
        if (!Directory.Exists(path))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{path}\"",
            UseShellExecute = true
        });
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        _busy = true;
        BuildButton.IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            AppendLog($"ERROR     {exception}");
            MessageBox.Show(this, exception.Message, "Build from unpacked content failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Build failed.";
        }
        finally
        {
            Mouse.OverrideCursor = null;
            BuildButton.IsEnabled = true;
            _busy = false;
        }
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

    private static string NormalizeWadName(string value, string fallback)
    {
        string name = value.Trim();
        if (name.Length == 0)
        {
            name = fallback;
        }

        name = Path.GetFileName(name);
        return name.EndsWith(".wad", StringComparison.OrdinalIgnoreCase) ? name : name + ".wad";
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
