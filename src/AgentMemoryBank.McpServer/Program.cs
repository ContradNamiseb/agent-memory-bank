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
                "mine-git" => MineGit(args[1..]),
                "search" => Search(args[1..]),
                "get" => Get(args[1..]),
                "handoff" => Handoff(args[1..]),
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
              index  [--repo NAME]... [--full] [--with-git] [--note TEXT] [--db PATH] [--root DIR] [-v]
                     Phase A: index tracked, non-vendored source files. Incremental by
                     content hash; --full re-chunks everything. --with-git also runs Phase B.

              mine-git [--repo NAME]... [--limit N] [--since DATE] [--note TEXT] [--db PATH] [-v]
                     Phase B: mine substantive commit messages for historical rationale,
                     architectural decisions, and hazard fixes.

              search QUERY [--repo NAME] [--kind K] [--limit N] [--full] [--db PATH]
                     BM25 over notes and chunks. --full prints whole chunk bodies with
                     line numbers instead of a snippet.

              get    <note|chunk> ID [--db PATH]
                     Inspect one note or chunk in full with lines and citations.

              handoff [latest|save|list] [--agent A] [--topic T] [--summary S] [--next N] [--files F] [--db PATH]
                     Cross-agent session continuity: save or view handoff snapshots to migrate between
                     Antigravity, Claude Code, and Copilot without losing context.

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

        if (options.WithGit)
        {
            Console.WriteLine("\nMining git history (Phase B)...");
            var gitRun = GitCommitMiner.MineAll(db,
                repoRoot: options.Root,
                repos: repos,
                options: GitMineOptions.Default,
                note: options.Note);

            foreach (var r in gitRun.Results)
            {
                if (r.Error is not null)
                {
                    Console.Error.WriteLine($"  {r.Repo,-26} ERROR {r.Error}");
                    continue;
                }
                Console.WriteLine($"  {r.Repo,-26} examined {r.CommitsExamined,4}  notes {r.NotesCreated,3}  skipped {r.NotesSkipped,4}");
            }
        }

        var stats = MemorySearch.Stats(db);
        Console.WriteLine($"\nrun {run.RunId} in {run.Elapsed.TotalSeconds:F1}s -> {options.Db}");
        Console.WriteLine($"total: {stats.Files} files, {stats.Chunks} chunks, {stats.Notes} notes");
        if (options.Verbose)
            foreach (var repo in stats.PerRepo)
                Console.WriteLine($"  {repo.Repo,-26} {repo.Files} files, {repo.Chunks} chunks");

        return failed ? 1 : 0;
    }

    private static int MineGit(string[] args)
    {
        var options = CommandLine.Parse(args);
        var repos = options.Repos.Count > 0 ? options.Repos : Config.Repos;
        if (repos.Count == 0)
        {
            Console.Error.WriteLine(Config.ConfigurationHelp);
            return 2;
        }

        using var db = MemoryDatabase.Open(options.Db);
        var gitOptions = new GitMineOptions
        {
            MaxCommitsPerRepo = options.Limit > 0 && options.Limit != 10 ? options.Limit : null,
            Since = options.Since,
        };

        var run = GitCommitMiner.MineAll(
            db,
            options.Root,
            repos,
            gitOptions,
            options.Note,
            onMined: (repo, commit) =>
            {
                if (options.Verbose)
                    Console.Error.WriteLine($"  [{repo}] {commit.ShortSha}: {commit.Subject}");
            });

        foreach (var r in run.Results)
        {
            if (r.Error is not null)
            {
                Console.Error.WriteLine($"  {r.Repo,-26} ERROR {r.Error}");
                continue;
            }

            Console.WriteLine($"  {r.Repo,-26} examined {r.CommitsExamined,4}  created {r.NotesCreated,4} notes  (skipped {r.NotesSkipped,4})");
        }

        var stats = MemorySearch.Stats(db);
        Console.WriteLine($"\nmined git history in {run.Elapsed.TotalSeconds:F1}s (run #{run.RunId})");
        Console.WriteLine($"total: {stats.Files} files, {stats.Chunks} chunks, {stats.Notes} notes");
        return 0;
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

    private static int Get(string[] args)
    {
        if (args.Length < 2 || !long.TryParse(args[1], out var id))
            return Fail("usage: get <note|chunk> <id>");

        var kind = args[0].ToLowerInvariant();
        var options = CommandLine.Parse(args[2..]);
        using var db = MemoryDatabase.Open(options.Db);

        if (kind is "note")
        {
            var note = MemorySearch.GetNote(db, id);
            if (note is null) { Console.WriteLine($"note {id} not found"); return 1; }

            Console.WriteLine($"[note {note.Id}] {note.Kind?.ToString().ToLowerInvariant()} / {note.Confidence?.ToString().ToLowerInvariant()}");
            Console.WriteLine($"Claim: {note.Claim}");
            if (!string.IsNullOrWhiteSpace(note.Rationale)) Console.WriteLine($"\nRationale:\n{note.Rationale}");
            if (!string.IsNullOrWhiteSpace(note.Evidence)) Console.WriteLine($"\nEvidence: {note.Evidence}");
            if (note.VerifiedAt is not null) Console.WriteLine($"Verified: {note.VerifiedAt}");
            return 0;
        }
        else if (kind is "chunk")
        {
            var chunkWithFile = MemorySearch.GetChunk(db, id);
            if (chunkWithFile is null) { Console.WriteLine($"chunk {id} not found"); return 1; }

            var chunk = chunkWithFile.Value.Chunk;
            Console.WriteLine($"[chunk {id}] {chunkWithFile.Value.Repo}/{chunkWithFile.Value.Path}:{chunk.LineStart}-{chunk.LineEnd} ({chunk.Kind} {chunk.Symbol})");
            Console.WriteLine(new string('-', 70));
            var lines = chunk.Body.Split('\n');
            for (var i = 0; i < lines.Length; i++)
                Console.WriteLine($"{chunk.LineStart + i,5}| {lines[i]}");
            return 0;
        }

        return Fail($"unknown kind '{kind}' (expected 'note' or 'chunk')");
    }

    private static int Handoff(string[] args)
    {
        var verb = args.FirstOrDefault() ?? "latest";
        if (verb.StartsWith('-')) verb = "latest";
        var subArgs = (args.Length > 0 && !args[0].StartsWith('-')) ? args[1..] : args;
        var options = CommandLine.Parse(subArgs);

        using var db = MemoryDatabase.Open(options.Db);

        if (verb is "latest")
        {
            var latest = HandoffStore.GetLatest(db, options.Agent);
            if (latest is null)
            {
                Console.WriteLine("No session handoffs recorded yet.");
                return 0;
            }

            Console.WriteLine($"=== Session Handoff #{latest.Id} ({latest.Agent}) ===");
            Console.WriteLine($"Topic:   {latest.Topic}");
            Console.WriteLine($"Created: {latest.CreatedAt}");
            if (!string.IsNullOrWhiteSpace(latest.ActiveFiles))
                Console.WriteLine($"Files:   {latest.ActiveFiles}");
            Console.WriteLine($"\n--- Summary ---\n{latest.Summary}");
            Console.WriteLine($"\n--- Next Steps ---\n{latest.NextSteps}");
            if (!string.IsNullOrWhiteSpace(latest.OpenQuestions))
                Console.WriteLine($"\n--- Open Questions ---\n{latest.OpenQuestions}");
            return 0;
        }
        else if (verb is "save")
        {
            if (string.IsNullOrWhiteSpace(options.Agent) || string.IsNullOrWhiteSpace(options.Topic) ||
                string.IsNullOrWhiteSpace(options.Summary) || string.IsNullOrWhiteSpace(options.NextSteps))
            {
                return Fail("handoff save requires --agent, --topic, --summary, and --next");
            }

            var draft = new SessionHandoffDraft
            {
                Agent = options.Agent,
                Topic = options.Topic,
                Summary = options.Summary,
                NextSteps = options.NextSteps,
                ActiveFiles = options.ActiveFiles,
                OpenQuestions = options.OpenQuestions
            };

            var saved = HandoffStore.Save(db, draft);
            Console.WriteLine($"Session handoff #{saved.Id} saved ({saved.Agent} -> '{saved.Topic}').");
            return 0;
        }
        else if (verb is "list")
        {
            var list = HandoffStore.List(db, options.Limit, options.Agent);
            if (list.Count == 0)
            {
                Console.WriteLine("No session handoffs found.");
                return 0;
            }

            foreach (var h in list)
            {
                Console.WriteLine($"[handoff {h.Id}] {h.CreatedAt} - {h.Agent}: {h.Topic}");
            }
            return 0;
        }

        return Fail($"unknown handoff subcommand '{verb}' (expected 'latest', 'save', or 'list')");
    }

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
