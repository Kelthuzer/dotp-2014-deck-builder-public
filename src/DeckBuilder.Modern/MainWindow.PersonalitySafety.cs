using System.IO;
using System.Windows;
using DeckBuilder.GameData;

namespace DeckBuilder.Modern;

public partial class MainWindow
{
    private async Task<bool> EnsureDeckPersonalityAsync()
    {
        if (_deck.CustomPersonality is not null || !string.IsNullOrWhiteSpace(_deck.Personality))
            return true;

        if (string.IsNullOrWhiteSpace(_gameDirectory) || !Directory.Exists(_gameDirectory))
        {
            MessageBox.Show(this,
                "У колоды не задан AI personality, а папка игры не загружена. Без personality у ИИ пропадает аватар и отображение игровых зон.",
                "Не задан AI personality",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }

        try
        {
            GamePersonalityCatalogLoadResult catalog = await new GamePersonalityCatalogLoader().LoadAsync(_gameDirectory);
            InstalledPersonalityRecord? fallback = GamePersonalityFallbackSelector.SelectBest(catalog.Personalities);
            if (fallback is null)
            {
                MessageBox.Show(this,
                    "Не удалось найти установленный AI personality с большой и малой иконкой. Упаковка остановлена, чтобы не создавать колоду без аватара.",
                    "Нет подходящего AI personality",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }

            _deck.Personality = fallback.FileName;
            SetDirty(true);
            Status($"Для колоды автоматически выбран AI personality: {fallback.DisplayName} ({fallback.FileName}).");
            return true;
        }
        catch (Exception exception)
        {
            ShowError("Не удалось подобрать AI personality для колоды", exception);
            return false;
        }
    }
}
