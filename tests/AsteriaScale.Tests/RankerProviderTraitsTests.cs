using System.Diagnostics;
using AsteriaScale.RankerProtocol;
using AsteriaScale.RankerWorker;
using AsteriaScale.Services;

// Pins the provider table that the host and the ranker worker now share. A
// silent change here alters gloss rendering, the bi-encoder pipeline or the
// identity a worker must report, which reaches cached verdicts (AGENTS.md
// rules 2 and 3); a new provider must be declared in full on both sides.
internal static class RankerProviderTraitsTests
{
    public static Task RunAsync()
    {
        VerifyPinnedValues();
        VerifyHostResolvesEveryProvider();
        VerifyWorkerResolvesEveryProvider();
        VerifyHostArgumentsReachTheSameTraitsInTheWorker();
        return Task.CompletedTask;
    }

    private static void VerifyPinnedValues()
    {
        Check(
            RankerProviderTraits.All.Count == 2 &&
            RankerProviderTraits.All.Select(traits => traits.WireName)
                .Distinct(StringComparer.Ordinal).Count() == 2,
            "Provider wire names must be unique.");

        RankerProviderTraits cpu = RankerProviderTraits.Cpu;
        Check(
            cpu.WireName == "cpu" &&
            cpu.ExecutionProvider == "CPUExecutionProvider" &&
            cpu.Device == "CPU" &&
            cpu.EnrichedGlosses &&
            cpu.BiEncoderPrefilter &&
            cpu.AlternativeModels,
            "CPU provider traits drifted from the pinned contract.");

        RankerProviderTraits npu = RankerProviderTraits.Npu;
        Check(
            npu.WireName == "npu" &&
            npu.ExecutionProvider == "VitisAIExecutionProvider" &&
            npu.Device == "NPU" &&
            !npu.EnrichedGlosses &&
            !npu.BiEncoderPrefilter &&
            !npu.AlternativeModels,
            "NPU provider traits drifted from the pinned contract.");

        Check(
            GlossBertDictionarySelectorService.CpuExecutionProvider == cpu.ExecutionProvider &&
            GlossBertDictionarySelectorService.NpuExecutionProvider == npu.ExecutionProvider,
            "The selector's provider names must come from the shared table.");

        Check(
            RankerProviderTraits.TryFromWireName("NPU", out RankerProviderTraits? parsed) &&
            ReferenceEquals(parsed, npu) &&
            !RankerProviderTraits.TryFromWireName("coreml", out _) &&
            !RankerProviderTraits.TryFromWireName(null, out _),
            "Wire-name lookup must accept only declared providers, case-insensitively.");
    }

    private static void VerifyHostResolvesEveryProvider()
    {
        Check(
            ReferenceEquals(GlossBertRankerProvider.Cpu.Traits(), RankerProviderTraits.Cpu) &&
            ReferenceEquals(GlossBertRankerProvider.Npu.Traits(), RankerProviderTraits.Npu),
            "The host maps a provider to the wrong traits.");

        foreach (GlossBertRankerProvider provider in Enum.GetValues<GlossBertRankerProvider>())
        {
            Check(
                RankerProviderTraits.All.Contains(provider.Traits()),
                $"Host provider {provider} has no traits in the shared table.");
        }

        ExpectOutOfRange(
            () => ((GlossBertRankerProvider)99).Traits(),
            "An undeclared host provider must fail loudly, not borrow CPU traits.");
        ExpectOutOfRange(
            () => GlossBertDictionarySelectorService.GetModelCacheId((GlossBertRankerProvider)99),
            "An undeclared provider must not inherit the CPU cache identity.");
        ExpectOutOfRange(
            () => GlossBertDictionarySelectorService.GetEngineLabel((GlossBertRankerProvider)99),
            "An undeclared provider must not inherit the CPU engine label.");
    }

    private static void VerifyWorkerResolvesEveryProvider()
    {
        foreach (RankerProvider provider in Enum.GetValues<RankerProvider>())
        {
            Check(
                RankerProviderTraits.All.Contains(provider.Traits()),
                $"Worker provider {provider} has no traits in the shared table.");
        }

        ExpectOutOfRange(
            () => ((RankerProvider)99).Traits(),
            "An undeclared worker provider must fail loudly.");
    }

    private static void VerifyHostArgumentsReachTheSameTraitsInTheWorker()
    {
        foreach (GlossBertRankerProvider provider in Enum.GetValues<GlossBertRankerProvider>())
        {
            ProcessStartInfo startInfo = new();
            GlossBertDictionarySelectorService.AddWorkerArguments(
                startInfo,
                provider,
                RankerCpuModelProfile.GlossBert,
                Path.GetTempPath());
            Check(
                WorkerOptions.TryParse(startInfo.ArgumentList.ToList(), out WorkerOptions? options, out string? error) &&
                options is not null &&
                ReferenceEquals(options.Provider.Traits(), provider.Traits()),
                $"Host provider {provider} launched a worker with different traits: {error}");
        }
    }

    private static void ExpectOutOfRange(Func<object> action, string message)
    {
        try
        {
            action();
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
