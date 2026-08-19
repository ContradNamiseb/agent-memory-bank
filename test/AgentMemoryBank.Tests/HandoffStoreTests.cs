using AgentMemoryBank.Database;
using AgentMemoryBank.Models;
using Xunit;

namespace AgentMemoryBank.Tests;

public sealed class HandoffStoreTests
{
    [Fact]
    public void Saves_and_retrieves_latest_handoff()
    {
        using var repo = new TempRepo();
        using var db = MemoryDatabase.Open(repo.DbPath);

        var first = HandoffStore.Save(db, new SessionHandoffDraft
        {
            Agent = "Antigravity (Gemini)",
            Topic = "DirectML softplus fix",
            Summary = "Completed safe softplus linearization and test suite.",
            NextSteps = "Verify recovery checkpoint under low memory.",
            ActiveFiles = "src/engine/backend/layers.py",
            OpenQuestions = "Is MIN_AVAILABLE_GB 4.0 sufficient?"
        });

        Assert.True(first.Id > 0);

        var second = HandoffStore.Save(db, new SessionHandoffDraft
        {
            Agent = "Claude Code",
            Topic = "KDA TUI panel layout fix",
            Summary = "Fixed Static widget auto height clipping table cells.",
            NextSteps = "Run smoke test script and check rendering.",
            ActiveFiles = "src/engine/ui/app.tcss",
            OpenQuestions = null
        });

        Assert.True(second.Id > first.Id);

        // Get overall latest
        var latest = HandoffStore.GetLatest(db);
        Assert.NotNull(latest);
        Assert.Equal(second.Id, latest.Id);
        Assert.Equal("Claude Code", latest.Agent);
        Assert.Equal("KDA TUI panel layout fix", latest.Topic);

        // Filter by agent
        var latestAntigravity = HandoffStore.GetLatest(db, "Antigravity");
        Assert.NotNull(latestAntigravity);
        Assert.Equal(first.Id, latestAntigravity.Id);
        Assert.Equal("DirectML softplus fix", latestAntigravity.Topic);

        // List handoffs
        var list = HandoffStore.List(db, limit: 10);
        Assert.Equal(2, list.Count);
        Assert.Equal(second.Id, list[0].Id);
        Assert.Equal(first.Id, list[1].Id);
    }
}
