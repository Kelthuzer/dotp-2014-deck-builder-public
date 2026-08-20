using System.Runtime.CompilerServices;
using DeckBuilder.GameData;

internal static class LenientCardIdentityChecks
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        Run();
        Console.WriteLine("PASS: lenient CARD_V2 identity parsing");
    }

    private static void Run()
    {
        string malformed = """
            <CARD_V2>
              <FILENAME text="TOKEN_HUMAN_SOLDIER_C_1_1_W_CW_1" />
              <CARDNAME text="HUMAN_SOLDIER" />
              <ARTID value="TOKEN_HUMAN_SOLDIER_C_1_1_W_CW_1" />
              <POWER value="1" />
              <TOUGHNESS value="1" />
              <TOKEN />
              <BROKEN text="A & B" />
            </CARD_V2>
            """;

        var card = CardXmlParser.Parse(malformed, "test");
        if (card is null)
            throw new InvalidOperationException("Malformed legacy CARD_V2 identity was not recovered.");
        if (!card.FileName.Equals("TOKEN_HUMAN_SOLDIER_C_1_1_W_CW_1", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Wrong recovered FILENAME: {card.FileName}");
        if (!card.ImageId.Equals("TOKEN_HUMAN_SOLDIER_C_1_1_W_CW_1", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Wrong recovered ARTID: {card.ImageId}");
        if (!card.IsToken)
            throw new InvalidOperationException("Recovered token CARD_V2 lost its TOKEN marker.");
    }
}
