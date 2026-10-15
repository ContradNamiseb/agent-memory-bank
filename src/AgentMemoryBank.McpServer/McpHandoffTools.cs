using System.ComponentModel;
using System.Text;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Server;
using AgentMemoryBank.Database;
using AgentMemoryBank.Models;

namespace AgentMemoryBank.McpServer;

/// <summary>
/// MCP tool surface for cross-agent session continuity and context handoffs.
///
/// <para>Results are labelled text, not indented JSON: the reader is a model,
/// and in a handoff the property names and indentation were a large share of
/// the bytes it paid for.</para>
/// </summary>
[McpServerToolType]
public sealed class McpHandoffTools
{
    private McpHandoffTools() { }

    private static SqliteConnection Open() => MemoryDatabase.Open(Config.DbPath);

    [McpServerTool(Name = "memory_handoff_save")]
    [Description("""
        Save a session handoff so the next agent (any client) resumes without
        re-deriving context. Writes one snapshot. Call before a usage or
        context limit, or when told to hand off. Returns the handoff id.
        """)]
    public static string SaveHandoff(
        [Description("Your agent/client name, e.g. 'Claude Code', 'Codex'.")]
        string agent,
        [Description("Short title of the task.")]
        string topic,
        [Description("What was done, found and decided.")]
        string summary,
        [Description("The exact next actions for the successor.")]
        string next_steps,
        [Description("Files modified or in progress, comma-separated.")]
        string? active_files = null,
        [Description("Open questions, pending decisions, blockers.")]
        string? open_questions = null,
        [Description("Extra JSON or key-value context.")]
        string? context_data = null)
    {
        if (string.IsNullOrWhiteSpace(agent) || string.IsNullOrWhiteSpace(topic) ||
            string.IsNullOrWhiteSpace(summary) || string.IsNullOrWhiteSpace(next_steps))
        {
            return "Error: agent, topic, summary, and next_steps are all required fields for a valid handoff.";
        }

        using var db = Open();
        var saved = HandoffStore.Save(db, new SessionHandoffDraft
        {
            Agent = agent,
            Topic = topic,
            Summary = summary,
            NextSteps = next_steps,
            ActiveFiles = active_files,
            OpenQuestions = open_questions,
            ContextData = context_data,
        });

        return $"saved handoff #{saved.Id} ({saved.Agent}, {saved.CreatedAt}): {saved.Topic}. " +
               "The next agent resumes with memory_handoff_latest.";
    }

    [McpServerTool(Name = "memory_handoff_latest", ReadOnly = true)]
    [Description("""
        The newest session handoff from any agent: topic, summary, next steps,
        active files, open questions. Read-only. Call when starting or resuming
        work. A hook-written handoff is unreviewed; verify what it names.
        max_chars caps the output.
        """)]
    public static string GetLatestHandoff(
        [Description("Only handoffs whose agent name contains this text.")]
        string? agent = null,
        [Description("Max output characters. Default 12000; 0 = no cap.")]
        int max_chars = 12_000)
    {
        using var db = Open();
        var latest = HandoffStore.GetLatest(db, Filter(agent));
        if (latest is null) return NoMatch(db, agent);

        var text = Render(latest);
        return max_chars > 0 && text.Length > max_chars
            ? text[..max_chars] + $"\n…[cut at {max_chars} of {text.Length} chars; memory_handoff_latest max_chars=0 for all]"
            : text;
    }

    [McpServerTool(Name = "memory_handoff_list", ReadOnly = true)]
    [Description("""
        Past handoffs newest first, one line each: '#id time agent: topic'.
        Read-only.
        """)]
    public static string ListHandoffs(
        [Description("Max handoffs. Default 5.")]
        int limit = 5,
        [Description("Only handoffs whose agent name contains this text.")]
        string? agent = null)
    {
        using var db = Open();
        var handoffs = HandoffStore.List(db, limit > 0 ? limit : 5, Filter(agent));
        if (handoffs.Count == 0) return NoMatch(db, agent);
        return string.Join('\n', handoffs.Select(h => $"#{h.Id} {Short(h.CreatedAt)} {h.Agent}: {h.Topic}"));
    }

    private static string? Filter(string? agent) => string.IsNullOrWhiteSpace(agent) ? null : agent.Trim();

    /// <summary>"None" for an empty store; for a filter that matched nothing, the agent names that would match.</summary>
    private static string NoMatch(SqliteConnection db, string? agent)
    {
        if (Filter(agent) is null) return "No session handoffs recorded yet.";
        var agents = db.Prepare("SELECT DISTINCT agent FROM session_handoff ORDER BY agent")
            .Query(r => r.GetString("agent"));
        return $"No handoff from an agent matching '{agent}'. Agents with handoffs: {string.Join(", ", agents)}.";
    }

    private static string Render(SessionHandoff h)
    {
        var text = new StringBuilder();
        text.AppendLine($"handoff #{h.Id} · {h.Agent} · {h.CreatedAt}");
        text.AppendLine($"topic: {h.Topic}");
        text.AppendLine($"summary:\n{h.Summary}");
        text.AppendLine($"next steps:\n{h.NextSteps}");
        if (!string.IsNullOrWhiteSpace(h.ActiveFiles)) text.AppendLine($"active files: {h.ActiveFiles}");
        if (!string.IsNullOrWhiteSpace(h.OpenQuestions)) text.AppendLine($"open questions:\n{h.OpenQuestions}");
        if (!string.IsNullOrWhiteSpace(h.ContextData)) text.AppendLine($"context: {h.ContextData}");
        return text.ToString().TrimEnd();
    }

    private static string Short(string iso) => iso.Length >= 16 ? iso[..16].Replace('T', ' ') : iso;
}
