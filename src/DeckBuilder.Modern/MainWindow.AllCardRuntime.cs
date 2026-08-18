using System.IO;
using System.Windows;
using System.Windows.Input;
using DeckBuilder.GameData;

namespace DeckBuilder.Modern;

public partial class MainWindow
{
    private async void BuildAllCardRuntime_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsurePackagingWorkspaceAsync())
            return;

        if (_workspaceCardVariants is null || string.IsNullOrWhiteSpace(_workspaceDirectory))
        {
            MessageBox.Show(this,
                "Не удалось подготовить индекс распакованного workspace.",
                "Нет workspace",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        string outputDirectory = ConfiguredWadOutputDirectory(_gameDirectory);
        string outputPath = Path.Combine(outputDirectory, "Data_DLC_8000_DeckBuilder_Runtime.wad");
        int cardCount = _workspaceCardVariants.CardVariants
            .Select(variant => variant.Reference)
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        MessageBoxResult confirmation = MessageBox.Show(this,
            "Собрать один общий Runtime WAD для всех карт текущего workspace?\n\n" +
            $"CARD_V2-корней: {cardCount:N0}\n" +
            $"Результат:\n{outputPath}\n\n" +
            "В WAD попадут только зависимости механик карт: FUNCTIONS, SPECS, TEXT_PERMANENT и реально используемые runtime-assets. " +
            "Сами CARD_V2 и их иллюстрации в этот WAD не копируются.\n\n" +
            "Операция анализирует весь workspace и может занять заметное время.",
            "Общий runtime карт",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes)
            return;

        DeckBuildProgressWindow progressWindow = new() { Owner = this };
        IProgress<WorkspaceSelectedCardsProgress> progress =
            new Progress<WorkspaceSelectedCardsProgress>(progressWindow.Report);
        progressWindow.Show();
        progressWindow.SetProgress(1, "Общий runtime", "Подготавливаю анализ всех CARD_V2…");

        Cursor = Cursors.Wait;
        try
        {
            Status("Собираю общий runtime для всех карт workspace…");
            WorkspaceAllCardRuntimeBuildResult result = await new WorkspaceAllCardRuntimeBuilder().BuildAsync(
                outputPath,
                _workspaceDirectory,
                _workspaceCardVariants,
                order: 40,
                cancellationToken: progressWindow.CancellationToken,
                progress: progress);

            progressWindow.MarkCompleted();
            progressWindow.Close();

            long wadBytes = new FileInfo(result.WadPath).Length;
            string groups = string.Join(
                "\n",
                result.RuntimeResourceCounts
                    .OrderByDescending(pair => pair.Value)
                    .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Take(12)
                    .Select(pair => $"• {pair.Key}: {pair.Value:N0}"));
            string warningText = result.Warnings.Count == 0
                ? string.Empty
                : $"\n\nПредупреждения ({result.Warnings.Count}):\n" + string.Join("\n", result.Warnings.Take(8));

            MessageBox.Show(this,
                "Общий runtime WAD собран.\n\n" +
                $"CARD_V2 проанализировано: {result.CardRootCount:N0}\n" +
                $"Runtime-ресурсов: {result.RuntimeResourceCount:N0}\n" +
                $"Размер WAD: {FormatBytes(wadBytes)}\n\n" +
                $"Состав:\n{groups}\n\n" +
                $"WAD:\n{result.WadPath}\n\n" +
                $"Manifest:\n{result.ManifestPath}" + warningText,
                "Общий runtime готов",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            Status($"Общий runtime: {result.RuntimeResourceCount:N0} ресурсов / {result.CardRootCount:N0} CARD_V2 / {FormatBytes(wadBytes)}.");
        }
        catch (OperationCanceledException)
        {
            progressWindow.MarkCompleted();
            progressWindow.Close();
            Status("Сборка общего runtime отменена.");
        }
        catch (Exception exception)
        {
            progressWindow.MarkCompleted();
            progressWindow.Close();
            ShowError("Не удалось собрать общий runtime WAD", exception);
        }
        finally
        {
            if (progressWindow.IsVisible)
            {
                progressWindow.MarkCompleted();
                progressWindow.Close();
            }
            Cursor = null;
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }
}
