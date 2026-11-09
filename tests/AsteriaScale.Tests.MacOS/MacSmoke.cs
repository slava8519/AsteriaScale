using AsteriaScale.Services;

// Bookkeeping for the macOS subset of the smoke suite. Program.cs is shared
// with the Windows suite: a registration that cannot run on macOS is declared
// next to its code with Skip, a Windows-only sub-check inside a registration
// that does run is declared with Part, and Summarize refuses to print a PASS
// line unless what ran plus what was declared adds up to the Windows
// registration count (whose names MacProjectParityTests compares with the
// Windows view of Program.cs). Nothing is ever skipped by catching
// PlatformNotSupportedException or Win32Exception: a Windows-only path reached
// on macOS must fail the run, not quietly shrink it.
internal static class MacSmoke
{
    // Default-path registrations in tests/AsteriaScale.Tests/Program.cs; change
    // together. Summarize fails when what the macOS build ran plus what it
    // declared with Skip differs from this number. That alone cannot see a
    // registration added or renamed inside an #if WINDOWS block, so
    // MacProjectParityTests also reads Program.cs as each build compiles it and
    // requires the same names in the same order, and this many of them.
    internal const int SharedRegistrationCount = 57;

    // Registrations that exist only in the macOS branch at the end of
    // Program.cs; change together.
    internal const int MacOnlyRegistrationCount = 6;

    internal const string MacOnlyPrefix = "macOS: ";

    private const string NotReached = "not reached";

    // Every Windows-only sub-check the shared sources declare with Part. An
    // unknown key is a typo that would otherwise hide a missing declaration.
    private static readonly string[] ExpectedParts =
    [
        "managed-memory-budget",
        "overlay-width",
        "lease-exclusion"
    ];

    private static readonly HashSet<string> Registrations = new(StringComparer.Ordinal);
    private static readonly HashSet<string> PartsReached = new(StringComparer.Ordinal);
    private static int s_skipped;

    // Set by ConsecRankerTests: whether the installed tokenizer was checked
    // against the goldens, or why not. On Windows that check skips silently.
    internal static string ConsecRealBundle { get; set; } = NotReached;

    // Set by MacWorkerTests: whether the macOS worker answered a ConSeC
    // request on the bundle read in place, or which bundle file is absent.
    internal static string ConsecWorkerSmoke { get; set; } = NotReached;

    internal static void Begin(string[] args)
    {
        // The first Core-touching statement of the run. The bundled half stays
        // beside the apphost, as on Windows; the user half is deliberately left
        // unresolved, so a shared test that reaches a default user path
        // (settings, user dictionaries, history, caches, TranslateGemma models)
        // throws instead of writing into the real or temporary home, and the
        // hover log is dropped. A test that needs user state passes its own
        // folder under tempRoot. This file is never compiled into the Windows
        // suite, whose default layout stays LocalApplicationData\AsteriaScale.
        CoreDataLayout.Configure(CoreDataLayout.Resolve(
            Environment.ProcessPath,
            AppContext.BaseDirectory,
            localApplicationData: ""));

        // Core resolves Morphology and the dictionaries next to the process
        // (RussianMorphologyLexicon, LocalDictionaryService). Under the dotnet
        // muxer that is the SDK folder, where the lexicon silently loads empty.
        // The macOS apphost has no extension, and GetFileNameWithoutExtension
        // would read ".Tests" as one, so the whole file name is compared.
        string processName = Path.GetFileName(Environment.ProcessPath ?? string.Empty);
        if (!string.Equals(processName, "AsteriaScale.Tests", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The macOS subset must start through its apphost " +
                "(dotnet run --project tests/AsteriaScale.Tests.MacOS/AsteriaScale.Tests.MacOS.csproj, " +
                "or bin/<Configuration>/net10.0/osx-arm64/AsteriaScale.Tests), " +
                $"not '{Environment.ProcessPath}': Core reads its data next to the process.");
        }

        // Only --live and the OCR-image modes are compiled out under
        // #if WINDOWS; the benchmark modes are compiled but must stay
        // unreachable here, because benchmark provenance does not record the
        // platform yet. Accepting any option would also let a mode fall
        // through to a green default run that never exercised it
        // (TestCommandLine.cs).
        if (args.Length != 0)
        {
            throw new InvalidOperationException(
                $"The macOS subset accepts no options (got: {string.Join(' ', args)}). " +
                "Gated modes and benchmarks are Windows-only until benchmark provenance " +
                "records the platform (docs/MACOS.md).");
        }

        Console.WriteLine(
            "macOS subset of the smoke suite: Windows-only registrations print 'skip', " +
            "Windows-only sub-checks print 'note'.");
    }

    internal static void Record(string name)
    {
        if (!Registrations.Add(name))
        {
            throw new InvalidOperationException(
                $"The smoke registration '{name}' was recorded twice; names must stay unique " +
                "so the macOS subset can be compared with the Windows run.");
        }
    }

    internal static void Skip(string name, string reason)
    {
        Record(name);
        s_skipped++;
        Console.WriteLine($"skip  {name} — Windows-only: {reason}");
    }

    internal static void Part(string key, string test, string detail)
    {
        if (Array.IndexOf(ExpectedParts, key) < 0)
        {
            throw new InvalidOperationException(
                $"Unknown Windows-only part '{key}'; add it to MacSmoke.ExpectedParts.");
        }

        PartsReached.Add(key);
        Console.WriteLine($"note  {test}: {detail}");
    }

    internal static string Summarize(int sharedPassed, int macOnlyPassed)
    {
        int sharedRecorded = Registrations.Count(name =>
            !name.StartsWith(MacOnlyPrefix, StringComparison.Ordinal));
        if (sharedPassed + s_skipped != SharedRegistrationCount ||
            sharedRecorded != SharedRegistrationCount)
        {
            throw new InvalidOperationException(
                $"The macOS subset ran {sharedPassed} and skipped {s_skipped} shared registrations " +
                $"({sharedRecorded} distinct names), but tests/AsteriaScale.Tests/Program.cs has " +
                $"{SharedRegistrationCount}. Declare a Windows-only registration with MacSmoke.Skip " +
                "and update MacSmoke.SharedRegistrationCount together with Program.cs.");
        }

        if (macOnlyPassed != MacOnlyRegistrationCount ||
            Registrations.Count - sharedRecorded != MacOnlyRegistrationCount)
        {
            throw new InvalidOperationException(
                $"Expected {MacOnlyRegistrationCount} macOS-only checks, ran {macOnlyPassed}.");
        }

        string[] missingParts = ExpectedParts
            .Where(part => !PartsReached.Contains(part))
            .ToArray();
        if (missingParts.Length != 0)
        {
            throw new InvalidOperationException(
                "Declared Windows-only parts were never reached: " +
                string.Join(", ", missingParts) + ".");
        }

        if (ConsecRealBundle == NotReached || ConsecWorkerSmoke == NotReached)
        {
            throw new InvalidOperationException(
                $"A ConSeC status was never recorded (real bundle: {ConsecRealBundle}; " +
                $"worker smoke: {ConsecWorkerSmoke}).");
        }

        return $"PASS (macOS subset): {sharedPassed}/{SharedRegistrationCount} shared smoke tests + " +
            $"{macOnlyPassed} macOS checks; SKIPPED: {s_skipped} Windows-only; " +
            $"{ExpectedParts.Length} Windows-only parts not run; " +
            $"ConSeC real bundle: {ConsecRealBundle}; ConSeC worker smoke: {ConsecWorkerSmoke}. " +
            "Not the full Windows smoke suite.";
    }

    // The checks that read repository files start from the binary, never from
    // the working directory, so where the run was started cannot change them.
    internal static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AsteriaScale.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(
            $"No AsteriaScale.slnx above {AppContext.BaseDirectory}; the macOS subset runs from " +
            "its build output inside the repository.");
    }
}
