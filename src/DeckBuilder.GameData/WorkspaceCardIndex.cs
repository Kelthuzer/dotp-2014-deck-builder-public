using System.Xml.Linq;

namespace DeckBuilder.GameData;

/// <summary>
/// Immutable lookup tables for CARD_V2 variants used by portable packaging.
/// Expensive XML identity parsing happens once per build instead of once per dependency lookup.
/// </summary>
internal sealed class WorkspaceCardIndex
{
    private readonly IReadOnlyDictionary<string, WorkspaceContentVariant[]> _variantsByReference;
    private readonly IReadOnlyDictionary<string, WorkspaceContentVariantConflict> _conflictsByReference;

    private WorkspaceCardIndex(
        IReadOnlyDictionary<string, WorkspaceContentVariant[]> variantsByReference,
        IReadOnlyDictionary<string, WorkspaceContentVariantConflict> conflictsByReference,
        IReadOnlyDictionary<string, string> aliases)
    {
        _variantsByReference = variantsByReference;
        _conflictsByReference = conflictsByReference;
        Aliases = aliases;
    }

    public IReadOnlyDictionary<string, string> Aliases { get; }

    public static WorkspaceCardIndex Create(WorkspaceContentVariantScanResult scan)
    {
        ArgumentNullException.ThrowIfNull(scan);

        Dictionary<string, WorkspaceContentVariant[]> variantsByReference = scan.CardVariants
            .Where(variant => !string.IsNullOrWhiteSpace(variant.Reference))
            .GroupBy(variant => variant.Reference.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray(),
                StringComparer.OrdinalIgnoreCase);

        Dictionary<string, WorkspaceContentVariantConflict> conflictsByReference =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (WorkspaceContentVariantConflict conflict in scan.Conflicts.Where(conflict => conflict.IsCardDefinition))
        {
            foreach (string reference in conflict.Variants
                         .Select(variant => variant.Reference)
                         .Where(reference => !string.IsNullOrWhiteSpace(reference))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                conflictsByReference.TryAdd(reference.Trim(), conflict);
            }
        }

        return new WorkspaceCardIndex(
            variantsByReference,
            conflictsByReference,
            BuildAliases(scan.CardVariants));
    }

    public bool Contains(string reference) =>
        !string.IsNullOrWhiteSpace(reference)
        && _variantsByReference.ContainsKey(reference.Trim());

    public bool TryResolve(
        string reference,
        IReadOnlyDictionary<string, string>? selections,
        out WorkspaceContentVariant variant)
    {
        variant = null!;
        if (string.IsNullOrWhiteSpace(reference)
            || !_variantsByReference.TryGetValue(reference.Trim(), out WorkspaceContentVariant[]? variants)
            || variants.Length == 0)
        {
            return false;
        }

        if (_conflictsByReference.TryGetValue(reference.Trim(), out WorkspaceContentVariantConflict? conflict)
            && selections is not null
            && selections.TryGetValue(conflict.ConflictKey, out string? selectedKey))
        {
            WorkspaceContentVariant? selected = conflict.Variants.FirstOrDefault(candidate =>
                candidate.SelectionKey.Equals(selectedKey, StringComparison.Ordinal));
            if (selected is not null)
            {
                variant = selected;
                return true;
            }
        }

        variant = variants
            .OrderBy(candidate => candidate.IsRecommended ? 1 : 0)
            .ThenBy(candidate => candidate.WadOrder)
            .ThenBy(candidate => candidate.WadName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.PackageName, StringComparer.OrdinalIgnoreCase)
            .Last();
        return true;
    }

    private static IReadOnlyDictionary<string, string> BuildAliases(
        IReadOnlyList<WorkspaceContentVariant> variants)
    {
        Dictionary<string, HashSet<string>> candidates = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, IReadOnlyList<string>> xmlAliasesByPath = new(StringComparer.OrdinalIgnoreCase);

        foreach (WorkspaceContentVariant variant in variants)
        {
            if (string.IsNullOrWhiteSpace(variant.Reference))
                continue;

            string canonical = variant.Reference.Trim();
            AddAlias(candidates, canonical, canonical);
            AddAlias(candidates, Path.GetFileNameWithoutExtension(variant.RelativePath), canonical);
            AddAlias(candidates, variant.ArtId, canonical);

            if (!xmlAliasesByPath.TryGetValue(variant.StoragePath, out IReadOnlyList<string>? xmlAliases))
            {
                xmlAliases = ReadIdentityAliases(variant.StoragePath);
                xmlAliasesByPath[variant.StoragePath] = xmlAliases;
            }

            foreach (string alias in xmlAliases)
                AddAlias(candidates, alias, canonical);
        }

        // Ambiguous aliases are intentionally dropped. A numeric id or filename that maps to more
        // than one CARD_V2 must not silently choose a different card on the recipient installation.
        return candidates
            .Where(pair => pair.Value.Count == 1)
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Single(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> ReadIdentityAliases(string storagePath)
    {
        try
        {
            XDocument document = XDocument.Parse(File.ReadAllText(storagePath));
            XElement? card = document.Root?.DescendantsAndSelf()
                .FirstOrDefault(element => element.Name.LocalName.Equals("CARD_V2", StringComparison.OrdinalIgnoreCase));
            if (card is null)
                return Array.Empty<string>();

            List<string> aliases = new();
            foreach (string name in new[] { "FILENAME", "ARTID", "MULTIVERSEID" })
            {
                XElement? element = card.Elements()
                    .FirstOrDefault(child => child.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
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

            return aliases.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static void AddAlias(
        IDictionary<string, HashSet<string>> aliases,
        string? alias,
        string canonicalReference)
    {
        if (string.IsNullOrWhiteSpace(alias))
            return;

        string key = alias.Trim();
        if (!aliases.TryGetValue(key, out HashSet<string>? references))
        {
            references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            aliases[key] = references;
        }

        references.Add(canonicalReference);
    }
}
