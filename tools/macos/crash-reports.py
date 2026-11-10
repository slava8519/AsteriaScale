#!/usr/bin/env python3
"""Lists the crash reports in a DiagnosticReports folder that the G-crash
guard attributes to the checks' own processes.

Usage: python3 -I tools/macos/crash-reports.py <folder>

Prints the matching file names, one per line, sorted (bytewise). The folder is
only read. tools/macos/run-macos-subset.sh, tools/macos/test-swift.sh and
tools/macos/build-app.sh call it before and after their runs with the owner's
~/Library/Logs/DiagnosticReports and fail on any name that was not there
before.

A report counts when its file name, or for a JSON .ips report the crashed
process (procName), its parent (parentProc) or the process that killed it
(termination.byProc), starts with AsteriaScale, dotnet, Python, python3 or
swift. The file name alone is not enough: a child the checks start under a
system name (a sleep the Swift suite spawned from swiftpm-testing-helper)
files its report under that name, and the parent or the killer is what ties
it to the run. Reports of unrelated processes (launchd children, other apps)
never count, so a crash elsewhere on the Mac cannot fail a check. A report
that cannot be parsed (another format, still being written) counts by its
file name only. Subfolders such as Retired/ are not read.

Exit codes: 0 the listing was printed (empty for a missing folder: an account
that never crashed anything has none); 2 usage error or an unreadable folder,
never an empty listing.
"""

from __future__ import annotations

import json
import os
import re
import sys

PATTERN = re.compile(r"^(AsteriaScale|dotnet|Python|python3|swift)")


def body(path: str) -> dict | None:
    """The JSON body of an .ips report (a header line, then the body)."""
    try:
        with open(path, encoding="utf-8") as handle:
            handle.readline()
            data = json.loads(handle.read())
    except (OSError, UnicodeDecodeError, ValueError):
        return None
    return data if isinstance(data, dict) else None


def attributed(folder: str, name: str) -> bool:
    if PATTERN.match(name):
        return True
    if not name.endswith(".ips"):
        return False
    data = body(os.path.join(folder, name))
    if data is None:
        return False
    termination = data.get("termination")
    names = [data.get("procName"), data.get("parentProc"),
             termination.get("byProc") if isinstance(termination, dict) else None]
    return any(isinstance(value, str) and PATTERN.match(value) for value in names)


def main(argv: list[str]) -> int:
    if len(argv) != 2:
        print("usage: crash-reports.py <DiagnosticReports folder>", file=sys.stderr)
        return 2
    folder = argv[1]
    if not os.path.lexists(folder):
        return 0
    try:
        names = sorted(os.listdir(folder), key=lambda name: name.encode("utf-8", "surrogateescape"))
    except OSError as error:
        print(f"crash-reports: cannot list {folder}: {error.strerror}", file=sys.stderr)
        return 2
    for name in names:
        if os.path.isfile(os.path.join(folder, name)) and attributed(folder, name):
            print(name)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
