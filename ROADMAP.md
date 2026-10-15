# Roadmap

## The constraint that defines this project

**The store never leaves the machine it was built on.** Not to a server, not to
a sync folder, not to a teammate. This is the product, not a limitation of the
current version.

Two reasons, and the second is the one that is easy to lose sight of:

1. **Privacy.** The index holds verbatim source from private repositories, and
   the notes hold decisions about it. Nothing about that should be recoverable
   from anywhere but the machine that made it.
2. **The notes are personal.** A store reflects how one developer works and what
   they decided — their hazards, their fitted values, their reasons. Merging two
   people's stores does not produce a better store; it produces one where you
   can no longer tell whose judgement you are reading. Every developer's store
   being unique to them is the point.

A future 2.0 may support teams. That is a different product with a different
threat model, and nothing in 1.0 should be shaped around it in advance.

### What this rules out

Not open questions — settled, and settled deliberately:

- No sync, export, import, or merge between stores.
- No server mode, no hosted database, no network listener.
- No telemetry, and no call to any remote service, including model APIs.
- **No putting the database in OneDrive, Dropbox, iCloud or Google Drive.** This
  one looks like the harmless exception and is not. The store runs in WAL mode,
  so a live database is up to three files that must stay mutually consistent;
  a sync client copies them independently, on its own schedule, and will
  eventually capture a main file without its matching WAL. The result is a
  corrupt store, and the corruption surfaces long after the sync that caused it.
  Moving the file between machines means closing every connection and copying it
  deliberately.

If a change would make the data easier to move somewhere else, it is not an
improvement to this project.

## Already done, do not rebuild

| Concern | State |
|---|---|
| Configuration portability | `Config` resolves from environment variable, then an untracked local file, then a harmless default. Nothing names a real project. |
| Several agents, one machine | WAL, `busy_timeout = 5000`, pooling off, one connection per MCP call. Claude Code, Antigravity, Copilot and Cursor already share the file safely. |
| Keeping private data out of the remote | `.gitignore` covers `memory.db`, `memory-bank.local.json`, `local/`, and the agent-instruction documents that name real repositories. |
| Schema evolution | `Migrations.Apply` runs on every open, keyed on `PRAGMA user_version` but verified against the catalogue. v1 stamps the floor; the column-adding primitive is tested and ready. |

## Next, in order

Nothing here is blocked on a decision.

### 1. Make the guidance impossible to get wrong

The store is only as good as the querying habits around it, and those live in
prose across four files that have already drifted once.

- [ ] Keep `MemoryBankBenchmarkTests` and the tool description in step. The
      loosened-retry promise is now asserted; the rest of the guidance is not.
- [ ] Consider generating the agent-facing instructions from one source, so
      `README.md`, `AGENTS.md`, `CLAUDE.md` and the MCP tool description cannot
      disagree again.

### 2. Anyone can install it — on their own machine

Single-machine does not mean single-platform.

- [ ] Run the suite on Linux and macOS. Native SQLite for all four runtimes
      already ships in `publish/runtimes/`, so this is likely documentation and
      a CI job rather than code — but "likely" is doing real work there and
      nobody has checked.
- [ ] Port the two PowerShell hooks, or document a shell equivalent.
- [ ] CI on all three platforms.
- [ ] A first-run path that assumes nothing about the reader's machine.

### 3. Durability of the one copy that exists

The flip side of never syncing: there is exactly one copy, and no other machine
has it.

- [ ] A documented backup story — an explicit, offline, closed-database copy.
      `.gitignore` protects the store from leaving; nothing currently protects
      the user from losing it.
- [ ] Decide whether the tool should refuse to open a database sitting inside a
      known sync folder, or merely warn. Silence is the wrong answer either way.

## Deliberately not built

The list in `README.md` stands — no embeddings, no LLM-extracted code graph, no
auto-summarising. Add to it:

- **Access control inside the store.** One machine, one user, one store. If a
  note should not be readable by whoever is at the keyboard, it belongs in a
  different store, not behind a permissions model.
