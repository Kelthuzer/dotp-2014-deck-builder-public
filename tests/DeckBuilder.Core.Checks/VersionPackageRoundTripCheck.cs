using System.Runtime.CompilerServices;
using DeckBuilder.Core.Models;
using DeckBuilder.Core.Services;
using DeckBuilder.GameData;

internal static class VersionPackageRoundTripCheck
{
    [ModuleInitializer]
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dotp-version-package-check-{Guid.NewGuid():N}");
        string game = Path.Combine(root, "game");
        string workspace = Path.Combine(root, "workspace");
        string builds = Path.Combine(root, "builds");
        Directory.CreateDirectory(game);
        try
        {
            CardRecord spell = new(
                "TEST_SPELL",
                "Test Spell",
                "Test Spell",
                "Sorcery",
                "TEST",
                "Artist",
                "{1}{G}",
                "G");
            CardRecord[] catalog = new[] { spell }
                .Concat(Enumerable.Range(1, 4).Select(index => new CardRecord(
                    $"FOREST_{index}",
                    $"Forest {index}",
                    $"Forest {index}",
                    "Basic Land Forest",
                    "TEST",
                    "Artist")))
                .ToArray();
            DeckDocument deck = new() { Name = "Package round trip" };
            new DeckEditor(deck).Add(spell, DeckSection.MainDeck);
            string wadPath = Path.Combine(game, "Data_Decks_100008_PACKAGE_TEST.wad");
            ModernWadExporter.Export(
                deck,
                catalog,
                new ModernWadExportOptions(wadPath, 8, "Package round trip", "Package test"));

            GameVersionPackageService service = new();
            VersionPackageExtractResult extracted = service.ExtractAsync(
                    new VersionPackageExtractOptions(game, workspace, "ci-version"))
                .GetAwaiter()
                .GetResult();
            if (extracted.WadCount < 2 || !File.Exists(extracted.ManifestPath))
            {
                throw new InvalidOperationException("Version package extraction did not preserve all generated WADs.");
            }

            DotpVersionPackageManifest manifest = service.ReadManifest(extracted.PackageDirectory);
            DotpWadFileManifest editable = manifest.Wads
                .SelectMany(wad => wad.Files.Select(file => (Wad: wad, File: file)))
                .First(item => item.File.StoragePath.EndsWith(".XML", StringComparison.OrdinalIgnoreCase))
                .File;
            DotpWadPackageManifest owner = manifest.Wads.First(wad => wad.Files.Contains(editable));
            string editablePath = Path.Combine(
                extracted.PackageDirectory,
                "wads",
                owner.Name,
                editable.StoragePath.Replace('/', Path.DirectorySeparatorChar));
            File.AppendAllText(editablePath, Environment.NewLine);

            VersionPackageBuildResult built = service.BuildAsync(
                    new VersionPackageBuildOptions(extracted.PackageDirectory, builds))
                .GetAwaiter()
                .GetResult();
            if (built.WadCount != extracted.WadCount || built.ModifiedFiles < 1)
            {
                throw new InvalidOperationException("Version package rebuild did not detect and rebuild the modified payload.");
            }

            foreach (DotpWadPackageManifest wad in manifest.Wads)
            {
                if (!File.Exists(Path.Combine(built.OutputDirectory, wad.Name)))
                {
                    throw new InvalidOperationException($"Rebuilt WAD is missing: {wad.Name}");
                }
            }

            Console.WriteLine("PASS: WAD version package extract/edit/rebuild verification");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
