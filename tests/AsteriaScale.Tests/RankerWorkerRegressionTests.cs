using AsteriaScale.RankerProtocol;
using AsteriaScale.RankerWorker;

internal static class RankerWorkerRegressionTests
{
    public static Task RunAsync()
    {
        VerifyNpuNeverUsesCpuPrefilter();
        VerifyMissingGlossFailsOpen();
        VerifyClosestTargetOccurrenceIsMarked();
        VerifyQuotedTargetMarker();
        VerifyCpuModelOptionIsFailClosed();
        VerifyPinnedManifestIntegrityIsFailClosed();
        VerifyMultiClassPositiveProbability();
        return Task.CompletedTask;
    }

    private static void VerifyQuotedTargetMarker()
    {
        const string context =
            "He sat by the bank, then visited the bank for a loan.";
        int second = context.LastIndexOf("bank", StringComparison.Ordinal);
        Check(RankerTargetAnchor.TryBuildMarkedContext(
            context,
            "bank",
            second + 2,
            out string marked),
            "The worker could not resolve the exact hovered target occurrence.");
        Check(
            marked ==
                "He sat by the bank, then visited the \"bank\" for a loan.",
            "The worker did not quote exactly the hovered target occurrence.");
    }

    private static void VerifyPinnedManifestIntegrityIsFailClosed()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "AsteriaRankerManifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "model-manifest.json");
        try
        {
            byte[] expected = System.Text.Encoding.UTF8.GetBytes(
                "{\"modelFile\":\"candidate.onnx\"}");
            string sha256 = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(expected))
                .ToLowerInvariant();
            File.WriteAllBytes(path, expected);
            byte[] authenticated = RankerArtifactIntegrity.ReadAllBytesAndValidate(
                path,
                expected.LongLength,
                sha256,
                "manifest_integrity",
                "test manifest");
            Check(authenticated.SequenceEqual(expected),
                "Manifest validation did not return the authenticated bytes.");

            using (RankerArtifactReadLease lease =
                   RankerArtifactIntegrity.AcquireReadLeaseAndValidate(
                       path,
                       expected.LongLength,
                       sha256,
                       "manifest_integrity",
                       "test manifest"))
            {
                Check(lease.ReadAllBytes().SequenceEqual(expected),
                    "The integrity lease did not retain the authenticated bytes.");
#if WINDOWS
                bool writeBlocked = false;
                try
                {
                    using FileStream _ = new(
                        path,
                        FileMode.Open,
                        FileAccess.Write,
                        FileShare.ReadWrite | FileShare.Delete);
                }
                catch (IOException)
                {
                    writeBlocked = true;
                }

                Check(writeBlocked,
                    "The authenticated artifact lease allowed a concurrent writer.");

                string movedPath = path + ".moved";
                bool moveBlocked = false;
                try
                {
                    File.Move(path, movedPath);
                    File.Move(movedPath, path);
                }
                catch (IOException)
                {
                    moveBlocked = true;
                }
                catch (UnauthorizedAccessException)
                {
                    moveBlocked = true;
                }

                Check(moveBlocked,
                    "The authenticated artifact lease allowed a concurrent replacement.");
#else
                // .NET maps FileShare to an advisory flock(2) on macOS. The part
                // that holds is asserted: an exclusive opener is refused while the
                // lease is held. Writers that do not ask for a lock, renames and
                // deletes are not blocked, so there the artifact is validated at
                // load (RankerArtifactIntegrity.cs, worker README).
                bool exclusiveOpenBlocked = false;
                try
                {
                    using FileStream _ = new(
                        path,
                        FileMode.Open,
                        FileAccess.Write,
                        FileShare.None);
                }
                catch (IOException)
                {
                    exclusiveOpenBlocked = true;
                }

                Check(exclusiveOpenBlocked,
                    "The authenticated artifact lease did not refuse an exclusive opener.");
                MacSmoke.Part(
                    "lease-exclusion",
                    "Ranker worker fail-open and target anchoring",
                    "macOS FileShare is an advisory flock; writers, renames and deletes by other " +
                    "processes are not blocked, so artifacts are validated at load");
#endif
            }

            expected[^2] = expected[^2] == (byte)'x' ? (byte)'y' : (byte)'x';
            File.WriteAllBytes(path, expected);
            try
            {
                _ = RankerArtifactIntegrity.ReadAllBytesAndValidate(
                    path,
                    expected.LongLength,
                    sha256,
                    "manifest_integrity",
                    "test manifest");
                throw new InvalidOperationException(
                    "A same-length tampered manifest passed its SHA-256 pin.");
            }
            catch (RankerException ex) when (ex.ErrorCode == "manifest_integrity")
            {
                // Integrity must fail before any manifest field can be trusted.
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void VerifyCpuModelOptionIsFailClosed()
    {
        Check(
            WorkerOptions.TryParse(
                ["--cpu-model", "consec", "--provider", "cpu"],
                out WorkerOptions? consec,
                out string? error) &&
            error is null &&
            consec is
            {
                Provider: RankerProvider.Cpu,
                CpuModel: CpuRankerModel.Consec
            },
            "The explicit ConSeC CPU profile was not parsed.");

        Check(
            !WorkerOptions.TryParse(
                ["--cpu-model", "consec", "--provider", "npu"],
                out _,
                out error) &&
            error?.Contains("CPU-only", StringComparison.Ordinal) == true,
            "An NPU + ConSeC combination was not rejected independent of argument order.");

        // SANDWiCH and GlossDeBERTa were removed; a stale launcher that still
        // asks for them must fail loudly instead of running another model.
        foreach (string removedModel in new[] { "sandwich", "deberta", "SANDWiCH" })
        {
            Check(
                !WorkerOptions.TryParse(
                    ["--provider", "cpu", "--cpu-model", removedModel],
                    out _,
                    out error) &&
                error?.Contains(
                    $"Unsupported CPU model '{removedModel}'",
                    StringComparison.Ordinal) == true &&
                error.Contains("glossbert or consec", StringComparison.Ordinal),
                $"The removed CPU model '{removedModel}' was not rejected clearly.");
        }

        Check(
            WorkerOptions.TryParse([], out WorkerOptions? defaults, out error) &&
            error is null &&
            defaults is
            {
                Provider: RankerProvider.Npu,
                CpuModel: CpuRankerModel.GlossBert
            },
            "The worker's existing NPU + GlossBERT default changed.");

        foreach (string[] duplicate in new[]
                 {
                     new[] { "--provider", "npu", "--provider", "cpu" },
                     new[] { "--cpu-model", "glossbert", "--cpu-model", "consec" },
                     new[] { "--model-directory", ".", "--model-directory", "." },
                     new[] { "--smoke", "--smoke" }
                 })
        {
            Check(
                !WorkerOptions.TryParse(duplicate, out _, out error) &&
                error?.Contains("Duplicate argument", StringComparison.Ordinal) == true,
                $"A duplicate worker option was accepted: {string.Join(' ', duplicate)}");
        }
    }

    private static void VerifyMultiClassPositiveProbability()
    {
        float[] scores = RankerOutputProbabilities.ExtractPositiveClass(
            [0.0f, 3.0f, 0.0f, 1_000.0f, 999.0f, 998.0f],
            batchSize: 2,
            outputClassCount: 3,
            positiveLabelIndex: 1);
        Check(
            Math.Abs(scores[0] - 0.909443f) < 0.00001f,
            "The three-class positive probability was not a full softmax.");
        Check(
            Math.Abs(scores[1] - 0.244728f) < 0.00001f,
            "The three-class softmax was not numerically stable for large logits.");

        scores = RankerOutputProbabilities.ExtractPositiveClass(
            [-2.0f, 3.0f],
            batchSize: 1,
            outputClassCount: 2,
            positiveLabelIndex: 1);
        Check(
            Math.Abs(scores[0] - 0.993307f) < 0.00001f,
            "The generalized softmax changed GlossBERT's binary probability.");
    }

    private static void VerifyNpuNeverUsesCpuPrefilter()
    {
        Check(!RankerSelectionLogic.ShouldApplyBiEncoder(cpuProvider: false, 64),
            "An NPU selection was allowed to use the CPU-only bi-encoder prefilter.");
        Check(RankerSelectionLogic.ShouldApplyBiEncoder(cpuProvider: true, 17),
            "A large CPU inventory did not enable its pinned bi-encoder prefilter.");
        Check(!RankerSelectionLogic.ShouldApplyBiEncoder(cpuProvider: true, 16),
            "The CPU prefilter engaged below its calibrated inventory threshold.");
    }

    private static void VerifyMissingGlossFailsOpen()
    {
        int[] groupIds = Enumerable.Range(0, 17).ToArray();
        double?[] similarities = Enumerable.Range(0, 17)
            .Select(index => index < 12 ? (double?)null : index / 100.0)
            .ToArray();

        HashSet<int>? kept = RankerSelectionLogic.SelectPrefilterGroups(
            groupIds,
            similarities);
        Check(kept is null,
            "Missing gloss hashes pruned covered groups instead of restoring the full inventory.");

        double?[] complete = Enumerable.Range(0, 17)
            .Select(index => (double?)index)
            .ToArray();
        kept = RankerSelectionLogic.SelectPrefilterGroups(groupIds, complete);
        Check(kept is { Count: 12 } && kept.Contains(16) && !kept.Contains(0),
            "A fully covered inventory did not retain the twelve highest-scoring groups.");

        complete[0] = double.NaN;
        Check(RankerSelectionLogic.SelectPrefilterGroups(groupIds, complete) is null,
            "A NaN embedding similarity pruned the inventory instead of failing open.");
        complete[0] = double.PositiveInfinity;
        Check(RankerSelectionLogic.SelectPrefilterGroups(groupIds, complete) is null,
            "An infinite embedding similarity pruned the inventory instead of failing open.");
    }

    private static void VerifyClosestTargetOccurrenceIsMarked()
    {
        const string context =
            "He sat by the bank, then visited the bank for a loan.";
        int second = context.LastIndexOf("bank", StringComparison.Ordinal);
        int selected = RankerSelectionLogic.FindClosestTargetOccurrence(
            context,
            "bank",
            second + 2);
        Check(selected == second,
            "The ranker marked the first repeated target instead of the hovered occurrence.");

        const string substringContext = "The answer he gave was brief.";
        selected = RankerSelectionLogic.FindClosestTargetOccurrence(
            substringContext,
            "he",
            1);
        Check(selected == substringContext.IndexOf("he", 4, StringComparison.Ordinal),
            "The ranker accepted a target substring inside a different English token.");

        const string combiningContext = "The cafe\u0301 closed; the cafe opened.";
        int plainCafe = combiningContext.LastIndexOf("cafe", StringComparison.Ordinal);
        selected = RankerSelectionLogic.FindClosestTargetOccurrence(
            combiningContext,
            "cafe",
            4);
        Check(selected == plainCafe,
            "A combining mark was treated as a token boundary for target anchoring.");

        const string accentedContext = "The na\u00EFve answer differed.";
        selected = RankerSelectionLogic.FindClosestTargetOccurrence(
            accentedContext,
            "ve",
            accentedContext.IndexOf("ve", StringComparison.Ordinal));
        Check(selected == -1,
            "A target substring inside a Unicode English token was accepted.");

        const string astralContext = "𐐀bank and bank𐐀";
        selected = RankerSelectionLogic.FindClosestTargetOccurrence(
            astralContext,
            "bank",
            3);
        Check(selected == -1,
            "An astral Unicode letter was treated as a token boundary.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
