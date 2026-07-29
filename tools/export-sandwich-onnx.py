#!/usr/bin/env python3
"""Export the official SANDWiCH ensemble to Asteria's pinned ONNX bundle."""

from __future__ import annotations

import argparse
import hashlib
import inspect
import json
import os
import shutil
from pathlib import Path


UPSTREAM_COMMIT = "daca3cad63326204d4027b9ea09fb1405ab1ca0a"
UPSTREAM_ARCHIVE_SHA256 = (
    "98b3cda35a8599c961fc2ac36867d4687bd136e3f0ca1998d4a4754ea8c8e1de"
)
MODEL_NAMES = ("encoder-nv", "encoder-v")
OUTPUT_NAMES = {
    "encoder-nv": "encoder-nv-fp32.onnx",
    "encoder-v": "encoder-v-fp32.onnx",
}


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def export_model(source: Path, destination: Path) -> dict[str, object]:
    import numpy as np
    import onnx
    import onnxruntime as ort
    import torch
    from transformers import AutoModelForSequenceClassification, AutoTokenizer

    tokenizer = AutoTokenizer.from_pretrained(
        source,
        local_files_only=True,
        use_fast=True,
    )
    model = AutoModelForSequenceClassification.from_pretrained(
        source,
        local_files_only=True,
        torch_dtype=torch.float32,
    ).eval()

    class LogitsOnly(torch.nn.Module):
        def __init__(self, wrapped: object) -> None:
            super().__init__()
            self.wrapped = wrapped

        def forward(self, input_ids: object, attention_mask: object) -> object:
            return self.wrapped(
                input_ids=input_ids,
                attention_mask=attention_mask,
            ).logits

    encoded = tokenizer(
        [
            "She is trying to [D] butter [D] me up.",
            "He sat on the [D] bank [D] of the river.",
        ],
        [
            "To praise or flatter someone in order to gain a favor.",
            "The sloping land beside a body of water.",
        ],
        padding=True,
        truncation="longest_first",
        max_length=512,
        return_tensors="pt",
    )
    inputs = (encoded["input_ids"], encoded["attention_mask"])
    temporary = destination.with_suffix(destination.suffix + ".tmp")
    temporary.parent.mkdir(parents=True, exist_ok=True)
    export_options = {
        "input_names": ["input_ids", "attention_mask"],
        "output_names": ["logits"],
        "dynamic_axes": {
            "input_ids": {0: "batch", 1: "sequence"},
            "attention_mask": {0: "batch", 1: "sequence"},
            "logits": {0: "batch"},
        },
        "opset_version": 17,
        "do_constant_folding": True,
    }
    if "dynamo" in inspect.signature(torch.onnx.export).parameters:
        export_options["dynamo"] = False
    try:
        torch.onnx.export(LogitsOnly(model).eval(), inputs, temporary, **export_options)
        graph = onnx.load(temporary)
        onnx.checker.check_model(graph)

        session = ort.InferenceSession(
            str(temporary),
            providers=["CPUExecutionProvider"],
        )
        with torch.no_grad():
            expected = LogitsOnly(model).eval()(*inputs).detach().cpu().numpy()
        actual = session.run(
            ["logits"],
            {
                "input_ids": encoded["input_ids"].cpu().numpy(),
                "attention_mask": encoded["attention_mask"].cpu().numpy(),
            },
        )[0]
        delta = float(np.max(np.abs(expected - actual)))
        if not np.isfinite(delta) or delta > 1e-4:
            raise RuntimeError(f"ONNX parity failed for {source.name}: {delta:.9g}")
        os.replace(temporary, destination)
    finally:
        temporary.unlink(missing_ok=True)

    print(f"{source.name}: ONNX parity max abs delta {delta:.9g}")
    return {
        "file": destination.name,
        "bytes": destination.stat().st_size,
        "sha256": sha256(destination),
        "maximumSequenceLength": 512,
        "outputClassCount": 2,
        "positiveLabelIndex": 1,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--models-directory", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()

    source_root = args.models_directory.resolve()
    output = args.output.resolve()
    for model_name in MODEL_NAMES:
        required = source_root / model_name / "model.safetensors"
        if not required.is_file():
            raise FileNotFoundError(f"Missing official SANDWiCH artifact: {required}")

    output.mkdir(parents=True, exist_ok=True)
    cpu = output / "Cpu"
    model_records = [
        export_model(source_root / name, cpu / OUTPUT_NAMES[name])
        for name in MODEL_NAMES
    ]
    tokenizer_source = source_root / "encoder-nv" / "tokenizer.json"
    tokenizer_destination = output / "tokenizer.json"
    shutil.copy2(tokenizer_source, tokenizer_destination)
    tokenizer_record = {
        "file": tokenizer_destination.name,
        "bytes": tokenizer_destination.stat().st_size,
        "sha256": sha256(tokenizer_destination),
    }
    manifest = {
        "schemaVersion": 1,
        "adapterId": "sandwich-singleton-gloss-v1",
        "upstreamRepository": "https://github.com/danielguzmanolivares/sandwich",
        "upstreamCommit": UPSTREAM_COMMIT,
        "upstreamArchiveSha256": UPSTREAM_ARCHIVE_SHA256,
        "license": "MIT",
        "babelNetDataInstalled": False,
        "aggregation": "dual-softmax-mean-confidence-weighted-group",
        "models": model_records,
        "tokenizer": tokenizer_record,
    }
    manifest_path = output / "model-manifest.json"
    manifest_path.write_text(
        json.dumps(manifest, ensure_ascii=True, indent=2) + "\n",
        encoding="utf-8",
        newline="\n",
    )
    print(json.dumps(manifest, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
