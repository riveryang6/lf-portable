#!/usr/bin/env python3
"""Open a Windows Sandbox session for a manual LF Portable desktop smoke test."""

from __future__ import annotations

import argparse
from pathlib import Path
import os
import shutil
import subprocess
import sys
import tempfile
from xml.sax.saxutils import escape


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Launch a manual Windows Sandbox smoke test from WSL."
    )
    parser.add_argument("--release-root", required=True, type=Path)
    parser.add_argument(
        "--architecture",
        choices=("x64", "arm64"),
        help="architecture to select when the mapped directory contains both EXEs",
    )
    return parser.parse_args()


def run_text(command: list[str]) -> str:
    return subprocess.run(command, check=True, text=True, capture_output=True).stdout.strip()


def windows_path(path: Path) -> str:
    return run_text(["wslpath", "-aw", str(path)])


def windows_temp_directory() -> Path:
    windows_temp = run_text(["cmd.exe", "/d", "/c", "echo", "%TEMP%"])
    wsl_temp = run_text(["wslpath", "-au", windows_temp])
    result = Path(wsl_temp)
    if not result.is_dir():
        raise ValueError(f"Windows temporary directory is unavailable from WSL: {result}")
    return result


def windows_directory() -> Path:
    windows_root = run_text(["cmd.exe", "/d", "/c", "echo", "%WINDIR%"])
    wsl_root = run_text(["wslpath", "-au", windows_root])
    result = Path(wsl_root)
    if not result.is_dir():
        raise ValueError(f"Windows directory is unavailable from WSL: {result}")
    return result


def build_configuration(release_root: str, tools_root: str, architecture: str) -> str:
    release = escape(release_root)
    tools = escape(tools_root)
    return f"""<Configuration>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>{release}</HostFolder>
      <SandboxFolder>C:\\Input\\release</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
    <MappedFolder>
      <HostFolder>{tools}</HostFolder>
      <SandboxFolder>C:\\Tools</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
  </MappedFolders>
  <Networking>Disable</Networking>
  <ClipboardRedirection>Disable</ClipboardRedirection>
  <PrinterRedirection>Disable</PrinterRedirection>
  <AudioInput>Disable</AudioInput>
  <VideoInput>Disable</VideoInput>
  <MemoryInMB>4096</MemoryInMB>
  <LogonCommand>
    <Command>cmd.exe /d /c C:\\Tools\\sandbox-manual-runner.cmd {architecture}</Command>
  </LogonCommand>
</Configuration>
"""


def main() -> int:
    args = parse_args()
    release_input = args.release_root.expanduser().resolve()
    if release_input.is_file():
        mapping_root = release_input.parent
        selected_name = release_input.name.casefold()
        if selected_name == "lfportable-x64.exe":
            architecture = "x64"
        elif selected_name == "lfportable-arm64.exe":
            architecture = "arm64"
        else:
            architecture = args.architecture or "x64"
    else:
        mapping_root = release_input
        architecture = args.architecture
        if architecture is None:
            available = [name for name in ("x64", "arm64")
                         if (mapping_root / f"LFPortable-{name}.exe").is_file()]
            if len(available) == 1:
                architecture = available[0]
            elif (mapping_root / "CodexPortable.exe").is_file():
                architecture = "x64"
            else:
                raise ValueError("--architecture is required when both direct release EXEs are present")
    bootstrapper = mapping_root / "CodexPortable.exe"
    if not bootstrapper.is_file():
        bootstrapper = mapping_root / f"LFPortable-{architecture}.exe"
    tools_root = Path(__file__).resolve().parent
    runner = tools_root / "sandbox-manual-runner.cmd"

    if not bootstrapper.is_file():
        print(f"sandbox-smoke.py: release bootstrapper is missing: {bootstrapper}", file=sys.stderr)
        return 1
    if not runner.is_file():
        print(f"sandbox-smoke.py: Sandbox runner is missing: {runner}", file=sys.stderr)
        return 1
    if shutil.which("wslpath") is None or shutil.which("cmd.exe") is None:
        print("sandbox-smoke.py: this command must run from WSL", file=sys.stderr)
        return 1

    configuration: Path | None = None
    status = 0
    try:
        release_windows = windows_path(mapping_root)
        tools_windows = windows_path(tools_root)
        temp_root = windows_temp_directory()
        config_fd, config_name = tempfile.mkstemp(
            prefix="lf-portable-sandbox-", suffix=".wsb", dir=temp_root
        )
        os.close(config_fd)
        configuration = Path(config_name)
        configuration.write_text(
            build_configuration(release_windows, tools_windows, architecture),
            encoding="utf-8", newline="\r\n"
        )
        configuration_windows = windows_path(configuration)
        sandbox = windows_directory() / "System32" / "WindowsSandbox.exe"
        if not sandbox.is_file():
            raise ValueError(f"Windows Sandbox is unavailable: {sandbox}")

        print("Windows Sandbox is opening. Click Start Codex in the launcher, confirm the desktop, then close Sandbox.")
        subprocess.run([str(sandbox), configuration_windows], check=True)
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        print(f"sandbox-smoke.py: {error}", file=sys.stderr)
        status = 1
    finally:
        if configuration is not None:
            try:
                configuration.unlink(missing_ok=True)
            except OSError as error:
                print(f"sandbox-smoke.py: cannot remove temporary configuration: {error}", file=sys.stderr)
                status = 1
    return status


if __name__ == "__main__":
    raise SystemExit(main())
