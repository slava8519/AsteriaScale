using System.Text.RegularExpressions;

namespace AsteriaScale.Services;

/// <summary>
/// English function-word sets and light morphology shared by the deterministic
/// heuristic ranker and the smart-dictionary sense resolver. Part-of-speech
/// inference deliberately stays separate in each consumer: the heuristic ranker
/// picks final visible answers and must stay narrow, while the resolver only
/// filters and annotates candidates for the neural ranker.
/// </summary>
internal static partial class EnglishContextLexicon
{
    public static readonly HashSet<string> AuxiliaryBe =
        new(StringComparer.Ordinal)
        {
            "am", "is", "are", "was", "were", "be", "been", "being"
        };

    public static readonly HashSet<string> AuxiliaryHave =
        new(StringComparer.Ordinal) { "have", "has", "had" };

    public static readonly HashSet<string> Modals =
        new(StringComparer.Ordinal)
        {
            "can", "could", "may", "might", "must", "shall", "should", "will", "would"
        };

    public static readonly HashSet<string> Determiners =
        new(StringComparer.Ordinal)
        {
            "a", "an", "the", "this", "that", "these", "those", "each", "every", "some",
            "any", "no", "another"
        };

    public static readonly HashSet<string> Possessives =
        new(StringComparer.Ordinal)
        {
            "my", "your", "his", "her", "its", "our", "their", "whose"
        };

    public static readonly HashSet<string> SubjectPronouns =
        new(StringComparer.Ordinal) { "i", "you", "he", "she", "it", "we", "they" };

    public static readonly HashSet<string> LinkingVerbs =
        new(StringComparer.Ordinal)
        {
            "be", "am", "is", "are", "was", "were", "been", "seem", "seems", "seemed",
            "become", "becomes", "became", "feel", "feels", "felt", "look", "looks", "looked"
        };

    public static readonly HashSet<string> HabitualOrDurativeCues =
        new(StringComparer.Ordinal)
        {
            "always", "often", "usually", "repeatedly", "continually", "constantly", "daily",
            "regularly", "kept", "continued", "hours", "minutes", "years"
        };

    public static readonly IReadOnlyDictionary<string, string> IrregularPastToLemma =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["was"] = "be",
            ["were"] = "be",
            ["went"] = "go",
            ["had"] = "have",
            ["did"] = "do",
            ["saw"] = "see",
            ["said"] = "say",
            ["told"] = "tell",
            ["thought"] = "think",
            ["felt"] = "feel",
            ["found"] = "find",
            ["made"] = "make",
            ["took"] = "take",
            ["came"] = "come",
            ["gave"] = "give",
            ["got"] = "get",
            ["shook"] = "shake",
            ["spoke"] = "speak",
            ["wrote"] = "write",
            ["ran"] = "run",
            ["flew"] = "fly",
            ["left"] = "leave",
            ["lost"] = "lose",
            ["kept"] = "keep",
            ["heard"] = "hear",
            ["held"] = "hold",
            ["knew"] = "know",
            ["fed"] = "feed",
            ["led"] = "lead"
        };

    // Frequent words that end in -ed without being past forms. Without this
    // guard "need" or "sacred" would trigger past-tense morphology.
    public static readonly HashSet<string> NonPastEdForms =
        new(StringComparer.Ordinal)
        {
            "bleed", "breed", "creed", "exceed", "feed", "greed", "hatred", "hundred",
            "indeed", "kindred", "naked", "need", "proceed", "sacred", "seed", "shed",
            "speed", "succeed", "wicked", "wretched"
        };

    // After these words "to" is usually the preposition ("went to bed"),
    // not the infinitive marker, so the target may well be a noun.
    public static readonly HashSet<string> MotionWordsBeforeTo =
        new(StringComparer.Ordinal)
        {
            "back", "came", "come", "comes", "coming", "close", "closer", "down",
            "get", "gets", "getting", "go", "goes", "going", "gone", "got", "gotten",
            "head", "headed", "heading", "moved", "moving", "next", "off", "over",
            "ran", "run", "running", "up", "walk", "walked", "walking", "way", "went"
        };

    public static bool IsLikelyPastForm(string word) =>
        IrregularPastToLemma.ContainsKey(word) ||
        !NonPastEdForms.Contains(word) &&
        word.Length > 3 &&
        (word.EndsWith("ed", StringComparison.Ordinal) ||
         word.EndsWith("ied", StringComparison.Ordinal));

    public static string NormalizeToken(string value) =>
        value.Replace('’', '\'').ToLowerInvariant();

    public static string FindSubject(IReadOnlyList<Match> tokens, int targetIndex)
    {
        for (int index = Math.Min(targetIndex - 1, tokens.Count - 1);
             index >= Math.Max(0, targetIndex - 7);
             index--)
        {
            string token = NormalizeToken(tokens[index].Value);
            if (SubjectPronouns.Contains(token))
            {
                return token;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Finds the token equal to <paramref name="target"/> closest to the cursor.
    /// The resolver requires an exact occurrence (-1 otherwise), while the
    /// heuristic ranker intentionally falls back to the nearest token.
    /// </summary>
    public static int FindTargetToken(
        IReadOnlyList<Match> tokens,
        string target,
        int characterIndex,
        bool fallbackToNearest)
    {
        int bestIndex = -1;
        int bestDistance = int.MaxValue;
        for (int index = 0; index < tokens.Count; index++)
        {
            Match token = tokens[index];
            if (!string.Equals(
                    NormalizeToken(token.Value),
                    target,
                    StringComparison.Ordinal))
            {
                continue;
            }

            int distance =
                characterIndex >= token.Index &&
                characterIndex <= token.Index + token.Length
                    ? 0
                    : Math.Abs(token.Index + (token.Length / 2) - characterIndex);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = index;
            }
        }

        if (bestIndex >= 0 || !fallbackToNearest || tokens.Count == 0)
        {
            return bestIndex;
        }

        return Enumerable.Range(0, tokens.Count)
            .MinBy(index => Math.Abs(tokens[index].Index - characterIndex));
    }

    [GeneratedRegex(@"[A-Za-z]+(?:['’\-][A-Za-z]+)*", RegexOptions.CultureInvariant)]
    public static partial Regex TokenRegex();
}
