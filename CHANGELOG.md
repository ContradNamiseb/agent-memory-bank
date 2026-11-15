# Changelog

All notable user-visible changes to `agent-memory-bank` are documented in this file.

## 0.0.2 (unreleased)

- **Repo-scoped search filters notes accurately:** `repo=` on `memory_search` now filters notes as well as chunks — keeping notes attributed to the requested repository and unattributed general notes (matching no known repository), while excluding notes belonging only to other known repositories.
- **Multi-repo attribution for shared git history:** `mine-git` attributes shared commits across configured repositories via lowercase repo tags without duplicating notes, backfills shared repo tags across branches/worktrees, and follows `superseded_by` chains to the live replacement note without re-mining superseded commits.
- **Native cross-platform Claude Code hooks:** Added `agent-memory-bank hook session-start` and `agent-memory-bank hook recall` subcommands; `hooks/memory-session-start.ps1` and `hooks/memory-recall.ps1` are now thin cross-platform wrappers shipped in `publish/hooks/`.
- **Stricter CLI error handling:** Usage errors and unknown commands write to `stderr` and exit with code `2` (`hook` subcommands fail silent with `0` bytes on `stdout`); `serve` now uses `CommandLine.Parse` and rejects unknown options.
- **Global `--settings` option:** `--settings <path>` (alongside `MEMORY_BANK_SETTINGS`) applies to every CLI command, not only `serve`.
- **Handoff context flag:** Added `handoff save --context <text>` (alias `--context-data`) to record session context payloads from the CLI.

## 0.0.1

- Initial C# / .NET 10 baseline (`58ed6b0`): SQLite + FTS5 schema, Phase A indexer and language chunkers, Phase B git commit miner, anchored verification/superseding note store, cross-agent session handoffs, and the 9-tool MCP stdio server.
