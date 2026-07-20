using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using AsteriaScale.Interop;
using AsteriaScale.Services;
using MediaColor = System.Windows.Media.Color;

namespace AsteriaScale;

public partial class HoverTranslationOverlay : Window
{
    private static readonly SolidColorBrush DictionaryAccent = Brush(0x45, 0xDD, 0xC8);
    private static readonly SolidColorBrush DictionaryBorder = Brush(0x2F, 0x91, 0x84);
    private static readonly SolidColorBrush DictionaryBadge = Brush(0x14, 0x2A, 0x2B);
    private static readonly SolidColorBrush NeuralAccent = Brush(0xB8, 0xA9, 0xFF);
    private static readonly SolidColorBrush NeuralBorder = Brush(0x76, 0x5C, 0xFF);
    private static readonly SolidColorBrush NeuralBadge = Brush(0x20, 0x1A, 0x3B);
    private static readonly SolidColorBrush SmartAccent = Brush(0x67, 0xE4, 0xD4);
    private static readonly SolidColorBrush SmartBorder = Brush(0x55, 0x74, 0xDD);
    private static readonly SolidColorBrush SmartBadge = Brush(0x13, 0x2B, 0x35);
    private nint _handle;

    public HoverTranslationOverlay()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    internal void ShowLoading(string source, NativeMethods.Point cursor, string engine)
    {
        bool dictionaryMode = engine == LocalDictionaryService.EngineLabel;
        string? smartAccelerator = SmartAccelerator(engine);
        bool smartMode =
            smartAccelerator is not null ||
            engine == SmartDictionaryHeuristicRanker.EngineLabel ||
            engine == SmartDictionaryResultCache.EngineLabel;
        string normalizedSource = source.Trim();
        ConfigureMode(
            dictionaryMode,
            smartMode,
            normalizedSource,
            isPhrase: false,
            loading: true,
            smartAccelerator: smartAccelerator);
        SourceText.Text = normalizedSource;
        SourceText.FontSize = SourceFontSize(normalizedSource);
        MeaningsPanel.Visibility = Visibility.Collapsed;
        MoreMeaningsText.Visibility = Visibility.Collapsed;
        TranslationPanel.Visibility = Visibility.Visible;
        TranslationText.Text = dictionaryMode
            ? "Ищу в локальных словарях…"
            : smartMode ? "Сверяю значения с контекстом…" : "Перевожу локально…";
        TranslationText.FontSize = 11.5;
        TranslationText.FontWeight = FontWeights.Normal;
        TranslationText.Foreground = new SolidColorBrush(MediaColor.FromRgb(0xA8, 0xB1, 0xC9));
        HintText.Visibility = Visibility.Collapsed;
        Width = PreferredWidth(normalizedSource, TranslationText.Text, dictionaryMode || smartMode);
        ShowNear(cursor);
    }

    internal void ShowTranslation(
        string source,
        string translation,
        NativeMethods.Point cursor,
        string engine,
        bool dictionaryFound = true,
        bool isPhrase = false,
        string? language = null,
        double? confidence = null)
    {
        bool dictionaryMode = engine == LocalDictionaryService.EngineLabel;
        string? smartAccelerator = SmartAccelerator(engine);
        bool smartUsesTatoeba =
            engine == SmartDictionaryHeuristicRanker.TatoebaEngineLabel;
        bool smartUsesFastPath =
            engine == SmartDictionaryHeuristicRanker.EngineLabel ||
            smartUsesTatoeba;
        bool smartUsesCache = engine == SmartDictionaryResultCache.EngineLabel;
        bool smartAbstained =
            smartAccelerator is not null &&
            IsSmartAbstentionEngine(engine);
        bool smartUnavailable =
            smartAccelerator is not null &&
            IsSmartUnavailableEngine(engine);
        bool smartMode =
            smartAccelerator is not null ||
            smartUsesFastPath ||
            smartUsesCache;
        string normalizedSource = source.Trim();
        string normalizedTranslation = translation.Trim();
        ConfigureMode(
            dictionaryMode,
            smartMode,
            normalizedSource,
            isPhrase,
            loading: false,
            language,
            dictionaryFound,
            confidence,
            smartAccelerator,
            smartAbstained
                ? $"{smartAccelerator} · НЕ УВЕРЕН"
                : smartUnavailable
                    ? $"{smartAccelerator} · НЕДОСТУПЕН"
                : smartUsesTatoeba
                    ? "TATOEBA"
                    : smartUsesFastPath ? "БЕЗ МОДЕЛИ" : smartUsesCache ? "КЭШ" : null);

        SourceText.Text = normalizedSource;
        SourceText.FontSize = SourceFontSize(normalizedSource);
        Width = PreferredWidth(normalizedSource, normalizedTranslation, dictionaryMode || smartMode);

        bool smartCandidatesPending =
            smartMode &&
            confidence is null &&
            normalizedTranslation.Contains(';');
        if ((dictionaryMode || smartCandidatesPending) && dictionaryFound)
        {
            string[] allMeanings = normalizedTranslation
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            string[] meanings = allMeanings.Take(3).ToArray();
            MeaningsList.ItemsSource = meanings;
            MeaningsPanel.Visibility = Visibility.Visible;
            int hiddenCount = Math.Max(0, allMeanings.Length - meanings.Length);
            MoreMeaningsText.Text = hiddenCount > 0
                ? $"ещё {hiddenCount} в словаре"
                : string.Empty;
            MoreMeaningsText.Visibility = hiddenCount > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            TranslationPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            MeaningsPanel.Visibility = Visibility.Collapsed;
            MoreMeaningsText.Visibility = Visibility.Collapsed;
            TranslationPanel.Visibility = Visibility.Visible;
            TranslationText.FontSize = dictionaryMode ? 13.5 : smartMode ? 15 : 14.5;
            TranslationText.FontWeight = FontWeights.SemiBold;
            TranslationText.Foreground = new SolidColorBrush(MediaColor.FromRgb(0xF5, 0xF7, 0xFF));
            TranslationText.Text = dictionaryMode ? "Перевод не найден" : normalizedTranslation;
            HintText.Text = dictionaryMode
                ? "Полный перевод можно разрешить в настройках умного словаря."
                : string.Empty;
            HintText.Visibility = dictionaryMode ? Visibility.Visible : Visibility.Collapsed;
        }

        ShowNear(cursor);
    }

    internal void HideOverlay()
    {
        if (IsVisible)
        {
            Hide();
        }
    }

    internal static double PreferredWidth(string source, string translation, bool dictionaryMode)
    {
        int sourceLength = source.Trim().Length;
        string[] visibleMeanings = translation
            .Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries)
            .Take(3)
            .ToArray();
        int translationLength = visibleMeanings.Length == 0
            ? translation.Trim().Length
            : visibleMeanings.Max(value => value.Length);
        if (dictionaryMode)
        {
            if (sourceLength <= 30 && translationLength <= 28)
            {
                return 268;
            }

            return sourceLength <= 58 && translationLength <= 52 ? 292 : 324;
        }

        int totalLength = sourceLength + translationLength;
        return totalLength switch
        {
            <= 100 => 320,
            <= 230 => 376,
            _ => 430
        };
    }

    private void ConfigureMode(
        bool dictionaryMode,
        bool smartMode,
        string source,
        bool isPhrase,
        bool loading,
        string? language = null,
        bool dictionaryFound = true,
        double? confidence = null,
        string? smartAccelerator = null,
        string? smartStage = null)
    {
        string sourceLanguage = string.IsNullOrWhiteSpace(language)
            ? (ContainsJapanese(source) ? "JA" : "EN")
            : language.ToUpperInvariant();
        ModeText.Text = dictionaryMode ? "Словарь" : smartMode ? "Умный словарь" : "Локальный перевод";
        string acceleratorLabel = smartAccelerator ?? "МОДЕЛЬ";
        MetaText.Text = dictionaryMode
            ? $"{sourceLanguage} → RU  ·  {(loading ? "ПОИСК" : !dictionaryFound ? "НЕТ" : isPhrase ? "ВЫРАЖЕНИЕ" : "СЛОВО")}"
            : smartMode
                ? $"{sourceLanguage} → RU  ·  {smartStage ?? (loading || confidence is null ? acceleratorLabel + " АНАЛИЗ" : acceleratorLabel + " КОНТЕКСТ")}"
                : $"{sourceLanguage} → RU  ·  QWEN";

        SolidColorBrush accent = dictionaryMode ? DictionaryAccent : smartMode ? SmartAccent : NeuralAccent;
        Card.BorderBrush = dictionaryMode ? DictionaryBorder : smartMode ? SmartBorder : NeuralBorder;
        MetaBadge.Background = dictionaryMode ? DictionaryBadge : smartMode ? SmartBadge : NeuralBadge;
        MetaBadge.BorderBrush = dictionaryMode ? DictionaryBorder : smartMode ? SmartBorder : NeuralBorder;
        MetaText.Foreground = accent;
        StatusDot.Fill = accent;
    }

    internal static string? SmartAccelerator(string engine)
    {
        if (engine.StartsWith(
                GlossBertDictionarySelectorService.CpuEngineLabel,
                StringComparison.Ordinal))
        {
            return "CPU";
        }

        if (engine.StartsWith(
                GlossBertDictionarySelectorService.NpuEngineLabel,
                StringComparison.Ordinal))
        {
            return "NPU";
        }

        // Compatibility with selections cached or still in flight from previous releases.
        if (engine == FoundryTranslationService.SmartDictionaryEngineLabel)
        {
            return "GPU";
        }

        return engine == NpuDictionarySelectorService.EngineLabel ? "NPU" : null;
    }

    internal static bool IsSmartAbstentionEngine(string engine) =>
        engine.Contains("НЕ УВЕРЕН", StringComparison.OrdinalIgnoreCase);

    internal static bool IsSmartUnavailableEngine(string engine) =>
        engine.Contains("НЕДОСТУПЕН", StringComparison.OrdinalIgnoreCase);

    private static double SourceFontSize(string source) => source.Length switch
    {
        > 90 => 11.5,
        > 42 => 12.5,
        _ => 14
    };

    private static bool ContainsJapanese(string value) => value.Any(character =>
        character is >= '\u3040' and <= '\u30ff' or >= '\u3400' and <= '\u9fff');

    private static SolidColorBrush Brush(byte red, byte green, byte blue)
    {
        SolidColorBrush brush = new(MediaColor.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
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

    private void ShowNear(NativeMethods.Point cursor)
    {
        if (!IsVisible)
        {
            Show();
        }

        InvalidateMeasure();
        UpdateLayout();
        if (_handle == nint.Zero)
        {
            _handle = new WindowInteropHelper(this).Handle;
        }

        uint dpi = NativeMethods.GetDpiForWindow(_handle);
        double scale = dpi > 0 ? dpi / 96d : 1d;
        int width = Math.Max(1, (int)Math.Ceiling(ActualWidth * scale));
        int height = Math.Max(1, (int)Math.Ceiling(ActualHeight * scale));

        NativeMethods.Point monitorPoint = cursor;
        nint monitor = NativeMethods.MonitorFromPoint(monitorPoint, NativeMethods.MonitorDefaultToNearest);
        NativeMethods.MonitorInfo monitorInfo = new()
        {
            Size = Marshal.SizeOf<NativeMethods.MonitorInfo>()
        };
        NativeMethods.GetMonitorInfo(monitor, ref monitorInfo);

        int horizontalGap = Math.Max(18, (int)Math.Ceiling(12 * scale));
        int verticalGap = Math.Max(22, (int)Math.Ceiling(14 * scale));
        int edge = Math.Max(8, (int)Math.Ceiling(6 * scale));
        int x = cursor.X + horizontalGap;
        int y = cursor.Y + verticalGap;
        if (x + width > monitorInfo.WorkArea.Right - edge)
        {
            x = cursor.X - width - horizontalGap;
        }

        if (y + height > monitorInfo.WorkArea.Bottom - edge)
        {
            y = cursor.Y - height - verticalGap;
        }

        x = Math.Clamp(
            x,
            monitorInfo.WorkArea.Left + edge,
            Math.Max(monitorInfo.WorkArea.Left + edge, monitorInfo.WorkArea.Right - width - edge));
        y = Math.Clamp(
            y,
            monitorInfo.WorkArea.Top + edge,
            Math.Max(monitorInfo.WorkArea.Top + edge, monitorInfo.WorkArea.Bottom - height - edge));

        NativeMethods.SetWindowPos(
            _handle,
            NativeMethods.HwndTopmost,
            x,
            y,
            width,
            height,
            NativeMethods.SwpNoactivate | NativeMethods.SwpShowwindow);
    }
}
