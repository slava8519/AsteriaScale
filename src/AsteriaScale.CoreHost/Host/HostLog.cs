using System.Text;

namespace AsteriaScale.CoreHost.Host;

/// <summary>
/// CoreHost's diagnostics. stderr carries only these lines (stdout is the
/// protocol), one per message, each starting with "[corehost] " so the app's
/// log and the subset's checks can tell them from anything else, and capped
/// so one message cannot become a multi-megabyte line.
/// </summary>
internal static class HostLog
{
    internal const string Prefix = "[corehost] ";
    internal const int MaxLineBytes = 8 * 1024;

    private static readonly object Gate = new();
    private static readonly Stream Stderr = Console.OpenStandardError();

    public static void Write(string message)
    {
        string flat = message.Replace('\r', ' ').Replace('\n', ' ');
        byte[] bytes = Encoding.UTF8.GetBytes(Prefix + flat);
        int length = Math.Min(bytes.Length, MaxLineBytes - 1);
        // Never cut a multi-byte character in half: back up to its first byte.
        while (length < bytes.Length && length > 0 && (bytes[length] & 0xC0) == 0x80)
        {
            length--;
        }

        WriteLine(bytes.AsSpan(0, length));
    }

    // The test-only stderr flood: one raw, already prefixed line.
    internal static void WriteLine(ReadOnlySpan<byte> line)
    {
        lock (Gate)
        {
            try
            {
                Stderr.Write(line);
                Stderr.Write("\n"u8);
                Stderr.Flush();
            }
            catch (IOException)
            {
                // Nobody reads stderr any more; diagnostics must never take
                // the host down.
            }
        }
    }
}
