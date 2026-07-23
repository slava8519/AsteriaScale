using Microsoft.Win32;
using AsteriaScale.Interop;
using AsteriaScale.Models;

namespace AsteriaScale.Services;

internal sealed record DpiCompatibilityState(
    bool IsEnabled,
    bool IsManaged,
    bool CanRestore,
    bool HasExternalConflict,
    DpiAwareness? RuntimeAwareness)
{
    public bool RuntimeIsDpiAware => RuntimeAwareness is DpiAwareness.SystemAware or DpiAwareness.PerMonitorAware;
}

internal sealed class DpiOverrideMutation
{
    private Action? _rollback;

    public DpiOverrideMutation(bool changed, Action? rollback = null)
    {
        Changed = changed;
        _rollback = rollback;
    }

    public bool Changed { get; }

    public void Rollback()
    {
        Action? rollback = Interlocked.Exchange(ref _rollback, null);
        rollback?.Invoke();
    }
}

internal interface ICompatibilityLayersStore
{
    string? Read(string processPath);
    void Write(string processPath, string? value);
}

internal sealed class RegistryCompatibilityLayersStore : ICompatibilityLayersStore
{
    private const string LayersKey = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";

    public string? Read(string processPath)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(LayersKey, false);
        return key?.GetValue(processPath, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    public void Write(string processPath, string? value)
    {
        if (value is null)
        {
            using RegistryKey? writableKey = Registry.CurrentUser.OpenSubKey(LayersKey, true);
            writableKey?.DeleteValue(processPath, false);
            return;
        }

        using RegistryKey createdKey = Registry.CurrentUser.CreateSubKey(LayersKey, true);
        createdKey.SetValue(processPath, value, RegistryValueKind.String);
    }
}

public sealed class DpiCompatibilityService
{
    private const string ApplicationDpiFlag = "HIGHDPIAWARE";
    private static readonly string[] ConflictingDpiFlags = ["DPIUNAWARE", "GDIDPISCALING"];
    private readonly ICompatibilityLayersStore _store;

    public DpiCompatibilityService()
        : this(new RegistryCompatibilityLayersStore())
    {
    }

    internal DpiCompatibilityService(ICompatibilityLayersStore store)
    {
        _store = store;
    }

    internal DpiCompatibilityState GetState(
        AppSettings settings,
        string processPath,
        nint windowHandle = default)
    {
        string path = NormalizeExecutablePath(processPath);
        string? current = _store.Read(path);
        ManagedDpiOverride? managed = FindManaged(settings, path);
        bool enabled = HasFlag(current, ApplicationDpiFlag);
        bool exactManagedValue = managed is not null &&
            string.Equals(current, managed.AppliedCompatibilityLayers, StringComparison.Ordinal);

        return new DpiCompatibilityState(
            enabled,
            managed is not null,
            exactManagedValue,
            managed is not null && enabled && !exactManagedValue,
            NativeMethods.TryGetWindowDpiAwareness(windowHandle));
    }

    internal DpiOverrideMutation Enable(AppSettings settings, string processPath)
    {
        string path = NormalizeExecutablePath(processPath);
        string? original = _store.Read(path);
        ManagedDpiOverride? previousRecord = FindManaged(settings, path);
        int previousIndex = previousRecord is null ? -1 : settings.DpiOverrides.IndexOf(previousRecord);

        if (HasFlag(original, ApplicationDpiFlag))
        {
            return new DpiOverrideMutation(false);
        }

        string applied = BuildApplicationOverride(original);
        ManagedDpiOverride record = new()
        {
            ProcessPath = path,
            OriginalCompatibilityLayers = original,
            AppliedCompatibilityLayers = applied
        };

        _store.Write(path, applied);
        RemoveManaged(settings, path);
        settings.DpiOverrides.Add(record);

        return new DpiOverrideMutation(true, () =>
        {
            _store.Write(path, original);
            RemoveManaged(settings, path);
            if (previousRecord is not null)
            {
                settings.DpiOverrides.Insert(Math.Min(previousIndex, settings.DpiOverrides.Count), previousRecord);
            }
        });
    }

    internal DpiOverrideMutation Disable(AppSettings settings, string processPath)
    {
        string path = NormalizeExecutablePath(processPath);
        ManagedDpiOverride? managed = FindManaged(settings, path);
        if (managed is null)
        {
            return new DpiOverrideMutation(false);
        }

        int previousIndex = settings.DpiOverrides.IndexOf(managed);
        string? current = _store.Read(path);
        if (HasFlag(current, ApplicationDpiFlag) &&
            !string.Equals(current, managed.AppliedCompatibilityLayers, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Параметры совместимости игры изменены вне Asteria. Откройте свойства EXE и снимите DPI override вручную, чтобы не потерять другие флаги.");
        }

        if (string.Equals(current, managed.AppliedCompatibilityLayers, StringComparison.Ordinal))
        {
            _store.Write(path, managed.OriginalCompatibilityLayers);
        }

        RemoveManaged(settings, path);
        return new DpiOverrideMutation(true, () =>
        {
            _store.Write(path, current);
            RemoveManaged(settings, path);
            settings.DpiOverrides.Insert(Math.Min(previousIndex, settings.DpiOverrides.Count), managed);
        });
    }

    internal static bool HasApplicationOverride(string? value) => HasFlag(value, ApplicationDpiFlag);

    internal static string BuildApplicationOverride(string? value)
    {
        List<string> tokens = Tokenize(value)
            .Where(token => !string.Equals(token, "~", StringComparison.Ordinal) &&
                            !string.Equals(token, ApplicationDpiFlag, StringComparison.OrdinalIgnoreCase) &&
                            !ConflictingDpiFlags.Contains(token, StringComparer.OrdinalIgnoreCase))
            .ToList();
        tokens.Insert(0, "~");
        tokens.Add(ApplicationDpiFlag);
        return string.Join(' ', tokens);
    }

    private static bool HasFlag(string? value, string flag) =>
        Tokenize(value).Contains(flag, StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> Tokenize(string? value) =>
        (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string NormalizeExecutablePath(string processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath) || !Path.IsPathFullyQualified(processPath))
        {
            throw new InvalidOperationException("Не удалось определить полный путь к EXE игры.");
        }

        string path = Path.GetFullPath(processPath);
        if (!string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        {
            throw new InvalidOperationException("EXE выбранной игры больше не найден.");
        }

        return path;
    }

    private static ManagedDpiOverride? FindManaged(AppSettings settings, string processPath) =>
        settings.DpiOverrides.FirstOrDefault(item =>
            string.Equals(item.ProcessPath, processPath, StringComparison.OrdinalIgnoreCase));

    private static void RemoveManaged(AppSettings settings, string processPath) =>
        settings.DpiOverrides.RemoveAll(item =>
            string.Equals(item.ProcessPath, processPath, StringComparison.OrdinalIgnoreCase));
}
