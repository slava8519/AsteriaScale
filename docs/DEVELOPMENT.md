# Разработка и сборка

## Требования

| Компонент | Версия | Обязательно |
|---|---|---|
| Windows 11 | build 26100+ (для NPU OCR) | да |
| .NET SDK | 10.0 | да |
| [Magpie](https://github.com/Blinue/Magpie) | 0.12.1 | да (движок апскейла, ставится отдельно) |
| Windows AI Text Recognition | из Windows App SDK | для OCR-перевода |
| TranslateGemma 4B Q4_K_M + llama.cpp CPU/Vulkan | b9632 | опционально, независимый перевод фразы второй строкой |
| AMD NPU + драйвер | 32.0.203.329+ | опционально (NPU-путь GlossBERT) |
| SANDWiCH dual encoder | NAACL 2025, upstream `daca3cad…` | опционально, экспериментальный CPU semantic ranker |
| ConSeC/WNGT | upstream `9602b5fd…`, CC BY-NC-SA 4.0 | опционально, личный научный/некоммерческий CPU ranker |
| Python | 3.11+ (`python` в `PATH`) | для полного smoke-набора и регенерации артефактов |

## Артефакты модели GlossBERT

Файлы в `src/AsteriaScale.RankerWorker/Models/GlossBert` **не хранятся в git** (626 МБ, отдельные файлы превышают лимит GitHub 100 МБ). Перед сборкой поместите их туда вручную — из Git LFS, GitHub Release или экспортировав [kanishka/GlossBERT](https://huggingface.co/kanishka/GlossBERT) ревизии `0cc3b83af5496e27ebcc95ef0cf37ea0a9281a7a` в ONNX. Ожидаемые размеры и SHA-256 закреплены в [assets/Ranker/README.md](../assets/Ranker/README.md) и проверяются воркером при запуске: с несовпадающими файлами ранжировщик откажется работать.

Раскладка:

```text
Models/GlossBert/tokenizer.json
Models/GlossBert/Cpu/glossbert-fp32-dynamic.onnx
Models/GlossBert/Npu/glossbert-eager-vitis-context.onnx
Models/GlossBert/Npu/glossbert-eager-vitis-context.onnx_VITISAI.bin
Models/GlossBert/Npu/glossbert-eager-vitis-context.onnx.compatibility.txt
```

CPU bi-encoder bundle `Models/GlossEmbeddings` также обязателен. В отличие от
скомпилированного GlossBERT/NPU-набора, он полностью восстанавливается одной
командой из [assets/Ranker/README.md](../assets/Ranker/README.md): скрипт создаёт
индекс и сам помещает проверенные ONNX/tokenizer в требуемый каталог.

## Экспериментальный SANDWiCH CPU

Готовый [SANDWiCH](https://github.com/danielguzmanolivares/sandwich) не заменяет
GlossBERT по умолчанию. Он устанавливается отдельно: два официальных энкодера
скачиваются из upstream-архива, проверяются по размеру/SHA-256, экспортируются в
FP32 ONNX и ещё раз проверяются вместе с tokenizer и manifest. Веса, tokenizer
и ONNX больше 100 МБ не входят в git.

```powershell
.\scripts\install-sandwich.cmd
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\verify-sandwich.ps1
```

Неинтерактивный личный/CI-запуск после отдельной проверки условий upstream:

```powershell
.\scripts\install-sandwich.cmd -AcceptUpstreamTerms
```

Установщик фиксирует принятие в локальном ignored-файле
`Models/Sandwich/UPSTREAM_TERMS_ACCEPTED.json`, а архив кэширует в
`%LOCALAPPDATA%\AsteriaScale\Downloads`. Он устанавливает только MIT-код и два
официальных энкодера; BabelNet-derived `data/definitions` и `data/neighbours`
не скачиваются и не входят в Asteria. Полная таблица исходных и экспортированных
хешей приведена в [assets/Ranker/README.md](../assets/Ranker/README.md).

После пересборки выберите в настройках умного словаря
`SANDWiCH — исследовательский CPU`. Профиль CPU-only: если ранее был выбран NPU,
UI явно переключит ускоритель на CPU. Скрытого fallback нет. Проверка через
production benchmark:

```powershell
dotnet run --project tests/AsteriaScale.Tests/AsteriaScale.Tests.csproj `
  -c Release --no-build -- --benchmark-only `
  --ranker-benchmark assets/Ranker/ranker-benchmark.json dist/AsteriaScale cpu `
  --ranker-cpu-model sandwich `
  --dump artifacts/Ranker/sandwich-dev-dump.json `
  --ranker-report artifacts/Ranker/sandwich-dev-report.json
```

Открытый dev-набор содержит `134` случая visual novel, манги, литературы и
научных статей (`20` scientific cases). Каждый отчёт закрепляет точный SHA
набора; frozen `ranker-holdout*.json` при A/B не открываются. Финальный same-build
A/B и причина, по которой GlossBERT остаётся default, записаны в
[RESEARCH.md](RESEARCH.md).

Для sentence-level ConSeC и downstream-проверки TranslateGemma используется
отдельный открытый `assets/Ranker/ranker-sentence-benchmark.json`: `46` случаев,
из них `36` научных (ровно по шесть biology, medicine, physics, chemistry,
statistics и computer science), `4` контекстных и `6` нарративных; `138` точных
target spans / `276` sense groups и `19` случаев со скрытым `previousContext`.
Две пары имеют байт-в-байт одинаковый current sentence, но разные историю и
gold-смыслы. Gold-позиции сбалансированы `72/66`; `100` целей образуют
surface-unambiguous WSD-срез, а `38` имеют естественно пересекающиеся русские
поверхности. В обоих случаях считается выбранный sense GroupId: русский
reference сохранён для будущего full-text A/B, но текущий runner его не
оценивает. SHA-256 набора:
`bed4c0211b1c8e79654c630d4dcb95625e32b07d099da872f1dd4e3154ec6bca`.
Это dev/research-набор, не holdout; его schema и инвариант `GroupId == index`
проверяет полный smoke-suite.

## Исследовательский ConSeC CPU

ConSeC/WNGT устанавливается отдельно и только после явного принятия
CC BY-NC-SA 4.0. Checkpoint и полученный `1,62`-ГБ external-data ONNX остаются
вне git и обычной публикации. Установщик проверяет точный четырёхфайловый
контракт, защищает пути/reparse points и публикует bundle транзакционно. Он не
скачивает checkpoint: `SourceBundle` сначала создаётся по
[`tools/README-consec.md`](../tools/README-consec.md):

```powershell
.\scripts\install-consec.cmd `
  -SourceBundle "$env:LOCALAPPDATA\AsteriaScale\Models\Consec-WNGT-ONNX"
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\verify-consec.ps1
```

Неинтерактивный запуск разрешён только после самостоятельного ознакомления с
upstream terms:

```powershell
.\scripts\install-consec.cmd -AcceptUpstreamTerms `
  -SourceBundle "$env:LOCALAPPDATA\AsteriaScale\Models\Consec-WNGT-ONNX"
```

После пересборки выберите `ConSeC — лучший для науки, CPU`. Профиль строго
CPU-only, сообщает `pipeline=consec-independent`, сохраняет полное предложение
и отказывается от входа длиннее 512 токенов вместо скрытого усечения или
fallback. Exact model/cache identity и размеры/SHA приведены в
[assets/Ranker/README.md](../assets/Ranker/README.md).

Безопасный export и официальный reference gate описаны в
[`tools/README-consec.md`](../tools/README-consec.md). Gate уже пройден против
точного upstream Transformers 4.3.3: input hashes и choices совпали, max delta
всех logits — `3,10e-6` на dialogue и `5,01e-6` на mixed-case scientific
fixture.
Встроенный независимый профиль доступен для исследования, но GlossBERT остаётся
stable-default до feedback/joint planner и full-text A/B.

## Закрытый эксперимент WSD/DeBERTa на RTX 5080

**Новые прогоны собственного fine-tune больше не планируются.** Ниже сохранена
воспроизводимая инструкция исторического эксперимента для аудита уже полученных
результатов; она не является следующим шагом разработки. Итог эксперимента и
причина перехода к готовым SANDWiCH/ConSeC зафиксированы в
[RESEARCH.md](RESEARCH.md).

На GPU-машину переносится не рабочая копия проекта, а минимальный проверяемый
пакет: trainer, exact requirements и канонический
`artifacts/wsd-train-shipped.jsonl`. На исходной машине выполните:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\package-wsd-ranker-training.ps1
```

Скрипт запускает `--self-check` и `--inspect-only`, проверяет точный FP32 trainer
hash, записывает размеры, SHA-256, 27 перечислимых profiles и четыре непересекающиеся suites в
`package-manifest.json`, а затем атомарно создаёт новый ZIP в `artifacts`.
Модели, checkpoints и frozen holdout в пакет не входят; существующий ZIP не
перезаписывается. В корне ZIP лежит автономная инструкция `README.md`; её
отслеживаемый источник — `tools/README-wsd-rtx5080.md`; полный порядок
экспериментов копируется из `tools/WSD-EXPERIMENT-ROADMAP.md`. Распакуйте пакет на
локальный фиксированный NTFS-диск RTX-
машины (не в OneDrive, junction или сетевой каталог) и сначала проверьте
целостность распакованного пакета:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\run-wsd-ranker-rtx5080.ps1 -VerifyOnly
```

Этот режим сверяет manifest, размеры и SHA-256 без Python, CUDA и обучения.
Затем создайте CPython 3.12.10 x64 venv и установите закреплённое окружение:

```powershell
py -3.12 -m venv .venv
$python = (Resolve-Path '.\.venv\Scripts\python.exe').Path
& $python -m pip install --no-deps --only-binary=:all: --require-hashes -r .\tools\requirements-wsd-bootstrap.txt
& $python -m pip install --no-deps --only-binary=:all: --require-hashes -r .\tools\requirements-wsd-torch-cu130.txt
& $python -m pip install --no-deps --only-binary=:all: --require-hashes -r .\tools\requirements-wsd-ranker.txt
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\run-wsd-ranker-rtx5080.ps1 -ListProfiles
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\run-wsd-ranker-rtx5080.ps1 -RunReferenceSuite
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\run-wsd-ranker-rtx5080.ps1 -RunNextSuite
```

Launcher ничего не устанавливает и не делает CPU fallback: он сверяет package
manifest, exact-версии Python-пакетов, CUDA 13.0, compute capability не ниже
`12.0`, native BF16, полный lock зависимостей, profile-specific
`PYTHONHASHSEED` и `CUBLAS_WORKSPACE_CONFIG=:4096:8`. Без явного выбора launcher
fail-closed. Все профили используют полный шестиепохный schedule (`3006` шагов):
эксперимент показал, что качество дают и более медленный спад LR, и дополнительные
шаги. `reference` воспроизводит base7 с новым per-group отчётом; `next` сравнивает
на одинаковых семи слоях и seed `cross-encoder/nli-deberta-v3-base` reset против
nli-projected; `capacity` и `interaction` независимо проверяют глубину и NLI × depth.
Confirmation seeds уже перечислены в пакете, но до CPU/dev gate запускаются только вручную.
Произвольных model/revision/dataset/epochs аргументов нет. На Windows для CUDA
13 нужен NVIDIA driver `580.88+`; отдельный CUDA Toolkit wheel-у PyTorch не нужен.
Прерванный запуск продолжается только явно, с тем же каталогом:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\run-wsd-ranker-rtx5080.ps1 -Profile nli7-reset-s1 -Resume -OutputDirectory '<существующий-output>'
```

Вернуть нужно целиком `<output>/bundle/GlossDeberta`, а также `metrics.jsonl`,
`run-state.json`, `experiment-profile.json`, `validation-predictions.jsonl`,
`validation-predictions-manifest.json`, `training-*.log` и `pip-freeze-*.txt`.
Групповой отчёт не содержит исходного текста и используется для exact McNemar и
paired lemma-cluster bootstrap. Только после проверки
bundle-manifest модель устанавливается в ignored runtime-каталог и проходит
same-build dev/register CPU-бенчмарки на исходной машине. До выбора победителя
production остаётся на GlossBERT; frozen holdout в этом цикле не открывается.

## Экспериментальная TranslateGemma 4B

TranslateGemma не входит в git, публикацию или обычный `install.cmd` и никогда
не скачивается приложением автоматически. Модель распространяется по
[Gemma Terms](https://ai.google.dev/gemma/terms) и
[Gemma Prohibited Use Policy](https://ai.google.dev/gemma/prohibited_use_policy),
а не по лицензии Asteria. Для интерактивной установки выполните:

```powershell
.\scripts\install-translategemma.cmd
```

Скрипт сначала показывает ссылки на условия и продолжает только после точного
ответа `ПРИНИМАЮ`. Для автоматизированного запуска допустим ключ ниже, но лишь
если текущий пользователь уже прочитал и явно принял эти условия:

```powershell
.\scripts\install-translategemma.cmd -AcceptGemmaTerms
```

Установщик загружает около `2,5 ГБ` в профиль текущего пользователя, проверяет
размер и SHA-256 каждого файла и создаёт manifest принятия в
`%LOCALAPPDATA%\AsteriaScale\Models\TranslateGemma`. Закреплены:

- `translategemma-4b-it.Q4_K_M.gguf` — `2 489 909 760` байт,
  SHA-256 `81200d03e843d2ec1ece6eeafe7d13cb6e5211e1fcd336ade55790b683a08330`,
  статический quant `mradermacher/translategemma-4b-it-GGUF` revision
  `35a7486e128b19642cdc72d7b91b21ba388aaf42`;
- `llama-b9632-bin-win-cpu-x64.zip` — CPU runner llama.cpp b9632,
  `16 899 258` байт,
  SHA-256 `b835d5c5155dd2a5ed748a0351debf2ede0dc9f808757e0429f8700a11832dcd`;
- `llama-b9632-bin-win-vulkan-x64.zip` — Vulkan runner llama.cpp b9632,
  `38 545 553` байт,
  SHA-256 `90b914ff48167370190477ff3f54fd2c4eb034a305b943913eaa310eda78793d`;
- локальная копия `GEMMA_TERMS.txt`.

Runtime также узнаёт точный blob с тем же SHA-256 в `OLLAMA_MODELS` (либо
`%USERPROFILE%\.ollama\models`), но не принимает другой quant или похожий тег.
`TranslateGemmaTranslationService` проверяет pins перед запуском, распаковывает
runner в принадлежащий приложению временный каталог и поднимает скрытый
`llama-server` только на `127.0.0.1` со случайным API-ключом и context `2048`.
Backend выбирается явно: CPU запускается с `--n-gpu-layers 0`, Vulkan — через
закреплённый Vulkan runner с `--device Vulkan0 --n-gpu-layers all --fit off`.
Автоматического CPU fallback нет: отсутствие выбранного runner, Vulkan0
device или подтверждения полного offload является ошибкой только независимой
второй строки и не затрагивает словарный primary. Встроенный в GGUF
мультимодальный Jinja-template llama.cpp b9632 не может проверить со своим
тестовым string-content и завершает сервер до первого запроса. Поэтому runtime
явно использует `--no-jinja` и детерминированный raw `/completion` с тем же
отрендеренным direct EN→RU prompt; вход по-прежнему содержит только фразу.

Runtime-каталоги называются строго `Runtime/b9632-<GUID>`. Выгрузка сначала
подтверждает завершение принадлежащего ей процесса, затем удаляет каталог с
коротким bounded retry для блокировок DLL. Неуспешный путь не забывается, а
каждый unload/warm-up дополнительно и без возрастной задержки очищает старые
каталоги точного формата. Это происходит уже после single-instance mutex;
активный каталог исключается, а переживший crash процесс защищён блокировкой
своих DLL. Посторонние каталоги и reparse points никогда не входят в sweep,
ownership повторно проверяется непосредственно перед каждой retry-попыткой.

После установки отдельную живую проверку модели можно выполнить без запуска UI:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\verify-translategemma.ps1 -Backend Cpu
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\verify-translategemma.ps1 -Backend Vulkan
```

Скрипт перепроверяет закреплённые артефакты, запускает скрытый loopback-server
во временном каталоге, переводит четыре диагностические фразы и гарантированно
останавливает принадлежащий ему процесс. Backend обязателен: один runner никогда
не подменяется другим. Vulkan-проверка до первого probe требует в verbose-журнале
Vulkan0 и `offloaded 35/35 layers to GPU`; успешного `/health` без этих
свидетельств недостаточно. Это live-проверка доступности и контракта runtime, а
не количественная оценка качества.

Контрольный прогон Vulkan на Radeon 860M с выделенными `4 ГБ` UMA сообщил
`2 663 MiB` projected device memory: `2 368 MiB` model, `214 MiB` context и
`81 MiB` compute; подтверждён полный offload `35/35`. После прогрева один direct
probe занял `1,266 с` при `31,57 token/s`. Это измерение конкретной машины и
закреплённого b9632/Q4_K_M, а не минимальная гарантия для другого GPU.

После установки включите в UI: `Умный словарь` →
`Перевод фразы второй строкой` →
`TranslateGemma 4B — перевод предложения второй строкой`, затем отдельно выберите
`CPU` или `Vulkan GPU`. Режим по умолчанию выключен, а backend по умолчанию и
при миграции старых настроек — CPU. Основной словарный результат или решение
выбранного ranker показывается сразу;
после этого TranslateGemma независимо обрабатывает любой английский smart-result,
включая miss, abstention, trusted/heuristic-путь и попадание в кэш. Источник —
полное текущее предложение длиной от 1 до 64 слов. Перед prompt узкий
`SmartPhraseIdiomRewriter` может детерминированно заменить доказанную идиому на
однозначную английскую парафразу; русский primary и semantic evidence он не
читает. Sentence-wide semantic scaffold передаёт выбранному ranker до 12
остальных неоднозначных целей исходного предложения; hovered target повторно не
ранжируется, а синтетическая idiom-парафраза не используется как WSD-контекст.
Первый direct prompt получает только effective English. Без sense evidence при
длинном тексте, совпавшей истории Textractor или scaffold-терминах разрешён один
прежний APE post-edit. При наличии принятого sense запускается независимый
conditioned-перевод из CURRENT без direct-черновика. Он получает выбранное
определение/русскую опору и до четырёх отличимых rejected-поверхностей; затем
`SensePreservationGate` морфологически сравнивает direct и conditioned. Потерянная
или добавленная полярность проверяется по остаточным смысловым единицам после
one-to-one привязки accepted evidence; `никто … не` считается одной единицей.
Числа и полярность проверяются раньше WSD-оценки, а primary-hint слова под
курсором — раньше совокупных вторичных смыслов. Оставшийся
structural-loss, `Conflict`/`Mixed` или `Unresolved` получает один адресный repair,
поэтому нормальный sense-path
делает два generation, предельный — три. До трёх
предыдущих строк передаются как типизированные `context_only` entries с отдельно
помеченной speaker metadata.
Текущий speaker label извлекается из OCR/text-hook отдельно от CURRENT; явно
выбранный для этой игры род передаётся второму проходу только как grammatical metadata без
имени. Известный мужской/женский род гарантирует контекстную попытку; если она не прошла,
полезный direct-result можно показать, но нельзя записать в gender-specific
кэш. Provider-neutral evidence содержит английские определения
и выбранные/отвергнутые поверхности как ограниченные, необязательные опоры.
Gate не меняет margin/решение ranker и не делает selected surface обязательной:
естественный парафраз получает `Unresolved`, а не display-ошибку, но до
разрешения не попадает в persistent cache. `банк` не совпадает с `банкет`, а
`не в порядке` не эквивалентно `в порядке`, но вложенное `в порядке` внутри
правильного `не в порядке` само по себе не считается rejected-вхождением;
падежные/глагольные/прилагательные
формы проверяются консервативной русской морфологией. Если один source встречен
в CURRENT больше одного раза, spanless hint получает `NotApplicable`, не входит
в prompt и не может навязать один смысл всем вхождениям.

Conditioned/repair builder ограничивает полный raw prompt 4800 символами при
закреплённом `ctx-size=2048`: сначала снимает старую скрытую историю, затем
advisory metadata и второстепенные sense-группы, но сохраняет primary-hint слова
под курсором, CURRENT и обязательные user/VN terms. Если ни одна однозначная
группа не помещается, conditioned generation не запускается; при наличии других
APE-сигналов выполняется обычный context-only post-edit без этих unsafe hints.

Generic guard проверяет размер и русскоязычность ответа, управляющие символы,
prompt echo исходной и effective-фразы, объяснения и residual English с
исключением устойчивых собственных имён. Он также отклоняет speaker prefix,
внешние кавычки или начальное многоточие, которых нет в CURRENT, и частично
скопированную английскую цитату. Он намеренно не требует повторить
перевод слова из первой строки. При
ошибке основной результат остаётся на месте, а вторая строка становится
`Перевод фразы недоступен`; скрытого генеративного fallback нет. Проверенные
результаты хранятся в
`%LOCALAPPDATA%\AsteriaScale\smart-phrase-translations.v7.json`; fingerprint v7
включает исходную и effective-фразу, identity преобразования, упорядоченную
скрытую историю, род текущего говорящего, semantic plan, устойчивые собственные имена, model identity и
prompt version. Model identity включает выбранный CPU/Vulkan backend и профиль
offload, поэтому GPU и CPU никогда не делят один принятый результат. Cache entry
сохраняет generation route и точный набор применённых source-групп; приоритет
hovered evidence входит в ключ. Structural-loss, `Unresolved`/`Mixed`/`Conflict`
не сохраняются. На cache hit этот набор
восстанавливается для benchmark telemetry; runtime и закреплённые артефакты
по-прежнему проверяются первыми.

Default smoke проверяет direct/APE prompt, request, parser, pins обоих runner,
явную CPU/Vulkan маршрутизацию без fallback, полное
предложение 1–64 слова, speaker extraction и per-game gender mapping,
sentence-wide sense evidence, пять состояний gate, repeated-source abstention,
negation/numeral priority, bounded conditioned/repair prompt,
идиоматические и буквальные пары, структурный guard, cache v7, miss/abstention
routing, независимость основной и второй строк и fail-open без запуска или
загрузки живой модели.
Систематический live A/B качества и задержки ещё не проведён.

## Сборка и тесты

```powershell
dotnet build AsteriaScale.slnx -c Release
dotnet run --project tests/AsteriaScale.Tests/AsteriaScale.Tests.csproj -c Release --no-build
```

Smoke-набор — консольная программа без тестового фреймворка: каждый тест — `await RunAsync("имя", ...)` c `Assert`. По умолчанию прогоняются все базовые тесты, включая короткий реальный жизненный цикл Magpie («Engine lifecycle»). Тестовые классы — `internal static class XxxTests` с `RunAsync(tempRoot)`; доступ к internal-членам приложения открыт через `InternalsVisibleTo("AsteriaScale.Tests")`.

### Gated-режимы тестов (по аргументам CLI)

| Аргумент | Что делает |
|---|---|
| `--live` | Живой апскейл Notepad через Magpie |
| `--smart-live` / `--smart-gpu-live` | Живой умный словарь end-to-end |
| `--ocr-image <png> <x> <y>` | Диагностика hover-OCR по изображению |
| `--ranker-benchmark <set.json> <workerBaseDir> [cpu\|npu] [--ranker-cpu-model glossbert\|deberta\|sandwich\|consec]` | Бенчмарк выбранного definition-aware ranker через полный продакшн-конвейер |
| `--ranker-sentence-benchmark <set.json> <workerBaseDir> --ranker-cpu-model glossbert\|sandwich\|consec [--sentence-report <report.json>] [--benchmark-only]` | WSD-only sentence A/B: точность sense GroupId по целям/предложениям/научным и surface-unambiguous срезам, contrast pairs, cold/warm latency, peak RAM и точная provenance; русский/full-text не оценивается |
| `--translation-suite <set.jsonl> <workerBaseDir> --system-id <id> --profile direct\|smart --phrase-backend cpu\|vulkan --ranker-cpu-model glossbert\|sandwich\|consec --translation-output <output.jsonl> --translation-report <report.json> [--shard i/n] [--max-cases N] [--benchmark-only]` | Production A/B TranslateGemma; `direct` переводит весь segment, `smart` — предложение под focus через реальный словарный/WSD-каркас, а ошибки отдельных реплик остаются в output |

`workerBaseDir` — каталог, содержащий `Ranker\AsteriaScale.RankerWorker.exe` (удобно сделать junction на bin-выход воркера). Наборы: `assets/Ranker/ranker-benchmark.json` (открытый dev, `134` случая, включая `20` научных) и `ranker-holdout*.json` (замороженные — только финальные подтверждения, каждое открытие фиксируется в [RESEARCH.md](RESEARCH.md)).
Команда завершается с ошибкой, если в наборе меньше 20 случаев, отсутствуют ранжируемые/размеченные решения, точность принятых решений ниже `90 %`, expected-inventory coverage ниже `70 %`, доля принятых размеченных решений ниже `45 %` или end-to-end correct coverage с учётом детерминированных решений ниже `45 %`. Это CI-gate результата, а не дополнительный критерий воздержания модели: runtime по-прежнему использует только замороженный margin `0,25`.

Sentence-бенчмарк запускается одинаково для всех CPU-профилей, всегда подаёт
`previousContext` только как скрытый вход и корректирует индекс target. Он
отказывается читать любой путь `ranker-holdout*` до открытия файла. Пример:

```powershell
dotnet run --project tests/AsteriaScale.Tests/AsteriaScale.Tests.csproj `
  -c Release --no-build -- --benchmark-only `
  --ranker-sentence-benchmark assets/Ranker/ranker-sentence-benchmark.json `
  dist/AsteriaScale --ranker-cpu-model consec `
  --sentence-report artifacts/Ranker/consec-sentence-report.json
```

Same-build итог на `46` предложениях / `138` целях: GlossBERT `91`, SANDWiCH
`120`, ConSeC `131` верная группа; scientific `69/108`, `96/108`, `106/108`;
все target-смыслы верны в `20`, `31`, `41` предложениях; на
surface-unambiguous WSD-срезе `67`, `86`, `94`. Это не оценка русского перевода.
ConSeC: accepted precision `96,32 %`, coverage `98,55 %`, warm p50/p95
`140/205 мс`, cold `6 420 мс`, peak `1 500 МБ`, ошибок и overflow нет. Он ошибается в одной
context-only паре и имеет пять уверенных ошибок, поэтому joint planner остаётся
следующим этапом. Frozen holdout при измерениях не открывался.

Публичный полнотекстовый стенд, pinned downloader, схема отчётов и команды
построения локального HTML/CSV-ладера описаны в
[BENCHMARKING.md](BENCHMARKING.md). Он не возвращает удалённые Qwen/Foundry
маршруты: `direct` вызывает TranslateGemma напрямую, `smart` — текущий
production-конвейер умного словаря. Большие наборы делятся детерминированными
`--shard`/`--max-cases`; точный selection hash записывается в output и report.
Для сравнения `direct`/`smart` используется только whole-hover-срез из
[BENCHMARKING.md](BENCHMARKING.md); многопредложный WMT segment не выдаётся за
одну карточку умного словаря.

Исторические наборы полного перевода
`assets/Translation/translation-benchmark.json` и
`translation-holdout-v1.json`, а также результаты Qwen/Foundry сохранены для
исследовательской воспроизводимости в [RESEARCH.md](RESEARCH.md). Production
Qwen-конвейер и его `--foundry-live`/`--translation-benchmark` команды удалены
30 июля 2026 года; они не являются требованиями текущей сборки. Их сменил
отдельный TranslateGemma-runner, который не возвращает Foundry Local в
приложение.

## Публикация

```powershell
./scripts/build.cmd    # publish в dist/AsteriaScale + Ranker + подпись identity-пакета
./scripts/install.cmd  # ярлык и автозапуск
./scripts/run.cmd      # запуск после однократной регистрации identity через install
```

`build.ps1` подписывает MSIX identity-пакет сертификатом `CN=Asteria Scale Local`
из `Cert:\CurrentUser\My`. Самоподписанный сертификат разработки должен быть
доверен текущим пользователем, иначе финальный `signtool verify /pa` и
`Add-AppxPackage` корректно отвергнут пакет. На новой машине создайте его,
экспортируйте только публичную часть и добавьте её в `TrustedPeople`:

```powershell
$certificate = New-SelfSignedCertificate -Type Custom -Subject "CN=Asteria Scale Local" `
  -KeyUsage DigitalSignature -CertStoreLocation "Cert:\CurrentUser\My" `
  -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
$publicCertificatePath = Join-Path ([IO.Path]::GetTempPath()) "asteria-scale-local.cer"
Export-Certificate -Cert $certificate -FilePath $publicCertificatePath | Out-Null
Import-Certificate -FilePath $publicCertificatePath `
  -CertStoreLocation "Cert:\CurrentUser\TrustedPeople" | Out-Null
Remove-Item -LiteralPath $publicCertificatePath
```

Импортируется `.cer` без закрытого ключа; доверяйте так только собственному
локальному сертификату разработки. Для распространения нужен сертификат от
доверенного центра или сервис подписи.

Identity-пакет нужен Windows AI API (NPU OCR): без него `TextRecognizer` недоступен обычному Win32-процессу.
Основное приложение остаётся self-contained по .NET, но Windows App SDK намеренно
подключается как **framework-dependent** (`WindowsAppSDKSelfContained=false`).
Asteria использует package-with-external-location и запускается из каталога
пользователя; Windows AI не разрешает self-contained приложению работать из
пути под `C:\Users`. В нашей схеме включение этого свойства ломает
`TextRecognizer.EnsureReadyAsync` с `FileNotFound`, даже когда identity и пакет
Text Recognition установлены. Windows App SDK при этом предоставляет объявленная
в identity зависимость `Microsoft.WindowsAppRuntime.1.8`.
Main publish также использует `PublishSingleFile=false`, поскольку single-file
не поддерживается для package-with-external-location. .NET остаётся
self-contained. Целостность многофайловой публикации закрывает
`.build-output-manifest.json` с размером и SHA-256 каждого файла.

Публикация сначала собирает и проверяет приложение, Ranker с обоими наборами моделей и подписанный identity-пакет
в уникальном staging-каталоге внутри `dist`. Рабочие `dist/AsteriaScale` и `AsteriaScale.Identity.msix`
заменяются только после всех проверок; при ошибке прежняя публикация остаётся на месте. `run` и `install`
сверяют `.build-revision` с Git HEAD и `.build-source-fingerprint` с манифестом путей, содержимого и удалений входных файлов, поэтому устаревший `dist` пересобирается даже в dirty worktree. Закрытый
`.build-output-manifest.json` дополнительно фиксирует путь, размер и SHA-256
**каждого** опубликованного файла (включая DLL/runtime Ranker): удаление,
добавление или повреждение любого файла также делает публикацию устаревшей.
Запущенное из трея приложение всё равно следует закрыть перед сборкой: Windows может удерживать файлы старой публикации.

**Что обязано лежать в готовой публикации.** Список строится из дерева
исходников (`Get-AsteriaRequiredDistFiles` в `scripts/build-state.ps1`) и
проверяется дважды: `build.ps1` не отдаёт неполный staging, а `run`/`install`
считают `dist` без любого из этих файлов устаревшим и пересобирают его.

- `AsteriaScale.exe`, `THIRD_PARTY_NOTICES.md`, `LICENSE`;
- `Dictionaries\` — каждый `assets\Dictionaries\*.tsv.gz`, плюс `README.md`,
  `vn-build-manifest.json` и `vn-sources.lock.json`;
- `TextHook\*.tsv` и `Morphology\*.tsv.gz` — по одному на исходный файл;
- `Ranker\AsteriaScale.RankerWorker.exe` и **оба** набора моделей:
  `Models\GlossBert\` (tokenizer, CPU-ONNX, три файла NPU-контекста) и
  `Models\GlossEmbeddings\` (`eng-senses.vec`, `minilm-l6-v2.onnx`,
  `tokenizer.json`). Модели не лежат в git — см.
  [assets/Ranker/README.md](../assets/Ranker/README.md);
- если локально установлены оба ONNX SANDWiCH, `Models\Sandwich\` копируется в
  Ranker-публикацию условно и попадает в общий output manifest. Отсутствие этого
  экспериментального профиля не делает обычную GlossBERT-публикацию неполной;
- если локально установлен проверенный четырёхфайловый ConSeC bundle,
  `Models\Consec\` также копируется условно и попадает в output manifest.
  Отсутствие личного CC BY-NC-SA профиля не делает обычную публикацию неполной;
- `AsteriaScale.Identity.msix` рядом с каталогом публикации: проверяются
  манифест, block map, content types, обязательные PNG с точными размерами и
  подпись `CN=Asteria Scale Local`; после подписания отдельно запускается
  `signtool verify`.

`run.cmd` дополнительно требует, чтобы identity-пакет был **зарегистрирован**
(`Add-AppxPackage`, это делает `install.cmd`) именно для текущего
`dist\AsteriaScale`: sparse package хранит `ExternalLocation`, и регистрация из
другого checkout не даёт identity этому EXE. Также сверяется SHA-256 реально
зарегистрированного `AppxManifest.xml` с текущим подписанным MSIX, поэтому старые
capabilities по тому же пути не считаются актуальными. Без package identity
основной Windows OCR недоступен, и раньше приложение молча запускалось без него.

`install.cmd` сначала пытается выполнить штатное транзакционное обновление. Для
ограничения sparse MSIX «та же версия уже зарегистрирована» старый подписанный
пакет, его `ExternalLocation` и прежний publish заранее сохраняются во временный
rollback-набор; только после этого допустимы сборка и замена регистрации. Если
новая регистрация не проходит, восстанавливаются и регистрация, и внешние
файлы. При любой другой ошибке рабочая регистрация вообще не удаляется, а
publish всё равно откатывается к согласованной с ней версии.

## Регенерация морфословаря

`assets/Morphology/ru-morph.tsv.gz` (глагольные инфинитивы и прошедшее время
OpenCorpora) собирается так:

```powershell
pip install pymorphy3 pymorphy3-dicts-ru
python scripts/build-ru-morphology.py --output assets/Morphology/ru-morph.tsv.gz
```

## Регенерация словарей

Скачайте источники в `tools/dictionary-source` (ссылки в [assets/Dictionaries/README.md](../assets/Dictionaries/README.md)) и выполните команду из того же README. Критично: `normalize_english` в генераторе и `NormalizeEnglish`/`NormalizeEnglishSentenceKey` в `LocalDictionaryService` обязаны давать одинаковую каноническую форму ключей — при изменении одной стороны меняйте вторую и добавляйте тест в `LocalDictionaryNormalizationTests`.

VN-слой можно воспроизвести отдельно, не пересобирая многогигабайтные
Wiktionary-источники:

```powershell
python scripts/build-dictionaries.py `
  --curated-vn tools/dictionary-source/vn-lexicon.tsv `
  --vn-source-lock tools/dictionary-source/vn-sources.lock.json `
  --massive-archive tools/dictionary-source/amazon-massive-dataset-1.1.tar.gz `
  --output assets/Dictionaries
python scripts/test-build-dictionaries.py
```

Архив MASSIVE не хранится в git: его размер и SHA-256 закреплены в
`vn-sources.lock.json`. Генератор сверяет lock до чтения, пишет gzip с
фиксированным `mtime=0` и создаёт schema-v2 `vn-build-manifest.json` с хешами
генератора, lock и каждого результата. Curated и MASSIVE передаются только
вместе; набор сначала строится во временном каталоге, manifest заменяется
последним, а ошибка или прерывание замены откатывает уже перенесённые файлы. Python-тест
также сверяет текущие generator/curated/source-lock hashes с manifest. Source lock
публикуется рядом с manifest. Для расширения сначала добавляйте записи в структурированный
`vn-lexicon.tsv` с явной ролью `hard`, `soft` или `scaffold`; сгенерированные
TSV вручную не редактируются. Frozen benchmark/holdout-файлы в эту сборку не
входят.

## Стиль кода

- C# 13, file-scoped namespaces, `sealed` по умолчанию, records для DTO.
- Комментарии — по-английски и только про «почему» (инвариант, который код не показывает), не про «что делает строка».
- Строковые сравнения — явные: `StringComparer.Ordinal` для ключей, `OrdinalIgnoreCase` для путей/имён файлов.
- Ошибки внешних подсистем не роняют hover-путь: словари, кэш и диагностика деградируют мягко (см. существующие catch-блоки — они узкие и мотивированные).
- Новые зависимости NuGet — только при реальной необходимости; проект намеренно офлайн и локальный.
