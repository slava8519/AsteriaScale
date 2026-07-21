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

## Microsoft Foundry Local

- Project: https://github.com/microsoft/Foundry-Local
- License: MIT
- Components: Foundry Local CLI 0.10.2 and `Microsoft.AI.Foundry.Local.WinML` SDK 1.1.0
- Role: local model catalog/service and full-text translation inference

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

## Microsoft ML ONNX Runtime

- Project: https://github.com/microsoft/onnxruntime
- Package/version: `Microsoft.ML.OnnxRuntime` 1.27.1
- License: MIT
- Role: CPU execution of the FP32 GlossBERT reference artifact

## Tokenizers.DotNet

- Project: https://github.com/sappho192/Tokenizers.DotNet
- Packages/versions: `Tokenizers.DotNet` 1.4.1 and
  `Tokenizers.DotNet.runtime.win-x64` 1.4.1
- License: MIT
- Role: deterministic loading and execution of the packaged Hugging Face tokenizer

## Qwen 2.5 Coder 1.5B

- Model: `qwen2.5-coder-1.5b-instruct-vitis-npu:2`, downloaded separately by Foundry Local
- License reported by the Foundry catalog: Apache-2.0
- Role: research baseline for context selection in versions before 0.9; it is
  not packaged and is not used by the 0.9 smart-dictionary ranker

## Qwen 3.5

- Model: `qwen3.5-2b-text-generic-gpu:3`, downloaded separately by Foundry Local
- License reported by the Foundry catalog: Apache-2.0
- Role: local translation to Russian

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
