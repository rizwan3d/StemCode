using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StemCode.Application.Models;
using StemCode.Application.Abstractions;
using StemCode.Domain.Models;
using StemCode.Infrastructure.Conversation;
using StemCode.Infrastructure.GitHub;
using StemCode.Infrastructure.OpenAi;
using StemCode.Infrastructure.Secrets;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace StemCode.Tests.Infrastructure.Conversation;

public sealed class ConversationProviderAdapterTests
{
    [Fact]
    public async Task OpenAiCompatibleAdapter_Should_ApplyOpenRouterHeaders()
    {
        RecordingHandler handler = new(CreateJsonResponse("resp_openrouter", "Hello."));
        OpenAiCompatibleConversationProviderAdapter sut = new(
            CreateExecutor(handler),
            new ConversationProviderRequestPayloadFactory());

        await sut.SendAsync(
            CreateRequest(ProviderKind.OpenRouter, modelId: "openai/gpt-4o"),
            CancellationToken.None);

        handler.RequestUri.Should().Be(new Uri("https://openrouter.ai/api/v1/chat/completions"));
        handler.RefererHeader.Should().Be("https://github.com/rizwan3d/StemCode");
        handler.TitleHeader.Should().Be("StemCode");
    }

    [Fact]
    public async Task OpenAiChatGptAccountAdapter_Should_RefreshCredentialsAfterUnauthorized()
    {
        SequenceHandler handler = new(
            CreateResponse(HttpStatusCode.Unauthorized, """{"error":"expired"}"""),
            CreateResponse(HttpStatusCode.OK, "data: {\"id\":\"resp_1\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"Hello.\"}]}]}\n\n"));
        Mock<IOpenAiChatGptAccountCredentialService> credentialService = new(MockBehavior.Strict);
        credentialService
            .SetupSequence(service => service.ResolveAsync("stored", It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpenAiChatGptAccountResolvedCredential("expired-token", "account-1"))
            .ReturnsAsync(new OpenAiChatGptAccountResolvedCredential("fresh-token", "account-1"));
        OpenAiChatGptAccountConversationProviderAdapter sut = new(
            CreateExecutor(handler),
            new ConversationProviderRequestPayloadFactory(),
            new ConversationProviderResponseNormalizer(),
            credentialService.Object,
            sessionId: "session-1");

        ConversationProviderPayload payload = await sut.SendAsync(
            CreateRequest(ProviderKind.OpenAiChatGptAccount),
            CancellationToken.None);

        handler.AuthorizationHeaders.Should().Equal("Bearer expired-token", "Bearer fresh-token");
        payload.RawContent.Should().Contain("\"Hello.\"");
        credentialService.Verify(service => service.ResolveAsync("stored", false, It.IsAny<CancellationToken>()), Times.Once);
        credentialService.Verify(service => service.ResolveAsync("stored", true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OpenAiChatGptAccountAdapter_Should_StreamAssistantTextDeltas()
    {
        RecordingHandler handler = new("""
            event: response.created
            data: {"type":"response.created","response":{"id":"resp_stream"}}

            event: response.output_text.delta
            data: {"type":"response.output_text.delta","response_id":"resp_stream","delta":"Hello "}

            event: response.output_text.delta
            data: {"type":"response.output_text.delta","response_id":"resp_stream","delta":"world"}

            event: response.completed
            data: {"type":"response.completed","response":{"id":"resp_stream","error":null,"output":[]}}

            data: [DONE]

            """);
        List<string> chunks = [];
        Mock<IOpenAiChatGptAccountCredentialService> credentialService = new(MockBehavior.Strict);
        credentialService
            .Setup(service => service.ResolveAsync("stored", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpenAiChatGptAccountResolvedCredential("token", "account-1"));
        OpenAiChatGptAccountConversationProviderAdapter sut = new(
            CreateExecutor(handler),
            new ConversationProviderRequestPayloadFactory(),
            new ConversationProviderResponseNormalizer(),
            credentialService.Object,
            sessionId: "session-1");

        ConversationProviderPayload payload = await sut.SendAsync(
            CreateRequest(ProviderKind.OpenAiChatGptAccount) with
            {
                OnAssistantMessageChunkAsync = (text, _) =>
                {
                    chunks.Add(text);
                    return Task.CompletedTask;
                }
            },
            CancellationToken.None);

        chunks.Should().Equal("Hello ", "world");
        payload.AssistantMessageWasStreamed.Should().BeTrue();
        payload.RawContent.Should().Contain("\"Hello world\"");
    }

    [Fact]
    public async Task AnthropicClaudeAccountAdapter_Should_RunClaudeCodeAndNormalizeStructuredResult()
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
              "session_id": "session-1",
              "is_error": false,
              "result": "{\"message\":\"Claude reply.\",\"tool_calls\":[],\"reasoning_summary\":null}",
              "usage": {
                "input_tokens": 10,
                "cache_read_input_tokens": 3,
                "cache_creation_input_tokens": 2,
                "output_tokens": 5
              }
            }
            """, string.Empty));
        AnthropicClaudeAccountConversationProviderAdapter sut = new(processRunner.Object);

        ConversationProviderPayload payload = await sut.SendAsync(
            CreateRequest(ProviderKind.AnthropicClaudeAccount, modelId: "sonnet"),
            CancellationToken.None);

        capturedRequest.Should().NotBeNull();
        capturedRequest!.FileName.Should().Be("claude");
        capturedRequest.Arguments.Should().ContainInOrder("--print", "--output-format", "json");
        capturedRequest.Arguments.Should().ContainInOrder("--model", "sonnet");
        capturedRequest.StandardInput.Should().Contain("\"system_prompt\":\"You are helpful.\"");
        capturedRequest.StandardInput.Should().Contain("\"content\":\"Say hello.\"");
        payload.RawContent.Should().Contain("\"Claude reply.\"");
        payload.RawContent.Should().Contain("\"finish_reason\":\"stop\"");

        OpenAiConversationResponseMapper mapper = new();
        ConversationResponse response = mapper.Map(payload);
        response.AssistantMessage.Should().Be("Claude reply.");
        response.PromptTokens.Should().Be(15);
        response.CompletionTokens.Should().Be(5);
        response.CachedPromptTokens.Should().Be(3);
        processRunner.VerifyAll();
    }

    [Fact]
    public async Task GitHubCopilotAdapter_Should_AddVisionHeaderForImageInput()
    {
        RecordingHandler handler = new(CreateJsonResponse("resp_copilot", "Hello."));
        Mock<IGitHubCopilotCredentialService> credentialService = new(MockBehavior.Strict);
        credentialService
            .Setup(service => service.ResolveAsync("stored", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubCopilotResolvedCredential("copilot-token", null, new Uri("https://api.githubcopilot.com/")));
        GitHubCopilotConversationProviderAdapter sut = new(
            CreateExecutor(handler),
            new ConversationProviderRequestPayloadFactory(),
            credentialService.Object);

        await sut.SendAsync(
            CreateRequest(
                ProviderKind.GitHubCopilot,
                messages:
                [
                    ConversationRequestMessage.User(
                        "Inspect image.",
                        [
                            new ConversationAttachment(
                                "image.png",
                                "image/png",
                                Convert.ToBase64String([1, 2, 3]),
                                textContent: null)
                        ])
                ]),
            CancellationToken.None);

        handler.RequestUri.Should().Be(new Uri("https://api.githubcopilot.com/chat/completions"));
        handler.CopilotVisionHeader.Should().Be("true");
    }

    [Fact]
    public async Task OllamaCloudAdapter_Should_ConvertChatResponseToChatCompletionShape()
    {
        RecordingHandler handler = new("""
            {
              "created_at": "2025-01-01T00:00:00Z",
              "message": {
                "content": "Ollama reply."
              },
              "prompt_eval_count": 12,
              "eval_count": 4
            }
            """);
        OllamaCloudConversationProviderAdapter sut = new(
            CreateExecutor(handler),
            new ConversationProviderRequestPayloadFactory(),
            new ConversationProviderResponseNormalizer());

        ConversationProviderPayload payload = await sut.SendAsync(
            CreateRequest(ProviderKind.OllamaCloud),
            CancellationToken.None);

        handler.RequestUri.Should().Be(new Uri("https://ollama.com/api/chat"));
        payload.RawContent.Should().Contain("\"Ollama reply.\"");
        payload.RawContent.Should().Contain("\"completion_tokens\":4");
    }

    [Fact]
    public async Task OpenCodeZenAdapter_Should_UseMessagesEndpointForClaudeModels()
    {
        RecordingHandler handler = new("""
            {
              "id": "msg_1",
              "content": [
                { "type": "text", "text": "Zen reply." }
              ],
              "stop_reason": "end_turn"
            }
            """);
        OpenCodeZenConversationProviderAdapter sut = new(
            CreateExecutor(handler),
            new ConversationProviderRequestPayloadFactory(),
            new ConversationProviderResponseNormalizer());

        ConversationProviderPayload payload = await sut.SendAsync(
            CreateRequest(ProviderKind.OpenCodeZen, modelId: "claude-sonnet-4-5"),
            CancellationToken.None);

        handler.RequestUri.Should().Be(new Uri("https://opencode.ai/zen/v1/messages"));
        handler.AnthropicVersionHeader.Should().Be("2023-06-01");
        payload.RawContent.Should().Contain("\"Zen reply.\"");
    }

    [Fact]
    public async Task ExecuteAsync_Should_CapServerProvidedRetryDelay()
    {
        HttpResponseMessage rateLimited = CreateResponse(HttpStatusCode.TooManyRequests, "slow down");
        rateLimited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
        SequenceHandler handler = new(
            rateLimited,
            CreateResponse(HttpStatusCode.OK, CreateJsonResponse("resp_capped", "Hello.")));

        List<TimeSpan> delays = [];
        ConversationProviderHttpExecutor executor = new(
            new HttpClient(handler),
            NullLogger.Instance,
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            },
            () => 0d);

        ConversationProviderPayload payload = await executor.ExecuteAsync(
            ProviderKind.OpenRouter,
            () => new HttpRequestMessage(HttpMethod.Post, "https://example.test/v1/chat/completions"),
            CancellationToken.None);

        payload.RetryCount.Should().Be(1);
        delays.Should().ContainSingle();
        delays[0].Should().Be(TimeSpan.FromSeconds(5));
    }

    private static ConversationProviderRequest CreateRequest(
        ProviderKind providerKind,
        string apiKey = "stored",
        string modelId = "gpt-4.1",
        IReadOnlyList<ConversationRequestMessage>? messages = null)
    {
        return new ConversationProviderRequest(
            new AgentProviderProfile(providerKind, providerKind switch
            {
                ProviderKind.OpenRouter => null,
                ProviderKind.OpenAiChatGptAccount => null,
                ProviderKind.AnthropicClaudeAccount => null,
                ProviderKind.GitHubCopilot => null,
                ProviderKind.OllamaCloud => "https://ollama.com/",
                ProviderKind.OpenCodeZen => "https://opencode.ai/api/",
                _ => "http://127.0.0.1:1234/v1"
            }),
            apiKey,
            modelId,
            messages ?? [ConversationRequestMessage.User("Say hello.")],
            "You are helpful.",
            []);
    }

    private static IConversationProviderHttpExecutor CreateExecutor(HttpMessageHandler handler)
    {
        return new ConversationProviderHttpExecutor(
            new HttpClient(handler),
            NullLogger.Instance,
            (_, _) => Task.CompletedTask,
            () => 0d);
    }

    private static string CreateJsonResponse(string id, string content)
    {
        return $$"""
            {
              "id": "{{id}}",
              "choices": [
                {
                  "message": {
                    "content": "{{content}}"
                  }
                }
              ]
            }
            """;
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

        public RecordingHandler(string responseBody)
        {
            _responseBody = responseBody;
        }

        public string? AnthropicVersionHeader { get; private set; }
        public string? CopilotVisionHeader { get; private set; }
        public string? RefererHeader { get; private set; }
        public string? RequestBody { get; private set; }
        public HttpMethod? RequestMethod { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? TitleHeader { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestMethod = request.Method;
            RequestUri = request.RequestUri;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            RefererHeader = request.Headers.TryGetValues("HTTP-Referer", out IEnumerable<string>? refererValues)
                ? refererValues.Single()
                : null;
            TitleHeader = request.Headers.TryGetValues("X-Title", out IEnumerable<string>? titleValues)
                ? titleValues.Single()
                : null;
            CopilotVisionHeader = request.Headers.TryGetValues("Copilot-Vision-Request", out IEnumerable<string>? visionValues)
                ? visionValues.Single()
                : null;
            AnthropicVersionHeader = request.Headers.TryGetValues("anthropic-version", out IEnumerable<string>? anthropicValues)
                ? anthropicValues.Single()
                : null;

            HttpResponseMessage response = CreateResponse(HttpStatusCode.OK, _responseBody);
            response.Headers.Add("x-request-id", "req_789");
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

        public List<string?> AuthorizationHeaders { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            AuthorizationHeaders.Add(request.Headers.Authorization?.ToString());
            HttpResponseMessage response = _responses.Dequeue();
            response.Headers.Add("x-request-id", "req_789");
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task ExecuteAsync_Should_RecordProviderTelemetry_OnSuccess()
    {
        RecordingHandler handler = new(CreateJsonResponse("req_1", "Hi."));
        RecordingProductTelemetry telemetry = new();
        ConversationProviderHttpExecutor executor = new(
            new HttpClient(handler),
            NullLogger.Instance,
            (_, _) => Task.CompletedTask,
            () => 0d,
            TimeProvider.System,
            telemetry);

        ConversationProviderPayload payload = await executor.ExecuteAsync(
            ProviderKind.OpenAiCompatible,
            () => new HttpRequestMessage(HttpMethod.Post, "https://example.test/v1/chat/completions"),
            CancellationToken.None);

        payload.RetryCount.Should().Be(0);
        telemetry.ProviderRequests.Should().ContainSingle();
        (string ProviderName, bool Success, bool Streamed) recorded = telemetry.ProviderRequests[0];
        recorded.ProviderName.Should().Be("open_ai_compatible");
        recorded.Success.Should().BeTrue();
        recorded.Streamed.Should().BeFalse();
    }

    private sealed class RecordingProductTelemetry : IProductTelemetry
    {
        public List<(string ProviderName, bool Success, bool Streamed)> ProviderRequests { get; } = [];

        public void TrackAppStarted()
        {
        }

        public void TrackAppStopped()
        {
        }

        public void TrackFeatureUsed(
            string featureName,
            string interactionKind,
            bool success,
            ConversationTurnMetrics? metrics = null,
            int attachmentCount = 0,
            Exception? exception = null)
        {
        }

        public void TrackToolInvoked(
            string toolName,
            ToolResultStatus status,
            bool success,
            TimeSpan duration,
            ConversationExecutionPhase executionPhase,
            string? modelId = null,
            string? providerName = null,
            string? errorMessage = null)
        {
        }

        public void TrackProviderRequest(
            string providerName,
            bool success,
            TimeSpan latency,
            bool streamed,
            TimeSpan streamLatency,
            int retryCount,
            string? errorMessage = null)
            => ProviderRequests.Add((providerName, success, streamed));
    }
}
