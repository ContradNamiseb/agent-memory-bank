using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Server;
using AgentMemoryBank;
using AgentMemoryBank.Database;
using AgentMemoryBank.Models;

namespace AgentMemoryBank.McpServer;

/// <summary>What memory_get fetches.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ItemKind>))]
public enum ItemKind
{
    [JsonStringEnumMemberName("note")] Note,
    [JsonStringEnumMemberName("chunk")] Chunk,
}

/// <summary>
/// The read-only tool surface: search, get, index and stats. Nothing here can
/// corrupt the store.
///
/// <para>Design rule: these tools return <b>text with citations</b>, never bare
/// summaries. The caller must always be able to jump from an answer to the line
/// it came from, because a memory store that cannot be checked is worse than no
/// memory store — it is a confident one.</para>
///
/// <para>The descriptions matter as much as the code. Nothing queries a memory
/// bank unless something tells it to, and clients differ in whether they load
/// repository instruction files. Every MCP client shows the model its tool
/// descriptions, so the trigger for when to search belongs there — written
/// densely, because every session pays for them.</para>
///
/// <para>Sealed rather than static so the registration can name this type
/// explicitly: assembly scanning would also pick up any type that later gains
/// the attribute, which is the wrong default for a surface meant to stay
/// read-only.</para>
/// </summary>
[McpServerToolType]
public sealed class McpTools
{
    private McpTools() { }

    private static string UnknownRepo(SqliteConnection db, string repo)
    {
        var repos = NoteIndex.Repos(db);
        return repos.Count == 0
            ? $"Error: unknown repo '{repo}' — the store has no indexed repositories yet (run the indexer)."
            : $"Error: unknown repo '{repo}'. Valid repos: {string.Join(", ", repos)}.";
    }

    /// <summary>Opened per call: the store is small, and a long-lived handle would pin the WAL.</summary>
    private static SqliteConnection Open() => MemoryDatabase.Open(Config.DbPath);

    [McpServerTool(Name = "memory_search", ReadOnly = true)]
    [Description("""
        Search recorded decisions, hazards, measured findings and the indexed
        source behind them; BM25, notes first, each with a repo/path:line
        citation. Read-only. Search BEFORE changing a config value, tuning
        constant, build flag or loss, and before trusting a default. Use SHORT
        KEYWORDS ('wdl_spread', 'retry jitter'), not a sentence: terms are ANDed
        as phrases, with a labelled any-term retry when nothing matches all.
        Empty = genuinely not recorded. max_chars caps the output and lists the
        ids that did not fit.
        """)]
    public static string Search(
        [Description("An identifier, a phrase, or a few keywords.")]
        string query,
        [Description("Only chunks from this repository. Unknown names are refused with the valid list.")]
        string? repo = null,
        [Description("Only notes of this kind.")]
        NoteKind? kind = null,
        [Description("Max notes. Default 10.")]
        int limit = 10,
        [Description("Skip indexed source; return recorded notes only.")]
        bool notes_only = false,
        [Description("Max source chunks. Default 3.")]
        int chunk_limit = 3,
        [Description("Max output characters. Default 12000; 0 = no cap.")]
        int max_chars = 12_000)
    {
        if (string.IsNullOrWhiteSpace(query)) return "Error: query is empty.";

        using var db = Open();
        if (!string.IsNullOrWhiteSpace(repo) && !NoteIndex.Repos(db).Contains(repo.Trim()))
            return UnknownRepo(db, repo);

        SearchResult result;
        try
        {
            result = MemorySearch.Search(db, query, repo?.Trim(), kind, Math.Clamp(limit, 1, 50),
                Math.Clamp(chunk_limit, 0, 50), notes_only);
        }
        catch (ArgumentException e)
        {
            return $"Error: {e.Message}";
        }

        return ToolResponses.RenderSearch(result, db, max_chars);
    }

    [McpServerTool(Name = "memory_get", ReadOnly = true)]
    [Description("""
        Fetch stored items in full by id, as listed by memory_search or
        memory_index: a note with its rationale, evidence and verification
        state, or a source chunk with line numbers. Read-only. ids takes one
        or several; output is capped by max_chars and names the ids left
        over. An unknown id is reported with the valid range.
        """)]
    public static string Get(
        [Description("note or chunk.")]
        ItemKind kind,
        [Description("One or more ids, e.g. [3] or [3, 7].")]
        long[] ids,
        [Description("Max output characters. Default 20000; 0 = no cap.")]
        int max_chars = 20_000)
    {
        var wanted = (ids ?? []).Where(i => i > 0).Distinct().ToList();
        if (wanted.Count == 0) return "Error: ids is empty.";

        using var db = Open();
        var parts = new List<string>();
        var used = 0;
        for (var i = 0; i < wanted.Count; i++)
        {
            var part = kind == ItemKind.Chunk
                ? ToolResponses.RenderChunkById(db, wanted[i])
                : ToolResponses.RenderNoteById(db, wanted[i]);
            if (max_chars > 0 && parts.Count > 0 && used + part.Length > max_chars)
            {
                parts.Add($"max_chars reached; not shown: {string.Join(", ", wanted.Skip(i))}. " +
                          "Fetch them in another memory_get.");
                break;
            }
            parts.Add(part);
            used += part.Length;
        }
        return string.Join("\n\n---\n\n", parts);
    }

    [McpServerTool(Name = "memory_index", ReadOnly = true)]
    [Description("""
        Orient in one call: a TSV manifest of notes (id, kind, confidence,
        chars, tags, one-line claim), newest first, without bodies. Read-only.
        Filter by repo, kind or tag, then fetch only the ids you need with
        memory_get ids=[…]. Paginate with before_id. Unknown repo or tag is
        refused with the valid values.
        """)]
    public static string Index(
        [Description("Only notes citing this repository.")]
        string? repo = null,
        [Description("Only notes of this kind.")]
        NoteKind? kind = null,
        [Description("Only notes with this tag.")]
        string? tag = null,
        [Description("Include notes that were superseded. Default false.")]
        bool include_superseded = false,
        [Description("Max rows. Default 200.")]
        int limit = 200,
        [Description("Only notes with a lower id, to page backwards.")]
        long before_id = 0)
    {
        using var db = Open();
        if (!string.IsNullOrWhiteSpace(repo) && !NoteIndex.Repos(db).Contains(repo.Trim()))
            return UnknownRepo(db, repo);
        if (!string.IsNullOrWhiteSpace(tag) &&
            !db.Prepare("SELECT 1 FROM tag WHERE tag = $t", "$t").SetAll(tag.Trim().ToLowerInvariant()).Query(_ => 1).Any())
            return $"Error: no note has tag '{tag}'. Most used tags: {string.Join(", ", NoteIndex.Tags(db))}.";

        var (entries, total) = NoteIndex.List(db, repo, kind, tag, include_superseded, limit, before_id);
        var filters = string.Join(", ", new[]
        {
            repo is { Length: > 0 } ? $"repo={repo.Trim()}" : null,
            kind is { } k ? $"kind={k.ToString().ToLowerInvariant()}" : null,
            tag is { Length: > 0 } ? $"tag={tag.Trim()}" : null,
        }.Where(f => f is not null));
        return ToolResponses.RenderIndex(entries, total, filters.Length > 0 ? $"({filters})" : "");
    }

    [McpServerTool(Name = "memory_stats", ReadOnly = true)]
    [Description("""
        Store size: files, chunks and notes, per repository. Read-only. Use it
        to confirm the index is populated before trusting an empty search.
        """)]
    public static string Stats()
    {
        using var db = Open();
        return ToolResponses.RenderStats(db);
    }
}
