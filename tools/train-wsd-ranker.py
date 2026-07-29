#!/usr/bin/env python3
"""Fine-tune and export a binary definition-aware WSD cross-encoder.

The input is the grouped JSONL emitted by ``build-wsd-training-set.py``.  One
line is one disambiguation group and contains the exact pair shape used by the
runtime:

    text_a = context with the target wrapped in double quotes
    text_b = "<target>: <English definition>"

The script deliberately keeps training outside the application build.  It
produces a self-describing bundle only after a dynamic ONNX export passes
PyTorch/ONNX Runtime parity.  Production still has to pin the resulting files
and pass the project ranker benchmarks before it may consume the bundle.

Frozen ``ranker-holdout*.json`` files are rejected before they are opened.
They are not training, validation, threshold-selection, or smoke-test inputs.
"""

from __future__ import annotations

import argparse
import dataclasses
import datetime as dt
import gc
import hashlib
import importlib.metadata
import inspect
import json
import math
import os
import platform
import random
import re
import shutil
import struct
import sys
import tempfile
import unicodedata
from collections import defaultdict
from pathlib import Path
from typing import Any, Iterable, Sequence


DEFAULT_MODEL = "cross-encoder/nli-deberta-v3-xsmall"
DEFAULT_SEED = 20260727
MANIFEST_SCHEMA_VERSION = 2
TRAINER_STATE_SCHEMA_VERSION = 2
TRAINING_ALGORITHM_VERSION = "group-aware-v2.3-group-report"
CHECKPOINT_PATTERN = re.compile(r"checkpoint-step-(0|[1-9][0-9]*)\Z")
HOLDOUT_PATTERN = re.compile(r"ranker-holdout", re.IGNORECASE)
DETERMINISTIC_CUBLAS_WORKSPACE_CONFIG = ":4096:8"


@dataclasses.dataclass(frozen=True)
class PairExample:
    group_id: str
    lemma_key: str
    text_a: str
    text_b: str
    label: int
    group_size: int


@dataclasses.dataclass(frozen=True)
class GroupExample:
    group_id: str
    lemma_key: str
    split: str
    pairs: tuple[PairExample, ...]


@dataclasses.dataclass
class DatasetInventory:
    train: list[PairExample]
    valid: list[PairExample]
    train_groups: list[GroupExample]
    valid_groups: list[GroupExample]
    groups_by_split: dict[str, int]
    pairs_by_split: dict[str, int]
    labels_by_split: dict[str, dict[int, int]]
    selected_group_ids_sha256: dict[str, str]


@dataclasses.dataclass
class TrainingCursor:
    epoch: int = 0
    batch_in_epoch: int = 0
    global_step: int = 0
    best_group_accuracy: float = -1.0
    best_loss: float = math.inf
    best_step: int = -1
    best_provenance_sha256: str | None = None


class PairDataset:
    """A pickle-safe map dataset; DataLoader does not require a base class."""

    def __init__(self, examples: Sequence[PairExample]) -> None:
        self.examples = list(examples)

    def __len__(self) -> int:
        return len(self.examples)

    def __getitem__(self, index: int) -> PairExample:
        return self.examples[index]


class GroupDataset:
    """Map dataset whose items preserve complete disambiguation groups."""

    def __init__(self, groups: Sequence[GroupExample]) -> None:
        self.groups = list(groups)

    def __len__(self) -> int:
        return len(self.groups)

    def __getitem__(self, index: int) -> GroupExample:
        return self.groups[index]


@dataclasses.dataclass
class PairCollator:
    """Dynamic pair padding, kept top-level for Windows worker processes."""

    tokenizer: Any
    max_length: int
    pad_to_multiple_of: int | None
    group_weighting: str

    def __call__(self, examples: Sequence[PairExample]) -> dict[str, Any]:
        import torch

        encoded = self.tokenizer(
            [example.text_a for example in examples],
            [example.text_b for example in examples],
            padding=True,
            truncation="longest_first",
            max_length=self.max_length,
            pad_to_multiple_of=self.pad_to_multiple_of,
            return_tensors="pt",
        )
        encoded["labels"] = torch.tensor(
            [example.label for example in examples], dtype=torch.long
        )
        encoded["sample_weights"] = torch.tensor(
            [
                1.0 / example.group_size
                if self.group_weighting == "equal"
                else 1.0
                for example in examples
            ],
            dtype=torch.float32,
        )
        encoded["group_ids"] = [example.group_id for example in examples]
        return encoded


@dataclasses.dataclass
class GroupCollator:
    """Flatten whole groups only after the DataLoader has batched them."""

    tokenizer: Any
    max_length: int
    pad_to_multiple_of: int | None
    group_weighting: str

    def __call__(self, groups: Sequence[GroupExample]) -> dict[str, Any]:
        flattened = [pair for group in groups for pair in group.pairs]
        batch = PairCollator(
            tokenizer=self.tokenizer,
            max_length=self.max_length,
            pad_to_multiple_of=self.pad_to_multiple_of,
            group_weighting=self.group_weighting,
        )(flattened)
        batch["group_lengths"] = [len(group.pairs) for group in groups]
        batch["batched_group_ids"] = [group.group_id for group in groups]
        return batch


def _sha256_file(path: Path, chunk_size: int = 1024 * 1024) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        while chunk := handle.read(chunk_size):
            digest.update(chunk)
    return digest.hexdigest()


def _sha256_json(value: Any) -> str:
    encoded = json.dumps(
        value,
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
    ).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest()


def _utc_now() -> str:
    return dt.datetime.now(dt.timezone.utc).isoformat().replace("+00:00", "Z")


def _reject_holdout_path(path: Path) -> Path:
    expanded = path.expanduser()
    resolved = expanded.resolve()
    if any(
        HOLDOUT_PATTERN.search(part)
        for candidate in (expanded.absolute(), resolved)
        for part in candidate.parts
    ):
        raise SystemExit(
            "Refusing to consume a frozen ranker-holdout input. "
            "Use the shipped silver training JSONL and the public dev/register "
            "benchmarks instead."
        )
    return resolved


def _normalize_semantic_text(value: str) -> str:
    normalized = unicodedata.normalize("NFC", value).replace("’", "'")
    return " ".join(normalized.split()).casefold()


def _semantic_group_id(
    lemma: str,
    surface: str,
    text_a: str,
    candidates: Sequence[tuple[str, int]] = (),
) -> str:
    """Identify the linguistic question, independently of file order."""

    return _sha256_json(
        {
            "lemma": _normalize_semantic_text(lemma),
            "surface": _normalize_semantic_text(surface),
            "textA": _normalize_semantic_text(text_a),
            "candidates": sorted(
                (
                    {
                        "textB": _normalize_semantic_text(text_b),
                        "label": int(label),
                    }
                    for text_b, label in candidates
                ),
                key=lambda item: (item["textB"], item["label"]),
            ),
        }
    )


def _selected_group_digest(group_ids: Iterable[str]) -> str:
    digest = hashlib.sha256()
    for group_id in sorted(group_ids):
        digest.update(group_id.encode("utf-8"))
        digest.update(b"\n")
    return digest.hexdigest()


def _select_groups(
    groups: list[GroupExample],
    limit: int | None,
    seed: int,
    split: str,
) -> list[GroupExample]:
    if limit is None or len(groups) <= limit:
        return groups
    ranked = sorted(
        groups,
        key=lambda item: hashlib.sha256(
            f"{seed}\0{split}\0{item.group_id}".encode("utf-8")
        ).digest(),
    )
    selected = {item.group_id for item in ranked[:limit]}
    return [item for item in groups if item.group_id in selected]


def load_grouped_jsonl(
    path: Path,
    pair_variant: str,
    seed: int,
    max_train_groups: int | None,
    max_valid_groups: int | None,
) -> DatasetInventory:
    """Read the JSONL in one streaming pass while preserving group boundaries."""

    path = _reject_holdout_path(path)
    groups: dict[str, list[GroupExample]] = {
        "train": [],
        "valid": [],
    }
    seen_group_ids: dict[str, int] = {}
    lemma_splits: dict[str, tuple[str, int]] = {}
    with path.open("r", encoding="utf-8") as handle:
        for line_number, raw_line in enumerate(handle, start=1):
            if not raw_line.strip():
                continue
            try:
                record = json.loads(raw_line)
            except json.JSONDecodeError as error:
                raise ValueError(f"{path}:{line_number}: invalid JSON: {error}") from error
            if not isinstance(record, dict):
                raise ValueError(f"{path}:{line_number}: each line must be an object")

            text_a = record.get("text_a")
            pairs = record.get("pairs")
            lemma = record.get("lemma")
            surface = record.get("surface")
            if not isinstance(text_a, str) or not text_a.strip():
                raise ValueError(f"{path}:{line_number}: missing non-empty text_a")
            if not isinstance(lemma, str) or not lemma.strip():
                raise ValueError(f"{path}:{line_number}: missing non-empty lemma")
            if not isinstance(surface, str) or not surface.strip():
                raise ValueError(f"{path}:{line_number}: missing non-empty surface")
            if not isinstance(pairs, list) or len(pairs) < 2:
                raise ValueError(f"{path}:{line_number}: a group needs at least two pairs")

            split = record.get("split")
            if split not in {"train", "valid"}:
                raise ValueError(
                    f"{path}:{line_number}: split must be exactly 'train' or 'valid'"
                )
            lemma_key = _normalize_semantic_text(lemma)
            previous_split = lemma_splits.get(lemma_key)
            if previous_split is not None and previous_split[0] != split:
                raise ValueError(
                    f"{path}:{line_number}: lemma {lemma!r} leaks across "
                    f"{previous_split[0]}/{split} splits (first seen at line "
                    f"{previous_split[1]})"
                )
            lemma_splits.setdefault(lemma_key, (split, line_number))

            pending_pairs: list[tuple[str, int]] = []
            positive_count = 0
            seen_glosses: dict[str, int] = {}
            for pair_index, pair in enumerate(pairs):
                if not isinstance(pair, dict):
                    raise ValueError(
                        f"{path}:{line_number}: pair {pair_index} must be an object"
                    )
                label = pair.get("label")
                if label not in {0, 1}:
                    raise ValueError(
                        f"{path}:{line_number}: pair {pair_index} has non-binary label"
                    )
                field = "text_b_enriched" if pair_variant == "enriched" else "text_b"
                text_b = pair.get(field)
                if pair_variant == "enriched" and not isinstance(text_b, str):
                    text_b = pair.get("text_b")
                if not isinstance(text_b, str) or not text_b.strip():
                    raise ValueError(
                        f"{path}:{line_number}: pair {pair_index} has no {field}"
                    )
                normalized_gloss = _normalize_semantic_text(text_b)
                if normalized_gloss in seen_glosses:
                    raise ValueError(
                        f"{path}:{line_number}: duplicate candidate gloss at pair "
                        f"{pair_index}; first seen at pair "
                        f"{seen_glosses[normalized_gloss]}"
                    )
                seen_glosses[normalized_gloss] = pair_index
                positive_count += int(label)
                pending_pairs.append((text_b, int(label)))
            if positive_count != 1:
                raise ValueError(
                    f"{path}:{line_number}: expected exactly one positive pair, "
                    f"found {positive_count}"
                )
            group_id = _semantic_group_id(
                lemma, surface, text_a, pending_pairs
            )
            if group_id in seen_group_ids:
                raise ValueError(
                    f"{path}:{line_number}: duplicate semantic group; first seen "
                    f"at line {seen_group_ids[group_id]}"
                )
            seen_group_ids[group_id] = line_number
            examples = [
                PairExample(
                    group_id=group_id,
                    lemma_key=lemma_key,
                    text_a=text_a,
                    text_b=text_b,
                    label=label,
                    group_size=len(pending_pairs),
                )
                for text_b, label in pending_pairs
            ]
            groups[split].append(
                GroupExample(
                    group_id=group_id,
                    lemma_key=lemma_key,
                    split=split,
                    pairs=tuple(examples),
                )
            )

    groups["train"] = _select_groups(
        groups["train"], max_train_groups, seed, "train"
    )
    groups["valid"] = _select_groups(
        groups["valid"], max_valid_groups, seed, "valid"
    )
    if not groups["train"]:
        raise ValueError(f"{path}: no training groups")
    if not groups["valid"]:
        raise ValueError(f"{path}: no validation groups with split='valid'")

    flattened = {
        split: [example for group in groups[split] for example in group.pairs]
        for split in ("train", "valid")
    }
    labels: dict[str, dict[int, int]] = {}
    for split, examples in flattened.items():
        labels[split] = {
            0: sum(example.label == 0 for example in examples),
            1: sum(example.label == 1 for example in examples),
        }

    return DatasetInventory(
        train=flattened["train"],
        valid=flattened["valid"],
        train_groups=groups["train"],
        valid_groups=groups["valid"],
        groups_by_split={split: len(groups[split]) for split in groups},
        pairs_by_split={split: len(flattened[split]) for split in flattened},
        labels_by_split=labels,
        selected_group_ids_sha256={
            split: _selected_group_digest(item.group_id for item in groups[split])
            for split in groups
        },
    )


def _load_ml_stack() -> dict[str, Any]:
    try:
        import numpy
        import onnx
        import onnxruntime
        import torch
        import transformers
        from huggingface_hub import HfApi
        from torch.utils.data import DataLoader, Subset
        from transformers import (
            AutoConfig,
            AutoModelForSequenceClassification,
            AutoTokenizer,
            get_linear_schedule_with_warmup,
        )
    except ImportError as error:
        raise SystemExit(
            "Missing WSD training dependencies. Install them with:\n"
            "  python -m pip install -r tools/requirements-wsd-ranker.txt\n"
            f"Original import error: {error}"
        ) from error
    return {
        "numpy": numpy,
        "onnx": onnx,
        "onnxruntime": onnxruntime,
        "torch": torch,
        "transformers": transformers,
        "HfApi": HfApi,
        "DataLoader": DataLoader,
        "Subset": Subset,
        "AutoConfig": AutoConfig,
        "AutoModelForSequenceClassification": AutoModelForSequenceClassification,
        "AutoTokenizer": AutoTokenizer,
        "get_linear_schedule_with_warmup": get_linear_schedule_with_warmup,
    }


def _set_determinism(torch: Any, numpy: Any, seed: int, threads: int) -> None:
    random.seed(seed)
    numpy.random.seed(seed)
    torch.manual_seed(seed)
    if torch.cuda.is_available():
        torch.cuda.manual_seed_all(seed)
    torch.set_num_threads(threads)
    try:
        torch.set_num_interop_threads(max(1, min(threads, 4)))
    except RuntimeError:
        # PyTorch permits setting inter-op threads only before parallel work starts.
        pass
    torch.use_deterministic_algorithms(True)
    if hasattr(torch.backends, "cudnn"):
        torch.backends.cudnn.benchmark = False
        torch.backends.cudnn.deterministic = True


def _validate_deterministic_environment(device: str, seed: int) -> None:
    actual_hash_seed = os.environ.get("PYTHONHASHSEED")
    if actual_hash_seed != str(seed):
        raise SystemExit(
            f"Deterministic training requires PYTHONHASHSEED={seed} before "
            f"Python starts; received {actual_hash_seed!r}."
        )
    if device != "cuda":
        return
    actual_workspace = os.environ.get("CUBLAS_WORKSPACE_CONFIG")
    if actual_workspace != DETERMINISTIC_CUBLAS_WORKSPACE_CONFIG:
        raise SystemExit(
            "Strict CUDA determinism requires CUBLAS_WORKSPACE_CONFIG="
            f"{DETERMINISTIC_CUBLAS_WORKSPACE_CONFIG}; received "
            f"{actual_workspace!r}. "
            "Set it before Python starts so cuBLAS cannot initialize with a "
            "different workspace policy."
        )


def _resolve_training_device(torch: Any, device: str, precision: str) -> Any:
    if device == "cpu":
        if precision != "fp32":
            raise SystemExit("--precision=bf16 is supported only with --device=cuda")
        return torch.device("cpu")
    if not torch.cuda.is_available():
        raise SystemExit("--device=cuda requested, but PyTorch reports no CUDA device")
    if precision == "bf16":
        checker = getattr(torch.cuda, "is_bf16_supported", None)
        if checker is None or not checker():
            raise SystemExit(
                "--precision=bf16 requested, but the CUDA device/runtime does not "
                "report native bfloat16 support"
            )
    return torch.device("cuda")


def _autocast_context(torch: Any, device: Any, precision: str) -> Any:
    if device.type == "cuda" and precision == "bf16":
        return torch.autocast(device_type="cuda", dtype=torch.bfloat16)
    return torch.autocast(device_type=device.type, enabled=False)


def _move_batch_to_device(batch: dict[str, Any], device: Any, torch: Any) -> dict[str, Any]:
    return {
        key: value.to(device, non_blocking=False)
        if isinstance(value, torch.Tensor)
        else value
        for key, value in batch.items()
    }


def _optimizer_to_device(optimizer: Any, device: Any, torch: Any) -> None:
    for state in optimizer.state.values():
        for key, value in state.items():
            if isinstance(value, torch.Tensor):
                state[key] = value.to(device)


def _locate_output_linear(
    model: Any, torch: Any, expected_output_count: int
) -> tuple[str, Any]:
    preferred: list[tuple[str, Any]] = []
    for name in ("classifier", "score", "classification_head"):
        module = getattr(model, name, None)
        if isinstance(module, torch.nn.Linear):
            preferred.append((name, module))
        out_proj = getattr(module, "out_proj", None)
        if isinstance(out_proj, torch.nn.Linear):
            preferred.append((f"{name}.out_proj", out_proj))
    for name, module in preferred:
        if module.out_features == expected_output_count:
            return name, module

    linear_candidates = [
        (name, module)
        for name, module in model.named_modules()
        if isinstance(module, torch.nn.Linear)
        and module.out_features == expected_output_count
    ]
    if not linear_candidates:
        raise RuntimeError(
            f"Cannot locate the {expected_output_count}-class output layer"
        )
    return linear_candidates[-1]


def _replace_named_module(model: Any, name: str, replacement: Any) -> None:
    parent = model
    parts = name.split(".")
    for part in parts[:-1]:
        parent = parent[int(part)] if part.isdigit() else getattr(parent, part)
    leaf = parts[-1]
    if leaf.isdigit():
        parent[int(leaf)] = replacement
    else:
        setattr(parent, leaf, replacement)


def _reset_binary_head(model: Any, torch: Any) -> str:
    """Reset only the task output layer, retaining the pretrained encoder."""

    name, module = _locate_output_linear(model, torch, 2)
    module.reset_parameters()
    return name


def _canonical_label_map(config: Any) -> dict[int, str]:
    raw = getattr(config, "id2label", None) or {}
    result: dict[int, str] = {}
    for key, value in raw.items():
        try:
            index = int(key)
        except (TypeError, ValueError) as error:
            raise ValueError(f"Invalid source label index: {key!r}") from error
        result[index] = str(value)
    expected = int(getattr(config, "num_labels", len(result)))
    if set(result) != set(range(expected)):
        raise ValueError(
            "The source NLI config must provide an id2label entry for every class"
        )
    return result


def _entailment_label_index(labels: dict[int, str]) -> int:
    matches = [
        index
        for index, label in labels.items()
        if _normalize_semantic_text(label) in {"entailment", "entails", "entail"}
    ]
    if len(matches) != 1:
        raise ValueError(
            "--head-initialization=nli-projected requires exactly one source "
            f"entailment label; got {labels}"
        )
    return matches[0]


def _project_nli_rows(
    weight: Any, bias: Any | None, entailment_index: int, torch: Any
) -> tuple[Any, Any]:
    if weight.ndim != 2 or weight.shape[0] < 3:
        raise ValueError("NLI projection requires a classifier with at least 3 rows")
    other_indices = [index for index in range(weight.shape[0]) if index != entailment_index]
    projected_weight = torch.stack(
        [weight[other_indices].mean(dim=0), weight[entailment_index]], dim=0
    )
    source_bias = (
        bias
        if bias is not None
        else torch.zeros(weight.shape[0], dtype=weight.dtype, device=weight.device)
    )
    projected_bias = torch.stack(
        [
            source_bias[other_indices].mean(dim=0)
            + math.log(len(other_indices)),
            source_bias[entailment_index],
        ],
        dim=0,
    )
    return projected_weight, projected_bias


def _project_nli_head(model: Any, torch: Any) -> dict[str, Any]:
    labels = _canonical_label_map(model.config)
    entailment_index = _entailment_label_index(labels)
    head_name, source_head = _locate_output_linear(model, torch, len(labels))
    projected_weight, projected_bias = _project_nli_rows(
        source_head.weight.detach(),
        source_head.bias.detach() if source_head.bias is not None else None,
        entailment_index,
        torch,
    )
    replacement = torch.nn.Linear(
        source_head.in_features,
        2,
        bias=True,
    ).to(device=source_head.weight.device, dtype=source_head.weight.dtype)
    with torch.no_grad():
        replacement.weight.copy_(projected_weight)
        replacement.bias.copy_(projected_bias)
    _replace_named_module(model, head_name, replacement)
    model.config.num_labels = 2
    model.config.id2label = {0: "NEGATIVE", 1: "POSITIVE"}
    model.config.label2id = {"NEGATIVE": 0, "POSITIVE": 1}
    model.config.problem_type = "single_label_classification"
    if hasattr(model, "num_labels"):
        model.num_labels = 2
    return {
        "mode": "nli-projected",
        "head": head_name,
        "sourceLabels": {str(index): label for index, label in labels.items()},
        "entailmentLabelIndex": entailment_index,
        "negativeProjection": (
            "mean-of-non-entailment-rows-with-log-class-count-bias"
        ),
    }


def _truncate_config_encoder(config: Any, requested_layers: int) -> dict[str, int]:
    original = int(getattr(config, "num_hidden_layers", 0))
    if original <= 0:
        raise RuntimeError("The selected model config has no num_hidden_layers")
    if requested_layers:
        if requested_layers > original:
            raise ValueError(
                f"--truncate-encoder-layers={requested_layers} exceeds the "
                f"source model's {original} layers"
            )
        config.num_hidden_layers = requested_layers
    return {
        "sourceEncoderLayers": original,
        "loadedEncoderLayers": int(config.num_hidden_layers),
    }


def _freeze_encoder(model: Any, freeze_embeddings: bool, layer_count: int) -> dict[str, int]:
    base_model = getattr(model, getattr(model, "base_model_prefix", ""), None)
    if base_model is None:
        base_model = getattr(model, "base_model", None)
    if base_model is None:
        raise RuntimeError("Cannot locate the pretrained encoder for freezing")

    frozen = 0
    total = sum(parameter.numel() for parameter in model.parameters())
    if freeze_embeddings:
        embeddings = getattr(base_model, "embeddings", None)
        if embeddings is None:
            raise RuntimeError("The selected encoder has no discoverable embeddings module")
        for parameter in embeddings.parameters():
            parameter.requires_grad = False
            frozen += parameter.numel()

    if layer_count:
        encoder = getattr(base_model, "encoder", None)
        layers = getattr(encoder, "layer", None)
        if layers is None:
            layers = getattr(encoder, "layers", None)
        if layers is None:
            raise RuntimeError("The selected encoder has no discoverable layer sequence")
        if layer_count > len(layers):
            raise ValueError(
                f"--freeze-encoder-layers={layer_count} exceeds the model's "
                f"{len(layers)} layers"
            )
        for layer in list(layers)[:layer_count]:
            for parameter in layer.parameters():
                if parameter.requires_grad:
                    parameter.requires_grad = False
                    frozen += parameter.numel()

    return {
        "totalParameters": total,
        "frozenParameters": frozen,
        "trainableParameters": total - frozen,
    }


def _resolved_positive_weight(
    value: str,
    examples: Sequence[PairExample],
    group_weighting: str,
) -> float:
    if value == "none":
        return 1.0
    if value != "auto":
        parsed = float(value)
        if not math.isfinite(parsed) or parsed <= 0:
            raise ValueError("--positive-class-weight must be auto, none, or > 0")
        return parsed

    weighted = {0: 0.0, 1: 0.0}
    for example in examples:
        sample_weight = 1.0 / example.group_size if group_weighting == "equal" else 1.0
        weighted[example.label] += sample_weight
    if weighted[1] == 0:
        raise ValueError("training data has no positive pairs")
    return weighted[0] / weighted[1]


def _make_collator(
    tokenizer: Any,
    torch: Any,
    max_length: int,
    pad_to_multiple_of: int | None,
    group_weighting: str,
):
    del torch  # PairCollator imports torch inside worker processes when needed.
    return PairCollator(
        tokenizer=tokenizer,
        max_length=max_length,
        pad_to_multiple_of=pad_to_multiple_of,
        group_weighting=group_weighting,
    )


def _make_group_collator(
    tokenizer: Any,
    max_length: int,
    pad_to_multiple_of: int | None,
    group_weighting: str,
) -> GroupCollator:
    return GroupCollator(
        tokenizer=tokenizer,
        max_length=max_length,
        pad_to_multiple_of=pad_to_multiple_of,
        group_weighting=group_weighting,
    )


def _loader_generator(torch: Any, seed: int, role: str, epoch: int = 0) -> Any:
    digest = hashlib.sha256(f"{seed}\0{role}\0{epoch}".encode("utf-8")).digest()
    generator = torch.Generator()
    generator.manual_seed(int.from_bytes(digest[:8], "big") % ((1 << 63) - 1))
    return generator


def _model_inputs(batch: dict[str, Any]) -> dict[str, Any]:
    return {
        key: value
        for key, value in batch.items()
        if key
        not in {
            "labels",
            "sample_weights",
            "group_ids",
            "group_lengths",
            "batched_group_ids",
        }
    }


def _weighted_loss_parts(
    logits: Any,
    labels: Any,
    sample_weights: Any,
    torch: Any,
    positive_weight: float,
) -> tuple[Any, Any]:
    class_weights = torch.tensor(
        [1.0, positive_weight], dtype=logits.dtype, device=logits.device
    )
    losses = torch.nn.functional.cross_entropy(
        logits,
        labels,
        reduction="none",
    )
    effective_weights = sample_weights.to(logits.dtype) * class_weights[labels]
    numerator = (losses * effective_weights).sum()
    denominator = effective_weights.sum().clamp_min(1e-12)
    return numerator, denominator


def _hybrid_loss_parts(
    logits: Any,
    labels: Any,
    group_lengths: Sequence[int],
    torch: Any,
    positive_weight: float,
    pairwise_coefficient: float,
) -> tuple[Any, Any]:
    """Return a sum over equally weighted groups and its group denominator."""

    offset = 0
    group_losses: list[Any] = []
    class_weights = torch.tensor(
        [1.0, positive_weight], dtype=logits.dtype, device=logits.device
    )
    for length in group_lengths:
        end = offset + int(length)
        group_logits = logits[offset:end]
        group_labels = labels[offset:end]
        positives = torch.nonzero(group_labels == 1, as_tuple=False).flatten()
        if positives.numel() != 1:
            raise RuntimeError("Every hybrid-loss group must have exactly one positive")
        scores = group_logits[:, 1] - group_logits[:, 0]
        listwise = torch.nn.functional.cross_entropy(
            scores.unsqueeze(0), positives[:1], reduction="sum"
        )
        pair_losses = torch.nn.functional.cross_entropy(
            group_logits, group_labels, reduction="none"
        )
        effective = class_weights[group_labels]
        pairwise = (pair_losses * effective).sum() / effective.sum().clamp_min(1e-12)
        group_losses.append(listwise + pairwise_coefficient * pairwise)
        offset = end
    if offset != labels.shape[0] or not group_losses:
        raise RuntimeError("Hybrid-loss group boundaries do not cover the batch")
    return torch.stack(group_losses).sum(), logits.new_tensor(float(len(group_losses)))


def _loss_parts(
    logits: Any,
    batch: dict[str, Any],
    torch: Any,
    loss_mode: str,
    positive_weight: float,
    pairwise_coefficient: float,
) -> tuple[Any, Any]:
    if not torch.isfinite(logits).all():
        raise RuntimeError("Model produced non-finite logits")
    if loss_mode == "hybrid":
        return _hybrid_loss_parts(
            logits,
            batch["labels"],
            batch["group_lengths"],
            torch,
            positive_weight,
            pairwise_coefficient,
        )
    return _weighted_loss_parts(
        logits,
        batch["labels"],
        batch["sample_weights"],
        torch,
        positive_weight,
    )


def _group_result(scores: Sequence[tuple[float, int]]) -> tuple[bool, bool]:
    if not scores:
        raise RuntimeError("Cannot evaluate an empty group")
    maximum = max(score for score, _ in scores)
    winners = [label for score, label in scores if score == maximum]
    tied = len(winners) != 1
    return (not tied and winners[0] == 1), tied


def _group_outcome(
    group_id: str,
    lemma_key: str,
    scores: Sequence[tuple[float, int]],
) -> dict[str, Any]:
    """Build a stable, text-free record for paired significance tests."""

    if not scores:
        raise RuntimeError("Cannot report an empty group")
    numeric_scores = [float(score) for score, _ in scores]
    labels = [int(label) for _, label in scores]
    if labels.count(1) != 1:
        raise RuntimeError("Every reported group must have exactly one positive")
    maximum = max(numeric_scores)
    winners = [index for index, score in enumerate(numeric_scores) if score == maximum]
    predicted_index = winners[0] if len(winners) == 1 else None
    gold_index = labels.index(1)
    sorted_scores = sorted(numeric_scores, reverse=True)
    margin = (
        sorted_scores[0] - sorted_scores[1]
        if len(sorted_scores) > 1
        else math.inf
    )
    pair_predictions = [1 if score > 0.0 else 0 for score in numeric_scores]
    return {
        "groupId": group_id,
        "lemmaClusterSha256": hashlib.sha256(
            lemma_key.encode("utf-8")
        ).hexdigest(),
        "candidateCount": len(scores),
        "goldIndex": gold_index,
        "predictedIndex": predicted_index,
        "correct": predicted_index == gold_index,
        "tie": predicted_index is None,
        "margin": margin,
        "scores": numeric_scores,
        "labels": labels,
        "pairPredictions": pair_predictions,
        "pairCorrect": [
            predicted == label
            for predicted, label in zip(pair_predictions, labels)
        ],
    }


def evaluate(
    model: Any,
    dataset: Any,
    collator: Any,
    ml: dict[str, Any],
    batch_size: int,
    workers: int,
    positive_weight: float,
    loss_mode: str,
    pairwise_coefficient: float,
    seed: int,
    device: Any,
    precision: str,
    collect_group_outcomes: bool = False,
) -> dict[str, Any]:
    torch = ml["torch"]
    loader = ml["DataLoader"](
        dataset,
        batch_size=batch_size,
        shuffle=False,
        num_workers=workers,
        collate_fn=collator,
        pin_memory=False,
        generator=_loader_generator(torch, seed, "evaluation"),
    )
    model.eval()
    loss_numerator = 0.0
    loss_denominator = 0.0
    correct_pairs = 0
    pair_count = 0
    group_scores: dict[str, list[tuple[float, int]]] = defaultdict(list)
    dataset_groups = getattr(dataset, "groups", None)
    dataset_examples = getattr(dataset, "examples", None)
    if dataset_groups is not None:
        lemma_by_group = {
            group.group_id: group.lemma_key for group in dataset_groups
        }
    elif dataset_examples is not None:
        lemma_by_group = {
            example.group_id: example.lemma_key for example in dataset_examples
        }
    else:
        raise RuntimeError("Evaluation dataset exposes neither groups nor examples")
    with torch.inference_mode():
        for batch in loader:
            batch = _move_batch_to_device(batch, device, torch)
            labels = batch["labels"]
            weights = batch["sample_weights"]
            with _autocast_context(torch, device, precision):
                logits = model(**_model_inputs(batch)).logits
            if tuple(logits.shape) != (labels.shape[0], 2):
                raise RuntimeError(f"Expected logits [batch,2], got {tuple(logits.shape)}")
            numerator, denominator = _loss_parts(
                logits.float(),
                batch,
                torch,
                loss_mode,
                positive_weight,
                pairwise_coefficient,
            )
            if not torch.isfinite(numerator) or not torch.isfinite(denominator):
                raise RuntimeError("Validation loss became non-finite")
            loss_numerator += float(numerator)
            loss_denominator += float(denominator)
            predictions = logits.argmax(dim=-1)
            correct_pairs += int((predictions == labels).sum())
            pair_count += int(labels.shape[0])
            scores = (
                logits[:, 1].float() - logits[:, 0].float()
            ).detach().cpu().tolist()
            for group_id, score, label in zip(
                batch["group_ids"], scores, labels.detach().cpu().tolist()
            ):
                group_scores[group_id].append((float(score), int(label)))

    results = [_group_result(scores) for scores in group_scores.values()]
    correct_groups = sum(correct for correct, _ in results)
    tie_groups = sum(tied for _, tied in results)
    evaluation: dict[str, Any] = {
        "loss": loss_numerator / max(loss_denominator, 1e-12),
        "pairAccuracy": correct_pairs / max(pair_count, 1),
        "groupAccuracy": correct_groups / max(len(group_scores), 1),
        "pairs": pair_count,
        "groups": len(group_scores),
        "tieGroups": tie_groups,
    }
    if collect_group_outcomes:
        evaluation["groupOutcomes"] = [
            _group_outcome(group_id, lemma_by_group[group_id], group_scores[group_id])
            for group_id in sorted(group_scores)
        ]
    return evaluation


def _state_dict_path(checkpoint: Path) -> Path:
    return checkpoint / "training-state.pt"


def _lexical_absolute(path: Path) -> Path:
    return Path(os.path.abspath(os.fspath(path.expanduser())))


def _is_reparse_point(path: Path) -> bool:
    if path.is_symlink():
        return True
    try:
        if path.is_junction():
            return True
    except (AttributeError, OSError):
        pass
    try:
        attributes = int(getattr(path.lstat(), "st_file_attributes", 0))
    except (FileNotFoundError, OSError):
        return False
    return bool(attributes & 0x400)  # FILE_ATTRIBUTE_REPARSE_POINT


def _assert_no_reparse_components(path: Path) -> Path:
    absolute = _lexical_absolute(path)
    current = Path(absolute.anchor)
    for part in absolute.parts[1:]:
        current /= part
        if _is_reparse_point(current):
            raise RuntimeError(f"Refusing a symlink/junction/reparse path: {current}")
    return absolute


def _run_root_sha256(output_dir: Path) -> str:
    lexical = os.path.normcase(os.fspath(_lexical_absolute(output_dir)))
    return _sha256_json({"outputDirectory": lexical})


def _prepare_generated_parent(path: Path) -> Path:
    absolute = _lexical_absolute(path)
    _assert_no_reparse_components(absolute.parent)
    if _is_reparse_point(absolute):
        raise RuntimeError(f"Refusing a reparse output directory: {absolute}")
    absolute.mkdir(parents=False, exist_ok=True)
    return _assert_no_reparse_components(absolute)


def _safe_remove_generated_directory(path: Path, expected_parent: Path) -> None:
    absolute = _lexical_absolute(path)
    parent = _assert_no_reparse_components(expected_parent)
    if absolute.parent != parent or _is_reparse_point(absolute):
        raise RuntimeError(f"Refusing to remove unsafe generated directory: {path}")
    _assert_no_reparse_components(absolute)
    shutil.rmtree(absolute)


def _promote_directory(staging: Path, destination: Path) -> None:
    """Publish a verified directory while retaining a recoverable predecessor."""

    staging = _lexical_absolute(staging)
    destination = _lexical_absolute(destination)
    if staging.parent != destination.parent:
        raise RuntimeError("Staging and destination directories must be siblings")
    _assert_no_reparse_components(staging.parent)
    if not staging.is_dir() or _is_reparse_point(staging):
        raise RuntimeError(f"Unsafe or missing staging directory: {staging}")
    _assert_no_reparse_components(staging)
    if _is_reparse_point(destination):
        raise RuntimeError(f"Refusing to replace a reparse destination: {destination}")
    if destination.exists():
        _assert_no_reparse_components(destination)
    backup = destination.with_name(f".{destination.name}.previous")
    if _is_reparse_point(backup):
        raise RuntimeError(f"Refusing a reparse recovery directory: {backup}")
    if backup.exists() and not destination.exists():
        _assert_no_reparse_components(backup)
        backup.rename(destination)
    if backup.exists():
        raise RuntimeError(
            f"Cannot publish while a previous recovery directory exists: {backup}"
        )
    moved_previous = False
    try:
        if destination.exists():
            destination.rename(backup)
            moved_previous = True
        staging.rename(destination)
    except Exception:
        if moved_previous and not destination.exists() and backup.exists():
            backup.rename(destination)
        raise
    if moved_previous:
        if backup.parent != destination.parent or _is_reparse_point(backup):
            raise RuntimeError(f"Refusing to clean unsafe directory backup: {backup}")
        _assert_no_reparse_components(backup)
        shutil.rmtree(backup)


def _python_random_state() -> dict[str, Any]:
    version, internal, gaussian = random.getstate()
    return {
        "version": int(version),
        "internal": [int(value) for value in internal],
        "gaussian": gaussian,
    }


def _numpy_random_state(numpy: Any) -> dict[str, Any]:
    algorithm, values, position, has_gaussian, cached_gaussian = numpy.random.get_state()
    return {
        "algorithm": str(algorithm),
        "values": [int(value) for value in values.tolist()],
        "position": int(position),
        "hasGaussian": int(has_gaussian),
        "cachedGaussian": float(cached_gaussian),
    }


def _cursor_for_manifest(cursor: TrainingCursor) -> dict[str, Any]:
    return {
        "epoch": cursor.epoch,
        "batchInEpoch": cursor.batch_in_epoch,
        "globalStep": cursor.global_step,
        "bestGroupAccuracy": (
            cursor.best_group_accuracy
            if math.isfinite(cursor.best_group_accuracy)
            else None
        ),
        "bestLoss": cursor.best_loss if math.isfinite(cursor.best_loss) else None,
        "bestStep": cursor.best_step,
        "bestProvenanceSha256": cursor.best_provenance_sha256,
    }


def _checkpoint_manifest_path(checkpoint: Path) -> Path:
    return checkpoint / "checkpoint-manifest.json"


def _save_checkpoint(
    directory: Path,
    model: Any,
    tokenizer: Any,
    optimizer: Any,
    scheduler: Any,
    cursor: TrainingCursor,
    run_signature: str,
    run_root_sha256: str,
    output_dir: Path,
    base_model: dict[str, Any],
    torch: Any,
    numpy: Any,
) -> None:
    directory = _lexical_absolute(directory)
    _prepare_generated_parent(directory.parent)
    if _is_reparse_point(directory):
        raise RuntimeError(f"Refusing a reparse checkpoint destination: {directory}")
    staging = Path(
        tempfile.mkdtemp(
            prefix=f".{directory.name}.", suffix=".tmp", dir=directory.parent
        )
    )
    try:
        model.save_pretrained(staging, safe_serialization=True)
        tokenizer.save_pretrained(staging)
        state = {
            "schemaVersion": TRAINER_STATE_SCHEMA_VERSION,
            "savedUtc": _utc_now(),
            "runSignature": run_signature,
            "runRootSha256": run_root_sha256,
            "cursor": dataclasses.asdict(cursor),
            "baseModel": base_model,
            "optimizer": optimizer.state_dict(),
            "scheduler": scheduler.state_dict(),
            "pythonRandomState": _python_random_state(),
            "numpyRandomState": _numpy_random_state(numpy),
            "torchRandomState": torch.get_rng_state(),
            "torchCudaRandomStateAll": (
                torch.cuda.get_rng_state_all()
                if next(model.parameters()).device.type == "cuda"
                else []
            ),
        }
        torch.save(state, _state_dict_path(staging))
        best: dict[str, Any] | None = None
        if cursor.best_step >= 0:
            provenance_path = output_dir / "best" / "best-provenance.json"
            if not provenance_path.is_file() or _is_reparse_point(provenance_path):
                raise RuntimeError("The cursor names a best model without provenance")
            actual = _sha256_file(provenance_path)
            if actual != cursor.best_provenance_sha256:
                raise RuntimeError("Best-model provenance changed before checkpointing")
            best = {
                "step": cursor.best_step,
                "provenanceSha256": actual,
            }
        checkpoint_manifest = {
            "schemaVersion": TRAINER_STATE_SCHEMA_VERSION,
            "createdUtc": _utc_now(),
            "runSignature": run_signature,
            "runRootSha256": run_root_sha256,
            "cursor": _cursor_for_manifest(cursor),
            "best": best,
            "artifacts": _directory_artifacts(staging),
        }
        _write_json_atomic(
            _checkpoint_manifest_path(staging), checkpoint_manifest
        )
        _promote_directory(staging, directory)
    finally:
        if staging.exists():
            _safe_remove_generated_directory(staging, directory.parent)


def _save_model_snapshot(
    directory: Path,
    model: Any,
    tokenizer: Any,
    run_signature: str,
    run_root_sha256: str,
    step: int,
    selection_metric: str,
    metric: dict[str, Any],
) -> str:
    directory = _lexical_absolute(directory)
    _prepare_generated_parent(directory.parent)
    if _is_reparse_point(directory):
        raise RuntimeError(f"Refusing a reparse snapshot destination: {directory}")
    staging = Path(
        tempfile.mkdtemp(
            prefix=f".{directory.name}.", suffix=".tmp", dir=directory.parent
        )
    )
    try:
        model.save_pretrained(staging, safe_serialization=True)
        tokenizer.save_pretrained(staging)
        provenance = {
            "schemaVersion": TRAINER_STATE_SCHEMA_VERSION,
            "createdUtc": _utc_now(),
            "runSignature": run_signature,
            "runRootSha256": run_root_sha256,
            "optimizerStep": step,
            "selectionMetric": selection_metric,
            "validation": {
                "groupAccuracy": float(metric["groupAccuracy"]),
                "loss": float(metric["loss"]),
            },
            "artifacts": _directory_artifacts(staging),
        }
        _write_json_atomic(staging / "best-provenance.json", provenance)
        _promote_directory(staging, directory)
        return _sha256_file(directory / "best-provenance.json")
    finally:
        if staging.exists():
            _safe_remove_generated_directory(staging, directory.parent)


def _read_json_object(path: Path) -> dict[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise RuntimeError(f"Cannot read trusted metadata {path}: {error}") from error
    if not isinstance(value, dict):
        raise RuntimeError(f"Expected a JSON object in {path}")
    return value


def _validate_artifact_records(
    root: Path, records: Any, owning_manifest_name: str
) -> None:
    if not isinstance(records, list) or not records:
        raise RuntimeError(f"No artifact inventory below {root}")
    expected: set[str] = set()
    for record in records:
        if not isinstance(record, dict):
            raise RuntimeError("Malformed artifact inventory")
        relative = Path(str(record.get("file", "")))
        if relative.is_absolute() or not relative.parts or ".." in relative.parts:
            raise RuntimeError(f"Unsafe artifact path: {relative}")
        artifact = _lexical_absolute(root / relative)
        if artifact.parent != _lexical_absolute((root / relative).parent):
            raise RuntimeError(f"Unsafe artifact parent: {relative}")
        _assert_no_reparse_components(artifact)
        if not artifact.is_file():
            raise RuntimeError(f"Missing checkpoint artifact: {artifact}")
        relative_name = relative.as_posix()
        if relative_name in expected:
            raise RuntimeError(f"Duplicate artifact record: {relative_name}")
        expected.add(relative_name)
        if artifact.stat().st_size != int(record.get("bytes", -1)):
            raise RuntimeError(f"Artifact length mismatch: {artifact}")
        if _sha256_file(artifact) != record.get("sha256"):
            raise RuntimeError(f"Artifact SHA-256 mismatch: {artifact}")
    discovered = list(root.rglob("*"))
    for item in discovered:
        if _is_reparse_point(item):
            raise RuntimeError(f"Refusing reparse checkpoint artifact: {item}")
    actual = {
        item.relative_to(root).as_posix()
        for item in discovered
        if item.is_file() and item.name != owning_manifest_name
    }
    if actual != expected:
        raise RuntimeError(
            f"Checkpoint artifact inventory differs: expected {sorted(expected)}, "
            f"found {sorted(actual)}"
        )


def _validate_best_provenance(
    output_dir: Path,
    best_record: Any,
    run_signature: str,
    run_root_sha256: str,
) -> None:
    best_dir = output_dir / "best"
    if best_record is None:
        if best_dir.exists() or _is_reparse_point(best_dir):
            raise RuntimeError("Checkpoint has no best model but output-dir does")
        return
    if not isinstance(best_record, dict):
        raise RuntimeError("Malformed best-model checkpoint provenance")
    _assert_no_reparse_components(best_dir)
    provenance_path = best_dir / "best-provenance.json"
    expected_sha = best_record.get("provenanceSha256")
    if not provenance_path.is_file() or _sha256_file(provenance_path) != expected_sha:
        raise RuntimeError("Best-model provenance does not match the checkpoint")
    provenance = _read_json_object(provenance_path)
    if (
        provenance.get("schemaVersion") != TRAINER_STATE_SCHEMA_VERSION
        or provenance.get("runSignature") != run_signature
        or provenance.get("runRootSha256") != run_root_sha256
        or provenance.get("optimizerStep") != best_record.get("step")
    ):
        raise RuntimeError("Best-model provenance belongs to another run or step")
    _validate_artifact_records(
        best_dir, provenance.get("artifacts"), "best-provenance.json"
    )


def _load_checkpoint_state(
    path: Path,
    torch: Any,
    output_dir: Path,
    run_signature: str,
    run_root_sha256: str,
) -> tuple[dict[str, Any], dict[str, Any]]:
    manifest_path = _checkpoint_manifest_path(path)
    if not manifest_path.is_file() or _is_reparse_point(manifest_path):
        raise RuntimeError(f"Checkpoint has no safe JSON manifest: {path}")
    manifest = _read_json_object(manifest_path)
    if (
        manifest.get("schemaVersion") != TRAINER_STATE_SCHEMA_VERSION
        or manifest.get("runSignature") != run_signature
        or manifest.get("runRootSha256") != run_root_sha256
    ):
        raise RuntimeError("Checkpoint manifest belongs to another run")
    match = CHECKPOINT_PATTERN.fullmatch(path.name)
    if match is None or int(match.group(1)) != manifest.get("cursor", {}).get("globalStep"):
        raise RuntimeError("Checkpoint directory step differs from its manifest")
    _validate_artifact_records(
        path, manifest.get("artifacts"), "checkpoint-manifest.json"
    )
    _validate_best_provenance(
        output_dir,
        manifest.get("best"),
        run_signature,
        run_root_sha256,
    )
    try:
        state = torch.load(
            _state_dict_path(path), map_location="cpu", weights_only=True
        )
    except Exception as error:
        raise RuntimeError(f"Cannot safely load checkpoint state: {error}") from error
    if not isinstance(state, dict):
        raise RuntimeError("Checkpoint training state is not a dictionary")
    if (
        state.get("schemaVersion") != TRAINER_STATE_SCHEMA_VERSION
        or state.get("runSignature") != run_signature
        or state.get("runRootSha256") != run_root_sha256
    ):
        raise RuntimeError("Checkpoint state differs from its trusted manifest")
    state_cursor = TrainingCursor(**state.get("cursor", {}))
    if _cursor_for_manifest(state_cursor) != manifest.get("cursor"):
        raise RuntimeError("Checkpoint cursor differs between JSON and tensor state")
    best_record = manifest.get("best")
    if best_record is None:
        if state_cursor.best_step >= 0 or state_cursor.best_provenance_sha256 is not None:
            raise RuntimeError("Checkpoint cursor has unexpected best-model state")
    elif (
        state_cursor.best_step != best_record.get("step")
        or state_cursor.best_provenance_sha256 != best_record.get("provenanceSha256")
    ):
        raise RuntimeError("Checkpoint best-model cursor differs from provenance")
    return state, manifest


def _restore_rng(state: dict[str, Any], torch: Any, numpy: Any) -> None:
    python_state = state["pythonRandomState"]
    random.setstate(
        (
            int(python_state["version"]),
            tuple(int(value) for value in python_state["internal"]),
            python_state["gaussian"],
        )
    )
    numpy_state = state["numpyRandomState"]
    numpy.random.set_state(
        (
            str(numpy_state["algorithm"]),
            numpy.asarray(numpy_state["values"], dtype="uint32"),
            int(numpy_state["position"]),
            int(numpy_state["hasGaussian"]),
            float(numpy_state["cachedGaussian"]),
        )
    )
    torch.set_rng_state(state["torchRandomState"])
    cuda_states = state.get("torchCudaRandomStateAll", [])
    if cuda_states:
        if not torch.cuda.is_available():
            raise RuntimeError("CUDA checkpoint cannot restore RNG without CUDA")
        torch.cuda.set_rng_state_all(cuda_states)


def _latest_checkpoint(output_dir: Path) -> Path | None:
    candidates: list[tuple[int, Path]] = []
    checkpoint_root = _assert_no_reparse_components(output_dir / "checkpoints")
    for path in checkpoint_root.glob("checkpoint-step-*"):
        match = CHECKPOINT_PATTERN.fullmatch(path.name)
        if match is None:
            continue
        if _is_reparse_point(path):
            raise RuntimeError(f"Refusing reparse checkpoint: {path}")
        if _checkpoint_manifest_path(path).is_file():
            candidates.append((int(match.group(1)), path))
    return max(candidates, default=(0, None), key=lambda item: item[0])[1]


def _prune_checkpoints(output_dir: Path, keep: int) -> None:
    checkpoint_root = _assert_no_reparse_components(output_dir / "checkpoints")
    checkpoints: list[tuple[int, Path]] = []
    for path in checkpoint_root.glob("checkpoint-step-*"):
        match = CHECKPOINT_PATTERN.fullmatch(path.name)
        if match is None:
            continue
        checkpoints.append((int(match.group(1)), path))
    for _, path in sorted(checkpoints)[:-keep]:
        absolute = _lexical_absolute(path)
        if absolute.parent != checkpoint_root or _is_reparse_point(absolute):
            raise RuntimeError(f"Refusing to prune unsafe checkpoint path: {path}")
        _assert_no_reparse_components(absolute)
        shutil.rmtree(absolute)


def _write_json_atomic(path: Path, value: Any) -> None:
    path = _lexical_absolute(path)
    _assert_no_reparse_components(path.parent)
    if _is_reparse_point(path):
        raise RuntimeError(f"Refusing to replace reparse JSON destination: {path}")
    path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(
        "w",
        encoding="utf-8",
        newline="\n",
        delete=False,
        dir=path.parent,
        prefix=f".{path.name}.",
        suffix=".tmp",
    ) as handle:
        temporary = Path(handle.name)
        json.dump(value, handle, ensure_ascii=False, indent=2, sort_keys=True)
        handle.write("\n")
    try:
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def _write_jsonl_atomic(path: Path, rows: Sequence[dict[str, Any]]) -> None:
    path = _lexical_absolute(path)
    _assert_no_reparse_components(path.parent)
    if _is_reparse_point(path):
        raise RuntimeError(f"Refusing to replace reparse JSONL destination: {path}")
    path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(
        "w",
        encoding="utf-8",
        newline="\n",
        delete=False,
        dir=path.parent,
        prefix=f".{path.name}.",
        suffix=".tmp",
    ) as handle:
        temporary = Path(handle.name)
        for row in rows:
            handle.write(json.dumps(row, ensure_ascii=False, sort_keys=True) + "\n")
    try:
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def _append_metric(path: Path, metric: dict[str, Any]) -> None:
    path = _lexical_absolute(path)
    _assert_no_reparse_components(path.parent)
    if _is_reparse_point(path):
        raise RuntimeError(f"Refusing to append through a reparse path: {path}")
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a", encoding="utf-8", newline="\n") as handle:
        handle.write(json.dumps(metric, ensure_ascii=False, sort_keys=True) + "\n")


def _is_better(metric: dict[str, Any], cursor: TrainingCursor, selection: str) -> bool:
    accuracy = float(metric["groupAccuracy"])
    loss = float(metric["loss"])
    if selection == "loss":
        return loss < cursor.best_loss
    return accuracy > cursor.best_group_accuracy or (
        accuracy == cursor.best_group_accuracy and loss < cursor.best_loss
    )


def _resolve_license_and_revision(
    model_name: str,
    requested_revision: str,
    resolved_revision: str,
    license_override: str | None,
    ml: dict[str, Any],
    local_files_only: bool,
) -> dict[str, Any]:
    if license_override:
        return {"value": license_override, "source": "--license"}
    model_path = Path(model_name)
    if model_path.exists():
        return {"value": "unknown", "source": "local model metadata unavailable"}
    if local_files_only:
        return {"value": "unknown", "source": "offline mode"}
    try:
        info = ml["HfApi"]().model_info(
            model_name,
            revision=resolved_revision or requested_revision,
        )
        card_data = getattr(info, "card_data", None)
        license_value = getattr(card_data, "license", None)
        if license_value is None and isinstance(card_data, dict):
            license_value = card_data.get("license")
        return {
            "value": str(license_value or "unknown"),
            "source": "Hugging Face model card",
        }
    except Exception as error:  # Metadata failure must not corrupt a finished run.
        return {
            "value": "unknown",
            "source": f"metadata lookup failed: {type(error).__name__}",
        }


def _canonical_distribution_name(name: str) -> str:
    return re.sub(r"[-_.]+", "-", name).lower()


def _package_versions(ml: dict[str, Any]) -> dict[str, str]:
    del ml  # Loading the stack already proved the required imports are usable.
    versions: dict[str, str] = {}
    for distribution in importlib.metadata.distributions():
        raw_name = distribution.metadata.get("Name")
        if not raw_name:
            raise RuntimeError("An installed distribution has no Name metadata")
        name = _canonical_distribution_name(raw_name)
        version = str(distribution.version)
        previous = versions.get(name)
        if previous is not None and previous != version:
            raise RuntimeError(
                f"Conflicting installed distributions for {name}: "
                f"{previous} and {version}"
            )
        versions[name] = version
    return dict(sorted(versions.items()))


def _python_runtime() -> dict[str, Any]:
    return {
        "implementation": platform.python_implementation(),
        "version": platform.python_version(),
        "pointerBits": struct.calcsize("P") * 8,
    }


def _compute_environment(torch: Any) -> dict[str, Any]:
    cuda_devices: list[dict[str, Any]] = []
    if torch.cuda.is_available():
        for index in range(torch.cuda.device_count()):
            properties = torch.cuda.get_device_properties(index)
            cuda_devices.append(
                {
                    "index": index,
                    "name": properties.name,
                    "capability": list(torch.cuda.get_device_capability(index)),
                    "totalMemoryBytes": int(properties.total_memory),
                }
            )
    return {
        "cublasWorkspaceConfig": os.environ.get("CUBLAS_WORKSPACE_CONFIG"),
        "pythonHashSeed": os.environ.get("PYTHONHASHSEED"),
        "cudaAvailable": bool(torch.cuda.is_available()),
        "torchCudaVersion": str(getattr(torch.version, "cuda", None)),
        "cudnnVersion": (
            int(torch.backends.cudnn.version())
            if torch.cuda.is_available() and torch.backends.cudnn.version()
            else None
        ),
        "cudaDevices": cuda_devices,
    }


def _directory_artifacts(path: Path) -> list[dict[str, Any]]:
    artifacts: list[dict[str, Any]] = []
    root = _assert_no_reparse_components(path)
    for item in root.rglob("*"):
        if _is_reparse_point(item):
            raise RuntimeError(f"Refusing reparse artifact: {item}")
    for file_path in sorted(item for item in root.rglob("*") if item.is_file()):
        relative = file_path.relative_to(root).as_posix()
        artifacts.append(
            {
                "file": relative,
                "bytes": file_path.stat().st_size,
                "sha256": _sha256_file(file_path),
            }
        )
    return artifacts


def _export_onnx(
    model: Any,
    tokenizer: Any,
    examples: Sequence[PairExample],
    bundle_dir: Path,
    max_length: int,
    parity_tolerance: float,
    ort_threads: int,
    runtime_batch_size: int,
    ml: dict[str, Any],
) -> dict[str, Any]:
    torch = ml["torch"]
    numpy = ml["numpy"]
    onnx = ml["onnx"]
    ort = ml["onnxruntime"]
    bundle_dir.mkdir(parents=True, exist_ok=True)
    tokenizer.save_pretrained(bundle_dir)
    model_directory = bundle_dir / "Cpu"
    model_directory.mkdir(parents=True, exist_ok=True)

    forward_parameters = inspect.signature(model.forward).parameters
    include_token_types = (
        "token_type_ids" in forward_parameters
        and "token_type_ids" in getattr(tokenizer, "model_input_names", [])
    )
    input_names = ["input_ids", "attention_mask"]
    if include_token_types:
        input_names.append("token_type_ids")

    class LogitsOnly(torch.nn.Module):
        def __init__(self, wrapped: Any) -> None:
            super().__init__()
            self.wrapped = wrapped

        def forward(self, input_ids: Any, attention_mask: Any, token_type_ids: Any = None) -> Any:
            kwargs = {"input_ids": input_ids, "attention_mask": attention_mask}
            if include_token_types:
                kwargs["token_type_ids"] = token_type_ids
            return self.wrapped(**kwargs).logits

    model.eval()
    wrapper = LogitsOnly(model).eval()
    sample = list(examples[: min(4, len(examples))])
    encoded = tokenizer(
        [item.text_a for item in sample],
        [item.text_b for item in sample],
        padding=True,
        truncation="longest_first",
        max_length=max_length,
        return_tensors="pt",
    )
    tensors = tuple(encoded[name] for name in input_names)
    dynamic_axes = {
        name: {0: "batch", 1: "sequence"}
        for name in input_names
    }
    dynamic_axes["logits"] = {0: "batch"}

    onnx_path = model_directory / "definition-ranker-fp32.onnx"
    temporary = onnx_path.with_suffix(".onnx.tmp")
    try:
        export_kwargs = {
            "input_names": input_names,
            "output_names": ["logits"],
            "dynamic_axes": dynamic_axes,
            "opset_version": 17,
            "do_constant_folding": True,
        }
        if "dynamo" in inspect.signature(torch.onnx.export).parameters:
            export_kwargs["dynamo"] = False
        torch.onnx.export(wrapper, tensors, temporary, **export_kwargs)
        graph = onnx.load(temporary)
        onnx.checker.check_model(graph)

        options = ort.SessionOptions()
        options.intra_op_num_threads = ort_threads
        options.inter_op_num_threads = max(1, min(ort_threads, 4))
        session = ort.InferenceSession(
            str(temporary),
            sess_options=options,
            providers=["CPUExecutionProvider"],
        )
        outputs = session.get_outputs()
        if len(outputs) != 1 or outputs[0].name != "logits":
            raise RuntimeError("ONNX must expose exactly one output named logits")
        if len(outputs[0].shape) != 2 or outputs[0].shape[1] != 2:
            raise RuntimeError(
                f"ONNX logits must have shape [batch,2], got {outputs[0].shape}"
            )
        actual_input_names = [item.name for item in session.get_inputs()]
        if (
            not {"input_ids", "attention_mask"}.issubset(actual_input_names)
            or not set(actual_input_names).issubset(input_names)
        ):
            raise RuntimeError(
                "ONNX input mismatch: "
                f"exported from {input_names}, got {actual_input_names}"
            )

        short = min(
            sample,
            key=lambda item: len(
                tokenizer(item.text_a, item.text_b, truncation=False)["input_ids"]
            ),
        )
        long_text = " ".join([short.text_a] * max(8, max_length))
        long_gloss = " ".join([short.text_b] * max(8, max_length))
        truncated = PairExample(
            group_id="onnx-parity-truncated",
            lemma_key=short.lemma_key,
            text_a=long_text,
            text_b=long_gloss,
            label=short.label,
            group_size=1,
        )
        untruncated_length = len(
            tokenizer(long_text, long_gloss, truncation=False, verbose=False)[
                "input_ids"
            ]
        )
        if untruncated_length <= max_length:
            raise RuntimeError("Could not construct an overlength parity input")
        runtime_examples = [
            (truncated if index % 3 == 1 else sample[index % len(sample)])
            for index in range(runtime_batch_size)
        ]
        parity_batches = [
            ("short-batch-1", [short], False),
            ("truncated-batch-1", [truncated], True),
            (f"runtime-batch-{runtime_batch_size}", runtime_examples, True),
        ]
        max_delta = 0.0
        tested_shapes: list[list[int]] = []
        tested_cases: list[dict[str, Any]] = []
        with torch.inference_mode():
            for case_name, parity_examples, expects_truncation in parity_batches:
                parity_encoded = tokenizer(
                    [item.text_a for item in parity_examples],
                    [item.text_b for item in parity_examples],
                    padding=True,
                    truncation="longest_first",
                    max_length=max_length,
                    return_tensors="pt",
                )
                if expects_truncation and case_name == "truncated-batch-1":
                    if parity_encoded["input_ids"].shape[1] != max_length:
                        raise RuntimeError(
                            "The overlength ONNX parity case was not truncated to "
                            "maximumSequenceLength"
                        )
                model_kwargs = {
                    name: parity_encoded[name]
                    for name in input_names
                }
                torch_logits = model(**model_kwargs).logits.detach().cpu().numpy()
                ort_logits = session.run(
                    ["logits"],
                    {
                        name: parity_encoded[name].detach().cpu().numpy()
                        for name in actual_input_names
                    },
                )[0]
                if tuple(ort_logits.shape) != (len(parity_examples), 2):
                    raise RuntimeError(
                        f"ONNX produced {ort_logits.shape}, expected "
                        f"({len(parity_examples)}, 2)"
                    )
                if not numpy.isfinite(torch_logits).all() or not numpy.isfinite(ort_logits).all():
                    raise RuntimeError(f"Non-finite logits in ONNX parity case {case_name}")
                case_delta = float(numpy.max(numpy.abs(torch_logits - ort_logits)))
                max_delta = max(max_delta, case_delta)
                if not numpy.array_equal(
                    numpy.argmax(torch_logits, axis=1),
                    numpy.argmax(ort_logits, axis=1),
                ):
                    raise RuntimeError(f"Class prediction mismatch in {case_name}")
                torch_scores = torch_logits[:, 1] - torch_logits[:, 0]
                ort_scores = ort_logits[:, 1] - ort_logits[:, 0]
                for left in range(len(parity_examples)):
                    for right in range(left + 1, len(parity_examples)):
                        gap = float(torch_scores[left] - torch_scores[right])
                        if abs(gap) > 2 * parity_tolerance:
                            ort_gap = float(ort_scores[left] - ort_scores[right])
                            if (gap > 0) != (ort_gap > 0):
                                raise RuntimeError(
                                    f"Ranking order mismatch in {case_name}"
                                )
                shape = list(parity_encoded["input_ids"].shape)
                tested_shapes.append(shape)
                tested_cases.append(
                    {
                        "name": case_name,
                        "inputShape": shape,
                        "maxAbsoluteDelta": case_delta,
                    }
                )
        if max_delta > parity_tolerance:
            raise RuntimeError(
                f"PyTorch/ORT max abs delta {max_delta:.8g} exceeds "
                f"{parity_tolerance:.8g}"
            )
        provider = session.get_providers()[0]
        del session
        gc.collect()
        os.replace(temporary, onnx_path)
    finally:
        temporary.unlink(missing_ok=True)

    return {
        "file": onnx_path.name,
        "bytes": onnx_path.stat().st_size,
        "sha256": _sha256_file(onnx_path),
        "opset": 17,
        "inputs": actual_input_names,
        "output": {"name": "logits", "shape": ["batch", 2]},
        "dynamicAxes": ["batch", "sequence"],
        "parity": {
            "maxAbsoluteDelta": max_delta,
            "tolerance": parity_tolerance,
            "testedInputShapes": tested_shapes,
            "testedCases": tested_cases,
            "overlengthTokensBeforeTruncation": untruncated_length,
            "provider": provider,
        },
    }


def _training_signature(
    arguments: argparse.Namespace,
    dataset: dict[str, Any],
    package_versions: dict[str, str],
) -> str:
    relevant = {
        "algorithmVersion": TRAINING_ALGORITHM_VERSION,
        "stateSchemaVersion": TRAINER_STATE_SCHEMA_VERSION,
        "trainerSha256": _sha256_file(Path(__file__).resolve()),
        "dataset": dataset,
        "model": arguments.model,
        "revision": arguments.revision,
        "pairVariant": arguments.pair_variant,
        "seed": arguments.seed,
        "maxTrainGroups": arguments.max_train_groups,
        "maxValidGroups": arguments.max_valid_groups,
        "maxLength": arguments.max_length,
        "batchSize": arguments.batch_size,
        "groupsPerBatch": arguments.groups_per_batch,
        "evalBatchSize": arguments.eval_batch_size,
        "gradientAccumulationSteps": arguments.gradient_accumulation_steps,
        "epochs": arguments.epochs,
        "maxSteps": arguments.max_steps,
        "learningRate": arguments.learning_rate,
        "weightDecay": arguments.weight_decay,
        "warmupRatio": arguments.warmup_ratio,
        "maxGradNorm": arguments.max_grad_norm,
        "groupWeighting": arguments.group_weighting,
        "positiveClassWeight": arguments.positive_class_weight,
        "lossMode": arguments.loss_mode,
        "pairwiseCoefficient": arguments.pairwise_coefficient,
        "headInitialization": arguments.head_initialization,
        "freezeEmbeddings": arguments.freeze_embeddings,
        "freezeEncoderLayers": arguments.freeze_encoder_layers,
        "truncateEncoderLayers": arguments.truncate_encoder_layers,
        "selectionMetric": arguments.selection_metric,
        "padToMultipleOf": arguments.pad_to_multiple_of,
        "threads": arguments.threads,
        "workers": arguments.workers,
        "deterministicAlgorithms": "strict",
        "pythonHashSeed": os.environ.get("PYTHONHASHSEED"),
        "cublasWorkspaceConfig": (
            os.environ.get("CUBLAS_WORKSPACE_CONFIG")
            if arguments.device == "cuda"
            else None
        ),
        "parityTolerance": arguments.parity_tolerance,
        "runtimeBatchSize": arguments.runtime_batch_size,
        "device": arguments.device,
        "precision": arguments.precision,
        "packageVersions": package_versions,
        "pythonRuntime": _python_runtime(),
    }
    return _sha256_json(relevant)


def _parse_resume(value: str | None, output_dir: Path) -> Path | None:
    if not value:
        return None
    if value == "auto":
        checkpoint = _latest_checkpoint(output_dir)
        if checkpoint is None:
            raise SystemExit(f"No checkpoint found below {output_dir / 'checkpoints'}")
        return _lexical_absolute(checkpoint)
    checkpoint = _lexical_absolute(Path(value))
    checkpoint_root = _lexical_absolute(output_dir / "checkpoints")
    if (
        checkpoint.parent != checkpoint_root
        or CHECKPOINT_PATTERN.fullmatch(checkpoint.name) is None
    ):
        raise SystemExit(
            "--resume may name only a direct checkpoint-step-N child of "
            f"{checkpoint_root}"
        )
    if _is_reparse_point(checkpoint):
        raise SystemExit(f"Refusing a symlink/junction checkpoint: {checkpoint}")
    _assert_no_reparse_components(checkpoint)
    if not _checkpoint_manifest_path(checkpoint).is_file():
        raise SystemExit(f"Not a WSD training checkpoint: {checkpoint}")
    return checkpoint


def train_and_export(arguments: argparse.Namespace) -> int:
    if arguments.dataset is None:
        raise SystemExit("--dataset is required unless --self-check is used alone")
    dataset_path = _reject_holdout_path(arguments.dataset)
    output_dir = _lexical_absolute(arguments.output_dir)
    _assert_no_reparse_components(output_dir.parent)
    if _is_reparse_point(output_dir):
        raise SystemExit(f"Refusing a symlink/junction output directory: {output_dir}")
    if output_dir.exists():
        _assert_no_reparse_components(output_dir)

    dataset_file = {
        "fileName": dataset_path.name,
        "bytes": dataset_path.stat().st_size,
        "sha256": _sha256_file(dataset_path),
    }
    inventory = load_grouped_jsonl(
        dataset_path,
        pair_variant=arguments.pair_variant,
        seed=arguments.seed,
        max_train_groups=arguments.max_train_groups,
        max_valid_groups=arguments.max_valid_groups,
    )
    dataset_manifest = {
        **dataset_file,
        "pairVariant": arguments.pair_variant,
        "groups": inventory.groups_by_split,
        "pairs": inventory.pairs_by_split,
        "labels": {
            split: {str(label): count for label, count in counts.items()}
            for split, counts in inventory.labels_by_split.items()
        },
        "selectedGroupIdsSha256": inventory.selected_group_ids_sha256,
    }
    if arguments.inspect_only:
        print(json.dumps(dataset_manifest, ensure_ascii=False, indent=2, sort_keys=True))
        return 0

    output_dir.mkdir(parents=True, exist_ok=True)
    _assert_no_reparse_components(output_dir)

    ml = _load_ml_stack()
    torch = ml["torch"]
    numpy = ml["numpy"]
    _validate_deterministic_environment(arguments.device, arguments.seed)
    _set_determinism(torch, numpy, arguments.seed, arguments.threads)
    training_device = _resolve_training_device(
        torch, arguments.device, arguments.precision
    )
    package_versions = _package_versions(ml)

    run_signature = _training_signature(
        arguments, dataset_manifest, package_versions
    )
    run_root_sha256 = _run_root_sha256(output_dir)
    resume_path = _parse_resume(arguments.resume, output_dir)
    existing_state = output_dir / "run-state.json"
    if resume_path is None and existing_state.exists():
        raise SystemExit(
            f"{output_dir} already contains a run. Use --resume auto or choose a "
            "new --output-dir."
        )
    previous_run_state: dict[str, Any] | None = None
    checkpoint_state: dict[str, Any] | None = None
    if resume_path is not None:
        if not existing_state.is_file() or _is_reparse_point(existing_state):
            raise SystemExit("Resume requires output-dir/run-state.json")
        previous_run_state = _read_json_object(existing_state)
        if (
            previous_run_state.get("schemaVersion") != TRAINER_STATE_SCHEMA_VERSION
            or previous_run_state.get("runSignature") != run_signature
            or previous_run_state.get("runRootSha256") != run_root_sha256
        ):
            raise SystemExit(
                "Run root/signature differs from output-dir/run-state.json"
            )
        checkpoint_state, _ = _load_checkpoint_state(
            resume_path,
            torch,
            output_dir,
            run_signature,
            run_root_sha256,
        )

    tokenizer_source = resume_path or arguments.model
    tokenizer = ml["AutoTokenizer"].from_pretrained(
        tokenizer_source,
        revision=None if resume_path else arguments.revision,
        use_fast=True,
        local_files_only=arguments.local_files_only or bool(resume_path),
        trust_remote_code=False,
    )
    if resume_path:
        model = ml["AutoModelForSequenceClassification"].from_pretrained(
            resume_path,
            dtype=torch.float32,
            local_files_only=True,
            trust_remote_code=False,
        )
        if checkpoint_state is None:
            raise AssertionError("validated checkpoint state unexpectedly missing")
        base_model = checkpoint_state["baseModel"]
    else:
        config = ml["AutoConfig"].from_pretrained(
            arguments.model,
            revision=arguments.revision,
            local_files_only=arguments.local_files_only,
            trust_remote_code=False,
        )
        layer_initialization = _truncate_config_encoder(
            config, arguments.truncate_encoder_layers
        )
        if arguments.head_initialization == "nli-projected":
            model = ml["AutoModelForSequenceClassification"].from_pretrained(
                arguments.model,
                revision=arguments.revision,
                config=config,
                dtype=torch.float32,
                local_files_only=arguments.local_files_only,
                trust_remote_code=False,
            )
            head_initialization = _project_nli_head(model, torch)
        else:
            config.num_labels = 2
            config.id2label = {0: "NEGATIVE", 1: "POSITIVE"}
            config.label2id = {"NEGATIVE": 0, "POSITIVE": 1}
            config.problem_type = "single_label_classification"
            model = ml["AutoModelForSequenceClassification"].from_pretrained(
                arguments.model,
                revision=arguments.revision,
                config=config,
                dtype=torch.float32,
                ignore_mismatched_sizes=True,
                local_files_only=arguments.local_files_only,
                trust_remote_code=False,
            )
            head_initialization = {
                "mode": "reset",
                "head": _reset_binary_head(model, torch),
            }
        resolved_revision = str(
            getattr(model.config, "_commit_hash", None) or arguments.revision
        )
        base_model = {
            "modelId": arguments.model,
            "requestedRevision": arguments.revision,
            "resolvedRevision": resolved_revision,
            "license": _resolve_license_and_revision(
                arguments.model,
                arguments.revision,
                resolved_revision,
                arguments.license,
                ml,
                arguments.local_files_only,
            ),
            "architecture": type(model).__name__,
            "configSha256": _sha256_json(model.config.to_dict()),
            "headInitialization": head_initialization,
            **layer_initialization,
        }

    if int(model.config.num_labels) != 2:
        raise RuntimeError(f"Model must have two labels, got {model.config.num_labels}")
    model.config.id2label = {0: "NEGATIVE", 1: "POSITIVE"}
    model.config.label2id = {"NEGATIVE": 0, "POSITIVE": 1}
    model.config.problem_type = "single_label_classification"
    if hasattr(model, "num_labels"):
        model.num_labels = 2
    loaded_layers = int(getattr(model.config, "num_hidden_layers", 0))
    expected_layers = (
        arguments.truncate_encoder_layers
        or int(base_model.get("sourceEncoderLayers", loaded_layers))
    )
    if loaded_layers != expected_layers:
        raise RuntimeError(
            f"Loaded {loaded_layers} encoder layers, expected {expected_layers}"
        )
    model.to(training_device)
    parameter_counts = _freeze_encoder(
        model,
        freeze_embeddings=arguments.freeze_embeddings,
        layer_count=arguments.freeze_encoder_layers,
    )
    trainable = [parameter for parameter in model.parameters() if parameter.requires_grad]
    if not trainable:
        raise ValueError("All model parameters are frozen")

    if arguments.loss_mode == "hybrid":
        train_dataset: Any = GroupDataset(inventory.train_groups)
        train_collator: Any = _make_group_collator(
            tokenizer,
            max_length=arguments.max_length,
            pad_to_multiple_of=arguments.pad_to_multiple_of,
            group_weighting=arguments.group_weighting,
        )
        train_batch_size = arguments.groups_per_batch
    else:
        train_dataset = PairDataset(inventory.train)
        train_collator = _make_collator(
            tokenizer,
            torch,
            max_length=arguments.max_length,
            pad_to_multiple_of=arguments.pad_to_multiple_of,
            group_weighting=arguments.group_weighting,
        )
        train_batch_size = arguments.batch_size
    valid_dataset = GroupDataset(inventory.valid_groups)
    valid_collator = _make_group_collator(
        tokenizer,
        max_length=arguments.max_length,
        pad_to_multiple_of=arguments.pad_to_multiple_of,
        group_weighting=arguments.group_weighting,
    )
    positive_weight = _resolved_positive_weight(
        arguments.positive_class_weight,
        inventory.train,
        arguments.group_weighting,
    )
    batches_per_epoch = math.ceil(len(train_dataset) / train_batch_size)
    updates_per_epoch = math.ceil(
        batches_per_epoch / arguments.gradient_accumulation_steps
    )
    planned_steps = updates_per_epoch * arguments.epochs
    if arguments.max_steps:
        planned_steps = min(planned_steps, arguments.max_steps)
    warmup_steps = int(planned_steps * arguments.warmup_ratio)

    optimizer = torch.optim.AdamW(
        trainable,
        lr=arguments.learning_rate,
        weight_decay=arguments.weight_decay,
    )
    scheduler = ml["get_linear_schedule_with_warmup"](
        optimizer,
        num_warmup_steps=warmup_steps,
        num_training_steps=max(planned_steps, 1),
    )
    cursor = TrainingCursor()
    if checkpoint_state:
        optimizer.load_state_dict(checkpoint_state["optimizer"])
        _optimizer_to_device(optimizer, training_device, torch)
        scheduler.load_state_dict(checkpoint_state["scheduler"])
        cursor = TrainingCursor(**checkpoint_state["cursor"])
        _restore_rng(checkpoint_state, torch, numpy)

    run_state = {
        "schemaVersion": TRAINER_STATE_SCHEMA_VERSION,
        "algorithmVersion": TRAINING_ALGORITHM_VERSION,
        "runSignature": run_signature,
        "runRootSha256": run_root_sha256,
        "dataset": dataset_manifest,
        "baseModel": base_model,
        "packageVersions": package_versions,
        "pythonRuntime": _python_runtime(),
        "training": {
            "seed": arguments.seed,
            "epochs": arguments.epochs,
            "plannedOptimizerSteps": planned_steps,
            "batchSize": arguments.batch_size,
            "groupsPerBatch": arguments.groups_per_batch,
            "lossMode": arguments.loss_mode,
            "pairwiseCoefficient": arguments.pairwise_coefficient,
            "gradientAccumulationSteps": arguments.gradient_accumulation_steps,
            "effectiveBatchSize": train_batch_size * arguments.gradient_accumulation_steps,
            "effectiveBatchUnit": (
                "groups" if arguments.loss_mode == "hybrid" else "pairs"
            ),
            "evalBatchSizeGroups": arguments.eval_batch_size,
            "maxLength": arguments.max_length,
            "padToMultipleOf": arguments.pad_to_multiple_of,
            "learningRate": arguments.learning_rate,
            "weightDecay": arguments.weight_decay,
            "warmupRatio": arguments.warmup_ratio,
            "warmupSteps": warmup_steps,
            "groupWeighting": arguments.group_weighting,
            "positiveClassWeight": positive_weight,
            "freezeEmbeddings": arguments.freeze_embeddings,
            "freezeEncoderLayers": arguments.freeze_encoder_layers,
            "truncateEncoderLayers": arguments.truncate_encoder_layers,
            "headInitialization": arguments.head_initialization,
            "selectionMetric": arguments.selection_metric,
            "threads": arguments.threads,
            "workers": arguments.workers,
            "deterministicAlgorithms": "strict",
            "pythonHashSeed": os.environ.get("PYTHONHASHSEED"),
            "cublasWorkspaceConfig": (
                os.environ.get("CUBLAS_WORKSPACE_CONFIG")
                if arguments.device == "cuda"
                else None
            ),
            "device": arguments.device,
            "precision": arguments.precision,
            "parameterCounts": parameter_counts,
        },
    }
    if previous_run_state is None:
        _write_json_atomic(existing_state, run_state)
    elif previous_run_state != run_state:
        raise SystemExit(
            "Current run metadata differs from the existing resume root"
        )
    if resume_path is None:
        _save_checkpoint(
            output_dir / "checkpoints" / "checkpoint-step-0",
            model,
            tokenizer,
            optimizer,
            scheduler,
            cursor,
            run_signature,
            run_root_sha256,
            output_dir,
            base_model,
            torch,
            numpy,
        )
    print(
        f"Loaded {inventory.groups_by_split['train']} train / "
        f"{inventory.groups_by_split['valid']} valid groups; "
        f"{len(inventory.train)} train pairs; {planned_steps} optimizer steps."
    )
    print(
        f"Model {arguments.model}@{base_model['resolvedRevision']}; "
        f"positive-class weight {positive_weight:.6g}; device "
        f"{arguments.device}/{arguments.precision}; threads {arguments.threads}."
    )

    best_dir = output_dir / "best"
    metrics_path = output_dir / "metrics.jsonl"
    optimizer.zero_grad(set_to_none=True)
    stop = cursor.global_step >= planned_steps
    last_validation: dict[str, Any] | None = None
    for epoch in range(cursor.epoch, arguments.epochs):
        if stop:
            break
        start_batch = cursor.batch_in_epoch if epoch == cursor.epoch else 0
        generator = torch.Generator()
        generator.manual_seed(arguments.seed + epoch)
        indices = torch.randperm(len(train_dataset), generator=generator).tolist()
        start_index = start_batch * train_batch_size
        subset = ml["Subset"](train_dataset, indices[start_index:])
        loader = ml["DataLoader"](
            subset,
            batch_size=train_batch_size,
            shuffle=False,
            num_workers=arguments.workers,
            collate_fn=train_collator,
            pin_memory=False,
            generator=_loader_generator(torch, arguments.seed, "training", epoch),
        )
        model.train()
        accumulation = 0
        accumulated_denominator = 0.0
        accumulated_numerator = 0.0
        for local_batch, batch in enumerate(loader):
            absolute_batch = start_batch + local_batch
            batch = _move_batch_to_device(batch, training_device, torch)
            with _autocast_context(torch, training_device, arguments.precision):
                logits = model(**_model_inputs(batch)).logits
            if tuple(logits.shape) != (batch["labels"].shape[0], 2):
                raise RuntimeError(f"Expected logits [batch,2], got {tuple(logits.shape)}")
            numerator, denominator = _loss_parts(
                logits.float(),
                batch,
                torch,
                arguments.loss_mode,
                positive_weight,
                arguments.pairwise_coefficient,
            )
            if not torch.isfinite(numerator) or not torch.isfinite(denominator):
                raise RuntimeError("Training loss became non-finite")
            numerator.backward()
            accumulation += 1
            accumulated_numerator += float(numerator.detach().cpu())
            accumulated_denominator += float(denominator.detach().cpu())
            is_last_batch = absolute_batch + 1 == batches_per_epoch
            if accumulation < arguments.gradient_accumulation_steps and not is_last_batch:
                continue

            for parameter in trainable:
                if parameter.grad is not None:
                    parameter.grad.div_(max(accumulated_denominator, 1e-12))
            torch.nn.utils.clip_grad_norm_(trainable, arguments.max_grad_norm)
            optimizer.step()
            scheduler.step()
            optimizer.zero_grad(set_to_none=True)
            accumulation = 0
            reported_loss = accumulated_numerator / max(
                accumulated_denominator, 1e-12
            )
            accumulated_numerator = 0.0
            accumulated_denominator = 0.0
            cursor.global_step += 1
            cursor.epoch = epoch
            cursor.batch_in_epoch = absolute_batch + 1

            if cursor.global_step % arguments.log_every_steps == 0:
                print(
                    f"step {cursor.global_step}/{planned_steps} "
                    f"epoch {epoch + 1}/{arguments.epochs} "
                    f"loss {reported_loss:.6f} "
                    f"lr {scheduler.get_last_lr()[0]:.3g}"
                )
            if (
                arguments.save_every_steps
                and cursor.global_step % arguments.save_every_steps == 0
            ):
                checkpoint = (
                    output_dir
                    / "checkpoints"
                    / f"checkpoint-step-{cursor.global_step}"
                )
                _save_checkpoint(
                    checkpoint,
                    model,
                    tokenizer,
                    optimizer,
                    scheduler,
                    cursor,
                    run_signature,
                    run_root_sha256,
                    output_dir,
                    base_model,
                    torch,
                    numpy,
                )
                _prune_checkpoints(output_dir, arguments.save_total_limit)
            if cursor.global_step >= planned_steps:
                stop = True
                break

        epoch_complete = cursor.batch_in_epoch >= batches_per_epoch
        if epoch_complete:
            cursor.epoch = epoch + 1
            cursor.batch_in_epoch = 0
        last_validation = evaluate(
            model,
            valid_dataset,
            valid_collator,
            ml,
            batch_size=arguments.eval_batch_size,
            workers=arguments.workers,
            positive_weight=positive_weight,
            loss_mode=arguments.loss_mode,
            pairwise_coefficient=arguments.pairwise_coefficient,
            seed=arguments.seed,
            device=training_device,
            precision=arguments.precision,
        )
        metric_event = {
            "utc": _utc_now(),
            "optimizerStep": cursor.global_step,
            "epoch": cursor.epoch,
            "validation": last_validation,
        }
        _append_metric(metrics_path, metric_event)
        print(
            f"valid: loss {last_validation['loss']:.6f}; "
            f"group accuracy {last_validation['groupAccuracy']:.4%}; "
            f"pair accuracy {last_validation['pairAccuracy']:.4%}"
        )
        if _is_better(last_validation, cursor, arguments.selection_metric) or not best_dir.exists():
            cursor.best_group_accuracy = float(last_validation["groupAccuracy"])
            cursor.best_loss = float(last_validation["loss"])
            cursor.best_step = cursor.global_step
            cursor.best_provenance_sha256 = _save_model_snapshot(
                best_dir,
                model,
                tokenizer,
                run_signature,
                run_root_sha256,
                cursor.global_step,
                arguments.selection_metric,
                last_validation,
            )

        checkpoint = (
            output_dir / "checkpoints" / f"checkpoint-step-{cursor.global_step}"
        )
        _save_checkpoint(
            checkpoint,
            model,
            tokenizer,
            optimizer,
            scheduler,
            cursor,
            run_signature,
            run_root_sha256,
            output_dir,
            base_model,
            torch,
            numpy,
        )
        _prune_checkpoints(output_dir, arguments.save_total_limit)

    if last_validation is None:
        last_validation = evaluate(
            model,
            valid_dataset,
            valid_collator,
            ml,
            batch_size=arguments.eval_batch_size,
            workers=arguments.workers,
            positive_weight=positive_weight,
            loss_mode=arguments.loss_mode,
            pairwise_coefficient=arguments.pairwise_coefficient,
            seed=arguments.seed,
            device=training_device,
            precision=arguments.precision,
        )
        if not best_dir.exists():
            cursor.best_group_accuracy = float(last_validation["groupAccuracy"])
            cursor.best_loss = float(last_validation["loss"])
            cursor.best_step = cursor.global_step
            cursor.best_provenance_sha256 = _save_model_snapshot(
                best_dir,
                model,
                tokenizer,
                run_signature,
                run_root_sha256,
                cursor.global_step,
                arguments.selection_metric,
                last_validation,
            )
            _save_checkpoint(
                output_dir
                / "checkpoints"
                / f"checkpoint-step-{cursor.global_step}",
                model,
                tokenizer,
                optimizer,
                scheduler,
                cursor,
                run_signature,
                run_root_sha256,
                output_dir,
                base_model,
                torch,
                numpy,
            )

    _validate_best_provenance(
        output_dir,
        {
            "step": cursor.best_step,
            "provenanceSha256": cursor.best_provenance_sha256,
        },
        run_signature,
        run_root_sha256,
    )

    model.to("cpu")
    del model, optimizer, scheduler, trainable
    gc.collect()
    if torch.cuda.is_available():
        torch.cuda.empty_cache()

    best_model = ml["AutoModelForSequenceClassification"].from_pretrained(
        best_dir,
        local_files_only=True,
        trust_remote_code=False,
    ).eval()
    best_tokenizer = ml["AutoTokenizer"].from_pretrained(
        best_dir,
        local_files_only=True,
        use_fast=True,
        trust_remote_code=False,
    )
    best_model.to(training_device)
    best_validation_report = evaluate(
        best_model,
        valid_dataset,
        valid_collator,
        ml,
        batch_size=arguments.eval_batch_size,
        workers=arguments.workers,
        positive_weight=positive_weight,
        loss_mode=arguments.loss_mode,
        pairwise_coefficient=arguments.pairwise_coefficient,
        seed=arguments.seed,
        device=training_device,
        precision=arguments.precision,
        collect_group_outcomes=True,
    )
    group_outcomes = best_validation_report.pop("groupOutcomes")
    if not math.isclose(
        float(best_validation_report["groupAccuracy"]),
        cursor.best_group_accuracy,
        rel_tol=0.0,
        abs_tol=1e-12,
    ):
        raise RuntimeError("Reloaded best model does not reproduce selected accuracy")
    predictions_path = output_dir / "validation-predictions.jsonl"
    _write_jsonl_atomic(predictions_path, group_outcomes)
    predictions_artifact = {
        "file": predictions_path.name,
        "bytes": predictions_path.stat().st_size,
        "sha256": _sha256_file(predictions_path),
    }
    predictions_manifest = {
        "schemaVersion": 1,
        "createdUtc": _utc_now(),
        "purpose": "Text-free paired validation outcomes for significance tests",
        "runSignature": run_signature,
        "trainerSha256": _sha256_file(Path(__file__).resolve()),
        "datasetSha256": dataset_manifest["sha256"],
        "validationGroupIdsSha256": inventory.selected_group_ids_sha256["valid"],
        "bestOptimizerStep": cursor.best_step,
        "selectionMetric": arguments.selection_metric,
        "metrics": best_validation_report,
        "artifact": predictions_artifact,
    }
    _write_json_atomic(
        output_dir / "validation-predictions-manifest.json",
        predictions_manifest,
    )
    best_model.to("cpu")
    if torch.cuda.is_available():
        torch.cuda.empty_cache()
    bundle_root = output_dir / "bundle"
    _prepare_generated_parent(bundle_root)
    bundle_dir = bundle_root / "GlossDeberta"
    if _is_reparse_point(bundle_dir):
        raise RuntimeError(f"Refusing a reparse bundle destination: {bundle_dir}")
    bundle_staging = Path(
        tempfile.mkdtemp(prefix=".GlossDeberta.", suffix=".tmp", dir=bundle_root)
    )
    try:
        onnx_manifest = _export_onnx(
            best_model,
            best_tokenizer,
            inventory.valid,
            bundle_staging,
            max_length=arguments.max_length,
            parity_tolerance=arguments.parity_tolerance,
            ort_threads=arguments.threads,
            runtime_batch_size=arguments.runtime_batch_size,
            ml=ml,
        )
        tokenizer_path = bundle_staging / "tokenizer.json"
        if not tokenizer_path.is_file():
            raise RuntimeError("The exported runtime bundle has no tokenizer.json")
        runtime_manifest = {
            "schemaVersion": 1,
            "modelId": (
                f"DeBERTa-WSD:{base_model['modelId']}@"
                f"{base_model['resolvedRevision']}:{onnx_manifest['sha256'][:16]}"
            ),
            "modelFile": onnx_manifest["file"],
            "modelLength": onnx_manifest["bytes"],
            "modelSha256": onnx_manifest["sha256"],
            "tokenizerLength": tokenizer_path.stat().st_size,
            "tokenizerSha256": _sha256_file(tokenizer_path),
            "maximumSequenceLength": arguments.max_length,
            "batchSize": arguments.runtime_batch_size,
            "outputClassCount": 2,
            "positiveLabelIndex": 1,
        }
        _write_json_atomic(bundle_staging / "model-manifest.json", runtime_manifest)
        manifest = {
            "schemaVersion": MANIFEST_SCHEMA_VERSION,
            "createdUtc": _utc_now(),
            "purpose": "Asteria definition-aware binary WSD cross-encoder",
            "tool": {
                "file": Path(__file__).name,
                "sha256": _sha256_file(Path(__file__).resolve()),
            },
            "baseModel": base_model,
            "dataset": dataset_manifest,
            "training": {
                **run_state["training"],
                "completedOptimizerSteps": cursor.global_step,
                "completedEpochCursor": cursor.epoch,
                "selectionMetric": arguments.selection_metric,
                "bestValidation": {
                    "groupAccuracy": cursor.best_group_accuracy,
                    "loss": cursor.best_loss,
                },
                "bestValidationReport": predictions_manifest,
                "lastValidation": last_validation,
            },
            "export": onnx_manifest,
            "runtimeManifest": runtime_manifest,
            "environment": {
                "python": sys.version,
                "pythonRuntime": _python_runtime(),
                "platform": platform.platform(),
                "packages": package_versions,
                "compute": _compute_environment(torch),
                "trainingDevice": arguments.device,
                "trainingPrecision": arguments.precision,
                "cudaPathVerification": (
                    "exercised-by-this-run"
                    if arguments.device == "cuda"
                    else "not-exercised-by-this-run"
                ),
            },
        }
        manifest["bundleArtifacts"] = _directory_artifacts(bundle_staging)
        _write_json_atomic(bundle_staging / "manifest.json", manifest)
        _promote_directory(bundle_staging, bundle_dir)
    finally:
        if bundle_staging.exists():
            _safe_remove_generated_directory(bundle_staging, bundle_root)
    print(f"Bundle: {bundle_dir}")
    print(
        f"ONNX: {onnx_manifest['bytes']} bytes, sha256 "
        f"{onnx_manifest['sha256']}, max parity delta "
        f"{onnx_manifest['parity']['maxAbsoluteDelta']:.8g}"
    )
    print(f"Manifest sha256: {_sha256_file(bundle_dir / 'manifest.json')}")
    return 0


def _run_self_checks() -> None:
    """Fast invariant checks that do not consume production or holdout data."""

    try:
        import torch
        from torch.utils.data import DataLoader
    except ImportError as error:
        raise SystemExit(f"Self-check requires PyTorch: {error}") from error

    def expect_value_error(action: Any, label: str) -> None:
        try:
            action()
        except ValueError:
            return
        raise AssertionError(f"Self-check expected ValueError: {label}")

    def record(lemma: str, split: str, text_a: str) -> dict[str, Any]:
        return {
            "lemma": lemma,
            "surface": lemma,
            "text_a": text_a,
            "split": split,
            "pairs": [
                {"text_b": f"{lemma}: positive definition", "label": 1},
                {"text_b": f"{lemma}: negative definition", "label": 0},
            ],
        }

    assert _semantic_group_id("It’s", "IT'S", "  Some   Context ") == (
        _semantic_group_id("it's", "it’s", "some context")
    )
    with tempfile.TemporaryDirectory(prefix="wsd-trainer-self-check-") as temporary:
        root = Path(temporary)

        def write(name: str, rows: Sequence[dict[str, Any]]) -> Path:
            path = root / name
            path.write_text(
                "".join(json.dumps(row) + "\n" for row in rows),
                encoding="utf-8",
            )
            return path

        valid_path = write(
            "valid.jsonl",
            [
                record("alpha", "train", 'Use "alpha" here.'),
                record("beta", "valid", 'Use "beta" here.'),
            ],
        )
        inventory = load_grouped_jsonl(valid_path, "plain", 1, None, None)
        assert inventory.groups_by_split == {"train": 1, "valid": 1}
        expect_value_error(
            lambda: load_grouped_jsonl(
                write(
                    "unknown-split.jsonl",
                    [
                        record("alpha", "training", "alpha context"),
                        record("beta", "valid", "beta context"),
                    ],
                ),
                "plain",
                1,
                None,
                None,
            ),
            "unknown split",
        )
        duplicate = record("alpha", "train", "same context")
        expect_value_error(
            lambda: load_grouped_jsonl(
                write(
                    "duplicate.jsonl",
                    [duplicate, duplicate, record("beta", "valid", "beta context")],
                ),
                "plain",
                1,
                None,
                None,
            ),
            "duplicate group",
        )
        expect_value_error(
            lambda: load_grouped_jsonl(
                write(
                    "overlap.jsonl",
                    [
                        record("alpha", "train", "first alpha context"),
                        record("alpha", "valid", "second alpha context"),
                    ],
                ),
                "plain",
                1,
                None,
                None,
            ),
            "lemma split overlap",
        )

    logits = torch.zeros((10, 2), dtype=torch.float64, requires_grad=True)
    labels = torch.tensor([1, 0, 0, 1, 0, 0, 0, 0, 0, 0], dtype=torch.long)
    numerator, denominator = _hybrid_loss_parts(
        logits, labels, [2, 8], torch, 1.0, 0.25
    )
    expected = (math.log(2) + math.log(8) + 0.5 * math.log(2)) / 2
    assert math.isclose(
        float((numerator / denominator).detach()), expected, rel_tol=1e-12
    )
    (numerator / denominator).backward()
    assert torch.isfinite(logits.grad).all()
    assert _group_result([(0.0, 1), (0.0, 0)]) == (False, True)
    outcome = _group_outcome(
        "stable-group",
        "Example Lemma",
        [(0.75, 0), (-0.25, 1), (-1.0, 0)],
    )
    assert outcome["predictedIndex"] == 0
    assert outcome["goldIndex"] == 1
    assert outcome["correct"] is False
    assert outcome["pairPredictions"] == [1, 0, 0]
    assert outcome["pairCorrect"] == [False, False, True]
    assert math.isclose(outcome["margin"], 1.0, rel_tol=0.0, abs_tol=1e-12)
    assert outcome["lemmaClusterSha256"] == hashlib.sha256(
        b"Example Lemma"
    ).hexdigest()
    tied_outcome = _group_outcome(
        "tied-group", "lemma", [(0.0, 1), (0.0, 0)]
    )
    assert tied_outcome["predictedIndex"] is None
    assert tied_outcome["tie"] is True

    source_weight = torch.arange(12, dtype=torch.float32).reshape(3, 4)
    source_bias = torch.tensor([1.0, 2.0, 3.0])
    projected_weight, projected_bias = _project_nli_rows(
        source_weight, source_bias, 1, torch
    )
    assert torch.equal(projected_weight[1], source_weight[1])
    assert torch.equal(projected_weight[0], source_weight[[0, 2]].mean(dim=0))
    assert projected_bias is not None
    assert math.isclose(float(projected_bias[0]), 2.0 + math.log(2), rel_tol=1e-6)
    assert math.isclose(float(projected_bias[1]), 2.0, rel_tol=1e-6)
    zero_weight = torch.zeros((3, 1))
    _, zero_projected_bias = _project_nli_rows(
        zero_weight, torch.zeros(3), 1, torch
    )
    projected_probability = torch.softmax(zero_projected_bias, dim=0)[1]
    assert math.isclose(float(projected_probability), 1 / 3, rel_tol=1e-6)

    torch.manual_seed(12345)
    expected_random = torch.rand(1)
    torch.manual_seed(12345)
    list(
        DataLoader(
            [1],
            batch_size=1,
            generator=_loader_generator(torch, 9, "self-check"),
        )
    )
    assert torch.equal(torch.rand(1), expected_random)
    print("WSD trainer self-checks: OK")


def parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dataset", type=Path)
    parser.add_argument("--output-dir", type=Path, default=Path("artifacts/wsd-ranker"))
    parser.add_argument("--model", default=DEFAULT_MODEL)
    parser.add_argument("--revision", default="main")
    parser.add_argument(
        "--license",
        help="License identifier for a local/private model when metadata cannot be read.",
    )
    parser.add_argument("--local-files-only", action="store_true")
    parser.add_argument("--pair-variant", choices=("plain", "enriched"), default="plain")
    parser.add_argument("--seed", type=int, default=DEFAULT_SEED)
    parser.add_argument("--max-train-groups", type=int)
    parser.add_argument("--max-valid-groups", type=int)
    parser.add_argument("--inspect-only", action="store_true")
    parser.add_argument("--self-check", action="store_true")

    parser.add_argument("--epochs", type=int, default=3)
    parser.add_argument("--max-steps", type=int)
    parser.add_argument("--batch-size", type=int, default=8)
    parser.add_argument("--groups-per-batch", type=int, default=4)
    parser.add_argument("--eval-batch-size", type=int, default=16)
    parser.add_argument("--gradient-accumulation-steps", type=int, default=4)
    parser.add_argument("--max-length", type=int, default=128)
    parser.add_argument("--pad-to-multiple-of", type=int)
    parser.add_argument("--learning-rate", type=float, default=2e-5)
    parser.add_argument("--weight-decay", type=float, default=0.01)
    parser.add_argument("--warmup-ratio", type=float, default=0.1)
    parser.add_argument("--max-grad-norm", type=float, default=1.0)
    parser.add_argument("--group-weighting", choices=("equal", "none"), default="equal")
    parser.add_argument(
        "--loss-mode", choices=("pairwise", "hybrid"), default="pairwise"
    )
    parser.add_argument("--pairwise-coefficient", type=float, default=0.25)
    parser.add_argument(
        "--head-initialization",
        choices=("reset", "nli-projected"),
        default="nli-projected",
    )
    parser.add_argument(
        "--positive-class-weight",
        default="auto",
        help="auto, none, or a positive floating-point multiplier.",
    )
    parser.add_argument("--freeze-embeddings", action="store_true")
    parser.add_argument("--freeze-encoder-layers", type=int, default=0)
    parser.add_argument(
        "--truncate-encoder-layers",
        type=int,
        default=0,
        help="Load only the first N encoder layers; 0 keeps the source depth.",
    )
    parser.add_argument("--device", choices=("cpu", "cuda"), default="cpu")
    parser.add_argument("--precision", choices=("fp32", "bf16"), default="fp32")

    parser.add_argument("--threads", type=int, default=max(1, min(os.cpu_count() or 1, 8)))
    parser.add_argument("--workers", type=int, default=0)
    parser.add_argument("--log-every-steps", type=int, default=25)
    parser.add_argument("--save-every-steps", type=int, default=250)
    parser.add_argument("--save-total-limit", type=int, default=2)
    parser.add_argument("--resume", help="Checkpoint directory, or 'auto'.")
    parser.add_argument(
        "--selection-metric",
        choices=("group_accuracy", "loss"),
        default="group_accuracy",
    )
    parser.add_argument("--parity-tolerance", type=float, default=1e-4)
    parser.add_argument("--runtime-batch-size", type=int, default=16)
    arguments = parser.parse_args(argv)

    positive_ints = {
        "--epochs": arguments.epochs,
        "--batch-size": arguments.batch_size,
        "--groups-per-batch": arguments.groups_per_batch,
        "--eval-batch-size": arguments.eval_batch_size,
        "--gradient-accumulation-steps": arguments.gradient_accumulation_steps,
        "--max-length": arguments.max_length,
        "--threads": arguments.threads,
        "--log-every-steps": arguments.log_every_steps,
        "--save-total-limit": arguments.save_total_limit,
        "--runtime-batch-size": arguments.runtime_batch_size,
    }
    for name, value in positive_ints.items():
        if value <= 0:
            parser.error(f"{name} must be > 0")
    optional_positive_ints = {
        "--max-steps": arguments.max_steps,
        "--max-train-groups": arguments.max_train_groups,
        "--max-valid-groups": arguments.max_valid_groups,
        "--pad-to-multiple-of": arguments.pad_to_multiple_of,
    }
    for name, value in optional_positive_ints.items():
        if value is not None and value <= 0:
            parser.error(f"{name} must be > 0")
    if arguments.save_every_steps < 0:
        parser.error("--save-every-steps must be >= 0")
    if arguments.workers < 0:
        parser.error("--workers must be >= 0")
    if arguments.freeze_encoder_layers < 0:
        parser.error("--freeze-encoder-layers must be >= 0")
    if arguments.truncate_encoder_layers < 0:
        parser.error("--truncate-encoder-layers must be >= 0")
    if not math.isfinite(arguments.warmup_ratio) or not 0 <= arguments.warmup_ratio < 1:
        parser.error("--warmup-ratio must be in [0, 1)")
    if not math.isfinite(arguments.learning_rate) or arguments.learning_rate <= 0:
        parser.error("--learning-rate must be > 0")
    if not math.isfinite(arguments.weight_decay) or arguments.weight_decay < 0:
        parser.error("--weight-decay must be >= 0")
    if not math.isfinite(arguments.max_grad_norm) or arguments.max_grad_norm <= 0:
        parser.error("--max-grad-norm must be > 0")
    if not math.isfinite(arguments.parity_tolerance) or arguments.parity_tolerance <= 0:
        parser.error("--parity-tolerance must be > 0")
    if (
        not math.isfinite(arguments.pairwise_coefficient)
        or arguments.pairwise_coefficient < 0
    ):
        parser.error("--pairwise-coefficient must be finite and >= 0")
    if arguments.precision == "bf16" and arguments.device != "cuda":
        parser.error("--precision=bf16 requires --device=cuda")
    if arguments.dataset is None and not arguments.self_check:
        parser.error("--dataset is required unless --self-check is used")
    if arguments.inspect_only and arguments.dataset is None:
        parser.error("--inspect-only requires --dataset")
    return arguments


def main(argv: Sequence[str] | None = None) -> int:
    try:
        arguments = parse_args(argv)
        if arguments.self_check:
            _run_self_checks()
            if arguments.dataset is None:
                return 0
        return train_and_export(arguments)
    except KeyboardInterrupt:
        print("Interrupted. Resume from the latest completed checkpoint.", file=sys.stderr)
        return 130


if __name__ == "__main__":
    raise SystemExit(main())
