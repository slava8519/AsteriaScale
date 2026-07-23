#!/usr/bin/env python3
"""Build full-coverage UTF-8 TSV dictionaries used by Asteria's hover modes."""

from __future__ import annotations

import argparse
import bz2
import difflib
import gzip
import hashlib
import io
import json
import os
import re
import shutil
import tarfile
import tempfile
import unicodedata
import xml.etree.ElementTree as ET
from collections import defaultdict
from dataclasses import dataclass
from pathlib import Path
from typing import Any


XML_LANG = "{http://www.w3.org/XML/1998/namespace}lang"
TEI = "{http://www.tei-c.org/ns/1.0}"
SPACE_RE = re.compile(r"\s+")
JAPANESE_CHARACTER_RE = re.compile(r"[\u3040-\u30ff\u3400-\u9fff\uf900-\ufaff々〆ヶ]")
MAX_DIALOGUE_WORDS = 10
MIN_DIALOGUE_WORDS = 2
MAX_JAPANESE_DIALOGUE_LENGTH = 32
VN_SOURCE_COLUMNS = (
    "source_id",
    "record_id",
    "role",
    "tier",
    "english",
    "russian",
    "english_definition",
    "register",
    "part_of_speech",
)
VN_ROLES = {"hard", "soft", "scaffold"}
VN_TIERS = {"gold", "silver"}
VN_ROLE_MAX_TOKENS = {"hard": 10, "soft": 6, "scaffold": 10}
MASSIVE_TRANSFORM_CONTRACT = {
    "pipeline": "massive-en-ru-exact-v1",
    "partitions": ["train", "dev", "test"],
    "minimumWords": MIN_DIALOGUE_WORDS,
    "maximumWords": MAX_DIALOGUE_WORDS,
    "rejectNumericSource": True,
    "rejectLocalizedSlots": True,
    "requireMajorityPositiveJudgments": True,
    "rejectLatinRussian": True,
    "rejectConflictingSentenceKeys": True,
}
SHA256_RE = re.compile(r"[0-9a-fA-F]{64}\Z")
SOURCE_IDENTIFIER_RE = re.compile(r"[A-Za-z0-9][A-Za-z0-9._:-]{0,127}\Z")
CYRILLIC_CHARACTER_RE = re.compile(r"[\u0400-\u052f]")
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
RU_GLOSS_BRACKETS = {"(": ")", "[": "]", "{": "}"}
RU_GLOSS_META_RE = re.compile(
    r"(?:\bто\s+же(?:\s+самое)?\s*,?\s*что\b|"
    r"^см\.\s+\S|^смотри\s+(?:также|выше|ниже)(?:\s|$)|"
    r"^(?:перевод|значение)\s+(?:отсутствует|неизвестен|не\s+указано)$|"
    r"^нет\s+(?:перевода|значения)$)",
    re.IGNORECASE,
)


@dataclass(frozen=True)
class VnSourceRecord:
    source_id: str
    record_id: str
    role: str
    tier: str
    english: str
    normalized_english: str
    russian_surfaces: tuple[str, ...]
    english_definition: str
    register: str
    part_of_speech: str


@dataclass(frozen=True)
class VnBuildData:
    hard: dict[str, list[str]]
    soft: dict[str, list[str]]
    senses: dict[str, list[tuple[str, str, str, str]]]
    scaffold: dict[str, list[str]]
    records: tuple[VnSourceRecord, ...]


@dataclass(frozen=True)
class MassiveBuildData:
    entries: dict[str, list[str]]
    source_id: str
    record_ids_by_key: dict[str, tuple[str, ...]]
    statistics: dict[str, Any]
    transform: dict[str, Any]


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


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest().upper()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def _strict_json_pairs(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"Duplicate JSON property: {key!r}")
        result[key] = value
    return result


def load_strict_json(value: str, description: str) -> Any:
    try:
        return json.loads(value, object_pairs_hook=_strict_json_pairs)
    except (json.JSONDecodeError, ValueError) as error:
        raise ValueError(f"Invalid JSON in {description}: {error}") from error


def contains_latin_letter(value: str) -> bool:
    return any(
        unicodedata.category(character).startswith("L")
        and "LATIN" in unicodedata.name(character, "")
        for character in value
    )


def contains_number(value: str) -> bool:
    return any(unicodedata.category(character).startswith("N") for character in value)


def require_nonempty_string(value: Any, description: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise ValueError(f"{description} must be a non-empty string")
    if value != clean(value):
        raise ValueError(f"{description} contains leading, trailing or repeated whitespace")
    return value


def require_locked_size_and_hash(path: Path, lock: dict[str, Any], description: str) -> None:
    expected_size = lock.get("bytes")
    expected_hash = lock.get("sha256")
    if not isinstance(expected_size, int) or expected_size < 0:
        raise ValueError(f"{description} lock has an invalid bytes value")
    if not isinstance(expected_hash, str) or not SHA256_RE.fullmatch(expected_hash):
        raise ValueError(f"{description} lock has an invalid SHA-256 value")

    actual_size = path.stat().st_size
    if actual_size != expected_size:
        raise ValueError(
            f"{description} size mismatch: {actual_size:,}, expected {expected_size:,}"
        )
    actual_hash = sha256_file(path)
    if actual_hash != expected_hash.upper():
        raise ValueError(
            f"{description} SHA-256 mismatch: {actual_hash}, "
            f"expected {expected_hash.upper()}"
        )


def is_usable_english_definition(value: str) -> bool:
    """Reject section labels that Kaikki occasionally exposes as sense text."""
    normalized = " ".join(value.casefold().split()).strip(" .:")
    return normalized not in {"translation", "translations"}


ENGLISH_TRIM_CHARACTERS = " .,!?:;\"“”()[]"


def canonical_english_casefold(value: str) -> str:
    # Keep this expansion list byte-identical to LocalDictionaryService.
    # Python's wider Unicode casefold would otherwise create keys that .NET's
    # invariant lower-casing cannot reproduce at lookup time.
    expanded = (
        value
        .replace("ß", "ss")
        .replace("ẞ", "ss")
        .replace("İ", "i\u0307")
        .replace("ſ", "s")
        .replace("ς", "σ")
        .replace("ﬀ", "ff")
        .replace("ﬁ", "fi")
        .replace("ﬂ", "fl")
        .replace("ﬃ", "ffi")
        .replace("ﬄ", "ffl")
        .replace("ﬅ", "st")
        .replace("ﬆ", "st")
    )
    return "".join(character.lower() for character in expanded)


def english_tokens(text: str) -> list[str]:
    result: list[str] = []
    start = -1
    index = 0
    while index < len(text):
        category = unicodedata.category(text[index])
        if category[0] in {"L", "M"}:
            if start < 0:
                start = index
            index += 1
            continue

        if (
            start >= 0
            and text[index] in {"'", "’", "-"}
            and index + 1 < len(text)
            and unicodedata.category(text[index + 1])[0] in {"L", "M"}
        ):
            index += 1
            continue

        if start >= 0:
            result.append(text[start:index])
            start = -1
        index += 1

    if start >= 0:
        result.append(text[start:])
    return result


def normalize_english(value: str | None) -> str:
    # Lexical keys retain internal punctuation and digits. Sentence keys use
    # normalize_english_sentence_key instead.
    collapsed = clean(value).replace("’", "'").strip(ENGLISH_TRIM_CHARACTERS)
    return canonical_english_casefold(collapsed)


def normalize_english_sentence_key(value: str | None) -> str:
    return " ".join(
        canonical_english_casefold(token.replace("’", "'"))
        for token in english_tokens(clean(value))
    )


def normalize_japanese(value: str | None) -> str:
    return "".join(JAPANESE_CHARACTER_RE.findall(clean(value)))


def verify_normalization_contract() -> None:
    contract_path = Path(__file__).with_name("dictionary-normalization-contract.tsv")
    for line_number, line in enumerate(
        contract_path.read_text(encoding="utf-8").splitlines(), start=1
    ):
        if not line or line.startswith("#"):
            continue
        source, lexical_expected, sentence_expected = line.split("\t")
        lexical_actual = normalize_english(source)
        sentence_actual = normalize_english_sentence_key(source)
        if lexical_actual != lexical_expected or sentence_actual != sentence_expected:
            raise RuntimeError(
                f"{contract_path.name}:{line_number}: "
                f"normalization of {source!r} produced "
                f"({lexical_actual!r}, {sentence_actual!r}), expected "
                f"({lexical_expected!r}, {sentence_expected!r})"
            )


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


def parse_kaikki_english(
    path: Path,
) -> tuple[dict[str, list[str]], dict[str, dict[str, list[str]]]]:
    """Extract every EN→RU headword, inflected form, phrase and Russian gloss.

    Also returns the raw Russian glosses grouped by normalized key and POS so
    the en-Wiktionary sense sidecar can borrow surfaces from this layer
    (roadmap task #1).
    """
    entries: dict[str, list[str]] = defaultdict(list)
    glosses_by_key_pos: dict[str, dict[str, list[str]]] = defaultdict(
        lambda: defaultdict(list)
    )
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

            part_of_speech = clean(item.get("pos")).casefold()
            keys = [item.get("word"), *_kaikki_inflected_forms(item)]
            for key in keys:
                add(entries, key, translations, limit=None)
                normalized_key = normalize_english(key)
                if not normalized_key:
                    continue
                bucket = glosses_by_key_pos[normalized_key][part_of_speech]
                known = {value.casefold() for value in bucket}
                for translation in translations:
                    if translation.casefold() not in known:
                        bucket.append(translation)
                        known.add(translation.casefold())
    return entries, glosses_by_key_pos


def parse_kaikki_enwiktionary_russian(
    path: Path,
) -> tuple[
    dict[str, list[str]],
    dict[str, list[tuple[str, str, str, str]]],
    dict[str, list[tuple[str, str, str]]],
]:
    """Extract Russian translation boxes plus their aligned English sense definitions.

    The third result lists senses that have a usable English gloss but no
    Russian translation of their own: (definition, register, pos) per key.
    They are candidates for enrichment from the ru-Wiktionary layer.
    """
    entries: dict[str, list[str]] = defaultdict(list)
    sense_entries: dict[str, list[tuple[str, str, str, str]]] = defaultdict(list)
    uncovered_senses: dict[str, list[tuple[str, str, str]]] = defaultdict(list)
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

            item_uncovered: list[tuple[str, str, str]] = []
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
                sense_register = _kaikki_style_prefix(sense)
                aligned_before = len(aligned_translations)
                collect_translations(
                    sense.get("translations", []),
                    sense_register,
                    fallback_definition,
                )
                if len(aligned_translations) == aligned_before and fallback_definition:
                    item_uncovered.append(
                        (fallback_definition, sense_register, part_of_speech)
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

            # Items without any Russian translation still matter: their
            # uncovered senses are enrichment candidates (roadmap task #1).
            if not translations and not item_uncovered:
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
                known_uncovered = set(uncovered_senses[normalized_key])
                for entry in item_uncovered:
                    if entry not in known_uncovered:
                        uncovered_senses[normalized_key].append(entry)
                        known_uncovered.add(entry)
    return entries, sense_entries, uncovered_senses


def _split_top_level_commas(value: str) -> list[str]:
    """Keep explanatory commas inside brackets attached to their surface."""
    parts: list[str] = []
    start = 0
    closing_brackets: list[str] = []
    for index, character in enumerate(value):
        expected_closing = RU_GLOSS_BRACKETS.get(character)
        if expected_closing is not None:
            closing_brackets.append(expected_closing)
        elif character in RU_GLOSS_BRACKETS.values():
            if closing_brackets and closing_brackets[-1] == character:
                closing_brackets.pop()
        elif character == "," and not closing_brackets:
            parts.append(value[start:index])
            start = index + 1
    parts.append(value[start:])
    return parts


def _has_balanced_brackets(value: str) -> bool:
    closing_brackets: list[str] = []
    for character in value:
        expected_closing = RU_GLOSS_BRACKETS.get(character)
        if expected_closing is not None:
            closing_brackets.append(expected_closing)
        elif character in RU_GLOSS_BRACKETS.values():
            if not closing_brackets or closing_brackets.pop() != character:
                return False
    return not closing_brackets


def _is_usable_ru_gloss_surface(value: str) -> bool:
    if not value or len(value.split()) > 4:
        return False
    if not CYRILLIC_CHARACTER_RE.search(value) or contains_latin_letter(value):
        return False
    if not _has_balanced_brackets(value):
        return False
    # A fully parenthesized value is an explanation, not an independent surface.
    if value[0] in RU_GLOSS_BRACKETS or value[-1] in RU_GLOSS_BRACKETS:
        return False
    if RU_GLOSS_META_RE.search(value.casefold()):
        return False
    return True


def _split_ru_gloss(gloss: str) -> tuple[str, list[str]]:
    """Split a ru-Wiktionary gloss into a register and conservative surfaces."""
    register = ""
    value = clean(gloss)
    if value.startswith("["):
        end = value.find("]")
        if end > 0:
            register = value[1:end].strip()
            value = value[end + 1 :].strip()
    if RU_GLOSS_META_RE.search(value.casefold()):
        return register, []
    parts = [
        clean_russian_translation(part)
        for part in _split_top_level_commas(value)
    ]
    return register, [part for part in parts if _is_usable_ru_gloss_surface(part)]


def _russian_stems_related(left: str, right: str) -> bool:
    a = left.casefold().replace("ё", "е")
    b = right.casefold().replace("ё", "е")
    limit = min(len(a), len(b))
    if limit < 4:
        return a == b
    prefix = 0
    while prefix < limit and a[prefix] == b[prefix]:
        prefix += 1
    return prefix >= (4 if limit <= 5 else 5)


def enrich_sense_entries(
    sense_entries: dict[str, list[tuple[str, str, str, str]]],
    uncovered_senses: dict[str, list[tuple[str, str, str]]],
    ru_glosses_by_key_pos: dict[str, dict[str, list[str]]],
) -> int:
    """Borrow Russian surfaces from the ru-Wiktionary layer (roadmap task #1).

    Two deliberately conservative moves:
    1. An interjection sense with an empty translation box gains ru-Wiktionary
       interjection glosses of the same headword (up to 4 surfaces).
    2. A sense whose translation stem-matches one part of a comma-separated
       ru-Wiktionary gloss gains that gloss's sibling synonyms (up to 2).
    """
    added = 0
    for key, senses in uncovered_senses.items():
        intj_glosses = ru_glosses_by_key_pos.get(key, {}).get("intj", [])
        if not intj_glosses:
            continue
        rows = sense_entries[key]
        known = {(row[0].casefold(), row[1].casefold()) for row in rows}
        for definition, register, pos in senses:
            if pos != "intj":
                continue
            appended = 0
            for gloss in intj_glosses:
                gloss_register, parts = _split_ru_gloss(gloss)
                for part in parts:
                    if appended >= 4:
                        break
                    marker = (part.casefold(), definition.casefold())
                    if marker in known:
                        continue
                    rows.append((part, definition, register or gloss_register, pos))
                    known.add(marker)
                    appended += 1
                    added += 1

    for key, rows in sense_entries.items():
        pos_glosses = ru_glosses_by_key_pos.get(key)
        if not pos_glosses:
            continue
        gloss_parts_by_pos = {
            gloss_pos.casefold(): [
                _split_ru_gloss(gloss)
                for gloss in glosses
            ]
            for gloss_pos, glosses in pos_glosses.items()
        }
        known = {(row[0].casefold(), row[1].casefold()) for row in rows}
        for translation, definition, register, pos in list(rows):
            gloss_parts = gloss_parts_by_pos.get(pos.casefold(), [])
            siblings = 0
            for _, parts in gloss_parts:
                if siblings >= 2 or len(parts) < 2:
                    continue
                if not any(_russian_stems_related(part, translation) for part in parts):
                    continue
                for part in parts:
                    if siblings >= 2:
                        break
                    if _russian_stems_related(part, translation):
                        continue
                    marker = (part.casefold(), definition.casefold())
                    if marker in known:
                        continue
                    rows.append((part, definition, register, pos))
                    known.add(marker)
                    siblings += 1
                    added += 1
    return added


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
                key = normalize_english_sentence_key(parts[2])
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


def load_vn_source_lock(
    path: Path,
) -> tuple[dict[str, Any], dict[str, dict[str, Any]]]:
    raw = path.read_bytes()
    try:
        text = raw.decode("utf-8-sig")
    except UnicodeDecodeError as error:
        raise ValueError(f"{path}: source lock is not valid UTF-8") from error
    root = load_strict_json(text, str(path))
    if not isinstance(root, dict) or root.get("schemaVersion") != 1:
        raise ValueError(f"{path}: schemaVersion must be 1")

    sources = root.get("sources")
    if not isinstance(sources, list) or not sources:
        raise ValueError(f"{path}: sources must be a non-empty array")

    source_index: dict[str, dict[str, Any]] = {}
    for index, source in enumerate(sources):
        description = f"{path}: sources[{index}]"
        if not isinstance(source, dict):
            raise ValueError(f"{description} must be an object")
        source_id = require_nonempty_string(source.get("id"), f"{description}.id")
        if not SOURCE_IDENTIFIER_RE.fullmatch(source_id):
            raise ValueError(f"{description}.id has an invalid source identifier")
        if source_id in source_index:
            raise ValueError(f"{path}: duplicate source id {source_id!r}")

        license_info = source.get("license")
        if not isinstance(license_info, dict):
            raise ValueError(f"{description}.license must be an object")
        require_nonempty_string(
            license_info.get("spdx"),
            f"{description}.license.spdx",
        )
        require_nonempty_string(
            license_info.get("attribution"),
            f"{description}.license.attribution",
        )
        source_index[source_id] = source

    return root, source_index


def _validate_clean_vn_field(value: str, description: str, maximum_length: int) -> str:
    normalized = unicodedata.normalize("NFC", value)
    if normalized != value or clean(value) != value:
        raise ValueError(
            f"{description} must be NFC without leading, trailing or repeated whitespace"
        )
    if len(value) > maximum_length:
        raise ValueError(f"{description} exceeds {maximum_length} characters")
    return value


def parse_curated_tsv(
    path: Path,
    source_lock: dict[str, Any],
    source_index: dict[str, dict[str, Any]],
) -> VnBuildData:
    curated_lock = source_lock.get("curatedFile")
    if not isinstance(curated_lock, dict):
        raise ValueError("VN source lock must contain a curatedFile object")
    locked_name = curated_lock.get("fileName")
    if not isinstance(locked_name, str) or locked_name != path.name:
        raise ValueError(
            f"Curated VN lock fileName must be {path.name!r}, got {locked_name!r}"
        )
    require_locked_size_and_hash(path, curated_lock, "Curated VN source")

    records: list[VnSourceRecord] = []
    identities: set[tuple[str, str]] = set()
    saw_header = False
    saw_data = False
    with path.open("rt", encoding="utf-8-sig", newline="") as source:
        for line_number, raw_line in enumerate(source, start=1):
            line = raw_line.rstrip("\r\n")
            if not line or line.startswith("#"):
                continue
            fields = line.split("\t")
            if not saw_data and tuple(fields) == VN_SOURCE_COLUMNS:
                if saw_header:
                    raise ValueError(f"{path}:{line_number}: duplicate TSV header")
                saw_header = True
                continue
            saw_data = True
            if len(fields) != len(VN_SOURCE_COLUMNS):
                raise ValueError(
                    f"{path}:{line_number}: expected {len(VN_SOURCE_COLUMNS)} "
                    f"tab-separated columns, got {len(fields)}"
                )

            values = dict(zip(VN_SOURCE_COLUMNS, fields, strict=True))
            row = f"{path}:{line_number}"
            source_id = _validate_clean_vn_field(
                values["source_id"], f"{row}: source_id", 128
            )
            record_id = _validate_clean_vn_field(
                values["record_id"], f"{row}: record_id", 128
            )
            if not SOURCE_IDENTIFIER_RE.fullmatch(source_id):
                raise ValueError(f"{row}: invalid source_id {source_id!r}")
            if not SOURCE_IDENTIFIER_RE.fullmatch(record_id):
                raise ValueError(f"{row}: invalid record_id {record_id!r}")
            if source_id not in source_index:
                raise ValueError(f"{row}: source_id {source_id!r} is absent from the lock")
            identity = (source_id, record_id)
            if identity in identities:
                raise ValueError(f"{row}: duplicate source_id/record_id {identity!r}")
            identities.add(identity)

            role = values["role"]
            tier = values["tier"]
            if role not in VN_ROLES:
                raise ValueError(f"{row}: role must be one of {sorted(VN_ROLES)}")
            if tier not in VN_TIERS:
                raise ValueError(f"{row}: tier must be one of {sorted(VN_TIERS)}")
            if role in {"hard", "scaffold"} and tier != "gold":
                raise ValueError(f"{row}: {role} records must use the gold tier")

            english = _validate_clean_vn_field(
                values["english"], f"{row}: english", 512
            )
            russian = _validate_clean_vn_field(
                values["russian"], f"{row}: russian", 1024
            )
            definition = _validate_clean_vn_field(
                values["english_definition"],
                f"{row}: english_definition",
                1024,
            )
            register = _validate_clean_vn_field(
                values["register"], f"{row}: register", 64
            )
            part_of_speech = _validate_clean_vn_field(
                values["part_of_speech"], f"{row}: part_of_speech", 64
            )
            if not english:
                raise ValueError(f"{row}: english must not be empty")
            normalized_english = normalize_english(english)
            if not normalized_english:
                raise ValueError(f"{row}: english has an empty normalized key")
            token_count = len(normalized_english.split(" "))
            maximum_tokens = VN_ROLE_MAX_TOKENS[role]
            if token_count > maximum_tokens:
                raise ValueError(
                    f"{row}: {role} english has {token_count} tokens; "
                    f"the runtime limit is {maximum_tokens}"
                )
            if not russian:
                raise ValueError(f"{row}: russian must not be empty")
            raw_surfaces = russian.split(";")
            if len(raw_surfaces) > 8:
                raise ValueError(f"{row}: russian may contain at most 8 surfaces")
            surfaces = tuple(surface.strip() for surface in raw_surfaces)
            if russian != "; ".join(surfaces):
                raise ValueError(
                    f"{row}: russian surfaces must use exactly '; ' as separator"
                )
            if role == "scaffold" and len(surfaces) != 1:
                raise ValueError(f"{row}: scaffold records require one Russian surface")
            known_surfaces: set[str] = set()
            for surface_index, surface in enumerate(surfaces, start=1):
                surface_description = f"{row}: russian surface {surface_index}"
                if not surface:
                    raise ValueError(f"{surface_description} must not be empty")
                if surface != clean_russian_translation(surface):
                    raise ValueError(
                        f"{surface_description} must not contain stress marks or semicolons"
                    )
                if not CYRILLIC_CHARACTER_RE.search(surface):
                    raise ValueError(
                        f"{surface_description} must contain Cyrillic text"
                    )
                if contains_latin_letter(surface):
                    raise ValueError(
                        f"{surface_description} must not contain Latin letters"
                    )
                canonical_surface = surface.casefold().replace("ё", "е")
                if canonical_surface in known_surfaces:
                    raise ValueError(
                        f"{surface_description} duplicates another Russian surface"
                    )
                known_surfaces.add(canonical_surface)
            if role == "soft":
                if not definition:
                    raise ValueError(f"{row}: soft records require english_definition")
                if not part_of_speech:
                    raise ValueError(f"{row}: soft records require part_of_speech")
            if definition:
                if not contains_latin_letter(definition):
                    raise ValueError(
                        f"{row}: english_definition must contain Latin text"
                    )
                if CYRILLIC_CHARACTER_RE.search(definition):
                    raise ValueError(
                        f"{row}: english_definition must not contain Cyrillic text"
                    )
            if part_of_speech and not re.fullmatch(
                r"[a-z][a-z0-9_-]*(?: [a-z][a-z0-9_-]*)*",
                part_of_speech,
            ):
                raise ValueError(
                    f"{row}: part_of_speech must be a lower-case stable label"
                )

            records.append(
                VnSourceRecord(
                    source_id,
                    record_id,
                    role,
                    tier,
                    english,
                    normalized_english,
                    surfaces,
                    definition,
                    register,
                    part_of_speech,
                )
            )

    if not records:
        raise ValueError(f"{path}: no structured VN records found")

    records.sort(
        key=lambda record: (
            record.normalized_english,
            record.role,
            record.source_id,
            record.record_id,
        )
    )
    hard: dict[str, list[str]] = defaultdict(list)
    soft: dict[str, list[str]] = defaultdict(list)
    scaffold: dict[str, list[str]] = defaultdict(list)
    senses: dict[str, list[tuple[str, str, str, str]]] = defaultdict(list)
    known_senses: dict[str, set[tuple[str, str, str, str]]] = defaultdict(set)

    def add_surfaces(
        target: dict[str, list[str]],
        record: VnSourceRecord,
        limit: int,
    ) -> None:
        values = target[record.normalized_english]
        known = {
            value.casefold().replace("ё", "е")
            for value in values
        }
        for surface in record.russian_surfaces:
            canonical = surface.casefold().replace("ё", "е")
            if canonical in known:
                continue
            if len(values) >= limit:
                raise ValueError(
                    f"VN key {record.normalized_english!r} exceeds {limit} "
                    f"distinct Russian surfaces in role {record.role!r}"
                )
            values.append(surface)
            known.add(canonical)

    for record in records:
        if record.role == "hard":
            add_surfaces(hard, record, 8)
        elif record.role == "soft":
            add_surfaces(soft, record, 8)
            for surface in record.russian_surfaces:
                sense = (
                    surface,
                    record.english_definition,
                    record.register,
                    record.part_of_speech,
                )
                canonical_sense = tuple(value.casefold() for value in sense)
                if canonical_sense not in known_senses[record.normalized_english]:
                    senses[record.normalized_english].append(sense)
                    known_senses[record.normalized_english].add(canonical_sense)
        else:
            add_surfaces(scaffold, record, 1)

    return VnBuildData(
        dict(hard),
        dict(soft),
        dict(senses),
        dict(scaffold),
        tuple(records),
    )


def _find_massive_source(
    source_index: dict[str, dict[str, Any]],
) -> dict[str, Any]:
    source = source_index.get("amazon-massive-1.1-en-ru")
    if source is None:
        candidates = [
            value
            for value in source_index.values()
            if value.get("format") == "massive-1.1"
        ]
        if len(candidates) != 1:
            raise ValueError(
                "VN source lock must contain exactly one MASSIVE 1.1 source"
            )
        source = candidates[0]
    if source.get("version") != "1.1":
        raise ValueError("MASSIVE source lock version must be '1.1'")
    transform = source.get("transform")
    if transform != MASSIVE_TRANSFORM_CONTRACT:
        raise ValueError(
            "MASSIVE source lock transform must exactly match the frozen "
            f"{MASSIVE_TRANSFORM_CONTRACT['pipeline']} contract"
        )
    return source


def _verify_locked_bytes(
    value: bytes,
    lock: dict[str, Any],
    description: str,
) -> None:
    expected_size = lock.get("bytes")
    expected_hash = lock.get("sha256")
    if not isinstance(expected_size, int) or expected_size < 0:
        raise ValueError(f"{description} lock has an invalid bytes value")
    if not isinstance(expected_hash, str) or not SHA256_RE.fullmatch(expected_hash):
        raise ValueError(f"{description} lock has an invalid SHA-256 value")
    if len(value) != expected_size:
        raise ValueError(
            f"{description} size mismatch: {len(value):,}, expected {expected_size:,}"
        )
    actual_hash = sha256_bytes(value)
    if actual_hash != expected_hash.upper():
        raise ValueError(
            f"{description} SHA-256 mismatch: {actual_hash}, "
            f"expected {expected_hash.upper()}"
        )


def _read_locked_massive_members(
    archive_path: Path,
    source_lock: dict[str, Any],
) -> tuple[dict[str, bytes], dict[str, dict[str, Any]]]:
    archive_lock = source_lock.get("archive")
    if not isinstance(archive_lock, dict):
        raise ValueError("MASSIVE source lock must contain an archive object")
    locked_name = archive_lock.get("fileName")
    if not isinstance(locked_name, str) or locked_name != archive_path.name:
        raise ValueError(
            f"MASSIVE archive fileName must be {archive_path.name!r}, got {locked_name!r}"
        )
    require_locked_size_and_hash(archive_path, archive_lock, "MASSIVE archive")

    member_locks = source_lock.get("members")
    if not isinstance(member_locks, list) or not member_locks:
        raise ValueError("MASSIVE source lock must contain a non-empty members array")
    by_locale: dict[str, dict[str, Any]] = {}
    locked_paths: set[str] = set()
    for index, member_lock in enumerate(member_locks):
        description = f"MASSIVE members[{index}]"
        if not isinstance(member_lock, dict):
            raise ValueError(f"{description} must be an object")
        member_path = require_nonempty_string(
            member_lock.get("path"), f"{description}.path"
        )
        if member_path in locked_paths:
            raise ValueError(f"Duplicate locked MASSIVE member {member_path!r}")
        locked_paths.add(member_path)
        locale = member_lock.get("locale")
        if locale is not None:
            locale = require_nonempty_string(locale, f"{description}.locale")
            if locale in by_locale:
                raise ValueError(f"Duplicate MASSIVE locale lock {locale!r}")
            by_locale[locale] = member_lock

    if set(by_locale) != {"en-US", "ru-RU"}:
        raise ValueError("MASSIVE members must identify exactly en-US and ru-RU")

    contents: dict[str, bytes] = {}
    try:
        with tarfile.open(archive_path, mode="r:*") as archive:
            members_by_name: dict[str, list[tarfile.TarInfo]] = defaultdict(list)
            for member in archive.getmembers():
                members_by_name[member.name].append(member)
            for member_lock in member_locks:
                member_path = member_lock["path"]
                matches = members_by_name.get(member_path, [])
                if len(matches) != 1 or not matches[0].isfile():
                    raise ValueError(
                        f"MASSIVE archive must contain one regular member {member_path!r}"
                    )
                extracted = archive.extractfile(matches[0])
                if extracted is None:
                    raise ValueError(f"Cannot read MASSIVE member {member_path!r}")
                with extracted:
                    value = extracted.read()
                _verify_locked_bytes(value, member_lock, f"MASSIVE member {member_path}")
                contents[member_path] = value
    except (tarfile.TarError, OSError) as error:
        raise ValueError(f"Invalid MASSIVE archive {archive_path}: {error}") from error

    return contents, by_locale


def _validate_massive_slot_methods(value: Any, description: str) -> list[dict[str, str]]:
    if not isinstance(value, list):
        raise ValueError(f"{description}.slot_method must be an array")
    result: list[dict[str, str]] = []
    for index, item in enumerate(value):
        if not isinstance(item, dict):
            raise ValueError(f"{description}.slot_method[{index}] must be an object")
        slot = item.get("slot")
        method = item.get("method")
        if not isinstance(slot, str) or not slot:
            raise ValueError(f"{description}.slot_method[{index}].slot is invalid")
        if method not in {"translation", "localization", "unchanged"}:
            raise ValueError(f"{description}.slot_method[{index}].method is invalid")
        result.append({"slot": slot, "method": method})
    return result


def _validate_massive_judgments(value: Any, description: str) -> list[dict[str, Any]]:
    if not isinstance(value, list):
        raise ValueError(f"{description}.judgments must be an array")
    result: list[dict[str, Any]] = []
    numeric_fields = {
        "intent_score": {0, 1, 2},
        "slots_score": {0, 1, 2},
        "grammar_score": {0, 1, 2, 3, 4},
        "spelling_score": {0, 1, 2},
    }
    for index, item in enumerate(value):
        if not isinstance(item, dict):
            raise ValueError(f"{description}.judgments[{index}] must be an object")
        for field, allowed in numeric_fields.items():
            field_value = item.get(field)
            if type(field_value) is not int or field_value not in allowed:
                raise ValueError(
                    f"{description}.judgments[{index}].{field} is invalid"
                )
        language = item.get("language_identification")
        if not isinstance(language, str) or not language:
            raise ValueError(
                f"{description}.judgments[{index}].language_identification is invalid"
            )
        worker_id = item.get("worker_id")
        if not isinstance(worker_id, str) or not worker_id:
            raise ValueError(
                f"{description}.judgments[{index}].worker_id is invalid"
            )
        result.append(item)
    return result


def _parse_massive_locale(
    value: bytes,
    locale: str,
    member_lock: dict[str, Any],
) -> dict[str, dict[str, Any]]:
    expected_records = member_lock.get("records")
    if not isinstance(expected_records, int) or expected_records <= 0:
        raise ValueError(f"MASSIVE {locale} lock has an invalid records value")
    try:
        text = value.decode("utf-8-sig")
    except UnicodeDecodeError as error:
        raise ValueError(f"MASSIVE {locale} member is not valid UTF-8") from error

    records: dict[str, dict[str, Any]] = {}
    for line_number, line in enumerate(text.splitlines(), start=1):
        if not line.strip():
            raise ValueError(f"MASSIVE {locale}:{line_number}: blank JSONL line")
        item = load_strict_json(line, f"MASSIVE {locale}:{line_number}")
        if not isinstance(item, dict):
            raise ValueError(f"MASSIVE {locale}:{line_number}: row must be an object")
        description = f"MASSIVE {locale}:{line_number}"
        record_id = item.get("id")
        if not isinstance(record_id, str) or not record_id:
            raise ValueError(f"{description}: id must be a non-empty string")
        if record_id in records:
            raise ValueError(f"{description}: duplicate id {record_id!r}")
        if item.get("locale") != locale:
            raise ValueError(f"{description}: locale must be {locale!r}")
        if item.get("partition") not in set(
            MASSIVE_TRANSFORM_CONTRACT["partitions"]
        ):
            raise ValueError(f"{description}: invalid partition")
        for field in ("scenario", "intent", "utt", "annot_utt", "worker_id"):
            if not isinstance(item.get(field), str) or not item[field]:
                raise ValueError(f"{description}: {field} must be a non-empty string")
        if locale == "ru-RU":
            item["slot_method"] = _validate_massive_slot_methods(item.get("slot_method"), description)
            item["judgments"] = _validate_massive_judgments(item.get("judgments"), description)
        records[record_id] = item

    if len(records) != expected_records:
        raise ValueError(
            f"MASSIVE {locale} record count mismatch: {len(records):,}, "
            f"expected {expected_records:,}"
        )
    return records


def _massive_judgments_pass(judgments: list[dict[str, Any]]) -> bool:
    if len(judgments) < 2:
        return False
    accepted = sum(
        1
        for judgment in judgments
        if judgment["intent_score"] in {1, 2}
        and judgment["slots_score"] in {1, 2}
        and judgment["grammar_score"] >= 3
        and judgment["spelling_score"] == 2
        and judgment["language_identification"] == "target"
    )
    return accepted * 2 > len(judgments)


def parse_massive_archive(
    archive_path: Path,
    source_index: dict[str, dict[str, Any]],
) -> MassiveBuildData:
    massive_lock = _find_massive_source(source_index)
    contents, members_by_locale = _read_locked_massive_members(
        archive_path,
        massive_lock,
    )
    english_lock = members_by_locale["en-US"]
    russian_lock = members_by_locale["ru-RU"]
    english = _parse_massive_locale(
        contents[english_lock["path"]], "en-US", english_lock
    )
    russian = _parse_massive_locale(
        contents[russian_lock["path"]], "ru-RU", russian_lock
    )
    english_ids = set(english)
    russian_ids = set(russian)
    if english_ids != russian_ids:
        missing_russian = sorted(english_ids - russian_ids)[:5]
        missing_english = sorted(russian_ids - english_ids)[:5]
        raise ValueError(
            "MASSIVE locale IDs differ; "
            f"missing ru-RU={missing_russian}, missing en-US={missing_english}"
        )

    rejection_reasons = {
        "localized_slot": 0,
        "numeric_source": 0,
        "too_short": 0,
        "too_long": 0,
        "bad_judgments": 0,
        "no_cyrillic": 0,
        "latin_russian": 0,
        "conflicting_normalized_key": 0,
    }
    candidates: dict[str, list[tuple[str, str]]] = defaultdict(list)
    for record_id in sorted(english_ids):
        source = english[record_id]
        target = russian[record_id]
        for field in ("partition", "scenario", "intent"):
            if source[field] != target[field]:
                raise ValueError(
                    f"MASSIVE id {record_id!r}: {field} differs between locales"
                )

        if any(
            slot["method"] == "localization"
            for slot in target["slot_method"]
        ):
            rejection_reasons["localized_slot"] += 1
            continue
        source_text = clean(source["utt"])
        if contains_number(source_text):
            rejection_reasons["numeric_source"] += 1
            continue
        key = normalize_english_sentence_key(source_text)
        token_count = len(english_tokens(source_text))
        if token_count < MIN_DIALOGUE_WORDS or not key:
            rejection_reasons["too_short"] += 1
            continue
        if token_count > MAX_DIALOGUE_WORDS:
            rejection_reasons["too_long"] += 1
            continue
        if not _massive_judgments_pass(target["judgments"]):
            rejection_reasons["bad_judgments"] += 1
            continue
        translation = clean_russian_translation(target["utt"])
        if not CYRILLIC_CHARACTER_RE.search(translation):
            rejection_reasons["no_cyrillic"] += 1
            continue
        if contains_latin_letter(translation):
            rejection_reasons["latin_russian"] += 1
            continue
        candidates[key].append((translation, record_id))

    entries: dict[str, list[str]] = {}
    record_ids_by_key: dict[str, tuple[str, ...]] = {}
    equivalent_duplicates = 0
    for key in sorted(candidates):
        values = candidates[key]
        canonical_translations = {
            clean(translation).casefold().replace("ё", "е")
            for translation, _ in values
        }
        if len(canonical_translations) != 1:
            rejection_reasons["conflicting_normalized_key"] += len(values)
            continue
        selected_translation, _ = min(values, key=lambda value: value[1])
        entries[key] = [selected_translation]
        record_ids_by_key[key] = tuple(sorted(record_id for _, record_id in values))
        equivalent_duplicates += len(values) - 1

    accepted_before_conflicts = sum(map(len, candidates.values()))
    rejected = sum(rejection_reasons.values())
    if len(english_ids) != accepted_before_conflicts + sum(
        count
        for reason, count in rejection_reasons.items()
        if reason != "conflicting_normalized_key"
    ):
        raise RuntimeError("MASSIVE filter accounting invariant failed")
    if not entries:
        raise ValueError("MASSIVE filtering produced no exact-dialogue entries")

    statistics: dict[str, Any] = {
        "rawPairs": len(english_ids),
        "acceptedRecordsBeforeConflicts": accepted_before_conflicts,
        "shippedKeys": len(entries),
        "equivalentDuplicateRecords": equivalent_duplicates,
        "rejectedRecords": rejected,
        "rejectionsByReason": rejection_reasons,
    }
    return MassiveBuildData(
        entries,
        massive_lock["id"],
        record_ids_by_key,
        statistics,
        dict(massive_lock["transform"]),
    )


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


def write_vn_provenance(path: Path, records: tuple[VnSourceRecord, ...]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    ordered = sorted(
        records,
        key=lambda record: (
            record.source_id,
            record.record_id,
            record.role,
            record.normalized_english,
        ),
    )
    with path.open("wb") as raw:
        with gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0) as compressed:
            with io.TextIOWrapper(compressed, encoding="utf-8", newline="\n") as text:
                text.write(
                    "source_id\trecord_id\trole\ttier\tnormalized_english\t"
                    "english\trussian\tenglish_definition\tregister\tpart_of_speech\n"
                )
                for record in ordered:
                    text.write(
                        "\t".join(
                            (
                                record.source_id,
                                record.record_id,
                                record.role,
                                record.tier,
                                record.normalized_english,
                                record.english,
                                "; ".join(record.russian_surfaces),
                                record.english_definition,
                                record.register,
                                record.part_of_speech,
                            )
                        )
                    )
                    text.write("\n")


def write_vn_dialogue_provenance(path: Path, data: MassiveBuildData) -> None:
    if data.entries.keys() != data.record_ids_by_key.keys():
        raise RuntimeError("MASSIVE dictionary/provenance keys differ")
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("wb") as raw:
        with gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0) as compressed:
            with io.TextIOWrapper(compressed, encoding="utf-8", newline="\n") as text:
                text.write("source_id\trecord_ids\tnormalized_english\n")
                for key in sorted(data.record_ids_by_key):
                    record_ids = data.record_ids_by_key[key]
                    if not record_ids:
                        raise RuntimeError(
                            f"MASSIVE provenance has no record IDs for {key!r}"
                        )
                    text.write(data.source_id)
                    text.write("\t")
                    text.write("; ".join(record_ids))
                    text.write("\t")
                    text.write(key)
                    text.write("\n")


def _file_manifest(path: Path, rows: int) -> dict[str, Any]:
    return {
        "fileName": path.name,
        "bytes": path.stat().st_size,
        "rows": rows,
        "sha256": sha256_file(path),
    }


def write_vn_build_manifest(
    path: Path,
    source_lock_path: Path,
    output_rows: dict[Path, int],
    *,
    curated_path: Path | None,
    vn_data: VnBuildData | None,
    massive_path: Path | None,
    massive_data: MassiveBuildData | None,
) -> None:
    inputs: dict[str, Any] = {
        "sourceLock": {
            "fileName": source_lock_path.name,
            "bytes": source_lock_path.stat().st_size,
            "sha256": sha256_file(source_lock_path),
        }
    }
    counts: dict[str, Any] = {}
    if curated_path is not None and vn_data is not None:
        inputs["curatedVn"] = {
            "fileName": curated_path.name,
            "bytes": curated_path.stat().st_size,
            "sha256": sha256_file(curated_path),
        }
        role_counts = {
            role: sum(record.role == role for record in vn_data.records)
            for role in sorted(VN_ROLES)
        }
        tier_counts = {
            tier: sum(record.tier == tier for record in vn_data.records)
            for tier in sorted(VN_TIERS)
        }
        source_counts: dict[str, int] = defaultdict(int)
        for record in vn_data.records:
            source_counts[record.source_id] += 1
        counts["curatedVn"] = {
            "records": len(vn_data.records),
            "recordsByRole": role_counts,
            "recordsByTier": tier_counts,
            "recordsBySource": dict(sorted(source_counts.items())),
            "hardKeys": len(vn_data.hard),
            "softKeys": len(vn_data.soft),
            "senseRows": sum(map(len, vn_data.senses.values())),
            "scaffoldKeys": len(vn_data.scaffold),
        }
    if massive_path is not None and massive_data is not None:
        inputs["massiveArchive"] = {
            "fileName": massive_path.name,
            "bytes": massive_path.stat().st_size,
            "sha256": sha256_file(massive_path),
        }
        counts["massive"] = massive_data.statistics

    generator_path = Path(__file__).resolve()
    manifest = {
        "schemaVersion": 2,
        "generator": {
            "fileName": generator_path.name,
            "bytes": generator_path.stat().st_size,
            "sha256": sha256_file(generator_path),
        },
        "normalization": {
            "lexical": "normalize_english-v1",
            "sentence": "normalize_english_sentence_key-v1",
        },
        "transforms": {
            "massive": massive_data.transform if massive_data is not None else None,
        },
        "inputs": inputs,
        "counts": counts,
        "outputs": [
            _file_manifest(output_path, rows)
            for output_path, rows in sorted(
                output_rows.items(), key=lambda item: item[0].name
            )
        ],
    }
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
        newline="\n",
    )


def publish_vn_outputs(
    output_directory: Path,
    staged_outputs: list[Path],
) -> None:
    """Publish a manifest-last VN set and restore the old set on interruption."""
    output_directory.mkdir(parents=True, exist_ok=True)
    backup_directory = Path(
        tempfile.mkdtemp(
            prefix=".asteria-vn-backup-",
            dir=output_directory.parent,
        )
    )
    committed: list[tuple[Path, Path | None, bool]] = []
    try:
        for staged_path in staged_outputs:
            target_path = output_directory / staged_path.name
            backup_path: Path | None = None
            target_existed = target_path.exists()
            if target_existed:
                backup_path = backup_directory / staged_path.name

            # Register both moves before either one starts. A KeyboardInterrupt
            # may arrive immediately after an atomic OS operation completed.
            committed.append((target_path, backup_path, target_existed))
            if backup_path is not None:
                os.replace(target_path, backup_path)
            os.replace(staged_path, target_path)
    except BaseException as publication_error:
        rollback_errors: list[BaseException] = []
        for target_path, backup_path, target_existed in reversed(committed):
            try:
                if target_existed and backup_path is not None and backup_path.exists():
                    # os.replace restores the prior file atomically even when
                    # the newly published target already exists.
                    os.replace(backup_path, target_path)
                elif not target_existed and target_path.exists():
                    target_path.unlink()
            except BaseException as rollback_error:
                rollback_errors.append(rollback_error)

        if rollback_errors:
            raise RuntimeError(
                "VN output publication failed and rollback was incomplete; "
                f"recoverable backups remain in {backup_directory}"
            ) from publication_error

        shutil.rmtree(backup_directory)
        raise

    shutil.rmtree(backup_directory)


def main() -> None:
    verify_normalization_contract()
    parser = argparse.ArgumentParser()
    parser.add_argument("--freedict", type=Path)
    parser.add_argument("--jmdict", type=Path)
    parser.add_argument("--kaikki-english", type=Path)
    parser.add_argument("--kaikki-enwiktionary-russian", type=Path)
    parser.add_argument("--kaikki-japanese", type=Path)
    parser.add_argument("--curated-vn", type=Path)
    parser.add_argument("--vn-source-lock", type=Path)
    parser.add_argument("--massive-archive", type=Path)
    parser.add_argument("--tatoeba-english", type=Path)
    parser.add_argument("--tatoeba-japanese", type=Path)
    parser.add_argument("--tatoeba-russian", type=Path)
    parser.add_argument("--tatoeba-eng-rus-links", type=Path)
    parser.add_argument("--tatoeba-jpn-rus-links", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()

    if bool(args.curated_vn) != bool(args.massive_archive):
        parser.error(
            "--curated-vn and --massive-archive must be supplied together "
            "so the VN manifest cannot describe a partial stale inventory"
        )
    if args.curated_vn and not args.vn_source_lock:
        parser.error(
            "--vn-source-lock is required with --curated-vn or --massive-archive"
        )

    vn_source_lock: dict[str, Any] | None = None
    vn_source_index: dict[str, dict[str, Any]] | None = None
    if args.vn_source_lock:
        vn_source_lock, vn_source_index = load_vn_source_lock(args.vn_source_lock)

    vn_output_rows: dict[Path, int] = {}
    vn_data: VnBuildData | None = None
    massive_data: MassiveBuildData | None = None

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

    kaikki_english_glosses: dict[str, dict[str, list[str]]] = {}
    if args.kaikki_english:
        kaikki_english, kaikki_english_glosses = parse_kaikki_english(
            args.kaikki_english
        )
        write_dictionary(args.output / "eng-rus-wiktionary.tsv.gz", kaikki_english)
        print(
            "Kaikki/Russian Wiktionary English/Russian: "
            f"{len(kaikki_english):,} headwords, forms and phrases"
        )
        generated = True

    if args.kaikki_enwiktionary_russian:
        kaikki_enwiktionary, kaikki_enwiktionary_senses, kaikki_uncovered = (
            parse_kaikki_enwiktionary_russian(
            args.kaikki_enwiktionary_russian
            )
        )
        if kaikki_english_glosses:
            enriched = enrich_sense_entries(
                kaikki_enwiktionary_senses,
                kaikki_uncovered,
                kaikki_english_glosses,
            )
            print(
                "Sense sidecar enrichment from ru-Wiktionary: "
                f"{enriched:,} borrowed surfaces"
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

    if args.curated_vn and args.massive_archive:
        assert vn_source_lock is not None and vn_source_index is not None
        # Parse every source before touching the committed output. A malformed
        # late input must not leave a half-regenerated VN inventory behind.
        vn_data = parse_curated_tsv(
            args.curated_vn,
            vn_source_lock,
            vn_source_index,
        )
        massive_data = parse_massive_archive(
            args.massive_archive,
            vn_source_index,
        )

        args.output.parent.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(
            prefix=".asteria-vn-build-",
            dir=args.output.parent,
        ) as temporary:
            staging = Path(temporary)
            hard_path = staging / "eng-rus-vn.tsv.gz"
            soft_path = staging / "eng-rus-vn-soft.tsv.gz"
            senses_path = staging / "eng-rus-vn-senses.tsv.gz"
            scaffold_path = staging / "eng-rus-vn-scaffold.tsv.gz"
            provenance_path = staging / "vn-provenance.tsv.gz"
            massive_path = staging / "eng-rus-massive-dialogue.tsv.gz"
            massive_provenance_path = staging / "vn-dialogue-provenance.tsv.gz"
            manifest_path = staging / "vn-build-manifest.json"

            write_dictionary(hard_path, vn_data.hard)
            write_dictionary(soft_path, vn_data.soft)
            write_sense_dictionary(senses_path, vn_data.senses)
            write_dictionary(scaffold_path, vn_data.scaffold)
            write_vn_provenance(provenance_path, vn_data.records)
            write_dictionary(massive_path, massive_data.entries)
            write_vn_dialogue_provenance(massive_provenance_path, massive_data)
            vn_output_rows = {
                hard_path: len(vn_data.hard),
                soft_path: len(vn_data.soft),
                senses_path: sum(map(len, vn_data.senses.values())),
                scaffold_path: len(vn_data.scaffold),
                provenance_path: len(vn_data.records),
                massive_path: len(massive_data.entries),
                massive_provenance_path: len(massive_data.record_ids_by_key),
            }
            write_vn_build_manifest(
                manifest_path,
                args.vn_source_lock,
                vn_output_rows,
                curated_path=args.curated_vn,
                vn_data=vn_data,
                massive_path=args.massive_archive,
                massive_data=massive_data,
            )

            staged_outputs = sorted(
                vn_output_rows,
                key=lambda path: path.name,
            ) + [manifest_path]
            # The manifest is committed last. Publication catches process-level
            # interruption as well as ordinary I/O errors and restores the old set.
            publish_vn_outputs(args.output, staged_outputs)

        print(
            "Curated visual-novel English/Russian: "
            f"{len(vn_data.records):,} records; "
            f"hard={len(vn_data.hard):,}, soft={len(vn_data.soft):,}, "
            f"senses={sum(map(len, vn_data.senses.values())):,}, "
            f"scaffold={len(vn_data.scaffold):,}"
        )
        print(
            "MASSIVE 1.1 English/Russian exact dialogue: "
            f"{len(massive_data.entries):,} entries from "
            f"{massive_data.statistics['rawPairs']:,} pairs"
        )
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
