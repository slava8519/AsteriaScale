# EN→RU sense-ranker regression sets

The JSON files in this directory are deterministic quality gates for the
contextual dictionary ranker. They cover English homonyms plus colloquial and
literary wording typical of visual novels, manga and general literature; the
open dev set also includes a 20-case scientific-prose slice. Every sentence was written
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

- one exact English target span is marked for the model;
- trusted user/VN-hard and unambiguous phrases bypass the model, while
  ambiguous broad/VN-soft expressions with sense metadata use the same
  definition-aware path as words;
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
<workerBaseDir> [cpu|npu]`; CPU profile selected with
`--ranker-cpu-model glossbert|deberta|sandwich|consec`): no dev or holdout case
exceeds 18 glosses after
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

### DeBERTa CPU candidate (closed experiment, non-default)

The historical self-training candidate is staged separately under
`Models/GlossDeberta`. It is selected only by the benchmark/runtime profile;
GlossBERT remains the production default. Normal builds and publishes do not
copy this experimental 293 MB bundle. A local benchmark build must opt in:

```powershell
dotnet build AsteriaScale.slnx -c Release `
  -p:IncludeExperimentalDebertaModel=true
```

Alternatively, copy the complete bundle into an isolated benchmark worker
layout. The runtime authenticates
`model-manifest.json` before reading any path, tensor shape, class mapping or
cache-identity field from it. Pinned model, tokenizer, NPU sidecar/binary and
embedding files stay open with read-only sharing until their native sessions
are constructed, preventing replacement between SHA verification and load.

| Artifact | Bytes | SHA-256 |
|---|---:|---|
| `model-manifest.json` | 527 | `F4028FE04C8A138DFEF3D4E453907C6D2AD9E14ED8A44AF75D4410BE51149B89` |
| `tokenizer.json` | 8,340,127 | `8E71D5C9121679076EE7582B048973119BD8F2D742B57F1D8E6A5585697A5620` |
| `Cpu/definition-ranker-fp32.onnx` | 284,328,578 | `8C85154563AC9950F53387EEE1346209249C64F7EC9A1D7F5C5011A02FE21190` |

The exact host cache identity is pinned alongside these bytes. Promoting a
different candidate requires updating the manifest size/SHA in
`RankerEngine.cs`, this table and the host identity in one review.

### SANDWiCH CPU profile

The ready-made NAACL 2025 SANDWiCH ensemble from upstream commit
`daca3cad63326204d4027b9ea09fb1405ab1ca0a` is installed out of band under
`Models/Sandwich` and selected explicitly as the CPU ranker. Its reproducible
export provenance is tracked in
`src/AsteriaScale.RankerWorker/Models/Sandwich/model-manifest.json`; the two large
ONNX files and tokenizer are ignored by git.

Pinned upstream inputs:

| Artifact | Bytes | SHA-256 |
|---|---:|---|
| official `sandwich-models.zip` | 957,844,471 | `98B3CDA35A8599C961FC2AC36867D4687BD136E3F0CA1998D4A4754EA8C8E1DE` |
| `models/encoder-nv/model.safetensors` | 567,598,552 | `D759C83CF13C8D367ECCBDDB33D061C1DB6E5D5082E39B86679DCD4DD21EF602` |
| `models/encoder-v/model.safetensors` | 567,598,552 | `A63626AB5EF52CAD6360624BFDAAB91743716270E377F2BDF92FEABC512D273D` |
| upstream `tokenizer.json` | 8,656,779 | `2689DB9B56B94AE9FAC86DAEB5FE7DF0BE0C5F1A9DCE76A0EDDA7AFA93DD1304` |

Pinned local export:

| Artifact | Bytes | SHA-256 |
|---|---:|---|
| `tokenizer.json` | 8,656,779 | `2689DB9B56B94AE9FAC86DAEB5FE7DF0BE0C5F1A9DCE76A0EDDA7AFA93DD1304` |
| `Cpu/encoder-nv-fp32.onnx` | 568,094,742 | `B6A21C42C974F145A39A20E5C5B62B740CB0B63ED698499FA4C5E4B397998739` |
| `Cpu/encoder-v-fp32.onnx` | 568,094,742 | `21B778DC9552F84732D1061CA1ACE23F9F6C071E68FE4AB0615109F192E78F6B` |
| `model-manifest.json` | 1,108 | `B4D20C4F82CC876805F5D4A1C848DA88AE3E1C9D635A349CE1D34E7266DCA0A3` |

The adapter marks the exact target with literal `[D]` delimiters, averages the
two encoders' positive-class probabilities, then applies the official
confidence-weighted cluster reducer to the existing stable outcome groups.
`singleton-gloss` in the identity means that every Asteria gloss is submitted
as an individual encoder pair before that reducer; it does **not** discard
multiple glosses that share one GroupId.
The runtime/cache identity is
`SANDWiCH:naacl-2025:singleton-gloss:m-nv-b6a21c42c974:m-v-21b778dc9552:t-2689db9b56b9:s512:c2:p1:pipeline-sandwich`.
It contains both model hashes plus the singleton-gloss adapter marker so a
different grouping scheme cannot reuse decisions.

Upstream declares the models and code MIT. Asteria does not install the
BabelNet-derived definitions/neighbours governed by the separate
non-commercial data license; it supplies its own layered-dictionary glosses.

Install and independently verify the bundle:

```powershell
.\scripts\install-sandwich.cmd
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\verify-sandwich.ps1
```

The exporter passed Torch/ONNX Runtime parity with observed maximum deltas on
the order of `1e-6`. A real worker smoke loaded both CPU sessions and selected
GroupId `20` with margin `0.802342`, selected score `0.945381`, reported
`pipeline=sandwich`, and used no fallback.

The final same-build open-dev A/B used `134` cases (`20` scientific), made `113`
ranker requests and covered the expected sense inventory in `106`. Report schema
4 deliberately separates selection of the correct sense group from the exact
Russian surface the application shows:

| Profile | Sense correct/decided | Sense precision | Sense-aware E2E | Visible correct/decided | Visible E2E | Median select | Peak working set |
|---|---:|---:|---:|---:|---:|---:|---:|
| GlossBERT CPU | 62/64 | 96.875% | 75/134 (55.97%) | 51/66 (77.27%) | 64/134 (47.76%) | 156 ms | 691 MB |
| SANDWiCH CPU | 55/62 | 88.71% | 68/134 (50.75%) | 44/65 (67.69%) | 57/134 (42.54%) | 204 ms | 1,691 MB |

The visible comparison accepts exact labels and conservative inflections, but
not merely a shared prefix: for example, statistical `значимый` is not silently
credited as `значительный`. On the 20-case scientific/technical slice,
GlossBERT scored `14/14` accepted sense decisions and `11/14` visible decisions;
SANDWiCH scored `13/16` and `11/16`. Both reached `11/20` strict visible E2E,
but SANDWiCH made more incorrect accepted decisions.

SANDWiCH therefore fails the 90% accepted-sense precision gate and also loses
on total quality, latency and memory. It remains available only as an explicit
experimental research profile; GlossBERT remains the default. No frozen
holdout was opened.

### ConSeC CPU profile and sentence-planner foundation

ConSeC is not merely a newer pair scorer. The official WNGT checkpoint from upstream commit
`9602b5fd69f57be08a186988d1df34fe4152b63f`: `4,864,940,005` bytes, SHA-256
`92421ED245723964DB09CE396F19A0D1E55FE4D6E10D5ECB83278D9BC70CE8AF`, was
audited and converted locally with `tools/export-consec-onnx.py`. The
fail-closed exporter uses `weights_only=True`, checks every pickle global and
state shape, and produced a `1,621,487,616`-byte FP32 external-data graph. Its
two adapted-PyTorch/ORT fixtures had maximum absolute deltas `3.10e-6` and
`5.01e-6`; the ONNX-stable attention adapter matched stock Transformers 4.46.3
exactly on both fixture lengths.

The separate official-upstream gate has now passed in an exact Python 3.9.13 /
Transformers 4.3.3 environment. Both dialogue and scientific fixtures produced
identical input tensor hashes and choices; complete-logit maximum deltas were
`3.10e-6` and `5.01e-6`, well below `1e-4`. The mixed-case `PCR DNA` fixture
also pins the official Python `str.capitalize()` preprocessing, including the
`capitalize()`-before-`strip('.')` order. The C# runtime reproduces ASCII
English casing exactly and fails closed for cased Unicode letters whose Python
3.9 expansion/titlecase semantics cannot be guaranteed by .NET. The immutable
external proof is
`tools/consec-upstream-reference-golden.json`, SHA-256
`D6A5A3CDF6429BF4ACDE6BBA5A254C209EFF28F76C577AD7D845A7BA086AB22E`.
The old `officialUpstreamEndToEndReferenceVerified: false` field in the already
generated manifest is intentionally not rewritten; the golden binds that exact
manifest and bundle by hash.

The current `consec-independent` CPU runtime resolves each requested target
independently in the complete sentence and maps the result back to the original
stable GroupId. It does not truncate: a serialized sequence longer than 512 tokens is
rejected and the hover pipeline degrades safely to its dictionary result. It is
exposed explicitly in the UI as `ConSeC — лучший для науки, CPU`; NPU requests
are rejected and there is no GlossBERT fallback. The immutable identity is:

```text
ConSeC:WNGT:9602b5fd:adapter-consec-wngt-positional-extractor-v1:g-0eb0688f3fd2:d-1257dc55ebec:t-df91c443c8ea:pretokenizer-v3:context-full-v1:s512:pipeline-consec-independent
```

Exact local bundle:

| Artifact | Bytes | SHA-256 |
|---|---:|---|
| `Cpu/consec-wngt-fp32.onnx` | 843,194 | `0EB0688F3FD2AF67C6E2155DAEED0FEC5C5B2F1C7037B91BA526D50516C630AD` |
| `Cpu/consec-wngt-fp32.onnx.data` | 1,621,487,616 | `1257DC55EBEC8886B7F5AF4867F684AE44C7BE4308E75B1251963595354011B9` |
| `tokenizer.json` | 3,560,546 | `DF91C443C8EAC73A42FF72086F25B16AD3B244AE2BF0303625298265D50F73F1` |
| `model-manifest.json` | 4,407 | `13A8A14E3938D4F20519F7DA79A51F75543ECED352BD39456B5A43C4D93402B8` |

The bundle is ignored by git and excluded from a normal build/distribution.
For personal scientific/non-commercial work under CC BY-NC-SA 4.0, install and
verify it explicitly:

```powershell
.\scripts\install-consec.cmd `
  -SourceBundle "$env:LOCALAPPDATA\AsteriaScale\Models\Consec-WNGT-ONNX"
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\verify-consec.ps1
```

The open evaluation input is `ranker-sentence-benchmark.json`: 46 original CC0
cases with 138 exact target spans and 276 ordered sense groups. Its scientific
core has exactly six cases in each of biology, medicine, physics, chemistry,
statistics and computer science; four context-only cases form two contrast
pairs, and six narrative cases cover literature, visual novels and manga.
Nineteen cases carry an input-only `previousContext`; it never becomes displayed
source or reference output. Gold positions are balanced 72/66. One hundred
targets form a surface-unambiguous sense slice, while 38 retain natural
overlapping Russian renderings. Both slices still score the selected GroupId;
neither evaluates generated Russian or full-text translation. Dataset SHA-256:
`BED4C0211B1C8E79654C630D4DCB95625E32B07D099DA872F1DD4E3154EC6BCA`.
This is a development set, not a frozen holdout.

Same-build results on all `46` sentences / `138` targets:

| Metric | GlossBERT CPU | SANDWiCH CPU | ConSeC CPU |
|---|---:|---:|---:|
| Correct target groups | 91 | 120 | **131** |
| Scientific correct (`108`) | 69 | 96 | **106** |
| All target senses correct (`46`) | 20 | 31 | **41** |
| Surface-unambiguous target sense correct (`100`) | 67 | 86 | **94** |

Reproduce one profile without running the default Magpie smoke:

```powershell
dotnet run --project tests/AsteriaScale.Tests/AsteriaScale.Tests.csproj `
  -c Release --no-build -- --benchmark-only `
  --ranker-sentence-benchmark assets/Ranker/ranker-sentence-benchmark.json `
  dist/AsteriaScale --ranker-cpu-model consec `
  --sentence-report artifacts/Ranker/consec-sentence-report.json
```

ConSeC accepted `96.32%` of its decisions at `98.55%` coverage, with warm
p50/p95 `140/205 ms`, `6,420 ms` cold start, `1,500 MB` peak working set and no
runtime errors or sequence overflows. It nevertheless failed one of the two
history-only contrast pairs and made five confident errors. Therefore it is the
best current scientific/research profile, but GlossBERT remains the stable
default while feedback/joint planning and TranslateGemma/full-text A/B remain
unfinished.

For continuity with the older pair-oriented dev set, ConSeC also passed the
90% accepted-sense gate: `91/97` sense decisions, `72/103` visible decisions,
`104/134` sense-aware E2E, `85/134` visible E2E, `360 ms` median and `1,563 MB`
peak memory. Its accepted visible precision is still lower than GlossBERT's,
which is another reason not to change the default. No frozen holdout was opened.

Ranker reports use schema 4; register reports remain schema 3. Besides the benchmark and exact
active model files, each report records bytes and SHA-256 for the executing
test/application/worker binaries, every loaded `eng-rus*.tsv[.gz]` input and
`Morphology/ru-morph.tsv.gz`. Only the three pinned embedding files above are
counted; unrelated experiments left in `GlossEmbeddings` cannot alter the
reported footprint.

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
