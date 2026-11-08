using System.Text.Json.Serialization;

namespace AgentMemoryBank.Models;

/// <summary>
/// Where a handoff was written, read from git rather than typed by the agent.
///
/// <para>A handoff that says "migration done" is a claim about one checkout.
/// Without this it reads as true in every worktree, on every branch, and in a
/// fresh clone that never received the commit.</para>
/// </summary>
public sealed record CheckoutState
{
    /// <summary>
    /// Which repository: origin's URL as "host/owner/repo", so every clone and
    /// worktree of one project shares it, or the common git directory (shared by
    /// a repository's worktrees) when there is no origin. A clone that lacks a
    /// handoff's commit is the same repository and still finds the handoff, and
    /// the missing commit is reported rather than hidden.
    /// </summary>
    public required string RepoId { get; init; }

    /// <summary>Top level of the working tree the handoff was written in.</summary>
    public required string Worktree { get; init; }

    /// <summary>Checked-out branch, or null when HEAD is detached.</summary>
    public string? Branch { get; init; }

    /// <summary>HEAD commit, or null in a repository with no commits yet.</summary>
    public string? Head { get; init; }

    /// <summary>Changed and untracked paths. Their content exists only in that working tree.</summary>
    public int DirtyPaths { get; init; }

    /// <summary>The branch's upstream, or null when it has none.</summary>
    public string? Upstream { get; init; }

    /// <summary>Commits on HEAD that the upstream does not have; null without an upstream.</summary>
    public int? Ahead { get; init; }

    /// <summary>The branch as a person reads it: its name, or "detached HEAD".</summary>
    public string BranchLabel => Branch ?? "detached HEAD";

    /// <summary>The first seven characters of HEAD, or "no commits".</summary>
    public string ShortHead => Head is null ? "no commits" : Head[..Math.Min(7, Head.Length)];
}

/// <summary>Which handoffs "latest" and "list" consider.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<HandoffScope>))]
public enum HandoffScope
{
    /// <summary>Handoffs written in any worktree of the current repository.</summary>
    [JsonStringEnumMemberName("repo")] Repo,
    /// <summary>Handoffs written in the current worktree only.</summary>
    [JsonStringEnumMemberName("worktree")] Worktree,
    /// <summary>Every handoff, from any repository, including ones with no recorded checkout.</summary>
    [JsonStringEnumMemberName("all")] All,
}
