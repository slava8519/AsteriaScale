using Drawing = System.Drawing;
using DrawingImaging = System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Graphics.Imaging;
using Microsoft.Windows.AI;
using Microsoft.Windows.AI.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using AsteriaScale.Interop;
// The unit-free text rules (clean-up, sentence extraction, the agreement of
// two observations, frame reuse) and the pixel geometry (the line and word
// nearest the cursor, sentence and block assembly, capture bounds) moved
// verbatim to Core's OcrTextRules and OcrGeometry so a macOS host can share
// them; the imports, and NativeMethods.Point's implicit conversion to the
// System.Drawing.Point that Core takes, keep every call site here unchanged.
using static AsteriaScale.Services.OcrTextRules;
using static AsteriaScale.Services.OcrGeometry;

namespace AsteriaScale.Services;

internal sealed partial class WindowsAiOcrService : IDisposable
{
    private static int _traceSequence;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private readonly object _hoverCacheLock = new();
    private TextRecognizer? _recognizer;
    private HoverRecognitionCacheEntry? _hoverCache;

    public async Task EnsureReadyAsync()
    {
        if (_recognizer is not null)
        {
            return;
        }

        await _initializeLock.WaitAsync();
        try
        {
            if (_recognizer is not null)
            {
                return;
            }

            AIFeatureReadyState state = TextRecognizer.GetReadyState();
            if (state == AIFeatureReadyState.NotReady)
            {
                AIFeatureReadyResult result = await TextRecognizer.EnsureReadyAsync();
                if (result.Status != AIFeatureReadyResultState.Success)
                {
                    throw new InvalidOperationException("NPU OCR: " + result.ExtendedError.Message);
                }
            }
            else if (state != AIFeatureReadyState.Ready)
            {
                throw new InvalidOperationException(string.Format("NPU OCR недоступен: {0}", state));
            }

            _recognizer = await TextRecognizer.CreateAsync();
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    internal async Task<HoverOcrResult?> RecognizeNearCursorAsync(
        NativeMethods.Point cursor,
        bool sentenceContext,
        CancellationToken cancellationToken)
    {
        await EnsureReadyAsync();
        cancellationToken.ThrowIfCancellationRequested();

        using Capture firstCapture = sentenceContext
            ? CaptureSentenceContext(cursor, GetReusableSentenceBounds(cursor))
            : CaptureAround(cursor, 900, 340);
        byte[] firstPixels = ReadBgraPixels(firstCapture.Bitmap);
        ulong firstFingerprint = ComputeFrameFingerprint(firstPixels);
        OcrRecognition? firstRecognition = null;
        string? confirmedSpeakerName = null;
        bool cacheHit = sentenceContext &&
                        TryGetCachedRecognition(
                            firstCapture.Bounds,
                            firstFingerprint,
                            DateTimeOffset.UtcNow,
                            out firstRecognition,
                            out confirmedSpeakerName);
        if (cacheHit)
        {
            HoverOcrResult? cachedSelection = ApplyConfirmedFrameGates(
                SelectRecognitionNearCursor(
                    firstRecognition,
                    cursor,
                    firstCapture.Bounds,
                    sentenceContext),
                confirmedSpeakerName);
            TraceRecognition(
                firstCapture.Bitmap,
                firstRecognition!,
                cachedSelection,
                cursor.X - firstCapture.Bounds.Left,
                cursor.Y - firstCapture.Bounds.Top,
                firstCapture.Bounds);
            return cachedSelection;
        }

        firstRecognition = RecognizePixels(
            firstPixels,
            firstCapture.Bitmap.Width,
            firstCapture.Bitmap.Height,
            cancellationToken);
        HoverOcrResult? firstSelection = SelectRecognitionNearCursor(
            firstRecognition,
            cursor,
            firstCapture.Bounds,
            sentenceContext);
        if (!sentenceContext)
        {
            if (firstRecognition is not null)
            {
                TraceRecognition(
                    firstCapture.Bitmap,
                    firstRecognition,
                    firstSelection,
                    cursor.X - firstCapture.Bounds.Left,
                    cursor.Y - firstCapture.Bounds.Top,
                    firstCapture.Bounds);
            }

            return firstSelection;
        }

        // A second capture is deliberately bounded to one extra pass and runs
        // before the overlay is shown. VN renderers can expose a temporarily
        // incomplete glyph frame; agreeing observations are cheap to cache,
        // while a richer compatible observation repairs omissions without
        // ever splicing unrelated OCR strings together.
        cancellationToken.ThrowIfCancellationRequested();
        using Capture secondCapture = CaptureSentenceContext(cursor, firstCapture.Bounds);
        byte[] secondPixels = ReadBgraPixels(secondCapture.Bitmap);
        ulong secondFingerprint = ComputeFrameFingerprint(secondPixels);
        OcrRecognition? secondRecognition = RecognizePixels(
            secondPixels,
            secondCapture.Bitmap.Width,
            secondCapture.Bitmap.Height,
            cancellationToken);
        HoverOcrResult? secondSelection = SelectRecognitionNearCursor(
            secondRecognition,
            cursor,
            secondCapture.Bounds,
            sentenceContext: true);
        HoverOcrResult? stabilized = StabilizeSentenceContext(
            firstSelection,
            secondSelection,
            out bool useSecondObservation);
        if (stabilized is null)
        {
            return null;
        }

        Capture selectedCapture = useSecondObservation ? secondCapture : firstCapture;
        OcrRecognition? selectedRecognition = useSecondObservation
            ? secondRecognition
            : firstRecognition;
        ulong selectedFingerprint = useSecondObservation
            ? secondFingerprint
            : firstFingerprint;
        if (stabilized.IsSentenceContextStable && selectedRecognition is not null)
        {
            StoreCachedRecognition(
                selectedCapture.Bounds,
                selectedFingerprint,
                selectedRecognition,
                stabilized.CurrentSpeaker?.Name,
                DateTimeOffset.UtcNow);
        }

        if (selectedRecognition is not null)
        {
            TraceRecognition(
                selectedCapture.Bitmap,
                selectedRecognition,
                stabilized,
                cursor.X - selectedCapture.Bounds.Left,
                cursor.Y - selectedCapture.Bounds.Top,
                selectedCapture.Bounds);
        }

        return stabilized;
    }

    private static HoverOcrResult? SelectRecognitionNearCursor(
        OcrRecognition? recognition,
        NativeMethods.Point cursor,
        Drawing.Rectangle captureBounds,
        bool sentenceContext)
    {
        if (recognition is null || recognition.Lines.Count == 0)
        {
            return null;
        }

        return SelectNearPoint(
            recognition,
            cursor.X - captureBounds.Left,
            cursor.Y - captureBounds.Top,
            captureBounds,
            sentenceContext);
    }

    internal async Task<ScreenOcrResult> RecognizeImageBlocksAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync();
        using Drawing.Bitmap bitmap = new(path);
        Drawing.Rectangle bounds = new(0, 0, bitmap.Width, bitmap.Height);
        OcrRecognition? recognized = RecognizeBitmap(bitmap, cancellationToken);
        return recognized is null || recognized.Lines.Count == 0
            ? new ScreenOcrResult(bounds, [])
            : new ScreenOcrResult(bounds, BuildScreenBlocks(ExtractLineGeometries(recognized), bounds));
    }

    internal async Task<HoverOcrDiagnostic> DiagnoseImageNearPointAsync(
        string path,
        int cursorX,
        int cursorY,
        bool sentenceContext = false,
        CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync();
        using Drawing.Bitmap source = new(path);
        Drawing.Rectangle image = new(0, 0, source.Width, source.Height);
        Drawing.Rectangle bounds = sentenceContext
            ? CalculateSentenceCaptureBounds(
                image,
                new NativeMethods.Point { X = cursorX, Y = cursorY })
            : CalculateCaptureBounds(
                image,
                new NativeMethods.Point { X = cursorX, Y = cursorY },
                900,
                340);
        using Drawing.Bitmap crop = source.Clone(bounds, DrawingImaging.PixelFormat.Format32bppArgb);
        OcrRecognition? recognized = RecognizeBitmap(crop, cancellationToken);
        if (recognized is null || recognized.Lines.Count == 0)
        {
            return new HoverOcrDiagnostic(null, "OCR returned no lines.");
        }

        HoverOcrResult? selection = SelectNearPoint(
            recognized,
            cursorX - bounds.Left,
            cursorY - bounds.Top,
            bounds,
            sentenceContext);
        string details = DescribeRecognition(
            recognized,
            selection,
            cursorX - bounds.Left,
            cursorY - bounds.Top,
            bounds);
        return new HoverOcrDiagnostic(selection, details);
    }

    private static string DescribeRecognition(
        OcrRecognition recognized,
        HoverOcrResult? selection,
        float cursorX,
        float cursorY,
        Drawing.Rectangle bounds)
    {
        StringBuilder details = new();
        details.AppendLine($"crop={bounds} cursor=({cursorX:F1},{cursorY:F1})");
        foreach (OcrLineWords line in recognized.Lines)
        {
            details.AppendLine("LINE: " + Normalize(line.Text));
            foreach (OcrWordGeometry word in line.Words)
            {
                details.AppendLine(
                    $"  {Normalize(word.Text),-20} [{word.Left:F1},{word.Top:F1}..{word.Right:F1},{word.Bottom:F1}] confidence={word.Confidence:F3}");
            }
        }

        details.AppendLine("SELECTION: " + (selection is null
            ? "<none>"
            : $"{selection.HoveredWord} | {selection.Text} | index={selection.CharacterIndex}"));
        return details.ToString();
    }

    private static void TraceRecognition(
        Drawing.Bitmap bitmap,
        OcrRecognition recognized,
        HoverOcrResult? selection,
        float cursorX,
        float cursorY,
        Drawing.Rectangle bounds)
    {
        string? directory = Environment.GetEnvironmentVariable("ASTERIA_OCR_TRACE_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
            int sequence = Interlocked.Increment(ref _traceSequence);
            string prefix = Path.Combine(directory, $"ocr-{sequence:D3}");
            bitmap.Save(prefix + ".png", DrawingImaging.ImageFormat.Png);
            File.WriteAllText(
                prefix + ".txt",
                DescribeRecognition(recognized, selection, cursorX, cursorY, bounds),
                Encoding.UTF8);
        }
        catch
        {
            // Optional diagnostics must not interfere with hover translation.
        }
    }

    private OcrRecognition? RecognizeBitmap(
        Drawing.Bitmap bitmap,
        CancellationToken cancellationToken) =>
        RecognizePixels(ReadBgraPixels(bitmap), bitmap.Width, bitmap.Height, cancellationToken);

    private OcrRecognition? RecognizePixels(
        byte[] pixels,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using DataWriter writer = new();
        writer.WriteBytes(pixels);
        IBuffer buffer = writer.DetachBuffer();
        using SoftwareBitmap softwareBitmap = SoftwareBitmap.CreateCopyFromBuffer(
            buffer,
            BitmapPixelFormat.Bgra8,
            width,
            height,
            BitmapAlphaMode.Premultiplied);
        using ImageBuffer imageBuffer = ImageBuffer.CreateForSoftwareBitmap(softwareBitmap);
        RecognizedText? recognized = _recognizer!.RecognizeTextFromImage(imageBuffer);
        return recognized is null ? null : ExtractRecognition(recognized);
    }

    private static (double Left, double Top, double Right, double Bottom) BoundsOf(
        RecognizedTextBoundingBox box) =>
        (
            Math.Min(Math.Min(box.TopLeft.X, box.TopRight.X), Math.Min(box.BottomLeft.X, box.BottomRight.X)),
            Math.Min(Math.Min(box.TopLeft.Y, box.TopRight.Y), Math.Min(box.BottomLeft.Y, box.BottomRight.Y)),
            Math.Max(Math.Max(box.TopLeft.X, box.TopRight.X), Math.Max(box.BottomLeft.X, box.BottomRight.X)),
            Math.Max(Math.Max(box.TopLeft.Y, box.TopRight.Y), Math.Max(box.BottomLeft.Y, box.BottomRight.Y))
        );

    private static OcrRecognition ExtractRecognition(RecognizedText recognized)
    {
        List<OcrLineWords> lines = [];
        var recognizedLines = recognized.Lines;
        if (recognizedLines is null)
        {
            return new OcrRecognition(lines);
        }

        foreach (var line in recognizedLines)
        {
            if (line?.Words is null)
            {
                continue;
            }

            List<OcrWordGeometry> words = [];
            foreach (var word in line.Words.Where(word => word is not null))
            {
                var boundingBox = word.BoundingBox;
                (double left, double top, double right, double bottom) = BoundsOf(boundingBox);
                if (right <= left || bottom <= top)
                {
                    continue;
                }

                words.Add(new OcrWordGeometry(
                    Normalize(word.Text),
                    left,
                    top,
                    right,
                    bottom,
                    word.MatchConfidence));
            }

            string text = NormalizeLong(line.Text, 1200);
            if (words.Count > 0 && text.Length > 0)
            {
                lines.Add(new OcrLineWords(text, words));
            }
        }

        return new OcrRecognition(lines);
    }

    private static Capture CaptureAround(NativeMethods.Point cursor, int requestedWidth, int requestedHeight)
    {
        Drawing.Rectangle desktop = System.Windows.Forms.SystemInformation.VirtualScreen;
        Drawing.Rectangle bounds = CalculateCaptureBounds(desktop, cursor, requestedWidth, requestedHeight);

        Drawing.Bitmap bitmap = new(bounds.Width, bounds.Height, DrawingImaging.PixelFormat.Format32bppArgb);
        using Drawing.Graphics graphics = Drawing.Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.Location, Drawing.Point.Empty, bounds.Size, Drawing.CopyPixelOperation.SourceCopy);
        return new Capture(bitmap, bounds);
    }

    private static Capture CaptureSentenceContext(
        NativeMethods.Point cursor,
        Drawing.Rectangle? reusableBounds)
    {
        Drawing.Rectangle monitorBounds = MonitorBoundsAt(cursor);
        return CaptureBounds(CalculateSentenceCaptureBounds(monitorBounds, cursor, reusableBounds));
    }

    private static Drawing.Rectangle MonitorBoundsAt(NativeMethods.Point cursor)
    {
        nint monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MonitorDefaultToNearest);
        NativeMethods.MonitorInfo info = new() { Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (monitor != nint.Zero && NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return Drawing.Rectangle.FromLTRB(
                info.Monitor.Left,
                info.Monitor.Top,
                info.Monitor.Right,
                info.Monitor.Bottom);
        }

        return System.Windows.Forms.SystemInformation.VirtualScreen;
    }

    private static Capture CaptureBounds(Drawing.Rectangle bounds)
    {
        Drawing.Bitmap bitmap = new(bounds.Width, bounds.Height, DrawingImaging.PixelFormat.Format32bppArgb);
        using Drawing.Graphics graphics = Drawing.Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.Location, Drawing.Point.Empty, bounds.Size, Drawing.CopyPixelOperation.SourceCopy);
        return new Capture(bitmap, bounds);
    }

    private Drawing.Rectangle? GetReusableSentenceBounds(NativeMethods.Point cursor)
    {
        lock (_hoverCacheLock)
        {
            return _hoverCache is not null && IsInsideReuseArea(_hoverCache.Bounds, cursor)
                ? _hoverCache.Bounds
                : null;
        }
    }

    private bool TryGetCachedRecognition(
        Drawing.Rectangle bounds,
        ulong fingerprint,
        DateTimeOffset now,
        out OcrRecognition? recognition,
        out string? confirmedSpeakerName)
    {
        lock (_hoverCacheLock)
        {
            if (_hoverCache is not null &&
                CanReuseCachedRecognition(
                    _hoverCache.Bounds,
                    _hoverCache.Fingerprint,
                    _hoverCache.CreatedAt,
                    bounds,
                    fingerprint,
                    now))
            {
                recognition = _hoverCache.Recognition;
                confirmedSpeakerName = _hoverCache.ConfirmedSpeakerName;
                return true;
            }

            recognition = null;
            confirmedSpeakerName = null;
            return false;
        }
    }

    private void StoreCachedRecognition(
        Drawing.Rectangle bounds,
        ulong fingerprint,
        OcrRecognition recognition,
        string? confirmedSpeakerName,
        DateTimeOffset now)
    {
        lock (_hoverCacheLock)
        {
            _hoverCache = new HoverRecognitionCacheEntry(
                bounds,
                fingerprint,
                now,
                recognition,
                confirmedSpeakerName);
        }
    }

    private static byte[] ReadBgraPixels(Drawing.Bitmap bitmap)
    {
        Drawing.Bitmap? converted = null;
        Drawing.Bitmap source = bitmap;
        if (bitmap.PixelFormat != DrawingImaging.PixelFormat.Format32bppArgb)
        {
            converted = new Drawing.Bitmap(
                bitmap.Width,
                bitmap.Height,
                DrawingImaging.PixelFormat.Format32bppArgb);
            using Drawing.Graphics graphics = Drawing.Graphics.FromImage(converted);
            graphics.DrawImageUnscaled(bitmap, Drawing.Point.Empty);
            source = converted;
        }

        Drawing.Rectangle rectangle = new(0, 0, source.Width, source.Height);
        DrawingImaging.BitmapData? data = null;
        try
        {
            data = source.LockBits(
                rectangle,
                DrawingImaging.ImageLockMode.ReadOnly,
                DrawingImaging.PixelFormat.Format32bppArgb);
            int rowBytes = checked(source.Width * 4);
            byte[] pixels = GC.AllocateUninitializedArray<byte>(checked(rowBytes * source.Height));
            int stride = data.Stride;
            for (int y = 0; y < source.Height; y++)
            {
                nint row = stride >= 0
                    ? data.Scan0 + (y * stride)
                    : data.Scan0 + ((source.Height - 1 - y) * -stride);
                Marshal.Copy(row, pixels, y * rowBytes, rowBytes);
            }

            return pixels;
        }
        finally
        {
            if (data is not null)
            {
                source.UnlockBits(data);
            }

            converted?.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_hoverCacheLock)
        {
            _hoverCache = null;
        }

        _recognizer?.Dispose();
        _recognizer = null;
        _initializeLock.Dispose();
    }

    private sealed record Capture(Drawing.Bitmap Bitmap, Drawing.Rectangle Bounds) : IDisposable
    {
        public void Dispose() => Bitmap.Dispose();
    }

    // The confirmed name travels with the recognition because agreement was
    // established between two captures that no later hover can re-observe.
    private sealed record HoverRecognitionCacheEntry(
        Drawing.Rectangle Bounds,
        ulong Fingerprint,
        DateTimeOffset CreatedAt,
        OcrRecognition Recognition,
        string? ConfirmedSpeakerName);
}
