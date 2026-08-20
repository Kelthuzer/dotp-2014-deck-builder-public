namespace DeckBuilder.GameData;

/// <summary>
/// Chooses a built-in personality for a newly-created deck when the user did not select one.
/// A blank personality makes Magic 2014 lose the AI avatar/zone presentation, so exported decks
/// must always reference a usable installed personality.
/// </summary>
public static class GamePersonalityFallbackSelector
{
    public static InstalledPersonalityRecord? SelectBest(IEnumerable<InstalledPersonalityRecord> personalities)
    {
        ArgumentNullException.ThrowIfNull(personalities);

        return personalities
            .Where(IsUsable)
            .OrderByDescending(IsCorePersonality)
            .ThenByDescending(DefinitionScore)
            .ThenBy(personality => personality.FileName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static bool IsUsable(InstalledPersonalityRecord personality) =>
        !string.IsNullOrWhiteSpace(personality.FileName)
        && !string.IsNullOrWhiteSpace(personality.LargeAvatarImage)
        && !string.IsNullOrWhiteSpace(personality.SmallAvatarImage);

    private static bool IsCorePersonality(InstalledPersonalityRecord personality) =>
        personality.Source.StartsWith("DATA_CORE", StringComparison.OrdinalIgnoreCase);

    private static int DefinitionScore(InstalledPersonalityRecord personality)
    {
        int score = 0;
        if (!string.IsNullOrWhiteSpace(personality.LargeAvatarImage)) score += 20;
        if (!string.IsNullOrWhiteSpace(personality.SmallAvatarImage)) score += 20;
        if (!string.IsNullOrWhiteSpace(personality.SmallAvatarLockedImage)) score += 5;
        if (!string.IsNullOrWhiteSpace(personality.LobbyImage)) score += 5;
        if (!string.IsNullOrWhiteSpace(personality.NameTag)) score += 3;
        if (!string.IsNullOrWhiteSpace(personality.Music)) score += 1;
        return score;
    }
}
