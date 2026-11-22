using Microsoft.Extensions.DependencyInjection;
using StemCode.Application.Abstractions;
using StemCode.Application.Models;
using StemCode.Application.Tools.Models;

namespace StemCode.Application.Tools;

internal static class AgentDelegationSupport
{
    public const string NoIsolation = "none";
    public const string WorktreeIsolation = "worktree";

    public const string IsolationSchemaDescription =
        "Where the subagent works. 'none' (default) edits the shared workspace. 'worktree' runs it in a fresh git worktree on its own branch, created from HEAD (uncommitted changes in the workspace are not copied). The worktree is deleted if the subagent changes nothing, and kept for review and merge if it does. Isolated editing subagents can run in parallel.";

    private const int MaxTaskDescriptionLength = 80;

    internal sealed record ChildTurn(
        ReplSessionContext Session,
        ConversationTurnResult Turn,
        AgentWorktreeResult? Worktree);

    public static bool TryParseIsolation(
        string? value,
        out bool useWorktree)
    {
        useWorktree = false;
        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value.Trim(), NoIsolation, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(value.Trim(), WorktreeIsolation, StringComparison.OrdinalIgnoreCase))
        {
            useWorktree = true;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Runs one subagent turn. With <paramref name="useWorktree"/>, the child gets a
    /// fresh git worktree and every workspace-rooted service (file tools, shell,
    /// permissions) is scoped to it for the duration of the turn. The worktree is
    /// removed afterwards if nothing changed.
    /// </summary>
    public static async Task<ChildTurn> RunChildTurnAsync(
        IServiceProvider serviceProvider,
        ReplSessionContext parentSession,
        IAgentProfile subagentProfile,
        string delegatedInput,
        IConversationProgressSink progressSink,
        bool useWorktree,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(parentSession);
        ArgumentNullException.ThrowIfNull(subagentProfile);
        ArgumentNullException.ThrowIfNull(progressSink);

        IConversationPipeline conversationPipeline = serviceProvider.GetRequiredService<IConversationPipeline>();

        if (!useWorktree)
        {
            ReplSessionContext sharedSession = CreateChildSession(parentSession, subagentProfile);
            ConversationTurnResult sharedTurn = await conversationPipeline.ProcessAsync(
                delegatedInput,
                sharedSession,
                progressSink,
                cancellationToken);
            return new ChildTurn(sharedSession, sharedTurn, null);
        }

        IGitWorktreeService worktreeService = serviceProvider.GetService<IGitWorktreeService>()
            ?? throw new InvalidOperationException("Worktree isolation is not available in this host.");
        string parentRoot = serviceProvider.GetService<IWorkspaceRootProvider>()?.GetWorkspaceRoot()
            ?? parentSession.WorkspacePath;

        GitWorktreeHandle worktree = await worktreeService.CreateAsync(parentRoot, name: null, cancellationToken);
        ReplSessionContext childSession;
        ConversationTurnResult turn;
        try
        {
            childSession = CreateChildSession(parentSession, subagentProfile, worktree.WorkspaceRoot);
            string isolatedInput =
                delegatedInput +
                $"{Environment.NewLine}{Environment.NewLine}Isolation:{Environment.NewLine}" +
                $"You are working in an isolated git worktree on branch '{worktree.Branch}', created from the last commit. " +
                "Uncommitted changes in the parent workspace are not present here. Your edits stay in this worktree " +
                "and are handed back to the parent agent for review; mention every file you changed in your handoff.";

            using (WorkspaceRootScope.Enter(worktree.WorkspaceRoot))
            {
                turn = await conversationPipeline.ProcessAsync(
                    isolatedInput,
                    childSession,
                    progressSink,
                    cancellationToken);
            }
        }
        catch
        {
            await TryFinishWorktreeAsync(worktreeService, worktree);
            throw;
        }

        GitWorktreeOutcome outcome = await worktreeService.FinishAsync(worktree, CancellationToken.None);
        return new ChildTurn(childSession, turn, ToResult(outcome, parentRoot));
    }

    private static async Task TryFinishWorktreeAsync(
        IGitWorktreeService worktreeService,
        GitWorktreeHandle worktree)
    {
        try
        {
            _ = await worktreeService.FinishAsync(worktree, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Cleanup is best effort; the original failure is what the caller needs.
        }
    }

    private static AgentWorktreeResult ToResult(
        GitWorktreeOutcome outcome,
        string parentRoot)
    {
        string path = outcome.Worktree.WorktreePath;
        string relative = Path.GetRelativePath(parentRoot, path);
        if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
        {
            path = relative.Replace('\\', '/');
        }

        return new AgentWorktreeResult(
            outcome.Worktree.Name,
            path,
            outcome.Worktree.Branch,
            outcome.Kept,
            outcome.ChangedFiles,
            outcome.CommitCount);
    }

    public static ReplSessionContext CreateChildSession(
        ReplSessionContext parentSession,
        IAgentProfile subagentProfile,
        string? workspacePath = null)
    {
        ArgumentNullException.ThrowIfNull(parentSession);
        ArgumentNullException.ThrowIfNull(subagentProfile);

        ReplSessionContext childSession = new(
            parentSession.ApplicationName,
            parentSession.ProviderProfile,
            parentSession.ActiveModelId,
            parentSession.AvailableModelIds,
            agentProfile: subagentProfile,
            reasoningEffort: parentSession.ReasoningEffort,
            thinkingMode: parentSession.ThinkingMode,
            workspacePath: workspacePath ?? parentSession.WorkspacePath,
            modelContextWindowTokens: parentSession.ModelContextWindowTokens,
            modelContextMetadata: parentSession.ModelContextMetadata,
            activeProviderName: parentSession.ActiveProviderName);

        _ = childSession.TrySetWorkingDirectory(parentSession.WorkingDirectory, out _);

        foreach (PermissionRule rule in parentSession.PermissionOverrides)
        {
            childSession.AddPermissionOverride(rule);
        }

        return childSession;
    }

    public static string CreateDelegatedInput(
        ReplSessionContext parentSession,
        string task,
        string? context = null,
        string? writeScope = null,
        string? coordinationContext = null)
    {
        ArgumentNullException.ThrowIfNull(parentSession);
        ArgumentException.ThrowIfNullOrWhiteSpace(task);

        string instructions =
            $"Delegated task from parent agent '{parentSession.AgentProfile.Name}'.{Environment.NewLine}{Environment.NewLine}" +
            "Coordination rules:" + Environment.NewLine +
            "- Work only on the delegated task." + Environment.NewLine +
            "- You may be one of several delegated agents; keep your handoff useful for the parent agent to integrate." + Environment.NewLine +
            "- Do not revert unrelated changes or changes made by another agent." + Environment.NewLine +
            "- If a write scope is provided, keep file changes inside that scope." + Environment.NewLine + Environment.NewLine +
            $"Task:{Environment.NewLine}{task.Trim()}{Environment.NewLine}{Environment.NewLine}" +
            "Return your final response as a concise handoff to the parent agent.";

        List<string> sections = [instructions];

        if (!string.IsNullOrWhiteSpace(writeScope))
        {
            sections.Add($"Write scope:{Environment.NewLine}{writeScope.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(coordinationContext))
        {
            sections.Add($"Orchestration context:{Environment.NewLine}{coordinationContext.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(context))
        {
            sections.Add($"Additional context:{Environment.NewLine}{context.Trim()}");
        }

        return string.Join(
            $"{Environment.NewLine}{Environment.NewLine}",
            sections);
    }

    public static bool TryRecordChildFileEdits(
        ReplSessionContext childSession,
        ReplSessionContext parentSession,
        IWorkspaceFileService workspaceFileService,
        string subagentName,
        string task)
    {
        ArgumentNullException.ThrowIfNull(childSession);
        ArgumentNullException.ThrowIfNull(parentSession);
        ArgumentNullException.ThrowIfNull(workspaceFileService);
        ArgumentException.ThrowIfNullOrWhiteSpace(subagentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(task);

        if (!childSession.TryCreateFileEditTransactionSnapshot(
                $"subagent {subagentName}: {Truncate(task, MaxTaskDescriptionLength)}",
                out WorkspaceFileEditTransaction? transaction) ||
            transaction is null)
        {
            return false;
        }

        workspaceFileService.RetainTrackedFileEditTransaction(transaction);
        parentSession.RecordFileEditTransaction(transaction);
        return true;
    }

    public static string[] GetExecutedTools(ConversationTurnResult turnResult)
    {
        ArgumentNullException.ThrowIfNull(turnResult);

        return turnResult.ToolExecutionResult?.Results
            .Select(static result => result.ToolName)
            .Where(static tool => !string.IsNullOrWhiteSpace(tool))
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];
    }

    public static int GetEstimatedOutputTokens(
        ConversationTurnResult turnResult,
        ITokenEstimator tokenEstimator)
    {
        ArgumentNullException.ThrowIfNull(turnResult);
        ArgumentNullException.ThrowIfNull(tokenEstimator);

        return turnResult.Metrics?.EstimatedOutputTokens ??
            tokenEstimator.Estimate(turnResult.ResponseText);
    }

    private static string Truncate(
        string value,
        int maxLength)
    {
        string normalized = value.Trim();
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return normalized[..Math.Max(0, maxLength - 3)] + "...";
    }
}
