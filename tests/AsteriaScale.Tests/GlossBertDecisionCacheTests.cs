using AsteriaScale.RankerProtocol;
using AsteriaScale.Services;

/// <summary>
/// The shared GlossBERT decision cache is the single place where a hover
/// lookup and the full-text scaffold can reuse each other's inference. Its two
/// obligations pull in opposite directions: the fingerprint must merge requests
/// the worker would answer identically (otherwise nothing is ever shared), and
/// it must never merge requests the worker would answer differently.
/// </summary>
internal static class GlossBertDecisionCacheTests
{
    public static async Task RunAsync(string tempRoot)
    {
        string root = Path.Combine(tempRoot, "glossbert-decision-cache");
        Directory.CreateDirectory(root);

        VerifyFingerprintSharing();
        VerifyTargetSurvivesTrimming();
        VerifyScaffoldAndHoverAgree();
        VerifyAbstentionThresholdIsKeyed();
        VerifyEnrichmentGateIsClosed();
        VerifyFingerprintSeparation();
        await VerifyRoundTripAndPersistenceAsync(root);
        await VerifyRejectedDecisionsAsync(root);
        await VerifyStaleGroupIdAsync(root);
        await VerifyCorruptFileRecoveryAsync(root);
    }

    private const string Sentence = "She sat on the bank and watched the water.";
    private const string Target = "bank";

    /// <summary>
    /// The whole point of A4: hover passes a cursor position inside the word,
    /// the scaffold passes the token start. Both must reach one entry.
    /// </summary>
    private static void VerifyFingerprintSharing()
    {
        SmartDictionaryRankerPlan plan = CreatePlan();
        int tokenStart = Sentence.IndexOf(Target, StringComparison.Ordinal);
        string scaffoldKey = Fingerprint(Sentence, tokenStart, plan);
        for (int offset = 0; offset < Target.Length; offset++)
        {
            Check(Fingerprint(Sentence, tokenStart + offset, plan) == scaffoldKey,
                $"A cursor {offset} character(s) into the target changed the shared key.");
        }

        Check(scaffoldKey.Length == 64 && scaffoldKey.All(Uri.IsHexDigit),
            "The decision fingerprint is not a SHA-256 hex digest.");

        // Whitespace-only differences reach the worker identically.
        Check(Fingerprint(
                  "She sat on the  bank\tand watched the water.",
                  tokenStart,
                  plan) == scaffoldKey,
            "Collapsible whitespace produced a different decision key.");
    }

    /// <summary>
    /// The scaffold reaches the cache through its own request-shaped wrapper.
    /// If that wrapper and the hover call ever pass a differently composed
    /// dictionary version, both paths keep working and silently stop sharing —
    /// a regression with no visible symptom other than doubled inference.
    /// </summary>
    /// <summary>
    /// The defect roadmap #14.2B fixes. The worker's pair tokenizer truncates
    /// from the right, so on a reply longer than the sequence budget a target
    /// sitting late in the line was cut before the model saw it — the ranker
    /// then scored glosses against a context missing the very word being
    /// disambiguated. The dev benchmark cannot catch this: its longest context
    /// is 12 words, and no case puts the target past word 30.
    /// </summary>
    private static void VerifyTargetSurvivesTrimming()
    {
        int budget = TargetCenteredContextBuilder.MaximumWords;
        string lead = string.Join(
            " ",
            Enumerable.Range(0, budget * 2).Select(index => "lead" + index));
        string tail = string.Join(
            " ",
            Enumerable.Range(0, 4).Select(index => "tail" + index));
        string sentence = lead + " " + Target + " " + tail + ".";
        int rawIndex = sentence.IndexOf(Target, StringComparison.Ordinal);

        TargetCenteredContext centered = TargetCenteredContextBuilder.Build(
            sentence,
            Target,
            rawIndex);
        Check(centered.Trimmed,
            "An over-budget reply was not trimmed.");
        Check(centered.CharacterIndex >= 0 &&
              centered.CharacterIndex + Target.Length <= centered.Text.Length &&
              string.Equals(
                  centered.Text.Substring(centered.CharacterIndex, Target.Length),
                  Target,
                  StringComparison.Ordinal),
            $"The target did not survive trimming at its reported index: '{centered.Text}'");
        Check(!centered.Text.Contains("lead0", StringComparison.Ordinal),
            "Trimming kept the far end of the reply instead of the target's neighbourhood.");
        Check(centered.Text.Contains("tail0", StringComparison.Ordinal),
            "Trimming dropped context following the target.");
        Check(centered.Text.Split(' ').Length <= budget + 1,
            $"Trimming exceeded its word budget: '{centered.Text}'");

        // A target at the very start must still get a full-width window, with
        // the unused left side spent on the right.
        string leading = Target + " " + lead + ".";
        TargetCenteredContext atStart = TargetCenteredContextBuilder.Build(
            leading,
            Target,
            0);
        Check(atStart.CharacterIndex == 0 &&
              atStart.Text.StartsWith(Target, StringComparison.Ordinal),
            $"A leading target lost its position: '{atStart.Text}'");
        Check(atStart.Text.Split(' ').Length >= budget - 1,
            "A leading target did not receive the remaining budget on its right.");
    }

    private static void VerifyScaffoldAndHoverAgree()
    {
        SmartDictionaryRankerPlan plan = CreatePlan();
        int tokenStart = Sentence.IndexOf(Target, StringComparison.Ordinal);
        const string dictionaryVersion = "dictionary-v1";

        string scaffoldKey = TranslationScaffoldGlossBertRanker.CreateCacheKey(
            new TranslationScaffoldNeuralRequest(Sentence, Target, tokenStart, plan),
            GlossBertRankerProvider.Cpu,
            "model-v1",
            dictionaryVersion);
        string hoverKey = GlossBertDecisionCache.CreateFingerprint(
            Sentence,
            Target,
            // Hover anchors on the cursor, which lands inside the word.
            tokenStart + 2,
            plan,
            GlossBertRankerProvider.Cpu,
            "model-v1",
            dictionaryVersion);
        Check(scaffoldKey == hoverKey,
            "The scaffold and hover paths compute different decision keys for " +
            "the same worker call.");
    }

    /// <summary>
    /// Abstentions are cached, and the abstention threshold is what produces
    /// them. Recalibrating it (roadmap #8 aims to cut abstentions by 20%) must
    /// separate the key space, otherwise the stored verdicts would mask exactly
    /// the metric the recalibration is trying to move.
    /// </summary>
    private static void VerifyAbstentionThresholdIsKeyed()
    {
        SmartDictionaryRankerPlan plan = CreatePlan();
        int tokenStart = Sentence.IndexOf(Target, StringComparison.Ordinal);

        Check(RankerAbstention.MarginThreshold == 0.25,
            "The frozen abstention threshold changed without a benchmark run " +
            $"(AGENTS.md rule 4): {RankerAbstention.MarginThreshold}.");
        Check(RankerAbstention.CacheTag.Contains(
                  "0.25",
                  StringComparison.Ordinal),
            $"The abstention cache tag lost its value: {RankerAbstention.CacheTag}");

        string current = GlossBertDecisionCache.CreateFingerprint(
            Sentence,
            Target,
            tokenStart,
            plan,
            GlossBertRankerProvider.Cpu,
            "model-v1",
            "dictionary-v1",
            RankerAbstention.CacheTag);
        Check(current == Fingerprint(Sentence, tokenStart, plan),
            "The production fingerprint does not use the shared abstention tag.");

        string recalibrated = GlossBertDecisionCache.CreateFingerprint(
            Sentence,
            Target,
            tokenStart,
            plan,
            GlossBertRankerProvider.Cpu,
            "model-v1",
            "dictionary-v1",
            "abstain-margin=0.18");
        Check(current != recalibrated,
            "A recalibrated abstention threshold reused the previous decision key.");
    }

    /// <summary>
    /// Roadmap #8 candidate 5 (enriched sense representation) was measured and
    /// rejected; the mechanism stays for the training track. Two things must
    /// hold: it is inert everywhere worker input is formed, and the decision key
    /// follows the rendering — otherwise a future experiment would read verdicts
    /// computed from glosses the worker never saw.
    /// </summary>
    private static void VerifyEnrichmentGateIsClosed()
    {
        // The rule fires on a gloss outmatched by its neighbour, which is the
        // case it was designed for: a terse slang sense losing to a detailed
        // literal one. CreatePlan's two glosses are of similar length and would
        // render identically with the gate open, proving nothing.
        SmartDictionaryRankerPlan plan = new(
            [
                new SmartDictionaryRankerOutcome(0, 0, "бабки"),
                new SmartDictionaryRankerOutcome(1, 1, "берег")
            ],
            ["Money.", "sloping land alongside a river or a lake"],
            [0, 1]);
        SmartDictionaryRankerPlan enriched = plan with
        {
            CandidateEnrichments = ["cash, dough", "riverside, embankment"]
        };
        int tokenStart = Sentence.IndexOf(Target, StringComparison.Ordinal);

        Check(!SmartDictionaryRankerPlan.EnrichmentEnabled,
            "Candidate 5 was re-enabled. It is a measured negative result: see " +
            "docs/RESEARCH.md, and re-run the bake-off with a cleared cache " +
            "before trusting any number.");
        Check(ReferenceEquals(enriched.GlossesFor(true), enriched.CandidateGlosses),
            "The CPU rendering enriched glosses while the gate is closed — the " +
            "rejected variant would ship unmeasured.");
        Check(ReferenceEquals(
                enriched.GlossesFor(true, enabled: false),
                enriched.CandidateGlosses),
            "The gate parameter does not actually suppress enrichment.");
        Check(!ReferenceEquals(
                enriched.GlossesFor(true, enabled: true),
                enriched.CandidateGlosses),
            "The enrichment mechanism no longer renders anything; the stored " +
            "sidecar column and its measurements have quietly rotted.");
        Check(ReferenceEquals(
                enriched.GlossesFor(false, enabled: true),
                enriched.CandidateGlosses),
            "The NPU rendering spent sequence budget on enrichment; a pair " +
            "already truncates above the median gloss there.");

        // The key must follow the rendering, not the plan's raw glosses.
        // Otherwise reopening candidate 5 would read back verdicts the worker
        // produced from different input — the defect that hid the abstention
        // threshold from the key space.
        Check(Fingerprint(Sentence, tokenStart, enriched, enrichmentEnabled: true) !=
              Fingerprint(Sentence, tokenStart, enriched),
            "The decision key ignored the enriched gloss rendering.");
        Check(Fingerprint(Sentence, tokenStart, enriched) ==
              Fingerprint(Sentence, tokenStart, plan),
            "An inert enrichment column split the decision key space and " +
            "silently invalidated every stored verdict.");
    }

    private static void VerifyFingerprintSeparation()
    {
        SmartDictionaryRankerPlan plan = CreatePlan();
        int tokenStart = Sentence.IndexOf(Target, StringComparison.Ordinal);
        string baseline = Fingerprint(Sentence, tokenStart, plan);

        Check(baseline != Fingerprint(
                Sentence,
                tokenStart,
                plan,
                provider: GlossBertRankerProvider.Npu),
            "The decision key ignored the execution provider.");
        Check(baseline != Fingerprint(
                Sentence,
                tokenStart,
                plan,
                modelIdentity: "model-v2"),
            "The decision key ignored the model identity.");
        Check(baseline != Fingerprint(
                Sentence,
                tokenStart,
                plan,
                dictionaryVersion: "dictionary-v2"),
            "The decision key ignored the dictionary version.");
        Check(baseline != Fingerprint(
                "He crossed the bank of the river.",
                "He crossed the bank of the river.".IndexOf(
                    Target,
                    StringComparison.Ordinal),
                plan),
            "The decision key ignored the sentence.");

        SmartDictionaryRankerPlan reorderedOutcomes = new(
            [
                new SmartDictionaryRankerOutcome(0, 1, "берег"),
                new SmartDictionaryRankerOutcome(1, 0, "банк")
            ],
            plan.CandidateGlosses,
            plan.CandidateGroupIds);
        Check(baseline != Fingerprint(Sentence, tokenStart, reorderedOutcomes),
            "The decision key ignored the GroupId-to-translation mapping.");

        SmartDictionaryRankerPlan regroupedGlosses = new(
            plan.Outcomes,
            plan.CandidateGlosses,
            [1, 0]);
        Check(baseline != Fingerprint(Sentence, tokenStart, regroupedGlosses),
            "The decision key ignored the gloss-to-group assignment.");

        SmartDictionaryRankerPlan otherGlosses = new(
            plan.Outcomes,
            ["a financial institution", "sloping ground beside water"],
            plan.CandidateGroupIds);
        Check(baseline != Fingerprint(Sentence, tokenStart, otherGlosses),
            "The decision key ignored the candidate glosses.");

        // Two standalone occurrences are two different anchors for the worker.
        const string repeated = "The bank near the other bank was closed.";
        int first = repeated.IndexOf(Target, StringComparison.Ordinal);
        int second = repeated.IndexOf(Target, first + 1, StringComparison.Ordinal);
        Check(Fingerprint(repeated, first, plan) !=
              Fingerprint(repeated, second, plan),
            "Two occurrences of the target collapsed onto one decision key.");
    }

    private static async Task VerifyRoundTripAndPersistenceAsync(string root)
    {
        string path = Path.Combine(root, "round-trip.json");
        SmartDictionaryRankerPlan plan = CreatePlan();
        int tokenStart = Sentence.IndexOf(Target, StringComparison.Ordinal);
        string scaffoldKey = Fingerprint(Sentence, tokenStart, plan);
        GlossBertDecision selected = new(
            SelectedGroupId: 1,
            Abstained: false,
            Margin: 0.42,
            Score: 0.81,
            Confidence: 0.81,
            GlossBertDictionarySelectorService.CpuExecutionProvider,
            "CPU",
            "model-v1",
            "cross");
        GlossBertDecision abstained = new(
            SelectedGroupId: null,
            Abstained: true,
            Margin: 0.12,
            Score: 0.55,
            Confidence: 0.55,
            GlossBertDictionarySelectorService.CpuExecutionProvider,
            "CPU",
            "model-v1",
            "cross");
        string abstainedKey = Fingerprint(
            "I opened an account at the bank today.",
            "I opened an account at the bank today.".IndexOf(
                Target,
                StringComparison.Ordinal),
            plan);

        await using (GlossBertDecisionCache cache = new(path))
        {
            Check(await cache.StoreAsync(scaffoldKey, selected),
                "A valid selection was rejected.");
            Check(await cache.StoreAsync(abstainedKey, abstained),
                "A calibrated abstention was rejected.");
            await cache.FlushAsync();
        }

        // A fresh instance stands in for the next application launch: the
        // scaffold's in-memory cache used to lose every decision on restart.
        await using (GlossBertDecisionCache reopened = new(path))
        {
            // The hover pipeline asks with a cursor offset, not a token start.
            GlossBertDecision? hoverHit = await reopened.TryGetAsync(
                Fingerprint(Sentence, tokenStart + 2, plan),
                plan.Outcomes.Count);
            Check(hoverHit is
                {
                    Abstained: false,
                    SelectedGroupId: 1,
                    Margin: 0.42,
                    Confidence: 0.81,
                    ModelVersion: "model-v1",
                    Pipeline: "cross"
                },
                $"A scaffold decision did not survive a restart for hover: {hoverHit}");

            GlossBertDecision? abstainedHit = await reopened.TryGetAsync(
                abstainedKey,
                plan.Outcomes.Count);
            Check(abstainedHit is { Abstained: true, SelectedGroupId: null },
                $"A persisted abstention lost its shape: {abstainedHit}");

            Check(await reopened.TryGetAsync(
                    Fingerprint(Sentence, tokenStart, plan, modelIdentity: "model-v2"),
                    plan.Outcomes.Count) is null,
                "A decision was served across a model identity change.");
            Check(await reopened.TryGetAsync(
                    Fingerprint(
                        Sentence,
                        tokenStart,
                        plan,
                        dictionaryVersion: "dictionary-v2"),
                    plan.Outcomes.Count) is null,
                "A decision was served across a dictionary version change.");
            Check(await reopened.TryGetAsync(
                    Fingerprint(
                        Sentence,
                        tokenStart,
                        plan,
                        provider: GlossBertRankerProvider.Npu),
                    plan.Outcomes.Count) is null,
                "A CPU decision was served for an NPU request.");
        }
    }

    private static async Task VerifyRejectedDecisionsAsync(string root)
    {
        string path = Path.Combine(root, "rejected.json");
        SmartDictionaryRankerPlan plan = CreatePlan();
        string key = Fingerprint(
            Sentence,
            Sentence.IndexOf(Target, StringComparison.Ordinal),
            plan);

        await using GlossBertDecisionCache cache = new(path);
        Check(!await cache.StoreAsync(
                key,
                new GlossBertDecision(
                    SelectedGroupId: null,
                    Abstained: false,
                    0.4,
                    0.8,
                    0.8,
                    "CPUExecutionProvider",
                    "CPU",
                    "model-v1",
                    "cross")),
            "A selection without a GroupId was stored.");
        Check(!await cache.StoreAsync(
                key,
                new GlossBertDecision(
                    SelectedGroupId: 1,
                    Abstained: false,
                    0.4,
                    0.8,
                    0.8,
                    "CPUExecutionProvider",
                    "CPU",
                    ModelVersion: "   ",
                    "cross")),
            "A decision without a model identity was stored.");
        Check(await cache.TryGetAsync(key, plan.Outcomes.Count) is null,
            "A rejected decision became readable.");

        // An abstention carrying a GroupId is a protocol violation upstream;
        // normalizing it to a pure abstention keeps invariant 2 intact.
        Check(await cache.StoreAsync(
                key,
                new GlossBertDecision(
                    SelectedGroupId: 3,
                    Abstained: true,
                    0.1,
                    0.5,
                    0.5,
                    "CPUExecutionProvider",
                    "CPU",
                    "model-v1",
                    "cross")),
            "A normalizable abstention was rejected outright.");
        Check(await cache.TryGetAsync(key, plan.Outcomes.Count) is
            { Abstained: true, SelectedGroupId: null },
            "A stored abstention kept a group id.");
    }

    private static async Task VerifyStaleGroupIdAsync(string root)
    {
        string path = Path.Combine(root, "stale-group.json");
        SmartDictionaryRankerPlan plan = CreatePlan();
        string key = Fingerprint(
            Sentence,
            Sentence.IndexOf(Target, StringComparison.Ordinal),
            plan);

        await using GlossBertDecisionCache cache = new(path);
        Check(await cache.StoreAsync(
                key,
                new GlossBertDecision(
                    SelectedGroupId: 1,
                    Abstained: false,
                    0.4,
                    0.8,
                    0.8,
                    "CPUExecutionProvider",
                    "CPU",
                    "model-v1",
                    "cross")),
            "A valid selection was rejected.");

        // GroupId is an index into the caller's outcomes. A plan that can no
        // longer address it must miss instead of returning a foreign sense.
        Check(await cache.TryGetAsync(key, outcomeCount: 1) is null,
            "A GroupId outside the current plan was served.");
        Check(await cache.TryGetAsync(key, plan.Outcomes.Count) is null,
            "An unaddressable entry was served after being dropped.");
    }

    private static async Task VerifyCorruptFileRecoveryAsync(string root)
    {
        string path = Path.Combine(root, "corrupt.json");
        await File.WriteAllTextAsync(path, "{\"version\":1,\"entries\":[{\"key\":");

        SmartDictionaryRankerPlan plan = CreatePlan();
        string key = Fingerprint(
            Sentence,
            Sentence.IndexOf(Target, StringComparison.Ordinal),
            plan);
        await using GlossBertDecisionCache cache = new(path);
        Check(await cache.TryGetAsync(key, plan.Outcomes.Count) is null,
            "A truncated cache file produced a decision.");
        Check(await cache.StoreAsync(
                key,
                new GlossBertDecision(
                    SelectedGroupId: 0,
                    Abstained: false,
                    0.4,
                    0.8,
                    0.8,
                    "CPUExecutionProvider",
                    "CPU",
                    "model-v1",
                    "cross")),
            "A cache recovered from a corrupt file refused to store.");
        Check(await cache.TryGetAsync(key, plan.Outcomes.Count) is
            { SelectedGroupId: 0 },
            "A cache recovered from a corrupt file lost the new decision.");
    }

    private static SmartDictionaryRankerPlan CreatePlan() =>
        new(
            [
                new SmartDictionaryRankerOutcome(0, 0, "банк"),
                new SmartDictionaryRankerOutcome(1, 1, "берег")
            ],
            ["a financial institution", "land alongside a river"],
            [0, 1]);

    private static string Fingerprint(
        string sentence,
        int characterIndex,
        SmartDictionaryRankerPlan plan,
        GlossBertRankerProvider provider = GlossBertRankerProvider.Cpu,
        string modelIdentity = "model-v1",
        string dictionaryVersion = "dictionary-v1",
        bool enrichmentEnabled = SmartDictionaryRankerPlan.EnrichmentEnabled) =>
        GlossBertDecisionCache.CreateFingerprint(
            sentence,
            Target,
            characterIndex,
            plan,
            provider,
            modelIdentity,
            dictionaryVersion,
            RankerAbstention.CacheTag,
            enrichmentEnabled);

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
