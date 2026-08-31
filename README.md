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
- `dist/` contains only the four launcher binaries. It is not a runnable
  portable release and never contains the desktop payload, profile, keys,
  logs, or plugin cache.

## WSL-First Build And Packaging

Use WSL for source builds, release assembly, Git work, and GitHub upload. The
supported build requires Bash, Python 3, a .NET SDK, Mono's .NET Framework 4.8
reference assemblies, and standard GNU/binutils tools. On Debian or Ubuntu,
installing `mono-devel` and `binutils` supplies the reference assemblies and
PE inspection tools when they are not already present.

Build the launcher matrix from WSL:

```bash
src/portable-launcher/build-launcher.sh \
  --output-root ./dist
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
  --output-root /path/to/release-parent/release \
  --version 1.4.24.6
```

The command creates two direct, architecture-specific executables:
`LFPortable-x64.exe` and `LFPortable-arm64.exe`. Each EXE carries the common
runtime and exactly one official MSIX in its internal payload, so no
`CodexData` directory is present beside a fresh download. Use a new output
directory for each package attempt.

The retired Windows-script builder, release staging, package-manifest, and
publisher workflows are intentionally absent. They were replaced by the Bash
and Python entry points above. Product security behavior remains in the
launcher: official signed desktop packages, package identity, architecture,
and safe archive extraction are still validated at runtime. The offline
packages do not contain release descriptors, manifests, checkpoints, or
whole-tree digest records.

## Windows Manual Acceptance

Windows is still required for the two checks that need real Windows GUI and
device behavior. They are manual observations, not scripted release gates, and
they create no checkpoint, evidence directory, receipt, or result file.
When no volume labelled `CODEX_USB` is mounted, skip the USB deployment and
both USB launch observations; record that omission in the delivery notes, but
do not block the release. When the volume is mounted, the USB observations
remain required.

Run the Sandbox observation from WSL. It uses WSL interop to map the release
read-only with networking disabled, opens the launcher and Codex Desktop, and
keeps the desktop visible until it is closed:

```bash
src/release-update/sandbox-smoke.sh \
  --release-root /path/to/release-parent/release \
  --architecture x64
```

Before closing Sandbox, confirm that the LF launcher and Codex Desktop window
actually opened and that the first-run model announcement and `Try model` CTA
did not appear. Use `--architecture arm64` on an ARM64 host.

Deploy the same release to the real USB drive from WSL. The helper uses WSL
interop for the Windows volume and process APIs, and `robocopy` copies the
managed release files. It removes only the old expanded desktop/runtime,
package-owned offline-marketplace and plugin-cache catalogs discovered from the
common ZIP and matching MSIX, transaction staging, and retired descriptor paths
so the next start rebuilds them from the new offline package;
configuration, SQLite, secrets, sessions, logs, and unknown user files are
preserved.

```bash
src/release-update/sync-usb.sh \
  --source-root /path/to/release-parent/release \
  --architecture x64 \
  --usb-root "/mnt/<CODEX_USB-drive-letter>" \
  --execute
```

`--usb-root` must be the root of the drive labelled `CODEX_USB`. Then start
`CodexPortable.exe` from that drive in Windows and confirm that the desktop
opens. Before this observation, close task-started LF processes and remove any
task-created fixed-disk LF state or session cache; launch only the USB-root
bootstrapper and confirm the desktop executable runs below that same USB root.
Repeat this launch after task cleanup. With an independently installed
WindowsApps Codex Desktop already running, also confirm that the portable
instance starts alongside it and that
reopening the same portable root does not start a second portable instance.
Use the release directory matching the USB target host architecture.

WSL remains the workflow entry point, while its interop bridge invokes the
Windows process inspection, Windows Sandbox, and desktop interaction needed by
these two checks.

## Publish

After the required real Windows observations succeed for the exact executable,
publish from WSL with the GitHub CLI. When `CODEX_USB` is not mounted, the
Sandbox observation is the required GUI acceptance and the USB observation is
skipped as described above. A stable release has two program assets:
`LFPortable-x64.exe` and `LFPortable-arm64.exe`.

```bash
git add AGENTS.md README.md src/portable-launcher src/release-update dist
git commit -m "Release LF Portable 1.4.24.6"
git tag -a v1.4.24.6 -m "LF Portable 1.4.24.6"
git push origin HEAD:main refs/tags/v1.4.24.6:refs/tags/v1.4.24.6
src/release-update/publish-release.sh \
  --release-root /path/to/release-parent/release \
  --version 1.4.24.6
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
