using System.Text.Json;
using System.Diagnostics;
using AsteriaScale.RankerProtocol;
using AsteriaScale.Services;

internal static class GlossBertDictionarySelectorServiceTests
{
    public static async Task RunAsync(string tempRoot)
    {
        VerifyProtocolV2Shape();
        VerifyAcceptedStableGroupId();
        VerifyNormalAbstention();
        VerifyPipelineIdentity();
        VerifySwitchableCpuModelIdentity();
        VerifyConsecCpuModelIdentity();
        VerifySwitchableCpuModelArguments();
        VerifyFailClosedIdentityChecks();
        VerifySingleFileCompanionDirectoryResolution(tempRoot);
        VerifyCompanionDirectoryFallback(tempRoot);
        await VerifyDefaultWorkerLocationAsync(tempRoot);
        await VerifyHostSuppliedWorkerLocationAsync(tempRoot);
        VerifyRelativeWorkerLocationRejected(tempRoot);
        await VerifyParallelCandidateValidationAsync(tempRoot);
        await VerifyProfileSwitchAfterResponseDoesNotInvalidateSelectionAsync(tempRoot);
        await VerifyResponseDeadlineKillsWorkerAsync(tempRoot);
        await VerifyLastErrorCodeAsync(tempRoot);
    }

    private static void VerifyProtocolV2Shape()
    {
        const string repeatedContext = "          bank bank";
        RankerRequest anchoredRequest =
            GlossBertDictionarySelectorService.CreateSelectionRequestForTests(
                repeatedContext,
                "bank",
                repeatedContext.LastIndexOf("bank", StringComparison.Ordinal),
                ["банк"],
                [0],
                "English");
        Check(anchoredRequest.Context == repeatedContext &&
              anchoredRequest.CharacterIndex == repeatedContext.LastIndexOf(
                  "bank",
                  StringComparison.Ordinal),
            "Request normalization shifted the cursor anchor in a repeated target.");

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
        Check(root.GetProperty("version").GetString() == RankerProtocolV2.Version,
            "Ranker request did not advertise the current protocol version.");
        Check(root.GetProperty("candidateGroupIds")[1].GetInt32() == 29,
            "Stable candidate group ids were not serialized parallel to glosses.");

        RankerResponse? response = JsonSerializer.Deserialize<RankerResponse>(
            """
            {"version":"4","requestId":"shape","ok":true,"index":null,
             "abstained":true,"margin":0.12,"score":1.4,"confidence":0.51,
             "provider":"CPUExecutionProvider","device":"CPU","pipeline":"bi+cross",
             "modelVersion":"GlossBERT:0cc3b83af5496e27ebcc95ef0cf37ea0a9281a7a:qint8"}
            """,
            RankerProtocolV2.JsonOptions);
        Check(response is { Abstained: true, Index: null, Margin: 0.12, Pipeline: "bi+cross" },
            "Ranker v4 abstention diagnostics did not deserialize.");
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

    private static void VerifyPipelineIdentity()
    {
        string biModelVersion =
            GlossBertDictionarySelectorService.CpuModelCacheId +
            ":" + GlossBertDictionarySelectorService.CpuEmbeddingBundleCacheTag;
        Check(SmartHoverPipeline.ExpectedRankingPipeline(
                GlossBertRankerProvider.Cpu,
                17,
                biModelVersion) == "bi+cross",
            "The cache identity did not recognize the pinned CPU bi-encoder bundle.");
        Check(SmartHoverPipeline.ExpectedRankingPipeline(
                GlossBertRankerProvider.Npu,
                64,
                biModelVersion) == "cross",
            "An NPU cache identity advertised the CPU-only prefilter pipeline.");
        Check(SmartHoverPipeline.ExpectedRankingPipeline(
                GlossBertRankerProvider.Cpu,
                17,
                GlossBertDictionarySelectorService.CpuModelCacheId +
                ":pipeline-cross") == "cross",
            "A worker without the optional bundle advertised bi+cross caching.");
        Check(SmartHoverPipeline.ExpectedRankingPipeline(
                GlossBertRankerProvider.Cpu,
                2,
                GlossBertDictionarySelectorService.ConsecCpuModelCacheId +
                ":pipeline-consec-independent") == "consec-independent",
            "The cache identity did not recognize the pinned ConSeC pipeline.");
    }

    private static void VerifyConsecCpuModelIdentity()
    {
        string artifact = GlossBertDictionarySelectorService.ConsecCpuModelCacheId;
        RankerResponse response = Success(
            requestId: "consec",
            provider: GlossBertRankerProvider.Cpu) with
        {
            ModelVersion = artifact + ":pipeline-consec-independent",
            Pipeline = "consec-independent",
            Index = 19,
            Margin = 0.41,
            Score = 0.83,
            Confidence = 0.83
        };

        GlossBertDictionarySelection selection =
            GlossBertDictionarySelectorService.ValidateSelectionResponseForTests(
                response,
                "consec",
                GlossBertRankerProvider.Cpu,
                [12, 19],
                RankerCpuModelProfile.Consec);
        Check(selection is { Index: 19, Pipeline: "consec-independent" },
            "The host rejected the exact pinned ConSeC CPU identity.");

        ExpectProtocolViolation(
            response with { Pipeline = "cross" },
            "consec",
            GlossBertRankerProvider.Cpu,
            [12, 19],
            "A ConSeC identity was accepted with the cross-encoder pipeline.",
            RankerCpuModelProfile.Consec);
        ExpectProtocolViolation(
            response with
            {
                ModelVersion = artifact.Replace(
                    "d-1257dc55ebec",
                    "d-aaaaaaaaaaaa",
                    StringComparison.Ordinal) + ":pipeline-consec-independent"
            },
            "consec",
            GlossBertRankerProvider.Cpu,
            [12, 19],
            "A format-valid but unpinned ConSeC external-data identity was accepted.",
            RankerCpuModelProfile.Consec);

        try
        {
            GlossBertDictionarySelectorService.ValidateWarmResponseForTests(
                response with
                {
                    RequestId = "npu-consec",
                    Provider = GlossBertDictionarySelectorService.NpuExecutionProvider,
                    Device = "NPU",
                    Index = null,
                    Margin = null,
                    Score = null,
                    Confidence = null
                },
                "npu-consec",
                GlossBertRankerProvider.Npu,
                RankerCpuModelProfile.Consec);
            throw new InvalidOperationException("The host accepted ConSeC on NPU.");
        }
        catch (GlossBertRankerException ex) when (
            ex.ErrorCode == "unsupported_provider_model")
        {
            // The CPU-only profile must fail before transport, never fall back.
        }
    }

    private static void VerifySwitchableCpuModelIdentity()
    {
        // A verdict is valid only for the profile that produced it; accepting
        // it under another profile would serve a foreign answer after a switch.
        RankerResponse consecResponse = Success(
            requestId: "switch",
            provider: GlossBertRankerProvider.Cpu) with
        {
            ModelVersion = GlossBertDictionarySelectorService.ConsecCpuModelCacheId +
                ":pipeline-consec-independent",
            Pipeline = "consec-independent",
            Index = 4,
            Margin = 0.5,
            Score = 0.9,
            Confidence = 0.9
        };
        ExpectProtocolViolation(
            consecResponse,
            "switch",
            GlossBertRankerProvider.Cpu,
            [4, 9],
            "A ConSeC response was accepted for the GlossBERT profile.");
        RankerResponse glossBertResponse = Success(
            requestId: "switch",
            provider: GlossBertRankerProvider.Cpu) with
        {
            Index = 4,
            Margin = 0.5,
            Score = 0.9,
            Confidence = 0.9
        };
        _ = GlossBertDictionarySelectorService.ValidateSelectionResponseForTests(
            glossBertResponse,
            "switch",
            GlossBertRankerProvider.Cpu,
            [4, 9]);
        ExpectProtocolViolation(
            glossBertResponse,
            "switch",
            GlossBertRankerProvider.Cpu,
            [4, 9],
            "A GlossBERT response was accepted for the ConSeC profile.",
            RankerCpuModelProfile.Consec);

        // The worker appends an ONNX metadata version when the graph declares
        // one; the pinned identity must still be recognized behind it.
        RankerResponse onnxTagged = glossBertResponse with
        {
            ModelVersion = GlossBertDictionarySelectorService.CpuModelCacheId +
                ":onnx-17:pipeline-cross"
        };
        GlossBertDictionarySelection onnxSelection =
            GlossBertDictionarySelectorService.ValidateSelectionResponseForTests(
                onnxTagged,
                "switch",
                GlossBertRankerProvider.Cpu,
                [4, 9]);
        Check(onnxSelection.Index == 4,
            "The exact GlossBERT identity rejected its optional ONNX version suffix.");
    }

    private static void VerifySwitchableCpuModelArguments()
    {
        ProcessStartInfo startInfo = new("worker.exe");
        GlossBertDictionarySelectorService.AddWorkerArguments(
            startInfo,
            GlossBertRankerProvider.Cpu,
            RankerCpuModelProfile.GlossBert,
            "C:\\models");
        Check(
            startInfo.ArgumentList.SequenceEqual(
                [
                    "--provider",
                    "cpu",
                    "--cpu-model",
                    "glossbert",
                    "--model-directory",
                    "C:\\models"
                ],
                StringComparer.Ordinal),
            "The host did not pass the default GlossBERT profile to the worker.");

        ProcessStartInfo consecStartInfo = new("worker.exe");
        GlossBertDictionarySelectorService.AddWorkerArguments(
            consecStartInfo,
            GlossBertRankerProvider.Cpu,
            RankerCpuModelProfile.Consec,
            "C:\\models");
        Check(
            consecStartInfo.ArgumentList.SequenceEqual(
                [
                    "--provider",
                    "cpu",
                    "--cpu-model",
                    "consec",
                    "--model-directory",
                    "C:\\models"
                ],
                StringComparer.Ordinal),
            "The host did not pass the ConSeC CPU profile to the worker.");
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
                ModelVersion = GlossBertDictionarySelectorService.CpuModelCacheId,
                Index = 1
            },
            "id",
            GlossBertRankerProvider.Cpu,
            [1, 2],
            "A worker without an explicit pipeline identity was accepted.");
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
        ExpectProtocolViolation(
            Success("id", GlossBertRankerProvider.Cpu) with
            {
                Pipeline = null,
                Index = 1
            },
            "id",
            GlossBertRankerProvider.Cpu,
            [1, 2],
            "A selection without a declared ranking pipeline was accepted.");
        ExpectProtocolViolation(
            Success("id", GlossBertRankerProvider.Npu) with
            {
                Pipeline = "bi+cross",
                Index = 1
            },
            "id",
            GlossBertRankerProvider.Npu,
            [1, 2],
            "An NPU response advertised the CPU-only bi-encoder pipeline.");
        ExpectProtocolViolation(
            Success("id", GlossBertRankerProvider.Cpu) with
            {
                Pipeline = "bi+cross",
                Index = 1
            },
            "id",
            GlossBertRankerProvider.Cpu,
            Enumerable.Range(0, 17).ToArray(),
            "A cross-only CPU artifact advertised the bi-encoder pipeline.");

        string biModelVersion =
            GlossBertDictionarySelectorService.CpuModelCacheId +
            ":" + GlossBertDictionarySelectorService.CpuEmbeddingBundleCacheTag;
        RankerResponse validBi = Success("bi", GlossBertRankerProvider.Cpu) with
        {
            ModelVersion = biModelVersion,
            Pipeline = "bi+cross",
            Index = 1,
            Margin = 0.5,
            Score = 1.0,
            Confidence = 0.7
        };
        GlossBertDictionarySelection biSelection =
            GlossBertDictionarySelectorService.ValidateSelectionResponseForTests(
                validBi,
                "bi",
                GlossBertRankerProvider.Cpu,
                Enumerable.Range(0, 17).ToArray());
        Check(biSelection.Pipeline == "bi+cross",
            "A valid pinned CPU bi-encoder response was rejected.");
        GlossBertDictionarySelection failOpenSelection =
            GlossBertDictionarySelectorService.ValidateSelectionResponseForTests(
                validBi with { Pipeline = "cross" },
                "bi",
                GlossBertRankerProvider.Cpu,
                Enumerable.Range(0, 17).ToArray());
        Check(failOpenSelection.Pipeline == "cross",
            "A valid CPU bi-encoder fail-open response was rejected.");

        ExpectWarmProtocolViolation(
            Success("warm", GlossBertRankerProvider.Cpu) with
            {
                ModelVersion = GlossBertDictionarySelectorService.ExpectedModelVersionPrefix
            },
            "A successful warm-up without pinned artifact identity was accepted.");
        try
        {
            GlossBertDictionarySelectorService.ValidateWarmResponseForTests(
                Success("warm-error", GlossBertRankerProvider.Cpu) with
                {
                    Ok = false,
                    ModelVersion = GlossBertDictionarySelectorService.ExpectedModelVersionPrefix,
                    ErrorCode = "model_load_failed",
                    Error = "fixture"
                },
                "warm-error",
                GlossBertRankerProvider.Cpu);
            throw new InvalidOperationException("A worker warm-up error was accepted.");
        }
        catch (GlossBertRankerException ex) when (ex.ErrorCode == "model_load_failed")
        {
            // Worker loading failures must not be rewritten as identity errors.
        }

        ExpectWarmProtocolViolation(
            Success("warm-invalid-error", GlossBertRankerProvider.Cpu) with
            {
                Ok = false,
                ModelVersion = "unexpected-model",
                ErrorCode = "model_load_failed",
                Error = "fixture"
            },
            "A worker error with an invalid model identity was accepted.");
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

    // The Windows host passes no location; it must keep starting exactly the
    // worker and model folder the selector derived before RankerWorkerLocation.
    private static async Task VerifyDefaultWorkerLocationAsync(string tempRoot)
    {
        // The pre-seam expression from the selector constructor, verbatim.
        static (string Worker, string Models) OldCompanionPaths(string baseDirectory)
        {
            string rankerDirectory = Path.Combine(Path.GetFullPath(baseDirectory), "Ranker");
            return (
                Path.Combine(rankerDirectory, "AsteriaScale.RankerWorker.exe"),
                Path.Combine(rankerDirectory, "Models"));
        }

        string installed = Path.Combine(tempRoot, "worker-location", "installed");
        RankerWorkerLocation beside = RankerWorkerLocation.BesideBaseDirectory(installed);
        Check(
            (beside.WorkerExecutable, beside.ModelDirectory) == OldCompanionPaths(installed),
            "BesideBaseDirectory moved the Windows worker or its model folder.");

        // Benchmarks pass a user-supplied workerBaseDir, which may be relative;
        // it is still resolved against the working directory, as before.
        string relativeBase = Path.Combine("worker-location", "relative-base");
        RankerWorkerLocation relative = RankerWorkerLocation.BesideBaseDirectory(relativeBase);
        Check(
            (relative.WorkerExecutable, relative.ModelDirectory) == OldCompanionPaths(relativeBase) &&
            Path.IsPathFullyQualified(relative.WorkerExecutable),
            "BesideBaseDirectory stopped resolving a relative base directory to a full path.");

        await using GlossBertDictionarySelectorService internalBase = new(
            installed,
            TimeSpan.FromMinutes(1));
        Check(
            (internalBase.WorkerLocation.WorkerExecutable, internalBase.WorkerLocation.ModelDirectory) ==
            OldCompanionPaths(installed),
            "The internal base-directory constructor no longer derives the companion layout.");

        string companionBase = GlossBertDictionarySelectorService.ResolveCompanionBaseDirectory(
            Environment.ProcessPath,
            AppContext.BaseDirectory);
        RankerWorkerLocation current = RankerWorkerLocation.ForCurrentProcess();
        Check(
            (current.WorkerExecutable, current.ModelDirectory) == OldCompanionPaths(companionBase),
            "ForCurrentProcess no longer uses the companion folder of the physical executable.");

        // Constructing a selector starts nothing; only the paths are compared.
        await using GlossBertDictionarySelectorService defaultSelector = new();
        await using GlossBertDictionarySelectorService profileSelector = new(RankerCpuModelProfile.Consec);
        Check(
            (defaultSelector.WorkerLocation.WorkerExecutable, defaultSelector.WorkerLocation.ModelDirectory) ==
                OldCompanionPaths(companionBase) &&
            (profileSelector.WorkerLocation.WorkerExecutable, profileSelector.WorkerLocation.ModelDirectory) ==
                OldCompanionPaths(companionBase) &&
            defaultSelector.CpuModelProfile == RankerCpuModelProfile.GlossBert &&
            profileSelector.CpuModelProfile == RankerCpuModelProfile.Consec,
            "The public selector constructors changed the Windows worker location or profile.");
    }

    // The macOS layout: a worker without .exe inside an app bundle and a model
    // folder elsewhere. Both must reach validation and the command line.
    private static async Task VerifyHostSuppliedWorkerLocationAsync(string tempRoot)
    {
        string root = Path.Combine(tempRoot, "host-supplied-worker-location");
        string worker = Path.Combine(root, "Helpers", "Ranker", "AsteriaScale.RankerWorker");
        string models = Path.Combine(root, "UserModels", "Ranker");
        RankerWorkerLocation location = new(worker, models);
        await using GlossBertDictionarySelectorService service = new(
            location,
            RankerCpuModelProfile.Consec);
        Check(ReferenceEquals(service.WorkerLocation, location),
            "The selector did not keep the host-supplied worker location.");

        ProcessStartInfo startInfo = service.CreateWorkerStartInfo(
            GlossBertRankerProvider.Cpu,
            RankerCpuModelProfile.Consec);
        Check(
            startInfo.FileName == worker &&
            startInfo.WorkingDirectory == Path.GetDirectoryName(worker) &&
            startInfo.ArgumentList.SequenceEqual(
                [
                    "--provider",
                    "cpu",
                    "--cpu-model",
                    "consec",
                    "--model-directory",
                    models
                ],
                StringComparer.Ordinal),
            "A host-supplied worker location did not reach FileName, the working " +
            "directory and --model-directory.");

        // Validation checks the same two paths before anything is started.
        await ExpectWarmUpFailureAsync(
            service,
            "worker_missing",
            worker,
            "A missing host-supplied worker was not reported by its own path.");
        Directory.CreateDirectory(Path.GetDirectoryName(worker)!);
        File.WriteAllBytes(worker, []);
        await ExpectWarmUpFailureAsync(
            service,
            "model_directory_missing",
            models,
            "A missing host-supplied model folder was not reported by its own path.");
        Check(!service.IsWorkerRunning && !Directory.Exists(models),
            "Validating a host-supplied location started a worker or created its model folder.");
    }

    private static async Task ExpectWarmUpFailureAsync(
        GlossBertDictionarySelectorService service,
        string errorCode,
        string path,
        string message)
    {
        try
        {
            await service.WarmUpAsync(GlossBertRankerProvider.Cpu);
        }
        catch (GlossBertRankerException ex) when (
            ex.ErrorCode == errorCode &&
            ex.Message.Contains(path, StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    // A relative path would be resolved against whatever the working directory
    // is when the worker starts; the location refuses it up front.
    private static void VerifyRelativeWorkerLocationRejected(string tempRoot)
    {
        string worker = Path.Combine(tempRoot, "relative-worker-location", "AsteriaScale.RankerWorker");
        string models = Path.Combine(tempRoot, "relative-worker-location", "Models");
        string relative = Path.Combine("Ranker", "Models");
        (string Worker, string Models, string Parameter)[] cases =
        [
            (relative, models, "workerExecutable"),
            (worker, relative, "modelDirectory"),
            ("  ", models, "workerExecutable"),
            (worker, "", "modelDirectory")
        ];
        foreach ((string caseWorker, string caseModels, string parameter) in cases)
        {
            try
            {
                _ = new RankerWorkerLocation(caseWorker, caseModels);
            }
            catch (ArgumentException ex) when (ex.ParamName == parameter)
            {
                continue;
            }

            throw new InvalidOperationException(
                $"RankerWorkerLocation accepted '{caseWorker}' / '{caseModels}'.");
        }
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

    private static async Task VerifyProfileSwitchAfterResponseDoesNotInvalidateSelectionAsync(
        string tempRoot)
    {
        string root = Path.Combine(tempRoot, "ranker-profile-response-race");
        string rankerDirectory = Path.Combine(root, "Ranker");
        Directory.CreateDirectory(Path.Combine(rankerDirectory, "Models"));
        File.WriteAllBytes(
            Path.Combine(rankerDirectory, "AsteriaScale.RankerWorker.exe"),
            []);

#if WINDOWS
        string powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        Check(File.Exists(powershell),
            "The profile-race fixture requires the inbox Windows PowerShell executable.");
#endif

        const int selectedGroupId = 7;
#if WINDOWS
        string workerCommand =
            "$model = '" +
            GlossBertDictionarySelectorService.CpuModelCacheId +
            ":pipeline-cross'; " +
            "while (($line = [Console]::In.ReadLine()) -ne $null) { " +
            "$request = $line | ConvertFrom-Json; " +
            "$response = [ordered]@{ " +
            "version = '" + RankerProtocolV2.Version + "'; " +
            "requestId = [string]$request.requestId; ok = $true; " +
            "index = $null; abstained = $false; margin = $null; " +
            "score = $null; confidence = $null; " +
            "provider = '" +
            GlossBertDictionarySelectorService.CpuExecutionProvider +
            "'; device = 'CPU'; modelVersion = $model; pipeline = 'cross'; " +
            "errorCode = $null; error = $null }; " +
            "if ($request.command -eq 'select') { " +
            "$response.index = " + selectedGroupId + "; " +
            "$response.margin = 0.5; $response.score = 0.9; " +
            "$response.confidence = 0.9 }; " +
            "[Console]::Out.WriteLine(($response | ConvertTo-Json -Compress)); " +
            "[Console]::Out.Flush(); " +
            "if ($request.command -eq 'shutdown') { break } }";
#else
        // The same JSONL contract as the PowerShell fake above. python3 is
        // already a prerequisite of the macOS subset (atomic_output and the
        // translation-benchmark scorer), and -I keeps the fake isolated from
        // the working directory and user site-packages.
        const string workerScript = """
            import json, sys
            model, version, provider, group = sys.argv[1], sys.argv[2], sys.argv[3], int(sys.argv[4])
            for line in sys.stdin:
                request = json.loads(line)
                response = {"version": version, "requestId": str(request.get("requestId")), "ok": True,
                            "index": None, "abstained": False, "margin": None, "score": None,
                            "confidence": None, "provider": provider, "device": "CPU",
                            "modelVersion": model, "pipeline": "cross", "errorCode": None, "error": None}
                if request.get("command") == "select":
                    response.update(index=group, margin=0.5, score=0.9, confidence=0.9)
                sys.stdout.write(json.dumps(response, separators=(",", ":")) + "\n")
                sys.stdout.flush()
                if request.get("command") == "shutdown":
                    break
            """;
#endif

        await using GlossBertDictionarySelectorService service = new(
            root,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromSeconds(5),
            _ =>
            {
#if WINDOWS
                ProcessStartInfo startInfo = new(powershell);
                startInfo.ArgumentList.Add("-NoLogo");
                startInfo.ArgumentList.Add("-NoProfile");
                startInfo.ArgumentList.Add("-NonInteractive");
                startInfo.ArgumentList.Add("-Command");
                startInfo.ArgumentList.Add(workerCommand);
#else
                ProcessStartInfo startInfo = new("python3");
                startInfo.ArgumentList.Add("-I");
                startInfo.ArgumentList.Add("-c");
                startInfo.ArgumentList.Add(workerScript);
                startInfo.ArgumentList.Add(
                    GlossBertDictionarySelectorService.CpuModelCacheId + ":pipeline-cross");
                startInfo.ArgumentList.Add(RankerProtocolV2.Version);
                startInfo.ArgumentList.Add(GlossBertDictionarySelectorService.CpuExecutionProvider);
                startInfo.ArgumentList.Add(
                    selectedGroupId.ToString(System.Globalization.CultureInfo.InvariantCulture));
#endif
                return startInfo;
            });

        bool profileSwitchedAfterResponse = false;
        service.BeforeSelectionMaterializationForTests = async () =>
        {
            await service.ConfigureCpuModelProfileAsync(
                RankerCpuModelProfile.Consec);
            profileSwitchedAfterResponse = true;
        };

        GlossBertDictionarySelection selection = await service.SelectAsync(
            "He sat on the bank.",
            "bank",
            14,
            ["financial institution", "land beside water"],
            [selectedGroupId, 9],
            "English",
            GlossBertRankerProvider.Cpu);

        Check(profileSwitchedAfterResponse &&
              service.CpuModelProfile == RankerCpuModelProfile.Consec,
            "The deterministic fixture did not switch profiles after the response.");
        Check(selection is
        {
            Index: selectedGroupId,
            ModelVersion: GlossBertDictionarySelectorService.CpuModelCacheId +
                ":pipeline-cross"
        },
            "A completed GlossBERT response was revalidated against a later CPU profile.");
    }

    private static async Task VerifyResponseDeadlineKillsWorkerAsync(string tempRoot)
    {
        string root = Path.Combine(tempRoot, "ranker-response-timeout");
        string rankerDirectory = Path.Combine(root, "Ranker");
        Directory.CreateDirectory(Path.Combine(rankerDirectory, "Models"));
        File.WriteAllBytes(
            Path.Combine(rankerDirectory, "AsteriaScale.RankerWorker.exe"),
            []);

#if WINDOWS
        string powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        Check(File.Exists(powershell),
            "The response-timeout fixture requires the inbox Windows PowerShell executable.");
#endif

        await using GlossBertDictionarySelectorService service = new(
            root,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMilliseconds(300),
            _ =>
            {
#if WINDOWS
                ProcessStartInfo startInfo = new(powershell);
                startInfo.ArgumentList.Add("-NoLogo");
                startInfo.ArgumentList.Add("-NoProfile");
                startInfo.ArgumentList.Add("-NonInteractive");
                startInfo.ArgumentList.Add("-Command");
                startInfo.ArgumentList.Add(
                    "$null = [Console]::In.ReadLine(); Start-Sleep -Seconds 30");
#else
                // Reads the first request and then never answers, like the
                // PowerShell fake; the deadline must kill the whole tree.
                ProcessStartInfo startInfo = new("/bin/sh");
                startInfo.ArgumentList.Add("-c");
                startInfo.ArgumentList.Add("read line; sleep 30");
#endif
                return startInfo;
            });

        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            await service.WarmUpAsync(GlossBertRankerProvider.Cpu);
            throw new InvalidOperationException(
                "A worker that stopped responding after a request was accepted indefinitely.");
        }
        catch (GlossBertRankerException ex) when (
            ex.ErrorCode == "worker_response_timeout")
        {
            // Expected: a post-write deadline is a transport failure and kills
            // the entire isolated worker tree before the transaction unlocks.
        }

        Check(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            "The ranker response deadline did not bound the hung transaction.");
        Check(!service.IsWorkerRunning && service.WorkerProcessId is null,
            "The timed-out ranker worker was left running after transport recovery.");
    }

    // A host's status line (CoreHost's models.status) names the code of the
    // last failed worker transaction until a later one succeeds. The first
    // fake worker rejects warm without a code, which the selector reports as
    // worker_error; the restarted one answers.
    private static async Task VerifyLastErrorCodeAsync(string tempRoot)
    {
        string root = Path.Combine(tempRoot, "ranker-last-error-code");
        string rankerDirectory = Path.Combine(root, "Ranker");
        Directory.CreateDirectory(Path.Combine(rankerDirectory, "Models"));
        File.WriteAllBytes(
            Path.Combine(rankerDirectory, "AsteriaScale.RankerWorker.exe"),
            []);
        string model = GlossBertDictionarySelectorService.CpuModelCacheId + ":pipeline-cross";

#if WINDOWS
        string powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        Check(File.Exists(powershell),
            "The last-error fixture requires the inbox Windows PowerShell executable.");
#else
        // The same contract in python3, as the profile-race fake above.
        const string workerScript = """
            import json, sys
            model, version, provider, warm, fail = sys.argv[1:6]
            for line in sys.stdin:
                request = json.loads(line)
                response = {"version": version, "requestId": str(request.get("requestId")), "ok": True,
                            "index": None, "abstained": False, "margin": None, "score": None,
                            "confidence": None, "provider": provider, "device": "CPU",
                            "modelVersion": model, "pipeline": "cross", "errorCode": None, "error": None}
                if fail == "fail" and request.get("command") == warm:
                    response.update(ok=False, error="fixture failure")
                sys.stdout.write(json.dumps(response, separators=(",", ":")) + "\n")
                sys.stdout.flush()
                if request.get("command") == "shutdown":
                    break
            """;
#endif

        int starts = 0;
        await using GlossBertDictionarySelectorService service = new(
            root,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromSeconds(30),
            _ =>
            {
                bool fail = Interlocked.Increment(ref starts) == 1;
#if WINDOWS
                ProcessStartInfo startInfo = new(powershell);
                startInfo.ArgumentList.Add("-NoLogo");
                startInfo.ArgumentList.Add("-NoProfile");
                startInfo.ArgumentList.Add("-NonInteractive");
                startInfo.ArgumentList.Add("-Command");
                startInfo.ArgumentList.Add(
                    "$fail = $" + (fail ? "true" : "false") + "; " +
                    "while (($line = [Console]::In.ReadLine()) -ne $null) { " +
                    "$request = $line | ConvertFrom-Json; " +
                    "$response = [ordered]@{ " +
                    "version = '" + RankerProtocolV2.Version + "'; " +
                    "requestId = [string]$request.requestId; ok = $true; " +
                    "index = $null; abstained = $false; margin = $null; " +
                    "score = $null; confidence = $null; " +
                    "provider = '" +
                    GlossBertDictionarySelectorService.CpuExecutionProvider +
                    "'; device = 'CPU'; modelVersion = '" + model + "'; pipeline = 'cross'; " +
                    "errorCode = $null; error = $null }; " +
                    "if ($fail -and $request.command -eq '" + RankerProtocolV2.Warm + "') { " +
                    "$response.ok = $false; $response.error = 'fixture failure' }; " +
                    "[Console]::Out.WriteLine(($response | ConvertTo-Json -Compress)); " +
                    "[Console]::Out.Flush(); " +
                    "if ($request.command -eq 'shutdown') { break } }");
#else
                ProcessStartInfo startInfo = new("python3");
                startInfo.ArgumentList.Add("-I");
                startInfo.ArgumentList.Add("-c");
                startInfo.ArgumentList.Add(workerScript);
                startInfo.ArgumentList.Add(model);
                startInfo.ArgumentList.Add(RankerProtocolV2.Version);
                startInfo.ArgumentList.Add(GlossBertDictionarySelectorService.CpuExecutionProvider);
                startInfo.ArgumentList.Add(RankerProtocolV2.Warm);
                startInfo.ArgumentList.Add(fail ? "fail" : "answer");
#endif
                return startInfo;
            });

        Check(service.LastErrorCode is null,
            "The selector reported an error before any worker transaction.");
        try
        {
            await service.WarmUpAsync(GlossBertRankerProvider.Cpu);
            throw new InvalidOperationException("A worker that rejected warm was accepted.");
        }
        catch (GlossBertRankerException ex) when (ex.ErrorCode == "worker_error")
        {
            // Expected: the failure still reaches the caller unchanged.
        }

        Check(service.LastErrorCode == "worker_error" && !service.IsWorkerRunning,
            $"After a worker_error the selector reports '{service.LastErrorCode}' " +
            $"with the worker running: {service.IsWorkerRunning}.");

        await service.WarmUpAsync(GlossBertRankerProvider.Cpu);
        Check(service.LastErrorCode is null && service.IsWorkerRunning && Volatile.Read(ref starts) == 2,
            $"A successful transaction after the failure left '{service.LastErrorCode}' " +
            $"after {Volatile.Read(ref starts)} worker starts.");
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
            ModelVersion = GlossBertDictionarySelectorService.GetModelCacheId(provider) +
            ":pipeline-cross",
            Pipeline = "cross"
        };

    private static void ExpectProtocolViolation(
        RankerResponse response,
        string requestId,
        GlossBertRankerProvider provider,
        IReadOnlyCollection<int> groupIds,
        string message,
        RankerCpuModelProfile cpuModelProfile = RankerCpuModelProfile.GlossBert)
    {
        try
        {
            _ = GlossBertDictionarySelectorService.ValidateSelectionResponseForTests(
                response,
                requestId,
                provider,
                groupIds,
                cpuModelProfile);
        }
        catch (GlossBertRankerException ex) when (
            ex.ErrorCode == "protocol_violation")
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void ExpectWarmProtocolViolation(
        RankerResponse response,
        string message)
    {
        try
        {
            GlossBertDictionarySelectorService.ValidateWarmResponseForTests(
                response,
                response.RequestId,
                GlossBertRankerProvider.Cpu);
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
