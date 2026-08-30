#!/usr/bin/env python3
"""Score exported benchmark segments with a pinned COMET checkpoint.

The translation harness stays dependency-free: this script is the separate,
reproducible neural-metric step described in BENCHMARKING.md. It reads the
`segments.jsonl` produced by `translation-benchmark.py export-metrics` and
writes the `caseId,score` sidecar that `import-metric` binds back to the exact
report, so the metric can never be attached to a different output set.

The checkpoint is not bundled and is never fetched implicitly: pass an already
downloaded local path, or a model id that is present in the local cache.

Example
-------
python tools/run-comet.py \
    --model Unbabel/wmt22-comet-da \
    --input artifacts/Benchmarks/metric-input/wmt25-full/asteria-tg4b-direct-vulkan \
    --output artifacts/Benchmarks/metric-input/wmt25-full/asteria-tg4b-direct-vulkan/comet.csv
"""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
import sys
from pathlib import Path


def load_segments(directory: Path) -> tuple[list[dict], dict]:
    manifest_path = directory / "manifest.json"
    segments_path = directory / "segments.jsonl"
    if not manifest_path.is_file() or not segments_path.is_file():
        raise SystemExit(f"{directory}: manifest.json or segments.jsonl is missing.")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    segments = [
        json.loads(line)
        for line in segments_path.read_text(encoding="utf-8").splitlines()
        if line.strip()
    ]
    if not segments:
        raise SystemExit(f"{directory}: no segments to score.")
    return segments, manifest


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", required=True)
    # Repeatable: loading a 2.3 GB checkpoint dominates the run, so a whole
    # leaderboard is scored by one process and one identical model instance.
    parser.add_argument("--input", required=True, type=Path, action="append")
    parser.add_argument(
        "--output",
        type=Path,
        help="Only valid with a single --input; otherwise <input>/comet.csv.",
    )
    parser.add_argument("--batch-size", type=int, default=8)
    parser.add_argument("--gpus", type=int, default=0)
    arguments = parser.parse_args(argv)

    if arguments.output is not None and len(arguments.input) != 1:
        raise SystemExit("--output is only valid with a single --input.")
    inputs = [(directory, *load_segments(directory)) for directory in arguments.input]

    # Imported lazily so that --help and the argument contract stay usable in a
    # checkout without torch, which is the normal state of this repository.
    from comet import download_model, load_from_checkpoint

    model_path = Path(arguments.model)
    if model_path.is_file():
        checkpoint = str(model_path)
    else:
        checkpoint = download_model(arguments.model)
    model = load_from_checkpoint(checkpoint)
    model.eval()

    checkpoint_path = Path(checkpoint)
    checkpoint_bytes = checkpoint_path.stat().st_size
    checkpoint_sha256 = file_sha256(checkpoint_path)

    for directory, segments, manifest in inputs:
        payload = [
            {"src": item["src"], "mt": item["mt"], "ref": item["ref"]}
            for item in segments
        ]
        prediction = model.predict(
            payload,
            batch_size=arguments.batch_size,
            gpus=arguments.gpus,
            progress_bar=False,
        )

        output = arguments.output or directory / "comet.csv"
        output.parent.mkdir(parents=True, exist_ok=True)
        with output.open("w", encoding="utf-8", newline="") as handle:
            writer = csv.writer(handle)
            writer.writerow(["caseId", "score"])
            for item, score in zip(segments, prediction.scores, strict=True):
                writer.writerow([item["caseId"], f"{score:.6f}"])

        provenance = {
            "kind": "asteria.comet-run",
            "model": arguments.model,
            "checkpoint": str(checkpoint),
            "checkpointBytes": checkpoint_bytes,
            "checkpointSha256": checkpoint_sha256,
            "segments": len(segments),
            # The manifest identity is what import-metric verifies, so the run
            # is bound to one exact (dataset, slice, outputs) triple and cannot
            # be attached to a rerun that produced different translations.
            "runId": manifest.get("runId"),
            "metricInputSha256": manifest.get("metricInputSha256"),
            "outputsSha256": manifest.get("outputsSha256"),
            "systemScore": sum(prediction.scores) / len(prediction.scores),
        }
        output.with_suffix(".provenance.json").write_text(
            json.dumps(provenance, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
            encoding="utf-8",
        )
        print(
            f"{manifest.get('runId')}: {len(segments)} segments, "
            f"mean {provenance['systemScore']:.6f} -> {output}",
            flush=True,
        )

    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
