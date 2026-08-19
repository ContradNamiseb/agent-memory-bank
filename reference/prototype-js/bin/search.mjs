#!/usr/bin/env node
/**
 * Milestone 1 acceptance check, and a usable grep-with-ranking in its own right:
 *
 *   node bin/search.mjs "retry_jitter"
 *   node bin/search.mjs "cache_pool_size" --repo first-repo
 *   node bin/search.mjs "queue drain timeout" --limit 5 --full
 */

import { parseArgs } from 'node:util';
import { openDb } from '../src/db.mjs';
import { search, getChunk } from '../src/search.mjs';
import { DB_PATH } from '../src/config.mjs';

const { values, positionals } = parseArgs({
  allowPositionals: true,
  options: {
    db: { type: 'string', default: DB_PATH },
    repo: { type: 'string' },
    kind: { type: 'string' },
    limit: { type: 'string', default: '10' },
    full: { type: 'boolean', default: false },
    json: { type: 'boolean', default: false },
    help: { type: 'boolean', short: 'h', default: false },
  },
});

const query = positionals.join(' ').trim();
if (!query || values.help) {
  console.log('usage: node bin/search.mjs QUERY [--repo NAME] [--kind K] [--limit N] [--full] [--json]');
  process.exit(query ? 0 : 1);
}

const db = await openDb(values.db);
const limit = Number(values.limit) || 10;
const res = search(db, { query, repo: values.repo ?? null, kind: values.kind ?? null, limit });

if (values.json) {
  console.log(JSON.stringify(res, null, 2));
  db.close();
  process.exit(0);
}

if (res.notes.length) {
  console.log(`notes (${res.notes.length}):`);
  for (const n of res.notes) {
    console.log(`\n  [note ${n.id}] ${n.kind ?? '-'} / ${n.confidence ?? '-'}  score ${n.score.toFixed(2)}`);
    console.log(`  ${n.claim}`);
    if (n.evidence) console.log(`  evidence: ${n.evidence}`);
    if (n.verified_at) console.log(`  verified: ${n.verified_at}`);
  }
  console.log('');
}

console.log(`chunks (${res.chunks.length}):`);
for (const c of res.chunks) {
  const sym = c.symbol ? `  ${c.symbol}` : '';
  console.log(`\n  [chunk ${c.id}] ${c.citation}-${c.line_end}  ${c.kind}${sym}  score ${c.score.toFixed(2)}`);
  if (values.full) {
    const full = getChunk(db, c.id);
    console.log(full.body.split('\n')
      .map((l, i) => `    ${String(full.line_start + i).padStart(5)}| ${l}`).join('\n'));
  } else {
    console.log(`    ${c.snippet.replace(/\s+/g, ' ').trim()}`);
  }
}
if (res.chunks.length === 0 && res.notes.length === 0) console.log('  (no matches)');
db.close();
