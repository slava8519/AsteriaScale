using System.Windows.Threading;
using AsteriaScale.Interop;
using AsteriaScale.Models;

namespace AsteriaScale.Services;

/// <summary>
/// The Windows host of hover translation: Windows AI OCR, the Textractor
/// upgrade, the cursor tracker, the WPF overlays, the speaker editor and its
/// hotkey. Lifecycle, ordering, cancellation and history live in the core
/// (<see cref="HoverCoordinator{TAnchor}"/>, owner decision M12), which calls
/// back through <see cref="IHoverHost{TAnchor}"/>. The public API is the one
/// the service had before the move.
/// </summary>
public sealed class HoverTranslationService :
    IAsyncDisposable,
    IHoverHost<HoverTranslationService.CursorHover>
{
    private readonly WindowsAiOcrService _ocr = new();
    private readonly HoverCoordinator<CursorHover> _core;
    private readonly HoverTranslationOverlay _overlay;
    private readonly SpeakerGenderOverlay _speakerEditor;
    private readonly SpeakerGenderHotkeyService _speakerHotkey;
    private readonly Dispatcher _dispatcher;
    private readonly HoverTracking _tracking;
    private readonly TextHookService _textHook;
    private int _textHookDiagnosticProcessId;
    private bool _textHookMatchReported;
    private HoverSpeakerTarget<CursorHover>? _speakerEditorTarget;
    private bool _speakerEditorOpen;
    private bool _suppressSpeakerEditorResume;

    public HoverTranslationService(
        AppSettings settings,
        Func<Task>? saveSettingsAsync = null)
    {
        // The core services and the pipeline come first, then the WPF windows,
        // the tracker and Textractor, as before the move. The coordinator
        // constructor never calls back into this half-built host.
        _core = new HoverCoordinator<CursorHover>(settings, saveSettingsAsync, this);
        _core.StatusChanged += (_, status) => StatusChanged?.Invoke(this, status);
        _dispatcher = System.Windows.Application.Current.Dispatcher;
        _overlay = new HoverTranslationOverlay();
        _speakerEditor = new SpeakerGenderOverlay();
        _speakerEditor.Saved += OnSpeakerGenderSaved;
        _speakerEditor.Hidden += OnSpeakerEditorHidden;
        _speakerHotkey = new SpeakerGenderHotkeyService();
        _speakerHotkey.HotkeyPressed += OnSpeakerGenderHotkeyPressed;
        _tracking = new HoverTracking(
            () => _core.Enabled && !_speakerEditorOpen,
            _core.EffectiveDelayMs,
            _core.HideOverlay,
            _core.TryBeginProcessing,
            (point, token) => _core.StartHover(new CursorHover(point), token));
        _textHook = new TextHookService(settings);
    }

    public event EventHandler<string>? StatusChanged;

    public void Start()
    {
        try
        {
            _speakerHotkey.Register();
        }
        catch (Exception exception)
        {
            LogError(exception);
            ReportStatus(
                $"Быстрый выбор рода недоступен: {SpeakerGenderHotkeyService.DisplayGesture} занято");
        }

        _core.Start();
    }

    public void SetEnabled(bool enabled) => _core.SetEnabled(enabled);

    public void ApplySmartDictionarySettings() => _core.ApplySmartDictionarySettings();

    private void StartTimer() => _tracking.Start();

    private void StopTimer() => _tracking.Stop();

    void IHoverHost<CursorHover>.StartTracking() => StartTimer();

    void IHoverHost<CursorHover>.StopTracking() => StopTimer();

    public void SetMode(string mode) => _core.SetMode(mode);

    Task IHoverHost<CursorHover>.EnsureTextSourceReadyAsync() => _ocr.EnsureReadyAsync();

    Task<HoverOcrResult?> IHoverHost<CursorHover>.RecognizeAsync(
        CursorHover anchor,
        bool sentenceContext,
        CancellationToken cancellationToken) =>
        _ocr.RecognizeNearCursorAsync(anchor.Cursor, sentenceContext, cancellationToken);

    bool IHoverHost<CursorHover>.IsAnchorCurrent(CursorHover anchor) => CursorIsNear(anchor.Cursor);

    HoverOcrResult IHoverHost<CursorHover>.UpgradeRecognized(
        CursorHover anchor,
        HoverOcrResult recognized) =>
        UpgradeWithTextHook(recognized, anchor.Cursor);

    void IHoverHost<CursorHover>.OnRecognized(CursorHover anchor, HoverOcrResult recognized)
    {
        TextHookStatusSnapshot textHookStatus = _textHook.GetStatusSnapshot(
            _textHook.AttachedProcessId,
            recognized.TextSource == HoverTextSource.TextHook);
        // Every card of this hover shows the Textractor state captured here.
        anchor.TextHookStatus = textHookStatus;
        ReportStatus("Text-hook: " + textHookStatus.Details);
    }

    /// <summary>
    /// Optional Textractor upgrade: the hovered word and its position stay
    /// OCR-anchored, only the sentence context is replaced when a freshly
    /// hooked sentence contains that word. Any failure keeps the OCR result,
    /// so translation stays universal for arbitrary programs and documents.
    /// </summary>
    private HoverOcrResult UpgradeWithTextHook(HoverOcrResult recognized, NativeMethods.Point cursor)
    {
        if (!_textHook.IsEnabled)
        {
            _textHook.Detach();
            return recognized;
        }

        try
        {
            // OCR has already consumed the visible Magpie destination point.
            // Resolve only the process used by Textractor back to Magpie's
            // source HWND; changing the cursor here would corrupt OCR geometry.
            nint window = VisibleCursorService.GetTextSourceWindow(cursor);
            if (window == nint.Zero)
            {
                _textHook.Detach();
                return recognized;
            }

            NativeMethods.GetWindowThreadProcessId(window, out uint processIdValue);
            int processId = unchecked((int)processIdValue);
            if (processId <= 0 ||
                processId == Environment.ProcessId)
            {
                _textHook.Detach();
                return recognized;
            }

            // Let TextHookService see profile-disabled targets as well. It will
            // keep OCR in charge, but the card can then distinguish an explicit
            // per-game opt-out from a missing or failed Textractor installation.
            _textHook.EnsureAttached(processId);
            if (_textHook.AttachedProcessId != processId)
            {
                return recognized;
            }

            if (_textHookDiagnosticProcessId != processId)
            {
                _textHookDiagnosticProcessId = processId;
                _textHookMatchReported = false;
                ReportStatus("Text-hook: Textractor запущен, совпадающий текст пока не получен");
            }

            TextHookMatch? match = _textHook.TryGetSentenceContaining(
                processId,
                recognized.HoveredWord,
                recognized.Text,
                recognized.CharacterIndex);
            if (match is not null)
            {
                if (!_textHookMatchReported)
                {
                    _textHookMatchReported = true;
                    ReportStatus("Text-hook: используется текст Textractor (HOOK)");
                }

                return ApplyTextHookMatch(recognized, match);
            }
        }
        catch (Exception exception)
        {
            LogError(exception);
        }

        return recognized;
    }

    SpeakerGameIdentity? IHoverHost<CursorHover>.ResolveSpeakerGame(CursorHover anchor) =>
        ResolveSpeakerGameIdentity(anchor.Cursor);

    private static SpeakerGameIdentity? ResolveSpeakerGameIdentity(
        NativeMethods.Point cursor)
    {
        nint window = VisibleCursorService.GetTextSourceWindow(cursor);
        if (window == nint.Zero ||
            NativeMethods.GetWindowThreadProcessId(window, out uint processIdValue) == 0)
        {
            return null;
        }

        int processId = unchecked((int)processIdValue);
        return processId > 0 && processId != Environment.ProcessId
            ? SpeakerGenderPreferences.ResolveGameIdentity(processId)
            : null;
    }

    internal static HoverOcrResult ApplyTextHookMatch(
        HoverOcrResult recognized,
        TextHookMatch? match) =>
        match is null
            ? recognized
            : recognized with
            {
                Text = match.Text,
                CharacterIndex = match.CharacterIndex,
                TextSource = HoverTextSource.TextHook,
                PreviousContext = match.PreviousContext,
                CurrentSpeaker = match.CurrentSpeaker ?? recognized.CurrentSpeaker
            };

    private static bool CursorIsNear(NativeMethods.Point expected) =>
        VisibleCursorService.TryGetPosition(out NativeMethods.Point current) &&
        HoverTracking.DistanceSquared(current, expected) <= HoverTracking.RadiusSquared;

    bool IHoverHost<CursorHover>.SpeakerEditorSupported => _speakerHotkey.IsRegistered;

    private void ResetTracking() => _tracking.Reset();

    void IHoverHost<CursorHover>.ResetTracking() => ResetTracking();

    void IHoverHost<CursorHover>.ShowLoading(HoverLoadingCard<CursorHover> card) =>
        _dispatcher.BeginInvoke(
            () =>
            {
                if (!_core.IsDisposed &&
                    _core.Enabled &&
                    !_speakerEditorOpen &&
                    _core.IsCurrentDisplay(card.DisplayGeneration))
                {
                    _overlay.ShowLoading(
                        card.Source,
                        card.Anchor.Cursor,
                        card.Engine,
                        card.TextSource,
                        card.Anchor.TextHookStatus,
                        card.CurrentSpeaker,
                        card.SpeakerEditorAvailable);
                }
            },
            DispatcherPriority.Normal);

    void IHoverHost<CursorHover>.ShowTranslation(HoverCard<CursorHover> card)
    {
        _dispatcher.BeginInvoke(
            () =>
            {
                if (_core.IsDisposed ||
                    !_core.Enabled ||
                    _speakerEditorOpen ||
                    !_core.IsCurrentDisplay(card.DisplayGeneration) ||
                    card.RequireAnchorCurrent && !CursorIsNear(card.Anchor.Cursor))
                {
                    return;
                }

                _overlay.ShowTranslation(
                    card.Source,
                    card.Translation,
                    card.Anchor.Cursor,
                    card.Engine,
                    card.DictionaryFound,
                    card.IsPhrase,
                    card.Language,
                    card.Confidence,
                    card.TextSource,
                    card.PhraseLine,
                    card.Anchor.TextHookStatus,
                    card.CurrentSpeaker,
                    card.SpeakerEditorAvailable);
                _core.RecordShown(card);
            },
            DispatcherPriority.Normal);
    }

    /// <summary>History of shown translations, for the review panel and export.</summary>
    public TranslationHistoryService History => _core.History;

    private void OnSpeakerGenderHotkeyPressed(object? sender, EventArgs e)
    {
        _dispatcher.BeginInvoke(
            OpenSpeakerEditor,
            DispatcherPriority.Send);
    }

    private void OpenSpeakerEditor()
    {
        if (_core.IsDisposed || !_core.Enabled)
        {
            ReportStatus("Включите перевод под курсором, чтобы выбрать род говорящего");
            return;
        }

        if (_speakerEditorOpen)
        {
            _speakerEditor.Activate();
            return;
        }

        HoverSpeakerTarget<CursorHover>? target = _core.VisibleSpeakerTarget;
        if (target is null ||
            !_overlay.IsVisible ||
            !CursorIsNear(target.Anchor.Cursor))
        {
            ReportStatus(
                "Говорящий не распознан — наведите курсор на реплику с подписью имени");
            return;
        }

        _speakerEditorTarget = target;
        _speakerEditorOpen = true;
        StopTimer();
        _core.RestartModeCancellation();
        _core.HideOverlay();
        _speakerEditor.ShowFor(
            target.Speaker,
            target.Game.DisplayName,
            target.Anchor.Cursor);
    }

    private void OnSpeakerGenderSaved(
        object? sender,
        SpeakerGenderSavedEventArgs e)
    {
        HoverSpeakerTarget<CursorHover>? target = _speakerEditorTarget;
        if (target is null)
        {
            return;
        }

        // The core saves the choice, starts persisting the settings and
        // reports the status; the editor then keeps the re-resolved target.
        _speakerEditorTarget = _core.SaveSpeakerGender(target, e.Gender, e.Name);
    }

    private void OnSpeakerEditorHidden(object? sender, EventArgs e)
    {
        _speakerEditorOpen = false;
        _speakerEditorTarget = null;
        if (_suppressSpeakerEditorResume || _core.IsDisposed || !_core.Enabled)
        {
            return;
        }

        ResetTracking();
        _core.ConfigureActiveMode();
    }

    /// <summary>
    /// Re-reads dictionaries after the user edited their own layer, then warms
    /// them in the background so the next hover is not slowed down.
    /// </summary>
    public Task ReloadDictionariesAsync() => _core.ReloadDictionariesAsync();

    void IHoverHost<CursorHover>.HideCard() =>
        _dispatcher.BeginInvoke(_overlay.HideOverlay, DispatcherPriority.Normal);

    void IHoverHost<CursorHover>.CloseAuxiliaryWindows()
    {
        if (!_speakerEditor.IsVisible)
        {
            return;
        }

        _suppressSpeakerEditorResume = true;
        try
        {
            _speakerEditor.Cancel();
        }
        finally
        {
            _suppressSpeakerEditorResume = false;
            _speakerEditorOpen = false;
            _speakerEditorTarget = null;
        }
    }

    public void ApplyTextHookSettings()
    {
        _textHookDiagnosticProcessId = 0;
        _textHookMatchReported = false;
        _textHook.ApplySettings();
    }

    // Host statuses share the core's de-duplication state.
    private void ReportStatus(string status) => _core.ReportStatus(status);

    private static void LogError(Exception exception) => HoverDiagnostics.LogError(exception);

    public ValueTask DisposeAsync() => _core.DisposeAsync();

    async ValueTask IHoverHost<CursorHover>.ReleaseHostResourcesAsync()
    {
        await _dispatcher.InvokeAsync(() =>
        {
            _speakerHotkey.HotkeyPressed -= OnSpeakerGenderHotkeyPressed;
            _speakerHotkey.Dispose();
            _speakerEditor.Saved -= OnSpeakerGenderSaved;
            _speakerEditor.Hidden -= OnSpeakerEditorHidden;
            _suppressSpeakerEditorResume = true;
            if (_speakerEditor.IsVisible)
            {
                _speakerEditor.Cancel();
            }

            _speakerEditor.Close();
            _overlay.HideOverlay();
            _overlay.Close();
        });
        _tracking.Dispose();
        _ocr.Dispose();
    }

    async ValueTask IHoverHost<CursorHover>.ReleaseHostResourcesAfterHistoryAsync()
    {
        await _textHook.DisposeAsync();
    }

    /// <summary>
    /// One hover as the Windows host sees it: the visible cursor point, plus the
    /// Textractor state captured once the text was final.
    /// </summary>
    internal sealed class CursorHover(NativeMethods.Point cursor)
    {
        public NativeMethods.Point Cursor { get; } = cursor;

        public TextHookStatusSnapshot? TextHookStatus { get; set; }
    }
}
