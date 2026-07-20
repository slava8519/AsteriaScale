using System.Windows.Interop;
using AsteriaScale.Interop;

namespace AsteriaScale.Services;

internal sealed class GlobalHotkeyService : IDisposable
{
    private const int ScreenTranslationHotkeyId = 0x4154;
    private const int SmartFallbackHotkeyId = 0x4144;
    private readonly HwndSource _source;
    private bool _screenRegistered;
    private bool _smartFallbackRegistered;

    public GlobalHotkeyService()
    {
        HwndSourceParameters parameters = new("AsteriaScale_Hotkeys")
        {
            ParentWindow = NativeMethods.HwndMessage,
            WindowStyle = 0,
            Width = 0,
            Height = 0
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WindowProcedure);
    }

    public event EventHandler? ScreenTranslationPressed;
    public event EventHandler? SmartFallbackPressed;

    public bool RegisterScreenTranslation()
    {
        if (_screenRegistered)
        {
            return true;
        }

        _screenRegistered = NativeMethods.RegisterHotKey(
            _source.Handle,
            ScreenTranslationHotkeyId,
            NativeMethods.ModAlt | NativeMethods.ModShift | NativeMethods.ModNoRepeat,
            NativeMethods.VkT);
        return _screenRegistered;
    }

    public void UnregisterScreenTranslation()
    {
        if (!_screenRegistered)
        {
            return;
        }

        NativeMethods.UnregisterHotKey(_source.Handle, ScreenTranslationHotkeyId);
        _screenRegistered = false;
    }

    public bool RegisterSmartFallback()
    {
        if (_smartFallbackRegistered)
        {
            return true;
        }

        _smartFallbackRegistered = NativeMethods.RegisterHotKey(
            _source.Handle,
            SmartFallbackHotkeyId,
            NativeMethods.ModAlt | NativeMethods.ModShift | NativeMethods.ModNoRepeat,
            NativeMethods.VkD);
        return _smartFallbackRegistered;
    }

    public void UnregisterSmartFallback()
    {
        if (!_smartFallbackRegistered)
        {
            return;
        }

        NativeMethods.UnregisterHotKey(_source.Handle, SmartFallbackHotkeyId);
        _smartFallbackRegistered = false;
    }

    private nint WindowProcedure(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == NativeMethods.WmHotkey && wParam.ToInt32() == ScreenTranslationHotkeyId)
        {
            handled = true;
            ScreenTranslationPressed?.Invoke(this, EventArgs.Empty);
        }
        else if (message == NativeMethods.WmHotkey && wParam.ToInt32() == SmartFallbackHotkeyId)
        {
            handled = true;
            SmartFallbackPressed?.Invoke(this, EventArgs.Empty);
        }

        return nint.Zero;
    }

    public void Dispose()
    {
        UnregisterScreenTranslation();
        UnregisterSmartFallback();
        _source.RemoveHook(WindowProcedure);
        _source.Dispose();
    }
}
