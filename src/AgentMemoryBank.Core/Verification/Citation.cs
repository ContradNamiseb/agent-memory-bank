using System.Text.RegularExpressions;

namespace AgentMemoryBank.Verification;

/// <summary>
/// A pointer from a note into indexed source: repository, path, and line.
/// </summary>
/// <remarks>
/// Written as <c>repo/path:line</c> or <c>repo:path:line</c>; both are accepted
/// because both get typed. A line range (<c>:12-40</c>) keeps only its start —
/// the anchor is a single line, and a range would give nothing precise to
/// re-locate when the file moves.
/// </remarks>
public sealed partial record Citation(string Repo, string Path, int Line)
{
    public override string ToString() => $"{Repo}/{Path}:{Line}";

    /// <summary>Every citation found in a free-text evidence field.</summary>
    public static IReadOnlyList<Citation> ParseAll(string? evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence)) return [];

        var citations = new List<Citation>();
        foreach (Match match in CitationRegex().Matches(evidence))
        {
            var repo = match.Groups["repo"].Value;
            var path = match.Groups["path"].Value.Replace('\\', '/');
            if (!int.TryParse(match.Groups["line"].Value, out var line) || line < 1) continue;
            citations.Add(new Citation(repo, path, line));
        }
        return citations;
    }

    /// <summary>
    /// True when the evidence describes something that could be re-run: a command,
    /// a script invocation, or a measurement with a number attached.
    ///
    /// <para>This is deliberately generous. The rule it enforces is "show your
    /// working, not just your conclusion", and a rejection that an author cannot
    /// see how to satisfy teaches them to route around the store instead.</para>
    /// </summary>
    public static bool LooksReproducible(string? evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence)) return false;
        return CommandRegex().IsMatch(evidence) || MeasurementRegex().IsMatch(evidence);
    }

    // repo/path.ext:123  or  repo:path.ext:123
    [GeneratedRegex(@"(?<repo>[A-Za-z0-9._-]+)[/:](?<path>[A-Za-z0-9._/\\-]+\.[A-Za-z0-9]+):(?<line>\d+)")]
    private static partial Regex CitationRegex();

    // A flag, a shell-ish invocation, or an explicit command.
    [GeneratedRegex(@"(^|\s)(--?[A-Za-z][\w-]*|\S+\.(py|sh|ps1|exe|cmd|js|mjs)\b|(git|dotnet|python|py|node|npm|cargo|make|bash|pwsh)\s+\S+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex CommandRegex();

    // A number carrying meaning: "RMSE 0.015", "p95 = 240ms", "12% faster".
    [GeneratedRegex(@"\d+(\.\d+)?\s*(%|ms|s\b|MB|GB|x\b)|[A-Za-z_][\w]*\s*[=:]\s*-?\d+(\.\d+)?")]
    private static partial Regex MeasurementRegex();
}
