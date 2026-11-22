using Microsoft.Extensions.DependencyInjection;
using StemCode.Application.Abstractions;
using StemCode.Application.Models;
using StemCode.Application.Tools.Models;
using StemCode.Application.Tools.Serialization;
using System.Text;
using System.Text.Json;

namespace StemCode.Application.Tools;

internal sealed class AgentOrchestrateTool : ITool
{
    private const int MaxTasks = 6;
    private const int MaxParallelReadOnlyTasks = 4;
    private const string AutoStrategy = "auto";
    private const string ParallelReadOnlyStrategy = "parallel_readonly";
    private const string SequentialStrategy = "sequential";

    private readonly IServiceProvider _serviceProvider;
    private readonly IAgentProfileResolver _profileResolver;
    private readonly ITokenEstimator _tokenEstimator;
    private readonly ToolExecutionSettings _toolExecutionSettings;

    public AgentOrchestrateTool(
        IServiceProvider serviceProvider,
        IAgentProfileResolver profileResolver,
        ITokenEstimator tokenEstimator,
        ToolExecutionSettings toolExecutionSettings)
    {
        _serviceProvider = serviceProvider;
        _profileResolver = profileResolver;
        _tokenEstimator = tokenEstimator;
        _toolExecutionSettings = toolExecutionSettings;
    }

    public string Description =>
        "Coordinate several focused subagent tasks, running read-only work in parallel and editing-capable work in controlled sequence.";

    public string Name => AgentToolNames.AgentOrchestrate;

    public string PermissionRequirements => """
        {
          "approvalMode": "Automatic",
          "toolTags": ["agent"]
        }
        """;

    public string Schema => CreateSchema();

    public async Task<ToolResult> ExecuteAsync(
        ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (context.Session.AgentProfile.Mode != AgentProfileMode.Primary)
        {
            return ToolResultFactory.PermissionDenied(
                "subagent_cannot_orchestrate",
                $"Agent profile '{context.Session.AgentProfile.Name}' is not a primary profile and cannot orchestrate subagents.",
                new ToolRenderPayload(
                    "Subagent orchestration blocked",
                    "Only primary profiles can coordinate multiple subagents."));
        }

        if (!TryParseStrategy(context.Arguments, out string strategy, out ToolResult? strategyError))
        {
            return strategyError!;
        }

        if (!TryParseRequests(
                context,
                out IReadOnlyList<OrchestrationTaskRequest> requests,
                out ToolResult? requestError))
        {
            return requestError!;
        }

        if (string.Equals(strategy, ParallelReadOnlyStrategy, StringComparison.Ordinal) &&
            requests.Any(static request => request.RequiresExclusiveWorkspace))
        {
            return ToolResultFactory.InvalidArguments(
                "parallel_readonly_requires_readonly_subagents",
                "Strategy 'parallel_readonly' can only run read-only or worktree-isolated subagents.",
                new ToolRenderPayload(
                    "Invalid orchestration strategy",
                    "Use read-only subagents or isolation 'worktree' for parallel_readonly, or use auto/sequential for shared-workspace editing."));
        }

        using CancellationTokenSource? timeoutSource = CreateTimeoutSource(cancellationToken);
        CancellationToken executionCancellationToken = timeoutSource?.Token ?? cancellationToken;

        AgentOrchestrationTaskResult[] taskResults;
        try
        {
            taskResults = await ExecuteRequestsAsync(
                context.Session,
                requests,
                strategy,
                executionCancellationToken);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested &&
            timeoutSource is not null &&
            timeoutSource.IsCancellationRequested)
        {
            int timeoutSeconds = _toolExecutionSettings.AgentOrchestrationTimeoutSeconds;
            return ToolResultFactory.ExecutionError(
                "agent_orchestration_timeout",
                $"Subagent orchestration timed out after {timeoutSeconds} seconds.",
                new ToolRenderPayload(
                    "Subagent orchestration timed out",
                    $"The delegated tasks did not finish within {timeoutSeconds} seconds."));
        }

        AgentOrchestrationResult result = new(strategy, taskResults);
        string message = result.FailedTaskCount == 0
            ? $"Completed {result.SucceededTaskCount} delegated task(s)."
            : $"Completed {result.SucceededTaskCount} delegated task(s); {result.FailedTaskCount} failed.";
        ToolRenderPayload renderPayload = new(
            result.FailedTaskCount == 0
                ? "Subagent orchestration completed"
                : "Subagent orchestration had failures",
            CreateRenderText(result));

        return new ToolResult(
            result.FailedTaskCount == 0
                ? ToolResultStatus.Success
                : ToolResultStatus.ExecutionError,
            message,
            JsonSerializer.Serialize(result, ToolJsonContext.Default.AgentOrchestrationResult),
            renderPayload);
    }

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
            "strategy": {
              "type": "string",
              "description": "Execution strategy. auto runs consecutive read-only or worktree-isolated tasks in parallel and shared-workspace editing tasks one at a time. sequential runs every task in order. parallel_readonly runs all tasks together and requires read-only or worktree-isolated subagents.",
              "enum": ["auto", "sequential", "parallel_readonly"]
            },
            "tasks": {
              "type": "array",
              "description": "Focused subagent tasks to coordinate. Keep tasks independent and bounded.",
              "minItems": 1,
              "maxItems": {{MaxTasks}},
              "items": {
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
                  "writeScope": {
                    "type": "string",
                    "description": "Optional file, folder, or module ownership boundary for editing-capable subagents."
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
            }
          },
          "required": ["tasks"],
          "additionalProperties": false
        }
        """;
    }

    private async Task<AgentOrchestrationTaskResult[]> ExecuteRequestsAsync(
        ReplSessionContext parentSession,
        IReadOnlyList<OrchestrationTaskRequest> requests,
        string strategy,
        CancellationToken cancellationToken)
    {
        AgentOrchestrationTaskResult?[] results = new AgentOrchestrationTaskResult?[requests.Count];

        if (string.Equals(strategy, SequentialStrategy, StringComparison.Ordinal))
        {
            for (int index = 0; index < requests.Count; index++)
            {
                results[index] = await ExecuteRequestAsync(parentSession, requests[index], cancellationToken);
            }

            return CompleteResults(results);
        }

        if (string.Equals(strategy, ParallelReadOnlyStrategy, StringComparison.Ordinal))
        {
            await ExecuteParallelGroupAsync(parentSession, requests, 0, requests.Count, results, cancellationToken);
            return CompleteResults(results);
        }

        int cursor = 0;
        while (cursor < requests.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (requests[cursor].RequiresExclusiveWorkspace)
            {
                results[cursor] = await ExecuteRequestAsync(parentSession, requests[cursor], cancellationToken);
                cursor++;
                continue;
            }

            int groupStart = cursor;
            while (cursor < requests.Count && !requests[cursor].RequiresExclusiveWorkspace)
            {
                cursor++;
            }

            await ExecuteParallelGroupAsync(parentSession, requests, groupStart, cursor, results, cancellationToken);
        }

        return CompleteResults(results);
    }

    private async Task ExecuteParallelGroupAsync(
        ReplSessionContext parentSession,
        IReadOnlyList<OrchestrationTaskRequest> requests,
        int startIndex,
        int endIndex,
        AgentOrchestrationTaskResult?[] results,
        CancellationToken cancellationToken)
    {
        int groupCount = endIndex - startIndex;
        if (groupCount <= 1)
        {
            results[startIndex] = await ExecuteRequestAsync(parentSession, requests[startIndex], cancellationToken);
            return;
        }

        using SemaphoreSlim concurrency = new(MaxParallelReadOnlyTasks, MaxParallelReadOnlyTasks);
        Task<AgentOrchestrationTaskResult>[] tasks = requests
            .Skip(startIndex)
            .Take(groupCount)
            .Select(request => ExecuteParallelRequestAsync(parentSession, request, concurrency, cancellationToken))
            .ToArray();

        AgentOrchestrationTaskResult[] groupResults = await Task.WhenAll(tasks);
        foreach (AgentOrchestrationTaskResult result in groupResults)
        {
            results[result.Index] = result;
        }
    }

    private async Task<AgentOrchestrationTaskResult> ExecuteParallelRequestAsync(
        ReplSessionContext parentSession,
        OrchestrationTaskRequest request,
        SemaphoreSlim concurrency,
        CancellationToken cancellationToken)
    {
        await concurrency.WaitAsync(cancellationToken);
        try
        {
            return await ExecuteRequestAsync(parentSession, request, cancellationToken);
        }
        finally
        {
            concurrency.Release();
        }
    }

    private async Task<AgentOrchestrationTaskResult> ExecuteRequestAsync(
        ReplSessionContext parentSession,
        OrchestrationTaskRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            string input = AgentDelegationSupport.CreateDelegatedInput(
                parentSession,
                request.Task,
                request.Context,
                request.WriteScope,
                $"Task {request.Index + 1} of the current orchestration.");

            AgentDelegationSupport.ChildTurn childTurn = await AgentDelegationSupport.RunChildTurnAsync(
                _serviceProvider,
                parentSession,
                request.Profile,
                input,
                NoOpConversationProgressSink.Instance,
                request.UseWorktree,
                cancellationToken);
            ConversationTurnResult turnResult = childTurn.Turn;

            bool recordedFileEdits = childTurn.Worktree is null &&
                AgentDelegationSupport.TryRecordChildFileEdits(
                    childTurn.Session,
                    parentSession,
                    _serviceProvider.GetRequiredService<IWorkspaceFileService>(),
                    request.Profile.Name,
                    request.Task);

            return new AgentOrchestrationTaskResult(
                request.Index,
                request.Profile.Name,
                request.Task,
                succeeded: true,
                turnResult.ResponseText,
                AgentDelegationSupport.GetExecutedTools(turnResult),
                AgentDelegationSupport.GetEstimatedOutputTokens(turnResult, _tokenEstimator),
                recordedFileEdits,
                worktree: childTurn.Worktree);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new AgentOrchestrationTaskResult(
                request.Index,
                request.Profile.Name,
                request.Task,
                succeeded: false,
                response: string.Empty,
                executedTools: [],
                estimatedOutputTokens: 0,
                recordedFileEdits: false,
                errorMessage: exception.Message);
        }
    }

    private bool TryParseRequests(
        ToolExecutionContext context,
        out IReadOnlyList<OrchestrationTaskRequest> requests,
        out ToolResult? error)
    {
        requests = [];
        error = null;

        if (!context.Arguments.TryGetProperty("tasks", out JsonElement tasksElement) ||
            tasksElement.ValueKind != JsonValueKind.Array)
        {
            error = ToolResultFactory.InvalidArguments(
                "missing_tasks",
                "Tool 'agent_orchestrate' requires a non-empty 'tasks' array.",
                new ToolRenderPayload(
                    "Invalid agent_orchestrate arguments",
                    "Provide one to six delegated tasks."));
            return false;
        }

        int taskCount = tasksElement.GetArrayLength();
        if (taskCount == 0 || taskCount > MaxTasks)
        {
            error = ToolResultFactory.InvalidArguments(
                "invalid_task_count",
                $"Tool 'agent_orchestrate' requires between 1 and {MaxTasks} tasks.",
                new ToolRenderPayload(
                    "Invalid orchestration task count",
                    $"Provide between 1 and {MaxTasks} tasks."));
            return false;
        }

        List<OrchestrationTaskRequest> parsedRequests = [];
        int index = 0;
        foreach (JsonElement taskElement in tasksElement.EnumerateArray())
        {
            if (!TryParseRequest(context.Session, taskElement, index, out OrchestrationTaskRequest? request, out error))
            {
                return false;
            }

            parsedRequests.Add(request!);
            index++;
        }

        requests = parsedRequests;
        return true;
    }

    private bool TryParseRequest(
        ReplSessionContext parentSession,
        JsonElement taskElement,
        int index,
        out OrchestrationTaskRequest? request,
        out ToolResult? error)
    {
        request = null;
        error = null;

        if (taskElement.ValueKind != JsonValueKind.Object)
        {
            error = CreateTaskArgumentError(index, "Each orchestration task must be a JSON object.");
            return false;
        }

        if (!TryGetNonEmptyString(taskElement, "agent", out string? agentName))
        {
            error = CreateTaskArgumentError(index, "Each orchestration task requires a non-empty 'agent' string.");
            return false;
        }

        if (!TryGetNonEmptyString(taskElement, "task", out string? task))
        {
            error = CreateTaskArgumentError(index, "Each orchestration task requires a non-empty 'task' string.");
            return false;
        }

        IAgentProfile profile;
        try
        {
            profile = _profileResolver.Resolve(agentName);
        }
        catch (ArgumentException)
        {
            IReadOnlyList<IAgentProfile> subagentProfiles = ListSubagentProfiles();
            error = ToolResultFactory.InvalidArguments(
                "unknown_subagent",
                $"Unknown subagent '{agentName}'. Available subagents: {FormatProfileNames(subagentProfiles)}.",
                new ToolRenderPayload(
                    $"Unknown subagent: {agentName}",
                    $"Use one of: {FormatProfileNames(subagentProfiles)}."));
            return false;
        }

        if (profile.Mode != AgentProfileMode.Subagent)
        {
            IReadOnlyList<IAgentProfile> subagentProfiles = ListSubagentProfiles();
            error = ToolResultFactory.InvalidArguments(
                "profile_is_not_subagent",
                $"Agent profile '{profile.Name}' is a primary profile and cannot be used as an orchestration subagent.",
                new ToolRenderPayload(
                    $"Not a subagent: {profile.Name}",
                    $"Use /profile to switch primary profiles, or use one of: {FormatProfileNames(subagentProfiles)}."));
            return false;
        }

        if (parentSession.AgentProfile.PermissionIntent.EditMode == AgentProfileEditMode.ReadOnly &&
            profile.PermissionIntent.EditMode == AgentProfileEditMode.AllowEdits)
        {
            IReadOnlyList<IAgentProfile> readOnlySubagents = ListSubagentProfiles()
                .Where(static candidate => candidate.PermissionIntent.EditMode == AgentProfileEditMode.ReadOnly)
                .ToArray();
            error = ToolResultFactory.PermissionDenied(
                "readonly_profile_cannot_orchestrate_edits",
                $"Agent profile '{parentSession.AgentProfile.Name}' is read-only and cannot orchestrate editing subagent '{profile.Name}'.",
                new ToolRenderPayload(
                    "Editing subagent blocked",
                    $"Use read-only subagents such as: {FormatProfileNames(readOnlySubagents)}."));
            return false;
        }

        if (!AgentDelegationSupport.TryParseIsolation(
                GetOptionalString(taskElement, "isolation"),
                out bool useWorktree))
        {
            error = CreateTaskArgumentError(index, "'isolation' must be 'none' or 'worktree'.");
            return false;
        }

        request = new OrchestrationTaskRequest(
            index,
            profile,
            task!,
            GetOptionalString(taskElement, "context"),
            GetOptionalString(taskElement, "writeScope"),
            useWorktree);
        return true;
    }

    private static bool TryParseStrategy(
        JsonElement arguments,
        out string strategy,
        out ToolResult? error)
    {
        strategy = AutoStrategy;
        error = null;

        string? configuredStrategy = GetOptionalString(arguments, "strategy");
        if (string.IsNullOrWhiteSpace(configuredStrategy))
        {
            return true;
        }

        strategy = configuredStrategy.Trim();
        if (strategy is AutoStrategy or SequentialStrategy or ParallelReadOnlyStrategy)
        {
            return true;
        }

        error = ToolResultFactory.InvalidArguments(
            "invalid_strategy",
            "Strategy must be one of: auto, sequential, parallel_readonly.",
            new ToolRenderPayload(
                "Invalid orchestration strategy",
                "Use auto, sequential, or parallel_readonly."));
        return false;
    }

    private static ToolResult CreateTaskArgumentError(
        int index,
        string message)
    {
        return ToolResultFactory.InvalidArguments(
            "invalid_task",
            $"Task {index + 1}: {message}",
            new ToolRenderPayload(
                "Invalid orchestration task",
                $"Task {index + 1}: {message}"));
    }

    private static AgentOrchestrationTaskResult[] CompleteResults(
        IReadOnlyList<AgentOrchestrationTaskResult?> results)
    {
        return results
            .Select(static result => result ?? throw new InvalidOperationException("Orchestration completed without a task result."))
            .OrderBy(static result => result.Index)
            .ToArray();
    }

    private static string CreateRenderText(AgentOrchestrationResult result)
    {
        StringBuilder builder = new();
        builder.AppendLine($"Strategy: {result.Strategy}");
        builder.AppendLine($"Tasks: {result.SucceededTaskCount} succeeded, {result.FailedTaskCount} failed");

        foreach (AgentOrchestrationTaskResult task in result.Tasks)
        {
            builder.AppendLine();
            builder
                .Append(task.Index + 1)
                .Append(". ")
                .Append(task.AgentName)
                .Append(": ")
                .Append(task.Task)
                .Append(task.Succeeded ? " [ok]" : " [failed]");

            if (!string.IsNullOrWhiteSpace(task.Response))
            {
                builder.AppendLine();
                builder.Append(task.Response);
            }

            if (!string.IsNullOrWhiteSpace(task.ErrorMessage))
            {
                builder.AppendLine();
                builder.Append("Error: ").Append(task.ErrorMessage);
            }

            if (task.ExecutedTools.Count > 0)
            {
                builder.AppendLine();
                builder.Append("Tools: ").Append(string.Join(", ", task.ExecutedTools));
            }

            if (task.RecordedFileEdits)
            {
                builder.AppendLine();
                builder.Append("File edits were recorded for undo.");
            }

            if (task.Worktree is not null)
            {
                builder.AppendLine();
                builder.Append(task.Worktree.Describe());
            }
        }

        return builder.ToString().Trim();
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

    private static bool TryGetNonEmptyString(
        JsonElement element,
        string propertyName,
        out string? value)
    {
        value = GetOptionalString(element, propertyName);
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string? GetOptionalString(
        JsonElement element,
        string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? value = property.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
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

    private CancellationTokenSource? CreateTimeoutSource(CancellationToken cancellationToken)
    {
        if (_toolExecutionSettings.AgentOrchestrationTimeoutSeconds <= 0)
        {
            return null;
        }

        CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(_toolExecutionSettings.AgentOrchestrationTimeoutSeconds));
        return timeoutSource;
    }

    private sealed record OrchestrationTaskRequest(
        int Index,
        IAgentProfile Profile,
        string Task,
        string? Context,
        string? WriteScope,
        bool UseWorktree)
    {
        public bool IsEditingCapable => Profile.PermissionIntent.EditMode == AgentProfileEditMode.AllowEdits;

        /// <summary>
        /// Editing subagents that share the workspace must run alone; isolated
        /// worktree subagents can run alongside others.
        /// </summary>
        public bool RequiresExclusiveWorkspace => IsEditingCapable && !UseWorktree;
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
