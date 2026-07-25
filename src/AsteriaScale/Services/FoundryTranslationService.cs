using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AsteriaScale.Services;

internal enum TranslationScaffoldMode
{
    Disabled,
    Deterministic,
    GlossBert
}

public sealed class FoundryTranslationService : IAsyncDisposable
{
    public const string ModelId = "qwen3.5-2b-text-generic-cpu:1";
    public const string EngineLabel = "NPU OCR  •  GLOSSBERT+QWEN";
    public const string SmartDictionaryEngineLabel = "NPU OCR  •  CPU-КОНТЕКСТ";

    private static readonly JsonSerializerOptions BatchPromptJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly HttpClient _httpClient = new(new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SmartDictionarySelection> _selectionCache =
        new(StringComparer.Ordinal);
    private readonly LocalDictionaryService _dictionary;
    private readonly ResidualEnglishTranslationGuard _translationGuard;
    private readonly TranslationScaffoldBuilder? _scaffoldBuilder;
    private readonly Action<TranslationScaffoldDiagnostics>? _diagnosticSink;
    private readonly bool _ownsDictionary;
    private readonly bool _useTranslationScaffold;
    private readonly bool _useNeuralScaffold;
    private Uri? _endpoint;
    private string? _foundryPath;

    public FoundryTranslationService(LocalDictionaryService? dictionary = null)
        : this(dictionary, TranslationScaffoldMode.Deterministic, diagnosticSink: null)
    {
    }

    internal FoundryTranslationService(
        LocalDictionaryService? dictionary,
        TranslationScaffoldMode scaffoldMode,
        Action<TranslationScaffoldDiagnostics>? diagnosticSink = null,
        TranslationScaffoldNeuralRanker? neuralRanker = null)
    {
        if (scaffoldMode == TranslationScaffoldMode.GlossBert && neuralRanker is null)
        {
            throw new ArgumentNullException(
                nameof(neuralRanker),
                "GlossBERT scaffold mode requires a neural ranker.");
        }

        _dictionary = dictionary ?? new LocalDictionaryService();
        _ownsDictionary = dictionary is null;
        _useTranslationScaffold = scaffoldMode != TranslationScaffoldMode.Disabled;
        _useNeuralScaffold = scaffoldMode == TranslationScaffoldMode.GlossBert;
        _diagnosticSink = diagnosticSink;
        _translationGuard = new ResidualEnglishTranslationGuard(_dictionary);
        _scaffoldBuilder = _useTranslationScaffold
            ? new TranslationScaffoldBuilder(_dictionary, neuralRanker)
            : null;
    }

    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        if (_useTranslationScaffold)
        {
            await Task.WhenAll(
                EnsureInitializedAsync(cancellationToken),
                _dictionary.LoadSenseMetadataAsync(cancellationToken));
            return;
        }

        await EnsureInitializedAsync(cancellationToken);
    }

    public async Task<string> TranslateToRussianAsync(
        string source,
        CancellationToken cancellationToken)
    {
        source = Normalize(source);
        if (source.Length == 0 || IsPredominantlyCyrillic(source))
        {
            return source;
        }

        TranslationScaffold scaffold = await BuildScaffoldFailOpenAsync(
            source,
            neuralSession: null,
            cancellationToken);
        string cacheKey = CreateTranslationCacheKey(source, scaffold);
        if (_cache.TryGetValue(cacheKey, out string? cached))
        {
            ReportCachedDiagnostics(source, scaffold, cached);
            return cached;
        }

        if (!string.IsNullOrWhiteSpace(scaffold.ExactSentenceTranslation))
        {
            string exact = CleanTrustedExactOutput(scaffold.ExactSentenceTranslation);
            IReadOnlyList<ScaffoldTerm> missing = FindMissingTermsFailOpen(exact, scaffold.Terms);
            if (exact.Length > 0 && missing.Count == 0)
            {
                ReportDiagnostics(source, scaffold, scaffold.Terms.Count,
                    exactSentenceUsed: true, initialViolationCount: 0,
                    retryIssued: false, finalViolationCount: 0);
                CacheTranslation(cacheKey, exact);
                return exact;
            }
        }

        await EnsureInitializedAsync(cancellationToken);
        PreparedTranslationResult result = await TranslatePreparedAsync(
            source,
            scaffold,
            initialModelOutput: null,
            cancellationToken);
        if (result.Cacheable)
        {
            CacheTranslation(cacheKey, result.Translation);
        }

        return result.Translation;
    }

    public async Task<SmartDictionarySelection> SelectDictionaryMeaningAsync(
        string context,
        string target,
        int characterIndex,
        IReadOnlyList<string> candidates,
        string sourceLanguage,
        CancellationToken cancellationToken)
    {
        string[] normalizedCandidates = candidates
            .Select(Normalize)
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
        if (_selectionCache.TryGetValue(cacheKey, out SmartDictionarySelection? cached))
        {
            return cached;
        }

        await EnsureInitializedAsync(cancellationToken);
        string prompt =
            "You are a lexical-sense ranker, not a translator. Choose the single numbered Russian dictionary " +
            "meaning that fits the marked <target> in the source sentence. Use grammar and nearby context. " +
            "Candidate order has no priority. Never translate or explain. Return exactly one candidate number.\n" +
            "Source language: " + explicitLanguage + "\nSentence: " +
            MarkTarget(context, target, characterIndex) + "\nCandidates:\n" +
            string.Join("\n", normalizedCandidates.Select((candidate, index) => $"{index}: {candidate}")) +
            "\nAnswer:";
        string raw = await SendPromptAsync(prompt, 8, cancellationToken);
        NpuDictionarySelectorService.ParsedSelection parsed =
            NpuDictionarySelectorService.ParseSelectionResponse(raw, normalizedCandidates.Length);
        SmartDictionarySelection result = new(
            parsed.Index,
            normalizedCandidates[parsed.Index],
            parsed.Confidence,
            string.Empty);
        if (_selectionCache.Count >= 384)
        {
            _selectionCache.Clear();
        }

        _selectionCache[cacheKey] = result;
        return result;
    }

    public async Task<IReadOnlyList<string>> TranslateManyToRussianAsync(
        IReadOnlyList<string> sources,
        CancellationToken cancellationToken)
    {
        string[] normalized = sources.Select(Normalize).ToArray();
        string[] translations = new string[normalized.Length];
        List<BatchItem> pending = [];
        TranslationScaffoldNeuralSession? neuralSession = _useNeuralScaffold
            ? new TranslationScaffoldNeuralSession()
            : null;
        for (int index = 0; index < normalized.Length; index++)
        {
            string source = normalized[index];
            if (source.Length == 0 || IsPredominantlyCyrillic(source))
            {
                translations[index] = source;
            }
            else
            {
                TranslationScaffold scaffold = await BuildScaffoldFailOpenAsync(
                    source,
                    neuralSession,
                    cancellationToken);
                string cacheKey = CreateTranslationCacheKey(source, scaffold);
                if (_cache.TryGetValue(cacheKey, out string? cached))
                {
                    translations[index] = cached;
                    ReportCachedDiagnostics(source, scaffold, cached);
                }
                else if (!string.IsNullOrWhiteSpace(scaffold.ExactSentenceTranslation))
                {
                    string exact = CleanTrustedExactOutput(scaffold.ExactSentenceTranslation);
                    IReadOnlyList<ScaffoldTerm> missing = FindMissingTermsFailOpen(exact, scaffold.Terms);
                    if (exact.Length > 0 && missing.Count == 0)
                    {
                        ReportDiagnostics(source, scaffold, scaffold.Terms.Count,
                            exactSentenceUsed: true, initialViolationCount: 0,
                            retryIssued: false, finalViolationCount: 0);
                        translations[index] = exact;
                        CacheTranslation(cacheKey, exact);
                    }
                    else
                    {
                        pending.Add(new BatchItem(index, source, scaffold, cacheKey));
                    }
                }
                else
                {
                    pending.Add(new BatchItem(index, source, scaffold, cacheKey));
                }
            }
        }

        if (pending.Count == 0)
        {
            return translations;
        }

        await EnsureInitializedAsync(cancellationToken);
        foreach (IReadOnlyList<BatchItem> batch in CreateBatches(pending))
        {
            IReadOnlyDictionary<int, string> translated;
            try
            {
                translated = await RequestBatchTranslationAsync(batch, cancellationToken);
            }
            catch (JsonException)
            {
                translated = new Dictionary<int, string>();
            }

            foreach (BatchItem item in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? initialModelOutput = translated.TryGetValue(item.Id, out string? batchValue)
                    ? batchValue
                    : null;
                PreparedTranslationResult result = await TranslatePreparedAsync(
                    item.Source,
                    item.Scaffold,
                    initialModelOutput,
                    cancellationToken);
                translations[item.Id] = result.Translation;
                if (result.Cacheable)
                {
                    CacheTranslation(item.CacheKey, result.Translation);
                }
            }
        }

        return translations;
    }

    internal Task<string> ProtectTranslationAsync(
        string source,
        string translation,
        CancellationToken cancellationToken = default) =>
        _translationGuard.ProtectAsync(source, translation, cancellationToken);

    public async Task UnloadAsync()
    {
        await _initializeLock.WaitAsync();
        try
        {
            _endpoint = null;
            if (string.IsNullOrWhiteSpace(_foundryPath))
            {
                try
                {
                    _foundryPath = ResolveFoundryPath();
                }
                catch (InvalidOperationException)
                {
                    _endpoint = null;
                    return;
                }
            }

            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
            try
            {
                string serverJson = await RunCliAsync(
                    ["server", "status", "-o", "json"],
                    timeout.Token);
                if (!IsServerRunning(serverJson))
                {
                    return;
                }

                await RunCliAsync(["model", "unload", ModelId, "-o", "json"], timeout.Token);
            }
            catch
            {
                // Continue with the idle check: the model may already be unloaded.
            }

            try
            {
                string statusJson = await RunCliAsync(["status", "-o", "json"], timeout.Token);
                if (IsServerReadyAndIdle(statusJson))
                {
                    await RunCliAsync(["server", "stop", "-o", "json"], timeout.Token);
                }
            }
            catch
            {
                // Cleanup is best-effort. Never stop a server unless idle state was confirmed.
            }
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    internal static bool IsServerRunning(string serverStatusJson)
    {
        try
        {
            JsonNode? status = JsonNode.Parse(serverStatusJson);
            return status?["running"]?.GetValue<bool>() == true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    internal static bool IsServerReadyAndIdle(string statusJson)
    {
        try
        {
            JsonNode? status = JsonNode.Parse(statusJson);
            return status?["service"]?["ready"]?.GetValue<bool>() == true &&
                   status?["models"]?["loaded"]?.GetValue<int>() == 0;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_endpoint is not null)
        {
            return;
        }

        await _initializeLock.WaitAsync(cancellationToken);
        try
        {
            if (_endpoint is not null)
            {
                return;
            }

            _foundryPath ??= ResolveFoundryPath();
            await RunCliAsync(["server", "start", "-o", "json"], cancellationToken);
            await RunCliAsync(["model", "load", ModelId, "-o", "json"], cancellationToken);
            string statusJson = await RunCliAsync(["status", "-o", "json"], cancellationToken);
            JsonNode status = JsonNode.Parse(statusJson)
                ?? throw new InvalidOperationException("Foundry Local вернул пустой статус.");
            string? url = status["service"]?["webUrls"]?[0]?.GetValue<string>();
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? endpoint))
            {
                throw new InvalidOperationException("Foundry Local не сообщил адрес локального сервиса.");
            }

            _endpoint = endpoint;
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    private async Task<PreparedTranslationResult> TranslatePreparedAsync(
        string source,
        TranslationScaffold scaffold,
        string? initialModelOutput,
        CancellationToken cancellationToken)
    {
        string firstRaw = initialModelOutput ??
            await RequestSingleWithTransportRetriesAsync(
                source,
                scaffold,
                violatedTerms: null,
                previousTranslation: null,
                senseHints: null,
                cancellationToken);
        string first = await CleanAndProtectAsync(source, firstRaw, cancellationToken);
        first = await RetryEmptyTranslationOnceAsync(
            first,
            async retryCancellationToken =>
            {
                string retryRaw = await RequestSingleWithTransportRetriesAsync(
                    source,
                    scaffold,
                    violatedTerms: null,
                    previousTranslation: null,
                    senseHints: null,
                    retryCancellationToken);
                return await CleanAndProtectAsync(
                    source,
                    retryRaw,
                    retryCancellationToken);
            },
            cancellationToken);
        IReadOnlyList<ScaffoldTerm> initialMissing = FindMissingTermsFailOpen(first, scaffold.Terms);
        IReadOnlyList<ScaffoldSenseHint> initialSenseConflicts =
            FindSenseConflictsFailOpen(first, scaffold.SenseHints);
        if (initialMissing.Count == 0 && initialSenseConflicts.Count == 0)
        {
            ReportDiagnostics(source, scaffold, scaffold.Terms.Count,
                exactSentenceUsed: false, initialViolationCount: 0,
                retryIssued: false, finalViolationCount: 0,
                initialSenseConflictCount: 0, finalSenseConflictCount: 0);
            return new PreparedTranslationResult(first, Cacheable: first.Length > 0);
        }

        string retry;
        int retrySenseConflictCount = initialSenseConflicts.Count;
        bool senseReviewIssued = initialSenseConflicts.Count > 0 && initialMissing.Count == 0;
        TranslationSenseReviewOutcome senseReviewOutcome =
            TranslationSenseReviewOutcome.NotIssued;
        try
        {
            string retryRaw = await RequestSingleWithTransportRetriesAsync(
                source,
                scaffold,
                initialMissing,
                first,
                senseReviewIssued ? initialSenseConflicts : null,
                cancellationToken);
            if (senseReviewIssued)
            {
                if (ContainsFidelityPromptEcho(retryRaw))
                {
                    ReportDiagnostics(source, scaffold,
                        scaffold.Terms.Count - initialMissing.Count,
                        exactSentenceUsed: false,
                        initialViolationCount: initialMissing.Count,
                        retryIssued: true,
                        finalViolationCount: initialMissing.Count,
                        initialSenseConflictCount: initialSenseConflicts.Count,
                        finalSenseConflictCount: initialSenseConflicts.Count,
                        senseReviewOutcome: TranslationSenseReviewOutcome.RejectedResponse);
                    // The first text remains the safest visible fallback, but
                    // caching it would suppress semantic review on every later
                    // occurrence of the same line.
                    return new PreparedTranslationResult(first, Cacheable: false);
                }

                FidelityRetryResponse response = ParseFidelityRetryResponse(retryRaw);
                if (response.Action == FidelityRetryAction.Keep)
                {
                    if (initialMissing.Count == 0)
                    {
                        ReportDiagnostics(source, scaffold, scaffold.Terms.Count,
                            exactSentenceUsed: false,
                            initialViolationCount: 0,
                            retryIssued: true,
                            finalViolationCount: 0,
                            initialSenseConflictCount: initialSenseConflicts.Count,
                            finalSenseConflictCount: 0,
                            senseReviewOutcome: TranslationSenseReviewOutcome.KeptOriginal);
                        return new PreparedTranslationResult(first, Cacheable: first.Length > 0);
                    }

                    ReportDiagnostics(source, scaffold,
                        scaffold.Terms.Count - initialMissing.Count,
                        exactSentenceUsed: false,
                        initialViolationCount: initialMissing.Count,
                        retryIssued: true,
                        finalViolationCount: initialMissing.Count,
                        initialSenseConflictCount: initialSenseConflicts.Count,
                        finalSenseConflictCount: initialSenseConflicts.Count,
                        senseReviewOutcome: TranslationSenseReviewOutcome.RejectedResponse);
                    return new PreparedTranslationResult(first, Cacheable: false);
                }

                if (response.Action != FidelityRetryAction.Fix)
                {
                    ReportDiagnostics(source, scaffold,
                        scaffold.Terms.Count - initialMissing.Count,
                        exactSentenceUsed: false,
                        initialViolationCount: initialMissing.Count,
                        retryIssued: true,
                        finalViolationCount: initialMissing.Count,
                        initialSenseConflictCount: initialSenseConflicts.Count,
                        finalSenseConflictCount: initialSenseConflicts.Count,
                        senseReviewOutcome: TranslationSenseReviewOutcome.RejectedResponse);
                    return new PreparedTranslationResult(first, Cacheable: false);
                }

                retryRaw = response.Translation;
                retrySenseConflictCount = 0;
                senseReviewOutcome = TranslationSenseReviewOutcome.AppliedCorrection;
            }

            retry = await CleanAndProtectAsync(source, retryRaw, cancellationToken);
            if (senseReviewIssued &&
                !IsMeaningfulFidelityFix(first, retry))
            {
                retrySenseConflictCount = initialSenseConflicts.Count;
                senseReviewOutcome = TranslationSenseReviewOutcome.RejectedResponse;
            }
            else if (!senseReviewIssued)
            {
                retrySenseConflictCount = FindSenseConflictsFailOpen(
                    retry,
                    scaffold.SenseHints).Count;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            HoverTranslationService.LogError(exception);
            ReportDiagnostics(source, scaffold,
                scaffold.Terms.Count - initialMissing.Count,
                exactSentenceUsed: false,
                initialViolationCount: initialMissing.Count,
                retryIssued: true,
                finalViolationCount: initialMissing.Count,
                initialSenseConflictCount: initialSenseConflicts.Count,
                finalSenseConflictCount: initialSenseConflicts.Count,
                senseReviewOutcome: senseReviewIssued
                    ? TranslationSenseReviewOutcome.Failed
                    : TranslationSenseReviewOutcome.NotIssued);
            return new PreparedTranslationResult(first, Cacheable: false);
        }

        IReadOnlyList<ScaffoldTerm> finalMissing = FindMissingTermsFailOpen(retry, scaffold.Terms);
        bool selectRetry = ShouldSelectFidelityRetry(
            retry,
            initialMissing.Count,
            finalMissing.Count,
            initialSenseConflicts.Count,
            retrySenseConflictCount);
        string selected = selectRetry ? retry : first;
        int selectedMissingCount = selectRetry
            ? finalMissing.Count
            : initialMissing.Count;
        int selectedSenseConflictCount = selectRetry
            ? retrySenseConflictCount
            : initialSenseConflicts.Count;
        if (!selectRetry && senseReviewOutcome == TranslationSenseReviewOutcome.AppliedCorrection)
        {
            senseReviewOutcome = TranslationSenseReviewOutcome.RejectedResponse;
        }

        ReportDiagnostics(source, scaffold,
            scaffold.Terms.Count - selectedMissingCount,
            exactSentenceUsed: false,
            initialViolationCount: initialMissing.Count,
            retryIssued: true,
            finalViolationCount: selectedMissingCount,
            initialSenseConflictCount: initialSenseConflicts.Count,
            finalSenseConflictCount: selectedSenseConflictCount,
            senseReviewOutcome: senseReviewOutcome);
        if (selectedMissingCount > 0)
        {
            HoverTranslationService.LogError(new InvalidOperationException(
                $"Qwen translation violated {selectedMissingCount} glossary term(s) after one retry. " +
                $"Source: {source}"));
        }

        if (selectedSenseConflictCount > 0)
        {
            HoverTranslationService.LogError(new InvalidOperationException(
                $"Qwen translation left {selectedSenseConflictCount} GlossBERT sense conflict(s) " +
                $"unresolved after one review. Source: {source}"));
        }

        return new PreparedTranslationResult(
            selected,
            Cacheable: selected.Length > 0 &&
                       selectedMissingCount == 0 &&
                       selectedSenseConflictCount == 0);
    }

    internal static bool ShouldSelectFidelityRetry(
        string retryTranslation,
        int initialMissingCount,
        int retryMissingCount,
        int initialSenseConflictCount = 0,
        int retrySenseConflictCount = 0)
    {
        if (string.IsNullOrWhiteSpace(retryTranslation) ||
            retryMissingCount > initialMissingCount)
        {
            return false;
        }

        return retryMissingCount < initialMissingCount ||
               retrySenseConflictCount < initialSenseConflictCount;
    }

    internal static bool IsMeaningfulFidelityFix(
        string previousTranslation,
        string retryTranslation) =>
        !string.IsNullOrWhiteSpace(retryTranslation) &&
        !string.Equals(
            NormalizeCachePart(previousTranslation),
            NormalizeCachePart(retryTranslation),
            StringComparison.Ordinal);

    internal static async Task<string> RetryEmptyTranslationOnceAsync(
        string firstTranslation,
        Func<CancellationToken, Task<string>> retry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(retry);
        if (!string.IsNullOrWhiteSpace(firstTranslation))
        {
            return firstTranslation;
        }

        cancellationToken.ThrowIfCancellationRequested();
        string secondTranslation = await retry(cancellationToken);
        if (string.IsNullOrWhiteSpace(secondTranslation))
        {
            throw new InvalidOperationException(
                "Локальная модель перевода дважды вернула пустой ответ.");
        }

        return secondTranslation;
    }

    private async Task<string> RequestSingleWithTransportRetriesAsync(
        string source,
        TranslationScaffold scaffold,
        IReadOnlyList<ScaffoldTerm>? violatedTerms,
        string? previousTranslation,
        IReadOnlyList<ScaffoldSenseHint>? senseHints,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                string prompt = _useTranslationScaffold
                    ? BuildTranslationPrompt(
                        source,
                        scaffold,
                        violatedTerms,
                        previousTranslation,
                        senseHints)
                    : BuildLegacyTranslationPrompt(source);
                return await SendPromptAsync(prompt, 420, cancellationToken);
            }
            catch (HttpRequestException) when (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300 * (attempt + 1)), cancellationToken);
            }
        }
    }

    internal static string BuildTranslationPrompt(
        string source,
        TranslationScaffold scaffold,
        IReadOnlyList<ScaffoldTerm>? violatedTerms = null,
        string? previousTranslation = null,
        IReadOnlyList<ScaffoldSenseHint>? senseHints = null)
    {
        ArgumentNullException.ThrowIfNull(scaffold);
        if (senseHints is { Count: > 0 })
        {
            return BuildSenseReviewPrompt(
                source,
                scaffold,
                violatedTerms,
                previousTranslation,
                senseHints);
        }

        // Roadmap #14.2C: register fidelity is symmetric. Softening a blunt line
        // and spicing up a plain one are both mistranslations, and a model left
        // without the rule reliably drifts toward the polite reading.
        StringBuilder prompt = new(
            "Переведи реплику визуальной новеллы на русский. Только перевод, без пояснений.\n" +
            "Сохрани уровень грубости, разговорности и откровенности оригинала: не смягчай, " +
            "не цензурируй и не добавляй того, чего в оригинале нет.\n");
        if (violatedTerms is { Count: > 0 })
        {
            foreach (ScaffoldTerm term in violatedTerms)
            {
                prompt.Append("В прошлом переводе термин «")
                    .Append(NormalizePromptLine(term.Source))
                    .Append("» передан неверно. Обязательно используй «")
                    .Append(NormalizePromptLine(term.Translation))
                    .Append("».\n");
            }

            prompt.Append(
                "Переведи заново. Не заменяй обязательные русские слова синонимами " +
                "и не меняй их написание; разрешено только грамматическое склонение.\n");
        }

        if (scaffold.Terms.Count > 0)
        {
            prompt.Append("Обязательная терминология:\n");
            foreach (ScaffoldTerm term in scaffold.Terms)
            {
                prompt.Append("- ")
                    .Append(NormalizePromptLine(term.Source))
                    .Append(" → ")
                    .Append(NormalizePromptLine(term.Translation))
                    .Append('\n');
            }
        }

        prompt.Append("Реплика: ").Append(NormalizePromptLine(source));
        return prompt.ToString();
    }

    private static string BuildSenseReviewPrompt(
        string source,
        TranslationScaffold scaffold,
        IReadOnlyList<ScaffoldTerm>? violatedTerms,
        string? previousTranslation,
        IReadOnlyList<ScaffoldSenseHint> senseHints)
    {
        StringBuilder prompt = new(
            "Review a Russian translation of a visual-novel line.\nEnglish source: ");
        prompt.Append(NormalizePromptLine(source))
            .Append("\nPrevious Russian translation: ")
            .Append(NormalizePromptLine(previousTranslation ?? string.Empty))
            .Append('\n');
        if (scaffold.Terms.Count > 0)
        {
            prompt.Append("Required Russian terminology (synonyms are not allowed; inflection is allowed):\n");
            foreach (ScaffoldTerm term in scaffold.Terms)
            {
                prompt.Append("- ")
                    .Append(NormalizePromptLine(term.Source))
                    .Append(" -> ")
                    .Append(NormalizePromptLine(term.Translation))
                    .Append('\n');
            }
        }

        prompt.Append("Possible contextual senses (hypotheses, not required wording):\n");
        foreach (ScaffoldSenseHint hint in senseHints)
        {
            prompt.Append("- ")
                .Append(NormalizePromptLine(hint.Source))
                .Append(": ")
                .Append(NormalizePromptLine(hint.EnglishDefinition))
                .Append('\n');
        }

        prompt.Append(
            "Check each hypothesis against the complete English source. Russian synonyms and inflections are allowed. ");
        if (violatedTerms is { Count: > 0 })
        {
            prompt.Append("KEEP is forbidden because required terminology is missing. ");
        }
        else
        {
            prompt.Append(
                "If the previous translation already conveys the meaning naturally, or if a hypothesis does not fit " +
                "the source, output exactly KEEP. ");
        }

        prompt.Append(
            "Otherwise output exactly FIX| followed by the corrected Russian translation. Output no explanation.");
        return prompt.ToString();
    }

    private async Task<IReadOnlyDictionary<int, string>> RequestBatchTranslationAsync(
        IReadOnlyList<BatchItem> batch,
        CancellationToken cancellationToken)
    {
        string prompt = _useTranslationScaffold
            ? BuildBatchTranslationPrompt(batch)
            : BuildLegacyBatchTranslationPrompt(batch);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                string response = await SendPromptAsync(prompt, 1400, cancellationToken);
                IReadOnlyDictionary<int, string> parsed = ParseBatchResponse(response);
                if (parsed.Count != batch.Count ||
                    batch.Any(item => !parsed.ContainsKey(item.Id)))
                {
                    throw new JsonException(
                        "The translation model did not return exactly the requested ids.");
                }

                return parsed;
            }
            catch (HttpRequestException) when (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300 * (attempt + 1)), cancellationToken);
            }
        }
    }

    internal static string BuildBatchTranslationPrompt(IReadOnlyList<BatchItem> batch)
    {
        BatchPromptInput input = new(batch.Select(item => new BatchPromptItem(
            item.Id,
            item.Source,
            item.Scaffold.Terms.Count == 0
                ? null
                : item.Scaffold.Terms.Select(term => new BatchPromptTerm(
                    NormalizePromptLine(term.Source),
                    NormalizePromptLine(term.Translation))).ToArray())).ToArray());
        string json = JsonSerializer.Serialize(input, BatchPromptJsonOptions);
        return
            "Переведи каждую реплику из JSON на естественный русский. Только перевод, без пояснений. " +
            "Сохрани уровень грубости, разговорности и откровенности оригинала: не смягчай, " +
            "не цензурируй и не добавляй того, чего в оригинале нет. " +
            "Поле terminology содержит обязательные термины только для реплики с тем же id. " +
            "Не объединяй и не пропускай реплики. Верни только JSON строго в форме: " +
            "{\"translations\":[{\"id\":0,\"text\":\"русский перевод\"}]}. " +
            "Сохрани каждый id.\nINPUT:\n" + json;
    }

    private static string BuildLegacyTranslationPrompt(string source) =>
        "Translate the text between <text> tags into natural Russian. " +
        "Preserve its exact meaning, names, punctuation, emotion and level of politeness. " +
        "Do not answer or continue the text. Return only the Russian translation without quotes or notes.\n" +
        "<text>" + source + "</text>";

    private static string BuildLegacyBatchTranslationPrompt(IReadOnlyList<BatchItem> batch)
    {
        string input = JsonSerializer.Serialize(new
        {
            items = batch.Select(item => new { id = item.Id, text = item.Source })
        });
        return
            "Translate every JSON item into natural Russian. Preserve names, meaning, punctuation, emotion and politeness. " +
            "Do not merge, omit, answer or continue any item. Return JSON only in exactly this form: " +
            "{\"translations\":[{\"id\":0,\"text\":\"Russian translation\"}]}. " +
            "Keep each input id unchanged.\nINPUT:\n" + input;
    }

    private async Task<string> SendPromptAsync(
        string prompt,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        Uri endpoint = _endpoint
            ?? throw new InvalidOperationException("Локальный переводчик ещё не готов.");
        var body = new
        {
            model = ModelId,
            messages = new[] { new { role = "user", content = prompt } },
            temperature = 0,
            max_tokens = maxTokens
        };

        using HttpResponseMessage response = await _httpClient.PostAsJsonAsync(
            new Uri(endpoint, "/v1/chat/completions"),
            body,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? string.Empty;
    }

    internal static IReadOnlyDictionary<int, string> ParseBatchResponse(string response)
    {
        string cleaned = response.Trim();
        if (cleaned.StartsWith("```", StringComparison.Ordinal))
        {
            int firstNewLine = cleaned.IndexOf('\n');
            int closingFence = cleaned.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewLine >= 0 && closingFence > firstNewLine)
            {
                cleaned = cleaned[(firstNewLine + 1)..closingFence].Trim();
            }
        }

        int objectStart = cleaned.IndexOf('{');
        int arrayStart = cleaned.IndexOf('[');
        int start = objectStart < 0
            ? arrayStart
            : arrayStart < 0 ? objectStart : Math.Min(objectStart, arrayStart);
        if (start > 0)
        {
            cleaned = cleaned[start..];
        }

        JsonNode root = JsonNode.Parse(cleaned)
            ?? throw new JsonException("The translation model returned empty JSON.");
        if (root is not JsonObject rootObject || rootObject["translations"] is not JsonArray values)
        {
            throw new JsonException("The translation model returned no valid translations array.");
        }

        Dictionary<int, string> result = [];
        foreach (JsonNode? node in values)
        {
            if (node is not JsonObject item)
            {
                throw new JsonException("The translation model returned a malformed translation entry.");
            }

            if (item["id"] is not JsonValue idNode || !idNode.TryGetValue(out int id))
            {
                throw new JsonException("The translation model returned an invalid translation id.");
            }

            string? text = null;
            if (item["text"] is JsonValue textNode &&
                textNode.TryGetValue(out string? textValue) &&
                !string.IsNullOrWhiteSpace(textValue))
            {
                text = textValue;
            }

            if (text is null &&
                item["translation"] is JsonValue translationNode &&
                translationNode.TryGetValue(out string? translationValue) &&
                !string.IsNullOrWhiteSpace(translationValue))
            {
                text = translationValue;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                throw new JsonException("The translation model returned an empty translation.");
            }

            if (!result.TryAdd(id, text))
            {
                throw new JsonException("The translation model returned a duplicate translation id.");
            }
        }

        return result;
    }

    private static IEnumerable<IReadOnlyList<BatchItem>> CreateBatches(IReadOnlyList<BatchItem> items)
    {
        List<BatchItem> current = [];
        int characters = 0;
        foreach (BatchItem item in items)
        {
            if (current.Count > 0 &&
                (current.Count >= 8 || characters + item.EstimatedPromptLength > 2600))
            {
                yield return current.ToArray();
                current.Clear();
                characters = 0;
            }

            current.Add(item);
            characters += item.EstimatedPromptLength;
        }

        if (current.Count > 0)
        {
            yield return current.ToArray();
        }
    }

    private async Task<TranslationScaffold> BuildScaffoldFailOpenAsync(
        string source,
        TranslationScaffoldNeuralSession? neuralSession,
        CancellationToken cancellationToken)
    {
        if (_scaffoldBuilder is null)
        {
            return TranslationScaffold.Empty;
        }

        try
        {
            return await _scaffoldBuilder.BuildAsync(
                source,
                neuralSession,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            HoverTranslationService.LogError(exception);
            return TranslationScaffold.Empty;
        }
    }

    private static IReadOnlyList<ScaffoldTerm> FindMissingTermsFailOpen(
        string translation,
        IReadOnlyList<ScaffoldTerm> terms)
    {
        try
        {
            return SenseFidelityGuard.FindMissingTerms(translation, terms);
        }
        catch (Exception exception)
        {
            HoverTranslationService.LogError(exception);
            return [];
        }
    }

    private static IReadOnlyList<ScaffoldSenseHint> FindSenseConflictsFailOpen(
        string translation,
        IReadOnlyList<ScaffoldSenseHint> hints)
    {
        try
        {
            return NeuralSenseFidelityGuard.FindConflictingHints(translation, hints);
        }
        catch (Exception exception)
        {
            HoverTranslationService.LogError(exception);
            return [];
        }
    }

    internal static bool ContainsFidelityPromptEcho(string value) =>
        value.Contains("GlossBERT", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Возможные контекстные значения для проверки", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("служебные подсказки", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("В прошлом переводе значение", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Possible contextual senses", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Review a Russian translation", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Output no explanation", StringComparison.OrdinalIgnoreCase);

    internal static FidelityRetryResponse ParseFidelityRetryResponse(string value)
    {
        string normalized = Normalize(value).Trim('"', '\'', '«', '»');
        if (string.Equals(normalized, "KEEP", StringComparison.OrdinalIgnoreCase))
        {
            return new FidelityRetryResponse(FidelityRetryAction.Keep, string.Empty);
        }

        const string fixMarker = "FIX|";
        if (normalized.StartsWith(fixMarker, StringComparison.OrdinalIgnoreCase))
        {
            string translation = normalized[fixMarker.Length..].Trim();
            if (translation.Length > 0)
            {
                return new FidelityRetryResponse(FidelityRetryAction.Fix, translation);
            }
        }

        return new FidelityRetryResponse(FidelityRetryAction.Invalid, string.Empty);
    }

    private async Task<string> CleanAndProtectAsync(
        string source,
        string modelOutput,
        CancellationToken cancellationToken)
    {
        string cleaned = CleanModelOutput(modelOutput, source);
        return await ProtectTranslationAsync(source, cleaned, cancellationToken);
    }

    private string CreateTranslationCacheKey(string source, TranslationScaffold scaffold)
    {
        if (!_useTranslationScaffold)
        {
            return "qwen\u001f" + source;
        }

        StringBuilder key = new(_useNeuralScaffold
            ? "qwen-scaffold-glossbert\u001f"
            : "qwen-scaffold\u001f");
        key.Append(_dictionary.VersionToken).Append('\u001f').Append(source);
        foreach (ScaffoldTerm term in scaffold.Terms)
        {
            key.Append('\u001e')
                .Append(term.Source)
                .Append('\u001d')
                .Append(term.Translation)
                .Append('\u001d')
                .Append(term.Confidence.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }

        foreach (ScaffoldSenseHint hint in scaffold.SenseHints)
        {
            key.Append('\u001c')
                .Append(hint.Source)
                .Append('\u001d')
                .Append(hint.EnglishDefinition)
                .Append('\u001d')
                .Append(hint.SelectedTranslation)
                .Append('\u001d');
            foreach (string rejectedTranslation in hint.RejectedTranslations)
            {
                key.Append(rejectedTranslation).Append('\u001b');
            }

            key.Append('\u001d')
                .Append(hint.Confidence.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }

        key.Append('\u001f').Append(scaffold.ExactSentenceTranslation);
        return key.ToString();
    }

    private void CacheTranslation(string cacheKey, string translation)
    {
        if (translation.Length == 0)
        {
            return;
        }

        if (_cache.Count > 256)
        {
            _cache.Clear();
        }

        _cache[cacheKey] = translation;
    }

    private void ReportDiagnostics(
        string source,
        TranslationScaffold scaffold,
        int satisfiedTermCount,
        bool exactSentenceUsed,
        int initialViolationCount,
        bool retryIssued,
        int finalViolationCount,
        int initialSenseConflictCount = 0,
        int finalSenseConflictCount = 0,
        TranslationSenseReviewOutcome senseReviewOutcome =
            TranslationSenseReviewOutcome.NotIssued)
    {
        if (!_useTranslationScaffold || _diagnosticSink is null)
        {
            return;
        }

        try
        {
            int termCount = scaffold.Terms.Count;
            _diagnosticSink(new TranslationScaffoldDiagnostics(
                source,
                termCount,
                Math.Clamp(satisfiedTermCount, 0, termCount),
                exactSentenceUsed,
                initialViolationCount,
                retryIssued,
                finalViolationCount,
                initialSenseConflictCount,
                finalSenseConflictCount,
                senseReviewOutcome,
                scaffold.Terms.ToArray(),
                scaffold.NeuralDecisions.ToArray(),
                scaffold.SenseHints.ToArray()));
        }
        catch
        {
            // Benchmark diagnostics must not interfere with translation.
        }
    }

    private void ReportCachedDiagnostics(
        string source,
        TranslationScaffold scaffold,
        string cached)
    {
        bool exactSentenceUsed =
            !string.IsNullOrWhiteSpace(scaffold.ExactSentenceTranslation) &&
            string.Equals(
                cached,
                CleanTrustedExactOutput(scaffold.ExactSentenceTranslation),
                StringComparison.Ordinal);
        ReportDiagnostics(source, scaffold, scaffold.Terms.Count,
            exactSentenceUsed, initialViolationCount: 0,
            retryIssued: false, finalViolationCount: 0);
    }

    private static string NormalizePromptLine(string value) =>
        Normalize(RussianMorphology.RemoveStressMarks(value));

    private async Task<string> RunCliAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new(_foundryPath!)
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
        string windowsApps = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "WindowsApps");
        try
        {
            string? installed = Directory
                .EnumerateDirectories(windowsApps, "Microsoft.FoundryLocalCLI_*_x64__8wekyb3d8bbwe")
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => Path.Combine(path, "foundry.exe"))
                .FirstOrDefault(File.Exists);
            if (installed is not null)
            {
                return installed;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Fall through to the app execution alias.
        }

        string alias = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WindowsApps",
            "foundry.exe");
        if (File.Exists(alias))
        {
            return alias;
        }

        throw new InvalidOperationException("Foundry Local не установлен.");
    }

    private static string Normalize(string value) =>
        string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();

    private static string NormalizeCachePart(string value) =>
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
            int index = normalizedContext.IndexOf(
                normalizedTarget,
                start,
                StringComparison.OrdinalIgnoreCase);
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
            : normalizedContext[..best] + "<target>" +
              normalizedContext.Substring(best, normalizedTarget.Length) +
              "</target>" + normalizedContext[(best + normalizedTarget.Length)..];
    }

    private static bool IsPredominantlyCyrillic(string value)
    {
        int letters = 0;
        int cyrillic = 0;
        foreach (char character in value)
        {
            if (!char.IsLetter(character))
            {
                continue;
            }

            letters++;
            if (character is >= '\u0400' and <= '\u052F')
            {
                cyrillic++;
            }
        }

        return letters > 0 && cyrillic >= Math.Ceiling(letters * 0.7);
    }

    internal static string CleanModelOutput(string value, string? source = null)
    {
        string cleaned = Normalize(value).Trim('"', '\'', '«', '»');
        bool containsPromptEcho =
            cleaned.Contains("Обязательная терминология:", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Contains("В прошлом переводе термин", StringComparison.OrdinalIgnoreCase);
        int translationMarker = cleaned.LastIndexOf(
            "Перевод:",
            StringComparison.OrdinalIgnoreCase);
        if (translationMarker == 0 || translationMarker > 0 && containsPromptEcho)
        {
            string afterMarker = cleaned[(translationMarker + "Перевод:".Length)..].Trim();
            if (afterMarker.Length > 0)
            {
                cleaned = afterMarker;
            }
        }
        else
        {
            int echoedReply = cleaned.LastIndexOf("Реплика:", StringComparison.OrdinalIgnoreCase);
            if (echoedReply >= 0 && containsPromptEcho)
            {
                cleaned = cleaned[(echoedReply + "Реплика:".Length)..].Trim();
                string normalizedSource = Normalize(source ?? string.Empty);
                if (normalizedSource.Length > 0 &&
                    cleaned.StartsWith(normalizedSource, StringComparison.Ordinal) &&
                    (cleaned.Length == normalizedSource.Length ||
                     char.IsWhiteSpace(cleaned[normalizedSource.Length])))
                {
                    cleaned = cleaned[normalizedSource.Length..].Trim();
                }
            }
        }

        const string prefix = "Перевод:";
        if (cleaned.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[prefix.Length..].Trim();
        }

        return cleaned.Trim('"', '\'', '«', '»');
    }

    private static string CleanTrustedExactOutput(string value) =>
        Normalize(value).Trim('"', '\'', '«', '»');

    public async ValueTask DisposeAsync()
    {
        await UnloadAsync();
        if (_ownsDictionary)
        {
            await _dictionary.UnloadAsync();
        }

        _httpClient.Dispose();
        _initializeLock.Dispose();
    }

    private sealed record PreparedTranslationResult(string Translation, bool Cacheable);

    internal enum FidelityRetryAction
    {
        Invalid,
        Keep,
        Fix
    }

    internal sealed record FidelityRetryResponse(
        FidelityRetryAction Action,
        string Translation);

    internal sealed record BatchItem(
        int Id,
        string Source,
        TranslationScaffold Scaffold,
        string CacheKey)
    {
        public int EstimatedPromptLength => Source.Length + Scaffold.Terms.Sum(term =>
            term.Source.Length + term.Translation.Length + 12);
    }

    private sealed record BatchPromptInput(
        [property: JsonPropertyName("items")] IReadOnlyList<BatchPromptItem> Items);

    private sealed record BatchPromptItem(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("terminology")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<BatchPromptTerm>? Terminology);

    private sealed record BatchPromptTerm(
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("translation")] string Translation);

}
