using System.Text.Json.Serialization;

namespace AgentMemoryBank.Models;

// The JSON names are the lowercase strings the note table stores, so an MCP
// tool taking one of these enums advertises exactly the values the store
// accepts and the schema carries them as an "enum" list.

/// <summary>How a claim was established. Ordered from strongest to weakest.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Confidence>))]
public enum Confidence
{
    /// <summary>Backed by a measurement or a fit that can be reproduced.</summary>
    [JsonStringEnumMemberName("measured")] Measured,
    /// <summary>Read off code, commits or docs, but not measured.</summary>
    [JsonStringEnumMemberName("inferred")] Inferred,
    /// <summary>Believed, with nothing behind it yet. Treat with suspicion.</summary>
    [JsonStringEnumMemberName("assumed")] Assumed,
}

/// <summary>What kind of durable knowledge a note carries.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NoteKind>))]
public enum NoteKind
{
    /// <summary>A choice that was made, and what it was made instead of.</summary>
    [JsonStringEnumMemberName("decision")] Decision,
    /// <summary>Something that will bite: a default that destroys data, a silent truncation.</summary>
    [JsonStringEnumMemberName("hazard")] Hazard,
    /// <summary>A procedure worth not re-deriving.</summary>
    [JsonStringEnumMemberName("howto")] Howto,
    /// <summary>A measured fact about the system.</summary>
    [JsonStringEnumMemberName("finding")] Finding,
}

/// <summary>
/// The valuable row: a falsifiable claim with the evidence
/// that established it.
///
/// <para>Notes are the product; the file index exists to ground them. What is
/// lost between sessions is decisions and hazards, which are written down
/// nowhere and cannot be grepped.</para>
///
/// <para><see cref="AnchorText"/> and <see cref="AnchorSymbol"/> are what let
/// verification survive line drift: when a cited file changes, the anchor is
/// re-located rather than the note being declared stale.</para>
/// </summary>
public sealed record Note
{
    public long Id { get; init; }

    /// <summary>One sentence, falsifiable.</summary>
    public required string Claim { get; init; }

    /// <summary>Why, and how it was established.</summary>
    public string? Rationale { get; init; }

    /// <summary>repo:path:line references, commands, measurements. Required at write time.</summary>
    public string? Evidence { get; init; }

    /// <summary>Enclosing function or class, if any. The fallback when the anchor text moves.</summary>
    public string? AnchorSymbol { get; init; }

    /// <summary>One cited line, whitespace-normalised. The most distinctive line, not the first.</summary>
    public string? AnchorText { get; init; }

    public NoteKind? Kind { get; init; }
    public Confidence? Confidence { get; init; }

    public string? CreatedAt { get; init; }

    /// <summary>When the evidence was last re-checked. Surfaced on every read so it can be weighed.</summary>
    public string? VerifiedAt { get; init; }

    /// <summary>Notes are superseded, never deleted, so the record of having been wrong survives.</summary>
    public long? SupersededBy { get; init; }

    public long? RunId { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];
}
