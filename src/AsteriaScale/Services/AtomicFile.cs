using System.IO;

namespace AsteriaScale.Services;

/// <summary>
/// Atomic file replacement that survives Windows file-system interference.
///
/// Antivirus scanners and the search indexer briefly open a freshly created
/// file, which makes <see cref="File.Move(string, string, bool)"/> fail with
/// <see cref="UnauthorizedAccessException"/> even though the caller holds no
/// handle. A short bounded retry turns that transient collision into a
/// successful replace instead of a lost save.
/// </summary>
internal static class AtomicFile
{
    private const int MaximumAttempts = 5;

    public static void Replace(string temporaryPath, string destinationPath)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temporaryPath, destinationPath, overwrite: true);
                return;
            }
            catch (Exception exception) when (
                attempt < MaximumAttempts &&
                exception is UnauthorizedAccessException or IOException)
            {
                Thread.Sleep(20 * attempt);
            }
        }
    }
}
