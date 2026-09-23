using Microsoft.Extensions.DependencyInjection;
using StemCode.Application.Abstractions;
using StemCode.Application.Backend;
using StemCode.Application.Models;
using StemCode.Domain.Models;
using StemCode.Infrastructure.Configuration;
using StemCode.Sdk.Internal;

namespace StemCode.Sdk;

/// <summary>
/// Fluent builder for a <see cref="StemCodeClient"/>. Configure a provider and
/// model, optionally a workspace, agent profile, custom tools, MCP servers, and
/// DI overrides, then call <see cref="Build"/>.
/// </summary>
public sealed class StemCodeClientBuilder
{
    private readonly List<ITool> _toolInstances = [];
    private readonly List<Func<IServiceProvider, ITool>> _toolFactories = [];
    private readonly List<IDynamicToolProvider> _toolProviders = [];
    private readonly List<BackendMcpServerConfiguration> _mcpServers = [];
    private readonly List<Action<IServiceCollection>> _serviceConfigurations = [];

    private AgentProviderProfile? _explicitProfile;
    private ProviderKind? _providerKind;
    private string? _baseUrl;
    private string? _apiKey;
    private string? _model;
    private string? _workspace;
    private string? _profileName;
    private string? _thinkingMode;
    private string? _sectionId;
    private ConversationSystemPromptMode? _systemPromptMode;
    private string? _systemPrompt;
    private bool _autoApproveTools;
    private IAgentInteractionHandler? _interactionHandler;

    // --- Providers -------------------------------------------------------

    /// <summary>Use Anthropic (Claude) with an API key.</summary>
    public StemCodeClientBuilder UseAnthropic(string apiKey, string? model = null)
    {
        return SetProvider(ProviderKind.Anthropic, apiKey, baseUrl: null, model);
    }

    /// <summary>Use the official OpenAI API with an API key.</summary>
    public StemCodeClientBuilder UseOpenAi(string apiKey, string? model = null)
    {
        return SetProvider(ProviderKind.OpenAi, apiKey, baseUrl: null, model);
    }

    /// <summary>Use Google AI Studio (Gemini) with an API key.</summary>
    public StemCodeClientBuilder UseGoogleAiStudio(string apiKey, string? model = null)
    {
        return SetProvider(ProviderKind.GoogleAiStudio, apiKey, baseUrl: null, model);
    }

    /// <summary>Use OpenRouter with an API key.</summary>
    public StemCodeClientBuilder UseOpenRouter(string apiKey, string? model = null)
    {
        return SetProvider(ProviderKind.OpenRouter, apiKey, baseUrl: null, model);
    }

    /// <summary>Use DeepSeek with an API key.</summary>
    public StemCodeClientBuilder UseDeepSeek(string apiKey, string? model = null)
    {
        return SetProvider(ProviderKind.DeepSeek, apiKey, baseUrl: null, model);
    }

    /// <summary>Use Groq with an API key.</summary>
    public StemCodeClientBuilder UseGroq(string apiKey, string? model = null)
    {
        return SetProvider(ProviderKind.Groq, apiKey, baseUrl: null, model);
    }

    /// <summary>Use Cerebras with an API key.</summary>
    public StemCodeClientBuilder UseCerebras(string apiKey, string? model = null)
    {
        return SetProvider(ProviderKind.Cerebras, apiKey, baseUrl: null, model);
    }

    /// <summary>Use a local Ollama server. No API key is required.</summary>
    public StemCodeClientBuilder UseOllama(string? baseUrl = null, string? model = null)
    {
        return SetProvider(ProviderKind.Ollama, apiKey: null, baseUrl, model);
    }

    /// <summary>Use a local LM Studio server. No API key is required.</summary>
    public StemCodeClientBuilder UseLmStudio(string? baseUrl = null, string? model = null)
    {
        return SetProvider(ProviderKind.LmStudio, apiKey: null, baseUrl, model);
    }

    /// <summary>Use any OpenAI-compatible endpoint with a base URL and API key.</summary>
    public StemCodeClientBuilder UseOpenAiCompatible(string baseUrl, string apiKey, string? model = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        return SetProvider(ProviderKind.OpenAiCompatible, apiKey, baseUrl, model);
    }

    /// <summary>Use an explicitly constructed provider profile.</summary>
    public StemCodeClientBuilder UseProvider(
        AgentProviderProfile profile,
        string? apiKey = null,
        string? model = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _explicitProfile = profile;
        _providerKind = profile.ProviderKind;
        _baseUrl = profile.BaseUrl;
        _apiKey = Normalize(apiKey) ?? _apiKey;
        _model = Normalize(model) ?? _model;
        return this;
    }

    // --- Session / model options ----------------------------------------

    /// <summary>Sets the preferred model id (overrides any model passed to a Use* method).</summary>
    public StemCodeClientBuilder WithModel(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        _model = modelId.Trim();
        return this;
    }

    /// <summary>Pins the agent's workspace root to a specific directory.</summary>
    public StemCodeClientBuilder WithWorkspace(string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        _workspace = workspacePath.Trim();
        return this;
    }

    /// <summary>Selects a built-in agent profile (for example "build" or "plan").</summary>
    public StemCodeClientBuilder WithProfile(string profileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        _profileName = profileName.Trim();
        return this;
    }

    /// <summary>
    /// Uses StemCode's full build-tool agent preset: repository editing,
    /// filesystem/search, shell execution, browser/web search, planning, memory,
    /// code intelligence, and subagent orchestration tools.
    /// </summary>
    public StemCodeClientBuilder UseBuildTool()
    {
        _profileName = StemCodeBuildTools.ProfileName;
        return this;
    }

    /// <summary>Sets the thinking/reasoning mode (for example "on" or "off").</summary>
    public StemCodeClientBuilder WithThinkingMode(string thinkingMode)
    {
        _thinkingMode = ReasoningEffortOptions.NormalizeOrThrow(thinkingMode);
        return this;
    }

    /// <summary>Resumes a previously persisted session/section by id instead of creating a new one.</summary>
    public StemCodeClientBuilder ResumeSession(string sectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionId);
        _sectionId = sectionId.Trim();
        return this;
    }

    /// <summary>Uses a custom base system prompt for this SDK client.</summary>
    public StemCodeClientBuilder WithSystemPrompt(string systemPrompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);
        _systemPromptMode = ConversationSystemPromptMode.Custom;
        _systemPrompt = systemPrompt.Trim();
        return this;
    }

    /// <summary>Uses StemCode's built-in coding-agent base system prompt.</summary>
    public StemCodeClientBuilder UseStemCodeSystemPrompt()
    {
        _systemPromptMode = ConversationSystemPromptMode.StemCode;
        _systemPrompt = null;
        return this;
    }

    /// <summary>Uses no configured base system prompt. This is the SDK default.</summary>
    public StemCodeClientBuilder WithoutSystemPrompt()
    {
        _systemPromptMode = ConversationSystemPromptMode.None;
        _systemPrompt = null;
        return this;
    }

    /// <summary>
    /// Auto-approves every tool execution. Use only for trusted, sandboxed, or
    /// fully automated scenarios — it bypasses interactive permission prompts.
    /// </summary>
    public StemCodeClientBuilder AutoApproveTools()
    {
        _autoApproveTools = true;
        return this;
    }

    // --- Extensibility ---------------------------------------------------

    /// <summary>Registers a custom tool instance the agent can call.</summary>
    public StemCodeClientBuilder AddTool(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        _toolInstances.Add(tool);
        return this;
    }

    /// <summary>Registers a custom tool resolved from the DI container.</summary>
    public StemCodeClientBuilder AddTool(Func<IServiceProvider, ITool> toolFactory)
    {
        ArgumentNullException.ThrowIfNull(toolFactory);
        _toolFactories.Add(toolFactory);
        return this;
    }

    /// <summary>Registers a provider that contributes tools dynamically.</summary>
    public StemCodeClientBuilder AddToolProvider(IDynamicToolProvider toolProvider)
    {
        ArgumentNullException.ThrowIfNull(toolProvider);
        _toolProviders.Add(toolProvider);
        return this;
    }

    /// <summary>Adds a Model Context Protocol (MCP) server available to this session.</summary>
    public StemCodeClientBuilder AddMcpServer(BackendMcpServerConfiguration mcpServer)
    {
        ArgumentNullException.ThrowIfNull(mcpServer);
        _mcpServers.Add(mcpServer);
        return this;
    }

    /// <summary>
    /// Escape hatch to register or override any service in the agent's DI
    /// container. Runs after all built-in registrations, so it can replace them
    /// (for example to inject a fake provider client in tests).
    /// </summary>
    public StemCodeClientBuilder ConfigureServices(Action<IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _serviceConfigurations.Add(configure);
        return this;
    }

    /// <summary>Supplies a handler that answers interactive prompts programmatically.</summary>
    public StemCodeClientBuilder UseInteractionHandler(IAgentInteractionHandler interactionHandler)
    {
        ArgumentNullException.ThrowIfNull(interactionHandler);
        _interactionHandler = interactionHandler;
        return this;
    }

    // --- Build -----------------------------------------------------------

    /// <summary>Validates the configuration and creates a ready-to-initialize client.</summary>
    public StemCodeClient Build()
    {
        if (_explicitProfile is null && _providerKind is null)
        {
            throw new InvalidOperationException(
                "A provider must be configured. Call one of the Use* methods (for example UseAnthropic, UseOpenAi, or UseProvider) before Build().");
        }

        AgentProviderProfile profile = _explicitProfile
            ?? new AgentProviderProfile(_providerKind!.Value, _baseUrl);
        string providerName = profile.ProviderKind.ToDisplayName();
        string apiKey = ResolveApiKey(profile.ProviderKind, providerName);

        AgentConfiguration configuration = new(
            profile,
            PreferredModelId: _model,
            ReasoningEffort: _thinkingMode,
            ActiveProviderName: providerName);

        Action<IServiceCollection> configureServices = services =>
        {
            services.AddSingleton<IAgentConfigurationStore>(new InMemoryAgentConfigurationStore(configuration));
            services.AddSingleton<IApiKeySecretStore>(new InMemoryApiKeySecretStore(apiKey));
            services.PostConfigure<ApplicationOptions>(options =>
            {
                options.Conversation ??= new ConversationOptions();
                options.Conversation.SystemPromptMode = _systemPromptMode ?? ConversationSystemPromptMode.None;
                options.Conversation.SystemPrompt = _systemPrompt;
            });

            if (_workspace is not null)
            {
                services.AddSingleton<IWorkspaceRootProvider>(new FixedWorkspaceRootProvider(_workspace));
            }

            foreach (ITool tool in _toolInstances)
            {
                services.AddSingleton(tool);
            }

            foreach (Func<IServiceProvider, ITool> toolFactory in _toolFactories)
            {
                services.AddSingleton(toolFactory);
            }

            foreach (IDynamicToolProvider toolProvider in _toolProviders)
            {
                services.AddSingleton(toolProvider);
            }

            // User overrides run last so they can replace any built-in registration.
            foreach (Action<IServiceCollection> userConfigure in _serviceConfigurations)
            {
                userConfigure(services);
            }
        };

        BackendRuntimeArguments runtimeArguments = BackendRuntimeArguments.Parse(BuildArgs());
        StemCodeBackend backend = new(
            runtimeArguments,
            _mcpServers,
            _autoApproveTools,
            configureServices);

        return new StemCodeClient(backend, _interactionHandler);
    }

    private StemCodeClientBuilder SetProvider(
        ProviderKind providerKind,
        string? apiKey,
        string? baseUrl,
        string? model)
    {
        _explicitProfile = null;
        _providerKind = providerKind;
        _apiKey = Normalize(apiKey);
        _baseUrl = Normalize(baseUrl);
        if (Normalize(model) is { } normalizedModel)
        {
            _model = normalizedModel;
        }

        return this;
    }

    private string ResolveApiKey(ProviderKind providerKind, string providerName)
    {
        string? apiKey = _apiKey ?? providerKind.GetDefaultApiKey();
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            return apiKey;
        }

        if (IsKeylessLocalProvider(providerKind))
        {
            // Local providers ignore the key; seed a non-empty placeholder so the
            // onboarding flow treats the configuration as complete and stays headless.
            return "local";
        }

        throw new InvalidOperationException(
            $"An API key is required for {providerName}. Supply one when selecting the provider.");
    }

    private static bool IsKeylessLocalProvider(ProviderKind providerKind)
    {
        return providerKind is ProviderKind.Ollama or ProviderKind.LmStudio;
    }

    private string[] BuildArgs()
    {
        List<string> args = ["--no-update-check"];

        if (_profileName is not null)
        {
            args.Add("--profile");
            args.Add(_profileName);
        }

        if (_thinkingMode is not null)
        {
            args.Add("--thinking");
            args.Add(_thinkingMode);
        }

        if (_sectionId is not null)
        {
            args.Add("--section");
            args.Add(_sectionId);
        }

        return [.. args];
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
