# Design notes

Why agent-memory-bank works the way it does. The [README](../README.md) covers
setup and everyday use; nothing here is needed to install it.

## The problem it solves

Not context-window size. A few hundred source files already fit in a large
context, so retrieval to beat the limit is the weakest reason to build this.

The real loss is findings that die with the session. They have the same shape
every time:

- A constant that was *fitted* rather than chosen, where nothing records which
  measurement produced it or how to redo the fit, so the next person edits it
  by eye.
- A library function whose documented behaviour and actual behaviour differ,
  discovered once by reading the source.
- A tool whose destructive flag defaults to *on*, with the cleanup outside the
  error handler, so a failed run destroys its own inputs.
- A limit that silently discards work when exceeded, with no error and no log
  line, so the failure shows up much later as missing data.

These are decisions and hazards, not code. Grep cannot find them because they
are written down nowhere. That is what the store holds, and what an agent
otherwise re-derives, badly, at the start of every session.

## Notes

Notes are the product. The file index exists to ground them. Three rules hold
the quality line.

**Evidence is mandatory at write time.** A claim needs a `repo/path:line`
citation, a commit, or a command or measurement someone could re-run. A claim
with nothing behind it cannot be told apart from a guess once the session that
made it has ended.

**Refusals carry a worked example.** An opaque rejection is how an autonomous
caller gets stuck retrying the same malformed write. Every refusal returns a
reason code from a closed set (`missing_evidence`, `unresolvable_anchor`,
`duplicate_claim`, `claim_not_falsifiable`) plus a correct note to copy.

**Nothing is deleted.** A claim that turned out wrong is superseded, keeping
the original and the reason it changed. That is often the more useful fact,
because it stops the same wrong conclusion being reached twice.

Writing the same claim again is not an error. The same claim with the same
evidence is reported as unchanged; a changed one supersedes the old note.

Two smaller choices:

- **The trigger lives in the tool description.** Clients differ in whether
  they load repository instruction files, but every MCP client shows the model
  its tool descriptions. So the instruction to search before changing a
  measured constant is written there as well as in project instructions.
- **An empty result says which kind of empty it is.** A store that was never
  indexed answers every question with silence, and silence reads like evidence
  of absence. The tools distinguish "nothing recorded" from "never indexed".

## Search

Search is BM25 over two full-text tables, one for notes and one for source
chunks. Notes rank first. Each query term is matched as a phrase and the terms
are ANDed. When nothing matches every term, the search retries once matching
*any* term and labels the result `mode=Loosened`. A test pins that behaviour,
because the guidance given to agents depends on it: an empty result means the
retry already ran.

Every tool call passes through an argument guard. It maps the parameter names
models tend to reach for (`maxChars` to `max_chars`, `claim` to `claims`, `q`
to `query`), matches enum values ignoring case, and turns anything still wrong
into an error that carries the tool's signature. Errors on unknown ids, repos
and tags list the valid ones. Results are labelled text, not JSON.

## Staleness

A note describing code that has since changed is worse than no note. But
hashing the cited file is too blunt: adding three lines at the top changes the
hash while leaving the cited code untouched, and if every commit produces a
wave of false alarms, the alarms get ignored.

So `memory_verify` resolves cheapest-first, and only the last step surfaces:

1. Content hash unchanged: fine, no further work.
2. Hash changed, but the anchor line is found exactly once: silently re-anchor
   and update the stored line. This is the common case.
3. Anchor gone or ambiguous: fall back to the enclosing symbol. Still there,
   report `moved`. Gone too, report `broken`.

The anchor is the **cited line itself** wherever it can serve. Anchoring to the
explanatory comment above a constant would keep matching after someone changed
the constant, and verification would report the note as fine while the thing
it describes had moved. Only when the cited line cannot anchor (blank, trivial
like `}`, or repeated elsewhere) does the search widen to its neighbours.

Notes mined from commit messages are **unanchored by design**. A commit
message says which commit a change belongs to, never which line, so their
evidence is the commit itself (`git show <sha> -- <path>`) and `memory_verify`
reports them as not anchored. Inventing a `path:1` citation would pass the
evidence gate while anchoring every one of them to a licence header, and
verification would then call them healthy forever.

## Indexing

`index` runs `git ls-files` per repository and skips vendored trees and binary
files. It hashes each file, skips the unchanged ones, chunks by language, and
writes files, chunks and the full-text index in one transaction per
repository. Files that leave the repository take their chunks with them. A few
hundred files index in about a second.

Chunking is by language: C++ on function and class boundaries by brace
matching with strings and comments masked, Python on `def`/`class` by
indentation, Markdown on headings outside fenced code, textproto and proto on
blocks, YAML on top-level keys, and everything else by a non-overlapping line
window.

Every chunker test asserts the same invariant: a chunk's body is exactly the
lines it claims, and no line is claimed twice. A citation that points at the
wrong line is worse than no citation.

`mine-git` reads commit messages, drops the trivial ones, and records the rest
as notes with the commit as evidence. When configured repositories share git
history (branches or worktrees of one project), a shared commit becomes one
note tagged with each repository, not several copies.

## Handoffs

"Migration done" is true in one checkout and can be false in the next. So
`memory_handoff_save` records, from git and never from the agent: the
repository, worktree, branch, `HEAD` commit, how many paths were uncommitted,
and whether the branch's commits were pushed. The repository is identified by
`origin`'s URL (SSH and HTTPS forms match), so another clone of the same
project is the same repository. Without an `origin` it is the git directory
shared by that repository's worktrees.

`memory_handoff_latest` compares that with where it is being read, and puts
any difference before the handoff:

```text
WARNING: commit `4f41706` from this handoff does not exist in this checkout. The work it describes may not exist here at all: it was never pushed, or it was made in another clone.
WARNING: written on `feature-x` @ `4f41706` in `C:\...\wt-a`; you are on `main` @ `a1140d9` in `C:\...\lc0`. Verify before trusting 'done' claims.
When written, `C:\...\wt-a` had 1 uncommitted path. That work is in no commit, so it exists only in that working tree, if it is there at all.
```

A missing commit gets its own line because it means more than a moved `HEAD`:
the work may not exist here at all.

`latest` and `list` take a scope: `repo` (the default: any worktree of this
repository), `worktree` (this one only) or `all`. Handoffs saved before
checkouts were recorded have none, so they show only under `all`. The checkout
is read from the MCP server's directory unless the agent passes
`working_directory`, which it should when it works in a different worktree. On
the command line, use `--scope` and `--cwd`.

## Hooks

The three Claude Code hooks are subcommands of the program, so there is no
shell or script in between. Every hook exits with status `0` and prints
nothing on any failure or on a database that does not exist yet, and none of
them creates a database. A hook that breaks session start or compaction is
worse than no hook.

`hook recall` makes two choices on purpose:

- **It searches identifiers, not words.** Given `is policy_eval_temp still
  correct`, it searches `policy_eval_temp` alone.
- **It injects strict matches only.** When an agent asks, a loosened hit is
  useful: the store offers the nearest thing it holds. Nobody asked here, so a
  loose hit is only a note that shares a common word with the prompt, and
  injecting it would spend tokens on noise every turn.

`hook handoff` saves every time it runs. With `--mode threshold` it saves only
once the last request filled `--percent` (default 95) of the context window,
and at most every 15 minutes per session. That mode suits `UserPromptSubmit`.
The window is `autoCompactWindow` from Claude Code's `settings.json` (in
`CLAUDE_CONFIG_DIR`, else `~/.claude`), else 200,000 tokens. The checkout it
records is the session's folder from the hook input, else the last folder in
the transcript when that one no longer exists. Its log is
`hook-state/handoff.log` beside the database.

`hooks/` still holds two PowerShell wrappers from before the hooks moved into
the program. They forward to the same subcommands and are not needed.

## Configuration

Settings resolve in this order: an environment variable, then
`memory-bank.local.json`, then a harmless default.

| Variable | Setting |
|---|---|
| `MEMORY_BANK_SETTINGS` | Path of the settings file. |
| `MEMORY_BANK_DB` | Path of the database. Default: `memory.db` in the clone's folder. |
| `MEMORY_BANK_REPO_ROOT` | The folder that holds the repositories. |
| `MEMORY_BANK_REPOS` | Comma-separated repository names. |

`serve` and every command also accept `--db PATH` and `--settings PATH`,
written after the command name.

## Publishing without stopping servers

Every agent session runs its own copy of the server, and Windows will not
overwrite a program that is running. So `publish.ps1` and `publish.sh` build
into `releases/<timestamp>/` and repoint a `publish` link at the new folder (a
junction on Windows, a symlink elsewhere). A running server keeps the release
it started from, and every new launch gets the new one. Old releases are
removed only when nothing is using them. `publish.sh` checks that with `lsof`
and keeps every release when `lsof` is missing.

Publish on the machine you will run it from. The launcher is native to the
platform that built it, so a `publish/` folder copied from one operating
system to another will not start. To skip the launcher, run the assembly:
`dotnet /full/path/to/publish/agent-memory-bank.dll serve`.

## Everything stays on your machine

This is the point of the project, not a limitation of the current version.
There is no sync, no export, no server mode, no telemetry, and no call to any
remote service. Two reasons:

- **Privacy.** The index holds verbatim source from private repositories, and
  the notes hold decisions about that source.
- **The notes are personal.** A store reflects how one developer works and
  what they concluded. Merging two of them does not produce a better store. It
  produces one where you can no longer tell whose judgement you are reading.

The database runs in WAL mode, so a live store is up to three files
(`memory.db`, `memory.db-wal`, `memory.db-shm`) that must stay consistent with
each other. A sync client copies them independently and will eventually
capture a main file without its matching WAL. That corrupts the store, and the
corruption surfaces long after the sync that caused it.

## Deliberately not built

- **Embeddings and vector search.** BM25 over a corpus this size is
  sufficient. A model means a download, a runtime, and drift between index and
  model version. Revisit only with real queries that BM25 missed.
- **An LLM-extracted code graph.** ctags and LSP produce call and definition
  graphs accurately and for free. An inferred one is a slower, hallucinating
  version of that.
- **Auto-summarising every file.** Summaries go stale silently and then get
  cited as fact. Raw text stays the source of truth.
