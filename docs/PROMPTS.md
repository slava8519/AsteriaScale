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
> (собирает и `src/AsteriaScale.RankerWorker.MacOS`) и прогони macOS-подмножество
> во временном домашнем каталоге: `bash tools/macos/run-macos-subset.sh --allow
> 'home/Library/Application Support/AsteriaScale/hover-translation.log'` (код `9`
> — прогон оставил лишнее в своих временных каталогах, `10` — появился новый
> отчёт о сбое; прямой запуск или `dotnet run` пишут в настоящую `~/Library`).
> Приведи его итоговую строку `PASS (macOS subset): …`
> и не выдавай её за полный smoke. `AsteriaScale.slnx`, Windows-воркер и
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
> `destination-only`) с классом замены для каждого остаточного фрагмента. Полный
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
> HoverTranslationOverlay, TrayIconService. Правила:
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
> выдавать macOS-подмножество за полный smoke-набор; менять видимые Windows
> токены общих исходников вне `#if !WINDOWS` / `#else` без доказательства
> `tools/macos/check-windows-identity.sh` (`IDENTICAL`) — кроме намеренных
> Windows-правок Трека III по M12, которые обязаны кросс-собираться (код `1`,
> не `4`) и попадать в таблицу «Windows-изменения до прогона на Windows» в
> `docs/MACOS.md`.
