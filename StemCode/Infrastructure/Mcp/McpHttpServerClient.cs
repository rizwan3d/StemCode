using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace StemCode.Infrastructure.Mcp;

internal sealed class McpHttpServerClient : IMcpServerClient
{
    private readonly HttpClient _httpClient;
    private readonly McpServerConfiguration _configuration;
    private readonly TimeSpan _requestTimeout;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private int _nextRequestId;
    private readonly IMcpAccessTokenProvider? _accessTokenProvider;
    private string? _sessionId;

    public McpHttpServerClient(
        HttpClient httpClient,
        McpServerConfiguration configuration,
        TimeSpan requestTimeout,
        IMcpAccessTokenProvider? accessTokenProvider = null)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _requestTimeout = requestTimeout;
        _accessTokenProvider = accessTokenProvider;
    }

    public string Endpoint => _configuration.Url ?? string.Empty;

    public string ServerName => _configuration.Name;

    public string TransportKind => "http";

    public JsonElement ServerCapabilities { get; private set; }

    public Task<JsonElement> RequestAsync(
        string method,
        Action<Utf8JsonWriter>? writeParams,
        CancellationToken cancellationToken)
    {
        return SendRequestAsync(method, writeParams, cancellationToken);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        JsonElement initializeResult = await SendRequestAsync(
            "initialize",
            McpJson.WriteInitializeParams,
            cancellationToken);
        ServerCapabilities = McpServerFeatures.ReadCapabilities(initializeResult);
        await SendNotificationAsync(
            "notifications/initialized",
            writeParams: null,
            cancellationToken);
    }

    public async Task<IReadOnlyList<McpRemoteTool>> ListToolsAsync(CancellationToken cancellationToken)
    {
        List<McpRemoteTool> tools = [];
        string? cursor = null;

        do
        {
            JsonElement result = await SendRequestAsync(
                "tools/list",
                writer => McpJson.WriteListToolsParams(cursor, writer),
                cancellationToken);
            tools.AddRange(McpJson.ParseTools(result));
            cursor = McpJson.GetNextCursor(result);
        }
        while (!string.IsNullOrWhiteSpace(cursor));

        return tools;
    }

    public async Task<McpCallToolResult> CallToolAsync(
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        JsonElement result = await SendRequestAsync(
            "tools/call",
            writer => McpJson.WriteCallToolParams(toolName, arguments, writer),
            cancellationToken);
        return McpJson.ParseCallToolResult(result);
    }

    public ValueTask DisposeAsync()
    {
        _requestGate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<JsonElement> SendRequestAsync(
        string method,
        Action<Utf8JsonWriter>? writeParams,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource? timeoutSource = CreateRequestTimeoutSource(cancellationToken);
        CancellationToken requestCancellationToken = timeoutSource?.Token ?? cancellationToken;

        int requestId = Interlocked.Increment(ref _nextRequestId);
        string payload = McpJson.BuildRequest(requestId, method, writeParams);

        await _requestGate.WaitAsync(requestCancellationToken);
        try
        {
            JsonElement response = await SendPayloadAsync(
                payload,
                expectResponse: true,
                requestCancellationToken);
            if (response.TryGetProperty("error", out _))
            {
                throw new McpProtocolException(McpJson.GetJsonRpcErrorMessage(response));
            }

            if (!response.TryGetProperty("result", out JsonElement result))
            {
                throw new McpProtocolException(
                    $"MCP server '{ServerName}' returned a response without a result.");
            }

            return result.Clone();
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task SendNotificationAsync(
        string method,
        Action<Utf8JsonWriter>? writeParams,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource? timeoutSource = CreateRequestTimeoutSource(cancellationToken);
        CancellationToken requestCancellationToken = timeoutSource?.Token ?? cancellationToken;

        await _requestGate.WaitAsync(requestCancellationToken);
        try
        {
            await SendPayloadAsync(
                McpJson.BuildNotification(method, writeParams),
                expectResponse: false,
                requestCancellationToken);
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task<JsonElement> SendPayloadAsync(
        string payload,
        bool expectResponse,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_configuration.Url))
        {
            throw new McpProtocolException(
                $"MCP server '{ServerName}' does not define an HTTP URL.");
        }

        using HttpResponseMessage response = await SendWithAuthenticationAsync(payload, cancellationToken);
        CaptureSessionId(response);

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            string wwwAuthenticate = string.Join(", ", response.Headers.WwwAuthenticate.Select(static value => value.ToString()));
            throw new McpAuthenticationException(
                $"MCP server '{ServerName}' requires sign-in. Run /mcp login {ServerName}.",
                wwwAuthenticate);
        }

        if (!response.IsSuccessStatusCode)
        {
            string responseBody = response.Content is null
                ? string.Empty
                : await response.Content.ReadAsStringAsync(cancellationToken);
            string bodySuffix = string.IsNullOrWhiteSpace(responseBody)
                ? string.Empty
                : $" Response: {responseBody.Trim()}";
            throw new McpProtocolException(
                $"MCP server '{ServerName}' returned HTTP {(int)response.StatusCode}.{bodySuffix}");
        }

        HttpContent? content = response.Content;
        if (!expectResponse || content is null)
        {
            return default;
        }

        string contentType = content.Headers.ContentType?.MediaType ?? string.Empty;
        string json;

        if (contentType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
        {
            await using Stream stream = await content.ReadAsStreamAsync(cancellationToken);
            using StreamReader reader = new(stream);
            json = await ReadFirstSseDataEventAsync(reader, cancellationToken);
        }
        else
        {
            json = await content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
            {
                return default;
            }
        }

        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    // Sends the payload with the stored OAuth token (if any). A 401 with a token
    // triggers one forced refresh and retry before the caller sees it.
    private async Task<HttpResponseMessage> SendWithAuthenticationAsync(
        string payload,
        CancellationToken cancellationToken)
    {
        bool usesOAuth = _accessTokenProvider is not null && string.IsNullOrWhiteSpace(_configuration.BearerTokenEnvVar);
        string? accessToken = usesOAuth
            ? await _accessTokenProvider!.GetAccessTokenAsync(forceRefresh: false, cancellationToken)
            : null;

        HttpResponseMessage response = await SendOnceAsync(payload, accessToken, cancellationToken);
        if (response.StatusCode != System.Net.HttpStatusCode.Unauthorized || accessToken is null)
        {
            return response;
        }

        string? refreshed = await _accessTokenProvider!.GetAccessTokenAsync(forceRefresh: true, cancellationToken);
        if (refreshed is null || string.Equals(refreshed, accessToken, StringComparison.Ordinal))
        {
            return response;
        }

        response.Dispose();
        return await SendOnceAsync(payload, refreshed, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        string payload,
        string? accessToken,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, _configuration.Url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        AddConfiguredHeaders(request);
        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
    }

    private CancellationTokenSource? CreateRequestTimeoutSource(CancellationToken cancellationToken)
    {
        if (_requestTimeout == Timeout.InfiniteTimeSpan)
        {
            return null;
        }

        CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_requestTimeout);
        return timeoutSource;
    }

    private void AddConfiguredHeaders(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(_sessionId))
        {
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
        }

        if (!string.IsNullOrWhiteSpace(_configuration.BearerTokenEnvVar))
        {
            string? token = Environment.GetEnvironmentVariable(_configuration.BearerTokenEnvVar);
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
            }
        }

        foreach (KeyValuePair<string, string> header in _configuration.HttpHeaders)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (KeyValuePair<string, string> header in _configuration.EnvHttpHeaders)
        {
            string? value = Environment.GetEnvironmentVariable(header.Value);
            if (!string.IsNullOrWhiteSpace(value))
            {
                request.Headers.TryAddWithoutValidation(header.Key, value);
            }
        }
    }

    private void CaptureSessionId(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Mcp-Session-Id", out IEnumerable<string>? values))
        {
            _sessionId = values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
        }
    }

    private static async Task<string> ReadFirstSseDataEventAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        StringBuilder builder = new();

        while (true)
        {
            string? line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                if (builder.Length > 0)
                {
                    break;
                }

                continue;
            }

            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(line["data:".Length..].TrimStart());
        }

        string json = builder.ToString().Trim();
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new McpProtocolException("The MCP server returned an empty SSE response.");
        }

        return json;
    }
}
