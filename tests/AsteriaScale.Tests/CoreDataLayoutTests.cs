using AsteriaScale.Services;

// CoreDataLayout: every default data path of Core. Called from the "Settings
// round trip" registration, so it adds no registration. It builds layouts
// through the pure resolver and private slots and never configures the
// process-wide layout; the macOS subset has already configured that one in
// MacSmoke.Begin, with a user half that fails loudly.
internal static class CoreDataLayoutTests
{
    // The user-half getters, by name: each must fail the same way when the
    // system reports no usable LocalApplicationData.
    private static readonly (string Name, Func<CoreDataLayout, string> Read)[] UserPaths =
    [
        ("UserDataRoot", layout => layout.UserDataRoot),
        ("CacheRoot", layout => layout.CacheRoot),
        ("LogRoot", layout => layout.LogRoot),
        ("PhraseModelRoot", layout => layout.PhraseModelRoot),
        ("SettingsFile", layout => layout.SettingsFile),
        ("UserDictionaryDirectory", layout => layout.UserDictionaryDirectory),
        ("HistoryFile", layout => layout.HistoryFile),
        ("SmartResultCacheFile", layout => layout.SmartResultCacheFile),
        ("PhraseCacheFile", layout => layout.PhraseCacheFile),
        ("DecisionCacheFile", layout => layout.DecisionCacheFile),
        ("HoverLogFile", layout => layout.HoverLogFile)
    ];

    public static async Task RunAsync(string tempRoot)
    {
        VerifyWindowsLayoutUnchanged();
        VerifyUnresolvedUserRoot(tempRoot);
        VerifyExplicitRoots(tempRoot);
        VerifyConfigureOnce(tempRoot);
        VerifyHoverLogNeverInWorkingDirectory(tempRoot);
#if !WINDOWS
        await VerifyMacSubsetDefaultsRefuseUserPathsAsync();
#else
        await Task.CompletedTask;
#endif
    }

    // For this machine, the resolver must give every one of the ten sites
    // the exact string its pre-seam expression built.
    private static void VerifyWindowsLayoutUnchanged()
    {
        string localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        CoreDataLayout layout = CoreDataLayout.Resolve(
            Environment.ProcessPath,
            AppContext.BaseDirectory,
            localApplicationData);

        // LocalDictionaryService and RussianMorphologyLexicon, verbatim.
        string executableDirectory = Path.GetDirectoryName(Environment.ProcessPath)
            ?? AppContext.BaseDirectory;
        Check(layout.BundledDictionaryDirectory == Path.Combine(executableDirectory, "Dictionaries") &&
              layout.MorphologyFile == Path.Combine(executableDirectory, "Morphology", "ru-morph.tsv.gz"),
            "The bundled dictionaries or morphology moved away from the executable.");

        if (string.IsNullOrWhiteSpace(localApplicationData) ||
            !Path.IsPathFullyQualified(localApplicationData))
        {
            // A home without Library/Application Support (the runner's
            // --empty-home): before the seam every path below was relative.
            ExpectUnresolvedUserPaths(layout, "the system LocalApplicationData");
            return;
        }

        // Each expected value is the expression the site used before the seam.
        (string Actual, string Expected, string Site)[] sites =
        [
            (layout.SettingsFile,
                Path.Combine(localApplicationData, "AsteriaScale", "settings.json"),
                "SettingsService"),
            (layout.UserDictionaryDirectory,
                Path.Combine(localApplicationData, "AsteriaScale", "Dictionaries"),
                "UserDictionaryService and LocalDictionaryService"),
            (layout.HistoryFile,
                Path.Combine(localApplicationData, "AsteriaScale", "translation-history.v1.json"),
                "TranslationHistoryService"),
            (layout.SmartResultCacheFile,
                Path.Combine(localApplicationData, "AsteriaScale", "smart-dictionary-cache.v2.json"),
                "SmartDictionaryResultCache"),
            (layout.PhraseCacheFile,
                Path.Combine(localApplicationData, "AsteriaScale", "smart-phrase-translations.v7.json"),
                "SmartPhraseTranslationCache"),
            (layout.DecisionCacheFile,
                Path.Combine(localApplicationData, "AsteriaScale", "glossbert-decisions.v1.json"),
                "GlossBertDecisionCache"),
            (layout.HoverLogFile,
                Path.Combine(Path.Combine(localApplicationData, "AsteriaScale"), "hover-translation.log"),
                "HoverDiagnostics"),
            (layout.PhraseModelRoot,
                Path.Combine(localApplicationData, "AsteriaScale", "Models", "TranslateGemma"),
                "TranslateGemmaTranslationService")
        ];
        foreach ((string actual, string expected, string site) in sites)
        {
            Check(actual == expected,
                $"{site} would use '{actual}' instead of its old default '{expected}'.");
        }
    }

    // An empty, blank or relative LocalApplicationData leaves the user half
    // unresolved; the bundled half, which the morphology Lazy caches, resolves.
    private static void VerifyUnresolvedUserRoot(string tempRoot)
    {
        string app = Path.Combine(tempRoot, "core-data-layout", "app");
        string extraction = Path.Combine(tempRoot, "core-data-layout", "extraction");
        foreach (string localApplicationData in new[] { "", "   ", Path.Combine("relative", "AppData") })
        {
            CoreDataLayout layout = CoreDataLayout.Resolve(
                Path.Combine(app, "AsteriaScale.exe"),
                extraction,
                localApplicationData);
            ExpectUnresolvedUserPaths(layout, $"'{localApplicationData}'");
            Check(layout.BundledDataRoot == app &&
                  layout.MorphologyFile == Path.Combine(app, "Morphology", "ru-morph.tsv.gz") &&
                  layout.BundledDictionaryDirectory == Path.Combine(app, "Dictionaries"),
                $"With LocalApplicationData '{localApplicationData}' the bundled data no longer " +
                "resolves beside the physical executable.");
        }

        CoreDataLayout withoutProcessPath = CoreDataLayout.Resolve(null, extraction, "");
        Check(withoutProcessPath.BundledDataRoot == extraction,
            "Without a process path the bundled data did not fall back to AppContext.BaseDirectory.");
    }

    // The CoreHost passes every root; optional roots fall back to the user root.
    private static void VerifyExplicitRoots(string tempRoot)
    {
        string root = Path.Combine(tempRoot, "core-data-layout", "explicit");
        string bundled = Path.Combine(root, "Resources", "Data");
        string user = Path.Combine(root, "Application Support", "AsteriaScale");
        string cache = Path.Combine(root, "Caches");
        string logs = Path.Combine(root, "Logs");
        string phraseModels = Path.Combine(root, "PhraseModels");

        // Two strings must bind to the public constructor and keep the user
        // root; a private (string, string) overload once swallowed it.
        CoreDataLayout defaults = new(bundled, user);
        Check(defaults.UserDataRoot == user &&
              defaults.CacheRoot == user &&
              defaults.LogRoot == user &&
              defaults.PhraseModelRoot == Path.Combine(user, "Models", "TranslateGemma") &&
              defaults.SettingsFile == Path.Combine(user, "settings.json") &&
              defaults.SmartResultCacheFile == Path.Combine(user, "smart-dictionary-cache.v2.json") &&
              defaults.HoverLogFile == Path.Combine(user, "hover-translation.log") &&
              defaults.MorphologyFile == Path.Combine(bundled, "Morphology", "ru-morph.tsv.gz"),
            "An explicit layout without optional roots did not keep everything under the user root.");

        CoreDataLayout separate = new(bundled, user, cache, logs, phraseModels);
        Check(separate.SettingsFile == Path.Combine(user, "settings.json") &&
              separate.UserDictionaryDirectory == Path.Combine(user, "Dictionaries") &&
              separate.HistoryFile == Path.Combine(user, "translation-history.v1.json") &&
              separate.SmartResultCacheFile == Path.Combine(cache, "smart-dictionary-cache.v2.json") &&
              separate.PhraseCacheFile == Path.Combine(cache, "smart-phrase-translations.v7.json") &&
              separate.DecisionCacheFile == Path.Combine(cache, "glossbert-decisions.v1.json") &&
              separate.HoverLogFile == Path.Combine(logs, "hover-translation.log") &&
              separate.PhraseModelRoot == phraseModels,
            "Explicit cache, log or phrase-model roots were not honoured.");

        string relative = Path.Combine("relative", "root");
        (string Parameter, Func<CoreDataLayout> Create)[] rejected =
        [
            ("bundledDataRoot", () => new CoreDataLayout(relative, user)),
            ("userDataRoot", () => new CoreDataLayout(bundled, relative)),
            ("userDataRoot", () => new CoreDataLayout(bundled, "")),
            ("cacheRoot", () => new CoreDataLayout(bundled, user, cacheRoot: relative)),
            ("logRoot", () => new CoreDataLayout(bundled, user, logRoot: relative)),
            ("phraseModelRoot", () => new CoreDataLayout(bundled, user, phraseModelRoot: relative))
        ];
        foreach ((string parameter, Func<CoreDataLayout> create) in rejected)
        {
            try
            {
                _ = create();
            }
            catch (ArgumentException ex) when (ex.ParamName == parameter)
            {
                continue;
            }

            throw new InvalidOperationException(
                $"CoreDataLayout accepted a relative or empty {parameter}.");
        }
    }

    // Services created before a late Configure would keep the old roots, so
    // a second Configure and a Configure after the first read both throw.
    private static void VerifyConfigureOnce(string tempRoot)
    {
        string root = Path.Combine(tempRoot, "core-data-layout", "slot");
        CoreDataLayout fallback = new(Path.Combine(root, "data"), Path.Combine(root, "default-user"));
        CoreDataLayout configured = new(Path.Combine(root, "data"), Path.Combine(root, "host-user"));
        int defaultResolutions = 0;
        CoreDataLayout ResolveDefault()
        {
            defaultResolutions++;
            return fallback;
        }

        CoreDataLayoutSlot configuredSlot = new(ResolveDefault);
        configuredSlot.Configure(configured);
        Check(ReferenceEquals(configuredSlot.Current, configured) && defaultResolutions == 0,
            "A configured layout was not the one every reader sees.");
        ExpectConfigureRefused(configuredSlot, fallback, "A second Configure was accepted.");
        Check(ReferenceEquals(configuredSlot.Current, configured),
            "A refused Configure replaced the configured layout.");

        CoreDataLayoutSlot readSlot = new(ResolveDefault);
        Check(ReferenceEquals(readSlot.Current, fallback) &&
              ReferenceEquals(readSlot.Current, fallback) &&
              defaultResolutions == 1,
            "The default layout was not resolved exactly once and then kept.");
        ExpectConfigureRefused(readSlot, configured, "Configure after the first read was accepted.");
        Check(ReferenceEquals(readSlot.Current, fallback),
            "A refused Configure replaced the layout that readers already used.");
    }

    // Before the seam an empty LocalApplicationData made the hover log
    // ./AsteriaScale/hover-translation.log in whatever the working directory was.
    private static void VerifyHoverLogNeverInWorkingDirectory(string tempRoot)
    {
        string root = Path.Combine(tempRoot, "core-data-layout", "hover-log");
        string workingDirectory = Path.Combine(root, "cwd");
        string logs = Path.Combine(root, "logs");
        string user = Path.Combine(root, "user");
        Directory.CreateDirectory(workingDirectory);
        CoreDataLayout unresolved = CoreDataLayout.Resolve(
            Path.Combine(root, "app", "AsteriaScale.exe"),
            Path.Combine(root, "app"),
            "");
        CoreDataLayout resolved = new(Path.Combine(root, "app"), user, logRoot: logs);

        string previousDirectory = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(workingDirectory);
            HoverDiagnostics.AppendDiagnostic("unresolved layout", unresolved);
            Check(!Directory.EnumerateFileSystemEntries(workingDirectory).Any(),
                "The hover log was written relative to the working directory.");

            // Positive control: the same call writes exactly the layout's log.
            HoverDiagnostics.AppendDiagnostic("resolved layout", resolved);
            string log = Path.Combine(logs, "hover-translation.log");
            Check(File.Exists(log) &&
                  File.ReadAllText(log).Contains("resolved layout", StringComparison.Ordinal) &&
                  !File.ReadAllText(log).Contains("unresolved layout", StringComparison.Ordinal) &&
                  !Directory.Exists(user) &&
                  !Directory.EnumerateFileSystemEntries(workingDirectory).Any(),
                "A resolved layout did not write its hover log to its own log root only.");
#if !WINDOWS
            // The process-wide layout of the macOS subset drops the message too.
            HoverDiagnostics.LogDiagnostic("process-wide unresolved layout");
            Check(!Directory.EnumerateFileSystemEntries(workingDirectory).Any(),
                "The macOS subset wrote the hover log relative to the working directory.");
#endif
        }
        finally
        {
            Directory.SetCurrentDirectory(previousDirectory);
        }
    }

#if !WINDOWS
    // MacSmoke.Begin left the user half unresolved: every Core default that
    // would write user data must refuse before touching the disk, which is
    // what keeps the subset from writing into a real or temporary home.
    private static async Task VerifyMacSubsetDefaultsRefuseUserPathsAsync()
    {
        ExpectUnresolvedUserPaths(CoreDataLayout.Current, "the macOS subset's process-wide layout");
        (string Name, Func<object> Create)[] defaults =
        [
            ("SettingsService", () => new SettingsService()),
            ("UserDictionaryService", () => new UserDictionaryService()),
            ("TranslationHistoryService", () => new TranslationHistoryService()),
            ("SmartDictionaryResultCache", () => new SmartDictionaryResultCache()),
            ("SmartPhraseTranslationCache", () => new SmartPhraseTranslationCache()),
            ("GlossBertDecisionCache", () => new GlossBertDecisionCache()),
            ("TranslateGemmaTranslationService", () => new TranslateGemmaTranslationService())
        ];
        foreach ((string name, Func<object> create) in defaults)
        {
            object? created = null;
            try
            {
                created = create();
            }
            catch (InvalidOperationException ex) when (
                ex.Message == CoreDataLayout.UnresolvedUserDataRootMessage)
            {
                continue;
            }
            finally
            {
                if (created is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync();
                }
                else if (created is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }

            throw new InvalidOperationException(
                $"{name}() resolved a default user path in the macOS subset.");
        }

        LocalDictionaryService dictionary = new();
        try
        {
            await dictionary.LoadAsync();
            throw new InvalidOperationException(
                "LocalDictionaryService() loaded with an unresolved user dictionary folder.");
        }
        catch (InvalidOperationException ex) when (
            ex.Message == CoreDataLayout.UnresolvedUserDataRootMessage)
        {
            // Refused before any dictionary file was opened.
        }
    }
#endif

    private static void ExpectUnresolvedUserPaths(CoreDataLayout layout, string what)
    {
        foreach ((string name, Func<CoreDataLayout, string> read) in UserPaths)
        {
            try
            {
                string path = read(layout);
                throw new InvalidOperationException(
                    $"With {what} CoreDataLayout.{name} resolved to '{path}' instead of failing.");
            }
            catch (InvalidOperationException ex) when (
                ex.Message == CoreDataLayout.UnresolvedUserDataRootMessage)
            {
                // Expected: the explicit Russian error, not a relative path.
            }
        }
    }

    private static void ExpectConfigureRefused(
        CoreDataLayoutSlot slot,
        CoreDataLayout layout,
        string message)
    {
        try
        {
            slot.Configure(layout);
        }
        catch (InvalidOperationException)
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
