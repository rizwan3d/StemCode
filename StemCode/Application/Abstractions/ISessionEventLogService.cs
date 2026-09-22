using StemCode.Application.Models;

namespace StemCode.Application.Abstractions;

public interface ISessionEventLogService
{
    string GetStoragePath(string sectionId);

    Task<IReadOnlyList<SessionEventRecord>> GetTrajectoryAsync(
        string sectionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<SessionEventRecord>>([]);
    }

    Task StartSessionAsync(
        ReplSessionContext session,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    Task StartTurnAsync(
        ReplSessionContext session,
        string turnId,
        int turnIndex,
        string userInput,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    Task StartStepAsync(
        ReplSessionContext session,
        string turnId,
        string stepId,
        int stepIndex,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    Task RecordPromptSnapshotAsync(
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
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    Task StartModelRequestAsync(
        ReplSessionContext session,
        string turnId,
        string stepId,
        int stepIndex,
        string modelRequestId,
        ConversationProviderRequest request,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    Task RecordModelChunkAsync(
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
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    Task CompleteModelRequestAsync(
        ReplSessionContext session,
        string turnId,
        string stepId,
        int stepIndex,
        string modelRequestId,
        string status,
        DateTimeOffset startedAtUtc,
        DateTimeOffset? firstTokenAtUtc,
        DateTimeOffset completedAtUtc,
        ConversationProviderPayload? payload,
        ConversationResponse? response,
        Exception? exception,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    Task StartToolCallAsync(
        ReplSessionContext session,
        ConversationToolCall toolCall,
        DateTimeOffset startedAtUtc,
        string? parentCallId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    Task CompleteToolCallAsync(
        ReplSessionContext session,
        ConversationToolCall toolCall,
        ToolInvocationResult invocationResult,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        string? parentCallId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    Task RecordRetryAsync(
        ReplSessionContext session,
        string? turnId,
        string? stepId,
        int? stepIndex,
        string? modelRequestId,
        ProviderRetryProgress retryProgress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    Task RecordCompactionAsync(
        ReplSessionContext session,
        string summary,
        int itemsReplaced,
        int? tokensReplaced,
        string? rawOutput,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    Task EndStepAsync(
        ReplSessionContext session,
        string turnId,
        string stepId,
        int stepIndex,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    Task EndTurnAsync(
        ReplSessionContext session,
        string turnId,
        int turnIndex,
        string status,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    Task EndSessionAsync(
        ReplSessionContext session,
        string status,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    Task RecordUserInputAsync(
        ReplSessionContext session,
        string input,
        CancellationToken cancellationToken);

    Task RecordAssistantReasoningAsync(
        ReplSessionContext session,
        string reasoningText,
        CancellationToken cancellationToken);

    Task RecordAssistantOutputAsync(
        ReplSessionContext session,
        string outputText,
        CancellationToken cancellationToken);

    Task RecordToolCallRequestedAsync(
        ReplSessionContext session,
        ConversationToolCall toolCall,
        CancellationToken cancellationToken);

    Task RecordToolResultAsync(
        ReplSessionContext session,
        ToolInvocationResult invocationResult,
        CancellationToken cancellationToken);

    Task RecordExecutionPlanAsync(
        ReplSessionContext session,
        ExecutionPlanProgress executionPlanProgress,
        CancellationToken cancellationToken);

    Task RecordTurnFailureAsync(
        ReplSessionContext session,
        string input,
        Exception exception,
        CancellationToken cancellationToken);
}
