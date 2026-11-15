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
            return GitProcess.Run(repoDirectory, "rev-parse", "HEAD").Trim();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Tracked paths, repo-relative, using forward slashes as git reports them.</summary>
    public static IReadOnlyList<string> ListFiles(string repoDirectory) =>
        GitProcess.Run(repoDirectory, "ls-files", "-z")
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
}
