using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization.Metadata;
using AsteriaScale.CoreHost.Protocol;

namespace AsteriaScale.CoreHost.Host;

// The Core-backed commands of protocol v1. core.init builds the Core session
// once; before it finishes every other command here answers not_ready. Reads
// of in-memory snapshots (settings.get, models.status) are answered on the
// control lane; everything else runs on the work lane with the cancel mode its
// side effects allow: reads are Immediate, the warm-up is Cooperative, and
// anything that writes (settings, «Мой словарь», history, unload) is
// NotCancellable, so a cancel can never misreport whether it happened. A
// settings change is queued in Core at admission (AdmitSettingsChange), so
// whatever is admitted after it sees it.
internal sealed partial class CoreHostServer
{
    private bool AdmitCore(RequestEnvelope envelope)
    {
        switch (envelope.Command)
        {
            case "core.init":
                AdmitInit(envelope);
                return true;
            case "settings.get":
                AdmitControl(envelope, J.EmptyPayload, (request, _) =>
                    Respond(request, RequireCore().Settings, J.SettingsResult));
                return true;
            case "models.status":
                AdmitControl(envelope, J.EmptyPayload, (request, _) =>
                    Respond(request, RequireCore().ModelStatus(), J.ModelsStatusResult));
                return true;
            case "settings.update":
                AdmitSettingsChange(envelope, J.SettingsUpdatePayload,
                    payload => CoreSession.Validate(payload.Patch),
                    (core, payload, holdBack) => core.QueueSettingsUpdate(payload.BaseRevision, payload.Patch, holdBack),
                    J.SettingsResult);
                return true;
            case "speaker.set":
                AdmitSettingsChange(envelope, J.SpeakerSetPayload, null,
                    (core, payload, holdBack) => core.QueueSpeakerChange(payload, holdBack),
                    J.SpeakerSetResult);
                return true;
            case "models.warmUp":
                AdmitCoreWork(envelope, J.ModelsWarmUpPayload, CancelMode.Cooperative,
                    payload => CoreSession.ValidateWarmUp(payload.Targets),
                    async (core, request, payload) => Respond(
                        request,
                        await core.WarmUpAsync(payload.Targets, request.Cancellation.Token).ConfigureAwait(false),
                        J.ModelsStatusResult));
                return true;
            case "models.unload":
                AdmitCoreWork(envelope, J.EmptyPayload, CancelMode.NotCancellable, null,
                    async (core, request, _) => Respond(
                        request,
                        await core.UnloadAsync().ConfigureAwait(false),
                        J.ModelsStatusResult));
                return true;
            case "hover.displayed":
                AdmitCoreWork(envelope, J.HoverDisplayedPayload, CancelMode.NotCancellable,
                    payload => payload.Part is "primary" or "phrase"
                        ? null
                        : Errors.BadRequest($"part must be primary or phrase, got '{payload.Part}'"),
                    (core, request, payload) =>
                    {
                        Respond(request, new HoverDisplayedResult
                        {
                            Recorded = core.RecordDisplayed(payload.HoverId, payload.Part),
                        }, J.HoverDisplayedResult);
                        return Task.CompletedTask;
                    });
                return true;
            case "userDictionary.list":
                AdmitCoreWork(envelope, J.EmptyPayload, CancelMode.Immediate, null,
                    async (core, request, _) => Respond(
                        request,
                        await core.UserDictionaryAsync(request.Cancellation.Token).ConfigureAwait(false),
                        J.UserDictionaryListResult));
                return true;
            case "userDictionary.save":
                AdmitCoreWork(envelope, J.UserDictionarySavePayload, CancelMode.NotCancellable, null,
                    async (core, request, payload) => Respond(request, new UserDictionarySaveResult
                    {
                        Key = await core.SaveUserEntryAsync(payload.Key, payload.Translation).ConfigureAwait(false),
                    }, J.UserDictionarySaveResult));
                return true;
            case "userDictionary.remove":
                AdmitCoreWork(envelope, J.UserDictionaryRemovePayload, CancelMode.NotCancellable, null,
                    async (core, request, payload) => Respond(request, new UserDictionaryRemoveResult
                    {
                        Removed = await core.RemoveUserEntryAsync(payload.Key).ConfigureAwait(false),
                    }, J.UserDictionaryRemoveResult));
                return true;
            case "history.list":
                AdmitCoreWork(envelope, J.EmptyPayload, CancelMode.Immediate, null,
                    (core, request, _) =>
                    {
                        Respond(request, core.History(), J.HistoryListResult);
                        return Task.CompletedTask;
                    });
                return true;
            case "history.exportAnki":
                AdmitCoreWork(envelope, J.EmptyPayload, CancelMode.Immediate, null,
                    (core, request, _) =>
                    {
                        Respond(request, core.ExportAnki(), J.HistoryExportResult);
                        return Task.CompletedTask;
                    });
                return true;
            case "history.clear":
                AdmitCoreWork(envelope, J.EmptyPayload, CancelMode.NotCancellable, null,
                    (core, request, _) =>
                    {
                        Respond(request, new HistoryClearResult { Cleared = core.ClearHistory() }, J.HistoryClearResult);
                        return Task.CompletedTask;
                    });
                return true;
            default:
                return false;
        }
    }

    private void AdmitInit(RequestEnvelope envelope)
    {
        if (!TryPayload(envelope, J.EmptyPayload, out _))
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _initStarted, 1, 0) != 0)
        {
            // One Core per process: a failed init leaves the process for the
            // app to restart, and a second one would build a second Core.
            _writer.FailUnadmitted(envelope.Id, Errors.BadRequest("core.init was already sent"));
            return;
        }

        if (!TryReserve(envelope))
        {
            Volatile.Write(ref _initStarted, 0);
            return;
        }

        RequestState request = Register(envelope, Lane.Work, CancelMode.NotCancellable);
        StartWork(request, async state =>
        {
            (CoreSession session, InitResult result) =
                await CoreSession.InitializeAsync(_options, EmitStatus).ConfigureAwait(false);
            Volatile.Write(ref _core, session);
            // test.fakeCore, when set, keeps the hover lane.
            Interlocked.CompareExchange(ref _hoverEngine, session, null);
            Respond(state, result, J.InitResult);
        });
    }

    private void AdmitCoreWork<T>(
        RequestEnvelope envelope,
        JsonTypeInfo<T> info,
        CancelMode mode,
        Func<T, ErrorBody?>? validate,
        Func<CoreSession, RequestState, T, Task> work)
    {
        if (TryAdmitCoreWork(envelope, info, mode, validate, out CoreSession? core, out RequestState? request, out T? payload))
        {
            StartWork(request, state => work(core, state, payload));
        }
    }

    // settings.update and speaker.set. The change joins Core's settings queue
    // here, at admission on the reader thread, and not in the work task: the
    // reader's order is the arrival order, while the pool may start a work
    // task arbitrarily late. A hover, warm-up, unload or later change admitted
    // after this one therefore always finds the change queued and waits for
    // it. Queuing never blocks (a lock and a pool task); the queued task ends
    // once settings.json holds the change (a settings.update writes it before
    // Core changes, so a failed write changes nothing), and the work task
    // only awaits it and answers.
    private void AdmitSettingsChange<T, TResult>(
        RequestEnvelope envelope,
        JsonTypeInfo<T> info,
        Func<T, ErrorBody?>? validate,
        Func<CoreSession, T, TimeSpan, Task<TResult>> queue,
        JsonTypeInfo<TResult> resultInfo)
    {
        if (!TryAdmitCoreWork(envelope, info, CancelMode.NotCancellable, validate,
                out CoreSession? core, out RequestState? request, out T? payload))
        {
            return;
        }

        // test.delayNextWork holds back the change itself; its place in the
        // queue is taken now all the same.
        Task<TResult> saved = queue(core, payload, TakeTestHoldBack());
        StartWork(request, async state => Respond(state, await saved.ConfigureAwait(false), resultInfo));
    }

    private bool TryAdmitCoreWork<T>(
        RequestEnvelope envelope,
        JsonTypeInfo<T> info,
        CancelMode mode,
        Func<T, ErrorBody?>? validate,
        [NotNullWhen(true)] out CoreSession? core,
        [NotNullWhen(true)] out RequestState? request,
        [NotNullWhen(true)] out T? payload)
    {
        core = null;
        request = null;
        if (!TryPayload(envelope, info, out payload))
        {
            return false;
        }

        if (Volatile.Read(ref _core) is not { } session)
        {
            _writer.FailUnadmitted(envelope.Id, NotInitialized());
            return false;
        }

        if (validate?.Invoke(payload) is { } problem)
        {
            _writer.FailUnadmitted(envelope.Id, problem);
            return false;
        }

        if (!TryReserve(envelope))
        {
            return false;
        }

        core = session;
        request = Register(envelope, Lane.Work, mode);
        return true;
    }

    private CoreSession RequireCore() =>
        Volatile.Read(ref _core) ?? throw new RequestErrorException(NotInitialized());

    private static ErrorBody NotInitialized() => Errors.NotReady("core.init has not completed");
}
