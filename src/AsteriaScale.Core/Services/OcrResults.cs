using Drawing = System.Drawing;

namespace AsteriaScale.Services;

// What any OCR backend hands to the translation core. These records used to
// live beside the Windows AI recognizer, which tied the platform-neutral hover
// pipeline to Windows App SDK types; a macOS Vision backend produces the same
// shapes.

internal enum HoverTextSource
{
    Ocr,
    TextHook
}

internal sealed record HoverOcrResult(
    string Text,
    string HoveredWord,
    int CharacterIndex,
    Drawing.Rectangle CaptureBounds,
    HoverTextSource TextSource = HoverTextSource.Ocr)
{
    // Neural phrase translation is allowed only after two compatible OCR
    // observations. A single observation is still sufficient for the local
    // dictionary, which must remain available when OCR is unstable.
    public bool IsSentenceContextStable { get; init; } = true;

    // Context is populated only by a process- and hook-stream-scoped
    // Textractor match. It is never rendered as part of the current reply.
    public IReadOnlyList<string> PreviousContext { get; init; } = Array.Empty<string>();

    public CurrentSpeakerContext? CurrentSpeaker { get; init; }
}

internal sealed record HoverOcrDiagnostic(HoverOcrResult? Selection, string Details);

internal sealed record OcrLineGeometry(
    string Text,
    double Left,
    double Top,
    double Right,
    double Bottom);

internal sealed record ScreenOcrBlock(
    string Text,
    Drawing.Rectangle Bounds,
    int LineCount);

internal sealed record ScreenOcrResult(
    Drawing.Rectangle CaptureBounds,
    IReadOnlyList<ScreenOcrBlock> Blocks);

internal sealed record SentenceContext(string Text, int CharacterIndex)
{
    public CurrentSpeakerContext? CurrentSpeaker { get; init; }
}

internal sealed record OcrWordGeometry(
    string Text,
    double Left,
    double Top,
    double Right,
    double Bottom,
    double Confidence);

internal sealed record OcrLineWords(
    string Text,
    IReadOnlyList<OcrWordGeometry> Words);

internal sealed record OcrRecognition(
    IReadOnlyList<OcrLineWords> Lines);
