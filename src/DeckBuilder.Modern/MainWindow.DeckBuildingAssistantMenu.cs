using System.Windows.Controls;

namespace DeckBuilder.Modern;

public partial class MainWindow
{
    private bool _deckBuildingAssistantMenuInstalled;

    internal void InstallDeckBuildingAssistantMenu()
    {
        if (_deckBuildingAssistantMenuInstalled || Content is not DockPanel root)
            return;

        Menu? menu = root.Children.OfType<Menu>().FirstOrDefault();
        MenuItem? deck = menu?.Items.OfType<MenuItem>()
            .FirstOrDefault(item => (item.Header?.ToString() ?? string.Empty)
                .Replace("_", string.Empty, StringComparison.Ordinal)
                .Equals("Deck", StringComparison.OrdinalIgnoreCase));
        if (deck is null)
            return;

        MenuItem assistant = new() { Header = "Deck building _assistant…" };
        assistant.Click += DeckBuildingAssistant_Click;
        deck.Items.Insert(0, assistant);
        deck.Items.Insert(1, new Separator());
        _deckBuildingAssistantMenuInstalled = true;
    }
}
