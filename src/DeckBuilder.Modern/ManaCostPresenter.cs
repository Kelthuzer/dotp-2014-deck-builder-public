using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeckBuilder.Modern;

/// <summary>
/// Compact casting-cost renderer. DotP 2014 symbols are bundled with Modern and have one
/// deterministic source. Unknown tokens stay textual instead of being replaced by invented art.
/// </summary>
public sealed partial class ManaCostPresenter : StackPanel
{
    private const double SymbolSize = 24;

    public static readonly DependencyProperty CostProperty = DependencyProperty.Register(
        nameof(Cost),
        typeof(string),
        typeof(ManaCostPresenter),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure, OnCostChanged));

    public ManaCostPresenter()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Stretch;
        MinHeight = SymbolSize;
        ToolTip = string.Empty;
        Loaded += (_, _) => Rebuild();
    }

    public string Cost
    {
        get => (string)(GetValue(CostProperty) ?? string.Empty);
        set => SetValue(CostProperty, value ?? string.Empty);
    }

    private static void OnCostChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e) =>
        ((ManaCostPresenter)dependencyObject).Rebuild();

    private void Rebuild()
    {
        Children.Clear();
        ToolTip = Cost;
        if (string.IsNullOrWhiteSpace(Cost))
        {
            return;
        }

        MatchCollection matches = ManaTokenRegex().Matches(Cost);
        if (matches.Count == 0)
        {
            Children.Add(CreateText(Cost));
            return;
        }

        foreach (Match match in matches)
        {
            string? imageId = DeckBuilder.GameData.CardVisualMetadata.ManaImageId(match.Value);
            BitmapSource? embedded = EmbeddedManaSymbols.TryGet(imageId);
            Children.Add(embedded is not null && !string.IsNullOrWhiteSpace(imageId)
                ? CreateOriginalSymbol(embedded, imageId)
                : CreateText(match.Value));
        }
    }

    private static Image CreateOriginalSymbol(BitmapSource source, string imageId)
    {
        Image image = new()
        {
            Source = source,
            Width = SymbolSize,
            Height = SymbolSize,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 0, 1, 0),
            VerticalAlignment = VerticalAlignment.Stretch,
            SnapsToDevicePixels = true,
            ToolTip = imageId
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        return image;
    }

    private static TextBlock CreateText(string text) => new()
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(1, 0, 2, 0)
    };

    [GeneratedRegex(@"\{([^}]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex ManaTokenRegex();
}
