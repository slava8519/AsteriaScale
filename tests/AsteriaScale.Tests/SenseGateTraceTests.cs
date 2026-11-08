using System.Text.Json;
using AsteriaScale.Services;

internal static class SenseGateTraceTests
{
    internal static async Task RunAsync(string tempRoot)
    {
        string root = Path.Combine(tempRoot, "sense-gate-trace");
        Directory.CreateDirectory(root);
        VerifyParser(root);
        await VerifyWrapperForwardsUnchangedAsync();
        VerifyCandidateProjection();
        await VerifyWriterAsync(root);
    }

    private static void VerifyParser(string root)
    {
        string dataset = Path.Combine(root, "set.jsonl");
        string output = Path.Combine(root, "out.jsonl");
        string report = Path.Combine(root, "report.json");
        string trace = Path.Combine(root, "trace.jsonl");
        string[] Tail(string profile, params string[] extra) =>
        [
            "--benchmark-only", "--translation-suite", dataset, root,
            "--system-id", "trace", "--profile", profile,
            "--phrase-backend", "vulkan", "--ranker-cpu-model", "consec",
            "--translation-output", output, "--translation-report", report,
            .. extra
        ];

        TranslationSuiteOptions parsed = TranslationSuiteOptions.Parse(
            Tail("smart", "--sense-gate-trace", trace),
            1);
        Check(parsed.SenseGateTracePath == Path.GetFullPath(trace),
            "The smart profile lost the sense-gate trace path.");
        Check(TranslationSuiteOptions.Parse(Tail("smart"), 1).SenseGateTracePath is null,
            "A run without the option must not trace.");

        (string Name, string[] Arguments)[] invalid =
        [
            ("trace on direct", Tail("direct", "--sense-gate-trace", trace)),
            ("trace without jsonl", Tail("smart", "--sense-gate-trace",
                Path.Combine(root, "trace.json"))),
            ("trace overwriting output", Tail("smart", "--sense-gate-trace", output)),
            ("duplicate trace", Tail("smart",
                "--sense-gate-trace", trace, "--sense-gate-trace", trace))
        ];
        foreach ((string name, string[] arguments) in invalid)
        {
            ExpectThrows<InvalidOperationException>(
                () => TranslationSuiteOptions.Parse(arguments, 1),
                $"The suite parser accepted {name}.");
        }
    }

    private static async Task VerifyWrapperForwardsUnchangedAsync()
    {
        var inner = new FixedTranslator(new SmartPhraseTranslationResult(
            "Мы отдыхали у берега.",
            TranslateGemmaTranslationService.ModelIdentity,
            ElapsedMilliseconds: 7,
            GenerationRoute: SmartPhraseGenerationRoute.SenseConditioned));
        var wrapper = new SenseGateTraceTranslator(inner);
        SmartPhraseTranslationRequest request = CreateRequest("We rested on the bank.");

        SmartPhraseTranslationResult forwarded =
            await wrapper.TranslateAsync(request, CancellationToken.None);
        Check(ReferenceEquals(forwarded, inner.Result) &&
              ReferenceEquals(wrapper.Request, request) &&
              ReferenceEquals(wrapper.Result, inner.Result) &&
              wrapper.Calls == 1 && inner.Calls == 1 &&
              wrapper.CallFinishedTimestamp >= wrapper.CallStartedTimestamp &&
              wrapper.CallStartedTimestamp > 0,
            "The trace wrapper changed or failed to record the production call.");

        wrapper.Reset();
        Check(wrapper.Request is null && wrapper.Result is null && wrapper.Calls == 0,
            "Reset leaked the previous case into the next trace line.");
    }

    private static void VerifyCandidateProjection()
    {
        SmartPhraseTranslationRequest request =
            CreateRequest("We rested on the bank after sunset.");
        SenseGateTraceCandidate conflict = SenseGateTraceCandidate.From(
            SensePreservationGate.Evaluate(
                request,
                "Мы отдыхали в банке после заката.",
                request.ModelIdentity,
                SensePreservationCandidateKind.Direct));
        Check(conflict.Kind == SensePreservationCandidateKind.Direct &&
              conflict.DisplayAccepted &&
              conflict.RequiresTargetedRepair &&
              conflict.Sources is [{ Source: "bank", IsPrimary: true,
                  State: NeuralSenseFidelityState.Conflict }],
            "The trace misreported a primary-sense conflict in candidate A.");

        SenseGateTraceCandidate satisfied = SenseGateTraceCandidate.From(
            SensePreservationGate.Evaluate(
                request,
                "Мы отдыхали у берега после заката.",
                request.ModelIdentity,
                SensePreservationCandidateKind.Direct));
        Check(!satisfied.RequiresTargetedRepair &&
              satisfied.Sources is [{ State: NeuralSenseFidelityState.Satisfied }],
            "The trace misreported a satisfied primary sense in candidate A.");
    }

    private static async Task VerifyWriterAsync(string root)
    {
        string path = Path.Combine(root, "nested", "trace.jsonl");
        SmartPhraseTranslationRequest request =
            CreateRequest("We rested on the bank after sunset.");
        SenseGateTraceCandidate direct = SenseGateTraceCandidate.From(
            SensePreservationGate.Evaluate(
                request,
                "Мы отдыхали в банке после заката.",
                request.ModelIdentity,
                SensePreservationCandidateKind.Direct));
        await using (var writer = new SenseGateTraceWriter(path))
        {
            await writer.WriteAsync(new SenseGateTraceEntry(
                SenseGateTraceEntry.CurrentSchemaVersion,
                "case-1",
                CardMilliseconds: 120,
                ScaffoldMilliseconds: 340,
                GateMilliseconds: 6100,
                DirectReplayMilliseconds: 1400,
                TranslatorCalls: 1,
                SenseHintCount: 1,
                SmartPhraseGenerationRoute.SenseConditioned,
                "Мы отдыхали у берега после заката.",
                direct,
                DirectReplayError: null));
        }

        string[] lines = await File.ReadAllLinesAsync(path);
        Check(lines.Length == 1, "The trace writer did not emit exactly one line per case.");
        using JsonDocument document = JsonDocument.Parse(lines[0]);
        JsonElement line = document.RootElement;
        Check(line.GetProperty("finalRoute").GetString() == "senseConditioned" &&
              line.GetProperty("direct").GetProperty("kind").GetString() == "direct" &&
              line.GetProperty("direct").GetProperty("sources")[0]
                  .GetProperty("state").GetString() == "conflict" &&
              line.GetProperty("direct").GetProperty("sources")[0]
                  .GetProperty("isPrimary").GetBoolean() &&
              line.GetProperty("gateMilliseconds").GetInt64() == 6100 &&
              lines[0].Contains("берега", StringComparison.Ordinal),
            "The trace line lost a field the offline policy analysis reads.");
    }

    private static SmartPhraseTranslationRequest CreateRequest(string current) =>
        new(
            current,
            current,
            SmartPhraseIdiomRewriter.NoTransformIdentity,
            Array.Empty<string>(),
            TranslateGemmaTranslationService.ModelIdentity,
            TranslateGemmaTranslationService.PromptIdentity)
        {
            SenseHints =
            [
                new ScaffoldSenseHint(
                    "bank",
                    "land beside a river",
                    "берег",
                    ["банк"],
                    0.93)
                {
                    IsPrimary = true
                }
            ]
        };

    private sealed class FixedTranslator(SmartPhraseTranslationResult result)
        : ISmartPhraseTranslator
    {
        internal SmartPhraseTranslationResult Result { get; } = result;

        internal int Calls { get; private set; }

        public Task<SmartPhraseTranslationResult> TranslateAsync(
            SmartPhraseTranslationRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }

    private static void ExpectThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
