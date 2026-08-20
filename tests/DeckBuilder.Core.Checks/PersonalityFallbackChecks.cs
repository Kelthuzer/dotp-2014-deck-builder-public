using System.Runtime.CompilerServices;
using DeckBuilder.GameData;

internal static class PersonalityFallbackChecks
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        Run();
        Console.WriteLine("PASS: AI personality fallback selection");
    }

    private static void Run()
    {
        InstalledPersonalityRecord incompleteCore = new(
            "CORE_INCOMPLETE.XML", "DATA_CORE", "CORE_BAD", "Core incomplete",
            "LARGE", string.Empty, string.Empty, string.Empty, string.Empty);
        InstalledPersonalityRecord completeMod = new(
            "MOD_COMPLETE.XML", "DATA_DLC_MOD", "MOD", "Mod complete",
            "MOD_LARGE", "MOD_SMALL", "MOD_LOCKED", "MOD_LOBBY", "MOD_MUSIC");
        InstalledPersonalityRecord completeCore = new(
            "CORE_COMPLETE.XML", "DATA_CORE", "CORE", "Core complete",
            "CORE_LARGE", "CORE_SMALL", string.Empty, "CORE_LOBBY", "CORE_MUSIC");

        InstalledPersonalityRecord? selected = GamePersonalityFallbackSelector.SelectBest(
            [incompleteCore, completeMod, completeCore]);
        if (selected is null || !selected.FileName.Equals("CORE_COMPLETE.XML", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Fallback must prefer a usable built-in DATA_CORE personality.");
    }
}
