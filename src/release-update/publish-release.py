#!/usr/bin/env python3
"""Upload architecture-specific LF Portable executables with GitHub CLI."""

from __future__ import annotations

import argparse
from pathlib import Path
import re
import shutil
import subprocess
import sys
import zipfile


VERSION_PATTERN = re.compile(r"^\d+\.\d+\.\d+\.\d+$")
EMBEDDED_RELEASE_REQUIRED = {
    "codexdata/tools/launchers/codexportable.x86.exe",
    "codexdata/tools/launchers/codexportable.x64.exe",
    "codexdata/tools/launchers/codexportable.arm64.exe",
    "codexdata/packages/lfportable-common.zip",
}


def require_embedded_release_executable(path: Path, architecture: str) -> None:
    """Reject a launcher component when an assembled release is required."""

    try:
        with path.open("rb") as executable:
            if executable.read(2) != b"MZ":
                raise ValueError(f"release asset is not a Windows PE file: {path}")
        with zipfile.ZipFile(path) as archive:
            names = {
                info.filename.replace("\\", "/").rstrip("/").casefold()
                for info in archive.infolist()
                if info.filename and not info.is_dir()
            }
    except ValueError:
        raise
    except (OSError, zipfile.BadZipFile) as error:
        raise ValueError(
            f"release asset has no embedded program payload: {path}; "
            "assemble it with release.py before publishing"
        ) from error

    expected_package = f"codexdata/packages/lfportable-{architecture}.msix"
    missing = sorted((EMBEDDED_RELEASE_REQUIRED | {expected_package}) - names)
    desktop_packages = sorted(
        name for name in names
        if name.startswith("codexdata/packages/lfportable-") and name.endswith(".msix")
    )
    if len(desktop_packages) != 1:
        raise ValueError(
            f"release asset must embed exactly one desktop package: {path}"
        )
    if missing:
        raise ValueError(
            "release asset is missing embedded release inputs: "
            f"{', '.join(missing)}; assemble it with release.py before publishing"
        )


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Publish LFPortable-x64.exe and LFPortable-arm64.exe with gh."
    )
    parser.add_argument("--release-root", required=True, type=Path)
    parser.add_argument("--version", required=True)
    parser.add_argument("--repository", default="riveryang6/lf-portable")
    parser.add_argument(
        "--draft",
        action="store_true",
        help="create a draft release instead of publishing it immediately",
    )
    return parser.parse_args()


def find_gh() -> str:
    for candidate in ("gh", "gh.exe"):
        resolved = shutil.which(candidate)
        if resolved:
            return resolved
    raise ValueError("GitHub CLI (gh or gh.exe) is not available on PATH")


def gh_file_argument(gh: str, path: Path) -> str:
    """Translate WSL paths when a Windows GitHub CLI is selected."""

    if not sys.platform.startswith("linux") or not gh.lower().endswith(".exe"):
        return str(path)
    wslpath = shutil.which("wslpath")
    if not wslpath:
        raise ValueError("wslpath is required when WSL uses a Windows gh.exe")
    try:
        translated = subprocess.run(
            [wslpath, "-aw", str(path)],
            check=True,
            capture_output=True,
            text=True,
        ).stdout.strip()
    except (OSError, subprocess.CalledProcessError) as error:
        raise ValueError(
            f"cannot translate release asset for Windows gh.exe: {path}: {error}"
        ) from error
    if not translated:
        raise ValueError(f"wslpath returned an empty release asset path: {path}")
    return translated


def main() -> int:
    args = parse_args()
    if not VERSION_PATTERN.fullmatch(args.version):
        print("publish-release.py: --version must contain four numeric components", file=sys.stderr)
        return 1

    release_root = args.release_root.expanduser().resolve()
    executables = [
        release_root / f"LFPortable-{architecture}.exe"
        for architecture in ("x64", "arm64")
    ]
    missing = [str(path) for path in executables if not path.is_file()]
    if missing:
        print(
            "publish-release.py: release executable(s) are missing: "
            + ", ".join(missing),
            file=sys.stderr,
        )
        return 1

    try:
        for architecture, executable in zip(("x64", "arm64"), executables):
            require_embedded_release_executable(executable, architecture)
    except ValueError as error:
        print(f"publish-release.py: {error}", file=sys.stderr)
        return 1

    try:
        gh = find_gh()
        executable_arguments = [gh_file_argument(gh, executable) for executable in executables]
    except ValueError as error:
        print(f"publish-release.py: {error}", file=sys.stderr)
        return 1

    command = [
        gh,
        "release",
        "create",
        f"v{args.version}",
        *executable_arguments,
        "--repo",
        args.repository,
        "--title",
        f"LF Portable {args.version}",
        "--generate-notes",
    ]
    if args.draft:
        command.append("--draft")
    try:
        subprocess.run(command, check=True)
    except (OSError, subprocess.CalledProcessError) as error:
        print(f"publish-release.py: gh release create failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
