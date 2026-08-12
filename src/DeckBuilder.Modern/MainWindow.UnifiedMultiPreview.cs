using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DeckBuilder.Core.Models;

namespace DeckBuilder.Modern;

public partial class MainWindow
{
    private bool _unifiedMultiPreviewInstalled;
    private Grid? _unifiedPreviewPanel;
    private Grid? _unifiedPreviewHost;
    private int _unifiedPreviewVersion;
    private bool _previewFromMainDeck;
    private bool _carouselStateValid;
    private bool _carouselFromMainDeck;
    private int _carouselPreviewCount;
    private int _carouselSelectionIndex = -1;

    internal void InstallUnifiedMultiPreview()
    {
        if (_unifiedMultiPreviewInstalled || CardPreviewViewbox.Parent is not Grid previewHost)
            return;

        _unifiedMultiPreviewInstalled = true;
        _unifiedPreviewHost = previewHost;

        if (_neighborPreviewPanel is not null)
        {
            _neighborPreviewPanel.Visibility = Visibility.Collapsed;
            _neighborPreviewPanel.Height = 0;
            _neighborPreviewPanel.Margin = new Thickness(0);
        }

        // Layer host for the current preview set. During a carousel step the previous and next
        // layers briefly coexist, but shared cards are moved between their old/new slots instead
        // of being faded or redrawn as a slideshow.
        _unifiedPreviewPanel = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(4),
            ClipToBounds = true
        };
        Panel.SetZIndex(_unifiedPreviewPanel, 20);
        previewHost.Children.Add(_unifiedPreviewPanel);

        AvailableCardsGrid.SelectionChanged += (_, _) =>
        {
            if (AvailableCardsGrid.IsKeyboardFocusWithin)
                _previewFromMainDeck = false;
            _ = RefreshUnifiedMultiPreviewAsync();
        };
        AvailableCardsGrid.GotKeyboardFocus += (_, _) =>
        {
            _previewFromMainDeck = false;
            _ = RefreshUnifiedMultiPreviewAsync();
        };

        MainDeckGrid.SelectionChanged += (_, _) =>
        {
            if (MainDeckGrid.SelectedItem is not null)
            {
                _previewFromMainDeck = true;
                _ = RefreshUnifiedMultiPreviewAsync();
            }
        };
        MainDeckGrid.GotKeyboardFocus += (_, _) =>
        {
            if (MainDeckGrid.SelectedItem is not null)
            {
                _previewFromMainDeck = true;
                _ = RefreshUnifiedMultiPreviewAsync();
            }
        };

        CardPreviewViewbox.PreviewMouseLeftButtonDown += SinglePreview_MouseLeftButtonDown;
        previewHost.SizeChanged += (_, _) => _ = RefreshUnifiedMultiPreviewAsync();

        if (_previewCountBox is not null)
            _previewCountBox.SelectionChanged += (_, _) => _ = RefreshUnifiedMultiPreviewAsync();

        AttachComboBoxFocusRelease(this);
        _ = RefreshUnifiedMultiPreviewAsync();
    }

    private async Task RefreshUnifiedMultiPreviewAsync()
    {
        if (_unifiedPreviewPanel is null || _unifiedPreviewHost is null || _previewCountBox?.SelectedItem is not int requested)
            return;

        int version = ++_unifiedPreviewVersion;
        int count = Math.Clamp(requested, 1, 5);
        CardRecord? activeCard = GetActivePreviewCard();

        if (count <= 1 || _cardImageLoader is null || activeCard is null)
        {
            ClearUnifiedPreviewGrid();
            _carouselStateValid = false;
            _unifiedPreviewPanel.Visibility = Visibility.Collapsed;
            CardPreviewViewbox.Visibility = activeCard is not null ? Visibility.Visible : Visibility.Collapsed;
            PreviewPlaceholder.Visibility = activeCard is not null ? Visibility.Collapsed : Visibility.Visible;
            return;
        }

        List<(CardRecord Card, bool Active)> cards = BuildPreviewWindow(count);
        if (cards.Count == 0)
            return;

        int selectionIndex = _previewFromMainDeck
            ? MainDeckGrid.SelectedIndex
            : AvailableCardsGrid.SelectedIndex;
        int direction = 0;
        if (_carouselStateValid
            && _carouselFromMainDeck == _previewFromMainDeck
            && _carouselPreviewCount == count
            && selectionIndex >= 0
            && _carouselSelectionIndex >= 0)
        {
            direction = Math.Sign(selectionIndex - _carouselSelectionIndex);
        }

        CardPreviewViewbox.Visibility = Visibility.Collapsed;
        PreviewPlaceholder.Visibility = Visibility.Collapsed;
        _unifiedPreviewPanel.Visibility = Visibility.Visible;

        (int rows, int columns) = cards.Count switch
        {
            2 => (1, 2),
            3 => (1, 3),
            4 => (2, 2),
            5 => (2, 3),
            _ => (1, 1)
        };

        double hostWidth = Math.Max(240, _unifiedPreviewHost.ActualWidth - 18);
        double hostHeight = Math.Max(220, _unifiedPreviewHost.ActualHeight - 18);
        const double gap = 8;
        double cellWidth = Math.Max(90, (hostWidth - gap * (columns - 1)) / columns);
        double cellHeight = Math.Max(120, (hostHeight - gap * (rows - 1)) / rows);
        double cardWidth = Math.Min(cellWidth - 10, (cellHeight - 10) * 356.0 / 512.0);
        cardWidth = Math.Max(72, cardWidth);
        double cardHeight = cardWidth * 512.0 / 356.0;

        Grid nextLayer = new()
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        for (int row = 0; row < rows; row++)
            nextLayer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        for (int column = 0; column < columns; column++)
            nextLayer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Build the whole next set before touching the visible one. Rendering remains off the
        // animation path; the animation itself only changes TranslateTransform values.
        for (int index = 0; index < cards.Count; index++)
        {
            (CardRecord card, bool active) = cards[index];
            FrameworkElement rendered = await BuildExactCardPreviewAsync(card, cardWidth);
            if (version != _unifiedPreviewVersion)
                return;

            rendered.Width = cardWidth;
            rendered.Height = cardHeight;
            rendered.Margin = new Thickness(0);

            Border shell = new()
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Width = cardWidth + 6,
                Height = cardHeight + 6,
                Padding = new Thickness(2),
                Margin = new Thickness(4),
                BorderThickness = new Thickness(active ? 2 : 1),
                BorderBrush = active
                    ? (Brush)(Application.Current.TryFindResource("AccentBrush") ?? Brushes.DodgerBlue)
                    : (Brush)(Application.Current.TryFindResource("BorderBrush") ?? Brushes.Gray),
                CornerRadius = new CornerRadius(3),
                Child = rendered,
                Tag = card,
                ToolTip = active
                    ? (AppLocalization.IsRussian ? "Выбранная карта" : "Selected card")
                    : null
            };
            shell.PreviewMouseLeftButtonDown += MultiPreviewCard_MouseLeftButtonDown;

            int row;
            int column;
            if (cards.Count == 5 && index >= 3)
            {
                row = 1;
                column = index == 3 ? 0 : 2;
            }
            else
            {
                row = index / columns;
                column = index % columns;
            }

            Grid.SetRow(shell, row);
            Grid.SetColumn(shell, column);
            nextLayer.Children.Add(shell);
        }

        if (version != _unifiedPreviewVersion)
            return;

        SwapUnifiedPreviewLayer(nextLayer, direction);
        _carouselStateValid = true;
        _carouselFromMainDeck = _previewFromMainDeck;
        _carouselPreviewCount = count;
        _carouselSelectionIndex = selectionIndex;
    }

    private void SwapUnifiedPreviewLayer(Grid nextLayer, int direction)
    {
        if (_unifiedPreviewPanel is null)
            return;

        Grid? previousLayer = _unifiedPreviewPanel.Children.OfType<Grid>().LastOrDefault();
        bool animate = AppSettingsService.Current.SmoothPreviewTransitions
            && previousLayer is not null
            && direction != 0
            && previousLayer.RowDefinitions.Count == nextLayer.RowDefinitions.Count
            && previousLayer.ColumnDefinitions.Count == nextLayer.ColumnDefinitions.Count;

        // Stop a previous transition immediately. We never queue motion after the user stops.
        foreach (FrameworkElement element in _unifiedPreviewPanel.Children
                     .OfType<Grid>()
                     .SelectMany(layer => layer.Children.OfType<FrameworkElement>()))
        {
            if (element.RenderTransform is TranslateTransform transform)
            {
                transform.BeginAnimation(TranslateTransform.XProperty, null);
                transform.BeginAnimation(TranslateTransform.YProperty, null);
            }
        }

        while (_unifiedPreviewPanel.Children.Count > 1)
            _unifiedPreviewPanel.Children.RemoveAt(0);

        if (!animate || previousLayer is null)
        {
            _unifiedPreviewPanel.Children.Clear();
            _unifiedPreviewPanel.Children.Add(nextLayer);
            return;
        }

        double panelWidth = Math.Max(1, _unifiedPreviewPanel.ActualWidth);
        double panelHeight = Math.Max(1, _unifiedPreviewPanel.ActualHeight);
        Dictionary<string, Border> previousByCard = previousLayer.Children
            .OfType<Border>()
            .Where(shell => shell.Tag is CardRecord)
            .GroupBy(shell => ((CardRecord)shell.Tag).FileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        HashSet<Border> sharedPrevious = new();

        foreach (Border nextShell in nextLayer.Children.OfType<Border>())
        {
            TranslateTransform transform = new();
            nextShell.RenderTransform = transform;

            if (nextShell.Tag is CardRecord card
                && previousByCard.TryGetValue(card.FileName, out Border? oldShell))
            {
                Point oldCenter = SlotCenter(previousLayer, oldShell, panelWidth, panelHeight);
                Point newCenter = SlotCenter(nextLayer, nextShell, panelWidth, panelHeight);
                transform.X = oldCenter.X - newCenter.X;
                transform.Y = oldCenter.Y - newCenter.Y;
                oldShell.Visibility = Visibility.Hidden;
                sharedPrevious.Add(oldShell);
            }
            else
            {
                // Only the newly entering card starts outside the viewport.
                transform.X = direction > 0 ? panelWidth : -panelWidth;
                transform.Y = 0;
            }
        }

        // Keep only cards that actually leave the window visible in the old layer. Shared cards are
        // represented by their moving copy in nextLayer, avoiding doubled/ghosted artwork.
        foreach (Border oldShell in previousLayer.Children.OfType<Border>())
        {
            if (sharedPrevious.Contains(oldShell))
                continue;

            TranslateTransform transform = oldShell.RenderTransform as TranslateTransform ?? new TranslateTransform();
            oldShell.RenderTransform = transform;
            transform.X = 0;
            transform.Y = 0;
        }

        _unifiedPreviewPanel.Children.Add(nextLayer);

        TimeSpan duration = TimeSpan.FromMilliseconds(230);
        IEasingFunction easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        int animationsRemaining = 0;

        void CompleteOne(object? sender, EventArgs e)
        {
            animationsRemaining--;
            if (animationsRemaining > 0 || _unifiedPreviewPanel is null)
                return;

            foreach (Border shell in nextLayer.Children.OfType<Border>())
            {
                shell.RenderTransform = Transform.Identity;
            }
            if (_unifiedPreviewPanel.Children.Contains(previousLayer))
                _unifiedPreviewPanel.Children.Remove(previousLayer);
        }

        foreach (Border nextShell in nextLayer.Children.OfType<Border>())
        {
            if (nextShell.RenderTransform is not TranslateTransform transform)
                continue;

            DoubleAnimation x = new(transform.X, 0, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop
            };
            DoubleAnimation y = new(transform.Y, 0, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop
            };
            animationsRemaining++;
            x.Completed += CompleteOne;
            transform.BeginAnimation(TranslateTransform.XProperty, x);
            transform.BeginAnimation(TranslateTransform.YProperty, y);
        }

        foreach (Border oldShell in previousLayer.Children.OfType<Border>().Where(shell => !sharedPrevious.Contains(shell)))
        {
            if (oldShell.RenderTransform is not TranslateTransform transform)
                continue;

            DoubleAnimation x = new(0, direction > 0 ? -panelWidth : panelWidth, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop
            };
            animationsRemaining++;
            x.Completed += CompleteOne;
            transform.BeginAnimation(TranslateTransform.XProperty, x);
        }

        if (animationsRemaining == 0)
        {
            _unifiedPreviewPanel.Children.Remove(previousLayer);
        }
    }

    private static Point SlotCenter(Grid layer, Border shell, double width, double height)
    {
        int columns = Math.Max(1, layer.ColumnDefinitions.Count);
        int rows = Math.Max(1, layer.RowDefinitions.Count);
        int column = Math.Clamp(Grid.GetColumn(shell), 0, columns - 1);
        int row = Math.Clamp(Grid.GetRow(shell), 0, rows - 1);
        return new Point(
            (column + 0.5) * width / columns,
            (row + 0.5) * height / rows);
    }

    private CardRecord? GetActivePreviewCard()
    {
        if (_previewFromMainDeck && MainDeckGrid.SelectedItem is DeckEntry deckEntry)
            return deckEntry.Card;
        return AvailableCardsGrid.SelectedItem as CardRecord;
    }

    private void ClearUnifiedPreviewGrid()
    {
        if (_unifiedPreviewPanel is null)
            return;

        foreach (FrameworkElement element in _unifiedPreviewPanel.Children
                     .OfType<Grid>()
                     .SelectMany(layer => layer.Children.OfType<FrameworkElement>()))
        {
            if (element.RenderTransform is TranslateTransform transform)
            {
                transform.BeginAnimation(TranslateTransform.XProperty, null);
                transform.BeginAnimation(TranslateTransform.YProperty, null);
            }
        }
        _unifiedPreviewPanel.Children.Clear();
    }

    private List<(CardRecord Card, bool Active)> BuildPreviewWindow(int count)
    {
        List<(CardRecord Card, bool Active)> result = new();

        if (_previewFromMainDeck)
        {
            int itemCount = MainDeckGrid.Items.Count;
            int selected = MainDeckGrid.SelectedIndex;
            if (itemCount == 0 || selected < 0)
                return result;

            int start = Math.Max(0, Math.Min(selected - count / 2, Math.Max(0, itemCount - count)));
            int end = Math.Min(itemCount, start + count);
            for (int index = start; index < end; index++)
            {
                if (MainDeckGrid.Items[index] is DeckEntry entry)
                    result.Add((entry.Card, index == selected));
            }
            return result;
        }

        int catalogCount = AvailableCardsGrid.Items.Count;
        int catalogSelected = AvailableCardsGrid.SelectedIndex;
        if (catalogCount == 0 || catalogSelected < 0)
            return result;

        int catalogStart = Math.Max(0, Math.Min(catalogSelected - count / 2, Math.Max(0, catalogCount - count)));
        int catalogEnd = Math.Min(catalogCount, catalogStart + count);
        for (int index = catalogStart; index < catalogEnd; index++)
        {
            if (AvailableCardsGrid.Items[index] is CardRecord card)
                result.Add((card, index == catalogSelected));
        }
        return result;
    }

    private void SinglePreview_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2)
            return;
        CardRecord? card = GetActivePreviewCard();
        if (card is not null)
            AddPreviewCardToMainDeck(card);
        e.Handled = true;
    }

    private void MultiPreviewCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not FrameworkElement { Tag: CardRecord card })
            return;
        AddPreviewCardToMainDeck(card);
        e.Handled = true;
    }

    private void AddPreviewCardToMainDeck(CardRecord card)
    {
        try
        {
            _editor.Add(card, DeckSection.MainDeck);
            Changed($"Added {card.LocalizedName}.");
        }
        catch (Exception exception)
        {
            ShowError("Could not add the previewed card", exception);
        }
    }

    private void AttachComboBoxFocusRelease(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is ComboBox combo)
            {
                combo.DropDownClosed -= ComboBox_DropDownClosedReleaseFocus;
                combo.DropDownClosed += ComboBox_DropDownClosedReleaseFocus;
            }
            AttachComboBoxFocusRelease(child);
        }
    }

    private void ComboBox_DropDownClosedReleaseFocus(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            Keyboard.ClearFocus();
            if (_previewFromMainDeck && MainDeckGrid.SelectedItem is not null)
                MainDeckGrid.Focus();
            else
                AvailableCardsGrid.Focus();
        });
    }
}
