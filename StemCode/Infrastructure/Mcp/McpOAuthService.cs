using StemCode.Application.Abstractions;

namespace StemCode.Infrastructure.Mcp;

internal sealed class McpOAuthService : IMcpAuthService
{
    private readonly StemCodeMcpConfigLoader _configLoader;
    private readonly McpOAuthTokenStore _tokenStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IStatusMessageWriter _statusMessageWriter;
    private readonly McpDynamicToolProvider _toolProvider;

    public McpOAuthService(
        StemCodeMcpConfigLoader configLoader,
        McpOAuthTokenStore tokenStore,
        IHttpClientFactory httpClientFactory,
        IStatusMessageWriter statusMessageWriter,
        McpDynamicToolProvider toolProvider)
    {
        _configLoader = configLoader;
        _tokenStore = tokenStore;
        _httpClientFactory = httpClientFactory;
        _statusMessageWriter = statusMessageWriter;
        _toolProvider = toolProvider;
    }

    /// <summary>Overridable for tests; defaults to launching the system browser.</summary>
    internal Func<string, bool>? OpenBrowserOverride { get; set; }

    public async Task<string> LoginAsync(string serverName, CancellationToken cancellationToken)
    {
        McpServerConfiguration server = ResolveHttpServer(serverName);
        McpOAuthClient client = new(_httpClientFactory.CreateClient("StemCode.McpOAuth"));
        if (OpenBrowserOverride is not null)
        {
            client.OpenBrowser = OpenBrowserOverride;
        }

        McpOAuthTokens tokens = await client.AuthorizeAsync(
            server.Url!,
            message => _statusMessageWriter.ShowInfoAsync(message, cancellationToken),
            cancellationToken);
        await _tokenStore.SaveAsync(server.Name, server.Url!, tokens, cancellationToken);
        await _toolProvider.ResetAsync();

        return $"Signed in to MCP server '{server.Name}'. Its tools are available now.";
    }

    public async Task<string> LogoutAsync(string serverName, CancellationToken cancellationToken)
    {
        McpServerConfiguration server = ResolveHttpServer(serverName);
        await _tokenStore.ClearAsync(server.Name, server.Url!, cancellationToken);
        await _toolProvider.ResetAsync();
        return $"Signed out of MCP server '{server.Name}'.";
    }

    private McpServerConfiguration ResolveHttpServer(string serverName)
    {
        IReadOnlyList<McpServerConfiguration> servers = _configLoader.Load();
        McpServerConfiguration? server = servers.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, serverName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (server is null)
        {
            string known = servers.Count == 0 ? "none configured" : string.Join(", ", servers.Select(static candidate => candidate.Name));
            throw new InvalidOperationException($"No MCP server named '{serverName}'. Known servers: {known}.");
        }

        if (string.IsNullOrWhiteSpace(server.Url))
        {
            throw new InvalidOperationException(
                $"MCP server '{server.Name}' runs locally over stdio; only remote HTTP servers use sign-in.");
        }

        return server;
    }
}
