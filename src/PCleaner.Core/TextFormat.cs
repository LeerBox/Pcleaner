namespace PCleaner.Core;

/// <summary>Small helpers for user-facing English text so the UI never shows "1 items" or a parenthesised plural.</summary>
public static class TextFormat
{
    /// <summary>"1 item", "22 items", "1 entry", "3 entries" — thousands separators included.</summary>
    public static string Count(long count, string singular, string? plural = null)
    {
        var noun = count == 1 ? singular : plural ?? Pluralize(singular);
        return $"{count:N0} {noun}";
    }

    /// <summary>Regular English plurals: item → items, entry → entries, process → processes.</summary>
    public static string Pluralize(string singular)
    {
        if (singular.EndsWith('y') && singular.Length > 1 && !"aeiou".Contains(singular[^2]))
        {
            return singular[..^1] + "ies";
        }

        if (singular.EndsWith('s') || singular.EndsWith('x') || singular.EndsWith("ch", StringComparison.Ordinal) || singular.EndsWith("sh", StringComparison.Ordinal))
        {
            return singular + "es";
        }

        return singular + "s";
    }
}