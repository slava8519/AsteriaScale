using System.Text.RegularExpressions;
using AsteriaScale.RankerProtocol;

namespace AsteriaScale.Services;

/// <summary>
/// One complete source sentence for contextual dictionary-sense translation. The
/// target index is relative to <see cref="Text"/>, not to the original reply.
/// </summary>
internal readonly record struct SmartPhraseContext(
    string Text,
    int CharacterIndex,
    bool Trimmed,
    CurrentSpeakerContext? CurrentSpeaker = null);

/// <summary>
/// Extracts the complete sentence containing the hovered target. Wrapped OCR
/// lines are whitespace, not sentence boundaries: trimming a long sentence
/// around the cursor made the model invent continuations for the missing half.
/// Oversized sentences fail open to the primary dictionary line instead of
/// returning a misleading partial translation.
/// </summary>
internal static partial class SmartPhraseContextBuilder
{
    internal const int MaximumWords = 64;
    internal const int MaximumCharacters = 1_000;

    public static SmartPhraseContext? Build(
        string context,
        string target,
        int characterIndex,
        CurrentSpeakerContext? currentSpeaker = null)
    {
        if (string.IsNullOrWhiteSpace(context) || string.IsNullOrWhiteSpace(target))
        {
            return null;
        }

        int anchor = RankerTargetAnchor.FindClosestOccurrence(
            context,
            target,
            characterIndex);
        if (anchor < 0)
        {
            return null;
        }

        int targetEnd = anchor + target.Length;
        (int sentenceStart, int sentenceEnd) = SentenceBoundaryRules.FindContainingSpan(
            context,
            anchor,
            targetEnd);
        while (sentenceEnd < context.Length &&
               IsClosingPunctuation(context[sentenceEnd]))
        {
            sentenceEnd++;
        }
        string sentence = context[sentenceStart..sentenceEnd];
        int sentenceAnchor = anchor - sentenceStart;
        int sentenceTargetEnd = targetEnd - sentenceStart;
        Match[] words = WordRegex()
            .Matches(sentence)
            .Cast<Match>()
            .ToArray();
        if (words.Length == 0)
        {
            return null;
        }

        int firstTargetWord = -1;
        int lastTargetWord = -1;
        for (int index = 0; index < words.Length; index++)
        {
            Match word = words[index];
            if (word.Index >= sentenceTargetEnd ||
                sentenceAnchor >= word.Index + word.Length)
            {
                continue;
            }

            if (firstTargetWord < 0)
            {
                firstTargetWord = index;
            }

            lastTargetWord = index;
        }

        // Punctuation-only targets and oversized complete sentences are not
        // suitable for this phase. Returning null leaves the dictionary answer
        // intact; slicing here would contradict the full-sentence UI contract.
        if (firstTargetWord < 0)
        {
            return null;
        }

        // Keep both sentence edges and their punctuation. Question and
        // exclamation marks are semantic input, not cosmetic OCR noise.
        int start = SkipLeadingWhitespace(context, sentenceStart, sentenceEnd);
        int end = SkipTrailingWhitespace(context, sentenceStart, sentenceEnd);
        string phrase = context[start..end];
        if (phrase.Length > MaximumCharacters)
        {
            return null;
        }

        int localAnchor = anchor - start;
        if (SpeakerMetadataParser.TryFindLeadingSpeaker(
                phrase,
                localAnchor,
                out CurrentSpeakerContext? parsedSpeaker,
                out int dialogueStart))
        {
            phrase = phrase[dialogueStart..];
            localAnchor -= dialogueStart;
            if (currentSpeaker is null ||
                !string.Equals(
                    currentSpeaker.Name,
                    parsedSpeaker?.Name,
                    StringComparison.OrdinalIgnoreCase))
            {
                currentSpeaker = parsedSpeaker;
            }
        }

        int dialogueTextStart = FindTextStartAfterUnmatchedOpeningQuote(phrase);
        if (dialogueTextStart > 0)
        {
            phrase = phrase[dialogueTextStart..];
            localAnchor -= dialogueTextStart;
        }

        if (WordRegex().Matches(phrase).Count > MaximumWords ||
            localAnchor < 0 ||
            localAnchor + target.Length > phrase.Length ||
            !string.Equals(
                phrase.Substring(localAnchor, target.Length),
                target,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new SmartPhraseContext(
            phrase,
            localAnchor,
            Trimmed: start > 0 || end < context.Length,
            CurrentSpeaker: currentSpeaker);
    }

    internal static int FindDialogueStartAfterSpeakerMetadata(
        string phrase,
        int targetIndex) =>
        SpeakerMetadataParser.TryFindLeadingSpeaker(
            phrase,
            targetIndex,
            out _,
            out int dialogueStart)
            ? dialogueStart
            : 0;

    private static int FindTextStartAfterUnmatchedOpeningQuote(string phrase)
    {
        if (phrase.Length == 0)
        {
            return 0;
        }

        bool unmatchedAscii = phrase[0] == '"' &&
                              (phrase.Count(character => character == '"') & 1) != 0;
        bool unmatchedCurly = phrase[0] == '\u201c' &&
                              !phrase.Contains('\u201d');
        if (!unmatchedAscii && !unmatchedCurly)
        {
            return 0;
        }

        int start = 1;
        while (start < phrase.Length && char.IsWhiteSpace(phrase[start]))
        {
            start++;
        }

        return start;
    }

    private static bool IsClosingPunctuation(char value) =>
        value is '"' or '\'' or '\u2019' or '\u201d' or ')' or ']' or '}';

    private static int SkipLeadingWhitespace(string text, int start, int end)
    {
        while (start < end && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        return start;
    }

    private static int SkipTrailingWhitespace(string text, int start, int end)
    {
        while (end > start && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        return end;
    }

    [GeneratedRegex(
        @"[\p{L}\p{M}\p{N}]+(?:['’\-][\p{L}\p{M}\p{N}]+)*",
        RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();
}
