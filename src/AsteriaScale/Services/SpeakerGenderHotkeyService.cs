using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using AsteriaScale.Interop;

namespace AsteriaScale.Services;

/// <summary>
/// Owns the process-wide shortcut that opens the current-speaker editor. The
/// message-only window keeps registration independent from any visible WPF
/// window and therefore survives main-window minimization to the tray.
/// </summary>
internal sealed class SpeakerGenderHotkeyService : IDisposable
{
    private const int HotkeyId = 0xA57E;
    internal const string DisplayGesture = "Ctrl+Shift+G";

    private HwndSource? _messageSource;
    private bool _disposed;

    internal event EventHandler? HotkeyPressed;

    internal bool IsRegistered => _messageSource is not null;

    internal void Register()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_messageSource is not null)
        {
            return;
        }

        HwndSource source = new(new HwndSourceParameters(
            "AsteriaScale.SpeakerGenderHotkey")
        {
            ParentWindow = NativeMethods.HwndMessage,
            WindowStyle = 0,
            ExtendedWindowStyle = NativeMethods.WsExToolwindow,
            Width = 0,
            Height = 0
        });
        source.AddHook(OnWindowMessage);

        uint modifiers = NativeMethods.ModControl |
                         NativeMethods.ModShift |
                         NativeMethods.ModNoRepeat;
        if (!NativeMethods.RegisterHotKey(
                source.Handle,
                HotkeyId,
                modifiers,
                NativeMethods.VkG))
        {
            int error = Marshal.GetLastWin32Error();
            source.RemoveHook(OnWindowMessage);
            source.Dispose();
            throw new Win32Exception(
                error,
                $"Не удалось зарегистрировать сочетание {DisplayGesture}.");
        }

        _messageSource = source;
    }

    internal void Unregister()
    {
        HwndSource? source = _messageSource;
        if (source is null)
        {
            return;
        }

        source.Dispatcher.VerifyAccess();
        _messageSource = null;
        NativeMethods.UnregisterHotKey(source.Handle, HotkeyId);
        source.RemoveHook(OnWindowMessage);
        source.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        HwndSource? source = _messageSource;
        if (source is null)
        {
            return;
        }

        if (source.Dispatcher.CheckAccess())
        {
            Unregister();
            return;
        }

        source.Dispatcher.Invoke(Unregister);
    }

    private nint OnWindowMessage(
        nint window,
        int message,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        if (message == NativeMethods.WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
        }

        return nint.Zero;
    }
}
