using System.Reflection;
using Microsoft.Data.Sqlite;

namespace AgentMemoryBank.Database;

/// <summary>Raised when the environment cannot support the store.</summary>
public sealed class MemoryBankException(string message) : Exception(message);

/// <summary>Opening the database, applying the schema, and recording runs.</summary>
public static class MemoryDatabase
{
    /// <summary>
    /// Open (creating if needed) the memory bank database with the schema applied.
    ///
    /// WAL plus a busy timeout so several editors can share one file: SQLite
    /// handles many readers fine, but concurrent writers hit SQLITE_BUSY. Writes
    /// are rare (notes only), so this is enough and no server process is needed.
    /// </summary>
    public static SqliteConnection Open(string dbPath)
    {
        RequireFts5();

        if (dbPath != ":memory:")
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Pooling off: a pooled connection can outlive Close() and keep the
            // -wal file locked, which breaks test cleanup on Windows.
            Pooling = false,
        }.ToString());

        try
        {
            db.Open();
        }
        catch (SqliteException e)
        {
            // SQLite reports only "unable to open database file", which says
            // nothing about which file it tried or why the path was chosen.
            db.Dispose();
            throw new MemoryBankException(
                $"could not open the memory bank database at '{Path.GetFullPath(dbPath)}' ({e.Message}). " +
                "Pass --db to choose a path, or set MEMORY_BANK_DB.");
        }

        // An in-memory database has no journal to switch and no other writers.
        if (dbPath != ":memory:") db.Execute("PRAGMA journal_mode = WAL");
        db.Execute("PRAGMA busy_timeout = 5000");
        db.Execute("PRAGMA foreign_keys = ON");
        db.Execute("PRAGMA synchronous = NORMAL");
        db.Execute(SchemaSql);

        // Schema.sql only ever creates what is missing, so an existing database
        // would otherwise stay on whatever shape it was first created with.
        Migrations.Apply(db);
        return db;
    }

    /// <summary>The schema, embedded so a published binary carries it.</summary>
    public static string SchemaSql
    {
        get
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames().Single(n => n.EndsWith("Schema.sql", StringComparison.Ordinal));
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    /// <summary>
    /// Check the capability, not a version string.
    ///
    /// Microsoft.Data.Sqlite normally bundles a native SQLite with FTS5 compiled
    /// in, but a host that supplies its own sqlite3 library may not have it. In
    /// that case the connection opens happily and only the virtual table fails,
    /// so the probe has to go all the way through bm25.
    /// </summary>
    public static void RequireFts5()
    {
        try
        {
            using var probe = new SqliteConnection("Data Source=:memory:");
            probe.Open();
            probe.Execute("CREATE VIRTUAL TABLE probe_fts USING fts5(body)");
            probe.Execute("INSERT INTO probe_fts(body) VALUES ('probe')");
            using var cmd = probe.CreateCommand();
            cmd.CommandText = "SELECT bm25(probe_fts) FROM probe_fts WHERE probe_fts MATCH 'probe'";
            cmd.ExecuteScalar();
        }
        catch (Exception e)
        {
            throw new MemoryBankException(
                "memory-bank needs SQLite with FTS5 and bm25, which this runtime could not provide " +
                $"({e.Message}). Microsoft.Data.Sqlite ships a bundled SQLite that has FTS5 enabled; " +
                "check that no other SQLitePCLRaw provider is overriding it.");
        }
    }

    /// <summary>Record an ingestion run; the id is stamped on what the run writes.</summary>
    public static long StartRun(SqliteConnection db, string tool, string? gitRev = null, string? note = null)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO run(started_at, tool, git_rev, note)
            VALUES ($started, $tool, $rev, $note)
            RETURNING id
            """;
        cmd.Parameters.AddWithValue("$started", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$tool", tool);
        cmd.Parameters.AddWithValue("$rev", (object?)gitRev ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
        return (long)cmd.ExecuteScalar()!;
    }
}
