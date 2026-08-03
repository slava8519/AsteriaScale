using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace AsteriaScale.Services;

internal enum SensePreservationCandidateKind
{
    Direct,
    Conditioned,
    Repair
}

internal sealed record SensePreservationCandidate(
    SensePreservationCandidateKind Kind,
    string Translation,
    bool StrictAccepted,
    bool DisplayAccepted,
    SmartPhraseRejectionReason StrictRejection,
    SmartPhraseRejectionReason DisplayRejection,
    int MissingMandatoryTermCount,
    int MissingAdvisoryTermCount,
    int StructuralPenaltyCount,
    IReadOnlyList<string> StructuralViolationCodes,
    NeuralSenseFidelityAssessment SenseAssessment,
    IReadOnlyList<ScaffoldSenseHint> AppliedSenseHints);

internal sealed record SensePreservationRunResult(
    SensePreservationCandidate Selected,
    int AdditionalGenerationCount,
    bool Cacheable);

/// <summary>
/// Deterministic downstream selector for TranslateGemma hypotheses. It never
/// changes the ranker's acceptance decision: it only checks whether a generated
/// Russian sentence preserved the provider-neutral evidence already accepted
/// by the ranker.
/// </summary>
internal static class SensePreservationGate
{
    internal const string PolarityMismatchCode = "polarity_mismatch";
    internal const string NumberNotPreservedCode = "number_not_preserved";

    private static readonly Regex EnglishNumericLiteralRegex = new(
        @"(?<![\p{L}\p{N}])[-+−]?(?:(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d+)?|\.\d+)(?:[eE][-+]?\d+)?(?!\p{N})",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex RussianNumericLiteralRegex = new(
        @"(?<![\p{L}\p{N}])[-+−]?(?:(?:\d{1,3}(?:[ \u00A0\u202F\u2009]\d{3})+|\d+)(?:[,.]\d+)?|[,.]\d+)(?:[eE][-+]?\d+)?(?!\p{N})",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex IsoDateRegex = new(
        @"(?<!\d)(?<year>\d{4})[-/.](?<month>\d{1,2})[-/.](?<day>\d{1,2})(?!\d)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex RussianDateRegex = new(
        @"(?<!\d)(?:(?<year>\d{4})[-/.](?<month>\d{1,2})[-/.](?<day>\d{1,2})|(?<day>\d{1,2})\.(?<month>\d{1,2})\.(?<year>\d{4}))(?!\d)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly IReadOnlyDictionary<string, (string Digits, string Russian)>
        CardinalNumbers = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["zero"] = ("0", "ноль"),
            // "one" is frequently a pronoun, while Russian один/одна is also
            // predicative ("alone"). Digit 1 remains strict; ambiguous word
            // surfaces stay outside the structural gate.
            ["two"] = ("2", "два"),
            ["three"] = ("3", "три"),
            ["four"] = ("4", "четыре"),
            ["five"] = ("5", "пять"),
            ["six"] = ("6", "шесть"),
            ["seven"] = ("7", "семь"),
            ["eight"] = ("8", "восемь"),
            ["nine"] = ("9", "девять"),
            ["ten"] = ("10", "десять"),
            ["eleven"] = ("11", "одиннадцать"),
            ["twelve"] = ("12", "двенадцать"),
            ["thirteen"] = ("13", "тринадцать"),
            ["fourteen"] = ("14", "четырнадцать"),
            ["fifteen"] = ("15", "пятнадцать"),
            ["sixteen"] = ("16", "шестнадцать"),
            ["seventeen"] = ("17", "семнадцать"),
            ["eighteen"] = ("18", "восемнадцать"),
            ["nineteen"] = ("19", "девятнадцать"),
            ["twenty"] = ("20", "двадцать")
        };

    private static readonly IReadOnlyDictionary<string, string> RussianCardinalNumbers =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ноль"] = "0", ["нуля"] = "0", ["нулю"] = "0",
            ["нолем"] = "0", ["нуле"] = "0",
            ["два"] = "2", ["две"] = "2", ["двух"] = "2", ["двум"] = "2",
            ["двумя"] = "2",
            ["три"] = "3", ["трех"] = "3", ["трем"] = "3", ["тремя"] = "3",
            ["четыре"] = "4", ["четырех"] = "4", ["четырем"] = "4",
            ["четырьмя"] = "4",
            ["пять"] = "5", ["пяти"] = "5", ["пятью"] = "5",
            ["шесть"] = "6", ["шести"] = "6", ["шестью"] = "6",
            ["семь"] = "7", ["семи"] = "7", ["семью"] = "7",
            ["восемь"] = "8", ["восьми"] = "8", ["восемью"] = "8",
            ["девять"] = "9", ["девяти"] = "9", ["девятью"] = "9",
            ["десять"] = "10", ["десяти"] = "10", ["десятью"] = "10",
            ["одиннадцать"] = "11", ["одиннадцати"] = "11",
            ["одиннадцатью"] = "11",
            ["двенадцать"] = "12", ["двенадцати"] = "12", ["двенадцатью"] = "12",
            ["тринадцать"] = "13", ["тринадцати"] = "13", ["тринадцатью"] = "13",
            ["четырнадцать"] = "14", ["четырнадцати"] = "14",
            ["четырнадцатью"] = "14",
            ["пятнадцать"] = "15", ["пятнадцати"] = "15", ["пятнадцатью"] = "15",
            ["шестнадцать"] = "16", ["шестнадцати"] = "16",
            ["шестнадцатью"] = "16",
            ["семнадцать"] = "17", ["семнадцати"] = "17", ["семнадцатью"] = "17",
            ["восемнадцать"] = "18", ["восемнадцати"] = "18",
            ["восемнадцатью"] = "18",
            ["девятнадцать"] = "19", ["девятнадцати"] = "19",
            ["девятнадцатью"] = "19",
            ["двадцать"] = "20", ["двадцати"] = "20", ["двадцатью"] = "20"
        };

    public static SensePreservationCandidate Evaluate(
        SmartPhraseTranslationRequest request,
        string translation,
        string modelIdentity,
        SensePreservationCandidateKind kind,
        IReadOnlyList<ScaffoldSenseHint>? appliedSenseHints = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(translation);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelIdentity);

        SmartPhraseTranslationResult result = new(
            translation,
            modelIdentity,
            ElapsedMilliseconds: 0);
        SmartPhraseGuardResult strict = SmartPhraseTranslationGuard.Validate(request, result);
        SmartPhraseGuardResult display =
            SmartPhraseTranslationGuard.ValidateForDisplay(request, result);
        string normalized = strict.Accepted
            ? strict.Translation
            : display.Accepted
                ? display.Translation
                : SmartPhraseTranslationGuard.NormalizeOutput(translation);
        int missingMandatory = SenseFidelityGuard.FindMissingTerms(
            normalized,
            ScaffoldTermPolicy.MandatoryTerms(request.SemanticTerms)).Count;
        int missingAdvisory = SenseFidelityGuard.FindMissingTerms(
            normalized,
            ScaffoldTermPolicy.AdvisoryTerms(request.SemanticTerms)).Count;
        IReadOnlyList<string> structuralViolations =
            FindHardSemanticStructureViolations(request, normalized);
        return new SensePreservationCandidate(
            kind,
            normalized,
            strict.Accepted,
            display.Accepted,
            strict.Reason,
            display.Reason,
            missingMandatory,
            missingAdvisory,
            structuralViolations.Count,
            structuralViolations,
            NeuralSenseFidelityGuard.Assess(
                normalized,
                request.SenseHints,
                request.SourcePhrase),
            appliedSenseHints ?? Array.Empty<ScaffoldSenseHint>());
    }

    public static SensePreservationCandidate SelectBest(
        SmartPhraseTranslationRequest request,
        IReadOnlyList<SensePreservationCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
        {
            throw new ArgumentException("At least one candidate is required.", nameof(candidates));
        }

        SensePreservationCandidate best = candidates[0];
        for (int index = 1; index < candidates.Count; index++)
        {
            SensePreservationCandidate candidate = candidates[index];
            if (Compare(request, candidate, best) > 0)
            {
                best = candidate;
            }
        }

        return best;
    }

    public static bool RequiresTargetedRepair(SensePreservationCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return candidate.DisplayAccepted &&
               (candidate.SenseAssessment.RepairHints.Count > 0 ||
                candidate.StructuralPenaltyCount > 0);
    }

    public static bool HasBlockingSemanticFailure(
        SmartPhraseTranslationRequest request,
        string translation) =>
        FindHardSemanticStructureViolations(request, translation).Count > 0 ||
        NeuralSenseFidelityGuard.Assess(
            translation,
            request.SenseHints,
            request.SourcePhrase).HasBlockingState;

    private static int Compare(
        SmartPhraseTranslationRequest request,
        SensePreservationCandidate left,
        SensePreservationCandidate right)
    {
        int comparison = PreferTrue(left.DisplayAccepted, right.DisplayAccepted);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = PreferLower(
            left.MissingMandatoryTermCount,
            right.MissingMandatoryTermCount);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = PreferLower(
            left.StructuralPenaltyCount,
            right.StructuralPenaltyCount);
        if (comparison != 0)
        {
            return comparison;
        }

        // The word under the cursor is the user's immediate lookup intent.
        // Secondary sentence senses may refine the translation, but no number
        // of them may outvote an explicit failure for the primary source.
        comparison = CompareSenseStates(
            left.SenseAssessment,
            right.SenseAssessment,
            primaryOnly: true);
        if (comparison != 0)
        {
            return comparison;
        }

        // Explicit wrong-sense surfaces are stronger evidence than the absence
        // of a dictionary lemma. A natural paraphrase therefore beats a known
        // conflict, while Satisfied beats merely Unresolved.
        comparison = CompareSenseStates(
            left.SenseAssessment,
            right.SenseAssessment,
            primaryOnly: false);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = PreferLower(
            left.MissingAdvisoryTermCount,
            right.MissingAdvisoryTermCount);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = PreferTrue(left.StrictAccepted, right.StrictAccepted);
        if (comparison != 0)
        {
            return comparison;
        }

        bool knownSpeakerGender = request.CurrentSpeaker?.Gender is
            SpeakerGender.Masculine or SpeakerGender.Feminine;
        return CandidateTieBreak(left.Kind, knownSpeakerGender).CompareTo(
            CandidateTieBreak(right.Kind, knownSpeakerGender));
    }

    private static int PreferTrue(bool left, bool right) =>
        left.CompareTo(right);

    private static int PreferLower(int left, int right) =>
        right.CompareTo(left);

    private static int CompareSenseStates(
        NeuralSenseFidelityAssessment left,
        NeuralSenseFidelityAssessment right,
        bool primaryOnly)
    {
        int Count(
            NeuralSenseFidelityAssessment assessment,
            NeuralSenseFidelityState state) =>
            assessment.Sources.Count(source =>
                (!primaryOnly || source.Representative.IsPrimary) &&
                source.State == state);

        int comparison = PreferLower(
            Count(left, NeuralSenseFidelityState.Conflict),
            Count(right, NeuralSenseFidelityState.Conflict));
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = PreferLower(
            Count(left, NeuralSenseFidelityState.Mixed),
            Count(right, NeuralSenseFidelityState.Mixed));
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = PreferLower(
            Count(left, NeuralSenseFidelityState.Unresolved),
            Count(right, NeuralSenseFidelityState.Unresolved));
        if (comparison != 0)
        {
            return comparison;
        }

        return Count(left, NeuralSenseFidelityState.Satisfied).CompareTo(
            Count(right, NeuralSenseFidelityState.Satisfied));
    }

    private static int CandidateTieBreak(
        SensePreservationCandidateKind kind,
        bool knownSpeakerGender) =>
        knownSpeakerGender
            ? kind switch
            {
                SensePreservationCandidateKind.Conditioned => 3,
                SensePreservationCandidateKind.Repair => 2,
                _ => 1
            }
            : kind switch
            {
                SensePreservationCandidateKind.Direct => 3,
                SensePreservationCandidateKind.Conditioned => 2,
                _ => 1
            };

    internal static IReadOnlyList<string> FindHardSemanticStructureViolations(
        SmartPhraseTranslationRequest request,
        string translation)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<string> violations = [];
        string source = request.SourcePhrase;
        string[] sourceTokens = EnglishContextLexicon.TokenRegex()
            .Matches(source ?? string.Empty)
            .Select(match => EnglishContextLexicon.NormalizeToken(match.Value))
            .Where(token => token.Length > 0)
            .ToArray();
        string[] russianTokens = RussianMorphology.TokenRegex()
            .Matches(RussianMorphology.RemoveStressMarks(translation ?? string.Empty))
            .Select(match => RussianMorphology.NormalizeForStemComparison(match.Value))
            .Where(token => token.Length > 0)
            .ToArray();
        int[] sourceClauseIndexes = BuildClauseIndexes(
            source ?? string.Empty,
            english: true);
        int[] russianClauseIndexes = BuildClauseIndexes(
            translation ?? string.Empty,
            english: false);

        HashSet<int> consumedSourcePolarity = [];
        HashSet<int> consumedTranslationPolarity = [];
        ConsumePairedPolarityEvidence(
            request,
            sourceTokens,
            russianTokens,
            consumedSourcePolarity,
            consumedTranslationPolarity);
        ConsumeLinkedLexicalNegations(
            sourceTokens,
            russianTokens,
            consumedTranslationPolarity);
        ConsumeCollapsedRussianNegations(
            sourceTokens,
            russianTokens,
            sourceClauseIndexes,
            russianClauseIndexes,
            consumedSourcePolarity,
            consumedTranslationPolarity);
        int sourceNegationCount = CountEnglishNegationUnits(
            sourceTokens,
            consumedSourcePolarity);
        int translationNegationCount = CountRussianNegationUnits(
            russianTokens,
            consumedTranslationPolarity);
        if (sourceNegationCount != translationNegationCount)
        {
            violations.Add(PolarityMismatchCode);
        }

        List<string> sourceNumbers = [.. ExtractNumericFacts(source, english: true)];
        sourceNumbers.AddRange(sourceTokens
            .Select((token, index) => (Token: token, Index: index))
            .Where(item => IsEnglishCardinalOccurrence(sourceTokens, item.Index))
            .Select(item => CanonicalizeEnglishNumber(
                CardinalNumbers[item.Token].Digits)));
        List<string> translationNumbers =
            [.. ExtractNumericFacts(translation, english: false)];
        translationNumbers.AddRange(russianTokens
            .Where(RussianCardinalNumbers.ContainsKey)
            .Select(token => CanonicalizeRussianNumber(RussianCardinalNumbers[token])));
        CompareNumericFacts(
            sourceNumbers,
            translationNumbers,
            violations);

        return Array.AsReadOnly(violations.ToArray());
    }

    private static bool IsEnglishCardinalOccurrence(
        IReadOnlyList<string> tokens,
        int index)
    {
        if (index < 0 || index >= tokens.Count ||
            !CardinalNumbers.ContainsKey(tokens[index]))
        {
            return false;
        }

        return true;
    }

    private static string CanonicalizeEnglishNumber(string value) =>
        CanonicalizeNumericLiteral(value, english: true);

    private static string CanonicalizeRussianNumber(string value) =>
        CanonicalizeNumericLiteral(value, english: false);

    private static string CanonicalizeNumericLiteral(string value, bool english)
    {
        string normalized = string.Concat(value.Where(character =>
            character is not (' ' or '\u00A0' or '\u202F' or '\u2009')));
        normalized = normalized.Replace("−", "-", StringComparison.Ordinal);
        if (english)
        {
            normalized = normalized.Replace(",", string.Empty, StringComparison.Ordinal);
        }
        else
        {
            normalized = normalized.Replace(',', '.');
        }

        string sign = string.Empty;
        if (normalized.Length > 0 && normalized[0] is '-' or '+')
        {
            sign = normalized[0] == '-' ? "-" : string.Empty;
            normalized = normalized[1..];
        }

        int exponentIndex = normalized.IndexOf('e');
        if (exponentIndex < 0)
        {
            exponentIndex = normalized.IndexOf('E');
        }

        BigInteger explicitExponent = BigInteger.Zero;
        string mantissa = exponentIndex >= 0
            ? normalized[..exponentIndex]
            : normalized;
        if (exponentIndex >= 0 &&
            !BigInteger.TryParse(
                normalized[(exponentIndex + 1)..],
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out explicitExponent))
        {
            return sign + normalized;
        }

        int decimalIndex = mantissa.IndexOf('.');
        int integerDigitCount = decimalIndex >= 0 ? decimalIndex : mantissa.Length;
        string digits = mantissa.Replace(".", string.Empty, StringComparison.Ordinal);
        int firstNonZero = -1;
        for (int index = 0; index < digits.Length; index++)
        {
            if (digits[index] != '0')
            {
                firstNonZero = index;
                break;
            }
        }

        if (firstNonZero < 0)
        {
            return "0";
        }

        string significantDigits = digits[firstNonZero..].TrimEnd('0');
        BigInteger exponentOfFirstDigit = explicitExponent +
            integerDigitCount - firstNonZero - 1;
        return $"{sign}{significantDigits}@{exponentOfFirstDigit.ToString(CultureInfo.InvariantCulture)}";
    }

    private static IReadOnlyList<string> ExtractNumericFacts(string? text, bool english)
    {
        text ??= string.Empty;
        List<string> facts = [];
        List<(int Start, int End)> dateSpans = [];
        Regex dateRegex = english ? IsoDateRegex : RussianDateRegex;
        foreach (Match match in dateRegex.Matches(text))
        {
            if (!TryCanonicalizeDate(match, out string date))
            {
                continue;
            }

            facts.Add(date);
            dateSpans.Add((match.Index, match.Index + match.Length));
        }

        Regex numberRegex = english
            ? EnglishNumericLiteralRegex
            : RussianNumericLiteralRegex;
        foreach (Match match in numberRegex.Matches(text))
        {
            int end = match.Index + match.Length;
            if (dateSpans.Any(span => match.Index < span.End && end > span.Start))
            {
                continue;
            }

            facts.Add(english
                ? CanonicalizeEnglishNumber(match.Value)
                : CanonicalizeRussianNumber(match.Value));
        }

        return facts.AsReadOnly();
    }

    private static bool TryCanonicalizeDate(Match match, out string date)
    {
        date = string.Empty;
        if (!int.TryParse(match.Groups["year"].Value, out int year) ||
            !int.TryParse(match.Groups["month"].Value, out int month) ||
            !int.TryParse(match.Groups["day"].Value, out int day) ||
            year is < 1 or > 9999 ||
            month is < 1 or > 12 ||
            day < 1 ||
            day > DateTime.DaysInMonth(year, month))
        {
            return false;
        }

        date = $"date:{year:D4}-{month:D2}-{day:D2}";
        return true;
    }

    private static void CompareNumericFacts(
        IReadOnlyList<string> sourceFacts,
        IReadOnlyList<string> translationFacts,
        ICollection<string> violations)
    {
        Dictionary<string, int> remainingTranslation = translationFacts
            .GroupBy(value => value, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Count(),
                StringComparer.Ordinal);
        foreach (string sourceFact in sourceFacts)
        {
            if (TryConsumeFact(remainingTranslation, sourceFact))
            {
                continue;
            }

            violations.Add(NumberNotPreservedCode);
        }

        foreach ((string fact, int count) in remainingTranslation)
        {
            for (int index = 0; index < count; index++)
            {
                violations.Add(NumberNotPreservedCode);
            }
        }
    }

    private static bool TryConsumeFact(IDictionary<string, int> facts, string value)
    {
        if (!facts.TryGetValue(value, out int count) || count <= 0)
        {
            return false;
        }

        facts[value] = count - 1;
        return true;
    }

    private static int[] BuildClauseIndexes(string text, bool english)
    {
        string normalizedText = english
            ? text ?? string.Empty
            : RussianMorphology.RemoveStressMarks(text ?? string.Empty);
        Regex tokenRegex = english
            ? EnglishContextLexicon.TokenRegex()
            : RussianMorphology.TokenRegex();
        List<int> clauseIndexes = [];
        int currentClause = 0;
        int previousEnd = 0;
        foreach (Match match in tokenRegex.Matches(normalizedText))
        {
            bool hasPunctuationBoundary = match.Index > previousEnd &&
                HasClauseBoundary(normalizedText.AsSpan(
                    previousEnd,
                    match.Index - previousEnd));
            if (hasPunctuationBoundary)
            {
                currentClause++;
            }

            string token = english
                ? EnglishContextLexicon.NormalizeToken(match.Value)
                : RussianMorphology.NormalizeForStemComparison(match.Value);
            if (token.Length > 0)
            {
                if (clauseIndexes.Count > 0 &&
                    !hasPunctuationBoundary &&
                    IsContrastBoundary(token, english))
                {
                    currentClause++;
                }

                clauseIndexes.Add(currentClause);
            }

            previousEnd = match.Index + match.Length;
        }

        return clauseIndexes.ToArray();
    }

    private static bool HasClauseBoundary(ReadOnlySpan<char> separator)
    {
        foreach (char character in separator)
        {
            if (character is '.' or ';' or ':' or '!' or '?' or
                '\r' or '\n' or '…' or '—' or '–')
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsContrastBoundary(string token, bool english) =>
        english
            ? token is "but" or "however" or "whereas" or "yet"
            : token is "но" or "однако" or "зато";

    private static void ConsumePairedPolarityEvidence(
        SmartPhraseTranslationRequest request,
        IReadOnlyList<string> sourceTokens,
        IReadOnlyList<string> translationTokens,
        ISet<int> consumedSource,
        ISet<int> consumedTranslation)
    {
        HashSet<PolaritySpan> usedSourceSpans = [];
        HashSet<PolaritySpan> usedTranslationSpans = [];
        IEnumerable<PolarityEvidence> evidence = request.SemanticTerms
            .Select(term => new PolarityEvidence(term.Source, term.Translation))
            .Concat(NeuralSenseFidelityGuard
                .GetApplicableHints(request.SourcePhrase, request.SenseHints)
                .Select(hint =>
                    new PolarityEvidence(hint.Source, hint.SelectedTranslation)))
            .Where(item =>
                !string.IsNullOrWhiteSpace(item.Source) &&
                !string.IsNullOrWhiteSpace(item.Translation))
            .Distinct();
        foreach (PolarityEvidence item in evidence)
        {
            string[] sourceSurface = TokenizeEnglish(item.Source);
            string[] translationSurface = TokenizeRussian(item.Translation);
            if (!sourceSurface.Any(IsEnglishPolarityMarker) &&
                !translationSurface.Any(IsRussianPolarityMarker))
            {
                continue;
            }

            PolaritySpan[] sourceSpans = FindTokenSpans(
                    sourceSurface,
                    sourceTokens,
                    static (expected, actual) => string.Equals(
                        expected,
                        actual,
                        StringComparison.Ordinal))
                .Where(span => !usedSourceSpans.Any(used => used.Overlaps(span)))
                .ToArray();
            PolaritySpan[] translationSpans = FindTokenSpans(
                    translationSurface,
                    translationTokens,
                    static (expected, actual) =>
                        RussianMorphology.AreConservativeInflectionVariants(
                            expected,
                            actual))
                .Where(span => !usedTranslationSpans.Any(used => used.Overlaps(span)))
                .ToArray();
            int pairCount = Math.Min(sourceSpans.Length, translationSpans.Length);
            for (int pairIndex = 0; pairIndex < pairCount; pairIndex++)
            {
                PolaritySpan sourceSpan = sourceSpans[pairIndex];
                PolaritySpan translationSpan = translationSpans[pairIndex];
                usedSourceSpans.Add(sourceSpan);
                usedTranslationSpans.Add(translationSpan);
                ConsumePolarityMarkers(
                    sourceTokens,
                    sourceSpan,
                    IsEnglishPolarityMarker,
                    consumedSource);
                ConsumePolarityMarkers(
                    translationTokens,
                    translationSpan,
                    IsRussianPolarityMarker,
                    consumedTranslation);
            }
        }
    }

    private static void ConsumePolarityMarkers(
        IReadOnlyList<string> tokens,
        PolaritySpan span,
        Func<string, bool> isMarker,
        ISet<int> consumed)
    {
        for (int index = span.Start; index < span.End; index++)
        {
            if (isMarker(tokens[index]))
            {
                consumed.Add(index);
            }
        }
    }

    private static void ConsumeLinkedLexicalNegations(
        IReadOnlyList<string> sourceTokens,
        IReadOnlyList<string> russianTokens,
        ISet<int> consumedTranslation)
    {
        ConsumeLinkedLexicalNegations(
            sourceTokens,
            russianTokens,
            consumedTranslation,
            static token => token is "forget" or "forgets" or "forgot" or "forgotten",
            ["помн", "вспомн"],
            allowStandaloneNo: false);
        ConsumeLinkedLexicalNegations(
            sourceTokens,
            russianTokens,
            consumedTranslation,
            static token => token is "fail" or "fails" or "failed" or "failing",
            ["мог", "смог", "сум", "удал", "получ", "справ"],
            allowStandaloneNo: false);
        ConsumeLinkedLexicalNegations(
            sourceTokens,
            russianTokens,
            consumedTranslation,
            static token => token is "lack" or "lacks" or "lacked" or "lacking",
            ["хват", "име"],
            allowStandaloneNo: true);
        ConsumeLinkedLexicalNegations(
            sourceTokens,
            russianTokens,
            consumedTranslation,
            static token => token is "unable",
            ["мог", "смог", "способ"],
            allowStandaloneNo: false);
    }

    private static void ConsumeLinkedLexicalNegations(
        IReadOnlyList<string> sourceTokens,
        IReadOnlyList<string> russianTokens,
        ISet<int> consumedTranslation,
        Func<string, bool> matchesSource,
        IReadOnlyList<string> licensedStems,
        bool allowStandaloneNo)
    {
        int remainingLicenses = sourceTokens.Count(matchesSource);
        if (remainingLicenses == 0)
        {
            return;
        }

        for (int index = 0; index < russianTokens.Count; index++)
        {
            if (remainingLicenses == 0)
            {
                break;
            }

            if (consumedTranslation.Contains(index))
            {
                continue;
            }

            if (allowStandaloneNo && russianTokens[index] == "нет")
            {
                consumedTranslation.Add(index);
                remainingLicenses--;
                continue;
            }

            if (russianTokens[index] != "не")
            {
                continue;
            }

            int end = Math.Min(russianTokens.Count, index + 4);
            if (russianTokens
                .Skip(index + 1)
                .Take(end - index - 1)
                .Any(token => licensedStems.Any(stem =>
                    token.StartsWith(stem, StringComparison.Ordinal))))
            {
                consumedTranslation.Add(index);
                remainingLicenses--;
            }
        }
    }

    private static void ConsumeCollapsedRussianNegations(
        IReadOnlyList<string> sourceTokens,
        IReadOnlyList<string> russianTokens,
        IReadOnlyList<int> sourceClauseIndexes,
        IReadOnlyList<int> russianClauseIndexes,
        ISet<int> consumedSource,
        ISet<int> consumedTranslation)
    {
        ConsumeCollapsedRussianNegations(
            sourceTokens,
            russianTokens,
            sourceClauseIndexes,
            russianClauseIndexes,
            consumedSource,
            consumedTranslation,
            "not",
            static token => token == "possible",
            ["невозмож"]);
        ConsumeCollapsedRussianNegations(
            sourceTokens,
            russianTokens,
            sourceClauseIndexes,
            russianClauseIndexes,
            consumedSource,
            consumedTranslation,
            "no",
            static token => token == "wonder",
            ["неудивитель"]);
        ConsumeCollapsedRussianNegations(
            sourceTokens,
            russianTokens,
            sourceClauseIndexes,
            russianClauseIndexes,
            consumedSource,
            consumedTranslation,
            "not",
            static token => token is "surprising" or "surprised",
            ["неудивитель"]);
        ConsumeCollapsedRussianNegations(
            sourceTokens,
            russianTokens,
            sourceClauseIndexes,
            russianClauseIndexes,
            consumedSource,
            consumedTranslation,
            "not",
            static token => token == "necessary",
            ["необязател"]);
        ConsumeCollapsedRussianNegations(
            sourceTokens,
            russianTokens,
            sourceClauseIndexes,
            russianClauseIndexes,
            consumedSource,
            consumedTranslation,
            "not",
            static token => token == "available",
            ["недоступ"]);
        ConsumeCollapsedRussianNegations(
            sourceTokens,
            russianTokens,
            sourceClauseIndexes,
            russianClauseIndexes,
            consumedSource,
            consumedTranslation,
            "not",
            static token => token is "clear" or "understandable",
            ["неясн", "непонят"]);
        ConsumeCollapsedRussianNegations(
            sourceTokens,
            russianTokens,
            sourceClauseIndexes,
            russianClauseIndexes,
            consumedSource,
            consumedTranslation,
            "not",
            static token => token == "able",
            ["неспособ", "невозмож"]);
    }

    private static void ConsumeCollapsedRussianNegations(
        IReadOnlyList<string> sourceTokens,
        IReadOnlyList<string> russianTokens,
        IReadOnlyList<int> sourceClauseIndexes,
        IReadOnlyList<int> russianClauseIndexes,
        ISet<int> consumedSource,
        ISet<int> consumedTranslation,
        string sourceMarker,
        Func<string, bool> matchesSourceHead,
        IReadOnlyList<string> russianStems)
    {
        if (sourceClauseIndexes.Count != sourceTokens.Count ||
            russianClauseIndexes.Count != russianTokens.Count ||
            russianClauseIndexes.Count == 0)
        {
            return;
        }

        for (int sourceIndex = 0; sourceIndex < sourceTokens.Count; sourceIndex++)
        {
            if (consumedSource.Contains(sourceIndex) ||
                sourceTokens[sourceIndex] != sourceMarker)
            {
                continue;
            }

            int sourceEnd = Math.Min(sourceTokens.Count, sourceIndex + 5);
            int sourceClause = sourceClauseIndexes[sourceIndex];
            bool hasLocalSourceHead = false;
            for (int candidate = sourceIndex + 1; candidate < sourceEnd; candidate++)
            {
                if (sourceClauseIndexes[candidate] == sourceClause &&
                    matchesSourceHead(sourceTokens[candidate]))
                {
                    hasLocalSourceHead = true;
                    break;
                }
            }

            if (!hasLocalSourceHead)
            {
                continue;
            }

            int russianClause = MapAlignedClause(
                sourceClause,
                sourceClauseIndexes,
                russianClauseIndexes);
            int russianIndex = -1;
            for (int candidate = 0; candidate < russianTokens.Count; candidate++)
            {
                if (!consumedTranslation.Contains(candidate) &&
                    russianClauseIndexes[candidate] == russianClause &&
                    russianStems.Any(stem => russianTokens[candidate]
                        .StartsWith(stem, StringComparison.Ordinal)))
                {
                    russianIndex = candidate;
                    break;
                }
            }

            if (russianIndex < 0)
            {
                continue;
            }

            consumedSource.Add(sourceIndex);
            consumedTranslation.Add(russianIndex);
        }
    }

    private static int MapAlignedClause(
        int sourceClause,
        IReadOnlyList<int> sourceClauseIndexes,
        IReadOnlyList<int> russianClauseIndexes)
    {
        int sourceClauseCount = sourceClauseIndexes.Max() + 1;
        int russianClauseCount = russianClauseIndexes.Max() + 1;
        if (sourceClauseCount <= 1 || russianClauseCount <= 1)
        {
            return 0;
        }

        double relativeClause = sourceClause / (double)(sourceClauseCount - 1);
        return (int)Math.Round(
            relativeClause * (russianClauseCount - 1),
            MidpointRounding.AwayFromZero);
    }

    private static int CountEnglishNegationUnits(
        IReadOnlyList<string> tokens,
        IReadOnlySet<int> consumed)
    {
        int count = 0;
        bool hasUnconsumedNeither = false;
        for (int index = 0; index < tokens.Count; index++)
        {
            if (consumed.Contains(index))
            {
                continue;
            }

            string token = tokens[index];
            if (token == "not" &&
                index + 1 < tokens.Count &&
                tokens[index + 1] is "only" or "merely" or "just")
            {
                continue;
            }

            if (token == "neither")
            {
                hasUnconsumedNeither = true;
                count++;
                continue;
            }

            if (token == "nor" && hasUnconsumedNeither)
            {
                continue;
            }

            if (IsEnglishPolarityMarker(token))
            {
                count++;
            }
        }

        return count;
    }

    private static int CountRussianNegationUnits(
        IReadOnlyList<string> tokens,
        IReadOnlySet<int> consumed)
    {
        HashSet<int> concordPredicateMarkers = [];
        Dictionary<int, bool> concordGroups = [];
        int standaloneHeadCount = 0;
        for (int index = 0; index < tokens.Count; index++)
        {
            if (!IsRussianConcordHead(tokens[index]))
            {
                continue;
            }

            int predicateMarker = -1;
            int end = Math.Min(tokens.Count, index + 17);
            for (int candidate = index + 1; candidate < end; candidate++)
            {
                if (IsRussianConcordScopeBoundary(tokens, candidate))
                {
                    break;
                }

                if (IsRussianPredicateNegation(tokens[candidate]))
                {
                    predicateMarker = candidate;
                    break;
                }
            }

            if (predicateMarker < 0)
            {
                if (!consumed.Contains(index))
                {
                    standaloneHeadCount++;
                }

                continue;
            }

            concordPredicateMarkers.Add(predicateMarker);
            bool contributes = !consumed.Contains(index);
            concordGroups[predicateMarker] =
                concordGroups.GetValueOrDefault(predicateMarker) || contributes;
        }

        // Russian repeats predicate "не" under one negative subject. Keep the
        // whole coordinated chain in the same semantic polarity unit unless a
        // contrast or an explicit new subject starts another clause.
        foreach (int predicateMarker in concordGroups.Keys)
        {
            for (int candidate = predicateMarker + 1;
                 candidate < tokens.Count;
                 candidate++)
            {
                if (IsRussianConcordScopeBoundary(tokens, candidate))
                {
                    break;
                }

                if (IsRussianPredicateNegation(tokens[candidate]))
                {
                    concordPredicateMarkers.Add(candidate);
                }
            }
        }

        int count = standaloneHeadCount + concordGroups.Values.Count(contributes => contributes);
        bool hasStandaloneNi = false;
        for (int index = 0; index < tokens.Count; index++)
        {
            if (consumed.Contains(index) ||
                concordPredicateMarkers.Contains(index) ||
                IsRussianConcordHead(tokens[index]))
            {
                continue;
            }

            string token = tokens[index];
            if (token == "ни")
            {
                hasStandaloneNi = true;
                continue;
            }

            if (token == "не" &&
                index + 1 < tokens.Count &&
                tokens[index + 1] is "только" or "столько" or "просто")
            {
                continue;
            }

            if (IsRussianPolarityMarker(token))
            {
                count++;
            }
        }

        return count == 0 && hasStandaloneNi ? 1 : count;
    }

    private static bool IsEnglishPolarityMarker(string token) =>
        token is "no" or "no-one" or "none" or "not" or "never" or "nobody" or
            "nothing" or "nowhere" or "unless" or "hardly" or "barely" or
            "scarcely" or "rarely" or "seldom" or "neither" or "nor" or
            "without" or "cannot" ||
        token.EndsWith("n't", StringComparison.Ordinal);

    private static bool IsRussianPolarityMarker(string token) =>
        token is "не" or "ни" or "нет" or "нельзя" or "без" or "едва" or "еле" or
            "редко" ||
        IsRussianConcordHead(token);

    private static bool IsRussianConcordHead(string token) =>
        token is "никто" or "никого" or "никому" or "никем" or "никогда" or
            "ничто" or "ничего" or "ничему" or "ничем" or "ничей" or "ничья" or
            "никакой" or "никакая" or "никакое" or "никакие" or "нигде" or
            "никуда" or "ниоткуда" or "некого" or "нечего" or "негде" or "некуда";

    private static bool IsRussianPredicateNegation(string token) =>
        token is "не" or "нет" or "нельзя";

    private static bool IsRussianConcordScopeBoundary(
        IReadOnlyList<string> tokens,
        int index)
    {
        string token = tokens[index];
        if (token is "но" or "а" or "зато")
        {
            return true;
        }

        if (token is not ("и" or "или" or "либо"))
        {
            return false;
        }

        if (index + 1 >= tokens.Count)
        {
            return true;
        }

        string next = tokens[index + 1];
        if (IsRussianPredicateNegation(next))
        {
            return false;
        }

        // These adverbs commonly continue the same omitted subject before a
        // repeated predicate negation. Other tokens conservatively start a new
        // clause so an unrelated "не" cannot be hidden by concord.
        return next is not (
            "снова" or "опять" or "больше" or "уже" or "ещё" or "еще" or
            "потом" or "сразу" or "даже" or "просто" or "почти");
    }

    private static string[] TokenizeEnglish(string value) =>
        EnglishContextLexicon.TokenRegex()
            .Matches(value ?? string.Empty)
            .Select(match => EnglishContextLexicon.NormalizeToken(match.Value))
            .Where(token => token.Length > 0)
            .ToArray();

    private static string[] TokenizeRussian(string value) =>
        RussianMorphology.TokenRegex()
            .Matches(RussianMorphology.RemoveStressMarks(value ?? string.Empty))
            .Select(match => RussianMorphology.NormalizeForStemComparison(match.Value))
            .Where(token => token.Length > 0)
            .ToArray();

    private static IReadOnlyList<PolaritySpan> FindTokenSpans(
        IReadOnlyList<string> expected,
        IReadOnlyList<string> actual,
        Func<string, string, bool> equals)
    {
        if (expected.Count == 0 || actual.Count < expected.Count)
        {
            return Array.Empty<PolaritySpan>();
        }

        List<PolaritySpan> matches = [];
        for (int start = 0; start <= actual.Count - expected.Count; start++)
        {
            bool match = true;
            for (int index = 0; index < expected.Count; index++)
            {
                if (equals(expected[index], actual[start + index]))
                {
                    continue;
                }

                match = false;
                break;
            }

            if (match)
            {
                matches.Add(new PolaritySpan(start, expected.Count));
            }
        }

        return matches;
    }

    private readonly record struct PolarityEvidence(
        string Source,
        string Translation);

    private readonly record struct PolaritySpan(int Start, int Length)
    {
        public int End => Start + Length;

        public bool Overlaps(PolaritySpan other) =>
            Start < other.End && other.Start < End;
    }
}
