using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AsteriaScale.Services;

internal static class TranslateGemmaTranslationServiceTests
{
    internal static async Task RunAsync(string tempRoot)
    {
        VerifyPinnedIdentities();
        VerifyOfficialRawPrompt();
        VerifyPostEditPrompt();
        VerifyDeterministicCompletionRequest();
        VerifyCompletionResponseParsing();
        VerifyBackendServerStartInfo();
        VerifyVulkanStartupEvidence();
        await VerifyArtifactValidationAsync(tempRoot);
        await VerifyRunnerArchiveResolutionAsync(tempRoot);
        await VerifyModelPathResolutionAsync(tempRoot);
        await VerifyCleanupFailurePreservationAsync();
        await VerifyRuntimeCleanupRetryAsync(tempRoot);
        await VerifyStaleRuntimeSweepAsync(tempRoot);
        await VerifyDisabledStartupCleanupAsync(tempRoot);
    }

    private static async Task VerifyCleanupFailurePreservationAsync()
    {
        InvalidDataException primary = new("synthetic startup failure");
        Exception unchanged =
            TranslateGemmaTranslationService.CombinePrimaryAndCleanupFailures(
                primary,
                []);
        Check(ReferenceEquals(unchanged, primary),
            "A startup failure without cleanup errors lost its original identity.");

        IOException stopFailure = new("synthetic stop failure");
        UnauthorizedAccessException sweepFailure = new("synthetic sweep failure");
        int cleanupSteps = 0;
        IReadOnlyList<Exception> cleanupFailures =
            await TranslateGemmaTranslationService.CollectCleanupErrorsAsync(
                () =>
                {
                    cleanupSteps++;
                    return Task.FromException(stopFailure);
                },
                () =>
                {
                    cleanupSteps++;
                    return Task.FromException(sweepFailure);
                });
        Exception combined =
            TranslateGemmaTranslationService.CombinePrimaryAndCleanupFailures(
                primary,
                cleanupFailures);
        Check(cleanupSteps == 2 &&
              combined is AggregateException aggregate &&
              aggregate.InnerExceptions.Count == 3 &&
              ReferenceEquals(aggregate.InnerExceptions[0], primary) &&
              ReferenceEquals(aggregate.InnerExceptions[1], stopFailure) &&
              ReferenceEquals(aggregate.InnerExceptions[2], sweepFailure),
            "Cleanup stopped at the first error or masked the primary startup failure.");
    }

    private static void VerifyPinnedIdentities()
    {
        Check(TranslateGemmaTranslationService.ModelIdentity.Contains(
                  TranslateGemmaTranslationService.ModelSha256,
                  StringComparison.Ordinal) &&
              TranslateGemmaTranslationService.ModelIdentity.Contains(
                  "llama.cpp-b9632-cpu",
                  StringComparison.Ordinal) &&
              TranslateGemmaTranslationService.DirectPromptIdentity ==
              "translategemma-direct-en-ru-v1" &&
              TranslateGemmaTranslationService.CorrectionPromptIdentity ==
              "translategemma-ape-en-ru-v2" &&
              TranslateGemmaTranslationService.PromptIdentity ==
              "translategemma-direct-ape-en-ru-v3",
            "The experimental service silently changed the model/quantization.");
        Check(TranslateGemmaTranslationService.GetModelIdentity(
                  TranslateGemmaExecutionBackend.Cpu) ==
              TranslateGemmaTranslationService.ModelIdentity &&
              TranslateGemmaTranslationService.GetModelIdentity(
                  TranslateGemmaExecutionBackend.Vulkan) ==
              TranslateGemmaTranslationService.VulkanModelIdentity &&
              TranslateGemmaTranslationService.VulkanModelIdentity.Contains(
                  "llama.cpp-b9632-vulkan-full",
                  StringComparison.Ordinal) &&
              TranslateGemmaTranslationService.VulkanModelIdentity.Contains(
                  TranslateGemmaTranslationService.ModelSha256,
                  StringComparison.Ordinal) &&
              TranslateGemmaTranslationService.ModelIdentity !=
              TranslateGemmaTranslationService.VulkanModelIdentity,
            "CPU and Vulkan execution identities are not distinct and pinned.");
        Check(TranslateGemmaTranslationService.ResolveBackend("cpu") ==
                  TranslateGemmaExecutionBackend.Cpu &&
              TranslateGemmaTranslationService.ResolveBackend(null) ==
                  TranslateGemmaExecutionBackend.Cpu &&
              TranslateGemmaTranslationService.ResolveBackend("VULKAN") ==
                  TranslateGemmaExecutionBackend.Vulkan &&
              TranslateGemmaTranslationService.ResolveBackendIdentity(
                  TranslateGemmaTranslationService.ModelIdentity) ==
                  TranslateGemmaExecutionBackend.Cpu &&
              TranslateGemmaTranslationService.ResolveBackendIdentity(
                  TranslateGemmaTranslationService.VulkanModelIdentity) ==
                  TranslateGemmaExecutionBackend.Vulkan &&
              TranslateGemmaTranslationService.GetEngineLabel(
                  TranslateGemmaExecutionBackend.Vulkan).Contains(
                  "VULKAN",
                  StringComparison.Ordinal),
            "TranslateGemma backend settings or engine labels are not stable.");
        CheckThrows<InvalidOperationException>(
            () => TranslateGemmaTranslationService.ResolveBackend("unsupported"),
            "An unknown explicit backend silently fell back to CPU.");
        CheckThrows<InvalidOperationException>(
            () => TranslateGemmaTranslationService.ResolveBackendIdentity("different-model"),
            "An unknown model identity silently selected a runtime.");
        Check(TranslateGemmaTranslationService.ModelIdentity.Contains(
                  "mradermacher-static-q4_K_M@35a7486e128b19642cdc72d7b91b21ba388aaf42",
                  StringComparison.Ordinal) &&
              TranslateGemmaTranslationService.ModelSize == 2_489_909_760 &&
              TranslateGemmaTranslationService.ModelSha256 ==
              "81200d03e843d2ec1ece6eeafe7d13cb6e5211e1fcd336ade55790b683a08330",
            "The llama.cpp-compatible Q4_K_M artifact pin changed.");
        Check(TranslateGemmaTranslationService.RunnerBuild == "b9632" &&
              TranslateGemmaTranslationService.RunnerArchiveSize == 16_899_258 &&
              TranslateGemmaTranslationService.RunnerArchiveSha256 ==
              "b835d5c5155dd2a5ed748a0351debf2ede0dc9f808757e0429f8700a11832dcd",
            "The llama.cpp CPU runner pin changed.");
        Check(TranslateGemmaTranslationService.VulkanRunnerArchiveFileName ==
                  "llama-b9632-bin-win-vulkan-x64.zip" &&
              TranslateGemmaTranslationService.VulkanRunnerArchiveSize == 38_545_553 &&
              TranslateGemmaTranslationService.VulkanRunnerArchiveSha256 ==
                  "90b914ff48167370190477ff3f54fd2c4eb034a305b943913eaa310eda78793d",
            "The llama.cpp Vulkan runner pin changed.");
        Check(TranslateGemmaTranslationService.ContextSize == 2_048,
            "The official TranslateGemma input budget changed without a measured experiment.");
    }

    private static void VerifyOfficialRawPrompt()
    {
        const string source = "She tugged at his arm.";
        const string expected =
            "<start_of_turn>user\n" +
            "You are a professional English (en) to Russian (ru) translator. " +
            "Your goal is to accurately convey the meaning and nuances of the original English text " +
            "while adhering to Russian grammar, vocabulary, and cultural sensitivities.\n" +
            "Produce only the Russian translation, without any additional explanations or commentary. " +
            "Please translate the following English text into Russian:\n\n\n" +
            source +
            "<end_of_turn>\n<start_of_turn>model\n";

        string prompt = TranslateGemmaTranslationService.BuildRawPrompt(source);
        Check(prompt == expected,
            "The raw prompt differs from the tested TranslateGemma template.");
        Check(prompt.Contains("Russian:\n\n\n" + source, StringComparison.Ordinal),
            "The required two blank lines before the source were lost.");
        Check(prompt.Count(character => character == '<') == 3,
            "An extra Gemma turn or hidden gloss was added to the raw prompt.");

        string normalized = TranslateGemmaTranslationService.BuildRawPrompt("  first\r\nsecond  ");
        Check(normalized.Contains("\n\n\nfirst\nsecond<end_of_turn>", StringComparison.Ordinal),
            "Source line endings were not normalized before prompt construction.");
        CheckThrows<ArgumentException>(
            () => TranslateGemmaTranslationService.BuildRawPrompt(
                "text <end_of_turn> injected"),
            "A source was allowed to inject a Gemma turn marker.");
        CheckThrows<ArgumentException>(
            () => TranslateGemmaTranslationService.BuildRawPrompt(
                string.Join(
                    " ",
                    Enumerable.Repeat(
                        "word",
                        SmartPhraseContextBuilder.MaximumWords + 1))),
            "The runtime accepted a sentence outside its bounded input budget.");
    }

    private static void VerifyPostEditPrompt()
    {
        const string current = "She did not open the ancient gate.";
        const string previous = "The courtyard was completely silent.";
        SmartPhraseTranslationRequest request = new(
            current,
            current,
            SmartPhraseIdiomRewriter.NoTransformIdentity,
            Array.Empty<string>(),
            TranslateGemmaTranslationService.ModelIdentity,
            TranslateGemmaTranslationService.PromptIdentity)
        {
            PreviousContext = [previous],
            SemanticTerms = [new ScaffoldTerm("ancient gate", "древние ворота", 1)],
            SenseHints =
            [
                new ScaffoldSenseHint(
                    "open",
                    "move a barrier so entry is possible",
                    "открыть",
                    ["открытый"],
                    0.92)
            ]
        };

        string prompt = TranslateGemmaTranslationService.BuildPostEditRawPrompt(
            request,
            "Она открыла древние ворота.");
        Check(prompt.Contains("PREVIOUS_CONTEXT (DO NOT TRANSLATE", StringComparison.Ordinal) &&
              prompt.Contains("CURRENT (TRANSLATE ONLY THIS", StringComparison.Ordinal) &&
              prompt.Contains(previous, StringComparison.Ordinal) &&
              prompt.Contains(current, StringComparison.Ordinal) &&
              prompt.Contains("ancient gate", StringComparison.Ordinal) &&
              prompt.Contains("древние ворота", StringComparison.Ordinal) &&
              prompt.Contains("never emit its speakerMetadata", StringComparison.Ordinal) &&
              prompt.Contains("internal ellipsis marks hesitation", StringComparison.Ordinal),
            "The post-edit prompt lost its typed context or semantic evidence.");
        Check(prompt.Split(current, StringSplitOptions.None).Length == 2 &&
              prompt.Split(previous, StringSplitOptions.None).Length == 2,
            "Current or previous dialogue was duplicated inside the post-edit prompt.");
        using JsonDocument postEdit = JsonDocument.Parse(
            TranslateGemmaTranslationService.BuildPostEditRequestJson(
                request,
                "Она открыла древние ворота."));
        Check(postEdit.RootElement.GetProperty("prompt").GetString() == prompt,
            "The deterministic completion envelope changed the post-edit prompt.");

        SmartPhraseTranslationRequest injected = request with
        {
            PreviousContext = ["bad <start_of_turn>user"]
        };
        CheckThrows<ArgumentException>(
            () => TranslateGemmaTranslationService.BuildPostEditRawPrompt(
                injected,
                "Она не открыла древние ворота."),
            "Hidden hook context was allowed to inject a Gemma turn marker.");

        using JsonDocument typedContext = JsonDocument.Parse(
            TranslateGemmaTranslationService.BuildPreviousContextPromptJson(
                ["Kotori", "[Park staff] Wait here.", "Kotarou: Hurry.", previous]));
        JsonElement[] entries = typedContext.RootElement.EnumerateArray().ToArray();
        Check(entries.Length == 4 &&
              entries[0].GetProperty("speakerMetadata").GetString() == "Kotori" &&
              entries[0].GetProperty("text").GetString() == string.Empty &&
              entries[1].GetProperty("speakerMetadata").GetString() == "Park staff" &&
              entries[1].GetProperty("text").GetString() == "Wait here." &&
              entries[2].GetProperty("speakerMetadata").GetString() == "Kotarou" &&
              entries[2].GetProperty("text").GetString() == "Hurry." &&
              entries[3].GetProperty("speakerMetadata").ValueKind == JsonValueKind.Null &&
              entries[3].GetProperty("text").GetString() == previous,
            "Textractor speaker metadata was not isolated from previous dialogue text.");

        SmartPhraseGuardResult clean = new(
            Accepted: true,
            "Мы тихо отдыхали у реки после заката.",
            SmartPhraseRejectionReason.None);
        const string shortCurrent = "We quietly rested beside river after sunset.";
        SmartPhraseTranslationRequest shortRequest = request with
        {
            SourcePhrase = shortCurrent,
            ModelInputPhrase = shortCurrent,
            PreviousContext = Array.Empty<string>(),
            SemanticTerms = Array.Empty<ScaffoldTerm>(),
            SenseHints = Array.Empty<ScaffoldSenseHint>()
        };
        Check(!TranslateGemmaTranslationService.ShouldPostEdit(shortRequest, clean),
            "A clean short sentence paid for an unnecessary second generation.");
        const string longCurrent =
            "We quietly rested beside the wide river after sunset.";
        Check(TranslateGemmaTranslationService.ShouldPostEdit(
                shortRequest with
                {
                    SourcePhrase = longCurrent,
                    ModelInputPhrase = longCurrent
                },
                clean) &&
              TranslateGemmaTranslationService.ShouldPostEdit(
                  shortRequest with { PreviousContext = [previous] },
                  clean) &&
              TranslateGemmaTranslationService.ShouldPostEdit(
                  shortRequest,
                  clean with
                  {
                      Accepted = false,
                      Reason = SmartPhraseRejectionReason.SemanticTermMissing
                  }),
            "Long, contextual, or guard-rejected output did not enter the single APE pass.");
    }

    private static void VerifyDeterministicCompletionRequest()
    {
        string requestJson = TranslateGemmaTranslationService.BuildCompletionRequestJson("Hello!");
        using JsonDocument document = JsonDocument.Parse(requestJson);
        JsonElement root = document.RootElement;

        Check(root.GetProperty("prompt").GetString() ==
              TranslateGemmaTranslationService.BuildRawPrompt("Hello!"),
            "The request does not carry the exact tested raw prompt.");
        Check(root.GetProperty("n_predict").GetInt32() ==
              TranslateGemmaTranslationService.MaximumGeneratedTokens,
            "The completion token bound changed.");
        Check(root.GetProperty("temperature").GetDouble() == 0 &&
              root.GetProperty("top_k").GetInt32() == 1 &&
              root.GetProperty("top_p").GetDouble() == 1 &&
              root.GetProperty("min_p").GetDouble() == 0 &&
              root.GetProperty("seed").GetInt32() == 0,
            "The raw completion request is no longer deterministic.");
        Check(!root.GetProperty("stream").GetBoolean() &&
              root.GetProperty("cache_prompt").GetBoolean(),
            "The service must use one bounded, non-streaming response.");
        JsonElement.ArrayEnumerator stop = root.GetProperty("stop").EnumerateArray();
        Check(stop.MoveNext() && stop.Current.GetString() == "<end_of_turn>" &&
              !stop.MoveNext(),
            "The generation stop marker differs from the Ollama model parameters.");
    }

    private static void VerifyCompletionResponseParsing()
    {
        string translation = TranslateGemmaTranslationService.ParseCompletionResponse(
            "{\"content\":\"  Она потянула его за руку.  <end_of_turn>\",\"stop\":true}");
        Check(translation == "Она потянула его за руку.",
            $"The response parser kept protocol framing: '{translation}'.");

        CheckThrows<InvalidDataException>(
            () => TranslateGemmaTranslationService.ParseCompletionResponse("{\"stop\":true}"),
            "A completion without content was accepted.");
        CheckThrows<InvalidDataException>(
            () => TranslateGemmaTranslationService.ParseCompletionResponse("{\"content\":\"   \"}"),
            "An empty translation was accepted.");
        CheckThrows<InvalidDataException>(
            () => TranslateGemmaTranslationService.ParseCompletionResponse(
                "{\"content\":\"<start_of_turn>user\"}"),
            "A prompt echo was accepted as a translation.");
        CheckThrows<InvalidDataException>(
            () => TranslateGemmaTranslationService.ParseCompletionResponse("not-json"),
            "Malformed llama-server JSON was accepted.");
    }

    private static void VerifyBackendServerStartInfo()
    {
        ProcessStartInfo cpu = TranslateGemmaTranslationService.CreateServerStartInfo(
            @"C:\runtime\llama-server.exe",
            @"C:\runtime",
            @"C:\models\translate gemma.gguf",
            43123,
            "random-secret",
            TranslateGemmaExecutionBackend.Cpu);
        string[] arguments = cpu.ArgumentList.ToArray();

        Check(!cpu.UseShellExecute && cpu.CreateNoWindow &&
              cpu.WindowStyle == ProcessWindowStyle.Hidden &&
              cpu.RedirectStandardOutput && cpu.RedirectStandardError,
            "The owned server could open a console or block on an undrained pipe.");
        Check(HasPair(arguments, "--host", "127.0.0.1") &&
              HasPair(arguments, "--port", "43123") &&
              HasPair(arguments, "--ctx-size", "2048") &&
              HasPair(arguments, "--n-gpu-layers", "0") &&
              HasPair(arguments, "--parallel", "1") &&
              HasPair(arguments, "--api-key", "random-secret"),
            "The server is not pinned to loopback, CPU-only execution and a private API key.");
        Check(arguments.Contains("--no-jinja", StringComparer.Ordinal) &&
              arguments.Contains("--no-webui", StringComparer.Ordinal),
            "The raw-completion runtime enabled Jinja parsing or llama.cpp's web UI.");

        ProcessStartInfo vulkan = TranslateGemmaTranslationService.CreateServerStartInfo(
            @"C:\runtime\llama-server.exe",
            @"C:\runtime",
            @"C:\models\translate gemma.gguf",
            43124,
            "vulkan-secret",
            TranslateGemmaExecutionBackend.Vulkan);
        string[] vulkanArguments = vulkan.ArgumentList.ToArray();
        Check(HasPair(vulkanArguments, "--device", "Vulkan0") &&
              HasPair(vulkanArguments, "--n-gpu-layers", "all") &&
              HasPair(vulkanArguments, "--fit", "off") &&
              HasPair(vulkanArguments, "-lv", "4") &&
              HasPair(vulkanArguments, "--api-key", "vulkan-secret") &&
              !HasPair(vulkanArguments, "--n-gpu-layers", "0"),
            "The Vulkan server is not pinned to full Vulkan0 offload without auto-fit.");
    }

    private static void VerifyVulkanStartupEvidence()
    {
        TranslateGemmaTranslationService.VulkanStartupEvidenceTracker accepted = new();
        accepted.Observe("common_init: using device Vulkan0 (AMD Radeon 860M) - 4096 MiB free");
        accepted.Observe("load_tensors: offloaded 35/35 layers to GPU");
        Check(accepted.IsComplete && accepted.DescribeMissing().Length == 0,
            "A complete Vulkan0/full-offload startup log was rejected.");

        TranslateGemmaTranslationService.VulkanStartupEvidenceTracker partial = new();
        partial.Observe("common_init: using device Vulkan0 (AMD Radeon 860M) - 4096 MiB free");
        partial.Observe("load_tensors: offloaded 34/35 layers to GPU");
        Check(!partial.IsComplete &&
              partial.DescribeMissing().Contains(
                  "offloaded 35/35 layers to GPU",
                  StringComparison.Ordinal),
            "A partial Vulkan offload was accepted as the full GPU backend.");

        TranslateGemmaTranslationService.VulkanStartupEvidenceTracker wrongDevice = new();
        wrongDevice.Observe("common_init: using device Vulkan1 (other GPU) - 8192 MiB free");
        wrongDevice.Observe("load_tensors: offloaded 35/35 layers to GPU");
        Check(!wrongDevice.IsComplete &&
              wrongDevice.DescribeMissing().Contains("Vulkan0", StringComparison.Ordinal),
            "A different Vulkan device satisfied the pinned Vulkan0 contract.");
    }

    private static async Task VerifyArtifactValidationAsync(string tempRoot)
    {
        string root = Path.Combine(tempRoot, "translategemma-artifacts");
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "artifact.bin");
        byte[] bytes = Encoding.UTF8.GetBytes("pinned TranslateGemma test artifact");
        await File.WriteAllBytesAsync(path, bytes);
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));

        await TranslateGemmaTranslationService.ValidatePinnedArtifactAsync(
            path,
            bytes.LongLength,
            sha256);
        await CheckThrowsAsync<InvalidDataException>(
            () => TranslateGemmaTranslationService.ValidatePinnedArtifactAsync(
                path,
                bytes.LongLength + 1,
                sha256),
            "An artifact with the wrong byte length passed validation.");
        await CheckThrowsAsync<InvalidDataException>(
            () => TranslateGemmaTranslationService.ValidatePinnedArtifactAsync(
                path,
                bytes.LongLength,
                new string('0', 64)),
            "An artifact with the wrong SHA-256 passed validation.");
        await CheckThrowsAsync<FileNotFoundException>(
            () => TranslateGemmaTranslationService.ValidatePinnedArtifactAsync(
                Path.Combine(root, "missing.bin"),
                expectedSize: null,
                sha256),
            "A missing pinned artifact passed validation.");
    }

    private static async Task VerifyRunnerArchiveResolutionAsync(string tempRoot)
    {
        string root = Path.Combine(tempRoot, "translategemma-runner-resolution");
        Directory.CreateDirectory(root);
        string cpu = Path.Combine(
            root,
            TranslateGemmaTranslationService.RunnerArchiveFileName);
        await File.WriteAllBytesAsync(cpu, []);

        Check(TranslateGemmaTranslationService.FindRunnerArchivePath(
                  root,
                  TranslateGemmaExecutionBackend.Cpu) == cpu,
            "The CPU profile did not resolve its exact pinned archive.");
        CheckThrows<FileNotFoundException>(
            () => TranslateGemmaTranslationService.FindRunnerArchivePath(
                root,
                TranslateGemmaExecutionBackend.Vulkan),
            "A missing Vulkan archive silently fell back to the installed CPU runner.");

        string vulkan = Path.Combine(
            root,
            TranslateGemmaTranslationService.VulkanRunnerArchiveFileName);
        await File.WriteAllBytesAsync(vulkan, []);
        Check(TranslateGemmaTranslationService.FindRunnerArchivePath(
                  root,
                  TranslateGemmaExecutionBackend.Vulkan) == vulkan,
            "The Vulkan profile did not resolve its exact pinned archive.");
    }

    private static async Task VerifyModelPathResolutionAsync(string tempRoot)
    {
        string modelRoot = Path.Combine(tempRoot, "translategemma-model-root");
        string ollamaRoot = Path.Combine(tempRoot, "translategemma-ollama-root");
        string blobs = Path.Combine(ollamaRoot, "blobs");
        Directory.CreateDirectory(modelRoot);
        Directory.CreateDirectory(blobs);

        string blob = Path.Combine(blobs, TranslateGemmaTranslationService.OllamaBlobFileName);
        await File.WriteAllBytesAsync(blob, []);
        Check(TranslateGemmaTranslationService.FindModelPath(modelRoot, ollamaRoot) == blob,
            "The pinned Ollama blob was not discovered.");

        string local = Path.Combine(
            modelRoot,
            TranslateGemmaTranslationService.LocalModelFileName);
        await File.WriteAllBytesAsync(local, []);
        Check(TranslateGemmaTranslationService.FindModelPath(modelRoot, ollamaRoot) == local,
            "The managed model directory did not take priority over Ollama storage.");

        File.Delete(local);
        File.Delete(blob);
        CheckThrows<FileNotFoundException>(
            () => TranslateGemmaTranslationService.FindModelPath(modelRoot, ollamaRoot),
            "Model resolution silently substituted an unpinned model.");
    }

    private static async Task VerifyRuntimeCleanupRetryAsync(string tempRoot)
    {
        string runtimeRoot = Path.Combine(tempRoot, "translategemma-runtime-retry");
        string runtimeDirectory = Path.Combine(
            runtimeRoot,
            TranslateGemmaTranslationService.RunnerBuild + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runtimeDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(runtimeDirectory, "ggml-base.dll"),
            "fixture");

        int attempts = 0;
        int retries = 0;
        bool deleted = await TranslateGemmaTranslationService.DeleteRuntimeDirectoryWithRetryAsync(
            runtimeDirectory,
            runtimeRoot,
            maximumAttempts: 3,
            initialRetryDelay: TimeSpan.FromMilliseconds(1),
            deleteDirectory: path =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new IOException("synthetic transient DLL lock");
                }

                Directory.Delete(path, recursive: true);
            },
            delayAsync: _ => Task.CompletedTask,
            retryObserver: (_, exception) =>
            {
                Check(exception is IOException, "A non-I/O cleanup error was retried.");
                retries++;
            });
        Check(deleted && attempts == 2 && retries == 1 && !Directory.Exists(runtimeDirectory),
            "A transient runtime lock did not retry exactly once and clean the directory.");

        bool absentDeleteCalled = false;
        bool absentDeleted =
            await TranslateGemmaTranslationService.DeleteRuntimeDirectoryWithRetryAsync(
                runtimeDirectory,
                runtimeRoot,
                maximumAttempts: 1,
                initialRetryDelay: TimeSpan.FromMilliseconds(1),
                deleteDirectory: _ => absentDeleteCalled = true);
        Check(!absentDeleted && !absentDeleteCalled,
            "An already-absent runtime directory was reported as deleted.");

        string outside = Path.Combine(
            tempRoot,
            TranslateGemmaTranslationService.RunnerBuild + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        bool unsafeDeleteCalled = false;
        await CheckThrowsAsync<InvalidOperationException>(
            () => TranslateGemmaTranslationService.DeleteRuntimeDirectoryWithRetryAsync(
                outside,
                runtimeRoot,
                maximumAttempts: 1,
                initialRetryDelay: TimeSpan.FromMilliseconds(1),
                deleteDirectory: _ => unsafeDeleteCalled = true),
            "Runtime cleanup accepted a same-shaped directory outside its owned root.");
        Check(!unsafeDeleteCalled && Directory.Exists(outside),
            "Runtime path validation happened after the destructive operation.");

        string swapped = CreateRuntimeDirectory(runtimeRoot);
        int validationCalls = 0;
        int swappedDeleteCalls = 0;
        await CheckThrowsAsync<InvalidOperationException>(
            async () =>
            {
                await TranslateGemmaTranslationService.DeleteRuntimeDirectoryWithRetryAsync(
                    swapped,
                    runtimeRoot,
                    maximumAttempts: 2,
                    initialRetryDelay: TimeSpan.FromMilliseconds(1),
                    deleteDirectory: _ =>
                    {
                        swappedDeleteCalls++;
                        throw new IOException("synthetic first-attempt lock");
                    },
                    delayAsync: _ => Task.CompletedTask,
                    validateDirectory: (path, _) =>
                    {
                        validationCalls++;
                        if (validationCalls == 2)
                        {
                            throw new InvalidOperationException(
                                "synthetic reparse-point replacement");
                        }

                        return Path.GetFullPath(path);
                    });
            },
            "Runtime ownership was not revalidated before a retry.");
        Check(validationCalls == 2 &&
              swappedDeleteCalls == 1 &&
              Directory.Exists(swapped),
            "A changed runtime path reached a second destructive attempt.");
        await TranslateGemmaTranslationService.DeleteRuntimeDirectoryWithRetryAsync(
            swapped,
            runtimeRoot,
            maximumAttempts: 1,
            initialRetryDelay: TimeSpan.FromMilliseconds(1));
    }

    private static async Task VerifyStaleRuntimeSweepAsync(string tempRoot)
    {
        string runtimeRoot = Path.Combine(tempRoot, "translategemma-runtime-sweep");
        Directory.CreateDirectory(runtimeRoot);
        string removable = CreateRuntimeDirectory(runtimeRoot);
        string locked = CreateRuntimeDirectory(runtimeRoot);
        string vanished = CreateRuntimeDirectory(runtimeRoot);
        string active = CreateRuntimeDirectory(runtimeRoot);
        string malformed = Path.Combine(runtimeRoot, TranslateGemmaTranslationService.RunnerBuild + "-not-a-guid");
        string foreign = Path.Combine(runtimeRoot, "verification-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(malformed);
        Directory.CreateDirectory(foreign);
        foreach (string directory in new[] { removable, locked, vanished, active, malformed, foreign })
        {
            Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow - TimeSpan.FromMinutes(10));
        }

        int lockedAttempts = 0;
        int deleted = await TranslateGemmaTranslationService.DeleteStaleRuntimeDirectoriesAsync(
            runtimeRoot,
            activeDirectory: active,
            minimumAge: TimeSpan.Zero,
            maximumAttempts: 2,
            deleteDirectory: path =>
            {
                if (string.Equals(path, locked, StringComparison.OrdinalIgnoreCase))
                {
                    lockedAttempts++;
                    throw new IOException("synthetic persistent DLL lock");
                }

                if (string.Equals(path, vanished, StringComparison.OrdinalIgnoreCase))
                {
                    Directory.Delete(path, recursive: true);
                    throw new IOException("synthetic concurrent remover");
                }

                Directory.Delete(path, recursive: true);
            },
            delayAsync: _ => Task.CompletedTask);
        Check(deleted == 1 &&
              !Directory.Exists(removable) &&
              !Directory.Exists(vanished) &&
              Directory.Exists(locked) &&
              lockedAttempts == 2 &&
              Directory.Exists(active) &&
              Directory.Exists(malformed) &&
              Directory.Exists(foreign),
            "The stale-runtime sweep crossed ownership boundaries or stopped at one locked directory.");

        await TranslateGemmaTranslationService.DeleteRuntimeDirectoryWithRetryAsync(
            locked,
            runtimeRoot,
            maximumAttempts: 1,
            initialRetryDelay: TimeSpan.FromMilliseconds(1));
        Check(!Directory.Exists(locked),
            "A directory left retryable by a failed sweep could not be cleaned later.");
    }

    private static async Task VerifyDisabledStartupCleanupAsync(string tempRoot)
    {
        string modelRoot = Path.Combine(tempRoot, "translategemma-disabled-cleanup");
        string runtimeRoot = Path.Combine(modelRoot, "Runtime");
        string stale = CreateRuntimeDirectory(runtimeRoot);
        string malformed = Path.Combine(
            runtimeRoot,
            TranslateGemmaTranslationService.RunnerBuild + "-not-a-guid");
        string foreign = Path.Combine(runtimeRoot, "verification-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(malformed);
        Directory.CreateDirectory(foreign);

        await using TranslateGemmaTranslationService service = new(
            modelRoot,
            Path.Combine(tempRoot, "translategemma-disabled-ollama"),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
        await service.UnloadAsync();
        Check(!Directory.Exists(stale) &&
              Directory.Exists(malformed) &&
              Directory.Exists(foreign),
            "Starting with phrase translation disabled did not sweep a legacy runtime directory.");
    }

    private static string CreateRuntimeDirectory(string runtimeRoot)
    {
        string directory = Path.Combine(
            runtimeRoot,
            TranslateGemmaTranslationService.RunnerBuild + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "ggml-base.dll"), "fixture");
        return directory;
    }

    private static bool HasPair(IReadOnlyList<string> values, string name, string value)
    {
        for (int index = 0; index + 1 < values.Count; index++)
        {
            if (values[index] == name && values[index + 1] == value)
            {
                return true;
            }
        }

        return false;
    }

    private static void CheckThrows<TException>(Action action, string message)
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

    private static async Task CheckThrowsAsync<TException>(
        Func<Task> action,
        string message)
        where TException : Exception
    {
        try
        {
            await action();
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
