namespace DeckBuilder.GameData;

public sealed record CardReferenceScanRow(
    string FileName,
    string ArtId,
    string MultiverseId,
    bool IsToken,
    int InboundReferenceCount,
    string UsedBy,
    bool ArtFound,
    string Source,
    string ArtPath,
    string ArtCandidates,
    string ArtMatches);

public sealed record CardReferenceScanResult(
    IReadOnlyList<CardReferenceScanRow> Rows,
    int XmlFiles,
    int CardRecords,
    int TdxFiles,
    int ParseFailures);
