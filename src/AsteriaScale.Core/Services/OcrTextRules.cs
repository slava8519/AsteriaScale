using Drawing = System.Drawing;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AsteriaScale.RankerProtocol;

namespace AsteriaScale.Services;

/// <summary>
/// The unit-free half of hover OCR, moved verbatim from the Windows AI
/// recognizer (owner decision M12): text clean-up with the mixed-script
/// repair, sentence extraction and the completeness gate, the agreement of two
/// observations and the unchanged-frame reuse rule. They live in Core so that
/// every text source applies the same rules: the Windows recognizer imports
/// them with <c>using static</c>, and a macOS host can build its hover
/// selection with them instead of keeping a second copy. Line geometry, capture
/// bounds and their pixel thresholds are the other half, in
/// <see cref="OcrGeometry"/> (roadmap 20.3; docs/MACOS.md T13 and T18).
/// </summary>
internal static partial class OcrTextRules
{
    internal static readonly TimeSpan HoverCacheLifetime = TimeSpan.FromSeconds(30);

    internal static HoverOcrResult? StabilizeSentenceContext(
        HoverOcrResult? first,
        HoverOcrResult? second,
        out bool useSecondObservation)
    {
        useSecondObservation = first is null;
        if (first is null)
        {
            return second is null
                ? null
                : second with { IsSentenceContextStable = false };
        }

        if (second is null ||
            !string.Equals(
                LocalDictionaryService.NormalizeEnglish(first.HoveredWord),
                LocalDictionaryService.NormalizeEnglish(second.HoveredWord),
                StringComparison.Ordinal))
        {
            useSecondObservation = false;
            return first with { IsSentenceContextStable = false };
        }

        string[] firstTokens = SentenceComparison.Tokens(first.Text);
        string[] secondTokens = SentenceComparison.Tokens(second.Text);
        if (firstTokens.Length == 0 || secondTokens.Length == 0)
        {
            useSecondObservation = false;
            return first with { IsSentenceContextStable = false };
        }

        bool firstFitsSecond = SentenceComparison.IsOrderedSubsequence(firstTokens, secondTokens);
        bool secondFitsFirst = SentenceComparison.IsOrderedSubsequence(secondTokens, firstTokens);
        if (!firstFitsSecond && !secondFitsFirst)
        {
            useSecondObservation = false;
            return first with { IsSentenceContextStable = false };
        }

        useSecondObservation = firstFitsSecond &&
                               (!secondFitsFirst || second.Text.Length > first.Text.Length);
        HoverOcrResult selected = useSecondObservation ? second : first;
        return selected with
        {
            IsSentenceContextStable = LooksLikeCompleteOcrSentence(selected.Text),
            CurrentSpeaker = StableObservedSpeaker(first, second, selected)
        };
    }

    /// <summary>
    /// Re-applies the per-selection gates to a result drawn from a cached
    /// recognition. The cache carries frame-level agreement only — the two
    /// captures behind it described the same words — while completeness belongs
    /// to the one sentence the cursor happened to pick, and a nameplate is only
    /// accepted once two observations reported the same name.
    ///
    /// Both must therefore be re-decided on every hover over the same frame.
    /// A static VN frame is the common case, not an edge: the picture stops
    /// changing exactly while the reader looks around it, so a fingerprint hit
    /// covered every hover after the first. Those hovers selected other lines
    /// and reported them as fully stable, which let a sentence clipped by the
    /// capture rectangle reach the phrase model and could resurrect a nameplate
    /// that <see cref="StableObservedSpeaker"/> had refused.
    /// </summary>
    internal static HoverOcrResult? ApplyConfirmedFrameGates(
        HoverOcrResult? selection,
        string? confirmedSpeakerName) =>
        selection is null
            ? null
            : selection with
            {
                IsSentenceContextStable = LooksLikeCompleteOcrSentence(selection.Text),
                CurrentSpeaker = confirmedSpeakerName is not null &&
                                 string.Equals(
                                     selection.CurrentSpeaker?.Name,
                                     confirmedSpeakerName,
                                     StringComparison.Ordinal)
                    ? selection.CurrentSpeaker
                    : null
            };

    internal static CurrentSpeakerContext? StableObservedSpeaker(
        HoverOcrResult first,
        HoverOcrResult second,
        HoverOcrResult selected)
    {
        if (first.CurrentSpeaker is null ||
            second.CurrentSpeaker is null ||
            !string.Equals(
                first.CurrentSpeaker.Name,
                second.CurrentSpeaker.Name,
                StringComparison.Ordinal))
        {
            return null;
        }

        return selected.CurrentSpeaker ?? first.CurrentSpeaker;
    }

    [GeneratedRegex(
        @"\b(I|You|He|She|It|We|They)['\u2019][1Il]{2}\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex OcrFutureContractionRegex();

    internal static SentenceContext ExtractSentence(string text, int characterIndex)
    {
        CurrentSpeakerContext? currentSpeaker = null;
        if (SpeakerMetadataParser.TryFindLeadingSpeaker(
                text,
                characterIndex,
                out CurrentSpeakerContext? rawSpeaker,
                out int rawDialogueStart))
        {
            text = text[rawDialogueStart..];
            characterIndex -= rawDialogueStart;
            currentSpeaker = rawSpeaker;
        }

        string normalized = Normalize(text);
        if (currentSpeaker is null &&
            SpeakerMetadataParser.TryFindLeadingSpeaker(
                normalized,
                characterIndex,
                out CurrentSpeakerContext? normalizedSpeaker,
                out int normalizedDialogueStart))
        {
            normalized = normalized[normalizedDialogueStart..];
            characterIndex -= normalizedDialogueStart;
            currentSpeaker = normalizedSpeaker;
        }

        text = StripUnmatchedDialogueQuote(normalized);
        if (normalized.Length > text.Length && normalized.EndsWith(text, StringComparison.Ordinal))
        {
            characterIndex -= normalized.Length - text.Length;
        }

        if (text.Length == 0)
        {
            return new SentenceContext(string.Empty, 0);
        }

        int anchor = Math.Clamp(characterIndex, 0, text.Length - 1);
        (int start, int end) = SentenceBoundaryRules.FindContainingSpan(
            text,
            anchor,
            anchor);
        while (end < text.Length && IsClosingQuote(text[end]))
        {
            end++;
        }

        while (start < end && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        while (end > start && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        string sentence = text[start..end];

        return new SentenceContext(
            sentence,
            sentence.Length == 0 ? 0 : Math.Clamp(anchor - start, 0, sentence.Length - 1))
        {
            CurrentSpeaker = currentSpeaker
        };
    }

    internal static bool IsClosingQuote(char character) =>
        character is '"' or '\'' or '\u2019' or '\u201D' or '\u00BB' or '\u300D' or '\u300F' or '\u3011';

    internal static bool LooksLikeCompleteOcrSentence(string value)
    {
        string normalized = StripUnmatchedDialogueQuote(Normalize(value));
        if (normalized.Length == 0)
        {
            return false;
        }

        int end = normalized.Length - 1;
        while (end >= 0 &&
               (IsClosingQuote(normalized[end]) || normalized[end] is ')' or ']' or '}'))
        {
            end--;
        }

        if (end < 0 || normalized[end] is not ('.' or '!' or '?' or '\u2026' or '\u3002' or '\uFF01' or '\uFF1F'))
        {
            return false;
        }

        int start = SmartPhraseContextBuilder.FindDialogueStartAfterSpeakerMetadata(
            normalized,
            normalized.Length);
        while (start <= end &&
               (char.IsWhiteSpace(normalized[start]) ||
                normalized[start] is '"' or '\'' or '\u2018' or '\u201C' or '\u00AB' or
                    '(' or '[' or '{' or '-' or '\u2014' or '.' or '\u2026'))
        {
            start++;
        }

        if (start > end)
        {
            return false;
        }

        char firstCharacter = normalized[start];
        return char.IsDigit(firstCharacter) ||
               char.IsUpper(firstCharacter) ||
               (char.IsLetter(firstCharacter) && firstCharacter is not (>= 'a' and <= 'z'));
    }

    /// <summary>
    /// Maps the cursor onto a character index inside the recognized line. OCR
    /// reports the word under the cursor and its box but not where that word
    /// sits in the line string, so the occurrence has to be resolved by text
    /// and only then refined by the cursor's position inside the word box.
    /// Without a usable occurrence the proportional estimate stays, which is
    /// the long-standing degradation path for lines whose word texts do not
    /// reassemble into the line text.
    /// </summary>
    internal static int ResolveLineCharacterIndex(
        string text,
        string hoveredWord,
        double cursorX,
        double lineLeft,
        double lineRight,
        double wordLeft,
        double wordRight)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        int approximateIndex = Math.Clamp(
            (int)(((cursorX - lineLeft) / Math.Max(1, lineRight - lineLeft)) * text.Length),
            0,
            text.Length - 1);
        // One resolver owns "which occurrence of this surface is meant" for the
        // whole app. The private copy that used to live here matched on raw
        // IndexOf, so a short word could anchor inside a longer one — "in"
        // inside "waiting" — and LocalDictionaryService.FindClosestToken then
        // handed the dictionary the enclosing token instead of the hovered one.
        int wordStart = RankerTargetAnchor.FindClosestOccurrence(
            text,
            hoveredWord,
            approximateIndex);
        if (wordStart < 0)
        {
            return approximateIndex;
        }

        double wordRatio = wordRight > wordLeft
            ? Math.Clamp((cursorX - wordLeft) / (wordRight - wordLeft), 0, 0.999)
            : 0;
        int wordOffset = hoveredWord.Length == 0
            ? 0
            : Math.Clamp((int)(wordRatio * hoveredWord.Length), 0, hoveredWord.Length - 1);
        return wordStart + wordOffset;
    }

    internal static ulong ComputeFrameFingerprint(ReadOnlySpan<byte> pixels)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(pixels, digest);
        return BinaryPrimitives.ReadUInt64LittleEndian(digest);
    }

    internal static bool CanReuseCachedRecognition(
        Drawing.Rectangle cachedBounds,
        ulong cachedFingerprint,
        DateTimeOffset cachedAt,
        Drawing.Rectangle currentBounds,
        ulong currentFingerprint,
        DateTimeOffset now) =>
        cachedBounds == currentBounds &&
        cachedFingerprint == currentFingerprint &&
        now >= cachedAt &&
        now - cachedAt <= HoverCacheLifetime;

    internal static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string normalized = RepairMixedScriptText(
            string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        normalized = OcrFutureContractionRegex().Replace(
            normalized,
            match => match.Groups[1].Value + "'ll");
        return normalized;
    }

    internal static string StripUnmatchedDialogueQuote(string value)
    {
        int asciiQuoteCount = value.Count(character => character == '"');
        if ((asciiQuoteCount & 1) != 0)
        {
            if (value.StartsWith('"'))
            {
                return value[1..].TrimStart();
            }

            if (value.EndsWith('"'))
            {
                return value[..^1].TrimEnd();
            }
        }

        if (value.StartsWith('\u201C') && !value.EndsWith('\u201D'))
        {
            return value[1..].TrimStart();
        }

        if (value.EndsWith('\u201D') && !value.StartsWith('\u201C'))
        {
            return value[..^1].TrimEnd();
        }

        return value;
    }

    internal static string NormalizeLong(string? text, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string normalized = RepairMixedScriptText(
            string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }

    private static readonly Dictionary<char, char> CyrillicToLatinHomoglyphs = new()
    {
        ['а'] = 'a',
        ['е'] = 'e',
        ['о'] = 'o',
        ['р'] = 'p',
        ['с'] = 'c',
        ['у'] = 'y',
        ['х'] = 'x',
        ['к'] = 'k',
        ['і'] = 'i',
        ['ѕ'] = 's',
        ['А'] = 'A',
        ['В'] = 'B',
        ['Е'] = 'E',
        ['К'] = 'K',
        ['М'] = 'M',
        ['Н'] = 'H',
        ['О'] = 'O',
        ['Р'] = 'P',
        ['С'] = 'C',
        ['Т'] = 'T',
        ['У'] = 'Y',
        ['Х'] = 'X'
    };

    private static readonly Dictionary<char, char> LatinToCyrillicHomoglyphs = new()
    {
        ['a'] = 'а',
        ['e'] = 'е',
        ['o'] = 'о',
        ['p'] = 'р',
        ['c'] = 'с',
        ['y'] = 'у',
        ['x'] = 'х',
        ['k'] = 'к',
        ['A'] = 'А',
        ['B'] = 'В',
        ['E'] = 'Е',
        ['K'] = 'К',
        ['M'] = 'М',
        ['H'] = 'Н',
        ['O'] = 'О',
        ['P'] = 'Р',
        ['C'] = 'С',
        ['T'] = 'Т',
        ['X'] = 'Х'
    };

    /// <summary>
    /// OCR on stylized fonts sometimes emits homoglyphs from the other script
    /// ("prancing аround" with a Cyrillic а breaks every dictionary key).
    /// Only tokens that mix Latin and Cyrillic are repaired, toward the
    /// dominant script; pure-Latin and pure-Cyrillic tokens are never touched.
    /// </summary>
    internal static string RepairMixedScriptText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        char[] result = text.ToCharArray();
        int index = 0;
        while (index < result.Length)
        {
            if (!char.IsLetter(result[index]))
            {
                index++;
                continue;
            }

            int start = index;
            int latin = 0;
            int cyrillic = 0;
            while (index < result.Length && char.IsLetter(result[index]))
            {
                char character = result[index];
                if (character is >= 'a' and <= 'z' or >= 'A' and <= 'Z')
                {
                    latin++;
                }
                else if (character is >= 'Ѐ' and <= 'ӿ')
                {
                    cyrillic++;
                }

                index++;
            }

            if (latin == 0 || cyrillic == 0)
            {
                continue;
            }

            Dictionary<char, char> map = latin >= cyrillic
                ? CyrillicToLatinHomoglyphs
                : LatinToCyrillicHomoglyphs;
            for (int position = start; position < index; position++)
            {
                if (map.TryGetValue(result[position], out char repaired))
                {
                    result[position] = repaired;
                }
            }
        }

        return new string(result);
    }

    internal static bool LooksTranslatable(string text) =>
        text.Length > 0 && text.Any(char.IsLetter);
}
