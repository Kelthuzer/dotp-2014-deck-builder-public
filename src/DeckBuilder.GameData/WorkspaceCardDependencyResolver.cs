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
    // TOKEN_REGISTRATION appears in ordinary definitions and must never itself be reported as a token.
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

        // Community CW_Tokens does not synthesize a CARD_V2 definition. It constructs a physical
        // FILENAME such as TOKEN_HUMAN_SOLDIER_C_1_1_W_CW_3 and passes that name to
        // PutTokensOntoBattlefield. Therefore TOKEN_REGISTRATION entries used by CW_Tokens are real
        // card dependencies: package the CARD_V2 when it exists and report it when it does not.
        foreach (string registeredToken in ExtractRegisteredTokenReferences(xml))
        {
            if (registeredToken.Equals(currentReference, StringComparison.OrdinalIgnoreCase)
                || IsSelfAlias(registeredToken, currentReference))
            {
                continue;
            }

            if (TryResolveReferenceAlias(registeredToken, referenceAliases, out string canonicalReference))
            {
                references.Add(canonicalReference);
            }
            else if (registeredToken.StartsWith("TOKEN_", StringComparison.Ordinal))
            {
                missingTokens.Add(registeredToken);
            }
        }

        // Scan XML payload values rather than element/attribute names. Schema names such as
        // TOKEN_REGISTRATION describe the card format; only values/text can name another card.
        foreach (string payload in ExtractPayloadValues(xml))
        {
            foreach (Match match in IdentifierRegex.Matches(payload))
            {
                string candidate = match.Value;
                if (!TryResolveReferenceAlias(candidate, referenceAliases, out string canonicalReference)
                    || canonicalReference.Equals(currentReference, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                references.Add(canonicalReference);
            }

            foreach (Match match in TokenRegex.Matches(payload))
            {
                string candidate = match.Value;

                // CARD_V2 filenames and CW/RSN logical token ids are conventionally uppercase.
                // Lower-case values such as token_count, token_size and token_chest are helper
                // variables/parameters used by card scripts and must not be reported as missing cards.
                if (!candidate.StartsWith("TOKEN_", StringComparison.Ordinal))
                    continue;

                if (IgnoredTokenIdentifiers.Contains(candidate)
                    || candidate.Equals(currentReference, StringComparison.OrdinalIgnoreCase)
                    || IsSelfAlias(candidate, currentReference)
                    || TryResolveReferenceAlias(candidate, referenceAliases, out _))
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

    /// <summary>
    /// Resolves the logical identifiers used by CARD_V2 and shared CW/RSN runtime files to the
    /// canonical CARD_V2 reference selected from the extracted workspace. Kept here so card XML
    /// scanning and runtime-resource scanning cannot diverge in their alias rules.
    /// </summary>
    internal static bool TryResolveReferenceAlias(
        string candidate,
        IReadOnlyDictionary<string, string> referenceAliases,
        out string canonicalReference)
    {
        if (referenceAliases.TryGetValue(candidate, out string? exactReference))
        {
            canonicalReference = exactReference;
            return true;
        }

        // Community/RSN card definitions commonly register helper tokens with an RSN_ prefixed
        // CARD_V2 filename while abilities refer to the logical TOKEN_* name without that prefix.
        // Resolve both directions so TOKEN_CONSTRUCT_* can package RSN_TOKEN_CONSTRUCT_* (and vice
        // versa) instead of merely suppressing the missing-token warning.
        string? alternate = candidate.StartsWith("RSN_TOKEN_", StringComparison.OrdinalIgnoreCase)
            ? candidate[4..]
            : candidate.StartsWith("TOKEN_", StringComparison.OrdinalIgnoreCase)
                ? "RSN_" + candidate
                : null;

        if (alternate is not null
            && referenceAliases.TryGetValue(alternate, out string? alternateReference))
        {
            canonicalReference = alternateReference;
            return true;
        }

        canonicalReference = string.Empty;
        return false;
    }

    private static bool IsSelfAlias(string candidate, string currentReference)
    {
        // Some DotP helper token definitions use an RSN_ prefixed CARD_V2 filename while their
        // own payload refers to the same logical token without that implementation prefix, e.g.
        // RSN_TOKEN_MANA_G -> TOKEN_MANA_G. This is not a dependency on a second CARD_V2.
        return currentReference.StartsWith("RSN_", StringComparison.OrdinalIgnoreCase)
            && candidate.Equals(currentReference[4..], StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ExtractRegisteredTokenReferences(string xml)
    {
        XDocument? document = null;
        try
        {
            document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        }
        catch
        {
            // The generic raw scanner below remains available for malformed legacy XML.
        }

        if (document is null)
            yield break;

        foreach (XElement registration in document.Descendants().Where(element =>
                     element.Name.LocalName.Equals("TOKEN_REGISTRATION", StringComparison.OrdinalIgnoreCase)))
        {
            string type = registration.Attributes()
                .FirstOrDefault(attribute => attribute.Name.LocalName.Equals("type", StringComparison.OrdinalIgnoreCase))
                ?.Value.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(type))
                yield return type;
        }
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
