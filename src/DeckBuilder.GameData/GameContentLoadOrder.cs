using System.Text;
using System.Xml.Linq;
using Gibbed.Duels.FileFormats;

namespace DeckBuilder.GameData;

internal static class GameContentLoadOrder
{
    public static int Read(string path)
    {
        try
        {
            byte[] header = Directory.Exists(path)
                ? ReadDirectoryHeader(path)
                : ReadWadHeader(path);
            return ReadHeader(header);
        }
        catch
        {
            // Order is advisory for catalog precedence. A malformed header must not make an
            // otherwise readable card source disappear from the editor.
            return 0;
        }
    }

    internal static int ReadHeader(byte[] header)
    {
        if (header.Length == 0)
            return 0;

        try
        {
            XDocument document = XDocument.Parse(DecodeText(header));
            XElement? entry = document.Descendants().FirstOrDefault(element =>
                element.Name.LocalName.Equals("ENTRY", StringComparison.OrdinalIgnoreCase)
                && Attribute(element, "platform").Equals("ALL", StringComparison.OrdinalIgnoreCase));
            return entry is not null && int.TryParse(Attribute(entry, "order"), out int order)
                ? order
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static byte[] ReadDirectoryHeader(string directory)
    {
        string? headerPath = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(file => Path.GetFileName(file).Equals("HEADER.XML", StringComparison.OrdinalIgnoreCase));
        return headerPath is null ? Array.Empty<byte>() : File.ReadAllBytes(headerPath);
    }

    private static byte[] ReadWadHeader(string path)
    {
        using FileStream input = File.OpenRead(path);
        if (WadFile.IsBadHeader(input, out _, out _, out _))
            return Array.Empty<byte>();

        input.Position = 0;
        WadFile archive = new();
        archive.Deserialize(input);
        return archive.HeaderXml ?? Array.Empty<byte>();
    }

    private static string DecodeText(byte[] data)
    {
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            return Encoding.UTF8.GetString(data, 3, data.Length - 3);
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
            return Encoding.Unicode.GetString(data, 2, data.Length - 2);
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
        return Encoding.UTF8.GetString(data);
    }

    private static string Attribute(XElement element, string name) => element.Attributes()
        .FirstOrDefault(attribute => attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?.Value.Trim() ?? string.Empty;
}
