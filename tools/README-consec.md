# Локальная конвертация ConSeC WNGT

`export-consec-onnx.py` — исследовательский foundation для безопасной конвертации
официального `ConSeC-SemCor+WNGT` в inference-only ONNX. Отдельные
`install-consec.cmd` / `verify-consec.ps1` устанавливают проверенный результат
в локальный CPU runtime Asteria; checkpoint и bundle по-прежнему не входят в
git, обычную установку или публичную поставку.

Источники закреплены:

- ConSeC: `SapienzaNLP/consec`, commit
  `9602b5fd69f57be08a186988d1df34fe4152b63f`, лицензия
  CC BY-NC-SA 4.0;
- checkpoint `consec_wngt_best.ckpt`: 4 864 940 005 байт, SHA-256
  `92421ed245723964db09ce396f19a0d1e55fe4d6e10d5ecb83278d9bc70ce8af`;
- base config/tokenizer: `microsoft/deberta-large`, revision
  `a97e054da5f34feed3d26951db4a25831dfcb486`.

Из-за ограничения `NonCommercial` полученный bundle нельзя включать в обычную
публичную/коммерческую поставку. Для личного научного исследования он должен
оставаться локальным; при распространении также действуют Attribution и
ShareAlike. Скрипт не скачивает модель автоматически и не добавляет большие
файлы в git.

## Окружение

```powershell
python -m venv "$env:LOCALAPPDATA\AsteriaScale\Tooling\ConsecOnnx-v1"
& "$env:LOCALAPPDATA\AsteriaScale\Tooling\ConsecOnnx-v1\Scripts\python.exe" `
  -m pip install -r tools/requirements-consec-export.txt
```

Для экспорта не нужен `pytorch_model.bin`: веса берутся из checkpoint. Нужны
только четыре маленьких файла закреплённой ревизии base model:
`config.json`, `tokenizer_config.json`, `vocab.json`, `merges.txt`. Например:

```powershell
& "$env:LOCALAPPDATA\AsteriaScale\Tooling\ConsecOnnx-v1\Scripts\huggingface-cli.exe" `
  download microsoft/deberta-large `
  --revision a97e054da5f34feed3d26951db4a25831dfcb486 `
  --include config.json tokenizer_config.json vocab.json merges.txt `
  --local-dir "$env:LOCALAPPDATA\AsteriaScale\Models\deberta-large-metadata"
```

Экспортёр независимо сверяет размер и SHA-256 каждого из этих файлов, поэтому
похожий, но другой snapshot будет отклонён.

## Безопасная инспекция

Сначала можно проверить checkpoint без ONNX-экспорта:

```powershell
$upstream = Join-Path $env:TEMP "asteria-consec-upstream"
$python = "$env:LOCALAPPDATA\AsteriaScale\Tooling\ConsecOnnx-v1\Scripts\python.exe"
& $python tools/export-consec-onnx.py `
  --upstream-directory $upstream `
  --checkpoint (Join-Path $upstream "consec_wngt_best.ckpt") `
  --inspect-only
```

До десериализации скрипт проверяет commit upstream, лицензию, размер и хеш
checkpoint, а затем статически сравнивает все pickle globals с закрытым
allowlist. Загрузка выполняется только `torch.load(weights_only=True,
mmap=True)`. Lightning callback и OmegaConf metadata восстанавливаются в
инертные классы с точными qualname; `src.pl_callbacks`, Hydra, OmegaConf и
PyTorch Lightning не импортируются и не нужны.

Инспекция fail-closed проверяет 390 state entries, 405 372 416 tensor elements,
embedding `50468 x 1024`, 24 слоя и ключевые параметры официальной
relative-position конфигурации.

## Экспорт

```powershell
& $python tools/export-consec-onnx.py `
  --upstream-directory $upstream `
  --checkpoint (Join-Path $upstream "consec_wngt_best.ckpt") `
  --base-model-directory "$env:LOCALAPPDATA\AsteriaScale\Models\deberta-large-metadata" `
  --output "$env:LOCALAPPDATA\AsteriaScale\Models\Consec-WNGT-ONNX"
```

Каталог `--output` обязан отсутствовать: сборка идёт в соседнем staging и
публикуется только после всех проверок. Результат:

- `Cpu/consec-wngt-fp32.onnx` — граф с динамическими batch/sequence axes;
- `Cpu/consec-wngt-fp32.onnx.data` — единый external-data файл весов;
- `tokenizer.json` — tokenizer с точными ID `<d>`, `</d>`, `DEF_SEP`,
  `DEF_END`, `CONTEXT_DEFS`;
- `model-manifest.json` — детерминированный manifest с source/artifact hashes.

Перед публикацией скрипт сравнивает PyTorch и ONNX Runtime на двух fixtures:
диалоговом примере с feedback definition и научном примере с аббревиатурой
`PCR`, несколькими смыслами `strain` и двумя feedback definitions. Вторая
fixture одновременно проверяет динамическую длину ONNX. Допуск max absolute
delta — `1e-4`.

### Граница доказательства

Текущая автоматическая parity означает следующее:

1. адаптированный inference-only PyTorch extractor совпадает с ONNX Runtime;
2. ONNX-friendly fixed-span relative attention сравнивается с **stock
   Transformers 4.46.3 attention** на обеих fixtures (допуск `1e-6`; в
   проверенном полном экспорте delta была `0.0`). Fixed span нужен потому, что
   legacy ONNX trace иначе замораживает Python slice на длине первой fixture;
3. проверенный полный экспорт дал max delta `3.10e-6` и `5.01e-6` для ONNX.

Сам по себе этот этап не доказывал end-to-end parity с официальным upstream на
его исходном `transformers==4.3.3`: exporter намеренно не импортирует upstream
runtime, Hydra или Lightning. Поэтому до runtime был выполнен отдельный
reference gate из следующего раздела. Одинаковые sentence
tokens/candidate definitions/feedback дали одинаковые input hashes и choices,
а полные candidate logits совпали в пределах `5.01e-6`. Научная fixture
содержит `PCR DNA` и отдельно закрепляет точное понижение регистра хвоста через
официальный Python `str.capitalize()`, включая порядок до `strip('.')` и
сохранение ведущих пробелов. Runtime гарантирует это для английского
ASCII-регистра, сохраняет уже строчные Unicode-буквы хвоста без изменения,
а требующие Unicode-преобразования случаи вне воспроизводимого Python 3.9
domain отклоняет fail-closed. В уже собранном
manifest исторически остаётся
`officialUpstreamEndToEndReferenceVerified: false`; внешний golden связывает
успешный gate именно с этим manifest и не требует задним числом менять артефакт.

## Official-upstream reference gate

Обязательный gate реализован отдельно в `run-consec-upstream-reference.py`.
Он запускает **исходные** `DeBERTaTokenizer.power_tokenize`,
`ConsecDebertaModel` и `DebertaPositionalExtractor` из закреплённого commit,
сначала сверяет точные input tensor hashes, затем весь вектор logits и выбор
кандидата с готовым ONNX. Ни Hydra, ни Lightning, ни их небезопасный
`load_from_checkpoint` не импортируются.

Reference-env намеренно исторический и отдельный от exporter-env:

- Python `3.9.13` (последний обычный Windows installer ветки 3.9);
- `transformers==4.3.3`, `tokenizers==0.10.3` — зависимости официального
  ConSeC;
- современный `torch==2.5.1+cpu` нужен только ради fail-closed
  `weights_only=True` и `mmap=True`;
- `onnxruntime==1.19.2` — последняя доступная Windows wheel для Python 3.9;
  она читает тот же opset-17 external-data graph, а её результаты дополнительно
  сверяются с logits, записанными exporter-env на ORT 1.20.1.

Создание окружения (нужен именно Python 3.9.13):

```powershell
$referenceRoot = "$env:LOCALAPPDATA\AsteriaScale\Tooling\ConsecReference-v1"
& "C:\Path\To\Python39\python.exe" -m venv $referenceRoot
$referencePython = Join-Path $referenceRoot "Scripts\python.exe"
& $referencePython -m pip install -r tools/requirements-consec-reference.txt
```

Старый tokenizer читает не `vocab.json/merges.txt`, а дополнительный
`bpe_encoder.bin`. Нужен файл той же ревизии base model:

```powershell
& "$env:LOCALAPPDATA\AsteriaScale\Tooling\ConsecOnnx-v1\Scripts\huggingface-cli.exe" `
  download microsoft/deberta-large `
  --revision a97e054da5f34feed3d26951db4a25831dfcb486 `
  --include bpe_encoder.bin `
  --local-dir "$env:LOCALAPPDATA\AsteriaScale\Models\deberta-large-metadata"
```

Gate закрепляет для него 3 917 897 байт и SHA-256
`e7c6f9eecb461c01e09c00656ccf3e27944b9e74bfe29e51632b13d3cd9d6c8e`.
Это существенно: `transformers 4.3.3` внутри tokenizer вызывает legacy
`torch.load` без безопасного режима. Runner перехватывает ровно этот один
вызов, принимает только закреплённый путь и вызывает настоящий loader с
`weights_only=True`; любое другое обращение аварийно завершает gate.

Запуск:

```powershell
$upstream = Join-Path $env:TEMP "asteria-consec-upstream"
$baseModel = "$env:LOCALAPPDATA\AsteriaScale\Models\deberta-large-metadata"
$bundle = "$env:LOCALAPPDATA\AsteriaScale\Models\Consec-WNGT-ONNX"
& $referencePython tools/run-consec-upstream-reference.py `
  --upstream-directory $upstream `
  --checkpoint (Join-Path $upstream "consec_wngt_best.ckpt") `
  --base-model-directory $baseModel `
  --bundle $bundle `
  --report "$env:TEMP\consec-upstream-reference.json"
```

Runner fail-closed проверяет чистоту пяти используемых upstream-файлов против
Git HEAD, все source/checkpoint/base/bundle hashes, закрытый allowlist pickle
globals, 390 state entries, 405 372 416 tensor elements и строгую совместимость
всех ключей/форм. Официальная модель создаётся на `meta` сразу с финальным
embedding `50 468 x 1 024`: это эквивалент официального `+203` resize, но не
выделяет и затем не перезаписывает 1,6 ГБ случайных base weights.

Проверенный результат для bundle с manifest SHA-256
`13a8a14e3938d4f20519f7da79a51f75543eced352bd39456b5a43c4d93402b8`:

| Fixture | Input SHA совпал | Max delta всех logits | Max delta кандидатов | Выбор |
|---|---:|---:|---:|---:|
| dialogue feedback / `bank` | да | `3.10e-6` | `2.38e-6` | `1 == 1` |
| scientific PCR / `strain` | да | `5.01e-6` | `3.81e-6` | `0 == 0` |

Оба результата намного ниже допуска `1e-4`; текущий ONNX на ORT 1.19.2 также
в точности (`0.0`) воспроизвёл candidate logits, записанные ORT 1.20.1.
Проверенный отчёт хранится в `consec-upstream-reference-golden.json`. Он
привязан к хешу конкретного manifest: после новой конвертации gate надо
перезапустить, а golden заменить. SHA-256 текущего LF-normalized golden:
`d6a5a3cdf6429bf4acde6bba5a254c209eff28f76c577ad7d845a7ba086ab22e`.
Булево `false` внутри уже собранного
foundation-manifest не переписывается задним числом; доказательством для него
служит этот внешний, связанный с ним хешами отчёт.

## Установка и runtime

Для личного научного/некоммерческого использования прочитайте CC BY-NC-SA 4.0
и запустите отдельный установщик. Интерактивный режим требует явного принятия;
флаг допустим только после самостоятельного ознакомления с условиями:

```powershell
.\scripts\install-consec.cmd `
  -SourceBundle "$env:LOCALAPPDATA\AsteriaScale\Models\Consec-WNGT-ONNX"
# либо для уже подтверждённых условий:
.\scripts\install-consec.cmd -AcceptUpstreamTerms `
  -SourceBundle "$env:LOCALAPPDATA\AsteriaScale\Models\Consec-WNGT-ONNX"

powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\verify-consec.ps1
```

Установщик сверяет точные размеры/SHA, лицензию, adapter identity и структуру
manifest; запрещает reparse/traversal, собирает staging рядом с назначением и
заменяет bundle транзакционно. Локальный ignored-каталог:
`src/AsteriaScale.RankerWorker/Models/Consec`. Если он присутствует и прошёл
проверку, build/publish условно включает его в локальную публикацию; отсутствие
ConSeC не делает обычную GlossBERT-сборку неполной.

Worker запускает профиль только явно:

```powershell
dotnet run --project src/AsteriaScale.RankerWorker/AsteriaScale.RankerWorker.csproj `
  -c Release --no-build -- --provider cpu --cpu-model consec `
  --model-directory src/AsteriaScale.RankerWorker/Models --smoke
```

Pipeline — `consec-independent`: полное предложение остаётся неизменённым,
worker размечает точный target по anchor index, independently оценивает его
группы и применяет общий margin `0.25`. NPU/fallback и silent truncation нет;
последовательность длиннее 512 токенов отклоняется. Exact model/cache identity:

```text
ConSeC:WNGT:9602b5fd:adapter-consec-wngt-positional-extractor-v1:g-0eb0688f3fd2:d-1257dc55ebec:t-df91c443c8ea:pretokenizer-v3:context-full-v1:s512:pipeline-consec-independent
```

## Open-dev результат

Воспроизводимый запуск одного профиля:

```powershell
dotnet run --project tests/AsteriaScale.Tests/AsteriaScale.Tests.csproj `
  -c Release --no-build -- --benchmark-only `
  --ranker-sentence-benchmark assets/Ranker/ranker-sentence-benchmark.json `
  dist/AsteriaScale --ranker-cpu-model consec `
  --sentence-report artifacts/Ranker/consec-sentence-report.json
```

На same-build sentence benchmark (`46` предложений, `138` целей) GlossBERT,
SANDWiCH и ConSeC дали соответственно `91`, `120` и `131` правильную группу;
на научном ядре `69/108`, `96/108`, `106/108`; все смыслы предложения верны в
`20`, `31`, `41` случаях; на surface-unambiguous WSD-срезе — `67`, `86`, `94`.
Последний показатель всё ещё сравнивает sense GroupId, а не сгенерированный
русский текст. У ConSeC accepted precision `96,32 %`, coverage `98,55 %`, warm
p50/p95 `140/205 мс`, cold start `6 420 мс`, peak `1 500 МБ`;
ошибок и sequence overflow не было.

Остаток принципиален: одна context-only contrast pair не разрешилась, а пять
ошибок были уверенно приняты. Поэтому UI уже предлагает
`ConSeC — лучший для науки, CPU`, но GlossBERT остаётся стабильным default.
Frozen `ranker-holdout*.json` при этой работе не открывались. Дальнейшая
история профиля — downstream A/B влияния evidence на TranslateGemma,
sense-preservation gate и promotion gate на `replication199` (не пройден) — и
его текущий статус ведутся в №8 [docs/ROADMAP.md](../docs/ROADMAP.md);
feedback/joint planner пока не реализован.

Нужен существенный запас ресурсов: checkpoint mmap занимает около 4.9 ГБ на
диске, raw ONNX временно соседствует с external-data bundle, а экспорт/проверка
могут потребовать несколько гигабайт RAM. Полученные файлы больше 100 МБ нельзя
добавлять в git.
