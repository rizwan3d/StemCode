using System.Text.Json;
using System.Text.Json.Serialization;

namespace StemCode.Infrastructure.Conversation;

internal sealed record OpenAiChatCompletionRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] IReadOnlyList<OpenAiChatCompletionRequestMessage> Messages,
    [property: JsonPropertyName("tools")] IReadOnlyList<OpenAiChatCompletionToolDefinition> Tools,
    [property: JsonPropertyName("reasoning_effort")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReasoningEffort = null,
    [property: JsonPropertyName("reasoning")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ProviderReasoningConfig? Reasoning = null,
    [property: JsonPropertyName("thinkingConfig")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GeminiThinkingConfig? ThinkingConfig = null,
    [property: JsonPropertyName("thinking")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ChatCompletionThinkingConfig? Thinking = null,
    [property: JsonPropertyName("enable_thinking")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? EnableThinking = null);

/// <summary>
/// Vendor thinking switch used by DeepSeek, Z.ai GLM and Moonshot Kimi: {"type": "enabled" | "disabled"}.
/// </summary>
internal sealed record ChatCompletionThinkingConfig(
    [property: JsonPropertyName("type")] string Type)
{
    public static ChatCompletionThinkingConfig Enabled { get; } = new("enabled");

    public static ChatCompletionThinkingConfig Disabled { get; } = new("disabled");
}

internal sealed record OpenAiChatCompletionRequestMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Content,
    [property: JsonPropertyName("tool_call_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ToolCallId = null,
    [property: JsonPropertyName("tool_calls")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<OpenAiChatCompletionToolCall>? ToolCalls = null,
    [property: JsonPropertyName("reasoning_content")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReasoningContent = null,
    [property: JsonPropertyName("reasoning_details")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? ReasoningDetails = null);

internal sealed record OpenAiChatCompletionContentPart(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text = null,
    [property: JsonPropertyName("image_url")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OpenAiChatCompletionImageUrl? ImageUrl = null);

internal sealed record OpenAiChatCompletionImageUrl(
    [property: JsonPropertyName("url")] string Url);

internal sealed record OpenAiChatCompletionToolDefinition(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("function")] OpenAiChatCompletionFunctionDefinition Function);

internal sealed record OpenAiChatCompletionFunctionDefinition(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("parameters")] JsonElement Parameters);

internal sealed record OllamaChatRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] IReadOnlyList<OpenAiChatCompletionRequestMessage> Messages,
    [property: JsonPropertyName("tools")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<OpenAiChatCompletionToolDefinition>? Tools,
    [property: JsonPropertyName("stream")] bool Stream,
    [property: JsonPropertyName("think")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Think = null);
