using Microsoft.Data.Sqlite;

namespace AgentMemoryBank.Database;

/// <summary>
/// Brings an existing database up to the current schema.
///
/// <para><c>Schema.sql</c> is idempotent, which makes a fresh database correct
/// and an existing one frozen: <c>CREATE TABLE IF NOT EXISTS</c> is a no-op the
/// moment the table exists, so a column added to that file never reaches a store
/// anyone has already been using. The failure is quiet at open and loud later —
/// the store opens fine, then every write naming the new column fails with "no
/// such column". Without this, the schema could not change at all without asking
/// people to delete their notes, which for a store whose whole value is
/// accumulated history is the same as asking them to stop using it.</para>
///
/// <para>Migrations are keyed on <c>PRAGMA user_version</c> but do not trust it:
/// every step checks the catalogue and skips work already present. Databases
/// created before versioning existed all report 0 while differing in what they
/// actually contain, so a step that assumed the version number would fail on
/// exactly the stores this exists to rescue.</para>
///
/// <para>v1 adds no columns. It stamps the version so the first real migration
/// has a known floor to start from, and it exists now rather than later because
/// the mechanism has to predate the change that needs it.</para>
///
/// <para>v2 records on each session handoff the checkout it was written in, so
/// a handoff read on another branch, worktree or clone can say so.</para>
/// </summary>
public static class Migrations
{
    /// <summary>Bump when adding a step below.</summary>
    public const int CurrentVersion = 2;

    /// <summary>Apply every step the database has not had, in order.</summary>
    public static void Apply(SqliteConnection db)
    {
        var version = db.Scalar<long>("PRAGMA user_version");
        if (version >= CurrentVersion) return;

        // v1: no schema change. Establishes the version floor; see the class note.

        // v2: the checkout a handoff was written in. Existing handoffs keep NULLs,
        // which reads back as "not recorded" rather than as a false match.
        foreach (var (column, type) in HandoffCheckoutColumns)
            AddColumnIfMissing(db, "session_handoff", column, type);
        db.Execute("CREATE INDEX IF NOT EXISTS handoff_by_repo ON session_handoff(repo_id, id DESC)");
        db.Execute("CREATE INDEX IF NOT EXISTS handoff_by_worktree ON session_handoff(worktree, id DESC)");

        db.Execute($"PRAGMA user_version = {CurrentVersion}");
    }

    private static readonly (string Column, string Type)[] HandoffCheckoutColumns =
    [
        ("repo_id", "TEXT"), ("worktree", "TEXT"), ("branch", "TEXT"), ("head_commit", "TEXT"),
        ("dirty_paths", "INTEGER"), ("upstream", "TEXT"), ("ahead", "INTEGER"),
    ];

    /// <summary>True when the table already has the column.</summary>
    public static bool ColumnExists(SqliteConnection db, string table, string column)
    {
        using var cmd = db.CreateCommand();
        // PRAGMA does not take a bound parameter in this position; table names
        // passed here are compile-time literals from the steps above, never input.
        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $column";
        cmd.Parameters.AddWithValue("$column", column);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    /// <summary>
    /// Add a column unless it is already there, so a step is safe to re-run and
    /// safe on a database created fresh from the current Schema.sql, which
    /// already has every column a step adds.
    /// </summary>
    public static void AddColumnIfMissing(SqliteConnection db, string table, string column, string type)
    {
        if (!TableExists(db, table)) return;
        if (ColumnExists(db, table, column)) return;
        db.Execute($"ALTER TABLE {table} ADD COLUMN {column} {type}");
    }

    private static bool TableExists(SqliteConnection db, string table)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $table";
        cmd.Parameters.AddWithValue("$table", table);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }
}
