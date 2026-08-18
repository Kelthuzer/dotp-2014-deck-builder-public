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
        Console.WriteLine("PASS: shared all-card runtime completeness");
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
            AddPayload("SPECS\\CREATURE_TYPES.TXT", "Angel=1\nConstruct=2\n");
            AddPayload("SPECS\\DYNAMIC_ONLY.TXT", "dynamic=true\n");
            AddPayload("TEXT_PERMANENT\\CREATURE_TYPE_TEXT_TEST.XML", "<Workbook />");
            AddPayload("TEXT_PERMANENT\\DYNAMIC_ONLY.XML", "<Workbook />");
            AddBinaryPayload("ART_ASSETS\\TEXTURES\\EFFECTS\\USED_EFFECT.TDX", [5, 6]);

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

            WorkspaceContentVariant variant = new(
                "selection-ROOT",
                "CARDS\\ROOT.XML",
                "TEST_VERSION",
                wadName,
                10,
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

            string output = Path.Combine(root, "Data_DLC_8000_DeckBuilder_Runtime.wad");
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

            True(!paths.Any(path => path.Contains("\\CARDS\\ROOT.XML", StringComparison.OrdinalIgnoreCase)),
                "Shared runtime WAD must not contain CARD_V2 payloads.");
            True(!paths.Any(path => path.Contains("\\ART_ASSETS\\ILLUSTRATIONS\\10001.TDX", StringComparison.OrdinalIgnoreCase)),
                "Shared runtime WAD must not contain normal card illustrations.");

            string runtimeManifest = File.ReadAllText(result.ManifestPath);
            True(runtimeManifest.Contains("\"formatVersion\": 2", StringComparison.Ordinal),
                "Shared runtime manifest must use format version 2.");
            True(runtimeManifest.Contains("TEXT_PERMANENT", StringComparison.Ordinal),
                "Shared runtime manifest must record the complete permanent-text tree.");
            True(result.RuntimeResourceCounts.TryGetValue("FUNCTIONS", out int functions) && functions == 2,
                "The full FUNCTIONS tree must be included, not only statically reachable functions.");
            True(result.RuntimeResourceCounts.TryGetValue("SPECS", out int specs) && specs == 2,
                "The full SPECS tree must be included.");
            True(result.RuntimeResourceCounts.TryGetValue("TEXT_PERMANENT", out int text) && text == 2,
                "The full TEXT_PERMANENT tree must be included.");
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
