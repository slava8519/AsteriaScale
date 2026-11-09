namespace AsteriaScale.Services;

/// <summary>
/// The card-kind predicates the WPF hover card applies to engine strings:
/// dictionary mode, Tatoeba, the model-free fast path, the result cache, the
/// sense accelerator with its abstention and unavailable statuses, the wide
/// header those statuses need, and the phrase-line caption. They moved
/// unchanged out of <c>HoverTranslationOverlay</c> (owner decision M12), so
/// another host and the shared tests read a card kind exactly as the Windows
/// card does. They match the Windows label constants, which history files
/// and benchmark reports store, so those strings stay byte for byte. Reading
/// a stored label back to its typed provenance covers every host's labels.
/// </summary>
internal static class HoverEngineLabelClassifier
{
    internal static bool IsDictionaryEngine(string engine) =>
        engine == LocalDictionaryService.EngineLabel;

    internal static bool IsTatoebaEngine(string engine) =>
        engine == SmartDictionaryHeuristicRanker.TatoebaEngineLabel;

    internal static bool IsFastPathEngine(string engine) =>
        engine == SmartDictionaryHeuristicRanker.EngineLabel ||
        IsTatoebaEngine(engine);

    internal static bool IsResultCacheEngine(string engine) =>
        engine == SmartDictionaryResultCache.EngineLabel;

    // The provisional marker tells the reader that candidate A may still be
    // replaced by the sense gate, so a changed line is expected, not a glitch.
    internal static string PhraseEngineLabel(SmartHoverPhraseLine phraseLine)
    {
        string label = phraseLine.Engine.Contains("VULKAN", StringComparison.Ordinal)
            ? "Фраза · TG4B · GPU"
            : "Фраза · TG4B · CPU";
        return phraseLine.IsProvisional ? label + " · уточняется…" : label;
    }

    // Reads the card header (meta label), not the engine string: these two
    // statuses make it long enough that a compact card would clip it.
    internal static bool IsLongSmartStatus(string metaLabel) =>
        metaLabel.Contains("НЕ УВЕРЕН", StringComparison.Ordinal) ||
        metaLabel.Contains("НЕДОСТУПЕН", StringComparison.Ordinal);

    internal static string? SmartAccelerator(string engine)
    {
        if (engine.StartsWith(
                GlossBertDictionarySelectorService.CpuEngineLabel,
                StringComparison.Ordinal))
        {
            return "CPU";
        }

        if (engine.StartsWith(
                GlossBertDictionarySelectorService.NpuEngineLabel,
                StringComparison.Ordinal))
        {
            return "NPU";
        }

        return null;
    }

    internal static bool IsSmartAbstentionEngine(string engine) =>
        engine.Contains("НЕ УВЕРЕН", StringComparison.OrdinalIgnoreCase);

    internal static bool IsSmartUnavailableEngine(string engine) =>
        engine.Contains("НЕДОСТУПЕН", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The provenance a stored primary-result label names, under any host's
    /// label set, or null for a phrase label or an unknown string. A history
    /// list reports typed provenance this way instead of leaving its reader
    /// to parse the label. The predicates above stay what the WPF card reads.
    /// </summary>
    internal static HoverProvenance? ReadProvenance(string engine)
    {
        // The history stores a label with its whitespace runs collapsed
        // (TranslationHistoryService.Record), so both sides are compared in
        // that form: a live label and its history entry read the same.
        string stored = CollapseWhitespace(engine);
        foreach (HoverEngineLabels labels in HoverEngineLabels.All)
        {
            foreach (HoverProvenance provenance in HoverProvenance.All)
            {
                if (string.Equals(
                        CollapseWhitespace(labels.For(provenance)),
                        stored,
                        StringComparison.Ordinal))
                {
                    return provenance;
                }
            }
        }

        return null;
    }

    private static string CollapseWhitespace(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
