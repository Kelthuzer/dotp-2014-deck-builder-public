using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using DeckBuilder.GameData;

internal static class NativeXmasRuntimeChecks
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        Run();
        Console.WriteLine("PASS: native XMAS runtime packaging mode");
    }

    private static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dotp-native-xmas-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string game = Path.Combine(root, "Magic 2014");
            string myDecks = Path.Combine(game, "MyDecks");
            string nativeRuntime = Path.Combine(game, "DATA_DLC_DECK_BUILDER_CUSTOM");
            string nativeData = Path.Combine(nativeRuntime, "DATA_ALL_PLATFORMS");
            Directory.CreateDirectory(myDecks);
            Directory.CreateDirectory(nativeData);
            File.WriteAllBytes(Path.Combine(game, "DotP_D14.exe"), [0x4D, 0x5A]);
            File.WriteAllText(Path.Combine(nativeRuntime, "HEADER.XML"), "<WAD order='99' />");

            string workspace = Path.Combine(root, "Workspace");
            string package = Path.Combine(workspace, "Magic 2014 XMAS");
            string wadName = "DATA_DLC_TEST_XMAS.wad";
            string wadDirectory = Path.Combine(package, "wads", wadName);
            Directory.CreateDirectory(wadDirectory);

            List<DotpWadFileManifest> files = new();
            int storageId = 0;
            string AddPayload(string relativePath, string content)
            {
                string storageRelative = $"payload/{storageId++:D3}.bin";
                string storage = Path.Combine(wadDirectory, storageRelative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(storage)!);
                File.WriteAllText(storage, content);
                files.Add(new DotpWadFileManifest(
                    $"DATA_DLC_TEST_XMAS\\DATA_ALL_PLATFORMS\\{relativePath}",
                    storageRelative,
                    new FileInfo(storage).Length,
                    Hash(storage),
                    0));
                return storage;
            }

            string card = AddPayload("CARDS\\HORN_TEST.XML", "<CARD_V2><FILENAME text='HORN_TEST'/><ABILITY function='CW_Tokens'/></CARD_V2>");
            AddPayload("FUNCTIONS\\CW_TOKENS.LOL", "function CW_Tokens(Name, Count) end");
            AddPayload("SPECS\\CREATURE_TYPES.TXT", "Human=1");
            AddPayload("TEXT_PERMANENT\\CW_SUBTYPES.XML", "<Workbook />");

            DotpVersionPackageManifest manifest = new(
                GameVersionPackageService.CurrentFormatVersion,
                "Magic 2014 XMAS",
                DateTime.UtcNow,
                game,
                [new DotpWadPackageManifest(
                    wadName,
                    0,
                    string.Empty,
                    true,
                    99,
                    0x202,
                    0,
                    "header.xml",
                    string.Empty,
                    files)]);
            Directory.CreateDirectory(package);
            File.WriteAllText(
                Path.Combine(package, GameVersionPackageService.ManifestFileName),
                JsonSerializer.Serialize(manifest));

            WorkspaceContentVariant variant = new(
                "selection-HORN_TEST",
                "CARDS\\HORN_TEST.XML",
                "Magic 2014 XMAS",
                wadName,
                99,
                Hash(card),
                card,
                true,
                true,
                "HORN_TEST",
                "HORN_TEST",
                string.Empty,
                "Artifact",
                string.Empty,
                string.Empty,
                "XMS",
                string.Empty,
                string.Empty,
                null,
                null,
                null,
                null);
            WorkspaceContentVariantScanResult scan = new(
                UnpackedContentKind.Cards,
                1,
                1,
                1,
                0,
                Array.Empty<WorkspaceContentVariantConflict>(),
                [variant]);

            string generatedRuntime = Path.Combine(myDecks, WorkspaceSharedRuntimeContract.WadFileName);
            WorkspaceSharedRuntimeInspection inspection = WorkspaceSharedRuntimeContract.Inspect(
                generatedRuntime,
                workspace,
                scan,
                default);

            True(inspection.IsUsable, $"Native XMAS runtime should be accepted: {inspection.Reason}");
            Equal(Path.GetFullPath(nativeRuntime), inspection.Runtime!.WadPath);
            Equal(Path.GetFullPath(Path.Combine(nativeRuntime, "HEADER.XML")), inspection.Runtime.ManifestPath);
            Equal(99, inspection.Runtime.Order);
            True(inspection.Runtime.Resources.Contains("FUNCTIONS\\CW_TOKENS.LOL"),
                "Native mode must cover workspace runtime resources without generating Data_DLC_8000.");
            True(!File.Exists(generatedRuntime),
                "Inspecting a native XMAS install must not create Data_DLC_8000_DeckBuilder_Runtime.wad.");

            WorkspaceContentVariantScanResult mixedScan = new(
                UnpackedContentKind.Cards,
                2,
                1,
                1,
                0,
                Array.Empty<WorkspaceContentVariantConflict>(),
                [variant]);
            WorkspaceSharedRuntimeInspection mixedInspection = WorkspaceSharedRuntimeContract.Inspect(
                generatedRuntime,
                workspace,
                mixedScan,
                default);
            True(!mixedInspection.IsUsable,
                "A mixed multi-version workspace must not silently reuse the native XMAS runtime.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static string Hash(string path)
    {
        using FileStream input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }
}
