using StemCode.Application.Exceptions;
using StemCode.Application.Models;
using StemCode.Domain.Models;
using StemCode.Infrastructure.Secrets;
using System.ComponentModel;
using System.Text;
using System.Text.Json;

namespace StemCode.Infrastructure.Conversation;

internal sealed class AnthropicClaudeAccountConversationProviderAdapter : IConversationProviderAdapter
{
    private const int MaxProcessOutputCharacters = 8 * 1024 * 1024;
    private const string BridgeSystemPrompt =
        "You are the language-model backend for StemCode. Follow the supplied system prompt and " +
        "conversation. When a listed tool is needed, return a tool call instead of pretending to " +
        "run it. StemCode will execute the tool and send its result in the next request.";
    private const string ResponseSchema =
        "{\"type\":\"object\",\"properties\":{" +
        "\"message\":{\"anyOf\":[{\"type\":\"string\"},{\"type\":\"null\"}]}" +
        ",\"tool_calls\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{" +
        "\"name\":{\"type\":\"string\"},\"arguments_json\":{\"type\":\"string\"}}," +
        "\"required\":[\"name\",\"arguments_json\"],\"additionalProperties\":false}}" +
        ",\"reasoning_summary\":{\"anyOf\":[{\"type\":\"string\"},{\"type\":\"null\"}]}}" +
        ",\"required\":[\"message\",\"tool_calls\",\"reasoning_summary\"],\"additionalProperties\":false}";

    private readonly IProcessRunner? _processRunner;

    public AnthropicClaudeAccountConversationProviderAdapter(IProcessRunner? processRunner)
    {
        _processRunner = processRunner;
    }

    public async Task<ConversationProviderPayload> SendAsync(
        ConversationProviderRequest request,
        CancellationToken cancellationToken)
    {
        if (_processRunner is null)
        {
            throw new ConversationProviderException(
                "Claude Code CLI execution is unavailable in this runtime.");
        }

        if (request.AttachmentsContainBinaryContent())
        {
            throw new ConversationProviderException(
                "Claude Code subscription mode does not yet support binary attachments. " +
                "Use a text attachment or the Anthropic API-key provider for this request.");
        }

        ProcessExecutionRequest processRequest = new(
            "claude",
            BuildArguments(request),
            StandardInput: BuildPrompt(request),
            WorkingDirectory: Environment.CurrentDirectory,
            MaxOutputCharacters: MaxProcessOutputCharacters);

        ProcessExecutionResult result;
        try
        {
            result = await _processRunner.RunAsync(processRequest, cancellationToken);
        }
        catch (Win32Exception exception)
        {
            throw new ConversationProviderException(
                "Claude Code is not installed or is not available on PATH.",
                exception);
        }

        if (result.ExitCode != 0)
        {
            string detail = FirstNonEmpty(result.StandardError, result.StandardOutput);
            throw new ConversationProviderException(
                "Claude Code failed to complete the request." + FormatDetail(detail));
        }

        return ParseResult(result.StandardOutput);
    }

    private static IReadOnlyList<string> BuildArguments(ConversationProviderRequest request)
    {
        List<string> arguments =
        [
            "--print",
            "--output-format", "json",
            "--tools", string.Empty,
            "--permission-prompts", "none",
            "--disable-slash-commands",
            "--safe-mode",
            "--no-session-persistence",
            "--system-prompt", BridgeSystemPrompt,
            "--json-schema", ResponseSchema,
            "--model", request.ModelId
        ];

        if (MapEffort(request.ReasoningEffort) is string effort)
        {
            arguments.Add("--effort");
            arguments.Add(effort);
        }

        return arguments;
    }

    private static string BuildPrompt(ConversationProviderRequest request)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteString(
                "instructions",
                "Return a message when you can answer, or one or more tool_calls when tools are needed. " +
                "Each arguments_json value must be a JSON object encoded as a string and must satisfy " +
                "the selected tool's input schema. Never claim a tool was executed before its tool result appears.");

            if (string.IsNullOrWhiteSpace(request.SystemPrompt))
            {
                writer.WriteNull("system_prompt");
            }
            else
            {
                writer.WriteString("system_prompt", request.SystemPrompt);
            }

            writer.WriteStartArray("conversation");
            foreach (ConversationRequestMessage message in request.Messages)
            {
                WriteMessage(writer, message);
            }
            writer.WriteEndArray();

            writer.WriteStartArray("tools");
            foreach (ToolDefinition tool in request.AvailableTools)
            {
                writer.WriteStartObject();
                writer.WriteString("name", tool.Name);
                writer.WriteString("description", tool.Description);
                writer.WritePropertyName("input_schema");
                tool.Schema.WriteTo(writer);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteMessage(Utf8JsonWriter writer, ConversationRequestMessage message)
    {
        writer.WriteStartObject();
        writer.WriteString("role", message.Role);
        if (message.Content is null)
        {
            writer.WriteNull("content");
        }
        else
        {
            writer.WriteString("content", message.Content);
        }

        if (message.ToolCallId is not null)
        {
            writer.WriteString("tool_call_id", message.ToolCallId);
        }

        if (message.ToolCalls.Count > 0)
        {
            writer.WriteStartArray("tool_calls");
            foreach (ConversationToolCall toolCall in message.ToolCalls)
            {
                writer.WriteStartObject();
                writer.WriteString("id", toolCall.Id);
                writer.WriteString("name", toolCall.Name);
                writer.WriteString("arguments_json", toolCall.ArgumentsJson);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        if (message.Attachments.Count > 0)
        {
            writer.WriteStartArray("text_attachments");
            foreach (ConversationAttachment attachment in message.Attachments)
            {
                writer.WriteStartObject();
                writer.WriteString("name", attachment.Name);
                writer.WriteString("media_type", attachment.MediaType);
                writer.WriteString("content", attachment.TextContent);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static ConversationProviderPayload ParseResult(string standardOutput)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(standardOutput);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("is_error", out JsonElement isError) &&
                isError.ValueKind == JsonValueKind.True)
            {
                throw new ConversationProviderException(
                    "Claude Code returned an error: " +
                    (TryGetString(root, "result") ?? "Unknown error."));
            }

            JsonElement bridgeResult;
            if (root.TryGetProperty("structured_output", out JsonElement structuredOutput) &&
                structuredOutput.ValueKind == JsonValueKind.Object)
            {
                bridgeResult = structuredOutput;
            }
            else if (TryGetString(root, "result") is string resultText)
            {
                using JsonDocument resultDocument = JsonDocument.Parse(resultText);
                bridgeResult = resultDocument.RootElement.Clone();
            }
            else
            {
                throw new ConversationProviderException(
                    "Claude Code did not return a structured result.");
            }

            return CreateProviderPayload(root, bridgeResult);
        }
        catch (JsonException exception)
        {
            throw new ConversationProviderException(
                "Claude Code returned an invalid JSON response.",
                exception);
        }
    }

    private static ConversationProviderPayload CreateProviderPayload(
        JsonElement cliResult,
        JsonElement bridgeResult)
    {
        string? responseId = TryGetString(cliResult, "session_id");
        string? message = TryGetString(bridgeResult, "message");
        string? reasoningSummary = TryGetString(bridgeResult, "reasoning_summary");

        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            if (responseId is null)
            {
                writer.WriteNull("id");
            }
            else
            {
                writer.WriteString("id", responseId);
            }

            writer.WriteStartArray("choices");
            writer.WriteStartObject();
            writer.WriteStartObject("message");
            if (message is null)
            {
                writer.WriteNull("content");
            }
            else
            {
                writer.WriteString("content", message);
            }

            int toolCallCount = WriteToolCalls(writer, bridgeResult);
            if (reasoningSummary is not null)
            {
                writer.WriteString("reasoning_content", reasoningSummary);
            }
            writer.WriteEndObject();
            writer.WriteString("finish_reason", toolCallCount > 0 ? "tool_calls" : "stop");
            writer.WriteEndObject();
            writer.WriteEndArray();

            WriteUsage(writer, cliResult);
            writer.WriteEndObject();
        }

        return new ConversationProviderPayload(
            ProviderKind.AnthropicClaudeAccount,
            Encoding.UTF8.GetString(stream.ToArray()),
            responseId);
    }

    private static int WriteToolCalls(Utf8JsonWriter writer, JsonElement bridgeResult)
    {
        if (!bridgeResult.TryGetProperty("tool_calls", out JsonElement toolCalls) ||
            toolCalls.ValueKind != JsonValueKind.Array ||
            toolCalls.GetArrayLength() == 0)
        {
            return 0;
        }

        writer.WriteStartArray("tool_calls");
        int count = 0;
        foreach (JsonElement toolCall in toolCalls.EnumerateArray())
        {
            string? name = TryGetString(toolCall, "name");
            string? argumentsJson = TryGetString(toolCall, "arguments_json");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(argumentsJson))
            {
                continue;
            }

            writer.WriteStartObject();
            writer.WriteString("id", $"call_{Guid.NewGuid():N}");
            writer.WriteString("type", "function");
            writer.WriteStartObject("function");
            writer.WriteString("name", name);
            writer.WriteString("arguments", argumentsJson);
            writer.WriteEndObject();
            writer.WriteEndObject();
            count++;
        }
        writer.WriteEndArray();
        return count;
    }

    private static void WriteUsage(Utf8JsonWriter writer, JsonElement cliResult)
    {
        if (!cliResult.TryGetProperty("usage", out JsonElement usage) ||
            usage.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        int input = TryGetInt32(usage, "input_tokens");
        int cacheRead = TryGetInt32(usage, "cache_read_input_tokens");
        int cacheCreated = TryGetInt32(usage, "cache_creation_input_tokens");
        int output = TryGetInt32(usage, "output_tokens");
        int prompt = input + cacheRead + cacheCreated;

        writer.WriteStartObject("usage");
        writer.WriteNumber("prompt_tokens", prompt);
        writer.WriteNumber("completion_tokens", output);
        writer.WriteNumber("total_tokens", prompt + output);
        writer.WriteStartObject("prompt_tokens_details");
        writer.WriteNumber("cached_tokens", cacheRead);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement property) &&
            property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }

    private static int TryGetInt32(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement property) &&
            property.TryGetInt32(out int value)
                ? value
                : 0;
    }

    private static string? MapEffort(string? effort)
    {
        return effort?.Trim().ToLowerInvariant() switch
        {
            "low" => "low",
            "medium" => "medium",
            "high" => "high",
            "xhigh" => "xhigh",
            "max" => "max",
            _ => null
        };
    }

    private static string FirstNonEmpty(string first, string second)
    {
        return !string.IsNullOrWhiteSpace(first) ? first.Trim() : second.Trim();
    }

    private static string FormatDetail(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return string.Empty;
        }

        const int maxLength = 500;
        string truncated = detail.Length <= maxLength
            ? detail
            : detail[..(maxLength - 3)] + "...";
        return $" Claude Code reported: {truncated}";
    }
}

internal static class ClaudeCodeConversationRequestExtensions
{
    public static bool AttachmentsContainBinaryContent(this ConversationProviderRequest request)
    {
        return request.Messages
            .SelectMany(static message => message.Attachments)
            .Any(static attachment => !attachment.IsText);
    }
}
