using Microsoft.Extensions.Logging;
using StemCode.Application.Abstractions;
using StemCode.Application.Models;
using StemCode.Domain.Models;
using StemCode.Infrastructure.GitHub;
using StemCode.Infrastructure.StemCodeEnterprise;
using StemCode.Infrastructure.OpenAi;
using StemCode.Infrastructure.Secrets;

namespace StemCode.Infrastructure.Conversation;

internal sealed class OpenAiCompatibleConversationProviderClient : IConversationProviderClient
{
    private readonly AnthropicClaudeAccountConversationProviderAdapter _anthropicClaudeAccountAdapter;
    private readonly GitHubCopilotConversationProviderAdapter _gitHubCopilotAdapter;
    private readonly OpenAiChatGptAccountConversationProviderAdapter _openAiChatGptAccountAdapter;
    private readonly OpenAiCompatibleConversationProviderAdapter _openAiCompatibleAdapter;
    private readonly OpenCodeZenConversationProviderAdapter _openCodeZenAdapter;
    private readonly OllamaCloudConversationProviderAdapter _ollamaCloudAdapter;

    public OpenAiCompatibleConversationProviderClient(
        HttpClient httpClient,
        ILogger<OpenAiCompatibleConversationProviderClient> logger,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        Func<double>? nextJitter = null,
        IOpenAiChatGptAccountCredentialService? openAiChatGptAccountCredentialService = null,
        IGitHubCopilotCredentialService? gitHubCopilotCredentialService = null,
        IStemCodeEnterpriseCredentialService? stemCodeEnterpriseCredentialService = null,
        TimeProvider? timeProvider = null,
        IProductTelemetry? telemetry = null,
        ProviderRequestProjectHeaderProvider? providerRequestProjectHeaderProvider = null,
        IProcessRunner? processRunner = null)
    {
        ConversationProviderRequestPayloadFactory payloadFactory = new();
        ConversationProviderResponseNormalizer responseNormalizer = new();
        ConversationProviderHttpExecutor httpExecutor = new(httpClient, logger, delayAsync, nextJitter, timeProvider, telemetry);

        _openAiChatGptAccountAdapter = new OpenAiChatGptAccountConversationProviderAdapter(
            httpExecutor,
            payloadFactory,
            responseNormalizer,
            openAiChatGptAccountCredentialService);
        _anthropicClaudeAccountAdapter = new AnthropicClaudeAccountConversationProviderAdapter(
            processRunner);
        _gitHubCopilotAdapter = new GitHubCopilotConversationProviderAdapter(
            httpExecutor,
            payloadFactory,
            gitHubCopilotCredentialService);
        _ollamaCloudAdapter = new OllamaCloudConversationProviderAdapter(
            httpExecutor,
            payloadFactory,
            responseNormalizer);
        _openCodeZenAdapter = new OpenCodeZenConversationProviderAdapter(
            httpExecutor,
            payloadFactory,
            responseNormalizer);
        _openAiCompatibleAdapter = new OpenAiCompatibleConversationProviderAdapter(
            httpExecutor,
            payloadFactory,
            stemCodeEnterpriseCredentialService,
            providerRequestProjectHeaderProvider);
    }

    public Task<ConversationProviderPayload> SendAsync(
        ConversationProviderRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        return ResolveAdapter(request.ProviderProfile.ProviderKind).SendAsync(request, cancellationToken);
    }

    private IConversationProviderAdapter ResolveAdapter(ProviderKind providerKind)
    {
        return providerKind switch
        {
            ProviderKind.OpenAiChatGptAccount => _openAiChatGptAccountAdapter,
            ProviderKind.AnthropicClaudeAccount => _anthropicClaudeAccountAdapter,
            ProviderKind.GitHubCopilot => _gitHubCopilotAdapter,
            ProviderKind.OllamaCloud => _ollamaCloudAdapter,
            ProviderKind.OpenCodeZen => _openCodeZenAdapter,
            _ => _openAiCompatibleAdapter
        };
    }
}
