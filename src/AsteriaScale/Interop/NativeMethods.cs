using System.Runtime.InteropServices;
using System.Text;

namespace AsteriaScale.Interop;

internal static class NativeMethods
{
    internal const int SwHide = 0;
    internal const int SwShow = 5;
    internal const int SwRestore = 9;
    internal const uint GwOwner = 4;
    internal const uint MonitorDefaultToNearest = 2;
    internal const uint DwmwaCloaked = 14;
    internal const uint KeyeventfKeyup = 0x0002;
    internal const uint InputKeyboard = 1;
    internal const uint WmHotkey = 0x0312;
    internal const uint ModControl = 0x0002;
    internal const uint ModShift = 0x0004;
    internal const uint ModNoRepeat = 0x4000;
    internal const int GwlExstyle = -20;
    internal const int WsExTransparent = 0x00000020;
    internal const int WsExToolwindow = 0x00000080;
    internal const int WsExNoactivate = 0x08000000;
    internal const uint SwpNoactivate = 0x0010;
    internal const uint SwpShowwindow = 0x0040;
    internal const ushort VkMenu = 0x12;
    internal const ushort VkShift = 0x10;
    internal const ushort VkA = 0x41;
    internal const ushort VkG = 0x47;
    internal const ushort VkQ = 0x51;
    internal static readonly nint HwndMessage = new(-3);
    internal static readonly nint HwndTopmost = new(-1);

    internal delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    internal static extern int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(nint hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(nint hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    internal static extern nint GetWindow(nint hWnd, uint command);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWow64Process(
        nint processHandle,
        [MarshalAs(UnmanagedType.Bool)] out bool isWow64);

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(
        nint hWnd,
        int id,
        uint modifiers,
        uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    internal static extern nint WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(nint hWnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(nint hWnd);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetClientRect(nint hWnd, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ClientToScreen(nint hWnd, ref Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint hWnd, out Rect rect);

    [DllImport("user32.dll")]
    internal static extern nint MonitorFromPoint(Point point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint FindWindowEx(nint parent, nint childAfter, string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(nint hWnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    internal static extern nint GetProp(nint hWnd, string propertyName);

    [DllImport("user32.dll")]
    internal static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(nint hWnd, uint attribute, out int value, int valueSize);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(nint hWnd, int attribute, ref int value, int valueSize);

    [DllImport("Magnification.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool MagInitialize();

    [DllImport("Magnification.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool MagShowSystemCursor([MarshalAs(UnmanagedType.Bool)] bool showCursor);

    [DllImport("Magnification.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool MagUninitialize();

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static extern nint GetWindowLongPtr(nint hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    internal static extern nint SetWindowLongPtr(nint hWnd, int index, nint newValue);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        nint hWnd,
        nint hWndInsertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(nint hWnd);

    [DllImport("user32.dll")]
    internal static extern nint GetWindowDpiAwarenessContext(nint hWnd);

    [DllImport("user32.dll")]
    internal static extern DpiAwareness GetAwarenessFromDpiAwarenessContext(nint value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayDevices(string? device, uint deviceIndex, ref DisplayDevice displayDevice, uint flags);

    internal static string ReadWindowTitle(nint hWnd)
    {
        int length = GetWindowTextLength(hWnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        StringBuilder buffer = new(length + 1);
        GetWindowText(hWnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    internal static string ReadClassName(nint hWnd)
    {
        StringBuilder buffer = new(256);
        GetClassName(hWnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    internal static nint FindTopLevelWindow(string className, int processId)
    {
        nint result = nint.Zero;
        EnumWindows((window, _) =>
        {
            if (string.Equals(ReadClassName(window), className, StringComparison.Ordinal) &&
                GetWindowThreadProcessId(window, out uint ownerProcessId) != 0 &&
                ownerProcessId == (uint)processId)
            {
                result = window;
                return false;
            }

            return true;
        }, nint.Zero);
        return result;
    }

    internal static nint FindMessageOnlyWindow(string className, int processId)
    {
        nint current = nint.Zero;
        while ((current = FindWindowEx(HwndMessage, current, className, null)) != nint.Zero)
        {
            if (GetWindowThreadProcessId(current, out uint ownerProcessId) != 0 &&
                ownerProcessId == unchecked((uint)processId))
            {
                return current;
            }
        }

        return nint.Zero;
    }

    internal static bool IsCloaked(nint hWnd) =>
        DwmGetWindowAttribute(hWnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    internal static DpiAwareness? TryGetWindowDpiAwareness(nint hWnd)
    {
        if (hWnd == nint.Zero || !IsWindow(hWnd))
        {
            return null;
        }

        try
        {
            DpiAwareness awareness = GetAwarenessFromDpiAwarenessContext(GetWindowDpiAwarenessContext(hWnd));
            return awareness == DpiAwareness.Invalid ? null : awareness;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    internal static void FocusWindow(nint hWnd)
    {
        nint foreground = GetForegroundWindow();
        uint foregroundThread = foreground == nint.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);
        uint currentThread = GetCurrentThreadId();
        bool attached = foregroundThread != 0 && foregroundThread != currentThread &&
                        AttachThreadInput(currentThread, foregroundThread, true);

        try
        {
            if (IsIconic(hWnd))
            {
                ShowWindow(hWnd, SwRestore);
            }

            BringWindowToTop(hWnd);
            SetForegroundWindow(hWnd);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }

    internal static bool SendMagpieHotkey(bool windowed)
    {
        ushort trigger = windowed ? VkQ : VkA;
        Input[] inputs =
        [
            KeyDown(VkMenu),
            KeyDown(VkShift),
            KeyDown(trigger),
            KeyUp(trigger),
            KeyUp(VkShift),
            KeyUp(VkMenu)
        ];
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length;
    }

    internal static bool TriggerMagpieAction(
        bool windowed,
        int processId,
        bool allowKeyboardFallback)
    {
        nint hotkeyWindow = FindMessageOnlyWindow("Magpie_Hotkey", processId);
        if (hotkeyWindow != nint.Zero && PostMessage(
                hotkeyWindow,
                WmHotkey,
                windowed ? new nint(1) : nint.Zero,
                nint.Zero))
        {
            return true;
        }

        return allowKeyboardFallback && SendMagpieHotkey(windowed);
    }

    private static Input KeyDown(ushort key) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = key } }
    };

    private static Input KeyUp(ushort key) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion
        {
            Keyboard = new KeyboardInput { VirtualKey = key, Flags = KeyeventfKeyup }
        }
    };

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;

        internal readonly int Width => Math.Max(0, Right - Left);
        internal readonly int Height => Math.Max(0, Bottom - Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MonitorInfo
    {
        internal int Size;
        internal Rect Monitor;
        internal Rect WorkArea;
        internal uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        internal uint Type;
        internal InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)]
        internal KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        internal ushort VirtualKey;
        internal ushort ScanCode;
        internal uint Flags;
        internal uint Time;
        internal nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DisplayDevice
    {
        internal int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string DeviceString;
        internal int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string DeviceKey;
    }
}

internal enum DpiAwareness
{
    Invalid = -1,
    Unaware = 0,
    SystemAware = 1,
    PerMonitorAware = 2
}
