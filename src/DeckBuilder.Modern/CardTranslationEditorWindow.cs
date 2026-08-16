using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using DeckBuilder.Core.Models;
using DeckBuilder.GameData;
using Microsoft.Win32;

namespace DeckBuilder.Modern;

public sealed class CardTranslationEditorWindow : Window
{
    private readonly string _xmlPath;
    private readonly XDocument _document;
    private readonly XElement _card;

    private readonly TextBox _fileNameBox = new() { IsReadOnly = true };
    private readonly TextBox _cardNameBox = new();
    private readonly TextBox _titleEnBox = new();
    private readonly TextBox _titleRuBox = new();
    private readonly TextBox _castingCostBox = new();
    private readonly ManaCostPresenter _manaPreview = new();
    private readonly TextBox _colourBox = new();
    private readonly TextBox _expansionBox = new();
    private readonly TextBox _rarityBox = new();
    private readonly TextBox _powerBox = new();
    private readonly TextBox _toughnessBox = new();
    private readonly TextBox _artistBox = new();
    private readonly TextBox _frameTypeBox = new();

    private readonly StackPanel _typesPanel = new();
    private readonly List<(XElement Element, TextBox Metaname, TextBox Value)> _typeEditors = new();

    private readonly StackPanel _abilitiesPanel = new();
    private readonly List<(XElement Element, TextBox English, TextBox Russian)> _abilityEditors = new();
    private readonly TextBox _flavorEnBox = MultilineBox(92);
    private readonly TextBox _flavorRuBox = MultilineBox(92);

    private readonly TextBox _artIdBox = new();
    private readonly TextBox _artPathBox = new() { IsReadOnly = true };
    private readonly TextBlock _artStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Image _artPreview = new()
    {
        Width = 512,
        Height = 376,
        Stretch = Stretch.Uniform,
        HorizontalAlignment = HorizontalAlignment.Left
    };

    private string? _pendingArtSource;

    public bool Saved { get; private set; }

    public CardTranslationEditorWindow(CardRecord card, string xmlPath)
    {
        _xmlPath = Path.GetFullPath(xmlPath);
        _document = XDocument.Load(_xmlPath, LoadOptions.PreserveWhitespace);
        _card = FindCard(_document) ?? throw new InvalidDataException("CARD_V2 element was not found.");

        Title = AppLocalization.IsRussian
            ? $"Редактор карты — {card.EnglishName}"
            : $"Card editor — {card.EnglishName}";
        Width = 1080;
        Height = 860;
        MinWidth = 820;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        Content = BuildUi(card);
        LoadValues();
    }

    private UIElement BuildUi(CardRecord record)
    {
        Grid root = new() { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        TextBlock info = new()
        {
            Text = $"{record.FileName}\n{_xmlPath}",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72,
            Margin = new Thickness(0, 0, 0, 10)
        };
        root.Children.Add(info);

        TabControl tabs = new();
        tabs.Items.Add(new TabItem
        {
            Header = AppLocalization.IsRussian ? "Основное" : "General",
            Content = Scroll(BuildGeneralTab())
        });
        tabs.Items.Add(new TabItem
        {
            Header = AppLocalization.IsRussian ? "Текст и способности" : "Text and abilities",
            Content = Scroll(BuildTextTab())
        });
        tabs.Items.Add(new TabItem
        {
            Header = AppLocalization.IsRussian ? "Арт" : "Art",
            Content = Scroll(BuildArtTab())
        });
        Grid.SetRow(tabs, 1);
        root.Children.Add(tabs);

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        Button save = new()
        {
            Content = AppLocalization.IsRussian ? "Сохранить карту" : "Save card",
            MinWidth = 135,
            IsDefault = true
        };
        save.Click += Save_Click;
        Button cancel = new()
        {
            Content = AppLocalization.IsRussian ? "Отмена" : "Cancel",
            MinWidth = 90,
            IsCancel = true,
            Margin = new Thickness(8, 0, 0, 0)
        };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        return root;
    }

    private StackPanel BuildGeneralTab()
    {
        StackPanel form = FormPanel();

        form.Children.Add(Section(AppLocalization.IsRussian ? "Идентификаторы" : "Identifiers"));
        AddField(form, "FILENAME", _fileNameBox);
        AddField(form, "CARDNAME", _cardNameBox);

        form.Children.Add(Section(AppLocalization.IsRussian ? "Мана" : "Mana"));
        AddField(form, AppLocalization.IsRussian ? "Стоимость маны" : "Casting cost", _castingCostBox);
        _castingCostBox.TextChanged += (_, _) => _manaPreview.Cost = _castingCostBox.Text;
        _manaPreview.Margin = new Thickness(0, 0, 0, 8);
        form.Children.Add(_manaPreview);
        form.Children.Add(BuildManaButtons());

        form.Children.Add(Section(AppLocalization.IsRussian ? "Параметры карты" : "Card properties"));
        AddField(form, "COLOUR", _colourBox);
        AddField(form, "EXPANSION", _expansionBox);
        AddField(form, "RARITY", _rarityBox);

        Grid stats = TwoColumnGrid();
        AddGridField(stats, 0, 0, "POWER", _powerBox);
        AddGridField(stats, 0, 1, "TOUGHNESS", _toughnessBox);
        form.Children.Add(stats);

        AddField(form, "ARTIST", _artistBox);
        AddField(form, "FRAME_TYPE", _frameTypeBox);

        form.Children.Add(Section(AppLocalization.IsRussian
            ? "Типы (существующие элементы CARD_V2)"
            : "Types (existing CARD_V2 elements)"));
        _typesPanel.Margin = new Thickness(0, 0, 0, 12);
        form.Children.Add(_typesPanel);

        return form;
    }

    private StackPanel BuildTextTab()
    {
        StackPanel form = FormPanel();

        form.Children.Add(Section(AppLocalization.IsRussian ? "Название" : "Title"));
        AddField(form, "TITLE en-US", _titleEnBox);
        AddField(form, "TITLE ru-RU", _titleRuBox);

        form.Children.Add(Section(AppLocalization.IsRussian ? "Способности" : "Abilities"));
        _abilitiesPanel.Margin = new Thickness(0, 0, 0, 12);
        form.Children.Add(_abilitiesPanel);

        form.Children.Add(Section(AppLocalization.IsRussian ? "Художественный текст" : "Flavor text"));
        AddField(form, "FLAVOURTEXT en-US", _flavorEnBox);
        AddField(form, "FLAVOURTEXT ru-RU", _flavorRuBox);

        return form;
    }

    private StackPanel BuildArtTab()
    {
        StackPanel form = FormPanel();

        form.Children.Add(Section(AppLocalization.IsRussian ? "Привязка иллюстрации" : "Illustration binding"));
        form.Children.Add(new TextBlock
        {
            Text = AppLocalization.IsRussian
                ? "CARD_V2 хранит не полный путь, а ARTID. Билдер ищет TDX с таким именем в ART_ASSETS\\ILLUSTRATIONS."
                : "CARD_V2 stores an ARTID, not a full path. The builder resolves a TDX with that name under ART_ASSETS\\ILLUSTRATIONS.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.76,
            Margin = new Thickness(0, 0, 0, 10)
        });

        AddField(form, "ARTID", _artIdBox);
        _artIdBox.TextChanged += (_, _) => RefreshArtPathStatus();

        AddField(form, AppLocalization.IsRussian ? "Ожидаемый путь TDX" : "Expected TDX path", _artPathBox);

        WrapPanel artButtons = new() { Margin = new Thickness(0, 0, 0, 8) };
        Button choose = new()
        {
            Content = AppLocalization.IsRussian ? "Выбрать / установить TDX…" : "Choose / install TDX…",
            MinWidth = 165,
            Margin = new Thickness(0, 0, 6, 6)
        };
        choose.Click += ChooseArt_Click;
        artButtons.Children.Add(choose);

        Button check = new()
        {
            Content = AppLocalization.IsRussian ? "Проверить TDX" : "Validate TDX",
            MinWidth = 120,
            Margin = new Thickness(0, 0, 6, 6)
        };
        check.Click += CheckArt_Click;
        artButtons.Children.Add(check);

        Button openFolder = new()
        {
            Content = AppLocalization.IsRussian ? "Открыть папку" : "Open folder",
            MinWidth = 115,
            Margin = new Thickness(0, 0, 6, 6)
        };
        openFolder.Click += OpenArtFolder_Click;
        artButtons.Children.Add(openFolder);
        form.Children.Add(artButtons);

        Border statusBorder = new()
        {
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 10),
            Child = _artStatus
        };
        form.Children.Add(statusBorder);

        Border previewBorder = new()
        {
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = _artPreview
        };
        form.Children.Add(previewBorder);

        return form;
    }

    private UIElement BuildManaButtons()
    {
        StackPanel root = new() { Margin = new Thickness(0, 0, 0, 12) };

        root.Children.Add(new TextBlock
        {
            Text = AppLocalization.IsRussian
                ? "Кнопки добавляют точные токены DotP 2014:"
                : "Buttons append exact DotP 2014 tokens:",
            Opacity = 0.72,
            Margin = new Thickness(0, 0, 0, 5)
        });

        WrapPanel generic = new();
        foreach (string token in Enumerable.Range(0, 17).Select(value => $"{{{value}}}").Append("{X}"))
            AddManaButton(generic, token);
        root.Children.Add(generic);

        WrapPanel colours = new() { Margin = new Thickness(0, 4, 0, 0) };
        foreach (string token in new[] { "{W}", "{U}", "{B}", "{R}", "{G}" })
            AddManaButton(colours, token);
        root.Children.Add(colours);

        WrapPanel hybrid = new() { Margin = new Thickness(0, 4, 0, 0) };
        foreach (string token in new[]
                 {
                     "{W/U}", "{W/B}", "{W/R}", "{W/G}",
                     "{U/B}", "{U/R}", "{U/G}",
                     "{B/R}", "{B/G}", "{R/G}"
                 })
            AddManaButton(hybrid, token);
        root.Children.Add(hybrid);

        WrapPanel phyrexian = new() { Margin = new Thickness(0, 4, 0, 0) };
        foreach (string token in new[] { "{W/P}", "{U/P}", "{B/P}", "{R/P}", "{G/P}" })
            AddManaButton(phyrexian, token);

        Button backspace = new()
        {
            Content = AppLocalization.IsRussian ? "← токен" : "← token",
            MinWidth = 68,
            Margin = new Thickness(2)
        };
        backspace.Click += (_, _) => RemoveLastManaToken();
        phyrexian.Children.Add(backspace);

        Button clear = new()
        {
            Content = AppLocalization.IsRussian ? "Очистить" : "Clear",
            MinWidth = 68,
            Margin = new Thickness(2)
        };
        clear.Click += (_, _) => _castingCostBox.Clear();
        phyrexian.Children.Add(clear);
        root.Children.Add(phyrexian);

        return root;
    }

    private void AddManaButton(Panel panel, string token)
    {
        Button button = new()
        {
            Content = token,
            MinWidth = 43,
            Margin = new Thickness(2),
            Padding = new Thickness(6, 2, 6, 2)
        };
        button.Click += (_, _) =>
        {
            int caret = _castingCostBox.CaretIndex;
            _castingCostBox.Text = _castingCostBox.Text.Insert(caret, token);
            _castingCostBox.CaretIndex = caret + token.Length;
            _castingCostBox.Focus();
        };
        panel.Children.Add(button);
    }

    private void RemoveLastManaToken()
    {
        string text = _castingCostBox.Text;
        if (text.Length == 0)
            return;

        int caret = _castingCostBox.CaretIndex;
        if (caret <= 0)
            return;

        int end = text.LastIndexOf('}', caret - 1);
        int start = end >= 0 ? text.LastIndexOf('{', end) : -1;
        if (start >= 0 && end == caret - 1)
        {
            _castingCostBox.Text = text.Remove(start, end - start + 1);
            _castingCostBox.CaretIndex = start;
            return;
        }

        _castingCostBox.Text = text.Remove(caret - 1, 1);
        _castingCostBox.CaretIndex = caret - 1;
    }

    private void LoadValues()
    {
        _fileNameBox.Text = Attribute(Child(_card, "FILENAME"), "text");
        _cardNameBox.Text = Attribute(Child(_card, "CARDNAME"), "text");

        _titleEnBox.Text = GetLocalized(Child(_card, "TITLE"), "en-US") ?? string.Empty;
        _titleRuBox.Text = GetLocalized(Child(_card, "TITLE"), "ru-RU") ?? string.Empty;
        _castingCostBox.Text = Attribute(Child(_card, "CASTING_COST"), "cost");
        _manaPreview.Cost = _castingCostBox.Text;
        _colourBox.Text = Attribute(Child(_card, "COLOUR"), "value");
        _expansionBox.Text = Attribute(Child(_card, "EXPANSION"), "value");
        _rarityBox.Text = Attribute(Child(_card, "RARITY"), "metaname");
        _powerBox.Text = Attribute(Child(_card, "POWER"), "value");
        _toughnessBox.Text = Attribute(Child(_card, "TOUGHNESS"), "value");
        _artistBox.Text = Attribute(Child(_card, "ARTIST"), "name");
        _frameTypeBox.Text = Attribute(Child(_card, "FRAME_TYPE"), "type");
        _artIdBox.Text = Attribute(Child(_card, "ARTID"), "value");

        LoadTypeEditors();
        LoadAbilityEditors();

        _flavorEnBox.Text = GetLocalized(Child(_card, "FLAVOURTEXT"), "en-US") ?? string.Empty;
        _flavorRuBox.Text = GetLocalized(Child(_card, "FLAVOURTEXT"), "ru-RU") ?? string.Empty;

        RefreshArtPathStatus();
    }

    private void LoadTypeEditors()
    {
        _typesPanel.Children.Clear();
        _typeEditors.Clear();

        XElement[] elements = _card.Elements()
            .Where(element =>
                element.Name.LocalName.Equals("SUPERTYPE", StringComparison.OrdinalIgnoreCase)
                || element.Name.LocalName.Equals("TYPE", StringComparison.OrdinalIgnoreCase)
                || element.Name.LocalName.Equals("SUB_TYPE", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (XElement element in elements)
        {
            Grid row = new() { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            TextBlock name = new()
            {
                Text = element.Name.LocalName,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold
            };
            row.Children.Add(name);

            TextBox metaname = new()
            {
                Text = Attribute(element, "metaname"),
                Margin = new Thickness(5, 0, 5, 0),
                ToolTip = "metaname"
            };
            Grid.SetColumn(metaname, 1);
            row.Children.Add(metaname);

            TextBox value = new()
            {
                Text = Attribute(element, "value"),
                Margin = new Thickness(5, 0, 0, 0),
                ToolTip = "value"
            };
            Grid.SetColumn(value, 2);
            row.Children.Add(value);

            _typesPanel.Children.Add(row);
            _typeEditors.Add((element, metaname, value));
        }

        if (_typeEditors.Count == 0)
        {
            _typesPanel.Children.Add(new TextBlock
            {
                Text = AppLocalization.IsRussian
                    ? "Элементы SUPERTYPE / TYPE / SUB_TYPE отсутствуют."
                    : "No SUPERTYPE / TYPE / SUB_TYPE elements.",
                Opacity = 0.7
            });
        }
    }

    private void LoadAbilityEditors()
    {
        _abilitiesPanel.Children.Clear();
        _abilityEditors.Clear();

        foreach (XElement ability in _card.Descendants().Where(element =>
                     element.Name.LocalName.Contains("_ABILITY", StringComparison.OrdinalIgnoreCase)))
        {
            if (int.TryParse(Attribute(ability, "resource_id"), out int resourceId) && resourceId >= 0)
                continue;

            string english = GetLocalized(ability, "en-US") ?? string.Empty;
            string russian = GetLocalized(ability, "ru-RU") ?? string.Empty;

            Border block = new()
            {
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8),
                Margin = new Thickness(0, 0, 0, 8)
            };
            StackPanel panel = new();
            block.Child = panel;

            panel.Children.Add(new TextBlock
            {
                Text = ability.Name.LocalName,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 5)
            });

            TextBox englishBox = MultilineBox(58);
            englishBox.Text = english;
            AddField(panel, "en-US", englishBox);

            TextBox russianBox = MultilineBox(58);
            russianBox.Text = russian;
            AddField(panel, "ru-RU", russianBox);

            _abilitiesPanel.Children.Add(block);
            _abilityEditors.Add((ability, englishBox, russianBox));
        }

        if (_abilityEditors.Count == 0)
        {
            _abilitiesPanel.Children.Add(new TextBlock
            {
                Text = AppLocalization.IsRussian
                    ? "Редактируемых текстовых способностей нет."
                    : "No editable text abilities.",
                Opacity = 0.7
            });
        }
    }

    private async void ChooseArt_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new()
        {
            Title = AppLocalization.IsRussian ? "Выбери TDX иллюстрации" : "Choose illustration TDX",
            Filter = "DotP texture (*.tdx)|*.tdx|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
            return;

        _pendingArtSource = Path.GetFullPath(dialog.FileName);
        _artIdBox.Text = (Path.GetFileNameWithoutExtension(_pendingArtSource) ?? string.Empty).ToUpperInvariant();
        await ValidateArtAsync(_pendingArtSource, pending: true);
    }

    private async void CheckArt_Click(object sender, RoutedEventArgs e)
    {
        string? path = _pendingArtSource;
        if (string.IsNullOrWhiteSpace(path))
            path = ExpectedArtPath();

        if (string.IsNullOrWhiteSpace(path))
        {
            SetArtStatus(AppLocalization.IsRussian
                ? "Невозможно определить путь: ARTID пуст или XML не находится внутри DATA_ALL_PLATFORMS."
                : "Cannot resolve the path: ARTID is empty or XML is not inside DATA_ALL_PLATFORMS.", false);
            return;
        }

        await ValidateArtAsync(path, pending: _pendingArtSource is not null);
    }

    private void OpenArtFolder_Click(object sender, RoutedEventArgs e)
    {
        string? path = ExpectedArtPath();
        string? directory = path is null ? null : Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            SetArtStatus(AppLocalization.IsRussian
                ? "Папка иллюстрации не определена."
                : "Illustration directory could not be resolved.", false);
            return;
        }

        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo
        {
            FileName = directory,
            UseShellExecute = true
        });
    }

    private async Task ValidateArtAsync(string path, bool pending)
    {
        _artPreview.Source = null;

        if (!File.Exists(path))
        {
            SetArtStatus(
                (pending
                    ? (AppLocalization.IsRussian ? "Выбранный TDX не найден: " : "Selected TDX not found: ")
                    : (AppLocalization.IsRussian ? "TDX отсутствует: " : "TDX is missing: "))
                + path,
                false);
            return;
        }

        try
        {
            CardImageData image = await TdxFileImageLoader.LoadAsync(path);
            BitmapSource bitmap = BitmapSource.Create(
                image.Width,
                image.Height,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                image.BgraPixels,
                checked(image.Width * 4));
            bitmap.Freeze();
            _artPreview.Source = bitmap;

            string prefix = pending
                ? (AppLocalization.IsRussian ? "TDX выбран и валиден" : "Selected TDX is valid")
                : (AppLocalization.IsRussian ? "TDX найден и валиден" : "TDX found and valid");
            SetArtStatus($"{prefix}: {image.Width}×{image.Height}\n{path}", true);
        }
        catch (Exception exception)
        {
            SetArtStatus(
                $"{(AppLocalization.IsRussian ? "TDX найден, но декодер его не принимает" : "TDX exists but the decoder rejects it")}:\n{path}\n\n{exception.Message}",
                false);
        }
    }

    private void RefreshArtPathStatus()
    {
        string? path = ExpectedArtPath();
        _artPathBox.Text = path ?? string.Empty;

        if (_pendingArtSource is not null)
        {
            _artStatus.Text = AppLocalization.IsRussian
                ? $"Выбран новый TDX. При сохранении он будет скопирован сюда:\n{path ?? "(путь не определён)"}"
                : $"A new TDX is selected. On save it will be copied to:\n{path ?? "(unresolved path)"}";
            _artStatus.Foreground = SystemColors.ControlTextBrush;
            return;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            SetArtStatus(AppLocalization.IsRussian
                ? "Путь TDX не определён."
                : "TDX path is unresolved.", false);
            return;
        }

        SetArtStatus(
            File.Exists(path)
                ? (AppLocalization.IsRussian
                    ? $"TDX существует. Нажми «Проверить TDX», чтобы проверить контейнер и декодирование.\n{path}"
                    : $"TDX exists. Use “Validate TDX” to verify the container and decoding.\n{path}")
                : (AppLocalization.IsRussian ? $"TDX не найден:\n{path}" : $"TDX not found:\n{path}"),
            File.Exists(path));
    }

    private void SetArtStatus(string text, bool ok)
    {
        _artStatus.Text = text;
        _artStatus.Foreground = ok ? Brushes.DarkGreen : Brushes.DarkRed;
    }

    private string? ExpectedArtPath()
    {
        string artId = Path.GetFileNameWithoutExtension(_artIdBox.Text.Trim()) ?? string.Empty;
        if (artId.Length == 0)
            return null;

        DirectoryInfo? directory = new FileInfo(_xmlPath).Directory;
        while (directory is not null)
        {
            if (directory.Name.Equals("DATA_ALL_PLATFORMS", StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine(
                    directory.FullName,
                    "ART_ASSETS",
                    "ILLUSTRATIONS",
                    artId + ".TDX");
            }

            directory = directory.Parent;
        }

        return null;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ValidateLocalizedText(_titleEnBox.Text);
            ValidateLocalizedText(_titleRuBox.Text);
            ValidateLocalizedText(_flavorEnBox.Text);
            ValidateLocalizedText(_flavorRuBox.Text);
            foreach (var (_, english, russian) in _abilityEditors)
            {
                ValidateLocalizedText(english.Text);
                ValidateLocalizedText(russian.Text);
            }

            SetChildAttribute("CARDNAME", "text", _cardNameBox.Text);
            SetLocalized(EnsureChild(_card, "TITLE"), "en-US", _titleEnBox.Text);
            SetLocalized(EnsureChild(_card, "TITLE"), "ru-RU", _titleRuBox.Text);

            SetChildAttribute("CASTING_COST", "cost", _castingCostBox.Text);
            SetChildAttribute("COLOUR", "value", _colourBox.Text);
            SetChildAttribute("EXPANSION", "value", _expansionBox.Text);
            SetChildAttribute("RARITY", "metaname", _rarityBox.Text);
            SetChildAttribute("POWER", "value", _powerBox.Text);
            SetChildAttribute("TOUGHNESS", "value", _toughnessBox.Text);
            SetChildAttribute("ARTIST", "name", _artistBox.Text);
            SetChildAttribute("FRAME_TYPE", "type", _frameTypeBox.Text, createWhenEmpty: false);
            SetChildAttribute("ARTID", "value", _artIdBox.Text);

            foreach ((XElement element, TextBox metaname, TextBox value) in _typeEditors)
            {
                SetAttribute(element, "metaname", metaname.Text);
                SetAttribute(element, "value", value.Text, createWhenEmpty: false);
            }

            foreach ((XElement element, TextBox english, TextBox russian) in _abilityEditors)
            {
                SetLocalized(element, "en-US", english.Text);
                SetLocalized(element, "ru-RU", russian.Text);
            }

            XElement? flavor = Child(_card, "FLAVOURTEXT");
            if (flavor is not null
                || !string.IsNullOrWhiteSpace(_flavorEnBox.Text)
                || !string.IsNullOrWhiteSpace(_flavorRuBox.Text))
            {
                flavor ??= EnsureChild(_card, "FLAVOURTEXT");
                SetLocalized(flavor, "en-US", _flavorEnBox.Text);
                SetLocalized(flavor, "ru-RU", _flavorRuBox.Text);
            }

            InstallPendingArt();

            string backup = _xmlPath + ".bak";
            if (!File.Exists(backup))
                File.Copy(_xmlPath, backup);

            using FileStream stream = new(_xmlPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using StreamWriter writer = new(stream, new UTF8Encoding(false));
            _document.Save(writer, SaveOptions.DisableFormatting);

            Saved = true;
            DialogResult = true;
            Close();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                AppLocalization.IsRussian ? "Ошибка сохранения карты" : "Card save error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void InstallPendingArt()
    {
        if (string.IsNullOrWhiteSpace(_pendingArtSource))
            return;

        string source = Path.GetFullPath(_pendingArtSource);
        if (!File.Exists(source))
            throw new FileNotFoundException("Selected TDX no longer exists.", source);

        string? target = ExpectedArtPath();
        if (string.IsNullOrWhiteSpace(target))
            throw new InvalidOperationException(AppLocalization.IsRussian
                ? "Не удалось определить целевой путь TDX по ARTID и расположению XML."
                : "Could not resolve the target TDX path from ARTID and XML location.");

        string? directory = Path.GetDirectoryName(target);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("TDX target directory is invalid.");

        Directory.CreateDirectory(directory);
        if (!source.Equals(target, StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(target) && !File.Exists(target + ".bak"))
                File.Copy(target, target + ".bak");

            File.Copy(source, target, overwrite: true);
        }

        _pendingArtSource = null;
    }

    private void SetChildAttribute(string childName, string attributeName, string value, bool createWhenEmpty = true)
    {
        XElement? element = Child(_card, childName);
        if (element is null)
        {
            if (!createWhenEmpty && string.IsNullOrWhiteSpace(value))
                return;
            element = EnsureChild(_card, childName);
        }

        SetAttribute(element, attributeName, value, createWhenEmpty);
    }

    private static void SetAttribute(XElement element, string name, string value, bool createWhenEmpty = true)
    {
        XAttribute? attribute = element.Attributes()
            .FirstOrDefault(item => item.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (attribute is null)
        {
            if (!createWhenEmpty && string.IsNullOrWhiteSpace(value))
                return;
            element.Add(new XAttribute(name, value ?? string.Empty));
            return;
        }

        attribute.Value = value ?? string.Empty;
    }

    private static void ValidateLocalizedText(string value)
    {
        if (value.Contains("]]>", StringComparison.Ordinal))
            throw new InvalidDataException("Localized text cannot contain the CDATA terminator ]]>");
    }

    private static ScrollViewer Scroll(UIElement content) => new()
    {
        Content = content,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };

    private static StackPanel FormPanel() => new()
    {
        Margin = new Thickness(12, 10, 12, 12)
    };

    private static TextBlock Section(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.Bold,
        FontSize = 15,
        Margin = new Thickness(0, 8, 0, 8)
    };

    private static void AddField(Panel panel, string label, Control editor)
    {
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });
        editor.Margin = new Thickness(0, 0, 0, 10);
        panel.Children.Add(editor);
    }

    private static Grid TwoColumnGrid()
    {
        Grid grid = new() { Margin = new Thickness(0, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        return grid;
    }

    private static void AddGridField(Grid grid, int row, int column, string label, TextBox editor)
    {
        StackPanel panel = new() { Margin = new Thickness(column == 0 ? 0 : 5, 0, column == 0 ? 5 : 0, 6) };
        panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(editor);
        Grid.SetRow(panel, row);
        Grid.SetColumn(panel, column);
        grid.Children.Add(panel);
    }

    private static TextBox MultilineBox(double minHeight) => new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MinHeight = minHeight,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };

    private static XElement? FindCard(XDocument document) =>
        document.Root?.Name.LocalName.Equals("CARD_V2", StringComparison.OrdinalIgnoreCase) == true
            ? document.Root
            : document.Descendants().FirstOrDefault(element =>
                element.Name.LocalName.Equals("CARD_V2", StringComparison.OrdinalIgnoreCase));

    private static XElement? Child(XElement? parent, string name) =>
        parent?.Elements().FirstOrDefault(element =>
            element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static XElement EnsureChild(XElement parent, string name)
    {
        XElement? existing = Child(parent, name);
        if (existing is not null)
            return existing;

        XElement created = new(name);
        parent.Add(created);
        return created;
    }

    private static string Attribute(XElement? element, string name) =>
        element?.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?.Value ?? string.Empty;

    private static string? GetLocalized(XElement? parent, string language) =>
        parent?.Elements()
            .FirstOrDefault(element =>
                element.Name.LocalName.Equals("LOCALISED_TEXT", StringComparison.OrdinalIgnoreCase)
                && Attribute(element, "LanguageCode").Equals(language, StringComparison.OrdinalIgnoreCase))
            ?.Value;

    private static void SetLocalized(XElement parent, string language, string value)
    {
        XElement? localized = parent.Elements().FirstOrDefault(element =>
            element.Name.LocalName.Equals("LOCALISED_TEXT", StringComparison.OrdinalIgnoreCase)
            && Attribute(element, "LanguageCode").Equals(language, StringComparison.OrdinalIgnoreCase));

        if (localized is null)
        {
            localized = new XElement(
                "LOCALISED_TEXT",
                new XAttribute("LanguageCode", language),
                new XCData(value ?? string.Empty));

            if (language.Equals("ru-RU", StringComparison.OrdinalIgnoreCase))
            {
                XElement? english = parent.Elements().FirstOrDefault(element =>
                    element.Name.LocalName.Equals("LOCALISED_TEXT", StringComparison.OrdinalIgnoreCase)
                    && Attribute(element, "LanguageCode").Equals("en-US", StringComparison.OrdinalIgnoreCase));
                if (english is not null)
                {
                    english.AddAfterSelf(localized);
                    return;
                }
            }
            else if (language.Equals("en-US", StringComparison.OrdinalIgnoreCase))
            {
                XElement? russian = parent.Elements().FirstOrDefault(element =>
                    element.Name.LocalName.Equals("LOCALISED_TEXT", StringComparison.OrdinalIgnoreCase)
                    && Attribute(element, "LanguageCode").Equals("ru-RU", StringComparison.OrdinalIgnoreCase));
                if (russian is not null)
                {
                    russian.AddBeforeSelf(localized);
                    return;
                }
            }

            parent.Add(localized);
            return;
        }

        XCData? cdata = localized.Nodes().OfType<XCData>().FirstOrDefault();
        if (cdata is not null && localized.Nodes().All(node => node is XCData))
        {
            cdata.Value = value ?? string.Empty;
            foreach (XCData extra in localized.Nodes().OfType<XCData>().Skip(1).ToArray())
                extra.Remove();
            return;
        }

        localized.RemoveNodes();
        localized.Add(new XCData(value ?? string.Empty));
    }
}
