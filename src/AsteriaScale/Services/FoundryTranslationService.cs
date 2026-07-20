using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AsteriaScale.Services;

public sealed class FoundryTranslationService : IAsyncDisposable
{
    public const string ModelId = "qwen3.5-2b-text-generic-gpu:3";
    public const string EngineLabel = "NPU OCR  •  QWEN LOCAL";
    public const string SmartDictionaryEngineLabel = "NPU OCR  •  GPU-КОНТЕКСТ";

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
    private readonly bool _ownsDictionary;
    private Uri? _endpoint;
    private string? _foundryPath;

    public FoundryTranslationService(LocalDictionaryService? dictionary = null)
    {
        _dictionary = dictionary ?? new LocalDictionaryService();
        _ownsDictionary = dictionary is null;
        _translationGuard = new ResidualEnglishTranslationGuard(_dictionary);
    }

    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
    }

    public async Task<string> TranslateToRussianAsync(string source, CancellationToken cancellationToken)
    {
        source = Normalize(source);
        if (source.Length == 0 || IsPredominantlyCyrillic(source))
        {
            return source;
        }

        if (_cache.TryGetValue(source, out string? cached))
        {
            return cached;
        }

        await EnsureInitializedAsync(cancellationToken);
        string translation = string.Empty;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                translation = await RequestTranslationAsync(source, cancellationToken);
                break;
            }
            catch (HttpRequestException) when (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300 * (attempt + 1)), cancellationToken);
            }
        }

        translation = CleanModelOutput(translation);
        translation = await ProtectTranslationAsync(source, translation, cancellationToken);
        if (translation.Length > 0)
        {
            if (_cache.Count > 256)
            {
                _cache.Clear();
            }

            _cache[source] = translation;
        }

        return translation;
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
            else if (_cache.TryGetValue(source, out string? cached))
            {
                translations[index] = cached;
            }
            else
            {
                pending.Add(new BatchItem(index, source));
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
                string value = translated.TryGetValue(item.Id, out string? batchValue)
                    ? CleanModelOutput(batchValue)
                    : string.Empty;
                if (value.Length == 0)
                {
                    value = CleanModelOutput(await RequestTranslationAsync(item.Source, cancellationToken));
                }

                value = await ProtectTranslationAsync(item.Source, value, cancellationToken);
                translations[item.Id] = value;
                if (value.Length > 0)
                {
                    _cache[item.Source] = value;
                }
            }
        }

        if (_cache.Count > 256)
        {
            _cache.Clear();
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

    private async Task<string> RequestTranslationAsync(string source, CancellationToken cancellationToken)
    {
        string prompt = "Translate the text between <text> tags into natural Russian. " +
                        "Preserve its exact meaning, names, punctuation, emotion and level of politeness. " +
                        "Do not answer or continue the text. Return only the Russian translation without quotes or notes.\n" +
                        "<text>" + source + "</text>";
        return await SendPromptAsync(prompt, 420, cancellationToken);
    }

    private async Task<IReadOnlyDictionary<int, string>> RequestBatchTranslationAsync(
        IReadOnlyList<BatchItem> batch,
        CancellationToken cancellationToken)
    {
        string input = JsonSerializer.Serialize(new
        {
            items = batch.Select(item => new { id = item.Id, text = item.Source })
        });
        string prompt =
            "Translate every JSON item into natural Russian. Preserve names, meaning, punctuation, emotion and politeness. " +
            "Do not merge, omit, answer or continue any item. Return JSON only in exactly this form: " +
            "{\"translations\":[{\"id\":0,\"text\":\"Russian translation\"}]}. " +
            "Keep each input id unchanged.\nINPUT:\n" + input;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                string response = await SendPromptAsync(prompt, 1400, cancellationToken);
                return ParseBatchResponse(response);
            }
            catch (HttpRequestException) when (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300 * (attempt + 1)), cancellationToken);
            }
        }
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
        JsonArray? values = root as JsonArray ?? root["translations"] as JsonArray ?? root["items"] as JsonArray;
        if (values is null)
        {
            throw new JsonException("The translation model returned no translation array.");
        }

        Dictionary<int, string> result = [];
        foreach (JsonNode? node in values)
        {
            if (node is not JsonObject item)
            {
                continue;
            }

            int? id = item["id"]?.GetValue<int>();
            string? text = item["text"]?.GetValue<string>() ?? item["translation"]?.GetValue<string>();
            if (id is not null && !string.IsNullOrWhiteSpace(text))
            {
                result[id.Value] = text;
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
            if (current.Count > 0 && (current.Count >= 8 || characters + item.Source.Length > 2600))
            {
                yield return current.ToArray();
                current.Clear();
                characters = 0;
            }

            current.Add(item);
            characters += item.Source.Length;
        }

        if (current.Count > 0)
        {
            yield return current.ToArray();
        }
    }

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

    private static string CleanModelOutput(string value)
    {
        string cleaned = Normalize(value).Trim('"', '\'', '«', '»');
        const string prefix = "Перевод:";
        if (cleaned.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[prefix.Length..].Trim();
        }

        return cleaned;
    }

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

    private sealed record BatchItem(int Id, string Source);
}
