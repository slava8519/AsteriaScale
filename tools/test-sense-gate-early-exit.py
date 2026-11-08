#!/usr/bin/env python3
"""Dependency-free tests for tools/sense-gate-early-exit.py."""

from __future__ import annotations

import importlib.util
import unittest
from pathlib import Path

MODULE_PATH = Path(__file__).with_name("sense-gate-early-exit.py")
SPEC = importlib.util.spec_from_file_location("sense_gate_early_exit", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
analysis = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(analysis)


def case(case_id: str, focus: str) -> dict:
    return {
        "id": case_id,
        "source": f"We rested on the {focus}.",
        "focus": {"text": focus, "characterIndex": 17, "annotated": True},
        "metadata": {
            "wsdCorrectTargets": ["берегу"],
            "wsdForbiddenTargets": ["банке"],
        },
    }


def direct(translation: str, primary_state: str, *, repair: bool) -> dict:
    return {
        "kind": "direct",
        "translation": translation,
        "strictAccepted": True,
        "displayAccepted": True,
        "missingMandatoryTermCount": 0,
        "missingAdvisoryTermCount": 0,
        "structuralPenaltyCount": 0,
        "structuralViolationCodes": [],
        "requiresTargetedRepair": repair,
        "sources": [{"source": "bank", "isPrimary": True, "state": primary_state}],
    }


def trace(case_id: str, a: dict, route: str, final: str, *, scaffold: int, replay: int) -> dict:
    return {
        "schemaVersion": 1,
        "id": case_id,
        "cardMilliseconds": 100,
        "scaffoldMilliseconds": scaffold,
        "gateMilliseconds": 9000,
        "directReplayMilliseconds": replay,
        "translatorCalls": 1,
        "senseHintCount": 1,
        "finalRoute": route,
        "finalTranslation": final,
        "direct": a,
        "directReplayError": None,
    }


class EarlyExitTests(unittest.TestCase):
    def setUp(self) -> None:
        # Case 1: A already preserved the hovered sense; the gate kept A.
        # Case 2: A used the forbidden sense; the conditioned candidate fixed it.
        self.dataset = [case("c1", "shore"), case("c2", "bank")]
        self.outputs = [
            {"id": "c1", "status": "ok", "hypothesis": "Мы отдыхали на берегу."},
            {"id": "c2", "status": "ok", "hypothesis": "Мы отдыхали на берегу."},
        ]
        self.trace = [
            trace(
                "c1",
                direct("Мы отдыхали на берегу.", "satisfied", repair=False),
                "direct",
                "Мы отдыхали на берегу.",
                scaffold=500,
                replay=1500,
            ),
            trace(
                "c2",
                direct("Мы отдыхали в банке.", "conflict", repair=True),
                "senseConditioned",
                "Мы отдыхали на берегу.",
                scaffold=2000,
                replay=1000,
            ),
        ]

    def run_analysis(self) -> dict:
        return analysis.analyze(
            self.dataset, self.outputs, self.trace, bootstrap_samples=50, seed=1
        )["arms"]

    def test_early_exit_keeps_the_gate_fix_and_saves_time_only_where_safe(self) -> None:
        arms = self.run_analysis()
        self.assertEqual(arms["always-a"]["correct"], 1)
        self.assertEqual(arms["current-gate"]["correct"], 2)
        exit_arm = arms["exit:primary-satisfied"]
        self.assertEqual(exit_arm["exits"], 1)
        self.assertEqual(exit_arm["correct"], 2)
        self.assertEqual(exit_arm["versusCurrentGate"]["leftOnlyCorrect"], 0)
        # c1 exits: card + scaffold + A = 100 + 500 + 1500; c2 keeps 100 + 2000 + 9000.
        self.assertEqual(exit_arm["latencyP50Milliseconds"], 2100)
        self.assertEqual(exit_arm["latencyP95Milliseconds"], 11100)

    def test_parallel_variant_overlaps_scaffold_and_a(self) -> None:
        arms = self.run_analysis()
        # c1 exits at card + max(scaffold, A) = 100 + 1500.
        self.assertEqual(arms["exit:primary-satisfied+parallel"]["latencyP50Milliseconds"], 1600)

    def test_policy_that_exits_on_a_conflict_is_never_offered(self) -> None:
        arms = self.run_analysis()
        for name in analysis.POLICIES:
            self.assertEqual(
                arms[f"exit:{name}"]["correct"],
                2,
                f"{name} showed A although the hovered sense was contradicted",
            )

    def test_progressive_line_keeps_the_gate_and_counts_replacements(self) -> None:
        progressive = analysis.analyze(
            self.dataset, self.outputs, self.trace, bootstrap_samples=50, seed=1
        )["progressive"]
        self.assertEqual(progressive["correct"], 2)
        self.assertEqual(progressive["replacedAfterFirstLine"], 1)
        # First line: card + A = 1600 and 1100 ms.
        self.assertEqual(progressive["firstLineP50Milliseconds"], 1100)
        self.assertEqual(progressive["firstLineP95Milliseconds"], 1600)
        # Final: card + max(scaffold, A) + (gate - A) = 9100 and 10100 ms.
        self.assertEqual(progressive["finalLineP95Milliseconds"], 10100)

    @unittest.skipUnless(
        importlib.util.find_spec("pymorphy3") is not None, "pymorphy3 is not installed"
    )
    def test_lemma_outcome_accepts_inflection_the_exact_proxy_misses(self) -> None:
        lemma = analysis.LemmaOutcome()
        self.assertEqual(
            analysis.benchmark._sense_outcome("Мы пришли с заботой.", ["забота"], ["беспокойство"]),
            "unresolved",
        )
        self.assertEqual(lemma("Мы пришли с заботой.", ["забота"], ["беспокойство"]), "correct")
        self.assertEqual(lemma("Мы отдыхали в банке.", ["берег"], ["банк"]), "wrong")
        self.assertEqual(lemma("Это был банкет.", ["берег"], ["банк"]), "unresolved")

    def test_schema_two_reports_the_measured_progressive_hover(self) -> None:
        # c1: A preserved the sense, was shown provisionally and confirmed.
        # c2: A contradicted the hovered sense, so production withheld it.
        for line, first, final, provisional in (
            (self.trace[0], 1600, 2000, "Мы отдыхали на берегу."),
            (self.trace[1], None, 9000, None),
        ):
            line["schemaVersion"] = 2
            line["directReplayMilliseconds"] = None
            line["draftMilliseconds"] = 1400
            line["firstLineMilliseconds"] = first
            line["finalLineMilliseconds"] = final
            line["provisionalTranslation"] = provisional
        result = analysis.analyze(
            self.dataset, self.outputs, self.trace, bootstrap_samples=50, seed=1
        )
        progressive = result["progressive"]
        self.assertEqual(progressive["source"], "measured")
        self.assertEqual(progressive["provisionalShown"], 1)
        self.assertEqual(progressive["replacedAfterFirstLine"], 0)
        self.assertEqual(progressive["displayableAWithheld"], 1)
        self.assertEqual(progressive["provisionalOutcomes"], {"correct": 1})
        self.assertEqual(progressive["firstLineP50Milliseconds"], 1600)
        self.assertEqual(progressive["firstLineP95Milliseconds"], 9000)
        self.assertEqual(progressive["finalLineP95Milliseconds"], 9000)
        # The early-exit cost model assumes sequential stages; it must not run.
        self.assertEqual(set(result["arms"]), {"always-a", "current-gate"})
        self.assertEqual(result["arms"]["always-a"]["latencyP50Milliseconds"], 1500)

    def test_mixed_schema_versions_are_rejected(self) -> None:
        self.trace[1]["schemaVersion"] = 2
        with self.assertRaises(ValueError):
            analysis.analyze(self.dataset, self.outputs, self.trace, bootstrap_samples=1)

    def test_missing_trace_line_is_rejected(self) -> None:
        with self.assertRaises(ValueError):
            analysis.analyze(self.dataset, self.outputs, self.trace[:1], bootstrap_samples=1)


if __name__ == "__main__":
    unittest.main()
