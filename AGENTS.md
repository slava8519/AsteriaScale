# Agent instructions for Asteria Scale

Asteria Scale is a Windows WPF app that upscales low-res game windows through
the external Magpie engine and translates on-screen text to Russian fully
offline (NPU OCR → layered dictionaries → GlossBERT sense ranking → optional
local Qwen). Read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) before touching
translation code; it lists the load-bearing invariants. Build/test/release
mechanics live in [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md). Ready-made task
prompts (in Russian) are in [docs/PROMPTS.md](docs/PROMPTS.md).

## Commands

```powershell
dotnet build AsteriaScale.slnx -c Release
dotnet run --project tests/AsteriaScale.Tests/AsteriaScale.Tests.csproj -c Release --no-build
./scripts/build.cmd   # publish to dist/ (close the tray app first — it locks the EXE)
```

Always run the full smoke suite after changes; it finishes in ~1 minute and
includes a real Magpie engine start/stop. Never pass `--live`, `--foundry-live`
or `--smart-live` unless explicitly asked.

## Hard rules

1. **Canonical dictionary keys.** Every dictionary key is normalized at load
   with the exact lookup normalization (`NormalizeEnglish`: lowercase, `’`→`'`,
   collapsed whitespace; sentence keys are punctuation-free token joins).
   `scripts/build-dictionaries.py::normalize_english` must produce the same
   form. If you change either side, change the other and extend
   `LocalDictionaryNormalizationTests`.
2. **GroupId == index.** Ranker plan outcomes, `visibleTranslations` and the
   result cache rely on sequential GroupIds and three coherent normalizations
   (resolver / cache / worker). Do not add or change deduplication in one place
   only.
3. **No silent provider fallback.** An NPU request runs on NPU or fails loudly.
   The host validates the worker's reported provider/device/model version.
4. **Abstention is margin-only** (threshold `0.25`, frozen on the dev set). Any
   change to acceptance criteria requires a `--ranker-benchmark` run on
   `assets/Ranker/ranker-benchmark.json` and a note in `docs/RESEARCH.md`.
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
8. **Model artifacts are pinned** by size + SHA-256 in `RankerEngine.cs` and
   `assets/Ranker/README.md`; they are not in git (see .gitignore). Do not
   commit files over 100 MB.
9. **User-facing strings are Russian; code comments are English** and explain
   *why* (constraints), not *what*. Key comparisons use `StringComparer.Ordinal`.
10. **Dictionary layer priority** (user > curated VN > merged broad > dialogue)
    is a documented contract — do not reorder or switch broad layers back to
    first-wins semantics.

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
  edit it by hand; republish via `./scripts/build.cmd`.
- `MagpieConfigEditor` edits the user's real Magpie config; keep edits
  idempotent and preserve the `.before-asteria.json` backup semantics.
- The hover pipeline must never block or crash on translation errors — degrade
  to a dictionary result or hide the overlay.
