using System.Diagnostics;
using System.Text;

namespace AgentMemoryBank.Indexing;

/// <summary>
/// Shared git subprocess runner for Phase A file discovery and Phase B commit mining.
/// </summary>
public static class GitProcess
{
    /// <summary>Run git inside a repository working directory, capturing UTF-8 stdout.</summary>
    public static string Run(string workingDirectory, params string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("could not start git");
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        var stderr = stderrTask.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed in {workingDirectory}: {stderr.Trim()}");
        return stdout;
    }
}
