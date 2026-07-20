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
}

public sealed record DictionarySenseCandidate(
    string Translation,
    string EnglishDefinition,
    string Register,
    string PartOfSpeech = "");

public sealed partial class LocalDictionaryService
{
    public const string EngineLabel = "NPU OCR  •  СЛОВАРЬ";
    private const int MaximumGeneralPhraseTokens = 3;
    private const int MaximumLexicalPhraseTokens = 6;
    // The merged value only feeds a 380-character tooltip and an
    // eight-candidate SplitMeanings ranking, so a deeper tail is dead weight.
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
    private Dictionary<string, string> _english = new(StringComparer.Ordinal);
    private Dictionary<string, string> _japanese = new(StringComparer.Ordinal);
    private Dictionary<string, string> _trustedEnglishExact = new(StringComparer.Ordinal);
    private Dictionary<string, DictionarySenseCandidate[]> _englishSenseMetadata =
        new(StringComparer.Ordinal);
    private HashSet<string> _preferredEnglishSurfaceForms = new(StringComparer.Ordinal);
    private HashSet<string> _lexicalEnglishPhrases = new(StringComparer.Ordinal);
    private HashSet<string> _dialogueEnglishSentences = new(StringComparer.Ordinal);
    private int _maximumJapaneseLength = 1;
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

    public int EntryCount => _english.Count + _japanese.Count;
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
            _loadTask ??= LoadCoreAsync();
            task = _loadTask;
        }

        return task.WaitAsync(cancellationToken);
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
            _trustedEnglishExact = new Dictionary<string, string>(StringComparer.Ordinal);
            _englishSenseMetadata =
                new Dictionary<string, DictionarySenseCandidate[]>(StringComparer.Ordinal);
            _preferredEnglishSurfaceForms = new HashSet<string>(StringComparer.Ordinal);
            _lexicalEnglishPhrases = new HashSet<string>(StringComparer.Ordinal);
            _dialogueEnglishSentences = new HashSet<string>(StringComparer.Ordinal);
            _maximumJapaneseLength = 1;
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
            : LookupEnglish(text, hoveredWord, characterIndex, includeSenseMetadata);
    }

    private async Task EnsureSenseMetadataLoadedAsync(CancellationToken cancellationToken)
    {
        Task task;
        lock (_loadLock)
        {
            _senseMetadataLoadTask ??= LoadEnglishSenseMetadataCoreAsync();
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
            _trustedLoadTask ??= LoadTrustedEnglishCoreAsync();
            task = _trustedLoadTask;
        }

        await task.WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return _trustedEnglishExact.TryGetValue(NormalizeEnglish(source), out string? translation)
            ? translation
            : null;
    }

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
        _preferredEnglishSurfaceForms = english.PreferredSurfaceForms;
        _lexicalEnglishPhrases = english.LexicalPhrases;
        _dialogueEnglishSentences = english.DialogueSentences;
        _sourceCount = english.SourceCount + japanese.SourceCount;
        _versionToken = english.VersionToken + "." + japanese.VersionToken;
        _maximumJapaneseLength = _japanese.Count == 0
            ? 1
            : Math.Min(32, _japanese.Keys.Max(key => key.Length));
    }

    private async Task LoadTrustedEnglishCoreAsync()
    {
        (string bundledDirectory, string userDirectory) = ResolveDictionaryDirectories();
        Directory.CreateDirectory(userDirectory);

        string[] files = DiscoverFiles(bundledDirectory, userDirectory, "eng-rus")
            .Where(path =>
                string.Equals(
                    Path.GetDirectoryName(path),
                    userDirectory,
                    StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(path).Contains("-vn.", StringComparison.OrdinalIgnoreCase))
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
                    partOfSpeechPool);
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

        Dictionary<string, DictionarySenseCandidate[]> compact =
            new(mutable.Count, StringComparer.Ordinal);
        int candidateCount = 0;
        foreach ((string key, List<DictionarySenseCandidate> candidates) in mutable)
        {
            if (!_english.ContainsKey(key) ||
                _preferredEnglishSurfaceForms.Contains(key))
            {
                continue;
            }

            compact[key] = candidates.ToArray();
            candidateCount += candidates.Count;
        }

        _englishSenseMetadata = compact;
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
        HashSet<string> lexicalPhrases = new(StringComparer.Ordinal);
        HashSet<string> dialogueSentences = new(StringComparer.Ordinal);
        bool japaneseLayer = prefix.StartsWith("jpn", StringComparison.OrdinalIgnoreCase);
        foreach (string path in files)
        {
            bool userLayer = string.Equals(
                Path.GetDirectoryName(path),
                userDirectory,
                StringComparison.OrdinalIgnoreCase);
            string fileName = Path.GetFileName(path);
            bool curatedLayer = fileName
                .Contains("-vn.", StringComparison.OrdinalIgnoreCase);
            bool dialogueLayer = fileName
                .Contains("-dialogue.", StringComparison.OrdinalIgnoreCase);
            await MergeFileAsync(
                path,
                entries,
                userKeys,
                preferredSurfaceForms,
                lexicalPhrases,
                dialogueSentences,
                userLayer,
                curatedLayer,
                dialogueLayer,
                japaneseLayer);
        }

        return new LoadedDictionary(
            entries,
            files.Count,
            preferredSurfaceForms,
            lexicalPhrases,
            dialogueSentences,
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

    private static int DictionaryPriority(string path, string prefix)
    {
        string name = Path.GetFileName(path);
        if (string.Equals(name, prefix + ".tsv", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, prefix + ".tsv.gz", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return name.Contains("-vn.", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
    }

    private static async Task MergeFileAsync(
        string path,
        Dictionary<string, string> entries,
        HashSet<string> userKeys,
        HashSet<string> preferredSurfaceForms,
        HashSet<string> lexicalPhrases,
        HashSet<string> dialogueSentences,
        bool userLayer,
        bool curatedLayer,
        bool dialogueLayer,
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
                : NormalizeEnglish(line[..separator]);
            if (key.Length == 0)
            {
                continue;
            }

            string value = line[(separator + 1)..];
            if (!dialogueLayer &&
                key.Contains(' '))
            {
                lexicalPhrases.Add(key);
            }
            else if (dialogueLayer &&
                     key.Contains(' '))
            {
                dialogueSentences.Add(key);
            }

            if (userLayer)
            {
                entries[key] = value;
                userKeys.Add(key);
                preferredSurfaceForms.Add(key);
            }
            else if (curatedLayer)
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
        Dictionary<string, string> partOfSpeechPool)
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
                    : NormalizeSenseField(line[(fourthSeparator + 1)..], 64)
                        .ToLowerInvariant(),
                partOfSpeechPool);
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
                partOfSpeech);
            if (!candidates.Contains(candidate))
            {
                candidates.Add(candidate);
            }
        }
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
        string text,
        string hoveredWord,
        int characterIndex,
        bool includeSenseMetadata)
    {
        MatchCollection tokens = EnglishTokenRegex().Matches(text);
        if (tokens.Count == 0)
        {
            return Missing(hoveredWord, "EN");
        }

        int tokenIndex = FindClosestToken(tokens, characterIndex, hoveredWord);
        string normalizedSentence = NormalizeEnglishSentenceKey(text);
        string? dialogueTranslation =
            _dialogueEnglishSentences.Contains(normalizedSentence) &&
            _english.TryGetValue(normalizedSentence, out string? exactSentenceTranslation)
                ? exactSentenceTranslation
                : null;
        int maximumTokens = Math.Min(10, tokens.Count);
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
                if (_english.TryGetValue(key, out string? translation) &&
                    (length <= MaximumGeneralPhraseTokens ||
                     _preferredEnglishSurfaceForms.Contains(key) ||
                     length <= MaximumLexicalPhraseTokens &&
                     _lexicalEnglishPhrases.Contains(key)))
                {
                    return Found(
                        phrase,
                        translation,
                        true,
                        "EN",
                        key,
                        includeSenseMetadata,
                        dialogueTranslation);
                }

                if (length > MaximumLexicalPhraseTokens)
                {
                    continue;
                }

                foreach (string canonicalKey in CanonicalEnglishPhraseCandidates(
                             tokens,
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

        string word = tokens[tokenIndex].Value;
        string normalizedWord = NormalizeEnglish(word);
        bool preferredSurface = _preferredEnglishSurfaceForms.Contains(normalizedWord);
        string[] candidates = EnglishCandidates(word, false).ToArray();
        string[] preferredLemmaCandidates = preferredSurface
            ? []
            : candidates
                .Skip(1)
                .Where(_preferredEnglishSurfaceForms.Contains)
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
        else
        {
            bool preferLemma = !preferredSurface &&
                               ShouldPreferEnglishLemma(tokens, tokenIndex);
            orderedCandidates = EnglishCandidates(word, preferLemma);
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

    private DictionaryLookupResult LookupJapanese(string text, string hoveredWord, int characterIndex)
    {
        (string normalized, int anchor) = NormalizeJapaneseWithAnchor(text, characterIndex);
        if (normalized.Length == 0)
        {
            normalized = NormalizeJapanese(hoveredWord);
            anchor = Math.Max(0, normalized.Length / 2);
        }

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
                    return Found(candidate, translation, length > 1, "JA");
                }
            }
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
        MatchCollection tokens,
        int start,
        int length,
        string exactKey)
    {
        string[] canonical = Enumerable.Range(start, length)
            .Select(index => NormalizeEnglish(tokens[index].Value))
            .Select(NormalizeEnglishPossessive)
            .ToArray();
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
            foreach (string lemma in EnglishCandidates(
                         canonical[tokenOffset],
                         preferLemma: false)
                     .Skip(1)
                     .Take(3))
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

    private static IEnumerable<string> CandidateSequence(string word)
    {
        yield return word;

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

    // OCR regularly reads the typographic apostrophe, while dictionary keys use
    // the straight one; both must resolve to the same canonical key.
    private static string NormalizeEnglish(string value) =>
        string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Replace('’', '\'')
            .Trim(' ', '.', ',', '!', '?', ':', ';', '"', '“', '”', '(', ')', '[', ']')
            .ToLowerInvariant();

    // Dialogue sentence keys are built by build-dictionaries.py as tokens joined
    // with single spaces and no punctuation. Trim-based normalization keeps
    // internal commas and quotes and would never match those keys.
    private static string NormalizeEnglishSentenceKey(string value) =>
        NormalizeEnglish(string.Join(
            " ",
            EnglishTokenRegex().Matches(value).Select(match => match.Value)));

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

    private static string NormalizeJapanese(string value) =>
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
                out DictionarySenseCandidate[]? candidates)
                ? candidates
                : Array.Empty<DictionarySenseCandidate>();
        return new DictionaryLookupResult(
            true,
            source,
            Limit(translation),
            isPhrase,
            language)
        {
            SenseCandidates = senses,
            ContextTranslation = contextTranslation ?? string.Empty
        };
    }

    private static DictionaryLookupResult Missing(string source, string language) =>
        new(false, source, "Нет записи в локальном словаре", false, language);

    private static string Limit(string value)
    {
        if (value.Length <= 380)
        {
            return value;
        }

        // Never cut inside a meaning: a partial chunk would otherwise look
        // like a real dictionary variant to SplitMeanings-based rankers.
        string truncated = value[..377];
        int boundary = truncated.LastIndexOf("; ", StringComparison.Ordinal);
        return boundary > 0
            ? truncated[..boundary] + "; …"
            : truncated.TrimEnd() + "…";
    }

    [GeneratedRegex(@"[\p{L}\p{M}]+(?:['’\-][\p{L}\p{M}]+)*", RegexOptions.CultureInvariant)]
    private static partial Regex EnglishTokenRegex();

    private sealed record LoadedDictionary(
        Dictionary<string, string> Entries,
        int SourceCount,
        HashSet<string> PreferredSurfaceForms,
        HashSet<string> LexicalPhrases,
        HashSet<string> DialogueSentences,
        string VersionToken);
}
