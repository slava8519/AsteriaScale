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
  cancellation, history, statuses, and for a host that answers requests each
  hover's `HoverOutcome`, a `HoverModelStatus` snapshot and host-requested
  unloads; the phrase model is optional), the unit-free OCR text rules
  (`OcrTextRules`: clean-up, sentence extraction, the two-observation
  agreement, frame reuse), the OCR pixel geometry (`OcrGeometry`: the line
  and word nearest the cursor, sentence and nameplate assembly from line
  boxes, screen block grouping, capture bounds and their reuse area; boxes
  in frame pixels, bounds in the host's screen pixels;
  `WindowsAiOcrService` imports both and keeps capture, Windows AI
  recognition, `ExtractRecognition`, its instance cache and diagnostics)
  and the card-kind predicates over engine
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
  Core does not name the text source either: engine labels and the source
  name in warm-up statuses come from the host's `HoverEngineLabels` (the
  default Windows set returns the old strings byte for byte, the macOS set
  says «APPLE VISION»), and every result and card carries a typed
  `HoverProvenance`, which a new host renders from instead of parsing the
  label. A new label goes into every set of `HoverEngineLabels`.
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
- `src/AsteriaScale.CoreHost` — the macOS app's Core host (owner decision M11):
  Core as a separate process speaking JSONL protocol v1 on stdin/stdout.
  Platform-neutral `net10.0` without a RID, but `Main` refuses every platform
  except macOS; it references only Core, declares the WPF project's
  `Dictionaries`/`Morphology` data items, keeps its own code NativeAOT-clean
  (source-generated JSON only; the trim and AOT analyzers fail the build) and
  is never added to `AsteriaScale.slnx` or referenced by a Windows project.
  Its launch arguments are exactly
  `tests/AsteriaScale.Tests.MacOS/CoreHost/launch-args-v1.txt`, its wire bytes
  are pinned by `protocol-v1.golden.jsonl` next to it, and its `test.*`
  commands exist only with `--enable-test-commands`. After `core.init` it
  serves hovers, settings, model status, history, «Мой словарь» and speaker
  genders through Core's `HoverCoordinator` (Apple Vision labels, no phrase
  model before 20.5). A hover's text is the app's selection or its Apple
  Vision observations, which CoreHost selects in with Core's `OcrGeometry`
  and `OcrTextRules` (`ocr.plan` gives the capture bounds; the one-entry
  frame cache and the copy of `ExtractRecognition`'s filters stay in
  CoreHost, docs/MACOS.md T27 and T28, and a CoreHost check fails when that
  method or the 900x340 dictionary capture changes in
  `WindowsAiOcrService`); `ocr.select` is the same selection without state and
  needs only `core.hello`, and `frame-fingerprint-v1.txt` beside the golden
  pins the frame fingerprint's wire form. It is the only writer of
  `settings.json`, writes the macOS first-run settings (ConSeC on the CPU,
  docs/MACOS.md T7) instead of changing the shared `AppSettings` defaults,
  and shows a sense-ranker error as Russian text naming the code
  (`Host/RankerErrorText.cs`; paths only in `detail`). A settings change
  (`settings.update`, `speaker.set`) joins Core's settings queue when it is
  admitted, on the reader thread, never from its pool work, so a hover,
  `ocr.plan`, warm-up, unload or change admitted after it sees it however
  late the pool runs; the Core-backed check pins this with the test command
  `test.delayNextWork`. Both changes write a changed copy of the settings
  first and reach Core only after the write succeeded:
  `speaker.set` changes a copied speaker list by Core's alias rule (mirrored
  in `StoreSpeakerGender`) and refuses a `game.key` whose normalization
  changes again on a second pass, so Core's own `SaveSpeakerGender` after
  the swap stores and saves nothing unless a hand-edited `settings.json`
  holds the same speaker under such a key (a save of Core's own is logged
  to stderr and fails the check), and it stores the choice under the card's
  `observedName` as it is, the spelling CoreHost records in
  `UpgradeRecognized` before Core resolves it to a display name.
- `macos/AsteriaScale` — the native macOS app (owner decisions M2, M11,
  M13-M15), a SwiftPM package built with the Command Line Tools only (no
  Xcode project) and never added to `AsteriaScale.slnx`.
  `Sources/AsteriaScaleKit` holds everything that runs without a window
  server, Foundation and Darwin only: the strict protocol-v1 client
  (`CoreConnection`), `ChildProcess` (posix_spawn: own process group,
  `CLOEXEC_DEFAULT`, an empty signal mask, default SIGPIPE, SIGTERM, SIGINT
  and SIGHUP), the
  `CoreSupervisor` (hello checks, optional `core.init`, ping, restart with
  backoff, orderly stop; the process group is killed before the zombie is
  reaped), the single-instance `InstanceLock`, the `OrphanSweep` of a group a
  killed app left behind, `AppPaths` (CoreHost's arguments exactly per
  `launch-args-v1.txt`, its environment with `DOTNET_EnableDiagnostics=0` and
  a private `TMPDIR`), the default hotkeys ⌥T, ⌥D, ⌥V and ⌃⌥G (M14, M20;
  the speaker editor's `editSpeaker` is the last `HotkeyAction`, whose
  order gives the Carbon ids), the
  settings and model status the app reads (`SettingsClient`, the only path
  to `settings.json`, which CoreHost alone writes; `ModelsStatus`), how a
  hover's end is shown (`HoverReport`), the app log, the bundle id
  `io.github.slava8519.AsteriaScale` (`AppIdentity`, M13) and every
  user-facing string in `RU` (`Strings.swift`). Those strings are Russian and
  say «перевод»/«служба перевода», never the internal word for the host
  (M15); the Swift suite and the C# parity check scan the package for it.
  Kit also holds the parts of Apple Vision (20.3) that need no Vision: the
  frame and recognizer types (`Frame` in the dense BGRA layout
  `frame-fingerprint-v1.txt` pins, the `TextRecognizer` protocol, the T23
  `RecognizerConfiguration`), `CaptureGeometry` (Cocoa global points to the
  display under the cursor and its whole pixels, capture bounds back to the
  capture's points), `VisionGeometry` and `WordSplitter` (Vision's
  normalized boxes to frame pixels, words with UTF-16 ranges), the frame
  fingerprint's wire form behind the `FrameFingerprinting` seam, the typed
  protocol-v1 observations with CoreHost's limits and the 900 KiB budget on
  the encoded request line (`observations_too_large`, never truncated; a
  recognized word CoreHost would refuse, a non-finite box or more than 256
  UTF-16 units, is left out of its line instead), and
  `ObservationClient` (`ocr.plan`, `ocr.select`, the observations hover with
  `hover.loading` and `stale_frame`). Kit also decides every hover over the
  screen (20.3, DoD 7 and 8), as pure code over injected seams:
  `HoverPipeline` (the permission first, then `ocr.plan`, frame 1 and its
  fingerprint, Vision skipped when the plan's cached fingerprint matches, in
  «Умный словарь» frame 2 taken only after the first recognition, the
  observations hover; exactly one retry with full observations on
  `stale_frame`, one new plan when CoreHost refuses a hover as
  `bad_request` and the mode turns out changed, `frame_mismatch`, the
  budget, a cancellation at any step) over `FrameSource`,
  `CapturePermission`, `TextRecognizer` and `GameResolver` (the screen
  source's instance, so only with capture on, where screen hovers exist at
  all; `NoGameResolver` stands in only in the self-test's refused hover;
  the game it names reaches the
  app before any hover.resolve), `HoverTracker` (a pure state machine on an
  injected `MonotonicClock`: 90 ms polls, 15 display pixels, the settings'
  delay clamped to 350..1500 ms, an own window only hides the card, a hover
  takes the cursor of the poll that starts it and its cards, a phrase line
  and an end message are re-checked against it; a new mode or new smart
  settings restart it, as Windows' `SetMode` and
  `ApplySmartDictionarySettings` reset tracking: a new mode hides the card
  at once, new smart settings at the next poll), `OverlayState` (who owns
  what the overlay shows: a screen hover owns nothing until it shows
  something, so the tracker's hide and an end without a message leave a
  ⌥V or «Проверка перевода» card alone (an end message, shown while the
  cursor is still there, replaces it), and the tracker's hide drops the
  rest of that hover, its phrase line included), `HoverReport.afterScreenHover` (the end
  of a screen hover: the re-check on its end message, a withheld card noted
  as withheld), `CaptureGate` (capture only without `--self-test`, with
  `getppid() == 1` and `__CFBundleIdentifier` and the bundle both M13's;
  its `Permit` is made only in `decide`, its initializer fileprivate),
  `CaptureFailure` (-3801 is the permission message) and `RecognitionStatus`
  (the menu's recognition line; a tracked hover's refused capture brings
  back the permission line and item and stops tracking until a relaunch;
  no tracking while the speaker editor is open). Kit also names the game
  and holds the speaker editor's model (20.3, DoD 5, docs/MACOS.md T29-T32):
  `ProcessArguments` parses a KERN_PROCARGS2 record from bytes (any byte
  string ends in a record or nil, never a trap; the run of NULs Wine's
  in-place argv rewrite leaves after the arguments is skipped; empty
  arguments at the start, an empty argv[0] and the empty ones right after
  it, cannot be told from the padding and are skipped with it, shifting
  as many environment entries into the arguments) and
  `GameIdentity` takes the frontmost window of another process under the
  cursor that is layer 0, or above it when its owner is Wine (a full-screen
  bottle measured at layer 26 on CrossOver 25.1.1; any other window above 0
  is passed over,
  the desktop never taken), from the window list's owner pid, layer and
  bounds, and names `path:` its
  executable, or for Wine (`wine*`, `*.exe`, anything inside CrossOver.app
  or a `winetemp-*` folder, where CrossOver 25.1.1 runs every process of a
  bottle from a loader copy named after the first program) the Unix
  path of the first fully qualified .exe in its arguments outside Wine's own
  programs, C: as `<WINEPREFIX>/drive_c` and other letters through the
  text of the link `<WINEPREFIX>/dosdevices/<x>:`, read with readlink and
  joined by name, never followed (its target is never looked up; no
  prompt for a bottle in a protected folder is owner check 12.4); never
  Wine's own path or `process:`, and no game rather than a key Core would
  store under another spelling or CoreHost would refuse (T32: the key's
  own end only, a bottle or drive folder may end in a space or a quote).
  `SpeakerEditor` opens
  only on a shown card of a screen hover with a speaker and a game (else
  «Под курсором нет имени говорящего» or «Не удалось определить игру…»,
  shown as an `OverlayState.notice`, which supersedes no hover in flight),
  maps keys 1, 2, 3 and Esc, and sends speaker.set with the hover's game,
  the card's observedName as it is and the name as Core keeps it;
  `SpeakerClient` sends it, and `OverlayState.suspend` keeps every screen
  hover off the overlay while the editor is open. `Sources/AsteriaScaleVision` is Apple
  Vision itself, Foundation, Vision, CoreGraphics, CoreText, ImageIO and
  CryptoKit only (no AppKit, no screen capture): `VisionTextRecognizer`
  (T23; frames below 1.5 pixels per point reach Vision enlarged twice
  through `PixelBuffer.visionImage`, T24, and the recognizer reports the
  size Vision was given), the in-memory CoreText fixture
  (`FixtureRenderer`, `VisionFixture`, and `FixtureFrameSource`, which puts
  it on a frame of a plan's bounds for the self-test; no image is
  committed), `PixelBuffer` and the CryptoKit `SHA256FrameFingerprinter`
  (T27).
  `Sources/AsteriaScale` is the menu-bar app (AppKit, SwiftUI, Carbon,
  ScreenCaptureKit): the status item (with the recognition line and, only
  while capture is on and not permitted or a tracked hover's capture was
  refused, «Разрешить запись экрана…»), the
  click-through overlay, the Carbon hotkeys, the settings and «Проверка
  перевода» windows, the speaker editor «Род говорящего»
  (`SpeakerEditorPanel.swift`: ⌃⌥G on a shown card opens a non-activating
  panel near it, the card hides and the tracking pauses until it closes;
  the cursor is never moved, T31), `HoverDriver` (the glue from the tracker through the
  pipeline to the overlay), `ScreenSource.swift` (the screen capture,
  constructed once at launch only when `CaptureGate` says on; the only file
  that names ScreenCaptureKit, the Screen Recording preflight and request,
  the window-list API and another process's KERN_PROCARGS2 record; the
  preflight, the request, the capture and the game read (`GameResolver`)
  are instance members, and the self-test's window-list read is its one
  static) and
  the scripted `--self-test`; CoreHost's
  free-text statuses appear only in «Проверка перевода» and `app.log`, never
  in the menu or the overlay. `Bundle/` holds the Info.plist template and
  the entitlements `tools/macos/build-app.sh` signs with.
  `Tests/AsteriaScaleVisionProbe` is the DoD 6 probe of 20.3 (docs/DEVELOPMENT.md,
  «Проба Vision»): `test-swift.sh` builds it beside the test bundle, it is
  never part of the app and the suite never runs it. No `#Preview` or
  other Xcode-only macros (the Command Line Tools ship no PreviewsMacros
  plugin) and no XCTest (they have none): tests are Swift Testing only,
  through `tools/macos/test-swift.sh`.
- `tests/AsteriaScale.Tests` — the smoke suite.
- `tests/AsteriaScale.Tests.MacOS` — the macOS subset of the same suite. It
  shares `Program.cs`; Windows-only parts sit under `#if WINDOWS` and declare
  themselves through `MacSmoke.cs`. The CoreHost checks live in its
  `CoreHost/` folder. Never added to `AsteriaScale.slnx`.
- `tools/macos/check-windows-identity.sh` — proves on a Mac that a change
  leaves every Windows build output byte-identical.
- `tools/macos/run-macos-subset.sh` — runs the built macOS subset in a
  temporary home, cwd and `TMPDIR` and fails on anything a run leaves there.
- `tools/macos/audit-verbatim-move.py` — audits a move or an edit of shared
  code line by line (`move`: multiset and order-aware) and compares the
  `[GeneratedRegex]` attributes with a base revision (`regex`).
- `tools/macos/test-swift.sh` — builds CoreHost and the Swift package (any
  warning fails) and runs its Swift Testing suite in a throwaway home with
  SwiftPM's state under `.build/swiftpm-state`, failing on anything left
  behind.
- `tools/macos/build-app.sh` — builds, assembles, signs (inside out, hardened
  runtime, ad-hoc by default) and verifies `Asteria Scale.app`, runs it
  only in a throwaway home (`--self-test`, `--run-isolated`) and installs a
  verified copy in place (`--install [DIR]`, docs/MACOS.md T34). Both scripts
  end with a report of the cache entries outside the run's home that are
  newer than the run, whoever wrote them (T35).

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
osx-arm64 library), the arm64 .NET 10 SDK, Python 3.11+ as `python3` and the
Command Line Tools with Swift 6 (their `/usr/bin/python3` runs the Swift
suite's fake CoreHost); no PowerShell or Magpie. Xcode.app may be installed
for the owner's signing certificate, but `test-swift.sh` and `build-app.sh`
export `DEVELOPER_DIR=/Library/Developer/CommandLineTools` whenever the
Command Line Tools are installed (docs/MACOS.md T33). There only the CPU
worker (ConSeC), CoreHost, the Swift package `macos/AsteriaScale` and the
macOS subset of the smoke suite build and run. Report that run as the "macOS subset" with
its final `PASS (macOS subset): …` line, never as the smoke suite; the line
also states whether the ConSeC real-bundle checks (worker smoke and the
CoreHost smart hover) ran. Keep a macOS clone
outside every File Provider domain (iCloud Drive's Desktop and Documents,
`~/Library/Mobile Documents`, the folders in `~/Library/CloudStorage`), for
example in `~/Developer/AsteriaScale`: a `.nosync` name stops the upload, not
File Provider's tags (`com.apple.FinderInfo`) on every new `.app` folder,
which codesign rejects, so `tools/macos/build-app.sh` refuses such an output
with exit 2. Moving a clone is the owner's, never an agent's
(docs/DEVELOPMENT.md, «Клон и iCloud Drive», also for the stale
`com.apple.fileprovider.dir#N` tags a moved clone keeps, which are harmless
outside a domain).

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
| `tools/macos/crash-reports.py` | `python3 tools/macos/test-crash-reports.py` |

**Safe on a macOS clone (Apple Silicon).** Build the worker, CoreHost and the
macOS subset (the test build also builds the worker and CoreHost first; the
CoreHost build must report 0 warnings), run the subset through the hermetic
runner, and check the ConSeC worker with the bundle read in place:

```bash
dotnet build src/AsteriaScale.RankerWorker.MacOS/AsteriaScale.RankerWorker.MacOS.csproj -c Release
dotnet build src/AsteriaScale.CoreHost/AsteriaScale.CoreHost.csproj -c Release
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
lists the owner's crash reports (`~/Library/Logs/DiagnosticReports`, read
only) before and after with `tools/macos/crash-reports.py` (G-crash) and
exits 10 on a new one: a report counts when its name, or the crashed
process, its parent or its killer recorded in it, starts with
`AsteriaScale`, `dotnet`, `Python`, `python3` or `swift`, so a child spawned
under a system name (a `sleep` started by `swiftpm-testing-helper`) counts
too. Exit 2 is a usage error or a missing build, any other code is the
subset's own. Launching the apphost directly or with `dotnet run`
uses the real home: do not report such a run.

Never start CoreHost (or any helper a test spawns) with the real home: the
subset's CoreHost checks spawn it with `HOME`/`CFFIXED_USER_HOME` at a
private home (both: .NET's LocalApplicationData on macOS ignores `HOME` and
follows `CFFIXED_USER_HOME`, or the account's real home when that is unset),
a private `TMPDIR`, `DOTNET_EnableDiagnostics=0` (a killed .NET
process otherwise leaves debugger FIFOs in `TMPDIR`) and an empty cwd, and
fail if anything is left there. No automated check may crash a process by
abort or a fault signal (`Environment.FailFast`, an unhandled exception,
SIGABRT, SIGSEGV, a Swift trap such as an index out of range): ReportCrash
then writes into the owner's real `~/Library/Logs/DiagnosticReports`
whatever `HOME` was, so a Swift test checks a size with `#require` before it
indexes, and a regressed or mutated build fails the test instead of
trapping `swiftpm-testing-helper`. A sudden death is
simulated with `_exit` (CoreHost's `test.exit`) or SIGKILL, which leave no
report. Never copy a system binary (`/bin/sleep`, anything under `/usr/bin`)
out of place and run it: outside the sealed system volume it is no longer a
platform binary, the kernel marks its signature invalid, and its death, even
by the test's own SIGKILL, leaves a "SIGKILL (Code Signature Invalid)"
report. A test
that needs an executable inside its scratch folder copies the package's own
`AsteriaTestSleeper` (Swift) instead.

Build and test the Swift package only through the script (it also builds
CoreHost first, because the suite launches it to `core.hello` and
`core.init`):

```bash
bash tools/macos/test-swift.sh       # about 20 s, about a minute from an empty .build
bash tools/macos/test-swift.sh -- --filter ConnectionTests
```

Report its result as "Swift suite: N tests", from Swift Testing's `Test run
with N tests in M suites passed`; it is neither the smoke suite nor the macOS
subset. The script runs every `swift` command with `HOME`/`CFFIXED_USER_HOME`
at a throwaway home, its own `TMPDIR` and an empty cwd inside a `mktemp -d`
folder W, with `--cache-path`, `--config-path`, `--security-path` under
`macos/AsteriaScale/.build/swiftpm-state` and `--manifest-cache local`: even
with those flags SwiftPM creates `~/.swiftpm` and `~/Library/org.swift.swiftpm`
in the home it is given, so a plain `swift build` or `swift test` writes into
the real `~/Library`. A `warning:` in the release or test build fails the run
(exit 4). `swift test --skip-build` starts only when the test bundle exists:
without it `swiftpm-testing-helper` dies of a fatal error (SIGTRAP), which
writes a crash report into the owner's DiagnosticReports. Afterwards the
suite's scratch folder and cwd must be empty and its home and `TMPDIR` may
hold only SwiftPM's own entries (exit 9), no process the suite started may be
left (exit 11, killed; found by the run's token or W, so a concurrent run's
processes are never touched), nothing may be newer than the run in the real
`~/Library/Caches/org.swift.swiftpm`, `~/Library/org.swift.swiftpm` or
`~/.swiftpm` (exit 12), and no new crash report of the run's processes may
appear (exit 10, the G-crash listing above); exit 3 is a failed CoreHost
build, 2 a usage error, missing Command Line Tools frameworks or a
crash-report folder that cannot be listed, any other code `swift test`'s
own. It then prints, as a report and never a failure
(docs/MACOS.md T35), the entries of `getconf DARWIN_USER_CACHE_DIR` (to
depth 2) and of the real `~/Library/Caches/com.apple.e5rt.e5bundlecache`
(to depth 3) newer than the run's start, whoever wrote them, at most 40
lines per folder (also when the run stops after its builds started: its
EXIT trap prints it and removes the marker): that cache folder belongs to
the account, not to `HOME`, so no throwaway home covers it. List new
entries in your report; never delete them (removal is the owner's).

Build the app and run its self-test only through the script; read the
owner's ConSeC bundle in place, never copy it:

```bash
bash tools/macos/build-app.sh --self-test --ranker-models src/AsteriaScale.RankerWorker/Models
bash tools/macos/build-app.sh --run-isolated --ranker-models src/AsteriaScale.RankerWorker/Models
```

The default `--output` (`artifacts/macos` in the clone) works only in a clone
outside every File Provider domain. Before it creates the output, in every
mode including `--no-build`, the script reads (only, with `/usr/bin/xattr`)
the extended attributes of the output's nearest existing folder, resolved
with `pwd -P`, and of every folder above it, and exits 2 with nothing built
or created when one of them is a domain root
(`com.apple.file-provider-domain-id`), when the part of `--output` that does
not exist yet has a `.` or `..` component, when a part of it exists but is
not a folder (a file, or a link to nothing or to a file), or when it cannot
resolve that folder or read a folder's attributes. A
`com.apple.fileprovider.*` tag on the nearest folder with no domain root
found above it (the `dir#N` a moved clone keeps) only prints a one-line
warning: outside a domain it is inert (measured on 2026-11-10). For a folder
in the clone the warning names the owner's optional removal, which acts on
links themselves (`xattr -s`), never on what they point at. Do not move the
clone or remove such tags yourself: report a domain to the owner, or pass
`--output` outside every domain (a new path is a new LaunchServices record
to list).

Report the self-test as "app self-test: SELFTEST PASS n/n" (plus the
`codesign` lines "valid on disk" and "satisfies its Designated
Requirement"); without `--ranker-models` its in-flight ConSeC line says
`absent: <path>`. The script publishes CoreHost and the worker with
`--artifacts-path` under its output's `.work` folder, so the clone's `bin/`
and `obj/` stay as they are, and stops with exit 3 on any model file in the
bundle. The app runs only by direct exec in a throwaway home (`HOME` and
`CFFIXED_USER_HOME` at `W/home`, its own `TMPDIR`, an empty cwd);
`--run-isolated` prints `W` and keeps it, `--home W` reuses it, and ^C or
`kill -TERM` to the script reaches the app, which quits in order (a
`--self-test` stopped that way ends with 130 or 143 and keeps its folder). Never launch the app in the real home
(`open`, a copy in `~/Applications`, a double click): that, and copying the
ConSeC bundle into `~/Library/Application Support/AsteriaScale/Models/Ranker`
with `ditto`, are the owner's alone (docs/MACOS.md T9). Every direct launch
also registers the bundle's path in the owner's LaunchServices database
(outside `~/Library`, so exit 12 cannot see it), and the record outlives the
bundle: never clean it (`lsregister -u <path>` or `-gc` is the owner's) and
list the bundle paths you launched in your report. A physical press of
⌥T, ⌥D, ⌥V or ⌃⌥G is an owner check too: the self-test proves registration and
the handler, not that the window server delivers a real key. Stage 20.3
(Apple Vision and screen capture) adds four rules. No agent run calls
ScreenCaptureKit, `CGPreflightScreenCaptureAccess`,
`CGRequestScreenCaptureAccess` or anything else that can raise a privacy
(TCC) prompt (Screen Recording, Accessibility, Input Monitoring), and no
test, probe or script creates a `ScreenSource` or calls its capture,
permission or game members (the window list, another process's
arguments and environment): that code is compiled and scanned, never run;
`GameIdentity` is tested on synthetic bytes and folders only, and the
self-test's speaker editor takes its game from a stub resolver and is
driven without making it key, activating the app or changing the active
app; only
`ScreenSource`'s static `ownWindowRecord` (one window-list read of the app's own panel,
no ScreenCaptureKit, no permission) runs, in the self-test's
`overlay.windowServer`. A real capture, the cursor tracking and the first grant
are owner checks, and the app turns capture on only when the owner
launches it through LaunchServices (`CaptureGate`, docs/MACOS.md T22): in
a self-test, `--run-isolated` or any other direct exec it is off, no
screen source exists, the cursor is not tracked, the app warms no Vision
and the menu says «Распознавание отключено: проверочный запуск». The real Vision recognizer runs
only in the app self-test and in the DoD 6 probe, both only in a throwaway
home: the probe is a copy of `AsteriaScaleVisionProbe` re-signed with
`codesign -s - -o runtime -f` (no entitlements) and started by direct exec
with `HOME`/`CFFIXED_USER_HOME`, `TMPDIR` and an empty cwd in a folder
from `mktemp -d "${TMPDIR:-/tmp}/asteria-probe.XXXXXX"` outside the clone
(macOS's bare `mktemp -d` ignores `TMPDIR`), with the crash reports listed
before and after (docs/DEVELOPMENT.md, «Проба Vision», has the commands).
Vision's first call there compiles its models into that home's
`Library/Caches/<process name or bundle id>/com.apple.e5rt.e5bundlecache`
(about 24 s, again for a rebuilt binary in the same home). It also
updates entries outside every throwaway home, in `getconf
DARWIN_USER_CACHE_DIR`: `com.apple.metal/<n>` (the probe) and
`io.github.slava8519.AsteriaScale/com.apple.metal/<n>` (the app),
attributed by timing; list them in your report, never delete them (T35's
depth-2 listing names the app's folder only when it is new). The Swift
suite and the macOS subset use synthetic observations and never render
text (T26).
Agents sign ad hoc only: the
owner's Apple Development identity (`--sign`, `ASTERIA_SIGN_IDENTITY`, M16)
is for the owner's builds, and using it can raise a keychain dialog, so pass
`--sign -` whenever `ASTERIA_SIGN_IDENTITY` is set. `build-app.sh --install
DIR` (T34) goes only into a folder inside `mktemp -d`, always with an
explicit DIR and in a full build with its own `--output` (`--sign` does
nothing with `--no-build`, whose copy keeps the bundle's signature), and
never combines with `--run-isolated`. Without DIR it means `~/Applications`,
the owner's alone, and it refuses (exit 2) to go there with the identity
`-`, so an unquoted empty `$dir` never reaches it. It installs only after
every check of the run passed (with `--self-test`, after the self-test),
copies with `ditto` into a temporary `DIR/.asteria-install.*`, compares the
bytes, verifies the copy with `codesign`, re-checks the target and swaps it
in with one atomic `renamex_np` (the earlier copy of this app found before
the build is replaced in place), verifies the installed copy and prints its
two `codesign` lines; it never launches the copy and never calls
`lsregister`. Before anything is created it refuses (exit 2) a DIR in a
File Provider domain or with the other `--output` path faults, a DIR that
is or lies in the output folder (or holds the output in its bundle), a
`DIR/Asteria Scale.app` that is a link, a file, a bundle of another id (a
foreign bundle is never replaced) or stored under another spelling, and any
process running from it, found by the executable path the kernel reports
(`proc_pidpath`, however the copy was started, compared ignoring case) and
by `pgrep -i -f`. At the swap (exit 7) it refuses a process that started
from the target during the run and a target that appeared, went or changed
since the check before the build; a failed mkdir, copy, comparison or
rename is exit 8, all three with nothing replaced. From that late re-check
to the end of the run INT and TERM are ignored, so the install completes
or is undone; a ^C or `kill -TERM` before it ends the run with 130 or 143
and nothing replaced. The crash reports are listed again after the install
(exit 10, the install stands). A fake running copy in a check is a copy of
`AsteriaTestSleeper`. Like `test-swift.sh`, it ends with the T35 cache
report (also printed when the run stops early). Exit 4 is a
failed build or a warning (from `swift build` or either `dotnet publish`,
which runs at `-v minimal` so MSBuild prints its warnings), 5 signing or
verification (on codesign's "detritus" or "Disallowed xattr" it also names
the extended attributes on the bundle and marks the likely culprits), 6 a
failed self-test, 9 a file left in the run's cwd, 10 a new crash report (the
G-crash listing, also after the install) or a listing that fails after the
build, 11 a process left
running (killed when the run started it, only listed otherwise), 12 a new
entry in the real `~/Library` named after the app or SwiftPM, 7 and 8 the
`--install` failures above, 2 a usage error, an `--output` or `--install`
DIR inside a File Provider domain (or with `.` or `..` in its missing part,
or a file or a link to no folder on its way), a `TMPDIR` inside the clone,
a bad `--home`, `--no-build` without a bundle, an unreadable crash-report
folder, a process already running from the bundle, `--install` without DIR
with the identity `-` or an install target refused above. Every exit 2
comes before the output folder is
created, so none leaves it behind, not even empty: the script has built,
created, replaced and killed nothing, and an owner's running copy keeps its
bundle and its CoreHost.

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
- The CoreHost checks ("macOS: CoreHost protocol conformance", "… lifecycle
  and stdio isolation", "… fault containment", "… answers a hover through
  Core") are macOS-only registrations in the `#else` branch at the end of
  `Program.cs` (`MacSmoke.MacOnlyRegistrationCount`, seven macOS checks in all).
  They start the real apphost through `posix_spawn` (own process group,
  `CLOEXEC_DEFAULT`, an empty signal mask) and talk to it with a strict
  client that enforces one terminal per request and a Russian message
  without «ядро» (M15) in every error, fault and status. A change to the
  launch arguments changes `launch-args-v1.txt` in the same commit; a change
  to the wire bytes changes `protocol-v1.golden.jsonl`. The only helper in
  `tests/` that may create a symbolic link is
  `CoreHostFixtures.CreateLinkToEmptyDirectory` (its target is a fresh empty
  folder under the run's temporary root); the project parity check enforces
  it. The Core-backed check reads the owner's ConSeC bundle in place
  (`--ranker-models src/AsteriaScale.RankerWorker/Models`) and reports
  `CoreHost smart hover: checked in place` or `absent: <path>`. The parity
  check also fails on «ядро» anywhere in CoreHost's sources or in the macOS
  app's Swift sources and string catalogs (M15).
- The Swift suite (`macos/AsteriaScale/Tests/AsteriaScaleKitTests`) is Swift
  Testing only. Every suite that starts a process is nested in the
  serialized `ProcessSuites`; a test puts its folders under `Scratch` (the
  script's `ASTERIA_TEST_TMPDIR`) and removes them (the only symbolic
  links the suite makes are `GameIdentityTests`' bottle `dosdevices` links,
  inside its scratch folder and pointing inside it), gives every child it
  spawns its own home and `TMPDIR`, and marks every `/bin/sleep` it starts
  with the duration `sleepMarker` (`3141.59` followed by the run's ten-digit
  `ASTERIA_TEST_RUN_TOKEN`; the fake gets the token as `-X asteria_run=…`),
  by which the script finds this run's leftovers and no other run's. An
  executable that must lie inside a scratch folder is a copy (named `sleep`,
  with the same marker) of the package's `AsteriaTestSleeper`, which SwiftPM
  builds beside the test bundle (`TestSleeper.url()`), never a copy of a
  system binary. Faults
  come from the stdlib fake `Fixtures/fake-corehost.py` (protocol v1 under
  `/usr/bin/python3`), never from crashing a real process; its hooks for
  `HoverPipelineTests` are CoreHost's `test.delayNextWork` (the next
  `ocr.plan` or `ocr.select`), a card «slow» that holds an observations
  hover until `request.cancel` (the fake's own, like its selection
  «slow»), and a stderr note per `request.cancel`; the real CoreHost
  is launched only by `realCoreHostReachesHello`, with `AppPaths` arguments
  in a throwaway home. A change to CoreHost's arguments or wire bytes
  updates the Swift side in the same commit: `launchArgumentsMatchGolden`
  and the golden-transcript tests read the same two files as the C# checks,
  and `FrameFingerprintTests` reads `frame-fingerprint-v1.txt` beside them.
  No test runs Vision or renders text (fonts may write caches into the
  run's home, which `test-swift.sh` refuses): `SourceScanTests` walks the
  package and fails on a test import other than Testing, Foundation,
  Darwin, CoreGraphics, Carbon and the package's modules (any form:
  attributes, an access modifier, `;`, a block comment inside the
  statement, an attribute on a line of its own or the module on the next
  line; text in a comment or a string literal is no import, and a file
  that leaves a comment or literal open fails; Vision, CoreText and AppKit
  are caught on the raw line too, even in a comment), on a Vision type, a
  text-recognition request or handler, `FixtureRenderer`, `VisionFixture`,
  `FixtureFrameSource` or a CoreText, CoreGraphics or HIToolbox font or text-drawing call in a
  test, on a Kit import other than Foundation and Darwin, on an
  AsteriaScaleVision import outside DoD 6's list (the same forms), and on a
  screen-capture or permission (TCC) API outside
  `Sources/AsteriaScale/ScreenSource.swift` (DoD 8, T22: DoD 8's names,
  every `CGWindowList*` name and the Swift `CGImage(windowListFrom…)`).
  That file may hold ScreenCaptureKit, its three types the capture uses and
  the two Screen Recording calls on any line, and of the window-list API
  only the two lines of the allow-list: `CGWindowListCopyWindowInfo` in the
  static helper through which 20.2's `overlay.windowServer` self-test check
  reads its own panel's record, which needs no instance and asks for no
  permission, and in the instance's game read. Of the window list's keys
  only `kCGWindowOwnerPID`, `kCGWindowLayer`, `kCGWindowBounds`,
  `kCGWindowSharingState` and `kCGWindowIsOnscreen` appear on any line of
  any source (never the window's name, T29), and another process's
  arguments record (`KERN_PROCARGS…`) is named in code only in
  `ScreenSource.swift`, and its lower-case sysctl name on no line. A
  construction of `ScreenSource`, in any spelling the scan
  knows (`ScreenSource(`, `ScreenSource.init`, `.init(permit:`), appears in
  the package's sources only on the one line of `AppDelegate.swift` that
  unwraps `CaptureGate`'s permit, besides the initializer's declaration in
  `ScreenSource.swift`. The decision is read as code with white space
  folded, so spaced and split spellings count, and over every source:
  every reference to `decide` (qualified, through `.self` or a typealias,
  unqualified in an extension, a function reference) is one of two, its
  declaration in Kit, whose body is pinned whole with its one `Permit()`,
  and the statement right before that line, pinned whole to this
  process's own traits (`CommandLine.arguments`, `getppid()`, the process
  environment, `Bundle.main`'s id); `Permit` is named only in Kit's
  `Capture.swift` (its struct declaration, pinned whole with the
  fileprivate initializer, `case on(Permit)` and `decide`'s `Permit()`; no
  `.init` or `Self(` there) and in the initializer of `ScreenSource`, so
  no conformance, typealias or extension makes another permit; `getppid`
  appears only as a call and is never declared with `func`, and
  `CommandLine`, `ProcessInfo` and `Bundle` only before a `.`, so no
  declaration, parameter, closure parameter, pattern or generic
  parameter takes their names; no source sets the environment (`setenv`,
  `putenv`, `unsetenv`, `environ`, `_NSGetEnviron`) or binds a function to
  a symbol name (`@_cdecl`, `@_silgen_name`, `@_dynamicReplacement`,
  `@_extern`); and of the declarations of `captureDecision` the scan
  reads (`let`/`var captureDecision`, `captureDecision:` for an
  annotation or a parameter, `captureDecision in` for a closure parameter
  or a loop variable) there is one, the stored property, with no
  accessors, observers, initial value or attribute, and that statement
  is its only write in the forms the scan counts (`=` other than `==`, also
  after `?` or `!`, a tuple pattern before `=`, `&` inout and key paths; a
  write cannot turn capture on without a permit anyway). So a literal
  reaches the decision in no spelling the scan reads; code that defeats
  the language (an unsafe cast or pointer write, a symbol looked up at
  run time) and code outside the package's Swift sources (a C target, a
  linker setting) are beyond it, and the tests call the pure decision
  freely.
  `ScreenSource.swift` declares, besides its imports, only its class,
  with one initializer, no nested type, nothing at file scope and no
  static or class member but `ownWindowRecord`, whose whole text is
  pinned (its body reads that one window record and nothing else), and no
  extension header in any source names the class and no typealias stands
  for it, so the preflight, the request, the capture and the game read
  need an instance.
  `HoverDriver.swift` ends a screen hover through one
  `HoverReport.afterScreenHover` with the fresh `mayShow()` re-check, then
  `screenHoverEnded`, then `overlay.finish`, and carries out the tracker's
  hideCard only through `overlay.trackerHide()` (no plain hide there), and
  a card clears an earlier card's phrase line in `Overlay.swift` (the
  suite does not link the app, so the calls are pinned). The capture
  file's whole `game(at:)` is pinned (only on-screen windows without the
  desktop, Kit's window rule with the app's Wine check of the owner,
  another process's record read only for Wine, the `dosdevices` link read
  and never followed), and that record's reader, pinned whole and the
  only place naming the record's constant, is named only in its
  declaration, that call and the argument label. `AppDelegate.swift`
  decides the tracking from the speaker editor's state, deciding again
  right after the editor opens and right after the overlay resumes on
  close, closes the editor only there and at the quit, resumes the overlay
  only there and shows a refused ⌃⌥G only as a notice; `HoverDriver.swift`
  hands the editor the shown card's speaker, game, point and token right
  after recording the card; `SpeakerEditorPanel.swift`'s name-field state
  and key path, «Отмена»'s action and the key handler (a cancel only calls
  `onCancel`) and `close()` are pinned whole, and of `close`, `orderOut`,
  `performClose` and `setIsVisible` it names only `close()`'s declaration
  and the one `orderOut` in it (pinned too: the self-test never tracks and
  never types). Event taps, global NSEvent monitors,
  `AXIsProcessTrustedWithOptions`, `IOHIDRequestAccess`,
  `CGRequestListenEventAccess`, `CGRequestPostEventAccess`,
  `IOHIDManagerOpen` and the older capture calls (`CGDisplayCreateImage`,
  `CGDisplayStream`, `AVCaptureScreenInput`) may appear nowhere. The scans
  are pure functions over lines; `theScansFlagEveryForbiddenSpelling` and
  `theImportParserReadsEveryForm` feed them planted lines, at least one per
  name and import form, each checked for the exact issue its own rule
  reports, and every name list is pinned against a second spelling, so a
  scan that stops seeing a spelling fails.
- Keep a generic class such as `Task<T>` at its own type in shared tests:
  declaring it as `Task` or `object`, assigning it to a property or field of
  that type, passing it to a test-assembly method that takes that type, or
  returning a `Task<T>` expression from a lambda makes CsWinRT's AOT
  generator add a vtable entry to the Windows test build, an extra
  `WinRTGlobalVtableLookup.g.cs` line in the identity diff.
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
