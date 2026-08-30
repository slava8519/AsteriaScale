using AsteriaScale.Services;

/// <summary>
/// Quantizations that exist only for benchmarking. They live in the test
/// project on purpose: production ships exactly one pinned artifact, and
/// nothing in the app can select these. Each entry carries its own size and
/// SHA-256, so `--phrase-quant` relaxes nothing — it names a different file
/// that is verified just as strictly, under an identity that says so.
/// </summary>
internal static class AlternativeModelPins
{
    private const string ModelRevision =
        "google/translategemma-4b-it@10042cb0e6e7fdce748996a71dc3dc432a4e0c89" +
        "/mradermacher-static-";

    internal static readonly TranslateGemmaTranslationService.TranslateGemmaModelPin Q6K =
        Create(
            "q6_K",
            "translategemma-4b-it.Q6_K.gguf",
            3_190_755_840,
            "9848d1c4c61020511d278e1a82d460f198d3214e19d74344ef09446cdc3cb544");

    internal static TranslateGemmaTranslationService.TranslateGemmaModelPin Resolve(
        string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.ToLowerInvariant() switch
        {
            "q6_k" => Q6K,
            _ => throw new InvalidOperationException(
                $"Неизвестная квантизация '{value}'. Доступно: q6_k.")
        };
    }

    private static TranslateGemmaTranslationService.TranslateGemmaModelPin Create(
        string quantTag,
        string fileName,
        long bytes,
        string sha256)
    {
        string identity =
            ModelRevision + quantTag +
            "@35a7486e128b19642cdc72d7b91b21ba388aaf42:" + sha256;
        return new TranslateGemmaTranslationService.TranslateGemmaModelPin(
            fileName,
            bytes,
            sha256,
            identity + "/llama.cpp-b9632-cpu",
            identity + "/llama.cpp-b9632-vulkan-full");
    }
}
