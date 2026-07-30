#!/usr/bin/env python3
"""Offline scoring and reporting for Asteria translation benchmarks.

The tool deliberately uses only the Python standard library. Neural metrics are
run elsewhere and imported as fingerprint-bound sidecars, so producing or
viewing a benchmark report never requires network access or an ML runtime.
"""

from __future__ import annotations

import argparse
import csv
import datetime as dt
import hashlib
import html
import io
import itertools
import json
import math
import os
import re
import random
import statistics
import sys
import tempfile
import unicodedata
from collections import Counter
from pathlib import Path
from typing import Any, Iterable, Sequence


SCHEMA_VERSION = 1
REPORT_KIND = "asteria.translation-benchmark-run"
METRIC_INPUT_KIND = "asteria.translation-metric-input"
METRIC_EVALUATION_KIND = "asteria.translation-metric-evaluation"
LEADERBOARD_KIND = "asteria.translation-benchmark-leaderboard"
PAIRED_COMPARISON_KIND = "asteria.translation-benchmark-paired-comparison"
SCORING_CONTRACT = "asteria.translation-score-v2"
OUTPUT_FINGERPRINT_DOMAIN = "asteria.translation-outputs-v2"
SENSE_OUTCOME_METRIC_ID = "asteria.mucow-surface-outcome@2"
SENSE_OUTCOME_METHOD = (
    "full-positive-list-exact-token-yo-fold-no-lemmatization"
)
SENSE_EVIDENCE_DOMAIN = "asteria-smart-sense-evidence-v1\n"

_DATASET_FIELDS = {
    "schemaVersion",
    "id",
    "source",
    "sourceSha256",
    "sourceLanguage",
    "targetLanguage",
    "references",
    "referenceFree",
    "previousContext",
    "focus",
    "speaker",
    "termAnchors",
    "contrastReferences",
    "contrastSetId",
    "domain",
    "tags",
    "documentId",
    "segmentIndex",
    "license",
    "baselineTranslations",
    "metadata",
}
_OUTPUT_FIELDS = {
    "schemaVersion",
    "id",
    "sourceSha256",
    "hypothesis",
    "status",
    "latencyMilliseconds",
    "errorCode",
    "errorMessage",
    "focus",
    "fromCache",
    "systemId",
    "profile",
    "backend",
    "ranker",
    "phraseBackend",
    "rankerCpuModel",
    "engine",
    "modelIdentity",
    "promptIdentity",
    "selectionSha256",
    "metadata",
}
_OUTPUT_STATUSES = {
    "ok",
    "unavailable",
    "error",
    "timeout",
    "rejected",
    "empty",
    "missing",
}
_HEX_64 = re.compile(r"^[0-9a-fA-F]{64}$")
_SMART_METADATA_FIELDS = {
    "dictionaryEngine",
    "dictionaryFound",
    "dictionaryIsPhrase",
    "dictionaryTranslation",
    "senseEvidence",
    "senseEvidenceSha256",
    "appliedSenseEvidence",
    "appliedSenseEvidenceSha256",
}


class BenchmarkError(ValueError):
    """A user-facing input or report validation error."""


def utc_now() -> str:
    return dt.datetime.now(dt.timezone.utc).isoformat().replace("+00:00", "Z")


def canonical_json(value: object) -> str:
    return json.dumps(
        value,
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
        allow_nan=False,
    )


def canonical_records_sha256(domain: str, records: Iterable[object]) -> str:
    digest = hashlib.sha256()
    digest.update((domain + "\n").encode("utf-8"))
    for record in records:
        digest.update(canonical_json(record).encode("utf-8"))
        digest.update(b"\n")
    return digest.hexdigest()


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def source_sha256(source: str) -> str:
    return sha256_bytes(source.encode("utf-8"))


def selection_sha256(
    records: Sequence[dict[str, Any]], dataset_artifact_sha256: str
) -> str:
    """Match TranslationSuiteRunner.CreateSelectionSha256 exactly."""

    if not isinstance(dataset_artifact_sha256, str) or not _HEX_64.fullmatch(
        dataset_artifact_sha256
    ):
        raise BenchmarkError("Dataset artifact SHA-256 имеет неверный формат.")
    lines = [
        "translation-suite-selection-v2\n",
        f"dataset\t{dataset_artifact_sha256.lower()}\n",
    ]
    for record in records:
        lines.append(
            f"{record['id']}\t{source_sha256(record['source']).lower()}\n"
        )
    return sha256_bytes("".join(lines).encode("utf-8"))


def read_json(path: Path) -> dict[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError) as error:
        raise BenchmarkError(f"Не удалось прочитать JSON '{path}': {error}") from error
    if not isinstance(value, dict):
        raise BenchmarkError(f"JSON '{path}' должен содержать объект.")
    return value


def read_jsonl(path: Path) -> list[dict[str, Any]]:
    records: list[dict[str, Any]] = []
    try:
        with path.open("r", encoding="utf-8-sig", newline="") as stream:
            for line_number, raw_line in enumerate(stream, 1):
                line = raw_line.strip()
                if not line:
                    continue
                try:
                    value = json.loads(line)
                except json.JSONDecodeError as error:
                    raise BenchmarkError(
                        f"Некорректный JSONL '{path}', строка {line_number}: {error}"
                    ) from error
                if not isinstance(value, dict):
                    raise BenchmarkError(
                        f"JSONL '{path}', строка {line_number}: ожидается объект."
                    )
                records.append(value)
    except OSError as error:
        raise BenchmarkError(f"Не удалось прочитать '{path}': {error}") from error
    return records


def _reject_unknown(record: dict[str, Any], allowed: set[str], label: str) -> None:
    unknown = sorted(set(record) - allowed)
    if unknown:
        raise BenchmarkError(
            f"{label}: неизвестные поля: {', '.join(unknown)}. "
            "Дополнительные данные следует помещать в metadata."
        )


def _require_nonempty_string(value: object, label: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise BenchmarkError(f"{label}: ожидается непустая строка.")
    return value


def _validate_string_array(
    value: object,
    label: str,
    *,
    allow_empty: bool = True,
) -> list[str]:
    if not isinstance(value, list) or (not allow_empty and not value):
        suffix = "непустой массив строк" if not allow_empty else "массив строк"
        raise BenchmarkError(f"{label}: ожидается {suffix}.")
    if any(not isinstance(item, str) or not item.strip() for item in value):
        raise BenchmarkError(f"{label}: все элементы должны быть непустыми строками.")
    return list(value)


def utf16_length(value: str) -> int:
    """Return the number of UTF-16 code units used by .NET string indices."""

    return len(value.encode("utf-16-le")) // 2


def _has_unsafe_controls(value: str) -> bool:
    """Mirror .NET char.IsControl while allowing benchmark text whitespace."""

    return any(
        unicodedata.category(character) == "Cc"
        and character not in {"\r", "\n", "\t"}
        for character in value
    )


def _utf16_slice(value: str, index: int, length: int) -> str | None:
    encoded = value.encode("utf-16-le")
    start = index * 2
    end = (index + length) * 2
    if start < 0 or end > len(encoded):
        return None
    try:
        return encoded[start:end].decode("utf-16-le")
    except UnicodeDecodeError:
        return None


def _validate_focus(value: object, source: str, label: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise BenchmarkError(f"{label}: focus должен быть объектом.")
    allowed = {"text", "characterIndex", "annotated"}
    _reject_unknown(value, allowed, f"{label}.focus")
    text = _require_nonempty_string(value.get("text"), f"{label}.focus.text")
    index = value.get("characterIndex")
    if not isinstance(index, int) or isinstance(index, bool) or index < 0:
        raise BenchmarkError(
            f"{label}.focus.characterIndex: ожидается неотрицательное целое число."
        )
    if _utf16_slice(source, index, utf16_length(text)) != text:
        raise BenchmarkError(f"{label}.focus не совпадает с точным span исходника.")
    annotated = value.get("annotated", True)
    if not isinstance(annotated, bool):
        raise BenchmarkError(f"{label}.focus.annotated должен быть bool.")
    return {
        "text": text,
        "characterIndex": index,
        "annotated": annotated,
    }


def _validate_anchors(value: object, label: str) -> list[dict[str, Any]]:
    if value is None:
        return []
    if not isinstance(value, list):
        raise BenchmarkError(f"{label}: termAnchors должен быть массивом.")
    result: list[dict[str, Any]] = []
    allowed_fields = {"source", "acceptable", "targets", "match", "en", "ru"}
    for index, anchor in enumerate(value):
        anchor_label = f"{label}.termAnchors[{index}]"
        if not isinstance(anchor, dict):
            raise BenchmarkError(f"{anchor_label}: ожидается объект.")
        _reject_unknown(anchor, allowed_fields, anchor_label)
        normalized = dict(anchor)
        if "en" in anchor or "ru" in anchor:
            _require_nonempty_string(anchor.get("en"), f"{anchor_label}.en")
            _require_nonempty_string(anchor.get("ru"), f"{anchor_label}.ru")
            if "acceptable" in anchor or "targets" in anchor:
                raise BenchmarkError(
                    f"{anchor_label}: legacy ru нельзя смешивать с acceptable/targets."
                )
        else:
            if "source" in anchor:
                _require_nonempty_string(anchor["source"], f"{anchor_label}.source")
            alternatives = anchor.get("acceptable", anchor.get("targets"))
            _validate_string_array(
                alternatives,
                f"{anchor_label}.acceptable",
                allow_empty=False,
            )
        match = anchor.get("match")
        if match is not None and match not in {"token", "phrase", "stem"}:
            raise BenchmarkError(
                f"{anchor_label}.match: ожидается token, phrase или stem."
            )
        result.append(normalized)
    return result


def validate_dataset_records(records: Sequence[dict[str, Any]]) -> list[dict[str, Any]]:
    if not records:
        raise BenchmarkError("Набор не содержит ни одного случая.")
    normalized: list[dict[str, Any]] = []
    ids: set[str] = set()
    for index, raw in enumerate(records):
        label = f"dataset[{index}]"
        _reject_unknown(raw, _DATASET_FIELDS, label)
        schema_version = raw.get("schemaVersion", SCHEMA_VERSION)
        if schema_version != SCHEMA_VERSION:
            raise BenchmarkError(f"{label}: неподдерживаемая schemaVersion {schema_version}.")
        case_id = _require_nonempty_string(raw.get("id"), f"{label}.id")
        if case_id in ids:
            raise BenchmarkError(f"Повторный id в dataset: '{case_id}'.")
        ids.add(case_id)
        source = _require_nonempty_string(raw.get("source"), f"{label}.source")
        if utf16_length(source) > 100_000 or _has_unsafe_controls(source):
            raise BenchmarkError(
                f"{label}.source слишком велик или содержит небезопасные управляющие символы."
            )
        supplied_source_hash = raw.get("sourceSha256")
        exact_source_hash = source_sha256(source)
        if supplied_source_hash is not None and (
            not isinstance(supplied_source_hash, str)
            or not _HEX_64.fullmatch(supplied_source_hash)
            or supplied_source_hash.lower() != exact_source_hash
        ):
            raise BenchmarkError(
                f"{label}.sourceSha256 не соответствует точному UTF-8 source."
            )
        reference_free = raw.get("referenceFree", False)
        if not isinstance(reference_free, bool):
            raise BenchmarkError(f"{label}.referenceFree должен быть bool.")
        references = _validate_string_array(
            raw.get("references"),
            f"{label}.references",
            allow_empty=reference_free,
        )
        if len(references) != len(set(references)):
            raise BenchmarkError(f"{label}.references содержит повторы.")
        if not references and not reference_free:
            raise BenchmarkError(
                f"{label}: пустые references допустимы только при referenceFree=true."
            )
        previous_context = _validate_string_array(
            raw.get("previousContext", []),
            f"{label}.previousContext",
        )
        contrast_references = _validate_string_array(
            raw.get("contrastReferences", []),
            f"{label}.contrastReferences",
        )
        if contrast_references and not references:
            raise BenchmarkError(
                f"{label}: contrastReferences требуют хотя бы один правильный reference."
            )
        tags = _validate_string_array(raw.get("tags", []), f"{label}.tags")
        focus = (
            _validate_focus(raw["focus"], source, label) if "focus" in raw else None
        )
        speaker = raw.get("speaker")
        if speaker is not None:
            if not isinstance(speaker, dict):
                raise BenchmarkError(f"{label}.speaker должен быть объектом.")
            _reject_unknown(speaker, {"name", "gender"}, f"{label}.speaker")
            _require_nonempty_string(speaker.get("name"), f"{label}.speaker.name")
            if speaker.get("gender") not in {"masculine", "feminine", "unknown"}:
                raise BenchmarkError(
                    f"{label}.speaker.gender: ожидается masculine, feminine или unknown."
                )
        segment_index = raw.get("segmentIndex")
        if segment_index is not None and (
            not isinstance(segment_index, int)
            or isinstance(segment_index, bool)
            or segment_index < 0
        ):
            raise BenchmarkError(f"{label}.segmentIndex должен быть неотрицательным int.")
        for field, expected in (("sourceLanguage", "en"), ("targetLanguage", "ru")):
            if field in raw and raw[field] != expected:
                raise BenchmarkError(f"{label}.{field}: ожидается '{expected}'.")
        for field in ("contrastSetId", "domain", "documentId"):
            if field in raw:
                _require_nonempty_string(raw[field], f"{label}.{field}")
        if "license" in raw and not isinstance(raw["license"], (str, dict)):
            raise BenchmarkError(f"{label}.license должен быть строкой или объектом.")
        metadata = raw.get("metadata")
        if metadata is not None and not isinstance(metadata, dict):
            raise BenchmarkError(f"{label}.metadata должен быть объектом.")
        anchors = _validate_anchors(raw.get("termAnchors", []), label)
        baseline_translations = raw.get("baselineTranslations")
        if baseline_translations is not None and (
            not isinstance(baseline_translations, dict)
            or any(
                not isinstance(system, str)
                or not system.strip()
                or not isinstance(translation, str)
                for system, translation in baseline_translations.items()
            )
        ):
            raise BenchmarkError(
                f"{label}.baselineTranslations должен быть объектом string -> string."
            )

        record = dict(raw)
        record["schemaVersion"] = SCHEMA_VERSION
        record["id"] = case_id
        record["source"] = source
        record["sourceSha256"] = exact_source_hash
        record["references"] = references
        record["referenceFree"] = reference_free
        record["previousContext"] = previous_context
        record["termAnchors"] = anchors
        record["contrastReferences"] = contrast_references
        record["tags"] = tags
        if focus is not None:
            record["focus"] = focus
        normalized.append(record)
    return normalized


def validate_output_records(
    records: Sequence[dict[str, Any]],
    dataset: Sequence[dict[str, Any]],
    expected_system_id: str,
) -> list[dict[str, Any]]:
    dataset_by_id = {item["id"]: item for item in dataset}
    outputs_by_id: dict[str, dict[str, Any]] = {}
    for index, raw in enumerate(records):
        label = f"outputs[{index}]"
        _reject_unknown(raw, _OUTPUT_FIELDS, label)
        schema_version = raw.get("schemaVersion", SCHEMA_VERSION)
        if schema_version != SCHEMA_VERSION:
            raise BenchmarkError(f"{label}: неподдерживаемая schemaVersion {schema_version}.")
        case_id = _require_nonempty_string(raw.get("id"), f"{label}.id")
        if case_id in outputs_by_id:
            raise BenchmarkError(f"Повторный id в outputs: '{case_id}'.")
        if case_id not in dataset_by_id:
            raise BenchmarkError(f"Outputs содержит неизвестный id '{case_id}'.")
        system_id = raw.get("systemId")
        if system_id is not None and system_id != expected_system_id:
            raise BenchmarkError(
                f"{label}.systemId='{system_id}' не совпадает с '{expected_system_id}'."
            )
        status = raw.get("status", "ok")
        if status not in _OUTPUT_STATUSES - {"missing"}:
            raise BenchmarkError(f"{label}.status: неизвестное значение '{status}'.")
        hypothesis = raw.get("hypothesis", "")
        if not isinstance(hypothesis, str):
            raise BenchmarkError(f"{label}.hypothesis должен быть строкой.")
        if status == "ok" and not hypothesis.strip():
            status = "empty"
        if status == "ok" and "hypothesis" not in raw:
            raise BenchmarkError(f"{label}: status=ok требует hypothesis.")
        for field in (
            "errorCode",
            "errorMessage",
            "profile",
            "backend",
            "ranker",
            "phraseBackend",
            "rankerCpuModel",
            "engine",
            "modelIdentity",
            "promptIdentity",
        ):
            if raw.get(field) is not None and not isinstance(raw[field], str):
                raise BenchmarkError(f"{label}.{field} должен быть строкой или null.")
        from_cache = raw.get("fromCache")
        if from_cache is not None and not isinstance(from_cache, bool):
            raise BenchmarkError(f"{label}.fromCache должен быть bool или null.")
        metadata = raw.get("metadata")
        if metadata is not None and not isinstance(metadata, dict):
            raise BenchmarkError(f"{label}.metadata должен быть объектом.")
        latency = raw.get("latencyMilliseconds")
        if latency is not None and (
            not isinstance(latency, (int, float))
            or isinstance(latency, bool)
            or not math.isfinite(float(latency))
            or float(latency) < 0
        ):
            raise BenchmarkError(
                f"{label}.latencyMilliseconds должен быть конечным неотрицательным числом."
            )
        source_hash = raw.get("sourceSha256")
        expected_hash = source_sha256(dataset_by_id[case_id]["source"])
        if source_hash is not None:
            if not isinstance(source_hash, str) or not _HEX_64.fullmatch(source_hash):
                raise BenchmarkError(f"{label}.sourceSha256 имеет неверный формат.")
            if source_hash.lower() != expected_hash:
                raise BenchmarkError(
                    f"{label}.sourceSha256 не соответствует точному source."
                )
        selection_hash = raw.get("selectionSha256")
        if selection_hash is not None and (
            not isinstance(selection_hash, str)
            or not _HEX_64.fullmatch(selection_hash)
        ):
            raise BenchmarkError(f"{label}.selectionSha256 имеет неверный формат.")
        actual_focus = None
        if raw.get("focus") is not None:
            actual_focus = _validate_focus(
                raw["focus"], dataset_by_id[case_id]["source"], label
            )
        expected_focus = dataset_by_id[case_id].get("focus")
        if expected_focus is not None and actual_focus is not None:
            if (
                expected_focus["text"] != actual_focus["text"]
                or expected_focus["characterIndex"] != actual_focus["characterIndex"]
                or expected_focus["annotated"] != actual_focus["annotated"]
            ):
                raise BenchmarkError(f"{label}.focus не совпадает с dataset focus.")
        normalized = dict(raw)
        normalized["schemaVersion"] = SCHEMA_VERSION
        normalized["id"] = case_id
        normalized["sourceSha256"] = expected_hash
        if selection_hash is not None:
            normalized["selectionSha256"] = selection_hash.lower()
        normalized["hypothesis"] = hypothesis
        normalized["status"] = status
        if actual_focus is not None:
            normalized["focus"] = actual_focus
        if normalized.get("profile") == "smart":
            _validate_sense_evidence_metadata(normalized, label)
        outputs_by_id[case_id] = normalized

    aligned: list[dict[str, Any]] = []
    for item in dataset:
        case_id = item["id"]
        if case_id in outputs_by_id:
            aligned.append(outputs_by_id[case_id])
        else:
            aligned.append(
                {
                    "schemaVersion": SCHEMA_VERSION,
                    "id": case_id,
                    "sourceSha256": source_sha256(item["source"]),
                    "hypothesis": "",
                    "status": "missing",
                    "errorCode": "missing_output",
                }
            )
    return aligned


def _strip_for_chrf(value: str) -> str:
    return "".join(character.lower() for character in value if not character.isspace())


def _ngrams(value: str, width: int) -> Counter[str]:
    return Counter(value[index : index + width] for index in range(len(value) - width + 1))


def chrf2(hypothesis: str, reference: str, maximum_n: int = 6, beta: float = 2.0) -> float:
    """Match the dependency-free C# chrF2 implementation used by Asteria."""

    left = _strip_for_chrf(hypothesis)
    right = _strip_for_chrf(reference)
    if not left or not right:
        return 0.0
    precision_sum = 0.0
    recall_sum = 0.0
    counted = 0
    for width in range(1, maximum_n + 1):
        hypothesis_grams = _ngrams(left, width)
        reference_grams = _ngrams(right, width)
        if not hypothesis_grams or not reference_grams:
            continue
        common = sum(
            min(count, reference_grams.get(gram, 0))
            for gram, count in hypothesis_grams.items()
        )
        precision_sum += common / sum(hypothesis_grams.values())
        recall_sum += common / sum(reference_grams.values())
        counted += 1
    if counted == 0:
        return 0.0
    precision = precision_sum / counted
    recall = recall_sum / counted
    beta_squared = beta * beta
    denominator = beta_squared * precision + recall
    return 0.0 if denominator <= 0 else (1 + beta_squared) * precision * recall / denominator


def multi_reference_chrf2(hypothesis: str, references: Sequence[str]) -> float | None:
    if not references:
        return None
    return max(chrf2(hypothesis, reference) for reference in references)


def _letter_tokens(value: str) -> list[str]:
    result: list[str] = []
    current: list[str] = []
    for character in value:
        if character.isalpha():
            current.append(character)
        elif current:
            result.append("".join(current))
            current.clear()
    if current:
        result.append("".join(current))
    return result


def residual_latin_rate(value: str) -> float:
    words = _letter_tokens(value)
    if not words:
        return 0.0
    latin = sum(
        all("a" <= character.lower() <= "z" for character in word) for word in words
    )
    return latin / len(words)


def _fold_russian(value: str) -> str:
    return value.lower().replace("ё", "е")


def _anchor_alternatives(anchor: dict[str, Any]) -> tuple[list[str], str]:
    if "ru" in anchor:
        return [anchor["ru"]], anchor.get("match", "stem")
    values = anchor.get("acceptable", anchor.get("targets", []))
    return list(values), anchor.get("match", "phrase")


def contains_term_anchor(hypothesis: str, anchor: dict[str, Any]) -> bool:
    output_tokens = [_fold_russian(token) for token in _letter_tokens(hypothesis)]
    alternatives, match = _anchor_alternatives(anchor)
    for alternative in alternatives:
        expected = [_fold_russian(token) for token in _letter_tokens(alternative)]
        if not expected:
            continue
        for start in range(0, len(output_tokens) - len(expected) + 1):
            actual = output_tokens[start : start + len(expected)]
            if match == "stem":
                matched = all(token.startswith(prefix) for token, prefix in zip(actual, expected))
            else:
                matched = actual == expected
            if matched:
                return True
    return False


def _sense_targets(case: dict[str, Any]) -> tuple[list[str], list[str]] | None:
    metadata = case.get("metadata")
    if not isinstance(metadata, dict):
        return None
    if metadata.get("wsdSurfaceProxyCompatible") is False:
        return None
    correct = metadata.get("wsdCorrectTargets")
    forbidden = metadata.get("wsdForbiddenTargets")
    if (
        not isinstance(correct, list)
        or not correct
        or any(not isinstance(item, str) or not item.strip() for item in correct)
        or not isinstance(forbidden, list)
        or any(not isinstance(item, str) or not item.strip() for item in forbidden)
    ):
        return None
    return correct, forbidden


def _sense_outcome(
    text: str,
    correct_targets: Sequence[str],
    forbidden_targets: Sequence[str],
) -> str:
    correct = any(
        contains_term_anchor(
            text,
            {"acceptable": [target], "match": "token"},
        )
        for target in correct_targets
    )
    forbidden = any(
        contains_term_anchor(
            text,
            {"acceptable": [target], "match": "token"},
        )
        for target in forbidden_targets
    )
    if correct and forbidden:
        return "mixed"
    if correct:
        return "correct"
    if forbidden:
        return "wrong"
    return "unresolved"


def _contains_standalone_ascii_focus(source: str, focus: str) -> bool:
    if not source or not focus:
        return False
    pattern = rf"(?<![A-Za-z]){re.escape(focus)}(?![A-Za-z])"
    return re.search(pattern, source, flags=re.IGNORECASE) is not None


def _hover_sense_text(case: dict[str, Any], output: dict[str, Any]) -> str:
    metadata = output.get("metadata")
    if not isinstance(metadata, dict):
        return ""
    focus = case.get("focus")
    focus_text = focus.get("text", "") if isinstance(focus, dict) else ""
    evidence = metadata.get("senseEvidence")
    selected: list[str] = []
    if isinstance(evidence, list):
        for item in evidence:
            if not isinstance(item, dict):
                continue
            source = item.get("source")
            translation = item.get("selectedTranslation")
            if (
                isinstance(source, str)
                and isinstance(translation, str)
                and _contains_standalone_ascii_focus(source, focus_text)
                and translation.strip()
            ):
                selected.append(translation)
    if selected:
        return " ".join(selected)
    dictionary_translation = metadata.get("dictionaryTranslation")
    return dictionary_translation if isinstance(dictionary_translation, str) else ""


def _sense_summary(counts: Counter[str]) -> dict[str, Any]:
    evaluated = sum(counts.values())
    resolved = counts["correct"] + counts["wrong"] + counts["mixed"]
    return {
        "metricId": SENSE_OUTCOME_METRIC_ID,
        "method": SENSE_OUTCOME_METHOD,
        "evaluated": evaluated,
        "correct": counts["correct"],
        "wrong": counts["wrong"],
        "mixed": counts["mixed"],
        "unresolved": counts["unresolved"],
        "resolved": resolved,
        "accuracyResolved": counts["correct"] / resolved if resolved else None,
        "accuracyIntentionToTreat": counts["correct"] / evaluated if evaluated else None,
        "forbiddenRate": (
            (counts["wrong"] + counts["mixed"]) / evaluated
            if evaluated
            else None
        ),
    }


def nearest_rank_percentile(values: Sequence[float], percentile: float) -> float | None:
    if not values:
        return None
    if percentile <= 0 or percentile > 1:
        raise ValueError("percentile must be in (0, 1]")
    ordered = sorted(values)
    index = max(0, min(len(ordered) - 1, math.ceil(percentile * len(ordered)) - 1))
    return ordered[index]


def _mean(values: Sequence[float]) -> float | None:
    return statistics.fmean(values) if values else None


def _median(values: Sequence[float]) -> float | None:
    return statistics.median(values) if values else None


def _output_fingerprint_records(outputs: Sequence[dict[str, Any]]) -> list[dict[str, Any]]:
    # The report binds every quality- and provenance-relevant output field,
    # including smart-ranker evidence. The former v1 projection covered only
    # id/status/hypothesis and could not detect metadata or latency drift.
    return list(outputs)


def _utf16_code_units(value: str) -> int:
    return len(value.encode("utf-16-le")) // 2


def sense_evidence_sha256(evidence: Sequence[dict[str, Any]]) -> str:
    canonical = [SENSE_EVIDENCE_DOMAIN]
    for index, item in enumerate(evidence):
        if not isinstance(item, dict):
            raise BenchmarkError(f"sense evidence [{index}] должен быть объектом.")
        if set(item) != {"source", "englishDefinition", "selectedTranslation"}:
            raise BenchmarkError(
                f"sense evidence [{index}] имеет неизвестные или отсутствующие поля."
            )
        for field in ("source", "englishDefinition", "selectedTranslation"):
            value = item.get(field)
            if not isinstance(value, str):
                raise BenchmarkError(
                    f"sense evidence [{index}].{field} должен быть строкой."
                )
            canonical.append(f"{_utf16_code_units(value)}:{value}\n")
    return hashlib.sha256("".join(canonical).encode("utf-8")).hexdigest()


def _validate_sense_evidence_metadata(output: dict[str, Any], label: str) -> None:
    metadata = output.get("metadata")
    status = output.get("status")
    if not isinstance(metadata, dict):
        if status == "ok":
            raise BenchmarkError(f"{label}: успешный smart output не содержит metadata.")
        return
    if set(metadata) != _SMART_METADATA_FIELDS:
        raise BenchmarkError(
            f"{label}.metadata имеет неизвестные или отсутствующие smart-поля."
        )
    if not isinstance(metadata.get("dictionaryEngine"), str):
        raise BenchmarkError(f"{label}.metadata.dictionaryEngine должен быть строкой.")
    if not isinstance(metadata.get("dictionaryTranslation"), str):
        raise BenchmarkError(
            f"{label}.metadata.dictionaryTranslation должен быть строкой."
        )
    for field in ("dictionaryFound", "dictionaryIsPhrase"):
        if not isinstance(metadata.get(field), bool):
            raise BenchmarkError(f"{label}.metadata.{field} должен быть bool.")

    selected = metadata.get("senseEvidence")
    selected_sha256 = metadata.get("senseEvidenceSha256")
    if not isinstance(selected, list):
        raise BenchmarkError(f"{label}: senseEvidence должен быть массивом.")
    if len(selected) > 256:
        raise BenchmarkError(f"{label}: senseEvidence превышает безопасный предел.")
    expected_selected = sense_evidence_sha256(selected)
    if selected_sha256 != expected_selected:
        raise BenchmarkError(f"{label}: senseEvidenceSha256 не совпадает с evidence.")

    applied = metadata.get("appliedSenseEvidence")
    applied_sha256 = metadata.get("appliedSenseEvidenceSha256")
    if status == "ok":
        if not isinstance(applied, list):
            raise BenchmarkError(
                f"{label}: успешный smart output не содержит appliedSenseEvidence."
            )
        if len(applied) > 256:
            raise BenchmarkError(
                f"{label}: appliedSenseEvidence превышает безопасный предел."
            )
        expected_applied = sense_evidence_sha256(applied)
        if applied_sha256 != expected_applied:
            raise BenchmarkError(
                f"{label}: appliedSenseEvidenceSha256 не совпадает с evidence."
            )
    elif applied not in (None, []) or applied_sha256 is not None:
        raise BenchmarkError(
            f"{label}: неуспешный output не мог применить sense evidence к переводу."
        )


def score_cases(
    dataset: Sequence[dict[str, Any]], outputs: Sequence[dict[str, Any]]
) -> tuple[list[dict[str, Any]], dict[str, Any]]:
    case_results: list[dict[str, Any]] = []
    statuses: Counter[str] = Counter()
    chrf_values: list[float] = []
    residual_values: list[float] = []
    anchor_correct = 0
    anchor_total = 0
    contrast_correct = 0
    contrast_ties = 0
    contrast_scored = 0
    translation_sense_outcomes: Counter[str] = Counter()
    hover_sense_outcomes: Counter[str] = Counter()
    all_latencies: list[float] = []
    successful_latencies: list[float] = []

    for case, output in zip(dataset, outputs, strict=True):
        status = output["status"]
        successful = status == "ok" and bool(output["hypothesis"].strip())
        statuses[status] += 1
        hypothesis = output["hypothesis"] if successful else ""
        references = case["references"]
        case_chrf = multi_reference_chrf2(hypothesis, references)
        if case_chrf is not None:
            chrf_values.append(case_chrf)

        anchors = case.get("termAnchors", [])
        matched_anchors = sum(
            successful and contains_term_anchor(hypothesis, anchor) for anchor in anchors
        )
        anchor_correct += matched_anchors
        anchor_total += len(anchors)

        residual = residual_latin_rate(hypothesis) if successful else None
        if residual is not None:
            residual_values.append(residual)

        contrast_references = case.get("contrastReferences", [])
        contrast_outcome: str | None = None
        best_contrast: float | None = None
        if contrast_references:
            contrast_scored += 1
            if successful and case_chrf is not None:
                best_contrast = max(chrf2(hypothesis, item) for item in contrast_references)
                if case_chrf > best_contrast + 1e-12:
                    contrast_outcome = "correct"
                    contrast_correct += 1
                elif abs(case_chrf - best_contrast) <= 1e-12:
                    contrast_outcome = "tie"
                    contrast_ties += 1
                else:
                    contrast_outcome = "incorrect"
            else:
                contrast_outcome = "incorrect"

        translation_sense_outcome: str | None = None
        hover_sense_outcome: str | None = None
        targets = _sense_targets(case)
        if targets is not None:
            if output.get("profile") == "smart":
                _validate_sense_evidence_metadata(
                    output,
                    f"output[{case['id']}]",
                )
            correct_targets, forbidden_targets = targets
            translation_sense_outcome = _sense_outcome(
                hypothesis,
                correct_targets,
                forbidden_targets,
            )
            hover_sense_outcome = _sense_outcome(
                _hover_sense_text(case, output),
                correct_targets,
                forbidden_targets,
            )
            translation_sense_outcomes[translation_sense_outcome] += 1
            hover_sense_outcomes[hover_sense_outcome] += 1

        latency = output.get("latencyMilliseconds")
        if latency is not None:
            numeric_latency = float(latency)
            all_latencies.append(numeric_latency)
            if successful:
                successful_latencies.append(numeric_latency)

        case_results.append(
            {
                "input": case,
                "output": output,
                "metrics": {
                    "successful": successful,
                    "chrF2": case_chrf,
                    "termAnchorsCorrect": matched_anchors,
                    "termAnchorsTotal": len(anchors),
                    "residualLatinRate": residual,
                    "bestContrastChrF2": best_contrast,
                    "contrastiveOutcome": contrast_outcome,
                    "translationSenseOutcome": translation_sense_outcome,
                    "hoverSenseOutcome": hover_sense_outcome,
                },
            }
        )

    total = len(dataset)
    successful_count = statuses["ok"]
    summary = {
        "cases": total,
        "successful": successful_count,
        "successRate": successful_count / total,
        "statuses": dict(sorted(statuses.items())),
        "chrF2": {
            "metricId": "asteria.macro-chrf2@1",
            "higherIsBetter": True,
            "referenceScoredCases": len(chrf_values),
            "mean": _mean(chrf_values),
            "median": _median(chrf_values),
            "minimum": min(chrf_values) if chrf_values else None,
            "maximum": max(chrf_values) if chrf_values else None,
        },
        "termAnchors": {
            "correct": anchor_correct,
            "total": anchor_total,
            "recall": anchor_correct / anchor_total if anchor_total else None,
        },
        "residualLatin": {
            "successfulCases": len(residual_values),
            "meanSuccessful": _mean(residual_values),
            "casesWithResidual": sum(value > 0 for value in residual_values),
        },
        "contrastive": {
            "scored": contrast_scored,
            "correct": contrast_correct,
            "ties": contrast_ties,
            "accuracy": contrast_correct / contrast_scored if contrast_scored else None,
            "method": "best-gold-chrf2-strictly-greater-than-best-known-wrong-chrf2",
        },
        "translationSenseOutcome": _sense_summary(translation_sense_outcomes),
        "hoverSenseOutcome": _sense_summary(hover_sense_outcomes),
        "latencyMilliseconds": {
            "samples": len(all_latencies),
            "p50": nearest_rank_percentile(all_latencies, 0.50),
            "p95": nearest_rank_percentile(all_latencies, 0.95),
            "p99": nearest_rank_percentile(all_latencies, 0.99),
            "maximum": max(all_latencies) if all_latencies else None,
            "successfulSamples": len(successful_latencies),
            "successfulP50": nearest_rank_percentile(successful_latencies, 0.50),
            "successfulP95": nearest_rank_percentile(successful_latencies, 0.95),
        },
    }
    return case_results, summary


def _consistent_output_value(outputs: Sequence[dict[str, Any]], field: str) -> Any:
    values = [item[field] for item in outputs if field in item and item[field] is not None]
    if not values:
        return None
    serialized = {canonical_json(value): value for value in values}
    if len(serialized) != 1:
        raise BenchmarkError(f"Outputs содержит разные значения системного поля '{field}'.")
    return next(iter(serialized.values()))


def _consistent_output_alias(
    outputs: Sequence[dict[str, Any]], *fields: str
) -> Any:
    values: list[Any] = []
    for item in outputs:
        present = [item[field] for field in fields if item.get(field) is not None]
        if len({canonical_json(value) for value in present}) > 1:
            raise BenchmarkError(
                "Outputs содержит конфликтующие алиасы полей: " + ", ".join(fields)
            )
        if present:
            values.append(present[0])
    if not values:
        return None
    serialized = {canonical_json(value): value for value in values}
    if len(serialized) != 1:
        raise BenchmarkError(
            "Outputs содержит разные значения системного поля: " + ", ".join(fields)
        )
    return next(iter(serialized.values()))


def build_score_report(
    dataset_path: Path,
    outputs_path: Path | Sequence[Path],
    system_id: str,
    system_name: str | None = None,
    *,
    dataset_id: str | None = None,
    dataset_version: str | None = None,
    run_id: str | None = None,
    selection_report_path: Path | None = None,
) -> dict[str, Any]:
    system_id = _require_nonempty_string(system_id, "system-id")
    complete_dataset = validate_dataset_records(read_jsonl(dataset_path))
    dataset_artifact_sha256 = sha256_file(dataset_path)
    dataset = complete_dataset
    selection: dict[str, Any] | None = None
    run_provenance: dict[str, Any] | None = None
    if selection_report_path is not None:
        selection_document = read_json(selection_report_path)
        selected_ids = selection_document.get("selectedCaseIds")
        if (
            selection_document.get("schemaVersion") != SCHEMA_VERSION
            or not isinstance(selected_ids, list)
            or not selected_ids
            or any(not isinstance(item, str) or not item for item in selected_ids)
            or len(selected_ids) != len(set(selected_ids))
        ):
            raise BenchmarkError("Selection report имеет неверный selectedCaseIds.")
        selection_hash = selection_document.get("selectionSha256")
        if not isinstance(selection_hash, str) or not _HEX_64.fullmatch(selection_hash):
            raise BenchmarkError("Selection report имеет неверный selectionSha256.")
        complete_by_id = {item["id"]: item for item in complete_dataset}
        unknown = [item for item in selected_ids if item not in complete_by_id]
        if unknown:
            raise BenchmarkError(
                "Selection report содержит неизвестные case id: " + ", ".join(unknown)
            )
        dataset = [complete_by_id[item] for item in selected_ids]
        expected_selection_hash = selection_sha256(
            dataset, dataset_artifact_sha256
        )
        if selection_hash.lower() != expected_selection_hash:
            raise BenchmarkError(
                "Selection report selectionSha256 не соответствует выбранным cases."
            )
        selection = {
            "selectionSha256": expected_selection_hash,
            "selectedCaseIds": selected_ids,
            "maximumCases": selection_document.get("maximumCases"),
            "shardIndex": selection_document.get("shardIndex"),
            "shardCount": selection_document.get("shardCount"),
        }
        candidate_provenance = selection_document.get("provenance")
        if candidate_provenance is not None:
            if not isinstance(candidate_provenance, dict):
                raise BenchmarkError("Selection report provenance должен быть объектом.")
            run_provenance = candidate_provenance

    output_paths = (
        [outputs_path] if isinstance(outputs_path, Path) else list(outputs_path)
    )
    if not output_paths:
        raise BenchmarkError("Нужен хотя бы один outputs JSONL.")
    output_batches = [(path, read_jsonl(path)) for path in output_paths]
    selected_by_id = {item["id"]: item for item in dataset}
    for path, batch in output_batches:
        if not batch:
            continue
        unknown_ids = [
            item.get("id") for item in batch if item.get("id") not in selected_by_id
        ]
        if unknown_ids:
            raise BenchmarkError(
                f"Outputs '{path}' содержит неизвестные id: "
                + ", ".join(str(item) for item in unknown_ids)
            )
        tagged = [item.get("selectionSha256") for item in batch]
        if any(value is not None for value in tagged):
            if any(value is None for value in tagged):
                raise BenchmarkError(
                    f"Outputs '{path}' лишь частично содержит selectionSha256."
                )
            expected_batch_selection = selection_sha256(
                [selected_by_id[item["id"]] for item in batch],
                dataset_artifact_sha256,
            )
            if any(
                not isinstance(value, str)
                or not _HEX_64.fullmatch(value)
                or value.lower() != expected_batch_selection
                for value in tagged
            ):
                raise BenchmarkError(
                    f"Outputs '{path}' имеет selectionSha256 от другой выборки."
                )
    raw_outputs = [record for _, batch in output_batches for record in batch]
    if selection_report_path is not None and isinstance(
        selection_document.get("cases"), list
    ):
        if canonical_json(selection_document["cases"]) != canonical_json(raw_outputs):
            raise BenchmarkError(
                "Selection report cases не совпадают с переданными outputs."
            )
        runner_dataset = selection_document.get("dataset")
        if isinstance(runner_dataset, dict):
            if runner_dataset.get("bytes") != dataset_path.stat().st_size or str(
                runner_dataset.get("sha256", "")
            ).lower() != dataset_artifact_sha256:
                raise BenchmarkError(
                    "Selection report закрепляет другой dataset artifact."
                )
    outputs = validate_output_records(raw_outputs, dataset, system_id)
    output_selection_hashes = sorted(
        {
            item["selectionSha256"].lower()
            for item in outputs
            if item.get("selectionSha256") is not None
        }
    )
    if selection is not None and output_selection_hashes != [selection["selectionSha256"]]:
        raise BenchmarkError(
            "selectionSha256 в outputs не совпадает с selection report."
        )
    dataset_fingerprint = canonical_records_sha256(
        "asteria.translation-dataset-v1", complete_dataset
    )
    evaluation_slice_fingerprint = canonical_records_sha256(
        "asteria.translation-dataset-v1", dataset
    )
    output_core = _output_fingerprint_records(outputs)
    outputs_fingerprint = canonical_records_sha256(
        OUTPUT_FINGERPRINT_DOMAIN, output_core
    )
    cases, summary = score_cases(dataset, outputs)
    system_profile = _consistent_output_value(outputs, "profile")
    system_backend = _consistent_output_alias(outputs, "phraseBackend", "backend")
    system_ranker = _consistent_output_alias(outputs, "rankerCpuModel", "ranker")
    system_engine = _consistent_output_value(outputs, "engine")
    system_model_identity = _consistent_output_value(outputs, "modelIdentity")
    system_prompt_identity = _consistent_output_value(outputs, "promptIdentity")
    if selection_report_path is not None:
        expected_run_fields = {
            "systemId": system_id,
            "profile": system_profile,
            "phraseBackend": system_backend,
            "rankerCpuModel": system_ranker,
        }
        for field, expected in expected_run_fields.items():
            actual = selection_document.get(field)
            if actual is not None and actual != expected:
                raise BenchmarkError(
                    f"Selection report {field} не совпадает с outputs."
                )
    if run_provenance is not None:
        if run_provenance.get("translationModelIdentity") != system_model_identity:
            raise BenchmarkError(
                "Selection report translationModelIdentity не совпадает с outputs."
            )
        if run_provenance.get("translationPromptIdentity") != system_prompt_identity:
            raise BenchmarkError(
                "Selection report translationPromptIdentity не совпадает с outputs."
            )
    effective_dataset_id = dataset_id or dataset_path.stem
    effective_run_id = run_id or (
        f"{system_id}-{dataset_fingerprint[:12]}-{outputs_fingerprint[:12]}"
    )
    return {
        "schemaVersion": SCHEMA_VERSION,
        "kind": REPORT_KIND,
        "metricContract": SCORING_CONTRACT,
        "runId": effective_run_id,
        "generatedUtc": utc_now(),
        "track": "text",
        "dataset": {
            "id": effective_dataset_id,
            "version": dataset_version,
            "languagePair": _infer_language_pair(dataset),
            "cases": len(complete_dataset),
            "selectedCases": len(dataset),
            "referenceBearingCases": sum(bool(item["references"]) for item in dataset),
            "referenceFree": all(item["referenceFree"] for item in dataset),
            "canonicalSha256": dataset_fingerprint,
            "evaluationSliceSha256": evaluation_slice_fingerprint,
            "artifact": _artifact_record(dataset_path),
        },
        "outputs": {
            "canonicalSha256": outputs_fingerprint,
            "selectionSha256": output_selection_hashes,
            "artifacts": [_artifact_record(path) for path in output_paths],
        },
        "system": {
            "id": system_id,
            "name": system_name or system_id,
            "profile": system_profile,
            "backend": system_backend,
            "ranker": system_ranker,
            "engine": system_engine,
            "modelIdentity": system_model_identity,
            "promptIdentity": system_prompt_identity,
        },
        "runProvenance": run_provenance,
        "summary": summary,
        "selection": selection,
        "cases": cases,
    }


def _infer_language_pair(dataset: Sequence[dict[str, Any]]) -> str | None:
    pairs = {
        (item.get("sourceLanguage"), item.get("targetLanguage")) for item in dataset
    }
    if len(pairs) != 1:
        return None
    source_language, target_language = next(iter(pairs))
    if not source_language or not target_language:
        return None
    return f"{source_language}-{target_language}"


def _artifact_record(path: Path) -> dict[str, Any]:
    return {
        "name": path.name,
        "bytes": path.stat().st_size,
        "sha256": sha256_file(path),
    }


def metric_input_records(report: dict[str, Any]) -> list[dict[str, Any]]:
    return [
        {
            "caseId": item["input"]["id"],
            "src": item["input"]["source"],
            "mt": item["output"]["hypothesis"]
            if item["output"]["status"] == "ok"
            else "",
            "ref": item["input"]["references"][0]
            if item["input"]["references"]
            else None,
            "refs": item["input"]["references"],
        }
        for item in report["cases"]
    ]


def _metric_segments_bytes(segments: Sequence[dict[str, Any]]) -> bytes:
    return "".join(canonical_json(item) + "\n" for item in segments).encode("utf-8")


def validate_report(report: dict[str, Any], label: str = "report") -> None:
    if report.get("schemaVersion") != SCHEMA_VERSION or report.get("kind") != REPORT_KIND:
        raise BenchmarkError(f"{label}: неподдерживаемый формат отчёта.")
    if report.get("metricContract") != SCORING_CONTRACT:
        raise BenchmarkError(f"{label}: неподдерживаемый контракт метрик.")
    cases = report.get("cases")
    if not isinstance(cases, list) or not cases:
        raise BenchmarkError(f"{label}: отсутствуют cases.")
    try:
        dataset = [item["input"] for item in cases]
        outputs = [item["output"] for item in cases]
    except (KeyError, TypeError) as error:
        raise BenchmarkError(f"{label}: повреждена структура cases.") from error
    expected_dataset = canonical_records_sha256(
        "asteria.translation-dataset-v1", dataset
    )
    expected_outputs = canonical_records_sha256(
        OUTPUT_FINGERPRINT_DOMAIN, _output_fingerprint_records(outputs)
    )
    if report.get("dataset", {}).get("evaluationSliceSha256") != expected_dataset:
        raise BenchmarkError(f"{label}: evaluation slice fingerprint не совпадает с cases.")
    if report.get("outputs", {}).get("canonicalSha256") != expected_outputs:
        raise BenchmarkError(f"{label}: outputs fingerprint не совпадает с cases.")
    selected_cases = report.get("dataset", {}).get("selectedCases")
    complete_cases = report.get("dataset", {}).get("cases")
    if selected_cases != len(cases) or not isinstance(complete_cases, int):
        raise BenchmarkError(f"{label}: неверное число выбранных cases.")
    if complete_cases < selected_cases:
        raise BenchmarkError(f"{label}: dataset.cases меньше selectedCases.")
    if (
        complete_cases == selected_cases
        and report.get("dataset", {}).get("canonicalSha256") != expected_dataset
    ):
        raise BenchmarkError(f"{label}: fingerprint полного dataset не совпадает с cases.")

    reported_selection_hashes = report.get("outputs", {}).get("selectionSha256")
    if (
        not isinstance(reported_selection_hashes, list)
        or any(
            not isinstance(value, str) or not _HEX_64.fullmatch(value)
            for value in reported_selection_hashes
        )
        or reported_selection_hashes != sorted(set(reported_selection_hashes))
    ):
        raise BenchmarkError(f"{label}: неверный outputs.selectionSha256.")
    actual_selection_hashes = sorted(
        {
            item["selectionSha256"].lower()
            for item in outputs
            if item.get("selectionSha256") is not None
        }
    )
    if reported_selection_hashes != actual_selection_hashes:
        raise BenchmarkError(f"{label}: selection fingerprints не совпадают с cases.")

    selection = report.get("selection")
    if selection is not None:
        if not isinstance(selection, dict):
            raise BenchmarkError(f"{label}: selection должен быть объектом или null.")
        selected_ids = selection.get("selectedCaseIds")
        case_ids = [item["input"]["id"] for item in cases]
        if selected_ids != case_ids:
            raise BenchmarkError(f"{label}: selection.selectedCaseIds не совпадает с cases.")
        if reported_selection_hashes != [selection.get("selectionSha256")]:
            raise BenchmarkError(f"{label}: selectionSha256 не совпадает с outputs.")

    expected_cases, expected_summary = score_cases(dataset, outputs)
    reported_metrics = [item.get("metrics") for item in cases]
    expected_metrics = [item["metrics"] for item in expected_cases]
    if canonical_json(reported_metrics) != canonical_json(expected_metrics):
        raise BenchmarkError(f"{label}: case metrics не совпадают с пересчётом.")
    if canonical_json(report.get("summary")) != canonical_json(expected_summary):
        raise BenchmarkError(f"{label}: summary не совпадает с пересчётом.")

    system = report.get("system")
    if not isinstance(system, dict):
        raise BenchmarkError(f"{label}: system должен быть объектом.")
    expected_system = {
        "id": _consistent_output_value(outputs, "systemId") or system.get("id"),
        "profile": _consistent_output_value(outputs, "profile"),
        "backend": _consistent_output_alias(outputs, "phraseBackend", "backend"),
        "ranker": _consistent_output_alias(outputs, "rankerCpuModel", "ranker"),
        "engine": _consistent_output_value(outputs, "engine"),
        "modelIdentity": _consistent_output_value(outputs, "modelIdentity"),
        "promptIdentity": _consistent_output_value(outputs, "promptIdentity"),
    }
    for field, expected in expected_system.items():
        if system.get(field) != expected:
            raise BenchmarkError(
                f"{label}: system.{field} не совпадает с case outputs."
            )
    provenance = report.get("runProvenance")
    if provenance is not None:
        if not isinstance(provenance, dict):
            raise BenchmarkError(f"{label}: runProvenance должен быть объектом или null.")
        if provenance.get("translationModelIdentity") != system.get("modelIdentity"):
            raise BenchmarkError(
                f"{label}: provenance translationModelIdentity не совпадает с system."
            )
        if provenance.get("translationPromptIdentity") != system.get("promptIdentity"):
            raise BenchmarkError(
                f"{label}: provenance translationPromptIdentity не совпадает с system."
            )


def export_metric_inputs(report_path: Path, output_directory: Path) -> tuple[Path, Path]:
    report = read_json(report_path)
    validate_report(report)
    segments = metric_input_records(report)
    input_fingerprint = canonical_records_sha256(
        "asteria.translation-metric-input-v1", segments
    )
    segment_path = output_directory / "segments.jsonl"
    manifest_path = output_directory / "manifest.json"
    segment_bytes = _metric_segments_bytes(segments)
    manifest = {
        "schemaVersion": SCHEMA_VERSION,
        "kind": METRIC_INPUT_KIND,
        "generatedUtc": utc_now(),
        "runId": report["runId"],
        "datasetSha256": report["dataset"]["canonicalSha256"],
        "evaluationSliceSha256": report["dataset"]["evaluationSliceSha256"],
        "outputsSha256": report["outputs"]["canonicalSha256"],
        "metricInputSha256": input_fingerprint,
        "cases": len(segments),
        "referenceBearingCases": sum(item["ref"] is not None for item in segments),
        "segmentsArtifact": {
            "name": segment_path.name,
            "bytes": len(segment_bytes),
            "sha256": sha256_bytes(segment_bytes),
        },
    }
    atomic_write_bytes(segment_path, segment_bytes)
    atomic_write_json(manifest_path, manifest)
    return manifest_path, segment_path


def _read_score_records(path: Path) -> list[dict[str, Any]]:
    if path.suffix.lower() == ".csv":
        try:
            with path.open("r", encoding="utf-8-sig", newline="") as stream:
                reader = csv.DictReader(stream)
                if reader.fieldnames is None:
                    raise BenchmarkError(f"CSV '{path}' не содержит заголовок.")
                return [dict(item) for item in reader]
        except OSError as error:
            raise BenchmarkError(f"Не удалось прочитать '{path}': {error}") from error

    text = path.read_text(encoding="utf-8-sig")
    stripped = text.lstrip()
    if stripped.startswith("[") or stripped.startswith("{"):
        try:
            parsed = json.loads(text)
        except json.JSONDecodeError:
            return read_jsonl(path)
        if isinstance(parsed, list):
            if not all(isinstance(item, dict) for item in parsed):
                raise BenchmarkError(f"Scores JSON '{path}' должен содержать объекты.")
            return list(parsed)
        if isinstance(parsed, dict) and isinstance(parsed.get("scores"), list):
            if not all(isinstance(item, dict) for item in parsed["scores"]):
                raise BenchmarkError(f"Scores JSON '{path}' содержит неверный scores.")
            return list(parsed["scores"])
        if isinstance(parsed, dict):
            return [parsed]
    return read_jsonl(path)


def _normalize_scores(path: Path) -> dict[str, float]:
    result: dict[str, float] = {}
    for index, record in enumerate(_read_score_records(path)):
        case_id = record.get("caseId", record.get("id"))
        value = record.get("score", record.get("value"))
        case_id = _require_nonempty_string(case_id, f"scores[{index}].caseId")
        if case_id in result:
            raise BenchmarkError(f"Повторный caseId в scores: '{case_id}'.")
        try:
            numeric = float(value)
        except (TypeError, ValueError) as error:
            raise BenchmarkError(f"scores[{index}].score должен быть числом.") from error
        if not math.isfinite(numeric):
            raise BenchmarkError(f"scores[{index}].score должен быть конечным.")
        result[case_id] = numeric
    return result


def _metric_direction(metric_id: str, explicit: str) -> str:
    if explicit != "auto":
        return explicit
    folded = metric_id.lower()
    if folded.startswith("metricx") or folded.startswith("metric-x"):
        return "lower"
    if "comet" in folded:
        return "higher"
    raise BenchmarkError(
        "Для неизвестной метрики укажите --direction higher или lower."
    )


def import_external_metric(
    report_path: Path,
    manifest_path: Path,
    scores_path: Path,
    metric_id: str,
    model_revision: str,
    *,
    direction: str = "auto",
    reference_mode: str = "auto",
    allow_partial: bool = False,
) -> dict[str, Any]:
    report = read_json(report_path)
    validate_report(report)
    manifest = read_json(manifest_path)
    if manifest.get("schemaVersion") != SCHEMA_VERSION or manifest.get("kind") != METRIC_INPUT_KIND:
        raise BenchmarkError("Manifest внешней метрики имеет неподдерживаемый формат.")
    segments = metric_input_records(report)
    input_fingerprint = canonical_records_sha256(
        "asteria.translation-metric-input-v1", segments
    )
    expected = {
        "runId": report["runId"],
        "datasetSha256": report["dataset"]["canonicalSha256"],
        "evaluationSliceSha256": report["dataset"]["evaluationSliceSha256"],
        "outputsSha256": report["outputs"]["canonicalSha256"],
        "metricInputSha256": input_fingerprint,
    }
    for key, value in expected.items():
        if manifest.get(key) != value:
            raise BenchmarkError(f"Manifest внешней метрики: не совпадает {key}.")

    expected_segment_bytes = _metric_segments_bytes(segments)
    artifact = manifest.get("segmentsArtifact")
    if not isinstance(artifact, dict):
        raise BenchmarkError("Manifest внешней метрики не содержит segmentsArtifact.")
    artifact_name = artifact.get("name")
    if (
        not isinstance(artifact_name, str)
        or not artifact_name
        or Path(artifact_name).is_absolute()
        or Path(artifact_name).name != artifact_name
    ):
        raise BenchmarkError("Manifest внешней метрики содержит небезопасное имя segmentsArtifact.")
    expected_artifact = {
        "bytes": len(expected_segment_bytes),
        "sha256": sha256_bytes(expected_segment_bytes),
    }
    if artifact.get("bytes") != expected_artifact["bytes"] or artifact.get(
        "sha256"
    ) != expected_artifact["sha256"]:
        raise BenchmarkError(
            "Manifest внешней метрики не соответствует ожидаемому segments artifact."
        )
    segment_path = manifest_path.parent / artifact_name
    try:
        actual_segment_bytes = segment_path.read_bytes()
    except OSError as error:
        raise BenchmarkError(
            f"Не удалось прочитать обязательный segments artifact '{segment_path}': {error}"
        ) from error
    if (
        len(actual_segment_bytes) != artifact["bytes"]
        or sha256_bytes(actual_segment_bytes) != artifact["sha256"]
        or actual_segment_bytes != expected_segment_bytes
    ):
        raise BenchmarkError(
            "Экспортированный segments artifact изменён или не соответствует report."
        )

    metric_id = _require_nonempty_string(metric_id, "metric-id")
    model_revision = _require_nonempty_string(model_revision, "model-revision")
    resolved_direction = _metric_direction(metric_id, direction)
    has_missing_references = any(not item["refs"] for item in segments)
    if reference_mode == "auto":
        reference_mode = "reference-free" if has_missing_references else "reference-based"
    if reference_mode == "reference-based" and has_missing_references:
        raise BenchmarkError(
            "Reference-based метрика не может оценить случаи без references."
        )

    scores = _normalize_scores(scores_path)
    expected_ids = [item["caseId"] for item in segments]
    expected_id_set = set(expected_ids)
    extra = sorted(set(scores) - expected_id_set)
    missing = [case_id for case_id in expected_ids if case_id not in scores]
    if extra:
        raise BenchmarkError(f"Scores содержит неизвестные caseId: {', '.join(extra)}.")
    if missing and not allow_partial:
        raise BenchmarkError(
            f"Scores не содержит {len(missing)} случаев; используйте --allow-partial явно."
        )
    ordered_scores = [
        {"caseId": case_id, "value": scores[case_id]}
        for case_id in expected_ids
        if case_id in scores
    ]
    values = [item["value"] for item in ordered_scores]
    return {
        "schemaVersion": SCHEMA_VERSION,
        "kind": METRIC_EVALUATION_KIND,
        "generatedUtc": utc_now(),
        "runId": report["runId"],
        "datasetSha256": expected["datasetSha256"],
        "evaluationSliceSha256": expected["evaluationSliceSha256"],
        "outputsSha256": expected["outputsSha256"],
        "metricInputSha256": input_fingerprint,
        "segmentsArtifact": dict(artifact),
        "metric": {
            "id": metric_id,
            "modelRevision": model_revision,
            "direction": resolved_direction,
            "higherIsBetter": resolved_direction == "higher",
            "referenceMode": reference_mode,
        },
        "coverage": {
            "scored": len(ordered_scores),
            "total": len(expected_ids),
            "complete": not missing,
        },
        "summary": {
            "mean": _mean(values),
            "median": _median(values),
            "minimum": min(values) if values else None,
            "maximum": max(values) if values else None,
        },
        "scoresArtifact": _artifact_record(scores_path),
        "scores": ordered_scores,
    }


def _metric_key(evaluation: dict[str, Any]) -> str:
    metric = evaluation["metric"]
    return f"{metric['id']}@{metric['modelRevision']}"


def validate_evaluation(evaluation: dict[str, Any], reports: Sequence[dict[str, Any]]) -> None:
    if (
        evaluation.get("schemaVersion") != SCHEMA_VERSION
        or evaluation.get("kind") != METRIC_EVALUATION_KIND
    ):
        raise BenchmarkError("Неподдерживаемый external metric sidecar.")
    metric = evaluation.get("metric")
    if not isinstance(metric, dict):
        raise BenchmarkError("External metric sidecar не содержит metric.")
    _require_nonempty_string(metric.get("id"), "external metric id")
    _require_nonempty_string(
        metric.get("modelRevision"), "external metric modelRevision"
    )
    direction = metric.get("direction")
    if direction not in {"higher", "lower"}:
        raise BenchmarkError("External metric sidecar имеет неверное direction.")
    if metric.get("higherIsBetter") is not (direction == "higher"):
        raise BenchmarkError("External metric sidecar имеет неверное higherIsBetter.")
    if metric.get("referenceMode") not in {"reference-based", "reference-free"}:
        raise BenchmarkError("External metric sidecar имеет неверное referenceMode.")
    candidates = [
        report
        for report in reports
        if report["runId"] == evaluation.get("runId")
        and report["dataset"]["canonicalSha256"] == evaluation.get("datasetSha256")
        and report["dataset"]["evaluationSliceSha256"]
        == evaluation.get("evaluationSliceSha256")
        and report["outputs"]["canonicalSha256"] == evaluation.get("outputsSha256")
    ]
    if len(candidates) != 1:
        raise BenchmarkError(
            "External metric sidecar должен однозначно соответствовать одному report."
        )
    matching_report = candidates[0]
    expected_ids = [item["input"]["id"] for item in matching_report["cases"]]
    expected_metric_input = canonical_records_sha256(
        "asteria.translation-metric-input-v1", metric_input_records(matching_report)
    )
    if evaluation.get("metricInputSha256") != expected_metric_input:
        raise BenchmarkError(
            "External metric sidecar имеет неверный metricInputSha256."
        )
    score_rows = evaluation.get("scores")
    if not isinstance(score_rows, list) or any(
        not isinstance(item, dict) for item in score_rows
    ):
        raise BenchmarkError("External metric sidecar имеет неверный scores.")
    score_ids = [item.get("caseId") for item in score_rows]
    if len(score_ids) != len(set(score_ids)) or any(item not in expected_ids for item in score_ids):
        raise BenchmarkError("External metric sidecar содержит неверные caseId.")
    values = [item.get("value") for item in score_rows]
    if any(
        not isinstance(value, (int, float))
        or isinstance(value, bool)
        or not math.isfinite(float(value))
        for value in values
    ):
        raise BenchmarkError("External metric sidecar содержит неверные scores.")
    expected_mean = _mean([float(value) for value in values])
    expected_summary = {
        "mean": expected_mean,
        "median": _median([float(value) for value in values]),
        "minimum": min(values) if values else None,
        "maximum": max(values) if values else None,
    }
    actual_summary = evaluation.get("summary", {})
    for key, expected_value in expected_summary.items():
        actual_value = actual_summary.get(key)
        if expected_value is None:
            if actual_value is not None:
                raise BenchmarkError(
                    f"External metric sidecar содержит неверный aggregate {key}."
                )
            continue
        if (
            not isinstance(actual_value, (int, float))
            or isinstance(actual_value, bool)
            or not math.isfinite(float(actual_value))
            or abs(float(expected_value) - float(actual_value)) > 1e-12
        ):
            raise BenchmarkError(
                f"External metric sidecar содержит неверный aggregate {key}."
            )
    coverage = evaluation.get("coverage", {})
    missing = len(expected_ids) - len(score_ids)
    if (
        coverage.get("scored") != len(score_ids)
        or coverage.get("total") != len(expected_ids)
        or coverage.get("complete") is not (missing == 0)
    ):
        raise BenchmarkError("External metric sidecar содержит неверное coverage.")


def _native_metric(report: dict[str, Any], metric_id: str) -> tuple[float | None, str]:
    summary = report["summary"]
    mapping: dict[str, tuple[float | None, str]] = {
        "chrf2": (summary["chrF2"]["mean"], "higher"),
        "success-rate": (summary["successRate"], "higher"),
        "anchor-recall": (summary["termAnchors"]["recall"], "higher"),
        "contrast-accuracy": (summary["contrastive"]["accuracy"], "higher"),
        "residual-latin": (summary["residualLatin"]["meanSuccessful"], "lower"),
    }
    if metric_id not in mapping:
        raise BenchmarkError(f"Неизвестная встроенная метрика '{metric_id}'.")
    return mapping[metric_id]


def _choose_primary_metric(
    reports: Sequence[dict[str, Any]],
    evaluations_by_output: dict[tuple[str, str, str, str], list[dict[str, Any]]],
    requested: str,
) -> tuple[str, str]:
    if requested != "auto":
        if requested in {
            "chrf2",
            "success-rate",
            "anchor-recall",
            "contrast-accuracy",
            "residual-latin",
        }:
            return requested, _native_metric(reports[0], requested)[1]
        matching = []
        for report in reports:
            key = (
                report["runId"],
                report["dataset"]["canonicalSha256"],
                report["dataset"]["evaluationSliceSha256"],
                report["outputs"]["canonicalSha256"],
            )
            found = [item for item in evaluations_by_output.get(key, []) if _metric_key(item) == requested]
            if len(found) != 1:
                raise BenchmarkError(
                    f"Метрика '{requested}' отсутствует у части систем или неоднозначна."
                )
            evaluation = found[0]
            if evaluation.get("coverage", {}).get("complete") is not True:
                raise BenchmarkError(
                    f"Метрика '{requested}' имеет неполное покрытие и не может быть primary."
                )
            matching.append(evaluation)
        coverage_identities = {
            canonical_records_sha256(
                "asteria.translation-metric-coverage-v1",
                sorted(item["caseId"] for item in evaluation["scores"]),
            )
            for evaluation in matching
        }
        if len(coverage_identities) != 1:
            raise BenchmarkError(
                f"Метрика '{requested}' оценивает разные наборы случаев и не может быть primary."
            )
        directions = {evaluation["metric"]["direction"] for evaluation in matching}
        if len(directions) != 1:
            raise BenchmarkError(
                f"Метрика '{requested}' имеет несовместимые направления оптимизации."
            )
        return requested, next(iter(directions))

    common: set[str] | None = None
    direction_by_key: dict[str, str] = {}
    for report in reports:
        output_key = (
            report["runId"],
            report["dataset"]["canonicalSha256"],
            report["dataset"]["evaluationSliceSha256"],
            report["outputs"]["canonicalSha256"],
        )
        complete = {
            _metric_key(item)
            for item in evaluations_by_output.get(output_key, [])
            if item.get("coverage", {}).get("complete") is True
        }
        for item in evaluations_by_output.get(output_key, []):
            direction_by_key[_metric_key(item)] = item["metric"]["direction"]
        common = complete if common is None else common & complete
    if common:
        ordered = sorted(
            common,
            key=lambda value: (
                0 if value.lower().startswith("metricx") else 1 if "comet" in value.lower() else 2,
                value,
            ),
        )
        selected = ordered[0]
        return selected, direction_by_key[selected]
    if all(report["summary"]["chrF2"]["mean"] is not None for report in reports):
        return "chrf2", "higher"
    return "success-rate", "higher"


def build_leaderboard(
    reports: Sequence[dict[str, Any]],
    evaluations: Sequence[dict[str, Any]],
    *,
    title: str,
    primary_metric: str = "auto",
) -> dict[str, Any]:
    for index, report in enumerate(reports):
        validate_report(report, f"report[{index}]")
    if not reports:
        raise BenchmarkError("Для leaderboard нужен хотя бы один report.")
    for evaluation in evaluations:
        validate_evaluation(evaluation, reports)

    evaluations_by_output: dict[
        tuple[str, str, str, str], list[dict[str, Any]]
    ] = {}
    for evaluation in evaluations:
        key = (
            evaluation["runId"],
            evaluation["datasetSha256"],
            evaluation["evaluationSliceSha256"],
            evaluation["outputsSha256"],
        )
        values = evaluations_by_output.setdefault(key, [])
        metric_key = _metric_key(evaluation)
        if any(_metric_key(item) == metric_key for item in values):
            raise BenchmarkError(f"Повторный external metric sidecar '{metric_key}'.")
        values.append(evaluation)

    grouped: dict[tuple[str, str, str, str], list[dict[str, Any]]] = {}
    for report in reports:
        key = (
            report["dataset"]["id"],
            report["dataset"]["canonicalSha256"],
            report["dataset"]["evaluationSliceSha256"],
            report["track"],
        )
        grouped.setdefault(key, []).append(report)

    groups: list[dict[str, Any]] = []
    for (
        dataset_id,
        dataset_sha,
        evaluation_slice_sha,
        track,
    ), group_reports in sorted(grouped.items()):
        selection_identity = canonical_records_sha256(
            "asteria.translation-selection-v1",
            [item["input"]["id"] for item in group_reports[0]["cases"]],
        )
        system_ids = [report["system"]["id"] for report in group_reports]
        if len(system_ids) != len(set(system_ids)):
            raise BenchmarkError(
                f"Dataset '{dataset_id}' содержит повторный system id."
            )
        selected_metric, direction = _choose_primary_metric(
            group_reports, evaluations_by_output, primary_metric
        )
        rows: list[dict[str, Any]] = []
        for report in group_reports:
            output_key = (
                report["runId"],
                dataset_sha,
                evaluation_slice_sha,
                report["outputs"]["canonicalSha256"],
            )
            external = {
                _metric_key(item): item["summary"]["mean"]
                for item in evaluations_by_output.get(output_key, [])
            }
            if selected_metric in external:
                primary_value = external[selected_metric]
            else:
                primary_value, _ = _native_metric(report, selected_metric)
            summary = report["summary"]
            rows.append(
                {
                    "rank": None,
                    "systemId": report["system"]["id"],
                    "systemName": report["system"]["name"],
                    "profile": report["system"].get("profile"),
                    "backend": report["system"].get("backend"),
                    "primaryValue": primary_value,
                    "cases": summary["cases"],
                    "successful": summary["successful"],
                    "successRate": summary["successRate"],
                    "chrF2Mean": summary["chrF2"]["mean"],
                    "chrF2Median": summary["chrF2"]["median"],
                    "anchorRecall": summary["termAnchors"]["recall"],
                    "contrastAccuracy": summary["contrastive"]["accuracy"],
                    "residualLatinMean": summary["residualLatin"]["meanSuccessful"],
                    "latencyP50Milliseconds": summary["latencyMilliseconds"]["successfulP50"],
                    "latencyP95Milliseconds": summary["latencyMilliseconds"]["successfulP95"],
                    "statuses": summary["statuses"],
                    "externalMetrics": external,
                    "report": report,
                }
            )
        comparable = [row for row in rows if row["primaryValue"] is not None]
        comparable.sort(
            key=lambda row: (
                -row["primaryValue"] if direction == "higher" else row["primaryValue"],
                row["systemId"],
            )
        )
        previous_value: float | None = None
        previous_rank = 0
        for position, row in enumerate(comparable, 1):
            value = float(row["primaryValue"])
            if previous_value is None or value != previous_value:
                previous_rank = position
                previous_value = value
            row["rank"] = previous_rank
        rows.sort(key=lambda row: (row["rank"] is None, row["rank"] or 0, row["systemId"]))
        groups.append(
            {
                "datasetId": dataset_id,
                "datasetSha256": dataset_sha,
                "evaluationSliceSha256": evaluation_slice_sha,
                "selectionIdentitySha256": selection_identity,
                "selectedCases": group_reports[0]["dataset"].get(
                    "selectedCases", len(group_reports[0]["cases"])
                ),
                "track": track,
                "primaryMetric": selected_metric,
                "primaryDirection": direction,
                "rows": rows,
            }
        )
    return {
        "schemaVersion": SCHEMA_VERSION,
        "kind": LEADERBOARD_KIND,
        "generatedUtc": utc_now(),
        "title": title,
        "groups": groups,
    }


def _public_leaderboard(leaderboard: dict[str, Any]) -> dict[str, Any]:
    public = dict(leaderboard)
    public["groups"] = []
    for group in leaderboard["groups"]:
        public_group = dict(group)
        public_group["rows"] = [
            {key: value for key, value in row.items() if key != "report"}
            for row in group["rows"]
        ]
        public["groups"].append(public_group)
    return public


def _format_number(value: object, digits: int = 6) -> str:
    if value is None:
        return ""
    if isinstance(value, bool):
        return "1" if value else "0"
    if isinstance(value, (int, float)):
        return f"{value:.{digits}f}".rstrip("0").rstrip(".")
    return str(value)


def render_leaderboard_csv(leaderboard: dict[str, Any]) -> bytes:
    external_keys = sorted(
        {
            key
            for group in leaderboard["groups"]
            for row in group["rows"]
            for key in row["externalMetrics"]
        }
    )
    group_fields = [
        "datasetId",
        "datasetSha256",
        "evaluationSliceSha256",
        "selectionIdentitySha256",
        "selectedCases",
        "track",
        "primaryMetric",
        "primaryDirection",
    ]
    fields = group_fields + [
        "rank",
        "systemId",
        "systemName",
        "profile",
        "backend",
        "primaryValue",
        "cases",
        "successful",
        "successRate",
        "chrF2Mean",
        "chrF2Median",
        "anchorRecall",
        "contrastAccuracy",
        "residualLatinMean",
        "latencyP50Milliseconds",
        "latencyP95Milliseconds",
    ] + [f"external:{key}" for key in external_keys]
    stream = io.StringIO(newline="")
    writer = csv.DictWriter(stream, fieldnames=fields, lineterminator="\n")
    writer.writeheader()
    for group in leaderboard["groups"]:
        for row in group["rows"]:
            output: dict[str, object] = {
                "datasetId": group["datasetId"],
                "datasetSha256": group["datasetSha256"],
                "evaluationSliceSha256": group["evaluationSliceSha256"],
                "selectionIdentitySha256": group["selectionIdentitySha256"],
                "selectedCases": group["selectedCases"],
                "track": group["track"],
                "primaryMetric": group["primaryMetric"],
                "primaryDirection": group["primaryDirection"],
            }
            for field in fields[len(group_fields) :]:
                if field.startswith("external:"):
                    key = field.removeprefix("external:")
                    output[field] = _format_number(row["externalMetrics"].get(key))
                else:
                    output[field] = _format_number(row.get(field))
            writer.writerow(output)
    return b"\xef\xbb\xbf" + stream.getvalue().encode("utf-8")


def _html_cell(value: object, *, number: bool = False) -> str:
    rendered = _format_number(value, 4) if number else ("" if value is None else str(value))
    return html.escape(rendered, quote=True)


def render_leaderboard_html(leaderboard: dict[str, Any]) -> str:
    sections: list[str] = []
    for group in leaderboard["groups"]:
        rows: list[str] = []
        details: list[str] = []
        for row in group["rows"]:
            rows.append(
                "<tr>"
                f"<td>{_html_cell(row['rank'])}</td>"
                f"<td>{_html_cell(row['systemName'])}</td>"
                f"<td>{_html_cell(row['primaryValue'], number=True)}</td>"
                f"<td>{_html_cell(row['successRate'], number=True)}</td>"
                f"<td>{_html_cell(row['chrF2Mean'], number=True)}</td>"
                f"<td>{_html_cell(row['anchorRecall'], number=True)}</td>"
                f"<td>{_html_cell(row['contrastAccuracy'], number=True)}</td>"
                f"<td>{_html_cell(row['residualLatinMean'], number=True)}</td>"
                f"<td>{_html_cell(row['latencyP50Milliseconds'], number=True)}</td>"
                f"<td>{_html_cell(row['latencyP95Milliseconds'], number=True)}</td>"
                "</tr>"
            )
            case_rows = []
            for case in row["report"]["cases"]:
                case_rows.append(
                    "<tr>"
                    f"<td>{_html_cell(case['input']['id'])}</td>"
                    f"<td>{_html_cell(case['output']['status'])}</td>"
                    f"<td>{_html_cell(case['metrics']['chrF2'], number=True)}</td>"
                    f"<td>{_html_cell(case['input']['source'])}</td>"
                    f"<td>{_html_cell(case['output']['hypothesis'])}</td>"
                    "</tr>"
                )
            details.append(
                "<details><summary>"
                + _html_cell(row["systemName"])
                + " — случаи</summary><div class=scroll><table><thead><tr>"
                "<th>ID</th><th>Статус</th><th>chrF2</th><th>Исходник</th><th>Перевод</th>"
                "</tr></thead><tbody>"
                + "".join(case_rows)
                + "</tbody></table></div></details>"
            )
        sections.append(
            "<section>"
            f"<h2>{_html_cell(group['datasetId'])} · {_html_cell(group['track'])}</h2>"
            f"<p>Основная метрика: <code>{_html_cell(group['primaryMetric'])}</code> "
            f"({_html_cell(group['primaryDirection'])}); SHA-256: "
            f"<code>{_html_cell(group['datasetSha256'])}</code>; "
            f"выборка: {_html_cell(group['selectedCases'])} случаев, "
            f"<code>{_html_cell(group['evaluationSliceSha256'])}</code></p>"
            "<div class=scroll><table><thead><tr>"
            "<th>Место</th><th>Система</th><th>Основная</th><th>Успех</th>"
            "<th>chrF2</th><th>Якоря</th><th>Контрасты</th><th>Латиница</th>"
            "<th>p50, мс</th><th>p95, мс</th>"
            "</tr></thead><tbody>"
            + "".join(rows)
            + "</tbody></table></div>"
            + "".join(details)
            + "</section>"
        )
    return """<!doctype html>
<html lang="ru"><head><meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{title}</title><style>
:root {{ color-scheme: light dark; font-family: system-ui, sans-serif; }}
body {{ margin: 2rem auto; max-width: 1500px; padding: 0 1rem; }}
h1, h2 {{ line-height: 1.2; }} section {{ margin: 2rem 0 3rem; }}
.scroll {{ overflow-x: auto; }} table {{ border-collapse: collapse; width: 100%; }}
th, td {{ border-bottom: 1px solid #8886; padding: .55rem; text-align: left; vertical-align: top; }}
th {{ position: sticky; top: 0; background: Canvas; }} code {{ overflow-wrap: anywhere; }}
details {{ margin-top: .8rem; }} summary {{ cursor: pointer; font-weight: 600; }}
details table td:nth-child(4), details table td:nth-child(5) {{ min-width: 20rem; white-space: pre-wrap; }}
</style></head><body><h1>{title}</h1>
<p>Автономный отчёт Asteria: перевод, WSD и OCR ранжируются раздельно.</p>
{sections}</body></html>
""".format(
        title=html.escape(str(leaderboard["title"]), quote=True),
        sections="".join(sections),
    )


def atomic_write_bytes(path: Path, content: bytes) -> None:
    path = path.resolve()
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary_name = tempfile.mkstemp(
        prefix=f".{path.name}.", suffix=".tmp", dir=path.parent
    )
    temporary = Path(temporary_name)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        if temporary.exists():
            temporary.unlink()


def atomic_write_text(path: Path, content: str, *, bom: bool = False) -> None:
    encoded = content.encode("utf-8")
    if bom:
        encoded = b"\xef\xbb\xbf" + encoded
    atomic_write_bytes(path, encoded)


def atomic_write_json(path: Path, value: object) -> None:
    atomic_write_text(
        path,
        json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False) + "\n",
    )


def write_leaderboard_bundle(leaderboard: dict[str, Any], output_directory: Path) -> None:
    public = _public_leaderboard(leaderboard)
    json_content = json.dumps(public, ensure_ascii=False, indent=2, allow_nan=False) + "\n"
    csv_content = render_leaderboard_csv(leaderboard)
    html_content = render_leaderboard_html(leaderboard)
    atomic_write_text(output_directory / "leaderboard.json", json_content)
    atomic_write_bytes(output_directory / "leaderboard.csv", csv_content)
    atomic_write_text(output_directory / "leaderboard.html", html_content)


def _mcnemar_exact(left: Sequence[bool], right: Sequence[bool]) -> dict[str, Any]:
    left_only = sum(a and not b for a, b in zip(left, right, strict=True))
    right_only = sum(b and not a for a, b in zip(left, right, strict=True))
    discordant = left_only + right_only
    if discordant == 0:
        p_value = 1.0
    else:
        tail = sum(
            math.comb(discordant, index)
            for index in range(min(left_only, right_only) + 1)
        ) / (2 ** discordant)
        p_value = min(1.0, 2 * tail)
    return {
        "leftOnlyCorrect": left_only,
        "rightOnlyCorrect": right_only,
        "discordant": discordant,
        "exactTwoSidedP": p_value,
    }


def _cluster_key(case: dict[str, Any]) -> str:
    focus = case.get("focus")
    if isinstance(focus, dict):
        focus_text = focus.get("text")
        if isinstance(focus_text, str) and focus_text.strip():
            return focus_text.strip().casefold()
    return case["id"]


def _cluster_bootstrap_delta(
    deltas: Sequence[float],
    cluster_keys: Sequence[str],
    *,
    samples: int,
    seed: int,
) -> dict[str, Any]:
    grouped: dict[str, list[float]] = {}
    for value, key in zip(deltas, cluster_keys, strict=True):
        grouped.setdefault(key, []).append(value)
    keys = sorted(grouped)
    observed = _mean(list(deltas))
    if not keys or samples <= 0:
        return {
            "deltaRightMinusLeft": observed,
            "confidence95": None,
            "samples": samples,
            "clusters": len(keys),
        }
    generator = random.Random(seed)
    draws: list[float] = []
    for _ in range(samples):
        sampled = [grouped[generator.choice(keys)] for _ in keys]
        flattened = [value for cluster in sampled for value in cluster]
        draws.append(statistics.fmean(flattened))
    draws.sort()
    lower = draws[max(0, math.floor(0.025 * (len(draws) - 1)))]
    upper = draws[min(len(draws) - 1, math.ceil(0.975 * (len(draws) - 1)))]
    return {
        "deltaRightMinusLeft": observed,
        "confidence95": [lower, upper],
        "samples": samples,
        "clusters": len(keys),
    }


def _metadata_fingerprint(output: dict[str, Any]) -> str | None:
    metadata = output.get("metadata")
    if not isinstance(metadata, dict):
        return None
    value = metadata.get("appliedSenseEvidenceSha256")
    return value if isinstance(value, str) and _HEX_64.fullmatch(value) else None


def _focus_evidence_signature(
    case: dict[str, Any], output: dict[str, Any]
) -> str | None:
    if _metadata_fingerprint(output) is None:
        return None
    focus = case.get("focus")
    metadata = output.get("metadata")
    if not isinstance(focus, dict) or not isinstance(metadata, dict):
        return None
    focus_text = focus.get("text")
    evidence = metadata.get("appliedSenseEvidence")
    if not isinstance(focus_text, str) or not isinstance(evidence, list):
        return None
    selected = [
        {
            "source": item.get("source"),
            "meaning": item.get("englishDefinition"),
            "russian": item.get("selectedTranslation"),
        }
        for item in evidence
        if isinstance(item, dict)
        and isinstance(item.get("source"), str)
        and _contains_standalone_ascii_focus(item["source"], focus_text)
    ]
    return canonical_json(selected)


def _paired_provenance_invariants(
    report: dict[str, Any], label: str
) -> dict[str, Any]:
    provenance = report.get("runProvenance")
    if not isinstance(provenance, dict):
        raise BenchmarkError(
            f"{label}: paired comparison требует runProvenance нового формата."
        )
    ranker = provenance.get("ranker")
    if not isinstance(ranker, dict):
        raise BenchmarkError(f"{label}: отсутствует provenance ранжировщика.")
    required_top = (
        "translationModelIdentity",
        "translationPromptIdentity",
        "rankerProvider",
        "abstentionTag",
        "applicationArtifact",
        "harnessArtifact",
    )
    required_ranker = (
        "benchmarkSet",
        "runtimeArtifacts",
        "dictionaryArtifacts",
    )
    if any(provenance.get(field) is None for field in required_top):
        raise BenchmarkError(f"{label}: provenance запуска неполон.")
    if any(ranker.get(field) is None for field in required_ranker):
        raise BenchmarkError(f"{label}: provenance ранжировщика неполон.")
    return {
        field: provenance[field]
        for field in required_top
    } | {
        field: ranker[field]
        for field in required_ranker
    }


def _apply_holm_family(pairs: Sequence[dict[str, Any]], metric: str) -> None:
    p_values = [pair[metric]["mcnemar"]["exactTwoSidedP"] for pair in pairs]
    order = sorted(range(len(p_values)), key=lambda index: p_values[index])
    previous = 0.0
    for rank, index in enumerate(order):
        adjusted = min(1.0, p_values[index] * (len(p_values) - rank))
        previous = max(previous, adjusted)
        pairs[index][metric]["mcnemar"]["holmAdjustedP"] = previous


def build_paired_comparison(
    reports: Sequence[dict[str, Any]],
    *,
    bootstrap_samples: int = 10_000,
    seed: int = 20_260_730,
) -> dict[str, Any]:
    if len(reports) < 2:
        raise BenchmarkError("Для paired comparison нужны хотя бы два отчёта.")
    for index, report in enumerate(reports):
        validate_report(report, f"reports[{index}]")
        if report.get("system", {}).get("profile") != "smart":
            raise BenchmarkError("Paired ranker comparison допускает только profile=smart.")

    first = reports[0]
    provenance_invariants = [
        _paired_provenance_invariants(report, f"reports[{index}]")
        for index, report in enumerate(reports)
    ]
    expected_provenance = canonical_json(provenance_invariants[0])
    if any(
        canonical_json(item) != expected_provenance
        for item in provenance_invariants[1:]
    ):
        raise BenchmarkError(
            "Paired comparison requires the same build, prompt, provider, "
            "runtime, dictionaries, and benchmark provenance."
        )
    invariant_paths = (
        ("dataset", "evaluationSliceSha256"),
        ("system", "profile"),
        ("system", "backend"),
        ("system", "engine"),
        ("system", "modelIdentity"),
        ("system", "promptIdentity"),
    )
    for path in invariant_paths:
        expected = first[path[0]].get(path[1])
        if any(report[path[0]].get(path[1]) != expected for report in reports[1:]):
            raise BenchmarkError(
                "Paired comparison requires the same " + ".".join(path) + "."
            )
    expected_ids = [item["input"]["id"] for item in first["cases"]]
    if any(
        [item["input"]["id"] for item in report["cases"]] != expected_ids
        for report in reports[1:]
    ):
        raise BenchmarkError("Paired comparison requires identical ordered case ids.")
    rankers = [report["system"].get("ranker") for report in reports]
    if any(not isinstance(item, str) or not item for item in rankers):
        raise BenchmarkError("Every paired report must identify its ranker.")
    if len(set(rankers)) != len(rankers):
        raise BenchmarkError("Paired reports must use distinct rankers.")

    cluster_keys = [_cluster_key(item["input"]) for item in first["cases"]]
    pairs: list[dict[str, Any]] = []
    for left_report, right_report in itertools.combinations(reports, 2):
        left_cases = left_report["cases"]
        right_cases = right_report["cases"]
        left_metrics = [item["metrics"] for item in left_cases]
        right_metrics = [item["metrics"] for item in right_cases]
        left_outputs = [item["output"] for item in left_cases]
        right_outputs = [item["output"] for item in right_cases]

        left_translation = [
            item.get("translationSenseOutcome") == "correct" for item in left_metrics
        ]
        right_translation = [
            item.get("translationSenseOutcome") == "correct" for item in right_metrics
        ]
        left_hover = [item.get("hoverSenseOutcome") == "correct" for item in left_metrics]
        right_hover = [item.get("hoverSenseOutcome") == "correct" for item in right_metrics]
        left_contrast = [item.get("contrastiveOutcome") == "correct" for item in left_metrics]
        right_contrast = [item.get("contrastiveOutcome") == "correct" for item in right_metrics]
        left_anchor = [
            item.get("termAnchorsTotal", 0) > 0
            and item.get("termAnchorsCorrect") == item.get("termAnchorsTotal")
            for item in left_metrics
        ]
        right_anchor = [
            item.get("termAnchorsTotal", 0) > 0
            and item.get("termAnchorsCorrect") == item.get("termAnchorsTotal")
            for item in right_metrics
        ]
        left_chrf = [float(item.get("chrF2") or 0) for item in left_metrics]
        right_chrf = [float(item.get("chrF2") or 0) for item in right_metrics]

        pair_seed = seed + int(hashlib.sha256(
            (left_report["system"]["id"] + "\0" + right_report["system"]["id"]).encode("utf-8")
        ).hexdigest()[:8], 16)
        left_fingerprints = [_metadata_fingerprint(item) for item in left_outputs]
        right_fingerprints = [_metadata_fingerprint(item) for item in right_outputs]
        evidence_comparable = [
            left is not None and right is not None
            for left, right in zip(left_fingerprints, right_fingerprints, strict=True)
        ]
        evidence_changed = [
            comparable and left != right
            for comparable, left, right in zip(
                evidence_comparable,
                left_fingerprints,
                right_fingerprints,
                strict=True,
            )
        ]
        evidence_availability_mismatch = [
            (left is None) != (right is None)
            for left, right in zip(left_fingerprints, right_fingerprints, strict=True)
        ]
        evidence_not_applied_both = [
            left is None and right is None
            for left, right in zip(left_fingerprints, right_fingerprints, strict=True)
        ]
        left_focus_signatures = [
            _focus_evidence_signature(case["input"], output)
            for case, output in zip(left_cases, left_outputs, strict=True)
        ]
        right_focus_signatures = [
            _focus_evidence_signature(case["input"], output)
            for case, output in zip(right_cases, right_outputs, strict=True)
        ]
        focus_comparable = [
            left is not None and right is not None
            for left, right in zip(
                left_focus_signatures,
                right_focus_signatures,
                strict=True,
            )
        ]
        focus_evidence_changed = [
            comparable and left != right
            for comparable, left, right in zip(
                focus_comparable,
                left_focus_signatures,
                right_focus_signatures,
                strict=True,
            )
        ]
        output_changed = [
            left.get("status") != right.get("status")
            or left.get("hypothesis") != right.get("hypothesis")
            for left, right in zip(left_outputs, right_outputs, strict=True)
        ]
        changed_evidence_count = sum(evidence_changed)
        comparable_evidence_count = sum(evidence_comparable)
        same_evidence_count = comparable_evidence_count - changed_evidence_count
        changed_focus_count = sum(focus_evidence_changed)
        comparable_focus_count = sum(focus_comparable)
        pairs.append(
            {
                "left": left_report["system"]["id"],
                "right": right_report["system"]["id"],
                "leftRanker": left_report["system"]["ranker"],
                "rightRanker": right_report["system"]["ranker"],
                "translationSense": {
                    "leftAccuracyIntentionToTreat": _mean([float(item) for item in left_translation]),
                    "rightAccuracyIntentionToTreat": _mean([float(item) for item in right_translation]),
                    "bootstrap": _cluster_bootstrap_delta(
                        [float(b) - float(a) for a, b in zip(left_translation, right_translation, strict=True)],
                        cluster_keys,
                        samples=bootstrap_samples,
                        seed=pair_seed,
                    ),
                    "mcnemar": _mcnemar_exact(left_translation, right_translation),
                },
                "hoverSense": {
                    "leftAccuracyIntentionToTreat": _mean([float(item) for item in left_hover]),
                    "rightAccuracyIntentionToTreat": _mean([float(item) for item in right_hover]),
                    "bootstrap": _cluster_bootstrap_delta(
                        [float(b) - float(a) for a, b in zip(left_hover, right_hover, strict=True)],
                        cluster_keys,
                        samples=bootstrap_samples,
                        seed=pair_seed + 2,
                    ),
                    "mcnemar": _mcnemar_exact(left_hover, right_hover),
                },
                "contrastive": {
                    "leftAccuracy": _mean([float(item) for item in left_contrast]),
                    "rightAccuracy": _mean([float(item) for item in right_contrast]),
                    "mcnemar": _mcnemar_exact(left_contrast, right_contrast),
                },
                "termAnchor": {
                    "leftRecall": _mean([float(item) for item in left_anchor]),
                    "rightRecall": _mean([float(item) for item in right_anchor]),
                    "mcnemar": _mcnemar_exact(left_anchor, right_anchor),
                },
                "chrF2": {
                    "leftMean": _mean(left_chrf),
                    "rightMean": _mean(right_chrf),
                    "winsRight": sum(b > a + 1e-12 for a, b in zip(left_chrf, right_chrf, strict=True)),
                    "ties": sum(abs(b - a) <= 1e-12 for a, b in zip(left_chrf, right_chrf, strict=True)),
                    "lossesRight": sum(b < a - 1e-12 for a, b in zip(left_chrf, right_chrf, strict=True)),
                    "bootstrap": _cluster_bootstrap_delta(
                        [b - a for a, b in zip(left_chrf, right_chrf, strict=True)],
                        cluster_keys,
                        samples=bootstrap_samples,
                        seed=pair_seed + 1,
                    ),
                },
                "causalDiagnostics": {
                    "evidenceComparableCases": comparable_evidence_count,
                    "evidenceNotAppliedBoth": sum(evidence_not_applied_both),
                    "evidenceAvailabilityMismatch": sum(evidence_availability_mismatch),
                    "evidenceChanged": changed_evidence_count,
                    "evidenceChangedRate": (
                        changed_evidence_count / comparable_evidence_count
                        if comparable_evidence_count
                        else None
                    ),
                    "outputChanged": sum(output_changed),
                    "outputChangedRate": sum(output_changed) / len(output_changed),
                    "outputChangedGivenEvidenceChanged": (
                        sum(changed and output for changed, output in zip(evidence_changed, output_changed, strict=True))
                        / changed_evidence_count
                        if changed_evidence_count
                        else None
                    ),
                    "outputChangedGivenEvidenceSame": (
                        sum((not changed) and output for changed, output in zip(evidence_changed, output_changed, strict=True))
                        / same_evidence_count
                        if same_evidence_count
                        else None
                    ),
                    "focusEvidenceChanged": changed_focus_count,
                    "focusEvidenceComparableCases": comparable_focus_count,
                    "focusEvidenceChangedRate": (
                        changed_focus_count / comparable_focus_count
                        if comparable_focus_count
                        else None
                    ),
                    "outputChangedGivenFocusEvidenceChanged": (
                        sum(changed and output for changed, output in zip(focus_evidence_changed, output_changed, strict=True))
                        / changed_focus_count
                        if changed_focus_count
                        else None
                    ),
                },
            }
        )

    _apply_holm_family(pairs, "translationSense")
    _apply_holm_family(pairs, "hoverSense")

    return {
        "schemaVersion": SCHEMA_VERSION,
        "kind": PAIRED_COMPARISON_KIND,
        "generatedUtc": utc_now(),
        "dataset": first["dataset"],
        "invariants": {
            "profile": first["system"]["profile"],
            "backend": first["system"]["backend"],
            "engine": first["system"]["engine"],
            "modelIdentity": first["system"]["modelIdentity"],
            "promptIdentity": first["system"]["promptIdentity"],
            "rankerProvider": provenance_invariants[0]["rankerProvider"],
            "abstentionTag": provenance_invariants[0]["abstentionTag"],
            "applicationArtifact": provenance_invariants[0]["applicationArtifact"],
            "harnessArtifact": provenance_invariants[0]["harnessArtifact"],
            "benchmarkSet": provenance_invariants[0]["benchmarkSet"],
            "runtimeArtifactsSha256": hashlib.sha256(
                canonical_json(provenance_invariants[0]["runtimeArtifacts"]).encode("utf-8")
            ).hexdigest(),
            "dictionaryArtifactsSha256": hashlib.sha256(
                canonical_json(provenance_invariants[0]["dictionaryArtifacts"]).encode("utf-8")
            ).hexdigest(),
            "orderedCaseIdsSha256": hashlib.sha256(
                ("\n".join(expected_ids) + "\n").encode("utf-8")
            ).hexdigest(),
        },
        "bootstrap": {
            "samples": bootstrap_samples,
            "seed": seed,
            "unit": "focus-word-cluster",
        },
        "systems": [report["system"] for report in reports],
        "pairs": pairs,
    }


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Автономный бенчмарк EN-RU-перевода Asteria."
    )
    subparsers = parser.add_subparsers(dest="command", required=True)

    score = subparsers.add_parser("score", help="Посчитать встроенные метрики.")
    score.add_argument("--dataset", type=Path, required=True)
    score.add_argument("--outputs", type=Path, nargs="+", required=True)
    score.add_argument(
        "--selection-report",
        type=Path,
        help="Отчёт C#-раннера для проверки частичной выборки.",
    )
    score.add_argument("--system-id", required=True)
    score.add_argument("--system-name")
    score.add_argument("--dataset-id")
    score.add_argument("--dataset-version")
    score.add_argument("--run-id")
    score.add_argument("--output", type=Path, required=True)

    export = subparsers.add_parser(
        "export-metrics", help="Экспортировать вход для COMET/MetricX."
    )
    export.add_argument("--report", type=Path, required=True)
    export.add_argument("--output-dir", type=Path, required=True)

    metric = subparsers.add_parser(
        "import-metric", help="Импортировать fingerprint-bound external metric."
    )
    metric.add_argument("--report", type=Path, required=True)
    metric.add_argument("--manifest", type=Path, required=True)
    metric.add_argument("--scores", type=Path, required=True)
    metric.add_argument("--metric-id", required=True)
    metric.add_argument("--model-revision", required=True)
    metric.add_argument("--direction", choices=["auto", "higher", "lower"], default="auto")
    metric.add_argument(
        "--reference-mode",
        choices=["auto", "reference-based", "reference-free"],
        default="auto",
    )
    metric.add_argument("--allow-partial", action="store_true")
    metric.add_argument("--output", type=Path, required=True)

    leaderboard = subparsers.add_parser(
        "leaderboard", help="Собрать автономные JSON, CSV и HTML."
    )
    leaderboard.add_argument("--reports", type=Path, nargs="+", required=True)
    leaderboard.add_argument("--metrics", type=Path, nargs="*", default=[])
    leaderboard.add_argument("--title", default="Asteria Translation Benchmark")
    leaderboard.add_argument("--primary-metric", default="auto")
    leaderboard.add_argument("--output-dir", type=Path, required=True)

    paired = subparsers.add_parser(
        "paired", help="Compare smart rankers on the same cases."
    )
    paired.add_argument("--reports", type=Path, nargs="+", required=True)
    paired.add_argument("--bootstrap-samples", type=int, default=10_000)
    paired.add_argument("--seed", type=int, default=20_260_730)
    paired.add_argument("--output", type=Path, required=True)
    return parser


def main(arguments: Sequence[str] | None = None) -> int:
    args = build_parser().parse_args(arguments)
    try:
        if args.command == "score":
            report = build_score_report(
                args.dataset,
                args.outputs,
                args.system_id,
                args.system_name,
                dataset_id=args.dataset_id,
                dataset_version=args.dataset_version,
                run_id=args.run_id,
                selection_report_path=args.selection_report,
            )
            atomic_write_json(args.output, report)
            print(
                f"Готово: {args.output.resolve()} · chrF2="
                f"{_format_number(report['summary']['chrF2']['mean'], 4)}"
            )
            return 0
        if args.command == "export-metrics":
            manifest, segments = export_metric_inputs(args.report, args.output_dir)
            print(f"Готово: {manifest.resolve()} и {segments.resolve()}")
            return 0
        if args.command == "import-metric":
            evaluation = import_external_metric(
                args.report,
                args.manifest,
                args.scores,
                args.metric_id,
                args.model_revision,
                direction=args.direction,
                reference_mode=args.reference_mode,
                allow_partial=args.allow_partial,
            )
            atomic_write_json(args.output, evaluation)
            print(f"Готово: {args.output.resolve()}")
            return 0
        if args.command == "leaderboard":
            reports = [read_json(path) for path in args.reports]
            evaluations = [read_json(path) for path in args.metrics]
            result = build_leaderboard(
                reports,
                evaluations,
                title=args.title,
                primary_metric=args.primary_metric,
            )
            write_leaderboard_bundle(result, args.output_dir)
            print(f"Готово: {(args.output_dir / 'leaderboard.html').resolve()}")
            return 0
        if args.command == "paired":
            if args.bootstrap_samples < 1:
                raise BenchmarkError("bootstrap-samples must be positive.")
            result = build_paired_comparison(
                [read_json(path) for path in args.reports],
                bootstrap_samples=args.bootstrap_samples,
                seed=args.seed,
            )
            atomic_write_json(args.output, result)
            print(f"Готово: {args.output.resolve()}")
            return 0
        raise BenchmarkError(f"Неизвестная команда '{args.command}'.")
    except (BenchmarkError, OSError) as error:
        print(f"Ошибка: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
