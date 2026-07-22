using AsteriaScale.Services;

internal static class ModelActivityGateTests
{
    public static async Task RunAsync()
    {
        ModelActivityGate gate = new();
        Check(gate.TryBeginProcessing(), "The first translation request did not acquire the gate.");

        Task<IDisposable> maintenanceTask = gate.BeginMaintenanceWhenIdleAsync();
        await Task.Yield();
        Check(!maintenanceTask.IsCompleted,
            "Model maintenance started before the active translation drained.");

        gate.EndProcessing();
        IDisposable maintenance = await maintenanceTask.WaitAsync(TimeSpan.FromSeconds(2));
        Check(!gate.TryBeginProcessing(),
            "A new translation started while destructive model maintenance held the gate.");
        Task idleTask = gate.WaitForIdleAsync();
        Check(!idleTask.IsCompleted,
            "The lifecycle gate reported idle while model maintenance was active.");

        maintenance.Dispose();
        await idleTask.WaitAsync(TimeSpan.FromSeconds(2));
        Check(gate.TryBeginProcessing(),
            "Translation did not resume after model maintenance released the gate.");
        gate.EndProcessing();

        IDisposable firstMaintenance = await gate.BeginMaintenanceWhenIdleAsync();
        Task<IDisposable> secondMaintenanceTask = gate.BeginMaintenanceWhenIdleAsync();
        await Task.Yield();
        Check(!secondMaintenanceTask.IsCompleted,
            "Two independent unload owners entered maintenance concurrently.");
        firstMaintenance.Dispose();
        using IDisposable secondMaintenance =
            await secondMaintenanceTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
