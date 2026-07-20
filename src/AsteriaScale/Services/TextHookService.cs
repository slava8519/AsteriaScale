using System.Diagnostics;
using System.IO;
using System.Text;
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
public sealed class TextHookService : IAsyncDisposable
{
    private const int MaximumSentences = 32;
    private static readonly TimeSpan SentenceLifetime = TimeSpan.FromMinutes(2);

    private readonly AppSettings _settings;
    private readonly object _gate = new();
    private readonly LinkedList<(string Text, DateTime ReceivedUtc)> _recent = new();
    private Process? _cli;
    private StreamWriter? _cliInput;
    private Task? _readLoop;
    private int _attachedProcessId;
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

    /// <summary>
    /// Best-effort attach to the process under the cursor. Injection may be
    /// refused (elevation, antivirus); every failure leaves OCR in charge.
    /// </summary>
    public void EnsureAttached(int processId)
    {
        if (!IsEnabled || processId <= 0 || _disposed)
        {
            return;
        }

        lock (_gate)
        {
            if (_attachedProcessId == processId && _cli is { HasExited: false })
            {
                return;
            }

            try
            {
                StopCliLocked();
                string? cliPath = ResolveCliPath(processId);
                if (cliPath is null)
                {
                    return;
                }

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
                _cliInput.AutoFlush = true;
                _cliInput.WriteLine($"attach -P{processId}");
                foreach (string hookCode in ResolveHookCodes(processId))
                {
                    _cliInput.WriteLine($"{hookCode} -P{processId}");
                }

                _attachedProcessId = processId;
                _readLoop = ReadLoopAsync(cli.StandardOutput);
                _ = DrainAsync(cli.StandardError);
            }
            catch (Exception exception) when (exception is IOException or
                                              InvalidOperationException or
                                              System.ComponentModel.Win32Exception)
            {
                _attachedProcessId = 0;
            }
        }
    }

    /// <summary>
    /// Returns the freshest hooked sentence containing the OCR-anchored word.
    /// Containment is the relevance guard, so no hook selection UI is needed:
    /// noisy hook threads simply never match the hovered word.
    /// </summary>
    public string? TryGetSentenceContaining(string word) =>
        TryGetSentenceContaining(word, DateTime.UtcNow);

    internal string? TryGetSentenceContaining(string word, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(word))
        {
            return null;
        }

        lock (_gate)
        {
            foreach ((string text, DateTime receivedUtc) in _recent)
            {
                if (nowUtc - receivedUtc > SentenceLifetime)
                {
                    break;
                }

                if (text.Contains(word, StringComparison.OrdinalIgnoreCase))
                {
                    return text;
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
    internal void IngestLine(string? line)
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
            if (_recent.First is { } newest &&
                string.Equals(newest.Value.Text, sentence, StringComparison.Ordinal))
            {
                return;
            }

            _recent.AddFirst((sentence, DateTime.UtcNow));
            while (_recent.Count > MaximumSentences)
            {
                _recent.RemoveLast();
            }
        }
    }

    private async Task ReadLoopAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                IngestLine(line);
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
            return _settings.Profiles.FirstOrDefault(profile =>
                !string.IsNullOrWhiteSpace(profile.ProcessName) &&
                string.Equals(profile.ProcessName, name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException)
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

    private void StopCliLocked()
    {
        Process? cli = _cli;
        StreamWriter? input = _cliInput;
        int attached = _attachedProcessId;
        _cli = null;
        _cliInput = null;
        _attachedProcessId = 0;
        if (cli is null)
        {
            input?.Dispose();
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
        cli.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        lock (_gate)
        {
            StopCliLocked();
            _recent.Clear();
        }

        return ValueTask.CompletedTask;
    }
}
