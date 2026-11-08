using System.Text.Json;
using AsteriaScale.Services;

internal static class SmartDictionaryResultCacheTests
{
    public static async Task RunAsync(string tempRoot)
    {
        string root = Path.Combine(tempRoot, "smart-result-cache");
        Directory.CreateDirectory(root);

        await VerifyKeyNormalizationAsync(root);
        await VerifyExactTextKeyAsync();
        await VerifyConsecKeyExactnessAsync();
        await VerifyPersistenceAndValidationAsync(root);
        await VerifyRegisterLabelIdentityAsync(root);
        await VerifyLiteraryRegisterPreservesGroupIndexAsync(root);
        await VerifyLargeCandidateInventoryAsync(root);
        await VerifyLruEvictionAsync(root);
        await VerifyConcurrentBoundedWritesAsync(root);
        await VerifyCorruptFileRecoveryAsync(root);
        await VerifyFailedWriteRecoveryAsync(root);
    }

    /// <summary>
    /// A background write that fails persisted nothing, so the entries are still
    /// pending. The store has to keep them dirty, retry on its own and keep
    /// evicting in the meantime: the caller only ever schedules a write on the
    /// clean-to-dirty edge, so a store left dirty by a failure would never write
    /// again and would grow past capacity for the rest of the session.
    /// </summary>
    private static async Task VerifyFailedWriteRecoveryAsync(string root)
    {
        string path = Path.Combine(root, "failed-write-recovery.json");
        SmartDictionaryCacheRequest[] requests =
        [
            CreateRequest("The first ambiguous word appeared here.", "word", ["первое", "второе"]),
            CreateRequest("A second ambiguous bank appeared here.", "bank", ["банк", "берег"]),
            CreateRequest("A third ambiguous spring appeared here.", "spring", ["весна", "пружина"]),
            CreateRequest("A fourth ambiguous crane appeared here.", "crane", ["журавль", "кран"])
        ];

        await using SmartDictionaryResultCache cache = new(path, capacity: 2);
        Check(await cache.StoreAsync(requests[0], Selection(requests[0])),
            "The first selection was rejected.");
        await WaitUntilAsync(
            () => File.Exists(path),
            TimeSpan.FromSeconds(5),
            "The debounced write never created the cache file.");

        // Holding the destination open makes the atomic replace fail exactly the
        // way a locked file or a full disk would.
        using (FileStream blocked = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            foreach (SmartDictionaryCacheRequest request in requests[1..])
            {
                Check(await cache.StoreAsync(request, Selection(request)),
                    "A selection was rejected while persistence was failing.");
            }

            // Long enough for the debounced write to run and fail at least once.
            await Task.Delay(TimeSpan.FromMilliseconds(1200));
        }

        // Nothing mutates the cache from here on: only the store's own retry can
        // move the file forward, and eviction must have run despite the failure.
        await WaitUntilAsync(
            () => EntryCount(path) == 2,
            TimeSpan.FromSeconds(20),
            "A failed background write was never retried, or eviction was skipped " +
            $"while the store stayed dirty (entries on disk: {EntryCount(path)}).");

        SmartDictionarySelection? newest = await cache.TryGetAsync(requests[^1]);
        Check(newest is { Index: 0 }, "The most recent entry was evicted instead of the oldest.");
    }

    private static SmartDictionarySelection Selection(SmartDictionaryCacheRequest request) =>
        new(0, request.Candidates[0], 0.9, string.Empty);

    private static int EntryCount(string path)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
            return document.RootElement.TryGetProperty("entries", out JsonElement entries)
                ? entries.GetArrayLength()
                : -1;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return -1;
        }
    }

    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        TimeSpan timeout,
        string failureMessage)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!predicate() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Check(predicate(), failureMessage);
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

    private static async Task VerifyRegisterLabelIdentityAsync(string root)
    {
        string path = Path.Combine(root, "register-label.json");
        SmartDictionaryCacheRequest request = CreateRequest(
            "He was pissed about it.",
            "pissed",
            ["пьяный", "взбешённый (ГРУБ.)"]);

        await using (SmartDictionaryResultCache cache = new(path, capacity: 4))
        {
            Check(await cache.StoreAsync(
                    request,
                    new SmartDictionarySelection(
                        1,
                        "взбешённый",
                        0.93,
                        string.Empty)),
                "A register-labelled outcome was rejected by the hover cache.");
        }

        await using SmartDictionaryResultCache reopened = new(path, capacity: 4);
        SmartDictionarySelection? hit = await reopened.TryGetAsync(request);
        Check(hit is { Index: 1, Translation: "взбешённый" },
            "A cached register-labelled outcome lost its clean Russian surface.");
    }

    private static async Task VerifyLiteraryRegisterPreservesGroupIndexAsync(string root)
    {
        string path = Path.Combine(root, "literary-register-index.json");
        SmartDictionaryRankerOutcome[] outcomes =
        [
            new(0, 0, "лик") { Register = SenseRegister.Neutral },
            new(1, 1, "лик") { Register = SenseRegister.Literary }
        ];
        string[] candidates = outcomes.Select(outcome => outcome.Describe()).ToArray();
        SmartDictionaryCacheRequest request = CreateRequest(
            "Her countenance appeared in the portrait.",
            "countenance",
            candidates);

        Check(candidates.Distinct(StringComparer.OrdinalIgnoreCase).Count() == outcomes.Length,
            "Literary and neutral outcomes with the same surface collapsed before ranking.");

        await using (SmartDictionaryResultCache cache = new(path, capacity: 4))
        {
            Check(await cache.StoreAsync(
                    request,
                    new SmartDictionarySelection(1, "лик", 0.91, string.Empty)),
                "The literary outcome index was rejected after candidate normalization.");
        }

        await using SmartDictionaryResultCache reopened = new(path, capacity: 4);
        SmartDictionarySelection? hit = await reopened.TryGetAsync(request);
        Check(hit is { Index: 1, Translation: "лик" },
            "The literary outcome lost its GroupId-aligned index or clean surface.");
    }

    private static Task VerifyKeyNormalizationAsync(string root)
    {
        const string context =
            "alpha beta gamma delta epsilon zeta one two three four five bank six seven eight nine ten.";
        SmartDictionaryCacheRequest request = CreateRequest(
            context,
            "bank",
            ["банк", "берег"]);
        // Same sentence, only spelling of the request metadata differs.
        const string equivalentContext =
            "alpha beta gamma delta epsilon zeta  one  two three four five BANK six seven eight nine ten.";
        SmartDictionaryCacheRequest equivalent = request with
        {
            Context = equivalentContext,
            CharacterIndex = equivalentContext.IndexOf("BANK", StringComparison.Ordinal),
            SourceLanguage = "EN",
            Backend = " NPU ",
            DictionaryVersion = request.DictionaryVersion.ToUpperInvariant(),
            Candidates = ["БАНК", "БЕРЕГ"]
        };

        string first = SmartDictionaryResultCache.CreateKeyFingerprint(request);
        string second = SmartDictionaryResultCache.CreateKeyFingerprint(equivalent);
        Check(first == second,
            "Equivalent language names, casing and whitespace must share a key.");

        // Roadmap #14.2B changed this contract deliberately. The window now
        // matches the ranker's own input, so context the model can see must
        // separate keys: previously a +-5 word radius let two different replies
        // reuse each other's verdict while the model had scored both in full.
        SmartDictionaryCacheRequest differentOpening = request with
        {
            Context =
                "OMEGA SIGMA THETA KAPPA LAMBDA ZETA one two three four five bank six seven eight nine ten.",
            CharacterIndex =
                "OMEGA SIGMA THETA KAPPA LAMBDA ZETA one two three four five bank six seven eight nine ten."
                    .IndexOf("bank", StringComparison.Ordinal)
        };
        Check(first != SmartDictionaryResultCache.CreateKeyFingerprint(differentOpening),
            "Context inside the ranker's own window must separate cache keys.");

        // The key may not merge inputs the tokenizer keeps apart. Compatibility
        // normalization folded the "fi" ligature into two letters while the
        // worker still encoded the original character, so two texts with
        // different token ids shared one verdict.
        const string ligatureContext =
            "alpha beta gamma delta epsilon zeta one two three four ﬁve bank six seven eight nine ten.";
        SmartDictionaryCacheRequest ligature = request with
        {
            Context = ligatureContext,
            CharacterIndex = ligatureContext.IndexOf("bank", StringComparison.Ordinal)
        };
        Check(first != SmartDictionaryResultCache.CreateKeyFingerprint(ligature),
            "A compatibility ligature the tokenizer sees as different shared a cache key.");

        // BertNormalizer clean_text deletes VT, FF and NEL instead of turning
        // them into spaces. .NET classifies all three as whitespace, so a broad
        // whitespace collapse used to serve the spaced sentence's verdict for
        // a joined token sequence the worker actually scored differently.
        string joinedControlContext = context.Replace(
            "alpha beta",
            "alphabeta",
            StringComparison.Ordinal);
        SmartDictionaryCacheRequest joinedControl = request with
        {
            Context = joinedControlContext,
            CharacterIndex = joinedControlContext.IndexOf("bank", StringComparison.Ordinal)
        };
        string joinedControlKey = SmartDictionaryResultCache.CreateKeyFingerprint(joinedControl);
        foreach (char preservedWhitespace in
                 new[] { '\t', '\n', '\r', '\u00A0', '\u2028', '\u2029' })
        {
            string separatedContext = context.Replace(
                "alpha beta",
                $"alpha{preservedWhitespace}beta",
                StringComparison.Ordinal);
            SmartDictionaryCacheRequest separated = request with
            {
                Context = separatedContext,
                CharacterIndex = separatedContext.IndexOf("bank", StringComparison.Ordinal)
            };
            Check(SmartDictionaryResultCache.CreateKeyFingerprint(separated) == first,
                $"BertNormalizer whitespace U+{(int)preservedWhitespace:X4} did not map to a space.");
        }

        foreach (char deletedControl in new[] { '\u000B', '\u000C', '\u0085' })
        {
            string controlledContext = context.Replace(
                "alpha beta",
                $"alpha{deletedControl}beta",
                StringComparison.Ordinal);
            SmartDictionaryCacheRequest controlled = request with
            {
                Context = controlledContext,
                CharacterIndex = controlledContext.IndexOf("bank", StringComparison.Ordinal)
            };
            string controlledKey = SmartDictionaryResultCache.CreateKeyFingerprint(controlled);
            Check(controlledKey == joinedControlKey,
                $"BertNormalizer-deleted U+{(int)deletedControl:X4} was not removed from the result-cache key.");
            Check(controlledKey != first,
                $"BertNormalizer-deleted U+{(int)deletedControl:X4} collided with a preserved space.");
        }

        // Beyond the window the model genuinely cannot see the difference, so
        // those requests still share a key. "Distant" is now defined by the
        // token budget instead of an arbitrary radius.
        // Half the budget on each side of the target, so the window is filled
        // entirely from the shared middle and the differing opening lies beyond
        // it whichever way the symmetric expansion resolves.
        int flank = TargetCenteredContextBuilder.MaximumWords / 2;
        string near = string.Join(
            " ",
            Enumerable.Range(0, flank).Select(index => "near" + index));
        string after = string.Join(
            " ",
            Enumerable.Range(0, flank).Select(index => "after" + index));
        string shared = near + " bank " + after + ".";
        string longContext = "alpha beta gamma delta " + shared;
        string longVariant = "omega sigma theta kappa " + shared;
        SmartDictionaryCacheRequest longRequest = request with
        {
            Context = longContext,
            CharacterIndex = longContext.IndexOf("bank", StringComparison.Ordinal)
        };
        SmartDictionaryCacheRequest longVariantRequest = request with
        {
            Context = longVariant,
            CharacterIndex = longVariant.IndexOf("bank", StringComparison.Ordinal)
        };
        Check(SmartDictionaryResultCache.CreateKeyFingerprint(longRequest) ==
              SmartDictionaryResultCache.CreateKeyFingerprint(longVariantRequest),
            "Context outside the ranker's window must not separate cache keys.");
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
                request with { RankingPipeline = "consec-independent" }),
            "The ConSeC pipeline must participate in the cache key.");
        bool removedPipelineRejected = false;
        try
        {
            _ = SmartDictionaryResultCache.CreateKeyFingerprint(
                request with { RankingPipeline = "sandwich" });
        }
        catch (ArgumentException)
        {
            removedPipelineRejected = true;
        }

        Check(removedPipelineRejected,
            "The removed SANDWiCH pipeline was still accepted as a cache identity.");
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

        // A reply within the budget is keyed whole, exactly as the ranker reads
        // it; only a reply past the budget is trimmed, and then around the
        // target rather than from the right.
        string trimmedWindow = SmartDictionaryResultCache.CreateLocalContextWindow(
            longContext,
            "bank",
            longContext.IndexOf("bank", StringComparison.Ordinal));
        Check(trimmedWindow.Contains("<target>bank</target>", StringComparison.Ordinal),
            "The target must survive trimming of an over-budget reply.");
        Check(!trimmedWindow.Contains("alpha", StringComparison.Ordinal),
            "An over-budget reply must be trimmed away from the target.");
        Check(trimmedWindow.Contains("near" + (flank - 1), StringComparison.Ordinal) &&
              trimmedWindow.Contains("after0", StringComparison.Ordinal),
            "Trimming must keep the words immediately around the target.");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Every model other than the pinned GlossBERT gets the exact-text key
    /// domain; ConSeC is the profile that uses it today.
    /// </summary>
    private static Task VerifyExactTextKeyAsync()
    {
        const string context = "She sat on the bank and watched the water.";
        SmartDictionaryCacheRequest request = CreateRequest(
            context,
            "bank",
            ["банк", "берег"]) with
        {
            Backend = "cpu",
            ModelId = GlossBertDictionarySelectorService.ConsecCpuModelCacheId +
                      ":pipeline-consec-independent",
            RankingPipeline = "consec-independent"
        };
        string baseline = SmartDictionaryResultCache.CreateKeyFingerprint(request);

        const string capitalizedContext =
            "She sat on the Bank and watched the water.";
        Check(baseline != SmartDictionaryResultCache.CreateKeyFingerprint(
                request with
                {
                    Context = capitalizedContext,
                    CharacterIndex = capitalizedContext.IndexOf(
                        "Bank",
                        StringComparison.Ordinal)
                }),
            "The ConSeC hover key folded exact context casing.");
        Check(baseline != SmartDictionaryResultCache.CreateKeyFingerprint(
                request with { Target = "Bank" }),
            "The ConSeC hover key folded exact target casing.");

        char[] exactScalars = ['\u200D', '\u00AD', '\u0001', '\u000B', '\u2060'];
        HashSet<string> contextKeys = new(StringComparer.Ordinal) { baseline };
        foreach (char scalar in exactScalars)
        {
            string variant = context.Replace(
                "She sat",
                $"She{scalar}sat",
                StringComparison.Ordinal);
            string key = SmartDictionaryResultCache.CreateKeyFingerprint(
                request with
                {
                    Context = variant,
                    CharacterIndex = variant.IndexOf("bank", StringComparison.Ordinal)
                });
            Check(contextKeys.Add(key),
                $"The ConSeC hover context U+{(int)scalar:X4} collided with another exact input.");
        }

        const string doubleSpaced =
            "She  sat on the bank and watched the water.";
        Check(baseline != SmartDictionaryResultCache.CreateKeyFingerprint(
                request with
                {
                    Context = doubleSpaced,
                    CharacterIndex = doubleSpaced.IndexOf("bank", StringComparison.Ordinal)
                }),
            "The ConSeC hover key folded internal whitespace.");
        Check(baseline != SmartDictionaryResultCache.CreateKeyFingerprint(
                request with { Candidates = ["БАНК", "БЕРЕГ"] }),
            "The ConSeC hover key folded exact ordered candidate surfaces.");
        Check(baseline != SmartDictionaryResultCache.CreateKeyFingerprint(
                request with { Candidates = ["берег", "банк"] }),
            "The ConSeC hover key ignored candidate/GroupId order.");
        Check(baseline != SmartDictionaryResultCache.CreateKeyFingerprint(
                request with { ModelId = request.ModelId.ToUpperInvariant() }),
            "The ConSeC hover key folded exact model identity.");
        Check(baseline != SmartDictionaryResultCache.CreateKeyFingerprint(
                request with { DictionaryVersion = "dictionary-v1.abstain-margin=0.18" }),
            "The ConSeC hover key ignored its threshold-bearing dictionary epoch.");

        string joined = context.Replace(
            "She sat",
            "Shesat",
            StringComparison.Ordinal);
        string controlled = context.Replace(
            "She sat",
            "She\u000Bsat",
            StringComparison.Ordinal);
        Check(SmartDictionaryResultCache.CreateKeyFingerprint(
                  request with
                  {
                      Context = joined,
                      CharacterIndex = joined.IndexOf("bank", StringComparison.Ordinal)
                  }) !=
              SmartDictionaryResultCache.CreateKeyFingerprint(
                  request with
                  {
                      Context = controlled,
                      CharacterIndex = controlled.IndexOf("bank", StringComparison.Ordinal)
                  }),
            "A non-BERT profile inherited BERT control deletion in the hover key.");

        return Task.CompletedTask;
    }

    private static Task VerifyConsecKeyExactnessAsync()
    {
        const string context = "The bank near the other bank was closed.";
        int first = context.IndexOf("bank", StringComparison.Ordinal);
        int second = context.LastIndexOf("bank", StringComparison.Ordinal);
        SmartDictionaryCacheRequest request = CreateRequest(
            context,
            "bank",
            ["банк", "берег"]) with
        {
            CharacterIndex = first,
            Backend = "cpu",
            ModelId = GlossBertDictionarySelectorService.ConsecCpuModelCacheId +
                      ":pipeline-consec-independent",
            RankingPipeline = "consec-independent"
        };

        string firstKey = SmartDictionaryResultCache.CreateKeyFingerprint(request);
        string secondKey = SmartDictionaryResultCache.CreateKeyFingerprint(
            request with { CharacterIndex = second });
        Check(firstKey != secondKey,
            "Two ConSeC target spans in one exact sentence shared a result-cache key.");

        const string changedWhitespace =
            "The  bank near the other bank was closed.";
        Check(firstKey != SmartDictionaryResultCache.CreateKeyFingerprint(
                request with
                {
                    Context = changedWhitespace,
                    CharacterIndex = changedWhitespace.IndexOf(
                        "bank",
                        StringComparison.Ordinal)
                }),
            "The ConSeC result cache folded source whitespace that reaches its tokenizer.");

        int flank = TargetCenteredContextBuilder.MaximumWords / 2;
        string near = string.Join(
            " ",
            Enumerable.Range(0, flank).Select(index => "near" + index));
        string after = string.Join(
            " ",
            Enumerable.Range(0, flank).Select(index => "after" + index));
        string shared = near + " bank " + after + ".";
        string longContext = "distant alpha evidence " + shared;
        string longVariant = "distant omega evidence " + shared;
        SmartDictionaryCacheRequest longRequest = request with
        {
            Context = longContext,
            CharacterIndex = longContext.IndexOf("bank", StringComparison.Ordinal)
        };
        SmartDictionaryCacheRequest longVariantRequest = request with
        {
            Context = longVariant,
            CharacterIndex = longVariant.IndexOf("bank", StringComparison.Ordinal)
        };
        Check(SmartDictionaryResultCache.CreateKeyFingerprint(longRequest) !=
              SmartDictionaryResultCache.CreateKeyFingerprint(longVariantRequest),
            "ConSeC ignored distant full-sentence evidence outside the pair-model window.");

        Check(firstKey != SmartDictionaryResultCache.CreateKeyFingerprint(
                request with
                {
                    ModelId = request.ModelId.Replace(
                        "context-full-v1",
                        "context-full-v2",
                        StringComparison.Ordinal)
                }),
            "The ConSeC full-context identity did not participate in the result-cache key.");
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
                version = SmartDictionaryResultCache.SchemaVersion,
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
        Check(document.RootElement.GetProperty("version").GetInt32() ==
              SmartDictionaryResultCache.SchemaVersion,
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
            GlossBertDictionarySelectorService.NpuModelCacheId + ":pipeline-cross",
            "dictionary-v1");

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
