using System.Reflection;
using AsteriaScale.CoreHost.Protocol;
using AsteriaScale.Models;
using AsteriaScale.Services;

namespace AsteriaScale.CoreHost.Host;

// The Core-backed commands other than hover.resolve: settings, model status,
// warm-up and unload, history of what the app showed, «Мой словарь» and the
// speaker gender.
internal sealed partial class CoreSession
{
    private const int RecentHoverCount = 8;

    private static readonly string[] WarmUpTargets = ["dictionary", "sense"];

    // Settings changes (settings.update, speaker.set) join _applyTail when they
    // are admitted, on the reader thread, and run one at a time in that
    // arrival order, each after the previous one, under _settingsGate; the
    // gate also covers every save, so settings.json is never serialized while
    // a change is half applied. Each change writes settings.json under the
    // gate before its task ends, and writes its changed copy before Core
    // changes at all, so a failed write leaves Core (the speaker genders
    // included), the revision and the view as they were. The tail never
    // faults, so a hover, capture plan, warm-up or unload can wait for it
    // whatever the change before it did.
    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    private readonly object _applyLock = new();
    private readonly object _recentLock = new();
    private readonly LinkedList<RecentHover> _recent = new();
    private Task _applyTail = Task.CompletedTask;
    private long _revision;
    private SettingsView _settingsView;
    private RankerFailure? _lastRankerFailure;

    public SettingsResult Settings
    {
        get
        {
            lock (_applyLock)
            {
                return new SettingsResult { Settings = _settingsView, Revision = _revision };
            }
        }
    }

    /// <summary>
    /// Checks a patch without touching anything. Nothing is coerced: an NPU
    /// backend or the TranslateGemma phrase line is refused as unavailable on
    /// macOS (rule 3), any other unknown spelling as invalid.
    /// </summary>
    public static ErrorBody? Validate(SettingsPatch patch)
    {
        if (patch.HoverDelayMs is int delay && delay is < 350 or > 1500)
        {
            return Errors.InvalidSettings($"hoverDelayMs must be 350..1500 ms, got {delay}");
        }

        if (patch.Mode is { } mode && mode is not (HoverTranslationModes.Smart or HoverTranslationModes.Dictionary))
        {
            return Errors.InvalidSettings($"mode must be smart or dictionary, got '{mode}'");
        }

        if (patch.Sense is { } sense)
        {
            if (sense.Backend == SmartDictionaryBackends.Npu)
            {
                return Errors.UnsupportedOnPlatform("the npu sense backend needs a Windows NPU; macOS ranks senses on the CPU");
            }

            if (sense.Backend is { } backend && backend is not (SmartDictionaryBackends.Auto or SmartDictionaryBackends.Cpu))
            {
                return Errors.InvalidSettings($"sense.backend must be auto or cpu, got '{backend}'");
            }

            if (sense.Model is { } model && model is not (SmartDictionaryRankerModels.GlossBert or SmartDictionaryRankerModels.Consec))
            {
                return Errors.InvalidSettings($"sense.model must be glossbert or consec, got '{model}'");
            }

            if (sense.SourceLanguage is { } language && language is not (
                    SmartDictionarySourceLanguages.Auto or
                    SmartDictionarySourceLanguages.English or
                    SmartDictionarySourceLanguages.Japanese))
            {
                return Errors.InvalidSettings($"sense.sourceLanguage must be auto, en or ja, got '{language}'");
            }
        }

        if (patch.Phrase?.Mode is { } phraseMode)
        {
            if (phraseMode == SmartPhraseTranslationModes.TranslateGemma)
            {
                return Errors.UnsupportedOnPlatform("the TranslateGemma phrase line arrives on macOS with 20.5");
            }

            if (phraseMode != SmartPhraseTranslationModes.Off)
            {
                return Errors.InvalidSettings($"phrase.mode must be off, got '{phraseMode}'");
            }
        }

        return null;
    }

    /// <summary>
    /// Queues a validated settings.update behind the changes admitted before
    /// it and returns once it is saved and applied (later hovers see it).
    /// The change is transactional: settings.json is written first, from a
    /// patched copy, and only a successful write lets the patch reach Core,
    /// the revision and the view. A failed write (settings_save_failed)
    /// leaves all of them, and the coordinator's lifecycle, as they were:
    /// the app keeps showing its previous snapshot after a refusal, so
    /// anything else would show a state CoreHost no longer has. Called at
    /// admission; <paramref name="holdBack"/> is zero except under
    /// test.delayNextWork.
    /// </summary>
    public Task<SettingsResult> QueueSettingsUpdate(long baseRevision, SettingsPatch patch, TimeSpan holdBack) =>
        QueueSettingsChange(holdBack, async () =>
        {
            if (baseRevision != _revision)
            {
                throw new RequestErrorException(Errors.Conflict(_revision));
            }

            await WriteSettingsAsync(Patched(_settings, patch)).ConfigureAwait(false);
            ApplyPatch(patch);
            SettingsView view = View(_settings);
            lock (_applyLock)
            {
                _revision++;
                _settingsView = view;
                return new SettingsResult { Settings = view, Revision = _revision };
            }
        });

    /// <summary>
    /// speaker.set: stores the gender of a speaker in one game (the alias for
    /// a corrected spelling included) and returns the speaker as it now
    /// resolves, with the spelling it was stored under. The speaker editor
    /// that sends it arrives with 20.3. Queued at admission like a
    /// settings.update; speaker genders are not part of the settings view, so
    /// the revision stays. observedName is the spelling the card carried
    /// (hover.primary's speaker.observedName) and is used as it is: resolving
    /// it again would turn a corrected spelling into its display name, and a
    /// second edit would never reach the entry the next hover reads.
    /// Transactional like settings.update (docs/MACOS.md T30): Core's
    /// SaveSpeakerGender changes the live list, reports the choice saved and
    /// queues a save of its own, all before CoreHost could write anything, so
    /// the change is first made on a copy of the list and written; only a
    /// successful write swaps the copy in. Core's call then finds the choice
    /// stored, saves nothing and reports it (PersistSettingsAsync names the
    /// cases where it would still save). A failed write is
    /// settings_save_failed, and the genders, the status and the file stay as
    /// they were.
    /// </summary>
    public Task<SpeakerSetResult> QueueSpeakerChange(SpeakerSetPayload payload, TimeSpan holdBack) =>
        QueueSettingsChange(holdBack, async () =>
        {
            SpeakerGender gender = payload.Gender switch
            {
                SpeakerGenderPreferences.MasculineValue => SpeakerGender.Masculine,
                SpeakerGenderPreferences.FeminineValue => SpeakerGender.Feminine,
                SpeakerGenderPreferences.IgnoreValue => SpeakerGender.Ignore,
                _ => throw new RequestErrorException(Errors.BadRequest(
                    $"gender must be masculine, feminine or ignore, got '{payload.Gender}'")),
            };
            SpeakerGameIdentity game = new(payload.Game.Key, payload.Game.DisplayName);
            string observedName = SpeakerGenderPreferences.NormalizeSpeakerName(payload.ObservedName);
            (string gameKey, bool stableKey) = NormalizeGameKey(game.GameKey, game.DisplayName);
            if (gameKey.Length == 0 || observedName.Length == 0 ||
                SpeakerGenderPreferences.NormalizeSpeakerName(payload.Name).Length == 0)
            {
                // Core would store nothing and still report the choice saved.
                throw new RequestErrorException(Errors.BadRequest(
                    "game.key must start with path: or process:, and both names must hold 1..48 characters"));
            }

            // Core normalizes a stored game key again on every pass, and some
            // keys change on the second one (NormalizeGameKey). Such a key
            // never resolves, and Core's own call after the swap would add
            // the choice to the live list again and queue a save of its own,
            // so it is refused like a key Core drops.
            if (!stableKey)
            {
                throw new RequestErrorException(Errors.BadRequest(
                    $"game.key must keep its normalized form when normalized again, got '{payload.Game.Key}'"));
            }

            HoverSpeakerTarget<CoreHostHover> target = new(
                game,
                new CurrentSpeakerContext(observedName, SpeakerGender.Unknown, gameKey),
                CoreHostHover.Detached);
            AppSettings candidate = CopyOf(_settings);
            // CopyOf shares the list; Set happens to replace it (Normalize),
            // which this must not rely on.
            candidate.SpeakerGenders = [.. _settings.SpeakerGenders];
            StoreSpeakerGender(candidate, target, gender, payload.Name);
            await WriteSettingsAsync(candidate).ConfigureAwait(false);
            // One reference store: a hover running meanwhile reads either list
            // whole, and CoreHost changes no list a hover can see in place.
            _settings.SpeakerGenders = candidate.SpeakerGenders;
            HoverSpeakerTarget<CoreHostHover> saved = _coordinator.SaveSpeakerGender(target, gender, payload.Name);
            return new SpeakerSetResult { Speaker = ToSpeakerView(saved.Speaker, observedName) };
        });

    // Core's SaveSpeakerGender (HoverCoordinator) stores exactly this: the
    // choice under the observed spelling and, when the user's name differs
    // from it, under that name too, so a later frame that recognizes the
    // correction cleanly finds it. The operands are the ones Core compares,
    // so for a game key speaker.set accepts its own call after the swap
    // changes nothing and queues no save; a change to Core's rule must
    // change this copy with it, or that save comes back (PersistSettingsAsync
    // logs it, and the CoreHost speaker check fails on the line).
    private static void StoreSpeakerGender(
        AppSettings settings,
        HoverSpeakerTarget<CoreHostHover> target,
        SpeakerGender gender,
        string name)
    {
        SpeakerGenderPreferences.Set(settings, target.Game, target.Speaker.Name, gender, name);
        if (!string.Equals(target.Speaker.Name, name, StringComparison.OrdinalIgnoreCase))
        {
            SpeakerGenderPreferences.Set(settings, target.Game, name, gender, name);
        }
    }

    // A settings change's own write: the change holds _settingsGate, which is
    // not reentrant, so this must not take it again. A failed write is
    // settings_save_failed.
    private async Task WriteSettingsAsync(AppSettings settings)
    {
        try
        {
            await _settingsService.SaveAsync(settings).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new RequestErrorException(Errors.SettingsSaveFailed(exception.Message));
        }
    }

    // The coordinator's save hook, which only SaveSpeakerGender reaches, and
    // only when it found a choice not stored yet. speaker.set stores and
    // writes the choice before Core's call (StoreSpeakerGender) and refuses a
    // game key Core changes again on a second pass, so this runs only if
    // that copy of Core's rule fell behind Core, or if a hand-edited
    // settings.json holds the same speaker under such a key (Core's next
    // pass then merges that entry over the stored choice and stores the
    // choice again). It then still saves what memory holds, and says so on
    // stderr, where the CoreHost speaker check looks for the line. It runs
    // outside any change, so it takes the gate itself.
    private async Task PersistSettingsAsync()
    {
        HostLog.Write("Core saved the settings itself after speaker.set: " +
                      "CoreHost's copy of Core's speaker-gender rule differs from Core's, " +
                      "or settings.json holds this speaker under a game key Core shortens on each pass");
        await _settingsGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _settingsService.SaveAsync(_settings).ConfigureAwait(false);
        }
        finally
        {
            _settingsGate.Release();
        }
    }

    // Called on the reader thread at admission (CoreHostServer.AdmitSettingsChange),
    // so the queue order is the arrival order. The change itself runs on the
    // pool: the coordinator's lifecycle calls must never run on the reader
    // thread, which admits requests and never blocks. holdBack starts it late
    // the way a starved pool would (test.delayNextWork only).
    private Task<T> QueueSettingsChange<T>(TimeSpan holdBack, Func<Task<T>> change)
    {
        lock (_applyLock)
        {
            Task previous = _applyTail;
            Task<T> applied = Task.Run(async () =>
            {
                if (holdBack > TimeSpan.Zero)
                {
                    await Task.Delay(holdBack).ConfigureAwait(false);
                }

                await previous.ConfigureAwait(false);
                await _settingsGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    return await change().ConfigureAwait(false);
                }
                finally
                {
                    _settingsGate.Release();
                }
            });
            _applyTail = applied.ContinueWith(
                static _ => { },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return applied;
        }
    }

    private Task AppliedSettingsAsync()
    {
        lock (_applyLock)
        {
            return _applyTail;
        }
    }

    // The order the Windows settings window leaves Core in: values first,
    // then one lifecycle call per kind of change. Switching off goes first and
    // switching on last, so neither warms up a mode that is about to change.
    private void ApplyPatch(SettingsPatch patch)
    {
        if (patch.HoverEnabled == false && _coordinator.Enabled)
        {
            _coordinator.SetEnabled(false);
        }

        if (patch.HoverDelayMs is int delay)
        {
            _settings.HoverTranslationDelayMs = delay;
        }

        bool smartChanged = false;
        if (patch.Sense is { } sense)
        {
            if (sense.Backend is { } backend &&
                backend != SmartDictionaryBackends.Normalize(_settings.SmartDictionaryBackend))
            {
                _settings.SmartDictionaryBackend = backend;
                smartChanged = true;
            }

            if (sense.Model is { } model &&
                model != SmartDictionaryRankerModels.Normalize(_settings.SmartDictionaryRankerModel))
            {
                _settings.SmartDictionaryRankerModel = model;
                smartChanged = true;
            }

            if (sense.SourceLanguage is { } language &&
                language != SmartDictionarySourceLanguages.Normalize(_settings.SmartDictionarySourceLanguage))
            {
                _settings.SmartDictionarySourceLanguage = language;
                smartChanged = true;
            }

            if (sense.KeepWarm is bool keepWarm && keepWarm != _settings.SmartDictionaryKeepWarm)
            {
                _settings.SmartDictionaryKeepWarm = keepWarm;
                smartChanged = true;
            }
        }

        if (patch.Phrase?.Mode is { } phraseMode &&
            phraseMode != SmartPhraseTranslationModes.Normalize(_settings.SmartPhraseTranslationMode))
        {
            _settings.SmartPhraseTranslationMode = phraseMode;
            smartChanged = true;
        }

        if (patch.Mode is { } mode &&
            mode != HoverTranslationModes.NormalizeUserMode(_settings.HoverTranslationMode))
        {
            _coordinator.SetMode(mode);
        }

        if (smartChanged)
        {
            _coordinator.ApplySmartDictionarySettings();
        }

        if (patch.HoverEnabled == true && !_coordinator.Enabled)
        {
            _coordinator.SetEnabled(true);
        }
    }

    // What ApplyPatch leaves in the settings, computed on a copy: the copy is
    // what settings.update writes before Core sees the patch. Every value is
    // the validated one, which ApplyPatch stores as it is (the stored ones
    // were normalized when settings.json was read).
    private static AppSettings Patched(AppSettings settings, SettingsPatch patch)
    {
        AppSettings patched = CopyOf(settings);
        if (patch.HoverEnabled is bool enabled)
        {
            patched.HoverTranslationEnabled = enabled;
        }

        if (patch.HoverDelayMs is int delay)
        {
            patched.HoverTranslationDelayMs = delay;
        }

        if (patch.Mode is { } mode)
        {
            patched.HoverTranslationMode = HoverTranslationModes.NormalizeUserMode(mode);
        }

        if (patch.Sense is { } sense)
        {
            patched.SmartDictionaryBackend = sense.Backend ?? patched.SmartDictionaryBackend;
            patched.SmartDictionaryRankerModel = sense.Model ?? patched.SmartDictionaryRankerModel;
            patched.SmartDictionarySourceLanguage = sense.SourceLanguage ?? patched.SmartDictionarySourceLanguage;
            patched.SmartDictionaryKeepWarm = sense.KeepWarm ?? patched.SmartDictionaryKeepWarm;
        }

        if (patch.Phrase?.Mode is { } phraseMode)
        {
            patched.SmartPhraseTranslationMode = phraseMode;
        }

        return patched;
    }

    // Every public property, so a property Core adds later is written too;
    // reflection over the statically known type is what the trim and AOT
    // analyzers accept here, a JSON round trip would need the reflection
    // serializer they refuse in CoreHost's own code. The lists are shared,
    // not copied: no settings.update changes them, and speaker.set, the only
    // change that does, copies the speaker list itself before changing it.
    private static AppSettings CopyOf(AppSettings settings)
    {
        AppSettings copy = new();
        foreach (PropertyInfo property in typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.CanRead && property.CanWrite)
            {
                property.SetValue(copy, property.GetValue(settings));
            }
        }

        return copy;
    }

    private static SettingsView View(AppSettings settings) => new()
    {
        HoverEnabled = settings.HoverTranslationEnabled,
        HoverDelayMs = settings.HoverTranslationDelayMs,
        Mode = HoverTranslationModes.NormalizeUserMode(settings.HoverTranslationMode),
        Sense = new SenseSettingsView
        {
            Backend = SmartDictionaryBackends.Normalize(settings.SmartDictionaryBackend),
            Model = SmartDictionaryRankerModels.Normalize(settings.SmartDictionaryRankerModel),
            SourceLanguage = SmartDictionarySourceLanguages.Normalize(settings.SmartDictionarySourceLanguage),
            KeepWarm = settings.SmartDictionaryKeepWarm,
        },
        Phrase = new PhraseSettingsView
        {
            Mode = SmartPhraseTranslationModes.Normalize(settings.SmartPhraseTranslationMode),
            Backend = SmartPhraseTranslationBackends.Normalize(settings.SmartPhraseTranslationBackend),
        },
    };

    // ---- models --------------------------------------------------------------

    /// <summary>
    /// models.status from the coordinator's snapshot, which waits for nothing
    /// and loads nothing.
    /// </summary>
    public ModelsStatusResult ModelStatus()
    {
        HoverModelStatus status = _coordinator.GetModelStatus();
        return new ModelsStatusResult
        {
            Dictionary = new DictionaryModelStatus
            {
                Loaded = status.Dictionary.Loaded,
                Entries = status.Dictionary.Entries,
                Sources = status.Dictionary.Sources,
                Version = status.Dictionary.Version,
                MorphologyEntries = RussianMorphologyLexicon.EntryCount,
                SenseMetadataKeys = status.Dictionary.SenseMetadataKeys,
            },
            Sense = new SenseModelStatus
            {
                InUse = status.Sense.InUse,
                Provider = ProviderName(status.Sense.Provider),
                Model = status.Sense.Model == RankerCpuModelProfile.Consec
                    ? SmartDictionaryRankerModels.Consec
                    : SmartDictionaryRankerModels.GlossBert,
                KeepWarm = status.Sense.KeepWarm,
                Running = status.Sense.Running,
                Pid = status.Sense.ProcessId,
                ActiveProvider = status.Sense.ActiveProvider is { } active ? ProviderName(active) : null,
                ModelVersion = status.Sense.ActiveModelVersion,
                Error = status.Sense.LastErrorCode is { } code ? ToSenseError(code) : null,
            },
            Phrase = new PhraseModelStatus
            {
                Available = status.Phrase.Available,
                Requested = status.Phrase.Requested,
                WarmedUp = status.Phrase.WarmedUp,
                Status = status.Phrase.UnavailableStatus,
            },
        };
    }

    public static ErrorBody? ValidateWarmUp(string[] targets) =>
        targets.Length == 0 ||
        targets.Distinct(StringComparer.Ordinal).Count() != targets.Length ||
        targets.Any(target => !WarmUpTargets.Contains(target))
            ? Errors.BadRequest("targets must name dictionary and/or sense, each once")
            : null;

    /// <summary>
    /// models.warmUp: loads the dictionaries the active mode reads and/or
    /// starts the sense worker for the selected model and backend, as the
    /// coordinator's own warm-up does. A worker error is answered with its
    /// code and Russian text (and stays in models.status until a later
    /// transaction succeeds); nothing falls back to another provider.
    /// </summary>
    public async Task<ModelsStatusResult> WarmUpAsync(string[] targets, CancellationToken cancellationToken)
    {
        // The mode, model and backend of every settings change admitted
        // before this warm-up decide what it loads.
        await AppliedSettingsAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        foreach (string target in targets)
        {
            if (target == "dictionary")
            {
                bool senseMetadata =
                    HoverTranslationModes.NormalizeUserMode(_settings.HoverTranslationMode) == HoverTranslationModes.Smart &&
                    SmartDictionarySourceLanguages.Normalize(_settings.SmartDictionarySourceLanguage) !=
                    SmartDictionarySourceLanguages.Japanese;
                await (senseMetadata
                    ? _services.Dictionary.LoadSenseMetadataAsync(cancellationToken)
                    : _services.Dictionary.LoadAsync(cancellationToken)).ConfigureAwait(false);
                continue;
            }

            GlossBertRankerProvider provider =
                SmartDictionaryBackends.Normalize(_settings.SmartDictionaryBackend) == SmartDictionaryBackends.Npu
                    ? GlossBertRankerProvider.Npu
                    : GlossBertRankerProvider.Cpu;
            try
            {
                await _services.Selector.ConfigureCpuModelProfileAsync(
                    SemanticRankerProfileResolver.FromSettings(_settings),
                    cancellationToken).ConfigureAwait(false);
                await _services.Selector.WarmUpAsync(provider, cancellationToken).ConfigureAwait(false);
            }
            catch (GlossBertRankerException exception)
            {
                Volatile.Write(ref _lastRankerFailure, new RankerFailure(exception.ErrorCode, exception.Message));
                HostLog.Write($"sense warm-up failed: {exception.ErrorCode}: {exception.Message}");
                throw new RequestErrorException(Errors.Create(
                    exception.ErrorCode,
                    RankerErrorText.For(exception.ErrorCode),
                    exception.Message));
            }
        }

        return ModelStatus();
    }

    /// <summary>
    /// models.unload: Core's coalesced unload pass on the host's behalf; it
    /// unloads what the settings do not need, the changes admitted before it
    /// included, so it first waits for those to be applied.
    /// </summary>
    public async Task<ModelsStatusResult> UnloadAsync()
    {
        await AppliedSettingsAsync().ConfigureAwait(false);
        await _coordinator.RequestUnload().ConfigureAwait(false);
        return ModelStatus();
    }

    private SenseError ToSenseError(string code)
    {
        RankerFailure? failure = Volatile.Read(ref _lastRankerFailure);
        return new SenseError
        {
            Code = code,
            Message = RankerErrorText.For(code),
            Detail = failure is not null && failure.Code == code
                ? failure.Detail
                : $"the last sense worker transaction failed with {code}",
        };
    }

    private sealed record RankerFailure(string Code, string Detail);

    // ---- history of what the app showed --------------------------------------

    private void RememberShown(HoverCard<CoreHostHover> card)
    {
        lock (_recentLock)
        {
            RecentHover? entry = _recent.FirstOrDefault(candidate => candidate.Id == card.Anchor.Id);
            if (entry is null)
            {
                entry = new RecentHover(card.Anchor.Id);
                _recent.AddFirst(entry);
                while (_recent.Count > RecentHoverCount)
                {
                    _recent.RemoveLast();
                }
            }

            if (card.PhraseLine is null)
            {
                entry.Primary = card;
            }
            else if (!card.PhraseLine.IsProvisional)
            {
                entry.FinalPhrase = card;
            }
        }
    }

    /// <summary>
    /// hover.displayed: the app rendered a part of one of the last eight
    /// hovers, so Core records it now, by its own rules (a found primary
    /// result; a final translated phrase line), once per part. History holds
    /// only what the user saw, as on Windows.
    /// </summary>
    public bool RecordDisplayed(long hoverId, string part)
    {
        HoverCard<CoreHostHover>? card;
        lock (_recentLock)
        {
            RecentHover? entry = _recent.FirstOrDefault(candidate => candidate.Id == hoverId);
            if (entry is null)
            {
                return false;
            }

            if (part == "primary")
            {
                card = entry.PrimaryRecorded ? null : entry.Primary;
                entry.PrimaryRecorded |= card is not null;
            }
            else
            {
                card = entry.PhraseRecorded ? null : entry.FinalPhrase;
                entry.PhraseRecorded |= card is not null;
            }
        }

        if (card is null)
        {
            return false;
        }

        _coordinator.RecordShown(card);
        return card.PhraseLine is null ? card.DictionaryFound : HoverPolicy.IsPhraseHistoryEntry(card.PhraseLine);
    }

    public HistoryListResult History() => new()
    {
        Entries = _coordinator.History.Snapshot()
            .Select(entry => new HistoryItem
            {
                Source = entry.Source,
                Translation = entry.Translation,
                Engine = entry.Engine,
                Context = entry.Context,
                Language = entry.Language,
                RecordedAt = entry.RecordedAt,
            })
            .ToArray(),
    };

    public bool ClearHistory() => _coordinator.History.Clear();

    /// <summary>
    /// The Anki export as text. Core writes it to a file, so it goes to a
    /// private file in CoreHost's TMPDIR, is read back and deleted: the app,
    /// not CoreHost, writes where the user chose.
    /// </summary>
    public HistoryExportResult ExportAnki()
    {
        string path = Path.Combine(Path.GetTempPath(), $"asteria-anki-{Guid.NewGuid():N}.tsv");
        try
        {
            int count = _coordinator.History.ExportToAnki(path);
            return new HistoryExportResult
            {
                Count = count,
                // ReadAllText drops the UTF-8 BOM Core writes for Anki.
                Tsv = count == 0 ? "" : File.ReadAllText(path),
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new RequestErrorException(Errors.IoError(exception.Message));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- «Мой словарь» -------------------------------------------------------

    public async Task<UserDictionaryListResult> UserDictionaryAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<UserDictionaryEntry> entries = await UserDictionaryIoAsync(
            () => _userDictionary.LoadAsync(cancellationToken)).ConfigureAwait(false);
        return new UserDictionaryListResult
        {
            Entries = entries
                .Select(entry => new UserDictionaryItem
                {
                    Key = entry.Key,
                    Translation = entry.Translation,
                    Language = entry.Language,
                })
                .ToArray(),
        };
    }

    public async Task<string> SaveUserEntryAsync(string key, string translation)
    {
        string canonicalKey = await UserDictionaryIoAsync(
            () => _userDictionary.SaveEntryAsync(key, translation)).ConfigureAwait(false);
        await ReloadDictionariesAsync().ConfigureAwait(false);
        return canonicalKey;
    }

    public async Task<bool> RemoveUserEntryAsync(string key)
    {
        bool removed = await UserDictionaryIoAsync(
            () => _userDictionary.RemoveEntryAsync(key)).ConfigureAwait(false);
        if (removed)
        {
            await ReloadDictionariesAsync().ConfigureAwait(false);
        }

        return removed;
    }

    // The user layer has the highest priority, so an edit is visible to the
    // next hover only after Core re-reads the layers (under its maintenance
    // lease, after the hover in flight).
    private async Task ReloadDictionariesAsync()
    {
        await _coordinator.ReloadDictionariesAsync().ConfigureAwait(false);
        LocalDictionaryService dictionary = _services.Dictionary;
        _status("dictionary", "ready", null,
            $"Словари перезагружены: {dictionary.EntryCount:N0} записей из {dictionary.SourceCount} словарей");
    }

    private static async Task<T> UserDictionaryIoAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            // Core's reason is Russian; the framework appends the parameter
            // name in English, which is not for the user.
            string message = exception.Message;
            int parameter = message.IndexOf(" (Parameter '", StringComparison.Ordinal);
            throw new RequestErrorException(Errors.InvalidEntry(parameter < 0 ? message : message[..parameter]));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new RequestErrorException(Errors.IoError(exception.Message));
        }
    }

    private sealed class RecentHover(long id)
    {
        public long Id { get; } = id;

        public HoverCard<CoreHostHover>? Primary { get; set; }

        public HoverCard<CoreHostHover>? FinalPhrase { get; set; }

        public bool PrimaryRecorded { get; set; }

        public bool PhraseRecorded { get; set; }
    }
}
