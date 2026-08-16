namespace DeckBuilder.Modern;

internal static class RandomDeckDictionaryExtensions
{
    public static int GetValueOrDefault(this IDictionary<string, int> dictionary, string key) =>
        dictionary.TryGetValue(key, out int value) ? value : 0;
}
