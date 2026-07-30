namespace AsteriaScale.Services;

internal static class DictionaryMeaningParser
{
    internal static string[] SplitMeanings(string translation) => translation
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(Normalize)
        // A lone ellipsis is the dictionary's truncation marker, not a rankable meaning.
        .Where(value => value.Length > 0 && value != "…")
        .Distinct(StringComparer.CurrentCultureIgnoreCase)
        .Take(8)
        .ToArray();

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
}
