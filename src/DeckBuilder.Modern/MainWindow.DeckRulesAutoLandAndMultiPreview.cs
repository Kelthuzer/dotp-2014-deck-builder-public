using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using DeckBuilder.Core.Models;
using DeckBuilder.GameData;

namespace DeckBuilder.Modern;

public partial class MainWindow
{
    private bool _multiPreviewInstalled;
    private ComboBox? _previewCountBox;
    private WrapPanel? _neighborPreviewPanel;
    private int _neighborPreviewVersion;

    private static int SuggestedLandCount(double averageManaValue) => averageManaValue switch
    {
        > 0 and <= 2.2 => 23,
        >= 3.8 => 25,
        _ => 24
    };

    private IReadOnlyList<string> BuildDeckRuleWarnings()
    {
        bool ru = AppLocalization.IsRussian;
        List<string> warnings = new();
        int total = _deck.MainDeck.Sum(entry => entry.Quantity);
        if (total < 60)
            warnings.Add(ru ? $"меньше 60 карт ({total})" : $"fewer than 60 cards ({total})");

        foreach (IGrouping<string, DeckEntry> group in _deck.MainDeck
                     .Where(entry => !IsBasicLand(entry.Card))
                     .GroupBy(entry => CardIdentity(entry.Card), StringComparer.OrdinalIgnoreCase))
        {
            int copies = group.Sum(entry => entry.Quantity);
            if (copies <= 4)
                continue;

            string name = group.First().Card.LocalizedName;
            if (string.IsNullOrWhiteSpace(name))
                name = group.Key;
            warnings.Add(ru
                ? $"{name}: {copies} копий (макс. 4)"
                : $"{name}: {copies} copies (max 4)");
        }

        return warnings;
    }

    private static string CardIdentity(CardRecord card)
    {
        if (!string.IsNullOrWhiteSpace(card.EnglishName))
            return card.EnglishName.Trim();
        if (!string.IsNullOrWhiteSpace(card.LocalizedName))
            return card.LocalizedName.Trim();
        return card.FileName;
    }

    private static bool IsBasicLand(CardRecord card)
    {
        // BasicLandColors is deliberately strict for the five normal basics and already knows
        // about XMAS filename/localized-name variants. Use it as the primary source of truth so
        // random-deck and auto-land code do not reject a card after successfully identifying it.
        if (BasicLandColors(card).Count > 0)
            return true;

        if (!IsLand(card))
            return false;

        // Preserve support for unusual/custom basic lands outside the normal WUBRG five.
        return card.TypeLine.Contains("Basic", StringComparison.OrdinalIgnoreCase)
            || card.TypeLine.Contains("Базов", StringComparison.OrdinalIgnoreCase);
    }

    private void AutoFillLands_Click(object sender, RoutedEventArgs e)
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

        List<char> activeColors = "WUBRG".Where(color => demand[color] > 0).ToList();
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

        Dictionary<char, List<CardRecord>> basicLandVariants = new();
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

            basicLandVariants[color] = variants;
        }

        int totalDemand = activeColors.Sum(color => demand[color]);
        Dictionary<char, int> desired = activeColors.ToDictionary(
            color => color,
            color => (int)Math.Floor(targetLands * demand[color] / (double)totalDemand));
        int assigned = desired.Values.Sum();
        foreach (char color in activeColors
                     .OrderByDescending(color => targetLands * demand[color] / (double)totalDemand - desired[color]))
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
                .ThenByDescending(color => demand[color])
                .First();
            additions.Add(next);
            current[next]++;
        }

        Dictionary<string, int> existingVariantCounts = _deck.MainDeck
            .Where(entry => IsBasicLand(entry.Card))
            .GroupBy(entry => entry.Card.FileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(entry => entry.Quantity), StringComparer.OrdinalIgnoreCase);

        foreach (char color in additions)
        {
            List<CardRecord> variants = basicLandVariants[color];
            CardRecord chosen = variants
                .OrderBy(card => existingVariantCounts.GetValueOrDefault(card.FileName))
                .ThenBy(card => card.Expansion, StringComparer.OrdinalIgnoreCase)
                .ThenBy(card => card.FileName, StringComparer.OrdinalIgnoreCase)
                .First();

            _editor.Add(chosen, DeckSection.MainDeck);
            existingVariantCounts[chosen.FileName] = existingVariantCounts.GetValueOrDefault(chosen.FileName) + 1;
        }

        SetDirty(true);
        RefreshCollections();
        UpdateDeckAssistantDashboard();

        string colorSummary = string.Join(", ", additions
            .GroupBy(color => color)
            .Select(group => $"{group.Key} ×{group.Count()}"));
        int distinctArts = additions
            .SelectMany(color => basicLandVariants[color])
            .Select(card => card.FileName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        Status(ru
            ? $"Автоземли: добавлено {additions.Count} базовых земель ({colorSummary}); доступные арты перемешаны по {distinctArts} вариантам."
            : $"Auto lands: added {additions.Count} basic lands ({colorSummary}); art was mixed across {distinctArts} available variants.");
    }

    internal void InstallMultiCardPreview()
    {
        if (_multiPreviewInstalled || PreviewName.Parent is not Grid previewRoot)
            return;

        _multiPreviewInstalled = true;

        StackPanel selector = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 0, 8)
        };
        TextBlock label = new()
        {
            Text = AppLocalization.IsRussian ? "Превью:" : "Previews:",
            Margin = new Thickness(0, 0, 6, 0)
        };
        _previewCountBox = new ComboBox
        {
            Width = 58,
            SelectedIndex = 0,
            ToolTip = AppLocalization.IsRussian
                ? "Сколько карт показывать одновременно"
                : "How many cards to show at once"
        };
        for (int count = 1; count <= 5; count++)
            _previewCountBox.Items.Add(count);
        _previewCountBox.SelectionChanged += (_, _) => _ = RefreshNeighborPreviewsAsync();
        selector.Children.Add(label);
        selector.Children.Add(_previewCountBox);
        Grid.SetRow(selector, 0);
        previewRoot.Children.Add(selector);
        PreviewName.Margin = new Thickness(2, 0, 92, 10);

        previewRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _neighborPreviewPanel = new WrapPanel
        {
            Margin = new Thickness(2, 8, 2, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Grid.SetRow(_neighborPreviewPanel, previewRoot.RowDefinitions.Count - 1);
        previewRoot.Children.Add(_neighborPreviewPanel);

        AvailableCardsGrid.SelectionChanged += (_, _) => _ = RefreshNeighborPreviewsAsync();
    }

    private async Task RefreshNeighborPreviewsAsync()
    {
        if (_neighborPreviewPanel is null || _previewCountBox?.SelectedItem is not int count)
            return;

        int version = ++_neighborPreviewVersion;
        _neighborPreviewPanel.Children.Clear();
        if (count <= 1 || _cardImageLoader is null || AvailableCardsGrid.SelectedIndex < 0)
            return;

        int selectedIndex = AvailableCardsGrid.SelectedIndex;
        int sideCount = count - 1;
        int aboveCount = (sideCount + 1) / 2;
        int belowCount = sideCount / 2;
        List<CardRecord> neighbors = new();

        for (int offset = aboveCount; offset >= 1; offset--)
        {
            int index = selectedIndex - offset;
            if (index >= 0 && AvailableCardsGrid.Items[index] is CardRecord card)
                neighbors.Add(card);
        }
        for (int offset = 1; offset <= belowCount; offset++)
        {
            int index = selectedIndex + offset;
            if (index < AvailableCardsGrid.Items.Count && AvailableCardsGrid.Items[index] is CardRecord card)
                neighbors.Add(card);
        }

        double availableWidth = Math.Max(210, ActualWidth * 0.18);
        double cardWidth = Math.Clamp((availableWidth - Math.Max(0, neighbors.Count - 1) * 6) / Math.Max(1, Math.Min(2, neighbors.Count)), 105, 170);

        foreach (CardRecord card in neighbors)
        {
            FrameworkElement preview = await BuildFullNeighborCardAsync(card, cardWidth);
            if (version != _neighborPreviewVersion)
                return;
            _neighborPreviewPanel.Children.Add(preview);
        }
    }

    private async Task<FrameworkElement> BuildFullNeighborCardAsync(CardRecord card, double width)
    {
        CardVisualSpec visual = CardVisualMetadata.FromCard(card);

        PreviewArtLookup artLookup;
        try
        {
            artLookup = await PreviewArtResolver.ResolveAsync(_cardImageLoader!, card);
        }
        catch
        {
            artLookup = new PreviewArtLookup(null, card.ImageId, null, Array.Empty<string>());
        }

        Task<CardImageData?> frameTask = _cardImageLoader!.LoadAsync(visual.FrameId, GameImageKind.Frame);
        Task<CardImageData?> powerTask = _cardImageLoader.LoadAsync(visual.PowerBoxId, GameImageKind.Texture);
        Task<CardImageData?> rarityTask = _cardImageLoader.LoadAsync(visual.RarityId, GameImageKind.Texture);
        Task<CardImageData?> creditTask = _cardImageLoader.LoadAsync(visual.CreditId, GameImageKind.Texture);
        await Task.WhenAll(frameTask, powerTask, rarityTask, creditTask);

        Canvas canvas = new()
        {
            Width = 356,
            Height = 512,
            Background = Brushes.Black,
            ClipToBounds = true
        };

        Rectangle fallback = new()
        {
            Width = 356,
            Height = 512,
            Fill = new SolidColorBrush(Color.FromRgb(214, 211, 201))
        };
        canvas.Children.Add(fallback);

        CardImageData? frame = await frameTask;
        if (frame is not null)
        {
            Image frameImage = new()
            {
                Width = 356,
                Height = 512,
                Stretch = Stretch.Fill,
                Source = ToBitmapSource(frame)
            };
            Panel.SetZIndex(frameImage, 1);
            canvas.Children.Add(frameImage);
        }

        if (artLookup.Image is not null)
        {
            Image art = new()
            {
                Stretch = Stretch.Fill,
                Source = ToBitmapSource(artLookup.Image)
            };
            if (visual.FullBleedArt)
            {
                Canvas.SetLeft(art, 8);
                Canvas.SetTop(art, 35);
                art.Width = 340;
                art.Height = 430;
            }
            else
            {
                Canvas.SetLeft(art, 16);
                Canvas.SetTop(art, 47);
                art.Width = 324;
                art.Height = 238;
            }
            // Match the normal preview layer order: frame background first, artwork above it,
            // then title/type/rules symbols on top. Keeping art below the frame caused the
            // characteristic vertical split/white panel in multi-card previews.
            Panel.SetZIndex(art, 2);
            canvas.Children.Add(art);
        }

        TextBlock title = CardText(
            string.IsNullOrWhiteSpace(card.LocalizedName) ? card.FileName : card.LocalizedName,
            18,
            FontWeights.Bold,
            278,
            25);
        Canvas.SetLeft(title, 12);
        Canvas.SetTop(title, 13);
        Panel.SetZIndex(title, 3);
        canvas.Children.Add(title);

        StackPanel mana = new()
        {
            Orientation = Orientation.Horizontal,
            Height = 25
        };
        foreach (string imageId in visual.ManaImageIds)
        {
            ImageSource? source = EmbeddedManaSymbols.TryGet(imageId);
            if (source is null)
                continue;
            mana.Children.Add(new Image
            {
                Source = source,
                Width = 22,
                Height = 22,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(1, 0, 0, 0)
            });
        }
        mana.Measure(new Size(double.PositiveInfinity, 25));
        Canvas.SetLeft(mana, Math.Max(290, 344 - mana.DesiredSize.Width));
        Canvas.SetTop(mana, 8);
        Panel.SetZIndex(mana, 4);
        canvas.Children.Add(mana);

        TextBlock type = CardText(card.TypeLine, 14, FontWeights.Normal, 286, 23);
        Canvas.SetLeft(type, 14);
        Canvas.SetTop(type, 294);
        Panel.SetZIndex(type, 3);
        canvas.Children.Add(type);

        CardImageData? rarity = await rarityTask;
        if (rarity is not null)
        {
            Image rarityImage = new()
            {
                Source = ToBitmapSource(rarity),
                Width = 50,
                Height = 25,
                Stretch = Stretch.Uniform
            };
            Canvas.SetLeft(rarityImage, 302);
            Canvas.SetTop(rarityImage, 292);
            Panel.SetZIndex(rarityImage, 3);
            canvas.Children.Add(rarityImage);
        }

        string rules = card.RulesText;
        if (!string.IsNullOrWhiteSpace(card.FlavorText))
        {
            if (!string.IsNullOrWhiteSpace(rules))
                rules += Environment.NewLine + Environment.NewLine;
            rules += card.FlavorText;
        }
        TextBlock rulesText = new()
        {
            Text = rules,
            Width = 324,
            Height = 150,
            FontFamily = new FontFamily("Georgia"),
            FontSize = 13,
            LineHeight = 16,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Brushes.Black
        };
        Canvas.SetLeft(rulesText, 15);
        Canvas.SetTop(rulesText, 324);
        Panel.SetZIndex(rulesText, 3);
        canvas.Children.Add(rulesText);

        CardImageData? power = await powerTask;
        if (visual.ShowsPower)
        {
            if (power is not null)
            {
                Image powerImage = new()
                {
                    Source = ToBitmapSource(power),
                    Width = 130,
                    Height = 65,
                    Stretch = Stretch.Fill
                };
                Canvas.SetLeft(powerImage, 245);
                Canvas.SetTop(powerImage, 453);
                Panel.SetZIndex(powerImage, 3);
                canvas.Children.Add(powerImage);
            }

            TextBlock powerText = CardText($"{card.Power} / {card.Toughness}", 17, FontWeights.Bold, 60, 23);
            powerText.TextAlignment = TextAlignment.Center;
            Canvas.SetLeft(powerText, 280);
            Canvas.SetTop(powerText, 474);
            Panel.SetZIndex(powerText, 4);
            canvas.Children.Add(powerText);
        }

        CardImageData? credit = await creditTask;
        if (credit is not null)
        {
            Image creditImage = new()
            {
                Source = ToBitmapSource(credit),
                Width = 34,
                Height = 9,
                Stretch = Stretch.Fill
            };
            Canvas.SetLeft(creditImage, 10);
            Canvas.SetTop(creditImage, 488);
            Panel.SetZIndex(creditImage, 3);
            canvas.Children.Add(creditImage);
        }

        TextBlock artist = CardText(card.Artist, 10, FontWeights.Bold, 230, 17);
        Canvas.SetLeft(artist, 42);
        Canvas.SetTop(artist, 482);
        Panel.SetZIndex(artist, 4);
        canvas.Children.Add(artist);

        Viewbox viewbox = new()
        {
            Width = width,
            Height = width * 512 / 356,
            Stretch = Stretch.Uniform,
            Child = canvas,
            Margin = new Thickness(3),
            ToolTip = string.IsNullOrWhiteSpace(card.LocalizedName) ? card.FileName : card.LocalizedName
        };
        return viewbox;
    }

    private static TextBlock CardText(string? text, double size, FontWeight weight, double width, double height) => new()
    {
        Text = text ?? string.Empty,
        Width = width,
        Height = height,
        FontFamily = new FontFamily("Georgia"),
        FontSize = size,
        FontWeight = weight,
        TextTrimming = TextTrimming.CharacterEllipsis,
        Foreground = Brushes.Black
    };
}
