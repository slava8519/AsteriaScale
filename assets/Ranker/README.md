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

## Bi-encoder prefilter artifacts (roadmap #7)

`Models/GlossEmbeddings` (not in git; regenerate with
`tools/build-gloss-embeddings.py`):

| Artifact | Bytes | SHA-256 |
|---|---|---|
| eng-senses.vec | 91,577,685 | 13C0FB6DB773D85F94E2C930E2C49C8BC022BD2003550413BCF1F963CC55C2D3 |
| minilm-l6-v2.onnx | 90,405,214 | 6FD5D72FE4589F189F8EBC006442DBB529BB7CE38F8082112682524616046452 |
| tokenizer.json | 466,247 | BE50C3628F2BF5BB5E3A7F17B1F74611B2561A3A27EEAB05E5AA30F411572037 |

Encoder: sentence-transformers/all-MiniLM-L6-v2 @ c9745ed1 (Apache-2.0),
225,217 int8 vectors (384-dim, per-vector scale), matched by FNV-1a hashes of
normalized definitions. The prefilter engages only above 16 distinct outcome
groups and keeps the top 12 with all their glosses (GroupIds preserved).
It is CPU-only: explicit NPU requests always use the pinned GlossBERT cross
encoder without a hidden CPU stage. At startup the CPU worker validates the
size and SHA-256 of all three files plus the index model/revision/source
metadata; a partial or mismatched bundle is a hard error. Worker model identity
includes the validated bundle revision, and each result separately reports
`cross` or `bi+cross` for cache isolation.
Measured: decisions and margins are bit-identical to the pure cross path on
the dev set and the hyper-polysemy probe; k=8 was rejected after it evicted
the correct sense of "set" and turned a calibrated abstention into a wrong
answer. Missing artifacts silently fall back to the pure cross pipeline.

All three files are **required for a complete build**: `scripts/build.ps1` refuses
to publish without them, and `scripts/build-state.ps1` treats a `dist` that lacks
any of them as out of date.

### Regenerating the index

```powershell
python tools/build-gloss-embeddings.py `
  --senses assets/Dictionaries/eng-rus-enwiktionary-senses.tsv.gz `
  --output src/AsteriaScale.RankerWorker/Models/GlossEmbeddings/eng-senses.vec `
  --bundle-dir src/AsteriaScale.RankerWorker/Models/GlossEmbeddings
```

The pinned encoder and tokenizer are downloaded at the immutable revision and
verified by size and SHA-256. The index is generated under a sibling temporary
name, verified, and only then replaces `eng-senses.vec`; interruption cannot
truncate the working bundle. The two pinned dependencies are likewise replaced
through temporary files. Thus the command creates the complete three-file
runtime bundle from a clean checkout rather than leaving two files in the
Hugging Face cache for manual copying.

The runtime pins the whole bundle, so a regenerated index is rejected with
`embedding_metadata_mismatch` until `RankerEngine.cs` is updated. The script
prints every paste-ready constant — `EmbeddingIndexFormat`,
`EmbeddingIndexLength`, `EmbeddingIndexSha256`, `EmbeddingModelLength`,
`EmbeddingModelSha256`, `EmbeddingTokenizerLength`,
`EmbeddingTokenizerSha256`, `EmbeddingSenseFileSha256`, `EmbeddingKeyCount`,
`EmbeddingVectorCount`, and the complete `EmbeddingBundleCacheTag` — and the
table above must be updated with the new size and SHA-256.

Note on `EmbeddingSenseFileSha256`: the shipped index is **format 1**, which
recorded the SHA-256 of the sidecar's gzip *container*. Those bytes change on
every recompression even when the glosses are identical, so the pinned value
`A15C4037…` no longer matches the sidecar currently on disk
(`A776B4DA…`) — the sidecar was rebuilt after the index. A regenerated bundle is
**format 2** and records the digest of the decompressed contents instead, which
is stable across recompression. The shipped bundle is intact and passes
validation; only regeneration moves to the new format.
