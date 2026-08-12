using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DeckBuilder.GameData;

public sealed class CardReferenceScanService
{
    private static readonly Regex NameRegex = new(@"TOKEN_[A-Za-z0-9_]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NumberRegex = new(@"(?<!\d)\d{5,}(?!\d)", RegexOptions.Compiled);

    public Task<CardReferenceScanResult> ScanAsync(string root, IProgress<string>? progress = null) =>
        Task.Run(() => Scan(Path.GetFullPath(root), progress));

    private static CardReferenceScanResult Scan(string root, IProgress<string>? progress)
    {
        string[] xmlFiles = Directory.EnumerateFiles(root, "*.xml", SearchOption.AllDirectories).ToArray();
        string[] tdxFiles = Directory.EnumerateFiles(root, "*.tdx", SearchOption.AllDirectories).ToArray();
        Dictionary<string, List<string>> artIndex = BuildArtIndex(tdxFiles);
        List<XmlSource> sources = new();
        List<CardInfo> cards = new();
        int failures = 0;

        for (int i = 0; i < xmlFiles.Length; i++)
        {
            try
            {
                string raw = File.ReadAllText(xmlFiles[i]);
                CardInfo? card = ParseCard(xmlFiles[i], raw);
                sources.Add(new XmlSource(xmlFiles[i], raw, card));
                if (card is not null) cards.Add(card);
            }
            catch { failures++; }
            if ((i + 1) % 500 == 0 || i + 1 == xmlFiles.Length)
                progress?.Report($"Reading XML {i + 1:N0}/{xmlFiles.Length:N0}…");
        }

        Dictionary<string, List<CardInfo>> byName = cards.GroupBy(c => c.FileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<CardInfo>> byId = new(StringComparer.OrdinalIgnoreCase);
        foreach (CardInfo card in cards)
        {
            AddCard(byId, card.ArtId, card);
            AddCard(byId, card.MultiverseId, card);
        }

        Dictionary<string, HashSet<string>> inbound = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < sources.Count; i++)
        {
            XmlSource source = sources[i];
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in NameRegex.Matches(source.RawXml))
            {
                string value = match.Value;
                if (!seen.Add("N:" + value) || !byName.TryGetValue(value, out List<CardInfo>? targets)) continue;
                foreach (CardInfo target in targets)
                {
                    if (source.Card?.SourcePath.Equals(target.SourcePath, StringComparison.OrdinalIgnoreCase) == true) continue;
                    AddInbound(inbound, target.SourcePath, Label(root, source));
                }
            }
            foreach (Match match in NumberRegex.Matches(source.RawXml))
            {
                string value = match.Value;
                if (source.Card is not null && (value == source.Card.ArtId || value == source.Card.MultiverseId)) continue;
                if (!seen.Add("I:" + value) || !byId.TryGetValue(value, out List<CardInfo>? targets)) continue;
                foreach (CardInfo target in targets)
                {
                    if (source.Card?.SourcePath.Equals(target.SourcePath, StringComparison.OrdinalIgnoreCase) == true) continue;
                    AddInbound(inbound, target.SourcePath, Label(root, source));
                }
            }
            if ((i + 1) % 500 == 0 || i + 1 == sources.Count)
                progress?.Report($"Resolving references {i + 1:N0}/{sources.Count:N0}…");
        }

        List<CardReferenceScanRow> rows = new();
        foreach (CardInfo card in cards)
        {
            HashSet<string> users = inbound.TryGetValue(card.SourcePath, out HashSet<string>? set)
                ? set : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string[] candidates = ArtCandidates(card)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string[] matchingPaths = candidates
                .Where(artIndex.ContainsKey)
                .SelectMany(id => artIndex[id])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string? artPath = matchingPaths.FirstOrDefault();

            rows.Add(new CardReferenceScanRow(
                card.FileName,
                card.ArtId,
                card.MultiverseId,
                card.IsToken,
                users.Count,
                string.Join(" | ", users.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
                artPath is not null,
                Path.GetRelativePath(root, card.SourcePath),
                artPath is null ? string.Empty : Path.GetRelativePath(root, artPath),
                string.Join(" | ", candidates),
                string.Join(" | ", matchingPaths.Select(path => Path.GetRelativePath(root, path)))));
        }

        return new CardReferenceScanResult(rows.OrderBy(r => r.FileName, StringComparer.OrdinalIgnoreCase).ToArray(),
            xmlFiles.Length, cards.Count, tdxFiles.Length, failures);
    }

    private static CardInfo? ParseCard(string path, string raw)
    {
        XElement? card = XDocument.Parse(raw).Root?.DescendantsAndSelf()
            .FirstOrDefault(e => e.Name.LocalName.Equals("CARD_V2", StringComparison.OrdinalIgnoreCase));
        if (card is null) return null;
        string fileName = Attribute(Child(card, "FILENAME"), "text");
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        return new CardInfo(fileName,
            Attribute(Child(card, "ARTID"), "value"),
            Attribute(Child(card, "MULTIVERSEID"), "value"),
            fileName.StartsWith("TOKEN_", StringComparison.OrdinalIgnoreCase) || Child(card, "TOKEN") is not null,
            path);
    }

    private static Dictionary<string, List<string>> BuildArtIndex(IEnumerable<string> files)
    {
        Dictionary<string, List<string>> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in files)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            AddArt(result, name, path);
            Match tail = Regex.Match(name, @"(\d{5,})$");
            if (tail.Success)
            {
                AddArt(result, tail.Groups[1].Value, path);
                AddArt(result, "T" + tail.Groups[1].Value, path);
            }
        }
        return result;
    }

    private static IEnumerable<string> ArtCandidates(CardInfo card)
    {
        if (!string.IsNullOrWhiteSpace(card.ArtId))
        {
            yield return card.ArtId;
            if (card.ArtId.All(char.IsDigit))
                yield return "T" + card.ArtId;
        }

        if (!string.IsNullOrWhiteSpace(card.MultiverseId))
        {
            yield return card.MultiverseId;
            if (card.MultiverseId.All(char.IsDigit))
                yield return "T" + card.MultiverseId;
        }

        yield return card.FileName;

        Match numericTail = Regex.Match(card.FileName, @"(\d{5,})$");
        if (numericTail.Success)
        {
            yield return numericTail.Groups[1].Value;
            yield return "T" + numericTail.Groups[1].Value;
        }
    }

    private static void AddArt(Dictionary<string, List<string>> index, string key, string path)
    {
        if (!index.TryGetValue(key, out List<string>? list)) index[key] = list = new List<string>();
        if (!list.Contains(path, StringComparer.OrdinalIgnoreCase)) list.Add(path);
    }

    private static void AddCard(Dictionary<string, List<CardInfo>> index, string key, CardInfo card)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        if (!index.TryGetValue(key, out List<CardInfo>? list)) index[key] = list = new List<CardInfo>();
        list.Add(card);
    }

    private static void AddInbound(Dictionary<string, HashSet<string>> inbound, string target, string source)
    {
        if (!inbound.TryGetValue(target, out HashSet<string>? set)) inbound[target] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        set.Add(source);
    }

    private static string Label(string root, XmlSource source) => source.Card?.FileName ?? Path.GetRelativePath(root, source.SourcePath);
    private static XElement? Child(XElement parent, string name) => parent.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
    private static string Attribute(XElement? element, string name) => element?.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value.Trim() ?? string.Empty;

    private sealed record XmlSource(string SourcePath, string RawXml, CardInfo? Card);
    private sealed record CardInfo(string FileName, string ArtId, string MultiverseId, bool IsToken, string SourcePath);
}
