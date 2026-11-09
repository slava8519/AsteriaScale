# Разработка и сборка

## Требования

| Компонент | Версия | Обязательно |
|---|---|---|
| Windows 11 | build 26100+ (для NPU OCR) | да |
| macOS (Apple Silicon) | 14.0+ (`libonnxruntime.dylib` ORT 1.27.1) | только CPU-воркер (ConSeC) и macOS-подмножество smoke |
| .NET SDK | 10.0 | да |
| [Magpie](https://github.com/Blinue/Magpie) | 0.12.1 | да (движок апскейла, ставится отдельно) |
| Windows AI Text Recognition | из Windows App SDK | для OCR-перевода |
| TranslateGemma 4B Q4_K_M + llama.cpp CPU/Vulkan | b9632 | опционально, независимый перевод фразы второй строкой |
| AMD NPU + драйвер | 32.0.203.329+ | опционально (NPU-путь GlossBERT) |
| ConSeC/WNGT | upstream `9602b5fd…`, CC BY-NC-SA 4.0 | опционально, личный научный/некоммерческий CPU ranker |
| Python | 3.11+ (`python` в `PATH` на Windows, `python3` на macOS) | для полного smoke-набора, macOS-подмножества и регенерации артефактов |

## Артефакты модели GlossBERT

Набор GlossBERT в `src/AsteriaScale.RankerWorker/Models/GlossBert` занимает 626 МБ,
и три его файла **не хранятся в git** (`.gitignore`; отдельные файлы превышают
лимит GitHub 100 МБ): `Cpu/glossbert-fp32-dynamic.onnx`,
`Npu/glossbert-eager-vitis-context.onnx` и
`Npu/glossbert-eager-vitis-context.onnx_VITISAI.bin`. В git лежат только
`tokenizer.json` и `Npu/glossbert-eager-vitis-context.onnx.compatibility.txt`.
Публичного источника трёх больших файлов сейчас нет: Git LFS не настроен
(в `.gitattributes` нет `filter=lfs`), GitHub Release и тегов нет, а ONNX-экспорт в `tools/` есть только для ConSeC
(`export-consec-onnx.py`), но не для GlossBERT.

Что о закреплённых файлах записано в репозитории: CPU-граф — непроквантованный
динамический FP32 ONNX модели [kanishka/GlossBERT](https://huggingface.co/kanishka/GlossBERT)
ревизии `0cc3b83af5496e27ebcc95ef0cf37ea0a9281a7a` (MIT); NPU-набор — заранее
скомпилированный AMD Vitis AI EPContext, чей compatibility-файл указывает
устройство `IPU_STRIX` (`AMD_AIE2PS_VAIML`). Точная процедура экспорта и компиляции не
записана. Размеры и SHA-256 всех пяти файлов закреплены в `RankerEngine.cs` и
[assets/Ranker/README.md](../assets/Ranker/README.md) и проверяются воркером до
загрузки: с несовпадающими файлами ранжировщик откажется работать. Поэтому
собственный экспорт или повторная компиляция не заменяют закреплённые файлы:
для NPU-контекста воспроизвести закреплённый SHA-256 по записанным в репозитории
данным нельзя, а другой артефакт означает перепин в коде и документации, а не
подмену файла.

Кроме работы GlossBERT в самом приложении, от этих файлов зависят только
публикация и gated-бенчмарки. `dotnet build AsteriaScale.slnx` их не требует (csproj воркера подбирает
`Models\GlossBert\**` по glob), smoke-набор тоже: тесты пишут однобайтовые
заглушки, а тест-проект не ссылается на ONNX Runtime. `scripts/build.cmd`,
напротив, без условий требует все пять файлов GlossBERT и три файла
GlossEmbeddings (`Get-AsteriaRequiredRankerFiles` в `scripts/build-state.ps1`)
и останавливается с `Publish is incomplete. Missing: …`. `AsteriaScale.slnx`
собирается только на Windows; на macOS проекты собираются по одному (раздел
«macOS (Apple Silicon)» ниже), и macOS-проект воркера ничего не копирует из
`Models`.

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

## Открытые dev-наборы ранжировщика

Pair-ориентированный открытый dev-набор `assets/Ranker/ranker-benchmark.json`
содержит `134` случая visual novel, манги, литературы и научных статей (`20`
scientific cases). Каждый отчёт закрепляет точный SHA набора; frozen
`ranker-holdout*.json` при A/B не открываются. Same-build A/B ранжировщиков и
причина, по которой GlossBERT остаётся default, записаны в
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

На macOS установщика и проверочного скрипта нет: оба написаны на PowerShell.
Бандл лежит в том же ignored-каталоге `src/AsteriaScale.RankerWorker/Models/Consec`
(те же условия CC BY-NC-SA 4.0), а проверкой служит собственная сверка
воркера при каждой загрузке: четыре пути, размеры, SHA-256, поля manifest и
special IDs токенизатора. macOS-воркер читает бандл на месте через
`--model-directory src/AsteriaScale.RankerWorker/Models` и никуда его не
копирует (раздел «macOS (Apple Silicon)»).

Безопасный export и официальный reference gate описаны в
[`tools/README-consec.md`](../tools/README-consec.md). Gate уже пройден против
точного upstream Transformers 4.3.3: input hashes и choices совпали, max delta
всех logits — `3,10e-6` на dialogue и `5,01e-6` на mixed-case scientific
fixture.
Встроенный независимый профиль доступен для исследования, но GlossBERT остаётся
stable-default до feedback/joint planner и full-text A/B.

## Удалённые профили SANDWiCH и GlossDeBERTa

8 ноября 2026 из кода, скриптов и `tools/` удалены готовый SANDWiCH
(установщик, проверка, ONNX-экспорт, CPU-профиль воркера и UI-вариант),
профиль GlossDeBERTa и инструменты закрытого эксперимента собственного
DeBERTa fine-tune на RTX 5080 (сборка обучающего набора, trainer, переносимый
пакет, launcher и их requirements). Последний коммит, где они есть, —
`911510e`: инструкции установки SANDWiCH и воспроизведения эксперимента
читайте через `git show 911510e:docs/DEVELOPMENT.md`. Измерения и причины
удаления — в [RESEARCH.md](RESEARCH.md). Сохранённое в `settings.json`
значение `sandwich` или `deberta` загружается как GlossBERT, а воркер
отвергает `--cpu-model sandwich|deberta`. Локальные бандлы
`Models/Sandwich` и `Models/GlossDeberta` остаются в `.gitignore` и в
публикацию не копируются. Бэкбон DeBERTa-large внутри ConSeC это удаление не
затрагивает.

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

### macOS (Apple Silicon)

На macOS собираются и запускаются только CPU-воркер (ConSeC) и
macOS-подмножество smoke-набора (роадмап №20.1, решение M7 в
[MACOS.md](MACOS.md)). Оба проекта — `net10.0`/`osx-arm64`, не входят в
`AsteriaScale.slnx` и компилируют те же исходники по ссылке, а не копией:

- `src/AsteriaScale.RankerWorker.MacOS` — воркер без Windows ML и без единого
  `Content`: модели читаются на месте через `--model-directory`, поэтому его
  сборка никогда не копирует бандл ConSeC в `bin/`;
- `tests/AsteriaScale.Tests.MacOS` — подмножество того же набора с общим
  `Program.cs`. Windows-only части стоят под `#if WINDOWS`, а ветки `#else`
  объявляют, что не выполнено (`MacSmoke.cs`). Сборка тест-проекта сначала
  собирает macOS-воркер (ссылка только задаёт порядок сборки); как и Windows
  тест-проект, он не ссылается на ONNX Runtime.

```bash
dotnet build tests/AsteriaScale.Tests.MacOS/AsteriaScale.Tests.MacOS.csproj -c Release
bash tools/macos/run-macos-subset.sh --allow 'home/Library/Application Support/AsteriaScale/hover-translation.log'
```

`tools/macos/run-macos-subset.sh` ничего не собирает. Каждый прогон он
запускает в новом каталоге `W` из `mktemp -d` (вне клона): текущий каталог —
`W/cwd`, `TMPDIR=W/tmp`, а `HOME` и `CFFIXED_USER_HOME` указывают на `W/home`
с пустой `Library/Application Support`. .NET берёт LocalApplicationData из
`HOME`, а код на CoreFoundation — из `CFFIXED_USER_HOME`, поэтому заданы оба и
прогон не пишет в настоящую `~/Library`. После прогона скрипт перечисляет всё,
что осталось в `cwd`, `home` (кроме двух созданных им пустых папок) и `tmp`;
запись, которую не называет `--allow <путь относительно W>` (сама, папкой
выше или папкой на пути к ней), даёт код `9`. До и после прогонов он считает
отчёты о сбоях владельца в `~/Library/Logs/DiagnosticReports` с именами
`^(AsteriaScale|dotnet|Python|python3|swift)` (папка только читается; её
путь берётся из базы пользователей, а не из `HOME`, потому что ReportCrash
пишет туда при любом `HOME` упавшего процесса) и при росте их числа
завершается кодом `10`. `--repeat N` повторяет прогон `N` раз, каждый раз в
новом каталоге, и останавливается на первом сбое, сохраняя его каталог;
каталог чистого прогона удаляется. `--empty-home` создаёт домашний каталог
без `Library`. Остальные коды: `2` — ошибка аргументов, нет собранного
apphost или не Apple Silicon; любой другой — код самого подмножества (`1`
при `FAIL`). Прямой запуск apphost или `dotnet run` работают с настоящим
домашним каталогом, и о таком прогоне не сообщают.

Подмножество запускается только через apphost
(`bin/Release/net10.0/osx-arm64/AsteriaScale.Tests`). Ядро ищет словари и
морфологию рядом с процессом, а под `dotnet AsteriaScale.Tests.dll` это каталог
установки `dotnet`, где морфологический лексикон молча оказался бы пустым,
поэтому такой запуск завершается ошибкой. Любой аргумент тоже ошибка:
`MacSmoke.Begin` отвергает его до первого теста, поэтому gated-режимы и
бенчмарки на macOS недостижимы, пока provenance бенчмарка не различает
платформу. Их код при этом компилируется; под `#if WINDOWS` вынесены только
`--live` и режимы OCR-изображений.

Из `56` общих регистраций выполняются `39`. Остальные `17` Windows-only
объявлены на месте через `MacSmoke.Skip` с названием API, которое их требует
(WPF-сборка, Magpie, Windows AI OCR, Windows PowerShell, `cmd.exe`), и
печатаются строками `skip`. Шесть Windows-only проверок внутри выполняемых
тестов объявлены через `MacSmoke.Part` и печатаются строками `note`; одна из
них — бюджет управляемой памяти, который один тест, «Local dictionary
lookup», печатает дважды (замеры `Layered dictionaries` и `Aligned sense
metadata`), поэтому строк `note` семь. Затем идут три проверки только для
macOS (префикс `macOS: `):

- паритет с Windows-проектами: пакеты и их версии, общие файлы воркера,
  данные тест-проекта, отсутствие `Content`/`None`/`EmbeddedResource` у
  macOS-воркера, отсутствие ONNX Runtime в выходе тестов и регистрации общего
  `Program.cs` (см. ниже);
- отказ воркера от NPU без перехода на CPU: `--provider npu --smoke` и
  `--smoke` с провайдером по умолчанию дают `npu_provider_unavailable` и код
  выхода `1`;
- ответ воркера на запрос ConSeC: `--smoke` с бандлом, прочитанным на месте из
  `src/AsteriaScale.RankerWorker/Models`, проверяет выбор, провайдер, pipeline
  и точную identity модели.

Эталонный прогон: `42` строки `ok`, `17` — `skip`, `7` — `note`, последняя
строка:

```text
PASS (macOS subset): 39/56 shared smoke tests + 3 macOS checks; SKIPPED: 17 Windows-only; 6 Windows-only parts not run; ConSeC real bundle: checked in place: tokenizer length+SHA-256 pin and 2 tensor goldens; ConSeC worker smoke: checked in place. Not the full Windows smoke suite.
```

Это результат macOS-подмножества, а не smoke-набора, и сообщать о нём нужно
именно так. `MacSmoke.Summarize` печатает строку `PASS`, только если
выполненные и объявленные регистрации вместе дают ровно `56` с
неповторяющимися именами, прошли все три macOS-проверки, достигнуты все шесть
объявленных частей и записаны оба статуса ConSeC. `checked in place` значит,
что токенизатор сверен с закреплёнными длиной и SHA-256 и двумя
tensor-эталонами, а воркер ответил на `--smoke`. Без бандла оба статуса
начинаются с `absent: ` и называют отсутствующий путь; прогон остаётся
зелёным, но строка это показывает.

Сам `Summarize` видит только то, что скомпилировала macOS-сборка, поэтому
регистрацию, добавленную или переименованную внутри блока `#if WINDOWS`, он
бы не заметил. Это закрывает проверка «macOS: project parity with the Windows
projects»: она читает `tests/AsteriaScale.Tests/Program.cs` так, как его
компилирует каждая сборка (понимает только `#if WINDOWS`, `#if !WINDOWS`,
`#else` и `#endif`; любая другая директива или несбалансированная
вложенность — ошибка), берёт default-путь между проверкой
`TestCommandLine.FindUnknownOption(args)` и `int screenOcrImageArgument` и
требует, чтобы имена `await RunAsync(…)` Windows-сборки совпадали с именами
`await RunAsync(…)` и `MacSmoke.Skip(…)` macOS-сборки (без префикса
`macOS: `) поимённо и по порядку, без повторов, и чтобы их было ровно
`MacSmoke.SharedRegistrationCount`. Поэтому добавление или удаление
регистрации default-пути, в том числе Windows-only, требует в той же правке
нового значения счётчика, а для Windows-only — ещё и парного `MacSmoke.Skip`;
при переименовании Windows-only регистрации меняется только имя в её
`MacSmoke.Skip`. Иначе прогон падает с сообщением о первом расхождении.

В домашнем каталоге прогона подмножество пока пишет журнал
`Library/Application Support/AsteriaScale/hover-translation.log`, поэтому
команда выше разрешает ровно его. С `--empty-home` тот же журнал оказывается
в текущем каталоге (`cwd/AsteriaScale/hover-translation.log`): без папки
`Library/Application Support` .NET возвращает пустой путь LocalApplicationData,
и путь журнала становится относительным; такой прогон завершается кодом `9`.
Папку `Dictionaries` прогон не создаёт и пользовательские слои
`eng-rus*`/`jpn-rus*` оттуда не читает: тесты словарей, включая «Aligned
dictionary sense metadata», берут пользовательский каталог из временного корня
прогона. `TMPDIR` после прогона пуст.

Что подмножество печатает, а не проверяет:

- **Бюджеты управляемой памяти не применяются.** Тест «Local dictionary
  lookup» печатает оба замера, `Layered dictionaries` и `Aligned sense
  metadata`, с удержанными МБ рядом с Windows-бюджетом и пометкой
  `NOT ENFORCED on macOS`. На эталонном прогоне слоёные словари удерживали
  `314,7 МБ` при бюджете `256 МБ`, а sense-метаданные — `-67,1 МБ`:
  отрицательное значение показывает, что settle-замер на macOS ненадёжен.
  Калибровка — follow-up карточки №20 [ROADMAP.md](ROADMAP.md).
- **Артефакты модели — «validated at load».** На Windows `FileShare.Read` не
  даёт другому процессу заменить, обрезать или удалить проверенный файл модели,
  пока воркер его держит. На macOS .NET отображает `FileShare` на advisory
  `flock(2)`: отказ получает только открывающий с эксклюзивной блокировкой (это
  подмножество и проверяет), а запись, переименование и удаление другими
  процессами не блокируются; ONNX Runtime к тому же повторно открывает файл по
  пути. Поэтому на macOS гарантия — сверка размера и SHA-256 при загрузке. Эта
  гарантия выбрана для 20.1 по умолчанию при реализации этапа; владелец может
  её пересмотреть. Усиление — follow-up карточки №20.

Воркер отдельно:

```bash
W=src/AsteriaScale.RankerWorker.MacOS/bin/Release/net10.0/osx-arm64/AsteriaScale.RankerWorker
dotnet build src/AsteriaScale.RankerWorker.MacOS/AsteriaScale.RankerWorker.MacOS.csproj -c Release
$W --provider cpu --cpu-model consec --model-directory src/AsteriaScale.RankerWorker/Models --smoke
```

Ожидается код `0` и одна строка JSON с `ok:true`, `index:20`,
`CPUExecutionProvider`/`CPU`, `pipeline:"consec-independent"` и точной identity
ConSeC; в stderr — только строки `[ranker]`. Без `--smoke` воркер ведёт обычную
JSONL-сессию с теми же аргументами. Запрос NPU (`--provider npu`, а также
запуск без `--provider`: провайдер по умолчанию по-прежнему NPU) получает
ошибку `npu_provider_unavailable` до чтения любых артефактов; CPU не
подставляется. С `--smoke` воркер печатает эту ошибку и завершается с кодом
`1`. В JSONL-сессии процесс не завершается: `hello` отвечает успехом, каждый
`warm` и каждый корректный `select` — `ok:false` с `errorCode`
`npu_provider_unavailable` (отказ кэшируется), а воркер обслуживает запросы
дальше до `shutdown` или конца stdin и выходит с кодом `0`. `--provider cpu`
без `--cpu-model consec` требует CPU-ONNX GlossBERT, которого нет в git, и
отвечает `model_missing`.

**Не собирайте Windows-проекты в рабочем дереве на macOS:** `AsteriaScale.slnx`,
`src/AsteriaScale.RankerWorker/AsteriaScale.RankerWorker.csproj` и
`tests/AsteriaScale.Tests/AsteriaScale.Tests.csproj`. Без
`EnableWindowsTargeting` они останавливаются на NETSDK1100, а с ним csproj
Windows-воркера ставит локальный бандл ConSeC (`1,6 ГБ`, CC BY-NC-SA 4.0) в
`bin/`.

#### Проверка, что Windows-сборка не изменилась

Правка общего исходника (`src/AsteriaScale.RankerWorker/*.cs`,
`tests/AsteriaScale.Tests/*.cs`) ради macOS по умолчанию не меняет токены,
видимые Windows-сборке: код для macOS стоит под `#if !WINDOWS` или в ветке
`#else` (SDK определяет `WINDOWS` только для целей `net10.0-windows…`), и
скрипт ниже доказывает, что выходы Windows-сборки не изменились (код `0`,
`IDENTICAL`). В этапе 20.1 есть ещё пять правок видимых Windows токенов вне
`#if`, которые скрипт доказал идентичными: удалён неиспользуемый `using`, и
четыре константы читаются из оригинала в ядре (`PreviousContextLimits`) вместо
равных им псевдонимов `TextHookService`.

Решение владельца M12 ([MACOS.md](MACOS.md)) разрешает Треку III менять
Windows-код уже сейчас — ядро, RankerProtocol, ranker-воркер, WPF-хост и общие
тесты, — а полный прогон на Windows по M10 откладывается до конца Трека III.
Такая правка в том же коммите обязана: (1) кросс-собираться — после коммита
при чистом рабочем дереве скрипт ниже в режиме ревизий (`HEAD~1 HEAD`) должен
пройти все Windows-сборки и завершиться кодом `1` с `DIFFERENT` (код `4`,
упавшая Windows-сборка, недопустим никогда); (2) оставлять зелёным
macOS-подмножество; (3) добавить строку в таблицу «Windows-изменения до прогона
на Windows» в [MACOS.md](MACOS.md): что изменилось и что проверить на Windows;
(4) нести в сообщении изменённые строки манифеста, строку
`audit-verbatim-move.py regex --base HEAD~1` и, если коммит переносит или
правит общий код, оба раздела `audit-verbatim-move.py move` (множественный и
учитывающий порядок) с классом замены для каждого остаточного фрагмента (см.
«Аудит переноса и `[GeneratedRegex]`» ниже). Правка, которую скрипт доказал
идентичной, строки не требует. Запуск:

```bash
bash tools/macos/check-windows-identity.sh <base-rev> [--worktree | <candidate-rev>]
git status --porcelain                                   # после коммита: пусто
bash tools/macos/check-windows-identity.sh HEAD~1 HEAD   # результат для коммита
```

Без второго аргумента кандидат — рабочее дерево (`--worktree`), включая
незакоммиченные правки и неотслеживаемые файлы: файл, который так и не попал в
`git add`, такую проверку проходит, а в коммит не попадает. Поэтому
`--worktree` — только предварительная проверка до коммита, а в сообщение
коммита идёт запуск `HEAD~1 HEAD` после него при пустом `git status
--porcelain`. Скрипт экспортирует обе стороны в `$TMPDIR` (внутри
клона он работать откажется; `git archive` для ревизии,
`git ls-files -co --exclude-standard` для рабочего дерева, поэтому ignored-бандл
ConSeC в экспорт не попадает) и подкладывает четыре пустые заглушки бандла.
Symlink на месте `ConSeC_WNGT` или `Models/Consec` либо внутри них, на месте
`src`, папки воркера или `Models`, а также любой symlink под `Models`, если он
попал в экспорт, останавливают скрипт до записи заглушек: запись через такую
ссылку обнулила бы настоящий бандл. Обычная неотслеживаемая ссылка на месте
`Models/Consec` или внутри него игнорируется `.gitignore` и в экспорт не
попадает; отказ срабатывает, когда ссылку отслеживает git или её содержит
проверяемая ревизия. Неотслеживаемый вложенный git-репозиторий пропускается с
пометкой в stderr, без его содержимого. Затем скрипт по очереди в одном и том
же каталоге выполняет publish воркера из
`scripts/build.ps1` (`-r win-x64`, self-contained) и сборку Windows
smoke-набора с `EnableWindowsTargeting` и сравнивает SHA-256 каждого
полученного файла. Успех — `IDENTICAL: <n> Windows output files` и код `0`
(сейчас `n` = `286`); если выходы отличаются, печатается diff манифестов и
`DIFFERENT`, код `1`, а рабочий каталог сохраняется. Остальные коды: `2` —
ошибка аргументов или отказ экспорта (symlink, лицензионный файл в ревизии),
`3` — лицензионный файл ConSeC в рабочем дереве, `4` — упала одна из
Windows-сборок (последние строки её журнала печатаются). Сравнивайте по этой строке, а не по хешам DLL между
запусками: хеш `AsteriaScale.Core.dll` зависит от пути нового рабочего каталога
(`[GeneratedRegex]`), а `AsteriaScale.Tests.dll` Windows-сборки невоспроизводим
даже из одного дерева (генератор CsWinRT пишет таблицу vtable в случайном
порядке), поэтому скрипт собирает набор дважды; подробности — в заголовке
скрипта. Правка `THIRD_PARTY_NOTICES.md` ожидаемо даёт ровно одну
отличающуюся строку, `./Tests/THIRD_PARTY_NOTICES.md`: WPF-проект копирует этот
файл в выход.

Проверка не доказывает того, что выполняется только на Windows: сам
smoke-набор, NPU/VitisAI, MakePri и MSIX; запускает их владелец на Windows.
При `IDENTICAL` эти прогоны видят неизменённые входы. Правку с `DIFFERENT`
кросс-сборка доказывает только компилируемой; по M12 её поведение на Windows
проверяется в конце Трека III по таблице «Windows-изменения до прогона на
Windows» в [MACOS.md](MACOS.md).

#### Аудит переноса и `[GeneratedRegex]`

Скрипт выше сравнивает выходы генераторов только хешами отсортированных
токенов, поэтому перенесённый `[GeneratedRegex]` от изменённого не отличает, а
байты DLL ничего не говорят о том, перенесён ли код дословно и в прежнем
порядке. Это закрывает `tools/macos/audit-verbatim-move.py` (только
стандартная библиотека Python; читает ревизию через `git` и файлы рабочего
дерева, ничего не меняет). Запускайте его из корня клона; пути —
относительно корня:

```bash
python3 tools/macos/audit-verbatim-move.py move --base <rev> --from <файл>... --to <файл>...
python3 tools/macos/audit-verbatim-move.py regex --base <rev> [--paths <каталог>...]
```

`move` читает файлы `--from` в ревизии `<rev>`, а файлы `--to` — из рабочего
дерева как непустые строки без начальных и конечных пробелов и печатает два
раздела:

- множественный, `verbatim: K/N`: сколько из `N` строк базы нашлось среди
  строк назначения (каждая строка назначения засчитывается один раз). Порядка
  он не видит: переставленные операторы проходят как дословные;
- учитывающий порядок: для каждого файла `--to` по очереди
  `difflib.SequenceMatcher` (`autojunk=False`) сопоставляет с ним ещё не
  взятые строки базы в исходном порядке. `base residual R` — строки базы,
  которые не взял ни один файл, `destination-only D` — строки назначения вне
  совпавшей подпоследовательности; обе группы печатаются фрагментами
  `файл:строки`. Совпавшие строки идут в одном порядке с обеих сторон, поэтому
  переставленный или вставленный оператор здесь виден всегда.

Если `--from` и `--to` называют один файл, тот же режим проверяет правку на
месте. Код `0` значит, что отчёт напечатан: оценивает его автор коммита,
относя каждый остаточный фрагмент к разрешённому классу замен в сообщении
коммита.

`regex` сравнивает аргументы всех атрибутов `[GeneratedRegex(...)]` в
`src/AsteriaScale.Core` и `src/AsteriaScale` (или в каталогах `--paths`) в
ревизии `<rev>` и в рабочем дереве — отслеживаемые и неотслеживаемые
неигнорируемые `.cs`, как их компилирует сборка, — как мультимножества
(шаблон, опции, таймаут, культура). Сканер понимает комментарии, директивы
препроцессора, символьные литералы и обычные, verbatim, raw и интерполированные
строки, поэтому `)` внутри шаблона не обрывает атрибут, а закомментированный
атрибут не считается; пробелы и комментарии вне литералов не учитываются,
литералы сравниваются как написаны. Успех — `GeneratedRegex attributes:
identical (<n>)` и код `0` (сейчас `n` = `22`: `20` в ядре и по одному в
`TextHookService` и `WindowsAiOcrService`); различие — список удалённых и
добавленных атрибутов с местами и код `1`. Код `2` в обоих режимах — ошибка
аргументов: неизвестная ревизия, нечитаемый файл, каталог `--paths` без
единого `.cs`, исходник, который сканер не может разобрать.

Свой набор тестов у инструмента — `python3
tools/macos/test-audit-verbatim-move.py` (`18` тестов на временных
git-репозиториях без глобальной и системной конфигурации git): чистый
перенос, правка строки, дважды встречающаяся `}`, перенесённая один раз,
переставленные и вставленный операторы, перенос regex без изменений,
изменённые шаблон и опции, многострочный атрибут с `)` внутри verbatim-строки.

#### Клон и iCloud Drive

Держите клон вне папок, которые синхронизирует iCloud Drive (например,
`~/Documents` при включённой синхронизации «Рабочего стола» и «Документов»),
или в папке, имя которой оканчивается на `.nosync`: такие папки iCloud не
синхронизирует. Если клон переименован, по старому пути можно оставить symlink
на новую папку. Причины:

- iCloud создаёт копии-конфликты вида `name 2.ext` в `bin/`, `obj/` и среди
  исходников, а лишний `* 2.cs` попадает в glob `*.cs` проектов;
- iCloud выгрузил бы в облако бандл ConSeC, которому по CC BY-NC-SA и решению
  M7 нельзя попадать в публичные хранилища;
- «Оптимизировать хранилище Mac» оставляет на диске только заглушки давно не
  открывавшихся больших файлов, в том числе моделей; их чтение требует
  повторной загрузки из облака.

Проверка reparse point в воркере (`ConsecArtifactBundle.cs`) отвергает
symlink у самого каталога `--model-directory` (`Models`), у `Models/Consec` и
у всего ниже, с ошибкой `consec_artifact_path_unsafe`. Каталоги выше
`--model-directory` не проверяются, поэтому symlink на месте старого пути к
клону работает, а symlink на `Models`, `Models/Consec` или файлы бандла — нет.

### Стратегия тестирования

Объём после удаления SANDWiCH и GlossDeBERTa (8 ноября 2026), этапа №20.1
и регистрации «Smart hover engine strings» этапа 20.2 (9 ноября 2026):
default-путь `tests/AsteriaScale.Tests/Program.cs` содержит
`56` блоков `await RunAsync(...)`, из них `32` вызывают отдельные классы
`XxxTests.RunAsync`. `56` — число регистраций Windows-прогона
(`MacSmoke.SharedRegistrationCount`); macOS-подмножество выполняет `39` из них
и три собственные проверки. В `tests/` `47` файлов C# — `27 317` строк вместе с
harness gated-бенчмарков и тремя файлами `tests/AsteriaScale.Tests.MacOS` —
против `36 069` строк C# в `src/`. Отдельно есть пять
Python-наборов `unittest` на `71` тест (см. «Python-тесты инструментов»).

| Слой | Что проверяет | Где | Что нужно кроме .NET 10 SDK |
|---|---|---|---|
| Чистая логика | словарный поиск и группировку смыслов, эвристики, регистр, извлечение OCR-предложения, prompt/parser/guard/gate TranslateGemma, маршруты hover-конвейера с подставными переводчиком и воркером | блоки `Program.cs`, `SmartDictionaryQualityFoundationTests`, `SmartPhraseTranslationFoundationTests`, `SmartHoverPhraseIntegrationTests`, `TranslateGemmaTranslationServiceTests` | ничего |
| Контракты данных C# ↔ Python | одинаковую нормализацию ключей (общий `scripts/dictionary-normalization-contract.tsv` читают и тест, и генератор), VN-слои, schema и provenance наборов, приём output C#-runner Python-scorer'ом | `LocalDictionaryNormalizationTests`, `VnDictionaryLayerTests`, `RankerBenchmarkProvenanceTests`, `TranslationSuiteTests`, `AtomicOutputSafetyTests` | Python 3.11+ как `python` в `PATH`: тесты запускают `tools/translation-benchmark.py` и `tools/atomic_output.py` |
| Процессы, IPC, кэши, транзакции | протокол v4 и fail-closed identity воркера, дедлайн ответа с остановкой воркера, строку движка каждого результата hover-конвейера (словарь, эвристики, Tatoeba, оба кэша, выбор и воздержание ранжировщика CPU и NPU, его недоступность, строки фразы) с поддельным воркером, общие признаки провайдеров с CLI-round-trip хост → воркер, ключи и инвалидацию кэшей решений, транзакционность установщиков и `build`-скриптов, безопасность правок конфига Magpie, text-hook, DPI override (через in-memory хранилище, не реестр) | `GlossBertDictionarySelectorServiceTests`, `SmartHoverEngineStringTests`, `RankerWorkerRegressionTests`, `RankerProviderTraitsTests`, `GlossBertDecisionCacheTests`, `SmartDictionaryResultCacheTests`, `ConsecBundleInfrastructureTests`, `BuildScriptSafetyTests`, `MagpieSafetyTests`, `TextHookSafetyTests`, `DpiCompatibilityServiceTests` | встроенный Windows PowerShell (`System32\WindowsPowerShell\v1.0\powershell.exe`): им запускаются поддельный воркер и проверяемые скрипты. Модели не нужны |
| Реальный движок | «Installed effects» (эффекты всех пресетов на месте) и «Engine lifecycle» (запуск и остановка Magpie с временным `config.json` и его резервной копией) | конец default-пути `Program.cs` | установленный Magpie 0.12.1 |
| Gated-бенчмарки | качество, покрытие и задержку на открытых и замороженных наборах через собранную публикацию | режимы из таблицы ниже, только по явному аргументу | `workerBaseDir` с воркером и моделями; для `npu` — AMD NPU; для `--translation-suite` — установленная TranslateGemma и скачанные наборы |

На macOS из этих слоёв выполняются чистая логика (без WPF-помощников),
контракты данных — с `python3` — и процессы/IPC: поддельные воркеры там
написаны на `python3` и `/bin/sh` вместо Windows PowerShell. Установщики,
`build`-скрипты, Magpie, text-hook, DPI, реальный движок и gated-бенчмарки на
macOS не выполняются (`skip` или не компилируются).

Сознательно не покрыто default-набором:

- **WPF UI.** Окна и оверлеи не создаются; `App.xaml` и `AsteriaScale.csproj`
  проверяются только как XML.
- **Живой NPU OCR.** Проверяются статические помощники `WindowsAiOcrService`
  (сборка предложения, границы захвата, починка mixed-script); распознавание
  Windows AI запускают только `--ocr-image` и `--screen-ocr-image`.
- **Инференс моделей и качество вывода GPU.** Ни один тест-проект не ссылается
  на ONNX Runtime, а для TranslateGemma проверяются pins, аргументы CPU/Vulkan
  `llama-server` и окружение процесса без его запуска. Живой вывод проверяют
  gated-режимы и `scripts/verify-translategemma.ps1`. Исключение —
  macOS-подмножество: при установленном бандле оно запускает собранный
  macOS-воркер отдельным процессом и получает один ответ ConSeC (`--smoke`).
- **Локальный бандл ConSeC.** На Windows сверка его tokenizer с закреплённым
  эталоном молча пропускается, если бандл не установлен (`ConsecRankerTests`);
  контракт кэша и установщика проверяется без артефактов. macOS-подмножество
  печатает в итоговой строке, выполнена ли сверка (`checked in place` или
  `absent: …`).

Почему раннер — консольная программа, а не xUnit, в репозитории не записано.
Фактически это один процесс: блоки идут последовательно, первое исключение
завершает прогон строкой `FAIL` и кодом `1`, тот же исполняемый файл обслуживает
gated-режимы, а internal-члены открыты через `InternalsVisibleTo`. Правило из
[AGENTS.md](../AGENTS.md): исправление поведения приходит вместе с тестом,
который без исправления падает.

### Gated-режимы тестов (по аргументам CLI)

Gated-режимы пока только для Windows: macOS-подмножество отвергает любой
аргумент, пока provenance бенчмарка не записывает платформу (follow-up
карточки №20 [ROADMAP.md](ROADMAP.md)).

| Аргумент | Что делает |
|---|---|
| `--live` | Живой апскейл Notepad через Magpie |
| `--ocr-image <png> <x> <y>` | Диагностика hover-OCR по изображению |
| `--screen-ocr-image <png>` | Диагностика экранного OCR по изображению |
| `--ranker-benchmark <set.json> <workerBaseDir> [cpu\|npu] [--ranker-cpu-model glossbert\|consec] [--dump <path>] [--ranker-report <path>]` | Бенчмарк выбранного definition-aware ranker через полный продакшн-конвейер |
| `--register-benchmark <set.json> <workerBaseDir> [cpu\|npu] [--ranker-cpu-model glossbert\|consec] [--register-report <path>]` | Сохраняет ли выбранная русская поверхность регистр английского смысла (роадмап №14.2C); набор — `assets/Ranker/ranker-register-benchmark.json`, отчёт schema 3 |
| `--ranker-sentence-benchmark <set.json> <workerBaseDir> [--ranker-cpu-model glossbert\|consec] [--sentence-report <report.json>]` | WSD-only sentence A/B: точность sense GroupId по целям/предложениям/научным и surface-unambiguous срезам, contrast pairs, cold/warm latency, peak RAM и точная provenance; русский/full-text не оценивается. `--output` — синоним `--sentence-report` |
| `--translation-suite <set.jsonl> <workerBaseDir> --system-id <id> --profile direct\|smart --phrase-backend cpu\|vulkan --ranker-cpu-model glossbert\|consec --translation-output <output.jsonl> --translation-report <report.json> [--shard i/n] [--max-cases N] [--phrase-quant q6_k] [--sense-gate-trace <trace.jsonl>] [--phrase-coopmat on\|off] [--phrase-cache-prompt on\|off]` | Production A/B TranslateGemma; `direct` переводит весь segment, `smart` — предложение под focus через реальный словарный/WSD-каркас, а ошибки отдельных реплик остаются в output. Диагностические опции: `--phrase-quant q6_k` (только `direct`) подменяет модель на альтернативный, так же строго закреплённый Q6_K из `tests/AsteriaScale.Tests/AlternativeModelPins.cs` под собственной identity; `--sense-gate-trace` (только `smart`), `--phrase-coopmat` (только с `--phrase-backend vulkan`) и `--phrase-cache-prompt` (только `direct`) описаны в [BENCHMARKING.md](BENCHMARKING.md) |

У `--ranker-benchmark` и `--register-benchmark` по умолчанию `cpu` и
`glossbert`; `--ranker-sentence-benchmark` работает только на CPU, и по
умолчанию у него `glossbert`. Другой профиль указывайте явно.
`--benchmark-only` ставится **перед** флагом режима: тогда smoke-набор не
запускается, а в одном вызове допускается ровно одно семейство бенчмарков. Для
`--translation-suite` он обязателен; остальные бенчмарки без него выполняются
после полного smoke-набора, включая Magpie. Опции translation-suite разбирает
`TranslationSuiteOptions.Parse` (`TranslationSuiteModels.cs`), его `Usage` —
полный список.

Нераспознанный аргумент **завершает прогон ошибкой** (`TestCommandLine.FindUnknownOption`).
Раньше он молча проваливался в обычный smoke-набор, и удалённый gated-режим
(`--smart-live`, `--foundry-live`, `--translation-benchmark` — все удалены вместе
с Qwen-конвейером) печатал зелёный `PASS`, хотя ничего живого не запускалось.
Добавляя режим, добавь его флаги в `TestCommandLine.KnownOptions`.

`workerBaseDir` — каталог, содержащий `Ranker\AsteriaScale.RankerWorker.exe` (удобно сделать junction на bin-выход воркера). Наборы: `assets/Ranker/ranker-benchmark.json` (открытый dev, `134` случая, включая `20` научных) и `ranker-holdout*.json` (замороженные — только финальные подтверждения, каждое открытие фиксируется в [RESEARCH.md](RESEARCH.md)).
Команда завершается с ошибкой, если в наборе меньше 20 случаев, отсутствуют ранжируемые/размеченные решения, точность принятых решений ниже `90 %`, expected-inventory coverage ниже `70 %`, доля принятых размеченных решений ниже `45 %` или end-to-end correct coverage с учётом детерминированных решений ниже `45 %`. Это ручной gate результата (CI-конфигурации в репозитории нет: прогон запускают локально, итог записывают в [RESEARCH.md](RESEARCH.md)), а не дополнительный критерий воздержания модели: runtime по-прежнему использует только замороженный margin `0,25`.

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
(профиль удалён 8 ноября 2026) `120`, ConSeC `131` верная группа; scientific `69/108`, `96/108`, `106/108`;
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
- если локально установлен проверенный четырёхфайловый ConSeC bundle,
  `Models\Consec\` также копируется условно и попадает в output manifest.
  Отсутствие личного CC BY-NC-SA профиля не делает обычную публикацию неполной.
  Условие в csproj Windows-воркера — наличие всех четырёх файлов, и копирует
  их не только publish, но и обычная сборка воркера (`bin/`); macOS-проект
  воркера бандл не копирует никогда;
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

### Python-тесты инструментов

Python-инструменты имеют пять собственных наборов на `unittest` (`71` тест).
Запускай соответствующий после правки скрипта — все пять работают без сети и
без внешних пакетов; единственный тест, которому нужен `pymorphy3`
(лемматизация в `sense-gate-early-exit.py`), без него пропускается:

```powershell
python scripts/test-build-dictionaries.py            # генератор словарей (16)
python tools/test-translation-benchmark.py           # tools/translation-benchmark.py (18)
python tools/test-sense-gate-early-exit.py           # tools/sense-gate-early-exit.py (8)
python tools/tests/test_fetch_benchmark_datasets.py  # tools/fetch-benchmark-datasets.py (11)
```

На macOS те же команды запускаются как `python3 …`. Пятый набор — у
macOS-инструмента аудита переноса; ему нужен `git` в `PATH`:

```bash
python3 tools/macos/test-audit-verbatim-move.py      # tools/macos/audit-verbatim-move.py (18)
```

C#-smoke эти наборы не запускает, но сам вызывает `python` для части `tools/`:
`TranslationSuiteTests` прогоняет `tools/translation-benchmark.py score` на
output C#-runner, `AtomicOutputSafetyTests` — `tools/atomic_output.py`. Без `python` в `PATH`
полный smoke падает. macOS-подмножество вызывает вместо него `python3` (через
него же работают поддельные воркеры `GlossBertDictionarySelectorServiceTests` и
`SmartHoverEngineStringTests`).

`tools/tests/` намеренно без `__init__.py`, поэтому файл запускается напрямую, а
не через `unittest discover`.

## Стиль кода

- C# 14 — версия языка по умолчанию для .NET 10 SDK: `LangVersion` не задан ни в одном `.csproj`, `Directory.Build.props` нет. File-scoped namespaces, `sealed` по умолчанию, records для DTO.
- Комментарии — по-английски и только про «почему» (инвариант, который код не показывает), не про «что делает строка».
- Строковые сравнения — явные: `StringComparer.Ordinal` для ключей, `OrdinalIgnoreCase` для путей/имён файлов.
- Ошибки внешних подсистем не роняют hover-путь: словари, кэш и диагностика деградируют мягко (см. существующие catch-блоки — они узкие и мотивированные).
- Новые зависимости NuGet — только при реальной необходимости; проект намеренно офлайн и локальный.
