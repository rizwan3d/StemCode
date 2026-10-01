using StemCode.Application.Abstractions;
using StemCode.Application.Models;
using StemCode.Domain.Models;
using StemCode.Domain.Services;
using System.Text.Json;

namespace StemCode.Infrastructure.Storage;

internal sealed class JsonAgentConfigurationStore : IAgentConfigurationStore
{
    private const string BaseUrlEnvironmentVariableName = "STEMCODE_BASE_URL";
    private const string ModelEnvironmentVariableName = "STEMCODE_MODEL";
    private const string ProviderEnvironmentVariableName = "STEMCODE_PROVIDER";
    private const string ThinkingEnvironmentVariableName = "STEMCODE_THINKING";
    private const string ReasoningEnvironmentVariableName = "STEMCODE_REASONING";

    private readonly IUserDataPathProvider _pathProvider;

    public JsonAgentConfigurationStore(IUserDataPathProvider pathProvider)
    {
        _pathProvider = pathProvider;
    }

    public async Task<AgentConfiguration?> LoadAsync(CancellationToken cancellationToken)
    {
        AgentConfiguration? environmentConfiguration = LoadEnvironmentConfiguration();
        if (environmentConfiguration is not null)
        {
            return environmentConfiguration;
        }

        string filePath = _pathProvider.GetConfigurationFilePath();
        if (!File.Exists(filePath))
        {
            return null;
        }

        string json = await File.ReadAllTextAsync(filePath, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        AgentConfiguration? configuration = TryDeserializeConfiguration(json);
        if (configuration is not null)
        {
            return configuration;
        }

        return TryDeserializeLegacyConfiguration(json);
    }

    public async Task SaveAsync(AgentConfiguration configuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        AgentProfileConfigurationDocument document =
            await AgentProfileConfigurationReader.LoadUserDocumentAsync(
                _pathProvider,
                cancellationToken) ??
            new AgentProfileConfigurationDocument();

        string? explicitProviderName = NormalizeProviderName(configuration.ActiveProviderName);
        string providerName = ResolveProviderName(document, configuration);
        document.Providers ??= new Dictionary<string, ProviderProfileConfigurationDocument>(StringComparer.Ordinal);
        document.Providers[providerName] = new ProviderProfileConfigurationDocument
        {
            ProviderProfile = configuration.ProviderProfile,
            PreferredModelId = configuration.PreferredModelId
        };
        if (explicitProviderName is not null)
        {
            document.ActiveProviderName = providerName;
        }

        document.ProviderProfile = configuration.ProviderProfile;
        document.PreferredModelId = configuration.PreferredModelId;
        document.ReasoningEffort = configuration.ReasoningEffort;
        document.ThinkingMode = configuration.ThinkingMode;

        await AgentProfileConfigurationReader.SaveUserDocumentAsync(
            _pathProvider,
            document,
            cancellationToken);
    }

    public async Task<IReadOnlyList<SavedProviderConfiguration>> ListProvidersAsync(
        CancellationToken cancellationToken)
    {
        AgentProfileConfigurationDocument? document =
            await AgentProfileConfigurationReader.LoadUserDocumentAsync(
                _pathProvider,
                cancellationToken);

        if (document is null)
        {
            return [];
        }

        List<SavedProviderConfiguration> providers = [];
        HashSet<string> seenNames = new(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, ProviderProfileConfigurationDocument> item in document.Providers ?? [])
        {
            string? name = NormalizeProviderName(item.Key);
            AgentProviderProfile? profile = NormalizeProfile(item.Value?.ProviderProfile);
            if (name is null || profile is null || !seenNames.Add(name))
            {
                continue;
            }

            providers.Add(new SavedProviderConfiguration(
                name,
                profile,
                ModelIdMatcher.NormalizeOrNull(item.Value?.PreferredModelId)));
        }

        if (document.ProviderProfile is not null &&
            NormalizeProfile(document.ProviderProfile) is { } legacyProfile)
        {
            string legacyName = NormalizeProviderName(document.ActiveProviderName) ??
                CreateProviderName(legacyProfile);
            if (seenNames.Add(legacyName))
            {
                providers.Add(new SavedProviderConfiguration(
                    legacyName,
                    legacyProfile,
                    ModelIdMatcher.NormalizeOrNull(document.PreferredModelId)));
            }
        }

        return providers
            .OrderBy(provider => provider.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task SetActiveProviderAsync(
        string providerName,
        CancellationToken cancellationToken)
    {
        string normalizedProviderName = NormalizeProviderName(providerName)
            ?? throw new ArgumentException("Provider name is required.", nameof(providerName));

        AgentProfileConfigurationDocument document =
            await AgentProfileConfigurationReader.LoadUserDocumentAsync(
                _pathProvider,
                cancellationToken) ??
            throw new InvalidOperationException("No provider configurations are saved.");

        (string Name, ProviderProfileConfigurationDocument Provider)? match = FindProvider(
            document,
            normalizedProviderName);

        if (match is null)
        {
            throw new InvalidOperationException(
                $"Provider '{normalizedProviderName}' is not configured.");
        }

        AgentProviderProfile? normalizedProfile = NormalizeProfile(match.Value.Provider.ProviderProfile);
        if (normalizedProfile is null)
        {
            throw new InvalidOperationException(
                $"Provider '{match.Value.Name}' has invalid configuration.");
        }

        document.ActiveProviderName = match.Value.Name;
        document.ProviderProfile = normalizedProfile;
        document.PreferredModelId = ModelIdMatcher.NormalizeOrNull(match.Value.Provider.PreferredModelId);

        await AgentProfileConfigurationReader.SaveUserDocumentAsync(
            _pathProvider,
            document,
            cancellationToken);
    }

    private static AgentConfiguration? TryDeserializeConfiguration(string json)
    {
        try
        {
            AgentProfileConfigurationDocument? document = JsonSerializer.Deserialize(
                json,
                OnboardingStorageJsonContext.Default.AgentProfileConfigurationDocument);

            if (document is null)
            {
                return null;
            }

            AgentConfiguration? activeProviderConfiguration = TryCreateActiveProviderConfiguration(document);
            if (activeProviderConfiguration is not null)
            {
                return activeProviderConfiguration;
            }

            if (document.ProviderProfile is null)
            {
                return null;
            }

            AgentProviderProfile? normalizedProfile = NormalizeProfile(document.ProviderProfile);
            if (normalizedProfile is null)
            {
                return null;
            }

            (string? normalizedThinkingMode, string? normalizedReasoningEffort) =
                ReasoningOptions.NormalizeStoredValues(
                    document.ThinkingMode,
                    document.ReasoningEffort);

            return new AgentConfiguration(
                normalizedProfile,
                ModelIdMatcher.NormalizeOrNull(document.PreferredModelId),
                normalizedReasoningEffort,
                ActiveProviderName: null,
                ThinkingMode: normalizedThinkingMode);
        }
        catch (JsonException)
        {
            try
            {
                AgentConfiguration? configuration = JsonSerializer.Deserialize(
                    json,
                    OnboardingStorageJsonContext.Default.AgentConfiguration);

                if (configuration is null)
                {
                    return null;
                }

                AgentProviderProfile? normalizedProfile = NormalizeProfile(configuration.ProviderProfile);
                if (normalizedProfile is null)
                {
                    return null;
                }

                (string? normalizedThinkingMode, string? normalizedReasoningEffort) =
                    ReasoningOptions.NormalizeStoredValues(
                        configuration.ThinkingMode,
                        configuration.ReasoningEffort);

                return new AgentConfiguration(
                    normalizedProfile,
                    ModelIdMatcher.NormalizeOrNull(configuration.PreferredModelId),
                    normalizedReasoningEffort,
                    NormalizeProviderName(configuration.ActiveProviderName),
                    normalizedThinkingMode);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    private static AgentConfiguration? TryDeserializeLegacyConfiguration(string json)
    {
        try
        {
            AgentProviderProfile? legacyProfile = JsonSerializer.Deserialize(
                json,
                OnboardingStorageJsonContext.Default.AgentProviderProfile);

            AgentProviderProfile? normalizedLegacyProfile = NormalizeProfile(legacyProfile);
            return normalizedLegacyProfile is null
                ? null
                : new AgentConfiguration(normalizedLegacyProfile, null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static AgentConfiguration? TryCreateActiveProviderConfiguration(
        AgentProfileConfigurationDocument document)
    {
        string? activeProviderName = NormalizeProviderName(document.ActiveProviderName);
        if (activeProviderName is null ||
            document.Providers is null ||
            document.Providers.Count == 0)
        {
            return null;
        }

        (string Name, ProviderProfileConfigurationDocument Provider)? activeProvider = null;
        activeProvider = FindProvider(document, activeProviderName);

        if (activeProvider is null)
        {
            foreach (KeyValuePair<string, ProviderProfileConfigurationDocument> item in document.Providers)
            {
                string? candidateName = NormalizeProviderName(item.Key);
                if (candidateName is null || item.Value is null)
                {
                    continue;
                }

                activeProvider = (candidateName, item.Value);
                break;
            }
        }

        if (activeProvider is null)
        {
            return null;
        }

        AgentProviderProfile? normalizedProfile = NormalizeProfile(activeProvider.Value.Provider.ProviderProfile);
        if (normalizedProfile is null)
        {
            return null;
        }

        (string? normalizedThinkingMode, string? normalizedReasoningEffort) =
            ReasoningOptions.NormalizeStoredValues(
                document.ThinkingMode,
                document.ReasoningEffort);

        return new AgentConfiguration(
            normalizedProfile,
            ModelIdMatcher.NormalizeOrNull(activeProvider.Value.Provider.PreferredModelId),
            normalizedReasoningEffort,
            activeProvider.Value.Name,
            normalizedThinkingMode);
    }

    private static (string Name, ProviderProfileConfigurationDocument Provider)? FindProvider(
        AgentProfileConfigurationDocument document,
        string providerName)
    {
        string? normalizedProviderName = NormalizeProviderName(providerName);
        if (normalizedProviderName is null || document.Providers is null)
        {
            return null;
        }

        foreach (KeyValuePair<string, ProviderProfileConfigurationDocument> item in document.Providers)
        {
            string? candidateName = NormalizeProviderName(item.Key);
            if (candidateName is not null &&
                item.Value is not null &&
                string.Equals(candidateName, normalizedProviderName, StringComparison.OrdinalIgnoreCase))
            {
                return (candidateName, item.Value);
            }
        }

        return null;
    }

    private static string ResolveProviderName(
        AgentProfileConfigurationDocument document,
        AgentConfiguration configuration)
    {
        string? explicitName = NormalizeProviderName(configuration.ActiveProviderName);
        if (explicitName is not null)
        {
            return explicitName;
        }

        if (NormalizeProviderName(document.ActiveProviderName) is { } activeProviderName &&
            FindProvider(document, activeProviderName) is { } activeProvider &&
            Equals(NormalizeProfile(activeProvider.Provider.ProviderProfile), configuration.ProviderProfile))
        {
            return activeProvider.Name;
        }

        foreach (KeyValuePair<string, ProviderProfileConfigurationDocument> item in document.Providers ?? [])
        {
            string? name = NormalizeProviderName(item.Key);
            if (name is not null &&
                item.Value is not null &&
                Equals(NormalizeProfile(item.Value.ProviderProfile), configuration.ProviderProfile))
            {
                return name;
            }
        }

        return CreateUniqueProviderName(
            document,
            CreateProviderName(configuration.ProviderProfile));
    }

    private static string CreateUniqueProviderName(
        AgentProfileConfigurationDocument document,
        string baseName)
    {
        string normalizedBaseName = NormalizeProviderName(baseName) ?? "Provider";
        HashSet<string> existingNames = new(
            (document.Providers ?? [])
                .Select(static item => NormalizeProviderName(item.Key))
                .Where(static name => name is not null)
                .Select(static name => name!),
            StringComparer.OrdinalIgnoreCase);

        if (!existingNames.Contains(normalizedBaseName))
        {
            return normalizedBaseName;
        }

        for (int suffix = 2; suffix < int.MaxValue; suffix++)
        {
            string candidate = $"{normalizedBaseName} {suffix}";
            if (!existingNames.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not create a unique provider name.");
    }

    private static string CreateProviderName(AgentProviderProfile profile)
    {
        string providerName = profile.ProviderKind.ToDisplayName();
        if (profile.ProviderKind == ProviderKind.OpenAiCompatible &&
            Uri.TryCreate(profile.ResolveBaseUrl(), UriKind.Absolute, out Uri? uri) &&
            !string.IsNullOrWhiteSpace(uri.Host))
        {
            return $"{providerName} ({uri.Host})";
        }

        return providerName;
    }

    private static AgentConfiguration? LoadEnvironmentConfiguration()
    {
        string? providerName = NormalizeOptional(Environment.GetEnvironmentVariable(ProviderEnvironmentVariableName));
        if (providerName is null)
        {
            return null;
        }

        ProviderKind providerKind = ParseProviderKind(providerName);
        string? baseUrl = NormalizeOptional(Environment.GetEnvironmentVariable(BaseUrlEnvironmentVariableName));
        AgentProviderProfile profile = providerKind switch
        {
            ProviderKind.OpenAiCompatible => string.IsNullOrWhiteSpace(baseUrl)
                ? throw new InvalidOperationException(
                    $"{BaseUrlEnvironmentVariableName} must be set when {ProviderEnvironmentVariableName} is 'openai-compatible'.")
                : new AgentProviderProfile(
                    ProviderKind.OpenAiCompatible,
                    CompatibleProviderBaseUrlNormalizer.Normalize(baseUrl)),
            ProviderKind.LmStudio => new AgentProviderProfile(
                ProviderKind.LmStudio,
                string.IsNullOrWhiteSpace(baseUrl)
                    ? null
                    : CompatibleProviderBaseUrlNormalizer.Normalize(baseUrl)),
            _ => new AgentProviderProfile(providerKind, BaseUrl: null)
        };

        return new AgentConfiguration(
            profile,
            ModelIdMatcher.NormalizeOrNull(Environment.GetEnvironmentVariable(ModelEnvironmentVariableName)),
            ReasoningEffort: ReasoningEffortOptions.NormalizeOrThrow(
                Environment.GetEnvironmentVariable(ReasoningEnvironmentVariableName)),
            ActiveProviderName: CreateProviderName(profile),
            ThinkingMode: ThinkingModeOptions.NormalizeOrNull(
                Environment.GetEnvironmentVariable(ThinkingEnvironmentVariableName)));
    }

    private static ProviderKind ParseProviderKind(string providerName)
    {
        string normalized = new(
            providerName
                .Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());

        return normalized switch
        {
            "openai" => ProviderKind.OpenAi,
            "openaicompatible" or "compatible" or "custom" => ProviderKind.OpenAiCompatible,
            "googleaistudio" or "googleai" or "gemini" => ProviderKind.GoogleAiStudio,
            "anthropic" or "claude" => ProviderKind.Anthropic,
            "anthropicclaudeaccount" or "anthropicaccount" or "claudeaccount" or
                "claudepro" or "claudemax" or "claudepromax" => ProviderKind.AnthropicClaudeAccount,
            "githubcopilot" or "copilot" => ProviderKind.GitHubCopilot,
            "openrouter" => ProviderKind.OpenRouter,
            "kilocode" or "kilo" or "kiloai" => ProviderKind.KiloCode,
            "cerebras" => ProviderKind.Cerebras,
            "groq" => ProviderKind.Groq,
            "opencodezen" or "opencode" => ProviderKind.OpenCodeZen,
            "deepseek" => ProviderKind.DeepSeek,
            "ollama" => ProviderKind.Ollama,
            "lmstudio" => ProviderKind.LmStudio,
            "ollamacloud" => ProviderKind.OllamaCloud,
            "openaichatgptaccount" or "chatgpt" => ProviderKind.OpenAiChatGptAccount,
            _ => throw new InvalidOperationException(
                $"Unsupported {ProviderEnvironmentVariableName} value '{providerName}'. Supported values: openai, openai-compatible, google-ai-studio, anthropic, anthropic-claude-account, github-copilot, openrouter, kilo-code, cerebras, groq, opencode-zen, deepseek, ollama, lm-studio, ollama-cloud.")
        };
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }

    private static string? NormalizeProviderName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string normalized = new(
            value
                .Trim()
                .Where(static character => !char.IsControl(character))
                .ToArray());

        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : normalized;
    }

    private static AgentProviderProfile? NormalizeProfile(AgentProviderProfile? profile)
    {
        if (profile is null)
        {
            return null;
        }

        try
        {
            return profile.ProviderKind switch
            {
                ProviderKind.OpenAi => new AgentProviderProfile(ProviderKind.OpenAi, BaseUrl: null),
                ProviderKind.OpenAiChatGptAccount => new AgentProviderProfile(
                    ProviderKind.OpenAiChatGptAccount,
                    BaseUrl: null),
                ProviderKind.AnthropicClaudeAccount => new AgentProviderProfile(
                    ProviderKind.AnthropicClaudeAccount,
                    BaseUrl: null),
                ProviderKind.GitHubCopilot => new AgentProviderProfile(
                    ProviderKind.GitHubCopilot,
                    BaseUrl: null),
                ProviderKind.OpenRouter => new AgentProviderProfile(ProviderKind.OpenRouter, BaseUrl: null),
                ProviderKind.KiloCode => new AgentProviderProfile(ProviderKind.KiloCode, BaseUrl: null),
                ProviderKind.Cerebras => new AgentProviderProfile(ProviderKind.Cerebras, BaseUrl: null),
                ProviderKind.Groq => new AgentProviderProfile(ProviderKind.Groq, BaseUrl: null),
                ProviderKind.OpenCodeZen => new AgentProviderProfile(ProviderKind.OpenCodeZen, BaseUrl: null),
                ProviderKind.DeepSeek => new AgentProviderProfile(ProviderKind.DeepSeek, BaseUrl: null),
                ProviderKind.Ollama => new AgentProviderProfile(ProviderKind.Ollama, BaseUrl: null),
                ProviderKind.LmStudio => new AgentProviderProfile(
                    ProviderKind.LmStudio,
                    string.IsNullOrWhiteSpace(profile.BaseUrl)
                        ? null
                        : CompatibleProviderBaseUrlNormalizer.Normalize(profile.BaseUrl)),
                ProviderKind.OllamaCloud => new AgentProviderProfile(ProviderKind.OllamaCloud, BaseUrl: null),
                ProviderKind.GoogleAiStudio => new AgentProviderProfile(ProviderKind.GoogleAiStudio, BaseUrl: null),
                ProviderKind.Anthropic => new AgentProviderProfile(ProviderKind.Anthropic, BaseUrl: null),
                ProviderKind.OpenAiCompatible when !string.IsNullOrWhiteSpace(profile.BaseUrl)
                    => new AgentProviderProfile(
                        ProviderKind.OpenAiCompatible,
                        CompatibleProviderBaseUrlNormalizer.Normalize(profile.BaseUrl)),
                _ => null
            };
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
