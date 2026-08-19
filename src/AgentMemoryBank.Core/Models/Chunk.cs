namespace AgentMemoryBank.Models;

/// <summary>
/// One indexed span of a file: a function, a class, a doc section, a config
/// block, or the declarations between them.
///
/// <para><see cref="LineStart"/> and <see cref="LineEnd"/> are 1-based and
/// inclusive, and <see cref="Body"/> is exactly those lines joined by "\n".
/// Citations are the whole point of the store, so that invariant is asserted in
/// the tests rather than assumed.</para>
/// </summary>
/// <param name="Kind">function, class, section, block, module, toplevel, doc, file.</param>
/// <param name="Symbol">Qualified name where one could be determined, else null.</param>
public sealed record Chunk(string Kind, string? Symbol, int LineStart, int LineEnd, string Body)
{
    public int LineCount => LineEnd - LineStart + 1;
}
