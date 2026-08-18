using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using DeckBuilder.GameData;

internal static class PortableRuntimeSelectionChecks
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        Run();
        Console.WriteLine("PASS: portable runtime selects shared data narrowly");
    }

    private static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dotp-runtime-selection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string package = Path.Combine(root, "TEST_VERSION");
            string wadName = "DATA_DLC_TEST_RUNTIME.wad";
            string wadDirectory = Path.Combine(package, "wads", wadName);
            Directory.CreateDirectory(wadDirectory);

            List<DotpWadFileManifest> files = new();
            int id = 0;

            void Add(string relativePath, string content)
            {
                string storageRelative = $"payload/{id++:D3}.bin";
                string storage = Path.Combine(wadDirectory, storageRelative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(storage)!);
                File.WriteAllText(storage, content);
                files.Add(new DotpWadFileManifest(
                    $"DATA_DLC_TEST_RUNTIME\\DATA_ALL_PLATFORMS\\{relativePath}",
                    storageRelative,
                    new FileInfo(storage).Length,
                    Hash(storage),
                    0));
            }

            Add("SPECS\\CREATURE_TYPES.TXT", "Angel=1\nConstruct=2\n");
            Add("SPECS\\UNRELATED_BIG_TABLE.TXT", new string('x', 4096));
            Add("TEXT_PERMANENT\\CREATURE_TYPE_TEXT_TEST.XML", "<Workbook />");
            Add("TEXT_PERMANENT\\UNRELATED_TEXT_001.XML", "<Workbook />");
            Add("TEXT_PERMANENT\\UNRELATED_TEXT_002.XML", "<Workbook />");

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
                    files)]);
            Directory.CreateDirectory(package);
            File.WriteAllText(
                Path.Combine(package, GameVersionPackageService.ManifestFileName),
                JsonSerializer.Serialize(manifest));

            string card = Path.Combine(root, "ROOT_CARD.XML");
            File.WriteAllText(card, "<CARD_V2><FILENAME text=\"ROOT_CARD\" /></CARD_V2>");

            List<string> warnings = new();
            HashSet<string> warningKeys = new(StringComparer.OrdinalIgnoreCase);
            WorkspacePortableRuntimeIndex index = WorkspacePortableRuntimeIndex.Load(
                root,
                warnings,
                warningKeys,
                default);
            WorkspacePortableRuntimeResolution resolution = index.Resolve(
                [card],
                Array.Empty<string>(),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                warnings,
                warningKeys,
                default);

            True(resolution.ResourcePaths.Any(path => path.Equals(
                    "SPECS\\CREATURE_TYPES.TXT",
                    StringComparison.OrdinalIgnoreCase)),
                "Creature type spec must remain in the implicit compatibility bundle.");
            True(resolution.ResourcePaths.Any(path => path.Equals(
                    "TEXT_PERMANENT\\CREATURE_TYPE_TEXT_TEST.XML",
                    StringComparison.OrdinalIgnoreCase)),
                "Creature type permanent text must remain in the implicit compatibility bundle.");
            True(!resolution.ResourcePaths.Any(path => path.Contains(
                    "UNRELATED_BIG_TABLE",
                    StringComparison.OrdinalIgnoreCase)),
                "Unrelated SPECS files must not be copied into every portable deck.");
            True(!resolution.ResourcePaths.Any(path => path.Contains(
                    "UNRELATED_TEXT",
                    StringComparison.OrdinalIgnoreCase)),
                "Unrelated TEXT_PERMANENT files must not be copied into every portable deck.");
            Equal(2, resolution.ResourceCount,
                "The synthetic workspace should contribute only the two proven implicit shared resources.");
            Equal(0, warnings.Count, "The selective runtime check produced unexpected warnings.");
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

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message} Expected {expected}; actual {actual}.");
    }
}
