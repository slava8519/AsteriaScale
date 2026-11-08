# Лицензии данных

[LICENSE](LICENSE) (MIT) распространяется на исходный код и оригинальную
документацию Asteria Scale. Он не распространяется на сторонние данные из
таблицы ниже: эти файлы получены из открытых источников и распространяются по
лицензиям этих источников.

Таблица охватывает все отслеживаемые git файлы в `assets/Dictionaries`,
`assets/Morphology`, `tools/dictionary-source`, `assets/Ranker`,
`assets/Translation`, `assets/TextHook` и все отслеживаемые файлы в
`src/AsteriaScale.RankerWorker/Models`. Источники, версии и лицензии взяты из
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md),
[assets/Dictionaries/README.md](assets/Dictionaries/README.md),
`tools/dictionary-source/vn-sources.lock.json`, заголовков `scripts/build-*.py`
и самих файлов. Если версия или дата нигде в репозитории не записана, в таблице
стоит «не зафиксировано». Это справочная сводка фактов, а не юридическая
консультация.

Колонка «В сборке» показывает, копирует ли обычная публикация
(`scripts/build.cmd`) файл в `dist/AsteriaScale`.

## Словари и морфология

| Файл(ы) | Источник и версия | Лицензия файла | Атрибуция | В сборке |
|---|---|---|---|---|
| `assets/Dictionaries/eng-rus.tsv.gz` | FreeDict/WikDict eng-rus, версия 2025.11.23 | CC BY-SA 3.0 (указана в TEI-заголовке источника) | FreeDict/WikDict; Wiktionary; DBnary | да |
| `assets/Dictionaries/eng-rus-wiktionary.tsv.gz` | Русский Викисловарь: дамп 2026.07.06, извлечение Kaikki/Wiktextract 2026.07.15 | CC BY-SA и GFDL (двойная лицензия Викисловаря; версия CC BY-SA не зафиксирована) | ru.wiktionary.org; Kaikki/wiktextract | да |
| `assets/Dictionaries/jpn-rus-wiktionary.tsv.gz` | Русский Викисловарь, извлечение Kaikki/Wiktextract 2026.07.15; дата дампа не зафиксирована | CC BY-SA и GFDL (версия CC BY-SA не зафиксирована) | ru.wiktionary.org; Kaikki/wiktextract | да |
| `assets/Dictionaries/eng-rus-enwiktionary.tsv.gz` | Английский Викисловарь: дамп 2026.07.06, извлечение Kaikki 2026.07.16; исходный JSONL 3 186 887 546 байт, SHA-256 `E491232E4AD314F825F025969AA1B1407A8B5FFA311711D49A83CC80E6204E71` | CC BY-SA и GFDL (версия CC BY-SA не зафиксирована) | en.wiktionary.org; Kaikki/wiktextract | да |
| `assets/Dictionaries/eng-rus-enwiktionary-senses.tsv.gz` | Тот же английский Викисловарь; 39 513 русских поверхностей заимствованы из слоя русского Викисловаря | CC BY-SA и GFDL (версия CC BY-SA не зафиксирована) | en.wiktionary.org; ru.wiktionary.org; Kaikki/wiktextract | да |
| `assets/Dictionaries/jpn-rus.tsv.gz` | Многоязычный файл JMdict; версия и дата не зафиксированы | лицензия EDRDG для JMdict/EDICT, https://www.edrdg.org/edrdg/licence.html | © Electronic Dictionary Research and Development Group (EDRDG) | да |
| `assets/Dictionaries/eng-rus-dialogue.tsv.gz`, `assets/Dictionaries/jpn-rus-dialogue.tsv.gz` | Пары предложений Tatoeba; дата выгрузки не зафиксирована | CC BY 2.0 FR | Tatoeba, https://tatoeba.org/ | да |
| `assets/Dictionaries/eng-rus-massive-dialogue.tsv.gz`, `assets/Dictionaries/vn-dialogue-provenance.tsv.gz` | Amazon MASSIVE 1.1 (`en-US` и `ru-RU`); архив 40 251 390 байт, SHA-256 `4CBA5FAA11C71437928E17CB1B9B3D8B8E727E7EA363A3A9A8045E19C0491577` | CC BY 4.0 | MASSIVE Copyright Amazon.com, Inc. or its affiliates; английские реплики получены из SLURP (CC BY 4.0) | да |
| `assets/Dictionaries/eng-rus-vn.tsv.gz`, `assets/Dictionaries/eng-rus-vn-soft.tsv.gz`, `assets/Dictionaries/eng-rus-vn-senses.tsv.gz`, `assets/Dictionaries/eng-rus-vn-scaffold.tsv.gz`, `assets/Dictionaries/vn-provenance.tsv.gz` | Оригинальный курируемый лексикон проекта `asteria-vn-curated-v2`, версия 2 (`tools/dictionary-source/vn-lexicon.tsv`) | MIT (по `vn-sources.lock.json`) | Asteria Scale contributors | да |
| `assets/Dictionaries/eng-rus-phrasal.tsv`, `assets/Dictionaries/eng-rus-phrasal.tsv.gz` | Ручной список проекта: 806 английских фразовых и предложных глаголов; `.gz` собирается из `.tsv` скриптом `scripts/build-phrasal-dictionary.py` | отдельно не зафиксирована (в `vn-sources.lock.json` не входит) | — | только `.tsv.gz` |
| `assets/Dictionaries/vn-build-manifest.json` | Manifest сборки VN-данных: хеши входов и выходов, параметры, статистика; создаётся `scripts/build-dictionaries.py` | отдельно не зафиксирована | — | да |
| `assets/Dictionaries/README.md` | Документация проекта | MIT | Asteria Scale contributors | да |
| `assets/Morphology/ru-morph.tsv.gz` | Морфологический словарь OpenCorpora через пакет `pymorphy3-dicts-ru`; версия пакета не зафиксирована | CC BY-SA (THIRD_PARTY_NOTICES.md); заголовок `scripts/build-ru-morphology.py` указывает CC BY-SA 4.0 | OpenCorpora, https://opencorpora.org/ | да |
| `tools/dictionary-source/kaikki.org-dictionary-Английский.jsonl.gz` | Сырой экспорт Kaikki/Wiktextract раздела «Английский» русского Викисловаря; дата выгрузки в файле не записана, файл добавлен коммитом `d0b22f6` (2026-07-20) | CC BY-SA и GFDL (версия CC BY-SA не зафиксирована) | ru.wiktionary.org; Kaikki/wiktextract | нет |
| `tools/dictionary-source/vn-lexicon.tsv` | Оригинальный курируемый лексикон `asteria-vn-curated-v2`, версия 2; 30 215 байт, SHA-256 `4256650C7786759F9955E2043B24041EA50A5457102C1DE25F1BF51EE591A9D0` | MIT (по `vn-sources.lock.json`) | Asteria Scale contributors | нет |
| `tools/dictionary-source/vn-sources.lock.json` | Source lock проекта: лицензии, размеры и SHA-256 входов VN-сборки | отдельно не зафиксирована | — | да, как `Dictionaries/vn-sources.lock.json` |

Изменения в производных файлах: нормализация ключей, фильтрация и упаковка в
детерминированный gzip TSV скриптами `scripts/build-dictionaries.py`,
`scripts/build-phrasal-dictionary.py` и `scripts/build-ru-morphology.py`;
подробности по каждому источнику — в [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

**Share-alike.** Производный файл распространяется по лицензии своего
источника. Файлы из источников под CC BY-SA (FreeDict/WikDict, Викисловарь,
OpenCorpora) распространяются на тех же условиях (для Викисловаря — CC BY-SA и
GFDL), а не по MIT. Файлы из
Tatoeba, Amazon MASSIVE и JMdict распространяются соответственно по CC BY 2.0 FR,
CC BY 4.0 и лицензии EDRDG.

## Регрессионные и бенчмарк-наборы

| Файл(ы) | Источник и версия | Лицензия файла | Атрибуция | В сборке |
|---|---|---|---|---|
| `assets/Ranker/ranker-benchmark.json`, `assets/Ranker/ranker-sentence-benchmark.json`, `assets/Ranker/ranker-register-benchmark.json` | Оригинальные наборы, написанные для проекта | CC0-1.0 (поле `license` в файле) | не требуется | нет |
| `assets/Ranker/ranker-holdout.json`, `assets/Ranker/ranker-holdout-v2.json`, `assets/Ranker/ranker-holdout-v3.json` | Замороженные оригинальные наборы проекта | CC0-1.0 (по [assets/Ranker/README.md](assets/Ranker/README.md)) | не требуется | нет |
| `assets/Ranker/baselines/glossbert-cpu.json` | Эталонный отчёт (schema 4) тестового стенда проекта по `ranker-benchmark.json` | в файлах не указана; не зафиксирована | — | нет |
| `assets/Ranker/README.md` | Документация проекта | MIT | Asteria Scale contributors | нет |
| `assets/Translation/translation-benchmark.json`, `assets/Translation/translation-register-benchmark.json` | Оригинальные наборы, написанные для проекта | CC0-1.0 (поле `license` в файле) | не требуется | нет |
| `assets/Translation/translation-holdout-v1.json` | Замороженный оригинальный набор проекта | CC0 (по [docs/RESEARCH.md](docs/RESEARCH.md), раздел «Метрики полного перевода») | не требуется | нет |

Замороженные holdout-файлы при составлении таблицы не открывались
(правило 5 в [AGENTS.md](AGENTS.md)); их лицензия взята из документации,
а не из самих файлов.

## Hook-коды и файлы моделей

| Файл(ы) | Источник и версия | Лицензия файла | Атрибуция | В сборке |
|---|---|---|---|---|
| `assets/TextHook/hook-codes.tsv` | Подборка hook-кодов сообщества Textractor; источник по заголовку файла — github.com/Artikash/Textractor/discussions/943; дата не зафиксирована | не зафиксирована | Textractor, discussion #943 | да |
| `src/AsteriaScale.RankerWorker/Models/GlossBert/tokenizer.json` | Токенизатор [`kanishka/GlossBERT`](https://huggingface.co/kanishka/GlossBERT), ревизия `0cc3b83af5496e27ebcc95ef0cf37ea0a9281a7a`; 465 642 байт, SHA-256 `04FD39DFF4BFA1B7AE42425C484EF0173F8DA60B485F66642D2B44B251C6A014` | MIT (заявлена репозиторием модели) | kanishka/GlossBERT | да |
| `src/AsteriaScale.RankerWorker/Models/GlossEmbeddings/tokenizer.json` | Токенизатор [`sentence-transformers/all-MiniLM-L6-v2`](https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2), ревизия `c9745ed1`; 466 247 байт, SHA-256 `BE50C3628F2BF5BB5E3A7F17B1F74611B2561A3A27EEAB05E5AA30F411572037` | Apache-2.0 | sentence-transformers/all-MiniLM-L6-v2 | да |
| `src/AsteriaScale.RankerWorker/Models/GlossBert/Npu/glossbert-eager-vitis-context.onnx.compatibility.txt` | Служебный сидекар совместимости AMD Vitis для NPU-сборки GlossBERT | отдельно не зафиксирована | — | да |

Веса моделей (GlossBERT, all-MiniLM-L6-v2, ConSeC, TranslateGemma) в
git не хранятся; их лицензии и условия — в
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
