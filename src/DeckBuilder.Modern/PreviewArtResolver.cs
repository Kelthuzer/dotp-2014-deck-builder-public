using System.IO;
using DeckBuilder.Core.Models;
using DeckBuilder.GameData;

namespace DeckBuilder.Modern;

internal sealed record PreviewArtLookup(
    CardImageData? Image,
    string RequestedId,
    string? ResolvedId,
    IReadOnlyList<string> TriedIds)
{
    public bool UsedAlternateName => Image is not null
        && ResolvedId is not null
        && !ResolvedId.Equals(RequestedId, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Resolves preview art without changing the card reference or definition. The only alternates
/// considered are exact physical TDX identifiers derivable from the card's own ARTID/filename.
/// No suffix stripping or fuzzy substitution of another printing is performed.
/// </summary>
internal static class PreviewArtResolver
{
    public static async Task<PreviewArtLookup> ResolveAsync(GameCardImageLoader loader, CardRecord card)
    {
        string requested = Normalize(card.ImageId);
        List<string> tried = new();
        if (requested.Length == 0)
        {
            return new PreviewArtLookup(null, requested, null, tried);
        }

        tried.Add(requested);
        CardImageData? exact = await loader.LoadAsync(requested, GameImageKind.Illustration);
        if (exact is not null)
        {
            return new PreviewArtLookup(exact, requested, requested, tried);
        }

        List<string> candidates = new();
        AddCandidate(candidates, card.FileName, requested);
        if (requested.StartsWith('T') && requested.Length > 1)
        {
            AddCandidate(candidates, requested[1..], requested);
        }
        else
        {
            AddCandidate(candidates, "T" + requested, requested);
        }

        IReadOnlyList<string> available = await loader.GetImageIdsAsync(GameImageKind.Illustration);
        foreach (string id in available)
        {
            if (id.EndsWith("_" + requested, StringComparison.OrdinalIgnoreCase))
            {
                AddCandidate(candidates, id, requested);
            }
        }

        foreach (string candidate in candidates)
        {
            tried.Add(candidate);
            CardImageData? image;
            try
            {
                image = await loader.LoadAsync(candidate, GameImageKind.Illustration);
            }
            catch
            {
                continue;
            }

            if (image is not null)
            {
                return new PreviewArtLookup(image, requested, candidate, tried);
            }
        }

        return new PreviewArtLookup(null, requested, null, tried);
    }

    private static void AddCandidate(ICollection<string> candidates, string? candidate, string requested)
    {
        string normalized = Normalize(candidate);
        if (normalized.Length == 0
            || normalized.Equals(requested, StringComparison.OrdinalIgnoreCase)
            || candidates.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        candidates.Add(normalized);
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : Path.GetFileNameWithoutExtension(value.Trim()).ToUpperInvariant();
}
