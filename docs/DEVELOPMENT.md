# Разработка и сборка

## Требования

| Компонент | Версия | Обязательно |
|---|---|---|
| Windows 11 | build 26100+ (для NPU OCR) | да |
| .NET SDK | 10.0 | да |
| [Magpie](https://github.com/Blinue/Magpie) | 0.12.1 | да (движок апскейла, ставится отдельно) |
| Windows AI Text Recognition | из Windows App SDK | для OCR-перевода |
| Foundry Local + `qwen3.5-2b-text-generic-cpu:1` | 0.10.2 | только для полного/экранного перевода |
| AMD NPU + драйвер | 32.0.203.329+ | опционально (NPU-путь GlossBERT) |
| Python | 3.11+ | только для регенерации словарей |

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
| `--foundry-live` | Живой Qwen: перевод и выгрузка по простою |
| `--smart-live` / `--smart-gpu-live` | Живой умный словарь end-to-end |
| `--ocr-image <png> <x> <y>` | Диагностика hover-OCR по изображению |
| `--screen-ocr-image <png>` | Диагностика экранного OCR |
| `--ranker-benchmark <set.json> <workerBaseDir> [cpu\|npu]` | Бенчмарк GlossBERT через полный продакшн-конвейер |
| `--translation-benchmark <set.json> [engine] [--ranker-base <dir>] [--dump <file>] [--benchmark-only]` | Бенчмарк полного перевода: chrF, терминологические якоря, остаточный английский, латентность и scaffold/GlossBERT-диагностика. Требует живой Foundry Local; `--benchmark-only` не запускает default smoke/Magpie |

`workerBaseDir` — каталог, содержащий `Ranker\AsteriaScale.RankerWorker.exe` (удобно сделать junction на bin-выход воркера). Наборы: `assets/Ranker/ranker-benchmark.json` (dev) и `ranker-holdout-v2.json` (замороженный — только финальные подтверждения, каждое открытие фиксируется в [RESEARCH.md](RESEARCH.md)).
Команда завершается с ошибкой при отсутствии ранжируемых/размеченных решений или при точности принятых решений ниже `90 %`; это CI-gate результата, не дополнительный критерий воздержания модели.

Наборы полного перевода: `assets/Translation/translation-benchmark.json` (dev, 64 реплики) и `translation-holdout-v1.json` (замороженный, 30 реплик — правило то же). В dev-наборе 61 **term anchor**, а не 61 случай: chrF считается на всех 64 репликах, включая 15 реплик без терминологического якоря. Смоук-тест «Benchmark set schema» читает и проверяет только dev-наборы `ranker-benchmark.json` и `translation-benchmark.json`: default smoke никогда не открывает frozen holdout. Holdout передаётся лишь явно в gated-команду для одноразового финального подтверждения, и каждое такое открытие фиксируется в `RESEARCH.md`.

`--translation-benchmark` всегда падает на пустом наборе или пустом ответе. Dump записывается до quality gates и содержит SHA-256 набора, точные engine/model/runtime, общие и per-language агрегаты. Эталонный dev-прогон:

```powershell
foundry model download qwen3.5-2b-text-generic-cpu:1
dotnet run --project tests/AsteriaScale.Tests/AsteriaScale.Tests.csproj -c Release --no-build -- --translation-benchmark assets/Translation/translation-benchmark.json qwen --dump artifacts/translation-qwen-cpu-baseline.json --benchmark-only
dotnet run --project tests/AsteriaScale.Tests/AsteriaScale.Tests.csproj -c Release --no-build -- --translation-benchmark assets/Translation/translation-benchmark.json qwen-scaffold --dump artifacts/translation-qwen-scaffold.json --benchmark-only
dotnet run --project tests/AsteriaScale.Tests/AsteriaScale.Tests.csproj -c Release --no-build -- --translation-benchmark assets/Translation/translation-benchmark.json qwen-scaffold-glossbert-cpu --ranker-base dist/AsteriaScale --dump artifacts/translation-qwen-scaffold-glossbert-cpu.json --benchmark-only
```

`qwen` сохраняет исходный prompt без каркаса; `qwen-scaffold` включает только детерминированный production-каркас, exact-sentence bypass и fidelity retry. `qwen-scaffold-glossbert-cpu` и `qwen-scaffold-glossbert-npu` запускают тот же полный production-конвейер с явно выбранным провайдером GlossBERT; для них `--ranker-base` обязан указывать на каталог, содержащий `Ranker\AsteriaScale.RankerWorker.exe`. Имя движка намеренно фиксирует провайдер: NPU-запрос никогда не повторяется на CPU. Принятый GlossBERT-смысл сохраняется как мягкая метаинформация, а не как обязательная русская лексема: `neuralHintsEmitted` считает созданные English sense hints, `neuralTermsEmitted` — защитный аудит и обязан оставаться нулём. В первый prompt hints не попадают; лишь явный surface-prefilter при отсутствии mandatory-ошибок запускает отдельный English `KEEP`/`FIX` review Qwen. Schema v3 сохраняет его outcomes (`SenseReviewKeeps`, `SenseReviewCorrections`, `SenseReviewRejected`, `SenseReviewFailures`; rejected включает невалидные ответы и prompt echo), каждый neural-исход (`selected`, `abstained`, `error`, `skipped`), причину пропуска (включая `session-disabled-after-error`), provider/device/model/pipeline и задержку. Именованный GlossBERT-прогон завершается ошибкой после записи dump, если не сделал ни одного ranker-запроса, получил хотя бы одну ошибку, создал mandatory neural-термин, не создал ни одного sense hint или не дошёл ни до одного Qwen sense review; калиброванное воздержание ошибкой не считается. Benchmark создаёт пустой временный user-словарь, поэтому личные термины разработчика не влияют на A/B. Quality gates включаются переменными окружения `ASTERIA_TRANSLATION_MIN_CHRF`, `ASTERIA_TRANSLATION_MIN_ANCHOR_RATE` и `ASTERIA_TRANSLATION_MAX_RESIDUAL_ENGLISH`; значения задаются от `0` до `1` с точкой. Для текущего CPU-baseline контрольные пороги — `0.50`, `0.85` и `0.01` соответственно. По умолчанию они остаются отключёнными, чтобы default smoke не требовал живую модель.

## Публикация

```powershell
./scripts/build.cmd    # publish в dist/AsteriaScale + Ranker + подпись identity-пакета
./scripts/install.cmd  # ярлык и автозапуск
./scripts/run.cmd      # запуск без установки
```

`build.ps1` подписывает MSIX identity-пакет сертификатом `CN=Asteria Scale Local` из `Cert:\CurrentUser\My`. На новой машине создайте его один раз:

```powershell
New-SelfSignedCertificate -Type Custom -Subject "CN=Asteria Scale Local" `
  -KeyUsage DigitalSignature -CertStoreLocation "Cert:\CurrentUser\My" `
  -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
```

Identity-пакет нужен Windows AI API (NPU OCR): без него `TextRecognizer` недоступен обычному Win32-процессу.

Публикация сначала собирает и проверяет приложение, Ranker с обоими наборами моделей и подписанный identity-пакет
в уникальном staging-каталоге внутри `dist`. Рабочие `dist/AsteriaScale` и `AsteriaScale.Identity.msix`
заменяются только после всех проверок; при ошибке прежняя публикация остаётся на месте. `run` и `install`
сверяют `.build-revision` с Git HEAD и `.build-source-fingerprint` с манифестом путей, содержимого и удалений входных файлов, поэтому устаревший `dist` пересобирается даже в dirty worktree.
Запущенное из трея приложение всё равно следует закрыть перед сборкой: Windows может удерживать файлы старой публикации.

## Регенерация морфословаря

`assets/Morphology/ru-morph.tsv.gz` (глагольные инфинитивы и прошедшее время
OpenCorpora) собирается так:

```powershell
pip install pymorphy3 pymorphy3-dicts-ru
python scripts/build-ru-morphology.py --output assets/Morphology/ru-morph.tsv.gz
```

## Регенерация словарей

Скачайте источники в `tools/dictionary-source` (ссылки в [assets/Dictionaries/README.md](../assets/Dictionaries/README.md)) и выполните команду из того же README. Критично: `normalize_english` в генераторе и `NormalizeEnglish`/`NormalizeEnglishSentenceKey` в `LocalDictionaryService` обязаны давать одинаковую каноническую форму ключей — при изменении одной стороны меняйте вторую и добавляйте тест в `LocalDictionaryNormalizationTests`.

## Стиль кода

- C# 13, file-scoped namespaces, `sealed` по умолчанию, records для DTO.
- Комментарии — по-английски и только про «почему» (инвариант, который код не показывает), не про «что делает строка».
- Строковые сравнения — явные: `StringComparer.Ordinal` для ключей, `OrdinalIgnoreCase` для путей/имён файлов.
- Ошибки внешних подсистем не роняют hover-путь: словари, кэш и диагностика деградируют мягко (см. существующие catch-блоки — они узкие и мотивированные).
- Новые зависимости NuGet — только при реальной необходимости; проект намеренно офлайн и локальный.
