using StemCode.Application.Models;
using StemCode.Domain.Models;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace StemCode.Infrastructure.Conversation;

internal sealed class OllamaCloudConversationProviderAdapter : IConversationProviderAdapter
{
    private readonly IConversationProviderHttpExecutor _httpExecutor;
    private readonly ConversationProviderRequestPayloadFactory _payloadFactory;
    private readonly ConversationProviderResponseNormalizer _responseNormalizer;

    public OllamaCloudConversationProviderAdapter(
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
        OpenAiChatCompletionRequest chatPayload = _payloadFactory.BuildChatCompletionRequest(request);
        OllamaChatRequest payload = new(
            chatPayload.Model,
            chatPayload.Messages,
            chatPayload.Tools.Count == 0 ? null : chatPayload.Tools,
            Stream: false,
            Think: _payloadFactory.MapOllamaThink(request));
        string requestBody = JsonSerializer.Serialize(
            payload,
            OpenAiConversationJsonContext.Default.OllamaChatRequest);
        Uri baseUri = request.ProviderProfile.ResolveBaseUri();

        return _httpExecutor.ExecuteAsync(
            request.ProviderProfile.ProviderKind,
            () => CreateHttpRequest(baseUri, request.ApiKey, requestBody),
            cancellationToken,
            _responseNormalizer.ConvertOllamaChatResponseToChatCompletion,
            onRetryAsync: request.OnProviderRetryAsync);
    }

    private static HttpRequestMessage CreateHttpRequest(
        Uri baseUri,
        string apiKey,
        string requestBody)
    {
        HttpRequestMessage httpRequest = new(HttpMethod.Post, new Uri(baseUri, "api/chat"));
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
        return httpRequest;
    }
}
