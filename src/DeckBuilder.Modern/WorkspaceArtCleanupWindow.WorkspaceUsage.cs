using System.IO;
using System.Xml.Linq;
using DeckBuilder.Core.Models;
using DeckBuilder.GameData;

namespace DeckBuilder.Modern;

public partial class WorkspaceArtCleanupWindow
{
    public WorkspaceArtCleanupWindow(string workspaceDirectory, GameCardImageLoader? imageLoader)
        : this(workspaceDirectory, ReadWorkspaceCardUsage(workspaceDirectory), imageLoader)
    {
    }

    private static IReadOnlyList<CardRecord> ReadWorkspaceCardUsage(string workspaceDirectory)
    {
        List<CardRecord> cards = new();
        if (!Directory.Exists(workspaceDirectory))
            return cards;

        foreach (string path in Directory.EnumerateFiles(workspaceDirectory, "*.xml", System.IO.SearchOption.AllDirectories))
        {
            string normalized = path.Replace('/', '\\');
            if (!normalized.Contains("\\DATA_ALL_PLATFORMS\\CARDS\\", StringComparison.OrdinalIgnoreCase)
                && !normalized.Contains("\\CARDS\\", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                XDocument document = XDocument.Load(path, LoadOptions.None);
                XElement? card = document.Root?.Name.LocalName.Equals("CARD_V2", StringComparison.OrdinalIgnoreCase) == true
                    ? document.Root
                    : document.Descendants().FirstOrDefault(element =>
                        element.Name.LocalName.Equals("CARD_V2", StringComparison.OrdinalIgnoreCase));
                if (card is null)
                    continue;

                string fileName = ChildAttribute(card, "FILENAME", "text");
                string artId = ChildAttribute(card, "ARTID", "value");
                if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(artId))
                    continue;

                cards.Add(new CardRecord(
                    fileName,
                    fileName,
                    fileName,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    path,
                    artId,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    false));
            }
            catch
            {
                // Usage analysis is best-effort; one malformed XML must not block the cleanup window.
            }
        }

        return cards;
    }

    private static string ChildAttribute(XElement parent, string childName, string attributeName)
    {
        XElement? child = parent.Elements().FirstOrDefault(element =>
            element.Name.LocalName.Equals(childName, StringComparison.OrdinalIgnoreCase));
        return child?.Attributes().FirstOrDefault(attribute =>
            attribute.Name.LocalName.Equals(attributeName, StringComparison.OrdinalIgnoreCase))?.Value.Trim()
            ?? string.Empty;
    }
}
