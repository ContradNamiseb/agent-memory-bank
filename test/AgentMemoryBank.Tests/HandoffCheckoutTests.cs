using System.Diagnostics;
using AgentMemoryBank.Continuity;
using AgentMemoryBank.Database;
using AgentMemoryBank.Models;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentMemoryBank.Tests;

/// <summary>
/// A handoff is a claim about one checkout. These tests write one in a real
/// repository, move away from it the ways people actually do — another branch,
/// another worktree, another clone — and check that reading it says so first.
/// </summary>
public sealed class HandoffCheckoutTests
{
    private static SessionHandoff SaveHere(SqliteConnection db, string directory, string topic = "migration") =>
        HandoffStore.Save(db, new SessionHandoffDraft
        {
            Agent = "Claude Code",
            Topic = topic,
            Summary = "Migration done.",
            NextSteps = "Run the backfill.",
            Checkout = GitCheckout.Capture(directory),
        });

    private static TempRepo CommittedRepo()
    {
        var repo = new TempRepo();
        repo.Write("README.md", "fixture\n");
        repo.Commit("first");
        return repo;
    }

    /// <summary>git in a directory other than the fixture's own.</summary>
    private static void GitIn(string directory, params string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {stderr}");
    }

    [Fact]
    public void Save_records_the_checkout_from_git()
    {
        using var repo = CommittedRepo();
        File.WriteAllText(Path.Combine(repo.RepoDirectory, "scratch.txt"), "uncommitted\n");
        using var db = MemoryDatabase.Open(repo.DbPath);

        SaveHere(db, repo.RepoDirectory);
        var checkout = HandoffStore.GetLatest(db)!.Checkout;

        Assert.NotNull(checkout);
        Assert.NotNull(checkout.Branch);
        Assert.Equal(40, checkout.Head!.Length);
        Assert.Equal(1, checkout.DirtyPaths);
        Assert.Null(checkout.Upstream);
        Assert.Null(checkout.Ahead);
        Assert.Equal(repo.RepoName, Path.GetFileName(checkout.Worktree));
    }

    [Fact]
    public void Reading_in_the_same_checkout_says_it_matches_and_warns_of_nothing()
    {
        using var repo = CommittedRepo();
        using var db = MemoryDatabase.Open(repo.DbPath);
        SaveHere(db, repo.RepoDirectory);

        var lookup = HandoffReader.Latest(db, null, HandoffScope.Repo, repo.RepoDirectory);

        Assert.NotNull(lookup.Handoff);
        var line = Assert.Single(lookup.Preamble);
        Assert.StartsWith("Checkout: matches", line);
    }

    [Fact]
    public void Moving_on_along_the_branch_warns_and_says_the_work_is_included()
    {
        using var repo = CommittedRepo();
        using var db = MemoryDatabase.Open(repo.DbPath);
        var saved = SaveHere(db, repo.RepoDirectory);

        repo.Git("checkout", "-q", "-b", "next");
        repo.Write("later.txt", "later\n");
        repo.Commit("later");

        var preamble = HandoffReader.Latest(db, null, HandoffScope.Repo, repo.RepoDirectory).Preamble;

        Assert.StartsWith(
            $"WARNING: written on `{saved.Checkout!.Branch}` @ `{saved.Checkout.ShortHead}` in `", preamble[0]);
        Assert.Contains("; you are on `next` @ `", preamble[0]);
        Assert.EndsWith(CheckoutCheck.VerifyAdvice, preamble[0]);
        Assert.Contains(preamble, l => l.StartsWith("HEAD is 1 commit ahead of the handoff's commit"));
        Assert.DoesNotContain(preamble, l => l.Contains("does not exist in this checkout"));
    }

    [Fact]
    public void Switching_to_a_branch_without_the_work_says_it_is_not_here()
    {
        using var repo = CommittedRepo();
        repo.Git("branch", "base");
        repo.Git("checkout", "-q", "-b", "feature-x");
        repo.Write("migration.sql", "-- done\n");
        repo.Commit("migration");
        using var db = MemoryDatabase.Open(repo.DbPath);
        SaveHere(db, repo.RepoDirectory);

        repo.Git("checkout", "-q", "base");
        var preamble = HandoffReader.Latest(db, null, HandoffScope.Repo, repo.RepoDirectory).Preamble;

        Assert.StartsWith("WARNING: written on `feature-x`", preamble[0]);
        Assert.Contains("you are on `base`", preamble[0]);
        Assert.Contains(preamble, l => l.StartsWith("HEAD does not contain the handoff's commit"));
        Assert.Contains(preamble, l => l.Contains("Branch `feature-x` had no upstream when written"));
    }

    [Fact]
    public void Another_worktree_is_found_by_repo_scope_flagged_and_hidden_by_worktree_scope()
    {
        using var repo = CommittedRepo();
        var otherWorktree = Path.Combine(repo.Root, "wt-a");
        repo.Git("worktree", "add", "-q", "-b", "wt-a", otherWorktree);
        using var db = MemoryDatabase.Open(repo.DbPath);
        SaveHere(db, otherWorktree);

        var repoScope = HandoffReader.Latest(db, null, HandoffScope.Repo, repo.RepoDirectory);
        Assert.NotNull(repoScope.Handoff);
        Assert.StartsWith("WARNING: written on `wt-a`", repoScope.Preamble[0]);
        Assert.Contains("wt-a`; you are on", repoScope.Preamble[0]);

        var worktreeScope = HandoffReader.Latest(db, null, HandoffScope.Worktree, repo.RepoDirectory);
        Assert.Null(worktreeScope.Handoff);
        Assert.StartsWith("No handoff for this worktree", worktreeScope.Miss);
        Assert.Contains("1 other handoff exists", worktreeScope.Miss);

        Assert.NotNull(HandoffReader.Latest(db, null, HandoffScope.Worktree, otherWorktree).Handoff);
    }

    [Fact]
    public void A_commit_missing_from_this_clone_is_called_out_first_on_its_own_line()
    {
        const string origin = "https://example.invalid/team/fixture.git";
        using var repo = CommittedRepo();
        repo.Git("remote", "add", "origin", origin);

        var clone = Path.Combine(repo.Root, "clone");
        repo.Git("clone", "-q", repo.RepoDirectory, clone);
        GitIn(clone, "remote", "set-url", "origin", origin);

        // Work the clone never receives: committed after it was made, never pushed.
        repo.Write("migration.sql", "-- done\n");
        repo.Commit("migration");
        using var db = MemoryDatabase.Open(repo.DbPath);
        var saved = SaveHere(db, repo.RepoDirectory);

        var lookup = HandoffReader.Latest(db, null, HandoffScope.Repo, clone);

        Assert.NotNull(lookup.Handoff);   // same origin, so the same repository
        Assert.StartsWith($"WARNING: commit `{saved.Checkout!.ShortHead}` from this handoff does not exist in this checkout",
            lookup.Preamble[0]);
        Assert.StartsWith("WARNING: written on", lookup.Preamble[1]);
        Assert.DoesNotContain(lookup.Preamble, l => l.Contains("different repository"));
    }

    [Fact]
    public void A_handoff_saved_without_a_checkout_is_only_in_scope_all_and_says_so()
    {
        using var repo = CommittedRepo();
        using var db = MemoryDatabase.Open(repo.DbPath);
        HandoffStore.Save(db, new SessionHandoffDraft
        {
            Agent = "Codex", Topic = "legacy", Summary = "s", NextSteps = "n",
        });

        var scoped = HandoffReader.Latest(db, null, HandoffScope.Repo, repo.RepoDirectory);
        Assert.Null(scoped.Handoff);
        Assert.Contains("1 other handoff exists", scoped.Miss);
        Assert.Contains("scope 'all'", scoped.Miss);

        var all = HandoffReader.Latest(db, null, HandoffScope.All, repo.RepoDirectory);
        Assert.NotNull(all.Handoff);
        Assert.StartsWith("WARNING: this handoff has no recorded checkout", Assert.Single(all.Preamble));
    }

    [Fact]
    public void Outside_a_repository_the_scope_widens_and_the_reader_is_told()
    {
        using var repo = CommittedRepo();
        using var db = MemoryDatabase.Open(repo.DbPath);
        SaveHere(db, repo.RepoDirectory);
        var notARepo = Path.Combine(repo.Root, "plain");
        Directory.CreateDirectory(notARepo);

        var lookup = HandoffReader.Latest(db, null, HandoffScope.Repo, notARepo);

        Assert.NotNull(lookup.Handoff);
        Assert.StartsWith("Not in a git checkout", lookup.Preamble[0]);
        Assert.Contains("you are not in a git checkout", lookup.Preamble[1]);
    }

    [Fact]
    public void The_ssh_and_https_forms_of_one_remote_are_one_repository()
    {
        var https = GitCheckout.NormalizeRemote("https://github.com/ContradNamiseb/agent-memory-bank.git");
        var ssh = GitCheckout.NormalizeRemote("git@github.com:ContradNamiseb/agent-memory-bank.git");
        var sshUrl = GitCheckout.NormalizeRemote("ssh://git@github.com/ContradNamiseb/agent-memory-bank");

        Assert.Equal("github.com/contradnamiseb/agent-memory-bank", https);
        Assert.Equal(https, ssh);
        Assert.Equal(https, sshUrl);
        Assert.NotEqual(https, GitCheckout.NormalizeRemote("https://github.com/someone-else/agent-memory-bank"));
    }

    [Fact]
    public void An_existing_store_gains_the_checkout_columns_and_keeps_its_handoffs()
    {
        using var repo = new TempRepo();
        using (var legacy = new SqliteConnection(
                   new SqliteConnectionStringBuilder { DataSource = repo.DbPath, Pooling = false }.ToString()))
        {
            legacy.Open();
            legacy.Execute("""
                CREATE TABLE session_handoff (
                  id INTEGER PRIMARY KEY AUTOINCREMENT, agent TEXT NOT NULL, topic TEXT NOT NULL,
                  summary TEXT NOT NULL, next_steps TEXT NOT NULL, active_files TEXT,
                  open_questions TEXT, context_data TEXT,
                  created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')));
                INSERT INTO session_handoff (agent, topic, summary, next_steps)
                VALUES ('Codex', 'old work', 'done', 'next');
                PRAGMA user_version = 1;
                """);
        }

        using var db = MemoryDatabase.Open(repo.DbPath);

        Assert.Equal(Migrations.CurrentVersion, db.Scalar<long>("PRAGMA user_version"));
        foreach (var column in new[] { "repo_id", "worktree", "branch", "head_commit", "dirty_paths", "upstream", "ahead" })
            Assert.True(Migrations.ColumnExists(db, "session_handoff", column), column);

        var old = HandoffStore.GetLatest(db);
        Assert.NotNull(old);
        Assert.Equal("old work", old.Topic);
        Assert.Null(old.Checkout);
    }
}
