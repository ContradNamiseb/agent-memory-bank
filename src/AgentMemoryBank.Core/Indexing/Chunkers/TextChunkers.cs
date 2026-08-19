using System.Text.RegularExpressions;
using AgentMemoryBank.Models;

namespace AgentMemoryBank.Indexing.Chunkers;

/// <summary>Splits Markdown into sections, one per heading.</summary>
internal static partial class MarkdownChunker
{
    public static IReadOnlyList<Chunk> Chunk(IReadOnlyList<string> lines)
    {
        var collector = new ChunkCollector(lines);
        var headings = FindHeadings(lines);

        if (headings.Count == 0)
        {
            collector.Emit(0, lines.Count - 1, "doc");
            return collector.Chunks;
        }

        collector.Emit(0, headings[0].Line - 1, "doc");
        for (var k = 0; k < headings.Count; k++)
        {
            var end = k + 1 < headings.Count ? headings[k + 1].Line - 1 : lines.Count - 1;
            collector.Emit(headings[k].Line, end, "section", headings[k].Title);
        }
        return collector.Chunks;
    }

    /// <summary>Headings outside fenced code. A "# comment" in a bash fence is not one.</summary>
    private static List<(int Line, string Title)> FindHeadings(IReadOnlyList<string> lines)
    {
        var headings = new List<(int, string)>();
        char? fence = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var fenceMatch = FenceRegex().Match(lines[i]);
            if (fenceMatch.Success)
            {
                var marker = fenceMatch.Groups[1].Value[0];
                if (fence is null) fence = marker;
                else if (fence == marker) fence = null;
                continue;
            }
            if (fence is not null) continue;

            var heading = HeadingRegex().Match(lines[i]);
            if (heading.Success) headings.Add((i, heading.Groups[2].Value));
        }
        return headings;
    }

    [GeneratedRegex("^\\s{0,3}([`~]{3,})")]
    private static partial Regex FenceRegex();

    [GeneratedRegex(@"^(#{1,6})\s+(.*?)\s*#*\s*$")]
    private static partial Regex HeadingRegex();
}

/// <summary>
/// Splits brace-delimited config into its top-level blocks: a loader
/// textproto's "stage {}", or a .proto file's "message Foo {}".
/// </summary>
internal static partial class BracedChunker
{
    private static readonly HashSet<string> ProtoKeywords =
        ["message", "enum", "service", "oneof", "extend", "rpc", "group"];

    public static IReadOnlyList<Chunk> Chunk(IReadOnlyList<string> lines)
    {
        var collector = new ChunkCollector(lines);
        var masked = lines.Select(l => CommentRegex().Replace(l, "")).ToList();

        var gapStart = 0;
        var i = 0;
        while (i < lines.Count)
        {
            if (!masked[i].Contains('{')) { i++; continue; }

            var level = 0;
            var end = -1;
            for (var j = i; j < lines.Count; j++)
            {
                foreach (var c in masked[j])
                {
                    if (c == '{') level++;
                    else if (c == '}') level--;
                }
                if (level <= 0) { end = j; break; }
            }
            if (end < 0) { i++; continue; }

            var head = i;
            while (head > gapStart && !ChunkCollector.IsBlank(lines[head - 1])
                   && LeadingCommentRegex().IsMatch(lines[head - 1])) head--;

            collector.Emit(gapStart, head - 1, "block");
            collector.Emit(head, end, "block", SymbolOf(string.Join(' ', lines.Skip(i).Take(2))));
            gapStart = end + 1;
            i = end + 1;
        }
        collector.Emit(gapStart, lines.Count - 1, "block");
        return collector.Chunks;
    }

    private static string? SymbolOf(string header)
    {
        var flat = WhitespaceRegex().Replace(header, " ").Trim();
        var brace = flat.IndexOf('{');
        if (brace >= 0) flat = flat[..brace].Trim();

        var tokens = flat.Split([' ', ':', '='], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return null;
        if (ProtoKeywords.Contains(tokens[0]) && tokens.Length > 1) return $"{tokens[0]} {tokens[1]}";
        return tokens[0];
    }

    [GeneratedRegex(@"(#|//).*$")]
    private static partial Regex CommentRegex();

    [GeneratedRegex(@"^\s*(//|#)")]
    private static partial Regex LeadingCommentRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}

/// <summary>Splits YAML into one chunk per top-level key or document.</summary>
internal static partial class YamlChunker
{
    public static IReadOnlyList<Chunk> Chunk(IReadOnlyList<string> lines)
    {
        var collector = new ChunkCollector(lines);
        var starts = new List<(int Line, string? Key)>();

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (ChunkCollector.IsBlank(line) || CommentRegex().IsMatch(line)) continue;
            if (line.Trim() is "---" or "...") { starts.Add((i, null)); continue; }
            if (PythonChunker.IndentOf(line) != 0) continue;

            var key = KeyRegex().Match(line);
            if (key.Success) starts.Add((i, key.Groups[1].Value));
        }

        if (starts.Count == 0)
        {
            collector.Emit(0, lines.Count - 1, "block");
            return collector.Chunks;
        }

        collector.Emit(0, starts[0].Line - 1, "block");
        for (var k = 0; k < starts.Count; k++)
        {
            var head = starts[k].Line;
            while (head > 0 && CommentRegex().IsMatch(lines[head - 1])) head--;
            var end = k + 1 < starts.Count ? starts[k + 1].Line - 1 : lines.Count - 1;
            collector.Emit(head, end, "block", starts[k].Key);
        }
        return collector.Chunks;
    }

    [GeneratedRegex(@"^\s*#")]
    private static partial Regex CommentRegex();

    [GeneratedRegex("""^([A-Za-z_"'][^:\s]*)\s*:""")]
    private static partial Regex KeyRegex();
}
