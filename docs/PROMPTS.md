# Промты для ИИ-агентов

Готовые шаблоны для типовых задач. Подставьте свои детали вместо `<...>`.
Каждый промт уже содержит нужные ограничения — копируйте целиком, а не только
первую строку. Общие правила для любого агента: [AGENTS.md](../AGENTS.md).

## Универсальная преамбула

Если агент не читает `AGENTS.md`/`CLAUDE.md` автоматически, добавляйте в начало
любого промта:

> Прочитай AGENTS.md и docs/ARCHITECTURE.md перед изменениями. После правок
> собери `dotnet build AsteriaScale.slnx -c Release` и прогони smoke-набор
> `dotnet run --project tests/AsteriaScale.Tests/AsteriaScale.Tests.csproj -c
> Release --no-build`; после правки Python-скрипта прогони его набор из таблицы
> в AGENTS.md. Не запускай `--live`-режимы, `scripts/install-*.cmd` и флаги
> `-Accept*Terms`. Поведенческий фикс — только вместе с тестом, который падает
> до фикса.

На macOS-клоне (Apple Silicon) вместо неё:

> Прочитай AGENTS.md и docs/ARCHITECTURE.md перед изменениями. На macOS
> собирай только macOS-проекты: `dotnet build
> tests/AsteriaScale.Tests.MacOS/AsteriaScale.Tests.MacOS.csproj -c Release`
> (собирает и `src/AsteriaScale.RankerWorker.MacOS`, и
> `src/AsteriaScale.CoreHost`; сборка CoreHost — без единого предупреждения)
> и прогони macOS-подмножество
> во временном домашнем каталоге: `bash tools/macos/run-macos-subset.sh` (код `9`
> — прогон оставил лишнее в своих временных каталогах, `10` — появился новый
> отчёт о сбое; прямой запуск или `dotnet run` идут с настоящим домашним
> каталогом без этих проверок). Пользовательские пути ядра в подмножестве —
> явная ошибка (`MacSmoke.Begin` настраивает `CoreDataLayout`): тест, которому
> нужны настройки, кэш или словарь пользователя, передаёт свой каталог во
> временном корне.
> Приведи его итоговую строку `PASS (macOS subset): 46/57 shared smoke tests +
> 7 macOS checks; …` (с `CoreHost smart hover: checked in place` или
> `absent: <путь>`) и не выдавай её за полный smoke. CoreHost запускай только
> так, как это делают проверки подмножества: во временном домашнем каталоге
> (и `HOME`, и `CFFIXED_USER_HOME`: LocalApplicationData в .NET на macOS
> следует второй, а без неё — настоящему домашнему каталогу), с
> собственным `TMPDIR` и `DOTNET_EnableDiagnostics=0`; проверки не роняют
> процессы через abort или сигнал ошибки (отчёт о сбое попадёт в настоящую
> `~/Library/Logs/DiagnosticReports`), только `_exit` или SIGKILL, и не
> запускают копии системных программ вне их места (смерть копии `/bin/sleep`
> оставляет отчёт «SIGKILL (Code Signature Invalid)»; тесту Swift-пакета служит
> копия `AsteriaTestSleeper`). Новый отчёт процесса проверки (по имени,
> родителю или убийце, `tools/macos/crash-reports.py`; после его правки —
> `python3 tools/macos/test-crash-reports.py`) роняет `run-macos-subset.sh`,
> `test-swift.sh` и `build-app.sh` кодом `10`; такие отчёты не удаляй, а
> перечисли владельцу. Аргументы
> CoreHost меняются вместе с `tests/AsteriaScale.Tests.MacOS/CoreHost/launch-args-v1.txt`,
> байты протокола — вместе с `protocol-v1.golden.jsonl`. `settings.json` на
> macOS пишет только CoreHost (на первом запуске — ConSeC на CPU, значения по
> умолчанию `AppSettings` не трогай; изменение настроек встаёт в очередь при
> приёме запроса, а не в его работе в пуле, `settings.update` и `speaker.set`
> меняют ядро только после успешной записи `settings.json`, а `speaker.set`
> берёт `observedName` карточки как есть; `ocr.plan` тоже ждёт этой очереди,
> `ocr.select` работает без `core.init`, а наблюдения Apple Vision
> выбираются геометрией ядра — `OcrGeometry` и `OcrTextRules`, кэш кадра и
> копия фильтров `ExtractRecognition` живут в CoreHost, `Core.dll` не
> меняется), ошибки модели значений показываются
> русским текстом с кодом и без путей, а в тексте интерфейса и сообщениях
> CoreHost нет слова «ядро» (M15) — команды и правила в docs/DEVELOPMENT.md,
> раздел «CoreHost». Swift-пакет `macos/AsteriaScale` собирай и тестируй только
> `bash tools/macos/test-swift.sh` (он сначала собирает CoreHost, держит
> состояние SwiftPM в `.build/swiftpm-state` и запускает `swift` во временном
> домашнем каталоге: простой `swift build` или `swift test` пишет в настоящую
> `~/Library`) и приводи итог строкой «Swift suite: N tests»; сборка — без
> единого предупреждения, тесты — только Swift Testing (без `#Preview` и
> XCTest). Строки интерфейса приложения — русские, в `RU` (`Strings.swift`),
> со словами «перевод»/«служба перевода» и без слова «ядро» (M15); горячие
> клавиши по умолчанию — ⌥T, ⌥D, ⌥V (M14) и ⌃⌥G — редактор рода говорящего
> (M20; новое действие — только в конец `HotkeyAction`), и если система откажет в
> регистрации, приложение показывает русскую ошибку, а другие сочетания не
> подбираются молча. Приложение собирай и проверяй только `bash
> tools/macos/build-app.sh --self-test --ranker-models
> src/AsteriaScale.RankerWorker/Models` (бандл ConSeC читается на месте и
> никогда не копируется; ни одного файла модели в `.app`) и приводи итог
> строкой «app self-test: SELFTEST PASS n/n» вместе с «valid on disk» и
> «satisfies its Designated Requirement»; если из этого бандла уже запущена
> копия (владельца), скрипт выходит с кодом `2`, ничего не тронув, — попроси
> владельца выйти из неё, а не убивай её сам. `--output` по умолчанию
> (`artifacts/macos` в клоне) работает, только если клон лежит вне всех
> доменов File Provider («Рабочий стол» и «Документы» iCloud Drive,
> `~/Library/Mobile Documents`, папки в `~/Library/CloudStorage`; имени
> `.nosync` мало: File Provider всё равно ставит на новую папку `.app`
> `com.apple.FinderInfo`, и `codesign` её отвергает). Иначе скрипт до сборки
> выходит с кодом `2`, ничего не создав, и называет папку и атрибут; так же
> он отказывает `--output` с `.` или `..` в ещё не существующей части или с
> файлом либо символической ссылкой не на папку на пути, и ни один выход с
> кодом `2` не оставляет каталог вывода. О метке
> `com.apple.fileprovider.*` (например, `dir#N` в перенесённом из домена
> клоне), выше которой корень домена не найден, он только предупреждает: вне
> домена она безвредна. Клон не переноси и метки не снимай сам, даже
> командой из предупреждения (это дело владельца,
> docs/DEVELOPMENT.md, «Клон и iCloud Drive»), а о домене сообщи владельцу
> или передай `--output` вне всех доменов и перечисли этот путь вместе с
> остальными путями бандлов. Каждый прямой запуск оставляет
> путь бандла в базе LaunchServices владельца и после удаления бандла: базу
> не чисти (`lsregister -u` / `-gc` — дело владельца), а перечисли ему пути,
> которые запускал. Ручные проверки — только `bash
> tools/macos/build-app.sh --run-isolated` во временном домашнем каталоге;
> запуск приложения в настоящем домашнем каталоге (`open`,
> `~/Applications`), копия бандла ConSeC командой `ditto` и реальное нажатие
> горячих клавиш (⌥T, ⌥D, ⌥V, ⌃⌥G) — только владелец. Ни один твой запуск не вызывает
> ScreenCaptureKit, `CGPreflightScreenCaptureAccess`,
> `CGRequestScreenCaptureAccess` и ничего, что может вызвать запрос TCC
> («Запись экрана», «Универсальный доступ», «Мониторинг ввода»), и ни один
> тест, проба или скрипт не создаёт `ScreenSource` и не вызывает его захват,
> проверку или запрос разрешения и чтение игры (список окон, аргументы и
> окружение чужого процесса): этот код только собирается и сканируется;
> `GameIdentity` проверяй только на синтетических байтах и папках во
> временной папке теста, а редактор рода в самопроверке — с игрой из
> подменного определителя, не делая его ключевым окном, не активируя
> приложение и не меняя активное (клавиши — через обработчик самой панели,
> не через системные события); выполняется лишь статический `ScreenSource.ownWindowRecord` (одно
> чтение записи своей панели из списка окон, без ScreenCaptureKit и без
> разрешения) — в проверке `overlay.windowServer` самопроверки. Настоящий захват, слежение за курсором и первое
> разрешение — проверки владельца; захват включается, только когда
> владелец запускает приложение через LaunchServices (`CaptureGate`,
> docs/MACOS.md T22), а в самопроверке, `--run-isolated` и любом прямом
> запуске он выключен: `ScreenSource` не создаётся, курсор не
> отслеживается, приложение не прогревает Vision, а меню говорит
> «Распознавание отключено: проверочный запуск». Настоящий Vision — только в самопроверке приложения
> и в пробе DoD 6 (копия `AsteriaScaleVisionProbe`, переподписанная
> `codesign -s - -o runtime -f` без entitlements, прямым запуском со своими
> `HOME`/`CFFIXED_USER_HOME`, `TMPDIR` и пустым текущим каталогом в каталоге
> из `mktemp -d "${TMPDIR:-/tmp}/asteria-probe.XXXXXX"` вне клона (голый
> `mktemp -d` на macOS `TMPDIR` не учитывает) и со списком отчётов о сбоях
> до и после;
> docs/DEVELOPMENT.md, «Проба Vision»), обе — только во временном домашнем
> каталоге (T26). Vision при этом обновляет и записи вне временного
> домашнего каталога, в `getconf DARWIN_USER_CACHE_DIR`
> (`com.apple.metal/…`, у приложения —
> `io.github.slava8519.AsteriaScale/com.apple.metal/…`): перечисли их в
> отчёте и не удаляй. Swift-набор Vision не вызывает и текст не рисует, а
> имена захвата экрана и разрешений TCC (DoD 8, любое `CGWindowList*` и
> `CGImage(windowListFrom…)`) держит только в
> `Sources/AsteriaScale/ScreenSource.swift` (из `CGWindowList…` — две
> строки `CGWindowListCopyWindowInfo`: статического помощника проверки
> `overlay.windowServer` и чтения игры экземпляром; из ключей списка окон —
> только pid владельца, слой, границы, `SharingState` и `IsOnscreen`, имени
> окна — нигде; `KERN_PROCARGS…` в коде — только там же), а создание `ScreenSource` в любом известном
> проверке написании (`ScreenSource(`, `ScreenSource.init`,
> `.init(permit:`) — только в одной строке `AppDelegate.swift`, той, что
> разворачивает разрешение решения `CaptureGate` (кроме объявления
> инициализатора в `ScreenSource.swift`), а само решение — один раз там же,
> прямо перед ней, и только из признаков своего процесса: проверка читает
> код со схлопнутыми пробелами, и нарушение — любое другое упоминание
> `decide`; любое упоминание `Permit`, кроме объявления структуры, `case
> on(Permit)` и `Permit()` в `decide` и инициализатора `ScreenSource`;
> `getppid` не как вызов или его объявление через `func`, а `CommandLine`,
> `ProcessInfo` и `Bundle` не перед точкой (объявление, параметр, образец,
> параметр обобщения с этим именем); `setenv`, `putenv`, `unsetenv`,
> `environ`, `_NSGetEnviron` и `@_cdecl`, `@_silgen_name`,
> `@_dynamicReplacement`, `@_extern`; любое другое объявление
> `captureDecision` из тех, что читает проверка (`let`/`var`, `имя:`,
> `имя in`; оно одно — хранимое, без аксессоров, наблюдателей, начального
> значения и атрибута) и
> любая другая его запись из считаемых (`=`, кроме `==`, и после `?` или
> `!`, кортеж-образец перед `=`, `&`, путь ключа); код, обходящий сам язык
> (небезопасное приведение, запись по указателю), и код вне
> Swift-исходников пакета проверка не видит;
> `ScreenSource.swift` — только класс, чьи захват, разрешение и чтение
> игры доступны лишь через экземпляр (статический член — один
> `ownWindowRecord`, чей текст закреплён целиком, а заголовок расширения
> с именем класса или `typealias`, который им является, — нарушение), а `HoverDriver` заканчивает наведение одним
> `HoverReport.afterScreenHover` со свежей сверкой курсора, а скрытие
> слежением — только через `overlay.trackerHide()`, запись чужого процесса читается один раз и только
> для Wine, а `AppDelegate` решает о слежении по состоянию редактора рода и решает заново при его
> открытии и закрытии (`SourceScanTests`); оркестрацию наведения (`HoverPipeline`,
> `HoverTracker`, `OverlayState`, `HoverReport`, `CaptureGate`) держи в Kit чистой и покрытой
> Swift-набором с подменами и поддельным CoreHost, а приложение — только
> связкой (так же — игру и модель редактора рода: `GameIdentity`,
> `ProcessArguments`, `SpeakerEditor`); тест Swift обращается по индексу только после
> проверки размера через `#require` (ловушка Swift, как и abort, оставляет
> отчёт о сбое). Подписывай только ad-hoc: сертификат
> Apple Development владельца (M16) — для его сборок, и если задан
> `ASTERIA_SIGN_IDENTITY`, передавай `--sign -`; `build-app.sh --install DIR`
> (T34) — только с явным `DIR` внутри каталога из `mktemp -d` и полной
> сборкой в свой `--output` (с `--no-build` копия несёт подпись бандла из
> `--output`), не вместе с `--run-isolated`; без `DIR` он ставит в
> `~/Applications`, а это только владелец (с подписью `-` скрипт туда не
> ставит, код `2`). Установленную копию не запускай. Он отказывает кодом `2`
> до сборки, если `DIR` в домене File Provider, совпадает с выводом или лежит
> в нём, если `DIR/Asteria Scale.app` — чужой бандл, ссылка, файл или запись
> под другим написанием или из него запущен процесс (ищется по пути
> исполняемого файла от macOS, как бы его ни запустили), и кодом `7` (ничего
> не заменив), если процесс запустился из цели за время прогона или цель
> появилась, пропала или сменилась; поддельная «запущенная копия» в такой
> проверке — только копия `AsteriaTestSleeper`. `build-app.sh` и
> `test-swift.sh` в конце печатают записи `getconf DARWIN_USER_CACHE_DIR` и
> `~/Library/Caches/com.apple.e5rt.e5bundlecache` новее начала прогона, кто
> бы их ни записал (T35, только отчёт): новые записи перечисли в отчёте и не
> удаляй. `AsteriaScale.slnx`, Windows-воркер и
> Windows тест-проект в рабочем дереве не собирай: без `EnableWindowsTargeting`
> они останавливаются на NETSDK1100, а с ним Windows-воркер (в том числе в
> составе `AsteriaScale.slnx`) копирует бандл ConSeC в `bin/`. Код для macOS
> в общих исходниках по умолчанию ставь под `#if !WINDOWS` / `#else` и докажи,
> что выходы Windows-сборки не изменились:
> `bash tools/macos/check-windows-identity.sh <base> --worktree` до коммита
> (предварительно: он собирает и неотслеживаемые файлы) и
> `bash tools/macos/check-windows-identity.sh HEAD~1 HEAD` после коммита при
> пустом `git status --porcelain` должны напечатать `IDENTICAL` (единственное
> допустимое отличие — строка
> `./Tests/THIRD_PARTY_NOTICES.md` после правки этого файла). Если задача Трека
> III по решению M12 намеренно меняет Windows-код (ядро, RankerProtocol,
> воркер, WPF-хост, общие тесты), скрипт должен пройти все Windows-сборки и
> завершиться кодом `1` с `DIFFERENT` (код `4` — упавшая сборка — недопустим),
> `python3 tools/macos/audit-verbatim-move.py regex --base HEAD~1` должен
> напечатать `GeneratedRegex attributes: identical (…)`, если задача не меняет
> regex, macOS-подмножество должно остаться зелёным, а в том же коммите добавь строку
> в таблицу «Windows-изменения до прогона на Windows» в `docs/MACOS.md`. В
> сообщение такого коммита помести изменённые строки манифеста, строку
> `regex` и, если коммит переносит или правит общий код, оба раздела
> `python3 tools/macos/audit-verbatim-move.py move --base <rev> --from … --to …`
> (множественный `verbatim: K/N` и учитывающий порядок `base residual` /
> `destination-only`) с классом замены для каждого остаточного фрагмента. В
> общих тестах держи обобщённый `Task<T>` в его собственном типе: приведение к
> `Task` или `object` добавляет в diff строку `WinRTGlobalVtableLookup.g.cs`
> Windows-тестов. Полный
> Windows smoke прогоняет владелец на Windows (по M10 — в конце Трека III). Python вызывай как `python3`. Не запускай
> `--live`-режимы, `scripts/install-*.cmd` и флаги `-Accept*Terms`.
> Поведенческий фикс — только вместе с тестом, который падает до фикса.

## 1. Онбординг / аудит

> Сделай аудит подсистемы `<словарей / умного словаря / OCR / апскейла>`.
> Сначала прочитай docs/ARCHITECTURE.md и перечисленные там инварианты, затем
> сами сервисы в src/AsteriaScale.Core/Services (ядро перевода) и src/AsteriaScale/Services (Windows-часть). Ничего не меняй: составь список
> находок с путями и номерами строк, для каждой — конкретный сценарий сбоя и
> предлагаемое исправление. Отдельно пометь, какие находки затрагивают
> инварианты из ARCHITECTURE.md.

## 2. Багфикс в словарном поиске

> В словарном поиске баг: `<симптом: какое слово/фраза/предложение, какой режим,
> что показано и что ожидалось>`. Разберись в LocalDictionaryService
> (src/AsteriaScale.Core/Services/LocalDictionaryService.cs). Помни инвариант №1:
> ключи всех слоёв канонизируются при загрузке той же нормализацией, что и
> ключи поиска, а python-генератор scripts/build-dictionaries.py обязан давать
> ту же форму — если меняешь нормализацию, сначала добавь строку в общий
> контракт scripts/dictionary-normalization-contract.tsv (его читают и
> C#-smoke, и генератор), затем меняй обе стороны и прогони
> `python scripts/test-build-dictionaries.py`. Добавь
> регрессионный тест в tests/AsteriaScale.Tests/LocalDictionaryNormalizationTests.cs
> (изолированные временные каталоги через internal-конструктор, не трогай
> реальный %LOCALAPPDATA%). Прогони smoke-набор — тест «Local dictionary
> lookup» работает на реальных данных и должен остаться зелёным.

## 3. Эвристики умного словаря

> Улучши эвристику `<описание: например, выбор вида глагола при «kept -ing»>`.
> Затрагиваются SmartDictionaryHeuristicRanker и/или SmartDictionarySenseResolver.
> Правила: общая лингвистика (наборы слов, морфология) живёт в
> EnglishContextLexicon и RussianMorphology — расширяй их, а не локальные копии;
> POS-инференс в двух файлах сознательно раздельный, не объединяй его. Эвристика
> имеет право сработать только при высокой уверенности — при сомнении верни
> null/воздержание, решение примет нейронный ранжировщик смыслов. Добавь
> случаи в тестовые блоки
> «Smart dictionary sense grouping and surface selection» и «Smart dictionary
> deterministic heuristic ranker» в tests/AsteriaScale.Tests/Program.cs: и
> позитивный, и случай обязательного воздержания.

## 4. Ранжировщик смыслов / worker

> Задача по ранжировщику: `<описание>`. Код: src/AsteriaScale.RankerWorker
> (RankerEngine, PairTokenizer, Consec*), хост
> src/AsteriaScale.Core/Services/GlossBertDictionarySelectorService.cs и общий
> контракт src/AsteriaScale.RankerProtocol (RankerProviderTraits,
> RankerAbstention). Жёсткие правила: порог margin 0.25 и критерии принятия
> решения не меняются без прогона бенчмарка; тихий fallback NPU→CPU запрещён;
> всё, о чём хост и воркер договариваются про провайдер, читается из
> RankerProviderTraits — новый провайдер = запись в таблице, исчерпывающие
> switch на обеих сторонах и обновлённый RankerProviderTraitsTests; после
> записи запроса в stdin транзакция не отменяется — ровно один ответ
> вычитывается из stdout; хеши артефактов модели закреплены. Меряй ConSeC —
> рабочую конфигурацию владельца (default кода — GlossBERT, его не меняй); если
> нет src/AsteriaScale.RankerWorker/Models/Consec, остановись и спроси.
> Валидация: собери Release, сделай junction `<scratch>\rankerbase\Ranker` на
> bin-выход воркера
> (src/AsteriaScale.RankerWorker/bin/Release/net10.0-windows10.0.26100.0/win-x64)
> и прогони
> `dotnet run --project tests/AsteriaScale.Tests/AsteriaScale.Tests.csproj -c
> Release --no-build -- --benchmark-only --ranker-benchmark
> assets/Ranker/ranker-benchmark.json <scratch>\rankerbase cpu
> --ranker-cpu-model consec --ranker-report artifacts/Ranker/<имя>.json`.
> ConSeC работает только на CPU; прогон `npu` без `--ranker-cpu-model` нужен,
> только если задача касается GlossBERT на NPU и NPU доступен. Holdout-наборы
> не используй для подбора — только одно финальное подтверждение с записью в
> docs/RESEARCH.md. Приведи цифры до/после в отчёте и назови ранжировщик
> каждой руки.
>
> На macOS junction и бенчмарк выше — только для Windows. Валидация там —
> `--smoke` macOS-воркера с ConSeC
> (`src/AsteriaScale.RankerWorker.MacOS/bin/Release/net10.0/osx-arm64/AsteriaScale.RankerWorker
> --provider cpu --cpu-model consec --model-directory
> src/AsteriaScale.RankerWorker/Models --smoke`), JSONL-сессия с теми же
> аргументами без `--smoke` и macOS-подмножество. Запрос NPU на macOS всегда
> получает ошибку `npu_provider_unavailable`: `--smoke` выходит с кодом `1`, а
> JSONL-сессия отвечает этой ошибкой на `warm`/`select` и работает дальше до
> `shutdown`. Бенчмарков на macOS нет, пока
> provenance не различает платформу.

## 5. Новый словарный слой / регенерация словарей

> Добавь словарный слой `<источник, лицензия, направление>`. Изучи
> scripts/build-dictionaries.py и assets/Dictionaries/README.md. Требования:
> ключи — через normalize_english/normalize_japanese (паритет с C#-стороной);
> имя файла определяет роль слоя (`-vn.` кураторский VN-hard, `-vn-soft.`
> сливается с broad, `-scaffold.` только для каркаса полного перевода и в
> поиске не виден, `-dialogue.` предложения, `-senses.` sidecar, остальное —
> broad с объединением значений); после правки генератора прогони
> `python scripts/test-build-dictionaries.py`, а хеши в
> assets/Dictionaries/vn-build-manifest.json вручную не правь; лицензию
> добавь в THIRD_PARTY_NOTICES.md и README словарей; broad-слой не должен
> перекрывать кураторские записи. После генерации прогони smoke-набор: тест
> «Local dictionary lookup» проверяет реальные данные и счётчик источников.

## 6. Пресет апскейла

> Добавь/измени пресет апскейла: `<цепочка эффектов и целевой сценарий>`.
> Код: src/AsteriaScale.Core/Models/UpscalePreset.cs (PresetCatalog) и MagpieConfigEditor. Эффекты
> должны существовать в установленном Magpie — тест «Installed effects»
> валидирует каждый пресет против каталога эффектов; правка конфига обязана
> остаться идемпотентной (тест «Magpie config edit»). Обнови таблицу пресетов в
> README.md.

## 7. UI / оверлеи

> Задача по UI: `<описание>`. Файлы: MainWindow.xaml(.cs),
> HoverTranslationOverlay, TrayIconService; вид карточки по строке движка
> (словарь, кэш, ускоритель, «НЕ УВЕРЕН», подпись строки фразы) определяет
> `HoverEngineLabelClassifier` в ядре. Сами строки движка и имя источника
> текста в статусах задаёт хост через `HoverEngineLabels` (Windows — прежние
> строки байт в байт), а новый хост рисует карточку по `HoverProvenance`
> результата, не разбирая строку. Правила:
> оверлеи не перехватывают клики (hit-test прозрачность), корректность при
> Per-Monitor V2 DPI обязательна (эталон — 3200×2000 @ 200%), все
> пользовательские строки по-русски. UI-поток не блокировать: тяжёлая работа —
> в сервисах, в UI — через Dispatcher.

## 8. Релиз

> Подготовь релиз `<версия>`: прогони полный smoke-набор, обнови «Ограничения
> версии» и версию в README.md, проверь THIRD_PARTY_NOTICES.md, собери
> `./scripts/build.cmd` (приложение из трея закрыть заранее — dist заблокирован),
> убедись, что артефакты модели НЕ попадают в git (лимит GitHub 100 МБ; они
> распространяются отдельно — см. docs/DEVELOPMENT.md), и составь changelog по
> истории изменений.

## 9. Задача из роадмапа

> Реализуй задачу №`<номер>` из docs/ROADMAP.md («`<название>`»). Карточка
> задачи — твоё ТЗ: следуй разделу «Архитектура» и не выходи за скоуп; разделы
> «Инварианты» и «Definition of Done» обязательны к выполнению дословно. Если
> у задачи есть зависимости из таблицы — сначала проверь, что они выполнены, и
> остановись с вопросом, если нет. По завершении обнови саму карточку
> (пометь выполненное) и, если требуется карточкой, RESEARCH.md.

## 10. Дообучение ранжировщика на машине с дискретной GPU (архив)

**Архив, не для новых задач.** Эксперимент собственного DeBERTa fine-tune на
RTX 5080 закрыт 29 июля 2026, а 8 ноября 2026 его инструменты
(`tools/build-wsd-training-set.py`, `tools/train-wsd-ranker.py`, launcher,
переносимый пакет и `tools/requirements-wsd-*.txt`) удалены из репозитория
вместе с профилями SANDWiCH и GlossDeBERTa. Промт ссылался на эти скрипты и
больше неприменим. Исторический текст промта — в git:
`git show 911510e:docs/PROMPTS.md` (раздел 10); итог эксперимента и причины
удаления — в [RESEARCH.md](RESEARCH.md).

## Чего агент делать не должен (вставляйте при сомнении)

> Запрещено: менять порог 0.25 и семантику воздержания без бенчмарка; открывать
> holdout-наборы при подборе; включать тихий CPU/GPU-fallback для NPU; менять
> приоритет словарных слоёв; редактировать dist/ руками; коммитить файлы
> >100 МБ; ослаблять валидацию протокола воркера; удалять резервную копию
> конфига Magpie; запускать `scripts/install-*.cmd`, флаги `-Accept*Terms` и
> скачивание датасетов; ослаблять проверки `build.cmd` на артефакты модели и
> сертификат; править хеши в `vn-build-manifest.json` вручную; собирать на
> macOS `AsteriaScale.slnx`, Windows-воркер или Windows тест-проект в рабочем
> дереве (с `EnableWindowsTargeting` Windows-воркер, в том числе в составе
> `AsteriaScale.slnx`, копирует бандл ConSeC в `bin/`);
> выдавать macOS-подмножество за полный smoke-набор; запускать
> macOS-приложение в настоящем домашнем каталоге или класть файл модели в
> `.app`; вызывать ScreenCaptureKit или запрос TCC и подписывать
> сертификатом владельца; менять видимые Windows
> токены общих исходников вне `#if !WINDOWS` / `#else` без доказательства
> `tools/macos/check-windows-identity.sh` (`IDENTICAL`) — кроме намеренных
> Windows-правок Трека III по M12, которые обязаны кросс-собираться (код `1`,
> не `4`) и попадать в таблицу «Windows-изменения до прогона на Windows» в
> `docs/MACOS.md`.
