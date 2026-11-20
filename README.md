# agent-memory-bank

A memory that your coding agents share, and that outlives the session that
wrote it. It is one SQLite file on your machine, served to any agent over MCP.

It keeps three things:

- **Notes.** Findings the code does not state: why a constant has the value it
  has, what a default silently does, what an earlier attempt got wrong. Every
  note must cite evidence (a `repo/path:line`, a commit, or a command someone
  can re-run), so the store does not fill up with guesses.
- **Handoffs.** A snapshot of where a session stopped: what was done, what is
  next, which files, and which git checkout. The next session, or a different
  agent, continues from it.
- **An index of your source.** The tracked files of the repositories you point
  it at, split into chunks and full-text searchable. It is there so that notes
  can cite real lines and be re-checked later.

Nothing leaves your machine. There is no sync, no telemetry and no network
call.

## Platform status

| Platform | Status |
|---|---|
| Windows | Tested. This is where it is used every day. |
| Linux, macOS | CI builds, tests and publishes it on Ubuntu and macOS on every push, then runs the published program. Nobody has used it day to day there yet, so the steps below are our best guess for those systems. They should work. We will test them later; if you get there first, please open an issue with what you find. |

## Set up

You need the [.NET SDK 10](https://dotnet.microsoft.com/download) and `git`.
Check with `dotnet --version` (it should print `10.0.` something) and
`git --version`.

### The short way: let an agent do it

Clone the repository:

```bash
git clone https://github.com/ContradNamiseb/agent-memory-bank.git
```

Then give your agent the path and this prompt:

> Set up agent-memory-bank for me. It is cloned at `<path>`. Follow the
> "Set up" section of its README step by step. Ask me which repositories to
> index, and tell me when I need to restart you.

The numbered steps below are what the agent will do. They are also the manual
route.

### 1. Build it

Run this from the clone's folder.

Windows:

```bash
powershell -ExecutionPolicy Bypass -File publish.ps1
```

Linux and macOS:

```bash
sh publish.sh
```

The script builds into `releases/<timestamp>/` and points a `publish` link at
that folder. The program is now:

- Windows: `publish/agent-memory-bank.exe`
- Linux and macOS: `publish/agent-memory-bank`

Every later step uses the **full path** to that file. The examples below write
it as `/path/to/agent-memory-bank/publish/agent-memory-bank`; on Windows use
your own path ending in `.exe`, with forward slashes
(`C:/path/to/agent-memory-bank/publish/agent-memory-bank.exe`).

Check it: `publish/agent-memory-bank help` prints the list of commands.

Two things to know:

- Leave the build inside the clone. The program finds its settings and its
  database by walking up to the folder that holds `AgentMemoryBank.slnx`.
- Do not start the server with `dotnet run`. It rebuilds on every launch, and
  an MCP client reads that delay as a server that failed to start.

### 2. Say which repositories to index

Copy `memory-bank.local.json.example` to `memory-bank.local.json` in the
clone's folder, and edit it:

```json
{
  "repoRoot": "C:/path/to/your/repos",
  "repos": ["first-repo", "second-repo"]
}
```

`repoRoot` is the folder that holds your repositories. `repos` are the names of
the folders inside it that you want indexed. Use forward slashes on Windows
too. The example file lists the other settings.

This file is gitignored. It is the only place your paths are written down.

### 3. Build the index

```bash
publish/agent-memory-bank index -v
publish/agent-memory-bank mine-git
publish/agent-memory-bank stats
```

- `index` reads the tracked files of each repository. It skips files that have
  not changed, so you can re-run it whenever you like.
- `mine-git` turns the commit messages that explain a decision into notes.
- `stats` should now list each repository with its file and chunk counts.

The database is `memory.db` in the clone's folder.

### 4. Connect your agents

The server is the same program, started as `agent-memory-bank serve`. Register
it in every agent client you use. They all read the same database, so a note
written from one agent is there when another one asks.

**Claude Code** (one command):

```bash
claude mcp add agent-memory-bank --scope user -- /path/to/agent-memory-bank/publish/agent-memory-bank serve
```

Then open `~/.claude.json`, find the `agent-memory-bank` entry under
`mcpServers`, and add `"alwaysLoad": true` to it. Without that, Claude Code
loads only the tool names at startup and has to look each tool up before it can
call it. `claude mcp list` shows whether the server connected.

**Most other clients** take this JSON shape:

```json
{
  "mcpServers": {
    "agent-memory-bank": {
      "command": "/path/to/agent-memory-bank/publish/agent-memory-bank",
      "args": ["serve"]
    }
  }
}
```

| Client | File | Notes |
|---|---|---|
| Antigravity | `~/.gemini/config/mcp_config.json` | as above |
| Cursor | `~/.cursor/mcp.json` | as above |
| GitHub Copilot CLI | `~/.copilot/mcp-config.json` | add `"type": "stdio"` and `"tools": ["*"]` to the entry |
| Kimi Code | `~/.kimi-code/mcp.json` | add `"transport": "stdio"` to the entry |
| VS Code | `.vscode/mcp.json`, or `mcp.json` in your user profile | the top-level key is `"servers"`, not `"mcpServers"` |

**Codex** (`~/.codex/config.toml`):

```toml
[mcp_servers.agent-memory-bank]
command = '/path/to/agent-memory-bank/publish/agent-memory-bank'
args = ["serve"]
```

**OpenCode** (`~/.config/opencode/opencode.jsonc`, under `"mcp"`):

```jsonc
"agent-memory-bank": {
  "type": "local",
  "command": ["/path/to/agent-memory-bank/publish/agent-memory-bank", "serve"],
  "enabled": true
}
```

Restart each client afterwards. They read their MCP configuration once, at
startup.

### 5. Tell your agents to use it

Registering the server makes the tools available. It does not make an agent
reach for them. Paste this into the instruction file your client reads
(`CLAUDE.md`, `AGENTS.md`, or its equivalent):

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

### 6. Claude Code only: make it automatic (optional)

Three hooks make the store work without anyone asking. Each one is a
subcommand of the same program, so they behave the same on every platform.

| Hook event | Command | What it does |
|---|---|---|
| `SessionStart` | `hook session-start` | Shows the newest handoff for this repository, so a new session opens knowing where the last one stopped. |
| `UserPromptSubmit` | `hook recall` | Finds identifiers in your prompt and adds any notes that match them exactly. |
| `PreCompact` | `hook handoff` | Saves a handoff from the session transcript before the context is compacted. |

Add this to `~/.claude/settings.json`. If the file already has a `hooks`
section, merge these entries into it:

```json
{
  "hooks": {
    "SessionStart": [
      { "hooks": [{ "type": "command",
                    "command": "/path/to/agent-memory-bank/publish/agent-memory-bank",
                    "args": ["hook", "session-start"] }] }
    ],
    "UserPromptSubmit": [
      { "hooks": [{ "type": "command",
                    "command": "/path/to/agent-memory-bank/publish/agent-memory-bank",
                    "args": ["hook", "recall"] }] }
    ],
    "PreCompact": [
      { "hooks": [{ "type": "command",
                    "command": "/path/to/agent-memory-bank/publish/agent-memory-bank",
                    "args": ["hook", "handoff"], "timeout": 120 }] }
    ]
  }
}
```

A hook that fails prints nothing and exits cleanly, so it can never block a
session. If a handoff you expected is missing, look in
`hook-state/handoff.log` beside the database.

### 7. Check that it works

Open a new session in one of your clients and ask:

> Call `memory_stats` and show me the result.

You should see the same counts that `stats` printed in step 3. If the agent
says it has no such tool, the client has not been restarted since step 4, or
the path in its configuration is wrong.

## Using it

Once the instructions from step 5 are in place, agents search the store before
they investigate and record what they learn. You can also ask directly:

- *"Search the memory bank for `policy_temperature`."*
- *"Record that in the memory bank."*
- *"Save a session handoff to the memory bank."* Say this before you switch
  agents or run out of usage.
- *"Read the latest handoff from the memory bank and continue."* Say this in
  the next agent.

A handoff remembers the branch, commit and worktree it was written in. Read
from a different checkout, it starts with a warning, because "done" in one
checkout can be false in another.

From a terminal, the same program answers the same questions (shown here
without the `publish/` path in front):

```bash
agent-memory-bank search "some_identifier"    # notes first, then source chunks
agent-memory-bank get note 12                 # one note in full
agent-memory-bank handoff latest              # where the last session stopped
agent-memory-bank verify                      # do the notes still match the code?
agent-memory-bank index                       # pick up changed files
agent-memory-bank help                        # every command and option
```

## Updating

```bash
git pull
```

Then run the build command from step 1 again. Servers that are already running
keep the old build until their session ends, and new sessions get the new one.
Nothing needs to be stopped. Do not kill the server by name (`taskkill /IM`,
`pkill`): some clients never restart a dead MCP server.

## The tools agents get

| Tool | What it does |
|---|---|
| `memory_search` | Searches notes and source chunks. Notes come first, and every hit carries a citation. |
| `memory_index` | Lists notes one line each, so an agent can pick ids before fetching bodies. |
| `memory_get` | Returns notes or chunks in full, with line numbers. |
| `memory_stats` | Says what the store holds, per repository. |
| `memory_notes_upsert` | The only way to write notes. Evidence is required and checked. |
| `memory_verify` | Re-checks that the code a note cites still says what the note says. |
| `memory_handoff_save` | Saves a session handoff, with the git checkout it was written in. |
| `memory_handoff_latest` | Returns the newest handoff for this repository. |
| `memory_handoff_list` | Lists past handoffs. |

## Why you can trust what it returns

- **Evidence is required.** A note with nothing behind it is refused, and the
  refusal says why and shows a correct example.
- **Nothing is deleted.** A note that turned out wrong is superseded. The old
  note and the reason it changed are kept, so the same mistake is not made
  twice.
- **Stale notes are found.** `verify` re-reads the cited code. If the line only
  moved, the note is quietly re-anchored. If it changed or is gone, the note is
  reported as `moved` or `broken`.
- **Empty means empty.** A search that finds nothing in a populated store means
  "not recorded", and the tools say when the store has not been indexed at all.

The reasoning behind these rules, and how indexing and verification work, is
in [docs/design.md](docs/design.md).

## Your data

These files hold your data. All of them are gitignored, so they cannot be
pushed by accident:

| Path | What it holds |
|---|---|
| `memory.db` | The store: your notes, your handoffs and the source index. |
| `memory-bank.local.json` | The names and paths of your repositories. |
| `hook-state/` | The hook's log and per-session markers. |
| `local/` | Scratch space for your own notes. |

**Do not put the database in OneDrive, Dropbox, iCloud or Google Drive.** A
live store is up to three files that must stay consistent with each other. A
sync client copies them one at a time and will eventually corrupt the store.

To back the store up or move it, close every agent client and copy
`memory.db`.

### A second, separate store

To keep another body of knowledge apart from your code (for example, writing
projects), register the server a second time under another name and give it
its own files:

```json
"memory-bank-writing": {
  "command": "/path/to/agent-memory-bank/publish/agent-memory-bank",
  "args": ["serve", "--db", "/path/to/writing.db",
           "--settings", "/path/to/writing.memory-bank.json"]
}
```

On the command line, the same two options (or the `MEMORY_BANK_DB` and
`MEMORY_BANK_SETTINGS` environment variables) select that store.

## What it deliberately does not do

- **No embeddings or vector search.** Keyword search is enough for a store
  this size, and a model would add a download, a runtime and version drift.
- **No code graph built by an LLM.** ctags and language servers already do
  that accurately.
- **No automatic file summaries.** Summaries go stale silently and then get
  cited as fact.
- **No sync, export or server mode.** The store is personal and local on
  purpose.

## Repository layout

```
src/AgentMemoryBank.Core/        the library: database, indexing, verification, handoffs
src/AgentMemoryBank.McpServer/   the program: command line, hooks, MCP server
test/AgentMemoryBank.Tests/      xUnit tests
publish.ps1, publish.sh          build without stopping running servers
docs/design.md                   why it works the way it does
ROADMAP.md                       what is planned
CHANGELOG.md                     what changed
```

It depends on three NuGet packages: `Microsoft.Data.Sqlite` and
`Microsoft.Extensions.Hosting` from Microsoft, and `ModelContextProtocol`, the
official MCP SDK for C#. SQLite ships prebuilt inside the first one, so there
is no native build step.

## Disclaimer

This project was built with Claude (Anthropic's Claude Code). The design and
the decisions are the maintainer's. Claude wrote the implementation against
that design, and the maintainer reviews it. That review is why the project is
in C#: the rule here is to depend on nothing that has not been read.

## License

MIT. See [LICENSE](LICENSE).
