using Drawing = System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using AsteriaScale.Interop;
using AsteriaScale.Services;

namespace AsteriaScale;

internal sealed record ScreenTranslationVisual(ScreenOcrBlock Source, string Translation);

public partial class ScreenTranslationOverlay : Window
{
    private nint _handle;
    private double _dpiScale = 1;

    public ScreenTranslationOverlay()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    internal void ShowLoading(Drawing.Rectangle screenBounds, string message)
    {
        TranslationCanvas.Children.Clear();
        StatusText.Text = message;
        StatusPill.Visibility = Visibility.Visible;
        ShowOnScreen(screenBounds);
    }

    internal void ShowMessage(Drawing.Rectangle screenBounds, string message)
    {
        TranslationCanvas.Children.Clear();
        StatusText.Text = message;
        StatusPill.Visibility = Visibility.Visible;
        ShowOnScreen(screenBounds);
    }

    internal void ShowTranslations(
        Drawing.Rectangle screenBounds,
        IReadOnlyList<ScreenTranslationVisual> translations)
    {
        TranslationCanvas.Children.Clear();
        ShowOnScreen(screenBounds);

        double logicalWidth = screenBounds.Width / _dpiScale;
        double logicalHeight = screenBounds.Height / _dpiScale;
        foreach (ScreenTranslationVisual item in translations)
        {
            AddTranslationCard(screenBounds, logicalWidth, logicalHeight, item);
        }

        StatusText.Text = $"Переведено: {translations.Count}  •  Alt+Shift+T — обновить";
        StatusPill.Visibility = Visibility.Visible;
    }

    internal void HideOverlay()
    {
        if (IsVisible)
        {
            Hide();
        }
    }

    private void AddTranslationCard(
        Drawing.Rectangle screenBounds,
        double logicalWidth,
        double logicalHeight,
        ScreenTranslationVisual item)
    {
        Drawing.Rectangle source = item.Source.Bounds;
        double x = ((source.Left - screenBounds.Left) / _dpiScale) - 5;
        double y = ((source.Top - screenBounds.Top) / _dpiScale) - 4;
        double originalWidth = source.Width / _dpiScale;
        double originalHeight = source.Height / _dpiScale;
        double width = Math.Clamp(Math.Max(originalWidth + 14, 165), 165, Math.Max(165, logicalWidth - 20));
        x = Math.Clamp(x, 10, Math.Max(10, logicalWidth - width - 10));

        double averageLineHeight = originalHeight / Math.Max(1, item.Source.LineCount);
        double fontSize = Math.Clamp(averageLineHeight * 0.63, 12.5, 28);
        TextBlock text = new()
        {
            Text = item.Translation,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(247, 248, 255)),
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontWeight = FontWeights.SemiBold,
            FontSize = fontSize,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = fontSize * 1.32,
            VerticalAlignment = VerticalAlignment.Center
        };

        Border accent = new()
        {
            Width = 3,
            Margin = new Thickness(0, 1, 10, 1),
            CornerRadius = new CornerRadius(2),
            Background = new LinearGradientBrush(
                System.Windows.Media.Color.FromRgb(124, 92, 255),
                System.Windows.Media.Color.FromRgb(39, 215, 196),
                90)
        };
        Grid content = new();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(accent);
        Grid.SetColumn(text, 1);
        content.Children.Add(text);

        Border card = new()
        {
            Width = width,
            MinHeight = Math.Max(36, originalHeight + 8),
            Padding = new Thickness(10, 8, 12, 8),
            CornerRadius = new CornerRadius(11),
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(242, 9, 14, 29)),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(205, 105, 82, 232)),
            BorderThickness = new Thickness(1),
            Child = content,
            Effect = new DropShadowEffect
            {
                BlurRadius = 20,
                ShadowDepth = 5,
                Opacity = 0.56,
                Color = Colors.Black
            }
        };

        card.Measure(new System.Windows.Size(width, double.PositiveInfinity));
        double measuredHeight = Math.Max(card.MinHeight, card.DesiredSize.Height);
        y = Math.Clamp(y, 10, Math.Max(10, logicalHeight - measuredHeight - 10));
        Canvas.SetLeft(card, x);
        Canvas.SetTop(card, y);
        TranslationCanvas.Children.Add(card);
    }

    private void ShowOnScreen(Drawing.Rectangle screenBounds)
    {
        if (!IsVisible)
        {
            Show();
        }

        if (_handle == nint.Zero)
        {
            _handle = new WindowInteropHelper(this).Handle;
        }

        NativeMethods.SetWindowPos(
            _handle,
            NativeMethods.HwndTopmost,
            screenBounds.Left,
            screenBounds.Top,
            screenBounds.Width,
            screenBounds.Height,
            NativeMethods.SwpNoactivate | NativeMethods.SwpShowwindow);

        uint dpi = NativeMethods.GetDpiForWindow(_handle);
        _dpiScale = dpi > 0 ? dpi / 96d : 1d;
        OverlayRoot.Width = screenBounds.Width / _dpiScale;
        OverlayRoot.Height = screenBounds.Height / _dpiScale;
        TranslationCanvas.Width = OverlayRoot.Width;
        TranslationCanvas.Height = OverlayRoot.Height;
        UpdateLayout();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        nint current = NativeMethods.GetWindowLongPtr(_handle, NativeMethods.GwlExstyle);
        long styles = current.ToInt64() |
                      NativeMethods.WsExTransparent |
                      NativeMethods.WsExToolwindow |
                      NativeMethods.WsExNoactivate;
        NativeMethods.SetWindowLongPtr(_handle, NativeMethods.GwlExstyle, new nint(styles));
    }
}
