#!/usr/bin/env python3
"""Evaluate GlossBERT as an English context/gloss dictionary-sense ranker.

This is an isolated research harness. It does not modify Asteria settings or
package model weights. The blind holdout requires an explicit opt-in so it
cannot accidentally become a prompt-tuning set.
"""

from __future__ import annotations

import argparse
import gzip
import json
import statistics
import time
import unicodedata
from collections import defaultdict
from pathlib import Path

import torch
from transformers import AutoModelForSequenceClassification, AutoTokenizer


def normalize(value: str) -> str:
    return " ".join(unicodedata.normalize("NFC", value).casefold().replace("ё", "е").split())


def mark_target(context: str, target: str) -> str:
    index = context.find(target)
    if index < 0:
        raise ValueError(f"Target {target!r} does not occur in {context!r}")
    end = index + len(target)
    return f'{context[:index]}"{context[index:end]}"{context[end:]}'


def load_sidecar(path: Path, targets: set[str]) -> dict[str, dict[str, list[str]]]:
    result: dict[str, dict[str, list[str]]] = defaultdict(lambda: defaultdict(list))
    with gzip.open(path, "rt", encoding="utf-8") as source:
        for line in source:
            fields = line.rstrip("\n").split("\t")
            if len(fields) < 3:
                continue
            key, translation, definition = fields[:3]
            normalized_key = normalize(key)
            if normalized_key not in targets or not definition.strip():
                continue
            bucket = result[normalized_key][normalize(translation)]
            if definition not in bucket:
                bucket.append(definition)
    return result


def positive_label_index(model) -> int:
    labels = {
        int(index): str(label).casefold()
        for index, label in getattr(model.config, "id2label", {}).items()
    }
    for index, label in labels.items():
        if label in {"yes", "true", "positive", "label_1"}:
            return index
    if getattr(model.config, "num_labels", 0) == 2:
        return 1
    raise ValueError(f"Cannot identify positive label from {labels!r}")


def score_pairs(model, tokenizer, pairs: list[tuple[str, str]], positive_index: int) -> list[float]:
    encoded = tokenizer(
        [pair[0] for pair in pairs],
        [pair[1] for pair in pairs],
        padding=True,
        truncation=True,
        max_length=256,
        return_tensors="pt",
    )
    with torch.inference_mode():
        logits = model(**encoded).logits
        probabilities = torch.softmax(logits, dim=-1)[:, positive_index]
    return [float(value) for value in probabilities]


def evaluate(args: argparse.Namespace) -> dict:
    benchmark_path = args.benchmark.resolve()
    if "holdout" in benchmark_path.name.casefold() and not args.allow_blind_holdout:
        raise SystemExit(
            "Refusing to consume the blind holdout. Pass --allow-blind-holdout only "
            "after model and threshold selection are frozen."
        )

    benchmark = json.loads(benchmark_path.read_text(encoding="utf-8"))
    cases = benchmark["cases"]
    targets = {normalize(case["target"]) for case in cases}
    sidecar = load_sidecar(args.sense_sidecar, targets) if args.sense_sidecar else {}

    started = time.perf_counter()
    tokenizer = AutoTokenizer.from_pretrained(args.model)
    model = AutoModelForSequenceClassification.from_pretrained(args.model)
    model.eval()
    load_seconds = time.perf_counter() - started
    positive_index = positive_label_index(model)

    strict_total = 0
    strict_correct = 0
    partial_total = 0
    partial_correct = 0
    latencies_ms: list[float] = []
    details: list[dict] = []

    for case in cases:
        context = mark_target(case["context"], case["target"])
        raw_candidates = case["candidates"]
        inventory = sidecar.get(normalize(case["target"]), {})
        if args.sidecar_inventory:
            sense_to_translations: dict[str, list[str]] = defaultdict(list)
            for translation, candidate_definitions in inventory.items():
                for definition in candidate_definitions:
                    normalized_definition = normalize(definition)
                    if translation not in sense_to_translations[normalized_definition]:
                        sense_to_translations[normalized_definition].append(translation)

            senses = list(sense_to_translations)
            if len(senses) < 2:
                details.append({"id": case["id"], "coverage": len(senses), "prediction": None})
                continue

            pairs = [
                (context, f'{case["target"]}: {definition}')
                for definition in senses
            ]
            inference_started = time.perf_counter()
            sense_scores = score_pairs(model, tokenizer, pairs, positive_index)
            latencies_ms.append((time.perf_counter() - inference_started) * 1000)
            ranked_senses = sorted(
                range(len(senses)),
                key=lambda index: sense_scores[index],
                reverse=True,
            )
            selected_sense = senses[ranked_senses[0]]
            expected_senses = {
                normalize(definition)
                for definition in inventory.get(normalize(case["expectedTranslation"]), [])
            }
            expected_is_covered = bool(expected_senses)
            correct = selected_sense in expected_senses
            if expected_is_covered:
                strict_total += 1
                strict_correct += int(correct)
                partial_total += 1
                partial_correct += int(correct)

            top_score = sense_scores[ranked_senses[0]]
            second_score = sense_scores[ranked_senses[1]]
            details.append(
                {
                    "id": case["id"],
                    "coverage": len(senses),
                    "expectedSenseCovered": expected_is_covered,
                    "selectedSense": selected_sense,
                    "selectedTranslations": sense_to_translations[selected_sense],
                    "expected": case["expectedTranslation"],
                    "correct": correct if expected_is_covered else None,
                    "topScore": top_score,
                    "margin": top_score - second_score,
                }
            )
            continue

        if raw_candidates and isinstance(raw_candidates[0], dict):
            candidates = [candidate["translation"] for candidate in raw_candidates]
            definitions = [
                [candidate["definition"]]
                if isinstance(candidate.get("definition"), str)
                else list(candidate.get("definitions", []))
                for candidate in raw_candidates
            ]
        else:
            candidates = list(raw_candidates)
            definitions = [
                sidecar.get(normalize(case["target"]), {}).get(normalize(candidate), [])
                for candidate in candidates
            ]

        flattened: list[tuple[str, str]] = []
        owners: list[int] = []
        for candidate_index, candidate_definitions in enumerate(definitions):
            for definition in candidate_definitions[: args.max_definitions]:
                flattened.append(
                    (context, f'{case["target"]}: {definition}')
                )
                owners.append(candidate_index)

        if not flattened:
            details.append({"id": case["id"], "coverage": 0, "prediction": None})
            continue

        inference_started = time.perf_counter()
        pair_scores = score_pairs(model, tokenizer, flattened, positive_index)
        latencies_ms.append((time.perf_counter() - inference_started) * 1000)
        candidate_scores: list[float | None] = [None] * len(candidates)
        for owner, score in zip(owners, pair_scores):
            current = candidate_scores[owner]
            candidate_scores[owner] = score if current is None else max(current, score)

        covered = [index for index, score in enumerate(candidate_scores) if score is not None]
        ranked = sorted(covered, key=lambda index: candidate_scores[index], reverse=True)
        predicted = candidates[ranked[0]]
        expected = case["expectedTranslation"]
        correct = normalize(predicted) == normalize(expected)
        expected_is_covered = any(
            normalize(candidates[index]) == normalize(expected) for index in covered
        )
        full_coverage = len(covered) == len(candidates)
        if expected_is_covered and len(covered) >= 2:
            partial_total += 1
            partial_correct += int(correct)
        if full_coverage:
            strict_total += 1
            strict_correct += int(correct)

        top_score = candidate_scores[ranked[0]]
        second_score = candidate_scores[ranked[1]] if len(ranked) > 1 else None
        details.append(
            {
                "id": case["id"],
                "coverage": len(covered),
                "fullCoverage": full_coverage,
                "prediction": predicted,
                "expected": expected,
                "correct": correct,
                "topScore": top_score,
                "margin": top_score - second_score if second_score is not None else None,
            }
        )

    return {
        "benchmark": str(benchmark_path),
        "model": args.model,
        "modelLoadSeconds": load_seconds,
        "positiveLabelIndex": positive_index,
        "strict": {
            "correct": strict_correct,
            "total": strict_total,
            "accuracy": strict_correct / strict_total if strict_total else None,
        },
        "unsafePartial": {
            "correct": partial_correct,
            "total": partial_total,
            "accuracy": partial_correct / partial_total if partial_total else None,
        },
        "latencyMs": {
            "median": statistics.median(latencies_ms) if latencies_ms else None,
            "maximum": max(latencies_ms) if latencies_ms else None,
        },
        "details": details,
    }


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--benchmark", type=Path, required=True)
    parser.add_argument("--sense-sidecar", type=Path)
    parser.add_argument("--model", default="kanishka/GlossBERT")
    parser.add_argument("--max-definitions", type=int, default=8)
    parser.add_argument(
        "--sidecar-inventory",
        action="store_true",
        help="Rank the complete aligned sense inventory instead of benchmark distractors.",
    )
    parser.add_argument("--allow-blind-holdout", action="store_true")
    return parser.parse_args()


if __name__ == "__main__":
    print(json.dumps(evaluate(parse_args()), ensure_ascii=False, indent=2))
