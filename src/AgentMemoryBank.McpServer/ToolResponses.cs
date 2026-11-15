using System.Text;
using Microsoft.Data.Sqlite;
using AgentMemoryBank.Continuity;
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
    public static string RenderSearch(SearchResult result, SqliteConnection db, int maxChars = 0) =>
        Render(result, db, maxChars);

    /// <summary>One chunk in full, with line numbers so any line can be cited.</summary>
    public static string RenderChunkById(SqliteConnection db, long id)
    {
        TryRenderChunkById(db, id, out var text);
        return text;
    }

    /// <summary>Try to render one chunk by id, returning false when not found.</summary>
    public static bool TryRenderChunkById(SqliteConnection db, long id, out string text) =>
        RenderChunk(db, id, out text);

    /// <summary>One note in full, including whether its evidence was ever re-verified.</summary>
    public static string RenderNoteById(SqliteConnection db, long id)
    {
        TryRenderNoteById(db, id, out var text);
        return text;
    }

    /// <summary>Try to render one note by id, returning false when not found.</summary>
    public static bool TryRenderNoteById(SqliteConnection db, long id, out string text) =>
        RenderNote(db, id, out text);

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

    private static string Render(SearchResult result, SqliteConnection db, int maxChars)
    {
        if (result.Total == 0)
        {
            var stats = MemorySearch.Stats(db);
            if (stats.Files == 0)
                return $"No results for '{result.Query}'. The store is empty: run the indexer before " +
                       "treating this as evidence that nothing is recorded.";

            // Say which passes ran. Claiming a query "genuinely matched nothing" when
            // only the AND pass was tried is how a caller concludes the store is empty
            // and stops asking.
            var tried = MemorySearch.CanLoosen(result.Query)
                ? "Both an all-terms and an any-term pass were tried"
                : "Searched as a single term";
            return $"No results for '{result.Query}'. The store holds {stats.Files} files and " +
                   $"{stats.Notes} notes. {tried}, so nothing on this subject is recorded. " +
                   "Try fewer or different keywords before concluding that.";
        }

        var text = new StringBuilder();

        if (result.Mode == MatchMode.Loosened)
        {
            text.AppendLine($"_No result matched every term of '{result.Query}'. These are **loosened** " +
                            "matches - any term, best first - so treat them as the nearest thing on file, " +
                            "not as a precise answer._");
            text.AppendLine();
        }

        // Blocks are added whole until the budget runs out; what did not fit is
        // named by id, so the caller fetches exactly those with memory_get.
        var cut = new List<string>();
        bool Fits(string block) => maxChars <= 0 || cut.Count == 0 && text.Length + block.Length <= maxChars;

        if (result.Notes.Count > 0)
        {
            text.AppendLine($"## Recorded knowledge ({result.Notes.Count})");
            text.AppendLine();
            foreach (var note in result.Notes)
            {
                var block = new StringBuilder();
                block.AppendLine($"### [note {note.Id}] {note.Claim}");
                block.AppendLine($"- kind: {Lower(note.Kind)} · confidence: {Lower(note.Confidence)}");
                if (!string.IsNullOrWhiteSpace(note.Rationale)) block.AppendLine($"- why: {note.Rationale}");
                if (!string.IsNullOrWhiteSpace(note.Evidence)) block.AppendLine($"- evidence: {note.Evidence}");
                block.AppendLine(note.VerifiedAt is null
                    ? "- evidence has never been re-verified since it was written"
                    : $"- evidence last verified: {note.VerifiedAt}");
                block.AppendLine();
                if (Fits(block.ToString())) text.Append(block);
                else cut.Add($"note {note.Id}");
            }
        }

        if (result.Chunks.Count > 0)
        {
            text.AppendLine($"## Indexed source ({result.Chunks.Count})");
            text.AppendLine();
            foreach (var hit in result.Chunks)
            {
                var symbol = hit.Symbol is null ? "" : $" — {hit.Symbol}";
                var block = $"### [chunk {hit.Id}] {hit.Citation}-{hit.LineEnd} ({hit.Kind}{symbol})\n" +
                            $"{Collapse(hit.Snippet)}\n\n";
                if (Fits(block)) text.Append(block);
                else cut.Add($"chunk {hit.Id}");
            }
            text.AppendLine("Call memory_get kind=chunk ids=[…] with ids above for the full text.");
        }

        if (cut.Count > 0)
            text.AppendLine($"max_chars reached; not shown: {string.Join(", ", cut)}. " +
                            "Fetch them with memory_get (ids takes a list).");

        return text.ToString().TrimEnd();
    }

    private static bool RenderChunk(SqliteConnection db, long id, out string rendered)
    {
        var found = MemorySearch.GetChunk(db, id);
        if (found is null)
        {
            rendered = $"Error: no chunk with id {id}; chunk ids run #1–#{db.Scalar<long>("SELECT COALESCE(MAX(id), 0) FROM chunk")}. " +
                       "Chunk ids come from memory_search results.";
            return false;
        }

        var (chunk, repo, path, gitRev) = found.Value;
        var text = new StringBuilder();
        text.AppendLine($"{repo}/{path}:{chunk.LineStart}-{chunk.LineEnd} ({chunk.Kind}" +
                        (chunk.Symbol is null ? "" : $" — {chunk.Symbol}") + ")");
        if (gitRev is not null) text.AppendLine($"indexed at commit {gitRev[..Math.Min(12, gitRev.Length)]}");
        text.AppendLine();

        var lines = chunk.Body.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            text.AppendLine($"{chunk.LineStart + i,6}| {lines[i]}");
        rendered = text.ToString().TrimEnd();
        return true;
    }

    private static bool RenderNote(SqliteConnection db, long id, out string rendered)
    {
        var note = MemorySearch.GetNote(db, id);
        if (note is null)
        {
            rendered = $"Error: no note with id {id}; {NoteStore.IdHint(db, id)}.";
            return false;
        }

        var text = new StringBuilder();
        text.AppendLine($"[note {id}] {note.Claim}");
        text.AppendLine($"kind: {Lower(note.Kind)} · confidence: {Lower(note.Confidence)} · created: {note.CreatedAt ?? "-"}");
        if (note.SupersededBy is not null)
            text.AppendLine($"SUPERSEDED by note {note.SupersededBy} — kept as a record of having been wrong.");
        if (note.Rationale is not null) text.AppendLine($"\nwhy:\n{note.Rationale}");
        if (note.Evidence is not null) text.AppendLine($"\nevidence:\n{note.Evidence}");
        if (note.AnchorSymbol is not null) text.AppendLine($"\nanchored to: {note.AnchorSymbol}");
        text.AppendLine(note.VerifiedAt is null
            ? "\nThis note's evidence has never been re-verified; the cited code may have moved."
            : $"\nEvidence last verified: {note.VerifiedAt}");
        rendered = text.ToString().TrimEnd();
        return true;
    }

    internal static string FormatHandoff(HandoffReader.Lookup lookup, int maxChars = 0)
    {
        var h = lookup.Handoff ?? throw new ArgumentNullException(nameof(lookup));
        var sb = new StringBuilder();
        sb.AppendLine($"=== Session Handoff #{h.Id} ({h.Agent}) ===");
        sb.AppendLine($"Topic:   {h.Topic}");
        sb.AppendLine($"Created: {h.CreatedAt}");
        if (h.Checkout is { } c)
            sb.AppendLine($"Written: `{c.BranchLabel}` @ `{c.ShortHead}` in `{c.Worktree}`");
        if (!string.IsNullOrWhiteSpace(h.ActiveFiles))
            sb.AppendLine($"Files:   {h.ActiveFiles}");
        sb.AppendLine();
        sb.AppendLine("--- Summary ---");
        sb.AppendLine(h.Summary);
        sb.AppendLine();
        sb.AppendLine("--- Next Steps ---");
        sb.AppendLine(h.NextSteps);
        if (!string.IsNullOrWhiteSpace(h.OpenQuestions))
        {
            sb.AppendLine();
            sb.AppendLine("--- Open Questions ---");
            sb.AppendLine(h.OpenQuestions);
        }
        if (!string.IsNullOrWhiteSpace(h.ContextData))
        {
            sb.AppendLine();
            sb.AppendLine("--- Context ---");
            sb.AppendLine(h.ContextData);
        }

        var body = sb.ToString().TrimEnd();
        if (maxChars > 0 && body.Length > maxChars)
            body = body[..maxChars] + $"\n…[cut at {maxChars} of {body.Length} chars; memory_handoff_latest max_chars=0 for all]";

        if (lookup.Preamble.Count == 0)
            return body;

        return string.Join(Environment.NewLine, lookup.Preamble) + Environment.NewLine + Environment.NewLine + body;
    }

    internal static string FormatNoteHits(IReadOnlyList<NoteHit> notes)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"notes ({notes.Count}):");
        foreach (var note in notes)
        {
            sb.AppendLine();
            sb.AppendLine($"  [note {note.Id}] {Lower(note.Kind)} / {Lower(note.Confidence)}  score {note.Score:F2}");
            sb.AppendLine($"  {note.Claim}");
            if (note.Evidence is not null)
                sb.AppendLine($"  evidence: {note.Evidence}");
            if (note.VerifiedAt is not null)
                sb.AppendLine($"  verified: {note.VerifiedAt}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The manifest as TSV: one header, one line per note, claims cut to a
    /// line. Column names once instead of property names per row is what makes
    /// a 200-note index affordable.
    /// </summary>
    public static string RenderIndex(IReadOnlyList<IndexEntry> entries, int total, string filters)
    {
        if (entries.Count == 0)
            return $"No notes match{(filters.Length > 0 ? $" {filters}" : "")}.";
        var text = new StringBuilder();
        text.AppendLine($"{entries.Count} of {total} note(s){(filters.Length > 0 ? $" {filters}" : "")}, newest first. " +
                        "Fetch bodies with memory_get kind=note ids=[…].");
        text.AppendLine("id\tkind\tconf\tchars\ttags\tclaim");
        foreach (var e in entries)
        {
            var claim = Collapse(e.Claim);
            if (claim.Length > 110) claim = claim[..109] + "…";
            var superseded = e.SupersededBy is { } by ? $" [superseded by {by}]" : "";
            text.AppendLine($"{e.Id}\t{e.Kind}\t{e.Confidence}\t{e.Chars}\t{string.Join(',', e.Tags)}\t{claim}{superseded}");
        }
        if (entries.Count < total)
            text.AppendLine($"… {total - entries.Count} older: continue with before_id={entries[^1].Id}.");
        return text.ToString().TrimEnd();
    }

    private static string Lower<T>(T? value) where T : struct, Enum =>
        value?.ToString().ToLowerInvariant() ?? "-";

    private static string Collapse(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
