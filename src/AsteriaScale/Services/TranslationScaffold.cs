using System.Text.RegularExpressions;

namespace AsteriaScale.Services;

internal sealed record ScaffoldTerm(
    string Source,
    string Translation,
    double Confidence);

internal sealed record TranslationScaffold(
    IReadOnlyList<ScaffoldTerm> Terms,
    string? ExactSentenceTranslation,
    IReadOnlyList<string> ProperNames)
{
    public static TranslationScaffold Empty { get; } = new(
        Array.Empty<ScaffoldTerm>(),
        ExactSentenceTranslation: null,
        Array.Empty<string>());
}

internal sealed record TranslationScaffoldNeuralRequest(
    string Sentence,
    string Target,
    int CharacterIndex,
    SmartDictionaryRankerPlan Plan);

internal delegate Task<SmartDictionaryRankerOutcome?> TranslationScaffoldNeuralRanker(
    TranslationScaffoldNeuralRequest request,
    CancellationToken cancellationToken);

/// <summary>
/// Builds high-confidence terminology constraints deterministically by default.
/// A neural ranker is available only for an explicitly requested single hover
/// fallback; batch translation never starts one ranker request per token.
/// </summary>
internal sealed class TranslationScaffoldBuilder
{
    internal const double MinimumConfidence = 0.9;
    internal const int MaximumTermCount = 12;
    // Mandatory prompt constraints are stricter than hover suggestions. The
    // 0.90-0.94 heuristic band includes reproducible false senses such as
    // "saw" -> "пилить" and "fifth" -> "квинта" in ordinary sentences.
    private const double MinimumHeuristicConfidence = 0.95;

    private static readonly HashSet<string> ScaffoldStopWords =
        new(StringComparer.Ordinal)
        {
            "a", "an", "the", "i", "me", "my", "mine", "myself", "you", "your",
            "yours", "yourself", "yourselves", "he", "him", "his", "himself", "she",
            "her", "hers", "herself", "it", "its", "itself", "we", "us", "our",
            "ours", "ourselves", "they", "them", "their", "theirs", "themselves",
            "this", "that", "these", "those", "anyone", "anything", "everyone",
            "everything", "nobody", "nothing", "someone", "something", "who", "what",
            "which", "am", "is", "are", "was", "were", "be", "been", "being", "have",
            "has", "had", "having", "do", "does", "did", "doing", "can", "could",
            "may", "might", "must", "shall", "should", "will", "would", "and", "or",
            "but", "if", "as", "at", "by", "for", "from", "in", "of", "on", "to",
            "with", "without"
        };

    private readonly LocalDictionaryService _dictionary;
    private readonly TranslationScaffoldNeuralRanker? _neuralRanker;

    public TranslationScaffoldBuilder(
        LocalDictionaryService dictionary,
        TranslationScaffoldNeuralRanker? neuralRanker = null)
    {
        _dictionary = dictionary ?? throw new ArgumentNullException(nameof(dictionary));
        _neuralRanker = neuralRanker;
    }

    public Task<TranslationScaffold> BuildAsync(
        string sentence,
        CancellationToken cancellationToken = default) =>
        BuildAsync(sentence, useNeuralRanker: false, cancellationToken);

    internal async Task<TranslationScaffold> BuildAsync(
        string sentence,
        bool useNeuralRanker,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sentence))
        {
            return TranslationScaffold.Empty;
        }

        if (sentence.Any(character =>
                character is >= '\u3040' and <= '\u30ff' or
                >= '\u3400' and <= '\u9fff'))
        {
            // Japanese glossary extraction is roadmap #9. Feeding partial
            // Latin fragments from mixed OCR would create false constraints.
            return TranslationScaffold.Empty;
        }

        try
        {
            // The shipped dialogue keys predate numeric sentence-key support,
            // so "at 1" and "at 9" collide after normalization. Never bypass
            // the model when the source contains a digit until that asset is
            // rebuilt under a synchronized runtime/generator contract.
            string? exactSentenceTranslation = sentence.Any(char.IsDigit)
                ? null
                : NormalizeExactSentenceTranslation(
                    await _dictionary.LookupExactEnglishDialogueAsync(
                        sentence,
                        cancellationToken));
            Match[] tokens = EnglishContextLexicon.TokenRegex()
                .Matches(sentence)
                .Cast<Match>()
                .ToArray();
            if (tokens.Length == 0)
            {
                return exactSentenceTranslation is null
                    ? TranslationScaffold.Empty
                    : new TranslationScaffold(
                        Array.Empty<ScaffoldTerm>(),
                        exactSentenceTranslation,
                        Array.Empty<string>());
            }

            List<TermCandidate> candidates = [];
            bool neuralRankerAvailable = useNeuralRanker && _neuralRanker is not null;
            for (int tokenIndex = 0; tokenIndex < tokens.Length; tokenIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Match token = tokens[tokenIndex];
                bool explicitUserToken = _dictionary.IsUserEnglishKey(token.Value);
                DictionaryLookupResult lookup =
                    await _dictionary.LookupWithSenseMetadataAsync(
                        sentence,
                        token.Value,
                        token.Index,
                        cancellationToken);
                if (!lookup.Found ||
                    !string.Equals(lookup.Language, "EN", StringComparison.Ordinal))
                {
                    continue;
                }

                // When no lexical entry exists, ordinary lookup falls back to
                // the exact dialogue row. That row belongs in the dedicated
                // sentence field, not in the glossary as a sentence-sized MWE.
                if (exactSentenceTranslation is not null &&
                    string.Equals(lookup.Source, sentence, StringComparison.Ordinal) &&
                    string.Equals(
                        NormalizeExactSentenceTranslation(lookup.Translation),
                        exactSentenceTranslation,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                string matchedEnglishKey = lookup.MatchedEnglishKey.Length > 0
                    ? lookup.MatchedEnglishKey
                    : lookup.Source;
                bool preferredEntry = _dictionary.IsPreferredEnglishKey(matchedEnglishKey);
                bool matchedUserEntry = _dictionary.IsUserEnglishKey(matchedEnglishKey);
                bool preferredPhrase = preferredEntry && lookup.IsPhrase;
                string normalizedToken = EnglishContextLexicon.NormalizeToken(token.Value);
                if (!explicitUserToken &&
                    !matchedUserEntry &&
                    !preferredPhrase &&
                    (EnglishContextLexicon.DefinitionStopWords.Contains(normalizedToken) ||
                     ScaffoldStopWords.Contains(normalizedToken)))
                {
                    // Stopword-only user/VN phrases remain discoverable because
                    // lookup runs before this filter and exposes the matched key.
                    continue;
                }

                SelectionAttempt attempt = SelectTranslation(
                    sentence,
                    lookup,
                    token.Index,
                    preferredEntry);
                SelectedTranslation? selected = attempt.Selection;
                if (selected is null &&
                    neuralRankerAvailable &&
                    attempt.Plan is { Outcomes.Count: > 1, CandidateGlosses.Count: > 1 } plan)
                {
                    NeuralSelectionAttempt neural = await TrySelectWithNeuralRankerAsync(
                        sentence,
                        lookup.Source,
                        token.Index,
                        plan,
                        cancellationToken);
                    selected = neural.Selection;
                    neuralRankerAvailable = !neural.Failed;
                }

                if (selected is null || selected.Confidence < MinimumConfidence)
                {
                    continue;
                }

                string source = NormalizeSource(lookup.Source);
                string translation = NormalizeRussianTerm(selected.Translation);
                if (source.Length == 0 || translation.Length == 0)
                {
                    continue;
                }

                bool userEntry = matchedUserEntry ||
                                  explicitUserToken;
                bool properName = userEntry && LooksLikeProperName(source);
                TermPriority priority = lookup.IsPhrase ||
                                        EnglishContextLexicon.TokenRegex()
                                            .Matches(source)
                                            .Count > 1
                    ? TermPriority.MultiWordExpression
                    : properName
                        ? TermPriority.ProperName
                        : TermPriority.Word;
                candidates.Add(new TermCandidate(
                    new ScaffoldTerm(source, translation, selected.Confidence),
                    priority,
                    token.Index,
                    properName));
            }

            List<TermCandidate> eligibleCandidates = [];
            HashSet<string> seenSources = new(StringComparer.Ordinal);
            HashSet<string> conflictingSources = candidates
                .GroupBy(
                    candidate => LocalDictionaryService.NormalizeEnglish(candidate.Term.Source),
                    StringComparer.Ordinal)
                .Where(group => group
                    .Select(candidate => candidate.Term.Translation)
                    .Distinct(StringComparer.Ordinal)
                    .Skip(1)
                    .Any())
                .Select(group => group.Key)
                .ToHashSet(StringComparer.Ordinal);
            foreach (TermCandidate candidate in candidates
                         .OrderBy(candidate => candidate.Priority)
                         .ThenBy(candidate => candidate.CharacterIndex))
            {
                string key = LocalDictionaryService.NormalizeEnglish(
                    candidate.Term.Source);
                if (key.Length == 0 ||
                    conflictingSources.Contains(key) ||
                    !seenSources.Add(key))
                {
                    continue;
                }

                eligibleCandidates.Add(candidate);
            }

            ScaffoldTerm[] allTerms = eligibleCandidates
                .Select(candidate => candidate.Term)
                .ToArray();
            ScaffoldTerm[] terms = allTerms
                .Take(MaximumTermCount)
                .ToArray();
            string[] properNames = eligibleCandidates
                .Take(MaximumTermCount)
                .Where(candidate => candidate.IsProperName)
                .Select(candidate => candidate.Term.Source)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            // Dialogue data is the lowest-priority layer. It may short-circuit
            // generation only when it honors every authoritative constraint
            // discovered before the prompt cap. Otherwise a thirteenth
            // user/VN override could be silently bypassed by the dialogue layer.
            if (exactSentenceTranslation is not null &&
                SenseFidelityGuard.FindMissingTerms(
                    exactSentenceTranslation,
                    allTerms).Count > 0)
            {
                exactSentenceTranslation = null;
            }

            return terms.Length == 0 && exactSentenceTranslation is null
                ? TranslationScaffold.Empty
                : new TranslationScaffold(terms, exactSentenceTranslation, properNames);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A scaffold is an optional quality aid. Translation must remain
            // available if a dictionary file is missing or malformed.
            return TranslationScaffold.Empty;
        }
    }

    private static SelectionAttempt SelectTranslation(
        string sentence,
        DictionaryLookupResult lookup,
        int characterIndex,
        bool preferredEntry)
    {
        string[] outcomes;
        SmartDictionaryRankerPlan? plan = null;
        if (lookup.SenseCandidates.Count > 0)
        {
            SmartDictionarySenseInventory inventory =
                SmartDictionarySenseResolver.GroupCandidates(
                    sentence,
                    lookup.Source,
                    characterIndex,
                    lookup.SenseCandidates);
            plan =
                SmartDictionarySenseResolver.CreateRankerPlan(
                    sentence,
                    lookup.Source,
                    characterIndex,
                    inventory.Groups);
            outcomes = plan.Outcomes
                .OrderBy(outcome => outcome.GroupId)
                .Select(outcome => outcome.Translation)
                .ToArray();
        }
        else
        {
            outcomes = NpuDictionarySelectorService.SplitMeanings(lookup.Translation);
        }

        outcomes = outcomes
            .Select(NormalizeRussianTerm)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (outcomes.Length == 0)
        {
            return new SelectionAttempt(null, plan);
        }

        SmartDictionaryHeuristicSelection? aligned =
            SmartDictionaryHeuristicRanker.TrySelectFromContextTranslation(
                lookup.ContextTranslation,
                outcomes);
        if (aligned is not null)
        {
            return new SelectionAttempt(
                new SelectedTranslation(
                    aligned.Translation,
                    aligned.Confidence),
                plan);
        }

        SmartDictionaryHeuristicSelection? heuristic = outcomes.Length > 1
            ? SmartDictionaryHeuristicRanker.TrySelect(
                sentence,
                lookup.Source,
                characterIndex,
                outcomes,
                "English")
            : null;
        if (heuristic is { Confidence: >= MinimumHeuristicConfidence })
        {
            return new SelectionAttempt(
                new SelectedTranslation(
                    heuristic.Translation,
                    heuristic.Confidence),
                plan);
        }

        // A lone broad-dictionary translation is not automatically a lone
        // semantic sense (for example, archaic "shrine → рака"). Keep it as
        // a deterministic result below the mandatory-confidence floor unless
        // it came from the user/VN priority layers.
        return new SelectionAttempt(
            outcomes.Length == 1
                ? new SelectedTranslation(outcomes[0], preferredEntry ? 1 : 0.85)
                : null,
            plan);
    }

    private async Task<NeuralSelectionAttempt> TrySelectWithNeuralRankerAsync(
        string sentence,
        string target,
        int characterIndex,
        SmartDictionaryRankerPlan plan,
        CancellationToken cancellationToken)
    {
        try
        {
            SmartDictionaryRankerOutcome? selected = await _neuralRanker!(
                new TranslationScaffoldNeuralRequest(
                    sentence,
                    target,
                    characterIndex,
                    plan),
                cancellationToken);
            SmartDictionaryRankerOutcome? canonical = selected is null
                ? null
                : plan.Outcomes.FirstOrDefault(
                    outcome => outcome.GroupId == selected.GroupId);
            return new NeuralSelectionAttempt(
                canonical is null
                    ? null
                    : new SelectedTranslation(canonical.Translation, MinimumConfidence),
                Failed: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A failed optional ranker omits only this ambiguous term; already
            // selected deterministic constraints must survive. The caller also
            // disables more neural attempts for this sentence.
            HoverTranslationService.LogError(exception);
            return new NeuralSelectionAttempt(null, Failed: true);
        }
    }

    private static string NormalizeSource(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
        {
            return string.Empty;
        }

        string normalized = string.Join(
            " ",
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 128 &&
               EnglishContextLexicon.TokenRegex().IsMatch(normalized)
            ? normalized
            : string.Empty;
    }

    private static string NormalizeRussianTerm(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
        {
            return string.Empty;
        }

        string normalized = string.Join(
                " ",
                value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        normalized = RussianMorphology.RemoveStressMarks(normalized)
            .Trim();
        return normalized.Length <= 160 &&
               normalized.Any(IsCyrillic) &&
               !normalized.Any(IsAsciiLetter)
            ? normalized
            : string.Empty;
    }

    private static string? NormalizeExactSentenceTranslation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
        {
            return null;
        }

        string normalized = string.Join(
            " ",
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        int alternativeSeparator = FindDialogueAlternativeSeparator(normalized);
        string first = alternativeSeparator >= 0
            ? normalized[..alternativeSeparator].TrimEnd()
            : normalized;
        return string.IsNullOrWhiteSpace(first) ? null : first;
    }

    private static int FindDialogueAlternativeSeparator(string value)
    {
        int searchStart = 0;
        while (value.IndexOf("; ", searchStart, StringComparison.Ordinal) is int separator &&
               separator >= 0)
        {
            int previous = separator - 1;
            while (previous >= 0 && value[previous] is '"' or '\'' or '»' or ')' or ']')
            {
                previous--;
            }

            if (previous >= 0 && value[previous] is '.' or '!' or '?' or '…')
            {
                return separator;
            }

            searchStart = separator + 2;
        }

        return -1;
    }

    private static bool LooksLikeProperName(string source)
    {
        MatchCollection tokens = EnglishContextLexicon.TokenRegex().Matches(source);
        return tokens.Count > 0 && tokens
            .Cast<Match>()
            .All(token => char.IsUpper(token.Value[0]));
    }

    private static bool IsAsciiLetter(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsCyrillic(char character) =>
        character is >= '\u0400' and <= '\u052f';

    private enum TermPriority
    {
        MultiWordExpression,
        ProperName,
        Word
    }

    private sealed record SelectedTranslation(
        string Translation,
        double Confidence);

    private sealed record SelectionAttempt(
        SelectedTranslation? Selection,
        SmartDictionaryRankerPlan? Plan);

    private sealed record NeuralSelectionAttempt(
        SelectedTranslation? Selection,
        bool Failed);

    private sealed record TermCandidate(
        ScaffoldTerm Term,
        TermPriority Priority,
        int CharacterIndex,
        bool IsProperName);
}
