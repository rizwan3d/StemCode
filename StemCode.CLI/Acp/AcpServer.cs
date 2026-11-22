using StemCode.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using StemCode.Application.Backend;
using StemCode.Application.Commands;
using StemCode.Application.Exceptions;
using StemCode.Application.Formatting;
using StemCode.Application.Models;
using StemCode.Application.UI;
using StemCode.Infrastructure.Configuration;
using StemCode.Infrastructure.Storage;
using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Buffers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace StemCode.CLI;

internal sealed class AcpServer : IAsyncDisposable
{
    private const int JsonRpcParseError = -32700;
    private const int JsonRpcInvalidRequest = -32600;
    private const int JsonRpcMethodNotFound = -32601;
    private const int JsonRpcInvalidParams = -32602;
    private const int JsonRpcInternalError = -32603;
    private const int JsonRpcUnauthorized = -32001;
    private const int JsonRpcRateLimitExceeded = -32002;
    private const int ProtocolVersion = 1;
    private const string AcpAuthenticationTokenEnvironmentVariable = "STEMCODE_ACP_AUTH_TOKEN";
    private const int DefaultMaxIncomingLineLength = 10 * 1024 * 1024;
    private const int DefaultMaxRequestQueueDepth = 100;
    private const int DefaultMaxRequestsPerWindow = 100;

    private readonly string? _acpAuthenticationToken;
    private readonly string[] _backendArgs;
    private readonly Func<string[], IReadOnlyList<BackendMcpServerConfiguration>, IStemCodeBackend> _backendFactory;
    private readonly TextWriter _error;
    private readonly TextReader _input;
    private readonly int _maxIncomingLineLength;
    private readonly TextWriter _output;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pendingRequests = new(StringComparer.Ordinal);
    private readonly Channel<JsonElement> _requestQueue;
    private readonly SlidingWindowRequestLimiter _requestRateLimiter;
    private readonly bool _replayLoadedSessionHistory;
    private readonly SemaphoreSlim _outputLock = new(1, 1);
    private readonly string? _providerAuthKey;
    private readonly string _startupDirectory;
    private readonly ConcurrentDictionary<string, AcpSession> _sessions = new(StringComparer.Ordinal);
    private IReadOnlyList<BackendMcpServerConfiguration> _initializeMcpServers = [];
    private bool _isAuthenticated;
    private long _nextRequestId;

    public AcpServer(
        TextReader input,
        TextWriter output,
        TextWriter error,
        string[] backendArgs,
        string? providerAuthKey)
        : this(
            input,
            output,
            error,
            backendArgs,
            providerAuthKey,
            replayLoadedSessionHistory: true,
            autoApproveAllTools: false)
    {
    }

    public AcpServer(
        TextReader input,
        TextWriter output,
        TextWriter error,
        string[] backendArgs,
        string? providerAuthKey,
        bool replayLoadedSessionHistory,
        bool autoApproveAllTools)
        : this(
            input,
            output,
            error,
            backendArgs,
            providerAuthKey,
            replayLoadedSessionHistory,
            autoApproveAllTools,
            // factory runs inside CreateSessionAsync right after
            // SetCurrentDirectory(cwd), so GetCurrentDirectory() == the session cwd.
            // Pinning it here makes the session's tools workspace-independent of
            // global cwd, which is what lets prompts run concurrently.
            (args, sessionMcpServers) => new StemCodeBackend(
                args,
                sessionMcpServers,
                autoApproveAllTools,
                Directory.GetCurrentDirectory()))
    {
    }

    internal AcpServer(
        TextReader input,
        TextWriter output,
        TextWriter error,
        string[] backendArgs,
        string? providerAuthKey,
        Func<string[], IStemCodeBackend> backendFactory)
        : this(
            input,
            output,
            error,
            backendArgs,
            providerAuthKey,
            replayLoadedSessionHistory: true,
            autoApproveAllTools: false,
            (args, _) => (backendFactory ?? throw new ArgumentNullException(nameof(backendFactory)))(args))
    {
    }

    internal AcpServer(
        TextReader input,
        TextWriter output,
        TextWriter error,
        string[] backendArgs,
        string? providerAuthKey,
        bool replayLoadedSessionHistory,
        bool autoApproveAllTools,
        Func<string[], IReadOnlyList<BackendMcpServerConfiguration>, IStemCodeBackend> backendFactory,
        string? acpAuthenticationToken = null,
        int maxIncomingLineLength = DefaultMaxIncomingLineLength,
        int maxRequestQueueDepth = DefaultMaxRequestQueueDepth,
        int maxRequestsPerWindow = DefaultMaxRequestsPerWindow,
        TimeSpan? requestRateLimitWindow = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxIncomingLineLength, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRequestQueueDepth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRequestsPerWindow, 1);

        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _error = error ?? throw new ArgumentNullException(nameof(error));
        _backendArgs = backendArgs ?? throw new ArgumentNullException(nameof(backendArgs));
        _providerAuthKey = NormalizeOrNull(providerAuthKey);
        _replayLoadedSessionHistory = replayLoadedSessionHistory;
        _backendFactory = backendFactory ?? throw new ArgumentNullException(nameof(backendFactory));
        _maxIncomingLineLength = maxIncomingLineLength;
        _requestQueue = Channel.CreateBounded<JsonElement>(
            new BoundedChannelOptions(maxRequestQueueDepth)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });
        _requestRateLimiter = new SlidingWindowRequestLimiter(
            maxRequestsPerWindow,
            requestRateLimitWindow ?? TimeSpan.FromSeconds(1));
        _startupDirectory = Directory.GetCurrentDirectory();
        _acpAuthenticationToken = NormalizeOrNull(acpAuthenticationToken) ??
            LoadAcpAuthenticationToken(_startupDirectory);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Task processor = ProcessRequestQueueAsync(cancellationToken);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await ReadLineWithLimitAsync(_input, _maxIncomingLineLength, cancellationToken);
                }
                catch (IncomingLineTooLongException)
                {
                    await SendErrorAsync(
                        id: null,
                        JsonRpcInvalidRequest,
                        $"JSON-RPC message exceeds the maximum size of {_maxIncomingLineLength} characters.",
                        cancellationToken);
                    continue;
                }

                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                await ProcessIncomingLineAsync(line, cancellationToken);
            }
        }
        finally
        {
            _requestQueue.Writer.TryComplete();
        }

        await processor;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (AcpSession session in _sessions.Values.ToArray())
        {
            await CloseSessionAsync(session, CancellationToken.None);
        }

        _sessions.Clear();
        _outputLock.Dispose();
    }

    private async Task ProcessIncomingLineAsync(string line, CancellationToken cancellationToken)
    {
        if (Encoding.UTF8.GetByteCount(line) > _maxIncomingLineLength)
        {
            await SendErrorAsync(
                id: null,
                JsonRpcInvalidRequest,
                $"JSON-RPC message exceeds the maximum size of {_maxIncomingLineLength} bytes.",
                cancellationToken);
            return;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException exception)
        {
            await SendErrorAsync(
                id: null,
                JsonRpcParseError,
                $"Invalid JSON-RPC message: {exception.Message}",
                cancellationToken);
            return;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                await SendErrorAsync(
                    id: null,
                    JsonRpcInvalidRequest,
                    "JSON-RPC message must be an object.",
                    cancellationToken);
                return;
            }

            if (IsResponse(root))
            {
                RouteResponse(root);
                return;
            }

            if (!_requestRateLimiter.TryAcquire())
            {
                JsonElement? id = TryGetRequestId(root, out JsonElement requestId)
                    ? requestId
                    : null;

                await SendErrorAsync(
                    id,
                    JsonRpcRateLimitExceeded,
                    "ACP request rate limit exceeded.",
                    cancellationToken);
                return;
            }

            if (!TryGetString(root, "method", out string method))
            {
                JsonElement? id = TryGetRequestId(root, out JsonElement requestId)
                    ? requestId
                    : null;

                await SendErrorAsync(
                    id,
                    JsonRpcInvalidRequest,
                    "JSON-RPC request is missing a method.",
                    cancellationToken);
                return;
            }

            if (string.Equals(method, "session/cancel", StringComparison.Ordinal))
            {
                await HandleCancelNotificationAsync(root, cancellationToken);
                return;
            }

            await _requestQueue.Writer.WriteAsync(root.Clone(), cancellationToken);
        }
    }

    private async Task ProcessRequestQueueAsync(CancellationToken cancellationToken)
    {
        // session/prompt is the only long-running method; let prompts for
        // different sessions overlap. Everything else (initialize, session/new,
        // load, close, ...) stays serial so the handshake ordering clients rely
        // on is preserved. Same-session prompts still serialize via TurnLock.
        List<Task> inFlightPrompts = [];
        await foreach (JsonElement root in _requestQueue.Reader.ReadAllAsync(cancellationToken))
        {
            if (IsPromptMethod(root))
            {
                inFlightPrompts.RemoveAll(static task => task.IsCompleted);
                inFlightPrompts.Add(ProcessRequestAsync(root, cancellationToken));
            }
            else
            {
                await ProcessRequestAsync(root, cancellationToken);
            }
        }

        await Task.WhenAll(inFlightPrompts);
    }

    private static bool IsPromptMethod(JsonElement root)
    {
        return TryGetString(root, "method", out string method) &&
            string.Equals(method, "session/prompt", StringComparison.Ordinal);
    }

    private async Task ProcessRequestAsync(JsonElement root, CancellationToken cancellationToken)
    {
        bool hasId = TryGetRequestId(root, out JsonElement id);
        JsonElement? responseId = hasId ? id : null;

        try
        {
            if (!TryGetString(root, "method", out string method))
            {
                throw new AcpProtocolException(JsonRpcInvalidRequest, "JSON-RPC request is missing a method.");
            }

            if (RequiresAuthentication &&
                !_isAuthenticated &&
                !string.Equals(method, "initialize", StringComparison.Ordinal) &&
                !string.Equals(method, "authenticate", StringComparison.Ordinal))
            {
                throw new AcpProtocolException(
                    JsonRpcUnauthorized,
                    $"ACP authentication is required before calling '{method}'.");
            }

            switch (method)
            {
                case "initialize":
                    await HandleInitializeAsync(root, responseId, cancellationToken);
                    break;

                case "authenticate":
                    await HandleAuthenticateAsync(root, responseId, cancellationToken);
                    break;

                case "session/new":
                    await HandleSessionNewAsync(root, responseId, cancellationToken);
                    break;

                case "session/load":
                    await HandleSessionLoadAsync(root, responseId, cancellationToken);
                    break;

                case "session/list":
                    await HandleSessionListAsync(responseId, cancellationToken);
                    break;

                case "session/prompt":
                    await HandleSessionPromptAsync(root, responseId, cancellationToken);
                    break;

                case "session/close":
                    await HandleSessionCloseAsync(root, responseId, cancellationToken);
                    break;

                default:
                    throw new AcpProtocolException(JsonRpcMethodNotFound, $"Method '{method}' is not supported.");
            }
        }
        catch (AcpProtocolException exception)
        {
            if (hasId)
            {
                await SendErrorAsync(responseId, exception.Code, exception.Message, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (hasId)
            {
                await SendErrorAsync(responseId, JsonRpcInternalError, exception.Message, cancellationToken);
            }
            else
            {
                _error.WriteLine($"StemCode ACP request error: {exception.Message}");
            }
        }
    }

    private async Task HandleInitializeAsync(
        JsonElement root,
        JsonElement? id,
        CancellationToken cancellationToken)
    {
        if (TryGetProperty(root, "params", out JsonElement parameters) &&
            parameters.ValueKind == JsonValueKind.Object)
        {
            _initializeMcpServers = AcpMcpServerParser.Parse(parameters, "ACP initialize");
        }

        await SendResultAsync(
            id,
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("protocolVersion", ProtocolVersion);
                writer.WritePropertyName("agentCapabilities");
                writer.WriteStartObject();
                writer.WriteBoolean("loadSession", true);
                writer.WritePropertyName("promptCapabilities");
                writer.WriteStartObject();
                writer.WriteBoolean("image", true);
                writer.WriteBoolean("audio", false);
                writer.WriteBoolean("embeddedContext", true);
                writer.WriteEndObject();
                writer.WritePropertyName("mcpCapabilities");
                writer.WriteStartObject();
                writer.WriteBoolean("http", true);
                writer.WriteBoolean("sse", false);
                writer.WriteEndObject();
                writer.WritePropertyName("sessionCapabilities");
                writer.WriteStartObject();
                writer.WritePropertyName("close");
                writer.WriteStartObject();
                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.WritePropertyName("agentInfo");
                writer.WriteStartObject();
                writer.WriteString("name", "stemcode");
                writer.WriteString("title", "StemCode");
                writer.WriteString("version", GetVersion());
                writer.WriteEndObject();
                writer.WritePropertyName("authMethods");
                writer.WriteStartArray();
                if (RequiresAuthentication)
                {
                    writer.WriteStringValue("token");
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            },
            cancellationToken);
    }

    private async Task HandleAuthenticateAsync(
        JsonElement root,
        JsonElement? id,
        CancellationToken cancellationToken)
    {
        if (!RequiresAuthentication)
        {
            throw new AcpProtocolException(
                JsonRpcInvalidRequest,
                "ACP authentication is not configured for this server.");
        }

        string providedToken = ReadAuthenticationToken(root);
        if (!SecureEquals(_acpAuthenticationToken, providedToken))
        {
            throw new AcpProtocolException(JsonRpcUnauthorized, "Invalid ACP authentication token.");
        }

        _isAuthenticated = true;

        await SendResultAsync(
            id,
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
            },
            cancellationToken);
    }

    private async Task HandleSessionNewAsync(
        JsonElement root,
        JsonElement? id,
        CancellationToken cancellationToken)
    {
        JsonElement parameters = GetRequiredParams(root);
        string cwd = ReadRequiredAbsolutePath(parameters, "cwd");
        IReadOnlyList<BackendMcpServerConfiguration> sessionMcpServers = CreateSessionMcpServers(parameters);

        AcpSession session = await CreateSessionAsync(
            cwd,
            sectionId: null,
            replayHistory: false,
            sessionMcpServers,
            cancellationToken);

        if (!_sessions.TryAdd(session.SessionId, session))
        {
            await CloseSessionAsync(session, cancellationToken);
            throw new AcpProtocolException(
                JsonRpcInvalidRequest,
                $"Session '{session.SessionId}' is already active.");
        }

        await SendResultAsync(
            id,
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("sessionId", session.SessionId);
                writer.WriteEndObject();
            },
            cancellationToken);

        await SendSessionInfoUpdateAsync(session, cancellationToken);
        await SendAvailableCommandsAsync(session, cancellationToken);
    }

    // Reads the on-disk session store directly — no backend/provider init needed just to list.
    private async Task HandleSessionListAsync(
        JsonElement? id,
        CancellationToken cancellationToken)
    {
        JsonConversationSectionStore store = new(new UserDataPathProvider());
        IReadOnlyList<ConversationSectionSnapshot> sessions = await store.ListAsync(cancellationToken);

        await SendResultAsync(
            id,
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteStartArray("sessions");
                foreach (ConversationSectionSnapshot snapshot in sessions)
                {
                    writer.WriteStartObject();
                    writer.WriteString("sessionId", snapshot.SectionId);
                    writer.WriteString("title", snapshot.Title);
                    writer.WriteString("updatedAtUtc", snapshot.UpdatedAtUtc.ToString("O"));
                    writer.WriteString("modelId", snapshot.ActiveModelId);
                    writer.WriteString("profileName", snapshot.AgentProfileName);
                    writer.WriteNumber("turnCount", snapshot.Turns.Count);
                    if (!string.IsNullOrWhiteSpace(snapshot.ParentSessionId))
                    {
                        writer.WriteString("parentSessionId", snapshot.ParentSessionId);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            },
            cancellationToken);
    }

    private async Task HandleSessionLoadAsync(
        JsonElement root,
        JsonElement? id,
        CancellationToken cancellationToken)
    {
        JsonElement parameters = GetRequiredParams(root);
        string cwd = ReadRequiredAbsolutePath(parameters, "cwd");
        string sessionId = ReadRequiredGuidString(parameters, "sessionId");
        IReadOnlyList<BackendMcpServerConfiguration> sessionMcpServers = CreateSessionMcpServers(parameters);

        AcpSession session = await CreateSessionAsync(
            cwd,
            sessionId,
            replayHistory: _replayLoadedSessionHistory,
            sessionMcpServers,
            cancellationToken);

        if (!_sessions.TryAdd(session.SessionId, session))
        {
            await CloseSessionAsync(session, cancellationToken);
            throw new AcpProtocolException(
                JsonRpcInvalidRequest,
                $"Session '{session.SessionId}' is already active.");
        }

        await SendResultAsync(
            id,
            static writer => writer.WriteNullValue(),
            cancellationToken);

        await SendSessionInfoUpdateAsync(session, cancellationToken);
        await SendAvailableCommandsAsync(session, cancellationToken);
    }

    private async Task HandleSessionPromptAsync(
        JsonElement root,
        JsonElement? id,
        CancellationToken cancellationToken)
    {
        JsonElement parameters = GetRequiredParams(root);
        string sessionId = ReadRequiredString(parameters, "sessionId");
        AcpSession session = GetSession(sessionId);
        AcpPrompt prompt = ReadPrompt(parameters);

        await session.TurnLock.WaitAsync(cancellationToken);
        using CancellationTokenSource promptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        session.ActivePromptCancellation = promptCancellation;

        try
        {
            session.Bridge.ResetAssistantMessageChunkTracking();

            string responseText;
            ConversationTurnMetrics? metrics = null;
            bool shouldEmitCompletionChunk = true;
            if (prompt.Attachments.Count == 0 &&
                !McpPromptCommand.IsCandidate(prompt.Text) &&
                prompt.Text.StartsWith("/", StringComparison.Ordinal))
            {
                if (CustomSlashCommandService.TryExpand(
                        session.WorkingDirectory,
                        prompt.Text,
                        out CustomSlashCommandResolution? customCommand,
                        out string? customCommandError))
                {
                    if (customCommand is null)
                    {
                        responseText = customCommandError ?? "Custom command could not be expanded.";
                    }
                    else
                    {
                        ConversationTurnResult result = await session.Backend.RunTurnAsync(
                            customCommand.ExpandedPrompt,
                            prompt.Attachments,
                            session.Bridge,
                            promptCancellation.Token);
                        responseText = result.ResponseText;
                        metrics = result.Metrics;
                        shouldEmitCompletionChunk = !session.Bridge.HasObservedAssistantMessageChunks();
                    }
                }
                else
                {
                    BackendCommandResult commandResult = await session.Backend.RunCommandAsync(
                        prompt.Text,
                        promptCancellation.Token);
                    session.SessionInfo = commandResult.SessionInfo;
                    await SendSessionInfoUpdateAsync(session, cancellationToken);

                    // Commands like /fork, /clone, /import, /resume, and /tree switch the
                    // backend to another section. The terminal re-renders the transcript;
                    // ACP clients get a reset signal followed by the new section's history.
                    if (commandResult.CommandResult.ReplaySession)
                    {
                        await SendSessionHistoryResetAsync(session.SessionId, cancellationToken);
                        await ReplayConversationHistoryAsync(
                            session.SessionId,
                            commandResult.SessionInfo,
                            cancellationToken);
                    }

                    // /reload, /init, and /skill install can change the command set.
                    await SendAvailableCommandsAsync(session, cancellationToken);
                    responseText = FormatCommandResultMessage(commandResult.CommandResult);
                }
            }
            else
            {
                ConversationTurnResult result = await session.Backend.RunTurnAsync(
                    prompt.Text,
                    prompt.Attachments,
                    session.Bridge,
                    promptCancellation.Token);
                responseText = result.ResponseText;
                metrics = result.Metrics;
                shouldEmitCompletionChunk = !session.Bridge.HasObservedAssistantMessageChunks();
            }

            await session.Bridge.FlushAsync();

            if (shouldEmitCompletionChunk &&
                !string.IsNullOrWhiteSpace(responseText))
            {
                await SendAgentMessageChunkAsync(session.SessionId, responseText, cancellationToken);
            }

            await SendFileEditsSummaryAsync(session, cancellationToken);

            await SendResultAsync(
                id,
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("stopReason", "end_turn");
                    if (metrics is not null)
                    {
                        writer.WritePropertyName("metrics");
                        WritePromptMetrics(writer, metrics);
                    }

                    writer.WriteEndObject();
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (promptCancellation.IsCancellationRequested)
        {
            await session.Bridge.FlushAsync();
            await SendResultAsync(
                id,
                static writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("stopReason", "cancelled");
                    writer.WriteEndObject();
                },
                CancellationToken.None);
        }
        catch (PromptCancelledException)
        {
            await session.Bridge.FlushAsync();
            await SendResultAsync(
                id,
                static writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("stopReason", "cancelled");
                    writer.WriteEndObject();
                },
                CancellationToken.None);
        }
        finally
        {
            session.ActivePromptCancellation = null;
            session.TurnLock.Release();
        }
    }

    private async Task HandleSessionCloseAsync(
        JsonElement root,
        JsonElement? id,
        CancellationToken cancellationToken)
    {
        JsonElement parameters = GetRequiredParams(root);
        string sessionId = ReadRequiredString(parameters, "sessionId");
        AcpSession session = GetSession(sessionId);

        _sessions.TryRemove(sessionId, out _);
        await CloseSessionAsync(session, cancellationToken);

        await SendResultAsync(
            id,
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
            },
            cancellationToken);
    }

    private Task HandleCancelNotificationAsync(JsonElement root, CancellationToken cancellationToken)
    {
        if (!TryGetProperty(root, "params", out JsonElement parameters) ||
            !TryGetString(parameters, "sessionId", out string sessionId))
        {
            return Task.CompletedTask;
        }

        if (_sessions.TryGetValue(sessionId, out AcpSession? session))
        {
            session.ActivePromptCancellation?.Cancel();
        }

        return Task.CompletedTask;
    }

    private async Task<AcpSession> CreateSessionAsync(
        string cwd,
        string? sectionId,
        bool replayHistory,
        IReadOnlyList<BackendMcpServerConfiguration> sessionMcpServers,
        CancellationToken cancellationToken)
    {
        // Session creation is processed serially (only session/prompt runs
        // concurrently), so setting global cwd here is race-free. The default
        // backend factory captures this cwd as the session's fixed workspace
        // root; backend init also reads cwd for config/git probes.
        Directory.SetCurrentDirectory(cwd);

        string[] backendArgs = CreateBackendArgs(sectionId);
        IStemCodeBackend backend = _backendFactory(backendArgs, sessionMcpServers);
        ToolExecutionSettings toolExecutionSettings = LoadToolExecutionSettings(cwd);
        AcpUiBridge bridge = new(
            this,
            _error,
            _providerAuthKey,
            GetAcpRequestTimeout(toolExecutionSettings));

        try
        {
            BackendSessionInfo sessionInfo = await backend.InitializeAsync(bridge, cancellationToken);
            bridge.SessionId = sessionInfo.SessionId;

            AcpSession session = new(
                sessionInfo.SessionId,
                cwd,
                backend,
                bridge,
                sessionInfo);

            if (replayHistory)
            {
                await ReplayConversationHistoryAsync(sessionInfo.SessionId, sessionInfo, cancellationToken);
            }

            return session;
        }
        catch
        {
            await backend.DisposeAsync();
            throw;
        }
    }

    // acpSessionId is the client-facing id; it can differ from sessionInfo.SessionId
    // after commands such as /fork or /resume switch the backend to another section.
    private async Task ReplayConversationHistoryAsync(
        string acpSessionId,
        BackendSessionInfo sessionInfo,
        CancellationToken cancellationToken)
    {
        foreach (BackendConversationMessage message in sessionInfo.ConversationHistory)
        {
            if (string.IsNullOrWhiteSpace(message.Content))
            {
                continue;
            }

            bool isUserMessage = string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase);
            if (!isUserMessage)
            {
                string? reasoningText = ExtractReasoningTextForDisplay(message);
                if (!string.IsNullOrWhiteSpace(reasoningText))
                {
                    await SendThinkingAsync(
                        acpSessionId,
                        reasoningText,
                        cancellationToken);
                }
            }

            string updateKind = isUserMessage
                ? "user_message_chunk"
                : "agent_message_chunk";

            await SendMessageChunkAsync(
                acpSessionId,
                updateKind,
                message.Content,
                cancellationToken);
        }
    }

    private static string? ExtractReasoningTextForDisplay(BackendConversationMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.ReasoningContent))
        {
            return message.ReasoningContent.Trim();
        }

        return ExtractReasoningDetailsText(message.ReasoningDetailsJson);
    }

    private static string? ExtractReasoningDetailsText(string? reasoningDetailsJson)
    {
        if (string.IsNullOrWhiteSpace(reasoningDetailsJson))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(reasoningDetailsJson);
            List<string> parts = [];
            CollectReasoningText(document.RootElement, parts);

            return parts.Count == 0
                ? null
                : string.Join(
                    Environment.NewLine + Environment.NewLine,
                    parts.Distinct(StringComparer.Ordinal));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void CollectReasoningText(
        JsonElement element,
        List<string> parts)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                AddReasoningTextProperty(element, parts, "text");
                AddReasoningTextProperty(element, parts, "summary");
                AddReasoningTextProperty(element, parts, "content");

                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        CollectReasoningText(property.Value, parts);
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    CollectReasoningText(item, parts);
                }

                break;
        }
    }

    private static void AddReasoningTextProperty(
        JsonElement element,
        List<string> parts,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
        {
            return;
        }

        if (value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString()))
        {
            parts.Add(value.GetString()!.Trim());
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(item.GetString()))
                {
                    parts.Add(item.GetString()!.Trim());
                }
            }
        }
    }

    private async Task CloseSessionAsync(AcpSession session, CancellationToken cancellationToken)
    {
        session.ActivePromptCancellation?.Cancel();
        await session.Backend.DisposeAsync();

        try
        {
            Directory.SetCurrentDirectory(_startupDirectory);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private async Task SendSessionInfoUpdateAsync(AcpSession session, CancellationToken cancellationToken)
    {
        await SendNotificationAsync(
            "session/update",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("sessionId", session.SessionId);
                writer.WritePropertyName("update");
                writer.WriteStartObject();
                writer.WriteString("sessionUpdate", "session_info_update");
                writer.WriteString("updatedAt", DateTimeOffset.UtcNow.ToString("O"));
                WriteSessionInfo(writer, session.SessionInfo);
                writer.WriteEndObject();
                writer.WriteEndObject();
            },
            cancellationToken);
    }

    private Task SendSessionHistoryResetAsync(string sessionId, CancellationToken cancellationToken)
    {
        return SendNotificationAsync(
            "session/update",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("sessionId", sessionId);
                writer.WritePropertyName("update");
                writer.WriteStartObject();
                writer.WriteString("sessionUpdate", "session_history_reset");
                writer.WriteEndObject();
                writer.WriteEndObject();
            },
            cancellationToken);
    }

    // Standard ACP available_commands_update. Names omit the leading slash per the ACP
    // spec; "usage" is a StemCode extension field clients may show as a hint.
    private async Task SendAvailableCommandsAsync(AcpSession session, CancellationToken cancellationToken)
    {
        IReadOnlyList<McpPromptDescriptor> mcpPrompts;
        try
        {
            // Listing prompts may connect to MCP servers, so keep it off the caller's thread.
            mcpPrompts = await Task.Run(() => session.Backend.ListMcpPrompts(), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            mcpPrompts = [];
        }

        IReadOnlyList<AcpAvailableCommand> commands = GetAvailableCommands(session.WorkingDirectory, mcpPrompts);

        await SendNotificationAsync(
            "session/update",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("sessionId", session.SessionId);
                writer.WritePropertyName("update");
                writer.WriteStartObject();
                writer.WriteString("sessionUpdate", "available_commands_update");
                writer.WriteStartArray("availableCommands");
                foreach (AcpAvailableCommand command in commands)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", command.Name);
                    writer.WriteString("description", command.Description);
                    writer.WriteString("usage", command.Usage);
                    writer.WriteBoolean("custom", command.IsCustom);
                    if (!string.IsNullOrWhiteSpace(command.InputHint))
                    {
                        writer.WritePropertyName("input");
                        writer.WriteStartObject();
                        writer.WriteString("hint", command.InputHint);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.WriteEndObject();
            },
            cancellationToken);
    }

    internal static IReadOnlyList<AcpAvailableCommand> GetAvailableCommands(
        string workingDirectory,
        IReadOnlyList<McpPromptDescriptor>? mcpPrompts = null)
    {
        List<AcpAvailableCommand> commands = ReplCommandCatalog.All
            .Select(static metadata => CreateAvailableCommand(
                metadata.CommandName,
                metadata.Usage,
                metadata.Description,
                isCustom: false))
            .ToList();

        HashSet<string> builtInNames = new(
            commands.Select(static command => command.Name),
            StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<CustomSlashCommandDescriptor> customCommands;
        try
        {
            customCommands = CustomSlashCommandService.List(workingDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            customCommands = [];
        }

        foreach (CustomSlashCommandDescriptor custom in customCommands)
        {
            string name = custom.Command.TrimStart('/');
            if (name.Length == 0 || builtInNames.Contains(name))
            {
                continue;
            }

            commands.Add(CreateAvailableCommand(name, custom.Usage, custom.Description, isCustom: true));
        }

        foreach (McpPromptDescriptor prompt in mcpPrompts ?? [])
        {
            commands.Add(CreateAvailableCommand(
                prompt.CommandName,
                prompt.Usage,
                string.IsNullOrWhiteSpace(prompt.Description)
                    ? $"MCP prompt '{prompt.PromptName}' from {prompt.ServerName}."
                    : $"{prompt.Description.Trim()} (MCP: {prompt.ServerName})",
                isCustom: true));
        }

        return commands
            .OrderBy(static command => command.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static AcpAvailableCommand CreateAvailableCommand(
        string name,
        string usage,
        string description,
        bool isCustom)
    {
        string normalizedUsage = string.IsNullOrWhiteSpace(usage) ? "/" + name : usage.Trim();
        string prefix = "/" + name;
        string? inputHint = normalizedUsage.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? normalizedUsage[prefix.Length..].Trim()
            : null;

        return new AcpAvailableCommand(
            name,
            description ?? string.Empty,
            normalizedUsage,
            string.IsNullOrWhiteSpace(inputHint) ? null : inputHint,
            isCustom);
    }

    internal sealed record AcpAvailableCommand(
        string Name,
        string Description,
        string Usage,
        string? InputHint,
        bool IsCustom);

    private async Task SendFileEditsSummaryAsync(AcpSession session, CancellationToken cancellationToken)
    {
        IReadOnlyList<FileEditSummary> files = session.Backend.GetFileEditSummary();
        if (files.Count == 0)
        {
            return;
        }

        await SendNotificationAsync(
            "session/update",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("sessionId", session.SessionId);
                writer.WritePropertyName("update");
                writer.WriteStartObject();
                writer.WriteString("sessionUpdate", "file_edits_summary");
                writer.WritePropertyName("files");
                writer.WriteStartArray();
                foreach (FileEditSummary file in files)
                {
                    writer.WriteStartObject();
                    writer.WriteString("displayPath", file.DisplayPath);
                    writer.WriteString("absolutePath", file.AbsolutePath);
                    writer.WriteNumber("addedLineCount", file.AddedLineCount);
                    writer.WriteNumber("removedLineCount", file.RemovedLineCount);
                    writer.WriteNumber("editCount", file.EditCount);
                    writer.WriteString("action", file.Action);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.WriteEndObject();
            },
            cancellationToken);
    }

    private static void WriteSessionInfo(Utf8JsonWriter writer, BackendSessionInfo sessionInfo)
    {
        writer.WriteString("sectionResumeCommand", sessionInfo.SectionResumeCommand);
        writer.WriteString("providerName", sessionInfo.ProviderName);
        writer.WriteString("modelId", sessionInfo.ModelId);
        writer.WritePropertyName("activeModelContextWindowTokens");
        if (sessionInfo.ActiveModelContextWindowTokens is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteNumberValue(sessionInfo.ActiveModelContextWindowTokens.Value);
        }

        writer.WritePropertyName("availableModelIds");
        writer.WriteStartArray();
        foreach (string modelId in sessionInfo.AvailableModelIds)
        {
            writer.WriteStringValue(modelId);
        }

        writer.WriteEndArray();
        writer.WriteNumber("totalEstimatedOutputTokens", sessionInfo.TotalEstimatedOutputTokens);
        writer.WriteNumber("sectionEstimatedContextTokens", sessionInfo.SectionEstimatedContextTokens);
        writer.WriteString("thinkingMode", sessionInfo.ThinkingMode);
        writer.WriteString("reasoningEffort", sessionInfo.ReasoningEffort);
        writer.WriteString("agentProfileName", sessionInfo.AgentProfileName);
        writer.WritePropertyName("availableAgentProfiles");
        writer.WriteStartArray();
        foreach (BackendAgentProfileInfo profile in sessionInfo.AvailableAgentProfiles)
        {
            writer.WriteStartObject();
            writer.WriteString("name", profile.Name);
            writer.WriteString("mode", profile.Mode);
            writer.WriteString("description", profile.Description);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteString("sectionTitle", sessionInfo.SectionTitle);
    }

    private static void WritePromptMetrics(
        Utf8JsonWriter writer,
        ConversationTurnMetrics metrics)
    {
        writer.WriteStartObject();
        writer.WriteNumber("elapsedMilliseconds", metrics.Elapsed.TotalMilliseconds);
        writer.WriteNumber("estimatedInputTokens", metrics.EstimatedInputTokens);
        writer.WriteNumber("estimatedOutputTokens", metrics.EstimatedOutputTokens);
        writer.WriteNumber("displayedEstimatedOutputTokens", metrics.DisplayedEstimatedOutputTokens);
        writer.WriteNumber("estimatedTotalTokens", metrics.EstimatedTotalTokens);
        writer.WriteNumber("cachedInputTokens", metrics.CachedInputTokens);
        writer.WriteNumber("providerRetryCount", metrics.ProviderRetryCount);
        writer.WriteNumber("toolRoundCount", metrics.ToolRoundCount);
        if (metrics.SessionEstimatedOutputTokens is not null)
        {
            writer.WriteNumber("sessionEstimatedOutputTokens", metrics.SessionEstimatedOutputTokens.Value);
        }

        writer.WriteEndObject();
    }

    private static ToolExecutionSettings LoadToolExecutionSettings(string workspaceRoot)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddJsonFile(
                Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                optional: true,
                reloadOnChange: false)
            .AddJsonFile(
                Path.Combine(workspaceRoot, ".stemcode", "agent-profile.json"),
                optional: true,
                reloadOnChange: false)
            .Build();

        ToolExecutionSettings configured = new();
        configuration.GetSection($"{ApplicationOptions.SectionName}:Tools").Bind(configured);

        return ApplicationSettingsFactory.CreateToolExecutionSettings(
            new ApplicationOptions
            {
                Tools = configured
            });
    }

    private static string? LoadAcpAuthenticationToken(string workspaceRoot)
    {
        string? configuredToken = new ConfigurationBuilder()
            .AddJsonFile(
                Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                optional: true,
                reloadOnChange: false)
            .AddJsonFile(
                Path.Combine(workspaceRoot, ".stemcode", "agent-profile.json"),
                optional: true,
                reloadOnChange: false)
            .Build()[$"{ApplicationOptions.SectionName}:Acp:AuthenticationToken"];

        return NormalizeOrNull(Environment.GetEnvironmentVariable(AcpAuthenticationTokenEnvironmentVariable)) ??
            NormalizeOrNull(configuredToken);
    }

    private static TimeSpan GetAcpRequestTimeout(ToolExecutionSettings settings)
    {
        return settings.AcpRequestTimeoutSeconds > 0
            ? TimeSpan.FromSeconds(settings.AcpRequestTimeoutSeconds)
            : Timeout.InfiniteTimeSpan;
    }

    internal async Task<JsonElement> SendClientRequestAsync(
        string method,
        Action<Utf8JsonWriter> writeParams,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        long id = Interlocked.Increment(ref _nextRequestId);
        string key = CreateIdKey(id);
        TaskCompletionSource<JsonElement> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_pendingRequests.TryAdd(key, completion))
        {
            throw new InvalidOperationException("Duplicate ACP request id.");
        }

        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            if (_pendingRequests.TryRemove(key, out TaskCompletionSource<JsonElement>? pending))
            {
                pending.TrySetCanceled(cancellationToken);
            }
        });

        try
        {
            await WriteMessageAsync(
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("jsonrpc", "2.0");
                    writer.WriteNumber("id", id);
                    writer.WriteString("method", method);
                    writer.WritePropertyName("params");
                    writeParams(writer);
                    writer.WriteEndObject();
                },
                cancellationToken);

            return timeout == Timeout.InfiniteTimeSpan
                ? await completion.Task.WaitAsync(cancellationToken)
                : await completion.Task.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            _pendingRequests.TryRemove(key, out _);
            throw new TimeoutException(
                $"ACP request '{method}' timed out after {timeout.TotalSeconds:0} seconds.");
        }
        catch
        {
            _pendingRequests.TryRemove(key, out _);
            throw;
        }
    }

    internal Task SendToolCallAsync(
        string sessionId,
        ConversationToolCall toolCall,
        string title,
        string kind,
        CancellationToken cancellationToken)
    {
        return SendNotificationAsync(
            "session/update",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("sessionId", sessionId);
                writer.WritePropertyName("update");
                writer.WriteStartObject();
                writer.WriteString("sessionUpdate", "tool_call");
                writer.WriteString("toolCallId", toolCall.Id);
                writer.WriteString("title", title);
                writer.WriteString("kind", kind);
                writer.WriteString("status", "pending");
                WriteJsonObjectOrString(writer, "rawInput", toolCall.ArgumentsJson);
                writer.WriteEndObject();
                writer.WriteEndObject();
            },
            cancellationToken);
    }

    internal Task SendToolCallUpdateAsync(
        string sessionId,
        string toolCallId,
        bool success,
        string content,
        CancellationToken cancellationToken)
    {
        return SendNotificationAsync(
            "session/update",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("sessionId", sessionId);
                writer.WritePropertyName("update");
                writer.WriteStartObject();
                writer.WriteString("sessionUpdate", "tool_call_update");
                writer.WriteString("toolCallId", toolCallId);
                writer.WriteString("status", success ? "completed" : "failed");
                writer.WritePropertyName("content");
                writer.WriteStartArray();
                WriteToolTextContent(writer, content);
                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.WriteEndObject();
            },
            cancellationToken);
    }

    internal Task SendPlanAsync(
        string sessionId,
        ExecutionPlanProgress progress,
        CancellationToken cancellationToken)
    {
        return SendNotificationAsync(
            "session/update",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("sessionId", sessionId);
                writer.WritePropertyName("update");
                writer.WriteStartObject();
                writer.WriteString("sessionUpdate", "plan");
                writer.WritePropertyName("entries");
                writer.WriteStartArray();

                for (int index = 0; index < progress.Tasks.Count; index++)
                {
                    string status = index < progress.CompletedTaskCount
                        ? "completed"
                        : index == progress.CompletedTaskCount
                            ? "in_progress"
                            : "pending";

                    writer.WriteStartObject();
                    writer.WriteString("content", progress.Tasks[index]);
                    writer.WriteString("priority", "medium");
                    writer.WriteString("status", status);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.WriteEndObject();
            },
            cancellationToken);
    }

    internal Task SendThinkingAsync(
        string sessionId,
        string reasoningText,
        CancellationToken cancellationToken)
    {
        return SendReasoningChunkAsync(
            sessionId,
            reasoningText.Trim(),
            cancellationToken);
    }

    internal Task SendAgentMessageChunkAsync(
        string sessionId,
        string text,
        CancellationToken cancellationToken)
    {
        return SendMessageChunkAsync(
            sessionId,
            "agent_message_chunk",
            text,
            cancellationToken);
    }

    private Task SendMessageChunkAsync(
        string sessionId,
        string updateKind,
        string text,
        CancellationToken cancellationToken)
    {
        return SendNotificationAsync(
            "session/update",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("sessionId", sessionId);
                writer.WritePropertyName("update");
                writer.WriteStartObject();
                writer.WriteString("sessionUpdate", updateKind);
                writer.WritePropertyName("content");
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", text);
                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.WriteEndObject();
            },
            cancellationToken);
    }

    private Task SendReasoningChunkAsync(
        string sessionId,
        string text,
        CancellationToken cancellationToken)
    {
        return SendNotificationAsync(
            "session/update",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("sessionId", sessionId);
                writer.WritePropertyName("update");
                writer.WriteStartObject();
                writer.WriteString("sessionUpdate", "agent_reasoning_chunk");
                writer.WritePropertyName("content");
                writer.WriteStartObject();
                writer.WriteString("type", "thinking");
                writer.WriteString("text", text);
                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.WriteEndObject();
            },
            cancellationToken);
    }

    private async Task SendResultAsync(
        JsonElement? id,
        Action<Utf8JsonWriter> writeResult,
        CancellationToken cancellationToken)
    {
        if (id is null)
        {
            return;
        }

        await WriteMessageAsync(
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("jsonrpc", "2.0");
                writer.WritePropertyName("id");
                WriteId(writer, id);
                writer.WritePropertyName("result");
                writeResult(writer);
                writer.WriteEndObject();
            },
            cancellationToken);
    }

    private async Task SendErrorAsync(
        JsonElement? id,
        int code,
        string message,
        CancellationToken cancellationToken)
    {
        await WriteMessageAsync(
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("jsonrpc", "2.0");
                writer.WritePropertyName("id");
                WriteId(writer, id);
                writer.WritePropertyName("error");
                writer.WriteStartObject();
                writer.WriteNumber("code", code);
                writer.WriteString("message", message);
                writer.WriteEndObject();
                writer.WriteEndObject();
            },
            cancellationToken);
    }

    private Task SendNotificationAsync(
        string method,
        Action<Utf8JsonWriter> writeParams,
        CancellationToken cancellationToken)
    {
        return WriteMessageAsync(
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("jsonrpc", "2.0");
                writer.WriteString("method", method);
                writer.WritePropertyName("params");
                writeParams(writer);
                writer.WriteEndObject();
            },
            cancellationToken);
    }

    private async Task WriteMessageAsync(
        Action<Utf8JsonWriter> writeMessage,
        CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> bufferWriter = new();
        using (Utf8JsonWriter writer = new(bufferWriter))
        {
            writeMessage(writer);
        }

        string message = Encoding.UTF8.GetString(bufferWriter.WrittenSpan);
        await _outputLock.WaitAsync(cancellationToken);
        try
        {
            await _output.WriteLineAsync(message);
            await _output.FlushAsync();
        }
        finally
        {
            _outputLock.Release();
        }
    }

    private void RouteResponse(JsonElement root)
    {
        if (!TryGetRequestId(root, out JsonElement id))
        {
            return;
        }

        string key = CreateIdKey(id);
        if (!_pendingRequests.TryRemove(key, out TaskCompletionSource<JsonElement>? completion))
        {
            return;
        }

        if (TryGetProperty(root, "error", out JsonElement error))
        {
            int code = TryGetInt32(error, "code", out int errorCode)
                ? errorCode
                : JsonRpcInternalError;
            string message = TryGetString(error, "message", out string errorMessage)
                ? errorMessage
                : "ACP client returned an error.";

            completion.TrySetException(new AcpRemoteException(code, message));
            return;
        }

        if (TryGetProperty(root, "result", out JsonElement result))
        {
            completion.TrySetResult(result.Clone());
            return;
        }

        completion.TrySetResult(default);
    }

    private AcpSession GetSession(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out AcpSession? session))
        {
            throw new AcpProtocolException(JsonRpcInvalidParams, $"Unknown session '{sessionId}'.");
        }

        return session;
    }

    private string[] CreateBackendArgs(string? sectionId)
    {
        List<string> args = [];
        bool skipUpdateCheck = false;
        for (int index = 0; index < _backendArgs.Length; index++)
        {
            string arg = _backendArgs[index];
            if (IsOptionWithValue(arg, "--section") ||
                IsOptionWithValue(arg, "--session"))
            {
                if (string.Equals(arg, "--section", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--session", StringComparison.OrdinalIgnoreCase))
                {
                    index++;
                }

                continue;
            }

            if (string.Equals(arg, "--no-update-check", StringComparison.OrdinalIgnoreCase))
            {
                skipUpdateCheck = true;
                continue;
            }

            if (string.Equals(arg, "--no-old-reader", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            args.Add(arg);
        }

        if (!string.IsNullOrWhiteSpace(sectionId))
        {
            args.Add("--section");
            args.Add(sectionId.Trim());
        }

        if (skipUpdateCheck)
        {
            args.Add("--no-update-check");
        }

        return args.ToArray();
    }

    private static AcpPrompt ReadPrompt(JsonElement parameters)
    {
        if (!TryGetProperty(parameters, "prompt", out JsonElement promptElement) ||
            promptElement.ValueKind != JsonValueKind.Array)
        {
            throw new AcpProtocolException(JsonRpcInvalidParams, "session/prompt requires a prompt array.");
        }

        StringBuilder text = new();
        List<ConversationAttachment> attachments = [];
        int attachmentIndex = 0;

        foreach (JsonElement block in promptElement.EnumerateArray())
        {
            if (!TryGetString(block, "type", out string type))
            {
                continue;
            }

            switch (type)
            {
                case "text":
                    if (TryGetString(block, "text", out string blockText))
                    {
                        AppendParagraph(text, blockText);
                    }

                    break;

                case "image":
                    if (TryGetString(block, "data", out string imageData) &&
                        TryGetString(block, "mimeType", out string imageMimeType))
                    {
                        attachments.Add(new ConversationAttachment(
                            CreateAttachmentName(block, ++attachmentIndex, imageMimeType),
                            imageMimeType,
                            imageData));
                    }

                    break;

                case "resource":
                    AppendResource(text, attachments, block, ref attachmentIndex);
                    break;

                case "resource_link":
                    AppendResourceLink(text, block);
                    break;
            }
        }

        string normalizedText = text.ToString().Trim();
        if (string.IsNullOrWhiteSpace(normalizedText) && attachments.Count > 0)
        {
            normalizedText = "Please review the attached context.";
        }

        if (string.IsNullOrWhiteSpace(normalizedText))
        {
            throw new AcpProtocolException(JsonRpcInvalidParams, "session/prompt requires text or supported context.");
        }

        return new AcpPrompt(normalizedText, attachments);
    }

    private static void AppendResource(
        StringBuilder text,
        List<ConversationAttachment> attachments,
        JsonElement block,
        ref int attachmentIndex)
    {
        if (!TryGetProperty(block, "resource", out JsonElement resource) ||
            resource.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        string uri = TryGetString(resource, "uri", out string resourceUri)
            ? resourceUri
            : "resource";
        string mimeType = TryGetString(resource, "mimeType", out string resourceMimeType)
            ? resourceMimeType
            : "text/plain";

        if (TryGetString(resource, "text", out string resourceText))
        {
            AppendParagraph(
                text,
                $"Resource: {uri}{Environment.NewLine}{resourceText}");
            return;
        }

        if (TryGetString(resource, "blob", out string blob) &&
            mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            attachments.Add(new ConversationAttachment(
                CreateAttachmentName(resource, ++attachmentIndex, mimeType),
                mimeType,
                blob));
            return;
        }

        AppendParagraph(text, $"Resource attached but not embedded: {uri}");
    }

    private static void AppendResourceLink(StringBuilder text, JsonElement block)
    {
        string uri = TryGetString(block, "uri", out string linkUri)
            ? linkUri
            : "resource";
        string name = TryGetString(block, "title", out string title)
            ? title
            : TryGetString(block, "name", out string linkName)
                ? linkName
                : uri;

        AppendParagraph(text, $"Resource link: {name} ({uri})");
    }

    private static string CreateAttachmentName(JsonElement block, int attachmentIndex, string mimeType)
    {
        if (TryGetString(block, "uri", out string uri) &&
            Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsedUri) &&
            !string.IsNullOrWhiteSpace(Path.GetFileName(parsedUri.LocalPath)))
        {
            return Path.GetFileName(parsedUri.LocalPath);
        }

        string extension = mimeType.EndsWith("/jpeg", StringComparison.OrdinalIgnoreCase)
            ? ".jpg"
            : mimeType.EndsWith("/png", StringComparison.OrdinalIgnoreCase)
                ? ".png"
                : ".bin";

        return $"attachment-{attachmentIndex}{extension}";
    }

    private static void AppendParagraph(StringBuilder builder, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine();
        }

        builder.Append(value.Trim());
    }

    private static string FormatCommandResultMessage(ReplCommandResult result)
    {
        if (string.IsNullOrWhiteSpace(result.Message))
        {
            return string.Empty;
        }

        string prefix = result.FeedbackKind switch
        {
            ReplFeedbackKind.Error => "Error: ",
            ReplFeedbackKind.Warning => "Warning: ",
            _ => string.Empty
        };

        return prefix + result.Message.Trim();
    }

    private static JsonElement GetRequiredParams(JsonElement root)
    {
        if (!TryGetProperty(root, "params", out JsonElement parameters) ||
            parameters.ValueKind != JsonValueKind.Object)
        {
            throw new AcpProtocolException(JsonRpcInvalidParams, "Request params must be an object.");
        }

        return parameters;
    }

    private static string ReadRequiredAbsolutePath(JsonElement element, string propertyName)
    {
        string path = ReadRequiredString(element, propertyName);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new AcpProtocolException(JsonRpcInvalidParams, $"{propertyName} must be an absolute path.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new AcpProtocolException(JsonRpcInvalidParams, $"{propertyName} must be an absolute path.");
        }

        if (!Path.IsPathFullyQualified(fullPath))
        {
            throw new AcpProtocolException(JsonRpcInvalidParams, $"{propertyName} must be an absolute path.");
        }

        if (!Directory.Exists(fullPath))
        {
            throw new AcpProtocolException(JsonRpcInvalidParams, $"{propertyName} does not exist.");
        }

        return fullPath;
    }

    private static string ReadRequiredString(JsonElement element, string propertyName)
    {
        if (!TryGetString(element, propertyName, out string value) ||
            string.IsNullOrWhiteSpace(value))
        {
            throw new AcpProtocolException(JsonRpcInvalidParams, $"Missing required string parameter '{propertyName}'.");
        }

        return value.Trim();
    }

    private static string ReadRequiredGuidString(JsonElement element, string propertyName)
    {
        string value = ReadRequiredString(element, propertyName);
        if (!Guid.TryParse(value, out Guid parsedGuid))
        {
            throw new AcpProtocolException(JsonRpcInvalidParams, $"{propertyName} must be a valid GUID.");
        }

        return parsedGuid.ToString("D");
    }

    private IReadOnlyList<BackendMcpServerConfiguration> CreateSessionMcpServers(JsonElement parameters)
    {
        IReadOnlyList<BackendMcpServerConfiguration> sessionMcpServers =
            AcpMcpServerParser.Parse(parameters, "ACP session");

        if (_initializeMcpServers.Count == 0)
        {
            return sessionMcpServers;
        }

        if (sessionMcpServers.Count == 0)
        {
            return _initializeMcpServers;
        }

        return _initializeMcpServers
            .Concat(sessionMcpServers)
            .ToArray();
    }

    private static bool IsResponse(JsonElement root)
    {
        return TryGetRequestId(root, out _) &&
            (TryGetProperty(root, "result", out _) || TryGetProperty(root, "error", out _)) &&
            !TryGetProperty(root, "method", out _);
    }

    private static bool TryGetRequestId(JsonElement root, out JsonElement id)
    {
        return TryGetProperty(root, "id", out id);
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement property)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            property = default;
            return false;
        }

        return element.TryGetProperty(propertyName, out property);
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!TryGetProperty(element, propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryGetInt32(JsonElement element, string propertyName, out int value)
    {
        value = 0;
        return TryGetProperty(element, propertyName, out JsonElement property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out value);
    }

    private static bool IsOptionWithValue(string arg, string optionName)
    {
        return string.Equals(arg, optionName, StringComparison.OrdinalIgnoreCase) ||
            arg.StartsWith(optionName + "=", StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteId(Utf8JsonWriter writer, JsonElement? id)
    {
        if (id is null)
        {
            writer.WriteNullValue();
            return;
        }

        id.Value.WriteTo(writer);
    }

    internal static void WriteToolTextContent(Utf8JsonWriter writer, string content)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "content");
        writer.WritePropertyName("content");
        writer.WriteStartObject();
        writer.WriteString("type", "text");
        writer.WriteString("text", content);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteJsonObjectOrString(
        Utf8JsonWriter writer,
        string propertyName,
        string json)
    {
        writer.WritePropertyName(propertyName);
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            document.RootElement.WriteTo(writer);
        }
        catch (JsonException)
        {
            writer.WriteStringValue(json);
        }
    }

    private static string CreateIdKey(long id)
    {
        return $"n:{id}";
    }

    private static string CreateIdKey(JsonElement id)
    {
        return id.ValueKind switch
        {
            JsonValueKind.Number when id.TryGetInt64(out long number) => CreateIdKey(number),
            JsonValueKind.String => "s:" + (id.GetString() ?? string.Empty),
            JsonValueKind.Null => "null",
            _ => id.GetRawText()
        };
    }

    private static string GetVersion()
    {
        return typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
            typeof(Program).Assembly.GetName().Version?.ToString() ??
            "0.0.0";
    }

    private static string? NormalizeOrNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }

    private static async Task<string?> ReadLineWithLimitAsync(
        TextReader reader,
        int maxLineLength,
        CancellationToken cancellationToken)
    {
        char[] buffer = new char[1];
        StringBuilder line = new(Math.Min(maxLineLength, 4096));
        bool exceeded = false;

        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(0, 1), cancellationToken);
            if (read == 0)
            {
                if (line.Length == 0 && !exceeded)
                {
                    return null;
                }

                TrimTrailingCarriageReturn(line);
                if (exceeded)
                {
                    throw new IncomingLineTooLongException(maxLineLength);
                }

                return line.ToString();
            }

            char current = buffer[0];
            if (current == '\n')
            {
                TrimTrailingCarriageReturn(line);
                if (exceeded)
                {
                    throw new IncomingLineTooLongException(maxLineLength);
                }

                return line.ToString();
            }

            if (exceeded)
            {
                continue;
            }

            if (line.Length >= maxLineLength)
            {
                exceeded = true;
                continue;
            }

            line.Append(current);
        }
    }

    private static void TrimTrailingCarriageReturn(StringBuilder line)
    {
        if (line.Length > 0 && line[^1] == '\r')
        {
            line.Length--;
        }
    }

    private bool RequiresAuthentication => !string.IsNullOrWhiteSpace(_acpAuthenticationToken);

    private static string ReadAuthenticationToken(JsonElement root)
    {
        if (!TryGetProperty(root, "params", out JsonElement parameters))
        {
            throw new AcpProtocolException(
                JsonRpcInvalidParams,
                "authenticate requires a non-empty token parameter.");
        }

        if (parameters.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(parameters.GetString()))
        {
            return parameters.GetString()!.Trim();
        }

        if (parameters.ValueKind == JsonValueKind.Object &&
            TryGetString(parameters, "token", out string token) &&
            !string.IsNullOrWhiteSpace(token))
        {
            return token.Trim();
        }

        throw new AcpProtocolException(
            JsonRpcInvalidParams,
            "authenticate requires a non-empty token parameter.");
    }

    private static bool SecureEquals(string? expected, string actual)
    {
        if (string.IsNullOrEmpty(expected))
        {
            return false;
        }

        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
        byte[] actualBytes = Encoding.UTF8.GetBytes(actual);
        return expectedBytes.Length == actualBytes.Length &&
            CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private sealed record AcpPrompt(
        string Text,
        IReadOnlyList<ConversationAttachment> Attachments);


}