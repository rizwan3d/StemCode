using StemCode.Application.Exceptions;
using System.Text;
using System.Text.Json;

namespace StemCode.Infrastructure.Conversation;

/// <summary>
/// Reads an Anthropic Messages server-sent event stream, forwards text deltas as they arrive,
/// and rebuilds the complete message JSON so <see cref="ConversationProviderResponseNormalizer"/>
/// can handle streamed and non-streamed responses the same way.
/// </summary>
internal static class AnthropicMessagesEventStreamParser
{
    public static async Task<StreamingResponseReadResult> ReadResponseBodyAsync(
        Stream stream,
        Func<string, CancellationToken, Task>? onAssistantMessageChunkAsync,
        CancellationToken cancellationToken)
    {
        using StreamReader reader = new(stream, leaveOpen: true);
        MessageAccumulator message = new();
        StringBuilder data = new();
        bool isEventStream = false;
        bool assistantMessageWasStreamed = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (!isEventStream)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                if (!IsEventStreamLine(line))
                {
                    // Not an event stream (a proxy or test double answered with plain JSON).
                    // Return the body unchanged for the regular JSON normalizer.
                    string remainder = await reader.ReadToEndAsync(cancellationToken);
                    return new StreamingResponseReadResult(line + "\n" + remainder, false);
                }

                isEventStream = true;
            }

            if (line.Length == 0)
            {
                assistantMessageWasStreamed |= await DispatchEventAsync(
                    message,
                    data,
                    onAssistantMessageChunkAsync,
                    cancellationToken);
                data.Clear();
                continue;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                // "event:" names duplicate the JSON "type" field; comments and ids are ignored.
                continue;
            }

            if (data.Length > 0)
            {
                data.Append('\n');
            }

            string value = line[5..];
            data.Append(value.StartsWith(' ') ? value[1..] : value);
        }

        assistantMessageWasStreamed |= await DispatchEventAsync(
            message,
            data,
            onAssistantMessageChunkAsync,
            cancellationToken);

        return new StreamingResponseReadResult(message.ToJson(), assistantMessageWasStreamed);
    }

    private static bool IsEventStreamLine(string line)
    {
        return line.StartsWith("event:", StringComparison.Ordinal) ||
            line.StartsWith("data:", StringComparison.Ordinal) ||
            line.StartsWith(':');
    }

    private static async Task<bool> DispatchEventAsync(
        MessageAccumulator message,
        StringBuilder data,
        Func<string, CancellationToken, Task>? onAssistantMessageChunkAsync,
        CancellationToken cancellationToken)
    {
        if (data.Length == 0)
        {
            return false;
        }

        using JsonDocument document = JsonDocument.Parse(data.ToString());
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        switch (GetString(root, "type"))
        {
            case "message_start" when root.TryGetProperty("message", out JsonElement startMessage):
                message.Id = GetString(startMessage, "id") ?? message.Id;
                message.MergeUsage(startMessage);
                return false;

            case "content_block_start" when root.TryGetProperty("content_block", out JsonElement block):
                message.StartBlock(GetIndex(root), block);
                return false;

            case "content_block_delta" when root.TryGetProperty("delta", out JsonElement delta):
                string? textDelta = message.ApplyDelta(GetIndex(root), delta);
                if (textDelta is { Length: > 0 } && onAssistantMessageChunkAsync is not null)
                {
                    await onAssistantMessageChunkAsync(textDelta, cancellationToken);
                    return true;
                }

                return false;

            case "message_delta":
                if (root.TryGetProperty("delta", out JsonElement messageDelta))
                {
                    message.StopReason = GetString(messageDelta, "stop_reason") ?? message.StopReason;
                }

                message.MergeUsage(root);
                return false;

            case "error":
                JsonElement error = root.TryGetProperty("error", out JsonElement errorElement)
                    ? errorElement
                    : root;
                throw new ConversationProviderException(
                    "The Anthropic stream returned an error: " +
                    $"{GetString(error, "type") ?? "unknown_error"}: {GetString(error, "message") ?? "no details"}");

            default:
                // ping, content_block_stop and message_stop carry nothing the final message needs.
                return false;
        }
    }

    private static int GetIndex(JsonElement element)
    {
        return element.TryGetProperty("index", out JsonElement index) &&
            index.ValueKind == JsonValueKind.Number &&
            index.TryGetInt32(out int value)
                ? value
                : 0;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out JsonElement property) &&
            property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }

    private static int? GetInt32(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out JsonElement property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out int value)
                ? value
                : null;
    }

    private sealed class MessageAccumulator
    {
        private readonly SortedDictionary<int, BlockAccumulator> _blocks = [];
        private int? _inputTokens;
        private int? _outputTokens;
        private int? _cacheReadInputTokens;
        private int? _cacheCreationInputTokens;

        public string? Id { get; set; }

        public string? StopReason { get; set; }

        public void StartBlock(int index, JsonElement block)
        {
            _blocks[index] = new BlockAccumulator(block);
        }

        public string? ApplyDelta(int index, JsonElement delta)
        {
            if (!_blocks.TryGetValue(index, out BlockAccumulator? block))
            {
                return null;
            }

            switch (GetString(delta, "type"))
            {
                case "text_delta":
                    string? text = GetString(delta, "text");
                    block.Text.Append(text);
                    return text;
                case "thinking_delta":
                    block.Thinking.Append(GetString(delta, "thinking"));
                    return null;
                case "signature_delta":
                    block.Signature.Append(GetString(delta, "signature"));
                    return null;
                case "input_json_delta":
                    block.PartialJson.Append(GetString(delta, "partial_json"));
                    return null;
                default:
                    return null;
            }
        }

        // message_start carries input and cache counts; message_delta carries cumulative output
        // counts (and, on newer API versions, updated input counts). Later values win.
        public void MergeUsage(JsonElement container)
        {
            if (!container.TryGetProperty("usage", out JsonElement usage) ||
                usage.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            _inputTokens = GetInt32(usage, "input_tokens") ?? _inputTokens;
            _outputTokens = GetInt32(usage, "output_tokens") ?? _outputTokens;
            _cacheReadInputTokens = GetInt32(usage, "cache_read_input_tokens") ?? _cacheReadInputTokens;
            _cacheCreationInputTokens = GetInt32(usage, "cache_creation_input_tokens") ?? _cacheCreationInputTokens;
        }

        public string ToJson()
        {
            using MemoryStream stream = new();
            using (Utf8JsonWriter writer = new(stream))
            {
                writer.WriteStartObject();
                if (Id is not null)
                {
                    writer.WriteString("id", Id);
                }

                writer.WriteString("type", "message");
                writer.WriteString("role", "assistant");
                writer.WriteStartArray("content");
                foreach (BlockAccumulator block in _blocks.Values)
                {
                    block.WriteTo(writer);
                }

                writer.WriteEndArray();

                if (StopReason is not null)
                {
                    writer.WriteString("stop_reason", StopReason);
                }

                writer.WriteStartObject("usage");
                WriteOptionalNumber(writer, "input_tokens", _inputTokens);
                WriteOptionalNumber(writer, "output_tokens", _outputTokens);
                WriteOptionalNumber(writer, "cache_read_input_tokens", _cacheReadInputTokens);
                WriteOptionalNumber(writer, "cache_creation_input_tokens", _cacheCreationInputTokens);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private static void WriteOptionalNumber(Utf8JsonWriter writer, string name, int? value)
        {
            if (value is not null)
            {
                writer.WriteNumber(name, value.Value);
            }
        }
    }

    private sealed class BlockAccumulator
    {
        private readonly string? _type;
        private readonly string? _id;
        private readonly string? _name;
        private readonly string? _data;
        private readonly string? _initialInputJson;

        public BlockAccumulator(JsonElement block)
        {
            _type = GetString(block, "type");
            _id = GetString(block, "id");
            _name = GetString(block, "name");
            _data = GetString(block, "data");
            Text.Append(GetString(block, "text"));
            Thinking.Append(GetString(block, "thinking"));
            Signature.Append(GetString(block, "signature"));
            if (block.TryGetProperty("input", out JsonElement input) &&
                input.ValueKind == JsonValueKind.Object)
            {
                _initialInputJson = input.GetRawText();
            }
        }

        public StringBuilder Text { get; } = new();

        public StringBuilder Thinking { get; } = new();

        public StringBuilder Signature { get; } = new();

        public StringBuilder PartialJson { get; } = new();

        public void WriteTo(Utf8JsonWriter writer)
        {
            switch (_type)
            {
                case "text":
                    writer.WriteStartObject();
                    writer.WriteString("type", "text");
                    writer.WriteString("text", Text.ToString());
                    writer.WriteEndObject();
                    break;

                case "thinking":
                    writer.WriteStartObject();
                    writer.WriteString("type", "thinking");
                    writer.WriteString("thinking", Thinking.ToString());
                    writer.WriteString("signature", Signature.ToString());
                    writer.WriteEndObject();
                    break;

                case "redacted_thinking":
                    writer.WriteStartObject();
                    writer.WriteString("type", "redacted_thinking");
                    writer.WriteString("data", _data);
                    writer.WriteEndObject();
                    break;

                case "tool_use":
                    writer.WriteStartObject();
                    writer.WriteString("type", "tool_use");
                    writer.WriteString("id", _id);
                    writer.WriteString("name", _name);
                    writer.WritePropertyName("input");
                    WriteToolInput(writer);
                    writer.WriteEndObject();
                    break;

                // Server-tool and other block types are not produced by StemCode's client tools.
            }
        }

        private void WriteToolInput(Utf8JsonWriter writer)
        {
            string inputJson = PartialJson.Length > 0
                ? PartialJson.ToString()
                : _initialInputJson ?? "{}";
            try
            {
                using JsonDocument document = JsonDocument.Parse(inputJson);
                document.RootElement.WriteTo(writer);
            }
            catch (JsonException)
            {
                // Input cut off mid-stream (usually stop_reason max_tokens). An empty object
                // fails the tool's schema check, and that error goes back to the model.
                writer.WriteStartObject();
                writer.WriteEndObject();
            }
        }
    }
}
