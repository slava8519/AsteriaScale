using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AsteriaScale.Services;

internal sealed record ScaffoldTerm(
    string Source,
    string Translation,
    double Confidence,
    ScaffoldTermProvenance Provenance = ScaffoldTermProvenance.Deterministic);

internal sealed record ScaffoldSenseHint(
    string Source,
    string EnglishDefinition,
    string SelectedTranslation,
    IReadOnlyList<string> RejectedTranslations,
    double Confidence)
{
    public ScaffoldSenseHint(
        string Source,
        string EnglishDefinition,
        double Confidence)
        : this(
            Source,
            EnglishDefinition,
            SelectedTranslation: string.Empty,
            Array.Empty<string>(),
            Confidence)
    {
    }
}

internal enum ScaffoldTermProvenance
{
    Deterministic,
    GlossBert
}

internal enum TranslationScaffoldNeuralStatus
{
    Selected,
    Abstained,
    Error,
    Skipped
}

internal sealed record TranslationScaffoldNeuralResult(
    int? GroupId,
    bool Abstained,
    double? Margin,
    double? Score,
    double? Confidence,
    GlossBertRankerProvider RequestedProvider,
    string ExecutionProvider,
    string Device,
    string ModelVersion,
    string Pipeline,
    long ElapsedMilliseconds,
    bool FromCache = false);

internal sealed record TranslationScaffoldNeuralDiagnostic(
    string Target,
    int CharacterIndex,
    TranslationScaffoldNeuralStatus Status,
    int CandidateGroupCount,
    int CandidateGlossCount,
    int? SelectedGroupId = null,
    string? SelectedTranslation = null,
    double? Margin = null,
    double? Score = null,
    double? Confidence = null,
    GlossBertRankerProvider? RequestedProvider = null,
    string? ExecutionProvider = null,
    string? Device = null,
    string? ModelVersion = null,
    string? Pipeline = null,
    long ElapsedMilliseconds = 0,
    string? ErrorCode = null,
    bool FromCache = false,
    string? Detail = null,
    ScaffoldTermProvenance? Provenance = null);

internal sealed record TranslationScaffold(
    IReadOnlyList<ScaffoldTerm> Terms,
    string? ExactSentenceTranslation,
    IReadOnlyList<string> ProperNames,
    IReadOnlyList<TranslationScaffoldNeuralDiagnostic> NeuralDecisions)
{
    public IReadOnlyList<ScaffoldSenseHint> SenseHints { get; init; } =
        Array.Empty<ScaffoldSenseHint>();

    public TranslationScaffold(
        IReadOnlyList<ScaffoldTerm> Terms,
        string? ExactSentenceTranslation,
        IReadOnlyList<string> ProperNames)
        : this(
            Terms,
            ExactSentenceTranslation,
            ProperNames,
            Array.Empty<TranslationScaffoldNeuralDiagnostic>())
    {
    }

    public void Deconstruct(
        out IReadOnlyList<ScaffoldTerm> terms,
        out string? exactSentenceTranslation,
        out IReadOnlyList<string> properNames)
    {
        terms = Terms;
        exactSentenceTranslation = ExactSentenceTranslation;
        properNames = ProperNames;
    }

    public static TranslationScaffold Empty { get; } = new(
        Array.Empty<ScaffoldTerm>(),
        ExactSentenceTranslation: null,
        Array.Empty<string>(),
        Array.Empty<TranslationScaffoldNeuralDiagnostic>());
}

internal sealed record TranslationScaffoldNeuralRequest(
    string Sentence,
    string Target,
    int CharacterIndex,
    SmartDictionaryRankerPlan Plan);

internal delegate Task<TranslationScaffoldNeuralResult> TranslationScaffoldNeuralRanker(
    TranslationScaffoldNeuralRequest request,
    CancellationToken cancellationToken);

/// <summary>
/// Shares the fail-open circuit breaker across every sentence in one caller
/// operation. A ranker transport/model failure must not be retried once per
/// screen block, while cancellation remains owned by the caller.
/// </summary>
internal sealed class TranslationScaffoldNeuralSession
{
    private int _neuralDisabled;

    public bool NeuralEnabled => Volatile.Read(ref _neuralDisabled) == 0;

    internal void DisableNeural() => Interlocked.Exchange(ref _neuralDisabled, 1);
}

/// <summary>
/// Builds high-confidence terminology constraints. Supplying a neural ranker
/// enables margin-calibrated GlossBERT selection automatically; a builder with
/// no ranker retains the deterministic benchmark/control behavior.
/// </summary>
internal sealed class TranslationScaffoldBuilder
{
    internal const double MinimumConfidence = 0.9;
    internal const int MaximumTermCount = 12;
    internal const int MaximumNeuralRequestCount = 12;
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
        BuildAsync(sentence, neuralSession: null, cancellationToken);

    internal async Task<TranslationScaffold> BuildAsync(
        string sentence,
        TranslationScaffoldNeuralSession? neuralSession,
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
            List<TranslationScaffoldNeuralDiagnostic> neuralDecisions = [];
            List<ScaffoldSenseHint> senseHints = [];
            HashSet<string> neuralSources = new(StringComparer.Ordinal);
            TranslationScaffoldNeuralSession effectiveNeuralSession =
                neuralSession ?? new TranslationScaffoldNeuralSession();
            int neuralRequestCount = 0;
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
                    _neuralRanker is not null &&
                    attempt.Plan is { Outcomes.Count: > 1, CandidateGlosses.Count: > 1 } plan)
                {
                    string neuralSourceKey = LocalDictionaryService.NormalizeEnglish(
                        lookup.Source);
                    if (neuralSourceKey.Length == 0)
                    {
                        neuralDecisions.Add(CreateSkippedNeuralDiagnostic(
                            lookup.Source,
                            token.Index,
                            plan,
                            "invalid-source"));
                    }
                    else if (!neuralSources.Add(neuralSourceKey))
                    {
                        neuralDecisions.Add(CreateSkippedNeuralDiagnostic(
                            lookup.Source,
                            token.Index,
                            plan,
                            "duplicate-source"));
                    }
                    else if (!effectiveNeuralSession.NeuralEnabled)
                    {
                        neuralDecisions.Add(CreateSkippedNeuralDiagnostic(
                            lookup.Source,
                            token.Index,
                            plan,
                            "session-disabled-after-error"));
                    }
                    else if (neuralRequestCount >= MaximumNeuralRequestCount)
                    {
                        neuralDecisions.Add(CreateSkippedNeuralDiagnostic(
                            lookup.Source,
                            token.Index,
                            plan,
                            "sentence-request-limit"));
                    }
                    else
                    {
                        neuralRequestCount++;
                        NeuralSelectionAttempt neural = await TrySelectWithNeuralRankerAsync(
                            sentence,
                            lookup.Source,
                            token.Index,
                            plan,
                            effectiveNeuralSession,
                            cancellationToken);
                        senseHints.AddRange(neural.SenseHints);
                        neuralDecisions.Add(neural.Diagnostic);
                    }
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
                    new ScaffoldTerm(
                        source,
                        translation,
                        selected.Confidence,
                        selected.Provenance),
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

            IReadOnlyList<TranslationScaffoldNeuralDiagnostic> immutableNeuralDecisions =
                Array.AsReadOnly(neuralDecisions.ToArray());
            IReadOnlyList<ScaffoldSenseHint> immutableSenseHints =
                Array.AsReadOnly(senseHints.ToArray());
            return terms.Length == 0 &&
                   exactSentenceTranslation is null &&
                   immutableNeuralDecisions.Count == 0 &&
                   immutableSenseHints.Count == 0
                ? TranslationScaffold.Empty
                : new TranslationScaffold(
                    terms,
                    exactSentenceTranslation,
                    properNames,
                    immutableNeuralDecisions)
                {
                    SenseHints = immutableSenseHints
                };
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
                    aligned.Confidence,
                    ScaffoldTermProvenance.Deterministic),
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
                    heuristic.Confidence,
                    ScaffoldTermProvenance.Deterministic),
                plan);
        }

        // A lone broad-dictionary translation is not automatically a lone
        // semantic sense (for example, archaic "shrine → рака"). Keep it as
        // a deterministic result below the mandatory-confidence floor unless
        // it came from the user/VN priority layers.
        return new SelectionAttempt(
            outcomes.Length == 1
                ? new SelectedTranslation(
                    outcomes[0],
                    preferredEntry ? 1 : 0.85,
                    ScaffoldTermProvenance.Deterministic)
                : null,
            plan);
    }

    private async Task<NeuralSelectionAttempt> TrySelectWithNeuralRankerAsync(
        string sentence,
        string target,
        int characterIndex,
        SmartDictionaryRankerPlan plan,
        TranslationScaffoldNeuralSession neuralSession,
        CancellationToken cancellationToken)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        try
        {
            TranslationScaffoldNeuralResult result = await _neuralRanker!(
                new TranslationScaffoldNeuralRequest(
                    sentence,
                    target,
                    characterIndex,
                    plan),
                cancellationToken);

            if (result.Abstained)
            {
                if (result.GroupId is not null)
                {
                    throw new InvalidOperationException(
                        "An abstained GlossBERT result cannot contain a GroupId.");
                }

                return new NeuralSelectionAttempt(
                    SenseHints: Array.Empty<ScaffoldSenseHint>(),
                    Diagnostic: CreateNeuralDiagnostic(
                        target,
                        characterIndex,
                        TranslationScaffoldNeuralStatus.Abstained,
                        plan,
                        result,
                        selectedTranslation: null,
                        provenance: null));
            }

            if (result.GroupId is not { } selectedGroupId)
            {
                throw new InvalidOperationException(
                    "A selected GlossBERT result must contain a GroupId.");
            }

            SmartDictionaryRankerOutcome? canonical = plan.Outcomes.FirstOrDefault(
                outcome => outcome.GroupId == selectedGroupId);
            if (canonical is null)
            {
                throw new InvalidOperationException(
                    $"GlossBERT selected unknown GroupId {selectedGroupId}.");
            }

            IReadOnlyList<ScaffoldSenseHint> selectedSenseHints = CreateSenseHints(
                target,
                plan,
                selectedGroupId);
            return new NeuralSelectionAttempt(
                selectedSenseHints,
                CreateNeuralDiagnostic(
                    target,
                    characterIndex,
                    TranslationScaffoldNeuralStatus.Selected,
                    plan,
                    result,
                    canonical.Translation,
                    ScaffoldTermProvenance.GlossBert));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A failed optional ranker omits only this ambiguous term; already
            // selected deterministic constraints must survive. A shared session
            // also prevents a systemic failure from repeating per screen block.
            neuralSession.DisableNeural();
            HoverTranslationService.LogError(exception);
            return new NeuralSelectionAttempt(
                SenseHints: Array.Empty<ScaffoldSenseHint>(),
                Diagnostic: new TranslationScaffoldNeuralDiagnostic(
                    target,
                    characterIndex,
                    TranslationScaffoldNeuralStatus.Error,
                    plan.Outcomes.Count,
                    plan.CandidateGlosses.Count,
                    ElapsedMilliseconds: elapsed.ElapsedMilliseconds,
                    ErrorCode: exception is GlossBertRankerException rankerException
                        ? rankerException.ErrorCode
                        : exception.GetType().Name,
                    Detail: exception.GetType().Name + ": " + exception.Message));
        }
    }

    private static TranslationScaffoldNeuralDiagnostic CreateNeuralDiagnostic(
        string source,
        int characterIndex,
        TranslationScaffoldNeuralStatus status,
        SmartDictionaryRankerPlan plan,
        TranslationScaffoldNeuralResult result,
        string? selectedTranslation,
        ScaffoldTermProvenance? provenance) =>
        new(
            source,
            characterIndex,
            status,
            plan.Outcomes.Count,
            plan.CandidateGlosses.Count,
            result.GroupId,
            selectedTranslation,
            result.Margin,
            result.Score,
            result.Confidence,
            result.RequestedProvider,
            result.ExecutionProvider,
            result.Device,
            result.ModelVersion,
            result.Pipeline,
            result.ElapsedMilliseconds,
            ErrorCode: null,
            FromCache: result.FromCache,
            Provenance: provenance);

    private static IReadOnlyList<ScaffoldSenseHint> CreateSenseHints(
        string source,
        SmartDictionaryRankerPlan plan,
        int selectedGroupId)
    {
        string normalizedSource = NormalizeSource(source);
        SmartDictionaryRankerOutcome? selectedOutcome = plan.Outcomes.FirstOrDefault(
            outcome => outcome.GroupId == selectedGroupId);
        if (selectedOutcome is null)
        {
            throw new InvalidOperationException(
                $"GlossBERT selected unknown GroupId {selectedGroupId}.");
        }

        string selectedTranslation = NormalizeRussianTerm(selectedOutcome.Translation);
        IReadOnlyList<string> rejectedTranslations = Array.AsReadOnly(plan.Outcomes
            .Where(outcome => outcome.GroupId != selectedGroupId)
            .Select(outcome => NormalizeRussianTerm(outcome.Translation))
            .Where(translation => translation.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray());
        ScaffoldSenseHint[] hints = Enumerable.Range(0, plan.CandidateGlosses.Count)
            .Where(index => plan.CandidateGroupIds[index] == selectedGroupId)
            .Select(index => NormalizeEnglishDefinition(plan.CandidateGlosses[index]))
            .Where(definition => definition.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .Select(definition => new ScaffoldSenseHint(
                normalizedSource,
                definition,
                selectedTranslation,
                rejectedTranslations,
                MinimumConfidence))
            .ToArray();
        if (normalizedSource.Length == 0 ||
            selectedTranslation.Length == 0 ||
            hints.Length == 0)
        {
            throw new InvalidOperationException(
                $"GlossBERT selected GroupId {selectedGroupId} without a usable English sense hint.");
        }

        return Array.AsReadOnly(hints);
    }

    private static string NormalizeEnglishDefinition(string value)
    {
        string withoutControls = new(value
            .Select(character => char.IsControl(character) ? ' ' : character)
            .ToArray());
        string normalized = string.Join(
            " ",
            withoutControls.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length <= 320)
        {
            return normalized;
        }

        int length = 320;
        if (char.IsHighSurrogate(normalized[length - 1]))
        {
            length--;
        }

        return normalized[..length].TrimEnd();
    }

    private static TranslationScaffoldNeuralDiagnostic CreateSkippedNeuralDiagnostic(
        string source,
        int characterIndex,
        SmartDictionaryRankerPlan plan,
        string detail) =>
        new(
            source,
            characterIndex,
            TranslationScaffoldNeuralStatus.Skipped,
            plan.Outcomes.Count,
            plan.CandidateGlosses.Count,
            ErrorCode: detail,
            Detail: detail);

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
        double Confidence,
        ScaffoldTermProvenance Provenance);

    private sealed record SelectionAttempt(
        SelectedTranslation? Selection,
        SmartDictionaryRankerPlan? Plan);

    private sealed record NeuralSelectionAttempt(
        IReadOnlyList<ScaffoldSenseHint> SenseHints,
        TranslationScaffoldNeuralDiagnostic Diagnostic);

    private sealed record TermCandidate(
        ScaffoldTerm Term,
        TermPriority Priority,
        int CharacterIndex,
        bool IsProperName);
}
