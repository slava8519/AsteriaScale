using System.Text.RegularExpressions;

namespace AsteriaScale.Services;

internal sealed record SmartDictionaryHeuristicSelection(
    int Index,
    string Translation,
    double Confidence,
    string Reason);

/// <summary>
/// Resolves only high-confidence English dictionary ambiguities without starting a model.
/// A null result is intentional: uncertain or stylistic choices must be left to the
/// contextual NPU/GPU ranker.
/// </summary>
internal static partial class SmartDictionaryHeuristicRanker
{
    internal const string EngineLabel = "NPU OCR  •  SMART-FAST";
    internal const string TatoebaEngineLabel = EngineLabel + "  •  TATOEBA";

    private enum LexicalPartOfSpeech
    {
        Unknown,
        Noun,
        Verb,
        Adjective,
        Adverb,
        Interjection
    }

    private sealed record CandidateAnalysis(
        int Index,
        string Text,
        string Normalized,
        LexicalPartOfSpeech PartOfSpeech,
        RussianPastForm PastForm,
        string PastStem,
        bool IsSemelfactive);

    private sealed record EnglishAnalysis(
        string Lemma,
        LexicalPartOfSpeech PartOfSpeech,
        double PartOfSpeechConfidence,
        string Subject,
        bool IsPast,
        bool IsProgressive,
        bool IsHabitualOrDurative);

    private sealed record SenseRule(
        string Lemma,
        string[] ContextCues,
        string[] CandidateCues,
        string Reason);

    private static readonly SenseRule[] SenseRules =
    [
        new(
            "bank",
            ["river", "stream", "creek", "canal", "lake", "water", "shore", "fisherman", "fishing", "flood"],
            ["берег", "побереж"],
            "river-bank context"),
        new(
            "bank",
            ["money", "account", "deposit", "loan", "credit", "cash", "finance", "savings", "teller", "mortgage"],
            ["банк", "финанс"],
            "financial-bank context"),
        new(
            "bat",
            ["cave", "wing", "wings", "flew", "fly", "flying", "nocturnal", "mammal", "vampire", "roost"],
            ["летуч", "рукокрыл"],
            "animal-bat context"),
        new(
            "bat",
            ["ball", "baseball", "pitch", "pitcher", "swing", "swung", "hit", "inning", "player"],
            ["бит"],
            "sports-bat context"),
        new(
            "crane",
            ["bird", "wing", "wings", "nest", "nested", "flock", "feather", "flew", "fly", "lake"],
            ["журав"],
            "bird-crane context"),
        new(
            "crane",
            ["construction", "lift", "lifting", "steel", "cargo", "building", "operator", "hook", "boom"],
            ["кран"],
            "machine-crane context"),
        new(
            "beam",
            ["smile", "smiled", "face", "grin", "eyes", "joy", "happy", "radiant", "delight"],
            ["сия", "лучезар", "радост", "счастлив"],
            "radiant expression context"),
        new(
            "beam",
            ["steel", "wood", "ceiling", "roof", "support", "construction", "girder", "frame"],
            ["балк", "брус", "переклад", "траверс"],
            "structural-beam context"),
        new(
            "teeter",
            ["verge", "brink", "edge", "collapse", "failure", "confusion", "disaster"],
            ["гран"],
            "on-the-brink context"),
        new(
            "spring",
            ["summer", "winter", "autumn", "season", "april", "may", "flowers", "warm"],
            ["весн"],
            "season context"),
        new(
            "spring",
            ["coil", "mattress", "mechanism", "compressed", "metal", "suspension"],
            ["пружин"],
            "mechanical-spring context"),
        new(
            "seal",
            ["ocean", "sea", "flipper", "flippers", "animal", "swam", "swim", "zoo"],
            ["тюлен"],
            "animal-seal context"),
        new(
            "seal",
            ["document", "envelope", "stamp", "wax", "official", "package", "airtight"],
            ["печат", "пломб", "гермет"],
            "stamp-or-closure context"),
        new(
            "bark",
            ["dog", "puppy", "hound", "growl", "howl", "loud"],
            ["лай", "лаят", "гавк"],
            "dog-bark context"),
        new(
            "bark",
            ["tree", "trunk", "wood", "forest", "branch", "rough"],
            ["кор"],
            "tree-bark context"),
        new(
            "match",
            ["candle", "fire", "flame", "light", "box", "strike", "burn"],
            ["спич"],
            "fire-match context"),
        new(
            "match",
            ["team", "score", "game", "tournament", "player", "stadium", "football", "tennis"],
            ["матч", "игр"],
            "sports-match context"),
        new(
            "light",
            ["dress", "shirt", "fabric", "bag", "weight", "heavy", "carry", "luggage"],
            ["легк", "невесом"],
            "low-weight context"),
        new(
            "light",
            ["lamp", "bulb", "dark", "room", "sun", "shine", "bright", "switch"],
            ["свет", "освещ"],
            "illumination context"),
        new(
            "draft",
            ["window", "door", "cold", "air", "breeze", "room"],
            ["сквозня"],
            "air-draft context"),
        new(
            "draft",
            ["document", "version", "edit", "manuscript", "proposal", "rewrite"],
            ["чернов", "проект"],
            "document-draft context"),
        new(
            "watch",
            ["wrist", "time", "hour", "minute", "wear", "wore"],
            ["час"],
            "timepiece-watch context"),
        new(
            "lie",
            ["bed", "sofa", "down", "floor", "rest", "lay"],
            ["леж"],
            "recline-lie context"),
        new(
            "lie",
            ["truth", "honest", "believe", "deceive", "false", "secret"],
            ["лг", "лож", "обман"],
            "falsehood-lie context")
    ];

    public static SmartDictionaryHeuristicSelection? TrySelect(
        string context,
        string target,
        int characterIndex,
        IReadOnlyList<string> candidates,
        string sourceLanguage)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (!IsEnglish(sourceLanguage, context) || candidates.Count == 0)
        {
            return null;
        }

        CandidateAnalysis[] analyzedCandidates = ValidatePastParadigms(
            candidates
                .Select((candidate, index) => AnalyzeCandidate(candidate, index))
                .Where(candidate => candidate.Normalized.Length > 0)
                .ToArray());
        if (analyzedCandidates.Length == 0)
        {
            return null;
        }

        if (analyzedCandidates.Length == 1)
        {
            CandidateAnalysis only = analyzedCandidates[0];
            return Selection(only, 1, "single dictionary meaning");
        }

        EnglishAnalysis english = AnalyzeEnglish(context, target, characterIndex);

        SmartDictionaryHeuristicSelection? agreement =
            SelectByPastAgreement(english, analyzedCandidates);
        if (agreement is not null)
        {
            return agreement;
        }

        SmartDictionaryHeuristicSelection? semantic =
            SelectBySemanticContext(context, characterIndex, english, analyzedCandidates);
        if (semantic is not null)
        {
            return semantic;
        }

        SmartDictionaryHeuristicSelection? aspect =
            SelectByAspect(english, analyzedCandidates);
        if (aspect is not null)
        {
            return aspect;
        }

        return SelectByPartOfSpeech(english, analyzedCandidates);
    }

    /// <summary>
    /// Uses an independently found exact Tatoeba sentence only when its Russian
    /// translation contains one unique dictionary outcome. This is deliberately
    /// conservative: shared stems and unrelated sentence translations abstain,
    /// leaving GlossBERT or the visible dictionary choices unchanged.
    /// </summary>
    public static SmartDictionaryHeuristicSelection? TrySelectFromContextTranslation(
        string contextTranslation,
        IReadOnlyList<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (string.IsNullOrWhiteSpace(contextTranslation) || candidates.Count < 2)
        {
            return null;
        }

        string[] contextTokens = RussianContentTokens(contextTranslation);
        if (contextTokens.Length == 0)
        {
            return null;
        }

        (int Index, string Translation, int Score)[] ranked = candidates
            .Select((candidate, index) => (
                Index: index,
                Translation: candidate,
                Score: ContextTranslationScore(candidate, contextTokens)))
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Index)
            .ToArray();
        if (ranked.Length == 0 || ranked[0].Score < 2)
        {
            return null;
        }

        int secondScore = ranked.Skip(1).Select(candidate => candidate.Score).FirstOrDefault();
        if (ranked[0].Score == secondScore)
        {
            return null;
        }

        return new SmartDictionaryHeuristicSelection(
            ranked[0].Index,
            ranked[0].Translation,
            0.995,
            "exact Tatoeba sentence alignment");
    }

    private static int ContextTranslationScore(
        string candidate,
        IReadOnlyList<string> contextTokens)
    {
        string[] candidateTokens = RussianContentTokens(candidate);
        int score = 0;
        foreach (string candidateToken in candidateTokens)
        {
            int best = contextTokens
                .Select(contextToken => RussianTokenMatchScore(candidateToken, contextToken))
                .DefaultIfEmpty()
                .Max();
            score += best;
        }

        return score;
    }

    private static string[] RussianContentTokens(string value) =>
        RussianTokenRegex()
            .Matches(RussianMorphology.RemoveStressMarks(value))
            .Select(match => RussianMorphology.NormalizeForStemComparison(match.Value))
            .Where(token => token.Length >= 3 && !RussianContextStopWords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static int RussianTokenMatchScore(string candidate, string context) =>
        RussianMorphology.RussianTokenMatchScore(candidate, context);

    private static SmartDictionaryHeuristicSelection? SelectByPastAgreement(
        EnglishAnalysis english,
        IReadOnlyList<CandidateAnalysis> candidates)
    {
        if (!english.IsPast)
        {
            return null;
        }

        RussianPastForm expected = english.Subject switch
        {
            "he" => RussianPastForm.Masculine,
            "she" => RussianPastForm.Feminine,
            "it" => RussianPastForm.Neuter,
            "we" or "they" => RussianPastForm.Plural,
            _ => RussianPastForm.None
        };
        if (expected == RussianPastForm.None)
        {
            return null;
        }

        CandidateAnalysis[] inflected = candidates
            .Where(candidate => candidate.PastForm != RussianPastForm.None)
            .ToArray();
        if (inflected.Select(candidate => candidate.PastStem).Distinct().Count() != 1 ||
            inflected.Select(candidate => candidate.PastForm).Distinct().Count() < 3)
        {
            return null;
        }

        CandidateAnalysis[] matches = inflected
            .Where(candidate => candidate.PastForm == expected)
            .ToArray();
        return inflected.Select(candidate => candidate.PastForm).Distinct().Count() >= 2 &&
               matches.Length == 1
            ? Selection(matches[0], 0.99, "Russian past-tense agreement")
            : null;
    }

    private static SmartDictionaryHeuristicSelection? SelectBySemanticContext(
        string context,
        int characterIndex,
        EnglishAnalysis english,
        IReadOnlyList<CandidateAnalysis> candidates)
    {
        string normalizedContext = CreateSemanticContextWindow(context, characterIndex);
        SenseRule[] matchingRules = SenseRules
            .Where(rule =>
                rule.Lemma == english.Lemma &&
                rule.ContextCues.Any(cue => ContainsEnglishCue(normalizedContext, cue)))
            .ToArray();
        if (matchingRules.Length == 0)
        {
            return null;
        }

        var scores = candidates.ToDictionary(candidate => candidate.Index, _ => 0);
        var reasons = new Dictionary<int, string>();
        foreach (SenseRule rule in matchingRules)
        {
            int contextEvidence = rule.ContextCues.Count(
                cue => ContainsEnglishCue(normalizedContext, cue));
            foreach (CandidateAnalysis candidate in candidates)
            {
                if (!rule.CandidateCues.Any(cue =>
                        candidate.Normalized.Contains(cue, StringComparison.Ordinal)))
                {
                    continue;
                }

                scores[candidate.Index] += Math.Min(3, contextEvidence);
                reasons[candidate.Index] = rule.Reason;
            }
        }

        IOrderedEnumerable<CandidateAnalysis> ordered = candidates
            .OrderByDescending(candidate => scores[candidate.Index])
            .ThenBy(candidate => candidate.Index);
        CandidateAnalysis best = ordered.First();
        int bestScore = scores[best.Index];
        int secondScore = ordered.Skip(1).Select(candidate => scores[candidate.Index]).FirstOrDefault();
        if (bestScore <= 0 || bestScore == secondScore)
        {
            // Multiple candidates may be valid synonyms of the same sense. Choosing their
            // wording is not a deterministic lexical decision, so leave it to the model.
            return null;
        }

        double confidence = bestScore >= 2 ? 0.98 : 0.95;
        return Selection(best, confidence, reasons.GetValueOrDefault(best.Index, "semantic context"));
    }

    private static SmartDictionaryHeuristicSelection? SelectByAspect(
        EnglishAnalysis english,
        IReadOnlyList<CandidateAnalysis> candidates)
    {
        CandidateAnalysis[] infinitives = candidates
            .Where(candidate => candidate.PartOfSpeech == LexicalPartOfSpeech.Verb &&
                                candidate.PastForm == RussianPastForm.None)
            .ToArray();
        CandidateAnalysis[] semelfactive = infinitives
            .Where(candidate => candidate.IsSemelfactive)
            .ToArray();
        CandidateAnalysis[] nonSemelfactive = infinitives
            .Where(candidate => !candidate.IsSemelfactive)
            .ToArray();
        if (semelfactive.Length != 1 || nonSemelfactive.Length != 1)
        {
            return null;
        }

        if (english.IsProgressive)
        {
            return Selection(nonSemelfactive[0], 0.96, "progressive action aspect");
        }

        return english.IsPast && !english.IsHabitualOrDurative
            ? Selection(semelfactive[0], 0.93, "single completed action aspect")
            : null;
    }

    private static SmartDictionaryHeuristicSelection? SelectByPartOfSpeech(
        EnglishAnalysis english,
        IReadOnlyList<CandidateAnalysis> candidates)
    {
        if (english.PartOfSpeech == LexicalPartOfSpeech.Unknown ||
            english.PartOfSpeechConfidence < 0.9)
        {
            return null;
        }

        CandidateAnalysis[] matches = candidates
            .Where(candidate => candidate.PartOfSpeech == english.PartOfSpeech)
            .ToArray();
        if (matches.Length != 1)
        {
            return null;
        }

        // Unknown Russian forms may in fact have the requested POS. Refuse to select if
        // the morphology could not confidently classify every competing candidate.
        if (candidates.Any(candidate => candidate.PartOfSpeech == LexicalPartOfSpeech.Unknown))
        {
            return null;
        }

        return Selection(
            matches[0],
            Math.Min(0.97, english.PartOfSpeechConfidence),
            "part-of-speech context");
    }

    private static EnglishAnalysis AnalyzeEnglish(string context, string target, int characterIndex)
    {
        Match[] tokens = EnglishTokenRegex().Matches(context)
            .Cast<Match>()
            .ToArray();
        string targetWord = EnglishContextLexicon.NormalizeToken(
            EnglishTokenRegex().Match(target).Value);
        if (targetWord.Length == 0)
        {
            targetWord = NormalizeEnglish(target).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? string.Empty;
        }

        int tokenIndex = FindTargetToken(tokens, targetWord, characterIndex);
        string current = tokenIndex >= 0
            ? EnglishContextLexicon.NormalizeToken(tokens[tokenIndex].Value)
            : targetWord;
        string previous = tokenIndex > 0
            ? EnglishContextLexicon.NormalizeToken(tokens[tokenIndex - 1].Value)
            : string.Empty;
        string next = tokenIndex >= 0 && tokenIndex + 1 < tokens.Length
            ? EnglishContextLexicon.NormalizeToken(tokens[tokenIndex + 1].Value)
            : string.Empty;
        string beforePrevious = tokenIndex > 1
            ? EnglishContextLexicon.NormalizeToken(tokens[tokenIndex - 2].Value)
            : string.Empty;
        string lemma = LemmatizeEnglish(current);
        bool isProgressive = current.EndsWith("ing", StringComparison.Ordinal) &&
                             EnglishContextLexicon.AuxiliaryBe.Contains(previous);
        bool isPast = EnglishContextLexicon.IsLikelyPastForm(current);
        bool habitualOrDurative = tokens.Any(token =>
            EnglishContextLexicon.HabitualOrDurativeCues.Contains(
                EnglishContextLexicon.NormalizeToken(token.Value))) ||
            NormalizeEnglish(context).Contains("used to", StringComparison.Ordinal) ||
            NormalizeEnglish(context).Contains("every day", StringComparison.Ordinal) ||
            NormalizeEnglish(context).Contains("all day", StringComparison.Ordinal) ||
            NormalizeEnglish(context).Contains("all night", StringComparison.Ordinal);

        (LexicalPartOfSpeech partOfSpeech, double confidence) =
            InferEnglishPartOfSpeech(current, previous, next, beforePrevious, isProgressive, isPast);
        string subject = EnglishContextLexicon.FindSubject(tokens, tokenIndex);
        return new EnglishAnalysis(
            lemma,
            partOfSpeech,
            confidence,
            subject,
            isPast,
            isProgressive,
            habitualOrDurative);
    }

    private static (LexicalPartOfSpeech PartOfSpeech, double Confidence) InferEnglishPartOfSpeech(
        string word,
        string previous,
        string next,
        string beforePrevious,
        bool isProgressive,
        bool isPast)
    {
        if (previous == "to" || EnglishContextLexicon.Modals.Contains(previous))
        {
            // "went to bed" and "went to sleep" look identical here, so a
            // prepositional-looking "to" keeps the verb reading only as a weak
            // prior below the 0.9 selection threshold.
            return previous == "to" &&
                   EnglishContextLexicon.MotionWordsBeforeTo.Contains(beforePrevious)
                ? (LexicalPartOfSpeech.Verb, 0.85)
                : (LexicalPartOfSpeech.Verb, 0.99);
        }

        if (isProgressive || EnglishContextLexicon.AuxiliaryHave.Contains(previous))
        {
            return (LexicalPartOfSpeech.Verb, 0.98);
        }

        if (word.EndsWith("ing", StringComparison.Ordinal) &&
            (EnglishContextLexicon.Determiners.Contains(previous) || EnglishContextLexicon.Possessives.Contains(previous)) &&
            next.Length > 0)
        {
            return (LexicalPartOfSpeech.Adjective, 0.94);
        }

        if (isPast && (EnglishContextLexicon.SubjectPronouns.Contains(previous) ||
                       previous.Length > 0 && !EnglishContextLexicon.Determiners.Contains(previous)))
        {
            return (LexicalPartOfSpeech.Verb, 0.92);
        }

        if (word.EndsWith("ly", StringComparison.Ordinal) && word.Length > 4)
        {
            return (LexicalPartOfSpeech.Adverb, 0.97);
        }

        if (word.EndsWith("ness", StringComparison.Ordinal) ||
            word.EndsWith("ment", StringComparison.Ordinal) ||
            word.EndsWith("tion", StringComparison.Ordinal) ||
            word.EndsWith("ity", StringComparison.Ordinal) ||
            word.EndsWith("ship", StringComparison.Ordinal) ||
            word.EndsWith("hood", StringComparison.Ordinal))
        {
            return (LexicalPartOfSpeech.Noun, 0.94);
        }

        if (word.EndsWith("ous", StringComparison.Ordinal) ||
            word.EndsWith("ful", StringComparison.Ordinal) ||
            word.EndsWith("less", StringComparison.Ordinal) ||
            word.EndsWith("ive", StringComparison.Ordinal) ||
            word.EndsWith("able", StringComparison.Ordinal) ||
            word.EndsWith("ible", StringComparison.Ordinal))
        {
            return (LexicalPartOfSpeech.Adjective, 0.93);
        }

        if (EnglishContextLexicon.LinkingVerbs.Contains(previous))
        {
            return (LexicalPartOfSpeech.Adjective, 0.91);
        }

        if ((EnglishContextLexicon.Determiners.Contains(previous) || EnglishContextLexicon.Possessives.Contains(previous)) &&
            (next.Length == 0 || IsLikelyBoundaryFollower(next)))
        {
            return (LexicalPartOfSpeech.Noun, 0.94);
        }

        if (EnglishContextLexicon.SubjectPronouns.Contains(previous))
        {
            return (LexicalPartOfSpeech.Verb, 0.91);
        }

        return (LexicalPartOfSpeech.Unknown, 0);
    }

    private static bool IsLikelyBoundaryFollower(string word) =>
        word is "of" or "in" or "on" or "at" or "from" or "with" or "for" or "was" or "were" or "is" or
            "are" or "has" or "had";

    private static int FindTargetToken(IReadOnlyList<Match> tokens, string target, int characterIndex)
    {
        if (tokens.Count == 0)
        {
            return -1;
        }

        return EnglishContextLexicon.FindTargetToken(
            tokens,
            target,
            characterIndex,
            fallbackToNearest: true);
    }

    private static CandidateAnalysis AnalyzeCandidate(string? candidate, int index)
    {
        string text = string.Join(
            " ",
            (candidate ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        string normalized = NormalizeRussian(text);
        string[] words = RussianTokenRegex().Matches(normalized)
            .Select(match => match.Value)
            .ToArray();
        (RussianPastForm pastForm, string pastStem) = (RussianPastForm.None, string.Empty);
        foreach (string word in words)
        {
            (RussianPastForm form, string stem) = RussianMorphology.DetectPastForm(word);
            if (form != RussianPastForm.None)
            {
                (pastForm, pastStem) = (form, stem);
                break;
            }
        }

        LexicalPartOfSpeech partOfSpeech = DetectRussianPartOfSpeech(words, pastForm);
        bool semelfactive = words.Any(RussianMorphology.IsSemelfactiveInfinitive);
        return new CandidateAnalysis(
            index,
            text,
            normalized,
            partOfSpeech,
            pastForm,
            pastStem,
            semelfactive);
    }

    private static CandidateAnalysis[] ValidatePastParadigms(
        IReadOnlyList<CandidateAnalysis> candidates)
    {
        HashSet<string> validatedStems = candidates
            .Where(candidate =>
                candidate.PastForm != RussianPastForm.None &&
                candidate.PastStem.Length > 1)
            .GroupBy(candidate => candidate.PastStem, StringComparer.Ordinal)
            .Where(group =>
                group.Select(candidate => candidate.PastForm).Distinct().Count() >= 3)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);

        return candidates
            .Select(candidate =>
            {
                if (validatedStems.Contains(candidate.PastStem))
                {
                    return candidate;
                }

                string[] words = RussianTokenRegex().Matches(candidate.Normalized)
                    .Select(match => match.Value)
                    .ToArray();
                return candidate with
                {
                    PartOfSpeech = DetectRussianPartOfSpeech(words, RussianPastForm.None),
                    PastForm = RussianPastForm.None,
                    PastStem = string.Empty
                };
            })
            .ToArray();
    }

    private static LexicalPartOfSpeech DetectRussianPartOfSpeech(
        IReadOnlyList<string> words,
        RussianPastForm pastForm)
    {
        if (words.Count == 0)
        {
            return LexicalPartOfSpeech.Unknown;
        }

        if (pastForm != RussianPastForm.None)
        {
            return LexicalPartOfSpeech.Verb;
        }

        string first = words[0];
        if (first is "ах" or "ох" or "эх" or "увы" or "ага" or "эй" or "ой" or "ура")
        {
            return LexicalPartOfSpeech.Interjection;
        }

        if (RussianMorphology.IsInfinitive(first))
        {
            return LexicalPartOfSpeech.Verb;
        }

        if (RussianAdjectiveRegex().IsMatch(first))
        {
            return LexicalPartOfSpeech.Adjective;
        }

        if (RussianKnownAdverbs.Contains(first) ||
            first.EndsWith("чески", StringComparison.Ordinal))
        {
            return LexicalPartOfSpeech.Adverb;
        }

        return LexicalPartOfSpeech.Noun;
    }

    private static string LemmatizeEnglish(string word)
    {
        string normalized = word.ToLowerInvariant();
        if (EnglishContextLexicon.IrregularPastToLemma.TryGetValue(normalized, out string? irregular))
        {
            return irregular;
        }

        if (normalized.EndsWith("ing", StringComparison.Ordinal) && normalized.Length > 5)
        {
            string stem = normalized[..^3];
            if (EndsWithDoubledLetter(stem))
            {
                return stem[..^1];
            }

            if (SenseRules.Any(rule => rule.Lemma == stem + "e"))
            {
                return stem + "e";
            }

            return stem;
        }

        if (normalized.EndsWith("ied", StringComparison.Ordinal) && normalized.Length > 4)
        {
            return normalized[..^3] + "y";
        }

        if (normalized.EndsWith("ed", StringComparison.Ordinal) && normalized.Length > 4)
        {
            string stem = normalized[..^2];
            if (EndsWithDoubledLetter(stem))
            {
                return stem[..^1];
            }

            if (SenseRules.Any(rule => rule.Lemma == stem + "e"))
            {
                return stem + "e";
            }

            return stem;
        }

        if (normalized.EndsWith("es", StringComparison.Ordinal) && normalized.Length > 4)
        {
            return normalized[..^2];
        }

        if (normalized.EndsWith('s') &&
            normalized.Length > 3 &&
            !normalized.EndsWith("ss", StringComparison.Ordinal))
        {
            return normalized[..^1];
        }

        return normalized;
    }

    private static bool EndsWithDoubledLetter(string value) =>
        value.Length > 1 && value[^1] == value[^2];

    private static bool ContainsEnglishCue(string normalizedContext, string cue) =>
        (" " + normalizedContext + " ").Contains(
            " " + cue.ToLowerInvariant() + " ",
            StringComparison.Ordinal);

    private static string CreateSemanticContextWindow(string context, int characterIndex)
    {
        if (string.IsNullOrWhiteSpace(context))
        {
            return string.Empty;
        }

        int anchor = Math.Clamp(characterIndex, 0, context.Length - 1);
        int clauseStart = anchor;
        while (clauseStart > 0 && !IsClauseBoundary(context[clauseStart - 1]))
        {
            clauseStart--;
        }

        int clauseEnd = anchor;
        while (clauseEnd < context.Length && !IsClauseBoundary(context[clauseEnd]))
        {
            clauseEnd++;
        }

        string clause = context[clauseStart..clauseEnd];
        Match[] tokens = EnglishTokenRegex().Matches(clause).Cast<Match>().ToArray();
        if (tokens.Length == 0)
        {
            return string.Empty;
        }

        int localAnchor = anchor - clauseStart;
        int anchorToken = Enumerable.Range(0, tokens.Length)
            .MinBy(index => Math.Abs(
                tokens[index].Index + (tokens[index].Length / 2) - localAnchor));
        const int radius = 6;
        int first = Math.Max(0, anchorToken - radius);
        int last = Math.Min(tokens.Length - 1, anchorToken + radius);
        return string.Join(
            " ",
            tokens[first..(last + 1)].Select(token => token.Value.ToLowerInvariant()));
    }

    private static bool IsClauseBoundary(char character) =>
        character is ',' or ';' or ':' or '.' or '!' or '?' or '\r' or '\n' or '—' or '–';

    private static bool IsEnglish(string sourceLanguage, string context)
    {
        if (sourceLanguage.Contains("Japanese", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sourceLanguage, "ja", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !context.Any(character =>
            character is >= '\u3040' and <= '\u30ff' or
            >= '\u3400' and <= '\u9fff');
    }

    private static string NormalizeEnglish(string value) => string.Join(
            " ",
            EnglishTokenRegex().Matches(value).Select(match => match.Value.ToLowerInvariant()))
        .Trim();

    private static string NormalizeRussian(string value) =>
        string.Join(" ", RussianTokenRegex().Matches(value)
                .Select(match => match.Value.ToLowerInvariant().Replace('ё', 'е')))
            .Trim();

    private static SmartDictionaryHeuristicSelection Selection(
        CandidateAnalysis candidate,
        double confidence,
        string reason) =>
        new(candidate.Index, candidate.Text, confidence, reason);

    private static readonly HashSet<string> RussianKnownAdverbs =
        new(StringComparer.Ordinal)
        {
            "быстро", "медленно", "тихо", "громко", "хорошо", "плохо", "вместе", "снова",
            "внезапно", "сразу", "здесь", "там", "рядом", "далеко", "близко", "всегда", "никогда"
        };

    private static readonly HashSet<string> RussianContextStopWords =
        new(StringComparer.Ordinal)
        {
            "без", "бы", "был", "была", "были", "было", "быть", "вам", "вас", "ваш",
            "весь", "вот", "для", "его", "ему", "если", "еще", "или", "как", "когда",
            "который", "мне", "мой", "она", "они", "оно", "перед", "при", "про", "свой",
            "так", "там", "тебя", "тебе", "тем", "то", "только", "тот", "тут", "ты",
            "уже", "что", "чтобы", "эта", "эти", "это", "этот"
        };

    private static Regex EnglishTokenRegex() => EnglishContextLexicon.TokenRegex();

    private static Regex RussianTokenRegex() => RussianMorphology.TokenRegex();

    [GeneratedRegex(
        @"(?:ый|ий|ой|ая|яя|ое|ее|ые|ие|ого|его|ому|ему|ым|им|ую|юю|ых|их|ыми|ими)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex RussianAdjectiveRegex();
}
