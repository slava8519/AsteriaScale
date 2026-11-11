#!/usr/bin/env python3
"""A fake CoreHost speaking protocol v1, for the AsteriaScaleKit tests.

Not a Core: it answers the v1 envelope, reports what it inherited, and
injects faults on command, so the Swift connection and supervisor can be
tested without .NET. Stdlib only, Python 3.9+ (/usr/bin/python3 of the
Command Line Tools). The real CoreHost is covered by the macOS subset and by
the suite's own launch of the built apphost.

Startup modes (argv[1]):
  normal, crash-on-start, hang-on-hello, bad-protocol, not-leader,
  test-commands, wrong-build, blocked-sigterm, stubborn (ignores SIGTERM and
  core.shutdown), init-fails, golden <transcript> (replays the outbound lines
  of protocol-v1.golden.jsonl after each matching request).

Settings: core.init, settings.get and settings.update keep CoreHost's
settings view and revision (a stale baseRevision answers conflict).

Speakers: a hover whose source names a speakerName answers with a speaker
{name, gender, gameKey?, observedName}, observedName being the spelling sent
and name the one a choice stored for it (in any mode; CoreHost sends a
speaker only for a smart hover); speaker.set {game, observedName, name,
gender} stores the choice under observedName as it is and, when the name
differs, under the name too, like CoreHost.

Apple Vision observations (20.3), in CoreHost's shapes and Russian messages
but without Core: hello lists hoverSources ["selection","observations"].
ocr.select {mode, source} needs no core.init and answers {selection|null,
lines}. ocr.plan {display, cursor} answers not_ready before core.init and
hover_disabled while hoverEnabled is off, then {mode, captureBounds,
observations, cachedFingerprint?} with CoreHost's formulas (900x340 in
dictionary mode, up to 2400x720 in smart mode, the cached frame's bounds
inside their reuse area). An observations hover refuses a missing mode,
a count that does not fit the mode or a malformed fingerprint as
bad_request and answers not_ready before core.init, all at admission (so
none of them supersedes the running hover); then hover_disabled, a mode
other than the active one (bad_request) or zero observations without a
cached frame of that display, bounds and fingerprint (stale_frame) end it
with no event, and so does a selection of nothing (outcome empty).
Otherwise it sends hover.loading before hover.primary with CoreHost's
labels: in dictionary mode the line and «СЛОВАРЬ»; in smart mode the word
under the ranker's «CPU-СМЫСЛ» (the first-run backend), then a card with
provenance heuristic and «SMART-FAST». That card is what CoreHost answers
without a ranker worker for the tests' words in their sentences, not for
every word: Core's heuristic decides per word in its sentence, and where it
leaves the word to the ranker CoreHost answers rankerUnavailable («APPLE
VISION  •  CPU-СМЫСЛ  •  НЕДОСТУПЕН», with senseProvider), which the fake
never sends, as for "of" in "He sat on the bank of the river." and for
"bank" itself in the clipped "He sat on the bank of the". A card's source
is the hovered word without edge punctuation ("bank" for "bank." and
"'bank'"), where CoreHost names what its dictionary looked up (which can be a
phrase); cards carry language "EN", as CoreHost's do, but not the
confidence CoreHost puts on a heuristic card (0.95 for "bank", 1 for "the"
there).

Selection takes OcrGeometry.SelectNearPoint's line and word: lines with a
letter (degenerate boxes and empty lines dropped, as CoreHost's decoder
does), the one whose box is nearest the cursor by dx² + 1.6·dy², nothing
beyond 95 px, and in it the word box nearest the cursor, whose text with its
whitespace collapsed is hoveredWord (edge punctuation kept: "bank.",
"'bank'"). Core's sentence assembly is not mirrored: text is the whole line,
where Core takes the sentence around the word joined with the neighbouring
lines of its block and moves a "Name: " prefix or a nameplate into the
speaker, so the fake's observations never yield a speakerName or a speaker,
and two lines that differ only in another sentence diverge here but agree
in CoreHost; characterIndex is where the word first occurs in the line.

Smart mode agrees two selections by Core's StabilizeSentenceContext: the
same hovered word under NormalizeEnglish and one token sequence inside the
other in order, else observation 1, unstable; when they agree it takes
observation 2 exactly when Core does (its tokens hold the first's, and the
first's do not hold its own or its text is longer), stable when that text
looks like a complete sentence (LooksLikeCompleteOcrSentence, approximated
as looks_complete says). A stable hover caches the chosen observation (its
frame and fingerprint) per display for 30 s, as CoreHost does, which
ocr.plan then names and a hover with zero observations, or with that
fingerprint first, selects in; one observation in smart ocr.select is never
stable. Only the count, mode and fingerprint rules are checked; the C#
CoreHost checks pin the rest.

An observations hover whose card would be "slow" waits up to 10 s for
request.cancel (or a newer hover) before any event, as the selection hover
on "slow" below does, so a client can be cancelled while hover.resolve is
in flight. test.delayNextWork {ms} holds back the next ocr.plan or
ocr.select, as CoreHost's test command holds back its next work-lane
request (the fake's settings.update is not held, and an ocr.plan before
core.init is refused at once, never held): it starts that much later, and
request.cancel meanwhile answers it at once with cancelled
(CoreHost's CancelMode.Immediate), dropping the late work. Every
request.cancel is noted on stderr as "[fake] request.cancel target <id>:
cancelled" or ": not_in_flight", so a client test can tell which request
it reached.

A selection hover answers hover.primary and then a hover.phrase of the
fake's own, which exercises the phrase decoding (CoreHost sends no
hover.phrase before 20.5). It never gets hover.loading, except for
hoveredWord "loading", which sends it first so that a client can prove it
ignores an event it does not use.

Fault commands: test.sleep {ms}, test.delayNextWork {ms}, test.events {count}, test.unsolicited,
test.violate, test.unknownId, test.duplicate, test.eventAfterTerminal,
test.unknownMember, test.badVersion, test.crash, test.hang,
test.stderrFlood {bytes}, test.spawnGrandchild {inherit?}.

Every /bin/sleep it starts runs for 3141.59 s followed by the run's token
(-X asteria_run=<ten digits>, which the suite passes): tools/macos/test-swift.sh
finds this run's leftovers by that marker.
"""
import json
import os
import re
import signal
import subprocess
import sys
import threading
import time
import unicodedata

MODE = sys.argv[1] if len(sys.argv) > 1 else "normal"
RUN_TOKEN = sys._xoptions.get("asteria_run", "")
SLEEP_MARKER = "3141.59" + (RUN_TOKEN if len(RUN_TOKEN) == 10 and RUN_TOKEN.isdigit() else "")
WATCHED_SIGNALS = [("SIGHUP", signal.SIGHUP), ("SIGINT", signal.SIGINT),
                   ("SIGTERM", signal.SIGTERM), ("SIGCHLD", signal.SIGCHLD)]

out_lock = threading.Lock()
pending = {}            # request id -> threading.Event (set = cancel)
hover_lock = threading.Lock()
current_hover = None    # dict: id, cancel, done, superseded_by
shown_hovers = set()
started = time.time()
shutdown_answered = False
settings_lock = threading.Lock()
speakers = {}           # (game key, spelling) casefolded -> (display name, gender)
settings = {"hoverEnabled": False, "hoverDelayMs": 700, "mode": "smart",
            "sense": {"backend": "cpu", "model": "consec", "sourceLanguage": "auto", "keepWarm": True},
            "phrase": {"mode": "off", "backend": "cpu"}}
revision = 1
initialized = False
frame_lock = threading.Lock()
held_lock = threading.Lock()
held = {}               # request id -> answered: work held back by test.delayNextWork
next_work_delay_ms = 0
frame_cache = None      # dict: display, bounds (x, y, w, h), fingerprint, created, frame
FRAME_CACHE_SECONDS = 30
DICTIONARY_CAPTURE = (900, 340)     # CoreHost's ObservationRules (WindowsAiOcrService)
SENTENCE_CAPTURE = (2400, 720)      # Core's OcrGeometry
HEX_DIGITS = set("0123456789abcdef")
# HoverEngineLabels.AppleVision. A smart hover loads under the ranker's label
# (HoverCoordinator: the first-run CPU backend), but its card names what
# actually decided, which for the tests' words without a ranker worker is the
# heuristic.
LOADING_LABELS = {"dictionary": "APPLE VISION  •  СЛОВАРЬ", "smart": "APPLE VISION  •  CPU-СМЫСЛ"}
PRIMARY = {"dictionary": ("dictionary", "APPLE VISION  •  СЛОВАРЬ"),
           "smart": ("heuristic", "APPLE VISION  •  SMART-FAST")}
# SentenceComparison's TokenRegex: [^\W_] stands in for [\p{L}\p{N}], which
# re cannot spell; combining marks are not covered, NFC composes most first.
TOKEN = re.compile(r"[^\W_]+(?:['’\-][^\W_]+)*")
SENTENCE_END = ".!?…。！？"
CLOSING = "\"'’”»」』】)]}"
OPENING = "\"'‘“«([{-—.…"
BAD_REQUEST = "Некорректный запрос к службе перевода."
NOT_READY = "Служба перевода ещё не готова."
HOVER_DISABLED = "Перевод текста выключен — включите его в меню."
STALE_FRAME = "Кадр на экране изменился."
# Edge marks a card's source drops, standing in for the dictionary lookup that
# names the word on CoreHost's card ("bank" for a box "bank." or "'bank'").
CARD_TRIM = ".,!?;:\"'()[]“”‘’"


def emit_raw(data):
    with out_lock:
        sys.stdout.buffer.write(data)
        sys.stdout.buffer.flush()


def emit(obj):
    emit_raw((json.dumps(obj, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8"))


def ok(rid, result=None):
    emit({"version": 1, "id": rid, "ok": True, "result": result if result is not None else {}})


def err(rid, code, message, detail=None, **extra):
    body = {"code": code, "message": message}
    if detail is not None:
        body["detail"] = detail
    body.update(extra)
    emit({"version": 1, "id": rid, "ok": False, "error": body})


def event(name, ref, payload):
    frame = {"version": 1, "event": name, "payload": payload}
    if ref is not None:
        frame["ref"] = ref
    emit(frame)


def blocked_signal_names():
    mask = signal.pthread_sigmask(signal.SIG_BLOCK, [])
    return [name for name, number in WATCHED_SIGNALS if number in mask]


def hello(rid, payload):
    if MODE == "hang-on-hello":
        time.sleep(3600)
    pid = os.getpid()
    pgid = os.getpgid(0)
    blocked = blocked_signal_names()
    if MODE == "blocked-sigterm" and "SIGTERM" not in blocked:
        blocked.append("SIGTERM")
    mask = signal.pthread_sigmask(signal.SIG_BLOCK, [])
    ok(rid, {
        "protocol": 99 if MODE == "bad-protocol" else 1,
        "coreHostVersion": "0.0.0", "coreVersion": "0.0.0",
        "build": "other-build" if MODE == "wrong-build" else "fake-build",
        "runtime": "python", "aot": False,
        "pid": pid, "pgid": pgid,
        "processGroupLeader": False if MODE == "not-leader" else pgid == pid,
        "platform": "macos",
        "testCommands": MODE == "test-commands",
        "blockedSignals": blocked,
        "paths": {"dataRoot": "/fake", "userData": "/fake", "logDir": "/fake"},
        "capabilities": {"hoverModes": ["dictionary", "smart"], "hoverSources": ["selection", "observations"]},
        # Fake-only facts about what the process inherited.
        "client": payload.get("client"),
        "cwd": os.getcwd(),
        "home": os.environ.get("HOME"),
        "tmpdir": os.environ.get("TMPDIR"),
        "fds": sorted(int(name) for name in os.listdir("/dev/fd") if name.isdigit()),
        "blockedSignalNumbers": sorted(int(number) for number in mask),
    })


def sleeper(rid, ms):
    cancel = pending.setdefault(rid, threading.Event())
    if cancel.wait(ms / 1000.0):
        err(rid, "cancelled", "Запрос отменён.")
    else:
        ok(rid, {"slept": ms})
    pending.pop(rid, None)


def start_hover(rid, payload):
    global current_hover
    me = {"id": rid, "cancel": threading.Event(), "done": threading.Event(), "superseded_by": None}
    pending[rid] = me["cancel"]
    with hover_lock:
        previous = current_hover
        current_hover = me
    if previous is not None:
        previous["superseded_by"] = rid
        previous["cancel"].set()
    threading.Thread(target=run_hover, args=(me, previous, payload), daemon=True).start()


def run_hover(me, previous, payload):
    rid = me["id"]
    try:
        if previous is not None:
            # G3: the replaced hover's terminal precedes this hover's events.
            previous["done"].wait()
        source = payload.get("source") or {}
        if source.get("kind") == "observations":
            run_observations_hover(me, payload.get("mode"), source)
            return
        word = source.get("hoveredWord", "")
        if word == "disabled":
            err(rid, "hover_disabled", "Перевод текста выключен — включите его в меню.", "hover translation is off")
            return
        if word == "slow" and me["cancel"].wait(10):
            if me["superseded_by"] is not None:
                err(rid, "superseded", "Запрос заменён новым наведением.", supersededBy=me["superseded_by"])
            else:
                err(rid, "cancelled", "Запрос отменён.")
            return
        if word == "fail":
            event("status", None, {"area": "hover", "state": "error", "code": "hover_failed",
                                   "text": "Не удалось перевести текст под курсором."})
            ok(rid, {"outcome": "failed", "timings": {"queuedMs": 0}})
            return
        if word == "nothing":
            ok(rid, {"outcome": "empty", "timings": {"queuedMs": 0}})
            return
        if word == "loading":
            event("hover.loading", rid, {"source": word, "engineLabel": LOADING_LABELS["dictionary"]})
        shown_hovers.add(rid)
        primary = {
            "source": word, "translation": "перевод: " + word, "language": "EN", "isPhrase": False,
            "found": True, "provenance": "dictionary", "engineLabel": "APPLE VISION  •  СЛОВАРЬ",
            "context": source.get("text"), "characterIndex": source.get("characterIndex")}
        if source.get("speakerName"):
            primary["speaker"] = speaker_view((payload.get("game") or {}).get("key"), source["speakerName"])
        event("hover.primary", rid, primary)
        event("hover.phrase", rid, {"source": source.get("text", ""), "translation": "черновик", "status": "translated",
                                    "provisional": True, "fromCache": False,
                                    "model": {"family": "test", "variant": "fake", "backend": "cpu", "identity": "fake"}})
        ok(rid, {"outcome": "completed", "timings": {"queuedMs": 0, "primaryMs": 0.1}})
    finally:
        pending.pop(rid, None)
        me["done"].set()


def is_fingerprint(text):
    return isinstance(text, str) and len(text) == 16 and set(text) <= HEX_DIGITS


def has_shape(command, payload, plan=False):
    """The members the fake reads, with their JSON types (CoreHost's strict types refuse more)."""
    def integers(value, names):
        return isinstance(value, dict) and all(type(value.get(name)) is int for name in names)
    if plan:
        return integers(payload.get("display"), ("id", "width", "height")) and integers(payload.get("cursor"), ("x", "y"))
    source = payload.get("source")
    if not (isinstance(source, dict) and integers(source.get("display"), ("id", "width", "height"))
            and integers(source.get("captureBounds"), ("x", "y", "width", "height"))
            and integers(source.get("cursor"), ("x", "y")) and isinstance(source.get("observations"), list)):
        return False
    for frame in source["observations"]:
        if not (isinstance(frame, dict) and isinstance(frame.get("lines"), list)):
            return False
        for line in frame["lines"]:
            if not (isinstance(line, dict) and isinstance(line.get("text"), str) and isinstance(line.get("words"), list)):
                return False
            for word in line["words"]:
                if not (isinstance(word, dict) and isinstance(word.get("text"), str) and all(
                        type(word.get(edge)) in (int, float) for edge in ("left", "top", "right", "bottom"))):
                    return False
    return True


def schema_error(rid, command):
    err(rid, "bad_request", BAD_REQUEST,
        "the payload of %s does not match its schema (unknown, missing, null or mistyped member)" % command)


def observations_problem(mode, source, for_select):
    """CoreHost's count, mode and fingerprint rules (ObservationRules), with its details."""
    if mode not in ("dictionary", "smart"):
        return ("mode is required with an observations source" if mode is None
                else "mode must be dictionary or smart, got '%s'" % mode)
    if source.get("kind") != "observations":
        return "source.kind must be observations, got '%s'" % source.get("kind")
    observations = source.get("observations")
    if not isinstance(observations, list):
        return "source.observations is missing"
    count = len(observations)
    cached = source.get("cachedFingerprint")
    if for_select:
        if cached is not None:
            return "ocr.select keeps no frame cache and takes no source.cachedFingerprint"
        if count != 1 if mode == "dictionary" else not 1 <= count <= 2:
            return "ocr.select takes 1 observation in dictionary mode and 1 or 2 in smart mode, got %d" % count
    elif mode == "dictionary":
        if count != 1 or cached is not None:
            return "dictionary mode takes exactly 1 observation and no source.cachedFingerprint, got %d" % count
    elif cached is None if count == 0 else count != 2 or cached is not None:
        return ("smart mode takes 2 observations, or 0 together with source.cachedFingerprint "
                "(and the fingerprint only then), got %d" % count)
    if cached is not None and not is_fingerprint(cached):
        return "source.cachedFingerprint must be 16 lowercase hex digits"
    for index, frame in enumerate(observations):
        if not is_fingerprint(frame.get("fingerprint")):
            return "source.observations[%d]: fingerprint must be 16 lowercase hex digits" % index
    return None


def rectangle(value):
    return (int(value["x"]), int(value["y"]), int(value["width"]), int(value["height"]))


def capture_bounds(display, cursor, requested_width, requested_height):
    """OcrGeometry.CalculateCaptureBounds in display pixels."""
    width = min(requested_width, display["width"])
    height = min(requested_height, display["height"])
    left = min(max(cursor["x"] - width // 2, 0), display["width"] - width)
    top = min(max(cursor["y"] - height // 2, 0), display["height"] - height)
    return (left, top, width, height)


def inside_reuse_area(bounds, cursor):
    """OcrGeometry.IsInsideReuseArea."""
    x, y, width, height = bounds
    horizontal = min(120, max(0, width // 6))
    vertical = min(80, max(0, height // 6))
    return x + horizontal <= cursor["x"] < x + width - horizontal and y + vertical <= cursor["y"] < y + height - vertical


def cached_frame(display_id, bounds, fingerprint):
    with frame_lock:
        entry = frame_cache
    if (entry is not None and entry["display"] == display_id and entry["bounds"] == bounds
            and entry["fingerprint"] == fingerprint and time.monotonic() - entry["created"] <= FRAME_CACHE_SECONDS):
        return entry
    return None


def decoded_lines(frame):
    """The lines a selection may come from: words with a real box, non-empty text."""
    lines = []
    for line in frame.get("lines", []):
        text = " ".join(line.get("text", "").split())
        words = [word for word in line.get("words", [])
                 if word["right"] > word["left"] and word["bottom"] > word["top"]]
        if text and words:
            lines.append((text, words))
    return lines


def select_near_cursor(frame, source):
    """OcrGeometry.SelectNearPoint's line and word, in frame pixels, without its
    sentence assembly. The word keeps its punctuation because Core compares
    words under NormalizeEnglish, which trims some edge marks and not others
    (a quote), so stripping them here would agree where CoreHost does not."""
    x = source["cursor"]["x"] - source["captureBounds"]["x"]
    y = source["cursor"]["y"] - source["captureBounds"]["y"]
    best = None
    for text, words in decoded_lines(frame):
        if not any(character.isalpha() for character in text):
            continue
        closest = None
        for word in words:
            dx = max(word["left"] - x, 0, x - word["right"])
            dy = max(word["top"] - y, 0, y - word["bottom"])
            if closest is None or dx * dx + dy * dy < closest[0]:
                closest = (dx * dx + dy * dy, " ".join(word["text"].split()))
        dx = max(min(word["left"] for word in words) - x, 0, x - max(word["right"] for word in words))
        dy = max(min(word["top"] for word in words) - y, 0, y - max(word["bottom"] for word in words))
        distance = dx * dx + dy * dy * 1.6
        if best is None or distance < best[0]:
            best = (distance, closest[1], text)
    if best is None or best[0] > 95 * 95:
        return None
    _, word, text = best
    return {"text": text, "hoveredWord": word, "characterIndex": max(text.find(word), 0), "sentenceStable": True}


def normalize_english(value):
    """LocalDictionaryService.NormalizeEnglish; casefold stands in for FoldEnglishCase."""
    collapsed = " ".join(unicodedata.normalize("NFC", value or "").split()).replace("’", "'")
    return collapsed.strip(" .,!?:;\"“”()[]").casefold()


def tokens(text):
    """SentenceComparison.Tokens."""
    return [normalize_english(match.group()) for match in TOKEN.finditer(unicodedata.normalize("NFC", text or ""))]


def is_ordered_subsequence(candidate, sequence):
    """SentenceComparison.IsOrderedSubsequence."""
    if len(candidate) > len(sequence):
        return False
    found = 0
    for token in sequence:
        if found < len(candidate) and candidate[found] == token:
            found += 1
    return found == len(candidate)


def utf16_length(text):
    return len(text.encode("utf-16-le")) // 2


def looks_complete(text):
    """OcrTextRules.LooksLikeCompleteOcrSentence without its speaker-name prefix, its
    unmatched-quote strip and Normalize's script repair: sentence-final punctuation
    (closing quotes and brackets skipped), then after any opening quote, bracket,
    dash or ellipsis a digit, a capital or a non-ASCII letter."""
    value = " ".join(text.split())
    end = len(value) - 1
    while end >= 0 and value[end] in CLOSING:
        end -= 1
    if end < 0 or value[end] not in SENTENCE_END:
        return False
    start = 0
    while start <= end and (value[start].isspace() or value[start] in OPENING):
        start += 1
    if start > end:
        return False
    first = value[start]
    return first.isdecimal() or first.isupper() or (first.isalpha() and not "a" <= first <= "z")


def stabilize(first, second):
    """OcrTextRules.StabilizeSentenceContext on the fake's selections: the same
    hovered word and one token sequence inside the other in order, else the
    first, unstable. When they agree, the second is chosen when it holds the
    first and the first does not hold it or it is longer; the choice is stable
    when it looks like a complete sentence. Returns (selection, second chosen)."""
    if first is None:
        return (None if second is None else dict(second, sentenceStable=False)), True
    if second is None or normalize_english(first["hoveredWord"]) != normalize_english(second["hoveredWord"]):
        return dict(first, sentenceStable=False), False
    first_tokens, second_tokens = tokens(first["text"]), tokens(second["text"])
    if not first_tokens or not second_tokens:
        return dict(first, sentenceStable=False), False
    first_fits = is_ordered_subsequence(first_tokens, second_tokens)
    second_fits = is_ordered_subsequence(second_tokens, first_tokens)
    if not first_fits and not second_fits:
        return dict(first, sentenceStable=False), False
    use_second = first_fits and (not second_fits or utf16_length(second["text"]) > utf16_length(first["text"]))
    chosen = second if use_second else first
    return dict(chosen, sentenceStable=looks_complete(chosen["text"])), use_second


def ocr_select(rid, payload):
    source = payload.get("source") or {}
    problem = observations_problem(payload.get("mode"), source, for_select=True)
    if problem:
        err(rid, "bad_request", BAD_REQUEST, problem)
        return
    observations = source["observations"]
    first = select_near_cursor(observations[0], source)
    if payload["mode"] == "smart":
        second = select_near_cursor(observations[1], source) if len(observations) > 1 else None
        first, _ = stabilize(first, second)
    ok(rid, {"selection": first, "lines": [text for text, _ in decoded_lines(observations[0])]})


def ocr_plan(rid, payload):
    if not initialized:
        err(rid, "not_ready", NOT_READY, "core.init has not completed")
        return
    with settings_lock:
        enabled, mode = settings["hoverEnabled"], settings["mode"]
    if not enabled:
        err(rid, "hover_disabled", HOVER_DISABLED, "hover translation is off")
        return
    display, cursor = payload["display"], payload["cursor"]
    cached = None
    if mode == "dictionary":
        bounds = capture_bounds(display, cursor, *DICTIONARY_CAPTURE)
    else:
        with frame_lock:
            entry = frame_cache
        same_display = entry is not None and entry["display"] == display["id"]
        # OcrGeometry.CalculateSentenceCaptureBounds: the display must hold the old bounds.
        if (same_display and inside_reuse_area(entry["bounds"], cursor)
                and 0 <= entry["bounds"][0] and entry["bounds"][0] + entry["bounds"][2] <= display["width"]
                and 0 <= entry["bounds"][1] and entry["bounds"][1] + entry["bounds"][3] <= display["height"]):
            bounds = entry["bounds"]
        else:
            bounds = capture_bounds(display, cursor, *SENTENCE_CAPTURE)
        if same_display and cached_frame(display["id"], bounds, entry["fingerprint"]) is not None:
            cached = entry["fingerprint"]
    result = {"mode": mode, "captureBounds": dict(zip(("x", "y", "width", "height"), bounds)),
              "observations": 1 if mode == "dictionary" else 2}
    if cached is not None:
        result["cachedFingerprint"] = cached
    ok(rid, result)


def run_observations_hover(me, mode, source):
    """After admission: CoreSession.ResolveAsync's refusals, then the cards."""
    global frame_cache
    rid = me["id"]
    with settings_lock:
        enabled, active = settings["hoverEnabled"], settings["mode"]
    if not enabled:
        err(rid, "hover_disabled", HOVER_DISABLED, "hover translation is off")
        return
    if mode != active:
        err(rid, "bad_request", BAD_REQUEST,
            "hover.resolve asked for mode %s, the active mode is %s; change it with settings.update" % (mode, active))
        return
    display_id = source["display"]["id"]
    bounds = rectangle(source["captureBounds"])
    observations = source["observations"]
    if not observations:
        entry = cached_frame(display_id, bounds, source["cachedFingerprint"])
        if entry is None:
            err(rid, "stale_frame", STALE_FRAME,
                "no cached frame for display %d, bounds (%d, %d, %dx%d) and fingerprint %s"
                % ((display_id,) + bounds + (source["cachedFingerprint"],)))
            return
        selection = select_near_cursor(entry["frame"], source)
    elif mode == "dictionary":
        selection = select_near_cursor(observations[0], source)
    else:
        entry = cached_frame(display_id, bounds, observations[0]["fingerprint"])
        if entry is not None:
            selection = select_near_cursor(entry["frame"], source)
        else:
            selection, use_second = stabilize(select_near_cursor(observations[0], source),
                                              select_near_cursor(observations[1], source))
            if selection is not None and selection["sentenceStable"]:
                # CoreHost stores the observation Core chose, as Windows
                # stores selectedFingerprint and selectedRecognition.
                chosen = observations[1 if use_second else 0]
                with frame_lock:
                    frame_cache = {"display": display_id, "bounds": bounds, "frame": chosen,
                                   "fingerprint": chosen["fingerprint"], "created": time.monotonic()}
    if selection is None:
        ok(rid, {"outcome": "empty", "timings": {"queuedMs": 0}})
        return
    word = selection["hoveredWord"]
    # The card names what the dictionary looked up, not the box's text.
    card = word.strip(CARD_TRIM) or word
    if card == "slow" and me["cancel"].wait(10):
        if me["superseded_by"] is not None:
            err(rid, "superseded", "Запрос заменён новым наведением.", supersededBy=me["superseded_by"])
        else:
            err(rid, "cancelled", "Запрос отменён.")
        return
    provenance, label = PRIMARY[mode]
    event("hover.loading", rid, {"source": word if mode == "smart" else selection["text"],
                                 "engineLabel": LOADING_LABELS[mode]})
    shown_hovers.add(rid)
    event("hover.primary", rid, {
        "source": card, "translation": "перевод: " + card, "language": "EN", "isPhrase": False, "found": True,
        "provenance": provenance, "engineLabel": label, "context": selection["text"]})
    ok(rid, {"outcome": "completed", "timings": {"queuedMs": 0, "primaryMs": 0.1}})


def note_cancel(target, cancelled):
    sys.stderr.write("[fake] request.cancel target %s: %s\n" % (target, "cancelled" if cancelled else "not_in_flight"))
    sys.stderr.flush()


def cancel_request(rid, payload):
    target = payload.get("target")
    with held_lock:
        waiting = held.get(target) is False
        if waiting:
            held[target] = True
    if waiting:
        # CancelMode.Immediate: the target's terminal goes first.
        err(target, "cancelled", "Запрос отменён.")
        note_cancel(target, True)
        ok(rid, {"cancelled": True})
        return
    cancel = pending.get(target)
    if cancel is not None and not cancel.is_set():
        cancel.set()
        note_cancel(target, True)
        ok(rid, {"cancelled": True})
    else:
        note_cancel(target, False)
        ok(rid, {"cancelled": False, "reason": "not_in_flight"})


def hold_back(rid, payload, work):
    """test.delayNextWork: this work-lane request starts that much later."""
    global next_work_delay_ms
    with held_lock:
        ms, next_work_delay_ms = next_work_delay_ms, 0
        if ms > 0:
            held[rid] = False
    if ms <= 0:
        work(rid, payload)
        return

    def later():
        time.sleep(ms / 1000.0)
        with held_lock:
            answered = held.pop(rid, True)
        if not answered:
            work(rid, payload)

    threading.Thread(target=later, daemon=True).start()


def settings_result():
    return {"settings": json.loads(json.dumps(settings)), "revision": revision}


def update_settings(rid, payload):
    global revision
    with settings_lock:
        if payload.get("baseRevision") != revision:
            err(rid, "conflict", "Настройки перевода изменились; повторите.", "revision is %d" % revision)
            return
        patch = payload.get("patch") or {}
        for key, value in patch.items():
            if isinstance(value, dict):
                settings[key].update(value)
            else:
                settings[key] = value
        revision += 1
        ok(rid, settings_result())


def speaker_view(game_key, observed):
    with settings_lock:
        name, gender = speakers.get(((game_key or "").casefold(), observed.casefold()), (observed, "unknown"))
    view = {"name": name, "gender": gender}
    if game_key:
        view["gameKey"] = game_key
    view["observedName"] = observed
    return view


def set_speaker(rid, payload):
    game = payload.get("game") or {}
    observed = payload.get("observedName") or ""
    name = payload.get("name") or ""
    gender = payload.get("gender")
    if not game.get("key") or not observed or not name or gender not in ("masculine", "feminine", "ignore"):
        err(rid, "bad_request", "Некорректный запрос к службе перевода.", "speaker.set needs game, both names and a gender")
        return
    key = game["key"].casefold()
    with settings_lock:
        speakers[(key, observed.casefold())] = (name, gender)
        if name.casefold() != observed.casefold():
            speakers[(key, name.casefold())] = (name, gender)
    ok(rid, {"speaker": speaker_view(game["key"], observed)})


def admit_hover(rid, payload):
    source = payload.get("source")
    if isinstance(source, dict) and source.get("kind") == "observations":
        if not has_shape("hover.resolve", payload):
            schema_error(rid, "hover.resolve")
            return
        problem = observations_problem(payload.get("mode"), source, for_select=False)
        if problem:
            err(rid, "bad_request", BAD_REQUEST, problem)
            return
        if not initialized:
            err(rid, "not_ready", NOT_READY, "the hover engine is not ready")
            return
    start_hover(rid, payload)


def handle(frame):
    global shutdown_answered, initialized, next_work_delay_ms
    rid = frame.get("id")
    command = frame.get("command")
    payload = frame.get("payload") or {}
    if frame.get("version") != 1 or not isinstance(rid, int) or set(frame) - {"version", "id", "command", "payload"}:
        sys.stderr.write("[fake] bad envelope\n")
        sys.stderr.flush()
        os._exit(3)
    if command == "core.hello":
        hello(rid, payload)
    elif command == "core.ping":
        ok(rid, {"uptimeMs": (time.time() - started) * 1000, "outstanding": len(pending)})
    elif command == "core.shutdown":
        if MODE == "stubborn":
            return
        ok(rid)
        shutdown_answered = True
    elif command == "core.init":
        if MODE == "init-fails":
            err(rid, "dictionary_missing", "Не найдены словари перевода.", "no eng-rus*.tsv.gz in /fake")
        else:
            result = settings_result()
            result.update({"dictionary": {"entries": 3, "sources": 1, "version": "fake"}, "morphologyEntries": 1,
                           "loadMs": 1, "warmupLookupMs": 1})
            initialized = True
            ok(rid, result)
    elif command == "request.cancel":
        cancel_request(rid, payload)
    elif command == "hover.resolve":
        admit_hover(rid, payload)
    elif command == "ocr.select":
        if has_shape(command, payload):
            hold_back(rid, payload, ocr_select)
        else:
            schema_error(rid, command)
    elif command == "ocr.plan":
        if not has_shape(command, payload, plan=True):
            schema_error(rid, command)
        elif not initialized:
            err(rid, "not_ready", NOT_READY, "core.init has not completed")
        else:
            hold_back(rid, payload, ocr_plan)
    elif command == "test.delayNextWork":
        ms = payload.get("ms")
        if isinstance(ms, bool) or not isinstance(ms, int) or not 0 <= ms <= 600000:
            err(rid, "bad_request", BAD_REQUEST, "test.delayNextWork takes ms 0..600000")
        else:
            with held_lock:
                next_work_delay_ms = ms
            ok(rid, {})
    elif command == "hover.displayed":
        ok(rid, {"recorded": payload.get("hoverId") in shown_hovers})
    elif command == "settings.get":
        with settings_lock:
            ok(rid, settings_result())
    elif command == "settings.update":
        update_settings(rid, payload)
    elif command == "speaker.set":
        set_speaker(rid, payload)
    elif command == "test.sleep":
        pending[rid] = threading.Event()
        threading.Thread(target=sleeper, args=(rid, payload.get("ms", 1000)), daemon=True).start()
    elif command == "test.events":
        for index in range(payload.get("count", 3)):
            event("test.tick", rid, {"i": index})
        ok(rid, {"count": payload.get("count", 3)})
    elif command == "test.unsolicited":
        event("status", None, {"area": "translation", "text": "Словари загружены"})
        ok(rid)
    elif command == "test.violate":
        emit_raw(b"this is not json\n")
    elif command == "test.unknownId":
        ok(rid + 100000)
    elif command == "test.duplicate":
        ok(rid)
        ok(rid)
    elif command == "test.eventAfterTerminal":
        ok(rid)
        event("test.late", rid, {})
    elif command == "test.unknownMember":
        emit({"version": 1, "id": rid, "ok": True, "result": {}, "extra": 1})
    elif command == "test.badVersion":
        emit({"version": 2, "id": rid, "ok": True, "result": {}})
    elif command == "test.crash":
        os._exit(70)
    elif command == "test.hang":
        time.sleep(3600)
    elif command == "test.stderrFlood":
        remaining = payload.get("bytes", 1 << 20)
        chunk = b"x" * 1023 + b"\n"
        while remaining > 0:
            sys.stderr.buffer.write(chunk[:min(remaining, len(chunk))])
            remaining -= len(chunk)
        sys.stderr.buffer.flush()
        ok(rid, {"flooded": payload.get("bytes")})
    elif command == "test.spawnGrandchild":
        # With inherit the grandchild keeps the fake's stdout and stderr
        # open, so the client sees no EOF when only the fake dies.
        if payload.get("inherit"):
            child = subprocess.Popen(["/bin/sleep", SLEEP_MARKER], stdin=subprocess.DEVNULL)
        else:
            child = subprocess.Popen(["/bin/sleep", SLEEP_MARKER], stdin=subprocess.DEVNULL,
                                     stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        ok(rid, {"pid": child.pid, "pgid": os.getpgid(child.pid)})
    else:
        err(rid, "unknown_command", "Служба перевода не знает такой команды.", command)


def replay_golden(path):
    """Answers each request with the golden's outbound lines up to the next request."""
    with open(path, "rb") as transcript:
        lines = [line for line in transcript.read().split(b"\n") if line]
    steps = []
    for line in lines:
        frame = json.loads(line)
        if "command" in frame:
            steps.append((frame, []))
        else:
            steps[-1][1].append(line + b"\n")
    for expected, answers in steps:
        raw = sys.stdin.buffer.readline()
        if not raw:
            sys.stderr.write("[fake] EOF before the golden ended\n")
            os._exit(4)
        received = json.loads(raw)
        if received != expected:
            sys.stderr.write("[fake] golden mismatch: got %r, expected %r\n" % (received, expected))
            sys.stderr.flush()
            os._exit(3)
        for answer in answers:
            emit_raw(answer)
    while sys.stdin.buffer.readline():
        pass
    os._exit(0)


def main():
    if MODE == "crash-on-start":
        sys.stderr.write("[fake] exiting on start\n")
        os._exit(71)
    if MODE == "stubborn":
        signal.signal(signal.SIGTERM, signal.SIG_IGN)
    if MODE == "golden":
        replay_golden(sys.argv[2])
    for raw in sys.stdin.buffer:
        try:
            frame = json.loads(raw)
        except ValueError:
            sys.stderr.write("[fake] not a JSON request\n")
            os._exit(3)
        handle(frame)
    # stdin EOF: exit 0 after an answered core.shutdown, 4 otherwise, like CoreHost.
    os._exit(0 if shutdown_answered else 4)


main()
