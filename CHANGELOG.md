# Changelog

All notable user-visible changes to `agent-memory-bank` are documented in this file.

## Unreleased

- **Automatic handoff hook in the binary:** `agent-memory-bank hook handoff` (for `PreCompact`) saves a handoff from the Claude Code session transcript: the recent requests, where the session had got to, the files it edited, and the git checkout of the session's folder (the hook input's `cwd`, else the transcript's last folder when that one no longer exists). `--mode threshold --percent N` saves only at N% of `autoCompactWindow`, at most every 15 minutes per session. It replaces a PowerShell script, so all three Claude Code hooks now run without PowerShell on Windows, Linux and macOS. Its log and per-session stamps are in `hook-state/` beside the database.
- **`publish.sh` for Linux and macOS:** the counterpart of `publish.ps1`, publishing into `releases/<timestamp>/` behind a `publish` symlink so running servers are never killed. It prunes old releases only when `lsof` shows nothing using them.
- **CI publishes on every platform:** the test workflow now also runs `publish.sh` (Ubuntu, macOS) or `publish.ps1` (Windows) twice, checks the link and pruning, and runs the published binary and a hook.

## 0.0.2

- **Checkout-aware session handoffs and warnings:** `memory_handoff_save` and `handoff save` record the current git repository, worktree, branch, `HEAD` commit, dirty path count, and unpushed state (migrating existing stores to schema `v2` on first open). `memory_handoff_latest`, `memory_handoff_list`, `handoff latest`/`list`, and the `SessionStart` hook scope to the current repository by default (`--scope repo|worktree|all`, `--cwd DIR`, `working_directory`), leading with `WARNING:` lines and a `Written:` header when read from another branch, commit, worktree, or clone.
- **Repo-scoped search filters notes accurately:** `repo=` on `memory_search` now filters notes as well as chunks — keeping notes attributed to the requested repository and unattributed general notes (matching no known repository), while excluding notes belonging only to other known repositories.
- **Multi-repo attribution for shared git history:** `mine-git` attributes shared commits across configured repositories via lowercase repo tags without duplicating notes, backfills shared repo tags across branches/worktrees, and follows `superseded_by` chains to the live replacement note without re-mining superseded commits.
- **Native cross-platform Claude Code hooks:** Added `agent-memory-bank hook session-start` (reading hook stdin JSON `cwd` for checkout comparison) and `agent-memory-bank hook recall` subcommands; `hooks/memory-session-start.ps1` and `hooks/memory-recall.ps1` are now thin cross-platform wrappers shipped in `publish/hooks/`.
- **Stricter CLI error handling:** Usage errors and unknown commands write to `stderr` and exit with code `2` (`hook` subcommands fail silent with `0` bytes on `stdout`); `serve` now uses `CommandLine.Parse` and rejects unknown options.
- **Global `--settings` option:** `--settings <path>` (alongside `MEMORY_BANK_SETTINGS`) applies to every CLI command, not only `serve`.
- **Handoff context flag:** Added `handoff save --context <text>` (alias `--context-data`) to record session context payloads from the CLI.

## 0.0.1

- Initial C# / .NET 10 baseline (`58ed6b0`): SQLite + FTS5 schema, Phase A indexer and language chunkers, Phase B git commit miner, anchored verification/superseding note store, cross-agent session handoffs, and the 9-tool MCP stdio server.
