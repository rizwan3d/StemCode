using StemCode.Application.Models;
using StemCode.Domain.Models;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace StemCode.Infrastructure.Conversation;

/// <summary>
/// Sends Anthropic API-key conversations to the native Messages API (<c>/v1/messages</c>)
/// rather than Anthropic's OpenAI-compatibility endpoint, which supports neither prompt
/// caching nor thinking-block replay.
/// </summary>
internal sealed class AnthropicMessagesConversationProviderAdapter : IConversationProviderAdapter
{
    private readonly IConversationProviderHttpExecutor _httpExecutor;
    private readonly ConversationProviderRequestPayloadFactory _payloadFactory;
    private readonly ConversationProviderResponseNormalizer _responseNormalizer;

    public AnthropicMessagesConversationProviderAdapter(
        IConversationProviderHttpExecutor httpExecutor,
        ConversationProviderRequestPayloadFactory payloadFactory,
        ConversationProviderResponseNormalizer responseNormalizer)
    {
        _httpExecutor = httpExecutor;
        _payloadFactory = payloadFactory;
        _responseNormalizer = responseNormalizer;
    }

    public Task<ConversationProviderPayload> SendAsync(
        ConversationProviderRequest request,
        CancellationToken cancellationToken)
    {
        return AnthropicMessagesTransport.SendAsync(
            _httpExecutor,
            _payloadFactory,
            _responseNormalizer,
            request,
            request.ProviderProfile.ResolveBaseUri(),
            headers => headers.TryAddWithoutValidation("x-api-key", request.ApiKey),
            cancellationToken);
    }
}

internal static class AnthropicMessagesTransport
{
    public const string AnthropicVersion = "2023-06-01";
    public const string MessagesPath = "messages";

    public static Task<ConversationProviderPayload> SendAsync(
        IConversationProviderHttpExecutor httpExecutor,
        ConversationProviderRequestPayloadFactory payloadFactory,
        ConversationProviderResponseNormalizer responseNormalizer,
        ConversationProviderRequest request,
        Uri baseUri,
        Action<HttpRequestHeaders> applyAuthentication,
        CancellationToken cancellationToken)
    {
        string requestBody = JsonSerializer.Serialize(
            payloadFactory.BuildAnthropicMessagesRequest(request),
            OpenAiConversationJsonContext.Default.AnthropicMessagesRequest);

        return httpExecutor.ExecuteAsync(
            request.ProviderProfile.ProviderKind,
            () => CreateHttpRequest(baseUri, requestBody, applyAuthentication),
            cancellationToken,
            responseNormalizer.ConvertAnthropicMessagesResponseToChatCompletion,
            onRetryAsync: request.OnProviderRetryAsync,
            readResponseBodyAsync: AnthropicMessagesEventStreamParser.ReadResponseBodyAsync,
            onAssistantMessageChunkAsync: request.OnAssistantMessageChunkAsync);
    }

    private static HttpRequestMessage CreateHttpRequest(
        Uri baseUri,
        string requestBody,
        Action<HttpRequestHeaders> applyAuthentication)
    {
        HttpRequestMessage httpRequest = new(HttpMethod.Post, new Uri(baseUri, MessagesPath));
        applyAuthentication(httpRequest.Headers);
        httpRequest.Headers.Accept.ParseAdd("text/event-stream");
        httpRequest.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
        httpRequest.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
        return httpRequest;
    }
}
