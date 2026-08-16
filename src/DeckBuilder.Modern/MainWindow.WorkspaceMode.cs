using System.IO;
using System.Windows;
using System.Windows.Input;
using DeckBuilder.Core.Models;
using DeckBuilder.Core.Services;
using DeckBuilder.GameData;
using Microsoft.Win32;

namespace DeckBuilder.Modern;

public partial class MainWindow
{
    private readonly WorkspaceDeepPoolLoader _workspacePoolLoader = new();
    private readonly WorkspacePayloadManifestRefresher _workspacePayloadManifestRefresher = new();
    private readonly WorkspaceSelectedCardsBuilder _workspaceSelectedCardsBuilder = new();
    private string? _workspaceDirectory;
    private WorkspaceContentVariantScanResult? _workspaceCardVariants;

    private async void LoadWorkspace_Click(object sender, RoutedEventArgs e)
    {
        OpenFolderDialog dialog = new()
        {
            Title = "Select extracted WAD workspace (for example D:\\Games\\WAD)",
            Multiselect = false,
            InitialDirectory = Directory.Exists(_workspaceDirectory) ? _workspaceDirectory : null
        };
        if (dialog.ShowDialog(this) == true)
        {
            await LoadWorkspaceAsync(dialog.FolderName);
        }
    }

    private async void ReloadWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_workspaceDirectory) || !Directory.Exists(_workspaceDirectory))
        {
            LoadWorkspace_Click(sender, e);
            return;
        }

        await LoadWorkspaceAsync(_workspaceDirectory, rescanPayload: true);
    }

    private async Task LoadWorkspaceAsync(string path, bool rescanPayload = false)
    {
        if (_loading)
        {
            return;
        }

        string fullPath = Path.GetFullPath(path);
        SetLoadingIndicatorContext("Loading unpacked workspace…", $"Scanning extracted data in {fullPath}…");
        Status($"Workspace: scanning extracted data in {fullPath}…");
        _loading = true;
        Cursor = Cursors.Wait;
        try
        {
            if (rescanPayload)
            {
                SetLoadingIndicatorContext("Reloading unpacked workspace…", "Rescanning actual payload files…");
                Status($"Workspace: rescanning all extracted payload files in {fullPath}…");
                WorkspacePayloadRefreshResult refresh =
                    await _workspacePayloadManifestRefresher.RefreshAsync(fullPath);
                Status(
                    $"Workspace: payload rescan complete — {refresh.FilesScanned:N0} file(s) scanned, " +
                    $"{refresh.FilesAdded:N0} new file(s) registered in {refresh.ManifestsUpdated:N0} manifest(s).");
            }

            Progress<CatalogLoadProgress> progress = new(value =>
                Status($"Workspace: loading {value.Source} — {value.CardsLoaded:N0} cards…"));
            WorkspacePoolLoadResult result = await _workspacePoolLoader.LoadAsync(fullPath, progress);

            Status("Workspace: resolving card and deck variants…");
            _catalog = result.Cards.ToList();
            _installedDecks = result.Decks.ToList();
            _workspaceCardVariants = result.CardVariants;
            _workspaceDirectory = fullPath;
            _cardImageLoader = new GameCardImageLoader(_workspaceDirectory);

            Status("Workspace: rebuilding search index and card catalog…");
            RebindDeckCardsToCatalog();
            RebuildSearchIndex();
            // RebuildSearchIndex still services the legacy search path, which historically capped
            // AvailableCards at 5,000. The modern catalog search/sort path is uncapped, so make it
            // authoritative after a workspace load.
            RefreshCatalogSearchResults();
            RefreshCollections();

            int conflictCount = result.CardVariants.Conflicts.Count;
            string warnings = result.Warnings.Count == 0 ? string.Empty : $"; {result.Warnings.Count:N0} warning(s)";
            Status(
                $"Workspace mode: {_catalog.Count:N0} logical cards, {_installedDecks.Count:N0} deck variants, " +
                $"{conflictCount:N0} card variant conflict(s) from {result.PackageCount:N0} extracted version(s){warnings}. " +
                "No WAD has been built.");
        }
        catch (Exception exception)
        {
            ShowError("Could not load the extracted workspace", exception);
        }
        finally
        {
            Cursor = null;
            _loading = false;
            SetLoadingIndicatorContext("Loading game data…", "Preparing…");
        }
    }

    private async void CreateFromExistingSource_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_workspaceDirectory))
        {
            CreateFromExistingDeck_Click(sender, e);
            return;
        }

        if (_installedDecks.Count == 0)
        {
            MessageBox.Show(
                this,
                "No decks were found in the extracted workspace.",
                "No existing decks",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (_dirty && MessageBox.Show(
                this,
                "Creating a copy will replace the current unsaved project. Continue?",
                "Unsaved work",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        ExistingDeckPickerWindow dialog = new(_installedDecks) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedDeck is null)
        {
            return;
        }

        int slot = -1;
        if (!string.IsNullOrWhiteSpace(_gameDirectory) && Directory.Exists(_gameDirectory))
        {
            try
            {
                slot = await Task.Run(() => ModernWadExporter.SuggestSlot(_gameDirectory, -1));
            }
            catch
            {
                slot = -1;
            }
        }

        int uid = slot >= 0 ? int.Parse($"1000{slot:00}") : -1;
        _deck = DeckDocumentCloner.Clone(dialog.SelectedDeck.Deck, uid, dialog.NewDeckName);
        _editor = new DeckEditor(_deck);
        _projectName = dialog.NewDeckName;
        _projectPath = null;
        MergeDeckCardsIntoCatalog();
        SetDirty(true);
        RefreshCollections();
        Status($"Created a workspace copy of {dialog.SelectedDeck.DisplayName} from {dialog.SelectedDeck.Source}.");
    }

    private async void ExportWorkspaceAwareWad_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_workspaceDirectory) || _workspaceCardVariants is null)
        {
            ExportGameWad_Click(sender, e);
            return;
        }

        if (_deck.MainDeckCardCount == 0)
        {
            MessageBox.Show(this, "Add at least one card to the main deck before exporting.", "Empty deck",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        int slot = PreferredWorkspaceSlot();
        if (!string.IsNullOrWhiteSpace(_gameDirectory) && Directory.Exists(_gameDirectory))
        {
            try
            {
                slot = await Task.Run(() => ModernWadExporter.SuggestSlot(_gameDirectory, _deck.Uid));
            }
            catch
            {
                // Workspace export can still be written to a chosen directory without scanning an install.
            }
        }

        string deckName = string.IsNullOrWhiteSpace(_deck.Name) || _deck.Name == "Untitled deck"
            ? _projectName
            : _deck.Name;
        int deckUid = int.Parse($"1000{slot:00}");
        string codeName = SanitizeWadCodeName(deckName);
        SaveFileDialog dialog = new()
        {
            Title = "Export Magic 2014 deck from unpacked workspace",
            Filter = "Magic 2014 WAD (*.wad)|*.wad",
            DefaultExt = ".wad",
            AddExtension = true,
            InitialDirectory = !string.IsNullOrWhiteSpace(_gameDirectory) && Directory.Exists(_gameDirectory)
                ? _gameDirectory
                : _workspaceDirectory,
            FileName = $"Data_Decks_{deckUid}_{codeName}.wad"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        string[] usedReferences = _deck.MainDeck
            .Concat(_deck.RegularUnlocks)
            .Concat(_deck.PromoUnlocks)
            .Select(entry => entry.Card.FileName)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        WorkspaceContentVariantConflict[] relevantConflicts = _workspaceCardVariants.Conflicts
            .Where(conflict => conflict.IsCardDefinition
                               && conflict.Variants.Any(variant => usedReferences.Contains(
                                   variant.Reference,
                                   StringComparer.OrdinalIgnoreCase)))
            .ToArray();

        MessageBoxResult confirmation = MessageBox.Show(
            this,
            $"Export '{deckName}' to slot {slot:00} (deck UID {deckUid})?\n\n" +
            $"Deck WAD: {dialog.FileName}\n" +
            $"Cards used: {usedReferences.Length:N0}\n" +
            $"Card conflicts requiring a choice: {relevantConflicts.Length:N0}\n\n" +
            "Only cards actually used by this deck will be packaged from the extracted workspace.",
            "Confirm workspace WAD export",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

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
                Status("Workspace export cancelled while choosing card variants.");
                return;
            }

            selections = resolver.Selections;
        }

        string outputDirectory = Path.GetDirectoryName(dialog.FileName)!;
        string supportWadPath = Path.Combine(
            outputDirectory,
            $"Data_DLC_9000_{deckUid}_{codeName}_Cards.wad");

        Cursor = Cursors.Wait;
        try
        {
            WorkspaceSelectedCardsBuildResult support = await _workspaceSelectedCardsBuilder.BuildAsync(
                supportWadPath,
                usedReferences,
                _workspaceCardVariants,
                selections,
                order: 50);

            ModernWadExportOptions options = new(
                dialog.FileName,
                slot,
                deckName,
                _deck.Description);
            ModernWadExportResult deckResult = await Task.Run(() =>
                ModernWadExporter.Export(_deck, BuildCatalogSnapshot(), options));

            _deck.Uid = deckResult.DeckUid;
            _deck.ContentPack = options.IdBlock;
            _deck.Name = deckName;
            SetDirty(true);

            string warningText = support.Warnings.Count == 0
                ? string.Empty
                : $"\n\nWarnings ({support.Warnings.Count}):\n" + string.Join("\n", support.Warnings.Take(8));
            MessageBox.Show(
                this,
                $"Workspace export complete.\n\n" +
                $"Deck WAD:\n{deckResult.WadPath}\n\n" +
                $"Selected-card support WAD (order 50):\n{support.WadPath}\n\n" +
                $"Packaged cards: {support.CardCount:N0}; illustrations: {support.ArtCount:N0}\n" +
                $"Sources: {support.SourcesPath}" + warningText,
                "WAD export complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            Status(
                $"Exported workspace deck {deckResult.DeckUid}: {support.CardCount:N0} selected card definitions " +
                $"and {support.ArtCount:N0} illustrations packaged after variant resolution.");
        }
        catch (Exception exception)
        {
            ShowError("Could not export the workspace deck", exception);
        }
        finally
        {
            Cursor = null;
        }
    }

    private int PreferredWorkspaceSlot()
    {
        if (_deck.Uid is >= 100000 and <= 100099)
        {
            return _deck.Uid - 100000;
        }

        return 0;
    }
}
