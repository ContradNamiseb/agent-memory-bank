using Microsoft.Data.Sqlite;
using AgentMemoryBank.Database;
using AgentMemoryBank.Indexing.Chunkers;

namespace AgentMemoryBank.Verification;

/// <summary>How a note's evidence stands up when the code has moved on.</summary>
public enum VerificationStatus
{
    /// <summary>The cited file is byte-identical to when the note was written.</summary>
    Unchanged,

    /// <summary>The file changed, but the cited line was found again and the note updated silently.</summary>
    Relocated,

    /// <summary>The cited line is gone; its enclosing symbol survives. Needs a human glance.</summary>
    Moved,

    /// <summary>Neither the line nor its symbol survives. The note may now be false.</summary>
    Broken,

    /// <summary>Evidence is a command or measurement, with no line to check.</summary>
    NotAnchored,
}

/// <summary>Outcome for one note.</summary>
public sealed record VerificationResult(
    long NoteId,
    VerificationStatus Status,
    string Detail,
    Citation? Citation = null,
    int? OldLine = null,
    int? NewLine = null)
{
    /// <summary>True when a person needs to look. The rest is noise.</summary>
    public bool NeedsAttention => Status is VerificationStatus.Moved or VerificationStatus.Broken;
}

/// <summary>
/// Re-checks that a note's cited code still says what the note claims.
///
/// <para>A note asserting something about code that has since changed is worse
/// than no note. But hashing the file alone is far too blunt: adding three lines
/// to the top of a file changes its hash while leaving the cited code untouched,
/// and if every commit produces a wave of false alarms, the alarms get ignored —
/// which is the same as not having them.</para>
///
/// <para>So resolution runs cheapest-first, and only the last step surfaces:</para>
/// <list type="number">
/// <item>Content hash unchanged — the note is fine, no further work.</item>
/// <item>Hash changed, but the anchor text is found exactly once — silently
/// update the stored line. This is the common case and should be invisible.</item>
/// <item>Anchor missing or ambiguous — fall back to the enclosing symbol.
/// Still there, flag <c>Moved</c>; gone too, flag <c>Broken</c>.</item>
/// </list>
/// </summary>
public static class EvidenceVerifier
{
    /// <summary>Verify one note, or every note that has not been superseded.</summary>
    public static IReadOnlyList<VerificationResult> Verify(SqliteConnection db, long? noteId = null,
        string? repoRoot = null)
    {
        repoRoot ??= Config.RepoRoot;

        using var select = db.Prepare($"""
            SELECT id, evidence, anchor_text, anchor_symbol
            FROM note
            WHERE superseded_by IS NULL {(noteId is null ? "" : "AND id = $id")}
            ORDER BY id
            """, noteId is null ? [] : ["$id"]);
        if (noteId is not null) select.SetAll(noteId.Value);

        var notes = select.Query(r => (
            Id: r.GetInt64("id"),
            Evidence: r.GetStringOrNull("evidence"),
            AnchorText: r.GetStringOrNull("anchor_text"),
            AnchorSymbol: r.GetStringOrNull("anchor_symbol")));

        var results = new List<VerificationResult>();
        foreach (var note in notes)
            results.Add(VerifyOne(db, note.Id, note.Evidence, note.AnchorText, note.AnchorSymbol, repoRoot));
        return results;
    }

    private static VerificationResult VerifyOne(SqliteConnection db, long noteId, string? evidence,
        string? anchorText, string? anchorSymbol, string repoRoot)
    {
        var citation = Citation.ParseAll(evidence).FirstOrDefault();
        if (citation is null)
            return new VerificationResult(noteId, VerificationStatus.NotAnchored,
                "Evidence cites no file line; it can only be checked by re-running it.");

        using var fileRow = db.Prepare(
            "SELECT id, content_sha FROM file WHERE repo = $repo AND path = $path", "$repo", "$path");
        var indexed = fileRow.SetAll(citation.Repo, citation.Path)
            .Query(r => (Id: r.GetInt64("id"), Sha: r.GetString("content_sha")))
            .FirstOrDefault();

        var absolute = Config.ResolveCitedFilePath(citation.Repo, citation.Path, repoRoot);
        if (!File.Exists(absolute))
            return new VerificationResult(noteId, VerificationStatus.Broken,
                $"The cited file no longer exists: {citation}", citation);

        var bytes = File.ReadAllBytes(absolute);
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

        // 1. Nothing changed.
        if (indexed.Id != 0 && indexed.Sha == sha)
        {
            Touch(db, noteId);
            return new VerificationResult(noteId, VerificationStatus.Unchanged,
                $"{citation} is unchanged since the note was written.", citation);
        }

        var lines = Chunker.SplitLines(System.Text.Encoding.UTF8.GetString(bytes));

        // 2. The anchor text is still there, exactly once: follow it.
        if (!string.IsNullOrWhiteSpace(anchorText))
        {
            var normalisedAnchor = AnchorSelector.Normalise(anchorText);
            var matches = new List<int>();
            for (var i = 0; i < lines.Count; i++)
                if (AnchorSelector.Normalise(lines[i]) == normalisedAnchor) matches.Add(i + 1);

            if (matches.Count == 1)
            {
                var newLine = matches[0];
                if (newLine != citation.Line) Relocate(db, noteId, citation, newLine);
                Touch(db, noteId);
                var detail = newLine != citation.Line
                    ? $"The cited line moved from {citation.Line} to {newLine}; the note was updated."
                    : indexed.Id == 0
                        // No stored hash to compare against, so step 1 was skipped.
                        ? $"{citation} is not in the index, but the cited line is still exactly where the note says."
                        : $"{citation} still holds; the file changed elsewhere.";
                return new VerificationResult(noteId, VerificationStatus.Relocated, detail,
                    citation, citation.Line, newLine);
            }

            if (matches.Count > 1)
                return new VerificationResult(noteId, VerificationStatus.Moved,
                    $"The anchor line now appears {matches.Count} times in {citation.Path}; " +
                    "it is too generic to follow automatically.", citation, citation.Line);
        }

        // 3. Fall back to the enclosing symbol.
        if (!string.IsNullOrWhiteSpace(anchorSymbol))
        {
            using var symbolRow = db.Prepare("""
                SELECT c.line_start FROM chunk c JOIN file f ON f.id = c.file_id
                WHERE f.repo = $repo AND f.path = $path AND c.symbol = $symbol
                LIMIT 1
                """, "$repo", "$path", "$symbol");
            var line = symbolRow.SetAll(citation.Repo, citation.Path, anchorSymbol)
                .Query(r => r.GetInt32("line_start")).FirstOrDefault();

            if (line != 0)
                return new VerificationResult(noteId, VerificationStatus.Moved,
                    $"The cited line is gone, but '{anchorSymbol}' still exists near line {line}. " +
                    "Check whether the note still holds.", citation, citation.Line, line);

            return new VerificationResult(noteId, VerificationStatus.Broken,
                $"Neither the cited line nor '{anchorSymbol}' survives in {citation.Path}. " +
                "The note may now be false.", citation, citation.Line);
        }

        return new VerificationResult(noteId, VerificationStatus.Broken,
            $"{citation.Path} changed and the note has no anchor to re-locate it by.", citation, citation.Line);
    }

    private static void Touch(SqliteConnection db, long noteId)
    {
        using var cmd = db.Prepare("UPDATE note SET verified_at = $at WHERE id = $id", "$at", "$id");
        cmd.SetAll(DateTime.UtcNow.ToString("o"), noteId).ExecuteNonQuery();
    }

    /// <summary>Rewrite the line number inside the evidence text, leaving the rest intact.</summary>
    private static void Relocate(SqliteConnection db, long noteId, Citation citation, int newLine)
    {
        using var read = db.Prepare("SELECT evidence FROM note WHERE id = $id", "$id");
        var evidence = read.SetAll(noteId).Query(r => r.GetStringOrNull("evidence")).FirstOrDefault();
        if (evidence is null) return;

        var updated = evidence
            .Replace($"{citation.Repo}/{citation.Path}:{citation.Line}", $"{citation.Repo}/{citation.Path}:{newLine}")
            .Replace($"{citation.Repo}:{citation.Path}:{citation.Line}", $"{citation.Repo}:{citation.Path}:{newLine}");

        using var write = db.Prepare("UPDATE note SET evidence = $evidence WHERE id = $id", "$evidence", "$id");
        write.SetAll(updated, noteId).ExecuteNonQuery();
    }
}
