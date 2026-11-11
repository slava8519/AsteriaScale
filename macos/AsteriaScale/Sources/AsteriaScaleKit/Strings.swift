import Foundation

/// Every user-facing string of the app (rule 9: Russian), in one place so a
/// test can check them all. Owner decision M15: the interface names the
/// translation, never the internal host, so the state reads «Перевод: готов»,
/// «Перевод: перезапуск…» or «Перевод: недоступен», and a reason says
/// «служба перевода»; an error code may follow in the details. No label says
/// "NPU OCR" or "VULKAN" on a Mac (DoD 3).
public enum RU {
    // Status item menu. The first four mirror TrayIconService.cs on Windows.
    public static let appName = "Asteria Scale"
    public static let menuHoverTranslation = "Перевод текста"
    public static let menuTranslationMode = "Режим перевода"
    public static let menuModeSmart = "Умный словарь — выбор по контексту"
    public static let menuModeDictionary = "Словарный — слово или выражение"
    public static let menuCheckTranslation = "Проверка перевода…"
    public static let menuTranslateClipboard = "Перевести текст из буфера обмена"
    public static let menuSettings = "Настройки…"
    public static let menuRestartTranslation = "Перезапустить перевод"
    public static let menuQuit = "Выход"
    /// The alert of a second instance.
    public static let alreadyRunning = "Asteria Scale уже запущен: его значок — в строке меню."
    /// The alert of a start that cannot create its folders: a Russian
    /// reason and the error's domain and code, never Foundation's
    /// description, which follows the system language (rule 9); the app
    /// writes the description to stderr.
    public static func startFoldersFailed(_ error: any Error) -> String {
        let error = error as NSError
        return "Asteria Scale не запустился: не удалось создать папки приложения в ~/Library"
            + " (\(error.domain) \(error.code))."
    }

    /// The alert of a start whose single-instance lock file cannot be
    /// opened or locked: the failed call and its errno, not strerror's
    /// English text, which the app writes to app.log.
    public static func startLockFailed(_ failure: InstanceLock.Failure) -> String {
        "Asteria Scale не запустился: не удалось открыть файл блокировки экземпляра"
            + " (\(failure.call), errno \(failure.code))."
    }

    // The translation state line under the menu-bar icon (M15).
    public static let stateStopped = "Перевод: остановлен"
    public static let stateStarting = "Перевод: запуск…"
    public static let stateReady = "Перевод: готов"
    public static let stateRestarting = "Перевод: перезапуск…"
    public static let stateUnavailable = "Перевод: недоступен"

    /// The details under a restarting state.
    public static func restartDetail(attempt: Int, delay: Duration, reason: String) -> String {
        "Попытка \(attempt) через \(format(delay)): \(reason)"
    }

    // Why the translation service stopped or is unavailable.
    public static let reasonStopped = "служба перевода остановлена"
    public static let reasonExitedUnexpectedly = "служба перевода неожиданно завершилась"
    public static let reasonProtocolBroken = "служба перевода нарушила протокол обмена"
    public static let reasonHelloRejected = "служба перевода не прошла проверку при запуске"
    public static let reasonNoAnswer = "служба перевода не отвечает"
    public static let reasonHelperMissing = "не найден исполняемый файл службы перевода"
    public static let reasonSpawnFailed = "не удалось запустить службу перевода"
    public static let reasonCrashLoop = "служба перевода завершается при каждом запуске; перезапустите перевод из меню"

    /// A failed start whose cause CoreHost named itself (morphology_missing,
    /// dictionary_missing): its Russian message, with the code for details.
    public static func reasonInitFailed(_ message: String, code: String) -> String { "\(message) (\(code))" }

    /// Client-side error texts; CoreHost's own errors carry their own message.
    public static func errorText(_ code: String) -> String {
        switch code {
        case "connection_lost": return "Служба перевода перезапустилась, запрос прерван."
        case "timeout": return "Служба перевода не ответила вовремя."
        case "not_ready": return "Служба перевода ещё не готова."
        case "not_running": return "Служба перевода остановлена и сама не перезапустится."
        case "cancelled": return "Запрос отменён."
        case "superseded": return "Запрос заменён новым наведением."
        case "bad_request": return "Некорректный запрос к службе перевода."
        case "request_too_large": return "Запрос к службе перевода слишком длинный."
        case "conflict": return "Настройки перевода изменились одновременно с запросом; повторите."
        case "bad_response": return "Служба перевода ответила в неверном формате."
        case "observations_too_large": return "Слишком много текста в области распознавания."
        case "stale_frame": return "Кадр на экране изменился."
        default: return "Ошибка службы перевода (\(code))."
        }
    }

    // The menu's model line (models.status). A worker error is shown as the
    // Russian sentence CoreHost sends, which names the code.
    public static let senseNotInUse = "Модель значений: не используется"
    public static let senseUnknown = "Модель значений: состояние неизвестно"
    public static func senseModelName(_ model: String, provider: String) -> String {
        let name = model == "consec" ? "ConSeC" : model == "glossbert" ? "GlossBERT" : model
        return provider.isEmpty ? name : "\(name), \(provider.uppercased())"
    }
    public static func senseRunning(_ name: String) -> String { "Модель значений: \(name) — загружена" }
    public static func senseIdle(_ name: String) -> String { "Модель значений: \(name) — не загружена" }

    // Overlay. The two loading lines are the WPF overlay's wording.
    public static let overlayLookingUpDictionary = "Ищу в локальных словарях…"
    public static let overlayCheckingContext = "Сверяю значения с контекстом…"
    public static let overlayNotFound = "Нет в словарях"
    public static let overlayUnavailable = "Перевод недоступен — служба перевода перезапускается"
    /// Failed or stopped: no restart is scheduled until the user asks for one.
    public static let overlayNotRunning = "Перевод недоступен — перезапустите перевод из меню"
    public static let overlayFailed = "Перевод не выполнен — подробности в окне «Проверка перевода»"
    /// Shown for a moment by the app's self-test only.
    public static let overlaySelfTest = "Проверка оверлея"

    // Text recognition (Apple Vision, 20.3).
    /// The captured frame is not the size of its capture bounds (T24): the
    /// boxes would not fit the screen, so nothing is recognized.
    public static let recognitionFrameMismatch = "Не удалось сопоставить кадр с экраном."
    public static func recognitionFailed(_ code: String) -> String { "Не удалось распознать текст (\(code))." }
    // The recognition line of the menu (DoD 20.3 items 6-8).
    public static let recognitionPreparing = "Распознавание: подготовка…"
    public static let recognitionReady = "Распознавание: готово"
    public static func recognitionUnavailable(_ reason: String) -> String { "Распознавание недоступно: \(reason)" }
    /// The reason of a failed Vision warm-up, with the failure's code.
    public static func recognitionWarmUpFailed(_ code: String) -> String { "прогрев Vision не удался (\(code))" }
    public static let recognitionNeedsPermission = "Распознавание: нужен доступ к записи экрана"
    /// After «Разрешить запись экрана…» in this run: assumed that macOS
    /// applies a new grant to a running app only after a relaunch (the
    /// owner checks it, roadmap DoD 20.3 item 12.2).
    public static let recognitionRestartAfterGrant =
        "Распознавание: после разрешения записи экрана перезапустите Asteria Scale"
    /// A self-test or a direct start from a terminal or a script (T22).
    public static let recognitionDisabledCheckRun = "Распознавание отключено: проверочный запуск"
    public static let menuAllowScreenRecording = "Разрешить запись экрана…"
    // Capture failures on the overlay.
    public static let captureNoPermission =
        "Нет доступа к записи экрана: меню Asteria Scale → «Разрешить запись экрана…»."
    public static let captureSelfMissing = "Не удалось исключить окна Asteria Scale из захвата экрана."
    public static let captureDisplayMissing = "Дисплей под курсором недоступен для захвата экрана."
    public static func captureFailed(_ code: String) -> String { "Не удалось снять экран (\(code))." }

    // Hotkeys (M14).
    public static let hotkeyToggleHover = "Включить или выключить перевод при наведении"
    public static let hotkeyShowDiagnostic = "Открыть окно «Проверка перевода»"
    public static let hotkeyTranslateClipboard = "Перевести текст из буфера обмена"
    public static func hotkeyTaken(_ gesture: String) -> String { "Сочетание \(gesture) уже занято другим приложением" }
    public static func hotkeyFailed(_ gesture: String, status: Int32) -> String {
        "Не удалось зарегистрировать сочетание \(gesture) (код \(status))"
    }
    public static let hotkeyNotAssigned = "не назначено"
    public static func hotkeyLine(_ title: String, _ gesture: String) -> String { "\(title): \(gesture)" }

    // The 20.2 diagnostic hover source; Apple Vision replaces it in 20.3.
    public static let diagnosticTitle = "Проверка перевода"
    public static let diagnosticPrompt = "Введите фразу. Слово в [квадратных скобках] — слово под курсором."
    public static let diagnosticTranslate = "Перевести"
    public static let diagnosticEmpty = "Введите текст для перевода"
    public static let diagnosticTooLong = "Текст длиннее 1200 символов"
    public static let diagnosticClipboardEmpty = "В буфере обмена нет текста"
    public static let diagnosticClipboardDenied = "Доступ к буферу обмена запрещён в Системных настройках"
    public static let diagnosticHoverDisabled = "Перевод текста выключен — включите его в меню"
    public static let diagnosticServiceMessages = "Сообщения службы перевода (диагностика)"
    public static let diagnosticNoMessages = "Сообщений пока нет"
    public static let diagnosticResult = "Результат"
    public static let diagnosticShown = "Перевод показан в оверлее"
    public static let diagnosticNothingToShow = "Переводить нечего: в тексте нет слова под курсором"
    public static let diagnosticFailed = "Перевод не выполнен — причина в сообщениях службы перевода ниже"
    public static func diagnosticError(_ message: String, code: String) -> String { "\(message) (\(code))" }
    public static func diagnosticCard(source: String, translation: String, badge: String) -> String {
        "\(source) → \(translation) [\(badge)]"
    }

    // Settings window. The translation section's values belong to CoreHost.
    public static let settingsTitle = "Настройки Asteria Scale"
    public static let settingsTranslation = "Перевод"
    public static let settingsDelay = "Задержка перед переводом"
    public static let settingsHotkeys = "Горячие клавиши"
    public static let settingsService = "Служба перевода"
    public static let settingsFolders = "Папки"
    public static let settingsSense = "Модель значений"
    public static let folderUserData = "Данные пользователя"
    public static let folderModels = "Модели значений"
    public static let folderLogs = "Журналы"
    public static func folderLine(_ title: String, _ path: String) -> String { "\(title): \(path)" }
    public static func milliseconds(_ value: Int) -> String { "\(value) мс" }

    /// The badge of a primary card. Swift renders it from the typed
    /// provenance and never parses CoreHost's engine label.
    public static func provenance(_ provenance: Provenance, found: Bool) -> String {
        switch provenance {
        case .dictionary: return found ? "СЛОВАРЬ" : "НЕ НАЙДЕНО"
        case .heuristic: return "ЭВРИСТИКА"
        case .tatoeba: return "ПРИМЕРЫ TATOEBA"
        case .resultCache: return "КЭШ"
        case .ranker: return "РАНЖИРОВЩИК"
        case .rankerAbstained: return "РАНЖИРОВЩИК НЕ УВЕРЕН"
        case .rankerUnavailable: return "РАНЖИРОВЩИК НЕДОСТУПЕН"
        case .unknown(let wire): return "ИСТОЧНИК: \(wire)"
        }
    }

    static func format(_ duration: Duration) -> String {
        let parts = duration.components
        let seconds = Double(parts.seconds) + Double(parts.attoseconds) / 1e18
        return seconds < 1 ? "\(Int((seconds * 1000).rounded())) мс" : "\(Int(seconds.rounded())) с"
    }

    /// For the rule-9 and M15 checks: every fixed string the app can show,
    /// and a sample of each parameterised one.
    public static var allFixedStrings: [String] {
        let fixed = [
            menuHoverTranslation, menuTranslationMode, menuModeSmart, menuModeDictionary, menuCheckTranslation,
            menuTranslateClipboard, menuSettings, menuRestartTranslation, menuQuit, alreadyRunning,
            senseNotInUse, senseUnknown, overlaySelfTest, hotkeyNotAssigned,
            diagnosticNoMessages, diagnosticResult, diagnosticShown, diagnosticNothingToShow, diagnosticFailed,
            settingsSense, folderUserData, folderModels, folderLogs,
            stateStopped, stateStarting, stateReady, stateRestarting, stateUnavailable,
            reasonStopped, reasonExitedUnexpectedly, reasonProtocolBroken, reasonHelloRejected, reasonNoAnswer,
            reasonHelperMissing, reasonSpawnFailed, reasonCrashLoop,
            overlayLookingUpDictionary, overlayCheckingContext, overlayNotFound, overlayUnavailable, overlayNotRunning,
            overlayFailed, hotkeyToggleHover, hotkeyShowDiagnostic, hotkeyTranslateClipboard,
            diagnosticTitle, diagnosticPrompt, diagnosticTranslate, diagnosticEmpty, diagnosticTooLong,
            diagnosticClipboardEmpty, diagnosticClipboardDenied, diagnosticHoverDisabled, diagnosticServiceMessages,
            settingsTitle, settingsTranslation, settingsDelay, settingsHotkeys, settingsService, settingsFolders,
            recognitionFrameMismatch, recognitionPreparing, recognitionReady, recognitionNeedsPermission,
            recognitionRestartAfterGrant, recognitionDisabledCheckRun, menuAllowScreenRecording,
            captureNoPermission, captureSelfMissing, captureDisplayMissing,
        ]
        let samples = [
            restartDetail(attempt: 2, delay: .milliseconds(500), reason: reasonNoAnswer),
            reasonInitFailed("Не найдены словари перевода.", code: "dictionary_missing"),
            hotkeyTaken("⌥V"), hotkeyFailed("⌥V", status: -9878), milliseconds(700),
            startFoldersFailed(CocoaError(.fileWriteNoPermission)),
            startLockFailed(InstanceLock.Failure(call: "open", code: EACCES)),
            senseRunning(senseModelName("consec", provider: "cpu")),
            senseIdle(senseModelName("glossbert", provider: "cpu")), hotkeyLine(hotkeyToggleHover, "⌥T"),
            diagnosticError(errorText("not_ready"), code: "not_ready"),
            diagnosticCard(source: "bank", translation: "берег", badge: provenance(.dictionary, found: true)),
            folderLine(folderLogs, "/x"),
            recognitionFailed("recognizer_failed"),
            recognitionUnavailable(recognitionWarmUpFailed("recognizer_failed")),
            captureFailed("capture_failed"),
        ]
        let errors = ["connection_lost", "timeout", "not_ready", "not_running", "cancelled", "superseded", "bad_request",
                      "request_too_large", "conflict", "bad_response", "observations_too_large", "stale_frame",
                      "x"].map(errorText)
        let badges = Provenance.allKnown.flatMap { [provenance($0, found: true), provenance($0, found: false)] }
        return fixed + samples + errors + badges
    }
}

/// Typed provenance of a primary card, one to one with Core's
/// HoverResultSource as CoreHost puts it on the wire (docs/MACOS.md T3).
public enum Provenance: Sendable, Hashable {
    case dictionary, heuristic, tatoeba, resultCache, ranker, rankerAbstained, rankerUnavailable
    case unknown(String)

    public init(wire: String) {
        switch wire {
        case "dictionary": self = .dictionary
        case "heuristic": self = .heuristic
        case "tatoeba": self = .tatoeba
        case "resultCache": self = .resultCache
        case "ranker": self = .ranker
        case "rankerAbstained": self = .rankerAbstained
        case "rankerUnavailable": self = .rankerUnavailable
        default: self = .unknown(wire)
        }
    }

    public var wire: String {
        switch self {
        case .dictionary: return "dictionary"
        case .heuristic: return "heuristic"
        case .tatoeba: return "tatoeba"
        case .resultCache: return "resultCache"
        case .ranker: return "ranker"
        case .rankerAbstained: return "rankerAbstained"
        case .rankerUnavailable: return "rankerUnavailable"
        case .unknown(let wire): return wire
        }
    }

    /// The three sources that come with a senseProvider.
    public var isRanker: Bool { self == .ranker || self == .rankerAbstained || self == .rankerUnavailable }

    public static let allKnown: [Provenance] = [
        .dictionary, .heuristic, .tatoeba, .resultCache, .ranker, .rankerAbstained, .rankerUnavailable,
    ]
}
