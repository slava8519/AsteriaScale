#!/usr/bin/env python3
"""Build full-coverage UTF-8 TSV dictionaries used by Asteria's hover modes."""

from __future__ import annotations

import argparse
import bz2
import difflib
import gzip
import io
import json
import re
import unicodedata
import xml.etree.ElementTree as ET
from collections import defaultdict
from pathlib import Path


XML_LANG = "{http://www.w3.org/XML/1998/namespace}lang"
TEI = "{http://www.tei-c.org/ns/1.0}"
SPACE_RE = re.compile(r"\s+")
ENGLISH_TOKEN_RE = re.compile(r"[^\W\d_]+(?:['’\-][^\W\d_]+)*", re.UNICODE)
JAPANESE_CHARACTER_RE = re.compile(r"[\u3040-\u30ff\u3400-\u9fff\uf900-\ufaff々〆ヶ]")
MAX_DIALOGUE_WORDS = 10
MAX_JAPANESE_DIALOGUE_LENGTH = 32
KAIKKI_STYLE_TAGS = {
    "colloquial": "разг.",
    "slang": "жарг.",
    "literary": "лит.",
    "vulgar": "груб.",
    "taboo": "табу",
    "obsolete": "устар.",
    "archaic": "устар.",
    "figuratively": "перен.",
    "dialectal": "диал.",
}


def clean(value: str | None) -> str:
    if not value:
        return ""
    value = unicodedata.normalize("NFC", value)
    return SPACE_RE.sub(" ", value.replace("\t", " ").replace("\n", " ")).strip()


def clean_russian_translation(value: str | None) -> str:
    """Normalize a displayed Russian translation and remove dictionary stress marks."""
    return (
        clean(value)
        .replace("\u0301", "")
        .replace("\u0341", "")
        .replace(";", ",")
    )


def is_usable_english_definition(value: str) -> bool:
    """Reject section labels that Kaikki occasionally exposes as sense text."""
    normalized = " ".join(value.casefold().split()).strip(" .:")
    return normalized not in {"translation", "translations"}


def normalize_english(value: str | None) -> str:
    # Keys keep only the straight apostrophe: the runtime lookup canonicalizes
    # OCR text the same way, so both spellings resolve to one entry.
    return " ".join(ENGLISH_TOKEN_RE.findall(clean(value))).casefold().replace("’", "'")


def normalize_japanese(value: str | None) -> str:
    return "".join(JAPANESE_CHARACTER_RE.findall(clean(value)))


def add(
    entries: dict[str, list[str]],
    key: str | None,
    translations: list[str],
    *,
    normalized: bool = False,
    limit: int | None = 6,
) -> None:
    key = clean(key) if normalized else normalize_english(key)
    if not key or not translations:
        return
    values = entries[key]
    known = {value.casefold() for value in values}
    for translation in translations:
        translation = clean(translation)
        normalized_translation = translation.casefold()
        if translation and normalized_translation not in known:
            values.append(translation)
            known.add(normalized_translation)
        if limit is not None and len(values) >= limit:
            break


def parse_freedict(path: Path) -> dict[str, list[str]]:
    entries: dict[str, list[str]] = defaultdict(list)
    for _, element in ET.iterparse(path, events=("end",)):
        if element.tag != TEI + "entry":
            continue
        orthographies = [node.text for node in element.findall(f".//{TEI}form/{TEI}orth")]
        translations = [
            quote.text
            for citation in element.findall(f".//{TEI}cit")
            if citation.get(XML_LANG) == "ru"
            for quote in citation.findall(f"./{TEI}quote")
        ]
        for orthography in orthographies:
            add(entries, orthography, translations)
        element.clear()
    return entries


def parse_jmdict(path: Path) -> dict[str, list[str]]:
    entries: dict[str, list[str]] = defaultdict(list)
    with gzip.open(path, "rb") as source:
        for _, element in ET.iterparse(source, events=("end",)):
            if element.tag != "entry":
                continue
            spellings = [node.text for node in element.findall("./k_ele/keb")]
            readings = [node.text for node in element.findall("./r_ele/reb")]
            translations = [
                gloss.text
                for gloss in element.findall("./sense/gloss")
                if gloss.get(XML_LANG) == "rus"
            ]
            for spelling in spellings + readings:
                add(entries, normalize_japanese(spelling), translations, normalized=True)
            element.clear()
    return entries


def _kaikki_style_prefix(sense: dict) -> str:
    labels: list[str] = []
    for tag in sense.get("tags", []):
        label = KAIKKI_STYLE_TAGS.get(str(tag))
        if label and label not in labels:
            labels.append(label)

    category_text = " ".join(
        str(category.get("name", ""))
        for category in sense.get("categories", [])
        if isinstance(category, dict)
    ).casefold()
    raw_text = " ".join(str(value) for value in sense.get("raw_tags", [])).casefold()
    inferred = (
        ("разг.", ("разговор",)),
        ("жарг.", ("жаргон", "сленг")),
        ("лит.", ("литератур",)),
        ("устар.", ("устар", "архаич")),
        ("груб.", ("вульгар", "груб")),
        ("табу", ("табу",)),
    )
    combined = category_text + " " + raw_text
    for label, markers in inferred:
        if label not in labels and any(marker in combined for marker in markers):
            labels.append(label)
    return " ".join(labels)


def _kaikki_glosses(sense: dict) -> list[str]:
    if "no-gloss" in sense.get("tags", []):
        return []

    prefix = _kaikki_style_prefix(sense)
    values: list[str] = []
    for raw_gloss in sense.get("glosses", []):
        # Semicolons delimit independent candidates in Asteria's on-disk schema.
        # Keep a Wiktionary sense atomic so its register label cannot become detached.
        gloss = clean(str(raw_gloss)).replace(";", ",")
        if not gloss:
            continue
        values.append(f"[{prefix}] {gloss}" if prefix else gloss)
    return values


def _kaikki_inflected_forms(item: dict) -> list[str]:
    word = normalize_english(item.get("word"))
    comparable_word = word.replace(" ", "").replace("-", "").replace("'", "")
    forms: list[str] = []
    for value in item.get("forms", []):
        if not isinstance(value, dict):
            continue
        raw_form = clean(value.get("form"))
        form = normalize_english(raw_form)
        comparable_form = form.replace(" ", "").replace("-", "").replace("'", "")
        if not form or form == word or raw_form.startswith("-") or raw_form.endswith("-"):
            continue

        # Some templates in the Russian Wiktionary dump expose a suffix such as
        # "ing" instead of the actual surface form. Accept only forms that remain
        # recognizably related to their lemma; irregular high-frequency English
        # verbs are handled by the runtime lemmatizer.
        minimum_length = max(3, len(comparable_word) // 2)
        similarity = difflib.SequenceMatcher(
            None,
            comparable_word,
            comparable_form,
            autojunk=False,
        ).ratio()
        if len(comparable_form) >= minimum_length and similarity >= 0.5:
            forms.append(form)
    return forms


def parse_kaikki_english(path: Path) -> dict[str, list[str]]:
    """Extract every EN→RU headword, inflected form, phrase and Russian gloss."""
    entries: dict[str, list[str]] = defaultdict(list)
    with path.open("rt", encoding="utf-8") as source:
        for line_number, line in enumerate(source, start=1):
            try:
                item = json.loads(line)
            except json.JSONDecodeError as error:
                raise ValueError(f"Invalid Kaikki JSONL at line {line_number}") from error
            if item.get("lang_code") != "en":
                continue

            translations = [
                gloss
                for sense in item.get("senses", [])
                if isinstance(sense, dict)
                for gloss in _kaikki_glosses(sense)
            ]
            if not translations:
                continue

            keys = [item.get("word"), *_kaikki_inflected_forms(item)]
            for key in keys:
                add(entries, key, translations, limit=None)
    return entries


def parse_kaikki_enwiktionary_russian(
    path: Path,
) -> tuple[dict[str, list[str]], dict[str, list[tuple[str, str, str, str]]]]:
    """Extract Russian translation boxes plus their aligned English sense definitions."""
    entries: dict[str, list[str]] = defaultdict(list)
    sense_entries: dict[str, list[tuple[str, str, str, str]]] = defaultdict(list)
    with path.open("rt", encoding="utf-8") as source:
        for line_number, line in enumerate(source, start=1):
            try:
                item = json.loads(line)
            except json.JSONDecodeError as error:
                raise ValueError(
                    f"Invalid English-Wiktionary Kaikki JSONL at line {line_number}"
                ) from error
            if item.get("lang_code") != "en":
                continue

            source_senses = [
                sense
                for sense in item.get("senses", [])
                if isinstance(sense, dict)
            ]
            translations: list[str] = []
            aligned_translations: list[tuple[str, str, str, str]] = []
            part_of_speech = clean(item.get("pos")).casefold()

            def collect_translations(
                source_translations: list,
                register: str,
                fallback_definition: str = "",
            ) -> None:
                for translation in source_translations:
                    if not isinstance(translation, dict):
                        continue
                    if (
                        translation.get("code") != "ru"
                        and translation.get("lang_code") != "ru"
                        and translation.get("lang") != "Russian"
                    ):
                        continue

                    value = clean_russian_translation(translation.get("word"))
                    if not value:
                        continue
                    short_definition = clean(translation.get("sense")).replace("\t", " ")
                    # The translation-box label is often only "bird" or "machinery".
                    # Prefer the source Wiktionary gloss, which contains the lexical
                    # detail a WSD cross-encoder needs, and use the short label only
                    # when no full gloss is attached to this sense.
                    definition = fallback_definition or short_definition
                    translations.append(
                        f"[{register}] {value}" if register else value
                    )
                    if definition and is_usable_english_definition(definition):
                        aligned_translations.append(
                            (value, definition, register, part_of_speech)
                        )

            for sense in source_senses:
                fallback_definition = max(
                    (
                        clean(gloss).replace("\t", " ")
                        for gloss in sense.get("glosses", [])
                        if clean(gloss) and is_usable_english_definition(clean(gloss))
                    ),
                    key=len,
                    default="",
                )
                collect_translations(
                    sense.get("translations", []),
                    _kaikki_style_prefix(sense),
                    fallback_definition,
                )

            style_prefixes = [
                prefix
                for sense in source_senses
                if (prefix := _kaikki_style_prefix(sense))
            ]
            collect_translations(
                item.get("translations", []),
                " ".join(dict.fromkeys(style_prefixes)),
            )

            if not translations:
                continue

            keys = [item.get("word"), *_kaikki_inflected_forms(item)]
            for key in keys:
                add(entries, key, translations, limit=None)
                normalized_key = normalize_english(key)
                if not normalized_key:
                    continue
                known_senses = {
                    (
                        translation.casefold(),
                        definition.casefold(),
                        register.casefold(),
                        sense_part_of_speech.casefold(),
                    )
                    for translation, definition, register, sense_part_of_speech
                    in sense_entries[normalized_key]
                }
                for aligned in aligned_translations:
                    normalized_aligned = tuple(value.casefold() for value in aligned)
                    if normalized_aligned not in known_senses:
                        sense_entries[normalized_key].append(aligned)
                        known_senses.add(normalized_aligned)
    return entries, sense_entries


def parse_kaikki_japanese(path: Path) -> dict[str, list[str]]:
    """Extract every JA→RU spelling, Japanese surface form, phrase and Russian gloss."""
    entries: dict[str, list[str]] = defaultdict(list)
    with path.open("rt", encoding="utf-8") as source:
        for line_number, line in enumerate(source, start=1):
            try:
                item = json.loads(line)
            except json.JSONDecodeError as error:
                raise ValueError(f"Invalid Kaikki JSONL at line {line_number}") from error
            if item.get("lang_code") != "ja":
                continue

            translations = [
                gloss
                for sense in item.get("senses", [])
                if isinstance(sense, dict)
                for gloss in _kaikki_glosses(sense)
            ]
            if not translations:
                continue

            keys = [item.get("word")]
            keys.extend(
                form.get("form")
                for form in item.get("forms") or []
                if isinstance(form, dict) and "form" in form
            )
            for key in keys:
                normalized_key = normalize_japanese(key)
                if normalized_key:
                    add(
                        entries,
                        normalized_key,
                        translations,
                        normalized=True,
                        limit=None,
                    )
    return entries


def read_existing_keys(path: Path) -> set[str]:
    if not path.exists():
        return set()

    keys: set[str] = set()
    with gzip.open(path, "rt", encoding="utf-8") as source:
        for line in source:
            key, separator, _ = line.partition("\t")
            if separator:
                keys.add(key)
    return keys


def parse_tatoeba_links(path: Path) -> dict[int, list[int]]:
    links: dict[int, list[int]] = defaultdict(list)
    with bz2.open(path, "rt", encoding="utf-8") as source:
        for line in source:
            source_id, separator, translation_id = line.partition("\t")
            if not separator:
                continue
            try:
                source_number = int(source_id)
                translation_number = int(translation_id)
            except ValueError:
                continue
            values = links[source_number]
            if translation_number not in values and len(values) < 4:
                values.append(translation_number)
    return links


def load_tatoeba_sentences(path: Path, wanted: set[int]) -> dict[int, str]:
    sentences: dict[int, str] = {}
    with bz2.open(path, "rt", encoding="utf-8") as source:
        for line in source:
            parts = line.rstrip("\n").split("\t", 2)
            if len(parts) != 3:
                continue
            try:
                sentence_id = int(parts[0])
            except ValueError:
                continue
            if sentence_id in wanted:
                sentences[sentence_id] = clean(parts[2])
    return sentences


def parse_tatoeba(
    source_path: Path,
    russian_path: Path,
    links_path: Path,
    language: str,
    excluded_keys: set[str],
) -> dict[str, list[str]]:
    links = parse_tatoeba_links(links_path)
    russian_ids = {translation_id for values in links.values() for translation_id in values}
    russian = load_tatoeba_sentences(russian_path, russian_ids)
    entries: dict[str, list[str]] = defaultdict(list)

    with bz2.open(source_path, "rt", encoding="utf-8") as source:
        for line in source:
            parts = line.rstrip("\n").split("\t", 2)
            if len(parts) != 3:
                continue
            try:
                sentence_id = int(parts[0])
            except ValueError:
                continue
            translation_ids = links.get(sentence_id)
            if not translation_ids:
                continue

            if language == "eng":
                key = normalize_english(parts[2])
                token_count = key.count(" ") + 1 if key else 0
                if token_count == 0 or token_count > MAX_DIALOGUE_WORDS:
                    continue
            else:
                key = normalize_japanese(parts[2])
                if not key or len(key) > MAX_JAPANESE_DIALOGUE_LENGTH:
                    continue

            if key in excluded_keys:
                continue
            translations = [russian[value] for value in translation_ids if value in russian]
            add(entries, key, translations, normalized=True, limit=4)

    return entries


def parse_curated_tsv(path: Path) -> dict[str, list[str]]:
    entries: dict[str, list[str]] = defaultdict(list)
    with path.open("rt", encoding="utf-8") as source:
        for line in source:
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            key, separator, translation = line.partition("\t")
            if separator:
                add(entries, key, [translation])
    return entries


def write_dictionary(path: Path, entries: dict[str, list[str]]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("wb") as raw:
        with gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0) as compressed:
            with io.TextIOWrapper(compressed, encoding="utf-8", newline="\n") as text:
                for key in sorted(entries):
                    text.write(key)
                    text.write("\t")
                    text.write("; ".join(entries[key]))
                    text.write("\n")


def write_sense_dictionary(
    path: Path,
    entries: dict[str, list[tuple[str, str, str, str]]],
) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("wb") as raw:
        with gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0) as compressed:
            with io.TextIOWrapper(compressed, encoding="utf-8", newline="\n") as text:
                for key in sorted(entries):
                    for translation, definition, register, part_of_speech in entries[key]:
                        text.write(key)
                        text.write("\t")
                        text.write(translation.replace("\t", " "))
                        text.write("\t")
                        text.write(definition.replace("\t", " "))
                        text.write("\t")
                        text.write(register.replace("\t", " "))
                        text.write("\t")
                        text.write(part_of_speech.replace("\t", " "))
                        text.write("\n")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--freedict", type=Path)
    parser.add_argument("--jmdict", type=Path)
    parser.add_argument("--kaikki-english", type=Path)
    parser.add_argument("--kaikki-enwiktionary-russian", type=Path)
    parser.add_argument("--kaikki-japanese", type=Path)
    parser.add_argument("--curated-vn", type=Path)
    parser.add_argument("--tatoeba-english", type=Path)
    parser.add_argument("--tatoeba-japanese", type=Path)
    parser.add_argument("--tatoeba-russian", type=Path)
    parser.add_argument("--tatoeba-eng-rus-links", type=Path)
    parser.add_argument("--tatoeba-jpn-rus-links", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()

    generated = False
    if args.freedict:
        english = parse_freedict(args.freedict)
        write_dictionary(args.output / "eng-rus.tsv.gz", english)
        print(f"FreeDict English/Russian: {len(english):,} entries")
        generated = True

    if args.jmdict:
        japanese = parse_jmdict(args.jmdict)
        write_dictionary(args.output / "jpn-rus.tsv.gz", japanese)
        print(f"JMdict Japanese/Russian: {len(japanese):,} entries")
        generated = True

    if args.kaikki_english:
        kaikki_english = parse_kaikki_english(args.kaikki_english)
        write_dictionary(args.output / "eng-rus-wiktionary.tsv.gz", kaikki_english)
        print(
            "Kaikki/Russian Wiktionary English/Russian: "
            f"{len(kaikki_english):,} headwords, forms and phrases"
        )
        generated = True

    if args.kaikki_enwiktionary_russian:
        kaikki_enwiktionary, kaikki_enwiktionary_senses = (
            parse_kaikki_enwiktionary_russian(
            args.kaikki_enwiktionary_russian
            )
        )
        write_dictionary(
            args.output / "eng-rus-enwiktionary.tsv.gz",
            kaikki_enwiktionary,
        )
        write_sense_dictionary(
            args.output / "eng-rus-enwiktionary-senses.tsv.gz",
            kaikki_enwiktionary_senses,
        )
        print(
            "Kaikki/English Wiktionary English/Russian translation boxes: "
            f"{len(kaikki_enwiktionary):,} headwords, forms and phrases; "
            f"{sum(map(len, kaikki_enwiktionary_senses.values())):,} aligned senses"
        )
        generated = True

    if args.kaikki_japanese:
        kaikki_japanese = parse_kaikki_japanese(args.kaikki_japanese)
        write_dictionary(args.output / "jpn-rus-wiktionary.tsv.gz", kaikki_japanese)
        print(
            "Kaikki/Russian Wiktionary Japanese/Russian: "
            f"{len(kaikki_japanese):,} spellings, forms and phrases"
        )
        generated = True

    if args.curated_vn:
        curated = parse_curated_tsv(args.curated_vn)
        write_dictionary(args.output / "eng-rus-vn.tsv.gz", curated)
        print(f"Curated visual-novel English/Russian: {len(curated):,} entries")
        generated = True

    tatoeba_arguments = [
        args.tatoeba_english,
        args.tatoeba_japanese,
        args.tatoeba_russian,
        args.tatoeba_eng_rus_links,
        args.tatoeba_jpn_rus_links,
    ]
    if any(tatoeba_arguments) and not args.tatoeba_russian:
        parser.error("--tatoeba-russian is required for Tatoeba dictionaries")

    if args.tatoeba_english or args.tatoeba_eng_rus_links:
        if not args.tatoeba_english or not args.tatoeba_eng_rus_links:
            parser.error("English Tatoeba data requires both sentence and link files")
        english_dialogue = parse_tatoeba(
            args.tatoeba_english,
            args.tatoeba_russian,
            args.tatoeba_eng_rus_links,
            "eng",
            read_existing_keys(args.output / "eng-rus.tsv.gz"),
        )
        write_dictionary(args.output / "eng-rus-dialogue.tsv.gz", english_dialogue)
        print(f"Tatoeba English/Russian dialogue: {len(english_dialogue):,} entries")
        generated = True

    if args.tatoeba_japanese or args.tatoeba_jpn_rus_links:
        if not args.tatoeba_japanese or not args.tatoeba_jpn_rus_links:
            parser.error("Japanese Tatoeba data requires both sentence and link files")
        japanese_dialogue = parse_tatoeba(
            args.tatoeba_japanese,
            args.tatoeba_russian,
            args.tatoeba_jpn_rus_links,
            "jpn",
            read_existing_keys(args.output / "jpn-rus.tsv.gz"),
        )
        write_dictionary(args.output / "jpn-rus-dialogue.tsv.gz", japanese_dialogue)
        print(f"Tatoeba Japanese/Russian dialogue: {len(japanese_dialogue):,} entries")
        generated = True

    if not generated:
        parser.error("No dictionary source was provided")


if __name__ == "__main__":
    main()
