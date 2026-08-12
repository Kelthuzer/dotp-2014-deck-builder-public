using Gibbed.Duels.FileFormats;
using Tdx = Gibbed.Duels.FileFormats.Tdx;

namespace DeckBuilder.GameData;

public static class TdxImageEncoder
{
    /// <summary>
    /// Writes an uncompressed A8R8G8B8 TDX from a tightly packed BGRA32 pixel buffer.
    /// The original DotP 2014 Deck Builder uses the same format as its safe default for
    /// user-supplied images, and uncompressed images do not require mipmaps.
    /// </summary>
    public static void SaveBgra32(string outputPath, int width, int height, byte[] bgraPixels)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(bgraPixels);
        if (width <= 0 || height <= 0 || width > ushort.MaxValue || height > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(width), $"Invalid TDX size {width}x{height}.");

        int requiredLength = checked(width * height * 4);
        if (bgraPixels.Length != requiredLength)
            throw new InvalidDataException(
                $"BGRA buffer has {bgraPixels.Length} bytes; {requiredLength} are required for {width}x{height}.");

        TdxFile tdx = new()
        {
            Width = checked((ushort)width),
            Height = checked((ushort)height),
            Flags = 0,
            Format = Tdx.D3DFormat.A8R8G8B8
        };
        tdx.Mipmaps.Add(new Tdx.Mipmap
        {
            Width = checked((ushort)width),
            Height = checked((ushort)height),
            Data = bgraPixels
        });

        string fullPath = Path.GetFullPath(outputPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        using FileStream output = File.Create(fullPath);
        tdx.Serialize(output);
    }
}
