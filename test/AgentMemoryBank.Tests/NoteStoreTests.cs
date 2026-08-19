using AgentMemoryBank.Database;
using AgentMemoryBank.Indexing;
using AgentMemoryBank.Models;
using AgentMemoryBank.Verification;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentMemoryBank.Tests;

/// <summary>
/// The write path. Evidence enforcement is the single constraint keeping the
/// store from filling with plausible sludge, so most of these tests are about
/// what gets refused and whether the refusal is actionable.
/// </summary>
public class NoteStoreTests
{
    private const string SampleCpp = """
        struct Options {
          // Fitted against measured p95 latency, not chosen by eye.
          float retry_backoff = 1.5f;
          float retry_jitter = 0.25f;
        };
        """;

    private static SqliteConnection Indexed(TempRepo repo)
    {
        repo.Write("src/options.h", SampleCpp);
        repo.Commit();
        var db = MemoryDatabase.Open(repo.DbPath);
        Indexer.IndexRepo(db, repo.RepoName, repo.RepoDirectory);
        return db;
    }

    private static NoteDraft Draft(string claim, string evidence) =>
        new() { Claim = claim, Evidence = evidence, Confidence = Confidence.Measured };

    [Fact]
    public void A_grounded_note_is_recorded_and_linked_to_the_line_it_cites()
    {
        using var repo = new TempRepo();
        using var db = Indexed(repo);

        var result = NoteStore.Add(db, Draft(
            "retry_backoff is fitted to measured p95 latency, not chosen by eye",
            $"{repo.RepoName}/src/options.h:3"), repo.Root);

        var accepted = Assert.IsType<NoteWriteResult.Accepted>(result);
        Assert.True(accepted.IsGrounded);

        // The citation became a real edge into the index, not just prose.
        using var edges = db.Prepare(
            "SELECT dst_id FROM edge WHERE src_kind='note' AND src_id=$id AND rel='cites'", "$id");
        Assert.NotEmpty(edges.SetAll(accepted.Id).Query(r => r.GetInt64("dst_id")));
    }

    [Fact]
    public void Evidence_is_mandatory_and_the_refusal_carries_a_copyable_example()
    {
        using var repo = new TempRepo();
        using var db = Indexed(repo);

        var result = NoteStore.Add(db, new NoteDraft
        {
            Claim = "the retry backoff value is correct and should not be changed",
            Evidence = "  ",
        });

        var rejected = Assert.IsType<NoteWriteResult.Rejected>(result);
        Assert.Equal(RejectionReason.MissingEvidence, rejected.Reason);
        Assert.Equal("missing_evidence", rejected.Code);
        // The example is what breaks a retry loop: something correct to copy.
        Assert.Contains(":", rejected.Example.Evidence);
        Assert.Equal(0, db.Scalar<int>("SELECT count(*) FROM note"));
    }

    [Fact]
    public void A_reproducible_command_counts_as_evidence_even_without_a_citation()
    {
        using var repo = new TempRepo();
        using var db = Indexed(repo);

        var result = NoteStore.Add(db, Draft(
            "the importer drops rows whose timestamp precedes the epoch",
            "py scripts/check_import.py sample.csv --strict -> 14 rows dropped"), repo.Root);

        var accepted = Assert.IsType<NoteWriteResult.Accepted>(result);
        // Honest about its own limits: nothing anchors it, so verification cannot check it.
        Assert.False(accepted.IsGrounded);
    }

    [Fact]
    public void A_citation_to_a_path_that_does_not_exist_is_refused()
    {
        using var repo = new TempRepo();
        using var db = Indexed(repo);

        var result = NoteStore.Add(db, Draft(
            "the loader silently truncates when the pool is exhausted",
            $"{repo.RepoName}/src/does_not_exist.h:12"), repo.Root);

        var rejected = Assert.IsType<NoteWriteResult.Rejected>(result);
        Assert.Equal(RejectionReason.UnresolvableAnchor, rejected.Reason);
    }

    [Theory]
    [InlineData("too short")]
    [InlineData("does the retry backoff need refitting?")]
    [InlineData("the retry backoff might possibly be wrong here")]
    public void Claims_that_nothing_could_contradict_are_refused(string claim)
    {
        using var repo = new TempRepo();
        using var db = Indexed(repo);

        var result = NoteStore.Add(db, Draft(claim, $"{repo.RepoName}/src/options.h:3"), repo.Root);

        var rejected = Assert.IsType<NoteWriteResult.Rejected>(result);
        Assert.Equal(RejectionReason.ClaimNotFalsifiable, rejected.Reason);
        Assert.NotEmpty(rejected.Message);
    }

    [Fact]
    public void The_same_claim_twice_is_refused_and_points_at_the_original()
    {
        using var repo = new TempRepo();
        using var db = Indexed(repo);
        var evidence = $"{repo.RepoName}/src/options.h:3";
        var claim = "retry_backoff is fitted to measured p95 latency, not chosen by eye";

        var first = Assert.IsType<NoteWriteResult.Accepted>(NoteStore.Add(db, Draft(claim, evidence), repo.Root));
        var second = NoteStore.Add(db, Draft(claim, evidence), repo.Root);

        var rejected = Assert.IsType<NoteWriteResult.Rejected>(second);
        Assert.Equal(RejectionReason.DuplicateClaim, rejected.Reason);
        Assert.Contains(first.Id.ToString(), rejected.Message);
    }

    [Fact]
    public void Superseding_keeps_the_original_as_a_record_and_hides_it_from_search()
    {
        using var repo = new TempRepo();
        using var db = Indexed(repo);
        var evidence = $"{repo.RepoName}/src/options.h:3";

        var original = Assert.IsType<NoteWriteResult.Accepted>(NoteStore.Add(db,
            Draft("retry_backoff of 0.5s is fitted to measured p95 latency", evidence), repo.Root));

        var replacement = Assert.IsType<NoteWriteResult.Accepted>(NoteStore.Supersede(db, original.Id,
            Draft("retry_backoff of 1.5s is fitted to measured p95 latency", evidence),
            why: "the earlier fit used a truncated trace and was low by 3x", repoRoot: repo.Root));

        // Nothing is deleted: being wrong stays on the record.
        Assert.Equal(2, db.Scalar<int>("SELECT count(*) FROM note"));

        var live = MemorySearch.SearchNotes(db, "retry_backoff fitted");
        Assert.Equal(replacement.Id, Assert.Single(live).Id);

        var all = MemorySearch.SearchNotes(db, "retry_backoff fitted", includeSuperseded: true);
        Assert.Equal(2, all.Count);

        // And the reason it changed is preserved, which is the useful part.
        using var reason = db.Prepare("SELECT rationale FROM note WHERE id = $id", "$id");
        Assert.Contains("truncated trace",
            Assert.Single(reason.SetAll(replacement.Id).Query(r => r.GetString("rationale"))));
    }

    [Fact]
    public void A_note_cannot_be_superseded_twice()
    {
        using var repo = new TempRepo();
        using var db = Indexed(repo);
        var evidence = $"{repo.RepoName}/src/options.h:3";

        var original = Assert.IsType<NoteWriteResult.Accepted>(NoteStore.Add(db,
            Draft("retry_backoff of 0.5s is fitted to measured latency", evidence), repo.Root));
        NoteStore.Supersede(db, original.Id,
            Draft("retry_backoff of 1.5s is fitted to measured latency", evidence), repoRoot: repo.Root);

        var third = NoteStore.Supersede(db, original.Id,
            Draft("retry_backoff of 2.5s is fitted to measured latency", evidence), repoRoot: repo.Root);

        var rejected = Assert.IsType<NoteWriteResult.Rejected>(third);
        Assert.Contains("already superseded", rejected.Message);
    }

    [Fact]
    public void Tags_are_normalised_and_deduplicated()
    {
        using var repo = new TempRepo();
        using var db = Indexed(repo);

        var accepted = Assert.IsType<NoteWriteResult.Accepted>(NoteStore.Add(db, new NoteDraft
        {
            Claim = "retry_backoff is fitted to measured p95 latency, not chosen",
            Evidence = $"{repo.RepoName}/src/options.h:3",
            Tags = ["Calibration", "calibration", "  latency  "],
        }, repo.Root));

        using var cmd = db.Prepare("SELECT tag FROM tag WHERE note_id = $id ORDER BY tag", "$id");
        Assert.Equal(["calibration", "latency"], cmd.SetAll(accepted.Id).Query(r => r.GetString("tag")));
    }
}

/// <summary>
/// Citations into trees the indexer skips. These came out of backfilling real
/// findings by hand: the most valuable hazards were in vendored third-party
/// code, and the evidence gate refused exactly those notes.
/// </summary>
public class UnindexedCitationTests
{
    [Fact]
    public void A_citation_into_a_vendored_tree_is_accepted_and_still_verifiable()
    {
        using var repo = new TempRepo();
        repo.Write("src/app.cc", "int main() { return 0; }\n");
        repo.Commit();

        // Vendored: present on disk, deliberately excluded from the index.
        var vendored = Path.Combine(repo.RepoDirectory, "vendor", "lib", "cleanup.cc");
        Directory.CreateDirectory(Path.GetDirectoryName(vendored)!);
        File.WriteAllText(vendored,
            "void Run() {\n  const bool kDeleteFiles = true;   // destroys inputs on failure\n  remove(path);\n}\n");

        using var db = MemoryDatabase.Open(repo.DbPath);
        Indexer.IndexRepo(db, repo.RepoName, repo.RepoDirectory, options: new IndexOptions
        {
            VendoredDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "vendor" },
        });

        var result = NoteStore.Add(db, new NoteDraft
        {
            Claim = "the vendored cleanup helper deletes its inputs on failure as well as success",
            Evidence = $"{repo.RepoName}/vendor/lib/cleanup.cc:2",
            Confidence = Confidence.Measured,
        }, repo.Root);

        var accepted = Assert.IsType<NoteWriteResult.Accepted>(result);
        Assert.False(accepted.IsGrounded);     // nothing to link to: it is not indexed
        Assert.True(accepted.IsVerifiable);    // but the line was anchored, so it can be re-checked

        var verified = Assert.Single(EvidenceVerifier.Verify(db, accepted.Id, repo.Root));
        Assert.False(verified.NeedsAttention);
    }

    [Fact]
    public void A_citation_to_a_file_that_does_not_exist_is_still_refused()
    {
        using var repo = new TempRepo();
        repo.Write("src/app.cc", "int main() { return 0; }\n");
        repo.Commit();

        using var db = MemoryDatabase.Open(repo.DbPath);
        Indexer.IndexRepo(db, repo.RepoName, repo.RepoDirectory);

        var result = NoteStore.Add(db, new NoteDraft
        {
            Claim = "the imaginary module drops records when the queue is full",
            Evidence = $"{repo.RepoName}/vendor/lib/imaginary.cc:2",
        }, repo.Root);

        var rejected = Assert.IsType<NoteWriteResult.Rejected>(result);
        Assert.Equal(RejectionReason.UnresolvableAnchor, rejected.Reason);
    }
}
