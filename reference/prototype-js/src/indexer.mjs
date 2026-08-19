/**
 * Phase A ingestion: deterministic, no LLM (deterministic, no LLM).
 *
 * `git ls-files` per repo skipping vendored trees, hash each file, skip
 * unchanged, chunk by language, write file + chunk + FTS. Always correct,
 * runs in seconds.
 */

import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { execFileSync } from 'node:child_process';

import { chunkText, langForPath } from './chunkers.mjs';
import { startRun } from './db.mjs';
import { VENDORED_DIRS, BINARY_EXTS, MAX_FILE_BYTES, REPO_ROOT, REPOS } from './config.mjs';

const git = (cwd, args) =>
  execFileSync('git', args, { cwd, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });

export function gitRev(repoDir) {
  try {
    return git(repoDir, ['rev-parse', 'HEAD']).trim();
  } catch {
    return null;
  }
}

/** Tracked, non-vendored, plausibly-textual paths for one repo. */
export function listRepoFiles(repoDir) {
  const out = git(repoDir, ['ls-files', '-z']).split('\0').filter(Boolean);
  return out.filter(p => !isVendored(p) && !isBinaryPath(p));
}

export function isVendored(p) {
  const first = p.split('/')[0];
  return VENDORED_DIRS.includes(first);
}

export function isBinaryPath(p) {
  const base = path.basename(p);
  const dot = base.lastIndexOf('.');
  if (dot <= 0) return false;
  return BINARY_EXTS.has(base.slice(dot + 1).toLowerCase());
}

const sha256 = (buf) => crypto.createHash('sha256').update(buf).digest('hex');

/**
 * Index one repository. Incremental by content hash: a file whose bytes are
 * unchanged is not re-read for chunking, and files that left the index are
 * removed along with their chunks.
 */
export function indexRepo(db, { repo, repoDir, runId = null, full = false, onFile = null }) {
  const rev = gitRev(repoDir);
  const now = new Date().toISOString();
  const stats = { repo, indexed: 0, skipped: 0, removed: 0, chunks: 0, errors: [] };

  const selFile = db.prepare('SELECT id, content_sha FROM file WHERE repo = ? AND path = ?');
  const insFile = db.prepare(
    'INSERT INTO file(repo, path, lang, content_sha, git_rev, indexed_at) VALUES (?,?,?,?,?,?)');
  const updFile = db.prepare(
    'UPDATE file SET lang = ?, content_sha = ?, git_rev = ?, indexed_at = ? WHERE id = ?');
  const touchFile = db.prepare('UPDATE file SET git_rev = ?, indexed_at = ? WHERE id = ?');
  const delChunks = db.prepare('DELETE FROM chunk WHERE file_id = ?');
  const insChunk = db.prepare(
    'INSERT INTO chunk(file_id, kind, symbol, line_start, line_end, body) VALUES (?,?,?,?,?,?)');

  const paths = listRepoFiles(repoDir);
  const seen = new Set(paths);

  db.exec('BEGIN');
  try {
    for (const rel of paths) {
      const abs = path.join(repoDir, rel);
      let buf;
      try {
        const st = fs.statSync(abs);
        // git ls-files reports submodule gitlinks as paths; they stat as dirs.
        if (st.isDirectory() || st.size > MAX_FILE_BYTES) { stats.skipped++; continue; }
        buf = fs.readFileSync(abs);
      } catch (e) {
        stats.errors.push(`${rel}: ${e.message}`);   // deleted between ls-files and read
        continue;
      }
      if (buf.includes(0)) { stats.skipped++; continue; }   // binary without a telling extension

      const sha = sha256(buf);
      const existing = selFile.get(repo, rel);
      if (existing && existing.content_sha === sha && !full) {
        touchFile.run(rev, now, existing.id);
        stats.skipped++;
        continue;
      }

      const lang = langForPath(rel);
      const chunks = chunkText(lang, buf.toString('utf8'));
      let fileId;
      if (existing) {
        fileId = existing.id;
        updFile.run(lang, sha, rev, now, fileId);
        delChunks.run(fileId);
      } else {
        fileId = Number(insFile.run(repo, rel, lang, sha, rev, now).lastInsertRowid);
      }
      for (const c of chunks) {
        insChunk.run(fileId, c.kind, c.symbol, c.line_start, c.line_end, c.body);
      }
      stats.indexed++;
      stats.chunks += chunks.length;
      if (onFile) onFile(rel, chunks.length);
    }

    for (const row of db.prepare('SELECT id, path FROM file WHERE repo = ?').all(repo)) {
      if (seen.has(row.path)) continue;
      delChunks.run(row.id);
      db.prepare('DELETE FROM file WHERE id = ?').run(row.id);
      stats.removed++;
    }
    db.exec('COMMIT');
  } catch (e) {
    db.exec('ROLLBACK');
    throw e;
  }

  stats.git_rev = rev;
  stats.run_id = runId;
  return stats;
}

/** Index every configured repo, recording one `run` row for the whole pass. */
export function reindex(db, { repoRoot = REPO_ROOT, repos = REPOS, full = false, note = null } = {}) {
  const runId = startRun(db, { tool: 'phase-a-indexer', gitRev: null, note });
  const results = [];
  for (const repo of repos) {
    const repoDir = path.join(repoRoot, repo);
    if (!fs.existsSync(path.join(repoDir, '.git'))) {
      results.push({ repo, error: `not a git repository: ${repoDir}` });
      continue;
    }
    results.push(indexRepo(db, { repo, repoDir, runId, full }));
  }
  // The run's git_rev is only meaningful per repo; record them together.
  db.prepare('UPDATE run SET git_rev = ? WHERE id = ?')
    .run(results.map(r => `${r.repo}@${(r.git_rev ?? 'none').slice(0, 12)}`).join(' '), runId);
  return { runId, results };
}

/**
 * Optimise the FTS indexes. Worth doing after a full reindex; pointless after
 * an incremental one, so it is a separate call rather than automatic.
 */
export function optimizeFts(db) {
  db.exec("INSERT INTO chunk_fts(chunk_fts) VALUES ('optimize')");
  db.exec("INSERT INTO note_fts(note_fts) VALUES ('optimize')");
}
