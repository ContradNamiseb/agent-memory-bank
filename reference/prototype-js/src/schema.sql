-- agent-memory-bank schema.
-- Idempotent: safe to run on every open.

-- Provenance for every ingestion run.
CREATE TABLE IF NOT EXISTS run(
  id INTEGER PRIMARY KEY, started_at TEXT, tool TEXT, git_rev TEXT, note TEXT);

-- One row per indexed file.
CREATE TABLE IF NOT EXISTS file(
  id INTEGER PRIMARY KEY, repo TEXT, path TEXT, lang TEXT,
  content_sha TEXT,            -- re-chunk only when this changes
  git_rev TEXT, indexed_at TEXT,
  UNIQUE(repo, path));

-- Chunks: function, class, doc section, config block.
CREATE TABLE IF NOT EXISTS chunk(
  id INTEGER PRIMARY KEY, file_id INTEGER REFERENCES file(id),
  kind TEXT, symbol TEXT, line_start INTEGER, line_end INTEGER, body TEXT);

-- The valuable table. Durable claims, written by you or an agent.
CREATE TABLE IF NOT EXISTS note(
  id INTEGER PRIMARY KEY,
  claim TEXT NOT NULL,         -- one sentence, falsifiable
  rationale TEXT,              -- why, and how it was established
  evidence TEXT,               -- repo:path:line refs, commands, measurements
  anchor_symbol TEXT,          -- enclosing function/class, if any
  anchor_text TEXT,            -- one cited line, whitespace-normalised
  kind TEXT,                   -- decision | hazard | howto | finding
  confidence TEXT,             -- measured | inferred | assumed
  created_at TEXT, verified_at TEXT,
  superseded_by INTEGER REFERENCES note(id),
  run_id INTEGER REFERENCES run(id));

CREATE TABLE IF NOT EXISTS tag(note_id INTEGER, tag TEXT, PRIMARY KEY(note_id, tag));

-- Typed relations: note->chunk (cites), note->note (refines / contradicts).
CREATE TABLE IF NOT EXISTS edge(
  src_kind TEXT, src_id INTEGER, dst_kind TEXT, dst_id INTEGER, rel TEXT);

CREATE INDEX IF NOT EXISTS chunk_by_file ON chunk(file_id);
CREATE INDEX IF NOT EXISTS file_by_repo ON file(repo);
CREATE INDEX IF NOT EXISTS edge_by_src ON edge(src_kind, src_id);
CREATE INDEX IF NOT EXISTS edge_by_dst ON edge(dst_kind, dst_id);

-- Default unicode61 tokenizer: '_' is a separator, so `retry_jitter` indexes as
-- the two tokens retry, jitter. Searching the identifier as a quoted phrase
-- matches it exactly, and a search for `retry` alone still finds it. Making '_'
-- a token character would break the second case, so the default stays.
CREATE VIRTUAL TABLE IF NOT EXISTS chunk_fts USING fts5(
  body, symbol, content='chunk', content_rowid='id');
CREATE VIRTUAL TABLE IF NOT EXISTS note_fts USING fts5(
  claim, rationale, evidence, content='note', content_rowid='id');

-- External-content FTS tables do not track their source table by themselves.
CREATE TRIGGER IF NOT EXISTS chunk_ai AFTER INSERT ON chunk BEGIN
  INSERT INTO chunk_fts(rowid, body, symbol) VALUES (new.id, new.body, new.symbol);
END;
CREATE TRIGGER IF NOT EXISTS chunk_ad AFTER DELETE ON chunk BEGIN
  INSERT INTO chunk_fts(chunk_fts, rowid, body, symbol)
    VALUES ('delete', old.id, old.body, old.symbol);
END;
CREATE TRIGGER IF NOT EXISTS chunk_au AFTER UPDATE ON chunk BEGIN
  INSERT INTO chunk_fts(chunk_fts, rowid, body, symbol)
    VALUES ('delete', old.id, old.body, old.symbol);
  INSERT INTO chunk_fts(rowid, body, symbol) VALUES (new.id, new.body, new.symbol);
END;

CREATE TRIGGER IF NOT EXISTS note_ai AFTER INSERT ON note BEGIN
  INSERT INTO note_fts(rowid, claim, rationale, evidence)
    VALUES (new.id, new.claim, new.rationale, new.evidence);
END;
CREATE TRIGGER IF NOT EXISTS note_ad AFTER DELETE ON note BEGIN
  INSERT INTO note_fts(note_fts, rowid, claim, rationale, evidence)
    VALUES ('delete', old.id, old.claim, old.rationale, old.evidence);
END;
CREATE TRIGGER IF NOT EXISTS note_au AFTER UPDATE ON note BEGIN
  INSERT INTO note_fts(note_fts, rowid, claim, rationale, evidence)
    VALUES ('delete', old.id, old.claim, old.rationale, old.evidence);
  INSERT INTO note_fts(rowid, claim, rationale, evidence)
    VALUES (new.id, new.claim, new.rationale, new.evidence);
END;
