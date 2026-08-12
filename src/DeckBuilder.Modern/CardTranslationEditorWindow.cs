using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using DeckBuilder.Core.Models;

namespace DeckBuilder.Modern;

public sealed class CardTranslationEditorWindow : Window
{
    private readonly string _xmlPath;
    private readonly XDocument _document;
    private readonly XElement _card;
    private readonly TextBox _titleBox = new();
    private readonly TextBox _flavorBox = new();
    private readonly StackPanel _abilitiesPanel = new();
    private readonly List<(XElement Element, TextBox Editor)> _abilityEditors = new();

    public bool Saved { get; private set; }

    public CardTranslationEditorWindow(CardRecord card, string xmlPath)
    {
        _xmlPath = xmlPath;
        _document = XDocument.Load(xmlPath, LoadOptions.PreserveWhitespace);
        _card = FindCard(_document) ?? throw new InvalidDataException("CARD_V2 element was not found.");

        Title = AppLocalization.IsRussian ? $"Перевод карты — {card.EnglishName}" : $"Card translation — {card.EnglishName}";
        Width = 820;
        Height = 760;
        MinWidth = 650;
        MinHeight = 520;
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

        ScrollViewer scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1);
        StackPanel form = new();
        scroll.Content = form;
        root.Children.Add(scroll);

        form.Children.Add(Label(AppLocalization.IsRussian ? "Название (ru-RU)" : "Title (ru-RU)"));
        _titleBox.Margin = new Thickness(0, 0, 0, 12);
        form.Children.Add(_titleBox);

        TextBlock abilityHeading = Label(AppLocalization.IsRussian ? "Способности (каждая строка оригинальной CARD_V2 отдельно)" : "Abilities (each CARD_V2 ability separately)");
        form.Children.Add(abilityHeading);
        _abilitiesPanel.Margin = new Thickness(0, 0, 0, 12);
        form.Children.Add(_abilitiesPanel);

        form.Children.Add(Label(AppLocalization.IsRussian ? "Художественный текст (ru-RU)" : "Flavor text (ru-RU)"));
        _flavorBox.AcceptsReturn = true;
        _flavorBox.TextWrapping = TextWrapping.Wrap;
        _flavorBox.MinHeight = 90;
        _flavorBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        form.Children.Add(_flavorBox);

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        Button save = new() { Content = AppLocalization.IsRussian ? "Сохранить в XML" : "Save to XML", MinWidth = 125, IsDefault = true };
        save.Click += Save_Click;
        Button cancel = new() { Content = AppLocalization.IsRussian ? "Отмена" : "Cancel", MinWidth = 90, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        return root;
    }

    private void LoadValues()
    {
        _titleBox.Text = GetLocalized(Child(_card, "TITLE"), "ru-RU") ?? string.Empty;
        _flavorBox.Text = GetLocalized(Child(_card, "FLAVOURTEXT"), "ru-RU") ?? string.Empty;

        foreach (XElement ability in _card.Elements().Where(element => element.Name.LocalName.Contains("_ABILITY", StringComparison.OrdinalIgnoreCase)))
        {
            if (int.TryParse(Attribute(ability, "resource_id"), out int resourceId) && resourceId >= 0)
                continue;

            string english = GetLocalized(ability, "en-US") ?? string.Empty;
            string russian = GetLocalized(ability, "ru-RU") ?? string.Empty;

            Border block = new() { BorderBrush = System.Windows.Media.Brushes.Gray, BorderThickness = new Thickness(1), Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 8) };
            StackPanel panel = new();
            block.Child = panel;
            panel.Children.Add(new TextBlock
            {
                Text = $"{ability.Name.LocalName}: {english}",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.72,
                Margin = new Thickness(0, 0, 0, 5)
            });
            TextBox editor = new()
            {
                Text = russian,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 58,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            panel.Children.Add(editor);
            _abilitiesPanel.Children.Add(block);
            _abilityEditors.Add((ability, editor));
        }

        if (_abilityEditors.Count == 0)
        {
            _abilitiesPanel.Children.Add(new TextBlock
            {
                Text = AppLocalization.IsRussian ? "Редактируемых текстовых способностей нет." : "No editable text abilities.",
                Opacity = 0.7
            });
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetLocalized(EnsureChild(_card, "TITLE"), "ru-RU", _titleBox.Text);

            XElement? flavor = Child(_card, "FLAVOURTEXT");
            if (flavor is not null || !string.IsNullOrWhiteSpace(_flavorBox.Text))
                SetLocalized(flavor ?? EnsureChild(_card, "FLAVOURTEXT"), "ru-RU", _flavorBox.Text);

            foreach ((XElement element, TextBox editor) in _abilityEditors)
                SetLocalized(element, "ru-RU", editor.Text);

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
            MessageBox.Show(this, exception.Message, AppLocalization.IsRussian ? "Ошибка сохранения" : "Save error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 5) };

    private static XElement? FindCard(XDocument document) => document.Root?.Name.LocalName.Equals("CARD_V2", StringComparison.OrdinalIgnoreCase) == true
        ? document.Root
        : document.Descendants().FirstOrDefault(element => element.Name.LocalName.Equals("CARD_V2", StringComparison.OrdinalIgnoreCase));

    private static XElement? Child(XElement parent, string name) => parent.Elements().FirstOrDefault(element => element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static XElement EnsureChild(XElement parent, string name)
    {
        XElement? existing = Child(parent, name);
        if (existing is not null) return existing;
        XElement created = new(name);
        parent.Add(created);
        return created;
    }

    private static string Attribute(XElement element, string name) => element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty;

    private static string? GetLocalized(XElement? parent, string language) => parent?.Elements()
        .FirstOrDefault(element => element.Name.LocalName.Equals("LOCALISED_TEXT", StringComparison.OrdinalIgnoreCase)
            && Attribute(element, "LanguageCode").Equals(language, StringComparison.OrdinalIgnoreCase))?.Value;

    private static void SetLocalized(XElement parent, string language, string value)
    {
        XElement? localized = parent.Elements().FirstOrDefault(element => element.Name.LocalName.Equals("LOCALISED_TEXT", StringComparison.OrdinalIgnoreCase)
            && Attribute(element, "LanguageCode").Equals(language, StringComparison.OrdinalIgnoreCase));
        if (localized is null)
        {
            localized = new XElement("LOCALISED_TEXT", new XAttribute("LanguageCode", language));
            XElement? english = parent.Elements().FirstOrDefault(element => element.Name.LocalName.Equals("LOCALISED_TEXT", StringComparison.OrdinalIgnoreCase)
                && Attribute(element, "LanguageCode").Equals("en-US", StringComparison.OrdinalIgnoreCase));
            if (english is not null) english.AddAfterSelf(localized); else parent.Add(localized);
        }
        localized.Value = value ?? string.Empty;
    }
}
