using AsteriaScale.Interop;

namespace AsteriaScale.Services;

/// <summary>
/// Returns the cursor position that is visible to the user. Magpie keeps the
/// system cursor in source-window coordinates while drawing it in the scaled
/// destination, so GetCursorPos alone does not identify the hovered pixel.
/// </summary>
internal static class VisibleCursorService
{
    private const string MagpieScalingWindowClass =
        "Window_Magpie_967EB565-6F73-4E94-AE53-00CC42592A22";

    internal static bool TryGetPosition(out NativeMethods.Point position)
    {
        if (!NativeMethods.GetCursorPos(out position))
        {
            return false;
        }

        nint scalingWindow = NativeMethods.FindWindow(MagpieScalingWindowClass, null);
        if (scalingWindow == nint.Zero)
        {
            return true;
        }

        nint sourceWindow = NativeMethods.GetProp(scalingWindow, "Magpie.SrcHWND");
        if (sourceWindow == nint.Zero || !NativeMethods.IsWindow(sourceWindow))
        {
            return true;
        }

        NativeMethods.Rect source = ReadRect(scalingWindow, "Magpie.Src");
        NativeMethods.Rect destination = ReadRect(scalingWindow, "Magpie.Dest");
        if (TryMapFromSourceToDestination(position, source, destination, out NativeMethods.Point mapped))
        {
            position = mapped;
        }

        return true;
    }

    /// <summary>
    /// Returns the window that owns text rendered at a visible screen point.
    /// Under Magpie the visible surface belongs to Magpie while OCR must keep
    /// using that destination point; Textractor, however, must attach to the
    /// source window recorded by Magpie.
    /// </summary>
    internal static nint GetTextSourceWindow(NativeMethods.Point visiblePoint)
    {
        nint hoveredWindow = NativeMethods.WindowFromPoint(visiblePoint);
        if (hoveredWindow == nint.Zero)
        {
            return nint.Zero;
        }

        // Prefer the property on the actual hit-test window. This remains
        // compatible if Magpie changes its generated class name while keeping
        // the documented source-window property.
        nint directSourceWindow = NativeMethods.GetProp(hoveredWindow, "Magpie.SrcHWND");
        if (IsValidSourceWindow(directSourceWindow, hoveredWindow, hoveredWindow))
        {
            return directSourceWindow;
        }

        nint scalingWindow = NativeMethods.FindWindow(MagpieScalingWindowClass, null);
        if (scalingWindow == nint.Zero ||
            !NativeMethods.IsWindow(scalingWindow) ||
            !NativeMethods.IsWindowVisible(scalingWindow))
        {
            return hoveredWindow;
        }

        nint sourceWindow = NativeMethods.GetProp(scalingWindow, "Magpie.SrcHWND");
        bool sourceWindowValid =
            IsValidSourceWindow(sourceWindow, hoveredWindow, scalingWindow);

        NativeMethods.GetWindowThreadProcessId(hoveredWindow, out uint hoveredProcessId);
        NativeMethods.GetWindowThreadProcessId(scalingWindow, out uint scalingProcessId);
        bool pointInsideDestination = PointIsInside(
            visiblePoint,
            ReadDestinationBounds(scalingWindow));

        return SelectTextSourceWindow(
            hoveredWindow,
            scalingWindow,
            sourceWindow,
            sourceWindowValid,
            hoveredProcessId,
            scalingProcessId,
            pointInsideDestination);
    }

    /// <summary>
    /// Pure selection seam for the safety cases that cannot be reproduced in
    /// CI without a live injected Magpie session.
    /// </summary>
    internal static nint SelectTextSourceWindow(
        nint hoveredWindow,
        nint scalingWindow,
        nint sourceWindow,
        bool sourceWindowValid,
        uint hoveredProcessId,
        uint scalingProcessId,
        bool pointInsideDestination)
    {
        if (!sourceWindowValid)
        {
            return hoveredWindow;
        }

        // WindowFromPoint normally returns the scaling HWND itself. A future
        // Magpie surface may use a child/companion HWND, so a window owned by
        // the same process is accepted only while the point is inside this
        // scaling session's destination rectangle.
        bool isScalingSurface =
            hoveredWindow == scalingWindow ||
            (pointInsideDestination &&
             hoveredProcessId != 0 &&
             hoveredProcessId == scalingProcessId);
        return isScalingSurface ? sourceWindow : hoveredWindow;
    }

    internal static bool TryMapFromSourceToDestination(
        NativeMethods.Point sourcePoint,
        NativeMethods.Rect source,
        NativeMethods.Rect destination,
        out NativeMethods.Point destinationPoint)
    {
        destinationPoint = sourcePoint;
        if (source.Width < 2 || source.Height < 2 ||
            destination.Width < 2 || destination.Height < 2 ||
            sourcePoint.X < source.Left || sourcePoint.X >= source.Right ||
            sourcePoint.Y < source.Top || sourcePoint.Y >= source.Bottom)
        {
            return false;
        }

        destinationPoint = new NativeMethods.Point
        {
            X = MapAxis(sourcePoint.X, source.Left, source.Width, destination.Left, destination.Width),
            Y = MapAxis(sourcePoint.Y, source.Top, source.Height, destination.Top, destination.Height)
        };
        return true;
    }

    private static NativeMethods.Rect ReadRect(nint window, string prefix) => new()
    {
        Left = ReadIntProperty(window, prefix + "Left"),
        Top = ReadIntProperty(window, prefix + "Top"),
        Right = ReadIntProperty(window, prefix + "Right"),
        Bottom = ReadIntProperty(window, prefix + "Bottom")
    };

    private static int ReadIntProperty(nint window, string name) =>
        unchecked((int)NativeMethods.GetProp(window, name).ToInt64());

    private static NativeMethods.Rect ReadDestinationBounds(nint scalingWindow)
    {
        NativeMethods.Rect destination = ReadRect(scalingWindow, "Magpie.Dest");
        return destination.Width > 0 && destination.Height > 0
            ? destination
            : NativeMethods.GetWindowRect(scalingWindow, out NativeMethods.Rect bounds)
                ? bounds
                : default;
    }

    private static bool PointIsInside(NativeMethods.Point point, NativeMethods.Rect bounds) =>
        bounds.Width > 0 &&
        bounds.Height > 0 &&
        point.X >= bounds.Left &&
        point.X < bounds.Right &&
        point.Y >= bounds.Top &&
        point.Y < bounds.Bottom;

    private static bool IsValidSourceWindow(
        nint sourceWindow,
        nint hoveredWindow,
        nint scalingWindow) =>
        sourceWindow != nint.Zero &&
        sourceWindow != hoveredWindow &&
        sourceWindow != scalingWindow &&
        NativeMethods.IsWindow(sourceWindow);

    private static int MapAxis(int value, int sourceStart, int sourceLength, int destinationStart, int destinationLength)
    {
        double normalized = (value - sourceStart) / (double)(sourceLength - 1);
        int offset = (int)Math.Round(
            normalized * (destinationLength - 1),
            MidpointRounding.AwayFromZero);
        return destinationStart + offset;
    }
}
