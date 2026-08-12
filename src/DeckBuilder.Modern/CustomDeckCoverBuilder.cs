using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeckBuilder.GameData;

namespace DeckBuilder.Modern;

internal static class CustomDeckCoverBuilder
{
    private static readonly Rect ArtworkRect = new(150, 93, 216, 257);

    internal static readonly string[] MaterialPresets =
    {
        "Classic",
        "Steel",
        "Wood",
        "Bone",
        "Obsidian",
        "Gold"
    };

    internal static IReadOnlyList<string> SkinPresets => MaterialPresets;

    public static void Build(
        string sourceImagePath,
        string outputTdxPath,
        double offsetX = 0,
        double offsetY = 0,
        double zoom = 1,
        string materialPreset = "Classic",
        string tintHex = "#FFFFFF")
    {
        BitmapSource preview = RenderPreview(sourceImagePath, offsetX, offsetY, zoom, materialPreset, tintHex);
        BitmapSource bgra = EnsureBgra32(preview);
        int width = bgra.PixelWidth;
        int height = bgra.PixelHeight;
        int stride = checked(width * 4);
        byte[] pixels = new byte[checked(stride * height)];
        bgra.CopyPixels(pixels, stride, 0);
        TdxImageEncoder.SaveBgra32(outputTdxPath, width, height, pixels);
    }

    public static BitmapSource RenderPreview(
        string sourceImagePath,
        double offsetX = 0,
        double offsetY = 0,
        double zoom = 1,
        string materialPreset = "Classic",
        string tintHex = "#FFFFFF")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceImagePath);
        if (!File.Exists(sourceImagePath))
            throw new FileNotFoundException("Custom deck-cover image was not found.", sourceImagePath);

        ResolveEncodedStyle(ref materialPreset, ref tintHex);
        materialPreset = NormalizeMaterial(materialPreset);
        Color tint = ParseTint(tintHex);
        zoom = Math.Clamp(zoom, 0.25, 6.0);

        string templateDirectory = Path.Combine(AppContext.BaseDirectory, "Images", "DeckBoxTemplates", "A");
        BitmapSource overlay = EnsureBgra32(LoadBitmap(Path.Combine(templateDirectory, "D14_DeckBoxOverlay.png")));
        BitmapSource mask = EnsureBgra32(LoadBitmap(Path.Combine(templateDirectory, "D14_DeckBoxMask.png")));
        BitmapSource alpha = EnsureBgra32(LoadBitmap(Path.Combine(templateDirectory, "D14_DeckBoxAlpha.png")));
        BitmapSource source = LoadBitmap(sourceImagePath);

        int width = overlay.PixelWidth;
        int height = overlay.PixelHeight;
        if (mask.PixelWidth != width || mask.PixelHeight != height
            || alpha.PixelWidth != width || alpha.PixelHeight != height)
        {
            throw new InvalidDataException("Deck-box template images have mismatched dimensions.");
        }

        Rect sourceRect = CalculateArtworkRect(source, offsetX, offsetY, zoom);

        RenderTargetBitmap sourceAndMask = new(width, height, 96, 96, PixelFormats.Pbgra32);
        DrawingVisual visual = new();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawImage(source, sourceRect);
            drawing.DrawImage(mask, new Rect(0, 0, width, height));
        }
        sourceAndMask.Render(visual);

        byte[] basePixels = CopyBgra(sourceAndMask);
        byte[] maskPixels = CopyBgra(mask);
        byte[] overlayPixels = CopyBgra(overlay);
        byte[] alphaPixels = CopyBgra(alpha);

        byte keyB = maskPixels[0];
        byte keyG = maskPixels[1];
        byte keyR = maskPixels[2];
        ApplyChromaKey(basePixels, keyB, keyG, keyR);

        ApplyMaterialTexture(overlayPixels, width, height, materialPreset);
        ApplyTint(overlayPixels, tint);
        AlphaComposite(basePixels, overlayPixels);
        ApplyMaterialChassis(basePixels, alphaPixels, width, height, materialPreset, tint);

        for (int index = 3; index < basePixels.Length; index += 4)
            basePixels[index] = alphaPixels[index];

        WriteableBitmap result = new(width, height, 96, 96, PixelFormats.Bgra32, null);
        result.WritePixels(new Int32Rect(0, 0, width, height), basePixels, width * 4, 0);
        result.Freeze();
        return result;
    }

    internal static string EncodeStyle(string materialPreset, string tintHex) =>
        $"{NormalizeMaterial(materialPreset)}|{NormalizeTintHex(tintHex)}";

    internal static void DecodeStyle(string? encoded, out string materialPreset, out string tintHex)
    {
        materialPreset = encoded ?? "Classic";
        tintHex = "#FFFFFF";
        ResolveEncodedStyle(ref materialPreset, ref tintHex);
        materialPreset = NormalizeMaterial(materialPreset);
        tintHex = NormalizeTintHex(tintHex);
    }

    internal static string NormalizeTintHex(string? value)
    {
        Color color = ParseTint(value);
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private static void ResolveEncodedStyle(ref string materialPreset, ref string tintHex)
    {
        int separator = materialPreset.IndexOf('|');
        if (separator <= 0)
            return;

        string encodedTint = materialPreset[(separator + 1)..].Trim();
        materialPreset = materialPreset[..separator].Trim();
        if (!string.IsNullOrWhiteSpace(encodedTint)
            && (string.IsNullOrWhiteSpace(tintHex) || tintHex.Equals("#FFFFFF", StringComparison.OrdinalIgnoreCase)))
        {
            tintHex = encodedTint;
        }
    }

    private static string NormalizeMaterial(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            string? match = MaterialPresets.FirstOrDefault(item =>
                item.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }

        return "Classic";
    }

    private static Color ParseTint(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            string text = value.Trim().TrimStart('#');
            if (text.Length == 6
                && byte.TryParse(text[..2], System.Globalization.NumberStyles.HexNumber, null, out byte r)
                && byte.TryParse(text.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out byte g)
                && byte.TryParse(text.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out byte b))
            {
                return Color.FromRgb(r, g, b);
            }
        }

        return Colors.White;
    }

    private static Rect CalculateArtworkRect(BitmapSource source, double offsetX, double offsetY, double zoom)
    {
        double widthScale = ArtworkRect.Width / source.PixelWidth;
        double heightScale = ArtworkRect.Height / source.PixelHeight;
        double baseScale = Math.Max(widthScale, heightScale);
        double scale = baseScale * zoom;
        double width = source.PixelWidth * scale;
        double height = source.PixelHeight * scale;
        return new Rect(
            ArtworkRect.X + (ArtworkRect.Width - width) / 2.0 + offsetX,
            ArtworkRect.Y + (ArtworkRect.Height - height) / 2.0 + offsetY,
            width,
            height);
    }

    private static void ApplyChromaKey(byte[] pixels, byte b, byte g, byte r)
    {
        for (int index = 0; index < pixels.Length; index += 4)
        {
            if (pixels[index] == b && pixels[index + 1] == g && pixels[index + 2] == r)
                pixels[index + 3] = 0;
        }
    }

    private static void AlphaComposite(byte[] destination, byte[] source)
    {
        for (int index = 0; index < destination.Length; index += 4)
        {
            int sa = source[index + 3];
            if (sa == 0)
                continue;

            int inv = 255 - sa;
            destination[index] = (byte)((source[index] * sa + destination[index] * inv) / 255);
            destination[index + 1] = (byte)((source[index + 1] * sa + destination[index + 1] * inv) / 255);
            destination[index + 2] = (byte)((source[index + 2] * sa + destination[index + 2] * inv) / 255);
            destination[index + 3] = (byte)Math.Min(255, sa + destination[index + 3] * inv / 255);
        }
    }

    private static void ApplyMaterialTexture(byte[] pixels, int width, int height, string material)
    {
        if (material.Equals("Classic", StringComparison.OrdinalIgnoreCase))
            return;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = (y * width + x) * 4;
                if (pixels[index + 3] == 0)
                    continue;

                double luminance = (pixels[index + 2] * 0.30 + pixels[index + 1] * 0.59 + pixels[index] * 0.11) / 255.0;
                MaterialSample sample = SampleMaterial(material, x, y, luminance);
                pixels[index] = ClampByte(sample.B);
                pixels[index + 1] = ClampByte(sample.G);
                pixels[index + 2] = ClampByte(sample.R);
            }
        }
    }

    private static MaterialSample SampleMaterial(string material, int x, int y, double luminance)
    {
        double noise = HashNoise(x, y) - 0.5;
        return material switch
        {
            "Steel" => SteelSample(x, y, luminance, noise),
            "Wood" => WoodSample(x, y, luminance, noise),
            "Bone" => BoneSample(x, y, luminance, noise),
            "Obsidian" => ObsidianSample(x, y, luminance, noise),
            "Gold" => GoldSample(x, y, luminance, noise),
            _ => new MaterialSample(luminance * 255, luminance * 255, luminance * 255)
        };
    }

    private static MaterialSample SteelSample(int x, int y, double l, double noise)
    {
        double brushing = Math.Sin(y * 0.72) * 8 + Math.Sin(y * 2.7 + x * 0.025) * 4 + noise * 12;
        double baseValue = 52 + l * 165 + brushing;
        return new MaterialSample(baseValue + 10, baseValue + 6, baseValue);
    }

    private static MaterialSample WoodSample(int x, int y, double l, double noise)
    {
        double grain = Math.Sin(x * 0.105 + Math.Sin(y * 0.045) * 2.8) * 18
            + Math.Sin(x * 0.026 + y * 0.018) * 11
            + noise * 8;
        double knot = Math.Sin(Math.Sqrt((x % 170 - 85) * (x % 170 - 85) + (y % 210 - 105) * (y % 210 - 105)) * 0.11) * 6;
        double v = l * 0.72 + 0.28;
        return new MaterialSample(58 * v + grain * 0.35, 91 * v + grain * 0.55, 133 * v + grain + knot);
    }

    private static MaterialSample BoneSample(int x, int y, double l, double noise)
    {
        double pores = HashNoise(x / 3, y / 3) > 0.89 ? -34 : 0;
        double waves = Math.Sin(x * 0.035 + y * 0.018) * 5;
        double v = 0.58 + l * 0.45;
        return new MaterialSample(176 * v + noise * 8 + pores, 199 * v + waves + pores, 218 * v + waves + pores);
    }

    private static MaterialSample ObsidianSample(int x, int y, double l, double noise)
    {
        double sheen = Math.Pow(Math.Max(0, Math.Sin((x + y * 0.7) * 0.018)), 14) * 55;
        double crackSeed = Math.Abs(Math.Sin(x * 0.071 + y * 0.113) + Math.Sin(x * 0.019 - y * 0.083));
        double crack = crackSeed < 0.055 ? 62 : 0;
        double v = 15 + l * 54 + noise * 7 + sheen + crack;
        return new MaterialSample(v + 10, v + 5, v + 2);
    }

    private static MaterialSample GoldSample(int x, int y, double l, double noise)
    {
        double brushed = Math.Sin(y * 0.42) * 7 + noise * 8;
        double highlight = Math.Pow(Math.Max(0, Math.Sin(x * 0.017 + y * 0.004)), 10) * 28;
        double v = 0.42 + l * 0.68;
        return new MaterialSample(38 * v + brushed, 137 * v + brushed + highlight * 0.45, 219 * v + brushed + highlight);
    }

    private static double HashNoise(int x, int y)
    {
        unchecked
        {
            uint n = (uint)(x * 374761393 + y * 668265263);
            n = (n ^ (n >> 13)) * 1274126177;
            n ^= n >> 16;
            return (n & 0x00FFFFFF) / 16777215.0;
        }
    }

    private static void ApplyTint(byte[] pixels, Color tint)
    {
        if (tint.R == 255 && tint.G == 255 && tint.B == 255)
            return;

        double r = 0.32 + 0.68 * tint.R / 255.0;
        double g = 0.32 + 0.68 * tint.G / 255.0;
        double b = 0.32 + 0.68 * tint.B / 255.0;
        for (int index = 0; index < pixels.Length; index += 4)
        {
            if (pixels[index + 3] == 0)
                continue;
            pixels[index] = ClampByte(pixels[index] * b);
            pixels[index + 1] = ClampByte(pixels[index + 1] * g);
            pixels[index + 2] = ClampByte(pixels[index + 2] * r);
        }
    }

    private static void ApplyMaterialChassis(
        byte[] pixels,
        byte[] alphaMask,
        int width,
        int height,
        string material,
        Color tint)
    {
        if (material.Equals("Classic", StringComparison.OrdinalIgnoreCase))
            return;

        ChassisStyle style = material switch
        {
            "Steel" => new(88, 96, 108, 218, 228, 238, 3, 1),
            "Wood" => new(88, 51, 27, 188, 128, 70, 5, 2),
            "Bone" => new(184, 174, 145, 236, 221, 178, 4, 3),
            "Obsidian" => new(22, 23, 29, 112, 126, 158, 3, 4),
            "Gold" => new(116, 77, 18, 255, 205, 74, 4, 1),
            _ => new(64, 64, 64, 190, 190, 190, 3, 1)
        };

        style = TintStyle(style, tint);
        int left = (int)ArtworkRect.X;
        int top = (int)ArtworkRect.Y;
        int right = (int)(ArtworkRect.X + ArtworkRect.Width);
        int bottom = (int)(ArtworkRect.Y + ArtworkRect.Height);

        int railWidth = material == "Wood" ? 15 : material == "Bone" ? 13 : 11;
        FillTexturedRect(pixels, alphaMask, width, height, left - railWidth - 7, top + 5, railWidth, bottom - top + 14,
            style, material, 205);
        FillTexturedRect(pixels, alphaMask, width, height, right + 7, top + 3, railWidth, bottom - top + 17,
            style, material, 205);
        FillTexturedRect(pixels, alphaMask, width, height, left - 3, top - 13, right - left + 6, 11,
            style, material, 205);

        DrawBottomChassis(pixels, alphaMask, width, height, material, style, left, right, bottom);
        DrawMaterialDetails(pixels, alphaMask, width, height, material, style, left, top, right, bottom);
    }

    private static void DrawBottomChassis(
        byte[] pixels,
        byte[] alphaMask,
        int width,
        int height,
        string material,
        ChassisStyle style,
        int left,
        int right,
        int bottom)
    {
        int yTop = bottom - 15;
        int yBottom = bottom + 46;
        int center = (left + right) / 2;
        for (int y = yTop; y <= yBottom; y++)
        {
            double t = (y - yTop) / (double)Math.Max(1, yBottom - yTop);
            int halfWidth = material switch
            {
                "Wood" => (int)(72 + 12 * t),
                "Bone" => (int)(62 + 30 * Math.Sin(t * Math.PI)),
                "Obsidian" => (int)(69 + 18 * (1 - Math.Abs(t - 0.5) * 2)),
                "Gold" => (int)(65 + 26 * t),
                _ => (int)(66 + 20 * t)
            };
            for (int x = center - halfWidth; x <= center + halfWidth; x++)
            {
                if (!Inside(width, height, x, y))
                    continue;
                double edge = 1.0 - Math.Abs(x - center) / (double)Math.Max(1, halfWidth);
                int alpha = (int)(215 * Math.Clamp(edge * 2.2, 0.35, 1.0));
                BlendMaterialPixel(pixels, alphaMask, width, x, y, style, material, alpha);
            }
        }
    }

    private static void DrawMaterialDetails(
        byte[] pixels,
        byte[] alphaMask,
        int width,
        int height,
        string material,
        ChassisStyle style,
        int left,
        int top,
        int right,
        int bottom)
    {
        if (material == "Wood")
        {
            for (int y = top + 24; y < bottom; y += 46)
            {
                DrawFastener(pixels, alphaMask, width, height, left - 15, y, style);
                DrawFastener(pixels, alphaMask, width, height, right + 15, y + 13, style);
            }
        }
        else if (material == "Bone")
        {
            for (int y = top + 10; y < bottom + 28; y += 24)
            {
                int wobble = (int)(Math.Sin(y * 0.12) * 5);
                DrawFastener(pixels, alphaMask, width, height, left - 15 + wobble, y, style);
                if (y % 48 == 0)
                    DrawFastener(pixels, alphaMask, width, height, right + 14 - wobble, y + 8, style);
            }
        }
        else if (material == "Obsidian")
        {
            for (int i = 0; i < 8; i++)
            {
                int x0 = left - 17 + (i % 2) * (right - left + 33);
                int y0 = top + 20 + i * 38;
                DrawCrack(pixels, alphaMask, width, height, x0, y0, i % 2 == 0 ? 1 : -1, style);
            }
        }
        else
        {
            int spacing = material == "Gold" ? 52 : 64;
            for (int y = top + 22; y < bottom; y += spacing)
            {
                DrawFastener(pixels, alphaMask, width, height, left - 13, y, style);
                DrawFastener(pixels, alphaMask, width, height, right + 13, y, style);
            }
        }
    }

    private static ChassisStyle TintStyle(ChassisStyle style, Color tint)
    {
        double r = 0.42 + 0.58 * tint.R / 255.0;
        double g = 0.42 + 0.58 * tint.G / 255.0;
        double b = 0.42 + 0.58 * tint.B / 255.0;
        return style with
        {
            DarkR = ClampByte(style.DarkR * r),
            DarkG = ClampByte(style.DarkG * g),
            DarkB = ClampByte(style.DarkB * b),
            LightR = ClampByte(style.LightR * r),
            LightG = ClampByte(style.LightG * g),
            LightB = ClampByte(style.LightB * b)
        };
    }

    private static void FillTexturedRect(
        byte[] pixels,
        byte[] alphaMask,
        int width,
        int height,
        int x,
        int y,
        int rectWidth,
        int rectHeight,
        ChassisStyle style,
        string material,
        int alpha)
    {
        for (int py = y; py < y + rectHeight; py++)
        {
            for (int px = x; px < x + rectWidth; px++)
            {
                if (Inside(width, height, px, py))
                    BlendMaterialPixel(pixels, alphaMask, width, px, py, style, material, alpha);
            }
        }
    }

    private static void DrawFastener(byte[] pixels, byte[] alphaMask, int width, int height, int cx, int cy, ChassisStyle style)
    {
        int radius = style.FastenerRadius;
        for (int y = cy - radius; y <= cy + radius; y++)
        {
            for (int x = cx - radius; x <= cx + radius; x++)
            {
                if (!Inside(width, height, x, y))
                    continue;
                int dx = x - cx;
                int dy = y - cy;
                if (dx * dx + dy * dy > radius * radius)
                    continue;
                int index = (y * width + x) * 4;
                if (alphaMask[index + 3] == 0)
                    continue;
                byte shade = (byte)(dx + dy < 0 ? 232 : 48);
                BlendPixel(pixels, index, shade, shade, shade, 220);
            }
        }
    }

    private static void DrawCrack(
        byte[] pixels,
        byte[] alphaMask,
        int width,
        int height,
        int x,
        int y,
        int direction,
        ChassisStyle style)
    {
        for (int i = 0; i < 26; i++)
        {
            int px = x + direction * (i / 4 + (i % 3));
            int py = y + i;
            if (!Inside(width, height, px, py))
                continue;
            int index = (py * width + px) * 4;
            if (alphaMask[index + 3] == 0)
                continue;
            BlendPixel(pixels, index, style.LightB, style.LightG, style.LightR, 150);
        }
    }

    private static void BlendMaterialPixel(
        byte[] pixels,
        byte[] alphaMask,
        int width,
        int x,
        int y,
        ChassisStyle style,
        string material,
        int alpha)
    {
        int index = (y * width + x) * 4;
        if (alphaMask[index + 3] == 0)
            return;

        double t = Math.Clamp((HashNoise(x, y) * 0.33) + 0.34 + Math.Sin((x + y) * 0.035) * 0.10, 0, 1);
        if (material == "Wood")
            t = Math.Clamp(t + Math.Sin(y * 0.12 + x * 0.025) * 0.18, 0, 1);
        if (material == "Bone")
            t = Math.Clamp(t + Math.Sin(x * 0.055) * 0.08, 0, 1);

        byte r = ClampByte(style.DarkR + (style.LightR - style.DarkR) * t);
        byte g = ClampByte(style.DarkG + (style.LightG - style.DarkG) * t);
        byte b = ClampByte(style.DarkB + (style.LightB - style.DarkB) * t);
        BlendPixel(pixels, index, b, g, r, alpha);
    }

    private static bool Inside(int width, int height, int x, int y) => x >= 0 && x < width && y >= 0 && y < height;

    private static void BlendPixel(byte[] pixels, int index, byte b, byte g, byte r, int alpha)
    {
        int a = Math.Clamp(alpha, 0, 255);
        int inv = 255 - a;
        pixels[index] = (byte)((b * a + pixels[index] * inv) / 255);
        pixels[index + 1] = (byte)((g * a + pixels[index + 1] * inv) / 255);
        pixels[index + 2] = (byte)((r * a + pixels[index + 2] * inv) / 255);
    }

    private static byte ClampByte(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);

    private static BitmapSource LoadBitmap(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Deck-box template image was not found.", path);

        using FileStream stream = File.OpenRead(path);
        BitmapImage bitmap = new();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource EnsureBgra32(BitmapSource source)
    {
        if (source.Format == PixelFormats.Bgra32)
            return source;

        FormatConvertedBitmap converted = new(source, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    private static byte[] CopyBgra(BitmapSource bitmap)
    {
        BitmapSource converted = EnsureBgra32(bitmap);
        int stride = converted.PixelWidth * 4;
        byte[] pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private readonly record struct MaterialSample(double B, double G, double R);

    private readonly record struct ChassisStyle(
        byte DarkR,
        byte DarkG,
        byte DarkB,
        byte LightR,
        byte LightG,
        byte LightB,
        int FastenerRadius,
        int DetailVariant);
}
