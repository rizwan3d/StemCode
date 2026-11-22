using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StemCode.Application.Abstractions;
using StemCode.Application.Exceptions;
using StemCode.Application.Models;
using StemCode.Domain.Models;
using StemCode.Infrastructure;
using StemCode.Infrastructure.Conversation;
using StemCode.Infrastructure.GitHub;
using StemCode.Infrastructure.StemCodeEnterprise;
using StemCode.Infrastructure.OpenAi;
using StemCode.Infrastructure.Secrets;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace StemCode.Tests.Infrastructure.Conversation;

public sealed class OpenAiCompatibleConversationProviderClientTests
{
    [Fact]
    public async Task SendAsync_Should_PostChatCompletionsToV1Endpoint_When_CompatibleProviderBaseUrlHasNoPath()
    {
        RecordingHandler handler = new("""
            {
              "id": "resp_1",
              "choices": [
                {
                  "message": {
                    "content": "Hello."
                  }
                }
              ]
            }
            """);
        HttpClient httpClient = new(handler);
        OpenAiCompatibleConversationProviderClient sut = CreateSut(httpClient);

        ConversationProviderPayload payload = await sut.SendAsync(
            new ConversationProviderRequest(
                new AgentProviderProfile(ProviderKind.OpenAiCompatible, "http://127.0.0.1:1234"),
                "test-key",
                "gpt-4.1",
                [
                    ConversationRequestMessage.User("Explain the diff.")
                ],
                "You are helpful.",
                [CreateToolDefinition("file_read")]),
            CancellationToken.None);

        handler.RequestUri.Should().Be(new Uri("http://127.0.0.1:1234/v1/chat/completions"));
        handler.RequestMethod.Should().Be(HttpMethod.Post);
        handler.AuthorizationHeader.Should().Be("Bearer test-key");
        handler.RequestBody.Should().Contain("\"model\":\"gpt-4.1\"");
        handler.RequestBody.Should().Contain("\"role\":\"system\"");
        handler.RequestBody.Should().Contain("\"role\":\"user\"");
        handler.RequestBody.Should().Contain("\"tools\"");
        handler.RequestBody.Should().Contain("\"name\":\"file_read\"");
        payload.ResponseId.Should().Be("req_789");
    }

    [Fact]
    public async Task SendAsync_Should_SerializeAssistantToolCallsAndToolMessages_When_RequestContainsToolHistory()
    {
        RecordingHandler handler = new("""
            {
              "id": "resp_2",
              "choices": [
                {
                  "message": {
                    "content": "Done."
                  }
                }
              ]
            }
            """);
        HttpClient httpClient = new(handler);
        OpenAiCompatibleConversationProviderClient sut = CreateSut(httpClient);

        ConversationProviderPayload payload = await sut.SendAsync(
            new ConversationProviderRequest(
                new AgentProviderProfile(ProviderKind.OpenAiCompatible, "http://127.0.0.1:1234/v1"),
                "test-key",
                "gpt-4.1",
                [
                    ConversationRequestMessage.User("Create the files."),
                    ConversationRequestMessage.AssistantToolCalls([
                        new ConversationToolCall("call_1", "file_write", """{"path":"index.html","content":"...","overwrite":true}""")
                    ]),
                    ConversationRequestMessage.ToolResult("call_1", """{"path":"index.html","written":true}""")
                ],
                "You are helpful.",
                [CreateToolDefinition("file_write")]),
            CancellationToken.None);

        handler.RequestUri.Should().Be(new Uri("http://127.0.0.1:1234/v1/chat/completions"));
        handler.RequestBody.Should().Contain("\"role\":\"assistant\"");
        handler.RequestBody.Should().Contain("\"tool_calls\"");
        handler.RequestBody.Should().Contain("\"id\":\"call_1\"");
        handler.RequestBody.Should().Contain("\"name\":\"file_write\"");
        handler.RequestBody.Should().Contain("\"role\":\"tool\"");
        handler.RequestBody.Should().Contain("\"tool_call_id\":\"call_1\"");
        handler.RequestBody.Should().Contain("\\u0022written\\u0022:true");
        payload.ResponseId.Should().Be("req_789");
    }

    [Fact]
    public async Task SendAsync_Should_ReplayAssistantReasoningMetadata_When_RequestContainsThinkingHistory()
    {
        RecordingHandler handler = new("""
            {
              "id": "resp_reasoning_replay",
              "choices": [
                {
                  "message": {
                    "content": "Done."
                  }
                }
              ]
            }
            """);
        HttpClient httpClient = new(handler);
        OpenAiCompatibleConversationProviderClient sut = CreateSut(httpClient);

        await sut.SendAsync(
            new ConversationProviderRequest(
                new AgentProviderProfile(ProviderKind.OpenAiCompatible, "http://127.0.0.1:1234/v1"),
                "test-key",
                "deepseek-v4-pro",
                [
                    ConversationRequestMessage.User("Inspect the file."),
                    ConversationRequestMessage.AssistantToolCalls(
                        [
                            new ConversationToolCall(
                                "call_1",
                                "file_read",
                                """{"path":"README.md"}""")
                        ],
                        content: "I will inspect README.md.",
                        reasoningContent: "The next step is to read README.md before answering."),
                    ConversationRequestMessage.ToolResult("call_1", """{"content":"hello"}"""),
                    ConversationRequestMessage.AssistantMessage(
                        "I found the file.",
                        reasoningDetailsJson: """
                            [
                              {
                                "type": "reasoning.text",
                                "text": "Router reasoning block"
                              }
                            ]
                            """),
                    ConversationRequestMessage.User("Continue.")
                ],
                "You are helpful.",
                [CreateToolDefinition("file_read")],
                "on"),
            CancellationToken.None);

        using JsonDocument requestDocument = JsonDocument.Parse(handler.RequestBody!);
        JsonElement messages = requestDocument.RootElement.GetProperty("messages");

        JsonElement toolCallAssistant = messages[2];
        toolCallAssistant.GetProperty("role").GetString().Should().Be("assistant");
        toolCallAssistant.GetProperty("reasoning_content").GetString()
            .Should()
            .Be("The next step is to read README.md before answering.");
        toolCallAssistant.TryGetProperty("reasoning_details", out _).Should().BeFalse();

        JsonElement reasoningDetailsAssistant = messages[4];
        reasoningDetailsAssistant.GetProperty("role").GetString().Should().Be("assistant");
        reasoningDetailsAssistant.GetProperty("reasoning_details")[0]
            .GetProperty("text")
            .GetString()
            .Should()
            .Be("Router reasoning block");
        reasoningDetailsAssistant.TryGetProperty("reasoning_content", out _).Should().BeFalse();
    }

    [Fact]
    public async Task SendAsync_Should_PostChatCompletionsToGoogleAiStudioEndpoint_When_GoogleAiStudioProviderIsSelected()
    {
        RecordingHandler handler = new("""
            {
              "id": "resp_4",
              "choices": [
                {
                  "message": {
                    "content": "Hello from Gemini."
                  }
                }
              ]
            }
            """);
        HttpClient httpClient = new(handler);
        OpenAiCompatibleConversationProviderClient sut = CreateSut(httpClient);

        ConversationProviderPayload payload = await sut.SendAsync(
            new ConversationProviderRequest(
                new AgentProviderProfile(ProviderKind.GoogleAiStudio, null),
                "test-key",
                "gemini-2.5-flash",
                [
                    ConversationRequestMessage.User("Say hello.")
                ],
                "You are helpful.",
                []),
            CancellationToken.None);

        handler.RequestUri.Should().Be(new Uri("https://generativelanguage.googleapis.com/v1beta/openai/chat/completions"));
        handler.AuthorizationHeader.Should().Be("Bearer test-key");
        handler.RequestBody.Should().Contain("\"model\":\"gemini-2.5-flash\"");
        payload.ResponseId.Should().Be("req_789");
    }

    [Fact]
    public async Task SendAsync_Should_ResolveStemCodeEnterpriseCredentials_When_CompatibleProviderUsesEnterpriseAuth()
    {
        RecordingHandler handler = new("""
            {
              "id": "resp_enterprise",
              "choices": [
                {
                  "message": {
                    "content": "Hello from enterprise."
                  }
                }
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
        OpenAiCompatibleConversationProviderClient sut = CreateSut(httpClient, credentialService.Object);

        ConversationProviderPayload payload = await sut.SendAsync(
            new ConversationProviderRequest(
                new AgentProviderProfile(ProviderKind.OpenAiCompatible, "https://enterprise.example.com/v1"),
                "enterprise-credential-json",
                "gpt-5",
                [
                    ConversationRequestMessage.User("Say hello.")
                ],
                "You are helpful.",
                []),
            CancellationToken.None);

        handler.RequestUri.Should().Be(new Uri("https://enterprise.example.com/v1/chat/completions"));
        handler.AuthorizationHeader.Should().Be("Bearer enterprise-access-token");
        payload.ResponseId.Should().Be("req_789");
        credentialService.VerifyAll();
    }

    [Fact]
    public async Task SendAsync_Should_SendEnterpriseProjectHeadersUsingGitRepositoryName()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), "stemcode-enterprise-conversation-" + Guid.NewGuid().ToString("N"));
        string repositoryRoot = Path.Combine(tempPath, "sample-repo");
        string workspaceRoot = Path.Combine(repositoryRoot, "src", "app");

        Directory.CreateDirectory(Path.Combine(repositoryRoot, ".git"));
        Directory.CreateDirectory(workspaceRoot);

        try
        {
            RecordingHandler handler = new("""
                {
                  "id": "resp_enterprise_headers",
                  "choices": [
                    {
                      "message": {
                        "content": "Hello from enterprise."
                      }
                    }
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
            OpenAiCompatibleConversationProviderClient sut = CreateSut(
                httpClient,
                credentialService.Object,
                new ProviderRequestProjectHeaderProvider(new StubWorkspaceRootProvider(workspaceRoot)));

            await sut.SendAsync(
                new ConversationProviderRequest(
                    new AgentProviderProfile(ProviderKind.OpenAiCompatible, "https://enterprise.example.com/v1"),
                    "enterprise-credential-json",
                    "gpt-5",
                    [
                        ConversationRequestMessage.User("Say hello.")
                    ],
                    "You are helpful.",
                    []),
                CancellationToken.None);

            handler.OpenRouterTitleHeader.Should().Be("StemCode");
            handler.ProjectHeader.Should().Be("sample-repo");
            credentialService.VerifyAll();
        }
        finally
        {
            Directory.Delete(tempPath, recursive: true);
        }
    }

    [Fact]
    public async Task SendAsync_Should_SendConfiguredProjectHeader_When_ProjectNameEnvironmentVariableIsSet()
    {
        using EnvironmentVariableScope projectName = new(
            ProviderRequestProjectHeaderProvider.ProjectNameEnvironmentVariableName,
            "customer-portal");
        RecordingHandler handler = new("""
            {
              "id": "resp_project_header",
              "choices": [
                {
                  "message": {
                    "content": "Hello."
                  }
                }
              ]
            }
            """);
        HttpClient httpClient = new(handler);
        OpenAiCompatibleConversationProviderClient sut = CreateSut(httpClient);

        await sut.SendAsync(
            new ConversationProviderRequest(
                new AgentProviderProfile(ProviderKind.OpenAiCompatible, "https://provider.example.com/v1"),
                "test-key",
                "gpt-5",
                [
                    ConversationRequestMessage.User("Say hello.")
                ],
                "You are helpful.",
                []),
            CancellationToken.None);

        handler.ProjectHeader.Should().Be("customer-portal");
    }

    [Fact]
    public async Task SendAsync_Should_SendConfiguredTitleHeader_When_TitleEnvironmentVariableIsSet()
    {
        using EnvironmentVariableScope title = new(
            ProviderRequestProjectHeaderProvider.TitleEnvironmentVariableName,
            "Customer Portal");
        RecordingHandler handler = new("""
            {
              "id": "resp_title_header",
              "choices": [
                {
                  "message": {
                    "content": "Hello."
                  }
                }
              ]
            }
            """);
        HttpClient httpClient = new(handler);
        OpenAiCompatibleConversationProviderClient sut = CreateSut(httpClient);

        await sut.SendAsync(
            new ConversationProviderRequest(
                new AgentProviderProfile(ProviderKind.OpenRouter, null),
                "test-key",
                "openai/gpt-4o",
                [
                    ConversationRequestMessage.User("Say hello.")
                ],
                "You are helpful.",
                []),
            CancellationToken.None);

        handler.OpenRouterTitleHeader.Should().Be("Customer Portal");
    }

    [Fact]
    public async Task SendAsync_Should_PostChatCompletionsToOpenRouterEndpointWithAppHeaders_When_OpenRouterProviderIsSelected()
    {
        RecordingHandler handler = new("""
            {
              "id": "resp_openrouter",
              "choices": [
                {
                  "message": {
                    "content": "Hello from OpenRouter."
                  }
                }
              ]
            }
            """);
        HttpClient httpClient = new(handler);
        OpenAiCompatibleConversationProviderClient sut = CreateSut(httpClient);

        ConversationProviderPayload payload = await sut.SendAsync(
            new ConversationProviderRequest(
                new AgentProviderProfile(ProviderKind.OpenRouter, null),
                "test-key",
                "openai/gpt-4o",
                [
                    ConversationRequestMessage.User("Say hello.")
                ],
                "You are helpful.",
                []),
            CancellationToken.None);

        handler.RequestUri.Should().Be(new Uri("https://openrouter.ai/api/v1/chat/completions"));
        handler.AuthorizationHeader.Should().Be("Bearer test-key");
        handler.OpenRouterRefererHeader.Should().Be("https://github.com/rizwan3d/StemCode");
        handler.OpenRouterTitleHeader.Should().Be("StemCode");
        handler.RequestBody.Should().Contain("\"model\":\"openai/gpt-4o\"");
        payload.ProviderKind.Should().Be(ProviderKind.OpenRouter);
        payload.ResponseId.Should().Be("req_789");
    }

    [Fact]
    public async Task SendAsync_Should_PostChatCompletionsToKiloCodeEndpointWithKiloHeaders_When_ProviderIsSelected()
    {
        RecordingHandler handler = new("""
            {
              "id": "resp_kilo",
              "choices": [
                {
                  "message": {
                    "content": "Hello from Kilo."
                  }
                }
              ]
            }
            """);
        HttpClient httpClient = new(handler);
        OpenAiCompatibleConversationProviderClient sut = CreateSut(httpClient);

        ConversationProviderPayload payload = await sut.SendAsync(
            new ConversationProviderRequest(
                new AgentProviderProfile(ProviderKind.KiloCode, null),
                "test-key",
                "kilo-auto/free",
                [
                    ConversationRequestMessage.User("Say hello.")
                ],
                "You are helpful.",
                []),
            CancellationToken.None);

        handler.RequestUri.Should().Be(new Uri("https://api.kilo.ai/api/gateway/chat/completions"));
        handler.AuthorizationHeader.Should().Be("Bearer test-key");
        handler.KiloCodeEditorHeader.Should().Be("StemCode");
        handler.KiloCodeUserAgentHeader.Should().Be("stemcode-kilo-provider");
        handler.RequestBody.Should().Contain("\"model\":\"kilo-auto/free\"");
        payload.ProviderKind.Should().Be(ProviderKind.KiloCode);
        payload.ResponseId.Should().Be("req_789");
    }

    [Fact]
    public async Task SendAsync_Should_PostChatCompletionsToCerebrasEndpoint_When_ProviderIsSelected()
    {
        RecordingHandler handler = new("""
            {
              "id": "resp_cerebras",
              "choices": [
                {
                  "message": {
                    "content": "Hello from Cerebras."
                  }
                }
              ]
            }
            """);
        HttpClient httpClient = new(handler);
        OpenAiCompatibleConversationProviderClient sut = CreateSut(httpClient);

        ConversationProviderPayload payload = await sut.SendAsync(
            new ConversationProviderRequest(
                new AgentProviderProfile(ProviderKind.Cerebras, null),
                "cerebras-key",
                "llama3.1-8b",
                [
                    ConversationRequestMessage.User("Say hello.")
                ],
                "You are helpful.",
                []),
            CancellationToken.None);

        handler.RequestUri.Should().Be(new Uri("https://api.cerebras.ai/v1/chat/completions"));
        handler.AuthorizationHeader.Should().Be("Bearer cerebras-key");
        handler.RequestBody.Should().Contain("\"model\":\"llama3.1-8b\"");
        payload.ProviderKind.Should().Be(ProviderKind.Cerebras);
        payload.ResponseId.Should().Be("req_789");
    }

    [Fact]
    public async Task SendAsync_Should_PostChatCompletionsToGroqEndpoint_When_ProviderIsSelected()
    {
        RecordingHandler handler = new("""
            {
              "id": "resp_groq",
              "choices": [
                {
                  "message": {
                    "content": "Hello from Groq."
                  }
                }
              ]
            }
            """);
        HttpClient httpClient = new(handler);
        OpenAiCompatibleConversationProviderClient sut = CreateSut(httpClient);

        ConversationProviderPayload payload = await sut.SendAsync(
            new ConversationProviderRequest(
                new AgentProviderProfile(ProviderKind.Groq, null),
                "groq-key",
                "llama-3.3-70b-versatile",
                [
                    ConversationRequestMessage.User("Say hello.")
                ],
                "You are helpful.",
                []),
            CancellationToken.None);

        handler.RequestUri.Should().Be(new Uri("https://api.groq.com/openai/v1/chat/completions"));
        handler.AuthorizationHeader.Should().Be("Bearer groq-key");
        handler.RequestBody.Should().Contain("\"model\":\"llama-3.3-70b-versatile\"");
        payload.ProviderKind.Should().Be(ProviderKind.Groq);
        payload.ResponseId.Should().Be("req_789");
    }

    [Fact]
    public async Task SendAsync_Should_PostChatCompletionsToDeepSeekEndpoint_When_ProviderIsSelected()
    {
        RecordingHandler handler = new("""
            {
              "id": "resp_deepseek",
              "choices": [
                {
                  "message": {
                    "content": "Hello from DeepSeek."
                  }
                }
              ]
            }
            """);
        HttpClient httpClient = new(handler);
        OpenAiCompatibleConversationProviderClient sut = CreateSut(httpClient);

        ConversationProviderPayload payload = await sut.SendAsync(
            new ConversationProviderRequest(
                new AgentProviderProfile(ProviderKind.DeepSeek, null),
                "deepseek-key",
                "deepseek-chat",
                [
                    ConversationRequestMessage.User("Say hello.")
                ],
                "You are helpful.",
                []),
            CancellationToken.None);

        handler.RequestUri.Should().Be(new Uri("https://api.deepseek.com/v1/chat/completions"));
        handler.AuthorizationHeader.Should().Be("Bearer deepseek-key");
        handler.RequestBody.Should().Contain("\"model\":\"deepseek-chat\"");
        payload.ProviderKind.Should().Be(ProviderKind.DeepSeek);
        payload.ResponseId.Should().Be("req_789");
    }

    [Fact]
    public async Task SendAsync_Should_PostChatCompletionsToOpenCodeZenEndpoint_When_ProviderIsSelected()
    {
        RecordingHandler handler = new("""
            {
              "id": "resp_opencode_zen",
              "choices": [
                {
                  "message": {
                    "content": "Hello from OpenCode Zen."
                  }
                }
              ]
            }
            """);
        HttpClient httpClient = new(handler);
        OpenAiCompatibleConversationProviderClient sut = CreateSut(httpClient);

        ConversationProviderPayload payload = await sut.SendAsync(
            new ConversationProviderRequest(
                new AgentProviderProfile(ProviderKind.OpenCodeZen, null),
                "opencode-zen-key",
                "qwen3.6-plus",
                [
                    ConversationRequestMessage.User("Say hello.")
                ],
                "You are helpful.",
                []),
            CancellationToken.None);

        handler.RequestUri.Should().Be(new Uri("https://opencode.ai/zen/v1/chat/completions"));
        handler.AuthorizationHeader.Should().Be("Bearer opencode-zen-key");
        handler.RequestBody.Should().Contain("\"model\":\"qwen3.6-plus\"");
        payload.ProviderKind.Should().Be(ProviderKind.OpenCodeZen);
        payload.ResponseId.Should().Be("req_789");
    }

    [Fact]
    public async Task SendAsync_Should_PostResponsesRequestToOpenCodeZenEndpoint_When_GptModelIsSelected()
    {
        RecordingHandler handler = new("""
            {
              "id": "resp_opencode_zen_gpt",
              "output": [
                {
                  "type": "message",
                  "content": [
                    { "type": "output_text", "text": "Hello from OpenCode Zen responses." }
                  ]
                }
              ],
              "usage": {
                "input_tokens": 4,
                "output_tokens": 2,
                "total_tokens": 6
              }
            }
            """);
        HttpClient httpClient = new(handler);
        OpenAiCompatibleConversationProviderClient sut = CreateSut(httpClient);

        ConversationProviderPayload payload = await sut.SendAsync(
            new ConversationProviderRequest(
                new AgentProviderProfile(ProviderKind.OpenCodeZen, null),
                "opencode-zen-key",
                "gpt-5.2-codex",
                [
                    ConversationRequestMessage.User("Say hello.")
                ],
                "You are helpful.",
                []),
            CancellationToken.None);

        handler.RequestUri.Should().Be(new Uri("https://opencode.ai/zen/v1/responses"));
        handler.AuthorizationHeader.Should().Be("Bearer opencode-zen-key");

        using JsonDocument requestDocument = JsonDocument.Parse(handler.RequestBody!);
        requestDocument.RootElement.GetProperty("model").GetString().Should().Be("gpt-5.2-codex");
        requestDocument.RootElement.GetProperty("stream").GetBoolean().Should().BeTrue();

        payload.ProviderKind.Should().Be(ProviderKind.OpenCodeZen);
        OpenAiConversationResponseMapper mapper = new();
        ConversationResponse response = mapper.Map(payload);
        response.AssistantMessage.Should().Be("Hello from OpenCode Zen responses.");
        response.PromptTokens.Should().Be(4);
        response.CompletionTokens.Should().Be(2);
        response.TotalTokens.Should().Be(6);
    }

    [Fact]
    public async Task SendAsync_Should_PostMessagesRequestToOpenCodeZenEndpoint_When_ClaudeModelIsSelected()
    {
        RecordingHandler handler = new("""
            {
              "id": "msg_opencode_zen_claude",
              "type": "message",
              "role": "assistant",
              "content": [
                { "type": "text", "text": "Hello from OpenCode Zen messages." }
              ],
              "stop_reason": "end_turn",
              "usage": {
                "input_tokens": 6,
                "output_tokens": 3
              }
            }
            """, responseIdHeaderName: "request-id");
        HttpClient httpClient = new(handler);
        OpenAiCompatibleConversationProviderClient sut = CreateSut(httpClient);

        ConversationProviderPayload payload = await sut.SendAsync(
            new ConversationProviderRequest(
                new AgentProviderProfile(ProviderKind.OpenCodeZen, null),
                "opencode-zen-key",
                "claude-sonnet-4-5",
                [
                    ConversationRequestMessage.User("Say hello.")
                ],
                "You are helpful.",
                []),
            CancellationToken.None);

        handler.RequestUri.Should().Be(new Uri("https://opencode.ai/zen/v1/messages"));
        handler.AuthorizationHeader.Should().Be("Bearer opencode-zen-key");
        handler.AnthropicVersionHeader.Should().Be("2023-06-01");

        using JsonDocument requestDocument = JsonDocument.Parse(handler.RequestBody!);
        requestDocument.RootElement.GetProperty("model").GetString().Should().Be("claude-sonnet-4-5");

        payload.ProviderKind.Should().Be(ProviderKind.OpenCodeZen);
        OpenAiConversationResponseMapper mapper = new();
        ConversationResponse response = mapper.Map(payload);
        response.AssistantMessage.Should().Be("Hello from OpenCode Zen messages.");
        response.PromptTokens.Should().Be(6);
        response.CompletionTokens.Should().Be(3);
        response.TotalTokens.Should().Be(9);
    }

    [Fact]
    public async Task SendAsync_Should_PostChatCompletionsToOllamaEndpoint_When_ProviderIsSelected()
    {
        RecordingHandler handler = new("""
            {
              "id": "resp_ollama",
              "choices": [
                {
                  "message": {
                    "content": "Hello from Ollama."
                  }
                }
              ]
            }
            """);
        HttpClient httpClient = new(handler);
        OpenAiCompatibleConversationProviderClient sut = CreateSut(httpClient);

        ConversationProviderPayload payload = await sut.SendAsync(
            new ConversationProviderRequest(
                new AgentProviderProfile(ProviderKind.Ollama, null),
                "ollama",
                "llama3.2",
                [
                    ConversationRequestMessage.User("Say hello.")
                ],
                "You are helpful.",
                []),
            CancellationToken.None);

        handler.RequestUri.Should().Be(new Uri("http://127.0.0.1:11434/v1/chat/completions"));
        handler.AuthorizationHeader.Should().Be("Bearer ollama");
        handler.RequestBody.Should().Contain("\"model\":\"llama3.2\"");
        payload.ProviderKind.Should().Be(ProviderKind.Ollama);
        payload.ResponseId.Should().Be("req_789");
    }

    [Fact]
    public async Task SendAsync_Should_PostChatCompletionsToLmStudioEndpoint_When_ProviderIsSelected()
    {
        RecordingHandler handler = new("""
            {
              "id": "resp_lm_studio",
              "choices": [
                {
                  "message": {
                    "content": "Hello from LM Studio."
                  }
                }
              ]
            }
            """);
        HttpClient httpClient = new(handler);
        OpenAiCompatibleConversationProviderClient sut = CreateSut(httpClient);

        ConversationProviderPayload payload = await sut.SendAsync(
            new ConversationProviderRequest(
                new AgentProviderProfile(ProviderKind.LmStudio, "http://127.0.0.1:4321/v1"),
                "lm-studio-key",
                "qwen3-8b",
                [
                    ConversationRequestMessage.User("Say hello.")
                ],
                "You are helpful.",
                []),
            CancellationToken.None);

        handler.RequestUri.Should().Be(new Uri("http://127.0.0.1:4321/v1/chat/completions"));
        handler.AuthorizationHeader.Should().Be("Bearer lm-studio-key");
        handler.RequestBody.Should().Contain("\"model\":\"qwen3-8b\"");
        payload.ProviderKind.Should().Be(ProviderKind.LmStudio);
        payload.ResponseId.Should().Be("req_789");
    }

    [Fact]
    public async Task SendAsync_Should_PostChatRequestToOllamaCloudEndpoint_When_ProviderIsSelected()
    {
        RecordingHandler handler = new("""
            {
              "created_at": "2026-05-07T00:00:00Z",
              "message": {
                "role": "assistant",
                "content": "Hello from Ollama Cloud."
              },
              "done": true,
              "prompt_eval_count": 10,
              "eval_count": 5
            }
            """);
        HttpClient httpClient = new(handler);
        OpenAiCompatibleConversationProviderClient sut = CreateSut(httpClient);

        ConversationProviderPayload payload = await sut.SendAsync(
            new ConversationProviderRequest(
                new AgentProviderProfile(ProviderKind.OllamaCloud, null),
                "ollama-cloud-key",
                "gpt-oss:20b",
                [
                    ConversationRequestMessage.User("Say hello.")
                ],
                "You are helpful.",
                []),
            CancellationToken.None);

        handler.RequestUri.Should().Be(new Uri("https://ollama.com/api/chat"));
        handler.AuthorizationHeader.Should().Be("Bearer ollama-cloud-key");
        handler.RequestBody.Should().Contain("\"model\":\"gpt-oss:20b\"");
        handler.RequestBody.Should().Contain("\"stream\":false");
        payload.ProviderKind.Should().Be(ProviderKind.OllamaCloud);

        OpenAiConversationResponseMapper mapper = new();
        ConversationResponse response = mapper.Map(payload);
        response.AssistantMessage.Should().Be("Hello from Ollama Cloud.");
        response.PromptTokens.Should().Be(10);
        response.CompletionTokens.Should().Be(5);
        response.TotalTokens.Should().Be(15);
    }

    [Fact]
    public async Task SendAsync_Should_PostChatCompletionsToAnthropicEndpoint_When_AnthropicProviderIsSelected()
    {
        RecordingHandler handler = new("""
            {
              "id": "resp_5",
              "choices": [
                {
                  "message": {
                    "content": "Hello from Claude."
                  }
                }
              ]
            }
            """, responseIdHeaderName: "request-id");
        HttpClient httpClient = new(handler);
        OpenAiCompatibleConversationProviderClient sut = CreateSut(httpClient);

        ConversationProviderPayload payload = await sut.SendAsync(
            new ConversationProviderRequest(
                new AgentProviderProfile(ProviderKind.Anthropic, null),
                "test-key",
                "claude-sonnet-4-6",
                [
                    ConversationRequestMessage.User("Say hello.")
                ],
                "You are helpful.",
                []),
            CancellationToken.None);

        handler.RequestUri.Should().Be(new Uri("https://api.anthropic.com/v1/chat/completions"));
        handler.AuthorizationHeader.Should().Be("Bearer test-key");
        handler.RequestBody.Should().Contain("\"model\":\"claude-sonnet-4-6\"");
        payload.ProviderKind.Should().Be(ProviderKind.Anthropic);
        payload.ResponseId.Should().Be("req_789");
    }

    [Fact]
    public async Task SendAsync_Should_RunClaudeCodeCli_When_AnthropicClaudeAccountProviderIsSelected()
    {
        ProcessExecutionRequest? capturedRequest = null;
        Mock<IProcessRunner> processRunner = new(MockBehavior.Strict);
        processRunner
            .Setup(runner => runner.RunAsync(
                It.IsAny<ProcessExecutionRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<ProcessExecutionRequest, CancellationToken>((request, _) => capturedRequest = request)
            .ReturnsAsync(new ProcessExecutionResult(0, """
            {
              "session_id": "session-account",
              "is_error": false,
              "result": "{\"message\":\"I can help.\",\"tool_calls\":[{\"name\":\"file_read\",\"arguments_json\":\"{\\\"path\\\":\\\"README.md\\\"}\"}],\"reasoning_summary\":null}",
              "usage": {
                "input_tokens": 12,
                "output_tokens": 7,
                "cache_read_input_tokens": 3
              }
            }
            """, string.Empty));
        OpenAiCompatibleConversationProviderClient sut = new(
            new HttpClient(new RecordingHandler("{}")),
            NullLogger<OpenAiCompatibleConversationProviderClient>.Instance,
            processRunner: processRunner.Object);

        ConversationProviderPayload payload = await sut.SendAsync(
            new ConversationProviderRequest(
                new AgentProviderProfile(ProviderKind.AnthropicClaudeAccount, null),
                "stored-credentials",
                "claude-sonnet-4-6",
                [
                    ConversationRequestMessage.User("Read the README.")
                ],
                "You are helpful.",
                [CreateToolDefinition("file_read")],
                ReasoningEffort: null,
                ThinkingMode: "on"),
            CancellationToken.None);

        capturedRequest.Should().NotBeNull();
        capturedRequest!.FileName.Should().Be("claude");
        capturedRequest.Arguments.Should().ContainInOrder("--model", "claude-sonnet-4-6");
        capturedRequest.StandardInput.Should().Contain("\"name\":\"file_read\"");
        payload.ProviderKind.Should().Be(ProviderKind.AnthropicClaudeAccount);

        OpenAiConversationResponseMapper mapper = new();
        ConversationResponse response = mapper.Map(payload);
        response.AssistantMessage.Should().Be("I can help.");
        response.ResponseId.Should().Be("session-account");
        response.PromptTokens.Should().Be(15);
        response.CompletionTokens.Should().Be(7);
        response.CachedPromptTokens.Should().Be(3);
        response.ToolCalls.Should().ContainSingle();
        response.ToolCalls[0].Name.Should().Be("file_read");
        response.ToolCalls[0].ArgumentsJson.Should().Be("""{"path":"README.md"}""");
        processRunner.VerifyAll();
    }

    [Fact]
    public async Task SendAsync_Should_PostChatCompletionsWithCopilotHeaders_When_GitHubCopilotProviderIsSelected()
    {
        RecordingHandler handler = new("""
            {
              "id": "resp_copilot",
              "choices": [
                {
                  "message": {
                    "content": "Hello from Copilot."
                  }
                }
              ]
            }
            """);
        HttpClient httpClient = new(handler);
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
        OpenAiCompatibleConversationProviderClient sut = CreateSut(httpClient, credentialService.Object);

        ConversationProviderPayload payload = await sut.SendAsync(
            new ConversationProviderRequest(
                new AgentProviderProfile(ProviderKind.GitHubCopilot, null),
                "stored-credentials",
                "gpt-5",
                [
                    ConversationRequestMessage.User(
                        "Describe this.",
                        [new ConversationAttachment("screen.png", "image/png", "aW1n")])
                ],
                "You are helpful.",
                [CreateToolDefinition("file_read")]),
            CancellationToken.None);

        handler.RequestUri.Should().Be(new Uri("https://api.individual.githubcopilot.com/chat/completions"));
        handler.RequestMethod.Should().Be(HttpMethod.Post);
        handler.AuthorizationHeader.Should().Be("Bearer copilot-token");
        handler.CopilotIntegrationIdHeader.Should().Be("vscode-chat");
        handler.CopilotInitiatorHeader.Should().Be("user");
        handler.CopilotIntentHeader.Should().Be("conversation-edits");
        handler.CopilotVisionHeader.Should().Be("true");
        handler.RequestBody.Should().Contain("\"model\":\"gpt-5\"");
        handler.RequestBody.Should().Contain("\"image_url\"");
        payload.ProviderKind.Should().Be(ProviderKind.GitHubCopilot);
        payload.ResponseId.Should().Be("req_789");
        credentialService.VerifyAll();
    }



    [Fact]
    public async Task SendAsync_Should_SerializeProviderReasoningEffort_When_ThinkingIsOn()
    {
        RecordingHandler handler = new("""
            {
              "id": "resp_reasoning",
              "choices": [
                {
                  "message": {
                    "content": "Done."
                  }
                }
              ]
            }
            """);
        HttpClient httpClient = new(handler);
        OpenAiCompatibleConversationProviderClient sut = CreateSut(httpClient);

        await sut.SendAsync(
            new ConversationProviderRequest(
                new AgentProviderProfile(ProviderKind.OpenAi, null),
                "test-key",
                "gpt-5.4",
                [ConversationRequestMessage.User("Think carefully.")],
                "You are helpful.",
                [],
                ReasoningEffort: "high",
                ThinkingMode: "on"),
            CancellationToken.None);

        handler.RequestBody.Should().Contain("\"reasoning_effort\":\"high\"");
    }

    [Fact]
    public async Task SendAsync_Should_SerializeChatCompletionUserAttachments_AsContentParts()
    {
        RecordingHandler handler = new("""
            {
              "id": "resp_attachments",
              "choices": [
                {
                  "message": {
                    "content": "I can see them."
                  }
                }
              ]
            }
            """);
        HttpClient httpClient = new(handler);
        OpenAiCompatibleConversationProviderClient sut = CreateSut(httpClient);

        await sut.SendAsync(
            new ConversationProviderRequest(
                new AgentProviderProfile(ProviderKind.OpenAiCompatible, "http://127.0.0.1:1234/v1"),
                "test-key",
                "gpt-4.1",
                [
                    ConversationRequestMessage.User(
                        "Review these.",
                        [
                            new ConversationAttachment("screenshot.png", "image/png", "aW1hZ2U="),
                            new ConversationAttachment("notes.txt", "text/plain", "bm90ZXM=", "hello notes")
                        ])
                ],
                "You are helpful.",
                []),
            CancellationToken.None);

        using JsonDocument requestDocument = JsonDocument.Parse(handler.RequestBody!);
        JsonElement content = requestDocument.RootElement
            .GetProperty("messages")[1]
            .GetProperty("content");

        content.ValueKind.Should().Be(JsonValueKind.Array);
        content[0].GetProperty("type").GetString().Should().Be("text");
        content[0].GetProperty("text").GetString().Should().Be("Review these.");
        content[1].GetProperty("type").GetString().Should().Be("image_url");
        content[1].GetProperty("image_url").GetProperty("url").GetString()
            .Should()
            .StartWith("data:image/png;base64,");
        content[2].GetProperty("type").GetString().Should().Be("text");
        content[2].GetProperty("text").GetString().Should().Contain("Attached file: notes.txt");
        content[2].GetProperty("text").GetString().Should().Contain("hello notes");
    }

    [Fact]
    public async Task SendAsync_Should_PostResponsesRequestWithAccountHeaders_When_OpenAiChatGptAccountProviderIsSelected()
    {
        RecordingHandler handler = new("""
            {
              "id": "resp_account",
              "output": [
                {
                  "type": "message",
                  "content": [
                    { "type": "output_text", "text": "Done." }
                  ]
                }
              ]
            }
            """);
        HttpClient httpClient = new(handler);
        Mock<IOpenAiChatGptAccountCredentialService> credentialService = new(MockBehavior.Strict);
        credentialService
            .Setup(service => service.ResolveAsync(
                "stored-credentials",
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpenAiChatGptAccountResolvedCredential("access-token", "acct_123"));
        OpenAiCompatibleConversationProviderClient sut = CreateSut(httpClient, credentialService.Object);

        ConversationProviderPayload payload = await sut.SendAsync(
            new ConversationProviderRequest(
                new AgentProviderProfile(ProviderKind.OpenAiChatGptAccount, null),
                "stored-credentials",
                "gpt-5.3-codex",
                [
                    ConversationRequestMessage.User("Read the file."),
                    ConversationRequestMessage.AssistantToolCalls([
                        new ConversationToolCall("call_1", "file_read", """{"path":"README.md"}""")
                    ]),
                    ConversationRequestMessage.ToolResult("call_1", """{"content":"hello"}""")
                ],
                "You are helpful.",
                [CreateToolDefinition("file_read")],
                ReasoningEffort: "high",
                ThinkingMode: "on"),
            CancellationToken.None);

        handler.RequestUri.Should().Be(new Uri("https://chatgpt.com/backend-api/codex/responses"));
        handler.RequestMethod.Should().Be(HttpMethod.Post);
        handler.AuthorizationHeader.Should().Be("Bearer access-token");
        handler.AccountIdHeader.Should().Be("acct_123");
        handler.OriginatorHeader.Should().Be("stemcode");
        handler.SessionIdHeader.Should().NotBeNullOrWhiteSpace();
        payload.ProviderKind.Should().Be(ProviderKind.OpenAiChatGptAccount);

        using JsonDocument requestDocument = JsonDocument.Parse(handler.RequestBody!);
        JsonElement root = requestDocument.RootElement;
        root.GetProperty("model").GetString().Should().Be("gpt-5.3-codex");
        root.GetProperty("stream").GetBoolean().Should().BeTrue();
        root.GetProperty("store").GetBoolean().Should().BeFalse();
        root.GetProperty("instructions").GetString().Should().Be("You are helpful.");
        root.GetProperty("reasoning").GetProperty("effort").GetString().Should().Be("high");
        root.GetProperty("input")[0].GetProperty("content")[0].GetProperty("type").GetString().Should().Be("input_text");
        root.GetProperty("input")[1].GetProperty("type").GetString().Should().Be("function_call");
        root.GetProperty("input")[2].GetProperty("type").GetString().Should().Be("function_call_output");
        root.GetProperty("tools")[0].GetProperty("strict").GetBoolean().Should().BeTrue();
        credentialService.VerifyAll();
    }

    [Fact]
    public async Task SendAsync_Should_SerializeResponsesUserAttachments_AsInputParts()
    {
        RecordingHandler handler = new("""
            {
              "id": "resp_account_attachments",
              "output": [
                {
                  "type": "message",
                  "content": [
                    { "type": "output_text", "text": "Done." }
                  ]
                }
              ]
            }
            """);
        HttpClient httpClient = new(handler);
        Mock<IOpenAiChatGptAccountCredentialService> credentialService = new(MockBehavior.Strict);
        credentialService
            .Setup(service => service.ResolveAsync(
                "stored-credentials",
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpenAiChatGptAccountResolvedCredential("access-token", null));
        OpenAiCompatibleConversationProviderClient sut = CreateSut(httpClient, credentialService.Object);

        await sut.SendAsync(
            new ConversationProviderRequest(
                new AgentProviderProfile(ProviderKind.OpenAiChatGptAccount, null),
                "stored-credentials",
                "gpt-5.3-codex",
                [
                    ConversationRequestMessage.User(
                        "Inspect this image.",
                        [new ConversationAttachment("clipboard.png", "image/png", "aW1hZ2U=")])
                ],
                "You are helpful.",
                []),
            CancellationToken.None);

        using JsonDocument requestDocument = JsonDocument.Parse(handler.RequestBody!);
        JsonElement content = requestDocument.RootElement
            .GetProperty("input")[0]
            .GetProperty("content");

        content[0].GetProperty("type").GetString().Should().Be("input_text");
        content[0].GetProperty("text").GetString().Should().Be("Inspect this image.");
        content[1].GetProperty("type").GetString().Should().Be("input_image");
        content[1].GetProperty("image_url").GetString()
            .Should()
            .StartWith("data:image/png;base64,");
        credentialService.VerifyAll();
    }

    [Fact]
    public async Task SendAsync_Should_ReturnResponsesPayload_When_OpenAiChatGptAccountReturnsEventStream()
    {
        RecordingHandler handler = new("""
            event: response.created
            data: {"type":"response.created","response":{"id":"resp_stream"}}

            event: response.output_item.done
            data: {"type":"response.output_item.done","output_index":0,"item":{"type":"reasoning","summary":[{"type":"summary_text","text":"I can answer directly from the prompt."}],"encrypted_content":"sealed"}}

            event: response.output_item.done
            data: {"type":"response.output_item.done","output_index":1,"item":{"type":"message","content":[{"type":"output_text","text":"Done from stream."}]}}

            event: response.completed
            data: {"type":"response.completed","response":{"id":"resp_stream","error":null,"output":[],"usage":{"input_tokens":5,"output_tokens":3,"total_tokens":8}}}

            data: [DONE]

            """);
        HttpClient httpClient = new(handler);
        Mock<IOpenAiChatGptAccountCredentialService> credentialService = new(MockBehavior.Strict);
        credentialService
            .Setup(service => service.ResolveAsync(
                "stored-credentials",
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpenAiChatGptAccountResolvedCredential("access-token", null));
        OpenAiCompatibleConversationProviderClient sut = CreateSut(httpClient, credentialService.Object);

        ConversationProviderPayload payload = await sut.SendAsync(
            new ConversationProviderRequest(
                new AgentProviderProfile(ProviderKind.OpenAiChatGptAccount, null),
                "stored-credentials",
                "gpt-5.3-codex",
                [ConversationRequestMessage.User("Say done.")],
                "You are helpful.",
                []),
            CancellationToken.None);

        payload.RawContent.Should().NotContain("data:");

        OpenAiConversationResponseMapper mapper = new();
        ConversationResponse response = mapper.Map(payload);
        response.AssistantMessage.Should().Be("Done from stream.");
        response.ReasoningDetailsJson.Should().Contain("I can answer directly from the prompt.");
        response.ReasoningDetailsJson.Should().Contain("sealed");
        response.ResponseId.Should().Be("resp_stream");
        response.PromptTokens.Should().Be(5);
        response.CompletionTokens.Should().Be(3);
        response.TotalTokens.Should().Be(8);
        credentialService.VerifyAll();
    }

    [Fact]
    public async Task SendAsync_Should_UseTextDeltas_When_OpenAiChatGptAccountCompletedEventHasNoOutputItems()
    {
        RecordingHandler handler = new("""
            event: response.created
            data: {"type":"response.created","response":{"id":"resp_delta"}}

            event: response.output_text.delta
            data: {"type":"response.output_text.delta","response_id":"resp_delta","delta":"Hello "}

            event: response.output_text.delta
            data: {"type":"response.output_text.delta","response_id":"resp_delta","delta":"from deltas."}

            event: response.completed
            data: {"type":"response.completed","response":{"id":"resp_delta","error":null,"output":[]}}

            data: [DONE]

            """);
        HttpClient httpClient = new(handler);
        Mock<IOpenAiChatGptAccountCredentialService> credentialService = new(MockBehavior.Strict);
        credentialService
            .Setup(service => service.ResolveAsync(
                "stored-credentials",
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpenAiChatGptAccountResolvedCredential("access-token", null));
        OpenAiCompatibleConversationProviderClient sut = CreateSut(httpClient, credentialService.Object);

        ConversationProviderPayload payload = await sut.SendAsync(
            new ConversationProviderRequest(
                new AgentProviderProfile(ProviderKind.OpenAiChatGptAccount, null),
                "stored-credentials",
                "gpt-5.3-codex",
                [ConversationRequestMessage.User("Say hello.")],
                "You are helpful.",
                []),
            CancellationToken.None);

        OpenAiConversationResponseMapper mapper = new();
        ConversationResponse response = mapper.Map(payload);
        response.AssistantMessage.Should().Be("Hello from deltas.");
        response.ResponseId.Should().Be("resp_delta");
        credentialService.VerifyAll();
    }

    [Fact]
    public async Task SendAsync_Should_PreserveStructuredToolFeedbackJson_When_ToolMessagesContainStatusMetadata()
    {
        RecordingHandler handler = new("""
            {
              "id": "resp_3",
              "choices": [
                {
                  "message": {
                    "content": "Adjusted after tool feedback."
                  }
                }
              ]
            }
            """);
        HttpClient httpClient = new(handler);
        OpenAiCompatibleConversationProviderClient sut = CreateSut(httpClient);

        string toolFeedbackJson = """
            {
              "ToolName": "shell_command",
              "Status": "ExecutionError",
              "IsSuccess": false,
              "Message": "The command exited with code 1.",
              "Data": {
                "Code": "exit_code_1",
                "Message": "The command exited with code 1."
              }
            }
            """;

        await sut.SendAsync(
            new ConversationProviderRequest(
                new AgentProviderProfile(ProviderKind.OpenAiCompatible, "http://127.0.0.1:1234/v1"),
                "test-key",
                "gpt-4.1",
                [
                    ConversationRequestMessage.User("Run tests and fix failures."),
                    ConversationRequestMessage.ToolResult("call_2", toolFeedbackJson)
                ],
                "You are helpful.",
                [CreateToolDefinition("shell_command")]),
            CancellationToken.None);

        using JsonDocument requestDocument = JsonDocument.Parse(handler.RequestBody!);
        JsonElement toolMessage = requestDocument.RootElement
            .GetProperty("messages")[2];

        toolMessage.GetProperty("tool_call_id").GetString().Should().Be("call_2");

        using JsonDocument toolContentDocument = JsonDocument.Parse(toolMessage.GetProperty("content").GetString()!);
        JsonElement toolContent = toolContentDocument.RootElement;
        toolContent.GetProperty("ToolName").GetString().Should().Be("shell_command");
        toolContent.GetProperty("Status").GetString().Should().Be("ExecutionError");
        toolContent.GetProperty("IsSuccess").GetBoolean().Should().BeFalse();
    }

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task SendAsync_Should_RetryRetryableHttpStatusCodes(int statusCode)
    {
        SequenceHandler handler = new(
            CreateResponse((HttpStatusCode)statusCode, """{ "error": "retry later" }"""),
            CreateResponse(HttpStatusCode.OK, """
                {
                  "id": "resp_retry",
                  "choices": [
                    {
                      "message": {
                        "content": "Recovered."
                      }
                    }
                  ]
                }
                """));
        List<TimeSpan> delays = [];
        HttpClient httpClient = new(handler);
        OpenAiCompatibleConversationProviderClient sut = CreateSut(
            httpClient,
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            },
            () => 0.5d);

        ConversationProviderPayload payload = await sut.SendAsync(
            CreateRequest(),
            CancellationToken.None);

        payload.RawContent.Should().Contain("Recovered.");
        payload.RetryCount.Should().Be(1);
        handler.RequestBodies.Should().HaveCount(2);
        delays.Should().Equal([TimeSpan.FromMilliseconds(125)]);
    }

    [Fact]
    public async Task SendAsync_Should_UseExponentialBackoffWithJitter_When_MultipleRetriesAreNeeded()
    {
        SequenceHandler handler = new(
            CreateResponse(HttpStatusCode.TooManyRequests, """{ "error": "rate limited" }"""),
            CreateResponse(HttpStatusCode.InternalServerError, """{ "error": "temporary" }"""),
            CreateResponse(HttpStatusCode.OK, """
                {
                  "id": "resp_retry",
                  "choices": [
                    {
                      "message": {
                        "content": "Recovered."
                      }
                    }
                  ]
                }
                """));
        List<TimeSpan> delays = [];
        HttpClient httpClient = new(handler);
        OpenAiCompatibleConversationProviderClient sut = CreateSut(
            httpClient,
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            },
            () => 0.5d);

        ConversationProviderPayload payload = await sut.SendAsync(
            CreateRequest(),
            CancellationToken.None);

        payload.RetryCount.Should().Be(2);
        handler.RequestBodies.Should().HaveCount(3);
        delays.Should().Equal([
            TimeSpan.FromMilliseconds(125),
            TimeSpan.FromMilliseconds(250)
        ]);
    }

    [Fact]
    public async Task SendAsync_Should_NotRetryNonRetryableHttpStatusCodes()
    {
        SequenceHandler handler = new(
            CreateResponse(HttpStatusCode.BadRequest, """{ "error": "bad request" }"""),
            CreateResponse(HttpStatusCode.OK, """
                {
                  "id": "resp_not_used",
                  "choices": [
                    {
                      "message": {
                        "content": "Should not be used."
                      }
                    }
                  ]
                }
                """));
        HttpClient httpClient = new(handler);
        OpenAiCompatibleConversationProviderClient sut = CreateSut(
            httpClient,
            (_, _) => Task.CompletedTask,
            () => 0.5d);

        Func<Task> action = async () => await sut.SendAsync(
            CreateRequest(),
            CancellationToken.None);

        await action.Should()
            .ThrowAsync<ConversationProviderException>()
            .WithMessage("*HTTP 400*");
        handler.RequestBodies.Should().ContainSingle();
    }

    [Fact]
    public async Task SendAsync_Should_RetryAndReportProgress_When_HostCannotBeResolved()
    {
        ThrowingSequenceHandler handler = new(
            new HttpRequestException(
                "No such host is known.",
                new SocketException((int)SocketError.HostNotFound)),
            CreateResponse(HttpStatusCode.OK, """
                {
                  "id": "resp_recovered",
                  "choices": [
                    {
                      "message": {
                        "content": "Recovered after DNS retry."
                      }
                    }
                  ]
                }
                """));
        List<ProviderRetryProgress> retries = [];
        HttpClient httpClient = new(handler);
        OpenAiCompatibleConversationProviderClient sut = CreateSut(
            httpClient,
            (_, _) => Task.CompletedTask,
            () => 0.5d);

        ConversationProviderPayload payload = await sut.SendAsync(
            CreateRequest() with
            {
                OnProviderRetryAsync = (progress, _) =>
                {
                    retries.Add(progress);
                    return Task.CompletedTask;
                }
            },
            CancellationToken.None);

        payload.RawContent.Should().Contain("Recovered after DNS retry.");
        payload.RetryCount.Should().Be(1);
        handler.RequestCount.Should().Be(2);
        retries.Should().ContainSingle();
        retries[0].Attempt.Should().Be(1);
        retries[0].MaxAttempts.Should().Be(10);
        retries[0].Reason.Should().Be("host not found");
    }

    [Fact]
    public async Task SendAsync_Should_ReportRetryProgress_When_RetryableHttpStatusReturned()
    {
        SequenceHandler handler = new(
            CreateResponse(HttpStatusCode.ServiceUnavailable, """{ "error": "later" }"""),
            CreateResponse(HttpStatusCode.OK, """
                {
                  "id": "resp_ok",
                  "choices": [
                    {
                      "message": {
                        "content": "Recovered."
                      }
                    }
                  ]
                }
                """));
        List<ProviderRetryProgress> retries = [];
        HttpClient httpClient = new(handler);
        OpenAiCompatibleConversationProviderClient sut = CreateSut(
            httpClient,
            (_, _) => Task.CompletedTask,
            () => 0.5d);

        await sut.SendAsync(
            CreateRequest() with
            {
                OnProviderRetryAsync = (progress, _) =>
                {
                    retries.Add(progress);
                    return Task.CompletedTask;
                }
            },
            CancellationToken.None);

        retries.Should().ContainSingle();
        retries[0].Attempt.Should().Be(1);
        retries[0].MaxAttempts.Should().Be(10);
        retries[0].Reason.Should().Be("HTTP 503");
    }

    private static ToolDefinition CreateToolDefinition(string name)
    {
        using JsonDocument schemaDocument = JsonDocument.Parse(
            """{ "type": "object", "properties": {}, "additionalProperties": false }""");

        return new ToolDefinition(
            name,
            $"Description for {name}",
            schemaDocument.RootElement.Clone());
    }

    private static OpenAiCompatibleConversationProviderClient CreateSut(HttpClient httpClient)
    {
        return CreateSut(httpClient, delayAsync: null, nextJitter: null);
    }

    private static OpenAiCompatibleConversationProviderClient CreateSut(
        HttpClient httpClient,
        IOpenAiChatGptAccountCredentialService credentialService)
    {
        return new OpenAiCompatibleConversationProviderClient(
            httpClient,
            NullLogger<OpenAiCompatibleConversationProviderClient>.Instance,
            delayAsync: null,
            nextJitter: null,
            openAiChatGptAccountCredentialService: credentialService);
    }

    private static OpenAiCompatibleConversationProviderClient CreateSut(
        HttpClient httpClient,
        IGitHubCopilotCredentialService credentialService)
    {
        return new OpenAiCompatibleConversationProviderClient(
            httpClient,
            NullLogger<OpenAiCompatibleConversationProviderClient>.Instance,
            delayAsync: null,
            nextJitter: null,
            gitHubCopilotCredentialService: credentialService);
    }

    private static OpenAiCompatibleConversationProviderClient CreateSut(
        HttpClient httpClient,
        IStemCodeEnterpriseCredentialService credentialService)
    {
        return CreateSut(httpClient, credentialService, providerRequestProjectHeaderProvider: null);
    }

    private static OpenAiCompatibleConversationProviderClient CreateSut(
        HttpClient httpClient,
        IStemCodeEnterpriseCredentialService credentialService,
        ProviderRequestProjectHeaderProvider? providerRequestProjectHeaderProvider)
    {
        return new OpenAiCompatibleConversationProviderClient(
            httpClient,
            NullLogger<OpenAiCompatibleConversationProviderClient>.Instance,
            delayAsync: null,
            nextJitter: null,
            stemCodeEnterpriseCredentialService: credentialService,
            providerRequestProjectHeaderProvider: providerRequestProjectHeaderProvider);
    }


    private static OpenAiCompatibleConversationProviderClient CreateSut(
        HttpClient httpClient,
        Func<TimeSpan, CancellationToken, Task>? delayAsync,
        Func<double>? nextJitter)
    {
        return new OpenAiCompatibleConversationProviderClient(
            httpClient,
            NullLogger<OpenAiCompatibleConversationProviderClient>.Instance,
            delayAsync,
            nextJitter);
    }

    private static ConversationProviderRequest CreateRequest()
    {
        return new ConversationProviderRequest(
            new AgentProviderProfile(ProviderKind.OpenAiCompatible, "http://127.0.0.1:1234/v1"),
            "test-key",
            "gpt-4.1",
            [
                ConversationRequestMessage.User("Retry this request.")
            ],
            "You are helpful.",
            [CreateToolDefinition("file_read")]);
    }

    private static HttpResponseMessage CreateResponse(HttpStatusCode statusCode, string body)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly string _responseBody;
        private readonly string _responseIdHeaderName;

        public RecordingHandler(string responseBody, string responseIdHeaderName = "x-request-id")
        {
            _responseBody = responseBody;
            _responseIdHeaderName = responseIdHeaderName;
        }

        public string? AuthorizationHeader { get; private set; }

        public string? AccountIdHeader { get; private set; }

        public string? OriginatorHeader { get; private set; }

        public string? OpenRouterRefererHeader { get; private set; }

        public string? OpenRouterTitleHeader { get; private set; }

        public string? ProjectHeader { get; private set; }

        public string? KiloCodeEditorHeader { get; private set; }

        public string? KiloCodeUserAgentHeader { get; private set; }

        public string? AnthropicAppHeader { get; private set; }

        public string? AnthropicBetaHeader { get; private set; }

        public string? AnthropicVersionHeader { get; private set; }

        public string? CopilotInitiatorHeader { get; private set; }

        public string? CopilotIntegrationIdHeader { get; private set; }

        public string? CopilotIntentHeader { get; private set; }

        public string? CopilotVisionHeader { get; private set; }

        public string? RequestBody { get; private set; }

        public HttpMethod? RequestMethod { get; private set; }

        public Uri? RequestUri { get; private set; }

        public string? SessionIdHeader { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            RequestMethod = request.Method;
            AuthorizationHeader = request.Headers.Authorization?.ToString();
            AccountIdHeader = request.Headers.TryGetValues("ChatGPT-Account-Id", out IEnumerable<string>? accountIdValues)
                ? accountIdValues.FirstOrDefault()
                : null;
            OriginatorHeader = request.Headers.TryGetValues("originator", out IEnumerable<string>? originatorValues)
                ? originatorValues.FirstOrDefault()
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
            AnthropicVersionHeader = request.Headers.TryGetValues("anthropic-version", out IEnumerable<string>? versionValues)
                ? versionValues.FirstOrDefault()
                : null;
            AnthropicBetaHeader = request.Headers.TryGetValues("anthropic-beta", out IEnumerable<string>? betaValues)
                ? betaValues.FirstOrDefault()
                : null;
            AnthropicAppHeader = request.Headers.TryGetValues("x-app", out IEnumerable<string>? appValues)
                ? appValues.FirstOrDefault()
                : null;
            CopilotIntegrationIdHeader = request.Headers.TryGetValues(
                "Copilot-Integration-Id",
                out IEnumerable<string>? integrationValues)
                    ? integrationValues.FirstOrDefault()
                    : null;
            CopilotInitiatorHeader = request.Headers.TryGetValues("X-Initiator", out IEnumerable<string>? initiatorValues)
                ? initiatorValues.FirstOrDefault()
                : null;
            CopilotIntentHeader = request.Headers.TryGetValues("Openai-Intent", out IEnumerable<string>? intentValues)
                ? intentValues.FirstOrDefault()
                : null;
            CopilotVisionHeader = request.Headers.TryGetValues("Copilot-Vision-Request", out IEnumerable<string>? visionValues)
                ? visionValues.FirstOrDefault()
                : null;
            SessionIdHeader = request.Headers.TryGetValues("session_id", out IEnumerable<string>? sessionIdValues)
                ? sessionIdValues.FirstOrDefault()
                : null;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json")
            };
            response.Headers.Add(_responseIdHeaderName, "req_789");

            return response;
        }
    }

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public SequenceHandler(params HttpResponseMessage[] responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        public List<string?> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBodies.Add(request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken));

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No HTTP response was queued for this request.");
            }

            return _responses.Dequeue();
        }
    }

    private sealed class ThrowingSequenceHandler : HttpMessageHandler
    {
        private readonly Queue<object> _steps;

        public ThrowingSequenceHandler(params object[] steps)
        {
            _steps = new Queue<object>(steps);
        }

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;

            if (_steps.Count == 0)
            {
                throw new InvalidOperationException("No HTTP step was queued for this request.");
            }

            return _steps.Dequeue() switch
            {
                Exception exception => Task.FromException<HttpResponseMessage>(exception),
                HttpResponseMessage response => Task.FromResult(response),
                object step => throw new InvalidOperationException(
                    $"Unsupported queued step type '{step.GetType()}'.")
            };
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
