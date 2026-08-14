namespace DeckBuilder.GameData;

public static class TdxPreviewLoader
{
    public static async Task<CardImageData?> LoadFileAsync(string? path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        byte[] data = await File.ReadAllBytesAsync(path, cancellationToken);
        return await Task.Run(() => TdxImageDecoder.Decode(data), cancellationToken);
    }
}
