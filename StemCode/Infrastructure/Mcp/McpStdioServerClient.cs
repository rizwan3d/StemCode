using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace StemCode.Infrastructure.Mcp;

internal sealed class McpStdioServerClient : IMcpServerClient
{
    private const int MaxStderrCharacters = 8_000;

    private readonly McpServerConfiguration _configuration;
    private readonly TimeSpan _requestTimeout;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pendingRequests = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _nextRequestId;
    private Process? _process;
    private string _stderr = string.Empty;
    private Task? _stderrTask;
    private Task? _stdoutTask;

    public McpStdioServerClient(
        McpServerConfiguration configuration,
        TimeSpan requestTimeout)
    {
        _configuration = configuration;
        _requestTimeout = requestTimeout;
    }

    public string Endpoint => string.Join(" ", new[] { _configuration.Command }.Concat(_configuration.Args).Where(static value => !string.IsNullOrWhiteSpace(value)));

    public string ServerName => _configuration.Name;

    public string TransportKind => "stdio";

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
        cancellationToken.ThrowIfCancellationRequested();
        EnsureProcessStarted();

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

    public async ValueTask DisposeAsync()
    {
        foreach (TaskCompletionSource<JsonElement> pendingRequest in _pendingRequests.Values)
        {
            pendingRequest.TrySetException(new ObjectDisposedException(nameof(McpStdioServerClient)));
        }

        _pendingRequests.Clear();

        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }

        if (_stdoutTask is not null)
        {
            await WaitQuietlyAsync(_stdoutTask);
        }

        if (_stderrTask is not null)
        {
            await WaitQuietlyAsync(_stderrTask);
        }

        _process?.Dispose();
        _writeGate.Dispose();
    }

    private void EnsureProcessStarted()
    {
        if (_process is not null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_configuration.Command))
        {
            throw new McpProtocolException(
                $"MCP server '{ServerName}' does not define a stdio command.");
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = _configuration.Command,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (!string.IsNullOrWhiteSpace(_configuration.Cwd))
        {
            startInfo.WorkingDirectory = _configuration.Cwd;
        }

        foreach (string argument in _configuration.Args)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (KeyValuePair<string, string> item in BuildEnvironment(_configuration))
        {
            startInfo.Environment[item.Key] = item.Value;
        }

        _process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        _process.Exited += (_, _) => FailPendingRequests(CreateTransportClosedException());

        try
        {
            _process.Start();
        }
        catch (Exception exception)
        {
            throw new McpProtocolException(
                $"MCP server '{ServerName}' failed to start command '{_configuration.Command}': {exception.Message}",
                exception);
        }

        _stdoutTask = Task.Run(ReadStdoutLoopAsync);
        _stderrTask = Task.Run(ReadStderrLoopAsync);
    }

    private static IReadOnlyDictionary<string, string> BuildEnvironment(McpServerConfiguration configuration)
    {
        Dictionary<string, string> environment = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> item in configuration.Env)
        {
            environment[item.Key] = item.Value;
        }

        foreach (string envVar in configuration.EnvVars)
        {
            string? value = Environment.GetEnvironmentVariable(envVar);
            if (value is not null)
            {
                environment[envVar] = value;
            }
        }

        return environment;
    }

    private async Task<JsonElement> SendRequestAsync(
        string method,
        Action<Utf8JsonWriter>? writeParams,
        CancellationToken cancellationToken)
    {
        EnsureProcessStarted();
        using CancellationTokenSource? timeoutSource = CreateRequestTimeoutSource(cancellationToken);
        CancellationToken requestCancellationToken = timeoutSource?.Token ?? cancellationToken;

        int requestId = Interlocked.Increment(ref _nextRequestId);
        TaskCompletionSource<JsonElement> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingRequests.TryAdd(requestId, completion))
        {
            throw new InvalidOperationException("MCP request id collision.");
        }

        string payload = McpJson.BuildRequest(requestId, method, writeParams);

        try
        {
            await WriteLineAsync(payload, requestCancellationToken);
            JsonElement response = await completion.Task.WaitAsync(requestCancellationToken);
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
            _pendingRequests.TryRemove(requestId, out _);
        }
    }

    private async Task SendNotificationAsync(
        string method,
        Action<Utf8JsonWriter>? writeParams,
        CancellationToken cancellationToken)
    {
        EnsureProcessStarted();
        using CancellationTokenSource? timeoutSource = CreateRequestTimeoutSource(cancellationToken);
        CancellationToken requestCancellationToken = timeoutSource?.Token ?? cancellationToken;

        await WriteLineAsync(
            McpJson.BuildNotification(method, writeParams),
            requestCancellationToken);
    }

    private async Task WriteLineAsync(
        string payload,
        CancellationToken cancellationToken)
    {
        Process process = _process ??
            throw new McpProtocolException($"MCP server '{ServerName}' is not running.");

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            if (process.HasExited)
            {
                throw CreateTransportClosedException();
            }

            await process.StandardInput.WriteLineAsync(payload.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadStdoutLoopAsync()
    {
        Process process = _process ??
            throw new InvalidOperationException("MCP process was not started.");

        try
        {
            while (!process.HasExited)
            {
                string? line = await process.StandardOutput.ReadLineAsync();
                if (line is null)
                {
                    break;
                }

                await HandleMessageAsync(line);
            }
        }
        catch (Exception exception)
        {
            FailPendingRequests(exception);
        }
        finally
        {
            FailPendingRequests(CreateTransportClosedException());
        }
    }

    private async Task HandleMessageAsync(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        JsonElement root;
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return;
        }

        if (root.TryGetProperty("id", out JsonElement idElement) &&
            idElement.ValueKind == JsonValueKind.Number &&
            idElement.TryGetInt32(out int id) &&
            _pendingRequests.TryGetValue(id, out TaskCompletionSource<JsonElement>? completion))
        {
            completion.TrySetResult(root);
            return;
        }

        if (root.TryGetProperty("method", out _) &&
            root.TryGetProperty("id", out JsonElement serverRequestId))
        {
            await WriteLineAsync(
                McpJson.BuildMethodNotFoundResponse(serverRequestId),
                CancellationToken.None);
        }
    }

    private async Task ReadStderrLoopAsync()
    {
        Process process = _process ??
            throw new InvalidOperationException("MCP process was not started.");

        char[] buffer = new char[1024];
        StringBuilder builder = new();
        while (true)
        {
            int read = await process.StandardError.ReadAsync(buffer.AsMemory(0, buffer.Length));
            if (read == 0)
            {
                break;
            }

            if (builder.Length < MaxStderrCharacters)
            {
                int remaining = MaxStderrCharacters - builder.Length;
                builder.Append(buffer, 0, Math.Min(read, remaining));
                _stderr = builder.ToString();
            }
        }
    }

    private void FailPendingRequests(Exception exception)
    {
        foreach (TaskCompletionSource<JsonElement> completion in _pendingRequests.Values)
        {
            completion.TrySetException(exception);
        }
    }

    private McpProtocolException CreateTransportClosedException()
    {
        string stderr = string.IsNullOrWhiteSpace(_stderr)
            ? string.Empty
            : $" Stderr: {_stderr.Trim()}";

        return new McpProtocolException(
            $"MCP server '{ServerName}' transport closed.{stderr}");
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

    private static async Task WaitQuietlyAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }
    }
}
