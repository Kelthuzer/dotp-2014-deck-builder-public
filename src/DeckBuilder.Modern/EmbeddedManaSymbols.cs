using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeckBuilder.Modern;

/// <summary>
/// Exact DotP 2014 mana artwork vendored from DATA_CORE.WAD.
/// The legacy Deck Builder loaded these same MANA_* textures from the game at runtime.
/// Modern embeds the converted PNG files directly so mana rendering never depends on the active WAD/workspace.
/// Transparent TDX padding is cropped before display so the visible glyph can fill its UI slot.
/// </summary>
internal static class EmbeddedManaSymbols
{
    private const string ResourcePrefix = "DeckBuilder.Modern.Assets.Mana.";

    private static readonly ConcurrentDictionary<string, BitmapSource?> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static BitmapSource? TryGet(string? imageId)
    {
        if (string.IsNullOrWhiteSpace(imageId))
        {
            return null;
        }

        string id = Path.GetFileNameWithoutExtension(imageId.Trim()).ToUpperInvariant();
        return Cache.GetOrAdd(id, Load);
    }

    private static BitmapSource? Load(string id)
    {
        try
        {
            Assembly assembly = typeof(EmbeddedManaSymbols).Assembly;
            using Stream? input = assembly.GetManifestResourceStream(ResourcePrefix + id + ".png");
            if (input is null)
            {
                return null;
            }

            PngBitmapDecoder decoder = new(
                input,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            BitmapSource source = decoder.Frames[0];
            source.Freeze();
            return CropTransparentPadding(source);
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource CropTransparentPadding(BitmapSource source)
    {
        BitmapSource bgra = source;
        if (source.Format != PixelFormats.Bgra32)
        {
            FormatConvertedBitmap converted = new(source, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            bgra = converted;
        }

        int width = bgra.PixelWidth;
        int height = bgra.PixelHeight;
        int stride = checked(width * 4);
        byte[] pixels = new byte[checked(stride * height)];
        bgra.CopyPixels(pixels, stride, 0);

        int minX = width;
        int minY = height;
        int maxX = -1;
        int maxY = -1;
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                byte alpha = pixels[row + (x * 4) + 3];
                if (alpha <= 4)
                {
                    continue;
                }

                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        if (maxX < minX || maxY < minY)
        {
            return source;
        }

        minX = Math.Max(0, minX - 1);
        minY = Math.Max(0, minY - 1);
        maxX = Math.Min(width - 1, maxX + 1);
        maxY = Math.Min(height - 1, maxY + 1);

        Int32Rect bounds = new(minX, minY, maxX - minX + 1, maxY - minY + 1);
        if (bounds.Width == width && bounds.Height == height)
        {
            return source;
        }

        CroppedBitmap cropped = new(bgra, bounds);
        cropped.Freeze();
        return cropped;
    }
}
