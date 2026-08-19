using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Server;
using AgentMemoryBank.Database;
using AgentMemoryBank.Models;

namespace AgentMemoryBank.McpServer;

/// <summary>
/// MCP tool surface for cross-agent session continuity and context handoffs.
/// </summary>
[McpServerToolType]
public sealed class McpHandoffTools
{
    private McpHandoffTools() { }

    private static SqliteConnection Open() => MemoryDatabase.Open(Config.DbPath);

    [McpServerTool(Name = "memory_handoff_save")]
    [Description("""
        Save a session handoff / context snapshot to the memory bank.
        Use this before ending a session or when hitting a rate limit so the next agent
        (Claude Code, Antigravity, VS Code Copilot, Cursor) can seamlessly continue your work without losing context.
        """)]
    public static string SaveHandoff(
        [Description("Name of the agent creating the handoff, e.g. 'Antigravity (Gemini)', 'Claude Code', 'VS Code Copilot'.")]
        string agent,
        [Description("Brief title or topic of the task, e.g. 'DirectML checkpoint recovery fix'.")]
        string topic,
        [Description("Clear summary of what was accomplished, what was discovered, and what decisions were made.")]
        string summary,
        [Description("Explicit list of next immediate action steps for the incoming agent to execute.")]
        string next_steps,
        [Description("Comma-separated list or paths of files currently modified or in progress.")]
        string? active_files = null,
        [Description("Any open questions, pending decisions, or blockers needing attention.")]
        string? open_questions = null,
        [Description("Optional JSON or key-value context data.")]
        string? context_data = null)
    {
        if (string.IsNullOrWhiteSpace(agent) || string.IsNullOrWhiteSpace(topic) ||
            string.IsNullOrWhiteSpace(summary) || string.IsNullOrWhiteSpace(next_steps))
        {
            return "Error: agent, topic, summary, and next_steps are all required fields for a valid handoff.";
        }

        using var db = Open();
        var draft = new SessionHandoffDraft
        {
            Agent = agent,
            Topic = topic,
            Summary = summary,
            NextSteps = next_steps,
            ActiveFiles = active_files,
            OpenQuestions = open_questions,
            ContextData = context_data
        };

        var saved = HandoffStore.Save(db, draft);

        return JsonSerializer.Serialize(new
        {
            status = "saved",
            handoff_id = saved.Id,
            agent = saved.Agent,
            topic = saved.Topic,
            created_at = saved.CreatedAt,
            message = $"Session handoff #{saved.Id} saved. The next agent can resume by calling memory_handoff_latest."
        }, JsonOptions);
    }

    [McpServerTool(Name = "memory_handoff_latest")]
    [Description("""
        Retrieve the latest session handoff snapshot recorded by any AI agent.
        Call this at the beginning of a session or when resuming work to understand what the previous
        agent completed, active files, and the exact next steps.
        """)]
    public static string GetLatestHandoff(
        [Description("Optional filter by agent name (e.g. 'Claude', 'Antigravity').")]
        string? agent = null)
    {
        using var db = Open();
        var latest = HandoffStore.GetLatest(db, agent);

        if (latest is null)
        {
            return JsonSerializer.Serialize(new
            {
                status = "none",
                message = "No session handoffs recorded yet."
            }, JsonOptions);
        }

        return JsonSerializer.Serialize(new
        {
            status = "found",
            handoff = latest
        }, JsonOptions);
    }

    [McpServerTool(Name = "memory_handoff_list")]
    [Description("List past session handoffs across all AI agents.")]
    public static string ListHandoffs(
        [Description("Maximum number of handoffs to return. Default 5.")]
        int limit = 5,
        [Description("Optional filter by agent name.")]
        string? agent = null)
    {
        using var db = Open();
        var handoffs = HandoffStore.List(db, limit > 0 ? limit : 5, agent);

        return JsonSerializer.Serialize(new
        {
            status = "found",
            count = handoffs.Count,
            handoffs = handoffs
        }, JsonOptions);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
