using System.Text.Json;
using System.Text.Json.Serialization;

namespace StemCode.Infrastructure.Conversation;

internal sealed record AnthropicMessagesRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] IReadOnlyList<AnthropicMessage> Messages,
    [property: JsonPropertyName("max_tokens")] int MaxTokens,
    [property: JsonPropertyName("system")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<AnthropicContentBlock>? System = null,
    [property: JsonPropertyName("tools")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<AnthropicToolDefinition>? Tools = null,
    [property: JsonPropertyName("output_config")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AnthropicOutputConfig? OutputConfig = null,
    [property: JsonPropertyName("thinking")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AnthropicThinking? Thinking = null,
    [property: JsonPropertyName("stream")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Stream = null);

internal sealed record AnthropicMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] IReadOnlyList<AnthropicContentBlock> Content);

internal sealed record AnthropicContentBlock(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text = null,
    [property: JsonPropertyName("source")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AnthropicImageSource? Source = null,
    [property: JsonPropertyName("id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Id = null,
    [property: JsonPropertyName("name")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null,
    [property: JsonPropertyName("input")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Input = null,
    [property: JsonPropertyName("tool_use_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ToolUseId = null,
    [property: JsonPropertyName("content")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Content = null,
    [property: JsonPropertyName("is_error")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsError = null,
    // thinking / redacted_thinking blocks replayed from an earlier response. They must be sent
    // back byte-for-byte (including the signature) or the API rejects or drops them.
    [property: JsonPropertyName("thinking")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Thinking = null,
    [property: JsonPropertyName("signature")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Signature = null,
    [property: JsonPropertyName("data")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Data = null,
    [property: JsonPropertyName("cache_control")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AnthropicCacheControl? CacheControl = null);

internal sealed record AnthropicImageSource(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("media_type")] string MediaType,
    [property: JsonPropertyName("data")] string Data);

internal sealed record AnthropicToolDefinition(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("input_schema")] JsonElement InputSchema,
    [property: JsonPropertyName("cache_control")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AnthropicCacheControl? CacheControl = null);

internal sealed record AnthropicCacheControl(
    [property: JsonPropertyName("type")] string Type)
{
    public static AnthropicCacheControl Ephemeral { get; } = new("ephemeral");
}

internal sealed record AnthropicThinking(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("budget_tokens")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? BudgetTokens = null,
    [property: JsonPropertyName("display")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Display = null);

internal sealed record AnthropicOutputConfig(
    [property: JsonPropertyName("effort")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Effort = null);

internal sealed record AnthropicMessagesResponse(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("content")] IReadOnlyList<AnthropicResponseContentBlock>? Content,
    [property: JsonPropertyName("stop_reason")] string? StopReason,
    [property: JsonPropertyName("usage")] AnthropicUsage? Usage);

internal sealed record AnthropicResponseContentBlock(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("thinking")] string? Thinking,
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("input")] JsonElement Input,
    [property: JsonPropertyName("signature")] string? Signature = null,
    [property: JsonPropertyName("data")] string? Data = null);

internal sealed record AnthropicUsage(
    [property: JsonPropertyName("input_tokens")] int? InputTokens,
    [property: JsonPropertyName("output_tokens")] int? OutputTokens,
    [property: JsonPropertyName("cache_read_input_tokens")] int? CacheReadInputTokens,
    [property: JsonPropertyName("cache_creation_input_tokens")] int? CacheCreationInputTokens);
