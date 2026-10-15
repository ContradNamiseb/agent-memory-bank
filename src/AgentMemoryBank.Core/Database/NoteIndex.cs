using Microsoft.Data.Sqlite;
using AgentMemoryBank.Models;

namespace AgentMemoryBank.Database;

/// <summary>One manifest row: enough to decide whether to fetch the note, nothing more.</summary>
/// <param name="Chars">Claim + rationale + evidence length: what a full fetch would cost.</param>
public sealed record IndexEntry(
    long Id, string Kind, string Confidence, int Chars, IReadOnlyList<string> Tags, string Claim,
    long? SupersededBy);

/// <summary>
/// The cheap orientation pass: what notes exist for a repository, and how big
/// each is, without their bodies. An agent reads this once, picks ids, and
/// fetches only those — instead of running searches until something relevant
/// turns up.
/// </summary>
public static class NoteIndex
{
    /// <summary>
    /// Notes matching every given filter, newest first. A note belongs to a
    /// repository when one of its citations resolved into that repository's
    /// index, or when its evidence names the repository as a path prefix —
    /// the second catches citations into vendored or unindexed trees, which
    /// are deliberately allowed and get no index edge.
    /// </summary>
    public static (IReadOnlyList<IndexEntry> Entries, int Total) List(SqliteConnection db,
        string? repo = null, NoteKind? kind = null, string? tag = null, bool includeSuperseded = false,
        int limit = 200, long beforeId = 0)
    {
        const string filter = """
            FROM note n
            WHERE ($kind IS NULL OR n.kind = $kind)
              AND ($all OR n.superseded_by IS NULL)
              AND ($before = 0 OR n.id < $before)
              AND ($tag IS NULL OR EXISTS (SELECT 1 FROM tag t WHERE t.note_id = n.id AND t.tag = $tag))
              AND ($repo IS NULL
                   OR n.evidence LIKE '%' || $repo || '/%'
                   OR EXISTS (SELECT 1 FROM edge e
                              JOIN chunk c ON c.id = e.dst_id
                              JOIN file f ON f.id = c.file_id
                              WHERE e.src_kind = 'note' AND e.src_id = n.id
                                AND e.dst_kind = 'chunk' AND f.repo = $repo))
            """;
        string[] names = ["$kind", "$all", "$before", "$tag", "$repo"];
        object?[] values =
        [
            kind?.ToString().ToLowerInvariant(), includeSuperseded ? 1 : 0, beforeId,
            string.IsNullOrWhiteSpace(tag) ? null : tag.Trim().ToLowerInvariant(),
            string.IsNullOrWhiteSpace(repo) ? null : repo.Trim(),
        ];

        using var count = db.Prepare($"SELECT COUNT(*) {filter}", names);
        var total = Convert.ToInt32(count.SetAll(values).ExecuteScalar());

        using var rows = db.Prepare($"""
            SELECT n.id, n.kind, n.confidence, n.claim, n.superseded_by,
                   length(n.claim) + COALESCE(length(n.rationale), 0) + COALESCE(length(n.evidence), 0) AS chars,
                   (SELECT group_concat(t.tag, ',') FROM tag t WHERE t.note_id = n.id) AS tags
            {filter}
            ORDER BY n.id DESC
            LIMIT $limit
            """, [.. names, "$limit"]);
        var entries = rows.SetAll([.. values, Math.Clamp(limit, 1, 2000)]).Query(r => new IndexEntry(
            r.GetInt64("id"),
            r.GetStringOrNull("kind") ?? "-",
            r.GetStringOrNull("confidence") ?? "-",
            r.GetInt32("chars"),
            (r.GetStringOrNull("tags") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries),
            r.GetString("claim"),
            r.IsDBNull(r.GetOrdinal("superseded_by")) ? null : r.GetInt64("superseded_by")));
        return (entries, total);
    }

    /// <summary>Every repository name the index knows, for refusing an unknown one with the valid list.</summary>
    public static IReadOnlyList<string> Repos(SqliteConnection db) =>
        db.Prepare("SELECT DISTINCT repo FROM file ORDER BY repo").Query(r => r.GetString("repo"));

    /// <summary>The most used tags, for refusing an unknown one with the valid list.</summary>
    public static IReadOnlyList<string> Tags(SqliteConnection db, int limit = 40) =>
        db.Prepare("SELECT tag FROM tag GROUP BY tag ORDER BY COUNT(*) DESC, tag LIMIT $n", "$n")
            .SetAll(limit).Query(r => r.GetString("tag"));
}
