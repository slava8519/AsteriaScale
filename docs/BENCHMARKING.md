# Бенчмарки перевода

Этот стенд отвечает не на вопрос «какой переводчик вообще лучший», а на более
полезный для Asteria вопрос: **какая конфигурация лучше переводит именно
EN→RU-текст, который встречается в визуальных новеллах, литературе, манге и
научных материалах**. Поэтому итог не сворачивается в один непрозрачный балл.
Качество текста, контекст, выбор значения слова, род и скорость считаются
отдельно.

Все внешние наборы скачиваются только явной командой в ignored-каталог
`artifacts/Benchmarks`. В git находятся лишь manifest с закреплёнными
upstream-ревизиями/хешами, адаптеры и тесты. Frozen-наборы Asteria этим стендом
не читаются.

## Дорожки

| Дорожка | Набор | Что измеряет | Как интерпретировать |
|---|---|---|---|
| Полное предложение EN→RU | WMT25 Human Evaluation, production-guard slice | 40 официальных сегментов с русскими референсами и публичными выходами 40 систем | Внешняя точка профиля `direct`: все строки проходят текущий лимит TranslateGemma, но часть содержит несколько предложений |
| Smart hover EN→RU | WMT25 Human Evaluation, whole-hover-phrase slice | 10 закреплённых сегментов из тех же данных | Честный A/B `direct`/`smart`: production builder возвращает всю строку, а не только первое предложение; срез мал и служит диагностикой, не итоговым рейтингом |
| Document-level | WMT25 General MT | 87 полных документов с референсами и четырьмя закреплёнными baseline outputs | Внешний/reference-анализ; документы не выдаются за live Asteria-тест, потому что превышают production sentence guard |
| Будущий WMT | WMT26 General MT | Актуальные английские источники | Пока только генерация: в текущем upstream русские референсы пусты |
| Контекст | Voita et al. | Дейксис, эллипсис и лексическая связность | Контрастивная оценка генерации; это адаптация для black-box переводчика, а не официальный model-scoring протокол |
| Значение слова | MuCoW EN→RU | Передачу нужного смысла многозначного слова | Проверяет влияние GlossBERT/SANDWiCH/ConSeC на итог TranslateGemma |
| Род | WinoMT | Гендерную согласованность на профессиях и местоимениях | Набор source-only; нужен специальный evaluator или ручная разметка русского вывода |
| Наука | WMT24 Biomedical | Заголовки и аннотации | Терминология и длинные научные конструкции |
| Термины | WMT25 Terminology | EN→RU IT-термины в контексте | Точность терминов отдельно от беглости |
| Локальная регрессия | открытые dev-наборы Asteria | VN, литературу, науку и production WSD | Не является публичным ладером; нужен для защиты уже исправленных сценариев |

Для WSD-only A/B ранжировщиков остаётся отдельный
`--ranker-sentence-benchmark`. Он не оценивает русский текст и потому не должен
смешиваться с результатом полного перевода.

## 1. Получение данных

Посмотреть каталог без сети:

```powershell
python .\tools\fetch-benchmark-datasets.py list
```

Скачать, проверить закреплённые байты и импортировать один набор:

```powershell
python .\tools\fetch-benchmark-datasets.py fetch-import wmt25-humeval
```

То же для всех наборов:

```powershell
python .\tools\fetch-benchmark-datasets.py fetch-import all
```

Повторная проверка локальных raw-файлов по size/SHA-256 не обращается к
приложению:

```powershell
python .\tools\fetch-benchmark-datasets.py verify all
```

Каждый canonical JSONL содержит стабильный `id`, исходный текст,
`sourceSha256`, доступные референсы и provenance. Контекст передаётся в
`previousContext`: профиль `smart` передаёт его production post-edit как
`context_only`, но не добавляет в переводимую строку. Чистый baseline `direct`
намеренно использует официальный context-free prompt и это поле игнорирует.
`focus.characterIndex` измеряется в UTF-16 code units, как индекс строки в
.NET, а не в Unicode code points; это важно для строк с emoji до target.

## 2. Production-прогон Asteria

Runner использует настоящий TranslateGemma-сервис. Профиль `smart` дополнительно
проходит через тот же словарный/WSD-каркас, который использует умный словарь;
`direct` является чистым TranslateGemma baseline. Qwen и Foundry Local не
возвращаются. Унифицированный CLI всё ещё требует `--ranker-cpu-model`, но в
профиле `direct` значение не используется и в отчёте ranker остаётся `null`.

```powershell
dotnet run --project .\tests\AsteriaScale.Tests\AsteriaScale.Tests.csproj `
  -c Release --no-build -- --benchmark-only `
  --translation-suite .\artifacts\Benchmarks\normalized\wmt25-humeval-smart-hover-compatible.jsonl `
  .\dist\AsteriaScale `
  --system-id asteria-smart-consec-vulkan `
  --profile smart `
  --phrase-backend vulkan `
  --ranker-cpu-model consec `
  --translation-output .\artifacts\Benchmarks\runs\asteria-smart-consec.jsonl `
  --translation-report .\artifacts\Benchmarks\runs\asteria-smart-consec-run.json
```

Для честного A/B меняется только один фактор. `direct` и `smart` сравниваются на
`wmt25-humeval-smart-hover-compatible`: production sentence builder доказанно
возвращает там всю исходную строку. Срез из 40 строк предназначен для
полносегментного `direct`; запуск `smart` на нём методологически неверен, потому
что часть WMT-сегментов содержит несколько предложений, а карточка переводит
только предложение под курсором. После этого на whole-hover-срезе можно менять
GlossBERT, SANDWiCH и ConSeC на одной машине и одном canonical dataset.
Скорость разных машин в одну таблицу качества помещать можно, но сравнивать её
как ускорение нельзя.

`direct` против `smart` показывает пользу/цену **всего** production-каркаса, а
не изолированный вклад ranker: при наличии `previousContext` профиль `smart`
видит ещё и историю. Для чистого сравнения GlossBERT/SANDWiCH/ConSeC запускают
несколько `smart`-прогонов с одинаковым контекстом, меняя только
`--ranker-cpu-model`.

Большие наборы можно делить воспроизводимо, добавив к той же команде
`--shard 1/4 --max-cases 250`: это означает первую из четырёх
непересекающихся частей и не более 250 записей из неё. Runner записывает список
case ID и SHA selection в отчёт и каждую строку output, поэтому разные срезы
нельзя незаметно принять за один эксперимент. Для обоих закреплённых WMT25
срезов эти параметры не нужны — каждый прогоняется целиком.

Runner не превращает ошибку отдельной реплики в «хороший пустой перевод»:
`unavailable`/`error` остаются в output и входят в coverage/error-rate. Выходной
JSONL связывает каждую гипотезу с точным `id` и SHA исходной строки.

## 3. Метрики и отчёт

Посчитать встроенные воспроизводимые метрики:

```powershell
python .\tools\translation-benchmark.py score `
  --dataset .\artifacts\Benchmarks\normalized\wmt25-humeval-smart-hover-compatible.jsonl `
  --outputs .\artifacts\Benchmarks\runs\asteria-smart-consec.jsonl `
  --selection-report .\artifacts\Benchmarks\runs\asteria-smart-consec-run.json `
  --system-id asteria-smart-consec-vulkan `
  --system-name "Asteria smart / ConSeC / Vulkan" `
  --output .\artifacts\Benchmarks\reports\asteria-smart-consec.json
```

Встроенный отчёт считает:

- `chrF2` — символьную референсную метрику;
- term-anchor recall и остаточную латиницу;
- generation-adapted contrastive accuracy там, где есть контрастивные варианты;
- coverage, ошибки и p50/p95 latency;
- срезы по suite/domain/tags без усреднения несовместимых дорожек.

`chrF2` намеренно так и называется: это не `chrF++`, потому что встроенный
вариант не добавляет словные n-граммы. Для публикационного сравнения стоит
добавить COMET или MetricX внешним воспроизводимым прогоном.

Экспорт входа для внешней метрики:

```powershell
python .\tools\translation-benchmark.py export-metrics `
  --report .\artifacts\Benchmarks\reports\asteria-smart-consec.json `
  --output-dir .\artifacts\Benchmarks\metric-input\asteria-smart-consec
```

После внешнего вычисления импортируется sidecar `caseId,score`; manifest не
даст случайно соединить оценки с другой версией данных или другим output:

```powershell
python .\tools\translation-benchmark.py import-metric `
  --report .\artifacts\Benchmarks\reports\asteria-smart-consec.json `
  --manifest .\artifacts\Benchmarks\metric-input\asteria-smart-consec\manifest.json `
  --scores .\artifacts\Benchmarks\metric-input\asteria-smart-consec\comet.csv `
  --metric-id comet `
  --model-revision <точная-ревизия-модели> `
  --output .\artifacts\Benchmarks\reports\asteria-smart-consec-comet.json
```

## 4. Ладер

Fetcher материализует публичные WMT25-системы как обычные canonical outputs в
`normalized/baselines/<selection-id>/`. Их оценивает тот же scorer, например:

```powershell
python .\tools\translation-benchmark.py score `
  --dataset .\artifacts\Benchmarks\normalized\wmt25-humeval-translategemma-compatible.jsonl `
  --outputs .\artifacts\Benchmarks\normalized\baselines\wmt25-humeval-translategemma-compatible\gemma-3-12b.jsonl `
  --system-id gemma-3-12b `
  --system-name "Gemma-3-12B (WMT25)" `
  --output .\artifacts\Benchmarks\reports\wmt25-gemma-3-12b.json
```

После этого стенд принимает отчёты Asteria и публичных систем вместе.
Результат — машиночитаемый JSON, CSV для таблицы и автономный HTML:

```powershell
$reports = Get-ChildItem .\artifacts\Benchmarks\reports\*.json |
  Select-Object -ExpandProperty FullName
$metrics = Get-ChildItem .\artifacts\Benchmarks\reports\*-comet.json `
  -ErrorAction SilentlyContinue |
  Select-Object -ExpandProperty FullName
python .\tools\translation-benchmark.py leaderboard `
  --reports $reports `
  --metrics $metrics `
  --title "Asteria EN→RU — WMT25" `
  --primary-metric auto `
  --output-dir .\artifacts\Benchmarks\leaderboard\wmt25
```

Главная строка сравнения — одинаковый WMT25 slice с одинаковой метрикой.
Публичный WMT-лидер и лучший результат Asteria показываются рядом, но не
объявляются эквивалентными, если различаются сегментация, набор референсов или
ревизия evaluator.

## 5. Первый воспроизводимый контрольный прогон

На текущей машине выполнен полный `direct`-прогон 40 production-compatible
WMT25-сегментов. Встроенный macro chrF2 (не официальный WMT COMET/MetricX):

| Система | chrF2 | Coverage | Asteria latency p50 / p95 |
|---|---:|---:|---:|
| Gemini-2.5-Pro, опубликованный WMT output | 0,5088 | 100 % | — |
| Yandex, опубликованный WMT output | 0,4853 | 100 % | — |
| **Asteria TranslateGemma 4B direct / Vulkan** | **0,4258** | **100 %** | **3,31 / 6,40 с** |
| NLLB, опубликованный WMT output | 0,4165 | 100 % | — |
| Gemma-3-12B, опубликованный WMT output | 0,3885 | 100 % | — |

На отдельном whole-hover-срезе из 10 строк `direct` получил `0,3711` при 100 %
coverage, а текущий `smart + ConSeC` — `0,2419` при 70 % coverage и трёх
`phrase_translation_unavailable`. Это полезный найденный дефект/регрессионный
gate, но слишком малый срез для вывода о качестве ConSeC. WSD-качество
ранжировщика по-прежнему проверяется MuCoW и `--ranker-sentence-benchmark`, а
влияние на русский текст — более крупным sentence-level A/B.

## Минимальный протокол честного эксперимента

1. Не менять dataset после первого baseline; новый slice получает новый ID.
2. Не открывать frozen-наборы во время настройки.
3. Сначала сравнивать `direct` и `smart` как два полных профиля на одной
   TranslateGemma/backend; вклад ranker сравнивать только между `smart`-прогонами
   с идентичным контекстом.
4. Для качества сохранять dataset/output SHA; для скорости — ещё железо,
   backend, cold/warm policy и состояние кэша.
5. Смотреть не только средний `chrF2`, но и худшие случаи, coverage, контекст,
   WSD, род и доменные срезы.
6. Решение о смене production-default принимать только при улучшении основной
   дорожки без регрессии VN/context/WSD и без неприемлемого роста p95.

## Официальные источники

- [WMT25 General MT](https://github.com/wmt-conference/wmt25-general-mt)
- [WMT26 General MT](https://github.com/wmt-conference/wmt26-general-mt)
- [Good Translation, Wrong in Context](https://github.com/lena-voita/good-translation-wrong-in-context)
- [MuCoW](https://github.com/Helsinki-NLP/MuCoW)
- [WinoMT](https://github.com/gabrielStanovsky/mt_gender)

Лицензии и условия каждого upstream остаются его собственными. Fetcher
показывает их до скачивания и не включает сторонние данные в обычную сборку или
репозиторий Asteria.
