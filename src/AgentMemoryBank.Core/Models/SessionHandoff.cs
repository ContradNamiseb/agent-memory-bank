namespace AgentMemoryBank.Models;

/// <summary>
/// A cross-agent session handoff snapshot.
/// Allows uninterrupted continuation when switching between Antigravity (Gemini),
/// Claude Code (Anthropic), VS Code / GitHub Copilot, and Cursor.
/// </summary>
public sealed record SessionHandoff
{
    public long Id { get; init; }
    public required string Agent { get; init; }
    public required string Topic { get; init; }
    public required string Summary { get; init; }
    public required string NextSteps { get; init; }
    public string? ActiveFiles { get; init; }
    public string? OpenQuestions { get; init; }
    public string? ContextData { get; init; }
    public required string CreatedAt { get; init; }
}

public sealed record SessionHandoffDraft
{
    public required string Agent { get; init; }
    public required string Topic { get; init; }
    public required string Summary { get; init; }
    public required string NextSteps { get; init; }
    public string? ActiveFiles { get; init; }
    public string? OpenQuestions { get; init; }
    public string? ContextData { get; init; }
}
