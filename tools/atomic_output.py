"""Small stdlib-only helper for replacing generated files after success."""

from __future__ import annotations

import os
import shutil
import uuid
from contextlib import contextmanager
from pathlib import Path
from collections.abc import Callable, Iterable
from typing import Iterator


@contextmanager
def atomic_output_path(destination: Path) -> Iterator[Path]:
    """Yield a sibling temporary path and replace destination on clean exit.

    The caller may open, close, hash, and validate the temporary file before
    leaving the context. An exception preserves the previous destination.
    """

    destination = Path(destination)
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = destination.with_name(
        f".{destination.name}.{os.getpid()}.{uuid.uuid4().hex}.tmp"
    )
    try:
        yield temporary
        if not temporary.is_file():
            raise RuntimeError(f"Generated output is missing: {temporary}")
        temporary.replace(destination)
    finally:
        temporary.unlink(missing_ok=True)


def atomic_verified_copy(
    source: Path,
    destination: Path,
    verify: Callable[[Path], None],
) -> None:
    """Copy through a verified temporary file before replacing destination."""

    source = Path(source)
    destination = Path(destination)
    if source.resolve() == destination.resolve():
        verify(source)
        return

    with atomic_output_path(destination) as temporary:
        shutil.copyfile(source, temporary)
        verify(temporary)
    verify(destination)


def ensure_distinct_paths(
    named_paths: dict[str, Path],
    allowed_aliases: Iterable[frozenset[str]] = (),
) -> None:
    """Reject artifact aliases except explicitly safe source/destination pairs."""

    allowed = set(allowed_aliases)
    resolved = {
        name: Path(path).resolve()
        for name, path in named_paths.items()
    }
    names = list(resolved)
    for index, first in enumerate(names):
        for second in names[index + 1:]:
            if resolved[first] != resolved[second]:
                continue
            pair = frozenset((first, second))
            if pair not in allowed:
                raise ValueError(
                    f"unsafe artifact path collision: {first} and {second} "
                    f"both resolve to {resolved[first]}"
                )
