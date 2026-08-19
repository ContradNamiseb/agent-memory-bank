using Microsoft.Data.Sqlite;
using AgentMemoryBank.Models;
using AgentMemoryBank.Verification;
using AgentMemoryBank.Indexing.Chunkers;

namespace AgentMemoryBank.Database;

/// <summary>What a caller proposes to record.</summary>
public sealed record NoteDraft
{
    /// <summary>One sentence, falsifiable.</summary>
    public required string Claim { get; init; }

    /// <summary>Why, and how it was established.</summary>
    public string? Rationale { get; init; }

    /// <summary>repo/path:line references, commands, measurements. Required.</summary>
    public required string Evidence { get; init; }

    public NoteKind Kind { get; init; } = NoteKind.Finding;
    public Confidence Confidence { get; init; } = Confidence.Inferred;
    public IReadOnlyList<string> Tags { get; init; } = [];
    public long? RunId { get; init; }
}

/// <summary>
/// The write path. Notes are the product; this is the gate that keeps them worth
/// having.
///
/// <para>Evidence is mandatory, and that single constraint is what stops the
/// store filling with plausible-sounding sludge. A claim with nothing behind it
/// is indistinguishable from a guess once the session that made it has ended.</para>
///
/// <para>Nothing here deletes. Being wrong is superseded rather than erased, so
/// the record of having been wrong survives — which is often the more useful
/// fact.</para>
/// </summary>
public static class NoteStore
{
    /// <summary>Words that promise a claim nothing can falsify.</summary>
    private static readonly string[] Hedges =
        ["might", "may be", "possibly", "perhaps", "probably", "seems", "appears to", "could be"];

    /// <summary>
    /// Record a note, or refuse it with a reason and a worked example.
    /// </summary>
    public static NoteWriteResult Add(SqliteConnection db, NoteDraft draft, string? repoRoot = null)
    {
        var claim = draft.Claim?.Trim() ?? "";
        var evidence = draft.Evidence?.Trim() ?? "";

        if (!IsFalsifiable(claim, out var why))
            return Reject(RejectionReason.ClaimNotFalsifiable, why);

        var citations = Citation.ParseAll(evidence);
        if (citations.Count == 0 && !Citation.LooksReproducible(evidence))
            return Reject(RejectionReason.MissingEvidence,
                "evidence must cite a valid 'repo/path:line' or give a reproducible command or measurement");

        if (FindDuplicate(db, claim) is { } duplicateId)
            return Reject(RejectionReason.DuplicateClaim,
                $"note {duplicateId} already claims this; supersede it instead of adding a second copy");

        // Anchor to the citations that point at something real.
        //
        // Being indexed is not the test. Vendored trees are excluded from the
        // index on purpose, and yet they are where some of the most valuable
        // hazards live — a third-party default that destroys data is exactly the
        // thing worth recording. Refusing those notes would turn the evidence
        // rule from a quality gate into an arbitrary one, so a citation counts
        // when the file exists on disk, whether or not it was indexed. Such a
        // note simply gets no chunk edge and no enclosing symbol.
        var root = repoRoot ?? Config.RepoRoot;
        string? anchorText = null, anchorSymbol = null;
        var citedChunks = new List<long>();
        var anyResolved = false;

        foreach (var citation in citations)
        {
            var indexed = Resolve(db, citation, root);
            if (indexed is not null)
            {
                citedChunks.Add(indexed.Value.ChunkId);
                anchorText ??= indexed.Value.AnchorText;
                anchorSymbol ??= indexed.Value.Symbol;
                anyResolved = true;
                continue;
            }

            var fromDisk = AnchorFromDisk(citation, root);
            if (fromDisk is null) continue;
            anchorText ??= fromDisk;
            anyResolved = true;
        }

        if (citations.Count > 0 && !anyResolved)
            return Reject(RejectionReason.UnresolvableAnchor,
                $"'{citations[0]}' points at no file that exists: check the repository name, the " +
                "path, and the line number");

        using var transaction = db.BeginTransaction();
        using var insert = db.Prepare("""
            INSERT INTO note(claim, rationale, evidence, anchor_symbol, anchor_text,
                             kind, confidence, created_at, verified_at, run_id)
            VALUES ($claim, $why, $evidence, $symbol, $anchor, $kind, $confidence, $at, $at, $run)
            RETURNING id
            """, "$claim", "$why", "$evidence", "$symbol", "$anchor", "$kind", "$confidence", "$at", "$run");
        insert.Transaction = transaction;

        var now = DateTime.UtcNow.ToString("o");
        var id = (long)insert.SetAll(
            claim,
            string.IsNullOrWhiteSpace(draft.Rationale) ? null : draft.Rationale.Trim(),
            evidence,
            anchorSymbol,
            anchorText,
            draft.Kind.ToString().ToLowerInvariant(),
            draft.Confidence.ToString().ToLowerInvariant(),
            now,
            draft.RunId).ExecuteScalar()!;

        AddTags(db, transaction, id, draft.Tags);
        foreach (var chunkId in citedChunks.Distinct())
            AddEdge(db, transaction, "note", id, "chunk", chunkId, "cites");

        transaction.Commit();
        return new NoteWriteResult.Accepted(id, citedChunks, anchorText is not null);
    }

    /// <summary>
    /// Replace a note with a corrected one, keeping the original as a record.
    ///
    /// <para>The superseded note stays searchable only when explicitly asked for:
    /// knowing that something was once believed, and why it stopped being true, is
    /// worth more than a clean table.</para>
    /// </summary>
    public static NoteWriteResult Supersede(SqliteConnection db, long id, NoteDraft replacement,
        string? why = null, string? repoRoot = null)
    {
        using var exists = db.Prepare("SELECT claim, superseded_by FROM note WHERE id = $id", "$id");
        var original = exists.SetAll(id)
            .Query(r => (Claim: r.GetString("claim"),
                         Superseded: r.IsDBNull(r.GetOrdinal("superseded_by")) ? (long?)null : r.GetInt64("superseded_by")))
            .FirstOrDefault();

        if (original.Claim is null)
            return Reject(RejectionReason.UnresolvableAnchor, $"there is no note with id {id}");
        if (original.Superseded is not null)
            return Reject(RejectionReason.DuplicateClaim,
                $"note {id} was already superseded by note {original.Superseded}; supersede that one instead");

        var rationale = string.IsNullOrWhiteSpace(why)
            ? replacement.Rationale
            : $"Supersedes note {id} ({original.Claim}). {why}".Trim();

        var written = Add(db, replacement with { Rationale = rationale }, repoRoot);
        if (written is not NoteWriteResult.Accepted accepted) return written;

        using var mark = db.Prepare("UPDATE note SET superseded_by = $new WHERE id = $old", "$new", "$old");
        mark.SetAll(accepted.Id, id).ExecuteNonQuery();
        AddEdge(db, null, "note", accepted.Id, "note", id, "supersedes");
        return accepted;
    }

    /// <summary>Notes whose evidence points into a given file, for a staleness sweep.</summary>
    public static IReadOnlyList<long> NotesCiting(SqliteConnection db, string repo, string path)
    {
        using var cmd = db.Prepare("""
            SELECT id FROM note
            WHERE superseded_by IS NULL AND evidence LIKE $needle
            ORDER BY id
            """, "$needle");
        return cmd.SetAll($"%{repo}%{path}%").Query(r => r.GetInt64("id"));
    }

    /// <summary>
    /// A claim has to be able to turn out false. This check is deliberately weak —
    /// it catches emptiness and pure hedging, not bad judgement — because a gate
    /// that rejects good notes teaches authors to route around the store.
    /// </summary>
    private static bool IsFalsifiable(string claim, out string why)
    {
        why = "";
        if (claim.Length < 12)
        {
            why = "the claim is too short to state anything checkable";
            return false;
        }
        if (claim.EndsWith('?'))
        {
            why = "a question is not a claim; record what you established, not what you asked";
            return false;
        }
        if (Hedges.Any(h => claim.Contains(h, StringComparison.OrdinalIgnoreCase)))
        {
            why = "the claim hedges ('might', 'seems', 'probably'), so nothing could contradict it; " +
                  "state what was observed, and put the uncertainty in confidence instead";
            return false;
        }
        return true;
    }

    private static long? FindDuplicate(SqliteConnection db, string claim)
    {
        using var cmd = db.Prepare(
            "SELECT id FROM note WHERE superseded_by IS NULL AND lower(trim(claim)) = lower(trim($claim)) LIMIT 1",
            "$claim");
        var id = cmd.SetAll(claim).Query(r => r.GetInt64("id")).FirstOrDefault();
        return id == 0 ? null : id;
    }

    /// <summary>Locate the chunk containing a cited line, and pick an anchor within it.</summary>
    private static (long ChunkId, string? Symbol, string? AnchorText)? Resolve(
        SqliteConnection db, Citation citation, string repoRoot)
    {
        using var cmd = db.Prepare("""
            SELECT c.id, c.symbol FROM chunk c JOIN file f ON f.id = c.file_id
            WHERE f.repo = $repo AND f.path = $path
              AND c.line_start <= $line AND c.line_end >= $line
            LIMIT 1
            """, "$repo", "$path", "$line");

        var chunk = cmd.SetAll(citation.Repo, citation.Path, citation.Line)
            .Query(r => (Id: r.GetInt64("id"), Symbol: r.GetStringOrNull("symbol")))
            .FirstOrDefault();
        if (chunk.Id == 0) return null;

        string? anchor = null;
        var absolute = Path.Combine(Config.GetRepoDirectory(citation.Repo, repoRoot), citation.Path);
        if (File.Exists(absolute))
        {
            var lines = Chunker.SplitLines(File.ReadAllText(absolute));
            if (citation.Line <= lines.Count)
                anchor = EvidenceVerifier.ChooseAnchorText(lines, citation.Line);
        }
        return (chunk.Id, chunk.Symbol, anchor);
    }

    /// <summary>
    /// Anchor text for a citation into a file that exists but was not indexed —
    /// a vendored tree, or a path the indexer skips. Returns null when the file
    /// or the line is not there, which is what makes the citation unresolvable.
    /// </summary>
    private static string? AnchorFromDisk(Citation citation, string repoRoot)
    {
        var absolute = Path.Combine(Config.GetRepoDirectory(citation.Repo, repoRoot), citation.Path);
        if (!File.Exists(absolute)) return null;

        var lines = Chunker.SplitLines(File.ReadAllText(absolute));
        return citation.Line > lines.Count
            ? null
            : EvidenceVerifier.ChooseAnchorText(lines, citation.Line);
    }

    private static void AddTags(SqliteConnection db, SqliteTransaction? transaction, long noteId,
        IReadOnlyList<string> tags)
    {
        if (tags.Count == 0) return;
        using var cmd = db.Prepare(
            "INSERT OR IGNORE INTO tag(note_id, tag) VALUES ($note, $tag)", "$note", "$tag");
        cmd.Transaction = transaction;
        foreach (var tag in tags.Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).Distinct())
            cmd.SetAll(noteId, tag).ExecuteNonQuery();
    }

    private static void AddEdge(SqliteConnection db, SqliteTransaction? transaction,
        string srcKind, long srcId, string dstKind, long dstId, string relation)
    {
        using var cmd = db.Prepare("""
            INSERT INTO edge(src_kind, src_id, dst_kind, dst_id, rel)
            VALUES ($sk, $si, $dk, $di, $rel)
            """, "$sk", "$si", "$dk", "$di", "$rel");
        cmd.Transaction = transaction;
        cmd.SetAll(srcKind, srcId, dstKind, dstId, relation).ExecuteNonQuery();
    }

    private static NoteWriteResult.Rejected Reject(RejectionReason reason, string message) =>
        new(reason, message, NoteExample.Default);
}
