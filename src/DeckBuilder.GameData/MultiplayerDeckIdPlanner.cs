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
    public const int MinimumCustomContentPackId = 1000;
    public const int MaximumSafeContentPackId = 8191;

    // DotP_D14.exe multiplayer crash dumps show content_pack being used as an index into a
    // fixed-size bit table. The former 9100-9109 presets therefore indexed past the table and
    // crashed the host with 0xC0000005. Keep custom multiplayer content-pack IDs below 8192.
    // Player 1 deliberately starts at 1000 for the control test.
    public static IReadOnlyList<MultiplayerIdBlockPreset> PlayerPresets { get; } =
        Enumerable.Range(0, 10)
            .Select(index => new MultiplayerIdBlockPreset(index + 1, 1000 + index))
            .ToArray();

    public static MultiplayerIdBlockPreset GetPlayerPreset(int playerNumber)
    {
        if (playerNumber is < 1 or > 10)
            throw new ArgumentOutOfRangeException(nameof(playerNumber), "Player number must be between 1 and 10.");

        return PlayerPresets[playerNumber - 1];
    }

    public static bool IsSafeContentPackId(int idBlock) =>
        idBlock is >= MinimumCustomContentPackId and <= MaximumSafeContentPackId;

    public static void ValidateContentPackId(int idBlock)
    {
        if (!IsSafeContentPackId(idBlock))
        {
            throw new ArgumentOutOfRangeException(
                nameof(idBlock),
                idBlock,
                $"DotP 2014 custom content_pack must be between {MinimumCustomContentPackId} and {MaximumSafeContentPackId}. " +
                "IDs 8192 and above are blocked because the game indexes content_pack into a fixed-size multiplayer bit table.");
        }
    }

    public static int SuggestSlot(string gameDirectory, int idBlock, int preferredDeckUid = -1)
    {
        ValidateContentPackId(idBlock);

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
        ValidateContentPackId(idBlock);
        if (slot is < 0 or > 99)
            throw new ArgumentOutOfRangeException(nameof(slot), "Deck slot must be between 0 and 99.");

        return int.Parse($"{idBlock}{slot:00}");
    }

    public static int SlotFromDeckUid(int uid, int idBlock)
    {
        ValidateContentPackId(idBlock);
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
        ValidateContentPackId(idBlock);
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
}
