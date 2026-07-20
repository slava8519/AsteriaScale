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

    private static int MapAxis(int value, int sourceStart, int sourceLength, int destinationStart, int destinationLength)
    {
        double normalized = (value - sourceStart) / (double)(sourceLength - 1);
        int offset = (int)Math.Round(
            normalized * (destinationLength - 1),
            MidpointRounding.AwayFromZero);
        return destinationStart + offset;
    }
}
