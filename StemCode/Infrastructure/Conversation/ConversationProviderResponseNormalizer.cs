using StemCode.Application.Exceptions;
using System.Text.Json;

namespace StemCode.Infrastructure.Conversation;

internal sealed class ConversationProviderResponseNormalizer
{
    public string NormalizeOpenAiResponsesBody(string responseBody)
    {
        return OpenAiResponsesEventStreamParser.TryParseResponsePayload(responseBody) ?? responseBody;
    }

    public string ConvertAnthropicMessagesResponseToChatCompletion(string responseBody)
    {
        AnthropicMessagesResponse? response = JsonSerializer.Deserialize(
            responseBody,
            OpenAiConversationJsonContext.Default.AnthropicMessagesResponse);
        if (response?.Content is null)
        {
            throw new ConversationProviderException(
                "The Anthropic Messages response did not contain message content.");
        }

        List<string> textParts = [];
        List<string> thinkingParts = [];
        List<AnthropicResponseContentBlock> replayableThinkingBlocks = [];
        List<OpenAiChatCompletionToolCall> toolCalls = [];
        int toolCallOrdinal = 0;

        foreach (AnthropicResponseContentBlock contentBlock in response.Content)
        {
            if (string.Equals(contentBlock.Type, "thinking", StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(contentBlock.Signature))
                {
                    replayableThinkingBlocks.Add(contentBlock);
                }

                if (!string.IsNullOrWhiteSpace(contentBlock.Thinking))
                {
                    thinkingParts.Add(contentBlock.Thinking.Trim());
                }

                continue;
            }

            if (string.Equals(contentBlock.Type, "redacted_thinking", StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(contentBlock.Data))
                {
                    replayableThinkingBlocks.Add(contentBlock);
                }

                continue;
            }

            if (string.Equals(contentBlock.Type, "text", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(contentBlock.Text))
            {
                textParts.Add(contentBlock.Text.Trim());
                continue;
            }

            if (string.Equals(contentBlock.Type, "tool_use", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(contentBlock.Name))
            {
                toolCalls.Add(new OpenAiChatCompletionToolCall(
                    CreateToolCallId(contentBlock.Id, response.Id, ++toolCallOrdinal),
                    "function",
                    new OpenAiChatCompletionFunctionCall(
                        contentBlock.Name.Trim(),
                        contentBlock.Input.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                            ? "{}"
                            : contentBlock.Input.GetRawText())));
            }
        }

        string content = string.Join(
            Environment.NewLine + Environment.NewLine,
            textParts);
        string? reasoningContent = thinkingParts.Count == 0
            ? null
            : string.Join(Environment.NewLine + Environment.NewLine, thinkingParts);
        OpenAiChatCompletionResponse convertedResponse = new(
            response.Id,
            [
                new OpenAiChatCompletionChoice(
                    new OpenAiChatCompletionResponseMessage(
                        CreateStringContentElement(content),
                        toolCalls.Count == 0 ? null : toolCalls,
                        FunctionCall: null,
                        Refusal: null,
                        ReasoningContent: reasoningContent,
                        ReasoningDetails: CreateAnthropicThinkingDetails(replayableThinkingBlocks)),
                    MapAnthropicStopReason(response.StopReason))
            ],
            ConvertAnthropicUsage(response.Usage));

        return JsonSerializer.Serialize(
            convertedResponse,
            OpenAiConversationJsonContext.Default.OpenAiChatCompletionResponse);
    }

    // Keeps the signed thinking blocks exactly as returned. They travel through the pipeline
    // as reasoning_details and are replayed on the next request by
    // ConversationProviderRequestPayloadFactory.CreateAnthropicAssistantContent.
    private static JsonElement? CreateAnthropicThinkingDetails(
        IReadOnlyList<AnthropicResponseContentBlock> blocks)
    {
        if (blocks.Count == 0)
        {
            return null;
        }

        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartArray();
            foreach (AnthropicResponseContentBlock block in blocks)
            {
                writer.WriteStartObject();
                writer.WriteString("type", block.Type);
                if (string.Equals(block.Type, "redacted_thinking", StringComparison.Ordinal))
                {
                    writer.WriteString("data", block.Data);
                }
                else
                {
                    writer.WriteString("thinking", block.Thinking ?? string.Empty);
                    writer.WriteString("signature", block.Signature);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        using JsonDocument document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    public string ConvertOllamaChatResponseToChatCompletion(string responseBody)
    {
        using JsonDocument document = JsonDocument.Parse(responseBody);
        JsonElement root = document.RootElement;
        JsonElement message = root.TryGetProperty("message", out JsonElement messageElement)
            ? messageElement
            : default;

        string content = TryGetString(message, "content") ?? string.Empty;
        List<OpenAiChatCompletionToolCall> toolCalls = [];
        if (message.ValueKind == JsonValueKind.Object &&
            message.TryGetProperty("tool_calls", out JsonElement toolCallsElement) &&
            toolCallsElement.ValueKind == JsonValueKind.Array)
        {
            int toolCallOrdinal = 0;
            foreach (JsonElement toolCallElement in toolCallsElement.EnumerateArray())
            {
                if (!toolCallElement.TryGetProperty("function", out JsonElement functionElement) ||
                    functionElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string? name = TryGetString(functionElement, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                string argumentsJson = "{}";
                if (functionElement.TryGetProperty("arguments", out JsonElement argumentsElement) &&
                    argumentsElement.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
                {
                    argumentsJson = argumentsElement.ValueKind == JsonValueKind.String
                        ? argumentsElement.GetString() ?? "{}"
                        : argumentsElement.GetRawText();
                }

                toolCalls.Add(new OpenAiChatCompletionToolCall(
                    CreateToolCallId(null, TryGetString(root, "created_at"), ++toolCallOrdinal),
                    "function",
                    new OpenAiChatCompletionFunctionCall(name.Trim(), argumentsJson)));
            }
        }

        int? promptTokens = TryGetInt32(root, "prompt_eval_count");
        int? completionTokens = TryGetInt32(root, "eval_count");
        OpenAiChatCompletionResponse convertedResponse = new(
            TryGetString(root, "created_at"),
            [
                new OpenAiChatCompletionChoice(
                    new OpenAiChatCompletionResponseMessage(
                        CreateStringContentElement(content),
                        toolCalls.Count == 0 ? null : toolCalls,
                        FunctionCall: null,
                        Refusal: null),
                    toolCalls.Count == 0 ? "stop" : "tool_calls")
            ],
            new OpenAiChatCompletionUsage(
                completionTokens,
                promptTokens,
                SumNullable(promptTokens, completionTokens),
                PromptTokensDetails: null));

        return JsonSerializer.Serialize(
            convertedResponse,
            OpenAiConversationJsonContext.Default.OpenAiChatCompletionResponse);
    }

    private static string CreateToolCallId(string? rawId, string? responseId, int ordinal)
    {
        string? normalizedId = NormalizeOrNull(rawId);
        if (normalizedId is not null)
        {
            return normalizedId;
        }

        string? normalizedResponseId = NormalizeOrNull(responseId);
        return normalizedResponseId is null
            ? $"tool_call_{ordinal}"
            : $"{normalizedResponseId}_tool_call_{ordinal}";
    }

    private static string? NormalizeOrNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }

    private static JsonElement CreateStringContentElement(string? value)
    {
        string json = JsonSerializer.Serialize(
            value ?? string.Empty,
            OpenAiConversationJsonContext.Default.String);
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return NormalizeOrNull(property.GetString());
    }

    private static int? TryGetInt32(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt32(out int value))
        {
            return null;
        }

        return value;
    }

    private static string? MapAnthropicStopReason(string? stopReason)
    {
        return stopReason switch
        {
            "end_turn" => "stop",
            "tool_use" => "tool_calls",
            "max_tokens" => "length",
            "stop_sequence" => "stop",
            null => null,
            _ => stopReason
        };
    }

    private static OpenAiChatCompletionUsage? ConvertAnthropicUsage(AnthropicUsage? usage)
    {
        if (usage is null)
        {
            return null;
        }

        int? promptTokens = SumNullable(
            usage.InputTokens,
            usage.CacheReadInputTokens,
            usage.CacheCreationInputTokens);
        int? completionTokens = usage.OutputTokens;
        int? totalTokens = SumNullable(promptTokens, completionTokens);

        return new OpenAiChatCompletionUsage(
            completionTokens,
            promptTokens,
            totalTokens,
            usage.CacheReadInputTokens is null
                ? null
                : new OpenAiChatCompletionUsageDetails(usage.CacheReadInputTokens));
    }

    private static int? SumNullable(params int?[] values)
    {
        int sum = 0;
        bool hasValue = false;
        foreach (int? value in values)
        {
            if (value is null)
            {
                continue;
            }

            checked
            {
                sum += value.Value;
            }

            hasValue = true;
        }

        return hasValue ? sum : null;
    }
}
