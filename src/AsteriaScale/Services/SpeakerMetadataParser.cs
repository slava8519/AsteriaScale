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

    private static readonly HashSet<string> StandaloneUiOrUtteranceWords = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "auto", "back", "close", "config", "hello", "hey", "history",
        "load", "menu", "no", "ok", "okay", "please", "save", "settings",
        "skip", "sorry", "stop", "thanks", "wait", "yes"
    };

    private static readonly HashSet<string> NonSpeakerLeadingWords = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "he", "here", "i", "it", "she", "that", "these", "they", "this",
        "those", "there", "we", "you"
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
                TryParseName(text[(first + 1)..closing], standalone: false, out string name))
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
            TryParseName(text[first..lineEnd], standalone: true, out string lineName))
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
            TryParseName(text[first..colon], standalone: false, out string colonName))
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
        TryParseName(value, standalone: true, out name);

    private static bool TryParseName(string? value, bool standalone, out string name)
    {
        name = NormalizeName(value);
        string firstWord = name.Split(' ', 2)[0];
        if (name.Length is < 1 or > MaximumNameLength ||
            !char.IsUpper(name[0]) ||
            NonSpeakerLeadingWords.Contains(firstWord) ||
            (standalone && StandaloneUiOrUtteranceWords.Contains(name)))
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
