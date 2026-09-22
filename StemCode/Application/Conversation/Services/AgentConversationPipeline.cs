using Microsoft.Extensions.Logging;
using StemCode.Application.Abstractions;
using StemCode.Application.Conversation.Serialization;
using StemCode.Application.Exceptions;
using StemCode.Application.Formatting;
using StemCode.Application.Logging;
using StemCode.Application.Models;
using StemCode.Application.Planning;
using StemCode.Application.Tools;
using StemCode.Application.Tools.Models;
using StemCode.Application.Tools.Serialization;
using StemCode.Domain.Models;
using System.Buffers;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace StemCode.Application.Conversation.Services;

internal sealed class AgentConversationPipeline : IConversationPipeline
{
    private const int RetryableProviderOutputRetryLimit = 3;
    private const int IncompletePlanFinalResponseRetryLimit = 1;
    private const int DefaultModelContextWindowTokens = 128_000;
    private const int MinimumUsableInputTokens = 2_048;
    private const int MinimumRecentConversationTokens = 2_000;
    private const string EmptyResponseRetryInstruction =
        """
        The previous provider response was empty even though it ended normally. This output was rejected by the runtime and was not saved.
        Continue the same task from the current conversation state and return either:
        - a valid call to an available tool if any work remains, or
        - a non-empty assistant message that actually completes or materially advances the work
        Do not return empty content, whitespace, or another tool-less empty response.
        """;
    private const string ProviderRecoveryRetryInstruction =
        """
        This is a recovery request for the same user turn.
        Continue the same task and return either:
        - a non-empty assistant message that materially advances the work, or
        - a valid call to an available tool
        """;
    private const string RawToolCallRetryInstruction =
        """
        The previous provider response exposed raw tool-call protocol text in assistant content instead of returning a structured tool call.
        Continue the same task and return either:
        - a normal assistant message with no tool-call protocol markers, or
        - a valid structured tool call to one of the available tools
        Do not write markers such as <|channel>call:, <tool_call|>, assistant/tool protocol text, or tool-call JSON inside assistant content.
        """;
    private const string IncompletePlanRetryInstruction =
        """
        The previous provider response tried to finish while the live update_plan still had in_progress or pending work.
        Continue the same task now by calling the appropriate available tools, or call update_plan to revise/complete the live plan if it no longer reflects the work.
        If the listed work is truly complete, call update_plan with every item completed before returning final text.
        Do not repeat the final answer until the live plan has no in_progress or pending work.
        """;
    private const int StagnantActionReassessmentThreshold = 2;
    private const string StagnantActionReassessmentInstruction =
        """
        Loop/stagnation guard:
        The last tool action repeated without changing files, arguments, hypotheses, or observable results.
        Stop repeating that action. Reassess the task before making another tool call:
        - state what the repeated result proves or rules out,
        - choose a materially different next action, argument, or hypothesis, or
        - provide the final answer if the available evidence is sufficient.
        """;
    private const string InterruptedTurnRecoveryMessage =
        """
        Recovery context:
        The previous task was interrupted before the assistant completed its response.
        Continue from the preserved task and available tool progress.
        """;

    private static readonly ConversationJsonContext RelaxedConversationJsonContext = new(
    new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });
    private static readonly HashSet<string> ToolOutputsProtectedFromPruning = new(StringComparer.Ordinal)
    {
        AgentToolNames.SkillLoad
    };

    private sealed class PreparedTurnContext : IDisposable
    {
        public PreparedTurnContext(
            string normalizedInput,
            IReadOnlyList<ConversationAttachment> attachments,
            string apiKey,
            ConversationSettings settings,
            string? baseSystemPrompt,
            string? profileSystemPrompt,
            IReadOnlyList<ToolDefinition> availableToolDefinitions,
            IReadOnlySet<string> availableToolNames,
            CancellationTokenSource timeoutSource,
            DateTimeOffset startedAt)
        {
            NormalizedInput = normalizedInput;
            Attachments = attachments;
            ApiKey = apiKey;
            Settings = settings;
            BaseSystemPrompt = baseSystemPrompt;
            ProfileSystemPrompt = profileSystemPrompt;
            AvailableToolDefinitions = availableToolDefinitions;
            AvailableToolNames = availableToolNames;
            TimeoutSource = timeoutSource;
            StartedAt = startedAt;
        }

        public string NormalizedInput { get; }

        public IReadOnlyList<ConversationAttachment> Attachments { get; }

        public string ApiKey { get; }

        public ConversationSettings Settings { get; }

        public string? BaseSystemPrompt { get; }

        public string? ProfileSystemPrompt { get; }

        public IReadOnlyList<ToolDefinition> AvailableToolDefinitions { get; }

        public IReadOnlySet<string> AvailableToolNames { get; }

        public CancellationTokenSource TimeoutSource { get; }

        public DateTimeOffset StartedAt { get; }

        public void Dispose()
        {
            TimeoutSource.Dispose();
        }
    }

    private sealed class PreparedConversationRequest
    {
        public PreparedConversationRequest(
            string? systemPrompt,
            IReadOnlyList<ConversationRequestMessage> messages)
        {
            SystemPrompt = systemPrompt;
            Messages = messages;
        }

        public IReadOnlyList<ConversationRequestMessage> Messages { get; }

        public string? SystemPrompt { get; }
    }

    private sealed class ContextBudget
    {
        public ContextBudget(
            int contextWindowTokens,
            int usableInputTokens,
            int recentConversationTargetTokens,
            int autoCompactTokenLimit,
            int toolResultTokenLimit,
            ToolResultTruncationPolicy? toolResultTruncationPolicy)
        {
            ContextWindowTokens = contextWindowTokens;
            UsableInputTokens = usableInputTokens;
            RecentConversationTargetTokens = recentConversationTargetTokens;
            AutoCompactTokenLimit = autoCompactTokenLimit;
            ToolResultTokenLimit = toolResultTokenLimit;
            ToolResultTruncationPolicy = toolResultTruncationPolicy;
        }

        public int AutoCompactTokenLimit { get; }

        public int ContextWindowTokens { get; }

        public int RecentConversationTargetTokens { get; }

        public ToolResultTruncationPolicy? ToolResultTruncationPolicy { get; }

        public int ToolResultTokenLimit { get; }

        public int UsableInputTokens { get; }
    }

    private readonly TimeProvider _timeProvider;
    private readonly ITokenEstimator _tokenEstimator;
    private readonly IApiKeySecretStore _secretStore;
    private readonly IConversationProviderClient _providerClient;
    private readonly IConversationResponseMapper _responseMapper;
    private readonly ILifecycleHookService _lifecycleHookService;
    private readonly IToolExecutionPipeline _toolExecutionPipeline;
    private readonly IToolRegistry _toolRegistry;
    private readonly IConversationConfigurationAccessor _configurationAccessor;
    private readonly IBudgetControlsUsageService _budgetControlsUsageService;
    private readonly IWorkspaceSystemPromptProvider _workspaceSystemPromptProvider;
    private readonly IWorkspaceAgentProfilePromptProvider _workspaceAgentProfilePromptProvider;
    private readonly IWorkspaceInstructionsProvider _workspaceInstructionsProvider;
    private readonly ILessonMemoryService _lessonMemoryService;
    private readonly ISkillService _skillService;
    private readonly IToolOutputFormatter _toolOutputFormatter;
    private readonly IReplSectionService? _sectionService;
    private readonly ICodebaseIndexService? _codebaseIndexService;
    private readonly ISessionEventLogService? _sessionEventLogService;
    private readonly CodebaseIndexSettings _codebaseIndexSettings;
    private readonly ILogger<AgentConversationPipeline> _logger;

    public AgentConversationPipeline(
        TimeProvider timeProvider,
        ITokenEstimator tokenEstimator,
        IApiKeySecretStore secretStore,
        IConversationProviderClient providerClient,
        IConversationResponseMapper responseMapper,
        IToolExecutionPipeline toolExecutionPipeline,
        IToolRegistry toolRegistry,
        IConversationConfigurationAccessor configurationAccessor,
        IWorkspaceSystemPromptProvider workspaceSystemPromptProvider,
        IWorkspaceInstructionsProvider workspaceInstructionsProvider,
        ILessonMemoryService lessonMemoryService,
        ILogger<AgentConversationPipeline> logger,
        ILifecycleHookService? lifecycleHookService = null,
        ISkillService? skillService = null,
        IToolOutputFormatter? toolOutputFormatter = null,
        IBudgetControlsUsageService? budgetControlsUsageService = null,
        IWorkspaceAgentProfilePromptProvider? workspaceAgentProfilePromptProvider = null,
        IReplSectionService? sectionService = null,
        ICodebaseIndexService? codebaseIndexService = null,
        CodebaseIndexSettings? codebaseIndexSettings = null,
        ISessionEventLogService? sessionEventLogService = null)
    {
        _timeProvider = timeProvider;
        _tokenEstimator = tokenEstimator;
        _secretStore = secretStore;
        _providerClient = providerClient;
        _responseMapper = responseMapper;
        _lifecycleHookService = lifecycleHookService ?? DisabledLifecycleHookService.Instance;
        _toolExecutionPipeline = toolExecutionPipeline;
        _toolRegistry = toolRegistry;
        _configurationAccessor = configurationAccessor;
        _budgetControlsUsageService = budgetControlsUsageService ?? DisabledBudgetControlsUsageService.Instance;
        _workspaceSystemPromptProvider = workspaceSystemPromptProvider;
        _workspaceAgentProfilePromptProvider =
            workspaceAgentProfilePromptProvider ?? DisabledWorkspaceAgentProfilePromptProvider.Instance;
        _workspaceInstructionsProvider = workspaceInstructionsProvider;
        _lessonMemoryService = lessonMemoryService;
        _skillService = skillService ?? DisabledSkillService.Instance;
        _toolOutputFormatter = toolOutputFormatter ?? new ToolOutputFormatter();
        _sectionService = sectionService;
        _codebaseIndexService = codebaseIndexService;
        _codebaseIndexSettings = codebaseIndexSettings ?? new CodebaseIndexSettings();
        _sessionEventLogService = sessionEventLogService;
        _logger = logger;
    }

    public async Task<ConversationTurnResult> ProcessAsync(
        string input,
        ReplSessionContext session,
        IConversationProgressSink progressSink,
        CancellationToken cancellationToken)
    {
        return await ProcessAsync(
            input,
            session,
            progressSink,
            [],
            cancellationToken);
    }

    public async Task<ConversationTurnResult> ProcessAsync(
        string input,
        ReplSessionContext session,
        IConversationProgressSink progressSink,
        IReadOnlyList<ConversationAttachment> attachments,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(progressSink);
        ArgumentNullException.ThrowIfNull(attachments);
        cancellationToken.ThrowIfCancellationRequested();

        string normalizedInput = input.Trim();
        IReadOnlyList<ConversationAttachment> normalizedAttachments = attachments
            .Where(static attachment => attachment is not null)
            .ToArray();
        await RunBeforeTaskStartHookAsync(normalizedInput, session, cancellationToken);

        ConversationSectionTurn pendingTurn = session.CreatePendingConversationTurn(
            normalizedInput,
            normalizedAttachments);
        int turnIndex = session.ConversationTurns.Count;
        await RecordTurnStartedAsync(
            session,
            pendingTurn.TurnId,
            turnIndex,
            normalizedInput,
            cancellationToken);
        await PersistSessionStateAsync(session, cancellationToken);

        try
        {
            using PreparedTurnContext preparedTurn = await PrepareTurnAsync(
                normalizedInput,
                normalizedAttachments,
                session,
                cancellationToken);

            if (session.PendingExecutionPlan is not null &&
                PlanningModePolicy.IsExecutionApproval(normalizedInput))
            {
                CompletedAssistantTurn approvedTurn = await ExecuteApprovedPlanAsync(
                    preparedTurn.NormalizedInput,
                    session,
                    progressSink,
                    preparedTurn.Settings,
                    preparedTurn.BaseSystemPrompt,
                    preparedTurn.ApiKey,
                    preparedTurn.AvailableToolDefinitions,
                    preparedTurn.AvailableToolNames,
                    preparedTurn.TimeoutSource,
                    preparedTurn.StartedAt,
                    preparedTurn.Attachments,
                    cancellationToken);

                ConversationTurnResult approvedResult = await FinalizeCompletedTurnAsync(
                    pendingTurn.TurnId,
                    preparedTurn.NormalizedInput,
                    session,
                    approvedTurn,
                    cancellationToken);
                await RecordTurnEndedAsync(
                    session,
                    pendingTurn.TurnId,
                    turnIndex,
                    "completed",
                    cancellationToken);
                return approvedResult;
            }

            List<ConversationRequestMessage> messages =
            [
                .. BuildInitialConversationMessages(session)
            ];

            ApplicationLogMessages.ConversationRequestStarted(
                _logger,
                session.ProviderName,
                session.ActiveModelId);

            PhaseExecutionResult result = await RunPhaseAsync(
                preparedTurn.ApiKey,
                pendingTurn.TurnId,
                session,
                messages,
                PlanningModePolicy.CreateToolDrivenConversationSystemPrompt(preparedTurn.ProfileSystemPrompt),
                preparedTurn.AvailableToolDefinitions,
                preparedTurn.AvailableToolNames,
                ConversationExecutionPhase.Execution,
                progressSink,
                preparedTurn.Settings,
                preparedTurn.TimeoutSource,
                executionPlanTracker: null,
                cancellationToken);

            CompletedAssistantTurn completedTurn = CreateCompletedAssistantTurn(
                preparedTurn.NormalizedInput,
                result,
                preparedTurn.StartedAt,
                session,
                session.ShowThinking);

            ConversationTurnResult finalResult = await FinalizeCompletedTurnAsync(
                pendingTurn.TurnId,
                preparedTurn.NormalizedInput,
                session,
                completedTurn,
                cancellationToken);
            await RecordTurnEndedAsync(
                session,
                pendingTurn.TurnId,
                turnIndex,
                "completed",
                cancellationToken);
            return finalResult;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            session.TryCancelConversationTurn(pendingTurn.TurnId);
            await PersistSessionStateIgnoringErrorsAsync(session, cancellationToken);
            await RecordTurnEndedIgnoringErrorsAsync(session, pendingTurn.TurnId, turnIndex, "cancelled");
            throw;
        }
        catch (OperationCanceledException)
        {
            session.TryInterruptConversationTurn(
                pendingTurn.TurnId,
                failureInfo: CreateFailureInfo(
                    session,
                    new ConversationProviderException("The conversation request was cancelled.")));
            await PersistSessionStateIgnoringErrorsAsync(session, cancellationToken);
            await RecordTurnEndedIgnoringErrorsAsync(session, pendingTurn.TurnId, turnIndex, "interrupted");
            throw;
        }
        catch (Exception exception)
        {
            session.TryInterruptConversationTurn(
                pendingTurn.TurnId,
                failureInfo: CreateFailureInfo(session, exception));
            await PersistSessionStateIgnoringErrorsAsync(session, cancellationToken);
            await RunAfterTaskFailedHookAsync(normalizedInput, session, exception, cancellationToken);
            await RecordTurnEndedIgnoringErrorsAsync(session, pendingTurn.TurnId, turnIndex, "failed");
            throw;
        }
        finally
        {
            session.DeleteTemporaryArtifacts(TemporaryArtifactRetention.Turn);
        }
    }

    private async Task<PreparedTurnContext> PrepareTurnAsync(
        string normalizedInput,
        IReadOnlyList<ConversationAttachment> normalizedAttachments,
        ReplSessionContext session,
        CancellationToken cancellationToken)
    {
        string apiKey = await LoadProviderSecretAsync(session, cancellationToken)
            ?? throw new ConversationPipelineException(
                "Conversation cannot start because the API key is missing.");

        ConversationSettings settings = _configurationAccessor.GetSettings();
        string? baseSystemPrompt = await CreateBaseSystemPromptAsync(
            settings.SystemPrompt,
            session,
            cancellationToken);
        string? profileSystemPrompt = await CreateProfileSystemPromptAsync(
            baseSystemPrompt,
            normalizedInput,
            session,
            cancellationToken);
        IReadOnlyList<ToolDefinition> availableToolDefinitions = GetProfileToolDefinitions(session);
        IReadOnlySet<string> availableToolNames = availableToolDefinitions
            .Select(static definition => definition.Name)
            .ToHashSet(StringComparer.Ordinal);
        CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(settings.RequestTimeout);

        return new PreparedTurnContext(
            normalizedInput,
            normalizedAttachments,
            apiKey,
            settings,
            baseSystemPrompt,
            profileSystemPrompt,
            availableToolDefinitions,
            availableToolNames,
            timeoutSource,
            _timeProvider.GetUtcNow());
    }

    private async Task<ConversationTurnResult> FinalizeCompletedTurnAsync(
        string turnId,
        string input,
        ReplSessionContext session,
        CompletedAssistantTurn completedTurn,
        CancellationToken cancellationToken)
    {
        await RunAfterTaskCompleteHookAsync(input, session, completedTurn.Result, cancellationToken);
        CommitCompletedTurn(turnId, session, completedTurn);
        await PersistSessionStateAsync(session, cancellationToken);
        await MaybeUpdateCodebaseIndexAsync(cancellationToken);
        return completedTurn.Result;
    }

    private async Task<CompletedAssistantTurn> ExecuteApprovedPlanAsync(
        string normalizedInput,
        ReplSessionContext session,
        IConversationProgressSink progressSink,
        ConversationSettings settings,
        string? baseSystemPrompt,
        string apiKey,
        IReadOnlyList<ToolDefinition> allToolDefinitions,
        IReadOnlySet<string> executionToolNames,
        CancellationTokenSource timeoutSource,
        DateTimeOffset startedAt,
        IReadOnlyList<ConversationAttachment> attachments,
        CancellationToken cancellationToken)
    {
        PendingExecutionPlan pendingPlan = session.PendingExecutionPlan
            ?? throw new InvalidOperationException("A pending execution plan is required.");

        ExecutionPlanTracker? executionPlanTracker = ExecutionPlanTracker.Create(
            pendingPlan.Tasks);
        if (executionPlanTracker is not null)
        {
            await progressSink.ReportExecutionPlanAsync(
                executionPlanTracker.CreateSnapshot(),
                cancellationToken);
        }

        List<ConversationRequestMessage> executionMessages =
        [
            .. BuildInitialConversationMessages(session)
        ];

        PhaseExecutionResult executionResult = await RunPhaseAsync(
            apiKey,
            session.ConversationTurns.Last().TurnId,
            session,
            executionMessages,
            PlanningModePolicy.CreateExecutionSystemPrompt(
                await CreateProfileSystemPromptAsync(
                    baseSystemPrompt,
                    pendingPlan.PlanningSummary,
                    session,
                    cancellationToken),
                pendingPlan.PlanningSummary),
            allToolDefinitions,
            executionToolNames,
            ConversationExecutionPhase.Execution,
            progressSink,
            settings,
            timeoutSource,
            executionPlanTracker,
            cancellationToken);

        if (executionPlanTracker is not null)
        {
            await progressSink.ReportExecutionPlanAsync(
                executionPlanTracker.Complete(),
                cancellationToken);
        }

        return CreateCompletedAssistantTurn(
            normalizedInput,
            executionResult,
            startedAt,
            session,
            session.ShowThinking);
    }

    private CompletedAssistantTurn CreateCompletedAssistantTurn(
        string userInput,
        PhaseExecutionResult phaseResult,
        DateTimeOffset startedAt,
        ReplSessionContext session,
        bool showThinking)
    {
        ToolExecutionBatchResult? batchResult = CreateBatchResult(phaseResult.ExecutedToolResults);
        ConversationTurnResult result = ConversationTurnResult.AssistantMessage(
            phaseResult.AssistantMessage,
            batchResult,
            CreateMetrics(
                startedAt,
                phaseResult,
                session),
            showThinking
                ? phaseResult.AssistantReasoningContent ??
                    ExtractReasoningDetailsText(phaseResult.AssistantReasoningDetailsJson)
                : null);

        return new CompletedAssistantTurn(
            userInput,
            phaseResult.AssistantMessage,
            phaseResult.ToolCalls,
            batchResult,
            phaseResult.AssistantReasoningContent,
            phaseResult.AssistantReasoningDetailsJson,
            result);
    }

    private void CommitCompletedTurn(
        string turnId,
        ReplSessionContext session,
        CompletedAssistantTurn completedTurn)
    {
        ApplicationLogMessages.ConversationAssistantMessageReceived(_logger);
        session.ClearPendingExecutionPlan();
        session.TryCompleteConversationTurn(
            turnId,
            completedTurn.AssistantResponse,
            completedTurn.ToolCalls,
            CreateToolOutputMessages(completedTurn.BatchResult, session),
            completedTurn.AssistantReasoningContent,
            completedTurn.AssistantReasoningDetailsJson);
    }

    private ConversationTurnMetrics CreateMetrics(
        DateTimeOffset startedAt,
        PhaseExecutionResult phaseResult,
        ReplSessionContext session)
    {
        TimeSpan elapsed = _timeProvider.GetUtcNow() - startedAt;
        int estimatedOutputTokens = GetCompletionTokens(phaseResult) is > 0
            ? phaseResult.TotalCompletionTokens
            : _tokenEstimator.Estimate(phaseResult.AssistantMessage);
        ConversationTurnMetrics metrics = new(
            elapsed,
            estimatedOutputTokens,
            estimatedInputTokens: phaseResult.EstimatedInputTokens,
            cachedInputTokens: phaseResult.CachedInputTokens,
            providerRetryCount: phaseResult.ProviderRetryCount,
            toolRoundCount: phaseResult.ToolRoundCount,
            providerName: session.ProviderName,
            modelId: session.ActiveModelId);

        ApplicationLogMessages.ConversationTurnMetricsRecorded(
            _logger,
            Math.Max(0, (long)Math.Round(elapsed.TotalMilliseconds, MidpointRounding.AwayFromZero)),
            metrics.EstimatedInputTokens,
            metrics.EstimatedOutputTokens,
            metrics.EstimatedTotalTokens,
            metrics.ProviderRetryCount,
            metrics.ToolRoundCount);

        return metrics;
    }

    private async Task RunBeforeTaskStartHookAsync(
        string input,
        ReplSessionContext session,
        CancellationToken cancellationToken)
    {
        LifecycleHookRunResult result = await _lifecycleHookService.RunAsync(
            CreateTaskHookContext(LifecycleHookEvents.BeforeTaskStart, input, session),
            cancellationToken);
        if (!result.IsAllowed)
        {
            throw new ConversationPipelineException(
                result.Message ?? $"Lifecycle hook '{result.FailedHookName}' blocked the task.");
        }
    }

    private async Task RunAfterTaskCompleteHookAsync(
        string input,
        ReplSessionContext session,
        ConversationTurnResult result,
        CancellationToken cancellationToken)
    {
        LifecycleHookRunResult hookResult;
        try
        {
            hookResult = await _lifecycleHookService.RunAsync(
                CreateTaskHookContext(LifecycleHookEvents.AfterTaskComplete, input, session, result),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // A broken hook implementation should not turn a completed assistant turn into a failed turn.
            return;
        }

        if (!hookResult.IsAllowed)
        {
            throw new ConversationPipelineException(
                hookResult.Message ?? $"Lifecycle hook '{hookResult.FailedHookName}' rejected the completed task.");
        }
    }

    private async Task RunAfterTaskFailedHookAsync(
        string input,
        ReplSessionContext session,
        Exception exception,
        CancellationToken cancellationToken)
    {
        try
        {
            await _lifecycleHookService.RunAsync(
                CreateTaskHookContext(LifecycleHookEvents.AfterTaskFailed, input, session, result: null, exception),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // The original task failure is more important than a follow-up hook issue.
        }
    }

    private async Task MaybeUpdateCodebaseIndexAsync(CancellationToken cancellationToken)
    {
        if (!_codebaseIndexSettings.AutoUpdateAfterTask || _codebaseIndexService is null)
        {
            return;
        }

        try
        {
            await _codebaseIndexService.BuildAsync(force: false, cancellationToken);
        }
        catch (Exception exception)
        {
            // A best-effort post-turn index refresh must never turn a completed assistant turn into a failed turn.
            ApplicationLogMessages.CodebaseIndexAutoUpdateFailed(_logger, exception);
        }
    }

    private static LifecycleHookContext CreateTaskHookContext(
        string eventName,
        string input,
        ReplSessionContext session,
        ConversationTurnResult? result = null,
        Exception? exception = null)
    {
        return new LifecycleHookContext
        {
            ApplicationName = session.ApplicationName,
            ErrorMessage = exception?.Message,
            ErrorType = exception?.GetType().Name,
            EventName = eventName,
            InputTokens = result?.Metrics?.EstimatedInputTokens,
            LatencyMilliseconds = result?.Metrics is null
                ? null
                : Math.Max(0, (long)Math.Round(result.Metrics.Elapsed.TotalMilliseconds, MidpointRounding.AwayFromZero)),
            ModelId = session.ActiveModelId,
            OutputTokens = result?.Metrics?.EstimatedOutputTokens,
            ProviderName = session.ProviderName,
            ProviderRetryCount = result?.Metrics?.ProviderRetryCount,
            ResponseText = result?.ResponseText,
            ResultStatus = result?.Kind.ToString(),
            ResultSuccess = result is not null && exception is null,
            SessionId = session.SessionId,
            TaskInput = input,
            ToolRoundCount = result?.Metrics?.ToolRoundCount,
            TotalTokens = result?.Metrics?.EstimatedTotalTokens
        };
    }

    private static string CreateProviderRetrySystemPrompt(
        string? systemPrompt,
        ConversationResponseException exception,
        int recoveryAttempt,
        int retryLimit)
    {
        string retryInstruction = exception switch
        {
            { IsRetryableRawToolCallResponse: true } => RawToolCallRetryInstruction,
            { IsRetryableIncompletePlanResponse: true } => IncompletePlanRetryInstruction,
            _ => EmptyResponseRetryInstruction
        };

        string recoveryInstruction =
            $"{ProviderRecoveryRetryInstruction.Trim()}{Environment.NewLine}" +
            $"Recovery attempt {recoveryAttempt} of {retryLimit}.";

        return string.IsNullOrWhiteSpace(systemPrompt)
            ? $"{recoveryInstruction}{Environment.NewLine}{Environment.NewLine}{retryInstruction}"
            : $"{systemPrompt.Trim()}{Environment.NewLine}{Environment.NewLine}{recoveryInstruction}{Environment.NewLine}{Environment.NewLine}{retryInstruction}";
    }

    private IReadOnlyList<ToolDefinition> GetProfileToolDefinitions(ReplSessionContext session)
    {
        IReadOnlySet<string> enabledTools = session.AgentProfile.EnabledTools;

        return _toolRegistry.GetToolDefinitions()
            .Where(definition =>
                enabledTools.Contains(definition.Name) ||
                IsDynamicTool(definition.Name))
            .ToArray();
    }

    private static bool IsDynamicTool(string toolName)
    {
        return toolName.StartsWith(AgentToolNames.McpToolPrefix, StringComparison.Ordinal) ||
            toolName.StartsWith(AgentToolNames.CustomToolPrefix, StringComparison.Ordinal);
    }

    private async Task<string?> CreateProfileSystemPromptAsync(
        string? basePrompt,
        string lessonMemoryQuery,
        ReplSessionContext session,
        CancellationToken cancellationToken)
    {
        string? workspaceProfilePrompt = await _workspaceAgentProfilePromptProvider.LoadAsync(
            session,
            cancellationToken);
        string? contribution = string.IsNullOrWhiteSpace(workspaceProfilePrompt)
            ? session.AgentProfile.SystemPrompt
            : workspaceProfilePrompt;
        string? workspaceInstructions = await _workspaceInstructionsProvider.LoadAsync(
            session,
            cancellationToken);
        string? skillRouting = session.AgentProfile.EnabledTools.Contains(AgentToolNames.SkillLoad)
            ? await _skillService.CreateRoutingPromptAsync(
                session,
                cancellationToken)
            : null;
        string? lessonMemoryPrompt = await TryCreateLessonMemoryPromptAsync(
            lessonMemoryQuery,
            cancellationToken);
        string? statefulContext = session.CreateStatefulContextPrompt();
        string?[] promptSections =
        [
            basePrompt,
            contribution,
            workspaceInstructions,
            skillRouting,
            lessonMemoryPrompt,
            statefulContext
        ];

        string[] normalizedSections = promptSections
            .Where(static section => !string.IsNullOrWhiteSpace(section))
            .Select(static section => section!.Trim())
            .ToArray();

        return normalizedSections.Length == 0
            ? null
            : string.Join(
                $"{Environment.NewLine}{Environment.NewLine}",
                normalizedSections);
    }

    private async Task<string?> TryCreateLessonMemoryPromptAsync(
        string query,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        try
        {
            return await _lessonMemoryService.CreatePromptAsync(
                query,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Lesson memory is optional context; a load failure should not block the turn.
            return null;
        }
    }

    private async Task<string?> CreateBaseSystemPromptAsync(
        string? configuredSystemPrompt,
        ReplSessionContext session,
        CancellationToken cancellationToken)
    {
        return await _workspaceSystemPromptProvider.LoadAsync(
            session,
            configuredSystemPrompt,
            cancellationToken);
    }

    private static int? GetCompletionTokens(PhaseExecutionResult phaseResult)
    {
        return phaseResult.HasReportedCompletionTokens
            ? phaseResult.TotalCompletionTokens
            : null;
    }

    private static int AddClamped(int current, int delta)
    {
        if (delta <= 0)
        {
            return current;
        }

        return current > int.MaxValue - delta
            ? int.MaxValue
            : current + delta;
    }

    private static string CreateToolFeedbackContent(
        ToolInvocationResult invocationResult,
        int consecutiveFailureCount)
    {
        ArgumentNullException.ThrowIfNull(invocationResult);

        using JsonDocument dataDocument = JsonDocument.Parse(invocationResult.Result.JsonResult);

        ToolFeedbackPayload payload = new(
            invocationResult.ToolName,
            invocationResult.Result.Status,
            invocationResult.Result.IsSuccess,
            consecutiveFailureCount,
            invocationResult.Result.Message,
            CompactToolFeedbackData(
                invocationResult.ToolName,
                dataDocument.RootElement));

        return JsonSerializer.Serialize(
            payload,
            RelaxedConversationJsonContext.ToolFeedbackPayload);
    }

    private static JsonElement CompactToolFeedbackData(
        string toolName,
        JsonElement data)
    {
        return toolName switch
        {
            AgentToolNames.FileRead => CompactFileReadFeedback(data),
            AgentToolNames.CodebaseIndex => CompactCodebaseIndexFeedback(data),
            AgentToolNames.SearchFiles => CompactSearchFilesFeedback(data),
            AgentToolNames.TextSearch => CompactTextSearchFeedback(data),
            AgentToolNames.DirectoryList => CompactDirectoryListFeedback(data),
            AgentToolNames.AgentOrchestrate => CompactAgentOrchestrateFeedback(data),
            _ => data.Clone()
        };
    }

    private static JsonElement CompactFileReadFeedback(JsonElement data)
    {
        string? displayContent = TryGetStringProperty(data, "DisplayContent") ??
            TryGetStringProperty(data, "RawContent");

        return CreateJsonElement(writer =>
        {
            writer.WriteStartObject();
            WriteStringProperty(writer, "Path", TryGetStringProperty(data, "Path"));
            WriteStringProperty(writer, "DisplayContent", displayContent);
            WriteNullableInt32Property(writer, "StartLine", TryGetInt32Property(data, "StartLine"));
            WriteNullableInt32Property(writer, "EndLine", TryGetInt32Property(data, "EndLine"));
            WriteNullableInt32Property(writer, "TotalLines", TryGetInt32Property(data, "TotalLines"));
            WriteNullableBooleanProperty(writer, "Truncated", TryGetBooleanProperty(data, "Truncated"));
            WriteNullableInt32Property(writer, "NextOffset", TryGetNullableInt32Property(data, "NextOffset"));
            WriteStringProperty(writer, "Sha256", TryGetStringProperty(data, "Sha256"));
            WriteStringProperty(writer, "Encoding", TryGetStringProperty(data, "Encoding"));
            writer.WriteEndObject();
        });
    }

    private static JsonElement CompactCodebaseIndexFeedback(JsonElement data)
    {
        return CreateJsonElement(writer =>
        {
            writer.WriteStartObject();
            WriteStringProperty(writer, "Query", TryGetStringProperty(data, "Query"));
            WriteStringProperty(writer, "IndexPath", TryGetStringProperty(data, "IndexPath"));
            WriteNullableBooleanProperty(writer, "IndexWasUpdated", TryGetBooleanProperty(data, "IndexWasUpdated"));
            WriteNullableInt32Property(writer, "IndexedFileCount", TryGetInt32Property(data, "IndexedFileCount"));
            WriteStringArrayProperty(writer, "Warnings", TryGetStringArrayProperty(data, "Warnings", 5));
            writer.WritePropertyName("Matches");
            writer.WriteStartArray();
            foreach (JsonElement match in TryGetArrayProperty(data, "Matches").Take(5))
            {
                writer.WriteStartObject();
                WriteStringProperty(writer, "Path", TryGetStringProperty(match, "Path"));
                WriteStringProperty(writer, "Language", TryGetStringProperty(match, "Language"));
                WriteNullableDoubleProperty(writer, "Score", TryGetDoubleProperty(match, "Score"));
                WriteStringArrayProperty(writer, "Symbols", TryGetStringArrayProperty(match, "Symbols", 5));
                writer.WritePropertyName("Snippets");
                writer.WriteStartArray();
                foreach (JsonElement snippet in TryGetArrayProperty(match, "Snippets").Take(3))
                {
                    writer.WriteStartObject();
                    WriteNullableInt32Property(writer, "LineNumber", TryGetInt32Property(snippet, "LineNumber"));
                    WriteStringProperty(writer, "Text", NormalizeSummaryText(TryGetStringProperty(snippet, "Text")));
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    private static JsonElement CompactSearchFilesFeedback(JsonElement data)
    {
        return CreateJsonElement(writer =>
        {
            JsonElement[] matches = TryGetArrayProperty(data, "Matches").Take(10).ToArray();
            writer.WriteStartObject();
            WriteStringProperty(writer, "Query", TryGetStringProperty(data, "Query"));
            WriteStringProperty(writer, "Path", TryGetStringProperty(data, "Path"));
            WriteNullableInt32Property(writer, "TotalMatchCount", TryGetInt32Property(data, "TotalMatchCount") ?? matches.Length);
            WriteNullableBooleanProperty(writer, "HasMore", TryGetBooleanProperty(data, "HasMore"));
            writer.WritePropertyName("Matches");
            writer.WriteStartArray();
            foreach (JsonElement match in matches)
            {
                writer.WriteStartObject();
                WriteStringProperty(writer, "Path", TryGetStringProperty(match, "Path"));
                WriteNullableDoubleProperty(writer, "Score", TryGetDoubleProperty(match, "Score"));
                WriteStringProperty(writer, "MatchKind", TryGetStringProperty(match, "MatchKind"));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    private static JsonElement CompactTextSearchFeedback(JsonElement data)
    {
        return CreateJsonElement(writer =>
        {
            JsonElement[] matches = TryGetArrayProperty(data, "Matches")
                .Where(match => !IsGitIndexPath(TryGetStringProperty(match, "Path")))
                .Take(10)
                .ToArray();
            writer.WriteStartObject();
            WriteStringProperty(writer, "Query", TryGetStringProperty(data, "Query"));
            WriteStringProperty(writer, "Path", TryGetStringProperty(data, "Path"));
            WriteNullableInt32Property(writer, "MatchCount", matches.Length);
            writer.WritePropertyName("Matches");
            writer.WriteStartArray();
            foreach (JsonElement match in matches)
            {
                writer.WriteStartObject();
                WriteStringProperty(writer, "Path", TryGetStringProperty(match, "Path"));
                WriteNullableInt32Property(writer, "LineNumber", TryGetInt32Property(match, "LineNumber"));
                WriteStringProperty(writer, "LineText", NormalizeSummaryText(TryGetStringProperty(match, "LineText")));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    private static JsonElement CompactDirectoryListFeedback(JsonElement data)
    {
        return CreateJsonElement(writer =>
        {
            JsonElement[] entries = TryGetArrayProperty(data, "Entries").Take(50).ToArray();
            writer.WriteStartObject();
            WriteStringProperty(writer, "Path", TryGetStringProperty(data, "Path"));
            WriteNullableInt32Property(writer, "EntryCount", TryGetArrayProperty(data, "Entries").Count());
            writer.WritePropertyName("Entries");
            writer.WriteStartArray();
            foreach (JsonElement entry in entries)
            {
                writer.WriteStartObject();
                WriteStringProperty(writer, "Path", TryGetStringProperty(entry, "Path"));
                WriteStringProperty(writer, "EntryType", TryGetStringProperty(entry, "EntryType"));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    private static JsonElement CompactAgentOrchestrateFeedback(JsonElement data)
    {
        return CreateJsonElement(writer =>
        {
            writer.WriteStartObject();
            WriteNullableInt32Property(writer, "FailedTaskCount", TryGetInt32Property(data, "FailedTaskCount"));
            WriteNullableInt32Property(writer, "SucceededTaskCount", TryGetInt32Property(data, "SucceededTaskCount"));
            WriteNullableBooleanProperty(writer, "RecordedFileEdits", TryGetBooleanProperty(data, "RecordedFileEdits"));
            writer.WritePropertyName("Tasks");
            writer.WriteStartArray();
            foreach (JsonElement task in TryGetArrayProperty(data, "Tasks").Take(6))
            {
                writer.WriteStartObject();
                WriteStringProperty(writer, "AgentName", TryGetStringProperty(task, "AgentName"));
                WriteNullableInt32Property(writer, "Index", TryGetInt32Property(task, "Index"));
                WriteNullableBooleanProperty(writer, "Succeeded", TryGetBooleanProperty(task, "Succeeded"));
                WriteStringProperty(writer, "ErrorMessage", NormalizeSummaryText(TryGetStringProperty(task, "ErrorMessage")));
                WriteStringProperty(writer, "Response", NormalizeSummaryText(TryGetStringProperty(task, "Response")));
                WriteStringArrayProperty(writer, "ExecutedTools", TryGetStringArrayProperty(task, "ExecutedTools", 8));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    private static JsonElement CreateJsonElement(Action<Utf8JsonWriter> write)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            write(writer);
        }

        using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static string CreateSummaryJson(string summary)
    {
        JsonElement element = CreateJsonElement(writer =>
        {
            writer.WriteStartObject();
            WriteStringProperty(writer, "summary", summary);
            writer.WriteEndObject();
        });

        return element.GetRawText();
    }

    private static void WriteStringProperty(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
            return;
        }

        writer.WriteString(name, value);
    }

    private static void WriteNullableInt32Property(Utf8JsonWriter writer, string name, int? value)
    {
        if (value.HasValue)
        {
            writer.WriteNumber(name, value.Value);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static void WriteNullableDoubleProperty(Utf8JsonWriter writer, string name, double? value)
    {
        if (value.HasValue)
        {
            writer.WriteNumber(name, value.Value);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static void WriteNullableBooleanProperty(Utf8JsonWriter writer, string name, bool? value)
    {
        if (value.HasValue)
        {
            writer.WriteBoolean(name, value.Value);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static void WriteStringArrayProperty(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
    {
        writer.WritePropertyName(name);
        writer.WriteStartArray();
        foreach (string value in values)
        {
            writer.WriteStringValue(value);
        }
        writer.WriteEndArray();
    }

    private async Task<PhaseExecutionResult> RunPhaseAsync(
        string apiKey,
        string turnId,
        ReplSessionContext session,
        IReadOnlyList<ConversationRequestMessage> initialMessages,
        string? systemPrompt,
        IReadOnlyList<ToolDefinition> availableTools,
        IReadOnlySet<string> allowedToolNames,
        ConversationExecutionPhase executionPhase,
        IConversationProgressSink progressSink,
        ConversationSettings settings,
        CancellationTokenSource timeoutSource,
        ExecutionPlanTracker? executionPlanTracker,
        CancellationToken cancellationToken)
    {
        List<ConversationRequestMessage> messages = initialMessages.ToList();
        List<ConversationToolCall> executedToolCalls = [];
        List<ToolInvocationResult> executedToolResults = [];
        int consecutiveToolFailureCount = 0;
        int incompletePlanFinalResponseRetryCount = 0;
        int totalCompletionTokens = 0;
        int totalCachedInputTokens = 0;
        bool hasReportedCompletionTokens = false;
        ConversationTelemetryAccumulator telemetry = new();
        StagnantActionTracker stagnantActionTracker = new();
        ExecutionPlanProgress? latestPlanProgress = null;
        string? phaseSystemPrompt = systemPrompt;

        for (int round = 0; IsWithinToolRoundLimit(round, settings.MaxToolRoundsPerTurn); round++)
        {
            int stepIndex = round + 1;
            string stepId = CreateStableTrajectoryId(turnId, "step", stepIndex);
            await RecordStepStartedAsync(
                session,
                turnId,
                stepId,
                stepIndex,
                cancellationToken);

            ConversationResponse response = await SendAndMapResponseAsync(
                apiKey,
                session,
                turnId,
                stepId,
                stepIndex,
                messages,
                phaseSystemPrompt,
                availableTools,
                settings,
                timeoutSource,
                telemetry,
                progressSink,
                cancellationToken);
            await RecordStepEndedAsync(
                session,
                turnId,
                stepId,
                stepIndex,
                cancellationToken);

            if (response.CompletionTokens is > 0)
            {
                totalCompletionTokens += response.CompletionTokens.Value;
                hasReportedCompletionTokens = true;
            }

            if (response.CachedPromptTokens is > 0)
            {
                totalCachedInputTokens = AddClamped(
                    totalCachedInputTokens,
                    response.CachedPromptTokens.Value);
            }

            await RecordBudgetUsageAsync(
                session,
                response,
                cancellationToken);

            await ReportAssistantReasoningAsync(
                session,
                response,
                progressSink,
                cancellationToken);

            if (response.HasToolCalls)
            {
                telemetry.IncrementToolRoundCount();
                phaseSystemPrompt = systemPrompt;
                incompletePlanFinalResponseRetryCount = 0;

                ApplicationLogMessages.ConversationToolHandoffStarted(
                    _logger,
                    response.ToolCalls.Count);

                await progressSink.ReportToolCallsStartedAsync(
                    response.ToolCalls,
                    cancellationToken);

                bool reportedToolResultsDuringExecution = false;
                WorkspaceFileEditTransaction? pendingUndoBeforeToolExecution = GetPendingUndoFileEdit(session);
                ToolExecutionBatchResult toolExecutionResult;
                if (_toolExecutionPipeline is IStreamingToolExecutionPipeline streamingToolExecutionPipeline)
                {
                    toolExecutionResult = await streamingToolExecutionPipeline.ExecuteAsync(
                        response.ToolCalls,
                        session,
                        executionPhase,
                        allowedToolNames,
                        cancellationToken,
                        async (toolInvocationResult, toolCancellationToken) =>
                        {
                            reportedToolResultsDuringExecution = true;
                            await progressSink.ReportToolResultsAsync(
                                new ToolExecutionBatchResult([toolInvocationResult]),
                                toolCancellationToken);
                        });
                }
                else
                {
                    toolExecutionResult = await _toolExecutionPipeline.ExecuteAsync(
                        response.ToolCalls,
                        session,
                        executionPhase,
                        allowedToolNames,
                        cancellationToken);
                }
                bool recordedFileEdits = !ReferenceEquals(
                    pendingUndoBeforeToolExecution,
                    GetPendingUndoFileEdit(session));

                ApplicationLogMessages.ConversationToolHandoffCompleted(_logger);
                executedToolCalls.AddRange(response.ToolCalls);
                executedToolResults.AddRange(toolExecutionResult.Results);
                session.TryAppendConversationTurnToolProgress(
                    turnId,
                    response.ToolCalls,
                    CreateToolOutputMessages(toolExecutionResult, session));

                ExecutionPlanProgress? reportedPlanUpdate = await ReportPlanUpdatesAsync(
                    toolExecutionResult,
                    progressSink,
                    cancellationToken);
                if (reportedPlanUpdate is not null)
                {
                    latestPlanProgress = reportedPlanUpdate;
                }

                if (!reportedToolResultsDuringExecution)
                {
                    await progressSink.ReportToolResultsAsync(
                        toolExecutionResult,
                        cancellationToken);
                }

                if (executionPhase == ConversationExecutionPhase.Execution &&
                    executionPlanTracker is not null &&
                    reportedPlanUpdate is null)
                {
                    latestPlanProgress = executionPlanTracker.Advance();
                    await progressSink.ReportExecutionPlanAsync(
                        latestPlanProgress,
                        cancellationToken);
                }

                messages.Add(ConversationRequestMessage.AssistantToolCalls(
                    response.ToolCalls,
                    response.AssistantMessage,
                    response.ReasoningContent,
                    response.ReasoningDetailsJson));

                foreach (ToolInvocationResult invocationResult in toolExecutionResult.Results)
                {
                    consecutiveToolFailureCount = invocationResult.Result.IsSuccess
                        ? 0
                        : IncrementFailureCount(consecutiveToolFailureCount);

                    messages.Add(CreateToolResultMessage(
                        session,
                        invocationResult.ToolCallId,
                        CreateToolFeedbackContent(invocationResult, consecutiveToolFailureCount)));
                }

                if (stagnantActionTracker.Observe(
                        response.ToolCalls,
                        toolExecutionResult.Results,
                        recordedFileEdits))
                {
                    messages.Add(ConversationRequestMessage.User(StagnantActionReassessmentInstruction));
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(response.AssistantMessage))
            {
                throw new ConversationResponseException(
                    "The provider response did not contain an assistant message or any tool calls.");
            }

            if (HasIncompleteLivePlan(latestPlanProgress))
            {
                if (incompletePlanFinalResponseRetryCount < IncompletePlanFinalResponseRetryLimit)
                {
                    incompletePlanFinalResponseRetryCount++;
                    phaseSystemPrompt = CreateProviderRetrySystemPrompt(
                        systemPrompt,
                        CreateIncompletePlanException(),
                        incompletePlanFinalResponseRetryCount,
                        IncompletePlanFinalResponseRetryLimit);
                    continue;
                }

                latestPlanProgress = CompleteLivePlan(latestPlanProgress!);
                await progressSink.ReportExecutionPlanAsync(
                    latestPlanProgress,
                    cancellationToken);
            }

            return new PhaseExecutionResult(
                response.AssistantMessage,
                executedToolCalls,
                executedToolResults,
                totalCompletionTokens,
                hasReportedCompletionTokens,
                telemetry.EstimatedInputTokens,
                totalCachedInputTokens,
                telemetry.ProviderRetryCount,
                telemetry.ToolRoundCount,
                response.ReasoningContent,
                response.ReasoningDetailsJson);
        }

        throw new ConversationResponseException(
            $"The provider requested too many sequential tool rounds without producing a final assistant message. " +
            $"Configured limit: {settings.MaxToolRoundsPerTurn} round(s).");
    }

    private static WorkspaceFileEditTransaction? GetPendingUndoFileEdit(ReplSessionContext session)
    {
        return session.TryGetPendingUndoFileEdit(out WorkspaceFileEditTransaction? transaction)
            ? transaction
            : null;
    }

    private static bool IsWithinToolRoundLimit(
        int completedToolRoundCount,
        int maxToolRoundsPerTurn)
    {
        return maxToolRoundsPerTurn <= 0 ||
            completedToolRoundCount < maxToolRoundsPerTurn;
    }

    private static async Task ReportAssistantReasoningAsync(
        ReplSessionContext session,
        ConversationResponse response,
        IConversationProgressSink progressSink,
        CancellationToken cancellationToken)
    {
        if (!session.ShowThinking)
        {
            return;
        }

        string? reasoningText = ExtractReasoningTextForDisplay(response);
        if (string.IsNullOrWhiteSpace(reasoningText))
        {
            return;
        }

        await progressSink.ReportAssistantReasoningAsync(
            reasoningText,
            cancellationToken);
    }

    private static string? ExtractReasoningTextForDisplay(ConversationResponse response)
    {
        if (!string.IsNullOrWhiteSpace(response.ReasoningContent))
        {
            return response.ReasoningContent.Trim();
        }

        return ExtractReasoningDetailsText(response.ReasoningDetailsJson);
    }

    private static string? ExtractReasoningDetailsText(string? reasoningDetailsJson)
    {
        if (string.IsNullOrWhiteSpace(reasoningDetailsJson))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(reasoningDetailsJson);
            List<string> parts = [];
            CollectReasoningText(document.RootElement, parts);

            return parts.Count == 0
                ? null
                : string.Join(
                    Environment.NewLine + Environment.NewLine,
                    parts.Distinct(StringComparer.Ordinal));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void CollectReasoningText(
        JsonElement element,
        List<string> parts)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                AddReasoningTextProperty(element, parts, "text");
                AddReasoningTextProperty(element, parts, "summary");
                AddReasoningTextProperty(element, parts, "content");

                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        CollectReasoningText(property.Value, parts);
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    CollectReasoningText(item, parts);
                }

                break;
        }
    }

    private static void AddReasoningTextProperty(
        JsonElement element,
        List<string> parts,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
        {
            return;
        }

        if (value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString()))
        {
            parts.Add(value.GetString()!.Trim());
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(item.GetString()))
                {
                    parts.Add(item.GetString()!.Trim());
                }
            }
        }
    }

    private static async Task<ExecutionPlanProgress?> ReportPlanUpdatesAsync(
        ToolExecutionBatchResult toolExecutionResult,
        IConversationProgressSink progressSink,
        CancellationToken cancellationToken)
    {
        ExecutionPlanProgress? latestProgress = null;

        foreach (ToolInvocationResult invocationResult in toolExecutionResult.Results)
        {
            if (!TryCreatePlanProgress(invocationResult, out ExecutionPlanProgress? progress) ||
                progress is null)
            {
                continue;
            }

            await progressSink.ReportExecutionPlanAsync(
                progress,
                cancellationToken);
            latestProgress = progress;
        }

        return latestProgress;
    }

    private static bool TryCreatePlanProgress(
        ToolInvocationResult invocationResult,
        out ExecutionPlanProgress? progress)
    {
        progress = null;

        if (!invocationResult.Result.IsSuccess ||
            !string.Equals(invocationResult.ToolName, AgentToolNames.UpdatePlan, StringComparison.Ordinal))
        {
            return false;
        }

        PlanUpdateResult? result;
        try
        {
            result = JsonSerializer.Deserialize(
                invocationResult.Result.JsonResult,
                ToolJsonContext.Default.PlanUpdateResult);
        }
        catch (JsonException)
        {
            return false;
        }

        if (result is null || result.Plan.Count == 0)
        {
            return false;
        }

        string[] tasks = result.Plan
            .Select(static item => item.Step)
            .Where(static step => !string.IsNullOrWhiteSpace(step))
            .ToArray();

        if (tasks.Length == 0)
        {
            return false;
        }

        int completedTaskCount = Math.Min(
            result.CompletedTaskCount,
            tasks.Length);
        progress = new ExecutionPlanProgress(tasks, completedTaskCount);
        return true;
    }

    private static int IncrementFailureCount(int currentFailureCount)
    {
        return currentFailureCount == int.MaxValue
            ? int.MaxValue
            : currentFailureCount + 1;
    }

    private async Task<ConversationResponse> SendAndMapResponseAsync(
        string apiKey,
        ReplSessionContext session,
        string turnId,
        string stepId,
        int stepIndex,
        IReadOnlyList<ConversationRequestMessage> messages,
        string? systemPrompt,
        IReadOnlyList<ToolDefinition> availableTools,
        ConversationSettings settings,
        CancellationTokenSource timeoutSource,
        ConversationTelemetryAccumulator telemetry,
        IConversationProgressSink progressSink,
        CancellationToken cancellationToken)
    {
        string? requestSystemPrompt = systemPrompt;

        for (int attempt = 0; attempt <= RetryableProviderOutputRetryLimit; attempt++)
        {
            ModelRequestExchange exchange = await SendProviderRequestAsync(
                apiKey,
                session,
                turnId,
                stepId,
                stepIndex,
                attempt + 1,
                messages,
                requestSystemPrompt,
                availableTools,
                settings,
                timeoutSource,
                telemetry,
                progressSink,
                cancellationToken);

            try
            {
                ConversationResponse response = _responseMapper.Map(exchange.Payload)
                    ?? throw new ConversationResponseException(
                        "The provider response mapper returned no normalized response.");
                telemetry.ReplaceLastEstimatedInputTokens(GetReportedInputTokens(response));
                await RecordModelRequestCompletedAsync(
                    session,
                    turnId,
                    stepId,
                    stepIndex,
                    exchange,
                    response,
                    "completed",
                    null,
                    cancellationToken);
                return response;
            }
            catch (ConversationResponseException exception)
                when (exception.IsRetryableProviderOutput && attempt < RetryableProviderOutputRetryLimit)
            {
                await RecordModelRequestCompletedAsync(
                    session,
                    turnId,
                    stepId,
                    stepIndex,
                    exchange,
                    null,
                    "rejected",
                    exception,
                    cancellationToken);
                await RecordProviderOutputRetryAsync(
                    session,
                    turnId,
                    stepId,
                    stepIndex,
                    attempt + 1,
                    RetryableProviderOutputRetryLimit,
                    exception,
                    cancellationToken);
                requestSystemPrompt = CreateProviderRetrySystemPrompt(
                    systemPrompt,
                    exception,
                    attempt + 1,
                    RetryableProviderOutputRetryLimit);
            }
            catch (ConversationResponseException exception) when (exception.IsRetryableProviderOutput)
            {
                await RecordModelRequestCompletedAsync(
                    session,
                    turnId,
                    stepId,
                    stepIndex,
                    exchange,
                    null,
                    "failed",
                    exception,
                    cancellationToken);
                throw CreateProviderOutputExhaustedException(exception);
            }
            catch (ConversationResponseException exception)
            {
                await RecordModelRequestCompletedAsync(
                    session,
                    turnId,
                    stepId,
                    stepIndex,
                    exchange,
                    null,
                    "failed",
                    exception,
                    cancellationToken);
                throw;
            }
            catch (Exception exception)
            {
                await RecordModelRequestCompletedAsync(
                    session,
                    turnId,
                    stepId,
                    stepIndex,
                    exchange,
                    null,
                    "failed",
                    exception,
                    cancellationToken);
                throw new ConversationResponseException(
                    "The provider response could not be normalized into the internal conversation model.",
                    exception);
            }
        }

        throw new ConversationResponseException(
            "The provider response recovery loop ended without producing a normalized response.");
    }

    private static bool HasIncompleteLivePlan(ExecutionPlanProgress? latestPlanProgress)
    {
        return latestPlanProgress is not null &&
            latestPlanProgress.CompletedTaskCount < latestPlanProgress.Tasks.Count;
    }

    private static ConversationResponseException CreateIncompletePlanException()
    {
        return new ConversationResponseException(
            "The provider returned a final assistant message while the live plan still had in-progress or pending work.",
            isRetryableIncompletePlanResponse: true);
    }

    private static ExecutionPlanProgress CompleteLivePlan(ExecutionPlanProgress progress)
    {
        return new ExecutionPlanProgress(progress.Tasks, progress.Tasks.Count);
    }

    private static int? GetReportedInputTokens(ConversationResponse response)
    {
        if (response.PromptTokens is > 0)
        {
            return response.PromptTokens.Value;
        }

        if (response.TotalTokens is > 0 && response.CompletionTokens is > 0)
        {
            int promptTokens = response.TotalTokens.Value - response.CompletionTokens.Value;
            return promptTokens > 0
                ? promptTokens
                : null;
        }

        return null;
    }

    private async Task RecordBudgetUsageAsync(
        ReplSessionContext session,
        ConversationResponse response,
        CancellationToken cancellationToken)
    {
        int inputTokens = Math.Max(0, GetReportedInputTokens(response) ?? 0);
        int cachedInputTokens = Math.Clamp(
            response.CachedPromptTokens ?? 0,
            0,
            inputTokens);
        int outputTokens = response.CompletionTokens is > 0
            ? response.CompletionTokens.Value
            : EstimateResponseOutputTokens(response);

        BudgetControlsUsageDelta usage = new(
            inputTokens,
            cachedInputTokens,
            Math.Max(0, outputTokens));

        if (!usage.HasUsage)
        {
            return;
        }

        try
        {
            await _budgetControlsUsageService.RecordUsageAsync(
                session,
                usage,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Budget reporting is advisory; failed reporting should not turn a valid model response into a failed turn.
        }
    }

    private int EstimateResponseOutputTokens(ConversationResponse response)
    {
        int total = 0;
        AddEstimate(response.AssistantMessage);
        foreach (ConversationToolCall toolCall in response.ToolCalls)
        {
            AddEstimate(toolCall.Name);
            AddEstimate(toolCall.ArgumentsJson);
        }

        return total;

        void AddEstimate(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                total += _tokenEstimator.Estimate(value);
            }
        }
    }

    private int EstimateInputTokens(ConversationProviderRequest request)
    {
        int total = 0;

        AddEstimate(request.SystemPrompt);
        foreach (ConversationRequestMessage message in request.Messages)
        {
            AddEstimate(message.Role);
            AddEstimate(message.Content);
            AddEstimate(message.ReasoningContent);
            AddEstimate(message.ReasoningDetailsJson);
            AddEstimate(message.ToolCallId);

            foreach (ConversationAttachment attachment in message.Attachments)
            {
                AddEstimate(attachment.Name);
                AddEstimate(attachment.MediaType);
                AddEstimate(attachment.TextContent);

                if (attachment.IsImage)
                {
                    total += 1_000;
                }
                else if (!attachment.IsText)
                {
                    AddEstimate(attachment.ContentBase64);
                }
            }

            foreach (ConversationToolCall toolCall in message.ToolCalls)
            {
                AddEstimate(toolCall.Id);
                AddEstimate(toolCall.Name);
                AddEstimate(toolCall.ArgumentsJson);
            }
        }

        foreach (ToolDefinition tool in request.AvailableTools)
        {
            AddEstimate(tool.Name);
            AddEstimate(tool.Description);
            AddEstimate(tool.Schema.GetRawText());
        }

        AddEstimate(request.ReasoningEffort);
        return total;

        void AddEstimate(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                total += _tokenEstimator.Estimate(value);
            }
        }
    }

    private async Task<ModelRequestExchange> SendProviderRequestAsync(
        string apiKey,
        ReplSessionContext session,
        string turnId,
        string stepId,
        int stepIndex,
        int attemptNumber,
        IReadOnlyList<ConversationRequestMessage> messages,
        string? systemPrompt,
        IReadOnlyList<ToolDefinition> availableTools,
        ConversationSettings settings,
        CancellationTokenSource timeoutSource,
        ConversationTelemetryAccumulator telemetry,
        IConversationProgressSink progressSink,
        CancellationToken cancellationToken)
    {
        try
        {
            await EnsureBudgetAllowsProviderRequestAsync(
                session,
                cancellationToken);

            PreparedConversationRequest preparedRequest = PrepareConversationRequest(
                session,
                messages,
                systemPrompt,
                availableTools);
            await RecordCompactionIfChangedAsync(
                session,
                messages,
                systemPrompt,
                preparedRequest,
                cancellationToken);

            ConversationProviderRequest request = new(
                session.ProviderProfile,
                apiKey,
                session.ActiveModelId,
                preparedRequest.Messages,
                preparedRequest.SystemPrompt,
                availableTools,
                ReasoningEffort: session.ReasoningEffort,
                OnAssistantMessageChunkAsync: (text, textCancellationToken) =>
                    progressSink.ReportAssistantMessageChunkAsync(text, textCancellationToken),
                OnProviderRetryAsync: (retryProgress, retryCancellationToken) =>
                    progressSink.ReportProviderRetryAsync(retryProgress, retryCancellationToken),
                ThinkingMode: session.ThinkingMode,
                ShowThinking: session.ShowThinking);

            telemetry.AddEstimatedInputTokens(EstimateInputTokens(request));
            string modelRequestId = CreateStableTrajectoryId(
                turnId,
                "request",
                stepIndex,
                attemptNumber);
            DateTimeOffset startedAtUtc = _timeProvider.GetUtcNow();
            DateTimeOffset? firstTokenAtUtc = null;

            await RecordPromptSnapshotAsync(
                session,
                turnId,
                stepId,
                stepIndex,
                modelRequestId,
                preparedRequest.SystemPrompt,
                preparedRequest.Messages,
                availableTools,
                request,
                cancellationToken);
            await RecordModelRequestStartedAsync(
                session,
                turnId,
                stepId,
                stepIndex,
                modelRequestId,
                request,
                startedAtUtc,
                cancellationToken);

            ConversationProviderPayload payload = await _providerClient.SendAsync(
                request with
                {
                    OnAssistantMessageChunkAsync = async (text, textCancellationToken) =>
                    {
                        DateTimeOffset chunkAtUtc = _timeProvider.GetUtcNow();
                        bool isFirstChunk = firstTokenAtUtc is null;
                        firstTokenAtUtc ??= chunkAtUtc;
                        await RecordModelChunkAsync(
                            session,
                            turnId,
                            stepId,
                            stepIndex,
                            modelRequestId,
                            text,
                            chunkAtUtc,
                            isFirstChunk,
                            textCancellationToken);
                        await progressSink.ReportAssistantMessageChunkAsync(text, textCancellationToken);
                    },
                    OnProviderRetryAsync = async (retryProgress, retryCancellationToken) =>
                    {
                        await RecordRetryAsync(
                            session,
                            turnId,
                            stepId,
                            stepIndex,
                            modelRequestId,
                            retryProgress,
                            retryCancellationToken);
                        await progressSink.ReportProviderRetryAsync(retryProgress, retryCancellationToken);
                    }
                },
                timeoutSource.Token);
            telemetry.AddProviderRetryCount(payload.RetryCount);
            return new ModelRequestExchange(
                modelRequestId,
                startedAtUtc,
                firstTokenAtUtc,
                _timeProvider.GetUtcNow(),
                payload);
        }
        catch (ConversationProviderException)
        {
            throw;
        }
        catch (ConversationPipelineException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutSource.IsCancellationRequested)
        {
            throw new ConversationProviderException(
                $"The conversation request timed out after {settings.RequestTimeout.TotalSeconds:0} seconds.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ConversationProviderException(
                "The configured provider failed while processing the conversation request.",
                exception);
        }
    }

    private IReadOnlyList<ConversationRequestMessage> BuildInitialConversationMessages(
        ReplSessionContext session)
    {
        ConversationSectionTurn[] turns = session.ConversationTurns.ToArray();
        List<ConversationRequestMessage> messages = [];
        foreach (ConversationSectionTurn turn in turns)
        {
            if (turn.Status == ConversationTurnStatus.Cancelled)
            {
                continue;
            }

            messages.Add(ConversationRequestMessage.User(turn.UserInput, turn.Attachments));

            if (turn.ToolCalls.Count > 0)
            {
                messages.Add(ConversationRequestMessage.AssistantToolCalls(turn.ToolCalls));
                foreach ((string toolCallId, string toolOutputMessage) in CreateStoredToolResultMessages(turn))
                {
                    messages.Add(ConversationRequestMessage.ToolResult(
                        toolCallId,
                        TruncateToolResultContent(session, toolOutputMessage)));
                }
            }

            if (!string.IsNullOrWhiteSpace(turn.AssistantResponse))
            {
                messages.Add(ConversationRequestMessage.AssistantMessage(
                    turn.AssistantResponse,
                    turn.AssistantReasoningContent,
                    turn.AssistantReasoningDetailsJson));
            }
        }

        return messages;
    }

    private static IReadOnlyList<(string ToolCallId, string ToolOutputMessage)> CreateStoredToolResultMessages(
        ConversationSectionTurn turn)
    {
        if (turn.ToolOutputMessages.Count == 0)
        {
            return [];
        }

        List<(string ToolCallId, string ToolOutputMessage)> results = [];
        for (int index = 0; index < turn.ToolOutputMessages.Count; index++)
        {
            string toolCallId = index < turn.ToolCalls.Count
                ? turn.ToolCalls[index].Id
                : $"{turn.TurnId}-tool-{index + 1}";
            results.Add((toolCallId, turn.ToolOutputMessages[index]));
        }

        return results;
    }

    private static string? AppendInterruptedTurnRecoveryContext(
        string? systemPrompt,
        ReplSessionContext session)
    {
        if (!session.ConversationTurns.Any(static turn => turn.Status == ConversationTurnStatus.Interrupted))
        {
            return systemPrompt;
        }

        return string.IsNullOrWhiteSpace(systemPrompt)
            ? InterruptedTurnRecoveryMessage
            : InterruptedTurnRecoveryMessage + Environment.NewLine + Environment.NewLine + systemPrompt.TrimStart();
    }

    private PreparedConversationRequest PrepareConversationRequest(
        ReplSessionContext session,
        IReadOnlyList<ConversationRequestMessage> messages,
        string? systemPrompt,
        IReadOnlyList<ToolDefinition> availableTools)
    {
        int toolDefinitionTokens = EstimateToolDefinitionTokens(availableTools);
        ContextBudget budget = CreateContextBudget(
            session.ActiveModelContextMetadata,
            session.ActiveModelContextWindowTokens ?? DefaultModelContextWindowTokens,
            toolDefinitionTokens);

        string? effectiveSystemPrompt = AppendInterruptedTurnRecoveryContext(systemPrompt, session);
        List<ConversationRequestMessage> truncatedMessages = ApplyToolResultTruncation(messages, budget);
        int fullMessageTokens = truncatedMessages.Sum(EstimateMessageTokens);
        int fullSystemPromptTokens = string.IsNullOrWhiteSpace(effectiveSystemPrompt)
            ? 0
            : _tokenEstimator.Estimate(effectiveSystemPrompt);

        if (fullMessageTokens + fullSystemPromptTokens <= budget.AutoCompactTokenLimit &&
            fullMessageTokens + fullSystemPromptTokens <= budget.UsableInputTokens)
        {
            return new PreparedConversationRequest(
                effectiveSystemPrompt,
                truncatedMessages.ToArray());
        }

        List<ConversationRequestMessage> trimmedMessages = TrimMessages(
            truncatedMessages,
            budget);
        int messageTokens = trimmedMessages.Sum(EstimateMessageTokens);
        int remainingSystemBudget = Math.Max(256, budget.UsableInputTokens - messageTokens);
        string? trimmedSystemPrompt = TrimSystemPrompt(
            effectiveSystemPrompt,
            remainingSystemBudget);

        if (!string.IsNullOrWhiteSpace(trimmedSystemPrompt))
        {
            int totalTokens = messageTokens + _tokenEstimator.Estimate(trimmedSystemPrompt);
            if (totalTokens > budget.UsableInputTokens)
            {
                trimmedSystemPrompt = TrimSystemPrompt(
                    trimmedSystemPrompt,
                    Math.Max(256, budget.UsableInputTokens - messageTokens));
            }
        }

        return new PreparedConversationRequest(
            trimmedSystemPrompt,
            trimmedMessages.ToArray());
    }

    private List<ConversationRequestMessage> TrimMessages(
        IReadOnlyList<ConversationRequestMessage> messages,
        ContextBudget budget)
    {
        if (messages.Count == 0)
        {
            return [];
        }

        int currentTaskIndex = FindCurrentTaskIndex(messages);
        IReadOnlyList<ConversationRequestMessage> historicalConversation = messages
            .Take(currentTaskIndex)
            .ToArray();
        IReadOnlyList<ConversationRequestMessage> activeTurnMessages = messages
            .Skip(currentTaskIndex + 1)
            .ToArray();

        int activeToolTokens = activeTurnMessages.Sum(EstimateMessageTokens);
        int currentTaskBudget = Math.Max(
            512,
            budget.UsableInputTokens -
            Math.Min(budget.RecentConversationTargetTokens, budget.UsableInputTokens / 2) -
            activeToolTokens);
        ConversationRequestMessage currentTask = TrimMessageToBudget(
            messages[currentTaskIndex],
            currentTaskBudget)
            ?? messages[currentTaskIndex];
        int currentTaskTokens = EstimateMessageTokens(currentTask);

        int remainingBudget = Math.Max(
            256,
            budget.UsableInputTokens - currentTaskTokens - activeToolTokens);
        int recentConversationBudget = Math.Min(
            budget.RecentConversationTargetTokens,
            remainingBudget);

        List<ConversationRequestMessage> keptHistoricalConversation = KeepNewestTurnsWithinBudget(
            historicalConversation,
            recentConversationBudget);
        int keptHistoricalTokens = keptHistoricalConversation.Sum(EstimateMessageTokens);

        List<ConversationRequestMessage> keptActiveTurnMessages = KeepNewestToolRoundsWithinBudget(
            activeTurnMessages,
            Math.Max(256, budget.UsableInputTokens - currentTaskTokens - keptHistoricalTokens));

        List<ConversationRequestMessage> finalMessages = [];
        finalMessages.AddRange(keptHistoricalConversation);
        finalMessages.Add(currentTask);
        finalMessages.AddRange(keptActiveTurnMessages);
        return finalMessages;
    }

    private ContextBudget CreateContextBudget(
        ModelContextMetadata? modelContextMetadata,
        int fallbackContextWindowTokens,
        int toolDefinitionTokens)
    {
        int contextWindowTokens = modelContextMetadata?.ContextWindowTokens is > 0
            ? Math.Min(modelContextMetadata.ContextWindowTokens, fallbackContextWindowTokens)
            : fallbackContextWindowTokens;
        double effectivePercent = modelContextMetadata?.EffectiveContextWindowPercent is > 0d and <= 1d
            ? modelContextMetadata.EffectiveContextWindowPercent.Value
            : 1d;
        int effectiveInputLimit = Math.Max(
            MinimumUsableInputTokens,
            FractionOf(contextWindowTokens, effectivePercent));
        int derivedAutoCompactTokenLimit = FractionOf(contextWindowTokens, 0.90d);
        int autoCompactTokenLimit = modelContextMetadata?.AutoCompactTokenLimit is > 0
            ? Math.Min(modelContextMetadata.AutoCompactTokenLimit.Value, derivedAutoCompactTokenLimit)
            : derivedAutoCompactTokenLimit;
        int usableInputTokens = Math.Max(
            MinimumUsableInputTokens,
            effectiveInputLimit - toolDefinitionTokens);
        int recentConversationTargetTokens = Math.Max(
            MinimumRecentConversationTokens,
            usableInputTokens / 4);
        int toolResultTokenLimit = ResolveToolResultTokenLimit(
            modelContextMetadata,
            contextWindowTokens);

        return new ContextBudget(
            contextWindowTokens,
            usableInputTokens,
            recentConversationTargetTokens,
            autoCompactTokenLimit,
            toolResultTokenLimit,
            modelContextMetadata?.ToolResultTruncationPolicy);
    }

    private static int FindCurrentTaskIndex(IReadOnlyList<ConversationRequestMessage> messages)
    {
        for (int index = messages.Count - 1; index >= 0; index--)
        {
            if (string.Equals(messages[index].Role, "user", StringComparison.Ordinal))
            {
                return index;
            }
        }

        return messages.Count - 1;
    }

    private List<ConversationRequestMessage> KeepNewestTurnsWithinBudget(
        IReadOnlyList<ConversationRequestMessage> messages,
        int budgetTokens)
    {
        if (messages.Count == 0 || budgetTokens <= 0)
        {
            return [];
        }

        List<(int Start, int Count, int Tokens)> turns = [];
        int index = 0;
        while (index < messages.Count)
        {
            int start = index;
            int tokens = EstimateMessageTokens(messages[index]);
            index++;

            while (index < messages.Count &&
                !string.Equals(messages[index].Role, "user", StringComparison.Ordinal))
            {
                tokens += EstimateMessageTokens(messages[index]);
                index++;
            }

            turns.Add((start, index - start, tokens));
        }

        int total = 0;
        List<ConversationRequestMessage> kept = [];
        for (int turnIndex = turns.Count - 1; turnIndex >= 0; turnIndex--)
        {
            (int start, int count, int tokens) = turns[turnIndex];
            if (total + tokens <= budgetTokens)
            {
                kept.InsertRange(0, messages.Skip(start).Take(count));
                total += tokens;
                continue;
            }

            int remainingBudget = budgetTokens - total;
            if (remainingBudget <= 0)
            {
                break;
            }

            List<ConversationRequestMessage> partialTurn = KeepNewestMessagesWithinBudget(
                messages.Skip(start).Take(count).ToArray(),
                remainingBudget,
                allowPartialOldestMessage: true);
            if (partialTurn.Count > 0)
            {
                kept.InsertRange(0, partialTurn);
            }

            break;
        }

        return kept;
    }

    private List<ConversationRequestMessage> KeepNewestMessagesWithinBudget(
        IReadOnlyList<ConversationRequestMessage> messages,
        int budgetTokens,
        bool allowPartialOldestMessage)
    {
        if (messages.Count == 0 || budgetTokens <= 0)
        {
            return [];
        }

        int total = 0;
        List<ConversationRequestMessage> kept = [];
        for (int index = messages.Count - 1; index >= 0; index--)
        {
            ConversationRequestMessage message = messages[index];
            int messageTokens = EstimateMessageTokens(message);
            if (total + messageTokens <= budgetTokens)
            {
                kept.Add(message);
                total += messageTokens;
                continue;
            }

            if (allowPartialOldestMessage)
            {
                int remainingBudget = budgetTokens - total;
                ConversationRequestMessage? partialMessage = TrimMessageToBudget(
                    message,
                    remainingBudget,
                    preserveTail: true);
                if (partialMessage is not null)
                {
                    kept.Add(partialMessage);
                }
            }

            break;
        }

        kept.Reverse();
        return kept;
    }

    private List<ConversationRequestMessage> KeepNewestToolRoundsWithinBudget(
        IReadOnlyList<ConversationRequestMessage> activeTurnMessages,
        int budgetTokens)
    {
        if (activeTurnMessages.Count == 0 || budgetTokens <= 0)
        {
            return [];
        }

        List<(int Start, int Count, int Tokens)> rounds = [];
        int index = 0;
        while (index < activeTurnMessages.Count)
        {
            int start = index;
            int count = 1;
            int tokens = EstimateMessageTokens(activeTurnMessages[index]);
            index++;

            while (index < activeTurnMessages.Count &&
                string.Equals(activeTurnMessages[index].Role, "tool", StringComparison.Ordinal))
            {
                tokens += EstimateMessageTokens(activeTurnMessages[index]);
                count++;
                index++;
            }

            rounds.Add((start, count, tokens));
        }

        int total = 0;
        int earliestKeptRound = rounds.Count;
        for (int roundIndex = rounds.Count - 1; roundIndex >= 0; roundIndex--)
        {
            int roundTokens = rounds[roundIndex].Tokens;
            if (earliestKeptRound < rounds.Count && total + roundTokens > budgetTokens)
            {
                break;
            }

            earliestKeptRound = roundIndex;
            total += roundTokens;
        }

        if (earliestKeptRound >= rounds.Count)
        {
            return [];
        }

        int startIndex = rounds[earliestKeptRound].Start;
        return activeTurnMessages
            .Skip(startIndex)
            .ToList();
    }

    private List<ConversationRequestMessage> PruneOldToolOutput(
        IReadOnlyList<ConversationRequestMessage> activeTurnMessages)
    {
        return activeTurnMessages.ToList();
    }

    private ConversationRequestMessage SummarizeToolResultMessage(ConversationRequestMessage message)
    {
        string summary = CreateToolResultSummary(message.Content);
        return ConversationRequestMessage.ToolResult(
            message.ToolCallId ?? "pruned_tool_result",
            summary);
    }

    private string CreateToolResultSummary(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return """{"summary":"Tool output was pruned during context compaction."}""";
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(content);
            JsonElement root = document.RootElement;
            string toolName = TryGetStringProperty(root, "ToolName") ?? "tool";
            string messageText = NormalizeSummaryText(TryGetStringProperty(root, "Message"));
            string status = TryGetStringProperty(root, "Status") ?? "Unknown";
            string[] dataExcerpts = ExtractRelevantJsonExcerpts(root, 3);

            List<string> lines =
            [
                $"Tool: {toolName}",
                $"Status: {status}"
            ];
            if (!string.IsNullOrWhiteSpace(messageText))
            {
                lines.Add($"Summary: {messageText}");
            }

            lines.AddRange(dataExcerpts);
            lines.Add("Full output was pruned during context compaction.");

            string text = string.Join(Environment.NewLine, lines);
            return CreateSummaryJson(text);
        }
        catch (JsonException)
        {
            string text = NormalizeSummaryText(content);
            return CreateSummaryJson(
                $"{text}{Environment.NewLine}Full output was pruned during context compaction.");
        }
    }

    private string? TrimSystemPrompt(string? systemPrompt, int budgetTokens)
    {
        if (string.IsNullOrWhiteSpace(systemPrompt))
        {
            return null;
        }

        string[] sections = systemPrompt
            .Split($"{Environment.NewLine}{Environment.NewLine}", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (sections.Length == 0)
        {
            return null;
        }

        List<string> keptSections = [];
        foreach (string section in sections)
        {
            string candidate = keptSections.Count == 0
                ? section
                : string.Join($"{Environment.NewLine}{Environment.NewLine}", keptSections.Append(section));
            if (_tokenEstimator.Estimate(candidate) <= budgetTokens)
            {
                keptSections.Add(section);
                continue;
            }

            int remainingTokens = Math.Max(
                0,
                budgetTokens - (keptSections.Count == 0
                    ? 0
                    : _tokenEstimator.Estimate(string.Join($"{Environment.NewLine}{Environment.NewLine}", keptSections))));
            if (remainingTokens > 64)
            {
                string trimmedSection = TrimTextToBudget(section, remainingTokens);
                if (!string.IsNullOrWhiteSpace(trimmedSection))
                {
                    keptSections.Add(trimmedSection);
                }
            }

            break;
        }

        return keptSections.Count == 0
            ? TrimTextToBudget(systemPrompt.Trim(), budgetTokens)
            : string.Join($"{Environment.NewLine}{Environment.NewLine}", keptSections);
    }

    private ConversationRequestMessage? TrimMessageToBudget(
        ConversationRequestMessage message,
        int budgetTokens,
        bool preserveTail = false)
    {
        if (budgetTokens <= 0)
        {
            return null;
        }

        if (EstimateMessageTokens(message) <= budgetTokens)
        {
            return message;
        }

        if (!string.Equals(message.Role, "user", StringComparison.Ordinal))
        {
            string trimmedContent = TrimTextToBudget(message.Content, budgetTokens, preserveTail);
            return string.IsNullOrWhiteSpace(trimmedContent)
                ? null
                : ConversationRequestMessage.AssistantMessage(
                    trimmedContent,
                    message.ReasoningContent,
                    message.ReasoningDetailsJson);
        }

        IReadOnlyList<ConversationAttachment> attachments = message.Attachments;
        string? trimmedContentText = TrimTextToBudget(
            message.Content,
            Math.Max(128, budgetTokens / 2),
            preserveTail);
        List<ConversationAttachment> trimmedAttachments = [];
        foreach (ConversationAttachment attachment in attachments)
        {
            if (!attachment.IsText)
            {
                trimmedAttachments.Add(attachment);
                continue;
            }

            trimmedAttachments.Add(new ConversationAttachment(
                attachment.Name,
                attachment.MediaType,
                attachment.ContentBase64,
                TrimTextToBudget(
                    attachment.TextContent,
                    Math.Max(64, budgetTokens / Math.Max(1, attachments.Count + 1)),
                    preserveTail)));
        }

        if (string.IsNullOrWhiteSpace(trimmedContentText) && trimmedAttachments.Count == 0)
        {
            return null;
        }

        return ConversationRequestMessage.User(
            string.IsNullOrWhiteSpace(trimmedContentText) ? message.Content! : trimmedContentText,
            trimmedAttachments);
    }

    private int EstimateToolDefinitionTokens(IReadOnlyList<ToolDefinition> availableTools)
    {
        int total = 0;
        foreach (ToolDefinition tool in availableTools)
        {
            total += _tokenEstimator.Estimate(tool.Name);
            total += _tokenEstimator.Estimate(tool.Description);
            total += _tokenEstimator.Estimate(tool.Schema.GetRawText());
        }

        return total;
    }

    private int EstimateMessageTokens(ConversationRequestMessage message)
    {
        int total = 0;
        AddEstimate(message.Role);
        AddEstimate(message.Content);
        AddEstimate(message.ReasoningContent);
        AddEstimate(message.ReasoningDetailsJson);
        AddEstimate(message.ToolCallId);

        foreach (ConversationAttachment attachment in message.Attachments)
        {
            AddEstimate(attachment.Name);
            AddEstimate(attachment.MediaType);
            AddEstimate(attachment.TextContent);
            if (attachment.IsImage)
            {
                total += 1_000;
            }
            else if (!attachment.IsText)
            {
                AddEstimate(attachment.ContentBase64);
            }
        }

        foreach (ConversationToolCall toolCall in message.ToolCalls)
        {
            AddEstimate(toolCall.Id);
            AddEstimate(toolCall.Name);
            AddEstimate(toolCall.ArgumentsJson);
        }

        return total;

        void AddEstimate(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                total += _tokenEstimator.Estimate(value);
            }
        }
    }

    private List<ConversationRequestMessage> ApplyToolResultTruncation(
        IReadOnlyList<ConversationRequestMessage> messages,
        ContextBudget budget)
    {
        List<ConversationRequestMessage> normalized = [];
        foreach (ConversationRequestMessage message in messages)
        {
            if (!string.Equals(message.Role, "tool", StringComparison.Ordinal))
            {
                normalized.Add(message);
                continue;
            }

            normalized.Add(ConversationRequestMessage.ToolResult(
                message.ToolCallId ?? "tool_result",
                TruncateToolResultContent(
                    message.Content,
                    budget.ToolResultTokenLimit,
                    budget.ToolResultTruncationPolicy)));
        }

        return normalized;
    }

    private static int FractionOf(int total, double fraction)
    {
        return (int)Math.Round(total * fraction, MidpointRounding.AwayFromZero);
    }

    private static int ResolveToolResultTokenLimit(
        ModelContextMetadata? modelContextMetadata,
        int contextWindowTokens)
    {
        if (modelContextMetadata?.ToolOutputTokenLimit is > 0)
        {
            return modelContextMetadata.ToolOutputTokenLimit.Value;
        }

        if (modelContextMetadata?.ToolResultTruncationPolicy?.Limit is > 0)
        {
            return modelContextMetadata.ToolResultTruncationPolicy.Limit;
        }

        return Math.Min(32_768, Math.Max(2_048, contextWindowTokens / 10));
    }

    private string TrimTextToBudget(
        string? text,
        int budgetTokens,
        bool preserveTail = false)
    {
        if (string.IsNullOrWhiteSpace(text) || budgetTokens <= 0)
        {
            return string.Empty;
        }

        string normalized = text.Trim();
        if (_tokenEstimator.Estimate(normalized) <= budgetTokens)
        {
            return normalized;
        }

        const string ellipsis = "...";
        int low = 1;
        int high = normalized.Length;
        string best = string.Empty;
        while (low <= high)
        {
            int length = low + ((high - low) / 2);
            string slice = preserveTail
                ? normalized[^length..]
                : normalized[..length];
            string candidate = preserveTail
                ? ellipsis + slice
                : slice + ellipsis;

            if (_tokenEstimator.Estimate(candidate) <= budgetTokens)
            {
                best = candidate;
                low = length + 1;
            }
            else
            {
                high = length - 1;
            }
        }

        return best;
    }

    private static string NormalizeSummaryText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string normalized = string.Join(
            ' ',
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 240
            ? normalized
            : normalized[..237].TrimEnd() + "...";
    }

    private ConversationRequestMessage CreateToolResultMessage(
        ReplSessionContext session,
        string toolCallId,
        string content)
    {
        return ConversationRequestMessage.ToolResult(
            toolCallId,
            TruncateToolResultContent(session, content));
    }

    private string TruncateToolResultContent(
        ReplSessionContext session,
        string? content)
    {
        ContextBudget budget = CreateContextBudget(
            session.ActiveModelContextMetadata,
            session.ActiveModelContextWindowTokens ?? DefaultModelContextWindowTokens,
            toolDefinitionTokens: 0);
        return TruncateToolResultContent(
            content,
            budget.ToolResultTokenLimit,
            budget.ToolResultTruncationPolicy);
    }

    private string TruncateToolResultContent(
        string? content,
        int tokenLimit,
        ToolResultTruncationPolicy? truncationPolicy)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        string normalized = content.Trim();
        string? toolName = TryGetToolNameFromToolResult(normalized);
        if (!string.IsNullOrWhiteSpace(toolName) &&
            ToolOutputsProtectedFromPruning.Contains(toolName))
        {
            return normalized;
        }

        if (truncationPolicy is not null &&
            string.Equals(truncationPolicy.Mode, "bytes", StringComparison.OrdinalIgnoreCase))
        {
            return TrimTextToByteBudget(normalized, truncationPolicy.Limit);
        }

        return _tokenEstimator.Estimate(normalized) <= tokenLimit
            ? normalized
            : TrimTextToBudget(normalized, tokenLimit);
    }

    private static string TrimTextToByteBudget(string text, int byteLimit)
    {
        if (string.IsNullOrWhiteSpace(text) || byteLimit <= 0)
        {
            return string.Empty;
        }

        if (System.Text.Encoding.UTF8.GetByteCount(text) <= byteLimit)
        {
            return text;
        }

        const string ellipsis = "...";
        int low = 1;
        int high = text.Length;
        string best = string.Empty;
        while (low <= high)
        {
            int length = low + ((high - low) / 2);
            string candidate = text[..length] + ellipsis;
            if (System.Text.Encoding.UTF8.GetByteCount(candidate) <= byteLimit)
            {
                best = candidate;
                low = length + 1;
            }
            else
            {
                high = length - 1;
            }
        }

        return best;
    }

    private static string? TryGetStringProperty(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement property) &&
            property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }

    private static int? TryGetInt32Property(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out int value)
                ? value
                : null;
    }

    private static int? TryGetNullableInt32Property(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.Number when property.TryGetInt32(out int value) => value,
            _ => null
        };
    }

    private static double? TryGetDoubleProperty(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetDouble(out double value)
                ? value
                : null;
    }

    private static bool? TryGetBooleanProperty(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement property) &&
            (property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False)
                ? property.GetBoolean()
                : null;
    }

    private static IEnumerable<JsonElement> TryGetArrayProperty(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement property) &&
            property.ValueKind == JsonValueKind.Array
                ? property.EnumerateArray().ToArray()
                : [];
    }

    private static string[] TryGetStringArrayProperty(
        JsonElement element,
        string propertyName,
        int limit)
    {
        return TryGetArrayProperty(element, propertyName)
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Take(limit)
            .Cast<string>()
            .ToArray();
    }

    private static bool IsGitIndexPath(string? path)
    {
        return string.Equals(path, ".git/index", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, ".git\\index", StringComparison.OrdinalIgnoreCase);
    }

    private static string[] ExtractRelevantJsonExcerpts(JsonElement root, int limit)
    {
        if (!root.TryGetProperty("Data", out JsonElement data))
        {
            return [];
        }

        List<string> excerpts = [];
        foreach (JsonProperty property in data.EnumerateObject())
        {
            if (excerpts.Count >= limit)
            {
                break;
            }

            string value = property.Value.ValueKind switch
            {
                JsonValueKind.String => NormalizeSummaryText(property.Value.GetString()),
                JsonValueKind.Number => property.Value.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => string.Empty
            };

            if (!string.IsNullOrWhiteSpace(value))
            {
                excerpts.Add($"{property.Name}: {value}");
            }
        }

        return excerpts.ToArray();
    }

    private static bool IsToolResultProtectedFromPruning(ConversationRequestMessage message)
    {
        string? toolName = TryGetToolNameFromToolResult(message.Content);
        return !string.IsNullOrWhiteSpace(toolName) &&
            ToolOutputsProtectedFromPruning.Contains(toolName);
    }

    private static string? TryGetToolNameFromToolResult(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(content);
            return TryGetStringProperty(document.RootElement, "ToolName");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task EnsureBudgetAllowsProviderRequestAsync(
        ReplSessionContext session,
        CancellationToken cancellationToken)
    {
        BudgetControlsStatus status = await _budgetControlsUsageService.GetStatusAsync(
            session,
            cancellationToken);

        if (!status.Enabled ||
            status.MonthlyBudgetUsd is not decimal monthlyBudgetUsd ||
            monthlyBudgetUsd <= 0m ||
            status.SpentUsd < monthlyBudgetUsd)
        {
            return;
        }

        throw new ConversationPipelineException(
            "Budget controls blocked the provider request because recorded spend " +
            $"${status.SpentUsd.ToString("0.##", CultureInfo.InvariantCulture)} " +
            "has reached or exceeded the monthly budget of " +
            $"${monthlyBudgetUsd.ToString("0.##", CultureInfo.InvariantCulture)}.");
    }

    private async Task<string?> LoadProviderSecretAsync(
        ReplSessionContext session,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(session.ActiveProviderName))
        {
            string? providerSecret = await _secretStore.LoadAsync(
                session.ActiveProviderName,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(providerSecret))
            {
                return providerSecret;
            }
        }

        return await _secretStore.LoadAsync(cancellationToken) ??
            session.ProviderProfile.ProviderKind.GetDefaultApiKey();
    }

    private static ConversationResponseException CreateProviderOutputExhaustedException(
        ConversationResponseException lastException)
    {
        return new ConversationResponseException(
            $"The provider returned unusable output after {RetryableProviderOutputRetryLimit + 1} request(s). " +
            $"Last provider issue: {lastException.Message}",
            lastException);
    }

    private static ToolExecutionBatchResult? CreateBatchResult(
        IReadOnlyList<ToolInvocationResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        return results.Count == 0
            ? null
            : new ToolExecutionBatchResult(results.ToArray());
    }

    private IReadOnlyList<string> CreateToolOutputMessages(
        ToolExecutionBatchResult? batchResult,
        ReplSessionContext session)
    {
        return batchResult is null
            ? []
            : _toolOutputFormatter
                .FormatResults(batchResult)
                .Select(message => TruncateToolResultContent(session, message))
                .ToArray();
    }

    private ConversationFailureInfo CreateFailureInfo(
        ReplSessionContext session,
        Exception exception)
    {
        string category = exception switch
        {
            ConversationResponseException responseException when responseException.IsRetryableProviderOutput =>
                "provider_output_exhausted",
            ConversationResponseException => "provider_response_error",
            ConversationProviderException providerException when providerException.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase) =>
                "provider_timeout",
            ConversationProviderException => "provider_request_error",
            ConversationPipelineException => "conversation_pipeline_error",
            _ => "unexpected_error"
        };

        bool isRetryable = exception is ConversationResponseException response &&
            response.IsRetryableProviderOutput;

        return new ConversationFailureInfo(
            category,
            session.ProviderName,
            session.ActiveModelId,
            isRetryable);
    }

    private async Task PersistSessionStateAsync(
        ReplSessionContext session,
        CancellationToken cancellationToken)
    {
        if (_sectionService is null)
        {
            return;
        }

        await _sectionService.SaveIfDirtyAsync(session, cancellationToken);
    }

    private async Task RecordTurnStartedAsync(
        ReplSessionContext session,
        string turnId,
        int turnIndex,
        string input,
        CancellationToken cancellationToken)
    {
        if (_sessionEventLogService is null)
        {
            return;
        }

        try
        {
            await _sessionEventLogService.StartTurnAsync(
                session,
                turnId,
                turnIndex,
                input,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }
    }

    private async Task RecordTurnEndedAsync(
        ReplSessionContext session,
        string turnId,
        int turnIndex,
        string status,
        CancellationToken cancellationToken)
    {
        if (_sessionEventLogService is null)
        {
            return;
        }

        try
        {
            await _sessionEventLogService.EndTurnAsync(
                session,
                turnId,
                turnIndex,
                status,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }
    }

    private async Task RecordTurnEndedIgnoringErrorsAsync(
        ReplSessionContext session,
        string turnId,
        int turnIndex,
        string status)
    {
        try
        {
            await RecordTurnEndedAsync(
                session,
                turnId,
                turnIndex,
                status,
                CancellationToken.None);
        }
        catch
        {
        }
    }

    private async Task RecordStepStartedAsync(
        ReplSessionContext session,
        string turnId,
        string stepId,
        int stepIndex,
        CancellationToken cancellationToken)
    {
        if (_sessionEventLogService is null)
        {
            return;
        }

        try
        {
            await _sessionEventLogService.StartStepAsync(
                session,
                turnId,
                stepId,
                stepIndex,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }
    }

    private async Task RecordStepEndedAsync(
        ReplSessionContext session,
        string turnId,
        string stepId,
        int stepIndex,
        CancellationToken cancellationToken)
    {
        if (_sessionEventLogService is null)
        {
            return;
        }

        try
        {
            await _sessionEventLogService.EndStepAsync(
                session,
                turnId,
                stepId,
                stepIndex,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }
    }

    private async Task RecordPromptSnapshotAsync(
        ReplSessionContext session,
        string turnId,
        string stepId,
        int stepIndex,
        string modelRequestId,
        string? systemPrompt,
        IReadOnlyList<ConversationRequestMessage> messages,
        IReadOnlyList<ToolDefinition> availableTools,
        ConversationProviderRequest request,
        CancellationToken cancellationToken)
    {
        if (_sessionEventLogService is null)
        {
            return;
        }

        try
        {
            await _sessionEventLogService.RecordPromptSnapshotAsync(
                session,
                turnId,
                stepId,
                stepIndex,
                modelRequestId,
                systemPrompt,
                messages,
                availableTools,
                request,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }
    }

    private async Task RecordModelRequestStartedAsync(
        ReplSessionContext session,
        string turnId,
        string stepId,
        int stepIndex,
        string modelRequestId,
        ConversationProviderRequest request,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken)
    {
        if (_sessionEventLogService is null)
        {
            return;
        }

        try
        {
            await _sessionEventLogService.StartModelRequestAsync(
                session,
                turnId,
                stepId,
                stepIndex,
                modelRequestId,
                request,
                startedAtUtc,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }
    }

    private async Task RecordModelChunkAsync(
        ReplSessionContext session,
        string turnId,
        string stepId,
        int stepIndex,
        string modelRequestId,
        string text,
        DateTimeOffset timestampUtc,
        bool isFirstChunk,
        CancellationToken cancellationToken)
    {
        if (_sessionEventLogService is null)
        {
            return;
        }

        try
        {
            await _sessionEventLogService.RecordModelChunkAsync(
                session,
                turnId,
                stepId,
                stepIndex,
                modelRequestId,
                text,
                timestampUtc,
                isFirstChunk,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }
    }

    private async Task RecordModelRequestCompletedAsync(
        ReplSessionContext session,
        string turnId,
        string stepId,
        int stepIndex,
        ModelRequestExchange exchange,
        ConversationResponse? response,
        string status,
        Exception? exception,
        CancellationToken cancellationToken)
    {
        if (_sessionEventLogService is null)
        {
            return;
        }

        try
        {
            await _sessionEventLogService.CompleteModelRequestAsync(
                session,
                turnId,
                stepId,
                stepIndex,
                exchange.ModelRequestId,
                status,
                exchange.StartedAtUtc,
                exchange.FirstTokenAtUtc,
                exchange.CompletedAtUtc,
                exchange.Payload,
                response,
                exception,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }
    }

    private async Task RecordRetryAsync(
        ReplSessionContext session,
        string turnId,
        string stepId,
        int stepIndex,
        string modelRequestId,
        ProviderRetryProgress retryProgress,
        CancellationToken cancellationToken)
    {
        if (_sessionEventLogService is null)
        {
            return;
        }

        try
        {
            await _sessionEventLogService.RecordRetryAsync(
                session,
                turnId,
                stepId,
                stepIndex,
                modelRequestId,
                retryProgress,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }
    }

    private Task RecordProviderOutputRetryAsync(
        ReplSessionContext session,
        string turnId,
        string stepId,
        int stepIndex,
        int retryNumber,
        int maxRetries,
        Exception exception,
        CancellationToken cancellationToken)
    {
        return RecordRetryAsync(
            session,
            turnId,
            stepId,
            stepIndex,
            CreateStableTrajectoryId(turnId, "request", stepIndex, retryNumber),
            new ProviderRetryProgress(
                retryNumber,
                maxRetries,
                exception.Message),
            cancellationToken);
    }

    private async Task RecordCompactionIfChangedAsync(
        ReplSessionContext session,
        IReadOnlyList<ConversationRequestMessage> originalMessages,
        string? originalSystemPrompt,
        PreparedConversationRequest preparedRequest,
        CancellationToken cancellationToken)
    {
        if (_sessionEventLogService is null)
        {
            return;
        }

        int removedMessageCount = Math.Max(0, originalMessages.Count - preparedRequest.Messages.Count);
        int originalTokens = EstimatePromptTokens(originalSystemPrompt, originalMessages);
        int preparedTokens = EstimatePromptTokens(preparedRequest.SystemPrompt, preparedRequest.Messages);
        bool systemPromptChanged = !string.Equals(
            originalSystemPrompt?.Trim(),
            preparedRequest.SystemPrompt?.Trim(),
            StringComparison.Ordinal);
        int replacedTokens = Math.Max(0, originalTokens - preparedTokens);

        if (removedMessageCount == 0 && replacedTokens == 0 && !systemPromptChanged)
        {
            return;
        }

        string summary =
            $"Prepared request context compacted: removed {removedMessageCount.ToString(CultureInfo.InvariantCulture)} message(s), " +
            $"replaced about {replacedTokens.ToString(CultureInfo.InvariantCulture)} token(s).";

        try
        {
            await _sessionEventLogService.RecordCompactionAsync(
                session,
                summary,
                removedMessageCount,
                replacedTokens,
                rawOutput: null,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }
    }

    private int EstimatePromptTokens(
        string? systemPrompt,
        IReadOnlyList<ConversationRequestMessage> messages)
    {
        int total = string.IsNullOrWhiteSpace(systemPrompt)
            ? 0
            : _tokenEstimator.Estimate(systemPrompt);
        foreach (ConversationRequestMessage message in messages)
        {
            total += EstimateMessageTokens(message);
        }

        return total;
    }

    private static string CreateStableTrajectoryId(
        string turnId,
        string kind,
        int index,
        int? attempt = null)
    {
        string id = $"{turnId}-{kind}-{index.ToString(CultureInfo.InvariantCulture)}";
        return attempt is null
            ? id
            : id + "-" + attempt.Value.ToString(CultureInfo.InvariantCulture);
    }

    private async Task PersistSessionStateIgnoringErrorsAsync(
        ReplSessionContext session,
        CancellationToken cancellationToken)
    {
        try
        {
            await PersistSessionStateAsync(session, cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Failed to persist conversation session state during interrupted-turn recovery.");
        }
    }

    private sealed class DisabledLifecycleHookService : ILifecycleHookService
    {
        public static DisabledLifecycleHookService Instance { get; } = new();

        public Task<LifecycleHookRunResult> RunAsync(
            LifecycleHookContext context,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(context);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(LifecycleHookRunResult.Allowed());
        }
    }

    private sealed class DisabledSkillService : ISkillService
    {
        public static DisabledSkillService Instance { get; } = new();

        public Task<IReadOnlyList<WorkspaceSkillDescriptor>> ListAsync(
            ReplSessionContext session,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<WorkspaceSkillDescriptor>>([]);
        }

        public Task<string?> CreateRoutingPromptAsync(
            ReplSessionContext session,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<string?>(null);
        }

        public Task<WorkspaceSkillLoadResult?> LoadAsync(
            ReplSessionContext session,
            string name,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<WorkspaceSkillLoadResult?>(null);
        }
    }

    private sealed class DisabledWorkspaceAgentProfilePromptProvider : IWorkspaceAgentProfilePromptProvider
    {
        public static DisabledWorkspaceAgentProfilePromptProvider Instance { get; } = new();

        public Task<string?> LoadAsync(
            ReplSessionContext session,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<string?>(null);
        }
    }

    private sealed class DisabledBudgetControlsUsageService : IBudgetControlsUsageService
    {
        public static DisabledBudgetControlsUsageService Instance { get; } = new();

        public Task ConfigureLocalAsync(
            ReplSessionContext session,
            string? localPath,
            BudgetControlsLocalOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<BudgetControlsStatus> GetStatusAsync(
            ReplSessionContext session,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(BudgetControlsStatus.Disabled);
        }

        public Task RecordUsageAsync(
            ReplSessionContext session,
            BudgetControlsUsageDelta usage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class ConversationTelemetryAccumulator
    {
        public int EstimatedInputTokens { get; private set; }

        public int ProviderRetryCount { get; private set; }

        public int ToolRoundCount { get; private set; }

        public void AddEstimatedInputTokens(int estimatedInputTokens)
        {
            EstimatedInputTokens = AddClamped(EstimatedInputTokens, estimatedInputTokens);
            _lastInputTokenEstimate = Math.Max(0, estimatedInputTokens);
        }

        public void AddProviderRetryCount(int retryCount)
        {
            ProviderRetryCount = AddClamped(ProviderRetryCount, retryCount);
        }

        public void ReplaceLastEstimatedInputTokens(int? inputTokens)
        {
            if (inputTokens is not > 0)
            {
                return;
            }

            EstimatedInputTokens = Math.Max(0, EstimatedInputTokens - _lastInputTokenEstimate);
            AddEstimatedInputTokens(inputTokens.Value);
        }

        public void IncrementToolRoundCount()
        {
            ToolRoundCount = AddClamped(ToolRoundCount, 1);
        }

        private static int AddClamped(int current, int delta)
        {
            if (delta <= 0)
            {
                return current;
            }

            return current > int.MaxValue - delta
                ? int.MaxValue
                : current + delta;
        }

        private int _lastInputTokenEstimate;
    }

    private sealed record ModelRequestExchange(
        string ModelRequestId,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset? FirstTokenAtUtc,
        DateTimeOffset CompletedAtUtc,
        ConversationProviderPayload Payload);

    private sealed class StagnantActionTracker
    {
        private string? _lastSignature;
        private int _repeatCount;

        public bool Observe(
            IReadOnlyList<ConversationToolCall> toolCalls,
            IReadOnlyList<ToolInvocationResult> results,
            bool recordedFileEdits)
        {
            ArgumentNullException.ThrowIfNull(toolCalls);
            ArgumentNullException.ThrowIfNull(results);

            if (recordedFileEdits)
            {
                Reset();
                return false;
            }

            string signature = CreateSignature(toolCalls, results);
            if (string.Equals(signature, _lastSignature, StringComparison.Ordinal))
            {
                _repeatCount++;
            }
            else
            {
                _lastSignature = signature;
                _repeatCount = 1;
            }

            return _repeatCount >= StagnantActionReassessmentThreshold;
        }

        private void Reset()
        {
            _lastSignature = null;
            _repeatCount = 0;
        }

        private static string CreateSignature(
            IReadOnlyList<ConversationToolCall> toolCalls,
            IReadOnlyList<ToolInvocationResult> results)
        {
            using MemoryStream stream = new();
            using (Utf8JsonWriter writer = new(stream))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("ToolCalls");
                writer.WriteStartArray();
                foreach (ConversationToolCall toolCall in toolCalls)
                {
                    writer.WriteStartObject();
                    writer.WriteString("Name", toolCall.Name);
                    writer.WriteString("ArgumentsJson", NormalizeJson(toolCall.ArgumentsJson));
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WritePropertyName("Results");
                writer.WriteStartArray();
                foreach (ToolInvocationResult result in results)
                {
                    writer.WriteStartObject();
                    writer.WriteString("ToolName", result.ToolName);
                    writer.WriteString("Status", result.Result.Status.ToString());
                    writer.WriteBoolean("IsSuccess", result.Result.IsSuccess);
                    writer.WriteString("Message", result.Result.Message);
                    writer.WriteString("JsonResult", NormalizeJson(result.Result.JsonResult));
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            return Convert.ToBase64String(stream.ToArray());
        }

        private static string NormalizeJson(string json)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                using MemoryStream stream = new();
                using (Utf8JsonWriter writer = new(stream))
                {
                    WriteCanonicalJson(document.RootElement, writer);
                }

                return System.Text.Encoding.UTF8.GetString(stream.ToArray());
            }
            catch (JsonException)
            {
                return json.Trim();
            }
        }

        private static void WriteCanonicalJson(
            JsonElement element,
            Utf8JsonWriter writer)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (JsonProperty property in element.EnumerateObject()
                                 .OrderBy(static property => property.Name, StringComparer.Ordinal))
                    {
                        writer.WritePropertyName(property.Name);
                        WriteCanonicalJson(property.Value, writer);
                    }

                    writer.WriteEndObject();
                    break;

                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (JsonElement item in element.EnumerateArray())
                    {
                        WriteCanonicalJson(item, writer);
                    }

                    writer.WriteEndArray();
                    break;

                default:
                    element.WriteTo(writer);
                    break;
            }
        }
    }

    private sealed class CompletedAssistantTurn
    {
        public CompletedAssistantTurn(
            string userInput,
            string assistantResponse,
            IReadOnlyList<ConversationToolCall> toolCalls,
            ToolExecutionBatchResult? batchResult,
            string? assistantReasoningContent,
            string? assistantReasoningDetailsJson,
            ConversationTurnResult result)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(userInput);
            ArgumentException.ThrowIfNullOrWhiteSpace(assistantResponse);
            ArgumentNullException.ThrowIfNull(toolCalls);
            ArgumentNullException.ThrowIfNull(result);

            UserInput = userInput.Trim();
            AssistantResponse = assistantResponse.Trim();
            ToolCalls = toolCalls.ToArray();
            BatchResult = batchResult;
            AssistantReasoningContent = assistantReasoningContent;
            AssistantReasoningDetailsJson = assistantReasoningDetailsJson;
            Result = result;
        }

        public string UserInput { get; }

        public string AssistantResponse { get; }

        public IReadOnlyList<ConversationToolCall> ToolCalls { get; }

        public ToolExecutionBatchResult? BatchResult { get; }

        public string? AssistantReasoningContent { get; }

        public string? AssistantReasoningDetailsJson { get; }

        public ConversationTurnResult Result { get; }
    }

    private sealed class ExecutionPlanTracker
    {
        private readonly IReadOnlyList<string> _tasks;
        private int _completedTaskCount;

        private ExecutionPlanTracker(IReadOnlyList<string> tasks)
        {
            _tasks = tasks;
        }

        public static ExecutionPlanTracker? Create(IReadOnlyList<string> tasks)
        {
            ArgumentNullException.ThrowIfNull(tasks);

            return tasks.Count == 0
                ? null
                : new ExecutionPlanTracker(tasks.ToArray());
        }

        public ExecutionPlanProgress Advance()
        {
            if (_completedTaskCount < _tasks.Count)
            {
                _completedTaskCount++;
            }

            return CreateSnapshot();
        }

        public ExecutionPlanProgress Complete()
        {
            _completedTaskCount = _tasks.Count;
            return CreateSnapshot();
        }

        public ExecutionPlanProgress CreateSnapshot()
        {
            return new ExecutionPlanProgress(
                _tasks,
                _completedTaskCount);
        }
    }

    private sealed class PhaseExecutionResult
    {
        public PhaseExecutionResult(
            string assistantMessage,
            IReadOnlyList<ConversationToolCall> toolCalls,
            IReadOnlyList<ToolInvocationResult> executedToolResults,
            int totalCompletionTokens,
            bool hasReportedCompletionTokens,
            int estimatedInputTokens,
            int cachedInputTokens,
            int providerRetryCount,
            int toolRoundCount,
            string? assistantReasoningContent = null,
            string? assistantReasoningDetailsJson = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(assistantMessage);
            ArgumentNullException.ThrowIfNull(toolCalls);
            ArgumentNullException.ThrowIfNull(executedToolResults);

            AssistantMessage = assistantMessage.Trim();
            ToolCalls = toolCalls.ToArray();
            ExecutedToolResults = executedToolResults.ToArray();
            TotalCompletionTokens = totalCompletionTokens;
            HasReportedCompletionTokens = hasReportedCompletionTokens;
            EstimatedInputTokens = Math.Max(0, estimatedInputTokens);
            CachedInputTokens = Math.Max(0, cachedInputTokens);
            ProviderRetryCount = Math.Max(0, providerRetryCount);
            ToolRoundCount = Math.Max(0, toolRoundCount);
            AssistantReasoningContent = assistantReasoningContent;
            AssistantReasoningDetailsJson = assistantReasoningDetailsJson;
        }

        public string AssistantMessage { get; }

        public string? AssistantReasoningContent { get; }

        public string? AssistantReasoningDetailsJson { get; }

        public IReadOnlyList<ToolInvocationResult> ExecutedToolResults { get; }

        public int CachedInputTokens { get; }

        public int EstimatedInputTokens { get; }

        public bool HasReportedCompletionTokens { get; }

        public int ProviderRetryCount { get; }

        public IReadOnlyList<ConversationToolCall> ToolCalls { get; }

        public int ToolRoundCount { get; }

        public int TotalCompletionTokens { get; }
    }
}
