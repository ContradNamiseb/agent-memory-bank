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
