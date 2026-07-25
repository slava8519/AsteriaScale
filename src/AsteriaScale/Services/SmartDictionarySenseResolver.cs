using System.Text;
using System.Text.RegularExpressions;

namespace AsteriaScale.Services;

/// <summary>
/// A Russian surface form that belongs to one English dictionary sense.
/// SourceIndex is the stable row order from the dictionary sidecar.
/// </summary>
internal sealed record SmartDictionarySenseSurface(
    int SourceIndex,
    string Translation,
    string Register);

/// <summary>
/// One rankable English sense. Russian synonyms are deliberately kept out of the
/// model input: the model chooses a definition, then deterministic code chooses
/// a surface form from Surfaces.
/// </summary>
internal sealed record SmartDictionarySenseGroup(
    int SourceIndex,
    string EnglishDefinition,
    string PartOfSpeech,
    IReadOnlyList<SmartDictionarySenseSurface> Surfaces);

internal sealed record SmartDictionarySenseInventory(
    IReadOnlyList<SmartDictionarySenseGroup> Groups,
    string InferredPartOfSpeech,
    double PartOfSpeechConfidence,
    bool PartOfSpeechFilterApplied);

internal sealed record SmartDictionarySurfaceSelection(
    int SourceIndex,
    string Translation,
    double Confidence,
    string Reason);

/// <summary>
/// One distinct translation that can be shown to the user. Several English
/// glosses may intentionally point to the same outcome.
/// </summary>
internal sealed record SmartDictionaryRankerOutcome(
    int GroupId,
    int SourceIndex,
    string Translation);

/// <summary>
/// GlossBERT input aligned with stable, distinct visible translations. The
/// worker ranks CandidateGlosses, but its margin is calculated between the
/// CandidateGroupIds so duplicate glosses cannot create false uncertainty.
/// </summary>
internal sealed record SmartDictionaryRankerPlan(
    IReadOnlyList<SmartDictionaryRankerOutcome> Outcomes,
    IReadOnlyList<string> CandidateGlosses,
    IReadOnlyList<int> CandidateGroupIds);

internal sealed record SmartDictionaryTargetSpan(
    string Source,
    bool IsPhrase);

/// <summary>
/// Prepares aligned dictionary metadata for a contextual sense ranker and resolves
/// the selected sense to a Russian surface form without text generation.
///
/// The two operations are intentionally separate:
///  1. GroupCandidates creates unique (definition, POS) senses for the model.
///  2. SelectSurfaceForm applies morphology, register safety and stable source order.
/// </summary>
internal static partial class SmartDictionarySenseResolver
{
    internal const double MinimumPartOfSpeechFilterConfidence = 0.9;

    private const string Noun = "noun";
    private const string Verb = "verb";
    private const string Adjective = "adj";
    private const string Adverb = "adv";
    private const string Interjection = "intj";
    private const string Conjunction = "conj";
    private const string Determiner = "det";
    private const string Unknown = "";

    private readonly record struct EnglishContextAnalysis(
        string PartOfSpeech,
        double PartOfSpeechConfidence,
        string Subject,
        bool IsPast,
        bool IsProgressive,
        bool IsHabitualOrDurative,
        bool IsPatientConstruction,
        string GovernedPreposition,
        bool PreferDurationAgoSurface,
        bool PreferStandaloneSaveSurface);

    private readonly record struct RussianSurfaceAnalysis(
        RussianPastForm PastForm,
        string PastStem,
        bool IsInfinitive,
        bool IsSemelfactive);

    private sealed class SenseGroupBuilder
    {
        private readonly HashSet<string> _normalizedSurfaces = new(StringComparer.Ordinal);

        public SenseGroupBuilder(
            int sourceIndex,
            string definition,
            string partOfSpeech)
        {
            SourceIndex = sourceIndex;
            Definition = definition;
            PartOfSpeech = partOfSpeech;
        }

        public int SourceIndex { get; }
        public string Definition { get; }
        public string PartOfSpeech { get; }
        public List<SmartDictionarySenseSurface> Surfaces { get; } = [];

        public void AddSurface(int sourceIndex, string translation, string register)
        {
            string normalized = NormalizeRussianSurface(translation);
            if (normalized.Length == 0 || !_normalizedSurfaces.Add(normalized))
            {
                return;
            }

            Surfaces.Add(new SmartDictionarySenseSurface(
                sourceIndex,
                CollapseWhitespace(translation),
                CollapseWhitespace(register)));
        }

        public SmartDictionarySenseGroup Build() =>
            new(SourceIndex, Definition, PartOfSpeech, Surfaces.ToArray());
    }

    private static readonly HashSet<string> EnglishGetForms =
        new(StringComparer.Ordinal) { "get", "gets", "got", "gotten", "getting" };

    private static readonly HashSet<string> EnglishInterjections =
        new(StringComparer.Ordinal)
        {
            "ah", "aha", "alas", "eh", "hey", "hmm", "oh", "oops", "ouch", "ugh", "uh",
            "um", "wow", "yay"
        };

    private static readonly HashSet<string> EnglishAdverbialDirections =
        new(StringComparer.Ordinal)
        {
            "away", "downstairs", "home", "left", "right", "sideways", "upstairs"
        };

    private static readonly HashSet<string> EnglishVerbParticlesOrPrepositions =
        new(StringComparer.Ordinal)
        {
            "about", "along", "around", "away", "back", "down", "from", "in", "into",
            "off", "on", "out", "over", "through", "to", "up", "with"
        };

    private static readonly HashSet<string> TechnicalSurfaceTokens =
        new(StringComparer.Ordinal)
        {
            "adj", "adjective", "adv", "adverb", "archaic", "colloquial",
            "countable", "dialect", "f", "figurative", "formal", "impf",
            "imperfective", "intransitive", "literary", "m", "n", "obsolete",
            "off", "on", "on/off", "perfective", "pf", "pl", "plural", "rare",
            "sg", "singular", "slang", "technical", "transitive", "uncountable",
            "v", "verb", "vulgar", "with", "without"
        };

    private static readonly HashSet<string> DefinitionQualifierTokens =
        new(StringComparer.Ordinal)
        {
            "chiefly", "commonly", "especially", "generally", "often", "usually",
            "sometimes", "specifically", "typically", "visible"
        };

    private static readonly HashSet<string> DefinitionPolarityTokens =
        new(StringComparer.Ordinal)
        {
            "except", "excluding", "never", "no", "not", "without"
        };

    private static readonly HashSet<string> DefinitionSemanticGuardTokens =
        new(StringComparer.Ordinal)
        {
            "become", "cause", "caused", "causes", "causing", "lack", "only", "state"
        };

    private static readonly HashSet<string> DefinitionStopWords =
        new(StringComparer.Ordinal)
        {
            "a", "an", "and", "are", "as", "at", "be", "been", "being", "by", "can",
            "for", "from", "in", "is", "it", "its", "may", "of", "on", "one", "or",
            "someone", "something", "that", "the", "thing", "things", "this", "to",
            "used", "which", "who", "with"
        };

    /// <summary>
    /// Groups source rows by normalized English definition and canonical POS. The
    /// original ordering of both senses and Russian forms is preserved.
    /// </summary>
    public static SmartDictionarySenseInventory GroupCandidates(
        string context,
        string target,
        int characterIndex,
        IReadOnlyList<DictionarySenseCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        List<SenseGroupBuilder> builders = [];
        Dictionary<string, SenseGroupBuilder> bySense = new(StringComparer.Ordinal);
        for (int index = 0; index < candidates.Count; index++)
        {
            DictionarySenseCandidate candidate = candidates[index];
            string definition = CollapseWhitespace(candidate.EnglishDefinition);
            string definitionKey = NormalizeEnglishDefinition(definition);
            string partOfSpeech = CanonicalizePartOfSpeech(candidate.PartOfSpeech);
            string translation = CollapseWhitespace(candidate.Translation);
            if (definitionKey.Length == 0 || translation.Length == 0)
            {
                continue;
            }

            string key = definitionKey + "\u001f" + partOfSpeech;
            if (!bySense.TryGetValue(key, out SenseGroupBuilder? builder))
            {
                builder = builders.FirstOrDefault(existing =>
                    existing.PartOfSpeech == partOfSpeech &&
                    AreNearEquivalentDefinitions(
                        existing.Definition,
                        definition));
                if (builder is null)
                {
                    builder = new SenseGroupBuilder(index, definition, partOfSpeech);
                    builders.Add(builder);
                }

                bySense.Add(key, builder);
            }

            builder.AddSurface(index, translation, candidate.Register);
        }

        SmartDictionarySenseGroup[] allGroups = builders
            .Select(builder => builder.Build())
            .Where(group => group.Surfaces.Count > 0)
            .ToArray();
        EnglishContextAnalysis english = AnalyzeEnglishContext(
            context,
            target,
            characterIndex);
        if (english.PartOfSpeechConfidence < MinimumPartOfSpeechFilterConfidence ||
            english.PartOfSpeech.Length == 0)
        {
            return new SmartDictionarySenseInventory(
                allGroups,
                english.PartOfSpeech,
                english.PartOfSpeechConfidence,
                false);
        }

        SmartDictionarySenseGroup[] matchingGroups = allGroups
            .Where(group => IsCompatiblePartOfSpeech(
                english.PartOfSpeech,
                group.PartOfSpeech))
            .ToArray();

        // Fail open. Incomplete or unusual sidecar POS metadata must never erase
        // every sense. Also avoid claiming that a filter was applied when all
        // groups already have the inferred POS.
        if (matchingGroups.Length < 2 || matchingGroups.Length == allGroups.Length)
        {
            return new SmartDictionarySenseInventory(
                allGroups,
                english.PartOfSpeech,
                english.PartOfSpeechConfidence,
                false);
        }

        return new SmartDictionarySenseInventory(
            matchingGroups,
            english.PartOfSpeech,
            english.PartOfSpeechConfidence,
            true);
    }

    /// <summary>
    /// Selects a Russian surface form from an already selected English sense.
    /// This method never changes the chosen sense and never invokes a model.
    /// </summary>
    public static SmartDictionarySurfaceSelection? SelectSurfaceForm(
        string context,
        string target,
        int characterIndex,
        SmartDictionarySenseGroup selectedSense)
    {
        ArgumentNullException.ThrowIfNull(selectedSense);
        return SelectSurfaceForm(
            AnalyzeEnglishContext(context, target, characterIndex),
            selectedSense);
    }

    private static SmartDictionarySurfaceSelection? SelectSurfaceForm(
        EnglishContextAnalysis english,
        SmartDictionarySenseGroup selectedSense)
    {
        if (selectedSense.Surfaces.Count == 0)
        {
            return null;
        }

        if (english.IsPatientConstruction &&
            selectedSense.PartOfSpeech == Verb)
        {
            SmartDictionarySurfaceSelection? patient =
                SelectPatientConstructionSurface(
                    selectedSense.Surfaces,
                    english.GovernedPreposition);
            if (patient is not null)
            {
                return patient;
            }
        }

        if (selectedSense.Surfaces.Count == 1)
        {
            SmartDictionarySenseSurface only = selectedSense.Surfaces[0];
            return new SmartDictionarySurfaceSelection(
                only.SourceIndex,
                only.Translation,
                1,
                "single surface form");
        }

        var analyzed = selectedSense.Surfaces
            .Select(surface => (
                Surface: surface,
                Analysis: AnalyzeRussianSurface(surface.Translation)))
            .ToArray();

        RussianPastForm expectedPast = english.Subject switch
        {
            "he" => RussianPastForm.Masculine,
            "she" => RussianPastForm.Feminine,
            "it" => RussianPastForm.Neuter,
            "we" or "they" => RussianPastForm.Plural,
            _ => RussianPastForm.None
        };
        string pastParadigmStem = english.IsPast &&
                                  expectedPast != RussianPastForm.None
            ? FindPastParadigmStem(analyzed, expectedPast)
            : string.Empty;
        bool usePastAgreement = pastParadigmStem.Length > 0;
        bool hasSemelfactiveChoice =
            analyzed.Any(item => item.Analysis.IsInfinitive &&
                                 item.Analysis.IsSemelfactive) &&
            analyzed.Any(item => item.Analysis.IsInfinitive &&
                                 !item.Analysis.IsSemelfactive);
        bool useProgressiveAspect =
            hasSemelfactiveChoice &&
            (english.IsProgressive || english.IsHabitualOrDurative);
        bool useCompletedAspect =
            hasSemelfactiveChoice &&
            english.IsPast &&
            !english.IsProgressive &&
            !english.IsHabitualOrDurative;

        var ranked = analyzed
            .Select(item => new
            {
                item.Surface,
                SurfaceQualityRank = SurfaceQualityRank(item.Surface.Translation),
                ContextSurfaceRank = ContextSurfaceRank(
                    english,
                    item.Surface.Translation),
                MorphologyRank = MorphologyRank(
                    item.Analysis,
                    expectedPast,
                    pastParadigmStem,
                    usePastAgreement,
                    useProgressiveAspect,
                    useCompletedAspect),
                RegisterRank = RegisterRank(item.Surface.Register)
            })
            // A malformed dictionary annotation must not beat a clean synonym,
            // even when the malformed row happens to appear first in the source.
            .OrderBy(item => item.SurfaceQualityRank)
            .ThenBy(item => item.ContextSurfaceRank)
            .ThenBy(item => item.MorphologyRank)
            .ThenBy(item => item.RegisterRank)
            .ThenBy(item => item.Surface.SourceIndex)
            .ToArray();
        var best = ranked[0];

        string reason;
        double confidence;
        if (english.PreferDurationAgoSurface &&
            best.ContextSurfaceRank == 0 &&
            NormalizeRussianSurface(best.Surface.Translation) == "назад")
        {
            reason = "duration-before-now construction";
            confidence = 0.99;
        }
        else if (english.PreferStandaloneSaveSurface &&
                 best.ContextSurfaceRank == 0 &&
                 NormalizeRussianSurface(best.Surface.Translation) == "сохранить")
        {
            reason = "standalone save command";
            confidence = 0.99;
        }
        else if (ranked.Any(item =>
                     item.SurfaceQualityRank != best.SurfaceQualityRank))
        {
            reason = "cleaner Russian dictionary surface";
            confidence = 0.98;
        }
        else if (usePastAgreement && best.MorphologyRank == 0)
        {
            reason = "Russian past-tense agreement";
            confidence = 0.99;
        }
        else if (useProgressiveAspect && best.MorphologyRank == 0)
        {
            reason = "progressive or durative aspect";
            confidence = 0.96;
        }
        else if (useCompletedAspect && best.MorphologyRank == 0)
        {
            reason = "single completed action aspect";
            confidence = 0.93;
        }
        else if (ranked.Any(item => item.RegisterRank != best.RegisterRank))
        {
            reason = "safer dictionary register";
            confidence = 0.9;
        }
        else
        {
            reason = "stable dictionary source order";
            confidence = 0.8;
        }

        return new SmartDictionarySurfaceSelection(
            best.Surface.SourceIndex,
            best.Surface.Translation,
            confidence,
            reason);
    }

    /// <summary>
    /// Grouping and plan construction as one step, for the two production
    /// consumers that always perform both with the same arguments: the hover
    /// pipeline and the full-text scaffold. Passing different arguments to the
    /// two stages, or applying one without the other, produces a plan whose
    /// GroupIds no longer describe the ranked senses — so the pair is not
    /// something a caller should be able to get half right.
    ///
    /// The two-stage form stays public: tests assert on the inventory's POS
    /// inference, which no production path consumes.
    /// </summary>
    public static SmartDictionaryRankerPlan CreateRankerPlan(
        string context,
        string target,
        int characterIndex,
        IReadOnlyList<DictionarySenseCandidate> candidates) =>
        CreateRankerPlan(
            context,
            target,
            characterIndex,
            GroupCandidates(context, target, characterIndex, candidates).Groups);

    /// <summary>
    /// Resolves morphology and register before neural ranking, then merges
    /// senses that would produce the same visible Russian result. This keeps
    /// the model's abstention margin semantically meaningful.
    /// </summary>
    public static SmartDictionaryRankerPlan CreateRankerPlan(
        string context,
        string target,
        int characterIndex,
        IReadOnlyList<SmartDictionarySenseGroup> senses)
    {
        ArgumentNullException.ThrowIfNull(senses);

        List<SmartDictionaryRankerOutcome> outcomes = [];
        List<string> glosses = [];
        List<int> groupIds = [];
        Dictionary<string, int> groupByTranslation =
            new(StringComparer.Ordinal);

        EnglishContextAnalysis english = AnalyzeEnglishContext(
            context,
            target,
            characterIndex);
        foreach (SmartDictionarySenseGroup sense in senses
                     .OrderBy(group => group.SourceIndex))
        {
            SmartDictionarySurfaceSelection? surface = SelectSurfaceForm(
                english,
                sense);
            string gloss = CollapseWhitespace(sense.EnglishDefinition);
            if (surface is null || gloss.Length == 0)
            {
                continue;
            }

            string translationKey = NormalizeRussianSurface(surface.Translation);
            if (translationKey.Length == 0)
            {
                continue;
            }

            if (!groupByTranslation.TryGetValue(translationKey, out int groupId))
            {
                groupId = outcomes.Count;
                groupByTranslation.Add(translationKey, groupId);
                outcomes.Add(new SmartDictionaryRankerOutcome(
                    groupId,
                    surface.SourceIndex,
                    surface.Translation));
            }

            glosses.Add(gloss);
            groupIds.Add(groupId);
        }

        return new SmartDictionaryRankerPlan(
            outcomes.ToArray(),
            glosses.ToArray(),
            groupIds.ToArray());
    }

    internal static string CanonicalizePartOfSpeech(string? value)
    {
        string normalized = CollapseWhitespace(value ?? string.Empty)
            .ToLowerInvariant()
            .Replace('-', '_');
        return normalized switch
        {
            "n" or "noun" or "common_noun" => Noun,
            "v" or "verb" => Verb,
            "a" or "adj" or "adjective" => Adjective,
            "r" or "adv" or "adverb" => Adverb,
            "intj" or "interj" or "interjection" => Interjection,
            "proper_noun" or "proper name" or "name" => "name",
            "pron" or "pronoun" => "pron",
            "prep" or "preposition" => "prep",
            "prep_phrase" or "prepositional_phrase" => "prep_phrase",
            "conj" or "conjunction" => Conjunction,
            "det" or "determiner" or "article" => Determiner,
            "phrase" => "phrase",
            "proverb" => "proverb",
            "particle" => "particle",
            _ => normalized
        };
    }

    public static SmartDictionaryTargetSpan ResolveTargetSpan(
        string context,
        string target,
        int characterIndex)
    {
        Match[] tokens = EnglishTokenRegex().Matches(context ?? string.Empty)
            .Cast<Match>()
            .ToArray();
        string[] targetTokens = EnglishTokenRegex().Matches(target ?? string.Empty)
            .Select(match => NormalizeEnglishToken(match.Value))
            .Where(value => value.Length > 0)
            .ToArray();
        if (targetTokens.Length != 1)
        {
            return new SmartDictionaryTargetSpan(
                target ?? string.Empty,
                targetTokens.Length > 1);
        }

        int tokenIndex = FindTargetToken(tokens, targetTokens[0], characterIndex);
        if (tokenIndex <= 0)
        {
            return new SmartDictionaryTargetSpan(target ?? string.Empty, false);
        }

        string current = NormalizeEnglishToken(tokens[tokenIndex].Value);
        string previous = NormalizeEnglishToken(tokens[tokenIndex - 1].Value);
        string next = tokenIndex + 1 < tokens.Length
            ? NormalizeEnglishToken(tokens[tokenIndex + 1].Value)
            : string.Empty;
        if (!IsPatientConstruction(previous, current, next))
        {
            return new SmartDictionaryTargetSpan(target ?? string.Empty, false);
        }

        int last = next is "by" or "from"
            ? tokenIndex + 1
            : tokenIndex;
        string source = string.Join(
            " ",
            tokens[(tokenIndex - 1)..(last + 1)].Select(token => token.Value));
        return new SmartDictionaryTargetSpan(source, true);
    }

    private static bool IsCompatiblePartOfSpeech(string inferred, string candidate) =>
        inferred == candidate ||
        inferred == Noun && candidate == "name";

    private static EnglishContextAnalysis AnalyzeEnglishContext(
        string context,
        string target,
        int characterIndex)
    {
        Match[] contextTokens = EnglishTokenRegex().Matches(context ?? string.Empty)
            .Cast<Match>()
            .ToArray();
        string[] targetTokens = EnglishTokenRegex().Matches(target ?? string.Empty)
            .Select(match => NormalizeEnglishToken(match.Value))
            .Where(value => value.Length > 0)
            .ToArray();
        if (targetTokens.Length != 1)
        {
            return new EnglishContextAnalysis(
                Unknown,
                0,
                string.Empty,
                false,
                false,
                false,
                false,
                string.Empty,
                false,
                false);
        }

        string targetWord = targetTokens[0];
        int tokenIndex = FindTargetToken(contextTokens, targetWord, characterIndex);
        string current = tokenIndex >= 0
            ? NormalizeEnglishToken(contextTokens[tokenIndex].Value)
            : targetWord;
        string previous = tokenIndex > 0
            ? NormalizeEnglishToken(contextTokens[tokenIndex - 1].Value)
            : string.Empty;
        string next = tokenIndex >= 0 && tokenIndex + 1 < contextTokens.Length
            ? NormalizeEnglishToken(contextTokens[tokenIndex + 1].Value)
            : string.Empty;

        bool isProgressive =
            current.EndsWith("ing", StringComparison.Ordinal) &&
            EnglishContextLexicon.AuxiliaryBe.Contains(previous);
        bool isPast = EnglishContextLexicon.IsLikelyPastForm(current);
        bool isHabitualOrDurative =
            contextTokens.Any(token =>
                EnglishContextLexicon.HabitualOrDurativeCues.Contains(
                    NormalizeEnglishToken(token.Value))) ||
            ContainsNormalizedPhrase(context, "used to") ||
            ContainsNormalizedPhrase(context, "every day") ||
            ContainsNormalizedPhrase(context, "all day") ||
            ContainsNormalizedPhrase(context, "all night");
        bool isPatientConstruction =
            IsPatientConstruction(previous, current, next);
        string governedPreposition =
            isPatientConstruction && next is ("by" or "from")
                ? next
                : string.Empty;
        bool preferDurationAgoSurface =
            current == "ago" &&
            IsDurationAgoContext(contextTokens, tokenIndex);
        bool preferStandaloneSaveSurface =
            current == "save" &&
            tokenIndex == 0 &&
            contextTokens.Length == 1;

        (string partOfSpeech, double confidence) = InferEnglishPartOfSpeech(
            current,
            previous,
            next,
            tokenIndex,
            contextTokens,
            isProgressive,
            isPast);
        return new EnglishContextAnalysis(
            partOfSpeech,
            confidence,
            EnglishContextLexicon.FindSubject(contextTokens, tokenIndex),
            isPast,
            isProgressive,
            isHabitualOrDurative,
            isPatientConstruction,
            governedPreposition,
            preferDurationAgoSurface,
            preferStandaloneSaveSurface);
    }

    private static bool IsPatientConstruction(
        string previous,
        string current,
        string next)
    {
        bool looksLikeParticiple =
            EnglishContextLexicon.IsLikelyPastForm(current) ||
            current.EndsWith("en", StringComparison.Ordinal) && current.Length > 3;
        if (!looksLikeParticiple)
        {
            return false;
        }

        return EnglishGetForms.Contains(previous) ||
               EnglishContextLexicon.AuxiliaryBe.Contains(previous) &&
               next is ("by" or "from");
    }

    private static SmartDictionarySurfaceSelection? SelectPatientConstructionSurface(
        IReadOnlyList<SmartDictionarySenseSurface> surfaces,
        string governedPreposition)
    {
        var infinitives = surfaces
            .Select(surface => new
            {
                Surface = surface,
                Word = SingleRussianInfinitive(surface.Translation),
                SurfaceQualityRank = SurfaceQualityRank(surface.Translation),
                RegisterRank = RegisterRank(surface.Register)
            })
            .Where(candidate => candidate.Word.Length > 0)
            .OrderBy(candidate => candidate.SurfaceQualityRank)
            .ThenBy(candidate => candidate.RegisterRank)
            .ThenBy(candidate => candidate.Surface.SourceIndex)
            .ToArray();
        if (infinitives.Length == 0)
        {
            return null;
        }

        var selected = infinitives.FirstOrDefault(candidate =>
                           candidate.Word.EndsWith("ться", StringComparison.Ordinal) ||
                           candidate.Word.EndsWith("тись", StringComparison.Ordinal) ||
                           candidate.Word.EndsWith("чься", StringComparison.Ordinal))
                       ?? infinitives.FirstOrDefault(candidate =>
                           HasPerfectiveCounterpart(candidate.Word, infinitives.Select(
                               item => item.Word)))
                       ?? infinitives[0];
        string reflexive = ToRussianReflexiveInfinitive(selected.Word);
        if (reflexive.Length == 0)
        {
            return null;
        }

        string preposition = governedPreposition == "from" ? " от" : string.Empty;
        return new SmartDictionarySurfaceSelection(
            selected.Surface.SourceIndex,
            reflexive + preposition,
            0.94,
            "passive or result-state construction");
    }

    private static string SingleRussianInfinitive(string translation)
    {
        string[] words = RussianTokenRegex().Matches(translation)
            .Select(match => match.Value.ToLowerInvariant().Replace('ё', 'е'))
            .ToArray();
        return words.Length == 1 && RussianMorphology.IsInfinitive(words[0])
            ? words[0]
            : string.Empty;
    }

    private static bool HasPerfectiveCounterpart(
        string candidate,
        IEnumerable<string> alternatives)
    {
        if (!candidate.EndsWith("ить", StringComparison.Ordinal) ||
            candidate.Length <= 4)
        {
            return false;
        }

        string stem = candidate[..^3];
        return alternatives.Any(alternative =>
            alternative.EndsWith("ять", StringComparison.Ordinal) &&
            alternative.Length > 4 &&
            string.Equals(alternative[..^3], stem, StringComparison.Ordinal));
    }

    private static string ToRussianReflexiveInfinitive(string word)
    {
        if (word.EndsWith("ться", StringComparison.Ordinal) ||
            word.EndsWith("тись", StringComparison.Ordinal) ||
            word.EndsWith("чься", StringComparison.Ordinal))
        {
            return word;
        }

        if (word.EndsWith("ть", StringComparison.Ordinal))
        {
            return word[..^2] + "ться";
        }

        if (word.EndsWith("ти", StringComparison.Ordinal))
        {
            return word + "сь";
        }

        return word.EndsWith("чь", StringComparison.Ordinal)
            ? word + "ся"
            : string.Empty;
    }

    private static (string PartOfSpeech, double Confidence) InferEnglishPartOfSpeech(
        string word,
        string previous,
        string next,
        int tokenIndex,
        IReadOnlyList<Match> tokens,
        bool isProgressive,
        bool isPast)
    {
        if (word == "while")
        {
            if (IsWhileNounContext(previous, tokenIndex, tokens))
            {
                return (Noun, 0.99);
            }

            if (IsLikelyClauseAfterWhile(next))
            {
                return (Conjunction, 0.97);
            }
        }

        if (word == "that")
        {
            if (IsThatConjunctionContext(previous, next))
            {
                return (Conjunction, 0.98);
            }

            if (IsThatDeterminerContext(previous, next, tokenIndex, tokens))
            {
                return (Determiner, 0.96);
            }
        }

        if (EnglishInterjections.Contains(word) &&
            IsClauseInitial(tokens, tokenIndex))
        {
            return (Interjection, 0.98);
        }

        if (previous == "to" || EnglishContextLexicon.Modals.Contains(previous))
        {
            // "went to bed" and "went to sleep" look identical here, so a
            // prepositional-looking "to" keeps the verb reading only as a weak
            // prior that never drives the hard POS filter.
            string beforeTo = previous == "to" && tokenIndex >= 2
                ? NormalizeEnglishToken(tokens[tokenIndex - 2].Value)
                : string.Empty;
            return EnglishContextLexicon.MotionWordsBeforeTo.Contains(beforeTo)
                ? (Verb, 0.85)
                : (Verb, 0.99);
        }

        if (isProgressive || EnglishContextLexicon.AuxiliaryHave.Contains(previous))
        {
            return (Verb, 0.98);
        }

        if (word.EndsWith("ing", StringComparison.Ordinal) &&
            EnglishVerbParticlesOrPrepositions.Contains(next) &&
            !EnglishContextLexicon.Determiners.Contains(previous) &&
            !EnglishContextLexicon.Possessives.Contains(previous))
        {
            return (Verb, 0.96);
        }

        if (word.EndsWith("ing", StringComparison.Ordinal) &&
            (EnglishContextLexicon.Determiners.Contains(previous) || EnglishContextLexicon.Possessives.Contains(previous)) &&
            next.Length > 0)
        {
            return (Adjective, 0.94);
        }

        if (EnglishAdverbialDirections.Contains(word) &&
            IsLikelyFiniteVerb(previous) &&
            (next.Length == 0 || next is "and" or "but" or "then"))
        {
            return (Adverb, 0.95);
        }

        if (isPast &&
            (EnglishContextLexicon.SubjectPronouns.Contains(previous) ||
             previous.Length > 0 && !EnglishContextLexicon.Determiners.Contains(previous)))
        {
            return (Verb, 0.94);
        }

        if (word.EndsWith("ly", StringComparison.Ordinal) &&
            word.Length > 4 &&
            word is not ("friendly" or "likely" or "lonely" or "lovely" or "ugly"))
        {
            return (Adverb, 0.97);
        }

        if (word.EndsWith("ness", StringComparison.Ordinal) ||
            word.EndsWith("ment", StringComparison.Ordinal) ||
            word.EndsWith("tion", StringComparison.Ordinal) ||
            word.EndsWith("ity", StringComparison.Ordinal) ||
            word.EndsWith("ship", StringComparison.Ordinal) ||
            word.EndsWith("hood", StringComparison.Ordinal))
        {
            return (Noun, 0.94);
        }

        if (word.EndsWith("ous", StringComparison.Ordinal) ||
            word.EndsWith("ful", StringComparison.Ordinal) ||
            word.EndsWith("less", StringComparison.Ordinal) ||
            word.EndsWith("ive", StringComparison.Ordinal) ||
            word.EndsWith("able", StringComparison.Ordinal) ||
            word.EndsWith("ible", StringComparison.Ordinal))
        {
            return (Adjective, 0.93);
        }

        if (EnglishContextLexicon.LinkingVerbs.Contains(previous))
        {
            return (Adjective, 0.91);
        }

        if ((EnglishContextLexicon.Determiners.Contains(previous) || EnglishContextLexicon.Possessives.Contains(previous)) &&
            (next.Length == 0 ||
             IsLikelyFiniteVerb(next) ||
             IsLikelyNounFollower(next) ||
             IsCoordinatedPredicateBoundary(tokens, tokenIndex)))
        {
            return (Noun, 0.94);
        }

        if ((EnglishContextLexicon.Determiners.Contains(previous) || EnglishContextLexicon.Possessives.Contains(previous)) &&
            next.Length > 0 &&
            !IsCoordinatingConjunction(next))
        {
            return (Adjective, 0.92);
        }

        if (EnglishAdverbialDirections.Contains(word) &&
            previous.Length > 0 &&
            !EnglishContextLexicon.Determiners.Contains(previous) &&
            next.Length == 0)
        {
            return (Adverb, 0.92);
        }

        if (EnglishContextLexicon.SubjectPronouns.Contains(previous))
        {
            return (Verb, 0.91);
        }

        return (Unknown, 0);
    }

    private static bool IsWhileNounContext(
        string previous,
        int tokenIndex,
        IReadOnlyList<Match> tokens)
    {
        if (EnglishContextLexicon.Determiners.Contains(previous) ||
            EnglishContextLexicon.Possessives.Contains(previous))
        {
            return true;
        }

        if (previous is not ("little" or "long" or "short") || tokenIndex < 2)
        {
            return false;
        }

        string beforeModifier = NormalizeEnglishToken(tokens[tokenIndex - 2].Value);
        return EnglishContextLexicon.Determiners.Contains(beforeModifier) ||
               EnglishContextLexicon.Possessives.Contains(beforeModifier);
    }

    private static bool IsLikelyClauseAfterWhile(string next) =>
        EnglishContextLexicon.SubjectPronouns.Contains(next) ||
        next == "there" ||
        next.EndsWith("ing", StringComparison.Ordinal) && next.Length > 4;

    private static bool IsThatConjunctionContext(string previous, string next) =>
        previous == "so" ||
        EnglishContextLexicon.SubjectPronouns.Contains(next) ||
        next == "there";

    private static bool IsThatDeterminerContext(
        string previous,
        string next,
        int tokenIndex,
        IReadOnlyList<Match> tokens)
    {
        if (next.Length == 0 ||
            EnglishContextLexicon.SubjectPronouns.Contains(next) ||
            EnglishContextLexicon.AuxiliaryBe.Contains(next) ||
            EnglishContextLexicon.AuxiliaryHave.Contains(next) ||
            EnglishContextLexicon.Modals.Contains(next) ||
            IsLikelyFiniteVerb(next) ||
            EnglishContextLexicon.DemonstrativePredicateFollowers.Contains(next))
        {
            return false;
        }

        if (EnglishContextLexicon.PrepositionsBeforeDeterminer.Contains(previous))
        {
            return true;
        }

        if (tokenIndex != 0)
        {
            return false;
        }

        if (tokens.Count == 2)
        {
            return true;
        }

        string afterNext = NormalizeEnglishToken(tokens[tokenIndex + 2].Value);
        return IsLikelyFiniteVerb(afterNext) ||
               EnglishContextLexicon.DemonstrativePredicateFollowers.Contains(afterNext);
    }

    private static bool IsLikelyFiniteVerb(string word) =>
        EnglishContextLexicon.Modals.Contains(word) ||
        EnglishContextLexicon.IsLikelyPastForm(word) ||
        word is "am" or "is" or "are" or "was" or "were" or "has" or "have" or "had" or
            "does" or "do" or "did";

    private static bool IsLikelyNounFollower(string word) =>
        word is "of" or "in" or "on" or "at" or "from" or "with" or "for" or "by" or "who" or
            "which" or "that";

    private static bool IsCoordinatedPredicateBoundary(
        IReadOnlyList<Match> tokens,
        int tokenIndex)
    {
        if (tokenIndex < 0 || tokenIndex + 2 >= tokens.Count)
        {
            return false;
        }

        string conjunction = NormalizeEnglishToken(tokens[tokenIndex + 1].Value);
        if (!IsCoordinatingConjunction(conjunction))
        {
            return false;
        }

        string following = NormalizeEnglishToken(tokens[tokenIndex + 2].Value);
        return IsLikelyFiniteVerb(following) ||
               EnglishContextLexicon.Modals.Contains(following) ||
               EnglishContextLexicon.SubjectPronouns.Contains(following);
    }

    private static bool IsCoordinatingConjunction(string word) =>
        word is "and" or "but" or "or" or "nor" or "yet";

    private static bool IsClauseInitial(IReadOnlyList<Match> tokens, int tokenIndex)
    {
        if (tokenIndex <= 0)
        {
            return true;
        }

        int previousEnd = tokens[tokenIndex - 1].Index + tokens[tokenIndex - 1].Length;
        int currentStart = tokens[tokenIndex].Index;
        // Regex indices are sufficient here: a gap wider than ordinary whitespace
        // normally contains sentence punctuation or a speaker marker.
        return currentStart - previousEnd > 2;
    }

    private static int FindTargetToken(
        IReadOnlyList<Match> tokens,
        string target,
        int characterIndex) =>
        EnglishContextLexicon.FindTargetToken(
            tokens,
            target,
            characterIndex,
            fallbackToNearest: false);

    private static bool ContainsNormalizedPhrase(string? value, string phrase)
    {
        string normalized = string.Join(
            " ",
            EnglishTokenRegex().Matches(value ?? string.Empty)
                .Select(match => NormalizeEnglishToken(match.Value)));
        return (" " + normalized + " ").Contains(
            " " + phrase + " ",
            StringComparison.Ordinal);
    }

    private static RussianSurfaceAnalysis AnalyzeRussianSurface(string translation)
    {
        string[] words = RussianTokenRegex().Matches(translation)
            .Select(match => match.Value.ToLowerInvariant().Replace('ё', 'е'))
            .ToArray();
        RussianPastForm pastForm = RussianPastForm.None;
        string pastStem = string.Empty;
        foreach (string word in words)
        {
            (RussianPastForm form, string stem) = RussianMorphology.DetectPastForm(word);
            if (form == RussianPastForm.None)
            {
                continue;
            }

            pastForm = form;
            pastStem = stem;
            break;
        }

        bool isInfinitive = words.Any(RussianMorphology.IsInfinitive);
        bool isSemelfactive = words.Any(RussianMorphology.IsSemelfactiveInfinitive);
        return new RussianSurfaceAnalysis(
            pastForm,
            pastStem,
            isInfinitive,
            isSemelfactive);
    }

    private static string FindPastParadigmStem(
        IReadOnlyList<(
            SmartDictionarySenseSurface Surface,
            RussianSurfaceAnalysis Analysis)> candidates,
        RussianPastForm expected)
    {
        var matchingParadigm = candidates
            .Where(item =>
                item.Analysis.PastForm != RussianPastForm.None &&
                item.Analysis.PastStem.Length > 1)
            .GroupBy(item => item.Analysis.PastStem, StringComparer.Ordinal)
            .Where(group =>
                group.Select(item => item.Analysis.PastForm).Distinct().Count() >= 2 &&
                group.Any(item => item.Analysis.PastForm == expected))
            .OrderBy(group => group.Min(item => item.Surface.SourceIndex))
            .FirstOrDefault();
        return matchingParadigm?.Key ?? string.Empty;
    }

    private static int MorphologyRank(
        RussianSurfaceAnalysis candidate,
        RussianPastForm expectedPast,
        string pastParadigmStem,
        bool usePastAgreement,
        bool useProgressiveAspect,
        bool useCompletedAspect)
    {
        if (usePastAgreement)
        {
            if (candidate.PastStem == pastParadigmStem &&
                candidate.PastForm == expectedPast)
            {
                return 0;
            }

            return candidate.PastStem == pastParadigmStem ? 2 : 1;
        }

        if (useProgressiveAspect)
        {
            return candidate.IsInfinitive && !candidate.IsSemelfactive ? 0 : 1;
        }

        if (useCompletedAspect)
        {
            return candidate.IsInfinitive && candidate.IsSemelfactive ? 0 : 1;
        }

        return 0;
    }

    private static bool IsDurationAgoContext(
        IReadOnlyList<Match> tokens,
        int tokenIndex)
    {
        if (tokenIndex <= 0)
        {
            return false;
        }

        string previous = NormalizeEnglishToken(tokens[tokenIndex - 1].Value);
        return EnglishContextLexicon.DurationWordsBeforeAgo.Contains(previous);
    }

    private static int ContextSurfaceRank(
        EnglishContextAnalysis english,
        string translation)
    {
        string normalized = NormalizeRussianSurface(translation);
        if (english.PreferDurationAgoSurface)
        {
            return normalized switch
            {
                "назад" => 0,
                _ when normalized.EndsWith(" назад", StringComparison.Ordinal) => 1,
                _ => 2
            };
        }

        if (english.PreferStandaloneSaveSurface)
        {
            return normalized switch
            {
                "сохранить" => 0,
                "сохранять" => 1,
                _ => 2
            };
        }

        return 0;
    }

    private static int SurfaceQualityRank(string translation)
    {
        string value = CollapseWhitespace(translation).Normalize(NormalizationForm.FormKC);
        if (value.Length == 0)
        {
            return int.MaxValue;
        }

        int rank = HasBalancedSurfaceDelimiters(value) ? 0 : 100;
        Match[] latinTokens = LatinSurfaceTokenRegex().Matches(value)
            .Cast<Match>()
            .ToArray();
        if (latinTokens.Length == 0)
        {
            return rank;
        }

        bool hasCyrillic = value.Any(character =>
            character is >= '\u0400' and <= '\u052f');
        bool hasTechnicalFragment = latinTokens.Any(match =>
        {
            string token = match.Value.ToLowerInvariant();
            return TechnicalSurfaceTokens.Contains(token) ||
                   hasCyrillic && token.Contains('/');
        });
        return hasTechnicalFragment ? rank + 50 : rank;
    }

    private static bool HasBalancedSurfaceDelimiters(string value)
    {
        Stack<char> openings = new();
        foreach (char character in value)
        {
            if (character is '(' or '[' or '{')
            {
                openings.Push(character);
                continue;
            }

            if (character is not (')' or ']' or '}'))
            {
                continue;
            }

            if (openings.Count == 0)
            {
                return false;
            }

            char opening = openings.Pop();
            if (character == ')' && opening != '(' ||
                character == ']' && opening != '[' ||
                character == '}' && opening != '{')
            {
                return false;
            }
        }

        return openings.Count == 0;
    }

    private static int RegisterRank(string register)
    {
        string normalized = CollapseWhitespace(register).ToLowerInvariant();
        if (normalized.Length == 0)
        {
            return 0;
        }

        if (ContainsAny(normalized, "диал", "dialect"))
        {
            return 10;
        }

        if (ContainsAny(normalized, "устар", "archaic", "obsolete"))
        {
            return 9;
        }

        if (ContainsAny(normalized, "груб", "vulgar", "offensive"))
        {
            return 8;
        }

        if (ContainsAny(normalized, "жарг", "slang"))
        {
            return 7;
        }

        if (ContainsAny(normalized, "прост", "colloq", "разг"))
        {
            return 2;
        }

        if (ContainsAny(normalized, "лит", "literary", "poetic"))
        {
            return 1;
        }

        // Domain and figurative labels describe the selected sense rather than
        // making its Russian surface unsafe.
        return 0;
    }

    private static bool ContainsAny(string value, params string[] fragments) =>
        fragments.Any(fragment =>
            value.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static string NormalizeEnglishDefinition(string value) =>
        CollapseWhitespace(value)
            .Normalize(NormalizationForm.FormKC)
            .ToLowerInvariant();

    private static bool AreNearEquivalentDefinitions(string left, string right)
    {
        string[] leftTokens = EnglishTokenRegex().Matches(left)
            .Select(match => NormalizeEnglishToken(match.Value))
            .ToArray();
        string[] rightTokens = EnglishTokenRegex().Matches(right)
            .Select(match => NormalizeEnglishToken(match.Value))
            .ToArray();
        if (leftTokens.Length == 0 || rightTokens.Length == 0 ||
            !HaveSameDefinitionGuards(leftTokens, rightTokens))
        {
            return false;
        }

        if (Math.Abs(leftTokens.Length - rightTokens.Length) == 1)
        {
            string[] longer = leftTokens.Length > rightTokens.Length
                ? leftTokens
                : rightTokens;
            string[] shorter = leftTokens.Length > rightTokens.Length
                ? rightTokens
                : leftTokens;
            if (shorter.Length >= 7)
            {
                int shortIndex = 0;
                string? omitted = null;
                for (int longIndex = 0; longIndex < longer.Length; longIndex++)
                {
                    if (shortIndex < shorter.Length &&
                        string.Equals(
                            longer[longIndex],
                            shorter[shortIndex],
                            StringComparison.Ordinal))
                    {
                        shortIndex++;
                        continue;
                    }

                    if (omitted is not null)
                    {
                        omitted = string.Empty;
                        break;
                    }

                    omitted = longer[longIndex];
                }

                if (shortIndex == shorter.Length &&
                    omitted is not null &&
                    DefinitionQualifierTokens.Contains(omitted))
                {
                    return true;
                }
            }
        }

        HashSet<string> leftContent = leftTokens
            .Where(token => !DefinitionStopWords.Contains(token))
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> rightContent = rightTokens
            .Where(token => !DefinitionStopWords.Contains(token))
            .ToHashSet(StringComparer.Ordinal);
        int common = leftContent.Intersect(rightContent).Count();
        if (common < 3)
        {
            return false;
        }

        int union = leftContent.Union(rightContent).Count();
        double jaccard = union == 0 ? 0 : common / (double)union;
        double containment = common /
                             (double)Math.Max(
                                 1,
                                 Math.Min(leftContent.Count, rightContent.Count));
        return jaccard >= 0.75 ||
               containment >= 0.9 &&
               Math.Abs(leftContent.Count - rightContent.Count) <= 2;
    }

    private static bool HaveSameDefinitionGuards(
        IReadOnlyCollection<string> left,
        IReadOnlyCollection<string> right)
    {
        foreach (string guard in DefinitionPolarityTokens.Concat(
                     DefinitionSemanticGuardTokens))
        {
            if (left.Contains(guard) != right.Contains(guard))
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizeEnglishToken(string value) =>
        EnglishContextLexicon.NormalizeToken(value);

    private static string NormalizeRussianSurface(string value) =>
        CollapseWhitespace(value)
            .Normalize(NormalizationForm.FormKC)
            .ToLowerInvariant()
            .Replace('ё', 'е');

    private static string CollapseWhitespace(string value) =>
        string.Join(
            " ",
            (value ?? string.Empty).Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));

    private static Regex EnglishTokenRegex() => EnglishContextLexicon.TokenRegex();

    private static Regex RussianTokenRegex() => RussianMorphology.TokenRegex();

    [GeneratedRegex(
        @"[A-Za-z]+(?:[/.-][A-Za-z]+)*",
        RegexOptions.CultureInvariant)]
    private static partial Regex LatinSurfaceTokenRegex();
}
