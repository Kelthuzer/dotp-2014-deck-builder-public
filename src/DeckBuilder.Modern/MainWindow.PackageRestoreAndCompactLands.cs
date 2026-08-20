using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DeckBuilder.Core.Models;
using DeckBuilder.Core.Services;
using DeckBuilder.GameData;
using Microsoft.Win32;

namespace DeckBuilder.Modern;

public partial class MainWindow
{
    private bool _packageAwareOpenInstalled;
    private bool _compactAutoLandInstalled;

    internal void InstallPackageRestoreAndCompactLands()
    {
        InstallPackageAwareOpen();
        InstallCompactAutoLands();
    }

    private void InstallPackageAwareOpen()
    {
        if (_packageAwareOpenInstalled || Content is not DockPanel root)
            return;

        Menu? menu = root.Children.OfType<Menu>().FirstOrDefault();
        MenuItem? fileMenu = menu?.Items.OfType<MenuItem>().FirstOrDefault();
        MenuItem? openItem = fileMenu?.Items.OfType<MenuItem>()
            .FirstOrDefault(item => string.Equals(
                item.InputGestureText,
                "Ctrl+O",
                StringComparison.OrdinalIgnoreCase));
        if (openItem is null)
            return;

        openItem.Click -= OpenProject_Click;
        openItem.Click += OpenProjectOrPackagedDeck_Click;
        openItem.Header = AppLocalization.IsRussian
            ? "_Открыть проект / упакованную колоду…"
            : "_Open project / packaged deck…";

        PreviewKeyDown -= MainWindow_PreviewKeyDown;
        PreviewKeyDown += MainWindow_PackageAwarePreviewKeyDown;
        _packageAwareOpenInstalled = true;
    }

    private void InstallCompactAutoLands()
    {
        if (_compactAutoLandInstalled || _deckAssistantAutoLandButton is null)
            return;

        _deckAssistantAutoLandButton.Click -= AutoFillLands_Click;
        _deckAssistantAutoLandButton.Click += AutoFillLandsCompact_Click;
        _compactAutoLandInstalled = true;
    }

    private void MainWindow_PackageAwarePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O)
        {
            OpenProjectOrPackagedDeck();
            e.Handled = true;
            return;
        }

        MainWindow_PreviewKeyDown(sender, e);
    }

    private void OpenProjectOrPackagedDeck_Click(object sender, RoutedEventArgs e) =>
        OpenProjectOrPackagedDeck();

    private async void OpenProjectOrPackagedDeck()
    {
        bool ru = AppLocalization.IsRussian;
        OpenFileDialog dialog = new()
        {
            Title = ru ? "Открыть проект или упакованную колоду" : "Open project or packaged deck",
            Filter =
                "DotP modern project (*.dotpdeck)|*.dotpdeck|" +
                "Magic 2014 deck/package WAD (*.wad)|*.wad|" +
                "Deck package manifest (*.wad.sources.json)|*.wad.sources.json|" +
                "JSON files (*.json)|*.json|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        if (_dirty && MessageBox.Show(
                this,
                ru
                    ? "Открытие заменит текущий несохранённый проект. Продолжить?"
                    : "Opening this file will replace the current unsaved project. Continue?",
                ru ? "Несохранённые изменения" : "Unsaved work",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        string selectedPath = Path.GetFullPath(dialog.FileName);
        try
        {
            if (IsSourcesManifest(selectedPath) ||
                Path.GetExtension(selectedPath).Equals(".wad", StringComparison.OrdinalIgnoreCase))
            {
                string deckWadPath = ResolvePackagedDeckWadPath(selectedPath);
                await RestoreDeckFromWadAsync(deckWadPath, selectedPath);
                return;
            }

            DeckWorkspace workspace = await DeckWorkspaceSerializer.LoadAsync(selectedPath);
            _deck = workspace.Deck;
            _editor = new DeckEditor(_deck);
            _catalog = workspace.Catalog.ToList();
            RebuildSearchIndex();
            _projectPath = selectedPath;
            _projectName = string.IsNullOrWhiteSpace(workspace.Name)
                ? Path.GetFileNameWithoutExtension(selectedPath)
                : workspace.Name;
            SetDirty(false);
            RefreshCollections();
            Status(ru
                ? $"Открыт проект {Path.GetFileName(selectedPath)}."
                : $"Opened {Path.GetFileName(selectedPath)}.");
        }
        catch (Exception exception)
        {
            ShowError(ru ? "Не удалось открыть колоду" : "Could not open the deck", exception);
        }
    }

    private async Task RestoreDeckFromWadAsync(string deckWadPath, string selectedArtifactPath)
    {
        bool ru = AppLocalization.IsRussian;
        string directory = Path.GetDirectoryName(deckWadPath)
            ?? throw new DirectoryNotFoundException(ru
                ? "Не удалось определить папку выбранного WAD."
                : "Could not determine the selected WAD directory.");

        Status(ru
            ? $"Восстановление: читаю WAD-комплект из {directory}…"
            : $"Restore: reading the WAD package from {directory}…");
        Cursor = Cursors.Wait;
        try
        {
            Dictionary<string, CardRecord> mergedCatalog = BuildCatalogSnapshot()
                .Where(card => !string.IsNullOrWhiteSpace(card.FileName))
                .GroupBy(card => card.FileName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            CatalogLoadResult localCatalog = await _catalogLoader.LoadAsync(directory);
            foreach (CardRecord card in localCatalog.Cards)
            {
                if (!string.IsNullOrWhiteSpace(card.FileName))
                    mergedCatalog[card.FileName] = card;
            }

            CardRecord[] catalog = mergedCatalog.Values.ToArray();
            GameDeckCatalogLoadResult deckResult = await _deckCatalogLoader.LoadAsync(directory, catalog);
            string source = Path.GetFileNameWithoutExtension(deckWadPath) ?? string.Empty;
            InstalledDeckRecord[] matches = deckResult.Decks
                .Where(deck => deck.Source.Equals(source, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (matches.Length == 0)
            {
                throw new InvalidDataException(ru
                    ? $"{Path.GetFileName(deckWadPath)} не содержит редактируемого DECK XML. Для восстановления нужен Data_Decks_*.wad, а не только Cards/runtime WAD."
                    : $"{Path.GetFileName(deckWadPath)} contains no editable DECK XML. Restore requires a Data_Decks_*.wad, not only a Cards/runtime WAD.");
            }

            InstalledDeckRecord selected = SelectDeckFromWad(matches, deckWadPath);
            _deck = DeckDocumentCloner.Clone(selected.Deck, selected.Deck.Uid, selected.DisplayName);
            _editor = new DeckEditor(_deck);
            _catalog = catalog
                .OrderBy(card => card.LocalizedName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(card => card.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _projectName = selected.DisplayName;
            _projectPath = null;
            _cardImageLoader = new GameCardImageLoader(directory);
            MergeDeckCardsIntoCatalog();
            SetDirty(true);
            RefreshCollections();

            Status(ru
                ? $"Восстановлена колода {selected.DisplayName} из {Path.GetFileName(deckWadPath)}. Сохрани её как .dotpdeck, если нужен отдельный проект."
                : $"Restored {selected.DisplayName} from {Path.GetFileName(deckWadPath)}. Save it as .dotpdeck if you want a standalone project.");

            if (selected.MissingCardCount > 0)
            {
                string shown = string.Join("\n", selected.MissingCardReferences.Take(20).Select(reference => "• " + reference));
                string more = selected.MissingCardCount > 20
                    ? $"\n…{(ru ? "и ещё" : "and") } {selected.MissingCardCount - 20}."
                    : string.Empty;
                MessageBox.Show(
                    this,
                    (ru
                        ? "Колода восстановлена, но рядом не найдены определения некоторых карт. Вероятно, отсутствует соответствующий Cards WAD.\n\n"
                        : "The deck was restored, but some card definitions were not found nearby. The matching Cards WAD is probably missing.\n\n") +
                    shown + more,
                    ru ? "Неполный комплект" : "Incomplete package",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            else if (!string.Equals(selectedArtifactPath, deckWadPath, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(
                    this,
                    ru
                        ? $"Манифест/служебный WAD распознан. Колода автоматически восстановлена из соседнего файла:\n\n{Path.GetFileName(deckWadPath)}"
                        : $"The manifest/support WAD was recognized. The deck was restored automatically from the sibling file:\n\n{Path.GetFileName(deckWadPath)}",
                    ru ? "Колода восстановлена" : "Deck restored",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        finally
        {
            Cursor = null;
        }
    }

    private static InstalledDeckRecord SelectDeckFromWad(
        IReadOnlyList<InstalledDeckRecord> matches,
        string deckWadPath)
    {
        if (matches.Count == 1)
            return matches[0];

        int? uid = TryDeckUidFromWadName(deckWadPath);
        if (uid.HasValue)
        {
            InstalledDeckRecord[] uidMatches = matches.Where(deck => deck.Uid == uid.Value).ToArray();
            if (uidMatches.Length == 1)
                return uidMatches[0];
        }

        throw new InvalidDataException(
            $"{Path.GetFileName(deckWadPath)} contains {matches.Count} deck definitions and the intended deck cannot be selected unambiguously.");
    }

    private static string ResolvePackagedDeckWadPath(string selectedPath)
    {
        string fullPath = Path.GetFullPath(selectedPath);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new DirectoryNotFoundException("The package directory could not be determined.");
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException(directory);

        string artifactName = Path.GetFileName(fullPath);
        string? explicitDeckWad = null;
        string? supportWadName = null;

        if (IsSourcesManifest(fullPath))
        {
            supportWadName = artifactName[..^".sources.json".Length];
            try
            {
                using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(fullPath));
                JsonElement root = manifest.RootElement;
                if (TryGetStringProperty(root, "deckWad", out string? deckWadValue))
                    explicitDeckWad = Path.GetFileName(deckWadValue);
                if (TryGetStringProperty(root, "wad", out string? wadValue))
                    supportWadName = Path.GetFileName(wadValue);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The selected package manifest is not valid JSON.", exception);
            }
        }
        else if (IsSupportCardsWad(artifactName))
        {
            supportWadName = artifactName;
        }
        else
        {
            return fullPath;
        }

        if (!string.IsNullOrWhiteSpace(explicitDeckWad))
        {
            string explicitSibling = Path.Combine(directory, explicitDeckWad);
            if (File.Exists(explicitSibling))
                return explicitSibling;
        }

        if (string.IsNullOrWhiteSpace(supportWadName) || !TryParseSupportCardsWadName(supportWadName, out string uid, out string code))
        {
            throw new InvalidDataException(
                "The package manifest/support WAD name does not contain a recognizable deck UID. " +
                "Select the matching Data_Decks_<UID>_*.wad directly.");
        }

        string exact = Path.Combine(directory, $"Data_Decks_{uid}_{code}.wad");
        if (File.Exists(exact))
            return exact;

        string prefix = $"Data_Decks_{uid}_";
        string[] candidates = Directory.EnumerateFiles(directory, "*.wad", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidates.Length == 1)
            return candidates[0];
        if (candidates.Length > 1)
        {
            throw new InvalidDataException(
                $"Several Data_Decks_{uid}_*.wad files are next to the package manifest. " +
                "Select the intended deck WAD directly so nothing is guessed.");
        }

        throw new FileNotFoundException(
            $"The package manifest describes support resources, not exact deck quantities/sections. " +
            $"The sibling deck WAD was not found. Expected {Path.GetFileName(exact)} (or another Data_Decks_{uid}_*.wad).",
            exact);
    }

    private static bool TryGetStringProperty(JsonElement root, string name, out string? value)
    {
        value = null;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out JsonElement property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool IsSourcesManifest(string path) =>
        path.EndsWith(".wad.sources.json", StringComparison.OrdinalIgnoreCase);

    private static bool IsSupportCardsWad(string fileName) =>
        fileName.StartsWith("Data_DLC_9000_", StringComparison.OrdinalIgnoreCase) &&
        fileName.EndsWith("_Cards.wad", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseSupportCardsWadName(string fileName, out string uid, out string code)
    {
        const string prefix = "Data_DLC_9000_";
        const string suffix = "_Cards.wad";
        uid = string.Empty;
        code = string.Empty;
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
            fileName.Length <= prefix.Length + suffix.Length)
        {
            return false;
        }

        string middle = fileName[prefix.Length..^suffix.Length];
        int separator = middle.IndexOf('_');
        if (separator <= 0 || separator >= middle.Length - 1)
            return false;

        uid = middle[..separator];
        code = middle[(separator + 1)..];
        return uid.All(char.IsDigit) && uid.Length > 0 && code.Length > 0;
    }

    private static int? TryDeckUidFromWadName(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
        const string prefix = "Data_Decks_";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        string remainder = name[prefix.Length..];
        int separator = remainder.IndexOf('_');
        string uidText = separator < 0 ? remainder : remainder[..separator];
        return int.TryParse(uidText, out int uid) ? uid : null;
    }

    private void AutoFillLandsCompact_Click(object sender, RoutedEventArgs e)
    {
        bool ru = AppLocalization.IsRussian;
        int currentLands = _deck.MainDeck.Where(entry => IsLand(entry.Card)).Sum(entry => entry.Quantity);
        int targetLands = SuggestedLandCount(EstimateAverageManaValue());
        int landsToAdd = Math.Max(0, targetLands - currentLands);

        if (landsToAdd <= 0)
        {
            MessageBox.Show(this,
                ru
                    ? $"В колоде уже {currentLands} земель; ориентир по текущей кривой — {targetLands}. Добавлять больше автоматически не нужно."
                    : $"The deck already has {currentLands} lands; the current curve suggests {targetLands}. No more lands need to be added automatically.",
                ru ? "Автоземли" : "Auto lands",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        Dictionary<char, int> demand = CountColoredManaPips();
        if (demand.Values.Sum() == 0 && _assistantColors.Count > 0)
        {
            foreach (char color in _assistantColors)
                demand[color] = 1;
        }

        List<char> activeColors = "WUBRG".Where(color => demand.GetValueOrDefault(color) > 0).ToList();
        if (activeColors.Count == 0)
        {
            MessageBox.Show(this,
                ru
                    ? "Не удалось определить цвета маны. Добавь цветные карты или выбери цвета в помощнике сборки."
                    : "Mana colors could not be determined. Add colored cards or choose colors in the deck assistant.",
                ru ? "Автоземли" : "Auto lands",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        Dictionary<char, List<CardRecord>> variantsByColor = new();
        Dictionary<char, CardRecord> preferredByColor = new();
        foreach (char color in activeColors)
        {
            List<CardRecord> variants = _catalog
                .Where(card => IsBasicLand(card) && BasicLandColors(card).Contains(color))
                .GroupBy(card => card.FileName, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(card => card.Expansion, StringComparer.OrdinalIgnoreCase)
                .ThenBy(card => card.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (variants.Count == 0)
            {
                MessageBox.Show(this,
                    ru ? $"Не найдена базовая земля для цвета {color}." : $"No basic land was found for {color}.",
                    ru ? "Автоземли" : "Auto lands",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            variantsByColor[color] = variants;
            DeckEntry? existingPreferred = _deck.MainDeck
                .Where(entry => IsBasicLand(entry.Card) && BasicLandColors(entry.Card).Contains(color))
                .OrderByDescending(entry => entry.Quantity)
                .ThenBy(entry => entry.Card.FileName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            preferredByColor[color] = existingPreferred?.Card ?? variants[0];
        }

        int totalDemand = activeColors.Sum(color => demand.GetValueOrDefault(color));
        Dictionary<char, int> desired = activeColors.ToDictionary(
            color => color,
            color => (int)Math.Floor(targetLands * demand.GetValueOrDefault(color) / (double)totalDemand));
        int assigned = desired.Values.Sum();
        foreach (char color in activeColors
                     .OrderByDescending(color =>
                         targetLands * demand.GetValueOrDefault(color) / (double)totalDemand - desired[color]))
        {
            if (assigned >= targetLands)
                break;
            desired[color]++;
            assigned++;
        }

        Dictionary<char, int> current = activeColors.ToDictionary(color => color, CountBasicLandSources);
        List<char> additions = new();
        while (additions.Count < landsToAdd)
        {
            char next = activeColors
                .OrderByDescending(color => desired[color] - current[color])
                .ThenByDescending(color => demand.GetValueOrDefault(color))
                .First();
            additions.Add(next);
            current[next]++;
        }

        foreach (char color in additions)
            _editor.Add(preferredByColor[color], DeckSection.MainDeck);

        SetDirty(true);
        RefreshCollections();
        UpdateDeckAssistantDashboard();

        string colorSummary = string.Join(", ", additions
            .GroupBy(color => color)
            .Select(group => $"{group.Key} ×{group.Count()}"));
        string variantSummary = string.Join(", ", activeColors
            .Where(color => additions.Contains(color))
            .Select(color => $"{color}: {preferredByColor[color].FileName}"));
        Status(ru
            ? $"Автоземли: добавлено {additions.Count} базовых земель ({colorSummary}). Копии объединены по одному предпочтительному арту на цвет ({variantSummary})."
            : $"Auto lands: added {additions.Count} basic lands ({colorSummary}). Copies were consolidated into one preferred art per color ({variantSummary}).");
    }
}
