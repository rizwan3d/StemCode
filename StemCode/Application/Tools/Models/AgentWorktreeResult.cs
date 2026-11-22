namespace StemCode.Application.Tools.Models;

/// <summary>
/// What happened to the isolated git worktree a subagent ran in.
/// </summary>
public sealed class AgentWorktreeResult
{
    public AgentWorktreeResult(
        string name,
        string path,
        string branch,
        bool kept,
        IReadOnlyList<string> changedFiles,
        int commitCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentNullException.ThrowIfNull(changedFiles);

        Name = name;
        Path = path;
        Branch = branch;
        Kept = kept;
        ChangedFiles = changedFiles.ToArray();
        CommitCount = commitCount;
    }

    public string Branch { get; }

    public IReadOnlyList<string> ChangedFiles { get; }

    public int CommitCount { get; }

    /// <summary>True when the worktree had changes and was left in place for review.</summary>
    public bool Kept { get; }

    public string Name { get; }

    /// <summary>Worktree location, relative to the parent workspace when it is inside it.</summary>
    public string Path { get; }

    public string Describe()
    {
        if (!Kept)
        {
            return $"Worktree {Name} made no changes and was removed.";
        }

        string files = ChangedFiles.Count == 1 ? "1 changed file" : $"{ChangedFiles.Count} changed files";
        string commits = CommitCount switch
        {
            0 => string.Empty,
            1 => ", 1 commit",
            _ => $", {CommitCount} commits"
        };
        return $"Worktree kept at {Path} on branch {Branch} ({files}{commits}). " +
            $"Review with `git -C \"{Path}\" status` / `git -C \"{Path}\" diff`; " +
            $"bring it in by committing there and running `git merge {Branch}`, then remove it with /worktree remove {Name}.";
    }
}
