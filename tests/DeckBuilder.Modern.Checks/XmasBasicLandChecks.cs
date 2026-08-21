using System.Reflection;
using System.Runtime.CompilerServices;
using DeckBuilder.Core.Models;
using DeckBuilder.Core.Services;
using DeckBuilder.Modern;

internal static class XmasBasicLandChecks
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        MethodInfo basicLandColors = RequireStaticNonPublic("BasicLandColors");
        MethodInfo isLand = RequireStaticNonPublic("IsLand");
        MethodInfo isBasicLand = RequireStaticNonPublic("IsBasicLand");

        CardRecord xmasMountain = new(
            "MOUNTAIN_CW_NEG_67",
            "Гора",
            "MOUNTAIN",
            "Basic Land Mountain",
            "XMAS",
            string.Empty);

        HashSet<char> colors = Invoke<HashSet<char>>(basicLandColors, xmasMountain);
        True(colors.SetEquals(['R']),
            "XMAS Basic Land Mountain metadata must be recognized as red regardless of filename suffix.");
        True(Invoke<bool>(isLand, xmasMountain),
            "XMAS Basic Land Mountain metadata must be recognized as a land.");
        True(Invoke<bool>(isBasicLand, xmasMountain),
            "XMAS Basic Land Mountain metadata must pass the Modern basic-land filter.");

        CardRecord localizedOnlyPlains = new(
            "COMMUNITY_BASIC_UNKNOWN",
            "Равнина",
            "PLAINS",
            string.Empty,
            "XMAS",
            string.Empty);
        True(Invoke<HashSet<char>>(basicLandColors, localizedOnlyPlains).SetEquals(['W']),
            "A type-less legacy Plains may still use exact-name fallback.");
        True(Invoke<bool>(isLand, localizedOnlyPlains),
            "A type-less exact-name Plains fallback must be recognized as a land.");
        True(Invoke<bool>(isBasicLand, localizedOnlyPlains),
            "A type-less exact-name Plains fallback must pass the basic-land filter.");

        CardRecord xmasCwPlains = new(
            "PLAINS_CW_10581",
            "Равнина",
            "PLAINS",
            "Basic Land Plains",
            "XMAS",
            string.Empty);
        True(Invoke<HashSet<char>>(basicLandColors, xmasCwPlains).SetEquals(['W']),
            "The baseline XMAS PLAINS_CW_10581 shape must be recognized as white from metadata.");
        True(Invoke<bool>(isLand, xmasCwPlains),
            "The baseline XMAS PLAINS_CW_10581 shape must be recognized as a land.");
        True(Invoke<bool>(isBasicLand, xmasCwPlains),
            "The baseline XMAS PLAINS_CW_10581 shape must pass the Modern basic-land filter.");

        DeckDocument xmasDeck = new();
        DeckEditor xmasEditor = new(xmasDeck);
        for (int copy = 0; copy < 6; copy++)
            xmasEditor.Add(xmasCwPlains, DeckSection.MainDeck);
        True(xmasDeck.MainDeckCardCount == 6,
            "DeckEditor must allow more than four copies of a metadata-defined XMAS basic land.");

        CardRecord nonBasicMountain = new(
            "MADBLIND_MOUNTAIN_999002",
            "Безумная гора",
            "Madblind Mountain",
            "Land Mountain",
            "XMAS",
            string.Empty);
        True(Invoke<HashSet<char>>(basicLandColors, nonBasicMountain).Count == 0,
            "A nonbasic Mountain subtype must not be classified as a basic Mountain without Basic.");
        True(!Invoke<bool>(isBasicLand, nonBasicMountain),
            "A nonbasic Mountain must not pass the basic-land filter.");

        CardRecord islandOfWakWak = new(
            "ISLAND_OF_WAKWAK_CW_989",
            "Island of Wak-Wak",
            "ISLAND_OF_WAKWAK",
            "Land",
            "XMAS",
            string.Empty);
        True(Invoke<HashSet<char>>(basicLandColors, islandOfWakWak).Count == 0,
            "Island of Wak-Wak must not be classified as a basic Island from its filename.");
        True(Invoke<bool>(isLand, islandOfWakWak),
            "Island of Wak-Wak must still be recognized as a land.");
        True(!Invoke<bool>(isBasicLand, islandOfWakWak),
            "Island of Wak-Wak must remain subject to the normal constructed four-copy limit.");

        Console.WriteLine("PASS: XMAS basic-land recognition");
    }

    private static MethodInfo RequireStaticNonPublic(string name) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(MainWindow).FullName, name);

    private static T Invoke<T>(MethodInfo method, CardRecord card)
    {
        object? value = method.Invoke(null, [card]);
        return value is T typed
            ? typed
            : throw new InvalidOperationException($"{method.Name} returned {value?.GetType().FullName ?? "<null>"}, expected {typeof(T).FullName}.");
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
