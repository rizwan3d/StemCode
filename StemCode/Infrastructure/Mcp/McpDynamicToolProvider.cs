using Microsoft.Extensions.Logging;
using StemCode.Application.Abstractions;
using StemCode.Application.Models;

namespace StemCode.Infrastructure.Mcp;

internal sealed class McpDynamicToolProvider : IDynamicToolProvider, IMcpPromptService, IAsyncDisposable
{
    private sealed record PromptBinding(McpPromptDescriptor Descriptor, IMcpServerClient Client);

    private readonly StemCodeMcpConfigLoader _configLoader;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<McpDynamicToolProvider> _logger;
    private readonly ToolExecutionSettings _toolExecutionSettings;
    private readonly object _gate = new();
    private bool _initialized;
    private IReadOnlyList<ITool> _tools = [];
    private IReadOnlyList<DynamicToolProviderStatus> _statuses = [];
    private IReadOnlyList<IMcpServerClient> _clients = [];
    private IReadOnlyDictionary<string, PromptBinding> _prompts = new Dictionary<string, PromptBinding>();

    private readonly McpOAuthTokenStore? _tokenStore;
    private int _version;

    public McpDynamicToolProvider(
        StemCodeMcpConfigLoader configLoader,
        IHttpClientFactory httpClientFactory,
        ToolExecutionSettings toolExecutionSettings,
        ILogger<McpDynamicToolProvider> logger,
        McpOAuthTokenStore? tokenStore = null)
    {
        _configLoader = configLoader;
        _httpClientFactory = httpClientFactory;
        _toolExecutionSettings = toolExecutionSettings;
        _logger = logger;
        _tokenStore = tokenStore;
    }

    public int Version => Volatile.Read(ref _version);

    /// <summary>
    /// Disconnects every server so the next use reconnects with fresh
    /// configuration and credentials (used after /mcp login and /mcp logout).
    /// </summary>
    public async Task ResetAsync()
    {
        IReadOnlyList<IMcpServerClient> previousClients;
        lock (_gate)
        {
            previousClients = _clients;
            _clients = [];
            _tools = [];
            _statuses = [];
            _prompts = new Dictionary<string, PromptBinding>();
            _initialized = false;
            Interlocked.Increment(ref _version);
        }

        foreach (IMcpServerClient client in previousClients)
        {
            await client.DisposeAsync();
        }
    }

    public IReadOnlyList<ITool> GetTools()
    {
        EnsureInitialized();
        return _tools;
    }

    public IReadOnlyList<DynamicToolProviderStatus> GetStatuses()
    {
        EnsureInitialized();
        return _statuses;
    }

    public IReadOnlyList<McpPromptDescriptor> ListPrompts()
    {
        EnsureInitialized();
        return _prompts.Values
            .Select(static binding => binding.Descriptor)
            .OrderBy(static descriptor => descriptor.CommandName, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<McpPromptExpansion?> TryExpandAsync(
        string input,
        CancellationToken cancellationToken)
    {
        if (!McpPromptCommand.IsCandidate(input))
        {
            return null;
        }

        EnsureInitialized();
        (string commandName, string argumentText) = McpPromptCommand.Split(input);
        if (!_prompts.TryGetValue(commandName, out PromptBinding? binding))
        {
            return new McpPromptExpansion(
                null,
                _prompts.Count == 0
                    ? $"Unknown MCP prompt '/{commandName}'. No connected MCP server publishes prompts."
                    : $"Unknown MCP prompt '/{commandName}'. Available: {string.Join(", ", _prompts.Keys.Order(StringComparer.Ordinal).Select(static name => "/" + name))}.");
        }

        if (!McpPromptCommand.TryBindArguments(
                binding.Descriptor,
                argumentText,
                out Dictionary<string, string> arguments,
                out string? bindError))
        {
            return new McpPromptExpansion(null, bindError);
        }

        try
        {
            string prompt = await binding.Client.GetPromptTextAsync(
                binding.Descriptor.PromptName,
                arguments,
                cancellationToken);
            return string.IsNullOrWhiteSpace(prompt)
                ? new McpPromptExpansion(null, $"MCP prompt '{binding.Descriptor.PromptName}' on '{binding.Descriptor.ServerName}' returned no text.")
                : new McpPromptExpansion(prompt, null);
        }
        catch (Exception exception) when (exception is McpProtocolException or IOException or HttpRequestException or InvalidOperationException)
        {
            return new McpPromptExpansion(
                null,
                $"MCP prompt '{binding.Descriptor.PromptName}' on '{binding.Descriptor.ServerName}' failed: {exception.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (IMcpServerClient client in _clients)
        {
            await client.DisposeAsync();
        }
    }

    private void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        lock (_gate)
        {
            if (_initialized)
            {
                return;
            }

            InitializeAsync().GetAwaiter().GetResult();
            _initialized = true;
        }
    }

    private async Task InitializeAsync()
    {
        IReadOnlyList<McpServerConfiguration> configurations = _configLoader.Load();
        List<ITool> tools = [];
        List<DynamicToolProviderStatus> statuses = [];
        List<IMcpServerClient> clients = [];
        HashSet<string> usedToolNames = new(StringComparer.Ordinal);
        Dictionary<string, IMcpServerClient> resourceClients = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, PromptBinding> prompts = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> usedPromptNames = new(StringComparer.Ordinal);

        foreach (McpServerConfiguration configuration in configurations)
        {
            if (!configuration.Enabled)
            {
                statuses.Add(new DynamicToolProviderStatus(
                    configuration.Name,
                    GetTransportKind(configuration),
                    Enabled: false,
                    IsAvailable: false,
                    ToolCount: 0,
                    Details: FormatDetails(configuration, "disabled")));
                continue;
            }

            IMcpServerClient? client = null;
            try
            {
                client = CreateClient(configuration);
                using CancellationTokenSource startupTimeout = new(
                    TimeSpan.FromSeconds(configuration.StartupTimeoutSeconds));
                await client.InitializeAsync(startupTimeout.Token);
                IReadOnlyList<McpRemoteTool> remoteTools = await client.ListToolsAsync(startupTimeout.Token);
                McpRemoteTool[] enabledTools = remoteTools
                    .Where(tool => configuration.ShouldIncludeTool(tool.Name))
                    .ToArray();

                foreach (McpRemoteTool remoteTool in enabledTools)
                {
                    string toolName = McpToolName.Create(
                        configuration.Name,
                        remoteTool.Name,
                        usedToolNames);
                    usedToolNames.Add(toolName);
                    tools.Add(new McpTool(
                        toolName,
                        remoteTool.Name,
                        remoteTool.Description,
                        remoteTool.InputSchema,
                        client,
                        GetApprovalMode(configuration, remoteTool.Name),
                        TimeSpan.FromSeconds(configuration.ToolTimeoutSeconds)));
                }

                List<string> features = [];
                if (client.SupportsResources())
                {
                    resourceClients[configuration.Name] = client;
                    features.Add("resources");
                }

                if (client.SupportsPrompts())
                {
                    int promptCount = await DiscoverPromptsAsync(
                        configuration,
                        client,
                        prompts,
                        usedPromptNames,
                        startupTimeout.Token);
                    features.Add($"{promptCount} prompt(s)");
                }

                clients.Add(client);
                string endpointDetails = features.Count == 0
                    ? client.Endpoint
                    : $"{client.Endpoint}; {string.Join(", ", features)}";
                statuses.Add(new DynamicToolProviderStatus(
                    configuration.Name,
                    client.TransportKind,
                    Enabled: true,
                    IsAvailable: true,
                    ToolCount: enabledTools.Length,
                    Details: FormatDetails(configuration, endpointDetails)));
            }
            catch (Exception exception)
            {
                if (client is not null)
                {
                    await client.DisposeAsync();
                }

                string message = exception is McpAuthenticationException
                    ? $"MCP server '{configuration.Name}' needs sign-in: run /mcp login {configuration.Name}"
                    : $"MCP server '{configuration.Name}' unavailable: {exception.Message}";
                _logger.LogWarning(exception, "{Message}", message);
                statuses.Add(new DynamicToolProviderStatus(
                    configuration.Name,
                    GetTransportKind(configuration),
                    Enabled: true,
                    IsAvailable: false,
                    ToolCount: 0,
                    Details: FormatDetails(configuration, exception is McpAuthenticationException ? $"needs sign-in: run /mcp login {configuration.Name}" : exception.Message)));

                if (configuration.Required)
                {
                    throw new InvalidOperationException(message, exception);
                }
            }
        }

        if (resourceClients.Count > 0)
        {
            tools.Add(new McpListResourcesTool(resourceClients));
            tools.Add(new McpReadResourceTool(resourceClients, GetResourceReadTimeout()));
        }

        _tools = tools;
        _statuses = statuses;
        _clients = clients;
        _prompts = prompts;
    }

    private async Task<int> DiscoverPromptsAsync(
        McpServerConfiguration configuration,
        IMcpServerClient client,
        Dictionary<string, PromptBinding> prompts,
        HashSet<string> usedPromptNames,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<McpRemotePrompt> remotePrompts;
        try
        {
            remotePrompts = await client.ListPromptsAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is McpProtocolException or IOException or HttpRequestException)
        {
            _logger.LogWarning(exception, "MCP server '{Server}' advertised prompts but listing them failed.", configuration.Name);
            return 0;
        }

        foreach (McpRemotePrompt remotePrompt in remotePrompts)
        {
            string commandName = McpToolName.Create(configuration.Name, remotePrompt.Name, usedPromptNames);
            usedPromptNames.Add(commandName);
            prompts[commandName] = new PromptBinding(
                new McpPromptDescriptor(
                    commandName,
                    configuration.Name,
                    remotePrompt.Name,
                    remotePrompt.Description,
                    remotePrompt.Arguments
                        .Select(static argument => new McpPromptArgumentDescriptor(
                            argument.Name,
                            argument.Description,
                            argument.Required))
                        .ToArray()),
                client);
        }

        return remotePrompts.Count;
    }

    private TimeSpan GetResourceReadTimeout()
    {
        TimeSpan requestTimeout = GetRequestTimeout();
        return requestTimeout == Timeout.InfiniteTimeSpan
            ? TimeSpan.FromMinutes(2)
            : requestTimeout;
    }

    private IMcpServerClient CreateClient(McpServerConfiguration configuration)
    {
        if (!string.IsNullOrWhiteSpace(configuration.Command))
        {
            return new McpStdioServerClient(configuration, GetRequestTimeout());
        }

        if (!string.IsNullOrWhiteSpace(configuration.Url))
        {
            IMcpAccessTokenProvider? tokenProvider = _tokenStore is null
                ? null
                : new McpOAuthAccessTokenProvider(
                    configuration.Name,
                    configuration.Url!,
                    _tokenStore,
                    new McpOAuthClient(_httpClientFactory.CreateClient("StemCode.McpOAuth")));
            return new McpHttpServerClient(
                _httpClientFactory.CreateClient("StemCode.Mcp"),
                configuration,
                GetRequestTimeout(),
                tokenProvider);
        }

        throw new InvalidOperationException(
            "Configure either 'command' for stdio MCP or 'url' for streamable HTTP MCP.");
    }

    private TimeSpan GetRequestTimeout()
    {
        return _toolExecutionSettings.McpRequestTimeoutSeconds > 0
            ? TimeSpan.FromSeconds(_toolExecutionSettings.McpRequestTimeoutSeconds)
            : Timeout.InfiniteTimeSpan;
    }

    private static string GetTransportKind(McpServerConfiguration configuration)
    {
        if (!string.IsNullOrWhiteSpace(configuration.Command))
        {
            return "stdio";
        }

        return !string.IsNullOrWhiteSpace(configuration.Url)
            ? "http"
            : "unknown";
    }

    private static string FormatDetails(McpServerConfiguration configuration, string? details)
    {
        string normalizedDetails = string.IsNullOrWhiteSpace(details)
            ? string.Empty
            : details.Trim();

        if (!StemCodeMcpConfigLoader.IsAcpSource(configuration.SourcePath))
        {
            return normalizedDetails;
        }

        string source = configuration.SourcePath!.Trim();
        return string.IsNullOrWhiteSpace(normalizedDetails)
            ? $"source: {source}"
            : $"{normalizedDetails}; source: {source}";
    }

    private static ToolApprovalMode GetApprovalMode(
        McpServerConfiguration configuration,
        string remoteToolName)
    {
        string? configuredMode = configuration.ToolApprovalModes.TryGetValue(remoteToolName, out string? toolMode)
            ? toolMode
            : configuration.DefaultToolsApprovalMode;

        return configuredMode?.Trim().ToLowerInvariant() switch
        {
            "approve" or "auto" => ToolApprovalMode.Automatic,
            "prompt" => ToolApprovalMode.RequireApproval,
            _ => ToolApprovalMode.RequireApproval
        };
    }
}
