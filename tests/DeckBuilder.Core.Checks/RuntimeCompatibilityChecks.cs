using System.Runtime.CompilerServices;
using DeckBuilder.GameData;

internal static class RuntimeCompatibilityChecks
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        Run();
        Console.WriteLine("PASS: CW token runtime compatibility");
    }

    private static void Run()
    {
        string card = """
            <CARD_V2>
              <RESOLUTION_TIME_ACTION>
                CW_Tokens("HUMAN_SOLDIER_C_1_1_W", 1)
              </RESOLUTION_TIME_ACTION>
              <TOKEN_REGISTRATION reservation="1" type="TOKEN_HUMAN_SOLDIER_C_1_1_W_CW_1" />
            </CARD_V2>
            """;

        IReadOnlyList<string> keys = WorkspaceRuntimeCompatibility.ExtractCwTokenKeys(card);
        Equal(1, keys.Count, "One CW token archetype should be discovered.");
        Equal("HUMAN_SOLDIER_C_1_1_W", keys[0], "CW token archetype was parsed incorrectly.");
        True(
            WorkspaceRuntimeCompatibility.IsDynamicCwRegistration(
                "TOKEN_HUMAN_SOLDIER_C_1_1_W_CW_1",
                keys),
            "Matching CW token registrations should still be recognizable for runtime diagnostics.");
        True(
            !WorkspaceRuntimeCompatibility.IsDynamicCwRegistration(
                "TOKEN_HUMAN_SOLDIER_C_1_1_W_OTHER_1",
                keys),
            "Unrelated token registrations must not be treated as CW runtime tokens.");

        WorkspaceCardDependencyScanResult missingScan = WorkspaceCardDependencyResolver.Scan(
            card,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            "HORN_TEST");
        True(
            missingScan.MissingTokenReferences.Contains(
                "TOKEN_HUMAN_SOLDIER_C_1_1_W_CW_1",
                StringComparer.OrdinalIgnoreCase),
            "CW_Tokens still instantiates a physical CARD_V2 filename; an unavailable registered token must be reported as missing.");

        Dictionary<string, string> aliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["TOKEN_HUMAN_SOLDIER_C_1_1_W_CW_1"] = "TOKEN_HUMAN_SOLDIER_C_1_1_W_CW_1"
        };
        WorkspaceCardDependencyScanResult resolvedScan = WorkspaceCardDependencyResolver.Scan(
            card,
            aliases,
            "HORN_TEST");
        True(
            resolvedScan.References.Contains(
                "TOKEN_HUMAN_SOLDIER_C_1_1_W_CW_1",
                StringComparer.OrdinalIgnoreCase),
            "An available CW token CARD_V2 must be packaged as a dependency.");
        True(
            !resolvedScan.MissingTokenReferences.Contains(
                "TOKEN_HUMAN_SOLDIER_C_1_1_W_CW_1",
                StringComparer.OrdinalIgnoreCase),
            "An available CW token CARD_V2 must not also be reported as missing.");

        string temp = Path.Combine(Path.GetTempPath(), $"cw-runtime-compat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            string oldRuntime = Path.Combine(temp, "old.lol");
            string newRuntime = Path.Combine(temp, "new.lol");
            File.WriteAllText(oldRuntime, "CW_TokenList = { ANGEL_C_4_4_W_F = 1 }");
            File.WriteAllText(newRuntime, "CW_TokenList = { ANGEL_C_4_4_W_F = 1, HUMAN_SOLDIER_C_1_1_W = 1 }");
            HashSet<string> required = new(keys, StringComparer.OrdinalIgnoreCase);

            int oldCoverage = WorkspaceRuntimeCompatibility.CountCwTokenCoverage(
                "FUNCTIONS\\CW_TOKENS.LOL",
                oldRuntime,
                required);
            int newCoverage = WorkspaceRuntimeCompatibility.CountCwTokenCoverage(
                "FUNCTIONS\\CW_TOKENS.LOL",
                newRuntime,
                required);
            Equal(0, oldCoverage, "Old runtime should not cover the Human Soldier token archetype.");
            Equal(1, newCoverage, "New runtime should cover the Human Soldier token archetype.");
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message} Expected {expected}; actual {actual}.");
    }
}
