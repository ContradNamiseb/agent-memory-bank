using AgentMemoryBank.Models;

namespace AgentMemoryBank.Continuity;

/// <summary>
/// Compares the checkout a handoff was written in with the one it is being
/// read in, and says what the difference means for trusting it.
///
/// <para>The lines go before the handoff, not after: a reader that stops at
/// "summary: migration done" must already have been told that the migration
/// was done on another branch.</para>
/// </summary>
public static class CheckoutCheck
{
    /// <summary>The trailing instruction every mismatch carries.</summary>
    public const string VerifyAdvice = "Verify before trusting 'done' claims.";

    /// <summary>
    /// Lines to print ahead of the handoff. A mismatch starts with "WARNING:";
    /// a match is one line saying so.
    /// </summary>
    /// <param name="recorded">The checkout stored on the handoff, or null for one saved without it.</param>
    /// <param name="current">The checkout being read in, or null outside a repository.</param>
    /// <param name="currentDirectory">Where the reader is, for naming it when it is not a checkout.</param>
    public static IReadOnlyList<string> Describe(CheckoutState? recorded, CheckoutState? current, string currentDirectory)
    {
        if (recorded is null)
        {
            return
            [
                "WARNING: this handoff has no recorded checkout: it predates checkout tracking or was " +
                "saved outside a git repository, so nothing ties it to a branch or commit. " +
                "Check both yourself. " + VerifyAdvice,
            ];
        }

        var lines = new List<string>();

        if (current is null)
        {
            lines.Add($"WARNING: written on {Where(recorded)}; you are not in a git checkout " +
                      $"(`{currentDirectory}`). {VerifyAdvice}");
            AddUnsavedWork(lines, recorded);
            return lines;
        }

        var sameRepo = GitCheckout.SameRepo(recorded.RepoId, current.RepoId);
        var commitMissing = recorded.Head is not null && !GitCheckout.CommitExists(current.Worktree, recorded.Head);

        // Its own line, first: a moved HEAD means the work may be behind or
        // ahead of you, but a missing commit means it may not exist here at all.
        if (commitMissing)
        {
            lines.Add($"WARNING: commit `{recorded.ShortHead}` from this handoff does not exist in this " +
                      "checkout. The work it describes may not exist here at all: it was never pushed, " +
                      "or it was made in another clone.");
        }

        var matches = sameRepo &&
                      GitCheckout.SamePath(recorded.Worktree, current.Worktree) &&
                      recorded.Branch == current.Branch &&
                      recorded.Head == current.Head;
        if (matches)
        {
            lines.Add($"Checkout: matches where this handoff was written ({Where(current)}).");
            return lines;
        }

        lines.Add($"WARNING: written on {Where(recorded)}; you are on {Where(current)}. {VerifyAdvice}");

        if (!sameRepo)
        {
            lines.Add($"It was written in a different repository (`{recorded.RepoId}`).");
        }
        else if (!commitMissing && recorded.Head is not null && current.Head is not null &&
                 recorded.Head != current.Head)
        {
            if (GitCheckout.IsAncestor(current.Worktree, recorded.Head, current.Head))
            {
                var ahead = GitCheckout.CommitsBetween(current.Worktree, recorded.Head, current.Head);
                lines.Add($"HEAD is {Count(ahead, "commit")} ahead of the handoff's commit, " +
                          "so the commits it describes are included here.");
            }
            else
            {
                lines.Add("HEAD does not contain the handoff's commit: the branch was switched, reset " +
                          "or has diverged, so the committed work it describes is not in this checkout.");
            }
        }

        AddUnsavedWork(lines, recorded);
        return lines;
    }

    /// <summary>Work the handoff's checkout held that no other checkout can have.</summary>
    private static void AddUnsavedWork(List<string> lines, CheckoutState recorded)
    {
        if (recorded.DirtyPaths > 0)
        {
            lines.Add($"When written, `{recorded.Worktree}` had {Count(recorded.DirtyPaths, "uncommitted path")}. " +
                      "That work is in no commit, so it exists only in that working tree, if it is there at all.");
        }

        if (recorded.Ahead > 0)
        {
            lines.Add($"{Count(recorded.Ahead, "commit")} had not been pushed to `{recorded.Upstream}` when written.");
        }
        else if (recorded.Upstream is null && recorded.Branch is not null && recorded.Head is not null)
        {
            lines.Add($"Branch `{recorded.Branch}` had no upstream when written, so none of its commits had been pushed.");
        }
    }

    private static string Where(CheckoutState state) =>
        $"`{state.BranchLabel}` @ `{state.ShortHead}` in `{state.Worktree}`";

    private static string Count(int? n, string noun) =>
        n is null ? $"some {noun}s" : $"{n} {noun}{(n == 1 ? "" : "s")}";
}
