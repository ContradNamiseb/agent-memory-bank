#!/usr/bin/env node
/**
 * Phase A indexer CLI.
 *
 *   node bin/reindex.mjs                  incremental, all configured repos
 *   node bin/reindex.mjs --repo second-repo
 *   node bin/reindex.mjs --full           re-chunk even unchanged files
 *   node bin/reindex.mjs --db ./scratch.db --note "after chunker change"
 */

import { parseArgs } from 'node:util';
import { openDb } from '../src/db.mjs';
import { reindex, optimizeFts } from '../src/indexer.mjs';
import { stats } from '../src/search.mjs';
import { DB_PATH, REPO_ROOT, REPOS } from '../src/config.mjs';

const { values } = parseArgs({
  options: {
    db: { type: 'string', default: DB_PATH },
    root: { type: 'string', default: REPO_ROOT },
    repo: { type: 'string', multiple: true },
    full: { type: 'boolean', default: false },
    note: { type: 'string' },
    verbose: { type: 'boolean', short: 'v', default: false },
    help: { type: 'boolean', short: 'h', default: false },
  },
});

if (values.help) {
  console.log(`usage: node bin/reindex.mjs [--db PATH] [--root DIR] [--repo NAME]... [--full] [--note TEXT] [-v]

Indexes tracked, non-vendored source files into ${DB_PATH}.
Repos: ${REPOS.join(', ')} under ${REPO_ROOT}`);
  process.exit(0);
}

const db = await openDb(values.db);
const t0 = Date.now();
const { runId, results } = reindex(db, {
  repoRoot: values.root,
  repos: values.repo?.length ? values.repo : REPOS,
  full: values.full,
  note: values.note ?? null,
});
if (values.full) optimizeFts(db);

let failed = false;
for (const r of results) {
  if (r.error) {
    console.error(`  ${r.repo}: ERROR ${r.error}`);
    failed = true;
    continue;
  }
  console.log(
    `  ${r.repo.padEnd(26)} indexed ${String(r.indexed).padStart(4)}  ` +
    `unchanged ${String(r.skipped).padStart(4)}  removed ${String(r.removed).padStart(3)}  ` +
    `chunks ${String(r.chunks).padStart(5)}  @${(r.git_rev ?? 'none').slice(0, 8)}`);
  for (const e of r.errors) console.error(`      skipped ${e}`);
}

const s = stats(db);
console.log(`\nrun ${runId} in ${((Date.now() - t0) / 1000).toFixed(1)}s -> ${values.db}`);
console.log(`total: ${s.files} files, ${s.chunks} chunks, ${s.notes} notes`);
if (values.verbose) {
  for (const p of s.per_repo) console.log(`  ${p.repo.padEnd(26)} ${p.files} files, ${p.chunks} chunks`);
}
db.close();
process.exit(failed ? 1 : 0);
