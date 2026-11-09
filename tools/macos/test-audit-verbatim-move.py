#!/usr/bin/env python3
"""Dependency-free tests for tools/macos/audit-verbatim-move.py.

Each case builds a throwaway git repository in a temporary folder, commits a
base, edits the working tree and runs the audit as a subprocess, the way a
Track III commit runs it. Git runs without the user's global and system
configuration (no signing hooks or templates reach it) and with HOME in the
temporary folder.
"""

from __future__ import annotations

import importlib.util
import os
import re
import subprocess
import sys
import tempfile
import textwrap
import unittest
from pathlib import Path

MODULE_PATH = Path(__file__).with_name("audit-verbatim-move.py")
# Importing the tool would otherwise leave tools/macos/__pycache__ in the clone.
sys.dont_write_bytecode = True
SPEC = importlib.util.spec_from_file_location("audit_verbatim_move", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
audit = importlib.util.module_from_spec(SPEC)
# dataclasses resolve the module's postponed annotations through sys.modules.
sys.modules[SPEC.name] = audit
SPEC.loader.exec_module(audit)


def source(text: str) -> str:
    return textwrap.dedent(text).lstrip("\n")


class Repository:
    def __init__(self, root: Path) -> None:
        self.root = root
        home = root.parent / "home"
        home.mkdir(exist_ok=True)
        self.env = {
            key: value
            for key, value in os.environ.items()
            if not key.startswith("GIT_") and key not in {"HOME", "XDG_CONFIG_HOME"}
        }
        self.env.update(
            {
                "HOME": str(home),
                "XDG_CONFIG_HOME": str(home),
                "GIT_CONFIG_NOSYSTEM": "1",
                "GIT_CONFIG_GLOBAL": os.devnull,
                "GIT_AUTHOR_NAME": "Audit Test",
                "GIT_AUTHOR_EMAIL": "audit@example.invalid",
                "GIT_COMMITTER_NAME": "Audit Test",
                "GIT_COMMITTER_EMAIL": "audit@example.invalid",
                "GIT_AUTHOR_DATE": "2026-01-01T00:00:00Z",
                "GIT_COMMITTER_DATE": "2026-01-01T00:00:00Z",
            }
        )
        root.mkdir()
        self.git("init", "-q", "-b", "main")

    def git(self, *args: str) -> str:
        result = subprocess.run(
            ["git", *args], cwd=self.root, env=self.env, capture_output=True, text=True, check=False
        )
        if result.returncode != 0:
            raise AssertionError(f"git {' '.join(args)} failed: {result.stderr}")
        return result.stdout

    def write(self, path: str, text: str) -> None:
        target = self.root / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(text, encoding="utf-8", newline="\n")

    def remove(self, path: str) -> None:
        (self.root / path).unlink()

    def commit(self) -> str:
        self.git("add", "-A")
        self.git("commit", "-q", "-m", "base")
        return self.git("rev-parse", "HEAD").strip()

    def audit(self, *args: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [sys.executable, "-I", str(MODULE_PATH), *args],
            cwd=self.root,
            env=self.env,
            capture_output=True,
            text=True,
            check=False,
        )


class AuditTestCase(unittest.TestCase):
    def setUp(self) -> None:
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.repo = Repository(Path(temporary.name) / "repo")

    def move(self, from_files: list[str], to_files: list[str]) -> dict[str, object]:
        result = self.repo.audit("move", "--base", "HEAD", "--from", *from_files, "--to", *to_files)
        self.assertEqual(result.returncode, 0, result.stderr)
        multiset = re.search(r"^multiset: verbatim: (\d+)/(\d+) nonblank base lines; residual (\d+)$", result.stdout, re.M)
        ordered = re.search(
            r"^ordered: base residual (\d+) in \d+ hunks; destination-only (\d+) in \d+ hunks$", result.stdout, re.M
        )
        self.assertIsNotNone(multiset, result.stdout)
        self.assertIsNotNone(ordered, result.stdout)
        assert multiset is not None and ordered is not None
        verbatim, total, residual = (int(value) for value in multiset.groups())
        self.assertEqual(verbatim + residual, total, result.stdout)
        return {
            "verbatim": verbatim,
            "total": total,
            "multiset": residual,
            "residual": int(ordered.group(1)),
            "destination": int(ordered.group(2)),
            "stdout": result.stdout,
        }


class MoveAuditTests(AuditTestCase):
    METHOD_CLASS = source(
        """
        namespace Demo;

        public static class Numbers
        {
            public static int One() => 1;

            public static int Two()
            {
                return 2;
            }
        }
        """
    )

    def test_pure_move_reports_nothing(self) -> None:
        self.repo.write("src/Numbers.cs", self.METHOD_CLASS)
        self.repo.commit()
        self.repo.write(
            "src/Numbers.cs",
            source(
                """
                namespace Demo;

                public static class Numbers
                {
                    public static int One() => 1;
                }
                """
            ),
        )
        # Re-indented, with different blank lines: only nonblank stripped
        # lines count.
        self.repo.write(
            "src/Two.cs",
            "\n        public static int Two()\n{\n\n    return 2;\n        }\n\n",
        )
        report = self.move(["src/Numbers.cs"], ["src/Numbers.cs", "src/Two.cs"])
        self.assertEqual((report["verbatim"], report["total"]), (9, 9))
        self.assertEqual((report["multiset"], report["residual"], report["destination"]), (0, 0, 0))

    def test_edited_line_is_one_residual_and_one_destination_line(self) -> None:
        self.repo.write("src/Numbers.cs", self.METHOD_CLASS)
        self.repo.commit()
        self.repo.write("src/Numbers.cs", self.METHOD_CLASS.replace("return 2;", "return 3;"))
        report = self.move(["src/Numbers.cs"], ["src/Numbers.cs"])
        self.assertEqual((report["multiset"], report["residual"], report["destination"]), (1, 1, 1))
        self.assertIn("  base residual src/Numbers.cs:9 (1 line)\n    9: return 2;\n", report["stdout"])
        self.assertIn("  destination-only src/Numbers.cs:9 (1 line)\n    9: return 3;\n", report["stdout"])

    def test_duplicated_closing_brace_moved_once_is_residual(self) -> None:
        self.repo.write("src/Numbers.cs", self.METHOD_CLASS)
        self.repo.commit()
        # The method moves with its own closing brace; the class's closing
        # brace has nowhere to go.
        self.repo.write("src/Two.cs", "public static int Two()\n{\n    return 2;\n}\n")
        self.repo.write(
            "src/Numbers.cs",
            "namespace Demo;\npublic static class Numbers\n{\n    public static int One() => 1;\n",
        )
        report = self.move(["src/Numbers.cs"], ["src/Numbers.cs", "src/Two.cs"])
        self.assertEqual((report["verbatim"], report["total"]), (8, 9))
        self.assertEqual((report["multiset"], report["residual"], report["destination"]), (1, 1, 0))
        self.assertIn("    11: }\n", report["stdout"])

    def test_swapped_statements_are_invisible_to_the_multiset_but_not_to_the_order(self) -> None:
        body = source(
            """
            void Show()
            {
                ShowPrimary();
                StoreInCache();
            }
            """
        )
        self.repo.write("src/Hover.cs", body)
        self.repo.commit()
        self.repo.write(
            "src/Hover.cs", body.replace("    ShowPrimary();\n    StoreInCache();", "    StoreInCache();\n    ShowPrimary();")
        )
        report = self.move(["src/Hover.cs"], ["src/Hover.cs"])
        self.assertEqual(report["multiset"], 0)
        self.assertGreaterEqual(report["residual"], 1)
        self.assertGreaterEqual(report["destination"], 1)

    def test_inserted_statement_is_destination_only(self) -> None:
        body = source(
            """
            void Show()
            {
                ShowPrimary();
                StoreInCache();
            }
            """
        )
        self.repo.write("src/Hover.cs", body)
        self.repo.commit()
        self.repo.write("src/Hover.cs", body.replace("    StoreInCache();", "    Extra();\n    StoreInCache();"))
        report = self.move(["src/Hover.cs"], ["src/Hover.cs"])
        self.assertEqual((report["multiset"], report["residual"], report["destination"]), (0, 0, 1))
        self.assertIn("  destination-only src/Hover.cs:4 (1 line)\n    4: Extra();\n", report["stdout"])

    def test_statement_moved_into_another_destination_out_of_order_is_reported(self) -> None:
        # Base order a, b, c; b moves to a later file: within each file the
        # order holds, so this is a clean move, but a moved block that lands
        # before what used to precede it in the same destination is not.
        self.repo.write("src/A.cs", "First();\nSecond();\nThird();\n")
        self.repo.commit()
        self.repo.write("src/A.cs", "First();\nThird();\n")
        self.repo.write("src/B.cs", "Second();\n")
        clean = self.move(["src/A.cs"], ["src/A.cs", "src/B.cs"])
        self.assertEqual((clean["residual"], clean["destination"]), (0, 0))
        self.repo.remove("src/B.cs")
        self.repo.write("src/A.cs", "Third();\nFirst();\nSecond();\n")
        moved = self.move(["src/A.cs"], ["src/A.cs"])
        self.assertEqual(moved["multiset"], 0)
        self.assertEqual((moved["residual"], moved["destination"]), (1, 1))

    def test_from_files_are_read_at_the_base_even_when_deleted(self) -> None:
        self.repo.write("src/Old.cs", "Alpha();\nBeta();\n")
        self.repo.commit()
        self.repo.remove("src/Old.cs")
        self.repo.write("src/New.cs", "Alpha();\nBeta();\n")
        report = self.move(["src/Old.cs"], ["src/New.cs"])
        self.assertEqual((report["verbatim"], report["residual"], report["destination"]), (2, 0, 0))

    def test_usage_errors_exit_2(self) -> None:
        self.repo.write("src/A.cs", "A();\n")
        self.repo.commit()
        cases = [
            ("move", "--base", "no-such-rev", "--from", "src/A.cs", "--to", "src/A.cs"),
            ("move", "--base", "HEAD", "--from", "src/A.cs"),
            ("move", "--base", "HEAD", "--from", "src/Missing.cs", "--to", "src/A.cs"),
            ("move", "--base", "HEAD", "--from", "src/A.cs", "--to", "src/Missing.cs"),
            ("move", "--base", "HEAD", "--from", "../outside.cs", "--to", "src/A.cs"),
            ("regex", "--base", "HEAD", "--paths", "src/Nothing"),
            ("unknown",),
        ]
        for args in cases:
            with self.subTest(args=args):
                result = self.repo.audit(*args)
                self.assertEqual(result.returncode, 2, result.stdout + result.stderr)


class RegexAuditTests(AuditTestCase):
    CORE = source(
        """
        using System.Text.RegularExpressions;

        namespace Demo;

        internal static partial class Words
        {
            [GeneratedRegex(@"[A-Za-z]+(?:['’\\-][A-Za-z]+)*", RegexOptions.CultureInvariant)]
            private static partial Regex WordRegex();

            [GeneratedRegex(
                \"""^\\s*["'\\u00ab]?\\s*\\[[^\\]\\r\\n]{1,48}\\]\\s*\""",
                RegexOptions.CultureInvariant)]
            private static partial Regex LabelRegex();
        }
        """
    )
    WPF = source(
        """
        using System.Text.RegularExpressions;

        namespace Demo;

        internal static partial class Ocr
        {
            [GeneratedRegex(
                @"\\b(I|You)['\\u2019][1Il]{2}\\b",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
            private static partial Regex ContractionRegex();
        }
        """
    )

    def regex(self, *extra: str) -> subprocess.CompletedProcess[str]:
        return self.repo.audit("regex", "--base", "HEAD", *extra)

    def commit_default_layout(self) -> None:
        self.repo.write("src/AsteriaScale.Core/Services/Words.cs", self.CORE)
        self.repo.write("src/AsteriaScale/Services/Ocr.cs", self.WPF)
        self.repo.commit()

    def test_unchanged_tree_is_identical(self) -> None:
        self.commit_default_layout()
        result = self.regex()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("GeneratedRegex attributes: identical (3)\n", result.stdout)

    def test_regex_moved_unchanged_between_projects_is_identical(self) -> None:
        self.commit_default_layout()
        # The OCR rule moves from the WPF project into Core, re-wrapped and
        # with a comment inside the argument list.
        self.repo.remove("src/AsteriaScale/Services/Ocr.cs")
        self.repo.write(
            "src/AsteriaScale.Core/Services/OcrTextRules.cs",
            source(
                """
                internal static partial class OcrTextRules
                {
                    [GeneratedRegex(@"\\b(I|You)['\\u2019][1Il]{2}\\b", /* moved */ RegexOptions.CultureInvariant|RegexOptions.IgnoreCase)]
                    internal static partial Regex ContractionRegex();
                }
                """
            ),
        )
        self.repo.write("src/AsteriaScale/Services/Ocr.cs", "namespace Demo;\n")
        result = self.regex()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("GeneratedRegex attributes: identical (3)\n", result.stdout)

    def test_changed_pattern_is_different(self) -> None:
        self.commit_default_layout()
        self.repo.write(
            "src/AsteriaScale.Core/Services/Words.cs", self.CORE.replace("[A-Za-z]+(?:", "[A-Za-z]*(?:", 1)
        )
        result = self.regex()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("GeneratedRegex attributes: different (base 3, working tree 3)\n", result.stdout)
        self.assertIn("removed (base only) x1: pattern=@\"[A-Za-z]+(?:", result.stdout)
        self.assertIn("added (working tree only) x1: pattern=@\"[A-Za-z]*(?:", result.stdout)
        self.assertIn("at src/AsteriaScale.Core/Services/Words.cs:7", result.stdout)

    def test_changed_options_are_different(self) -> None:
        self.commit_default_layout()
        self.repo.write(
            "src/AsteriaScale/Services/Ocr.cs",
            self.WPF.replace("RegexOptions.CultureInvariant | RegexOptions.IgnoreCase", "RegexOptions.CultureInvariant"),
        )
        result = self.regex()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("options=RegexOptions.CultureInvariant|RegexOptions.IgnoreCase", result.stdout)

    def test_added_timeout_and_culture_are_different(self) -> None:
        self.commit_default_layout()
        self.repo.write(
            "src/AsteriaScale.Core/Services/Words.cs",
            self.CORE.replace(
                "RegexOptions.CultureInvariant)]\n    private static partial Regex WordRegex",
                'RegexOptions.CultureInvariant, 1000, "en-US")]\n    private static partial Regex WordRegex',
            ),
        )
        result = self.regex()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn('timeout=1000 culture="en-US"', result.stdout)

    def test_untracked_file_counts_as_the_build_compiles_it(self) -> None:
        self.commit_default_layout()
        self.repo.write(
            "src/AsteriaScale.Core/Services/Extra.cs",
            '[GeneratedRegex("x+")]\nprivate static partial Regex Extra();\n',
        )
        result = self.regex()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn('added (working tree only) x1: pattern="x+" options=- timeout=- culture=-', result.stdout)

    def test_files_outside_the_paths_are_ignored(self) -> None:
        self.commit_default_layout()
        self.repo.write("src/AsteriaScale.RankerWorker/Rules.cs", '[GeneratedRegex("y")]\nstatic partial Regex Y();\n')
        self.repo.write("tests/Rules.cs", '[GeneratedRegex("z")]\nstatic partial Regex Z();\n')
        result = self.regex()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        result = self.regex("--paths", "src/AsteriaScale.Core", "src/AsteriaScale", "tests")
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn('pattern="z"', result.stdout)


class ScannerTests(unittest.TestCase):
    def scan(self, text: str) -> list:
        return audit.scan_regex_attributes("Rules.cs", source(text))

    def test_multiline_attribute_with_parenthesis_inside_a_verbatim_string_is_parsed_whole(self) -> None:
        attributes = self.scan(
            """
            internal static partial class Rules
            {
                [GeneratedRegex(
                    @"(?<word>\\w+)\\)"")(",
                    RegexOptions.IgnoreCase
                        | RegexOptions.CultureInvariant)]
                private static partial Regex ClosingRegex();
            }
            """
        )
        self.assertEqual(len(attributes), 1)
        attribute = attributes[0]
        self.assertEqual(attribute.line, 3)
        self.assertEqual(attribute.pattern, '@"(?<word>\\w+)\\)"")("')
        self.assertEqual(attribute.options, "RegexOptions.IgnoreCase|RegexOptions.CultureInvariant")
        self.assertIsNone(attribute.timeout)
        self.assertIsNone(attribute.culture)

    def test_comments_strings_and_char_literals_never_count_or_derail_the_scan(self) -> None:
        attributes = self.scan(
            """
            #region Don't count this
            // [GeneratedRegex("commented")]
            /* [GeneratedRegex("block")] */
            internal static partial class Rules
            {
                private const char Quote = '"';
                private const char Escaped = '\\'';
                private static readonly string Fake = "[GeneratedRegex(\\"string\\")]";
                private static readonly string Verbatim = @"[GeneratedRegex(""verbatim"")]";
                private const string Folder = @"C:\\Temp\\";
                private static readonly string Raw = \"""[GeneratedRegex("raw")] "quoted" \""";
                private static string Hole(int count) => $"{string.Join(", ", new[] { "a" })} {count:N0}";
                private static string RawHole(int count) => $$\"""{{{count}}} "x" \""";
                private static string Unquote(string text) => $"{text.Trim('"') /* " */}";
                private static Regex GeneratedRegex(string pattern) => new(pattern);

                [System.Text.RegularExpressions.GeneratedRegex("real", RegexOptions.None)]
                private static partial Regex Real();

                [Obsolete("x"), global::System.Text.RegularExpressions.GeneratedRegexAttribute(pattern: "named", cultureName: "en-US")]
                private static partial Regex Named();

                [method: GeneratedRegex(\"""raw ) pattern\""", options: RegexOptions.Multiline, matchTimeoutMilliseconds: 50)]
                private static partial Regex Targeted();
            }
            #endregion
            """
        )
        described = [attribute.describe() for attribute in attributes]
        self.assertEqual(
            described,
            [
                'pattern="real" options=RegexOptions.None timeout=- culture=-',
                'pattern="named" options=- timeout=- culture="en-US"',
                'pattern="""raw ) pattern""" options=RegexOptions.Multiline timeout=50 culture=-',
            ],
        )

    def test_unterminated_literal_is_a_scan_error(self) -> None:
        with self.assertRaises(audit.ScanError):
            self.scan('[GeneratedRegex("open)]\nstatic partial Regex Broken();\n')


if __name__ == "__main__":
    unittest.main()
