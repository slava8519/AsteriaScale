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
    private static readonly TimeSpan SentenceLifetime = TimeSpan.FromSeconds(4);

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

            if (!IsEnabled || !IsProcessAllowed(processId))
            {
                StopCliLocked(clearSentences: true);
                return;
            }

            string? cliPath = ResolveCliPath(processId);
            if (cliPath is null)
            {
                StopCliLocked(clearSentences: true);
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
                long generation = ++_attachmentGeneration;
                _readLoop = ReadLoopAsync(_cliOutput, processId, generation);
                _errorDrain = DrainAsync(_cliError);
            }
            catch (Exception exception) when (exception is IOException or
                                              InvalidOperationException or
                                              System.ComponentModel.Win32Exception)
            {
                StopCliLocked(clearSentences: true);
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
        TryGetSentenceContaining(processId, word, preferredOccurrenceOrdinal: null, DateTime.UtcNow);

    internal TextHookMatch? TryGetSentenceContaining(
        int processId,
        string word,
        string ocrText,
        int ocrCharacterIndex)
    {
        int? occurrenceOrdinal = FindTokenOccurrenceOrdinal(
            ocrText,
            word,
            ocrCharacterIndex);
        return occurrenceOrdinal is null
            ? null
            : TryGetSentenceContaining(
                processId,
                word,
                occurrenceOrdinal,
                DateTime.UtcNow);
    }

    internal string? TryGetSentenceContaining(string word) =>
        TryGetSentenceContaining(0, word, preferredOccurrenceOrdinal: null, DateTime.UtcNow)?.Text;

    internal string? TryGetSentenceContaining(string word, DateTime nowUtc) =>
        TryGetSentenceContaining(0, word, preferredOccurrenceOrdinal: null, nowUtc)?.Text;

    internal TextHookMatch? TryGetSentenceContaining(
        int processId,
        string word,
        int? preferredOccurrenceOrdinal,
        DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(word))
        {
            return null;
        }

        lock (_gate)
        {
            foreach (HookSentence sentence in _recent)
            {
                if (nowUtc - sentence.ReceivedUtc > SentenceLifetime)
                {
                    break;
                }

                if ((processId == 0 || sentence.ProcessId == processId) &&
                    TryFindToken(
                        sentence.Text,
                        word,
                        preferredOccurrenceOrdinal,
                        out int characterIndex))
                {
                    return new TextHookMatch(sentence.Text, characterIndex);
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
        if (string.IsNullOrWhiteSpace(line) || line[0] != '[')
        {
            return;
        }

        int headerEnd = line.IndexOf(']');
        if (headerEnd <= 0 || headerEnd == line.Length - 1)
        {
            return;
        }

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

            _recent.AddFirst(new HookSentence(processId, sentence, receivedUtc));
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
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The CLI exited; the next hover simply reattaches or stays on OCR.
        }
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is not null)
            {
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
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
        string?[] roots =
        [
            Environment.GetEnvironmentVariable("ProgramFiles"),
            Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        ];
        return roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .SelectMany(root => new[]
            {
                Path.Combine(root!, "Textractor"),
                Path.Combine(root!, "Programs", "Textractor")
            })
            .FirstOrDefault(directory =>
                File.Exists(Path.Combine(directory, "x86", "TextractorCLI.exe")) ||
                File.Exists(Path.Combine(directory, "x64", "TextractorCLI.exe")) ||
                File.Exists(Path.Combine(directory, "TextractorCLI.exe")));
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
        int? preferredOccurrenceOrdinal,
        out int characterIndex)
    {
        string expected = word.Trim();
        int occurrenceOrdinal = 0;
        characterIndex = -1;
        foreach (Match match in WordRegex().Matches(text))
        {
            if (string.Equals(match.Value, expected, StringComparison.OrdinalIgnoreCase))
            {
                if (preferredOccurrenceOrdinal is null ||
                    occurrenceOrdinal == preferredOccurrenceOrdinal)
                {
                    characterIndex = match.Index;
                    return true;
                }

                occurrenceOrdinal++;
            }
        }

        return false;
    }

    private static int? FindTokenOccurrenceOrdinal(
        string text,
        string word,
        int characterIndex)
    {
        string expected = word.Trim();
        int occurrenceOrdinal = 0;
        foreach (Match match in WordRegex().Matches(text))
        {
            if (!string.Equals(match.Value, expected, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (characterIndex >= match.Index &&
                characterIndex < match.Index + match.Length)
            {
                return occurrenceOrdinal;
            }

            occurrenceOrdinal++;
        }

        // OCR occasionally reports a geometric fallback index outside the
        // token. Without a reliable occurrence mapping, retaining OCR context
        // is safer than assigning a repeated hook token to the wrong sense.
        return null;
    }

    [GeneratedRegex(@"[\p{L}\p{M}]+(?:['’\-][\p{L}\p{M}]+)*", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();

    private sealed record HookSentence(int ProcessId, string Text, DateTime ReceivedUtc);
}

internal sealed record TextHookMatch(string Text, int CharacterIndex);
