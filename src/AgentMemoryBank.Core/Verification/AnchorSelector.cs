using System.Text.RegularExpressions;

namespace AgentMemoryBank.Verification;

/// <summary>
/// Pure domain policy for picking a resilient anchor line and normalising lines
/// so whitespace-only reformats do not register as deletions.
/// </summary>
public static partial class AnchorSelector
{
    /// <summary>
    /// Pick the line worth anchoring to.
    ///
    /// <para>The cited line itself wins whenever it can carry the anchor, and
    /// that preference is not a tie-breaker — it is the point. A note about
    /// <c>retry_backoff = 1.5f</c> that anchored to the explanatory comment above
    /// it would keep matching after someone changed the value, and verification
    /// would report the note as fine while the thing it describes had moved out
    /// from under it. Silent false confidence is the worst failure this store
    /// has.</para>
    ///
    /// <para>Only when the cited line cannot serve — it is blank, or trivial like
    /// <c>}</c> or <c>return;</c>, or repeats elsewhere in the file so a match
    /// would be ambiguous — does the search widen to its neighbours, scoring them
    /// by rarity, substance, and closeness.</para>
    /// </summary>
    public static string? ChooseAnchorText(IReadOnlyList<string> fileLines, int citedLine, int window = 2)
    {
        if (fileLines.Count == 0 || citedLine < 1 || citedLine > fileLines.Count) return null;

        var counts = fileLines.GroupBy(Normalise).ToDictionary(g => g.Key, g => g.Count());

        var cited = Normalise(fileLines[citedLine - 1]);
        if (CanAnchor(cited, counts)) return cited;

        string? best = null;
        var bestScore = int.MinValue;
        var from = Math.Max(1, citedLine - window);
        var to = Math.Min(fileLines.Count, citedLine + window);
        for (var line = from; line <= to; line++)
        {
            var normalised = Normalise(fileLines[line - 1]);
            if (!CanAnchor(normalised, counts)) continue;

            var score = Math.Min(normalised.Length, 80) - 3 * Math.Abs(line - citedLine);
            if (score > bestScore) { bestScore = score; best = normalised; }
        }
        return best;
    }

    /// <summary>A line can anchor only if it is unique in the file and says something.</summary>
    private static bool CanAnchor(string normalised, Dictionary<string, int> counts) =>
        normalised.Length >= 8
        && normalised.Any(char.IsLetterOrDigit)
        && counts.GetValueOrDefault(normalised, 1) == 1;

    /// <summary>
    /// Collapse whitespace before comparing. Without this every reformat looks
    /// like a deletion, and the store cries wolf after each formatting pass.
    /// </summary>
    public static string Normalise(string line) => WhitespaceRegex().Replace(line, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
