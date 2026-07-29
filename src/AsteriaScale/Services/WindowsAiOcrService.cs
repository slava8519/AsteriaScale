using Drawing = System.Drawing;
using DrawingImaging = System.Drawing.Imaging;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Graphics.Imaging;
using Microsoft.Windows.AI;
using Microsoft.Windows.AI.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using AsteriaScale.Interop;

namespace AsteriaScale.Services;

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

internal sealed record SentenceContext(string Text, int CharacterIndex);

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

internal sealed partial class WindowsAiOcrService : IDisposable
{
    private const int SentenceCaptureMaximumWidth = 2400;
    private const int SentenceCaptureMaximumHeight = 720;
    private static readonly TimeSpan HoverCacheLifetime = TimeSpan.FromSeconds(30);
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
        bool cacheHit = sentenceContext &&
                        TryGetCachedRecognition(
                            firstCapture.Bounds,
                            firstFingerprint,
                            DateTimeOffset.UtcNow,
                            out firstRecognition);
        if (cacheHit)
        {
            HoverOcrResult? cachedSelection = SelectRecognitionNearCursor(
                firstRecognition,
                cursor,
                firstCapture.Bounds,
                sentenceContext);
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

    internal static HoverOcrResult? StabilizeSentenceContext(
        HoverOcrResult? first,
        HoverOcrResult? second,
        out bool useSecondObservation)
    {
        useSecondObservation = first is null;
        if (first is null)
        {
            return second is null
                ? null
                : second with { IsSentenceContextStable = false };
        }

        if (second is null ||
            !string.Equals(
                LocalDictionaryService.NormalizeEnglish(first.HoveredWord),
                LocalDictionaryService.NormalizeEnglish(second.HoveredWord),
                StringComparison.Ordinal))
        {
            useSecondObservation = false;
            return first with { IsSentenceContextStable = false };
        }

        string[] firstTokens = SentenceComparisonTokens(first.Text);
        string[] secondTokens = SentenceComparisonTokens(second.Text);
        if (firstTokens.Length == 0 || secondTokens.Length == 0)
        {
            useSecondObservation = false;
            return first with { IsSentenceContextStable = false };
        }

        bool firstFitsSecond = IsOrderedSubsequence(firstTokens, secondTokens);
        bool secondFitsFirst = IsOrderedSubsequence(secondTokens, firstTokens);
        if (!firstFitsSecond && !secondFitsFirst)
        {
            useSecondObservation = false;
            return first with { IsSentenceContextStable = false };
        }

        useSecondObservation = firstFitsSecond &&
                               (!secondFitsFirst || second.Text.Length > first.Text.Length);
        HoverOcrResult selected = useSecondObservation ? second : first;
        return selected with { IsSentenceContextStable = true };
    }

    private static string[] SentenceComparisonTokens(string value) =>
        SentenceComparisonTokenRegex()
            .Matches((value ?? string.Empty).Normalize(NormalizationForm.FormC))
            .Select(match => LocalDictionaryService.NormalizeEnglish(match.Value))
            .ToArray();

    private static bool IsOrderedSubsequence(
        IReadOnlyList<string> candidate,
        IReadOnlyList<string> sequence)
    {
        if (candidate.Count > sequence.Count)
        {
            return false;
        }

        int candidateIndex = 0;
        foreach (string token in sequence)
        {
            if (candidateIndex < candidate.Count &&
                string.Equals(candidate[candidateIndex], token, StringComparison.Ordinal))
            {
                candidateIndex++;
            }
        }

        return candidateIndex == candidate.Count;
    }

    [GeneratedRegex(
        @"[\p{L}\p{M}\p{N}]+(?:['’\-][\p{L}\p{M}\p{N}]+)*",
        RegexOptions.CultureInvariant)]
    private static partial Regex SentenceComparisonTokenRegex();

    internal async Task<ScreenOcrResult> RecognizeScreenAsync(
        nint preferredWindow,
        CancellationToken cancellationToken)
    {
        await EnsureReadyAsync();
        cancellationToken.ThrowIfCancellationRequested();

        Drawing.Rectangle bounds = MonitorBoundsForWindow(preferredWindow);
        using Capture capture = CaptureBounds(bounds);
        OcrRecognition? recognized = RecognizeBitmap(capture.Bitmap, cancellationToken);
        if (recognized is null || recognized.Lines.Count == 0)
        {
            return new ScreenOcrResult(bounds, []);
        }

        return new ScreenOcrResult(bounds, BuildScreenBlocks(ExtractLineGeometries(recognized), bounds));
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

    internal static IReadOnlyList<ScreenOcrBlock> BuildScreenBlocks(
        IReadOnlyList<OcrLineGeometry> lines,
        Drawing.Rectangle captureBounds)
    {
        List<MutableScreenBlock> blocks = [];
        foreach (OcrLineGeometry line in lines
                     .Where(line => LooksTranslatable(line.Text))
                     .OrderBy(line => line.Top)
                     .ThenBy(line => line.Left))
        {
            MutableScreenBlock? best = null;
            double bestScore = double.MaxValue;
            foreach (MutableScreenBlock block in blocks)
            {
                OcrLineGeometry previous = block.Lines[^1];
                if (!LinesBelongToSameBlock(previous, line))
                {
                    continue;
                }

                double verticalGap = Math.Max(0, line.Top - previous.Bottom);
                double horizontalShift = Math.Abs(line.Left - previous.Left);
                double score = (verticalGap * 3) + horizontalShift;
                if (score < bestScore)
                {
                    best = block;
                    bestScore = score;
                }
            }

            if (best is null)
            {
                blocks.Add(new MutableScreenBlock(line));
            }
            else
            {
                best.Add(line);
            }
        }

        List<ScreenOcrBlock> result = [];
        foreach (MutableScreenBlock block in blocks)
        {
            string text = NormalizeLong(string.Join(" ", block.Lines.Select(line => line.Text)), 1800);
            int letters = text.Count(char.IsLetter);
            if (letters < 2)
            {
                continue;
            }

            int left = captureBounds.Left + (int)Math.Floor(block.Left);
            int top = captureBounds.Top + (int)Math.Floor(block.Top);
            int right = captureBounds.Left + (int)Math.Ceiling(block.Right);
            int bottom = captureBounds.Top + (int)Math.Ceiling(block.Bottom);
            Drawing.Rectangle rectangle = Drawing.Rectangle.FromLTRB(left, top, right, bottom);
            if (rectangle.Width < 8 || rectangle.Height < 6)
            {
                continue;
            }

            result.Add(new ScreenOcrBlock(text, rectangle, block.Lines.Count));
        }

        return result
            .OrderBy(block => block.Bounds.Top)
            .ThenBy(block => block.Bounds.Left)
            .Take(24)
            .ToArray();
    }

    private static IReadOnlyList<OcrLineGeometry> ExtractLineGeometries(OcrRecognition recognized)
    {
        List<OcrLineGeometry> lines = [];
        foreach (OcrLineWords line in recognized.Lines)
        {
            string text = NormalizeLong(line.Text, 1200);
            if (line.Words.Count == 0 || !LooksTranslatable(text))
            {
                continue;
            }

            double left = double.MaxValue;
            double top = double.MaxValue;
            double right = double.MinValue;
            double bottom = double.MinValue;
            foreach (OcrWordGeometry word in line.Words)
            {
                left = Math.Min(left, word.Left);
                top = Math.Min(top, word.Top);
                right = Math.Max(right, word.Right);
                bottom = Math.Max(bottom, word.Bottom);
            }

            if (right > left && bottom > top)
            {
                lines.Add(new OcrLineGeometry(text, left, top, right, bottom));
            }
        }

        return lines;
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

    private static HoverOcrResult? SelectNearPoint(
        OcrRecognition result,
        float cursorX,
        float cursorY,
        Drawing.Rectangle captureBounds,
        bool sentenceContext)
    {
        List<LineSelection> candidates = [];

        foreach (OcrLineWords line in result.Lines)
        {
            if (line.Words.Count == 0)
            {
                continue;
            }

            double left = double.MaxValue;
            double top = double.MaxValue;
            double right = double.MinValue;
            double bottom = double.MinValue;
            string closestWord = string.Empty;
            double closestWordLeft = 0;
            double closestWordRight = 0;
            double closestWordDistance = double.MaxValue;
            foreach (OcrWordGeometry word in line.Words)
            {
                double wordLeft = word.Left;
                double wordTop = word.Top;
                double wordRight = word.Right;
                double wordBottom = word.Bottom;
                left = Math.Min(left, word.Left);
                top = Math.Min(top, word.Top);
                right = Math.Max(right, word.Right);
                bottom = Math.Max(bottom, word.Bottom);

                double wordDx = cursorX < wordLeft ? wordLeft - cursorX : cursorX > wordRight ? cursorX - wordRight : 0;
                double wordDy = cursorY < wordTop ? wordTop - cursorY : cursorY > wordBottom ? cursorY - wordBottom : 0;
                double wordDistance = (wordDx * wordDx) + (wordDy * wordDy);
                if (wordDistance < closestWordDistance)
                {
                    closestWordDistance = wordDistance;
                    closestWord = Normalize(word.Text);
                    closestWordLeft = wordLeft;
                    closestWordRight = wordRight;
                }
            }

            double dx = cursorX < left ? left - cursorX : cursorX > right ? cursorX - right : 0;
            double dy = cursorY < top ? top - cursorY : cursorY > bottom ? cursorY - bottom : 0;
            double distance = (dx * dx) + (dy * dy * 1.6);
            string text = Normalize(line.Text);
            if (LooksTranslatable(text))
            {
                int approximateIndex = Math.Clamp(
                    (int)(((cursorX - left) / Math.Max(1, right - left)) * text.Length),
                    0,
                    text.Length - 1);
                int wordStart = FindClosestOccurrence(text, closestWord, approximateIndex);
                double wordRatio = closestWordRight > closestWordLeft
                    ? Math.Clamp((cursorX - closestWordLeft) / (closestWordRight - closestWordLeft), 0, 0.999)
                    : 0;
                int wordOffset = closestWord.Length == 0
                    ? 0
                    : Math.Clamp((int)(wordRatio * closestWord.Length), 0, closestWord.Length - 1);
                int characterIndex = wordStart >= 0
                    ? wordStart + wordOffset
                    : approximateIndex;
                candidates.Add(new LineSelection(
                    new OcrLineGeometry(text, left, top, right, bottom),
                    closestWord,
                    characterIndex,
                    distance));
            }
        }

        const double maximumDistance = 95 * 95;
        LineSelection? selected = candidates.MinBy(candidate => candidate.Distance);
        if (selected is null || selected.Distance > maximumDistance)
        {
            return null;
        }

        if (!sentenceContext)
        {
            return new HoverOcrResult(
                selected.Geometry.Text,
                selected.HoveredWord,
                selected.CharacterIndex,
                captureBounds);
        }

        IReadOnlyList<OcrLineGeometry> geometries = candidates
            .Select(candidate => candidate.Geometry)
            .ToArray();
        int selectedLineIndex = candidates.IndexOf(selected);
        SentenceContext context = BuildSentenceContext(
            geometries,
            selectedLineIndex,
            selected.CharacterIndex);
        return new HoverOcrResult(
            context.Text,
            selected.HoveredWord,
            context.CharacterIndex,
            captureBounds);
    }

    internal static SentenceContext BuildSentenceContext(
        IReadOnlyList<OcrLineGeometry> lines,
        int selectedLineIndex,
        int selectedCharacterIndex)
    {
        if (lines.Count == 0 || selectedLineIndex < 0 || selectedLineIndex >= lines.Count)
        {
            return new SentenceContext(string.Empty, 0);
        }

        OcrLineGeometry selected = lines[selectedLineIndex];
        List<OcrLineGeometry> ordered = lines
            .OrderBy(line => line.Top)
            .ThenBy(line => line.Left)
            .ToList();
        int anchor = ordered.IndexOf(selected);
        int first = anchor;
        int last = anchor;
        while (first > 0 && LinesBelongToSameBlock(ordered[first - 1], ordered[first]))
        {
            first--;
        }

        while (last + 1 < ordered.Count && LinesBelongToSameBlock(ordered[last], ordered[last + 1]))
        {
            last++;
        }

        StringBuilder merged = new();
        int mergedAnchor = 0;
        for (int index = first; index <= last; index++)
        {
            string text = Normalize(ordered[index].Text);
            if (text.Length == 0)
            {
                continue;
            }

            if (merged.Length > 0)
            {
                merged.Append(' ');
            }

            int lineStart = merged.Length;
            merged.Append(text);
            if (ReferenceEquals(ordered[index], selected) || ordered[index] == selected)
            {
                mergedAnchor = lineStart + Math.Clamp(selectedCharacterIndex, 0, text.Length - 1);
            }
        }

        return ExtractSentence(merged.ToString(), mergedAnchor);
    }

    internal static SentenceContext ExtractSentence(string text, int characterIndex)
    {
        text = Normalize(text);
        if (text.Length == 0)
        {
            return new SentenceContext(string.Empty, 0);
        }

        int anchor = Math.Clamp(characterIndex, 0, text.Length - 1);
        (int start, int end) = SentenceBoundaryRules.FindContainingSpan(
            text,
            anchor,
            anchor);
        while (end < text.Length && IsClosingQuote(text[end]))
        {
            end++;
        }

        while (start < end && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        while (end > start && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        string sentence = text[start..end];
        if (sentence.Length > 420)
        {
            int relativeAnchor = Math.Clamp(anchor - start, 0, sentence.Length - 1);
            int windowStart = Math.Clamp(relativeAnchor - 210, 0, sentence.Length - 420);
            sentence = sentence.Substring(windowStart, 420).Trim();
            start += windowStart;
        }

        return new SentenceContext(
            sentence,
            sentence.Length == 0 ? 0 : Math.Clamp(anchor - start, 0, sentence.Length - 1));
    }

    private static bool LinesBelongToSameBlock(OcrLineGeometry upper, OcrLineGeometry lower)
    {
        double upperHeight = Math.Max(1, upper.Bottom - upper.Top);
        double lowerHeight = Math.Max(1, lower.Bottom - lower.Top);
        double heightRatio = Math.Min(upperHeight, lowerHeight) / Math.Max(upperHeight, lowerHeight);
        if (heightRatio < 0.55)
        {
            return false;
        }

        double verticalGap = lower.Top - upper.Bottom;
        double maximumGap = Math.Max(24, Math.Min(upperHeight, lowerHeight) * 0.9);
        if (verticalGap < -Math.Min(upperHeight, lowerHeight) * 0.45 || verticalGap > maximumGap)
        {
            return false;
        }

        double upperWidth = Math.Max(1, upper.Right - upper.Left);
        double lowerWidth = Math.Max(1, lower.Right - lower.Left);
        double widthRatio = Math.Min(upperWidth, lowerWidth) / Math.Max(upperWidth, lowerWidth);
        if (widthRatio < 0.22 && verticalGap > Math.Min(upperHeight, lowerHeight) * 0.35)
        {
            return false;
        }

        double overlap = Math.Max(0, Math.Min(upper.Right, lower.Right) - Math.Max(upper.Left, lower.Left));
        double narrowerWidth = Math.Min(upperWidth, lowerWidth);
        double leftTolerance = Math.Max(160, Math.Max(upperHeight, lowerHeight) * 3);
        return overlap / narrowerWidth >= 0.35 || Math.Abs(upper.Left - lower.Left) <= leftTolerance;
    }

    private static bool IsClosingQuote(char character) =>
        character is '"' or '\'' or '\u2019' or '\u201D' or '\u00BB' or '\u300D' or '\u300F' or '\u3011';

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

    private static int FindClosestOccurrence(string text, string word, int approximateIndex)
    {
        if (word.Length == 0)
        {
            return -1;
        }

        int bestIndex = -1;
        int bestDistance = int.MaxValue;
        int searchFrom = 0;
        while (searchFrom < text.Length)
        {
            int index = text.IndexOf(word, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                break;
            }

            int distance = Math.Abs((index + (word.Length / 2)) - approximateIndex);
            if (distance < bestDistance)
            {
                bestIndex = index;
                bestDistance = distance;
            }

            searchFrom = index + Math.Max(1, word.Length);
        }

        return bestIndex;
    }

    internal static Drawing.Rectangle CalculateCaptureBounds(
        Drawing.Rectangle desktop,
        NativeMethods.Point cursor,
        int requestedWidth,
        int requestedHeight)
    {
        if (desktop.Width <= 0 || desktop.Height <= 0)
        {
            return Drawing.Rectangle.Empty;
        }

        int width = Math.Min(requestedWidth, desktop.Width);
        int height = Math.Min(requestedHeight, desktop.Height);
        int left = Math.Clamp(cursor.X - (width / 2), desktop.Left, desktop.Right - width);
        int top = Math.Clamp(cursor.Y - (height / 2), desktop.Top, desktop.Bottom - height);
        return new Drawing.Rectangle(left, top, width, height);
    }

    internal static Drawing.Rectangle CalculateSentenceCaptureBounds(
        Drawing.Rectangle monitorBounds,
        NativeMethods.Point cursor,
        Drawing.Rectangle? reusableBounds = null)
    {
        if (reusableBounds is Drawing.Rectangle previous &&
            monitorBounds.Contains(previous) &&
            IsInsideReuseArea(previous, cursor))
        {
            return previous;
        }

        return CalculateCaptureBounds(
            monitorBounds,
            cursor,
            SentenceCaptureMaximumWidth,
            SentenceCaptureMaximumHeight);
    }

    private static bool IsInsideReuseArea(Drawing.Rectangle bounds, NativeMethods.Point cursor)
    {
        int horizontalMargin = Math.Min(120, Math.Max(0, bounds.Width / 6));
        int verticalMargin = Math.Min(80, Math.Max(0, bounds.Height / 6));
        Drawing.Rectangle reuseArea = Drawing.Rectangle.FromLTRB(
            bounds.Left + horizontalMargin,
            bounds.Top + verticalMargin,
            bounds.Right - horizontalMargin,
            bounds.Bottom - verticalMargin);
        return reuseArea.Contains(cursor.X, cursor.Y);
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

    private static Drawing.Rectangle MonitorBoundsForWindow(nint window)
    {
        nint monitor = window != nint.Zero && NativeMethods.IsWindow(window)
            ? NativeMethods.MonitorFromWindow(window, NativeMethods.MonitorDefaultToNearest)
            : nint.Zero;
        if (monitor == nint.Zero && NativeMethods.GetCursorPos(out NativeMethods.Point cursor))
        {
            monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MonitorDefaultToNearest);
        }

        NativeMethods.MonitorInfo info = new()
        {
            Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>()
        };
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

    internal static ulong ComputeFrameFingerprint(ReadOnlySpan<byte> pixels)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(pixels, digest);
        return BinaryPrimitives.ReadUInt64LittleEndian(digest);
    }

    internal static bool CanReuseCachedRecognition(
        Drawing.Rectangle cachedBounds,
        ulong cachedFingerprint,
        DateTimeOffset cachedAt,
        Drawing.Rectangle currentBounds,
        ulong currentFingerprint,
        DateTimeOffset now) =>
        cachedBounds == currentBounds &&
        cachedFingerprint == currentFingerprint &&
        now >= cachedAt &&
        now - cachedAt <= HoverCacheLifetime;

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
        out OcrRecognition? recognition)
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
                return true;
            }

            recognition = null;
            return false;
        }
    }

    private void StoreCachedRecognition(
        Drawing.Rectangle bounds,
        ulong fingerprint,
        OcrRecognition recognition,
        DateTimeOffset now)
    {
        lock (_hoverCacheLock)
        {
            _hoverCache = new HoverRecognitionCacheEntry(bounds, fingerprint, now, recognition);
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

    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string normalized = RepairMixedScriptText(
            string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        return normalized.Length <= 420 ? normalized : normalized[..420];
    }

    private static string NormalizeLong(string? text, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string normalized = RepairMixedScriptText(
            string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }

    private static readonly Dictionary<char, char> CyrillicToLatinHomoglyphs = new()
    {
        ['а'] = 'a',
        ['е'] = 'e',
        ['о'] = 'o',
        ['р'] = 'p',
        ['с'] = 'c',
        ['у'] = 'y',
        ['х'] = 'x',
        ['к'] = 'k',
        ['і'] = 'i',
        ['ѕ'] = 's',
        ['А'] = 'A',
        ['В'] = 'B',
        ['Е'] = 'E',
        ['К'] = 'K',
        ['М'] = 'M',
        ['Н'] = 'H',
        ['О'] = 'O',
        ['Р'] = 'P',
        ['С'] = 'C',
        ['Т'] = 'T',
        ['У'] = 'Y',
        ['Х'] = 'X'
    };

    private static readonly Dictionary<char, char> LatinToCyrillicHomoglyphs = new()
    {
        ['a'] = 'а',
        ['e'] = 'е',
        ['o'] = 'о',
        ['p'] = 'р',
        ['c'] = 'с',
        ['y'] = 'у',
        ['x'] = 'х',
        ['k'] = 'к',
        ['A'] = 'А',
        ['B'] = 'В',
        ['E'] = 'Е',
        ['K'] = 'К',
        ['M'] = 'М',
        ['H'] = 'Н',
        ['O'] = 'О',
        ['P'] = 'Р',
        ['C'] = 'С',
        ['T'] = 'Т',
        ['X'] = 'Х'
    };

    /// <summary>
    /// OCR on stylized fonts sometimes emits homoglyphs from the other script
    /// ("prancing аround" with a Cyrillic а breaks every dictionary key).
    /// Only tokens that mix Latin and Cyrillic are repaired, toward the
    /// dominant script; pure-Latin and pure-Cyrillic tokens are never touched.
    /// </summary>
    internal static string RepairMixedScriptText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        char[] result = text.ToCharArray();
        int index = 0;
        while (index < result.Length)
        {
            if (!char.IsLetter(result[index]))
            {
                index++;
                continue;
            }

            int start = index;
            int latin = 0;
            int cyrillic = 0;
            while (index < result.Length && char.IsLetter(result[index]))
            {
                char character = result[index];
                if (character is >= 'a' and <= 'z' or >= 'A' and <= 'Z')
                {
                    latin++;
                }
                else if (character is >= 'Ѐ' and <= 'ӿ')
                {
                    cyrillic++;
                }

                index++;
            }

            if (latin == 0 || cyrillic == 0)
            {
                continue;
            }

            Dictionary<char, char> map = latin >= cyrillic
                ? CyrillicToLatinHomoglyphs
                : LatinToCyrillicHomoglyphs;
            for (int position = start; position < index; position++)
            {
                if (map.TryGetValue(result[position], out char repaired))
                {
                    result[position] = repaired;
                }
            }
        }

        return new string(result);
    }

    private static bool LooksTranslatable(string text) =>
        text.Length > 0 && text.Any(char.IsLetter);

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

    private sealed record HoverRecognitionCacheEntry(
        Drawing.Rectangle Bounds,
        ulong Fingerprint,
        DateTimeOffset CreatedAt,
        OcrRecognition Recognition);

    private sealed record LineSelection(
        OcrLineGeometry Geometry,
        string HoveredWord,
        int CharacterIndex,
        double Distance);

    private sealed class MutableScreenBlock
    {
        internal MutableScreenBlock(OcrLineGeometry line)
        {
            Lines.Add(line);
            Left = line.Left;
            Top = line.Top;
            Right = line.Right;
            Bottom = line.Bottom;
        }

        internal List<OcrLineGeometry> Lines { get; } = [];
        internal double Left { get; private set; }
        internal double Top { get; private set; }
        internal double Right { get; private set; }
        internal double Bottom { get; private set; }

        internal void Add(OcrLineGeometry line)
        {
            Lines.Add(line);
            Left = Math.Min(Left, line.Left);
            Top = Math.Min(Top, line.Top);
            Right = Math.Max(Right, line.Right);
            Bottom = Math.Max(Bottom, line.Bottom);
        }
    }
}
