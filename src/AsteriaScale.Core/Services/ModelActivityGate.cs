namespace AsteriaScale.Services;

/// <summary>
/// Serializes foreground translation work with destructive model maintenance.
/// The maintenance lease is acquired only after the active request drains and
/// prevents a new request from starting until every model owner is stable.
/// </summary>
internal sealed class ModelActivityGate
{
    private readonly object _sync = new();
    private bool _processing;
    private bool _maintenance;
    private TaskCompletionSource _idle = CompletedSource();

    public bool TryBeginProcessing()
    {
        lock (_sync)
        {
            if (_processing || _maintenance)
            {
                return false;
            }

            _processing = true;
            _idle = PendingSource();
            return true;
        }
    }

    public void EndProcessing()
    {
        TaskCompletionSource completion;
        lock (_sync)
        {
            if (!_processing)
            {
                return;
            }

            _processing = false;
            completion = _idle;
        }

        completion.TrySetResult();
    }

    public async Task<IDisposable> BeginMaintenanceWhenIdleAsync(
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task idle;
            lock (_sync)
            {
                if (!_processing && !_maintenance)
                {
                    _maintenance = true;
                    _idle = PendingSource();
                    return new MaintenanceLease(this);
                }

                idle = _idle.Task;
            }

            await idle.WaitAsync(cancellationToken);
        }
    }

    public Task WaitForIdleAsync()
    {
        lock (_sync)
        {
            return _processing || _maintenance
                ? _idle.Task
                : Task.CompletedTask;
        }
    }

    private void EndMaintenance()
    {
        TaskCompletionSource completion;
        lock (_sync)
        {
            if (!_maintenance)
            {
                return;
            }

            _maintenance = false;
            completion = _idle;
        }

        completion.TrySetResult();
    }

    private static TaskCompletionSource PendingSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource CompletedSource()
    {
        TaskCompletionSource source = PendingSource();
        source.SetResult();
        return source;
    }

    private sealed class MaintenanceLease(ModelActivityGate owner) : IDisposable
    {
        private ModelActivityGate? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.EndMaintenance();
    }
}
