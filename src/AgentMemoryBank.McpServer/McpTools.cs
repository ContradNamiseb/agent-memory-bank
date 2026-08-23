using System.ComponentModel;
using System.Text;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Server;
using AgentMemoryBank;
using AgentMemoryBank.Database;
using AgentMemoryBank.Models;

namespace AgentMemoryBank.McpServer;

/// <summary>
/// The read-only tool surface: search and get. Writing notes is a later
/// milestone, so nothing here can corrupt the store.
///
/// <para>Design rule: these tools return <b>text with citations</b>, never bare
/// summaries. The caller must always be able to jump from an answer to the line
/// it came from, because a memory store that cannot be checked is worse than no
/// memory store — it is a confident one.</para>
///
/// <para>The descriptions matter as much as the code. Nothing queries a memory
/// bank unless something tells it to, and clients differ in whether they load
/// repository instruction files. Every MCP client shows the model its tool
/// descriptions, so the trigger for when to search belongs there.</para>
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

    /// <summary>Opened per call: the store is small, and a long-lived handle would pin the WAL.</summary>
    private static SqliteConnection Open() => MemoryDatabase.Open(Config.DbPath);

    [McpServerTool(Name = "memory_search")]
    [Description("""
        Search durable project knowledge: recorded decisions, hazards, measured findings, and
        the indexed source that grounds them. Ranked by BM25, notes first, every result carrying
        a repo/path:line citation.

        Search here BEFORE changing configuration values, tuning parameters, build flags, loss
        functions, or any constant whose correct value was established by measurement rather than
        chosen. Also search before concluding that a default is safe: recorded hazards are mostly
        defaults that destroy data or limits that fail silently.

        Query with SHORT KEYWORDS, not a sentence: 'wdl_spread' or 'retry jitter', not 'what
        wdl spread should I use'. Terms are ANDed, so every extra word narrows the result. Each
        term is matched as a phrase, so an identifier such as some_config_key finds that exact
        identifier. If nothing matches every term the search retries with any term and says so.

        Returns nothing when the store is empty rather than guessing.
        """)]
    public static string Search(
        [Description("What to look for. An identifier, a phrase, or a few keywords.")]
        string query,
        [Description("Limit results to one repository by name. Omit to search all of them.")]
        string? repo = null,
        [Description("Limit notes to one kind: decision, hazard, howto, or finding.")]
        string? kind = null,
        [Description("Maximum notes to return. Defaults to 10.")]
        int limit = 10,
        [Description("Return only recorded notes, skipping the indexed source that grounds them. "
                     + "Use when you already have the code open and want the decisions, not the lines.")]
        bool notes_only = false,
        [Description("Maximum indexed-source results. Defaults to 3 - the notes are the answer, "
                     + "the chunks are only there so the answer can be checked.")]
        int chunk_limit = 3)
    {
        if (string.IsNullOrWhiteSpace(query)) return "Error: query is empty.";

        using var db = Open();
        NoteKind? noteKind = null;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            if (!Enum.TryParse<NoteKind>(kind, ignoreCase: true, out var parsed))
                return $"Error: unknown kind '{kind}'. Expected one of: " +
                       string.Join(", ", Enum.GetNames<NoteKind>().Select(n => n.ToLowerInvariant()));
            noteKind = parsed;
        }

        SearchResult result;
        try
        {
            result = MemorySearch.Search(db, query, repo, noteKind, Math.Clamp(limit, 1, 50),
                Math.Clamp(chunk_limit, 0, 50), notes_only);
        }
        catch (ArgumentException e)
        {
            return $"Error: {e.Message}";
        }

        return ToolResponses.RenderSearch(result, db);
    }

    [McpServerTool(Name = "memory_get")]
    [Description("""
        Fetch one stored item in full by id, as returned by memory_search. Use this when a search
        snippet is not enough to judge whether a finding still applies. Kind is 'chunk' for indexed
        source or 'note' for a recorded claim.
        """)]
    public static string Get(
        [Description("Either 'chunk' or 'note'.")] string kind,
        [Description("The id shown in the search result, e.g. 'chunk 3160'.")] long id)
    {
        using var db = Open();
        return kind.ToLowerInvariant() switch
        {
            "chunk" => ToolResponses.RenderChunkById(db, id),
            "note" => ToolResponses.RenderNoteById(db, id),
            _ => $"Error: kind must be 'chunk' or 'note', not '{kind}'.",
        };
    }

    [McpServerTool(Name = "memory_stats")]
    [Description("""
        Report what the store currently holds: how many files, chunks and notes, broken down by
        repository. Use this to check whether the index is populated and current before trusting
        an empty search result.
        """)]
    public static string Stats()
    {
        using var db = Open();
        return ToolResponses.RenderStats(db);
    }
}
