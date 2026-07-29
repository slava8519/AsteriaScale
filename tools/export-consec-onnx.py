#!/usr/bin/env python3
"""Safely inspect and export the official ConSeC WNGT checkpoint to ONNX.

The inference architecture and relative-position encoding are an independent,
minimal adaptation of ConSeC at the pinned upstream commit below. ConSeC is
licensed CC BY-NC-SA 4.0; this tool neither imports nor vendors upstream code.
"""

from __future__ import annotations

import argparse
import collections
import gc
import hashlib
import inspect
import json
import os
import pickletools
import re
import shutil
import subprocess
import sys
import tempfile
import types
import typing
import zipfile
from pathlib import Path
from typing import Any, Iterable, Sequence


UPSTREAM_REPOSITORY = "https://github.com/SapienzaNLP/consec"
UPSTREAM_COMMIT = "9602b5fd69f57be08a186988d1df34fe4152b63f"
UPSTREAM_LICENSE = "CC-BY-NC-SA-4.0"

CHECKPOINT_NAME = "consec_wngt_best.ckpt"
CHECKPOINT_BYTES = 4_864_940_005
CHECKPOINT_SHA256 = (
    "92421ed245723964db09ce396f19a0d1e55fe4d6e10d5ecb83278d9bc70ce8af"
)

BASE_MODEL_REPOSITORY = "microsoft/deberta-large"
BASE_MODEL_REVISION = "a97e054da5f34feed3d26951db4a25831dfcb486"
BASE_MODEL_FILES = {
    "config.json": (
        475,
        "71ed32326d68dc14aa63294acdfec1fe3fcaf1f9cb4f0744ee71c2fe59fd7aaa",
    ),
    "tokenizer_config.json": (
        52,
        "052b1ce2b5b3bf119a72dee8cbb2de441dd4ff8f2255be61cd8aa63fb24d8149",
    ),
    "vocab.json": (
        898_825,
        "ef13b2af772bf95f74e5512265bbe22b786bf25861df22d9f19fde0b3937fdea",
    ),
    "merges.txt": (
        456_318,
        "1ce1664773c50f3e0cc8842619a93edc4624525b728b188a9e0be33b7726adc5",
    ),
}

EXPECTED_PICKLE_GLOBALS = frozenset(
    {
        "__builtin__ dict",
        "__builtin__ list",
        "__builtin__ long",
        "collections OrderedDict",
        "collections defaultdict",
        "omegaconf.base ContainerMetadata",
        "omegaconf.base Metadata",
        "omegaconf.dictconfig DictConfig",
        "omegaconf.listconfig ListConfig",
        "omegaconf.nodes AnyNode",
        "pytorch_lightning.callbacks.early_stopping EarlyStopping",
        "src.pl_callbacks ModelCheckpointWithBest",
        "torch FloatStorage",
        "torch LongStorage",
        "torch._utils _rebuild_tensor_v2",
        "typing Any",
    }
)

EXPECTED_STATE_ENTRIES = 390
EXPECTED_TENSOR_ELEMENTS = 405_372_416
EXPECTED_EMBEDDING_SHAPE = (50_468, 1_024)
EXPECTED_LAYER_COUNT = 24
EXPECTED_POSITION_IDS_SHAPE = (1, 512)
STATE_PREFIX = "sense_extractor."
POSITION_IDS_KEY = "sense_extractor.model.embeddings.position_ids"

SPECIAL_TOKENS = ("<d>", "</d>", "DEF_SEP", "DEF_END", "CONTEXT_DEFS")
EXPECTED_SPECIAL_TOKEN_IDS = (50_265, 50_266, 50_267, 50_268, 50_269)

TOOL_VERSIONS = {
    "torch": "2.5.1",
    "transformers": "4.46.3",
    "tokenizers": "0.20.3",
    "onnx": "1.17.0",
    "onnxruntime": "1.20.1",
    "numpy": "2.5.1",
}

GRAPH_NAME = "consec-wngt-fp32.onnx"
EXTERNAL_DATA_NAME = GRAPH_NAME + ".data"
TOKENIZER_NAME = "tokenizer.json"
MANIFEST_NAME = "model-manifest.json"
ONNX_OPSET = 17
PARITY_TOLERANCE = 1e-4


class ExportError(RuntimeError):
    """A fail-closed validation or export failure."""


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(8 * 1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def file_record(path: Path, relative_to: Path) -> dict[str, object]:
    return {
        "file": path.relative_to(relative_to).as_posix(),
        "bytes": path.stat().st_size,
        "sha256": sha256(path),
    }


def verify_exact_file(path: Path, expected_bytes: int, expected_sha256: str) -> None:
    if not path.is_file():
        raise ExportError(f"Required file is missing: {path}")
    actual_bytes = path.stat().st_size
    if actual_bytes != expected_bytes:
        raise ExportError(
            f"Unexpected size for {path.name}: {actual_bytes}; expected {expected_bytes}"
        )
    actual_sha256 = sha256(path)
    if actual_sha256.lower() != expected_sha256.lower():
        raise ExportError(
            f"Unexpected SHA-256 for {path.name}: {actual_sha256}; "
            f"expected {expected_sha256}"
        )


def verify_upstream_checkout(upstream: Path) -> None:
    if not (upstream / ".git").exists():
        raise ExportError(f"Not a Git checkout: {upstream}")
    completed = subprocess.run(
        ["git", "-C", str(upstream), "rev-parse", "HEAD"],
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
    )
    if completed.returncode != 0:
        raise ExportError(f"Cannot read upstream revision: {completed.stderr.strip()}")
    revision = completed.stdout.strip().lower()
    if revision != UPSTREAM_COMMIT:
        raise ExportError(
            f"Unexpected ConSeC revision: {revision}; expected {UPSTREAM_COMMIT}"
        )
    license_path = upstream / "LICENSE.txt"
    if not license_path.is_file():
        raise ExportError(f"Upstream license is missing: {license_path}")
    license_header = license_path.read_text(encoding="utf-8").splitlines()[0].strip()
    if license_header != "Attribution-NonCommercial-ShareAlike 4.0 International":
        raise ExportError(f"Unexpected ConSeC license header: {license_header!r}")


def inspect_pickle_globals(checkpoint_path: Path) -> list[str]:
    try:
        with zipfile.ZipFile(checkpoint_path) as archive:
            pickle_members = [
                member
                for member in archive.infolist()
                if member.filename.endswith("/data.pkl")
            ]
            if len(pickle_members) != 1:
                raise ExportError(
                    "The checkpoint must contain exactly one PyTorch data.pkl member"
                )
            pickle_data = archive.read(pickle_members[0])
    except zipfile.BadZipFile as exc:
        raise ExportError("The checkpoint is not a valid PyTorch ZIP archive") from exc

    globals_seen: list[str] = []
    for opcode, argument, _ in pickletools.genops(pickle_data):
        if opcode.name == "GLOBAL":
            globals_seen.append(str(argument))
        elif opcode.name in {"STACK_GLOBAL", "EXT1", "EXT2", "EXT4"}:
            raise ExportError(f"Unsupported pickle opcode in checkpoint: {opcode.name}")

    globals_set = frozenset(globals_seen)
    if globals_set != EXPECTED_PICKLE_GLOBALS or len(globals_seen) != len(
        EXPECTED_PICKLE_GLOBALS
    ):
        missing = sorted(EXPECTED_PICKLE_GLOBALS - globals_set)
        unexpected = sorted(globals_set - EXPECTED_PICKLE_GLOBALS)
        raise ExportError(
            "Checkpoint pickle globals differ from the audited allowlist; "
            f"missing={missing}, unexpected={unexpected}, count={len(globals_seen)}"
        )
    return sorted(globals_set)


def inert_surrogate(module: str, name: str) -> type:
    # A blank object accepts legacy metadata state without importing callback code.
    surrogate = types.new_class(name, (object,))
    surrogate.__module__ = module
    surrogate.__qualname__ = name
    return surrogate


def checkpoint_safe_globals() -> list[object]:
    return [
        inert_surrogate(
            "pytorch_lightning.callbacks.early_stopping", "EarlyStopping"
        ),
        inert_surrogate("src.pl_callbacks", "ModelCheckpointWithBest"),
        inert_surrogate("omegaconf.dictconfig", "DictConfig"),
        inert_surrogate("omegaconf.base", "ContainerMetadata"),
        typing.Any,
        collections.defaultdict,
        inert_surrogate("omegaconf.nodes", "AnyNode"),
        inert_surrogate("omegaconf.base", "Metadata"),
        inert_surrogate("omegaconf.listconfig", "ListConfig"),
        dict,
        list,
        int,
    ]


def core_version(version: str) -> str:
    return version.split("+", maxsplit=1)[0]


def require_torch_version(torch: Any) -> None:
    actual = core_version(str(torch.__version__))
    if actual != TOOL_VERSIONS["torch"]:
        raise ExportError(
            f"torch {actual} is not the audited version {TOOL_VERSIONS['torch']}"
        )
    signature = inspect.signature(torch.load)
    if "weights_only" not in signature.parameters or "mmap" not in signature.parameters:
        raise ExportError("torch.load lacks the required safe-loading parameters")
    if not hasattr(torch.serialization, "safe_globals"):
        raise ExportError("torch.serialization.safe_globals is unavailable")


def safe_load_checkpoint(checkpoint_path: Path) -> tuple[dict[str, Any], list[str]]:
    verify_exact_file(checkpoint_path, CHECKPOINT_BYTES, CHECKPOINT_SHA256)
    pickle_globals = inspect_pickle_globals(checkpoint_path)

    import torch

    require_torch_version(torch)
    safe_globals = checkpoint_safe_globals()
    with torch.serialization.safe_globals(safe_globals):
        checkpoint = torch.load(
            checkpoint_path,
            map_location="cpu",
            weights_only=True,
            mmap=True,
        )
    if type(checkpoint) is not dict:
        raise ExportError(f"Unexpected checkpoint root type: {type(checkpoint)!r}")
    return checkpoint, pickle_globals


def omega_unwrap(value: object) -> object:
    if hasattr(value, "_val"):
        return getattr(value, "_val")
    content = getattr(value, "_content", None)
    if isinstance(content, list):
        return [omega_unwrap(item) for item in content]
    if isinstance(content, dict):
        return {str(key): omega_unwrap(item) for key, item in content.items()}
    return value


def omega_get(root: object, *path: str) -> object:
    current = root
    for part in path:
        content = getattr(current, "_content", None)
        if not isinstance(content, dict) or part not in content:
            raise ExportError(f"Checkpoint metadata path is missing: {'.'.join(path)}")
        current = content[part]
    return omega_unwrap(current)


def validate_checkpoint(checkpoint: dict[str, Any], pickle_globals: list[str]) -> dict[str, object]:
    import torch

    state_dict = checkpoint.get("state_dict")
    if not isinstance(state_dict, collections.OrderedDict):
        raise ExportError("Checkpoint state_dict is not an OrderedDict")
    if len(state_dict) != EXPECTED_STATE_ENTRIES:
        raise ExportError(
            f"Unexpected state entry count: {len(state_dict)}; "
            f"expected {EXPECTED_STATE_ENTRIES}"
        )
    if not all(isinstance(value, torch.Tensor) for value in state_dict.values()):
        raise ExportError("Checkpoint state_dict contains a non-tensor value")

    tensor_elements = sum(value.numel() for value in state_dict.values())
    if tensor_elements != EXPECTED_TENSOR_ELEMENTS:
        raise ExportError(
            f"Unexpected tensor element count: {tensor_elements}; "
            f"expected {EXPECTED_TENSOR_ELEMENTS}"
        )

    embedding_key = STATE_PREFIX + "model.embeddings.word_embeddings.weight"
    embedding = state_dict.get(embedding_key)
    if embedding is None or tuple(embedding.shape) != EXPECTED_EMBEDDING_SHAPE:
        raise ExportError(
            f"Unexpected embedding shape: {getattr(embedding, 'shape', None)}; "
            f"expected {EXPECTED_EMBEDDING_SHAPE}"
        )
    position_ids = state_dict.get(POSITION_IDS_KEY)
    if position_ids is None or tuple(position_ids.shape) != EXPECTED_POSITION_IDS_SHAPE:
        raise ExportError("Unexpected legacy position_ids buffer")

    layer_pattern = re.compile(
        r"^sense_extractor\.model\.encoder\.layer\.(\d+)\."
    )
    layers = sorted(
        {
            int(match.group(1))
            for key in state_dict
            if (match := layer_pattern.match(key)) is not None
        }
    )
    if layers != list(range(EXPECTED_LAYER_COUNT)):
        raise ExportError(f"Unexpected encoder layers: {layers}")

    hparams = checkpoint.get("hyper_parameters")
    metadata_expectations: tuple[tuple[tuple[str, ...], object], ...] = (
        (("model", "transformer_model"), BASE_MODEL_REPOSITORY),
        (
            ("model", "sense_extractor", "_target_"),
            "src.sense_extractors.DebertaPositionalExtractor",
        ),
        (("model", "sense_extractor", "dropout"), 0.0),
        (("model", "sense_extractor", "use_definitions_mask"), True),
        (
            ("tokenizer", "consec_tokenizer", "_target_"),
            "src.consec_tokenizer.DeBERTaTokenizer",
        ),
        (("tokenizer", "consec_tokenizer", "target_marker"), ["<d>", "</d>"]),
        (("tokenizer", "consec_tokenizer", "optimize_relative_positions"), True),
        (("tokenizer", "consec_tokenizer", "enforce_symmetry"), False),
        (("data", "text_encoding_strategy"), "relative-positions"),
        (("data", "use_definition_start"), True),
    )
    for path, expected in metadata_expectations:
        actual = omega_get(hparams, *path)
        if actual != expected:
            raise ExportError(
                f"Unexpected checkpoint metadata {'.'.join(path)}: "
                f"{actual!r}; expected {expected!r}"
            )

    return {
        "checkpoint": {
            "file": CHECKPOINT_NAME,
            "bytes": CHECKPOINT_BYTES,
            "sha256": CHECKPOINT_SHA256,
            "pytorchLightningVersion": checkpoint.get("pytorch-lightning_version"),
        },
        "stateEntries": len(state_dict),
        "tensorElements": tensor_elements,
        "embeddingShape": list(embedding.shape),
        "encoderLayers": len(layers),
        "pickleGlobals": pickle_globals,
        "metadata": {
            "baseModel": omega_get(hparams, "model", "transformer_model"),
            "extractor": omega_get(hparams, "model", "sense_extractor", "_target_"),
            "encoding": omega_get(hparams, "data", "text_encoding_strategy"),
        },
    }


def verify_export_environment() -> dict[str, str]:
    import numpy
    import onnx
    import onnxruntime
    import tokenizers
    import torch
    import transformers

    actual = {
        "torch": core_version(str(torch.__version__)),
        "transformers": transformers.__version__,
        "tokenizers": tokenizers.__version__,
        "onnx": onnx.__version__,
        "onnxruntime": onnxruntime.__version__,
        "numpy": numpy.__version__,
    }
    mismatches = {
        name: {"actual": actual[name], "expected": expected}
        for name, expected in TOOL_VERSIONS.items()
        if actual[name] != expected
    }
    if mismatches:
        raise ExportError(f"Unpinned export environment: {mismatches}")
    return actual


def verify_base_model_source(source: Path) -> list[dict[str, object]]:
    records: list[dict[str, object]] = []
    for name, (expected_bytes, expected_sha256) in BASE_MODEL_FILES.items():
        path = source / name
        verify_exact_file(path, expected_bytes, expected_sha256)
        records.append(
            {
                "file": name,
                "bytes": expected_bytes,
                "sha256": expected_sha256,
            }
        )
    return records


def create_tokenizer(source: Path) -> object:
    from tokenizers import AddedToken
    from transformers import AutoTokenizer

    tokenizer = AutoTokenizer.from_pretrained(
        source,
        local_files_only=True,
        use_fast=True,
    )
    if not tokenizer.is_fast:
        raise ExportError("The pinned DeBERTa tokenizer did not load as a fast tokenizer")
    added_tokens = [
        AddedToken(token, single_word=True, lstrip=True)
        for token in SPECIAL_TOKENS
    ]
    added_count = tokenizer.add_special_tokens(
        {"additional_special_tokens": added_tokens}
    )
    token_ids = tuple(tokenizer.convert_tokens_to_ids(token) for token in SPECIAL_TOKENS)
    if added_count != len(SPECIAL_TOKENS) or token_ids != EXPECTED_SPECIAL_TOKEN_IDS:
        raise ExportError(
            f"Unexpected ConSeC token allocation: added={added_count}, ids={token_ids}; "
            f"expected ids={EXPECTED_SPECIAL_TOKEN_IDS}"
        )
    return tokenizer


def create_model(checkpoint: dict[str, Any], base_model_source: Path) -> object:
    import torch
    from transformers import AutoConfig
    from transformers.models.deberta.modeling_deberta import (
        DebertaEmbeddings,
        DebertaEncoder,
        DisentangledSelfAttention,
        c2p_dynamic_expand,
        p2c_dynamic_expand,
    )

    config = AutoConfig.from_pretrained(base_model_source, local_files_only=True)
    config_expectations = {
        "model_type": "deberta",
        "hidden_size": 1_024,
        "intermediate_size": 4_096,
        "num_attention_heads": 16,
        "num_hidden_layers": 24,
        "vocab_size": 50_265,
        "max_position_embeddings": 512,
        "relative_attention": True,
        "position_biased_input": False,
    }
    for name, expected in config_expectations.items():
        actual = getattr(config, name, None)
        if actual != expected:
            raise ExportError(
                f"Unexpected DeBERTa config {name}: {actual!r}; expected {expected!r}"
            )
    config.vocab_size = EXPECTED_EMBEDDING_SHAPE[0]
    config.output_attentions = False
    config.output_hidden_states = False
    config.return_dict = False

    class FixedSpanDisentangledSelfAttention(DisentangledSelfAttention):
        def disentangled_att_bias(
            self,
            query_layer: object,
            key_layer: object,
            relative_pos: object,
            rel_embeddings: object,
            scale_factor: int,
        ) -> object:
            if relative_pos.dim() == 2:
                relative_pos = relative_pos.unsqueeze(0).unsqueeze(0)
            elif relative_pos.dim() == 3:
                relative_pos = relative_pos.unsqueeze(1)
            elif relative_pos.dim() != 4:
                raise ExportError("relative_pos must have rank 2, 3, or 4")

            # The upstream dynamic slice selects the same global relative vectors,
            # but legacy ONNX freezes its Python shape. A full fixed span is exact
            # for self-attention and leaves sequence length genuinely dynamic.
            attention_span = self.max_relative_positions
            relative_pos = relative_pos.long().to(query_layer.device)
            relative_vectors = rel_embeddings[: 2 * attention_span, :].unsqueeze(0)
            score = 0

            if "c2p" in self.pos_att_type:
                position_keys = self.transpose_for_scores(
                    self.pos_proj(relative_vectors)
                )
                content_to_position = torch.matmul(
                    query_layer, position_keys.transpose(-1, -2)
                )
                content_indices = torch.clamp(
                    relative_pos + attention_span,
                    0,
                    attention_span * 2 - 1,
                )
                score = score + torch.gather(
                    content_to_position,
                    dim=-1,
                    index=c2p_dynamic_expand(
                        content_indices, query_layer, relative_pos
                    ),
                )

            if "p2c" in self.pos_att_type:
                position_queries = self.transpose_for_scores(
                    self.pos_q_proj(relative_vectors)
                )
                divisor = float(
                    (config.hidden_size // config.num_attention_heads) * scale_factor
                ) ** 0.5
                position_queries = position_queries / divisor
                position_indices = torch.clamp(
                    -relative_pos + attention_span,
                    0,
                    attention_span * 2 - 1,
                )
                position_to_content = torch.matmul(
                    key_layer,
                    position_queries.transpose(-1, -2).to(dtype=key_layer.dtype),
                )
                score = score + torch.gather(
                    position_to_content,
                    dim=-1,
                    index=p2c_dynamic_expand(
                        position_indices, query_layer, key_layer
                    ),
                ).transpose(-1, -2)
            return score

    class PositionalBackbone(torch.nn.Module):
        def __init__(self) -> None:
            super().__init__()
            self.embeddings = DebertaEmbeddings(config)
            self.encoder = DebertaEncoder(config)
            for layer in self.encoder.layer:
                layer.attention.self.__class__ = FixedSpanDisentangledSelfAttention

        def forward(
            self,
            input_ids: object,
            attention_mask: object,
            token_type_ids: object,
            relative_pos: object,
        ) -> object:
            hidden = self.embeddings(
                input_ids=input_ids,
                token_type_ids=token_type_ids,
                mask=attention_mask,
            )
            return self.encoder(
                hidden,
                attention_mask,
                output_hidden_states=False,
                output_attentions=False,
                relative_pos=relative_pos,
                return_dict=False,
            )[0]

    class PositionalExtractor(torch.nn.Module):
        def __init__(self) -> None:
            super().__init__()
            self.model = PositionalBackbone()
            self.classification_head = torch.nn.Sequential(
                torch.nn.Dropout(0.0),
                torch.nn.Linear(config.hidden_size, 1, bias=False),
            )

        def forward(
            self,
            input_ids: object,
            attention_mask: object,
            token_type_ids: object,
            relative_pos: object,
        ) -> object:
            hidden = self.model(
                input_ids,
                attention_mask,
                token_type_ids,
                relative_pos,
            )
            logits = self.classification_head(hidden).squeeze(-1)
            # type_vocab_size is zero, but the stable four-input ABI is useful to
            # every planned ConSeC caller and mirrors the official batch schema.
            return logits + token_type_ids.to(dtype=logits.dtype) * 0.0

    with torch.device("meta"):
        model = PositionalExtractor()

    original_state = checkpoint["state_dict"]
    position_ids = original_state[POSITION_IDS_KEY]
    adapted_state = collections.OrderedDict(
        (key.removeprefix(STATE_PREFIX), value)
        for key, value in original_state.items()
        if key != POSITION_IDS_KEY
    )
    expected_state = model.state_dict()
    if set(adapted_state) != set(expected_state):
        raise ExportError(
            "Checkpoint/model key mismatch: "
            f"missing={sorted(set(expected_state) - set(adapted_state))}, "
            f"unexpected={sorted(set(adapted_state) - set(expected_state))}"
        )
    for name, expected in expected_state.items():
        actual = adapted_state[name]
        if tuple(actual.shape) != tuple(expected.shape) or actual.dtype != expected.dtype:
            raise ExportError(
                f"Checkpoint tensor mismatch for {name}: "
                f"{tuple(actual.shape)}/{actual.dtype} vs "
                f"{tuple(expected.shape)}/{expected.dtype}"
            )

    incompatible = model.load_state_dict(adapted_state, strict=True, assign=True)
    if incompatible.missing_keys or incompatible.unexpected_keys:
        raise ExportError(f"Strict state assignment failed: {incompatible}")
    model.model.embeddings.position_ids = position_ids
    model.eval()
    return model


def token_ids(tokenizer: object, text: str) -> list[int]:
    encoded = tokenizer(
        " " + text.strip(),
        add_special_tokens=True,
        return_attention_mask=False,
        return_token_type_ids=False,
    )["input_ids"]
    return list(encoded[1:-1])


def refined_definition(text: str) -> str:
    # Exact pinned ConsecDataset.refine_definitions behavior. Python
    # str.capitalize() lowercases the tail, which is material for acronym BPE.
    clean = text.capitalize().strip(".")
    if not clean:
        raise ExportError("A parity definition is empty")
    return "DEF_SEP " + clean + "."


def verify_capitalization_contract() -> None:
    cases = (
        (" PCR DNA", "DEF_SEP  pcr dna."),
        ("...PCR DNA", "DEF_SEP pcr dna."),
        ("\u00dfETA expansion", "DEF_SEP Sseta expansion."),
        ("\u01f3ETA titlecase", "DEF_SEP \u01f2eta titlecase."),
        ("\u039f\u03a3 contextual lowercase", "DEF_SEP \u039f\u03c2 contextual lowercase."),
    )
    for source, expected in cases:
        actual = refined_definition(source)
        if actual != expected:
            raise ExportError(
                "Python str.capitalize() contract changed for {!r}: {!r} != {!r}".format(
                    source, actual, expected
                )
            )


def build_fixture_inputs(
    tokenizer: object,
    sentence: Sequence[str],
    target_index: int,
    candidates: Sequence[str],
    feedback: Sequence[tuple[str, int]],
) -> tuple[dict[str, object], list[int]]:
    import torch

    if not 0 <= target_index < len(sentence):
        raise ExportError("Parity fixture target index is outside the sentence")
    if not candidates:
        raise ExportError("A parity fixture needs at least one candidate")

    marked_sentence = list(sentence)
    marked_sentence[target_index] = f"<d> {sentence[target_index]} </d>"
    sentence_ids = [tokenizer.cls_token_id]
    sentence_offsets: list[tuple[int, int]] = []
    for token in marked_sentence:
        piece = token_ids(tokenizer, token)
        sentence_offsets.append((len(sentence_ids), len(sentence_ids) + len(piece)))
        sentence_ids.extend(piece)
    sentence_ids.append(tokenizer.sep_token_id)

    candidate_ids = [token_ids(tokenizer, refined_definition(item)) for item in candidates]
    feedback_ids = [token_ids(tokenizer, refined_definition(item)) for item, _ in feedback]
    definition_blocks = candidate_ids + feedback_ids
    sequence_length = len(sentence_ids) + sum(map(len, definition_blocks)) + 1

    input_ids = list(sentence_ids)
    marker_positions: list[int] = []
    for piece in candidate_ids:
        marker_positions.append(len(input_ids))
        input_ids.extend(piece)
    for piece in feedback_ids:
        input_ids.extend(piece)
    input_ids.append(tokenizer.sep_token_id)
    if len(input_ids) != sequence_length:
        raise ExportError("Internal ConSeC fixture length mismatch")

    token_type_ids = [0] * len(sentence_ids) + [1] * (
        sequence_length - len(sentence_ids)
    )
    sentence_length = len(sentence_ids)
    relative_pos = torch.zeros(
        (sequence_length, sequence_length), dtype=torch.long
    )
    for sentence_position in range(sentence_length):
        values = [
            sentence_position - other_position
            for other_position in range(sentence_length)
        ]
        for definition_piece in definition_blocks:
            values.extend(
                sentence_position - sentence_length - definition_offset
                for definition_offset in range(len(definition_piece))
            )
        relative_pos[sentence_position, :-1] = torch.tensor(
            values, dtype=torch.long
        )
    relative_pos[:, :sentence_length] = (
        -relative_pos[:sentence_length, :].transpose(0, 1).clone()
    )

    links = [(target_index, piece, False) for piece in candidate_ids]
    links.extend((position, piece, True) for piece, (_, position) in zip(feedback_ids, feedback))
    current_offset = len(sentence_ids)
    for linked_token, definition_piece, symmetric in links:
        if not 0 <= linked_token < len(sentence_offsets):
            raise ExportError("Parity feedback position is outside the sentence")
        linked_start, linked_end = sentence_offsets[linked_token]
        linked_width = linked_end - linked_start
        for definition_offset in range(len(definition_piece)):
            definition_position = current_offset + definition_offset
            for linked_offset, linked_position in enumerate(
                range(linked_start, linked_end)
            ):
                distance = definition_offset + linked_width - linked_offset
                relative_pos[definition_position, linked_position] = distance
                if symmetric:
                    relative_pos[linked_position, definition_position] = -distance
            relative_pos[definition_position, sentence_length:-1] = (
                torch.arange(
                    current_offset + definition_offset - sentence_length,
                    current_offset + definition_offset - sentence_length
                    - sum(map(len, definition_blocks)),
                    -1,
                    dtype=torch.long,
                )
            )
        current_offset += len(definition_piece)

    last_token_positions = relative_pos.min(dim=-1).values - 1
    relative_pos[-1, :] = -last_token_positions
    relative_pos[:, -1] = last_token_positions
    relative_pos[-1, -1] = 0

    inputs = {
        "input_ids": torch.tensor([input_ids], dtype=torch.long),
        "attention_mask": torch.ones((1, sequence_length), dtype=torch.long),
        "token_type_ids": torch.tensor([token_type_ids], dtype=torch.long),
        "relative_pos": relative_pos.unsqueeze(0),
    }
    return inputs, marker_positions


def tensor_bundle_sha256(inputs: dict[str, object]) -> str:
    digest = hashlib.sha256()
    for name in ("input_ids", "attention_mask", "token_type_ids", "relative_pos"):
        tensor = inputs[name].detach().cpu().contiguous()
        digest.update(name.encode("ascii"))
        digest.update(str(tensor.dtype).encode("ascii"))
        digest.update(json.dumps(list(tensor.shape), separators=(",", ":")).encode("ascii"))
        digest.update(tensor.numpy().tobytes(order="C"))
    return digest.hexdigest()


def parity_fixture_specs() -> list[dict[str, object]]:
    return [
        {
            "id": "dialogue-feedback-bank",
            "sentence": ["She", "will", "bank", "on", "his", "support", "."],
            "targetIndex": 2,
            "candidates": [
                "a financial institution that accepts deposits",
                "to rely on someone or something",
            ],
            "feedback": [
                {
                    "definition": "help or encouragement given to someone",
                    "position": 5,
                }
            ],
        },
        {
            "id": "scientific-acronym-pcr-strain",
            "sentence": [
                "The",
                "PCR",
                "assay",
                "identified",
                "a",
                "resistant",
                "strain",
                ".",
            ],
            "targetIndex": 6,
            "candidates": [
                "a genetic variant or subtype of a microorganism",
                "physical deformation caused by stress",
                "an injury caused by excessive effort",
            ],
            "feedback": [
                {
                    "definition": "PCR DNA laboratory test",
                    "position": 1,
                },
                {
                    "definition": "a procedure that detects a target in a sample",
                    "position": 2,
                },
            ],
        },
    ]


def materialize_fixtures(tokenizer: object) -> list[dict[str, object]]:
    fixtures: list[dict[str, object]] = []
    for spec in parity_fixture_specs():
        feedback = [
            (str(item["definition"]), int(item["position"]))
            for item in spec["feedback"]
        ]
        inputs, marker_positions = build_fixture_inputs(
            tokenizer,
            typing.cast(Sequence[str], spec["sentence"]),
            int(spec["targetIndex"]),
            typing.cast(Sequence[str], spec["candidates"]),
            feedback,
        )
        fixtures.append(
            {
                "spec": spec,
                "inputs": inputs,
                "markerPositions": marker_positions,
                "inputSha256": tensor_bundle_sha256(inputs),
            }
        )
    return fixtures


def export_raw_onnx(model: object, sample_inputs: dict[str, object], path: Path) -> None:
    import torch

    torch.onnx.export(
        model,
        tuple(
            sample_inputs[name]
            for name in ("input_ids", "attention_mask", "token_type_ids", "relative_pos")
        ),
        path,
        input_names=["input_ids", "attention_mask", "token_type_ids", "relative_pos"],
        output_names=["token_logits"],
        dynamic_axes={
            "input_ids": {0: "batch", 1: "sequence"},
            "attention_mask": {0: "batch", 1: "sequence"},
            "token_type_ids": {0: "batch", 1: "sequence"},
            "relative_pos": {0: "batch", 1: "query_sequence", 2: "key_sequence"},
            "token_logits": {0: "batch", 1: "sequence"},
        },
        opset_version=ONNX_OPSET,
        do_constant_folding=True,
        dynamo=False,
    )


def convert_to_external_data(raw_path: Path, graph_path: Path, data_path: Path) -> None:
    import onnx

    model_proto = onnx.load_model(raw_path, load_external_data=True)
    onnx.save_model(
        model_proto,
        graph_path,
        save_as_external_data=True,
        all_tensors_to_one_file=True,
        location=data_path.name,
        size_threshold=0,
        convert_attribute=False,
    )
    del model_proto
    gc.collect()
    raw_path.unlink()

    if not graph_path.is_file() or not data_path.is_file():
        raise ExportError("ONNX external-data conversion did not produce both files")
    graph_without_data = onnx.load_model(graph_path, load_external_data=False)
    external_initializers = [
        initializer
        for initializer in graph_without_data.graph.initializer
        if initializer.data_location == onnx.TensorProto.EXTERNAL
    ]
    if not external_initializers:
        raise ExportError("The ONNX graph does not reference external initializer data")
    external_locations = {
        entry.value
        for initializer in external_initializers
        for entry in initializer.external_data
        if entry.key == "location"
    }
    if external_locations != {data_path.name}:
        raise ExportError(
            f"Unexpected ONNX external-data locations: {sorted(external_locations)}"
        )
    del graph_without_data
    gc.collect()
    onnx.checker.check_model(str(graph_path))


def run_parity(
    model: object,
    fixtures: list[dict[str, object]],
    graph_path: Path,
) -> list[dict[str, object]]:
    import numpy as np
    import onnxruntime as ort
    import torch

    expected_outputs: dict[str, object] = {}
    with torch.inference_mode():
        for fixture in fixtures:
            inputs = typing.cast(dict[str, object], fixture["inputs"])
            expected_outputs[str(fixture["spec"]["id"])] = model(
                inputs["input_ids"],
                inputs["attention_mask"],
                inputs["token_type_ids"],
                inputs["relative_pos"],
            ).detach().cpu().numpy()

    session_options = ort.SessionOptions()
    session_options.intra_op_num_threads = 1
    session_options.inter_op_num_threads = 1
    session = ort.InferenceSession(
        str(graph_path),
        sess_options=session_options,
        providers=["CPUExecutionProvider"],
    )
    if session.get_providers() != ["CPUExecutionProvider"]:
        raise ExportError(f"Unexpected ONNX Runtime providers: {session.get_providers()}")

    results: list[dict[str, object]] = []
    for fixture in fixtures:
        fixture_id = str(fixture["spec"]["id"])
        torch_inputs = typing.cast(dict[str, object], fixture["inputs"])
        ort_inputs = {
            name: tensor.detach().cpu().numpy()
            for name, tensor in torch_inputs.items()
        }
        actual = session.run(["token_logits"], ort_inputs)[0]
        expected = expected_outputs[fixture_id]
        if actual.shape != expected.shape:
            raise ExportError(
                f"ONNX parity shape mismatch for {fixture_id}: "
                f"{actual.shape} vs {expected.shape}"
            )
        delta = float(np.max(np.abs(actual - expected)))
        if not np.isfinite(delta) or delta > PARITY_TOLERANCE:
            raise ExportError(f"ONNX parity failed for {fixture_id}: {delta:.9g}")
        marker_positions = typing.cast(list[int], fixture["markerPositions"])
        candidate_logits = [float(actual[0, position]) for position in marker_positions]
        results.append(
            {
                "id": fixture_id,
                "maximumAbsoluteDelta": delta,
                "candidateLogits": candidate_logits,
            }
        )
        print(
            f"{fixture_id}: ONNX parity max abs delta {delta:.9g}; "
            f"candidate logits={candidate_logits}",
            flush=True,
        )
    return results


def run_stock_attention_parity(
    model: object,
    fixtures: list[dict[str, object]],
) -> list[dict[str, object]]:
    import numpy as np
    import torch

    attention_modules = [
        layer.attention.self for layer in model.model.encoder.layer
    ]
    adapted_classes = [module.__class__ for module in attention_modules]
    if not all(cls.__name__ == "FixedSpanDisentangledSelfAttention" for cls in adapted_classes):
        raise ExportError("Cannot identify the fixed-span attention adaptation")
    stock_classes = [cls.__bases__[0] for cls in adapted_classes]
    if not all(cls.__name__ == "DisentangledSelfAttention" for cls in stock_classes):
        raise ExportError("Cannot identify the stock Transformers attention reference")

    with torch.inference_mode():
        adapted_outputs = {
            str(fixture["spec"]["id"]): model(
                **typing.cast(dict[str, object], fixture["inputs"])
            ).detach().cpu().numpy()
            for fixture in fixtures
        }
    try:
        for module, stock_class in zip(attention_modules, stock_classes):
            module.__class__ = stock_class
        with torch.inference_mode():
            stock_outputs = {
                str(fixture["spec"]["id"]): model(
                    **typing.cast(dict[str, object], fixture["inputs"])
                ).detach().cpu().numpy()
                for fixture in fixtures
            }
    finally:
        for module, adapted_class in zip(attention_modules, adapted_classes):
            module.__class__ = adapted_class

    results: list[dict[str, object]] = []
    for fixture_id, adapted in adapted_outputs.items():
        stock = stock_outputs[fixture_id]
        delta = float(np.max(np.abs(adapted - stock)))
        if not np.isfinite(delta) or delta > 1e-6:
            raise ExportError(
                f"Fixed-span attention differs from stock Transformers for "
                f"{fixture_id}: {delta:.9g}"
            )
        fixture = next(
            item for item in fixtures if str(item["spec"]["id"]) == fixture_id
        )
        results.append(
            {
                "id": fixture_id,
                "sequenceLength": int(fixture["inputs"]["input_ids"].shape[1]),
                "maximumAbsoluteDelta": delta,
            }
        )
        print(
            f"{fixture_id}: fixed-span vs stock attention max abs delta "
            f"{delta:.9g}",
            flush=True,
        )
    return results


def write_json(path: Path, payload: object) -> None:
    path.write_text(
        json.dumps(payload, ensure_ascii=True, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
        newline="\n",
    )


def export_bundle(
    checkpoint: dict[str, Any],
    inspection: dict[str, object],
    base_model_source: Path,
    output: Path,
) -> dict[str, object]:
    environment = verify_export_environment()
    base_records = verify_base_model_source(base_model_source)
    tokenizer = create_tokenizer(base_model_source)
    fixtures = materialize_fixtures(tokenizer)
    model = create_model(checkpoint, base_model_source)
    stock_attention_parity = run_stock_attention_parity(model, fixtures)

    if output.exists():
        raise ExportError(f"Output must not already exist: {output}")
    output.parent.mkdir(parents=True, exist_ok=True)
    staging_root = Path(
        tempfile.mkdtemp(prefix=".consec-export-", dir=str(output.parent))
    )
    staging = staging_root / "bundle"
    cpu = staging / "Cpu"
    cpu.mkdir(parents=True)
    raw_graph = cpu / (GRAPH_NAME + ".raw")
    graph_path = cpu / GRAPH_NAME
    data_path = cpu / EXTERNAL_DATA_NAME
    tokenizer_path = staging / TOKENIZER_NAME

    try:
        print("Exporting the pinned ConSeC positional extractor...", flush=True)
        export_raw_onnx(model, typing.cast(dict[str, object], fixtures[0]["inputs"]), raw_graph)
        print("Converting ONNX initializers to one external-data file...", flush=True)
        convert_to_external_data(raw_graph, graph_path, data_path)
        parity_results = run_parity(model, fixtures, graph_path)

        tokenizer.backend_tokenizer.save(str(tokenizer_path))
        reloaded = type(tokenizer)(tokenizer_file=str(tokenizer_path))
        if [reloaded.convert_tokens_to_ids(token) for token in SPECIAL_TOKENS] != list(
            EXPECTED_SPECIAL_TOKEN_IDS
        ):
            raise ExportError("Saved tokenizer did not preserve ConSeC token IDs")

        artifact_records = [
            file_record(graph_path, staging),
            file_record(data_path, staging),
            file_record(tokenizer_path, staging),
        ]
        fixture_records = [
            {
                "id": fixture["spec"]["id"],
                "inputSha256": fixture["inputSha256"],
                "sequenceLength": int(
                    fixture["inputs"]["input_ids"].shape[1]
                ),
                "candidateCount": len(fixture["markerPositions"]),
                "hasFeedbackContext": bool(fixture["spec"]["feedback"]),
            }
            for fixture in fixtures
        ]
        manifest = {
            "schemaVersion": 1,
            "adapterId": "consec-wngt-positional-extractor-v1",
            "license": UPSTREAM_LICENSE,
            "commercialUseAllowed": False,
            "upstreamRepository": UPSTREAM_REPOSITORY,
            "upstreamCommit": UPSTREAM_COMMIT,
            "checkpoint": inspection["checkpoint"],
            "baseModel": {
                "repository": BASE_MODEL_REPOSITORY,
                "revision": BASE_MODEL_REVISION,
                "sourceFiles": base_records,
            },
            "environment": environment,
            "model": {
                "format": "ONNX external data",
                "precision": "float32",
                "opset": ONNX_OPSET,
                "inputs": [
                    "input_ids",
                    "attention_mask",
                    "token_type_ids",
                    "relative_pos",
                ],
                "output": "token_logits",
                "stateEntries": inspection["stateEntries"],
                "tensorElements": inspection["tensorElements"],
                "embeddingShape": inspection["embeddingShape"],
                "encoderLayers": inspection["encoderLayers"],
                "relativeAttentionAdapter": "fixed-max-span-onnx-v1",
            },
            "tokenizer": {
                "specialTokens": [
                    {"token": token, "id": token_id}
                    for token, token_id in zip(
                        SPECIAL_TOKENS, EXPECTED_SPECIAL_TOKEN_IDS
                    )
                ]
            },
            "artifacts": artifact_records,
            "parity": {
                "scope": "adapted-pytorch-to-onnx",
                "provider": "CPUExecutionProvider",
                "maximumAbsoluteDeltaTolerance": PARITY_TOLERANCE,
                "fixtures": fixture_records,
                "observed": parity_results,
                "stockTransformersAttention": {
                    "scope": "transformers-4.46.3-stock-to-fixed-span-adapter",
                    "maximumAbsoluteDeltaTolerance": 1e-6,
                    "observed": stock_attention_parity,
                },
                "officialUpstreamEndToEndReferenceVerified": False,
            },
        }
        write_json(staging / MANIFEST_NAME, manifest)
        os.replace(staging, output)
        print(
            json.dumps(
                {
                    "output": str(output),
                    "manifest": manifest,
                    "observedParity": parity_results,
                },
                ensure_ascii=False,
                indent=2,
                sort_keys=True,
            ),
            flush=True,
        )
        return manifest
    finally:
        shutil.rmtree(staging_root, ignore_errors=True)


def parse_args(argv: Sequence[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Safely inspect or export the pinned official ConSeC WNGT checkpoint."
    )
    parser.add_argument("--upstream-directory", required=True, type=Path)
    parser.add_argument("--checkpoint", required=True, type=Path)
    parser.add_argument("--inspect-only", action="store_true")
    parser.add_argument("--base-model-directory", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args(argv)
    if args.inspect_only:
        if args.output is not None:
            parser.error("--output is not valid with --inspect-only")
    elif args.base_model_directory is None or args.output is None:
        parser.error("export requires --base-model-directory and --output")
    return args


def main(argv: Sequence[str] | None = None) -> int:
    args = parse_args(sys.argv[1:] if argv is None else argv)
    try:
        verify_capitalization_contract()
        upstream = args.upstream_directory.resolve(strict=True)
        checkpoint_path = args.checkpoint.resolve(strict=True)
        verify_upstream_checkout(upstream)
        checkpoint, pickle_globals = safe_load_checkpoint(checkpoint_path)
        inspection = validate_checkpoint(checkpoint, pickle_globals)
        if args.inspect_only:
            print(json.dumps(inspection, ensure_ascii=False, indent=2, sort_keys=True))
            return 0

        base_model_source = args.base_model_directory.resolve(strict=True)
        output = args.output.resolve()
        export_bundle(
            checkpoint,
            inspection,
            base_model_source,
            output,
        )
        return 0
    except (ExportError, FileNotFoundError, OSError, ValueError) as exc:
        print(f"ConSeC export failed: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
