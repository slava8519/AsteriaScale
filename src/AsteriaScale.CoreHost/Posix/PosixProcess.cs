using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AsteriaScale.CoreHost.Posix;

/// <summary>
/// Process-group facts and cleanup. The app spawns CoreHost as the leader of
/// its own process group, and every helper CoreHost starts (the ranker
/// worker, llama-server) stays in it; the group is therefore exactly CoreHost
/// and what it owns, and emptying it on exit is how a helper that does not
/// watch its parent (llama-server) is never left running. CoreHost never calls
/// setpgid itself: that would break terminal job control while debugging.
/// </summary>
[SupportedOSPlatform("macos")]
internal static partial class PosixProcess
{
    private const int SigKill = 9;
    private const int SigBlock = 1;
    private const int Esrch = 3;

    // The signals a supervisor must be able to deliver or observe. They are
    // reported, not unblocked: a mask inherited from a libdispatch thread
    // (SIGTERM and SIGCHLD blocked) stays on every thread .NET creates later,
    // so only the spawner can fix it, and hello lets it refuse the helper.
    private static readonly (int Number, string Name)[] ReportedSignals =
    [
        (1, "SIGHUP"),
        (2, "SIGINT"),
        (15, "SIGTERM"),
        (20, "SIGCHLD"),
    ];

    public static int ProcessGroup => getpgrp();

    public static bool IsGroupLeader => getpgrp() == Environment.ProcessId;

    /// <summary>The reported signals blocked in the calling thread's mask.</summary>
    public static unsafe string[] ReadBlockedSignals()
    {
        uint read = 0;
        if (pthread_sigmask(SigBlock, null, &read) != 0)
        {
            throw new IOException("pthread_sigmask failed");
        }

        uint mask = read;
        return ReportedSignals
            .Where(signal => (mask & (1u << (signal.Number - 1))) != 0)
            .Select(signal => signal.Name)
            .ToArray();
    }

    /// <summary>
    /// SIGKILLs every process CoreHost owns: the other members of its group
    /// when it leads one, otherwise its descendants (a CoreHost started inside
    /// someone else's group, such as a shell pipeline, must not kill its
    /// neighbours). Returns how many were signalled.
    /// </summary>
    public static int KillOwnedProcesses()
    {
        int self = Environment.ProcessId;
        int[] targets = IsGroupLeader
            ? ListGroup(getpgrp()).Where(pid => pid != self).ToArray()
            : ListDescendants(self);
        int signalled = 0;
        foreach (int pid in targets)
        {
            if (pid > 1 && kill(pid, SigKill) == 0)
            {
                signalled++;
            }
        }

        return signalled;
    }

    /// <summary>
    /// Ends the process at once with the given code, without running managed
    /// shutdown or writing a crash report (an abort would put one in the
    /// owner's ~/Library/Logs/DiagnosticReports whatever HOME is).
    /// </summary>
    public static void ExitImmediately(int code) => _exit(code);

    private static int[] ListGroup(int pgid) => ListPids(buffer => proc_listpgrppids(pgid, buffer.pointer, buffer.bytes));

    private static int[] ListDescendants(int root)
    {
        List<int> found = [];
        Queue<int> pending = new([root]);
        while (pending.Count > 0 && found.Count < 4096)
        {
            int parent = pending.Dequeue();
            foreach (int child in ListPids(buffer => proc_listchildpids(parent, buffer.pointer, buffer.bytes)))
            {
                if (child > 1 && !found.Contains(child))
                {
                    found.Add(child);
                    pending.Enqueue(child);
                }
            }
        }

        return found.ToArray();
    }

    // Both libproc calls return the number of pids written. The buffer grows
    // until it is not full, so a group larger than the first guess is listed
    // whole.
    private static unsafe int[] ListPids(Func<(nint pointer, int bytes), int> list)
    {
        for (int capacity = 256; capacity <= 1 << 16; capacity *= 4)
        {
            int[] buffer = new int[capacity];
            int count;
            fixed (int* pointer = buffer)
            {
                count = list(((nint)pointer, capacity * sizeof(int)));
            }

            if (count < 0)
            {
                return Marshal.GetLastPInvokeError() == Esrch ? [] : throw new IOException("libproc listing failed");
            }

            if (count < capacity)
            {
                return buffer[..count].Where(pid => pid > 0).ToArray();
            }
        }

        throw new IOException("libproc listing did not fit");
    }

    [LibraryImport("libc")]
    private static partial int getpgrp();

    [LibraryImport("libc", SetLastError = true)]
    private static partial int kill(int pid, int signal);

    [LibraryImport("libc")]
    private static unsafe partial int pthread_sigmask(int how, uint* set, uint* oldSet);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int proc_listpgrppids(int pgid, nint buffer, int bufferSize);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int proc_listchildpids(int ppid, nint buffer, int bufferSize);

    [LibraryImport("libc")]
    private static partial void _exit(int status);
}
