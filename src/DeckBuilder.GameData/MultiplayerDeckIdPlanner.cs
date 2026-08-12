namespace DeckBuilder.GameData;

public sealed record MultiplayerIdBlockPreset(int PlayerNumber, int IdBlock)
{
    public string DisplayName => $"Player {PlayerNumber} · block {IdBlock}";

    public int FirstDeckUid => MultiplayerDeckIdPlanner.DeckUid(IdBlock, 0);

    public int LastDeckUid => MultiplayerDeckIdPlanner.DeckUid(IdBlock, 99);

    public string ContentPackEnablerFileName =>
        $"Data_DLC_{IdBlock}_Content_Pack_Enabler.wad";
}

public static class MultiplayerDeckIdPlanner
{
    // These blocks are reserved by this project for a private 10-player setup.
    // The historical SlightlyMagic Prefix/Id Registry is no longer reliably reachable;
    // archived forum searches did not find DotP 2014 uses of 9100-9109. Keep the list
    // centralized so it can be changed without touching export logic.
    public static IReadOnlyList<MultiplayerIdBlockPreset> PlayerPresets { get; } =
        Enumerable.Range(0, 10)
            .Select(index => new MultiplayerIdBlockPreset(index + 1, 9100 + index))
            .ToArray();

    public static MultiplayerIdBlockPreset GetPlayerPreset(int playerNumber)
    {
        if (playerNumber is < 1 or > 10)
            throw new ArgumentOutOfRangeException(nameof(playerNumber), "Player number must be between 1 and 10.");

        return PlayerPresets[playerNumber - 1];
    }

    public static int SuggestSlot(string gameDirectory, int idBlock, int preferredDeckUid = -1)
    {
        ValidateIdBlock(idBlock);

        int preferred = SlotFromDeckUid(preferredDeckUid, idBlock);
        IReadOnlySet<int> used = FindUsedSlots(gameDirectory, idBlock);
        if (preferred >= 0 && !used.Contains(preferred))
            return preferred;

        return Enumerable.Range(0, 100).FirstOrDefault(slot => !used.Contains(slot), -1);
    }

    public static bool IsDeckUidAvailable(string gameDirectory, int idBlock, int deckUid)
    {
        int slot = SlotFromDeckUid(deckUid, idBlock);
        return slot >= 0 && !FindUsedSlots(gameDirectory, idBlock).Contains(slot);
    }

    public static int DeckUid(int idBlock, int slot)
    {
        ValidateIdBlock(idBlock);
        if (slot is < 0 or > 99)
            throw new ArgumentOutOfRangeException(nameof(slot), "Deck slot must be between 0 and 99.");

        return int.Parse($"{idBlock}{slot:00}");
    }

    public static int SlotFromDeckUid(int uid, int idBlock)
    {
        ValidateIdBlock(idBlock);
        if (uid < 0)
            return -1;

        string value = uid.ToString();
        string prefix = idBlock.ToString();
        return value.Length == prefix.Length + 2
               && value.StartsWith(prefix, StringComparison.Ordinal)
               && int.TryParse(value[prefix.Length..], out int slot)
               && slot is >= 0 and <= 99
            ? slot
            : -1;
    }

    public static IReadOnlySet<int> FindUsedSlots(string gameDirectory, int idBlock)
    {
        ValidateIdBlock(idBlock);
        HashSet<int> used = new();
        if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory))
            return used;

        foreach (string path in Directory.EnumerateFiles(gameDirectory, "*.wad", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (!name.StartsWith("Data_Decks_", StringComparison.OrdinalIgnoreCase))
                continue;

            string remainder = name["Data_Decks_".Length..];
            string uidText = remainder.Split('_')[0];
            if (!int.TryParse(uidText, out int uid))
                continue;

            int slot = SlotFromDeckUid(uid, idBlock);
            if (slot >= 0)
                used.Add(slot);
        }

        return used;
    }

    private static void ValidateIdBlock(int idBlock)
    {
        if (idBlock is < 1000 or > 9999)
            throw new ArgumentOutOfRangeException(nameof(idBlock), "DotP 2014 custom ID block must be a four-digit value.");
    }
}
