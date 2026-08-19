using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
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
/// <para>Nothing here deletes. A claim that turned out wrong is superseded, so
/// the record of having believed it survives — often the more useful fact,
/// because it stops the same wrong conclusion being reached twice.</para>
/// </summary>
[McpServerToolType]
public sealed class McpWriteTools
{
    private McpWriteTools() { }

    private static SqliteConnection Open() => MemoryDatabase.Open(Config.DbPath);

    [McpServerTool(Name = "memory_note_add")]
    [Description("""
        Record a durable finding so the next session does not have to re-derive it: a decision and
        what it was made instead of, a hazard, a measured constant, or a procedure worth keeping.

        Write a note when you establish something that is NOT recoverable by reading the code —
        why a value is what it is, what a default silently does, what an earlier attempt got wrong.
        Do not write notes restating what the code plainly says.

        Evidence is mandatory and is enforced: give a 'repo/path:line' citation, or a command or
        measurement someone could re-run. A claim with nothing behind it is indistinguishable from
        a guess once the session ends, so it is refused. Refusals come back with a reason code and
        a worked example to copy — read it rather than retrying the same call.
        """)]
    public static string Add(
        [Description("One sentence, falsifiable. State what is true, not what you wondered.")]
        string claim,
        [Description("repo/path:line citations, and/or a command or measurement that establishes it.")]
        string evidence,
        [Description("Why it is true and how it was established. Optional but strongly wanted.")]
        string? rationale = null,
        [Description("decision, hazard, howto, or finding. Defaults to finding.")]
        string kind = "finding",
        [Description("measured, inferred, or assumed. Be honest: this is how a reader weighs it.")]
        string confidence = "inferred",
        [Description("Comma-separated tags for grouping, e.g. 'calibration,loader'.")]
        string? tags = null)
    {
        if (!Enum.TryParse<NoteKind>(kind, ignoreCase: true, out var noteKind))
            return Envelope("rejected", "invalid_kind",
                $"kind must be one of: {Names<NoteKind>()}", NoteExample.Default);
        if (!Enum.TryParse<Confidence>(confidence, ignoreCase: true, out var noteConfidence))
            return Envelope("rejected", "invalid_confidence",
                $"confidence must be one of: {Names<Confidence>()}", NoteExample.Default);

        using var db = Open();
        var draft = new NoteDraft
        {
            Claim = claim ?? "",
            Evidence = evidence ?? "",
            Rationale = rationale,
            Kind = noteKind,
            Confidence = noteConfidence,
            Tags = tags?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [],
        };

        return NoteStore.Add(db, draft) switch
        {
            NoteWriteResult.Accepted accepted => Accepted(accepted, db),
            NoteWriteResult.Rejected rejected => Envelope("rejected", rejected.Code, rejected.Message, rejected.Example),
            _ => "Error: unexpected result.",
        };
    }

    [McpServerTool(Name = "memory_note_supersede")]
    [Description("""
        Replace a note that turned out to be wrong or incomplete with a corrected one. The original
        is kept and marked superseded, never deleted, so the record of having been wrong survives.

        Use this instead of adding a second note that contradicts the first — two live notes
        disagreeing is how a store stops being trusted. The replacement needs its own evidence,
        held to the same standard as any other write.
        """)]
    public static string Supersede(
        [Description("Id of the note being replaced.")] long id,
        [Description("The corrected claim.")] string claim,
        [Description("Evidence for the corrected claim.")] string evidence,
        [Description("Why the earlier note was wrong. This is the part worth keeping.")]
        string? why = null,
        [Description("decision, hazard, howto, or finding.")] string kind = "finding",
        [Description("measured, inferred, or assumed.")] string confidence = "inferred")
    {
        if (!Enum.TryParse<NoteKind>(kind, ignoreCase: true, out var noteKind))
            return Envelope("rejected", "invalid_kind", $"kind must be one of: {Names<NoteKind>()}", NoteExample.Default);
        if (!Enum.TryParse<Confidence>(confidence, ignoreCase: true, out var noteConfidence))
            return Envelope("rejected", "invalid_confidence", $"confidence must be one of: {Names<Confidence>()}", NoteExample.Default);

        using var db = Open();
        var replacement = new NoteDraft
        {
            Claim = claim ?? "",
            Evidence = evidence ?? "",
            Kind = noteKind,
            Confidence = noteConfidence,
        };

        return NoteStore.Supersede(db, id, replacement, why) switch
        {
            NoteWriteResult.Accepted accepted =>
                $"Recorded note {accepted.Id}, superseding note {id}. The original is kept and " +
                "will no longer appear in searches unless asked for.",
            NoteWriteResult.Rejected rejected => Envelope("rejected", rejected.Code, rejected.Message, rejected.Example),
            _ => "Error: unexpected result.",
        };
    }

    [McpServerTool(Name = "memory_verify")]
    [Description("""
        Re-check that the code a note cites still says what the note says. Run this after pulling
        changes, or before relying on an old finding.

        Notes whose cited line merely moved are re-anchored silently and reported as fine. Only two
        outcomes need a human: 'moved', where the line is gone but its enclosing function survives,
        and 'broken', where neither does and the note may now be false.
        """)]
    public static string Verify(
        [Description("Verify one note by id. Omit to check every note that is still current.")]
        long? id = null)
    {
        using var db = Open();
        var results = EvidenceVerifier.Verify(db, id);
        if (results.Count == 0) return id is null ? "There are no notes to verify." : $"No note with id {id}.";

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
            text.AppendLine("Read each with memory_get, then correct it with memory_note_supersede " +
                            "if the finding no longer holds.");
        }
        return text.ToString().TrimEnd();
    }

    private static string Accepted(NoteWriteResult.Accepted accepted, SqliteConnection db)
    {
        var text = new StringBuilder($"Recorded note {accepted.Id}.");
        if (accepted.IsGrounded)
            text.Append($" Linked to {accepted.CitedChunkIds.Count} indexed location(s); the citation " +
                        "resolves and memory_verify can re-check it.");
        else if (accepted.IsVerifiable)
            text.Append(" The cited file exists but is not indexed (a vendored tree, or a skipped path), " +
                        "so there is no link into the index — but the line was anchored and memory_verify " +
                        "can still re-check it.");
        else
            text.Append(" The evidence is a command or measurement rather than a file citation, so nothing " +
                        "anchors it to code and memory_verify cannot re-check it automatically.");
        return text.ToString();
    }

    /// <summary>
    /// A machine-readable refusal. An opaque rejection is how an autonomous caller
    /// gets stuck retrying the same malformed write; the worked example gives it
    /// something to copy rather than a constraint to guess at.
    /// </summary>
    private static string Envelope(string status, string reason, string message, NoteExample example) =>
        JsonSerializer.Serialize(new
        {
            status,
            reason,
            message,
            example = new { claim = example.Claim, evidence = example.Evidence, confidence = example.Confidence },
        }, RejectionJson);

    /// <summary>
    /// Relaxed escaping: the default encoder turns an apostrophe into ',
    /// and this envelope is read by a model that has to act on the message.
    /// The output is plain text, never HTML, so the strict escaping buys nothing.
    /// </summary>
    private static readonly JsonSerializerOptions RejectionJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string Names<T>() where T : struct, Enum =>
        string.Join(", ", Enum.GetNames<T>().Select(n => n.ToLowerInvariant()));
}
