from __future__ import annotations

import hashlib
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path


SCRIPT_PATH = Path(__file__).resolve().parents[1] / "fetch-benchmark-datasets.py"
SPEC = importlib.util.spec_from_file_location("fetch_benchmark_datasets", SCRIPT_PATH)
assert SPEC is not None and SPEC.loader is not None
fetcher = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(fetcher)


def jsonl(*rows: dict[str, object]) -> bytes:
    return b"".join(
        (json.dumps(row, ensure_ascii=False, separators=(",", ":")) + "\n").encode(
            "utf-8"
        )
        for row in rows
    )


class BenchmarkDatasetFetcherTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)

    def tearDown(self) -> None:
        self.temp.cleanup()

    def _file(self, file_id: str, role: str, path: str, payload: bytes, **extra):
        source = self.root / "fixture-sources" / path
        source.parent.mkdir(parents=True, exist_ok=True)
        source.write_bytes(payload)
        return {
            "id": file_id,
            "role": role,
            "path": path,
            "url": source.as_uri(),
            "size": len(payload),
            "sha256": hashlib.sha256(payload).hexdigest(),
            "fixtureSource": str(source),
            **extra,
        }

    @staticmethod
    def _dataset(dataset_id: str, importer: str, expected: int, files, **extra):
        return {
            "id": dataset_id,
            "title": dataset_id,
            "homepage": "https://example.test/dataset",
            "revision": "test-revision",
            "languagePair": "en-ru",
            "importer": importer,
            "expectedRecords": expected,
            "license": {
                "spdx": "MIT",
                "redistribution": "test-only",
            },
            "files": files,
            **extra,
        }

    def _install_raw(self, output: Path, dataset: dict[str, object]) -> None:
        for file_info in dataset["files"]:
            source = Path(file_info["fixtureSource"])
            destination = fetcher.raw_file_path(output, dataset, file_info)
            destination.parent.mkdir(parents=True, exist_ok=True)
            destination.write_bytes(source.read_bytes())

    def _manifest_file(self) -> Path:
        path = self.root / "fixture-manifest.json"
        path.write_text("{}\n", encoding="utf-8", newline="\n")
        return path

    def test_production_manifest_is_valid_and_pinned(self) -> None:
        manifest = fetcher.load_manifest(fetcher.DEFAULT_MANIFEST)
        by_id = {dataset["id"]: dataset for dataset in manifest["datasets"]}
        self.assertEqual(87, by_id["wmt25-general"]["expectedRecords"])
        self.assertEqual(200, by_id["wmt25-humeval"]["expectedRecords"])
        self.assertEqual(
            40,
            by_id["wmt25-humeval"]["derivedSelections"][0]["expectedRecords"],
        )
        self.assertEqual(
            10,
            by_id["wmt25-humeval"]["derivedSelections"][1]["expectedRecords"],
        )
        self.assertEqual("NOASSERTION", by_id["wmt24-biomedical"]["license"]["spdx"])
        self.assertEqual(
            "CC-BY-NC-4.0", by_id["wmt25-terminology"]["license"]["spdx"]
        )
        for dataset in manifest["datasets"]:
            for file_info in dataset["files"]:
                self.assertRegex(file_info["sha256"], r"^[0-9a-f]{64}$")
                self.assertGreater(file_info["size"], 0)

    def test_fetch_is_atomic_and_replaces_only_after_exact_verification(self) -> None:
        payload = "закреплённые данные\n".encode("utf-8")
        file_info = self._file("dataset", "dataset", "data/set.txt", payload)
        destination = self.root / "cache" / "set.txt"
        self.assertTrue(fetcher.fetch_file(destination, file_info, 5.0))
        self.assertEqual(payload, destination.read_bytes())
        self.assertFalse(fetcher.fetch_file(destination, file_info, 5.0))

        destination.write_bytes(b"corrupt")
        self.assertTrue(fetcher.fetch_file(destination, file_info, 5.0))
        self.assertEqual(payload, destination.read_bytes())
        self.assertFalse(destination.with_name("set.txt.partial").exists())

        invalid = dict(file_info)
        invalid["sha256"] = "0" * 64
        bad_destination = self.root / "cache" / "bad.txt"
        with self.assertRaises(fetcher.DatasetToolError):
            fetcher.fetch_file(bad_destination, invalid, 5.0)
        self.assertFalse(bad_destination.exists())
        self.assertFalse(bad_destination.with_name("bad.txt.partial").exists())

    def test_path_traversal_is_rejected(self) -> None:
        with self.assertRaises(fetcher.DatasetToolError):
            fetcher.safe_join(self.root, "../escape.jsonl")

    def test_focus_and_guard_match_dotnet_utf16_indices(self) -> None:
        source = "🧵 NOTE: keep this exact."
        focus = fetcher._focus(source, "NOTE", "emoji")
        self.assertEqual(3, focus["characterIndex"])
        self.assertEqual("NOTE", focus["text"])

    def test_smart_hover_selector_rejects_partial_wmt_segments(self) -> None:
        self.assertTrue(fetcher._smart_hover_whole_source_compatible("Wait here, please."))
        self.assertTrue(fetcher._smart_hover_whole_source_compatible("Ask Mr. Smith first."))
        self.assertTrue(fetcher._smart_hover_whole_source_compatible("Version 2.5 works"))
        self.assertFalse(fetcher._smart_hover_whole_source_compatible("Wait here. Then leave."))
        self.assertFalse(fetcher._smart_hover_whole_source_compatible('"Wait here.'))
        self.assertFalse(fetcher._smart_hover_whole_source_compatible(" Wait here."))
        self.assertFalse(fetcher._smart_hover_whole_source_compatible("Alice: Wait here."))
        self.assertFalse(fetcher._smart_hover_whole_source_compatible("[Alice] Wait here."))
        self.assertFalse(fetcher._smart_hover_whole_source_compatible("Alice\nWait here."))
        metadata = fetcher._guard_metadata("🧵 " + ("a" * 998))
        self.assertEqual(1001, metadata["sourceCharacters"])
        self.assertFalse(metadata["modelGuardCompatible"])

    def test_wmt25_general_imports_references_baselines_and_real_guard(self) -> None:
        long_source = " ".join(["word"] * 65)
        source = jsonl(
            {
                "dataset_id": "wmttest2025",
                "collection_id": "general",
                "doc_id": "en-ru_RU_#_news_#_one",
                "domain": "news",
                "src_lang": "en",
                "tgt_lang": "ru_RU",
                "src_text": long_source,
                "refs": {"refA": {"ref": "Эталон."}},
            },
            {
                "doc_id": "de-en_#_news_#_ignored",
                "domain": "news",
                "src_lang": "de",
                "tgt_lang": "en",
                "src_text": "ignored",
                "refs": {"refA": {"ref": "ignored"}},
            },
        )
        baseline = jsonl(
            {
                "doc_id": "en-ru_RU_#_news_#_one",
                "hypothesis": "Публичный перевод.",
            }
        )
        files = [
            self._file("dataset", "dataset", "wmt/source.jsonl", source),
            self._file(
                "baseline",
                "baseline",
                "wmt/system.jsonl",
                baseline,
                systemId="Public System",
            ),
        ]
        dataset = self._dataset("wmt25-general-test", "wmt25-general", 1, files)
        paths = {item["id"]: Path(item["fixtureSource"]) for item in files}
        rows = fetcher._import_wmt25_general(dataset, paths)
        self.assertEqual(["Эталон."], rows[0]["references"])
        self.assertEqual("Публичный перевод.", rows[0]["baselineTranslations"]["Public System"])
        self.assertFalse(rows[0]["metadata"]["modelGuardCompatible"])
        self.assertEqual(65, rows[0]["metadata"]["sourceWordCount"])

    def test_humeval_materializes_full_slice_and_public_baselines(self) -> None:
        eligible = {
            "doc_id": "en-ru_RU_#_social_#_doc_#_0",
            "src_text": "A short sentence.",
            "tgt_text": {"refA": "Короткое предложение.", "System A": "Перевод A."},
            "scores": {"System A": [{"score": 90, "errors": []}]},
        }
        ineligible = {
            "doc_id": "en-ru_RU_#_news_#_doc_#_1",
            "src_text": " ".join(["word"] * 65),
            "tgt_text": {"refA": "Длинный эталон.", "System A": "Перевод B."},
            "scores": {
                "System A": [
                    {"score": 70, "errors": [{"severity": "major"}]}
                ]
            },
        }
        files = [
            self._file("dataset", "dataset", "humeval/data.jsonl", jsonl(eligible, ineligible))
        ]
        dataset = self._dataset(
            "wmt25-humeval-test",
            "wmt25-humeval",
            2,
            files,
            derivedSelections=[
                {
                    "id": "wmt25-humeval-test-compatible",
                    "title": "compatible",
                    "selector": "production-phrase-guard-v1",
                    "expectedRecords": 1,
                },
                {
                    "id": "wmt25-humeval-test-smart-hover",
                    "title": "smart hover",
                    "selector": "smart-hover-whole-source-v1",
                    "expectedRecords": 1,
                }
            ],
        )
        output = self.root / "artifacts"
        self._install_raw(output, dataset)
        results = fetcher.import_dataset(output, dataset, self._manifest_file())
        self.assertEqual(3, len(results))
        full_path = output / "normalized" / "wmt25-humeval-test.jsonl"
        slice_path = output / "normalized" / "wmt25-humeval-test-compatible.jsonl"
        self.assertEqual(2, len(fetcher._read_jsonl(full_path)))
        slice_rows = fetcher._read_jsonl(slice_path)
        self.assertEqual(1, len(slice_rows))
        self.assertEqual("A short sentence.", slice_rows[0]["source"])

        baseline_path = (
            output
            / "normalized"
            / "baselines"
            / "wmt25-humeval-test-compatible"
            / "system-a.jsonl"
        )
        baseline_rows = fetcher._read_jsonl(baseline_path)
        self.assertEqual(1, len(baseline_rows))
        self.assertEqual("system-a", baseline_rows[0]["systemId"])
        self.assertEqual(slice_rows[0]["sourceSha256"], baseline_rows[0]["sourceSha256"])
        expected_selection = fetcher.selection_sha256(
            slice_rows,
            fetcher.sha256_file(slice_path),
        )
        self.assertEqual(expected_selection, baseline_rows[0]["selectionSha256"])
        smart_slice = fetcher._read_jsonl(
            output / "normalized" / "wmt25-humeval-test-smart-hover.jsonl"
        )
        self.assertEqual(["A short sentence."], [row["source"] for row in smart_slice])

        before_dataset = slice_path.read_bytes()
        provenance_path = slice_path.with_suffix(".provenance.json")
        before_provenance = provenance_path.read_bytes()
        fetcher.import_dataset(output, dataset, self._manifest_file())
        self.assertEqual(before_dataset, slice_path.read_bytes())
        self.assertEqual(before_provenance, provenance_path.read_bytes())

    def test_wmt26_and_winomt_are_explicitly_reference_free(self) -> None:
        wmt_path = self.root / "wmt26.jsonl"
        wmt_path.write_bytes(
            jsonl(
                {
                    "doc_id": "doc",
                    "domain": "news",
                    "src_lang": "eng_Latn",
                    "tgt_lang": "rus_Cyrl",
                    "src_text": "Source only.",
                    "refs": {},
                }
            )
        )
        wmt_rows = fetcher._import_wmt26_general(
            {"files": []}, {"dataset": wmt_path}
        )
        self.assertTrue(wmt_rows[0]["referenceFree"])
        self.assertEqual([], wmt_rows[0]["references"])

        winomt_path = self.root / "winomt.txt"
        winomt_path.write_text(
            "female\t1\tThe developer thanked her manager.\tdeveloper\n",
            encoding="utf-8",
            newline="\n",
        )
        winomt_rows = fetcher._import_winomt_gender(
            {"files": []}, {"dataset": winomt_path}
        )
        self.assertTrue(winomt_rows[0]["referenceFree"])
        self.assertEqual("female", winomt_rows[0]["metadata"]["expectedGender"])
        self.assertEqual("developer", winomt_rows[0]["focus"]["text"])

    def test_voita_mucow_biomedical_and_terminology_importers(self) -> None:
        voita_path = self.root / "voita.json"
        voita_path.write_text(
            json.dumps(
                [
                    {
                        "src": "one _eos two _eos three _eos It works.",
                        "dst": [
                            "один _eos два _eos три _eos Это работает.",
                            "один _eos два _eos три _eos Она работает.",
                        ],
                        "true_ind": 0,
                        "ctx_dist": 3,
                    }
                ],
                ensure_ascii=False,
            ),
            encoding="utf-8",
        )
        voita_dataset = {
            "files": [
                {
                    "id": "deixis-test",
                    "phenomenon": "deixis",
                    "split": "test",
                    "expectedRecords": 1,
                }
            ]
        }
        voita = fetcher._import_voita_context(
            voita_dataset, {"deixis-test": voita_path}
        )[0]
        self.assertEqual(["one", "two", "three"], voita["previousContext"])
        self.assertEqual(["Это работает."], voita["references"])
        self.assertEqual(["Она работает."], voita["contrastReferences"])

        mucow_path = self.root / "mucow.json"
        mucow_path.write_text(
            json.dumps(
                [
                    {
                        "id": "duplicate",
                        "source": "The bank is closed.",
                        "reference": "Банк закрыт.",
                        "original translation": "Банк закрыт.",
                        "ambig word": "bank",
                        "origin": "books",
                        "sense": "financial",
                        "errors": [
                            {
                                "contrastive": "Берег закрыт.",
                                "type": "sense",
                                "replacement": "берег",
                            }
                        ],
                    },
                    {
                        "id": "duplicate",
                        "source": "The bank approved it.",
                        "reference": "Банк одобрил это.",
                        "original translation": "Банк одобрил это.",
                        "ambig word": "bank",
                        "origin": "books",
                        "sense": "financial",
                        "errors": [],
                    },
                ],
                ensure_ascii=False,
            ),
            encoding="utf-8",
        )
        mucow = fetcher._import_mucow_wsd(
            {"files": []}, {"dataset": mucow_path}
        )
        self.assertNotEqual(mucow[0]["id"], mucow[1]["id"])
        self.assertEqual("bank", mucow[0]["focus"]["text"])

        source_path = self.root / "bio-en.txt"
        reference_path = self.root / "bio-ru.txt"
        source_path.write_text("Medical abstract.\n", encoding="utf-8", newline="\n")
        reference_path.write_text("Медицинская аннотация.\n", encoding="utf-8", newline="\n")
        biomedical = fetcher._import_wmt24_biomedical(
            {"files": []}, {"source": source_path, "reference": reference_path}
        )
        self.assertEqual("biomedical", biomedical[0]["domain"])

        terminology_path = self.root / "terms.jsonl"
        terminology_path.write_bytes(
            jsonl(
                {
                    "en": "Open the Notes pane.",
                    "ru": "Откройте область примечаний.",
                    "proper": {"pane": "область"},
                    "random": {"Notes": "примечания"},
                    "noterm": {},
                }
            )
        )
        terminology = fetcher._import_wmt25_terminology(
            {"files": []}, {"dataset": terminology_path}
        )[0]
        self.assertEqual([{"en": "pane", "ru": "область"}], terminology["termAnchors"])


if __name__ == "__main__":
    unittest.main()
