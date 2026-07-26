using System.Collections.Frozen;
using System.IO.Compression;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AsteriaScale.Services;

public sealed record DictionaryLookupResult(
    bool Found,
    string Source,
    string Translation,
    bool IsPhrase,
    string Language)
{
    public IReadOnlyList<DictionarySenseCandidate> SenseCandidates { get; init; } =
        Array.Empty<DictionarySenseCandidate>();
    public string ContextTranslation { get; init; } = string.Empty;
    public bool IsTrustedPhrase { get; init; }
    public bool HasMultipleMeanings { get; init; }
    internal string MatchedEnglishKey { get; init; } = string.Empty;
}

public sealed record DictionarySenseCandidate(
    string Translation,
    string EnglishDefinition,
    string Register,
    string PartOfSpeech = "")
{
    /// <summary>
    /// Short disambiguating hint (synonyms, else a clipped example) shown to the
    /// sense ranker alongside the definition. Never part of sense identity:
    /// grouping and merging still key on the definition alone.
    /// </summary>
    public string Enrichment { get; init; } = string.Empty;
}

internal sealed record ScaffoldDictionaryMatch(
    string Source,
    string MatchedEnglishKey,
    string Translation,
    int CharacterIndex,
    int CharacterLength);

public sealed partial class LocalDictionaryService
{
    public const string EngineLabel = "NPU OCR  •  СЛОВАРЬ";
    private const int MaximumGeneralPhraseTokens = 3;
    private const int MaximumLexicalPhraseTokens = 6;
    private const int MaximumTrustedPhraseTokens = 10;
    // Every merged value reaches its consumers through Limit, which cuts at
    // MaximumDisplayedValueLength on a meaning boundary and marks the cut with
    // an ellipsis. The merge budget is therefore deliberately *larger* than the
    // display ceiling rather than equal to it: if merging stopped at exactly
    // what fits, Limit would never truncate, never append the ellipsis, and a
    // cut list would look complete to the user. The headroom is what makes the
    // "there is more" signal honest, not stored value the user can reach.
    // Overflow always drops the tail, and the tail is the lowest-priority
    // layer: merge order follows DictionaryPriority (invariant: layer order).
    internal const int MaximumDisplayedValueLength = 380;
    private const int MaximumMergedMeaningCount = 32;
    private const int MaximumMergedValueLength = 4 * 1024;
    private const int MaximumSenseCountPerKey = 128;

    private readonly object _loadLock = new();
    private readonly string? _senseMetadataFileOverride;
    private readonly string? _bundledDirectoryOverride;
    private readonly string? _userDirectoryOverride;
    private Task? _loadTask;
    private Task? _trustedLoadTask;
    private Task? _senseMetadataLoadTask;
    // Measured: freezing these two large maps costs ~57 MB extra, so they stay
    // ordinary dictionaries; only the sense-blob index below is frozen.
    private Dictionary<string, string> _english = new(StringComparer.Ordinal);
    private Dictionary<string, string> _japanese = new(StringComparer.Ordinal);
    private Dictionary<string, string> _scaffoldEnglishTranslations =
        new(StringComparer.Ordinal);
    private Dictionary<string, string> _trustedEnglishExact = new(StringComparer.Ordinal);
    // Sense candidates are stored as one compact UTF-8 blob per key and decoded
    // only for the hovered key: English definitions are ASCII-heavy, so this
    // halves their footprint and removes half a million record/string objects.
    private FrozenDictionary<string, byte[]> _englishSenseMetadata =
        FrozenDictionary<string, byte[]>.Empty;
    private HashSet<string> _userEnglishKeys = new(StringComparer.Ordinal);
    private HashSet<string> _preferredEnglishSurfaceForms = new(StringComparer.Ordinal);
    private HashSet<string> _softEnglishSurfaceForms = new(StringComparer.Ordinal);
    private HashSet<string> _lexicalEnglishPhrases = new(StringComparer.Ordinal);
    private Dictionary<string, string> _dialogueEnglishTranslations = new(StringComparer.Ordinal);
    private Dictionary<string, string> _dialogueJapaneseTranslations = new(StringComparer.Ordinal);
    private int _maximumJapaneseLength = 1;
    private int _maximumEnglishScaffoldTokens = 1;
    private int _sourceCount;
    private string _versionToken = "unloaded";
    private string _senseMetadataVersionToken = string.Empty;

    public LocalDictionaryService()
    {
    }

    internal LocalDictionaryService(string senseMetadataFileOverride)
    {
        _senseMetadataFileOverride = senseMetadataFileOverride;
    }

    internal LocalDictionaryService(
        string bundledDirectoryOverride,
        string userDirectoryOverride,
        string? senseMetadataFileOverride = null)
    {
        _bundledDirectoryOverride = bundledDirectoryOverride;
        _userDirectoryOverride = userDirectoryOverride;
        _senseMetadataFileOverride = senseMetadataFileOverride;
    }

    private (string BundledDirectory, string UserDirectory) ResolveDictionaryDirectories()
    {
        string executableDirectory = Path.GetDirectoryName(Environment.ProcessPath)
            ?? AppContext.BaseDirectory;
        string bundledDirectory = _bundledDirectoryOverride
            ?? Path.Combine(executableDirectory, "Dictionaries");
        string userDirectory = _userDirectoryOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AsteriaScale",
            "Dictionaries");
        return (bundledDirectory, userDirectory);
    }

    private static readonly IReadOnlyDictionary<string, string> IrregularEnglish =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["am"] = "be",
            ["are"] = "be",
            ["is"] = "be",
            ["was"] = "be",
            ["were"] = "be",
            ["been"] = "be",
            ["went"] = "go",
            ["gone"] = "go",
            ["had"] = "have",
            ["has"] = "have",
            ["did"] = "do",
            ["done"] = "do",
            ["saw"] = "see",
            ["seen"] = "see",
            ["said"] = "say",
            ["told"] = "tell",
            ["thought"] = "think",
            ["knew"] = "know",
            ["known"] = "know",
            ["felt"] = "feel",
            ["found"] = "find",
            ["made"] = "make",
            ["took"] = "take",
            ["taken"] = "take",
            ["came"] = "come",
            ["gave"] = "give",
            ["given"] = "give",
            ["got"] = "get",
            ["gotten"] = "get",
            ["shook"] = "shake",
            ["shaken"] = "shake"
        };

    /// <summary>
    /// Common censored spellings mapped to the word they stand for
    /// (roadmap #14.2C). This is a curated table on purpose: restoring the
    /// hidden letters by substitution is unsafe, because it silently lands on a
    /// *different real word* and the result is then confidently mistranslated.
    /// Measured against the shipped dictionary, a single-letter guess turns
    /// "b*stard" into "bustard" (a bird), "h*ll" into "hill", "b*llocks" into
    /// "bullocks" (oxen), "d*ck" into "dock" and "sh*t" into "shat".
    ///
    /// These are lookup candidates only. NormalizeEnglish and sentence keys are
    /// untouched, and the card keeps showing the masked form as it was written,
    /// because the entry is reached through the candidate sequence rather than
    /// by rewriting the source span.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> MaskedEnglish =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["f*ck"] = "fuck",
            ["f**k"] = "fuck",
            ["fu*k"] = "fuck",
            ["f*cked"] = "fucked",
            ["f**ked"] = "fucked",
            ["f*cking"] = "fucking",
            ["f**king"] = "fucking",
            ["fuck!ng"] = "fucking",
            ["sh*t"] = "shit",
            ["sh!t"] = "shit",
            ["s**t"] = "shit",
            ["sh*tty"] = "shitty",
            ["sh!tty"] = "shitty",
            ["sh*thole"] = "shithole",
            ["sh!thole"] = "shithole",
            ["b*tch"] = "bitch",
            ["b!tch"] = "bitch",
            ["b*tches"] = "bitches",
            ["b!tches"] = "bitches",
            ["a**hole"] = "asshole",
            ["a*shole"] = "asshole",
            ["assh*le"] = "asshole",
            ["a**holes"] = "assholes",
            ["d*mn"] = "damn",
            ["d*mmit"] = "dammit",
            ["b*llocks"] = "bollocks",
            ["b****cks"] = "bollocks",
            ["bl**dy"] = "bloody",
            ["bl*ody"] = "bloody",
            ["p*ss"] = "piss",
            ["p!ss"] = "piss",
            ["cr*p"] = "crap",
            ["cr*ppy"] = "crappy",
            ["d*ck"] = "dick",
            ["d!ck"] = "dick",
            ["pr*ck"] = "prick",
            ["b*stard"] = "bastard",
            ["b*stards"] = "bastards",
            ["h*ll"] = "hell",
            ["w*nker"] = "wanker",
            ["w**ker"] = "wanker",
            ["c*nt"] = "cunt",
            ["c**t"] = "cunt",
            ["wh*re"] = "whore",
            ["sl*t"] = "slut",
            ["tw*t"] = "twat",
            ["m*therfucker"] = "motherfucker",
            ["motherf**ker"] = "motherfucker",
            ["motherf****r"] = "motherfucker"
        };

    public int EntryCount =>
        _english.Count + _japanese.Count +
        _scaffoldEnglishTranslations.Count +
        _dialogueEnglishTranslations.Count + _dialogueJapaneseTranslations.Count;
    public int SourceCount => _sourceCount;
    internal int SenseMetadataKeyCount => _englishSenseMetadata.Count;
    internal int SenseMetadataCandidateCount { get; private set; }
    public string VersionToken => _senseMetadataVersionToken.Length == 0
        ? _versionToken
        : _versionToken + "." + _senseMetadataVersionToken;

    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Task task;
        lock (_loadLock)
        {
            _loadTask ??= RunDetached(LoadCoreAsync);
            task = _loadTask;
        }

        return task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Runs a loader so the stored Task does not reference its async state
    /// machine: a completed machine pins every hoisted local (mutable build
    /// dictionaries, interning pools) for as long as the Task field lives.
    /// </summary>
    private static Task RunDetached(Func<Task> work)
    {
        TaskCompletionSource completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _ = RelayAsync();
        return completion.Task;

        async Task RelayAsync()
        {
            try
            {
                await work().ConfigureAwait(false);
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        }
    }

    public async Task UnloadAsync()
    {
        Task? loadTask;
        Task? trustedLoadTask;
        Task? senseMetadataLoadTask;
        lock (_loadLock)
        {
            loadTask = _loadTask;
            trustedLoadTask = _trustedLoadTask;
            senseMetadataLoadTask = _senseMetadataLoadTask;
        }

        Task[] pendingTasks =
            new Task?[] { loadTask, trustedLoadTask, senseMetadataLoadTask }
            .OfType<Task>()
            .ToArray();
        if (pendingTasks.Length > 0)
        {
            await Task.WhenAll(pendingTasks);
        }

        lock (_loadLock)
        {
            if (!ReferenceEquals(_loadTask, loadTask) ||
                !ReferenceEquals(_trustedLoadTask, trustedLoadTask) ||
                !ReferenceEquals(_senseMetadataLoadTask, senseMetadataLoadTask))
            {
                return;
            }

            _english = new Dictionary<string, string>(StringComparer.Ordinal);
            _japanese = new Dictionary<string, string>(StringComparer.Ordinal);
            _scaffoldEnglishTranslations = new Dictionary<string, string>(StringComparer.Ordinal);
            _trustedEnglishExact = new Dictionary<string, string>(StringComparer.Ordinal);
            _englishSenseMetadata = FrozenDictionary<string, byte[]>.Empty;
            _userEnglishKeys = new HashSet<string>(StringComparer.Ordinal);
            _preferredEnglishSurfaceForms = new HashSet<string>(StringComparer.Ordinal);
            _softEnglishSurfaceForms = new HashSet<string>(StringComparer.Ordinal);
            _lexicalEnglishPhrases = new HashSet<string>(StringComparer.Ordinal);
            _dialogueEnglishTranslations = new Dictionary<string, string>(StringComparer.Ordinal);
            _dialogueJapaneseTranslations = new Dictionary<string, string>(StringComparer.Ordinal);
            _maximumJapaneseLength = 1;
            _maximumEnglishScaffoldTokens = 1;
            _sourceCount = 0;
            SenseMetadataCandidateCount = 0;
            _versionToken = "unloaded";
            _senseMetadataVersionToken = string.Empty;
            _loadTask = null;
            _trustedLoadTask = null;
            _senseMetadataLoadTask = null;
        }
    }

    public async Task<DictionaryLookupResult> LookupAsync(
        string text,
        string hoveredWord,
        int characterIndex,
        CancellationToken cancellationToken = default)
    {
        return await LookupCoreAsync(
            text,
            hoveredWord,
            characterIndex,
            includeSenseMetadata: false,
            cancellationToken);
    }

    public async Task<DictionaryLookupResult> LookupWithSenseMetadataAsync(
        string text,
        string hoveredWord,
        int characterIndex,
        CancellationToken cancellationToken = default)
    {
        return await LookupCoreAsync(
            text,
            hoveredWord,
            characterIndex,
            includeSenseMetadata: true,
            cancellationToken);
    }

    public async Task LoadSenseMetadataAsync(CancellationToken cancellationToken = default)
    {
        await LoadAsync(cancellationToken);
        await EnsureSenseMetadataLoadedAsync(cancellationToken);
    }

    /// <summary>
    /// Per-sentence work shared by every lookup into the same reply: the token
    /// scan and the dialogue sentence key. A caller that probes one reply once
    /// per token — the full-text scaffold builder does exactly that — would
    /// otherwise redo both for every token.
    /// </summary>
    internal sealed class EnglishLookupContext
    {
        internal EnglishLookupContext(string text)
        {
            Text = text ?? string.Empty;
            Tokens = EnglishLookupTokenRegex().Matches(Text);
            SentenceKey = NormalizeEnglishSentenceKey(Text);
            // The phrase search normalizes the same token text once per
            // (start, length) pair and once more per canonical candidate, which
            // is thousands of repeats of an identical result per reply.
            NormalizedTokens = new string[Tokens.Count];
            for (int index = 0; index < Tokens.Count; index++)
            {
                NormalizedTokens[index] = NormalizeEnglish(Tokens[index].Value);
            }
        }

        public string Text { get; }
        public MatchCollection Tokens { get; }
        public string SentenceKey { get; }
        public string[] NormalizedTokens { get; }

        private Dictionary<string, string[]>? _lemmas;

        /// <summary>
        /// Lemma candidates for one already normalized token. The phrase search
        /// asks for the same token's lemmas once per (start, length) pair, and
        /// each miss allocated a HashSet and a List to rebuild an identical
        /// answer. Scoped to one reply, so it cannot go stale.
        /// </summary>
        public string[] LemmasFor(string normalizedToken)
        {
            _lemmas ??= new Dictionary<string, string[]>(StringComparer.Ordinal);
            if (!_lemmas.TryGetValue(normalizedToken, out string[]? lemmas))
            {
                lemmas = EnglishCandidates(normalizedToken, false)
                    .Skip(1)
                    .Take(3)
                    .ToArray();
                _lemmas[normalizedToken] = lemmas;
            }

            return lemmas;
        }
    }

    internal EnglishLookupContext CreateEnglishLookupContext(string text) => new(text);

    /// <summary>
    /// Lookup that reuses an already built per-sentence context. Sense metadata
    /// is still loaded on demand, so the caller cannot skip that step by
    /// holding a context.
    /// </summary>
    internal async Task<DictionaryLookupResult> LookupWithSenseMetadataAsync(
        EnglishLookupContext context,
        string hoveredWord,
        int characterIndex,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        await LoadAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (ContainsJapanese(context.Text) || ContainsJapanese(hoveredWord))
        {
            return LookupJapanese(context.Text, hoveredWord, characterIndex);
        }

        await EnsureSenseMetadataLoadedAsync(cancellationToken);
        return LookupEnglish(context, hoveredWord, characterIndex, true);
    }

    private async Task<DictionaryLookupResult> LookupCoreAsync(
        string text,
        string hoveredWord,
        int characterIndex,
        bool includeSenseMetadata,
        CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        bool japanese = ContainsJapanese(text) || ContainsJapanese(hoveredWord);
        if (includeSenseMetadata && !japanese)
        {
            await EnsureSenseMetadataLoadedAsync(cancellationToken);
        }

        return japanese
            ? LookupJapanese(text, hoveredWord, characterIndex)
            : LookupEnglish(
                new EnglishLookupContext(text),
                hoveredWord,
                characterIndex,
                includeSenseMetadata);
    }

    private async Task EnsureSenseMetadataLoadedAsync(CancellationToken cancellationToken)
    {
        Task task;
        lock (_loadLock)
        {
            _senseMetadataLoadTask ??= RunDetached(LoadEnglishSenseMetadataCoreAsync);
            task = _senseMetadataLoadTask;
        }

        await task.WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    internal async Task<string?> LookupTrustedEnglishExactAsync(
        string source,
        CancellationToken cancellationToken = default)
    {
        Task task;
        lock (_loadLock)
        {
            _trustedLoadTask ??= RunDetached(LoadTrustedEnglishCoreAsync);
            task = _trustedLoadTask;
        }

        await task.WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return _trustedEnglishExact.TryGetValue(NormalizeEnglish(source), out string? translation)
            ? translation
            : null;
    }

    internal async Task<string?> LookupExactEnglishDialogueAsync(
        string sentence,
        CancellationToken cancellationToken = default)
    {
        await LoadAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        string key = NormalizeEnglishSentenceKey(sentence);
        return key.Length > 0 &&
               _dialogueEnglishTranslations.TryGetValue(key, out string? translation)
            ? Limit(translation)
            : null;
    }

    internal Task<IReadOnlyList<ScaffoldDictionaryMatch>>
        FindEnglishScaffoldMatchesAsync(
            string sentence,
            CancellationToken cancellationToken = default) =>
        FindEnglishScaffoldMatchesAsync(
            CreateEnglishLookupContext(sentence ?? string.Empty),
            cancellationToken);

    internal async Task<IReadOnlyList<ScaffoldDictionaryMatch>>
        FindEnglishScaffoldMatchesAsync(
            EnglishLookupContext context,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        await LoadAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        string sentence = context.Text;
        if (_scaffoldEnglishTranslations.Count == 0 || string.IsNullOrWhiteSpace(sentence))
        {
            return Array.Empty<ScaffoldDictionaryMatch>();
        }

        MatchCollection tokens = context.Tokens;
        if (tokens.Count == 0)
        {
            return Array.Empty<ScaffoldDictionaryMatch>();
        }

        List<ScaffoldDictionaryMatch> matches = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        for (int start = 0; start < tokens.Count; start++)
        {
            int maximumLength = Math.Min(
                _maximumEnglishScaffoldTokens,
                tokens.Count - start);
            bool matched = false;
            for (int length = maximumLength; length >= 1 && !matched; length--)
            {
                string exactKey = NormalizeEnglish(string.Join(
                    " ",
                    Enumerable.Range(start, length).Select(index => tokens[index].Value)));
                string rawPhrase = ExtractEnglishPhrase(sentence, tokens, start, length);
                string rawKey = NormalizeEnglish(rawPhrase);
                IEnumerable<string> lookupKeys = rawKey.Contains(' ') &&
                                                 !string.Equals(
                                                     rawKey,
                                                     exactKey,
                                                     StringComparison.Ordinal)
                    ? new[] { rawKey, exactKey }
                    : new[] { exactKey };

                foreach (string lookupKey in lookupKeys.Concat(
                             CanonicalEnglishPhraseCandidates(
                                 context,
                                 start,
                                 length,
                                 exactKey)))
                {
                    if (!_scaffoldEnglishTranslations.TryGetValue(
                            lookupKey,
                            out string? translation))
                    {
                        continue;
                    }

                    string occurrenceKey = lookupKey + "\u001f" + tokens[start].Index;
                    if (seen.Add(occurrenceKey))
                    {
                        int characterLength =
                            tokens[start + length - 1].Index +
                            tokens[start + length - 1].Length -
                            tokens[start].Index;
                        matches.Add(new ScaffoldDictionaryMatch(
                            TrimEnglishDisplayPunctuation(rawPhrase),
                            lookupKey,
                            translation,
                            tokens[start].Index,
                            characterLength));
                    }

                    start += length - 1;
                    matched = true;
                    break;
                }
            }
        }

        return matches.Count == 0
            ? Array.Empty<ScaffoldDictionaryMatch>()
            : Array.AsReadOnly(matches.ToArray());
    }

    internal bool IsUserEnglishKey(string source) =>
        _userEnglishKeys.Contains(NormalizeEnglish(source));

    internal bool IsPreferredEnglishKey(string source) =>
        _preferredEnglishSurfaceForms.Contains(NormalizeEnglish(source));

    private async Task LoadCoreAsync()
    {
        (string bundledDirectory, string userDirectory) = ResolveDictionaryDirectories();
        Directory.CreateDirectory(userDirectory);

        Task<LoadedDictionary> englishTask = LoadFilesAsync(
            bundledDirectory,
            userDirectory,
            "eng-rus");
        Task<LoadedDictionary> japaneseTask = LoadFilesAsync(
            bundledDirectory,
            userDirectory,
            "jpn-rus");
        await Task.WhenAll(englishTask, japaneseTask);
        LoadedDictionary english = await englishTask;
        LoadedDictionary japanese = await japaneseTask;
        _english = english.Entries;
        _japanese = japanese.Entries;
        _scaffoldEnglishTranslations = english.ScaffoldTranslations;
        _userEnglishKeys = english.UserKeys;
        _preferredEnglishSurfaceForms = english.PreferredSurfaceForms;
        _softEnglishSurfaceForms = english.SoftSurfaceForms;
        _lexicalEnglishPhrases = english.LexicalPhrases;
        _dialogueEnglishTranslations = english.DialogueTranslations;
        _dialogueJapaneseTranslations = japanese.DialogueTranslations;
        _sourceCount = english.SourceCount + japanese.SourceCount;
        _versionToken = english.VersionToken + "." + japanese.VersionToken;
        _maximumJapaneseLength = _japanese.Count == 0
            ? 1
            : Math.Min(32, _japanese.Keys.Max(key => key.Length));
        _maximumEnglishScaffoldTokens = _scaffoldEnglishTranslations.Count == 0
            ? 1
            : Math.Min(
                10,
                _scaffoldEnglishTranslations.Keys.Max(key => key.Count(character => character == ' ') + 1));
    }

    private async Task LoadTrustedEnglishCoreAsync()
    {
        (string bundledDirectory, string userDirectory) = ResolveDictionaryDirectories();
        Directory.CreateDirectory(userDirectory);

        string[] files = DiscoverFiles(bundledDirectory, userDirectory, "eng-rus")
            .Where(path =>
            {
                DictionaryFileRole role = GetDictionaryFileRole(path);
                bool userLayer = string.Equals(
                    Path.GetDirectoryName(path),
                    userDirectory,
                    StringComparison.OrdinalIgnoreCase);
                return role == DictionaryFileRole.VnHard ||
                       userLayer &&
                       (role is DictionaryFileRole.Broad or DictionaryFileRole.VnSoft);
            })
            .ToArray();
        Dictionary<string, string> entries = new(StringComparer.Ordinal);
        HashSet<string> userKeys = new(StringComparer.Ordinal);
        foreach (string path in files)
        {
            bool userLayer = string.Equals(
                Path.GetDirectoryName(path),
                userDirectory,
                StringComparison.OrdinalIgnoreCase);
            await MergeTrustedEnglishFileAsync(path, entries, userKeys, userLayer);
        }

        _trustedEnglishExact = entries;
    }

    private async Task LoadEnglishSenseMetadataCoreAsync()
    {
        (string bundledDirectory, string userDirectory) = ResolveDictionaryDirectories();

        IReadOnlyList<string> files;
        if (!string.IsNullOrWhiteSpace(_senseMetadataFileOverride))
        {
            files = File.Exists(_senseMetadataFileOverride)
                ? [_senseMetadataFileOverride]
                : [];
        }
        else
        {
            files = DiscoverSenseMetadataFiles(bundledDirectory, userDirectory);
        }

        Dictionary<string, List<DictionarySenseCandidate>> mutable =
            new(StringComparer.Ordinal);
        Dictionary<string, string> translationPool = new(StringComparer.Ordinal);
        Dictionary<string, string> definitionPool = new(StringComparer.Ordinal);
        Dictionary<string, string> registerPool = new(StringComparer.Ordinal);
        Dictionary<string, string> partOfSpeechPool = new(StringComparer.Ordinal);
        Dictionary<string, string> enrichmentPool = new(StringComparer.Ordinal);
        List<string> loadedFiles = [];
        foreach (string path in files)
        {
            try
            {
                await MergeSenseMetadataFileAsync(
                    path,
                    mutable,
                    translationPool,
                    definitionPool,
                    registerPool,
                    partOfSpeechPool,
                    enrichmentPool);
                loadedFiles.Add(path);
            }
            catch (Exception exception) when (exception is IOException or
                                              InvalidDataException or
                                              UnauthorizedAccessException)
            {
                // Sense metadata is an optional ranking aid. A missing or damaged
                // sidecar must not make ordinary dictionary lookup unavailable.
            }
        }

        Dictionary<string, byte[]> compact =
            new(mutable.Count, StringComparer.Ordinal);
        int candidateCount = 0;
        foreach ((string key, List<DictionarySenseCandidate> candidates) in mutable)
        {
            if (!_english.ContainsKey(key) ||
                _preferredEnglishSurfaceForms.Contains(key))
            {
                continue;
            }

            compact[key] = EncodeSenseCandidates(candidates);
            candidateCount += candidates.Count;
        }

        _englishSenseMetadata = compact.ToFrozenDictionary(StringComparer.Ordinal);
        SenseMetadataCandidateCount = candidateCount;
        _senseMetadataVersionToken = loadedFiles.Count == 0
            ? "no-senses"
            : ComputeVersionToken(loadedFiles);
    }

    private static async Task<LoadedDictionary> LoadFilesAsync(
        string bundledDirectory,
        string userDirectory,
        string prefix)
    {
        IReadOnlyList<string> files = DiscoverFiles(bundledDirectory, userDirectory, prefix);
        if (files.Count == 0)
        {
            throw new FileNotFoundException(
                "Локальная база перевода не найдена.",
                Path.Combine(bundledDirectory, prefix + ".tsv.gz"));
        }

        Dictionary<string, string> entries = new(StringComparer.Ordinal);
        HashSet<string> userKeys = new(StringComparer.Ordinal);
        HashSet<string> preferredSurfaceForms = new(StringComparer.Ordinal);
        HashSet<string> softSurfaceForms = new(StringComparer.Ordinal);
        HashSet<string> lexicalPhrases = new(StringComparer.Ordinal);
        Dictionary<string, string> scaffoldTranslations = new(StringComparer.Ordinal);
        Dictionary<string, string> dialogueTranslations = new(StringComparer.Ordinal);
        bool japaneseLayer = prefix.StartsWith("jpn", StringComparison.OrdinalIgnoreCase);
        foreach (string path in files)
        {
            bool userLayer = string.Equals(
                Path.GetDirectoryName(path),
                userDirectory,
                StringComparison.OrdinalIgnoreCase);
            string fileName = Path.GetFileName(path);
            DictionaryFileRole role = GetDictionaryFileRole(fileName);
            await MergeFileAsync(
                path,
                entries,
                userKeys,
                preferredSurfaceForms,
                softSurfaceForms,
                lexicalPhrases,
                scaffoldTranslations,
                dialogueTranslations,
                userLayer,
                role,
                japaneseLayer);
        }

        // Inflected forms of one lemma carry identical translation strings
        // across thousands of keys; sharing them saves ~18 MB (measured).
        Dictionary<string, string> valuePool = new(StringComparer.Ordinal);
        foreach (string key in entries.Keys.ToArray())
        {
            string value = entries[key];
            if (valuePool.TryGetValue(value, out string? shared))
            {
                entries[key] = shared;
            }
            else
            {
                valuePool[value] = value;
            }
        }

        return new LoadedDictionary(
            entries,
            files.Count,
            userKeys,
            preferredSurfaceForms,
            softSurfaceForms,
            lexicalPhrases,
            scaffoldTranslations,
            dialogueTranslations,
            ComputeVersionToken(files));
    }

    private static string ComputeVersionToken(IReadOnlyList<string> files)
    {
        StringBuilder fingerprint = new();
        foreach (string path in files.OrderBy(
                     value => value,
                     StringComparer.OrdinalIgnoreCase))
        {
            FileInfo file = new(path);
            fingerprint.Append(Path.GetFileName(path).ToLowerInvariant());
            fingerprint.Append('\u001f');
            fingerprint.Append(file.Length);
            fingerprint.Append('\u001f');
            fingerprint.Append(file.LastWriteTimeUtc.Ticks);
            fingerprint.Append('\u001e');
        }

        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint.ToString())));
    }

    private static IReadOnlyList<string> DiscoverFiles(
        string bundledDirectory,
        string userDirectory,
        string prefix)
    {
        static IEnumerable<string> Enumerate(string directory, string namePrefix) =>
            Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, namePrefix + "*.tsv*", SearchOption.TopDirectoryOnly)
                    .Where(path => path.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase) ||
                                   path.EndsWith(".tsv.gz", StringComparison.OrdinalIgnoreCase))
                    .Where(path => !IsSenseMetadataFile(path))
                : [];

        IEnumerable<string> userFiles = Enumerate(userDirectory, prefix)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
        if (prefix.StartsWith("jpn", StringComparison.OrdinalIgnoreCase))
        {
            string legacyEnglishUserFile = Path.Combine(userDirectory, "eng-rus-user.tsv");
            if (File.Exists(legacyEnglishUserFile))
            {
                // Older releases stored Japanese user rows in the English file.
                // Read it first as a compatibility layer; a new jpn-rus user
                // entry then overrides the legacy copy normally.
                userFiles = new[] { legacyEnglishUserFile }.Concat(userFiles);
            }
        }

        IEnumerable<string> bundledFiles = Enumerate(bundledDirectory, prefix)
            .OrderBy(path => DictionaryPriority(path, prefix))
            .ThenBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
        return userFiles.Concat(bundledFiles).ToArray();
    }

    private static IReadOnlyList<string> DiscoverSenseMetadataFiles(
        string bundledDirectory,
        string userDirectory)
    {
        static IEnumerable<string> Enumerate(string directory) =>
            Directory.Exists(directory)
                ? Directory.EnumerateFiles(
                        directory,
                        "eng-rus*-senses.tsv*",
                        SearchOption.TopDirectoryOnly)
                    .Where(path => path.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase) ||
                                   path.EndsWith(".tsv.gz", StringComparison.OrdinalIgnoreCase))
                    .Where(IsSenseMetadataFile)
                : [];

        return Enumerate(userDirectory)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .Concat(Enumerate(bundledDirectory)
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static bool IsSenseMetadataFile(string path)
    {
        string name = Path.GetFileName(path);
        return name.EndsWith("-senses.tsv", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("-senses.tsv.gz", StringComparison.OrdinalIgnoreCase);
    }

    private static DictionaryFileRole GetDictionaryFileRole(string path)
    {
        string name = Path.GetFileName(path);
        if (IsSenseMetadataFile(name))
        {
            return DictionaryFileRole.SenseMetadata;
        }

        if (name.EndsWith("-scaffold.tsv", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("-scaffold.tsv.gz", StringComparison.OrdinalIgnoreCase))
        {
            return DictionaryFileRole.Scaffold;
        }

        if (name.EndsWith("-dialogue.tsv", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("-dialogue.tsv.gz", StringComparison.OrdinalIgnoreCase))
        {
            return DictionaryFileRole.ExactDialogue;
        }

        if (name.EndsWith("-vn-soft.tsv", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("-vn-soft.tsv.gz", StringComparison.OrdinalIgnoreCase))
        {
            return DictionaryFileRole.VnSoft;
        }

        if (name.EndsWith("-vn.tsv", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("-vn.tsv.gz", StringComparison.OrdinalIgnoreCase))
        {
            return DictionaryFileRole.VnHard;
        }

        return DictionaryFileRole.Broad;
    }

    private static int DictionaryPriority(string path, string prefix)
    {
        string name = Path.GetFileName(path);
        if (string.Equals(name, prefix + ".tsv", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, prefix + ".tsv.gz", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return GetDictionaryFileRole(name) switch
        {
            DictionaryFileRole.VnHard => 1,
            DictionaryFileRole.Scaffold => 1,
            _ => 2
        };
    }

    private static async Task MergeFileAsync(
        string path,
        Dictionary<string, string> entries,
        HashSet<string> userKeys,
        HashSet<string> preferredSurfaceForms,
        HashSet<string> softSurfaceForms,
        HashSet<string> lexicalPhrases,
        Dictionary<string, string> scaffoldTranslations,
        Dictionary<string, string> dialogueTranslations,
        bool userLayer,
        DictionaryFileRole role,
        bool japaneseLayer)
    {
        await using FileStream file = File.OpenRead(path);
        Stream input = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new GZipStream(file, CompressionMode.Decompress)
            : file;
        using StreamReader reader = new(input, Encoding.UTF8, true, 64 * 1024);
        while (await reader.ReadLineAsync() is { } line)
        {
            int separator = line.IndexOf('\t');
            if (separator <= 0 || separator == line.Length - 1)
            {
                continue;
            }

            // Hand-written user TSVs are not pre-normalized, so every layer's key
            // is canonicalized with the exact lookup normalization. Otherwise an
            // entry such as "Hello" or one with a typographic apostrophe would be
            // stored but never found.
            string key = japaneseLayer
                ? NormalizeJapanese(line[..separator])
                : role == DictionaryFileRole.ExactDialogue
                    ? NormalizeEnglishSentenceKey(line[..separator])
                    : NormalizeEnglish(line[..separator]);
            if (key.Length == 0)
            {
                continue;
            }

            string value = line[(separator + 1)..];
            if (role == DictionaryFileRole.ExactDialogue)
            {
                // Dialogue rows are sentence context, never lexical phrases.
                // Keeping them outside the main map prevents a short Tatoeba
                // sentence from shadowing a real word or phrase entry.
                dialogueTranslations.TryAdd(key, value);
                continue;
            }

            if (role == DictionaryFileRole.Scaffold)
            {
                if (userLayer)
                {
                    scaffoldTranslations[key] = value;
                }
                else
                {
                    scaffoldTranslations.TryAdd(key, value);
                }

                continue;
            }

            if (key.Contains(' '))
            {
                lexicalPhrases.Add(key);
            }

            if (!userLayer && role == DictionaryFileRole.VnSoft)
            {
                softSurfaceForms.Add(key);
            }

            if (userLayer)
            {
                entries[key] = value;
                userKeys.Add(key);
                preferredSurfaceForms.Add(key);
            }
            else if (role == DictionaryFileRole.VnHard)
            {
                if (!userKeys.Contains(key))
                {
                    entries[key] = value;
                }

                preferredSurfaceForms.Add(key);
            }
            else
            {
                // Broad bundled dictionaries complement one another. In particular,
                // FreeDict and Kaikki often cover different senses of the same
                // headword, so first-wins semantics silently discarded useful
                // meanings. User and curated VN entries stay authoritative: they
                // are reviewed overrides rather than broad lexical layers.
                if (userKeys.Contains(key) || preferredSurfaceForms.Contains(key))
                {
                    continue;
                }

                if (entries.TryGetValue(key, out string? existing))
                {
                    entries[key] = MergeBundledMeanings(existing, value);
                }
                else
                {
                    entries[key] = value;
                }
            }
        }
    }

    private static async Task MergeSenseMetadataFileAsync(
        string path,
        Dictionary<string, List<DictionarySenseCandidate>> entries,
        Dictionary<string, string> translationPool,
        Dictionary<string, string> definitionPool,
        Dictionary<string, string> registerPool,
        Dictionary<string, string> partOfSpeechPool,
        Dictionary<string, string> enrichmentPool)
    {
        await using FileStream file = File.OpenRead(path);
        Stream input = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new GZipStream(file, CompressionMode.Decompress)
            : file;
        using StreamReader reader = new(input, Encoding.UTF8, true, 64 * 1024);
        while (await reader.ReadLineAsync() is { } line)
        {
            int firstSeparator = line.IndexOf('\t');
            int secondSeparator = firstSeparator < 0
                ? -1
                : line.IndexOf('\t', firstSeparator + 1);
            int thirdSeparator = secondSeparator < 0
                ? -1
                : line.IndexOf('\t', secondSeparator + 1);
            int fourthSeparator = thirdSeparator < 0
                ? -1
                : line.IndexOf('\t', thirdSeparator + 1);
            // Sixth column (roadmap #8 candidate 5) is optional: a sidecar
            // written before it must keep loading with an empty enrichment.
            int fifthSeparator = fourthSeparator < 0
                ? -1
                : line.IndexOf('\t', fourthSeparator + 1);
            if (firstSeparator <= 0 ||
                secondSeparator <= firstSeparator + 1 ||
                thirdSeparator <= secondSeparator + 1)
            {
                continue;
            }

            string key = NormalizeEnglish(line[..firstSeparator]);
            string translation = InternSenseField(
                NormalizeSenseField(
                    line[(firstSeparator + 1)..secondSeparator],
                    1024),
                translationPool);
            string definition = InternSenseField(
                NormalizeSenseField(
                    line[(secondSeparator + 1)..thirdSeparator],
                    4096),
                definitionPool);
            string register = InternSenseField(
                NormalizeSenseField(
                    fourthSeparator < 0
                        ? line[(thirdSeparator + 1)..]
                        : line[(thirdSeparator + 1)..fourthSeparator],
                    256),
                registerPool);
            string partOfSpeech = InternSenseField(
                fourthSeparator < 0
                    ? string.Empty
                    : NormalizeSenseField(
                            fifthSeparator < 0
                                ? line[(fourthSeparator + 1)..]
                                : line[(fourthSeparator + 1)..fifthSeparator],
                            64)
                        .ToLowerInvariant(),
                partOfSpeechPool);
            string enrichment = InternSenseField(
                fifthSeparator < 0
                    ? string.Empty
                    : NormalizeSenseField(line[(fifthSeparator + 1)..], 96),
                enrichmentPool);
            if (key.Length == 0 ||
                key.Length > 256 ||
                translation.Length == 0 ||
                definition.Length == 0)
            {
                continue;
            }

            if (!entries.TryGetValue(key, out List<DictionarySenseCandidate>? candidates))
            {
                candidates = [];
                entries[key] = candidates;
            }

            if (candidates.Count >= MaximumSenseCountPerKey)
            {
                continue;
            }

            DictionarySenseCandidate candidate = new(
                translation,
                definition,
                register,
                partOfSpeech)
            {
                Enrichment = enrichment
            };
            if (!candidates.Contains(candidate))
            {
                candidates.Add(candidate);
            }
        }
    }

    private static byte[] EncodeSenseCandidates(IReadOnlyList<DictionarySenseCandidate> candidates)
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(candidates.Count);
            foreach (DictionarySenseCandidate candidate in candidates)
            {
                writer.Write(candidate.Translation);
                writer.Write(candidate.EnglishDefinition);
                writer.Write(candidate.Register);
                writer.Write(candidate.PartOfSpeech);
                writer.Write(candidate.Enrichment);
            }
        }

        return stream.ToArray();
    }

    private static DictionarySenseCandidate[] DecodeSenseCandidates(byte[] blob)
    {
        using MemoryStream stream = new(blob, writable: false);
        using BinaryReader reader = new(stream, Encoding.UTF8);
        int count = reader.ReadInt32();
        var candidates = new DictionarySenseCandidate[count];
        for (int index = 0; index < count; index++)
        {
            // Field order and count must mirror EncodeSenseCandidates exactly:
            // a missing read does not fail, it shifts every later candidate by
            // one field and silently corrupts the whole blob.
            candidates[index] = new DictionarySenseCandidate(
                reader.ReadString(),
                reader.ReadString(),
                reader.ReadString(),
                reader.ReadString())
            {
                Enrichment = reader.ReadString()
            };
        }

        return candidates;
    }

    private static string NormalizeSenseField(string value, int maximumLength)
    {
        string normalized = string.Join(
            " ",
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maximumLength
            ? normalized
            : normalized[..maximumLength].TrimEnd();
    }

    private static string InternSenseField(
        string value,
        Dictionary<string, string> pool)
    {
        if (pool.TryGetValue(value, out string? interned))
        {
            return interned;
        }

        pool[value] = value;
        return value;
    }

    internal static string MergeBundledMeanings(string preferred, string additional)
    {
        if (string.Equals(preferred, additional, StringComparison.Ordinal))
        {
            return preferred;
        }

        List<string> meanings = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        int valueLength = 0;

        Add(preferred);
        Add(additional);
        return meanings.Count == 0 ? preferred : string.Join("; ", meanings);

        void Add(string value)
        {
            foreach (string rawMeaning in value.Split(
                         ';',
                         StringSplitOptions.RemoveEmptyEntries |
                         StringSplitOptions.TrimEntries))
            {
                if (meanings.Count >= MaximumMergedMeaningCount)
                {
                    return;
                }

                string meaning = rawMeaning.Trim();
                string deduplicationKey = NormalizeMeaningForDeduplication(meaning);
                if (deduplicationKey.Length == 0 || !seen.Add(deduplicationKey))
                {
                    continue;
                }

                int separatorLength = meanings.Count == 0 ? 0 : 2;
                if (meaning.Length > MaximumMergedValueLength ||
                    valueLength + separatorLength + meaning.Length > MaximumMergedValueLength)
                {
                    continue;
                }

                meanings.Add(meaning);
                valueLength += separatorLength + meaning.Length;
            }
        }
    }

    private static string NormalizeMeaningForDeduplication(string value)
    {
        string decomposed = value.Normalize(NormalizationForm.FormD);
        StringBuilder normalized = new(decomposed.Length);
        bool pendingSpace = false;
        foreach (char character in decomposed)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category is UnicodeCategory.NonSpacingMark or
                UnicodeCategory.SpacingCombiningMark or
                UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                pendingSpace = normalized.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                normalized.Append(' ');
                pendingSpace = false;
            }

            char lowered = char.ToLowerInvariant(character);
            normalized.Append(lowered == 'ё' ? 'е' : lowered);
        }

        return normalized.ToString().Trim();
    }

    private static async Task MergeTrustedEnglishFileAsync(
        string path,
        Dictionary<string, string> entries,
        HashSet<string> userKeys,
        bool userLayer)
    {
        await using FileStream file = File.OpenRead(path);
        Stream input = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new GZipStream(file, CompressionMode.Decompress)
            : file;
        using StreamReader reader = new(input, Encoding.UTF8, true, 16 * 1024);
        while (await reader.ReadLineAsync() is { } line)
        {
            int separator = line.IndexOf('\t');
            if (separator <= 0 || separator == line.Length - 1)
            {
                continue;
            }

            string key = NormalizeEnglish(line[..separator]);
            if (key.Length == 0)
            {
                continue;
            }

            string value = line[(separator + 1)..];
            if (userLayer)
            {
                entries[key] = value;
                userKeys.Add(key);
            }
            else if (!userKeys.Contains(key))
            {
                entries[key] = value;
            }
        }
    }

    private DictionaryLookupResult LookupEnglish(
        EnglishLookupContext context,
        string hoveredWord,
        int characterIndex,
        bool includeSenseMetadata)
    {
        // Lookup anchors include numbers even though dialogue sentence keys do
        // not. Otherwise valid lexical keys such as "101", "24/7" and
        // "3d printer" are loaded canonically but can never be reached.
        string text = context.Text;
        MatchCollection tokens = context.Tokens;
        if (tokens.Count == 0)
        {
            return Missing(hoveredWord, "EN");
        }

        int tokenIndex = FindClosestToken(tokens, characterIndex, hoveredWord);
        string normalizedSentence = context.SentenceKey;
        _dialogueEnglishTranslations.TryGetValue(
            normalizedSentence,
            out string? dialogueTranslation);

        Match hoveredToken = tokens[tokenIndex];
        string lexicalSurface = ExtractEnglishLexeme(text, hoveredToken);
        string normalizedWord = context.NormalizedTokens[tokenIndex];
        string lexicalSurfaceKey = NormalizeEnglish(lexicalSurface);
        if (!string.Equals(lexicalSurfaceKey, normalizedWord, StringComparison.Ordinal) &&
            _english.TryGetValue(lexicalSurfaceKey, out string? lexicalSurfaceTranslation))
        {
            return Found(
                TrimEnglishDisplayPunctuation(lexicalSurface),
                lexicalSurfaceTranslation,
                false,
                "EN",
                lexicalSurfaceKey,
                includeSenseMetadata,
                dialogueTranslation);
        }

        // A masked spelling is split by the token regex ("b*stard" becomes "b"
        // and "stard"), so the curated table has to be consulted on the whole
        // whitespace-delimited lexeme rather than on the anchor token. The
        // displayed source stays the masked form the user actually saw.
        if (MaskedEnglish.TryGetValue(lexicalSurfaceKey, out string? unmaskedLexeme) &&
            _english.TryGetValue(unmaskedLexeme, out string? unmaskedTranslation))
        {
            return Found(
                TrimEnglishDisplayPunctuation(lexicalSurface),
                unmaskedTranslation,
                false,
                "EN",
                unmaskedLexeme,
                includeSenseMetadata,
                dialogueTranslation);
        }

        int maximumTokens = Math.Min(MaximumTrustedPhraseTokens, tokens.Count);
        for (int length = maximumTokens; length >= 2; length--)
        {
            int firstStart = Math.Max(0, tokenIndex - length + 1);
            int lastStart = Math.Min(tokenIndex, tokens.Count - length);
            for (int start = firstStart; start <= lastStart; start++)
            {
                string phrase = string.Join(
                    " ",
                    Enumerable.Range(start, length).Select(index => tokens[index].Value));
                string key = NormalizeEnglish(phrase);
                string rawPhrase = ExtractEnglishPhrase(text, tokens, start, length);
                string rawKey = NormalizeEnglish(rawPhrase);
                string[] lookupKeys = rawKey.Contains(' ') &&
                                      !string.Equals(rawKey, key, StringComparison.Ordinal)
                    ? [rawKey, key]
                    : [key];
                foreach (string lookupKey in lookupKeys)
                {
                    if (_english.TryGetValue(lookupKey, out string? translation) &&
                        (length <= MaximumGeneralPhraseTokens ||
                         _preferredEnglishSurfaceForms.Contains(lookupKey) ||
                         length <= MaximumLexicalPhraseTokens &&
                         _lexicalEnglishPhrases.Contains(lookupKey)))
                    {
                        return Found(
                            string.Equals(lookupKey, rawKey, StringComparison.Ordinal)
                                ? TrimEnglishDisplayPunctuation(rawPhrase)
                                : phrase,
                            translation,
                            true,
                            "EN",
                            lookupKey,
                            includeSenseMetadata,
                            dialogueTranslation);
                    }
                }

                if (length > MaximumLexicalPhraseTokens)
                {
                    continue;
                }

                foreach (string canonicalKey in CanonicalEnglishPhraseCandidates(
                             context,
                             start,
                             length,
                             key))
                {
                    if (!_lexicalEnglishPhrases.Contains(canonicalKey) ||
                        !_english.TryGetValue(
                            canonicalKey,
                            out string? canonicalTranslation))
                    {
                        continue;
                    }

                    return Found(
                        phrase,
                        canonicalTranslation,
                        true,
                        "EN",
                        canonicalKey,
                        includeSenseMetadata,
                        dialogueTranslation);
                }
            }
        }

        string word = hoveredToken.Value;
        bool preferredSurface = _preferredEnglishSurfaceForms.Contains(normalizedWord);
        bool softSurface = _softEnglishSurfaceForms.Contains(normalizedWord);
        // VN-hard rows are exact display repairs, not lemma declarations. Letting
        // suffix heuristics derive them turns ordinary words such as "lolling"
        // into the chat acronym "lol"; explicit user entries retain that authority.
        string[] candidates = EnglishCandidates(word, false)
            .Where(candidate =>
                string.Equals(candidate, normalizedWord, StringComparison.Ordinal) ||
                !_preferredEnglishSurfaceForms.Contains(candidate) ||
                _userEnglishKeys.Contains(candidate))
            .ToArray();
        string[] preferredLemmaCandidates = preferredSurface
            ? []
            : candidates
                .Skip(1)
                .Where(candidate =>
                    _preferredEnglishSurfaceForms.Contains(candidate) &&
                    _userEnglishKeys.Contains(candidate))
                .ToArray();
        string[] softLemmaCandidates = preferredSurface || softSurface
            ? []
            : candidates
                .Skip(1)
                .Where(candidate =>
                    _softEnglishSurfaceForms.Contains(candidate) &&
                    CanPromoteSoftLemma(
                        normalizedWord,
                        candidate,
                        tokens,
                        tokenIndex))
                .ToArray();
        IEnumerable<string> orderedCandidates;
        if (preferredLemmaCandidates.Length > 0)
        {
            HashSet<string> preferredLemmas = new(
                preferredLemmaCandidates,
                StringComparer.Ordinal);
            orderedCandidates = preferredLemmaCandidates.Concat(
                candidates.Where(candidate => !preferredLemmas.Contains(candidate)));
        }
        else if (softLemmaCandidates.Length > 0)
        {
            HashSet<string> softLemmas = new(
                softLemmaCandidates,
                StringComparer.Ordinal);
            orderedCandidates = softLemmaCandidates.Concat(
                candidates.Where(candidate => !softLemmas.Contains(candidate)));
        }
        else
        {
            bool preferLemma = !preferredSurface &&
                               ShouldPreferEnglishLemma(tokens, tokenIndex);
            orderedCandidates = preferLemma && candidates.Length > 1
                ? candidates.Skip(1).Concat(candidates.Take(1))
                : candidates;
        }

        foreach (string candidate in orderedCandidates)
        {
            if (_english.TryGetValue(candidate, out string? translation))
            {
                return Found(
                    word,
                    translation,
                    false,
                    "EN",
                    candidate,
                    includeSenseMetadata,
                    dialogueTranslation);
            }
        }

        if (!string.IsNullOrWhiteSpace(dialogueTranslation))
        {
            return Found(
                text,
                dialogueTranslation,
                true,
                "EN",
                normalizedSentence,
                includeSenseMetadata: false,
                contextTranslation: dialogueTranslation);
        }

        return Missing(word, "EN");
    }

    private static string ExtractEnglishLexeme(string text, Match token)
    {
        int start = token.Index;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1]))
        {
            start--;
        }

        int end = token.Index + token.Length;
        while (end < text.Length && !char.IsWhiteSpace(text[end]))
        {
            end++;
        }

        return text[start..end];
    }

    private static string ExtractEnglishPhrase(
        string text,
        MatchCollection tokens,
        int start,
        int length)
    {
        int first = tokens[start].Index;
        int nextToken = start + length;
        int end = nextToken < tokens.Count ? tokens[nextToken].Index : text.Length;
        return text[first..end].TrimEnd();
    }

    private static string TrimEnglishDisplayPunctuation(string value) =>
        value.Trim(' ', '.', ',', '!', '?', ':', ';', '"', '“', '”', '(', ')', '[', ']');

    private DictionaryLookupResult LookupJapanese(string text, string hoveredWord, int characterIndex)
    {
        (string normalized, int anchor) = NormalizeJapaneseWithAnchor(text, characterIndex);
        if (normalized.Length == 0)
        {
            normalized = NormalizeJapanese(hoveredWord);
            anchor = Math.Max(0, normalized.Length / 2);
        }

        _dialogueJapaneseTranslations.TryGetValue(normalized, out string? dialogueTranslation);
        int maximumLength = Math.Min(_maximumJapaneseLength, normalized.Length);
        for (int length = maximumLength; length >= 1; length--)
        {
            int firstStart = Math.Max(0, anchor - length + 1);
            int lastStart = Math.Min(anchor, normalized.Length - length);
            for (int start = firstStart; start <= lastStart; start++)
            {
                string candidate = normalized.Substring(start, length);
                if (_japanese.TryGetValue(candidate, out string? translation))
                {
                    return Found(
                        candidate,
                        translation,
                        length > 1,
                        "JA",
                        contextTranslation: dialogueTranslation);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(dialogueTranslation))
        {
            return Found(
                text,
                dialogueTranslation,
                true,
                "JA",
                contextTranslation: dialogueTranslation);
        }

        string fallback = NormalizeJapanese(hoveredWord);
        return Missing(fallback.Length == 0 ? hoveredWord : fallback, "JA");
    }

    private static int FindClosestToken(MatchCollection tokens, int characterIndex, string hoveredWord)
    {
        for (int index = 0; index < tokens.Count; index++)
        {
            Match token = tokens[index];
            if (characterIndex >= token.Index && characterIndex <= token.Index + token.Length)
            {
                return index;
            }
        }

        string normalizedHovered = NormalizeEnglish(hoveredWord);
        for (int index = 0; index < tokens.Count; index++)
        {
            if (NormalizeEnglish(tokens[index].Value) == normalizedHovered)
            {
                return index;
            }
        }

        return Enumerable.Range(0, tokens.Count)
            .MinBy(index => Math.Abs(tokens[index].Index - characterIndex));
    }

    private static IEnumerable<string> EnglishCandidates(string value, bool preferLemma)
    {
        string word = NormalizeEnglish(value);
        HashSet<string> yielded = new(StringComparer.Ordinal);
        List<string> candidates = [];

        foreach (string candidate in CandidateSequence(word))
        {
            if (candidate.Length > 0 && yielded.Add(candidate))
            {
                candidates.Add(candidate);
            }
        }

        if (preferLemma && candidates.Count > 1)
        {
            foreach (string candidate in candidates.Skip(1))
            {
                yield return candidate;
            }

            yield return candidates[0];
            yield break;
        }

        foreach (string candidate in candidates)
        {
            yield return candidate;
        }
    }

    private static IEnumerable<string> CanonicalEnglishPhraseCandidates(
        EnglishLookupContext context,
        int start,
        int length,
        string exactKey)
    {
        string[] canonical = new string[length];
        for (int offset = 0; offset < length; offset++)
        {
            canonical[offset] =
                NormalizeEnglishPossessive(context.NormalizedTokens[start + offset]);
        }
        HashSet<string> yielded = new(StringComparer.Ordinal) { exactKey };

        string possessiveNormalized = string.Join(" ", canonical);
        if (yielded.Add(possessiveNormalized))
        {
            yield return possessiveNormalized;
        }

        // Keep morphology bounded: normalize one inflected token at a time on
        // top of possessive normalization. This covers lexical MWEs such as
        // "shook her head" -> "shake one's head" without producing an
        // exponential number of sentence guesses.
        for (int tokenOffset = 0; tokenOffset < canonical.Length; tokenOffset++)
        {
            foreach (string lemma in context.LemmasFor(canonical[tokenOffset]))
            {
                string original = canonical[tokenOffset];
                canonical[tokenOffset] = lemma;
                string candidate = string.Join(" ", canonical);
                canonical[tokenOffset] = original;
                if (yielded.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }
    }

    private static string NormalizeEnglishPossessive(string value) =>
        value is "my" or "your" or "his" or "her" or "its" or "our" or "their"
            ? "one's"
            : value;

    private static bool ShouldPreferEnglishLemma(MatchCollection tokens, int tokenIndex)
    {
        if (tokenIndex <= 0)
        {
            return false;
        }

        string word = NormalizeEnglish(tokens[tokenIndex].Value);
        string previous = NormalizeEnglish(tokens[tokenIndex - 1].Value);
        bool progressive = word.EndsWith("ing", StringComparison.Ordinal) &&
                           previous is "am" or "is" or "are" or "was" or "were" or "be" or "been";
        bool perfect = word.EndsWith("ed", StringComparison.Ordinal) &&
                       previous is "have" or "has" or "had";
        return progressive || perfect;
    }

    private static bool CanPromoteSoftLemma(
        string surface,
        string lemma,
        MatchCollection tokens,
        int tokenIndex)
    {
        // Soft data may grow automatically, so it must not turn an ambiguous
        // irregular homograph such as "saw" into "see" ahead of an exact broad
        // entry. Only regular verbal morphology with explicit form evidence is
        // safe enough to expose the reviewed lemma and its aligned senses.
        if (IrregularEnglish.ContainsKey(surface))
        {
            return false;
        }

        if (surface.EndsWith("ing", StringComparison.Ordinal))
        {
            return ShouldPreferEnglishLemma(tokens, tokenIndex) &&
                   EnglishCandidates(surface, false)
                       .Skip(1)
                       .Contains(lemma, StringComparer.Ordinal);
        }

        return EnglishContextLexicon.IsLikelyPastForm(surface) &&
               EnglishCandidates(surface, false)
                   .Skip(1)
                   .Contains(lemma, StringComparer.Ordinal);
    }

    private static IEnumerable<string> CandidateSequence(string word)
    {
        yield return word;

        // After the exact form: a dictionary that carries the masked spelling
        // itself (Wiktionary has "f*ck", "s**t" and others) keeps winning, and
        // the unmasked word is only a fallback for the spellings it lacks.
        if (MaskedEnglish.TryGetValue(word, out string? unmasked))
        {
            yield return unmasked;
        }

        if (IrregularEnglish.TryGetValue(word, out string? irregular))
        {
            yield return irregular;
        }

        if (word.EndsWith("'s", StringComparison.Ordinal) || word.EndsWith("’s", StringComparison.Ordinal))
        {
            yield return word[..^2];
        }

        if (word.EndsWith("ies", StringComparison.Ordinal) && word.Length > 4)
        {
            yield return word[..^3] + "y";
        }

        if (word.EndsWith("ing", StringComparison.Ordinal) && word.Length > 5)
        {
            string stem = word[..^3];
            yield return stem;
            yield return stem + "e";
            if (EndsWithDoubledLetter(stem))
            {
                yield return stem[..^1];
            }
        }

        if (word.EndsWith("ied", StringComparison.Ordinal) && word.Length > 4)
        {
            yield return word[..^3] + "y";
        }
        else if (word.EndsWith("ed", StringComparison.Ordinal) && word.Length > 4)
        {
            string stem = word[..^2];
            yield return stem;
            yield return stem + "e";
            if (EndsWithDoubledLetter(stem))
            {
                yield return stem[..^1];
            }
        }

        if (word.EndsWith("es", StringComparison.Ordinal) && word.Length > 4)
        {
            yield return word[..^2];
            yield return word[..^1];
        }

        if (word.EndsWith('s') && word.Length > 3 && !word.EndsWith("ss", StringComparison.Ordinal))
        {
            yield return word[..^1];
        }
    }

    private static bool EndsWithDoubledLetter(string value) =>
        value.Length > 1 && value[^1] == value[^2];

    // Lexical keys retain internal punctuation and digits. Only surrounding
    // display punctuation is trimmed; sentence keys use the token-only helper
    // below. The generator mirrors both forms exactly (invariant 1).
    internal static string NormalizeEnglish(string value)
    {
        string collapsed = string.Join(
                " ",
                (value ?? string.Empty)
                    .Normalize(NormalizationForm.FormC)
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Replace('’', '\'')
            .Trim(' ', '.', ',', '!', '?', ':', ';', '"', '“', '”', '(', ')', '[', ']');
        return FoldEnglishCase(collapsed);
    }

    // Dialogue sentence keys are built by build-dictionaries.py as tokens joined
    // with single spaces and no punctuation. Trim-based normalization keeps
    // internal commas and quotes and would never match those keys.
    internal static string NormalizeEnglishSentenceKey(string value) =>
        string.Join(
            " ",
            EnglishTokenRegex()
                .Matches((value ?? string.Empty).Normalize(NormalizationForm.FormC))
                .Select(match => FoldEnglishCase(match.Value.Replace('’', '\''))));

    // Unicode default lower-casing is one-to-one in .NET. These expanding
    // folds cover the English-adjacent characters accepted by our dictionaries
    // and mirror canonical_casefold in build-dictionaries.py.
    private static string FoldEnglishCase(string value)
    {
        // Every expanding fold below is non-ASCII, so ordinary English text
        // needs none of the twelve scans. This is the hottest string operation
        // in the project — it runs per token and per phrase candidate — so the
        // common case gets a single pass instead of thirteen.
        if (IsAscii(value))
        {
            return LowerInvariant(value);
        }

        string expanded = value
            .Replace("ß", "ss", StringComparison.Ordinal)
            .Replace("ẞ", "ss", StringComparison.Ordinal)
            .Replace("İ", "i\u0307", StringComparison.Ordinal)
            .Replace("ſ", "s", StringComparison.Ordinal)
            .Replace("ς", "σ", StringComparison.Ordinal)
            .Replace("ﬀ", "ff", StringComparison.Ordinal)
            .Replace("ﬁ", "fi", StringComparison.Ordinal)
            .Replace("ﬂ", "fl", StringComparison.Ordinal)
            .Replace("ﬃ", "ffi", StringComparison.Ordinal)
            .Replace("ﬄ", "ffl", StringComparison.Ordinal)
            .Replace("ﬅ", "st", StringComparison.Ordinal)
            .Replace("ﬆ", "st", StringComparison.Ordinal);
        return LowerInvariant(expanded);
    }

    private static bool IsAscii(string value)
    {
        foreach (char character in value)
        {
            if (character > 0x7F)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Per-character invariant lowering, identical to the previous
    /// <c>Select(char.ToLowerInvariant)</c> but without the enumerator and the
    /// intermediate array. Deliberately not <c>string.ToLowerInvariant()</c>:
    /// that applies full case mapping, which is not one-to-one and would change
    /// canonical keys (invariant 1).
    /// </summary>
    private static string LowerInvariant(string value) =>
        string.Create(value.Length, value, static (destination, source) =>
        {
            for (int index = 0; index < source.Length; index++)
            {
                destination[index] = char.ToLowerInvariant(source[index]);
            }
        });

    private static (string Text, int Anchor) NormalizeJapaneseWithAnchor(string value, int characterIndex)
    {
        StringBuilder builder = new(value.Length);
        int anchor = 0;
        for (int index = 0; index < value.Length; index++)
        {
            if (!IsJapaneseDictionaryCharacter(value[index]))
            {
                continue;
            }

            if (index <= characterIndex)
            {
                anchor = builder.Length;
            }

            builder.Append(value[index]);
        }

        return (builder.ToString(), Math.Clamp(anchor, 0, Math.Max(0, builder.Length - 1)));
    }

    internal static string NormalizeJapanese(string value) =>
        new(value.Where(IsJapaneseDictionaryCharacter).ToArray());

    private static bool ContainsJapanese(string value) => value.Any(character =>
        character is >= '\u3040' and <= '\u30ff' or >= '\u3400' and <= '\u9fff');

    private static bool IsJapaneseDictionaryCharacter(char character) =>
        character is >= '\u3040' and <= '\u30ff' or
            >= '\u3400' and <= '\u9fff' or
            >= '\uf900' and <= '\ufaff' or
            '々' or '〆' or 'ヶ';

    private DictionaryLookupResult Found(
        string source,
        string translation,
        bool isPhrase,
        string language,
        string? matchedEnglishKey = null,
        bool includeSenseMetadata = false,
        string? contextTranslation = null)
    {
        IReadOnlyList<DictionarySenseCandidate> senses =
            includeSenseMetadata &&
            matchedEnglishKey is not null &&
            !_preferredEnglishSurfaceForms.Contains(matchedEnglishKey) &&
            _englishSenseMetadata.TryGetValue(
                matchedEnglishKey,
                out byte[]? senseBlob)
                ? DecodeSenseCandidates(senseBlob)
                : Array.Empty<DictionarySenseCandidate>();
        return new DictionaryLookupResult(
            true,
            source,
            Limit(translation),
            isPhrase,
            language)
        {
            SenseCandidates = senses,
            ContextTranslation = contextTranslation ?? string.Empty,
            IsTrustedPhrase = isPhrase &&
                              matchedEnglishKey is not null &&
                              _preferredEnglishSurfaceForms.Contains(matchedEnglishKey),
            HasMultipleMeanings =
                NpuDictionarySelectorService.SplitMeanings(translation).Length > 1,
            MatchedEnglishKey = matchedEnglishKey ?? string.Empty
        };
    }

    private static DictionaryLookupResult Missing(string source, string language) =>
        new(false, source, "Нет записи в локальном словаре", false, language);

    private static string Limit(string value)
    {
        if (value.Length <= MaximumDisplayedValueLength)
        {
            return value;
        }

        // Never cut inside a meaning: a partial chunk would otherwise look
        // like a real dictionary variant to SplitMeanings-based rankers.
        // The three reserved characters are the "; …" continuation marker.
        string truncated = value[..(MaximumDisplayedValueLength - 3)];
        int boundary = truncated.LastIndexOf("; ", StringComparison.Ordinal);
        return boundary > 0
            ? truncated[..boundary] + "; …"
            : truncated.TrimEnd() + "…";
    }

    [GeneratedRegex(@"[\p{L}\p{M}]+(?:['’\-][\p{L}\p{M}]+)*", RegexOptions.CultureInvariant)]
    private static partial Regex EnglishTokenRegex();

    [GeneratedRegex(@"[\p{L}\p{M}\p{N}]+(?:['’\-][\p{L}\p{M}\p{N}]+)*", RegexOptions.CultureInvariant)]
    private static partial Regex EnglishLookupTokenRegex();

    private sealed record LoadedDictionary(
        Dictionary<string, string> Entries,
        int SourceCount,
        HashSet<string> UserKeys,
        HashSet<string> PreferredSurfaceForms,
        HashSet<string> SoftSurfaceForms,
        HashSet<string> LexicalPhrases,
        Dictionary<string, string> ScaffoldTranslations,
        Dictionary<string, string> DialogueTranslations,
        string VersionToken);

    private enum DictionaryFileRole
    {
        Broad,
        VnSoft,
        VnHard,
        SenseMetadata,
        Scaffold,
        ExactDialogue
    }
}
