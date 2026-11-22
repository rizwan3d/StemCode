using StemCode.Application.Models;
using StemCode.Domain.Models;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace StemCode.Infrastructure.Conversation;

internal sealed class OpenCodeZenConversationProviderAdapter : IConversationProviderAdapter
{
    private const string ChatCompletionsPath = "chat/completions";
    private const string MessagesPath = AnthropicMessagesTransport.MessagesPath;
    private const string ResponsesPath = "responses";

    private readonly IConversationProviderHttpExecutor _httpExecutor;
    private readonly ConversationProviderRequestPayloadFactory _payloadFactory;
    private readonly ConversationProviderResponseNormalizer _responseNormalizer;

    public OpenCodeZenConversationProviderAdapter(
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
        string path = ResolvePath(request.ModelId);
        Uri baseUri = request.ProviderProfile.ResolveBaseUri();

        if (path == MessagesPath)
        {
            return AnthropicMessagesTransport.SendAsync(
                _httpExecutor,
                _payloadFactory,
                _responseNormalizer,
                request,
                baseUri,
                headers => headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey),
                cancellationToken);
        }

        string requestBody = CreateRequestBody(request, path);

        return _httpExecutor.ExecuteAsync(
            request.ProviderProfile.ProviderKind,
            () => CreateHttpRequest(baseUri, path, request.ApiKey, requestBody),
            cancellationToken,
            ResolveResponseNormalizer(path),
            onRetryAsync: request.OnProviderRetryAsync,
            readResponseBodyAsync: path == ResponsesPath
                ? OpenAiResponsesEventStreamParser.ReadResponseBodyAsync
                : null,
            onAssistantMessageChunkAsync: path == ResponsesPath
                ? request.OnAssistantMessageChunkAsync
                : null);
    }

    private Func<string, string>? ResolveResponseNormalizer(string path)
    {
        return path switch
        {
            ResponsesPath => _responseNormalizer.NormalizeOpenAiResponsesBody,
            _ => null
        };
    }

    private string CreateRequestBody(
        ConversationProviderRequest request,
        string path)
    {
        return path switch
        {
            ResponsesPath => JsonSerializer.Serialize(
                _payloadFactory.BuildResponsesRequest(request),
                OpenAiConversationJsonContext.Default.OpenAiResponsesRequest),
            ChatCompletionsPath => JsonSerializer.Serialize(
                _payloadFactory.BuildChatCompletionRequest(request),
                OpenAiConversationJsonContext.Default.OpenAiChatCompletionRequest),
            _ => throw new InvalidOperationException($"Unsupported OpenCode Zen path '{path}'.")
        };
    }

    private static string ResolvePath(string modelId)
    {
        string normalizedModelId = modelId.Trim().ToLowerInvariant();
        if (normalizedModelId.StartsWith("gpt-", StringComparison.Ordinal))
        {
            return ResponsesPath;
        }

        if (normalizedModelId.StartsWith("claude-", StringComparison.Ordinal))
        {
            return MessagesPath;
        }

        return ChatCompletionsPath;
    }

    private static HttpRequestMessage CreateHttpRequest(
        Uri baseUri,
        string path,
        string apiKey,
        string requestBody)
    {
        HttpRequestMessage httpRequest = new(HttpMethod.Post, new Uri(baseUri, path));
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Headers.Accept.ParseAdd("application/json");
        httpRequest.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
        return httpRequest;
    }
}
