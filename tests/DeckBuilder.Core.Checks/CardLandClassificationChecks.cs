using System.Runtime.CompilerServices;
using DeckBuilder.Core.Models;
using DeckBuilder.Core.Services;

internal static class CardLandClassificationChecks
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        CardRecord xmasPlains = Card(
            "PLAINS_CW_10581",
            "Равнина",
            "PLAINS",
            "Basic Land Plains");
        True(CardLandClassification.IsLand(xmasPlains),
            "A real XMAS Basic Land Plains definition must be a land.");
        True(CardLandClassification.IsBasicLand(xmasPlains),
            "A real XMAS Basic Land Plains definition must be basic.");
        True(CardLandClassification.BasicLandColors(xmasPlains).SetEquals(['W']),
            "A real XMAS Basic Land Plains definition must provide white mana classification.");

        CardRecord xmasNegativeForest = Card(
            "FOREST_CW_NEG_29",
            "Лес",
            "FOREST",
            "Basic Land Forest");
        True(CardLandClassification.IsBasicLand(xmasNegativeForest),
            "XMAS CW_NEG filenames must not matter when CARD_V2 metadata says Basic Land Forest.");
        True(CardLandClassification.BasicLandColors(xmasNegativeForest).SetEquals(['G']),
            "XMAS Basic Land Forest metadata must classify as green regardless of filename suffix.");

        CardRecord wakWak = Card(
            "ISLAND_OF_WAKWAK_CW_989",
            "Island of Wak-Wak",
            "ISLAND_OF_WAKWAK",
            "Land");
        True(CardLandClassification.IsLand(wakWak),
            "Island of Wak-Wak must remain a land.");
        True(!CardLandClassification.IsBasicLand(wakWak),
            "An explicit Land without Basic must never be promoted from an ISLAND_* filename.");
        True(CardLandClassification.BasicLandColors(wakWak).Count == 0,
            "Island of Wak-Wak must not provide a basic-Island color classification.");

        CardRecord malformedFallback = Card(
            "COMMUNITY_PLAINS_UNKNOWN",
            "Равнина",
            "PLAINS",
            string.Empty);
        True(CardLandClassification.IsBasicLand(malformedFallback),
            "A type-less legacy definition may fall back to an exact canonical basic-land name.");
        True(CardLandClassification.BasicLandColors(malformedFallback).SetEquals(['W']),
            "Exact canonical name fallback must still classify a type-less Plains as white.");

        CardRecord deceptiveNonbasic = Card(
            "ISLAND_FAKE_PREFIX",
            "Island of Something",
            "ISLAND_OF_SOMETHING",
            "Land Island");
        True(!CardLandClassification.IsBasicLand(deceptiveNonbasic),
            "Explicit Land Island metadata without Basic must win over filename prefixes.");

        DeckDocument deck = new();
        DeckEditor editor = new(deck);
        for (int copy = 0; copy < 6; copy++)
            editor.Add(xmasPlains, DeckSection.MainDeck);
        True(deck.MainDeckCardCount == 6,
            "DeckEditor must allow more than four copies of a metadata-defined basic land.");

        DeckDocument wakDeck = new();
        DeckEditor wakEditor = new(wakDeck);
        for (int copy = 0; copy < 4; copy++)
            wakEditor.Add(wakWak, DeckSection.MainDeck);
        Throws<InvalidOperationException>(
            () => wakEditor.Add(wakWak, DeckSection.MainDeck),
            "DeckEditor must still enforce the four-copy limit for Island of Wak-Wak.");

        Console.WriteLine("PASS: CARD_V2 metadata land classification");
    }

    private static CardRecord Card(string fileName, string localizedName, string englishName, string typeLine) => new(
        fileName,
        localizedName,
        englishName,
        typeLine,
        "XMAS",
        "");

    private static void True(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }

    private static void Throws<TException>(Action action, string message) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }
}
