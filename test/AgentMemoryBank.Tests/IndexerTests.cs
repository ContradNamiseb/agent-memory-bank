using AgentMemoryBank.Database;
using AgentMemoryBank.Indexing;
using Xunit;

namespace AgentMemoryBank.Tests;

public class IndexerTests
{
    private const string SampleCpp = """
        namespace training {

        // Fitted to real measured outcomes, not chosen by eye.
        struct Options {
          float retry_backoff = 1.5f;
          float retry_jitter = 0.25f;
        };

        }  // namespace training
        """;

    /// <summary>
    /// Fixed options rather than whatever this machine's memory-bank.local.json
    /// happens to say, so the suite indexes the same thing on every machine.
    /// </summary>
    private static readonly IndexOptions TestOptions = new()
    {
        VendoredDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "vendor", "third_party" },
    };

    private static RepoIndexResult Index(TempRepo repo, Microsoft.Data.Sqlite.SqliteConnection db, bool full = false) =>
        Indexer.IndexRepo(db, repo.RepoName, repo.RepoDirectory, full, onFile: null, options: TestOptions);

    [Fact]
    public void Indexes_tracked_files_and_records_the_revision()
    {
        using var repo = new TempRepo();
        repo.Write("src/options.h", SampleCpp);
        repo.Write("README.md", "# Title\n\nbody text about retry_jitter\n");
        repo.Commit();

        using var db = MemoryDatabase.Open(repo.DbPath);
        var result = Index(repo, db);

        Assert.Null(result.Error);
        Assert.Equal(2, result.Indexed);
        Assert.Equal(0, result.Skipped);
        Assert.True(result.Chunks >= 2);
        Assert.NotNull(result.GitRev);

        var stats = MemorySearch.Stats(db);
        Assert.Equal(2, stats.Files);
        Assert.Equal(result.Chunks, stats.Chunks);
    }

    [Fact]
    public void Untracked_vendored_and_binary_files_are_left_out()
    {
        using var repo = new TempRepo();
        repo.Write("src/options.h", SampleCpp);
        repo.Write("vendor/somelib/vendored.cc", "int vendored() { return 1; }");
        repo.Write("docs/manual.pdf", "%PDF-1.4 fake");
        repo.Commit();
        File.WriteAllText(Path.Combine(repo.RepoDirectory, "untracked.cc"), "int loose() { return 0; }");

        using var db = MemoryDatabase.Open(repo.DbPath);
        Index(repo, db);

        using var paths = db.Prepare("SELECT path FROM file ORDER BY path");
        Assert.Equal(["src/options.h"], paths.Query(r => r.GetString("path")));
    }

    [Fact]
    public void Unchanged_files_are_skipped_on_the_next_pass()
    {
        using var repo = new TempRepo();
        repo.Write("src/options.h", SampleCpp);
        repo.Commit();

        using var db = MemoryDatabase.Open(repo.DbPath);
        Index(repo, db);
        var second = Index(repo, db);

        Assert.Equal(0, second.Indexed);
        Assert.Equal(1, second.Skipped);
        Assert.Equal(0, second.Chunks);

        // --full re-chunks regardless of the hash.
        var forced = Index(repo, db, full: true);
        Assert.Equal(1, forced.Indexed);
    }

    [Fact]
    public void Editing_a_file_replaces_its_chunks_and_its_search_index()
    {
        using var repo = new TempRepo();
        repo.Write("src/options.h", SampleCpp);
        repo.Commit();

        using var db = MemoryDatabase.Open(repo.DbPath);
        Index(repo, db);
        Assert.NotEmpty(MemorySearch.SearchChunks(db, "retry_jitter"));

        repo.Write("src/options.h", "struct Options {\n  float renamed_knob = 0.5f;\n};\n");
        repo.Commit("edit");
        var second = Index(repo, db);

        Assert.Equal(1, second.Indexed);
        Assert.Equal(1, MemorySearch.Stats(db).Files);
        // The stale text must be gone from FTS, not merely superseded in the table.
        Assert.Empty(MemorySearch.SearchChunks(db, "retry_jitter"));
        Assert.NotEmpty(MemorySearch.SearchChunks(db, "renamed_knob"));
    }

    [Fact]
    public void Deleting_a_file_removes_its_rows_and_leaves_no_ghosts_in_fts()
    {
        using var repo = new TempRepo();
        repo.Write("src/options.h", SampleCpp);
        repo.Write("README.md", "# Title\n\nunrelated prose\n");
        repo.Commit();

        using var db = MemoryDatabase.Open(repo.DbPath);
        Index(repo, db);

        repo.Delete("src/options.h");
        repo.Commit("remove");
        var second = Index(repo, db);

        Assert.Equal(1, second.Removed);
        Assert.Equal(1, MemorySearch.Stats(db).Files);
        Assert.Equal(0, db.Scalar<int>(
            "SELECT count(*) FROM chunk c LEFT JOIN file f ON f.id = c.file_id WHERE f.id IS NULL"));
        Assert.Empty(MemorySearch.SearchChunks(db, "retry_jitter"));
    }

    [Fact]
    public void A_missing_repository_is_reported_without_aborting_the_run()
    {
        using var repo = new TempRepo();
        repo.Write("src/options.h", SampleCpp);
        repo.Commit();

        using var db = MemoryDatabase.Open(repo.DbPath);
        var run = Indexer.Reindex(db, repoRoot: repo.Root, repos: ["does-not-exist", repo.RepoName],
            options: TestOptions);

        Assert.Equal(2, run.Results.Count);
        Assert.NotNull(run.Results[0].Error);
        Assert.Null(run.Results[1].Error);
        Assert.Equal(1, run.Results[1].Indexed);
    }

    [Fact]
    public void Every_run_is_recorded_with_its_note_and_revisions()
    {
        using var repo = new TempRepo();
        repo.Write("src/options.h", SampleCpp);
        repo.Commit();

        using var db = MemoryDatabase.Open(repo.DbPath);
        var run = Indexer.Reindex(db, repoRoot: repo.Root, repos: [repo.RepoName], note: "why this pass ran",
            options: TestOptions);

        using var cmd = db.Prepare("SELECT tool, git_rev, note FROM run WHERE id = $id", "$id");
        var recorded = Assert.Single(cmd.SetAll(run.RunId)
            .Query(r => (Tool: r.GetString("tool"), Rev: r.GetString("git_rev"), Note: r.GetString("note"))));

        Assert.Equal("phase-a-indexer", recorded.Tool);
        Assert.Equal("why this pass ran", recorded.Note);
        Assert.StartsWith($"{repo.RepoName}@", recorded.Rev);
    }
}
