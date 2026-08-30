/// <summary>
/// Guards the smoke suite against silently ignored options. A gated mode that
/// is removed from the code but survives in a document used to report a green
/// run that never happened, so an unrecognized option must fail loudly instead
/// of falling through to the default smoke set.
/// </summary>
internal static class TestCommandLine
{
    /// <summary>
    /// Every option the suite itself parses. Options forwarded to the ranker
    /// worker, llama-server or the Python tools are deliberately absent: they
    /// never reach this argument vector.
    /// </summary>
    internal static readonly IReadOnlySet<string> KnownOptions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "--benchmark-only",
            "--dump",
            "--live",
            "--max-cases",
            "--ocr-image",
            "--output",
            "--phrase-backend",
            "--phrase-quant",
            "--profile",
            "--ranker-benchmark",
            "--ranker-cpu-model",
            "--ranker-report",
            "--ranker-sentence-benchmark",
            "--register-benchmark",
            "--register-report",
            "--screen-ocr-image",
            "--sentence-report",
            "--shard",
            "--system-id",
            "--translation-output",
            "--translation-report",
            "--translation-suite"
        };

    /// <summary>
    /// Returns the first option the suite cannot act on, or null when every
    /// option is known. Positional values are skipped by construction: only
    /// tokens starting with "--" are treated as options.
    /// </summary>
    internal static string? FindUnknownOption(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        foreach (string argument in arguments)
        {
            if (argument is null ||
                !argument.StartsWith("--", StringComparison.Ordinal) ||
                argument.Length == 2)
            {
                // "--" is the standard end-of-options separator and carries no
                // meaning of its own here.
                continue;
            }

            if (!KnownOptions.Contains(argument))
            {
                return argument;
            }
        }

        return null;
    }

    internal static string DescribeUnknownOption(string option) =>
        $"Unknown option {option}. Known options: " +
        string.Join(", ", KnownOptions.OrderBy(value => value, StringComparer.Ordinal)) +
        ". A removed gated mode must fail here rather than report a green smoke run.";
}
