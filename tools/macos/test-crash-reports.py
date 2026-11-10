#!/usr/bin/env python3
"""Dependency-free tests for tools/macos/crash-reports.py.

Each case writes synthetic reports into a temporary folder (never the owner's
DiagnosticReports) and runs the tool as a subprocess, the way the G-crash
guard of run-macos-subset.sh, test-swift.sh and build-app.sh runs it.
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

TOOL = Path(__file__).with_name("crash-reports.py")


def ips(proc: str, parent: str, killer: str | None) -> str:
    """An .ips report shaped like ReportCrash's: a header line, then the body."""
    header = {"app_name": proc, "name": proc, "bug_type": "309"}
    data: dict = {"procName": proc, "parentProc": parent, "procPath": f"/private/tmp/*/{proc}"}
    if killer is not None:
        data["termination"] = {"code": 9, "namespace": "SIGNAL", "byProc": killer}
    return json.dumps(header) + "\n" + json.dumps(data, indent=2)


class CrashReportsTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.folder = Path(self.temp.name) / "DiagnosticReports"
        self.folder.mkdir()

    def tearDown(self) -> None:
        self.temp.cleanup()

    def write(self, name: str, text: str) -> None:
        (self.folder / name).write_text(text, encoding="utf-8")

    def run_tool(self, *arguments: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run([sys.executable, "-I", str(TOOL), *arguments], capture_output=True, text=True,
                              check=False)

    def listing(self) -> list[str]:
        result = self.run_tool(str(self.folder))
        self.assertEqual(result.returncode, 0, result.stderr)
        return result.stdout.splitlines()

    def test_names_of_the_checks_processes_count(self) -> None:
        for name in ("AsteriaScale.CoreHost-2026-11-10-010101.ips", "dotnet-2026-11-10-010101.ips",
                     "Python-2026-11-10-010101.ips", "python3-2026-11-10-010101.crash",
                     "swiftpm-testing-helper-2026-11-10-010101.ips"):
            self.write(name, "not json")
        self.assertEqual(len(self.listing()), 5)

    def test_a_child_under_a_system_name_counts_by_its_parent_or_killer(self) -> None:
        # The step-13 regression: a copied /bin/sleep spawned and killed by
        # the Swift suite; ReportCrash names the report after sleep.
        self.write("sleep-2026-11-10-055004.ips", ips("sleep", "swiftpm-testing-helper", "swiftpm-testing-helper"))
        # The parent may be gone before the report is written.
        self.write("sleep-2026-11-10-055244.ips", ips("sleep", "Exited process", "swiftpm-testing-helper"))
        self.write("sh-2026-11-10-010101.ips", ips("sh", "AsteriaScale.CoreHost", None))
        self.assertEqual(self.listing(), ["sh-2026-11-10-010101.ips", "sleep-2026-11-10-055004.ips",
                                          "sleep-2026-11-10-055244.ips"])

    def test_unrelated_reports_never_count(self) -> None:
        self.write("ExcUserFault_nsattributedstringagent-2026-11-09-011643.ips",
                   ips("nsattributedstringagent", "launchd", None))
        self.write("probe-2026-11-10-010751.ips", ips("probe", "zsh", "probe"))
        self.write("sleep-2026-11-10-010101.ips", ips("sleep", "zsh", "kill"))
        # Unparsable and not named after a checked process: counted by name only.
        self.write("sleep-2026-11-10-020202.ips", '{"name":"sleep"}\n{"procName": "sleep", "parentProc": "swift')
        self.write("Safari-2026-11-10.diag", "text")
        retired = self.folder / "Retired"
        retired.mkdir()
        (retired / "AsteriaScale-2026-11-01-010101.ips").write_text("x", encoding="utf-8")
        self.assertEqual(self.listing(), [])

    def test_a_missing_folder_is_empty_and_an_unreadable_one_an_error(self) -> None:
        result = self.run_tool(str(self.folder / "missing"))
        self.assertEqual((result.returncode, result.stdout), (0, ""))
        self.folder.chmod(0)
        try:
            if os.access(self.folder, os.R_OK):
                self.skipTest("running as a user who can read any folder")
            result = self.run_tool(str(self.folder))
            self.assertEqual(result.returncode, 2)
            self.assertEqual(result.stdout, "")
            self.assertIn("cannot list", result.stderr)
        finally:
            self.folder.chmod(0o755)

    def test_usage_error(self) -> None:
        self.assertEqual(self.run_tool().returncode, 2)
        self.assertEqual(self.run_tool("a", "b").returncode, 2)


if __name__ == "__main__":
    unittest.main()
