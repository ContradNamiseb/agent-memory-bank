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
/// </summary>
public static class Migrations
{
    /// <summary>Bump when adding a step below.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Apply every step the database has not had, in order.</summary>
    public static void Apply(SqliteConnection db)
    {
        var version = db.Scalar<long>("PRAGMA user_version");
        if (version >= CurrentVersion) return;

        // v1: no schema change. Establishes the version floor; see the class note.

        db.Execute($"PRAGMA user_version = {CurrentVersion}");
    }

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
    /// safe on a database created fresh from the current Schema.sql. Unused at
    /// v1 and kept deliberately: it is the operation every future step will
    /// need, and writing it under time pressure is how the ALTER gets run
    /// unguarded against a store that already has the column.
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
