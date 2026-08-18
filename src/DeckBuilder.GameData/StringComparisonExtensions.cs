namespace DeckBuilder.GameData;

internal static class StringComparisonExtensions
{
    /// <summary>
    /// Character overload matching the string StartsWith(..., StringComparison) shape.
    /// Useful when the caller already specifies the comparison mode explicitly.
    /// </summary>
    internal static bool StartsWith(this string value, char prefix, StringComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0)
            return false;

        return string.Equals(value[..1], prefix.ToString(), comparison);
    }
}
