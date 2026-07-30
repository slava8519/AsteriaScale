namespace AsteriaScale.Services;

/// <summary>
/// Shared utterance-boundary rules for OCR context and phrase translation.
/// Consumers keep their own whitespace, quote and length-budget policies.
/// </summary>
internal static class SentenceBoundaryRules
{
    private static readonly HashSet<string> HonorificAbbreviations = new(
        [
            "adm", "capt", "cmdr", "col", "cpt", "det", "dr", "fr", "gen",
            "gov", "hon", "insp", "lt", "mr", "mrs", "ms", "mx", "pres",
            "prof", "rep", "rev", "sen", "sgt", "supt"
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> NumericLabelAbbreviations = new(
        [
            "eq", "fig", "no", "nos", "ref", "sec", "vol"
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> LexicalContinuationAbbreviations = new(
        [
            "a.k.a", "cf", "e.g", "i.e", "vs"
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ContextualAbbreviations = new(
        [
            "a.m", "approx", "dept", "est", "etc", "p.m", "pp"
        ],
        StringComparer.OrdinalIgnoreCase);

    internal static (int Start, int End) FindContainingSpan(
        string text,
        int contentStart,
        int contentEnd)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (contentStart < 0 ||
            contentStart > text.Length ||
            contentEnd < contentStart ||
            contentEnd > text.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(contentStart),
                "The selected content span is outside the source text.");
        }

        int start = 0;
        for (int index = contentStart - 1; index >= 0; index--)
        {
            if (!IsSentenceTerminator(text[index]))
            {
                continue;
            }

            int runStart = TerminatorRunStart(text, index);
            int runEnd = TerminatorRunEnd(text, runStart);
            if (!ClosesUtterance(text, runStart, runEnd))
            {
                // Skip the complete punctuation run so another dot from the
                // same hesitation cannot become a contradictory boundary.
                index = runStart;
                continue;
            }

            start = runEnd;
            break;
        }

        int end = text.Length;
        for (int index = contentEnd; index < text.Length; index++)
        {
            if (!IsSentenceTerminator(text[index]))
            {
                continue;
            }

            int runStart = TerminatorRunStart(text, index);
            int runEnd = TerminatorRunEnd(text, runStart);
            if (!ClosesUtterance(text, runStart, runEnd))
            {
                index = runEnd - 1;
                continue;
            }

            end = runEnd;
            break;
        }

        return (start, end);
    }

    private static int TerminatorRunStart(string text, int index)
    {
        int start = index;
        while (start > 0 && IsSentenceTerminator(text[start - 1]))
        {
            start--;
        }

        return start;
    }

    private static int TerminatorRunEnd(string text, int index)
    {
        int end = index + 1;
        while (end < text.Length && IsSentenceTerminator(text[end]))
        {
            end++;
        }

        return end;
    }

    private static bool ClosesUtterance(string text, int runStart, int runEnd)
    {
        if (!IsEllipsisRun(text, runStart, runEnd))
        {
            return !IsNonTerminalPeriod(text, runStart, runEnd);
        }

        int next = runEnd;
        while (next < text.Length && char.IsWhiteSpace(text[next]))
        {
            next++;
        }

        return next >= text.Length ||
               char.IsUpper(text[next]) ||
               IsOpeningQuote(text[next]);
    }

    private static bool IsNonTerminalPeriod(string text, int runStart, int runEnd)
    {
        if (runEnd - runStart != 1 || text[runStart] != '.')
        {
            return false;
        }

        int previous = runStart - 1;
        if (previous < 0)
        {
            return false;
        }

        // Decimal points and the internal dots of compact abbreviations are
        // never utterance boundaries. The final dot is handled below.
        if (runEnd < text.Length &&
            ((char.IsDigit(text[previous]) && char.IsDigit(text[runEnd])) ||
             (char.IsLetter(text[previous]) &&
              char.IsLetter(text[runEnd]) &&
              (char.IsLower(text[runEnd]) ||
               (runEnd + 1 < text.Length && text[runEnd + 1] == '.')))))
        {
            return true;
        }

        int next = FindNextContentCharacter(text, runEnd);
        if (next >= text.Length)
        {
            return false;
        }

        string abbreviation = ReadAbbreviationBefore(text, runStart);
        if (abbreviation.Length == 0)
        {
            return false;
        }

        if (HonorificAbbreviations.Contains(abbreviation))
        {
            return char.IsUpper(text[next]);
        }

        if (NumericLabelAbbreviations.Contains(abbreviation))
        {
            return char.IsDigit(text[next]);
        }

        return (LexicalContinuationAbbreviations.Contains(abbreviation) ||
                ContextualAbbreviations.Contains(abbreviation)) &&
               (char.IsLower(text[next]) || char.IsDigit(text[next]));
    }

    private static int FindNextContentCharacter(string text, int start)
    {
        int next = start;
        while (next < text.Length &&
               (char.IsWhiteSpace(text[next]) || text[next] is '(' or '[' or '{'))
        {
            next++;
        }

        return next;
    }

    private static string ReadAbbreviationBefore(string text, int periodIndex)
    {
        int start = periodIndex;
        while (start > 0 &&
               (char.IsLetter(text[start - 1]) || text[start - 1] == '.'))
        {
            start--;
        }

        return text[start..periodIndex].Trim('.');
    }

    private static bool IsEllipsisRun(string text, int runStart, int runEnd)
    {
        bool ellipsis = false;
        for (int index = runStart; index < runEnd; index++)
        {
            if (text[index] is not ('.' or '…'))
            {
                return false;
            }

            ellipsis |= text[index] == '…' || runEnd - runStart > 1;
        }

        return ellipsis;
    }

    private static bool IsOpeningQuote(char character) =>
        character is '"' or '‘' or '“' or '«' or '「' or '『';

    private static bool IsSentenceTerminator(char character) =>
        character is '.' or '!' or '?' or '…' or '\u3002' or '\uFF01' or '\uFF1F';
}
