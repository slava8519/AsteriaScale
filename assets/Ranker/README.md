# EN→RU sense-ranker regression sets

The JSON files in this directory are deterministic quality gates for the
contextual dictionary ranker. They cover English homonyms plus colloquial and
literary wording typical of visual novels. Every sentence was written
specifically for Asteria Scale and is released under CC0-1.0.

The root object contains metadata and a `cases` array. Each case has:

- `id`: stable unique identifier;
- `context`: full English sentence shown to the ranker;
- `target`: word or phrase being translated;
- `candidates`: Russian surfaces and, in the definition-aware sets, their
  English definitions;
- the expected visible Russian outcome.

The v2 holdout additionally records `characterIndex`, `partOfSpeech`, and
`senseClass`. For those cases,
`context.Substring(characterIndex, target.Length) == target` must hold. The
holdout files must remain unopened while a model, prompt, threshold, or
candidate grouping is being tuned.

## Shipped ranker

Asteria Scale 0.9.3 ships
[`kanishka/GlossBERT`](https://huggingface.co/kanishka/GlossBERT) at immutable
revision `0cc3b83af5496e27ebcc95ef0cf37ea0a9281a7a` under the MIT license.
GlossBERT scores an English context against English sense definitions; it does
not generate a Russian translation.

The automatic path is deliberately narrow:

- only a single English target token is sent to the model;
- dictionary phrases bypass the model;
- duplicate glosses that produce the same visible translation are grouped;
- a decision is accepted only when the score margin is at least `0.25`;
- a low-margin abstention always stays a dictionary result;
- only a missing dictionary entry or ranker failure may invoke full translation,
  and only when the user explicitly selects the `Automatic` fallback.

CPU is the default provider and uses the unquantized FP32 reference graph. The
precompiled AMD Vitis NPU provider never silently falls back to CPU or GPU. On
the frozen 30-case `ranker-holdout-v2.json`, CPU accepted 18 decisions and 17
matched the labels; NPU accepted 16 and 15 matched. On the tested machine the
warmed NPU path was roughly twice as fast. These counts are a regression signal
from a small synthetic set, not a general accuracy guarantee.

An additional forced abstention above the calibration batch size (32 glosses on
CPU, 4 on NPU) was later removed: pair scores are independent and computed in
fixed-size chunks, so inventory size cannot change individual scores, and the
margin threshold remains the sole abstention criterion. Measured through the
full production pipeline (`AsteriaScale.Tests --ranker-benchmark <set.json>
<workerBaseDir> [cpu|npu]`): no dev or holdout case exceeds 18 glosses after
sense grouping, a hyper-polysemy probe (`set`, `run`, `go`, `light`) peaks at
27, and with the rule removed CPU and NPU agree — dev set 17/17 correct
accepted decisions among inventory-covered labels on both providers (margins
within `0.01`), holdout-v2 confirmation 8/8 on both with five abstentions. The
benchmark matcher is strict about inflection, so these figures are conservative
and not directly comparable to the 0.9.3 methodology.

## Packaged artifacts

The source artifacts are staged in
`src/AsteriaScale.RankerWorker/Models/GlossBert` and published under
`Ranker/Models/GlossBert`.

| Artifact | Bytes | SHA-256 |
|---|---:|---|
| `tokenizer.json` | 465,642 | `04FD39DFF4BFA1B7AE42425C484EF0173F8DA60B485F66642D2B44B251C6A014` |
| `Cpu/glossbert-fp32-dynamic.onnx` | 438,167,088 | `DAA4098D59B9077AF59E22F846818455CA39593567326338F88E328EBEEF18B5` |
| `Npu/glossbert-eager-vitis-context.onnx` | 93,776,750 | `152BD5B4C54D4A339A4DBB642489B950AF6C712BC7736F2B7CEB977A05573320` |
| `Npu/glossbert-eager-vitis-context.onnx.compatibility.txt` | 235 | `A15F82D6AED5DE34EB6BD75B727E4ECBC2476FBF14C91B04E37A7F1C1FF0F777` |
| `Npu/glossbert-eager-vitis-context.onnx_VITISAI.bin` | 123,964,416 | `94095699D6B7B59AB6EDC92C7471BF507B861BD14B4D57CB3EC75F49F3F5D672` |

`cross-encoder/mmarco-mMiniLMv2-L12-H384-v1` and generative Qwen selectors were
evaluated as earlier baselines and rejected for automatic sense selection.
They are not part of the packaged 0.9 ranker.
