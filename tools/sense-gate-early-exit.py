#!/usr/bin/env python3
"""Offline early-exit analysis for the TranslateGemma sense-preservation gate.

Reads one traced smart run (``--sense-gate-trace``) and asks: if the gate
stopped after candidate A whenever A already looked safe, how much of the
sense gain over plain TranslateGemma would remain, and how much latency would
it save? Every policy is replayed from recorded facts only: A's text and gate
verdict, the production final output, and the measured stage timings. No model
runs here, so the analysis is cheap to repeat with a different policy.

Latency is "second line visible, counted from hover start":
card (dictionary + hovered-word ranker) + scaffold (sentence-wide WSD) +
generation. A replayed A is timed separately and never inside the gate time.

Schema 1 traces (E16, sequential hover) feed the policy simulation. Schema 2
traces (E18, progressive hover) already contain the measured first and final
line times, so their ``progressive`` block is measured rather than modelled and
the early-exit arms, whose cost model assumes sequential stages, are omitted.
"""

from __future__ import annotations

import argparse
import importlib.util
import json
import re
import sys
from collections import Counter
from pathlib import Path
from typing import Any, Callable

MODULE_PATH = Path(__file__).with_name("translation-benchmark.py")
SPEC = importlib.util.spec_from_file_location("translation_benchmark", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
benchmark = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(benchmark)

EXPLICIT = {"conflict", "mixed"}
RUSSIAN_TOKEN = re.compile(r"[а-яё]+(?:-[а-яё]+)*")


class LemmaOutcome:
    """Lemma-aware variant of the exact-token MuCoW surface proxy.

    The exact proxy (``asteria.mucow-surface-outcome@2``) counts ``заботой`` as
    unresolved against the target ``забота``. A candidate that copies the
    dictionary lemma therefore scores better than one that inflects it
    correctly, which is exactly what a sense-conditioned prompt does. This
    estimate uses the first pymorphy3 parse; it is a diagnostic, not the @3
    metric that audit card E8 still owes.
    """

    def __init__(self) -> None:
        import pymorphy3  # imported lazily: the default path needs no dependency

        self._analyzer = pymorphy3.MorphAnalyzer()
        self._cache: dict[str, str] = {}

    def _lemma(self, token: str) -> str:
        cached = self._cache.get(token)
        if cached is None:
            cached = self._analyzer.parse(token)[0].normal_form.replace("ё", "е")
            self._cache[token] = cached
        return cached

    def _lemmas(self, text: str) -> list[str]:
        folded = text.lower().replace("ё", "е")
        return [self._lemma(token) for token in RUSSIAN_TOKEN.findall(folded)]

    def _contains(self, text: list[str], target: list[str]) -> bool:
        width = len(target)
        return bool(target) and any(
            text[index : index + width] == target
            for index in range(len(text) - width + 1)
        )

    def __call__(self, text: str, correct: list[str], forbidden: list[str]) -> str:
        lemmas = self._lemmas(text)
        has_correct = any(self._contains(lemmas, self._lemmas(item)) for item in correct)
        has_forbidden = any(self._contains(lemmas, self._lemmas(item)) for item in forbidden)
        if has_correct and has_forbidden:
            return "mixed"
        if has_correct:
            return "correct"
        if has_forbidden:
            return "wrong"
        return "unresolved"


def _structurally_safe(direct: dict[str, Any]) -> bool:
    return (
        direct["displayAccepted"]
        and direct["structuralPenaltyCount"] == 0
        and direct["missingMandatoryTermCount"] == 0
    )


def _primary_states(direct: dict[str, Any]) -> list[str]:
    return [source["state"] for source in direct["sources"] if source["isPrimary"]]


# Each policy answers "show A and stop?" from A's recorded gate verdict only.
POLICIES: dict[str, Callable[[dict[str, Any]], bool]] = {
    # Strictest: exactly the condition under which the gate would not repair.
    "no-repair-needed": lambda d: _structurally_safe(d) and not d["requiresTargetedRepair"],
    # The hovered word is proven preserved; secondary senses are ignored.
    "primary-satisfied": lambda d: _structurally_safe(d)
    and bool(_primary_states(d))
    and all(state == "satisfied" for state in _primary_states(d)),
    # The hovered word is not explicitly contradicted (absent or unresolved
    # primary still exits); secondary senses are ignored.
    "primary-not-contradicted": lambda d: _structurally_safe(d)
    and not any(state in EXPLICIT for state in _primary_states(d)),
    # No source anywhere is explicitly contradicted; unresolved is tolerated.
    "no-explicit-conflict": lambda d: _structurally_safe(d)
    and not any(source["state"] in EXPLICIT for source in d["sources"]),
}


def read_jsonl(path: Path) -> list[dict[str, Any]]:
    with path.open(encoding="utf-8") as handle:
        return [json.loads(line) for line in handle if line.strip()]


def percentile(values: list[float], fraction: float) -> float | None:
    return benchmark.nearest_rank_percentile(values, fraction)


def analyze(
    dataset: list[dict[str, Any]],
    outputs: list[dict[str, Any]],
    trace: list[dict[str, Any]],
    *,
    bootstrap_samples: int = 10_000,
    seed: int = 20260816,
    sense_outcome: Callable[[str, list[str], list[str]], str] | None = None,
) -> dict[str, Any]:
    score = sense_outcome or benchmark._sense_outcome
    cases = {record["id"]: record for record in dataset}
    by_output = {record["id"]: record for record in outputs}
    by_trace = {record["id"]: record for record in trace}
    ids = [record["id"] for record in outputs]
    missing = [case_id for case_id in ids if case_id not in by_trace]
    if missing:
        raise ValueError(f"trace is missing {len(missing)} output cases, e.g. {missing[0]}")
    versions = {line.get("schemaVersion", 1) for line in trace}
    if len(versions) != 1 or not versions <= {1, 2}:
        raise ValueError(f"trace mixes or uses unknown schema versions: {sorted(versions)}")
    measured = versions == {2}

    rows: list[dict[str, Any]] = []
    for case_id in ids:
        case = cases[case_id]
        output = by_output[case_id]
        line = by_trace[case_id]
        targets = benchmark._sense_targets(case)
        if targets is None:
            continue
        current_text = output["hypothesis"] if output["status"] == "ok" else ""
        direct = line.get("direct")
        a_text = (
            direct["translation"]
            if direct is not None and direct["displayAccepted"]
            else None
        )
        card = line["cardMilliseconds"]
        scaffold = line.get("scaffoldMilliseconds") or 0
        gate = line.get("gateMilliseconds") or 0
        # A schema-2 A is the production draft, timed while it overlapped
        # the scaffold; a replay exists only when no draft did.
        replay = line.get("directReplayMilliseconds")
        if measured and replay is None:
            replay = line.get("draftMilliseconds")
        final_line = line.get("finalLineMilliseconds") if measured else None
        rows.append(
            {
                "id": case_id,
                "cluster": benchmark._cluster_key(case),
                "targets": targets,
                "current": current_text,
                "currentRoute": line.get("finalRoute"),
                "direct": direct,
                "aText": a_text,
                "card": card,
                "scaffold": scaffold,
                "gate": gate,
                "replay": replay,
                "currentLatency": final_line if final_line is not None else card + scaffold + gate,
                "firstLine": line.get("firstLineMilliseconds") if measured else None,
                "provisional": line.get("provisionalTranslation") if measured else None,
            }
        )

    def outcome(text: str, row: dict[str, Any]) -> str:
        correct, forbidden = row["targets"]
        return score(text, correct, forbidden)

    # The always-A arm is plain TranslateGemma inside the same session and the
    # same warm server: the fair "no sentence WSD" control for this run.
    def always_a(row: dict[str, Any]) -> tuple[str, float]:
        if row["aText"] is None:
            return row["current"], row["currentLatency"]
        return row["aText"], row["card"] + row["replay"]

    def current(row: dict[str, Any]) -> tuple[str, float]:
        return row["current"], row["currentLatency"]

    def policy_arm(
        exits: Callable[[dict[str, Any]], bool], parallel: bool
    ) -> Callable[[dict[str, Any]], tuple[str, float]]:
        def arm(row: dict[str, Any]) -> tuple[str, float]:
            direct = row["direct"]
            if direct is None or row["aText"] is None or not exits(direct):
                return row["current"], row["currentLatency"]
            # A needs no sense hints, so it may start right after the card and
            # overlap the CPU scaffold; the gate verdict still waits for both.
            generation = (
                max(row["scaffold"], row["replay"])
                if parallel
                else row["scaffold"] + row["replay"]
            )
            return row["aText"], row["card"] + generation

        return arm

    arms: dict[str, Callable[[dict[str, Any]], tuple[str, float]]] = {
        "always-a": always_a,
        "current-gate": current,
    }
    if not measured:
        for name, exits in POLICIES.items():
            arms[f"exit:{name}"] = policy_arm(exits, parallel=False)
            arms[f"exit:{name}+parallel"] = policy_arm(exits, parallel=True)

    baseline = [outcome(always_a(row)[0], row) == "correct" for row in rows]
    reference = [outcome(current(row)[0], row) == "correct" for row in rows]
    clusters = [row["cluster"] for row in rows]
    summaries: dict[str, Any] = {}
    for name, arm in arms.items():
        produced = [arm(row) for row in rows]
        outcomes = Counter(outcome(text, row) for (text, _), row in zip(produced, rows))
        correct = [outcome(text, row) == "correct" for (text, _), row in zip(produced, rows)]
        latencies = [latency for _, latency in produced]
        exits: int | None = None
        if name.startswith("exit:"):
            policy = POLICIES[name.removeprefix("exit:").removesuffix("+parallel")]
            exits = sum(
                1
                for row in rows
                if row["direct"] is not None
                and row["aText"] is not None
                and policy(row["direct"])
            )
        summaries[name] = {
            "cases": len(rows),
            "exits": exits,
            "outcomes": dict(outcomes),
            "correct": sum(correct),
            "versusAlwaysA": {
                **benchmark._mcnemar_exact(baseline, correct),
                **benchmark._cluster_bootstrap_delta(
                    [float(b) - float(a) for a, b in zip(baseline, correct)],
                    clusters,
                    samples=bootstrap_samples,
                    seed=seed,
                ),
            },
            "versusCurrentGate": benchmark._mcnemar_exact(reference, correct),
            "latencyP50Milliseconds": percentile(latencies, 0.50),
            "latencyP95Milliseconds": percentile(latencies, 0.95),
        }

    progressive = measured_progressive(rows, outcome) if measured else modelled_progressive(rows)
    progressive["correct"] = summaries["current-gate"]["correct"]

    a_rows = [row for row in rows if row["direct"] is not None]
    direct_final = [row for row in a_rows if row["currentRoute"] == "direct"]
    consistency = {
        "casesWithReplayedA": len(a_rows),
        "finalRouteDirect": len(direct_final),
        "finalRouteDirectButReplayDiffers": sum(
            1 for row in direct_final if row["aText"] != row["current"]
        ),
        "routes": dict(Counter(str(row["currentRoute"]) for row in rows)),
        "primaryStatesOfA": dict(
            Counter(
                ",".join(_primary_states(row["direct"])) or "no-primary"
                for row in a_rows
            )
        ),
    }
    stages = {
        stage: {
            "p50": percentile([float(row[stage]) for row in rows if row[stage] is not None], 0.5),
            "p95": percentile([float(row[stage]) for row in rows if row[stage] is not None], 0.95),
        }
        for stage in ("card", "scaffold", "gate", "replay")
    }
    return {
        "arms": summaries,
        "progressive": progressive,
        "consistency": consistency,
        "stages": stages,
    }


def modelled_progressive(rows: list[dict[str, Any]]) -> dict[str, Any]:
    """Schema 1: progressive display estimated from sequential stage timings.

    A is shown as soon as it exists (it needs no hints, so it overlaps the CPU
    scaffold) and is replaced only when the gate selects another candidate. Its
    final time reuses A, so only the gate's remaining generations follow.
    """
    first: list[float] = []
    final: list[float] = []
    replaced = 0
    for row in rows:
        if row["aText"] is None or row["replay"] is None:
            first.append(row["currentLatency"])
            final.append(row["currentLatency"])
            continue
        first.append(row["card"] + row["replay"])
        final.append(
            row["card"]
            + max(row["scaffold"], row["replay"])
            + max(0, row["gate"] - row["replay"])
        )
        if row["currentRoute"] not in (None, "direct"):
            replaced += 1
    return {
        "source": "modelled",
        "replacedAfterFirstLine": replaced,
        "firstLineP50Milliseconds": percentile(first, 0.50),
        "firstLineP95Milliseconds": percentile(first, 0.95),
        "finalLineP50Milliseconds": percentile(final, 0.50),
        "finalLineP95Milliseconds": percentile(final, 0.95),
    }


def measured_progressive(
    rows: list[dict[str, Any]], outcome: Callable[[str, dict[str, Any]], str]
) -> dict[str, Any]:
    """Schema 2: what the progressive hover actually painted, and when.

    ``provisionalOutcomes`` scores the provisional line itself: a ``wrong``
    there is a misleading sense the reader saw until the gate replaced it.
    """
    first: list[float] = []
    final: list[float] = []
    shown = 0
    replaced = 0
    withheld = 0
    provisional_outcomes: Counter[str] = Counter()
    for row in rows:
        final.append(row["currentLatency"])
        provisional = row["provisional"]
        if provisional is None or row["firstLine"] is None:
            first.append(row["currentLatency"])
            if row["aText"] is not None:
                withheld += 1
            continue
        shown += 1
        first.append(row["firstLine"])
        provisional_outcomes[outcome(provisional, row)] += 1
        if provisional != row["current"]:
            replaced += 1
    return {
        "source": "measured",
        "provisionalShown": shown,
        "replacedAfterFirstLine": replaced,
        "displayableAWithheld": withheld,
        "provisionalOutcomes": dict(provisional_outcomes),
        "firstLineP50Milliseconds": percentile(first, 0.50),
        "firstLineP95Milliseconds": percentile(first, 0.95),
        "finalLineP50Milliseconds": percentile(final, 0.50),
        "finalLineP95Milliseconds": percentile(final, 0.95),
    }


def render(result: dict[str, Any]) -> str:
    lines = [
        f"{'arm':42} {'exits':>5} {'correct':>7} {'d vs A':>7} {'CI95 (pp)':>17} "
        f"{'McNemar':>8} {'lost vs gate':>12} {'p50 ms':>7} {'p95 ms':>7}"
    ]
    for name, arm in result["arms"].items():
        versus = arm["versusAlwaysA"]
        interval = versus["confidence95"]
        ci = (
            f"[{interval[0] * 100:+.2f}; {interval[1] * 100:+.2f}]"
            if interval
            else "n/a"
        )
        lost = arm["versusCurrentGate"]["leftOnlyCorrect"]
        gained = arm["versusCurrentGate"]["rightOnlyCorrect"]
        lines.append(
            f"{name:42} {arm['exits'] if arm['exits'] is not None else '-':>5} "
            f"{arm['correct']:>7} {versus['deltaRightMinusLeft'] * 100:>+6.2f} "
            f"{ci:>17} {versus['exactTwoSidedP']:>8.4f} {f'-{lost}/+{gained}':>12} "
            f"{arm['latencyP50Milliseconds']:>7.0f} {arm['latencyP95Milliseconds']:>7.0f}"
        )
    lines.append("progressive: " + json.dumps(result["progressive"], ensure_ascii=False))
    lines.append("consistency: " + json.dumps(result["consistency"], ensure_ascii=False))
    lines.append("stages (ms): " + json.dumps(result["stages"], ensure_ascii=False))
    return "\n".join(lines)


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("dataset", type=Path)
    parser.add_argument("outputs", type=Path)
    parser.add_argument("trace", type=Path)
    parser.add_argument("--summary", type=Path)
    parser.add_argument(
        "--lemmatize",
        action="store_true",
        help="score senses by pymorphy3 lemma instead of the exact-token @2 proxy",
    )
    args = parser.parse_args(argv)
    # The consistency block carries Russian route data; a Windows console
    # defaults to a legacy code page that cannot encode it.
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    result = analyze(
        read_jsonl(args.dataset),
        read_jsonl(args.outputs),
        read_jsonl(args.trace),
        sense_outcome=LemmaOutcome() if args.lemmatize else None,
    )
    result["senseMetric"] = (
        "pymorphy3-first-parse-lemma (diagnostic)"
        if args.lemmatize
        else "asteria.mucow-surface-outcome@2"
    )
    print(render(result))
    if args.summary is not None:
        benchmark.atomic_write_json(args.summary, result)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
