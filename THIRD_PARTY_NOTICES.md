# Third-party notices

Asteria Scale does not copy Magpie binaries or shader files into its own source tree. It discovers and launches a separately installed Magpie distribution.

## Magpie

- Project: https://github.com/Blinue/Magpie
- License: GNU General Public License v3.0
- Role: window capture, DirectX 11 rendering, effect execution and presentation

## CuNNy / CuNNy2 effects

- Project: https://github.com/funnyplanter/CuNNy
- Upstream license: LGPL-3.0; consult the shader headers shipped by Magpie for the license of each distributed effect
- Role: visual-novel-trained CNN upscaling

## Anime4K

- Project: https://github.com/bloc97/Anime4K
- License: MIT
- Role: anime line restoration and upscaling effects

## AMD FidelityFX Super Resolution 1

- Project: https://github.com/GPUOpen-Effects/FidelityFX-FSR
- License: MIT
- Role: EASU edge-adaptive scaling and RCAS sharpening

## Other effects

Magpie ships additional effects such as SSimDownscaler and xBRZ. Their notices and source headers are part of the separately installed Magpie distribution. Asteria only refers to their effect names in user configuration.

## GlossBERT

- Model: https://huggingface.co/kanishka/GlossBERT
- Pinned revision: `0cc3b83af5496e27ebcc95ef0cf37ea0a9281a7a`
- Paper: https://aclanthology.org/D19-1355/
- License reported by the model repository: MIT
- Changes: exported to a dynamic FP32 ONNX graph for the CPU package, compiled
  to an AMD Vitis EPContext package for the optional NPU path, and paired with
  a deterministic tokenizer artifact
- Role: context-aware ranking of English dictionary senses for a single target
  token; it does not generate translations
- CPU ONNX SHA-256: `DAA4098D59B9077AF59E22F846818455CA39593567326338F88E328EBEEF18B5`
- NPU context SHA-256: `152BD5B4C54D4A339A4DBB642489B950AF6C712BC7736F2B7CEB977A05573320`
- NPU binary SHA-256: `94095699D6B7B59AB6EDC92C7471BF507B861BD14B4D57CB3EC75F49F3F5D672`
- NPU compatibility metadata SHA-256: `A15F82D6AED5DE34EB6BD75B727E4ECBC2476FBF14C91B04E37A7F1C1FF0F777`
- Tokenizer SHA-256: `04FD39DFF4BFA1B7AE42425C484EF0173F8DA60B485F66642D2B44B251C6A014`

## SANDWiCH (optional experimental ranker)

- Project: https://github.com/danielguzmanolivares/sandwich
- Paper: https://aclanthology.org/2025.naacl-long.358/
- Pinned upstream commit: `daca3cad63326204d4027b9ea09fb1405ab1ca0a`
- License reported by upstream for code and published models: MIT
- Integration: not stored in git; the separate installer downloads the pinned
  official archive, verifies SHA-256
  `98B3CDA35A8599C961FC2AC36867D4687BD136E3F0CA1998D4A4754EA8C8E1DE`,
  and exports the two official encoders to FP32 ONNX
- Changes: exact target marked with upstream `[D]` delimiters; positive-class
  probabilities from `encoder-nv` and `encoder-v` are averaged, then reduced
  over Asteria's stable sense groups with the upstream confidence weighting
- Role: optional CPU-only definition-aware ranker and semantic-evidence source;
  GlossBERT remains the default
- `encoder-nv` source SHA-256: `D759C83CF13C8D367ECCBDDB33D061C1DB6E5D5082E39B86679DCD4DD21EF602`
- `encoder-v` source SHA-256: `A63626AB5EF52CAD6360624BFDAAB91743716270E377F2BDF92FEABC512D273D`
- CPU ONNX SHA-256: `B6A21C42C974F145A39A20E5C5B62B740CB0B63ED698499FA4C5E4B397998739`
  (`encoder-nv`) and
  `21B778DC9552F84732D1061CA1ACE23F9F6C071E68FE4AB0615109F192E78F6B`
  (`encoder-v`)
- Tokenizer SHA-256: `2689DB9B56B94AE9FAC86DAEB5FE7DF0BE0C5F1A9DCE76A0EDDA7AFA93DD1304`
- Data boundary: Asteria does not download or distribute the upstream
  BabelNet-derived definitions/neighbours; it supplies its own dictionary
  glosses to the models

## DeBERTa-v3 WSD candidates (closed experiment, not bundled)

- Base model: [`microsoft/deberta-v3-base`](https://huggingface.co/microsoft/deberta-v3-base)
- Pinned revision: `8ccc9b6f36199bec6961081d44eb72fb3f7353f3`
- License reported by the model repository: MIT
- NLI model: [`cross-encoder/nli-deberta-v3-base`](https://huggingface.co/cross-encoder/nli-deberta-v3-base)
- Pinned revision: `6c749ce3425cd33b46d187e45b92bbf96ee12ec7`
- License reported by the model repository: Apache-2.0
- NLI training provenance reported by its model card: SNLI and MultiNLI
- Integration: model weights are not stored in git or in the RTX transfer ZIP;
  the training launcher downloads only the selected pinned revision and records
  its resolved identity in run provenance
- Role: historical controlled RTX fine-tuning candidates for the CPU
  definition-aware WSD ranker. The custom-training track is closed; neither
  candidate replaced production GlossBERT

## ConSeC/WNGT (optional personal research ranker, not bundled)

- Project: https://github.com/SapienzaNLP/consec
- Pinned upstream commit: `9602b5fd69f57be08a186988d1df34fe4152b63f`
- License: CC BY-NC-SA 4.0
- Evaluated upstream artifact: official WNGT checkpoint, `4,864,940,005` bytes,
  SHA-256 `92421ED245723964DB09CE396F19A0D1E55FE4D6E10D5ECB83278D9BC70CE8AF`
- Role: optional CPU-only sentence-aware WSD ranker whose provider-neutral
  semantic evidence can feed Smart Dictionary phrase output and TranslateGemma
  APE; GlossBERT remains the stable default
- Changes and verification: inference weights were extracted fail-closed with
  `torch.load(weights_only=True, mmap=True)`, exported to a FP32 ONNX external
  data bundle and integrated through the independent `consec-independent`
  adapter. An isolated run of the exact upstream Transformers 4.3.3 model and
  preprocessing matched the ONNX input hashes, selected choices and complete
  logits (`3.10e-6` dialogue and `5.01e-6` mixed-case scientific maximum
  deltas)
- Integration boundary: neither checkpoint nor generated bundle is stored in
  git, the normal installer or a public Asteria distribution. The separate
  `install-consec.cmd` requires explicit acceptance, verifies pinned hashes and
  installs the four-file bundle out of band. Use is restricted to personal
  scientific/non-commercial research under CC BY-NC-SA 4.0
- Runtime artifacts: `Cpu/consec-wngt-fp32.onnx` SHA-256
  `0EB0688F3FD2AF67C6E2155DAEED0FEC5C5B2F1C7037B91BA526D50516C630AD`,
  external data SHA-256
  `1257DC55EBEC8886B7F5AF4867F684AE44C7BE4308E75B1251963595354011B9`,
  tokenizer SHA-256
  `DF91C443C8EAC73A42FF72086F25B16AD3B244AE2BF0303625298265D50F73F1`

## Microsoft ML ONNX Runtime

- Project: https://github.com/microsoft/onnxruntime
- Package/version: `Microsoft.ML.OnnxRuntime` 1.27.1
- License: MIT
- Role: CPU execution of the FP32 GlossBERT reference artifact, optional
  SANDWiCH dual-encoder graphs and optional ConSeC external-data graph

## Tokenizers.DotNet

- Project: https://github.com/sappho192/Tokenizers.DotNet
- Packages/versions: `Tokenizers.DotNet` 1.4.1 and
  `Tokenizers.DotNet.runtime.win-x64` 1.4.1
- License: MIT
- Role: deterministic loading and execution of the packaged Hugging Face tokenizer

## TranslateGemma 4B (optional, not bundled)

- Model: [Google TranslateGemma 4B IT](https://huggingface.co/google/translategemma-4b-it)
- Optional quantized distribution:
  [`mradermacher/translategemma-4b-it-GGUF` Q4_K_M](https://huggingface.co/mradermacher/translategemma-4b-it-GGUF),
  revision `35a7486e128b19642cdc72d7b91b21ba388aaf42`
- Terms: [Gemma Terms of Use](https://ai.google.dev/gemma/terms)
- Use restrictions:
  [Gemma Prohibited Use Policy](https://ai.google.dev/gemma/prohibited_use_policy)
- Integration: the model is not part of the source tree, application publish or
  normal installer. `scripts/install-translategemma.cmd` downloads it into the
  current user's local application data only after explicit acceptance of the
  Gemma Terms, and records a local copy of the terms plus pinned size/SHA-256
  metadata.
- Role: optional independent second-line EN→RU translation of the complete
  current sentence in Smart Dictionary, on an explicitly selected CPU or
  Vulkan GPU backend

## llama.cpp (optional TranslateGemma runtime)

- Project: https://github.com/ggml-org/llama.cpp
- Pinned release: `b9632`, separate Windows x64 CPU and Vulkan archives
- License: MIT
- Integration: not bundled; downloaded and hash-verified by the separate
  TranslateGemma installer, then launched as an owned loopback-only
  `llama-server` process. CPU uses `--n-gpu-layers 0`; Vulkan is accepted only
  after selecting `Vulkan0` and verifying complete model offload
- Role: local inference runtime for the optional TranslateGemma phrase phase

## all-MiniLM-L6-v2

- Model: https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2
- Pinned revision: `c9745ed1`
- License: Apache-2.0
- Changes: exported to ONNX and paired with a precomputed int8 index of 225,217
  gloss vectors plus a deterministic tokenizer artifact
- Role: CPU-only bi-encoder prefilter that narrows a large sense inventory before
  GlossBERT ranks it; it never selects a sense on its own
- Encoder SHA-256: `6FD5D72FE4589F189F8EBC006442DBB529BB7CE38F8082112682524616046452`
- Gloss index SHA-256: `13C0FB6DB773D85F94E2C930E2C49C8BC022BD2003550413BCF1F963CC55C2D3`
- Tokenizer SHA-256: `BE50C3628F2BF5BB5E3A7F17B1F74611B2561A3A27EEAB05E5AA30F411572037`

## FreeDict / WikDict English–Russian data

- Source: https://download.freedict.org/generated/eng-rus/
- Version used: 2025.11.23
- License stated in the source TEI header: CC BY-SA 3.0
- Upstream sources attributed by the dataset: Wiktionary and DBnary
- Role: separately loaded English–Russian words and phrases for fast dictionary mode

## Russian Wiktionary / Kaikki English–Russian data

- Project: https://kaikki.org/ruwiktionary/Английский/
- Upstream: https://ru.wiktionary.org/
- Extractor: https://github.com/tatuylonen/wiktextract
- Version used: Russian Wiktionary dump 2026.07.06, Kaikki extraction 2026.07.15
- License: the same dual licensing as Wiktionary, CC BY-SA and GFDL
- Changes: audio, etymology and unrelated article metadata were removed; English
  headwords, inflected forms, phrases, Russian glosses and lexical-register labels were
  normalized into Asteria's deterministic gzip TSV format
- Role: full-coverage English–Russian layer, including conversational, slang, literary,
  obsolete and other register-marked vocabulary

## English Wiktionary / Kaikki English–Russian translation data

- Project: https://kaikki.org/dictionary/English/
- Upstream: https://en.wiktionary.org/
- Extractor: https://github.com/tatuylonen/wiktextract
- Version used: English Wiktionary dump 2026.07.06, Kaikki extraction 2026.07.16
- License: the same dual licensing as Wiktionary, CC BY-SA and GFDL
- Changes: entries without Russian translation boxes and unrelated article metadata were
  removed; English headwords and inflected forms, Russian translations, full English sense
  definitions, lexical-register labels and parts of speech were normalized into
  deterministic gzip TSV files
- Role: complete complementary English–Russian dictionary plus a lazily loaded,
  definition-aligned context-ranking layer

## JMdict Japanese multilingual data

- Project: https://www.jmdict.org/
- Copyright: Electronic Dictionary Research and Development Group (EDRDG)
- License: https://www.edrdg.org/edrdg/licence.html
- Role: Japanese spellings/readings with Russian glosses for fast dictionary mode

## Tatoeba English–Russian and Japanese–Russian sentence pairs

- Project: https://tatoeba.org/
- Downloads: https://tatoeba.org/downloads
- License: Creative Commons Attribution 2.0 France (CC BY 2.0 FR)
- Role: short dialogue and expression coverage for fast dictionary mode

## Amazon MASSIVE English–Russian utterances

- Project: https://github.com/alexa/massive
- Dataset/version: MASSIVE 1.1
- Archive: https://amazon-massive-nlu-dataset.s3.amazonaws.com/amazon-massive-dataset-1.1.tar.gz
- Copyright: Amazon.com, Inc. or its affiliates
- License: [Creative Commons Attribution 4.0 International (CC BY 4.0)](https://creativecommons.org/licenses/by/4.0/)
- Upstream attribution: the English source utterances were derived from SLURP, also
  distributed under CC BY 4.0; see the [MASSIVE NOTICE](https://github.com/alexa/massive/blob/main/NOTICE.md)
  and the [SLURP project](https://github.com/pswietojanski/slurp) for full attribution
- Changes: `en-US` and `ru-RU` were aligned by stable record ID; localized slots,
  low-quality judgments, lines outside the 2–10 word bound, unsafe script mixtures and
  conflicting normalized keys were removed; the remaining pairs were normalized into a
  deterministic exact-dialogue TSV
- Role: low-priority complete-sentence translation; never a lexical override, GlossBERT
  sense, or mandatory scaffold term

## OpenCorpora Russian morphological dictionary

- Project: https://opencorpora.org/ (consumed via the pymorphy3-dicts-ru distribution)
- License: Creative Commons Attribution-ShareAlike (CC BY-SA)
- Changes: only verb infinitives and past-tense forms were exported; forms and
  lemmas were casefolded with ё→е and packed into a deterministic gzip TSV
  (`Morphology/ru-morph.tsv.gz`) by `scripts/build-ru-morphology.py`
- Role: paradigm-accurate Russian verb morphology (past-tense agreement and
  grammeme-based aspect) for the smart-dictionary surface selection, with the
  original suffix heuristics as fallback for unknown words

## Textractor (optional text hook)

- Project: https://github.com/Artikash/Textractor
- License: GPLv3
- Integration: never linked or bundled; Asteria optionally launches a
  user-installed TextractorCLI.exe as a separate process (the same isolation
  model as Magpie) and reads its sentence stream. Text hooking injects into
  the game process; antivirus software may flag it, and elevated games
  require an elevated Asteria.
- Role: optional hover-context upgrade for compatible game engines; OCR
  remains the default and universal text source
