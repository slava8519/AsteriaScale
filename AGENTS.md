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
A macOS version is planned as a native Swift app written from scratch (not a
port of the WPF host) on top of Core, RankerProtocol and the ranker worker; the
owner's decisions and open questions are in [docs/MACOS.md](docs/MACOS.md), and
its status is roadmap Track III (card 20). Do not reintroduce a text hook there:
Apple Vision is the only text source on macOS.

## Where code goes

Check the file path, not the namespace: the Core split kept the old
namespaces, so `AsteriaScale.Services` exists in two assemblies.

- `src/AsteriaScale.Core` — platform-neutral translation core (plain
  `net10.0`, no RID): dictionaries, morphology, sense-ranking host, phrase
  translation, caches, settings, the hover orchestration
  (`HoverCoordinator`: lifecycle, warm-up and unload, card order,
  cancellation, history, statuses), the unit-free OCR text rules
  (`OcrTextRules`: clean-up, sentence extraction, the two-observation
  agreement, frame reuse; `WindowsAiOcrService` imports them and keeps capture
  and pixel geometry until 20.3) and the card-kind predicates over engine
  strings (`HoverEngineLabelClassifier`; the WPF `HoverTranslationOverlay`
  imports them and keeps the card's texts and layout). CA1416 is a build
  error. Add no Windows binary names or paths; the existing ones listed in
  docs/ARCHITECTURE.md are known seams. Most types are `internal` (a few,
  such as `AppSettings`, `LocalDictionaryService` and
  `GlossBertDictionarySelectorService`, are `public`), and
  `InternalsVisibleTo` covers only `AsteriaScale`, `AsteriaScale.Tests` and
  `AsteriaScale.CoreHost` (the macOS Core host), so a new consumer of an
  internal type needs its own line. Core never derives a data path on its own:
  bundled data (dictionaries, morphology) and user state
  (settings, user dictionaries, history, caches, hover log, TranslateGemma
  models) come from `CoreDataLayout`, the ranker worker and its model folder
  from `RankerWorkerLocation`; their Windows defaults are the old paths.
- `src/AsteriaScale` — the WPF host and every Windows facility (UI, overlays,
  tray, hotkeys, Magpie, DPI, Windows AI OCR, Textractor).
  `HoverTranslationService` is only the Windows host of `HoverCoordinator`: it
  implements `IHoverHost<TAnchor>` (OCR and Textractor, cursor tracking, WPF
  cards, the speaker editor) and keeps its old public API. A change to hover
  lifecycle, ordering or history goes into the coordinator, not the adapter.
- `src/AsteriaScale.RankerProtocol` — the shared host/worker contract: JSONL
  protocol, `RankerProviderTraits`, `RankerAbstention`.
- `src/AsteriaScale.RankerWorker` — the ONNX worker. Some of its files are
  compiled into both test projects via `<Compile Include … Link>` in
  `AsteriaScale.Tests.csproj` and `AsteriaScale.Tests.MacOS.csproj`.
- `src/AsteriaScale.RankerWorker.MacOS` — the osx-arm64 CPU build of the same
  worker sources (roadmap 20.1). It links them, declares no Content (models are
  read in place through `--model-directory`, so no build copies a bundle) and
  is never added to `AsteriaScale.slnx`.
- `tests/AsteriaScale.Tests` — the smoke suite.
- `tests/AsteriaScale.Tests.MacOS` — the macOS subset of the same suite. It
  shares `Program.cs`; Windows-only parts sit under `#if WINDOWS` and declare
  themselves through `MacSmoke.cs`. Never added to `AsteriaScale.slnx`.
- `tools/macos/check-windows-identity.sh` — proves on a Mac that a change
  leaves every Windows build output byte-identical.
- `tools/macos/run-macos-subset.sh` — runs the built macOS subset in a
  temporary home, cwd and `TMPDIR` and fails on anything a run leaves there.
- `tools/macos/audit-verbatim-move.py` — audits a move or an edit of shared
  code line by line (`move`: multiset and order-aware) and compares the
  `[GeneratedRegex]` attributes with a base revision (`regex`).

A macOS-motivated edit to a shared source goes under `#if !WINDOWS` or into an
`#else` branch by default (the SDK defines `WINDOWS` only for the
`net10.0-windows…` targets), so the Windows-visible tokens stay unchanged;
prove it with the identity tool (exit 0, `IDENTICAL`). Roadmap 20.1 also has
five IL-neutral token edits outside `#if` that the tool proved identical (a
dead `using` removed, and four constants read from `PreviousContextLimits` in
Core instead of their equal `TextHookService` aliases).

Owner decision M12 (docs/MACOS.md) additionally lets Track III change
Windows-visible code now — Core, RankerProtocol, the ranker worker, the WPF
host and the shared tests — while the owner's Windows build and smoke run wait
until the macOS build is complete (M10). Every such change must, in the same
commit: (1) cross-build — run after the commit with a clean working tree,
`check-windows-identity.sh HEAD~1 HEAD` must finish all Windows builds and
then exit 1 with `DIFFERENT` (exit 4, a failed Windows build, is never
acceptable); (2) keep the macOS subset green; (3) add a row to «Windows-изменения
до прогона на Windows» in docs/MACOS.md saying what changed and what the
owner's Windows run must check; (4) carry in its message the changed manifest
lines, the `audit-verbatim-move.py regex --base HEAD~1` line and, for a move
or edit of shared code, both `audit-verbatim-move.py move` sections
(multiset and order-aware) with a substitution class for every residual
hunk. A change the tool proves `IDENTICAL` needs no row.

## Prerequisites

Windows 11 build 26100+, .NET 10 SDK, Magpie 0.12.1 (the "Installed effects"
and "Engine lifecycle" smoke tests use it), Python 3.11+ on `PATH` as `python`
(the smoke run executes `tools/translation-benchmark.py` and
`tools/atomic_output.py`), and the inbox
Windows PowerShell. Build and the default smoke run need no model artifacts
(ranker tests use fake bytes); the ConSeC tokenizer check is skipped
silently when that ignored bundle is absent, so a clean-clone pass covers
less than the owner's machine. `scripts/build.cmd` and every benchmark need the
real artifacts (docs/DEVELOPMENT.md).

On macOS: Apple Silicon, macOS 14.0+ (the minimum of the ONNX Runtime 1.27.1
osx-arm64 library), the arm64 .NET 10 SDK and Python 3.11+ as `python3`; no
PowerShell or Magpie. There only the CPU worker (ConSeC) and the macOS subset
of the smoke suite build and run. Report that run as the "macOS subset" with
its final `PASS (macOS subset): …` line, never as the smoke suite; the line
also states whether the ConSeC real-bundle checks ran. Keep a macOS clone
outside iCloud Drive-synced folders, or in a folder whose name ends in
`.nosync` (docs/DEVELOPMENT.md, «macOS (Apple Silicon)»).

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
The Python suites are offline and stdlib-only; the C# smoke does not run them.
On macOS run each command below as `python3 …`:

| After changing | Run |
|---|---|
| `scripts/build-dictionaries.py`, `scripts/dictionary-normalization-contract.tsv`, `tools/dictionary-source/vn-*` | `python scripts/test-build-dictionaries.py` |
| `tools/translation-benchmark.py` | `python tools/test-translation-benchmark.py` and `python tools/test-sense-gate-early-exit.py` (it loads `tools/sense-gate-early-exit.py`, which imports `translation-benchmark.py`) |
| `tools/sense-gate-early-exit.py` | `python tools/test-sense-gate-early-exit.py` |
| `tools/fetch-benchmark-datasets.py` | `python tools/tests/test_fetch_benchmark_datasets.py` |
| `tools/macos/audit-verbatim-move.py` | `python3 tools/macos/test-audit-verbatim-move.py` (needs `git`) |

**Safe on a macOS clone (Apple Silicon).** Build the worker and the macOS
subset (the test build also builds the worker first), run the subset through
the hermetic runner, and check the ConSeC worker with the bundle read in
place:

```bash
dotnet build src/AsteriaScale.RankerWorker.MacOS/AsteriaScale.RankerWorker.MacOS.csproj -c Release
dotnet build tests/AsteriaScale.Tests.MacOS/AsteriaScale.Tests.MacOS.csproj -c Release
bash tools/macos/run-macos-subset.sh
src/AsteriaScale.RankerWorker.MacOS/bin/Release/net10.0/osx-arm64/AsteriaScale.RankerWorker \
  --provider cpu --cpu-model consec --model-directory src/AsteriaScale.RankerWorker/Models --smoke
```

The runner builds nothing. Each run starts the built apphost in a fresh
`mktemp -d` folder W outside the clone, with cwd `W/cwd`, `TMPDIR=W/tmp` and
`HOME`/`CFFIXED_USER_HOME` at `W/home` (an empty `Library/Application
Support`; none with `--empty-home`), so it never writes into the real
`~/Library`. It then lists every entry left in cwd, home and tmp and exits 9
on one that `--allow <path relative to W>` does not name; a clean run leaves
nothing there, so the command above passes no `--allow`. `--repeat N` runs
it N times and stops at the first failure, keeping that folder. It also
counts the owner's crash reports (`~/Library/Logs/DiagnosticReports`, names
matching `^(AsteriaScale|dotnet|Python|python3|swift)`; read only) and exits
10 when the count grows; exit 2 is a usage error or a missing build, any other
code is the subset's own. Launching the apphost directly or with `dotnet run`
uses the real home: do not report such a run.

After editing a shared source or anything else the Windows build reads, run
the identity tool (`TMPDIR` must lie outside the clone). A change meant to be
Windows-neutral must print `IDENTICAL: <n> Windows output files` and exit 0;
the one expected exception is an edit to `THIRD_PARTY_NOTICES.md`, which the
WPF project copies, where exactly one manifest line,
`./Tests/THIRD_PARTY_NOTICES.md`, differs. A deliberate Windows change under
M12 ends with exit 1 `DIFFERENT` after successful builds; put the changed
manifest lines in the commit message. Exit codes: 0 identical, 1 different,
2 usage error or refused export, 3 licensed ConSeC payload in the working tree,
4 a Windows build failed. `--worktree` (the default) is only a pre-check: it
also builds untracked files, so a file never `git add`ed passes it. The
result that goes with a commit is the revision-mode run after it, with
`git status --porcelain` printing nothing:

```bash
bash tools/macos/check-windows-identity.sh <base-rev> [--worktree | <candidate-rev>]
bash tools/macos/check-windows-identity.sh HEAD~1 HEAD   # after committing
```

The identity tool compares source-generator outputs only as hashes of sorted
tokens, so it cannot tell a moved `[GeneratedRegex]` from a changed one. A
commit that changes Windows-visible code therefore also runs the regex guard,
which must print `GeneratedRegex attributes: identical (<n>)` and exit 0
unless the commit means to change a regex (exit 1 lists what was added and
removed). A commit that moves or edits shared code runs the move audit and
quotes both sections: `verbatim: K/N` (multiset, blind to order) and
`base residual R` / `destination-only D` (order-aware, file:line hunks; a
swapped or inserted statement always shows there). Both read the base
revision and the working tree; run them from the clone root (docs/DEVELOPMENT.md,
«Аудит переноса и `[GeneratedRegex]`»):

```bash
python3 tools/macos/audit-verbatim-move.py regex --base HEAD~1
python3 tools/macos/audit-verbatim-move.py move --base <rev> --from <file>... --to <file>...
```

Never build `AsteriaScale.slnx`,
`src/AsteriaScale.RankerWorker/AsteriaScale.RankerWorker.csproj` or
`tests/AsteriaScale.Tests/AsteriaScale.Tests.csproj` in the live tree on macOS:
they stop at NETSDK1100, and with `EnableWindowsTargeting` the Windows worker
schedules the local 1.6 GB CC BY-NC-SA ConSeC bundle into `bin/`. The identity
tool builds them only inside an export that cannot contain the bundle.

**Owner's machine only, and only after asking:** `./scripts/build.cmd`
(replaces `dist/`; close the tray app first — it locks the EXE),
`./scripts/run.cmd`, `scripts/verify-*.ps1`, and any
`--live`, `--*-benchmark` or `--translation-suite` run. On a clean clone
`build.cmd` fails on the missing model artifacts (`Get-AsteriaRequiredRankerFiles`
in `scripts/build-state.ps1`) or the missing `CN=Asteria Scale Local` signing
certificate. That is expected: report it, never weaken the checks. The macOS
subset rejects every option, so the gated modes and benchmarks stay
Windows-only until benchmark provenance records the platform (roadmap card 20).

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
   copy that pattern. The macOS worker build has no Windows ML: every NPU
   request fails with `npu_provider_unavailable` before any artifact is read
   (`--smoke` exits 1; a JSONL session answers each `warm`/`select` with that
   error and keeps serving), the default provider stays NPU, and that platform
   gate counts as worker-internal session setup. TranslateGemma follows the same rule: the
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
- `InternalsVisibleTo("AsteriaScale.Tests")` is already in place. The macOS
  test assembly keeps the name `AsteriaScale.Tests`, so it applies there too.
- Dictionary tests must use the internal `LocalDictionaryService(bundled, user)`
  constructor (`(bundled, user, sense)` for sense metadata) with a temp user
  directory — never the real `%LOCALAPPDATA%\AsteriaScale\Dictionaries`: every
  load creates the user directory and reads any `eng-rus*`/`jpn-rus*` layers
  in it. On macOS `MacSmoke.Begin` (macOS-only, the first statement of the
  run) configures a `CoreDataLayout` whose user half is an explicit error: a
  shared test that reaches a default user path (settings, user dictionaries,
  history, caches, TranslateGemma models) fails instead of writing, and the
  hover log is dropped. Such a test passes its own folder under `tempRoot`.
- A behavior fix ships with a test that fails before the fix.
- The macOS subset shares `Program.cs`. A registration that cannot run on
  macOS is wrapped as `#if WINDOWS … #else MacSmoke.Skip("<exact name>",
  "<the Windows-only API that forces it>"); #endif`; a Windows-only sub-check
  inside a registration that does run uses `MacSmoke.Part`. Never skip by
  catching an exception (`PlatformNotSupportedException`, `Win32Exception` or
  any other). The check "macOS: project parity with the Windows projects"
  reads `Program.cs` as each build compiles it (it understands only
  `#if WINDOWS`, `#if !WINDOWS`, `#else` and `#endif`) and fails unless the
  default-path `RunAsync` names of the Windows build and the names the macOS
  build runs or skips are identical, in the same order, without duplicates,
  and number `MacSmoke.SharedRegistrationCount`. So adding or removing a
  default registration, Windows-only ones included, needs the count updated
  in the same change, and a Windows-only one also needs its matching
  `MacSmoke.Skip`; renaming a Windows-only registration needs only the
  `MacSmoke.Skip` name changed with it. Otherwise the macOS run fails.

## Danger zones

- `dist/` is build output (and the user's installed app runs from it) — never
  edit it by hand; republish via `./scripts/build.cmd` (owner's machine, after
  asking).
- `MagpieConfigEditor` edits the user's real Magpie config; keep edits
  idempotent and preserve the `.before-asteria.json` backup semantics.
- The hover pipeline must never block or crash on translation errors — degrade
  to a dictionary result or hide the overlay.
