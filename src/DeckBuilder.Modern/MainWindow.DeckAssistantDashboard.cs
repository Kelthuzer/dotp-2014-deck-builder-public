using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DeckBuilder.Core.Models;

namespace DeckBuilder.Modern;

public partial class MainWindow
{
    private bool _deckAssistantDashboardInstalled;
    private TextBlock? _deckAssistantDashboardTitle;
    private TextBlock? _deckAssistantDashboardText;
    private Button? _deckAssistantConfigureButton;
    private Button? _deckAssistantAutoLandButton;
    private DispatcherTimer? _deckAssistantDashboardTimer;

    internal void InstallDeckAssistantDashboard()
    {
        if (_deckAssistantDashboardInstalled || Content is not DockPanel root)
            return;

        Grid? workspaceGrid = root.Children.OfType<Grid>().LastOrDefault();
        Border? rightBorder = workspaceGrid?.Children.OfType<Border>()
            .FirstOrDefault(border => Grid.GetColumn(border) == 4);
        if (rightBorder?.Child is not Grid rightGrid || rightGrid.RowDefinitions.Count < 6)
            return;

        TextBlock? originalTitle = rightGrid.Children.OfType<TextBlock>()
            .FirstOrDefault(child => Grid.GetRow(child) == 0);
        if (originalTitle is null)
            return;

        _deckAssistantDashboardInstalled = true;
        rightGrid.Children.Remove(originalTitle);

        Grid header = new()
        {
            Margin = new Thickness(2, 0, 0, 8)
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        TextBlock mainDeckTitle = new()
        {
            Text = "Main deck",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(mainDeckTitle, 0);
        header.Children.Add(mainDeckTitle);

        _deckAssistantDashboardTitle = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };

        _deckAssistantDashboardText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        };
        _deckAssistantDashboardText.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

        StackPanel assistantLine = new()
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        assistantLine.Children.Add(_deckAssistantDashboardTitle);
        assistantLine.Children.Add(_deckAssistantDashboardText);
        Grid.SetColumn(assistantLine, 2);
        header.Children.Add(assistantLine);

        StackPanel actions = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };

        _deckAssistantAutoLandButton = new Button
        {
            MinWidth = 105,
            Padding = new Thickness(8, 2, 8, 2)
        };
        _deckAssistantAutoLandButton.Click += AutoFillLands_Click;
        actions.Children.Add(_deckAssistantAutoLandButton);

        _deckAssistantConfigureButton = new Button
        {
            MinWidth = 88,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(8, 2, 8, 2)
        };
        _deckAssistantConfigureButton.Click += DeckBuildingAssistant_Click;
        actions.Children.Add(_deckAssistantConfigureButton);

        Grid.SetColumn(actions, 3);
        header.Children.Add(actions);
        Grid.SetRow(header, 0);
        rightGrid.Children.Add(header);

        _deckAssistantDashboardTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        _deckAssistantDashboardTimer.Tick += (_, _) => UpdateDeckAssistantDashboard();
        _deckAssistantDashboardTimer.Start();
        Closed += (_, _) => _deckAssistantDashboardTimer?.Stop();

        UpdateDeckAssistantDashboard();
    }

    internal void UpdateDeckAssistantDashboard()
    {
        if (_deckAssistantDashboardText is null ||
            _deckAssistantDashboardTitle is null ||
            _deckAssistantConfigureButton is null ||
            _deckAssistantAutoLandButton is null)
            return;

        bool ru = AppLocalization.IsRussian;
        _deckAssistantDashboardTitle.Text = ru ? "Помощник:" : "Assistant:";
        _deckAssistantConfigureButton.Content = ru ? "Подробнее…" : "Details…";
        _deckAssistantAutoLandButton.Content = ru ? "Автоземли" : "Auto lands";

        int cards = _deck.MainDeck.Sum(entry => entry.Quantity);
        int lands = _deck.MainDeck.Where(entry => IsLand(entry.Card)).Sum(entry => entry.Quantity);
        int nonlands = cards - lands;
        double averageManaValue = EstimateAverageManaValue();
        int targetLands = SuggestedLandCount(averageManaValue);
        int targetNonlands = 60 - targetLands;
        int cardsNeeded = Math.Max(0, 60 - cards);
        int landsNeeded = Math.Max(0, targetLands - lands);
        int nonlandsNeeded = Math.Max(0, targetNonlands - nonlands);
        IReadOnlyList<string> ruleWarnings = BuildDeckRuleWarnings();

        StringBuilder compact = new();
        if (ru)
        {
            compact.Append($"Колода {cards}/60");
            if (cardsNeeded > 0) compact.Append($" (+{cardsNeeded})");
            else if (cards > 60) compact.Append($" (−{cards - 60})");
            compact.Append($"  •  Земли {lands}/~{targetLands}");
            if (landsNeeded > 0) compact.Append($" (+~{landsNeeded})");
            if (averageManaValue > 0) compact.Append($"  •  Ср. мана {averageManaValue:0.0}");
            compact.Append(ruleWarnings.Count == 0 ? "  •  Правила: OK" : $"  •  Нарушения: {ruleWarnings.Count}");
        }
        else
        {
            compact.Append($"Deck {cards}/60");
            if (cardsNeeded > 0) compact.Append($" (+{cardsNeeded})");
            else if (cards > 60) compact.Append($" (−{cards - 60})");
            compact.Append($"  •  Lands {lands}/~{targetLands}");
            if (landsNeeded > 0) compact.Append($" (+~{landsNeeded})");
            if (averageManaValue > 0) compact.Append($"  •  Avg mana {averageManaValue:0.0}");
            compact.Append(ruleWarnings.Count == 0 ? "  •  Rules: OK" : $"  •  Issues: {ruleWarnings.Count}");
        }
        _deckAssistantDashboardText.Text = compact.ToString();

        StringBuilder details = new();
        if (ru)
        {
            details.Append($"Колода: {cards}/60; земли: {lands}/~{targetLands}; неземельные: {nonlands}/~{targetNonlands}");
            if (nonlandsNeeded > 0) details.Append($"; неземельных ещё ~{nonlandsNeeded}");
            if (averageManaValue > 0) details.Append($"; средняя мана: {averageManaValue:0.0}");
        }
        else
        {
            details.Append($"Deck: {cards}/60; lands: {lands}/~{targetLands}; nonlands: {nonlands}/~{targetNonlands}");
            if (nonlandsNeeded > 0) details.Append($"; ~{nonlandsNeeded} more nonlands");
            if (averageManaValue > 0) details.Append($"; average mana: {averageManaValue:0.0}");
        }

        Dictionary<char, int> pips = CountColoredManaPips();
        int totalPips = pips.Values.Sum();
        if (totalPips > 0)
        {
            details.AppendLine();
            details.Append(ru ? "Цветные источники: " : "Colored sources: ");
            bool first = true;
            foreach (char color in "WUBRG")
            {
                int pipCount = pips[color];
                if (pipCount == 0)
                    continue;
                if (!first) details.Append(" • ");
                first = false;

                int targetSources = Math.Max(1, (int)Math.Round(targetLands * (pipCount / (double)totalPips)));
                int currentBasicSources = CountBasicLandSources(color);
                details.Append($"{color}: {currentBasicSources}/~{targetSources}");
            }
        }

        details.AppendLine();
        if (ruleWarnings.Count == 0)
            details.Append(ru ? "Правила: базовые проверки пройдены" : "Rules: basic checks passed");
        else
            details.Append((ru ? "Нарушения: " : "Issues: ") + string.Join(" • ", ruleWarnings));

        ToolTipService.SetToolTip(_deckAssistantDashboardText, details.ToString());
    }

    private int CountBasicLandSources(char color)
    {
        int result = 0;
        foreach (DeckEntry entry in _deck.MainDeck.Where(entry => IsLand(entry.Card)))
        {
            if (BasicLandColors(entry.Card).Contains(color))
                result += entry.Quantity;
        }
        return result;
    }
}
