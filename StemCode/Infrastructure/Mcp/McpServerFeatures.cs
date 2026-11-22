using System.Text;
using System.Text.Json;

namespace StemCode.Infrastructure.Mcp;

internal sealed record McpRemoteResource(
    string Uri,
    string Name,
    string? Description,
    string? MimeType);

internal sealed record McpRemoteResourceTemplate(
    string UriTemplate,
    string Name,
    string? Description,
    string? MimeType);

internal sealed record McpRemotePromptArgument(
    string Name,
    string? Description,
    bool Required);

internal sealed record McpRemotePrompt(
    string Name,
    string? Description,
    IReadOnlyList<McpRemotePromptArgument> Arguments);

/// <summary>
/// The MCP resources and prompts protocol (<c>resources/list</c>,
/// <c>resources/templates/list</c>, <c>resources/read</c>, <c>prompts/list</c>,
/// <c>prompts/get</c>) on top of any <see cref="IMcpServerClient"/>.
/// </summary>
internal static class McpServerFeatures
{
    private const int MaxPages = 50;
    private const int MaxResourceTextCharacters = 100_000;

    public static JsonElement ReadCapabilities(JsonElement initializeResult)
    {
        return initializeResult.ValueKind == JsonValueKind.Object &&
               initializeResult.TryGetProperty("capabilities", out JsonElement capabilities) &&
               capabilities.ValueKind == JsonValueKind.Object
            ? capabilities.Clone()
            : default;
    }

    public static bool SupportsResources(this IMcpServerClient client)
    {
        return HasCapability(client.ServerCapabilities, "resources");
    }

    public static bool SupportsPrompts(this IMcpServerClient client)
    {
        return HasCapability(client.ServerCapabilities, "prompts");
    }

    public static async Task<IReadOnlyList<McpRemoteResource>> ListResourcesAsync(
        this IMcpServerClient client,
        CancellationToken cancellationToken)
    {
        List<McpRemoteResource> resources = [];
        await foreach (JsonElement item in ListPagedAsync(client, "resources/list", "resources", cancellationToken))
        {
            string? uri = GetString(item, "uri");
            if (uri is null)
            {
                continue;
            }

            resources.Add(new McpRemoteResource(
                uri,
                GetString(item, "name") ?? uri,
                GetString(item, "description"),
                GetString(item, "mimeType")));
        }

        return resources;
    }

    public static async Task<IReadOnlyList<McpRemoteResourceTemplate>> ListResourceTemplatesAsync(
        this IMcpServerClient client,
        CancellationToken cancellationToken)
    {
        List<McpRemoteResourceTemplate> templates = [];
        try
        {
            await foreach (JsonElement item in ListPagedAsync(client, "resources/templates/list", "resourceTemplates", cancellationToken))
            {
                string? uriTemplate = GetString(item, "uriTemplate");
                if (uriTemplate is null)
                {
                    continue;
                }

                templates.Add(new McpRemoteResourceTemplate(
                    uriTemplate,
                    GetString(item, "name") ?? uriTemplate,
                    GetString(item, "description"),
                    GetString(item, "mimeType")));
            }
        }
        catch (McpProtocolException)
        {
            // Templates are optional; servers that only list concrete resources may
            // reject the method.
        }

        return templates;
    }

    /// <summary>
    /// Reads a resource and returns its text. Binary (<c>blob</c>) contents are
    /// summarized rather than inlined.
    /// </summary>
    public static async Task<string> ReadResourceTextAsync(
        this IMcpServerClient client,
        string uri,
        CancellationToken cancellationToken)
    {
        JsonElement result = await client.RequestAsync(
            "resources/read",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("uri", uri);
                writer.WriteEndObject();
            },
            cancellationToken);

        if (!result.TryGetProperty("contents", out JsonElement contents) ||
            contents.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        List<string> parts = [];
        foreach (JsonElement content in contents.EnumerateArray())
        {
            parts.Add(DescribeResourceContent(content));
        }

        return Truncate(string.Join(Environment.NewLine + Environment.NewLine, parts));
    }

    public static async Task<IReadOnlyList<McpRemotePrompt>> ListPromptsAsync(
        this IMcpServerClient client,
        CancellationToken cancellationToken)
    {
        List<McpRemotePrompt> prompts = [];
        await foreach (JsonElement item in ListPagedAsync(client, "prompts/list", "prompts", cancellationToken))
        {
            string? name = GetString(item, "name");
            if (name is null)
            {
                continue;
            }

            List<McpRemotePromptArgument> arguments = [];
            if (item.TryGetProperty("arguments", out JsonElement argumentsElement) &&
                argumentsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement argument in argumentsElement.EnumerateArray())
                {
                    string? argumentName = GetString(argument, "name");
                    if (argumentName is null)
                    {
                        continue;
                    }

                    arguments.Add(new McpRemotePromptArgument(
                        argumentName,
                        GetString(argument, "description"),
                        argument.TryGetProperty("required", out JsonElement required) &&
                        required.ValueKind == JsonValueKind.True));
                }
            }

            prompts.Add(new McpRemotePrompt(name, GetString(item, "description"), arguments));
        }

        return prompts;
    }

    /// <summary>
    /// Fetches a prompt and flattens its messages into one block of text that can
    /// be sent as the user's turn.
    /// </summary>
    public static async Task<string> GetPromptTextAsync(
        this IMcpServerClient client,
        string promptName,
        IReadOnlyDictionary<string, string> arguments,
        CancellationToken cancellationToken)
    {
        JsonElement result = await client.RequestAsync(
            "prompts/get",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("name", promptName);
                writer.WritePropertyName("arguments");
                writer.WriteStartObject();
                foreach (KeyValuePair<string, string> argument in arguments)
                {
                    writer.WriteString(argument.Key, argument.Value);
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            },
            cancellationToken);

        return FlattenPromptMessages(result);
    }

    internal static string FlattenPromptMessages(JsonElement result)
    {
        if (!result.TryGetProperty("messages", out JsonElement messages) ||
            messages.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        List<(string Role, string Text)> parts = [];
        foreach (JsonElement message in messages.EnumerateArray())
        {
            string role = GetString(message, "role") ?? "user";
            string text = message.TryGetProperty("content", out JsonElement content)
                ? DescribePromptContent(content)
                : string.Empty;
            if (!string.IsNullOrWhiteSpace(text))
            {
                parts.Add((role, text.Trim()));
            }
        }

        if (parts.Count == 1 && string.Equals(parts[0].Role, "user", StringComparison.OrdinalIgnoreCase))
        {
            return parts[0].Text;
        }

        StringBuilder builder = new();
        foreach ((string role, string text) in parts)
        {
            if (builder.Length > 0)
            {
                builder.AppendLine().AppendLine();
            }

            builder.Append('[').Append(role).AppendLine("]");
            builder.Append(text);
        }

        return builder.ToString();
    }

    private static string DescribePromptContent(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.Array)
        {
            return string.Join(
                Environment.NewLine,
                content.EnumerateArray().Select(DescribePromptContent).Where(static text => text.Length > 0));
        }

        string? type = GetString(content, "type");
        return type switch
        {
            "text" => GetString(content, "text") ?? string.Empty,
            "resource" when content.TryGetProperty("resource", out JsonElement resource) =>
                DescribeResourceContent(resource),
            "resource_link" => $"[resource: {GetString(content, "uri")}]",
            "image" or "audio" => $"[{GetString(content, "mimeType") ?? type} {type} from MCP prompt]",
            _ => content.ValueKind == JsonValueKind.Undefined ? string.Empty : content.GetRawText()
        };
    }

    private static string DescribeResourceContent(JsonElement content)
    {
        string uri = GetString(content, "uri") ?? "(unknown uri)";
        string? text = GetString(content, "text");
        if (text is not null)
        {
            return $"<resource uri=\"{uri}\">{Environment.NewLine}{text}{Environment.NewLine}</resource>";
        }

        string? blob = GetString(content, "blob");
        string mimeType = GetString(content, "mimeType") ?? "application/octet-stream";
        int approximateBytes = blob is null ? 0 : blob.Length * 3 / 4;
        return $"<resource uri=\"{uri}\" mimeType=\"{mimeType}\">[binary content, about {approximateBytes} bytes, not shown]</resource>";
    }

    private static async IAsyncEnumerable<JsonElement> ListPagedAsync(
        IMcpServerClient client,
        string method,
        string itemsProperty,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? cursor = null;
        for (int page = 0; page < MaxPages; page++)
        {
            JsonElement result = await client.RequestAsync(
                method,
                writer => McpJson.WriteListToolsParams(cursor, writer),
                cancellationToken);

            if (result.TryGetProperty(itemsProperty, out JsonElement items) &&
                items.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in items.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        yield return item.Clone();
                    }
                }
            }

            cursor = McpJson.GetNextCursor(result);
            if (string.IsNullOrWhiteSpace(cursor))
            {
                yield break;
            }
        }
    }

    private static bool HasCapability(JsonElement capabilities, string name)
    {
        return capabilities.ValueKind == JsonValueKind.Object &&
               capabilities.TryGetProperty(name, out JsonElement capability) &&
               capability.ValueKind is JsonValueKind.Object or JsonValueKind.True;
    }

    private static string? GetString(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(property, out JsonElement value) &&
               value.ValueKind == JsonValueKind.String &&
               !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;
    }

    private static string Truncate(string value)
    {
        return value.Length <= MaxResourceTextCharacters
            ? value
            : value[..MaxResourceTextCharacters] + $"{Environment.NewLine}[truncated at {MaxResourceTextCharacters} characters]";
    }
}
