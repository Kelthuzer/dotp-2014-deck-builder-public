using System.Runtime.CompilerServices;
using DeckBuilder.Core.Models;
using DeckBuilder.Core.Services;
using DeckBuilder.GameData;

internal static class GameCatalogLoadOrderChecks
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dotp-catalog-order-{Guid.NewGuid():N}");
        try
        {
            string low = CreateLooseWad(root, "DATA_DLC_LOW_PRIORITY", 1);
            string high = CreateLooseWad(root, "DATA_DLC_DECK_BUILDER_CUSTOM", 99);

            WriteCard(
                low,
                """
                <CARD_V2 ExportVersion="1">
                  <FILENAME text="SHARED_XMAS_PLAINS" />
                  <CARDNAME text="NOT_A_BASIC" />
                  <TITLE><LOCALISED_TEXT LanguageCode="ru-RU">Очень богатая старая версия</LOCALISED_TEXT></TITLE>
                  <ARTID value="LOW_ART" />
                  <EXPANSION value="LOW" />
                  <ARTIST name="Low priority artist" />
                  <TYPE metaname="Land" />
                  <SUB_TYPE metaname="Island" />
                  <FRAME_TYPE type="LAND" />
                  <STATIC_ABILITY><LOCALISED_TEXT LanguageCode="ru-RU">Старый текст способности</LOCALISED_TEXT></STATIC_ABILITY>
                </CARD_V2>
                """);

            WriteCard(
                high,
                """
                <CARD_V2 ExportVersion="1">
                  <FILENAME text="SHARED_XMAS_PLAINS" />
                  <CARDNAME text="PLAINS" />
                  <ARTID value="HIGH_ART" />
                  <SUPERTYPE metaname="Basic" />
                  <TYPE metaname="Land" />
                  <SUB_TYPE metaname="Plains" />
                </CARD_V2>
                """);

            CatalogLoadResult result = new GameCardCatalogLoader()
                .LoadAsync(root)
                .GetAwaiter()
                .GetResult();
            CardRecord card = result.Cards.Single(item =>
                item.FileName.Equals("SHARED_XMAS_PLAINS", StringComparison.OrdinalIgnoreCase));

            Equal("Basic Land Plains", card.TypeLine,
                "The order-99 native XMAS definition must override a richer lower-order duplicate.");
            Equal("PLAINS", card.EnglishName,
                "Catalog precedence must preserve the higher-order CARDNAME, not the richer older duplicate.");
            True(card.Source.Equals("DATA_DLC_DECK_BUILDER_CUSTOM", StringComparison.OrdinalIgnoreCase),
                "The selected card source must be the order-99 native XMAS loose WAD.");
            True(CardLandClassification.BasicLandColors(card).SetEquals(['W']),
                "The selected order-99 XMAS definition must remain a white basic land.");

            Console.WriteLine("PASS: game catalog WAD-order precedence");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateLooseWad(string root, string name, int order)
    {
        string directory = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(directory, "DATA_ALL_PLATFORMS", "CARDS"));
        File.WriteAllText(
            Path.Combine(directory, "HEADER.XML"),
            $"<WAD_HEADER><ENTRY platform=\"ALL\" source=\"{name}/DATA_ALL_PLATFORMS/\" alias=\"Content\" order=\"{order}\" /></WAD_HEADER>");
        return directory;
    }

    private static void WriteCard(string looseWad, string xml)
    {
        File.WriteAllText(
            Path.Combine(looseWad, "DATA_ALL_PLATFORMS", "CARDS", "SHARED_XMAS_PLAINS.xml"),
            xml);
    }

    private static void True(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message) where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message} Expected {expected}, got {actual}.");
    }
}
