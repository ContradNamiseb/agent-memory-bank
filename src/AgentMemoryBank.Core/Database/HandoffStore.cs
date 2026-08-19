using Microsoft.Data.Sqlite;
using AgentMemoryBank.Models;

namespace AgentMemoryBank.Database;

/// <summary>
/// Persistence for cross-agent session handoffs and context snapshots.
/// </summary>
public static class HandoffStore
{
    public static SessionHandoff Save(SqliteConnection db, SessionHandoffDraft draft)
    {
        using var cmd = db.Prepare("""
            INSERT INTO session_handoff (agent, topic, summary, next_steps, active_files, open_questions, context_data)
            VALUES ($agent, $topic, $summary, $next_steps, $active_files, $open_questions, $context_data)
            RETURNING id, agent, topic, summary, next_steps, active_files, open_questions, context_data, created_at;
            """, "$agent", "$topic", "$summary", "$next_steps", "$active_files", "$open_questions", "$context_data");

        return cmd.SetAll(
            draft.Agent.Trim(),
            draft.Topic.Trim(),
            draft.Summary.Trim(),
            draft.NextSteps.Trim(),
            draft.ActiveFiles?.Trim(),
            draft.OpenQuestions?.Trim(),
            draft.ContextData?.Trim())
            .Query(r => new SessionHandoff
            {
                Id = r.GetInt64("id"),
                Agent = r.GetString("agent"),
                Topic = r.GetString("topic"),
                Summary = r.GetString("summary"),
                NextSteps = r.GetString("next_steps"),
                ActiveFiles = r.GetStringOrNull("active_files"),
                OpenQuestions = r.GetStringOrNull("open_questions"),
                ContextData = r.GetStringOrNull("context_data"),
                CreatedAt = r.GetString("created_at")
            })
            .First();
    }

    public static SessionHandoff? GetLatest(SqliteConnection db, string? agent = null)
    {
        using var cmd = db.Prepare("""
            SELECT id, agent, topic, summary, next_steps, active_files, open_questions, context_data, created_at
            FROM session_handoff
            WHERE ($agent IS NULL OR agent LIKE '%' || $agent || '%')
            ORDER BY id DESC
            LIMIT 1;
            """, "$agent");

        return cmd.SetAll(agent)
            .Query(r => new SessionHandoff
            {
                Id = r.GetInt64("id"),
                Agent = r.GetString("agent"),
                Topic = r.GetString("topic"),
                Summary = r.GetString("summary"),
                NextSteps = r.GetString("next_steps"),
                ActiveFiles = r.GetStringOrNull("active_files"),
                OpenQuestions = r.GetStringOrNull("open_questions"),
                ContextData = r.GetStringOrNull("context_data"),
                CreatedAt = r.GetString("created_at")
            })
            .FirstOrDefault();
    }

    public static IReadOnlyList<SessionHandoff> List(SqliteConnection db, int limit = 10, string? agent = null)
    {
        using var cmd = db.Prepare("""
            SELECT id, agent, topic, summary, next_steps, active_files, open_questions, context_data, created_at
            FROM session_handoff
            WHERE ($agent IS NULL OR agent LIKE '%' || $agent || '%')
            ORDER BY id DESC
            LIMIT $limit;
            """, "$agent", "$limit");

        return cmd.SetAll(agent, limit)
            .Query(r => new SessionHandoff
            {
                Id = r.GetInt64("id"),
                Agent = r.GetString("agent"),
                Topic = r.GetString("topic"),
                Summary = r.GetString("summary"),
                NextSteps = r.GetString("next_steps"),
                ActiveFiles = r.GetStringOrNull("active_files"),
                OpenQuestions = r.GetStringOrNull("open_questions"),
                ContextData = r.GetStringOrNull("context_data"),
                CreatedAt = r.GetString("created_at")
            });
    }
}
