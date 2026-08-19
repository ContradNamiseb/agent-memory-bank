using AgentMemoryBank.Database;
using Xunit;

namespace AgentMemoryBank.Tests;

public class ConfigTests
{
    /// <summary>
    /// The walk up to the checkout root has no loud failure mode: when the
    /// marker does not match it silently falls back to the current directory,
    /// so the default database lands wherever the tool was launched from. That
    /// went unnoticed until the tool was run from another directory.
    /// </summary>
    [Fact]
    public void ProjectRoot_finds_the_checkout_rather_than_the_launch_directory()
    {
        var root = Config.ProjectRoot;

        Assert.True(
            File.Exists(Path.Combine(root, "AgentMemoryBank.slnx")) ||
            File.Exists(Path.Combine(root, "AgentMemoryBank.sln")),
            $"ProjectRoot resolved to '{root}', which holds no solution file");
        Assert.True(File.Exists(Path.Combine(root, "README.md")), "README.md should sit at the checkout root");
    }

    [Fact]
    public void DbPath_defaults_beside_the_solution()
    {
        Assert.Equal(Path.Combine(Config.ProjectRoot, "memory.db"), Config.DbPath);
        Assert.Equal("memory.db", Path.GetFileName(Config.DbPath));
    }

    [Fact]
    public void Opening_a_database_in_an_unwritable_place_explains_which_path_failed()
    {
        // A directory cannot be opened as a database file. The message must name
        // the path, not just repeat SQLite's "unable to open database file".
        using var repo = new TempRepo();
        var directoryNotFile = repo.RepoDirectory;

        var error = Assert.Throws<MemoryBankException>(() => MemoryDatabase.Open(directoryNotFile));
        Assert.Contains(Path.GetFileName(directoryNotFile), error.Message);
        Assert.Contains("MEMORY_BANK_DB", error.Message);
    }
}
