using System.IO;
using System.Windows;
using System.Windows.Controls;
using DeckBuilder.Core.Models;

namespace DeckBuilder.Modern;

public partial class MainWindow
{
    private bool _cardTranslationEditorInstalled;

    internal void InstallCardTranslationEditor()
    {
        if (_cardTranslationEditorInstalled || Content is not DockPanel root)
            return;

        Menu? menu = root.Children.OfType<Menu>().FirstOrDefault();
        MenuItem? gameData = menu?.Items.OfType<MenuItem>()
            .FirstOrDefault(item => (item.Header?.ToString() ?? string.Empty)
                .Replace("_", string.Empty, StringComparison.Ordinal)
                .Equals("Game data", StringComparison.OrdinalIgnoreCase));
        if (gameData is null)
            return;

        gameData.Items.Add(new Separator());
        MenuItem editor = new()
        {
            Header = AppLocalization.IsRussian ? "Редактор _карты…" : "Card _editor…"
        };
        editor.Click += CardTranslationEditor_Click;
        gameData.Items.Add(editor);
        _cardTranslationEditorInstalled = true;
    }

    private async void CardTranslationEditor_Click(object sender, RoutedEventArgs e)
    {
        CardRecord? card = SelectedCardForTranslation();
        if (card is null)
        {
            MessageBox.Show(this,
                AppLocalization.IsRussian ? "Сначала выбери карту в каталоге или основной колоде." : "Select a card in the catalog or main deck first.",
                AppLocalization.IsRussian ? "Карта не выбрана" : "No card selected",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(_workspaceDirectory) || !Directory.Exists(_workspaceDirectory))
        {
            MessageBox.Show(this,
                AppLocalization.IsRussian
                    ? "Редактор пишет изменения прямо в CARD_V2 XML. Сначала загрузи распакованный workspace через «Данные игры → Загрузить распакованный workspace…»."
                    : "The editor writes changes directly into CARD_V2 XML. Load an unpacked workspace first.",
                AppLocalization.IsRussian ? "Нужен распакованный workspace" : "Unpacked workspace required",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        string[] candidates;
        try
        {
            candidates = Directory.EnumerateFiles(_workspaceDirectory, "*.xml", SearchOption.AllDirectories)
                .Where(path => Path.GetFileNameWithoutExtension(path).Equals(card.FileName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(path => PathContainsSource(path, card.Source))
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception)
        {
            ShowError("Could not search the workspace for the selected card XML", exception);
            return;
        }

        if (candidates.Length == 0)
        {
            MessageBox.Show(this,
                AppLocalization.IsRussian
                    ? $"Для {card.FileName} не найден распакованный XML. Возможно, эта карта сейчас доступна только внутри WAD."
                    : $"No unpacked XML was found for {card.FileName}. The card may currently exist only inside a WAD.",
                AppLocalization.IsRussian ? "XML не найден" : "XML not found",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        string? xmlPath = candidates.Length == 1 ? candidates[0] : ChooseTranslationSource(candidates, card.Source);
        if (string.IsNullOrWhiteSpace(xmlPath))
            return;

        CardTranslationEditorWindow dialog = new(card, xmlPath) { Owner = this };
        if (dialog.ShowDialog() != true || !dialog.Saved)
            return;

        Status(AppLocalization.IsRussian
            ? $"Карта {card.FileName} сохранена в XML; перечитываю workspace…"
            : $"Saved card {card.FileName} to XML; reloading workspace…");

        // Saving from the card editor may also install or replace a TDX illustration.
        // Make the normal Reload unpacked reconciliation authoritative here as well so
        // the manifest, catalog and image index immediately see every changed file.
        await LoadWorkspaceAsync(_workspaceDirectory, rescanPayload: true);

        CardRecord? refreshed = _catalog.FirstOrDefault(item => item.FileName.Equals(card.FileName, StringComparison.OrdinalIgnoreCase));
        if (refreshed is not null)
        {
            AvailableCardsGrid.SelectedItem = AvailableCards.FirstOrDefault(item => item.FileName.Equals(card.FileName, StringComparison.OrdinalIgnoreCase));
            AvailableCardsGrid.ScrollIntoView(AvailableCardsGrid.SelectedItem);
        }
    }

    private CardRecord? SelectedCardForTranslation()
    {
        if (AvailableCardsGrid.IsKeyboardFocusWithin && AvailableCardsGrid.SelectedItem is CardRecord catalogCard)
            return catalogCard;
        if (MainDeckGrid.IsKeyboardFocusWithin && MainDeckGrid.SelectedItem is DeckEntry main)
            return main.Card;
        if (RegularUnlocksGrid.IsKeyboardFocusWithin && RegularUnlocksGrid.SelectedItem is DeckEntry regular)
            return regular.Card;
        if (PromoUnlocksGrid.IsKeyboardFocusWithin && PromoUnlocksGrid.SelectedItem is DeckEntry promo)
            return promo.Card;

        if (AvailableCardsGrid.SelectedItem is CardRecord fallbackCatalog)
            return fallbackCatalog;
        if (MainDeckGrid.SelectedItem is DeckEntry fallbackMain)
            return fallbackMain.Card;
        return null;
    }

    private static bool PathContainsSource(string path, string source) =>
        !string.IsNullOrWhiteSpace(source)
        && path.Contains(source, StringComparison.OrdinalIgnoreCase);

    private string? ChooseTranslationSource(IReadOnlyList<string> paths, string source)
    {
        Window picker = new()
        {
            Owner = this,
            Title = AppLocalization.IsRussian ? "Выбери XML карты" : "Choose card XML",
            Width = 820,
            Height = 430,
            MinWidth = 600,
            MinHeight = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        Grid grid = new() { Margin = new Thickness(12) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = AppLocalization.IsRussian
                ? $"Найдено несколько определений. Текущий источник: {source}. Выбери XML, который нужно изменить."
                : $"Multiple definitions were found. Current source: {source}. Choose the XML to edit.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });

        ListBox list = new() { ItemsSource = paths, SelectedIndex = 0 };
        Grid.SetRow(list, 1);
        grid.Children.Add(list);

        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        Button ok = new() { Content = "OK", IsDefault = true, MinWidth = 90 };
        ok.Click += (_, _) => picker.DialogResult = true;
        Button cancel = new() { Content = AppLocalization.IsRussian ? "Отмена" : "Cancel", IsCancel = true, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 2);
        grid.Children.Add(buttons);
        picker.Content = grid;

        return picker.ShowDialog() == true ? list.SelectedItem as string : null;
    }
}
