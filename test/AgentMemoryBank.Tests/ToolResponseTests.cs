using System.Text.Json;
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

        Assert.Contains("nothing on this subject is recorded", text);
        Assert.Contains("Searched as a single term", text);
        Assert.DoesNotContain("store is empty", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_multi_term_miss_says_both_passes_were_tried()
    {
        using var repo = new TempRepo();
        using var db = IndexedStore(repo);

        var text = ToolResponses.RenderSearch(MemorySearch.Search(db, "nonexistent_symbol absent_thing"), db);

        Assert.Contains("Both an all-terms and an any-term pass were tried", text);
    }

    [Fact]
    public void Loosened_results_are_labelled_as_such()
    {
        using var repo = new TempRepo();
        using var db = IndexedStore(repo);

        // One term that exists in the store, one that does not: the AND pass finds
        // nothing, so the OR retry is what produces these hits.
        var result = MemorySearch.Search(db, "retry_backoff nonexistent_symbol");

        Assert.Equal(MatchMode.Loosened, result.Mode);
        Assert.Contains("loosened", ToolResponses.RenderSearch(result, db), StringComparison.OrdinalIgnoreCase);
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

        // Plain, and with the valid range, so the caller can correct the id
        // without a separate listing call.
        Assert.Contains("no chunk with id 999999; chunk ids run #1–#", ToolResponses.RenderChunkById(db, 999_999));
        Assert.Contains("no note with id 999999", ToolResponses.RenderNoteById(db, 999_999));
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

    [Fact]
    public void HookRunner_ExtractIdentifiers_filters_stop_words_and_caps_at_three()
    {
        Assert.Empty(HookRunner.ExtractIdentifiers("please change the files and inspect behaviour"));
        Assert.Equal(
            ["wdl_spread", "memorysearch.search", "cache2"],
            HookRunner.ExtractIdentifiers(
                "Compare wdl_spread with MemorySearch.Search, cache2, wdl_spread, and ignored_fourth_key please."));
    }

    [Fact]
    public void HookRunner_RunSessionStart_emits_json_for_latest_handoff_and_is_silent_when_missing()
    {
        using var repo = new TempRepo();

        // Missing DB file must emit nothing and never create an empty database file.
        var silent = new StringWriter();
        Assert.Equal(0, HookRunner.RunSessionStart(repo.DbPath, silent));
        Assert.Equal("", silent.ToString());
        Assert.False(File.Exists(repo.DbPath));

        using var db = MemoryDatabase.Open(repo.DbPath);
        HandoffStore.Save(db, new SessionHandoffDraft
        {
            Agent = "codex",
            Topic = "Refactor memory bank hooks",
            Summary = "Moved hook logic into HookRunner.",
            NextSteps = "Run full test suite.",
        });

        var output = new StringWriter();
        Assert.Equal(0, HookRunner.RunSessionStart(repo.DbPath, output));

        using var doc = JsonDocument.Parse(output.ToString().Trim());
        var hook = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("SessionStart", hook.GetProperty("hookEventName").GetString());
        var ctx = hook.GetProperty("additionalContext").GetString()!;
        Assert.Contains("Refactor memory bank hooks", ctx);
        Assert.Contains("Moved hook logic into HookRunner.", ctx);
    }

    [Fact]
    public void HookRunner_RunRecall_injects_strict_hits_only()
    {
        using var repo = new TempRepo();
        using var db = IndexedStore(repo);
        NoteStore.Add(db, new NoteDraft
        {
            Claim = "retry_jitter is fitted against p95 latency",
            Evidence = "fixture/src/options.h:4",
        }, repoRoot: repo.Root);

        // Short prompt / malformed JSON / loosened-only match all emit nothing.
        foreach (var payload in new[]
        {
            "{bad json",
            """{"prompt":"short"}""",
            """{"prompt":"inspect the retry_jitter and nonexistent_key_99 values"}""",
        })
        {
            var silent = new StringWriter();
            Assert.Equal(0, HookRunner.RunRecall(repo.DbPath, new StringReader(payload), silent));
            Assert.Equal("", silent.ToString());
        }

        // Exact identifier hit emits UserPromptSubmit hook JSON with the matching note.
        var output = new StringWriter();
        var promptJson = """{"prompt":"Verify whether retry_jitter changed in recent commits"}""";
        Assert.Equal(0, HookRunner.RunRecall(repo.DbPath, new StringReader(promptJson), output));

        using var doc = JsonDocument.Parse(output.ToString().Trim());
        var hook = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("UserPromptSubmit", hook.GetProperty("hookEventName").GetString());
        Assert.Contains("retry_jitter is fitted against p95 latency", hook.GetProperty("additionalContext").GetString());
    }

    [Fact]
    public void ToolResponses_FormatHandoff_includes_context_when_present()
    {
        using var repo = new TempRepo();
        using var db = MemoryDatabase.Open(repo.DbPath);

        var withoutContext = HandoffStore.Save(db, new SessionHandoffDraft
        {
            Agent = "codex",
            Topic = "Without context",
            Summary = "Summary A",
            NextSteps = "Next A",
            ContextData = "   ",
        });
        Assert.DoesNotContain("--- Context ---", ToolResponses.FormatHandoff(withoutContext));

        var withContext = HandoffStore.Save(db, new SessionHandoffDraft
        {
            Agent = "claudecode",
            Topic = "With context",
            Summary = "Summary B",
            NextSteps = "Next B",
            ContextData = "branch=refactor/clean-architecture sha=abc1234",
        });
        var formatted = ToolResponses.FormatHandoff(withContext);
        Assert.Contains("--- Context ---", formatted);
        Assert.Contains("branch=refactor/clean-architecture sha=abc1234", formatted);
    }
}
