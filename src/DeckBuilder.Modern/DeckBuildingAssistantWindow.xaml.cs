using System.Windows;

namespace DeckBuilder.Modern;

public partial class DeckBuildingAssistantWindow : Window
{
    public DeckBuildingAssistantWindow(string guidance)
    {
        InitializeComponent();
        GuidanceText.Text = guidance;
        AppLocalization.Apply(this);
    }
}
