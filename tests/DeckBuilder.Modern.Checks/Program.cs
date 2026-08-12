using System.Reflection;
using System.Windows.Controls;
using DeckBuilder.GameData;
using DeckBuilder.Modern;

internal static class Program
{
    private static readonly string[] RequiredEmbeddedIds =
    [
        "MANA_0", "MANA_1", "MANA_2", "MANA_3", "MANA_4", "MANA_5", "MANA_6", "MANA_7",
        "MANA_8", "MANA_9", "MANA_10", "MANA_11", "MANA_12", "MANA_13", "MANA_14", "MANA_15",
        "MANA_16", "MANA_X",
        "MANA_B", "MANA_U", "MANA_G", "MANA_R", "MANA_W",
        "MANA_BG", "MANA_BR", "MANA_UB", "MANA_WB", "MANA_RG", "MANA_GU", "MANA_GW", "MANA_UR",
        "MANA_RW", "MANA_WU",
        "PHYREXIAN_BLACK_MANA", "PHYREXIAN_BLUE_MANA", "PHYREXIAN_GREEN_MANA",
        "PHYREXIAN_RED_MANA", "PHYREXIAN_WHITE_MANA",
        "MANA_T", "MANA_Q"
    ];

    private static readonly IReadOnlyDictionary<string, string> RequiredCostMappings =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["{0}"] = "MANA_0", ["{9}"] = "MANA_9", ["{11}"] = "MANA_11", ["{13}"] = "MANA_13",
            ["{16}"] = "MANA_16", ["{X}"] = "MANA_X",
            ["{B}"] = "MANA_B", ["{U}"] = "MANA_U", ["{G}"] = "MANA_G",
            ["{R}"] = "MANA_R", ["{W}"] = "MANA_W",
            ["{B/G}"] = "MANA_BG", ["{G/B}"] = "MANA_BG",
            ["{B/R}"] = "MANA_BR", ["{R/B}"] = "MANA_BR",
            ["{B/U}"] = "MANA_UB", ["{U/B}"] = "MANA_UB",
            ["{B/W}"] = "MANA_WB", ["{W/B}"] = "MANA_WB",
            ["{G/R}"] = "MANA_RG", ["{R/G}"] = "MANA_RG",
            ["{G/U}"] = "MANA_GU", ["{U/G}"] = "MANA_GU",
            ["{G/W}"] = "MANA_GW", ["{W/G}"] = "MANA_GW",
            ["{R/U}"] = "MANA_UR", ["{U/R}"] = "MANA_UR",
            ["{R/W}"] = "MANA_RW", ["{W/R}"] = "MANA_RW",
            ["{U/W}"] = "MANA_WU", ["{W/U}"] = "MANA_WU",
            ["{B/P}"] = "PHYREXIAN_BLACK_MANA", ["{U/P}"] = "PHYREXIAN_BLUE_MANA",
            ["{G/P}"] = "PHYREXIAN_GREEN_MANA", ["{R/P}"] = "PHYREXIAN_RED_MANA",
            ["{W/P}"] = "PHYREXIAN_WHITE_MANA"
        };

    private static readonly IReadOnlyDictionary<string, string> RequiredTextMappings =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["{B}"] = "MANA_B", ["{U}"] = "MANA_U", ["{G}"] = "MANA_G",
            ["{R}"] = "MANA_R", ["{W}"] = "MANA_W", ["{T}"] = "MANA_T", ["{q}"] = "MANA_Q",
            ["{a}"] = "MANA_BG", ["{b}"] = "MANA_BR", ["{c}"] = "MANA_GU", ["{d}"] = "MANA_GW",
            ["{e}"] = "MANA_RG", ["{f}"] = "MANA_RW", ["{g}"] = "MANA_UB", ["{h}"] = "MANA_UR",
            ["{i}"] = "MANA_WB", ["{j}"] = "MANA_WU",
            ["{J}"] = "PHYREXIAN_BLACK_MANA", ["{K}"] = "PHYREXIAN_BLUE_MANA",
            ["{L}"] = "PHYREXIAN_GREEN_MANA", ["{I}"] = "PHYREXIAN_RED_MANA",
            ["{O}"] = "PHYREXIAN_WHITE_MANA", ["{Q}"] = "CHAOS_UNLOCK_SYMBOL"
        };

    [STAThread]
    private static int Main()
    {
        bool failed = false;
        foreach ((string token, string expected) in RequiredCostMappings)
        {
            failed |= !VerifyMapping("COST", token, expected, CardVisualMetadata.ManaImageId(token));
        }

        foreach ((string token, string expected) in RequiredTextMappings)
        {
            failed |= !VerifyMapping("TEXT", token, expected, DotpSymbolMap.TextTokenImageId(token));
        }

        failed |= !VerifyNullMapping("COST", "{T}", CardVisualMetadata.ManaImageId("{T}"));
        failed |= !VerifyNullMapping("COST", "{Q}", CardVisualMetadata.ManaImageId("{Q}"));
        failed |= !VerifyNullMapping("COST", "{b}", CardVisualMetadata.ManaImageId("{b}"));
        failed |= !VerifyNullMapping("TEXT", "{B/G}", DotpSymbolMap.TextTokenImageId("{B/G}"));
        failed |= !VerifyNullMapping("TEXT", "{P}", DotpSymbolMap.TextTokenImageId("{P}"));

        Assembly modern = typeof(MainWindow).Assembly;
        Type symbols = modern.GetType("DeckBuilder.Modern.EmbeddedManaSymbols", throwOnError: true)!;
        MethodInfo tryGet = symbols.GetMethod("TryGet", BindingFlags.Static | BindingFlags.Public)
            ?? throw new MissingMethodException(symbols.FullName, "TryGet");

        foreach (string imageId in RequiredEmbeddedIds)
        {
            object? image;
            try
            {
                image = tryGet.Invoke(null, [imageId]);
            }
            catch (TargetInvocationException exception)
            {
                failed = true;
                Console.Error.WriteLine($"DECODE FAIL {imageId}: {exception.InnerException ?? exception}");
                continue;
            }

            if (image is null)
            {
                failed = true;
                Console.Error.WriteLine($"MISSING/DECODE FAIL {imageId}");
            }
            else
            {
                Console.WriteLine($"EMBEDDED OK {imageId}");
            }
        }

        failed |= !VerifyPresenter("{2}{B}", 2);
        failed |= !VerifyPresenter("{3}{W}{B}{G}", 4);
        failed |= !VerifyPresenter("{W}{U}{B}{R}{G}", 5);
        failed |= !VerifyPresenter("{1}{U}{U}", 3);
        failed |= !VerifyPresenter("{B/G}{R/P}", 2);
        failed |= !VerifyPresenterContainsText("{T}", "{T}");

        if (failed)
        {
            return 1;
        }

        Console.WriteLine(
            $"Verified {RequiredEmbeddedIds.Length} embedded DotP symbols, " +
            $"{RequiredCostMappings.Count} legacy cost mappings, {RequiredTextMappings.Count} legacy text mappings " +
            "and representative presenter output.");
        return 0;
    }

    private static bool VerifyMapping(string table, string token, string expected, string? actual)
    {
        if (string.Equals(actual, expected, StringComparison.Ordinal))
        {
            return true;
        }

        Console.Error.WriteLine($"{table} MAPPING FAIL {token}: expected {expected}, got {actual ?? "<null>"}");
        return false;
    }

    private static bool VerifyNullMapping(string table, string token, string? actual)
    {
        if (actual is null)
        {
            return true;
        }

        Console.Error.WriteLine($"{table} MAPPING FAIL {token}: expected <null>, got {actual}");
        return false;
    }

    private static bool VerifyPresenter(string cost, int expectedCount)
    {
        ManaCostPresenter presenter = new() { Cost = cost };
        bool valid = presenter.Children.Count == expectedCount
                     && presenter.Children.Cast<object>().All(child => child is Image);
        if (valid)
        {
            Console.WriteLine($"PRESENTER OK {cost}");
            return true;
        }

        string children = string.Join(", ", presenter.Children.Cast<object>().Select(child => child.GetType().Name));
        Console.Error.WriteLine(
            $"PRESENTER FAIL {cost}: expected {expectedCount} Image children; got {presenter.Children.Count}: {children}");
        return false;
    }

    private static bool VerifyPresenterContainsText(string cost, string expectedText)
    {
        ManaCostPresenter presenter = new() { Cost = cost };
        bool valid = presenter.Children.Count == 1
                     && presenter.Children[0] is TextBlock text
                     && text.Text == expectedText;
        if (valid)
        {
            Console.WriteLine($"PRESENTER TEXT OK {cost}");
            return true;
        }

        Console.Error.WriteLine($"PRESENTER FAIL {cost}: expected literal text {expectedText}.");
        return false;
    }
}
