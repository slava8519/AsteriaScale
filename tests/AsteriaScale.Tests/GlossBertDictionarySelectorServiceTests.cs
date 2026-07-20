using System.Text.Json;
using AsteriaScale.RankerProtocol;
using AsteriaScale.Services;

internal static class GlossBertDictionarySelectorServiceTests
{
    public static async Task RunAsync(string tempRoot)
    {
        VerifyProtocolV2Shape();
        VerifyAcceptedStableGroupId();
        VerifyNormalAbstention();
        VerifyFailClosedIdentityChecks();
        VerifySingleFileCompanionDirectoryResolution(tempRoot);
        VerifyCompanionDirectoryFallback(tempRoot);
        await VerifyParallelCandidateValidationAsync(tempRoot);
    }

    private static void VerifyProtocolV2Shape()
    {
        RankerRequest request = new()
        {
            Command = RankerProtocolV2.Select,
            RequestId = "shape",
            Context = "He sat on the bank.",
            Target = "bank",
            CharacterIndex = 14,
            SourceLanguage = "English",
            Candidates = ["bank: financial institution", "bank: land beside water"],
            CandidateGroupIds = [17, 29]
        };

        string json = JsonSerializer.Serialize(request, RankerProtocolV2.JsonOptions);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Check(root.GetProperty("version").GetString() == "2",
            "Ranker request did not advertise protocol v2.");
        Check(root.GetProperty("candidateGroupIds")[1].GetInt32() == 29,
            "Stable candidate group ids were not serialized parallel to glosses.");

        RankerResponse? response = JsonSerializer.Deserialize<RankerResponse>(
            """
            {"version":"2","requestId":"shape","ok":true,"index":null,
             "abstained":true,"margin":0.12,"score":1.4,"confidence":0.51,
             "provider":"CPUExecutionProvider","device":"CPU",
             "modelVersion":"GlossBERT:0cc3b83af5496e27ebcc95ef0cf37ea0a9281a7a:qint8"}
            """,
            RankerProtocolV2.JsonOptions);
        Check(response is { Abstained: true, Index: null, Margin: 0.12 },
            "Ranker v2 abstention diagnostics did not deserialize.");
    }

    private static void VerifyAcceptedStableGroupId()
    {
        RankerResponse response = Success(
            requestId: "accepted",
            provider: GlossBertRankerProvider.Cpu) with
        {
            Index = 42,
            Margin = 0.34,
            Score = 2.8,
            Confidence = 0.86
        };

        GlossBertDictionarySelection selection =
            GlossBertDictionarySelectorService.ValidateSelectionResponseForTests(
                response,
                "accepted",
                GlossBertRankerProvider.Cpu,
                [7, 42]);
        Check(selection is
              {
                  Index: 42,
                  SelectedGroupId: 42,
                  Abstained: false,
                  RequestedProvider: GlossBertRankerProvider.Cpu
              },
            "The host treated a stable group id as a positional candidate index.");
    }

    private static void VerifyNormalAbstention()
    {
        RankerResponse response = Success(
            requestId: "abstain",
            provider: GlossBertRankerProvider.Npu) with
        {
            Abstained = true,
            Index = null,
            Margin = 0.08,
            Score = 1.1,
            Confidence = 0.54
        };

        GlossBertDictionarySelection selection =
            GlossBertDictionarySelectorService.ValidateSelectionResponseForTests(
                response,
                "abstain",
                GlossBertRankerProvider.Npu,
                [3, 9]);
        Check(selection.Abstained && selection.Index is null,
            "A calibrated abstention was incorrectly converted into a failure.");
    }

    private static void VerifyFailClosedIdentityChecks()
    {
        ExpectProtocolViolation(
            Success("id", GlossBertRankerProvider.Cpu) with { Index = 99 },
            "id",
            GlossBertRankerProvider.Cpu,
            [1, 2],
            "An unknown outcome group id was accepted.");
        ExpectProtocolViolation(
            Success("id", GlossBertRankerProvider.Cpu) with
            {
                Provider = GlossBertDictionarySelectorService.NpuExecutionProvider,
                Device = "NPU",
                Index = 1
            },
            "id",
            GlossBertRankerProvider.Cpu,
            [1, 2],
            "A hidden CPU/NPU provider substitution was accepted.");
        ExpectProtocolViolation(
            Success("id", GlossBertRankerProvider.Cpu) with
            {
                ModelVersion = "another-model:1",
                Index = 1
            },
            "id",
            GlossBertRankerProvider.Cpu,
            [1, 2],
            "An unexpected model revision was accepted.");
        ExpectProtocolViolation(
            Success("id", GlossBertRankerProvider.Cpu) with
            {
                ModelVersion =
                    GlossBertDictionarySelectorService.ExpectedModelVersionPrefix +
                    ":qint8-dynamic",
                Index = 1
            },
            "id",
            GlossBertRankerProvider.Cpu,
            [1, 2],
            "A stale CPU artifact was accepted under the current cache identity.");
        ExpectProtocolViolation(
            Success("wrong-id", GlossBertRankerProvider.Cpu) with { Index = 1 },
            "id",
            GlossBertRankerProvider.Cpu,
            [1, 2],
            "A response from another JSONL transaction was accepted.");
        ExpectProtocolViolation(
            Success("id", GlossBertRankerProvider.Cpu) with
            {
                Version = "1",
                Index = 1
            },
            "id",
            GlossBertRankerProvider.Cpu,
            [1, 2],
            "A stale protocol response was accepted.");
        ExpectProtocolViolation(
            Success("id", GlossBertRankerProvider.Cpu) with
            {
                Abstained = true,
                Index = 1
            },
            "id",
            GlossBertRankerProvider.Cpu,
            [1, 2],
            "A contradictory abstained response was accepted.");
    }

    private static void VerifySingleFileCompanionDirectoryResolution(
        string tempRoot)
    {
        string root = Path.Combine(tempRoot, "single-file-companion");
        string physicalDirectory = Path.Combine(root, "installed");
        string extractionDirectory = Path.Combine(
            root,
            "bundle-extraction",
            "hash");
        string worker = Path.Combine(
            physicalDirectory,
            "Ranker",
            "AsteriaScale.RankerWorker.exe");

        Directory.CreateDirectory(Path.GetDirectoryName(worker)!);
        Directory.CreateDirectory(extractionDirectory);
        File.WriteAllBytes(worker, []);

        string resolved =
            GlossBertDictionarySelectorService.ResolveCompanionBaseDirectory(
                Path.Combine(physicalDirectory, "AsteriaScale.exe"),
                extractionDirectory);

        Check(
            Path.GetFullPath(resolved) ==
            Path.GetFullPath(physicalDirectory),
            "A single-file host resolved companions relative to the bundle " +
            "extraction directory instead of the physical executable.");
        Check(
            File.Exists(Path.Combine(
                resolved,
                "Ranker",
                "AsteriaScale.RankerWorker.exe")),
            "The resolved single-file companion directory does not contain " +
            "the bundled ranker worker.");
        Check(
            !File.Exists(Path.Combine(
                extractionDirectory,
                "Ranker",
                "AsteriaScale.RankerWorker.exe")),
            "The regression fixture unexpectedly placed the worker in the " +
            "bundle extraction directory.");
    }

    private static void VerifyCompanionDirectoryFallback(string tempRoot)
    {
        string appContextDirectory = Path.Combine(
            tempRoot,
            "non-single-file-app-context");

        string resolved =
            GlossBertDictionarySelectorService.ResolveCompanionBaseDirectory(
                processPath: null,
                appContextDirectory);

        Check(
            Path.GetFullPath(resolved) ==
            Path.GetFullPath(appContextDirectory),
            "Companion resolution did not fall back to AppContext.BaseDirectory " +
            "when the physical process path was unavailable.");
    }

    private static async Task VerifyParallelCandidateValidationAsync(string tempRoot)
    {
        string root = Path.Combine(tempRoot, "missing-ranker-is-intentional");
        await using GlossBertDictionarySelectorService service =
            new(root, TimeSpan.FromMilliseconds(50));
        try
        {
            _ = await service.SelectAsync(
                "He sat on the bank.",
                "bank",
                14,
                ["bank: financial institution", "bank: land beside water"],
                [7],
                "English",
                GlossBertRankerProvider.Cpu);
            throw new InvalidOperationException(
                "Mismatched candidate and group arrays were accepted.");
        }
        catch (ArgumentException)
        {
            // Expected before any attempt to resolve or start the worker.
        }
    }

    private static RankerResponse Success(
        string requestId,
        GlossBertRankerProvider provider) => new()
    {
        RequestId = requestId,
        Ok = true,
        Provider = provider == GlossBertRankerProvider.Npu
            ? GlossBertDictionarySelectorService.NpuExecutionProvider
            : GlossBertDictionarySelectorService.CpuExecutionProvider,
        Device = provider == GlossBertRankerProvider.Npu ? "NPU" : "CPU",
        ModelVersion = GlossBertDictionarySelectorService.GetModelCacheId(provider)
    };

    private static void ExpectProtocolViolation(
        RankerResponse response,
        string requestId,
        GlossBertRankerProvider provider,
        IReadOnlyCollection<int> groupIds,
        string message)
    {
        try
        {
            _ = GlossBertDictionarySelectorService.ValidateSelectionResponseForTests(
                response,
                requestId,
                provider,
                groupIds);
        }
        catch (GlossBertRankerException ex) when (
            ex.ErrorCode == "protocol_violation")
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
