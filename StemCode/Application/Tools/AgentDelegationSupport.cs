using StemCode.Application.Abstractions;
using StemCode.Application.Models;

namespace StemCode.Application.Tools;

internal static class AgentDelegationSupport
{
    private const int MaxTaskDescriptionLength = 80;

    public static ReplSessionContext CreateChildSession(
        ReplSessionContext parentSession,
        IAgentProfile subagentProfile)
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
            workspacePath: parentSession.WorkspacePath,
            modelContextWindowTokens: parentSession.ModelContextWindowTokens,
            modelContextMetadata: parentSession.ModelContextMetadata,
            activeProviderName: parentSession.ActiveProviderName);

        if (parentSession.ContextWindowOverrideTokens is > 0)
        {
            childSession.SetContextWindowOverride(parentSession.ContextWindowOverrideTokens);
        }

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
