using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using AsteriaScale.Models;

namespace AsteriaScale.Services;

/// <summary>
/// Optional Textractor-based text source (roadmap task #4). Textractor is
/// GPLv3 and injects into the game process, so it runs strictly as a separate
/// user-installed CLI, mirroring the Magpie isolation model. The hook only
/// upgrades hover context quality: the hovered word and its position always
/// come from OCR, and any failure here silently leaves OCR in charge, keeping
/// translation universal for arbitrary programs and documents.
/// </summary>
public sealed partial class TextHookService : IAsyncDisposable
{
    private const int MaximumSentences = 32;
    internal const int MaximumContextSentences = PreviousContextLimits.MaximumSentences;
    internal const int MaximumContextCharacters = PreviousContextLimits.MaximumCharacters;
    private static readonly TimeSpan SentenceLifetime = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ContextLifetime = TimeSpan.FromMinutes(2);

    private readonly AppSettings _settings;
    private readonly object _gate = new();
    private readonly LinkedList<HookSentence> _recent = new();
    private readonly List<Task> _retiredReaders = [];
    private Process? _cli;
    private StreamWriter? _cliInput;
    private StreamReader? _cliOutput;
    private StreamReader? _cliError;
    private Task? _readLoop;
    private Task? _errorDrain;
    private int _attachedProcessId;
    private long _attachmentGeneration;
    private string _activeCliPath = string.Empty;
    private TextHookStatusSnapshot _status = TextHookStatusSnapshot.Disabled();
    private string _lastCliMessage = string.Empty;
    private bool _disposed;

    public TextHookService(AppSettings settings)
    {
        _settings = settings;
    }

    public bool IsEnabled =>
        _settings.TextHookEnabled &&
        !string.IsNullOrWhiteSpace(_settings.TextractorDirectory);

    internal int RecentSentenceCount
    {
        get
        {
            lock (_gate)
            {
                return _recent.Count;
            }
        }
    }

    internal int AttachedProcessId
    {
        get
        {
            lock (_gate)
            {
                return _attachedProcessId;
            }
        }
    }

    /// <summary>
    /// Returns an honest, display-safe snapshot of the current hook state.
    /// A running injector is only "active" for a card after its exact OCR
    /// target was found in a fresh hooked sentence; attach alone means waiting.
    /// </summary>
    internal TextHookStatusSnapshot GetStatusSnapshot(
        int expectedProcessId,
        bool matchedCurrentText = false)
    {
        lock (_gate)
        {
            if (!_settings.TextHookEnabled)
            {
                return TextHookStatusSnapshot.Disabled(
                    "Text-hook выключен в настройках; используется OCR.");
            }

            if (string.IsNullOrWhiteSpace(_settings.TextractorDirectory))
            {
                return TextHookStatusSnapshot.Unavailable(
                    expectedProcessId,
                    DescribeTarget(expectedProcessId),
                    string.Empty,
                    "Папка Textractor не указана; используется OCR.");
            }

            if (_disposed)
            {
                return TextHookStatusSnapshot.Unavailable(
                    expectedProcessId,
                    DescribeTarget(expectedProcessId),
                    string.Empty,
                    "Служба text-hook уже остановлена; используется OCR.");
            }

            TextHookStatusSnapshot status = _status;
            if (status.State == TextHookRuntimeState.Disabled &&
                status.ProcessId == 0)
            {
                return TextHookStatusSnapshot.Unavailable(
                    expectedProcessId,
                    DescribeTarget(expectedProcessId),
                    string.Empty,
                    "Целевой процесс для Textractor не определён; используется OCR.");
            }

            if (expectedProcessId > 0 && status.ProcessId != expectedProcessId)
            {
                ProcessTarget target = DescribeTarget(expectedProcessId);
                return TextHookStatusSnapshot.Unavailable(
                    expectedProcessId,
                    target,
                    string.Empty,
                    $"Textractor не подключён к {target.DisplayName}; используется OCR.");
            }

            if (status.State is TextHookRuntimeState.AttachedWaiting or
                TextHookRuntimeState.Active)
            {
                bool alive;
                try
                {
                    alive = _cli is { HasExited: false };
                }
                catch (InvalidOperationException)
                {
                    alive = false;
                }

                if (!alive)
                {
                    status = status with
                    {
                        State = TextHookRuntimeState.Unavailable,
                        Details = $"TextractorCLI завершился после подключения к " +
                                  $"{status.TargetDisplayName}; используется OCR."
                    };
                    _status = status;
                }
            }

            TextHookStatusSnapshot verifiedStatus = status.WithCurrentMatch(matchedCurrentText);
            if (verifiedStatus.State != status.State)
            {
                // Once an exact OCR-anchored sentence has proved the stream,
                // keep reporting the hook as operational for this attachment.
                // Individual cards still state OCR when that specific context
                // did not come from Textractor.
                _status = verifiedStatus;
            }

            status = verifiedStatus;
            if (!string.IsNullOrWhiteSpace(_lastCliMessage))
            {
                status = status with
                {
                    Details = status.Details + " Последнее сообщение CLI: " + _lastCliMessage
                };
            }

            return status;
        }
    }

    /// <summary>
    /// Best-effort attach to the process under the cursor. Injection may be
    /// refused (elevation, antivirus); every failure leaves OCR in charge.
    /// </summary>
    public void EnsureAttached(int processId)
    {
        if (processId <= 0 || _disposed)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            ProcessTarget target = DescribeTarget(processId);
            if (!IsEnabled)
            {
                StopCliLocked(clearSentences: true);
                _status = !_settings.TextHookEnabled
                    ? TextHookStatusSnapshot.Disabled(
                        "Text-hook выключен в настройках; используется OCR.")
                    : TextHookStatusSnapshot.Unavailable(
                        processId,
                        target,
                        string.Empty,
                        "Папка Textractor не указана; используется OCR.");
                return;
            }

            if (!IsProcessAllowed(processId))
            {
                StopCliLocked(clearSentences: true);
                _status = TextHookStatusSnapshot.Disabled(
                    $"Text-hook выключен для {target.DisplayName}; используется OCR.",
                    processId,
                    target);
                return;
            }

            string? cliPath = ResolveCliPath(processId);
            if (cliPath is null)
            {
                StopCliLocked(clearSentences: true);
                _status = TextHookStatusSnapshot.Unavailable(
                    processId,
                    target,
                    string.Empty,
                    $"TextractorCLI для {target.DisplayName} не найден в " +
                    $"'{_settings.TextractorDirectory}'; используется OCR.");
                return;
            }

            if (_attachedProcessId == processId &&
                _cli is { HasExited: false } &&
                string.Equals(_activeCliPath, cliPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                StopCliLocked(clearSentences: true);
                _lastCliMessage = string.Empty;

                ProcessStartInfo startInfo = new(cliPath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    // TextractorCLI speaks UTF-16LE on both pipes.
                    StandardInputEncoding = Encoding.Unicode,
                    StandardOutputEncoding = Encoding.Unicode,
                    WorkingDirectory = Path.GetDirectoryName(cliPath)!
                };
                Process? cli = Process.Start(startInfo);
                if (cli is null)
                {
                    _status = TextHookStatusSnapshot.Unavailable(
                        processId,
                        target,
                        cliPath,
                        $"Не удалось запустить TextractorCLI для {target.DisplayName}; используется OCR.");
                    return;
                }

                _cli = cli;
                _cliInput = cli.StandardInput;
                _cliOutput = cli.StandardOutput;
                _cliError = cli.StandardError;
                _cliInput.AutoFlush = true;
                _cliInput.WriteLine($"attach -P{processId}");
                foreach (string hookCode in ResolveHookCodes(processId))
                {
                    _cliInput.WriteLine($"{hookCode} -P{processId}");
                }

                _attachedProcessId = processId;
                _activeCliPath = cliPath;
                _status = TextHookStatusSnapshot.Attached(
                    processId,
                    target,
                    cliPath);
                long generation = ++_attachmentGeneration;
                _readLoop = ReadLoopAsync(_cliOutput, processId, generation);
                _errorDrain = DrainAsync(_cliError, processId, generation);
            }
            catch (Exception exception) when (exception is IOException or
                                              InvalidOperationException or
                                              System.ComponentModel.Win32Exception)
            {
                StopCliLocked(clearSentences: true);
                _status = TextHookStatusSnapshot.Unavailable(
                    processId,
                    target,
                    cliPath,
                    $"Не удалось подключить Textractor к {target.DisplayName}: " +
                    $"{CompactDiagnostic(exception.Message)} Используется OCR.");
            }
        }
    }

    /// <summary>
    /// Applies a changed global toggle/path immediately. Waiting for the next
    /// OCR request would leave Textractor injected after the user disabled it.
    /// </summary>
    public void ApplySettings()
    {
        lock (_gate)
        {
            if (_disposed || !IsEnabled)
            {
                StopCliLocked(clearSentences: true);
                return;
            }

            if (_attachedProcessId > 0)
            {
                string? expectedPath = ResolveCliPath(_attachedProcessId);
                if (!IsProcessAllowed(_attachedProcessId) ||
                    expectedPath is null ||
                    !string.Equals(expectedPath, _activeCliPath, StringComparison.OrdinalIgnoreCase))
                {
                    StopCliLocked(clearSentences: true);
                }
            }
        }
    }

    public void Detach()
    {
        lock (_gate)
        {
            StopCliLocked(clearSentences: true);
        }
    }

    /// <summary>
    /// Returns the freshest hooked sentence containing the OCR-anchored word.
    /// Containment is the relevance guard, so no hook selection UI is needed:
    /// noisy hook threads simply never match the hovered word.
    /// </summary>
    internal TextHookMatch? TryGetSentenceContaining(int processId, string word) =>
        TryGetSentenceContaining(processId, word, anchor: null, DateTime.UtcNow);

    internal TextHookMatch? TryGetSentenceContaining(
        int processId,
        string word,
        string ocrText,
        int ocrCharacterIndex)
    {
        OcrTargetAnchor? anchor = ResolveOcrTargetAnchor(
            ocrText,
            word,
            ocrCharacterIndex);
        return anchor is null
            ? null
            : TryGetSentenceContaining(
                processId,
                word,
                anchor,
                DateTime.UtcNow);
    }

    internal string? TryGetSentenceContaining(string word) =>
        TryGetSentenceContaining(0, word, anchor: null, DateTime.UtcNow)?.Text;

    internal string? TryGetSentenceContaining(string word, DateTime nowUtc) =>
        TryGetSentenceContaining(0, word, anchor: null, nowUtc)?.Text;

    private TextHookMatch? TryGetSentenceContaining(
        int processId,
        string word,
        OcrTargetAnchor? anchor,
        DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(word))
        {
            return null;
        }

        lock (_gate)
        {
            for (LinkedListNode<HookSentence>? node = _recent.First;
                 node is not null;
                 node = node.Next)
            {
                HookSentence sentence = node.Value;
                if (nowUtc - sentence.ReceivedUtc > SentenceLifetime)
                {
                    break;
                }

                if ((processId == 0 || sentence.ProcessId == processId) &&
                    TryFindToken(
                        sentence.Text,
                        word,
                        anchor,
                        out int characterIndex))
                {
                    string dialogue = sentence.Text;
                    CurrentSpeakerContext? currentSpeaker = null;
                    LinkedListNode<HookSentence>? boundSpeakerNode = null;
                    if (SpeakerMetadataParser.TryFindLeadingSpeaker(
                            dialogue,
                            characterIndex,
                            out CurrentSpeakerContext? inlineSpeaker,
                            out int dialogueStart))
                    {
                        dialogue = dialogue[dialogueStart..];
                        characterIndex -= dialogueStart;
                        currentSpeaker = inlineSpeaker;
                    }
                    else if (TryBindImmediatelyPrecedingSpeaker(
                                 node,
                                 sentence,
                                 nowUtc,
                                 out CurrentSpeakerContext? precedingSpeaker,
                                 out boundSpeakerNode))
                    {
                        currentSpeaker = precedingSpeaker;
                    }

                    return new TextHookMatch(dialogue, characterIndex)
                    {
                        PreviousContext = CollectPreviousContext(
                            node,
                            sentence,
                            nowUtc,
                            boundSpeakerNode),
                        CurrentSpeaker = currentSpeaker
                    };
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Parses one TextractorCLI output line: hooked sentences arrive as
    /// "[hookcode:pid:...:name] sentence"; anything without that header is CLI
    /// diagnostics and is ignored.
    /// </summary>
    internal void IngestLine(string? line) => IngestLine(line, processId: 0);

    internal void IngestLine(string? line, int processId)
    {
        long generation;
        lock (_gate)
        {
            generation = _attachmentGeneration;
        }

        IngestLine(line, processId, generation, DateTime.UtcNow);
    }

    private void IngestLine(
        string? line,
        int processId,
        long generation,
        DateTime receivedUtc)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (line[0] != '[')
        {
            RecordCliMessage(line, processId, generation);
            return;
        }

        int headerEnd = line.IndexOf(']');
        if (headerEnd <= 0 || headerEnd == line.Length - 1)
        {
            return;
        }

        string streamIdentity = line[..(headerEnd + 1)];
        string sentence = string.Join(
            " ",
            line[(headerEnd + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (sentence.Length == 0 || sentence.Length > 2000)
        {
            return;
        }

        lock (_gate)
        {
            if (generation != _attachmentGeneration ||
                (processId != 0 &&
                 _attachedProcessId != 0 &&
                 processId != _attachedProcessId))
            {
                return;
            }

            if (_recent.First is { } newest &&
                newest.Value.ProcessId == processId &&
                string.Equals(newest.Value.Text, sentence, StringComparison.Ordinal))
            {
                return;
            }

            _recent.AddFirst(new HookSentence(
                processId,
                streamIdentity,
                sentence,
                receivedUtc));
            while (_recent.Count > MaximumSentences)
            {
                _recent.RemoveLast();
            }
        }
    }

    private async Task ReadLoopAsync(StreamReader reader, int processId, long generation)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                IngestLine(line, processId, generation, DateTime.UtcNow);
            }

            RecordCliExit(processId, generation);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The CLI exited; the next hover simply reattaches or stays on OCR.
            RecordCliExit(processId, generation);
        }
    }

    private async Task DrainAsync(StreamReader reader, int processId, long generation)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                RecordCliMessage(line, processId, generation);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
        }
    }

    private void RecordCliMessage(string message, int processId, long generation)
    {
        string compact = CompactDiagnostic(message);
        if (compact.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (generation == _attachmentGeneration &&
                (_attachedProcessId == 0 ||
                 processId == 0 ||
                 processId == _attachedProcessId))
            {
                _lastCliMessage = compact;
            }
        }
    }

    private void RecordCliExit(int processId, long generation)
    {
        lock (_gate)
        {
            if (generation != _attachmentGeneration ||
                processId != _attachedProcessId)
            {
                return;
            }

            _attachedProcessId = 0;
            _status = _status with
            {
                State = TextHookRuntimeState.Unavailable,
                Details = $"TextractorCLI завершился после подключения к " +
                          $"{_status.TargetDisplayName}; используется OCR."
            };
        }
    }

    /// <summary>
    /// A profile explicitly controls hooking for its own game; applications
    /// without a matching profile follow the global toggle alone.
    /// </summary>
    public bool IsProcessAllowed(int processId)
    {
        AutomationProfile? profile = FindProfileFor(processId);
        return profile?.TextHookEnabled ?? true;
    }

    private AutomationProfile? FindProfileFor(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            string name = process.ProcessName;
            string path;
            try
            {
                path = process.MainModule?.FileName ?? string.Empty;
            }
            catch (Exception exception) when (exception is InvalidOperationException or
                                              System.ComponentModel.Win32Exception or
                                              NotSupportedException)
            {
                path = string.Empty;
            }

            AutomationProfile? pathProfile = _settings.Profiles.FirstOrDefault(profile =>
                profile.Enabled &&
                !string.IsNullOrWhiteSpace(profile.ProcessPath) &&
                !string.IsNullOrWhiteSpace(path) &&
                string.Equals(profile.ProcessPath, path, StringComparison.OrdinalIgnoreCase));
            return pathProfile ?? _settings.Profiles.FirstOrDefault(profile =>
                profile.Enabled &&
                string.IsNullOrWhiteSpace(profile.ProcessPath) &&
                !string.IsNullOrWhiteSpace(profile.ProcessName) &&
                string.Equals(profile.ProcessName, name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException or
                                          System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static readonly Lazy<ILookup<string, string>> KnownHookCodes =
        new(LoadKnownHookCodes, isThreadSafe: true);

    /// <summary>
    /// Automatic H-code selection: a user-set profile code wins; otherwise all
    /// database codes for this exe are applied. Sending several candidate
    /// codes is safe by construction — a wrong code simply hooks nothing, and
    /// noisy output never matches the hovered word.
    /// </summary>
    internal IReadOnlyList<string> ResolveHookCodes(int processId)
    {
        string? profileCode = FindProfileFor(processId)?.TextHookCode;
        if (!string.IsNullOrWhiteSpace(profileCode))
        {
            return [profileCode.Trim()];
        }

        try
        {
            using Process process = Process.GetProcessById(processId);
            return KnownHookCodes.Value[NormalizeExeKey(process.ProcessName)]
                .Take(8)
                .ToArray();
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException)
        {
            return [];
        }
    }

    private static ILookup<string, string> LoadKnownHookCodes()
    {
        List<(string Key, string Code)> rows = [];
        string executableDirectory = Path.GetDirectoryName(Environment.ProcessPath)
            ?? AppContext.BaseDirectory;
        string[] files =
        [
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AsteriaScale",
                "TextHook",
                "hook-codes.tsv"),
            Path.Combine(executableDirectory, "TextHook", "hook-codes.tsv")
        ];
        foreach (string path in files)
        {
            try
            {
                if (File.Exists(path))
                {
                    ParseHookCodeLines(File.ReadLines(path), rows);
                }
            }
            catch (Exception exception) when (exception is IOException or
                                              UnauthorizedAccessException)
            {
                // The database is an optional aid; attach works without it.
            }
        }

        return rows.ToLookup(
            row => row.Key,
            row => row.Code,
            StringComparer.OrdinalIgnoreCase);
    }

    internal static void ParseHookCodeLines(
        IEnumerable<string> lines,
        List<(string Key, string Code)> rows)
    {
        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            string[] parts = line.Split('\t');
            if (parts.Length < 2)
            {
                continue;
            }

            string key = NormalizeExeKey(parts[0]);
            string code = parts[1].Trim();
            if (key.Length == 0 ||
                code.Length == 0 ||
                !(code.StartsWith('H') || code.StartsWith('R') || code.StartsWith('/')))
            {
                continue;
            }

            if (!rows.Contains((key, code)))
            {
                rows.Add((key, code));
            }
        }
    }

    internal static string NormalizeExeKey(string value)
    {
        string key = value.Trim().ToLowerInvariant();
        return key.EndsWith(".exe", StringComparison.Ordinal) ? key[..^4] : key;
    }

    /// <summary>Probes standard install locations to prefill the UI path.</summary>
    public static string? ProbeDefaultDirectory()
    {
        string? systemDrive = Path.GetPathRoot(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        string?[] roots =
        [
            Environment.GetEnvironmentVariable("ProgramFiles"),
            Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        ];
        IEnumerable<string> candidates = new[]
            {
                Environment.GetEnvironmentVariable("TEXTRACTOR_DIRECTORY"),
                string.IsNullOrWhiteSpace(systemDrive)
                    ? null
                    : Path.Combine(systemDrive, "Textractor")
            }
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Select(directory => directory!)
            .Concat(roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .SelectMany(root => new[]
            {
                Path.Combine(root!, "Textractor"),
                Path.Combine(root!, "Programs", "Textractor")
            }));
        return ProbeDefaultDirectory(candidates);
    }

    internal static string? ProbeDefaultDirectory(IEnumerable<string> candidates) =>
        candidates
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(IsTextractorDirectory);

    public static bool IsTextractorDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        try
        {
            return File.Exists(Path.Combine(directory, "x86", "TextractorCLI.exe")) ||
                   File.Exists(Path.Combine(directory, "x64", "TextractorCLI.exe")) ||
                   File.Exists(Path.Combine(directory, "TextractorCLI.exe"));
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          IOException or
                                          NotSupportedException)
        {
            return false;
        }
    }

    public static string DescribeInstallation(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return "папка Textractor не указана";
        }

        bool x86 = File.Exists(Path.Combine(directory, "x86", "TextractorCLI.exe"));
        bool x64 = File.Exists(Path.Combine(directory, "x64", "TextractorCLI.exe"));
        bool flat = File.Exists(Path.Combine(directory, "TextractorCLI.exe"));
        if (!x86 && !x64 && !flat)
        {
            return "TextractorCLI.exe не найден — используется OCR";
        }

        string architecture = (x86, x64, flat) switch
        {
            (true, true, _) => "x86 и x64",
            (true, false, _) => "x86",
            (false, true, _) => "x64",
            _ => "универсальный CLI"
        };
        return $"Textractor готов ({architecture})";
    }

    /// <summary>
    /// Picks the CLI matching the target's bitness: old visual novels are
    /// mostly 32-bit and require the x86 injector.
    /// </summary>
    private string? ResolveCliPath(int processId)
    {
        string root = _settings.TextractorDirectory;
        bool targetIs64Bit = Is64BitProcess(processId);
        string[] candidates = targetIs64Bit
            ? [Path.Combine(root, "x64", "TextractorCLI.exe"), Path.Combine(root, "TextractorCLI.exe")]
            : [Path.Combine(root, "x86", "TextractorCLI.exe"), Path.Combine(root, "TextractorCLI.exe")];
        return candidates.FirstOrDefault(File.Exists);
    }

    private static bool Is64BitProcess(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return Interop.NativeMethods.IsWow64Process(process.Handle, out bool isWow64) && !isWow64;
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException or
                                          System.ComponentModel.Win32Exception)
        {
            return !Environment.Is64BitOperatingSystem;
        }
    }

    private static ProcessTarget DescribeTarget(int processId)
    {
        if (processId <= 0)
        {
            return new ProcessTarget(
                processId,
                "неизвестный процесс",
                "архитектура неизвестна");
        }

        try
        {
            using Process process = Process.GetProcessById(processId);
            string executableName = process.ProcessName.EndsWith(
                    ".exe",
                    StringComparison.OrdinalIgnoreCase)
                ? process.ProcessName
                : process.ProcessName + ".exe";
            string architecture;
            try
            {
                architecture = !Environment.Is64BitOperatingSystem
                    ? "x86"
                    : Interop.NativeMethods.IsWow64Process(process.Handle, out bool isWow64)
                        ? isWow64 ? "x86" : "x64"
                        : "архитектура неизвестна";
            }
            catch (Exception exception) when (exception is InvalidOperationException or
                                              System.ComponentModel.Win32Exception or
                                              NotSupportedException)
            {
                architecture = "архитектура неизвестна";
            }

            return new ProcessTarget(processId, executableName, architecture);
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException or
                                          System.ComponentModel.Win32Exception)
        {
            return new ProcessTarget(
                processId,
                "неизвестный процесс",
                "архитектура неизвестна");
        }
    }

    private static string CompactDiagnostic(string? value)
    {
        string compact = string.Join(
            " ",
            (value ?? string.Empty).Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 240 ? compact : compact[..237] + "…";
    }

    private void StopCliLocked(bool clearSentences)
    {
        Process? cli = _cli;
        StreamWriter? input = _cliInput;
        StreamReader? output = _cliOutput;
        StreamReader? error = _cliError;
        Task? readLoop = _readLoop;
        Task? errorDrain = _errorDrain;
        int attached = _attachedProcessId;
        _cli = null;
        _cliInput = null;
        _cliOutput = null;
        _cliError = null;
        _readLoop = null;
        _errorDrain = null;
        _attachedProcessId = 0;
        _activeCliPath = string.Empty;
        _attachmentGeneration++;
        if (clearSentences)
        {
            _recent.Clear();
        }

        if (readLoop is not null)
        {
            _retiredReaders.Add(readLoop);
        }

        if (errorDrain is not null)
        {
            _retiredReaders.Add(errorDrain);
        }

        if (cli is null)
        {
            input?.Dispose();
            output?.Dispose();
            error?.Dispose();
            return;
        }

        try
        {
            if (!cli.HasExited && input is not null && attached > 0)
            {
                input.WriteLine($"detach -P{attached}");
                input.Flush();
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
        }

        try
        {
            if (!cli.HasExited)
            {
                cli.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or
                                          System.ComponentModel.Win32Exception)
        {
        }

        input?.Dispose();
        output?.Dispose();
        error?.Dispose();
        cli.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        Task[] readers;
        _disposed = true;
        lock (_gate)
        {
            StopCliLocked(clearSentences: true);
            readers = _retiredReaders.ToArray();
            _retiredReaders.Clear();
        }

        if (readers.Length > 0)
        {
            try
            {
                await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception exception) when (exception is IOException or
                                              ObjectDisposedException or
                                              TimeoutException)
            {
                // The process tree has already been killed; shutdown must not wait forever.
            }
        }
    }

    private static bool TryFindToken(
        string text,
        string word,
        OcrTargetAnchor? anchor,
        out int characterIndex)
    {
        string expected = word.Trim();
        characterIndex = -1;
        Match[] occurrences = WordRegex()
            .Matches(text)
            .Where(match => string.Equals(
                match.Value,
                expected,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (occurrences.Length == 0)
        {
            return false;
        }

        if (anchor is null)
        {
            characterIndex = occurrences[0].Index;
            return true;
        }

        // The ordinal was counted inside the one sentence OCR showed, so it may
        // only be applied inside a hook sentence carrying those same words. One
        // Textractor line usually holds several sentences; counting across the
        // whole line silently bound a repeat from a neighbouring one and sent
        // that sentence to the phrase model instead of the hovered one.
        // Occurrences arrive in index order and sentence spans do not overlap,
        // so occurrences sharing a span form one contiguous run.
        int spanStart = -1;
        int ordinal = 0;
        foreach (Match occurrence in occurrences)
        {
            (int start, int end) = SentenceBoundaryRules.FindContainingSpan(
                text,
                occurrence.Index,
                occurrence.Index + occurrence.Length);
            ordinal = start == spanStart ? ordinal + 1 : 0;
            spanStart = start;
            if (ordinal == anchor.OccurrenceOrdinal &&
                SentenceComparison.IsOrderedSubsequence(
                    anchor.SentenceTokens,
                    SentenceComparison.Tokens(text[start..end])))
            {
                characterIndex = occurrence.Index;
                return true;
            }
        }

        // A single occurrence needs no ordinal, so a line OCR read imperfectly
        // still upgrades the context. A repeated token without a comparable
        // sentence keeps OCR rather than guessing which one was hovered.
        if (occurrences.Length == 1)
        {
            characterIndex = occurrences[0].Index;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The hovered target expressed the way a hook line has to be searched: the
    /// sentence OCR actually showed, plus which occurrence inside that sentence
    /// carried the cursor. An ordinal alone is not transferable — the two texts
    /// split into sentences differently.
    /// </summary>
    private sealed record OcrTargetAnchor(
        IReadOnlyList<string> SentenceTokens,
        int OccurrenceOrdinal);

    private static OcrTargetAnchor? ResolveOcrTargetAnchor(
        string text,
        string word,
        int characterIndex)
    {
        if (string.IsNullOrEmpty(text) ||
            characterIndex < 0 ||
            characterIndex > text.Length)
        {
            return null;
        }

        // Smart mode hands over a single sentence, dictionary mode a whole OCR
        // line. Narrowing to the sentence around the cursor makes both produce
        // an ordinal that a hook sentence can actually be searched with.
        (int start, int end) = SentenceBoundaryRules.FindContainingSpan(
            text,
            characterIndex,
            characterIndex);
        string sentence = text[start..end];
        int sentenceIndex = characterIndex - start;
        string expected = word.Trim();
        int occurrenceOrdinal = 0;
        foreach (Match match in WordRegex().Matches(sentence))
        {
            if (!string.Equals(match.Value, expected, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (sentenceIndex >= match.Index &&
                sentenceIndex < match.Index + match.Length)
            {
                return new OcrTargetAnchor(
                    SentenceComparison.Tokens(sentence),
                    occurrenceOrdinal);
            }

            occurrenceOrdinal++;
        }

        // OCR occasionally reports a geometric fallback index outside the
        // token. Without a reliable occurrence mapping, retaining OCR context
        // is safer than assigning a repeated hook token to the wrong sense.
        return null;
    }

    private static bool TryBindImmediatelyPrecedingSpeaker(
        LinkedListNode<HookSentence> currentNode,
        HookSentence current,
        DateTime nowUtc,
        out CurrentSpeakerContext? speaker,
        out LinkedListNode<HookSentence>? speakerNode)
    {
        speaker = null;
        speakerNode = currentNode.Next;
        if (speakerNode is null)
        {
            return false;
        }

        HookSentence candidate = speakerNode.Value;
        TimeSpan separation = current.ReceivedUtc - candidate.ReceivedUtc;
        if (candidate.ProcessId != current.ProcessId ||
            !string.Equals(
                candidate.StreamIdentity,
                current.StreamIdentity,
                StringComparison.Ordinal) ||
            separation < TimeSpan.Zero ||
            separation > SentenceLifetime ||
            nowUtc - candidate.ReceivedUtc > SentenceLifetime ||
            !SpeakerMetadataParser.TryParseStandaloneName(
                candidate.Text,
                out string name))
        {
            speakerNode = null;
            return false;
        }

        speaker = new CurrentSpeakerContext(name);
        return true;
    }

    private static IReadOnlyList<string> CollectPreviousContext(
        LinkedListNode<HookSentence> currentNode,
        HookSentence current,
        DateTime nowUtc,
        LinkedListNode<HookSentence>? excludedNode = null)
    {
        List<string> newestFirst = [];
        HashSet<string> seen = new(StringComparer.Ordinal)
        {
            current.Text
        };
        int characters = 0;
        for (LinkedListNode<HookSentence>? node = currentNode.Next;
             node is not null && newestFirst.Count < MaximumContextSentences;
             node = node.Next)
        {
            HookSentence candidate = node.Value;
            if (nowUtc - candidate.ReceivedUtc > ContextLifetime)
            {
                break;
            }

            if (ReferenceEquals(node, excludedNode))
            {
                continue;
            }

            if (candidate.ProcessId != current.ProcessId ||
                !string.Equals(
                    candidate.StreamIdentity,
                    current.StreamIdentity,
                    StringComparison.Ordinal) ||
                !seen.Add(candidate.Text))
            {
                continue;
            }

            int nextCharacters = characters + candidate.Text.Length;
            if (nextCharacters > MaximumContextCharacters)
            {
                continue;
            }

            newestFirst.Add(candidate.Text);
            characters = nextCharacters;
        }

        newestFirst.Reverse();
        return Array.AsReadOnly(newestFirst.ToArray());
    }

    [GeneratedRegex(@"[\p{L}\p{M}]+(?:['’\-][\p{L}\p{M}]+)*", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();

    private sealed record HookSentence(
        int ProcessId,
        string StreamIdentity,
        string Text,
        DateTime ReceivedUtc);
}

internal sealed record TextHookMatch(string Text, int CharacterIndex)
{
    public IReadOnlyList<string> PreviousContext { get; init; } = Array.Empty<string>();

    public CurrentSpeakerContext? CurrentSpeaker { get; init; }
}

internal enum TextHookRuntimeState
{
    Disabled,
    Unavailable,
    AttachedWaiting,
    Active
}

internal sealed record ProcessTarget(
    int ProcessId,
    string ExecutableName,
    string Architecture)
{
    internal string DisplayName => ProcessId > 0
        ? $"{ExecutableName} (PID {ProcessId}, {Architecture})"
        : ExecutableName;
}

internal sealed record TextHookStatusSnapshot(
    TextHookRuntimeState State,
    int ProcessId,
    string ExecutableName,
    string ProcessArchitecture,
    string CliPath,
    string Details)
{
    internal string TargetDisplayName => ProcessId > 0
        ? $"{ExecutableName} (PID {ProcessId}, {ProcessArchitecture})"
        : ExecutableName;

    internal string CardBadge => State switch
    {
        TextHookRuntimeState.Disabled => "OCR · HOOK ВЫКЛ",
        TextHookRuntimeState.Unavailable => "OCR · HOOK НЕТ",
        TextHookRuntimeState.AttachedWaiting => "OCR · HOOK ЖДЁТ",
        TextHookRuntimeState.Active => "OCR · HOOK АКТИВЕН",
        _ => "OCR"
    };

    internal TextHookStatusSnapshot WithCurrentMatch(bool matched) =>
        matched && State == TextHookRuntimeState.AttachedWaiting
            ? this with
            {
                State = TextHookRuntimeState.Active,
                Details = $"Контекст карточки получен из Textractor для " +
                          $"{TargetDisplayName} через '{CliPath}'."
            }
            : this;

    internal static TextHookStatusSnapshot Disabled(
        string details = "Text-hook выключен; используется OCR.",
        int processId = 0,
        ProcessTarget? target = null) =>
        new(
            TextHookRuntimeState.Disabled,
            processId,
            target?.ExecutableName ?? "text-hook",
            target?.Architecture ?? string.Empty,
            string.Empty,
            details);

    internal static TextHookStatusSnapshot Unavailable(
        int processId,
        ProcessTarget target,
        string cliPath,
        string details) =>
        new(
            TextHookRuntimeState.Unavailable,
            processId,
            target.ExecutableName,
            target.Architecture,
            cliPath,
            details);

    internal static TextHookStatusSnapshot Attached(
        int processId,
        ProcessTarget target,
        string cliPath) =>
        new(
            TextHookRuntimeState.AttachedWaiting,
            processId,
            target.ExecutableName,
            target.Architecture,
            cliPath,
            $"TextractorCLI подключён к {target.DisplayName} через '{cliPath}'; " +
            "совпадающая строка пока не получена.");
}
