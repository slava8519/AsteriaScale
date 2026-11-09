using System.Xml.Linq;

// The macOS projects are siblings of the Windows ones rather than conditions
// inside them, so the Windows build stays byte-identical by construction. The
// price is that two project files must agree by hand; this check makes every
// such agreement mechanical, and it pins the licence guard that keeps the
// CC BY-NC-SA ConSeC bundle out of every macOS build. The same goes for the
// shared Program.cs: the default-path registrations the Windows build compiles
// must match, name for name and in order, what the macOS build runs or
// declares with MacSmoke.Skip, and their number must be
// MacSmoke.SharedRegistrationCount. CoreHost has no Windows twin, but its
// data items must be the WPF project's, it may reference only Core, and it
// must stay out of the solution and of every Windows project.
internal static class MacProjectParityTests
{
    private const string WindowsWorkerProject = "src/AsteriaScale.RankerWorker/AsteriaScale.RankerWorker.csproj";
    private const string MacWorkerProject = "src/AsteriaScale.RankerWorker.MacOS/AsteriaScale.RankerWorker.MacOS.csproj";
    private const string WindowsTestProject = "tests/AsteriaScale.Tests/AsteriaScale.Tests.csproj";
    private const string MacTestProject = "tests/AsteriaScale.Tests.MacOS/AsteriaScale.Tests.MacOS.csproj";
    private const string WpfProject = "src/AsteriaScale/AsteriaScale.csproj";
    private const string CoreHostProject = "src/AsteriaScale.CoreHost/AsteriaScale.CoreHost.csproj";
    private const string CoreHostName = "AsteriaScale.CoreHost";
    private const string LinkHelperFile = "tests/AsteriaScale.Tests.MacOS/CoreHost/CoreHostFixtures.cs";
    private const string LinkHelperMethod = "CreateLinkToEmptyDirectory";
    private const string SharedWorkerPrefix = @"..\..\src\AsteriaScale.RankerWorker\";
    private const string SharedTestDirectoryToken = "$(SharedTestDirectory)";

    private const string SharedProgram = "tests/AsteriaScale.Tests/Program.cs";
    // The default path: after the unknown-option guard, before the first gated
    // mode. Gated modes register their own tests and never run on macOS.
    private const string DefaultPathStart = "TestCommandLine.FindUnknownOption(args)";
    private const string DefaultPathEnd = "int screenOcrImageArgument";
    private const string RunCall = "await RunAsync(";
    private const string SkipCall = "MacSmoke.Skip(";
    private const string MacOnlyPrefixExpression = "MacSmoke.MacOnlyPrefix + ";

    public static Task RunAsync()
    {
        string root = MacSmoke.FindRepositoryRoot();
        XDocument windowsWorker = Load(root, WindowsWorkerProject);
        XDocument macWorker = Load(root, MacWorkerProject);
        XDocument windowsTests = Load(root, WindowsTestProject);
        XDocument macTests = Load(root, MacTestProject);
        XDocument wpf = Load(root, WpfProject);
        XDocument coreHost = Load(root, CoreHostProject);

        VerifyWorkerPackages(windowsWorker, macWorker);
        VerifyWorkerLicenceGuard(macWorker);
        VerifyWorkerLayout(macWorker);
        VerifyFlatWorkerFolder(root);
        VerifyTestProjects(root, windowsTests, macTests, wpf);
        VerifySharedRegistrations(root);
        VerifyCoreHostProject(root, coreHost, wpf, macTests);
        VerifyOutsideTheSolution(root);
        VerifySymbolicLinkHelper(root);
        VerifyNoHelperFilesInTestOutput();
        return Task.CompletedTask;
    }

    private static void VerifyCoreHostProject(string root, XDocument coreHost, XDocument wpf, XDocument macTests)
    {
        // Platform-neutral like Core: the RID is chosen at publish, and the
        // CoreHost checks start bin/<Configuration>/net10.0.
        Check(string.Equals(Property(coreHost, "TargetFramework"), CoreHostFixtures.TargetFramework, StringComparison.Ordinal) &&
              Property(coreHost, "RuntimeIdentifier") is null &&
              Property(coreHost, "RuntimeIdentifiers") is null &&
              string.Equals(Property(coreHost, "OutputType"), "Exe", StringComparison.Ordinal) &&
              string.Equals(Property(coreHost, "AssemblyName"), CoreHostName, StringComparison.Ordinal),
            $"{CoreHostProject} must be a net10.0 Exe named {CoreHostName} without a RuntimeIdentifier.");

        // Its own code stays NativeAOT-clean (M11): the analyzers fail the build.
        string warningsAsErrors = Property(coreHost, "WarningsAsErrors") ?? string.Empty;
        Check(string.Equals(Property(coreHost, "IsAotCompatible"), "true", StringComparison.Ordinal) &&
              string.Equals(Property(coreHost, "TreatWarningsAsErrors"), "true", StringComparison.Ordinal) &&
              new[] { "CA1416", "IL2026", "IL3050" }.All(code => warningsAsErrors.Split(';').Contains(code)),
            $"{CoreHostProject} must keep IsAotCompatible, TreatWarningsAsErrors and CA1416/IL2026/IL3050 as errors.");

        Check(ProjectReferences(coreHost).SequenceEqual([@"..\AsteriaScale.Core\AsteriaScale.Core.csproj"], StringComparer.Ordinal) &&
              !coreHost.Descendants("PackageReference").Any(),
            $"{CoreHostProject} may reference AsteriaScale.Core and nothing else.");

        // In development CoreHost's data root is its own output folder,
        // filled from these items; they must be the WPF project's, so both
        // hosts read the same dictionaries and morphology.
        (string Include, string Link)[] expected = ContentPairs(wpf)
            .Where(pair => pair.Include.EndsWith(".tsv.gz", StringComparison.Ordinal) &&
                           (pair.Link.StartsWith(@"Dictionaries\", StringComparison.Ordinal) ||
                            pair.Link.StartsWith(@"Morphology\", StringComparison.Ordinal)))
            .ToArray();
        (string Include, string Link)[] declared = ContentPairs(coreHost).ToArray();
        Check(expected.Length == 2 && declared.SequenceEqual(expected) &&
              !coreHost.Descendants().Any(element =>
                  element.Name.LocalName is "None" or "EmbeddedResource" &&
                  (element.Attribute("Include") is not null || element.Attribute("Update") is not null)),
            $"The CoreHost data items diverged from the WPF project: expected [{string.Join("; ", expected)}], " +
            $"found [{string.Join("; ", declared)}].");

        XElement? reference = macTests.Descendants("ProjectReference").SingleOrDefault(element =>
            ((string?)element.Attribute("Include"))?.EndsWith(CoreHostName + ".csproj", StringComparison.Ordinal) == true);
        Check(reference is not null &&
              string.Equals((string?)reference.Attribute("ReferenceOutputAssembly"), "false", StringComparison.Ordinal),
            "The macOS test project must reference CoreHost for build order only (ReferenceOutputAssembly=false).");

        // No Windows project may build or ship it.
        string[] windowsReferences = new[] { WindowsWorkerProject, WindowsTestProject, WpfProject }
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/')))
            .Where(path => path != CoreHostProject && path != MacTestProject)
            .Distinct(StringComparer.Ordinal)
            .Where(path => File.ReadAllText(Path.Combine(root, path)).Contains(CoreHostName, StringComparison.Ordinal))
            .ToArray();
        Check(windowsReferences.Length == 0,
            "Only the macOS test project may reference CoreHost: " + string.Join(", ", windowsReferences));
    }

    // Symbolic links are dangerous next to the licensed ConSeC bundle: the
    // worker refuses one at its model folder, and the identity tool refuses
    // one in an export. The only helper that creates a link makes a fresh
    // empty target of its own under the run's temporary root, and nothing
    // else under tests/ may create one.
    private static void VerifySymbolicLinkHelper(string root)
    {
        // Split so this file does not count itself.
        string[] tokens = ["CreateSymbolic" + "Link", "CreateAsSymbolic" + "Link"];
        List<(string File, int Line)> calls = [];
        foreach (string path in Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Split('/').Any(part => part is "bin" or "obj"))
            {
                continue;
            }

            string[] lines = File.ReadAllLines(path);
            for (int index = 0; index < lines.Length; index++)
            {
                if (tokens.Any(token => lines[index].Contains(token, StringComparison.Ordinal)))
                {
                    calls.Add((relative, index));
                }
            }
        }

        Check(calls.Count == 1 && calls[0].File == LinkHelperFile,
            $"Exactly one symbolic-link call may exist under tests/, in {LinkHelperFile}; found " +
            (calls.Count == 0 ? "none." : string.Join(", ", calls.Select(call => $"{call.File}:{call.Line + 1}"))));
        string[] helper = File.ReadAllLines(Path.Combine(root, LinkHelperFile.Replace('/', Path.DirectorySeparatorChar)));
        int declaration = Array.FindLastIndex(helper, calls[0].Line, line =>
            line.TrimStart().StartsWith("public static", StringComparison.Ordinal) ||
            line.TrimStart().StartsWith("private static", StringComparison.Ordinal) ||
            line.TrimStart().StartsWith("internal static", StringComparison.Ordinal));
        Check(declaration >= 0 && helper[declaration].Contains(" " + LinkHelperMethod + "(", StringComparison.Ordinal),
            $"The symbolic-link call in {LinkHelperFile} must sit inside {LinkHelperMethod}.");
        string text = string.Join('\n', helper);
        string[] forbidden = ["Consec", "ConSeC_WNGT", "Models"];
        Check(!forbidden.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase)),
            $"{LinkHelperFile} must not name the model folders ({string.Join(", ", forbidden)}).");
    }

    // MacSmoke.Summarize can only count what the macOS build compiled, so a
    // registration added or renamed inside an existing #if WINDOWS block would
    // pass unnoticed there. Reading the source as both builds see it closes
    // that gap without touching a Windows-visible token.
    private static void VerifySharedRegistrations(string root)
    {
        string[] lines = File.ReadAllLines(
            Path.Combine(root, SharedProgram.Replace('/', Path.DirectorySeparatorChar)));
        int start = SingleMarkerLine(lines, DefaultPathStart);
        int end = SingleMarkerLine(lines, DefaultPathEnd);
        Check(start < end,
            $"{SharedProgram}: '{DefaultPathStart}' (line {start + 1}) must come before " +
            $"'{DefaultPathEnd}' (line {end + 1}).");

        (string Name, int Line)[] windows = Registrations(
            lines, ResolveConditionalRegions(lines, windowsDefined: true), start, end, [RunCall]);
        (string Name, int Line)[] mac = Registrations(
                lines, ResolveConditionalRegions(lines, windowsDefined: false), start, end, [RunCall, SkipCall])
            .Where(registration => !registration.Name.StartsWith(MacSmoke.MacOnlyPrefix, StringComparison.Ordinal))
            .ToArray();
        CheckUnique("Windows", windows);
        CheckUnique("macOS", mac);

        int common = Math.Min(windows.Length, mac.Length);
        for (int index = 0; index < common; index++)
        {
            Check(string.Equals(windows[index].Name, mac[index].Name, StringComparison.Ordinal),
                $"Default-path registration #{index + 1} of {SharedProgram} differs between the builds: " +
                $"Windows registers \"{windows[index].Name}\" (line {windows[index].Line}), macOS " +
                $"\"{mac[index].Name}\" (line {mac[index].Line}). A registration under #if WINDOWS needs " +
                "a MacSmoke.Skip with the same name at the same position in its #else branch.");
        }

        if (windows.Length != mac.Length)
        {
            bool windowsLonger = windows.Length > mac.Length;
            (string name, int line) = windowsLonger ? windows[common] : mac[common];
            throw new InvalidOperationException(
                $"{SharedProgram} has {windows.Length} default-path registrations in the Windows build " +
                $"and {mac.Length} (run or MacSmoke.Skip) in the macOS build; the first unmatched one, " +
                $"\"{name}\" (line {line}), exists only in the {(windowsLonger ? "Windows" : "macOS")} build.");
        }

        Check(windows.Length == MacSmoke.SharedRegistrationCount,
            $"Both builds of {SharedProgram} have {windows.Length} default-path registrations, but " +
            $"MacSmoke.SharedRegistrationCount is {MacSmoke.SharedRegistrationCount}; update it in the same change.");
    }

    private static int SingleMarkerLine(string[] lines, string marker)
    {
        int[] found = Enumerable.Range(0, lines.Length)
            .Where(index => lines[index].Contains(marker, StringComparison.Ordinal))
            .ToArray();
        Check(found.Length == 1,
            $"{SharedProgram}: expected exactly one line containing '{marker}' to bound the default " +
            $"registrations, found {found.Length}" +
            (found.Length == 0 ? "." : $" (lines {string.Join(", ", found.Select(index => index + 1))})."));
        return found[0];
    }

    // Marks which lines one build compiles. Only the directive forms the shared
    // Program.cs uses are understood; any other directive, or unbalanced
    // nesting, fails instead of being guessed at, because a wrong guess would
    // make both views agree on something neither build compiles.
    private static bool[] ResolveConditionalRegions(string[] lines, bool windowsDefined)
    {
        bool[] active = new bool[lines.Length];
        Stack<(bool Enclosing, bool Taken, bool InElse)> frames = new();
        bool current = true;
        for (int index = 0; index < lines.Length; index++)
        {
            string directive = lines[index].Trim();
            if (!directive.StartsWith('#'))
            {
                active[index] = current;
                continue;
            }

            switch (directive)
            {
                case "#if WINDOWS":
                case "#if !WINDOWS":
                    bool taken = directive == "#if WINDOWS" ? windowsDefined : !windowsDefined;
                    frames.Push((current, taken, false));
                    current = current && taken;
                    break;
                case "#else":
                    Check(frames.Count > 0, $"{SharedProgram}:{index + 1}: #else without #if.");
                    (bool enclosing, bool wasTaken, bool inElse) = frames.Pop();
                    Check(!inElse, $"{SharedProgram}:{index + 1}: a second #else in one #if block.");
                    frames.Push((enclosing, !wasTaken, true));
                    current = enclosing && !wasTaken;
                    break;
                case "#endif":
                    Check(frames.Count > 0, $"{SharedProgram}:{index + 1}: #endif without #if.");
                    current = frames.Pop().Enclosing;
                    break;
                default:
                    throw new InvalidOperationException(
                        $"{SharedProgram}:{index + 1}: unsupported preprocessor line '{directive}'. The " +
                        "registration parity check understands only '#if WINDOWS', '#if !WINDOWS', " +
                        "'#else' and '#endif'; extend it before using another directive there.");
            }
        }

        Check(frames.Count == 0, $"{SharedProgram} ends inside {frames.Count} unclosed #if block(s).");
        return active;
    }

    private static (string Name, int Line)[] Registrations(
        string[] lines,
        bool[] active,
        int start,
        int end,
        string[] calls)
    {
        List<(string Name, int Line)> found = [];
        for (int index = start + 1; index < end; index++)
        {
            if (!active[index])
            {
                continue;
            }

            string line = lines[index];
            int comment = line.IndexOf("//", StringComparison.Ordinal);
            foreach (string call in calls)
            {
                int position = line.IndexOf(call, StringComparison.Ordinal);
                if (position < 0 || comment >= 0 && comment < position)
                {
                    continue;
                }

                string argument = line[(position + call.Length)..].Trim();
                int argumentLine = index;
                if (argument.Length == 0)
                {
                    // The formatter moves a long name to the next line.
                    argumentLine = index + 1;
                    Check(argumentLine < end && active[argumentLine],
                        $"{SharedProgram}:{index + 1}: '{call}' is not followed by its name.");
                    argument = lines[argumentLine].Trim();
                }

                found.Add((RegistrationName(argument, call, argumentLine), argumentLine + 1));
            }
        }

        return found.ToArray();
    }

    private static string RegistrationName(string argument, string call, int lineIndex)
    {
        string prefix = string.Empty;
        if (argument.StartsWith(MacOnlyPrefixExpression, StringComparison.Ordinal))
        {
            prefix = MacSmoke.MacOnlyPrefix;
            argument = argument[MacOnlyPrefixExpression.Length..];
        }

        // Names are plain literals; an escape or an interpolation would need a
        // real C# lexer, so it fails rather than being misread.
        int close = argument.Length > 1 && argument[0] == '"' ? argument.IndexOf('"', 1) : -1;
        Check(close > 0 && argument.IndexOf('\\', 1, close - 1) < 0,
            $"{SharedProgram}:{lineIndex + 1}: the first argument of '{call}' must be a plain string " +
            $"literal, found: {argument}");
        return prefix + argument[1..close];
    }

    private static void CheckUnique(string build, (string Name, int Line)[] registrations)
    {
        IGrouping<string, (string Name, int Line)>? duplicate = registrations
            .GroupBy(registration => registration.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"The {build} build of {SharedProgram} registers \"{duplicate.Key}\" more than once " +
                $"(lines {string.Join(", ", duplicate.Select(registration => registration.Line))}); " +
                "names must stay unique so the two runs can be compared.");
        }
    }

    private static void VerifyWorkerPackages(XDocument windowsWorker, XDocument macWorker)
    {
        Dictionary<string, string> windows = Packages(windowsWorker);
        Dictionary<string, string> mac = Packages(macWorker);
        bool windowsMl = windows.Remove("Microsoft.Windows.AI.MachineLearning");
        bool windowsNative = windows.Remove("Tokenizers.DotNet.runtime.win-x64", out string? winRuntime);
        bool macNative = mac.Remove("Tokenizers.DotNet.runtime.osx-arm64", out string? macRuntime);
        Check(windowsMl && windowsNative && macNative,
            "A worker project lost a platform package this parity check expects.");
        Check(windows.Count == mac.Count &&
              windows.All(package =>
                  mac.TryGetValue(package.Key, out string? version) &&
                  string.Equals(version, package.Value, StringComparison.Ordinal)),
            "The macOS worker packages diverged from the Windows worker: " +
            $"Windows [{Describe(windows)}], macOS [{Describe(mac)}].");
        string? tokenizers = windows.GetValueOrDefault("Tokenizers.DotNet");
        Check(tokenizers is not null &&
              string.Equals(winRuntime, tokenizers, StringComparison.Ordinal) &&
              string.Equals(macRuntime, tokenizers, StringComparison.Ordinal),
            $"Tokenizers.DotNet {tokenizers} and its native runtimes (win-x64 {winRuntime}, " +
            $"osx-arm64 {macRuntime}) must share one version.");
        Check(ProjectReferences(windowsWorker).SequenceEqual(ProjectReferences(macWorker), StringComparer.Ordinal),
            "The macOS worker project references differ from the Windows worker.");
        foreach (string property in new[] { "AssemblyName", "RootNamespace", "Version" })
        {
            Check(string.Equals(Property(windowsWorker, property), Property(macWorker, property), StringComparison.Ordinal),
                $"The worker {property} differs between the Windows and macOS projects.");
        }
    }

    private static void VerifyWorkerLicenceGuard(XDocument macWorker)
    {
        // A Remove inside a target only drops items; anything that adds or
        // updates a file item could copy a model into bin.
        XElement[] fileItems = macWorker.Descendants()
            .Where(element => element.Name.LocalName is "Content" or "None" or "EmbeddedResource")
            .Where(element => element.Attribute("Include") is not null || element.Attribute("Update") is not null)
            .ToArray();
        Check(fileItems.Length == 0,
            "The macOS worker project must not declare Content, None or EmbeddedResource items " +
            "(models are read in place through --model-directory): " +
            string.Join("; ", fileItems.Select(element => element.ToString())));
        Check(!macWorker.Descendants().Any(element =>
                  element.Name.LocalName is "CopyToOutputDirectory" or "CopyToPublishDirectory" ||
                  element.Attributes().Any(attribute =>
                      attribute.Name.LocalName is "CopyToOutputDirectory" or "CopyToPublishDirectory")),
            "The macOS worker project must not copy any file to its output.");
    }

    private static void VerifyWorkerLayout(XDocument macWorker)
    {
        // MacWorkerTests derives the apphost path from these two values.
        Check(string.Equals(Property(macWorker, "TargetFramework"), MacWorkerTests.WorkerTargetFramework, StringComparison.Ordinal) &&
              string.Equals(Property(macWorker, "RuntimeIdentifier"), MacWorkerTests.WorkerRuntimeIdentifier, StringComparison.Ordinal),
            "The macOS worker output folder no longer matches the path MacWorkerTests starts.");
    }

    private static void VerifyFlatWorkerFolder(string root)
    {
        // The macOS worker links the shared sources with a top-level glob, so
        // a source file in a subfolder would compile on Windows only.
        string workerDirectory = Path.Combine(root, "src", "AsteriaScale.RankerWorker");
        string[] nested = Directory.EnumerateDirectories(workerDirectory)
            .Where(directory => Path.GetFileName(directory) is not ("bin" or "obj"))
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            .ToArray();
        Check(nested.Length == 0,
            "src/AsteriaScale.RankerWorker must keep its sources at the top level: " +
            string.Join(", ", nested));
    }

    private static void VerifyTestProjects(
        string root,
        XDocument windowsTests,
        XDocument macTests,
        XDocument wpf)
    {
        Check(LinkedWorkerFiles(windowsTests).SequenceEqual(LinkedWorkerFiles(macTests), StringComparer.Ordinal),
            "The two test projects link different worker files.");

        Dictionary<string, string> windowsPackages = Packages(windowsTests);
        Dictionary<string, string> macPackages = Packages(macTests);
        Check(windowsPackages.TryGetValue("Tokenizers.DotNet", out string? windowsTokenizers) &&
              macPackages.TryGetValue("Tokenizers.DotNet", out string? macTokenizers) &&
              windowsPackages.TryGetValue("Tokenizers.DotNet.runtime.win-x64", out string? windowsRuntime) &&
              macPackages.TryGetValue("Tokenizers.DotNet.runtime.osx-arm64", out string? macRuntime) &&
              new[] { macTokenizers, windowsRuntime, macRuntime }.All(version =>
                  string.Equals(version, windowsTokenizers, StringComparison.Ordinal)),
            "The test projects disagree on the Tokenizers.DotNet version.");
        Check(!macPackages.Keys.Any(package => package.StartsWith("Microsoft.ML.OnnxRuntime", StringComparison.Ordinal)),
            "The macOS test project must not reference ONNX Runtime.");

        XElement? workerReference = macTests.Descendants("ProjectReference").SingleOrDefault(element =>
            ((string?)element.Attribute("Include"))?.EndsWith(
                "AsteriaScale.RankerWorker.MacOS.csproj",
                StringComparison.Ordinal) == true);
        Check(workerReference is not null &&
              string.Equals((string?)workerReference.Attribute("ReferenceOutputAssembly"), "false", StringComparison.Ordinal),
            "The macOS test project must reference the macOS worker for build order only " +
            "(ReferenceOutputAssembly=false).");

        string[] excluded = macTests.Descendants("Compile")
            .Select(element => (string?)element.Attribute("Exclude"))
            .Where(value => value is not null)
            .SelectMany(value => value!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToArray();
        Check(excluded.Length == 6,
            $"Expected six Windows-only test classes to be excluded, found {excluded.Length}.");
        foreach (string entry in excluded)
        {
            Check(entry.StartsWith(SharedTestDirectoryToken, StringComparison.Ordinal),
                $"An excluded test class is not under $(SharedTestDirectory): {entry}");
            string path = Path.Combine(
                root,
                "tests",
                "AsteriaScale.Tests",
                entry[SharedTestDirectoryToken.Length..]);
            Check(File.Exists(path),
                $"An excluded Windows-only test class no longer exists, so the exclusion is stale: {entry}");
        }

        (string Include, string Link)[] expectedContent = ContentPairs(wpf)
            .Concat(ContentPairs(windowsTests))
            .ToArray();
        (string Include, string Link)[] macContent = ContentPairs(macTests).ToArray();
        Check(expectedContent.SequenceEqual(macContent),
            "The macOS test data items diverged from the WPF project plus the Windows test golden: " +
            $"expected [{string.Join("; ", expectedContent)}], found [{string.Join("; ", macContent)}].");
    }

    private static void VerifyOutsideTheSolution(string root)
    {
        string solution = File.ReadAllText(Path.Combine(root, "AsteriaScale.slnx"));
        Check(!solution.Contains(".MacOS", StringComparison.Ordinal) &&
              !solution.Contains(CoreHostName, StringComparison.Ordinal),
            "A macOS project was added to AsteriaScale.slnx; the solution is the Windows build.");
    }

    private static void VerifyNoHelperFilesInTestOutput()
    {
        // The worker and CoreHost references are for build order only; their
        // assemblies and ONNX Runtime must not reach the test output.
        string[] leaked = Directory.EnumerateFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .Where(name => name is not null &&
                (name.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase) ||
                 name.StartsWith("AsteriaScale.RankerWorker", StringComparison.Ordinal) ||
                 name.StartsWith(CoreHostName, StringComparison.Ordinal)))
            .Select(name => name!)
            .ToArray();
        Check(leaked.Length == 0,
            "The macOS test output contains worker, CoreHost or ONNX Runtime files: " + string.Join(", ", leaked));
    }

    private static XDocument Load(string root, string relativePath) =>
        XDocument.Load(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static Dictionary<string, string> Packages(XDocument project) =>
        project.Descendants("PackageReference").ToDictionary(
            element => (string?)element.Attribute("Include") ??
                throw new InvalidOperationException("A PackageReference has no Include."),
            element => (string?)element.Attribute("Version") ??
                throw new InvalidOperationException("A PackageReference has no Version."),
            StringComparer.Ordinal);

    private static string[] ProjectReferences(XDocument project) =>
        project.Descendants("ProjectReference")
            .Select(element => (string?)element.Attribute("Include") ?? string.Empty)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string? Property(XDocument project, string name) =>
        project.Descendants(name).SingleOrDefault()?.Value;

    private static string[] LinkedWorkerFiles(XDocument project) =>
        project.Descendants("Compile")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(include => include is not null &&
                include.StartsWith(SharedWorkerPrefix, StringComparison.Ordinal))
            .Select(include => include![SharedWorkerPrefix.Length..])
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static IEnumerable<(string Include, string Link)> ContentPairs(XDocument project) =>
        project.Descendants("Content").Select(element => (
            (string?)element.Attribute("Include") ?? string.Empty,
            (string?)element.Attribute("Link") ?? string.Empty));

    private static string Describe(Dictionary<string, string> packages) =>
        string.Join(", ", packages
            .OrderBy(package => package.Key, StringComparer.Ordinal)
            .Select(package => package.Key + " " + package.Value));

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
