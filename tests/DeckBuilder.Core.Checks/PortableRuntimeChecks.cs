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

            string rootArt = AddBinaryPayload("ART_ASSETS\\ILLUSTRATIONS\\10001.TDX", [1, 2, 3, 4]);
            string helperArt = AddBinaryPayload("ART_ASSETS\\ILLUSTRATIONS\\10002.TDX", [5, 6, 7, 8]);

            AddPayload("FUNCTIONS\\BRIDGE.LOL", """
                function CARD_BRIDGE()
                    local generated_card = 88888
                    local effect_texture = CUSTOM_EFFECT_TEXTURE
                end
                """);
            AddPayload("SPECS\\CREATURE_TYPES.TXT", "Angel=1\nConstruct=2\n");
            AddPayload("TEXT_PERMANENT\\CREATURE_TYPE_TEXT_TEST.XML", "<Workbook />");
            AddBinaryPayload("ART_ASSETS\\TEXTURES\\MANA\\CUSTOM_MANA.TDX", [9, 10]);
            AddBinaryPayload("ART_ASSETS\\TEXTURES\\CARD_FRAMES\\CUSTOM_FRAME.TDX", [11, 12]);
            AddBinaryPayload("ART_ASSETS\\FRONTEND\\CUSTOM_CHOICE.TDX", [13, 14]);
            AddBinaryPayload("ART_ASSETS\\TEXTURES\\EFFECTS\\CUSTOM_EFFECT_TEXTURE.TDX", [15, 16]);

            // These must never be imported merely because the source workspace contains them.
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
                Variant("HELPER_TOKEN", helperCard, "10002", helperArt)
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
            WorkspaceSelectedCardsBuildResult result = new WorkspaceSelectedCardsBuilder()
                .BuildAsync(output, ["ROOT_CARD"], scan, null, root)
                .GetAwaiter()
                .GetResult();

            Equal(2, result.CardCount, "Runtime MULTIVERSEID reference must pull HELPER_TOKEN CARD_V2.");
            True(result.Warnings.Count == 0, $"Unexpected portable-runtime warning: {string.Join(" | ", result.Warnings)}");

            HashSet<string> paths = WadPaths(output);
            ContainsSuffix(paths, "\\DATA_ALL_PLATFORMS\\CARDS\\ROOT_CARD.XML");
            ContainsSuffix(paths, "\\DATA_ALL_PLATFORMS\\CARDS\\HELPER_TOKEN.XML");
            ContainsSuffix(paths, "\\DATA_ALL_PLATFORMS\\FUNCTIONS\\BRIDGE.LOL");
            ContainsSuffix(paths, "\\DATA_ALL_PLATFORMS\\SPECS\\CREATURE_TYPES.TXT");
            ContainsSuffix(paths, "\\DATA_ALL_PLATFORMS\\TEXT_PERMANENT\\CREATURE_TYPE_TEXT_TEST.XML");
            ContainsSuffix(paths, "\\DATA_ALL_PLATFORMS\\ART_ASSETS\\TEXTURES\\MANA\\CUSTOM_MANA.TDX");
            ContainsSuffix(paths, "\\DATA_ALL_PLATFORMS\\ART_ASSETS\\TEXTURES\\CARD_FRAMES\\CUSTOM_FRAME.TDX");
            ContainsSuffix(paths, "\\DATA_ALL_PLATFORMS\\ART_ASSETS\\FRONTEND\\CUSTOM_CHOICE.TDX");
            ContainsSuffix(paths, "\\DATA_ALL_PLATFORMS\\ART_ASSETS\\TEXTURES\\EFFECTS\\CUSTOM_EFFECT_TEXTURE.TDX");

            True(!paths.Any(path => path.Contains("\\DECKS\\FOREIGN_DECK.XML", StringComparison.OrdinalIgnoreCase)),
                "Portable card runtime must not import foreign decks.");
            True(!paths.Any(path => path.Contains("\\UNLOCKS\\FOREIGN_UNLOCK.XML", StringComparison.OrdinalIgnoreCase)),
                "Portable card runtime must not import foreign unlocks.");
            True(!paths.Any(path => path.Contains("\\AI_PERSONALITIES\\FOREIGN_AI.XML", StringComparison.OrdinalIgnoreCase)),
                "Portable card runtime must not import foreign AI personalities.");

            string provenance = File.ReadAllText(result.SourcesPath);
            True(provenance.Contains("runtimeResourceCount", StringComparison.Ordinal),
                "Portable runtime provenance count must be written.");
            True(provenance.Contains("runtimeResourceCounts", StringComparison.Ordinal),
                "Portable runtime provenance breakdown must be written.");
            True(provenance.Contains("HELPER_TOKEN", StringComparison.Ordinal),
                "Runtime-discovered MULTIVERSEID dependency must be recorded through its canonical card closure.");
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
