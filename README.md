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

## Status

| # | Milestone | State |
|---|-----------|-------|
| 1 | Schema + Phase A deterministic indexer | **done** |
| 2 | MCP server, search/get only | **done** |
| 3 | Notes + write path, evidence enforced | **done** |
| 4 | Phase B git-history mining | next |
| 5 | Additional MCP clients | not started |
| 6 | Phase C LLM extraction | not started |

Milestone 3 is the real test: if hand-written notes do not earn their keep,
automated ones will not either.

## Requirements

- .NET SDK 10 (developed against 10.0.302)
- git on the PATH
- Three NuGet dependencies, all Microsoft- or Anthropic-published:
  `Microsoft.Data.Sqlite` (bundles a prebuilt SQLite with FTS5, so there is no
  compiler and no native build step), `ModelContextProtocol` for the server,
  and `Microsoft.Extensions.Hosting` to host it

## Getting started

Point it at some repositories. Copy `memory-bank.local.json.example` to
`memory-bank.local.json` — gitignored, and the only place your paths live:

```json
{
  "repoRoot": "C:/path/to/your/repos",
  "repos": ["first-repo", "second-repo"]
}
```

Then:

```bash
dotnet build
dotnet test

# Phase A: index tracked, non-vendored source files. Incremental by content hash.
dotnet run --project src/AgentMemoryBank.McpServer -- index -v
dotnet run --project src/AgentMemoryBank.McpServer -- index --full   # re-chunk everything

# BM25 over notes and chunks; notes ranked first.
dotnet run --project src/AgentMemoryBank.McpServer -- search "some_identifier"
dotnet run --project src/AgentMemoryBank.McpServer -- search "retry backoff" --full
dotnet run --project src/AgentMemoryBank.McpServer -- stats
```

Every setting can also come from the environment, which is handy for one-off
runs: `MEMORY_BANK_REPO_ROOT`, `MEMORY_BANK_REPOS`, `MEMORY_BANK_DB`,
`MEMORY_BANK_SETTINGS`.

## Using it as an MCP server

Publish once, then point clients at the produced executable rather than at
`dotnet run`, which would rebuild on every launch:

```bash
dotnet publish src/AgentMemoryBank.McpServer -c Release -o publish
```

Register it with Claude Code:

```bash
claude mcp add memory-bank --scope user -- /full/path/to/publish/agent-memory-bank.exe serve
```

Or, for VS Code, a `.vscode/mcp.json` in the workspace:

```json
{ "servers": { "memory-bank": {
    "command": "/full/path/to/publish/agent-memory-bank.exe",
    "args": ["serve"] } } }
```

Three read-only tools are exposed. Nothing in this milestone can write to the
store, so a misbehaving client cannot corrupt it.

| tool | purpose |
|------|---------|
| `memory_search(query, repo?, kind?, limit?)` | BM25 over notes and chunks, notes first, every hit cited |
| `memory_get(kind, id)` | One chunk or note in full, with line numbers |
| `memory_stats()` | What the store holds, and whether it has been indexed at all |
| `memory_note_add(claim, evidence, ...)` | Record a durable finding. Evidence is required and checked |
| `memory_note_supersede(id, claim, evidence, why)` | Correct a note without deleting the record of being wrong |
| `memory_verify(id?)` | Re-check that cited code still says what the note says |

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
  Verification/                  evidence anchors — arrives with Milestone 3
src/AgentMemoryBank.McpServer/   the executable: index / search / stats / serve
test/AgentMemoryBank.Tests/      xUnit
reference/prototype-js/          the JS spike this was ported from, kept for diffing
```

## What stays local

By design, none of these are tracked:

| path | why |
|------|-----|
| `memory-bank.local.json` | names your repositories and their paths |
| `memory.db` | the index — verbatim source from private repositories |
| `local/` | scratch space for project-specific notes |

The store is *about* private code even when the tool is public, so the split is
enforced by `.gitignore` rather than by remembering.

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
