# agent-memory-bank

A local SQLite store of durable project knowledge, exposed to any agent over
MCP. Built in-house, in C# on .NET 10, so there is nothing to trust but code
that has actually been read.

Everything it learns stays on the machine that runs it. The index, the notes,
and the configuration naming your repositories are all untracked — this
repository is the tool, never its contents.

## The problem it solves

Not context-window size. A few hundred source files already fit in a large
context, and will for a while, so retrieval-to-beat-the-limit is the *weakest*
reason to build this.

The real loss is **findings that die with the session**. The shape of them is
always the same:

- A constant that was *fitted* rather than chosen, where nothing records which
  measurement produced it or how to redo the fit — so the next person edits it
  by eye.
- A library function whose documented behaviour and actual behaviour differ,
  discovered once by reading the source.
- A tool whose destructive flag defaults to *on*, with the cleanup outside the
  error handler, so a failed run destroys its own inputs.
- A limit that silently discards work when exceeded, with no error and no log
  line, so the failure shows up much later as missing data.

These are *decisions and hazards*, not code. Grep cannot find them because they
are written down nowhere. That is the thing worth storing — and the thing an
agent re-derives, badly, at the start of every session.

## Requirements

- **Windows.** That is the only platform this has been run on, and the only one
  it is currently supported on. Mac and Linux are intended and will come later;
  see below for what is already in place for them.
- .NET SDK 10 (developed against 10.0.302)
- git on the PATH
- Three NuGet dependencies, all Microsoft- or Anthropic-published:
  `Microsoft.Data.Sqlite` (bundles a prebuilt SQLite with FTS5, so there is no
  compiler and no native build step), `ModelContextProtocol` for the server,
  and `Microsoft.Extensions.Hosting` to host it

## Getting started

### 1. Point it at your repositories

Copy `memory-bank.local.json.example` to `memory-bank.local.json`. It is
gitignored, and it is the only place your paths live:

```json
{
  "repoRoot": "C:/path/to/your/repos",
  "repos": ["first-repo", "second-repo"]
}
```

Every setting can also come from the environment, which is handy for one-off
runs: `MEMORY_BANK_REPO_ROOT`, `MEMORY_BANK_REPOS`, `MEMORY_BANK_DB`,
`MEMORY_BANK_SETTINGS`.

### 2. Publish

Publish once, then point everything — your own shell and every MCP client — at
the produced executable. Launching the server through `dotnet run` instead makes
it rebuild on each start, which a client sees as a server that is slow or hung:

```bash
dotnet test
dotnet publish src/AgentMemoryBank.McpServer -c Release -o publish
```

That produces `publish/agent-memory-bank.exe` on Windows and
`publish/agent-memory-bank` on Linux and macOS. It is not on your PATH, so every
command below is written against the full path to it — substitute your own.

Publish on the machine you will run it from. The launcher is native to whatever
platform built it, so a `publish/` folder copied from one OS to another will not
start. To skip the launcher entirely, run the assembly directly:

```bash
dotnet /full/path/to/publish/agent-memory-bank.dll serve
```

**On Mac and Linux this is untested.** Nothing is known to be wrong with it —
`agent-memory-bank.dll` is IL rather than a Windows library, git is invoked off
the `PATH` the same way on every platform, and the native SQLite for linux-x64,
linux-arm64, osx-x64 and osx-arm64 already ships in `publish/runtimes/`. But
"should work" is not "does work", and nobody has run `dotnet test` on either
yet. If you try it, the results are worth an issue whichever way they go.

If you only want to try it out, you can skip this and go through the SDK
instead — same commands, with `--` separating dotnet's arguments from the
tool's:

```bash
dotnet run --project src/AgentMemoryBank.McpServer -- index -v
dotnet run --project src/AgentMemoryBank.McpServer -- search "some_identifier"
dotnet run --project src/AgentMemoryBank.McpServer -- stats
```

That is fine for a look around. Publish before wiring up a client, though:
`dotnet run` rebuilds on every launch, and an MCP client reads that delay as a
server that failed to start.

### 3. Build the index

```bash
cd publish

# Phase A: index tracked, non-vendored source files. Incremental by content hash.
./agent-memory-bank index -v
./agent-memory-bank index --full        # re-chunk everything

# Phase B: mine commit messages for historical rationale and decisions.
./agent-memory-bank mine-git -v

./agent-memory-bank stats
```

Re-run `index` whenever you want; it hashes first and skips what has not
changed, so a few hundred files take about a second.

Everything the server exposes over MCP also works from the shell, which is the
quickest way to confirm the store holds what you think it does:

```bash
# BM25 over notes and chunks; notes ranked first.
./agent-memory-bank search "some_identifier"
./agent-memory-bank search "checkpoint resume" --full

# Inspect one note or chunk in full, with line numbers.
./agent-memory-bank get note 1
./agent-memory-bank get chunk 1220

./agent-memory-bank handoff latest
```

### 4. Wire it into your agent

The server speaks MCP over stdio, so any client that can launch a command can
use it. Give each one the **absolute** path to the published executable and the
single argument `serve`.

The examples below use a Windows path. On Linux and macOS use the extensionless
`agent-memory-bank` instead, or set `"command": "dotnet"` with
`"args": ["/path/to/publish/agent-memory-bank.dll", "serve"]`.

#### Claude Code

One command, no file to edit. `--scope user` makes it available in every
project rather than only the current one:

```bash
claude mcp add memory-bank --scope user -- C:/path/to/publish/agent-memory-bank.exe serve
```

Verify it connected — the server should be listed, and its tools appear as
`memory_*`:

```bash
claude mcp list
```

Claude Code reads MCP configuration once at startup, so restart any session
that was already open before adding it.

#### Google Antigravity

Add to `~/.gemini/config/mcp_config.json`:

```json
{
  "mcpServers": {
    "memory-bank": {
      "command": "C:/path/to/publish/agent-memory-bank.exe",
      "args": ["serve"]
    }
  }
}
```

#### VS Code and GitHub Copilot

Create `.vscode/mcp.json` in your workspace, or add the same block to your user
settings to get it everywhere:

```json
{
  "servers": {
    "memory-bank": {
      "command": "C:/path/to/publish/agent-memory-bank.exe",
      "args": ["serve"]
    }
  }
}
```

#### Cursor and Windsurf

Add to `.cursor/mcp.json` or `.codeium/windsurf/mcp_config.json`:

```json
{
  "mcpServers": {
    "memory-bank": {
      "command": "C:/path/to/publish/agent-memory-bank.exe",
      "args": ["serve"]
    }
  }
}
```

Configuring more than one client is the point rather than a nuisance: they read
the same database, so a note written from Claude Code is there when Antigravity
next asks.

### 5. Tell the agent to use it

Registering the server makes the tools *available*. It does not make an agent
*reach for them*, and an agent that never asks is the same as an empty store.

Paste this at the top of your `CLAUDE.md`, `AGENTS.md`, or whatever instruction
file your client reads:

```markdown
## Memory bank first

Before investigating anything whose answer was *decided* or *measured* rather
than stated in the code -- config values, tuning constants, build flags, known
hazards, "why is it set this way" -- query the `agent-memory-bank` MCP server
first.

1. `memory_search` with **short keyword queries**: `wdl_spread`, not
   `what wdl spread should I use for conversion`. Each term is matched as a
   phrase and the terms are ANDed, so a keyword query is the precise one. A
   longer query is not thrown away -- when nothing matches every term the
   search retries with *any* term and labels the result `mode=Loosened` -- but
   that pass ranks partly by coincidence, so read it as the store offering its
   nearest thing rather than as an answer.
2. If it returns results, use them -- but verify anything they name still
   exists in the code before acting. Notes reflect what was true when written.
3. If it returns nothing, say so, then investigate normally. Empty means
   empty: the loosened retry has already run by that point, so there is no
   shorter phrasing left to try.
4. When you establish something durable that the code does not state -- why a
   value is what it is, what a default silently does, what an earlier attempt
   got wrong -- record it with `memory_notes_upsert` before you finish. Pass
   `dry_run=true` first if you are unsure the evidence will be accepted.

`memory_stats` confirms the index is populated. An empty result from a
populated store is a real "not recorded", not a broken connection.
```

### 6. Or skip the asking entirely (Claude Code)

An instruction competes with everything else in the prompt. Two hooks in
`hooks/` make recall happen whether or not the agent thinks to ask:

| hook | event | what it does |
|---|---|---|
| `memory-session-start.ps1` | `SessionStart` | Injects the most recent handoff, so a new session opens knowing where the last one stopped. |
| `memory-recall.ps1` | `UserPromptSubmit` | Pulls identifiers out of the prompt, searches, and injects any matching notes. |

Add them to `~/.claude/settings.json`:

```json
{
  "hooks": {
    "SessionStart": [
      { "hooks": [{ "type": "command", "command": "powershell",
                    "args": ["-NoProfile", "-File",
                             "C:/path/to/agent-memory-bank/hooks/memory-session-start.ps1"] }] }
    ],
    "UserPromptSubmit": [
      { "hooks": [{ "type": "command", "command": "powershell",
                    "args": ["-NoProfile", "-File",
                             "C:/path/to/agent-memory-bank/hooks/memory-recall.ps1"] }] }
    ]
  }
}
```

Both resolve the executable from `$env:AGENT_MEMORY_BANK_EXE`, falling back to
`../publish/agent-memory-bank.exe` relative to the script. Both exit silently on
any failure: a hook that breaks session start is worse than no hook.

Two deliberate choices in `memory-recall.ps1` are worth knowing, because both
were wrong in the first draft and testing caught them:

- **It searches identifiers, not words.** Given `is policy_eval_temp still
  correct`, it searches `policy_eval_temp` alone. Because the hook takes strict
  matches only (below), the loosened retry that rescues a wordy query in normal
  use does not apply here -- so handing it a sentence's worth of ordinary
  English really does guarantee a miss.
- **It injects strict matches only.** When you *ask*, a loosened any-term hit
  is useful -- the store offering the nearest thing it holds. Nobody asked
  here, so a loose hit is just a note sharing a common word with the prompt,
  and injecting it would spend tokens on noise every turn.

## The MCP tool surface

Nine tools, across search, durable notes, verification, and cross-agent
handoffs. Every search result carries a citation, and every write demands one:

| Tool | Category | Purpose |
| :--- | :--- | :--- |
| `memory_search(query, repo?, kind?, limit?, max_chars?)` | Search | BM25 over notes and chunks, notes first, every hit cited. Output capped (default 12000 chars); ids that did not fit are listed |
| `memory_index(repo?, kind?, tag?, include_superseded?, limit?, before_id?)` | Orientation | TSV manifest — id, kind, confidence, chars, tags, one-line claim — so an agent picks ids before fetching bodies |
| `memory_get(kind, ids, max_chars?)` | Inspection | One or several chunks or notes in full, with line numbers |
| `memory_stats()` | Inspection | What the store holds, and row counts per repo |
| `memory_notes_upsert(claims[], evidence[], kinds?[], confidences?[], rationales?[], tags?[], supersedes?[], dry_run?)` | Write | The only note write (replaced `memory_note_add` and `memory_note_supersede`). Evidence is required and checked; a claim already recorded does not fail: same claim + same evidence is *unchanged*, a changed one *supersedes* the old note. Never overwrites |
| `memory_verify(id?)` | Verification | Re-check that cited code still says what the note says |
| `memory_handoff_save(agent, topic, summary, next_steps, ...)` | Continuity | Save a session handoff before switching agents or hitting limits |
| `memory_handoff_latest(agent?)` | Continuity | Retrieve the latest handoff so an incoming agent resumes instantly |
| `memory_handoff_list(limit?, agent?)` | Continuity | List past session handoffs across all AI tools |

`kind` and `confidence` are enums in the schema. Every call passes through an
argument guard (`ToolArgumentGuard`, shared verbatim with AgoraMCP) that maps
the names models reach for (`claim` → `claims`, `message` → `body`,
`threadId` → `thread_id`), matches enum values ignoring case, and turns
anything still wrong into an error carrying the tool's signature. Errors on
unknown ids, repos and tags list the valid ones. Results are labelled text,
not JSON.

---

## Moving a task between agents

Usage limits and context limits both end sessions mid-task. The store is what
survives them:

1. **Before leaving your current agent (e.g. Antigravity)**:
   Say: *"Save a session handoff to memory bank with our progress and next steps."*
   The agent calls `memory_handoff_save` with what was finished, active files, and pending next steps.

2. **When opening your next agent (e.g. Claude Code or VS Code Chat)**:
   Say: *"Read the latest handoff from memory bank and continue the task."*
   The incoming agent calls `memory_handoff_latest` via MCP, reads the exact state, and picks up immediately with zero re-prompting.

3. **In Terminal**:
   You can also check active handoff status anytime:
   ```bash
   ./agent-memory-bank handoff latest
   ```

---

Notes are the product; the file index exists to ground them. Three rules hold
the quality line:

**Evidence is mandatory at write time.** A claim needs a `repo/path:line`
citation, or a command or measurement someone could re-run. This single
constraint is what stops the store filling with plausible-sounding sludge — a
claim with nothing behind it is indistinguishable from a guess once the session
that made it has ended.

**Refusals carry a worked example.** An opaque rejection is how an autonomous
caller gets stuck retrying the same malformed write. Every refusal returns a
reason code from a closed set (`missing_evidence`, `unresolvable_anchor`,
`duplicate_claim`, `claim_not_falsifiable`) plus a correct note to copy.

**Nothing is deleted.** A claim that turned out wrong is superseded, keeping the
original and the reason it changed — often the more useful fact, because it stops
the same wrong conclusion being reached twice.

Two more details that matter more than they look:

**The trigger lives in the tool description.** Nothing queries a memory bank
unless something tells it to, and clients differ in whether they load repository
instruction files — but every MCP client shows the model its tool descriptions.
So the instruction to search *before* changing a measured constant or trusting a
default is written there, not only in a project's own instructions.

**An empty result distinguishes "nothing recorded" from "never indexed."** A
store that has not been indexed answers every question with silence, and silence
reads like evidence of absence. The tools say which one it is.

## Staleness

A note describing code that has since changed is worse than no note. But hashing
the cited file is far too blunt: adding three lines at the top changes the hash
while leaving the cited code untouched, and if every commit produces a wave of
false alarms, the alarms get ignored — which is the same as not having them.

So `memory_verify` resolves cheapest-first, and only the last step surfaces:

1. Content hash unchanged — fine, no further work.
2. Hash changed, but the anchor line is found exactly once — silently re-anchor
   and update the stored line. This is the common case and is meant to be invisible.
3. Anchor gone or ambiguous — fall back to the enclosing symbol. Still there,
   report `moved`; gone too, report `broken`.

The anchor is the **cited line itself** wherever it can serve, and that
preference is the point rather than a tie-breaker. Anchoring to the explanatory
comment above a constant would keep matching after someone changed the constant,
and verification would report the note as fine while the thing it describes had
moved out from under it. Only when the cited line cannot anchor — blank, trivial
like `}`, or repeated elsewhere — does the search widen to its neighbours.

Notes mined from commit messages are **unanchored by design**. A commit
message says which commit a change belongs to, never which line, so their
evidence is the commit itself (`git show <sha> -- <path>`) and `memory_verify`
reports them as not anchored. Synthesising a `path:1` citation would pass the
evidence gate while anchoring every one of them to a licence header, and
verification would then call them healthy forever — including after the code
they describe was deleted.

## How Phase A works

`git ls-files` per repository, skipping vendored trees. Hash each file, skip
unchanged, chunk by language, write `file` + `chunk` + FTS in one transaction
per repository. Files that leave the repository take their chunks with them.
Always correct, and fast enough to rerun without thinking about it — a few
hundred files and a few thousand chunks index in about a second.

Chunking is by language: C++ on function and class boundaries by brace matching
with strings and comments masked, Python on `def`/`class` by indentation,
Markdown on headings outside fenced code, textproto and proto on blocks, YAML
on top-level keys, everything else by non-overlapping line window.

Every chunker test asserts the same invariant: **a chunk's body is exactly the
lines it claims, and no line is claimed twice**. A citation that points at the
wrong line is worse than no citation.

## Layout

```
src/AgentMemoryBank.Core/        class library
  Database/                      open, schema, FTS5 capability probe, BM25 search
  Models/                        Note, Chunk, SearchResult
  Indexing/                      git scanning, Phase A indexer, chunkers
  Verification/                  evidence anchors and staleness resolution
src/AgentMemoryBank.McpServer/   the executable: index / search / stats / serve
test/AgentMemoryBank.Tests/      xUnit
reference/prototype-js/          the JS spike this was ported from, kept for diffing
```

## Everything stays on your machine

This is the point of the project, not a limitation of the current version.

**The store never leaves the machine it was built on.** There is no sync, no
export, no server mode, no telemetry, and no call to any remote service. The
tool is public; the store it builds is not, and there is no code path that
would move it.

Two reasons, and the second is easier to lose sight of:

- **Privacy.** The index holds verbatim source from private repositories, and
  the notes hold decisions about that source.
- **The notes are personal.** A store reflects how one developer works and what
  they concluded. Merging two of them does not produce a better store — it
  produces one where you can no longer tell whose judgement you are reading.

By design, none of these are tracked:

| path | why |
|------|-----|
| `memory-bank.local.json` | names your repositories and their paths |
| `memory.db` | the index — verbatim source from private repositories |
| `local/` | scratch space for project-specific notes |

The split is enforced by `.gitignore` rather than by remembering.

### Do not put the database in a sync folder

OneDrive, Dropbox, iCloud and Google Drive all look like a harmless way to move
the store between your own machines. They are not. The database runs in WAL
mode, so a live store is up to three files that must stay mutually consistent —
`memory.db`, `memory.db-wal`, `memory.db-shm`. A sync client copies them
independently and on its own schedule, and will eventually capture a main file
without its matching WAL. That produces a corrupt database, and the corruption
surfaces long after the sync that caused it.

To move a store deliberately: close every client, then copy the file.

## Deliberately not built

- **Embeddings / vector search.** BM25 over a corpus this size is sufficient.
  Adding a model means a download, a runtime, and drift between index and model
  version. Revisit only when you can point at real queries BM25 missed.
- **An LLM-extracted code graph.** ctags and LSP produce call and definition
  graphs accurately and for free; an inferred one is a slower, hallucinating
  version of that. The LLM budget goes to prose rationale instead.
- **Auto-summarising every file.** Summaries go stale silently and then get
  cited as fact. Raw text stays the source of truth.

## Disclaimer

This project was built with **Claude** (Anthropic's Claude Code, Opus 5). The
design and the decisions behind it are the maintainer's; the implementation was
written by Claude against that design, and is reviewed by the maintainer.

That review is why the project is in C# rather than the JavaScript it was first
prototyped in: the rule here is to depend on nothing that has not been read, and
code the maintainer cannot review fails that rule just as a vendored dependency
would. Generated code is not exempt from it.
