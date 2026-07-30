using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using AsteriaScale.Interop;
using AsteriaScale.Services;

namespace AsteriaScale;

internal sealed class SpeakerGenderSavedEventArgs : EventArgs
{
    internal SpeakerGenderSavedEventArgs(
        CurrentSpeakerContext originalContext,
        string name,
        SpeakerGender gender)
    {
        OriginalContext = originalContext;
        Name = name;
        Gender = gender;
    }

    internal CurrentSpeakerContext OriginalContext { get; }
    internal string Name { get; }
    internal SpeakerGender Gender { get; }

    internal CurrentSpeakerContext UpdatedContext => OriginalContext with
    {
        Name = Name,
        Gender = Gender
    };
}

/// <summary>
/// A reusable, keyboard-friendly editor for the latest detected speaker. It is
/// deliberately separate from the click-through translation card: editing can
/// take focus temporarily and then return it to the game that owned focus.
/// </summary>
public partial class SpeakerGenderOverlay : Window
{
    private CurrentSpeakerContext? _context;
    private NativeMethods.Point _physicalAnchor;
    private nint _handle;
    private nint _previousForegroundWindow;

    public SpeakerGenderOverlay()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    internal event EventHandler<SpeakerGenderSavedEventArgs>? Saved;
    internal event EventHandler? Cancelled;
    internal event EventHandler? Hidden;

    internal void ShowFor(
        CurrentSpeakerContext context,
        string gameDisplayName,
        NativeMethods.Point physicalAnchor)
    {
        ArgumentNullException.ThrowIfNull(context);
        Dispatcher.VerifyAccess();

        nint foreground = NativeMethods.GetForegroundWindow();
        if (foreground != nint.Zero && foreground != _handle)
        {
            _previousForegroundWindow = foreground;
        }

        _context = context;
        _physicalAnchor = physicalAnchor;
        SpeakerNameBox.Text = context.Name;
        GameNoteText.Text = string.IsNullOrWhiteSpace(gameDisplayName)
            ? "Настройка сохранится только для текущей игры. Всё остаётся на этом компьютере."
            : $"Настройка сохранится только для игры «{gameDisplayName.Trim()}». Всё остаётся на этом компьютере.";
        UpdateSelectedChoice(context.Gender);
        UpdateChoiceAvailability();

        if (!IsVisible)
        {
            Show();
        }

        UpdateLayout();
        PositionNear(_physicalAnchor);
        Activate();
        if (_handle != nint.Zero)
        {
            NativeMethods.FocusWindow(_handle);
        }

        SpeakerNameBox.Focus();
        Keyboard.Focus(SpeakerNameBox);
        SpeakerNameBox.SelectAll();
    }

    internal void Cancel()
    {
        if (!IsVisible)
        {
            return;
        }

        HideAndRestoreForeground();
        Cancelled?.Invoke(this, EventArgs.Empty);
        Hidden?.Invoke(this, EventArgs.Empty);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        nint current = NativeMethods.GetWindowLongPtr(_handle, NativeMethods.GwlExstyle);
        long styles = current.ToInt64() | NativeMethods.WsExToolwindow;
        NativeMethods.SetWindowLongPtr(_handle, NativeMethods.GwlExstyle, new nint(styles));
    }

    private void OnSpeakerNameChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        UpdateChoiceAvailability();
    }

    private void UpdateChoiceAvailability()
    {
        bool hasName = !string.IsNullOrWhiteSpace(SpeakerNameBox.Text);
        MasculineButton.IsEnabled = hasName;
        FeminineButton.IsEnabled = hasName;
        IgnoreButton.IsEnabled = hasName;
        NameValidationText.Visibility = hasName ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateSelectedChoice(SpeakerGender gender)
    {
        MasculineButton.Tag = gender == SpeakerGender.Masculine ? "Selected" : null;
        FeminineButton.Tag = gender == SpeakerGender.Feminine ? "Selected" : null;
        IgnoreButton.Tag = gender == SpeakerGender.Ignore ? "Selected" : null;
    }

    private void OnMasculineClick(object sender, RoutedEventArgs e) =>
        Save(SpeakerGender.Masculine);

    private void OnFeminineClick(object sender, RoutedEventArgs e) =>
        Save(SpeakerGender.Feminine);

    private void OnIgnoreClick(object sender, RoutedEventArgs e) =>
        Save(SpeakerGender.Ignore);

    private void OnCancelClick(object sender, RoutedEventArgs e) => Cancel();

    private void Save(SpeakerGender gender)
    {
        CurrentSpeakerContext? context = _context;
        string name = SpeakerNameBox.Text.Trim();
        if (context is null || name.Length == 0)
        {
            NameValidationText.Visibility = Visibility.Visible;
            SpeakerNameBox.Focus();
            return;
        }

        SpeakerGenderSavedEventArgs args = new(context, name, gender);
        HideAndRestoreForeground();
        try
        {
            Saved?.Invoke(this, args);
        }
        finally
        {
            Hidden?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape ||
            key == Key.F4 && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            e.Handled = true;
            Cancel();
        }
    }

    private void HideAndRestoreForeground()
    {
        nint restoreWindow = _previousForegroundWindow;
        _previousForegroundWindow = nint.Zero;
        Hide();

        // This is a transient editor opened from a hover anchor. Returning the
        // pointer prevents the next OCR pass from reading the button location.
        NativeMethods.SetCursorPos(_physicalAnchor.X, _physicalAnchor.Y);

        if (restoreWindow != nint.Zero &&
            restoreWindow != _handle &&
            NativeMethods.IsWindow(restoreWindow))
        {
            NativeMethods.FocusWindow(restoreWindow);
        }
    }

    private void PositionNear(NativeMethods.Point anchor)
    {
        if (_handle == nint.Zero)
        {
            _handle = new WindowInteropHelper(this).Handle;
        }

        int width;
        int height;
        if (NativeMethods.GetWindowRect(_handle, out NativeMethods.Rect bounds) &&
            bounds.Width > 0 && bounds.Height > 0)
        {
            width = bounds.Width;
            height = bounds.Height;
        }
        else
        {
            uint dpi = NativeMethods.GetDpiForWindow(_handle);
            double scale = dpi > 0 ? dpi / 96d : 1d;
            width = Math.Max(1, (int)Math.Ceiling(ActualWidth * scale));
            height = Math.Max(1, (int)Math.Ceiling(ActualHeight * scale));
        }

        nint monitor = NativeMethods.MonitorFromPoint(
            anchor,
            NativeMethods.MonitorDefaultToNearest);
        NativeMethods.MonitorInfo monitorInfo = new()
        {
            Size = Marshal.SizeOf<NativeMethods.MonitorInfo>()
        };
        if (monitor == nint.Zero || !NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
        {
            NativeMethods.SetWindowPos(
                _handle,
                NativeMethods.HwndTopmost,
                anchor.X,
                anchor.Y,
                width,
                height,
                NativeMethods.SwpShowwindow);
            return;
        }

        uint windowDpi = NativeMethods.GetDpiForWindow(_handle);
        double windowScale = windowDpi > 0 ? windowDpi / 96d : 1d;
        int horizontalGap = Math.Max(18, (int)Math.Ceiling(12 * windowScale));
        int verticalGap = Math.Max(22, (int)Math.Ceiling(14 * windowScale));
        int edge = Math.Max(10, (int)Math.Ceiling(8 * windowScale));
        int x = anchor.X + horizontalGap;
        int y = anchor.Y + verticalGap;
        if (x + width > monitorInfo.WorkArea.Right - edge)
        {
            x = anchor.X - width - horizontalGap;
        }

        if (y + height > monitorInfo.WorkArea.Bottom - edge)
        {
            y = anchor.Y - height - verticalGap;
        }

        x = Math.Clamp(
            x,
            monitorInfo.WorkArea.Left + edge,
            Math.Max(
                monitorInfo.WorkArea.Left + edge,
                monitorInfo.WorkArea.Right - width - edge));
        y = Math.Clamp(
            y,
            monitorInfo.WorkArea.Top + edge,
            Math.Max(
                monitorInfo.WorkArea.Top + edge,
                monitorInfo.WorkArea.Bottom - height - edge));

        NativeMethods.SetWindowPos(
            _handle,
            NativeMethods.HwndTopmost,
            x,
            y,
            width,
            height,
            NativeMethods.SwpShowwindow);
    }
}
