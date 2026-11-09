namespace AsteriaScale.Services;

// The hover-translation log. It sits in the core because the pipeline and
// scaffold report failures from platform-neutral code. The file is
// CoreDataLayout.HoverLogFile: LocalApplicationData\AsteriaScale on Windows,
// the log folder the host configured elsewhere.
internal static class HoverDiagnostics
{
    internal static void LogError(Exception exception)
    {
        AppendDiagnostic(exception.ToString());
    }

    internal static void LogDiagnostic(string message)
    {
        AppendDiagnostic((message ?? string.Empty).Replace('\r', ' ').Replace('\n', ' '));
    }

    private static void AppendDiagnostic(string message) =>
        AppendDiagnostic(message, layout: null);

    // Null reads the process-wide layout; a test passes its own. Either way the
    // layout is read inside the try, so not even resolving it can disturb a hover.
    internal static void AppendDiagnostic(string message, CoreDataLayout? layout)
    {
        try
        {
            // An unresolved user root throws here and the message is dropped:
            // the log is never written relative to the working directory.
            string path = (layout ?? CoreDataLayout.Current).HoverLogFile;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            FileInfo log = new(path);
            if (log.Exists && log.Length > 512 * 1024)
            {
                File.Move(path, path + ".1", overwrite: true);
            }

            File.AppendAllText(path, $"[{DateTimeOffset.Now:O}] {message}\n\n");
        }
        catch
        {
            // Diagnostics must never interfere with hover translation.
        }
    }
}
