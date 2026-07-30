#!/usr/bin/env python3
"""Dependency-free tests for tools/translation-benchmark.py."""

from __future__ import annotations

import csv
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path


MODULE_PATH = Path(__file__).with_name("translation-benchmark.py")
SPEC = importlib.util.spec_from_file_location("translation_benchmark", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
benchmark = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(benchmark)


def write_jsonl(path: Path, records: list[dict]) -> None:
    path.write_text(
        "".join(
            json.dumps(record, ensure_ascii=False, separators=(",", ":")) + "\n"
            for record in records
        ),
        encoding="utf-8",
    )


def output_for(
    case: dict,
    hypothesis: str,
    *,
    system_id: str,
    selection: str,
    status: str = "ok",
    latency: int = 10,
) -> dict:
    return {
        "schemaVersion": 1,
        "id": case["id"],
        "sourceSha256": benchmark.source_sha256(case["source"]),
        "selectionSha256": selection,
        "hypothesis": hypothesis,
        "status": status,
        "latencyMilliseconds": latency,
        "focus": case.get("focus"),
        "fromCache": False,
        "systemId": system_id,
        "profile": "smart",
        "phraseBackend": "vulkan",
        "rankerCpuModel": "consec",
        "engine": "translategemma-4b",
    }


class TranslationBenchmarkTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.dataset_path = self.root / "suite.jsonl"
        self.dataset = [
            {
                "schemaVersion": 1,
                "id": "multi-ref",
                "source": "The exact term wins.",
                "sourceLanguage": "en",
                "targetLanguage": "ru",
                "references": ["Ошибочный вариант.", "Точный термин побеждает."],
                "referenceFree": False,
                "previousContext": ["Context."],
                "focus": {"text": "term", "characterIndex": 10},
                "speaker": {"name": "Alice", "gender": "feminine"},
                "termAnchors": [{"en": "term", "ru": "термин", "match": "stem"}],
                "contrastReferences": ["Ошибочный вариант."],
                "tags": ["vn"],
                "metadata": {"fixture": True},
            },
            {
                "schemaVersion": 1,
                "id": "latin",
                "source": "Keep it local.",
                "sourceLanguage": "en",
                "targetLanguage": "ru",
                "references": ["Оставьте это локальным."],
                "previousContext": [],
            },
            {
                "schemaVersion": 1,
                "id": "error",
                "source": "This failed.",
                "sourceLanguage": "en",
                "targetLanguage": "ru",
                "references": ["Это не удалось."],
            },
            {
                "schemaVersion": 1,
                "id": "source-only",
                "source": "A future source-only benchmark.",
                "sourceLanguage": "en",
                "targetLanguage": "ru",
                "references": [],
                "referenceFree": True,
            },
        ]
        write_jsonl(self.dataset_path, self.dataset)
        self.dataset_sha256 = benchmark.sha256_file(self.dataset_path)
        self.selection_a = benchmark.selection_sha256(
            [self.dataset[0]], self.dataset_sha256
        )
        self.selection_b = benchmark.selection_sha256(
            [self.dataset[1]], self.dataset_sha256
        )
        self.full_selection = benchmark.selection_sha256(
            self.dataset, self.dataset_sha256
        )

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def make_report(
        self,
        system_id: str,
        hypotheses: list[str],
        *,
        system_name: str | None = None,
        selection: str | None = None,
    ) -> dict:
        selected = selection or self.full_selection
        outputs = [
            output_for(case, hypothesis, system_id=system_id, selection=selected)
            for case, hypothesis in zip(self.dataset, hypotheses, strict=True)
        ]
        outputs[2]["status"] = "error"
        outputs[2]["hypothesis"] = ""
        output_path = self.root / f"{system_id}.jsonl"
        write_jsonl(output_path, outputs)
        return benchmark.build_score_report(
            self.dataset_path,
            output_path,
            system_id,
            system_name,
            dataset_id="fixture",
        )

    def test_chrf2_multi_reference_and_residual_latin(self) -> None:
        self.assertAlmostEqual(benchmark.chrf2("Точно", "Точно"), 1.0)
        self.assertLess(benchmark.chrf2("Иное", "Точно"), 1.0)
        self.assertAlmostEqual(
            benchmark.multi_reference_chrf2(
                "Второй ответ", ["Первый ответ", "Второй ответ"]
            ),
            1.0,
        )
        self.assertAlmostEqual(benchmark.residual_latin_rate("Я забыл umbrella дома"), 0.25)

    def test_score_accepts_csharp_output_schema(self) -> None:
        report = self.make_report(
            "asteria",
            [
                "Точный термин побеждает.",
                "Русский umbrella",
                "",
                "Перевод без эталона.",
            ],
        )
        benchmark.validate_report(report)
        self.assertEqual(report["summary"]["cases"], 4)
        self.assertEqual(report["summary"]["successful"], 3)
        self.assertEqual(report["summary"]["chrF2"]["referenceScoredCases"], 3)
        self.assertEqual(report["summary"]["termAnchors"], {"correct": 1, "total": 1, "recall": 1.0})
        self.assertEqual(report["summary"]["contrastive"]["accuracy"], 1.0)
        self.assertGreater(report["summary"]["residualLatin"]["meanSuccessful"], 0)
        self.assertEqual(report["system"]["backend"], "vulkan")
        self.assertEqual(report["system"]["ranker"], "consec")
        self.assertEqual(report["system"]["engine"], "translategemma-4b")
        self.assertEqual(report["outputs"]["selectionSha256"], [self.full_selection])

    def test_validation_rejects_source_drift_unknown_fields_and_implicit_source_only(self) -> None:
        normalized = benchmark.validate_dataset_records(self.dataset)
        self.assertEqual(
            normalized[0]["sourceSha256"], benchmark.source_sha256(self.dataset[0]["source"])
        )

        bad_output = output_for(
            self.dataset[0], "Текст", system_id="sys", selection=self.selection_a
        )
        bad_output["sourceSha256"] = "0" * 64
        with self.assertRaises(benchmark.BenchmarkError):
            benchmark.validate_output_records([bad_output], normalized, "sys")

        unknown = dict(self.dataset[0], typoField=True)
        with self.assertRaises(benchmark.BenchmarkError):
            benchmark.validate_dataset_records([unknown])

        source_only = {"id": "x", "source": "Only source", "references": []}
        with self.assertRaises(benchmark.BenchmarkError):
            benchmark.validate_dataset_records([source_only])

    def test_dataset_contract_matches_csharp_languages_license_and_source_limits(self) -> None:
        base = {
            "id": "contract",
            "source": "Safe\r\n\ttext",
            "sourceLanguage": "en",
            "targetLanguage": "ru",
            "references": ["Безопасный текст."],
            "license": {"id": "CC-BY-4.0", "url": "https://example.invalid/license"},
        }
        normalized = benchmark.validate_dataset_records([base])
        self.assertEqual(normalized[0]["license"], base["license"])
        benchmark.validate_dataset_records([dict(base, license="")])

        for invalid in (
            dict(base, sourceLanguage="de"),
            dict(base, targetLanguage="en"),
            dict(base, license=["CC-BY-4.0"]),
            dict(base, source="unsafe\x01control"),
        ):
            with self.subTest(invalid=invalid):
                with self.assertRaises(benchmark.BenchmarkError):
                    benchmark.validate_dataset_records([invalid])

        benchmark.validate_dataset_records([dict(base, source="🧵" * 50_000)])
        with self.assertRaises(benchmark.BenchmarkError):
            benchmark.validate_dataset_records([dict(base, source="🧵" * 50_000 + "x")])

    def test_focus_indices_use_dotnet_utf16_code_units(self) -> None:
        source = "🧵 NOTE: keep this exact."
        record = {
            "id": "emoji-focus",
            "source": source,
            "references": ["Примечание."],
            "focus": {"text": "NOTE", "characterIndex": 3},
        }
        normalized = benchmark.validate_dataset_records([record])
        self.assertEqual(3, normalized[0]["focus"]["characterIndex"])
        self.assertTrue(normalized[0]["focus"]["annotated"])
        wrong = dict(record, focus={"text": "NOTE", "characterIndex": 2})
        with self.assertRaises(benchmark.BenchmarkError):
            benchmark.validate_dataset_records([wrong])

        explicit_false = dict(
            record,
            focus={"text": "NOTE", "characterIndex": 3, "annotated": False},
        )
        normalized_false = benchmark.validate_dataset_records([explicit_false])
        self.assertFalse(normalized_false[0]["focus"]["annotated"])
        mismatched_output = output_for(
            explicit_false,
            "Примечание.",
            system_id="focus-system",
            selection=benchmark.selection_sha256(
                [explicit_false], benchmark.sha256_bytes(b"focus-fixture")
            ),
        )
        mismatched_output["focus"] = {
            "text": "NOTE",
            "characterIndex": 3,
            "annotated": True,
        }
        with self.assertRaises(benchmark.BenchmarkError):
            benchmark.validate_output_records(
                [mismatched_output], normalized_false, "focus-system"
            )

    def test_multiple_shards_and_selection_report(self) -> None:
        short_dataset = self.dataset[:2]
        short_path = self.root / "short.jsonl"
        write_jsonl(short_path, short_dataset)
        short_sha256 = benchmark.sha256_file(short_path)
        selection_a = benchmark.selection_sha256([short_dataset[0]], short_sha256)
        selection_b = benchmark.selection_sha256([short_dataset[1]], short_sha256)
        first = output_for(
            short_dataset[0],
            "Точный термин побеждает.",
            system_id="sharded",
            selection=selection_a,
        )
        second = output_for(
            short_dataset[1],
            "Оставьте это локальным.",
            system_id="sharded",
            selection=selection_b,
        )
        shard_one = self.root / "shard-1.jsonl"
        shard_two = self.root / "shard-2.jsonl"
        write_jsonl(shard_one, [first])
        write_jsonl(shard_two, [second])

        merged = benchmark.build_score_report(
            short_path, [shard_one, shard_two], "sharded"
        )
        self.assertEqual(merged["summary"]["successful"], 2)
        self.assertEqual(
            merged["outputs"]["selectionSha256"],
            sorted([selection_a, selection_b]),
        )

        selection_path = self.root / "selection.json"
        selection_path.write_text(
            json.dumps(
                {
                    "schemaVersion": 1,
                    "selectionSha256": selection_a,
                    "selectedCaseIds": [short_dataset[0]["id"]],
                    "maximumCases": 1,
                    "shardIndex": 1,
                    "shardCount": 2,
                }
            ),
            encoding="utf-8",
        )
        partial = benchmark.build_score_report(
            short_path,
            shard_one,
            "sharded",
            selection_report_path=selection_path,
        )
        self.assertEqual(partial["dataset"]["cases"], 2)
        self.assertEqual(partial["dataset"]["selectedCases"], 1)
        self.assertEqual(partial["summary"]["cases"], 1)
        benchmark.validate_report(partial)

        mismatch = dict(json.loads(selection_path.read_text(encoding="utf-8")))
        mismatch["selectionSha256"] = selection_b
        selection_path.write_text(json.dumps(mismatch), encoding="utf-8")
        with self.assertRaises(benchmark.BenchmarkError):
            benchmark.build_score_report(
                short_path,
                shard_one,
                "sharded",
                selection_report_path=selection_path,
            )

    def test_leaderboard_groups_sharded_and_unsharded_exact_slice(self) -> None:
        short_dataset = self.dataset[:2]
        short_path = self.root / "same-slice.jsonl"
        write_jsonl(short_path, short_dataset)
        short_sha256 = benchmark.sha256_file(short_path)

        shard_paths: list[Path] = []
        for index, case in enumerate(short_dataset):
            path = self.root / f"same-slice-shard-{index}.jsonl"
            write_jsonl(
                path,
                [
                    output_for(
                        case,
                        f"Перевод {index}.",
                        system_id="sharded",
                        selection=benchmark.selection_sha256([case], short_sha256),
                    )
                ],
            )
            shard_paths.append(path)
        sharded = benchmark.build_score_report(
            short_path,
            shard_paths,
            "sharded",
            dataset_id="same-slice",
        )

        unsharded_path = self.root / "same-slice-unsharded.jsonl"
        full_selection = benchmark.selection_sha256(short_dataset, short_sha256)
        write_jsonl(
            unsharded_path,
            [
                output_for(
                    case,
                    f"Другой перевод {index}.",
                    system_id="unsharded",
                    selection=full_selection,
                )
                for index, case in enumerate(short_dataset)
            ],
        )
        unsharded = benchmark.build_score_report(
            short_path,
            unsharded_path,
            "unsharded",
            dataset_id="same-slice",
        )

        leaderboard = benchmark.build_leaderboard(
            [sharded, unsharded],
            [],
            title="Same exact slice",
            primary_metric="chrf2",
        )
        self.assertEqual(len(leaderboard["groups"]), 1)
        self.assertEqual(
            {row["systemId"] for row in leaderboard["groups"][0]["rows"]},
            {"sharded", "unsharded"},
        )

    def test_external_metric_is_bound_to_exact_inputs(self) -> None:
        report = self.make_report(
            "metric-system",
            ["Точный термин побеждает.", "Локально.", "", "Без эталона."],
        )
        report_path = self.root / "report.json"
        benchmark.atomic_write_json(report_path, report)
        metric_dir = self.root / "metric-input"
        manifest_path, segments_path = benchmark.export_metric_inputs(
            report_path, metric_dir
        )
        self.assertTrue(segments_path.exists())

        scores_path = self.root / "scores.csv"
        with scores_path.open("w", encoding="utf-8", newline="") as stream:
            writer = csv.DictWriter(stream, fieldnames=["caseId", "score"])
            writer.writeheader()
            for index, case in enumerate(report["cases"]):
                writer.writerow({"caseId": case["input"]["id"], "score": index + 0.25})
        evaluation = benchmark.import_external_metric(
            report_path,
            manifest_path,
            scores_path,
            "MetricX-24-Hybrid-XL",
            "sha256:fixture",
        )
        self.assertEqual(evaluation["metric"]["direction"], "lower")
        self.assertTrue(evaluation["coverage"]["complete"])
        self.assertEqual(evaluation["segmentsArtifact"]["name"], segments_path.name)
        benchmark.validate_evaluation(evaluation, [report])

        tampered = json.loads(json.dumps(evaluation))
        tampered["metricInputSha256"] = "0" * 64
        with self.assertRaises(benchmark.BenchmarkError):
            benchmark.validate_evaluation(tampered, [report])

        original_segments = segments_path.read_bytes()
        segments_path.write_bytes(original_segments + b"\n")
        with self.assertRaises(benchmark.BenchmarkError):
            benchmark.import_external_metric(
                report_path,
                manifest_path,
                scores_path,
                "MetricX",
                "revision",
            )
        segments_path.write_bytes(original_segments)

        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        manifest["evaluationSliceSha256"] = "0" * 64
        manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
        with self.assertRaises(benchmark.BenchmarkError):
            benchmark.import_external_metric(
                report_path,
                manifest_path,
                scores_path,
                "MetricX",
                "revision",
            )

    def test_partial_external_metric_cannot_be_primary(self) -> None:
        reports = [
            self.make_report(
                system_id,
                ["Перевод один.", "Перевод два.", "", "Перевод четыре."],
            )
            for system_id in ("complete-system", "partial-system")
        ]
        evaluations = []
        metric_key = "COMET@fixture-revision"
        for report in reports:
            report_path = self.root / f"{report['system']['id']}-report.json"
            benchmark.atomic_write_json(report_path, report)
            metric_dir = self.root / f"{report['system']['id']}-metric"
            manifest_path, _ = benchmark.export_metric_inputs(report_path, metric_dir)
            score_rows = report["cases"]
            if report["system"]["id"] == "partial-system":
                score_rows = score_rows[:-1]
            scores_path = self.root / f"{report['system']['id']}-scores.csv"
            with scores_path.open("w", encoding="utf-8", newline="") as stream:
                writer = csv.DictWriter(stream, fieldnames=["caseId", "score"])
                writer.writeheader()
                for index, case in enumerate(score_rows):
                    writer.writerow(
                        {"caseId": case["input"]["id"], "score": index + 0.5}
                    )
            evaluations.append(
                benchmark.import_external_metric(
                    report_path,
                    manifest_path,
                    scores_path,
                    "COMET",
                    "fixture-revision",
                    allow_partial=report["system"]["id"] == "partial-system",
                )
            )

        with self.assertRaises(benchmark.BenchmarkError):
            benchmark.build_leaderboard(
                reports,
                evaluations,
                title="Partial external metric",
                primary_metric=metric_key,
            )

    def test_leaderboard_separates_case_sets_and_escapes_html(self) -> None:
        better = self.make_report(
            "better",
            ["Точный термин побеждает.", "Оставьте это локальным.", "", "Перевод."],
            system_name="<script>alert('x')</script>",
        )
        worse = self.make_report(
            "worse",
            ["Совсем иначе.", "Неверно.", "", "Иначе."],
            system_name="Обычная система",
        )
        leaderboard = benchmark.build_leaderboard(
            [worse, better], [], title="Asteria <unsafe>", primary_metric="chrf2"
        )
        self.assertEqual(len(leaderboard["groups"]), 1)
        self.assertEqual(leaderboard["groups"][0]["rows"][0]["systemId"], "better")

        output_dir = self.root / "leaderboard"
        benchmark.write_leaderboard_bundle(leaderboard, output_dir)
        html_text = (output_dir / "leaderboard.html").read_text(encoding="utf-8")
        self.assertNotIn("<script>alert", html_text)
        self.assertIn("&lt;script&gt;alert", html_text)
        self.assertIn("Content-Security-Policy", html_text)
        csv_path = output_dir / "leaderboard.csv"
        self.assertTrue(csv_path.read_bytes().startswith(b"\xef\xbb\xbf"))
        with csv_path.open("r", encoding="utf-8-sig", newline="") as stream:
            csv_rows = list(csv.DictReader(stream))
        self.assertEqual(csv_rows[0]["datasetId"], "fixture")
        self.assertEqual(csv_rows[0]["track"], "text")
        self.assertEqual(csv_rows[0]["primaryMetric"], "chrf2")
        self.assertEqual(csv_rows[0]["selectedCases"], "4")
        self.assertFalse(list(output_dir.glob("*.tmp")))

        selection_path = self.root / "partial-selection.json"
        selection_path.write_text(
            json.dumps(
                {
                    "schemaVersion": 1,
                    "selectionSha256": self.selection_a,
                    "selectedCaseIds": [self.dataset[0]["id"]],
                }
            ),
            encoding="utf-8",
        )
        partial_output = self.root / "partial-output.jsonl"
        write_jsonl(
            partial_output,
            [
                output_for(
                    self.dataset[0],
                    "Точный термин побеждает.",
                    system_id="partial",
                    selection=self.selection_a,
                )
            ],
        )
        partial = benchmark.build_score_report(
            self.dataset_path,
            partial_output,
            "partial",
            dataset_id="fixture",
            selection_report_path=selection_path,
        )
        separated = benchmark.build_leaderboard(
            [better, partial], [], title="Different slices", primary_metric="chrf2"
        )
        self.assertEqual(len(separated["groups"]), 2)
        self.assertEqual(
            sorted(group["selectedCases"] for group in separated["groups"]), [1, 4]
        )

    def test_score_cli_accepts_one_or_more_output_shards(self) -> None:
        short_dataset = self.dataset[:2]
        short_path = self.root / "cli-suite.jsonl"
        write_jsonl(short_path, short_dataset)
        short_sha256 = benchmark.sha256_file(short_path)
        shard_one = self.root / "cli-shard-one.jsonl"
        shard_two = self.root / "cli-shard-two.jsonl"
        write_jsonl(
            shard_one,
            [
                output_for(
                    short_dataset[0],
                    "Точный термин побеждает.",
                    system_id="cli",
                    selection=benchmark.selection_sha256(
                        [short_dataset[0]], short_sha256
                    ),
                )
            ],
        )
        write_jsonl(
            shard_two,
            [
                output_for(
                    short_dataset[1],
                    "Оставьте это локальным.",
                    system_id="cli",
                    selection=benchmark.selection_sha256(
                        [short_dataset[1]], short_sha256
                    ),
                )
            ],
        )
        report_path = self.root / "cli-report.json"
        result = benchmark.main(
            [
                "score",
                "--dataset",
                str(short_path),
                "--outputs",
                str(shard_one),
                str(shard_two),
                "--system-id",
                "cli",
                "--output",
                str(report_path),
            ]
        )
        self.assertEqual(result, 0)
        report = benchmark.read_json(report_path)
        benchmark.validate_report(report)
        self.assertEqual(report["summary"]["successful"], 2)


if __name__ == "__main__":
    unittest.main()
