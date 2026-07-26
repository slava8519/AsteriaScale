using System.ComponentModel;
using System.Diagnostics;

internal static class PythonTestProcess
{
    internal static Process Start(ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo) ??
                throw new InvalidOperationException("Python returned no process handle.");
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException(
                "The full smoke suite requires Python 3.11+ available as 'python' in PATH; " +
                "see docs/DEVELOPMENT.md.",
                exception);
        }
    }
}
