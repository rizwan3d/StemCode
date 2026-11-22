using Microsoft.Extensions.DependencyInjection;
using StemCode.Application.Abstractions;
using StemCode.Application.Models;
using StemCode.Application.Tools.Models;
using StemCode.Application.Tools.Serialization;
using System.Text;

namespace StemCode.Application.Tools;

internal sealed class AgentDelegateTool : ITool
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IAgentProfileResolver _profileResolver;
    private readonly ITokenEstimator _tokenEstimator;

    public AgentDelegateTool(
        IServiceProvider serviceProvider,
        IAgentProfileResolver profileResolver,
        ITokenEstimator tokenEstimator)
    {
        _serviceProvider = serviceProvider;
        _profileResolver = profileResolver;
        _tokenEstimator = tokenEstimator;
    }

    public string Description =>
        "Delegate a focused task to an available subagent and return its handoff response. " +
        "Set isolation to 'worktree' to have an editing subagent work in its own git worktree and branch instead of the shared workspace.";

    public string Name => AgentToolNames.AgentDelegate;

    public string PermissionRequirements => """
        {
          "approvalMode": "Automatic",
          "toolTags": ["agent"]
        }
        """;

    public string Schema => CreateSchema();

    private string CreateSchema()
    {
        IReadOnlyList<IAgentProfile> subagentProfiles = ListSubagentProfiles();
        string subagentDescription = subagentProfiles.Count == 0
            ? "Subagent to invoke."
            : $"Subagent to invoke. Available subagents: {FormatProfileSummaries(subagentProfiles)}.";
        string enumValues = string.Join(
            ", ",
            subagentProfiles.Select(static profile => $"\"{EscapeJsonString(profile.Name)}\""));

        return $$"""
        {
          "type": "object",
          "properties": {
            "agent": {
              "type": "string",
              "description": "{{EscapeJsonString(subagentDescription)}}",
              "enum": [{{enumValues}}]
            },
            "task": {
              "type": "string",
              "description": "Focused, self-contained task for the subagent."
            },
            "context": {
              "type": "string",
              "description": "Optional concise context, constraints, files, or expected output for the subagent."
            },
            "isolation": {
              "type": "string",
              "enum": ["none", "worktree"],
              "description": "{{EscapeJsonString(AgentDelegationSupport.IsolationSchemaDescription)}}"
            }
          },
          "required": ["agent", "task"],
          "additionalProperties": false
        }
        """;
    }

    public async Task<ToolResult> ExecuteAsync(
        ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (!ToolArguments.TryGetNonEmptyString(context.Arguments, "agent", out string? agentName))
        {
            IReadOnlyList<IAgentProfile> subagentProfiles = ListSubagentProfiles();
            return ToolResultFactory.InvalidArguments(
                "missing_agent",
                "Tool 'agent_delegate' requires a non-empty 'agent' string.",
                new ToolRenderPayload(
                    "Invalid agent_delegate arguments",
                    $"Provide 'agent' as one of: {FormatProfileNames(subagentProfiles)}."));
        }

        if (!ToolArguments.TryGetNonEmptyString(context.Arguments, "task", out string? task))
        {
            return ToolResultFactory.InvalidArguments(
                "missing_task",
                "Tool 'agent_delegate' requires a non-empty 'task' string.",
                new ToolRenderPayload(
                    "Invalid agent_delegate arguments",
                    "Provide a focused, non-empty delegated task."));
        }

        if (!AgentDelegationSupport.TryParseIsolation(
                ToolArguments.GetOptionalString(context.Arguments, "isolation"),
                out bool useWorktree))
        {
            return ToolResultFactory.InvalidArguments(
                "invalid_isolation",
                "Tool 'agent_delegate' isolation must be 'none' or 'worktree'.",
                new ToolRenderPayload(
                    "Invalid agent_delegate arguments",
                    "Use isolation 'none' or 'worktree'."));
        }

        IAgentProfile subagentProfile;
        try
        {
            subagentProfile = _profileResolver.Resolve(agentName);
        }
        catch (ArgumentException)
        {
            IReadOnlyList<IAgentProfile> subagentProfiles = ListSubagentProfiles();
            return ToolResultFactory.InvalidArguments(
                "unknown_subagent",
                $"Unknown subagent '{agentName}'. Available subagents: {FormatProfileNames(subagentProfiles)}.",
                new ToolRenderPayload(
                    $"Unknown subagent: {agentName}",
                    $"Use one of: {FormatProfileNames(subagentProfiles)}."));
        }

        if (subagentProfile.Mode != AgentProfileMode.Subagent)
        {
            IReadOnlyList<IAgentProfile> subagentProfiles = ListSubagentProfiles();
            return ToolResultFactory.InvalidArguments(
                "profile_is_not_subagent",
                $"Agent profile '{subagentProfile.Name}' is a primary profile and cannot be invoked through agent_delegate.",
                new ToolRenderPayload(
                    $"Not a subagent: {subagentProfile.Name}",
                    $"Use /profile to switch primary profiles, or delegate to one of: {FormatProfileNames(subagentProfiles)}."));
        }

        if (context.Session.AgentProfile.Mode != AgentProfileMode.Primary)
        {
            return ToolResultFactory.PermissionDenied(
                "subagent_cannot_delegate",
                $"Agent profile '{context.Session.AgentProfile.Name}' is not a primary profile and cannot delegate to another subagent.",
                new ToolRenderPayload(
                    "Subagent delegation blocked",
                    "Only primary profiles can invoke agent_delegate."));
        }

        if (context.Session.AgentProfile.PermissionIntent.EditMode == AgentProfileEditMode.ReadOnly &&
            subagentProfile.PermissionIntent.EditMode == AgentProfileEditMode.AllowEdits)
        {
            IReadOnlyList<IAgentProfile> readOnlySubagents = ListSubagentProfiles()
                .Where(static profile => profile.PermissionIntent.EditMode == AgentProfileEditMode.ReadOnly)
                .ToArray();
            return ToolResultFactory.PermissionDenied(
                "readonly_profile_cannot_delegate_edits",
                $"Agent profile '{context.Session.AgentProfile.Name}' is read-only and cannot delegate to editing subagent '{subagentProfile.Name}'.",
                new ToolRenderPayload(
                    "Editing subagent blocked",
                    $"Use a read-only subagent such as: {FormatProfileNames(readOnlySubagents)}."));
        }

        string? delegatedContext = ToolArguments.GetOptionalString(context.Arguments, "context");
        string delegatedInput = AgentDelegationSupport.CreateDelegatedInput(
            context.Session,
            task!,
            delegatedContext);

        AgentDelegationSupport.ChildTurn childTurn;
        try
        {
            childTurn = await AgentDelegationSupport.RunChildTurnAsync(
                _serviceProvider,
                context.Session,
                subagentProfile,
                delegatedInput,
                NoOpConversationProgressSink.Instance,
                useWorktree,
                cancellationToken);
        }
        catch (InvalidOperationException exception) when (useWorktree)
        {
            return ToolResultFactory.ExecutionError(
                "worktree_unavailable",
                exception.Message,
                new ToolRenderPayload(
                    "Worktree isolation failed",
                    exception.Message));
        }

        ConversationTurnResult turnResult = childTurn.Turn;

        // Edits made inside an isolated worktree stay there for review; they are
        // not added to the parent's undo history.
        bool recordedFileEdits = childTurn.Worktree is null &&
            AgentDelegationSupport.TryRecordChildFileEdits(
                childTurn.Session,
                context.Session,
                _serviceProvider.GetRequiredService<IWorkspaceFileService>(),
                subagentProfile.Name,
                task!);

        string[] executedTools = AgentDelegationSupport.GetExecutedTools(turnResult);
        int estimatedOutputTokens = AgentDelegationSupport.GetEstimatedOutputTokens(
            turnResult,
            _tokenEstimator);

        AgentDelegationResult result = new(
            subagentProfile.Name,
            task!,
            turnResult.ResponseText,
            executedTools,
            estimatedOutputTokens,
            recordedFileEdits,
            childTurn.Worktree);

        return ToolResultFactory.Success(
            $"Subagent '{subagentProfile.Name}' completed the delegated task.",
            result,
            ToolJsonContext.Default.AgentDelegationResult,
            new ToolRenderPayload(
                $"Subagent {subagentProfile.Name} completed",
                CreateRenderText(result)));
    }

    private static string CreateRenderText(AgentDelegationResult result)
    {
        string text = result.Response;

        if (result.ExecutedTools.Count > 0)
        {
            text += $"{Environment.NewLine}{Environment.NewLine}Tools: {string.Join(", ", result.ExecutedTools)}";
        }

        if (result.RecordedFileEdits)
        {
            text += $"{Environment.NewLine}File edits were recorded for undo.";
        }

        if (result.Worktree is not null)
        {
            text += $"{Environment.NewLine}{result.Worktree.Describe()}";
        }

        return text;
    }

    private IReadOnlyList<IAgentProfile> ListSubagentProfiles()
    {
        return _profileResolver
            .List()
            .Where(static profile => profile.Mode == AgentProfileMode.Subagent)
            .ToArray();
    }

    private static string FormatProfileNames(IEnumerable<IAgentProfile> profiles)
    {
        return string.Join(", ", profiles.Select(static profile => profile.Name));
    }

    private static string FormatProfileSummaries(IReadOnlyList<IAgentProfile> profiles)
    {
        return string.Join(
            "; ",
            profiles.Select(static profile => $"{profile.Name} - {profile.Description}"));
    }

    private static string EscapeJsonString(string value)
    {
        StringBuilder builder = new(value.Length);
        foreach (char character in value)
        {
            switch (character)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (char.IsControl(character))
                    {
                        builder.Append("\\u");
                        builder.Append(((int)character).ToString("x4"));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        return builder.ToString();
    }

    private sealed class NoOpConversationProgressSink : IConversationProgressSink
    {
        public static NoOpConversationProgressSink Instance { get; } = new();

        public Task ReportAssistantReasoningAsync(
            string reasoningText,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task ReportExecutionPlanAsync(
            ExecutionPlanProgress executionPlanProgress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(executionPlanProgress);
            return Task.CompletedTask;
        }

        public Task ReportToolCallsStartedAsync(
            IReadOnlyList<ConversationToolCall> toolCalls,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task ReportToolResultsAsync(
            ToolExecutionBatchResult toolExecutionResult,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(toolExecutionResult);
            return Task.CompletedTask;
        }
    }

}
