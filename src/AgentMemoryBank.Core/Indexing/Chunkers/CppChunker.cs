using System.Text;
using System.Text.RegularExpressions;
using AgentMemoryBank.Models;

namespace AgentMemoryBank.Indexing.Chunkers;

/// <summary>
/// Splits C and C++ on function and class boundaries by brace matching.
///
/// This is deliberately not a parser. It needs to be right about where a
/// definition starts and ends often enough to cite it, and it must never claim
/// a line range it cannot back up — not to resolve overloads or templates.
/// </summary>
internal static partial class CppChunker
{
    public static IReadOnlyList<Chunk> Chunk(IReadOnlyList<string> lines)
    {
        var masked = MaskAll(lines);
        var collector = new ChunkCollector(lines);
        Walk(collector, lines, masked, 0, lines.Count - 1, prefix: "", depth: 0);
        return collector.Chunks;
    }

    /// <summary>
    /// Walk one brace level, emitting a chunk per definition and grouping what
    /// sits between definitions (includes, constants, field declarations) into
    /// "toplevel" chunks. That is where a line like "float retry_backoff = 1.5f;"
    /// lives, so dropping the gaps would lose exactly the lines worth citing.
    /// </summary>
    private static void Walk(ChunkCollector collector, IReadOnlyList<string> lines,
        IReadOnlyList<string> masked, int from, int to, string prefix, int depth)
    {
        var gapStart = from;
        var i = from;
        while (i <= to)
        {
            if (ChunkCollector.IsBlank(masked[i]) || !masked[i].Contains('{')) { i++; continue; }

            var end = FindClosingLine(masked, i, to);
            if (end < 0) { i++; continue; }   // unbalanced; leave it to the gap chunk

            var head = ExpandHeader(lines, masked, i, gapStart);
            var kind = KindOf(masked[i]);
            var symbol = SymbolOf(string.Join(' ', masked.Skip(head).Take(i - head + 1)));
            var qualified = symbol is not null && prefix.Length > 0 ? prefix + symbol : symbol;

            collector.Emit(gapStart, head - 1, "toplevel", prefix.Length > 0 ? prefix + "(decls)" : null);

            var tooBig = end - head + 1 > Chunker.MaxChunkLines;
            if (kind == "namespace")
            {
                // Transparent: a namespace is not a definition, so index its
                // contents at this level. Only a doc comment above it is kept.
                collector.Emit(head, i - 1, "toplevel");
                Walk(collector, lines, masked, i + 1, end - 1, prefix, depth);
            }
            else if (kind == "class" && tooBig && depth < 3)
            {
                // Too big to be one readable chunk: index the members instead.
                collector.Emit(head, i, "class", qualified is null ? null : $"{qualified} (declaration)");
                Walk(collector, lines, masked, i + 1, end - 1, $"{qualified}::", depth + 1);
            }
            else
            {
                collector.Emit(head, end, kind, qualified);
            }

            gapStart = end + 1;
            i = end + 1;
        }
        collector.Emit(gapStart, to, "toplevel", prefix.Length > 0 ? prefix + "(decls)" : null);
    }

    /// <summary>Line on which the block opened at <paramref name="open"/> closes, or -1.</summary>
    private static int FindClosingLine(IReadOnlyList<string> masked, int open, int to)
    {
        var level = 0;
        for (var j = open; j <= to; j++)
        {
            foreach (var c in masked[j])
            {
                if (c == '{') level++;
                else if (c == '}') level--;
            }
            if (level <= 0) return j;
        }
        return -1;
    }

    /// <summary>
    /// Expand backwards over the doc comment, template header and any
    /// continuation lines of the signature, stopping at a blank line, a
    /// preprocessor directive, or the end of the previous statement.
    /// </summary>
    private static int ExpandHeader(IReadOnlyList<string> lines, IReadOnlyList<string> masked,
        int open, int limit)
    {
        var head = open;
        while (head > limit)
        {
            var previous = lines[head - 1];
            var previousCode = masked[head - 1].Trim();
            if (ChunkCollector.IsBlank(previous)) break;
            if (previous.TrimStart().StartsWith('#')) break;
            if (previousCode.Length > 0 && (previousCode[^1] is ';' or '{' or '}')) break;
            head--;
        }
        return head;
    }

    private static string KindOf(string header)
    {
        if (NamespaceRegex().IsMatch(header)) return "namespace";
        if (ContainerRegex().IsMatch(header)) return "class";
        return "function";
    }

    /// <summary>Best-effort name for a definition: the type name, or the function name.</summary>
    internal static string? SymbolOf(string header)
    {
        var flat = WhitespaceRegex().Replace(header, " ").Trim();

        var container = ContainerNameRegex().Match(flat);
        if (container.Success) return container.Groups[2].Value;

        var paren = flat.IndexOf('(');
        var left = paren < 0 ? flat : flat[..paren];
        left = TemplateRegex().Replace(left, "").Trim();

        var name = FunctionNameRegex().Match(left);
        return name.Success ? WhitespaceRegex().Replace(name.Groups[1].Value, "") : null;
    }

    /// <summary>
    /// Blank out string, character and comment content so brace counting is not
    /// fooled by a brace inside a literal or a commented-out block. Block
    /// comment and string state carry across lines.
    /// </summary>
    internal static IReadOnlyList<string> MaskAll(IReadOnlyList<string> lines)
    {
        var masked = new List<string>(lines.Count);
        var inBlockComment = false;
        char? inString = null;
        foreach (var line in lines) masked.Add(MaskLine(line, ref inBlockComment, ref inString));
        return masked;
    }

    internal static string MaskLine(string line, ref bool inBlockComment, ref char? inString)
    {
        var masked = new StringBuilder(line.Length);
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            var next = i + 1 < line.Length ? line[i + 1] : '\0';

            if (inBlockComment)
            {
                if (c == '*' && next == '/') { inBlockComment = false; i++; masked.Append("  "); }
                else masked.Append(' ');
                continue;
            }
            if (inString is not null)
            {
                if (c == '\\') { masked.Append("  "); i++; continue; }
                if (c == inString) { inString = null; masked.Append(' '); continue; }
                masked.Append(' ');
                continue;
            }
            if (c == '/' && next == '*') { inBlockComment = true; i++; masked.Append("  "); continue; }
            if (c == '/' && next == '/') { masked.Append(' ', line.Length - i); break; }
            if (c is '"' or '\'') { inString = c; masked.Append(' '); continue; }
            masked.Append(c);
        }
        return masked.ToString();
    }

    [GeneratedRegex(@"\bnamespace\b")]
    private static partial Regex NamespaceRegex();

    [GeneratedRegex(@"\b(class|struct|union|namespace|enum)\b")]
    private static partial Regex ContainerRegex();

    [GeneratedRegex(@"\b(class|struct|union|enum\s+class|enum|namespace)\s+([A-Za-z_][\w:]*)")]
    private static partial Regex ContainerNameRegex();

    [GeneratedRegex(@"\btemplate\s*<[^>]*>\s*")]
    private static partial Regex TemplateRegex();

    [GeneratedRegex(@"(operator\s*[^\s(]+|[A-Za-z_~][\w:~]*)\s*$")]
    private static partial Regex FunctionNameRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
