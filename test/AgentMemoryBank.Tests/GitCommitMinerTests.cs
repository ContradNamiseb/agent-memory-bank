using AgentMemoryBank.Database;
using AgentMemoryBank.Indexing;
using AgentMemoryBank.Models;
using Xunit;

namespace AgentMemoryBank.Tests;

public sealed class GitCommitMinerTests
{
    [Fact]
    public void Parses_raw_git_log_records_correctly()
    {
        var rawLog = "\u001e4de83b5994e62fdf0b9280072f79c932d6311d19\u001fAlice Smith\u001f2026-08-15T12:58:24+02:00\u001fFix the LR panic-drop\u001fRestore lr 0.0003 instead of 0.00003.\u001f\nsrc/training.py\nsrc/derived_metrics.py\n";

        var commits = GitCommitMiner.ParseLogOutput(rawLog);

        Assert.Single(commits);
        var c = commits[0];
        Assert.Equal("4de83b5994e62fdf0b9280072f79c932d6311d19", c.Sha);
        Assert.Equal("4de83b59", c.ShortSha);
        Assert.Equal("Alice Smith", c.Author);
        Assert.Equal("Fix the LR panic-drop", c.Subject);
        Assert.Equal("Restore lr 0.0003 instead of 0.00003.", c.Body);
        Assert.Equal(2, c.Files.Count);
        Assert.Equal("src/training.py", c.Files[0]);
        Assert.Equal("src/derived_metrics.py", c.Files[1]);
    }

    [Theory]
    [InlineData("wip")]
    [InlineData("format")]
    [InlineData("cleanup")]
    [InlineData("typo")]
    [InlineData("bump")]
    public void Rejects_trivial_noise_commits_with_empty_bodies(string trivialSubject)
    {
        var commit = new MinedCommit
        {
            Sha = "1234567890abcdef",
            ShortSha = "12345678",
            Author = "Test",
            Date = DateTimeOffset.UtcNow,
            Subject = trivialSubject,
            Body = "",
            Files = ["test.txt"]
        };

        Assert.False(GitCommitMiner.IsSubstantive(commit));
    }

    [Fact]
    public void Accepts_substantive_commits()
    {
        var commitWithBody = new MinedCommit
        {
            Sha = "1234567890abcdef",
            ShortSha = "12345678",
            Author = "Test",
            Date = DateTimeOffset.UtcNow,
            Subject = "Fix checkpoint resume",
            Body = "Deduplicate parameters by id() so optimizer moments don't crash on reload.",
            Files = ["src/checkpoint.py"]
        };

        Assert.True(GitCommitMiner.IsSubstantive(commitWithBody));
    }

    [Theory]
    [InlineData("Fix deadlock in background worker", "", NoteKind.Hazard)]
    [InlineData("Benchmark throughput on AVX2", "Achieved 12% speedup with RMSE 0.015.", NoteKind.Finding)]
    [InlineData("Switch default optimizer to AdamW", "Better convergence on larger batch sizes.", NoteKind.Decision)]
    [InlineData("How to build and launch the trainer", "Run meson compile in build directory.", NoteKind.Howto)]
    public void Infers_appropriate_note_kind(string subject, string body, NoteKind expectedKind)
    {
        var commit = new MinedCommit
        {
            Sha = "1234567890abcdef",
            ShortSha = "12345678",
            Author = "Test",
            Date = DateTimeOffset.UtcNow,
            Subject = subject,
            Body = body,
            Files = ["src/model.py"]
        };

        Assert.Equal(expectedKind, GitCommitMiner.InferKind(commit));
    }

    [Fact]
    public void Mines_commits_from_real_git_repository_and_stores_searchable_notes()
    {
        using var repo = new TempRepo();
        repo.Write("src/engine.py", "def run():\n    pass\n");
        repo.Commit("Initial commit with engine structure");

        repo.Write("src/engine.py", "def run():\n    # fix deadlock by releasing mutex before async call\n    pass\n");
        repo.Commit("Fix critical deadlock in background engine worker\n\nReleasing the mutex before invoking async callback prevents thread freeze.");

        using var db = MemoryDatabase.Open(repo.DbPath);

        var run = GitCommitMiner.MineAll(
            db,
            repoRoot: repo.Root,
            repos: [repo.RepoName],
            options: GitMineOptions.Default);

        Assert.Single(run.Results);
        var repoResult = run.Results[0];
        Assert.True(repoResult.NotesCreated >= 1, "At least one substantive note should be created");

        // Search for the mined note using BM25
        var searchResult = MemorySearch.Search(db, "deadlock engine");
        Assert.NotEmpty(searchResult.Notes);

        var note = searchResult.Notes[0];
        Assert.Contains("deadlock", note.Claim, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Confidence.Inferred, note.Confidence);
        Assert.Equal(NoteKind.Hazard, note.Kind);
    }

    /// <summary>
    /// A commit message says nothing about which line it concerns, so the miner
    /// must not invent one. A synthesised "path:1" passes the evidence gate but
    /// anchors the note to the licence header at the top of the file, which
    /// never changes — so verification reports the note healthy forever, even
    /// after the code it describes is deleted. An honestly unanchored note is
    /// worth more than a falsely anchored one.
    /// </summary>
    [Fact]
    public void Mined_evidence_never_invents_a_line_citation()
    {
        var commit = new MinedCommit
        {
            Sha = "abcdef1234567890",
            ShortSha = "abcdef1",
            Subject = "Fix silent truncation when the pool is exhausted",
            Body = "The loop stopped early with no error.",
            Author = "someone",
            Date = new DateTime(2026, 1, 1),
            Files = ["src/loader/pool.cc"],
        };

        var draft = GitCommitMiner.BuildDraft("first-repo", commit, runId: 1);

        Assert.Empty(AgentMemoryBank.Verification.Citation.ParseAll(draft.Evidence));
        Assert.DoesNotContain(":1", draft.Evidence);
        // Still reproducible, so it passes the evidence gate honestly.
        Assert.True(AgentMemoryBank.Verification.Citation.LooksReproducible(draft.Evidence));
        Assert.Contains("git show abcdef1", draft.Evidence);
        Assert.Contains("src/loader/pool.cc", draft.Evidence);
    }
}
