using AgentMemoryBank.Database;
using AgentMemoryBank.Models;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentMemoryBank.Tests;

/// <summary>
/// The case that matters is not a fresh database — that one is correct by
/// construction from Schema.sql. It is the store someone has been writing notes
/// into for months, which <c>CREATE TABLE IF NOT EXISTS</c> silently declines to
/// change. These tests build a pre-migration database on purpose and then open it
/// through the real entry point.
///
/// <para>v1 changes no columns, so what is under test is the mechanism itself:
/// that an old store is recognised, stamped, and left with its data intact, and
/// that the column-adding primitive future steps depend on is correct before
/// anything relies on it.</para>
/// </summary>
public sealed class MigrationTests
{
    /// <summary>
    /// The note table as it stands, plus the run table its foreign key points at —
    /// a note row cannot be written without it. Created directly rather than via
    /// Schema.sql so the database starts unversioned, the way a real one did.
    /// </summary>
    private const string LegacySchema = """
        CREATE TABLE run(
          id INTEGER PRIMARY KEY, started_at TEXT, tool TEXT, git_rev TEXT, note TEXT);
        CREATE TABLE note(
          id INTEGER PRIMARY KEY,
          claim TEXT NOT NULL,
          rationale TEXT,
          evidence TEXT,
          anchor_symbol TEXT,
          anchor_text TEXT,
          kind TEXT,
          confidence TEXT,
          created_at TEXT, verified_at TEXT,
          superseded_by INTEGER REFERENCES note(id),
          run_id INTEGER REFERENCES run(id));
        """;

    private static void CreateLegacyStore(string dbPath, string? seedClaim = null)
    {
        using var legacy = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
        legacy.Open();
        legacy.Execute(LegacySchema);
        if (seedClaim is not null)
        {
            using var insert = legacy.Prepare("INSERT INTO note(claim, evidence) VALUES ($claim, $ev)",
                "$claim", "$ev");
            insert.SetAll(seedClaim, "py measure.py -> 1").ExecuteNonQuery();
        }
        Assert.Equal(0, legacy.Scalar<long>("PRAGMA user_version"));
    }

    [Fact]
    public void An_unversioned_database_is_stamped_on_open_without_losing_data()
    {
        using var repo = new TempRepo();
        CreateLegacyStore(repo.DbPath, seedClaim: "a pre-existing claim");

        using var db = MemoryDatabase.Open(repo.DbPath);

        Assert.Equal(Migrations.CurrentVersion, db.Scalar<long>("PRAGMA user_version"));
        Assert.Equal(1, db.Scalar<long>("SELECT COUNT(*) FROM note WHERE claim = 'a pre-existing claim'"));
        Assert.Equal("ok", db.Scalar<string>("PRAGMA integrity_check"));
    }

    [Fact]
    public void Opening_twice_is_a_no_op_the_second_time()
    {
        // A migration that is not safe to re-run turns every open into a gamble,
        // and this store is opened once per MCP call.
        using var repo = new TempRepo();

        using (var first = MemoryDatabase.Open(repo.DbPath))
            Assert.Equal(Migrations.CurrentVersion, first.Scalar<long>("PRAGMA user_version"));

        using var second = MemoryDatabase.Open(repo.DbPath);
        Assert.Equal(Migrations.CurrentVersion, second.Scalar<long>("PRAGMA user_version"));

        Migrations.Apply(second);   // explicitly re-run: must be harmless
        Assert.Equal(Migrations.CurrentVersion, second.Scalar<long>("PRAGMA user_version"));
    }

    [Fact]
    public void AddColumnIfMissing_adds_once_and_is_safe_to_repeat()
    {
        // Nothing calls this at v1. It is the primitive every future step will use,
        // so it is verified now rather than in the same change that first needs it.
        using var repo = new TempRepo();
        CreateLegacyStore(repo.DbPath);

        using var db = MemoryDatabase.Open(repo.DbPath);

        Assert.False(Migrations.ColumnExists(db, "note", "probe_column"));

        Migrations.AddColumnIfMissing(db, "note", "probe_column", "TEXT");
        Assert.True(Migrations.ColumnExists(db, "note", "probe_column"));

        // The second call is the one that would throw "duplicate column name".
        Migrations.AddColumnIfMissing(db, "note", "probe_column", "TEXT");
        Assert.True(Migrations.ColumnExists(db, "note", "probe_column"));

        // A table that does not exist is skipped rather than throwing, so a step
        // written for a later schema cannot break an older store.
        Migrations.AddColumnIfMissing(db, "table_that_does_not_exist", "c", "TEXT");
    }

    [Fact]
    public void Writes_and_reads_still_work_after_an_upgrade()
    {
        using var repo = new TempRepo();
        CreateLegacyStore(repo.DbPath);

        using var db = MemoryDatabase.Open(repo.DbPath);

        var result = NoteStore.Add(db, new NoteDraft
        {
            Claim = "an upgraded store still accepts writes and returns them from search",
            Evidence = "dotnet test --filter MigrationTests -> green",
            Confidence = Confidence.Measured,
        }, repo.RepoDirectory);

        var accepted = Assert.IsType<NoteWriteResult.Accepted>(result);
        Assert.True(accepted.Id > 0);

        var found = MemorySearch.SearchNotes(db, "upgraded store", limit: 5);
        Assert.Contains(found, n => n.Id == accepted.Id);
    }
}
