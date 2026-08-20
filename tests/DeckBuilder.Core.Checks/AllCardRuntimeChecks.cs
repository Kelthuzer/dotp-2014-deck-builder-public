using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using DeckBuilder.GameData;
using Gibbed.Duels.FileFormats;

internal static class AllCardRuntimeChecks
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        Run();
        Console.WriteLine("PASS: complete merged shared runtime");
    }

    private static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dotp-all-card-runtime-{Guid.NewGuid():N}");
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

            string card = AddPayload("CARDS\\ROOT.XML", """
                <CARD_V2>
                  <FILENAME text="ROOT" />
                  <ARTID value="10001" />
                  <ABILITY function="USED_FUNCTION" />
                </CARD_V2>
                """);
            string art = AddBinaryPayload("ART_ASSETS\\ILLUSTRATIONS\\10001.TDX", [1, 2, 3, 4]);

            AddPayload("FUNCTIONS\\USED.LOL", "function USED_FUNCTION() end");
            AddPayload("FUNCTIONS\\DYNAMIC_ONLY.LOL", "DYNAMIC_TABLE = { 1, 2, 3 }");
            AddPayload("FUNCTIONS\\_List Functions.ahk", "; editor helper, not game runtime");
            AddPayload("SPECS\\CREATURE_TYPES.TXT", "Angel=1\nConstruct=2\n");
            AddPayload("SPECS\\DYNAMIC_ONLY.TXT", "dynamic=true\n");
            AddPayload("TEXT_PERMANENT\\CREATURE_TYPE_TEXT_TEST.XML", "<Workbook />");
            AddPayload("TEXT_PERMANENT\\DYNAMIC_ONLY.XML", "<Workbook />");
            AddPayload("TEXT_PERMANENT\\.idea\\workspace.xml", "<project />");
            AddPayload("TEXT_PERMANENT\\TEXT_PERMANENT.iml", "<module />");

            // These are deliberately not referenced by ROOT. A complete merged runtime must still
            // contain them, because dynamic scripts can reach assets that static dependency analysis
            // cannot prove in advance.
            AddBinaryPayload("ART_ASSETS\\TEXTURES\\EFFECTS\\UNREFERENCED_EFFECT.TDX", [5, 6]);
            AddBinaryPayload("SOUNDS\\UNREFERENCED_SOUND.BNK", [7, 8, 9]);
            AddPayload("AI_PERSONALITIES\\D14_SISTERS.XML", "<AI_PERSONALITY />");

            // Deck/card payloads remain deck-specific and must not be merged into the shared runtime.
            AddPayload("DECKS\\SHOULD_NOT_COPY.XML", "<DECK />");
            AddPayload("UNLOCKS\\SHOULD_NOT_COPY.XML", "<UNLOCKS />");

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
                    90,
                    0x202,
                    0,
                    "header.xml",
                    string.Empty,
                    manifestFiles)]);
            Directory.CreateDirectory(package);
            File.WriteAllText(
                Path.Combine(package, GameVersionPackageService.ManifestFileName),
                JsonSerializer.Serialize(manifest));

            WorkspaceContentVariant variant = new(
                "selection-ROOT",
                "CARDS\\ROOT.XML",
                "TEST_VERSION",
                wadName,
                90,
                Hash(card),
                card,
                true,
                true,
                "ROOT",
                "ROOT",
                string.Empty,
                "Artifact",
                string.Empty,
                string.Empty,
                "TST",
                string.Empty,
                "10001",
                art,
                Hash(art),
                "ART_ASSETS\\ILLUSTRATIONS\\10001.TDX",
                "art-10001");
            WorkspaceContentVariantScanResult scan = new(
                UnpackedContentKind.Cards,
                1,
                1,
                1,
                0,
                Array.Empty<WorkspaceContentVariantConflict>(),
                [variant]);

            string output = Path.Combine(root, WorkspaceSharedRuntimeContract.WadFileName);
            WorkspaceAllCardRuntimeBuildResult result = new WorkspaceAllCardRuntimeBuilder().Build(
                output,
                root,
                scan,
                order: 40,
                cancellationToken: default);

            HashSet<string> paths = WadPaths(output);
            ContainsSuffix(paths, "\\DATA_ALL_PLATFORMS\\FUNCTIONS\\USED.LOL");
            ContainsSuffix(paths, "\\DATA_ALL_PLATFORMS\\FUNCTIONS\\DYNAMIC_ONLY.LOL");
            ContainsSuffix(paths, "\\DATA_ALL_PLATFORMS\\SPECS\\CREATURE_TYPES.TXT");
            ContainsSuffix(paths, "\\DATA_ALL_PLATFORMS\\SPECS\\DYNAMIC_ONLY.TXT");
            ContainsSuffix(paths, "\\DATA_ALL_PLATFORMS\\TEXT_PERMANENT\\CREATURE_TYPE_TEXT_TEST.XML");
            ContainsSuffix(paths, "\\DATA_ALL_PLATFORMS\\TEXT_PERMANENT\\DYNAMIC_ONLY.XML");
            ContainsSuffix(paths, "\\DATA_ALL_PLATFORMS\\ART_ASSETS\\TEXTURES\\EFFECTS\\UNREFERENCED_EFFECT.TDX");
            ContainsSuffix(paths, "\\DATA_ALL_PLATFORMS\\SOUNDS\\UNREFERENCED_SOUND.BNK");
            ContainsSuffix(paths, "\\DATA_ALL_PLATFORMS\\AI_PERSONALITIES\\D14_SISTERS.XML");

            True(!paths.Any(path => path.Contains("\\CARDS\\ROOT.XML", StringComparison.OrdinalIgnoreCase)),
                "Shared runtime WAD must not contain normal CARD_V2 payloads.");
            True(!paths.Any(path => path.Contains("\\ART_ASSETS\\ILLUSTRATIONS\\10001.TDX", StringComparison.OrdinalIgnoreCase)),
                "Shared runtime WAD must not contain normal card illustrations.");
            True(!paths.Any(path => path.Contains("\\DECKS\\SHOULD_NOT_COPY.XML", StringComparison.OrdinalIgnoreCase)),
                "Shared runtime WAD must not contain deck definitions.");
            True(!paths.Any(path => path.Contains("\\UNLOCKS\\SHOULD_NOT_COPY.XML", StringComparison.OrdinalIgnoreCase)),
                "Shared runtime WAD must not contain unlock definitions.");
            True(!paths.Any(path => path.EndsWith("_List Functions.ahk", StringComparison.OrdinalIgnoreCase)),
                "Editor helper scripts must not be copied into the shared runtime WAD.");
            True(!paths.Any(path => path.Contains("\\.idea\\", StringComparison.OrdinalIgnoreCase)),
                "IDE metadata must not be copied into the shared runtime WAD.");
            True(!paths.Any(path => path.EndsWith(".iml", StringComparison.OrdinalIgnoreCase)),
                "IDE module files must not be copied into the shared runtime WAD.");

            string runtimeManifest = File.ReadAllText(result.ManifestPath);
            True(runtimeManifest.Contains("\"formatVersion\": 4", StringComparison.Ordinal),
                "Complete merged runtime manifest must use format version 4.");
            True(runtimeManifest.Contains("\"coverageMode\": \"all-effective-runtime-v1\"", StringComparison.Ordinal),
                "Complete merged runtime manifest must declare full coverage mode.");
            True(runtimeManifest.Contains("\"requestedOrder\": 40", StringComparison.Ordinal),
                "Shared runtime manifest must preserve the requested order floor.");
            True(runtimeManifest.Contains("\"sourceMaxOrder\": 90", StringComparison.Ordinal),
                "Shared runtime manifest must record the highest source WAD order.");
            True(runtimeManifest.Contains("\"order\": 91", StringComparison.Ordinal),
                "Shared runtime must load after every source WAD instead of using a fixed low order.");
            True(runtimeManifest.Contains("workspaceRuntimeFingerprint", StringComparison.Ordinal),
                "Merged runtime manifest must carry a workspace fingerprint.");
            True(runtimeManifest.Contains("wadSha256", StringComparison.Ordinal),
                "Merged runtime manifest must carry the built WAD hash.");

            True(result.RuntimeResourceCounts.TryGetValue("FUNCTIONS", out int functions) && functions == 2,
                "The full FUNCTIONS tree must be included.");
            True(result.RuntimeResourceCounts.TryGetValue("SPECS", out int specs) && specs == 2,
                "The full SPECS tree must be included.");
            True(result.RuntimeResourceCounts.TryGetValue("TEXT_PERMANENT", out int text) && text == 2,
                "The full TEXT_PERMANENT game-runtime tree must be included while editor metadata is excluded.");
            True(result.RuntimeResourceCounts.TryGetValue("SOUNDS", out int sounds) && sounds == 1,
                "Unreferenced shared sound resources must be included by the full merged runtime.");
            True(result.RuntimeResourceCounts.TryGetValue("AI_PERSONALITIES", out int personalities) && personalities == 1,
                "AI personalities must be included by the full merged runtime.");

            WorkspaceSharedRuntimeInspection inspection = WorkspaceSharedRuntimeContract.Inspect(
                output,
                root,
                scan,
                default);
            True(inspection.IsUsable,
                $"Fresh full merged runtime must pass the shared-runtime contract: {inspection.Reason}");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

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
}
