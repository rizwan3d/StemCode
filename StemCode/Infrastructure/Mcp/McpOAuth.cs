using StemCode.Infrastructure.Configuration;
using StemCode.Infrastructure.Secrets;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StemCode.Infrastructure.Mcp;

/// <summary>OAuth tokens StemCode holds for one remote MCP server.</summary>
internal sealed class McpOAuthTokens
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("expires_at")]
    public DateTimeOffset? ExpiresAtUtc { get; set; }

    [JsonPropertyName("token_endpoint")]
    public string TokenEndpoint { get; set; } = string.Empty;

    [JsonPropertyName("client_id")]
    public string ClientId { get; set; } = string.Empty;

    [JsonPropertyName("client_secret")]
    public string? ClientSecret { get; set; }

    [JsonPropertyName("resource")]
    public string? Resource { get; set; }

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }

    public bool IsExpired(DateTimeOffset now)
    {
        return ExpiresAtUtc is not null && ExpiresAtUtc.Value - TimeSpan.FromMinutes(1) <= now;
    }
}

/// <summary>Stores MCP OAuth tokens in the OS credential store, one entry per server URL.</summary>
internal sealed class McpOAuthTokenStore
{
    private readonly IPlatformCredentialStore _credentialStore;

    public McpOAuthTokenStore(IPlatformCredentialStore credentialStore)
    {
        _credentialStore = credentialStore;
    }

    public async Task<McpOAuthTokens?> LoadAsync(string serverName, string serverUrl, CancellationToken cancellationToken)
    {
        string? json;
        try
        {
            json = await _credentialStore.LoadAsync(CreateReference(serverName, serverUrl), cancellationToken);
        }
        catch (SecretStorageException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            McpOAuthTokens? tokens = JsonSerializer.Deserialize<McpOAuthTokens>(json);
            return string.IsNullOrWhiteSpace(tokens?.AccessToken) ? null : tokens;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task SaveAsync(string serverName, string serverUrl, McpOAuthTokens tokens, CancellationToken cancellationToken)
    {
        return _credentialStore.SaveAsync(
            CreateReference(serverName, serverUrl),
            JsonSerializer.Serialize(tokens),
            cancellationToken);
    }

    /// <summary>The platform stores have no delete, so signing out overwrites the entry with an empty object.</summary>
    public Task ClearAsync(string serverName, string serverUrl, CancellationToken cancellationToken)
    {
        return _credentialStore.SaveAsync(CreateReference(serverName, serverUrl), "{}", cancellationToken);
    }

    private static SecretReference CreateReference(string serverName, string serverUrl)
    {
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serverUrl.Trim())))[..16].ToLowerInvariant();
        return new SecretReference(
            ApplicationIdentity.ProductName,
            $"mcp-oauth-{key}",
            $"{ApplicationIdentity.ProductName} MCP OAuth ({serverName})");
    }
}

/// <summary>Supplies (and refreshes) the bearer token for an HTTP MCP server.</summary>
internal interface IMcpAccessTokenProvider
{
    Task<string?> GetAccessTokenAsync(bool forceRefresh, CancellationToken cancellationToken);
}

internal sealed class McpOAuthAccessTokenProvider : IMcpAccessTokenProvider
{
    private readonly string _serverName;
    private readonly string _serverUrl;
    private readonly McpOAuthTokenStore _tokenStore;
    private readonly McpOAuthClient _oauthClient;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public McpOAuthAccessTokenProvider(
        string serverName,
        string serverUrl,
        McpOAuthTokenStore tokenStore,
        McpOAuthClient oauthClient)
    {
        _serverName = serverName;
        _serverUrl = serverUrl;
        _tokenStore = tokenStore;
        _oauthClient = oauthClient;
    }

    public async Task<string?> GetAccessTokenAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            McpOAuthTokens? tokens = await _tokenStore.LoadAsync(_serverName, _serverUrl, cancellationToken);
            if (tokens is null)
            {
                return null;
            }

            if (!forceRefresh && !tokens.IsExpired(DateTimeOffset.UtcNow))
            {
                return tokens.AccessToken;
            }

            if (string.IsNullOrWhiteSpace(tokens.RefreshToken))
            {
                return forceRefresh ? null : tokens.AccessToken;
            }

            try
            {
                McpOAuthTokens refreshed = await _oauthClient.RefreshAsync(tokens, cancellationToken);
                await _tokenStore.SaveAsync(_serverName, _serverUrl, refreshed, cancellationToken);
                return refreshed.AccessToken;
            }
            catch (McpAuthenticationException)
            {
                return null;
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }
}

internal sealed class McpAuthenticationException : InvalidOperationException
{
    public McpAuthenticationException(string message, string? wwwAuthenticate = null)
        : base(message)
    {
        WwwAuthenticate = wwwAuthenticate;
    }

    public string? WwwAuthenticate { get; }
}

internal sealed record McpAuthorizationServerMetadata(
    string Issuer,
    string AuthorizationEndpoint,
    string TokenEndpoint,
    string? RegistrationEndpoint,
    IReadOnlyList<string> ScopesSupported);

/// <summary>
/// OAuth 2.1 client for remote MCP servers, following the MCP authorization spec:
/// protected resource metadata (RFC 9728), authorization server metadata
/// (RFC 8414 / OpenID discovery), dynamic client registration (RFC 7591), and
/// the authorization code flow with PKCE on a loopback redirect, sending the
/// server URL as the <c>resource</c> indicator (RFC 8707).
/// </summary>
internal sealed class McpOAuthClient
{
    private static readonly TimeSpan CallbackTimeout = TimeSpan.FromMinutes(5);

    private readonly HttpClient _httpClient;

    public McpOAuthClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>Overridable for tests: opens the authorization URL in the user's browser.</summary>
    public Func<string, bool> OpenBrowser { get; set; } = TryOpenBrowser;

    public async Task<McpAuthorizationServerMetadata> DiscoverAsync(
        string serverUrl,
        CancellationToken cancellationToken)
    {
        Uri server = new(serverUrl);
        string? wwwAuthenticate = await ProbeWwwAuthenticateAsync(server, cancellationToken);
        string? resourceMetadataUrl = ParseAuthParameter(wwwAuthenticate, "resource_metadata");

        List<string> resourceMetadataCandidates = [];
        if (resourceMetadataUrl is not null)
        {
            resourceMetadataCandidates.Add(resourceMetadataUrl);
        }

        string origin = server.GetLeftPart(UriPartial.Authority);
        string path = server.AbsolutePath.TrimEnd('/');
        if (path.Length > 0)
        {
            resourceMetadataCandidates.Add($"{origin}/.well-known/oauth-protected-resource{path}");
        }

        resourceMetadataCandidates.Add($"{origin}/.well-known/oauth-protected-resource");

        string issuer = origin;
        IReadOnlyList<string> resourceScopes = [];
        foreach (string candidate in resourceMetadataCandidates)
        {
            JsonElement? metadata = await TryGetJsonAsync(candidate, cancellationToken);
            if (metadata is null)
            {
                continue;
            }

            if (metadata.Value.TryGetProperty("authorization_servers", out JsonElement servers) &&
                servers.ValueKind == JsonValueKind.Array &&
                servers.GetArrayLength() > 0 &&
                servers[0].GetString() is string authorizationServer)
            {
                issuer = authorizationServer.TrimEnd('/');
            }

            resourceScopes = ReadStringArray(metadata.Value, "scopes_supported");
            break;
        }

        McpAuthorizationServerMetadata? serverMetadata = await TryDiscoverAuthorizationServerAsync(issuer, cancellationToken);
        if (serverMetadata is not null)
        {
            return serverMetadata with
            {
                ScopesSupported = resourceScopes.Count > 0 ? resourceScopes : serverMetadata.ScopesSupported
            };
        }

        // Older servers (MCP 2025-03-26) put the default endpoints on the server's origin.
        string fallbackBase = new Uri(issuer).GetLeftPart(UriPartial.Authority);
        return new McpAuthorizationServerMetadata(
            fallbackBase,
            $"{fallbackBase}/authorize",
            $"{fallbackBase}/token",
            $"{fallbackBase}/register",
            resourceScopes);
    }

    /// <summary>
    /// Runs the interactive browser sign-in and returns the issued tokens.
    /// <paramref name="showMessage"/> receives progress text, including the URL to
    /// open manually if no browser can be launched.
    /// </summary>
    public async Task<McpOAuthTokens> AuthorizeAsync(
        string serverUrl,
        Func<string, Task> showMessage,
        CancellationToken cancellationToken)
    {
        McpAuthorizationServerMetadata metadata = await DiscoverAsync(serverUrl, cancellationToken);

        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string redirectUri = $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/callback";

        (string clientId, string? clientSecret) = await RegisterClientAsync(metadata, redirectUri, cancellationToken);

        string codeVerifier = ToBase64Url(RandomNumberGenerator.GetBytes(32));
        string codeChallenge = ToBase64Url(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
        string state = ToBase64Url(RandomNumberGenerator.GetBytes(16));
        string? scope = metadata.ScopesSupported.Count == 0 ? null : string.Join(' ', metadata.ScopesSupported);

        Dictionary<string, string?> query = new()
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["resource"] = serverUrl,
            ["scope"] = scope
        };
        string authorizationUrl = AppendQuery(metadata.AuthorizationEndpoint, query);

        await showMessage("Opening your browser to sign in to the MCP server.");
        await showMessage($"If it does not open, visit: {authorizationUrl}");
        _ = OpenBrowser(authorizationUrl);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CallbackTimeout);
        string code = await WaitForCallbackAsync(listener, state, timeout.Token);

        Dictionary<string, string> form = new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = clientId,
            ["code_verifier"] = codeVerifier,
            ["resource"] = serverUrl
        };
        if (clientSecret is not null)
        {
            form["client_secret"] = clientSecret;
        }

        McpOAuthTokens tokens = await RequestTokensAsync(metadata.TokenEndpoint, form, cancellationToken);
        tokens.TokenEndpoint = metadata.TokenEndpoint;
        tokens.ClientId = clientId;
        tokens.ClientSecret = clientSecret;
        tokens.Resource = serverUrl;
        tokens.Scope ??= scope;
        return tokens;
    }

    public async Task<McpOAuthTokens> RefreshAsync(
        McpOAuthTokens tokens,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tokens.RefreshToken))
        {
            throw new McpAuthenticationException("The MCP sign-in has expired and cannot be refreshed.");
        }

        Dictionary<string, string> form = new()
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = tokens.RefreshToken,
            ["client_id"] = tokens.ClientId
        };
        if (tokens.ClientSecret is not null)
        {
            form["client_secret"] = tokens.ClientSecret;
        }

        if (tokens.Resource is not null)
        {
            form["resource"] = tokens.Resource;
        }

        McpOAuthTokens refreshed = await RequestTokensAsync(tokens.TokenEndpoint, form, cancellationToken);
        refreshed.TokenEndpoint = tokens.TokenEndpoint;
        refreshed.ClientId = tokens.ClientId;
        refreshed.ClientSecret = tokens.ClientSecret;
        refreshed.Resource = tokens.Resource;
        refreshed.RefreshToken ??= tokens.RefreshToken;
        refreshed.Scope ??= tokens.Scope;
        return refreshed;
    }

    internal static string? ParseAuthParameter(string? header, string name)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        int index = header.IndexOf(name + "=", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        string rest = header[(index + name.Length + 1)..].TrimStart();
        if (rest.StartsWith('"'))
        {
            int end = rest.IndexOf('"', 1);
            return end < 0 ? null : rest[1..end];
        }

        int stop = rest.IndexOfAny([',', ' ']);
        return stop < 0 ? rest : rest[..stop];
    }

    private async Task<string?> ProbeWwwAuthenticateAsync(Uri server, CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, server);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            request.Content = new StringContent(
                McpJson.BuildRequest(1, "initialize", McpJson.WriteInitializeParams),
                Encoding.UTF8,
                "application/json");
            using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return response.StatusCode == HttpStatusCode.Unauthorized
                ? string.Join(", ", response.Headers.WwwAuthenticate.Select(static value => value.ToString()))
                : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private async Task<McpAuthorizationServerMetadata?> TryDiscoverAuthorizationServerAsync(
        string issuer,
        CancellationToken cancellationToken)
    {
        Uri issuerUri = new(issuer);
        string origin = issuerUri.GetLeftPart(UriPartial.Authority);
        string path = issuerUri.AbsolutePath.TrimEnd('/');
        List<string> candidates = [];
        if (path.Length > 0)
        {
            candidates.Add($"{origin}/.well-known/oauth-authorization-server{path}");
            candidates.Add($"{origin}/.well-known/openid-configuration{path}");
            candidates.Add($"{issuer}/.well-known/openid-configuration");
        }

        candidates.Add($"{origin}/.well-known/oauth-authorization-server");
        candidates.Add($"{origin}/.well-known/openid-configuration");

        foreach (string candidate in candidates)
        {
            JsonElement? metadata = await TryGetJsonAsync(candidate, cancellationToken);
            if (metadata is null ||
                GetString(metadata.Value, "authorization_endpoint") is not string authorizationEndpoint ||
                GetString(metadata.Value, "token_endpoint") is not string tokenEndpoint)
            {
                continue;
            }

            return new McpAuthorizationServerMetadata(
                GetString(metadata.Value, "issuer") ?? issuer,
                authorizationEndpoint,
                tokenEndpoint,
                GetString(metadata.Value, "registration_endpoint"),
                ReadStringArray(metadata.Value, "scopes_supported"));
        }

        return null;
    }

    private async Task<(string ClientId, string? ClientSecret)> RegisterClientAsync(
        McpAuthorizationServerMetadata metadata,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        if (metadata.RegistrationEndpoint is null)
        {
            throw new McpAuthenticationException(
                "The MCP server's authorization server does not support dynamic client registration, so StemCode cannot sign in automatically. Use bearerTokenEnvVar or httpHeaders instead.");
        }

        using MemoryStream body = new();
        using (Utf8JsonWriter writer = new(body))
        {
            writer.WriteStartObject();
            writer.WriteString("client_name", ApplicationIdentity.ProductName);
            writer.WriteStartArray("redirect_uris");
            writer.WriteStringValue(redirectUri);
            writer.WriteEndArray();
            writer.WriteStartArray("grant_types");
            writer.WriteStringValue("authorization_code");
            writer.WriteStringValue("refresh_token");
            writer.WriteEndArray();
            writer.WriteStartArray("response_types");
            writer.WriteStringValue("code");
            writer.WriteEndArray();
            writer.WriteString("token_endpoint_auth_method", "none");
            writer.WriteEndObject();
        }

        using HttpRequestMessage request = new(HttpMethod.Post, metadata.RegistrationEndpoint)
        {
            Content = new ByteArrayContent(body.ToArray())
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new McpAuthenticationException(
                $"MCP client registration failed with HTTP {(int)response.StatusCode}: {Truncate(json)}");
        }

        using JsonDocument document = JsonDocument.Parse(json);
        string clientId = GetString(document.RootElement, "client_id")
            ?? throw new McpAuthenticationException("MCP client registration returned no client_id.");
        return (clientId, GetString(document.RootElement, "client_secret"));
    }

    private async Task<McpOAuthTokens> RequestTokensAsync(
        string tokenEndpoint,
        IReadOnlyDictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new McpAuthenticationException(
                $"MCP token request failed with HTTP {(int)response.StatusCode}: {Truncate(json)}");
        }

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        string accessToken = GetString(root, "access_token")
            ?? throw new McpAuthenticationException("MCP token response contained no access_token.");
        DateTimeOffset? expiresAt = root.TryGetProperty("expires_in", out JsonElement expiresIn) &&
                                    expiresIn.TryGetInt64(out long seconds)
            ? DateTimeOffset.UtcNow.AddSeconds(seconds)
            : null;

        return new McpOAuthTokens
        {
            AccessToken = accessToken,
            RefreshToken = GetString(root, "refresh_token"),
            ExpiresAtUtc = expiresAt,
            Scope = GetString(root, "scope")
        };
    }

    private async Task<JsonElement?> TryGetJsonAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or UriFormatException)
        {
            return null;
        }
    }

    private static async Task<string> WaitForCallbackAsync(
        TcpListener listener,
        string expectedState,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);
            await using NetworkStream stream = client.GetStream();
            using StreamReader reader = new(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            string? requestLine = await reader.ReadLineAsync(cancellationToken);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken)))
            {
            }

            string[] parts = requestLine?.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries) ?? [];
            if (parts.Length < 2 ||
                !Uri.TryCreate(new Uri("http://127.0.0.1/"), parts[1], out Uri? requestUri) ||
                !string.Equals(requestUri.AbsolutePath, "/callback", StringComparison.Ordinal))
            {
                await WriteResponseAsync(stream, HttpStatusCode.NotFound, "Not found.", cancellationToken);
                continue;
            }

            Dictionary<string, string> query = ParseQuery(requestUri.Query);
            if (query.TryGetValue("error", out string? error))
            {
                await WriteResponseAsync(stream, HttpStatusCode.BadRequest, $"Sign-in failed: {WebUtility.HtmlEncode(error)}", cancellationToken);
                string description = query.TryGetValue("error_description", out string? text) ? $" ({text})" : string.Empty;
                throw new McpAuthenticationException($"MCP sign-in was refused: {error}{description}.");
            }

            if (!query.TryGetValue("state", out string? state) || !string.Equals(state, expectedState, StringComparison.Ordinal))
            {
                await WriteResponseAsync(stream, HttpStatusCode.BadRequest, "State mismatch.", cancellationToken);
                throw new McpAuthenticationException("MCP sign-in returned a mismatched state; please try again.");
            }

            if (!query.TryGetValue("code", out string? code) || string.IsNullOrWhiteSpace(code))
            {
                await WriteResponseAsync(stream, HttpStatusCode.BadRequest, "Missing code.", cancellationToken);
                throw new McpAuthenticationException("MCP sign-in returned no authorization code.");
            }

            await WriteResponseAsync(
                stream,
                HttpStatusCode.OK,
                "<!doctype html><html><head><meta charset=\"utf-8\"><title>Signed in</title></head>" +
                "<body style=\"font-family:sans-serif;margin:3rem\"><h1>Signed in</h1><p>You can close this window and return to StemCode.</p></body></html>",
                cancellationToken);
            return code;
        }
    }

    private static async Task WriteResponseAsync(Stream stream, HttpStatusCode status, string body, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        string headers =
            $"HTTP/1.1 {(int)status} {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            string key = Uri.UnescapeDataString((equals < 0 ? pair : pair[..equals]).Replace('+', ' '));
            string value = equals < 0 ? string.Empty : Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
            values[key] = value;
        }

        return values;
    }

    private static string AppendQuery(string url, IReadOnlyDictionary<string, string?> values)
    {
        string query = string.Join(
            "&",
            values
                .Where(static pair => pair.Value is not null)
                .Select(static pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}"));
        return url + (url.Contains('?') ? "&" : "?") + query;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out JsonElement array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString()!)
                .ToArray()
            : [];
    }

    private static string? GetString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out JsonElement value) &&
               value.ValueKind == JsonValueKind.String &&
               !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;
    }

    private static string ToBase64Url(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string Truncate(string value)
    {
        string trimmed = value.Trim();
        return trimmed.Length <= 300 ? trimmed : trimmed[..300] + "...";
    }

    private static bool TryOpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return true;
            }

            Process.Start(new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open", url) { UseShellExecute = false });
            return true;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return false;
        }
    }
}
