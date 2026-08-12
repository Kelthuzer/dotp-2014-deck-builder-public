using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using DeckBuilder.Core.Models;
using Gibbed.Duels.FileFormats;
using Gibbed.IO;
using ICSharpCode.SharpZipLib.Zip.Compression;
using ICSharpCode.SharpZipLib.Zip.Compression.Streams;
using Wad = Gibbed.Duels.FileFormats.Wad;

namespace DeckBuilder.GameData;

public sealed record WorkspaceContentBuildResult(
    string OutputPath,
    string ProvenancePath,
    UnpackedContentKind Kind,
    int PackageCount,
    int WadCount,
    int FileCount,
    long PayloadBytes,
    int DuplicateFiles,
    int ConflictingFiles,
    int SourceInstances);

public sealed record WorkspaceContentSourceRecord(
    string SelectionKey,
    string PackageName,
    string WadName,
    int WadOrder,
    string ArchivePath,
    string StoragePath,
    string Sha256,
    bool Selected,
    string Status);

public sealed record WorkspaceContentProvenanceEntry(
    string RelativePath,
    string SelectedKey,
    string SelectedPackage,
    string SelectedWad,
    int SelectedOrder,
    string SelectedSha256,
    IReadOnlyList<WorkspaceContentSourceRecord> Sources);

public sealed record WorkspaceContentProvenanceManifest(
    int FormatVersion,
    DateTime CreatedUtc,
    string WorkspaceDirectory,
    string OutputWad,
    string Kind,
    IReadOnlyList<WorkspaceContentProvenanceEntry> Entries);

/// <summary>
/// Aggregates cards or decks from every extracted DotP version package below a workspace root.
/// Every discovered source instance is preserved in a sidecar provenance manifest. The generated
/// game WAD can contain only one effective definition for a given resource/card reference, so its
/// payload follows game priority unless the UI supplies an explicit source selection.
/// </summary>
public sealed class WorkspaceContentWadBuilder
{
    private const ushort WadVersion = 0x202;
    private static readonly Wad.ArchiveFlags WadFlags = Wad.ArchiveFlags.Unknown6Observed
        | Wad.ArchiveFlags.HasDataTypes
        | Wad.ArchiveFlags.HasCompressedFiles;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static bool IsWorkspaceRoot(string sourceDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
        {
            return false;
        }

        if (File.Exists(Path.Combine(sourceDirectory, GameVersionPackageService.ManifestFileName)))
        {
            return false;
        }

        return Directory.EnumerateFiles(
                sourceDirectory,
                GameVersionPackageService.ManifestFileName,
                SearchOption.AllDirectories)
            .Any();
    }

    public Task<WorkspaceContentBuildResult> BuildAsync(
        string workspaceDirectory,
        string outputPath,
        UnpackedContentKind kind,
        int order = 50,
        IReadOnlyDictionary<string, string>? selections = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () => Build(workspaceDirectory, outputPath, kind, order, selections, cancellationToken),
            cancellationToken);
    }

    public static string CreateSelectionKey(
        string packageName,
        string wadName,
        string relativePath,
        string sha256) =>
        $"{packageName}\u001f{wadName}\u001f{relativePath}\u001f{sha256}";

    internal static string GetContentIdentity(
        string relativePath,
        string storagePath,
        UnpackedContentKind kind)
    {
        string normalized = relativePath.Replace('/', '\\');
        if (kind != UnpackedContentKind.Cards
            || !normalized.StartsWith("CARDS\\", StringComparison.OrdinalIgnoreCase)
            || !normalized.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        try
        {
            CardRecord? card = CardXmlParser.Parse(File.ReadAllText(storagePath), string.Empty);
            if (card is not null && !string.IsNullOrWhiteSpace(card.FileName))
            {
                return $"@CARD_REFERENCE:{card.FileName.Trim().ToUpperInvariant()}";
            }
        }
        catch
        {
            // Malformed/unknown XML still participates by physical path and remains diagnosable.
        }

        return normalized;
    }

    private static WorkspaceContentBuildResult Build(
        string workspaceDirectory,
        string outputPath,
        UnpackedContentKind kind,
        int order,
        IReadOnlyDictionary<string, string>? selections,
        CancellationToken cancellationToken)
    {
        string workspace = Path.GetFullPath(workspaceDirectory);
        string output = Path.GetFullPath(outputPath);
        string? outputDirectory = Path.GetDirectoryName(output);
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new DirectoryNotFoundException("The WAD output directory is missing.");
        }

        string[] manifestPaths = Directory.EnumerateFiles(
                workspace,
                GameVersionPackageService.ManifestFileName,
                SearchOption.AllDirectories)
            .OrderBy(path => Path.GetDirectoryName(path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (manifestPaths.Length == 0)
        {
            throw new InvalidDataException($"No extracted version packages were found below {workspace}.");
        }

        GameVersionPackageService packageService = new();
        List<AggregateCandidate> candidates = new();
        int wadCount = 0;

        foreach (string manifestPath in manifestPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string packageDirectory = Path.GetDirectoryName(manifestPath)!;
            DotpVersionPackageManifest manifest = packageService.ReadManifest(packageDirectory);
            string packageName = manifest.VersionName;

            foreach (DotpWadPackageManifest wad in manifest.Wads)
            {
                wadCount++;
                string wadDirectory = Path.Combine(packageDirectory, "wads", SafeDirectoryName(wad.Name));
                foreach (DotpWadFileManifest file in wad.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string? relative = ContentRelativeFromArchivePath(file.ArchivePath, kind);
                    if (relative is null)
                    {
                        continue;
                    }

                    string storagePath = Path.Combine(
                        wadDirectory,
                        file.StoragePath.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(storagePath))
                    {
                        throw new FileNotFoundException(
                            $"Extracted payload listed by '{packageName}' is missing: {file.StoragePath}",
                            storagePath);
                    }

                    string currentSha256 = HashFile(storagePath);
                    string identity = GetContentIdentity(relative, storagePath, kind);
                    candidates.Add(new AggregateCandidate(
                        identity,
                        relative,
                        storagePath,
                        file.Unknown0C,
                        packageName,
                        wad.Name,
                        wad.PrimaryOrder,
                        file.ArchivePath,
                        currentSha256));
                }
            }
        }

        Dictionary<string, AggregateCandidate> content = new(StringComparer.OrdinalIgnoreCase);
        List<WorkspaceContentProvenanceEntry> provenance = new();
        int duplicates = 0;
        int conflicts = 0;

        foreach (IGrouping<string, AggregateCandidate> group in candidates.GroupBy(
                     candidate => candidate.Identity,
                     StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AggregateCandidate[] ordered = group
                .OrderBy(candidate => candidate.Order)
                .ThenBy(candidate => candidate.WadName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.PackageName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            AggregateCandidate recommended = ordered[^1];
            AggregateCandidate winner = recommended;
            if (selections is not null && selections.TryGetValue(group.Key, out string? selectedKey))
            {
                winner = ordered.FirstOrDefault(candidate =>
                             CandidateKey(candidate).Equals(selectedKey, StringComparison.Ordinal))
                         ?? throw new InvalidDataException(
                             $"The saved source selection for '{group.Key}' no longer exists. Rescan variants and choose again.");
            }

            if (content.TryGetValue(winner.RelativePath, out AggregateCandidate? pathCollision)
                && !pathCollision.Identity.Equals(winner.Identity, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Two selected resources map to the same output path '{winner.RelativePath}'. " +
                    "Choose a different variant or rename the conflicting unpacked resource.");
            }

            content[winner.RelativePath] = winner;
            List<WorkspaceContentSourceRecord> sources = new();
            foreach (AggregateCandidate candidate in ordered)
            {
                bool selected = candidate == winner;
                string status;
                if (selected)
                {
                    status = candidate == recommended
                        ? (ordered.Length == 1 ? "Unique" : "Selected / game priority")
                        : "Selected manually";
                }
                else if (candidate.Sha256.Equals(winner.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    duplicates++;
                    status = "Identical copy";
                }
                else
                {
                    conflicts++;
                    status = "Variant / not selected";
                }

                sources.Add(new WorkspaceContentSourceRecord(
                    CandidateKey(candidate),
                    candidate.PackageName,
                    candidate.WadName,
                    candidate.Order,
                    candidate.ArchivePath,
                    candidate.Path,
                    candidate.Sha256,
                    selected,
                    status));
            }

            provenance.Add(new WorkspaceContentProvenanceEntry(
                winner.RelativePath,
                CandidateKey(winner),
                winner.PackageName,
                winner.WadName,
                winner.Order,
                winner.Sha256,
                sources));
        }

        if (content.Count == 0)
        {
            throw new InvalidDataException(
                $"No {kind.ToString().ToLowerInvariant()} content was found in the extracted packages below {workspace}.");
        }

        Directory.CreateDirectory(outputDirectory);
        string rootName = Path.GetFileNameWithoutExtension(output).ToUpperInvariant();
        byte[] header = CreateHeader(rootName, order);
        List<BuildPayload> payloads = content
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new BuildPayload(
                $"{rootName}\\DATA_ALL_PLATFORMS\\{pair.Key}",
                File.ReadAllBytes(pair.Value.Path),
                pair.Value.Unknown0C))
            .ToList();
        payloads.Add(new BuildPayload($"{rootName}\\HEADER.XML", header, 0));

        string temporary = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(output)}.{Guid.NewGuid():N}.tmp");
        try
        {
            WriteWad(temporary, header, payloads);
            ValidateWad(temporary, payloads);
            File.Move(temporary, output, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        string provenancePath = output + ".sources.json";
        WorkspaceContentProvenanceManifest sourceManifest = new(
            1,
            DateTime.UtcNow,
            workspace,
            output,
            kind.ToString(),
            provenance.OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray());
        File.WriteAllText(provenancePath, JsonSerializer.Serialize(sourceManifest, JsonOptions));

        return new WorkspaceContentBuildResult(
            output,
            provenancePath,
            kind,
            manifestPaths.Length,
            wadCount,
            content.Count,
            content.Values.Sum(value => new FileInfo(value.Path).Length),
            duplicates,
            conflicts,
            candidates.Count);
    }

    private static string CandidateKey(AggregateCandidate candidate) => CreateSelectionKey(
        candidate.PackageName,
        candidate.WadName,
        candidate.RelativePath,
        candidate.Sha256);

    private static string HashFile(string path)
    {
        using FileStream input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private static string? ContentRelativeFromArchivePath(string archivePath, UnpackedContentKind kind)
    {
        string normalized = archivePath.Replace('/', '\\');
        const string marker = "\\DATA_ALL_PLATFORMS\\";
        int index = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        string relative = normalized[(index + marker.Length)..];
        return MatchesKind(relative, kind) ? relative : null;
    }

    internal static bool MatchesKind(string relativePath, UnpackedContentKind kind)
    {
        string path = relativePath.Replace('/', '\\');
        if (kind == UnpackedContentKind.Cards)
        {
            return StartsWithDirectory(path, "CARDS")
                || StartsWithDirectory(path, "ART_ASSETS\\ILLUSTRATIONS")
                || StartsWithDirectory(path, "ART_ASSETS\\TEXTURES\\CARDS");
        }

        return StartsWithDirectory(path, "DECKS")
            || StartsWithDirectory(path, "UNLOCKS")
            || StartsWithDirectory(path, "AI_PERSONALITIES")
            || StartsWithDirectory(path, "TEXT_PERMANENT")
            || StartsWithDirectory(path, "ART_ASSETS\\TEXTURES\\DECKS");
    }

    private static bool StartsWithDirectory(string path, string directory) =>
        path.Equals(directory, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(directory + "\\", StringComparison.OrdinalIgnoreCase);

    private static byte[] CreateHeader(string rootName, int order)
    {
        XDocument document = new(
            new XDeclaration("1.0", null, null),
            new XElement("WAD_HEADER",
                new XElement("ENTRY",
                    new XAttribute("platform", "ALL"),
                    new XAttribute("source", $"{rootName}/DATA_ALL_PLATFORMS/"),
                    new XAttribute("alias", "Content"),
                    new XAttribute("order", order))));
        return Encoding.UTF8.GetBytes(document.Declaration + Environment.NewLine + document);
    }

    private static void WriteWad(string path, byte[] header, IReadOnlyList<BuildPayload> payloads)
    {
        WadFile archive = new()
        {
            Version = WadVersion,
            Flags = WadFlags,
            HeaderXml = header
        };
        List<OutputFileEntry> entries = new();
        foreach (BuildPayload payload in payloads.OrderBy(item => item.ArchivePath, StringComparer.OrdinalIgnoreCase))
        {
            (string directoryPath, string fileName) = SplitArchivePath(payload.ArchivePath);
            Wad.DirectoryEntry directory = GetOrCreateDirectory(archive, directoryPath);
            OutputFileEntry entry = new(directory, payload.Data)
            {
                Name = fileName,
                Size = 0,
                Unknown0C = payload.Unknown0C,
                OffsetIndex = archive.DataOffsets.Count,
                OffsetCount = 1
            };
            archive.DataOffsets.Add(0);
            directory.Files.Add(entry);
            entries.Add(entry);
        }

        using FileStream output = File.Create(path);
        archive.Serialize(output);
        foreach (OutputFileEntry entry in entries)
        {
            archive.DataOffsets[entry.OffsetIndex] = checked((uint)output.Position);
            using MemoryStream compressed = new();
            DeflaterOutputStream deflater = new(compressed, new Deflater(Deflater.BEST_COMPRESSION));
            deflater.Write(entry.Data, 0, entry.Data.Length);
            deflater.Finish();
            if (compressed.Length < entry.Data.Length)
            {
                entry.Size = checked((uint)(4 + compressed.Length));
                output.WriteValueU32(checked((uint)entry.Data.Length));
                compressed.Position = 0;
                compressed.CopyTo(output);
            }
            else
            {
                entry.Size = checked((uint)(4 + entry.Data.Length));
                output.WriteValueU32(uint.MaxValue);
                output.Write(entry.Data, 0, entry.Data.Length);
            }
        }

        output.Position = 0;
        archive.Serialize(output);
    }

    private static void ValidateWad(string path, IReadOnlyList<BuildPayload> expected)
    {
        using FileStream input = File.OpenRead(path);
        if (WadFile.IsBadHeader(input, out _, out _, out string reason))
        {
            throw new InvalidDataException($"Generated {Path.GetFileName(path)} has an invalid WAD header: {reason}");
        }

        input.Position = 0;
        WadFile archive = new();
        archive.Deserialize(input);
        bool compressed = (archive.Flags & Wad.ArchiveFlags.HasCompressedFiles) != 0;
        Dictionary<string, string> actual = new(StringComparer.OrdinalIgnoreCase);
        List<(string Path, Wad.FileEntry File)> files = new();
        foreach (Wad.DirectoryEntry root in archive.Directories)
        {
            CollectFiles(root, root.Name, files);
        }

        foreach ((string archivePath, Wad.FileEntry file) in files)
        {
            byte[] data = ReadFile(input, archive, file, compressed);
            actual[archivePath] = Convert.ToHexString(SHA256.HashData(data));
        }

        if (actual.Count != expected.Count)
        {
            throw new InvalidDataException(
                $"Generated {Path.GetFileName(path)} contains {actual.Count} files; {expected.Count} were expected.");
        }

        foreach (BuildPayload payload in expected)
        {
            string expectedHash = Convert.ToHexString(SHA256.HashData(payload.Data));
            if (!actual.TryGetValue(payload.ArchivePath, out string? actualHash)
                || !actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Generated {Path.GetFileName(path)} failed payload verification for {payload.ArchivePath}.");
            }
        }
    }

    private static void CollectFiles(
        Wad.DirectoryEntry directory,
        string path,
        ICollection<(string Path, Wad.FileEntry File)> output)
    {
        foreach (Wad.FileEntry file in directory.Files)
        {
            output.Add(($"{path}\\{file.Name}", file));
        }

        foreach (Wad.DirectoryEntry child in directory.Directories)
        {
            CollectFiles(child, $"{path}\\{child.Name}", output);
        }
    }

    private static byte[] ReadFile(FileStream input, WadFile archive, Wad.FileEntry file, bool compressed)
    {
        input.Position = archive.DataOffsets[file.OffsetIndex];
        if (!compressed)
        {
            return ReadExactly(input, checked((int)file.Size));
        }

        int inflatedLength = input.ReadValueS32(archive.Endian);
        int storedLength = checked((int)file.Size) - 4;
        if (inflatedLength == -1)
        {
            return ReadExactly(input, storedLength);
        }

        using MemoryStream compressedData = new(ReadExactly(input, storedLength), writable: false);
        using InflaterInputStream inflater = new(compressedData);
        return ReadExactly(inflater, inflatedLength);
    }

    private static byte[] ReadExactly(Stream input, int length)
    {
        byte[] result = new byte[length];
        int offset = 0;
        while (offset < result.Length)
        {
            int read = input.Read(result, offset, result.Length - offset);
            if (read == 0)
            {
                throw new EndOfStreamException($"Expected {length} bytes, received {offset}.");
            }

            offset += read;
        }

        return result;
    }

    private static (string DirectoryPath, string FileName) SplitArchivePath(string archivePath)
    {
        int separator = archivePath.LastIndexOf('\\');
        if (separator <= 0 || separator == archivePath.Length - 1)
        {
            throw new InvalidDataException($"Invalid archive path: {archivePath}");
        }

        return (archivePath[..separator], archivePath[(separator + 1)..]);
    }

    private static Wad.DirectoryEntry GetOrCreateDirectory(WadFile archive, string archivePath)
    {
        string[] parts = archivePath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        Wad.DirectoryEntry? current = archive.Directories.FirstOrDefault(directory =>
            directory.Name.Equals(parts[0], StringComparison.OrdinalIgnoreCase));
        if (current is null)
        {
            current = new Wad.DirectoryEntry(null) { Name = parts[0] };
            archive.Directories.Add(current);
        }

        foreach (string part in parts.Skip(1))
        {
            Wad.DirectoryEntry? child = current.Directories.FirstOrDefault(directory =>
                directory.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (child is null)
            {
                child = new Wad.DirectoryEntry(current) { Name = part };
                current.Directories.Add(child);
            }

            current = child;
        }

        return current;
    }

    private static string SafeDirectoryName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string result = new(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return result.Trim().TrimEnd('.');
    }

    private sealed record AggregateCandidate(
        string Identity,
        string RelativePath,
        string Path,
        uint Unknown0C,
        string PackageName,
        string WadName,
        int Order,
        string ArchivePath,
        string Sha256);

    private sealed record BuildPayload(string ArchivePath, byte[] Data, uint Unknown0C);

    private sealed class OutputFileEntry : Wad.FileEntry
    {
        public OutputFileEntry(Wad.DirectoryEntry directory, byte[] data)
            : base(directory)
        {
            Data = data;
        }

        public byte[] Data { get; }
    }
}
