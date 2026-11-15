using AgentMemoryBank.Database;
using AgentMemoryBank.Indexing;
using Xunit;

namespace AgentMemoryBank.Tests;

public class SearchTests
{
    [Theory]
    [InlineData("retry_jitter", "\"retry_jitter\"")]
    [InlineData("  cache_pool_size  ", "\"cache_pool_size\"")]
    [InlineData("drain queue timeout", "\"drain\" \"queue\" \"timeout\"")]
    // A stray operator character in a code query must not be read as FTS syntax.
    [InlineData("foo(bar)*", "\"foo(bar)*\"")]
    public void Queries_are_quoted_into_phrases(string query, string expected) =>
        Assert.Equal(expected, MemorySearch.BuildMatchExpression(query));

    [Theory]
    [InlineData("\"exact phrase\"")]
    [InlineData("retry OR jitter")]
    [InlineData("NEAR(retry jitter, 5)")]
    public void Deliberate_advanced_queries_pass_through(string query) =>
        Assert.Equal(query, MemorySearch.BuildMatchExpression(query));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("*")]
    public void Queries_with_nothing_searchable_are_rejected(string query) =>
        Assert.Throws<ArgumentException>(() => MemorySearch.BuildMatchExpression(query));

    [Fact]
    public void An_identifier_matches_as_a_phrase_not_as_two_loose_words()
    {
        using var repo = new TempRepo();
        repo.Write("src/options.h", "struct Options {\n  float retry_jitter = 0.25f;\n};\n");
        // Mentions both words, but not the identifier: it must not outrank the definition.
        repo.Write("notes.md", "# Notes\n\nThe retry model has a jitter parameter somewhere.\n");
        repo.Commit();

        using var db = MemoryDatabase.Open(repo.DbPath);
        Indexer.IndexRepo(db, repo.RepoName, repo.RepoDirectory);

        var hits = MemorySearch.SearchChunks(db, "retry_jitter");
        var hit = Assert.Single(hits);
        Assert.Equal("src/options.h", hit.Path);
        Assert.Contains("retry_jitter", hit.Body(db));
    }

    [Fact]
    public void Hits_carry_a_citation_that_resolves_to_the_right_lines()
    {
        using var repo = new TempRepo();
        repo.Write("src/options.h", "// header\nstruct Options {\n  float retry_jitter = 0.25f;\n};\n");
        repo.Commit();

        using var db = MemoryDatabase.Open(repo.DbPath);
        Indexer.IndexRepo(db, repo.RepoName, repo.RepoDirectory);

        var hit = Assert.Single(MemorySearch.SearchChunks(db, "retry_jitter"));
        Assert.Equal($"{repo.RepoName}/src/options.h:{hit.LineStart}", hit.Citation);

        var stored = MemorySearch.GetChunk(db, hit.Id);
        Assert.NotNull(stored);

        var fileLines = File.ReadAllText(Path.Combine(repo.RepoDirectory, "src/options.h"))
            .Replace("\r\n", "\n").Split('\n');
        var cited = string.Join('\n', fileLines
            .Skip(stored.Value.Chunk.LineStart - 1)
            .Take(stored.Value.Chunk.LineCount));
        Assert.Equal(cited, stored.Value.Chunk.Body);
    }

    [Fact]
    public void Searches_can_be_scoped_to_one_repository()
    {
        using var repo = new TempRepo();
        repo.Write("src/options.h", "struct Options {\n  float retry_jitter = 0.25f;\n};\n");
        repo.Commit();

        using var db = MemoryDatabase.Open(repo.DbPath);
        Indexer.IndexRepo(db, repo.RepoName, repo.RepoDirectory);

        Assert.NotEmpty(MemorySearch.SearchChunks(db, "retry_jitter", repo: repo.RepoName));
        Assert.Empty(MemorySearch.SearchChunks(db, "retry_jitter", repo: "some-other-repo"));
    }

    [Fact]
    public void Combined_search_with_repo_filter_scopes_both_notes_and_chunks()
    {
        using var repo = new TempRepo();
        repo.Write("src/options.h", "struct Options {\n  float retry_jitter = 0.25f;\n};\n");
        repo.Commit();

        var otherDir = Path.Combine(repo.Root, "other");
        var otherFile = Path.Combine(otherDir, "src", "other.h");
        Directory.CreateDirectory(Path.GetDirectoryName(otherFile)!);
        File.WriteAllText(otherFile, "struct Other {\n  float retry_jitter = 0.50f;\n};\n");
        GitProcess.Run(otherDir, "init");
        GitProcess.Run(otherDir, "config", "user.email", "test@example.com");
        GitProcess.Run(otherDir, "config", "user.name", "Test User");
        GitProcess.Run(otherDir, "add", ".");
        GitProcess.Run(otherDir, "commit", "-m", "init other");

        using var db = MemoryDatabase.Open(repo.DbPath);
        Indexer.IndexRepo(db, repo.RepoName, repo.RepoDirectory);
        Indexer.IndexRepo(db, "other", otherDir);

        var first = Assert.IsType<AgentMemoryBank.Models.NoteWriteResult.Accepted>(
            NoteStore.Add(db, new NoteDraft
            {
                Claim = "retry_jitter defaults to 0.25 in fixture",
                Evidence = "fixture/src/options.h:2",
            }, repoRoot: repo.Root));
        var second = Assert.IsType<AgentMemoryBank.Models.NoteWriteResult.Accepted>(
            NoteStore.Add(db, new NoteDraft
            {
                Claim = "retry_jitter defaults to 0.50 in other",
                Evidence = "other/src/other.h:2",
            }, repoRoot: repo.Root));

        var fixtureHits = MemorySearch.Search(db, "retry_jitter", repo: repo.RepoName);
        Assert.Equal([first.Id], fixtureHits.Notes.Select(n => n.Id));
        Assert.NotEmpty(fixtureHits.Chunks);

        var otherHits = MemorySearch.Search(db, "retry_jitter", repo: "other");
        Assert.Equal([second.Id], otherHits.Notes.Select(n => n.Id));
        Assert.NotEmpty(otherHits.Chunks);

        var unfiltered = MemorySearch.Search(db, "retry_jitter");
        Assert.Equal(2, unfiltered.Notes.Count);
    }

    [Fact]
    public void Unattributed_hazard_with_script_path_and_clock_time_stays_in_repo_scoped_search()
    {
        using var repo = new TempRepo();
        repo.Write("src/options.h", "struct Options {\n  float retry_jitter = 0.25f;\n};\n");
        repo.Commit();

        using var db = MemoryDatabase.Open(repo.DbPath);
        Indexer.IndexRepo(db, repo.RepoName, repo.RepoDirectory);

        var added = Assert.IsType<AgentMemoryBank.Models.NoteWriteResult.Accepted>(
            NoteStore.Add(db, new NoteDraft
            {
                Claim = "sanitizer race reproduces under 8 worker threads",
                Evidence = "scratchpad pr2417/sanitizer_runs.py on repos/app/build/app.exe, 9 runs, finished 12:30",
                Kind = AgentMemoryBank.Models.NoteKind.Hazard,
            }, repoRoot: repo.Root));

        Assert.Contains(added.Id, MemorySearch.Search(db, "sanitizer race").Notes.Select(n => n.Id));
        Assert.Contains(added.Id, MemorySearch.Search(db, "sanitizer race", repo: repo.RepoName).Notes.Select(n => n.Id));
    }

    [Fact]
    public void Searching_an_empty_store_returns_nothing_rather_than_failing()
    {
        using var repo = new TempRepo();
        using var db = MemoryDatabase.Open(repo.DbPath);

        var result = MemorySearch.Search(db, "retry_jitter");
        Assert.Equal(0, result.Total);
    }
}

internal static class ChunkHitExtensions
{
    /// <summary>Body of the chunk a hit points at, for assertions about content.</summary>
    public static string Body(this AgentMemoryBank.Models.ChunkHit hit, Microsoft.Data.Sqlite.SqliteConnection db) =>
        MemorySearch.GetChunk(db, hit.Id)?.Chunk.Body ?? "";
}
