using StemCode.Application.Abstractions;
using StemCode.Application.Models;
using StemCode.Application.Utilities;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace StemCode.Infrastructure.Storage;

internal sealed class JsonSessionEventLogService : ISessionEventLogService
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private readonly ConcurrentDictionary<string, long> _eventSequences = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _timeProvider;
    private readonly IUserDataPathProvider _userDataPathProvider;

    public JsonSessionEventLogService(
        IUserDataPathProvider userDataPathProvider,
        TimeProvider? timeProvider = null)
    {
        _userDataPathProvider = userDataPathProvider;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string GetStoragePath(string sectionId)
    {
        string normalizedSectionId = NormalizeSectionId(sectionId);
        return Path.Combine(
            _userDataPathProvider.GetSessionsDirectoryPath(),
            $"{normalizedSectionId}.events.jsonl");
    }

    public async Task<IReadOnlyList<SessionEventRecord>> GetTrajectoryAsync(
        string sectionId,
        CancellationToken cancellationToken)
    {
        string storagePath = GetStoragePath(sectionId);
        if (!File.Exists(storagePath))
        {
            return [];
        }

        List<SessionEventRecord> records = [];
        await foreach (string line in File.ReadLinesAsync(storagePath, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                SessionEventRecord? record = JsonSerializer.Deserialize(
                    line,
                    SessionEventLogJsonContext.Default.SessionEventRecord);
                if (record is not null)
                {
                    records.Add(record);
                }
            }
            catch (JsonException)
            {
            }
        }

        return records
            .OrderBy(static record => record.EventSequenceId ?? long.MaxValue)
            .ThenBy(static record => record.TimestampUtc)
            .ToArray();
    }

    public Task StartSessionAsync(
        ReplSessionContext session,
        CancellationToken cancellationToken)
    {
        return AppendSafeAsync(
            CreateRecord(
                session,
                "session_started",
                status: "running",
                summary: "Session recording started."),
            cancellationToken);
    }

    public Task StartTurnAsync(
        ReplSessionContext session,
        string turnId,
        int turnIndex,
        string userInput,
        CancellationToken cancellationToken)
    {
        return AppendSafeAsync(
            CreateRecord(
                session,
                "turn_started",
                text: userInput,
                turnId: turnId,
                turnIndex: turnIndex,
                status: "running"),
            cancellationToken);
    }

    public Task StartStepAsync(
        ReplSessionContext session,
        string turnId,
        string stepId,
        int stepIndex,
        CancellationToken cancellationToken)
    {
        return AppendSafeAsync(
            CreateRecord(
                session,
                "step_started",
                turnId: turnId,
                stepId: stepId,
                stepIndex: stepIndex,
                status: "running"),
            cancellationToken);
    }

    public Task RecordPromptSnapshotAsync(
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
        return AppendSafeAsync(
            CreateRecord(
                session,
                "prompt_snapshot",
                turnId: turnId,
                stepId: stepId,
                stepIndex: stepIndex,
                modelRequestId: modelRequestId,
                source: "agent_runtime",
                systemPrompt: systemPrompt,
                toolsJson: SerializeTools(availableTools, includeSchemas: false),
                toolSchemaJson: SerializeTools(availableTools, includeSchemas: true),
                requestOptionsJson: SerializeRequestOptions(request),
                inputJson: SerializeMessages(messages),
                rawJson: SerializePromptSnapshot(systemPrompt, messages, availableTools, request)),
            cancellationToken);
    }

    public Task StartModelRequestAsync(
        ReplSessionContext session,
        string turnId,
        string stepId,
        int stepIndex,
        string modelRequestId,
        ConversationProviderRequest request,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken)
    {
        return AppendSafeAsync(
            CreateRecord(
                session,
                "model_request_started",
                turnId: turnId,
                stepId: stepId,
                stepIndex: stepIndex,
                modelRequestId: modelRequestId,
                requestOptionsJson: SerializeRequestOptions(request),
                status: "running",
                requestStartedAtUtc: startedAtUtc),
            cancellationToken);
    }

    public Task RecordModelChunkAsync(
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
        return AppendSafeAsync(
            CreateRecord(
                session,
                isFirstChunk ? "model_first_token" : "assistant_output_delta",
                text: text,
                turnId: turnId,
                stepId: stepId,
                stepIndex: stepIndex,
                modelRequestId: modelRequestId,
                status: "streaming",
                isPartial: true,
                firstTokenAtUtc: isFirstChunk ? timestampUtc : null),
            cancellationToken);
    }

    public Task CompleteModelRequestAsync(
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
        int? inputTokens = response?.PromptTokens ??
            (response?.TotalTokens is > 0 && response?.CompletionTokens is > 0
                ? response.TotalTokens.Value - response.CompletionTokens.Value
                : null);
        TimeSpan totalDuration = completedAtUtc - startedAtUtc;
        TimeSpan? ttft = firstTokenAtUtc is null ? null : firstTokenAtUtc.Value - startedAtUtc;
        TimeSpan? generation = firstTokenAtUtc is null ? null : completedAtUtc - firstTokenAtUtc.Value;
        double? tokensPerSecond = response?.CompletionTokens is > 0 && generation is { TotalSeconds: > 0 }
            ? response.CompletionTokens.Value / generation.Value.TotalSeconds
            : null;

        return AppendSafeAsync(
            CreateRecord(
                session,
                "model_request_completed",
                text: response?.AssistantMessage,
                turnId: turnId,
                stepId: stepId,
                stepIndex: stepIndex,
                modelRequestId: modelRequestId,
                outputJson: SerializeResponse(response, payload),
                thinkingJson: response?.ReasoningDetailsJson ?? response?.ReasoningContent,
                rawJson: payload?.RawContent,
                status: status,
                errorType: exception?.GetType().FullName,
                errorCode: exception is null ? null : exception.GetType().Name,
                inputTokens: inputTokens,
                cacheReadTokens: response?.CachedPromptTokens,
                outputTokens: response?.CompletionTokens,
                requestStartedAtUtc: startedAtUtc,
                firstTokenAtUtc: firstTokenAtUtc,
                completedAtUtc: completedAtUtc,
                totalDurationMs: totalDuration.TotalMilliseconds,
                ttftMs: ttft?.TotalMilliseconds,
                generationDurationMs: generation?.TotalMilliseconds,
                tokensPerSecond: tokensPerSecond,
                isRunning: false,
                interrupted: string.Equals(status, "interrupted", StringComparison.OrdinalIgnoreCase)),
            cancellationToken);
    }

    public Task StartToolCallAsync(
        ReplSessionContext session,
        ConversationToolCall toolCall,
        DateTimeOffset startedAtUtc,
        string? parentCallId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        return AppendSafeAsync(
            CreateRecord(
                session,
                "tool_call_started",
                toolCallId: toolCall.Id,
                toolName: toolCall.Name,
                toolArgumentsJson: toolCall.ArgumentsJson,
                parentCallId: parentCallId,
                status: "running",
                requestStartedAtUtc: startedAtUtc,
                isRunning: true),
            cancellationToken);
    }

    public Task CompleteToolCallAsync(
        ReplSessionContext session,
        ConversationToolCall toolCall,
        ToolInvocationResult invocationResult,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        string? parentCallId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        ArgumentNullException.ThrowIfNull(invocationResult);

        TimeSpan duration = completedAtUtc - startedAtUtc;
        return AppendSafeAsync(
            CreateRecord(
                session,
                "tool_call_completed",
                toolCallId: invocationResult.ToolCallId,
                toolName: invocationResult.ToolName,
                toolArgumentsJson: toolCall.ArgumentsJson,
                toolStatus: invocationResult.Result.Status.ToString(),
                toolMessage: invocationResult.Result.Message,
                toolResultJson: invocationResult.Result.JsonResult,
                toolResultId: CreateStableId("tool-result", invocationResult.ToolCallId),
                parentCallId: parentCallId,
                status: invocationResult.Result.IsSuccess ? "succeeded" : "failed",
                errorType: invocationResult.Result.IsSuccess ? null : invocationResult.Result.Status.ToString(),
                requestStartedAtUtc: startedAtUtc,
                completedAtUtc: completedAtUtc,
                totalDurationMs: duration.TotalMilliseconds,
                isRunning: false),
            cancellationToken);
    }

    public Task RecordRetryAsync(
        ReplSessionContext session,
        string? turnId,
        string? stepId,
        int? stepIndex,
        string? modelRequestId,
        ProviderRetryProgress retryProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(retryProgress);

        return AppendSafeAsync(
            CreateRecord(
                session,
                "model_request_retry",
                text: retryProgress.Reason,
                turnId: turnId,
                stepId: stepId,
                stepIndex: stepIndex,
                modelRequestId: modelRequestId,
                status: "retrying",
                errorCode: retryProgress.Reason,
                retryNumber: retryProgress.Attempt,
                maxRetries: retryProgress.MaxAttempts),
            cancellationToken);
    }

    public Task RecordCompactionAsync(
        ReplSessionContext session,
        string summary,
        int itemsReplaced,
        int? tokensReplaced,
        string? rawOutput,
        CancellationToken cancellationToken)
    {
        return AppendSafeAsync(
            CreateRecord(
                session,
                "context_compaction",
                text: summary,
                rawJson: rawOutput,
                summary: summary,
                itemsReplaced: itemsReplaced,
                tokensReplaced: tokensReplaced),
            cancellationToken);
    }

    public Task EndStepAsync(
        ReplSessionContext session,
        string turnId,
        string stepId,
        int stepIndex,
        CancellationToken cancellationToken)
    {
        return AppendSafeAsync(
            CreateRecord(
                session,
                "step_completed",
                turnId: turnId,
                stepId: stepId,
                stepIndex: stepIndex,
                status: "completed"),
            cancellationToken);
    }

    public Task EndTurnAsync(
        ReplSessionContext session,
        string turnId,
        int turnIndex,
        string status,
        CancellationToken cancellationToken)
    {
        return AppendSafeAsync(
            CreateRecord(
                session,
                "turn_completed",
                turnId: turnId,
                turnIndex: turnIndex,
                status: status),
            cancellationToken);
    }

    public Task EndSessionAsync(
        ReplSessionContext session,
        string status,
        CancellationToken cancellationToken)
    {
        return AppendSafeAsync(
            CreateRecord(
                session,
                "session_completed",
                status: status),
            cancellationToken);
    }

    public Task RecordUserInputAsync(
        ReplSessionContext session,
        string input,
        CancellationToken cancellationToken)
    {
        return AppendSafeAsync(
            CreateRecord(
                session,
                "user_input",
                text: input),
            cancellationToken);
    }

    public Task RecordAssistantReasoningAsync(
        ReplSessionContext session,
        string reasoningText,
        CancellationToken cancellationToken)
    {
        return AppendSafeAsync(
            CreateRecord(
                session,
                "assistant_reasoning",
                text: reasoningText),
            cancellationToken);
    }

    public Task RecordAssistantOutputAsync(
        ReplSessionContext session,
        string outputText,
        CancellationToken cancellationToken)
    {
        return AppendSafeAsync(
            CreateRecord(
                session,
                "assistant_output",
                text: outputText),
            cancellationToken);
    }

    public Task RecordToolCallRequestedAsync(
        ReplSessionContext session,
        ConversationToolCall toolCall,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        return AppendSafeAsync(
            CreateRecord(
                session,
                "assistant_tool_call_request",
                toolCallId: toolCall.Id,
                toolName: toolCall.Name,
                toolArgumentsJson: toolCall.ArgumentsJson),
            cancellationToken);
    }

    public Task RecordToolResultAsync(
        ReplSessionContext session,
        ToolInvocationResult invocationResult,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocationResult);

        return AppendSafeAsync(
            CreateRecord(
                session,
                "tool_call_response",
                toolCallId: invocationResult.ToolCallId,
                toolName: invocationResult.ToolName,
                toolStatus: invocationResult.Result.Status.ToString(),
                toolMessage: invocationResult.Result.Message,
                toolResultJson: invocationResult.Result.JsonResult),
            cancellationToken);
    }

    public Task RecordExecutionPlanAsync(
        ReplSessionContext session,
        ExecutionPlanProgress executionPlanProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(executionPlanProgress);

        string text = executionPlanProgress.Tasks.Count == 0
            ? "Execution plan is empty."
            : $"Completed {executionPlanProgress.CompletedTaskCount} of {executionPlanProgress.Tasks.Count}: " +
              string.Join(" | ", executionPlanProgress.Tasks);

        return AppendSafeAsync(
            CreateRecord(
                session,
                "execution_plan",
                text: text),
            cancellationToken);
    }

    public Task RecordTurnFailureAsync(
        ReplSessionContext session,
        string input,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exception);

        string text = string.IsNullOrWhiteSpace(input)
            ? exception.Message
            : $"Input: {input}{Environment.NewLine}Error: {exception.Message}";

        return AppendSafeAsync(
            CreateRecord(
                session,
                "turn_failed",
                text: text,
                errorType: exception.GetType().FullName),
            cancellationToken);
    }

    private async Task AppendSafeAsync(
        SessionEventRecord record,
        CancellationToken cancellationToken)
    {
        try
        {
            await AppendAsync(record, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Event logging is best-effort and should not break the active turn.
        }
    }

    private async Task AppendAsync(
        SessionEventRecord record,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string json = JsonSerializer.Serialize(
            record,
            SessionEventLogJsonContext.Default.SessionEventRecord);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            string storagePath = GetStoragePath(record.SectionId);
            EnsureStorageDirectory(storagePath);

            await using FileStream stream = new(
                storagePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous);
            await using StreamWriter writer = new(stream, Utf8NoBom);
            await writer.WriteLineAsync(json.AsMemory(), cancellationToken);
            await writer.FlushAsync(cancellationToken);
            FilePermissionHelper.EnsurePrivateFile(storagePath);
        }
        finally
        {
            _gate.Release();
        }
    }

    private SessionEventRecord CreateRecord(
        ReplSessionContext session,
        string eventType,
        string? text = null,
        string? toolCallId = null,
        string? toolName = null,
        string? toolArgumentsJson = null,
        string? toolStatus = null,
        string? toolMessage = null,
        string? toolResultJson = null,
        string? errorType = null,
        string? turnId = null,
        int? turnIndex = null,
        string? stepId = null,
        int? stepIndex = null,
        string? modelRequestId = null,
        string? assistantMessageId = null,
        string? toolResultId = null,
        string? parentCallId = null,
        string? source = null,
        string? systemPrompt = null,
        string? toolsJson = null,
        string? toolSchemaJson = null,
        string? requestOptionsJson = null,
        string? inputJson = null,
        string? outputJson = null,
        string? thinkingJson = null,
        string? rawJson = null,
        string? status = null,
        string? errorCode = null,
        int? retryNumber = null,
        int? maxRetries = null,
        int? retryDelayMs = null,
        int? inputTokens = null,
        int? cacheReadTokens = null,
        int? cacheWriteTokens = null,
        int? outputTokens = null,
        int? reasoningTokens = null,
        DateTimeOffset? requestStartedAtUtc = null,
        DateTimeOffset? firstTokenAtUtc = null,
        DateTimeOffset? completedAtUtc = null,
        double? totalDurationMs = null,
        double? ttftMs = null,
        double? generationDurationMs = null,
        double? tokensPerSecond = null,
        bool? isPartial = null,
        bool? isRunning = null,
        bool? interrupted = null,
        string? summary = null,
        int? itemsReplaced = null,
        int? tokensReplaced = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);

        long sequenceId = _eventSequences.AddOrUpdate(
            session.SectionId,
            1,
            static (_, current) => current + 1);

        return new SessionEventRecord(
            _timeProvider.GetUtcNow(),
            session.SectionId,
            session.ParentSessionId,
            eventType.Trim(),
            session.AgentProfileName,
            session.ActiveModelId,
            session.WorkingDirectory,
            NormalizeText(text),
            NormalizeText(toolCallId),
            NormalizeText(toolName),
            NormalizeJsonText(toolArgumentsJson),
            NormalizeText(toolStatus),
            NormalizeText(toolMessage),
            NormalizeJsonText(toolResultJson),
            NormalizeText(errorType),
            sequenceId,
            NormalizeText(turnId),
            turnIndex,
            NormalizeText(stepId),
            stepIndex,
            NormalizeText(modelRequestId),
            NormalizeText(assistantMessageId),
            NormalizeText(toolResultId),
            NormalizeText(parentCallId),
            NormalizeText(source),
            NormalizeText(systemPrompt),
            NormalizeJsonText(toolsJson),
            NormalizeJsonText(toolSchemaJson),
            NormalizeJsonText(requestOptionsJson),
            NormalizeJsonText(inputJson),
            NormalizeJsonText(outputJson),
            NormalizeJsonText(thinkingJson),
            NormalizeText(rawJson),
            NormalizeText(status),
            NormalizeText(errorCode),
            retryNumber,
            maxRetries,
            retryDelayMs,
            inputTokens,
            cacheReadTokens,
            cacheWriteTokens,
            outputTokens,
            reasoningTokens,
            requestStartedAtUtc,
            firstTokenAtUtc,
            completedAtUtc,
            totalDurationMs,
            ttftMs,
            generationDurationMs,
            tokensPerSecond,
            isPartial,
            isRunning,
            interrupted,
            NormalizeText(summary),
            itemsReplaced,
            tokensReplaced);
    }

    private static string SerializePromptSnapshot(
        string? systemPrompt,
        IReadOnlyList<ConversationRequestMessage> messages,
        IReadOnlyList<ToolDefinition> availableTools,
        ConversationProviderRequest request)
    {
        return WriteJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("systemPrompt", systemPrompt);
            writer.WritePropertyName("messages");
            WriteMessages(writer, messages);
            writer.WritePropertyName("tools");
            WriteTools(writer, availableTools, includeSchemas: true);
            writer.WritePropertyName("requestOptions");
            WriteRequestOptions(writer, request);
            writer.WriteEndObject();
        });
    }

    private static string SerializeMessages(IReadOnlyList<ConversationRequestMessage> messages)
    {
        return WriteJson(writer => WriteMessages(writer, messages));
    }

    private static string SerializeTools(
        IReadOnlyList<ToolDefinition> tools,
        bool includeSchemas)
    {
        return WriteJson(writer => WriteTools(writer, tools, includeSchemas));
    }

    private static string SerializeRequestOptions(ConversationProviderRequest request)
    {
        return WriteJson(writer => WriteRequestOptions(writer, request));
    }

    private static string? SerializeResponse(
        ConversationResponse? response,
        ConversationProviderPayload? payload)
    {
        if (response is null && payload is null)
        {
            return null;
        }

        return WriteJson(writer =>
        {
            writer.WriteStartObject();
            if (payload is not null)
            {
                writer.WriteString("providerKind", payload.ProviderKind.ToString());
                writer.WriteString("responseId", payload.ResponseId);
                writer.WriteNumber("retryCount", payload.RetryCount);
                writer.WriteBoolean("assistantMessageWasStreamed", payload.AssistantMessageWasStreamed);
            }

            if (response is not null)
            {
                writer.WriteString("assistantMessage", response.AssistantMessage);
                writer.WriteString("responseId", response.ResponseId);
                WriteNullableNumber(writer, "completionTokens", response.CompletionTokens);
                WriteNullableNumber(writer, "promptTokens", response.PromptTokens);
                WriteNullableNumber(writer, "totalTokens", response.TotalTokens);
                WriteNullableNumber(writer, "cachedPromptTokens", response.CachedPromptTokens);
                writer.WriteString("reasoningContent", response.ReasoningContent);
                writer.WriteString("reasoningDetailsJson", response.ReasoningDetailsJson);
                writer.WritePropertyName("toolCalls");
                writer.WriteStartArray();
                foreach (ConversationToolCall toolCall in response.ToolCalls)
                {
                    WriteToolCall(writer, toolCall);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        });
    }

    private static string WriteJson(Action<Utf8JsonWriter> write)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteMessages(
        Utf8JsonWriter writer,
        IReadOnlyList<ConversationRequestMessage> messages)
    {
        writer.WriteStartArray();
        foreach (ConversationRequestMessage message in messages)
        {
            writer.WriteStartObject();
            writer.WriteString("role", message.Role);
            writer.WriteString("content", message.Content);
            writer.WriteString("toolCallId", message.ToolCallId);
            writer.WriteString("reasoningContent", message.ReasoningContent);
            writer.WriteString("reasoningDetailsJson", message.ReasoningDetailsJson);
            writer.WritePropertyName("toolCalls");
            writer.WriteStartArray();
            foreach (ConversationToolCall toolCall in message.ToolCalls)
            {
                WriteToolCall(writer, toolCall);
            }

            writer.WriteEndArray();
            writer.WritePropertyName("attachments");
            writer.WriteStartArray();
            foreach (ConversationAttachment attachment in message.Attachments)
            {
                writer.WriteStartObject();
                writer.WriteString("name", attachment.Name);
                writer.WriteString("mediaType", attachment.MediaType);
                writer.WriteBoolean("isText", attachment.IsText);
                writer.WriteBoolean("isImage", attachment.IsImage);
                writer.WriteNumber("textLength", attachment.TextContent?.Length ?? 0);
                writer.WriteNumber("base64Length", attachment.ContentBase64?.Length ?? 0);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteTools(
        Utf8JsonWriter writer,
        IReadOnlyList<ToolDefinition> tools,
        bool includeSchemas)
    {
        writer.WriteStartArray();
        foreach (ToolDefinition tool in tools)
        {
            writer.WriteStartObject();
            writer.WriteString("name", tool.Name);
            writer.WriteString("description", tool.Description);
            if (includeSchemas)
            {
                writer.WritePropertyName("schema");
                tool.Schema.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteRequestOptions(
        Utf8JsonWriter writer,
        ConversationProviderRequest request)
    {
        writer.WriteStartObject();
        writer.WriteString("provider", request.ProviderProfile.ProviderKind.ToString());
        writer.WriteString("modelId", request.ModelId);
        writer.WriteString("reasoningEffort", request.ReasoningEffort);
        writer.WriteString("thinkingMode", request.ThinkingMode);
        writer.WriteBoolean("showThinking", request.ShowThinking);
        writer.WriteBoolean("streamingEnabled", request.OnAssistantMessageChunkAsync is not null);
        writer.WriteNumber("messageCount", request.Messages.Count);
        writer.WriteNumber("toolCount", request.AvailableTools.Count);
        writer.WriteEndObject();
    }

    private static void WriteToolCall(
        Utf8JsonWriter writer,
        ConversationToolCall toolCall)
    {
        writer.WriteStartObject();
        writer.WriteString("id", toolCall.Id);
        writer.WriteString("name", toolCall.Name);
        writer.WriteString("argumentsJson", toolCall.ArgumentsJson);
        writer.WriteEndObject();
    }

    private static void WriteNullableNumber(
        Utf8JsonWriter writer,
        string propertyName,
        int? value)
    {
        if (value.HasValue)
        {
            writer.WriteNumber(propertyName, value.Value);
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }

    private static string CreateStableId(string prefix, string value)
    {
        string normalized = string.IsNullOrWhiteSpace(value)
            ? Guid.NewGuid().ToString("N")
            : value.Trim();
        return prefix + "-" + normalized;
    }

    private static string NormalizeSectionId(string sectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionId);

        if (!Guid.TryParse(sectionId.Trim(), out Guid parsedSectionId))
        {
            throw new ArgumentException(
                "Section id must be a valid GUID.",
                nameof(sectionId));
        }

        return parsedSectionId.ToString("D");
    }

    private static void EnsureStorageDirectory(string storagePath)
    {
        string? directoryPath = Path.GetDirectoryName(storagePath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            FilePermissionHelper.EnsurePrivateDirectory(directoryPath);
        }
    }

    private static string? NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return SecretRedactor.Redact(value)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
    }

    private static string? NormalizeJsonText(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : SecretRedactor.Redact(value.Trim());
    }
}
