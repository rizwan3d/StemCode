using FluentAssertions;
using StemCode.Infrastructure.Mcp;
using StemCode.Infrastructure.Secrets;
using System.Net;
using System.Text;
using System.Text.Json;

namespace StemCode.Tests.Infrastructure.Mcp;

public sealed class McpOAuthTests
{
    private const string ServerUrl = "https://mcp.example.com/mcp";

    [Fact]
    public async Task AuthorizeAsync_Should_DiscoverRegisterAndExchangeCode_WithPkceAndResource()
    {
        FakeAuthServer server = new();
        string? openedUrl = null;
        McpOAuthClient sut = new(new HttpClient(server))
        {
            OpenBrowser = url =>
            {
                openedUrl = url;
                return FollowRedirectInBackground(url);
            }
        };
        List<string> messages = [];

        McpOAuthTokens tokens = await sut.AuthorizeAsync(
            ServerUrl,
            message =>
            {
                messages.Add(message);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        tokens.AccessToken.Should().Be("access-1");
        tokens.RefreshToken.Should().Be("refresh-1");
        tokens.ClientId.Should().Be("client-123");
        tokens.TokenEndpoint.Should().Be("https://auth.example.com/token");
        tokens.Resource.Should().Be(ServerUrl);
        tokens.ExpiresAtUtc.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(50));
        messages.Should().Contain(message => message.Contains("https://auth.example.com/authorize?"));

        Dictionary<string, string> authorizationQuery = ParseQuery(new Uri(openedUrl!).Query);
        authorizationQuery["resource"].Should().Be(ServerUrl);
        authorizationQuery["code_challenge_method"].Should().Be("S256");
        authorizationQuery["scope"].Should().Be("mcp.read mcp.write");
        server.RegisteredRedirectUri.Should().StartWith("http://127.0.0.1:").And.EndWith("/callback");
        server.TokenForm!["grant_type"].Should().Be("authorization_code");
        server.TokenForm["code"].Should().Be("auth-code");
        server.TokenForm["resource"].Should().Be(ServerUrl);
        server.TokenForm["code_verifier"].Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task HttpClient_Should_SendStoredToken_AndRefreshOnceOn401()
    {
        FakeAuthServer server = new() { ValidAccessToken = "access-2" };
        InMemoryCredentialStore credentials = new();
        McpOAuthTokenStore store = new(credentials);
        await store.SaveAsync("remote", ServerUrl, new McpOAuthTokens
        {
            AccessToken = "access-1",
            RefreshToken = "refresh-1",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
            TokenEndpoint = "https://auth.example.com/token",
            ClientId = "client-123",
            Resource = ServerUrl
        }, CancellationToken.None);

        McpOAuthAccessTokenProvider tokenProvider = new("remote", ServerUrl, store, new McpOAuthClient(new HttpClient(server)));
        await using McpHttpServerClient client = new(
            new HttpClient(server),
            new McpServerConfiguration("remote") { Url = ServerUrl },
            TimeSpan.FromSeconds(5),
            tokenProvider);

        await client.InitializeAsync(CancellationToken.None);

        server.McpAuthorizationHeaders.Should().StartWith(["Bearer access-1", "Bearer access-2"]);
        server.TokenForm!["grant_type"].Should().Be("refresh_token");
        (await store.LoadAsync("remote", ServerUrl, CancellationToken.None))!.AccessToken.Should().Be("access-2");
    }

    [Fact]
    public async Task HttpClient_Should_ThrowSignInRequired_When_NoTokensAreStored()
    {
        FakeAuthServer server = new();
        McpOAuthAccessTokenProvider tokenProvider = new(
            "remote",
            ServerUrl,
            new McpOAuthTokenStore(new InMemoryCredentialStore()),
            new McpOAuthClient(new HttpClient(server)));
        await using McpHttpServerClient client = new(
            new HttpClient(server),
            new McpServerConfiguration("remote") { Url = ServerUrl },
            TimeSpan.FromSeconds(5),
            tokenProvider);

        Func<Task> act = () => client.InitializeAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<McpAuthenticationException>())
            .Which.Message.Should().Contain("/mcp login remote");
    }

    [Fact]
    public async Task TokenStore_Should_ForgetTokensOnClear()
    {
        McpOAuthTokenStore store = new(new InMemoryCredentialStore());
        await store.SaveAsync("s", ServerUrl, new McpOAuthTokens { AccessToken = "a" }, CancellationToken.None);

        await store.ClearAsync("s", ServerUrl, CancellationToken.None);

        (await store.LoadAsync("s", ServerUrl, CancellationToken.None)).Should().BeNull();
    }

    [Theory]
    [InlineData("Bearer resource_metadata=\"https://x/.well-known/oauth-protected-resource\", scope=\"a b\"", "resource_metadata", "https://x/.well-known/oauth-protected-resource")]
    [InlineData("Bearer realm=mcp, scope=read", "scope", "read")]
    [InlineData("Bearer realm=mcp", "resource_metadata", null)]
    public void ParseAuthParameter_Should_ReadQuotedAndBareValues(string header, string name, string? expected)
    {
        McpOAuthClient.ParseAuthParameter(header, name).Should().Be(expected);
    }

    private static bool FollowRedirectInBackground(string authorizationUrl)
    {
        Uri uri = new(authorizationUrl);
        Dictionary<string, string> query = ParseQuery(uri.Query);
        string callback = $"{query["redirect_uri"]}?code=auth-code&state={Uri.EscapeDataString(query["state"])}";
        _ = Task.Run(async () =>
        {
            using HttpClient browser = new();
            await browser.GetAsync(callback);
        });
        return true;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        return query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(static pair => pair.Split('=', 2))
            .ToDictionary(static parts => Uri.UnescapeDataString(parts[0]), static parts => Uri.UnescapeDataString(parts.Length > 1 ? parts[1] : string.Empty));
    }

    private sealed class InMemoryCredentialStore : IPlatformCredentialStore
    {
        private readonly Dictionary<string, string> _values = new();

        public Task<string?> LoadAsync(SecretReference secretReference, CancellationToken cancellationToken)
        {
            return Task.FromResult(_values.TryGetValue(secretReference.AccountName, out string? value) ? value : null);
        }

        public Task SaveAsync(SecretReference secretReference, string secretValue, CancellationToken cancellationToken)
        {
            _values[secretReference.AccountName] = secretValue;
            return Task.CompletedTask;
        }
    }

    /// <summary>Fake MCP resource server plus OAuth authorization server.</summary>
    private sealed class FakeAuthServer : HttpMessageHandler
    {
        public string ValidAccessToken { get; init; } = "access-1";

        public Dictionary<string, string>? AuthorizationQuery { get; private set; }

        public Dictionary<string, string>? TokenForm { get; private set; }

        public string? RegisteredRedirectUri { get; private set; }

        public List<string> McpAuthorizationHeaders { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.GetLeftPart(UriPartial.Path);
            switch (url)
            {
                case ServerUrl:
                    McpAuthorizationHeaders.Add(request.Headers.Authorization?.ToString() ?? string.Empty);
                    if (request.Headers.Authorization?.Parameter != ValidAccessToken)
                    {
                        HttpResponseMessage unauthorized = new(HttpStatusCode.Unauthorized);
                        unauthorized.Headers.WwwAuthenticate.ParseAdd(
                            "Bearer resource_metadata=\"https://mcp.example.com/.well-known/oauth-protected-resource/mcp\"");
                        return unauthorized;
                    }

                    string body = await request.Content!.ReadAsStringAsync(cancellationToken);
                    using (JsonDocument rpc = JsonDocument.Parse(body))
                    {
                        if (!rpc.RootElement.TryGetProperty("id", out JsonElement id))
                        {
                            return new HttpResponseMessage(HttpStatusCode.Accepted);
                        }

                        return Json($"{{\"jsonrpc\":\"2.0\",\"id\":{id.GetRawText()},\"result\":{{\"capabilities\":{{}}}}}}");
                    }

                case "https://mcp.example.com/.well-known/oauth-protected-resource/mcp":
                    return Json("""{"resource":"https://mcp.example.com/mcp","authorization_servers":["https://auth.example.com"],"scopes_supported":["mcp.read","mcp.write"]}""");

                case "https://auth.example.com/.well-known/oauth-authorization-server":
                    return Json("""
                        {"issuer":"https://auth.example.com","authorization_endpoint":"https://auth.example.com/authorize",
                         "token_endpoint":"https://auth.example.com/token","registration_endpoint":"https://auth.example.com/register"}
                        """);

                case "https://auth.example.com/register":
                    using (JsonDocument registration = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)))
                    {
                        RegisteredRedirectUri = registration.RootElement.GetProperty("redirect_uris")[0].GetString();
                    }

                    return Json("""{"client_id":"client-123"}""", HttpStatusCode.Created);

                case "https://auth.example.com/token":
                    TokenForm = ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
                    return TokenForm["grant_type"] == "refresh_token"
                        ? Json("""{"access_token":"access-2","token_type":"Bearer","expires_in":3600}""")
                        : Json("""{"access_token":"access-1","refresh_token":"refresh-1","token_type":"Bearer","expires_in":3600}""");

                case "https://auth.example.com/authorize":
                    AuthorizationQuery = ParseQuery(request.RequestUri.Query);
                    return new HttpResponseMessage(HttpStatusCode.OK);

                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        {
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
