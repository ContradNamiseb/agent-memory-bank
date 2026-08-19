using AgentMemoryBank.Database;
using AgentMemoryBank.Indexing;
using AgentMemoryBank.McpServer;
using AgentMemoryBank.Models;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentMemoryBank.Tests;

/// <summary>
/// What the MCP tools actually hand back to a model. These assertions are about
/// wording as much as data: the text is the entire interface, and a model acts
/// on what it says.
/// </summary>
public class ToolResponseTests
{
    private const string SampleCpp = """
        struct Options {
          // Fitted against measured outcomes, not chosen by eye.
          float retry_backoff = 1.5f;
          float retry_jitter = 0.25f;
        };
        """;

    private static SqliteConnection IndexedStore(TempRepo repo)
    {
        repo.Write("src/options.h", SampleCpp);
        repo.Commit();
        var db = MemoryDatabase.Open(repo.DbPath);
        Indexer.IndexRepo(db, repo.RepoName, repo.RepoDirectory);
        return db;
    }

    [Fact]
    public void An_empty_store_says_so_rather_than_implying_nothing_was_recorded()
    {
        using var repo = new TempRepo();
        using var db = MemoryDatabase.Open(repo.DbPath);

        var text = ToolResponses.RenderSearch(MemorySearch.Search(db, "retry_jitter"), db);

        // The distinction that matters: "never indexed" is not "no such finding".
        Assert.Contains("store is empty", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("run the indexer", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_populated_store_with_no_match_reports_a_genuine_miss()
    {
        using var repo = new TempRepo();
        using var db = IndexedStore(repo);

        var text = ToolResponses.RenderSearch(MemorySearch.Search(db, "nonexistent_symbol"), db);

        Assert.Contains("genuinely matched nothing", text);
        Assert.DoesNotContain("store is empty", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_search_hit_carries_a_citation_and_a_retrievable_id()
    {
        using var repo = new TempRepo();
        using var db = IndexedStore(repo);

        var result = MemorySearch.Search(db, "retry_jitter");
        var text = ToolResponses.RenderSearch(result, db);

        var hit = Assert.Single(result.Chunks);
        Assert.Contains($"[chunk {hit.Id}]", text);
        Assert.Contains($"{repo.RepoName}/src/options.h:{hit.LineStart}", text);
        Assert.Contains("memory_get", text);   // the reader is told how to get the full text
    }

    [Fact]
    public void Rendering_a_chunk_numbers_every_line_from_its_true_start()
    {
        using var repo = new TempRepo();
        using var db = IndexedStore(repo);

        var hit = Assert.Single(MemorySearch.SearchChunks(db, "retry_jitter"));
        var text = ToolResponses.RenderChunkById(db, hit.Id);

        Assert.Contains($"src/options.h:{hit.LineStart}-{hit.LineEnd}", text);
        Assert.Contains($"{hit.LineStart}| ", text);
        Assert.Contains("retry_jitter = 0.25f", text);
    }

    [Fact]
    public void A_missing_id_is_reported_plainly_rather_than_throwing()
    {
        using var repo = new TempRepo();
        using var db = IndexedStore(repo);

        Assert.Contains("No chunk with id 999999", ToolResponses.RenderChunkById(db, 999_999));
        Assert.Contains("No note with id 999999", ToolResponses.RenderNoteById(db, 999_999));
    }

    [Fact]
    public void Stats_warns_that_an_empty_store_makes_empty_results_meaningless()
    {
        using var repo = new TempRepo();
        using var db = MemoryDatabase.Open(repo.DbPath);

        Assert.Contains("empty", ToolResponses.RenderStats(db), StringComparison.OrdinalIgnoreCase);

        using var populated = IndexedStore(repo);
        var text = ToolResponses.RenderStats(populated);
        Assert.Contains($"{repo.RepoName}: 1 files", text);
        Assert.DoesNotContain("has not been indexed", text);
    }

    [Fact]
    public void A_note_that_was_never_verified_says_so_on_every_read()
    {
        using var repo = new TempRepo();
        using var db = IndexedStore(repo);

        // Written directly: the note write path arrives with Milestone 3.
        using var insert = db.Prepare("""
            INSERT INTO note(claim, rationale, evidence, kind, confidence, created_at)
            VALUES ($claim, $why, $evidence, $kind, $confidence, $at)
            RETURNING id
            """, "$claim", "$why", "$evidence", "$kind", "$confidence", "$at");
        var id = (long)insert.SetAll(
            "retry_jitter is fitted, not chosen",
            "Refit whenever the workload changes.",
            "fixture/src/options.h:4",
            "finding", "measured", DateTime.UtcNow.ToString("o")).ExecuteScalar()!;

        var text = ToolResponses.RenderNoteById(db, id);
        Assert.Contains("retry_jitter is fitted, not chosen", text);
        Assert.Contains("fixture/src/options.h:4", text);
        // Staleness is the failure mode, so an unverified note must never look verified.
        Assert.Contains("never been re-verified", text);

        // And notes outrank chunks in a combined search.
        var rendered = ToolResponses.RenderSearch(MemorySearch.Search(db, "retry_jitter"), db);
        Assert.True(rendered.IndexOf("Recorded knowledge", StringComparison.Ordinal) <
                    rendered.IndexOf("Indexed source", StringComparison.Ordinal),
            "notes should be rendered before the chunks that ground them");
    }
}
