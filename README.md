# LF Portable - Codex Desktop

LF Portable is a single-file Windows launcher for Codex Desktop. The published
EXE carries the compact release inputs internally; on first start it creates
`CodexData` beside itself and keeps packages, runtimes, configuration, SQLite
state, keys, profile data, and recovery inputs there. A disposable fixed-disk
session cache is optional and never a startup prerequisite.

## Repository Layout

- `src/portable-launcher/` contains the x86 bootstrapper, x86/x64/ARM64
  launcher sources, icons, and the WSL build entry point.
- `src/release-update/` contains the WSL package entry point and the
  WSL-to-Windows bridge used for USB deployment and Windows Sandbox.
- `dist/` is the local build and runnable delivery output. Its root contains
  only the assembled default x64 `CodexPortable.exe`; the architecture launcher
  inputs live below `CodexData/tools/launchers/`.
- `build/CodexPortable.bootstrapper.exe` is the bare x86 assembly input. It is
  kept outside `dist/` so the runnable root never contains a second EXE.

## WSL-First Build And Packaging

Use WSL for source builds, release assembly, Git work, and GitHub upload. The
supported build requires Bash, Python 3, a .NET SDK, Mono's .NET Framework 4.8
reference assemblies, and standard GNU/binutils tools. On Debian or Ubuntu,
installing `mono-devel` and `binutils` supplies the reference assemblies and
PE inspection tools when they are not already present. Windows-side Scoop or
Chocolatey packages do not substitute for the WSL Mono reference assemblies;
keep the build toolchain inside WSL.

Build the launcher matrix from WSL:

```bash
src/portable-launcher/build-launcher.sh \
  --output-root ./dist \
  --bootstrapper-output ./build/CodexPortable.bootstrapper.exe
```

Use a prepared, non-USB base root to assemble a release. It supplies the
portable documentation, notices, compact common runtime ZIP, and the official
x64 and ARM64 MSIX files. It must not include expanded desktop payloads, user
profiles, credentials, logs, or a derived plugin cache. When Microsoft Store
does not expose an offline MSIX directly, `store.rg-adguard.net` may only be
used to resolve the link; the downloaded file must come from Microsoft's
delivery domain and pass the launcher's signature, identity, and architecture
checks.

```bash
src/release-update/release.sh \
  --base-root /path/to/portable-base \
  --launcher-root ./dist \
  --bootstrapper ./build/CodexPortable.bootstrapper.exe \
  --output-root /path/to/release-parent/release \
  --version 1.4.24.8
```

The command creates two direct, architecture-specific executables:
`LFPortable-x64.exe` and `LFPortable-arm64.exe`. Each EXE carries the common
runtime and exactly one official MSIX in its internal payload, so no
`CodexData` directory is present beside a fresh download. Use a new output
directory for each package attempt.

The retired Windows-script builder and legacy staging/package-metadata
workflows are intentionally absent. Bash and Python entry points above perform
release assembly and publishing. Product security behavior remains in the
launcher: official signed desktop packages, package identity, architecture,
and safe archive extraction are still validated at runtime. The release adds no
custom descriptor, checkpoint, receipt, or whole-tree digest record; the
official MSIX keeps its platform-required `AppxManifest.xml`.

## Windows Reproduction And Troubleshooting

Use real Windows GUI and device behavior when reproducing Windows-specific
launcher problems. These observations are diagnostic aids, not completion,
approval, or release prerequisites, and they create no checkpoint, evidence
directory, receipt, or result file. When no volume labelled `CODEX_USB` is
mounted, ignore the USB scenario; it must not block delivery or publishing.

Run the Sandbox observation from WSL. It uses WSL interop to map the release
read-only with networking disabled, opens the launcher and Codex Desktop, and
keeps the desktop visible until it is closed:

```bash
src/release-update/sandbox-smoke.sh \
  --release-root /path/to/release-parent/release \
  --architecture x64
```

The smoke helper keeps its temporary `.wsb` configuration until the Sandbox
session has ended. Do not manually remove that file while `WindowsSandbox.exe`
or its service is starting; an early deletion can produce a misleading
`0x80070002` initialization error.

When investigating a Sandbox startup problem, observe whether the LF launcher
and Codex Desktop window open and whether the first-run model announcement or
`Try model` CTA appears. Use `--architecture arm64` on an ARM64 host.

Deploy the architecture EXE directly to the real USB drive from WSL. The
helper uses WSL interop for the Windows volume and process APIs and replaces
only the root `CodexPortable.exe`; a structured release directory is accepted
for legacy/migration use. It removes only the old expanded desktop/runtime,
package-owned offline-marketplace and plugin-cache catalogs discovered from the
common ZIP and matching MSIX, transaction staging, and retired descriptor paths
so the next start rebuilds them from the new offline package;
configuration, SQLite, secrets, sessions, logs, and unknown user files are
preserved.

```bash
src/release-update/sync-usb.sh \
  --source-root /path/to/release/LFPortable-x64.exe \
  --usb-root "/mnt/<CODEX_USB-drive-letter>" \
  --execute
```

`--usb-root` must be the root of the drive labelled `CODEX_USB`. When
reproducing a USB-only problem, close task-started LF processes and remove any
task-created fixed-disk LF state or session cache, then launch only the USB-root
`CodexPortable.exe` and observe whether its desktop executable runs below that
same USB root. An independently installed WindowsApps Codex Desktop may remain
running to reproduce coexistence behavior, and reopening the same portable root
should not start a second portable instance. Use the EXE matching the USB
target host architecture. These observations are diagnostic and do not block
building, delivery, or publishing.

`build/CodexPortable.bootstrapper.exe` is the bare x86 bootstrapper used while
assembling a release and has no embedded payload. It is not a runnable release,
and it stays outside `dist/`; never rename or pass it to the USB synchronizer.
Use the self-extracting `LFPortable-x64.exe` or `LFPortable-arm64.exe` output.
The build script keeps this input separate so a launcher rebuild cannot
overwrite a runnable `dist/CodexPortable.exe` or leave a second root EXE.
The synchronizer and publisher reject a direct executable that lacks the
embedded release ZIP before they touch the USB or invoke `gh`.
That rejection is an input correction, not a runnable result: finish assembly
from the prepared base root and use the resulting architecture EXE for the
USB or release.

WSL remains the workflow entry point, while its interop bridge invokes the
Windows process inspection, Windows Sandbox, and desktop interaction needed by
these two checks.

## Publish

Publish from WSL with the GitHub CLI. A stable release has two program assets:
`LFPortable-x64.exe` and `LFPortable-arm64.exe`.

Publishing is part of the same task as a verified delivery change. After the
build and runtime checks pass, continue through version bump, commit, tag,
push, release upload, and a remote check of the tag, Release, and both assets;
do not stop at a successful local `dist/` build. Use the current repository and
the explicitly selected release inputs. A rejected bare bootstrapper is an
input correction, not a completed release. This workflow does not add custom
checkpoints, receipts, hashes, or manifest-comparison files.

```bash
git add AGENTS.md README.md src/portable-launcher src/release-update dist
git commit -m "Release LF Portable 1.4.24.8"
git tag -a v1.4.24.8 -m "LF Portable 1.4.24.8"
git push origin HEAD:main refs/tags/v1.4.24.8:refs/tags/v1.4.24.8
src/release-update/publish-release.sh \
  --release-root /path/to/release-parent/release \
  --version 1.4.24.8
gh release view v1.4.24.8 --json tagName,name,assets
```

Do not add a complete desktop payload, portable user data, logs, screenshots,
remote-control records, USB backups, credentials, or machine-specific paths to
the repository or the release archive.

## Portable Behavior

The launcher initializes a custom API configuration and stores its mutable
portable state below `CodexData/data`. It starts one portable Codex instance per
portable root while allowing an independently installed official Codex Desktop
to run in parallel. More end-user details are included in
`src/release-update/CodexData-README.txt`. The bundled runtime and mainland CDN
decision are documented in `src/release-update/DEPENDENCY-AUDIT.md`.
