using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeckBuilder.Core.Models;
using DeckBuilder.Core.Services;
using DeckBuilder.GameData;
using Microsoft.Win32;

namespace DeckBuilder.Modern;

public partial class MainWindow : Window
{
    private static readonly string BuildIdentifier = ReadBuildIdentifier();

    private readonly GameCardCatalogLoader _catalogLoader = new();
    private readonly GameDeckCatalogLoader _deckCatalogLoader = new();
    private readonly string _settingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DotP2014DeckBuilder");
    private readonly string _settingsPath;

    private DeckDocument _deck = new();
    private DeckEditor _editor;
    private CardSearchIndex _searchIndex = new(Array.Empty<CardRecord>());
    private List<CardRecord> _catalog = new();
    private List<InstalledDeckRecord> _installedDecks = new();
    private GameCardImageLoader? _cardImageLoader;
    private string? _gameDirectory;
    private string? _projectPath;
    private string _projectName = "Untitled deck";
    private bool _dirty;
    private bool _loading;
    private Point _dragStart;
    private int _previewVersion;

    public ObservableCollection<CardRecord> AvailableCards { get; } = new();
    public ObservableCollection<DeckEntry> MainDeckEntries { get; } = new();
    public ObservableCollection<DeckEntry> RegularUnlockEntries { get; } = new();
    public ObservableCollection<DeckEntry> PromoUnlockEntries { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        _editor = new DeckEditor(_deck);
        _settingsPath = Path.Combine(_settingsDirectory, "game-directory.txt");
        DataContext = this;
        Loaded += MainWindow_Loaded;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        UpdateTitle();
        RefreshCollections();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(_settingsPath))
        {
            return;
        }

        string path = (await File.ReadAllTextAsync(_settingsPath)).Trim();
        if (Directory.Exists(path))
        {
            await LoadCatalogAsync(path);
        }
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N)
        {
            NewProject();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O)
        {
            OpenProject();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S)
        {
            _ = SaveProjectAsync(saveAs: false);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SearchBox.IsKeyboardFocusWithin)
        {
            SearchBox.Clear();
            e.Handled = true;
        }
    }

    private void NewProject_Click(object sender, RoutedEventArgs e) => NewProject();

    private async void CreateFromExistingDeck_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_gameDirectory) || !Directory.Exists(_gameDirectory))
        {
            MessageBox.Show(
                "Load the Magic 2014 folder first. Existing decks are read directly from its WAD files.",
                "Game data required",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (_installedDecks.Count == 0)
        {
            MessageBox.Show(
                "No decks were found in the loaded Magic 2014 folder.",
                "No existing decks",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (_dirty && MessageBox.Show(
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

        int slot;
        try
        {
            slot = await Task.Run(() => ModernWadExporter.SuggestSlot(_gameDirectory, -1));
        }
        catch (Exception exception)
        {
            ShowError("Could not choose a free deck UID", exception);
            return;
        }

        int uid = slot >= 0 ? int.Parse($"1000{slot:00}") : -1;
        _deck = DeckDocumentCloner.Clone(dialog.SelectedDeck.Deck, uid, dialog.NewDeckName);
        _editor = new DeckEditor(_deck);
        _projectName = dialog.NewDeckName;
        _projectPath = null;
        MergeDeckCardsIntoCatalog();
        SetDirty(true);
        RefreshCollections();
        Status($"Created a copy of {dialog.SelectedDeck.DisplayName} from {dialog.SelectedDeck.Source}.");
    }

    private void NewProject()
    {
        _deck = new DeckDocument { Name = "Untitled deck" };
        _editor = new DeckEditor(_deck);
        _projectPath = null;
        _projectName = "Untitled deck";
        SetDirty(false);
        RefreshCollections();
        Status("New empty deck created.");
    }

    private void OpenProject_Click(object sender, RoutedEventArgs e) => OpenProject();

    private async void OpenProject()
    {
        OpenFileDialog dialog = new()
        {
            Title = "Open modern deck project",
            Filter = "DotP modern project (*.dotpdeck)|*.dotpdeck|JSON files (*.json)|*.json|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            DeckWorkspace workspace = await DeckWorkspaceSerializer.LoadAsync(dialog.FileName);
            _deck = workspace.Deck;
            _editor = new DeckEditor(_deck);
            _catalog = workspace.Catalog.ToList();
            RebuildSearchIndex();
            _projectPath = dialog.FileName;
            _projectName = string.IsNullOrWhiteSpace(workspace.Name)
                ? Path.GetFileNameWithoutExtension(dialog.FileName)
                : workspace.Name;
            SetDirty(false);
            RefreshCollections();
            Status($"Opened {Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception exception)
        {
            ShowError("Could not open the project", exception);
        }
    }

    private async void SaveProject_Click(object sender, RoutedEventArgs e) => await SaveProjectAsync(saveAs: false);

    private async void SaveProjectAs_Click(object sender, RoutedEventArgs e) => await SaveProjectAsync(saveAs: true);

    private async Task<bool> SaveProjectAsync(bool saveAs)
    {
        string? path = _projectPath;
        if (saveAs || string.IsNullOrWhiteSpace(path))
        {
            SaveFileDialog dialog = new()
            {
                Title = "Save modern deck project",
                Filter = "DotP modern project (*.dotpdeck)|*.dotpdeck",
                DefaultExt = ".dotpdeck",
                AddExtension = true,
                FileName = SanitizeFileName(_projectName)
            };
            if (dialog.ShowDialog(this) != true)
            {
                return false;
            }

            path = dialog.FileName;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(_deck.Name) || _deck.Name == "Untitled deck")
            {
                _deck.Name = _projectName;
            }

            DeckWorkspace workspace = new(_projectName, _deck, BuildCatalogSnapshot());
            await DeckWorkspaceSerializer.SaveAsync(path, workspace);
            _projectPath = path;
            SetDirty(false);
            Status($"Saved {Path.GetFileName(path)}.");
            return true;
        }
        catch (Exception exception)
        {
            ShowError("Could not save the project", exception);
            return false;
        }
    }

    private void ImportDeckXml_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new()
        {
            Title = "Import DotP deck XML",
            Filter = "DotP deck XML (*.xml)|*.xml|All files (*.*)|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            DeckDocument imported = new();
            foreach (string fileName in dialog.FileNames)
            {
                MergeImportedDeck(imported, DotpDeckXmlSerializer.Load(fileName, _catalog));
            }

            _deck = imported;
            _editor = new DeckEditor(_deck);
            MergeDeckCardsIntoCatalog();
            _projectName = string.IsNullOrWhiteSpace(_deck.Name)
                ? Path.GetFileNameWithoutExtension(dialog.FileNames[0]) ?? "Imported deck"
                : _deck.Name;
            _projectPath = null;
            SetDirty(true);
            RefreshCollections();
            Status(dialog.FileNames.Length == 1
                ? $"Imported {Path.GetFileName(dialog.FileNames[0])}."
                : $"Imported {dialog.FileNames.Length} related deck XML files.");
        }
        catch (Exception exception)
        {
            ShowError("Could not import the deck XML", exception);
        }
    }

    private static void MergeImportedDeck(DeckDocument target, DeckDocument source)
    {
        if (source.Uid >= 0 || source.MainDeck.Count > 0)
        {
            CopyDeckMetadata(source, target);
        }

        foreach (DeckEntry entry in source.MainDeck)
        {
            DeckEntry? existing = target.MainDeck.FirstOrDefault(current =>
                current.CardReference.Equals(entry.CardReference, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                target.MainDeck.Add(new DeckEntry(entry.Card, entry.Quantity, entry.Bias, entry.Promo, entry.OrderId));
            }
            else
            {
                existing.Quantity += entry.Quantity;
            }
        }

        foreach (DeckEntry entry in source.RegularUnlocks)
        {
            target.RegularUnlocks.Add(new DeckEntry(entry.Card, 1, entry.Bias, entry.Promo, entry.OrderId));
        }

        foreach (DeckEntry entry in source.PromoUnlocks)
        {
            target.PromoUnlocks.Add(new DeckEntry(entry.Card, 1, entry.Bias, entry.Promo, entry.OrderId));
        }
    }

    private void ExportDeckXml_Click(object sender, RoutedEventArgs e)
    {
        SaveFileDialog dialog = new()
        {
            Title = "Export DotP deck XML",
            Filter = "DotP deck XML (*.xml)|*.xml",
            DefaultExt = ".xml",
            AddExtension = true,
            FileName = SanitizeFileName(_projectName)
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            DotpDeckXmlSerializer.Save(dialog.FileName, _deck);
            Status($"Exported {Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception exception)
        {
            ShowError("Could not export the deck XML", exception);
        }
    }

    private async void ExportGameWad_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_gameDirectory) || !Directory.Exists(_gameDirectory))
        {
            MessageBox.Show(
                "Load the Magic 2014 folder before exporting a game WAD.",
                "Game data required",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (_deck.MainDeckCardCount == 0)
        {
            MessageBox.Show(
                "Add at least one card to the main deck before exporting.",
                "Empty deck",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        int slot;
        try
        {
            slot = await Task.Run(() => ModernWadExporter.SuggestSlot(_gameDirectory, _deck.Uid));
        }
        catch (Exception exception)
        {
            ShowError("Could not inspect existing deck slots", exception);
            return;
        }

        if (slot < 0)
        {
            MessageBox.Show(
                "All 100 default custom deck slots appear to be occupied.",
                "No free deck slot",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        string deckName = string.IsNullOrWhiteSpace(_deck.Name) || _deck.Name == "Untitled deck"
            ? _projectName
            : _deck.Name;
        int deckUid = int.Parse($"1000{slot:00}");
        string codeName = SanitizeWadCodeName(deckName);
        SaveFileDialog dialog = new()
        {
            Title = "Export Magic 2014 game WAD",
            Filter = "Magic 2014 WAD (*.wad)|*.wad",
            DefaultExt = ".wad",
            AddExtension = true,
            InitialDirectory = _gameDirectory,
            FileName = $"Data_Decks_{deckUid}_{codeName}.wad"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        MessageBoxResult confirmation = MessageBox.Show(
            $"Export '{deckName}' to slot {slot:00} (deck UID {deckUid})?\n\n" +
            $"Target: {dialog.FileName}\n\n" +
            "If the target already exists, it will be backed up before replacement.",
            "Confirm game WAD export",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            ModernWadExportOptions options = new(
                dialog.FileName,
                slot,
                deckName,
                _deck.Description);
            ModernWadExportResult result = await Task.Run(() =>
                ModernWadExporter.Export(_deck, BuildCatalogSnapshot(), options));
            _deck.Uid = result.DeckUid;
            _deck.ContentPack = options.IdBlock;
            _deck.Name = deckName;
            SetDirty(true);
            Status($"Exported {Path.GetFileName(result.WadPath)} for deck UID {result.DeckUid}.");

            string backup = result.BackupPath is null
                ? "No previous WAD needed a backup."
                : $"Backup: {result.BackupPath}";
            string enabler = result.ContentPackEnablerCreated
                ? $"Content-pack enabler created: {result.ContentPackEnablerPath}"
                : $"Existing content-pack enabler kept: {result.ContentPackEnablerPath}";
            MessageBox.Show(
                $"Game WAD exported and verified.\n\n{result.WadPath}\n\n{backup}\n{enabler}",
                "WAD export complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            ShowError("Could not export the game WAD", exception);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private async void LoadGameFolder_Click(object sender, RoutedEventArgs e)
    {
        OpenFolderDialog dialog = new()
        {
            Title = "Select the Magic 2014 game folder",
            Multiselect = false,
            InitialDirectory = _gameDirectory
        };
        if (dialog.ShowDialog(this) == true)
        {
            await LoadCatalogAsync(dialog.FolderName);
        }
    }

    private async void ReloadGameFolder_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_gameDirectory) || !Directory.Exists(_gameDirectory))
        {
            LoadGameFolder_Click(sender, e);
            return;
        }

        await LoadCatalogAsync(_gameDirectory);
    }

    private async Task LoadCatalogAsync(string path)
    {
        if (_loading)
        {
            return;
        }

        _loading = true;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            Progress<CatalogLoadProgress> progress = new(value =>
                Status($"Loading {value.Source} — {value.CardsLoaded:N0} cards…"));
            CatalogLoadResult result = await _catalogLoader.LoadAsync(path, progress);
            _catalog = result.Cards.ToList();
            Status($"Loading installed decks from {path}…");
            GameDeckCatalogLoadResult deckResult = await _deckCatalogLoader.LoadAsync(path, result.Cards);
            _installedDecks = deckResult.Decks.ToList();
            _cardImageLoader = new GameCardImageLoader(path);
            RebindDeckCardsToCatalog();
            RebuildSearchIndex();
            _gameDirectory = path;
            Directory.CreateDirectory(_settingsDirectory);
            await File.WriteAllTextAsync(_settingsPath, path);

            int warningCount = result.Warnings.Count + deckResult.Warnings.Count;
            string warningText = warningCount == 0
                ? string.Empty
                : $" ({warningCount} files skipped)";
            Status($"Loaded {_catalog.Count:N0} cards and {_installedDecks.Count:N0} decks from {path}{warningText}.");
        }
        catch (Exception exception)
        {
            ShowError("Could not load Magic 2014 game data", exception);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            _loading = false;
            RefreshCollections();
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ShowSearchResults();

    private void ShowSearchResults()
    {
        IReadOnlyList<CardRecord> result = _searchIndex.Search(SearchBox.Text);
        AvailableCards.Clear();
        foreach (CardRecord card in result.Take(5000))
        {
            AvailableCards.Add(card);
        }

        UpdateCounts();
    }

    private void AvailableCardsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        AddSelectedCards(DeckSection.MainDeck);
    }

    private async void CardGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CardRecord? card = sender switch
        {
            DataGrid grid when ReferenceEquals(grid, AvailableCardsGrid) => grid.SelectedItem as CardRecord,
            DataGrid grid => (grid.SelectedItem as DeckEntry)?.Card,
            _ => null
        };
        if (card is not null)
        {
            await ShowCardPreviewAsync(card);
        }
    }

    private async Task ShowCardPreviewAsync(CardRecord card)
    {
        int version = ++_previewVersion;
        CardPreviewImage.Source = null;
        PreviewFrameImage.Source = null;
        PreviewPowerBoxImage.Source = null;
        PreviewRarityImage.Source = null;
        PreviewCreditImage.Source = null;
        PreviewManaPanel.Children.Clear();
        CardPreviewViewbox.Visibility = Visibility.Collapsed;
        PreviewName.Text = string.IsNullOrWhiteSpace(card.LocalizedName) ? card.FileName : card.LocalizedName;
        PreviewType.Text = string.Join(" · ", new[] { card.CastingCost, card.TypeLine }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        PreviewStats.Text = BuildCardStats(card);
        PreviewSource.Text = string.IsNullOrWhiteSpace(card.Source)
            ? card.FileName
            : $"{card.Expansion} · {card.Source} · {card.FileName}";

        if (_cardImageLoader is null || string.IsNullOrWhiteSpace(card.ImageId))
        {
            PreviewPlaceholder.Text = "No card art reference was found";
            PreviewPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        PreviewPlaceholder.Text = "Loading card art…";
        PreviewPlaceholder.Visibility = Visibility.Visible;
        try
        {
            CardVisualSpec visual = CardVisualMetadata.FromCard(card);
            Task<PreviewArtLookup> artTask = PreviewArtResolver.ResolveAsync(_cardImageLoader, card);
            Task<CardImageData?> frameTask = _cardImageLoader.LoadAsync(visual.FrameId, GameImageKind.Frame);
            Task<CardImageData?> powerBoxTask = _cardImageLoader.LoadAsync(visual.PowerBoxId, GameImageKind.Texture);
            Task<CardImageData?> creditTask = _cardImageLoader.LoadAsync(visual.CreditId, GameImageKind.Texture);
            Task<CardImageData?> rarityTask = _cardImageLoader.LoadAsync(visual.RarityId, GameImageKind.Texture);
            await Task.WhenAll(artTask, frameTask, powerBoxTask, creditTask, rarityTask);
            if (version != _previewVersion)
            {
                return;
            }

            PreviewArtLookup artLookup = await artTask;
            if (artLookup.Image is null)
            {
                string tried = artLookup.TriedIds.Count == 0
                    ? "no exact identifiers"
                    : string.Join(", ", artLookup.TriedIds.Take(6));
                PreviewPlaceholder.Text = $"Art {card.ImageId} was not found\nTried: {tried}";
                return;
            }

            ApplyCardPreview(
                card,
                visual,
                artLookup.Image,
                await frameTask,
                await powerBoxTask,
                await creditTask,
                await rarityTask);
            CardPreviewViewbox.Visibility = Visibility.Visible;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            if (artLookup.UsedAlternateName)
            {
                Status($"Art {card.ImageId} resolved from exact TDX identifier {artLookup.ResolvedId}.");
            }
        }
        catch (Exception exception)
        {
            if (version == _previewVersion)
            {
                string details = DescribeException(exception);
                PreviewPlaceholder.Text = $"Could not load art\n{details}";
                Status($"Could not load art {card.ImageId}: {details}");
            }
        }
    }

    private void ApplyCardPreview(
        CardRecord card,
        CardVisualSpec visual,
        CardImageData art,
        CardImageData? frame,
        CardImageData? powerBox,
        CardImageData? credit,
        CardImageData? rarity)
    {
        CardPreviewImage.Source = ToBitmapSource(art);
        Canvas.SetLeft(CardPreviewImage, visual.FullBleedArt ? 0 : 16);
        Canvas.SetTop(CardPreviewImage, visual.FullBleedArt ? 0 : 47);
        CardPreviewImage.Width = visual.FullBleedArt ? 356 : 324;
        CardPreviewImage.Height = visual.FullBleedArt ? 512 : 238;
        Panel.SetZIndex(CardPreviewImage, visual.FullBleedArt ? 0 : 2);

        PreviewFrameImage.Source = ToBitmapSource(frame, rotateLandscape: true);
        PreviewFrameFallback.Fill = FrameFallbackBrush(visual.FrameId);
        PreviewPowerBoxImage.Source = ToBitmapSource(powerBox);
        PreviewCreditImage.Source = ToBitmapSource(credit);
        PreviewRarityImage.Source = ToBitmapSource(rarity);

        PreviewCardTitle.Text = string.IsNullOrWhiteSpace(card.LocalizedName) ? card.FileName : card.LocalizedName;
        PreviewCardTypeLine.Text = card.TypeLine;
        PreviewPowerText.Text = visual.ShowsPower ? $"{card.Power} / {card.Toughness}" : string.Empty;
        PreviewArtistText.Text = card.Artist;
        PreviewArtistText.Foreground = visual.CreditId == "CREDIT_WHITE" ? Brushes.White : Brushes.Black;
        PreviewPowerBoxImage.Visibility = visual.ShowsPower && powerBox is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        PreviewPowerBoxFallback.Visibility = visual.ShowsPower && powerBox is null
            ? Visibility.Visible
            : Visibility.Collapsed;
        PreviewPowerText.Visibility = visual.ShowsPower ? Visibility.Visible : Visibility.Collapsed;

        bool tokenLayout = card.IsToken;
        Canvas.SetTop(PreviewCardTypeLine, tokenLayout ? 348 : 294);
        Canvas.SetTop(PreviewRulesText, tokenLayout ? 383 : 324);
        PreviewRulesText.Height = tokenLayout ? 87 : 150;
        BuildRulesText(card);

        int renderedMana = CardSymbolRenderer.RenderCastingCost(PreviewManaPanel, visual.ManaImageIds);
        double manaWidth = renderedMana * CardSymbolRenderer.PreviewSymbolAdvance;
        Canvas.SetLeft(PreviewManaPanel, Math.Max(12, 336 - manaWidth));
        PreviewCardTitle.Width = Math.Max(130, 330 - manaWidth);
    }

    private void BuildRulesText(CardRecord card) =>
        CardSymbolRenderer.RenderRules(PreviewRulesText.Inlines, card.RulesText, card.FlavorText);

    private static ImageSource? ToBitmapSource(CardImageData? image, bool rotateLandscape = false)
    {
        if (image is null)
        {
            return null;
        }

        BitmapSource source = BitmapSource.Create(
            image.Width,
            image.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            image.BgraPixels,
            checked(image.Width * 4));
        source.Freeze();
        if (!rotateLandscape || image.Width <= image.Height)
        {
            return source;
        }

        TransformedBitmap rotated = new(source, new RotateTransform(270));
        rotated.Freeze();
        return rotated;
    }

    private static Brush FrameFallbackBrush(string frameId)
    {
        string frame = frameId.ToUpperInvariant();
        if (frame is "Z" || frame.StartsWith("BG", StringComparison.Ordinal)
            || frame.StartsWith("BR", StringComparison.Ordinal) || frame.StartsWith("UB", StringComparison.Ordinal)
            || frame.StartsWith("UG", StringComparison.Ordinal) || frame.StartsWith("UR", StringComparison.Ordinal)
            || frame.StartsWith("WB", StringComparison.Ordinal) || frame.StartsWith("WG", StringComparison.Ordinal)
            || frame.StartsWith("WR", StringComparison.Ordinal) || frame.StartsWith("WU", StringComparison.Ordinal)
            || frame.StartsWith("RG", StringComparison.Ordinal))
        {
            return new SolidColorBrush(Color.FromRgb(196, 158, 66));
        }

        return frame[0] switch
        {
            'B' => new SolidColorBrush(Color.FromRgb(95, 88, 82)),
            'U' => new SolidColorBrush(Color.FromRgb(126, 181, 211)),
            'G' => new SolidColorBrush(Color.FromRgb(121, 164, 116)),
            'R' => new SolidColorBrush(Color.FromRgb(190, 104, 82)),
            'W' => new SolidColorBrush(Color.FromRgb(230, 220, 185)),
            _ => new SolidColorBrush(Color.FromRgb(190, 186, 174))
        };
    }

    private static string DescribeException(Exception exception)
    {
        List<string> chain = new();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            string item = string.IsNullOrWhiteSpace(current.Message)
                ? current.GetType().Name
                : $"{current.GetType().Name}: {current.Message}";
            if (chain.Count == 0 || !chain[^1].Equals(item, StringComparison.Ordinal))
            {
                chain.Add(item);
            }
        }

        return string.Join(" → ", chain);
    }

    private static string BuildCardStats(CardRecord card)
    {
        List<string> values = new();
        if (!string.IsNullOrWhiteSpace(card.Power) || !string.IsNullOrWhiteSpace(card.Toughness))
        {
            values.Add($"{card.Power}/{card.Toughness}");
        }

        if (!string.IsNullOrWhiteSpace(card.Rarity))
        {
            values.Add($"Rarity: {card.Rarity}");
        }

        if (!string.IsNullOrWhiteSpace(card.Artist))
        {
            values.Add($"Artist: {card.Artist}");
        }

        return string.Join(" · ", values);
    }

    private void AddToDeck_Click(object sender, RoutedEventArgs e) => AddSelectedCards(DeckSection.MainDeck);

    private void AddRegularUnlock_Click(object sender, RoutedEventArgs e) => AddSelectedCards(DeckSection.RegularUnlocks);

    private void AddPromoUnlock_Click(object sender, RoutedEventArgs e) => AddSelectedCards(DeckSection.PromoUnlocks);

    private void AddSelectedCards(DeckSection target)
    {
        IReadOnlyList<CardRecord> cards = AvailableCardsGrid.SelectedItems.Cast<CardRecord>().ToArray();
        if (cards.Count == 0)
        {
            return;
        }

        try
        {
            foreach (CardRecord card in cards)
            {
                _editor.Add(card, target);
            }

            Changed($"Added {cards.Count} card(s).");
        }
        catch (Exception exception)
        {
            ShowError("Could not add the selected cards", exception);
        }
    }

    private void AddDeckCopy_Click(object sender, RoutedEventArgs e)
    {
        foreach (DeckEntry entry in MainDeckGrid.SelectedItems.Cast<DeckEntry>().ToArray())
        {
            _editor.Add(entry.Card, DeckSection.MainDeck, entry.Bias, entry.Promo);
        }

        Changed("Added another copy.");
    }

    private void RemoveMain_Click(object sender, RoutedEventArgs e) => RemoveSelected(MainDeckGrid, DeckSection.MainDeck);

    private void RemoveRegular_Click(object sender, RoutedEventArgs e) => RemoveSelected(RegularUnlocksGrid, DeckSection.RegularUnlocks);

    private void RemovePromo_Click(object sender, RoutedEventArgs e) => RemoveSelected(PromoUnlocksGrid, DeckSection.PromoUnlocks);

    private void RemoveSelected(DataGrid grid, DeckSection section)
    {
        DeckEntry[] entries = grid.SelectedItems.Cast<DeckEntry>().ToArray();
        foreach (DeckEntry entry in entries)
        {
            _editor.Remove(entry, section, int.MaxValue);
        }

        if (entries.Length > 0)
        {
            Changed($"Removed {entries.Length} row(s).");
        }
    }

    private void MoveMainToUnlock_Click(object sender, RoutedEventArgs e)
    {
        MoveEntries(MainDeckGrid.SelectedItems.Cast<DeckEntry>().ToArray(), DeckSection.MainDeck, DeckSection.RegularUnlocks);
    }

    private void MoveEntries(IReadOnlyList<DeckEntry> entries, DeckSection source, DeckSection target)
    {
        try
        {
            foreach (DeckEntry entry in entries)
            {
                _editor.Move(entry, source, target);
            }

            if (entries.Count > 0)
            {
                Changed($"Moved {entries.Count} card(s).");
            }
        }
        catch (Exception exception)
        {
            ShowError("Could not move the selected cards", exception);
        }
    }

    private void RegularUp_Click(object sender, RoutedEventArgs e) => MoveUnlock(RegularUnlocksGrid, DeckSection.RegularUnlocks, -1);

    private void RegularDown_Click(object sender, RoutedEventArgs e) => MoveUnlock(RegularUnlocksGrid, DeckSection.RegularUnlocks, 1);

    private void PromoUp_Click(object sender, RoutedEventArgs e) => MoveUnlock(PromoUnlocksGrid, DeckSection.PromoUnlocks, -1);

    private void PromoDown_Click(object sender, RoutedEventArgs e) => MoveUnlock(PromoUnlocksGrid, DeckSection.PromoUnlocks, 1);

    private void MoveUnlock(DataGrid grid, DeckSection section, int direction)
    {
        if (grid.SelectedItem is not DeckEntry entry)
        {
            return;
        }

        IList<DeckEntry> entries = _deck.GetSection(section);
        int oldIndex = entries.IndexOf(entry);
        int newIndex = Math.Clamp(oldIndex + direction, 0, entries.Count - 1);
        if (oldIndex == newIndex)
        {
            return;
        }

        _editor.Reorder(entry, section, newIndex);
        Changed("Unlock order updated.");
        grid.SelectedItem = entry;
        grid.ScrollIntoView(entry);
    }

    private void SectionGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || sender is not DataGrid grid || grid.Tag is not string sectionName)
        {
            return;
        }

        RemoveSelected(grid, Enum.Parse<DeckSection>(sectionName));
        e.Handled = true;
    }

    private void Grid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
    }

    private void Grid_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        Point current = e.GetPosition(null);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (sender is not DataGrid grid)
        {
            return;
        }

        DragPayload? payload = grid == AvailableCardsGrid
            ? new DragPayload(grid.SelectedItems.Cast<CardRecord>().ToArray(), null, null)
            : grid.Tag is string sectionName
                ? new DragPayload(null, grid.SelectedItems.Cast<DeckEntry>().ToArray(), Enum.Parse<DeckSection>(sectionName))
                : null;
        if (payload is null || payload.Count == 0)
        {
            return;
        }

        DragDrop.DoDragDrop(grid, payload, payload.Source is null ? DragDropEffects.Copy : DragDropEffects.Move);
    }

    private void SectionGrid_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(DragPayload))
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void SectionGrid_Drop(object sender, DragEventArgs e)
    {
        if (sender is not DataGrid grid
            || grid.Tag is not string sectionName
            || e.Data.GetData(typeof(DragPayload)) is not DragPayload payload)
        {
            return;
        }

        DeckSection target = Enum.Parse<DeckSection>(sectionName);
        try
        {
            if (payload.Cards is not null)
            {
                foreach (CardRecord card in payload.Cards)
                {
                    _editor.Add(card, target);
                }
            }
            else if (payload.Entries is not null && payload.Source is DeckSection source)
            {
                if (source == target)
                {
                    DeckEntry? rowEntry = FindAncestor<DataGridRow>((DependencyObject)e.OriginalSource)?.Item as DeckEntry;
                    int targetIndex = rowEntry is null ? _deck.GetSection(target).Count - 1 : _deck.GetSection(target).IndexOf(rowEntry);
                    foreach (DeckEntry entry in payload.Entries)
                    {
                        _editor.Reorder(entry, target, targetIndex++);
                    }
                }
                else
                {
                    foreach (DeckEntry entry in payload.Entries)
                    {
                        _editor.Move(entry, source, target);
                    }
                }
            }

            Changed($"Dropped {payload.Count} card(s).");
        }
        catch (Exception exception)
        {
            ShowError("Could not drop the selected cards", exception);
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void RebindDeckCardsToCatalog()
    {
        Dictionary<string, CardRecord> cards = _catalog.ToDictionary(card => card.FileName, StringComparer.OrdinalIgnoreCase);
        DeckDocument rebound = new();
        CopyDeckMetadata(_deck, rebound);
        CopySection(_deck.MainDeck, rebound.MainDeck, cards);
        CopySection(_deck.RegularUnlocks, rebound.RegularUnlocks, cards);
        CopySection(_deck.PromoUnlocks, rebound.PromoUnlocks, cards);
        _deck = rebound;
        _editor = new DeckEditor(_deck);
    }

    private static void CopyDeckMetadata(DeckDocument source, DeckDocument target)
    {
        target.Uid = source.Uid;
        target.Name = source.Name;
        target.Description = source.Description;
        target.Personality = source.Personality;
        target.DeckBoxImage = source.DeckBoxImage;
        target.DeckBoxImageLocked = source.DeckBoxImageLocked;
        target.ContentPack = source.ContentPack;
        target.AlwaysAvailable = source.AlwaysAvailable;
    }

    private static void CopySection(IEnumerable<DeckEntry> source, IList<DeckEntry> target, IDictionary<string, CardRecord> cards)
    {
        foreach (DeckEntry entry in source)
        {
            CardRecord card = cards.TryGetValue(entry.Card.FileName, out CardRecord? current) ? current : entry.Card;
            target.Add(new DeckEntry(card, entry.Quantity, entry.Bias, entry.Promo, entry.OrderId));
        }
    }

    private void MergeDeckCardsIntoCatalog()
    {
        Dictionary<string, CardRecord> merged = _catalog.ToDictionary(card => card.FileName, StringComparer.OrdinalIgnoreCase);
        foreach (DeckEntry entry in _deck.MainDeck.Concat(_deck.RegularUnlocks).Concat(_deck.PromoUnlocks))
        {
            merged.TryAdd(entry.Card.FileName, entry.Card);
        }

        _catalog = merged.Values.OrderBy(card => card.LocalizedName, StringComparer.CurrentCultureIgnoreCase).ToList();
        RebuildSearchIndex();
    }

    private IReadOnlyList<CardRecord> BuildCatalogSnapshot()
    {
        Dictionary<string, CardRecord> result = _catalog.ToDictionary(card => card.FileName, StringComparer.OrdinalIgnoreCase);
        foreach (DeckEntry entry in _deck.MainDeck.Concat(_deck.RegularUnlocks).Concat(_deck.PromoUnlocks))
        {
            result.TryAdd(entry.Card.FileName, entry.Card);
        }

        return result.Values.ToArray();
    }

    private void RebuildSearchIndex()
    {
        _searchIndex = new CardSearchIndex(_catalog);
        ShowSearchResults();
    }

    private void RefreshCollections()
    {
        Replace(MainDeckEntries, _deck.MainDeck);
        Replace(RegularUnlockEntries, _deck.RegularUnlocks);
        Replace(PromoUnlockEntries, _deck.PromoUnlocks);
        UpdateOrderIds(_deck.RegularUnlocks);
        UpdateOrderIds(_deck.PromoUnlocks);
        MainDeckGrid.Items.Refresh();
        RegularUnlocksGrid.Items.Refresh();
        PromoUnlocksGrid.Items.Refresh();
        UpdateCounts();
    }

    private static void Replace(ObservableCollection<DeckEntry> target, IEnumerable<DeckEntry> source)
    {
        target.Clear();
        foreach (DeckEntry entry in source)
        {
            target.Add(entry);
        }
    }

    private static void UpdateOrderIds(IList<DeckEntry> entries)
    {
        for (int index = 0; index < entries.Count; index++)
        {
            entries[index].OrderId = index;
        }
    }

    private void Changed(string message)
    {
        SetDirty(true);
        RefreshCollections();
        Status(message);
    }

    private void SetDirty(bool value)
    {
        _dirty = value;
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        Title = $"{_projectName}{(_dirty ? " *" : string.Empty)} — DotP 2014 Deck Builder Modern — {BuildIdentifier}";
    }

    private void UpdateCounts()
    {
        CountText.Text = $"Cards: {_catalog.Count:N0} | Shown: {AvailableCards.Count:N0} | Deck: {_deck.MainDeckCardCount} | Unlocks: {_deck.RegularUnlocks.Count} + {_deck.PromoUnlocks.Count}";
    }

    private void Status(string message) => StatusText.Text = message;

    private static string SanitizeFileName(string value)
    {
        string result = value;
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            result = result.Replace(invalid, '_');
        }

        return string.IsNullOrWhiteSpace(result) ? "deck" : result;
    }

    private static string SanitizeWadCodeName(string value)
    {
        string result = new(value
            .Trim()
            .ToUpperInvariant()
            .Select(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9' ? character : '_')
            .ToArray());
        while (result.Contains("__", StringComparison.Ordinal))
        {
            result = result.Replace("__", "_", StringComparison.Ordinal);
        }

        result = result.Trim('_');
        return result.Length == 0 ? "CUSTOM_DECK" : result;
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private static string ReadBuildIdentifier()
    {
        string? value = typeof(MainWindow).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "BuildIdentifier")
            ?.Value;
        return string.IsNullOrWhiteSpace(value) ? "local build" : value;
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            $"DotP 2014 Deck Builder Modern\n{BuildIdentifier}\n\n.NET 8 WPF preview with native WAD card loading, indexed search and a UI-independent deck core.",
            "About",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void ShowError(string title, Exception exception)
    {
        Status($"{title}: {exception.Message}");
        MessageBox.Show(exception.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private sealed record DragPayload(
        IReadOnlyList<CardRecord>? Cards,
        IReadOnlyList<DeckEntry>? Entries,
        DeckSection? Source)
    {
        public int Count => Cards?.Count ?? Entries?.Count ?? 0;
    }
}
