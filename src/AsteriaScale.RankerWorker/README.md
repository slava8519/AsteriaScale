# Semantic ranker worker

This process isolates the dictionary word-sense model from the UI process so
its memory can be reclaimed completely. It implements JSON Lines protocol v4:
one request always produces exactly one response.

The pinned model is `kanishka/GlossBERT` revision
`0cc3b83af5496e27ebcc95ef0cf37ea0a9281a7a`. Every successful response has a
`modelVersion` beginning with:

```text
GlossBERT:0cc3b83af5496e27ebcc95ef0cf37ea0a9281a7a
```

## Bundle contract

`--model-directory` points to the parent of this exact bundle:

```text
Models/
  GlossBert/
    tokenizer.json
    Cpu/
      glossbert-fp32-dynamic.onnx
    Npu/
      glossbert-eager-vitis-context.onnx
      glossbert-eager-vitis-context.onnx_VITISAI.bin
      glossbert-eager-vitis-context.onnx.compatibility.txt
```

The worker validates both length and SHA-256 before loading any artifact:

| Artifact | Bytes | SHA-256 |
|---|---:|---|
| `tokenizer.json` | 465,642 | `04fd39dff4bfa1b7ae42425c484ef0173f8da60b485f66642d2b44b251c6a014` |
| CPU ONNX | 438,167,088 | `daa4098d59b9077af59e22f846818455ca39593567326338f88e328ebeef18b5` |
| NPU EPContext ONNX | 93,776,750 | `152bd5b4c54d4a339a4dbb642489b950af6c712bc7736f2b7ceb977a05573320` |
| NPU context binary | 123,964,416 | `94095699d6b7b59ab6edc92c7471bf507b861bd14b4d57cb3ec75f49f3f5d672` |
| NPU compatibility sidecar | 235 | `a15f82d6aed5de34eb6bd75b727e4ecbc2476fbf14c91b04e37a7f1c1ff0f777` |

The CPU path is explicitly pinned to `Microsoft.ML.OnnxRuntime` 1.27.1 and
uses the unquantized FP32 reference graph. The previous full dynamic-INT8
artifact was removed because it changed both top choices and score margins.
The same package runs on both platforms: it supplies `onnxruntime.dll` to the
win-x64 build and `libonnxruntime.dylib` to the osx-arm64 build
(`src/AsteriaScale.RankerWorker.MacOS`, see "macOS (osx-arm64)" below).

## Removed CPU profiles

`--cpu-model` accepts only `glossbert` (default) and `consec`. The SANDWiCH
dual-encoder profile and the GlossDeBERTa candidate were removed on
8 November 2026; `--cpu-model sandwich` or `--cpu-model deberta` now fails
with `Unsupported CPU model '<value>'. Expected glossbert or consec.` Their
bundle contracts, pins and the `[D]` target-marker path were last present in
commit `911510e`. Local `Models/Sandwich` and `Models/GlossDeberta` folders
are ignored by git and are not packaged with the worker.

## ConSeC CPU profile

`--provider cpu --cpu-model consec` selects the official ConSeC-SemCor+WNGT
checkpoint through Asteria's independent positional-extractor adapter. It is
CPU-only, CC BY-NC-SA 4.0 and requires the separately installed personal
scientific/non-commercial bundle:

```text
Models/
  Consec/
    model-manifest.json
    tokenizer.json
    Cpu/
      consec-wngt-fp32.onnx
      consec-wngt-fp32.onnx.data
```

| Artifact | Bytes | SHA-256 |
|---|---:|---|
| `Consec/tokenizer.json` | 3,560,546 | `df91c443c8eac73a42ff72086f25b16ad3b244ae2bf0303625298265d50f73f1` |
| `Consec/Cpu/consec-wngt-fp32.onnx` | 843,194 | `0eb0688f3fd2af67c6e2155daeed0fec5c5b2f1c7037b91ba526d50516c630ad` |
| `Consec/Cpu/consec-wngt-fp32.onnx.data` | 1,621,487,616 | `1257dc55ebec8886b7f5af4867f684ae44c7be4308e75b1251963595354011b9` |
| `Consec/model-manifest.json` | 4,407 | `13a8a14e3938d4f20519f7da79a51f75543eced352bd39456b5a43c4d93402b8` |

All four paths, sizes, hashes, manifest fields and tokenizer special IDs are
validated before the ONNX session is created. Read leases remain alive for the
graph and external-data file while inference is possible, so on Windows
neither can be replaced after validation. On macOS the guarantee is weaker
("validated at load"): .NET maps `FileShare` to an advisory `flock(2)`, so a
lease refuses only another opener that asks for an exclusive lock, while any
other process can still replace, truncate or delete the file, and ONNX Runtime
reopens the graph by path and maps external data copy-on-write. This guarantee
was chosen for 20.1 as the default during implementation; the owner may
revisit it. Hardening it is a roadmap card 20 follow-up. No ConSeC request can
run on NPU or fall back to GlossBERT.

The immutable runtime identity is:

```text
ConSeC:WNGT:9602b5fd:adapter-consec-wngt-positional-extractor-v1:g-0eb0688f3fd2:d-1257dc55ebec:t-df91c443c8ea:pretokenizer-v3:context-full-v1:s512:pipeline-consec-independent
```

The worker preserves the raw sentence and resolves the requested target span
from the canonical anchor index. It then builds upstream `<d>...</d>`,
`CONTEXT_DEFS`, `DEF_SEP` and `DEF_END` inputs and reads logits at candidate
marker positions. Definition preprocessing preserves the official Python order
`capitalize()` then `strip('.')`; lowercase Unicode letters in the tail are
preserved unchanged, while unsupported non-ASCII case mappings fail closed
instead of silently drifting from the pinned Python 3.9 tables. Independent
mode passes no previous-sense feedback; each
stable GroupId receives the maximum probability of its definitions before the
same sole `0.25` margin threshold. Sequences over 512 tokens fail explicitly;
there is no silent truncation. Responses report `pipeline:
"consec-independent"`.

The exact official Transformers 4.3.3 preprocessing/model path passed the
external reference gate for dialogue and scientific fixtures: input hashes and
choices were identical, while complete-logit maximum deltas were `3.10e-6` and
`5.01e-6`. The scientific fixture deliberately contains mixed-case `PCR DNA`
and therefore also pins upstream Python `str.capitalize()` preprocessing and
its ordering relative to literal-dot stripping. The
bound golden proof is
`tools/consec-upstream-reference-golden.json`.

## Sense-selection contract

Each candidate is an English definition, not a Russian translation. The
parallel `candidateGroupIds` array maps one or more glosses to a stable Russian
outcome. The worker:

1. quotes the exact target occurrence in context with ASCII `"`;
2. encodes the pair `(marked context, "target: definition")`;
3. converts the two classifier logits to positive-class probability;
4. takes the maximum gloss score for each distinct stable group;
5. compares the best two distinct group scores;
6. returns a normal successful protocol v4 response with `abstained: true` when
   the margin is below `0.25`.

At most 128 glosses are accepted. CPU inference uses dynamic inputs in chunks
of 32 and pads only to the longest pair in that chunk, capped at 128 tokens.
NPU inference uses fixed `[4,64]` chunks and duplicates the final real lane for
padding; padded results never participate in ranking. Scores from every real
lane are grouped before the same margin-only acceptance rule is applied.

CPU FP32 is the reference default. On the frozen 30-case VN holdout it accepted
18 decisions, of which 17 matched the labels (60% coverage, 94.4% selective
accuracy). The NPU artifact is a faster explicit option: it accepted 16
decisions, of which 15 matched. The worker never silently substitutes CPU for
a failed NPU request.

## NPU fail-closed proof (Windows only)

The NPU path exists only in the Windows build, which reaches the certified
VitisAI provider through Windows ML. On macOS see "macOS (osx-arm64)" below.

`--provider npu` requires an optimal compatibility result for the selected AMD
NPU and exact Int64 `[4,64]` inputs plus Float `[4,2]` classifier output. After
warmup, its one-shot ONNX Runtime profile must contain exactly:

- one Vitis transformer/classifier fused kernel with the pinned tensor shapes;
- five exact CPU front-end nodes: word and token-type embedding gathers,
  attention-mask cast, flatten, and mask gather.

Every node index, operator, element type, and shape is checked. Any extra CPU
assignment, missing Vitis kernel, incompatible context, or unexpected shape
fails initialization. The provider/device reported over the protocol is
`VitisAIExecutionProvider` / `NPU`.

## Smoke checks

```powershell
dotnet run --project src/AsteriaScale.RankerWorker/AsteriaScale.RankerWorker.csproj -c Release --no-build -- --provider cpu --smoke
dotnet run --project src/AsteriaScale.RankerWorker/AsteriaScale.RankerWorker.csproj -c Release --no-build -- --provider cpu --cpu-model consec --model-directory src/AsteriaScale.RankerWorker/Models --smoke
dotnet run --project src/AsteriaScale.RankerWorker/AsteriaScale.RankerWorker.csproj -c Release --no-build -- --provider npu --smoke
```

Every check must report the current protocol, exact pinned model identity and
pipeline, and finish without fallback. The model-specific regression suite
asserts the expected selected GroupId and margin behavior.

## macOS (osx-arm64)

`src/AsteriaScale.RankerWorker.MacOS` builds these same sources (by link) for
Apple Silicon, CPU only (roadmap 20.1). It declares no Content, so no build
copies a model bundle into `bin/`; the worker reads the bundle in place
through `--model-directory`. Only the ConSeC check applies there: the
GlossBERT CPU artifact is not in git (`model_missing`), and there is no NPU.

```bash
dotnet build src/AsteriaScale.RankerWorker.MacOS/AsteriaScale.RankerWorker.MacOS.csproj -c Release
src/AsteriaScale.RankerWorker.MacOS/bin/Release/net10.0/osx-arm64/AsteriaScale.RankerWorker \
  --provider cpu --cpu-model consec --model-directory src/AsteriaScale.RankerWorker/Models --smoke
```

The build has no Windows ML. With `--provider npu`, and also without
`--provider` (the default stays NPU), every model request fails with
`npu_provider_unavailable` before any artifact is read; CPU is never
substituted. `--smoke` then exits 1. A JSONL session keeps serving: `hello`
succeeds, each `warm` and each well-formed `select` answers `ok:false` with
that `errorCode` (the failure is cached), and the worker exits 0 on
`shutdown` or end of input. Artifact leases are
"validated at load" there (see "ConSeC CPU profile"). Never build the Windows
project on macOS: with `EnableWindowsTargeting` it schedules a complete local
ConSeC bundle into `bin/`.
