#!/usr/bin/env python3
"""Rebuild the curated phrasal-verb layer's gzip from its readable TSV source.

`assets/Dictionaries/eng-rus-phrasal.tsv` is the hand-edited source of truth: one
`surface<TAB>рус; рус; ...` row per common English phrasal verb, base surface form
only (the runtime canonicalizes inflected occurrences onto the base key). This
script validates it, rewrites it sorted/deduplicated, and emits a deterministic
`eng-rus-phrasal.tsv.gz` (mtime=0) that the app bundles as an ordinary broad layer.

Edit the .tsv, then run:  python scripts/build-phrasal-dictionary.py
"""
from __future__ import annotations

import gzip
import io
import sys
from pathlib import Path

DICTIONARIES = Path(__file__).resolve().parent.parent / "assets" / "Dictionaries"
TSV_PATH = DICTIONARIES / "eng-rus-phrasal.tsv"
GZ_PATH = DICTIONARIES / "eng-rus-phrasal.tsv.gz"


def load_rows(text: str) -> dict[str, str]:
    rows: dict[str, str] = {}
    for number, raw in enumerate(text.splitlines(), start=1):
        if raw == "":
            continue
        parts = raw.split("\t")
        if len(parts) != 2:
            raise SystemExit(f"line {number}: expected exactly one tab: {raw!r}")
        key, value = parts[0].strip(), parts[1].strip()
        if not key or not value:
            raise SystemExit(f"line {number}: empty key or value: {raw!r}")
        if key != key.lower():
            raise SystemExit(f"line {number}: key must be lower case: {key!r}")
        if key in rows:
            raise SystemExit(f"line {number}: duplicate key: {key!r}")
        rows[key] = value
    if not rows:
        raise SystemExit("phrasal source is empty")
    return rows


def main() -> None:
    if not TSV_PATH.exists():
        raise SystemExit(f"missing source: {TSV_PATH}")
    rows = load_rows(TSV_PATH.read_text(encoding="utf-8"))
    body = "".join(f"{key}\t{rows[key]}\n" for key in sorted(rows))

    # Rewrite the source canonically (sorted, LF) so diffs stay minimal.
    TSV_PATH.write_text(body, encoding="utf-8", newline="\n")

    raw = body.encode("utf-8")
    buffer = io.BytesIO()
    with gzip.GzipFile(fileobj=buffer, mode="wb", mtime=0) as gz:
        gz.write(raw)
    GZ_PATH.write_bytes(buffer.getvalue())

    print(f"phrasal verbs: {len(rows)}")
    print(f"{TSV_PATH.name}: {len(raw)} bytes")
    print(f"{GZ_PATH.name}: {GZ_PATH.stat().st_size} bytes")


if __name__ == "__main__":
    sys.exit(main())
