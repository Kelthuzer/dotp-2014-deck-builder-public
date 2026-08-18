using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DeckBuilder.GameData;
using Microsoft.Win32;

namespace DeckBuilder.Modern;

public partial class MainWindow
{
    private bool _packageDeckButtonInstalled;

    private void InstallPackageDeckButton(Border deckPanel)
    {
        if (_packageDeckButtonInstalled || deckPanel.Child is not UIElement originalContent)
            return;

        _packageDeckButtonInstalled = true;
        deckPanel.Child = null;

        DockPanel host = new() { LastChildFill = true };
        Border footer = new()
        {
            Padding = new Thickness(0, 10, 0, 0),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        DockPanel.SetDock(footer, Dock.Bottom);

        Grid footerGrid = new();
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Child = footerGrid;

        TextBlock hint = new()
        {
            Text = "Финальный шаг: ID игрока, ресурсы, обложка и полный WAD-комплект",
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.68,
            Margin = new Thickness(2, 0, 12, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        footerGrid.Children.Add(hint);

        Button packageButton = new()
        {
            Content = "Упаковать колоду…",
            MinWidth = 170,
            Padding = new Thickness(16, 7, 16, 7),
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right,
            ToolTip = "Собрать колоду, используемые CARD_V2/иллюстрации, обложку и Content Pack Enabler"
        };
        packageButton.Click += PackageDeck_Click;
        Grid.SetColumn(packageButton, 1);
        footerGrid.Children.Add(packageButton);

        host.Children.Add(footer);
        host.Children.Add(originalContent);
        deckPanel.Child = host;
    }

    private async void PackageDeck_Click(object sender, RoutedEventArgs e)
    {
        if (_deck.MainDeckCardCount == 0)
        {
            MessageBox.Show(this,
                "Сначала добавьте карты в основную колоду.",
                "Пустая колода",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!await EnsurePackagingWorkspaceAsync())
            return;

        DeckPackageWizardWindow wizard = new(
            _deck,
            _projectName,
            _gameDirectory,
            _cardImageLoader)
        {
            Owner = this
        };
        if (wizard.ShowDialog() != true)
            return;

        IReadOnlyList<DeckBuilder.Core.Models.CardRecord> packagingCatalog = BuildCatalogSnapshot();
        string[] usedReferences = PortableDeckCardReferencePlanner
            .GetRequiredReferences(_deck, packagingCatalog)
            .ToArray();

        if (_workspaceCardVariants is null)
        {
            MessageBox.Show(this,
                "Не удалось подготовить индекс распакованного workspace. Упаковка остановлена, чтобы не создать неполный WAD.",
                "Нет индекса ресурсов",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        string[] missingDefinitions = usedReferences
            .Where(reference => !_workspaceCardVariants.CardVariants.Any(variant =>
                variant.Reference.Equals(reference, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (missingDefinitions.Length > 0)
        {
            string shown = string.Join("\n", missingDefinitions.Take(20).Select(reference => "• " + reference));
            string more = missingDefinitions.Length > 20
                ? $"\n…и ещё {missingDefinitions.Length - 20}."
                : string.Empty;
            MessageBox.Show(this,
                "В распакованном workspace не найдены CARD_V2 для некоторых карт колоды или её автоматического land pool.\n" +
                "Я не буду собирать неполный комплект, который может уронить игру.\n\n" +
                shown + more,
                "Не хватает определений карт",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        WorkspaceContentVariantConflict[] relevantConflicts = _workspaceCardVariants.Conflicts
            .Where(conflict => conflict.IsCardDefinition
                && conflict.Variants.Any(variant => usedReferences.Contains(
                    variant.Reference,
                    StringComparer.OrdinalIgnoreCase)))
            .ToArray();

        IReadOnlyDictionary<string, string>? selections = null;
        if (relevantConflicts.Length > 0)
        {
            WorkspaceContentVariantScanResult relevantScan = new(
                _workspaceCardVariants.Kind,
                _workspaceCardVariants.PackageCount,
                _workspaceCardVariants.WadCount,
                _workspaceCardVariants.SourceInstances,
                _workspaceCardVariants.IdenticalCopies,
                relevantConflicts,
                _workspaceCardVariants.CardVariants
                    .Where(variant => usedReferences.Contains(variant.Reference, StringComparer.OrdinalIgnoreCase))
                    .ToArray());

            WorkspaceVariantResolverWindow resolver = new(relevantScan) { Owner = this };
            if (resolver.ShowDialog() != true)
            {
                Status("Упаковка отменена при выборе вариантов карт.");
                return;
            }
            selections = resolver.Selections;
        }

        string fallbackOutputDirectory = Path.GetDirectoryName(wizard.OutputPath)
            ?? _gameDirectory
            ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string outputDirectory = ConfiguredWadOutputDirectory(fallbackOutputDirectory);
        string deckWadPath = Path.Combine(outputDirectory, Path.GetFileName(wizard.OutputPath));
        AppSettingsService.Current.WadOutputDirectory = outputDirectory;
        AppSettingsService.Save();

        string codeName = SanitizeWadCodeName(wizard.DeckName);
        string supportWadPath = Path.Combine(
            outputDirectory,
            $"Data_DLC_9000_{wizard.DeckUid}_{codeName}_Cards.wad");
        string coverMode = string.IsNullOrWhiteSpace(wizard.CustomCoverSourcePath)
            ? "игровая"
            : $"своя ({Path.GetFileName(wizard.CustomCoverSourcePath)}, рубашка {wizard.CustomCoverSkin})";

        MessageBoxResult confirmation = MessageBox.Show(this,
            $"Упаковать '{wizard.DeckName}' полным комплектом?\n\n" +
            $"Игрок / ID Block: {wizard.IdBlock}\n" +
            $"Deck UID: {wizard.DeckUid}\n" +
            $"Обложка: {coverMode} — {wizard.DeckBoxImage}\n" +
            $"Основная колода: {_deck.MainDeckCardCount} карт\n" +
            $"Исходных CARD_V2 (колода + unlocks + land pool): {usedReferences.Length}\n" +
            $"Конфликтов вариантов: {relevantConflicts.Length}\n\n" +
            $"Deck WAD:\n{deckWadPath}\n\n" +
            $"Cards/art/runtime WAD:\n{supportWadPath}\n\n" +
            $"CPE блока {wizard.IdBlock} будет создан/проверен автоматически.",
            "Подтверждение полной упаковки",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes)
            return;

        DeckBuildProgressWindow progressWindow = new() { Owner = this };
        IProgress<WorkspaceSelectedCardsProgress> buildProgress =
            new Progress<WorkspaceSelectedCardsProgress>(progressWindow.Report);
        progressWindow.Show();
        progressWindow.SetProgress(1, "Подготовка", "Создаю временное окружение сборки…");

        Cursor = Cursors.Wait;
        string? generatedCoverTdx = null;
        try
        {
            _deck.Name = wizard.DeckName;
            _deck.Description = wizard.Description;
            _deck.DeckBoxImage = wizard.DeckBoxImage;

            if (!string.IsNullOrWhiteSpace(wizard.CustomCoverSourcePath))
            {
                progressWindow.SetProgress(3, "Обложка", "Готовлю пользовательскую обложку в формате TDX…");
                generatedCoverTdx = Path.Combine(
                    Path.GetTempPath(),
                    $"{wizard.DeckBoxImage}_{Guid.NewGuid():N}.TDX");
                CustomDeckCoverBuilder.Build(
                    wizard.CustomCoverSourcePath,
                    generatedCoverTdx,
                    wizard.CustomCoverOffsetX,
                    wizard.CustomCoverOffsetY,
                    wizard.CustomCoverZoom,
                    wizard.CustomCoverSkin);
            }

            Status("Упаковка: собираю CARD_V2 и portable runtime…");
            WorkspaceSelectedCardsBuildResult support = await _workspaceSelectedCardsBuilder.BuildAsync(
                supportWadPath,
                usedReferences,
                _workspaceCardVariants,
                selections,
                workspaceDirectory: _workspaceDirectory,
                deckBoxImageId: wizard.DeckBoxImage,
                deckBoxTexturePath: generatedCoverTdx,
                runtimeRootIdentifiers: GetAdditionalPortableRuntimeRoots(),
                order: 50,
                cancellationToken: progressWindow.CancellationToken,
                progress: buildProgress);

            progressWindow.CancellationToken.ThrowIfCancellationRequested();
            progressWindow.SetProgress(96, "Deck WAD", "Собираю описание колоды, unlocks и Content Pack Enabler…");
            Status("Упаковка: собираю Deck WAD и Content Pack Enabler…");
            ModernWadExportOptions options = new(
                deckWadPath,
                wizard.Slot,
                wizard.DeckName,
                wizard.Description,
                wizard.IdBlock);
            ModernWadExportResult result = await Task.Run(() =>
                ModernWadExporter.Export(_deck, packagingCatalog, options),
                progressWindow.CancellationToken);

            progressWindow.SetProgress(99, "Финальная проверка", "Проверяю итоговые пути и завершаю комплект…");
            _deck.Uid = result.DeckUid;
            _deck.ContentPack = wizard.IdBlock;
            SetDirty(true);

            string warningText = support.Warnings.Count == 0
                ? string.Empty
                : $"\n\nПредупреждения ({support.Warnings.Count}):\n" +
                  string.Join("\n", support.Warnings.Take(8));
            string enablerText = result.ContentPackEnablerCreated
                ? $"Создан CPE:\n{result.ContentPackEnablerPath}"
                : $"CPE уже существует:\n{result.ContentPackEnablerPath}";

            progressWindow.SetProgress(100, "Готово", "Deck WAD, Cards/runtime WAD и CPE собраны.");
            progressWindow.MarkCompleted();
            progressWindow.Close();

            MessageBox.Show(this,
                $"Полный комплект колоды собран.\n\n" +
                $"Deck UID: {result.DeckUid}\n" +
                $"Обложка: {coverMode} — {wizard.DeckBoxImage}\n\n" +
                $"1. Deck WAD:\n{result.WadPath}\n\n" +
                $"2. Cards/art/runtime WAD:\n{support.WadPath}\n" +
                $"   CARD_V2: {support.CardCount}; иллюстраций: {support.ArtCount}; runtime: {support.RuntimeResourceCount}; всего файлов: {support.BuildResult.FileCount}\n\n" +
                $"3. {enablerText}\n\n" +
                "Для переноса колоды другому игроку передавайте оба WAD колоды/ресурсов и CPE её ID-блока." +
                warningText,
                "Упаковка завершена",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            Status(
                $"Упакована колода {result.DeckUid}: deck WAD + {support.CardCount} CARD_V2 + " +
                $"{support.ArtCount} illustrations + {support.RuntimeResourceCount} runtime resources + " +
                $"{coverMode} deck cover {wizard.DeckBoxImage} + CPE {wizard.IdBlock}.");
        }
        catch (OperationCanceledException)
        {
            Status("Упаковка колоды отменена пользователем.");
            progressWindow.MarkCompleted();
            progressWindow.Close();
            MessageBox.Show(this,
                "Сборка отменена. Временные файлы очищены; готовые WAD не заменялись незавершёнными файлами.",
                "Упаковка отменена",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            progressWindow.MarkCompleted();
            progressWindow.Close();
            ShowError("Не удалось собрать полный комплект колоды", exception);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(generatedCoverTdx) && File.Exists(generatedCoverTdx))
            {
                try
                {
                    File.Delete(generatedCoverTdx);
                }
                catch
                {
                    // Temporary cover cleanup must not hide the packaging result/error.
                }
            }

            if (progressWindow.IsVisible)
            {
                progressWindow.MarkCompleted();
                progressWindow.Close();
            }
            Cursor = null;
        }
    }

    private IEnumerable<string> GetAdditionalPortableRuntimeRoots()
    {
        if (!string.IsNullOrWhiteSpace(_deck.DeckBoxImageLocked)
            && !_deck.DeckBoxImageLocked.Equals("locked", StringComparison.OrdinalIgnoreCase))
        {
            yield return DeckTexturePath(_deck.DeckBoxImageLocked);
        }

        DeckBuilder.Core.Models.AiPersonalityDefinition? personality = _deck.CustomPersonality;
        if (personality is null)
            yield break;

        foreach (string imageId in new[]
                 {
                     personality.LargeAvatarImage,
                     personality.SmallAvatarImage,
                     personality.SmallAvatarLockedImage,
                     personality.LobbyImage
                 })
        {
            if (!string.IsNullOrWhiteSpace(imageId))
                yield return PlaneswalkerTexturePath(imageId);
        }
    }

    private static string DeckTexturePath(string imageId) =>
        $"ART_ASSETS\\TEXTURES\\DECKS\\{Path.GetFileNameWithoutExtension(imageId.Trim())}.TDX";

    private static string PlaneswalkerTexturePath(string imageId) =>
        $"ART_ASSETS\\TEXTURES\\PLANESWALKERS\\{Path.GetFileNameWithoutExtension(imageId.Trim())}.TDX";

    private async Task<bool> EnsurePackagingWorkspaceAsync()
    {
        if (!string.IsNullOrWhiteSpace(_workspaceDirectory)
            && Directory.Exists(_workspaceDirectory)
            && _workspaceCardVariants is not null)
        {
            RememberPackagingWorkspace(_workspaceDirectory);
            return true;
        }

        string configured = AppSettingsService.Current.WorkspaceDirectory;
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
        {
            Status($"Упаковка: загружаю workspace из настроек {configured}…");
            await LoadWorkspaceAsync(configured);
            if (!string.IsNullOrWhiteSpace(_workspaceDirectory)
                && Directory.Exists(_workspaceDirectory)
                && _workspaceCardVariants is not null)
            {
                RememberPackagingWorkspace(_workspaceDirectory);
                return true;
            }
        }

        MessageBox.Show(this,
            "Для полной упаковки нужен распакованный workspace с CARD_V2 и всеми runtime-ресурсами карт.\n\n" +
            "Укажите его в Файл → Settings → «Распакованный workspace» или выберите папку сейчас.",
            "Нужен workspace",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        OpenFolderDialog dialog = new()
        {
            Title = "Выберите распакованный workspace Magic 2014",
            Multiselect = false,
            InitialDirectory = Directory.Exists(configured) ? configured : null
        };
        if (dialog.ShowDialog(this) != true)
        {
            Status("Упаковка отменена: workspace не выбран.");
            return false;
        }

        await LoadWorkspaceAsync(dialog.FolderName);
        if (string.IsNullOrWhiteSpace(_workspaceDirectory)
            || !Directory.Exists(_workspaceDirectory)
            || _workspaceCardVariants is null)
        {
            MessageBox.Show(this,
                "Выбранную папку не удалось загрузить как workspace. Неполный WAD создан не будет.",
                "Workspace не загружен",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }

        RememberPackagingWorkspace(_workspaceDirectory);
        return true;
    }

    private static void RememberPackagingWorkspace(string path)
    {
        AppSettingsService.Current.WorkspaceDirectory = Path.GetFullPath(path);
        AppSettingsService.Save();
    }
}
