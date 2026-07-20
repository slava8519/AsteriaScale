using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging.Abstractions;

namespace AsteriaScale.Services;

public sealed record SmartDictionarySelection(
    int Index,
    string Translation,
    double Confidence,
    string ContextTranslation);

public sealed class NpuDictionarySelectorService : IAsyncDisposable
{
    private const string ResponsePrefix = "ASTERIA_NPU:";
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);
    public const string ModelId = NpuDictionaryModelSession.ModelId;
    public const string EngineLabel = NpuDictionaryModelSession.EngineLabel;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private Process? _worker;
    private StreamWriter? _workerInput;
    private StreamReader? _workerOutput;
    private Task? _errorDrain;
    private readonly System.Threading.Timer _idleTimer;
    private volatile bool _keepWarm = true;
    private bool _disposed;

    internal int? WorkerProcessId => _worker is { HasExited: false } ? _worker.Id : null;

    public NpuDictionarySelectorService()
    {
        _idleTimer = new System.Threading.Timer(
            _ => _ = UnloadAfterIdleAsync(),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        WorkerResponse response = await SendAsync(new WorkerRequest("warm"), cancellationToken);
        ThrowIfFailed(response);
        ScheduleIdleUnload();
    }

    public void ConfigureKeepWarm(bool keepWarm)
    {
        _keepWarm = keepWarm;
        if (keepWarm)
        {
            _idleTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        else
        {
            ScheduleIdleUnload();
        }
    }

    public async Task<SmartDictionarySelection> SelectAsync(
        string context,
        string target,
        int characterIndex,
        IReadOnlyList<string> candidates,
        string sourceLanguage,
        CancellationToken cancellationToken)
    {
        string[] normalizedCandidates = candidates
            .Select(NpuDictionaryModelSession.NormalizeCandidate)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Take(8)
            .ToArray();
        if (normalizedCandidates.Length == 0)
        {
            throw new ArgumentException("Словарь не вернул вариантов перевода.", nameof(candidates));
        }

        if (normalizedCandidates.Length == 1)
        {
            return new SmartDictionarySelection(0, normalizedCandidates[0], 1, string.Empty);
        }

        WorkerResponse response = await SendAsync(
            new WorkerRequest(
                "select",
                context,
                target,
                characterIndex,
                normalizedCandidates,
                sourceLanguage),
            cancellationToken);
        ThrowIfFailed(response);
        ScheduleIdleUnload();
        return new SmartDictionarySelection(
            response.Index,
            response.Translation ?? string.Empty,
            response.Confidence,
            response.ContextTranslation ?? string.Empty);
    }

    public Task<SmartDictionarySelection> SelectAsync(
        string context,
        string target,
        int characterIndex,
        IReadOnlyList<string> candidates,
        CancellationToken cancellationToken) =>
        SelectAsync(context, target, characterIndex, candidates, "Auto", cancellationToken);

    public async Task UnloadAsync()
    {
        _idleTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        await _requestLock.WaitAsync();
        try
        {
            await StopWorkerLockedAsync();
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private void ScheduleIdleUnload()
    {
        if (!_disposed && !_keepWarm)
        {
            _idleTimer.Change(IdleTimeout, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task UnloadAfterIdleAsync()
    {
        try
        {
            await UnloadAsync();
        }
        catch
        {
            // The next translation request can start a fresh worker.
        }
    }

    private async Task<WorkerResponse> SendAsync(WorkerRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Task<WorkerResponse> transaction = ExecuteTransactionAsync(request, cancellationToken);
        try
        {
            return await transaction.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The UI is free immediately. If the request was already sent, its private
            // transaction continues draining exactly one response in the background.
            _ = ObserveAbandonedTransactionAsync(transaction);
            throw;
        }
    }

    private async Task<WorkerResponse> ExecuteTransactionAsync(
        WorkerRequest request,
        CancellationToken startCancellationToken)
    {
        await _requestLock.WaitAsync(startCancellationToken);
        try
        {
            await EnsureWorkerLockedAsync(startCancellationToken);
            startCancellationToken.ThrowIfCancellationRequested();
            string payload = JsonSerializer.Serialize(request, JsonOptions);
            // From this write onward the transaction is deliberately non-cancellable:
            // otherwise stdout would retain an orphaned response for the next request.
            await _workerInput!.WriteLineAsync(payload);
            string? line;
            do
            {
                line = await _workerOutput!.ReadLineAsync(CancellationToken.None);
                if (line is null)
                {
                    throw new InvalidOperationException("NPU-процесс завершился без ответа.");
                }
            }
            while (!line.TrimStart('\uFEFF').StartsWith(ResponsePrefix, StringComparison.Ordinal));

            string responseJson = line.TrimStart('\uFEFF')[ResponsePrefix.Length..];
            return JsonSerializer.Deserialize<WorkerResponse>(responseJson, JsonOptions)
                   ?? throw new InvalidOperationException("NPU-процесс вернул пустой ответ.");
        }
        catch (OperationCanceledException) when (startCancellationToken.IsCancellationRequested)
        {
            // Cancellation happened before the request entered the IPC stream.
            throw;
        }
        catch
        {
            await StopWorkerLockedAsync();
            throw;
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private static async Task ObserveAbandonedTransactionAsync(Task<WorkerResponse> transaction)
    {
        try
        {
            await transaction;
        }
        catch
        {
            // ExecuteTransactionAsync already performs worker cleanup for real IPC
            // failures. Observation here only prevents an unobserved task exception.
        }
    }

    private Task EnsureWorkerLockedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_worker is { HasExited: false })
        {
            return Task.CompletedTask;
        }

        string executable = ResolveWorkerExecutable();
        ProcessStartInfo startInfo = new(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        startInfo.ArgumentList.Add("--npu-selector-worker");
        _worker = Process.Start(startInfo)
                  ?? throw new InvalidOperationException("Не удалось запустить изолированный NPU-процесс.");
        _workerInput = _worker.StandardInput;
        _workerInput.AutoFlush = true;
        _workerOutput = _worker.StandardOutput;
        _errorDrain = DrainErrorsAsync(_worker.StandardError);
        return Task.CompletedTask;
    }

    private async Task StopWorkerLockedAsync()
    {
        Process? worker = _worker;
        StreamWriter? input = _workerInput;
        _worker = null;
        _workerInput = null;
        _workerOutput = null;
        if (worker is null)
        {
            return;
        }

        try
        {
            if (!worker.HasExited && input is not null)
            {
                await input.WriteLineAsync(JsonSerializer.Serialize(new WorkerRequest("stop"), JsonOptions));
                await input.FlushAsync();
            }

            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(8));
            await worker.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            if (!worker.HasExited)
            {
                worker.Kill(entireProcessTree: true);
                await worker.WaitForExitAsync();
            }
        }
        finally
        {
            input?.Dispose();
            worker.Dispose();
            if (_errorDrain is not null)
            {
                try
                {
                    await _errorDrain;
                }
                catch
                {
                    // Diagnostics must not keep the worker alive.
                }
            }

            _errorDrain = null;
        }
    }

    private static async Task DrainErrorsAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is not null)
        {
            // Vitis writes native diagnostics to stderr. Draining avoids a full pipe.
        }
    }

    private static string ResolveWorkerExecutable()
    {
        string assemblyLocation = typeof(NpuDictionarySelectorService).Assembly.Location;
        if (!string.IsNullOrWhiteSpace(assemblyLocation))
        {
            string candidate = Path.ChangeExtension(assemblyLocation, ".exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Environment.ProcessPath
               ?? throw new InvalidOperationException("Не найден исполняемый файл NPU-процесса.");
    }

    private static void ThrowIfFailed(WorkerResponse response)
    {
        if (!response.Ok)
        {
            throw new InvalidOperationException(response.Error ?? "NPU-процесс не выполнил запрос.");
        }
    }

    internal static async Task RunWorkerAsync(Stream input, Stream output, CancellationToken cancellationToken = default)
    {
        using StreamReader reader = new(input, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        await using StreamWriter writer = new(output, new UTF8Encoding(false), 4096, leaveOpen: true)
        {
            AutoFlush = true
        };
        await using NpuDictionaryModelSession session = new();
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            WorkerResponse response;
            try
            {
                WorkerRequest request = JsonSerializer.Deserialize<WorkerRequest>(line.TrimStart('\uFEFF'), JsonOptions)
                                        ?? throw new JsonException("Пустой запрос NPU-процесса.");
                if (request.Type == "stop")
                {
                    break;
                }

                if (request.Type == "warm")
                {
                    await session.WarmUpAsync(cancellationToken);
                    response = new WorkerResponse(true);
                }
                else if (request.Type == "select")
                {
                    SmartDictionarySelection selected = await session.SelectAsync(
                        request.Context ?? string.Empty,
                        request.Target ?? string.Empty,
                        request.CharacterIndex,
                        request.Candidates ?? [],
                        request.SourceLanguage ?? "Auto",
                        cancellationToken);
                    response = new WorkerResponse(
                        true,
                        selected.Index,
                        selected.Translation,
                        selected.Confidence,
                        selected.ContextTranslation);
                }
                else
                {
                    throw new InvalidOperationException("Неизвестная команда NPU-процесса.");
                }
            }
            catch (Exception ex)
            {
                response = new WorkerResponse(false, Error: ex.Message);
            }

            await writer.WriteLineAsync(ResponsePrefix + JsonSerializer.Serialize(response, JsonOptions));
        }
    }

    internal static ParsedSelection ParseSelectionResponse(string response, int candidateCount)
    {
        NpuDictionaryModelSession.ParsedSelection parsed =
            NpuDictionaryModelSession.ParseSelectionResponse(response, candidateCount);
        return new ParsedSelection(parsed.Index, parsed.Confidence, parsed.ContextTranslation);
    }

    internal static string[] SplitMeanings(string translation) =>
        NpuDictionaryModelSession.SplitMeanings(translation);

    internal static int AlignWithContextTranslation(
        string contextTranslation,
        IReadOnlyList<string> candidates,
        int modelIndex) =>
        NpuDictionaryModelSession.AlignWithContextTranslation(contextTranslation, candidates, modelIndex);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await UnloadAsync();
        _disposed = true;
        _idleTimer.Dispose();
        _requestLock.Dispose();
    }

    internal sealed record ParsedSelection(int Index, double Confidence, string ContextTranslation);
    private sealed record WorkerRequest(
        string Type,
        string? Context = null,
        string? Target = null,
        int CharacterIndex = 0,
        string[]? Candidates = null,
        string? SourceLanguage = null);
    private sealed record WorkerResponse(
        bool Ok,
        int Index = 0,
        string? Translation = null,
        double Confidence = 0,
        string? ContextTranslation = null,
        string? Error = null);
}

internal sealed class NpuDictionaryModelSession : IAsyncDisposable
{
    public const string ModelId = "phi-3-mini-128k-instruct-vitis-npu:2";
    public const string EngineLabel = "NPU OCR  •  NPU-КОНТЕКСТ";

    private const string SystemPrompt =
        "You disambiguate and translate only the marked word, never the complete sentence. " +
        "Choose the single numbered Russian dictionary meaning that fits the marked <target> in the source sentence. " +
        "Use grammar, nearby verbs, objects, prepositions and dialogue context. Candidate order has no priority. " +
        "Return the chosen number, a vertical bar, and the chosen Russian candidate. Do not explain.\n" +
        "Example: He swung the <target>bat</target> at the ball. 0=летучая мышь; 1=бита. Answer: 1|бита\n" +
        "Example: A <target>bat</target> flew out of the cave. 0=летучая мышь; 1=бита. Answer: 0|летучая мышь\n" +
        "Example: We sat on the <target>bank</target> of the river. 0=банк; 1=берег. Answer: 1|берег";

    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);
    private readonly ConcurrentDictionary<string, SmartDictionarySelection> _cache =
        new(StringComparer.Ordinal);
    private FoundryLocalManager? _manager;
    private IModel? _model;
    private OpenAIChatClient? _client;
    private bool _primed;
    private bool _disposed;

    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken);
        if (_primed)
        {
            return;
        }

        await _inferenceLock.WaitAsync(cancellationToken);
        try
        {
            if (_primed)
            {
                return;
            }

            await _client!.CompleteChatAsync(
                [
                    new ChatMessage { Role = "system", Content = SystemPrompt },
                    new ChatMessage
                    {
                        Role = "user",
                        Content = "Source language: English\nSentence: He swung the <target>bat</target> at the ball.\n" +
                                  "Candidates:\n0: летучая мышь\n1: бита\nAnswer:"
                    }
                ],
                cancellationToken);
            _primed = true;
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    public async Task<SmartDictionarySelection> SelectAsync(
        string context,
        string target,
        int characterIndex,
        IReadOnlyList<string> candidates,
        string sourceLanguage,
        CancellationToken cancellationToken)
    {
        string[] normalizedCandidates = candidates
            .Select(NormalizeCandidate)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Take(8)
            .ToArray();
        if (normalizedCandidates.Length == 0)
        {
            throw new ArgumentException("Словарь не вернул вариантов перевода.", nameof(candidates));
        }

        if (normalizedCandidates.Length == 1)
        {
            return new SmartDictionarySelection(0, normalizedCandidates[0], 1, string.Empty);
        }

        string explicitLanguage = ResolveSourceLanguage(sourceLanguage, context);
        string cacheKey = NormalizeCachePart(context) + '\u001f' +
                          NormalizeCachePart(target) + '\u001f' +
                          explicitLanguage + '\u001f' +
                          string.Join('\u001e', normalizedCandidates.Select(NormalizeCachePart));
        if (_cache.TryGetValue(cacheKey, out SmartDictionarySelection? cached))
        {
            return cached;
        }

        await EnsureReadyAsync(cancellationToken);
        await _inferenceLock.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Candidate[] ordered = ReorderCandidates(context, target, normalizedCandidates);
            string marked = MarkTarget(context, target, characterIndex);
            string prompt =
                "Source language: " + explicitLanguage + "\nSentence: " + marked + "\nCandidates:\n" +
                string.Join("\n", ordered.Select((item, index) => $"{index}: {item.Text}")) +
                "\nAnswer:";

            var response = await _client!.CompleteChatAsync(
                [
                    new ChatMessage { Role = "system", Content = SystemPrompt },
                    new ChatMessage { Role = "user", Content = prompt }
                ],
                cancellationToken);
            string raw = response.Choices?[0].Message.Content ?? string.Empty;
            ParsedSelection parsed = ParseSelectionResponse(raw, ordered.Length);
            Candidate selected = ordered[parsed.Index];
            SmartDictionarySelection result = new(
                selected.OriginalIndex,
                selected.Text,
                parsed.Confidence,
                string.Empty);

            if (_cache.Count >= 384)
            {
                _cache.Clear();
            }

            _cache[cacheKey] = result;
            return result;
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    public async Task UnloadAsync()
    {
        await _lifecycleLock.WaitAsync();
        try
        {
            _client = null;
            _primed = false;
            if (_model is not null)
            {
                try
                {
                    await _model.UnloadAsync();
                }
                catch
                {
                    // The model can already be unloaded after a cancelled initialization.
                }

                _model = null;
            }

            _manager?.Dispose();
            _manager = null;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    internal static ParsedSelection ParseSelectionResponse(string response, int candidateCount)
    {
        string cleaned = StripCodeFence(response);
        int? index = null;
        int start = cleaned.IndexOf('{');
        int end = cleaned.LastIndexOf('}');
        if (start >= 0 && end >= start)
        {
            try
            {
                JsonNode? root = JsonNode.Parse(cleaned[start..(end + 1)]);
                index = root?["index"]?.GetValue<int>();
            }
            catch (JsonException)
            {
                // Fall back to a bare number. Small local models occasionally add punctuation.
            }
        }

        if (index is null)
        {
            Match match = Regex.Match(cleaned, @"(?<!\d)\d{1,2}(?!\d)", RegexOptions.CultureInvariant);
            if (match.Success && int.TryParse(match.Value, out int parsedIndex))
            {
                index = parsedIndex;
            }
        }

        if (index is null)
        {
            throw new JsonException("NPU-модель не указала номер значения.");
        }

        if (index < 0 || index >= candidateCount)
        {
            throw new JsonException("NPU-модель выбрала отсутствующее значение.");
        }

        // The model is asked for a class index only. A generated self-confidence was
        // not calibrated and caused unnecessary full-translation fallbacks.
        return new ParsedSelection(index.Value, 0.9, string.Empty);
    }

    internal static string[] SplitMeanings(string translation) => translation
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(Normalize)
        // The dictionary appends a lone "…" chunk when a long meaning list is
        // truncated at a boundary; it must never become a rankable candidate.
        .Where(value => value.Length > 0 && value != "…")
        .Distinct(StringComparer.CurrentCultureIgnoreCase)
        .Take(8)
        .ToArray();

    internal static int AlignWithContextTranslation(
        string contextTranslation,
        IReadOnlyList<string> candidates,
        int modelIndex)
    {
        string[] contextTokens = RussianTokens(contextTranslation);
        if (contextTokens.Length == 0 || candidates.Count == 0)
        {
            return modelIndex;
        }

        double[] scores = new double[candidates.Count];
        for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            foreach (string candidateToken in RussianTokens(candidates[candidateIndex]))
            {
                double best = 0;
                foreach (string contextToken in contextTokens)
                {
                    if (string.Equals(candidateToken, contextToken, StringComparison.Ordinal))
                    {
                        best = 2;
                        break;
                    }

                    int requiredPrefix = candidateToken.Length <= 3
                        ? candidateToken.Length
                        : Math.Min(5, candidateToken.Length - 1);
                    if (requiredPrefix >= 2 && CommonPrefixLength(candidateToken, contextToken) >= requiredPrefix)
                    {
                        best = Math.Max(best, 1);
                    }
                }

                scores[candidateIndex] += best;
            }
        }

        int bestIndex = Enumerable.Range(0, scores.Length).MaxBy(index => scores[index]);
        double modelScore = modelIndex >= 0 && modelIndex < scores.Length ? scores[modelIndex] : 0;
        return scores[bestIndex] > 0 && scores[bestIndex] > modelScore
            ? bestIndex
            : modelIndex;
    }

    private async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_client is not null)
        {
            return;
        }

        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (_client is not null)
            {
                return;
            }

            await EnsureModelCachedAsync(cancellationToken);
            if (!FoundryLocalManager.IsInitialized)
            {
                string cacheDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".foundry",
                    "cache",
                    "models");
                await FoundryLocalManager.CreateAsync(
                    new Configuration
                    {
                        AppName = "AsteriaScale",
                        ModelCacheDir = cacheDirectory
                    },
                    NullLogger.Instance);
            }

            _manager = FoundryLocalManager.Instance;
            EpInfo[] eps = _manager.DiscoverEps();
            string[] vitisProviders = eps
                .Where(ep => ep.Name.Contains("Vitis", StringComparison.OrdinalIgnoreCase))
                .Select(ep => ep.Name)
                .ToArray();
            if (vitisProviders.Length == 0)
            {
                throw new InvalidOperationException("AMD Vitis AI для NPU не найден.");
            }

            await _manager.DownloadAndRegisterEpsAsync(vitisProviders, cancellationToken);
            ICatalog catalog = await _manager.GetCatalogAsync(cancellationToken);
            _model ??= await catalog.GetModelVariantAsync(ModelId, cancellationToken)
                ?? throw new InvalidOperationException("NPU-модель умного словаря не найдена в локальном кэше.");
            await _model.LoadAsync(cancellationToken);
            _client = await _model.GetChatClientAsync(cancellationToken);
            _client.Settings.Temperature = 0;
            _client.Settings.MaxTokens = 20;
            _client.Settings.TopP = 0.1f;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private static async Task EnsureModelCachedAsync(CancellationToken cancellationToken)
    {
        string cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".foundry",
            "cache",
            "models",
            "Microsoft",
            "phi-3-mini-128k-instruct-vitis-npu-2");
        if (Directory.Exists(cacheDirectory))
        {
            return;
        }

        string foundry = ResolveFoundryPath();
        await RunCliAsync(foundry, ["model", "download", ModelId, "-o", "json"], cancellationToken);
        if (!Directory.Exists(cacheDirectory))
        {
            throw new InvalidOperationException("Foundry Local не сохранил NPU-модель после загрузки.");
        }

        try
        {
            await RunCliAsync(foundry, ["server", "stop", "-o", "json"], cancellationToken);
        }
        catch
        {
            // A download can finish without leaving the daemon running.
        }
    }

    private static async Task<string> RunCliAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Не удалось запустить Foundry Local.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        string output = await outputTask;
        string error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "Foundry Local: " + (string.IsNullOrWhiteSpace(error) ? output : error).Trim());
        }

        return output.Trim();
    }

    private static string ResolveFoundryPath()
    {
        string alias = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WindowsApps",
            "foundry.exe");
        if (File.Exists(alias))
        {
            return alias;
        }

        throw new InvalidOperationException("Для загрузки NPU-модели требуется Foundry Local.");
    }

    private static Candidate[] ReorderCandidates(string context, string target, IReadOnlyList<string> candidates)
    {
        return candidates
            .Select((text, index) => new Candidate(index, text))
            .ToArray();
    }

    private static string MarkTarget(string context, string target, int characterIndex)
    {
        string normalizedContext = Normalize(context);
        string normalizedTarget = Normalize(target);
        if (normalizedContext.Length == 0 || normalizedTarget.Length == 0)
        {
            return normalizedContext;
        }

        int best = -1;
        int bestDistance = int.MaxValue;
        int start = 0;
        while (start < normalizedContext.Length)
        {
            int index = normalizedContext.IndexOf(normalizedTarget, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                break;
            }

            int distance = Math.Abs(index + (normalizedTarget.Length / 2) - characterIndex);
            if (distance < bestDistance)
            {
                best = index;
                bestDistance = distance;
            }

            start = index + Math.Max(1, normalizedTarget.Length);
        }

        return best < 0
            ? normalizedContext + " <target>" + normalizedTarget + "</target>"
            : normalizedContext[..best] + "<target>" + normalizedContext.Substring(best, normalizedTarget.Length) +
              "</target>" + normalizedContext[(best + normalizedTarget.Length)..];
    }

    private static string StripCodeFence(string value)
    {
        string cleaned = value.Trim();
        if (!cleaned.StartsWith("```", StringComparison.Ordinal))
        {
            return cleaned;
        }

        int firstLine = cleaned.IndexOf('\n');
        int closing = cleaned.LastIndexOf("```", StringComparison.Ordinal);
        return firstLine >= 0 && closing > firstLine
            ? cleaned[(firstLine + 1)..closing].Trim()
            : cleaned;
    }

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();

    internal static string NormalizeCandidate(string? value) => Normalize(value);

    private static string NormalizeCachePart(string? value) =>
        Normalize(value).ToLowerInvariant().Trim('.', ',', '!', '?', ';', ':', '"', '\'', '«', '»');

    private static string ResolveSourceLanguage(string? requested, string context)
    {
        if (requested?.Contains("Japanese", StringComparison.OrdinalIgnoreCase) == true ||
            string.Equals(requested, "ja", StringComparison.OrdinalIgnoreCase))
        {
            return "Japanese";
        }

        if (requested?.Contains("English", StringComparison.OrdinalIgnoreCase) == true ||
            string.Equals(requested, "en", StringComparison.OrdinalIgnoreCase))
        {
            return "English";
        }

        return context.Any(character =>
            character is >= '\u3040' and <= '\u30ff' or
            >= '\u3400' and <= '\u9fff')
            ? "Japanese"
            : "English";
    }

    private static string[] RussianTokens(string value) => value
        .ToLowerInvariant()
        .Replace('ё', 'е')
        .Split(value.Where(character => !char.IsLetter(character)).Distinct().ToArray(),
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(token => token.Any(character => character is >= 'а' and <= 'я'))
        .ToArray();

    private static int CommonPrefixLength(string left, string right)
    {
        int length = Math.Min(left.Length, right.Length);
        int index = 0;
        while (index < length && left[index] == right[index])
        {
            index++;
        }

        return index;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await UnloadAsync();
        _manager?.Dispose();
        _manager = null;
        _model = null;
        _lifecycleLock.Dispose();
        _inferenceLock.Dispose();
    }

    internal sealed record ParsedSelection(int Index, double Confidence, string ContextTranslation);
    private sealed record Candidate(int OriginalIndex, string Text);
}
