import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));

/**
 * Check the capability, not the version string. node:sqlite
 * landed in 22.5.0 but spent time behind a flag, and a build can ship SQLite
 * without FTS5 — in which case the import succeeds and only the virtual table
 * fails. Both failures are caught here.
 */
export async function requireSqlite() {
  let DatabaseSync;
  try {
    ({ DatabaseSync } = await import('node:sqlite'));
    const probe = new DatabaseSync(':memory:');
    probe.exec('CREATE VIRTUAL TABLE t USING fts5(b)');
    probe.exec("INSERT INTO t VALUES ('probe')");
    probe.prepare('SELECT bm25(t) FROM t WHERE t MATCH ?').get('probe');
    probe.close();
  } catch (e) {
    throw new Error(
      `memory-bank needs node:sqlite with FTS5. Node ${process.versions.node} ` +
      `could not provide it (${e.message}). Use Node 24+, or 22.5-23.x with ` +
      `--experimental-sqlite.`);
  }
  return DatabaseSync;
}

/**
 * Open (creating if needed) the memory bank database, with the schema applied.
 * WAL plus a busy timeout so several editors can share one file (see
 * "Concurrency caveat"); writes are rare enough that no server process is needed.
 */
export async function openDb(dbPath) {
  const DatabaseSync = await requireSqlite();
  if (dbPath !== ':memory:') fs.mkdirSync(path.dirname(path.resolve(dbPath)), { recursive: true });
  const db = new DatabaseSync(dbPath);
  db.exec('PRAGMA journal_mode = WAL');
  db.exec('PRAGMA busy_timeout = 5000');
  db.exec('PRAGMA foreign_keys = ON');
  db.exec('PRAGMA synchronous = NORMAL');
  db.exec(fs.readFileSync(path.join(HERE, 'schema.sql'), 'utf8'));
  return db;
}

/** Record an ingestion run; returns its id, to be stamped on what it writes. */
export function startRun(db, { tool, gitRev = null, note = null }) {
  const { lastInsertRowid } = db
    .prepare('INSERT INTO run(started_at, tool, git_rev, note) VALUES (?,?,?,?)')
    .run(new Date().toISOString(), tool, gitRev, note);
  return Number(lastInsertRowid);
}
