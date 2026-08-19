using System.Text;
using Microsoft.Data.Sqlite;
using AgentMemoryBank.Database;
using AgentMemoryBank.Models;

namespace AgentMemoryBank.McpServer;

/// <summary>
/// Turns query results into the text the MCP tools return.
///
/// <para>Separate from <see cref="McpTools"/> so it can be tested against a
/// throwaway database: the tool methods themselves open whatever the local
/// configuration points at, which is exactly what a test must not do.</para>
///
/// <para>The wording carries weight. A result that says "no matches" when the
/// store was never indexed invites the reader to conclude nothing was recorded,
/// so the empty cases distinguish the two.</para>
/// </summary>
public static class ToolResponses
{
    /// <summary>Search results: notes first, then the chunks that ground them.</summary>
    public static string RenderSearch(SearchResult result, SqliteConnection db) => Render(result, db);

    /// <summary>One chunk in full, with line numbers so any line can be cited.</summary>
    public static string RenderChunkById(SqliteConnection db, long id) => RenderChunk(db, id);

    /// <summary>One note in full, including whether its evidence was ever re-verified.</summary>
    public static string RenderNoteById(SqliteConnection db, long id) => RenderNote(db, id);

    /// <summary>What the store holds, and whether an empty search means anything.</summary>
    public static string RenderStats(SqliteConnection db)
    {
        var stats = MemorySearch.Stats(db);
        var text = new StringBuilder();
        text.AppendLine($"{stats.Files} files, {stats.Chunks} chunks, {stats.Notes} notes, {stats.Runs} indexing runs.");
        if (stats.Files == 0)
            text.AppendLine("The store is empty - it has not been indexed yet, so an empty search result means nothing.");
        foreach (var repo in stats.PerRepo)
            text.AppendLine($"  {repo.Repo}: {repo.Files} files, {repo.Chunks} chunks");
        return text.ToString().TrimEnd();
    }

    private static string Render(SearchResult result, SqliteConnection db)
    {
        if (result.Total == 0)
        {
            var stats = MemorySearch.Stats(db);
            return stats.Files == 0
                ? $"No results for '{result.Query}'. The store is empty: run the indexer before " +
                  "treating this as evidence that nothing is recorded."
                : $"No results for '{result.Query}'. The store holds {stats.Files} files and " +
                  $"{stats.Notes} notes, so this query genuinely matched nothing.";
        }

        var text = new StringBuilder();

        if (result.Notes.Count > 0)
        {
            text.AppendLine($"## Recorded knowledge ({result.Notes.Count})");
            text.AppendLine();
            foreach (var note in result.Notes)
            {
                text.AppendLine($"### [note {note.Id}] {note.Claim}");
                text.AppendLine($"- kind: {Lower(note.Kind)} · confidence: {Lower(note.Confidence)}");
                if (!string.IsNullOrWhiteSpace(note.Rationale)) text.AppendLine($"- why: {note.Rationale}");
                if (!string.IsNullOrWhiteSpace(note.Evidence)) text.AppendLine($"- evidence: {note.Evidence}");
                text.AppendLine(note.VerifiedAt is null
                    ? "- evidence has never been re-verified since it was written"
                    : $"- evidence last verified: {note.VerifiedAt}");
                text.AppendLine();
            }
        }

        if (result.Chunks.Count > 0)
        {
            text.AppendLine($"## Indexed source ({result.Chunks.Count})");
            text.AppendLine();
            foreach (var hit in result.Chunks)
            {
                var symbol = hit.Symbol is null ? "" : $" — {hit.Symbol}";
                text.AppendLine($"### [chunk {hit.Id}] {hit.Citation}-{hit.LineEnd} ({hit.Kind}{symbol})");
                text.AppendLine(Collapse(hit.Snippet));
                text.AppendLine();
            }
            text.AppendLine("Call memory_get with kind 'chunk' and an id above for the full text.");
        }

        return text.ToString().TrimEnd();
    }

    private static string RenderChunk(SqliteConnection db, long id)
    {
        var found = MemorySearch.GetChunk(db, id);
        if (found is null) return $"No chunk with id {id}.";

        var (chunk, repo, path, gitRev) = found.Value;
        var text = new StringBuilder();
        text.AppendLine($"{repo}/{path}:{chunk.LineStart}-{chunk.LineEnd} ({chunk.Kind}" +
                        (chunk.Symbol is null ? "" : $" — {chunk.Symbol}") + ")");
        if (gitRev is not null) text.AppendLine($"indexed at commit {gitRev[..Math.Min(12, gitRev.Length)]}");
        text.AppendLine();

        var lines = chunk.Body.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            text.AppendLine($"{chunk.LineStart + i,6}| {lines[i]}");
        return text.ToString().TrimEnd();
    }

    private static string RenderNote(SqliteConnection db, long id)
    {
        using var cmd = db.Prepare("""
            SELECT id, claim, rationale, evidence, anchor_symbol, anchor_text, kind, confidence,
                   created_at, verified_at, superseded_by
            FROM note WHERE id = $id
            """, "$id");

        var note = cmd.SetAll(id).Query(r => new
        {
            Claim = r.GetString("claim"),
            Rationale = r.GetStringOrNull("rationale"),
            Evidence = r.GetStringOrNull("evidence"),
            Anchor = r.GetStringOrNull("anchor_symbol"),
            Kind = r.GetStringOrNull("kind"),
            Confidence = r.GetStringOrNull("confidence"),
            Created = r.GetStringOrNull("created_at"),
            Verified = r.GetStringOrNull("verified_at"),
            Superseded = r.IsDBNull(r.GetOrdinal("superseded_by")) ? (long?)null : r.GetInt64("superseded_by"),
        }).FirstOrDefault();

        if (note is null) return $"No note with id {id}.";

        var text = new StringBuilder();
        text.AppendLine($"[note {id}] {note.Claim}");
        text.AppendLine($"kind: {note.Kind ?? "-"} · confidence: {note.Confidence ?? "-"} · created: {note.Created ?? "-"}");
        if (note.Superseded is not null)
            text.AppendLine($"SUPERSEDED by note {note.Superseded} — kept as a record of having been wrong.");
        if (note.Rationale is not null) text.AppendLine($"\nwhy:\n{note.Rationale}");
        if (note.Evidence is not null) text.AppendLine($"\nevidence:\n{note.Evidence}");
        if (note.Anchor is not null) text.AppendLine($"\nanchored to: {note.Anchor}");
        text.AppendLine(note.Verified is null
            ? "\nThis note's evidence has never been re-verified; the cited code may have moved."
            : $"\nEvidence last verified: {note.Verified}");
        return text.ToString().TrimEnd();
    }

    private static string Lower<T>(T? value) where T : struct, Enum =>
        value?.ToString().ToLowerInvariant() ?? "-";

    private static string Collapse(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
