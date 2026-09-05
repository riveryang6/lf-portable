#!/usr/bin/env python3
"""Assemble architecture-specific, self-extracting LF Portable executables."""

from __future__ import annotations

import argparse
import os
from pathlib import Path
import re
import shutil
import stat
import threading
import sys
import tempfile
import zipfile


DOCUMENTATION_SOURCE = Path(os.path.abspath(__file__)).with_name("CodexData-README.txt")
VERSION_PATTERN = re.compile(r"^\d+\.\d+\.\d+\.\d+$")
ARCHITECTURES = ("x64", "arm64")
BOOTSTRAPPER_NAME = "CodexPortable.bootstrapper.exe"

COMMON_FILES = (
    ("CodexData/README.txt", "documentation"),
    ("CodexData/THIRD_PARTY.txt", "base"),
    ("CodexData/tools/launchers/CodexPortable.x86.exe", "launcher"),
    ("CodexData/tools/launchers/CodexPortable.x64.exe", "launcher"),
    ("CodexData/tools/launchers/CodexPortable.arm64.exe", "launcher"),
    ("CodexData/packages/LFPortable-common.zip", "base"),
)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Create offline, architecture-specific LF Portable EXE releases."
    )
    parser.add_argument("--base-root", required=True, type=Path)
    parser.add_argument("--launcher-root", required=True, type=Path)
    parser.add_argument(
        "--bootstrapper",
        type=Path,
        help=(
            "bare bootstrapper input; defaults to ../build/"
            f"{BOOTSTRAPPER_NAME} relative to --launcher-root"
        ),
    )
    parser.add_argument("--output-root", required=True, type=Path)
    parser.add_argument("--version", required=True)
    parser.add_argument(
        "--architecture",
        choices=("both",) + ARCHITECTURES,
        default="both",
        help="package one architecture or both (default: both)",
    )
    return parser.parse_args()


REPARSE_POINT_ATTRIBUTE = 0x400


def reject_reparse_path(path: Path, label: str) -> None:
    """Reject symlinks/junctions in an input path before resolving it."""

    current = path.expanduser()
    while True:
        try:
            info = current.lstat()
        except FileNotFoundError:
            pass
        except OSError as error:
            raise ValueError(f"cannot inspect {label}: {current}: {error}") from error
        else:
            attributes = int(getattr(info, "st_file_attributes", 0))
            if stat.S_ISLNK(info.st_mode) or attributes & REPARSE_POINT_ATTRIBUTE:
                raise ValueError(f"{label} contains a symlink or reparse point: {current}")
        parent = current.parent
        if parent == current:
            return
        current = parent


def absolute(path: Path, label: str) -> Path:
    # abspath normalizes a relative spelling without resolving symlinks, so
    # reject_reparse_path can inspect every original path component first.
    candidate = Path(os.path.abspath(path.expanduser()))
    reject_reparse_path(candidate, label)
    return candidate


def require_regular_file(path: Path, label: str) -> Path:
    reject_reparse_path(path, label)
    try:
        # lstat avoids following a link introduced after the ancestor walk.
        info = path.lstat()
    except OSError as error:
        raise ValueError(f"required {label} is unavailable: {path}: {error}") from error
    if not stat.S_ISREG(info.st_mode) or int(getattr(info, "st_file_attributes", 0)) & REPARSE_POINT_ATTRIBUTE:
        raise ValueError(f"required {label} is missing or is not a regular file: {path}")
    return path


def source_files(base_root: Path, launcher_root: Path, architecture: str,
                 version: str) -> list[tuple[str, Path]]:
    roots = {"base": base_root, "documentation": DOCUMENTATION_SOURCE, "launcher": launcher_root}
    entries = list(COMMON_FILES)
    entries.append((f"CodexData/packages/LFPortable-{architecture}.msix", "base"))
    result: list[tuple[str, Path]] = []
    for relative_path, source_name in entries:
        root = roots[source_name]
        source = root if source_name == "documentation" else root / relative_path
        source = require_regular_file(source, f"{source_name} input {relative_path}")
        result.append((relative_path, source))
    return result


def archive_payload(archive_path: Path,
                    files: list[tuple[str, Path]]) -> None:
    """Write the payload zip straight from the release sources.

    The previous staging copy re-wrote the multi-gigabyte packages into a
    temporary payload root before zipping them; zipping from the original
    sources removes that whole copy pass.
    """

    with zipfile.ZipFile(archive_path, mode="x", compression=zipfile.ZIP_DEFLATED, allowZip64=True) as archive:
        for relative_path, source in files:
            # Packages are already compressed; storing them avoids a second multi-GB pass.
            compression = zipfile.ZIP_STORED if relative_path.endswith((".zip", ".msix")) else zipfile.ZIP_DEFLATED
            archive.write(source, arcname=relative_path, compress_type=compression)


def append_payload(bootstrapper: Path, payload_archive: Path,
                   output_path: Path) -> None:
    """Create a self-extracting EXE without loading the multi-gigabyte payload."""

    with bootstrapper.open("rb") as source, output_path.open("xb") as destination:
        shutil.copyfileobj(source, destination, length=1024 * 1024)
        with payload_archive.open("rb") as payload:
            shutil.copyfileobj(payload, destination, length=4 * 1024 * 1024)


def create_architecture_release(
    base_root: Path, launcher_root: Path, bootstrapper_path: Path,
    output_root: Path, architecture: str, version: str,
) -> Path:
    files = source_files(base_root, launcher_root, architecture, version)
    bootstrapper = require_regular_file(
        bootstrapper_path, "launcher bootstrapper"
    )
    output_path = output_root / f"LFPortable-{architecture}.exe"
    with tempfile.TemporaryDirectory(
        dir=str(output_root.parent), prefix=f".{output_root.name}-{architecture}-"
    ) as temporary:
        payload_archive = Path(temporary) / "payload.zip"
        archive_payload(payload_archive, files)
        append_payload(bootstrapper, payload_archive, output_path)
    return output_path


def create_release(base_root: Path, launcher_root: Path, output_root: Path, version: str,
                   requested_architecture: str, bootstrapper_path: Path | None = None) -> None:
    if not VERSION_PATTERN.fullmatch(version):
        raise ValueError("--version must contain exactly four numeric components")
    base_root = absolute(base_root, "base root")
    launcher_root = absolute(launcher_root, "launcher root")
    if bootstrapper_path is None:
        bootstrapper_path = launcher_root.parent / "build" / BOOTSTRAPPER_NAME
    bootstrapper_path = absolute(bootstrapper_path, "bootstrapper input")
    try:
        bootstrapper_path.relative_to(launcher_root)
    except ValueError:
        pass
    else:
        raise ValueError("bootstrapper input must be outside the launcher root")
    output_root = absolute(output_root, "output root")
    if output_root.exists():
        raise ValueError(f"output root already exists: {output_root}")
    if output_root.parent.exists() and not output_root.parent.is_dir():
        raise ValueError(f"output parent is not a directory: {output_root.parent}")

    architectures = ARCHITECTURES if requested_architecture == "both" else (requested_architecture,)
    output_root.mkdir(parents=True)
    try:
        # The two architecture executables are independent; build them in
        # parallel so their multi-gigabyte payload I/O overlaps.
        results = [None] * len(architectures)

        def run_architecture(index: int, architecture: str) -> None:
            try:
                results[index] = create_architecture_release(
                    base_root, launcher_root, bootstrapper_path, output_root,
                    architecture, version
                )
            except Exception as error:
                results[index] = error

        if len(architectures) == 1:
            run_architecture(0, architectures[0])
        else:
            workers = [
                threading.Thread(target=run_architecture, args=(index, architecture))
                for index, architecture in enumerate(architectures)
            ]
            for worker in workers:
                worker.start()
            for worker in workers:
                worker.join()

        failures = [result for result in results if isinstance(result, Exception)]
        if failures:
            raise failures[0]
        for result in results:
            if isinstance(result, Path):
                print(f"executable: {result}")
    except Exception:
        shutil.rmtree(output_root, ignore_errors=True)
        raise


def main() -> int:
    args = parse_args()
    try:
        create_release(
            args.base_root, args.launcher_root, args.output_root, args.version,
            args.architecture, args.bootstrapper
        )
    except (OSError, ValueError, shutil.Error, zipfile.BadZipFile) as error:
        print(f"release.py: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
