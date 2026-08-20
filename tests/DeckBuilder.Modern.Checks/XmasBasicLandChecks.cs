using System.Reflection;
using System.Runtime.CompilerServices;
using DeckBuilder.Core.Models;
using DeckBuilder.Modern;

internal static class XmasBasicLandChecks
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        CardRecord xmasMountain = new(
            "MOUNTAIN_999001",
            "Гора",
            "Mountain",
            string.Empty,
            "XMAS",
            string.Empty);

        HashSet<char> colors = MainWindow.BasicLandColors(xmasMountain);
        True(colors.SetEquals(['R']), "XMAS MOUNTAIN_* must be recognized as a red basic land even when TypeLine is incomplete.");
        True(MainWindow.IsLand(xmasMountain), "XMAS MOUNTAIN_* must be recognized as a land even when TypeLine is incomplete.");

        MethodInfo isBasicLand = typeof(MainWindow).GetMethod(
            "IsBasicLand",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(MainWindow).FullName, "IsBasicLand");
        bool basic = (bool)(isBasicLand.Invoke(null, [xmasMountain]) ?? false);
        True(basic, "XMAS MOUNTAIN_* with the canonical English name must pass the basic-land filter used by random/auto land generation.");

        CardRecord nonBasicMountain = new(
            "MADBLIND_MOUNTAIN_999002",
            "Безумная гора",
            "Madblind Mountain",
            "Land Mountain",
            "XMAS",
            string.Empty);
        True(MainWindow.BasicLandColors(nonBasicMountain).Count == 0,
            "A nonbasic card whose filename merely contains MOUNTAIN must not be classified as a basic Mountain.");

        Console.WriteLine("PASS: XMAS basic-land recognition");
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
