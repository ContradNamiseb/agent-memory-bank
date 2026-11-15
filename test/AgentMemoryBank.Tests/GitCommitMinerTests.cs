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

    [Fact]
    public void Superseding_a_mined_note_does_not_cause_next_mine_pass_to_remine_it()
    {
        var tempRoot = CreateTempDir();
        try
        {
            var repoDir = Path.Combine(tempRoot, "alpha");
            Directory.CreateDirectory(Path.Combine(repoDir, "src"));
            GitProcess.Run(repoDir, "init");
            GitProcess.Run(repoDir, "config", "user.email", "test@example.com");
            GitProcess.Run(repoDir, "config", "user.name", "Test User");

            File.WriteAllText(Path.Combine(repoDir, "src", "engine.cc"), "constexpr double kCpuctBase = 2800.0;\n");
            GitProcess.Run(repoDir, "add", "src/engine.cc");
            GitProcess.Run(repoDir, "commit",
                "-m", "Adjust mcts_cpuct_base after deeper endgame sweep",
                "-m", "Measured across 10k games; reduces tactical horizon drops.");

            using var db = MemoryDatabase.Open(Path.Combine(tempRoot, "memory.db"));

            var firstRun = GitCommitMiner.MineAll(db, tempRoot, ["alpha"], new GitMineOptions());
            var firstResult = Assert.Single(firstRun.Results);
            Assert.Null(firstResult.Error);
            Assert.Equal(1, firstResult.CommitsExamined);
            Assert.Equal(1, firstResult.NotesCreated);

            var listed = NoteIndex.List(db, repo: "alpha");
            var original = Assert.Single(listed.Entries);

            var replacement = new NoteDraft
            {
                Claim = "mcts_cpuct_base updated to 2900 after follow-up validation",
                Evidence = "alpha/src/engine.cc:1",
                Tags = ["alpha"],
            };
            var superseded = NoteStore.Supersede(db, original.Id, replacement, why: "re-calibrated", repoRoot: tempRoot);
            Assert.IsType<NoteWriteResult.Accepted>(superseded);
            Assert.NotNull(MemorySearch.GetNote(db, original.Id)?.SupersededBy);

            var secondRun = GitCommitMiner.MineAll(db, tempRoot, ["alpha"], new GitMineOptions());
            var secondResult = Assert.Single(secondRun.Results);
            Assert.Null(secondResult.Error);
            Assert.Equal(1, secondResult.CommitsExamined);
            Assert.Equal(0, secondResult.NotesCreated);
            Assert.Equal(1, secondResult.NotesSkipped);
        }
        finally
        {
            DeleteTempDir(tempRoot);
        }
    }

    [Fact]
    public void Repo_scoped_search_includes_mined_and_unattributed_notes_and_excludes_other_repos()
    {
        var tempRoot = CreateTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(tempRoot, "beta", "src"));
            File.WriteAllText(Path.Combine(tempRoot, "beta", "src", "cache.cc"), "constexpr int kProbeCacheCap = 64;\n");

            using var db = MemoryDatabase.Open(Path.Combine(tempRoot, "memory.db"));
            SeedRun(db, 1);

            var alphaDraft = GitCommitMiner.BuildDraft("alpha", new MinedCommit
            {
                Sha = "aaaaaaa111111111111111111111111111111111",
                ShortSha = "aaaaaaa",
                Author = "Alice",
                Date = DateTimeOffset.UtcNow,
                Subject = "Tune probe_shared_key in alpha pool allocator",
                Body = "Cuts peak RSS by 18% on large batches.",
                Files = ["src/pool.cc"],
            }, runId: 1);
            var alphaId = Assert.IsType<NoteWriteResult.Accepted>(NoteStore.Add(db, alphaDraft, tempRoot)).Id;

            var betaDraft = GitCommitMiner.BuildDraft("beta", new MinedCommit
            {
                Sha = "bbbbbbb222222222222222222222222222222222",
                ShortSha = "bbbbbbb",
                Author = "Bob",
                Date = DateTimeOffset.UtcNow,
                Subject = "Tune probe_shared_key in beta network loader",
                Body = "Avoids redundant tensor staging copies.",
                Files = ["src/loader.cc"],
            }, runId: 1);
            var betaMinedId = Assert.IsType<NoteWriteResult.Accepted>(NoteStore.Add(db, betaDraft, tempRoot)).Id;

            var betaFileId = Assert.IsType<NoteWriteResult.Accepted>(NoteStore.Add(db, new NoteDraft
            {
                Claim = "probe_shared_key in beta cache uses fixed 64-entry ring",
                Evidence = "beta/src/cache.cc:1",
                Tags = ["beta"],
            }, tempRoot)).Id;

            var unattributedId = Assert.IsType<NoteWriteResult.Accepted>(NoteStore.Add(db, new NoteDraft
            {
                Claim = "probe_shared_key cross-repo benchmark baseline is 12.4ms",
                Evidence = "python bench_suite.py --filter probe_shared_key -> 12.4ms p99",
                Tags = ["benchmark"],
            }, tempRoot)).Id;

            var alphaHits = MemorySearch.Search(db, "probe_shared_key", repo: "alpha", notesOnly: true).Notes
                .Select(n => n.Id)
                .ToHashSet();

            Assert.Contains(alphaId, alphaHits);
            Assert.Contains(unattributedId, alphaHits);
            Assert.DoesNotContain(betaMinedId, alphaHits);
            Assert.DoesNotContain(betaFileId, alphaHits);

            var repos = NoteIndex.Repos(db);
            Assert.Contains("alpha", repos);
            Assert.Contains("beta", repos);
        }
        finally
        {
            DeleteTempDir(tempRoot);
        }
    }

    [Fact]
    public void NoteIndex_list_with_repo_filter_includes_mined_notes_without_false_substring_matches()
    {
        var tempRoot = CreateTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(tempRoot, "alpha", "src"));
            Directory.CreateDirectory(Path.Combine(tempRoot, "alpha-v2", "src"));
            File.WriteAllText(Path.Combine(tempRoot, "alpha", "src", "cfg.cc"), "constexpr int kAlphaCfg = 1;\n");
            File.WriteAllText(Path.Combine(tempRoot, "alpha-v2", "src", "cfg.cc"), "constexpr int kAlphaV2Cfg = 2;\n");

            using var db = MemoryDatabase.Open(Path.Combine(tempRoot, "memory.db"));
            SeedRun(db, 1);

            var alphaMined = Assert.IsType<NoteWriteResult.Accepted>(NoteStore.Add(db, GitCommitMiner.BuildDraft("alpha", new MinedCommit
            {
                Sha = "1111111aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                ShortSha = "1111111",
                Author = "Alice",
                Date = DateTimeOffset.UtcNow,
                Subject = "Set alpha queue watermark for steady throughput",
                Body = "Prevents stall bursts under sustained load.",
                Files = ["src/queue.cc"],
            }, runId: 1), tempRoot)).Id;

            var alphaManual = Assert.IsType<NoteWriteResult.Accepted>(NoteStore.Add(db, new NoteDraft
            {
                Claim = "alpha configuration defaults to kAlphaCfg = 1",
                Evidence = "alpha/src/cfg.cc:1",
                Tags = ["alpha"],
            }, tempRoot)).Id;

            var alphaV2Mined = Assert.IsType<NoteWriteResult.Accepted>(NoteStore.Add(db, GitCommitMiner.BuildDraft("alpha-v2", new MinedCommit
            {
                Sha = "2222222bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                ShortSha = "2222222",
                Author = "Bob",
                Date = DateTimeOffset.UtcNow,
                Subject = "Set alpha-v2 queue watermark independently",
                Body = "Higher batch ceiling for v2 pipeline.",
                Files = ["src/queue.cc"],
            }, runId: 1), tempRoot)).Id;

            var alphaV2Manual = Assert.IsType<NoteWriteResult.Accepted>(NoteStore.Add(db, new NoteDraft
            {
                Claim = "alpha-v2 configuration defaults to kAlphaV2Cfg = 2",
                Evidence = "alpha-v2/src/cfg.cc:1",
                Tags = ["alpha-v2"],
            }, tempRoot)).Id;

            var unattributed = Assert.IsType<NoteWriteResult.Accepted>(NoteStore.Add(db, new NoteDraft
            {
                Claim = "shared timing measurement across builds is 42ms",
                Evidence = "dotnet test --filter TimingProbe -> 42ms",
                Tags = ["timing"],
            }, tempRoot)).Id;

            var (entries, total) = NoteIndex.List(db, repo: "alpha");
            var ids = entries.Select(e => e.Id).ToHashSet();

            Assert.Equal(2, total);
            Assert.Contains(alphaMined, ids);
            Assert.Contains(alphaManual, ids);
            Assert.DoesNotContain(alphaV2Mined, ids);
            Assert.DoesNotContain(alphaV2Manual, ids);
            Assert.DoesNotContain(unattributed, ids);
        }
        finally
        {
            DeleteTempDir(tempRoot);
        }
    }

    [Fact]
    public void Cloning_a_repo_after_superseding_a_mined_note_does_not_remine_and_links_clone_to_live_head()
    {
        var tempRoot = CreateTempDir();
        try
        {
            var fixtureDir = Path.Combine(tempRoot, "fixture");
            Directory.CreateDirectory(Path.Combine(fixtureDir, "src"));
            GitProcess.Run(fixtureDir, "init");
            GitProcess.Run(fixtureDir, "config", "user.email", "test@example.com");
            GitProcess.Run(fixtureDir, "config", "user.name", "Test User");

            File.WriteAllText(Path.Combine(fixtureDir, "src", "engine.cc"), "constexpr double kCpuctBase = 2800.0;\n");
            GitProcess.Run(fixtureDir, "add", "src/engine.cc");
            GitProcess.Run(fixtureDir, "commit",
                "-m", "Adjust mcts_cpuct_base after deeper endgame sweep",
                "-m", "Measured across 10k games; reduces tactical horizon drops.");

            using var db = MemoryDatabase.Open(Path.Combine(tempRoot, "memory.db"));

            var firstRun = GitCommitMiner.MineAll(db, tempRoot, ["fixture"], new GitMineOptions());
            Assert.Equal(1, Assert.Single(firstRun.Results).NotesCreated);

            var original = Assert.Single(NoteIndex.List(db, repo: "fixture").Entries);
            var superseded = Assert.IsType<NoteWriteResult.Accepted>(
                NoteStore.Supersede(db, original.Id, new NoteDraft
                {
                    Claim = "mcts_cpuct_base updated to 2900 after clone verification",
                    Evidence = "fixture/src/engine.cc:1",
                    Tags = ["fixture"],
                }, why: "re-calibrated", repoRoot: tempRoot));

            var cloneDir = Path.Combine(tempRoot, "clone");
            GitProcess.Run(tempRoot, "clone", fixtureDir, cloneDir);

            var cloneRun = GitCommitMiner.MineAll(db, tempRoot, ["clone"], new GitMineOptions());
            var cloneResult = Assert.Single(cloneRun.Results);
            Assert.Null(cloneResult.Error);
            Assert.Equal(0, cloneResult.NotesCreated);
            Assert.Equal(1, cloneResult.NotesSkipped);

            var cloneHits = MemorySearch.Search(db, "mcts_cpuct_base", repo: "clone", notesOnly: true).Notes;
            Assert.Contains(superseded.Id, cloneHits.Select(n => n.Id));
        }
        finally
        {
            DeleteTempDir(tempRoot);
        }
    }

    private static void SeedRun(Microsoft.Data.Sqlite.SqliteConnection db, long runId = 1)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO run(id, started_at, tool) VALUES ($id, datetime('now'), 'git-mine')";
        cmd.Parameters.AddWithValue("$id", runId);
        cmd.ExecuteNonQuery();
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "amb-gitminer-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDir(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }
}
