using AgentMemoryBank.Models;

namespace AgentMemoryBank.Indexing.Chunkers;

/// <summary>
/// The single funnel every chunker emits through. It owns three rules that must
/// hold no matter which language produced the span: blank edges are trimmed,
/// oversized spans are split into windows, and contentless leftovers are dropped.
/// </summary>
internal sealed class ChunkCollector(IReadOnlyList<string> lines)
{
    private readonly List<Chunk> _chunks = [];

    public IReadOnlyList<Chunk> Chunks => _chunks;

    /// <summary>Emit lines [start, end] (0-based, inclusive) as a chunk.</summary>
    public void Emit(int start, int end, string kind, string? symbol = null)
    {
        while (start <= end && IsBlank(lines[start])) start++;
        while (end >= start && IsBlank(lines[end])) end--;
        if (start > end) return;

        var body = Join(start, end);
        if (!HasSubstance(body, symbol)) return;

        if (end - start + 1 <= Chunker.MaxChunkLines)
        {
            _chunks.Add(new Chunk(kind, symbol, start + 1, end + 1, body));
            return;
        }

        // Windows never overlap: an overlapping copy would give one line two
        // citations. Each goes back through Emit so it gets the same blank-edge
        // trimming and substance check as any other chunk.
        var part = 1;
        for (var s = start; s <= end; s += Chunker.MaxChunkLines, part++)
        {
            var e = Math.Min(s + Chunker.MaxChunkLines - 1, end);
            Emit(s, e, kind, symbol is null ? null : $"{symbol} (part {part})");
        }
    }

    private string Join(int start, int end) =>
        string.Join('\n', lines.Skip(start).Take(end - start + 1));

    public static bool IsBlank(string line) => line.Trim().Length == 0;

    /// <summary>
    /// A leftover holding only braces, a closing "};" or a bare #endif is a row
    /// that could never usefully be retrieved. A short constant such as
    /// "CONST = 3" is worth citing, so the test is for content, not length.
    /// Anything with a symbol is always kept.
    /// </summary>
    private static bool HasSubstance(string body, string? symbol)
    {
        if (symbol is not null) return true;
        var bare = new string(body.Where(c => !char.IsWhiteSpace(c) && !"{}();,".Contains(c)).ToArray());
        return bare.Length > 0 && bare != "#endif" && bare.Any(char.IsLetterOrDigit);
    }
}
