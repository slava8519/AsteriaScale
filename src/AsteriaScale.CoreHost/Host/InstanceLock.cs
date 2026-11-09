using Microsoft.Win32.SafeHandles;

namespace AsteriaScale.CoreHost.Host;

/// <summary>
/// One CoreHost per user-data folder. Two hosts on the same settings, caches
/// and history would overwrite each other's files, so the second one exits 7
/// before it reads or writes anything else there. The lock is the advisory
/// flock(2) .NET takes for FileShare.None on Unix: the kernel drops it when
/// the process dies, so a crashed host never leaves a stale lock, and it
/// follows the file, not the path, so a second host reaching the same folder
/// through a symlink is refused too. The file itself stays; deleting it would
/// race with the next host.
/// </summary>
internal sealed class InstanceLock : IDisposable
{
    internal const string RelativePath = "Runtime/corehost.lock";

    // .NET reports a held flock as IOException with errno EWOULDBLOCK (35 on
    // macOS) in HResult; anything else is a real I/O failure.
    private const int EWouldBlock = 35;

    private readonly SafeFileHandle _handle;

    private InstanceLock(SafeFileHandle handle) => _handle = handle;

    /// <summary>The lock, or null when another host holds it.</summary>
    public static InstanceLock? TryAcquire(string userData)
    {
        string path = Path.Combine(userData, "Runtime", "corehost.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            return new InstanceLock(File.OpenHandle(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None));
        }
        catch (IOException exception) when (exception.HResult == EWouldBlock)
        {
            return null;
        }
    }

    public void Dispose() => _handle.Dispose();
}
