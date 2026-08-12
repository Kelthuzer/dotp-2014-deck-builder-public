namespace DeckBuilder.GameData;

public static class TdxFileImageLoader
{
    public static Task<CardImageData> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] data = File.ReadAllBytes(path);
            cancellationToken.ThrowIfCancellationRequested();
            return TdxImageDecoder.Decode(data);
        }, cancellationToken);
    }
}
