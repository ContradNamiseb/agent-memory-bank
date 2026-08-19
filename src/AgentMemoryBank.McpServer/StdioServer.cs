using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentMemoryBank.McpServer;

/// <summary>
/// Runs the memory bank as an MCP server over stdio.
/// </summary>
internal static class StdioServer
{
    public static async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        var builder = Host.CreateApplicationBuilder();

        // stdout carries JSON-RPC and nothing else. A single stray line of log
        // output there corrupts the stream and the client drops the connection,
        // which presents as a mysterious startup failure rather than as a log
        // problem — so every provider is pointed at stderr.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new()
                {
                    Name = "agent-memory-bank",
                    Version = typeof(StdioServer).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
                };
            })
            .WithStdioServerTransport()
            .WithTools<McpTools>();

        using var host = builder.Build();
        await host.RunAsync(cancellationToken);
        return 0;
    }
}
