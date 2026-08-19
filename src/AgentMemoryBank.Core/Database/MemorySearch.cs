using Microsoft.Data.Sqlite;
using AgentMemoryBank.Models;

namespace AgentMemoryBank.Database;

/// <summary>
/// BM25 retrieval over notes and chunks — what the memory_search tool exposes.
///
/// <para>No embeddings, deliberately: a corpus of this size indexes
/// fine with FTS5 and bm25, and a vector store would add a model download, a
/// runtime, and drift between index and model version.</para>
/// </summary>
public static class MemorySearch
{
    /// <summary>
    /// Turn a user query into a safe FTS5 MATCH expression.
    ///
    /// <para>The default unicode61 tokenizer treats '_' as a separator, so the
    /// identifier retry_jitter is indexed as the two tokens retry and jitter.
    /// Quoting each term makes it a phrase query, which matches the identifier
    /// exactly instead of matching any file that happens to mention both words.
    /// Quoting also stops a stray '*' or '(' in a code query from being read as
    /// FTS syntax and throwing.</para>
    /// </summary>
    public static string BuildMatchExpression(string query)
    {
        var raw = query.Trim();
        if (raw.Length == 0) throw new ArgumentException("empty query", nameof(query));

        // Let a deliberate advanced query (quoted phrases, AND/OR/NOT/NEAR) through untouched.
        if (raw.Contains('"') || ContainsOperator(raw)) return raw;

        var terms = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Replace("\"", ""))
            .Where(t => t.Any(char.IsLetterOrDigit))
            .Select(t => $"\"{t}\"")
            .ToList();

        if (terms.Count == 0) throw new ArgumentException($"no searchable terms in: {query}", nameof(query));
        return string.Join(' ', terms);
    }

    private static bool ContainsOperator(string query) =>
        query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Any(t => t is "AND" or "OR" or "NOT" || t.StartsWith("NEAR(", StringComparison.Ordinal));

    /// <summary>BM25 over chunk bodies, with the symbol column weighted up.</summary>
    public static IReadOnlyList<ChunkHit> SearchChunks(SqliteConnection db, string query,
        string? repo = null, string? lang = null, int limit = 10)
    {
        using var cmd = db.Prepare("""
            SELECT c.id, f.repo, f.path, f.lang, c.kind, c.symbol,
                   c.line_start, c.line_end, f.git_rev,
                   bm25(chunk_fts, 1.0, 5.0) AS score,
                   snippet(chunk_fts, 0, '>>', '<<', ' ... ', 24) AS snippet
            FROM chunk_fts
            JOIN chunk c ON c.id = chunk_fts.rowid
            JOIN file f ON f.id = c.file_id
            WHERE chunk_fts MATCH $match
              AND ($repo IS NULL OR f.repo = $repo)
              AND ($lang IS NULL OR f.lang = $lang)
            ORDER BY score
            LIMIT $limit
            """, "$match", "$repo", "$lang", "$limit");

        return cmd.SetAll(BuildMatchExpression(query), repo, lang, limit)
            .Query(r => new ChunkHit(
                Id: r.GetInt64("id"),
                Repo: r.GetString("repo"),
                Path: r.GetString("path"),
                Lang: r.GetString("lang"),
                Kind: r.GetString("kind"),
                Symbol: r.GetStringOrNull("symbol"),
                LineStart: r.GetInt32("line_start"),
                LineEnd: r.GetInt32("line_end"),
                GitRev: r.GetStringOrNull("git_rev"),
                Score: r.GetDouble("score"),
                Snippet: r.GetString("snippet")));
    }

    /// <summary>
    /// BM25 over notes, claim weighted highest. Superseded notes are hidden by
    /// default: they are kept as a record of having been wrong, not as an answer.
    /// </summary>
    public static IReadOnlyList<NoteHit> SearchNotes(SqliteConnection db, string query,
        NoteKind? kind = null, int limit = 10, bool includeSuperseded = false)
    {
        using var cmd = db.Prepare("""
            SELECT n.id, n.claim, n.rationale, n.evidence, n.kind, n.confidence,
                   n.anchor_symbol, n.created_at, n.verified_at, n.superseded_by,
                   bm25(note_fts, 5.0, 1.0, 2.0) AS score
            FROM note_fts
            JOIN note n ON n.id = note_fts.rowid
            WHERE note_fts MATCH $match
              AND ($kind IS NULL OR n.kind = $kind)
              AND ($includeSuperseded OR n.superseded_by IS NULL)
            ORDER BY score
            LIMIT $limit
            """, "$match", "$kind", "$includeSuperseded", "$limit");

        return cmd.SetAll(BuildMatchExpression(query), kind?.ToString().ToLowerInvariant(),
                includeSuperseded ? 1 : 0, limit)
            .Query(r => new NoteHit(
                Id: r.GetInt64("id"),
                Claim: r.GetString("claim"),
                Rationale: r.GetStringOrNull("rationale"),
                Evidence: r.GetStringOrNull("evidence"),
                Kind: ParseEnum<NoteKind>(r.GetStringOrNull("kind")),
                Confidence: ParseEnum<Confidence>(r.GetStringOrNull("confidence")),
                AnchorSymbol: r.GetStringOrNull("anchor_symbol"),
                CreatedAt: r.GetStringOrNull("created_at"),
                VerifiedAt: r.GetStringOrNull("verified_at"),
                SupersededBy: r.IsDBNull(r.GetOrdinal("superseded_by")) ? null : r.GetInt64("superseded_by"),
                Score: r.GetDouble("score")));
    }

    private static T? ParseEnum<T>(string? value) where T : struct =>
        Enum.TryParse<T>(value, ignoreCase: true, out var parsed) ? parsed : null;

    /// <summary>Notes first, then the chunks that ground them.</summary>
    public static SearchResult Search(SqliteConnection db, string query,
        string? repo = null, NoteKind? kind = null, int limit = 10) =>
        new(query,
            SearchNotes(db, query, kind, limit),
            SearchChunks(db, query, repo, limit: limit));

    /// <summary>Full text of one chunk, with the file columns needed to cite it.</summary>
    public static (Chunk Chunk, string Repo, string Path, string? GitRev)? GetChunk(SqliteConnection db, long id)
    {
        using var cmd = db.Prepare("""
            SELECT c.kind, c.symbol, c.line_start, c.line_end, c.body,
                   f.repo, f.path, f.git_rev
            FROM chunk c JOIN file f ON f.id = c.file_id
            WHERE c.id = $id
            """, "$id");

        return cmd.SetAll(id)
            .Query(r => ((Chunk, string, string, string?)?)(
                new Chunk(r.GetString("kind"), r.GetStringOrNull("symbol"),
                    r.GetInt32("line_start"), r.GetInt32("line_end"), r.GetString("body")),
                r.GetString("repo"), r.GetString("path"), r.GetStringOrNull("git_rev")))
            .FirstOrDefault();
    }

    /// <summary>Full details of one note.</summary>
    public static Note? GetNote(SqliteConnection db, long id)
    {
        using var cmd = db.Prepare("""
            SELECT id, claim, rationale, evidence, kind, confidence,
                   anchor_symbol, anchor_text, created_at, verified_at,
                   superseded_by, run_id
            FROM note
            WHERE id = $id
            """, "$id");

        return cmd.SetAll(id)
            .Query(r => new Note
            {
                Id = r.GetInt64("id"),
                Claim = r.GetString("claim"),
                Rationale = r.GetStringOrNull("rationale"),
                Evidence = r.GetStringOrNull("evidence"),
                AnchorSymbol = r.GetStringOrNull("anchor_symbol"),
                AnchorText = r.GetStringOrNull("anchor_text"),
                Kind = r.IsDBNull(r.GetOrdinal("kind")) ? null : Enum.Parse<NoteKind>(r.GetString("kind"), ignoreCase: true),
                Confidence = r.IsDBNull(r.GetOrdinal("confidence")) ? null : Enum.Parse<Confidence>(r.GetString("confidence"), ignoreCase: true),
                CreatedAt = r.GetStringOrNull("created_at"),
                VerifiedAt = r.GetStringOrNull("verified_at"),
                SupersededBy = r.IsDBNull(r.GetOrdinal("superseded_by")) ? null : r.GetInt64("superseded_by"),
                RunId = r.IsDBNull(r.GetOrdinal("run_id")) ? null : r.GetInt64("run_id"),
            })
            .FirstOrDefault();
    }

    public static StoreStats Stats(SqliteConnection db)
    {
        using var perRepo = db.Prepare("""
            SELECT f.repo, count(DISTINCT f.id) AS files, count(c.id) AS chunks
            FROM file f LEFT JOIN chunk c ON c.file_id = f.id
            GROUP BY f.repo ORDER BY f.repo
            """);

        return new StoreStats(
            Files: db.Scalar<int>("SELECT count(*) FROM file"),
            Chunks: db.Scalar<int>("SELECT count(*) FROM chunk"),
            Notes: db.Scalar<int>("SELECT count(*) FROM note"),
            Runs: db.Scalar<int>("SELECT count(*) FROM run"),
            PerRepo: perRepo.Query(r => new RepoStats(r.GetString("repo"), r.GetInt32("files"), r.GetInt32("chunks"))));
    }
}
