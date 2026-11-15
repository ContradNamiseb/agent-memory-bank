using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace AgentMemoryBank.Tests;

/// <summary>
/// The server as a client runs it: a child process spoken to over its stdin and
/// stdout. Calling the tool methods in-process cannot show what a git child
/// does with the server's own stdin, which is where the handoff tools hung.
/// </summary>
public class McpStdioTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task A_tool_that_runs_git_answers_while_the_server_waits_on_stdin()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "a");
        repo.Commit();

        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repo.RepoDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "agent-memory-bank.dll"));
        info.ArgumentList.Add("serve");
        info.ArgumentList.Add("--db");
        info.ArgumentList.Add(repo.DbPath);

        using var server = Process.Start(info)!;
        try
        {
            _ = server.StandardError.ReadToEndAsync();

            await Send(server, new
            {
                jsonrpc = "2.0", id = 1, method = "initialize",
                @params = new
                {
                    protocolVersion = "2024-11-05",
                    capabilities = new { },
                    clientInfo = new { name = "test", version = "0" },
                },
            });
            await Reply(server, 1);
            await Send(server, new { jsonrpc = "2.0", method = "notifications/initialized" });

            // Nothing more is sent: the server sits in its read of stdin while the
            // tool runs git, as it does between a client's requests.
            await Send(server, new
            {
                jsonrpc = "2.0", id = 2, method = "tools/call",
                @params = new { name = "memory_handoff_list", arguments = new { limit = 5 } },
            });
            var reply = await Reply(server, 2);
            Assert.True(reply.TryGetProperty("result", out _), reply.GetRawText());
        }
        finally
        {
            server.Kill(entireProcessTree: true);
        }
    }

    private static async Task Send(Process server, object message)
    {
        await server.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message));
        await server.StandardInput.FlushAsync();
    }

    private static async Task<JsonElement> Reply(Process server, int id)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (true)
        {
            string? line;
            try
            {
                line = await server.StandardOutput.ReadLineAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"no reply to request {id} within {Timeout.TotalSeconds:F0} s");
            }
            if (line is null)
                throw new InvalidOperationException($"the server exited before replying to request {id}");
            if (line.Length == 0)
                continue;

            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("id", out var replyId) &&
                replyId.ValueKind == JsonValueKind.Number && replyId.GetInt32() == id)
                return doc.RootElement.Clone();
        }
    }
}
