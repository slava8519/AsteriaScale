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

public sealed class FoundryTranslationService : IAsyncDisposable
{
    public const string ModelId = "qwen3.5-2b-text-generic-cpu:1";
    public const string EngineLabel = "NPU OCR  •  QWEN LOCAL";
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
    private Uri? _endpoint;
    private string? _foundryPath;

    public FoundryTranslationService(LocalDictionaryService? dictionary = null)
        : this(dictionary, useTranslationScaffold: true, diagnosticSink: null)
    {
    }

    internal FoundryTranslationService(
        LocalDictionaryService? dictionary,
        bool useTranslationScaffold,
        Action<TranslationScaffoldDiagnostics>? diagnosticSink = null,
        TranslationScaffoldNeuralRanker? neuralRanker = null)
    {
        _dictionary = dictionary ?? new LocalDictionaryService();
        _ownsDictionary = dictionary is null;
        _useTranslationScaffold = useTranslationScaffold;
        _diagnosticSink = diagnosticSink;
        _translationGuard = new ResidualEnglishTranslationGuard(_dictionary);
        _scaffoldBuilder = useTranslationScaffold
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

    public Task<string> TranslateToRussianAsync(
        string source,
        CancellationToken cancellationToken) =>
        TranslateToRussianAsync(source, useNeuralRanker: false, cancellationToken);

    internal async Task<string> TranslateToRussianAsync(
        string source,
        bool useNeuralRanker,
        CancellationToken cancellationToken)
    {
        source = Normalize(source);
        if (source.Length == 0 || IsPredominantlyCyrillic(source))
        {
            return source;
        }

        TranslationScaffold scaffold = await BuildScaffoldFailOpenAsync(
            source,
            useNeuralRanker,
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
                ReportDiagnostics(source, scaffold.Terms, scaffold.Terms.Count,
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
                    useNeuralRanker: false,
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
                        ReportDiagnostics(source, scaffold.Terms, scaffold.Terms.Count,
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
                cancellationToken);
        string first = await CleanAndProtectAsync(source, firstRaw, cancellationToken);
        IReadOnlyList<ScaffoldTerm> initialMissing = FindMissingTermsFailOpen(first, scaffold.Terms);
        if (initialMissing.Count == 0)
        {
            ReportDiagnostics(source, scaffold.Terms, scaffold.Terms.Count,
                exactSentenceUsed: false, initialViolationCount: 0,
                retryIssued: false, finalViolationCount: 0);
            return new PreparedTranslationResult(first, Cacheable: first.Length > 0);
        }

        string retry;
        try
        {
            string retryRaw = await RequestSingleWithTransportRetriesAsync(
                source,
                scaffold,
                initialMissing,
                first,
                cancellationToken);
            retry = await CleanAndProtectAsync(source, retryRaw, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            HoverTranslationService.LogError(exception);
            ReportDiagnostics(source, scaffold.Terms,
                scaffold.Terms.Count - initialMissing.Count,
                exactSentenceUsed: false,
                initialViolationCount: initialMissing.Count,
                retryIssued: true,
                finalViolationCount: initialMissing.Count);
            return new PreparedTranslationResult(first, Cacheable: false);
        }

        IReadOnlyList<ScaffoldTerm> finalMissing = FindMissingTermsFailOpen(retry, scaffold.Terms);
        string selected = finalMissing.Count <= initialMissing.Count ? retry : first;
        int selectedMissingCount = Math.Min(finalMissing.Count, initialMissing.Count);
        ReportDiagnostics(source, scaffold.Terms,
            scaffold.Terms.Count - selectedMissingCount,
            exactSentenceUsed: false,
            initialViolationCount: initialMissing.Count,
            retryIssued: true,
            finalViolationCount: selectedMissingCount);
        if (selectedMissingCount > 0)
        {
            HoverTranslationService.LogError(new InvalidOperationException(
                $"Qwen translation violated {selectedMissingCount} glossary term(s) after one retry. " +
                $"Source: {source}"));
        }

        return new PreparedTranslationResult(
            selected,
            Cacheable: selected.Length > 0 && selectedMissingCount == 0);
    }

    private async Task<string> RequestSingleWithTransportRetriesAsync(
        string source,
        TranslationScaffold scaffold,
        IReadOnlyList<ScaffoldTerm>? violatedTerms,
        string? previousTranslation,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                string prompt = _useTranslationScaffold
                    ? BuildTranslationPrompt(source, scaffold, violatedTerms, previousTranslation)
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
        string? previousTranslation = null)
    {
        ArgumentNullException.ThrowIfNull(scaffold);
        _ = previousTranslation; // Echoing the failed text anchors small models on the same mistake.
        StringBuilder prompt = new(
            "Переведи реплику визуальной новеллы на русский. Только перевод, без пояснений.\n");
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
        bool useNeuralRanker,
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
                useNeuralRanker,
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

        StringBuilder key = new("qwen-scaffold\u001f");
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
        IReadOnlyList<ScaffoldTerm> terms,
        int satisfiedTermCount,
        bool exactSentenceUsed,
        int initialViolationCount,
        bool retryIssued,
        int finalViolationCount)
    {
        if (!_useTranslationScaffold || _diagnosticSink is null)
        {
            return;
        }

        try
        {
            int termCount = terms.Count;
            _diagnosticSink(new TranslationScaffoldDiagnostics(
                source,
                termCount,
                Math.Clamp(satisfiedTermCount, 0, termCount),
                exactSentenceUsed,
                initialViolationCount,
                retryIssued,
                finalViolationCount,
                terms.ToArray()));
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
        ReportDiagnostics(source, scaffold.Terms, scaffold.Terms.Count,
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
