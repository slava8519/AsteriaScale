using System.Globalization;
using System.Text.Json;
using AsteriaScale.Services;

/// <summary>
/// Measures whether the chosen Russian surface keeps the register of the
/// English sense (roadmap #14.2C).
///
/// This is deliberately a separate harness from the calibration benchmark. That
/// one asks "did the ranker pick the right meaning"; this one asks "having
/// picked a meaning, did the pipeline keep how blunt it was". The dev set
/// cannot answer the second question — it contains no register-ambiguous case,
/// which is why every register change so far returned decisions identical to
/// its baseline.
/// </summary>
internal static class RegisterFidelityBenchmark
{
    // Gates from roadmap #14.2C phase C.
    private const double MinimumSemanticPrecision = 0.90;
    private const double MinimumRegisterFidelity = 0.90;
    private const double MinimumNoUnjustifiedStrengthening = 0.95;
    private static readonly string[] ExpectedBuckets =
    [
        "masked-euphemistic",
        "neutral-vulgar",
        "sexual-ordinary",
        "slang-colloquial"
    ];

    private sealed record RegisterCase(
        string Id,
        string Bucket,
        string Context,
        string Target,
        int CharacterIndex,
        string ExpectedTranslation,
        string ExpectedRegister);

    private sealed record CaseOutcome(
        string Id,
        string Bucket,
        bool Resolved,
        bool Abstained,
        bool SemanticallyCorrect,
        bool RegisterFaithful,
        bool Strengthened,
        string Selected,
        SenseRegister SelectedRegister,
        SenseRegister ExpectedRegister)
    {
        /// <summary>
        /// Distinct visible translations the plan offered. One means the model
        /// was never consulted.
        /// </summary>
        public int OutcomeCount { get; init; }

        /// <summary>
        /// Which stage produced the answer. A wrong sense decided without the
        /// model is a routing defect and is cheap to fix; the same wrong sense
        /// decided by the ranker is model quality and belongs to the bake-off.
        /// Telling them apart is the whole point of recording this.
        /// </summary>
        public string Route { get; init; } = "unresolved";

        public double? Margin { get; init; }
    }

    public static async Task RunAsync(
        string setPath,
        string workerBaseDirectory,
        GlossBertRankerProvider provider,
        string? reportPath = null,
        RankerCpuModelProfile cpuModelProfile = RankerCpuModelProfile.GlossBert)
    {
        using JsonDocument document = JsonDocument.Parse(
            await File.ReadAllTextAsync(setPath));
        RegisterCase[] cases = document.RootElement.GetProperty("cases")
            .EnumerateArray()
            .Select(element => new RegisterCase(
                element.GetProperty("id").GetString()!,
                element.GetProperty("bucket").GetString()!,
                element.GetProperty("context").GetString()!,
                element.GetProperty("target").GetString()!,
                element.GetProperty("characterIndex").GetInt32(),
                element.GetProperty("expectedTranslation").GetString()!,
                element.GetProperty("expectedRegister").GetString()!))
            .ToArray();
        if (cases.Length == 0)
        {
            throw new InvalidDataException("The register benchmark contains no cases.");
        }

        string isolatedUserDirectory = Path.Combine(
            Path.GetTempPath(),
            "AsteriaScale",
            "register-benchmark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolatedUserDirectory);
        try
        {
            LocalDictionaryService dictionary = new(
                Path.Combine(AppContext.BaseDirectory, "Dictionaries"),
                isolatedUserDirectory);
            await dictionary.LoadSenseMetadataAsync();
            await using GlossBertDictionarySelectorService selector = new(
                workerBaseDirectory,
                TimeSpan.FromMinutes(10),
                cpuModelProfile);

            List<CaseOutcome> outcomes = [];
            foreach (RegisterCase benchmarkCase in cases)
            {
                outcomes.Add(await EvaluateAsync(
                    dictionary,
                    selector,
                    provider,
                    benchmarkCase));
            }

            string? modelVersion = selector.ActiveModelVersion;
            if (reportPath is not null && modelVersion is null)
            {
                throw new InvalidOperationException(
                    "The register report has no active loaded model identity.");
            }

            await ReportAsync(
                cases.Length,
                outcomes,
                reportPath,
                setPath,
                workerBaseDirectory,
                provider,
                cpuModelProfile,
                modelVersion,
                dictionary.VersionToken);
        }
        finally
        {
            try
            {
                Directory.Delete(isolatedUserDirectory, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory must not fail a measurement run.
            }
        }
    }

    private static async Task<CaseOutcome> EvaluateAsync(
        LocalDictionaryService dictionary,
        GlossBertDictionarySelectorService selector,
        GlossBertRankerProvider provider,
        RegisterCase benchmarkCase)
    {
        SenseRegister expectedRegister = ParseExpected(benchmarkCase.ExpectedRegister);
        DictionaryLookupResult lookup = await dictionary.LookupWithSenseMetadataAsync(
            benchmarkCase.Context,
            benchmarkCase.Target,
            benchmarkCase.CharacterIndex);
        if (!lookup.Found || lookup.SenseCandidates.Count == 0)
        {
            return Unresolved(benchmarkCase, expectedRegister, "no sense inventory");
        }

        SmartDictionaryRankerPlan plan = SmartDictionarySenseResolver.CreateRankerPlan(
            benchmarkCase.Context,
            lookup.Source,
            benchmarkCase.CharacterIndex,
            lookup.SenseCandidates);
        if (plan.Outcomes.Count == 0)
        {
            return Unresolved(benchmarkCase, expectedRegister, "empty plan");
        }

        SmartDictionaryRankerOutcome? selected;
        string route;
        double? margin = null;
        if (plan.Outcomes.Count == 1)
        {
            selected = plan.Outcomes[0];
            route = "single-outcome";
        }
        else
        {
            // The window and the worker find the target in the line again, so
            // they get the lookup's anchor source, exactly as the hover sends
            // it; the plan above keeps Source, as the hover's does.
            TargetCenteredContext rankerContext = TargetCenteredContextBuilder.BuildForRanker(
                benchmarkCase.Context,
                lookup.AnchorSource,
                benchmarkCase.CharacterIndex,
                provider,
                selector.CpuModelProfile);
            GlossBertDictionarySelection ranked = await selector.SelectAsync(
                rankerContext.Text,
                lookup.AnchorSource,
                rankerContext.CharacterIndex,
                plan.GlossesFor(provider.Traits().EnrichedGlosses),
                plan.CandidateGroupIds,
                "English",
                provider,
                CancellationToken.None);
            if (ranked.Abstained || ranked.SelectedGroupId is not { } groupId)
            {
                return new CaseOutcome(
                    benchmarkCase.Id,
                    benchmarkCase.Bucket,
                    Resolved: true,
                    Abstained: true,
                    SemanticallyCorrect: false,
                    RegisterFaithful: false,
                    Strengthened: false,
                    Selected: string.Empty,
                    SenseRegister.Neutral,
                    expectedRegister)
                {
                    OutcomeCount = plan.Outcomes.Count,
                    Route = "abstained",
                    Margin = ranked.Margin
                };
            }

            selected = plan.Outcomes.First(outcome => outcome.GroupId == groupId);
            route = "model";
            margin = ranked.Margin;
        }

        // SourceIndex points back into the sense inventory the plan was built
        // from, so the register of the winning surface is read from the row the
        // pipeline actually chose rather than re-derived from its text.
        string rawRegister = selected.SourceIndex >= 0 &&
                             selected.SourceIndex < lookup.SenseCandidates.Count
            ? lookup.SenseCandidates[selected.SourceIndex].Register
            : string.Empty;
        SenseRegister selectedRegister = SenseRegisters.Parse(rawRegister);
        bool semantic = SelectedSameSense(
            lookup.SenseCandidates,
            selected.SourceIndex,
            benchmarkCase.ExpectedTranslation);
        return new CaseOutcome(
            benchmarkCase.Id,
            benchmarkCase.Bucket,
            Resolved: true,
            Abstained: false,
            semantic,
            RegisterFaithful: selectedRegister == expectedRegister,
            // Strengthening is the asymmetric failure the card calls out: making
            // a plain original coarser is worse than the reverse.
            Strengthened: selectedRegister > expectedRegister,
            selected.Translation,
            selectedRegister,
            expectedRegister)
        {
            OutcomeCount = plan.Outcomes.Count,
            Route = route,
            Margin = margin
        };
    }

    private static CaseOutcome Unresolved(
        RegisterCase benchmarkCase,
        SenseRegister expectedRegister,
        string reason)
    {
        Console.WriteLine($"  {benchmarkCase.Id,-28} unresolved ({reason})");
        return new CaseOutcome(
            benchmarkCase.Id,
            benchmarkCase.Bucket,
            Resolved: false,
            Abstained: false,
            SemanticallyCorrect: false,
            RegisterFaithful: false,
            Strengthened: false,
            Selected: string.Empty,
            SenseRegister.Neutral,
            expectedRegister);
    }

    private static SenseRegister ParseExpected(string value) => value switch
    {
        "neutral" => SenseRegister.Neutral,
        "literary" => SenseRegister.Literary,
        "archaic" => SenseRegister.Archaic,
        "dialect" => SenseRegister.Dialect,
        "colloquial" => SenseRegister.Colloquial,
        "slang" => SenseRegister.Slang,
        "sexual-domain" => SenseRegister.SexualDomain,
        "vulgar" => SenseRegister.Vulgar,
        _ => throw new InvalidDataException($"Unknown expected register '{value}'.")
    };

    /// <summary>
    /// Semantic correctness is judged at the level of the **sense**, not the
    /// surface string. A sense usually carries several equally valid Russian
    /// synonyms ("говнюк" and "мудак" render the same definition), and the
    /// expected translation names only one of them. Comparing strings would
    /// score a correct sense as a miss purely because deterministic surface
    /// ranking preferred a different synonym — measuring the harness's own
    /// strictness instead of the pipeline's accuracy.
    /// </summary>
    private static bool SelectedSameSense(
        IReadOnlyList<DictionarySenseCandidate> candidates,
        int selectedSourceIndex,
        string expectedTranslation)
    {
        if (selectedSourceIndex < 0 || selectedSourceIndex >= candidates.Count)
        {
            return false;
        }

        string selectedDefinition = Normalize(
            candidates[selectedSourceIndex].EnglishDefinition);
        string expected = Normalize(expectedTranslation);
        return candidates.Any(candidate =>
            string.Equals(Normalize(candidate.Translation), expected, StringComparison.Ordinal) &&
            string.Equals(
                Normalize(candidate.EnglishDefinition),
                selectedDefinition,
                StringComparison.Ordinal));
    }

    private static string Normalize(string value) =>
        string.Join(" ", value.ToLowerInvariant().Replace('ё', 'е')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static async Task ReportAsync(
        int caseCount,
        IReadOnlyList<CaseOutcome> outcomes,
        string? reportPath,
        string setPath,
        string workerBaseDirectory,
        GlossBertRankerProvider provider,
        RankerCpuModelProfile cpuModelProfile,
        string? modelVersion,
        string dictionaryVersion)
    {
        CaseOutcome[] decided = outcomes
            .Where(outcome => outcome.Resolved && !outcome.Abstained)
            .ToArray();
        int unresolved = outcomes.Count(outcome => !outcome.Resolved);
        int abstained = outcomes.Count(outcome => outcome.Abstained);
        int semantic = decided.Count(outcome => outcome.SemanticallyCorrect);
        int faithful = decided.Count(outcome => outcome.RegisterFaithful);
        int strengthened = decided.Count(outcome => outcome.Strengthened);

        BucketGate[] buckets = outcomes
            .GroupBy(outcome => outcome.Bucket)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                CaseOutcome[] bucketDecided = group
                    .Where(outcome => outcome.Resolved && !outcome.Abstained)
                    .ToArray();
                return new BucketGate(
                    group.Key,
                    group.Count(),
                    bucketDecided.Length,
                    bucketDecided.Count(outcome => outcome.SemanticallyCorrect),
                    bucketDecided.Count(outcome => outcome.RegisterFaithful),
                    bucketDecided.Count(outcome => outcome.Strengthened));
            })
            .ToArray();

        Console.WriteLine();
        Console.WriteLine(
            $"  cases={caseCount} decided={decided.Length} abstained={abstained} " +
            $"unresolved={unresolved}");
        foreach (BucketGate bucket in buckets)
        {
            Console.WriteLine(
                $"  {bucket.Bucket,-20} n={bucket.CaseCount,3} decided={bucket.DecidedCount,3} " +
                $"abstained={bucket.CaseCount - bucket.DecidedCount,3} " +
                $"semantic={Ratio(bucket.SemanticCount, bucket.DecidedCount)} " +
                $"register={Ratio(bucket.FaithfulCount, bucket.DecidedCount)} " +
                $"strengthened={bucket.StrengthenedCount}");
        }

        Console.WriteLine();
        Console.WriteLine($"  semantic accepted precision : {Ratio(semantic, decided.Length)}");
        Console.WriteLine($"  register fidelity           : {Ratio(faithful, decided.Length)}");
        Console.WriteLine(
            $"  no unjustified strengthening: " +
            $"{Ratio(decided.Length - strengthened, decided.Length)}");

        if (reportPath is not null)
        {
            RankerBenchmarkProvenanceData provenance =
                await RankerBenchmarkProvenance.CaptureAsync(
                    setPath,
                    workerBaseDirectory,
                    provider,
                    cpuModelProfile,
                    modelVersion!);
            string? directory = Path.GetDirectoryName(Path.GetFullPath(reportPath));
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(
                reportPath,
                JsonSerializer.Serialize(
                    new
                    {
                        SchemaVersion = 3,
                        GeneratedUtc = DateTime.UtcNow.ToString(
                            "O",
                            CultureInfo.InvariantCulture),
                        Provider = provider.ToString(),
                        ModelProfile = cpuModelProfile.ToString(),
                        ModelVersion = modelVersion,
                        AbstentionTag =
                            AsteriaScale.RankerProtocol.RankerAbstention.CacheTag,
                        DictionaryVersion = dictionaryVersion,
                        provenance.BenchmarkSet,
                        provenance.RuntimeArtifacts,
                        provenance.DictionaryArtifacts,
                        provenance.ModelArtifacts,
                        ModelArtifactBytes = provenance.ModelArtifacts.Sum(
                            artifact => artifact.Bytes),
                        Cases = caseCount,
                        Decided = decided.Length,
                        Abstained = abstained,
                        Unresolved = unresolved,
                        SemanticPrecision = Fraction(semantic, decided.Length),
                        RegisterFidelity = Fraction(faithful, decided.Length),
                        NoUnjustifiedStrengthening =
                            Fraction(decided.Length - strengthened, decided.Length),
                        // Per bucket as well: the gate is defined that way, and a
                        // later baseline comparison needs the abstention counts.
                        Buckets = buckets.Select(bucket => new
                        {
                            bucket.Bucket,
                            Cases = bucket.CaseCount,
                            Decided = bucket.DecidedCount,
                            Abstained = bucket.CaseCount - bucket.DecidedCount,
                            SemanticPrecision =
                                Fraction(bucket.SemanticCount, bucket.DecidedCount),
                            RegisterFidelity =
                                Fraction(bucket.FaithfulCount, bucket.DecidedCount),
                            NoUnjustifiedStrengthening = Fraction(
                                bucket.DecidedCount - bucket.StrengthenedCount,
                                bucket.DecidedCount)
                        }),
                        Outcomes = outcomes
                    },
                    new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"  report: {Path.GetFullPath(reportPath)}");
        }

        VerifyGates(buckets, unresolved);
    }

    /// <summary>
    /// One bucket's counts. The gate is defined per bucket (roadmap #14.2C
    /// phase C: "все метрики считаются отдельно по четырём корзинам"), so it is
    /// stated that way here rather than reconstructed from an aggregate.
    /// </summary>
    internal readonly record struct BucketGate(
        string Bucket,
        int CaseCount,
        int DecidedCount,
        int SemanticCount,
        int FaithfulCount,
        int StrengthenedCount);

    internal static void VerifyGates(
        IReadOnlyList<BucketGate> buckets,
        int unresolvedCount)
    {
        // An unresolved case is a hole in the oracle, not a soft failure: the
        // set was authored against the shipped inventory, so the pipeline must
        // be able to answer every case.
        if (unresolvedCount > 0)
        {
            throw new InvalidOperationException(
                $"{unresolvedCount} register case(s) produced no dictionary result.");
        }

        string[] actualBuckets = buckets
            .Select(bucket => bucket.Bucket)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (actualBuckets.Length != ExpectedBuckets.Length ||
            !actualBuckets.SequenceEqual(ExpectedBuckets, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "The register benchmark must contain exactly the documented " +
                $"buckets [{string.Join(", ", ExpectedBuckets)}]; got " +
                $"[{string.Join(", ", actualBuckets)}].");
        }

        foreach (BucketGate bucket in buckets)
        {
            if (bucket.CaseCount <= 0)
            {
                throw new InvalidOperationException(
                    $"Register bucket '{bucket.Bucket}' contains no cases.");
            }

            if (bucket.SemanticCount < 0 || bucket.SemanticCount > bucket.DecidedCount ||
                bucket.FaithfulCount < 0 || bucket.FaithfulCount > bucket.DecidedCount ||
                bucket.StrengthenedCount < 0 || bucket.StrengthenedCount > bucket.DecidedCount ||
                bucket.DecidedCount > bucket.CaseCount)
            {
                throw new InvalidOperationException(
                    $"Register bucket '{bucket.Bucket}' reported inconsistent counts.");
            }

            // Margin-only abstention is a documented runtime outcome, not an
            // empty translation or provider failure. Still, a bucket that
            // decided nothing has undefined quality metrics and could otherwise
            // disappear from the effective gate while remaining in the file.
            if (bucket.DecidedCount == 0)
            {
                throw new InvalidOperationException(
                    $"Register bucket '{bucket.Bucket}' abstained or failed on all " +
                    $"{bucket.CaseCount} case(s), so its gates cannot be evaluated.");
            }

            Check(
                bucket.SemanticCount / (double)bucket.DecidedCount,
                MinimumSemanticPrecision,
                "semantic accepted precision",
                bucket.Bucket);
            Check(
                bucket.FaithfulCount / (double)bucket.DecidedCount,
                MinimumRegisterFidelity,
                "register fidelity",
                bucket.Bucket);
            Check(
                (bucket.DecidedCount - bucket.StrengthenedCount) / (double)bucket.DecidedCount,
                MinimumNoUnjustifiedStrengthening,
                "no unjustified strengthening",
                bucket.Bucket);
        }

        // The overall ratios need no separate gate: a weighted average of values
        // that each clear the minimum clears it too. They stay in the report.
    }

    private static void Check(double value, double minimum, string label, string bucket)
    {
        if (value < minimum)
        {
            throw new InvalidOperationException(
                $"Register gate '{label}' for bucket '{bucket}' is {value:P1}, " +
                $"below the {minimum:P0} minimum.");
        }
    }

    private static string Ratio(int part, int total) =>
        total == 0
            ? "n/a"
            : $"{part}/{total} ({Fraction(part, total):P1})".Replace(
                ",",
                ".",
                StringComparison.Ordinal);

    private static double Fraction(int part, int total) =>
        total == 0 ? 0 : part / (double)total;
}
