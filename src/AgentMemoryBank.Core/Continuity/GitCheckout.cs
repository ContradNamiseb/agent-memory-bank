using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AgentMemoryBank.Models;

namespace AgentMemoryBank.Continuity;

/// <summary>
/// Reads the state of a checkout from git, for recording on a handoff and for
/// checking a handoff against where it is being read.
///
/// <para>Every query tolerates failure and answers null or false instead of
/// throwing: a directory that is not a repository, a branch with no upstream
/// and a commit that is not here are all ordinary answers, and a handoff must
/// still save and read when git has nothing to say.</para>
/// </summary>
public static partial class GitCheckout
{
    /// <summary>The checkout containing <paramref name="directory"/>, or null when it is not in one.</summary>
    public static CheckoutState? Capture(string directory)
    {
        if (!Directory.Exists(directory)) return null;

        // Both paths in one call. --path-format=absolute keeps the common dir
        // from coming back as a bare ".git" relative to wherever git ran.
        var paths = Lines(Run(directory, "rev-parse", "--path-format=absolute", "--git-common-dir", "--show-toplevel"));
        if (paths is not [var commonDir, var topLevel]) return null;

        var origin = Run(directory, "config", "--get", "remote.origin.url")?.Trim();
        var head = Run(directory, "rev-parse", "--verify", "-q", "HEAD")?.Trim();
        var branch = Run(directory, "symbolic-ref", "--short", "-q", "HEAD")?.Trim();
        var status = Run(directory, "status", "--porcelain", "--untracked-files=normal");
        var upstream = Run(directory, "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}")?.Trim();

        int? ahead = null;
        if (!string.IsNullOrEmpty(upstream) && !string.IsNullOrEmpty(head) &&
            int.TryParse(Run(directory, "rev-list", "--count", "@{upstream}..HEAD")?.Trim(), out var count))
        {
            ahead = count;
        }

        return new CheckoutState
        {
            RepoId = string.IsNullOrEmpty(origin) ? NormalizePath(commonDir) : NormalizeRemote(origin),
            Worktree = NormalizePath(topLevel),
            Branch = string.IsNullOrEmpty(branch) ? null : branch,
            Head = string.IsNullOrEmpty(head) ? null : head,
            DirtyPaths = Lines(status)?.Length ?? 0,
            Upstream = string.IsNullOrEmpty(upstream) ? null : upstream,
            Ahead = ahead,
        };
    }

    /// <summary>True when the commit exists in the repository containing <paramref name="directory"/>.</summary>
    public static bool CommitExists(string directory, string commit) =>
        Run(directory, "cat-file", "-e", commit + "^{commit}") is not null;

    /// <summary>True when <paramref name="ancestor"/> is reachable from <paramref name="descendant"/>.</summary>
    public static bool IsAncestor(string directory, string ancestor, string descendant) =>
        Run(directory, "merge-base", "--is-ancestor", ancestor, descendant) is not null;

    /// <summary>Commits reachable from <paramref name="to"/> but not from <paramref name="from"/>; null if git cannot say.</summary>
    public static int? CommitsBetween(string directory, string from, string to) =>
        int.TryParse(Run(directory, "rev-list", "--count", $"{from}..{to}")?.Trim(), out var count) ? count : null;

    /// <summary>True when two <see cref="CheckoutState.RepoId"/> values name the same repository.</summary>
    public static bool SameRepo(string? a, string? b) =>
        a is not null && b is not null &&
        (Path.IsPathFullyQualified(a) && Path.IsPathFullyQualified(b)
            ? SamePath(a, b)
            : string.Equals(a, b, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A remote URL as "host/owner/repo", lowercase, so the SSH and HTTPS forms
    /// of one repository compare equal. A local-path remote stays a path.
    /// </summary>
    public static string NormalizeRemote(string url)
    {
        var trimmed = url.Trim();
        string? hostAndPath = null;

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && !uri.IsFile && !string.IsNullOrEmpty(uri.Host))
        {
            hostAndPath = uri.Host + uri.AbsolutePath;
        }
        else if (ScpLikeRemote().Match(trimmed) is { Success: true } scp)
        {
            // user@host:owner/repo. A one-letter "host" is a Windows drive, not a host.
            hostAndPath = scp.Groups["host"].Value + "/" + scp.Groups["path"].Value;
        }
        else if (Uri.TryCreate(trimmed, UriKind.Absolute, out var file) && file.IsFile)
        {
            return NormalizePath(file.LocalPath);
        }

        if (hostAndPath is null) return Path.IsPathFullyQualified(trimmed) ? NormalizePath(trimmed) : trimmed;

        hostAndPath = hostAndPath.TrimEnd('/');
        if (hostAndPath.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) hostAndPath = hostAndPath[..^4];
        return hostAndPath.ToLowerInvariant();
    }

    [GeneratedRegex(@"^(?:[^@/\\]+@)?(?<host>[A-Za-z0-9.-]{2,}):(?!//)/?(?<path>.+)$")]
    private static partial Regex ScpLikeRemote();

    /// <summary>
    /// Path equality as the file system sees it: case-insensitive on Windows,
    /// where git and the shell can disagree on a drive letter's case.
    /// </summary>
    public static bool SamePath(string? a, string? b) =>
        a is not null && b is not null &&
        string.Equals(NormalizePath(a), NormalizePath(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>Absolute, in the platform's separators, with no trailing separator.</summary>
    public static string NormalizePath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        return full.Length > root.Length
            ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : full;
    }

    private static string[]? Lines(string? output) =>
        output?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Standard output on exit code 0, otherwise null. Never throws.</summary>
    private static string? Run(string workingDirectory, params string[] arguments)
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

        try
        {
            using var process = Process.Start(info);
            if (process is null) return null;
            // Drain stderr concurrently: a child blocked on a full stderr pipe
            // never exits while stdout is being read to the end.
            var stderr = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            _ = stderr.Result;
            return process.ExitCode == 0 ? stdout : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // git is not installed or cannot be started: there is no checkout to report.
            return null;
        }
    }
}
