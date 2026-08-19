using AgentMemoryBank.Database;
using AgentMemoryBank.Indexing;
using AgentMemoryBank.Models;
using AgentMemoryBank.Verification;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentMemoryBank.Tests;

/// <summary>
/// Staleness handling. The balance being tested is that the common case stays
/// silent while the dangerous case gets loud: alerts nobody trusts are the same
/// as no alerts at all.
/// </summary>
public class EvidenceVerifierTests
{
    private const string SampleCpp = """
        struct Options {
          // Fitted against measured p95 latency, not chosen by eye.
          float retry_backoff = 1.5f;
          float retry_jitter = 0.25f;
        };
        """;

    private static SqliteConnection Indexed(TempRepo repo, string content = SampleCpp)
    {
        repo.Write("src/options.h", content);
        repo.Commit();
        var db = MemoryDatabase.Open(repo.DbPath);
        Indexer.IndexRepo(db, repo.RepoName, repo.RepoDirectory);
        return db;
    }

    private static long AddNote(SqliteConnection db, TempRepo repo, int line = 3)
    {
        var result = NoteStore.Add(db, new NoteDraft
        {
            Claim = "retry_backoff is fitted to measured p95 latency, not chosen by eye",
            Evidence = $"{repo.RepoName}/src/options.h:{line}",
            Confidence = Confidence.Measured,
        }, repo.Root);
        return Assert.IsType<NoteWriteResult.Accepted>(result).Id;
    }

    /// <summary>Rewrite the file on disk without re-indexing, as a real edit would.</summary>
    private static void Rewrite(TempRepo repo, string content) =>
        File.WriteAllText(Path.Combine(repo.RepoDirectory, "src/options.h"), content);

    [Fact]
    public void The_anchor_is_the_cited_line_itself_not_the_comment_above_it()
    {
        var lines = AgentMemoryBank.Indexing.Chunkers.Chunker.SplitLines(SampleCpp);

        var anchor = EvidenceVerifier.ChooseAnchorText(lines, citedLine: 3);

        // Anchoring to the comment would keep matching after the value changed.
        Assert.Equal("float retry_backoff = 1.5f;", anchor);
    }

    [Fact]
    public void A_trivial_cited_line_falls_back_to_a_distinctive_neighbour()
    {
        var lines = AgentMemoryBank.Indexing.Chunkers.Chunker.SplitLines(SampleCpp);

        // Line 5 is "};" — useless as an anchor, it would match all over.
        var anchor = EvidenceVerifier.ChooseAnchorText(lines, citedLine: 5);

        Assert.NotNull(anchor);
        Assert.NotEqual("};", anchor);
        Assert.Contains("retry_jitter", anchor);
    }

    [Fact]
    public void An_untouched_file_verifies_silently()
    {
        using var repo = new TempRepo();
        using var db = Indexed(repo);
        var id = AddNote(db, repo);

        var result = Assert.Single(EvidenceVerifier.Verify(db, id, repo.Root));

        Assert.Equal(VerificationStatus.Unchanged, result.Status);
        Assert.False(result.NeedsAttention);
    }

    [Fact]
    public void A_line_that_merely_moved_is_re_anchored_without_bothering_anyone()
    {
        using var repo = new TempRepo();
        using var db = Indexed(repo);
        var id = AddNote(db, repo, line: 3);

        // Three lines added at the top: the cited code is untouched, but every
        // line number below shifts and the file hash changes.
        Rewrite(repo, "#include <cstdint>\n#include <string>\n\n" + SampleCpp);

        var result = Assert.Single(EvidenceVerifier.Verify(db, id, repo.Root));

        Assert.Equal(VerificationStatus.Relocated, result.Status);
        Assert.False(result.NeedsAttention);
        Assert.Equal(3, result.OldLine);
        Assert.Equal(6, result.NewLine);

        // The stored evidence was corrected, so the citation still resolves later.
        using var cmd = db.Prepare("SELECT evidence, verified_at FROM note WHERE id = $id", "$id");
        var row = Assert.Single(cmd.SetAll(id).Query(r =>
            (Evidence: r.GetString("evidence"), Verified: r.GetStringOrNull("verified_at"))));
        Assert.Contains("src/options.h:6", row.Evidence);
        Assert.NotNull(row.Verified);
    }

    [Fact]
    public void Reformatting_whitespace_does_not_look_like_a_deletion()
    {
        using var repo = new TempRepo();
        using var db = Indexed(repo);
        var id = AddNote(db, repo, line: 3);

        Rewrite(repo, SampleCpp.Replace("  float retry_backoff = 1.5f;", "\tfloat   retry_backoff   =   1.5f;"));

        var result = Assert.Single(EvidenceVerifier.Verify(db, id, repo.Root));

        Assert.Equal(VerificationStatus.Relocated, result.Status);
        Assert.False(result.NeedsAttention);
    }

    [Fact]
    public void Changing_the_cited_line_is_surfaced_rather_than_passed_over()
    {
        using var repo = new TempRepo();
        using var db = Indexed(repo);
        var id = AddNote(db, repo, line: 3);

        // The value the note is about changes. This must not verify clean.
        Rewrite(repo, SampleCpp.Replace("retry_backoff = 1.5f", "retry_backoff = 9.0f"));

        var result = Assert.Single(EvidenceVerifier.Verify(db, id, repo.Root));

        Assert.True(result.NeedsAttention, "a changed cited line must not be reported as fine");
        Assert.Equal(VerificationStatus.Moved, result.Status);
        Assert.Contains("Options", result.Detail);   // the enclosing symbol survives
    }

    [Fact]
    public void A_deleted_file_is_reported_as_broken()
    {
        using var repo = new TempRepo();
        using var db = Indexed(repo);
        var id = AddNote(db, repo);

        File.Delete(Path.Combine(repo.RepoDirectory, "src/options.h"));

        var result = Assert.Single(EvidenceVerifier.Verify(db, id, repo.Root));

        Assert.Equal(VerificationStatus.Broken, result.Status);
        Assert.True(result.NeedsAttention);
    }

    [Fact]
    public void Command_only_evidence_reports_that_it_cannot_be_checked()
    {
        using var repo = new TempRepo();
        using var db = Indexed(repo);

        var accepted = Assert.IsType<NoteWriteResult.Accepted>(NoteStore.Add(db, new NoteDraft
        {
            Claim = "the importer drops rows whose timestamp precedes the epoch",
            Evidence = "py scripts/check_import.py sample.csv --strict -> 14 rows dropped",
        }, repo.Root));

        var result = Assert.Single(EvidenceVerifier.Verify(db, accepted.Id, repo.Root));

        Assert.Equal(VerificationStatus.NotAnchored, result.Status);
        Assert.False(result.NeedsAttention);   // honest, not alarming
    }

    [Fact]
    public void Superseded_notes_are_not_verified()
    {
        using var repo = new TempRepo();
        using var db = Indexed(repo);
        var id = AddNote(db, repo);

        NoteStore.Supersede(db, id, new NoteDraft
        {
            Claim = "retry_backoff of 2.5s is fitted to measured p95 latency",
            Evidence = $"{repo.RepoName}/src/options.h:3",
        }, why: "the earlier fit used a truncated trace", repoRoot: repo.Root);

        var results = EvidenceVerifier.Verify(db, repoRoot: repo.Root);

        Assert.DoesNotContain(results, r => r.NoteId == id);
        Assert.Single(results);
    }
}
