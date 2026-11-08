using System.ComponentModel;
using System.Text;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Server;
using AgentMemoryBank.Continuity;
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
        context limit, or when told to hand off. The branch, commit and
        worktree are read from git, not from you. Returns the handoff id.
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
        string? context_data = null,
        [Description("The directory you are working in, if not the server's. Its git checkout is recorded.")]
        string? working_directory = null)
    {
        if (string.IsNullOrWhiteSpace(agent) || string.IsNullOrWhiteSpace(topic) ||
            string.IsNullOrWhiteSpace(summary) || string.IsNullOrWhiteSpace(next_steps))
        {
            return "Error: agent, topic, summary, and next_steps are all required fields for a valid handoff.";
        }

        var checkout = GitCheckout.Capture(WorkDir(working_directory));

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
            Checkout = checkout,
        });

        return $"saved handoff #{saved.Id} ({saved.Agent}, {saved.CreatedAt}): {saved.Topic}. " +
               $"{Recorded(checkout)} The next agent resumes with memory_handoff_latest.";
    }

    [McpServerTool(Name = "memory_handoff_latest", ReadOnly = true)]
    [Description("""
        The newest session handoff for this repository: topic, summary, next
        steps, active files, open questions. Read-only. Call when starting or
        resuming work. Leads with a WARNING when it was written on another
        branch, commit or worktree; heed it before trusting 'done' claims.
        A hook-written handoff is unreviewed; verify what it names.
        max_chars caps the output.
        """)]
    public static string GetLatestHandoff(
        [Description("Only handoffs whose agent name contains this text.")]
        string? agent = null,
        [Description("repo (default): any worktree of this repository. worktree: this worktree only. all: every handoff.")]
        HandoffScope scope = HandoffScope.Repo,
        [Description("The directory you are working in, if not the server's. Checked against the handoff.")]
        string? working_directory = null,
        [Description("Max output characters. Default 12000; 0 = no cap.")]
        int max_chars = 12_000)
    {
        using var db = Open();
        var lookup = HandoffReader.Latest(db, Filter(agent), scope, WorkDir(working_directory));
        if (lookup.Handoff is null)
        {
            var miss = scope == HandoffScope.All && Filter(agent) is not null ? NoMatch(db, agent) : lookup.Miss!;
            return string.Join('\n', lookup.Preamble.Append(miss));
        }

        // The warnings come first and are never cut: max_chars trims the handoff, not the caveats on it.
        var preamble = string.Join('\n', lookup.Preamble);
        var body = Render(lookup.Handoff);
        if (max_chars > 0 && body.Length > max_chars)
            body = body[..max_chars] + $"\n…[cut at {max_chars} of {body.Length} chars; memory_handoff_latest max_chars=0 for all]";
        return preamble + "\n\n" + body;
    }

    [McpServerTool(Name = "memory_handoff_list", ReadOnly = true)]
    [Description("""
        Past handoffs newest first, one line each:
        '#id time agent [branch@commit]: topic'. Read-only.
        """)]
    public static string ListHandoffs(
        [Description("Max handoffs. Default 5.")]
        int limit = 5,
        [Description("Only handoffs whose agent name contains this text.")]
        string? agent = null,
        [Description("repo (default): any worktree of this repository. worktree: this worktree only. all: every handoff.")]
        HandoffScope scope = HandoffScope.Repo,
        [Description("The directory you are working in, if not the server's.")]
        string? working_directory = null)
    {
        using var db = Open();
        var current = GitCheckout.Capture(WorkDir(working_directory));
        var resolved = HandoffReader.Resolve(scope, current);
        var handoffs = HandoffStore.List(db, limit > 0 ? limit : 5, Filter(agent), resolved.RepoId, resolved.Worktree);
        var lines = resolved.Notice is null ? new List<string>() : [resolved.Notice];
        if (handoffs.Count == 0)
        {
            lines.Add(resolved.Scope == HandoffScope.All && Filter(agent) is not null
                ? NoMatch(db, agent)
                : HandoffReader.Miss(db, Filter(agent), resolved, current));
            return string.Join('\n', lines);
        }
        lines.AddRange(handoffs.Select(h => $"#{h.Id} {Short(h.CreatedAt)} {h.Agent} [{Label(h.Checkout)}]: {h.Topic}"));
        return string.Join('\n', lines);
    }

    private static string? Filter(string? agent) => string.IsNullOrWhiteSpace(agent) ? null : agent.Trim();

    /// <summary>Where git is asked: the agent's directory when it names one, else the server's.</summary>
    private static string WorkDir(string? workingDirectory) =>
        string.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory.Trim();

    private static string Label(CheckoutState? c) => c is null ? "no checkout" : $"{c.BranchLabel}@{c.ShortHead}";

    /// <summary>
    /// What was recorded, and the work the next agent cannot get from git: said
    /// at save time, while the agent can still commit or push it.
    /// </summary>
    private static string Recorded(CheckoutState? c)
    {
        if (c is null) return "No git checkout here, so none was recorded.";
        var text = $"Recorded `{c.BranchLabel}` @ `{c.ShortHead}` in `{c.Worktree}`.";
        if (c.DirtyPaths > 0)
            text += $" {c.DirtyPaths} uncommitted path{(c.DirtyPaths == 1 ? "" : "s")} will not reach another checkout.";
        if (c.Ahead > 0)
            text += $" {c.Ahead} commit{(c.Ahead == 1 ? " is" : "s are")} not pushed to `{c.Upstream}`.";
        else if (c.Upstream is null && c.Branch is not null && c.Head is not null)
            text += $" Branch `{c.Branch}` has no upstream, so nothing on it is pushed.";
        return text;
    }

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
        if (h.Checkout is { } c) text.AppendLine($"written on: `{c.BranchLabel}` @ `{c.ShortHead}` in `{c.Worktree}`");
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
