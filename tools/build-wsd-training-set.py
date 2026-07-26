"""Build a silver WSD training set in the runtime's exact pair format.

Roadmap #8, training track. The ranker sees pairs built by
``RankerEngine.BuildMarkedContext`` and ``RankerEngine.Score``:

    text_a = context with the target occurrence wrapped in double quotes
    text_b = "<target>: <definition>"

and a two-class head whose positive-class softmax is the candidate score. Any
training set that does not reproduce that shape teaches the model a format the
worker never sends, so both strings are assembled here the same way.

The supervision is free: a Wiktionary sense carries example sentences, and those
examples are already attached to the very definition the shipped sidecar
contains. One example plus the sibling senses of the same lemma is exactly one
GlossBERT training group — positives and hard negatives from one entry, in this
project's own sense inventory rather than WordNet's.

Silver, not gold: dictionary examples are literary and the target domain is
visual-novel dialogue. The distribution gap is the experiment's main risk and is
why the final judgment stays with ``--ranker-benchmark`` on the real runtime.
"""

from __future__ import annotations

import argparse
import gzip
import hashlib
import importlib.util
import json
import random
import re
import sys
import unicodedata
from collections import defaultdict
from pathlib import Path

REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
BUILDER_PATH = REPOSITORY_ROOT / "scripts" / "build-dictionaries.py"
TARGET_CONTEXT_PATH = (
    REPOSITORY_ROOT / "src" / "AsteriaScale" / "Services" / "TargetCenteredContext.cs"
)


def runtime_window_words() -> int:
    """``TargetCenteredContextBuilder.MaximumWords``, read from the runtime.

    Copying the number here would let the two drift apart silently, and a
    training set built for the old window teaches the model an input shape
    production stopped sending. Recalibrating that constant already requires a
    ``--ranker-benchmark`` run; this makes the training set follow it.
    """
    match = re.search(
        r"const\s+int\s+MaximumWords\s*=\s*(\d+)",
        TARGET_CONTEXT_PATH.read_text(encoding="utf-8"),
    )
    if match is None:
        raise SystemExit(f"cannot read MaximumWords from {TARGET_CONTEXT_PATH}")
    return int(match.group(1))

# Shorter examples carry no disambiguating context. The upper bound is not a
# taste judgement: the runtime never hands the model more than this many words
# around the target, so a longer example would reach it as a window this script
# never scored. Counting uses the runtime's own word rule rather than
# str.split, which sees "well-being," as one word where WordRegex sees
# "well-being".
MINIMUM_EXAMPLE_WORDS = 5
MAXIMUM_EXAMPLE_WORDS = runtime_window_words()
# A lemma needs rival senses for the choice to mean anything.
MINIMUM_SENSES_PER_LEMMA = 2
MAXIMUM_NEGATIVES_PER_POSITIVE = 7
# An example that restates its own definition teaches lexical overlap instead of
# meaning. The threshold is deliberately strict: silver data is abundant, so
# discarding a suspect pair costs nothing.
MAXIMUM_DEFINITION_OVERLAP = 0.6
# When a sense has no synonyms, ``_kaikki_sense_enrichment`` falls back to its
# first example — and examples are exactly what this script uses as context. A
# sense with one example would then carry a hint quoting the sentence being
# disambiguated, teaching the model to match the hint against the context
# instead of reading the definition. Enrichment is dropped for that pair.
MAXIMUM_ENRICHMENT_OVERLAP = 0.7
VALIDATION_SHARE = 0.03
TOKEN_PATTERN = re.compile(r"[a-z0-9']+")
HOLDOUT_PATTERN = re.compile(r"ranker-holdout", re.IGNORECASE)
STOP_WORDS = frozenset(
    """a an and are as at be by for from had has have in into is it its of on or
    that the this to was were which who with without not no such other any""".split()
)


def _load_builder():
    """Import the generator so glosses are produced by one implementation.

    The training text must be byte-identical to the shipped definition; the only
    way to keep that true through later edits is to call the same code.
    """
    specification = importlib.util.spec_from_file_location(
        "asteria_build_dictionaries", BUILDER_PATH
    )
    if specification is None or specification.loader is None:
        raise SystemExit(f"cannot import {BUILDER_PATH}")
    module = importlib.util.module_from_spec(specification)
    sys.modules[specification.name] = module
    specification.loader.exec_module(module)
    return module


BUILDER = _load_builder()
clean = BUILDER.clean
is_usable_english_definition = BUILDER.is_usable_english_definition
normalize_english = BUILDER.normalize_english
sense_enrichment = BUILDER._kaikki_sense_enrichment
inflected_forms = BUILDER._kaikki_inflected_forms


def runtime_words(text: str) -> list[str]:
    """Word spans exactly as TargetCenteredContextBuilder.WordRegex sees them.

    That regex is ``[\\p{L}\\p{M}\\p{N}]+(?:['’-][\\p{L}\\p{M}\\p{N}]+)*``; the
    scan below is the same rule written the way build-dictionaries.py writes its
    tokenizer, so the two stay comparable by eye.
    """
    result: list[str] = []
    start = -1
    index = 0
    while index < len(text):
        if unicodedata.category(text[index])[0] in {"L", "M", "N"}:
            if start < 0:
                start = index
            index += 1
            continue

        if (
            start >= 0
            and text[index] in {"'", "’", "-"}
            and index + 1 < len(text)
            and unicodedata.category(text[index + 1])[0] in {"L", "M", "N"}
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


def is_token_character(character: str) -> bool:
    """``RankerTargetAnchor.IsTokenCharacter`` — the worker's own boundary rule.

    Letters, digits, combining marks, dash punctuation and apostrophes continue
    a token, so an occurrence touching one of them is not standalone and
    ``BuildMarkedContext`` refuses to mark it. ``str.isalnum`` alone accepts
    "well-being" as an occurrence of "well", which the worker would reject.
    """
    return (
        character.isalnum()
        or unicodedata.category(character) in {"Mn", "Mc", "Me", "Pd"}
        or character in {"'", "’", "ʼ"}
    )


def content_words(text: str) -> set[str]:
    return {
        token
        for token in TOKEN_PATTERN.findall(text.casefold())
        if token not in STOP_WORDS and len(token) > 2
    }


def definition_of(sense: dict) -> str:
    """The sidecar's definition rule: the longest usable gloss of the sense."""
    return max(
        (
            clean(gloss).replace("\t", " ")
            for gloss in sense.get("glosses", [])
            if clean(gloss) and is_usable_english_definition(clean(gloss))
        ),
        key=len,
        default="",
    )


def find_surface(example: str, forms: list[str]) -> tuple[int, str] | None:
    """Locate a whole-token occurrence of the lemma or one of its forms.

    ``BuildMarkedContext`` throws when the target does not occur in the context,
    so a pair whose surface cannot be located here could never be produced at
    inference either.
    """
    folded = example.casefold()
    best: tuple[int, str] | None = None
    for form in forms:
        if not form:
            continue
        start = 0
        needle = form.casefold()
        while True:
            index = folded.find(needle, start)
            if index < 0:
                break
            start = index + 1
            before = example[index - 1] if index > 0 else " "
            after_index = index + len(needle)
            after = example[after_index] if after_index < len(example) else " "
            if is_token_character(before) or is_token_character(after):
                continue
            candidate = (index, example[index : index + len(needle)])
            # Prefer the longest matched surface at the earliest position, so
            # "bank account" wins over "bank" when both are forms.
            if best is None or len(candidate[1]) > len(best[1]):
                best = candidate
            break
    return best


def marked_context(example: str, index: int, surface: str) -> str:
    return (
        example[:index] + '"' + surface + '"' + example[index + len(surface) :]
    ).strip()


def reject_holdouts(paths: list[Path]) -> None:
    """Frozen holdouts may not shape the training set (invariant 7).

    Reading only the ``target`` field looks harmless — the exclusion can only
    make a later measurement more conservative — but it still lets the holdout
    decide what the model is trained on, and a set that shaped training is no
    longer blind. ARCHITECTURE.md invariant 7 and AGENTS.md rule 5 reserve these
    files for one-shot final confirmation, so this refuses rather than warns:
    the previous default silently opened all three.
    """
    offenders = sorted({path.name for path in paths if HOLDOUT_PATTERN.search(path.name)})
    if offenders:
        raise SystemExit(
            "refusing to read frozen holdouts (" + ", ".join(offenders) + "): "
            "ARCHITECTURE.md invariant 7 and AGENTS.md rule 5 allow them only for "
            "one-shot final confirmation, never for preparing training data. Use "
            "assets/Ranker/ranker-benchmark.json for dev-set exclusions."
        )


def collect_excluded_lemmas(paths: list[Path]) -> tuple[set[str], list[str]]:
    """Target lemmas that must never appear in training.

    Only the ``target`` field is read — never a label, never an expected
    translation. Excluding evaluated lemmas can only make the measurement more
    conservative. Frozen holdouts are rejected outright by ``reject_holdouts``
    before this runs; these are dev sets, which tuning is allowed to consult.
    """
    excluded: set[str] = set()
    notes: list[str] = []
    for path in paths:
        if not path.exists():
            notes.append(f"{path.name}: missing")
            continue
        # Benchmark files are written by the repository's own tooling and carry
        # a BOM; utf-8-sig reads both forms.
        payload = json.loads(path.read_text(encoding="utf-8-sig"))
        cases = payload.get("cases", payload) if isinstance(payload, dict) else payload
        before = len(excluded)
        for case in cases:
            target = normalize_english(str(case.get("target", "")))
            if target:
                excluded.add(target)
        notes.append(f"{path.name}: +{len(excluded) - before} lemmas")
    return excluded, notes


def load_shipped_definitions(path: Path) -> dict[str, set[str]]:
    """Definitions actually present in the shipped sidecar, per lemma."""
    shipped: dict[str, set[str]] = defaultdict(set)
    if not path.exists():
        return shipped
    with gzip.open(path, "rt", encoding="utf-8") as handle:
        for line in handle:
            fields = line.rstrip("\n").split("\t")
            if len(fields) < 3:
                continue
            shipped[fields[0]].add(fields[2])
    return shipped


def source_digest(path: Path) -> tuple[int, str]:
    """Size and SHA-256 of the source dump.

    The dump is several gigabytes, is not in git and is versioned only by the
    day it was downloaded, so a run that does not record which bytes it read
    cannot be reproduced or compared against another machine's run.
    """
    digest = hashlib.sha256()
    size = 0
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1 << 20), b""):
            digest.update(block)
            size += len(block)
    return size, digest.hexdigest()


def iter_entries(path: Path, limit: int | None, statistics: defaultdict):
    with path.open(encoding="utf-8") as handle:
        for number, line in enumerate(handle):
            if limit is not None and number >= limit:
                return
            line = line.strip()
            if not line:
                continue
            try:
                yield json.loads(line)
            except json.JSONDecodeError:
                # A silent skip cannot be told apart from a truncated download,
                # which would quietly shrink the training set instead of failing.
                statistics["source_lines_unparsed"] += 1
                continue


def build(arguments: argparse.Namespace) -> int:
    exclude_paths = [Path(value) for value in arguments.exclude]
    reject_holdouts(exclude_paths)
    excluded, exclusion_notes = collect_excluded_lemmas(exclude_paths)
    shipped = load_shipped_definitions(Path(arguments.sidecar))

    source = Path(arguments.source)
    provenance = (
        None if arguments.skip_source_digest else source_digest(source)
    )

    # lemma -> definition -> [definition, enrichment, pos, examples]
    lemmas: dict[str, dict[str, dict]] = defaultdict(dict)
    statistics = defaultdict(int)

    for item in iter_entries(source, arguments.max_entries, statistics):
        word = clean(item.get("word"))
        lemma = normalize_english(word)
        if not lemma or lemma in excluded:
            statistics["entries_excluded"] += 1 if lemma in excluded else 0
            continue
        statistics["entries"] += 1
        part_of_speech = clean(item.get("pos"))
        forms = [word, *inflected_forms(item)]
        for sense in item.get("senses", []):
            definition = definition_of(sense)
            if not definition:
                continue
            record = lemmas[lemma].setdefault(
                definition,
                {
                    "definition": definition,
                    "enrichment": sense_enrichment(sense),
                    "pos": part_of_speech,
                    "forms": set(),
                    "examples": [],
                },
            )
            record["forms"].update(forms)
            if not record["enrichment"]:
                record["enrichment"] = sense_enrichment(sense)
            for entry in sense.get("examples", []) or []:
                if not isinstance(entry, dict):
                    continue
                text = clean(entry.get("text"))
                if not text:
                    continue
                words = runtime_words(text)
                if not MINIMUM_EXAMPLE_WORDS <= len(words) <= MAXIMUM_EXAMPLE_WORDS:
                    statistics["examples_out_of_range"] += 1
                    continue
                record["examples"].append(unicodedata.normalize("NFC", text))

    groups: list[dict] = []
    random.seed(arguments.seed)
    for lemma, senses in lemmas.items():
        if arguments.shipped_only:
            # At inference the candidate list only ever contains senses that
            # carry a Russian translation, so this restricts training to the
            # exact inventory the ranker will choose from — fewer pairs, no
            # distribution gap. Both variants are worth measuring.
            senses = {
                definition: record
                for definition, record in senses.items()
                if definition in shipped.get(lemma, ())
            }
        if len(senses) < MINIMUM_SENSES_PER_LEMMA:
            statistics["lemmas_single_sense"] += 1
            continue
        if shipped and lemma in shipped:
            matched = sum(
                1 for definition in senses if definition in shipped[lemma]
            )
            statistics["shipped_lemmas"] += 1
            statistics["shipped_definitions_matched"] += matched
            statistics["shipped_definitions_total"] += len(senses)

        ordered = sorted(senses.values(), key=lambda record: record["definition"])
        for positive in ordered:
            if not positive["examples"]:
                continue
            negatives = [
                other
                for other in ordered
                if other["definition"] != positive["definition"]
            ]
            if not negatives:
                continue
            # Same part of speech first: a noun sense competing with a verb sense
            # is a distinction the POS filter already makes upstream.
            negatives.sort(key=lambda other: other["pos"] != positive["pos"])
            negatives = negatives[:MAXIMUM_NEGATIVES_PER_POSITIVE]
            forms = sorted(positive["forms"], key=len, reverse=True)
            for example in positive["examples"]:
                located = find_surface(example, forms)
                if located is None:
                    statistics["examples_without_surface"] += 1
                    continue
                index, surface = located
                overlap_words = content_words(positive["definition"])
                if overlap_words:
                    shared = overlap_words & content_words(example)
                    if len(shared) / len(overlap_words) >= MAXIMUM_DEFINITION_OVERLAP:
                        statistics["examples_overlapping_definition"] += 1
                        continue
                context = marked_context(example, index, surface)
                example_words = content_words(example)

                def render(record: dict) -> dict:
                    plain = f"{surface}: {record['definition']}"
                    hint = record["enrichment"]
                    hint_words = content_words(hint) if hint else set()
                    leaks = bool(hint_words) and (
                        len(hint_words & example_words) / len(hint_words)
                        >= MAXIMUM_ENRICHMENT_OVERLAP
                    )
                    if leaks:
                        statistics["enrichment_dropped_leak"] += 1
                    return {
                        "text_b": plain,
                        "text_b_enriched": (
                            f"{plain} — {hint}" if hint and not leaks else plain
                        ),
                    }

                digest = hashlib.sha256(lemma.encode("utf-8")).hexdigest()
                split = (
                    "valid"
                    if int(digest[:8], 16) / 0xFFFFFFFF < VALIDATION_SHARE
                    else "train"
                )
                group = {
                    "lemma": lemma,
                    "surface": surface,
                    "split": split,
                    "text_a": context,
                    "pairs": [{**render(positive), "label": 1}],
                }
                for other in negatives:
                    group["pairs"].append({**render(other), "label": 0})
                groups.append(group)
                statistics["positives"] += 1
                statistics["negatives"] += len(negatives)
                statistics["enriched_pairs"] += sum(
                    1
                    for pair in group["pairs"]
                    if pair["text_b_enriched"] != pair["text_b"]
                )

    random.shuffle(groups)
    if arguments.max_groups:
        groups = groups[: arguments.max_groups]

    output = Path(arguments.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("w", encoding="utf-8", newline="\n") as handle:
        for group in groups:
            handle.write(json.dumps(group, ensure_ascii=False) + "\n")

    train = sum(1 for group in groups if group["split"] == "train")
    matched = statistics["shipped_definitions_matched"]
    total = statistics["shipped_definitions_total"]
    if provenance is not None:
        print(f"source                   : {source.name}")
        print(f"  bytes                  : {provenance[0]}")
        print(f"  sha-256                : {provenance[1]}")
    print(f"entries scanned          : {statistics['entries']}")
    print(f"lemmas with rival senses : {len(lemmas) - statistics['lemmas_single_sense']}")
    print(f"groups written           : {len(groups)} (train {train}, valid {len(groups) - train})")
    print(f"pairs                    : {sum(len(group['pairs']) for group in groups)}")
    print(f"  positives              : {statistics['positives']}")
    print(f"  negatives              : {statistics['negatives']}")
    print(f"  with enrichment        : {statistics['enriched_pairs']}")
    print(f"  hints dropped as leaks : {statistics['enrichment_dropped_leak']}")
    print(f"dropped: no surface      : {statistics['examples_without_surface']}")
    print(f"dropped: overlaps gloss  : {statistics['examples_overlapping_definition']}")
    print(f"dropped: length          : {statistics['examples_out_of_range']}")
    print(f"unparsed source lines    : {statistics['source_lines_unparsed']}")
    if total:
        print(
            f"sidecar parity           : {matched}/{total} definitions "
            f"({matched / total:.1%}) also present in the shipped sidecar"
        )
    for note in exclusion_notes:
        print(f"excluded from            : {note}")
    print(f"excluded lemmas          : {len(excluded)}")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--source",
        default=str(REPOSITORY_ROOT / "tools/dictionary-source/kaikki.org-dictionary-English.jsonl"),
    )
    parser.add_argument(
        "--sidecar",
        default=str(REPOSITORY_ROOT / "assets/Dictionaries/eng-rus-enwiktionary-senses.tsv.gz"),
        help="Shipped sense sidecar, used only to report format parity.",
    )
    parser.add_argument(
        "--exclude",
        nargs="*",
        default=[
            str(REPOSITORY_ROOT / "assets/Ranker/ranker-benchmark.json"),
            str(REPOSITORY_ROOT / "assets/Ranker/ranker-register-benchmark.json"),
        ],
        help=(
            "Dev benchmark files whose target lemmas are kept out of training. "
            "Frozen ranker-holdout* sets are rejected: they may not shape "
            "training data (ARCHITECTURE.md invariant 7)."
        ),
    )
    parser.add_argument(
        "--skip-source-digest",
        action="store_true",
        help="Do not hash the source dump (faster; the run is then unreproducible).",
    )
    parser.add_argument(
        "--shipped-only",
        action="store_true",
        help="Keep only senses present in the shipped sidecar (inference inventory).",
    )
    parser.add_argument("--output", default="artifacts/wsd-training-set.jsonl")
    parser.add_argument("--max-entries", type=int, default=None)
    parser.add_argument("--max-groups", type=int, default=None)
    parser.add_argument("--seed", type=int, default=20260726)
    return build(parser.parse_args())


if __name__ == "__main__":
    raise SystemExit(main())
