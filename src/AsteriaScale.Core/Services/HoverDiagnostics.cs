namespace AsteriaScale.Services;

// The hover-translation log. It sits in the core because the pipeline and
// scaffold report failures from platform-neutral code; on macOS the same
// LocalApplicationData call resolves to ~/Library/Application Support.
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

    private static void AppendDiagnostic(string message)
    {
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AsteriaScale");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "hover-translation.log");
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
