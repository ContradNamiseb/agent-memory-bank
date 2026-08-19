using AgentMemoryBank;
using AgentMemoryBank.Database;
using AgentMemoryBank.Indexing;
using AgentMemoryBank.Models;
using AgentMemoryBank.Verification;

namespace AgentMemoryBank.McpServer;

/// <summary>
/// Entry point. One executable serves both roles: the operational verbs used
/// from a terminal, and 'serve', which an MCP client launches to speak
/// JSON-RPC over stdio.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            return (args.FirstOrDefault() ?? "help") switch
            {
                "index" => Index(args[1..]),
                "search" => Search(args[1..]),
                "stats" => Stats(args[1..]),
                "note" => Note(args[1..]),
                "verify" => VerifyNotes(args[1..]),
                "serve" => StdioServer.RunAsync().GetAwaiter().GetResult(),
                "help" or "--help" or "-h" => Help(0),
                var unknown => Fail($"unknown command '{unknown}'"),
            };
        }
        catch (MemoryBankException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"{e.GetType().Name}: {e.Message}");
            return 1;
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return Help(2);
    }

    private static int Help(int exitCode)
    {
        Console.WriteLine($"""
            agent-memory-bank — local store of durable project knowledge

            Commands:
              index  [--repo NAME]... [--full] [--note TEXT] [--db PATH] [--root DIR] [-v]
                     Phase A: index tracked, non-vendored source files. Incremental by
                     content hash; --full re-chunks everything and optimises the index.

              search QUERY [--repo NAME] [--kind K] [--limit N] [--full] [--db PATH]
                     BM25 over notes and chunks. --full prints whole chunk bodies with
                     line numbers instead of a snippet.

              stats  [--db PATH]
                     Row counts per repository.

              note add --claim TEXT --evidence TEXT [--why TEXT] [--kind K]
                       [--confidence C] [--tags a,b] [--db PATH]
                     Record a finding by hand. Evidence is required and checked.

              verify [--id N] [--db PATH]
                     Re-check that the code each note cites still says what it says.

              serve  Run as an MCP stdio server, exposing memory_search, memory_get and
                     memory_stats. Speaks JSON-RPC on stdin/stdout; logs go to stderr.
                     Intended to be launched by an MCP client, not by hand.

            Resolved configuration (from {Config.SettingsFileName}, or the environment):
              database   {Config.DbPath}
              repo root  {Config.RepoRoot}
              repos      {(Config.Repos.Count == 0 ? "(none configured)" : string.Join(", ", Config.Repos))}

            Environment: MEMORY_BANK_DB, MEMORY_BANK_REPO_ROOT, MEMORY_BANK_REPOS,
                         MEMORY_BANK_SETTINGS
            """);
        return exitCode;
    }

    private static int Index(string[] args)
    {
        var options = CommandLine.Parse(args);
        var repos = options.Repos.Count > 0 ? options.Repos : Config.Repos;
        if (repos.Count == 0)
        {
            Console.Error.WriteLine(Config.ConfigurationHelp);
            return 2;
        }

        using var db = MemoryDatabase.Open(options.Db);
        var run = Indexer.Reindex(db,
            repoRoot: options.Root,
            repos: repos,
            full: options.Full,
            note: options.Note);

        if (options.Full) Indexer.OptimizeFts(db);

        var failed = false;
        foreach (var result in run.Results)
        {
            if (result.Error is not null)
            {
                Console.Error.WriteLine($"  {result.Repo}: ERROR {result.Error}");
                failed = true;
                continue;
            }
            Console.WriteLine(
                $"  {result.Repo,-26} indexed {result.Indexed,4}  unchanged {result.Skipped,4}  " +
                $"removed {result.Removed,3}  chunks {result.Chunks,5}  @{Short(result.GitRev)}");
            foreach (var error in result.FileErrors) Console.Error.WriteLine($"      skipped {error}");
        }

        var stats = MemorySearch.Stats(db);
        Console.WriteLine($"\nrun {run.RunId} in {run.Elapsed.TotalSeconds:F1}s -> {options.Db}");
        Console.WriteLine($"total: {stats.Files} files, {stats.Chunks} chunks, {stats.Notes} notes");
        if (options.Verbose)
            foreach (var repo in stats.PerRepo)
                Console.WriteLine($"  {repo.Repo,-26} {repo.Files} files, {repo.Chunks} chunks");

        return failed ? 1 : 0;
    }

    private static string Short(string? rev) => rev is null ? "none" : rev[..Math.Min(8, rev.Length)];

    private static int Search(string[] args)
    {
        var options = CommandLine.Parse(args);
        if (options.Query.Length == 0) return Fail("search needs a query");

        using var db = MemoryDatabase.Open(options.Db);
        var result = MemorySearch.Search(db, options.Query,
            repo: options.Repo, kind: options.Kind, limit: options.Limit);

        if (result.Notes.Count > 0)
        {
            Console.WriteLine($"notes ({result.Notes.Count}):");
            foreach (var note in result.Notes)
            {
                Console.WriteLine($"\n  [note {note.Id}] {note.Kind?.ToString().ToLowerInvariant() ?? "-"} / " +
                                  $"{note.Confidence?.ToString().ToLowerInvariant() ?? "-"}  score {note.Score:F2}");
                Console.WriteLine($"  {note.Claim}");
                if (note.Evidence is not null) Console.WriteLine($"  evidence: {note.Evidence}");
                if (note.VerifiedAt is not null) Console.WriteLine($"  verified: {note.VerifiedAt}");
            }
            Console.WriteLine();
        }

        Console.WriteLine($"chunks ({result.Chunks.Count}):");
        foreach (var hit in result.Chunks)
        {
            var symbol = hit.Symbol is null ? "" : $"  {hit.Symbol}";
            Console.WriteLine($"\n  [chunk {hit.Id}] {hit.Citation}-{hit.LineEnd}  {hit.Kind}{symbol}  score {hit.Score:F2}");
            if (options.Full)
            {
                var full = MemorySearch.GetChunk(db, hit.Id);
                if (full is null) continue;
                var lines = full.Value.Chunk.Body.Split('\n');
                for (var i = 0; i < lines.Length; i++)
                    Console.WriteLine($"    {full.Value.Chunk.LineStart + i,5}| {lines[i]}");
            }
            else
            {
                Console.WriteLine($"    {Collapse(hit.Snippet)}");
            }
        }

        if (result.Total == 0) Console.WriteLine("  (no matches)");
        return 0;
    }

    private static string Collapse(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Hand-written notes: the backfill path, and the honest test of the schema.</summary>
    private static int Note(string[] args)
    {
        if (args.FirstOrDefault() != "add") return Fail("usage: note add --claim TEXT --evidence TEXT [...]");

        var options = CommandLine.Parse(args[1..]);
        if (options.Claim is null || options.Evidence is null)
            return Fail("note add needs both --claim and --evidence");

        using var db = MemoryDatabase.Open(options.Db);
        var draft = new NoteDraft
        {
            Claim = options.Claim,
            Evidence = options.Evidence,
            Rationale = options.Why,
            Kind = options.Kind ?? NoteKind.Finding,
            Confidence = options.Confidence ?? AgentMemoryBank.Models.Confidence.Inferred,
            Tags = options.Tags,
        };

        switch (NoteStore.Add(db, draft, options.Root))
        {
            case NoteWriteResult.Accepted accepted:
                Console.WriteLine($"note {accepted.Id} recorded{Describe(accepted)}");
                return 0;
            case NoteWriteResult.Rejected rejected:
                Console.Error.WriteLine($"rejected [{rejected.Code}]: {rejected.Message}");
                Console.Error.WriteLine($"  example claim:    {rejected.Example.Claim}");
                Console.Error.WriteLine($"  example evidence: {rejected.Example.Evidence}");
                return 1;
            default:
                return 1;
        }
    }

    /// <summary>
    /// Three outcomes worth telling apart: linked to the index, anchored to a
    /// file outside it, or not anchored at all. Only the last cannot be
    /// re-checked when the code moves.
    /// </summary>
    private static string Describe(NoteWriteResult.Accepted accepted) =>
        accepted.IsGrounded
            ? $", anchored to {accepted.CitedChunkIds.Count} indexed location(s)"
            : accepted.IsVerifiable
                ? ", anchored to a file outside the index (vendored or skipped); verify can still check it"
                : " (evidence is a command or measurement; nothing anchors it to code)";

    private static int VerifyNotes(string[] args)
    {
        var options = CommandLine.Parse(args);
        using var db = MemoryDatabase.Open(options.Db);
        var results = EvidenceVerifier.Verify(db, options.Id, options.Root);

        if (results.Count == 0) { Console.WriteLine("no notes to verify"); return 0; }

        foreach (var result in results)
        {
            var marker = result.NeedsAttention ? "!" : " ";
            Console.WriteLine($" {marker} [note {result.NoteId}] {Label(result.Status),-13} {result.Detail}");
        }

        var attention = results.Count(r => r.NeedsAttention);
        Console.WriteLine();
        Console.WriteLine($"{results.Count} checked, {attention} need a look");
        return attention > 0 ? 1 : 0;
    }

    private static string Label(VerificationStatus status) => status switch
    {
        VerificationStatus.NotAnchored => "not anchored",
        _ => status.ToString().ToLowerInvariant(),
    };

    private static int Stats(string[] args)
    {
        var options = CommandLine.Parse(args);
        using var db = MemoryDatabase.Open(options.Db);
        var stats = MemorySearch.Stats(db);

        Console.WriteLine($"{options.Db}");
        Console.WriteLine($"  {stats.Files} files, {stats.Chunks} chunks, {stats.Notes} notes, {stats.Runs} runs");
        foreach (var repo in stats.PerRepo)
            Console.WriteLine($"  {repo.Repo,-26} {repo.Files,4} files  {repo.Chunks,5} chunks");
        return 0;
    }
}
