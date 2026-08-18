using System.IO;
using System.Windows;
using System.Windows.Controls;
using DeckBuilder.Core.Models;
using DeckBuilder.GameData;
using Microsoft.Win32;

namespace DeckBuilder.Modern;

public sealed class DeckPackageWizardWindow : Window
{
    private readonly DeckDocument _deck;
    private readonly string? _gameDirectory;
    private readonly GameCardImageLoader? _imageLoader;
    private readonly ComboBox _playerBox = new();
    private readonly TextBox _nameBox = new();
    private readonly TextBox _descriptionBox = new();
    private readonly TextBox _slotBox = new();
    private readonly TextBlock _uidText = new();
    private readonly TextBlock _idStatusText = new();
    private readonly TextBox _coverBox = new();
    private readonly TextBlock _coverDetails = new();
    private readonly TextBox _outputBox = new();
    private readonly TextBlock _validationText = new();
    private readonly Button _buildButton = new();
    private string? _customCoverSourcePath;
    private double _customCoverOffsetX;
    private double _customCoverOffsetY;
    private double _customCoverZoom = 1.0;
    private string _customCoverSkin = "Classic";

    public int IdBlock { get; private set; }
    public int Slot { get; private set; }
    public int DeckUid => MultiplayerDeckIdPlanner.DeckUid(IdBlock, Slot);
    public string DeckName { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public string DeckBoxImage { get; private set; } = string.Empty;
    public string? CustomCoverSourcePath { get; private set; }
    public double CustomCoverOffsetX { get; private set; }
    public double CustomCoverOffsetY { get; private set; }
    public double CustomCoverZoom { get; private set; } = 1.0;
    public string CustomCoverSkin { get; private set; } = "Classic";
    public string OutputPath { get; private set; } = string.Empty;

    public DeckPackageWizardWindow(
        DeckDocument deck,
        string projectName,
        string? gameDirectory,
        GameCardImageLoader? imageLoader)
    {
        _deck = deck ?? throw new ArgumentNullException(nameof(deck));
        _gameDirectory = gameDirectory;
        _imageLoader = imageLoader;

        Title = "Упаковать колоду";
        Width = 780;
        Height = 730;
        MinWidth = 700;
        MinHeight = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResizeWithGrip;

        string initialName = string.IsNullOrWhiteSpace(deck.Name) || deck.Name == "Untitled deck"
            ? projectName
            : deck.Name;
        _nameBox.Text = initialName;
        _descriptionBox.Text = deck.Description ?? string.Empty;
        _coverBox.Text = deck.DeckBoxImage ?? string.Empty;

        Content = BuildUi();
        Loaded += async (_, _) =>
        {
            _playerBox.SelectedIndex = GuessPlayerIndex(deck.ContentPack);
            await EnsureDefaultCoverAsync();
            await RefreshSuggestedSlotAsync();
            RefreshCoverDetails();
            RefreshValidation();
        };
    }

    private UIElement BuildUi()
    {
        Grid root = new() { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        StackPanel header = new();
        header.Children.Add(new TextBlock
        {
            Text = "Финальная сборка колоды",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold
        });
        header.Children.Add(new TextBlock
        {
            Text = "Выберите блок игрока, свободный Deck UID, оформление и файл назначения.",
            Margin = new Thickness(0, 5, 0, 14),
            TextWrapping = TextWrapping.Wrap
        });
        root.Children.Add(header);

        ScrollViewer scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        StackPanel form = new() { Margin = new Thickness(0, 0, 8, 8) };
        scroll.Content = form;

        form.Children.Add(SectionTitle("Колода"));
        form.Children.Add(Field("Название", _nameBox));
        _nameBox.TextChanged += (_, _) =>
        {
            UpdateSuggestedOutputPath();
            RefreshValidation();
        };
        _descriptionBox.AcceptsReturn = true;
        _descriptionBox.Height = 72;
        _descriptionBox.TextWrapping = TextWrapping.Wrap;
        form.Children.Add(Field("Описание", _descriptionBox));

        form.Children.Add(SectionTitle("ID для совместной игры"));
        _playerBox.ItemsSource = MultiplayerDeckIdPlanner.PlayerPresets;
        _playerBox.DisplayMemberPath = nameof(MultiplayerIdBlockPreset.DisplayName);
        _playerBox.SelectionChanged += async (_, _) => await RefreshSuggestedSlotAsync();
        form.Children.Add(Field("Игрок / ID Block", _playerBox));

        _slotBox.Width = 90;
        _slotBox.HorizontalAlignment = HorizontalAlignment.Left;
        _slotBox.TextChanged += (_, _) => RefreshIdFromSlot();
        form.Children.Add(Field("Слот внутри блока (00–99)", _slotBox));
        _uidText.FontWeight = FontWeights.SemiBold;
        form.Children.Add(Field("Итоговый Deck UID", _uidText));
        _idStatusText.TextWrapping = TextWrapping.Wrap;
        form.Children.Add(Field("Статус", _idStatusText));

        form.Children.Add(SectionTitle("Оформление"));
        _coverBox.IsReadOnly = true;
        Button chooseCover = new() { Content = "Игровая…", Margin = new Thickness(8, 0, 0, 0) };
        chooseCover.Click += ChooseCover_Click;
        Button chooseCustomCover = new() { Content = "Своя / редактор…", Margin = new Thickness(8, 0, 0, 0) };
        chooseCustomCover.Click += ChooseCustomCover_Click;
        Grid coverRow = new();
        coverRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        coverRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        coverRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        coverRow.Children.Add(_coverBox);
        Grid.SetColumn(chooseCover, 1);
        coverRow.Children.Add(chooseCover);
        Grid.SetColumn(chooseCustomCover, 2);
        coverRow.Children.Add(chooseCustomCover);
        form.Children.Add(Field("Обложка колоды", coverRow));
        _coverDetails.Margin = new Thickness(150, 0, 0, 10);
        _coverDetails.Opacity = 0.72;
        _coverDetails.TextWrapping = TextWrapping.Wrap;
        form.Children.Add(_coverDetails);

        form.Children.Add(SectionTitle("Файл WAD"));
        Button browseOutput = new() { Content = "Изменить…", Margin = new Thickness(8, 0, 0, 0), ToolTip = "Изменит также папку WAD в настройках" };
        browseOutput.Click += BrowseOutput_Click;
        Grid outputRow = new();
        outputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        outputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        outputRow.Children.Add(_outputBox);
        Grid.SetColumn(browseOutput, 1);
        outputRow.Children.Add(browseOutput);
        form.Children.Add(Field("Назначение", outputRow));
        _outputBox.TextChanged += (_, _) => RefreshValidation();

        Border validation = new()
        {
            Padding = new Thickness(10),
            Margin = new Thickness(0, 12, 0, 0),
            BorderThickness = new Thickness(1),
            BorderBrush = SystemColors.ControlDarkBrush,
            CornerRadius = new CornerRadius(4)
        };
        _validationText.TextWrapping = TextWrapping.Wrap;
        validation.Child = _validationText;
        form.Children.Add(validation);

        DockPanel footer = new() { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Button cancel = new() { Content = "Отмена", MinWidth = 95, Margin = new Thickness(8, 0, 0, 0) };
        cancel.Click += (_, _) => DialogResult = false;
        DockPanel.SetDock(cancel, Dock.Right);
        footer.Children.Add(cancel);

        _buildButton.Content = "УПАКОВАТЬ КОЛОДУ";
        _buildButton.MinWidth = 180;
        _buildButton.FontWeight = FontWeights.SemiBold;
        _buildButton.Click += Build_Click;
        DockPanel.SetDock(_buildButton, Dock.Right);
        footer.Children.Add(_buildButton);

        return root;
    }

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        FontSize = 16,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 10, 0, 8)
    };

    private static Grid Field(string label, UIElement control)
    {
        Grid row = new() { Margin = new Thickness(0, 0, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(145) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        });
        Grid.SetColumn(control, 1);
        row.Children.Add(control);
        return row;
    }

    private int GuessPlayerIndex(int contentPack)
    {
        int index = MultiplayerDeckIdPlanner.PlayerPresets
            .Select((preset, position) => (preset, position))
            .Where(item => item.preset.IdBlock == contentPack)
            .Select(item => item.position)
            .FirstOrDefault(-1);
        return index >= 0 ? index : 0;
    }

    private async Task EnsureDefaultCoverAsync()
    {
        if (!string.IsNullOrWhiteSpace(_coverBox.Text) || _imageLoader is null)
            return;

        try
        {
            IReadOnlyList<string> covers = await _imageLoader.GetImageIdsAsync(GameImageKind.Deck);
            string? first = covers.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (!string.IsNullOrWhiteSpace(first))
                _coverBox.Text = first;
        }
        catch
        {
            // Validation below will keep packaging disabled if no valid cover can be resolved.
        }
    }

    private async Task RefreshSuggestedSlotAsync()
    {
        if (_playerBox.SelectedItem is not MultiplayerIdBlockPreset preset)
            return;

        IdBlock = preset.IdBlock;
        _slotBox.IsEnabled = false;
        _idStatusText.Text = "Проверяю занятые ID…";
        try
        {
            int slot = await Task.Run(() => MultiplayerDeckIdPlanner.SuggestSlot(
                _gameDirectory ?? string.Empty,
                preset.IdBlock,
                _deck.Uid));
            _slotBox.Text = slot >= 0 ? slot.ToString("00") : string.Empty;
            _idStatusText.Text = slot >= 0
                ? $"Свободный слот найден в блоке {preset.IdBlock}."
                : $"В блоке {preset.IdBlock} нет свободных слотов.";
        }
        catch (Exception exception)
        {
            _slotBox.Text = string.Empty;
            _idStatusText.Text = $"Не удалось проверить ID: {exception.Message}";
        }
        finally
        {
            _slotBox.IsEnabled = true;
            RefreshIdFromSlot();
        }
    }

    private void RefreshIdFromSlot()
    {
        if (_playerBox.SelectedItem is not MultiplayerIdBlockPreset preset
            || !int.TryParse(_slotBox.Text, out int slot)
            || slot is < 0 or > 99)
        {
            _uidText.Text = "—";
            RefreshValidation();
            return;
        }

        IdBlock = preset.IdBlock;
        Slot = slot;
        _uidText.Text = MultiplayerDeckIdPlanner.DeckUid(IdBlock, Slot).ToString();
        RefreshCustomCoverId();
        UpdateSuggestedOutputPath();
        RefreshValidation();
    }

    private void RefreshCustomCoverId()
    {
        if (string.IsNullOrWhiteSpace(_customCoverSourcePath))
            return;

        _coverBox.Text = _uidText.Text == "—"
            ? "CUSTOM_DECK_IMAGE"
            : $"D14_{_uidText.Text}_CUSTOM_DECK_IMAGE";
    }

    private void UpdateSuggestedOutputPath()
    {
        if (string.IsNullOrWhiteSpace(_uidText.Text) || _uidText.Text == "—")
            return;

        string name = string.IsNullOrWhiteSpace(_nameBox.Text) ? "CUSTOM_DECK" : SanitizeCode(_nameBox.Text);
        string configured = AppSettingsService.Current.WadOutputDirectory;
        string directory = !string.IsNullOrWhiteSpace(configured)
            ? configured
            : !string.IsNullOrWhiteSpace(_gameDirectory) && Directory.Exists(_gameDirectory)
                ? _gameDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string expectedName = $"Data_Decks_{_uidText.Text}_{name}.wad";
        string currentDirectory = string.IsNullOrWhiteSpace(_outputBox.Text)
            ? string.Empty
            : Path.GetDirectoryName(_outputBox.Text) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(_outputBox.Text)
            || string.Equals(currentDirectory, directory, StringComparison.OrdinalIgnoreCase)
            || string.Equals(currentDirectory, _gameDirectory, StringComparison.OrdinalIgnoreCase))
        {
            _outputBox.Text = Path.Combine(directory, expectedName);
        }
    }

    private void ChooseCover_Click(object sender, RoutedEventArgs e)
    {
        if (_imageLoader is null)
        {
            MessageBox.Show(this,
                "Сначала загрузите игровую папку или распакованный workspace, чтобы выбрать игровую обложку.",
                "Обложки недоступны",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        GameImagePickerWindow picker = new(
            _imageLoader,
            GameImageKind.Deck,
            "Выберите обложку колоды",
            string.IsNullOrWhiteSpace(_customCoverSourcePath) ? _coverBox.Text : string.Empty)
        {
            Owner = this
        };
        if (picker.ShowDialog() == true)
        {
            _customCoverSourcePath = null;
            _customCoverOffsetX = 0;
            _customCoverOffsetY = 0;
            _customCoverZoom = 1;
            _customCoverSkin = "Classic";
            _coverBox.Text = picker.SelectedImageId;
            RefreshCoverDetails();
            RefreshValidation();
        }
    }

    private void ChooseCustomCover_Click(object sender, RoutedEventArgs e)
    {
        string? initialPath = _customCoverSourcePath;
        if (string.IsNullOrWhiteSpace(initialPath) || !File.Exists(initialPath))
        {
            OpenFileDialog dialog = new()
            {
                Title = "Выберите свою обложку колоды",
                Filter = "Изображения (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|Все файлы (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };
            if (dialog.ShowDialog(this) != true)
                return;
            initialPath = Path.GetFullPath(dialog.FileName);
        }

        CustomDeckCoverEditorWindow editor = new(
            initialPath,
            _customCoverOffsetX,
            _customCoverOffsetY,
            _customCoverZoom,
            _customCoverSkin)
        {
            Owner = this
        };
        if (editor.ShowDialog() != true)
            return;

        _customCoverSourcePath = editor.SourcePath;
        _customCoverOffsetX = editor.OffsetX;
        _customCoverOffsetY = editor.OffsetY;
        _customCoverZoom = editor.Zoom;
        _customCoverSkin = editor.SkinPreset;
        RefreshCustomCoverId();
        RefreshCoverDetails();
        RefreshValidation();
    }

    private void RefreshCoverDetails()
    {
        if (string.IsNullOrWhiteSpace(_customCoverSourcePath))
        {
            _coverDetails.Text = "Игровая обложка: готовый TDX из workspace.";
            return;
        }

        _coverDetails.Text =
            $"Своя: {Path.GetFileName(_customCoverSourcePath)} · рубашка {_customCoverSkin} · " +
            $"X {_customCoverOffsetX:0.0}, Y {_customCoverOffsetY:0.0}, zoom {_customCoverZoom:0.00}×. " +
            "Нажмите «Своя / редактор…», чтобы снова подвигать или масштабировать изображение.";
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        string configured = AppSettingsService.Current.WadOutputDirectory;
        SaveFileDialog dialog = new()
        {
            Title = "Куда сохранить WAD колоды",
            Filter = "Magic 2014 WAD (*.wad)|*.wad",
            DefaultExt = ".wad",
            AddExtension = true,
            FileName = string.IsNullOrWhiteSpace(_outputBox.Text)
                ? $"Data_Decks_{_uidText.Text}_{SanitizeCode(_nameBox.Text)}.wad"
                : Path.GetFileName(_outputBox.Text),
            InitialDirectory = !string.IsNullOrWhiteSpace(configured)
                ? configured
                : string.IsNullOrWhiteSpace(_outputBox.Text)
                    ? _gameDirectory
                    : Path.GetDirectoryName(_outputBox.Text)
        };
        if (dialog.ShowDialog(this) != true)
            return;

        _outputBox.Text = dialog.FileName;
        string? selectedDirectory = Path.GetDirectoryName(dialog.FileName);
        if (!string.IsNullOrWhiteSpace(selectedDirectory))
        {
            AppSettingsService.Current.WadOutputDirectory = Path.GetFullPath(selectedDirectory);
            AppSettingsService.Save();
        }
    }

    private void RefreshValidation()
    {
        List<string> problems = new();
        if (_deck.MainDeckCardCount < 60)
            problems.Add($"Основная колода содержит {_deck.MainDeckCardCount} карт; для обычной constructed-колоды нужно минимум 60.");
        if (string.IsNullOrWhiteSpace(_nameBox.Text) || _nameBox.Text.Equals("Untitled deck", StringComparison.OrdinalIgnoreCase))
            problems.Add("Задайте нормальное название колоды.");
        if (_playerBox.SelectedItem is not MultiplayerIdBlockPreset)
            problems.Add("Выберите игрока / ID Block.");
        if (!int.TryParse(_slotBox.Text, out int slot) || slot is < 0 or > 99)
            problems.Add("Слот должен быть от 00 до 99.");
        if (!string.IsNullOrWhiteSpace(_customCoverSourcePath) && !File.Exists(_customCoverSourcePath))
            problems.Add("Файл своей обложки больше не существует.");
        if (string.IsNullOrWhiteSpace(_coverBox.Text))
            problems.Add("Выберите игровую или свою обложку колоды.");
        if (string.IsNullOrWhiteSpace(_outputBox.Text))
            problems.Add("Выберите файл назначения WAD.");

        string coverType = string.IsNullOrWhiteSpace(_customCoverSourcePath)
            ? "игровая"
            : $"своя PNG/JPG, рубашка {_customCoverSkin}";
        _validationText.Text = problems.Count == 0
            ? $"✓ Готово к упаковке. Карт: {_deck.MainDeckCardCount}; Deck UID: {_uidText.Text}; обложка: {coverType} — {_coverBox.Text}."
            : "Нужно проверить:\n• " + string.Join("\n• ", problems);
        _buildButton.IsEnabled = problems.Count == 0;
    }

    private void Build_Click(object sender, RoutedEventArgs e)
    {
        RefreshValidation();
        if (!_buildButton.IsEnabled || _playerBox.SelectedItem is not MultiplayerIdBlockPreset preset)
            return;

        if (!int.TryParse(_slotBox.Text, out int slot) || slot is < 0 or > 99)
            return;

        if (!string.IsNullOrWhiteSpace(_gameDirectory)
            && Directory.Exists(_gameDirectory)
            && !MultiplayerDeckIdPlanner.IsDeckUidAvailable(_gameDirectory, preset.IdBlock,
                MultiplayerDeckIdPlanner.DeckUid(preset.IdBlock, slot))
            && _deck.Uid != MultiplayerDeckIdPlanner.DeckUid(preset.IdBlock, slot))
        {
            MessageBox.Show(this,
                "Этот Deck UID уже занят установленной колодой. Выберите другой слот.",
                "Конфликт Deck UID",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        IdBlock = preset.IdBlock;
        Slot = slot;
        DeckName = _nameBox.Text.Trim();
        Description = _descriptionBox.Text.Trim();
        DeckBoxImage = string.IsNullOrWhiteSpace(_customCoverSourcePath)
            ? _coverBox.Text.Trim()
            : $"D14_{DeckUid}_CUSTOM_DECK_IMAGE";
        CustomCoverSourcePath = _customCoverSourcePath;
        CustomCoverOffsetX = _customCoverOffsetX;
        CustomCoverOffsetY = _customCoverOffsetY;
        CustomCoverZoom = _customCoverZoom;
        CustomCoverSkin = _customCoverSkin;
        OutputPath = Path.GetFullPath(_outputBox.Text.Trim());

        string? outputDirectory = Path.GetDirectoryName(OutputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            AppSettingsService.Current.WadOutputDirectory = outputDirectory;
            AppSettingsService.Save();
        }

        DialogResult = true;
    }

    private static string SanitizeCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "CUSTOM_DECK";
        string code = new(value.Trim().ToUpperInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '_')
            .ToArray());
        while (code.Contains("__", StringComparison.Ordinal))
            code = code.Replace("__", "_", StringComparison.Ordinal);
        code = code.Trim('_');
        return code.Length == 0 ? "CUSTOM_DECK" : code;
    }
}
