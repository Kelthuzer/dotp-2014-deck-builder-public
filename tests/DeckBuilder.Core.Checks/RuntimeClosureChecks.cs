using System.Runtime.CompilerServices;
using System.Text.Json;
using DeckBuilder.GameData;

internal static class RuntimeClosureChecks
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        EffectiveCwTokenRequirementsIgnoreOverriddenCards();
        PortableRuntimeClosureHasNoArtificialResourceCeiling();
        Console.WriteLine("PASS: effective CW requirements and unlimited runtime closure");
    }

    private static void EffectiveCwTokenRequirementsIgnoreOverriddenCards()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dotp-effective-cw-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string oldCard = Path.Combine(root, "old.xml");
            string currentCard = Path.Combine(root, "current.xml");
            File.WriteAllText(oldCard, "<CARD_V2><FILENAME text=\"SAME_CARD\"/><ABILITY function=\"CW_Tokens('OLD_ONLY')\"/></CARD_V2>");
            File.WriteAllText(currentCard, "<CARD_V2><FILENAME text=\"SAME_CARD\"/><ABILITY function=\"CW_Tokens('CURRENT_ONLY')\"/></CARD_V2>");

            WorkspaceContentVariant oldVariant = Variant(
                "old",
                "OLD",
                "old.wad",
                10,
                oldCard,
                recommended: false);
            WorkspaceContentVariant currentVariant = Variant(
                "current",
                "CURRENT",
                "current.wad",
                20,
                currentCard,
                recommended: true);

            WorkspaceContentVariantScanResult scan = new(
                UnpackedContentKind.Cards,
                2,
                2,
                2,
                0,
                Array.Empty<WorkspaceContentVariantConflict>(),
                [oldVariant, currentVariant]);

            IReadOnlySet<string> keys = WorkspaceRuntimeCompatibility.ScanEffectiveCardSetCwTokenKeys(scan, default);
            True(keys.Contains("CURRENT_ONLY"), "Effective card CW token key was not discovered.");
            True(!keys.Contains("OLD_ONLY"), "Overridden historical card leaked a stale CW token requirement.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void PortableRuntimeClosureHasNoArtificialResourceCeiling()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dotp-runtime-unlimited-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            const int resourceCount = 1100;
            string package = Path.Combine(root, "TEST_VERSION");
            string wadName = "DATA_DLC_RUNTIME_LIMIT_TEST.wad";
            string wadDirectory = Path.Combine(package, "wads", wadName);
            Directory.CreateDirectory(wadDirectory);

            List<DotpWadFileManifest> files = new(resourceCount);
            for (int index = 0; index < resourceCount; index++)
            {
                string storageRelative = $"payload/{index:D4}.txt";
                string storage = Path.Combine(wadDirectory, storageRelative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(storage)!);
                File.WriteAllText(storage, $"type-{index}");
                files.Add(new DotpWadFileManifest(
                    $"DATA_DLC_RUNTIME_LIMIT_TEST\\DATA_ALL_PLATFORMS\\TEXT_PERMANENT\\CREATURE_TYPE_TEXT_LIMIT_{index:D4}.XML",
                    storageRelative,
                    new FileInfo(storage).Length,
                    string.Empty,
                    0));
            }

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
            File.WriteAllText(
                Path.Combine(package, GameVersionPackageService.ManifestFileName),
                JsonSerializer.Serialize(manifest));

            List<string> warnings = new();
            HashSet<string> warningKeys = new(StringComparer.OrdinalIgnoreCase);
            WorkspacePortableRuntimeIndex runtime = WorkspacePortableRuntimeIndex.Load(
                root,
                warnings,
                warningKeys,
                default);

            WorkspacePortableRuntimeResolution resolution = runtime.Resolve(
                Array.Empty<string>(),
                rootIdentifiers: null,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                warnings,
                warningKeys,
                default);

            True(
                resolution.ResourceCount == resourceCount,
                $"Expected all {resourceCount} implicit runtime resources, got {resolution.ResourceCount}.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static WorkspaceContentVariant Variant(
        string selectionKey,
        string package,
        string wad,
        int order,
        string storagePath,
        bool recommended) => new(
            selectionKey,
            "CARDS\\SAME_CARD.XML",
            package,
            wad,
            order,
            string.Empty,
            storagePath,
            recommended,
            true,
            "Same Card",
            "SAME_CARD",
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            null,
            null,
            null,
            null);

    private static void True(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
