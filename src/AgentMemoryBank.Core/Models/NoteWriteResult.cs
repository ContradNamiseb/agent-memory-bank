namespace AgentMemoryBank.Models;

/// <summary>
/// Why a write was refused. A closed set, so a caller can branch on it rather
/// than parse prose.
/// </summary>
public enum RejectionReason
{
    /// <summary>No repo:path:line and nothing that could be re-run.</summary>
    MissingEvidence,

    /// <summary>The citation is well-formed but points at nothing in the index.</summary>
    UnresolvableAnchor,

    /// <summary>An existing note already claims this.</summary>
    DuplicateClaim,

    /// <summary>The claim states nothing that could turn out to be false.</summary>
    ClaimNotFalsifiable,
}

/// <summary>
/// The outcome of a write. Rejections carry a reason code, an explanation, and a
/// worked example.
///
/// <para>The example is the part that matters. An opaque rejection is how an
/// autonomous caller gets stuck retrying the same malformed write; handing back
/// something correct to copy breaks that loop, because the caller then has a
/// template rather than a constraint to guess at.</para>
/// </summary>
public abstract record NoteWriteResult
{
    /// <summary>The note was written.</summary>
    /// <param name="Anchored">
    /// True when a cited line was found and stored as the anchor. This is what
    /// verification needs, and it is independent of indexing: a citation into a
    /// vendored tree anchors fine even though nothing links it to a chunk.
    /// </param>
    public sealed record Accepted(long Id, IReadOnlyList<long> CitedChunkIds, bool Anchored = false)
        : NoteWriteResult
    {
        /// <summary>True when the evidence resolved to indexed source, so it has chunk edges.</summary>
        public bool IsGrounded => CitedChunkIds.Count > 0;

        /// <summary>True when memory_verify can re-check this note against the file.</summary>
        public bool IsVerifiable => Anchored;

        /// <summary>
        /// Things worth saying about an accepted write: evidence that cannot be
        /// re-verified, an existing note that says nearly the same thing.
        ///
        /// <para>Warnings rather than rejections on purpose. Neither case is
        /// wrong often enough to refuse the write, and a store that refuses
        /// borderline notes teaches its callers to stop writing them.</para>
        /// </summary>
        public IReadOnlyList<string> Warnings { get; init; } = [];
    }

    /// <summary>The note was refused, with everything needed to fix it.</summary>
    public sealed record Rejected(
        RejectionReason Reason,
        string Message,
        NoteExample Example) : NoteWriteResult
    {
        /// <summary>Machine-readable code, e.g. "missing_evidence".</summary>
        public string Code => Reason switch
        {
            RejectionReason.MissingEvidence => "missing_evidence",
            RejectionReason.UnresolvableAnchor => "unresolvable_anchor",
            RejectionReason.DuplicateClaim => "duplicate_claim",
            RejectionReason.ClaimNotFalsifiable => "claim_not_falsifiable",
            _ => "rejected",
        };
    }
}

/// <summary>A correct write, returned alongside every rejection as a template.</summary>
public sealed record NoteExample(string Claim, string Evidence, string Confidence)
{
    public static NoteExample Default => new(
        Claim: "the retry backoff of 1.5s is fitted to measured p95 latency, not chosen",
        Evidence: "service-api/src/Options.h:64; py scripts/measure_latency.py traces.jsonl -> p95 = 240ms",
        Confidence: "measured");
}
