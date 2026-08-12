using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeckBuilder.GameData;

namespace DeckBuilder.Modern;

/// <summary>
/// Renders DotP card symbols directly from the original artwork embedded in the application.
/// There is deliberately no WAD/runtime compatibility fallback here: known symbols always have
/// one deterministic application-owned image source.
/// </summary>
internal static partial class CardSymbolRenderer
{
    public const double PreviewSymbolSize = 32;
    public const double PreviewSymbolAdvance = 18;
    public const double RulesSymbolSize = 16;

    public static int RenderCastingCost(Panel panel, IReadOnlyList<string> imageIds)
    {
        panel.Children.Clear();
        int rendered = 0;
        foreach (string imageId in imageIds)
        {
            BitmapSource? source = EmbeddedManaSymbols.TryGet(imageId);
            if (source is null)
            {
                continue;
            }

            Image image = new()
            {
                Source = source,
                Width = PreviewSymbolSize,
                Height = PreviewSymbolSize,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 0, -(PreviewSymbolSize - PreviewSymbolAdvance), 0),
                SnapsToDevicePixels = true,
                ToolTip = imageId
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            panel.Children.Add(image);
            rendered++;
        }

        return rendered;
    }

    public static void RenderRules(InlineCollection target, string? rulesText, string? flavorText)
    {
        target.Clear();
        if (!string.IsNullOrWhiteSpace(rulesText))
        {
            AppendText(target, rulesText, italic: false);
        }

        if (!string.IsNullOrWhiteSpace(flavorText))
        {
            if (target.Count > 0)
            {
                target.Add(new LineBreak());
                target.Add(new LineBreak());
            }

            AppendText(target, flavorText, italic: true);
        }
    }

    private static void AppendText(InlineCollection target, string text, bool italic)
    {
        MatchCollection matches = SymbolTokenRegex().Matches(text);
        int position = 0;
        foreach (Match match in matches)
        {
            AppendPlainText(target, text[position..match.Index], italic);

            string? imageId = DotpSymbolMap.TextTokenImageId(match.Value);
            BitmapSource? source = EmbeddedManaSymbols.TryGet(imageId);
            if (!string.IsNullOrWhiteSpace(imageId) && source is not null)
            {
                Image image = new()
                {
                    Source = source,
                    Width = RulesSymbolSize,
                    Height = RulesSymbolSize,
                    Stretch = Stretch.Uniform,
                    SnapsToDevicePixels = true,
                    ToolTip = imageId
                };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                target.Add(new InlineUIContainer(image)
                {
                    BaselineAlignment = BaselineAlignment.Center
                });
            }
            else
            {
                // Keep unknown or not-yet-bundled original symbols visible rather than inventing art.
                AppendPlainText(target, match.Value, italic);
            }

            position = match.Index + match.Length;
        }

        AppendPlainText(target, text[position..], italic);
    }

    private static void AppendPlainText(InlineCollection target, string text, bool italic)
    {
        if (text.Length == 0)
        {
            return;
        }

        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        string[] lines = normalized.Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            if (lines[index].Length > 0)
            {
                Run run = new(lines[index]);
                if (italic)
                {
                    run.FontStyle = FontStyles.Italic;
                }

                target.Add(run);
            }

            if (index < lines.Length - 1)
            {
                target.Add(new LineBreak());
            }
        }
    }

    [GeneratedRegex(@"\{[^}]+\}", RegexOptions.CultureInvariant)]
    private static partial Regex SymbolTokenRegex();
}
