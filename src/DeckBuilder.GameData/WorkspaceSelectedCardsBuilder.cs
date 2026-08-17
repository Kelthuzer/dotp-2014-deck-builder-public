using System.Text.Json;
using System.Xml.Linq;

namespace DeckBuilder.GameData;

public sealed record WorkspaceSelectedCardSource(
    string Reference,
    string PackageName,
    string WadName,
    int WadOrder,
    string CardPath,
    string CardSha256,
    string? ArtId,
    string? ArtPath,
    string? ArtSha256);

public sealed record WorkspaceSelectedCardsBuildResult(
    string WadPath,
    string SourcesPath,
    int CardCount,
    int ArtCount,
    UnpackedContentBuildResult BuildResult,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Creates a portable support WAD for the selected deck. The closure contains the selected
/// CARD_V2 definitions, recursively referenced cards/tokens and illustrations, plus the effective
/// CW/RSN runtime and concrete non-card resources reached by those definitions. Card and runtime
/// dependency discovery iterate to a fixpoint because a shared function can itself name a helper
/// CARD_V2 whose XML then introduces another runtime/resource dependency.
/// </summary>
public sealed class WorkspaceSelectedCardsBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly UnpackedContentWadBuilder _wadBuilder = new();

    public async Task<WorkspaceSelectedCardsBuildResult> BuildAsync(
        string outputPath,
        IEnumerable<string> references,
        WorkspaceContentVariantScanResult scan,
        IReadOnlyDictionary<string, string>? selections,
        string? workspaceDirectory = null,
        string? deckBoxImageId = null,
        string? deckBoxTexturePath = null,
        int order = 50,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(references);
        ArgumentNullException.ThrowIfNull(scan);

        string[] rootReferences = references
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        HashSet<string> rootReferenceSet = rootReferences.ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, string> referenceAliases = BuildReferenceAliases(scan.CardVariants);

        string output = Path.GetFullPath(outputPath);
        string outputDirectory = Path.GetDirectoryName(output)
            ?? throw new DirectoryNotFoundException("The support WAD output directory is missing.");
        Directory.CreateDirectory(outputDirectory);

        string staging = Path.Combine(outputDirectory, $".workspace-cards-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            string cardDirectory = Path.Combine(staging, "DATA_ALL_PLATFORMS", "CARDS");
            string artDirectory = Path.Combine(staging, "DATA_ALL_PLATFORMS", "ART_ASSETS", "ILLUSTRATIONS");
            Directory.CreateDirectory(cardDirectory);
            Directory.CreateDirectory(artDirectory);

            List<WorkspaceSelectedCardSource> sources = new();
            List<string> warnings = new();
            HashSet<string> warningKeys = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> copiedArt = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> scheduledReferences = new(StringComparer.OrdinalIgnoreCase);
            Queue<(string Reference, string? Parent)> pending = new();

            foreach (string reference in rootReferences)
            {
                if (scheduledReferences.Add(reference))
                    pending.Enqueue((reference, null));
            }

            WorkspaceSharedRuntimePackResult runtime = WorkspaceSharedRuntimePackResult.Empty;
            bool runtimeNeedsRefresh = true;

            while (pending.Count > 0 || runtimeNeedsRefresh)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (pending.Count == 0)
                {
                    runtime = WorkspaceSharedFunctionPackager.CopyIntoStaging(
                        workspaceDirectory,
                        staging,
                        referenceAliases,
                        warnings,
                        warningKeys,
                        cancellationToken);
                    runtimeNeedsRefresh = false;

                    foreach (string dependency in runtime.CardReferences)
                    {
                        if (scheduledReferences.Add(dependency))
                            pending.Enqueue((dependency, "portable shared runtime"));
                    }

                    continue;
                }

                (string reference, string? parent) = pending.Dequeue();
                WorkspaceContentVariant[] variants = scan.CardVariants
                    .Where(item => item.Reference.Equals(reference, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (variants.Length == 0)
                {
                    string warning = parent is null
                        ? $"No extracted CARD_V2 definition was found for {reference}; the deck will still keep the exact reference."
                        : $"Referenced CARD_V2 dependency {reference} required by {parent} was not found in the extracted workspace.";
                    if (warningKeys.Add(warning))
                        warnings.Add(warning);
                    continue;
                }

                WorkspaceContentVariant selected = ResolveVariant(reference, variants, scan.Conflicts, selections);
                string canonicalReference = string.IsNullOrWhiteSpace(selected.Reference)
                    ? reference
                    : selected.Reference.Trim();
                string extension = Path.GetExtension(selected.RelativePath);
                string cardFileName = SanitizeFileName(canonicalReference) + (string.IsNullOrWhiteSpace(extension) ? ".XML" : extension);
                string targetCard = Path.Combine(cardDirectory, cardFileName);
                File.Copy(selected.StoragePath, targetCard, overwrite: true);
                runtimeNeedsRefresh = true;

                if (!string.IsNullOrWhiteSpace(selected.ArtStoragePath)
                    && File.Exists(selected.ArtStoragePath)
                    && !string.IsNullOrWhiteSpace(selected.ArtId))
                {
                    string artFileName = SanitizeFileName(selected.ArtId) + ".TDX";
                    if (copiedArt.Add(artFileName))
                        File.Copy(selected.ArtStoragePath, Path.Combine(artDirectory, artFileName), overwrite: true);
                }

                sources.Add(new WorkspaceSelectedCardSource(
                    canonicalReference,
                    selected.PackageName,
                    selected.WadName,
                    selected.WadOrder,
                    selected.StoragePath,
                    selected.Sha256,
                    string.IsNullOrWhiteSpace(selected.ArtId) ? null : selected.ArtId,
                    selected.ArtStoragePath,
                    selected.ArtSha256));

                string rawXml;
                try
                {
                    rawXml = File.ReadAllText(selected.StoragePath);
                }
                catch (Exception exception)
                {
                    string warning = $"Could not inspect CARD_V2 dependencies for {canonicalReference}: {exception.Message}";
                    if (warningKeys.Add(warning))
                        warnings.Add(warning);
                    continue;
                }

                WorkspaceCardDependencyScanResult dependencyScan = WorkspaceCardDependencyResolver.Scan(
                    rawXml,
                    referenceAliases,
                    canonicalReference);

                foreach (string missingToken in dependencyScan.MissingTokenReferences)
                {
                    string warning = $"Token dependency {missingToken} referenced by {canonicalReference} was not found in the extracted workspace.";
                    if (warningKeys.Add(warning))
                        warnings.Add(warning);
                }

                foreach (string dependency in dependencyScan.References)
                {
                    if (scheduledReferences.Add(dependency))
                        pending.Enqueue((dependency, canonicalReference));
                }
            }

            int packagedRootCards = sources.Count(source => rootReferenceSet.Contains(source.Reference));
            if (packagedRootCards == 0)
                throw new InvalidDataException("None of the deck's cards have extracted definitions that can be packaged.");

            if (!string.IsNullOrWhiteSpace(deckBoxImageId))
            {
                string deckTexture;
                if (!string.IsNullOrWhiteSpace(deckBoxTexturePath))
                {
                    deckTexture = Path.GetFullPath(deckBoxTexturePath);
                    if (!File.Exists(deckTexture))
                        throw new FileNotFoundException("The generated custom deck-cover TDX was not found.", deckTexture);
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(workspaceDirectory) || !Directory.Exists(workspaceDirectory))
                        throw new DirectoryNotFoundException(
                            $"Cannot package deck cover '{deckBoxImageId}' because the extracted workspace is unavailable.");

                    deckTexture = FindDeckTexture(workspaceDirectory, deckBoxImageId)
                        ?? throw new FileNotFoundException(
                            $"Deck cover '{deckBoxImageId}' was selected, but its TDX was not found under ART_ASSETS\\TEXTURES in the extracted workspace.");
                }

                string deckTextureDirectory = Path.Combine(
                    staging,
                    "DATA_ALL_PLATFORMS",
                    "ART_ASSETS",
                    "TEXTURES",
                    "DECKS");
                Directory.CreateDirectory(deckTextureDirectory);
                File.Copy(
                    deckTexture,
                    Path.Combine(deckTextureDirectory, SanitizeFileName(deckBoxImageId) + ".TDX"),
                    overwrite: true);
            }

            UnpackedContentBuildResult result = await _wadBuilder.BuildAsync(
                new UnpackedContentBuildOptions(
                    staging,
                    output,
                    UnpackedContentKind.PortableCards,
                    order),
                cancellationToken);

            int sharedFunctionCount = runtime.ResourceCounts.TryGetValue("FUNCTIONS", out int functionCount)
                ? functionCount
                : 0;

            string sourcesPath = output + ".sources.json";
            File.WriteAllText(sourcesPath, JsonSerializer.Serialize(new
            {
                formatVersion = 2,
                createdUtc = DateTime.UtcNow,
                wad = output,
                order,
                deckBoxImage = string.IsNullOrWhiteSpace(deckBoxImageId) ? null : deckBoxImageId,
                customDeckBoxTexture = string.IsNullOrWhiteSpace(deckBoxTexturePath) ? null : Path.GetFullPath(deckBoxTexturePath),
                rootCards = rootReferences,
                dependencyCardCount = Math.Max(0, sources.Count - packagedRootCards),
                sharedFunctionCount,
                runtimeResourceCount = runtime.ResourceCount,
                runtimeResourceCounts = runtime.ResourceCounts,
                runtimeCardReferences = runtime.CardReferences,
                cards = sources
            }, JsonOptions));

            return new WorkspaceSelectedCardsBuildResult(
                output,
                sourcesPath,
                sources.Count,
                copiedArt.Count,
                result,
                warnings);
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }

    private static IReadOnlyDictionary<string, string> BuildReferenceAliases(
        IReadOnlyList<WorkspaceContentVariant> variants)
    {
        Dictionary<string, HashSet<string>> candidates = new(StringComparer.OrdinalIgnoreCase);

        foreach (WorkspaceContentVariant variant in variants)
        {
            if (string.IsNullOrWhiteSpace(variant.Reference))
                continue;

            string canonicalReference = variant.Reference.Trim();
            AddAlias(candidates, canonicalReference, canonicalReference);

            string sourceFileName = Path.GetFileNameWithoutExtension(variant.RelativePath);
            if (!string.IsNullOrWhiteSpace(sourceFileName))
                AddAlias(candidates, sourceFileName, canonicalReference);

            if (!string.IsNullOrWhiteSpace(variant.ArtId))
                AddAlias(candidates, variant.ArtId.Trim(), canonicalReference);

            foreach (string alias in ReadCardIdentityAliases(variant.StoragePath))
                AddAlias(candidates, alias, canonicalReference);
        }

        return candidates
            .Where(pair => pair.Value.Count == 1)
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Single(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> ReadCardIdentityAliases(string storagePath)
    {
        try
        {
            XDocument document = XDocument.Parse(File.ReadAllText(storagePath));
            XElement? card = document.Root?.DescendantsAndSelf()
                .FirstOrDefault(element => element.Name.LocalName.Equals("CARD_V2", StringComparison.OrdinalIgnoreCase));
            if (card is null)
                return Array.Empty<string>();

            List<string> aliases = new();
            foreach (string elementName in new[] { "FILENAME", "ARTID", "MULTIVERSEID" })
            {
                XElement? element = card.Elements()
                    .FirstOrDefault(child => child.Name.LocalName.Equals(elementName, StringComparison.OrdinalIgnoreCase));
                if (element is null)
                    continue;

                string value = element.Attributes()
                    .FirstOrDefault(attribute =>
                        attribute.Name.LocalName.Equals("text", StringComparison.OrdinalIgnoreCase)
                        || attribute.Name.LocalName.Equals("value", StringComparison.OrdinalIgnoreCase))
                    ?.Value.Trim() ?? element.Value.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                    aliases.Add(value);
            }

            return aliases
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static void AddAlias(
        IDictionary<string, HashSet<string>> aliases,
        string alias,
        string canonicalReference)
    {
        if (string.IsNullOrWhiteSpace(alias))
            return;

        if (!aliases.TryGetValue(alias, out HashSet<string>? references))
        {
            references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            aliases[alias] = references;
        }

        references.Add(canonicalReference);
    }

    private static string? FindDeckTexture(string workspaceDirectory, string deckBoxImageId)
    {
        string wanted = Path.GetFileNameWithoutExtension(deckBoxImageId.Trim());
        string? textureFallback = null;
        foreach (string path in Directory.EnumerateFiles(workspaceDirectory, "*.tdx", SearchOption.AllDirectories))
        {
            if (!Path.GetFileNameWithoutExtension(path).Equals(wanted, StringComparison.OrdinalIgnoreCase))
                continue;

            string normalized = path.Replace('/', '\\');
            if (normalized.Contains("\\ART_ASSETS\\TEXTURES\\DECKS\\", StringComparison.OrdinalIgnoreCase))
                return path;

            if (textureFallback is null
                && normalized.Contains("\\ART_ASSETS\\TEXTURES\\", StringComparison.OrdinalIgnoreCase))
            {
                textureFallback = path;
            }
        }

        return textureFallback;
    }

    private static WorkspaceContentVariant ResolveVariant(
        string reference,
        IReadOnlyList<WorkspaceContentVariant> variants,
        IReadOnlyList<WorkspaceContentVariantConflict> conflicts,
        IReadOnlyDictionary<string, string>? selections)
    {
        WorkspaceContentVariantConflict? conflict = conflicts.FirstOrDefault(item =>
            item.IsCardDefinition
            && item.Variants.Any(variant => variant.Reference.Equals(reference, StringComparison.OrdinalIgnoreCase)));
        if (conflict is not null
            && selections is not null
            && selections.TryGetValue(conflict.ConflictKey, out string? selectedKey))
        {
            WorkspaceContentVariant? selected = conflict.Variants.FirstOrDefault(item =>
                item.SelectionKey.Equals(selectedKey, StringComparison.Ordinal));
            if (selected is not null)
                return selected;
        }

        return variants
            .OrderBy(item => item.IsRecommended ? 1 : 0)
            .ThenBy(item => item.WadOrder)
            .ThenBy(item => item.WadName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.PackageName, StringComparer.OrdinalIgnoreCase)
            .Last();
    }

    private static string SanitizeFileName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string safe = new(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "CARD" : safe.Trim();
    }
}
