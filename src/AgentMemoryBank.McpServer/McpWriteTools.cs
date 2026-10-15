using System.ComponentModel;
using System.Text;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Server;
using AgentMemoryBank;
using AgentMemoryBank.Database;
using AgentMemoryBank.Models;
using AgentMemoryBank.Verification;

namespace AgentMemoryBank.McpServer;

/// <summary>
/// The write surface: recording durable claims, correcting them, and re-checking
/// that the code they cite still says what they say.
///
/// <para>Nothing here deletes or overwrites. A claim that turned out wrong is
/// superseded, so the record of having believed it survives — often the more
/// useful fact, because it stops the same wrong conclusion being reached
/// twice. That holds for the batch upsert too: its "update" is a supersede.</para>
/// </summary>
[McpServerToolType]
public sealed class McpWriteTools
{
    private McpWriteTools() { }

    private static SqliteConnection Open() => MemoryDatabase.Open(Config.DbPath);

    [McpServerTool(Name = "memory_notes_upsert")]
    [Description("""
        The one write tool for notes (replaces memory_note_add and
        memory_note_supersede). Record a durable finding the code does not
        state: a decision and its alternative, a hazard, a measured constant, a
        procedure. Evidence is mandatory: a repo/path:line citation (re-
        checkable by memory_verify), a commit SHA, or a re-runnable command or
        measurement. Parallel arrays, one entry per note: claims[i] with
        evidence[i]; kinds, confidences, rationales and tags may hold one value
        for all. A claim already live (same text, case-insensitive) is
        UNCHANGED when evidence, kind and confidence match (tags merged),
        otherwise the old note is superseded, never overwritten. To correct a
        reworded claim, set supersedes[i] to the old id and say why in
        rationales[i]. One refusal does not stop the others. Returns one line
        per note: 'i: added|superseded|unchanged|rejected …'. dry_run writes
        nothing.
        """)]
    public static string Upsert(
        [Description("One falsifiable sentence per note.")]
        string[] claims,
        [Description("Evidence per note, same order as claims.")]
        string[] evidence,
        [Description("Kind per note, or one for all. Default finding.")]
        NoteKind[]? kinds = null,
        [Description("Confidence per note, or one for all. Default inferred.")]
        Confidence[]? confidences = null,
        [Description("Rationale per note, or one for all.")]
        string[]? rationales = null,
        [Description("Comma-separated tags per note, or one string for all.")]
        string[]? tags = null,
        [Description("Note id each claim replaces; 0 = match by claim text.")]
        long[]? supersedes = null,
        [Description("Validate only; write nothing.")]
        bool dry_run = false)
    {
        var count = claims?.Length ?? 0;
        if (count == 0) return "Error: claims is empty.";
        var problems = new List<string>();
        void Check(string name, int length)
        {
            if (length is not 0 and not 1 && length != count)
                problems.Add($"{name} has {length} values; give 1 (applies to all) or {count} (one per claim)");
        }
        if (evidence is null || evidence.Length != count)
            problems.Add($"evidence has {evidence?.Length ?? 0} values; it needs exactly {count}, one per claim");
        Check("kinds", kinds?.Length ?? 0);
        Check("confidences", confidences?.Length ?? 0);
        Check("rationales", rationales?.Length ?? 0);
        Check("tags", tags?.Length ?? 0);
        // Never broadcast: one id retires one note, so it cannot apply to every claim.
        if (supersedes is { Length: > 0 } && supersedes.Length != count)
            problems.Add($"supersedes has {supersedes.Length} values; it needs exactly {count} (0 = match by claim)");
        if (problems.Count > 0) return $"Error: memory_notes_upsert: {string.Join("; ", problems)}. Nothing was written.";

        static T? At<T>(T[]? values, int i) =>
            values is null || values.Length == 0 ? default : values[values.Length == 1 ? 0 : i];

        using var db = Open();
        var lines = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var draft = new NoteDraft
            {
                Claim = claims![i] ?? "",
                Evidence = evidence![i] ?? "",
                Rationale = At(rationales, i),
                Kind = kinds is { Length: > 0 } ? At(kinds, i) : NoteKind.Finding,
                Confidence = confidences is { Length: > 0 } ? At(confidences, i) : Confidence.Inferred,
                Tags = SplitTags(At(tags, i)),
            };
            var replace = supersedes is { Length: > 0 } ? supersedes[i] : 0;

            var outcome = NoteStore.Upsert(db, draft, replace, dry_run);
            lines.Add($"{i}: " + outcome.Action switch
            {
                UpsertAction.Added => dry_run ? "would add" : $"added note {outcome.Id}",
                UpsertAction.Unchanged => $"unchanged note {outcome.Id} (same claim and evidence; tags merged)",
                UpsertAction.Superseded => dry_run
                    ? $"would supersede note {outcome.Replaced}"
                    : $"superseded note {outcome.Replaced} with note {outcome.Id}",
                _ => outcome.Write is NoteWriteResult.Rejected r
                    ? $"rejected ({r.Code}): {r.Message}"
                    : "rejected",
            } + Grounding(outcome.Write) + Warnings(outcome.Write));
        }

        var head = dry_run ? "Dry run - nothing was written.\n" : "";
        var rejected = lines.Count(l => l.Contains(": rejected", StringComparison.Ordinal));
        var tail = rejected > 0
            ? $"\n{rejected} rejected; fix those items and resend only them. Example of an accepted note: " +
              $"claim=\"{NoteExample.Default.Claim}\" evidence=\"{NoteExample.Default.Evidence}\""
            : "";
        return head + string.Join('\n', lines) + tail;
    }

    [McpServerTool(Name = "memory_verify")]
    [Description("""
        Re-check that the code a note cites still says what it says; run after
        pulling changes or before relying on an old note. Writes only
        re-anchoring of notes whose cited line merely moved. Returns counts and
        one line per note needing a human: MOVED (line gone, function survives)
        or BROKEN (both gone; the note may now be false).
        """)]
    public static string Verify(
        [Description("One note id. Omit to check every current note.")]
        long? id = null)
    {
        using var db = Open();
        var results = EvidenceVerifier.Verify(db, id);
        if (results.Count == 0)
            return id is null
                ? "There are no notes to verify."
                : $"Error: no note with id {id}; {NoteStore.IdHint(db, id.Value)}.";

        var text = new StringBuilder();
        var attention = results.Where(r => r.NeedsAttention).ToList();
        var relocated = results.Count(r => r.Status == VerificationStatus.Relocated && r.NewLine != r.OldLine);

        text.AppendLine($"Checked {results.Count} note(s): {results.Count - attention.Count} fine, " +
                        $"{attention.Count} need a look.");
        if (relocated > 0)
            text.AppendLine($"{relocated} had moved and were re-anchored automatically.");

        if (attention.Count > 0)
        {
            text.AppendLine();
            foreach (var result in attention)
                text.AppendLine($"- [note {result.NoteId}] {result.Status.ToString().ToUpperInvariant()}: {result.Detail}");
            text.AppendLine();
            text.AppendLine("Read each with memory_get, then correct it with memory_notes_upsert (supersedes=[id]) " +
                            "if the finding no longer holds.");
        }
        return text.ToString().TrimEnd();
    }

    private static IReadOnlyList<string> SplitTags(string? tags) =>
        tags?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

    /// <summary>Whether the citation resolved into the index; the unresolvable case is already a warning.</summary>
    private static string Grounding(NoteWriteResult? write) => write switch
    {
        NoteWriteResult.Accepted { IsGrounded: true } a => $" (linked to {a.CitedChunkIds.Count} indexed location(s))",
        NoteWriteResult.Accepted { IsVerifiable: true } => " (anchored; cited file not indexed)",
        _ => "",
    };

    private static string Warnings(NoteWriteResult? write) =>
        write is NoteWriteResult.Accepted { Warnings.Count: > 0 } a
            ? string.Concat(a.Warnings.Select(w => $"\n   ! {w}"))
            : "";

}
