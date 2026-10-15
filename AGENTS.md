# agent-memory-bank

Instructions for any coding agent working in this repository. Claude Code reads
`CLAUDE.md`; Codex, Cursor, Copilot and Antigravity read this file. The memory
bank section below is identical in both on purpose -- it is the one rule that
must hold whichever agent is driving, because they share one store.

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

## The rest

`CLAUDE.md` carries the working notes for this repository and `PLAN.md` carries
the design. Both apply here; read `PLAN.md` first.
