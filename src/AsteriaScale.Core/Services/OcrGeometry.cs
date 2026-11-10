using Drawing = System.Drawing;
using System.Text;
using static AsteriaScale.Services.OcrTextRules;

namespace AsteriaScale.Services;

/// <summary>
/// The pixel half of hover OCR, moved verbatim from the Windows AI recognizer
/// (owner decision M12; roadmap 20.3, docs/MACOS.md T13): the line and word
/// nearest the cursor, the sentence and speaker nameplate assembled from line
/// geometry, screen block grouping and the capture bounds with their reuse
/// area. They live in Core beside <see cref="OcrTextRules"/> so that every
/// host selects text with the same thresholds instead of keeping a second
/// copy. Line and word boxes, and the cursor SelectNearPoint takes, are pixels
/// of the recognized frame (origin top-left, y down). The monitor, the capture
/// bounds and the cursor the capture-bound helpers take are whole pixels in
/// the host's screen space, negative on a display left of or above the
/// primary one.
/// The thresholds (95 px, 2400x720, the 120/80 reuse margins) are not
/// rescaled, so a host passes device pixels, not points (docs/MACOS.md T18).
/// </summary>
internal static class OcrGeometry
{
    private const int SentenceCaptureMaximumWidth = 2400;
    private const int SentenceCaptureMaximumHeight = 720;

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

    internal static IReadOnlyList<OcrLineGeometry> ExtractLineGeometries(OcrRecognition recognized)
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

    internal static HoverOcrResult? SelectNearPoint(
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
                int characterIndex = ResolveLineCharacterIndex(
                    text,
                    closestWord,
                    cursorX,
                    left,
                    right,
                    closestWordLeft,
                    closestWordRight);
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
            captureBounds)
        {
            CurrentSpeaker = context.CurrentSpeaker
        };
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
        List<OcrLineGeometry> block = [selected];
        HashSet<OcrLineGeometry> included = [selected];
        OcrLineGeometry first = selected;
        OcrLineGeometry last = selected;
        while (FindAdjacentLine(lines, included, first, above: true) is { } previous)
        {
            block.Add(previous);
            included.Add(previous);
            first = previous;
        }

        while (FindAdjacentLine(lines, included, last, above: false) is { } next)
        {
            block.Add(next);
            included.Add(next);
            last = next;
        }

        List<OcrLineGeometry> ordered = block
            .OrderBy(line => line.Top)
            .ThenBy(line => line.Left)
            .ToList();
        OcrLineGeometry? speakerLine = FindSpeakerLineAboveBlock(
            lines,
            included,
            ordered);
        CurrentSpeakerContext? geometrySpeaker = null;
        if (speakerLine is not null &&
            SpeakerMetadataParser.TryParseStandaloneName(
                Normalize(speakerLine.Text),
                out string speakerName))
        {
            geometrySpeaker = new CurrentSpeakerContext(speakerName);
        }

        StringBuilder merged = new();
        int mergedAnchor = 0;
        for (int index = 0; index < ordered.Count; index++)
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

        SentenceContext extracted = ExtractSentence(merged.ToString(), mergedAnchor);
        return extracted.CurrentSpeaker is not null || geometrySpeaker is null
            ? extracted
            : extracted with { CurrentSpeaker = geometrySpeaker };
    }

    private static OcrLineGeometry? FindSpeakerLineAboveBlock(
        IReadOnlyList<OcrLineGeometry> lines,
        IReadOnlySet<OcrLineGeometry> included,
        IReadOnlyList<OcrLineGeometry> orderedBlock)
    {
        if (orderedBlock.Count == 0)
        {
            return null;
        }

        OcrLineGeometry first = orderedBlock[0];
        double dialogueHeight = Math.Max(1, first.Bottom - first.Top);
        double blockLeft = orderedBlock.Min(line => line.Left);
        double blockRight = orderedBlock.Max(line => line.Right);
        double maximumVerticalGap = Math.Max(120, dialogueHeight * 2.5);
        double maximumHorizontalGap = Math.Max(180, dialogueHeight * 4);

        return lines
            .Where(candidate =>
                !included.Contains(candidate) &&
                candidate.Bottom <= first.Top &&
                first.Top - candidate.Bottom <= maximumVerticalGap &&
                SpeakerMetadataParser.TryParseStandaloneName(
                    Normalize(candidate.Text),
                    out _) &&
                IsPlausibleSpeakerGeometry(
                    candidate,
                    dialogueHeight,
                    blockLeft,
                    blockRight,
                    maximumHorizontalGap))
            .OrderBy(candidate => first.Top - candidate.Bottom)
            .ThenBy(candidate => Math.Abs(candidate.Left - blockLeft))
            .FirstOrDefault();
    }

    private static bool IsPlausibleSpeakerGeometry(
        OcrLineGeometry candidate,
        double dialogueHeight,
        double blockLeft,
        double blockRight,
        double maximumHorizontalGap)
    {
        double candidateHeight = Math.Max(1, candidate.Bottom - candidate.Top);
        double heightRatio = candidateHeight / dialogueHeight;
        if (heightRatio is < 0.45 or > 1.8)
        {
            return false;
        }

        double horizontalGap = candidate.Right < blockLeft
            ? blockLeft - candidate.Right
            : candidate.Left > blockRight
                ? candidate.Left - blockRight
                : 0;
        return horizontalGap <= maximumHorizontalGap;
    }

    private static OcrLineGeometry? FindAdjacentLine(
        IReadOnlyList<OcrLineGeometry> lines,
        IReadOnlySet<OcrLineGeometry> included,
        OcrLineGeometry edge,
        bool above)
    {
        double edgeCenter = (edge.Top + edge.Bottom) / 2;
        return lines
            .Where(candidate =>
                !included.Contains(candidate) &&
                (above
                    ? (candidate.Top + candidate.Bottom) / 2 < edgeCenter &&
                      LinesBelongToSameBlock(candidate, edge)
                    : (candidate.Top + candidate.Bottom) / 2 > edgeCenter &&
                      LinesBelongToSameBlock(edge, candidate)))
            .OrderBy(candidate => Math.Abs(
                (candidate.Top + candidate.Bottom) / 2 - edgeCenter))
            .ThenBy(candidate => Math.Abs(candidate.Left - edge.Left))
            .FirstOrDefault();
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

    internal static Drawing.Rectangle CalculateCaptureBounds(
        Drawing.Rectangle desktop,
        Drawing.Point cursor,
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
        Drawing.Point cursor,
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

    internal static bool IsInsideReuseArea(Drawing.Rectangle bounds, Drawing.Point cursor)
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
