# Portable Plugin Cache

The portable desktop expects each bundled plugin below its catalog, plugin id,
and exact version from `.codex-plugin/plugin.json`:

```text
CodexData/data/profile/.codex/plugins/cache/
  openai-bundled/<plugin>/<version>/...
  openai-primary-runtime/<plugin>/<version>/...
```

Do not copy plugin contents directly into a catalog directory or create a
`latest` alias. A cache with that shape is not usable by Codex Desktop.

The compact common runtime ZIP intentionally contains no derived plugin cache.
Host preparation copies the read-only marketplace source and bundled
marketplace from the verified release packages into the machine-local
execution image, then the first explicit portable start reconstructs the
mutable cache in the portable data root. Later starts use those fixed-disk
sources and do not need package files from the USB. For the current desktop
layout, x64 uses thirteen plugins:

- `openai-bundled`: `sites`, `browser`, `chrome`, `computer-use`,
  `codex-app-tools`, `latex`, `deep-research`, and `visualize`.
- `openai-primary-runtime`: `documents`, `pdf`, `presentations`,
  `spreadsheets`, and `template-creator`.

ARM64 uses the same set except for `latex`, which is not included in the
official ARM64 package.

If a stopped portable installation has a missing or outdated cache, start it
through `CodexPortable.exe` and let the launcher rebuild the derived entries.
The launcher leaves user configuration, secrets, sessions, logs, and unknown
cache entries in place. There is no separate repair script in the WSL-first
build and release workflow.

Release assembly is performed from WSL with `release.sh`; host preparation and
USB deployment use the Windows fixed-disk/process APIs, and the actual desktop
check remains a Windows GUI operation. The cache is never copied from one USB
installation to another as a release input.
