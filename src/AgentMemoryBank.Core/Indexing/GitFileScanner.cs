using System.Diagnostics;
using System.Text;

namespace AgentMemoryBank.Indexing;

/// <summary>
/// The little git the indexer needs. Shelling out beats a git library here:
/// `git ls-files` already knows what is tracked, what is ignored, and what the
/// user's own config excludes, and being wrong about that is the whole risk.
/// </summary>
public static class GitFileScanner
{
    /// <summary>True when the directory is a git working tree (a worktree file counts).</summary>
    public static bool IsRepository(string directory) =>
        Directory.Exists(Path.Combine(directory, ".git")) || File.Exists(Path.Combine(directory, ".git"));

    /// <summary>Current commit, or null for a repository with no commits yet.</summary>
    public static string? HeadRevision(string repoDirectory)
    {
        try
        {
            return Run(repoDirectory, "rev-parse", "HEAD").Trim();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Tracked paths, repo-relative, using forward slashes as git reports them.</summary>
    public static IReadOnlyList<string> ListFiles(string repoDirectory) =>
        Run(repoDirectory, "ls-files", "-z")
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);

    private static string Run(string workingDirectory, params string[] arguments)
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
}
