using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentMemoryBank.McpServer;

/// <summary>What a session transcript says about where the session had got to.</summary>
internal sealed record TranscriptSnapshot(
    IReadOnlyList<string> UserRequests,
    string LastAssistantText,
    IReadOnlyList<string> EditedFiles,
    string? Branch,
    string? Cwd);

/// <summary>
/// Reads a Claude Code session transcript: one JSON record per line, written by
/// the client. Unreadable lines are skipped, never fatal: the transcript is
/// appended to while the hook reads it, so the last line can be half-written.
/// </summary>
internal static partial class ClaudeTranscript
{
    private const string UsageMarker = "\"cache_read_input_tokens\"";

    // Lines with the marker kept for parsing, newest last. More than one because
    // the newest need not be the client's own usage block (see LastPromptTokens).
    private const int UsageCandidates = 16;

    private static readonly HashSet<string> EditTools = new(StringComparer.Ordinal) { "Edit", "Write", "NotebookEdit" };

    // Turns the client records as "user" that the user did not type: slash
    // commands and their output, reminders, and background-task notifications.
    // A session that ends on a notification would otherwise name its handoff
    // "<task-notification> <task-id>...".
    [GeneratedRegex(@"^\s*<(command-name|local-command|system-reminder|task-notification)")]
    private static partial Regex InjectedTurnRegex();

    /// <summary>
    /// Prompt size of the most recent request: input, cache-read and
    /// cache-creation tokens from the last usage block. Matches the marker as
    /// plain text and parses only the newest matches, because threshold mode
    /// runs this on every prompt and a transcript reaches tens of megabytes:
    /// parsing every match doubles the time. The newest match is not always the
    /// one wanted: a subagent's result carries the subagent's usage outside
    /// <c>message</c>, and the last line can be half-written. So it walks back
    /// to the newest line that is the client's own usage block.
    /// </summary>
    public static long LastPromptTokens(string path)
    {
        var candidates = new Queue<string>(UsageCandidates);
        foreach (var line in File.ReadLines(path))
        {
            if (!line.Contains(UsageMarker, StringComparison.Ordinal))
                continue;
            if (candidates.Count == UsageCandidates)
                candidates.Dequeue();
            candidates.Enqueue(line);
        }

        foreach (var line in candidates.Reverse())
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (PromptTokens(doc.RootElement) is { } tokens)
                    return tokens;
            }
            catch (JsonException)
            {
                // Half-written: try the line before.
            }
        }
        return 0;
    }

    /// <summary>The prompt size a record's own usage block gives, or null when it has none.</summary>
    private static long? PromptTokens(JsonElement record) =>
        record.ValueKind == JsonValueKind.Object &&
        record.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object &&
        message.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
            ? Tokens(usage, "input_tokens") + Tokens(usage, "cache_read_input_tokens") +
              Tokens(usage, "cache_creation_input_tokens")
            : null;

    public static TranscriptSnapshot Read(string path)
    {
        var requests = new List<string>();
        var files = new List<string>();
        var seenFiles = new HashSet<string>(StringComparer.Ordinal);
        var lastAssistant = "";
        string? branch = null;
        string? cwd = null;

        foreach (var line in File.ReadLines(path))
        {
            if (line.Length < 2)
                continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }

            using (doc)
            {
                var record = doc.RootElement;
                if (record.ValueKind != JsonValueKind.Object)
                    continue;
                if (Text(record, "cwd") is { Length: > 0 } recordCwd)
                    cwd = recordCwd;
                if (Text(record, "gitBranch") is { Length: > 0 } recordBranch)
                    branch = recordBranch;
                if (!record.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                    continue;

                switch (Text(record, "type"))
                {
                    // A compact summary is the earlier conversation replayed as a
                    // user turn; taking it as the latest request misnames the handoff.
                    case "user" when Text(message, "content") is { Length: > 0 } request &&
                                     !IsTrue(record, "isCompactSummary") &&
                                     !InjectedTurnRegex().IsMatch(request):
                        requests.Add(request);
                        break;

                    case "assistant" when message.TryGetProperty("content", out var blocks) &&
                                          blocks.ValueKind == JsonValueKind.Array:
                        foreach (var block in blocks.EnumerateArray())
                        {
                            if (block.ValueKind != JsonValueKind.Object)
                                continue;
                            var blockType = Text(block, "type");
                            if (blockType == "text" && Text(block, "text") is { Length: > 0 } text)
                                lastAssistant = text;
                            else if (blockType == "tool_use" &&
                                     Text(block, "name") is { } tool && EditTools.Contains(tool) &&
                                     block.TryGetProperty("input", out var input) &&
                                     input.ValueKind == JsonValueKind.Object &&
                                     Text(input, "file_path") is { Length: > 0 } file &&
                                     !IsSessionLocal(file) && seenFiles.Add(file))
                                files.Add(file);
                        }
                        break;
                }
            }
        }

        return new TranscriptSnapshot(requests, lastAssistant, files, branch, cwd);
    }

    /// <summary>
    /// Files in the session's own scratch space: a <c>scratchpad</c> folder, or
    /// Claude Code's folder under the system temp directory. They are gone by the
    /// time anyone reads the handoff, so listing them only crowds out real files.
    /// Not the whole temp directory: a repository can live there too.
    /// </summary>
    internal static bool IsSessionLocal(string file)
    {
        string full;
        try { full = Path.GetFullPath(file); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }

        var claudeTemp = Path.Combine(Path.GetTempPath(), "claude") + Path.DirectorySeparatorChar;
        return full.StartsWith(claudeTemp, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
               full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   .Any(segment => segment.Equals("scratchpad", StringComparison.OrdinalIgnoreCase));
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsTrue(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static long Tokens(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var tokens)
            ? tokens
            : 0;
}
