using Microsoft.Extensions.Logging;
using StemCode.Application.Abstractions;
using StemCode.Application.Exceptions;
using StemCode.Domain.Models;
using StemCode.Infrastructure.Anthropic;
using StemCode.Infrastructure.GitHub;
using StemCode.Infrastructure.StemCodeEnterprise;
using StemCode.Infrastructure.OpenAi;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace StemCode.Infrastructure.Models;

internal sealed class OpenAiCompatibleModelProviderClient : IModelProviderClient
{
    private const string AnthropicVersion = "2023-06-01";
    private const string AccountHeaderName = "Chat" + "G" + "P" + "T-Account-Id";
    private const string Originator = "stemcode";
    private const string OpenRouterApplicationUrl = "https://github.com/rizwan3d/StemCode";
    private const string KiloCodeEditorName = "StemCode";
    private const string KiloCodeUserAgent = "stemcode-kilo-provider";
    private static readonly IReadOnlyDictionary<string, int> ContextWindowFallbacks =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["deepseek-v4-pro"] = 1_000_000,
            ["deepseek-v4-flash"] = 1_000_000
        };
    private static readonly string[] ContextWindowPropertyNames =
    [
        "context_length",
        "contextLength",
        "context_window",
        "contextWindow",
        "context_window_tokens",
        "contextWindowTokens",
        "max_context_length",
        "maxContextLength",
        "max_context_tokens",
        "maxContextTokens",
        "input_token_limit",
        "inputTokenLimit",
        "max_input_tokens",
        "maxInputTokens"
    ];
    private static readonly string[] ContextWindowContainerPropertyNames =
    [
        "metadata",
        "limits",
        "capabilities",
        "architecture",
        "top_provider",
        "topProvider"
    ];
    private static readonly string[] MaxContextWindowPropertyNames =
    [
        "max_context_window",
        "maxContextWindow",
        "max_context_window_tokens",
        "maxContextWindowTokens"
    ];
    private static readonly string[] AutoCompactTokenLimitPropertyNames =
    [
        "auto_compact_token_limit",
        "autoCompactTokenLimit"
    ];
    private static readonly string[] EffectiveContextWindowPercentPropertyNames =
    [
        "effective_context_window_percent",
        "effectiveContextWindowPercent"
    ];
    private static readonly string[] ToolOutputTokenLimitPropertyNames =
    [
        "tool_output_token_limit",
        "toolOutputTokenLimit"
    ];
    private static readonly string[] TruncationPolicyPropertyNames =
    [
        "truncation_policy",
        "truncationPolicy"
    ];

    private readonly HttpClient _httpClient;
    private readonly IOpenAiCodexClientVersionProvider _openAiCodexClientVersionProvider;
    private readonly IAnthropicClaudeAccountCredentialService? _anthropicClaudeAccountCredentialService;
    private readonly IGitHubCopilotCredentialService? _gitHubCopilotCredentialService;
    private readonly IStemCodeEnterpriseCredentialService? _stemCodeEnterpriseCredentialService;
    private readonly IOpenAiChatGptAccountCredentialService? _openAiChatGptAccountCredentialService;
    private readonly ProviderRequestProjectHeaderProvider? _providerRequestProjectHeaderProvider;
    private readonly ILogger<OpenAiCompatibleModelProviderClient> _logger;

    public OpenAiCompatibleModelProviderClient(
        HttpClient httpClient,
        ILogger<OpenAiCompatibleModelProviderClient> logger,
        IOpenAiChatGptAccountCredentialService? openAiChatGptAccountCredentialService = null,
        IOpenAiCodexClientVersionProvider? openAiCodexClientVersionProvider = null,
        IAnthropicClaudeAccountCredentialService? anthropicClaudeAccountCredentialService = null,
        IGitHubCopilotCredentialService? gitHubCopilotCredentialService = null,
        IStemCodeEnterpriseCredentialService? stemCodeEnterpriseCredentialService = null,
        ProviderRequestProjectHeaderProvider? providerRequestProjectHeaderProvider = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _openAiChatGptAccountCredentialService = openAiChatGptAccountCredentialService;
        _anthropicClaudeAccountCredentialService = anthropicClaudeAccountCredentialService;
        _gitHubCopilotCredentialService = gitHubCopilotCredentialService;
        _stemCodeEnterpriseCredentialService = stemCodeEnterpriseCredentialService;
        _providerRequestProjectHeaderProvider = providerRequestProjectHeaderProvider;
        _openAiCodexClientVersionProvider = openAiCodexClientVersionProvider ??
            new StaticOpenAiCodexClientVersionProvider(
                GitHubOpenAiCodexClientVersionProvider.FallbackClientVersion);
    }

    public async Task<IReadOnlyList<AvailableModel>> GetAvailableModelsAsync(
        AgentProviderProfile providerProfile,
        string apiKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(providerProfile);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        if (providerProfile.ProviderKind == ProviderKind.OpenAiChatGptAccount)
        {
            if (_openAiChatGptAccountCredentialService is null)
            {
                throw new ModelProviderException(
                    "OpenAI ChatGPT Plus/Pro credentials cannot be resolved in this runtime.");
            }

            return await GetOpenAiChatGptAccountModelsAsync(
                apiKey,
                cancellationToken);
        }

        if (providerProfile.ProviderKind == ProviderKind.AnthropicClaudeAccount)
        {
            if (_anthropicClaudeAccountCredentialService is null)
            {
                throw new ModelProviderException(
                    "Anthropic Claude Pro/Max credentials cannot be resolved in this runtime.");
            }

            return await GetAnthropicClaudeAccountModelsAsync(
                apiKey,
                cancellationToken);
        }

        if (providerProfile.ProviderKind == ProviderKind.GitHubCopilot)
        {
            if (_gitHubCopilotCredentialService is null)
            {
                throw new ModelProviderException(
                    "GitHub Copilot credentials cannot be resolved in this runtime.");
            }

            return await GetGitHubCopilotModelsAsync(
                apiKey,
                cancellationToken);
        }

        if (providerProfile.ProviderKind == ProviderKind.OllamaCloud)
        {
            return await GetOllamaCloudModelsAsync(
                providerProfile,
                apiKey,
                cancellationToken);
        }

        Uri baseUri = providerProfile.ResolveBaseUri();
        string authorizationValue = apiKey;
        bool usesStemCodeEnterpriseCredentials = _stemCodeEnterpriseCredentialService?.CanResolve(apiKey) == true;
        if (usesStemCodeEnterpriseCredentials)
        {
            if (_stemCodeEnterpriseCredentialService is null)
            {
                throw new ModelProviderException(
                    "StemCode Enterprise credentials cannot be resolved in this runtime.");
            }

            StemCodeEnterpriseResolvedCredential enterpriseCredential =
                await _stemCodeEnterpriseCredentialService.ResolveAsync(
                    apiKey,
                    forceRefresh: false,
                    cancellationToken);
            authorizationValue = enterpriseCredential.AccessToken;
        }

        bool refreshedAfterUnauthorized = false;
        bool reauthenticatedAfterUnauthorized = false;

        while (true)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, new Uri(baseUri, "models"));
            ApplyAuthenticationHeaders(
                request,
                providerProfile.ProviderKind,
                authorizationValue,
                usesStemCodeEnterpriseCredentials);
            LogDebugApiRequest(request.Method, request.RequestUri);

            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            LogDebugApiResponse(response.StatusCode);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized &&
                usesStemCodeEnterpriseCredentials &&
                !refreshedAfterUnauthorized)
            {
                refreshedAfterUnauthorized = true;
                StemCodeEnterpriseResolvedCredential enterpriseCredential;
                try
                {
                    enterpriseCredential = await _stemCodeEnterpriseCredentialService!.ResolveAsync(
                        apiKey,
                        forceRefresh: true,
                        cancellationToken);
                }
                catch (InvalidOperationException)
                {
                    string storedCredentials = await _stemCodeEnterpriseCredentialService!.AuthenticateAsync(
                        providerProfile.ResolveBaseUrl(),
                        cancellationToken);
                    enterpriseCredential = await _stemCodeEnterpriseCredentialService.ResolveAsync(
                        storedCredentials,
                        forceRefresh: false,
                        cancellationToken);
                    reauthenticatedAfterUnauthorized = true;
                }

                authorizationValue = enterpriseCredential.AccessToken;
                continue;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized &&
                usesStemCodeEnterpriseCredentials &&
                refreshedAfterUnauthorized &&
                !reauthenticatedAfterUnauthorized)
            {
                string storedCredentials = await _stemCodeEnterpriseCredentialService!.AuthenticateAsync(
                    providerProfile.ResolveBaseUrl(),
                    cancellationToken);
                StemCodeEnterpriseResolvedCredential enterpriseCredential =
                    await _stemCodeEnterpriseCredentialService.ResolveAsync(
                        storedCredentials,
                        forceRefresh: false,
                        cancellationToken);
                authorizationValue = enterpriseCredential.AccessToken;
                reauthenticatedAfterUnauthorized = true;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                string detail = string.IsNullOrWhiteSpace(responseBody)
                    ? $"Provider returned HTTP {(int)response.StatusCode}."
                    : $"Provider returned HTTP {(int)response.StatusCode}: {Truncate(responseBody.Trim(), 200)}";

                throw new ModelProviderException(
                    $"Unable to fetch models from the configured provider. {detail}");
            }

            IReadOnlyList<AvailableModel> models = ParseAvailableModels(
                responseBody,
                providerProfile.ProviderKind);
            if (models.Count == 0)
            {
                throw new ModelProviderException(
                    "The configured provider returned an invalid models response.");
            }

            return models;
        }
    }

    private async Task<IReadOnlyList<AvailableModel>> GetOpenAiChatGptAccountModelsAsync(
        string storedCredentials,
        CancellationToken cancellationToken)
    {
        if (_openAiChatGptAccountCredentialService is null)
        {
            throw new ModelProviderException(
                "OpenAI ChatGPT Plus/Pro credentials cannot be resolved in this runtime.");
        }

        Uri baseUri = new AgentProviderProfile(
            ProviderKind.OpenAiChatGptAccount,
            BaseUrl: null).ResolveBaseUri();
        OpenAiChatGptAccountResolvedCredential credential =
            await _openAiChatGptAccountCredentialService.ResolveAsync(
                storedCredentials,
                forceRefresh: false,
                cancellationToken);
        string accountClientVersion = await _openAiCodexClientVersionProvider.GetClientVersionAsync(
            cancellationToken);

        bool forcedRefreshAfterAuthFailure = false;

        while (true)
        {
            try
            {
                using HttpRequestMessage request = CreateOpenAiChatGptAccountModelsRequest(
                    baseUri,
                    credential,
                    accountClientVersion);
                LogDebugApiRequest(request.Method, request.RequestUri);

                using HttpResponseMessage response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                LogDebugApiResponse(response.StatusCode);

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized &&
                    !forcedRefreshAfterAuthFailure)
                {
                    forcedRefreshAfterAuthFailure = true;
                    credential = await _openAiChatGptAccountCredentialService.ResolveAsync(
                        storedCredentials,
                        forceRefresh: true,
                        cancellationToken);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    string detail = string.IsNullOrWhiteSpace(responseBody)
                        ? $"Provider returned HTTP {(int)response.StatusCode}."
                        : $"Provider returned HTTP {(int)response.StatusCode}: {Truncate(responseBody.Trim(), 200)}";

                    throw new ModelProviderException(
                        $"Unable to fetch OpenAI ChatGPT Plus/Pro models from the account API. {detail}");
                }

                IReadOnlyList<AvailableModel> models = ParseAvailableModels(
                    responseBody,
                    ProviderKind.OpenAiChatGptAccount);
                if (models.Count > 0)
                {
                    return models;
                }

                throw new ModelProviderException(
                    "The OpenAI ChatGPT Plus/Pro account API returned an invalid models response.");
            }
            catch (HttpRequestException exception)
            {
                throw new ModelProviderException(
                    "Unable to fetch OpenAI ChatGPT Plus/Pro models from the account API.",
                    exception);
            }
            catch (JsonException exception)
            {
                throw new ModelProviderException(
                    "The OpenAI ChatGPT Plus/Pro account API returned an invalid models response.",
                    exception);
            }
        }
    }

    private async Task<IReadOnlyList<AvailableModel>> GetAnthropicClaudeAccountModelsAsync(
        string storedCredentials,
        CancellationToken cancellationToken)
    {
        if (_anthropicClaudeAccountCredentialService is null)
        {
            throw new ModelProviderException(
                "Anthropic Claude Pro/Max credentials cannot be resolved in this runtime.");
        }

        await _anthropicClaudeAccountCredentialService.ResolveAsync(
            storedCredentials,
            forceRefresh: false,
            cancellationToken);

        // These aliases are resolved by the installed Claude Code release, so the
        // selected model follows Anthropic's current catalog without StemCode
        // calling the subscription API or handling OAuth credentials itself.
        return
        [
            new AvailableModel("sonnet"),
            new AvailableModel("opus"),
            new AvailableModel("haiku"),
            new AvailableModel("fable")
        ];
    }

    private async Task<IReadOnlyList<AvailableModel>> GetGitHubCopilotModelsAsync(
        string storedCredentials,
        CancellationToken cancellationToken)
    {
        if (_gitHubCopilotCredentialService is null)
        {
            throw new ModelProviderException(
                "GitHub Copilot credentials cannot be resolved in this runtime.");
        }

        GitHubCopilotResolvedCredential credential =
            await _gitHubCopilotCredentialService.ResolveAsync(
                storedCredentials,
                forceRefresh: false,
                cancellationToken);

        bool forcedRefreshAfterAuthFailure = false;

        while (true)
        {
            try
            {
                using HttpRequestMessage request = CreateGitHubCopilotModelsRequest(credential);
                LogDebugApiRequest(request.Method, request.RequestUri);

                using HttpResponseMessage response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                LogDebugApiResponse(response.StatusCode);

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized &&
                    !forcedRefreshAfterAuthFailure)
                {
                    forcedRefreshAfterAuthFailure = true;
                    credential = await _gitHubCopilotCredentialService.ResolveAsync(
                        storedCredentials,
                        forceRefresh: true,
                        cancellationToken);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    string detail = string.IsNullOrWhiteSpace(responseBody)
                        ? $"Provider returned HTTP {(int)response.StatusCode}."
                        : $"Provider returned HTTP {(int)response.StatusCode}: {Truncate(responseBody.Trim(), 200)}";

                    throw new ModelProviderException(
                        $"Unable to fetch GitHub Copilot models from the account API. {detail}");
                }

                IReadOnlyList<AvailableModel> models = ParseAvailableModels(
                    responseBody,
                    ProviderKind.GitHubCopilot);
                if (models.Count > 0)
                {
                    return models;
                }

                throw new ModelProviderException(
                    "The GitHub Copilot account API returned an invalid models response.");
            }
            catch (HttpRequestException exception)
            {
                throw new ModelProviderException(
                    "Unable to fetch GitHub Copilot models from the account API.",
                    exception);
            }
            catch (JsonException exception)
            {
                throw new ModelProviderException(
                    "The GitHub Copilot account API returned an invalid models response.",
                    exception);
            }
        }
    }

    private async Task<IReadOnlyList<AvailableModel>> GetOllamaCloudModelsAsync(
        AgentProviderProfile providerProfile,
        string apiKey,
        CancellationToken cancellationToken)
    {
        Uri baseUri = providerProfile.ResolveBaseUri();
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(baseUri, "api/tags"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        LogDebugApiRequest(request.Method, request.RequestUri);

        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        LogDebugApiResponse(response.StatusCode);

        if (!response.IsSuccessStatusCode)
        {
            string detail = string.IsNullOrWhiteSpace(responseBody)
                ? $"Provider returned HTTP {(int)response.StatusCode}."
                : $"Provider returned HTTP {(int)response.StatusCode}: {Truncate(responseBody.Trim(), 200)}";

            throw new ModelProviderException(
                $"Unable to fetch models from Ollama Cloud. {detail}");
        }

        IReadOnlyList<AvailableModel> models = ParseOllamaModels(responseBody);
        if (models.Count == 0)
        {
            throw new ModelProviderException(
                "Ollama Cloud returned an invalid models response.");
        }

        return models;
    }

    private HttpRequestMessage CreateOpenAiChatGptAccountModelsRequest(
        Uri baseUri,
        OpenAiChatGptAccountResolvedCredential credential,
        string accountClientVersion)
    {
        HttpRequestMessage request = new(
            HttpMethod.Get,
            new Uri(baseUri, $"models?client_version={Uri.EscapeDataString(accountClientVersion)}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        request.Headers.TryAddWithoutValidation("originator", Originator);
        request.Headers.TryAddWithoutValidation("User-Agent", "StemCode/1.0");
        if (!string.IsNullOrWhiteSpace(credential.AccountId))
        {
            request.Headers.TryAddWithoutValidation(AccountHeaderName, credential.AccountId);
        }

        return request;
    }

    private static HttpRequestMessage CreateGitHubCopilotModelsRequest(
        GitHubCopilotResolvedCredential credential)
    {
        HttpRequestMessage request = new(HttpMethod.Get, new Uri(credential.BaseUri, "models"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        request.Headers.Accept.ParseAdd("application/json");
        GitHubCopilotCredentialService.ApplyCopilotHeaders(request);
        return request;
    }

    private void ApplyAuthenticationHeaders(
        HttpRequestMessage request,
        ProviderKind providerKind,
        string apiKey,
        bool usesStemCodeEnterpriseCredentials)
    {
        if (providerKind == ProviderKind.Anthropic)
        {
            request.Headers.Add("x-api-key", apiKey);
            request.Headers.Add("anthropic-version", AnthropicVersion);
            return;
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        string requestTitle =
            ProviderRequestProjectHeaderProvider.GetConfiguredTitle() ??
            ProviderRequestProjectHeaderProvider.DefaultRequestTitle;

        if (providerKind == ProviderKind.OpenRouter)
        {
            request.Headers.TryAddWithoutValidation("HTTP-Referer", OpenRouterApplicationUrl);
            request.Headers.TryAddWithoutValidation("X-Title", requestTitle);
            return;
        }

        if (providerKind == ProviderKind.KiloCode)
        {
            request.Headers.TryAddWithoutValidation("X-KILOCODE-EDITORNAME", KiloCodeEditorName);
            request.Headers.TryAddWithoutValidation("User-Agent", KiloCodeUserAgent);
            return;
        }

        string? configuredProjectName = ProviderRequestProjectHeaderProvider.GetConfiguredProjectName();
        if (usesStemCodeEnterpriseCredentials)
        {
            request.Headers.TryAddWithoutValidation("X-Title", requestTitle);
        }

        if (usesStemCodeEnterpriseCredentials || configuredProjectName is not null)
        {
            request.Headers.TryAddWithoutValidation(
                "X-Project",
                _providerRequestProjectHeaderProvider?.GetProjectName() ??
                configuredProjectName ??
                ProviderRequestProjectHeaderProvider.ResolveProjectName(Directory.GetCurrentDirectory()));
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength
            ? value
            : value[..Math.Max(0, maxLength - 3)] + "...";
    }

    private static IReadOnlyList<AvailableModel> ParseAvailableModels(
        string responseBody,
        ProviderKind providerKind)
    {
        using JsonDocument document = JsonDocument.Parse(responseBody);
        if (!TryGetModelArray(document.RootElement, out JsonElement modelsElement))
        {
            return [];
        }

        List<AvailableModel> models = [];
        foreach (JsonElement item in modelsElement.EnumerateArray())
        {
            string? id = TryGetModelId(item);
            if (!string.IsNullOrWhiteSpace(id))
            {
                string normalizedId = id.Trim();
                int? contextWindowTokens = TryGetContextWindowTokens(item) ?? TryGetFallbackContextWindowTokens(
                    providerKind,
                    normalizedId);
                models.Add(new AvailableModel(
                    normalizedId,
                    contextWindowTokens,
                    TryGetModelContextMetadata(item, contextWindowTokens)));
            }
        }

        return models;
    }

    private static IReadOnlyList<AvailableModel> ParseOllamaModels(string responseBody)
    {
        using JsonDocument document = JsonDocument.Parse(responseBody);
        if (!document.RootElement.TryGetProperty("models", out JsonElement modelsElement) ||
            modelsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<AvailableModel> models = [];
        foreach (JsonElement item in modelsElement.EnumerateArray())
        {
            string? name = TryGetModelName(item);
            if (!string.IsNullOrWhiteSpace(name))
            {
                models.Add(new AvailableModel(name.Trim()));
            }
        }

        return models;
    }

    private static string? TryGetModelName(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.String)
        {
            return item.GetString();
        }

        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (string propertyName in new[] { "name", "model", "id" })
        {
            if (item.TryGetProperty(propertyName, out JsonElement property) &&
                property.ValueKind == JsonValueKind.String)
            {
                return property.GetString();
            }
        }

        return null;
    }

    private static bool TryGetModelArray(
        JsonElement root,
        out JsonElement modelsElement)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            modelsElement = root;
            return true;
        }

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("data", out modelsElement) &&
            modelsElement.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("models", out modelsElement) &&
            modelsElement.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        modelsElement = default;
        return false;
    }

    private static int? TryGetContextWindowTokens(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (JsonProperty property in item.EnumerateObject())
        {
            if (MatchesAny(property.Name, ContextWindowPropertyNames) &&
                TryReadPositiveInt32(property.Value, out int tokens))
            {
                return tokens;
            }
        }

        foreach (JsonProperty property in item.EnumerateObject())
        {
            if (MatchesAny(property.Name, ContextWindowContainerPropertyNames) &&
                TryGetContextWindowTokens(property.Value) is { } nestedTokens)
            {
                return nestedTokens;
            }
        }

        return null;
    }

    private static int? TryGetFallbackContextWindowTokens(
        ProviderKind providerKind,
        string modelId)
    {
        if (providerKind != ProviderKind.DeepSeek)
        {
            return null;
        }

        return ContextWindowFallbacks.TryGetValue(modelId.Replace("-free","").Replace("free",""), out int contextWindowTokens)
            ? contextWindowTokens
            : null;
    }

    private static ModelContextMetadata? TryGetModelContextMetadata(
        JsonElement item,
        int? fallbackContextWindowTokens)
    {
        int contextWindowTokens = TryGetContextWindowTokens(item) ?? fallbackContextWindowTokens ?? 0;
        if (contextWindowTokens <= 0)
        {
            return null;
        }

        return new ModelContextMetadata(
            contextWindowTokens,
            TryGetNamedPositiveInt32(item, MaxContextWindowPropertyNames),
            TryGetNamedPositiveInt32(item, AutoCompactTokenLimitPropertyNames),
            TryGetNamedFraction(item, EffectiveContextWindowPercentPropertyNames),
            TryGetTruncationPolicy(item),
            TryGetNamedPositiveInt32(item, ToolOutputTokenLimitPropertyNames));
    }

    private static ToolResultTruncationPolicy? TryGetTruncationPolicy(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (JsonProperty property in item.EnumerateObject())
        {
            if (!MatchesAny(property.Name, TruncationPolicyPropertyNames) ||
                property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            JsonElement policy = property.Value;
            if (!TryGetNamedString(policy, "mode", out string? mode))
            {
                continue;
            }

            int? limit = TryGetNamedPositiveInt32(policy, ["limit"]);
            if (limit is not > 0)
            {
                continue;
            }

            return new ToolResultTruncationPolicy(mode!, limit.Value);
        }

        foreach (JsonProperty property in item.EnumerateObject())
        {
            if (MatchesAny(property.Name, ContextWindowContainerPropertyNames))
            {
                ToolResultTruncationPolicy? nestedPolicy = TryGetTruncationPolicy(property.Value);
                if (nestedPolicy is not null)
                {
                    return nestedPolicy;
                }
            }
        }

        return null;
    }

    private static int? TryGetNamedPositiveInt32(
        JsonElement item,
        IReadOnlyList<string> propertyNames)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (JsonProperty property in item.EnumerateObject())
        {
            if (MatchesAny(property.Name, propertyNames) &&
                TryReadPositiveInt32(property.Value, out int value))
            {
                return value;
            }
        }

        foreach (JsonProperty property in item.EnumerateObject())
        {
            if (MatchesAny(property.Name, ContextWindowContainerPropertyNames))
            {
                int? nested = TryGetNamedPositiveInt32(property.Value, propertyNames);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static double? TryGetNamedFraction(
        JsonElement item,
        IReadOnlyList<string> propertyNames)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (JsonProperty property in item.EnumerateObject())
        {
            if (!MatchesAny(property.Name, propertyNames))
            {
                continue;
            }

            if (TryReadDouble(property.Value, out double value) &&
                value > 0d)
            {
                return value > 1d
                    ? Math.Min(1d, value / 100d)
                    : value;
            }
        }

        foreach (JsonProperty property in item.EnumerateObject())
        {
            if (MatchesAny(property.Name, ContextWindowContainerPropertyNames))
            {
                double? nested = TryGetNamedFraction(property.Value, propertyNames);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static bool TryGetNamedString(
        JsonElement item,
        string propertyName,
        out string? value)
    {
        value = null;
        if (item.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (JsonProperty property in item.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                value = property.Value.GetString();
                return !string.IsNullOrWhiteSpace(value);
            }
        }

        return false;
    }

    private static bool TryReadPositiveInt32(JsonElement value, out int result)
    {
        result = 0;
        long numericValue;

        if (value.ValueKind == JsonValueKind.Number)
        {
            if (!value.TryGetInt64(out numericValue))
            {
                return false;
            }
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            string? rawValue = value.GetString();
            if (!long.TryParse(
                    rawValue,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out numericValue))
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        if (numericValue <= 0 || numericValue > int.MaxValue)
        {
            return false;
        }

        result = (int)numericValue;
        return true;
    }

    private static bool TryReadDouble(JsonElement value, out double result)
    {
        result = 0d;
        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.TryGetDouble(out result);
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return double.TryParse(
                value.GetString(),
                NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture,
                out result);
        }

        return false;
    }

    private static bool MatchesAny(string value, IReadOnlyList<string> candidates)
    {
        return candidates.Any(candidate => string.Equals(
            candidate,
            value,
            StringComparison.OrdinalIgnoreCase));
    }

    private static string? TryGetModelId(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.String)
        {
            return item.GetString();
        }

        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (string propertyName in new[] { "id", "slug", "model_slug" })
        {
            if (item.TryGetProperty(propertyName, out JsonElement property) &&
                property.ValueKind == JsonValueKind.String)
            {
                return property.GetString();
            }
        }

        return null;
    }

    private void LogDebugApiRequest(
        HttpMethod method,
        Uri? requestUri)
    {
#if DEBUG
        _logger.LogInformation(
            "OpenAI-compatible models API request {Method} {RequestUri}",
            method,
            requestUri);
#endif
    }

    private void LogDebugApiResponse(
        System.Net.HttpStatusCode statusCode)
    {
#if DEBUG
        // Intentionally omits the response body to avoid logging provider payloads.
        _logger.LogInformation(
            "OpenAI-compatible models API response {StatusCode}",
            (int)statusCode);
#endif
    }

    private sealed class StaticOpenAiCodexClientVersionProvider : IOpenAiCodexClientVersionProvider
    {
        private readonly string _clientVersion;

        public StaticOpenAiCodexClientVersionProvider(string clientVersion)
        {
            _clientVersion = clientVersion;
        }

        public Task<string> GetClientVersionAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(_clientVersion);
        }
    }
}
