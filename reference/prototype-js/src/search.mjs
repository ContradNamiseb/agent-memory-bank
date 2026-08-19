/**
 * BM25 retrieval over notes and chunks. Milestone 2 wraps this as the
 * `memory_search` MCP tool; for now it backs the CLI that proves Milestone 1.
 *
 * Design rule: results carry citations, never bare summaries.
 * Every row returned here can be resolved back to repo:path:line.
 */

const FTS_OPERATORS = /(^|\s)(AND|OR|NOT|NEAR)(\s|\()/;

/**
 * Turn a user query into a safe FTS5 MATCH expression.
 *
 * The default unicode61 tokenizer treats `_` as a separator, so the identifier
 * `retry_jitter` is indexed as the tokens retry, jitter. Quoting each term makes it
 * a phrase query, which matches the identifier exactly rather than matching any
 * file that happens to mention both words. Quoting also stops a stray `*` or
 * `(` in a code query from being read as FTS syntax and throwing.
 */
export function ftsQuery(query) {
  const raw = query.trim();
  if (!raw) throw new Error('empty query');
  // Let a deliberate advanced query through untouched.
  if (raw.includes('"') || FTS_OPERATORS.test(raw)) return raw;
  const terms = raw.split(/\s+/)
    .map(t => t.replace(/"/g, ''))
    .filter(t => /[\p{L}\p{N}]/u.test(t))
    .map(t => `"${t}"`);
  if (terms.length === 0) throw new Error(`no searchable terms in: ${query}`);
  return terms.join(' ');
}

/** BM25 over chunk bodies, with the symbol column weighted up. */
export function searchChunks(db, { query, repo = null, lang = null, limit = 10 }) {
  const match = ftsQuery(query);
  const rows = db.prepare(`
    SELECT c.id, c.kind, c.symbol, c.line_start, c.line_end,
           f.repo, f.path, f.lang, f.git_rev,
           bm25(chunk_fts, 1.0, 5.0) AS score,
           snippet(chunk_fts, 0, '>>', '<<', ' ... ', 24) AS snippet
    FROM chunk_fts
    JOIN chunk c ON c.id = chunk_fts.rowid
    JOIN file f ON f.id = c.file_id
    WHERE chunk_fts MATCH ?
      AND (? IS NULL OR f.repo = ?)
      AND (? IS NULL OR f.lang = ?)
    ORDER BY score
    LIMIT ?`).all(match, repo, repo, lang, lang, limit);
  return rows.map(r => ({ ...r, result_kind: 'chunk', citation: `${r.repo}/${r.path}:${r.line_start}` }));
}

/** BM25 over notes. Empty until Milestone 3 backfills them. */
export function searchNotes(db, { query, kind = null, limit = 10, includeSuperseded = false }) {
  const match = ftsQuery(query);
  const rows = db.prepare(`
    SELECT n.id, n.claim, n.rationale, n.evidence, n.kind, n.confidence,
           n.anchor_symbol, n.created_at, n.verified_at, n.superseded_by,
           bm25(note_fts, 5.0, 1.0, 2.0) AS score
    FROM note_fts
    JOIN note n ON n.id = note_fts.rowid
    WHERE note_fts MATCH ?
      AND (? IS NULL OR n.kind = ?)
      AND (? OR n.superseded_by IS NULL)
    ORDER BY score
    LIMIT ?`).all(match, kind, kind, includeSuperseded ? 1 : 0, limit);
  return rows.map(r => ({ ...r, result_kind: 'note' }));
}

/** Notes first, then grounding chunks — the shape `memory_search` will return. */
export function search(db, { query, repo = null, kind = null, limit = 10 }) {
  return {
    query,
    notes: searchNotes(db, { query, kind, limit }),
    chunks: searchChunks(db, { query, repo, limit }),
  };
}

/** Full text of one chunk, with its citation. */
export function getChunk(db, id) {
  return db.prepare(`
    SELECT c.*, f.repo, f.path, f.lang, f.git_rev, f.content_sha
    FROM chunk c JOIN file f ON f.id = c.file_id WHERE c.id = ?`).get(id);
}

export function stats(db) {
  const one = (sql) => db.prepare(sql).get();
  return {
    files: one('SELECT count(*) n FROM file').n,
    chunks: one('SELECT count(*) n FROM chunk').n,
    notes: one('SELECT count(*) n FROM note').n,
    runs: one('SELECT count(*) n FROM run').n,
    per_repo: db.prepare(`
      SELECT f.repo, count(DISTINCT f.id) files, count(c.id) chunks
      FROM file f LEFT JOIN chunk c ON c.file_id = f.id
      GROUP BY f.repo ORDER BY f.repo`).all(),
  };
}
