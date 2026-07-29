using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class RankerSentenceBenchmarkTests
{
    private const int ExpectedSchemaVersion = 1;

    private static readonly string[] RequiredScientificDomains =
    [
        "biology",
        "medicine",
        "physics",
        "chemistry",
        "statistics",
        "computer-science"
    ];

    private static readonly string[] RequiredNarrativeDomains =
    [
        "literature",
        "visual-novel",
        "manga"
    ];

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false
        };

    public static Task RunAsync(string _)
    {
        string repositoryRoot = FindRepositoryRoot();
        string path = Path.Combine(
            repositoryRoot,
            "assets",
            "Ranker",
            "ranker-sentence-benchmark.json");

        SentenceBenchmarkDocument document = JsonSerializer.Deserialize<SentenceBenchmarkDocument>(
            File.ReadAllText(path, Encoding.UTF8),
            JsonOptions) ?? throw new InvalidOperationException(
                "The sentence benchmark document could not be parsed.");

        VerifyRootContract(document);
        VerifyCases(document.Cases);
        return Task.CompletedTask;
    }

    private static void VerifyRootContract(SentenceBenchmarkDocument document)
    {
        Check(document.SchemaVersion == ExpectedSchemaVersion,
            $"Unexpected sentence benchmark schema version: {document.SchemaVersion}.");
        Check(document.LanguagePair == "en-ru",
            "The sentence benchmark language pair must remain en-ru.");
        Check(document.License == "CC0-1.0",
            "The sentence benchmark must retain its CC0-1.0 declaration.");
        Check(document.Origin.Contains("written originally", StringComparison.Ordinal),
            "The sentence benchmark must retain an explicit original-authorship notice.");
        Check(document.ScientificDomains.SequenceEqual(RequiredScientificDomains),
            "The scientific domain registry changed order or lost a required domain.");
        Check(document.FieldSemantics.TryGetValue("previousContext", out string? semantics) &&
              semantics.Contains("input-only", StringComparison.Ordinal),
            "previousContext must remain documented as input-only history.");
        Check(document.FieldSemantics.TryGetValue("surfaceScorable", out semantics) &&
              semantics.Contains("sense scoring", StringComparison.Ordinal),
            "surfaceScorable must document the sense-only scoring fallback.");
        Check(document.FieldSemantics.TryGetValue("contrastSetId", out semantics) &&
              semantics.Contains("identical sourceText", StringComparison.Ordinal),
            "contrastSetId must document the context-only contrast contract.");
    }

    private static void VerifyCases(IReadOnlyList<SentenceBenchmarkCase> cases)
    {
        Check(cases.Count >= 40,
            $"The sentence benchmark must contain at least 40 cases, found {cases.Count}.");

        HashSet<string> ids = new(StringComparer.Ordinal);
        Dictionary<string, List<SentenceBenchmarkCase>> normalizedSourceGroups =
            new(StringComparer.Ordinal);
        HashSet<string> normalizedInputs = new(StringComparer.Ordinal);
        Dictionary<string, int> domainCounts = new(StringComparer.Ordinal);
        Dictionary<int, int> goldPositionCounts = [];
        Dictionary<string, Dictionary<int, int>> domainGoldPositionCounts =
            new(StringComparer.Ordinal);
        Dictionary<string, List<SentenceBenchmarkCase>> contrastSets =
            new(StringComparer.Ordinal);
        int surfaceScorableCount = 0;
        int previousContextCount = 0;

        foreach (SentenceBenchmarkCase benchmarkCase in cases)
        {
            Check(Regex.IsMatch(
                    benchmarkCase.Id,
                    "^[a-z0-9]+(?:-[a-z0-9]+)*$",
                    RegexOptions.CultureInvariant),
                $"Case id is not canonical kebab-case: {benchmarkCase.Id}.");
            Check(ids.Add(benchmarkCase.Id),
                $"Duplicate sentence benchmark id: {benchmarkCase.Id}.");
            Check(!string.IsNullOrWhiteSpace(benchmarkCase.Domain),
                $"Case {benchmarkCase.Id} has no domain.");
            domainCounts[benchmarkCase.Domain] =
                domainCounts.GetValueOrDefault(benchmarkCase.Domain) + 1;

            Check(!string.IsNullOrWhiteSpace(benchmarkCase.SourceText),
                $"Case {benchmarkCase.Id} has no sourceText.");
            Check(!string.IsNullOrWhiteSpace(benchmarkCase.ReferenceTranslation) &&
                  benchmarkCase.ReferenceTranslation.Any(IsCyrillic),
                $"Case {benchmarkCase.Id} has no Russian full-sentence reference.");

            string normalizedSource = NormalizeCaseText(benchmarkCase.SourceText);
            if (!normalizedSourceGroups.TryGetValue(
                    normalizedSource,
                    out List<SentenceBenchmarkCase>? sourceGroup))
            {
                sourceGroup = [];
                normalizedSourceGroups.Add(normalizedSource, sourceGroup);
            }

            sourceGroup.Add(benchmarkCase);

            string normalizedPrevious = NormalizeCaseText(
                benchmarkCase.PreviousContext ?? string.Empty);
            string normalizedInput = normalizedPrevious + '\u001f' + normalizedSource;
            Check(normalizedInputs.Add(normalizedInput),
                $"Duplicate normalized sentence input: {benchmarkCase.Id}.");

            if (benchmarkCase.PreviousContext is not null)
            {
                Check(!string.IsNullOrWhiteSpace(benchmarkCase.PreviousContext),
                    $"Case {benchmarkCase.Id} has an empty previousContext field.");
                Check(normalizedPrevious != normalizedSource,
                    $"Case {benchmarkCase.Id} duplicates sourceText as previousContext.");
                previousContextCount++;
            }

            if (benchmarkCase.ContrastSetId is not null)
            {
                Check(!string.IsNullOrWhiteSpace(benchmarkCase.ContrastSetId),
                    $"Case {benchmarkCase.Id} has an empty contrastSetId.");
                Check(benchmarkCase.PreviousContext is not null,
                    $"Contrast case {benchmarkCase.Id} has no previousContext.");
                if (!contrastSets.TryGetValue(
                        benchmarkCase.ContrastSetId,
                        out List<SentenceBenchmarkCase>? contrastCases))
                {
                    contrastCases = [];
                    contrastSets.Add(benchmarkCase.ContrastSetId, contrastCases);
                }

                contrastCases.Add(benchmarkCase);
            }

            Check(benchmarkCase.Targets.Count is >= 2 and <= 6,
                $"Case {benchmarkCase.Id} must contain 2-6 ambiguous targets.");
            VerifyTargets(benchmarkCase);
            foreach (SentenceTarget target in benchmarkCase.Targets)
            {
                if (target.SurfaceScorable == true)
                {
                    surfaceScorableCount++;
                }

                goldPositionCounts[target.GoldGroupId] =
                    goldPositionCounts.GetValueOrDefault(target.GoldGroupId) + 1;
                if (!domainGoldPositionCounts.TryGetValue(
                        benchmarkCase.Domain,
                        out Dictionary<int, int>? positions))
                {
                    positions = [];
                    domainGoldPositionCounts.Add(benchmarkCase.Domain, positions);
                }

                positions[target.GoldGroupId] =
                    positions.GetValueOrDefault(target.GoldGroupId) + 1;
            }
        }

        int scientificCount = RequiredScientificDomains.Sum(
            domain => domainCounts.GetValueOrDefault(domain));
        Check(scientificCount >= 30,
            $"At least 30 scientific cases are required, found {scientificCount}.");

        foreach (string domain in RequiredScientificDomains)
        {
            Check(domainCounts.GetValueOrDefault(domain) >= 5,
                $"Scientific domain '{domain}' must contain at least five cases.");
        }

        foreach (string domain in RequiredNarrativeDomains)
        {
            Check(domainCounts.GetValueOrDefault(domain) >= 2,
                $"Narrative domain '{domain}' must contain at least two cases.");
        }

        Check(previousContextCount >= 10,
            $"At least ten cases must exercise hidden previousContext; found {previousContextCount}.");
        Check(surfaceScorableCount >= 80,
            $"At least 80 targets must support surface-unambiguous sense scoring; " +
            $"found {surfaceScorableCount}.");
        VerifyContrastSets(normalizedSourceGroups, contrastSets);
        VerifyGoldPositionBalance(
            "the complete benchmark",
            goldPositionCounts,
            maximumShare: 0.60);
        foreach ((string domain, Dictionary<int, int> positions) in domainGoldPositionCounts)
        {
            VerifyGoldPositionBalance(
                $"domain '{domain}'",
                positions,
                maximumShare: 0.67);
        }
    }

    private static void VerifyContrastSets(
        IReadOnlyDictionary<string, List<SentenceBenchmarkCase>> normalizedSourceGroups,
        IReadOnlyDictionary<string, List<SentenceBenchmarkCase>> contrastSets)
    {
        Check(contrastSets.Count >= 2,
            $"At least two previous-context contrast sets are required; found {contrastSets.Count}.");

        foreach (List<SentenceBenchmarkCase> sourceGroup in normalizedSourceGroups.Values)
        {
            if (sourceGroup.Count == 1)
            {
                Check(sourceGroup[0].ContrastSetId is null,
                    $"Case {sourceGroup[0].Id} declares a contrastSetId without a source twin.");
                continue;
            }

            Check(sourceGroup.Count == 2,
                $"A normalized source may occur only as a two-case contrast pair: " +
                $"{string.Join(", ", sourceGroup.Select(item => item.Id))}.");
            SentenceBenchmarkCase left = sourceGroup[0];
            SentenceBenchmarkCase right = sourceGroup[1];
            Check(left.ContrastSetId is not null &&
                  left.ContrastSetId == right.ContrastSetId,
                $"Source twins {left.Id}/{right.Id} do not share one contrastSetId.");
            Check(left.SourceText.Equals(right.SourceText, StringComparison.Ordinal),
                $"Contrast set {left.ContrastSetId} must use byte-identical sourceText.");
            Check(!NormalizeCaseText(left.PreviousContext ?? string.Empty).Equals(
                    NormalizeCaseText(right.PreviousContext ?? string.Empty),
                    StringComparison.Ordinal),
                $"Contrast set {left.ContrastSetId} repeats previousContext.");
            Check(!left.ReferenceTranslation.Equals(
                    right.ReferenceTranslation,
                    StringComparison.Ordinal),
                $"Contrast set {left.ContrastSetId} does not change the full reference.");
            Check(left.Targets.Count == right.Targets.Count,
                $"Contrast set {left.ContrastSetId} changes its target count.");

            bool hasChangedGold = false;
            for (int targetIndex = 0; targetIndex < left.Targets.Count; targetIndex++)
            {
                SentenceTarget leftTarget = left.Targets[targetIndex];
                SentenceTarget rightTarget = right.Targets[targetIndex];
                Check(leftTarget.Text == rightTarget.Text &&
                      leftTarget.CharacterIndex == rightTarget.CharacterIndex &&
                      leftTarget.Length == rightTarget.Length &&
                      leftTarget.SurfaceScorable == rightTarget.SurfaceScorable,
                    $"Contrast set {left.ContrastSetId} changes target shape at " +
                    $"index {targetIndex}.");
                Check(leftTarget.Groups.Count == rightTarget.Groups.Count,
                    $"Contrast set {left.ContrastSetId} changes candidate count at " +
                    $"target {leftTarget.Text}.");
                for (int groupIndex = 0; groupIndex < leftTarget.Groups.Count; groupIndex++)
                {
                    SentenceSenseGroup leftGroup = leftTarget.Groups[groupIndex];
                    SentenceSenseGroup rightGroup = rightTarget.Groups[groupIndex];
                    Check(leftGroup.GroupId == rightGroup.GroupId &&
                          leftGroup.Gloss == rightGroup.Gloss &&
                          leftGroup.Translations.SequenceEqual(
                              rightGroup.Translations,
                              StringComparer.Ordinal),
                        $"Contrast set {left.ContrastSetId}/{leftTarget.Text} changes " +
                        $"candidate group {groupIndex}.");
                }

                if (leftTarget.GoldGroupId != rightTarget.GoldGroupId)
                {
                    hasChangedGold = true;
                    Check(leftTarget.ExpectedTranslation != rightTarget.ExpectedTranslation,
                        $"Contrast set {left.ContrastSetId}/{leftTarget.Text} changes gold " +
                        "without changing its expected surface.");
                }
            }

            Check(hasChangedGold,
                $"Contrast set {left.ContrastSetId} does not require previousContext.");
        }

        foreach ((string contrastSetId, List<SentenceBenchmarkCase> contrastCases) in contrastSets)
        {
            Check(contrastCases.Count == 2,
                $"Contrast set {contrastSetId} must contain exactly two cases.");
        }
    }

    private static void VerifyGoldPositionBalance(
        string scope,
        IReadOnlyDictionary<int, int> positions,
        double maximumShare)
    {
        int total = positions.Values.Sum();
        Check(positions.Count >= 2,
            $"Gold labels in {scope} occupy only one candidate position.");
        double dominantShare = positions.Values.Max() / (double)total;
        Check(dominantShare <= maximumShare,
            $"Gold labels in {scope} expose positional leakage: " +
            $"{dominantShare:P1} use one candidate position.");
    }

    private static void VerifyTargets(SentenceBenchmarkCase benchmarkCase)
    {
        int previousSpanEnd = -1;
        HashSet<string> targetTexts = new(StringComparer.Ordinal);

        foreach (SentenceTarget target in benchmarkCase.Targets)
        {
            Check(!string.IsNullOrWhiteSpace(target.Text),
                $"Case {benchmarkCase.Id} contains an empty target.");
            Check(targetTexts.Add(target.Text),
                $"Case {benchmarkCase.Id} repeats target '{target.Text}'.");
            Check(target.Length == target.Text.Length,
                $"Case {benchmarkCase.Id}/{target.Text} stores an incorrect length.");
            Check(target.CharacterIndex >= 0 &&
                  target.CharacterIndex + target.Length <= benchmarkCase.SourceText.Length,
                $"Case {benchmarkCase.Id}/{target.Text} has an out-of-range span.");

            string exactSpan = benchmarkCase.SourceText.Substring(
                target.CharacterIndex,
                target.Length);
            Check(exactSpan.Equals(target.Text, StringComparison.Ordinal),
                $"Case {benchmarkCase.Id}/{target.Text} does not match its exact source span.");
            Check(benchmarkCase.SourceText.IndexOf(target.Text, StringComparison.Ordinal) ==
                  target.CharacterIndex &&
                  benchmarkCase.SourceText.LastIndexOf(target.Text, StringComparison.Ordinal) ==
                  target.CharacterIndex,
                $"Case {benchmarkCase.Id}/{target.Text} is not anchored to one exact occurrence.");
            Check(target.CharacterIndex >= previousSpanEnd,
                $"Case {benchmarkCase.Id} targets must be ordered and non-overlapping.");
            previousSpanEnd = target.CharacterIndex + target.Length;

            Check(target.Groups.Count >= 2,
                $"Case {benchmarkCase.Id}/{target.Text} is not ambiguous.");
            Check(target.SurfaceScorable.HasValue,
                $"Case {benchmarkCase.Id}/{target.Text} has no surfaceScorable policy.");
            Dictionary<string, int> translationOwners = new(StringComparer.Ordinal);
            bool hasCrossGroupSurfaceCollision = false;
            for (int groupIndex = 0; groupIndex < target.Groups.Count; groupIndex++)
            {
                SentenceSenseGroup group = target.Groups[groupIndex];
                Check(group.GroupId == groupIndex,
                    $"Case {benchmarkCase.Id}/{target.Text} violates GroupId == index.");
                Check(!string.IsNullOrWhiteSpace(group.Gloss),
                    $"Case {benchmarkCase.Id}/{target.Text}/{groupIndex} has no gloss.");
                Check(group.Translations.Count > 0 &&
                      group.Translations.All(value => !string.IsNullOrWhiteSpace(value)),
                    $"Case {benchmarkCase.Id}/{target.Text}/{groupIndex} has invalid translations.");
                Check(group.Translations.Distinct(StringComparer.Ordinal).Count() ==
                      group.Translations.Count,
                    $"Case {benchmarkCase.Id}/{target.Text}/{groupIndex} repeats a translation.");
                foreach (string translation in group.Translations)
                {
                    string normalizedTranslation = NormalizeCaseText(translation);
                    if (translationOwners.TryGetValue(
                            normalizedTranslation,
                            out int ownerGroupId) &&
                        ownerGroupId != group.GroupId)
                    {
                        hasCrossGroupSurfaceCollision = true;
                        if (target.SurfaceScorable == true)
                        {
                            throw new InvalidOperationException(
                                $"Case {benchmarkCase.Id}/{target.Text} exposes translation " +
                                $"'{translation}' in both groups {ownerGroupId} and {group.GroupId}.");
                        }
                    }

                    translationOwners.TryAdd(normalizedTranslation, group.GroupId);
                }
            }

            Check(target.SurfaceScorable == true || hasCrossGroupSurfaceCollision,
                $"Case {benchmarkCase.Id}/{target.Text} opts out of " +
                "surface-unambiguous sense scoring " +
                "without a cross-sense Russian surface collision.");

            SentenceSenseGroup goldGroup = target.Groups.FirstOrDefault(
                    group => group.GroupId == target.GoldGroupId)
                ?? throw new InvalidOperationException(
                    $"Case {benchmarkCase.Id}/{target.Text} references a missing gold group.");
            Check(goldGroup.Translations.Contains(
                    target.ExpectedTranslation,
                    StringComparer.Ordinal),
                $"Case {benchmarkCase.Id}/{target.Text} expectedTranslation is absent " +
                "from the gold group.");
        }
    }

    private static string NormalizeCaseText(string value)
    {
        string canonical = value.Replace('’', '\'').ToLowerInvariant();
        string[] tokens = Regex.Split(
                canonical,
                "[^\\p{L}\\p{Nd}']+",
                RegexOptions.CultureInvariant)
            .Where(token => token.Length > 0)
            .ToArray();
        return string.Join(' ', tokens);
    }

    private static bool IsCyrillic(char value) =>
        value is >= '\u0400' and <= '\u04ff';

    private static string FindRepositoryRoot()
    {
        string? current = AppContext.BaseDirectory;
        while (current is not null &&
               !File.Exists(Path.Combine(current, "AsteriaScale.slnx")))
        {
            current = Path.GetDirectoryName(current);
        }

        return current ?? throw new InvalidOperationException(
            "Repository root was not found for sentence benchmark tests.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class SentenceBenchmarkDocument
    {
        public int SchemaVersion { get; init; }
        public string LanguagePair { get; init; } = string.Empty;
        public string License { get; init; } = string.Empty;
        public string Origin { get; init; } = string.Empty;
        public IReadOnlyDictionary<string, string> FieldSemantics { get; init; } =
            new Dictionary<string, string>();
        public IReadOnlyList<string> ScientificDomains { get; init; } = [];
        public IReadOnlyList<SentenceBenchmarkCase> Cases { get; init; } = [];
    }

    private sealed class SentenceBenchmarkCase
    {
        public string Id { get; init; } = string.Empty;
        public string Domain { get; init; } = string.Empty;
        public string SourceText { get; init; } = string.Empty;
        public string? PreviousContext { get; init; }
        public string? ContrastSetId { get; init; }
        public string ReferenceTranslation { get; init; } = string.Empty;
        public IReadOnlyList<SentenceTarget> Targets { get; init; } = [];
    }

    private sealed class SentenceTarget
    {
        public string Text { get; init; } = string.Empty;
        public int CharacterIndex { get; init; }
        public int Length { get; init; }
        public IReadOnlyList<SentenceSenseGroup> Groups { get; init; } = [];
        public int GoldGroupId { get; init; }
        public string ExpectedTranslation { get; init; } = string.Empty;
        public bool? SurfaceScorable { get; init; }
    }

    private sealed class SentenceSenseGroup
    {
        public int GroupId { get; init; }
        public string Gloss { get; init; } = string.Empty;
        public IReadOnlyList<string> Translations { get; init; } = [];
    }
}
