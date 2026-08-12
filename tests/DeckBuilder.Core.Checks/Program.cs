using DeckBuilder.Core.Formats;
using DeckBuilder.Core.Models;
using DeckBuilder.Core.Services;
using DeckBuilder.GameData;

var checks = new (string Name, Action Run)[]
{
    ("card reference parsing", CheckCardReferenceParsing),
    ("card reference formatting", CheckCardReferenceFormatting),
    ("main deck quantity merging", CheckMainDeckMerging),
    ("unlock behavior and promo limit", CheckUnlockBehavior),
    ("move behavior", CheckMoveBehavior),
    ("multi-word indexed search", CheckSearch),
    ("existing game deck copy", CheckExistingDeckCopy),
    ("workspace round trip", CheckWorkspaceRoundTrip),
    ("DotP XML round trip", CheckDotpXmlRoundTrip),
    ("namespaced legacy DotP XML import", CheckNamespacedDotpXmlImport),
    ("standalone legacy unlock XML import", CheckStandaloneUnlockXmlImport),
    ("managed DXT card art decoding", CheckManagedDxtDecoding),
    ("card frame and mana visual metadata", CheckCardVisualMetadata),
    ("atomic game WAD export", CheckGameWadExport)
};

foreach ((string name, Action run) in checks)
{
    run();
    Console.WriteLine($"PASS: {name}");
}

Console.WriteLine($"All {checks.Length} modern core checks passed.");
return;

static void CheckCardReferenceParsing()
{
    Equal(new DeckCardReference("CARD_ONE", false, 1), DeckCardReference.Parse("CARD_ONE"));
    Equal(new DeckCardReference("CARD_ONE", true, 1), DeckCardReference.Parse(" CARD_ONE# "));
    Equal(new DeckCardReference("CARD_ONE", false, 3), DeckCardReference.Parse("CARD_ONE@3"));
    Equal(new DeckCardReference("CARD_ONE", true, 4), DeckCardReference.Parse("CARD_ONE#@4"));
    True(!DeckCardReference.TryParse("CARD_ONE@x", out _), "Invalid bias must be rejected.");
}

static void CheckCardReferenceFormatting()
{
    Equal("CARD_ONE", DeckCardReference.Format("CARD_ONE"));
    Equal("CARD_ONE#", DeckCardReference.Format("CARD_ONE", promo: true));
    Equal("CARD_ONE@2", DeckCardReference.Format("CARD_ONE", bias: 2));
    Equal("CARD_ONE#@2", DeckCardReference.Format("CARD_ONE", promo: true, bias: 2));
}

static void CheckMainDeckMerging()
{
    CardRecord card = Card("LIGHTNING_BOLT", "Lightning Bolt", "Instant", "M10", "Christopher Rush");
    DeckDocument deck = new();
    DeckEditor editor = new(deck);
    DeckEntry first = editor.Add(card, DeckSection.MainDeck);
    DeckEntry second = editor.Add(card, DeckSection.MainDeck);

    True(ReferenceEquals(first, second), "Equal main-deck entries must merge.");
    Equal(2, first.Quantity);
    Equal(2, deck.MainDeckCardCount);

    editor.Add(card, DeckSection.MainDeck, bias: 2);
    Equal(2, deck.MainDeck.Count);
}

static void CheckUnlockBehavior()
{
    CardRecord card = Card("SERRA_ANGEL", "Serra Angel", "Creature Angel", "M14", "Greg Staples");
    DeckDocument deck = new();
    DeckEditor editor = new(deck);
    editor.Add(card, DeckSection.RegularUnlocks);
    editor.Add(card, DeckSection.RegularUnlocks);
    Equal(2, deck.RegularUnlocks.Count);

    for (int index = 0; index < DeckDocument.MaximumPromoUnlocks; index++)
    {
        editor.Add(card, DeckSection.PromoUnlocks);
    }

    Throws<InvalidOperationException>(() => editor.Add(card, DeckSection.PromoUnlocks));
}

static void CheckMoveBehavior()
{
    CardRecord card = Card("SHOCK", "Shock", "Instant", "M14", "Jon Foster");
    DeckDocument deck = new();
    DeckEditor editor = new(deck);
    DeckEntry entry = editor.Add(card, DeckSection.MainDeck);
    editor.Add(card, DeckSection.MainDeck);
    editor.Move(entry, DeckSection.MainDeck, DeckSection.RegularUnlocks);

    Equal(1, deck.MainDeck.Single().Quantity);
    Equal(1, deck.RegularUnlocks.Count);
    Equal("SHOCK", deck.RegularUnlocks[0].CardReference);
}

static void CheckSearch()
{
    CardRecord angel = Card("SERRA_ANGEL", "Ангел Серры", "Creature Angel", "M14", "Greg Staples");
    CardRecord bolt = Card("LIGHTNING_BOLT", "Молния", "Instant", "M10", "Christopher Rush");
    CardSearchIndex index = new(new[] { angel, bolt });

    Equal(angel, index.Search("angel m14").Single());
    Equal(bolt, index.Search("rush instant").Single());
    Equal(2, index.Search("  ").Count);
    Equal(0, index.Search("angel m10").Count);
}

static void CheckExistingDeckCopy()
{
    CardRecord card = Card("SERRA_ANGEL", "Ангел Серры", "Creature Angel", "M14", "Greg Staples");
    const string xml = """
        <DECK uid="4200" personality="D14_PERSONALITY_ANGELS" deck_box_image="ANGELS"
              deck_box_image_locked="ANGELS_LOCKED" content_pack="14" always_available="true">
          <CARD name="SERRA_ANGEL" deckOrderId="0" />
          <CARD name="SERRA_ANGEL" deckOrderId="1" />
          <RegularUnlocks><CARD name="SERRA_ANGEL@2" deckOrderId="2" /></RegularUnlocks>
          <PromoUnlocks><CARD name="SERRA_ANGEL#" deckOrderId="3" /></PromoUnlocks>
          <LocalizedDeckNames><LOCALISED_TEXT LanguageCode="ru-RU">Небесное воинство</LOCALISED_TEXT></LocalizedDeckNames>
          <LocalizedDescriptions><LOCALISED_TEXT LanguageCode="ru-RU">Исходная колода</LOCALISED_TEXT></LocalizedDescriptions>
        </DECK>
        """;
    DeckDocument source = DotpDeckXmlSerializer.Parse(xml, new[] { card });
    DeckDocument copy = DeckDocumentCloner.Clone(source, 100042, "Небесное воинство — копия");

    Equal(4200, source.Uid);
    Equal(100042, copy.Uid);
    Equal("Небесное воинство — копия", copy.Name);
    Equal(source.Description, copy.Description);
    Equal(source.Personality, copy.Personality);
    Equal(source.DeckBoxImage, copy.DeckBoxImage);
    Equal(2, copy.MainDeckCardCount);
    Equal(1, copy.RegularUnlocks.Count);
    Equal(1, copy.PromoUnlocks.Count);
    True(!ReferenceEquals(source.MainDeck[0], copy.MainDeck[0]), "Copied entries must be independent.");
    copy.MainDeck[0].Quantity = 4;
    Equal(2, source.MainDeck[0].Quantity);
}

static void CheckWorkspaceRoundTrip()
{
    CardRecord card = new(
        "SERRA_ANGEL",
        "Ангел Серры",
        "Serra Angel",
        "Creature Angel",
        "M14",
        "Greg Staples",
        "{3}{W}{W}",
        "W",
        "R",
        "4",
        "4",
        "data_core",
        "SERRA_ANGEL_ART",
        "Flying\r\nVigilance",
        "Born with wings of light.",
        "",
        false);
    DeckDocument deck = new();
    deck.Uid = 100042;
    deck.Name = "Test deck";
    deck.Description = "Round-trip metadata";
    deck.Personality = "D14_PERSONALITY_TEST";
    DeckEditor editor = new(deck);
    editor.Add(card, DeckSection.MainDeck, bias: 2, promo: true);
    editor.Add(card, DeckSection.MainDeck, bias: 2, promo: true);
    editor.Add(card, DeckSection.RegularUnlocks);
    DeckWorkspace workspace = new("Test deck", deck, new[] { card });
    string path = Path.Combine(Path.GetTempPath(), $"deck-builder-{Guid.NewGuid():N}.dotpdeck");
    try
    {
        DeckWorkspaceSerializer.SaveAsync(path, workspace).GetAwaiter().GetResult();
        DeckWorkspace loaded = DeckWorkspaceSerializer.LoadAsync(path).GetAwaiter().GetResult();
        Equal("Test deck", loaded.Name);
        Equal(2, loaded.Deck.MainDeckCardCount);
        Equal("SERRA_ANGEL#@2", loaded.Deck.MainDeck.Single().CardReference);
        Equal(1, loaded.Deck.RegularUnlocks.Count);
        Equal("SERRA_ANGEL_ART", loaded.Catalog.Single().ImageId);
        Equal("Flying\r\nVigilance", loaded.Catalog.Single().RulesText);
        Equal("Born with wings of light.", loaded.Catalog.Single().FlavorText);
        Equal(100042, loaded.Deck.Uid);
        Equal("Test deck", loaded.Deck.Name);
        Equal("Round-trip metadata", loaded.Deck.Description);
        Equal("D14_PERSONALITY_TEST", loaded.Deck.Personality);
    }
    finally
    {
        File.Delete(path);
    }
}

static void CheckDotpXmlRoundTrip()
{
    CardRecord card = Card("LIGHTNING_BOLT", "Молния", "Instant", "M10", "Christopher Rush");
    DeckDocument deck = new();
    DeckEditor editor = new(deck);
    editor.Add(card, DeckSection.MainDeck);
    editor.Add(card, DeckSection.MainDeck);
    editor.Add(card, DeckSection.RegularUnlocks, bias: 3);
    string path = Path.Combine(Path.GetTempPath(), $"deck-builder-{Guid.NewGuid():N}.xml");
    try
    {
        DotpDeckXmlSerializer.Save(path, deck);
        DeckDocument loaded = DotpDeckXmlSerializer.Load(path, new[] { card });
        Equal(2, loaded.MainDeckCardCount);
        Equal("LIGHTNING_BOLT@3", loaded.RegularUnlocks.Single().CardReference);
    }
    finally
    {
        File.Delete(path);
    }
}

static void CheckNamespacedDotpXmlImport()
{
    CardRecord card = Card("SERRA_ANGEL", "Ангел Серры", "Creature Angel", "M14", "Greg Staples");
    string path = Path.Combine(Path.GetTempPath(), $"deck-builder-{Guid.NewGuid():N}.xml");
    try
    {
        File.WriteAllText(path,
            "<wrapper xmlns='urn:dotp'><DECK uid='100041' personality='TEST_AI'><CARD NAME='SERRA_ANGEL' DECKORDERID='7' quantity='2'/>" +
            "<LocalizedDeckNames><LOCALISED_TEXT LanguageCode='ru-RU'>Старая колода</LOCALISED_TEXT></LocalizedDeckNames>" +
            "<RegularUnlocks><CARD Name='SERRA_ANGEL@2'/></RegularUnlocks></DECK></wrapper>");
        DeckDocument loaded = DotpDeckXmlSerializer.Load(path, new[] { card });
        Equal(2, loaded.MainDeckCardCount);
        Equal(7, loaded.MainDeck.Single().OrderId);
        Equal("SERRA_ANGEL@2", loaded.RegularUnlocks.Single().CardReference);
        Equal(100041, loaded.Uid);
        Equal("Старая колода", loaded.Name);
        Equal("TEST_AI", loaded.Personality);
    }
    finally
    {
        File.Delete(path);
    }
}

static void CheckStandaloneUnlockXmlImport()
{
    CardRecord card = Card("SERRA_ANGEL", "Ангел Серры", "Creature Angel", "M14", "Greg Staples");
    string path = Path.Combine(Path.GetTempPath(), $"deck-builder-{Guid.NewGuid():N}.xml");
    try
    {
        File.WriteAllText(path,
            "<UNLOCKS uid='1000301' deck_uid='100001' game_mode='2'>" +
            "<CARD name='SERRA_ANGEL#@2' deckOrderId='4' quantity='2'/></UNLOCKS>");
        DeckDocument loaded = DotpDeckXmlSerializer.Load(path, new[] { card });
        Equal(2, loaded.PromoUnlocks.Count);
        Equal("SERRA_ANGEL#@2", loaded.PromoUnlocks[0].CardReference);
    }
    finally
    {
        File.Delete(path);
    }
}

static void CheckManagedDxtDecoding()
{
    byte[] redDxt1 =
    {
        0x00, 0xF8, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00
    };
    byte[] redPixels = TdxImageDecoder.DecodeDxt(
        redDxt1, 4, 4, TdxImageDecoder.DxtCompression.Dxt1);
    Equal(64, redPixels.Length);
    Equal((byte)0, redPixels[0]);
    Equal((byte)0, redPixels[1]);
    Equal((byte)255, redPixels[2]);
    Equal((byte)255, redPixels[3]);

    byte[] transparentDxt1 =
    {
        0x00, 0x00, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF
    };
    byte[] transparentPixels = TdxImageDecoder.DecodeDxt(
        transparentDxt1, 4, 4, TdxImageDecoder.DxtCompression.Dxt1);
    Equal((byte)0, transparentPixels[3]);

    byte[] completeTdx = new byte[24];
    completeTdx[0] = 0x00;
    completeTdx[1] = 0x02;
    completeTdx[2] = 0x04;
    completeTdx[4] = 0x04;
    completeTdx[6] = 0x01;
    completeTdx[12] = 0x44;
    completeTdx[13] = 0x58;
    completeTdx[14] = 0x54;
    completeTdx[15] = 0x31;
    redDxt1.CopyTo(completeTdx, 16);
    CardImageData decodedTdx = TdxImageDecoder.Decode(completeTdx);
    Equal(4, decodedTdx.Width);
    Equal(4, decodedTdx.Height);
    Equal((byte)255, decodedTdx.BgraPixels[2]);

    byte[] greenDxt5 =
    {
        0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0xE0, 0x07, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    };
    byte[] greenPixels = TdxImageDecoder.DecodeDxt(
        greenDxt5, 4, 4, TdxImageDecoder.DxtCompression.Dxt5);
    Equal((byte)0, greenPixels[0]);
    Equal((byte)255, greenPixels[1]);
    Equal((byte)0, greenPixels[2]);
    Equal((byte)255, greenPixels[3]);
}

static void CheckCardVisualMetadata()
{
    CardRecord card = new(
        "TEST_CREATURE",
        "Тестовое существо",
        "Test Creature",
        "Creature Beast",
        "M14",
        "Artist",
        "{2}{G}",
        "G",
        "R",
        "3",
        "4");
    CardVisualSpec visual = CardVisualMetadata.FromCard(card);
    Equal("G", visual.FrameId);
    Equal("PTBOX_G", visual.PowerBoxId);
    Equal("EXPANSION_RARE", visual.RarityId);
    True(visual.ShowsPower, "A creature with P/T must show its power box.");
    Equal("MANA_2", visual.ManaImageIds[0]);
    Equal("MANA_G", visual.ManaImageIds[1]);

    CardRecord hybrid = new(
        "HYBRID",
        "Hybrid",
        "Hybrid",
        "Artifact Creature",
        "M14",
        "Artist",
        "{U/B}{U/B}",
        "UB");
    Equal("UB_ARTIFACT_HYBRID", CardVisualMetadata.FromCard(hybrid).FrameId);
}

static void CheckGameWadExport()
{
    string directory = Path.Combine(Path.GetTempPath(), $"deck-builder-wad-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        CardRecord spell = new(
            "TEST_SPELL",
            "Тест",
            "Test",
            "Sorcery",
            "M14",
            "Artist",
            "{1}{G}",
            "G");
        DeckDocument deck = new() { Name = "Test deck" };
        new DeckEditor(deck).Add(spell, DeckSection.MainDeck);
        CardRecord[] catalog = new[] { spell }
            .Concat(Enumerable.Range(1, 4).Select(index => new CardRecord(
                $"FOREST_{index}",
                $"Forest {index}",
                $"Forest {index}",
                "Basic Land Forest",
                "M14",
                "Artist")))
            .ToArray();
        string output = Path.Combine(directory, "Data_Decks_100007_TEST_DECK.wad");
        ModernWadExportOptions options = new(output, 7, "Test deck", "Export check");
        ModernWadExportResult first = ModernWadExporter.Export(deck, catalog, options);
        True(File.Exists(first.WadPath), "The game WAD must be written.");
        True(File.Exists(first.ContentPackEnablerPath), "The content-pack enabler must be written.");
        Equal(100007, first.DeckUid);
        Equal(1000107, first.LandPoolUid);

        ModernWadExportResult second = ModernWadExporter.Export(deck, catalog, options);
        True(second.BackupPath is not null && File.Exists(second.BackupPath),
            "Replacing an existing WAD must create a backup.");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static CardRecord Card(string fileName, string name, string type, string expansion, string artist, string? imageId = null) =>
    new(fileName, name, name, type, expansion, artist, imageId: imageId);

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }
}

static void True(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void Throws<TException>(Action action) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}
