#!/usr/bin/env python3
"""Build the compact Russian verb-morphology lexicon (roadmap task #2).

Source: the OpenCorpora morphological dictionary (CC BY-SA 4.0), consumed via
the maintained pymorphy3-dicts-ru distribution because opencorpora.org exports
are frequently offline. Requires: pip install pymorphy3 pymorphy3-dicts-ru.

Output (TSV inside gzip): normalized wordform, kind, aspect, normalized lemma.
kind: i = infinitive, m/f/n/p = past masculine/feminine/neuter/plural.
aspect: P = perfective, I = imperfective, B = both or ambiguous.

Only verb infinitives and past-tense forms are exported deliberately: these
are the only categories the runtime morphology consults. Everything else
stays on the suffix fallback, so a missing word never breaks analysis.
Forms and lemmas are casefolded with ё→е, matching the runtime normalization.
"""

from __future__ import annotations

import argparse
import gzip
import io
from pathlib import Path

import pymorphy3


def normalize(value: str) -> str:
    return " ".join(value.casefold().replace("ё", "е").split())


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()

    morph = pymorphy3.MorphAnalyzer()
    rows: dict[str, list[str]] = {}
    for parse in morph.iter_known_word_parses():
        tag = parse.tag
        if tag.POS == "INFN":
            kind = "i"
        elif tag.POS == "VERB" and tag.tense == "past":
            if tag.number == "plur":
                kind = "p"
            elif tag.gender == "masc":
                kind = "m"
            elif tag.gender == "femn":
                kind = "f"
            elif tag.gender == "neut":
                kind = "n"
            else:
                continue
        else:
            continue

        form = normalize(parse.word)
        lemma = normalize(parse.normal_form)
        if not form or not lemma or "\t" in form or "\t" in lemma:
            continue
        aspect = {"perf": "P", "impf": "I"}.get(tag.aspect, "B")

        existing = rows.get(form)
        if existing is None:
            rows[form] = [kind, aspect, lemma]
            continue
        # Homographs: conflicting grammatical kinds are dropped entirely so the
        # runtime falls back to the suffix rules instead of guessing.
        if existing[0] != kind:
            existing[0] = "?"
        elif existing[1] != aspect:
            existing[1] = "B"

    kept = {form: row for form, row in rows.items() if row[0] != "?"}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("wb") as raw:
        with gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0) as compressed:
            with io.TextIOWrapper(compressed, encoding="utf-8", newline="\n") as text:
                for form in sorted(kept):
                    kind, aspect, lemma = kept[form]
                    text.write(f"{form}\t{kind}\t{aspect}\t{lemma}\n")

    dropped = len(rows) - len(kept)
    print(f"Russian verb lexicon: {len(kept):,} forms ({dropped:,} homographs dropped)")


if __name__ == "__main__":
    main()
