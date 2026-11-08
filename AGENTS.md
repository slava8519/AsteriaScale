# Agent instructions for Asteria Scale

Asteria Scale is a Windows WPF app that upscales low-res game windows through
the external Magpie engine and translates on-screen text to Russian fully
offline: OCR (Windows AI on the NPU) or a Textractor text hook → layered
dictionaries → definition-aware sense ranking in an isolated ONNX worker
(GlossBERT by default; ConSeC is an opt-in CPU profile) → optional
local TranslateGemma 4B through llama.cpp (CPU or Vulkan). The Qwen/Foundry
Local pipeline was removed on 2026-07-30; do not reintroduce it. The SANDWiCH
and GlossDeBERTa ranker profiles and the closed DeBERTa fine-tune tooling were
removed on 2026-11-08 (last present in commit `911510e`); a saved `sandwich` or
`deberta` setting loads as GlossBERT. ConSeC's own DeBERTa-large backbone is
unaffected. Read
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) before touching translation code;
it lists the load-bearing invariants. Build/test/release mechanics live in
[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md). Ready-made task prompts (in
Russian) are in [docs/PROMPTS.md](docs/PROMPTS.md). Planned work is
in [docs/ROADMAP.md](docs/ROADMAP.md); closed audit findings are archived in
[docs/AUDIT-ROADMAP.md](docs/AUDIT-ROADMAP.md), and the one open audit pass —
translation quality and measurement methodology — is
[docs/audit/pass-e-translation-quality.md](docs/audit/pass-e-translation-quality.md).

## Where code goes

Check the file path, not the namespace: the Core split kept the old
namespaces, so `AsteriaScale.Services` exists in two assemblies.

- `src/AsteriaScale.Core` — platform-neutral translation core (plain
  `net10.0`, no RID): dictionaries, morphology, sense-ranking host, phrase
  translation, caches, settings. CA1416 is a build error. Add no Windows binary
  names or paths; the existing ones listed in docs/ARCHITECTURE.md are known
  seams. Most types are `internal` (a few, such as `AppSettings`,
  `LocalDictionaryService` and `GlossBertDictionarySelectorService`, are
  `public`), and `InternalsVisibleTo` covers only `AsteriaScale` and
  `AsteriaScale.Tests`, so a new consumer of an internal type needs its own line.
- `src/AsteriaScale` — the WPF host and every Windows facility (UI, overlays,
  tray, hotkeys, Magpie, DPI, Windows AI OCR, Textractor,
  `HoverTranslationService`).
- `src/AsteriaScale.RankerProtocol` — the shared host/worker contract: JSONL
  protocol, `RankerProviderTraits`, `RankerAbstention`.
- `src/AsteriaScale.RankerWorker` — the ONNX worker. Some of its files are
  compiled into the test project via `<Compile Include … Link>` in
  `AsteriaScale.Tests.csproj`.
- `tests/AsteriaScale.Tests` — the smoke suite.

## Prerequisites

Windows 11 build 26100+, .NET 10 SDK, Magpie 0.12.1 (the "Installed effects"
and "Engine lifecycle" smoke tests use it), Python 3.11+ on `PATH` as `python`
(the smoke run executes `tools/translation-benchmark.py` and
`tools/atomic_output.py`), and the inbox
Windows PowerShell. Build and the default smoke run need no model artifacts
(ranker tests use fake bytes); the ConSeC tokenizer check is skipped
silently when that ignored bundle is absent, so a clean-clone pass covers
less than the owner's machine. `scripts/build.cmd` and every benchmark need the
real artifacts (docs/DEVELOPMENT.md). Only Core and RankerProtocol target plain
`net10.0`; on a non-Windows host say so instead of reporting a test run.

## Commands

**Safe on any Windows clone.** Run the build and the full smoke suite after
every change; the suite finishes in ~1 minute and includes a real Magpie engine
start/stop:

```powershell
dotnet build AsteriaScale.slnx -c Release
dotnet run --project tests/AsteriaScale.Tests/AsteriaScale.Tests.csproj -c Release --no-build
```

An unrecognized option fails the run instead of falling through to the default
smoke set, so a removed gated mode cannot report a pass it never performed.
The Python suites are offline and stdlib-only; the C# smoke does not run them:

| After changing | Run |
|---|---|
| `scripts/build-dictionaries.py`, `scripts/dictionary-normalization-contract.tsv`, `tools/dictionary-source/vn-*` | `python scripts/test-build-dictionaries.py` |
| `tools/translation-benchmark.py` | `python tools/test-translation-benchmark.py` and `python tools/test-sense-gate-early-exit.py` (it loads `tools/sense-gate-early-exit.py`, which imports `translation-benchmark.py`) |
| `tools/sense-gate-early-exit.py` | `python tools/test-sense-gate-early-exit.py` |
| `tools/fetch-benchmark-datasets.py` | `python tools/tests/test_fetch_benchmark_datasets.py` |

**Owner's machine only, and only after asking:** `./scripts/build.cmd`
(replaces `dist/`; close the tray app first — it locks the EXE),
`./scripts/run.cmd`, `scripts/verify-*.ps1`, and any
`--live`, `--*-benchmark` or `--translation-suite` run. On a clean clone
`build.cmd` fails on the missing model artifacts (`Get-AsteriaRequiredRankerFiles`
in `scripts/build-state.ps1`) or the missing `CN=Asteria Scale Local` signing
certificate. That is expected: report it, never weaken the checks.

**Never on your own:** `./scripts/install.cmd` (it runs winget with
`--accept-package-agreements` and registers the MSIX identity with
`Add-AppxPackage`), `scripts/install-*.cmd` or any `-AcceptUpstreamTerms` /
`-AcceptGemmaTerms` switch (the switches accept third-party terms on the user's
behalf; install-translategemma also downloads about 2.5 GB, and
install-consec installs a locally exported bundle), dataset or
dictionary-source downloads (including `tools/fetch-benchmark-datasets.py`),
certificate creation, `Add-AppxPackage`.

## Hard rules

1. **Canonical dictionary keys.** Every dictionary key is normalized at load
   with the exact lookup normalization (`NormalizeEnglish` /
   `NormalizeEnglishSentenceKey`; the full form is ARCHITECTURE.md invariant 1).
   `scripts/build-dictionaries.py::normalize_english` must produce the same
   form. The shared parity fixture is `scripts/dictionary-normalization-contract.tsv`,
   read by both the C# smoke (`LocalDictionaryNormalizationTests`) and the
   generator: add the case there, then change both sides together. After
   editing the generator run `python scripts/test-build-dictionaries.py`.
   `assets/Dictionaries/vn-build-manifest.json` pins the bytes of the generator
   and the VN inputs, so regenerate the VN layer with the command in
   docs/DEVELOPMENT.md («Регенерация словарей») instead of hand-editing
   hashes. It needs the MASSIVE archive, which is not in git; if an input is
   missing, stop and ask.
2. **GroupId == index.** Ranker plan outcomes, `visibleTranslations` and the
   result cache rely on sequential GroupIds and three coherent normalizations
   (resolver / cache / worker). Do not add or change deduplication in one place
   only.
3. **No silent provider fallback.** An NPU request runs on NPU or fails loudly.
   The host validates the worker's reported provider/device/model version. Both
   consumers share one `GlossBertDecisionCache`; a cache hit may skip inference
   but never the warm-up that establishes provider and model identity.
   Anything host and worker must agree on about a provider (wire name,
   reported execution provider/device, enriched glosses, bi-encoder prefilter,
   whether non-GlossBERT models may run) comes from `RankerProviderTraits`. A
   new provider = one traits entry + the exhaustive `Traits()` switches on both
   sides + an updated `tests/AsteriaScale.Tests/RankerProviderTraitsTests.cs`;
   only worker-internal session setup (VitisAI options, NPU/CPU artifact
   leases, NPU input/output shapes) may still branch on the worker's
   `RankerProvider` enum. `RankerEngine.cs` still tests the enum in one place
   where `AlternativeModels` is the trait (the ConSeC CPU-only guard) — do not
   copy that pattern. TranslateGemma follows the same rule: the
   CPU/Vulkan backend (and Vulkan's coopmat-free path) is explicit and part of
   the model identity, and a Vulkan failure never starts CPU.
4. **Abstention is margin-only** (threshold `0.25`, frozen on the dev set,
   defined once in `RankerAbstention.MarginThreshold` and folded into every
   decision cache key). Any change to acceptance criteria requires a
   `--ranker-benchmark` run on `assets/Ranker/ranker-benchmark.json` and a note
   in `docs/RESEARCH.md`. Benchmarks and A/B runs measure ConSeC
   (`--ranker-cpu-model consec`) unless the task is explicitly about another
   ranker: the owner works on ConSeC, while the CLI and code default is
   GlossBERT. Do not change that default. Name the ranker of every arm in
   `docs/RESEARCH.md`. If `src/AsteriaScale.RankerWorker/Models/Consec` is
   absent, stop and ask.
5. **Never consume `assets/Ranker/ranker-holdout*.json` while tuning** —
   holdouts are for one-shot final confirmation only; record each opening in
   `docs/RESEARCH.md`.
6. **IPC discipline.** After a request line is written to the ranker worker's
   stdin, exactly one response line must be drained even on cancellation; a
   protocol violation kills the whole worker tree. stderr never shares stdout.
7. **POS inference stays duplicated on purpose.** `SmartDictionaryHeuristicRanker`
   picks final answers (narrow); `SmartDictionarySenseResolver` only filters for
   the neural ranker (wider, fail-open). Shared primitives live in
   `EnglishContextLexicon` / `RussianMorphology` — put common linguistics there,
   not cross-links between the two consumers.
8. **Model artifacts are pinned** by size + SHA-256 — the ranker bundles and
   the TranslateGemma GGUF and llama.cpp runner archives alike; none is in git
   (ranker bundles: see .gitignore; TranslateGemma installs to
   `%LOCALAPPDATA%\AsteriaScale\Models\TranslateGemma`). Pins are duplicated across code (`RankerEngine.cs`,
   `ConsecArtifactBundle.cs`, `TranslateGemmaTranslationService.cs`, …),
   install/verify scripts, tests, `assets/Ranker/baselines`, READMEs and
   `THIRD_PARTY_NOTICES.md`: when re-pinning, `git grep -i <old-hash-prefix>`
   and update every hit in one commit, except `docs/RESEARCH.md`, whose entries
   are append-only history. Do not commit files over 100 MB.
9. **User-facing strings are Russian; code comments are English** and explain
   *why* (constraints), not *what*. Key comparisons use `StringComparer.Ordinal`.
10. **Dictionary layer priority** (user > curated VN > merged broad > dialogue)
    is a documented contract — do not reorder or switch broad layers back to
    first-wins semantics.
11. **Hashed bytes are LF.** Any file whose raw bytes are hashed or pinned
    needs an explicit `text eol=lf` (or `-text`) line in `.gitattributes` in
    the same commit; the current globs miss, for example, `*.jsonl` and nested
    `tools/**/*.json`. Compute pins from git blob bytes (LF), never from a CRLF
    working copy, and check with `git ls-files --eol <path>` (`i/lf w/lf`).
12. **Status lives in one table.** A roadmap or audit card's status lives in
    its own table (`docs/ROADMAP.md`, `docs/audit/pass-e-translation-quality.md`);
    closing it in `docs/RESEARCH.md` requires flipping that table in the same
    commit. Likewise, a changed rule, command or path is updated in AGENTS.md,
    docs/DEVELOPMENT.md and docs/PROMPTS.md in one commit.

## Testing conventions

- Smoke tests are plain `await RunAsync("name", ...)` blocks in
  `tests/AsteriaScale.Tests/Program.cs` or `internal static class XxxTests`
  files with a `RunAsync(tempRoot)` entry, registered in `Program.cs`.
- `InternalsVisibleTo("AsteriaScale.Tests")` is already in place.
- Dictionary tests must use the internal `LocalDictionaryService(bundled, user)`
  constructor with temp directories — never write into the real
  `%LOCALAPPDATA%\AsteriaScale\Dictionaries`.
- A behavior fix ships with a test that fails before the fix.

## Danger zones

- `dist/` is build output (and the user's installed app runs from it) — never
  edit it by hand; republish via `./scripts/build.cmd` (owner's machine, after
  asking).
- `MagpieConfigEditor` edits the user's real Magpie config; keep edits
  idempotent and preserve the `.before-asteria.json` backup semantics.
- The hover pipeline must never block or crash on translation errors — degrade
  to a dictionary result or hide the overlay.
