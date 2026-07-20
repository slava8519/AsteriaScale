namespace AsteriaScale.RankerWorker;

internal enum RankerProvider
{
    Npu,
    Cpu
}

internal sealed record WorkerOptions(
    RankerProvider Provider,
    string ModelDirectory,
    bool Smoke)
{
    public static bool TryParse(
        IReadOnlyList<string> args,
        out WorkerOptions? options,
        out string? error)
    {
        var provider = RankerProvider.Npu;
        var modelDirectory = Path.Combine(AppContext.BaseDirectory, "Models");
        var smoke = false;

        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], "--smoke", StringComparison.OrdinalIgnoreCase))
            {
                smoke = true;
                continue;
            }

            if (string.Equals(args[i], "--provider", StringComparison.OrdinalIgnoreCase))
            {
                if (++i >= args.Count)
                {
                    options = null;
                    error = "--provider requires npu or cpu.";
                    return false;
                }

                if (string.Equals(args[i], "npu", StringComparison.OrdinalIgnoreCase))
                {
                    provider = RankerProvider.Npu;
                }
                else if (string.Equals(args[i], "cpu", StringComparison.OrdinalIgnoreCase))
                {
                    provider = RankerProvider.Cpu;
                }
                else
                {
                    options = null;
                    error = $"Unsupported provider '{args[i]}'. Expected npu or cpu.";
                    return false;
                }

                continue;
            }

            if (string.Equals(args[i], "--model-directory", StringComparison.OrdinalIgnoreCase))
            {
                if (++i >= args.Count || string.IsNullOrWhiteSpace(args[i]))
                {
                    options = null;
                    error = "--model-directory requires a path.";
                    return false;
                }

                try
                {
                    modelDirectory = Path.GetFullPath(args[i]);
                }
                catch (Exception ex) when (
                    ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    options = null;
                    error = $"Invalid --model-directory path: {ex.Message}";
                    return false;
                }

                continue;
            }

            options = null;
            error =
                $"Unknown argument '{args[i]}'. Expected --provider npu|cpu, " +
                "--model-directory <path>, or --smoke.";
            return false;
        }

        options = new WorkerOptions(provider, modelDirectory, smoke);
        error = null;
        return true;
    }
}
