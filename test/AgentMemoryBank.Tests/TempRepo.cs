using System.Diagnostics;

namespace AgentMemoryBank.Tests;

/// <summary>
/// A throwaway git repository plus a database path, so indexer tests exercise
/// the real `git ls-files` path rather than a stubbed file lister. What the
/// indexer gets wrong is usually what git reports, so stubbing it would test
/// the wrong thing.
/// </summary>
internal sealed class TempRepo : IDisposable
{
    public string Root { get; }
    public string RepoName => "fixture";
    public string RepoDirectory => Path.Combine(Root, RepoName);
    public string DbPath => Path.Combine(Root, "test.db");

    public TempRepo()
    {
        Root = Path.Combine(Path.GetTempPath(), "agent-memory-bank-tests", Path.GetRandomFileName());
        Directory.CreateDirectory(RepoDirectory);

        Git("init", "-q");
        Git("config", "user.email", "test@example.invalid");
        Git("config", "user.name", "test");
        Git("config", "commit.gpgsign", "false");
    }

    /// <summary>Write a file (creating directories) and stage it.</summary>
    public void Write(string relativePath, string content)
    {
        var absolute = Path.Combine(RepoDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, content);
        Git("add", "--", relativePath);
    }

    public void Delete(string relativePath)
    {
        File.Delete(Path.Combine(RepoDirectory, relativePath));
        Git("rm", "-q", "--cached", "--", relativePath);
    }

    public void Commit(string message = "fixture") => Git("commit", "-q", "-m", message);

    public void Git(params string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = RepoDirectory,
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

    public void Dispose()
    {
        try
        {
            // git marks objects read-only, which blocks a plain recursive delete.
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a green test run over.
        }
    }
}
