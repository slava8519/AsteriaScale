#!/usr/bin/env python3
"""Fetch and normalize pinned public EN->RU benchmark datasets.

Network access only happens for the explicit ``fetch`` and ``fetch-import``
commands. Raw and normalized data live below ignored ``artifacts/Benchmarks``;
only this importer and its source manifest belong in git.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
import unicodedata
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path, PurePosixPath
from typing import Any, Callable, Iterable, Sequence


SCHEMA_VERSION = 1
MANIFEST_KIND = "asteria.translation-benchmark-source-manifest"
PROVENANCE_KIND = "asteria.translation-benchmark-dataset-provenance"
REPOSITORY_ROOT = Path(__file__).resolve().parent.parent
DEFAULT_MANIFEST = Path(__file__).resolve().with_name("benchmark-datasets.json")
DEFAULT_OUTPUT = REPOSITORY_ROOT / "artifacts" / "Benchmarks"
DOWNLOAD_CHUNK_SIZE = 1024 * 1024
USER_AGENT = "AsteriaScale-benchmark-fetcher/1"
HEX_SHA256 = re.compile(r"^[0-9a-f]{64}$")
DATASET_ID = re.compile(r"^[a-z0-9](?:[a-z0-9.-]{0,95})$")
PRODUCTION_WORD_REGEX = re.compile(r"[A-Za-z]+(?:['’\-][A-Za-z]+)*")
PRODUCTION_MAXIMUM_CHARACTERS = 1_000
PRODUCTION_MAXIMUM_WORDS = 64
SMART_HOVER_SELECTOR = "smart-hover-whole-source-v1"
PRODUCTION_GUARD_SELECTOR = "production-phrase-guard-v1"
MUCOW_SMART_BALANCED_SELECTOR = "mucow-smart-hover-balanced-v1"
MUCOW_SMART_ONE_PER_SENSE_SELECTOR = "mucow-smart-hover-one-per-sense-v1"
MUCOW_SMART_REPLICATION_SELECTOR = "mucow-smart-hover-replication-v1"
MUCOW_SMART_BLIND_SELECTOR = "mucow-smart-hover-blind-v3"
MUCOW_SMART_BLIND_SEED = "mucow-wsd-smart-hover-blind-v3"
MUCOW_SMART_BALANCED_SEED = "mucow-wsd-smart-hover-balanced-v1"
MUCOW_SMART_BALANCED_RECORDS = 400
MUCOW_DOMAIN_PRIORITY = {
    "books": 0,
    "eubooks": 1,
    "tatoeba": 2,
    "ted": 3,
    "opensubs": 4,
}
SENTENCE_TERMINATORS = frozenset(".!?…。！？")
OPENING_QUOTES = frozenset('"‘“«「『')
CLOSING_PUNCTUATION = frozenset('"\'’”)]}')
HONORIFIC_ABBREVIATIONS = frozenset(
    {
        "adm", "capt", "cmdr", "col", "cpt", "det", "dr", "fr", "gen",
        "gov", "hon", "insp", "lt", "mr", "mrs", "ms", "mx", "pres",
        "prof", "rep", "rev", "sen", "sgt", "supt",
    }
)
NUMERIC_LABEL_ABBREVIATIONS = frozenset({"eq", "fig", "no", "nos", "ref", "sec", "vol"})
LEXICAL_CONTINUATION_ABBREVIATIONS = frozenset({"a.k.a", "cf", "e.g", "i.e", "vs"})
CONTEXTUAL_ABBREVIATIONS = frozenset({"a.m", "approx", "dept", "est", "etc", "p.m", "pp"})
STANDALONE_UI_OR_UTTERANCE_WORDS = frozenset(
    {
        "auto", "back", "close", "config", "hello", "hey", "history",
        "load", "menu", "no", "ok", "okay", "please", "save", "settings",
        "skip", "sorry", "stop", "thanks", "wait", "yes",
    }
)
NON_SPEAKER_LEADING_WORDS = frozenset(
    {"he", "here", "i", "it", "she", "that", "these", "they", "this", "those", "there", "we", "you"}
)


class DatasetToolError(RuntimeError):
    """A deterministic, user-facing fetch or import failure."""


def canonical_json(value: object) -> str:
    return json.dumps(
        value,
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
        allow_nan=False,
    )


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(DOWNLOAD_CHUNK_SIZE), b""):
            digest.update(chunk)
    return digest.hexdigest()


def source_sha256(source: str) -> str:
    return hashlib.sha256(source.encode("utf-8")).hexdigest()


def _require_string(value: object, label: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise DatasetToolError(f"{label}: ожидается непустая строка.")
    return value


def _require_int(value: object, label: str, *, minimum: int = 0) -> int:
    if not isinstance(value, int) or isinstance(value, bool) or value < minimum:
        raise DatasetToolError(
            f"{label}: ожидается целое число не меньше {minimum}."
        )
    return value


def _validate_relative_path(value: object, label: str) -> str:
    path = _require_string(value, label)
    pure = PurePosixPath(path.replace("\\", "/"))
    if pure.is_absolute() or not pure.parts or any(part in {"", ".", ".."} for part in pure.parts):
        raise DatasetToolError(f"{label}: путь должен быть безопасным относительным путём.")
    if Path(path).is_absolute() or Path(path).drive:
        raise DatasetToolError(f"{label}: абсолютные пути запрещены.")
    return pure.as_posix()


def _validate_manifest(raw: object, manifest_path: Path) -> dict[str, Any]:
    if not isinstance(raw, dict):
        raise DatasetToolError(f"Манифест '{manifest_path}' должен содержать JSON-объект.")
    if raw.get("schemaVersion") != SCHEMA_VERSION or raw.get("kind") != MANIFEST_KIND:
        raise DatasetToolError(f"Манифест '{manifest_path}' имеет неподдерживаемую схему.")
    datasets = raw.get("datasets")
    if not isinstance(datasets, list) or not datasets:
        raise DatasetToolError(f"Манифест '{manifest_path}' не содержит datasets.")

    known_importers = {
        "wmt25-general",
        "wmt25-humeval",
        "wmt26-general",
        "voita-context",
        "mucow-wsd",
        "winomt-gender",
        "wmt24-biomedical",
        "wmt25-terminology",
    }
    dataset_ids: set[str] = set()
    for dataset_index, dataset in enumerate(datasets):
        label = f"datasets[{dataset_index}]"
        if not isinstance(dataset, dict):
            raise DatasetToolError(f"{label}: ожидается объект.")
        dataset_id = _require_string(dataset.get("id"), f"{label}.id")
        if not DATASET_ID.fullmatch(dataset_id):
            raise DatasetToolError(f"{label}.id: недопустимый идентификатор '{dataset_id}'.")
        if dataset_id in dataset_ids:
            raise DatasetToolError(f"Повторный dataset id '{dataset_id}'.")
        dataset_ids.add(dataset_id)
        _require_string(dataset.get("title"), f"{label}.title")
        _require_string(dataset.get("homepage"), f"{label}.homepage")
        _require_string(dataset.get("revision"), f"{label}.revision")
        _require_string(dataset.get("languagePair"), f"{label}.languagePair")
        importer = _require_string(dataset.get("importer"), f"{label}.importer")
        if importer not in known_importers:
            raise DatasetToolError(f"{label}.importer: неизвестный importer '{importer}'.")
        _require_int(dataset.get("expectedRecords"), f"{label}.expectedRecords", minimum=1)
        if "referenceFree" in dataset and not isinstance(dataset["referenceFree"], bool):
            raise DatasetToolError(f"{label}.referenceFree: ожидается bool.")
        license_info = dataset.get("license")
        if not isinstance(license_info, dict):
            raise DatasetToolError(f"{label}.license: ожидается объект.")
        _require_string(license_info.get("spdx"), f"{label}.license.spdx")
        _require_string(
            license_info.get("redistribution"),
            f"{label}.license.redistribution",
        )
        selections = dataset.get("derivedSelections", [])
        if not isinstance(selections, list):
            raise DatasetToolError(f"{label}.derivedSelections: ожидается массив.")
        selection_ids: set[str] = set()
        for selection_index, selection in enumerate(selections):
            selection_label = f"{label}.derivedSelections[{selection_index}]"
            if not isinstance(selection, dict):
                raise DatasetToolError(f"{selection_label}: ожидается объект.")
            selection_id = _require_string(selection.get("id"), f"{selection_label}.id")
            if not DATASET_ID.fullmatch(selection_id) or selection_id == dataset_id:
                raise DatasetToolError(f"{selection_label}.id: недопустимый идентификатор.")
            if selection_id in selection_ids or selection_id in dataset_ids:
                raise DatasetToolError(f"Повторный selection id '{selection_id}'.")
            selection_ids.add(selection_id)
            dataset_ids.add(selection_id)
            _require_string(selection.get("title"), f"{selection_label}.title")
            if selection.get("selector") not in {
                PRODUCTION_GUARD_SELECTOR,
                SMART_HOVER_SELECTOR,
                MUCOW_SMART_BALANCED_SELECTOR,
                MUCOW_SMART_ONE_PER_SENSE_SELECTOR,
                MUCOW_SMART_REPLICATION_SELECTOR,
                MUCOW_SMART_BLIND_SELECTOR,
            }:
                raise DatasetToolError(f"{selection_label}.selector: неизвестный selector.")
            _require_int(
                selection.get("expectedRecords"),
                f"{selection_label}.expectedRecords",
                minimum=1,
            )

        files = dataset.get("files")
        if not isinstance(files, list) or not files:
            raise DatasetToolError(f"{label}.files: ожидается непустой массив.")
        file_ids: set[str] = set()
        for file_index, file_info in enumerate(files):
            file_label = f"{label}.files[{file_index}]"
            if not isinstance(file_info, dict):
                raise DatasetToolError(f"{file_label}: ожидается объект.")
            file_id = _require_string(file_info.get("id"), f"{file_label}.id")
            if file_id in file_ids:
                raise DatasetToolError(f"{label}: повторный file id '{file_id}'.")
            file_ids.add(file_id)
            _require_string(file_info.get("role"), f"{file_label}.role")
            file_info["path"] = _validate_relative_path(
                file_info.get("path"), f"{file_label}.path"
            )
            url = _require_string(file_info.get("url"), f"{file_label}.url")
            if urllib.parse.urlparse(url).scheme not in {"https", "http", "file"}:
                raise DatasetToolError(f"{file_label}.url: неподдерживаемая схема URL.")
            _require_int(file_info.get("size"), f"{file_label}.size", minimum=1)
            sha = _require_string(file_info.get("sha256"), f"{file_label}.sha256").lower()
            if not HEX_SHA256.fullmatch(sha):
                raise DatasetToolError(f"{file_label}.sha256: ожидается SHA-256 в hex.")
            file_info["sha256"] = sha
            if "expectedRecords" in file_info:
                _require_int(
                    file_info["expectedRecords"],
                    f"{file_label}.expectedRecords",
                    minimum=1,
                )
    return raw


def load_manifest(path: Path = DEFAULT_MANIFEST) -> dict[str, Any]:
    try:
        raw = json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError) as error:
        raise DatasetToolError(f"Не удалось прочитать манифест '{path}': {error}") from error
    return _validate_manifest(raw, path)


def _dataset_map(manifest: dict[str, Any]) -> dict[str, dict[str, Any]]:
    return {dataset["id"]: dataset for dataset in manifest["datasets"]}


def select_datasets(
    manifest: dict[str, Any], requested: Sequence[str]
) -> list[dict[str, Any]]:
    if not requested:
        raise DatasetToolError("Укажите хотя бы один набор или 'all'.")
    if "all" in requested and len(requested) != 1:
        raise DatasetToolError("'all' нельзя сочетать с отдельными наборами.")
    by_id = _dataset_map(manifest)
    names = list(by_id) if requested == ["all"] else list(requested)
    if len(names) != len(set(names)):
        raise DatasetToolError("Один и тот же набор указан повторно.")
    unknown = [name for name in names if name not in by_id]
    if unknown:
        raise DatasetToolError(f"Неизвестные наборы: {', '.join(unknown)}.")
    return [by_id[name] for name in names]


def safe_join(base: Path, relative_path: str) -> Path:
    relative = _validate_relative_path(relative_path, "relative_path")
    resolved_base = base.resolve()
    candidate = (resolved_base / Path(relative)).resolve()
    try:
        candidate.relative_to(resolved_base)
    except ValueError as error:
        raise DatasetToolError(
            f"Путь '{relative_path}' выходит за пределы '{resolved_base}'."
        ) from error
    return candidate


def raw_file_path(output_root: Path, dataset: dict[str, Any], file_info: dict[str, Any]) -> Path:
    base = safe_join(output_root, f"upstream/{dataset['id']}")
    return safe_join(base, file_info["path"])


def _file_problem(path: Path, file_info: dict[str, Any]) -> str | None:
    if not path.is_file():
        return "файл отсутствует"
    actual_size = path.stat().st_size
    if actual_size != file_info["size"]:
        return f"размер {actual_size}, ожидалось {file_info['size']}"
    actual_sha = sha256_file(path)
    if actual_sha != file_info["sha256"]:
        return f"SHA-256 {actual_sha}, ожидалось {file_info['sha256']}"
    return None


def verify_dataset(output_root: Path, dataset: dict[str, Any]) -> list[tuple[dict[str, Any], Path, str | None]]:
    return [
        (file_info, path, _file_problem(path, file_info))
        for file_info in dataset["files"]
        for path in [raw_file_path(output_root, dataset, file_info)]
    ]


def require_verified(output_root: Path, dataset: dict[str, Any]) -> dict[str, Path]:
    problems = verify_dataset(output_root, dataset)
    failed = [f"{file_info['id']}: {problem}" for file_info, _, problem in problems if problem]
    if failed:
        raise DatasetToolError(
            f"Набор '{dataset['id']}' не прошёл проверку: " + "; ".join(failed)
        )
    return {file_info["id"]: path for file_info, path, _ in problems}


def fetch_file(destination: Path, file_info: dict[str, Any], timeout: float) -> bool:
    if _file_problem(destination, file_info) is None:
        return False
    destination.parent.mkdir(parents=True, exist_ok=True)
    partial = destination.with_name(destination.name + ".partial")
    if partial.exists():
        partial.unlink()
    digest = hashlib.sha256()
    downloaded = 0
    request = urllib.request.Request(
        file_info["url"],
        headers={"User-Agent": USER_AGENT},
    )
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response, partial.open("wb") as stream:
            while True:
                chunk = response.read(DOWNLOAD_CHUNK_SIZE)
                if not chunk:
                    break
                downloaded += len(chunk)
                if downloaded > file_info["size"]:
                    raise DatasetToolError(
                        f"'{file_info['id']}': получено больше данных, чем закреплено в манифесте."
                    )
                digest.update(chunk)
                stream.write(chunk)
        if downloaded != file_info["size"]:
            raise DatasetToolError(
                f"'{file_info['id']}': скачано {downloaded} байт, ожидалось {file_info['size']}."
            )
        actual_sha = digest.hexdigest()
        if actual_sha != file_info["sha256"]:
            raise DatasetToolError(
                f"'{file_info['id']}': SHA-256 {actual_sha}, ожидалось {file_info['sha256']}."
            )
        os.replace(partial, destination)
        return True
    except (OSError, urllib.error.URLError) as error:
        raise DatasetToolError(
            f"Не удалось скачать '{file_info['id']}' из {file_info['url']}: {error}"
        ) from error
    finally:
        if partial.exists():
            partial.unlink()


def fetch_dataset(output_root: Path, dataset: dict[str, Any], timeout: float) -> list[tuple[str, bool, Path]]:
    results: list[tuple[str, bool, Path]] = []
    for file_info in dataset["files"]:
        destination = raw_file_path(output_root, dataset, file_info)
        changed = fetch_file(destination, file_info, timeout)
        results.append((file_info["id"], changed, destination))
    return results


def _read_jsonl(path: Path) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    try:
        with path.open("r", encoding="utf-8-sig", newline="") as stream:
            for line_number, raw_line in enumerate(stream, 1):
                if not raw_line.strip():
                    continue
                try:
                    row = json.loads(raw_line)
                except json.JSONDecodeError as error:
                    raise DatasetToolError(
                        f"Некорректный JSONL '{path}', строка {line_number}: {error}"
                    ) from error
                if not isinstance(row, dict):
                    raise DatasetToolError(
                        f"'{path}', строка {line_number}: ожидается JSON-объект."
                    )
                rows.append(row)
    except OSError as error:
        raise DatasetToolError(f"Не удалось прочитать '{path}': {error}") from error
    return rows


def _read_json_array(path: Path) -> list[dict[str, Any]]:
    try:
        value = json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError) as error:
        raise DatasetToolError(f"Не удалось прочитать JSON '{path}': {error}") from error
    if isinstance(value, dict) and isinstance(value.get("data"), list):
        value = value["data"]
    if not isinstance(value, list) or any(not isinstance(row, dict) for row in value):
        raise DatasetToolError(f"'{path}' должен содержать массив JSON-объектов.")
    return value


def _row_text(row: dict[str, Any], field: str, label: str) -> str:
    return _require_string(row.get(field), f"{label}.{field}")


def _unique_strings(values: Iterable[object], *, excluding: str | None = None) -> list[str]:
    result: list[str] = []
    seen: set[str] = set()
    for value in values:
        if not isinstance(value, str) or not value.strip() or value == excluding or value in seen:
            continue
        seen.add(value)
        result.append(value)
    return result


def _unique_casefold_strings(values: Iterable[object]) -> list[str]:
    result: list[str] = []
    seen: set[str] = set()
    for value in values:
        if not isinstance(value, str) or not value.strip():
            continue
        key = value.casefold()
        if key in seen:
            continue
        seen.add(key)
        result.append(value)
    return result


def _surface_tokens(value: str) -> list[str]:
    return [
        token.lower().replace("ё", "е")
        for token in re.findall(r"[^\W\d_]+", value, flags=re.UNICODE)
    ]


def _contains_surface_target(value: str, target: str) -> bool:
    actual = _surface_tokens(value)
    expected = _surface_tokens(target)
    return bool(expected) and any(
        actual[index : index + len(expected)] == expected
        for index in range(len(actual) - len(expected) + 1)
    )


def _mucow_surface_proxy_compatible(
    reference: str,
    contrasts: Sequence[str],
    correct_targets: Sequence[str],
    forbidden_targets: Sequence[str],
) -> bool:
    gold_correct = any(
        _contains_surface_target(reference, target) for target in correct_targets
    )
    gold_forbidden = any(
        _contains_surface_target(reference, target) for target in forbidden_targets
    )
    contrast_clean = all(
        any(_contains_surface_target(contrast, target) for target in forbidden_targets)
        and not any(
            _contains_surface_target(contrast, target) for target in correct_targets
        )
        for contrast in contrasts
    )
    return gold_correct and not gold_forbidden and contrast_clean


def _collect_references(value: object) -> list[str]:
    if isinstance(value, str):
        return [value] if value.strip() else []
    if isinstance(value, list):
        return _unique_strings(item for entry in value for item in _collect_references(entry))
    if isinstance(value, dict):
        if isinstance(value.get("ref"), str):
            return [value["ref"]] if value["ref"].strip() else []
        return _unique_strings(
            item
            for key in sorted(value)
            for item in _collect_references(value[key])
        )
    return []


def utf16_length(value: str) -> int:
    """Match the UTF-16 code-unit indices and Length used by the .NET runner."""

    return len(value.encode("utf-16-le")) // 2


def _focus(source: str, needle: str, label: str) -> dict[str, Any]:
    match = re.search(re.escape(needle), source, flags=re.IGNORECASE)
    if match is None:
        raise DatasetToolError(f"{label}: focus '{needle}' не найден в source.")
    return {
        "text": source[match.start() : match.end()],
        "characterIndex": utf16_length(source[: match.start()]),
        "annotated": True,
    }


def _standalone_ascii_focus(
    source: str,
    needle: str,
    label: str,
) -> tuple[dict[str, Any], int]:
    """Resolve a MuCoW target as a complete ASCII lexical token."""

    pattern = re.compile(
        rf"(?<![A-Za-z0-9_]){re.escape(needle)}(?![A-Za-z0-9_])",
        flags=re.IGNORECASE,
    )
    matches = list(pattern.finditer(source))
    if not matches:
        raise DatasetToolError(f"{label}: standalone focus '{needle}' was not found in source.")
    match = matches[0]
    return (
        {
            "text": source[match.start() : match.end()],
            "characterIndex": utf16_length(source[: match.start()]),
            "annotated": True,
        },
        len(matches),
    )


def _guard_metadata(source: str) -> dict[str, Any]:
    word_count = len(PRODUCTION_WORD_REGEX.findall(source))
    source_characters = utf16_length(source)
    return {
        "modelGuardCompatible": (
            source_characters <= PRODUCTION_MAXIMUM_CHARACTERS
            and 1 <= word_count <= PRODUCTION_MAXIMUM_WORDS
        ),
        "smartHoverWholeSourceCompatible": _smart_hover_whole_source_compatible(source),
        "sourceCharacters": source_characters,
        "sourceWordCount": word_count,
        "productionGuard": {
            "maximumCharacters": PRODUCTION_MAXIMUM_CHARACTERS,
            "minimumWords": 1,
            "maximumWords": PRODUCTION_MAXIMUM_WORDS,
            "identity": "SmartPhraseContextBuilder-v1",
        },
    }


def _terminator_run_start(text: str, index: int) -> int:
    while index > 0 and text[index - 1] in SENTENCE_TERMINATORS:
        index -= 1
    return index


def _terminator_run_end(text: str, index: int) -> int:
    index += 1
    while index < len(text) and text[index] in SENTENCE_TERMINATORS:
        index += 1
    return index


def _next_content_character(text: str, start: int) -> int:
    while start < len(text) and (text[start].isspace() or text[start] in "([{"):
        start += 1
    return start


def _abbreviation_before(text: str, period_index: int) -> str:
    start = period_index
    while start > 0 and (text[start - 1].isalpha() or text[start - 1] == "."):
        start -= 1
    return text[start:period_index].strip(".")


def _is_non_terminal_period(text: str, run_start: int, run_end: int) -> bool:
    if run_end - run_start != 1 or text[run_start] != "." or run_start == 0:
        return False
    previous = run_start - 1
    if run_end < len(text):
        follows_decimal = text[previous].isdigit() and text[run_end].isdigit()
        follows_compact_abbreviation = (
            text[previous].isalpha()
            and text[run_end].isalpha()
            and (
                text[run_end].islower()
                or (run_end + 1 < len(text) and text[run_end + 1] == ".")
            )
        )
        if follows_decimal or follows_compact_abbreviation:
            return True
    next_index = _next_content_character(text, run_end)
    if next_index >= len(text):
        return False
    abbreviation = _abbreviation_before(text, run_start).casefold()
    if not abbreviation:
        return False
    if abbreviation in HONORIFIC_ABBREVIATIONS:
        return text[next_index].isupper()
    if abbreviation in NUMERIC_LABEL_ABBREVIATIONS:
        return text[next_index].isdigit()
    return (
        abbreviation in LEXICAL_CONTINUATION_ABBREVIATIONS
        or abbreviation in CONTEXTUAL_ABBREVIATIONS
    ) and (text[next_index].islower() or text[next_index].isdigit())


def _closes_utterance(text: str, run_start: int, run_end: int) -> bool:
    run = text[run_start:run_end]
    ellipsis = all(character in ".…" for character in run) and (
        "…" in run or len(run) > 1
    )
    if not ellipsis:
        return not _is_non_terminal_period(text, run_start, run_end)
    next_index = run_end
    while next_index < len(text) and text[next_index].isspace():
        next_index += 1
    return (
        next_index >= len(text)
        or text[next_index].isupper()
        or text[next_index] in OPENING_QUOTES
    )


def _containing_sentence_span(text: str, content_start: int, content_end: int) -> tuple[int, int]:
    start = 0
    index = content_start - 1
    while index >= 0:
        if text[index] not in SENTENCE_TERMINATORS:
            index -= 1
            continue
        run_start = _terminator_run_start(text, index)
        run_end = _terminator_run_end(text, run_start)
        if _closes_utterance(text, run_start, run_end):
            start = run_end
            break
        index = run_start - 1

    end = len(text)
    index = content_end
    while index < len(text):
        if text[index] not in SENTENCE_TERMINATORS:
            index += 1
            continue
        run_start = _terminator_run_start(text, index)
        run_end = _terminator_run_end(text, run_start)
        if _closes_utterance(text, run_start, run_end):
            end = run_end
            break
        index = run_end
    return start, end


def _speaker_name(value: str, *, standalone: bool) -> str | None:
    name = unicodedata.normalize("NFC", " ".join(value.split()))
    if not name or utf16_length(name) > 48 or not name[0].isupper():
        return None
    first_word = name.split(" ", 1)[0].casefold()
    if first_word in NON_SPEAKER_LEADING_WORDS or (
        standalone and name.casefold() in STANDALONE_UI_OR_UTTERANCE_WORDS
    ):
        return None
    words = 0
    inside_word = False
    for character in name:
        word_character = (
            character.isalpha()
            or unicodedata.category(character) == "Mn"
            or character in "-'’"
        )
        if word_character:
            if not inside_word:
                words += 1
                inside_word = True
            continue
        if not character.isspace():
            return None
        inside_word = False
    return name if 1 <= words <= 4 else None


def _has_leading_speaker_shape(text: str) -> bool:
    """Conservatively reject text production could trim as speaker metadata."""

    first = 0
    while first < len(text) and text[first].isspace():
        first += 1
    if first >= len(text):
        return False
    if text[first] == "[":
        closing = text.find("]", first + 1)
        if (
            first + 1 < closing <= first + 49
            and _speaker_name(text[first + 1 : closing], standalone=False) is not None
            and text[closing + 1 :].strip()
        ):
            return True
    line_breaks = [
        position
        for position in (text.find("\r", first), text.find("\n", first))
        if position >= 0
    ]
    if line_breaks:
        line_end = min(line_breaks)
        if (
            line_end > first
            and _speaker_name(text[first:line_end], standalone=True) is not None
            and text[line_end:].strip()
        ):
            return True
    colon = text.find(":", first)
    return (
        first < colon <= first + 48
        and _speaker_name(text[first:colon], standalone=False) is not None
        and bool(text[colon + 1 :].strip())
    )


def _smart_hover_whole_source_compatible(source: str) -> bool:
    """True only when the production hover phrase is the complete source.

    This mirrors the load-bearing sentence-boundary rules instead of treating a
    multi-sentence WMT segment as output that Smart Dictionary promises to
    translate. The selector is deliberately strict about trimming and unmatched
    opening quotes so public baselines and Asteria receive identical text.
    """

    if not source or source != source.strip() or _has_leading_speaker_shape(source):
        return False
    focus = PRODUCTION_WORD_REGEX.search(source)
    if focus is None:
        return False
    start, end = _containing_sentence_span(source, focus.start(), focus.end())
    while end < len(source) and source[end] in CLOSING_PUNCTUATION:
        end += 1
    phrase = source[start:end].strip()
    if phrase.startswith('"') and phrase.count('"') % 2 != 0:
        phrase = phrase[1:].lstrip()
    elif phrase.startswith("“") and "”" not in phrase:
        phrase = phrase[1:].lstrip()
    return phrase == source


def _base_record(
    case_id: str,
    source: str,
    references: list[str],
    *,
    reference_free: bool = False,
) -> dict[str, Any]:
    if not source.strip():
        raise DatasetToolError(f"'{case_id}': пустой source.")
    if not references and not reference_free:
        raise DatasetToolError(f"'{case_id}': отсутствует русский reference.")
    return {
        "schemaVersion": SCHEMA_VERSION,
        "id": case_id,
        "source": source,
        "sourceSha256": source_sha256(source),
        "sourceLanguage": "en",
        "targetLanguage": "ru",
        "references": references,
        "referenceFree": reference_free,
    }


def _import_wmt25_general(dataset: dict[str, Any], paths: dict[str, Path]) -> list[dict[str, Any]]:
    source_rows = [
        row
        for row in _read_jsonl(paths["dataset"])
        if row.get("src_lang") == "en" and row.get("tgt_lang") == "ru_RU"
    ]
    selected_ids = {
        _row_text(row, "doc_id", f"wmt25 source[{index}]")
        for index, row in enumerate(source_rows)
    }
    if len(selected_ids) != len(source_rows):
        raise DatasetToolError("WMT25 EN-RU содержит повторные doc_id.")

    baselines: dict[str, dict[str, str]] = {}
    for file_info in dataset["files"]:
        if file_info["role"] != "baseline":
            continue
        system_id = _require_string(file_info.get("systemId"), f"{file_info['id']}.systemId")
        by_doc: dict[str, str] = {}
        for row_index, row in enumerate(_read_jsonl(paths[file_info["id"]])):
            doc_id = row.get("doc_id")
            if not isinstance(doc_id, str) or doc_id not in selected_ids:
                continue
            hypothesis = _row_text(row, "hypothesis", f"{file_info['id']}[{row_index}]")
            if doc_id in by_doc:
                raise DatasetToolError(f"{file_info['id']}: повторный doc_id '{doc_id}'.")
            by_doc[doc_id] = hypothesis
        missing = sorted(selected_ids - set(by_doc))
        if missing:
            raise DatasetToolError(
                f"{file_info['id']}: нет baseline для {len(missing)} EN-RU документов."
            )
        baselines[system_id] = by_doc

    records: list[dict[str, Any]] = []
    for index, row in enumerate(source_rows):
        label = f"wmt25 source[{index}]"
        doc_id = _row_text(row, "doc_id", label)
        source = _row_text(row, "src_text", label)
        references = _collect_references(row.get("refs"))
        record = _base_record(f"wmt25-general:{doc_id}", source, references)
        domain = _row_text(row, "domain", label)
        record.update(
            {
                "domain": domain,
                "tags": ["general-mt", domain],
                "documentId": doc_id,
                "baselineTranslations": {
                    system_id: by_doc[doc_id]
                    for system_id, by_doc in sorted(baselines.items())
                },
                "metadata": {
                    "upstreamDatasetId": row.get("dataset_id"),
                    "upstreamCollectionId": row.get("collection_id"),
                    "hasScreenshot": bool(row.get("screenshot")),
                    "hasVideo": bool(row.get("video")),
                    **_guard_metadata(source),
                },
            }
        )
        records.append(record)
    return records


def _human_evaluation_summary(value: object, label: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise DatasetToolError(f"{label}: ожидается объект system -> annotations.")
    result: dict[str, Any] = {}
    for system_id in sorted(value):
        annotations = value[system_id]
        if not isinstance(annotations, list) or any(
            not isinstance(annotation, dict) for annotation in annotations
        ):
            raise DatasetToolError(f"{label}.{system_id}: ожидается массив аннотаций.")
        scores = [
            float(annotation["score"])
            for annotation in annotations
            if isinstance(annotation.get("score"), (int, float))
            and not isinstance(annotation.get("score"), bool)
        ]
        severity_counts: dict[str, int] = {}
        for annotation in annotations:
            errors = annotation.get("errors", [])
            if not isinstance(errors, list):
                continue
            for error in errors:
                if not isinstance(error, dict) or not isinstance(error.get("severity"), str):
                    continue
                severity = error["severity"]
                severity_counts[severity] = severity_counts.get(severity, 0) + 1
        result[system_id] = {
            "annotationCount": len(annotations),
            "scores": scores,
            "meanScore": sum(scores) / len(scores) if scores else None,
            "errorSeverityCounts": dict(sorted(severity_counts.items())),
        }
    return result


def _import_wmt25_humeval(dataset: dict[str, Any], paths: dict[str, Path]) -> list[dict[str, Any]]:
    rows = [
        row
        for row in _read_jsonl(paths["dataset"])
        if isinstance(row.get("doc_id"), str)
        and row["doc_id"].startswith("en-ru_RU_#_")
    ]
    records: list[dict[str, Any]] = []
    for row_index, row in enumerate(rows):
        label = f"wmt25-humeval[{row_index}]"
        doc_id = _row_text(row, "doc_id", label)
        source = _row_text(row, "src_text", label)
        translations = row.get("tgt_text")
        if not isinstance(translations, dict):
            raise DatasetToolError(f"{label}.tgt_text: ожидается объект system -> translation.")
        reference = _require_string(translations.get("refA"), f"{label}.tgt_text.refA")
        baselines = {
            system_id: translation
            for system_id, translation in sorted(translations.items())
            if system_id != "refA"
            and isinstance(system_id, str)
            and isinstance(translation, str)
        }
        if not baselines:
            raise DatasetToolError(f"{label}: отсутствуют baseline translations.")
        parts = doc_id.split("_#_")
        if len(parts) < 4:
            raise DatasetToolError(f"{label}.doc_id: неизвестный формат '{doc_id}'.")
        domain = parts[1]
        try:
            segment_index = int(parts[-1])
        except ValueError as error:
            raise DatasetToolError(f"{label}.doc_id: нет segment index.") from error
        document_id = "_#_".join(parts[:-1])
        record = _base_record(f"wmt25-humeval:{doc_id}", source, [reference])
        record.update(
            {
                "domain": domain,
                "tags": ["general-mt", "human-evaluation", domain],
                "documentId": document_id,
                "segmentIndex": segment_index,
                "baselineTranslations": baselines,
                "metadata": {
                    "humanEvaluation": _human_evaluation_summary(
                        row.get("scores"), f"{label}.scores"
                    ),
                    **_guard_metadata(source),
                },
            }
        )
        records.append(record)
    return records


def _import_wmt26_general(dataset: dict[str, Any], paths: dict[str, Path]) -> list[dict[str, Any]]:
    rows = [
        row
        for row in _read_jsonl(paths["dataset"])
        if row.get("src_lang") == "eng_Latn" and row.get("tgt_lang") == "rus_Cyrl"
    ]
    records: list[dict[str, Any]] = []
    for index, row in enumerate(rows):
        label = f"wmt26 source[{index}]"
        doc_id = _row_text(row, "doc_id", label)
        source = _row_text(row, "src_text", label)
        references = _collect_references(row.get("refs"))
        record = _base_record(
            f"wmt26-general:{doc_id}",
            source,
            references,
            reference_free=not references,
        )
        domain = _row_text(row, "domain", label)
        record.update(
            {
                "domain": domain,
                "tags": ["general-mt", domain, "source-only"],
                "documentId": doc_id,
                "metadata": {
                    "hasScreenshot": bool(row.get("screenshot")),
                    "hasVideo": bool(row.get("video")),
                    "hasGoldTranscript": bool(row.get("gold_transcript")),
                    **_guard_metadata(source),
                },
            }
        )
        records.append(record)
    return records


EOS_SPLIT = re.compile(r"\s*_eos\s*", flags=re.IGNORECASE)


def _context_parts(value: str, label: str) -> list[str]:
    parts = [part.strip() for part in EOS_SPLIT.split(value)]
    if len(parts) != 4 or any(not part for part in parts):
        raise DatasetToolError(f"{label}: ожидалось ровно 4 непустых сегмента через _eos.")
    return parts


def _import_voita_context(dataset: dict[str, Any], paths: dict[str, Path]) -> list[dict[str, Any]]:
    records: list[dict[str, Any]] = []
    for file_info in dataset["files"]:
        rows = _read_json_array(paths[file_info["id"]])
        expected = file_info.get("expectedRecords")
        if expected is not None and len(rows) != expected:
            raise DatasetToolError(
                f"{file_info['id']}: {len(rows)} записей, ожидалось {expected}."
            )
        for row_index, row in enumerate(rows):
            label = f"{file_info['id']}[{row_index}]"
            source_parts = _context_parts(_row_text(row, "src", label), f"{label}.src")
            candidates = row.get("dst")
            true_index = row.get("true_ind")
            if not isinstance(candidates, list) or not candidates:
                raise DatasetToolError(f"{label}.dst: ожидается непустой массив.")
            if not isinstance(true_index, int) or isinstance(true_index, bool) or not 0 <= true_index < len(candidates):
                raise DatasetToolError(f"{label}.true_ind: индекс вне массива dst.")
            target_parts = [
                _context_parts(_require_string(candidate, f"{label}.dst[{candidate_index}]"), f"{label}.dst[{candidate_index}]")
                for candidate_index, candidate in enumerate(candidates)
            ]
            reference = target_parts[true_index][-1]
            contrasts = _unique_strings(
                (parts[-1] for candidate_index, parts in enumerate(target_parts) if candidate_index != true_index),
                excluding=reference,
            )
            case_id = f"voita-context:{file_info['id']}:{row_index:05d}"
            record = _base_record(case_id, source_parts[-1], [reference])
            phenomenon = _require_string(file_info.get("phenomenon"), f"{file_info['id']}.phenomenon")
            split = _require_string(file_info.get("split"), f"{file_info['id']}.split")
            record.update(
                {
                    "previousContext": source_parts[:-1],
                    "contrastReferences": contrasts,
                    "contrastSetId": case_id,
                    "domain": "dialogue",
                    "tags": ["context", "contrastive", "adapted-generative", phenomenon, split],
                    "documentId": f"{file_info['id']}:{row_index:05d}",
                    "segmentIndex": 3,
                    "metadata": {
                        "contextDistance": row.get("ctx_dist"),
                        "upstreamTrueIndex": true_index,
                        "officialProtocol": "contrastive-target-likelihood",
                        "importProtocol": "generated-current-sentence",
                        "adaptationWarning": "Asteria scores generated current-sentence text; results are not directly comparable to the paper's target-likelihood score.",
                    },
                }
            )
            records.append(record)
    return records


def _import_mucow_wsd(dataset: dict[str, Any], paths: dict[str, Path]) -> list[dict[str, Any]]:
    rows = _read_json_array(paths["dataset"])
    records: list[dict[str, Any]] = []
    for row_index, row in enumerate(rows):
        label = f"mucow[{row_index}]"
        source = _row_text(row, "source", label)
        reference = _row_text(row, "reference", label)
        ambiguous = _row_text(row, "ambig word", label)
        errors = row.get("errors", [])
        if not isinstance(errors, list) or any(not isinstance(error, dict) for error in errors):
            raise DatasetToolError(f"{label}.errors: ожидается массив объектов.")
        contrasts = _unique_strings(
            (error.get("contrastive") for error in errors), excluding=reference
        )
        origin = _require_string(row.get("origin", "unknown"), f"{label}.origin")
        original_translation = _row_text(row, "original translation", label)
        sense = _row_text(row, "sense", label)
        correct_targets = _unique_casefold_strings(
            [original_translation, *sense.split("_")]
        )
        correct_target_set = {target.casefold() for target in correct_targets}
        forbidden_targets = _unique_strings(
            error.get("replacement")
            for error in errors
            if not isinstance(error.get("replacement"), str)
            or error["replacement"].casefold() not in correct_target_set
        )
        focus, focus_occurrence_count = _standalone_ascii_focus(
            source,
            ambiguous,
            label,
        )
        case_id = f"mucow-wsd:{row_index:05d}"
        record = _base_record(case_id, source, [reference])
        record.update(
            {
                "focus": focus,
                "termAnchors": [
                    {
                        "source": ambiguous,
                        "acceptable": correct_targets,
                        "match": "token",
                    }
                ],
                "contrastReferences": contrasts,
                "contrastSetId": case_id,
                "domain": origin,
                "tags": ["wsd", "lexical-ambiguity", origin],
                "documentId": str(row.get("id", row_index)),
                "metadata": {
                    "upstreamId": row.get("id"),
                    "sense": sense,
                    "originalTranslation": original_translation,
                    "focusOccurrenceCount": focus_occurrence_count,
                    "wsdCorrectTargets": correct_targets,
                    "wsdForbiddenTargets": forbidden_targets,
                    "wsdSurfaceProxyCompatible": _mucow_surface_proxy_compatible(
                        reference,
                        contrasts,
                        correct_targets,
                        forbidden_targets,
                    ),
                    **_guard_metadata(source),
                    "errorAnnotations": [
                        {
                            "type": error.get("type"),
                            "replacement": error.get("replacement"),
                        }
                        for error in errors
                    ],
                },
            }
        )
        records.append(record)
    return records


def _import_winomt_gender(dataset: dict[str, Any], paths: dict[str, Path]) -> list[dict[str, Any]]:
    try:
        lines = paths["dataset"].read_text(encoding="utf-8-sig").splitlines()
    except OSError as error:
        raise DatasetToolError(f"Не удалось прочитать WinoMT: {error}") from error
    records: list[dict[str, Any]] = []
    for line_index, line in enumerate(lines):
        if not line.strip():
            continue
        fields = line.split("\t")
        if len(fields) != 4:
            raise DatasetToolError(
                f"WinoMT, строка {line_index + 1}: ожидалось 4 TSV-поля, получено {len(fields)}."
            )
        gender, coreference_raw, source, occupation = fields
        try:
            coreference_index = int(coreference_raw)
        except ValueError as error:
            raise DatasetToolError(
                f"WinoMT, строка {line_index + 1}: некорректный coreference index."
            ) from error
        case_id = f"winomt-gender:{len(records):05d}"
        record = _base_record(case_id, source, [], reference_free=True)
        record.update(
            {
                "focus": _focus(source, occupation, f"WinoMT[{line_index}]") ,
                "domain": "synthetic",
                "tags": ["gender-bias", f"expected-{gender.lower()}"],
                "metadata": {
                    "expectedGender": gender.lower(),
                    "coreferenceIndex": coreference_index,
                    "occupation": occupation,
                    "requiredScorer": "target-gender-morphology",
                },
            }
        )
        records.append(record)
    return records


def _nonempty_lines(path: Path, label: str) -> list[str]:
    try:
        lines = path.read_text(encoding="utf-8-sig").splitlines()
    except OSError as error:
        raise DatasetToolError(f"Не удалось прочитать {label} '{path}': {error}") from error
    if any(not line.strip() for line in lines):
        raise DatasetToolError(f"{label}: пустые строки нарушают параллельное выравнивание.")
    return lines


def _import_wmt24_biomedical(dataset: dict[str, Any], paths: dict[str, Path]) -> list[dict[str, Any]]:
    sources = _nonempty_lines(paths["source"], "WMT24 biomedical source")
    references = _nonempty_lines(paths["reference"], "WMT24 biomedical reference")
    if len(sources) != len(references):
        raise DatasetToolError(
            f"WMT24 biomedical: {len(sources)} source-строк и {len(references)} reference-строк."
        )
    records: list[dict[str, Any]] = []
    for index, (source, reference) in enumerate(zip(sources, references, strict=True)):
        case_id = f"wmt24-biomedical:{index:04d}"
        record = _base_record(case_id, source, [reference])
        record.update(
            {
                "domain": "biomedical",
                "tags": ["scientific", "biomedical", "document"],
                "documentId": f"wmt24-biomedical:{index:04d}",
                "metadata": _guard_metadata(source),
            }
        )
        records.append(record)
    return records


def _term_anchors(value: object, label: str) -> list[dict[str, Any]]:
    if not isinstance(value, dict):
        raise DatasetToolError(f"{label}: ожидается объект term -> translation.")
    anchors: list[dict[str, Any]] = []
    for source_term in sorted(value):
        source = _require_string(source_term, f"{label}.source")
        target = value[source_term]
        if isinstance(target, str):
            anchors.append({"en": source, "ru": _require_string(target, f"{label}.{source}")})
        elif isinstance(target, list):
            acceptable = [
                _require_string(item, f"{label}.{source}[{index}]")
                for index, item in enumerate(target)
            ]
            if not acceptable:
                raise DatasetToolError(f"{label}.{source}: пустой список переводов.")
            anchors.append({"source": source, "acceptable": acceptable, "match": "phrase"})
        else:
            raise DatasetToolError(f"{label}.{source}: перевод должен быть строкой или массивом.")
    return anchors


def _import_wmt25_terminology(dataset: dict[str, Any], paths: dict[str, Path]) -> list[dict[str, Any]]:
    records: list[dict[str, Any]] = []
    for row_index, row in enumerate(_read_jsonl(paths["dataset"])):
        label = f"wmt25-terminology[{row_index}]"
        source = _row_text(row, "en", label)
        reference = _row_text(row, "ru", label)
        case_id = f"wmt25-terminology:{row_index:04d}"
        record = _base_record(case_id, source, [reference])
        record.update(
            {
                "domain": "information-technology",
                "tags": ["terminology", "technical"],
                "termAnchors": _term_anchors(row.get("proper"), f"{label}.proper"),
                "metadata": {
                    "randomTerms": row.get("random"),
                    "noTerm": row.get("noterm"),
                    **_guard_metadata(source),
                },
            }
        )
        records.append(record)
    return records


IMPORTERS: dict[str, Callable[[dict[str, Any], dict[str, Path]], list[dict[str, Any]]]] = {
    "wmt25-general": _import_wmt25_general,
    "wmt25-humeval": _import_wmt25_humeval,
    "wmt26-general": _import_wmt26_general,
    "voita-context": _import_voita_context,
    "mucow-wsd": _import_mucow_wsd,
    "winomt-gender": _import_winomt_gender,
    "wmt24-biomedical": _import_wmt24_biomedical,
    "wmt25-terminology": _import_wmt25_terminology,
}


def _validate_imported_records(dataset: dict[str, Any], records: list[dict[str, Any]]) -> None:
    expected = dataset["expectedRecords"]
    if len(records) != expected:
        raise DatasetToolError(
            f"{dataset['id']}: импортировано {len(records)} записей, ожидалось {expected}."
        )
    seen: set[str] = set()
    for index, record in enumerate(records):
        case_id = _require_string(record.get("id"), f"{dataset['id']}[{index}].id")
        if case_id in seen:
            raise DatasetToolError(f"{dataset['id']}: повторный canonical id '{case_id}'.")
        seen.add(case_id)
        _require_string(record.get("source"), f"{case_id}.source")
        references = record.get("references")
        if not isinstance(references, list) or any(
            not isinstance(reference, str) or not reference.strip() for reference in references
        ):
            raise DatasetToolError(f"{case_id}.references: ожидается массив непустых строк.")
        if not references and record.get("referenceFree") is not True:
            raise DatasetToolError(
                f"{case_id}: пустые references допустимы только при referenceFree=true."
            )


def _atomic_write_jsonl(path: Path, records: Sequence[dict[str, Any]]) -> tuple[str, int]:
    path.parent.mkdir(parents=True, exist_ok=True)
    partial = path.with_name(path.name + ".partial")
    if partial.exists():
        partial.unlink()
    digest = hashlib.sha256()
    byte_count = 0
    try:
        with partial.open("wb") as stream:
            for record in records:
                encoded = (canonical_json(record) + "\n").encode("utf-8")
                stream.write(encoded)
                digest.update(encoded)
                byte_count += len(encoded)
        os.replace(partial, path)
    finally:
        if partial.exists():
            partial.unlink()
    return digest.hexdigest(), byte_count


def _atomic_write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    partial = path.with_name(path.name + ".partial")
    if partial.exists():
        partial.unlink()
    try:
        payload = (json.dumps(value, ensure_ascii=False, sort_keys=True, indent=2, allow_nan=False) + "\n").encode("utf-8")
        partial.write_bytes(payload)
        os.replace(partial, path)
    finally:
        if partial.exists():
            partial.unlink()


def selection_sha256(
    records: Sequence[dict[str, Any]], dataset_artifact_sha256: str
) -> str:
    if not isinstance(dataset_artifact_sha256, str) or not HEX_SHA256.fullmatch(
        dataset_artifact_sha256.lower()
    ):
        raise DatasetToolError("Dataset artifact SHA-256 имеет неверный формат.")
    digest = hashlib.sha256()
    digest.update(b"translation-suite-selection-v2\n")
    digest.update(f"dataset\t{dataset_artifact_sha256.lower()}\n".encode("utf-8"))
    for record in records:
        case_id = _require_string(record.get("id"), "selection.id")
        source_hash = record.get("sourceSha256")
        if not isinstance(source_hash, str) or not HEX_SHA256.fullmatch(source_hash.lower()):
            raise DatasetToolError(f"{case_id}: отсутствует корректный sourceSha256.")
        digest.update(f"{case_id}\t{source_hash.lower()}\n".encode("utf-8"))
    return digest.hexdigest()


def _safe_system_id(value: str) -> str:
    safe = re.sub(r"[^a-z0-9._-]+", "-", value.casefold()).strip("-._")
    if not safe:
        safe = "system"
    return safe[:64]


def _materialize_baselines(
    output_root: Path,
    selection_id: str,
    records: Sequence[dict[str, Any]],
    selection_hash: str,
) -> list[dict[str, Any]]:
    if not records or any("baselineTranslations" not in record for record in records):
        return []
    common_systems = set(records[0]["baselineTranslations"])
    for record in records[1:]:
        common_systems.intersection_update(record["baselineTranslations"])
    common_systems = {
        system
        for system in common_systems
        if all(
            isinstance(record["baselineTranslations"].get(system), str)
            and record["baselineTranslations"][system].strip()
            for record in records
        )
    }
    if not common_systems:
        raise DatasetToolError(
            f"{selection_id}: нет public baseline, покрывающего всю selection."
        )
    safe_ids: dict[str, str] = {}
    for system_name in sorted(common_systems):
        safe_id = _safe_system_id(system_name)
        if safe_id in safe_ids:
            raise DatasetToolError(
                f"{selection_id}: baseline names '{safe_ids[safe_id]}' и "
                f"'{system_name}' дают одинаковый system id '{safe_id}'."
            )
        safe_ids[safe_id] = system_name

    artifacts: list[dict[str, Any]] = []
    for safe_id, system_name in sorted(safe_ids.items()):
        output_records = [
            {
                "schemaVersion": SCHEMA_VERSION,
                "id": record["id"],
                "sourceSha256": record["sourceSha256"],
                "hypothesis": record["baselineTranslations"][system_name],
                "status": "ok",
                "systemId": safe_id,
                "selectionSha256": selection_hash,
                "metadata": {"publicSystemName": system_name},
            }
            for record in records
        ]
        relative_path = f"normalized/baselines/{selection_id}/{safe_id}.jsonl"
        path = safe_join(output_root, relative_path)
        digest, size = _atomic_write_jsonl(path, output_records)
        artifacts.append(
            {
                "systemId": safe_id,
                "publicSystemName": system_name,
                "path": relative_path,
                "recordCount": len(output_records),
                "size": size,
                "sha256": digest,
                "selectionSha256": selection_hash,
            }
        )
    return artifacts


def _mucow_stable_rank(record: dict[str, Any]) -> str:
    digest = hashlib.sha256()
    digest.update(MUCOW_SMART_BALANCED_SEED.encode("utf-8"))
    digest.update(b"\0")
    digest.update(record["id"].encode("utf-8"))
    digest.update(b"\0")
    digest.update(record["sourceSha256"].encode("ascii"))
    return digest.hexdigest()


def _select_mucow_smart_balanced(
    records: Sequence[dict[str, Any]],
    expected_records: int = MUCOW_SMART_BALANCED_RECORDS,
) -> list[dict[str, Any]]:
    """Select two domain-diverse examples per sense without observing outputs."""

    eligible = [
        record
        for record in records
        if record.get("metadata", {}).get("modelGuardCompatible") is True
        and record.get("metadata", {}).get("smartHoverWholeSourceCompatible") is True
        and record.get("metadata", {}).get("focusOccurrenceCount") == 1
        and bool(record.get("contrastReferences"))
        and bool(record.get("metadata", {}).get("originalTranslation"))
        and record.get("metadata", {}).get("wsdSurfaceProxyCompatible") is True
    ]
    groups: dict[tuple[str, str], list[dict[str, Any]]] = {}
    for record in eligible:
        focus = record.get("focus", {}).get("text")
        sense = record.get("metadata", {}).get("sense")
        if not isinstance(focus, str) or not isinstance(sense, str):
            continue
        groups.setdefault((focus.casefold(), sense), []).append(record)

    chosen: list[tuple[tuple[str, str], int, dict[str, Any]]] = []
    for key in sorted(groups, key=lambda item: (item[0], item[1])):
        candidates = sorted(
            groups[key],
            key=lambda record: (
                MUCOW_DOMAIN_PRIORITY.get(record.get("domain"), 99),
                _mucow_stable_rank(record),
                record["id"],
            ),
        )
        first = candidates[0]
        chosen.append((key, 0, first))
        different_domain = [
            record for record in candidates[1:]
            if record.get("domain") != first.get("domain")
        ]
        remaining = different_domain or candidates[1:]
        if remaining and expected_records > 201:
            chosen.append((key, 1, remaining[0]))

    if len(groups) != 201 or len(chosen) < expected_records:
        raise DatasetToolError(
            "MuCoW balanced selector no longer has the pinned 201 sense groups "
            f"and {expected_records} examples."
        )
    while len(chosen) > expected_records:
        removable = [item for item in chosen if item[1] == 1]
        if not removable:
            raise DatasetToolError("MuCoW balanced selector cannot preserve every sense group.")
        remove = max(removable, key=lambda item: _mucow_stable_rank(item[2]))
        chosen.remove(remove)

    chosen.sort(key=lambda item: (item[0][0], item[0][1], item[1], item[2]["id"]))
    selected = [item[2] for item in chosen]
    if len({record["sourceSha256"] for record in selected}) != len(selected):
        raise DatasetToolError("MuCoW balanced selector produced duplicate source sentences.")
    return selected


def _mucow_blind_rank(record: dict[str, Any]) -> str:
    digest = hashlib.sha256()
    digest.update(MUCOW_SMART_BLIND_SEED.encode("utf-8"))
    digest.update(b"\0")
    digest.update(record["id"].encode("utf-8"))
    digest.update(b"\0")
    digest.update(record["sourceSha256"].encode("ascii"))
    return digest.hexdigest()


def _select_mucow_smart_blind(
    records: Sequence[dict[str, Any]],
    expected_records: int,
) -> list[dict[str, Any]]:
    """Select one further example per sense, disjoint from dev201 and replication199.

    The MuCoW EN-RU suite holds only 203 sense groups and the eligibility filter
    keeps 201, so a blind set cannot introduce unseen words or senses: the corpus
    is exhausted. This slice therefore repeats the replication construction on a
    third source sentence per group, which keeps it directly comparable to the
    earlier two and keeps their known limitation — it varies context, not the
    lexical inventory.

    Selection observes no model output: eligibility, grouping and ordering are
    the pinned ones, and the only new input is a distinct seed.
    """

    development = _select_mucow_smart_balanced(records, 201)
    balanced = _select_mucow_smart_balanced(records, MUCOW_SMART_BALANCED_RECORDS)
    spent = {record["sourceSha256"] for record in balanced}
    spent.update(record["sourceSha256"] for record in development)

    eligible = [
        record
        for record in records
        if record.get("metadata", {}).get("modelGuardCompatible") is True
        and record.get("metadata", {}).get("smartHoverWholeSourceCompatible") is True
        and record.get("metadata", {}).get("focusOccurrenceCount") == 1
        and bool(record.get("contrastReferences"))
        and bool(record.get("metadata", {}).get("originalTranslation"))
        and record.get("metadata", {}).get("wsdSurfaceProxyCompatible") is True
        and record["sourceSha256"] not in spent
    ]
    groups: dict[tuple[str, str], list[dict[str, Any]]] = {}
    for record in eligible:
        focus = record.get("focus", {}).get("text")
        sense = record.get("metadata", {}).get("sense")
        if not isinstance(focus, str) or not isinstance(sense, str):
            continue
        groups.setdefault((focus.casefold(), sense), []).append(record)

    selected: list[dict[str, Any]] = []
    for key in sorted(groups):
        candidates = sorted(
            groups[key],
            key=lambda record: (
                MUCOW_DOMAIN_PRIORITY.get(record.get("domain"), 99),
                _mucow_blind_rank(record),
                record["id"],
            ),
        )
        selected.append(candidates[0])

    if len(selected) != expected_records:
        raise DatasetToolError(
            f"MuCoW blind selector produced {len(selected)} records, "
            f"expected {expected_records}."
        )
    if {record["sourceSha256"] for record in selected} & spent:
        raise DatasetToolError("MuCoW blind selector reused a spent source sentence.")
    if len({record["sourceSha256"] for record in selected}) != len(selected):
        raise DatasetToolError("MuCoW blind selector produced duplicate source sentences.")
    return selected


def _select_mucow_smart_replication(
    records: Sequence[dict[str, Any]],
) -> list[dict[str, Any]]:
    """Return only the second, source-disjoint example from the balanced slice."""

    development = _select_mucow_smart_balanced(records, 201)
    development_ids = {record["id"] for record in development}
    balanced = _select_mucow_smart_balanced(records, MUCOW_SMART_BALANCED_RECORDS)
    replication = [record for record in balanced if record["id"] not in development_ids]
    if len(replication) != MUCOW_SMART_BALANCED_RECORDS - len(development):
        raise DatasetToolError("MuCoW replication selector is no longer disjoint and pinned.")
    return replication


def import_dataset(
    output_root: Path,
    dataset: dict[str, Any],
    manifest_path: Path = DEFAULT_MANIFEST,
) -> list[tuple[Path, Path, int, str]]:
    paths = require_verified(output_root, dataset)
    records = IMPORTERS[dataset["importer"]](dataset, paths)
    _validate_imported_records(dataset, records)
    selections: list[tuple[dict[str, Any], list[dict[str, Any]]]] = [
        (
            {
                "id": dataset["id"],
                "title": dataset["title"],
                "selector": "all",
                "expectedRecords": dataset["expectedRecords"],
            },
            records,
        )
    ]
    for selection in dataset.get("derivedSelections", []):
        selector = selection["selector"]
        if selector == PRODUCTION_GUARD_SELECTOR:
            selected = [
                record
                for record in records
                if record.get("metadata", {}).get("modelGuardCompatible") is True
            ]
        elif selector == SMART_HOVER_SELECTOR:
            selected = [
                record
                for record in records
                if record.get("metadata", {}).get("modelGuardCompatible") is True
                and record.get("metadata", {}).get(
                    "smartHoverWholeSourceCompatible"
                ) is True
            ]
        elif selector == MUCOW_SMART_BALANCED_SELECTOR:
            selected = _select_mucow_smart_balanced(
                records,
                selection["expectedRecords"],
            )
        elif selector == MUCOW_SMART_ONE_PER_SENSE_SELECTOR:
            selected = _select_mucow_smart_balanced(
                records,
                selection["expectedRecords"],
            )
        elif selector == MUCOW_SMART_REPLICATION_SELECTOR:
            selected = _select_mucow_smart_replication(records)
        elif selector == MUCOW_SMART_BLIND_SELECTOR:
            selected = _select_mucow_smart_blind(records, selection["expectedRecords"])
        else:
            raise DatasetToolError(f"Неизвестный selector '{selector}'.")
        if len(selected) != selection["expectedRecords"]:
            raise DatasetToolError(
                f"{selection['id']}: отобрано {len(selected)} записей, "
                f"ожидалось {selection['expectedRecords']}."
            )
        selections.append((selection, selected))

    manifest_sha = sha256_file(manifest_path)
    outputs: list[tuple[Path, Path, int, str]] = []
    for selection, selected_records in selections:
        selection_id = selection["id"]
        normalized_path = safe_join(output_root, f"normalized/{selection_id}.jsonl")
        provenance_path = safe_join(
            output_root, f"normalized/{selection_id}.provenance.json"
        )
        normalized_sha, normalized_size = _atomic_write_jsonl(
            normalized_path, selected_records
        )
        selection_hash = selection_sha256(selected_records, normalized_sha)
        baseline_artifacts = _materialize_baselines(
            output_root,
            selection_id,
            selected_records,
            selection_hash,
        )
        provenance = {
            "schemaVersion": SCHEMA_VERSION,
            "kind": PROVENANCE_KIND,
            "dataset": {
                "id": selection_id,
                "sourceDatasetId": dataset["id"],
                "title": selection["title"],
                "homepage": dataset["homepage"],
                "revision": dataset["revision"],
                "languagePair": dataset["languagePair"],
                "license": dataset["license"],
                "referenceFree": bool(dataset.get("referenceFree", False)),
                "selector": selection["selector"],
            },
            "sourceManifest": {
                "file": manifest_path.name,
                "sha256": manifest_sha,
            },
            "upstreamFiles": [
                {
                    "id": file_info["id"],
                    "role": file_info["role"],
                    "path": file_info["path"],
                    "url": file_info["url"],
                    "size": file_info["size"],
                    "sha256": file_info["sha256"],
                    **(
                        {"systemId": file_info["systemId"]}
                        if "systemId" in file_info
                        else {}
                    ),
                }
                for file_info in dataset["files"]
            ],
            "normalized": {
                "path": f"normalized/{selection_id}.jsonl",
                "recordCount": len(selected_records),
                "size": normalized_size,
                "sha256": normalized_sha,
                "serialization": "canonical-jsonl-utf8-lf-v1",
                "selectionSha256": selection_hash,
            },
            "baselineOutputs": baseline_artifacts,
        }
        compatible = sum(
            bool(record.get("metadata", {}).get("modelGuardCompatible"))
            for record in selected_records
        )
        provenance["normalized"]["productionModelGuardCompatibleRecords"] = compatible
        if dataset["id"] == "wmt25-general":
            provenance["normalized"]["comparisonScope"] = (
                "external-document-baseline; not a live TranslateGemma sentence suite"
            )
        elif selection["selector"] == PRODUCTION_GUARD_SELECTOR:
            provenance["normalized"]["comparisonScope"] = (
                "live TranslateGemma-compatible public segment selection"
            )
            provenance["normalized"]["selectionRule"] = {
                "maximumCharacters": PRODUCTION_MAXIMUM_CHARACTERS,
                "minimumWords": 1,
                "maximumWords": PRODUCTION_MAXIMUM_WORDS,
                "tokenRegex": PRODUCTION_WORD_REGEX.pattern,
            }
        elif selection["selector"] == SMART_HOVER_SELECTOR:
            provenance["normalized"]["comparisonScope"] = (
                "Smart Dictionary whole-hover-phrase public segment selection"
            )
            provenance["normalized"]["selectionRule"] = {
                "baseSelector": PRODUCTION_GUARD_SELECTOR,
                "sentenceBoundaryIdentity": "SentenceBoundaryRules-v1",
                "speakerPrefixPolicy": "conservative-reject-v1",
                "requiresWholeSourcePhrase": True,
            }
        elif selection["selector"] in {
            MUCOW_SMART_BALANCED_SELECTOR,
            MUCOW_SMART_ONE_PER_SENSE_SELECTOR,
            MUCOW_SMART_REPLICATION_SELECTOR,
        }:
            provenance["normalized"]["comparisonScope"] = (
                "MuCoW lexical-ambiguity Smart Dictionary downstream A/B"
            )
            provenance["normalized"]["selectionRule"] = {
                "seed": MUCOW_SMART_BALANCED_SEED,
                "requiresWholeSourcePhrase": True,
                "requiresUniqueStandaloneFocus": True,
                "requiresContrastReference": True,
                "maximumPerFocusSense": (
                    1
                    if selection["selector"] in {
                        MUCOW_SMART_ONE_PER_SENSE_SELECTOR,
                        MUCOW_SMART_REPLICATION_SELECTOR,
                    }
                    else 2
                ),
                "preservesEveryEligibleFocusSense":
                    selection["selector"] != MUCOW_SMART_REPLICATION_SELECTOR,
                "focusSenseGroups": (
                    199
                    if selection["selector"] == MUCOW_SMART_REPLICATION_SELECTOR
                    else 201
                ),
                "disjointFromSelection": (
                    "mucow-wsd-smart-hover-one-per-sense-v1"
                    if selection["selector"] == MUCOW_SMART_REPLICATION_SELECTOR
                    else None
                ),
                "domainPriority": list(MUCOW_DOMAIN_PRIORITY),
            }
        _atomic_write_json(provenance_path, provenance)
        outputs.append(
            (normalized_path, provenance_path, len(selected_records), normalized_sha)
        )
    return outputs


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Закреплённая загрузка и нормализация публичных EN→RU бенчмарков."
    )
    parser.add_argument(
        "--manifest",
        type=Path,
        default=DEFAULT_MANIFEST,
        help="Путь к benchmark-datasets.json.",
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=DEFAULT_OUTPUT,
        help="Каталог для raw/normalized artifacts.",
    )
    subparsers = parser.add_subparsers(dest="command", required=True)
    subparsers.add_parser("list", help="Показать доступные закреплённые наборы.")
    for command, help_text in (
        ("fetch", "Явно скачать выбранные raw-файлы."),
        ("import", "Нормализовать уже скачанные и проверенные raw-файлы."),
        ("fetch-import", "Скачать, затем нормализовать выбранные наборы."),
        ("verify", "Проверить локальные raw-файлы по size + SHA-256."),
    ):
        child = subparsers.add_parser(command, help=help_text)
        child.add_argument("datasets", nargs="+", help="ID наборов или all.")
        if command in {"fetch", "fetch-import"}:
            child.add_argument(
                "--timeout",
                type=float,
                default=120.0,
                help="Тайм-аут одного HTTP-запроса в секундах.",
            )
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    arguments = _parser().parse_args(argv)
    try:
        manifest_path = arguments.manifest.resolve()
        manifest = load_manifest(manifest_path)
        output_root = arguments.output.resolve()
        if arguments.command == "list":
            for dataset in manifest["datasets"]:
                marker = " · без русских reference" if dataset.get("referenceFree") else ""
                print(
                    f"{dataset['id']}: {dataset['expectedRecords']} записей · "
                    f"{dataset['license']['spdx']}{marker}\n  {dataset['title']}"
                )
                for selection in dataset.get("derivedSelections", []):
                    print(
                        f"  -> {selection['id']}: {selection['expectedRecords']} записей · "
                        f"{selection['title']}"
                    )
            return 0

        datasets = select_datasets(manifest, arguments.datasets)
        if arguments.command in {"fetch", "fetch-import"}:
            if arguments.timeout <= 0:
                raise DatasetToolError("--timeout должен быть положительным.")
            for dataset in datasets:
                for file_id, changed, path in fetch_dataset(
                    output_root, dataset, arguments.timeout
                ):
                    action = "скачан" if changed else "уже проверен"
                    print(f"{dataset['id']}/{file_id}: {action} -> {path}")

        if arguments.command in {"import", "fetch-import"}:
            for dataset in datasets:
                for normalized, provenance, count, digest in import_dataset(
                    output_root, dataset, manifest_path
                ):
                    print(
                        f"{normalized.stem}: {count} записей, SHA-256 {digest}\n"
                        f"  {normalized}\n  {provenance}"
                    )

        if arguments.command == "verify":
            failures = 0
            for dataset in datasets:
                for file_info, path, problem in verify_dataset(output_root, dataset):
                    if problem:
                        failures += 1
                        print(f"ОШИБКА {dataset['id']}/{file_info['id']}: {problem} ({path})")
                    else:
                        print(f"OK {dataset['id']}/{file_info['id']}: {path}")
            return 1 if failures else 0
        return 0
    except DatasetToolError as error:
        print(f"Ошибка: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
