# Обучение WSD/DeBERTa на RTX 5080

Этот ZIP содержит только воспроизводимый пакет обучения ранжировщика значений: trainer, launcher, закреплённые зависимости и канонический train/valid-набор. В нём нет весов моделей, checkpoints и `ranker-holdout*.json`.

Данные подготовлены из English Wiktionary через Kaikki/wiktextract (dump `2026.07.06`, extraction `2026.07.16`) и наследуют условия CC BY-SA/GFDL. Точные источники, revisions, размеры и SHA-256 находятся в `package-manifest.json`.

## 1. Проверка пакета

Сначала сверьте SHA-256 ZIP со значением, выданным упаковщиком. Распакуйте архив на локальный фиксированный NTFS-диск, не в OneDrive, сетевой каталог, junction или symlink. Все команды выполняются из корня распакованного пакета.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\run-wsd-ranker-rtx5080.ps1 -VerifyOnly
```

Эта команда не требует Python/GPU и не запускает обучение. Launcher повторно проверяет schema, профили, suites, размеры и SHA-256 каждого файла.

## 2. Окружение

Нужны 64-битный CPython `3.12.10`, NVIDIA driver с поддержкой CUDA 13.0 и RTX 5080 (compute capability `12.0`). CUDA Toolkit отдельно не нужен.

```powershell
py -3.12 -m venv .venv
$python = (Resolve-Path '.\.venv\Scripts\python.exe').Path
& $python -m pip install --no-deps --only-binary=:all: --require-hashes -r .\tools\requirements-wsd-bootstrap.txt
& $python -m pip install --no-deps --only-binary=:all: --require-hashes -r .\tools\requirements-wsd-torch-cu130.txt
& $python -m pip install --no-deps --only-binary=:all: --require-hashes -r .\tools\requirements-wsd-ranker.txt
```

Launcher ничего не устанавливает. Перед запуском он проверяет версии, CUDA, BF16 и выполняет настоящую BF16-операцию на GPU. CPU fallback запрещён.

## 3. Что запускать

Все профили используют один честный baseline: `6` эпох, `3006` optimizer steps, warmup `10%` и линейный спад LR `2e-5 → 0`. Это важно: прежняя четырёхэпохная матрица отменена после подтверждения, что пользу дают и более пологая траектория LR, и дополнительные шаги.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\run-wsd-ranker-rtx5080.ps1 -ListProfiles
```

Наборы не пересекаются, поэтому их можно запускать последовательно без повторной работы:

```powershell
# Воспроизводимый base7 baseline с новым групповым отчётом
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\run-wsd-ranker-rtx5080.ps1 -RunReferenceSuite

# Главный следующий тест: вклад NLI encoder и projected head при 7 слоях
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\run-wsd-ranker-rtx5080.ps1 -RunNextSuite

# Чистый эффект ёмкости обычного DeBERTa
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\run-wsd-ranker-rtx5080.ps1 -RunCapacitySuite

# NLI × depth, уже включён в пакет на случай хороших первых результатов
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\run-wsd-ranker-rtx5080.ps1 -RunInteractionSuite
```

Рекомендуемый минимум для первого захода: `ReferenceSuite` и `NextSuite` — три запуска, ориентировочно около часа по скорости предыдущего опыта. `CapacitySuite` и `InteractionSuite` можно выполнить в тот же визит, но они не запускаются автоматически.

Suffix `s1/s2/s3` означает seed `20260727/20260728/20260729`. Дополнительные seeds уже перечислены в пакете, но их запускают вручную только для финалиста и seed-matched `base7-reset`:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\run-wsd-ranker-rtx5080.ps1 -Profile nli7-projected-s2
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\run-wsd-ranker-rtx5080.ps1 -Profile base7-reset-s2
```

При прерывании продолжайте только тот же профиль и явно укажите существующий output:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\run-wsd-ranker-rtx5080.ps1 -Profile nli7-reset-s1 -Resume -OutputDirectory '.\artifacts\runs\<имя-запуска>'
```

Не открывайте и не переносите на RTX-машину `assets/Ranker/ranker-holdout*.json`.

## 4. Что вернуть

Для каждого запуска верните целиком:

- `<output>\bundle\GlossDeberta`;
- `metrics.jsonl`, `run-state.json`, `experiment-profile.json`;
- `validation-predictions.jsonl` и `validation-predictions-manifest.json`;
- все `training-*.log` и `pip-freeze-*.txt`.

`validation-predictions.jsonl` не содержит исходных предложений: только стабильные group ID, хеш lemma-кластера, scores, gold/predicted indices, margin и попарные ответы. Он нужен для McNemar и paired lemma-cluster bootstrap. Checkpoints и `best` оставьте на RTX-машине до приёмки.

Полный порядок экспериментов и критерии остановки находятся в `EXPERIMENT-ROADMAP.md`.
