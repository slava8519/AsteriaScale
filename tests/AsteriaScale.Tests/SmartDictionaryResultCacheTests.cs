using System.Text.Json;
using AsteriaScale.Services;

internal static class SmartDictionaryResultCacheTests
{
    public static async Task RunAsync(string tempRoot)
    {
        string root = Path.Combine(tempRoot, "smart-result-cache");
        Directory.CreateDirectory(root);

        await VerifyKeyNormalizationAsync(root);
        await VerifyPersistenceAndValidationAsync(root);
        await VerifyLargeCandidateInventoryAsync(root);
        await VerifyLruEvictionAsync(root);
        await VerifyConcurrentBoundedWritesAsync(root);
        await VerifyCorruptFileRecoveryAsync(root);
    }

    private static async Task VerifyLargeCandidateInventoryAsync(string root)
    {
        string path = Path.Combine(root, "large-inventory.json");
        string[] candidates = Enumerable.Range(0, 16)
            .Select(index => $"значение {index}")
            .ToArray();
        SmartDictionaryCacheRequest request = CreateRequest(
            "The ambiguous word appeared in context.",
            "word",
            candidates);

        await using (SmartDictionaryResultCache cache = new(path, capacity: 4))
        {
            Check(await cache.StoreAsync(
                    request,
                    new SmartDictionarySelection(
                        11,
                        candidates[11],
                        0.91,
                        string.Empty)),
                "A valid selection beyond the legacy eight-candidate limit was rejected.");
        }

        await using SmartDictionaryResultCache reopened = new(path, capacity: 4);
        SmartDictionarySelection? hit = await reopened.TryGetAsync(request);
        Check(hit is { Index: 11 } && hit.Translation == candidates[11],
            "A large sense inventory did not survive cache restart.");
    }

    private static Task VerifyKeyNormalizationAsync(string root)
    {
        const string context =
            "alpha beta gamma delta epsilon zeta one two three four five bank six seven eight nine ten.";
        SmartDictionaryCacheRequest request = CreateRequest(
            context,
            "bank",
            ["банк", "берег"]);
        SmartDictionaryCacheRequest equivalent = request with
        {
            Context =
                "OMEGA SIGMA THETA KAPPA LAMBDA ZETA  one  two three four five BANK six seven eight nine ten.",
            CharacterIndex =
                "OMEGA SIGMA THETA KAPPA LAMBDA ZETA  one  two three four five BANK six seven eight nine ten."
                    .IndexOf("BANK", StringComparison.Ordinal),
            SourceLanguage = "EN",
            Backend = " NPU ",
            ModelId = request.ModelId.ToUpperInvariant(),
            DictionaryVersion = request.DictionaryVersion.ToUpperInvariant(),
            Candidates = ["БАНК", "БЕРЕГ"]
        };

        string first = SmartDictionaryResultCache.CreateKeyFingerprint(request);
        string second = SmartDictionaryResultCache.CreateKeyFingerprint(equivalent);
        Check(first == second,
            "Equivalent language names, casing, whitespace and distant context must share a key.");
        Check(first != SmartDictionaryResultCache.CreateKeyFingerprint(
                request with { Backend = "gpu" }),
            "Backend must participate in the cache key.");
        Check(first != SmartDictionaryResultCache.CreateKeyFingerprint(
                request with { ModelId = "another-model:1" }),
            "Model id must participate in the cache key.");
        Check(first != SmartDictionaryResultCache.CreateKeyFingerprint(
                request with { RankingPipeline = "bi+cross" }),
            "The actual cross/bi+cross pipeline must participate in the cache key.");
        Check(first != SmartDictionaryResultCache.CreateKeyFingerprint(
                request with { DictionaryVersion = "dictionary-v2" }),
            "Dictionary version must participate in the cache key.");
        Check(first != SmartDictionaryResultCache.CreateKeyFingerprint(
                request with { Candidates = ["берег", "банк"] }),
            "Candidate order must participate in the cache key.");

        string window = SmartDictionaryResultCache.CreateLocalContextWindow(
            request.Context,
            request.Target,
            request.CharacterIndex);
        Check(window.Contains("<target>bank</target>", StringComparison.Ordinal),
            "The selected occurrence must be marked inside the local context window.");
        Check(!window.Contains("alpha", StringComparison.Ordinal),
            "Distant dialogue must not inflate or invalidate the local-context key.");
        return Task.CompletedTask;
    }

    private static async Task VerifyPersistenceAndValidationAsync(string root)
    {
        string path = Path.Combine(root, "persistent.json");
        SmartDictionaryCacheRequest request = CreateRequest(
            "We sat on the bank of the river.",
            "bank",
            ["банк", "берег"]);

        await using (SmartDictionaryResultCache cache = new(path, capacity: 8))
        {
            bool invalidStored = await cache.StoreAsync(
                request,
                new SmartDictionarySelection(1, "журавль", 0.9, string.Empty));
            Check(!invalidStored, "A translation outside the candidate list must never be cached.");

            bool stored = await cache.StoreAsync(
                request,
                new SmartDictionarySelection(1, "берег", 0.87, "На берегу реки."));
            Check(stored, "A valid Smart Dictionary selection was rejected.");
        }

        Check(File.Exists(path), "The Smart Dictionary cache was not persisted.");
        await using (SmartDictionaryResultCache reopened = new(path, capacity: 8))
        {
            SmartDictionarySelection? hit = await reopened.TryGetAsync(request);
            Check(hit is { Index: 1, Translation: "берег" } &&
                  Math.Abs(hit.Confidence - 0.87) < 0.001,
                "A valid selection did not survive cache restart.");
        }

        string poisonedPath = Path.Combine(root, "poisoned.json");
        string key = SmartDictionaryResultCache.CreateKeyFingerprint(request);
        await File.WriteAllTextAsync(
            poisonedPath,
            JsonSerializer.Serialize(new
            {
                version = 2,
                entries = new[]
                {
                    new
                    {
                        key,
                        index = 1,
                        translation = "журавль",
                        confidence = 1,
                        contextTranslation = "",
                        lastAccess = 1
                    }
                }
            }));
        await using (SmartDictionaryResultCache poisoned = new(poisonedPath, capacity: 8))
        {
            SmartDictionarySelection? hit = await poisoned.TryGetAsync(request);
            Check(hit is null, "A persisted value outside the current candidates must be rejected.");
        }
    }

    private static async Task VerifyLruEvictionAsync(string root)
    {
        string path = Path.Combine(root, "lru.json");
        SmartDictionaryCacheRequest first = CreateRequest(
            "We sat on the bank of the river.", "bank", ["банк", "берег"]);
        SmartDictionaryCacheRequest second = CreateRequest(
            "A bat flew out of the cave.", "bat", ["бита", "летучая мышь"]);
        SmartDictionaryCacheRequest third = CreateRequest(
            "The crane spread its wings.", "crane", ["кран", "журавль"]);

        await using (SmartDictionaryResultCache cache = new(path, capacity: 2))
        {
            Check(await cache.StoreAsync(
                    first,
                    new SmartDictionarySelection(1, "берег", 0.9, string.Empty)),
                "Could not seed first LRU value.");
            Check(await cache.StoreAsync(
                    second,
                    new SmartDictionarySelection(1, "летучая мышь", 0.9, string.Empty)),
                "Could not seed second LRU value.");
            Check(await cache.TryGetAsync(first) is not null, "Could not touch the first LRU value.");
            Check(await cache.StoreAsync(
                    third,
                    new SmartDictionarySelection(1, "журавль", 0.9, string.Empty)),
                "Could not seed third LRU value.");
        }

        await using SmartDictionaryResultCache reopened = new(path, capacity: 2);
        Check(await reopened.TryGetAsync(first) is not null,
            "The recently accessed entry was incorrectly evicted.");
        Check(await reopened.TryGetAsync(second) is null,
            "The least recently used entry was not evicted.");
        Check(await reopened.TryGetAsync(third) is not null,
            "The newest LRU entry was incorrectly evicted.");
    }

    private static async Task VerifyConcurrentBoundedWritesAsync(string root)
    {
        string path = Path.Combine(root, "concurrent.json");
        const long maximumBytes = 2_048;
        await using (SmartDictionaryResultCache cache = new(
                         path,
                         capacity: 32,
                         maximumFileBytes: maximumBytes))
        {
            Task<bool>[] writes = Enumerable.Range(0, 24)
                .Select(index =>
                {
                    string target = "word" + index;
                    SmartDictionaryCacheRequest request = CreateRequest(
                        $"prefix dialogue {target} suffix dialogue",
                        target,
                        [$"вариант {index}", $"значение {index}"]);
                    return cache.StoreAsync(
                        request,
                        new SmartDictionarySelection(
                            1,
                            $"значение {index}",
                            0.8,
                            new string('x', 300)));
                })
                .ToArray();
            bool[] results = await Task.WhenAll(writes);
            Check(results.All(value => value), "Concurrent valid writes were rejected.");
        }

        Check(new FileInfo(path).Length <= maximumBytes,
            "The persistent Smart Dictionary cache exceeded its byte budget.");

        await using SmartDictionaryResultCache readable = new(
            path,
            capacity: 32,
            maximumFileBytes: maximumBytes);
        SmartDictionaryCacheRequest newest = CreateRequest(
            "prefix dialogue word23 suffix dialogue",
            "word23",
            ["вариант 23", "значение 23"]);
        Check(await readable.TryGetAsync(newest) is not null,
            "The bounded cache file was not readable after concurrent atomic writes.");
    }

    private static async Task VerifyCorruptFileRecoveryAsync(string root)
    {
        string path = Path.Combine(root, "corrupt.json");
        await File.WriteAllTextAsync(path, "{\"version\":1,\"entries\":[");
        SmartDictionaryCacheRequest request = CreateRequest(
            "A bat flew out of the cave.",
            "bat",
            ["бита", "летучая мышь"]);
        await using (SmartDictionaryResultCache cache = new(path, capacity: 4))
        {
            Check(await cache.TryGetAsync(request) is null,
                "A corrupt cache file must be ignored.");
            Check(await cache.StoreAsync(
                    request,
                    new SmartDictionarySelection(1, "летучая мышь", 0.9, string.Empty)),
                "The cache did not recover after a corrupt file.");
        }

        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Check(document.RootElement.GetProperty("version").GetInt32() == 2,
            "A corrupt cache was not replaced atomically with a valid document.");
    }

    private static SmartDictionaryCacheRequest CreateRequest(
        string context,
        string target,
        IReadOnlyList<string> candidates) =>
        new(
            context,
            target,
            context.IndexOf(target, StringComparison.OrdinalIgnoreCase),
            candidates,
            "English",
            "npu",
            NpuDictionarySelectorService.ModelId,
            "dictionary-v1");

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
