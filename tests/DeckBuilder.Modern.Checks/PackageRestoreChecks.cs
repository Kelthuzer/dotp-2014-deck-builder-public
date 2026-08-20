using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using DeckBuilder.Modern;

internal static class PackageRestoreChecks
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        MethodInfo resolver = typeof(MainWindow).GetMethod(
            "ResolvePackagedDeckWadPath",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(MainWindow).FullName, "ResolvePackagedDeckWadPath");

        string root = Path.Combine(Path.GetTempPath(), $"dotp-package-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string deck = Path.Combine(root, "Data_Decks_100000_WHITEDICK.wad");
            string support = Path.Combine(root, "Data_DLC_9000_100000_WHITEDICK_Cards.wad");
            string manifest = support + ".sources.json";
            File.WriteAllBytes(deck, [1]);
            File.WriteAllBytes(support, [2]);
            File.WriteAllText(manifest, """
                {
                  "formatVersion": 3,
                  "wad": "D:\\old\\Magic 2014\\MyDecks\\Data_DLC_9000_100000_WHITEDICK_Cards.wad"
                }
                """);

            Equal(deck, Invoke(resolver, support), "Support WAD should resolve to its sibling deck WAD.");
            Equal(deck, Invoke(resolver, manifest), "Legacy .wad.sources.json should resolve to its sibling deck WAD.");
            Equal(deck, Invoke(resolver, deck), "A direct Data_Decks WAD should remain the selected source.");

            File.Delete(deck);
            bool missingRejected = false;
            try
            {
                _ = Invoke(resolver, manifest);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is FileNotFoundException)
            {
                missingRejected = true;
            }

            True(missingRejected,
                "A sources manifest without a sibling Data_Decks WAD must be rejected instead of inventing deck quantities from rootCards.");
            Console.WriteLine("PASS: packaged deck path recovery");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Invoke(MethodInfo method, string path) =>
        (string)(method.Invoke(null, [path])
            ?? throw new InvalidOperationException("Package resolver returned null."));

    private static void True(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }

    private static void Equal(string expected, string actual, string message)
    {
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{message} Expected '{expected}', got '{actual}'.");
    }
}
