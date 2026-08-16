using System.Text.RegularExpressions;

namespace DeckBuilder.GameData;

internal sealed record WorkspaceCardDependencyScanResult(
    IReadOnlyList<string> References,
    IReadOnlyList<string> MissingTokenReferences);

/// <summary>
/// Finds CARD_V2 references embedded in a card definition. DotP mechanics frequently point at
/// token/card FILENAME values from ability XML; those referenced CARD_V2 files must travel with a
/// custom deck or the engine can execute the ability without being able to instantiate its result.
/// </summary>
internal static class WorkspaceCardDependencyResolver
{
    private static readonly Regex IdentifierRegex = new(
        @"(?<![A-Za-z0-9_])[A-Za-z0-9_]+(?![A-Za-z0-9_])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TokenRegex = new(
        @"(?<![A-Za-z0-9_])TOKEN_[A-Za-z0-9_]+(?![A-Za-z0-9_])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static WorkspaceCardDependencyScanResult Scan(
        string xml,
        IReadOnlyDictionary<string, string> referenceAliases,
        string currentReference)
    {
        ArgumentNullException.ThrowIfNull(xml);
        ArgumentNullException.ThrowIfNull(referenceAliases);

        HashSet<string> references = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> missingTokens = new(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in IdentifierRegex.Matches(xml))
        {
            string candidate = match.Value;
            if (!referenceAliases.TryGetValue(candidate, out string? canonicalReference)
                || canonicalReference.Equals(currentReference, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            references.Add(canonicalReference);
        }

        foreach (Match match in TokenRegex.Matches(xml))
        {
            string candidate = match.Value;
            if (!candidate.Equals(currentReference, StringComparison.OrdinalIgnoreCase)
                && !referenceAliases.ContainsKey(candidate))
            {
                missingTokens.Add(candidate);
            }
        }

        return new WorkspaceCardDependencyScanResult(
            references.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
            missingTokens.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray());
    }
}
