using System.Collections.Concurrent;
using DeckBuilder.Core.Models;

namespace DeckBuilder.GameData;

public sealed record CatalogLoadProgress(string Source, int SourcesProcessed, int CardsLoaded);

public sealed record CatalogLoadResult(IReadOnlyList<CardRecord> Cards, IReadOnlyList<string> Warnings);

public sealed class GameCardCatalogLoader
{
    public async Task<CatalogLoadResult> LoadAsync(
        string gameDirectory,
        IProgress<CatalogLoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        if (!Directory.Exists(gameDirectory))
        {
            throw new DirectoryNotFoundException(gameDirectory);
        }

        return await Task.Run(() => Load(gameDirectory, progress, cancellationToken), cancellationToken);
    }

    private static CatalogLoadResult Load(
        string gameDirectory,
        IProgress<CatalogLoadProgress>? progress,
        CancellationToken cancellationToken)
    {
        Dictionary<string, CatalogCandidate> selected = new(StringComparer.OrdinalIgnoreCase);
        ConcurrentBag<string> warnings = new();
        int processed = 0;

        CatalogSource[] sources = EnumerateSources(gameDirectory)
            .OrderBy(source => source.Order)
            .ThenBy(source => source.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (CatalogSource source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (source.IsDirectory)
                {
                    string cardDirectory = Path.Combine(source.Path, "DATA_ALL_PLATFORMS", "CARDS");
                    foreach (string cardPath in Directory.EnumerateFiles(cardDirectory, "*.xml", SearchOption.TopDirectoryOnly)
                                 .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            CardRecord? card = CardXmlParser.Parse(File.ReadAllText(cardPath), source.Name);
                            if (card is not null)
                            {
                                AddOrPrefer(selected, card, source.Order, source.Name);
                            }
                        }
                        catch (Exception exception)
                        {
                            warnings.Add($"{cardPath}: {exception.Message}");
                        }
                    }
                }
                else
                {
                    foreach (CardRecord card in WadCardCatalogReader.Read(source.Path, cancellationToken))
                    {
                        AddOrPrefer(selected, card, source.Order, source.Name);
                    }
                }
            }
            catch (Exception exception)
            {
                warnings.Add($"{source.Name}: {exception.Message}");
            }

            progress?.Report(new CatalogLoadProgress(source.Name, ++processed, selected.Count));
        }

        Dictionary<string, CardRecord> cards = selected.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Card,
            StringComparer.OrdinalIgnoreCase);

        RecoverReferencedCards(gameDirectory, cards, warnings, cancellationToken);

        return new CatalogLoadResult(
            cards.Values
                .Select(EnsureImageReference)
                .OrderBy(card => card.LocalizedName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(card => card.FileName, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            warnings.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static IEnumerable<CatalogSource> EnumerateSources(string gameDirectory)
    {
        foreach (string wadPath in Directory.EnumerateFiles(gameDirectory, "*.wad", SearchOption.TopDirectoryOnly)
                     .Where(GameWadSelection.IsSupported))
        {
            yield return new CatalogSource(
                wadPath,
                FileName(wadPath),
                GameContentLoadOrder.Read(wadPath),
                IsDirectory: false);
        }

        foreach (string directory in FindUnpackedWads(gameDirectory))
        {
            yield return new CatalogSource(
                directory,
                FileName(directory),
                GameContentLoadOrder.Read(directory),
                IsDirectory: true);
        }
    }

    private static void RecoverReferencedCards(
        string gameDirectory,
        IDictionary<string, CardRecord> cards,
        ConcurrentBag<string> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            GameDeckCatalogLoadResult deckResult = new GameDeckCatalogLoader()
                .LoadAsync(gameDirectory, cards.Values.ToArray(), cancellationToken)
                .GetAwaiter()
                .GetResult();
            string[] missing = deckResult.Decks
                .Where(deck => GameWadSelection.IsSupported(deck.Source))
                .SelectMany(deck => deck.Deck.MainDeck
                    .Concat(deck.Deck.RegularUnlocks)
                    .Concat(deck.Deck.PromoUnlocks))
                .Select(entry => entry.Card.FileName)
                .Where(reference => !cards.ContainsKey(reference))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(reference => reference, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (missing.Length == 0)
            {
                return;
            }

            MissingCardResolutionResult resolution = new MissingCardReferenceResolver()
                .ResolveAsync(gameDirectory, missing, cancellationToken)
                .GetAwaiter()
                .GetResult();
            foreach (CardRecord card in resolution.Cards)
            {
                AddOrPrefer(cards, card);
            }

            foreach (string warning in resolution.Warnings)
            {
                warnings.Add(warning);
            }
        }
        catch (Exception exception)
        {
            warnings.Add($"Referenced-card recovery: {exception.Message}");
        }
    }

    private static void AddOrPrefer(
        IDictionary<string, CatalogCandidate> cards,
        CardRecord candidate,
        int sourceOrder,
        string sourceName)
    {
        if (!cards.TryGetValue(candidate.FileName, out CatalogCandidate? existing))
        {
            cards.Add(candidate.FileName, new CatalogCandidate(candidate, sourceOrder, sourceName));
            return;
        }

        bool candidateWins = sourceOrder > existing.Order
            || sourceOrder == existing.Order && DefinitionScore(candidate) > DefinitionScore(existing.Card);
        CardRecord preferred = candidateWins ? candidate : existing.Card;
        CardRecord alternate = candidateWins ? existing.Card : candidate;
        int preferredOrder = candidateWins ? sourceOrder : existing.Order;
        string preferredSource = candidateWins ? sourceName : existing.SourceName;
        cards[candidate.FileName] = new CatalogCandidate(
            MergeRussianLocalization(preferred, alternate),
            preferredOrder,
            preferredSource);
    }

    private static void AddOrPrefer(IDictionary<string, CardRecord> cards, CardRecord candidate)
    {
        if (!cards.TryGetValue(candidate.FileName, out CardRecord? existing))
        {
            cards.Add(candidate.FileName, candidate);
            return;
        }

        CardRecord preferred = DefinitionScore(candidate) > DefinitionScore(existing) ? candidate : existing;
        CardRecord alternate = ReferenceEquals(preferred, candidate) ? existing : candidate;
        cards[candidate.FileName] = MergeRussianLocalization(preferred, alternate);
    }

    private static CardRecord MergeRussianLocalization(CardRecord preferred, CardRecord alternate)
    {
        string localizedName = PreferRussianText(preferred.LocalizedName, alternate.LocalizedName, preferred.FileName);
        string rulesText = PreferRussianText(preferred.RulesText, alternate.RulesText, string.Empty);
        string flavorText = PreferRussianText(preferred.FlavorText, alternate.FlavorText, string.Empty);

        return new CardRecord(
            preferred.FileName,
            localizedName,
            preferred.EnglishName,
            preferred.TypeLine,
            preferred.Expansion,
            preferred.Artist,
            preferred.CastingCost,
            preferred.Colour,
            preferred.Rarity,
            preferred.Power,
            preferred.Toughness,
            preferred.Source,
            preferred.ImageId,
            rulesText,
            flavorText,
            preferred.FrameType,
            preferred.IsToken);
    }

    private static string PreferRussianText(string preferred, string alternate, string fallback)
    {
        if (ContainsCyrillic(preferred))
            return preferred;
        if (ContainsCyrillic(alternate))
            return alternate;
        if (!string.IsNullOrWhiteSpace(preferred))
            return preferred;
        if (!string.IsNullOrWhiteSpace(alternate))
            return alternate;
        return fallback;
    }

    private static bool ContainsCyrillic(string value) => value.Any(character =>
        character is >= '\u0400' and <= '\u052F');

    private static int DefinitionScore(CardRecord card)
    {
        int score = 0;

        // DefinitionScore is only a tie-breaker within the same game load order. A later WAD
        // order always wins first, matching the actual Magic 2014 content override model.
        if (!string.IsNullOrWhiteSpace(card.ImageId)) score += 120;

        if (HasMeaningfulName(card.LocalizedName, card.FileName)) score += 45;
        if (HasMeaningfulName(card.EnglishName, card.FileName)) score += 30;
        if (!string.IsNullOrWhiteSpace(card.RulesText)) score += 25;
        if (!string.IsNullOrWhiteSpace(card.TypeLine)) score += 20;
        if (!string.IsNullOrWhiteSpace(card.CastingCost)) score += 10;
        if (!string.IsNullOrWhiteSpace(card.Expansion)) score += 8;
        if (!string.IsNullOrWhiteSpace(card.Artist)) score += 8;
        if (!string.IsNullOrWhiteSpace(card.Colour)) score += 5;
        if (!string.IsNullOrWhiteSpace(card.Rarity)) score += 5;
        if (!string.IsNullOrWhiteSpace(card.FrameType)) score += 4;
        if (!string.IsNullOrWhiteSpace(card.FlavorText)) score += 3;
        if (!string.IsNullOrWhiteSpace(card.Power)) score += 2;
        if (!string.IsNullOrWhiteSpace(card.Toughness)) score += 2;

        return score;
    }

    private static bool HasMeaningfulName(string value, string fileName) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.Equals(fileName, StringComparison.OrdinalIgnoreCase);

    private static CardRecord EnsureImageReference(CardRecord card)
    {
        if (!string.IsNullOrWhiteSpace(card.ImageId))
        {
            return card;
        }

        // A few valid Magic 2014 definitions omit ARTID while their illustration TDX uses the
        // card filename. Keep the original XML preference logic above, then use this only as the
        // last-resort image lookup key so the UI can still attempt to load the art.
        return new CardRecord(
            card.FileName,
            card.LocalizedName,
            card.EnglishName,
            card.TypeLine,
            card.Expansion,
            card.Artist,
            card.CastingCost,
            card.Colour,
            card.Rarity,
            card.Power,
            card.Toughness,
            card.Source,
            card.FileName,
            card.RulesText,
            card.FlavorText,
            card.FrameType,
            card.IsToken);
    }

    private static IEnumerable<string> FindUnpackedWads(string gameDirectory)
    {
        if (Directory.Exists(Path.Combine(gameDirectory, "DATA_ALL_PLATFORMS", "CARDS")))
        {
            yield return gameDirectory;
        }

        foreach (string directory in Directory.EnumerateDirectories(gameDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            if (GameWadSelection.IsSupported(directory)
                && Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                    .Any(file => Path.GetFileName(file).Equals("HEADER.XML", StringComparison.OrdinalIgnoreCase))
                && Directory.Exists(Path.Combine(directory, "DATA_ALL_PLATFORMS", "CARDS")))
            {
                yield return directory;
            }
        }
    }

    private static string FileName(string path) => Path.GetFileName(path) ?? path;

    private sealed record CatalogSource(string Path, string Name, int Order, bool IsDirectory);
    private sealed record CatalogCandidate(CardRecord Card, int Order, string SourceName);
}
