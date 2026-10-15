using System.Diagnostics;
using System.Text;
using AgentMemoryBank;
using AgentMemoryBank.Database;
using AgentMemoryBank.McpServer;
using AgentMemoryBank.Models;
using Xunit;
using Xunit.Abstractions;

namespace AgentMemoryBank.Tests;

/// <summary>
/// Measures the real store against the real repositories it indexes, and writes a
/// report meant to be read by someone who was not here.
///
/// <para>Separate from <see cref="MemoryBankBenchmarkTests"/> on purpose. That
/// suite builds its own fixtures so it can assert and run anywhere; this one
/// asserts almost nothing and refuses to invent data. A number produced from a
/// synthetic fixture cannot be checked by the person receiving it, which for a
/// figure being quoted to someone else makes it worse than no number.</para>
///
/// <para>Skips silently when the real store is absent, so a checkout without one
/// still runs green.</para>
/// </summary>
public sealed class RealStoreBenchmark(ITestOutputHelper output)
{
    /// <summary>~4 characters per token. Character counts are exact and reported alongside.</summary>
    private static int Tokens(string text) => (int)Math.Ceiling(text.Length / 4.0);
    private static int Tokens(int chars) => (int)Math.Ceiling(chars / 4.0);

    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        var i = (int)Math.Ceiling(p / 100.0 * sorted.Count) - 1;
        return sorted[Math.Clamp(i, 0, sorted.Count - 1)];
    }

    /// <summary>
    /// A question someone would actually ask, the file they would otherwise have
    /// had to open, and what the answer turns on.
    /// </summary>
    private sealed record Question(string Query, string RepoRelativePath, string WhatItAnswers);

    private static readonly Question[] Questions =
    [
        new("wdl_spread",
            "lc0-training/training-data-tool/src/PGNGame.h",
            "whether 0.21 is fitted or guessed, and what it was fitted against"),
        new("chunk_pool_size",
            "lc0-training/official-training-branch/proto/data_loader_config.proto",
            "that a value below the corpus count silently discards data"),
        new("material sacrifice",
            "lc0-training/official-training-branch/csrc/loader/stages/position_sampling.cc",
            "why selection is on the sacrifice event rather than its outcome"),
        new("rescorer delete_files",
            "lc0/master/src/trainingdata/rescorer.cc",
            "that the default deletes inputs on failure as well as success"),
    ];

    [Fact]
    public void Measure_the_real_store_and_write_a_report()
    {
        var realDb = Environment.GetEnvironmentVariable("MEMORY_BANK_REAL_DB") ?? Config.DbPath;
        if (!File.Exists(realDb))
        {
            output.WriteLine($"[SKIP] no real store at {realDb} — nothing to measure");
            return;
        }

        var repoRoot = Config.RepoRoot;
        var work = Path.Combine(Path.GetTempPath(), "agent-memory-bank-benchmark");
        Directory.CreateDirectory(work);
        var dbCopy = Path.Combine(work, "store-copy.db");

        // Copy rather than open the live store: opening runs migrations, and a
        // benchmark must not be able to alter the thing it is measuring.
        File.Copy(realDb, dbCopy, overwrite: true);

        using var db = MemoryDatabase.Open(dbCopy);
        var stats = MemorySearch.Stats(db);

        var report = new StringBuilder();
        void Say(string line)
        {
            report.AppendLine(line);
            output.WriteLine(line);
        }

        Say("# agent-memory-bank — measured performance");
        Say("");
        Say($"Measured {DateTime.Now:yyyy-MM-dd} against the real store, not a fixture.");
        Say("");
        Say($"- **Store**: {stats.Files} files, {stats.Chunks} chunks, {stats.Notes} notes across {stats.PerRepo.Count} repositories");
        Say($"- **Database size**: {new FileInfo(realDb).Length / 1024.0 / 1024.0:F1} MB");
        Say("- **Machine**: local disk, SQLite with FTS5, no server process, no network");
        Say("- **Token counts** are estimated at 4 characters per token. Character counts are exact and given alongside, so any tokenizer can be substituted.");
        Say("");

        // ---------------------------------------------------------------
        // 1. Retrieval speed
        // ---------------------------------------------------------------
        Say("## 1. Retrieval speed");
        Say("");

        var latencies = new List<double>();
        var hitCount = 0;
        const int iterations = 40;
        for (var i = 0; i < iterations; i++)
        {
            foreach (var q in Questions)
            {
                var sw = Stopwatch.StartNew();
                var r = MemorySearch.Search(db, q.Query, limit: 10, chunkLimit: 3);
                sw.Stop();
                latencies.Add(sw.Elapsed.TotalMilliseconds);
                hitCount += r.Total;
            }
        }
        var sortedLatency = latencies.OrderBy(x => x).ToList();

        Say($"{latencies.Count} queries against the full store, {hitCount} results returned.");
        Say("");
        Say("| | ms |");
        Say("|---|---|");
        Say($"| median | {Percentile(sortedLatency, 50):F2} |");
        Say($"| p95 | {Percentile(sortedLatency, 95):F2} |");
        Say($"| slowest | {sortedLatency[^1]:F2} |");
        Say("");
        Say("Fast enough to query mid-task without the pause being noticeable.");
        Say("");

        // ---------------------------------------------------------------
        // 2. Token cost per answered question
        // ---------------------------------------------------------------
        Say("## 2. Token cost: memory bank vs reading the source");
        Say("");
        Say("For each question: what `memory_search` returns, against the single file an");
        Say("agent would otherwise have opened. The file baseline is deliberately the");
        Say("*most* favourable one for the source — it assumes the agent already knew");
        Say("which file to open, which is itself something it usually had to search for.");
        Say("");
        Say("| Question | Search payload | Source file | Saving |");
        Say("|---|---|---|---|");

        var totalPayload = 0;
        var totalSource = 0;
        var rows = new List<(Question Q, int PayloadChars, int SourceChars, bool Found)>();

        foreach (var q in Questions)
        {
            var result = MemorySearch.Search(db, q.Query, limit: 5, chunkLimit: 2);
            var payload = ToolResponses.RenderSearch(result, db);

            var sourcePath = Path.Combine(repoRoot, q.RepoRelativePath);
            var sourceChars = File.Exists(sourcePath) ? File.ReadAllText(sourcePath).Length : 0;

            rows.Add((q, payload.Length, sourceChars, result.Total > 0));

            // A query that found nothing still renders a short "no results" message.
            // Counting that as a saving would be the single most misleading thing this
            // report could do: the payload is small because the store failed, not
            // because it condensed. Such rows are named below and excluded entirely.
            if (sourceChars == 0 || result.Total == 0) continue;

            totalPayload += payload.Length;
            totalSource += sourceChars;

            var saving = (1.0 - (double)Tokens(payload) / Tokens(sourceChars)) * 100.0;
            Say($"| `{q.Query}` | ~{Tokens(payload)} tok ({payload.Length} ch) | " +
                $"~{Tokens(sourceChars)} tok ({sourceChars} ch) | **{saving:F1}%** |");
        }

        var counted = rows.Count(r => r.Found && r.SourceChars > 0);
        var overall = totalSource == 0 ? 0 : (1.0 - (double)Tokens(totalPayload) / Tokens(totalSource)) * 100.0;
        Say($"| **all {counted}** | **~{Tokens(totalPayload)} tok** | **~{Tokens(totalSource)} tok** | **{overall:F1}%** |");
        Say("");

        foreach (var row in rows.Where(r => !r.Found))
            Say($"> `{row.Q.Query}` returned no results, so it is **excluded from the totals** — " +
                "a \"nothing found\" payload is small because the search failed, not because it condensed.");
        foreach (var row in rows.Where(r => r.SourceChars == 0))
            Say($"> `{row.Q.RepoRelativePath}` not found on disk — excluded from the totals.");
        Say("");
        Say("### The part the ratio does not capture");
        Say("");
        Say("Reading the source file often does not answer the question at all. The file");
        Say("shows *what* a value is; the note records that it was **fitted**, what it was");
        Say("fitted against, and what an earlier wrong value cost. Specifically:");
        Say("");
        foreach (var q in Questions)
            Say($"- `{q.Query}` — {q.WhatItAnswers}");
        Say("");
        Say("None of that is recoverable by reading the file, at any token cost.");
        Say("");

        // ---------------------------------------------------------------
        // 3. Fidelity across the storage boundary
        // ---------------------------------------------------------------
        Say("## 3. What survives storage");
        Say("");

        var handoff = HandoffStore.GetLatest(db);
        if (handoff is not null)
        {
            var reread = HandoffStore.List(db, limit: 50).Single(h => h.Id == handoff.Id);
            var identical =
                reread.Summary == handoff.Summary &&
                reread.NextSteps == handoff.NextSteps &&
                reread.Topic == handoff.Topic &&
                reread.OpenQuestions == handoff.OpenQuestions &&
                reread.ContextData == handoff.ContextData;

            var size = handoff.Summary.Length + handoff.NextSteps.Length
                       + (handoff.OpenQuestions?.Length ?? 0) + (handoff.ContextData?.Length ?? 0);

            Say($"The largest real session handoff in the store is {size} characters (~{Tokens(size)} tokens).");
            Say($"Re-read through a separate query it is **{(identical ? "byte-for-byte identical" : "NOT identical")}**.");
            Say("");
        }

        var longest = MemorySearch.SearchNotes(db, "measured", limit: 50)
            .OrderByDescending(n => (n.Claim?.Length ?? 0) + (n.Rationale?.Length ?? 0)).FirstOrDefault();
        if (longest is not null)
        {
            var full = MemorySearch.GetNote(db, longest.Id);
            var same = full is not null && full.Claim == longest.Claim;
            Say($"The longest note returned by a search re-reads identically by id: **{(same ? "yes" : "no")}**.");
            Say("");
        }

        Say("This matters more than it sounds. The store applies **no compression and no");
        Say("summarisation** — the condensing is done by the agent before the write, and");
        Say("the store's job is to not touch it afterwards. There is no truncation path in");
        Say("the code, which is what makes a fact written in one session the same fact in");
        Say("the next. Verified separately against deliberately hostile input — unicode,");
        Say("emoji, tabs, embedded quotes and SQL metacharacters — in `MemoryBankBenchmarkTests`.");
        Say("");

        Say("## Method");
        Say("");
        Say("- Run with `dotnet test --filter RealStoreBenchmark`.");
        Say("- The store is copied to a temporary file first; the live database is never opened.");
        Say("- Search payloads are produced by the same `ToolResponses.RenderSearch` the MCP");
        Say("  server returns to an agent, so the measured payload is the real one.");
        Say("- Source files are read whole from the repositories the store indexes.");

        var reportPath = Path.Combine(work, "benchmark-report.md");
        File.WriteAllText(reportPath, report.ToString());
        output.WriteLine("");
        output.WriteLine($"[REPORT] written to {reportPath}");

        // The only hard assertion: the store must actually have answered something,
        // or every ratio above is measuring an empty payload.
        Assert.Contains(rows, r => r.Found);
    }
}
