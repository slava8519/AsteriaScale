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

### Численный путь Vulkan и `--phrase-coopmat`

На llama.cpp b9632 с Radeon 860M cooperative-matrix-шейдеры Vulkan
(`KHR_coopmat`) делают greedy-вывод TranslateGemma зависимым от того, что тот
же процесс llama-server считал раньше (E17, `RESEARCH.md`). С 8 ноября 2026
продукт запускает Vulkan llama-server с `GGML_VK_DISABLE_COOPMAT=1`: same-build
A/B на dev201 не нашёл потерь смысла или COMET ни в одной дорожке при цене
`+6–10 %` латентности. Вывод с тех пор — функция одного промта, и у сравнения
двух прогонов на одной identity нет шумового пола порядка запросов.

Обе дорожки, `direct` и `smart`, принимают `--phrase-coopmat on|off`, но
только вместе с `--phrase-backend vulkan`: CPU-runner переменную не читает, и
опция на нём отклоняется. `off` передаёт переменную llama-server, `on` явно
убирает её из окружения, даже если она унаследована от оболочки. Без опции
прогон идёт production-путём, то есть `off`. `on` нужен только для
сравнения с прогонами до 8 ноября, и тогда обе руки должны идти с `on`.

Выбор входит в runtime identity. Production и `off` дают
`…/llama.cpp-b9632-vulkan-full-nocoopmat`, `on` — прежнюю
`…/llama.cpp-b9632-vulkan-full`, которую несут все сохранённые Vulkan-прогоны
до 8 ноября. Identity пишется в `modelIdentity` каждой строки output и в
provenance отчёта. В `smart` hover-пайплайн берёт identity запроса
у экземпляра переводчика, поэтому вариант проходит тот же identity guard и
тот же кэш фраз, что и продукт, без подмены кода. Журнал запуска выбор
подтвердить не может: b9632 регистрирует backend до применения `-lv`, строка
возможностей устройства не печатается, и логи обоих путей совпадают. Источник
истины — окружение процесса, оно закреплено тестом.

`translation-benchmark.py paired` такие прогоны не сравнивает: он требует
одинаковую `modelIdentity`, потому что создан для A/B ранжировщиков. Парный
анализ смены runtime (смысл @2 и лемма, bootstrap по кластерам focus word,
McNemar, chrF2, COMET, латентность) описан в разделе RESEARCH об A/B coopmat.

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
- распределение причин отказа (`rejections`): каждая неуспешная строка
  относится либо к вердикту production-гейта (`residualEnglish`,
  `explanation`, `structuralMismatch` и прочие значения
  `SmartPhraseRejectionReason` в camelCase), либо к `unreported` — генерация
  не дошла до гейта или прогон причину не сохранил. Профиль `direct` вердиктов
  не даёт: он намеренно идёт мимо output guard;
- term-anchor recall и остаточную латиницу;
- generation-adapted contrastive accuracy там, где есть контрастивные варианты;
- coverage, ошибки и p50/p95 latency;
- срезы по suite/domain/tags без усреднения несовместимых дорожек.

`chrF2` намеренно так и называется: это не `chrF++`, потому что встроенный
вариант не добавляет словные n-граммы. Для публикационного сравнения стоит
добавить COMET или MetricX внешним воспроизводимым прогоном.

Нейросетевая метрика считается отдельным инструментом `tools/run-comet.py`,
поэтому сам стенд остаётся без зависимостей. Окружение и веса не входят в
поставку и живут в ignored-каталогах:

```powershell
python -m venv artifacts\comet-env
.\artifacts\comet-env\Scripts\python.exe -m pip install "unbabel-comet==2.2.6" "setuptools<81"
.\artifacts\comet-env\Scripts\python.exe tools\run-comet.py `
  --model <путь к model.ckpt> `
  --input .\artifacts\Benchmarks\metric-input\wmt25-full\asteria-tg4b-direct-vulkan
```

`--input` повторяем: загрузка чекпоинта дороже самого скоринга, поэтому весь
ладер считается одним процессом и одной и той же копией модели. Закреплённая
модель — `Unbabel/wmt22-comet-da`, revision
`2760a223ac957f30acfb18c8aa649b01cf1d75f2`, `2 323 649 073` байт, SHA-256
`e213091cde220f97b89f8bdfa750c458cfea741ad62affb455b59900210ff2af`;
`setuptools<81` обязателен, потому что `torchmetrics 0.10.3` из зависимостей
COMET использует удалённый в новых версиях `pkg_resources`.

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

## 6. MuCoW: изолированный A/B ранжировщиков

Для downstream-проверки ранжировщика fetcher создаёт три воспроизводимых
sentence-level среза MuCoW:

- `mucow-wsd-smart-hover-one-per-sense-v1` — открытый development/diagnostic:
  `201` случай, `99` focus words и `201` размеченный смысл;
- `mucow-wsd-smart-hover-replication-v1` — `199` новых исходных предложений
  для тех же `99` слов и `199` тех же lexical-sense strata;
- `mucow-wsd-smart-hover-balanced-v1` — точное объединение этих двух срезов,
  `400` случаев.

Dev201 и replication199 не пересекаются по case ID и SHA исходного предложения,
а balanced400 является их union. Replication
не проверяет unseen words/senses и сильнее смещён к OpenSubtitles. Все выбранные
строки требуют ровно одного отдельного target, сохраняют полный список
допустимых русских реализаций и известные ошибочные поверхности, проходят
surface-discrimination check и полностью совпадают с production hover phrase.
Это также устраняет старую ошибку, где `pen` мог находиться внутри `pencil`,
`pen2` или `pen_name`.

```powershell
python .\tools\fetch-benchmark-datasets.py fetch-import mucow-wsd
```

Для каждого ранжировщика выполняется одинаковый `smart`-прогон на
`mucow-wsd-smart-hover-one-per-sense-v1`; меняется только
`--ranker-cpu-model`. Затем обычный `score` дополнительно считает:

- правильный, неправильный, смешанный или неразрешённый смысл в итоговом
  переводе TranslateGemma;
- тот же outcome в строке Smart Dictionary до генерации;
- resolved accuracy, intention-to-treat accuracy и forbidden-sense rate.

Runner отдельно сохраняет выбранные ранжировщиком hints и hints, действительно
применённые в успешном prompt, с SHA-256 по общему C#/Python-контракту. Новый
парный отчёт полностью связывает output/metadata/metrics, проверяет одинаковые
build/model/backend/prompt/runtime/dictionaries/order, использует bootstrap по
`99` кластерам focus word как основной интервал и case-level exact McNemar как
дополнительный sensitivity analysis. Holm применяется отдельно к семействам
translation-sense и hover-sense; chrF2-интервалы остаются descriptive и без
поправки на множественные сравнения.

После свежего model run с applied-evidence telemetry v2 команда выглядит так
(исторические `mucow201.json` нового строгого контракта намеренно не проходят):

```powershell
python .\tools\translation-benchmark.py paired `
  --reports `
    .\artifacts\Benchmarks\reports\asteria-smart-glossbert-vulkan-mucow201-applied-v2.json `
    .\artifacts\Benchmarks\reports\asteria-smart-sandwich-vulkan-mucow201-applied-v2.json `
    .\artifacts\Benchmarks\reports\asteria-smart-consec-vulkan-mucow201-applied-v2.json `
  --bootstrap-samples 10000 `
  --seed 20260730 `
  --output .\artifacts\Benchmarks\comparisons\mucow201-ranker-paired.json
```

Контрольный прогон 30 июля 2026 года использовал одну TranslateGemma 4B Q4_K_M,
llama.cpp b9632, Vulkan full-offload и prompt v5. Во всех профилях успешны
`193/201` случаев; восемь одинаково недоступны. После обнаружения неполного
списка gold-поверхностей сохранённые hypotheses и selected evidence были без
повторного model run пересчитаны метрикой
`asteria.mucow-surface-outcome@2` (`ё→е`, exact token, без лемматизации):

| Ранжировщик | Smart-смысл, ITT | Ошибочная/смешанная подсказка | Итоговый смысл, ITT | chrF2 | p50 / p95 |
|---|---:|---:|---:|---:|---:|
| GlossBERT | 45,77 % (`92/201`) | 79/201 | 37,31 % (`75/201`) | 0,43367 | 7,88 / 15,42 с |
| SANDWiCH | 43,78 % (`88/201`) | 71/201 | 37,31 % (`75/201`) | 0,43471 | 7,24 / 15,22 с |
| **ConSeC** | **58,71 % (`118/201`)** | **39/201** | 37,31 % (`75/201`) | **0,43578** | **6,55 / 12,15 с** |

ConSeC против GlossBERT дал `29` парных улучшений Smart-смысла при `3`
ухудшениях: delta `+12,94` п.п., focus-word 95% CI `[+7,43; +18,50]`, raw
McNemar `p=2,56e-6`, Holm `p=5,11e-6`. Против SANDWiCH — `35/5`, delta
`+14,93` п.п., CI `[+9,31; +21,08]`, Holm `p=4,15e-6`. Неправильные/смешанные
подсказки ConSeC сократил почти вдвое.

Однако итоговый смысл оказался буквально одинаков: `75/201` у всех трёх,
каждая парная delta `0`, CI `[0; 0]`, McNemar `p=1`. Descriptive chrF2 ConSeC
выше GlossBERT на `0,002116`, focus-word CI `[+0,000274; +0,004568]`, но это
малый exploratory эффект без multiplicity correction. Старые outputs ещё не
содержали отдельного applied-evidence lifecycle, поэтому строгий causal report
нового формата потребует следующего model run; эти цифры являются честным
read-only re-score старых hypotheses, а не новым прогоном.

Следовательно, ConSeC достоверно улучшает WSD-вход и одновременно снижает
latency, но TranslateGemma 4B не превращает преимущество в более точный
лексический смысл конечной фразы. Следующий quality-step — повысить соблюдение
advisory sense evidence, настраивать его на открытом dev201 и один раз проверить
на source-disjoint replication199. Официальный MuCoW evaluator дополнительно
использует lemma fallback; Asteria `@2` — намеренно более простой surface proxy,
а не официальный MuCoW score.

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
- [Официальный MuCoW evaluator](https://github.com/Helsinki-NLP/MuCoW/blob/8e7570b2bec5b6fcc3727e44da4705672fc026f8/WMT2019/translation%20test%20suite/evaluate.py)
- [Статья MuCoW, WMT 2019](https://aclanthology.org/W19-5354/)
- [WinoMT](https://github.com/gabrielStanovsky/mt_gender)

Лицензии и условия каждого upstream остаются его собственными. Fetcher
показывает их до скачивания и не включает сторонние данные в обычную сборку или
репозиторий Asteria.
