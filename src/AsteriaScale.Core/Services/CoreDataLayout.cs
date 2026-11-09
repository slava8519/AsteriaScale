namespace AsteriaScale.Services;

/// <summary>
/// Where Core reads its bundled data and keeps per-user state. The two halves
/// are resolved independently:
/// <list type="bullet">
/// <item>bundled: Dictionaries/ and Morphology/ under <see cref="BundledDataRoot"/>
/// (TextHook/ is a WPF concern and stays there);</item>
/// <item>user: settings, the user dictionaries, history, the three caches, the
/// hover log and the TranslateGemma models.</item>
/// </list>
/// The Windows host keeps the layout it always had: bundled data beside the
/// physical executable and user state in LocalApplicationData\AsteriaScale.
/// Another host (the macOS CoreHost) passes every root through
/// <see cref="Configure"/> before the first Core service reads one.
///
/// Every root is fully qualified. On macOS LocalApplicationData is "" when
/// ~/Library/Application Support does not exist, and combining "" with a file
/// name used to move settings, caches and the hover log silently into the
/// working directory. An unresolved user root now throws on use, while the
/// bundled half keeps working: the morphology lexicon caches its first load in
/// a Lazy, so a user-root failure must never reach it.
/// </summary>
public sealed class CoreDataLayout
{
    internal const string UnresolvedUserDataRootMessage =
        "Система не сообщила каталог данных пользователя (LocalApplicationData); " +
        "настройки и словари не будут записаны относительно текущего каталога.";

    private static readonly CoreDataLayoutSlot ProcessSlot = new(ForCurrentProcess);

    // Null only in the unresolved state built by the private constructor.
    private readonly string? _userDataRoot;
    private readonly string? _cacheRoot;
    private readonly string? _logRoot;
    private readonly string? _phraseModelRoot;

    public CoreDataLayout(
        string bundledDataRoot,
        string userDataRoot,
        string? cacheRoot = null,
        string? logRoot = null,
        string? phraseModelRoot = null)
    {
        BundledDataRoot = RequireRoot(bundledDataRoot, nameof(bundledDataRoot));
        _userDataRoot = RequireRoot(userDataRoot, nameof(userDataRoot));
        _cacheRoot = cacheRoot is null ? null : RequireRoot(cacheRoot, nameof(cacheRoot));
        _logRoot = logRoot is null ? null : RequireRoot(logRoot, nameof(logRoot));
        _phraseModelRoot = phraseModelRoot is null
            ? null
            : RequireRoot(phraseModelRoot, nameof(phraseModelRoot));
    }

    // The marker's type is private, so no call with string arguments can bind
    // here; a (string, string) overload would beat the public constructor's
    // optional parameters and turn real roots into the failure state.
    private CoreDataLayout(string bundledDataRoot, UnresolvedUserData unresolved)
    {
        ArgumentNullException.ThrowIfNull(unresolved);
        BundledDataRoot = RequireRoot(bundledDataRoot, nameof(bundledDataRoot));
    }

    /// <summary>Holds Dictionaries/ and Morphology/.</summary>
    public string BundledDataRoot { get; }

    public string BundledDictionaryDirectory => Path.Combine(BundledDataRoot, "Dictionaries");

    public string MorphologyFile => Path.Combine(BundledDataRoot, "Morphology", "ru-morph.tsv.gz");

    public string UserDataRoot =>
        _userDataRoot ?? throw new InvalidOperationException(UnresolvedUserDataRootMessage);

    public string CacheRoot => _cacheRoot ?? UserDataRoot;

    public string LogRoot => _logRoot ?? UserDataRoot;

    public string PhraseModelRoot =>
        _phraseModelRoot ?? Path.Combine(UserDataRoot, "Models", "TranslateGemma");

    // The file names are the ones every existing install already has on disk
    // (ARCHITECTURE.md, «Данные на машине пользователя»).
    public string SettingsFile => Path.Combine(UserDataRoot, "settings.json");

    public string UserDictionaryDirectory => Path.Combine(UserDataRoot, "Dictionaries");

    public string HistoryFile => Path.Combine(UserDataRoot, "translation-history.v1.json");

    public string SmartResultCacheFile => Path.Combine(CacheRoot, "smart-dictionary-cache.v2.json");

    public string PhraseCacheFile => Path.Combine(CacheRoot, "smart-phrase-translations.v7.json");

    public string DecisionCacheFile => Path.Combine(CacheRoot, "glossbert-decisions.v1.json");

    public string HoverLogFile => Path.Combine(LogRoot, "hover-translation.log");

    /// <summary>
    /// The process-wide layout. Unless a host configured one, the first read
    /// resolves the Windows layout and fixes it for the process lifetime.
    /// </summary>
    public static CoreDataLayout Current => ProcessSlot.Current;

    /// <summary>
    /// Sets the process-wide layout once, before anything reads it. A second
    /// call, or a call after the first read, throws: services created earlier
    /// would keep using the other roots.
    /// </summary>
    public static void Configure(CoreDataLayout layout) => ProcessSlot.Configure(layout);

    /// <summary>
    /// The pure resolver behind the Windows default. Bundled data sits beside
    /// the physical executable, by the same companion rule as the ranker worker
    /// (a single-file host's AppContext.BaseDirectory is its extraction folder).
    /// User state sits in &lt;localApplicationData&gt;/AsteriaScale; an empty or
    /// relative answer leaves the user half unresolved instead of relative.
    /// </summary>
    internal static CoreDataLayout Resolve(
        string? processPath,
        string appContextBaseDirectory,
        string localApplicationData)
    {
        string bundledDataRoot = GlossBertDictionarySelectorService.ResolveCompanionBaseDirectory(
            processPath,
            appContextBaseDirectory);
        if (string.IsNullOrWhiteSpace(localApplicationData) ||
            !Path.IsPathFullyQualified(localApplicationData))
        {
            return new CoreDataLayout(bundledDataRoot, UnresolvedUserData.Instance);
        }

        return new CoreDataLayout(
            bundledDataRoot,
            Path.Combine(localApplicationData, "AsteriaScale"));
    }

    private static CoreDataLayout ForCurrentProcess() =>
        Resolve(
            Environment.ProcessPath,
            AppContext.BaseDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    private static string RequireRoot(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!Path.IsPathFullyQualified(value))
        {
            throw new ArgumentException(
                $"Каталог данных ядра должен быть абсолютным путём: '{value}'.",
                parameterName);
        }

        return value;
    }

    private sealed class UnresolvedUserData
    {
        internal static readonly UnresolvedUserData Instance = new();

        private UnresolvedUserData()
        {
        }
    }
}

/// <summary>
/// The configure-once cell behind <see cref="CoreDataLayout.Current"/>. It is
/// a separate type so tests can check the once-only rule on their own slot
/// without touching the process-wide one.
/// </summary>
internal sealed class CoreDataLayoutSlot
{
    private readonly object _gate = new();
    private readonly Func<CoreDataLayout> _resolveDefault;
    private CoreDataLayout? _layout;

    internal CoreDataLayoutSlot(Func<CoreDataLayout> resolveDefault)
    {
        ArgumentNullException.ThrowIfNull(resolveDefault);
        _resolveDefault = resolveDefault;
    }

    internal CoreDataLayout Current
    {
        get
        {
            lock (_gate)
            {
                return _layout ??= _resolveDefault();
            }
        }
    }

    internal void Configure(CoreDataLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        lock (_gate)
        {
            if (_layout is not null)
            {
                throw new InvalidOperationException(
                    "Расположение данных ядра уже задано или прочитано; " +
                    "менять его после первого использования нельзя.");
            }

            _layout = layout;
        }
    }
}
