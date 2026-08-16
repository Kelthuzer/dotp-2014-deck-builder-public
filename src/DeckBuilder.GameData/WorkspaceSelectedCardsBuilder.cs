using System.Text.Json;

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
/// Creates a small support WAD containing card definitions/art referenced by the deck being
/// exported, including recursive CARD_V2 dependencies used by card mechanics (tokens, generated
/// cards and other referenced card definitions). Variant decisions are supplied by the UI after
/// the deck is already built.
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
        HashSet<string> knownReferences = scan.CardVariants
            .Select(variant => variant.Reference)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

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

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
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

                if (!string.IsNullOrWhiteSpace(selected.ArtStoragePath)
                    && File.Exists(selected.ArtStoragePath)
                    && !string.IsNullOrWhiteSpace(selected.ArtId))
                {
                    string artFileName = SanitizeFileName(selected.ArtId) + ".TDX";
                    if (copiedArt.Add(artFileName))
                    {
                        File.Copy(selected.ArtStoragePath, Path.Combine(artDirectory, artFileName), overwrite: true);
                    }
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
                    knownReferences,
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
            {
                throw new InvalidDataException("None of the deck's cards have extracted definitions that can be packaged.");
            }

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
                    {
                        throw new DirectoryNotFoundException(
                            $"Cannot package deck cover '{deckBoxImageId}' because the extracted workspace is unavailable.");
                    }

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
                    UnpackedContentKind.Cards,
                    order),
                cancellationToken);

            string sourcesPath = output + ".sources.json";
            File.WriteAllText(sourcesPath, JsonSerializer.Serialize(new
            {
                formatVersion = 1,
                createdUtc = DateTime.UtcNow,
                wad = output,
                order,
                deckBoxImage = string.IsNullOrWhiteSpace(deckBoxImageId) ? null : deckBoxImageId,
                customDeckBoxTexture = string.IsNullOrWhiteSpace(deckBoxTexturePath) ? null : Path.GetFullPath(deckBoxTexturePath),
                rootCards = rootReferences,
                dependencyCardCount = Math.Max(0, sources.Count - packagedRootCards),
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
            {
                Directory.Delete(staging, recursive: true);
            }
        }
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
            {
                return selected;
            }
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
