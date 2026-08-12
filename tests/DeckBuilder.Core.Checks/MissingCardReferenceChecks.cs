using System.Runtime.CompilerServices;
using DeckBuilder.GameData;

internal static class MissingCardReferenceChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        CheckLooseReferenceRecoveryAndIgnoredUtilityWad();
    }

    private static void CheckLooseReferenceRecoveryAndIgnoredUtilityWad()
    {
        string root = Path.Combine(Path.GetTempPath(), $"deck-builder-missing-card-{Guid.NewGuid():N}");
        try
        {
            string supported = Path.Combine(root, "Data_DLC_999_Reference_Test");
            string supportedCards = Path.Combine(supported, "DATA_ALL_PLATFORMS", "ODD_CARD_LOCATION");
            Directory.CreateDirectory(supportedCards);
            File.WriteAllText(
                Path.Combine(supportedCards, "RECOVER_ME.XML"),
                """
                <CARD_V2>
                  <FILENAME text="RECOVER_ME" />
                  <CARDNAME text="Recovered Card" />
                  <TITLE><LOCALISED_TEXT LanguageCode="en-US">Recovered Card</LOCALISED_TEXT></TITLE>
                  <TYPE metaname="Creature" />
                  <CASTING_COST cost="{1}{R}" />
                  <ARTID value="RECOVERED_ART" />
                </CARD_V2>
                """);

            string ignored = Path.Combine(root, "Data_Decks_HideOfficialDecks");
            string ignoredCards = Path.Combine(ignored, "DATA_ALL_PLATFORMS", "ODD_CARD_LOCATION");
            Directory.CreateDirectory(ignoredCards);
            File.WriteAllText(
                Path.Combine(ignoredCards, "IGNORE_ME.XML"),
                """
                <CARD_V2>
                  <FILENAME text="IGNORE_ME" />
                  <CARDNAME text="Ignored Card" />
                  <TITLE><LOCALISED_TEXT LanguageCode="en-US">Ignored Card</LOCALISED_TEXT></TITLE>
                  <ARTID value="IGNORED_ART" />
                </CARD_V2>
                """);

            MissingCardResolutionResult result = new MissingCardReferenceResolver()
                .ResolveAsync(root, new[] { "RECOVER_ME", "IGNORE_ME", "NOT_THERE" })
                .GetAwaiter()
                .GetResult();

            Equal(1, result.Cards.Count);
            Equal("RECOVER_ME", result.Cards.Single().FileName);
            Equal("RECOVERED_ART", result.Cards.Single().ImageId);
            True(result.UnresolvedReferences.Contains("IGNORE_ME", StringComparer.OrdinalIgnoreCase),
                "HideOfficialDecks must not provide recovered card definitions.");
            True(result.UnresolvedReferences.Contains("NOT_THERE", StringComparer.OrdinalIgnoreCase),
                "A genuinely absent card reference must remain unresolved.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
