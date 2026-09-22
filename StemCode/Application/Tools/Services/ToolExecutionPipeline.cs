using StemCode.Application.Abstractions;
using StemCode.Application.Models;

namespace StemCode.Application.Tools.Services;

internal sealed class ToolExecutionPipeline : IStreamingToolExecutionPipeline
{
    private const int DefaultMaxParallelToolExecutions = 4;

    private readonly IToolAuditLogService? _toolAuditLogService;
    private readonly ISessionEventLogService? _sessionEventLogService;
    private readonly IProductTelemetry? _telemetry;
    private readonly ILessonMemoryService? _lessonMemoryService;
    private readonly int _maxParallelToolExecutions;
    private readonly TimeProvider _timeProvider;
    private readonly IToolInvoker _toolInvoker;
    private readonly IWorkspaceFileService _workspaceFileService;

    public ToolExecutionPipeline(
        IWorkspaceFileService workspaceFileService,
        IToolInvoker toolInvoker,
        ILessonMemoryService? lessonMemoryService = null,
        IToolAuditLogService? toolAuditLogService = null,
        ISessionEventLogService? sessionEventLogService = null,
        IProductTelemetry? telemetry = null,
        TimeProvider? timeProvider = null,
        int maxParallelToolExecutions = DefaultMaxParallelToolExecutions)
    {
        _workspaceFileService = workspaceFileService;
        _toolInvoker = toolInvoker;
        _lessonMemoryService = lessonMemoryService;
        _toolAuditLogService = toolAuditLogService;
        _sessionEventLogService = sessionEventLogService;
        _telemetry = telemetry;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _maxParallelToolExecutions = Math.Max(1, maxParallelToolExecutions);
    }

    public Task<ToolExecutionBatchResult> ExecuteAsync(
        IReadOnlyList<ConversationToolCall> toolCalls,
        ReplSessionContext session,
        ConversationExecutionPhase executionPhase,
        IReadOnlySet<string> allowedToolNames,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            toolCalls,
            session,
            executionPhase,
            allowedToolNames,
            cancellationToken,
            onToolResult: null);
    }

    public async Task<ToolExecutionBatchResult> ExecuteAsync(
        IReadOnlyList<ConversationToolCall> toolCalls,
        ReplSessionContext session,
        ConversationExecutionPhase executionPhase,
        IReadOnlySet<string> allowedToolNames,
        CancellationToken cancellationToken,
        Func<ToolInvocationResult, CancellationToken, Task>? onToolResult)
    {
        ArgumentNullException.ThrowIfNull(toolCalls);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(allowedToolNames);
        cancellationToken.ThrowIfCancellationRequested();

        if (toolCalls.Count == 0)
        {
            return new ToolExecutionBatchResult([]);
        }

        ToolInvocationResult?[] results = new ToolInvocationResult?[toolCalls.Count];
        using (session.BeginFileEditTransactionBatch())
        {
            int index = 0;
            while (index < toolCalls.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!CanRunInParallel(toolCalls[index]))
                {
                    ToolExecutionRecord record = await InvokeToolAsync(
                        toolCalls[index],
                        session,
                        executionPhase,
                        allowedToolNames,
                        cancellationToken);
                    results[index] = record.InvocationResult;
                    await CompleteToolExecutionAsync(
                        record,
                        session,
                        executionPhase,
                        onToolResult,
                        cancellationToken);
                    index++;
                    continue;
                }

                int groupStartIndex = index;
                while (index < toolCalls.Count &&
                       CanRunInParallel(toolCalls[index]))
                {
                    index++;
                }

                await ExecuteParallelGroupAsync(
                    toolCalls,
                    groupStartIndex,
                    index,
                    session,
                    executionPhase,
                    allowedToolNames,
                    results,
                    onToolResult,
                    cancellationToken);
            }
        }

        _workspaceFileService.ReleaseTrackedFileEditTransactions(session.DrainExpiredFileEditTransactions());

        return new ToolExecutionBatchResult(results.Select(static result =>
            result ?? throw new InvalidOperationException("Tool execution completed without a result.")).ToArray());
    }

    private async Task ExecuteParallelGroupAsync(
        IReadOnlyList<ConversationToolCall> toolCalls,
        int startIndex,
        int endIndex,
        ReplSessionContext session,
        ConversationExecutionPhase executionPhase,
        IReadOnlySet<string> allowedToolNames,
        ToolInvocationResult?[] results,
        Func<ToolInvocationResult, CancellationToken, Task>? onToolResult,
        CancellationToken cancellationToken)
    {
        int groupCount = endIndex - startIndex;
        if (groupCount <= 1)
        {
            ToolExecutionRecord record = await InvokeToolAsync(
                toolCalls[startIndex],
                session,
                executionPhase,
                allowedToolNames,
                cancellationToken);
            results[startIndex] = record.InvocationResult;
            await CompleteToolExecutionAsync(
                record,
                session,
                executionPhase,
                onToolResult,
                cancellationToken);
            return;
        }

        using SemaphoreSlim concurrency = new(_maxParallelToolExecutions, _maxParallelToolExecutions);
        List<Task<IndexedToolExecutionRecord>> pendingTasks = new(groupCount);
        for (int index = startIndex; index < endIndex; index++)
        {
            int toolIndex = index;
            pendingTasks.Add(InvokeParallelToolAsync(
                toolCalls[toolIndex],
                toolIndex,
                session,
                executionPhase,
                allowedToolNames,
                concurrency,
                cancellationToken));
        }

        while (pendingTasks.Count > 0)
        {
            Task<IndexedToolExecutionRecord> completedTask = await Task.WhenAny(pendingTasks);
            pendingTasks.Remove(completedTask);
            IndexedToolExecutionRecord indexedRecord = await completedTask;
            results[indexedRecord.Index] = indexedRecord.Record.InvocationResult;
            await CompleteToolExecutionAsync(
                indexedRecord.Record,
                session,
                executionPhase,
                onToolResult,
                cancellationToken);
        }
    }

    private async Task<IndexedToolExecutionRecord> InvokeParallelToolAsync(
        ConversationToolCall toolCall,
        int index,
        ReplSessionContext session,
        ConversationExecutionPhase executionPhase,
        IReadOnlySet<string> allowedToolNames,
        SemaphoreSlim concurrency,
        CancellationToken cancellationToken)
    {
        await concurrency.WaitAsync(cancellationToken);
        try
        {
            return new IndexedToolExecutionRecord(
                index,
                await InvokeToolAsync(
                    toolCall,
                    session,
                    executionPhase,
                    allowedToolNames,
                    cancellationToken));
        }
        finally
        {
            concurrency.Release();
        }
    }

    private async Task<ToolExecutionRecord> InvokeToolAsync(
        ConversationToolCall toolCall,
        ReplSessionContext session,
        ConversationExecutionPhase executionPhase,
        IReadOnlySet<string> allowedToolNames,
        CancellationToken cancellationToken)
    {
        DateTimeOffset startedAtUtc = _timeProvider.GetUtcNow();
        await RecordToolCallStartedAsync(
            toolCall,
            session,
            startedAtUtc,
            cancellationToken);

        try
        {
            ToolInvocationResult result = await _toolInvoker.InvokeAsync(
                toolCall,
                session,
                executionPhase,
                allowedToolNames,
                cancellationToken);
            DateTimeOffset completedAtUtc = _timeProvider.GetUtcNow();
            await RecordToolCallCompletedAsync(
                toolCall,
                result,
                session,
                startedAtUtc,
                completedAtUtc,
                cancellationToken);

            return new ToolExecutionRecord(
                toolCall,
                result,
                startedAtUtc,
                completedAtUtc);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            DateTimeOffset completedAtUtc = _timeProvider.GetUtcNow();
            ToolInvocationResult failedResult = new(
                toolCall.Id,
                toolCall.Name,
                ToolResult.ExecutionError(
                    exception.Message,
                    "{\"error\":\"tool invocation failed\"}"),
                toolNameRecognized: allowedToolNames.Contains(toolCall.Name));
            await RecordToolCallCompletedAsync(
                toolCall,
                failedResult,
                session,
                startedAtUtc,
                completedAtUtc,
                cancellationToken);
            throw;
        }
    }

    private async Task CompleteToolExecutionAsync(
        ToolExecutionRecord record,
        ReplSessionContext session,
        ConversationExecutionPhase executionPhase,
        Func<ToolInvocationResult, CancellationToken, Task>? onToolResult,
        CancellationToken cancellationToken)
    {
        await RecordToolAuditAsync(
            record.ToolCall,
            record.InvocationResult,
            session,
            executionPhase,
            record.StartedAtUtc,
            record.CompletedAtUtc,
            cancellationToken);
        RecordToolTelemetry(record, session, executionPhase);
        await ObserveLessonMemoryAsync(
            record.ToolCall,
            record.InvocationResult,
            session,
            cancellationToken);

        if (onToolResult is not null)
        {
            await onToolResult(record.InvocationResult, cancellationToken);
        }
    }

    private async Task RecordToolCallStartedAsync(
        ConversationToolCall toolCall,
        ReplSessionContext session,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken)
    {
        if (_sessionEventLogService is null)
        {
            return;
        }

        try
        {
            await _sessionEventLogService.StartToolCallAsync(
                session,
                toolCall,
                startedAtUtc,
                parentCallId: null,
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

    private async Task RecordToolCallCompletedAsync(
        ConversationToolCall toolCall,
        ToolInvocationResult result,
        ReplSessionContext session,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken)
    {
        if (_sessionEventLogService is null)
        {
            return;
        }

        try
        {
            await _sessionEventLogService.CompleteToolCallAsync(
                session,
                toolCall,
                result,
                startedAtUtc,
                completedAtUtc,
                parentCallId: null,
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

    private static bool CanRunInParallel(ConversationToolCall toolCall)
    {
        return toolCall.Name is AgentToolNames.FileRead or
            AgentToolNames.DirectoryList or
            AgentToolNames.SearchFiles or
            AgentToolNames.TextSearch or
            AgentToolNames.WebSearch or
            AgentToolNames.SkillLoad;
    }

    private async Task RecordToolAuditAsync(
        ConversationToolCall toolCall,
        ToolInvocationResult result,
        ReplSessionContext session,
        ConversationExecutionPhase executionPhase,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken)
    {
        if (_toolAuditLogService is null)
        {
            return;
        }

        try
        {
            await _toolAuditLogService.RecordAsync(
                toolCall,
                result,
                session,
                executionPhase,
                startedAtUtc,
                completedAtUtc,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Audit logs are useful operational evidence, but a log write issue should
            // not turn a completed tool call into a failed agent turn.
        }
    }

    private void RecordToolTelemetry(
        ToolExecutionRecord record,
        ReplSessionContext session,
        ConversationExecutionPhase executionPhase)
    {
        if (_telemetry is null)
        {
            return;
        }

        try
        {
            ToolResult result = record.InvocationResult.Result;
            string telemetryToolName = record.InvocationResult.ToolNameRecognized
                ? record.ToolCall.Name
                : "unknown";
            _telemetry.TrackToolInvoked(
                telemetryToolName,
                result.Status,
                result.IsSuccess,
                record.CompletedAtUtc - record.StartedAtUtc,
                executionPhase,
                modelId: session.ActiveModelId,
                providerName: session.ProviderName,
                errorMessage: result.IsSuccess ? null : result.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Telemetry must never affect tool execution behavior.
        }
    }

    private async Task ObserveLessonMemoryAsync(
        ConversationToolCall toolCall,
        ToolInvocationResult result,
        ReplSessionContext session,
        CancellationToken cancellationToken)
    {
        if (_lessonMemoryService is null)
        {
            return;
        }

        try
        {
            await _lessonMemoryService.ObserveToolResultAsync(
                toolCall,
                result,
                cancellationToken,
                session);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Automatic lesson observation is helpful, but it should never break the tool round.
        }
    }

    private sealed record ToolExecutionRecord(
        ConversationToolCall ToolCall,
        ToolInvocationResult InvocationResult,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset CompletedAtUtc);

    private sealed record IndexedToolExecutionRecord(
        int Index,
        ToolExecutionRecord Record);
}
