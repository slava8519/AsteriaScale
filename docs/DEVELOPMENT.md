# Разработка и сборка

## Требования

| Компонент | Версия | Обязательно |
|---|---|---|
| Windows 11 | build 26100+ (для NPU OCR) | да |
| macOS (Apple Silicon) | 14.0+ (`libonnxruntime.dylib` ORT 1.27.1) | только CPU-воркер (ConSeC), CoreHost, Swift-пакет `macos/AsteriaScale` и macOS-подмножество smoke |
| Command Line Tools (Swift) | Swift 6 (проверено на 6.3.3); Xcode.app может стоять ради сертификата, но `test-swift.sh` и `build-app.sh` берут CLT (`DEVELOPER_DIR`, MACOS.md T33) | для Swift-пакета macOS-приложения (`tools/macos/test-swift.sh`) |
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

На macOS собираются и запускаются только CPU-воркер (ConSeC), CoreHost,
Swift-пакет macOS-приложения и macOS-подмножество smoke-набора (роадмап №20.1
и 20.2, решения M2, M7 и M11 в [MACOS.md](MACOS.md); Swift-пакет — раздел
«Swift-пакет `macos/AsteriaScale`» ниже). Воркер и подмножество — `net10.0`/`osx-arm64`, не
входят в `AsteriaScale.slnx` и компилируют те же исходники по ссылке, а не
копией:

- `src/AsteriaScale.RankerWorker.MacOS` — воркер без Windows ML и без единого
  `Content`: модели читаются на месте через `--model-directory`, поэтому его
  сборка никогда не копирует бандл ConSeC в `bin/`;
- `tests/AsteriaScale.Tests.MacOS` — подмножество того же набора с общим
  `Program.cs`. Windows-only части стоят под `#if WINDOWS`, а ветки `#else`
  объявляют, что не выполнено (`MacSmoke.cs`). Сборка тест-проекта сначала
  собирает macOS-воркер и CoreHost (ссылки только задают порядок сборки); как
  и Windows тест-проект, он не ссылается на ONNX Runtime.

Третий проект, `src/AsteriaScale.CoreHost`, — процесс ядра для
macOS-приложения (решение M11, раздел «CoreHost» ниже). Он платформенно
нейтральный (`net10.0` без RID, как ядро), ссылается только на ядро, тоже не
входит в `AsteriaScale.slnx` и собирается без единого предупреждения.

```bash
dotnet build src/AsteriaScale.CoreHost/AsteriaScale.CoreHost.csproj -c Release   # 0 предупреждений
dotnet build tests/AsteriaScale.Tests.MacOS/AsteriaScale.Tests.MacOS.csproj -c Release
bash tools/macos/run-macos-subset.sh
```

`tools/macos/run-macos-subset.sh` ничего не собирает. Каждый прогон он
запускает в новом каталоге `W` из `mktemp -d` (вне клона): текущий каталог —
`W/cwd`, `TMPDIR=W/tmp`, а `HOME` и `CFFIXED_USER_HOME` указывают на `W/home`
с пустой `Library/Application Support`. LocalApplicationData в .NET на
macOS берётся не из `HOME`, а через NSSearchPath: из `CFFIXED_USER_HOME`, а
когда она не задана, — из настоящего домашнего каталога учётной записи, что
бы ни было в `HOME` (за `HOME` следует только `UserProfile`, как и
POSIX-инструменты). Поэтому заданы обе переменные: один `HOME` .NET не
изолирует, и только с обеими прогон не пишет в настоящую `~/Library`. После
прогона скрипт перечисляет всё, что осталось в `cwd`, `home` (кроме двух
созданных им пустых папок) и `tmp`;
запись, которую не называет `--allow <путь относительно W>` (сама, папкой
выше или папкой на пути к ней), даёт код `9`. До и после прогонов он
перечисляет отчёты о сбоях владельца в `~/Library/Logs/DiagnosticReports`
через `tools/macos/crash-reports.py` (проверка G-crash; папка только
читается; её путь берётся из базы пользователей, а не из `HOME`, потому что
ReportCrash пишет туда при любом `HOME` упавшего процесса) и при появлении
нового отчёта завершается кодом `10`. Отчёт считается, если с
`AsteriaScale`, `dotnet`, `Python`, `python3` или `swift` начинается его имя
либо записанные в нём имя упавшего процесса (`procName`), его родителя
(`parentProc`) или убившего его процесса (`termination.byProc`): дочерний
процесс с системным именем (`sleep`, запущенный `swiftpm-testing-helper`)
тоже попадает в счёт. Отчёты посторонних процессов (дети `launchd`, другие
приложения) не считаются, поэтому сбой чужой программы проверку не роняет;
отчёт, который не разбирается как JSON, учитывается только по имени.
`--repeat N` повторяет прогон `N` раз, каждый раз в новом каталоге, и
останавливается на первом сбое, сохраняя его каталог; каталог чистого
прогона удаляется. `--empty-home` создаёт домашний каталог без `Library`. Остальные коды: `2` — ошибка аргументов, нет собранного
apphost или не Apple Silicon; любой другой — код самого подмножества (`1`
при `FAIL`). Прямой запуск apphost или `dotnet run` идут с настоящими
домашним каталогом и `TMPDIR` и без этих проверок, и о таком прогоне не
сообщают.

Подмножество запускается только через apphost
(`bin/Release/net10.0/osx-arm64/AsteriaScale.Tests`). Ядро по умолчанию ищет
словари и морфологию рядом с процессом (`CoreDataLayout`, поставляемая
половина), а под `dotnet AsteriaScale.Tests.dll` это каталог установки
`dotnet`, где морфологический лексикон молча оказался бы пустым, поэтому такой
запуск завершается ошибкой. Любой аргумент тоже ошибка:
`MacSmoke.Begin` отвергает его до первого теста, поэтому gated-режимы и
бенчмарки на macOS недостижимы, пока provenance бенчмарка не различает
платформу. Их код при этом компилируется; под `#if WINDOWS` вынесены только
`--live` и режимы OCR-изображений.

Из `57` общих регистраций выполняются `46`. Остальные `11` Windows-only
объявлены на месте через `MacSmoke.Skip` с названием API, которое их требует
(WPF-сборка, Magpie, Windows PowerShell, `cmd.exe`), и
печатаются строками `skip`. Три Windows-only проверки внутри выполняемых
тестов объявлены через `MacSmoke.Part` и печатаются строками `note`; одна из
них — бюджет управляемой памяти, который один тест, «Local dictionary
lookup», печатает дважды (замеры `Layered dictionaries` и `Aligned sense
metadata`), поэтому строк `note` четыре. Затем идут семь проверок только
для macOS (префикс `macOS: `):

- паритет с Windows-проектами: пакеты и их версии, общие файлы воркера,
  данные тест-проекта, отсутствие `Content`/`None`/`EmbeddedResource` у
  macOS-воркера, отсутствие ONNX Runtime, воркера и CoreHost в выходе тестов,
  регистрации общего `Program.cs` (см. ниже), а также проект CoreHost
  (`net10.0` без RID, `IsAotCompatible` и анализаторы как ошибки, ссылка
  только на ядро, данные `Dictionaries`/`Morphology` — те же строки, что у
  WPF-проекта, нет в `AsteriaScale.slnx` и ни в одном Windows-проекте) и
  единственный помощник, создающий symlink в `tests/`
  (`CoreHostFixtures.CreateLinkToEmptyDirectory`; в его файле нет имён
  каталогов моделей), а также отсутствие слова «ядро» в исходниках CoreHost
  и в Swift-исходниках и каталогах строк macOS-приложения (решение M15);
- отказ воркера от NPU без перехода на CPU: `--provider npu --smoke` и
  `--smoke` с провайдером по умолчанию дают `npu_provider_unavailable` и код
  выхода `1`;
- ответ воркера на запрос ConSeC: `--smoke` с бандлом, прочитанным на месте из
  `src/AsteriaScale.RankerWorker/Models`, проверяет выбор, провайдер, pipeline
  и точную identity модели;
- «CoreHost protocol conformance», «CoreHost lifecycle and stdio isolation»,
  «CoreHost fault containment» и «CoreHost answers a hover through Core» —
  CoreHost снаружи, как его видит приложение (раздел «CoreHost» ниже); первые
  три занимают около 5 с, четвёртая, с настоящим ядром, наблюдениями Apple
  Vision и бандлом ConSeC, — около 23 с.

Эталонный прогон: `53` строки `ok`, `11` — `skip`, `4` — `note`, последняя
строка:

```text
PASS (macOS subset): 46/57 shared smoke tests + 7 macOS checks; SKIPPED: 11 Windows-only; 3 Windows-only parts not run; ConSeC real bundle: checked in place: tokenizer length+SHA-256 pin and 2 tensor goldens; ConSeC worker smoke: checked in place; CoreHost smart hover: checked in place. Not the full Windows smoke suite.
```

Это результат macOS-подмножества, а не smoke-набора, и сообщать о нём нужно
именно так. `MacSmoke.Summarize` печатает строку `PASS`, только если
выполненные и объявленные регистрации вместе дают ровно `57` с
неповторяющимися именами, прошли все семь macOS-проверок
(`MacSmoke.MacOnlyRegistrationCount`), достигнуты все три объявленные части и
записаны все три статуса ConSeC. `checked in place` значит,
что токенизатор сверен с закреплёнными длиной и SHA-256 и двумя
tensor-эталонами, воркер ответил на `--smoke`, а CoreHost через ядро
ранжировал умное наведение на ConSeC CPU с точной identity бандла. Без бандла
все три статуса начинаются с `absent: ` и называют отсутствующий путь; прогон
остаётся зелёным, но строка это показывает.

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

Первым оператором прогона `MacSmoke.Begin` (только macOS, в Windows-сборку не
компилируется) задаёт `CoreDataLayout.Configure(CoreDataLayout.Resolve(
Environment.ProcessPath, AppContext.BaseDirectory, ""))`: словари и морфология
по-прежнему берутся рядом с apphost, а пользовательская половина (настройки,
«Мой словарь», история, кэши, журнал наведения, модели TranslateGemma) — явная
ошибка. Общий тест, который дойдёт до пути по умолчанию, падает с русским
`InvalidOperationException`, а не пишет в домашний каталог; журнал
`hover-translation.log` не пишется вовсе; тесты передают свои каталоги во
временном корне прогона. Поэтому прогон, в том числе с `--empty-home`, не
оставляет ни одной записи в `cwd`, `home` и `tmp`, и команда выше обходится без
`--allow`. Раньше без папки `Library/Application Support` .NET возвращал пустой
LocalApplicationData, и журнал оказывался в текущем каталоге
(`cwd/AsteriaScale/hover-translation.log`); теперь пустой или относительный
LocalApplicationData — явная ошибка и на Windows. Папку `Dictionaries` прогон
не создаёт и пользовательские слои `eng-rus*`/`jpn-rus*` оттуда не читает.

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

#### CoreHost

`src/AsteriaScale.CoreHost` — ядро отдельным процессом (решение M11):
Swift-приложение запускает его в собственной группе процессов и говорит с ним
по протоколу JSONL v1 через stdin/stdout. Разработческая сборка —
framework-dependent apphost
`src/AsteriaScale.CoreHost/bin/Release/net10.0/AsteriaScale.CoreHost`; рядом с
ним сборка кладёт `Dictionaries/` и `Morphology/` (те же элементы данных, что
у WPF-проекта), это его каталог данных. Собственный код CoreHost
NativeAOT-совместим: весь JSON — через source generation, а анализаторы trim,
AOT и single-file — ошибки сборки. Запускают CoreHost проверки подмножества и
приложение; запуск с настоящим домашним каталогом — только владелец.

**Аргументы запуска.** Единственный источник —
`tests/AsteriaScale.Tests.MacOS/CoreHost/launch-args-v1.txt`: его читают
проверка C# и Swift-тест `launchArgumentsMatchGolden`. Все пути —
абсолютные, иначе код `2`.

| Аргумент | Вид | Значение |
|---|---|---|
| `--data-root` | обязательный | каталог с `Dictionaries/` и `Morphology/` |
| `--user-data` | обязательный | настройки, «Мой словарь», история, по умолчанию и кэши; блокировка `Runtime/corehost.lock` |
| `--log-dir` | обязательный | журнал наведения ядра `hover-translation.log` |
| `--cache-dir` | необязательный | три кэша; без него — `--user-data` |
| `--ranker-worker`, `--ranker-models` | необязательные, только вместе | исполняемый файл воркера ранжировщика и каталог его моделей (воркер читает бандл на месте; symlink он отвергает); без них умный словарь сообщает `worker_missing`, а не ранжирует чем-то другим |
| `--enable-test-commands` | тестовый | включает команды `test.*`; `core.hello` сообщает `testCommands: true`, и приложение такой процесс не принимает |
| `--drain-timeout-ms` | тестовый | срок упорядоченного завершения вместо 10 с; только вместе с `--enable-test-commands` |
| `--phrase-models`, `--phrase-runtime` | зарезервированы | код `2` с «зарезервировано для 20.5» |

Любое другое имя (в том числе `--no-rehome`, `--log-file` и `--protocol` из
прототипов), повтор аргумента и аргумент без значения — тоже код `2`;
сообщение в stderr перечисляет допустимые имена. С ошибкой в аргументах
CoreHost ничего не создаёт.

**Запуск.** После разбора аргументов протокол переходит на дубликаты fd 0 и 1
с close-on-exec, fd 0 становится `/dev/null`, а fd 1 — копией stderr
(только `dup`, `dup2` и `ioctl(FIOCLEX)`: у вариадического `fcntl` на Apple
arm64 третий аргумент передаётся через стек, и фиксированная сигнатура
P/Invoke его портит). Поэтому дочерний процесс, запущенный без
перенаправления stdin (llama-server), получает `/dev/null`, а протокольные
дескрипторы не наследуются. Первая операция с ядром — `CoreDataLayout.Configure`
с корнями из аргументов; культура — `ru-RU`. Затем CoreHost берёт блокировку
`<user-data>/Runtime/corehost.lock` (advisory `flock`, её снимает ядро ОС,
когда процесс умирает): второй CoreHost на тех же данных, в том числе через
symlink, завершается кодом `7`, ничего там не прочитав и не записав.
`core.hello` сообщает `blockedSignals` — какие из SIGHUP, SIGINT, SIGTERM и
SIGCHLD заблокированы в унаследованной маске сигналов: запускающий обязан
передавать пустую маску.

**Протокол v1, кратко.** Запрос — `{"version":1,"id":N,"command":"…","payload":{…}}`,
ответ — `{"version":1,"id":N,"ok":true,"result":{…}}` или
`{"version":1,"id":N,"ok":false,"error":{"code":"…","message":"…","detail":"…"}}`,
событие — `{"version":1,"event":"…","ref":N,"payload":{…}}`. Номера запросов
строго растут; на каждый принятый запрос — ровно один ответ, события запроса —
только до него; stdout — только протокол, stderr — только строки
`[corehost] …`. Нарушение кадра (не JSON, не UTF-8, пустая строка, кадр
больше 1 МиБ, неверный номер или версия) — событие `core.fault` и код `3`.
`message` — русский текст для пользователя без слова «ядро» (M15), `detail` —
английский текст для журнала. Байты протокола закреплены эталоном
`tests/AsteriaScale.Tests.MacOS/CoreHost/protocol-v1.golden.jsonl` (pid, пути,
версии и тайминги при сверке подменяются); тот же файл разбирает и проигрывает
через своё соединение набор Swift. Команды — в таблице ниже;
`models.install`, `models.verify` и `phrase.variants` (20.5) отвечают
`not_implemented`, любая другая — `unknown_command`.

**Команды v1.** Управляющие команды отвечают сразу из снимков в памяти и не
считаются в лимит `busy`; наведение — одно за раз, новое заменяет текущее
(`superseded`); остальные идут в пуле потоков. Отмена: немедленная — ответ
`cancelled` сразу, работа доигрывает сама; кооперативная — сигнал, ответ даёт
сама работа; неотменяемая — всё, что пишет на диск, доигрывается до конца.

| Команда | Линия, отмена | Запрос → ответ |
|---|---|---|
| `core.hello` | управляющая | `{client, protocolMin, protocolMax}` → версии, `build`, pid и pgid, `processGroupLeader`, `testCommands`, `blockedSignals`, пути, возможности платформы; до него любой запрос — `hello_required` |
| `core.ping` | управляющая (через пул потоков) | `{}` → `{uptimeMs, outstanding, gcHeapBytes, workingSetBytes}` |
| `request.cancel` | управляющая | `{target}` → `{cancelled, reason?}` |
| `core.shutdown` | управляющая | `{}` → `{}` после упорядоченного завершения |
| `core.init` | пул, неотменяемая | `{}` → `{settings, revision, dictionary{entries, sources, version}, morphologyEntries, loadMs, warmupLookupMs, firstRun?}`; один раз за процесс, до его ответа остальные команды ниже, кроме `ocr.select`, — `not_ready` |
| `settings.get` | управляющая | `{}` → `{settings, revision}` |
| `settings.update` | пул, неотменяемая | `{baseRevision, patch}` → `{settings, revision}`; встаёт в очередь изменений при приёме; `conflict`, `invalid_settings`, `unsupported_on_platform`, `settings_save_failed` |
| `models.status` | управляющая | `{}` → `{dictionary{loaded, entries, sources, version, morphologyEntries, senseMetadataKeys}, sense{inUse, provider, model, keepWarm, running, pid?, activeProvider?, modelVersion?, error?}, phrase{available, requested, warmedUp?, status?}}` |
| `models.warmUp` | пул, кооперативная | `{targets: ["dictionary", "sense"]}` (одна или обе) → снимок `models.status`; грузит то, что нужно настройкам после изменений, принятых раньше; ошибка воркера — её код |
| `models.unload` | пул, неотменяемая | `{}` → снимок; после изменений настроек, принятых раньше, — проход выгрузки ядра по запросу хоста |
| `hover.resolve` | наведение, немедленная | `{mode?, source{kind: "selection", text, hoveredWord, characterIndex, sentenceStable?, speakerName?}, game?{key, displayName}}` или `{mode, source{kind: "observations", …}, game?}` (источник наблюдений ниже; `mode` для него обязателен) → у наблюдений сначала событие `hover.loading`, затем у обоих источников `hover.primary` (`hover.phrase` — с 20.5), затем `{outcome: completed, empty или failed, timings}`; `hover_disabled`, если перевод выключен; `bad_request`, если `mode` не совпадает с активным режимом (режим меняет только `settings.update`), и при приёме — при `game.key`, который ядро отбрасывает или нормализует иначе при втором проходе (то же правило, что у `speaker.set`, MACOS.md T32); `stale_frame` («Кадр на экране изменился.») — ноль наблюдений, а записи кэша кадра с этими `display.id`, границами и отпечатком нет: ответ приходит до ядра, без событий и без статуса, приложение повторяет запрос с полными наблюдениями |
| `ocr.plan` | пул, немедленная | `{display{id, width, height}, cursor{x, y}}` → `{mode, captureBounds{x, y, width, height}, observations, cachedFingerprint?}`; ждёт изменений настроек, принятых раньше (не очереди наведений), поэтому `mode` — тот, с которым сверится следующий `hover.resolve`; «Словарь»: `OcrGeometry.CalculateCaptureBounds(…, 900, 340)` (900×340 — константа CoreHost со ссылкой на `WindowsAiOcrService`, T39), `observations: 1`; «Умный словарь»: `CalculateSentenceCaptureBounds` (до 2400×720) с границами записи кэша того же `display.id`, пока курсор в её зоне повторного использования, `observations: 2`, а `cachedFingerprint` — отпечаток записи, только если границы плана равны её границам и ей не больше 30 с; до `core.init` — `not_ready`, при выключенном переводе — `hover_disabled` |
| `ocr.select` | пул, немедленная; считается в лимите `busy`; нужен `core.hello`, но не `core.init` | `{mode, source{kind: "observations", …}}` (в «Словаре» одно наблюдение, в «Умном словаре» одно или два, без `cachedFingerprint`) → `{selection: {text, hoveredWord, characterIndex, sentenceStable, speakerName?} или null, lines: [декодированные строки наблюдения 1]}`; без состояния, кэша, перевода и истории: «Словарь» — `OcrGeometry.SelectNearPoint`, «Умный словарь» — `OcrTextRules.StabilizeSentenceContext` двух наблюдений или одного против ничего (`StabilizeSentenceContext(first, null)`: всегда неустойчиво, а говорящего первого наблюдения ядро не снимает) |
| `hover.displayed` | пул, неотменяемая | `{hoverId, part: primary или phrase}` → `{recorded}` |
| `userDictionary.list` | пул, немедленная | `{}` → `{entries: [{key, translation, language}]}` |
| `userDictionary.save`, `userDictionary.remove` | пул, неотменяемая | `{key, translation}` → `{key}`, `{key}` → `{removed}`; затем словари перечитываются; `invalid_entry` |
| `history.list`, `history.exportAnki` | пул, немедленная | `{}` → `{entries: [{source, translation, engine, context, language, recordedAt}]}`, `{}` → `{tsv, count}` (файл пишет приложение) |
| `history.clear` | пул, неотменяемая | `{}` → `{cleared}` |
| `speaker.set` | пул, неотменяемая | `{game{key, displayName}, observedName, name, gender}` → `{speaker{name, gender, gameKey, observedName}}`; `observedName` — написание из `speaker.observedName` карточки, оно берётся как есть (только нормализуется) и повторно не разрешается в отображаемое имя; `bad_request` — при неизвестном `gender`, пустом или длиннее 48 знаков `observedName` или `name` и при `game.key`, который ядро отбрасывает (не `path:` и не `process:`) или нормализует иначе при втором проходе (`process:x.exe.exe` теряет по `.exe` за проход, `path:` с пробелом перед закрывающей кавычкой — этот пробел); встаёт в ту же очередь изменений при приёме; транзакционен, как `settings.update` (`settings_save_failed`); ревизию настроек не меняет (род говорящего не входит в `settings`), поэтому ожидающий `settings.update` клиента не получает `conflict` |

**Наблюдения Apple Vision** (20.3, MACOS.md T18–T20, T27, T28). Источник
`{kind: "observations", display{id, width, height}, captureBounds{x, y,
width, height}, cursor{x, y}, observations: [{fingerprint, lines: [{text,
confidence, words: [{text, left, top, right, bottom}]}]}],
cachedFingerprint?}`. `display`, `cursor` и `captureBounds` — целые пиксели
дисплея под курсором (начало — его левый верхний угол, y вниз), рамки слов —
пиксели кадра, равного `captureBounds` 1:1. Пределы (нарушение —
`bad_request` с английским `detail`): `display.id` 0..4294967295, ширина и
высота дисплея 1..32768; `captureBounds` не меньше 1×1 и целиком внутри
дисплея; `cursor` внутри дисплея (0 ≤ x < ширины), но не обязательно внутри
`captureBounds`; у наведения «Словарь» — ровно одно наблюдение, «Умный
словарь» — два или ноль вместе с `cachedFingerprint` (и `cachedFingerprint`
только тогда); до 256 строк в наблюдении, `text` строки до 4000 UTF-16,
`confidence` — конечное число 0..1; от 1 до 256 слов в строке и не больше
4096 в наблюдении, `text` слова 1..256; края рамок — конечные числа в
пределах `[-1, W+1]×[-1, H+1]`, где W×H — размер кадра; элемент `null` в
`observations`, `lines` или `words` — тоже `bad_request` при приёме (строгие
типы протокола отвергают `null` только в полях). Пределы ограничивают работу
запроса, а не его длину: запрос в пределах может оказаться длиннее 1 МиБ, а
это `frame_too_long` и конец соединения, поэтому приложение не отправляет
наблюдения больше 900 КиБ (DoD 20.3). Вырожденная рамка
(`right <= left` или `bottom <= top`) не отвергается, а отбрасывается: декодер
CoreHost (`Host/Observations.cs`, `ObservationDecoder`) повторяет фильтры
`ExtractRecognition` из `WindowsAiOcrService` — `Normalize` для слова,
`NormalizeLong(…, 1200)` для строки, строка без слов или с пустым текстом
отбрасывается, уверенность строки переходит к её словам; правила остались в
CoreHost, чтобы не менять `Core.dll` (T28). **Отпечаток кадра** —
`OcrTextRules.ComputeFrameFingerprint` его пикселей (первые 8 байт SHA-256,
прочитанные как little-endian `ulong`); в протоколе это ровно 16 строчных
шестнадцатеричных цифр этого `ulong` (`ToString("x16")`, то есть первые 8
байт хэша в обратном порядке: у пустого входа хэш начинается с
`e3b0c44298fc1c14`, отпечаток — `141cfc9842c4b0e3`). Закреплённые значения
для заданных байтов — `tests/AsteriaScale.Tests.MacOS/CoreHost/frame-fingerprint-v1.txt`
(формат описан в его заголовке; его читают C#-проверка и тест Swift
`FrameFingerprintTests`, который считает SHA-256 через CryptoKit приложения).
Наведение по наблюдениям повторяет порядок
`WindowsAiOcrService.RecognizeNearCursorAsync`: «Словарь» —
`OcrGeometry.SelectNearPoint` по единственному наблюдению, без кэша; «Умный
словарь» — сначала кэш кадра CoreHost (одна запись: `display.id`, границы,
отпечаток, время, распознавание и подтверждённый говорящий; срок 30 с; при
нуле наблюдений — запись, найденная и закреплённая до ядра, при двух —
запись с отпечатком наблюдения 1) через `ApplyConfirmedFrameGates`, иначе
`SelectNearPoint` по обоим наблюдениям и `StabilizeSentenceContext`, и
устойчивый результат записывается в кэш с выбранным наблюдением. Курсор
переводится в пиксели кадра целым вычитанием, как в
`SelectRecognitionNearCursor`. Событие `hover.loading` — `{source,
engineLabel, speaker?}`, то, что показывает карточка загрузки Windows
(слово под курсором в «Умном словаре», распознанная строка в «Словаре»),
только у наведений по наблюдениям и только для текущего поколения показа;
у наведения по выделению события прежние, как в 20.2.

`hover.primary` несёт `source`, `translation`, `language`, `isPhrase`,
`found`, `confidence`, типизированное происхождение `provenance`
(`dictionary`, `heuristic`, `tatoeba`, `resultCache`, `ranker`,
`rankerAbstained`, `rankerUnavailable`; у трёх последних — `senseProvider`),
русскую метку `engineLabel` для журнала и истории (приложение её не
разбирает), `context` и `speaker` — `{name, gender, gameKey?, observedName?}`,
если у текста есть говорящий; говорящего передаёт только наведение в умном
режиме, в словарном `speaker` нет даже при `source.speakerName`. `name` —
имя, которое показывает карточка (исправленное пользователем, если оно
есть), `gender` — `unknown`, `masculine`, `feminine` или `ignore`, а
`observedName` — написание, которое увидел источник текста: CoreHost
запоминает его до того, как ядро заменит его отображаемым именем, и
`speaker.set` принимает его обратно как есть, поэтому повторная правка с
карточки, где уже исправленное имя, доходит до записи, которую читает
следующее наведение.

**Ядро за протоколом.** `core.init` сначала проверяет данные и до того
ничего не пишет: ноль записей морфологии (`Morphology/ru-morph.tsv.gz`) —
`morphology_missing`, нет `eng-rus*.tsv.gz` или `jpn-rus*.tsv.gz` в
`Dictionaries/` — `dictionary_missing`. Затем он читает `settings.json`,
загружает словари с одним пробным поиском и запускает `HoverCoordinator`
ядра с метками Apple Vision (`APPLE VISION  •  СЛОВАРЬ` и так далее, MACOS.md
T4) и без модели фраз до 20.5. **Первый запуск** (нет `settings.json`):
CoreHost явно записывает `smartDictionaryRankerModel=consec` и
`smartDictionaryBackend=cpu`, перевод при наведении остаётся выключенным, как
на Windows (MACOS.md T7); ответ `core.init` содержит `firstRun`, а статус —
«Первый запуск на macOS: модель значений — ConSeC (CPU)». Значения по
умолчанию `AppSettings` (общий код Windows-сборки) не меняются. `settings.json`
пишет только CoreHost: изменения (`settings.update`, `speaker.set`) встают в
очередь в момент приёма запроса и применяются в порядке поступления, поэтому
наведение, `models.warmUp`, `models.unload` и следующее изменение, принятые
после них, их уже видят, даже если пул потоков начнёт работу изменения
позже; ответ приходит после записи на диск. `settings.update`
транзакционен: сначала записывается изменённая копия настроек, и только
после успешной записи правка доходит до ядра, ревизии и снимка; при
`settings_save_failed` значения, ревизия и состояние перевода (включён ли,
режим, прогрев) остаются прежними, как и снимок, который приложение
показывает после отказа. `speaker.set` транзакционен так же: выбор сначала
вносится в копию списка родов (по правилу ядра: под написанием OCR и, если
имя исправлено, ещё под исправленным именем) и записывается в
`settings.json`, и только после успешной записи список заменяется в памяти, а
ядро сообщает «сохранён»; его собственный вызов находит выбор уже
сохранённым и своей записи не ставит. Это верно для любого `game.key`,
который `speaker.set` принимает; исключение — `settings.json`, вручную
дополненный тем же говорящим под ключом, который ядро укорачивает при каждом
проходе: тогда ядро может записать файл само, и CoreHost пишет об этом
строку в stderr. При `settings_save_failed` род в памяти, статус и файл остаются
прежними, и отложенной записи нет. Ничего не подменяется: `npu` и
`translategemma-4b` — `unsupported_on_platform`, другое неизвестное значение —
`invalid_settings`. Выделение (`selection`) проходит те же правила текста,
что и OCR (`OcrTextRules`: схлопывание пробелов, починка смешанных алфавитов,
извлечение предложения и проверка его полноты); `characterIndex` — в
UTF-16-единицах присланного текста. В историю попадает только то, о показе
чего приложение сообщило `hover.displayed` (CoreHost помнит восемь последних
наведений). Свободный текст статусов самого ядра приходит событием `status`
с `area: "translation"` без `state`: приложение показывает его только в окне
«Проверка перевода» и журнале, а не в меню и не в оверлее.

**Ошибки модели значений.** Код воркера или селектора (`worker_missing`,
`model_directory_missing`, `consec_bundle_missing`, `consec_manifest_missing`,
`consec_artifact_path_unsafe` и другие) передаётся без изменений; `message` —
русская фраза, называющая этот код, без путей (`Host/RankerErrorText.cs`;
неизвестный код — «Ошибка модели значений (<код>)»), `detail` — исходный текст
с путями для журнала. Так же выглядит `models.status.sense.error`, пока
следующая транзакция воркера не пройдёт.

**Завершение и коды выхода.**

| Код | Когда |
|---|---|
| `0` | `core.shutdown` или SIGTERM, работа завершилась в срок |
| `2` | ошибка аргументов или не macOS |
| `3` | приложение нарушило протокол (перед выходом — `core.fault`) |
| `4` | приложения больше нет: EOF stdin без `core.shutdown` или EPIPE на stdout |
| `5` | приложение перестало читать: очередь ответов превысила 16 МиБ |
| `6` | упорядоченное завершение не уложилось в срок (`core.fault shutdown_timeout`) |
| `7` | другой CoreHost уже работает с этим `--user-data` |
| `70` | внутренняя ошибка |

`core.shutdown` и SIGTERM запускают одно и то же упорядоченное завершение,
открыт stdin или нет: новые запросы получают `shutting_down`, запросы с
немедленной отменой (наведение, чтение) сразу получают ответ, кооперативные —
сигнал отмены, а работа, которую прерывать нельзя, дожидается до 10 с;
затем освобождаются службы, `core.shutdown` получает ответ, код `0`. Если
работа за срок не закончилась — ответ `shutdown_timeout` на `core.shutdown`,
`core.fault shutdown_timeout` и код `6`. EOF stdin после `core.shutdown` или
во время такого завершения его исхода не меняет. EOF stdin без
`core.shutdown` значит, что приложения больше нет: все запросы отменяются, и
не позже чем через 500 мс CoreHost выходит с кодом `4`. Перед любым выходом
CoreHost посылает SIGKILL остальным процессам своей группы, если он её лидер
(`proc_listpgrppids`), а иначе — своим потомкам (`proc_listchildpids`):
группа пустеет не позже чем через 1 с после EOF.

**Окружение.** Проверки подмножества (и приложение) запускают CoreHost через
`posix_spawn` в собственной группе, с `CLOEXEC_DEFAULT`, пустой маской
сигналов, `HOME`/`CFFIXED_USER_HOME` в своём каталоге с пустой
`Library/Application Support`, собственным `TMPDIR`,
`DOTNET_EnableDiagnostics=0` (иначе убитый .NET-процесс оставляет в `TMPDIR`
FIFO отладчика `clr-debug-pipe-*`) и пустым текущим каталогом. Ни одна
автоматическая проверка не роняет процесс через abort или сигнал ошибки
(`Environment.FailFast`, необработанное исключение, SIGABRT, SIGSEGV,
ловушка Swift вроде индекса за границей): ReportCrash пишет отчёт в настоящую
`~/Library/Logs/DiagnosticReports` при любом `HOME`. Поэтому тест Swift
сначала проверяет размер через `#require` и только потом обращается по
индексу: сломанная или мутированная сборка проваливает тест, а не роняет
`swiftpm-testing-helper`. Внезапная смерть имитируется `_exit` (`test.exit`, код `70`) или
SIGKILL, отчётов они не оставляют. Копию системной программы (`/bin/sleep`,
что-либо из `/usr/bin`) вне её места проверки не запускают: вне
запечатанного системного тома она уже не платформенная, macOS считает её
подпись недействительной, и её смерть, даже от SIGKILL самого теста,
оставляет отчёт «SIGKILL (Code Signature Invalid)». Тесту, которому нужен исполняемый файл в своей временной
папке, служит копия `AsteriaTestSleeper` из Swift-пакета.

**Что проверяет подмножество.**

- «CoreHost protocol conformance»: аргументы против `launch-args-v1.txt`
  (отсутствующий обязательный, относительный путь, неизвестное или
  зарезервированное имя — код `2`, и ничего не создано; список допустимых имён
  в сообщении совпадает с файлом; со всеми именами сразу `core.hello`
  возвращает пути), сначала `core.hello`, несовпадение версии, неизвестные и
  зарезервированные команды, лишние, недостающие, `null` и не того типа поля
  (`bad_request`), четыре исхода `request.cancel`, порядок G1–G4 (события до
  ответа; основная карточка до строки фразы, черновик до окончательной; ответ
  заменённого наведения до событий нового; ответ отменённого запроса до ответа
  отмены), одно наведение в движке за раз, неудачное наведение — исход
  `failed` и русский статус, `busy` при 32 незавершённых запросах, число
  отправленных запросов равно числу ответов и эталон
  `protocol-v1.golden.jsonl`. Наведения идут через тестовый движок
  `test.fakeCore`, данные ядра не читаются. Наблюдения Apple Vision без
  ядра: `ocr.select` на раскладке Sakuya равен прямым вызовам
  `OcrGeometry.SelectNearPoint` и `OcrTextRules.StabilizeSentenceContext`
  (одно и два наблюдения, согласные и несогласные таблички, а у разошедшейся
  пары — неустойчивый выбор с говорящим первого наблюдения, как в ядре на
  Windows и в пункте 4 DoD 20.3); правила
  декодера сверяются с вручную собранным `OcrRecognition` по `lines` и
  выбору (две вырожденные рамки под курсором, строка, у которой не осталось
  слов, строка из пробелов, строка в 1299 знаков, обрезанная до 1200,
  схлопнутые пробелы и слово «bank» с пробелами вокруг); каждый предел
  источника на границе принят, а за ней —
  `bad_request` (в том числе рамка `1e999`, за пределами кадра, отпечаток
  не из 16 строчных цифр, неверное число наблюдений для режима и наведение
  без `mode`); `null` вместо наблюдения, строки или слова — `bad_request` и у
  `ocr.select`, и у `hover.resolve`, а CoreHost после этого отвечает дальше;
  `game.key`, который ядро отбрасывает или меняет вторым
  проходом, — `bad_request` у обоих источников; `ocr.select`, задержанный
  `test.delayNextWork`, — единственный незавершённый запрос в `core.ping`
  (он идёт по рабочей линии и считается в лимите `busy`), а при 32
  незавершённых — `busy`; наведение по наблюдениям через тестовый движок —
  `hover.loading` до `hover.primary`, наведение по выделению — только
  `hover.primary`, ноль наблюдений — `stale_frame`. Сторожа копий
  (`VerifyWindowsCopies`) читают `src/AsteriaScale/Services/WindowsAiOcrService.cs`
  как текст: любое изменение `ExtractRecognition` или захвата 900×340 в
  `RecognizeNearCursorAsync` (`: CaptureAround(cursor, 900, 340);`) роняет
  проверку и называет копию в `Host/Observations.cs` (`ObservationDecoder`
  или `ObservationRules.DictionaryCaptureWidth`/`DictionaryCaptureHeight`);
  ещё один читает сам `ObservationDecoder.Decode` и падает, если копия
  потеряла фильтр `ExtractRecognition`: `Normalize` слова снаружи не
  увидеть, потому что `SelectNearPoint` нормализует слово под курсором ещё
  раз; отпечатки кадра из
  `frame-fingerprint-v1.txt` сверяются с `ComputeFrameFingerprint` и с
  перевёрнутыми первыми 8 байтами SHA-256.
- «CoreHost lifecycle and stdio isolation»: лидер группы; `blockedSignals`
  пуст, а при запуске с заблокированным SIGTERM — `["SIGTERM"]`; EOF без
  `core.shutdown` в простое — код `4` быстрее 1 с; единственный ответ на
  `core.shutdown`, после которого завершён и дочерний процесс; SIGTERM при
  открытом stdin — код `0` в пределах срока; `--drain-timeout-ms 300` при
  неотменяемой работе — `core.fault shutdown_timeout` и код `6`; EOF при
  работающих запросе и дочернем процессе — группа пуста и код `4` быстрее 1 с;
  у дочернего процесса ровно `0:/dev/null`, `1` и `2`, и он в той же группе;
  второй CoreHost на тех же данных (через symlink) — код `7`, а
  `settings.json` первого не изменился ни в байтах, ни во времени изменения.
- «CoreHost fault containment»: `test.exit` (код `70`) и SIGKILL клиент
  видит как EOF; дочерний процесс, осиротевший после SIGKILL, живёт в группе
  до `killpg`; зависший запрос упирается в срок клиента; 16 МиБ в stderr не
  задерживают читающего клиента; шесть нарушений кадра — `core.fault` и код
  `3`; строку не-JSON в stdout ловит клиент; клиент, переставший читать, —
  код `5`; закрытый stdout — EPIPE и код `4`.
- «CoreHost answers a hover through Core»: CoreHost с настоящим ядром, без
  тестовых команд (кроме одного запуска, см. конец пункта). `core.init` с
  пустым каталогом данных — `morphology_missing`, с одной морфологией —
  `dictionary_missing`; со всеми данными — `881 924`
  записи из `12` словарей и `154 527` записей морфологии, статус и запись
  настроек первого запуска (ConSeC, CPU, перевод выключен), которые ядро
  читает без `.broken`-копии, а до `core.init` команды ядра — `not_ready`.
  Наведение при выключенном переводе — `hover_disabled`; `npu` и
  `translategemma-4b` — `unsupported_on_platform`, прочие неверные значения —
  `invalid_settings`, устаревшая ревизия — `conflict`; правка, которую не
  удалось записать (папка данных только для чтения), — `settings_save_failed`,
  и после неё `settings.get` отдаёт прежние значения и ревизию,
  `settings.json` не изменился, а наведение по-прежнему `hover_disabled`.
  Наведение сразу после
  включения словарного режима: bank → «берег» с меткой
  `APPLE VISION  •  СЛОВАРЬ`; история — только после `hover.displayed`, по
  разу на часть; экспорт Anki текстом; запись «Моего словаря» видна
  следующему наведению. Род говорящего — в отдельном запуске в умном режиме
  (наведение без воркера оставило бы `worker_missing` в `models.status`
  основного запуска): сохраняется через ядро в `settings.json`; `speaker.set`,
  который не удалось записать (папка данных только для чтения), —
  `settings_save_failed`, и после него следующее наведение получает прежний
  род, статусов «сохранён» и «Не удалось сохранить род говорящего» нет,
  байты и время изменения `settings.json` прежние после отказанного за ним
  `settings.update` и ещё секунду, а после возврата прав тот же выбор
  сохраняется; повторная правка с карточки, которая уже показывает
  исправленное имя, меняет род и под написанием OCR (`observedName`), и под
  исправленным именем, в `settings.json` обе записи, а каждая карточка и
  каждый ответ несут `observedName`; `game.key`, который ядро меняет и при
  втором проходе (`process:…exe.exe`, `process:… .exe`, `path:` с пробелом
  перед кавычкой), — `bad_request`; строки CoreHost о собственной записи
  ядра после `speaker.set` в stderr нет. Ошибки каталога моделей
  (`consec_bundle_missing`, `consec_manifest_missing`,
  `consec_artifact_path_unsafe` через symlink,
  `model_directory_missing`, а без `--ranker-*` — `worker_missing`) — русский
  текст с кодом и без `/`, путь — в `detail`. С бандлом ConSeC, прочитанным на
  месте, `models.status` сообщает точную identity, умное наведение
  ранжируется на ConSeC CPU (`APPLE VISION  •  CPU-СМЫСЛ`), воркер — в группе
  CoreHost и завершается вместе с ним: после ответа на `core.shutdown` и
  выхода CoreHost его pid должен исчезнуть за 1 с, до того как проверка сама
  убьёт группу (так же проверяется дочерний процесс в проверке дескрипторов);
  без бандла итоговая строка говорит
  `CoreHost smart hover: absent: <путь>`. Ни одна метка и ни один статус не
  содержат «NPU OCR» или «VULKAN». Наблюдения Apple Vision — в своём запуске
  (умные наведения без воркера; журнал наведений в каталоге журнала может
  содержать только `worker_missing`, проверка его читает и убирает): до
  `core.init` `ocr.plan` и наведение — `not_ready`, а `ocr.select` работает;
  при выключенном переводе `ocr.plan` — `hover_disabled`, а неверный дисплей
  или курсор вне дисплея — `bad_request` и тогда (каждый предел на шаг за
  границей); границы плана в
  «Словаре» равны `OcrGeometry.CalculateCaptureBounds(…, 900, 340)`, в том
  числе у края дисплея и на дисплее меньше 900×340; наведение по одному
  наблюдению — `hover.loading` с распознанной строкой до `hover.primary`
  bank → «берег» с меткой `APPLE VISION  •  СЛОВАРЬ`, в истории оно только
  после `hover.displayed`. В «Умном словаре» каждый результат сверяется с
  прямыми вызовами ядра на тех же записях: кадры, которые разошлись (в
  первом нет таблички с именем, во втором под курсором другое предложение),
  дают неустойчивый результат без говорящего и ничего не кэшируют, а та же
  пара с табличкой в первом кадре — неустойчивый результат с её говорящим
  на карточке (так делает ядро и на Windows, пункт 4 DoD 20.3; в кэш он не
  попадает); согласные —
  устойчивый с говорящим, и `ocr.plan` возвращает их отпечаток теми же 16
  цифрами в зоне повторного использования (с прежними границами), но не вне
  её и не на другом дисплее того же размера (там и в зоне — новые границы,
  а не закэшированные границы дисплея 7); ноль наблюдений по записи
  кэша, как и два, чьё первое — запись, дают выбор ядра на закэшированном
  кадре через `ApplyConfirmedFrameGates`, а без записи (другой дисплей,
  отпечаток или границы) — `stale_frame` без событий и статусов;
  несовпавшие таблички дают устойчивую запись без подтверждённого
  говорящего, и наведение по ней говорящего не показывает; когда ядро
  выбирает второе наблюдение (первому кадру не хватает третьей строки
  реплики), в кэш идёт оно: `ocr.plan` называет отпечаток второго, а ноль
  наблюдений над словом `people."`, которое есть только во втором кадре, дают выбор
  ядра на его распознавании. Ещё один
  запуск — с тестовыми командами ради `test.delayNextWork`: она
  задерживает на полсекунды работу следующего запроса рабочей линии (у
  изменения настроек — само изменение), как её задержал бы перегруженный
  пул; этот запуск идёт параллельно
  остальным. Отправленные сразу за таким запросом всё равно видят его
  изменение: наведение за включающим `settings.update`,
  `models.warmUp` за переключением в умный режим (грузятся метаданные
  смыслов), наведение за `speaker.set` (род говорящего), второй
  `settings.update` за первым (без `conflict`), `models.unload` за
  выключением перевода (словари выгружены) и `ocr.plan` за переключением в
  «Словарь» (режим «Словарь» и 900×340). Последним шагом этого запуска
  `test.fakeCore` ставит тестовый движок вместо движка наведений ядра
  (`ocr.plan` по-прежнему идёт через ядро) и держит наведение 5 с:
  `ocr.plan`, отправленный сразу за ним, отвечает, пока наведение ещё идёт,
  то есть очереди наведений не ждёт.

После каждого запуска проверки требуют пустую группу процессов, пустые
текущий каталог, `TMPDIR` и каталог журнала, домашний каталог только с пустой
`Library/Application Support`, а в `--user-data` — только блокировку, то, что
положил сам тест, и ровно те файлы, которые ядро должно вести в этом сценарии
(`settings.json`, история, папка «Моего словаря», кэши). Каждое сообщение об ошибке, `core.fault` и статус обязаны
быть русскими и без слова «ядро».

#### Swift-пакет `macos/AsteriaScale`

`macos/AsteriaScale` — SwiftPM-пакет нативного macOS-приложения (решения M2
и M11; Swift 6 в режиме строгой конкурентности, только Command Line Tools, без
Xcode-проекта; в `AsteriaScale.slnx` не входит). В нём библиотека
`AsteriaScaleKit` (Foundation и Darwin, без окна) с набором Swift Testing,
само приложение в строке меню (`Sources/AsteriaScale`: AppKit, SwiftUI,
Carbon, ScreenCaptureKit; раздел «Приложение в строке меню» ниже) и `Bundle/` — шаблон
`Info.plist` и entitlements, с которыми его собирает и подписывает
`tools/macos/build-app.sh` (раздел «Сборка `Asteria Scale.app`» ниже).
Цель `AsteriaScaleVision` (`Sources/AsteriaScaleVision`, 20.3) — само
распознавание Apple Vision; приложение её подключает, ниже — что в ней.
Цель `AsteriaTestSleeper` (`Tests/AsteriaTestSleeper`) — замена
`sleep(1)` только для набора: тест копирует её в свою временную папку, где
копия `/bin/sleep` оставила бы отчёт о сбое; в приложение она не входит. Цель
`AsteriaScaleVisionProbe` (`Tests/AsteriaScaleVisionProbe`) — проба DoD 6 этапа
20.3 (раздел «Проба Vision» ниже): `swift build --build-tests` кладёт её рядом
с тестовым бандлом, в приложение она не входит, набор её не запускает.

Что в `AsteriaScaleKit`:

- **Клиент протокола v1** (`CoreProtocol`, `JSONValue`). Каждый кадр
  разбирается строго, как в C#-клиенте подмножества: JSON без повторяющихся
  полей, без невалидного UTF-8, без `\r` и BOM; версия — целое `1`; в ответе
  только `version`, `id`, `ok` и ровно один объект `result` или `error` (с
  `code` и `message`), в событии — только `version`, `event`, `ref`,
  `payload`. Строка длиннее 4 МиБ — нарушение; запрос длиннее 1 МиБ не
  отправляется (`request_too_large`). Конверт запроса пишется в порядке
  эталона: `version`, `id`, `command`, `payload` (без `payload`, если он пуст).
- **`ChildProcess`**: `posix_spawn` с `SETPGROUP(0)`, `CLOEXEC_DEFAULT`,
  `SETSIGDEF{SIGPIPE, SIGTERM, SIGINT, SIGHUP}` и пустой маской
  `SETSIGMASK`. Поток пула Swift concurrency или dispatch-очереди блокирует
  SIGTERM и SIGCHLD, а `posix_spawn` передаёт маску вызывающего потока, и .NET
  её сохраняет: без пустой маски CoreHost не услышал бы SIGTERM. Игнорирование
  сигнала тоже переживает `exec`, а приложение игнорирует SIGPIPE, SIGTERM и
  SIGINT (последние два оно ловит dispatch-источниками для упорядоченного
  выхода), поэтому помощник получает их обработку по умолчанию. Ещё
  `addchdir_np` и `F_SETNOSIGPIPE` на нашей стороне stdin.
- **`CoreConnection`** — один CoreHost. Строки stdout обрабатываются на одной
  последовательной очереди в порядке прихода (события раньше ответа), запись —
  на отдельной. У каждого запроса свой срок по часам uptime. Нарушение
  протокола, пропущенный срок, неизвестный id, второй ответ и событие после
  ответа завершают всю группу. Отмена задачи вызывающего бросает запрос: его
  ответ потом вычитывается и отбрасывается, а с `cancelInHost` CoreHost
  получает `request.cancel`. Учёт: `sent == answered + drainedAbandoned + lost
  + inFlight`. Порядок завершения (critic M14): отдельный поток ждёт конца
  процесса через `waitid(WNOWAIT)`, пока зомби держит pid и id группы,
  посылает группе SIGKILL, ждёт до 1 с, пока в ней не останется живых, и
  только потом собирает зомби и сообщает о выходе; после сбора группа сигналов
  не получает (её id мог достаться чужой группе). После ответа на
  `core.shutdown` соединение само закрывает stdin.
- **`CoreSupervisor`**: состояния `stopped`, `starting`, `ready`,
  `restarting`, `failed`; строка состояния — «Перевод: готов», «Перевод:
  перезапуск…», «Перевод: недоступен», «Перевод: запуск…», «Перевод:
  остановлен» (M15), причина — в подробностях. При старте он очищает
  осиротевшую группу, затем запускает CoreHost и проверяет `core.hello`:
  версия протокола, pid, лидер собственной группы, нет тестовых команд,
  `build` совпадает с ожидаемой сборкой, SIGTERM и SIGCHLD не заблокированы.
  Неприемлемый hello — сбой, как падение. Затем, если задано, `core.init`:
  отказ CoreHost (`morphology_missing`, `dictionary_missing`) — состояние
  `failed` с его русским текстом и кодом, без перезапусков, потому что данные
  от перезапуска не появятся. Пинг `core.ping` каждые 5 с со сроком 2 с.
  Перезапуск — через 0,5, 1, 2, 4, 8, 16, 30 с; после 60 с стабильной работы
  счёт сбрасывается; пять сбоев за 120 с — `failed` без автоматических
  перезапусков, их сбрасывает «Перезапустить перевод» (`restartNow`).
  `stop()` — `core.shutdown` со сроком 3 с (пропущенный срок завершает
  группу), EOF stdin, SIGTERM группе и через 1 с SIGKILL; он возвращается,
  когда группа мертва, то есть не позже grace + 1 с.
- **`InstanceLock`** — блокировка единственного экземпляра приложения
  (`flock` на `Runtime/app.lock`).
- **`OrphanSweep`** — запись `Runtime/corehost.pid` (pid, pgid, время старта,
  путь) на время работы CoreHost и очистка при следующем старте после
  убитого приложения. Участник записанной группы получает SIGKILL, только если
  его исполняемый файл лежит под разрешённым префиксом и время старта
  сходится: у лидера — точно (иначе pid переиспользован, и группа чужая), у
  остальных — не раньше лидера (id группы не переиспользуется, пока группа
  существует, значит такой участник — потомок нашего CoreHost). Префиксы
  разрешаются через `realpath`, как путь в `proc_pidpath`
  (`/tmp/…` — это `/private/tmp/…`; `resolvingSymlinksInPath` из Foundation
  срезал бы `/private`, и очистка ничего бы не нашла). Запись удаляется в
  любом случае.
- **`AppPaths`** — пути (данные пользователя — `~/Library/Application
  Support/AsteriaScale`, журналы — `~/Library/Logs/AsteriaScale`, MACOS.md T8),
  аргументы CoreHost ровно по `launch-args-v1.txt` (`--data-root`,
  `--user-data`, `--log-dir` и пара `--ranker-worker`/`--ranker-models`;
  `--cache-dir` не передаётся, тестовые и зарезервированные — никогда) и
  окружение CoreHost: `HOME` и `CFFIXED_USER_HOME` — домашний каталог
  приложения, `TMPDIR` — `Runtime/tmp`, `DOTNET_EnableDiagnostics=0`,
  фиксированный `PATH` (MACOS.md T12); рабочий каталог — пустой `Runtime/cwd`.
- **`Hotkeys`, `Hover`, `Strings`**: горячие клавиши по умолчанию ⌥T, ⌥D,
  ⌥V (M14; на macOS 27.0.1 `RegisterEventHotKey` принял все три с кодом `0`,
  реальное нажатие проверяет владелец) и ⌃⌥G — редактор рода говорящего (M20,
  T40; `editSpeaker` — последнее действие `HotkeyAction`, потому что Carbon id
  действия — его место в списке); выделение окна «Проверка перевода»
  (`[слово]` — слово под курсором, индекс — в UTF-16-единицах, как в .NET),
  события `hover.primary` и `hover.phrase`, исходы наведения; все строки
  интерфейса (`RU`) и типизированное происхождение для значка карточки.
- **То, что читает приложение** (`TranslationSettings`, `HoverPresentation`,
  `AppLog`): настройки перевода из `settings.get`, `settings.update` и
  `core.init` с ревизией и `SettingsClient` — единственный путь приложения к
  `settings.json` (на `conflict` он перечитывает ревизию и один раз повторяет
  ту же правку); `ModelsStatus` из `models.status` и строка модели в меню
  (ошибка воркера — русская фраза CoreHost с кодом); сообщения `status` без
  `ref` и их журнал для окна «Проверка перевода»; `HoverReport` — что делают
  оверлей и окно после каждого исхода наведения; журнал приложения `app.log`
  (при 1 МБ — в `app.log.1`); `AppIdentity` — bundle id
  `io.github.slava8519.AsteriaScale` (M13) и `SenseIdentity` — точная
  identity ConSeC, которую требует самопроверка (та же строка, что
  `MacWorkerTests.ExpectedConsecModelVersion`).
- **Распознавание без Vision** (20.3, MACOS.md T18, T24, T27): всё, что
  нужно наведению по Apple Vision, кроме самого Vision, — чистые функции,
  проверенные без оконного сервера. `Frame` — кадр в раскладке
  `frame-fingerprint-v1.txt` (плотные строки BGRA сверху вниз, 4 байта на
  пиксель, без выравнивания строк) и плотность дисплея; кадр, размер которого
  не равен границам захвата, — ошибка `frame_mismatch` («Не удалось
  сопоставить кадр с экраном.»), а не масштабирование. Протокол
  `TextRecognizer` (настоящий — в `AsteriaScaleVision`; `HoverPipeline`
  получает его извне, а его тесты — подмену) и `RecognizerConfiguration` —
  значения T23, которые сверяет
  самопроверка.
  `CaptureGeometry`: из глобальных пунктов Cocoa (`NSEvent.mouseLocation`:
  начало — левый нижний угол главного дисплея, y вверх) и описаний дисплеев
  (`DisplayDescription`: id, рамка `NSScreen.frame` в пунктах, размер в
  пикселях) — дисплей под курсором и курсор в его целых пикселях (начало —
  левый верхний угол, y вниз). Дисплею принадлежат точки `minX <= x < maxX`
  и `minY < y <= maxY` (верхняя строка дисплея в Cocoa — `y == maxY`, а
  `y == minY` — уже верхняя строка дисплея ниже); при перекрытии побеждает
  первый в списке. Округление: пиксель, в котором лежит точка, —
  `floor((x − minX)·sx)`, `floor((maxY − y)·sy)`, затем прижатие внутрь
  дисплея (CoreHost отвергает курсор на ширине или высоте дисплея); вне всех
  дисплеев — `nil`, наведения нет. Обратно — `sourceRect` захвата в пунктах
  дисплея (начало — его левый верхний угол) без округления: каждый край
  делится на плотность точно, нечётный пиксель на 2x — полпункта, а размер
  кадра задаётся в пикселях границ. `VisionGeometry`: нормированный
  четырёхугольник Vision (начало — левый нижний угол картинки, которую видел
  Vision) → каждый угол в пиксели этой картинки (`x = nx·W'`,
  `y = (1 − ny)·H'`), деление на увеличение (2 у кадра с плотностью меньше
  1,5 пикселя на пункт, иначе 1; T24),
  осевая оболочка и обрезка до `[0, W]×[0, H]` кадра; угол не конечен — слово
  не отправляется (CoreHost отверг бы весь запрос), вырожденная после обрезки
  рамка отправляется как есть — её отбрасывает декодер CoreHost, как
  `ExtractRecognition`. `WordSplitter` делит строку на слова по пробельным
  скалярам Unicode (`White_Space`: пробел, табуляция, неразрывный и
  идеографический пробел), с диапазонами в UTF-16, как считает .NET:
  суррогатная пара не разрезается, комбинирующий знак остаётся при букве.
  `FrameFingerprint` — отпечаток и его 16 цифр в протоколе, `FrameFingerprinting`
  — шов, через который его считает SHA-256 из `AsteriaScaleVision` (Kit
  CryptoKit не подключает). `ObservationsSource` и `ObservationFrame`
  кодируются в значения, равные запросам `#5` и `#9` эталона после разбора
  (ключи — по алфавиту): целое значение рамки или уверенности — целым
  литералом, как его пишет System.Text.Json; `CapturePlan` и `OcrSelectResult`
  строго разбирают ответы `ocr.plan` и `ocr.select`, а ответ другой формы —
  `bad_response` («Служба перевода ответила в неверном формате.»).
  `ObservationFrame(recognized:)` не отправляет слово, из-за которого CoreHost
  отверг бы весь запрос: с неконечной рамкой или с текстом вне 1..256 единиц
  UTF-16 (сплошная цепочка без пробелов — отточие, длинный адрес; на Windows
  `ExtractRecognition` такое слово сохраняет); строка остаётся без него, а
  строка без слов не отправляется. Перед отправкой запрос с наблюдениями
  сверяется с пределами CoreHost (`ObservationLimits`: строки, слова, длины)
  и с бюджетом `ObservationBudget` — 900 КиБ закодированной строки запроса
  целиком, с конвертом и переводом строки, меряется в `CoreConnection` на
  настоящей строке с настоящим id; сверх любого — отказ без отправки и без
  id, код `observations_too_large`, «Слишком много текста в области
  распознавания.» (оверлей показывает его сам); наблюдения не урезаются
  никогда. `ObservationClient` — типизированные `ocr.plan`, `ocr.select` и
  наведение по наблюдениям поверх супервизора или одного соединения
  (`CoreRequesting`): события наведения — `HoverUpdate.loading`, затем
  `.primary`, исход `stale_frame` — `HoverOutcome.staleFrame` (оверлей
  скрыт, записка — в окне «Проверка перевода»; одну повторную попытку с
  полными наблюдениями делает `HoverPipeline`).
- **Наведение по экрану (20.3, DoD 7 и 8)** — чистый код над внедряемыми
  швами, поэтому каждая ветка проверяется без оконного сервера, без
  захвата и без Vision. `HoverPipeline` ведёт одно наведение в порядке
  `RecognizeNearCursorAsync`: сначала разрешение (`CapturePermission`; без
  него нет ни `ocr.plan`, ни вызова источника кадра, а исход —
  `screen_capture_denied` с русским сообщением), дисплей под курсором
  (`CaptureGeometry.locate`; вне всех дисплеев — пустой исход), `ocr.plan`
  (границы обязаны лежать в дисплее, иначе `bad_response`), игра
  (`GameResolver`: экземпляр `ScreenSource`, то есть только при включённом
  захвате, без которого наведений по экрану нет вовсе; `NoGameResolver` —
  только в отказанном наведении `hover.observations` самопроверки; названную игру `onGame` отдаёт приложению до первого
  `hover.resolve`, и редактор рода сохраняет выбор под ней), кадр 1 (`FrameSource`; кадр не
  того размера, что границы, — `frame_mismatch`, без масштабирования) и
  его отпечаток (`FrameFingerprinting`); в «Умном словаре», если отпечаток
  равен `cachedFingerprint` плана, — `hover.resolve` без наблюдений и без
  Vision, а на `stale_frame` — ровно одна повторная попытка с полными
  наблюдениями уже снятого кадра 1 (для устаревшего ответа ничего не
  показывается); иначе Vision на кадре 1 и в «Умном словаре» кадр 2, снятый
  только после первого распознавания, и Vision на нём; затем
  `hover.resolve`. Если CoreHost отверг наведение как `bad_request`, один
  новый `ocr.plan` проверяет, не сменился ли режим: режим другой — наведение
  проходит ещё раз с новым планом, тот же — отказ остаётся (английская
  подробность CoreHost не разбирается). Отмена задачи останавливает
  наведение на ближайшем шаге: запрос в полёте (`ocr.plan`,
  `hover.resolve`, повтор, новый план) бросается, и CoreHost получает
  `request.cancel`; локальный шаг (захват, Vision, игра) посередине не
  прерывается, но его результат отбрасывается и больше ничего не
  отправляется — как и его ошибка: захват или распознавание, которые
  упали уже после отмены, заканчивают наведение отменой, а не сообщением
  для места, откуда курсор ушёл. Наблюдения сверх пределов или 900 КиБ — `observations_too_large`,
  без урезания. `Result.steps` перечисляет шаги (их пишет `app.log`).
  `HoverTracker` — чистый автомат на внедряемых часах (`MonotonicClock`),
  правила `HoverTracking` Windows: опрос каждые 90 мс, якорь держится,
  пока курсор в 15 пикселях дисплея (dx² + dy² ≤ 225, другой дисплей —
  всегда далеко), наведение одно на якорь и начинается на первом опросе
  не раньше задержки из настроек (в пределах 350..1500 мс: 700 мс — на
  восьмом опросе, через 720 мс), берёт курсор этого опроса; уход дальше —
  отмена наведения и скрытие карточки, курсор над своим окном только
  скрывает карточку; `mayShow` перед карточкой загрузки и карточкой
  сверяет свежий курсор с курсором наведения (`CursorIsNear`), текущий
  якорь и отсутствие своего окна под курсором (та же сверка — перед
  строкой фразы и перед сообщением конца наведения:
  `HoverReport.overlay(cursorStillThere:)` вместо сообщения для места,
  откуда курсор ушёл, скрывает оверлей). Граница задержки включительна,
  как `>=` на Windows: опрос ровно на задержке начинает наведение. Новый
  режим или новые настройки «Умного словаря» (модель, бэкенд, удержание в
  памяти; `TranslationSettings.hoverChange(from:)`) перезапускают слежение,
  как `SetMode` и `ApplySmartDictionarySettings` сбрасывают его на Windows
  (`HoverTracker.restart(after:)`): наведение в полёте отменяется, якорь
  забывается, и курсор, оставшийся на месте, через задержку начинает
  наведение с новыми настройками; карточку новый режим скрывает сразу, а
  новые настройки — на следующем опросе, который не находит якоря и, как
  при любом новом якоре, скрывает её (так же `OnTimer` на Windows после
  `ResetTracking`). `HoverReport.afterScreenHover` — конец наведения по
  экрану, через который его заканчивает `HoverDriver`: та же сверка
  курсора перед сообщением конца, а карточка, которую сверка не показала,
  отмечается в окне «Проверка перевода» как скрытая («Карточка не показана:
  курсор ушёл с места наведения»), а не как отсутствие слова под курсором.
  `OverlayState` — чей показ на оверлее: наведение «Проверки перевода» или
  ⌥V показывает карточку загрузки сразу, наведение по экрану — ничего, пока
  его `hover.loading` или `hover.primary` не прошли сверку, и до того
  ничем не владеет: скрытие слежением и его конец без сообщения оставляют
  чужую карточку (её убирает её таймер или следующий показ; сообщение
  конца, показанное при курсоре на месте, её заменяет), а карточку
  загрузки не оставляет никто — её снимает и конец более нового наведения;
  карточка остаётся результатом, кто бы ни начал наведение последним.
  Скрытие слежением ещё и обрывает показ этого наведения: его поздние
  карточки и строка фразы уже не показываются (как поколение `HideOverlay`
  на Windows); строка фразы — только под своей карточкой (после её
  8-секундного таймера она возвращает карточку), а карточка без своей
  фразы не показывает чужую. Карточка наведения, у которого на панели
  ничего нет, ставится у его точки. Пока открыт редактор рода говорящего
  (`suspend` … `resume`), панель скрыта и ничего из наведений по экрану не
  показывается — ни карточка загрузки, ни карточка, ни строка фразы, ни
  сообщение конца (на Windows `ShowLoading` и `ShowTranslation` проверяют,
  что редактор закрыт); наведение «Проверки перевода» и ⌥V — просьба самого
  пользователя — показывается. Отказ ⌃⌥G — `notice`: он занимает панель, но
  не номер наведения, так что наведение в полёте показывает то, что
  приходит после него, поверх отказа: наведение по экрану, ещё ничего не
  показавшее, — свою карточку загрузки и карточку, а наведение, чью
  карточку загрузки отказ закрыл (⌥V — она поднята при старте, или по
  экрану — `hover.loading` приходит один раз), — только карточку; его конец без
  своей карточки и скрытие слежением оставляют отказ его таймеру, а строка
  фразы к закрытой отказом карточке не присоединяется (на Windows отказ
  уходит в строку состояния и не трогает ни оверлей, ни наведение).
  `CaptureGate.decide` (T22)
  включает захват, только если нет `--self-test`, `getppid() == 1`, а
  `__CFBundleIdentifier` и bundle id приложения — оба M13; включённое
  решение несёт `Permit`, который создаёт только `decide` (инициализатор
  `fileprivate` в `Capture.swift`), и только с ним строится `ScreenSource`;
  `offReason` даёт самопроверке причину без разрешения. `CaptureFailure` —
  отказы захвата с русскими текстами (код −3801 «пользователь отказал» из
  домена ошибок захвата — то же сообщение, что без разрешения),
  `RecognitionStatus` — строка «Распознавание…» меню, пункт «Разрешить
  запись экрана…» и можно ли следить за курсором: отказ захвата у
  наведения по слежению (`HoverOutcome.refusesCapture`) приравнен к
  отсутствию разрешения до перезапуска приложения, ведь проверка
  разрешения и до него отвечала «да»; `tracks(hoverEnabled:editorOpen:)` —
  слежение только при «готово», включённом переводе и закрытом редакторе
  рода говорящего;
  `Frame.placed`/`Frame.origin` кладут маленький кадр на холст границ.
- **Игра и редактор рода говорящего (20.3, DoD 5; MACOS.md T29–T32)** —
  тоже чистый код. `ProcessArguments` разбирает запись `KERN_PROCARGS2`
  (argc — четыре байта little-endian, путь исполняемого файла, NUL-выравнивание
  после него, ровно argc аргументов — пустой после непустого тоже
  аргумент, а пустые аргументы в начале (пустой argv[0] и пустые сразу за
  ним) от выравнивания не отличить: разбор пропускает их вместе с
  выравниванием, аргументы начинаются на столько же строк позже, и
  столько же первых записей окружения читаются последними аргументами (у процесса Wine
  argv[0] — путь загрузчика, а пустую строку, которую его перезапись
  сдвинула вперёд, восполняют нули в конце), — затем серия NUL и окружение
  до пустой записи или конца
  буфера: Wine переписывает свой argv на месте — `set_process_name` в ntdll
  сдвигает argv[1…] на место argv[0] и обнуляет хвост, а argc ядра
  остаётся прежним, так что последние аргументы читаются пустыми, а
  остаток хвоста — нулями перед окружением); любая
  строка байтов даёт запись или `nil` — без ловушки, без выделения памяти
  по argc, — а `encode` — единственное место, где раскладка записана для
  тестов и самопроверки. `GameIdentity`: окно под курсором — первое спереди
  окно чужого процесса, чьи границы содержат точку, на слое 0 или выше него,
  если владелец — Wine (`proc_pidpath`; спрашивается только про окно выше
  слоя 0 под точкой): полноэкранная бутылка, вероятно, выше слоя 0 (не
  измерено, MACOS.md T29), и окно слоя 0 за ней назвало бы чужую программу;
  прочие окна выше 0 (строка меню, плавающие панели) пропускаются, ниже 0
  (рабочий стол) не берутся никогда (из списка окон
  берутся только pid владельца, слой и границы, имя окна — никогда; y Cocoa
  переворачивается высотой главного дисплея, границы Quartz — `minX <= x <
  maxX`, `minY <= y < maxY`); игра — `path:` исполняемого файла
  (`proc_pidpath`), а у процесса Wine (имя `wine*` или путь внутри
  `CrossOver.app`) — Unix-путь первого аргумента, который называет `.exe`
  полным путём вне программ самого Wine (папка `windows` диска,
  `drive_c/windows` бутылки, `lib/wine`), так что правило не зависит от
  того, на каком месте среди аргументов Wine оставил путь: Windows-путь — через
  `WINEPREFIX` из окружения (`C:` — `<WINEPREFIX>/drive_c`, другие буквы —
  текст ссылки `<WINEPREFIX>/dosdevices/<x>:`, прочитанный `readlink`:
  абсолютный — как есть, относительный — от папки `dosdevices`, `.` и
  `..` применяются по имени; цель ссылки не открывается и не ищется, так
  что буква, указывающая в Документы или на съёмный том, не задевает их
  при наведении, а отсутствующая цель всё равно даёт ключ; папка на месте
  ссылки — сама папка, нет записи — игры нет; бутылку в защищённой папке
  `readlink` всё же читает — отсутствие запроса проверяет владелец,
  ROADMAP, DoD 20.3, пункт 12.4),
  абсолютный Unix-путь (`start.exe /unix …`) — как есть; имя — имя `.exe`
  или пакета `.app`. Путь загрузчика Wine и `process:` для Wine — никогда:
  не вышло — игры нет. Ключ — в той форме, в которой его хранит ядро (NFC,
  без пустых компонентов, `.` и `..`, как `GetFullPath`), так что CoreHost
  сохраняет ровно присланный ключ; путь, который ядро изменило бы иначе
  (пробел или кавычка в конце всего пути; папка бутылки или диска с таким
  концом ключу не мешает), и ключ или имя длиннее 256 единиц UTF-16,
  которые CoreHost отверг бы вместе с каждым наведением в этой игре (T32), —
  тоже «игры нет». `SpeakerEditor`: открывается только на показанной
  карточке наведения по экрану, у которой есть говорящий с `observedName`
  и игра (иначе «Под курсором нет имени говорящего» или «Не удалось
  определить игру — род говорящего не сохранить»), начинает с имени
  карточки, клавиши 1, 2, 3 (верхний ряд и цифровой блок — физические
  клавиши Carbon) выбирают «Мужской», «Женский», «Не учитывать», а пока
  правится имя — это текст; Esc — отмена всегда. Выбор шлёт `speaker.set`
  с игрой наведения, `observedName` карточки как есть (а не именем, которое
  карточка показывает, T30) и именем по правилу ядра (NFC, пробелы
  схлопнуты, 1..48 единиц UTF-16; иначе — «Введите имя говорящего: от 1 до
  48 знаков» и ничего не отправляется); карточка статуса — «Kotori: женский
  род для Novel.exe» из отправленного, а не из текста CoreHost.
  `appToReactivate` — какому приложению вернуть клавиатуру при закрытии:
  только тому, что было активным, когда редактор её взял, и только если
  наше приложение стало активным с тех пор. `SpeakerClient` — сам
  `speaker.set`.

Что в `AsteriaScaleVision` (Foundation, Vision, CoreGraphics, CoreText и
CryptoKit; ImageIO разрешён DoD, но не нужен; без AppKit и без захвата экрана,
так что ничто в ней не вызывает запрос TCC): `VisionTextRecognizer` —
`VNRecognizeTextRequest` с явной ревизией 3, `.accurate`, исправлением языка,
`minimumTextHeight = 0`, `recognitionLanguages = ["en-US"]` и
`automaticallyDetectsLanguage = false` (T23, M18); кадр с плотностью меньше
1,5 пикселя на пункт (1x и масштабированные режимы ближе к 1x) увеличивается
вдвое только для Vision (высокое качество интерполяции), от 1,5 — как есть;
рамки возвращаются в пиксели кадра. Картинку для Vision делает одна функция
`PixelBuffer.visionImage`, и распознаватель сообщает самопроверке и пробе её
настоящий размер (`VisionRecognition.seenSize`): по рамкам в пикселях кадра
увеличение не видно, нормированные рамки Vision от него не зависят. Каждая
строка делится на слова `WordSplitter`, а
рамка слова — `VNRecognizedText.boundingBox(for:)` (в `.accurate` диапазон
латинского слова даёт рамку всего слова); слово без рамки пропускается.
Запрос выполняется на глобальной очереди. `FixtureRenderer` рисует CoreText
(Helvetica, чёрным по белому) известный латинский текст в кадр в памяти и
возвращает раскладку (типографские рамки строк и слов в пикселях кадра);
`VisionFixture` — три строки фикстуры, прогрев одной строкой и сверка
распознанного с раскладкой (строки и слова дословно, каждый край рамки слова —
не дальше половины кегля в пикселях кадра). PNG в репозитории нет.
`PixelBuffer` — `CGImage` ↔ раскладка кадра: BGRA-картинка копируется по
строкам как есть (без перекодирования цвета; у раскладки без альфы,
`noneSkipFirst`, неиспользуемый четвёртый байт становится 255, потому что
Vision получает кадр как BGRA с альфой, а какой раскладкой отдаёт кадр
настоящий захват, проверяет владелец), любая другая рисуется в BGRA её
собственного цветового пространства RGB. `FixtureFrameSource` — источник
кадра самопроверки: фикстура, нарисованная с плотностью дисплея и
положенная на белый кадр ровно границ плана так, чтобы середина «bank»
легла под курсор (или как можно ближе). `SHA256FrameFingerprinter` —
SHA-256 CryptoKit (T27). Набор подключает цель ради `PixelBuffer` и
отпечатка и никогда не вызывает Vision и CoreText.

```bash
bash tools/macos/test-swift.sh
bash tools/macos/test-swift.sh -- --filter ConnectionTests   # аргументы после -- уходят в swift test
```

Скрипт:

1. Собирает `src/AsteriaScale.CoreHost` (`-c Release --disable-build-servers`,
   так что ни узел MSBuild, ни VBCSCompiler не переживают прогон) и спрашивает
   у MSBuild его informational version (цель `GetAssemblyAttributes`): её сообщает
   `core.hello`, и набор передаёт её супервизору как ожидаемую сборку
   (`ASTERIA_COREHOST_BUILD`).
2. Выполняет `swift build -c release`, затем `swift build --build-tests`;
   любая строка `warning:` — ошибка. Состояние SwiftPM — в
   `macos/AsteriaScale/.build/swiftpm-state` (`--cache-path`, `--config-path`,
   `--security-path`, `--manifest-cache local`).
3. Запускает `swift test --skip-build`, только если тестовый бандл собран: без
   него `swiftpm-testing-helper` падает с fatal error (SIGTRAP), и в
   `~/Library/Logs/DiagnosticReports` появляется отчёт о сбое.
4. Каждый вызов `swift` идёт в рабочем каталоге W из `mktemp -d` вне клона, с
   собственными `HOME`/`CFFIXED_USER_HOME` (пустая `Library/Application
   Support`), `TMPDIR` и пустым текущим каталогом — отдельно для сборок и для
   прогона. Одних флагов состояния мало: SwiftPM 6.3 всё равно создаёт в
   данном ему домашнем каталоге `~/.swiftpm/{cache,configuration,security}`,
   `~/Library/org.swift.swiftpm` и `~/Library/Caches/org.swift.swiftpm`, а в
   `TMPDIR` — lock-файлы и папки `TemporaryDirectory.*`. Поэтому в настоящий
   домашний каталог он не пишет ничего, и исключения для инструмента, как у
   `~/.nuget`, нет. Временные каталоги самих тестов — в
   `ASTERIA_TEST_TMPDIR` (`W/test/scratch`).
5. Проверяет после прогона: `W/test/scratch` и текущий каталог пусты, в
   домашнем каталоге и `TMPDIR` прогона только эти записи SwiftPM; не осталось
   процессов, запущенных набором (найденные убиваются). Их находят только по
   токену прогона или пути W: скрипт передаёт набору десять случайных цифр в
   `ASTERIA_TEST_RUN_TOKEN`, набор дописывает их к длительности каждого
   своего `sleep` (`3141.59<токен>`) и передаёт подделке CoreHost как
   `-X asteria_run=<токен>`, а она — своим `sleep`, поэтому два прогона
   одновременно (другой клон, экспорт) не убивают процессы друг друга; в настоящих
   `~/Library/Caches/org.swift.swiftpm`, `~/Library/org.swift.swiftpm` и
   `~/.swiftpm` нет записей новее начала прогона; нового отчёта о сбое
   процесса прогона нет (тот же список G-crash, что у `run-macos-subset.sh`).
6. Печатает — только отчёт, не ошибка и не удаление (MACOS.md T35), — записи
   `getconf DARWIN_USER_CACHE_DIR` до глубины 2 и настоящего
   `~/Library/Caches/com.apple.e5rt.e5bundlecache` до глубины 3, которые
   новее начала прогона (та же отметка, что у проверки SwiftPM): не больше
   40 строк на каталог и число невыведенных, `none`, если их нет;
   отсутствующий или нечитаемый каталог — строка-примечание. Каталог кэша
   пользователя принадлежит учётной записи, а не `HOME`, поэтому временный
   домашний каталог его не закрывает, и в него всё время пишут другие
   процессы: глубина 2 называет
   писателя (bundle id или службу) и вид кэша, глубже — пофайловый шум.
   Время изменения папки меняется, только когда прямо в ней добавляют,
   удаляют или переименовывают запись, поэтому перезапись глубже
   перечисленного уровня не видна. Записать их мог любой процесс, не
   только прогон. Отчёт печатается один раз в каждом прогоне, дошедшем до
   сборок Swift: после проверок или ловушкой `EXIT`, если прогон
   остановился раньше (не прочитался список отчётов о сбоях после
   прогона, упал `--show-bin-path`, ^C); после выхода с кодом `2` или `3`
   до сборок — нет. Отметку ловушка удаляет при любом выходе. Удалять ли
   такие записи, решает владелец; агенты перечисляют новые записи в отчёте.

| Код | Когда |
|---|---|
| `0` | сборки без предупреждений, набор зелёный, ничего не осталось |
| `2` | ошибка аргументов, не Apple Silicon, нет `Testing.framework` в Command Line Tools, рабочий каталог внутри клона, не читается каталог отчётов о сбоях (до или после прогона) |
| `3` | не собрался CoreHost или не прочиталась его версия |
| `4` | сборка Swift упала, напечатала предупреждение или не дала тестового бандла |
| `9` | прогон оставил лишнее в `W/test` |
| `10` | появился новый отчёт о сбое процесса прогона (G-crash) |
| `11` | остался процесс, запущенный набором (он убит) |
| `12` | SwiftPM записал что-то в настоящие `~/Library` или `~/.swiftpm` |
| иной | код `swift test` |

`--keep` оставляет W и после успешного прогона. Если Command Line Tools
установлены, скрипт берёт их, что бы ни выбрал `xcode-select`: он
экспортирует `DEVELOPER_DIR=/Library/Developer/CommandLineTools` (MACOS.md
T33; Xcode.app на машине разработки — только ради сертификата, M16), и так
же поступает `build-app.sh`. С Command Line Tools простой
`swift test` не находит модуль `Testing`: CLT держат `Testing.framework` и
`lib_TestingInterop.dylib` вне путей поиска, и скрипт добавляет `-F` и
`-rpath` для них. XCTest в CLT нет совсем, а `#Preview` и другие Xcode-only
макросы не собираются (в CLT нет плагина `PreviewsMacros`), поэтому в пакете
только Swift Testing и ни одного `#Preview`.

Итог сообщают строкой `Swift suite: N tests` по строке Swift Testing `Test run
with N tests in M suites passed`; это не smoke-набор и не macOS-подмножество.
Эталонный прогон: `203` теста в `40` наборах, около 17–21 с (со сборками из кэша
около 20 с; релизная сборка включает и приложение, и пробу Vision).

Что проверяет набор (`Tests/AsteriaScaleKitTests`):

- **Без процессов.** Строгий JSON (20 неверных строк, невалидный UTF-8,
  глубина) и конверт (22 неверные строки); строки запросов; эталон
  `protocol-v1.golden.jsonl`: каждая строка ответа и события разбирается,
  ровно один ответ на запрос, `core.hello` проходит проверки и называет
  `hoverSources` `selection` и `observations`, `ocr.select` до `core.init`
  отвечает выбором ядра (`characterIndex` `16` — знак под курсором, а не
  начало слова, `14`), наведение по наблюдениям до `core.init` — `not_ready`,
  `hover.primary` и `hover.phrase` декодируются, `superseded` несёт
  `supersededBy`, каждый запрос эталона кодируется тем же конвертом; аргументы `AppPaths` против
  `launch-args-v1.txt` (`launchArgumentsMatchGolden`: те же обязательные и
  необязательные имена, каждое один раз, все пути абсолютные, пара
  ранжировщика — вместе); выделение и UTF-16; паузы перезапуска; строки
  интерфейса (кириллица, без «NPU» и «VULKAN», без слова «ядро» — и в строках,
  и в каждой строке Swift-исходников пакета, M15); константы Carbon и
  ⌥T/⌥D/⌥V и ⌃⌥G (M14, M20; Carbon id 1–4 по порядку действий); пути и
  окружение; настройки приложения (`macos-app.json`
  объектом JSON и в прежней форме массива, отсутствующие ключи, файл, который
  не разобрался, — строка в журнале; уровень оверлея); происхождение;
  каждая проверка `core.hello`. Игра и редактор рода (`ProcessArgumentsTests`,
  `GameIdentityTests`, `SpeakerEditorTests`, только синтетические байты и
  папки): запись `KERN_PROCARGS2` байт в байт (выравнивание, пустой аргумент,
  окружение до пустой записи, что после неё — не окружение; argv,
  переписанный Wine на месте, — сдвиг на одну и на две строки при прежнем
  argc: окружение и `WINEPREFIX` читаются, игра та же), `encode` против
  `parse`, пустой argv[0] читается как выравнивание (аргументы сдвинуты,
  первая запись окружения потеряна, при любом выравнивании; два пустых в
  начале — сдвиг на две строки), поиск в
  окружении по полному имени и не в аргументах; пустой,
  короткий, без NUL, с отрицательным и огромным argc, обрезанный на каждом
  байте и 3000 псевдослучайных буферов — запись или `nil`, без ловушки;
  Wine — по имени `wine*` и по пути внутри `CrossOver.app`; родная
  программа — `path:` её файла с именем пакета `.app`, в форме ядра (NFC,
  `//`, `.`, `..`), а путь, который ядро изменило бы (пробел, неразрывный
  пробел или кавычка в конце, NUL, относительный), и ключ длиннее 256
  единиц UTF-16 — игры нет (ровно 256 — есть); каждый выданный ключ сверен
  с повторённым здесь правилом ядра (`NormalizeGameKey`, дважды); раскладки
  аргументов Wine: загрузчик и игра, игра первой с аргументами, прямые
  косые и строчная буква диска, `start.exe` перед игрой, `.` и `..`,
  `start.exe /unix` с Unix-путём — игра; рабочий стол Wine, программа
  `drive_c/windows`, встроенная из `lib/wine`, ни одного `.exe`, голое имя,
  относительный путь, одно расширение и UNC — нет, путь загрузчика —
  никогда, две бутылки — два ключа; без `WINEPREFIX` или с относительным —
  нет; другие буквы — через текст ссылки `dosdevices` (подмена на коротких
  путях): относительный — от `dosdevices`, абсолютный — как есть, корень —
  корень, `..` выше корня — не выше корня, папка на месте ссылки — её
  путь, у каждого — ключ; пустой текст и нет текста — нет игры;
  читатель спрошен ровно о `<WINEPREFIX>/dosdevices/<x>:`, `C:` его не
  спрашивает; папка бутылки или цель буквы с пробелом или кавычкой в конце
  дают ключ, который Core не меняет; настоящий `readlink` на ссылках во
  временной папке теста отдаёт текст ссылки — и при отсутствующей цели, и
  у ссылки на ссылку (`realpath` дал бы ничего или конечную цель), у папки —
  её путь, без записи — ничего;
  окно под курсором — слой 0, чужой pid, спереди назад, окно Wine выше
  слоя 0 — его, а не окно слоя 0 за ним (окно не Wine выше 0 пропускается,
  рабочий стол ниже 0 — никогда, Wine спрашивается только про окно выше 0
  под точкой), края `minX`/`minY`
  внутри, `maxX`/`maxY` снаружи, верхняя строка главного дисплея, дисплей
  выше главного, неконечные числа. Редактор: открывается только с
  говорящим, `observedName` и игрой (каждый отказ — своим текстом), клавиши
  1/2/3 верхнего ряда и цифрового блока и Esc против кодов Carbon (пока
  правится имя — только Esc), полезная нагрузка `speaker.set` с
  `observedName` как есть и именем по правилу ядра (NFC, пробелы, 48 единиц
  UTF-16), тексты статуса, кому вернуть клавиатуру, и слежения нет, пока
  редактор открыт. Распознавание без Vision: `CaptureGeometry` на
  пяти синтетических дисплеях (главный 1x, слева 2x, справа 1x, верх которого
  на 460 пунктов выше главного, сверху 2x, снизу 1x; углы, общие края,
  промежуток между дисплеями, зеркальные дисплеи, масштабированный режим,
  точки, у которых `floor` даёт ширину или высоту дисплея, — прижатие;
  дисплей, у которого пикселей на пункт бесконечно много, например с
  субнормальной шириной, отвергается, а никакая точка ни на каком принятом
  дисплее не доходит до падающего преобразования в `Int`: ответ — `nil` или
  пиксель внутри дисплея), `sourceRect` и `isInside` (границы с `Int.max` —
  `false`, без переполнения); `VisionGeometry` (переворот оси y, оболочка
  повёрнутого четырёхугольника, 2x — те же рамки бит в бит, обрезка,
  вырожденная рамка остаётся, неконечная — нет, порог увеличения — ровно 1,5
  пикселя на пункт: `1.5.nextDown` и 1,49 увеличиваются, 1,5 и 1,51 — нет;
  слово длиннее 256 единиц UTF-16 не отправляется, а 256 — отправляется,
  и 129 эмодзи или 129 «e» с U+0301 — это 258 единиц, а не 129 знаков);
  `WordSplitter` (повторные, ведущие и хвостовые пробелы, табуляция,
  неразрывный и идеографический пробел, суррогатные пары, комбинирующие
  знаки, обратный перевод диапазонов);
  отпечаток через CryptoKit против каждой строки `frame-fingerprint-v1.txt`
  (число байтов в строке — только цифры и не больше 64 МиБ, иначе чтение
  проваливается раньше `Data(count:)`) и его 16 цифр без CryptoKit;
  `PixelBuffer` на `CGImage` из байтов (без шрифтов): BGRA с выравниванием
  строк и без, раскладка без альфы (`noneSkipFirst`) — четвёртый байт 255,
  а у BGRA с альфой он прежний, RGBA через рисование, обратно из кадра;
  `Frame.placed` и `Frame.origin` — кадр на холсте побайтно, у краёв и
  без ловушки на крайних значениях; картинка для Vision
  (`PixelBuffer.visionImage`): кадр 1x — вдвое больше, чёрная четверть —
  снова слева сверху, а два пикселя на её краю — смесь чёрного и белого
  (увеличение интерполирует: замер в красном канале — 31 и 223, при `.none`
  было бы 0 и 255), кадр 2x — свои же байты; кадр и его границы 1:1;
  запросы `ocr.select` и `hover.resolve` эталона (`#5` и `#9`), собранные из
  типов, после разбора равны строкам эталона; пределы CoreHost — на границе
  (ровно 4096 слов проходят; слово из 128 эмодзи и строка из 2000 — тоже:
  обе длины — в единицах UTF-16) и на шаг за ней; бюджет 900 КиБ; разбор
  ответов `ocr.plan` и `ocr.select`; ответ
  другой формы через типизированный клиент — `bad_response`, а
  `ocr.select` и наведение уходят с бюджетом, `ocr.plan` — без него
  (подменный `CoreRequesting` без процесса); `HoverReport` для `stale_frame` и
  `observations_too_large`. Захват без захвата (`CaptureTests`):
  `CaptureGate` на каждой ветке DoD 8 — `--self-test` (где бы он ни стоял),
  `ppid != 1`, нет `__CFBundleIdentifier` (или он пуст), чужой
  идентификатор у осиротевшего процесса с `ppid 1` (`com.apple.Terminal`,
  `com.anthropic.claudefordesktop`, iTerm, тот же id в верхнем регистре или с
  суффиксом), чужой bundle id, всё на месте — захват включён, и
  `offReason` согласна с `decide`, а разрешение тесты видят только в
  решениях (его инициализатор им недоступен); −3801 из домена ошибок
  захвата — то же сообщение, что без разрешения, из другого домена — нет;
  каждый отказ захвата и распознавания — по-русски, со своим кодом, на
  оверлее своим текстом (`HoverReport.ownCodes`), а сообщение конца
  наведения по экрану при курсоре, ушедшем с якоря, становится скрытием
  (`HoverReport.overlay(cursorStillThere:)` и
  `HoverReport.afterScreenHover`, `AppSupportTests`; там же — записка
  «Карточка не показана…» для карточки, которую сверка не показала);
  `RecognitionStatus`: сначала решение, потом разрешение и отказ захвата
  (`screen_capture_denied` от проверки разрешения или −3801) при любом
  прогреве, потом прогрев, строки меню, пункт разрешения и слежение только
  при «готово». `OverlayStateTests`: наведение по экрану, ничего не
  показавшее, не скрывает карточку ⌥V ни слежением, ни своим концом без
  сообщения (сообщение конца её заменяет), а карточку загрузки
  вытесненного наведения снимает; слежение скрывает
  только показ наведений по экрану (и их сообщения конца) и обрывает
  поздние карточки и строку фразы этого наведения; карточка — на месте
  своей карточки загрузки или у точки наведения; строка фразы — только под
  своей карточкой, после её таймера — с ней снова; пока открыт редактор
  рода, наведение по экрану ничего не показывает (и его сообщение конца),
  а наведение «Проверки перевода» — показывает; отказ ⌃⌥G (`notice`) не
  отбирает панель у наведения в полёте — ни по экрану, ни ⌥V: карточка
  загрузки наведения по экрану, ещё ничего не показавшего, и карточка
  любого заменяют отказ, его конец без карточки и скрытие
  слежением отказ не снимают, строка фразы к закрытой карточке не
  присоединяется, а собственное сообщение приложения наведение по-прежнему
  вытесняет. `HoverTracker` на
  подменных часах (`HoverTrackerTests`): 90 мс, 225, пределы задержки
  (100 → 350, 5000 → 1500, `Int.min`/`Int.max`), первое наведение на
  360/720/1530 мс после остановки и больше ни одного на этом якоре, граница
  задержки включительна (720 и 450 мс — ровно на опросе), граница радиуса
  (15 и (9, 12) — рядом, 16, (10, 12), (11, 11) и первые дальние точки
  (15, 1) и (14, 6) — нет; у `mayShow` тоже), уход — отмена и скрытие,
  другой дисплей и курсор вне дисплеев — уход, своё окно — только скрытие,
  `mayShow` — против курсора наведения, а не якоря, `reset`,
  `restart(after:)` (отмена и забытый якорь, новый режим — ещё и скрытие
  сразу, а следующий опрос скрывает карточку и при новых настройках;
  наведение снова через задержку) и какие изменения настроек его вызывают
  (режим, модель, бэкенд, удержание в памяти; не задержка, не выключатель,
  не первые настройки); крайние значения курсора — далеко, без ловушки. `HoverPipeline` без
  процесса (`HoverPipelineScriptedTests`, подменный `CoreRequesting`): без
  разрешения источник кадра не вызывается ни разу и ни одного запроса нет;
  названная игра приходит в `onGame` один раз, до захвата, и уходит в
  `hover.resolve` (без игры — ни там, ни там);
  курсор вне дисплеев — ничего; границы плана вне дисплея — `bad_response`
  без захвата; ровно одна повторная попытка, даже если и она получила
  `stale_frame` (два `hover.resolve`, ничего не показано); `bad_request` в
  том же режиме — один новый план и тот же отказ; источник кадра или
  распознаватель, которые сами отменились или отказали, — отмена или их
  русский текст, и больше ничего; кадр не того размера, вернувшийся из
  захвата уже после отмены, — отмена, а не `frame_mismatch`; захват или
  распознавание, которые после отмены отказали (не своей отменой), —
  тоже отмена, без сообщения и без запросов; отмена во время повтора и во время
  нового плана доходит до самого запроса. Проверки исходников (`SourceScanTests`) — чистые
  функции над строками: обход папок пакета подаёт им файлы (новый файл
  проверяется без списка), а `theScansFlagEveryForbiddenSpelling` и
  `theImportParserReadsEveryForm` — подложенные строки, не меньше одной на
  каждое имя и каждую форму `import`, и сверяют весь список нарушений, то
  есть что строку отметило её собственное правило; каждый список имён ещё и
  записан заново из других кусков, так что проверка, переставшая видеть имя
  или форму `import`, падает. Имена захвата экрана и разрешений TCC из DoD 8
  (ScreenCaptureKit, `SCShareableContent`, `SCScreenshotManager`,
  `SCContentFilter`, `CG*ScreenCaptureAccess`), любое имя, начинающееся с
  `CGWindowList` (две функции DoD 8, остальные функции и тип параметров), и
  `CGImage(windowListFrom…)` — под этим именем Swift импортирует
  `CGWindowListCreateImageFromArray`, в нём префикса нет, — только в
  `Sources/AsteriaScale/ScreenSource.swift` (DoD 8, T22): первые шесть — в
  любой его строке (и проверка требует, чтобы каждое там было), а из
  `CGWindowList…` — только `CGWindowListCopyWindowInfo` и ровно в двух
  строках: статический помощник, через который проверка
  `overlay.windowServer` самопроверки 20.2 читает запись своей панели, без
  экземпляра и без запроса разрешения, и чтение игры экземпляром (T29);
  другой вызов `CGWindowList…` (и `windowListFrom`) — нарушение и там. Из
  ключей списка окон в любой строке любого исходника (и в комментарии)
  бывают только `kCGWindowOwnerPID`, `kCGWindowLayer`, `kCGWindowBounds`,
  `kCGWindowSharingState` и `kCGWindowIsOnscreen` — имени окна нет нигде, —
  а запись аргументов и окружения чужого процесса (`KERN_PROCARGS…`) в коде
  названа только в `ScreenSource.swift` (комментарии её называть могут), а
  её имя для `sysctlbyname` (строчными, через точку после `kern`) — ни в
  одной строке ни одного исходника; голое число константы проверка имён не
  видит. Там же закреплён как код весь `game(at:)`: список окон для игры —
  только видимые окна без рабочего стола; окно выбирает правило Kit с
  проверкой владельца на Wine из приложения (путь `proc_pidpath` через
  `GameIdentity.isWine`, без пути — не Wine); запись читается только для
  Wine (`let record = GameIdentity.isWine(path) ? processArguments(owner) :
  nil`; окружение другой программы может хранить секреты, а `GameIdentity`
  на него не смотрит); ссылка `dosdevices` читается `readlink`, без
  перехода по ней. Читающий запись член назван в файле ровно трижды
  (объявление, этот вызов и метка аргумента), так что ни вызов, ни ссылка
  на функцию, ни сохранённое значение не читают запись ещё где-то; сам он
  тоже закреплён целиком, а константа записи названа один раз, так что
  запись читает только его тело (обёртка над помощником, которого зовёт
  и другой член, не пройдёт); правило окна Kit вызывается один раз.
  Манифест `Package.swift` подключает
  фреймворк и называет его, но исходником пакета не считается. Создание
  `ScreenSource` в любом известном проверке написании (`ScreenSource(`,
  `ScreenSource.init`, `.init(permit:` у любого типа) есть в исходниках
  пакета только в одной строке — той, что в `AppDelegate.swift`
  разворачивает `Permit` решения `CaptureGate` (`if case .on(let permit) =
  captureDecision { … }`), — не считая объявления инициализатора
  `init(permit: CaptureGate.Permit)` в `ScreenSource.swift`. Решение
  читается как код (без комментариев и содержимого строк) со схлопнутыми
  пробелами и переводами строк, так что разнесённое по строкам или с
  пробелом написание — тот же текст, и во всех исходниках: любое
  упоминание `decide` (`CaptureGate.self.decide`, через `typealias`, без
  типа внутри расширения, ссылка на функцию) — одно из двух: объявление в
  `Capture.swift` Kit, чьё тело с единственным `Permit()` закреплено
  целиком, и выражение в `AppDelegate.swift`, закреплённое целиком
  (аргументы — `CommandLine.arguments`, `getppid()`, окружение процесса и
  bundle id `Bundle.main`) и сразу за которым идёт строка создания;
  `Permit` назван только в `Capture.swift` Kit (объявление структуры,
  закреплённое целиком вместе с `fileprivate`-инициализатором, `case
  on(Permit)` и `Permit()` в `decide`; ни `.init`, ни `Self(` там нет) и
  в инициализаторе `ScreenSource`, так что ни соответствие протоколу, ни
  `typealias`, ни расширение другого разрешения не создают; `getppid`
  встречается только как вызов и не объявляется через `func`, а
  `CommandLine`, `ProcessInfo` и `Bundle` — только перед точкой, так что
  их имя не берёт ни объявление, ни параметр функции или замыкания, ни
  образец, ни параметр обобщения; ни один исходник не меняет окружение
  (`setenv`, `putenv`, `unsetenv`, `environ`, `_NSGetEnviron`) и не
  привязывает функцию к имени символа (`@_cdecl`, `@_silgen_name`,
  `@_dynamicReplacement`, `@_extern`); из объявлений `captureDecision`,
  которые читает проверка (`let`/`var captureDecision`, `captureDecision:`
  — аннотация или параметр, `captureDecision in` — параметр замыкания или
  переменная цикла), есть одно — хранимое свойство без аксессоров,
  наблюдателей, начального значения и атрибута, и это выражение —
  единственная его запись из тех, что считает проверка
  (`=`, кроме `==`, в том числе после `?` и `!`, кортеж-образец перед
  `=`, передача через `&` и путь ключа; запись без разрешения захват всё
  равно не включит). Так литерал не доберётся до решения ни в одном
  написании, которое читает проверка; код, обходящий сам язык
  (небезопасное приведение или запись по указателю, символ, найденный во
  время работы), и код вне Swift-исходников пакета (цель на C, настройка
  компоновщика) она не видит (тесты вызывают чистое решение сами).
  `ScreenSource.swift`, кроме импортов, объявляет только свой класс: один
  инициализатор, ни вложенного типа, ни объявлений вне класса, ни
  статических членов и членов класса, кроме `ownWindowRecord`, чей текст
  закреплён целиком (его тело читает одну запись окна и ничего больше), и
  ни один заголовок расширения его не называет и ни один `typealias` им
  не является — проверка разрешения, запрос, захват и чтение игры
  доступны только через экземпляр.
  `HoverDriver.swift` заканчивает наведение по экрану одним
  `HoverReport.afterScreenHover` со свежей сверкой `mayShow()`, затем
  `screenHoverEnded`, затем `overlay.finish`, скрытие слежением выполняет
  только через `overlay.trackerHide()` (простого скрытия в файле нет), а
  карточка в `Overlay.swift` стирает строку фразы прежней карточки
  (Swift-набор не подключает цель приложения, поэтому закреплены сами
  вызовы); `AppDelegate.swift` решает о слежении по состоянию редактора
  рода (`status.tracks(hoverEnabled: enabled, editorOpen:
  speakerEditor.isOpen)` — одно решение, а переключателей слежения два: оно
  и остановка при выходе), открытие решает заново сразу за
  `speakerEditor.open(…)`, а закрытие — сразу за `overlay.resume()`;
  закрывается редактор только там и при выходе (прямо перед остановкой
  слежения), оверлей возобновляется только там, так что ни сохранение, ни
  отмена не закроют редактор в обход, а отказ ⌃⌥G показывается только
  через `overlay.showNotice` (самопроверка никогда не следит, поэтому паузу
  видит только этот закреплённый код); `HoverDriver.swift` отдаёт редактору
  говорящего, игру, точку и номер показанной карточки сразу после её
  записи (`app.cardShown`) и больше нигде; в `SpeakerEditorPanel.swift`
  закреплены признак правки имени (`firstResponder is NSText`) и путь
  клавиш панели, а действие «Отмена» и обработчик клавиш контроллера
  закреплены целиком (отмена ими только вызывает `onCancel`, то есть
  `closeSpeakerEditor`); `close()` закреплён целиком, и из вызовов, что
  закрывают или скрывают окно (`close`, `orderOut`, `performClose`,
  `setIsVisible`), файл называет только его объявление и один `orderOut` в
  нём (скрытие другим написанием, прозрачностью или рамкой за экраном,
  проверка имён не видит; самопроверка нажимает клавиши через обработчик,
  а не через панель);
  глобальные мониторы NSEvent, event tap,
  `AXIsProcessTrustedWithOptions`, `IOHIDRequestAccess`,
  `CGRequestListenEventAccess`, `CGRequestPostEventAccess`, `IOHIDManagerOpen`
  и старые вызовы захвата (`CGDisplayCreateImage`, `CGDisplayStream`,
  `AVCaptureScreenInput`) — нигде. Тесты импортируют только Testing,
  Foundation, Darwin, CoreGraphics, Carbon и модули пакета (`import` в любой
  форме: с атрибутами, модификатором доступа, через `;`, с блочным
  комментарием внутри, с атрибутом на отдельной строке или модулем на
  следующей строке; файл для этого читается как код: комментарии и
  содержимое строковых литералов,
  включая raw и многострочные, заменяются пробелами, так что текст в них —
  не `import`, а файл с незакрытым комментарием или литералом — нарушение;
  `everySourceReadsToItsEnd` сверяет, что разбор находит каждый `import` в
  начале строки во всех файлах пакета; Vision, CoreText и AppKit ловятся
  ещё и в сырой строке, даже в комментарии), не называют типы Vision (`VN` с
  заглавной), запросы распознавания и их обработчики, `FixtureRenderer`,
  `VisionFixture`, `FixtureFrameSource` и вызовы CoreText, CoreGraphics и HIToolbox, которые
  создают шрифт или рисуют текст; Kit импортирует только Foundation и
  Darwin, `AsteriaScaleVision` — только разрешённое DoD 6 (в тех же формах
  `import`). Для приложения (`AppSupportTests`): разбор
  настроек и правки `settings.update` в схеме CoreHost, `models.status` и
  строка модели (загружена, не загружена, не используется, ошибка воркера с
  кодом), сообщения `status` и их журнал, `HoverReport` для каждого исхода
  наведения, говорящий в `hover.primary` с `observedName` и выбор для
  `speaker.set`, который возвращает это написание как есть (без него выбора
  нет), `app.log` и его ротация, каталог моделей из командной строки
  (читается на месте и не создаётся) и префиксы очистки, русский текст
  отказа запуска с кодом ошибки вместо описания Foundation или `strerror`
  (папки, файл блокировки), identity ConSeC
  против `MacWorkerTests.cs`, bundle id M13 в `AppIdentity`, `Bundle/Info.plist`
  и `build-app.sh` и entitlements трёх подписей.
- **С поддельным CoreHost** (`Fixtures/fake-corehost.py`: stdlib,
  `/usr/bin/python3` из Command Line Tools, протокол v1 и внедрение сбоев):
  собственная группа, только fd 0–2, пустая маска сигналов у процесса,
  запущенного из актора (контроль: обычный `posix_spawn` из того же потока
  передаёт заблокированные SIGTERM и SIGCHLD; `kinfo_proc.kp_proc.p_sigmask`
  для такой проверки не годится — ядро всегда отдаёт там 0, поэтому маску
  сообщает сам процесс), SIGPIPE, SIGTERM, SIGINT и SIGHUP по умолчанию
  у ребёнка родителя, который их игнорирует (`p_sigignore`), порядок
  событий, `request.cancel`, брошенные запросы, шесть видов нарушений
  протокола (группа с внуком завершена), падение посреди запроса (потеря
  замечена быстрее 500 мс), зависание (SIGKILL в срок), 16 МиБ в stderr,
  запись в мёртвый процесс без SIGPIPE, слишком длинный запрос, EOF, внук
  мёртв к моменту сообщения о выходе (он держит stdout, поэтому его может
  завершить только kill между `WNOWAIT` и сбором), упорядоченная остановка и
  эталон, проигранный через настоящее соединение (подделка отвечает строками
  эталона; исходы `#5`–`#10` — `ok`, трижды `not_implemented` и дважды
  `not_ready`). Наблюдения Apple Vision (`ObservationTests`, через само
  соединение; типизированный клиент — ниже; подделка
  повторяет формы, отказы, русские сообщения и метки CoreHost; строку и
  слово она выбирает, как `OcrGeometry.SelectNearPoint` (ближайшая строка, по
  вертикали с весом 1,6, дальше 95 пикселей — ничего; в ней ближайшее слово,
  его текст как есть: «bank.», «'bank'»), но предложение ядра не собирает:
  текст — вся строка, говорящего из наблюдений у неё нет, а строки, которые
  различаются лишь другим предложением, у неё расходятся; правило согласия
  двух наблюдений `StabilizeSentenceContext` — то же слово под
  `NormalizeEnglish` и слова одного наблюдения внутри другого по порядку,
  иначе первое и неустойчиво; из согласных берётся более полное, устойчивое,
  если оно похоже на законченное предложение (приближение
  `LooksLikeCompleteOcrSentence` без имени говорящего, снятия непарной
  кавычки и починки письменности) — и кэширует, как CoreHost, выбранное
  наблюдение; геометрию ядра, сборку предложения, говорящих и кэш кадра
  закрепляют C#-проверки CoreHost; те же запросы с наблюдениями из этих
  тестов, отправленные настоящему CoreHost, дали то же слово, устойчивость,
  отпечаток в следующем `ocr.plan`, кадр, в котором выбирает наведение без
  наблюдений, события, метки и происхождение):
  `ocr.select` без `core.init` (выбор, явный `null`, когда строк нет и когда
  курсор дальше 95 пикселей от них, неустойчивое одно наблюдение и
  устойчивые два в «Умном словаре»; более короткое второе наблюдение
  оставляет первое, более полное выбирается, другое слово перед «bank» и
  слово в кавычках — расхождение, обрезанное предложение неустойчиво; четыре
  отказа `bad_request` с `detail` CoreHost), а
  `ocr.plan` и наведение до `core.init` — `not_ready`, при выключенном переводе
  `ocr.plan` — `hover_disabled`; «Словарь»: 900×340 у курсора и у края
  дисплея, `hover.loading` с распознанной строкой до `hover.primary`,
  `hover.displayed`, наведение в чужом режиме — `bad_request` без событий,
  наведение по выделению начинается с `hover.primary` без `hover.loading`
  (`hover.phrase` после него подделка шлёт от себя: CoreHost до 20.5 фраз не
  шлёт); «Умный словарь»: 2400×720, `hover.loading` со словом под
  меткой ранжировщика «APPLE VISION  •  CPU-СМЫСЛ», затем карточка эвристики
  (`heuristic`, «APPLE VISION  •  SMART-FAST», источник — слово без краевой
  пунктуации, «bank» и для «'bank'»: так CoreHost без воркера
  ранжировщика отвечает на слова этих тестов в их предложениях; эвристика
  решает по слову в его предложении, и там, где она оставляет слово
  ранжировщику, — «of» в полном предложении, «bank» в обрезанном «He sat on
  the bank of the», — CoreHost отвечает `rankerUnavailable`; его, как и
  уверенность эвристики, подделка не шлёт); наблюдения, расходящиеся и по
  правилу ядра (другое слово перед «bank», слово в кавычках), не кэшируются,
  согласные кэшируются, и план в зоне
  повторного использования сохраняет границы и называет отпечаток, а на
  другом дисплее и вне зоны — нет; ноль наблюдений по записи кэша и два, чьё
  первое — закэшированный кадр, выбирают в нём (у нового курсора); ноль
  наблюдений с другими границами, отпечатком или дисплеем — `stale_frame`
  («Кадр на экране изменился.») без событий; когда ядро выбирает более полное
  второе наблюдение, кэш держит его: `ocr.plan` называет отпечаток наблюдения
  2, ноль наблюдений выбирает в его кадре, а прежняя запись заменена
  (`stale_frame`); наведение, отвергнутое при приёме, не заменяет идущее.
  Типизированный клиент (`ObservationClientTests`): `ocr.select` без
  `core.init` (выбор, явный `null`, отказ `bad_request`), `ocr.plan` до
  `core.init` — `not_ready`, план «Словаря» 900×340 и наведение по нему —
  `.loading`, затем `.primary` с id запроса; наведение в чужом режиме —
  отказ; через супервизор в «Умном словаре»: ноль наблюдений без записи —
  `.staleFrame` без событий, согласные наблюдения кэшируются, план называет
  их отпечаток, ноль наблюдений по нему — `.loading` со словом; бюджет на
  настоящей строке и для `ocr.select`, и для наведения: 921 600 байт уходят
  (выбор получает ответ, наведение до `core.init` — `not_ready`), 921 601 —
  `observations_too_large` без записи и без id, превышение предела CoreHost —
  тоже отказ без отправки; через супервизор запрос сверх бюджета (около
  965 000 байт, меньше 1 МиБ) — тот же отказ, а супервизор остаётся готов.
  Конвейер наведения (`HoverPipelineTests`, подменные источник кадра,
  распознаватель и игра, записывающий `CoreRequesting` поверх соединения):
  «Словарь» — `ocr.plan`, игра, один кадр 900×340, одно распознавание,
  `hover.resolve` с одним наблюдением, его отпечатком, границами, курсором
  и игрой, затем `.loading` со строкой и `.primary` «bank»; «Умный
  словарь» — кадр 1, распознавание 1, кадр 2, распознавание 2 именно в этом
  порядке, два наблюдения, а следующее наведение по закэшированному кадру
  — один кадр, без Vision, ноль наблюдений с `cachedFingerprint`, другой
  кадр — снова два распознавания; запись кэша, которую между планом и
  наведением заняло устойчивое наведение на другом дисплее, — `stale_frame`
  без событий и ровно одна повторная попытка с распознаванием уже снятого
  кадра 1 и новым кадром 2; смена режима между планом и наведением — один
  новый план и наведение в новом режиме (кадр 900×340, затем два 2400×720),
  а вторая смена во время повтора не догоняется — отказ остаётся, планов
  ровно два; кадр на пиксель уже — `frame_mismatch` до Vision; 240 строк
  по 3900 букв — `observations_too_large`, записан только `ocr.plan`, а
  наведение отвергнуто без id. Отмена на каждом шаге: `ocr.plan`,
  задержанный `test.delayNextWork` (как у CoreHost), и `hover.resolve`,
  задержанный карточкой «slow» (крючок подделки, как её «slow» у
  выделения), получают `request.cancel` (подделка пишет его цель в stderr,
  проверка сверяет её с id запроса), а их поздний ответ сливается; игра,
  кадр 1, распознавание 1, кадр 2 и распознавание 2 — подмены дожидаются
  отмены и возвращают свой результат как обычно (настоящие захват и Vision
  посередине не прерываются), а конвейер всё равно заканчивается отменой, и
  после шага ничего не записано и не отправлено, `request.cancel` нет (в
  полёте ничего не было).
  Супервизор: `kill -9` — снова готов быстрее 3 с, запись `corehost.pid`
  следует за процессом; зависший пинг; цикл падений; чужая
  версия протокола; четыре неприемлемых hello (не лидер группы, тестовые
  команды, чужая сборка, заблокированный SIGTERM) — перезапуск, затем
  `failed`, ни разу не `ready`; отказ `core.init`; запрос в состоянии
  `failed` или до запуска — `not_running`, а не `not_ready`; ручной перезапуск;
  процесс, игнорирующий `core.shutdown` и SIGTERM, — группа пуста за
  grace + 1 с. Наведение: заменённое — `superseded`, отменённое — `request.cancel`
  в CoreHost, пустой и неудачный исход, `hover_disabled`, `hover.displayed`,
  повторная правка рода с карточки, где уже исправленное имя (подделка
  хранит выбор под `observedName`, как CoreHost), событие, которым клиент
  выделения не пользуется (`hover.loading` перед наведением по выделению на
  слове «loading»; CoreHost такого не шлёт), — исход и карточки прежние.
  Наведение называет свой запрос (`onSent`). Редактор рода
  (`SpeakerEditorFlowTests`): карточка наведения, которое отправило игру и
  говорящего «Kotorl», открывает редактор с этим именем; исправление на
  «Kotori» с выбором «Женский» через `SpeakerClient`, затем на следующей
  карточке (она показывает «Kotori») повторная правка «Мужской» отправляет
  `observedName` «Kotorl», и следующее наведение получает мужской род.
  Настройки: устаревшая ревизия —
  `conflict`, клиент перечитывает и повторяет правку один раз, старый снимок
  не заменяет новый. Блокировка экземпляра (второй держатель в том же и в
  другом процессе) и очистка осиротевшей группы (совпавший лидер, в том числе
  под префиксом через ссылку `/tmp` или `/var` — лидер там копия
  `AsteriaTestSleeper`, и тест сначала проверяет, что копия запускается и
  выходит с кодом 0; неверное время старта или
  путь — не трогается; осиротевший участник после выхода лидера; нет записи,
  битая запись, чужая или исчезнувшая группа).
- **Настоящий CoreHost** (`realCoreHostReachesHello`): сборка из шага 1
  запускается так же, как её запустит приложение (аргументы и окружение
  `AppPaths`, временный домашний каталог, супервизор с ожидаемой сборкой и
  `core.init`), проходит `core.hello` (пустая `blockedSignals`, лидер группы,
  без тестовых команд) и `core.init` (словари и морфология загружены), отвечает
  на пинг, отдаёт настройки первого запуска (ConSeC, CPU, перевод выключен),
  отвечает `conflict` на устаревшую ревизию (клиент повторяет правку) и
  `models.status`, и останавливается; группа пуста, его текущий каталог и
  `TMPDIR` пусты, `corehost.pid` удалён, статусы без «ядро», «NPU OCR» и
  «VULKAN».

#### Проба Vision

Проба DoD 6 этапа 20.3 (`Tests/AsteriaScaleVisionProbe`) отвечает до первой
самопроверки с Vision: распознаёт ли настоящий Apple Vision, настроенный как
в `AsteriaScaleVision`, латинскую фикстуру в исполняемом файле, подписанном
ad-hoc с hardened runtime и без entitlements, во временном домашнем каталоге,
и не появляется ли отчёт о сбое. Её собирает `tools/macos/test-swift.sh`
(`swift build --build-tests` кладёт её рядом с тестовым бандлом); набор её не
запускает, в приложение она не входит. Запускается только копия,
переподписанная так, прямым `exec` в каталоге из `mktemp -d` с шаблоном
`"${TMPDIR:-/tmp}/asteria-probe.XXXXXX"` вне клона (голый `mktemp -d` на
macOS `TMPDIR` не учитывает; у агента `TMPDIR` — в его рабочем каталоге),
со своими `HOME`/`CFFIXED_USER_HOME`, `TMPDIR` и пустым текущим каталогом,
со списком отчётов о сбоях до и после,
с проверкой, что после неё не осталось процессов и записей в текущем
каталоге и `TMPDIR`, и с отчётом в духе T35 о записях вне временного
домашнего каталога, которые новее запуска (только отчёт; удаляет их
владелец; защищённые папки кэша печатают ошибки доступа, поэтому оба
`find` — с `2>/dev/null`):

```bash
bash tools/macos/test-swift.sh
P=$(mktemp -d "${TMPDIR:-/tmp}/asteria-probe.XXXXXX") && P=$(cd "$P" && pwd -P) && mkdir -p "$P/home" "$P/tmp" "$P/cwd"
cp macos/AsteriaScale/.build/arm64-apple-macosx/debug/AsteriaScaleVisionProbe "$P/"
codesign -s - -o runtime -f "$P/AsteriaScaleVisionProbe"   # ad-hoc, hardened runtime, без entitlements
python3 -I tools/macos/crash-reports.py ~/Library/Logs/DiagnosticReports
touch "$P/marker" && sleep 1
(cd "$P/cwd" && exec env CFFIXED_USER_HOME="$P/home" HOME="$P/home" TMPDIR="$P/tmp/" \
  "$P/AsteriaScaleVisionProbe" </dev/null)
python3 -I tools/macos/crash-reports.py ~/Library/Logs/DiagnosticReports
pgrep -f "$P"; ls -A "$P/cwd" "$P/tmp"          # ничего
(cd "$(getconf DARWIN_USER_CACHE_DIR)" && find . -mindepth 1 -maxdepth 3 -newer "$P/marker" 2>/dev/null)
(cd ~/Library/Caches/com.apple.e5rt.e5bundlecache && find . -mindepth 1 -maxdepth 3 -newer "$P/marker" 2>/dev/null)
```

Проба печатает конфигурацию (та же сверка, что `vision.config`), время
прогрева, каждую строку и рамку слова обеих фикстур (2x и 1x) рядом с
нарисованной, наибольшее отклонение края, размер картинки, которую получил
Vision (`Vision given 658x236` у обеих фикстур: кадр 1x 329×118 увеличен
вдвое), и проверку кадра не того размера (`frame_mismatch`), затем
`PROBE PASS` и код `0`; любой провал — `PROBE FAIL` и код `1` (путей к
аварийному завершению в ней нет). Если увеличение пропустить, Vision всё
равно читает 14-пиксельную фикстуру 1x (отклонение 4,1 пикселя), и
провал даёт только проверка размера: так проба вела себя на экспорте с
мутацией, которая отдаёт Vision кадр без увеличения. Замер 11 ноября 2026
(macOS 27, сборка 26A434, M4 Pro, отладочная сборка пробы): первый запуск в
новом домашнем каталоге — прогрев 23,7–24,4 с (весь запуск 24,0–24,6 с),
фикстура 2x — 41–46 мс, 1x — 37–44 мс; второй запуск того же файла в том же
каталоге — прогрев 87–94 мс, весь запуск 0,19–0,20 с; другой исполняемый
файл (пересобранная проба) в том же каталоге снова компилировал, 23,7–24,3 с
в двух запусках; в самопроверке прогрев занял 24,4–25,0 с; наибольшее
отклонение края — 6,8
пикселя при 2x (допуск 14) и 3,8 при 1x (допуск 7). Кэш компиляции Vision
лёг в `$P/home/Library/Caches/AsteriaScaleVisionProbe/com.apple.e5rt.e5bundlecache/26A434/`
(три бандла, 136 КБ): каталог назван по имени процесса (у приложения — по
bundle id, `…/Caches/io.github.slava8519.AsteriaScale/…`) и по сборке ОС,
поэтому после обновления macOS ожидается новая компиляция. В настоящем
`~/Library/Caches/com.apple.e5rt.e5bundlecache` новых записей нет. Вне
временного домашнего каталога Vision обновляет записи в
`getconf DARWIN_USER_CACHE_DIR`, и временный `HOME` их не переносит: каждый
запуск пробы с компиляцией меняет `com.apple.metal/<n>/functions.list` и
`com.apple.metal/<n>/libraries.list` (здесь `<n>` — `16777235_467` и
`32024`) за время запуска, когда других процессов проекта нет, — связь
установлена по времени записи, в каждом измеренном запуске. Самопроверка
приложения так же меняет `io.github.slava8519.AsteriaScale/com.apple.metal/<n>`;
отчёт T35 смотрит только до глубины 2 и называет эту папку, лишь когда она
новая. Такие записи перечисляют в отчёте и не удаляют (это дело владельца).
Новых отчётов о сбоях проба не оставила.

#### Приложение в строке меню

`Sources/AsteriaScale` — приложение (accessory, `LSUIElement`: без значка в
Dock). Всё, что ему нужно от перевода, идёт через CoreHost; без CoreHost
меню, горячие клавиши и оверлей продолжают работать, а то, что требует
службы перевода, выключено или отвечает русским сообщением.

- **Запуск.** Пути — от `NSHomeDirectory()` (он следует
  `CFFIXED_USER_HOME`), CoreHost, воркер и данные — внутри бандла
  (`AppPaths.bundled`); `--ranker-models <каталог>` (только разработка)
  заменяет каталог моделей воркера, который читается на месте и не
  создаётся. Один экземпляр на домашний каталог (`flock` на
  `Runtime/app.lock`) и, кроме самопроверки, один на сеанс по bundle id
  (`NSRunningApplication`); второй показывает «Asteria Scale уже запущен…» и
  выходит. Супервизор получает ожидаемую сборку CoreHost из `Info.plist`
  (`AsteriaCoreHostBuild`, её ставит `build-app.sh`), `core.init` после
  hello, запись `Runtime/corehost.pid` и префиксы очистки: `Contents/Helpers`
  бандла и `Models/TranslateGemma`. Там же, один раз и больше никогда,
  `CaptureGate.decide` решает, можно ли этому процессу захватывать экран
  (MACOS.md T22), и только при включённом решении строится единственный
  `ScreenSource`; в журнал пишется `screen capture: on (…)` или `off: <причина>`.
- **Меню.** Строка состояния «Перевод: готов» / «Перевод: перезапуск…» /
  «Перевод: недоступен» (и «запуск…», «остановлен»; причина — второй
  строкой), строка модели значений из `models.status` (ошибка воркера —
  русская фраза CoreHost с кодом, например «Модель значений ConSeC не
  установлена (consec_bundle_missing).»), строка распознавания
  (`RecognitionStatus`): «Распознавание отключено: проверочный запуск» при
  выключенном захвате (самопроверка, `--run-isolated`, любой прямой запуск),
  «Распознавание: нужен доступ к записи экрана» и под ней пункт «Разрешить
  запись экрана…», пока захват включён, а разрешения нет или захват у
  наведения по слежению получил отказ (тогда — до перезапуска приложения;
  после выбора пункта —
  «Распознавание: после разрешения записи экрана перезапустите Asteria
  Scale»: исходим из того, что macOS применяет разрешение к запущенному
  приложению только после перезапуска; так ли это на macOS 27, проверяет
  владелец, ROADMAP DoD 20.3, пункт 12.2), затем «Распознавание:
  подготовка…» на время прогрева Vision, «Распознавание: готово» или
  «Распознавание недоступно: прогрев Vision не удался (<код>)». Строки
  «отключено: проверочный запуск» и «нужен доступ к записи экрана» видны с
  запуска; строки нет, только пока захват включён, разрешение есть, а
  перевод при наведении ни разу не включался (прогрева не было). Отказ в горячей клавише — своей строкой. Затем «Перевод текста» (⌥T, `hoverEnabled`), «Режим перевода»
  («Умный словарь — выбор по контексту», «Словарный — слово или выражение»),
  «Проверка перевода…» (⌥D), «Перевести текст из буфера обмена» (⌥V),
  «Настройки…» (⌘,), «Перезапустить перевод», «Выход» (⌘Q). Переключатели
  меняют настройки только через `settings.update`. Открытие меню заново
  читает разрешение (проверкой без запроса) и обновляет строку
  распознавания.
- **Распознавание и курсор (20.3).** Только при включённом решении
  `CaptureGate`: проверка разрешения «Запись экрана» —
  `CGPreflightScreenCaptureAccess()` (без запроса) через `ScreenSource`;
  системный запрос — только пункт «Разрешить запись экрана…»
  (`CGRequestScreenCaptureAccess()`, а если тот не дал разрешения или
  захват уже получил отказ, — ещё и панель `Privacy_ScreenCapture`
  Системных настроек, MACOS.md T22). Vision
  прогревается один раз за запуск, когда перевод при наведении впервые
  включён (около 24 с в новом домашнем каталоге, MACOS.md T25). Курсор
  отслеживается (`HoverDriver` → `HoverTracker`: `NSEvent.mouseLocation`
  каждые 90 мс, без глобальных мониторов и event tap), только пока захват
  включён, разрешение есть (и захват не получал отказа), Vision готов,
  служба перевода готова и перевод при наведении включён; иначе слежение
  остановлено, наведение в полёте
  отменено, а его карточка скрыта. Своё окно под курсором — все окна
  приложения, кроме прозрачного для мыши оверлея (`NSWindow.windowNumber(at:)`).
  Курсор, задержавшийся в 15 пикселях дисплея на время задержки,
  запускает `HoverPipeline` с `ScreenSource`, настоящим Vision, отпечатком
  CryptoKit и игрой под курсором (`ScreenSource` как `GameResolver`,
  MACOS.md T29: `CGWindowListCopyWindowInfo` только видимых окон без
  рабочего стола, из записи — pid владельца, слой и границы, затем
  `proc_pidpath`, а у Wine — `sysctl` `KERN_PROCARGS2` процесса того же
  пользователя, до 4 МиБ; решает `GameIdentity`): захват
  `SCScreenshotManager.captureImage(contentFilter:configuration:)` с
  фильтром дисплея под курсором, который исключает все приложения с
  bundle id M13 (если своего приложения нет в `SCShareableContent` — отказ
  `capture_self_missing`, а не захват), `sourceRect` в пунктах дисплея из
  `CaptureGeometry.sourceRect`, размер — пиксели границ плана, без
  курсора, BGRA; `SCShareableContent` (все окна, не только видимые)
  запоминается до `NSApplication.didChangeScreenParametersNotification`.
  Ничего из ScreenCaptureKit не вызывается, пока проверка разрешения не
  ответила «да»; отказ −3801 при захвате — то же, что без разрешения
  (DoD 20.3, пункт 7): русское сообщение на оверлее, а строка «нужен
  доступ к записи экрана» и пункт «Разрешить запись экрана…» возвращаются
  в меню, и слежение останавливается до перезапуска приложения (проверка
  разрешения и до отказа отвечала «да», так что снять отказ ей нечем);
  `HoverDriver` сообщает приложению об отказе до показа сообщения конца,
  поэтому остановка слежения его не скрывает. Карточка загрузки появляется с `hover.loading`, карточка
  — с `hover.primary`, и каждая — только если курсор всё ещё в 15
  пикселях от курсора наведения и не над своим окном (карточку, которую
  сверка не показала, окно «Проверка перевода» отмечает как «Карточка не
  показана: курсор ушёл с места наведения»); уход курсора
  отменяет наведение (`request.cancel`) и скрывает только карточки
  наведений по экрану (карточка «Проверки перевода» и ⌥V остаются, и
  когда наведение по экрану уже началось, но ничего не показало; конец
  такого наведения без сообщения их тоже не скрывает, а сообщение конца
  заменяет, `OverlayState`); та же сверка —
  перед строкой фразы (и только под карточкой этого наведения, которую
  слежение не скрыло) и перед сообщением конца наведения. Смена режима
  или настроек «Умного словаря» при слежении перезапускает его
  (`HoverDriver.settingsChanged`): новый режим скрывает карточку сразу,
  новые настройки — на следующем опросе.
  В историю попадает только показанная карточка (`hover.displayed`). Ни
  один запуск агента и ни одна проверка не создают `ScreenSource` и не
  выполняют захват, проверку и запрос разрешения, чтение игры (список окон,
  аргументы и окружение чужого процесса) и слежение за курсором:
  это проверки владельца (ROADMAP DoD 20.3, пункт 12); самопроверка
  выполняет только `HoverDriver.hover` с кадром из памяти и статический
  `ScreenSource.ownWindowRecord`.
- **Род говорящего (20.3, MACOS.md T17, T29–T31).** ⌃⌥G открывает
  редактор «Род говорящего» на карточке наведения по экрану, которая
  показана сейчас (`HoverDriver` запоминает её говорящего, игру, которую
  отправило это наведение, и точку): имя (поле, в нём можно исправить
  написание, прочитанное с ошибкой), «Игра: Novel.exe. Выбор сохранится
  только для неё.», примечание «Пока род только запоминается: переводить
  фразы с его учётом Asteria Scale начнёт, когда появится TranslateGemma.»,
  кнопки «1  Мужской», «2  Женский», «3  Не учитывать» и «Отмена», клавиши
  1, 2, 3 и Esc. Без такой карточки или без говорящего на ней оверлей
  говорит «Под курсором нет имени говорящего», без игры — «Не удалось
  определить игру — род говорящего не сохранить», и редактор не
  открывается; отказ не отбирает оверлей у наведения в полёте — его
  карточка, пришедшая позже, заменяет отказ, а у наведения по экрану, ещё
  ничего не показавшего, — и карточка загрузки (на Windows отказ уходит в
  строку состояния). Открытие скрывает карточку, а
  пока редактор открыт, слежение остановлено (наведение в полёте отменено, якорь забыт), и ничего из
  наведений по экрану на оверлей не попадает; закрытие возвращает слежение с
  нового якоря. Выбор — `speaker.set` с игрой наведения, `observedName`
  карточки как есть и именем из поля; после сохранения редактор
  закрывается, а оверлей показывает «Kotori: женский род для Novel.exe»;
  отказ (`settings_save_failed` и другие) остаётся в редакторе строкой с
  кодом. Редактор — неактивирующая панель: она должна брать клавиатуру, не
  активируя приложение, так что активной остаётся игра; курсор не
  возвращается (T31, на Windows — `SetCursorPos`), а если наше приложение
  тем временем стало активным, клавиатура возвращается приложению,
  которое было активным при открытии. Ни одна проверка этого не
  выполняла: самопроверка показывает панель, не давая ей клавиатуру; что
  панель получает 1/2/3, Esc и Return, цифра, набранная в поле имени, —
  текст, игра остаётся активной и клавиатура возвращается, проверяет
  владелец (ROADMAP, DoD 20.3, пункт 12.6). Цифры, пока правится имя, —
  текст (Return заканчивает правку), Esc отменяет и тогда. До TranslateGemma
  (20.5) род только запоминается: читает его модель фраз.
- **Горячие клавиши** — Carbon `RegisterEventHotKey` (без разрешений TCC),
  по умолчанию ⌥T, ⌥D, ⌥V (M14) и ⌃⌥G (`editSpeaker`, M20) или из
  `macos-app.json` в папке данных
  (его пишет только владелец, объектом JSON: `{"hotkeys":
  {"translateClipboard": {"keyCode": 9, "modifiers": 2304, "keyLabel": "V"}},
  "overlayLevel": "screenSaver"}`; любой ключ можно опустить, действие без
  записи получает своё сочетание по умолчанию, прежняя форма массива
  `["translateClipboard", {…}]` тоже читается). Файл, который не удалось
  прочитать или разобрать, даёт значения по умолчанию и строку
  `preferences: cannot use …` с причиной в `app.log`. Отказ системы
  (`eventHotKeyExistsErr` — «Сочетание ⌥V уже занято другим приложением»,
  иначе «Не удалось зарегистрировать сочетание … (код …)») виден в меню,
  настройках и `app.log`; другие клавиши взамен не берутся (у ⌃⌥G пункта
  меню нет, отказ виден строкой меню и в настройках). ⌥V читает
  буфер обмена программно: на macOS 15.4+ система может спросить
  разрешение, а при `alwaysDeny` приложение говорит об этом в оверлее.
- **Оверлей** — неактивирующая `NSPanel`, прозрачная для мыши, уровень
  `.screenSaver` (слой 1000) или из `overlayLevel` в `macos-app.json`
  (`screenSaver`; `shielding` — на два уровня выше экрана-заслонки, над
  бутылкой с CaptureDisplaysForFullscreen, не измерено до 20.3/20.4;
  неизвестное имя — строка в `app.log` и `screenSaver`; применённый уровень
  тоже пишется в журнал), на всех рабочих столах и поверх
  полноэкранных окон, `sharingType = .none`. Карточка загрузки — формулировки
  WPF («Ищу в локальных словарях…», «Сверяю значения с контекстом…»; у
  наведения по экрану — с распознанной строкой в «Словаре» и словом в
  «Умном словаре» из `hover.loading`), значок — из типизированного
  происхождения. Собственные отказы захвата и распознавания
  (`HoverReport.ownCodes`: нет разрешения, своего приложения нет в
  захвате, `frame_mismatch` и другие) показываются своим русским текстом,
  `stale_frame` — ничем (только записка в окне). Конец наведения решает
  `HoverReport`: показанная карточка остаётся (8 с), пустой исход скрывает
  оверлей, `failed` — «Перевод не выполнен — подробности в окне «Проверка
  перевода»», служба перевода не готова или потеряна — «Перевод недоступен —
  служба перевода перезапускается», а если она в состоянии «недоступен» или
  остановлена и сама не перезапустится (`not_running`), — «Перевод
  недоступен — перезапустите перевод из меню»; `hover_disabled` — оверлей скрыт. Когда
  служба перестаёт быть готовой, показанная карточка скрывается. О показе
  карточки приложение сообщает `hover.displayed` (в историю попадает только
  показанное).
- **Окна.** «Проверка перевода» (⌥D): фраза, `[слово]` — слово под
  курсором, кнопка «Перевести»; под ней итог последнего наведения
  (`hover_disabled` — «Перевод текста выключен — включите его в меню»), его
  карточка и «Сообщения службы перевода (диагностика)» — последние 50
  событий `status`. Свободный текст CoreHost (`area: "translation"`, бывает и
  английским, с путём) виден только там и в `app.log`. «Настройки»:
  перевод (включение, режим, задержка 350–1500 мс — значения CoreHost, каждое
  изменение — `settings.update`, при отказе — его текст), модель значений,
  горячие клавиши, служба перевода (состояние и перезапуск), папки.
- **Выход.** «Выход», SIGTERM и SIGINT идут одним путём:
  `applicationShouldTerminate` отвечает `.terminateLater`, приложение
  снимает горячие клавиши и останавливает CoreHost (`core.shutdown`, EOF
  stdin, SIGTERM и SIGKILL группе), затем отпускает блокировку. Сигнал
  приходит через dispatch-источник, но `terminate` вызывается из блока
  run loop, а не из блока главной очереди: иначе вложенный цикл
  `.terminateLater` не смог бы выполнить задачу остановки на главном
  акторе, и приложение зависло бы (это ловит самопроверка). Убитое
  приложение (SIGKILL) CoreHost видит как EOF stdin и сам завершает свою
  группу, а запись `corehost.pid` следующий запуск сверяет и очищает.
- **Журнал** — `~/Library/Logs/AsteriaScale/app.log` (английский, с
  цитатами показанных русских текстов): состояния, отказы и регистрации
  горячих клавиш, stderr CoreHost, сообщения `status`, итог каждого
  наведения.

#### Сборка `Asteria Scale.app`

```bash
bash tools/macos/build-app.sh                       # сборка, подпись, проверка подписи
bash tools/macos/build-app.sh --self-test --ranker-models src/AsteriaScale.RankerWorker/Models
bash tools/macos/build-app.sh --no-build --run-isolated --ranker-models src/AsteriaScale.RankerWorker/Models
w=$(mktemp -d) && bash tools/macos/build-app.sh --sign - --output "$w/out" --install "$w/Applications"   # агенты: только так
bash tools/macos/build-app.sh --self-test --ranker-models src/AsteriaScale.RankerWorker/Models --install   # только владелец, с ASTERIA_SIGN_IDENTITY: в ~/Applications
```

Только Command Line Tools (`DEVELOPER_DIR`, T33), даже если установлен Xcode.app; около 25 с, самопроверка — ещё 30–40 с (из них около 24 с — первый вызов Vision в новом домашнем каталоге).
Результат — `artifacts/macos/Asteria Scale.app` (`--output <каталог>`;
промежуточные файлы — в его `.work`). Каталог вывода не может лежать в домене
File Provider. После проверок аргументов, платформы, инструментов,
`--ranker-models` и `TMPDIR` (они только читают) и до создания `--output` (в
любом режиме, с `--no-build` тоже) скрипт только читает расширенные атрибуты
ближайшей существующей папки вывода, разрешённой физически (`pwd -P`:
symlink, относительный путь и `..` в существующей части ведут туда, куда
писал бы `mkdir`), и всех папок выше до `/`. Читает он их только
`/usr/bin/xattr`, а не первым `xattr` в `PATH` (у pip-пакета `xattr` в venv
другие ключи и вывод). Код `2`, ничего не собрано и не создано, если:

- на этом пути есть корень домена (`com.apple.file-provider-domain-id`;
  измерен у `~/Desktop`, `~/Documents` и `~/Library/Mobile Documents`, а у
  папок `~/Library/CloudStorage` только ожидается: на машине разработки она
  пуста); сообщение называет вывод, папку, атрибут и провайдера;
- в ещё не существующей части `--output` есть компонент `.` или `..`:
  `mkdir -p` разрешает `new/..` только после того, как создаст `new`, и по
  тексту пути не видно, куда попадёт бандл (для `nx/../fp/app` проверка
  начала бы с папки выше `fp` и не прочитала бы атрибуты `fp`), а создавать
  папки ради проверки нельзя: отказ не должен ничего создавать, тем более в
  домене;
- часть `--output` существует, но это не папка: файл или символическая
  ссылка в никуда или на файл (с `/` на конце или без). Иначе проверка
  приняла бы её за ещё не созданную папку, а `mkdir -p` остановился бы на
  ней с кодом `1` без сообщения скрипта;
- скрипт не может разрешить ближайшую существующую папку или прочитать
  атрибуты одной из папок (отказ в сторону безопасности).

Метка `com.apple.fileprovider.*` (например, `dir#N`) у ближайшей
существующей папки, выше которой корень домена не найден, — не отказ, а
предупреждение одной строкой: такая метка остаётся после переноса папки из
домена и вне домена безвредна (измерено, раздел «Клон и iCloud Drive»).
Предупреждение называет метку, а для папки внутри клона — и необязательную
команду владельца, которая снимает такие метки в клоне, не следуя по
символическим ссылкам (`find … -exec /usr/bin/xattr -s -d …`, шаг 5 переноса
там же), если имя метки состоит из букв, цифр и `._#-`, а в пути клона нет
`'`: команда для вставки в терминал стоит в одинарных кавычках. Для папки вне
клона команды нет, как и для папки в `~/Library/CloudStorage`: проверка видит
только домены, которые помечают свой корень, а для провайдеров
`~/Library/CloudStorage` это не измерено, поэтому там предупреждение просит
метку не снимать и при ошибке `codesign` передать `--output` в другое место.
Поэтому `--output` по умолчанию работает в клоне вне всех доменов File
Provider, в том числе в перенесённом из домена; для клона в домене передайте
`--output` вне всех доменов. Состав бандла:

| Путь в бандле | Что |
|---|---|
| `Contents/MacOS/AsteriaScale` | приложение (релизная сборка SwiftPM) |
| `Contents/Helpers/AsteriaScale.CoreHost` | CoreHost: self-contained single-file ReadyToRun (MACOS.md T11) |
| `Contents/Helpers/Ranker/` | воркер так же, плюс `libonnxruntime.dylib` и `libhf_tokenizers.dylib` рядом |
| `Contents/Resources/Data/{Dictionaries,Morphology}` | `--data-root` CoreHost |
| `Contents/Resources/Legal/` | `LICENSE`, `THIRD_PARTY_NOTICES.md`, `DATA-LICENSES.md` |
| `Contents/Info.plist` | из `macos/AsteriaScale/Bundle/Info.plist`: bundle id `io.github.slava8519.AsteriaScale` (M13), версия CoreHost, номер сборки — число коммитов, `AsteriaCoreHostBuild` |

Шаги скрипта:

1. `swift build -c release` так же изолированно, как в `test-swift.sh`
   (свои `HOME`/`CFFIXED_USER_HOME`, `TMPDIR` и текущий каталог, состояние
   SwiftPM в `.build/swiftpm-state`); `warning:` — ошибка.
2. `dotnet publish` CoreHost и `src/AsteriaScale.RankerWorker.MacOS` для
   `osx-arm64` (self-contained, single-file, ReadyToRun) с
   `--artifacts-path` в `.work`: `bin/` и `obj/` клона не меняются.
   Публикация идёт с `-v minimal`, где MSBuild печатает каждое
   предупреждение, и предупреждение (`: warning <код>:`) — ошибка, как у
   Swift. Informational version CoreHost (цель MSBuild
   `GetAssemblyAttributes`, то же, что сообщает `core.hello`) ставится в
   `Info.plist`; другой сборки CoreHost приложение не примет.
3. Сборка бандла. Ни одного файла модели: `*.onnx`, `*.onnx.data`,
   `*.gguf`, `*.safetensors`, `*.vec`, путь с `consec` или `Models/`, файл
   больше 120 МБ или symlink — код `3`.
4. Подпись изнутри наружу с hardened runtime, без `--deep`: две dylib,
   воркер (`allow-jit`, `disable-library-validation`; идентификатор
   `<bundle id>.rankerworker`), CoreHost (`allow-jit`; `<bundle id>.corehost`),
   приложение без entitlements. По умолчанию ad-hoc (`--sign <identity>` или
   `ASTERIA_SIGN_IDENTITY`): тогда designated requirement — cdhash, и он
   меняется с каждой сборкой. Постоянная подпись — сертификат Apple
   Development владельца (MACOS.md M16): им подписывает только владелец
   (обращение к ключу может вызвать диалог связки ключей), а агенты
   подписывают ad-hoc и передают `--sign -`, если `ASTERIA_SIGN_IDENTITY`
   задан.
5. Проверка: `codesign --verify --strict --deep` («valid on disk»,
   «satisfies its Designated Requirement»), затем у каждого исполняемого
   файла — entitlements, флаг `runtime` и идентификатор.

Правила этапа 20.3 (Apple Vision и захват экрана). Ни один запуск агента
не вызывает ScreenCaptureKit, `CGPreflightScreenCaptureAccess`,
`CGRequestScreenCaptureAccess` и ничего другого, что может вызвать запрос
TCC («Запись экрана», «Универсальный доступ», «Мониторинг ввода»), и ни
один тест, проба или скрипт не создаёт `ScreenSource` и не вызывает его
захват, проверку или запрос разрешения и чтение игры (список окон,
аргументы и окружение чужого процесса): этот код только собирается и
сканируется, `GameIdentity` проверяется только на синтетических байтах и
папках, а редактор рода в самопроверке берёт игру из подменного
определителя и управляется без того, чтобы стать ключевым окном,
активировать приложение или сменить активное; выполняется лишь статический `ScreenSource.ownWindowRecord` (одно
чтение записи своей панели из списка окон, без ScreenCaptureKit и без
разрешения) — в проверке `overlay.windowServer` самопроверки. Настоящий
захват, слежение за курсором и первое разрешение — проверки владельца, а
захват приложение включает, только если его запустил владелец через
LaunchServices (`CaptureGate`, MACOS.md T22): в самопроверке,
`--run-isolated` и любом другом прямом запуске захват выключен,
`ScreenSource` не создаётся, курсор не отслеживается, приложение не
прогревает Vision, а меню говорит «Распознавание отключено: проверочный
запуск». Настоящий Vision работает только в самопроверке приложения и в пробе
DoD 6 (раздел «Проба Vision»), обе — только во временном домашнем каталоге;
Swift-набор и macOS-подмножество используют синтетические наблюдения и
текст не рисуют (T26). `build-app.sh --install` (T34) агенты
выполняют только в каталог внутри `mktemp -d`; `~/Applications` по
умолчанию — только владелец.

`--self-test` и `--run-isolated` запускают исполняемый файл бандла напрямую
(не через `open`: ответственным процессом для TCC остаётся терминал) в
каталоге `W` из `mktemp -d` вне клона: `HOME` и `CFFIXED_USER_HOME` — `W/home`
с пустой `Library/Application Support`, `TMPDIR` — `W/tmp`, текущий каталог —
пустой `W/cwd`. После запуска не должно остаться процессов бандла (код
`11`): убиваются только запущенные этим прогоном (само приложение, процессы,
в аргументах которых есть `W` — CoreHost с его `--user-data` и
`--log-dir`; `W` считается и в физическом виде `/private/tmp/…`, и в виде
`/tmp/…`, в котором его передаёт приложение, — и участники и дети их групп,
например воркер), остальные
только перечисляются и продолжают работать. Если перед сборкой (и с
`--no-build`) из `Contents` бандла уже запущен процесс, например копия
владельца, открытая через `open`, скрипт сразу выходит с кодом `2`, ничего
не собрав, не создав, не заменив и не убив: иначе шаг сборки удалил бы бандл
под ней, а проверка после запуска убила бы её CoreHost. Так же, до создания
каталога вывода, проверяются `TMPDIR` (не внутри клона), `--home` (каталог
из `--run-isolated`, не в клоне и не сам домашний каталог), бандл для
`--no-build` и чтение каталога отчётов о сбоях: ни один выход с кодом `2` не
оставляет каталог вывода, даже пустой. Не должно остаться и файлов в
`W/cwd` (код `9`), в настоящей `~/Library`
не должно появиться ничего с именем приложения или SwiftPM (код `12`), а
нового отчёта о сбое процесса сборки или приложения (список G-crash) быть не
должно (код `10`). Скрипт
печатает содержимое `W/home` после запуска. `--run-isolated` печатает `W`,
pid приложения и путь `app.log` и оставляет `W`; `--home W` запускает снова в
том же каталоге (только в каталоге, созданном `--run-isolated`); выход —
«Выход», ^C или `kill -TERM` скрипту: сигнал доходит до приложения, и оно
выходит по порядку (самопроверка, остановленная так, завершается кодом `130`
или `143` и оставляет свой каталог). Прямой запуск регистрирует бандл в базе
LaunchServices пользователя по его пути, и запись не пропадает вместе с
бандлом: каждый `--self-test` или `--run-isolated` бандла по новому пути
(другой `--output`, экспорт) добавляет ещё одну запись с bundle id M13, и
она остаётся после удаления папки. База лежит вне `~/Library` (в
`/private/var/folders`), поэтому проверка кода `12` её не видит. Посмотреть
записи можно только для чтения: `lsregister -dump | grep -B8
io.github.slava8519.AsteriaScale`, где `lsregister` —
`/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister`.
Устаревшие записи убирает владелец: `lsregister -u "<путь>/Asteria
Scale.app"` — одну, `lsregister -gc` — все записи бандлов, которых больше
нет. Агенты базу не чистят и перечисляют владельцу пути бандлов, которые
они так запускали (как новые отчёты о сбоях).

Самопроверка (`AsteriaScale --self-test`; ждёт не больше 120 с, сторож — на
фоновой очереди, MACOS.md T38; сторож скрипта — 150 с) не читает буфер
обмена, не активирует приложение, не просит разрешений и не захватывает
экран: решение `CaptureGate` для неё выключено, `ScreenSource` не создаётся,
курсор не отслеживается; каждую проверку печатает строкой `selftest <имя>: ok`,
`FAIL` или `note`, а итог — `SELFTEST PASS n/n` (скрипт требует эту строку,
код `6` иначе). Первым шагом идёт прогрев Vision (`vision.warmup`): он
стартует до остальных проверок и параллельно запуску CoreHost, распознаёт
одну латинскую строку, нарисованную в памяти, и печатает своё время и
каталог кэша компиляции Vision, который появился во временном домашнем
каталоге (`W/home/Library/Caches/io.github.slava8519.AsteriaScale/com.apple.e5rt.e5bundlecache`;
около 24 с в новом доме). `vision.config` печатает конфигурацию и сверяет
шесть значений T23, прочитанных у настоящего `VNRecognizeTextRequest`, каким
его делает распознаватель (ревизия 3, `.accurate`, исправление языка,
`minimumTextHeight` 0, `en-US`, без автоопределения), и увеличение кадра с
плотностью 1 (T24), измеренное на картинке, которую распознаватель отдал бы
Vision (`PixelBuffer.visionImage`, без вызова Vision). `vision.fixture`
рисует CoreText три строки латинской фикстуры для плотности 2 (кадр 658×236)
и 1 (кадр 329×118) и сверяет распознанное с раскладкой: строки и слова
дословно, каждый край рамки слова в пикселях кадра — не дальше половины
кегля (14 и 7 пикселей; измерено не больше 6,8 и 3,8), а размер картинки,
которую получил Vision, — 658×236 в обоих случаях (кадр 1x увеличен вдвое;
этот размер печатается и проверяется, потому что по рамкам в пикселях кадра
увеличение не видно). Затем, после `core.init` (ниже), `ocr.select` в
бандловом CoreHost с тем, что Vision прочитал в кадре 2x, и курсором в
середине нарисованного «bank» — слово «bank» с индексом знака внутри него в
строке «He sat on the bank of the river.», строки — те, что прочитал Vision.
`capture.gate` печатает решение `CaptureGate` для этого самого процесса
без учёта `--self-test` (только причину, без разрешения): выключено,
потому что родитель — скрипт, а не launchd (`ppid != 1`); решение самого
запуска принято (до запуска его нет) и выключено самопроверкой, и
`ScreenSource` не создан.
`capture.disabled` (после включения перевода и режима «Словарь»): перевод
включён, но `ScreenSource` не создан, курсор не отслеживается, ни одного
наведения по экрану, приложение не прогревало Vision, строка меню —
«Распознавание отключено: проверочный запуск», пункта «Разрешить запись
экрана…» нет. `hover.observations` (в «Словаре», после
`hover.afterRestart`: история не записывает наведение, чьи источник и
перевод повторяют её последнюю запись, а сразу после `hover.dictionary` это
был бы тот же «bank») прогоняет тот же `HoverPipeline` через тот же
`HoverDriver`, что и курсор, но на синтетическом дисплее 2x (1000×600
пунктов, курсор в середине), с подменным разрешением, `FixtureFrameSource`
(фикстура 2x на белом кадре 900×340 — ровно границы плана — так, что «bank»
под курсором), настоящим Vision и бандловым CoreHost. Сначала то же
наведение, у которого сверка курсора отвечает «нет» (курсор ушёл): оно
завершается, но ни карточки загрузки, ни карточки, ни записи истории нет,
окно «Проверка перевода» пишет «Карточка не показана: курсор ушёл с места
наведения», а карточка «door» из `hover.afterRestart` (наведение «Проверки
перевода»), которой это наведение не владело, остаётся на месте (T37).
Затем наведение с подменным разрешением, которое отвечает «нет» (никаких
запросов и кадров): `screen_capture_denied`, и при ушедшем курсоре
сообщения конца нет, а при курсоре на месте оверлей показывает «Нет
доступа к записи экрана: меню Asteria Scale → «Разрешить запись
экрана…».» — сверка перед сообщением конца в самом `HoverDriver`; оверлей
затем скрывается. Затем само наведение: план «Словаря» (550,
430, 900×340), один кадр, одно распознавание, `hover.resolve` с одним
наблюдением, затем карточка загрузки со строкой «He sat on the bank of the
river.» и «Ищу в локальных словарях…», карточка «bank» с происхождением
`dictionary` и меткой «APPLE VISION  •  СЛОВАРЬ» и запись истории «bank»
с этой строкой после `hover.displayed`. Игру этим наведениям называет тот
же подменный определитель, что у `speaker.editor` (ниже): CoreHost
принимает ключ, который сделал `GameIdentity`, а `HoverDriver` с показанной
карточкой отдаёт редактору рода игру, которую отправило наведение, и номер
карточки, которую оверлей показывает (у карточки, которую сверка не
показала, — ничего).
`speaker.editor` (в «Умном словаре» после проверок ConSeC: говорящего на
карточку ядро ставит только в этом режиме) — редактор рода против
бандлового CoreHost, с игрой из подменного определителя
(`SyntheticGameResolver`: `GameIdentity` на записи процесса CrossOver,
собранной в памяти, — загрузчик Wine, `C:\Games\Novel\Novel.exe` в
аргументах, `WINEPREFIX=/Selftest/CrossOver/Bottles/Novel`; ключ
`path:/Selftest/CrossOver/Bottles/Novel/drive_c/Games/Novel/Novel.exe`,
имя `Novel.exe`; ни окно, ни процесс, ни файл не читаются). Карточки дают
выделения с говорящим «Kotorl» (ошибка чтения «Kotori») и этой игрой;
каждая показывается на оверлее так же, как карточка наведения по экрану, а
редактор открывает путь ⌃⌥G, и клавиши идут через тот же обработчик, что у
панели. Без карточки — «Под курсором нет имени говорящего», с карточкой без
игры — «Не удалось определить игру…», и редактор не открывается; второй
отказ — при наведении по экрану в полёте, которое остаётся новейшим и
потом показывает свою карточку; открытие
скрывает карточку и не пускает на оверлей карточку наведения по экрану
(слежения в самопроверке нет вовсе, поэтому паузу слежения закрепляет
`SourceScanTests`, а не она); имя, исправленное на «Kotori», и 2 —
`feminine` под обоими написаниями в `settings.json` временного домашнего
каталога и карточка статуса «Kotori: женский род для Novel.exe»; следующая
карточка показывает «Kotori», и повторная правка клавишей 1 меняет запись
под «Kotorl» — написанием, которое карточка несла как `observedName`;
обработчик не берёт цифру, когда ему передано, что имя правится (что поле
имени правится, решает панель — это закреплено `SourceScanTests` и
проверяет владелец), а Esc ничего не отправляет (байты
`settings.json` прежние). Редактор ни разу не становится ключевым окном,
приложение не становится активным и не выходит на передний план; pid
активного приложения до и после печатается, но не сравнивается (владелец
может переключиться во время прогона).
Проверки 20.2: accessory-приложение с bundle id M13; блокировка
экземпляра; значок и пункты меню; оверлей (настройки панели и то, как её
видит оконный сервер: слой 1000, `sharingState` 0 — через статический
помощник `ScreenSource`, без экземпляра и без запроса разрешения); ⌥T, ⌥D, ⌥V
и ⌃⌥G зарегистрированы с кодом `0` (если их держит другая запущенная копия
приложения — `note`, иначе `FAIL`) и синтезированное в процессе нажатие
доходит до обработчика (наблюдатель стоит весь прогон: нажатия только
записываются); оба окна; готовность за 30 с с принятой сборкой CoreHost;
CoreHost, воркер и данные — из бандла, модели — нет; `core.init` (словари,
морфология больше нуля, первый запуск — ConSeC, CPU, перевод выключен);
наведение при выключенном переводе — `hover_disabled` и текст в окне;
включение перевода и режим через то же действие, что ⌥T и меню; словарное
наведение «The [bank] was steep…» — карточка «bank» с переводом, значок
«СЛОВАРЬ», запись в истории; строка модели из `models.status`; выбор
ConSeC на CPU через `settings.update`. С бандлом (`--ranker-models`) —
умное наведение «The [pitcher] threw…» через ConSeC с провайдером CPU и
точной identity, воркер в группе CoreHost; затем SIGKILL CoreHost во время
следующего умного наведения: исход `connection_lost`, оверлей скрыт или
говорит «Перевод недоступен…», воркер завершён, старая группа пуста,
готовность — быстрее 3 с, и наведение после перезапуска снова ранжирует
ConSeC. Без бандла — карточка `rankerUnavailable` («РАНЖИРОВЩИК
НЕДОСТУПЕН») и строка модели с кодом воркера, а строка проверки убийства
во время наведения — `note — absent: <путь>`. Дальше: `kill -9` CoreHost в
простое (снова готов, старая группа пуста), наведение после перезапуска,
наведение во время перезапуска — русское сообщение, а не зависание; ни в
одном показанном тексте нет «ядро», «NPU OCR» и «VULKAN», а свободный текст
CoreHost есть только в «Проверке перевода»; наконец SIGTERM самому себе —
упорядоченный выход тем же путём, что у launchd (`quit.signal`), группа
пуста, `Runtime/cwd` и `Runtime/tmp` пусты, `corehost.pid` удалён. Итог
сообщают строкой «app self-test: SELFTEST PASS n/n»: эталон — `36/36`
с бандлом и `34/34` без него.

Агенты запускают приложение только так. Запуск в настоящем домашнем
каталоге (`open`, копия в `~/Applications`, двойной щелчок) и копия бандла
ConSeC для приложения — только владелец (MACOS.md T9):

```bash
ditto src/AsteriaScale.RankerWorker/Models/Consec \
  ~/Library/Application\ Support/AsteriaScale/Models/Ranker/Consec
```

`ditto`, а не symlink: воркер отвергает ссылки на каталог модели и ниже.
Реальное нажатие ⌥T, ⌥D, ⌥V и ⌃⌥G в работающем приложении — тоже проверка
владельца: самопроверка доказывает регистрацию и обработчик, но не то, что
оконный сервер доставляет настоящее нажатие (сообщалось, что macOS 15
отвергала сочетания только с ⌥).

**Установка (`--install [DIR]`, MACOS.md T34).** Ключ кладёт копию бандла в
`DIR/Asteria Scale.app`. `DIR` — следующий аргумент, если он не начинается
с `--`; по умолчанию — `~/Applications` учётной записи (что бы ни говорил
`HOME`), и это только владелец: без `DIR` скрипт ставит только с подписью
не ad-hoc (`--sign` или `ASTERIA_SIGN_IDENTITY`, M16), а с `-` выходит с
кодом `2`, так что пустая переменная без кавычек (`--install $dir`) до
`~/Applications` не доходит. Агенты всегда передают `DIR` внутри каталога
из `mktemp -d` и подписывают ad-hoc полной сборкой в свой `--output`: с
`--no-build` ключ `--sign` ничего не делает, и копия несёт подпись бандла
из `--output`. С `--run-isolated` ключ не сочетается
(код `2`: у ручного запуска нет вердикта, после которого ставить). Копия
ставится только после прогона, все проверки которого прошли (с
`--self-test` — после пройденной самопроверки, и после проверок процессов,
`W/cwd`, `~/Library` и отчётов о сбоях). До создания каталога вывода, в
любом режиме и с `--no-build` тоже, `DIR` проходит ту же проверку File
Provider и пути, что и `--output` (корень домена на пути, `.` или `..` в
ещё не существующей части, файл или ссылка не на папку на пути), и скрипт
выходит с кодом `2`, ничего не собрав, не создав, не заменив и не убив,
если:

- `DIR` — каталог вывода или лежит внутри него (сборка заменяет его бандл и
  `.work`), или вывод лежит внутри `DIR/Asteria Scale.app`; пути
  сравниваются без учёта регистра ASCII, как в APFS по умолчанию (`pwd -P`
  не всегда пишет существующую часть так, как она хранится);
- `DIR/Asteria Scale.app` — символическая ссылка, не папка, папка, чей
  `Contents/Info.plist` не называет `io.github.slava8519.AsteriaScale`
  (чужой бандл скрипт не заменяет никогда), или запись под другим
  написанием (`asteria scale.app`: APFS находит её и под
  `Asteria Scale.app`, а замена сохранила бы хранимое имя);
- из `DIR/Asteria Scale.app/Contents` запущен процесс (копия владельца,
  открытая из Finder) или не читается таблица процессов. Процесс ищется по
  пути исполняемого файла, который сообщает macOS (`proc_pidpath` из libproc
  через `ctypes`: путь физический и абсолютный, как бы копию ни запустили —
  по относительному пути, через ссылку, в другом регистре; сравнивается без
  учёта регистра, и сравниваются только строки системы, чужие файлы не
  открываются), и `pgrep -i -f` по пути в аргументах, а для `/private/…` —
  и по псевдониму `/tmp/…` или `/var/…`. Процессы бандла в `--output`
  ищутся `pgrep -f` по физическому пути и, теперь, по тому же псевдониму.

Сама установка — последний шаг (шаг 7 в заголовке скрипта): `mkdir -p DIR`,
временная папка `DIR/.asteria-install.XXXXXX` (`mktemp -d`, та же файловая
система, что у цели), копия `ditto` в неё под именем без `.app` (имя
приложения носит только цель), сравнение байтов с бандлом (`diff -r`) и
`codesign --verify --strict --deep` копии. До этого места ^C и
`kill -TERM` завершают прогон (коды `130` и `143`), ничего не заменив; с
повторной проверки ниже и до конца прогона INT и TERM игнорируются
(несколько секунд), так что установка либо завершается, либо отменяется, и
прогон заканчивается отчётом. Прямо перед заменой цель проверяется ещё раз:
процесс, запущенный из неё за время сборки или самопроверки, нечитаемая
таблица процессов, цель, переставшая быть бандлом приложения, появившаяся
там, где проверка до сборки её не нашла, или пропавшая — код `7`, ничего не
заменено. Замена — один вызов `renamex_np(2)` через `ctypes` в `python3`:
`RENAME_SWAP` атомарно меняет местами копию и прежнюю установку, найденную
проверкой до сборки, `RENAME_EXCL` ставит первую копию, только если имя ещё
свободно. Поэтому прерванный прогон никогда не оставляет `DIR` без рабочей
`Asteria Scale.app` или с недокопированной под этим именем. Затем
установленная копия проверяется снова, скрипт печатает её строки `codesign`
(«valid on disk», «satisfies its Designated Requirement») и удаляет прежнюю
копию, которая теперь лежит во временной папке. Если эта проверка не
прошла, замена отменяется (код `5`); если не удалась и отмена, скрипт
больше ничего не двигает и не удаляет: непроверенная копия остаётся на
месте цели, прежняя — во временной папке, и сообщение называет обе (код
`5`). После установки список отчётов о сбоях читается ещё раз: новый отчёт —
код `10`, установка остаётся в силе. Временную папку скрипт удаляет на
любом другом пути ошибки, в том числе по ^C и `kill -TERM`, а папки `DIR`,
созданные этим прогоном, — если установка не завершилась (`rmdir`, только
пустые). Измерено 11 ноября 2026 (замена и проверки замедлены обёртками на
`PATH`): `kill -TERM` во время копирования `ditto` — код `143`, ни
временной папки, ни созданных папок, прежняя копия на месте и проходит
`codesign`; `kill -TERM` во время `renamex_np` или проверки установленной
копии и два ^C во время этой проверки — код `0`, копия поставлена,
проверена и названа. Оставить временную папку может только SIGKILL,
прежняя копия, которую `rm` не смог удалить после замены (например, из-за
флага или политики системы на уже запускавшейся копии: установка остаётся
в силе, код `0`, а предупреждение называет оставшуюся папку), или
неудавшаяся отмена. Следующий `--install` в тот же `DIR` называет такие
папки предупреждением и не удаляет их: в одной из них может лежать
единственная прежняя копия. Скрипт не запускает установленную копию и
не вызывает `lsregister`; копия, поставленная в папку внутри `mktemp -d` и
ни разу не запущенная, записи в базе LaunchServices не получила (измерено
11 ноября 2026, `lsregister -dump`), а для `~/Applications` это не
измерялось. Ожидается (не измерено: это проверка владельца 12.2 этапа
20.3), что разрешение «Запись экрана» после пересборки и `--install`
сохраняется только при постоянной подписи (M16): TCC хранит его вместе с
требованием подписи, а у ad-hoc требование — cdhash, и он меняется с каждой
сборкой. Поддельная
«запущенная копия» в проверках агентов — только копия `AsteriaTestSleeper`
(никогда не системная программа).

**Отчёт о кэшах (MACOS.md T35).** В конце каждого прогона, прошедшего
проверки до создания каталога вывода (выход с кодом `2` отчёта не
печатает), — и тогда, когда прогон остановился раньше (упавшая сборка, ^C:
отчёт печатает ловушка `EXIT`), — скрипт печатает записи
`getconf DARWIN_USER_CACHE_DIR` до глубины 2 и настоящего
`~/Library/Caches/com.apple.e5rt.e5bundlecache` до глубины 3, которые новее
отметки начала прогона: не больше 40 строк на каталог и число
невыведенных, `none`, если их нет; отсутствующий или нечитаемый каталог —
строка-примечание. Это только отчёт, не ошибка и не удаление: каталог кэша
пользователя принадлежит учётной записи, а не `HOME`, поэтому приложение
пишет туда и из временного домашнего каталога (с 10 ноября 2026 там есть
`io.github.slava8519.AsteriaScale/com.apple.metalfe`). В него всё время
пишут и другие процессы, поэтому глубина 2 — писатель (bundle id или
служба) и вид кэша, а глубже — пофайловый шум. Время изменения папки
меняется, только когда прямо в ней добавляют, удаляют или переименовывают
запись, поэтому перезапись глубже перечисленного уровня не видна. Кэш e5rt
устроен как `<сборка ОС>/<хеш модели>/<бандл>`, отсюда глубина 3. Уже до
20.3 там были `com.apple.metalfe` и `local.asteriascale.*` (9 ноября 2026);
удалять ли их, решает владелец, а агенты перечисляют новые записи в отчёте
этапа. В конце прогона INT и TERM игнорируются, чтобы поздний ^C не
оборвал отчёт и итог. Записать перечисленное мог любой процесс, не только
прогон.

| Код | Когда |
|---|---|
| `0` | собрано и проверено (и самопроверка прошла, и копия установлена) |
| `2` | ошибка аргументов (в том числе `--install` с `--run-isolated`), не Apple Silicon, нет инструмента (`xattr` — только `/usr/bin/xattr`), `TMPDIR` внутри клона, `--output` или `--install DIR` в домене File Provider, с `.` или `..` в ещё не существующей части, с файлом или символической ссылкой не на папку на пути или такой, что проверка не может разрешить ближайшую папку или прочитать атрибуты, `--home` не из `--run-isolated` (или в клоне, или сам домашний каталог), `--no-build` без бандла, не читается каталог отчётов о сбоях, из бандла уже запущен процесс, `--install` без `DIR` с подписью `-`, `--install DIR` — каталог вывода или внутри него (или вывод внутри `DIR/Asteria Scale.app`), `DIR/Asteria Scale.app` — ссылка, не папка, чужой бандл или запись под другим написанием, из него запущен процесс, не читается таблица процессов; всегда до создания каталога вывода: ничего не собрано, не создано, не заменено и не убито |
| `3` | файл модели, слишком большой файл или symlink в бандле |
| `4` | сборка упала или напечатала предупреждение (`swift build` или `dotnet publish`) |
| `5` | подпись или её проверка не прошла, в том числе проверка копии `--install`: до замены ничего не заменено, после неё замена отменена, а если не удалась и отмена, сообщение называет непроверенную копию на месте цели и папку с прежней (при «detritus» или «Disallowed xattr» в выводе `codesign` скрипт перечисляет расширенные атрибуты бандла и помечает вероятных виновников: `com.apple.FinderInfo`, `com.apple.ResourceFork`, `com.apple.fileprovider.*`; `com.apple.macl` и `com.apple.provenance` `codesign` принимает) |
| `6` | самопроверка не прошла или не закончилась за 150 с |
| `7` | `--install`: прямо перед заменой из `DIR/Asteria Scale.app` запущен процесс, не читается таблица процессов, или цель перестала быть бандлом приложения, появилась, хотя проверка до сборки её не нашла, или пропала; ничего не заменено |
| `8` | `--install`: не удалось создать `DIR` или временную папку, скопировать `ditto`, сравнить байты или переименовать; ничего не заменено |
| `9` | запуск оставил файл в своём текущем каталоге |
| `10` | появился новый отчёт о сбое процесса сборки, приложения или установки (G-crash) или список отчётов не удалось прочитать после сборки; установка остаётся в силе |
| `11` | остался процесс бандла (запущенный этим прогоном убит, другой только перечислен) |
| `12` | в настоящей `~/Library` появилось что-то с именем приложения или SwiftPM |
| `130`, `143` | ^C или `kill -TERM` (с `--install` — до повторной проверки шага 7: ничего не заменено) |

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
файл в выход. Лишняя строка `WinRTGlobalVtableLookup.g.cs` тестового проекта в
diff выходов генераторов значит, что общий тест привёл обобщённый класс
(например, `Task<T>` из ядра) к другому типу: объявил его как `Task` или
`object`, присвоил свойству или полю такого типа, передал методу тестовой
сборки с параметром такого типа или вернул `Task<T>`-выражение из лямбды.
Генератор CsWinRT добавляет для него vtable; держите такое значение в его
собственном типе.

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
identical (<n>)` и код `0` (сейчас `n` = `22`: `21` в ядре, из них один в
`OcrTextRules`, и один в `TextHookService`); различие — список удалённых и
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

Держите клон вне всех доменов File Provider, например в
`~/Developer/AsteriaScale`. Домены File Provider — это «Рабочий стол» и
«Документы» iCloud Drive (`~/Desktop` и `~/Documents`, когда их синхронизация
включена), `~/Library/Mobile Documents` и папки в `~/Library/CloudStorage`
(Dropbox, Google Drive, OneDrive и другие провайдеры). Имени папки на `.nosync`
для этого мало: оно останавливает выгрузку в iCloud, но папкой по-прежнему
управляет File Provider. Измерено 10 ноября 2026 на macOS 27.0.1 с клоном в
`~/Documents/Claude/AsteriaScale.nosync`:

- `~/Documents`, `~/Desktop` и `~/Library/Mobile Documents` несут
  расширенный атрибут `com.apple.file-provider-domain-id` (значение
  `com.apple.CloudDocs.iCloudDriveFileProvider/<UUID>`): это корни домена
  iCloud Drive. Корень клона несёт `com.apple.fileprovider.dir#N`, сам
  домашний каталог — ни одного атрибута File Provider;
- каждая новая папка с именем `*.app` внутри клона примерно через секунду
  получает `com.apple.FinderInfo` и `com.apple.fileprovider.fpfs#P`, а
  `xattr -d com.apple.FinderInfo` не держится: атрибут сразу возвращается;
- поэтому `build-app.sh` с `--output` по умолчанию (`artifacts/macos` в
  клоне) падал на подписи после полной сборки (около минуты): «resource
  fork, Finder information, or similar detritus not allowed», код `5`. Бандл,
  подписанный вне домена (проверка `--strict` проходит) и скопированный в
  клон командой `ditto`, не проходит `codesign --verify --deep --strict`:
  «Disallowed xattr com.apple.FinderInfo found». Самопроверки 20.2 прошли
  только с `--output` вне клона.

Теперь `build-app.sh` отказывается от `--output` в домене File Provider
кодом `2`, ничего не собрав и не создав (раздел «Сборка `Asteria Scale.app`»).

Вне домена оставшиеся метки безвредны. Клон, перенесённый 10 ноября 2026 в
`~/Developer/AsteriaScale` переименованием, сохранил
`com.apple.fileprovider.dir#N` на 16 папках: корень,
`src/AsteriaScale.{Core,CoreHost,RankerProtocol,RankerWorker,RankerWorker.MacOS}`,
`tests/AsteriaScale.Tests{,.MacOS}` и восемь папок `bin|obj/Release/net10.0`;
ни у одной папки выше нет `com.apple.file-provider-domain-id`, а папки,
созданные после переноса (`macos/AsteriaScale/.build`), метки не получают.
Измерено в тот же день: во временных папках в корне клона и в
`src/AsteriaScale.Core` новая пустая папка `X.app` и подписанный бандл,
скопированный туда `ditto`, через 3 и через 15 с несли только
`com.apple.provenance` (внутри бандла ещё `com.apple.macl`, как у исходника),
ни `com.apple.FinderInfo`, ни `com.apple.fileprovider.*`, а `codesign --verify
--deep --strict --verbose=2` копии печатал «valid on disk» и «satisfies its
Designated Requirement»; сами новые папки метку не унаследовали. Поэтому о
метке `com.apple.fileprovider.*` у ближайшей папки вывода без корня домена
выше `build-app.sh` только предупреждает одной строкой и продолжает, а
`--output` по умолчанию в перенесённом клоне собирает и подписывает бандл.

Остальные причины держать клон вне iCloud Drive:

- iCloud создаёт копии-конфликты вида `name 2.ext` в `bin/`, `obj/` и среди
  исходников, а лишний `* 2.cs` попадает в glob `*.cs` проектов;
- iCloud выгрузил бы в облако бандл ConSeC, которому по CC BY-NC-SA и решению
  M7 нельзя попадать в публичные хранилища (от этого `.nosync` защищает, пока
  имя папки не меняется);
- «Оптимизировать хранилище Mac» оставляет на диске только заглушки давно не
  открывавшихся больших файлов, в том числе моделей; их чтение требует
  повторной загрузки из облака.

Место проверяется только чтением: ни у папки клона (если её ещё нет — у её
ближайшей существующей папки), ни у одной папки выше вплоть до `/` не должно
быть атрибута `com.apple.file-provider-domain-id`. Команда ниже печатает его и
метки `com.apple.fileprovider.*` с путём папки. Если папку не удаётся
разрешить или прочитать атрибуты одной из папок, она печатает
`cannot resolve the folder` или `<папка>: cannot read its attributes`: тогда
место не проверено. Строка `<путь>: exists but is not a folder` значит, что
на пути лежит файл или ссылка в никуда: такое место не подходит. Пустой вывод
значит, что место подходит, а у уже
перенесённого клона одна строка `com.apple.fileprovider.dir#N` у его корня —
безвредная метка, оставшаяся от старого места (шаг 5 ниже). Команда только
читает и работает и в zsh, и в `/bin/bash` 3.2 (проверено 10 ноября 2026, в
том числе на папках с правами `000` и `100`; прежний вариант на папке с
правами `000` не заканчивался):

```bash
d=~/Developer/AsteriaScale
while [ ! -d "$d" ]; do
  if [ -L "$d" ] || [ -e "$d" ]; then echo "$d: exists but is not a folder"; fi
  d=$(dirname "$d")
done
if d=$(cd -P "$d" >/dev/null 2>&1 && pwd -P) && [ "${d#/}" != "$d" ]; then
  while :; do
    if a=$(/usr/bin/xattr "$d"); then
      printf '%s\n' "$a" | grep -E '^com\.apple\.(file-provider-domain-id|fileprovider\.)' | sed "s|^|$d: |"
    else
      echo "$d: cannot read its attributes"
    fi
    [ "$d" = / ] && break
    d=$(dirname "$d")
  done
else
  echo "cannot resolve the folder"
fi
```

Перенос существующего клона делает владелец (агенты клон не переносят):

1. Выйдите из приложения и остановите всё, что запущено из клона: закройте
   открытые в нём терминалы и редакторы, выполните `dotnet build-server
   shutdown`; `pgrep -fl 'Documents/Claude/AsteriaScale'` и `pgrep -fl
   'Asteria Scale.app'` не должны показать ни одного процесса.
2. Создайте папку назначения и перенесите клон переименованием. Команда
   переносит, только если клон и папка назначения лежат на одном томе
   (`stat -L` даёт одно и то же число) и `~/Developer/AsteriaScale` ещё нет;
   иначе она ничего не делает и печатает почему:

   ```bash
   mkdir -p ~/Developer
   src=$(stat -L -f %d ~/Documents/Claude/AsteriaScale.nosync) && dst=$(stat -L -f %d ~/Developer) && if [ "$src" != "$dst" ]; then echo "not moving: the clone and ~/Developer are on different volumes"; elif [ -e ~/Developer/AsteriaScale ]; then echo "not moving: ~/Developer/AsteriaScale already exists"; else mv ~/Documents/Claude/AsteriaScale.nosync ~/Developer/AsteriaScale; fi
   ```

   Проверка и перенос — одна команда, поэтому вставка блока целиком не может
   перенести клон на другой том. Сообщения в командах этого раздела
   английские: интерактивный `/bin/bash` 3.2 без UTF-8-локали искажает
   кириллицу во вставленной строке, и команда не выполняется. Блок проверен
   10 ноября 2026 во временной копии домашнего каталога в zsh и в `/bin/bash`
   3.2: перенос, отказ, если папка назначения есть, и отказ, если
   `~/Developer` — ссылка на другой том. Сравнивается сама папка назначения, а не домашний каталог: если
   `~/Developer` уже есть и это ссылка или точка монтирования на другом
   томе, `mkdir -p` ничего не делает, и `mv` копировал бы. `-L` нужен
   потому, что без него `stat` описывает саму ссылку, а она лежит на томе
   домашнего каталога: ссылка на `/dev` без `-L` дала то же число, что и
   `~`, а с `-L` — число `/dev`.

   Проверка `-e` нужна потому, что в уже существующую папку назначения `mv`
   положил бы клон внутрь (`~/Developer/AsteriaScale/AsteriaScale.nosync`);
   если она есть, команда ничего не переносит — разберитесь с ней сначала.
   В пределах тома `mv` — это `rename(2)`: файлы, в том числе единственная
   копия бандла ConSeC (`src/AsteriaScale.RankerWorker/Models/Consec`) и
   `ConSeC_WNGT`, остаются теми же файлами на диске, ничего не копируется.
   Не копируйте клон (`cp`, `ditto`, Finder) и не переносите его на другой
   том: там `mv` копирует и удаляет, а копировать бандл ConSeC нельзя.
3. Удалите только повисшую ссылку на старом месте — по пути самой ссылки,
   без `/` на конце и без `-r` (`rm -r` по пути со слешем удалил бы
   содержимое папки, на которую ведёт ссылка). Новая ссылка не нужна:

   ```bash
   test -L ~/Documents/Claude/AsteriaScale && rm ~/Documents/Claude/AsteriaScale
   ```

4. Удалите `macos/AsteriaScale/.build` (SwiftPM вместе с `swiftpm-state`):
   в нём записаны абсолютные пути старого места, и без этого `swift build`
   в перенесённом клоне падает (10 ноября 2026: «precompiled file
   '…/.build/…/ModuleCache/…/SwiftShims-….pcm' was compiled with module
   cache path '<старое место>/…', but the path is currently '<новое
   место>/…'», `build-app.sh` — код `4`). `bin/` и `obj/` проектов удалять
   не обязательно, это предосторожность: при переносе 10 ноября 2026 они
   остались (папки созданы до переноса), `project.assets.json` записан
   заново при первой сборке после него, старый путь остался только в
   списках `obj/…/*.csproj.FileListAbsolute.txt`, а сборки и проверки
   прошли без предупреждений.

   ```bash
   rm -rf ~/Developer/AsteriaScale/macos/AsteriaScale/.build
   ```

   По желанию удалите и `bin/` с `obj/`. Сначала посмотрите список того, что
   будет удалено (команда только печатает):

   ```bash
   find ~/Developer/AsteriaScale/src ~/Developer/AsteriaScale/tests -mindepth 2 -maxdepth 2 -type d \( -name bin -o -name obj \) -print
   ```

   и только если в нём одни `src/<проект>/{bin,obj}` и
   `tests/<проект>/{bin,obj}`, удалите их отдельной командой:

   ```bash
   find ~/Developer/AsteriaScale/src ~/Developer/AsteriaScale/tests -mindepth 2 -maxdepth 2 -type d \( -name bin -o -name obj \) -exec rm -rf {} +
   ```

   В блоках нет комментариев: в интерактивном zsh (без `interactivecomments`)
   `#` не комментарий, и хвост `# …` стал бы аргументами команды. Пути
   абсолютные, без `cd` и без переменных: команды не зависят от текущего
   каталога, и неудавшийся `cd` не направит `rm -rf` в чужие `src` и `tests`.
   `find` видит только `src/<проект>/{bin,obj}` и `tests/<проект>/{bin,obj}`:
   ровно второй уровень и только настоящие каталоги (`-type d`, не ссылки).
   `src/AsteriaScale.RankerWorker/Models` называется иначе, всё внутри него
   лежит глубже второго уровня, а `ConSeC_WNGT` — в корне клона, вне `src` и
   `tests`, так что ни то, ни другое команда достать не может. Никогда не
   используйте для этого `git clean -x` или `git clean -X`: они удаляют
   игнорируемые файлы, то есть бандл ConSeC в `Models/Consec` (`.gitignore`)
   и `ConSeC_WNGT` (`.git/info/exclude`) — единственные копии.
5. Проверьте метки клона только чтением: `find ~/Developer/AsteriaScale
   -xattrname 'com.apple.fileprovider.dir#N' -print`. Переименование
   сохраняет расширенные атрибуты: клон, перенесённый 10 ноября 2026 из
   `~/Documents/Claude/AsteriaScale.nosync` в `~/Developer/AsteriaScale`,
   сохранил эту метку на 16 папках (выше). Если проверка места выше не нашла
   `com.apple.file-provider-domain-id`, метки остались от старого места и вне
   домена безвредны: `build-app.sh` печатает о такой метке у ближайшей папки
   вывода предупреждение и продолжает. Снять их можно по желанию, это дело
   владельца:

   ```bash
   find ~/Developer/AsteriaScale -xattrname 'com.apple.fileprovider.dir#N' \
     -exec /usr/bin/xattr -s -d 'com.apple.fileprovider.dir#N' {} +
   ```

   Команда меняет только этот атрибут и только у того, что `find` нашёл
   внутри клона (папки, файлы и сами символические ссылки), и не трогает
   то, на что ведут ссылки: `find` по ссылкам не идёт, а `xattr -s` снимает
   атрибут с самой ссылки. Без `-s` `xattr -d` идёт по ссылке, которая сама
   несёт метку, и снимает метку с её цели, а цель может лежать вне клона;
   `xattr -r -d` тоже идёт по ссылкам. Измерено 10 ноября 2026 на временном
   дереве с метками на папках, файлом с другим атрибутом, блобом в 100 КБ и
   помеченными ссылками на помеченную папку вне дерева, на файл вне дерева
   и в никуда: вариант без `-s` снял метку с папки вне дерева, а ссылки
   свои сохранили; код `1` и две ошибки дали только ссылки на файл без
   метки и в никуда, а с одной ссылкой на помеченную папку тот же вариант
   отработал молча, с кодом `0`. Вариант с `-s` снял метку со всех
   папок и ссылок дерева, папка вне его осталась с меткой, имена, SHA-256 и
   mtime всех файлов и другие атрибуты не изменились, код `0`, stderr пуст.
   Символические ссылки в клоне есть, например три абсолютные в
   `artifacts/macos/.work/swift/home/.swiftpm`, которые создаёт каждая
   сборка `build-app.sh` с `--output` по умолчанию (после переноса они ведут
   на старое место, пока следующая сборка не пересоздаст `.work`), поэтому
   безопасность команды держится на `-s`, а не на том, какие ссылки в клоне
   есть. Для папки вывода в клоне `build-app.sh` предлагает ту же команду с
   найденной меткой.
6. Пересоберите и прогоните macOS-проверки командами из AGENTS.md («Safe on
   a macOS clone»): сборки воркера, CoreHost и подмножества,
   `bash tools/macos/run-macos-subset.sh`, `--smoke` воркера с бандлом на
   месте, `bash tools/macos/test-swift.sh` и `bash tools/macos/build-app.sh
   --self-test --ranker-models src/AsteriaScale.RankerWorker/Models` (теперь с
   `--output` по умолчанию).
7. Записи LaunchServices бандлов, запущенных по старым путям, остаются, пока
   их не уберёт владелец (`lsregister -u` или `-gc`, раздел «Сборка
   `Asteria Scale.app`»).

`.git/info/exclude` переезжает вместе с `.git`, а его шаблон `/ConSeC_WNGT/`
отсчитывается от корня клона, поэтому после переноса его менять не нужно.

Проверка reparse point в воркере (`ConsecArtifactBundle.cs`) отвергает
symlink у самого каталога `--model-directory` (`Models`), у `Models/Consec` и
у всего ниже, с ошибкой `consec_artifact_path_unsafe`. Каталоги выше
`--model-directory` не проверяются: путь к клону через symlink (как прежний
`~/Documents/Claude/AsteriaScale`) воркер принимает, а symlink на `Models`,
`Models/Consec` или файлы бандла — нет. Перенос `mv` этого не меняет: бандл
остаётся настоящим каталогом внутри клона.

### Стратегия тестирования

Объём после удаления SANDWiCH и GlossDeBERTa (8 ноября 2026), этапа №20.1,
регистрации «Smart hover engine strings», швов ядра этапа 20.2, переноса
в ядро оркестрации наведения, текстовых правил OCR и признаков вида карточки,
типизированного происхождения результата и меток хоста (9 ноября 2026), API
координатора для CoreHost, самого CoreHost, его команд через ядро,
Swift-библиотеки приложения с супервизором CoreHost, самого приложения в
строке меню, переноса в ядро пиксельной геометрии OCR, закрепления её
выбора строки и слова у курсора и транзакционного `speaker.set` (10 ноября
2026), наблюдений Apple Vision в протоколе CoreHost, распознавателя Apple
Vision и геометрии захвата в Swift-пакете (11 ноября 2026):
default-путь `tests/AsteriaScale.Tests/Program.cs` содержит
`57` блоков `await RunAsync(...)`, из них `33` вызывают отдельные классы
`XxxTests.RunAsync`. `57` — число регистраций Windows-прогона
(`MacSmoke.SharedRegistrationCount`); macOS-подмножество выполняет `46` из них
и семь собственных проверок. В `tests/` `59` файлов C# — `35 036` строк вместе с
harness gated-бенчмарков и одиннадцатью файлами `tests/AsteriaScale.Tests.MacOS` —
против `43 161` строки C# в `src/` (вместе с CoreHost). Отдельно есть шесть
Python-наборов `unittest` на `76` тестов (см. «Python-тесты инструментов») и
набор Swift Testing Swift-пакета macOS-приложения: `203` теста в `40` наборах,
`7 468` строк Swift (из них `23` — `AsteriaTestSleeper` и `115` — проба Vision)
и поддельный CoreHost на Python (`881` строка) против `5 915` строк
`AsteriaScaleKit` и `555` строк `AsteriaScaleVision` (раздел «Swift-пакет
`macos/AsteriaScale`», команда `tools/macos/test-swift.sh`). Само приложение
(`3 146` строк Swift; захват экрана и чтение игры в нём не запускает ни одна проверка) и
настоящий Vision проверяет его самопроверка в собранном бандле (`tools/macos/build-app.sh
--self-test`, раздел «Сборка `Asteria Scale.app`»).

| Слой | Что проверяет | Где | Что нужно кроме .NET 10 SDK |
|---|---|---|---|
| Чистая логика | словарный поиск и группировку смыслов, эвристики, регистр, извлечение OCR-предложения, prompt/parser/guard/gate TranslateGemma, маршруты hover-конвейера с подставными переводчиком и воркером, признаки вида карточки по строке движка и побайтные эталоны Windows-строк движка, типизированное происхождение результата и метки хоста (Windows-набор — прежние строки, macOS-набор — без «NPU OCR» и «VULKAN» в метках и статусах; каждая метка читается обратно в происхождение, в том числе в виде из истории; происхождение в результате конвейера и карточке координатора, бэкенд в строке фразы), каталоги данных ядра (прежние пути Windows, отказ при пустом или относительном LocalAppData, однократная настройка, журнал наведения вне текущего каталога) | блоки `Program.cs`, `CoreDataLayoutTests` (внутри «Settings round trip»), `HoverProvenanceTests` (внутри «Hover coordinator ordering and lifecycle»), `SmartDictionaryQualityFoundationTests`, `SmartPhraseTranslationFoundationTests`, `SmartHoverPhraseIntegrationTests`, `TranslateGemmaTranslationServiceTests` | ничего |
| Контракты данных C# ↔ Python | одинаковую нормализацию ключей (общий `scripts/dictionary-normalization-contract.tsv` читают и тест, и генератор), VN-слои, schema и provenance наборов, приём output C#-runner Python-scorer'ом | `LocalDictionaryNormalizationTests`, `VnDictionaryLayerTests`, `RankerBenchmarkProvenanceTests`, `TranslationSuiteTests`, `AtomicOutputSafetyTests` | Python 3.11+ как `python` в `PATH`: тесты запускают `tools/translation-benchmark.py` и `tools/atomic_output.py` |
| Процессы, IPC, кэши, транзакции | протокол v4 и fail-closed identity воркера, путь воркера и каталог моделей по умолчанию и от хоста, дедлайн ответа с остановкой воркера, строку движка каждого результата hover-конвейера (словарь, эвристики, Tatoeba, оба кэша, выбор и воздержание ранжировщика CPU и NPU, его недоступность, строки фразы) с поддельным воркером, оркестрацию наведения в ядре с подставным хостом (порядок карточек, основная карточка до записи в кэш результатов, отмена и скрытие, история показанного, жизненный цикл, освобождение в две фазы, строки движка карточки загрузки, тексты статусов прогрева и готовности; для хоста, отвечающего на запросы, — исход каждой ветви наведения, снимок состояния моделей до и после загрузки и выгрузки, выгрузку по запросу хоста под арендой обслуживания с объединением запросов, хост без модели фраз), код последней ошибки воркера в селекторе и в снимке, общие признаки провайдеров с CLI-round-trip хост → воркер, ключи и инвалидацию кэшей решений, транзакционность установщиков и `build`-скриптов, безопасность правок конфига Magpie, text-hook, DPI override (через in-memory хранилище, не реестр) | `GlossBertDictionarySelectorServiceTests`, `SmartHoverEngineStringTests`, `HoverCoordinatorTests`, `HoverCoordinatorHostApiTests` (внутри «Hover coordinator ordering and lifecycle»), `RankerWorkerRegressionTests`, `RankerProviderTraitsTests`, `GlossBertDecisionCacheTests`, `SmartDictionaryResultCacheTests`, `ConsecBundleInfrastructureTests`, `BuildScriptSafetyTests`, `MagpieSafetyTests`, `TextHookSafetyTests`, `DpiCompatibilityServiceTests` | встроенный Windows PowerShell (`System32\WindowsPowerShell\v1.0\powershell.exe`): им запускаются поддельный воркер и проверяемые скрипты. Модели не нужны |
| Реальный движок | «Installed effects» (эффекты всех пресетов на месте) и «Engine lifecycle» (запуск и остановка Magpie с временным `config.json` и его резервной копией) | конец default-пути `Program.cs` | установленный Magpie 0.12.1 |
| Gated-бенчмарки | качество, покрытие и задержку на открытых и замороженных наборах через собранную публикацию | режимы из таблицы ниже, только по явному аргументу | `workerBaseDir` с воркером и моделями; для `npu` — AMD NPU; для `--translation-suite` — установленная TranslateGemma и скачанные наборы |

На macOS из этих слоёв выполняются чистая логика (без WPF-помощников),
контракты данных — с `python3` — и процессы/IPC: поддельные воркеры там
написаны на `python3` и `/bin/sh` вместо Windows PowerShell. Кроме того, только на
macOS четыре проверки запускают CoreHost отдельным процессом (раздел «CoreHost»
выше), а набор Swift Testing проверяет клиент протокола (с командами
наблюдений Apple Vision), супервизор CoreHost, блокировку экземпляра и
очистку осиротевшей группы приложения с поддельным и настоящим CoreHost.
Установщики,
`build`-скрипты, Magpie, text-hook, DPI, реальный движок и gated-бенчмарки на
macOS не выполняются (`skip` или не компилируются).

Сознательно не покрыто default-набором:

- **WPF UI.** Окна и оверлеи не создаются; `App.xaml` и `AsteriaScale.csproj`
  проверяются только как XML.
- **Живой NPU OCR.** Проверяются текстовые правила OCR в ядре
  (`OcrTextRules`: извлечение предложения, починка mixed-script, согласие двух
  наблюдений, повторное использование неизменившегося кадра) и пиксельная
  геометрия OCR в ядре (`OcrGeometry`: выбор строки и слова у курсора, сборка
  предложения из строк, границы захвата, группировка блоков); и то и другое
  выполняется и на macOS.
  Распознавание Windows AI запускают только `--ocr-image` и
  `--screen-ocr-image`.
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

Python-инструменты имеют шесть собственных наборов на `unittest` (`76` тестов).
Запускай соответствующий после правки скрипта — все шесть работают без сети и
без внешних пакетов; единственный тест, которому нужен `pymorphy3`
(лемматизация в `sense-gate-early-exit.py`), без него пропускается:

```powershell
python scripts/test-build-dictionaries.py            # генератор словарей (16)
python tools/test-translation-benchmark.py           # tools/translation-benchmark.py (18)
python tools/test-sense-gate-early-exit.py           # tools/sense-gate-early-exit.py (8)
python tools/tests/test_fetch_benchmark_datasets.py  # tools/fetch-benchmark-datasets.py (11)
```

На macOS те же команды запускаются как `python3 …`. Пятый набор — у
macOS-инструмента аудита переноса (ему нужен `git` в `PATH`), шестой — у
списка G-crash, по которому `run-macos-subset.sh`, `test-swift.sh` и
`build-app.sh` ищут новые отчёты о сбоях (отчёты в нём синтетические, во
временной папке; настоящая `~/Library/Logs/DiagnosticReports` не читается):

```bash
python3 tools/macos/test-audit-verbatim-move.py      # tools/macos/audit-verbatim-move.py (18)
python3 tools/macos/test-crash-reports.py            # tools/macos/crash-reports.py (5)
```

C#-smoke эти наборы не запускает, но сам вызывает `python` для части `tools/`:
`TranslationSuiteTests` прогоняет `tools/translation-benchmark.py score` на
output C#-runner, `AtomicOutputSafetyTests` — `tools/atomic_output.py`. Без `python` в `PATH`
полный smoke падает. macOS-подмножество вызывает вместо него `python3` (через
него же работают поддельные воркеры `GlossBertDictionarySelectorServiceTests`,
`SmartHoverEngineStringTests`, `HoverCoordinatorTests` и
`HoverCoordinatorHostApiTests`).

`tools/tests/` намеренно без `__init__.py`, поэтому файл запускается напрямую, а
не через `unittest discover`.

## Стиль кода

- C# 14 — версия языка по умолчанию для .NET 10 SDK: `LangVersion` не задан ни в одном `.csproj`, `Directory.Build.props` нет. File-scoped namespaces, `sealed` по умолчанию, records для DTO.
- Комментарии — по-английски и только про «почему» (инвариант, который код не показывает), не про «что делает строка».
- Строковые сравнения — явные: `StringComparer.Ordinal` для ключей, `OrdinalIgnoreCase` для путей/имён файлов.
- Ошибки внешних подсистем не роняют hover-путь: словари, кэш и диагностика деградируют мягко (см. существующие catch-блоки — они узкие и мотивированные).
- Новые зависимости NuGet — только при реальной необходимости; проект намеренно офлайн и локальный.
