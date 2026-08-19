namespace AgentMemoryBank.Models;

/// <summary>
/// A matching chunk, ranked by BM25 and carrying everything needed to cite it.
/// </summary>
/// <param name="Score">
/// BM25 as SQLite reports it: more negative is a better match, so results sort ascending.
/// </param>
/// <param name="Snippet">Matched terms delimited by &gt;&gt; and &lt;&lt;.</param>
public sealed record ChunkHit(
    long Id,
    string Repo,
    string Path,
    string Lang,
    string Kind,
    string? Symbol,
    int LineStart,
    int LineEnd,
    string? GitRev,
    double Score,
    string Snippet)
{
    /// <summary>repo/path:line — the form a note's evidence field must use.</summary>
    public string Citation => $"{Repo}/{Path}:{LineStart}";
}

/// <summary>A matching note, with its provenance so the reader can weigh it.</summary>
public sealed record NoteHit(
    long Id,
    string Claim,
    string? Rationale,
    string? Evidence,
    NoteKind? Kind,
    Confidence? Confidence,
    string? AnchorSymbol,
    string? CreatedAt,
    string? VerifiedAt,
    long? SupersededBy,
    double Score);

/// <summary>
/// The shape memory_search returns: notes first, then the chunks that ground
/// them. Never a bare summary — the reader must always be able to jump from an
/// answer to the line it came from.
/// </summary>
public sealed record SearchResult(
    string Query,
    IReadOnlyList<NoteHit> Notes,
    IReadOnlyList<ChunkHit> Chunks)
{
    public int Total => Notes.Count + Chunks.Count;
}

/// <summary>Row counts, for the CLI and for a quick sanity check after indexing.</summary>
public sealed record RepoStats(string Repo, int Files, int Chunks);

public sealed record StoreStats(
    int Files,
    int Chunks,
    int Notes,
    int Runs,
    IReadOnlyList<RepoStats> PerRepo);
