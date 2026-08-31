#!/usr/bin/env python3
"""Open a Windows Sandbox session for a manual LF Portable desktop smoke test."""

from __future__ import annotations

import argparse
import csv
from pathlib import Path
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
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


def windows_process_ids(image_name: str) -> set[int]:
    """Return Windows PIDs for an image, using stable CSV tasklist output."""
    result = subprocess.run(
        ["tasklist.exe", "/fi", f"IMAGENAME eq {image_name}", "/fo", "csv", "/nh"],
        check=False,
        text=True,
        capture_output=True,
        errors="replace",
    )
    process_ids: set[int] = set()
    for row in csv.reader(result.stdout.splitlines()):
        if len(row) < 2 or row[0].casefold() != image_name.casefold():
            continue
        try:
            process_ids.add(int(row[1]))
        except ValueError:
            continue
    return process_ids


def run_sandbox(sandbox: Path, configuration_windows: str) -> None:
    """Launch Sandbox and keep the .wsb alive until its VM session exits.

    WindowsSandbox.exe is only a client launcher and may return before the
    service has parsed the configuration.  The caller must therefore defer
    deleting the temporary .wsb until the newly-created server process exits.
    """
    existing_servers = windows_process_ids("WindowsSandboxServer.exe")
    command = [str(sandbox), configuration_windows]
    process = subprocess.Popen(command)
    try:
        deadline = time.monotonic() + 60
        new_servers: set[int] = set()
        while time.monotonic() < deadline:
            if process.poll() is not None and process.returncode not in (0, None):
                raise subprocess.CalledProcessError(process.returncode, command)
            new_servers = windows_process_ids("WindowsSandboxServer.exe") - existing_servers
            if new_servers:
                # Give the service a short interval to finish reading the file
                # before any cleanup can remove it.
                time.sleep(2)
                if not (windows_process_ids("WindowsSandboxServer.exe") & new_servers):
                    raise RuntimeError("Windows Sandbox exited during initialization")
                break
            time.sleep(0.25)
        if not new_servers:
            if existing_servers:
                raise RuntimeError("another Windows Sandbox session is already running")
            raise RuntimeError("Windows Sandbox did not start within 60 seconds")

        print("Windows Sandbox is open. Click Start Codex in the launcher, confirm that the desktop opens, then close Sandbox.")
        while windows_process_ids("WindowsSandboxServer.exe") & new_servers:
            time.sleep(1)
        process.wait(timeout=10)
    finally:
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)


def build_configuration(release_root: str, tools_root: str, architecture: str,
                        release_name: str | None = None) -> str:
    release = escape(release_root)
    tools = escape(tools_root)
    selected = ""
    if release_name is not None:
        if not re.fullmatch(r"[A-Za-z0-9._-]+", release_name):
            raise ValueError(f"unsafe release executable name: {release_name}")
        selected = f' "{release_name}"'
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
    <Command>cmd.exe /d /c C:\\Tools\\sandbox-manual-runner.cmd {architecture}{selected}</Command>
  </LogonCommand>
</Configuration>
"""


def main() -> int:
    args = parse_args()
    release_input = args.release_root.expanduser().resolve()
    explicit_bootstrapper: Path | None = None
    if release_input.is_file():
        mapping_root = release_input.parent
        explicit_bootstrapper = release_input
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
    bootstrapper = explicit_bootstrapper or (mapping_root / "CodexPortable.exe")
    if explicit_bootstrapper is None and not bootstrapper.is_file():
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
            build_configuration(release_windows, tools_windows, architecture,
                                explicit_bootstrapper.name if explicit_bootstrapper else None),
            encoding="utf-8", newline="\r\n"
        )
        configuration_windows = windows_path(configuration)
        sandbox = windows_directory() / "System32" / "WindowsSandbox.exe"
        if not sandbox.is_file():
            raise ValueError(f"Windows Sandbox is unavailable: {sandbox}")

        run_sandbox(sandbox, configuration_windows)
    except (OSError, ValueError, RuntimeError, subprocess.CalledProcessError,
            subprocess.TimeoutExpired) as error:
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
