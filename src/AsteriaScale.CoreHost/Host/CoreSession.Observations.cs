using AsteriaScale.CoreHost.Protocol;
using AsteriaScale.Models;
using AsteriaScale.Services;
using Drawing = System.Drawing;

namespace AsteriaScale.CoreHost.Host;

/// <summary>
/// One frame CoreHost recognized and found stable, as WindowsAiOcrService's
/// HoverRecognitionCacheEntry keeps it, plus the display it was on: rectangles
/// in display pixels coincide on different displays, where Windows tells them
/// apart by virtual-desktop coordinates. The confirmed name travels with the
/// recognition because agreement was established between two observations
/// that no later hover can re-observe.
/// </summary>
internal sealed record FrameCacheEntry(
    long DisplayId,
    Drawing.Rectangle Bounds,
    ulong Fingerprint,
    DateTimeOffset CreatedAt,
    OcrRecognition Recognition,
    string? ConfirmedSpeakerName);

// Apple Vision observations behind the protocol (roadmap 20.3; docs/MACOS.md
// T19, T27, T28): the capture plan (ocr.plan), the frame cache and the hover
// selection, in WindowsAiOcrService.RecognizeNearCursorAsync's order, with
// Core's OcrGeometry and OcrTextRules. The app captures and recognizes; every
// decision about the text is made here.
internal sealed partial class CoreSession
{
    // One entry, as on Windows, under its own lock; cleared on dispose.
    private readonly object _frameCacheLock = new();
    private FrameCacheEntry? _frameCache;

    /// <summary>
    /// ocr.plan: where the app captures for the next hover and how many
    /// observations the hover takes. Like a hover it first waits for every
    /// settings change admitted before it (not for the hover queue), so its
    /// mode is the one the following hover.resolve is checked against.
    /// Dictionary mode captures 900x340 around the cursor, kept inside the
    /// display; smart mode up to 2400x720, or the cached frame's bounds while
    /// the cursor stays inside their reuse area (no age check there, as on
    /// Windows), and names the cached fingerprint only when those bounds are
    /// the entry's and the entry is at most 30 s old.
    /// </summary>
    public async Task<OcrPlanResult> PlanCaptureAsync(OcrPlanPayload payload, CancellationToken cancellationToken)
    {
        await AppliedSettingsAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!_coordinator.Enabled)
        {
            throw new RequestErrorException(Errors.HoverDisabled());
        }

        string mode = HoverTranslationModes.NormalizeUserMode(_settings.HoverTranslationMode);
        Drawing.Rectangle display = ObservationRules.DisplayRectangle(payload.Display);
        Drawing.Point cursor = new(payload.Cursor.X, payload.Cursor.Y);
        if (mode == HoverTranslationModes.Dictionary)
        {
            return new OcrPlanResult
            {
                Mode = mode,
                CaptureBounds = ObservationRules.ToWire(OcrGeometry.CalculateCaptureBounds(
                    display,
                    cursor,
                    ObservationRules.DictionaryCaptureWidth,
                    ObservationRules.DictionaryCaptureHeight)),
                Observations = 1,
            };
        }

        Drawing.Rectangle bounds = OcrGeometry.CalculateSentenceCaptureBounds(
            display,
            cursor,
            GetReusableSentenceBounds(payload.Display.Id, cursor));
        return new OcrPlanResult
        {
            Mode = mode,
            CaptureBounds = ObservationRules.ToWire(bounds),
            Observations = 2,
            CachedFingerprint = CachedFingerprintFor(payload.Display.Id, bounds, DateTimeOffset.UtcNow) is ulong cached
                ? ObservationRules.FormatFingerprint(cached)
                : null,
        };
    }

    /// <summary>
    /// The frame a hover with zero observations was admitted against, looked
    /// up before Core sees the hover: a miss is stale_frame, not a failed
    /// hover, which is all an exception inside Core's recognition could
    /// become. Anything else needs no entry.
    /// </summary>
    private FrameCacheEntry? PinFrame(ObservationsSource source)
    {
        if (source.Observations.Length != 0)
        {
            return null;
        }

        Drawing.Rectangle bounds = ObservationRules.ToRectangle(source.CaptureBounds);
        ulong fingerprint = ObservationRules.ParseFingerprint(source.CachedFingerprint!);
        return TryGetCachedRecognition(source.Display.Id, bounds, fingerprint, DateTimeOffset.UtcNow) ??
            throw new RequestErrorException(Errors.StaleFrame(
                $"no cached frame for display {source.Display.Id}, bounds ({bounds.X}, {bounds.Y}, " +
                $"{bounds.Width}x{bounds.Height}) and fingerprint {source.CachedFingerprint}"));
    }

    /// <summary>
    /// The hover selection from observations, in RecognizeNearCursorAsync's
    /// order. Dictionary mode selects in its one observation and never reads
    /// or fills the cache. Smart mode first reuses the cached frame (the
    /// pinned entry, or the entry whose bounds and fingerprint match
    /// observation 1) and re-applies the per-selection gates to it;
    /// otherwise it selects in both observations, stabilizes them, and
    /// caches the chosen observation when the result is stable. An
    /// observation count that does not fit the mode Core hovers in (it
    /// changed after admission) fails the hover visibly.
    /// </summary>
    private HoverOcrResult? RecognizeObservations(CoreHostHover anchor, ObservationsSource source, bool sentenceContext)
    {
        Drawing.Rectangle bounds = ObservationRules.ToRectangle(source.CaptureBounds);
        ObservedFrame[] observations = source.Observations;
        if (!sentenceContext)
        {
            if (observations.Length != 1)
            {
                throw new InvalidOperationException(
                    $"A dictionary-mode hover got {observations.Length} observations; the mode changed after admission.");
            }

            return ObservationSelection.SelectNearCursor(
                ObservationDecoder.Decode(observations[0]),
                source.Cursor,
                bounds,
                sentenceContext: false);
        }

        if (observations.Length != 2 && anchor.PinnedFrame is null)
        {
            throw new InvalidOperationException(
                $"A smart-mode hover got {observations.Length} observations; the mode changed after admission.");
        }

        FrameCacheEntry? cached = anchor.PinnedFrame ?? TryGetCachedRecognition(
            source.Display.Id,
            bounds,
            ObservationRules.ParseFingerprint(observations[0].Fingerprint),
            DateTimeOffset.UtcNow);
        if (cached is not null)
        {
            return OcrTextRules.ApplyConfirmedFrameGates(
                ObservationSelection.SelectNearCursor(cached.Recognition, source.Cursor, bounds, sentenceContext: true),
                cached.ConfirmedSpeakerName);
        }

        OcrRecognition firstRecognition = ObservationDecoder.Decode(observations[0]);
        HoverOcrResult? firstSelection = ObservationSelection.SelectNearCursor(
            firstRecognition,
            source.Cursor,
            bounds,
            sentenceContext: true);
        OcrRecognition secondRecognition = ObservationDecoder.Decode(observations[1]);
        HoverOcrResult? secondSelection = ObservationSelection.SelectNearCursor(
            secondRecognition,
            source.Cursor,
            bounds,
            sentenceContext: true);
        HoverOcrResult? stabilized = OcrTextRules.StabilizeSentenceContext(
            firstSelection,
            secondSelection,
            out bool useSecondObservation);
        if (stabilized is null)
        {
            return null;
        }

        if (stabilized.IsSentenceContextStable)
        {
            StoreCachedRecognition(
                source.Display.Id,
                bounds,
                ObservationRules.ParseFingerprint(observations[useSecondObservation ? 1 : 0].Fingerprint),
                useSecondObservation ? secondRecognition : firstRecognition,
                stabilized.CurrentSpeaker?.Name,
                DateTimeOffset.UtcNow);
        }

        return stabilized;
    }

    // WindowsAiOcrService.GetReusableSentenceBounds, on the same display.
    private Drawing.Rectangle? GetReusableSentenceBounds(long displayId, Drawing.Point cursor)
    {
        lock (_frameCacheLock)
        {
            return _frameCache is not null &&
                   _frameCache.DisplayId == displayId &&
                   OcrGeometry.IsInsideReuseArea(_frameCache.Bounds, cursor)
                ? _frameCache.Bounds
                : null;
        }
    }

    // The fingerprint of the entry whose frame a capture at these bounds would
    // reuse now (Core's own reuse rule against the entry's fingerprint).
    private ulong? CachedFingerprintFor(long displayId, Drawing.Rectangle bounds, DateTimeOffset now)
    {
        lock (_frameCacheLock)
        {
            return _frameCache is not null &&
                   _frameCache.DisplayId == displayId &&
                   OcrTextRules.CanReuseCachedRecognition(
                       _frameCache.Bounds,
                       _frameCache.Fingerprint,
                       _frameCache.CreatedAt,
                       bounds,
                       _frameCache.Fingerprint,
                       now)
                ? _frameCache.Fingerprint
                : null;
        }
    }

    // WindowsAiOcrService.TryGetCachedRecognition, on the same display.
    private FrameCacheEntry? TryGetCachedRecognition(
        long displayId,
        Drawing.Rectangle bounds,
        ulong fingerprint,
        DateTimeOffset now)
    {
        lock (_frameCacheLock)
        {
            return _frameCache is not null &&
                   _frameCache.DisplayId == displayId &&
                   OcrTextRules.CanReuseCachedRecognition(
                       _frameCache.Bounds,
                       _frameCache.Fingerprint,
                       _frameCache.CreatedAt,
                       bounds,
                       fingerprint,
                       now)
                ? _frameCache
                : null;
        }
    }

    private void StoreCachedRecognition(
        long displayId,
        Drawing.Rectangle bounds,
        ulong fingerprint,
        OcrRecognition recognition,
        string? confirmedSpeakerName,
        DateTimeOffset now)
    {
        lock (_frameCacheLock)
        {
            _frameCache = new FrameCacheEntry(
                displayId,
                bounds,
                fingerprint,
                now,
                recognition,
                confirmedSpeakerName);
        }
    }

    private void ClearFrameCache()
    {
        lock (_frameCacheLock)
        {
            _frameCache = null;
        }
    }

    /// <summary>
    /// The game key as Core stores it (empty when Core drops it: not path: or
    /// process:), and whether Core's normalization keeps that form when it
    /// normalizes the stored key again. Core keeps the normalization private,
    /// so Resolve, which normalizes the key on every return path, computes it
    /// without reading any stored preference (no name). Some keys change on
    /// the second pass (process:x.exe.exe loses one ".exe" per pass; a quoted
    /// path: key ending in a space loses the space) and never resolve, so
    /// speaker.set and hover.resolve (docs/MACOS.md T32) refuse them.
    /// </summary>
    internal static (string Key, bool Stable) NormalizeGameKey(string key, string displayName)
    {
        AppSettings none = new();
        string once = SpeakerGenderPreferences.Resolve(none, new SpeakerGameIdentity(key, displayName), null).GameKey;
        string twice = SpeakerGenderPreferences.Resolve(none, new SpeakerGameIdentity(once, displayName), null).GameKey;
        return (once, string.Equals(once, twice, StringComparison.Ordinal));
    }
}
