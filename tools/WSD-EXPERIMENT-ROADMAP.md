# Roadmap экспериментов WSD/DeBERTa

Документ входит в переносимый RTX-пакет. Все допустимые архитектурные прогоны заранее перечислены в launcher: для них не потребуется редактировать код или заново передавать dataset. Продвижение модели в production остаётся отдельным решением после GPU-обучения, CPU-бенчмарков и product-dev проверки.

## Подтверждённый baseline и вывод о schedule

Внешний прогон `wsd-deberta-base7-rtx5080-20260727-232009` использовал `microsoft/deberta-v3-base`, первые 7 слоёв, reset-head, seed `20260727`, полный канонический набор. При одинаковых первых `2004` шагах были сопоставлены три LR-траектории:

| Траектория | LR@1000 | LR@2000 | Верных групп | Group acc | Pair acc | Loss |
|---|---:|---:|---:|---:|---:|---:|
| штатная 4-эпохная, спад в 0 | 1.11e-5 | 4.4e-8 | 822 | 75.48% | 74.31% | 0.7115 |
| 4 эпохи, floor 30% | 1.38e-5 | 6.0e-6 | 828 | 76.03% | 75.73% | 0.6938 |
| форма 6-эпохного schedule, остановка на шаге 2004 | 1.48e-5 | 7.4e-6 | 835 | 76.68% | 74.76% | 0.6867 |
| полный 6-эпохный, 3006 шагов | — | — | **843** | **77.41%** | **76.10%** | — |

Результаты пока считаются предварительно сообщёнными: их artifacts ещё должны пройти приёмку. Однако вывод для дизайна уже однозначен: пользу дали и более медленный спад, и дополнительные `1002` шага; длительность дала большую часть разницы. Поэтому новый общий baseline — полный шестиепохный schedule. Сравнивать с ним четырёхэпохные архитектуры нельзя.

Старый trainer `group-aware-v2.2` имел SHA-256 `74fe5f7d145676164c24efb9fc815dc126011a349b4e37152cc9440b0ae43605`. Текущий trainer сохраняет ту же логику обучения, но добавляет text-free групповой отчёт и имеет новый SHA-256 в manifest пакета. Resume между этими trainer запрещён подписью запуска.

## Общий неизменяемый профиль

- dataset: `32 019` train groups / `1 089` valid groups, SHA-256 из manifest;
- 6 эпох / 3006 optimizer steps;
- linear schedule `2e-5 → 0`, warmup 10%;
- hybrid group-aware loss, coefficient `0.25`;
- FP32 параметры, BF16 autocast, strict deterministic CUDA;
- groups per batch `8`, gradient accumulation `8`, workers `0`;
- selection: `group_accuracy`, ties считаются ошибками;
- runtime abstention остаётся только margin `0.25` и в обучении не настраивается.

Меняются только source checkpoint, encoder depth, head initialization и seed.

## Полная матрица в ZIP

| Семейство | Source | Слои | Head | Что измеряет |
|---|---|---:|---|---|
| `base7-reset` | `microsoft/deberta-v3-base` | 7 | reset | воспроизводимый baseline и seed-matched reference |
| `nli7-reset` | `cross-encoder/nli-deberta-v3-base` | 7 | reset | эффект NLI-finetuned encoder |
| `nli7-projected` | тот же NLI checkpoint | 7 | projected | дополнительный эффект NLI-head |
| `base9-reset` | plain base | 9 | reset | чистый эффект ёмкости 7→9 |
| `base12-reset` | plain base | 12 | reset | quality ceiling plain base |
| `nli9-reset/projected` | NLI base | 9 | оба | взаимодействие NLI и средней глубины |
| `nli12-reset/projected` | NLI base | 12 | оба | NLI quality ceiling / teacher candidate |

Каждое семейство имеет `s1`, `s2`, `s3`. Итого `27` runnable profiles; наличие профиля не означает, что его нужно запускать.

## Этап 1 — ближайший правильный тест

Запустить непересекающиеся `reference` и `next` suites:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\run-wsd-ranker-rtx5080.ps1 -RunReferenceSuite
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\run-wsd-ranker-rtx5080.ps1 -RunNextSuite
```

Получатся `base7-reset-s1`, `nli7-reset-s1`, `nli7-projected-s1` с одинаковым schedule, seed и group order. Это разделяет три эффекта: plain baseline → NLI encoder → projected head. Повторный base7 нужен не ради процента, а ради нового per-group отчёта и полной воспроизводимости сравнения.

Первичный кандидат должен выиграть у base7 не только по aggregate group accuracy, но и по paired outcome. Прирост меньше `1.0` п.п. (`≈11` групп) рассматривается как ничья до confirmation; при ничьей предпочтителен меньший/быстрый вариант. Pair accuracy и loss — диагностические, а не основная selection-метрика.

## Этап 2 — ёмкость и interaction

Если нужен полный architecture screen за один визит, отдельно выполнить:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\run-wsd-ranker-rtx5080.ps1 -RunCapacitySuite
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\run-wsd-ranker-rtx5080.ps1 -RunInteractionSuite
```

Suites не пересекаются. `capacity` отвечает, помогает ли глубина plain base. `interaction` не даёт ошибочно переносить вывод о projected head с 7 слоёв на 9/12. Base12 — прежде всего quality ceiling; даже высокий validation score не делает его production-кандидатом без CPU gate.

Остановить ветку, если:

- увеличение глубины не даёт хотя бы `1.0` п.п.;
- новый source/head ухудшает group accuracy и paired баланс ошибок;
- 7-слойный вариант уже не проходит CPU budget — тогда 9/12 не рассматриваются для production, только как teacher.

## Этап 3 — paired статистика

Каждый новый run сохраняет `validation-predictions.jsonl` и закрепляющий его manifest. На одном seed выполнить:

1. exact McNemar по `correct` для одинаковых `groupId`;
2. paired bootstrap разницы group accuracy, ресемплируя `lemmaClusterSha256`, а не отдельные строки;
3. отдельно посмотреть переходы correct→wrong, wrong→correct, tie и распределение margin;
4. для pair accuracy использовать совпадающие candidate indices и `pairCorrect`, но не подменять ею group selection.

Отчёт не содержит текстов или lemma в открытом виде. Совпадение dataset SHA, validation group IDs SHA, trainer SHA и seed обязательно; иначе парное сравнение недействительно.

## Этап 4 — CPU/product gate до дополнительных seeds

Все s1 bundles возвращаются на исходную машину и измеряются same-build, без Magpie и другой тяжёлой нагрузки. Production-кандидат должен:

- сохранить accepted precision не ниже 95% без значимого падения;
- поднять end-to-end correct coverage минимум на 8 п.п. и снизить abstention минимум на 20%;
- пройти register/18+ и VN-dialogue hard cases;
- не ухудшить median, p95, large-inventory latency или peak memory более чем на 20%;
- сохранить margin-only abstention `0.25`, отсутствие silent provider fallback и все runtime invariants.

## Этап 5 — confirmation seeds

Выбрать максимум одного финалиста. Запустить `s2` и `s3` для него и для seed-matched `base7-reset`:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\run-wsd-ranker-rtx5080.ps1 -Profile <finalist-family>-s2
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\run-wsd-ranker-rtx5080.ps1 -Profile base7-reset-s2
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\run-wsd-ranker-rtx5080.ps1 -Profile <finalist-family>-s3
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\run-wsd-ranker-rtx5080.ps1 -Profile base7-reset-s3
```

Требования: средний прирост не менее `1.0` п.п., одинаковый знак хотя бы на двух из трёх seeds и 95% paired lemma-cluster bootstrap CI выше нуля. Не запускать все confirmation-профили «на всякий случай».

## Этап 6 — данные, distillation и финальная фиксация

После architecture/head bake-off ожидаемая следующая отдача — данные:

1. `1 500–3 000` domain-gold случаев VN/dialogue;
2. hard negatives одной lemma/POS, MWE, редкие значения, разговорный/register/18+ слой;
3. ablation `base`, `base + domain-gold`, `base + domain-gold + new-silver` на неизменном valid;
4. teacher/distillation от лучшего 12-layer варианта в 7-layer student, если 12 слоёв качественнее, но медленнее CPU budget.

Для новых данных нужен новый dataset SHA и новый пакет. Нельзя менять dataset, epochs или trainer через Resume.

Существующие `assets/Ranker/ranker-holdout*.json` не открываются при тюнинге. После заморозки architecture/data/head создаётся новый независимый blind holdout и выполняется ровно один заранее объявленный финальный прогон. Если gate не пройден, production остаётся на текущем GlossBERT.
