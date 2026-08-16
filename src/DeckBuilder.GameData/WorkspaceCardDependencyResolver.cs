using System.Text.RegularExpressions;
using System.Xml.Linq;

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

    // These are XML/schema identifiers, not CARD_V2 FILENAME references. In particular,
    // TOKEN_REGISTRATION appears in ordinary definitions and must never be reported as a token.
    private static readonly HashSet<string> IgnoredTokenIdentifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "TOKEN_REGISTRATION"
    };

    public static WorkspaceCardDependencyScanResult Scan(
        string xml,
        IReadOnlyDictionary<string, string> referenceAliases,
        string currentReference)
    {
        ArgumentNullException.ThrowIfNull(xml);
        ArgumentNullException.ThrowIfNull(referenceAliases);

        HashSet<string> references = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> missingTokens = new(StringComparer.OrdinalIgnoreCase);

        // Scan XML payload values rather than element/attribute names. Schema names such as
        // TOKEN_REGISTRATION describe the card format; only values/text can name another card.
        foreach (string payload in ExtractPayloadValues(xml))
        {
            foreach (Match match in IdentifierRegex.Matches(payload))
            {
                string candidate = match.Value;
                if (!referenceAliases.TryGetValue(candidate, out string? canonicalReference)
                    || canonicalReference.Equals(currentReference, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                references.Add(canonicalReference);
            }

            foreach (Match match in TokenRegex.Matches(payload))
            {
                string candidate = match.Value;
                if (IgnoredTokenIdentifiers.Contains(candidate)
                    || candidate.Equals(currentReference, StringComparison.OrdinalIgnoreCase)
                    || IsSelfAlias(candidate, currentReference)
                    || referenceAliases.ContainsKey(candidate))
                {
                    continue;
                }

                missingTokens.Add(candidate);
            }
        }

        return new WorkspaceCardDependencyScanResult(
            references.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
            missingTokens.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static bool IsSelfAlias(string candidate, string currentReference)
    {
        // Some DotP helper token definitions use an RSN_ prefixed CARD_V2 filename while their
        // own payload refers to the same logical token without that implementation prefix, e.g.
        // RSN_TOKEN_MANA_G -> TOKEN_MANA_G. This is not a dependency on a second CARD_V2.
        return currentReference.StartsWith("RSN_", StringComparison.OrdinalIgnoreCase)
            && candidate.Equals(currentReference[4..], StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ExtractPayloadValues(string xml)
    {
        XDocument? document = null;
        try
        {
            document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        }
        catch
        {
            // Fall back to raw scanning below. The caller still filters known schema identifiers.
        }

        if (document is null)
        {
            yield return xml;
            yield break;
        }

        foreach (XAttribute attribute in document.Descendants().Attributes())
        {
            if (!string.IsNullOrWhiteSpace(attribute.Value))
                yield return attribute.Value;
        }

        foreach (XText text in document.DescendantNodes().OfType<XText>())
        {
            if (!string.IsNullOrWhiteSpace(text.Value))
                yield return text.Value;
        }
    }
}
