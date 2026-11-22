using System.Text.Json;

namespace StemCode.Infrastructure.Mcp;

internal interface IMcpServerClient : IAsyncDisposable
{
    string Endpoint { get; }

    string ServerName { get; }

    string TransportKind { get; }

    /// <summary>The <c>capabilities</c> object from the server's initialize result.</summary>
    JsonElement ServerCapabilities { get; }

    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>Sends one JSON-RPC request and returns its <c>result</c>.</summary>
    Task<JsonElement> RequestAsync(
        string method,
        Action<Utf8JsonWriter>? writeParams,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<McpRemoteTool>> ListToolsAsync(CancellationToken cancellationToken);

    Task<McpCallToolResult> CallToolAsync(
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken);
}

internal sealed record McpRemoteTool(
    string Name,
    string Description,
    JsonElement InputSchema);

internal sealed record McpCallToolResult(
    bool IsError,
    JsonElement Result,
    string RenderText);
