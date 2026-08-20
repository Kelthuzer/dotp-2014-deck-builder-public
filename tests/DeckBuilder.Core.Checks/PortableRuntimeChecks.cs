using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using DeckBuilder.GameData;
using Gibbed.Duels.FileFormats;

internal static class PortableRuntimeChecks
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        Run();
        Console.WriteLine("PASS: portable runtime dependency closure");
    }

    public static void Run()
    {
        WorkspaceCardDependencyScanResult registrationScan = WorkspaceCardDependencyResolver.Scan(
            "<CARD_V2><TOKEN_REGISTRATION reservation=\"1\" type=\"TOKEN_HUMAN_SOLDIER_C_1_1_W_CW_8\" /></CARD_V2>",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            "ROOT_CARD");
        True(
            registrationScan.MissingTokenReferences.Contains(
                "TOKEN_HUMAN_SOLDIER_C_1_1_W_CW_8",
                StringComparer.OrdinalIgnoreCase),
            "Explicit TOKEN_REGISTRATION entries must not be suppressed as generic dynamic CW token names.");

        string root = Path.Combine(Path.GetTempPath(), $"dotp-portable-runtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string package = Path.Combine(root, "TEST_VERSION");
            string wadName = "DATA_DLC_TEST_RUNTIME.wad";
            string wadDirectory = Path.Combine(package, "wads", wadName);
            Directory.CreateDirectory(wadDirectory);

            List<DotpWadFileManifest> manifestFiles = new();
            int storageId = 0;

            string AddPayload(string relativePath, string content)
            {
                // Version-package storage names are deliberately unrelated to archive extensions.
                string storageRelative = $"payload/{storageId++:D3}.bin";
                string storage = Path.Combine(wadDirectory, storageRelative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(storage)!);
                File.WriteAllText(storage, content);
                manifestFiles.Add(new DotpWadFileManifest(
                    $"DATA_DLC_TEST_RUNTIME\\DATA_ALL_PLATFORMS\\{relativePath}",
                    storageRelative,
                    new FileInfo(storage).Length,
                    Hash(storage),
                    0));
                return storage;
            }

            string AddBinaryPayload(string relativePath, byte[] content)
            {
                string storageRelative = $"payload/{storageId++:D3}.bin";
                string storage = Path.Combine(wadDirectory, storageRelative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(storage)!);
                File.WriteAllBytes(storage, content);
                manifestFiles.Add(new DotpWadFileManifest(
                    $"DATA_DLC_TEST_RUNTIME\\DATA_ALL_PLATFORMS\\{relativePath}",
                    storageRelative,
                    content.LongLength,
                    Convert.ToHexString(SHA256.HashData(content)),
                    0));
                return storage;
            }

            string rootCard = AddPayload("CARDS\\ROOT_CARD.XML", """
                <CARD_V2>
                  <FILENAME text="ROOT_CARD" />
                  <ARTID value="10001" />
                  <MULTIVERSEID value="77777" />
                  <ABILITY function="CARD_BRIDGE" />
                  <PORTABLE_TEST mana="CUSTOM_MANA" frame="CUSTOM_FRAME" choice="CUSTOM_CHOICE" state="SHARED_STATE" />
                </CARD_V2>
                """);
            string helperCard = AddPayload("CARDS\\HELPER_TOKEN.XML", """
                <CARD_V2>
                  <FILENAME text="HELPER_TOKEN" />
                  <ARTID value="10002" />
                  <MULTIVERSEID value="88888" />
                  <TOKEN />
                </CARD_V2>
                """);
            string decoyCard = AddPayload("CARDS\\DECOY_CARD.XML", """
                <CARD_V2>
                  <FILENAME text="DECOY_CARD" />
                  <ARTID value="10003" />
                  <MULTIVERSEID value="99999" />
                </CARD_V2>
                """);

            string rootArt = AddBinaryPayload("ART_ASSETS\\ILLUSTRATIONS\\10001.TDX", [1, 2, 3, 4]);
            string helperArt = AddBinaryPayload("ART_ASSETS\\ILLUSTRATIONS\\10002.TDX", [5, 6, 7, 8]);
            string decoyArt = AddBinaryPayload("ART_ASSETS\\ILLUSTRATIONS\\10003.TDX", [19, 20, 21, 22]);

            AddPayload("FUNCTIONS\\BRIDGE.LOL", """
                function CARD_BRIDGE()
                    local generated_card = HELPER_CARD_ID
                    local effect_texture = CUSTOM_EFFECT_TEXTURE
                end
                """);
            AddPayload("FUNCTIONS\\CONSTANTS.LOL", """
                HELPER_CARD_ID = 88888
                """);
            AddPayload("FUNCTIONS\\UNRELATED_REGISTRY.LOL", """
                SHARED_STATE = 1
                UNRELATED_CARD_ID = 99999
                """);
            AddPayload("SPECS\\CREATURE_TYPES.TXT", "Angel=1\nConstruct=2\n");
            AddPayload("TEXT_PERMANENT\\CREATURE_TYPE_TEXT_TEST.XML", "<Workbook />");
            AddBinaryPayload("ART_ASSETS\\TEXTURES\\MANA\\CUSTOM_MANA.TDX", [9, 10]);
            AddBinaryPayload("ART_ASSETS\\TEXTURES\\CARD_FRAMES\\CUSTOM_FRAME.TDX", [11, 12]);
            AddBinaryPayload("ART_ASSETS\\FRONTEND\\CUSTOM_CHOICE.TDX", [13, 14]);
            AddBinaryPayload("ART_ASSETS\\TEXTURES\\EFFECTS\\CUSTOM_EFFECT_TEXTURE.TDX", [15, 16]);
            AddBinaryPayload("ART_ASSETS\\TEXTURES\\EFFECTS\\UNUSED_EFFECT.TDX", [17, 18]);
            AddBinaryPayload("ART_ASSETS\\TEXTURES\\DECKS\\PORTABLE_DECK.TDX", [23, 24]);

            // Foreign game content must never leak into the portable support WAD.
            AddPayload("DECKS\\FOREIGN_DECK.XML", "<DECK uid=\"999\" />");
            AddPayload("UNLOCKS\\FOREIGN_UNLOCK.XML", "<UNLOCK />");
            AddPayload("AI_PERSONALITIES\\FOREIGN_AI.XML", "<AI_PERSONALITY />");

            DotpVersionPackageManifest manifest = new(
                GameVersionPackageService.CurrentFormatVersion,
                "TEST_VERSION",
                DateTime.UtcNow,
                root,
                [new DotpWadPackageManifest(
                    wadName,
                    0,
                    string.Empty,
                    true,
                    10,
                    0x202,
                    0,
                    "header.xml",
                    string.Empty,
                    manifestFiles)]);
            Directory.CreateDirectory(package);
            File.WriteAllText(
                Path.Combine(package, GameVersionPackageService.ManifestFileName),
                JsonSerializer.Serialize(manifest));

            WorkspaceContentVariant[] variants =
            [
                Variant("ROOT_CARD", rootCard, "10001", rootArt),
                Variant("HELPER_TOKEN", helperCard, "10002", helperArt),
                Variant("DECOY_CARD", decoyCard, "10003", decoyArt)
            ];
            WorkspaceContentVariantScanResult scan = new(
                UnpackedContentKind.Cards,
                1,
                1,
                variants.Length,
                0,
                Array.Empty<WorkspaceContentVariantConflict>(),
                variants);

            string output = Path.Combine(root, "DATA_DLC_9000_TEST_Cards.wad");
            WorkspaceSelectedCardsBuildResult result = new WorkspaceSelectedCardsBuilder().Build(
                output,
                ["ROOT_CARD"],
                scan,
                selections: null,
                workspaceDirectory: root,
                deckBoxImageId: "PORTABLE_DECK",
                deckBoxTexturePath: null,
                runtimeRootIdentifiers: null,
                order: 0,
                cancellationToken: default);

            Equal(2, result.CardCount,
                "Only the reachable helper CARD_V2 should be packaged; unrelated global registries must not expand the card closure.");
            True(result.SharedRuntimeResourceCount > 0,
                "Card mechanics must be resolved against the shared runtime.");
            Equal(1, result.RuntimeResourceCount,
                "Only the explicit deck texture should remain in the per-deck runtime payload.");
            True(File.Exists(result.SharedRuntimeWadPath),
                "Per-deck packaging must ensure the shared runtime WAD exists.");
            True(result.Warnings.Count == 0, $"Unexpected portable-runtime warning: {string.Join(" | ", result.Warnings)}");

            HashSet<string> paths = WadPaths(output);
            ContainsSuffix(paths, "\\DATA_ALL_PLATFORMS\\CARDS\\ROOT_CARD.XML");
            ContainsSuffix(paths, "\\DATA_ALL_PLATFORMS\\CARDS\\HELPER_TOKEN.XML");
            ContainsSuffix(paths, "\\DATA_ALL_PLATFORMS\\ART_ASSETS\\ILLUSTRATIONS\\10001.TDX");
            ContainsSuffix(paths, "\\DATA_ALL_PLATFORMS\\ART_ASSETS\\ILLUSTRATIONS\\10002.TDX");
            ContainsSuffix(paths, "\\DATA_ALL_PLATFORMS\\ART_ASSETS\\TEXTURES\\DECKS\\PORTABLE_DECK.TDX");

            True(!paths.Any(path => path.Contains("\\FUNCTIONS\\", StringComparison.OrdinalIgnoreCase)),
                "Shared FUNCTIONS must not be duplicated into every deck WAD.");
            True(!paths.Any(path => path.Contains("\\SPECS\\", StringComparison.OrdinalIgnoreCase)),
                "Shared SPECS must not be duplicated into every deck WAD.");
            True(!paths.Any(path => path.Contains("\\TEXT_PERMANENT\\", StringComparison.OrdinalIgnoreCase)),
                "Shared permanent text must not be duplicated into every deck WAD.");
            True(!paths.Any(path => path.Contains("CUSTOM_MANA.TDX", StringComparison.OrdinalIgnoreCase)),
                "Card-driven runtime assets belong to the shared runtime WAD.");
            True(!paths.Any(path => path.Contains("CUSTOM_FRAME.TDX", StringComparison.OrdinalIgnoreCase)),
                "Card-driven frame assets belong to the shared runtime WAD.");
            True(!paths.Any(path => path.Contains("CUSTOM_EFFECT_TEXTURE.TDX", StringComparison.OrdinalIgnoreCase)),
                "Card-driven effect assets belong to the shared runtime WAD.");
            True(!paths.Any(path => path.Contains("UNRELATED_REGISTRY.LOL", StringComparison.OrdinalIgnoreCase)),
                "The per-deck WAD must not receive the global function registry.");
            True(!paths.Any(path => path.Contains("DECOY_CARD.XML", StringComparison.OrdinalIgnoreCase)),
                "Cards mentioned only by an unrelated runtime registry must not leak into the portable deck.");
            True(!paths.Any(path => path.Contains("10003.TDX", StringComparison.OrdinalIgnoreCase)),
                "Artwork for an unrelated registry card must not leak into the portable deck.");
            True(!paths.Any(path => path.Contains("UNUSED_EFFECT.TDX", StringComparison.OrdinalIgnoreCase)),
                "Unreferenced heavy textures must not be copied just because they exist in the workspace.");
            True(!paths.Any(path => path.Contains("\\DECKS\\FOREIGN_DECK.XML", StringComparison.OrdinalIgnoreCase)),
                "Portable card payload must not import foreign decks.");
            True(!paths.Any(path => path.Contains("\\UNLOCKS\\FOREIGN_UNLOCK.XML", StringComparison.OrdinalIgnoreCase)),
                "Portable card payload must not import foreign unlocks.");
            True(!paths.Any(path => path.Contains("\\AI_PERSONALITIES\\FOREIGN_AI.XML", StringComparison.OrdinalIgnoreCase)),
                "Portable card payload must not import foreign AI personalities.");

            HashSet<string> sharedPaths = WadPaths(result.SharedRuntimeWadPath);
            ContainsSuffix(sharedPaths, "\\DATA_ALL_PLATFORMS\\FUNCTIONS\\BRIDGE.LOL");
            ContainsSuffix(sharedPaths, "\\DATA_ALL_PLATFORMS\\FUNCTIONS\\CONSTANTS.LOL");
            ContainsSuffix(sharedPaths, "\\DATA_ALL_PLATFORMS\\SPECS\\CREATURE_TYPES.TXT");
            ContainsSuffix(sharedPaths, "\\DATA_ALL_PLATFORMS\\TEXT_PERMANENT\\CREATURE_TYPE_TEXT_TEST.XML");
            ContainsSuffix(sharedPaths, "\\DATA_ALL_PLATFORMS\\ART_ASSETS\\TEXTURES\\MANA\\CUSTOM_MANA.TDX");
            ContainsSuffix(sharedPaths, "\\DATA_ALL_PLATFORMS\\ART_ASSETS\\TEXTURES\\CARD_FRAMES\\CUSTOM_FRAME.TDX");
            ContainsSuffix(sharedPaths, "\\DATA_ALL_PLATFORMS\\ART_ASSETS\\FRONTEND\\CUSTOM_CHOICE.TDX");
            ContainsSuffix(sharedPaths, "\\DATA_ALL_PLATFORMS\\ART_ASSETS\\TEXTURES\\EFFECTS\\CUSTOM_EFFECT_TEXTURE.TDX");
            True(!sharedPaths.Any(path => path.Contains("\\CARDS\\ROOT_CARD.XML", StringComparison.OrdinalIgnoreCase)),
                "The shared runtime must not duplicate CARD_V2 payloads.");
            True(!sharedPaths.Any(path => path.Contains("\\ART_ASSETS\\ILLUSTRATIONS\\10001.TDX", StringComparison.OrdinalIgnoreCase)),
                "The shared runtime must not duplicate normal card illustrations.");

            string provenance = File.ReadAllText(result.SourcesPath);
            True(provenance.Contains("\"formatVersion\": 4", StringComparison.Ordinal),
                "Portable provenance must record the shared-runtime contract format.");
            True(provenance.Contains("\"sharedRuntime\"", StringComparison.Ordinal),
                "Portable provenance must identify the shared runtime used by the deck.");
            True(provenance.Contains("\"resolvedRuntimeResourceCount\"", StringComparison.Ordinal),
                "Portable provenance must record the full resolved runtime closure before deduplication.");
            True(provenance.Contains("\"runtimeResourceCount\": 1", StringComparison.Ordinal),
                "Portable provenance must record only the deck-specific runtime payload copied into the support WAD.");
            True(provenance.Contains("\"order\": 41", StringComparison.Ordinal),
                "The deck support WAD must load after the shared runtime instead of using a fixed order.");
            True(provenance.Contains("HELPER_TOKEN", StringComparison.Ordinal),
                "Runtime-discovered MULTIVERSEID dependency must be recorded through its canonical card closure.");
            True(!provenance.Contains("DECOY_CARD", StringComparison.Ordinal),
                "Unrelated registry cards must not be recorded in portable provenance.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static WorkspaceContentVariant Variant(
        string reference,
        string storagePath,
        string artId,
        string artStoragePath) => new(
        $"selection-{reference}",
        $"CARDS\\{reference}.XML",
        "TEST_VERSION",
        "DATA_DLC_TEST_RUNTIME.wad",
        10,
        Hash(storagePath),
        storagePath,
        true,
        true,
        reference,
        reference,
        string.Empty,
        reference.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) ? "Artifact Creature Construct" : "Artifact",
        string.Empty,
        string.Empty,
        "TST",
        string.Empty,
        artId,
        artStoragePath,
        Hash(artStoragePath),
        $"ART_ASSETS\\ILLUSTRATIONS\\{artId}.TDX",
        $"art-{artId}");

    private static HashSet<string> WadPaths(string path)
    {
        using FileStream input = File.OpenRead(path);
        WadFile wad = new();
        wad.Deserialize(input);
        HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (Wad.DirectoryEntry directory in wad.Directories)
            Collect(directory, directory.Name, result);
        return result;
    }

    private static void Collect(Wad.DirectoryEntry directory, string path, ISet<string> output)
    {
        foreach (Wad.FileEntry file in directory.Files)
            output.Add($"{path}\\{file.Name}");
        foreach (Wad.DirectoryEntry child in directory.Directories)
            Collect(child, $"{path}\\{child.Name}", output);
    }

    private static string Hash(string path)
    {
        using FileStream input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private static void ContainsSuffix(IEnumerable<string> paths, string suffix) =>
        True(paths.Any(path => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)),
            $"Expected WAD resource ending in {suffix}.");

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
