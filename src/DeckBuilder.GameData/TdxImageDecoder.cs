using System.Runtime.CompilerServices;
using Gibbed.Duels.FileFormats;
using Tdx = Gibbed.Duels.FileFormats.Tdx;

[assembly: InternalsVisibleTo("DeckBuilder.Core.Checks")]

namespace DeckBuilder.GameData;

public sealed record CardImageData(int Width, int Height, byte[] BgraPixels);

internal static class TdxImageDecoder
{
    internal enum DxtCompression
    {
        Dxt1,
        Dxt3,
        Dxt5
    }

    /// <summary>
    /// Parse the TDX container through the same Gibbed.Duels.FileFormats TdxFile implementation
    /// used by the original DotP 2014 Deck Builder. This matters for small UI/mana textures and
    /// community assets whose mipmap/header layout is not always identical to card illustrations.
    /// Pixel conversion stays managed so the modern application does not depend on native Squish.
    /// </summary>
    public static CardImageData Decode(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        TdxFile tdx = new();
        try
        {
            using MemoryStream input = new(data, writable: false);
            tdx.Deserialize(input);
        }
        catch (Exception exception)
        {
            throw new InvalidDataException("Could not deserialize the TDX container with the legacy Gibbed parser.", exception);
        }

        if (tdx.Mipmaps is null || tdx.Mipmaps.Count == 0)
        {
            throw new InvalidDataException("The TDX image has no mipmaps.");
        }

        Tdx.Mipmap mip = tdx.Mipmaps[0];
        int width = checked((int)mip.Width);
        int height = checked((int)mip.Height);
        if (width <= 0 || height <= 0 || width > 4096 || height > 4096)
        {
            throw new InvalidDataException($"Invalid TDX dimensions: {width}x{height}.");
        }

        byte[] source = mip.Data ?? throw new InvalidDataException("The first TDX mipmap has no pixel data.");
        byte[] pixels = tdx.Format switch
        {
            Tdx.D3DFormat.DXT1 => DecodeDxt(source, width, height, DxtCompression.Dxt1),
            Tdx.D3DFormat.DXT3 => DecodeDxt(source, width, height, DxtCompression.Dxt3),
            Tdx.D3DFormat.DXT5 => DecodeDxt(source, width, height, DxtCompression.Dxt5),
            Tdx.D3DFormat.A8R8G8B8 => CopyBgra(source, width, height),
            Tdx.D3DFormat.X8R8G8B8 => CopyBgrx(source, width, height),
            Tdx.D3DFormat.A4R4G4B4 => DecodeA4R4G4B4(source, width, height),
            _ => throw new NotSupportedException($"Unsupported TDX format {tdx.Format}.")
        };

        return new CardImageData(width, height, pixels);
    }

    internal static byte[] DecodeDxt(byte[] source, int width, int height, DxtCompression format)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException($"Invalid DXT dimensions: {width}x{height}.");
        }

        int blockBytes = format == DxtCompression.Dxt1 ? 8 : 16;
        int blocksWide = checked((width + 3) / 4);
        int blocksHigh = checked((height + 3) / 4);
        int requiredLength = checked(blocksWide * blocksHigh * blockBytes);
        if (source.Length < requiredLength)
        {
            throw new InvalidDataException(
                $"The {format} pixel buffer is truncated: expected {requiredLength} bytes, got {source.Length}.");
        }

        byte[] result = new byte[checked(width * height * 4)];
        int sourceOffset = 0;
        Span<byte> alpha = stackalloc byte[16];
        for (int blockY = 0; blockY < blocksHigh; blockY++)
        {
            for (int blockX = 0; blockX < blocksWide; blockX++)
            {
                int colorOffset;
                if (format == DxtCompression.Dxt3)
                {
                    DecodeDxt3Alpha(source, sourceOffset, alpha);
                    colorOffset = sourceOffset + 8;
                }
                else if (format == DxtCompression.Dxt5)
                {
                    DecodeDxt5Alpha(source, sourceOffset, alpha);
                    colorOffset = sourceOffset + 8;
                }
                else
                {
                    alpha.Fill(byte.MaxValue);
                    colorOffset = sourceOffset;
                }

                DecodeColorBlock(
                    source,
                    colorOffset,
                    result,
                    width,
                    height,
                    blockX,
                    blockY,
                    alpha,
                    allowTransparency: format == DxtCompression.Dxt1);
                sourceOffset += blockBytes;
            }
        }

        return result;
    }

    private static void DecodeDxt3Alpha(byte[] source, int offset, Span<byte> alpha)
    {
        for (int pixel = 0; pixel < 16; pixel++)
        {
            byte packed = source[offset + (pixel / 2)];
            int value = (pixel & 1) == 0 ? packed & 0x0F : packed >> 4;
            alpha[pixel] = (byte)(value * 17);
        }
    }

    private static void DecodeDxt5Alpha(byte[] source, int offset, Span<byte> alpha)
    {
        Span<byte> palette = stackalloc byte[8];
        palette[0] = source[offset];
        palette[1] = source[offset + 1];
        if (palette[0] > palette[1])
        {
            for (int index = 2; index < 8; index++)
            {
                palette[index] = (byte)(((8 - index) * palette[0] + (index - 1) * palette[1]) / 7);
            }
        }
        else
        {
            for (int index = 2; index < 6; index++)
            {
                palette[index] = (byte)(((6 - index) * palette[0] + (index - 1) * palette[1]) / 5);
            }

            palette[6] = 0;
            palette[7] = byte.MaxValue;
        }

        ulong indices = 0;
        for (int index = 0; index < 6; index++)
        {
            indices |= (ulong)source[offset + 2 + index] << (8 * index);
        }

        for (int pixel = 0; pixel < 16; pixel++)
        {
            alpha[pixel] = palette[(int)((indices >> (3 * pixel)) & 0x07)];
        }
    }

    private static void DecodeColorBlock(
        byte[] source,
        int offset,
        byte[] target,
        int width,
        int height,
        int blockX,
        int blockY,
        ReadOnlySpan<byte> alpha,
        bool allowTransparency)
    {
        ushort color0 = ReadUInt16(source, offset);
        ushort color1 = ReadUInt16(source, offset + 2);
        Span<byte> colors = stackalloc byte[16];
        ExpandRgb565(color0, colors, 0);
        ExpandRgb565(color1, colors, 4);
        colors[3] = byte.MaxValue;
        colors[7] = byte.MaxValue;

        if (!allowTransparency || color0 > color1)
        {
            InterpolateColor(colors, 0, colors, 4, colors, 8, 2, 1, 3, byte.MaxValue);
            InterpolateColor(colors, 0, colors, 4, colors, 12, 1, 2, 3, byte.MaxValue);
        }
        else
        {
            InterpolateColor(colors, 0, colors, 4, colors, 8, 1, 1, 2, byte.MaxValue);
            colors.Slice(12, 4).Clear();
        }

        uint indices = ReadUInt32(source, offset + 4);
        for (int pixel = 0; pixel < 16; pixel++)
        {
            int localX = pixel & 3;
            int localY = pixel >> 2;
            int x = blockX * 4 + localX;
            int y = blockY * 4 + localY;
            if (x >= width || y >= height)
            {
                continue;
            }

            int colorIndex = (int)((indices >> (pixel * 2)) & 0x03);
            int colorBase = colorIndex * 4;
            int targetOffset = (y * width + x) * 4;
            target[targetOffset] = colors[colorBase];
            target[targetOffset + 1] = colors[colorBase + 1];
            target[targetOffset + 2] = colors[colorBase + 2];
            target[targetOffset + 3] = colors[colorBase + 3] == 0 ? (byte)0 : alpha[pixel];
        }
    }

    private static void ExpandRgb565(ushort packed, Span<byte> colors, int offset)
    {
        colors[offset] = Expand5(packed & 0x1F);
        colors[offset + 1] = Expand6((packed >> 5) & 0x3F);
        colors[offset + 2] = Expand5((packed >> 11) & 0x1F);
    }

    private static byte Expand5(int value) => (byte)((value << 3) | (value >> 2));

    private static byte Expand6(int value) => (byte)((value << 2) | (value >> 4));

    private static void InterpolateColor(
        ReadOnlySpan<byte> left,
        int leftOffset,
        ReadOnlySpan<byte> right,
        int rightOffset,
        Span<byte> target,
        int targetOffset,
        int leftWeight,
        int rightWeight,
        int divisor,
        byte alpha)
    {
        for (int channel = 0; channel < 3; channel++)
        {
            target[targetOffset + channel] = (byte)(
                (leftWeight * left[leftOffset + channel] + rightWeight * right[rightOffset + channel]) / divisor);
        }

        target[targetOffset + 3] = alpha;
    }

    private static ushort ReadUInt16(byte[] source, int offset) =>
        (ushort)(source[offset] | (source[offset + 1] << 8));

    private static uint ReadUInt32(byte[] source, int offset) =>
        (uint)(source[offset]
            | (source[offset + 1] << 8)
            | (source[offset + 2] << 16)
            | (source[offset + 3] << 24));

    private static byte[] CopyBgra(byte[] source, int width, int height)
    {
        int length = checked(width * height * 4);
        if (source.Length < length)
        {
            throw new InvalidDataException("The TDX pixel buffer is truncated.");
        }

        return source.AsSpan(0, length).ToArray();
    }

    private static byte[] CopyBgrx(byte[] source, int width, int height)
    {
        byte[] result = CopyBgra(source, width, height);
        for (int offset = 3; offset < result.Length; offset += 4)
        {
            result[offset] = byte.MaxValue;
        }

        return result;
    }

    private static byte[] DecodeA4R4G4B4(byte[] source, int width, int height)
    {
        int pixelCount = checked(width * height);
        if (source.Length < pixelCount * 2)
        {
            throw new InvalidDataException("The TDX pixel buffer is truncated.");
        }

        byte[] result = new byte[pixelCount * 4];
        for (int sourceOffset = 0, targetOffset = 0;
             targetOffset < result.Length;
             sourceOffset += 2, targetOffset += 4)
        {
            result[targetOffset] = (byte)((source[sourceOffset] & 0x0F) * 0x11);
            result[targetOffset + 1] = (byte)(((source[sourceOffset] >> 4) & 0x0F) * 0x11);
            result[targetOffset + 2] = (byte)((source[sourceOffset + 1] & 0x0F) * 0x11);
            result[targetOffset + 3] = (byte)(((source[sourceOffset + 1] >> 4) & 0x0F) * 0x11);
        }

        return result;
    }
}
