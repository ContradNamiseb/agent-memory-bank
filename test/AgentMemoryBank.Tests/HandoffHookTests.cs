using System.Text.Json;
using AgentMemoryBank.Continuity;
using AgentMemoryBank.Database;
using AgentMemoryBank.McpServer;
using AgentMemoryBank.Models;
using Xunit;

namespace AgentMemoryBank.Tests;

/// <summary>
/// `hook handoff`: the automatic handoff written from a Claude Code transcript
/// before compaction. It replaced a PowerShell script, so these pin the same
/// behaviour: what the snapshot takes from the transcript, which checkout it
/// records, and when threshold mode stays quiet.
/// </summary>
public class HandoffHookTests
{
    private const int Window = 1000;

    [Fact]
    public void Force_mode_saves_the_transcript_snapshot_with_the_session_checkout()
    {
        using var repo = NewRepo();
        var edited = Path.Combine(repo.RepoDirectory, "src", "fit.py");
        var transcript = WriteTranscript(repo, repo.RepoDirectory, promptTokens: 960,
            User("first ask"),
            new { type = "user", isCompactSummary = true, message = new { role = "user", content = "Summary of the earlier conversation" } },
            User("<command-name>/clear</command-name>"),
            new { type = "user", message = new { role = "user", content = new object[] { new { type = "tool_result", content = "ok" } } } },
            User("fix the retry_jitter fit"),
            Assistant("Fitted it; next is the p99 check.",
                Tool("Edit", edited),
                Tool("Write", Path.Combine(Path.GetTempPath(), "claude", "session-x", "tasks", "build.output")),
                Tool("Edit", Path.Combine(repo.Root, "scratchpad", "probe.py")),
                Tool("Edit", Path.Combine(repo.RepoDirectory, "scratchpad.md")),
                Tool("Edit", edited),
                Tool("Read", Path.Combine(repo.RepoDirectory, "README.md"))),
            // Arrives after the last real request, as a "user" turn nobody typed.
            User("<task-notification> <task-id>b1</task-id> <status>completed</status> </task-notification>"));

        var output = Run(repo, AutoHandoffMode.Force, Payload(transcript, repo.RepoDirectory, "session-1"));

        using var notice = JsonDocument.Parse(output.Trim());
        Assert.Equal("Context at 96% - session handoff written to the memory bank.",
            notice.RootElement.GetProperty("systemMessage").GetString());
        Assert.True(notice.RootElement.GetProperty("suppressOutput").GetBoolean());

        var saved = Latest(repo);
        Assert.Equal("Claude Code (auto)", saved.Agent);
        Assert.Equal("fix the retry_jitter fit", saved.Topic);
        Assert.Contains($"AUTO-SAVED at 96% of the {Window}-token context window (force).", saved.Summary);
        Assert.Contains("Session session-1, branch main,", saved.Summary);
        Assert.Contains("- first ask\n- fix the retry_jitter fit\n", saved.Summary);
        Assert.Contains("Fitted it; next is the p99 check.", saved.Summary);
        Assert.DoesNotContain("earlier conversation", saved.Summary);
        Assert.DoesNotContain("command-name", saved.Summary);
        Assert.DoesNotContain("task-notification", saved.Summary);
        // In order, once each; the scratchpad and Claude temp files dropped, a
        // repository file that merely has "scratchpad" in its name kept.
        Assert.Equal($"{edited}, {Path.Combine(repo.RepoDirectory, "scratchpad.md")}", saved.ActiveFiles);
        Assert.Contains($"Read the transcript at {transcript}", saved.NextSteps);

        Assert.NotNull(saved.Checkout);
        Assert.Equal(GitCheckout.Capture(repo.RepoDirectory)!.Worktree, saved.Checkout!.Worktree);
    }

    [Fact]
    public void A_request_sent_with_an_image_counts_and_client_written_turns_do_not()
    {
        using var repo = NewRepo();
        var transcript = WriteTranscript(repo, repo.RepoDirectory, promptTokens: 100,
            User("first ask"),
            // A scheduled prompt arrives as an isMeta string: the session's work, kept.
            new { type = "user", isMeta = true, message = new { role = "user", content = "Scheduled check: the agora inbox" } },
            // An attachment turns the content into a list of blocks.
            UserBlocks(new { type = "image", source = new { type = "base64", media_type = "image/png", data = "iVBORw0K" } },
                       new { type = "text", text = "why does the fit drift here?" }),
            // Written by the client in the same list form: an interruption note, a
            // reminder, and isMeta lists such as a skill's body or an image's path.
            UserBlocks(new { type = "text", text = "[Request interrupted by user for tool use]" }),
            UserBlocks(new { type = "text", text = "<system-reminder> The user started a background task </system-reminder>" }),
            new { type = "user", isMeta = true, message = new { role = "user", content = new object[] { new { type = "text", text = "Base directory for this skill: C:/skills/x" } } } },
            new { type = "user", isMeta = true, message = new { role = "user", content = new object[] { new { type = "text", text = "[Image: source: C:/tmp/shot.png]" } } } });

        Run(repo, AutoHandoffMode.Force, Payload(transcript, repo.RepoDirectory, "session-3"));

        var saved = Latest(repo);
        Assert.Equal("why does the fit drift here?", saved.Topic);
        Assert.Contains("- first ask\n- Scheduled check: the agora inbox\n- why does the fit drift here?\n\n", saved.Summary);
        Assert.DoesNotContain("interrupted", saved.Summary);
        Assert.DoesNotContain("system-reminder", saved.Summary);
        Assert.DoesNotContain("Base directory", saved.Summary);
        Assert.DoesNotContain("Image: source", saved.Summary);
    }

    [Fact]
    public void A_payload_folder_that_no_longer_exists_falls_back_to_the_transcript_folder()
    {
        using var repo = NewRepo();
        var transcript = WriteTranscript(repo, repo.RepoDirectory, promptTokens: 100, User("keep going on the fit"));
        var gone = Path.Combine(repo.Root, "deleted-worktree");

        Run(repo, AutoHandoffMode.Force, Payload(transcript, gone, "session-2"));

        var saved = Latest(repo);
        Assert.NotNull(saved.Checkout);
        Assert.Equal(GitCheckout.Capture(repo.RepoDirectory)!.Worktree, saved.Checkout!.Worktree);
        Assert.Contains($"cwd {repo.RepoDirectory}.", saved.Summary);
    }

    [Fact]
    public void Threshold_mode_waits_for_the_threshold_and_then_saves_once_per_interval()
    {
        using var repo = NewRepo();
        var below = WriteTranscript(repo, repo.RepoDirectory, promptTokens: 500, User("still plenty of room"));

        // A session id is payload text: it must not be able to name a path.
        var payload = Payload(below, repo.RepoDirectory, "../../escape");
        Assert.Equal("", Run(repo, AutoHandoffMode.Threshold, payload));
        Assert.Equal(0, Count(repo));

        var above = WriteTranscript(repo, repo.RepoDirectory, promptTokens: 960, User("nearly full now"));
        payload = Payload(above, repo.RepoDirectory, "../../escape");
        Assert.Contains("Context at 96%", Run(repo, AutoHandoffMode.Threshold, payload));
        Assert.Equal("", Run(repo, AutoHandoffMode.Threshold, payload));
        Assert.Equal(1, Count(repo));

        var stamps = Directory.GetFiles(Path.Combine(repo.Root, "hook-state"), "*.last");
        Assert.Single(stamps);
        Assert.False(File.Exists(Path.Combine(repo.Root, "..", "..", "escape.last")));

        // Force mode ignores both the threshold and the interval.
        Assert.Contains("Context at 96%", Run(repo, AutoHandoffMode.Force, payload));
        Assert.Equal(2, Count(repo));
    }

    [Fact]
    public void Usage_outside_the_message_or_on_a_half_written_line_does_not_reset_occupancy()
    {
        using var repo = NewRepo();
        var transcript = WriteTranscript(repo, repo.RepoDirectory, promptTokens: 960, User("nearly full now"));
        // A subagent's result carries the subagent's usage, outside message; a
        // record still being written can stop inside a usage block.
        File.AppendAllLines(transcript,
        [
            JsonSerializer.Serialize(new
            {
                type = "user",
                message = new { role = "user", content = new object[] { new { type = "tool_result", content = "done" } } },
                toolUseResult = new { usage = new { input_tokens = 3, cache_read_input_tokens = 2, cache_creation_input_tokens = 1 } },
            }),
            "{\"type\":\"assistant\",\"message\":{\"usage\":{\"cache_read_input_tokens\":",
        ]);

        Assert.Equal(960, ClaudeTranscript.LastPromptTokens(transcript));
        Assert.Contains("Context at 96%", Run(repo, AutoHandoffMode.Threshold, Payload(transcript, repo.RepoDirectory, "s")));
    }

    [Fact]
    public void Without_a_store_it_stays_silent_and_creates_nothing()
    {
        using var repo = new TempRepo();
        var transcript = WriteTranscript(repo, repo.RepoDirectory, promptTokens: 960, User("save me"));
        var output = new StringWriter();

        Assert.Equal(0, HookRunner.RunHandoff(repo.DbPath, AutoHandoffMode.Force, 95,
            new StringReader(Payload(transcript, repo.RepoDirectory, "s")), output, ClaudeDir(repo)));

        Assert.Equal("", output.ToString());
        Assert.False(File.Exists(repo.DbPath));
        Assert.False(Directory.Exists(Path.Combine(repo.Root, "hook-state")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("""{"session_id":"s"}""")]
    [InlineData("""{"transcript_path":"/no/such/transcript.jsonl"}""")]
    public void Bad_or_incomplete_input_is_silent_and_saves_nothing(string stdin)
    {
        using var repo = NewRepo();
        Assert.Equal("", Run(repo, AutoHandoffMode.Force, stdin));
        Assert.Equal(0, Count(repo));
    }

    [Fact]
    public void The_window_comes_from_autoCompactWindow_or_defaults_to_200k()
    {
        using var repo = new TempRepo();
        Assert.Equal(Window, HookRunner.ContextWindow(ClaudeDir(repo)));
        Assert.Equal(200_000, HookRunner.ContextWindow(Path.Combine(repo.Root, "no-claude-dir")));
    }

    private static TempRepo NewRepo()
    {
        var repo = new TempRepo();
        repo.Write("README.md", "fixture\n");
        repo.Commit("initial commit");
        MemoryDatabase.Open(repo.DbPath).Dispose();
        return repo;
    }

    private static string Run(TempRepo repo, AutoHandoffMode mode, string stdin)
    {
        var output = new StringWriter();
        Assert.Equal(0, HookRunner.RunHandoff(repo.DbPath, mode, 95, new StringReader(stdin), output, ClaudeDir(repo)));
        return output.ToString();
    }

    private static string ClaudeDir(TempRepo repo)
    {
        var dir = Path.Combine(repo.Root, "claude-config");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), $$"""{ "autoCompactWindow": {{Window}} }""");
        return dir;
    }

    private static string Payload(string transcript, string cwd, string sessionId) =>
        JsonSerializer.Serialize(new
        {
            session_id = sessionId,
            hook_event_name = "PreCompact",
            transcript_path = transcript,
            cwd,
        });

    private static string WriteTranscript(TempRepo repo, string cwd, int promptTokens, params object[] records)
    {
        var lines = records.Select(record => JsonSerializer.Serialize(record)).ToList();
        // Where the session is, as the client stamps every record, and the last
        // request's usage: what occupancy is measured from.
        lines.Add(JsonSerializer.Serialize(new
        {
            type = "assistant",
            cwd,
            gitBranch = "main",
            message = new
            {
                role = "assistant",
                content = Array.Empty<object>(),
                usage = new { input_tokens = promptTokens - 10, cache_read_input_tokens = 6, cache_creation_input_tokens = 4 },
            },
        }));
        lines.Add("{\"type\":\"assistant\",\"message\":"); // half-written last line: skipped

        var path = Path.Combine(repo.Root, $"transcript-{Guid.NewGuid():N}.jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    private static object User(string content) =>
        new { type = "user", message = new { role = "user", content } };

    private static object UserBlocks(params object[] blocks) =>
        new { type = "user", message = new { role = "user", content = blocks } };

    private static object Assistant(string text, params object[] tools) =>
        new
        {
            type = "assistant",
            message = new { role = "assistant", content = new object[] { new { type = "text", text } }.Concat(tools).ToArray() },
        };

    private static object Tool(string name, string filePath) =>
        new { type = "tool_use", name, input = new { file_path = filePath } };

    private static SessionHandoff Latest(TempRepo repo)
    {
        using var db = MemoryDatabase.Open(repo.DbPath);
        return HandoffStore.GetLatest(db) ?? throw new InvalidOperationException("no handoff saved");
    }

    private static long Count(TempRepo repo)
    {
        using var db = MemoryDatabase.Open(repo.DbPath);
        return HandoffStore.Count(db);
    }
}
