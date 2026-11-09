namespace AsteriaScale.Services;

internal enum HoverUnloadReason
{
    Startup,
    ExplicitDisable,
    ModeChanged,
    SmartSettingsChanged,
    PhraseMemorySaving,
    // A host asked for the pass itself (CoreHost's models.unload). Routine:
    // it never asks for the compacting dictionary GC on its own.
    HostRequested
}

/// <summary>
/// Coalesces unload requests without turning routine model maintenance into an
/// expensive compacting GC. The caller owns synchronization.
/// </summary>
internal sealed class HoverUnloadIntentState
{
    private bool _releaseDictionaryMemoryRequested;

    public void Request(HoverUnloadReason reason)
    {
        _releaseDictionaryMemoryRequested |= reason is
            HoverUnloadReason.ExplicitDisable or HoverUnloadReason.ModeChanged;
    }

    public bool ConsumeReleaseRequest()
    {
        bool requested = _releaseDictionaryMemoryRequested;
        _releaseDictionaryMemoryRequested = false;
        return requested;
    }

    public static bool ShouldReleaseDictionaryMemory(
        bool releaseRequested,
        bool unloadDictionary) =>
        releaseRequested && unloadDictionary;
}
