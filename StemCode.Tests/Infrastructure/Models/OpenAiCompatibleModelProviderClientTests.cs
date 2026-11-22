using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StemCode.Application.Abstractions;
using StemCode.Application.Exceptions;
using StemCode.Domain.Models;
using StemCode.Infrastructure;
using StemCode.Infrastructure.Anthropic;
using StemCode.Infrastructure.GitHub;
using StemCode.Infrastructure.Models;
using StemCode.Infrastructure.StemCodeEnterprise;
using StemCode.Infrastructure.OpenAi;
using System.Net;
using System.Text;

namespace StemCode.Tests.Infrastructure.Models;

public sealed class OpenAiCompatibleModelProviderClientTests
{
    [Fact]
    public async Task GetAvailableModelsAsync_Should_RequestV1Models_When_CompatibleProviderBaseUrlHasNoPath()
    {
        RecordingHandler handler = new("""
            {
              "data": [
                { "id": "gpt-4.1" }
              ]
            }
            """);
        HttpClient httpClient = new(handler);
        OpenAiCompatibleModelProviderClient sut = CreateSut(httpClient);

        IReadOnlyList<AvailableModel> models = await sut.GetAvailableModelsAsync(
            new AgentProviderProfile(ProviderKind.OpenAiCompatible, "http://127.0.0.1:1234"),
            "test-key",
            CancellationToken.None);

        handler.RequestUri.Should().Be(new Uri("http://127.0.0.1:1234/v1/models"));
        models.Select(model => model.Id).Should().Equal("gpt-4.1");
    }

    [Fact]
    public async Task GetAvailableModelsAsync_Should_ParseContextWindowMetadata_When_Returned()
    {
        RecordingHandler handler = new("""
            {
              "data": [
                { "id": "model-a", "context_length": 128000 },
                { "id": "model-b", "inputTokenLimit": 1048576 },
                { "id": "model-c", "limits": { "context_window_tokens": "32768" } },
                { "id": "model-d", "context_length": -1 }
              ]
            }
            """);
        HttpClient httpClient = new(handler);
        OpenAiCompatibleModelProviderClient sut = CreateSut(httpClient);

        IReadOnlyList<AvailableModel> models = await sut.GetAvailableModelsAsync(
            new AgentProviderProfile(ProviderKind.OpenAiCompatible, "http://127.0.0.1:1234"),
            "test-key",
            CancellationToken.None);

        models.Select(model => model.ContextWindowTokens)
            .Should()
            .Equal(new int?[] { 128_000, 1_048_576, 32_768, null });
    }

    [Fact]
    public async Task GetAvailableModelsAsync_Should_ApplyDeepSeekContextWindowFallback_When_MetadataIsMissing()
    {
        RecordingHandler handler = new("""
            {
              "data": [
                { "id": "deepseek-v4-pro" },
                { "id": "deepseek-v4-flash" },
                { "id": "deepseek-chat" }
              ]
            }
            """);
        HttpClient httpClient = new(handler);
        OpenAiCompatibleModelProviderClient sut = CreateSut(httpClient);

        IReadOnlyList<AvailableModel> models = await sut.GetAvailableModelsAsync(
            new AgentProviderProfile(ProviderKind.DeepSeek, null),
            "test-key",
            CancellationToken.None);

        models.Select(model => model.ContextWindowTokens)
            .Should()
            .Equal(new int?[] { 1_000_000, 1_000_000, null });
    }

    [Fact]
    public async Task GetAvailableModelsAsync_Should_Not_ApplyDeepSeekContextWindowFallback_ForOpenCodeZenModels()
    {
        RecordingHandler handler = new("""
            {
              "data": [
                { "id": "deepseek-v4-flash-free" }
              ]
            }
            """);
        HttpClient httpClient = new(handler);
        OpenAiCompatibleModelProviderClient sut = CreateSut(httpClient);

        IReadOnlyList<AvailableModel> models = await sut.GetAvailableModelsAsync(
            new AgentProviderProfile(ProviderKind.OpenCodeZen, null),
            "test-key",
            CancellationToken.None);

        models.Should().ContainSingle().Which.Should().Be(new AvailableModel("deepseek-v4-flash-free", null));
    }

    [Fact]
    public async Task GetAvailableModelsAsync_Should_RequestModelsRelativeToExplicitV1BaseUrl_When_CompatibleProviderBaseUrlAlreadyIncludesV1()
    {
        RecordingHandler handler = new("""
            {
              "data": [
                { "id": "gpt-5-mini" }
              ]
            }
            """);
        HttpClient httpClient = new(handler);
        OpenAiCompatibleModelProviderClient sut = CreateSut(httpClient);

        IReadOnlyList<AvailableModel> models = await sut.GetAvailableModelsAsync(
            new AgentProviderProfile(ProviderKind.OpenAiCompatible, "https://provider.example.com/v1"),
            "test-key",
            CancellationToken.None);

        handler.RequestUri.Should().Be(new Uri("https://provider.example.com/v1/models"));
        models.Select(model => model.Id).Should().Equal("gpt-5-mini");
    }

    [Fact]
    public async Task GetAvailableModelsAsync_Should_RequestGoogleAiStudioModelsEndpoint_When_GoogleAiStudioProviderIsConfigured()
    {
        RecordingHandler handler = new("""
            {
              "data": [
                { "id": "gemini-2.5-flash" }
              ]
            }
            """);
        HttpClient httpClient = new(handler);
        OpenAiCompatibleModelProviderClient sut = CreateSut(httpClient);

        IReadOnlyList<AvailableModel> models = await sut.GetAvailableModelsAsync(
            new AgentProviderProfile(ProviderKind.GoogleAiStudio, null),
            "test-key",
            CancellationToken.None);

        handler.RequestUri.Should().Be(new Uri("https://generativelanguage.googleapis.com/v1beta/openai/models"));
        models.Select(model => model.Id).Should().Equal("gemini-2.5-flash");
    }

    [Fact]
    public async Task GetAvailableModelsAsync_Should_ResolveStemCodeEnterpriseCredentials_When_CompatibleProviderUsesEnterpriseAuth()
    {
        RecordingHandler handler = new("""
            {
              "data": [
                { "id": "gpt-5" }
              ]
            }
            """);
        HttpClient httpClient = new(handler);
        Mock<IStemCodeEnterpriseCredentialService> credentialService = new(MockBehavior.Strict);
        credentialService
            .Setup(service => service.CanResolve("enterprise-credential-json"))
            .Returns(true);
        credentialService
            .Setup(service => service.ResolveAsync(
                "enterprise-credential-json",
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StemCodeEnterpriseResolvedCredential("enterprise-access-token"));
        OpenAiCompatibleModelProviderClient sut = CreateSut(
            httpClient,
            stemCodeEnterpriseCredentialService: credentialService.Object);

        IReadOnlyList<AvailableModel> models = await sut.GetAvailableModelsAsync(
            new AgentProviderProfile(ProviderKind.OpenAiCompatible, "https://enterprise.example.com/v1"),
            "enterprise-credential-json",
            CancellationToken.None);

        handler.RequestUri.Should().Be(new Uri("https://enterprise.example.com/v1/models"));
        handler.AuthorizationHeader.Should().Be("Bearer enterprise-access-token");
        models.Select(model => model.Id).Should().Equal("gpt-5");
        credentialService.VerifyAll();
    }

    [Fact]
    public async Task GetAvailableModelsAsync_Should_SendEnterpriseProjectHeadersUsingWorkspaceName_When_NoGitRepositoryExists()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), "stemcode-enterprise-models-" + Guid.NewGuid().ToString("N"));
        string workspaceRoot = Path.Combine(tempPath, "workspace-name");
        Directory.CreateDirectory(workspaceRoot);

        try
        {
            RecordingHandler handler = new("""
                {
                  "data": [
                    { "id": "gpt-5" }
                  ]
                }
                """);
            HttpClient httpClient = new(handler);
            Mock<IStemCodeEnterpriseCredentialService> credentialService = new(MockBehavior.Strict);
            credentialService
                .Setup(service => service.CanResolve("enterprise-credential-json"))
                .Returns(true);
            credentialService
                .Setup(service => service.ResolveAsync(
                    "enterprise-credential-json",
                    false,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new StemCodeEnterpriseResolvedCredential("enterprise-access-token"));
            OpenAiCompatibleModelProviderClient sut = CreateSut(
                httpClient,
                stemCodeEnterpriseCredentialService: credentialService.Object,
                providerRequestProjectHeaderProvider: new ProviderRequestProjectHeaderProvider(
                    new StubWorkspaceRootProvider(workspaceRoot)));

            await sut.GetAvailableModelsAsync(
                new AgentProviderProfile(ProviderKind.OpenAiCompatible, "https://enterprise.example.com/v1"),
                "enterprise-credential-json",
                CancellationToken.None);

            handler.OpenRouterTitleHeader.Should().Be("StemCode");
            handler.ProjectHeader.Should().Be("workspace-name");
            credentialService.VerifyAll();
        }
        finally
        {
            Directory.Delete(tempPath, recursive: true);
        }
    }

    [Fact]
    public async Task GetAvailableModelsAsync_Should_SendConfiguredProjectHeader_When_ProjectNameEnvironmentVariableIsSet()
    {
        using EnvironmentVariableScope projectName = new(
            ProviderRequestProjectHeaderProvider.ProjectNameEnvironmentVariableName,
            "customer-portal");
        RecordingHandler handler = new("""
            {
              "data": [
                { "id": "gpt-5" }
              ]
            }
            """);
        HttpClient httpClient = new(handler);
        OpenAiCompatibleModelProviderClient sut = CreateSut(httpClient);

        await sut.GetAvailableModelsAsync(
            new AgentProviderProfile(ProviderKind.OpenAiCompatible, "https://provider.example.com/v1"),
            "test-key",
            CancellationToken.None);

        handler.ProjectHeader.Should().Be("customer-portal");
    }

    [Fact]
    public async Task GetAvailableModelsAsync_Should_SendConfiguredTitleHeader_When_TitleEnvironmentVariableIsSet()
    {
        using EnvironmentVariableScope title = new(
            ProviderRequestProjectHeaderProvider.TitleEnvironmentVariableName,
            "Customer Portal");
        RecordingHandler handler = new("""
            {
              "data": [
                { "id": "gpt-5" }
              ]
            }
            """);
        HttpClient httpClient = new(handler);
        OpenAiCompatibleModelProviderClient sut = CreateSut(httpClient);

        await sut.GetAvailableModelsAsync(
            new AgentProviderProfile(ProviderKind.OpenRouter, null),
            "test-key",
            CancellationToken.None);

        handler.OpenRouterTitleHeader.Should().Be("Customer Portal");
    }

    [Fact]
    public async Task GetAvailableModelsAsync_Should_ReauthenticateStemCodeEnterprise_When_RefreshedCredentialIsStillUnauthorized()
    {
        SequencedHandler handler = new(
            new HttpResponseMessage(HttpStatusCode.Unauthorized),
            new HttpResponseMessage(HttpStatusCode.Unauthorized),
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "data": [
                        { "id": "enterprise-model" }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            });
        HttpClient httpClient = new(handler);
        Mock<IStemCodeEnterpriseCredentialService> credentialService = new(MockBehavior.Strict);
        credentialService
            .Setup(service => service.CanResolve("enterprise-credential-json"))
            .Returns(true);
        credentialService
            .Setup(service => service.ResolveAsync(
                "enterprise-credential-json",
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StemCodeEnterpriseResolvedCredential("expired-token"));
        credentialService
            .Setup(service => service.ResolveAsync(
                "enterprise-credential-json",
                true,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StemCodeEnterpriseResolvedCredential("still-invalid-token"));
        credentialService
            .Setup(service => service.AuthenticateAsync(
                "https://enterprise.example.com/v1",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("enterprise-credential-json-reauthenticated");
        credentialService
            .Setup(service => service.ResolveAsync(
                "enterprise-credential-json-reauthenticated",
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StemCodeEnterpriseResolvedCredential("fresh-token"));
        OpenAiCompatibleModelProviderClient sut = CreateSut(
            httpClient,
            stemCodeEnterpriseCredentialService: credentialService.Object);

        IReadOnlyList<AvailableModel> models = await sut.GetAvailableModelsAsync(
            new AgentProviderProfile(ProviderKind.OpenAiCompatible, "https://enterprise.example.com/v1"),
            "enterprise-credential-json",
            CancellationToken.None);

        handler.AuthorizationHeaders.Should().Equal(
            "Bearer expired-token",
            "Bearer still-invalid-token",
            "Bearer fresh-token");
        models.Select(model => model.Id).Should().Equal("enterprise-model");
        credentialService.VerifyAll();
    }

    [Fact]
    public async Task GetAvailableModelsAsync_Should_RequestOpenRouterModelsEndpointWithAppHeaders_When_OpenRouterProviderIsConfigured()
    {
        RecordingHandler handler = new("""
            {
              "data": [
                { "id": "openai/gpt-4o" }
              ]
            }
            """);
        HttpClient httpClient = new(handler);
        OpenAiCompatibleModelProviderClient sut = CreateSut(httpClient);

        IReadOnlyList<AvailableModel> models = await sut.GetAvailableModelsAsync(
            new AgentProviderProfile(ProviderKind.OpenRouter, null),
            "test-key",
            CancellationToken.None);

        handler.RequestUri.Should().Be(new Uri("https://openrouter.ai/api/v1/models"));
        handler.AuthorizationHeader.Should().Be("Bearer test-key");
        handler.OpenRouterRefererHeader.Should().Be("https://github.com/rizwan3d/StemCode");
        handler.OpenRouterTitleHeader.Should().Be("StemCode");
        models.Select(model => model.Id).Should().Equal("openai/gpt-4o");
    }

    [Fact]
    public async Task GetAvailableModelsAsync_Should_RequestKiloCodeModelsEndpointWithKiloHeaders_When_ProviderIsConfigured()
    {
        RecordingHandler handler = new("""
            {
              "data": [
                { "id": "kilo-auto/free", "context_length": 128000 }
              ]
            }
            """);
        HttpClient httpClient = new(handler);
        OpenAiCompatibleModelProviderClient sut = CreateSut(httpClient);

        IReadOnlyList<AvailableModel> models = await sut.GetAvailableModelsAsync(
            new AgentProviderProfile(ProviderKind.KiloCode, null),
            "test-key",
            CancellationToken.None);

        handler.RequestUri.Should().Be(new Uri("https://api.kilo.ai/api/gateway/models"));
        handler.AuthorizationHeader.Should().Be("Bearer test-key");
        handler.KiloCodeEditorHeader.Should().Be("StemCode");
        handler.KiloCodeUserAgentHeader.Should().Be("stemcode-kilo-provider");
        models.Should().ContainSingle();
        models[0].Id.Should().Be("kilo-auto/free");
        models[0].ContextWindowTokens.Should().Be(128000);
        models[0].ContextMetadata?.ContextWindowTokens.Should().Be(128000);
    }

    [Fact]
    public async Task GetAvailableModelsAsync_Should_RequestCerebrasModelsEndpoint_When_ProviderIsConfigured()
    {
        RecordingHandler handler = new("""
            {
              "data": [
                { "id": "llama3.1-8b" }
              ]
            }
            """);
        HttpClient httpClient = new(handler);
        OpenAiCompatibleModelProviderClient sut = CreateSut(httpClient);

        IReadOnlyList<AvailableModel> models = await sut.GetAvailableModelsAsync(
            new AgentProviderProfile(ProviderKind.Cerebras, null),
            "cerebras-key",
            CancellationToken.None);

        handler.RequestUri.Should().Be(new Uri("https://api.cerebras.ai/v1/models"));
        handler.AuthorizationHeader.Should().Be("Bearer cerebras-key");
        models.Select(model => model.Id).Should().Equal("llama3.1-8b");
    }

    [Fact]
    public async Task GetAvailableModelsAsync_Should_RequestGroqModelsEndpoint_When_ProviderIsConfigured()
    {
        RecordingHandler handler = new("""
            {
              "data": [
                { "id": "llama-3.3-70b-versatile" }
              ]
            }
            """);
        HttpClient httpClient = new(handler);
        OpenAiCompatibleModelProviderClient sut = CreateSut(httpClient);

        IReadOnlyList<AvailableModel> models = await sut.GetAvailableModelsAsync(
            new AgentProviderProfile(ProviderKind.Groq, null),
            "groq-key",
            CancellationToken.None);

        handler.RequestUri.Should().Be(new Uri("https://api.groq.com/openai/v1/models"));
        handler.AuthorizationHeader.Should().Be("Bearer groq-key");
        models.Select(model => model.Id).Should().Equal("llama-3.3-70b-versatile");
    }

    [Fact]
    public async Task GetAvailableModelsAsync_Should_RequestOpenCodeZenModelsEndpoint_When_ProviderIsConfigured()
    {
        RecordingHandler handler = new("""
            {
              "data": [
                { "id": "qwen3.6-plus" }
              ]
            }
            """);
        HttpClient httpClient = new(handler);
        OpenAiCompatibleModelProviderClient sut = CreateSut(httpClient);

        IReadOnlyList<AvailableModel> models = await sut.GetAvailableModelsAsync(
            new AgentProviderProfile(ProviderKind.OpenCodeZen, null),
            "opencode-zen-key",
            CancellationToken.None);

        handler.RequestUri.Should().Be(new Uri("https://opencode.ai/zen/v1/models"));
        handler.AuthorizationHeader.Should().Be("Bearer opencode-zen-key");
        models.Select(model => model.Id).Should().Equal("qwen3.6-plus");
    }

    [Fact]
    public async Task GetAvailableModelsAsync_Should_RequestOllamaModelsEndpoint_When_ProviderIsConfigured()
    {
        RecordingHandler handler = new("""
            {
              "data": [
                { "id": "llama3.2", "context_length": 131072 }
              ]
            }
            """);
        HttpClient httpClient = new(handler);
        OpenAiCompatibleModelProviderClient sut = CreateSut(httpClient);

        IReadOnlyList<AvailableModel> models = await sut.GetAvailableModelsAsync(
            new AgentProviderProfile(ProviderKind.Ollama, null),
            "ollama",
            CancellationToken.None);

        handler.RequestUri.Should().Be(new Uri("http://127.0.0.1:11434/v1/models"));
        handler.AuthorizationHeader.Should().Be("Bearer ollama");
        models.Should().ContainSingle();
        models[0].Id.Should().Be("llama3.2");
        models[0].ContextWindowTokens.Should().Be(131072);
        models[0].ContextMetadata?.ContextWindowTokens.Should().Be(131072);
    }

    [Fact]
    public async Task GetAvailableModelsAsync_Should_RequestLmStudioModelsEndpoint_When_ProviderIsConfigured()
    {
        RecordingHandler handler = new("""
            {
              "data": [
                { "id": "qwen3-8b", "context_length": 32768 }
              ]
            }
            """);
        HttpClient httpClient = new(handler);
        OpenAiCompatibleModelProviderClient sut = CreateSut(httpClient);

        IReadOnlyList<AvailableModel> models = await sut.GetAvailableModelsAsync(
            new AgentProviderProfile(ProviderKind.LmStudio, "http://127.0.0.1:4321/v1"),
            "lm-studio-key",
            CancellationToken.None);

        handler.RequestUri.Should().Be(new Uri("http://127.0.0.1:4321/v1/models"));
        handler.AuthorizationHeader.Should().Be("Bearer lm-studio-key");
        models.Should().ContainSingle();
        models[0].Id.Should().Be("qwen3-8b");
        models[0].ContextWindowTokens.Should().Be(32768);
        models[0].ContextMetadata?.ContextWindowTokens.Should().Be(32768);
    }

    [Fact]
    public async Task GetAvailableModelsAsync_Should_RequestOllamaCloudTagsEndpoint_When_ProviderIsConfigured()
    {
        RecordingHandler handler = new("""
            {
              "models": [
                { "name": "gpt-oss:20b" }
              ]
            }
            """);
        HttpClient httpClient = new(handler);
        OpenAiCompatibleModelProviderClient sut = CreateSut(httpClient);

        IReadOnlyList<AvailableModel> models = await sut.GetAvailableModelsAsync(
            new AgentProviderProfile(ProviderKind.OllamaCloud, null),
            "ollama-cloud-key",
            CancellationToken.None);

        handler.RequestUri.Should().Be(new Uri("https://ollama.com/api/tags"));
        handler.AuthorizationHeader.Should().Be("Bearer ollama-cloud-key");
        models.Should().ContainSingle().Which.Should().Be(new AvailableModel("gpt-oss:20b", null));
    }

    [Fact]
    public async Task GetAvailableModelsAsync_Should_RequestAnthropicModelsEndpointWithAnthropicHeaders_When_AnthropicProviderIsConfigured()
    {
        RecordingHandler handler = new("""
            {
              "data": [
                { "id": "claude-sonnet-4-6" }
              ]
            }
            """);
        HttpClient httpClient = new(handler);
        OpenAiCompatibleModelProviderClient sut = CreateSut(httpClient);

        IReadOnlyList<AvailableModel> models = await sut.GetAvailableModelsAsync(
            new AgentProviderProfile(ProviderKind.Anthropic, null),
            "test-key",
            CancellationToken.None);

        handler.RequestUri.Should().Be(new Uri("https://api.anthropic.com/v1/models"));
        handler.AuthorizationHeader.Should().BeNull();
        handler.AnthropicApiKeyHeader.Should().Be("test-key");
        handler.AnthropicVersionHeader.Should().Be("2023-06-01");
        models.Select(model => model.Id).Should().Equal("claude-sonnet-4-6");
    }

    [Fact]
    public async Task GetAvailableModelsAsync_Should_ReturnClaudeCodeAliasesWithoutCallingSubscriptionApi()
    {
        Mock<IAnthropicClaudeAccountCredentialService> credentialService = new(MockBehavior.Strict);
        credentialService
            .Setup(service => service.ResolveAsync(
                "stored-credentials",
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AnthropicClaudeAccountResolvedCredential("claude-code-cli"));

        RecordingHandler handler = new("""
            {
              "data": [
                { "id": "claude-sonnet-4-6" }
              ]
            }
            """);
        HttpClient httpClient = new(handler);
        OpenAiCompatibleModelProviderClient sut = CreateSut(
            httpClient,
            anthropicClaudeAccountCredentialService: credentialService.Object);

        IReadOnlyList<AvailableModel> models = await sut.GetAvailableModelsAsync(
            new AgentProviderProfile(ProviderKind.AnthropicClaudeAccount, null),
            "stored-credentials",
            CancellationToken.None);

        handler.RequestUri.Should().BeNull();
        models.Select(model => model.Id).Should().Equal("sonnet", "opus", "haiku", "fable");
        credentialService.VerifyAll();
    }

    [Fact]
    public async Task GetAvailableModelsAsync_Should_RequestGitHubCopilotModelsEndpointWithCopilotHeaders_When_ProviderIsConfigured()
    {
        Mock<IGitHubCopilotCredentialService> credentialService = new(MockBehavior.Strict);
        credentialService
            .Setup(service => service.ResolveAsync(
                "stored-credentials",
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubCopilotResolvedCredential(
                "copilot-token",
                EnterpriseDomain: null,
                new Uri("https://api.individual.githubcopilot.com/")));

        RecordingHandler handler = new("""
            {
              "data": [
                { "id": "gpt-5" }
              ]
            }
            """);
        HttpClient httpClient = new(handler);
        OpenAiCompatibleModelProviderClient sut = CreateSut(
            httpClient,
            gitHubCopilotCredentialService: credentialService.Object);

        IReadOnlyList<AvailableModel> models = await sut.GetAvailableModelsAsync(
            new AgentProviderProfile(ProviderKind.GitHubCopilot, null),
            "stored-credentials",
            CancellationToken.None);

        handler.RequestUri.Should().Be(new Uri("https://api.individual.githubcopilot.com/models"));
        handler.AuthorizationHeader.Should().Be("Bearer copilot-token");
        handler.CopilotIntegrationIdHeader.Should().Be("vscode-chat");
        models.Select(model => model.Id).Should().Equal("gpt-5");
        credentialService.VerifyAll();
    }



    [Fact]
    public async Task GetAvailableModelsAsync_Should_RequestAccountBackedModels_When_OpenAiChatGptAccountProviderIsConfigured()
    {
        Mock<IOpenAiChatGptAccountCredentialService> credentialService = new(MockBehavior.Strict);
        credentialService
            .Setup(service => service.ResolveAsync(
                "stored-credentials",
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpenAiChatGptAccountResolvedCredential("access-token", "acct_123"));

        RecordingHandler handler = new("""
            {
              "models": [
                { "slug": "account-model-a" },
                "account-model-b"
              ]
            }
            """);
        HttpClient httpClient = new(handler);
        OpenAiCompatibleModelProviderClient sut = CreateSut(
            httpClient,
            credentialService.Object,
            new StubOpenAiCodexClientVersionProvider("0.125.0"));

        IReadOnlyList<AvailableModel> models = await sut.GetAvailableModelsAsync(
            new AgentProviderProfile(ProviderKind.OpenAiChatGptAccount, null),
            "stored-credentials",
            CancellationToken.None);

        handler.RequestUri!.AbsolutePath.Should().EndWith("/models");
        handler.AuthorizationHeader.Should().Be("Bearer access-token");
        handler.AccountHeader.Should().Be("acct_123");
        models.Select(model => model.Id).Should().Equal("account-model-a", "account-model-b");
        handler.RequestUri!.Query.Should().Be("?client_version=0.125.0");
        credentialService.VerifyAll();
    }

    [Fact]
    public async Task GetAvailableModelsAsync_Should_RefreshCredentialsOnce_When_AccountModelRequestIsUnauthorized()
    {
        Mock<IOpenAiChatGptAccountCredentialService> credentialService = new(MockBehavior.Strict);
        credentialService
            .Setup(service => service.ResolveAsync(
                "stored-credentials",
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpenAiChatGptAccountResolvedCredential("expired-token", "acct_123"));
        credentialService
            .Setup(service => service.ResolveAsync(
                "stored-credentials",
                true,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpenAiChatGptAccountResolvedCredential("fresh-token", "acct_123"));

        SequencedHandler handler = new(
            new HttpResponseMessage(HttpStatusCode.Unauthorized),
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "data": [
                        { "id": "fresh-model" }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            });
        HttpClient httpClient = new(handler);
        OpenAiCompatibleModelProviderClient sut = CreateSut(httpClient, credentialService.Object);

        IReadOnlyList<AvailableModel> models = await sut.GetAvailableModelsAsync(
            new AgentProviderProfile(ProviderKind.OpenAiChatGptAccount, null),
            "stored-credentials",
            CancellationToken.None);

        handler.AuthorizationHeaders.Should().Equal("Bearer expired-token", "Bearer fresh-token");
        models.Select(model => model.Id).Should().Equal("fresh-model");
        credentialService.VerifyAll();
    }

    [Fact]
    public async Task GetAvailableModelsAsync_Should_ThrowModelProviderException_When_AccountModelRequestFails()
    {
        Mock<IOpenAiChatGptAccountCredentialService> credentialService = new(MockBehavior.Strict);
        credentialService
            .Setup(service => service.ResolveAsync(
                "stored-credentials",
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpenAiChatGptAccountResolvedCredential("access-token", "acct_123"));

        RecordingHandler handler = new(
            responseBody: string.Empty,
            statusCode: HttpStatusCode.ServiceUnavailable);
        HttpClient httpClient = new(handler);
        OpenAiCompatibleModelProviderClient sut = CreateSut(httpClient, credentialService.Object);

        Func<Task> action = () => sut.GetAvailableModelsAsync(
            new AgentProviderProfile(ProviderKind.OpenAiChatGptAccount, null),
            "stored-credentials",
            CancellationToken.None);

        await action.Should().ThrowAsync<ModelProviderException>()
            .WithMessage("*account API*HTTP 503*");
        credentialService.VerifyAll();
    }

    private static OpenAiCompatibleModelProviderClient CreateSut(
        HttpClient httpClient,
        IOpenAiChatGptAccountCredentialService? credentialService = null,
        IOpenAiCodexClientVersionProvider? versionProvider = null,
        IAnthropicClaudeAccountCredentialService? anthropicClaudeAccountCredentialService = null,
        IGitHubCopilotCredentialService? gitHubCopilotCredentialService = null,
        IStemCodeEnterpriseCredentialService? stemCodeEnterpriseCredentialService = null,
        ProviderRequestProjectHeaderProvider? providerRequestProjectHeaderProvider = null)
    {
        return new OpenAiCompatibleModelProviderClient(
            httpClient,
            NullLogger<OpenAiCompatibleModelProviderClient>.Instance,
            credentialService,
            versionProvider,
            anthropicClaudeAccountCredentialService,
            gitHubCopilotCredentialService,
            stemCodeEnterpriseCredentialService,
            providerRequestProjectHeaderProvider);
    }

    private sealed class StubOpenAiCodexClientVersionProvider : IOpenAiCodexClientVersionProvider
    {
        private readonly string _clientVersion;

        public StubOpenAiCodexClientVersionProvider(string clientVersion)
        {
            _clientVersion = clientVersion;
        }

        public Task<string> GetClientVersionAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(_clientVersion);
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly string _responseBody;

        public Uri? RequestUri { get; private set; }

        public string? AuthorizationHeader { get; private set; }

        public string? AnthropicApiKeyHeader { get; private set; }

        public string? AnthropicVersionHeader { get; private set; }

        public string? AnthropicBetaHeader { get; private set; }

        public string? AnthropicAppHeader { get; private set; }

        public string? OpenRouterRefererHeader { get; private set; }

        public string? OpenRouterTitleHeader { get; private set; }

        public string? ProjectHeader { get; private set; }

        public string? KiloCodeEditorHeader { get; private set; }

        public string? KiloCodeUserAgentHeader { get; private set; }

        public string? AccountHeader { get; private set; }

        public string? CopilotIntegrationIdHeader { get; private set; }

        public HttpStatusCode StatusCode { get; }

        public RecordingHandler(
            string responseBody,
            HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _responseBody = responseBody;
            StatusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CaptureRequest(request);

            HttpResponseMessage response = new(StatusCode)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json")
            };

            return Task.FromResult(response);
        }

        private void CaptureRequest(HttpRequestMessage request)
        {
            RequestUri = request.RequestUri;
            AuthorizationHeader = request.Headers.Authorization?.ToString();
            AnthropicApiKeyHeader = request.Headers.TryGetValues("x-api-key", out IEnumerable<string>? apiKeyValues)
                ? apiKeyValues.FirstOrDefault()
                : null;
            AnthropicVersionHeader = request.Headers.TryGetValues("anthropic-version", out IEnumerable<string>? versionValues)
                ? versionValues.FirstOrDefault()
                : null;
            AnthropicBetaHeader = request.Headers.TryGetValues("anthropic-beta", out IEnumerable<string>? betaValues)
                ? betaValues.FirstOrDefault()
                : null;
            AnthropicAppHeader = request.Headers.TryGetValues("x-app", out IEnumerable<string>? appValues)
                ? appValues.FirstOrDefault()
                : null;
            OpenRouterRefererHeader = request.Headers.TryGetValues("HTTP-Referer", out IEnumerable<string>? refererValues)
                ? refererValues.FirstOrDefault()
                : null;
            OpenRouterTitleHeader = request.Headers.TryGetValues("X-Title", out IEnumerable<string>? titleValues)
                ? titleValues.FirstOrDefault()
                : null;
            ProjectHeader = request.Headers.TryGetValues("X-Project", out IEnumerable<string>? projectValues)
                ? projectValues.FirstOrDefault()
                : null;
            KiloCodeEditorHeader = request.Headers.TryGetValues("X-KILOCODE-EDITORNAME", out IEnumerable<string>? kiloEditorValues)
                ? kiloEditorValues.FirstOrDefault()
                : null;
            KiloCodeUserAgentHeader = request.Headers.TryGetValues("User-Agent", out IEnumerable<string>? userAgentValues)
                ? userAgentValues.FirstOrDefault()
                : null;
            AccountHeader = request.Headers.TryGetValues(
                "Chat" + "G" + "P" + "T-Account-Id",
                out IEnumerable<string>? accountValues)
                ? accountValues.FirstOrDefault()
                : null;
            CopilotIntegrationIdHeader = request.Headers.TryGetValues(
                "Copilot-Integration-Id",
                out IEnumerable<string>? integrationValues)
                    ? integrationValues.FirstOrDefault()
                    : null;
        }
    }

    private sealed class SequencedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public SequencedHandler(params HttpResponseMessage[] responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        public List<string?> AuthorizationHeaders { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            AuthorizationHeaders.Add(request.Headers.Authorization?.ToString());
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class StubWorkspaceRootProvider : IWorkspaceRootProvider
    {
        private readonly string _workspaceRoot;

        public StubWorkspaceRootProvider(string workspaceRoot)
        {
            _workspaceRoot = workspaceRoot;
        }

        public string GetWorkspaceRoot()
        {
            return _workspaceRoot;
        }
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previousValue;

        public EnvironmentVariableScope(string name, string? value)
        {
            _name = name;
            _previousValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_name, _previousValue);
        }
    }
}
