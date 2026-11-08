using System.Diagnostics.CodeAnalysis;

namespace AsteriaScale.RankerProtocol;

/// <summary>
/// Everything the host and the ranker worker must agree on about one
/// execution provider.
///
/// Before this table each side asked "is it the CPU?" on its own, in a few
/// dozen places. With two providers that was harmless; a third one (CoreML on
/// macOS) added in only some of those places would let the host key a cached
/// verdict by glosses the worker never received, or expect a bi-encoder
/// pipeline the worker never ran, and serve stale decisions without any error
/// (AGENTS.md rules 2 and 3). Both assemblies reference this one, so a new
/// provider is declared once, with every trait spelled out.
///
/// The values are pinned by the "Ranker provider traits" smoke test; changing
/// one changes model input or cache identity and needs the same evidence as
/// any other ranker change.
/// </summary>
public sealed class RankerProviderTraits
{
    private RankerProviderTraits(
        string wireName,
        string executionProvider,
        string device,
        bool enrichedGlosses,
        bool biEncoderPrefilter,
        bool alternativeModels)
    {
        WireName = wireName;
        ExecutionProvider = executionProvider;
        Device = device;
        EnrichedGlosses = enrichedGlosses;
        BiEncoderPrefilter = biEncoderPrefilter;
        AlternativeModels = alternativeModels;
    }

    public static RankerProviderTraits Cpu { get; } = new(
        wireName: "cpu",
        executionProvider: "CPUExecutionProvider",
        device: "CPU",
        enrichedGlosses: true,
        biEncoderPrefilter: true,
        alternativeModels: true);

    // The NPU graph is compiled for a fixed sequence length: enriched glosses
    // already truncate above the median there, the embedding bundle is a CPU
    // artifact, and only GlossBERT has a VitisAI build.
    public static RankerProviderTraits Npu { get; } = new(
        wireName: "npu",
        executionProvider: "VitisAIExecutionProvider",
        device: "NPU",
        enrichedGlosses: false,
        biEncoderPrefilter: false,
        alternativeModels: false);

    public static IReadOnlyList<RankerProviderTraits> All { get; } = [Cpu, Npu];

    /// <summary>The value passed to the worker's <c>--provider</c> option.</summary>
    public string WireName { get; }

    /// <summary>The ONNX Runtime execution provider the worker must report.</summary>
    public string ExecutionProvider { get; }

    /// <summary>The device the worker must report.</summary>
    public string Device { get; }

    /// <summary>
    /// Whether candidate glosses are sent in their enriched rendering. The
    /// decision cache key includes the rendering, so the host and the request
    /// it builds must read the same flag.
    /// </summary>
    public bool EnrichedGlosses { get; }

    /// <summary>
    /// Whether the worker may narrow large candidate sets with the embedding
    /// bi-encoder before cross-encoding ("bi+cross" pipeline).
    /// </summary>
    public bool BiEncoderPrefilter { get; }

    /// <summary>
    /// Whether models other than GlossBERT (currently ConSeC) may run
    /// on this provider.
    /// </summary>
    public bool AlternativeModels { get; }

    public static bool TryFromWireName(
        string? wireName,
        [NotNullWhen(true)] out RankerProviderTraits? traits)
    {
        foreach (RankerProviderTraits candidate in All)
        {
            // The worker has always accepted the option case-insensitively.
            if (string.Equals(candidate.WireName, wireName, StringComparison.OrdinalIgnoreCase))
            {
                traits = candidate;
                return true;
            }
        }

        traits = null;
        return false;
    }

    public override string ToString() => WireName;
}
