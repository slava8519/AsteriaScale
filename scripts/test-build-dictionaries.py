#!/usr/bin/env python3
"""Focused tests for the reproducible VN dictionary builder."""

from __future__ import annotations

import importlib.util
import gzip
import io
import json
import subprocess
import sys
import tarfile
import tempfile
import unittest
from pathlib import Path
from unittest import mock


SCRIPT_PATH = Path(__file__).with_name("build-dictionaries.py")
SPEC = importlib.util.spec_from_file_location("asteria_build_dictionaries", SCRIPT_PATH)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError(f"Cannot load {SCRIPT_PATH}")
builder = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = builder
SPEC.loader.exec_module(builder)


def write_json(path: Path, value: object) -> None:
    path.write_text(
        json.dumps(value, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
        newline="\n",
    )


def passing_judgments() -> list[dict[str, object]]:
    return [
        {
            "worker_id": str(index),
            "intent_score": 1,
            "slots_score": 2,
            "grammar_score": 4,
            "spelling_score": 2,
            "language_identification": "target",
        }
        for index in range(3)
    ]


class StructuredVnTests(unittest.TestCase):
    def test_roles_surfaces_and_deterministic_outputs(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "vn.tsv"
            source.write_text(
                "\t".join(builder.VN_SOURCE_COLUMNS)
                + "\n"
                + "project\thard-1\thard\tgold\tain't\tне; нет\t"
                "A colloquial contraction.\tcolloquial\tphrase\n"
                + "project\tsoft-1\tsoft\tsilver\tbeam\tлуч; сияние\t"
                "A ray or shaft of light.\tgeneral\tnoun\n"
                + "project\tscaffold-1\tscaffold\tgold\theart skip a beat\t"
                "сердце\tTo feel a sudden strong emotion.\tfigurative\tphrase\n",
                encoding="utf-8",
                newline="\n",
            )
            source_lock = root / "vn-sources.lock.json"
            write_json(
                source_lock,
                {
                    "schemaVersion": 1,
                    "curatedFile": {
                        "fileName": source.name,
                        "bytes": source.stat().st_size,
                        "sha256": builder.sha256_file(source),
                    },
                    "sources": [
                        {
                            "id": "project",
                            "license": {
                                "spdx": "MIT",
                                "attribution": "Asteria Scale contributors",
                            },
                        }
                    ],
                },
            )
            lock, sources = builder.load_vn_source_lock(source_lock)
            data = builder.parse_curated_tsv(source, lock, sources)

            self.assertEqual(data.hard["ain't"], ["не", "нет"])
            self.assertEqual(data.soft["beam"], ["луч", "сияние"])
            self.assertEqual(len(data.senses["beam"]), 2)
            self.assertEqual(data.scaffold["heart skip a beat"], ["сердце"])

            first = root / "first.tsv.gz"
            second = root / "second.tsv.gz"
            builder.write_dictionary(first, data.hard)
            builder.write_dictionary(second, data.hard)
            self.assertEqual(first.read_bytes(), second.read_bytes())

    def test_scaffold_rejects_multiple_surfaces(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "vn.tsv"
            source.write_text(
                "project\trecord\tscaffold\tgold\ttell me\tсказать; говорить\t"
                "To communicate something.\tgeneral\tphrase\n",
                encoding="utf-8",
            )
            source_lock = root / "vn-sources.lock.json"
            write_json(
                source_lock,
                {
                    "schemaVersion": 1,
                    "curatedFile": {
                        "fileName": source.name,
                        "bytes": source.stat().st_size,
                        "sha256": builder.sha256_file(source),
                    },
                    "sources": [
                        {
                            "id": "project",
                            "license": {
                                "spdx": "MIT",
                                "attribution": "Asteria Scale contributors",
                            },
                        }
                    ],
                },
            )
            lock, sources = builder.load_vn_source_lock(source_lock)
            with self.assertRaisesRegex(ValueError, "scaffold records require one"):
                builder.parse_curated_tsv(source, lock, sources)

    def test_rejects_phrases_beyond_runtime_role_limits(self) -> None:
        cases = (
            ("soft", "silver", 7),
            ("hard", "gold", 11),
            ("scaffold", "gold", 11),
        )
        for role, tier, word_count in cases:
            with self.subTest(role=role), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                source = root / "vn.tsv"
                english = " ".join(f"word{index}" for index in range(word_count))
                source.write_text(
                    f"project\trecord\t{role}\t{tier}\t{english}\tперевод\t"
                    "An English definition.\tgeneral\tphrase\n",
                    encoding="utf-8",
                )
                source_lock = root / "vn-sources.lock.json"
                write_json(
                    source_lock,
                    {
                        "schemaVersion": 1,
                        "curatedFile": {
                            "fileName": source.name,
                            "bytes": source.stat().st_size,
                            "sha256": builder.sha256_file(source),
                        },
                        "sources": [
                            {
                                "id": "project",
                                "license": {
                                    "spdx": "MIT",
                                    "attribution": "Asteria Scale contributors",
                                },
                            }
                        ],
                    },
                )
                lock, sources = builder.load_vn_source_lock(source_lock)
                with self.assertRaisesRegex(ValueError, "runtime limit"):
                    builder.parse_curated_tsv(source, lock, sources)


class SenseEnrichmentTests(unittest.TestCase):
    def test_ru_gloss_split_preserves_parenthetical_commas(self) -> None:
        self.assertEqual(
            builder._split_ru_gloss(
                "[разг.] доставать (до чего-либо), достигать"
            ),
            ("разг.", ["доставать (до чего-либо)", "достигать"]),
        )

    def test_ru_gloss_split_rejects_malformed_and_meta_surfaces(self) -> None:
        self.assertEqual(
            builder._split_ru_gloss("зарабатывать (заработать"),
            ("", []),
        )
        self.assertEqual(
            builder._split_ru_gloss(
                "with), (pant for, мотора), беге), "
                "pant after) страстно желать, "
                "выключатель m on/off, !!!, достигать"
            ),
            ("", ["достигать"]),
        )
        for gloss in ("то же, что достигать", "см. также достигать", "(достигать)"):
            with self.subTest(gloss=gloss):
                self.assertEqual(builder._split_ru_gloss(gloss), ("", []))

    def test_sibling_enrichment_is_strictly_same_part_of_speech(self) -> None:
        # Five columns since roadmap #8 candidate 5: the trailing field is the
        # ranker enrichment, which sibling rows inherit as empty.
        sense_entries = {
            "reach": [
                ("доставать", "To extend as far as.", "", "verb", ""),
                ("доставание", "The act of obtaining.", "", "noun", ""),
            ]
        }
        ru_glosses = {
            "reach": {
                "adj": ["доставать, навязчивый"],
                "noun": ["доставание, получение"],
                "verb": ["доставать (до чего-либо), достигать"],
            }
        }

        added = builder.enrich_sense_entries(sense_entries, {}, ru_glosses)

        self.assertEqual(added, 2)
        self.assertEqual(
            sense_entries["reach"],
            [
                ("доставать", "To extend as far as.", "", "verb", ""),
                ("доставание", "The act of obtaining.", "", "noun", ""),
                ("достигать", "To extend as far as.", "", "verb", ""),
                ("получение", "The act of obtaining.", "", "noun", ""),
            ],
        )


class RegisterLabelTests(unittest.TestCase):
    """Register labels must describe one sense, not the whole article.

    Kaikki attaches most English translation boxes to the entry rather than to
    a sense. Concatenating every sense's label onto those rows marked ordinary
    readings as coarse - `come` "to move nearer" shipped as `жарг. груб.` - and
    surface selection now trusts the register (roadmap #14.2C).
    """

    @staticmethod
    def _sense(gloss: str, tags: list[str] | None = None) -> dict:
        return {"glosses": [gloss], "tags": tags or []}

    def test_mixed_entry_yields_no_entry_level_register(self) -> None:
        senses = [
            self._sense("To move nearer to the point of perspective.", ["intransitive"]),
            self._sense("To arrive.", ["intransitive"]),
            self._sense(
                "To achieve orgasm.", ["intransitive", "slang", "vulgar"]
            ),
        ]
        self.assertEqual(builder._entry_level_register(senses), "")

    def test_uniformly_marked_entry_keeps_its_register(self) -> None:
        senses = [
            self._sense("A coarse insult.", ["vulgar"]),
            self._sense("Another coarse insult.", ["vulgar", "slang"]),
        ]
        self.assertEqual(builder._entry_level_register(senses), "груб.")

    def test_entry_level_register_never_repeats_a_token(self) -> None:
        senses = [
            self._sense("One.", ["slang", "vulgar"]),
            self._sense("Two.", ["vulgar", "slang"]),
        ]
        register = builder._entry_level_register(senses)
        self.assertEqual(register.split(), list(dict.fromkeys(register.split())))
        self.assertEqual(set(register.split()), {"жарг.", "груб."})

    def test_empty_entry_has_no_register(self) -> None:
        self.assertEqual(builder._entry_level_register([]), "")

    def test_per_sense_register_is_unaffected(self) -> None:
        neutral = self._sense("To move nearer.", ["intransitive"])
        coarse = self._sense("To achieve orgasm.", ["slang", "vulgar"])
        self.assertEqual(builder._kaikki_style_prefix(neutral), "")
        self.assertEqual(
            set(builder._kaikki_style_prefix(coarse).split()), {"жарг.", "груб."}
        )

    def test_ordinary_sense_of_a_mixed_entry_ships_unmarked(self) -> None:
        """End-to-end guard on the exact row that exposed the defect."""
        item = {
            "word": "come",
            "lang_code": "en",
            "pos": "verb",
            "senses": [
                {
                    "glosses": ["To move nearer to the point of perspective."],
                    "tags": ["intransitive"],
                },
                {
                    "glosses": ["To achieve orgasm; to cum; to ejaculate."],
                    "tags": ["intransitive", "slang", "vulgar"],
                },
            ],
            "translations": [
                {"code": "ru", "word": "приходить", "sense": "to move nearer"}
            ],
        }
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "en.jsonl"
            source.write_text(
                json.dumps(item, ensure_ascii=False) + "\n", encoding="utf-8"
            )
            _, sense_entries, _ = builder.parse_kaikki_enwiktionary_russian(source)

        rows = sense_entries[builder.normalize_english("come")]
        marked = [row for row in rows if row[2].strip()]
        self.assertEqual(
            marked,
            [],
            f"An ordinary sense inherited the article's coarse labels: {marked}",
        )


class GeneratorPublicationTests(unittest.TestCase):
    def test_keyboard_interrupt_restores_previous_inventory(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            output = root / "output"
            staging = root / "staging"
            output.mkdir()
            staging.mkdir()
            for name in ("a.tsv.gz", "b.tsv.gz", "vn-build-manifest.json"):
                (output / name).write_bytes(("old-" + name).encode("ascii"))
                (staging / name).write_bytes(("new-" + name).encode("ascii"))

            staged_outputs = [
                staging / "a.tsv.gz",
                staging / "b.tsv.gz",
                staging / "vn-build-manifest.json",
            ]
            interrupted_path = staged_outputs[1]
            real_replace = builder.os.replace

            def interrupt_once(source: object, target: object) -> None:
                if Path(source) == interrupted_path:
                    raise KeyboardInterrupt
                real_replace(source, target)

            with mock.patch.object(builder.os, "replace", side_effect=interrupt_once):
                with self.assertRaises(KeyboardInterrupt):
                    builder.publish_vn_outputs(output, staged_outputs)

            for name in ("a.tsv.gz", "b.tsv.gz", "vn-build-manifest.json"):
                self.assertEqual(
                    (output / name).read_bytes(),
                    ("old-" + name).encode("ascii"),
                    name,
                )
            self.assertEqual(list(root.glob(".asteria-vn-backup-*")), [])

    def test_interrupt_after_backup_move_restores_previous_inventory(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            output = root / "output"
            staging = root / "staging"
            output.mkdir()
            staging.mkdir()
            names = ("a.tsv.gz", "b.tsv.gz", "vn-build-manifest.json")
            for name in names:
                (output / name).write_bytes(("old-" + name).encode("ascii"))
                (staging / name).write_bytes(("new-" + name).encode("ascii"))

            staged_outputs = [staging / name for name in names]
            interrupted_target = output / "b.tsv.gz"
            real_replace = builder.os.replace
            interrupted = False

            def interrupt_after_move(source: object, target: object) -> None:
                nonlocal interrupted
                real_replace(source, target)
                if not interrupted and Path(source) == interrupted_target:
                    interrupted = True
                    raise KeyboardInterrupt

            with mock.patch.object(
                builder.os,
                "replace",
                side_effect=interrupt_after_move,
            ):
                with self.assertRaises(KeyboardInterrupt):
                    builder.publish_vn_outputs(output, staged_outputs)

            for name in names:
                self.assertEqual(
                    (output / name).read_bytes(),
                    ("old-" + name).encode("ascii"),
                    name,
                )
            self.assertEqual(list(root.glob(".asteria-vn-backup-*")), [])

    def test_committed_manifest_tracks_repository_inputs(self) -> None:
        repository = SCRIPT_PATH.parent.parent
        manifest_path = repository / "assets" / "Dictionaries" / "vn-build-manifest.json"
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))

        expected = (
            (manifest["generator"], SCRIPT_PATH),
            (
                manifest["inputs"]["curatedVn"],
                repository / "tools" / "dictionary-source" / "vn-lexicon.tsv",
            ),
            (
                manifest["inputs"]["sourceLock"],
                repository / "tools" / "dictionary-source" / "vn-sources.lock.json",
            ),
        )
        for locked, path in expected:
            with self.subTest(path=path.name):
                self.assertEqual(locked["fileName"], path.name)
                self.assertEqual(locked["bytes"], path.stat().st_size)
                self.assertEqual(locked["sha256"], builder.sha256_file(path))


class MassiveTests(unittest.TestCase):
    @staticmethod
    def row(
        record_id: str,
        locale: str,
        utterance: str,
        *,
        slot_method: list[dict[str, str]] | None = None,
        judgments: list[dict[str, object]] | None = None,
    ) -> dict[str, object]:
        value: dict[str, object] = {
            "id": record_id,
            "locale": locale,
            "partition": "train",
            "scenario": "general",
            "intent": "general_greet",
            "utt": utterance,
            "annot_utt": utterance,
            "worker_id": "worker",
        }
        if locale == "ru-RU":
            value["slot_method"] = slot_method or []
            value["judgments"] = (
                passing_judgments() if judgments is None else judgments
            )
        return value

    def test_massive_filters_and_conflict_quarantine(self) -> None:
        english_rows = [
            self.row("1", "en-US", "wake me gently"),
            self.row("2", "en-US", "play local music"),
            self.row("3", "en-US", "set alarm at 5"),
            self.row("4", "en-US", "hello"),
            self.row("5", "en-US", "one two three four five six seven eight nine ten eleven"),
            self.row("6", "en-US", "this judgment fails"),
            self.row("7", "en-US", "show empty target"),
            self.row("8", "en-US", "play latin target"),
            self.row("9", "en-US", "hello there!"),
            self.row("10", "en-US", "hello there?"),
            self.row("11", "en-US", "wake me gently!"),
        ]
        bad_judgments = passing_judgments()
        for judgment in bad_judgments:
            judgment["grammar_score"] = 1
        russian_rows = [
            self.row("1", "ru-RU", "разбуди меня мягко"),
            self.row(
                "2",
                "ru-RU",
                "включи местную музыку",
                slot_method=[{"slot": "music", "method": "localization"}],
            ),
            self.row("3", "ru-RU", "поставь будильник на пять"),
            self.row("4", "ru-RU", "привет"),
            self.row("5", "ru-RU", "слишком длинная строка"),
            self.row("6", "ru-RU", "плохая оценка", judgments=bad_judgments),
            self.row("7", "ru-RU", "!!!"),
            self.row("8", "ru-RU", "включи Radio"),
            self.row("9", "ru-RU", "привет!"),
            self.row("10", "ru-RU", "здравствуйте!"),
        ]
        russian_rows.append(
            self.row("11", "ru-RU", str(russian_rows[0]["utt"]))
        )

        def jsonl(rows: list[dict[str, object]]) -> bytes:
            return (
                "\n".join(json.dumps(row, ensure_ascii=False) for row in rows) + "\n"
            ).encode("utf-8")

        english = jsonl(english_rows)
        russian = jsonl(russian_rows)
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            archive_path = root / "massive.tar"
            with tarfile.open(archive_path, "w") as archive:
                for name, value in (
                    ("1.1/data/en-US.jsonl", english),
                    ("1.1/data/ru-RU.jsonl", russian),
                ):
                    info = tarfile.TarInfo(name)
                    info.size = len(value)
                    info.mtime = 0
                    archive.addfile(info, io.BytesIO(value))

            curated_source = root / "vn.tsv"
            curated_source.write_text(
                "project\trecord\thard\tgold\tvisual novel\tвизуальная новелла\t"
                "A visual novel.\tgeneral\tphrase\n",
                encoding="utf-8",
            )
            source_lock = root / "vn-sources.lock.json"
            write_json(
                source_lock,
                {
                    "schemaVersion": 1,
                    "curatedFile": {
                        "fileName": curated_source.name,
                        "bytes": curated_source.stat().st_size,
                        "sha256": builder.sha256_file(curated_source),
                    },
                    "sources": [
                        {
                            "id": "project",
                            "license": {
                                "spdx": "MIT",
                                "attribution": "Asteria Scale contributors",
                            },
                        },
                        {
                            "id": "amazon-massive-1.1-en-ru",
                            "format": "massive-1.1",
                            "version": "1.1",
                            "license": {
                                "spdx": "CC-BY-4.0",
                                "attribution": "MASSIVE © Amazon.com Inc.",
                            },
                            "archive": {
                                "fileName": archive_path.name,
                                "bytes": archive_path.stat().st_size,
                                "sha256": builder.sha256_file(archive_path),
                            },
                            "members": [
                                {
                                    "path": "1.1/data/en-US.jsonl",
                                    "locale": "en-US",
                                    "records": len(english_rows),
                                    "bytes": len(english),
                                    "sha256": builder.sha256_bytes(english),
                                },
                                {
                                    "path": "1.1/data/ru-RU.jsonl",
                                    "locale": "ru-RU",
                                    "records": len(russian_rows),
                                    "bytes": len(russian),
                                    "sha256": builder.sha256_bytes(russian),
                                },
                            ],
                            "transform": builder.MASSIVE_TRANSFORM_CONTRACT,
                        }
                    ],
                },
            )
            _, sources = builder.load_vn_source_lock(source_lock)
            data = builder.parse_massive_archive(archive_path, sources)

            drifted_sources = json.loads(json.dumps(sources))
            drifted_sources["amazon-massive-1.1-en-ru"]["transform"][
                "maximumWords"
            ] = 9
            with self.assertRaisesRegex(ValueError, "frozen"):
                builder.parse_massive_archive(archive_path, drifted_sources)

            self.assertEqual(data.entries, {"wake me gently": ["разбуди меня мягко"]})
            self.assertEqual(
                data.record_ids_by_key,
                {"wake me gently": ("1", "11")},
            )
            self.assertEqual(data.statistics["equivalentDuplicateRecords"], 1)
            reasons = data.statistics["rejectionsByReason"]
            self.assertEqual(reasons["localized_slot"], 1)
            self.assertEqual(reasons["numeric_source"], 1)
            self.assertEqual(reasons["too_short"], 1)
            self.assertEqual(reasons["too_long"], 1)
            self.assertEqual(reasons["bad_judgments"], 1)
            self.assertEqual(reasons["no_cyrillic"], 1)
            self.assertEqual(reasons["latin_russian"], 1)
            self.assertEqual(reasons["conflicting_normalized_key"], 2)

            output = root / "output"
            partial = subprocess.run(
                [
                    sys.executable,
                    str(SCRIPT_PATH),
                    "--massive-archive",
                    str(archive_path),
                    "--vn-source-lock",
                    str(source_lock),
                    "--output",
                    str(output),
                ],
                check=False,
                capture_output=True,
                text=True,
            )
            self.assertNotEqual(partial.returncode, 0)
            self.assertIn("must be supplied together", partial.stderr)

            subprocess.run(
                [
                    sys.executable,
                    str(SCRIPT_PATH),
                    "--massive-archive",
                    str(archive_path),
                    "--curated-vn",
                    str(curated_source),
                    "--vn-source-lock",
                    str(source_lock),
                    "--output",
                    str(output),
                ],
                check=True,
                capture_output=True,
                text=True,
            )
            provenance_path = output / "vn-dialogue-provenance.tsv.gz"
            with gzip.open(provenance_path, "rt", encoding="utf-8") as source:
                self.assertEqual(
                    source.read(),
                    "source_id\trecord_ids\tnormalized_english\n"
                    "amazon-massive-1.1-en-ru\t1; 11\twake me gently\n",
                )
            manifest = json.loads(
                (output / "vn-build-manifest.json").read_text(encoding="utf-8")
            )
            outputs = {item["fileName"]: item for item in manifest["outputs"]}
            self.assertEqual(outputs["vn-dialogue-provenance.tsv.gz"]["rows"], 1)
            self.assertEqual(
                outputs["vn-dialogue-provenance.tsv.gz"]["sha256"],
                builder.sha256_file(provenance_path),
            )

            second_output = root / "second-output"
            subprocess.run(
                [
                    sys.executable,
                    str(SCRIPT_PATH),
                    "--massive-archive",
                    str(archive_path),
                    "--curated-vn",
                    str(curated_source),
                    "--vn-source-lock",
                    str(source_lock),
                    "--output",
                    str(second_output),
                ],
                check=True,
                capture_output=True,
                text=True,
            )
            self.assertEqual(
                provenance_path.read_bytes(),
                (second_output / "vn-dialogue-provenance.tsv.gz").read_bytes(),
            )
            first_outputs = sorted(path.name for path in output.iterdir())
            self.assertEqual(
                first_outputs,
                sorted(path.name for path in second_output.iterdir()),
            )
            for file_name in first_outputs:
                self.assertEqual(
                    (output / file_name).read_bytes(),
                    (second_output / file_name).read_bytes(),
                    file_name,
                )


if __name__ == "__main__":
    unittest.main()
