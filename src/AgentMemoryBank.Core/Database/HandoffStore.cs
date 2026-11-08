using Microsoft.Data.Sqlite;
using AgentMemoryBank.Models;

namespace AgentMemoryBank.Database;

/// <summary>
/// Persistence for cross-agent session handoffs and context snapshots.
/// </summary>
public static class HandoffStore
{
    private const string Columns = """
        id, agent, topic, summary, next_steps, active_files, open_questions, context_data, created_at,
        repo_id, worktree, branch, head_commit, dirty_paths, upstream, ahead
        """;

    /// <summary>
    /// Paths compare as the file system does. On Windows a drive letter or folder
    /// can come back in either case, and a scope that missed on that would hide
    /// the handoff it exists to find.
    /// </summary>
    private static readonly string PathCollation = OperatingSystem.IsWindows() ? " COLLATE NOCASE" : "";

    public static SessionHandoff Save(SqliteConnection db, SessionHandoffDraft draft)
    {
        var checkout = draft.Checkout;
        using var cmd = db.Prepare($"""
            INSERT INTO session_handoff (agent, topic, summary, next_steps, active_files, open_questions, context_data,
                                         repo_id, worktree, branch, head_commit, dirty_paths, upstream, ahead)
            VALUES ($agent, $topic, $summary, $next_steps, $active_files, $open_questions, $context_data,
                    $repo_id, $worktree, $branch, $head_commit, $dirty_paths, $upstream, $ahead)
            RETURNING {Columns};
            """, "$agent", "$topic", "$summary", "$next_steps", "$active_files", "$open_questions", "$context_data",
                 "$repo_id", "$worktree", "$branch", "$head_commit", "$dirty_paths", "$upstream", "$ahead");

        return cmd.SetAll(
            draft.Agent.Trim(),
            draft.Topic.Trim(),
            draft.Summary.Trim(),
            draft.NextSteps.Trim(),
            draft.ActiveFiles?.Trim(),
            draft.OpenQuestions?.Trim(),
            draft.ContextData?.Trim(),
            checkout?.RepoId,
            checkout?.Worktree,
            checkout?.Branch,
            checkout?.Head,
            checkout?.DirtyPaths,
            checkout?.Upstream,
            checkout?.Ahead)
            .Query(Read)
            .First();
    }

    /// <summary>
    /// The newest handoff matching every filter given. <paramref name="repoId"/>
    /// and <paramref name="worktree"/> narrow to handoffs written in that
    /// repository or working tree; handoffs with no recorded checkout never match them.
    /// </summary>
    public static SessionHandoff? GetLatest(
        SqliteConnection db, string? agent = null, string? repoId = null, string? worktree = null) =>
        List(db, 1, agent, repoId, worktree).FirstOrDefault();

    public static IReadOnlyList<SessionHandoff> List(
        SqliteConnection db, int limit = 10, string? agent = null, string? repoId = null, string? worktree = null)
    {
        using var cmd = db.Prepare($"""
            SELECT {Columns}
            FROM session_handoff
            WHERE ($agent IS NULL OR agent LIKE '%' || $agent || '%')
              AND ($repo_id IS NULL OR repo_id = $repo_id{PathCollation})
              AND ($worktree IS NULL OR worktree = $worktree{PathCollation})
            ORDER BY id DESC
            LIMIT $limit;
            """, "$agent", "$repo_id", "$worktree", "$limit");

        return cmd.SetAll(agent, repoId, worktree, limit).Query(Read);
    }

    /// <summary>Every handoff, or every one from a matching agent. Says whether a scoped miss hid any.</summary>
    public static long Count(SqliteConnection db, string? agent = null)
    {
        using var cmd = db.Prepare(
            "SELECT COUNT(*) FROM session_handoff WHERE ($agent IS NULL OR agent LIKE '%' || $agent || '%')",
            "$agent");
        return Convert.ToInt64(cmd.SetAll(agent).ExecuteScalar());
    }

    private static SessionHandoff Read(SqliteDataReader r)
    {
        var repoId = r.GetStringOrNull("repo_id");
        var worktree = r.GetStringOrNull("worktree");

        return new SessionHandoff
        {
            Id = r.GetInt64("id"),
            Agent = r.GetString("agent"),
            Topic = r.GetString("topic"),
            Summary = r.GetString("summary"),
            NextSteps = r.GetString("next_steps"),
            ActiveFiles = r.GetStringOrNull("active_files"),
            OpenQuestions = r.GetStringOrNull("open_questions"),
            ContextData = r.GetStringOrNull("context_data"),
            CreatedAt = r.GetString("created_at"),
            Checkout = repoId is null || worktree is null
                ? null
                : new CheckoutState
                {
                    RepoId = repoId,
                    Worktree = worktree,
                    Branch = r.GetStringOrNull("branch"),
                    Head = r.GetStringOrNull("head_commit"),
                    DirtyPaths = IntOrNull(r, "dirty_paths") ?? 0,
                    Upstream = r.GetStringOrNull("upstream"),
                    Ahead = IntOrNull(r, "ahead"),
                },
        };
    }

    private static int? IntOrNull(SqliteDataReader r, string column)
    {
        var ordinal = r.GetOrdinal(column);
        return r.IsDBNull(ordinal) ? null : r.GetInt32(ordinal);
    }
}
