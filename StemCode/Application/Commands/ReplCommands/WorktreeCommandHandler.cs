using StemCode.Application.Abstractions;
using StemCode.Application.Models;
using System.Text;

namespace StemCode.Application.Commands;

/// <summary>
/// Lists and cleans up the isolated git worktrees that subagents (and
/// <c>--worktree</c> sessions) create under <c>.stemcode/worktrees</c>.
/// </summary>
internal sealed class WorktreeCommandHandler : IReplCommandHandler
{
    public const string UsageText = "/worktree [list|remove <name>|remove --all]";

    private readonly IGitWorktreeService _worktreeService;
    private readonly IWorkspaceRootProvider _workspaceRootProvider;

    public WorktreeCommandHandler(
        IGitWorktreeService worktreeService,
        IWorkspaceRootProvider workspaceRootProvider)
    {
        _worktreeService = worktreeService;
        _workspaceRootProvider = workspaceRootProvider;
    }

    public string CommandName => "worktree";

    public string Description => "List or remove isolated git worktrees created for subagents and parallel sessions.";

    public string Usage => UsageText;

    public async Task<ReplCommandResult> ExecuteAsync(
        ReplCommandContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        string action = context.Arguments.Count == 0
            ? "list"
            : context.Arguments[0].Trim().ToLowerInvariant();
        string workspaceRoot = _workspaceRootProvider.GetWorkspaceRoot();

        try
        {
            return action switch
            {
                "list" or "ls" when context.Arguments.Count <= 1 => await ListAsync(workspaceRoot, cancellationToken),
                "remove" or "rm" or "delete" when context.Arguments.Count == 2 => await RemoveAsync(
                    workspaceRoot,
                    context.Arguments[1].Trim(),
                    cancellationToken),
                "help" or "-h" or "--help" => ReplCommandResult.Continue(Usage, ReplFeedbackKind.Info),
                _ => ReplCommandResult.Continue($"Usage: {Usage}", ReplFeedbackKind.Error)
            };
        }
        catch (InvalidOperationException exception)
        {
            return ReplCommandResult.Continue(exception.Message, ReplFeedbackKind.Error);
        }
        catch (ArgumentException exception)
        {
            return ReplCommandResult.Continue(exception.Message, ReplFeedbackKind.Error);
        }
    }

    private async Task<ReplCommandResult> ListAsync(
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<GitWorktreeInfo> worktrees = await _worktreeService.ListAsync(workspaceRoot, cancellationToken);
        if (worktrees.Count == 0)
        {
            return ReplCommandResult.Continue(
                "No StemCode worktrees. Subagents create one when delegated with isolation 'worktree'; start a parallel session in one with `stemcode --worktree [name]`.",
                ReplFeedbackKind.Info);
        }

        StringBuilder builder = new();
        builder.AppendLine($"StemCode worktrees ({worktrees.Count}):");
        foreach (GitWorktreeInfo worktree in worktrees)
        {
            string path = Path.GetRelativePath(workspaceRoot, worktree.WorktreePath).Replace('\\', '/');
            string head = string.IsNullOrWhiteSpace(worktree.HeadCommit)
                ? string.Empty
                : $" @ {worktree.HeadCommit[..Math.Min(8, worktree.HeadCommit.Length)]}";
            builder.AppendLine($"- {worktree.Name}  {worktree.Branch ?? "(detached)"}{head}  {path}");
        }

        builder.Append("Merge one with `git merge <branch>`, then clean up with /worktree remove <name>.");
        return ReplCommandResult.Continue(builder.ToString(), ReplFeedbackKind.Info);
    }

    private async Task<ReplCommandResult> RemoveAsync(
        string workspaceRoot,
        string name,
        CancellationToken cancellationToken)
    {
        if (string.Equals(name, "--all", StringComparison.OrdinalIgnoreCase))
        {
            IReadOnlyList<GitWorktreeInfo> worktrees = await _worktreeService.ListAsync(workspaceRoot, cancellationToken);
            foreach (GitWorktreeInfo worktree in worktrees)
            {
                await _worktreeService.RemoveAsync(workspaceRoot, worktree.Name, cancellationToken);
            }

            return ReplCommandResult.Continue(
                worktrees.Count == 0
                    ? "No StemCode worktrees to remove."
                    : $"Removed {worktrees.Count} worktree(s) and their branches.",
                ReplFeedbackKind.Info);
        }

        await _worktreeService.RemoveAsync(workspaceRoot, name, cancellationToken);
        return ReplCommandResult.Continue(
            $"Removed worktree '{name}' and its branch. Unmerged changes in it are gone.",
            ReplFeedbackKind.Info);
    }
}
