using System.Windows;
using System.Windows.Controls;

namespace DeckBuilder.Modern;

internal static class AppLocalization
{
    private static readonly IReadOnlyDictionary<string, string> EnglishToRussian =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DotP 2014 Deck Builder — Modern"] = "DotP 2014 Deck Builder — Modern",
            ["File"] = "Файл",
            ["New project"] = "Новый проект",
            ["Create from existing game deck…"] = "Создать из существующей колоды…",
            ["Open project…"] = "Открыть проект…",
            ["Save project"] = "Сохранить проект",
            ["Save project as…"] = "Сохранить проект как…",
            ["Import deck XML…"] = "Импортировать XML колоды…",
            ["Export deck XML…"] = "Экспортировать XML колоды…",
            ["Export game WAD…"] = "Экспортировать WAD…",
            ["Exit"] = "Выход",
            ["Deck"] = "Колода",
            ["Deck information…"] = "Информация о колоде…",
            ["Deck library…"] = "Библиотека колод…",
            ["Deck building assistant…"] = "Помощник сборки колоды…",
            ["Game data"] = "Данные игры",
            ["Load Magic 2014 folder…"] = "Загрузить папку Magic 2014…",
            ["Reload current folder"] = "Перезагрузить текущую папку",
            ["Load unpacked workspace…"] = "Загрузить распакованные ресурсы…",
            ["Reload unpacked workspace"] = "Перезагрузить распакованные ресурсы",
            ["WAD Workshop…"] = "Мастерская WAD…",
            ["Build cards/decks from unpacked…"] = "Собрать карты/колоды из распакованных ресурсов…",
            ["Card reference scanner…"] = "Сканер ссылок карт…",
            ["Help"] = "Помощь",
            ["About modern build"] = "О программе",
            ["Settings…"] = "Настройки…",
            ["Card catalog"] = "Каталог карт",
            ["Card preview"] = "Предпросмотр карты",
            ["Main deck"] = "Основная колода",
            ["Unlock order"] = "Порядок разблокировки",
            ["Regular unlocks"] = "Обычные разблокировки",
            ["Promo unlocks (maximum 10)"] = "Промо-разблокировки (максимум 10)",
            ["Add to deck"] = "Добавить в колоду",
            ["Add unlock"] = "Добавить в разблокировки",
            ["Add promo"] = "Добавить в промо",
            ["+1 copy"] = "+1 копия",
            ["Remove"] = "Удалить",
            ["Move to unlocks"] = "В разблокировки",
            ["Card"] = "Карта",
            ["Cost"] = "Стоимость",
            ["Type"] = "Тип",
            ["Set"] = "Сет",
            ["Filename"] = "Имя файла",
            ["Qty"] = "Кол-во",
            ["Reference"] = "Ссылка",
            ["Hide tokens"] = "Скрыть токены",
            ["Lands"] = "Земли",
            ["Units"] = "Юниты",
            ["Spells"] = "Заклинания",
            ["Instants"] = "Мгновенные",
            ["Sorceries"] = "Сорсери",
            ["Enchantments"] = "Зачарования",
            ["Artifacts"] = "Артефакты",
            ["Mana:"] = "Мана:",
            ["Type:"] = "Тип:",
            ["Colorless"] = "Бесцветные",
            ["Reset filters"] = "Сбросить фильтры",
            ["Card name"] = "Название карты",
            ["Type / tag"] = "Тип / тег",
            ["Sort:"] = "Сортировка:",
            ["Default"] = "По названию",
            ["Mana value"] = "Мана-стоимость",
            ["Rarity"] = "Редкость",
            ["Mana cost"] = "Цена маны",
            ["Select a card to load its art"] = "Выберите карту для загрузки изображения",
            ["Deck cover"] = "Обложка колоды",
            ["Deck building assistant"] = "Помощник сборки колоды",
            ["Deck colors"] = "Цвета колоды",
            ["Current deck guidance"] = "Рекомендации по текущей колоде",
            ["White (W)"] = "Белый (W)",
            ["Blue (U)"] = "Синий (U)",
            ["Black (B)"] = "Чёрный (B)",
            ["Red (R)"] = "Красный (R)",
            ["Green (G)"] = "Зелёный (G)",
            ["Cancel"] = "Отмена",
            ["Apply"] = "Применить",
            ["Choose one or more deck colors for mana-base guidance. The assistant does not filter or change the card catalog."] =
                "Выберите один или несколько цветов колоды для оценки манабазы. Помощник не фильтрует и не изменяет каталог карт.",
            ["Reference model: 60-card constructed deck; mana base adjusted by curve and colored requirements."] =
                "Ориентир: constructed-колода из 60 карт; манабаза корректируется по кривой маны и цветовым требованиям.",
            ["Application settings"] = "Настройки приложения",
            ["Language"] = "Язык",
            ["Theme"] = "Тема",
            ["System"] = "Системная",
            ["Light"] = "Светлая",
            ["Dark"] = "Тёмная",
            ["Russian"] = "Русский",
            ["English"] = "English",
            ["The interface language changes immediately and is saved for the next launch."] =
                "Язык интерфейса изменяется сразу и сохраняется для следующего запуска.",
            ["Language and theme changes are applied immediately and saved for the next launch."] =
                "Язык и тема применяются сразу и сохраняются для следующего запуска."
        };

    private static readonly IReadOnlyDictionary<string, string> RussianToEnglish = BuildReverseMap();

    public static bool IsRussian => AppSettingsService.Current.Language == "ru";

    public static string Text(string english)
    {
        if (!IsRussian)
            return EnglishToRussian.ContainsKey(english) ? english : RussianToEnglish.GetValueOrDefault(english, english);

        string normalizedEnglish = RussianToEnglish.GetValueOrDefault(english, english);
        return EnglishToRussian.GetValueOrDefault(normalizedEnglish, normalizedEnglish);
    }

    public static void ApplyToOpenWindows()
    {
        foreach (Window window in Application.Current.Windows)
            Apply(window);
    }

    public static void Apply(Window window)
    {
        window.Title = TranslateKnown(window.Title);
        ApplyElement(window.Content as DependencyObject);
    }

    private static void ApplyElement(DependencyObject? element)
    {
        if (element is null)
            return;

        switch (element)
        {
            case TextBlock textBlock:
                textBlock.Text = TranslateKnown(textBlock.Text);
                break;
            case Button button when button.Content is string text:
                button.Content = TranslateKnown(text);
                break;
            case CheckBox checkBox when checkBox.Content is string text:
                checkBox.Content = TranslateKnown(text);
                break;
            case ComboBoxItem comboBoxItem when comboBoxItem.Content is string text:
                comboBoxItem.Content = TranslateKnown(text);
                break;
            case GroupBox groupBox when groupBox.Header is string text:
                groupBox.Header = TranslateKnown(text);
                break;
            case MenuItem menuItem when menuItem.Header is string text:
                menuItem.Header = TranslateKnown(text.Replace("_", string.Empty, StringComparison.Ordinal));
                break;
            case DataGrid dataGrid:
                foreach (DataGridColumn column in dataGrid.Columns)
                {
                    if (column.Header is string header)
                        column.Header = TranslateKnown(header);
                }
                break;
        }

        if (element is Panel panel)
        {
            foreach (UIElement child in panel.Children)
                ApplyElement(child);
        }
        else if (element is Border border)
        {
            ApplyElement(border.Child);
        }
        else if (element is ContentControl contentControl && contentControl.Content is DependencyObject content)
        {
            ApplyElement(content);
        }
        else if (element is ItemsControl itemsControl)
        {
            foreach (object item in itemsControl.Items)
                if (item is DependencyObject dependencyObject)
                    ApplyElement(dependencyObject);
        }
    }

    private static string TranslateKnown(string text)
    {
        string plain = text.Replace("_", string.Empty, StringComparison.Ordinal);
        string english = RussianToEnglish.GetValueOrDefault(plain, plain);
        return IsRussian ? EnglishToRussian.GetValueOrDefault(english, plain) : english;
    }

    private static IReadOnlyDictionary<string, string> BuildReverseMap()
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        foreach ((string english, string translated) in EnglishToRussian)
            result.TryAdd(translated, english);
        return result;
    }
}
