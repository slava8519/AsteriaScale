using System.Globalization;
using System.Text;

namespace AsteriaScale.Services;

internal enum SpeakerGender
{
    Unknown,
    Masculine,
    Feminine,
    Ignore
}

internal sealed record CurrentSpeakerContext(
    string Name,
    SpeakerGender Gender = SpeakerGender.Unknown,
    string GameKey = "");

/// <summary>
/// Separates a bounded speaker label from current dialogue without treating
/// arbitrary preceding prose as identity metadata. Gender resolution is a
/// separate, game-scoped concern; extraction always starts as unknown.
/// </summary>
internal static class SpeakerMetadataParser
{
    internal const int MaximumNameLength = 48;
    private const int MaximumNameWords = 4;

    // Interface chrome and bare interjections are never speakers. This applies
    // to every branch: a colon is not evidence of a nameplate, so "Warning:"
    // used to enter the per-game speaker store exactly like a character would.
    private static readonly HashSet<string> UiOrUtteranceWords = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "attention", "auto", "back", "caution", "chapter", "close", "config",
        "error", "example", "goal", "hello", "hey", "hint", "history", "info",
        "item", "level", "load", "location", "menu", "mission", "no", "note",
        "notice", "objective", "ok", "okay", "please", "quest", "save", "score",
        "settings", "skip", "sorry", "status", "stop", "thanks", "tip",
        "tutorial", "wait", "warning", "yes"
    };

    private static readonly HashSet<string> NonSpeakerLeadingWords = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "he", "here", "i", "it", "she", "that", "these", "they", "this",
        "those", "there", "we", "you"
    };

    /// <summary>
    /// Finite attribution verbs, the words that turn a label into a reporting
    /// clause ("Mom always said:"). Deliberately narrower than
    /// <see cref="EnglishContextLexicon.ComplementTakingVerbs"/>: that set holds
    /// base forms such as "hope" and "feel", and Hope is a person's name. Only
    /// forms that cannot also head an English name belong here.
    /// </summary>
    private static readonly HashSet<string> AttributionVerbs = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "added", "admitted", "announced", "answered", "asked", "asks", "began",
        "called", "continued", "cried", "declared", "explained", "insisted",
        "laughed", "murmured", "muttered", "repeated", "replied", "responded",
        "said", "says", "screamed", "shouted", "sighed", "thought", "told",
        "tells", "warned", "whispered", "wrote", "yelled"
    };

    internal static bool TryFindLeadingSpeaker(
        string text,
        int targetIndex,
        out CurrentSpeakerContext? speaker,
        out int dialogueStart)
    {
        speaker = null;
        dialogueStart = 0;
        if (string.IsNullOrWhiteSpace(text) || targetIndex <= 0)
        {
            return false;
        }

        int first = SkipWhitespace(text, 0);
        if (first >= text.Length || first >= targetIndex)
        {
            return false;
        }

        if (text[first] == '[')
        {
            int closing = text.IndexOf(']', first + 1);
            if (closing > first + 1 &&
                closing - first <= MaximumNameLength + 1 &&
                closing < targetIndex &&
                TryParseName(text[(first + 1)..closing], out string name))
            {
                int start = SkipWhitespace(text, closing + 1);
                if (start < text.Length && start <= targetIndex)
                {
                    speaker = new CurrentSpeakerContext(name);
                    dialogueStart = start;
                    return true;
                }
            }
        }

        int lineEnd = text.IndexOfAny(['\r', '\n'], first);
        if (lineEnd > first &&
            lineEnd < targetIndex &&
            TryParseName(text[first..lineEnd], out string lineName))
        {
            int start = SkipWhitespace(text, lineEnd);
            if (start < text.Length && start <= targetIndex)
            {
                speaker = new CurrentSpeakerContext(lineName);
                dialogueStart = start;
                return true;
            }
        }

        int colon = text.IndexOf(':', first);
        if (colon > first &&
            colon - first <= MaximumNameLength &&
            colon < targetIndex &&
            TryParseName(text[first..colon], out string colonName))
        {
            int start = SkipWhitespace(text, colon + 1);
            if (start < text.Length && start <= targetIndex)
            {
                speaker = new CurrentSpeakerContext(colonName);
                dialogueStart = start;
                return true;
            }
        }

        return false;
    }

    internal static bool TryParseStandaloneName(string? value, out string name) =>
        TryParseName(value, out name);

    private static bool TryParseName(string? value, out string name)
    {
        name = NormalizeName(value);
        string firstWord = name.Split(' ', 2)[0];
        if (name.Length is < 1 or > MaximumNameLength ||
            !char.IsUpper(name[0]) ||
            NonSpeakerLeadingWords.Contains(firstWord) ||
            UiOrUtteranceWords.Contains(name) ||
            LooksLikeReportingClause(name, firstWord))
        {
            name = string.Empty;
            return false;
        }

        int words = 0;
        bool insideWord = false;
        foreach (char character in name)
        {
            bool wordCharacter = char.IsLetter(character) ||
                                 char.GetUnicodeCategory(character) ==
                                 UnicodeCategory.NonSpacingMark ||
                                 character is '-' or '\'' or '\u2019';
            if (wordCharacter)
            {
                if (!insideWord)
                {
                    words++;
                    insideWord = true;
                }

                continue;
            }

            if (!char.IsWhiteSpace(character))
            {
                name = string.Empty;
                return false;
            }

            insideWord = false;
        }

        if (words is < 1 or > MaximumNameWords)
        {
            name = string.Empty;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Whether the label is a sentence about a speaker rather than the speaker.
    /// "The doctor said: your arm is broken." is one utterance, so treating the
    /// clause as a nameplate deletes both the attribution and the past framing
    /// from the text the phrase model translates. The pronoun-subject form is
    /// already covered by <see cref="NonSpeakerLeadingWords"/>; a determiner or
    /// possessive opens the same clause with a full noun phrase instead.
    /// </summary>
    private static bool LooksLikeReportingClause(string name, string firstWord) =>
        EnglishContextLexicon.Determiners.Contains(firstWord.ToLowerInvariant()) ||
        EnglishContextLexicon.Possessives.Contains(firstWord.ToLowerInvariant()) ||
        name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(AttributionVerbs.Contains);

    private static string NormalizeName(string? value) =>
        string.Join(
                " ",
                (value ?? string.Empty).Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries))
            .Normalize(NormalizationForm.FormC);

    private static int SkipWhitespace(string text, int start)
    {
        while (start < text.Length && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        return start;
    }
}
