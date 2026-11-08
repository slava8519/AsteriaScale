using System.Globalization;
using System.Text;

namespace AsteriaScale.Services;

/// <summary>
/// Normalizes cache-key text according to the pinned BERT tokenizer's
/// clean_text and whitespace-boundary behavior. Case folding and accent
/// handling are left to each caller because the two caches have different
/// identifier contracts.
/// </summary>
internal static class RankerCacheTextNormalizer
{
    /// <summary>
    /// Preserves the exact UTF-16 text supplied to a tokenizer whose
    /// normalization contract is not BERT clean_text. This deliberately does
    /// not apply Unicode normalization, case folding, whitespace folding or
    /// control removal: an extra cache miss is safe, while merging two inputs
    /// that a model can distinguish is not.
    /// </summary>
    internal static string PreserveExact(string? value) => value ?? string.Empty;

    internal static string Normalize(string? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        string normalized = value.Normalize(NormalizationForm.FormC);
        StringBuilder result = new(normalized.Length);
        bool pendingSpace = false;
        foreach (Rune rune in normalized.EnumerateRunes())
        {
            if (IsBertWhitespace(rune))
            {
                pendingSpace = result.Length > 0;
                continue;
            }

            if (IsBertControl(rune))
            {
                continue;
            }

            if (pendingSpace)
            {
                result.Append(' ');
                pendingSpace = false;
            }

            result.Append(rune.ToString());
        }

        return result.ToString();
    }

    private static bool IsBertWhitespace(Rune rune) =>
        // clean_text itself maps Zs; Zl/Zp survive that stage but the pinned
        // BertPreTokenizer immediately splits them as whitespace. Mapping all
        // three separator categories therefore preserves final token identity.
        rune.Value is 0x0009 or 0x000A or 0x000D ||
        Rune.GetUnicodeCategory(rune) is
            UnicodeCategory.SpaceSeparator or
            UnicodeCategory.LineSeparator or
            UnicodeCategory.ParagraphSeparator;

    private static bool IsBertControl(Rune rune)
    {
        if (rune.Value is 0x0000 or 0xFFFD)
        {
            return true;
        }

        return Rune.GetUnicodeCategory(rune) is
            UnicodeCategory.Control or
            UnicodeCategory.Format or
            UnicodeCategory.Surrogate or
            UnicodeCategory.PrivateUse or
            UnicodeCategory.OtherNotAssigned;
    }
}
