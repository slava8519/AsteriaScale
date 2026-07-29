#!/usr/bin/env python3
"""Fail-closed ConSeC upstream-to-ONNX reference gate.

This runner imports preprocessing and model code from the pinned official
ConSeC checkout. It never imports Lightning and never lets the historical
DeBERTa tokenizer perform its implicit unsafe torch.load.
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
import subprocess
import sys
import tempfile
import types
import typing
import zipfile
from pathlib import Path


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
    # Transformers 4.3.3 uses the legacy official tokenizer asset rather than
    # vocab.json/merges.txt. It is pinned independently and loaded through a
    # guarded weights-only torch.load below.
    "bpe_encoder.bin": (
        3_917_897,
        "e7c6f9eecb461c01e09c00656ccf3e27944b9e74bfe29e51632b13d3cd9d6c8e",
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

EXPECTED_PYTHON = (3, 9, 13)
EXPECTED_VERSIONS = {
    "torch": "2.5.1",
    "transformers": "4.3.3",
    "tokenizers": "0.10.3",
    "onnxruntime": "1.19.2",
    "numpy": "1.26.4",
    "packaging": "20.9",
    "six": "1.16.0",
}
EXPECTED_STATE_ENTRIES = 390
EXPECTED_TENSOR_ELEMENTS = 405_372_416
EXPECTED_EMBEDDING_SHAPE = (50_468, 1_024)
EXPECTED_LAYER_COUNT = 24
STATE_PREFIX = "sense_extractor."
POSITION_IDS_KEY = STATE_PREFIX + "model.embeddings.position_ids"
SPECIAL_TOKENS = ("<d>", "</d>", "DEF_SEP", "DEF_END", "CONTEXT_DEFS")
EXPECTED_SPECIAL_TOKEN_IDS = (50_265, 50_266, 50_267, 50_268, 50_269)
REFERENCE_SOURCE_FILES = (
    "src/consec_tokenizer.py",
    "src/sense_extractors.py",
    "src/utils/collections.py",
    "conf/tokenizer/deberta.yaml",
    "conf/model/deberta.yaml",
)
PARITY_TOLERANCE = 1e-4


class ReferenceError(RuntimeError):
    """A fail-closed reference validation failure."""


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(8 * 1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def verify_exact_file(path, expected_bytes, expected_sha256):
    if not path.is_file():
        raise ReferenceError("Required file is missing: {}".format(path))
    actual_bytes = path.stat().st_size
    if actual_bytes != expected_bytes:
        raise ReferenceError(
            "Unexpected size for {}: {}; expected {}".format(
                path.name, actual_bytes, expected_bytes
            )
        )
    actual_sha256 = sha256(path)
    if actual_sha256.lower() != expected_sha256.lower():
        raise ReferenceError(
            "Unexpected SHA-256 for {}: {}; expected {}".format(
                path.name, actual_sha256, expected_sha256
            )
        )


def run_git(upstream, *arguments):
    completed = subprocess.run(
        ["git", "-C", str(upstream), *arguments],
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
    )
    if completed.returncode != 0:
        raise ReferenceError(
            "git {} failed: {}".format(" ".join(arguments), completed.stderr.strip())
        )
    return completed.stdout.strip()


def verify_upstream(upstream):
    if not (upstream / ".git").exists():
        raise ReferenceError("Not a Git checkout: {}".format(upstream))
    revision = run_git(upstream, "rev-parse", "HEAD").lower()
    if revision != UPSTREAM_COMMIT:
        raise ReferenceError(
            "Unexpected ConSeC revision: {}; expected {}".format(
                revision, UPSTREAM_COMMIT
            )
        )
    completed = subprocess.run(
        ["git", "-C", str(upstream), "diff", "--quiet", "HEAD", "--", *REFERENCE_SOURCE_FILES],
        check=False,
    )
    if completed.returncode != 0:
        raise ReferenceError("Pinned ConSeC reference sources have local modifications")
    license_path = upstream / "LICENSE.txt"
    if not license_path.is_file():
        raise ReferenceError("Upstream license is missing")
    first_line = license_path.read_text(encoding="utf-8").splitlines()[0].strip()
    if first_line != "Attribution-NonCommercial-ShareAlike 4.0 International":
        raise ReferenceError("Unexpected upstream license header: {!r}".format(first_line))
    return [
        {
            "file": relative,
            "bytes": (upstream / relative).stat().st_size,
            "sha256": sha256(upstream / relative),
        }
        for relative in REFERENCE_SOURCE_FILES
    ]


def verify_environment():
    import numpy
    import onnxruntime
    import packaging
    import six
    import tokenizers
    import torch
    import transformers

    if tuple(sys.version_info[:3]) != EXPECTED_PYTHON:
        raise ReferenceError(
            "Python {} is not the pinned {}".format(
                ".".join(map(str, sys.version_info[:3])),
                ".".join(map(str, EXPECTED_PYTHON)),
            )
        )
    actual = {
        "python": ".".join(map(str, sys.version_info[:3])),
        "torch": str(torch.__version__).split("+", 1)[0],
        "transformers": transformers.__version__,
        "tokenizers": tokenizers.__version__,
        "onnxruntime": onnxruntime.__version__,
        "numpy": numpy.__version__,
        "packaging": packaging.__version__,
        "six": six.__version__,
    }
    mismatches = {
        name: {"actual": actual[name], "expected": expected}
        for name, expected in EXPECTED_VERSIONS.items()
        if actual[name] != expected
    }
    if mismatches:
        raise ReferenceError("Unpinned reference environment: {}".format(mismatches))
    signature = inspect.signature(torch.load)
    if "weights_only" not in signature.parameters or "mmap" not in signature.parameters:
        raise ReferenceError("torch.load lacks required safe-loading parameters")
    if not hasattr(torch.serialization, "safe_globals"):
        raise ReferenceError("torch.serialization.safe_globals is unavailable")
    return actual


def inspect_pickle_globals(checkpoint_path):
    try:
        with zipfile.ZipFile(checkpoint_path) as archive:
            members = [
                member
                for member in archive.infolist()
                if member.filename.endswith("/data.pkl")
            ]
            if len(members) != 1:
                raise ReferenceError(
                    "The checkpoint must contain exactly one PyTorch data.pkl member"
                )
            pickle_data = archive.read(members[0])
    except zipfile.BadZipFile as exc:
        raise ReferenceError("The checkpoint is not a PyTorch ZIP archive") from exc

    globals_seen = []
    for opcode, argument, _ in pickletools.genops(pickle_data):
        if opcode.name == "GLOBAL":
            globals_seen.append(str(argument))
        elif opcode.name in {"STACK_GLOBAL", "EXT1", "EXT2", "EXT4"}:
            raise ReferenceError(
                "Unsupported pickle opcode in checkpoint: {}".format(opcode.name)
            )
    actual = frozenset(globals_seen)
    if actual != EXPECTED_PICKLE_GLOBALS or len(globals_seen) != len(
        EXPECTED_PICKLE_GLOBALS
    ):
        raise ReferenceError(
            "Checkpoint pickle globals differ from the audited allowlist; "
            "missing={}, unexpected={}, count={}".format(
                sorted(EXPECTED_PICKLE_GLOBALS - actual),
                sorted(actual - EXPECTED_PICKLE_GLOBALS),
                len(globals_seen),
            )
        )
    return sorted(actual)


def inert_surrogate(module, name):
    surrogate = types.new_class(name, (object,))
    surrogate.__module__ = module
    surrogate.__qualname__ = name
    return surrogate


def checkpoint_safe_globals():
    return [
        inert_surrogate(
            "pytorch_lightning.callbacks.early_stopping", "EarlyStopping"
        ),
        inert_surrogate("src.pl_callbacks", "ModelCheckpointWithBest"),
        inert_surrogate("omegaconf.dictconfig", "DictConfig"),
        inert_surrogate("omegaconf.base", "ContainerMetadata"),
        # Python 3.9 represents typing.Any as a _SpecialForm without __name__,
        # which torch safe_globals cannot index. The inert qualname is enough
        # for legacy metadata and preserves the audited pickle global exactly.
        inert_surrogate("typing", "Any"),
        collections.defaultdict,
        inert_surrogate("omegaconf.nodes", "AnyNode"),
        inert_surrogate("omegaconf.base", "Metadata"),
        inert_surrogate("omegaconf.listconfig", "ListConfig"),
        dict,
        list,
        int,
    ]


def safe_load_checkpoint(checkpoint_path):
    import torch

    verify_exact_file(checkpoint_path, CHECKPOINT_BYTES, CHECKPOINT_SHA256)
    pickle_globals = inspect_pickle_globals(checkpoint_path)
    with torch.serialization.safe_globals(checkpoint_safe_globals()):
        checkpoint = torch.load(
            checkpoint_path,
            map_location="cpu",
            weights_only=True,
            mmap=True,
        )
    if type(checkpoint) is not dict:
        raise ReferenceError("Unexpected checkpoint root type: {!r}".format(type(checkpoint)))
    state = checkpoint.get("state_dict")
    if not isinstance(state, collections.OrderedDict):
        raise ReferenceError("Checkpoint state_dict is not an OrderedDict")
    if len(state) != EXPECTED_STATE_ENTRIES:
        raise ReferenceError("Unexpected checkpoint state entry count: {}".format(len(state)))
    if not all(isinstance(value, torch.Tensor) for value in state.values()):
        raise ReferenceError("Checkpoint state_dict contains a non-tensor value")
    elements = sum(value.numel() for value in state.values())
    if elements != EXPECTED_TENSOR_ELEMENTS:
        raise ReferenceError("Unexpected checkpoint tensor element count: {}".format(elements))
    embedding = state.get(STATE_PREFIX + "model.embeddings.word_embeddings.weight")
    if embedding is None or tuple(embedding.shape) != EXPECTED_EMBEDDING_SHAPE:
        raise ReferenceError("Unexpected checkpoint embedding shape")
    layers = sorted(
        {
            int(match.group(1))
            for key in state
            for match in [
                re.match(r"^sense_extractor\.model\.encoder\.layer\.(\d+)\.", key)
            ]
            if match is not None
        }
    )
    if layers != list(range(EXPECTED_LAYER_COUNT)):
        raise ReferenceError("Unexpected checkpoint encoder layers: {}".format(layers))
    return checkpoint, pickle_globals


def verify_base_model(base_model):
    records = []
    for name, (expected_bytes, expected_sha256) in BASE_MODEL_FILES.items():
        path = base_model / name
        verify_exact_file(path, expected_bytes, expected_sha256)
        records.append(
            {
                "file": name,
                "bytes": expected_bytes,
                "sha256": expected_sha256,
            }
        )
    return records


def verify_imported_reference_modules(upstream):
    import src.consec_tokenizer as consec_tokenizer
    import src.sense_extractors as sense_extractors
    import src.utils.collections as collection_utils

    modules = {
        "src.consec_tokenizer": (consec_tokenizer, "src/consec_tokenizer.py"),
        "src.sense_extractors": (sense_extractors, "src/sense_extractors.py"),
        "src.utils.collections": (collection_utils, "src/utils/collections.py"),
    }
    records = []
    for name, (module, relative) in modules.items():
        actual = Path(module.__file__).resolve(strict=True)
        expected = (upstream / relative).resolve(strict=True)
        if actual != expected:
            raise ReferenceError(
                "Official module {} resolved outside the pinned checkout: {}".format(
                    name, actual
                )
            )
        records.append({"module": name, "file": relative})
    return records


def guarded_official_tokenizer(base_model):
    import torch

    from src.consec_tokenizer import DeBERTaTokenizer

    bpe_path = (base_model / "bpe_encoder.bin").resolve(strict=True)
    original_load = torch.load
    calls = []

    def weights_only_bpe_load(path, *args, **kwargs):
        actual_path = Path(path).resolve(strict=True)
        if actual_path != bpe_path or args or kwargs:
            raise ReferenceError(
                "Historical tokenizer attempted an unexpected torch.load: {}".format(
                    actual_path
                )
            )
        calls.append(str(actual_path))
        return original_load(
            actual_path,
            map_location="cpu",
            weights_only=True,
        )

    torch.load = weights_only_bpe_load
    try:
        tokenizer = DeBERTaTokenizer(
            str(base_model),
            target_marker=("<d>", "</d>"),
            context_definitions_token="CONTEXT_DEFS",
            context_markers={"number": 1, "pattern": ["DEF_SEP", "DEF_END"]},
            add_prefix_space=True,
            optimize_relative_positions=True,
            enforce_symmetry=False,
        )
    finally:
        torch.load = original_load
    if calls != [str(bpe_path)]:
        raise ReferenceError("Historical tokenizer did not load its pinned BPE exactly once")
    token_ids = tuple(
        tokenizer.tokenizer.convert_tokens_to_ids(token) for token in SPECIAL_TOKENS
    )
    if tokenizer.n_added_special_tokens != len(SPECIAL_TOKENS):
        raise ReferenceError("Unexpected number of ConSeC special tokens")
    if token_ids != EXPECTED_SPECIAL_TOKEN_IDS:
        raise ReferenceError(
            "Unexpected ConSeC special token IDs: {}".format(token_ids)
        )
    return tokenizer


def fixture_specs():
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


def refined_definition(text):
    # This is intentionally the exact relative-positions branch from the pinned
    # official ConsecDataset.refine_definitions implementation.
    return "DEF_SEP {}.".format(text.capitalize().strip("."))


def verify_capitalization_contract():
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
            raise ReferenceError(
                "Python str.capitalize() contract changed for {!r}: {!r} != {!r}".format(
                    source, actual, expected
                )
            )


def tensor_bundle_sha256(inputs):
    digest = hashlib.sha256()
    for name in ("input_ids", "attention_mask", "token_type_ids", "relative_pos"):
        tensor = inputs[name].detach().cpu().contiguous()
        digest.update(name.encode("ascii"))
        digest.update(str(tensor.dtype).encode("ascii"))
        digest.update(json.dumps(list(tensor.shape), separators=(",", ":")).encode("ascii"))
        digest.update(tensor.numpy().tobytes(order="C"))
    return digest.hexdigest()


def materialize_official_fixtures(tokenizer):
    fixtures = []
    for spec in fixture_specs():
        sentence = list(spec["sentence"])
        target_index = int(spec["targetIndex"])
        sentence[target_index] = tokenizer.mark_token(
            sentence[target_index], marker=tokenizer.target_marker
        )
        candidates = [refined_definition(item) for item in spec["candidates"]]
        feedback = [
            (refined_definition(item["definition"]), int(item["position"]))
            for item in spec["feedback"]
        ]
        output = tokenizer.tokenize(sentence, target_index, candidates, feedback)
        marker_positions = [output.definitions_offsets[item][0] for item in candidates]
        inputs = {
            "input_ids": output.input_ids.unsqueeze(0),
            "attention_mask": output.attention_mask.unsqueeze(0),
            "token_type_ids": output.token_type_ids.unsqueeze(0),
            "relative_pos": output.relative_positions.unsqueeze(0),
        }
        fixtures.append(
            {
                "spec": spec,
                "inputs": inputs,
                "markerPositions": marker_positions,
                "inputSha256": tensor_bundle_sha256(inputs),
            }
        )
    return fixtures


def load_bundle(bundle):
    manifest_path = bundle / "model-manifest.json"
    if not manifest_path.is_file():
        raise ReferenceError("ConSeC bundle manifest is missing")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    expected_values = {
        "schemaVersion": 1,
        "adapterId": "consec-wngt-positional-extractor-v1",
        "license": UPSTREAM_LICENSE,
        "commercialUseAllowed": False,
        "upstreamRepository": UPSTREAM_REPOSITORY,
        "upstreamCommit": UPSTREAM_COMMIT,
    }
    for name, expected in expected_values.items():
        if manifest.get(name) != expected:
            raise ReferenceError(
                "Unexpected bundle manifest {}: {!r}".format(name, manifest.get(name))
            )
    checkpoint = manifest.get("checkpoint", {})
    if (
        checkpoint.get("bytes") != CHECKPOINT_BYTES
        or checkpoint.get("sha256") != CHECKPOINT_SHA256
    ):
        raise ReferenceError("Bundle checkpoint provenance does not match")
    base_model = manifest.get("baseModel", {})
    if (
        base_model.get("repository") != BASE_MODEL_REPOSITORY
        or base_model.get("revision") != BASE_MODEL_REVISION
    ):
        raise ReferenceError("Bundle base-model provenance does not match")
    artifacts = manifest.get("artifacts")
    if not isinstance(artifacts, list) or len(artifacts) != 3:
        raise ReferenceError("Unexpected bundle artifact manifest")
    verified_artifacts = []
    bundle_resolved = bundle.resolve(strict=True)
    for record in artifacts:
        relative = str(record.get("file", ""))
        path = (bundle / relative).resolve(strict=True)
        if bundle_resolved not in path.parents:
            raise ReferenceError("Bundle artifact escapes its root: {}".format(relative))
        verify_exact_file(path, int(record["bytes"]), str(record["sha256"]))
        verified_artifacts.append(dict(record))
    graph_path = bundle / "Cpu" / "consec-wngt-fp32.onnx"
    if graph_path.resolve(strict=True) not in [
        (bundle / str(record["file"])).resolve(strict=True) for record in artifacts
    ]:
        raise ReferenceError("Pinned ONNX graph is absent from artifact manifest")
    return manifest, graph_path, sha256(manifest_path), verified_artifacts


def manifest_fixture_records(manifest):
    parity = manifest.get("parity", {})
    records = parity.get("fixtures")
    if not isinstance(records, list):
        raise ReferenceError("Bundle parity fixtures are missing")
    by_id = {str(record.get("id")): record for record in records}
    if set(by_id) != {spec["id"] for spec in fixture_specs()}:
        raise ReferenceError("Bundle parity fixture IDs do not match the reference")
    return by_id


def verify_preprocessing(fixtures, manifest):
    records = manifest_fixture_records(manifest)
    for fixture in fixtures:
        fixture_id = fixture["spec"]["id"]
        record = records[fixture_id]
        expected_count = len(fixture["spec"]["candidates"])
        if record.get("inputSha256") != fixture["inputSha256"]:
            raise ReferenceError(
                "Official preprocessing differs from exported input for {}: {} vs {}".format(
                    fixture_id, fixture["inputSha256"], record.get("inputSha256")
                )
            )
        if record.get("sequenceLength") != int(fixture["inputs"]["input_ids"].shape[1]):
            raise ReferenceError("Sequence length differs for {}".format(fixture_id))
        if record.get("candidateCount") != expected_count:
            raise ReferenceError("Candidate count differs for {}".format(fixture_id))


def create_official_model(checkpoint, base_model):
    import torch
    from transformers import AutoConfig

    from src.sense_extractors import ConsecDebertaModel, DebertaPositionalExtractor

    config = AutoConfig.from_pretrained(str(base_model), local_files_only=True)
    expectations = {
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
    for name, expected in expectations.items():
        actual = getattr(config, name, None)
        if actual != expected:
            raise ReferenceError(
                "Unexpected DeBERTa config {}: {!r}".format(name, actual)
            )
    # Official Lightning constructs the base model and immediately resizes it by
    # 203 entries. Building the final shape on meta avoids allocating irrelevant
    # base weights; every inference tensor is then assigned strictly from the
    # audited checkpoint.
    config.vocab_size = EXPECTED_EMBEDDING_SHAPE[0]
    original_from_pretrained = ConsecDebertaModel.from_pretrained

    def config_only_from_pretrained(cls, *args, **kwargs):
        return cls(config)

    ConsecDebertaModel.from_pretrained = classmethod(config_only_from_pretrained)
    try:
        with torch.device("meta"):
            model = DebertaPositionalExtractor(str(base_model), 0.0, True)
    finally:
        ConsecDebertaModel.from_pretrained = original_from_pretrained

    adapted_state = collections.OrderedDict(
        (key[len(STATE_PREFIX) :], value)
        for key, value in checkpoint["state_dict"].items()
    )
    expected_state = model.state_dict()
    if set(adapted_state) != set(expected_state):
        raise ReferenceError(
            "Official checkpoint/model key mismatch: missing={}, unexpected={}".format(
                sorted(set(expected_state) - set(adapted_state)),
                sorted(set(adapted_state) - set(expected_state)),
            )
        )
    for name, expected in expected_state.items():
        actual = adapted_state[name]
        if tuple(actual.shape) != tuple(expected.shape) or actual.dtype != expected.dtype:
            raise ReferenceError(
                "Official checkpoint tensor mismatch for {}".format(name)
            )
    incompatible = model.load_state_dict(adapted_state, strict=True, assign=True)
    if incompatible.missing_keys or incompatible.unexpected_keys:
        raise ReferenceError("Strict official state assignment failed")
    model.eval()
    return model


def run_official(model, fixtures):
    import torch

    results = {}
    with torch.inference_mode():
        for fixture in fixtures:
            inputs = fixture["inputs"]
            logits = model.forward(
                inputs["input_ids"],
                inputs["attention_mask"],
                inputs["token_type_ids"],
                inputs["relative_pos"],
            ).detach().cpu().numpy()
            results[fixture["spec"]["id"]] = logits
    return results


def run_onnx(graph_path, fixtures):
    import onnxruntime as ort

    options = ort.SessionOptions()
    options.intra_op_num_threads = 1
    options.inter_op_num_threads = 1
    session = ort.InferenceSession(
        str(graph_path),
        sess_options=options,
        providers=["CPUExecutionProvider"],
    )
    if session.get_providers() != ["CPUExecutionProvider"]:
        raise ReferenceError(
            "Unexpected ONNX Runtime providers: {}".format(session.get_providers())
        )
    results = {}
    for fixture in fixtures:
        inputs = {
            name: tensor.detach().cpu().numpy()
            for name, tensor in fixture["inputs"].items()
        }
        results[fixture["spec"]["id"]] = session.run(
            ["token_logits"], inputs
        )[0]
    return results


def compare_outputs(fixtures, manifest, official_outputs, onnx_outputs):
    import numpy as np

    parity = manifest.get("parity", {})
    if parity.get("scope") != "adapted-pytorch-to-onnx":
        raise ReferenceError("Bundle parity scope is not adapted-pytorch-to-onnx")
    if parity.get("maximumAbsoluteDeltaTolerance") != PARITY_TOLERANCE:
        raise ReferenceError("Bundle parity tolerance does not match the reference gate")
    observed_records = parity.get("observed")
    if not isinstance(observed_records, list):
        raise ReferenceError("Bundle parity.observed is missing")
    manifest_observed = {}
    for record in observed_records:
        fixture_id = str(record.get("id", ""))
        if fixture_id in manifest_observed:
            raise ReferenceError(
                "Duplicate bundle parity.observed fixture: {}".format(fixture_id)
            )
        manifest_observed[fixture_id] = record
    if set(manifest_observed) != {spec["id"] for spec in fixture_specs()}:
        raise ReferenceError("Bundle parity.observed fixture IDs do not match")
    results = []
    for fixture in fixtures:
        fixture_id = fixture["spec"]["id"]
        official = official_outputs[fixture_id]
        onnx = onnx_outputs[fixture_id]
        if official.shape != onnx.shape:
            raise ReferenceError(
                "Official/ONNX output shape differs for {}".format(fixture_id)
            )
        maximum_delta = float(np.max(np.abs(official - onnx)))
        marker_positions = fixture["markerPositions"]
        official_candidates = [float(official[0, pos]) for pos in marker_positions]
        onnx_candidates = [float(onnx[0, pos]) for pos in marker_positions]
        candidate_delta = float(
            np.max(np.abs(np.asarray(official_candidates) - np.asarray(onnx_candidates)))
        )
        if not np.isfinite(maximum_delta) or maximum_delta > PARITY_TOLERANCE:
            raise ReferenceError(
                "Official/ONNX parity failed for {}: {:.9g}".format(
                    fixture_id, maximum_delta
                )
            )
        official_choice = int(np.argmax(official_candidates))
        onnx_choice = int(np.argmax(onnx_candidates))
        if official_choice != onnx_choice:
            raise ReferenceError("Official/ONNX candidate choice differs for {}".format(fixture_id))
        observed = manifest_observed.get(fixture_id)
        if observed is None:
            raise ReferenceError("Bundle has no observed parity for {}".format(fixture_id))
        recorded_candidates = np.asarray(observed.get("candidateLogits", []), dtype=np.float64)
        if recorded_candidates.shape != np.asarray(onnx_candidates).shape:
            raise ReferenceError("Bundle candidate logit count differs for {}".format(fixture_id))
        current_vs_recorded_candidate_delta = float(
            np.max(np.abs(recorded_candidates - np.asarray(onnx_candidates)))
        )
        if (
            not np.isfinite(current_vs_recorded_candidate_delta)
            or current_vs_recorded_candidate_delta > PARITY_TOLERANCE
        ):
            raise ReferenceError(
                "Current ONNX output differs from bundle evidence for {}: {:.9g}".format(
                    fixture_id, current_vs_recorded_candidate_delta
                )
            )
        recorded_exporter_delta = float(observed.get("maximumAbsoluteDelta", float("nan")))
        exporter_delta_difference = abs(recorded_exporter_delta - maximum_delta)
        if (
            not np.isfinite(recorded_exporter_delta)
            or recorded_exporter_delta > PARITY_TOLERANCE
            or exporter_delta_difference > 1e-9
        ):
            raise ReferenceError(
                "Official delta differs from parity.observed for {}: current={:.9g}, "
                "recorded={:.9g}".format(
                    fixture_id, maximum_delta, recorded_exporter_delta
                )
            )
        results.append(
            {
                "id": fixture_id,
                "inputSha256": fixture["inputSha256"],
                "sequenceLength": int(fixture["inputs"]["input_ids"].shape[1]),
                "markerPositions": marker_positions,
                "officialCandidateLogits": official_candidates,
                "onnxCandidateLogits": onnx_candidates,
                "officialChoice": official_choice,
                "onnxChoice": onnx_choice,
                "maximumAbsoluteDelta": maximum_delta,
                "candidateMaximumAbsoluteDelta": candidate_delta,
                "recordedExporterMaximumAbsoluteDelta": recorded_exporter_delta,
                "officialVsRecordedExporterDeltaDifference": exporter_delta_difference,
                "currentOnnxVsRecordedCandidateLogitsMaximumAbsoluteDelta": (
                    current_vs_recorded_candidate_delta
                ),
            }
        )
        print(
            "{}: official vs ONNX max abs delta {:.9g}; candidate delta {:.9g}".format(
                fixture_id, maximum_delta, candidate_delta
            ),
            flush=True,
        )
    return results


def write_json_atomic(path, payload):
    path.parent.mkdir(parents=True, exist_ok=True)
    handle, temporary_name = tempfile.mkstemp(
        prefix="." + path.name + ".", suffix=".tmp", dir=str(path.parent)
    )
    os.close(handle)
    temporary = Path(temporary_name)
    try:
        temporary.write_text(
            json.dumps(payload, ensure_ascii=True, indent=2, sort_keys=True) + "\n",
            encoding="utf-8",
        )
        os.replace(str(temporary), str(path))
    finally:
        if temporary.exists():
            temporary.unlink()


def parse_args(argv):
    parser = argparse.ArgumentParser(
        description="Verify official pinned ConSeC preprocessing/model logits against ONNX."
    )
    parser.add_argument("--upstream-directory", required=True, type=Path)
    parser.add_argument("--checkpoint", required=True, type=Path)
    parser.add_argument("--base-model-directory", required=True, type=Path)
    parser.add_argument("--bundle", required=True, type=Path)
    parser.add_argument("--report", type=Path)
    return parser.parse_args(argv)


def main(argv=None):
    args = parse_args(sys.argv[1:] if argv is None else argv)
    try:
        verify_capitalization_contract()
        environment = verify_environment()
        upstream = args.upstream_directory.resolve(strict=True)
        checkpoint_path = args.checkpoint.resolve(strict=True)
        base_model = args.base_model_directory.resolve(strict=True)
        bundle = args.bundle.resolve(strict=True)
        source_records = verify_upstream(upstream)
        base_records = verify_base_model(base_model)
        manifest, graph_path, manifest_sha, artifacts = load_bundle(bundle)

        sys.path.insert(0, str(upstream))
        imported_modules = verify_imported_reference_modules(upstream)
        tokenizer = guarded_official_tokenizer(base_model)
        fixtures = materialize_official_fixtures(tokenizer)
        verify_preprocessing(fixtures, manifest)

        checkpoint, pickle_globals = safe_load_checkpoint(checkpoint_path)
        model = create_official_model(checkpoint, base_model)
        official_outputs = run_official(model, fixtures)
        del model
        del checkpoint
        gc.collect()

        onnx_outputs = run_onnx(graph_path, fixtures)
        fixture_results = compare_outputs(
            fixtures, manifest, official_outputs, onnx_outputs
        )
        report = {
            "schemaVersion": 1,
            "status": "passed",
            "scope": "official-upstream-4.3.3-preprocessing-and-model-to-exported-onnx",
            "upstream": {
                "repository": UPSTREAM_REPOSITORY,
                "commit": UPSTREAM_COMMIT,
                "license": UPSTREAM_LICENSE,
                "sourceFiles": source_records,
                "importedModules": imported_modules,
            },
            "checkpoint": {
                "file": CHECKPOINT_NAME,
                "bytes": CHECKPOINT_BYTES,
                "sha256": CHECKPOINT_SHA256,
                "pickleGlobals": pickle_globals,
                "loadPolicy": "torch.load(weights_only=True,mmap=True)",
            },
            "baseModel": {
                "repository": BASE_MODEL_REPOSITORY,
                "revision": BASE_MODEL_REVISION,
                "sourceFiles": base_records,
                "legacyBpeLoadPolicy": "guarded torch.load(weights_only=True)",
            },
            "environment": environment,
            "tooling": {
                "runner": {
                    "file": Path(__file__).name,
                    "sha256": sha256(Path(__file__).resolve()),
                },
                "requirements": {
                    "file": "requirements-consec-reference.txt",
                    "sha256": sha256(
                        Path(__file__).resolve().with_name(
                            "requirements-consec-reference.txt"
                        )
                    ),
                },
            },
            "bundle": {
                "manifestSha256": manifest_sha,
                "artifacts": artifacts,
            },
            "gates": {
                "officialSourceClean": True,
                "officialPreprocessingMatchesExportedInputs": True,
                "officialModelMatchesExportedOnnx": True,
                "candidateChoiceMatches": True,
                "currentOnnxMatchesParityObservedCandidateLogits": True,
                "officialDeltaMatchesParityObservedExporterDelta": True,
                "historicalTokenizerBpeLoadWasWeightsOnly": True,
                "maximumAbsoluteDeltaTolerance": PARITY_TOLERANCE,
            },
            "fixtures": fixture_results,
        }
        if args.report is not None:
            write_json_atomic(args.report.resolve(), report)
        print(json.dumps(report, ensure_ascii=False, indent=2, sort_keys=True), flush=True)
        return 0
    except (ReferenceError, FileNotFoundError, OSError, ValueError) as exc:
        print("ConSeC upstream reference failed: {}".format(exc), file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
