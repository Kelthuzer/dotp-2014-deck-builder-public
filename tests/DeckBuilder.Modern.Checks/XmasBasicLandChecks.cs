using System.Reflection;
using System.Runtime.CompilerServices;
using DeckBuilder.Core.Models;
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
            "MOUNTAIN_999001",
            "Гора",
            "Mountain",
            string.Empty,
            "XMAS",
            string.Empty);

        HashSet<char> colors = Invoke<HashSet<char>>(basicLandColors, xmasMountain);
        True(colors.SetEquals(['R']),
            "XMAS MOUNTAIN_* must be recognized as a red basic land even when TypeLine is incomplete.");
        True(Invoke<bool>(isLand, xmasMountain),
            "XMAS MOUNTAIN_* must be recognized as a land even when TypeLine is incomplete.");
        True(Invoke<bool>(isBasicLand, xmasMountain),
            "XMAS MOUNTAIN_* with the canonical English name must pass the basic-land filter used by random/auto land generation.");

        CardRecord localizedOnlyPlains = new(
            "XMAS_BASIC_999003",
            "Равнина",
            "Равнина",
            string.Empty,
            "XMAS",
            string.Empty);
        True(Invoke<HashSet<char>>(basicLandColors, localizedOnlyPlains).SetEquals(['W']),
            "A localized-only XMAS Plains must still be recognized as a white basic land.");
        True(Invoke<bool>(isLand, localizedOnlyPlains),
            "A localized-only XMAS Plains must be recognized as a land.");
        True(Invoke<bool>(isBasicLand, localizedOnlyPlains),
            "A localized-only XMAS Plains must pass the basic-land filter used by random/auto land generation.");

        CardRecord nonBasicMountain = new(
            "MADBLIND_MOUNTAIN_999002",
            "Безумная гора",
            "Madblind Mountain",
            "Land Mountain",
            "XMAS",
            string.Empty);
        True(Invoke<HashSet<char>>(basicLandColors, nonBasicMountain).Count == 0,
            "A nonbasic card whose filename merely contains MOUNTAIN must not be classified as a basic Mountain.");
        True(!Invoke<bool>(isBasicLand, nonBasicMountain),
            "A nonbasic Mountain must not pass the basic-land filter.");

        CardRecord islandOfWakWak = new(
            "ISLAND_OF_WAK_WAK_999004",
            "Island of Wak-Wak",
            "Island of Wak-Wak",
            "Land Island",
            "ARN",
            string.Empty);
        True(Invoke<HashSet<char>>(basicLandColors, islandOfWakWak).Count == 0,
            "Island of Wak-Wak must not be classified as a basic Island just because its filename starts with ISLAND_.");
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
