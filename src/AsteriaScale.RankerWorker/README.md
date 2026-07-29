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

## SANDWiCH CPU profile

`--provider cpu --cpu-model sandwich` selects the upstream NAACL 2025
SANDWiCH ensemble. It is deliberately CPU-only and requires this exact
out-of-band bundle beneath `--model-directory`:

```text
Models/
  Sandwich/
    model-manifest.json
    tokenizer.json
    Cpu/
      encoder-nv-fp32.onnx
      encoder-v-fp32.onnx
```

The worker validates all three files by byte length and SHA-256 before either
session is created. Both ONNX files must load; there is no single-encoder or
GlossBERT fallback.

| Artifact | Bytes | SHA-256 |
|---|---:|---|
| `Sandwich/tokenizer.json` | 8,656,779 | `2689db9b56b94ae9fac86daeb5fe7df0be0c5f1a9dce76a0edda7afa93dd1304` |
| `Sandwich/Cpu/encoder-nv-fp32.onnx` | 568,094,742 | `b6a21c42c974f145a39a20e5c5b62b740cb0b63ed698499fa4c5e4b397998739` |
| `Sandwich/Cpu/encoder-v-fp32.onnx` | 568,094,742 | `21b778dc9552f84732d1061ca1ace23f9f6c071e68fe4ab0615109f192e78f6b` |

The immutable runtime identity is:

```text
SANDWiCH:naacl-2025:singleton-gloss:m-nv-b6a21c42c974:m-v-21b778dc9552:t-2689db9b56b9:s512:c2:p1:pipeline-sandwich
```

The `singleton-gloss` adapter is important: Asteria does not install or ship
the upstream BabelNet-derived definition/neighbour files. Instead, every
existing English candidate gloss is one ensemble member and the existing
stable `candidateGroupIds` are its neighbourhood clusters. This keeps the
dictionary-layer contract intact and lets the selected stable outcome feed
the full-text TranslateGemma scaffold without exposing BabelNet data.

SANDWiCH input uses the exact hovered target occurrence delimited by literal
`[D]` tokens. For each gloss, both encoders produce a two-class softmax and
the positive probabilities are averaged. Within each stable group, member
weights are `softmax(abs(2p-1))`; the weighted member probabilities produce
the group score. The best/second group-score margin then goes through the same
sole `0.25` abstention threshold as the other rankers. Every selection response
reports `pipeline: "sandwich"`.

The upstream repository declares model and code MIT. Its optional data extracts
are governed by the BabelNet non-commercial license; they are intentionally
absent from this profile. The tracked reproducible-export provenance is in
`Models/Sandwich/model-manifest.json`.

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
graph and external-data file while inference is possible, so neither can be
replaced after validation. No ConSeC request can run on NPU or fall back to
GlossBERT.

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
6. returns a normal abstained v2 response when the margin is below `0.25`.

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

## NPU fail-closed proof

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
dotnet run --project src/AsteriaScale.RankerWorker/AsteriaScale.RankerWorker.csproj -c Release --no-build -- --provider cpu --cpu-model sandwich --model-directory src/AsteriaScale.RankerWorker/Models --smoke
dotnet run --project src/AsteriaScale.RankerWorker/AsteriaScale.RankerWorker.csproj -c Release --no-build -- --provider cpu --cpu-model consec --model-directory src/AsteriaScale.RankerWorker/Models --smoke
dotnet run --project src/AsteriaScale.RankerWorker/AsteriaScale.RankerWorker.csproj -c Release --no-build -- --provider npu --smoke
```

Every check must report the current protocol, exact pinned model identity and
pipeline, and finish without fallback. The model-specific regression suite
asserts the expected selected GroupId and margin behavior.
