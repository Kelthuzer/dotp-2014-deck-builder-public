using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using DeckBuilder.Core.Models;
using DeckBuilder.GameData;

namespace DeckBuilder.Modern;

public partial class MainWindow
{
    private async Task<FrameworkElement> BuildExactCardPreviewAsync(CardRecord card, double width)
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
            Fill = FrameFallbackBrush(visual.FrameId)
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
                Source = ToBitmapSource(frame, rotateLandscape: true)
            };
            Panel.SetZIndex(frameImage, 1);
            canvas.Children.Add(frameImage);
        }

        if (artLookup.Image is not null)
        {
            Image art = new()
            {
                Source = ToBitmapSource(artLookup.Image),
                Stretch = Stretch.Fill,
                Width = visual.FullBleedArt ? 356 : 324,
                Height = visual.FullBleedArt ? 512 : 238
            };
            Canvas.SetLeft(art, visual.FullBleedArt ? 0 : 16);
            Canvas.SetTop(art, visual.FullBleedArt ? 0 : 47);
            Panel.SetZIndex(art, visual.FullBleedArt ? 0 : 2);
            canvas.Children.Add(art);
        }

        StackPanel mana = new()
        {
            Orientation = Orientation.Horizontal,
            Height = 25
        };
        int renderedMana = CardSymbolRenderer.RenderCastingCost(mana, visual.ManaImageIds);
        double manaWidth = renderedMana * CardSymbolRenderer.PreviewSymbolAdvance;
        Canvas.SetLeft(mana, Math.Max(12, 336 - manaWidth));
        Canvas.SetTop(mana, 8);
        Panel.SetZIndex(mana, 4);
        canvas.Children.Add(mana);

        TextBlock title = CardText(
            string.IsNullOrWhiteSpace(card.LocalizedName) ? card.FileName : card.LocalizedName,
            18,
            FontWeights.Bold,
            Math.Max(130, 330 - manaWidth),
            25);
        Canvas.SetLeft(title, 12);
        Canvas.SetTop(title, 13);
        Panel.SetZIndex(title, 3);
        canvas.Children.Add(title);

        bool tokenLayout = card.IsToken;
        TextBlock type = CardText(card.TypeLine, 14, FontWeights.Normal, 286, 23);
        Canvas.SetLeft(type, 14);
        Canvas.SetTop(type, tokenLayout ? 348 : 294);
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

        TextBlock rulesText = new()
        {
            Width = 324,
            Height = tokenLayout ? 87 : 150,
            FontFamily = new FontFamily("Georgia"),
            FontSize = 13,
            LineHeight = 16,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Brushes.Black
        };
        CardSymbolRenderer.RenderRules(rulesText.Inlines, card.RulesText, card.FlavorText);
        Canvas.SetLeft(rulesText, 15);
        Canvas.SetTop(rulesText, tokenLayout ? 383 : 324);
        Panel.SetZIndex(rulesText, 3);
        canvas.Children.Add(rulesText);

        if (visual.ShowsPower)
        {
            CardImageData? power = await powerTask;
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
            else
            {
                Border powerFallback = new()
                {
                    Width = 66,
                    Height = 28,
                    Background = new SolidColorBrush(Color.FromRgb(231, 223, 201)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(59, 49, 40)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(9)
                };
                Canvas.SetLeft(powerFallback, 277);
                Canvas.SetTop(powerFallback, 472);
                Panel.SetZIndex(powerFallback, 3);
                canvas.Children.Add(powerFallback);
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
        artist.Foreground = visual.CreditId == "CREDIT_WHITE" ? Brushes.White : Brushes.Black;
        Canvas.SetLeft(artist, 42);
        Canvas.SetTop(artist, 482);
        Panel.SetZIndex(artist, 4);
        canvas.Children.Add(artist);

        return new Viewbox
        {
            Width = width,
            Height = width * 512.0 / 356.0,
            Stretch = Stretch.Uniform,
            Child = canvas,
            Margin = new Thickness(0),
            ToolTip = string.IsNullOrWhiteSpace(card.LocalizedName) ? card.FileName : card.LocalizedName
        };
    }
}
