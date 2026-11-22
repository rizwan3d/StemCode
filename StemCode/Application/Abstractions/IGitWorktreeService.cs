namespace StemCode.Application.Abstractions;

/// <summary>
/// Creates and cleans up isolated git worktrees so subagents and parallel
/// sessions can edit files without touching the main workspace.
/// </summary>
public interface IGitWorktreeService
{
    /// <summary>
    /// Creates a new worktree for the repository that contains
    /// <paramref name="workspaceRoot"/>, checked out on a fresh branch from HEAD.
    /// </summary>
    Task<GitWorktreeHandle> CreateAsync(
        string workspaceRoot,
        string? name,
        CancellationToken cancellationToken);

    /// <summary>
    /// Inspects the worktree after use. A worktree with no uncommitted changes
    /// and no new commits is removed together with its branch; otherwise it is
    /// kept so its changes can be reviewed and merged.
    /// </summary>
    Task<GitWorktreeOutcome> FinishAsync(
        GitWorktreeHandle worktree,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists the StemCode-managed worktrees of the repository that contains
    /// <paramref name="workspaceRoot"/>.
    /// </summary>
    Task<IReadOnlyList<GitWorktreeInfo>> ListAsync(
        string workspaceRoot,
        CancellationToken cancellationToken);

    /// <summary>
    /// Force-removes a StemCode-managed worktree and deletes its branch.
    /// </summary>
    Task RemoveAsync(
        string workspaceRoot,
        string name,
        CancellationToken cancellationToken);
}

/// <param name="Name">Short worktree name, unique within the repository.</param>
/// <param name="RepositoryRoot">Top-level directory of the main checkout.</param>
/// <param name="WorktreePath">Top-level directory of the new worktree.</param>
/// <param name="WorkspaceRoot">
/// The worktree equivalent of the caller's workspace root (it differs from
/// <paramref name="WorktreePath"/> when the workspace is a repository subdirectory).
/// </param>
/// <param name="Branch">Branch checked out in the worktree.</param>
/// <param name="BaseCommit">Commit the branch was created from.</param>
public sealed record GitWorktreeHandle(
    string Name,
    string RepositoryRoot,
    string WorktreePath,
    string WorkspaceRoot,
    string Branch,
    string BaseCommit);

/// <param name="Kept">True when the worktree had changes and was left in place.</param>
/// <param name="ChangedFiles">Files that differ from the base commit, relative to the worktree.</param>
/// <param name="CommitCount">Commits made on the worktree branch since the base commit.</param>
public sealed record GitWorktreeOutcome(
    GitWorktreeHandle Worktree,
    bool Kept,
    IReadOnlyList<string> ChangedFiles,
    int CommitCount);

public sealed record GitWorktreeInfo(
    string Name,
    string WorktreePath,
    string? Branch,
    string? HeadCommit);
