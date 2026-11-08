using Microsoft.Data.Sqlite;
using AgentMemoryBank.Database;
using AgentMemoryBank.Models;

namespace AgentMemoryBank.Continuity;

/// <summary>
/// Reading handoffs relative to where the reader is: which ones a scope
/// selects, and what the reader must be told before trusting one. Shared by the
/// MCP tools and the command line so both say the same thing.
/// </summary>
public static class HandoffReader
{
    /// <summary>The filters a scope comes to from <paramref name="current"/>.</summary>
    /// <param name="Notice">Set when the scope could not apply and every handoff was considered instead.</param>
    public sealed record ResolvedScope(string? RepoId, string? Worktree, HandoffScope Scope, string? Notice);

    /// <summary>The result of looking up the newest handoff.</summary>
    /// <param name="Handoff">The handoff, or null when none matched.</param>
    /// <param name="Preamble">Lines to print before it: scope notices, then checkout warnings.</param>
    /// <param name="Miss">Why nothing was found, when <paramref name="Handoff"/> is null.</param>
    public sealed record Lookup(SessionHandoff? Handoff, IReadOnlyList<string> Preamble, string? Miss);

    /// <summary>
    /// A repository or worktree scope needs a checkout to be relative to.
    /// Outside one it widens to every handoff and says so, rather than finding
    /// nothing and looking like an empty store.
    /// </summary>
    public static ResolvedScope Resolve(HandoffScope scope, CheckoutState? current) => (scope, current) switch
    {
        (HandoffScope.All, _) => new(null, null, HandoffScope.All, null),
        (_, null) => new(null, null, HandoffScope.All,
            "Not in a git checkout, so this considers handoffs from every repository."),
        (HandoffScope.Worktree, _) => new(null, current.Worktree, HandoffScope.Worktree, null),
        _ => new(current.RepoId, null, HandoffScope.Repo, null),
    };

    /// <summary>The newest handoff within <paramref name="scope"/> of <paramref name="directory"/>.</summary>
    public static Lookup Latest(SqliteConnection db, string? agent, HandoffScope scope, string directory)
    {
        var current = GitCheckout.Capture(directory);
        var resolved = Resolve(scope, current);
        var handoff = HandoffStore.GetLatest(db, agent, resolved.RepoId, resolved.Worktree);

        var preamble = new List<string>();
        if (resolved.Notice is not null) preamble.Add(resolved.Notice);

        if (handoff is null) return new(null, preamble, Miss(db, agent, resolved, current));

        preamble.AddRange(CheckoutCheck.Describe(handoff.Checkout, current, directory));
        return new(handoff, preamble, null);
    }

    /// <summary>
    /// Why nothing matched. A scoped miss names how many handoffs it left out,
    /// because after an upgrade every older handoff has no checkout and a bare
    /// "none" would read as an empty store.
    /// </summary>
    public static string Miss(SqliteConnection db, string? agent, ResolvedScope resolved, CheckoutState? current)
    {
        var total = HandoffStore.Count(db, agent);
        var from = string.IsNullOrWhiteSpace(agent) ? "" : $" from an agent matching '{agent}'";

        if (resolved.Scope == HandoffScope.All || current is null || total == 0)
            return $"No handoff{from} recorded yet.";

        var what = resolved.Scope == HandoffScope.Worktree ? "worktree" : "repository";
        return $"No handoff{from} for this {what} (`{current.Worktree}`). " +
               $"{total} other handoff{(total == 1 ? "" : "s")} exist{(total == 1 ? "s" : "")}, written in other " +
               "checkouts or before checkouts were recorded; use scope 'all' to see them.";
    }
}
