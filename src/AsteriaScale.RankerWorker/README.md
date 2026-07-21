# GlossBERT ranker worker

This process isolates the dictionary word-sense model from the UI process so
its memory can be reclaimed completely. It implements JSON Lines protocol v2:
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
dotnet run --project src/AsteriaScale.RankerWorker/AsteriaScale.RankerWorker.csproj -c Release --no-build -- --provider npu --smoke
```

Both checks must select the river-bank sense, report protocol v2 and the pinned
model-version prefix, and finish without fallback.
