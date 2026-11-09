using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

/// <summary>How a CoreHost process ended: an exit code or the signal that killed it.</summary>
internal readonly record struct CoreHostExit(bool Signaled, int Code)
{
    public static CoreHostExit Exited(int code) => new(false, code);

    public override string ToString() => Signaled ? $"signal {Code}" : $"exit {Code}";
}

/// <summary>
/// Starts the CoreHost apphost the way the app's supervisor does, which
/// System.Diagnostics.Process cannot: posix_spawn with SETPGROUP (CoreHost
/// leads its own process group), CLOEXEC_DEFAULT (it inherits exactly fds 0,
/// 1 and 2), SETSIGMASK with the given mask (empty by default: a mask
/// inherited from a libdispatch or .NET thread would otherwise reach
/// CoreHost) and SETSIGDEF for SIGPIPE (CoreHost must handle a closed stdout
/// itself). The exit is observed with waitid(WNOWAIT), so the zombie keeps
/// its pid and process group until <see cref="Reap"/> has killed the group:
/// a pgid can be reused only once nothing holds it.
/// </summary>
internal sealed class CoreHostProcess : IDisposable
{
    internal const int SigKill = 9;
    internal const int SigPipe = 13;
    internal const int SigTerm = 15;

    private const short PosixSpawnSetPgroup = 0x0002;
    private const short PosixSpawnSetSigDef = 0x0004;
    private const short PosixSpawnSetSigMask = 0x0008;
    private const short PosixSpawnCloexecDefault = 0x4000;
    private const int PPid = 1;
    private const int WExited = 0x04;
    private const int WNoWait = 0x20;
    private const int ClaExited = 1;
    private const int EIntr = 4;
    private const int EPerm = 1;
    private const ulong Fioclex = 0x20006601;

    // posix_spawn itself closes everything it does not map, but a pipe end
    // created here must also be FD_CLOEXEC before any other thread of the
    // test process (Process.Start for lsof or the worker) can fork.
    private static readonly object PipeGate = new();

    private readonly object _reapGate = new();
    private bool _reaped;

    private CoreHostProcess(int pid, int stdin, int stdout, int stderr)
    {
        Pid = pid;
        Stdin = new FileStream(new SafeFileHandle(stdin, ownsHandle: true), FileAccess.Write, 1);
        Stdout = new FileStream(new SafeFileHandle(stdout, ownsHandle: true), FileAccess.Read, 1);
        Stderr = new FileStream(new SafeFileHandle(stderr, ownsHandle: true), FileAccess.Read, 1);
        Exited = Task.Factory.StartNew(WaitForExit, TaskCreationOptions.LongRunning);
    }

    public int Pid { get; }

    // SETPGROUP with 0 makes the child the leader of a group named after it.
    public int Pgid => Pid;

    public Stream Stdin { get; }

    public Stream Stdout { get; }

    public Stream Stderr { get; }

    /// <summary>Completes when the process has ended; it stays a zombie until <see cref="Reap"/>.</summary>
    public Task<CoreHostExit> Exited { get; }

    public static CoreHostProcess Start(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        string workingDirectory,
        uint blockedSignals = 0)
    {
        int[] input = new int[2], output = new int[2], error = new int[2];
        lock (PipeGate)
        {
            CreatePipe(input);
            CreatePipe(output);
            CreatePipe(error);
        }

        IntPtr actions = IntPtr.Zero, attributes = IntPtr.Zero;
        List<IntPtr> strings = [];
        int pid;
        int result;
        try
        {
            Check(posix_spawn_file_actions_init(ref actions), "posix_spawn_file_actions_init");
            Check(posix_spawnattr_init(ref attributes), "posix_spawnattr_init");
            Check(posix_spawn_file_actions_adddup2(ref actions, input[0], 0), "adddup2 stdin");
            Check(posix_spawn_file_actions_adddup2(ref actions, output[1], 1), "adddup2 stdout");
            Check(posix_spawn_file_actions_adddup2(ref actions, error[1], 2), "adddup2 stderr");
            Check(posix_spawn_file_actions_addchdir_np(ref actions, workingDirectory), "addchdir");
            Check(posix_spawnattr_setflags(
                ref attributes,
                (short)(PosixSpawnSetPgroup | PosixSpawnSetSigMask | PosixSpawnSetSigDef | PosixSpawnCloexecDefault)),
                "setflags");
            Check(posix_spawnattr_setpgroup(ref attributes, 0), "setpgroup");
            uint mask = blockedSignals;
            Check(posix_spawnattr_setsigmask(ref attributes, ref mask), "setsigmask");
            uint defaults = 1u << (SigPipe - 1);
            Check(posix_spawnattr_setsigdefault(ref attributes, ref defaults), "setsigdefault");

            IntPtr[] argv = new IntPtr[arguments.Count + 2];
            argv[0] = Utf8(executable, strings);
            for (int index = 0; index < arguments.Count; index++)
            {
                argv[index + 1] = Utf8(arguments[index], strings);
            }

            IntPtr[] envp = new IntPtr[environment.Count + 1];
            int slot = 0;
            foreach ((string key, string value) in environment)
            {
                envp[slot++] = Utf8(key + "=" + value, strings);
            }

            result = posix_spawn(out pid, executable, ref actions, ref attributes, argv, envp);
        }
        finally
        {
            foreach (IntPtr pointer in strings)
            {
                Marshal.FreeCoTaskMem(pointer);
            }

            if (actions != IntPtr.Zero)
            {
                posix_spawn_file_actions_destroy(ref actions);
            }

            if (attributes != IntPtr.Zero)
            {
                posix_spawnattr_destroy(ref attributes);
            }

            close(input[0]);
            close(output[1]);
            close(error[1]);
        }

        if (result != 0)
        {
            close(input[1]);
            close(output[0]);
            close(error[0]);
            throw new IOException($"posix_spawn of {executable} failed: errno {result}");
        }

        return new CoreHostProcess(pid, input[1], output[0], error[0]);
    }

    /// <summary>The exit, or a TimeoutException that says what was waited for.</summary>
    public async Task<CoreHostExit> WaitExitAsync(TimeSpan bound, string what)
    {
        try
        {
            return await Exited.WaitAsync(bound).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"CoreHost {Pid} did not exit within {bound.TotalMilliseconds:0} ms after {what}.");
        }
    }

    public void Signal(int signal) => _ = kill(Pid, signal);

    public void KillGroup() => _ = kill(-Pgid, SigKill);

    /// <summary>Whether any process, a zombie included, is still in the group.</summary>
    public bool GroupAlive => IsGroupAlive(Pgid);

    public static bool IsGroupAlive(int pgid) => kill(-pgid, 0) == 0 || Marshal.GetLastPInvokeError() == EPerm;

    public static bool IsAlive(int pid) => kill(pid, 0) == 0 || Marshal.GetLastPInvokeError() == EPerm;

    public static int ProcessGroupOf(int pid) => getpgid(pid);

    /// <summary>
    /// After the exit: SIGKILL the whole group (orphans included), then reap
    /// the zombie, in that order, so the group cannot have been reused when
    /// it is signalled.
    /// </summary>
    public void Reap()
    {
        lock (_reapGate)
        {
            if (_reaped)
            {
                return;
            }

            if (!Exited.IsCompleted)
            {
                throw new InvalidOperationException("Reap is called only after the exit was observed.");
            }

            KillGroup();
            while (waitpid(Pid, out _, 0) < 0 && Marshal.GetLastPInvokeError() == EIntr)
            {
            }

            _reaped = true;
        }
    }

    /// <summary>Reaps and waits until nothing is left in the group.</summary>
    public async Task<bool> ReapAndWaitGroupEmptyAsync(TimeSpan bound)
    {
        await Exited.ConfigureAwait(false);
        Reap();
        DateTime deadline = DateTime.UtcNow + bound;
        while (GroupAlive)
        {
            if (DateTime.UtcNow > deadline)
            {
                return false;
            }

            await Task.Delay(5).ConfigureAwait(false);
        }

        return true;
    }

    public void Dispose()
    {
        try
        {
            Stdin.Dispose();
        }
        catch (IOException)
        {
        }

        if (!Exited.Wait(TimeSpan.FromSeconds(5)))
        {
            KillGroup();
            Exited.Wait(TimeSpan.FromSeconds(5));
        }

        if (Exited.IsCompleted)
        {
            Reap();
        }

        Stdout.Dispose();
        Stderr.Dispose();
    }

    private CoreHostExit WaitForExit()
    {
        byte[] info = new byte[128];
        while (waitid(PPid, (uint)Pid, info, WExited | WNoWait) < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            if (errno != EIntr)
            {
                throw new IOException($"waitid({Pid}) failed: errno {errno}");
            }
        }

        // siginfo_t: si_code at offset 8, si_status at offset 20.
        int code = BitConverter.ToInt32(info, 8);
        int status = BitConverter.ToInt32(info, 20);
        return code == ClaExited ? CoreHostExit.Exited(status) : new CoreHostExit(true, status);
    }

    private static void CreatePipe(int[] fds)
    {
        if (pipe(fds) != 0)
        {
            throw new IOException($"pipe failed: errno {Marshal.GetLastPInvokeError()}");
        }

        foreach (int fd in fds)
        {
            if (ioctl(fd, Fioclex) != 0)
            {
                throw new IOException($"FIOCLEX failed: errno {Marshal.GetLastPInvokeError()}");
            }
        }
    }

    private static IntPtr Utf8(string value, List<IntPtr> strings)
    {
        IntPtr pointer = Marshal.StringToCoTaskMemUTF8(value);
        strings.Add(pointer);
        return pointer;
    }

    private static void Check(int result, string what)
    {
        if (result != 0)
        {
            throw new IOException($"{what} failed: {result}");
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int pipe(int[] fds);

    // ioctl is variadic; FIOCLEX passes nothing beyond the two fixed
    // arguments, which travel in registers like any non-variadic call.
    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, ulong request);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    [DllImport("libc", SetLastError = true)]
    private static extern int getpgid(int pid);

    [DllImport("libc", SetLastError = true)]
    private static extern int waitpid(int pid, out int status, int options);

    [DllImport("libc", SetLastError = true)]
    private static extern int waitid(int idType, uint id, byte[] info, int options);

    [DllImport("libc")]
    private static extern int posix_spawn_file_actions_init(ref IntPtr actions);

    [DllImport("libc")]
    private static extern int posix_spawn_file_actions_destroy(ref IntPtr actions);

    [DllImport("libc")]
    private static extern int posix_spawn_file_actions_adddup2(ref IntPtr actions, int fd, int newFd);

    [DllImport("libc")]
    private static extern int posix_spawn_file_actions_addchdir_np(
        ref IntPtr actions,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport("libc")]
    private static extern int posix_spawnattr_init(ref IntPtr attributes);

    [DllImport("libc")]
    private static extern int posix_spawnattr_destroy(ref IntPtr attributes);

    [DllImport("libc")]
    private static extern int posix_spawnattr_setflags(ref IntPtr attributes, short flags);

    [DllImport("libc")]
    private static extern int posix_spawnattr_setpgroup(ref IntPtr attributes, int pgroup);

    [DllImport("libc")]
    private static extern int posix_spawnattr_setsigmask(ref IntPtr attributes, ref uint mask);

    [DllImport("libc")]
    private static extern int posix_spawnattr_setsigdefault(ref IntPtr attributes, ref uint mask);

    [DllImport("libc")]
    private static extern int posix_spawn(
        out int pid,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        ref IntPtr actions,
        ref IntPtr attributes,
        IntPtr[] argv,
        IntPtr[] envp);
}
