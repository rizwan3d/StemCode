using StemCode.Application.Abstractions;
using StemCode.Application.Models;
using StemCode.Application.Tools;
using StemCode.Application.Tools.Serialization;
using System.Text;
using System.Text.Json;

namespace StemCode.Infrastructure.Mcp;

/// <summary>Lists the resources (and resource templates) MCP servers publish.</summary>
internal sealed class McpListResourcesTool : ITool
{
    public const string ToolName = "mcp__list_resources";

    private readonly IReadOnlyDictionary<string, IMcpServerClient> _clients;

    public McpListResourcesTool(IReadOnlyDictionary<string, IMcpServerClient> clients)
    {
        _clients = clients;
    }

    public string Name => ToolName;

    public string Description =>
        "List the resources (files, records, documents) that connected MCP servers publish, with their URIs. " +
        $"Servers with resources: {string.Join(", ", _clients.Keys)}. Read one with {McpReadResourceTool.ToolName}.";

    public string PermissionRequirements => McpResourceToolSupport.PermissionRequirements;

    public string Schema => """
        {
          "type": "object",
          "properties": {
            "server": {
              "type": "string",
              "description": "Only list this server's resources. Omit to list every server."
            }
          },
          "additionalProperties": false
        }
        """;

    public async Task<ToolResult> ExecuteAsync(
        ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        string? server = ToolArguments.GetOptionalString(context.Arguments, "server");
        IEnumerable<KeyValuePair<string, IMcpServerClient>> targets = _clients;
        if (server is not null)
        {
            if (!McpResourceToolSupport.TryGetClient(_clients, server, out IMcpServerClient? client, out ToolResult? error))
            {
                return error!;
            }

            targets = [new KeyValuePair<string, IMcpServerClient>(client!.ServerName, client)];
        }

        StringBuilder text = new();
        int resourceCount = 0;
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("servers");
            writer.WriteStartArray();
            foreach ((string serverName, IMcpServerClient client) in targets)
            {
                writer.WriteStartObject();
                writer.WriteString("server", serverName);
                try
                {
                    IReadOnlyList<McpRemoteResource> resources = await client.ListResourcesAsync(cancellationToken);
                    IReadOnlyList<McpRemoteResourceTemplate> templates = await client.ListResourceTemplatesAsync(cancellationToken);
                    resourceCount += resources.Count;

                    text.AppendLine($"{serverName}: {resources.Count} resource(s), {templates.Count} template(s)");
                    writer.WritePropertyName("resources");
                    writer.WriteStartArray();
                    foreach (McpRemoteResource resource in resources)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("uri", resource.Uri);
                        writer.WriteString("name", resource.Name);
                        WriteOptional(writer, "description", resource.Description);
                        WriteOptional(writer, "mimeType", resource.MimeType);
                        writer.WriteEndObject();
                        text.AppendLine($"  {resource.Uri}  {resource.Name}{FormatSuffix(resource.Description)}");
                    }

                    writer.WriteEndArray();
                    writer.WritePropertyName("templates");
                    writer.WriteStartArray();
                    foreach (McpRemoteResourceTemplate template in templates)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("uriTemplate", template.UriTemplate);
                        writer.WriteString("name", template.Name);
                        WriteOptional(writer, "description", template.Description);
                        writer.WriteEndObject();
                        text.AppendLine($"  {template.UriTemplate}  (template) {template.Name}{FormatSuffix(template.Description)}");
                    }

                    writer.WriteEndArray();
                }
                catch (Exception exception) when (exception is McpProtocolException or IOException or HttpRequestException or InvalidOperationException)
                {
                    writer.WriteString("error", exception.Message);
                    text.AppendLine($"{serverName}: failed to list resources: {exception.Message}");
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        using JsonDocument document = JsonDocument.Parse(stream.ToArray());
        return ToolResultFactory.Success(
            $"Listed {resourceCount} MCP resource(s).",
            document.RootElement.Clone(),
            ToolJsonContext.Default.JsonElement,
            new ToolRenderPayload("MCP resources", text.ToString().TrimEnd()));
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            writer.WriteString(name, value);
        }
    }

    private static string FormatSuffix(string? description)
    {
        return string.IsNullOrWhiteSpace(description) ? string.Empty : $" - {description.Trim()}";
    }
}

/// <summary>Reads one MCP resource by URI.</summary>
internal sealed class McpReadResourceTool : ITool
{
    public const string ToolName = "mcp__read_resource";

    private readonly IReadOnlyDictionary<string, IMcpServerClient> _clients;
    private readonly TimeSpan _timeout;

    public McpReadResourceTool(
        IReadOnlyDictionary<string, IMcpServerClient> clients,
        TimeSpan timeout)
    {
        _clients = clients;
        _timeout = timeout;
    }

    public string Name => ToolName;

    public string Description =>
        "Read an MCP resource by URI and return its contents. " +
        $"Use {McpListResourcesTool.ToolName} to discover URIs; a URI may also come from a resource template.";

    public string PermissionRequirements => McpResourceToolSupport.PermissionRequirements;

    public string Schema => """
        {
          "type": "object",
          "properties": {
            "server": {
              "type": "string",
              "description": "Name of the MCP server that owns the resource."
            },
            "uri": {
              "type": "string",
              "description": "Resource URI, for example file:///repo/README.md or db://customers/42."
            }
          },
          "required": ["server", "uri"],
          "additionalProperties": false
        }
        """;

    public async Task<ToolResult> ExecuteAsync(
        ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!ToolArguments.TryGetNonEmptyString(context.Arguments, "server", out string? server) ||
            !ToolArguments.TryGetNonEmptyString(context.Arguments, "uri", out string? uri))
        {
            return ToolResultFactory.InvalidArguments(
                "missing_arguments",
                $"Tool '{ToolName}' requires 'server' and 'uri'.",
                new ToolRenderPayload("Invalid MCP resource arguments", "Provide both 'server' and 'uri'."));
        }

        if (!McpResourceToolSupport.TryGetClient(_clients, server!, out IMcpServerClient? client, out ToolResult? error))
        {
            return error!;
        }

        try
        {
            using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(_timeout);
            string content = await client!.ReadResourceTextAsync(uri!, timeoutSource.Token);

            using MemoryStream stream = new();
            using (Utf8JsonWriter writer = new(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("server", client.ServerName);
                writer.WriteString("uri", uri);
                writer.WriteString("content", content);
                writer.WriteEndObject();
            }

            using JsonDocument document = JsonDocument.Parse(stream.ToArray());
            return ToolResultFactory.Success(
                $"Read MCP resource '{uri}' from '{client.ServerName}'.",
                document.RootElement.Clone(),
                ToolJsonContext.Default.JsonElement,
                new ToolRenderPayload($"MCP resource: {uri}", content));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ToolResultFactory.ExecutionError(
                "mcp_resource_timeout",
                $"Reading '{uri}' from '{client!.ServerName}' timed out after {_timeout.TotalSeconds:0} seconds.",
                new ToolRenderPayload("MCP resource timed out", uri!));
        }
        catch (Exception exception) when (exception is McpProtocolException or IOException or HttpRequestException or InvalidOperationException)
        {
            return ToolResultFactory.ExecutionError(
                "mcp_resource_failed",
                $"Reading '{uri}' from '{client!.ServerName}' failed: {exception.Message}",
                new ToolRenderPayload("MCP resource failed", exception.Message));
        }
    }
}

internal static class McpResourceToolSupport
{
    // Reading resources is a read-only data fetch, so it runs without a prompt by
    // default; deny it with a rule on the "mcp" or "mcp:resources" tag if needed.
    public const string PermissionRequirements = """
        {
          "approvalMode": "Automatic",
          "toolTags": ["mcp", "mcp:resources", "read"]
        }
        """;

    public static bool TryGetClient(
        IReadOnlyDictionary<string, IMcpServerClient> clients,
        string server,
        out IMcpServerClient? client,
        out ToolResult? error)
    {
        client = clients
            .FirstOrDefault(pair => string.Equals(pair.Key, server.Trim(), StringComparison.OrdinalIgnoreCase))
            .Value;
        if (client is not null)
        {
            error = null;
            return true;
        }

        error = ToolResultFactory.InvalidArguments(
            "unknown_mcp_server",
            $"No connected MCP server named '{server}' publishes resources. Servers with resources: {string.Join(", ", clients.Keys)}.",
            new ToolRenderPayload("Unknown MCP server", server));
        return false;
    }
}
