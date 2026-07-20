using System.Windows.Threading;

namespace AsteriaScale.Services;

/// <summary>
/// The Alt+Shift+T screen translation flow: full-screen OCR, batched local
/// translation and the result overlay. Extracted verbatim from
/// HoverTranslationService (roadmap task #5, stage 2); the facade keeps the
/// hotkey entry point, the processing gate and idle-unload scheduling.
/// </summary>
internal sealed class ScreenTranslationFlow
{
    private readonly WindowsAiOcrService _ocr;
    private readonly FoundryTranslationService _translator;
    private readonly ScreenTranslationOverlay _screenOverlay;
    private readonly Dispatcher _dispatcher;
    private readonly Action<string> _reportStatus;
    private readonly Func<bool> _stillActive;

    public ScreenTranslationFlow(
        WindowsAiOcrService ocr,
        FoundryTranslationService translator,
        ScreenTranslationOverlay screenOverlay,
        Dispatcher dispatcher,
        Action<string> reportStatus,
        Func<bool> stillActive)
    {
        _ocr = ocr;
        _translator = translator;
        _screenOverlay = screenOverlay;
        _dispatcher = dispatcher;
        _reportStatus = reportStatus;
        _stillActive = stillActive;
    }

    private void ReportStatus(string status) => _reportStatus(status);

    private static void LogError(Exception exception) =>
        HoverTranslationService.LogError(exception);

    private void HideScreenOverlay() =>
        _dispatcher.BeginInvoke(_screenOverlay.HideOverlay, DispatcherPriority.Normal);

    public async Task<bool> ProcessAsync(nint foregroundWindow, CancellationToken cancellationToken)
    {
        bool translatedSuccessfully = false;
        try
        {
            await _dispatcher.InvokeAsync(_screenOverlay.HideOverlay, DispatcherPriority.Send, cancellationToken);
            await Task.Delay(90, cancellationToken);
            ReportStatus("Экранный перевод: распознаю все текстовые области…");
            ScreenOcrResult recognized = await _ocr.RecognizeScreenAsync(foregroundWindow, cancellationToken);
            if (!_stillActive())
            {
                return false;
            }

            if (recognized.Blocks.Count == 0)
            {
                await ShowScreenMessageAsync(recognized.CaptureBounds, "Текст для перевода не найден", cancellationToken);
                return false;
            }

            await _dispatcher.InvokeAsync(
                () => _screenOverlay.ShowLoading(
                    recognized.CaptureBounds,
                    $"Перевожу блоков: {recognized.Blocks.Count}…"),
                DispatcherPriority.Normal,
                cancellationToken);
            ReportStatus($"Экранный перевод: перевожу {recognized.Blocks.Count} текстовых блоков локально…");

            IReadOnlyList<string> translations = await _translator.TranslateManyToRussianAsync(
                recognized.Blocks.Select(block => block.Text).ToArray(),
                cancellationToken);
            List<ScreenTranslationVisual> visuals = [];
            for (int index = 0; index < recognized.Blocks.Count && index < translations.Count; index++)
            {
                string translation = translations[index].Trim();
                ScreenOcrBlock source = recognized.Blocks[index];
                if (translation.Length == 0 ||
                    string.Equals(source.Text, translation, StringComparison.CurrentCultureIgnoreCase))
                {
                    continue;
                }

                visuals.Add(new ScreenTranslationVisual(source, translation));
            }

            if (!_stillActive())
            {
                return false;
            }

            if (visuals.Count == 0)
            {
                await ShowScreenMessageAsync(
                    recognized.CaptureBounds,
                    "На экране уже русский текст или перевод не требуется",
                    cancellationToken);
                return false;
            }

            await _dispatcher.InvokeAsync(
                () => _screenOverlay.ShowTranslations(recognized.CaptureBounds, visuals),
                DispatcherPriority.Normal,
                cancellationToken);
            translatedSuccessfully = true;
            ReportStatus($"Экранный перевод: наложено реплик — {visuals.Count}; Alt+Shift+T обновит экран");
        }
        catch (OperationCanceledException)
        {
            HideScreenOverlay();
        }
        catch (Exception ex)
        {
            HideScreenOverlay();
            LogError(ex);
            ReportStatus("Экранный перевод: " + ex.Message);
        }

        return translatedSuccessfully;
    }

    private async Task ShowScreenMessageAsync(
        System.Drawing.Rectangle bounds,
        string message,
        CancellationToken cancellationToken)
    {
        await _dispatcher.InvokeAsync(
            () => _screenOverlay.ShowMessage(bounds, message),
            DispatcherPriority.Normal,
            cancellationToken);
        await Task.Delay(2400, cancellationToken);
        HideScreenOverlay();
    }

}
