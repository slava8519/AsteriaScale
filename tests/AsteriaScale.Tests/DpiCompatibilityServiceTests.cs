using AsteriaScale.Models;
using AsteriaScale.Services;

internal static class DpiCompatibilityServiceTests
{
    public static Task RunAsync(string tempRoot)
    {
        string directory = Path.Combine(tempRoot, "dpi-compatibility");
        Directory.CreateDirectory(directory);
        string executable = Path.Combine(directory, "Novel.exe");
        File.WriteAllBytes(executable, []);

        MemoryCompatibilityLayersStore store = new();
        DpiCompatibilityService service = new(store);
        AppSettings settings = new();

        store.Write(executable, "~ WIN7RTM DPIUNAWARE GDIDPISCALING");
        DpiOverrideMutation enable = service.Enable(settings, executable);
        Check(enable.Changed, "The first enable operation must change compatibility state.");
        Check(store.Read(executable) == "~ WIN7RTM HIGHDPIAWARE",
            "Application override must preserve unrelated flags and remove conflicting DPI flags.");
        Check(settings.DpiOverrides is [{ OriginalCompatibilityLayers: "~ WIN7RTM DPIUNAWARE GDIDPISCALING" }],
            "The exact previous compatibility value must be retained for rollback.");

        DpiCompatibilityState managed = service.GetState(settings, executable);
        Check(managed.IsEnabled && managed.IsManaged && managed.CanRestore && !managed.HasExternalConflict,
            "Asteria-owned exact registry state must be restorable.");

        enable.Rollback();
        Check(store.Read(executable) == "~ WIN7RTM DPIUNAWARE GDIDPISCALING" && settings.DpiOverrides.Count == 0,
            "Failed settings persistence must be able to restore registry and in-memory state.");

        service.Enable(settings, executable);
        DpiOverrideMutation disable = service.Disable(settings, executable);
        Check(disable.Changed &&
              store.Read(executable) == "~ WIN7RTM DPIUNAWARE GDIDPISCALING" &&
              settings.DpiOverrides.Count == 0,
            "Disabling an owned override must restore the exact prior value.");
        disable.Rollback();
        Check(store.Read(executable) == "~ WIN7RTM HIGHDPIAWARE" && settings.DpiOverrides.Count == 1,
            "Disable rollback must restore the managed override.");

        settings.DpiOverrides.Clear();
        store.Write(executable, "~ HIGHDPIAWARE");
        DpiCompatibilityState external = service.GetState(settings, executable);
        Check(external.IsEnabled && !external.IsManaged && !external.CanRestore,
            "A manual Windows compatibility setting must be detected without claiming ownership.");
        Check(!service.Enable(settings, executable).Changed && settings.DpiOverrides.Count == 0,
            "A manual override must never be overwritten or adopted silently.");

        store.Write(executable, null);
        service.Enable(settings, executable);
        store.Write(executable, "~ HIGHDPIAWARE RUNASADMIN");
        bool conflictRejected = false;
        try
        {
            service.Disable(settings, executable);
        }
        catch (InvalidOperationException)
        {
            conflictRejected = true;
        }

        Check(conflictRejected && store.Read(executable) == "~ HIGHDPIAWARE RUNASADMIN",
            "Externally changed compatibility flags must not be destroyed during restore.");

        Check(DpiCompatibilityService.BuildApplicationOverride("~ WIN8RTM") == "~ WIN8RTM HIGHDPIAWARE" &&
              DpiCompatibilityService.HasApplicationOverride("~ highdpiaware"),
            "Compatibility flag normalization must be deterministic and case-insensitive.");
        return Task.CompletedTask;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class MemoryCompatibilityLayersStore : ICompatibilityLayersStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public string? Read(string processPath) =>
            _values.TryGetValue(processPath, out string? value) ? value : null;

        public void Write(string processPath, string? value)
        {
            if (value is null)
            {
                _values.Remove(processPath);
            }
            else
            {
                _values[processPath] = value;
            }
        }
    }
}
