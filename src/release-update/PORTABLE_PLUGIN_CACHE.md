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
On the first portable start, the launcher reconstructs the cache from the
offline marketplace under the same portable root and the matching signed
desktop package stored there. For the current desktop layout, x64 uses fifteen
plugins:

- `openai-bundled`: `sites`, `browser`, `chrome`, `computer-use`,
  `codex-app-tools`, `latex`, `deep-research`, `unified-computer-use`,
  `user-writing`, and `visualize`.
- `openai-primary-runtime`: `documents`, `pdf`, `presentations`,
  `spreadsheets`, and `template-creator`.

ARM64 uses the same set except for `latex`, which is not included in the
official ARM64 package, for a total of fourteen plugins.

If a stopped portable installation has a missing or outdated cache, start it
through `CodexPortable.exe` and let the launcher rebuild the derived entries.
The launcher leaves user configuration, secrets, sessions, logs, and unknown
cache entries in place. There is no separate repair script in the WSL-first
build and release workflow.

Release assembly is performed from WSL with `release.sh`; the resulting
architecture-specific EXEs are the GitHub assets. USB deployment and a real
Windows desktop observation remain Windows-only diagnostics because they need
the Windows volume, process, and GUI APIs. If no `CODEX_USB` volume is mounted,
skip that scenario; it never blocks assembly, delivery, or publishing. The
cache is never copied from one USB installation to another as a release input.
