using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StemCode.Application.Abstractions;
using StemCode.Application.Models;
using StemCode.Domain.Models;
using StemCode.Infrastructure.StemCodeEnterprise;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace StemCode.Tests.Infrastructure.StemCodeEnterprise;

public sealed class StemCodeEnterpriseCredentialServiceTests
{
    [Fact]
    public async Task ResolveAsync_Should_RefreshExpiredCredentialsAndSaveUpdatedSecret()
    {
        string storedCredentials = JsonSerializer.Serialize(new
        {
            type = "stemcode-enterprise",
            providerBaseUrl = "https://enterprise.example.com/v1",
            access_token = "old-access",
            refresh_token = "old-refresh",
            expires = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds()
        });
        RecordingHandler handler = new("""
            {
              "access_token": "new-access",
              "token_type": "Bearer",
              "expires_in": 3600,
              "refresh_token": "new-refresh"
            }
            """);
        HttpClient httpClient = new(handler);

        string? savedSecret = null;
        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.SaveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((value, _) => savedSecret = value)
            .Returns(Task.CompletedTask);
        secretStore
            .Setup(store => store.SaveAsync("StemCode Enterprise", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Mock<IAgentConfigurationStore> configurationStore = new(MockBehavior.Strict);
        configurationStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentConfiguration(
                new AgentProviderProfile(ProviderKind.OpenAiCompatible, "https://enterprise.example.com/v1"),
                PreferredModelId: null,
                ReasoningEffort: null,
                ActiveProviderName: "StemCode Enterprise"));

        Mock<IStatusMessageWriter> statusMessageWriter = new(MockBehavior.Strict);

        StemCodeEnterpriseCredentialService sut = new(
            httpClient,
            configurationStore.Object,
            secretStore.Object,
            statusMessageWriter.Object,
            NullLogger<StemCodeEnterpriseCredentialService>.Instance);

        StemCodeEnterpriseResolvedCredential result = await sut.ResolveAsync(
            storedCredentials,
            forceRefresh: false,
            CancellationToken.None);

        result.AccessToken.Should().Be("new-access");
        handler.RequestUri.Should().Be(new Uri("https://enterprise.example.com/api/v1/auth/oauth/token"));
        handler.RequestBody.Should().Contain("grant_type=refresh_token");
        handler.RequestBody.Should().Contain("refresh_token=old-refresh");
        savedSecret.Should().NotBeNullOrWhiteSpace();
        sut.CanResolve(savedSecret!).Should().BeTrue();
        secretStore.VerifyAll();
        configurationStore.VerifyAll();
        statusMessageWriter.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AuthenticateAsync_Should_AcceptNativeBrowserTokenHandoff()
    {
        HttpClient httpClient = new(new NeverCalledHandler());

        List<string> infoMessages = [];
        string? savedSecret = null;
        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.SaveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((value, _) => savedSecret = value)
            .Returns(Task.CompletedTask);
        secretStore
            .Setup(store => store.SaveAsync("StemCode Enterprise", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Mock<IAgentConfigurationStore> configurationStore = new(MockBehavior.Strict);
        configurationStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentConfiguration(
                new AgentProviderProfile(ProviderKind.OpenAiCompatible, "https://app.getstemcode.com/v1"),
                PreferredModelId: null,
                ReasoningEffort: null,
                ActiveProviderName: "StemCode Enterprise"));

        Mock<IStatusMessageWriter> statusMessageWriter = new(MockBehavior.Strict);
        statusMessageWriter
            .Setup(writer => writer.ShowInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((message, _) => infoMessages.Add(message))
            .Returns(Task.CompletedTask);
        statusMessageWriter
            .Setup(writer => writer.ShowSuccessAsync(
                "StemCode subscription sign-in completed.",
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        StemCodeEnterpriseCredentialService sut = new(
            httpClient,
            configurationStore.Object,
            secretStore.Object,
            statusMessageWriter.Object,
            NullLogger<StemCodeEnterpriseCredentialService>.Instance,
            _ => true);

        Task<string> authenticateTask = sut.AuthenticateAsync(
            "https://app.getstemcode.com/v1",
            CancellationToken.None);

        string authorizationUrl = await WaitForAuthorizationUrlAsync(infoMessages);
        authorizationUrl.Should().StartWith("https://app.getstemcode.com/api/v1/auth/oauth/authorize?");
        string redirectUri = GetQueryParameter(authorizationUrl, "redirect_uri")
            ?? throw new InvalidOperationException("redirect_uri was missing from the authorization URL.");
        string expiry = DateTimeOffset.UtcNow.AddHours(2).ToString("O");

        using HttpClient callbackClient = new();
        using HttpRequestMessage callbackRequest = new(HttpMethod.Post, redirectUri)
        {
            Content = new StringContent(
                $$"""
                {"expiresAtUtc":"{{expiry}}"}
                """,
                Encoding.UTF8,
                "application/json")
        };
        callbackRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "native-access-token");
        callbackRequest.Headers.TryAddWithoutValidation("X-StemCode-Token-Expires-At", expiry);

        using HttpResponseMessage callbackResponse = await callbackClient.SendAsync(callbackRequest);
        callbackResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        string storedCredentials = await authenticateTask;
        storedCredentials.Should().Be(savedSecret);
        sut.CanResolve(storedCredentials).Should().BeTrue();

        StemCodeEnterpriseResolvedCredential resolved = await sut.ResolveAsync(
            storedCredentials,
            forceRefresh: false,
            CancellationToken.None);

        resolved.AccessToken.Should().Be("native-access-token");
        configurationStore.VerifyAll();
        secretStore.VerifyAll();
        statusMessageWriter.VerifyAll();
    }

    [Fact]
    public async Task ResolveAsync_Should_Reauthenticate_When_NativeTokenHasExpired()
    {
        HttpClient httpClient = new(new NeverCalledHandler());

        List<string> infoMessages = [];
        string? savedSecret = null;
        string expiredCredentials = JsonSerializer.Serialize(new
        {
            type = "stemcode-enterprise",
            providerBaseUrl = "https://app.getstemcode.com/v1",
            access_token = "expired-token",
            refresh_token = (string?)null,
            expires = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds()
        });

        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(expiredCredentials);
        secretStore
            .Setup(store => store.SaveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((value, _) => savedSecret = value)
            .Returns(Task.CompletedTask);
        secretStore
            .Setup(store => store.SaveAsync("StemCode Enterprise", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Mock<IAgentConfigurationStore> configurationStore = new(MockBehavior.Strict);
        configurationStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentConfiguration(
                new AgentProviderProfile(ProviderKind.OpenAiCompatible, "https://app.getstemcode.com/v1"),
                PreferredModelId: null,
                ReasoningEffort: null,
                ActiveProviderName: "StemCode Enterprise"));

        Mock<IStatusMessageWriter> statusMessageWriter = new(MockBehavior.Strict);
        statusMessageWriter
            .Setup(writer => writer.ShowInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((message, _) => infoMessages.Add(message))
            .Returns(Task.CompletedTask);
        statusMessageWriter
            .Setup(writer => writer.ShowSuccessAsync(
                "StemCode subscription sign-in completed.",
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        StemCodeEnterpriseCredentialService sut = new(
            httpClient,
            configurationStore.Object,
            secretStore.Object,
            statusMessageWriter.Object,
            NullLogger<StemCodeEnterpriseCredentialService>.Instance,
            _ => true);

        Task<StemCodeEnterpriseResolvedCredential> resolveTask = sut.ResolveAsync(
            expiredCredentials,
            forceRefresh: true,
            CancellationToken.None);

        string authorizationUrl = await WaitForAuthorizationUrlAsync(infoMessages);
        authorizationUrl.Should().StartWith("https://app.getstemcode.com/api/v1/auth/oauth/authorize?");
        string redirectUri = GetQueryParameter(authorizationUrl, "redirect_uri")
            ?? throw new InvalidOperationException("redirect_uri was missing from the authorization URL.");

        using HttpClient callbackClient = new();
        using HttpRequestMessage callbackRequest = new(HttpMethod.Post, redirectUri)
        {
            Content = new StringContent(
                """
                {"expiresAtUtc":"2099-01-01T00:00:00Z"}
                """,
                Encoding.UTF8,
                "application/json")
        };
        callbackRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "reauthenticated-token");
        callbackRequest.Headers.TryAddWithoutValidation("X-StemCode-Token-Expires-At", "2099-01-01T00:00:00Z");

        using HttpResponseMessage callbackResponse = await callbackClient.SendAsync(callbackRequest);
        callbackResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        StemCodeEnterpriseResolvedCredential resolved = await resolveTask;
        resolved.AccessToken.Should().Be("reauthenticated-token");
        savedSecret.Should().NotBeNullOrWhiteSpace();
        sut.CanResolve(savedSecret!).Should().BeTrue();
        configurationStore.VerifyAll();
        secretStore.VerifyAll();
        statusMessageWriter.VerifyAll();
    }

    private static async Task<string> WaitForAuthorizationUrlAsync(IReadOnlyList<string> infoMessages)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            string? message = infoMessages.FirstOrDefault(value =>
                value.StartsWith("If the browser does not open, visit: ", StringComparison.Ordinal));
            if (message is not null)
            {
                return message["If the browser does not open, visit: ".Length..];
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Timed out waiting for the StemCode Enterprise authorization URL.");
    }

    private static string? GetQueryParameter(string url, string parameterName)
    {
        Uri uri = new(url);
        foreach (string pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split('=', 2);
            if (parts.Length == 0 ||
                !string.Equals(Uri.UnescapeDataString(parts[0]), parameterName, StringComparison.Ordinal))
            {
                continue;
            }

            return parts.Length > 1
                ? Uri.UnescapeDataString(parts[1])
                : string.Empty;
        }

        return null;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly string _responseBody;

        public RecordingHandler(string responseBody)
        {
            _responseBody = responseBody;
        }

        public string? RequestBody { get; private set; }

        public Uri? RequestUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class NeverCalledHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException($"Unexpected HTTP request: {request.Method} {request.RequestUri}");
        }
    }
}
