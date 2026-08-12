namespace DeckBuilder.GameData;

internal static class GameWadSelection
{
    public static bool IsSupported(string path)
    {
        string name = Path.GetFileName(path) ?? path;
        if (name.Contains("HideOfficialDecks", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return name.StartsWith("data_core", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("data_dlc_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("data_decks_", StringComparison.OrdinalIgnoreCase);
    }
}
