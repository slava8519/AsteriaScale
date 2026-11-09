#!/usr/bin/env python3
"""Audits that shared code moved or edited for Track III stayed verbatim.

Usage (run inside the clone; paths are relative to the repository root):

  audit-verbatim-move.py move --base <rev> --from <file>... --to <file>...
  audit-verbatim-move.py regex --base <rev> [--paths <dir>...]

move reads every --from file at <rev> and every --to file from the working
tree as nonblank lines with leading and trailing whitespace stripped, and
prints two sections:

- multiset: "verbatim: K/N". K of the N base lines reappear somewhere in the
  destination files, each destination line used once. This is blind to order:
  two swapped statements, or a statement moved after the line that used to
  follow it, still count as verbatim.
- ordered: for each --to file in the order given, difflib.SequenceMatcher
  (autojunk=False) matches the base lines not yet taken, in base order,
  against that file. "base residual R" lists the base lines no destination
  took and "destination-only D" the destination lines outside the matched
  subsequence, both as file:line hunks. Matched lines keep their relative
  order on both sides, so a swapped or inserted statement always shows up
  here even when the multiset section is clean.

When --from and --to name the same file, the same mode audits an in-place
edit. Exit 0 means a report was printed; the counts are for the reader (the
commit message names a substitution class for every residual hunk).

regex compares every [GeneratedRegex(...)] attribute under --paths (default
src/AsteriaScale.Core and src/AsteriaScale) at <rev> with the working tree:
tracked and untracked non-ignored .cs files, as the build compiles them. The
identity tool (check-windows-identity.sh) compares generator outputs only as
hashes of sorted tokens, which cannot tell a moved regex from a changed one;
this guard compares the attribute arguments themselves. A string-literal-aware
scanner skips comments, character literals and regular, verbatim, raw and
interpolated strings, so a ")" inside a pattern never ends an attribute and a
commented-out attribute never counts. Whitespace and comments outside literals
are collapsed; literals are compared as written. Each attribute becomes
(pattern, options, timeout, culture) and the two multisets are compared.
Exit 0: "GeneratedRegex attributes: identical (n)"; 1: the added and removed
entries are printed.

Exit 2 is a usage error in both modes (unknown revision, unreadable file, a
--paths entry without any .cs file, a source the scanner cannot parse).

Stdlib only and read-only: it runs git show/ls-tree/ls-files and reads files.
"""

from __future__ import annotations

import argparse
import collections
import difflib
import re
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path, PurePosixPath

DEFAULT_REGEX_PATHS = ("src/AsteriaScale.Core", "src/AsteriaScale")
ATTRIBUTE_NAMES = frozenset({"GeneratedRegex", "GeneratedRegexAttribute"})
REGEX_FIELDS = ("pattern", "options", "timeout", "culture")
# Parameter names of the GeneratedRegexAttribute constructors.
NAMED_ARGUMENTS = {
    "pattern": "pattern",
    "options": "options",
    "matchTimeoutMilliseconds": "timeout",
    "cultureName": "culture",
}


class UsageError(Exception):
    """Bad arguments or unreadable input: exit 2, never a partial report."""


class ScanError(UsageError):
    """A C# source the literal-aware scanner cannot follow."""


# --------------------------------------------------------------------------
# git access


def git(repo: Path, *args: str) -> bytes:
    result = subprocess.run(["git", "-C", str(repo), *args], capture_output=True, check=False)
    if result.returncode != 0:
        detail = result.stderr.decode("utf-8", errors="replace").strip()
        raise UsageError(f"git {' '.join(args)} failed: {detail}")
    return result.stdout


def find_repository() -> Path:
    result = subprocess.run(
        ["git", "rev-parse", "--show-toplevel"], capture_output=True, check=False
    )
    if result.returncode != 0:
        raise UsageError("run this inside a git working tree")
    return Path(result.stdout.decode("utf-8").strip()).resolve()


def resolve_commit(repo: Path, rev: str) -> str:
    result = subprocess.run(
        ["git", "-C", str(repo), "rev-parse", "--verify", "--quiet", f"{rev}^{{commit}}"],
        capture_output=True,
        check=False,
    )
    if result.returncode != 0:
        raise UsageError(f"'{rev}' is not a commit")
    return result.stdout.decode("utf-8").strip()


def repo_relative(repo: Path, path: str) -> str:
    """Repository-relative POSIX form, so git and the report use one spelling."""
    candidate = Path(path)
    if candidate.is_absolute():
        try:
            candidate = candidate.resolve().relative_to(repo)
        except ValueError:
            raise UsageError(f"{path} is outside the repository {repo}") from None
    parts = PurePosixPath(candidate.as_posix()).parts
    parts = tuple(part for part in parts if part != ".")
    if not parts or ".." in parts:
        raise UsageError(f"{path} must name a path inside the repository")
    return "/".join(parts)


def decode_source(data: bytes, label: str) -> str:
    try:
        text = data.decode("utf-8")
    except UnicodeDecodeError as error:
        raise UsageError(f"{label} is not UTF-8 ({error})") from None
    # A byte-order mark is file metadata, not part of the first line.
    return text[1:] if text.startswith("﻿") else text


def read_base(repo: Path, commit: str, path: str) -> str:
    return decode_source(git(repo, "show", f"{commit}:{path}"), f"{path} at {commit[:12]}")


def read_worktree(repo: Path, path: str) -> str:
    try:
        data = (repo / path).read_bytes()
    except OSError as error:
        raise UsageError(f"cannot read {path} in the working tree ({error.strerror})") from None
    return decode_source(data, f"{path} in the working tree")


# --------------------------------------------------------------------------
# move


@dataclass(frozen=True)
class Line:
    path: str
    number: int  # 1-based line number in the file
    index: int  # position among the file's nonblank lines
    text: str  # stripped


def nonblank_lines(path: str, text: str) -> list[Line]:
    lines: list[Line] = []
    # Split on LF only: str.splitlines would also break on form feeds and
    # Unicode separators, which C# keeps inside a line. strip() drops the CR
    # of a CRLF line together with the indentation.
    for number, raw in enumerate(text.split("\n"), start=1):
        stripped = raw.strip()
        if stripped:
            lines.append(Line(path, number, len(lines), stripped))
    return lines


def group_hunks(lines: list[Line]) -> list[list[Line]]:
    """Runs of lines adjacent among their file's nonblank lines."""
    hunks: list[list[Line]] = []
    for line in lines:
        last = hunks[-1][-1] if hunks else None
        if last is not None and last.path == line.path and last.index + 1 == line.index:
            hunks[-1].append(line)
        else:
            hunks.append([line])
    return hunks


def hunk_label(hunk: list[Line]) -> str:
    first, last = hunk[0].number, hunk[-1].number
    where = f"{hunk[0].path}:{first}" if first == last else f"{hunk[0].path}:{first}-{last}"
    count = len(hunk)
    return f"{where} ({count} line{'s' if count != 1 else ''})"


@dataclass
class MoveReport:
    base_lines: list[Line]
    multiset_residual: list[Line]
    ordered_residual: list[Line]
    destination_only: list[Line]


def audit_move(base_files: list[tuple[str, str]], destination_files: list[tuple[str, str]]) -> MoveReport:
    base_lines = [line for path, text in base_files for line in nonblank_lines(path, text)]
    destinations = [nonblank_lines(path, text) for path, text in destination_files]

    available = collections.Counter(line.text for lines in destinations for line in lines)
    multiset_residual: list[Line] = []
    for line in base_lines:
        if available[line.text] > 0:
            available[line.text] -= 1
        else:
            multiset_residual.append(line)

    remaining = list(base_lines)
    destination_only: list[Line] = []
    for lines in destinations:
        matcher = difflib.SequenceMatcher(
            None, [line.text for line in remaining], [line.text for line in lines], autojunk=False
        )
        taken_base: set[int] = set()
        taken_destination: set[int] = set()
        for block in matcher.get_matching_blocks():
            taken_base.update(range(block.a, block.a + block.size))
            taken_destination.update(range(block.b, block.b + block.size))
        destination_only.extend(line for i, line in enumerate(lines) if i not in taken_destination)
        remaining = [line for i, line in enumerate(remaining) if i not in taken_base]

    return MoveReport(base_lines, multiset_residual, remaining, destination_only)


def print_hunks(title: str, lines: list[Line]) -> None:
    for hunk in group_hunks(lines):
        print(f"  {title} {hunk_label(hunk)}")
        for line in hunk:
            print(f"    {line.number}: {line.text}")


def run_move(repo: Path, args: argparse.Namespace) -> int:
    commit = resolve_commit(repo, args.base)
    from_paths = [repo_relative(repo, path) for path in args.from_files]
    to_paths = [repo_relative(repo, path) for path in args.to_files]
    base_files = [(path, read_base(repo, commit, path)) for path in from_paths]
    destination_files = [(path, read_worktree(repo, path)) for path in to_paths]
    report = audit_move(base_files, destination_files)

    def describe(files: list[tuple[str, str]]) -> str:
        return ", ".join(f"{path} ({len(nonblank_lines(path, text))})" for path, text in files)

    total = len(report.base_lines)
    residual = report.multiset_residual
    ordered = report.ordered_residual
    added = report.destination_only
    print(f"move audit: base {commit[:12]} ({args.base}) against the working tree")
    print(f"  from (nonblank lines at base): {describe(base_files)}")
    print(f"  to (nonblank lines in the working tree): {describe(destination_files)}")
    print(f"multiset: verbatim: {total - len(residual)}/{total} nonblank base lines; residual {len(residual)}")
    for line in residual:
        print(f"  {line.path}:{line.number}: {line.text}")
    print(
        f"ordered: base residual {len(ordered)} in {len(group_hunks(ordered))} hunks; "
        f"destination-only {len(added)} in {len(group_hunks(added))} hunks"
    )
    print_hunks("base residual", ordered)
    print_hunks("destination-only", added)
    return 0


# --------------------------------------------------------------------------
# regex: a C# scanner that knows just enough lexical structure


def is_identifier_char(char: str) -> bool:
    return char == "_" or char.isalnum()


class CSharpScanner:
    """Finds [GeneratedRegex(...)] attributes outside comments and literals.

    It does not parse C#; it only follows the lexical structure that can hide
    or fake a parenthesis, a quote or an attribute name: // and /* */
    comments, preprocessor lines, character literals and string literals
    (regular with backslash escapes, verbatim with "" escapes, raw with three
    or more quotes, and interpolated forms of all three, whose holes are code
    with their own nested literals).
    """

    def __init__(self, text: str, path: str) -> None:
        self.text = text
        self.path = path
        self.length = len(text)

    def fail(self, index: int, message: str) -> ScanError:
        line = self.text.count("\n", 0, index) + 1
        return ScanError(f"{self.path}:{line}: {message}")

    def at_line_start(self, index: int) -> bool:
        start = self.text.rfind("\n", 0, index) + 1
        return self.text[start:index].strip() == ""

    # Each skip_* method takes the index of the token's first character and
    # returns the index just past it.

    def skip_line_comment(self, index: int) -> int:
        end = self.text.find("\n", index)
        return self.length if end < 0 else end

    def skip_block_comment(self, index: int) -> int:
        end = self.text.find("*/", index + 2)
        if end < 0:
            raise self.fail(index, "unterminated block comment")
        return end + 2

    def skip_char_literal(self, index: int) -> int:
        cursor = index + 1
        if cursor < self.length and self.text[cursor] == "\\":
            cursor += 2
        else:
            cursor += 1
        # " and \U0001F600 escapes run several characters past the
        # backslash; nothing in a valid char literal spans a line.
        while cursor < self.length and self.text[cursor] not in "'\n":
            cursor += 1
        if cursor >= self.length or self.text[cursor] != "'":
            raise self.fail(index, "unterminated character literal")
        return cursor + 1

    def string_start(self, index: int) -> tuple[int, int, bool] | None:
        """(index of the first quote, dollar count, verbatim) if a string starts here."""
        cursor = index
        dollars = 0
        verbatim = False
        while cursor < self.length and self.text[cursor] in "$@":
            if self.text[cursor] == "@":
                if verbatim:
                    return None
                verbatim = True
            else:
                dollars += 1
            cursor += 1
        if cursor < self.length and self.text[cursor] == '"':
            return cursor, dollars, verbatim
        return None

    def skip_string(self, index: int) -> int:
        start = self.string_start(index)
        assert start is not None
        quote, dollars, verbatim = start
        run = 0
        while quote + run < self.length and self.text[quote + run] == '"':
            run += 1
        if run >= 3 and not verbatim:
            return self.skip_raw(quote, run, dollars)
        cursor = quote + 1
        while cursor < self.length:
            char = self.text[cursor]
            if char == '"':
                if verbatim and self.text.startswith('""', cursor):
                    cursor += 2
                    continue
                return cursor + 1
            if char == "\\" and not verbatim:
                cursor += 2
                continue
            if char == "\n" and not verbatim:
                raise self.fail(index, "newline in a regular string literal")
            if dollars and char == "{":
                if self.text.startswith("{{", cursor):
                    cursor += 2
                    continue
                cursor = self.skip_hole(cursor + 1, 1)
                continue
            cursor += 1
        raise self.fail(index, "unterminated string literal")

    def skip_raw(self, quote: int, run: int, dollars: int) -> int:
        cursor = quote + run
        while cursor < self.length:
            char = self.text[cursor]
            if char == '"':
                end = cursor
                while end < self.length and self.text[end] == '"':
                    end += 1
                if end - cursor >= run:
                    return cursor + run
                cursor = end
                continue
            if dollars and char == "{":
                end = cursor
                while end < self.length and self.text[end] == "{":
                    end += 1
                # $$"""…""" opens a hole with exactly as many braces as it
                # has dollars; fewer braces are content.
                if end - cursor >= dollars:
                    cursor = self.skip_hole(end, dollars)
                else:
                    cursor = end
                continue
            cursor += 1
        raise self.fail(quote, "unterminated raw string literal")

    def skip_hole(self, index: int, closing: int) -> int:
        """Skips an interpolation hole's code and format clause up to its closing braces."""
        depth = 0
        cursor = index
        while cursor < self.length:
            skipped = self.skip_trivia_or_literal(cursor)
            if skipped is not None:
                cursor = skipped
                continue
            char = self.text[cursor]
            if char in "([{":
                depth += 1
            elif char in ")]":
                depth -= 1
            elif char == "}":
                if depth == 0:
                    if self.text.startswith("}" * closing, cursor):
                        return cursor + closing
                    raise self.fail(cursor, "unbalanced interpolation hole")
                depth -= 1
            elif char == ":":
                if self.text.startswith("::", cursor):
                    cursor += 2
                    continue
                if depth == 0:
                    # A format clause runs to the closing braces; a
                    # conditional operator inside a hole must be
                    # parenthesized, so a bare ':' at depth 0 starts one.
                    end = self.text.find("}" * closing, cursor)
                    if end < 0:
                        raise self.fail(cursor, "unterminated interpolation format clause")
                    return end + closing
            cursor += 1
        raise self.fail(index, "unterminated interpolation hole")

    def skip_trivia_or_literal(self, index: int) -> int | None:
        """The index past a comment, preprocessor line or literal starting here, else None."""
        char = self.text[index]
        if char == "/" and self.text.startswith("//", index):
            return self.skip_line_comment(index)
        if char == "/" and self.text.startswith("/*", index):
            return self.skip_block_comment(index)
        if char == "#" and self.at_line_start(index):
            return self.skip_line_comment(index)
        if char == "'":
            return self.skip_char_literal(index)
        if char in "$@\"" and self.string_start(index) is not None:
            return self.skip_string(index)
        return None

    def attributes(self) -> list[tuple[int, list[str]]]:
        """(line, normalized top-level arguments) of every attribute found."""
        found: list[tuple[int, list[str]]] = []
        # The last significant code tokens, enough to see whether a name is
        # an attribute name: [Name(, [a, Name(, [method: Name( or a
        # qualified form of any of them.
        tokens: list[str] = []
        cursor = 0
        while cursor < self.length:
            char = self.text[cursor]
            if char.isspace():
                cursor += 1
                continue
            skipped = self.skip_trivia_or_literal(cursor)
            if skipped is not None:
                tokens.append("literal")
                cursor = skipped
                continue
            if char == "@" or is_identifier_char(char):
                end = cursor + 1
                while end < self.length and is_identifier_char(self.text[end]):
                    end += 1
                name = self.text[cursor:end].lstrip("@")
                if name in ATTRIBUTE_NAMES and self.is_attribute_name(tokens):
                    open_paren = self.next_significant(end)
                    if open_paren is not None and self.text[open_paren] == "(":
                        line = self.text.count("\n", 0, cursor) + 1
                        arguments, end = self.read_arguments(open_paren)
                        found.append((line, arguments))
                tokens.append(name)
                cursor = end
                continue
            if self.text.startswith("::", cursor):
                tokens.append("::")
                cursor += 2
                continue
            tokens.append(char)
            cursor += 1
            if len(tokens) > 64:
                del tokens[:32]
        return found

    @staticmethod
    def is_attribute_name(tokens: list[str]) -> bool:
        position = len(tokens)
        # Walk back over a qualifier such as System.Text.RegularExpressions.
        # or global::System.Text.RegularExpressions.
        while position >= 2 and tokens[position - 1] in (".", "::") and is_identifier_char(
            tokens[position - 2][:1]
        ):
            position -= 2
        if position == 0:
            return False
        previous = tokens[position - 1]
        return previous in ("[", ",", ":")

    def next_significant(self, index: int) -> int | None:
        cursor = index
        while cursor < self.length:
            char = self.text[cursor]
            if char.isspace():
                cursor += 1
                continue
            if self.text.startswith("//", cursor):
                cursor = self.skip_line_comment(cursor)
                continue
            if self.text.startswith("/*", cursor):
                cursor = self.skip_block_comment(cursor)
                continue
            return cursor
        return None

    def read_arguments(self, open_paren: int) -> tuple[list[str], int]:
        """Top-level arguments with whitespace and comments outside literals collapsed."""
        arguments: list[str] = []
        current: list[str] = []
        pending_space = False
        depth = 0
        cursor = open_paren + 1

        def append(piece: str) -> None:
            nonlocal pending_space
            # One space survives only where it separates two identifier
            # characters, so "a | b" and "a|b" compare equal while two words
            # never merge into one.
            if pending_space and current and is_identifier_char(current[-1][-1]) and is_identifier_char(piece[0]):
                current.append(" ")
            current.append(piece)
            pending_space = False

        while cursor < self.length:
            char = self.text[cursor]
            if char.isspace():
                pending_space = True
                cursor += 1
                continue
            if self.text.startswith("//", cursor) or self.text.startswith("/*", cursor) or (
                char == "#" and self.at_line_start(cursor)
            ):
                end = self.skip_trivia_or_literal(cursor)
                assert end is not None
                pending_space = True
                cursor = end
                continue
            literal_end = self.skip_trivia_or_literal(cursor)
            if literal_end is not None:
                append(self.text[cursor:literal_end])
                cursor = literal_end
                continue
            if char in "([{":
                depth += 1
            elif char in ")]}":
                if depth == 0:
                    if char != ")":
                        raise self.fail(cursor, f"unbalanced '{char}' in a GeneratedRegex argument list")
                    arguments.append("".join(current))
                    if arguments == [""]:
                        arguments = []
                    if "" in arguments:
                        raise self.fail(open_paren, "empty GeneratedRegex argument")
                    return arguments, cursor + 1
                depth -= 1
            elif char == "," and depth == 0:
                arguments.append("".join(current))
                current = []
                pending_space = False
                cursor += 1
                continue
            append(char)
            cursor += 1
        raise self.fail(open_paren, "unterminated GeneratedRegex argument list")


def is_string_literal(argument: str) -> bool:
    return re.match(r'^[$@]*"', argument) is not None


@dataclass(frozen=True)
class RegexAttribute:
    path: str
    line: int
    pattern: str | None
    options: str | None
    timeout: str | None
    culture: str | None
    extra: tuple[str, ...]

    def key(self) -> tuple:
        return (self.pattern, self.options, self.timeout, self.culture, self.extra)

    def describe(self) -> str:
        parts = [f"{name}={value if value is not None else '-'}" for name, value in zip(REGEX_FIELDS, self.key())]
        if self.extra:
            parts.append(f"other={', '.join(self.extra)}")
        return " ".join(parts)


def regex_attribute(path: str, line: int, arguments: list[str]) -> RegexAttribute:
    fields: dict[str, str | None] = dict.fromkeys(REGEX_FIELDS)
    extra: list[str] = []
    positional: list[str] = []
    named: list[tuple[str, str]] = []
    for argument in arguments:
        match = re.match(r"^@?([A-Za-z_][A-Za-z0-9_]*):(?!:)(.*)$", argument, re.DOTALL)
        if match:
            named.append((match.group(1), match.group(2)))
        else:
            positional.append(argument)
    if positional:
        fields["pattern"] = positional[0]
    # The constructors are (pattern), (pattern, options), (pattern, culture),
    # (pattern, options, culture), (pattern, options, timeout) and
    # (pattern, options, timeout, culture): a string literal after the
    # pattern is the culture name. A culture given as a named constant is
    # shown as the timeout; the comparison uses every argument either way.
    for position, argument in enumerate(positional[1:]):
        if is_string_literal(argument) and fields["culture"] is None:
            fields["culture"] = argument
        elif position == 0:
            fields["options"] = argument
        elif position == 1 and fields["timeout"] is None:
            fields["timeout"] = argument
        else:
            extra.append(argument)
    for name, value in named:
        field = NAMED_ARGUMENTS.get(name)
        if field is not None and fields[field] is None:
            fields[field] = value
        else:
            extra.append(f"{name}:{value}")
    return RegexAttribute(
        path, line, fields["pattern"], fields["options"], fields["timeout"], fields["culture"], tuple(extra)
    )


def scan_regex_attributes(path: str, text: str) -> list[RegexAttribute]:
    scanner = CSharpScanner(text, path)
    return [regex_attribute(path, line, arguments) for line, arguments in scanner.attributes()]


def base_sources(repo: Path, commit: str, paths: list[str]) -> dict[str, list[str]]:
    listing = git(repo, "ls-tree", "-r", "-z", "--name-only", commit, "--", *paths)
    names = [name for name in listing.decode("utf-8").split("\0") if name.endswith(".cs")]
    return {path: [name for name in names if name == path or name.startswith(path + "/")] for path in paths}


def worktree_sources(repo: Path, paths: list[str]) -> dict[str, list[str]]:
    # Tracked plus untracked non-ignored files: the SDK's *.cs glob compiles
    # an untracked file too. A tracked file deleted in the working tree is
    # still listed by -c; it would not be compiled, so it is skipped.
    listing = git(repo, "ls-files", "-z", "-co", "--exclude-standard", "--deduplicate", "--", *paths)
    names = sorted(
        {
            name
            for name in listing.decode("utf-8").split("\0")
            if name.endswith(".cs") and (repo / name).is_file()
        }
    )
    return {path: [name for name in names if name == path or name.startswith(path + "/")] for path in paths}


def run_regex(repo: Path, args: argparse.Namespace) -> int:
    commit = resolve_commit(repo, args.base)
    paths = [repo_relative(repo, path) for path in args.paths]
    base_by_path = base_sources(repo, commit, paths)
    work_by_path = worktree_sources(repo, paths)
    for path in paths:
        if not base_by_path[path] and not work_by_path[path]:
            raise UsageError(f"--paths {path} holds no .cs file at {args.base} or in the working tree")

    base_files = sorted({name for names in base_by_path.values() for name in names})
    work_files = sorted({name for names in work_by_path.values() for name in names})
    base_attributes = [
        attribute for name in base_files for attribute in scan_regex_attributes(name, read_base(repo, commit, name))
    ]
    work_attributes = [
        attribute for name in work_files for attribute in scan_regex_attributes(name, read_worktree(repo, name))
    ]

    print(f"regex audit: base {commit[:12]} ({args.base}) against the working tree; paths: {' '.join(paths)}")
    base_counts = collections.Counter(attribute.key() for attribute in base_attributes)
    work_counts = collections.Counter(attribute.key() for attribute in work_attributes)
    if base_counts == work_counts:
        print(f"GeneratedRegex attributes: identical ({len(base_attributes)})")
        return 0

    print(
        f"GeneratedRegex attributes: different (base {len(base_attributes)}, "
        f"working tree {len(work_attributes)})"
    )
    for label, counts, attributes in (
        ("removed (base only)", base_counts - work_counts, base_attributes),
        ("added (working tree only)", work_counts - base_counts, work_attributes),
    ):
        for key, count in sorted(counts.items(), key=lambda item: repr(item[0])):
            places = [attribute for attribute in attributes if attribute.key() == key]
            where = ", ".join(f"{attribute.path}:{attribute.line}" for attribute in places)
            print(f"  {label} x{count}: {places[0].describe()}")
            print(f"    at {where}")
    return 1


# --------------------------------------------------------------------------


class Parser(argparse.ArgumentParser):
    def error(self, message: str) -> None:  # argparse exits 2 too; keep one prefix
        self.print_usage(sys.stderr)
        print(f"audit-verbatim-move: {message}", file=sys.stderr)
        raise SystemExit(2)


def build_parser() -> Parser:
    parser = Parser(prog="audit-verbatim-move.py", description=__doc__.split("\n\n")[0])
    commands = parser.add_subparsers(dest="command", required=True, parser_class=Parser)

    move = commands.add_parser("move", help="multiset and order-aware audit of a move or an in-place edit")
    move.add_argument("--base", required=True, help="revision the --from files are read at")
    move.add_argument("--from", dest="from_files", nargs="+", required=True, metavar="FILE")
    move.add_argument("--to", dest="to_files", nargs="+", required=True, metavar="FILE")

    regex = commands.add_parser("regex", help="compare [GeneratedRegex] attributes with a base revision")
    regex.add_argument("--base", required=True, help="revision to compare the working tree with")
    regex.add_argument("--paths", nargs="+", default=list(DEFAULT_REGEX_PATHS), metavar="DIR")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        repo = find_repository()
        if args.command == "move":
            return run_move(repo, args)
        return run_regex(repo, args)
    except UsageError as error:
        print(f"audit-verbatim-move: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    # Line-buffered output keeps the report readable when it is piped.
    sys.stdout.reconfigure(line_buffering=True)
    raise SystemExit(main())
