namespace AsteriaScale.RankerWorker;

internal enum RankerProvider
{
    Npu,
    Cpu
}

internal enum CpuRankerModel
{
    GlossBert,
    Deberta,
    Sandwich,
    Consec
}

internal sealed record WorkerOptions(
    RankerProvider Provider,
    string ModelDirectory,
    bool Smoke,
    CpuRankerModel CpuModel = CpuRankerModel.GlossBert)
{
    public static bool TryParse(
        IReadOnlyList<string> args,
        out WorkerOptions? options,
        out string? error)
    {
        var provider = RankerProvider.Npu;
        var modelDirectory = Path.Combine(AppContext.BaseDirectory, "Models");
        var smoke = false;
        var cpuModel = CpuRankerModel.GlossBert;
        var providerSeen = false;
        var modelDirectorySeen = false;
        var smokeSeen = false;
        var cpuModelSeen = false;

        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], "--smoke", StringComparison.OrdinalIgnoreCase))
            {
                if (smokeSeen)
                {
                    options = null;
                    error = "Duplicate argument '--smoke'.";
                    return false;
                }

                smokeSeen = true;
                smoke = true;
                continue;
            }

            if (string.Equals(args[i], "--cpu-model", StringComparison.OrdinalIgnoreCase))
            {
                if (cpuModelSeen)
                {
                    options = null;
                    error = "Duplicate argument '--cpu-model'.";
                    return false;
                }

                cpuModelSeen = true;
                if (++i >= args.Count)
                {
                    options = null;
                    error = "--cpu-model requires glossbert, deberta, sandwich, or consec.";
                    return false;
                }

                if (string.Equals(args[i], "glossbert", StringComparison.OrdinalIgnoreCase))
                {
                    cpuModel = CpuRankerModel.GlossBert;
                }
                else if (string.Equals(args[i], "deberta", StringComparison.OrdinalIgnoreCase))
                {
                    cpuModel = CpuRankerModel.Deberta;
                }
                else if (string.Equals(args[i], "sandwich", StringComparison.OrdinalIgnoreCase))
                {
                    cpuModel = CpuRankerModel.Sandwich;
                }
                else if (string.Equals(args[i], "consec", StringComparison.OrdinalIgnoreCase))
                {
                    cpuModel = CpuRankerModel.Consec;
                }
                else
                {
                    options = null;
                    error =
                        $"Unsupported CPU model '{args[i]}'. " +
                        "Expected glossbert, deberta, sandwich, or consec.";
                    return false;
                }

                continue;
            }

            if (string.Equals(args[i], "--provider", StringComparison.OrdinalIgnoreCase))
            {
                if (providerSeen)
                {
                    options = null;
                    error = "Duplicate argument '--provider'.";
                    return false;
                }

                providerSeen = true;
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
                if (modelDirectorySeen)
                {
                    options = null;
                    error = "Duplicate argument '--model-directory'.";
                    return false;
                }

                modelDirectorySeen = true;
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
                "--cpu-model glossbert|deberta|sandwich|consec, " +
                "--model-directory <path>, or --smoke.";
            return false;
        }

        if (provider == RankerProvider.Npu && cpuModel != CpuRankerModel.GlossBert)
        {
            options = null;
            error =
                $"The {cpuModel} ranker is CPU-only. Use --provider cpu with " +
                $"--cpu-model {cpuModel.ToString().ToLowerInvariant()}; " +
                "NPU remains pinned to GlossBERT.";
            return false;
        }

        options = new WorkerOptions(provider, modelDirectory, smoke, cpuModel);
        error = null;
        return true;
    }
}
