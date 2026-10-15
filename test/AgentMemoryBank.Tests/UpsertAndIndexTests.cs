using System.Text.Json;
using AgentMemoryBank.Database;
using AgentMemoryBank.Indexing;
using AgentMemoryBank.McpServer;
using AgentMemoryBank.Models;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentMemoryBank.Tests;

/// <summary>
/// The batch-writer and orientation paths: an upsert that never overwrites,
/// a manifest an agent can read before fetching bodies, and refusals that
/// name what would have been valid.
/// </summary>
public class UpsertAndIndexTests
{
    private const string SampleCpp = """
        struct Options {
          // Fitted against measured p95 latency, not chosen by eye.
          float retry_backoff = 1.5f;
          float retry_jitter = 0.25f;
        };
        """;

    private const string Claim = "retry_backoff is fitted to measured p95 latency, not chosen by eye";

    private static SqliteConnection Indexed(TempRepo repo)
    {
        repo.Write("src/options.h", SampleCpp);
        repo.Commit();
        var db = MemoryDatabase.Open(repo.DbPath);
        Indexer.IndexRepo(db, repo.RepoName, repo.RepoDirectory);
        return db;
    }

    private static NoteDraft Draft(TempRepo repo, string claim, int line = 3, string tags = "") => new()
    {
        Claim = claim,
        Evidence = $"{repo.RepoName}/src/options.h:{line}",
        Confidence = Confidence.Measured,
        Tags = tags.Split(',', StringSplitOptions.RemoveEmptyEntries),
    };

    [Fact]
    public void Upsert_adds_then_is_idempotent_then_supersedes_on_new_evidence()
    {
        using var repo = new TempRepo();
        using var db = Indexed(repo);

        var first = NoteStore.Upsert(db, Draft(repo, Claim), repoRoot: repo.Root);
        Assert.Equal(UpsertAction.Added, first.Action);

        // The same claim again, with a different case and a new tag: nothing new, tags merged.
        var again = NoteStore.Upsert(db, Draft(repo, Claim.ToUpperInvariant(), tags: "calibration"), repoRoot: repo.Root);
        Assert.Equal(UpsertAction.Unchanged, again.Action);
        Assert.Equal(first.Id, again.Id);
        Assert.Contains("calibration", db.Prepare("SELECT tag FROM tag WHERE note_id = $id", "$id")
            .SetAll(first.Id).Query(r => r.GetString("tag")));

        // Same claim, different evidence: the old note is kept and superseded, never overwritten.
        var changed = NoteStore.Upsert(db, Draft(repo, Claim, line: 4), repoRoot: repo.Root);
        Assert.Equal(UpsertAction.Superseded, changed.Action);
        Assert.Equal(first.Id, changed.Replaced);
        Assert.NotEqual(first.Id, changed.Id);
        Assert.Equal(changed.Id, db.Prepare("SELECT superseded_by FROM note WHERE id = $id", "$id")
            .SetAll(first.Id).Query(r => r.GetInt64("superseded_by")).Single());
    }

    [Fact]
    public void Upsert_dry_run_writes_nothing()
    {
        using var repo = new TempRepo();
        using var db = Indexed(repo);

        var preview = NoteStore.Upsert(db, Draft(repo, Claim), dryRun: true, repoRoot: repo.Root);

        Assert.Equal(UpsertAction.Added, preview.Action);
        Assert.Equal(0, db.Scalar<long>("SELECT COUNT(*) FROM note"));
    }

    [Fact]
    public void Superseding_a_retired_note_names_the_live_one()
    {
        using var repo = new TempRepo();
        using var db = Indexed(repo);
        var first = NoteStore.Upsert(db, Draft(repo, Claim), repoRoot: repo.Root);
        var second = NoteStore.Upsert(db, Draft(repo, Claim, line: 4), repoRoot: repo.Root);

        var stale = NoteStore.Upsert(db, Draft(repo, "retry_jitter is fitted to measured p95 latency too", 4),
            supersedes: first.Id, repoRoot: repo.Root);

        Assert.Equal(UpsertAction.Rejected, stale.Action);
        var rejected = Assert.IsType<NoteWriteResult.Rejected>(stale.Write);
        Assert.Contains($"note {second.Id}", rejected.Message);
    }

    [Fact]
    public void Index_filters_by_repo_kind_and_tag_and_pages_backwards()
    {
        using var repo = new TempRepo();
        using var db = Indexed(repo);
        NoteStore.Upsert(db, Draft(repo, Claim, tags: "calibration"), repoRoot: repo.Root);
        NoteStore.Upsert(db, Draft(repo, "retry_jitter is fitted to measured p95 latency too", 4), repoRoot: repo.Root);
        NoteStore.Add(db, new NoteDraft
        {
            Claim = "The nightly job takes 41 minutes on the build box",
            Evidence = "measured: time ./nightly.sh -> 41m02s",
            Kind = NoteKind.Hazard,
        });

        var byRepo = NoteIndex.List(db, repo: repo.RepoName);
        Assert.Equal(2, byRepo.Total);

        var byTag = NoteIndex.List(db, tag: "calibration");
        Assert.Equal(Claim, Assert.Single(byTag.Entries).Claim);

        var hazards = NoteIndex.List(db, kind: NoteKind.Hazard);
        Assert.Equal("hazard", Assert.Single(hazards.Entries).Kind);

        var page = NoteIndex.List(db, limit: 1);
        Assert.Equal(3, page.Total);
        var older = NoteIndex.List(db, limit: 5, beforeId: page.Entries[0].Id);
        Assert.Equal(2, older.Entries.Count);

        var rendered = ToolResponses.RenderIndex(page.Entries, page.Total, "");
        Assert.Contains("id\tkind\tconf\tchars\ttags\tclaim", rendered);
        Assert.Contains($"continue with before_id={page.Entries[0].Id}", rendered);
    }

    [Fact]
    public void Unknown_note_ids_are_answered_with_the_valid_range()
    {
        using var repo = new TempRepo();
        using var db = Indexed(repo);
        var note = NoteStore.Upsert(db, Draft(repo, Claim), repoRoot: repo.Root);

        var text = ToolResponses.RenderNoteById(db, 9999);

        Assert.StartsWith("Error: no note with id 9999", text);
        Assert.Contains($"#{note.Id}", text);
    }

    [Fact]
    public void Guard_maps_a_singular_claim_onto_the_upsert_arrays()
    {
        var schema = JsonDocument.Parse("""
            {"type":"object","properties":{
              "claims":{"type":"array","items":{"type":"string"}},
              "evidence":{"type":"array","items":{"type":"string"}},
              "kinds":{"type":"array","items":{"type":"string","enum":["decision","hazard","howto","finding"]}}},
             "required":["claims","evidence"]}
            """).RootElement;
        var args = JsonSerializer.SerializeToElement(new { claim = "one claim", evidence = "x/y.cs:1", kind = "Hazard" })
            .EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

        var outcome = ToolArgumentGuard.Normalize("memory_notes_upsert", schema, args);

        Assert.Null(outcome.Error);
        Assert.Equal("one claim", outcome.Arguments["claims"][0].GetString());
        Assert.Equal("x/y.cs:1", outcome.Arguments["evidence"][0].GetString());
        Assert.Equal("hazard", outcome.Arguments["kinds"][0].GetString());
    }
}
