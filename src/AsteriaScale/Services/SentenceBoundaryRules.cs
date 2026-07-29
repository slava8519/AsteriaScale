namespace AsteriaScale.Services;

/// <summary>
/// Shared utterance-boundary rules for OCR context and phrase translation.
/// Consumers keep their own whitespace, quote and length-budget policies.
/// </summary>
internal static class SentenceBoundaryRules
{
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
            return true;
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
