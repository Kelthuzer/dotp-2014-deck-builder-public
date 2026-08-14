using System.Xml.Linq;

namespace DeckBuilder.GameData;

internal static class TextPermanentTableParser
{
    public static IReadOnlyDictionary<string, string> ParsePreferred(string xml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);

        XDocument document = XDocument.Parse(xml, LoadOptions.None);
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);

        foreach (XElement table in document.Descendants().Where(element =>
                     element.Name.LocalName.Equals("Table", StringComparison.OrdinalIgnoreCase)))
        {
            XElement[] rows = table.Elements().Where(element =>
                element.Name.LocalName.Equals("Row", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (rows.Length == 0)
            {
                continue;
            }

            int headerIndex = -1;
            IReadOnlyList<string?> header = Array.Empty<string?>();
            for (int index = 0; index < rows.Length; index++)
            {
                IReadOnlyList<string?> candidate = ReadRow(rows[index]);
                if (candidate.Any(value => value?.Trim().Equals("Ident", StringComparison.OrdinalIgnoreCase) == true))
                {
                    headerIndex = index;
                    header = candidate;
                    break;
                }
            }

            if (headerIndex < 0)
            {
                continue;
            }

            int identColumn = FindColumn(header, "Ident");
            int russianColumn = FindColumn(header, "Russian");
            int masterColumn = FindColumn(header, "Master Text");
            if (identColumn < 0 || (russianColumn < 0 && masterColumn < 0))
            {
                continue;
            }

            for (int rowIndex = headerIndex + 1; rowIndex < rows.Length; rowIndex++)
            {
                IReadOnlyList<string?> values = ReadRow(rows[rowIndex]);
                string id = Value(values, identColumn)?.Trim() ?? string.Empty;
                if (id.Length == 0)
                {
                    continue;
                }

                string? russian = Value(values, russianColumn);
                string? master = Value(values, masterColumn);
                string? preferred = IsUsable(russian) ? russian : IsUsable(master) ? master : null;
                if (preferred is not null)
                {
                    result[id] = preferred.Trim();
                }
            }
        }

        return result;
    }

    private static IReadOnlyList<string?> ReadRow(XElement row)
    {
        List<string?> values = new();
        int position = 0;
        foreach (XElement cell in row.Elements().Where(element =>
                     element.Name.LocalName.Equals("Cell", StringComparison.OrdinalIgnoreCase)))
        {
            XAttribute? indexAttribute = cell.Attributes().FirstOrDefault(attribute =>
                attribute.Name.LocalName.Equals("Index", StringComparison.OrdinalIgnoreCase));
            if (indexAttribute is not null
                && int.TryParse(indexAttribute.Value, out int oneBasedIndex)
                && oneBasedIndex > 0)
            {
                position = oneBasedIndex - 1;
            }

            while (values.Count <= position)
            {
                values.Add(null);
            }

            XElement? data = cell.Elements().FirstOrDefault(element =>
                element.Name.LocalName.Equals("Data", StringComparison.OrdinalIgnoreCase));
            values[position] = data?.Value ?? cell.Value;
            position++;
        }

        return values;
    }

    private static int FindColumn(IReadOnlyList<string?> header, string name)
    {
        for (int index = 0; index < header.Count; index++)
        {
            if (header[index]?.Trim().Equals(name, StringComparison.OrdinalIgnoreCase) == true)
            {
                return index;
            }
        }

        return -1;
    }

    private static string? Value(IReadOnlyList<string?> values, int index) =>
        index >= 0 && index < values.Count ? values[index] : null;

    private static bool IsUsable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Any(character =>
            !char.IsWhiteSpace(character)
            && character != '?'
            && character != '\uFFFD');
    }
}
