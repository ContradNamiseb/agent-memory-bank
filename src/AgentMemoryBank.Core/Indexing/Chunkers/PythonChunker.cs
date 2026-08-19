using System.Text.RegularExpressions;
using AgentMemoryBank.Models;

namespace AgentMemoryBank.Indexing.Chunkers;

/// <summary>
/// Splits Python on def and class boundaries using indentation, which is the
/// one structural rule the language guarantees.
/// </summary>
internal static partial class PythonChunker
{
    public static IReadOnlyList<Chunk> Chunk(IReadOnlyList<string> lines)
    {
        var collector = new ChunkCollector(lines);
        Walk(collector, lines, 0, lines.Count - 1, indent: 0, prefix: "", depth: 0);
        return collector.Chunks;
    }

    private static void Walk(ChunkCollector collector, IReadOnlyList<string> lines,
        int from, int to, int indent, string prefix, int depth)
    {
        var gapStart = from;
        var i = from;
        while (i <= to)
        {
            var line = lines[i];
            if (ChunkCollector.IsBlank(line) || IndentOf(line) != indent) { i++; continue; }

            var stripped = line[indent..];
            var def = DefRegex().Match(stripped);
            var cls = ClassRegex().Match(stripped);
            if (!def.Success && !cls.Success) { i++; continue; }

            // The body runs to the last line that is blank or indented deeper.
            var end = i;
            for (var j = i + 1; j <= to; j++)
            {
                if (ChunkCollector.IsBlank(lines[j]) || IndentOf(lines[j]) > indent) { end = j; continue; }
                break;
            }

            // Decorators and the comment block immediately above belong to the def.
            var head = i;
            while (head > gapStart)
            {
                var previous = lines[head - 1];
                if (ChunkCollector.IsBlank(previous) || IndentOf(previous) != indent) break;
                var p = previous[indent..];
                if (p.StartsWith('@') || p.StartsWith('#')) { head--; continue; }
                break;
            }

            var symbol = def.Success ? def.Groups[1].Value : cls.Groups[1].Value;
            var qualified = prefix + symbol;
            collector.Emit(gapStart, head - 1, "module", prefix.Length > 0 ? prefix + "(body)" : null);

            var tooBig = end - head + 1 > Chunker.MaxChunkLines;
            if (cls.Success && tooBig && depth < 3)
            {
                var innerIndent = FirstBodyIndent(lines, i + 1, end, indent);
                collector.Emit(head, i, "class", $"{qualified} (declaration)");
                Walk(collector, lines, i + 1, end, innerIndent, $"{qualified}.", depth + 1);
            }
            else
            {
                collector.Emit(head, end, cls.Success ? "class" : "function", qualified);
            }

            gapStart = end + 1;
            i = end + 1;
        }
        collector.Emit(gapStart, to, "module", prefix.Length > 0 ? prefix + "(body)" : null);
    }

    private static int FirstBodyIndent(IReadOnlyList<string> lines, int from, int to, int fallbackIndent)
    {
        for (var j = from; j <= to; j++)
            if (!ChunkCollector.IsBlank(lines[j])) return IndentOf(lines[j]);
        return fallbackIndent + 4;
    }

    /// <summary>Leading width in columns, counting a tab as four.</summary>
    internal static int IndentOf(string line)
    {
        var width = 0;
        foreach (var c in line)
        {
            if (c == ' ') width++;
            else if (c == '\t') width += 4;
            else break;
        }
        return width;
    }

    [GeneratedRegex(@"^(?:async\s+)?def\s+([A-Za-z_]\w*)")]
    private static partial Regex DefRegex();

    [GeneratedRegex(@"^class\s+([A-Za-z_]\w*)")]
    private static partial Regex ClassRegex();
}
