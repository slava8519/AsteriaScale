using System.Globalization;
using AsteriaScale.CoreHost.Protocol;
using AsteriaScale.Models;
using AsteriaScale.Services;
using Drawing = System.Drawing;

namespace AsteriaScale.CoreHost.Host;

/// <summary>
/// The schema rules of an observations source (hover.resolve, ocr.select) and
/// of ocr.plan, beyond what the strict wire types already refuse. A violation
/// is bad_request with the English detail returned here. The limits bound the
/// work of one request, not its size: a request inside all of them can still
/// exceed the 1 MiB inbound frame, which ends the connection (frame_too_long),
/// so the app refuses to send a frame over 900 KiB rather than cut
/// observations short.
/// </summary>
internal static class ObservationRules
{
    // The Windows dictionary capture, WindowsAiOcrService.RecognizeNearCursorAsync
    // (CaptureAround(cursor, 900, 340)), in display pixels (docs/MACOS.md T39).
    // Kept here, not in Core, so the macOS host leaves Core.dll unchanged; the
    // smart capture (up to 2400x720) is Core's own OcrGeometry constant.
    public const int DictionaryCaptureWidth = 900;
    public const int DictionaryCaptureHeight = 340;

    public const long MaxDisplayId = uint.MaxValue;
    public const int MaxDisplaySide = 32768;
    public const int MaxLines = 256;
    public const int MaxLineText = 4000;
    public const int MaxWordsPerLine = 256;
    public const int MaxWordsPerObservation = 4096;
    public const int MaxWordText = 256;
    public const int FingerprintDigits = 16;

    // A box edge may lie up to one pixel outside the frame (Vision's
    // rounding at the border); anything farther is not frame pixels.
    private const double BoxSlack = 1;

    public static string? ValidatePlan(OcrPlanPayload payload) =>
        ValidateDisplay(payload.Display) ?? ValidateCursor(payload.Cursor, payload.Display);

    /// <summary>hover.resolve: dictionary takes exactly one observation, smart two, or none with cachedFingerprint.</summary>
    public static string? ValidateHover(string? mode, ObservationsSource source) =>
        ValidateMode(mode) ?? ValidateSource(source, mode!, forSelect: false);

    /// <summary>ocr.select: dictionary takes one observation, smart one or two, and no cachedFingerprint.</summary>
    public static string? ValidateSelect(OcrSelectPayload payload) =>
        ValidateMode(payload.Mode) ?? ValidateSource(payload.Source, payload.Mode!, forSelect: true);

    public static bool TryParseFingerprint(string? text, out ulong value)
    {
        value = 0;
        if (text is null || text.Length != FingerprintDigits ||
            !text.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            return false;
        }

        return ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>The wire form of a fingerprint: the ulong as 16 lowercase hex digits.</summary>
    public static string FormatFingerprint(ulong value) => value.ToString("x16", CultureInfo.InvariantCulture);

    public static ulong ParseFingerprint(string text) =>
        TryParseFingerprint(text, out ulong value)
            ? value
            : throw new InvalidOperationException($"'{text}' is not a validated fingerprint.");

    /// <summary>The display in its own pixels: origin at its top-left corner.</summary>
    public static Drawing.Rectangle DisplayRectangle(DisplayInfo display) => new(0, 0, display.Width, display.Height);

    public static Drawing.Rectangle ToRectangle(PixelRectangle bounds) => new(bounds.X, bounds.Y, bounds.Width, bounds.Height);

    public static PixelRectangle ToWire(Drawing.Rectangle bounds) => new()
    {
        X = bounds.X,
        Y = bounds.Y,
        Width = bounds.Width,
        Height = bounds.Height,
    };

    private static string? ValidateMode(string? mode) =>
        mode is HoverTranslationModes.Dictionary or HoverTranslationModes.Smart
            ? null
            : mode is null
                ? "mode is required with an observations source"
                : $"mode must be dictionary or smart, got '{mode}'";

    private static string? ValidateDisplay(DisplayInfo display)
    {
        if (display.Id is < 0 or > MaxDisplayId)
        {
            return $"display.id must be 0..{MaxDisplayId}, got {display.Id}";
        }

        return display.Width is < 1 or > MaxDisplaySide || display.Height is < 1 or > MaxDisplaySide
            ? $"display.width and display.height must be 1..{MaxDisplaySide} pixels, got {display.Width}x{display.Height}"
            : null;
    }

    // Inside the display as Rectangle.Contains counts it: 0 <= x < width.
    private static string? ValidateCursor(PixelPoint cursor, DisplayInfo display) =>
        cursor.X < 0 || cursor.Y < 0 || cursor.X >= display.Width || cursor.Y >= display.Height
            ? $"cursor ({cursor.X}, {cursor.Y}) must lie inside the {display.Width}x{display.Height} display"
            : null;

    private static string? ValidateSource(ObservationsSource source, string mode, bool forSelect)
    {
        if (!string.Equals(source.Kind, "observations", StringComparison.Ordinal))
        {
            return $"source.kind must be observations, got '{source.Kind}'";
        }

        if ((ValidateDisplay(source.Display) ?? ValidateCursor(source.Cursor, source.Display)) is { } place)
        {
            return place;
        }

        PixelRectangle bounds = source.CaptureBounds;
        if (bounds.Width < 1 || bounds.Height < 1 || bounds.X < 0 || bounds.Y < 0 ||
            (long)bounds.X + bounds.Width > source.Display.Width ||
            (long)bounds.Y + bounds.Height > source.Display.Height)
        {
            return $"source.captureBounds ({bounds.X}, {bounds.Y}, {bounds.Width}x{bounds.Height}) must be at least 1x1 " +
                   $"and lie wholly inside the {source.Display.Width}x{source.Display.Height} display";
        }

        int count = source.Observations.Length;
        bool dictionary = mode == HoverTranslationModes.Dictionary;
        if (forSelect)
        {
            if (source.CachedFingerprint is not null)
            {
                return "ocr.select keeps no frame cache and takes no source.cachedFingerprint";
            }

            if (dictionary ? count != 1 : count is < 1 or > 2)
            {
                return $"ocr.select takes 1 observation in dictionary mode and 1 or 2 in smart mode, got {count}";
            }
        }
        else if (dictionary)
        {
            if (count != 1 || source.CachedFingerprint is not null)
            {
                return $"dictionary mode takes exactly 1 observation and no source.cachedFingerprint, got {count}";
            }
        }
        else if (count == 0 ? source.CachedFingerprint is null : count != 2 || source.CachedFingerprint is not null)
        {
            return "smart mode takes 2 observations, or 0 together with source.cachedFingerprint " +
                   $"(and the fingerprint only then), got {count}";
        }

        if (source.CachedFingerprint is { } cached && !TryParseFingerprint(cached, out _))
        {
            return "source.cachedFingerprint must be 16 lowercase hex digits";
        }

        for (int index = 0; index < count; index++)
        {
            if (ValidateFrame(source.Observations[index], bounds) is { } problem)
            {
                return $"source.observations[{index}]: {problem}";
            }
        }

        return null;
    }

    // The strict wire types refuse a null member but not a null array
    // element, so each element is checked here before anything reads it: on
    // the reader thread a NullReferenceException would end the connection.
    private static string? ValidateFrame(ObservedFrame? frame, PixelRectangle bounds)
    {
        if (frame is null)
        {
            return "must be an object, not null";
        }

        if (!TryParseFingerprint(frame.Fingerprint, out _))
        {
            return "fingerprint must be 16 lowercase hex digits";
        }

        if (frame.Lines.Length > MaxLines)
        {
            return $"holds {frame.Lines.Length} lines, at most {MaxLines} are allowed";
        }

        int words = 0;
        for (int lineIndex = 0; lineIndex < frame.Lines.Length; lineIndex++)
        {
            ObservedLine? line = frame.Lines[lineIndex];
            if (line is null)
            {
                return $"lines[{lineIndex}] must be an object, not null";
            }

            if (line.Text.Length > MaxLineText)
            {
                return $"lines[{lineIndex}].text must hold at most {MaxLineText} characters";
            }

            if (!double.IsFinite(line.Confidence) || line.Confidence is < 0 or > 1)
            {
                return $"lines[{lineIndex}].confidence must be a finite number in 0..1";
            }

            if (line.Words.Length is < 1 or > MaxWordsPerLine)
            {
                return $"lines[{lineIndex}] must hold 1..{MaxWordsPerLine} words";
            }

            words += line.Words.Length;
            if (words > MaxWordsPerObservation)
            {
                return $"holds more than {MaxWordsPerObservation} words";
            }

            for (int wordIndex = 0; wordIndex < line.Words.Length; wordIndex++)
            {
                ObservedWord? word = line.Words[wordIndex];
                if (word is null)
                {
                    return $"lines[{lineIndex}].words[{wordIndex}] must be an object, not null";
                }

                if (word.Text.Length is < 1 or > MaxWordText)
                {
                    return $"lines[{lineIndex}].words[{wordIndex}].text must hold 1..{MaxWordText} characters";
                }

                // Degenerate boxes (right <= left or bottom <= top) are valid
                // here: the decoder drops them, as ExtractRecognition does.
                if (!WithinFrame(word.Left, bounds.Width) || !WithinFrame(word.Right, bounds.Width) ||
                    !WithinFrame(word.Top, bounds.Height) || !WithinFrame(word.Bottom, bounds.Height))
                {
                    return $"lines[{lineIndex}].words[{wordIndex}] has a box outside the " +
                           $"{bounds.Width}x{bounds.Height} frame or a non-finite edge";
                }
            }
        }

        return null;
    }

    private static bool WithinFrame(double value, int side) =>
        double.IsFinite(value) && value >= -BoxSlack && value <= side + BoxSlack;
}

/// <summary>
/// Turns one observation into the <see cref="OcrRecognition"/> Core's
/// geometry reads, with exactly the filters of WindowsAiOcrService's
/// ExtractRecognition (docs/MACOS.md T28): a word box as it came, the word's
/// text through Normalize, a degenerate box dropped, the line's text through
/// NormalizeLong(…, 1200), and a line with no word left or with empty text
/// dropped. Vision rates a line, not its words, so the line's confidence is
/// copied to each word (Core reads it nowhere as a threshold). The rules are
/// copied here rather than moved into Core, which would change Core.dll; the
/// CoreHost protocol check reads ExtractRecognition's source and fails when
/// the statements this copies change there, and reads this method and fails
/// when it loses one of them (the word's Normalize is not observable from
/// outside: SelectNearPoint normalizes the hovered word again).
/// </summary>
internal static class ObservationDecoder
{
    public static OcrRecognition Decode(ObservedFrame frame)
    {
        List<OcrLineWords> lines = [];
        foreach (ObservedLine line in frame.Lines)
        {
            List<OcrWordGeometry> words = [];
            foreach (ObservedWord word in line.Words)
            {
                if (word.Right <= word.Left || word.Bottom <= word.Top)
                {
                    continue;
                }

                words.Add(new OcrWordGeometry(
                    OcrTextRules.Normalize(word.Text),
                    word.Left,
                    word.Top,
                    word.Right,
                    word.Bottom,
                    line.Confidence));
            }

            string text = OcrTextRules.NormalizeLong(line.Text, 1200);
            if (words.Count > 0 && text.Length > 0)
            {
                lines.Add(new OcrLineWords(text, words));
            }
        }

        return new OcrRecognition(lines);
    }
}

/// <summary>
/// The hover selection from decoded observations, as WindowsAiOcrService
/// makes it, and ocr.select, which applies the same rules without state,
/// translation or history.
/// </summary>
internal static class ObservationSelection
{
    /// <summary>
    /// WindowsAiOcrService.SelectRecognitionNearCursor: nothing without lines;
    /// otherwise the cursor relative to the capture (integer subtraction,
    /// passed as float) and the capture bounds, which Core passes through.
    /// </summary>
    public static HoverOcrResult? SelectNearCursor(
        OcrRecognition? recognition,
        PixelPoint cursor,
        Drawing.Rectangle captureBounds,
        bool sentenceContext)
    {
        if (recognition is null || recognition.Lines.Count == 0)
        {
            return null;
        }

        return OcrGeometry.SelectNearPoint(
            recognition,
            cursor.X - captureBounds.Left,
            cursor.Y - captureBounds.Top,
            captureBounds,
            sentenceContext);
    }

    /// <summary>
    /// ocr.select: dictionary mode selects in its one observation; smart mode
    /// stabilizes two observations, or one against none (never stable; Core
    /// keeps that observation's speaker). Nothing is cached, translated or
    /// recorded.
    /// </summary>
    public static OcrSelectResult Select(string mode, ObservationsSource source)
    {
        Drawing.Rectangle bounds = ObservationRules.ToRectangle(source.CaptureBounds);
        OcrRecognition first = ObservationDecoder.Decode(source.Observations[0]);
        HoverOcrResult? selection;
        if (mode == HoverTranslationModes.Dictionary)
        {
            selection = SelectNearCursor(first, source.Cursor, bounds, sentenceContext: false);
        }
        else
        {
            HoverOcrResult? firstSelection = SelectNearCursor(first, source.Cursor, bounds, sentenceContext: true);
            HoverOcrResult? secondSelection = source.Observations.Length > 1
                ? SelectNearCursor(ObservationDecoder.Decode(source.Observations[1]), source.Cursor, bounds, sentenceContext: true)
                : null;
            selection = OcrTextRules.StabilizeSentenceContext(firstSelection, secondSelection, out _);
        }

        return new OcrSelectResult
        {
            Selection = selection is null
                ? null
                : new OcrSelection
                {
                    Text = selection.Text,
                    HoveredWord = selection.HoveredWord,
                    CharacterIndex = selection.CharacterIndex,
                    SentenceStable = selection.IsSentenceContextStable,
                    SpeakerName = selection.CurrentSpeaker?.Name is { Length: > 0 } speaker ? speaker : null,
                },
            Lines = first.Lines.Select(line => line.Text).ToArray(),
        };
    }
}
