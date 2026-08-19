using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using AgentMemoryBank.Database;
using AgentMemoryBank.Models;
using AgentMemoryBank.Verification;

namespace AgentMemoryBank.Indexing;

public sealed record MinedCommit
{
    public required string Sha { get; init; }
    public required string ShortSha { get; init; }
    public required string Author { get; init; }
    public required DateTimeOffset Date { get; init; }
    public required string Subject { get; init; }
    public required string Body { get; init; }
    public required IReadOnlyList<string> Files { get; init; }
}

public sealed record RepoGitMineResult
{
    public required string Repo { get; init; }
    public int CommitsExamined { get; init; }
    public int NotesCreated { get; init; }
    public int NotesSkipped { get; init; }
    public string? Error { get; init; }
}

public sealed record GitMineRun(long RunId, IReadOnlyList<RepoGitMineResult> Results, TimeSpan Elapsed);

public sealed record GitMineOptions
{
    public int? MaxCommitsPerRepo { get; init; }
    public string? Since { get; init; }
    public bool OverwriteExisting { get; init; }
    public static GitMineOptions Default => new();
}

/// <summary>
/// Phase B ingestion: deterministic git-history mining (no LLM).
///
/// Mines commit messages for historical rationale, architectural decisions,
/// calibration changes, and hazard fixes, recording them as notes with
/// confidence = Inferred.
/// </summary>
public static partial class GitCommitMiner
{
    private const char UnitSeparator = '\u001f';
    private const char RecordSeparator = '\u001e';

    public static GitMineRun MineAll(
        SqliteConnection db,
        string? repoRoot = null,
        IReadOnlyList<string>? repos = null,
        GitMineOptions? options = null,
        string? note = null,
        Action<string, MinedCommit>? onMined = null)
    {
        options ??= GitMineOptions.Default;
        var start = DateTime.UtcNow;
        repoRoot ??= Config.RepoRoot;
        repos ??= Config.Repos;

        var runId = MemoryDatabase.StartRun(db, tool: "phase-b-git-miner", note: note);
        var results = new List<RepoGitMineResult>();

        foreach (var repo in repos)
        {
            var repoDirectory = Config.GetRepoDirectory(repo, repoRoot);
            if (!GitFileScanner.IsRepository(repoDirectory))
            {
                results.Add(new RepoGitMineResult
                {
                    Repo = repo,
                    Error = $"not a git repository: {repoDirectory}"
                });
                continue;
            }

            results.Add(MineRepo(db, repo, repoDirectory, runId, repoRoot, options, onMined));
        }

        return new GitMineRun(runId, results, DateTime.UtcNow - start);
    }

    public static RepoGitMineResult MineRepo(
        SqliteConnection db,
        string repoName,
        string repoDirectory,
        long runId,
        string repoRoot,
        GitMineOptions options,
        Action<string, MinedCommit>? onMined = null)
    {
        var commits = ReadCommits(repoDirectory, options);
        int created = 0, skipped = 0;

        foreach (var commit in commits)
        {
            if (!IsSubstantive(commit))
            {
                skipped++;
                continue;
            }

            if (!options.OverwriteExisting && IsAlreadyMined(db, commit.ShortSha))
            {
                skipped++;
                continue;
            }

            var draft = BuildDraft(repoName, commit, runId);
            var result = NoteStore.Add(db, draft, repoRoot);

            if (result is NoteWriteResult.Accepted)
            {
                created++;
                onMined?.Invoke(repoName, commit);
            }
            else
            {
                skipped++;
            }
        }

        return new RepoGitMineResult
        {
            Repo = repoName,
            CommitsExamined = commits.Count,
            NotesCreated = created,
            NotesSkipped = skipped
        };
    }

    public static IReadOnlyList<MinedCommit> ReadCommits(string repoDirectory, GitMineOptions options)
    {
        var args = new List<string>
        {
            "log",
            "--no-merges",
            $"--format=format:{RecordSeparator}%H{UnitSeparator}%an{UnitSeparator}%aI{UnitSeparator}%s{UnitSeparator}%b{UnitSeparator}",
            "--name-only"
        };

        if (options.MaxCommitsPerRepo is { } max && max > 0)
        {
            args.Add($"-n{max}");
        }

        if (!string.IsNullOrWhiteSpace(options.Since))
        {
            args.Add($"--since={options.Since}");
        }

        var output = RunGit(repoDirectory, [.. args]);
        return ParseLogOutput(output);
    }

    public static IReadOnlyList<MinedCommit> ParseLogOutput(string output)
    {
        var result = new List<MinedCommit>();
        var records = output.Split(RecordSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var record in records)
        {
            var parts = record.Split(UnitSeparator);
            if (parts.Length < 5) continue;

            var sha = parts[0].Trim();
            var author = parts[1].Trim();
            var dateStr = parts[2].Trim();
            var subject = parts[3].Trim();
            var body = parts[4].Trim();
            var fileListPart = parts.Length > 5 ? parts[5] : "";

            if (string.IsNullOrWhiteSpace(sha) || string.IsNullOrWhiteSpace(subject))
                continue;

            var date = DateTimeOffset.TryParse(dateStr, out var parsedDate)
                ? parsedDate
                : DateTimeOffset.UtcNow;

            var files = fileListPart
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(f => f.Trim().Replace('\\', '/'))
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .ToList();

            var shortSha = sha.Length >= 8 ? sha[..8] : sha;

            result.Add(new MinedCommit
            {
                Sha = sha,
                ShortSha = shortSha,
                Author = author,
                Date = date,
                Subject = subject,
                Body = body,
                Files = files
            });
        }

        return result;
    }

    public static bool IsSubstantive(MinedCommit commit)
    {
        var subject = commit.Subject.Trim();
        var body = commit.Body.Trim();

        // Skip obvious noise commit subjects
        if (TrivialCommitRegex().IsMatch(subject) && string.IsNullOrWhiteSpace(body))
            return false;

        // Clean body: strip Co-Authored-By / Signed-off-by trailers
        var cleanBody = CleanTrailers(body);

        // Substantive if:
        // 1. Has non-trivial body (> 30 characters), OR
        // 2. Subject is substantive (contains meaningful action keywords and > 25 characters)
        if (cleanBody.Length >= 30)
            return true;

        if (subject.Length >= 25 && SubstantiveSubjectRegex().IsMatch(subject))
            return true;

        return false;
    }

    public static NoteKind InferKind(MinedCommit commit)
    {
        var text = $"{commit.Subject}\n{commit.Body}";

        if (HazardRegex().IsMatch(text)) return NoteKind.Hazard;
        if (FindingRegex().IsMatch(text)) return NoteKind.Finding;
        if (HowToRegex().IsMatch(text)) return NoteKind.Howto;

        return NoteKind.Decision;
    }

    public static NoteDraft BuildDraft(string repoName, MinedCommit commit, long runId)
    {
        var subject = commit.Subject.Trim();
        var cleanBody = CleanTrailers(commit.Body.Trim());
        var kind = InferKind(commit);

        // Evidence is the commit itself, which is genuinely reproducible.
        //
        // Deliberately NOT "path:1". A commit message says nothing about which
        // line it concerns, so a synthesised line-1 citation is a lie that
        // passes the evidence gate: it anchors the note to whatever sits at the
        // top of the file — a licence header, a shebang, an include — which
        // never changes. Verification would then report the note healthy
        // forever, even after the code it describes was deleted. A note that is
        // honestly unanchored is worth more than one that is falsely anchored,
        // because the first admits it cannot be auto-checked.
        var primaryFile = commit.Files.FirstOrDefault(f => !f.StartsWith("."));
        var evidence = primaryFile is not null
            ? $"git show {commit.ShortSha} -- {primaryFile} ({repoName})"
            : $"git show {commit.ShortSha} ({repoName})";

        var rationale = !string.IsNullOrWhiteSpace(cleanBody)
            ? $"{cleanBody}\n\n(Committed by {commit.Author} on {commit.Date:yyyy-MM-dd})"
            : $"Committed by {commit.Author} on {commit.Date:yyyy-MM-dd}";

        return new NoteDraft
        {
            Claim = subject,
            Rationale = rationale,
            Evidence = evidence,
            Kind = kind,
            Confidence = Confidence.Inferred,
            Tags = ["git-history", repoName],
            RunId = runId
        };
    }

    private static bool IsAlreadyMined(SqliteConnection db, string shortSha)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM note WHERE evidence LIKE '%' || $sha || '%' LIMIT 1";
        cmd.Parameters.AddWithValue("$sha", shortSha);
        return cmd.ExecuteScalar() is not null;
    }

    private static string CleanTrailers(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        var lines = body.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith("Co-Authored-By:", StringComparison.OrdinalIgnoreCase) &&
                           !line.StartsWith("Signed-off-by:", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return string.Join("\n", lines).Trim();
    }

    private static string RunGit(string workingDirectory, params string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("could not start git");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed in {workingDirectory}: {stderr.Trim()}");
        return stdout;
    }

    [GeneratedRegex(@"^(wip|format|formatting|typo|clean\s*up|cleanup|bump|update\s*readme|update\s*version|version\s*bump)$", RegexOptions.IgnoreCase)]
    private static partial Regex TrivialCommitRegex();

    [GeneratedRegex(@"(fix|crash|deadlock|leak|calibrate|tune|resolve|rearchitect|speedup|refactor|prevent|restore|support|implement|add|remove|switch|handle|optimize)", RegexOptions.IgnoreCase)]
    private static partial Regex SubstantiveSubjectRegex();

    [GeneratedRegex(@"(crash|deadlock|leak|delete|corrupt|hang|bug|overflow|silent|panic|freeze|mismatch|race\s*condition|segfault|broken|fail|revert)", RegexOptions.IgnoreCase)]
    private static partial Regex HazardRegex();

    [GeneratedRegex(@"(measure|benchmark|speedup|evaluate|rmse|profile|quantiz|accuracy|plateau|loss|latency|speed|performance|test\s*result)", RegexOptions.IgnoreCase)]
    private static partial Regex FindingRegex();

    [GeneratedRegex(@"(setup|build|config|how\s*to|launch|install|usage|command\s*line)", RegexOptions.IgnoreCase)]
    private static partial Regex HowToRegex();
}
