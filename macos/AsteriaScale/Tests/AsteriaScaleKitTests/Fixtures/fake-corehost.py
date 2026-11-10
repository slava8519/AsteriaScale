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

Fault commands: test.sleep {ms}, test.events {count}, test.unsolicited,
test.violate, test.unknownId, test.duplicate, test.eventAfterTerminal,
test.unknownMember, test.badVersion, test.crash, test.hang,
test.stderrFlood {bytes}, test.spawnGrandchild {inherit?}.

Every /bin/sleep it starts runs for 3141.59 s followed by the run's token
(-X asteria_run=<ten digits>, which the suite passes): tools/macos/test-swift.sh
finds this run's leftovers by that marker.
"""
import json
import os
import signal
import subprocess
import sys
import threading
import time

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
        "capabilities": {"hoverModes": ["dictionary", "smart"], "hoverSources": ["selection"]},
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
        shown_hovers.add(rid)
        primary = {
            "source": word, "translation": "перевод: " + word, "language": "en", "isPhrase": False,
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


def cancel_request(rid, payload):
    target = payload.get("target")
    cancel = pending.get(target)
    if cancel is not None and not cancel.is_set():
        cancel.set()
        ok(rid, {"cancelled": True})
    else:
        ok(rid, {"cancelled": False, "reason": "not_in_flight"})


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


def handle(frame):
    global shutdown_answered
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
            ok(rid, result)
    elif command == "request.cancel":
        cancel_request(rid, payload)
    elif command == "hover.resolve":
        start_hover(rid, payload)
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
