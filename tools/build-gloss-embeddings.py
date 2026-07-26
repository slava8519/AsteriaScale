#!/usr/bin/env python3
"""Precompute bi-encoder embeddings for the smart-dictionary sense sidecar.

Roadmap task #7. Encodes every distinct (key, definition) gloss of
eng-rus-enwiktionary-senses.tsv.gz with sentence-transformers/all-MiniLM-L6-v2
(Apache-2.0, English, 384-dim, mean pooling, L2-normalized) and writes a
random-access index consumed by AsteriaScale.RankerWorker:

  line 1: JSON header (model, revision, dimensions, counts, source hash)
  then per key: [u16 key bytes len][utf8 key][i32 vector count]
                per vector: [u64 FNV-1a hash of normalized gloss]
                            [f32 scale][dim x i8 quantized values]

Vectors are int8-quantized with a per-vector max-abs scale. Gloss text is
normalized exactly like the C# side (lowercase, collapsed whitespace) before
hashing, so runtime lookups survive grouping and dedup.

pip install onnxruntime tokenizers numpy huggingface_hub
"""

from __future__ import annotations

import argparse
import gzip
import hashlib
import json
import shutil
import struct
import time
from collections import defaultdict
from pathlib import Path

import numpy as np
import onnxruntime
from huggingface_hub import hf_hub_download
from tokenizers import Tokenizer

from atomic_output import (
    atomic_output_path,
    atomic_verified_copy,
    ensure_distinct_paths,
)

MODEL_ID = "sentence-transformers/all-MiniLM-L6-v2"
REVISION = "c9745ed1d9f207416be6d2e6f8de32d1f16199bf"
EXPECTED_MODEL_LENGTH = 90_405_214
EXPECTED_MODEL_SHA256 = "6fd5d72fe4589f189f8ebc006442dbb529bb7ce38f8082112682524616046452"
EXPECTED_TOKENIZER_LENGTH = 466_247
EXPECTED_TOKENIZER_SHA256 = "be50c3628f2bf5bb5e3a7f17b1f74611b2561a3a27eeab05e5aa30f411572037"
DIMENSIONS = 384
SEQUENCE_LENGTH = 64
BATCH_SIZE = 64
FNV_OFFSET = 0xCBF29CE484222325
FNV_PRIME = 0x100000001B3
MASK64 = (1 << 64) - 1


def normalize_gloss(value: str) -> str:
    return " ".join(value.split()).lower()


def fnv1a64(value: str) -> int:
    digest = FNV_OFFSET
    for byte in value.encode("utf-8"):
        digest = ((digest ^ byte) * FNV_PRIME) & MASK64
    return digest


def load_glosses(path: Path) -> dict[str, list[str]]:
    glosses: dict[str, list[str]] = defaultdict(list)
    seen: set[tuple[str, str]] = set()
    with gzip.open(path, "rt", encoding="utf-8") as source:
        for line in source:
            fields = line.rstrip("\n").split("\t")
            if len(fields) < 3:
                continue
            key, definition = fields[0], fields[2]
            marker = (key, normalize_gloss(definition))
            if definition.strip() and marker not in seen:
                seen.add(marker)
                glosses[key].append(definition)
    return glosses


def sense_content_sha256(path: Path) -> str:
    """SHA-256 of the sidecar's *contents*, not of its gzip container.

    gzip stores a timestamp and depends on the compressor, so hashing the file
    bytes made the recorded value change whenever the sidecar was recompressed
    from identical glosses. The runtime pins this value, so an unstable digest
    made the documented regeneration produce an index it would then reject.
    """
    digest = hashlib.sha256()
    with gzip.open(path, "rb") as source:
        for block in iter(lambda: source.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def verify_pinned_file(
    source: Path,
    expected_length: int,
    expected_sha256: str,
) -> str:
    source_sha256 = file_sha256(source)
    if source.stat().st_size != expected_length or source_sha256 != expected_sha256:
        raise RuntimeError(
            f"Pinned artifact mismatch: {source} "
            f"({source.stat().st_size} bytes, SHA-256 {source_sha256})"
        )

    return source_sha256


def stage_pinned_file(
    source: Path,
    destination: Path,
    expected_length: int,
    expected_sha256: str,
) -> str:
    source_sha256 = verify_pinned_file(
        source,
        expected_length,
        expected_sha256,
    )

    def verify(path: Path) -> None:
        if path.stat().st_size != expected_length:
            raise RuntimeError(f"Staged artifact has the wrong length: {path}")
        if file_sha256(path) != expected_sha256:
            raise RuntimeError(f"Staged artifact has the wrong SHA-256: {path}")

    atomic_verified_copy(source, destination, verify)
    staged_sha256 = file_sha256(destination)
    return staged_sha256


def encode_batch(
    session: onnxruntime.InferenceSession,
    tokenizer: Tokenizer,
    texts: list[str],
) -> np.ndarray:
    encodings = [tokenizer.encode(text) for text in texts]
    ids = np.zeros((len(texts), SEQUENCE_LENGTH), dtype=np.int64)
    mask = np.zeros_like(ids)
    for row, encoding in enumerate(encodings):
        tokens = encoding.ids[:SEQUENCE_LENGTH]
        ids[row, : len(tokens)] = tokens
        mask[row, : len(tokens)] = 1
    outputs = session.run(
        None,
        {
            "input_ids": ids,
            "attention_mask": mask,
            "token_type_ids": np.zeros_like(ids),
        },
    )
    hidden = outputs[0]  # [batch, seq, dim]
    weights = mask[:, :, None].astype(np.float32)
    pooled = (hidden * weights).sum(axis=1) / np.clip(weights.sum(axis=1), 1e-9, None)
    norms = np.linalg.norm(pooled, axis=1, keepdims=True)
    return pooled / np.clip(norms, 1e-9, None)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--senses", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument(
        "--bundle-dir",
        type=Path,
        help=(
            "stage the complete runtime bundle here; defaults to the output "
            "directory"
        ),
    )
    args = parser.parse_args()

    bundle_dir = args.bundle_dir or args.output.parent
    staged_model = bundle_dir / "minilm-l6-v2.onnx"
    staged_tokenizer = bundle_dir / "tokenizer.json"
    staged_index = bundle_dir / "eng-senses.vec"
    try:
        ensure_distinct_paths(
            {
                "sense source": args.senses,
                "requested output": args.output,
                "bundle model": staged_model,
                "bundle tokenizer": staged_tokenizer,
                "bundle index": staged_index,
            },
            allowed_aliases=[
                frozenset(("requested output", "bundle index")),
            ],
        )
    except ValueError as error:
        parser.error(str(error))

    model_path = Path(
        hf_hub_download(MODEL_ID, "onnx/model.onnx", revision=REVISION)
    )
    tokenizer_path = Path(
        hf_hub_download(MODEL_ID, "tokenizer.json", revision=REVISION)
    )
    try:
        ensure_distinct_paths(
            {
                "sense source": args.senses,
                "requested output": args.output,
                "bundle model": staged_model,
                "bundle tokenizer": staged_tokenizer,
                "bundle index": staged_index,
                "downloaded model": model_path,
                "downloaded tokenizer": tokenizer_path,
            },
            allowed_aliases=[
                frozenset(("requested output", "bundle index")),
                frozenset(("bundle model", "downloaded model")),
                frozenset(("bundle tokenizer", "downloaded tokenizer")),
            ],
        )
    except ValueError as error:
        parser.error(str(error))

    model_sha = verify_pinned_file(
        model_path,
        EXPECTED_MODEL_LENGTH,
        EXPECTED_MODEL_SHA256,
    )
    tokenizer_sha = verify_pinned_file(
        tokenizer_path,
        EXPECTED_TOKENIZER_LENGTH,
        EXPECTED_TOKENIZER_SHA256,
    )
    tokenizer = Tokenizer.from_file(str(tokenizer_path))
    session = onnxruntime.InferenceSession(
        str(model_path), providers=["CPUExecutionProvider"]
    )

    glosses = load_glosses(args.senses)
    total = sum(len(values) for values in glosses.values())
    sense_sha = sense_content_sha256(args.senses)
    print(f"keys={len(glosses):,} glosses={total:,}")

    header = {
        # 2: senseFileSha256 is the digest of the decompressed sidecar. Format 1
        # hashed the gzip container, whose bytes change on every recompression
        # even when every gloss is identical, so the value could not be
        # reproduced and the shipped index no longer matches the sidecar on disk.
        "format": 2,
        "model": MODEL_ID,
        "revision": REVISION,
        "dimensions": DIMENSIONS,
        "normalized": True,
        "sequenceLength": SEQUENCE_LENGTH,
        "senseFileSha256": sense_sha.upper(),
        "keys": len(glosses),
        "vectors": total,
    }

    started = time.perf_counter()
    encoded = 0
    with atomic_output_path(args.output) as pending_output:
        with pending_output.open("wb") as output:
            output.write((json.dumps(header) + "\n").encode("utf-8"))
            pending_texts: list[str] = []
            pending_slots: list[tuple[str, int]] = []
            vectors: dict[str, list[np.ndarray | None]] = {
                key: [None] * len(values) for key, values in glosses.items()
            }

            def flush() -> None:
                nonlocal encoded
                if not pending_texts:
                    return
                matrix = encode_batch(session, tokenizer, pending_texts)
                for (slot_key, slot_index), vector in zip(pending_slots, matrix):
                    vectors[slot_key][slot_index] = vector
                encoded += len(pending_texts)
                pending_texts.clear()
                pending_slots.clear()
                if encoded % 12800 < BATCH_SIZE:
                    rate = encoded / max(time.perf_counter() - started, 1e-9)
                    print(
                        f"encoded {encoded:,}/{total:,} ({rate:,.0f}/s)",
                        flush=True,
                    )

            for key, definitions in glosses.items():
                for index, definition in enumerate(definitions):
                    # Mirror the cross-encoder gloss format "target: definition".
                    pending_texts.append(f"{key}: {definition}")
                    pending_slots.append((key, index))
                    if len(pending_texts) >= BATCH_SIZE:
                        flush()
            flush()

            for key, definitions in glosses.items():
                key_bytes = key.encode("utf-8")
                output.write(struct.pack("<H", len(key_bytes)))
                output.write(key_bytes)
                output.write(struct.pack("<i", len(definitions)))
                for definition, vector in zip(definitions, vectors[key]):
                    assert vector is not None
                    scale = float(np.max(np.abs(vector))) or 1.0
                    quantized = np.clip(
                        np.round(vector / scale * 127.0), -127, 127
                    ).astype(np.int8)
                    output.write(
                        struct.pack("<Q", fnv1a64(normalize_gloss(definition)))
                    )
                    output.write(struct.pack("<f", scale / 127.0))
                    output.write(quantized.tobytes())

        size = pending_output.stat().st_size
        index_sha = file_sha256(pending_output)

        # Keep the live bundle untouched until the expensive index generation
        # and digest both succeed. Each pinned dependency is copied through a
        # sibling temporary file; the index is committed last by the outer
        # atomic_output_path context.
        stage_pinned_file(
            model_path,
            staged_model,
            EXPECTED_MODEL_LENGTH,
            EXPECTED_MODEL_SHA256,
        )
        stage_pinned_file(
            tokenizer_path,
            staged_tokenizer,
            EXPECTED_TOKENIZER_LENGTH,
            EXPECTED_TOKENIZER_SHA256,
        )

    elapsed = time.perf_counter() - started
    if args.output.resolve() != staged_index.resolve():
        with atomic_output_path(staged_index) as temporary_index:
            shutil.copyfile(args.output, temporary_index)
            if file_sha256(temporary_index) != index_sha:
                raise RuntimeError(
                    f"Staged index has the wrong SHA-256: {temporary_index}"
                )
        if file_sha256(staged_index) != index_sha:
            raise RuntimeError(f"Staged index has the wrong SHA-256: {staged_index}")
    print(f"done: {total:,} vectors, {size / 1024 / 1024:.1f} MB, {elapsed:,.0f}s")
    print(f"bundle: {bundle_dir.resolve()}")

    # The runtime pins every one of these. Printing them is the difference
    # between a documented regeneration and one whose output the worker rejects
    # with embedding_metadata_mismatch. Paste into RankerEngine.cs and update the
    # table in assets/Ranker/README.md.
    print()
    print("RankerEngine.cs constants for this bundle:")
    print(f"  EmbeddingIndexFormat      = 2;")
    print(f"  EmbeddingIndexLength      = {size:_};")
    print(f'  EmbeddingIndexSha256      = "{index_sha}";')
    print(f"  EmbeddingModelLength      = {EXPECTED_MODEL_LENGTH:_};")
    print(f'  EmbeddingModelSha256      = "{model_sha}";')
    print(f"  EmbeddingTokenizerLength  = {EXPECTED_TOKENIZER_LENGTH:_};")
    print(f'  EmbeddingTokenizerSha256  = "{tokenizer_sha}";')
    print(f'  EmbeddingSenseFileSha256  = "{sense_sha.upper()}";')
    print(f"  EmbeddingKeyCount         = {len(glosses):_};")
    print(f"  EmbeddingVectorCount      = {total:_};")
    print(
        '  EmbeddingBundleCacheTag   = '
        f'"bi-{REVISION[:8]}-{index_sha[:8]}-{model_sha[:8]}-'
        f'{tokenizer_sha[:8]}";'
    )


if __name__ == "__main__":
    main()
