using System.Diagnostics;
using System.Text;
using AgentMemoryBank.Database;
using AgentMemoryBank.Indexing;
using AgentMemoryBank.McpServer;
using AgentMemoryBank.Models;
using Xunit;
using Xunit.Abstractions;

namespace AgentMemoryBank.Tests;

/// <summary>
/// Benchmarks for the three properties the store is actually bought for: it has
/// to be fast enough to query mid-task, small enough to be worth loading instead
/// of the source, and lossless enough that a fact written in one session is the
/// same fact in the next.
///
/// <para>These assert loose bounds and print exact numbers. A benchmark that
/// asserts a tight latency lands as a flaky failure on a busy machine and gets
/// muted, which costs more than it ever caught — so the thresholds here sit where
/// crossing them means a real regression (an index dropped, a scan gone
/// quadratic), not where a noisy laptop crosses them. Read the printed numbers
/// for actual performance; the assertions only catch collapse.</para>
/// </summary>
public sealed class MemoryBankBenchmarkTests(ITestOutputHelper output)
{
    // ~4 characters per token. Crude, but this measures a RATIO of two payloads
    // in the same notation, so the error largely divides out.
    private static int EstimateTokens(string text) => (int)Math.Ceiling(text.Length / 4.0);

    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        var index = (int)Math.Ceiling(p / 100.0 * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    // -----------------------------------------------------------------
    // 1. SPEED — write and query latency
    // -----------------------------------------------------------------
    [Fact]
    public void Write_and_query_latency_stay_within_budget()
    {
        const int noteCount = 500;
        const int queryCount = 200;

        using var repo = new TempRepo();
        using var db = MemoryDatabase.Open(repo.DbPath);

        var writeMs = new List<double>(noteCount);
        var accepted = 0;
        var rejected = new Dictionary<string, int>();

        var swTotal = Stopwatch.StartNew();
        for (var i = 0; i < noteCount; i++)
        {
            var draft = new NoteDraft
            {
                Claim = $"the {Subsystem(i)} retry budget of {i + 1}ms is fitted to measured p95 latency, not chosen",
                Rationale = $"Established by replaying trace batch {i} against the {Subsystem(i)} path.",
                Evidence = $"py scripts/measure_latency_{i}.py traces.jsonl -> p95 = {200 + i}ms",
                Kind = (NoteKind)(i % 4),
                Confidence = Confidence.Measured,
                Tags = ["benchmark", Subsystem(i)],
            };

            var sw = Stopwatch.StartNew();
            var result = NoteStore.Add(db, draft, repo.RepoDirectory);
            sw.Stop();
            writeMs.Add(sw.Elapsed.TotalMilliseconds);

            if (result is NoteWriteResult.Accepted) accepted++;
            else if (result is NoteWriteResult.Rejected r)
                rejected[r.Code] = rejected.GetValueOrDefault(r.Code) + 1;
        }
        swTotal.Stop();

        // A benchmark that silently measured 500 rejections would report a
        // wonderful write throughput for doing nothing at all.
        foreach (var (code, count) in rejected)
            output.WriteLine($"[WRITE] rejected {count} x {code}");
        Assert.True(accepted >= noteCount * 0.95,
            $"only {accepted}/{noteCount} notes were accepted; the throughput number would be measuring rejections");

        var sortedWrites = writeMs.OrderBy(x => x).ToList();
        output.WriteLine($"[SPEED] {accepted} notes written in {swTotal.ElapsedMilliseconds} ms");
        output.WriteLine($"[SPEED]   median {Percentile(sortedWrites, 50):F3} ms/write, " +
                         $"p95 {Percentile(sortedWrites, 95):F3} ms, max {sortedWrites[^1]:F3} ms");

        string[] queries = ["retry budget", "latency fitted", "loader", "p95 measured", "backend retry"];
        var queryMs = new List<double>(queryCount);
        var totalHits = 0;

        for (var i = 0; i < queryCount; i++)
        {
            var query = queries[i % queries.Length];
            var sw = Stopwatch.StartNew();
            var result = MemorySearch.Search(db, query, limit: 10);
            sw.Stop();
            queryMs.Add(sw.Elapsed.TotalMilliseconds);
            totalHits += result.Total;
        }

        var sortedQueries = queryMs.OrderBy(x => x).ToList();
        output.WriteLine($"[SPEED] {queryCount} queries, {totalHits} total hits");
        output.WriteLine($"[SPEED]   median {Percentile(sortedQueries, 50):F3} ms/query, " +
                         $"p95 {Percentile(sortedQueries, 95):F3} ms, max {sortedQueries[^1]:F3} ms");

        Assert.True(totalHits > 0, "every query returned nothing — the FTS index is not being populated");
        Assert.True(Percentile(sortedQueries, 95) < 250,
            $"p95 query latency {Percentile(sortedQueries, 95):F1} ms — an index is missing or a scan went linear");
    }

    // -----------------------------------------------------------------
    // 2. SPEED — does the write path degrade as the store fills?
    // -----------------------------------------------------------------
    [Fact]
    public void Write_cost_does_not_degrade_as_the_store_grows()
    {
        // Every Add runs FindDuplicate and FindSimilar against existing notes.
        // If either degrades with row count, writes go quadratic and the store
        // becomes unusable at exactly the size where it starts being valuable.
        const int noteCount = 600;

        using var repo = new TempRepo();
        using var db = MemoryDatabase.Open(repo.DbPath);

        var writeMs = new List<double>(noteCount);
        for (var i = 0; i < noteCount; i++)
        {
            var draft = new NoteDraft
            {
                Claim = $"the {Subsystem(i)} cache eviction threshold of {i + 1} entries is measured, not guessed",
                Evidence = $"dotnet run --project bench -- eviction {i} -> steady state at {i + 1}",
                Confidence = Confidence.Measured,
            };
            var sw = Stopwatch.StartNew();
            NoteStore.Add(db, draft, repo.RepoDirectory);
            sw.Stop();
            writeMs.Add(sw.Elapsed.TotalMilliseconds);
        }

        var quartile = noteCount / 4;
        var firstQuartile = writeMs.Take(quartile).OrderBy(x => x).ToList();
        var lastQuartile = writeMs.Skip(noteCount - quartile).OrderBy(x => x).ToList();

        var firstMedian = Percentile(firstQuartile, 50);
        var lastMedian = Percentile(lastQuartile, 50);
        var ratio = firstMedian <= 0 ? 1.0 : lastMedian / firstMedian;

        output.WriteLine($"[SCALING] first quartile median {firstMedian:F3} ms/write (rows 0-{quartile})");
        output.WriteLine($"[SCALING] last  quartile median {lastMedian:F3} ms/write (rows {noteCount - quartile}-{noteCount})");
        output.WriteLine($"[SCALING] degradation ratio {ratio:F2}x over {noteCount} rows");

        Assert.True(ratio < 6.0,
            $"write cost grew {ratio:F1}x across {noteCount} rows — the duplicate/similarity scan is not staying sublinear");
    }

    // -----------------------------------------------------------------
    // 3. TOKENS — what the agent receives vs the source it replaces
    // -----------------------------------------------------------------
    [Fact]
    public void Search_payload_is_a_small_fraction_of_the_source_it_indexes()
    {
        const int fileCount = 8;

        using var repo = new TempRepo();
        using var db = MemoryDatabase.Open(repo.DbPath);

        var sourceChars = 0;
        var answerFileChars = 0;
        for (var f = 0; f < fileCount; f++)
        {
            var source = GenerateSourceFile($"Module_{f}", lines: 220,
                marker: f == 3 ? "eviction threshold tuning lives here" : null);
            sourceChars += source.Length;
            if (f == 3) answerFileChars = source.Length;
            repo.Write($"src/module_{f}.cc", source);
        }
        repo.Commit();

        var indexResult = Indexer.IndexRepo(db, repo.RepoName, repo.RepoDirectory);
        Assert.Null(indexResult.Error);
        Assert.True(indexResult.Chunks > 0, "nothing was chunked, so the comparison would be meaningless");
        output.WriteLine($"[TOKENS] indexed {indexResult.Indexed} files -> {indexResult.Chunks} chunks");

        NoteStore.Add(db, new NoteDraft
        {
            Claim = "the eviction threshold is fitted to measured steady-state occupancy, not chosen by eye",
            Rationale = "Refitting it moves the miss rate by 4x; the value looks arbitrary in the source.",
            Evidence = $"{repo.RepoName}/src/module_3.cc:12",
            Confidence = Confidence.Measured,
        }, repo.RepoDirectory);

        var result = MemorySearch.Search(db, "eviction threshold", limit: 10, chunkLimit: 3);
        var payload = ToolResponses.RenderSearch(result, db);

        var sourceTokens = (int)Math.Ceiling(sourceChars / 4.0);
        var answerFileTokens = (int)Math.Ceiling(answerFileChars / 4.0);
        var payloadTokens = EstimateTokens(payload);

        // Two baselines, because the flattering one is not the honest one.
        // Against the whole tree the saving is enormous but the comparison is
        // rigged: nobody reads eight files to answer one question. The baseline
        // that decides whether this store earns its place is the single file an
        // agent would have opened instead — already knowing which file to open,
        // which is itself something the store had to tell it.
        var vsTree = (1.0 - (double)payloadTokens / sourceTokens) * 100.0;
        var vsOneFile = (1.0 - (double)payloadTokens / answerFileTokens) * 100.0;

        output.WriteLine($"[TOKENS] whole indexed tree ({fileCount} files): ~{sourceTokens} tokens ({sourceChars} chars)");
        output.WriteLine($"[TOKENS] the one file holding the answer:  ~{answerFileTokens} tokens ({answerFileChars} chars)");
        output.WriteLine($"[TOKENS] memory_search payload:            ~{payloadTokens} tokens ({payload.Length} chars)");
        output.WriteLine($"[TOKENS] reduction vs whole tree:  {vsTree:F2}%  (generous baseline)");
        output.WriteLine($"[TOKENS] reduction vs single file: {vsOneFile:F2}%  (the honest one)");
        output.WriteLine($"[TOKENS] payload actually returned:\n{Indent(payload)}");

        Assert.True(result.Total > 0, "the search returned nothing, so the payload is small for the wrong reason");
        Assert.True(vsOneFile > 80.0,
            $"against the single file an agent would otherwise open, the payload saved only {vsOneFile:F1}%");
    }

    // -----------------------------------------------------------------
    // 4. COMPRESSION — what survives the write/read boundary
    // -----------------------------------------------------------------
    [Fact]
    public void Handoff_round_trips_byte_exact_including_hostile_content()
    {
        // The store does not summarise or re-encode: the compression is done by
        // the agent before the write. So the property to hold it to is fidelity
        // — whatever was handed in comes back identical. Silent truncation here
        // would be undetectable at the call site and would corrupt exactly the
        // long, detailed handoffs that matter most.
        using var repo = new TempRepo();
        using var db = MemoryDatabase.Open(repo.DbPath);

        var longSummary = string.Join("\n", Enumerable.Range(0, 400)
            .Select(i => $"Line {i}: measured p95 at {i * 3}ms against a budget of 250ms."));

        var draft = new SessionHandoffDraft
        {
            Agent = "Claude Code",
            Topic = "Unicode, quotes ' \" and SQL-ish -- payload; DROP TABLE note; --",
            Summary = longSummary,
            NextSteps = "Keep allocations <= 512MB RAM\nCompatible with DirectML 1.14\nLines 88, 104, 312",
            ActiveFiles = "csrc/loader/stages/position_sampling.cc, tf/net.py",
            OpenQuestions = "Does ✓ unicode survive? Emoji \U0001F600? Tabs\tand\ttrailing spaces   ?",
            ContextData = "{\"nested\":{\"json\":[1,2,3]},\"quote\":\"he said \\\"hi\\\"\"}",
        };

        var saved = HandoffStore.Save(db, draft);
        var read = HandoffStore.GetLatest(db);

        Assert.NotNull(read);
        Assert.Equal(saved.Id, read.Id);

        // Field by field, so a failure names the field that was mangled.
        Assert.Equal(draft.Agent, read.Agent);
        Assert.Equal(draft.Topic, read.Topic);
        Assert.Equal(draft.Summary, read.Summary);
        Assert.Equal(draft.NextSteps, read.NextSteps);
        Assert.Equal(draft.ActiveFiles, read.ActiveFiles);
        Assert.Equal(draft.OpenQuestions, read.OpenQuestions);
        Assert.Equal(draft.ContextData, read.ContextData);

        // The specific facts a handoff exists to carry.
        Assert.Contains("88", read.NextSteps);
        Assert.Contains("312", read.NextSteps);
        Assert.Contains("DirectML 1.14", read.NextSteps);
        Assert.Contains("512MB", read.NextSteps);

        var writtenTokens = EstimateTokens(draft.Summary + draft.NextSteps + draft.OpenQuestions);
        output.WriteLine($"[FIDELITY] round-tripped {draft.Summary.Length} chars of summary byte-exact");
        output.WriteLine($"[FIDELITY] handoff payload ~{writtenTokens} tokens, 0 fields altered");
        output.WriteLine("[FIDELITY] unicode, emoji, tabs, embedded quotes and SQL metacharacters all preserved");
    }

    // -----------------------------------------------------------------
    // 5. COMPRESSION — is the stored fact still findable?
    // -----------------------------------------------------------------
    [Fact]
    public void Stored_facts_are_retrievable_by_the_keywords_an_agent_would_use()
    {
        // Lossless storage is worth nothing if retrieval cannot reach it. This is
        // the failure mode that actually bites: the note is there, the query is
        // reasonable, and the answer comes back empty.
        using var repo = new TempRepo();
        using var db = MemoryDatabase.Open(repo.DbPath);

        NoteStore.Add(db, new NoteDraft
        {
            Claim = "wdl_scale 1.13 and wdl_spread 0.21 are fitted to real W/D/L outcomes, not chosen by eye",
            Rationale = "RMSE 0.015, stable across five Fishtest files; an earlier 0.27 was wrong by 2x RMSE.",
            Evidence = "py scripts/measure_pgn_wdl.py -> RMSE 0.015",
            Confidence = Confidence.Measured,
            Tags = ["calibration"],
        }, repo.RepoDirectory);

        NoteStore.Add(db, new NoteDraft
        {
            Claim = "chunk_pool_size below the corpus chunk count discards sources oldest-first with no error",
            Evidence = "py scripts/count_chunks.py -> 2002208 chunk files",
            Kind = NoteKind.Hazard,
            Confidence = Confidence.Measured,
            Tags = ["loader"],
        }, repo.RepoDirectory);

        (string Query, string Expect)[] shouldHit =
        [
            ("wdl_spread", "wdl_spread"),
            ("wdl_scale fitted", "wdl_scale"),
            ("chunk_pool_size", "chunk_pool_size"),
            ("corpus oldest", "oldest-first"),
        ];

        foreach (var (query, expect) in shouldHit)
        {
            var result = MemorySearch.Search(db, query, limit: 5);
            var claims = string.Join(" | ", result.Notes.Select(n => n.Claim));
            output.WriteLine($"[RECALL] \"{query}\" -> {result.Notes.Count} note(s), mode={result.Mode}");
            Assert.True(result.Notes.Count > 0, $"\"{query}\" returned nothing but the fact is stored");
            Assert.Contains(expect, claims, StringComparison.OrdinalIgnoreCase);
        }
    }

    // -----------------------------------------------------------------
    // 6. The recall cliff, pinned as behaviour rather than folklore
    // -----------------------------------------------------------------
    [Fact]
    public void A_sentence_query_still_finds_the_note_via_the_loosened_retry()
    {
        // README.md, AGENTS.md and the global agent instructions all tell agents
        // that a sentence query still returns something, and that an empty result
        // is therefore a real "not recorded" rather than a phrasing problem. That
        // promise lives in prose across four files and is load-bearing — an agent
        // that believes an empty result is its own fault will keep rephrasing
        // instead of reporting the gap. Pinned here so the docs cannot drift back.
        using var repo = new TempRepo();
        using var db = MemoryDatabase.Open(repo.DbPath);

        NoteStore.Add(db, new NoteDraft
        {
            Claim = "wdl_spread 0.21 is fitted to measured W/D/L outcomes, not chosen by eye",
            Evidence = "py scripts/measure_pgn_wdl.py -> RMSE 0.015",
            Confidence = Confidence.Measured,
        }, repo.RepoDirectory);

        var keyword = MemorySearch.Search(db, "wdl_spread", limit: 5);
        var sentence = MemorySearch.Search(db,
            "what wdl spread value should I use when converting pgn games to training data", limit: 5);

        output.WriteLine($"[QUERY SHAPE] keyword  \"wdl_spread\" -> {keyword.Notes.Count} note(s), mode={keyword.Mode}");
        output.WriteLine($"[QUERY SHAPE] sentence (13 terms)    -> {sentence.Notes.Count} note(s), mode={sentence.Mode}");

        Assert.True(keyword.Notes.Count > 0, "the short keyword query must find the note");
        Assert.Equal(MatchMode.Strict, keyword.Mode);

        // The documented promise, asserted rather than merely reported.
        Assert.True(sentence.Notes.Count > 0,
            "a sentence query returned nothing — the loosened retry is gone, and the guidance in " +
            "README.md, AGENTS.md and the global agent instructions is now wrong");
        Assert.Equal(MatchMode.Loosened, sentence.Mode);
        Assert.Contains(sentence.Notes, n => n.Claim.Contains("wdl_spread", StringComparison.OrdinalIgnoreCase));

        output.WriteLine("[QUERY SHAPE] the sentence query recovered through the loosened retry, as documented");

        // A single term cannot loosen (OR of one term is the same query), so the
        // retry genuinely does nothing there — worth pinning so nobody documents
        // a fallback that does not exist for the most common query shape of all.
        Assert.False(MemorySearch.CanLoosen("wdl_spread"));
        Assert.True(MemorySearch.CanLoosen("wdl spread value"));
    }

    // -----------------------------------------------------------------
    // HELPERS
    // -----------------------------------------------------------------
    private static string Subsystem(int i) => (i % 5) switch
    {
        0 => "loader",
        1 => "backend",
        2 => "indexer",
        3 => "scheduler",
        _ => "encoder",
    };

    private static string Indent(string text) =>
        string.Join("\n", text.Split('\n').Select(l => "    " + l));

    private static string GenerateSourceFile(string name, int lines, string? marker)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#include <vector>");
        sb.AppendLine($"namespace bench {{  // {name}");
        if (marker is not null)
        {
            sb.AppendLine($"// {marker}");
            sb.AppendLine("constexpr int kEvictionThreshold = 512;  // eviction threshold");
        }
        for (var i = 0; i < lines; i++)
        {
            sb.AppendLine($"void ProcessStep_{i}(std::vector<int>& state) {{");
            sb.AppendLine($"  state.push_back({i});  // executing step {i} with active buffers");
            sb.AppendLine("}");
        }
        sb.AppendLine("}  // namespace bench");
        return sb.ToString();
    }
}
