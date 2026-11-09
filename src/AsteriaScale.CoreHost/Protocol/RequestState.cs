using System.Diagnostics;

namespace AsteriaScale.CoreHost.Protocol;

internal enum CancelMode
{
    // The terminal ("cancelled") is written at once; the work drains
    // privately and its later events are dropped, as the ranker client drains
    // a cancelled worker request (rule 6).
    Immediate,
    // The token is signalled; the request ends with its own terminal.
    Cooperative,
    // Side effects must not be misreported: the request runs to completion.
    NotCancellable,
}

internal enum Lane
{
    // Answered on the reader thread (ping through the pool) and never counted
    // as outstanding, so a client at the busy limit can still ping, cancel and
    // shut down.
    Control,
    // One hover at a time; a newer hover supersedes the current one.
    Hover,
    // Thread-pool tasks; their ordering is Core's business.
    Work,
}

/// <summary>One admitted request from admission to its single terminal.</summary>
internal sealed class RequestState(long id, string command, Lane lane, CancelMode cancelMode)
{
    // Guards Terminal: the writer checks it under this lock, so "no event after
    // the terminal" holds for every handler without each one checking.
    internal readonly object Gate = new();
    internal bool Terminal;

    public long Id { get; } = id;

    public string Command { get; } = command;

    public Lane Lane { get; } = lane;

    public CancelMode CancelMode { get; } = cancelMode;

    public CancellationTokenSource Cancellation { get; } = new();

    public long AdmittedTimestamp { get; } = Stopwatch.GetTimestamp();

    public bool IsTerminal
    {
        get
        {
            lock (Gate)
            {
                return Terminal;
            }
        }
    }

    public void Cancel()
    {
        try
        {
            Cancellation.Cancel();
        }
        catch (AggregateException)
        {
            // A registration callback failed; the request is cancelled anyway.
        }
    }
}
