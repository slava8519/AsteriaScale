using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace AsteriaScale.CoreHost.Posix;

/// <summary>
/// Moves the protocol off fds 0 and 1 before any Core code runs. The protocol
/// streams are close-on-exec duplicates, so a child never inherits them; then
/// fd 0 becomes /dev/null and fd 1 a copy of stderr, so a child started
/// without redirecting stdin (llama-server today) and native code printing to
/// stdout can neither steal request bytes nor corrupt the response stream.
///
/// The streams are plain FileStreams, not Console streams: .NET's console
/// stream treats EPIPE as success, which would hide a peer that closed its
/// read end (exit 4). fcntl(F_DUPFD_CLOEXEC) is not used because fcntl is
/// variadic, and on Apple arm64 a variadic argument travels on the stack,
/// which a fixed P/Invoke signature gets wrong; ioctl(FIOCLEX) passes no
/// variadic argument at all. Nothing else runs yet, so no fork can see a
/// duplicate before it is close-on-exec.
/// </summary>
[SupportedOSPlatform("macos")]
internal static partial class PosixStdio
{
    private const ulong Fioclex = 0x20006601;

    public static (Stream Input, Stream Output) Rehome()
    {
        int input = CloseOnExecDuplicate(0, "stdin");
        int output = CloseOnExecDuplicate(1, "stdout");
        using (SafeFileHandle devNull = File.OpenHandle("/dev/null", FileMode.Open, FileAccess.ReadWrite))
        {
            // dup2 clears FD_CLOEXEC on the target, so children do inherit
            // /dev/null as their stdin.
            Check(dup2((int)devNull.DangerousGetHandle(), 0), "dup2(/dev/null, 0)");
        }

        Check(dup2(2, 1), "dup2(2, 1)");
        // Whatever still writes to Console.Out now reaches stderr.
        Console.SetOut(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        return (
            new FileStream(new SafeFileHandle(input, ownsHandle: true), FileAccess.Read, bufferSize: 0),
            new FileStream(new SafeFileHandle(output, ownsHandle: true), FileAccess.Write, bufferSize: 0));
    }

    private static int CloseOnExecDuplicate(int fd, string what)
    {
        int duplicate = dup(fd);
        Check(duplicate, $"dup({what})");
        Check(ioctl(duplicate, Fioclex), $"ioctl(FIOCLEX) on the {what} duplicate");
        return duplicate;
    }

    private static void Check(int result, string what)
    {
        if (result < 0)
        {
            throw new IOException($"{what} failed: errno {Marshal.GetLastPInvokeError()}");
        }
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int dup(int fd);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int dup2(int fd, int fd2);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int ioctl(int fd, ulong request);
}
