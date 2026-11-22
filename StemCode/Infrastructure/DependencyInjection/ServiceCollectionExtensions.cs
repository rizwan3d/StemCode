using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StemCode.Application.Abstractions;
using StemCode.Application.Backend;
using StemCode.Application.Models;
using StemCode.Infrastructure.BudgetControls;
using StemCode.Infrastructure.Anthropic;
using StemCode.Infrastructure.CodeIntelligence;
using StemCode.Infrastructure.Configuration;
using StemCode.Infrastructure.Conversation;
using StemCode.Infrastructure.CustomTools;
using StemCode.Infrastructure.Git;
using StemCode.Infrastructure.GitHub;
using StemCode.Infrastructure.Hooks;
using StemCode.Infrastructure.Logging;
using StemCode.Infrastructure.Mcp;
using StemCode.Infrastructure.Models;
using StemCode.Infrastructure.StemCodeEnterprise;
using StemCode.Infrastructure.OpenAi;
using StemCode.Infrastructure.Plugins;
using StemCode.Infrastructure.Secrets;
using StemCode.Infrastructure.Storage;
using StemCode.Infrastructure.Telemetry;
using StemCode.Infrastructure.Tools;
using StemCode.Infrastructure.Updates;
using StemCode.Infrastructure.WindowsSandbox;

namespace StemCode.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<IUserDataPathProvider, UserDataPathProvider>();
        services.AddSingleton<IConversationSectionStore, JsonConversationSectionStore>();
        services.AddSingleton<ISessionStore, JsonSessionStore>();
        services.AddSingleton<ISessionEventLogService, JsonSessionEventLogService>();
        services.AddSingleton<IWorkspaceRootProvider, CurrentDirectoryWorkspaceRootProvider>();
        services.AddSingleton<ProviderRequestProjectHeaderProvider>();
        services.AddSingleton(static serviceProvider =>
            AgentProfileConfigurationReader.LoadMemorySettings(
                serviceProvider.GetRequiredService<IUserDataPathProvider>(),
                serviceProvider.GetRequiredService<IWorkspaceRootProvider>()));
        services.AddSingleton(static serviceProvider =>
            AgentProfileConfigurationReader.LoadCodebaseIndexSettings(
                serviceProvider.GetRequiredService<IUserDataPathProvider>(),
                serviceProvider.GetRequiredService<IWorkspaceRootProvider>()));
        services.AddSingleton(static serviceProvider =>
            AgentProfileConfigurationReader.LoadToolAuditSettings(
                serviceProvider.GetRequiredService<IUserDataPathProvider>(),
                serviceProvider.GetRequiredService<IWorkspaceRootProvider>()));
        services.AddSingleton(static serviceProvider =>
            AgentProfileConfigurationReader.LoadGitAutomationSettings(
                serviceProvider.GetRequiredService<IUserDataPathProvider>(),
                serviceProvider.GetRequiredService<IWorkspaceRootProvider>()));
        services.AddSingleton(static serviceProvider =>
            ApplicationSettingsFactory.CreateToolExecutionSettings(
                serviceProvider.GetRequiredService<IOptions<ApplicationOptions>>().Value));
        services.AddSingleton<IWorkspaceFileService, WorkspaceFileService>();
        services.AddSingleton<IWorkspaceSettingsWriter, WorkspaceSettingsWriter>();
        services.AddSingleton<ITerminalSettingsStore, TerminalSettingsStore>();
        services.AddSingleton<ICodebaseIndexService, WorkspaceCodebaseIndexService>();
        services.AddSingleton<ILanguageServerRegistry, LanguageServerRegistry>();
        services.AddSingleton<ICodeIntelligenceService, LspCodeIntelligenceService>();
        services.AddSingleton<IHeadlessBrowserService, HeadlessBrowserService>();
        services.AddSingleton<IWorkspaceSystemPromptProvider, WorkspaceSystemPromptProvider>();
        services.AddSingleton<IWorkspaceAgentProfilePromptProvider, WorkspaceAgentProfilePromptProvider>();
        services.AddSingleton<IWorkspaceInstructionsProvider, WorkspaceInstructionsProvider>();
        services.AddSingleton<ISkillService, WorkspaceSkillService>();
        services.AddSingleton<ILifecycleHookService, ShellLifecycleHookService>();
        services.AddSingleton<ILessonFailureClassifier, LessonFailureClassifier>();
        services.AddSingleton<ILessonMemoryService, WorkspaceLessonMemoryService>();
        services.AddSingleton<IToolAuditLogService, WorkspaceToolAuditLogService>();
        services.AddSingleton<SandboxNetworkProxy>();
        services.AddSingleton<IShellCommandService, ShellCommandService>();
        services.AddSingleton<IAutoCommitService, GitAutoCommitService>();
        services.AddSingleton<IGitWorktreeService, GitWorktreeService>();
        services.AddSingleton<CustomToolProcessExecutor>();
        services.AddSingleton<StemCodeMcpConfigLoader>();
        services.AddSingleton<IDynamicToolProvider, CustomToolDynamicProvider>();
        services.AddSingleton<McpOAuthTokenStore>();
        services.AddSingleton<McpDynamicToolProvider>();
        services.AddSingleton<IMcpAuthService, McpOAuthService>();
        services.AddSingleton<IDynamicToolProvider>(static serviceProvider => serviceProvider.GetRequiredService<McpDynamicToolProvider>());
        services.AddSingleton<IMcpPromptService>(static serviceProvider => serviceProvider.GetRequiredService<McpDynamicToolProvider>());
        services.AddSingleton(static serviceProvider =>
            ApplicationSettingsFactory.CreatePermissionSettings(
                serviceProvider.GetRequiredService<IOptions<ApplicationOptions>>().Value,
                serviceProvider.GetService<BackendRuntimeOptions>()?.AutoApproveAllTools == true));
        services.AddHttpClient<IWebSearchService, WebSearchService>((serviceProvider, client) =>
        {
            ConfigureHttpClient(
                client,
                serviceProvider.GetRequiredService<ToolExecutionSettings>(),
                TimeSpan.FromSeconds(20));
        });
        services.AddHttpClient<IApplicationUpdateService, GitHubApplicationUpdateService>((serviceProvider, client) =>
        {
            ConfigureHttpClient(
                client,
                serviceProvider.GetRequiredService<ToolExecutionSettings>(),
                TimeSpan.FromSeconds(20));
        });
        services.AddHttpClient<IBudgetControlsUsageService, BudgetControlsUsageService>((serviceProvider, client) =>
        {
            ConfigureHttpClient(
                client,
                serviceProvider.GetRequiredService<ToolExecutionSettings>(),
                TimeSpan.FromSeconds(20));
        });
        services.AddHttpClient<IPluginService, PluginService>((serviceProvider, client) =>
        {
            ConfigureHttpClient(
                client,
                serviceProvider.GetRequiredService<ToolExecutionSettings>(),
                TimeSpan.FromSeconds(20));
        });
        services.AddHttpClient<IOpenAiCodexClientVersionProvider, GitHubOpenAiCodexClientVersionProvider>((serviceProvider, client) =>
        {
            ConfigureHttpClient(
                client,
                serviceProvider.GetRequiredService<ToolExecutionSettings>(),
                TimeSpan.FromSeconds(10));
        });
        services.AddHttpClient<OpenAiChatGptAccountCredentialService>((serviceProvider, client) =>
        {
            ConfigureHttpClient(
                client,
                serviceProvider.GetRequiredService<ToolExecutionSettings>(),
                TimeSpan.FromSeconds(30));
        });
        services.AddTransient<AnthropicClaudeAccountCredentialService>();
        services.AddHttpClient<GitHubCopilotCredentialService>((serviceProvider, client) =>
        {
            ConfigureHttpClient(
                client,
                serviceProvider.GetRequiredService<ToolExecutionSettings>(),
                TimeSpan.FromSeconds(30));
        });
        services.AddHttpClient<StemCodeEnterpriseCredentialService>((serviceProvider, client) =>
        {
            ConfigureHttpClient(
                client,
                serviceProvider.GetRequiredService<ToolExecutionSettings>(),
                TimeSpan.FromSeconds(30));
        });
        services.AddTransient<IOpenAiChatGptAccountCredentialService>(serviceProvider =>
            serviceProvider.GetRequiredService<OpenAiChatGptAccountCredentialService>());
        services.AddTransient<IOpenAiChatGptAccountAuthenticator>(serviceProvider =>
            serviceProvider.GetRequiredService<OpenAiChatGptAccountCredentialService>());
        services.AddTransient<IAnthropicClaudeAccountCredentialService>(serviceProvider =>
            serviceProvider.GetRequiredService<AnthropicClaudeAccountCredentialService>());
        services.AddTransient<IAnthropicClaudeAccountAuthenticator>(serviceProvider =>
            serviceProvider.GetRequiredService<AnthropicClaudeAccountCredentialService>());
        services.AddTransient<IGitHubCopilotCredentialService>(serviceProvider =>
            serviceProvider.GetRequiredService<GitHubCopilotCredentialService>());
        services.AddTransient<IGitHubCopilotAuthenticator>(serviceProvider =>
            serviceProvider.GetRequiredService<GitHubCopilotCredentialService>());
        services.AddTransient<IStemCodeEnterpriseCredentialService>(serviceProvider =>
            serviceProvider.GetRequiredService<StemCodeEnterpriseCredentialService>());
        services.AddTransient<IStemCodeEnterpriseAuthenticator>(serviceProvider =>
            serviceProvider.GetRequiredService<StemCodeEnterpriseCredentialService>());
        services.AddHttpClient("StemCode.Mcp", (serviceProvider, client) =>
        {
            ConfigureHttpClient(
                client,
                serviceProvider.GetRequiredService<ToolExecutionSettings>(),
                Timeout.InfiniteTimeSpan);
        });
        services.AddSingleton<IAgentConfigurationStore, JsonAgentConfigurationStore>();
        services.AddSingleton<IApiKeySecretStore, ApiKeySecretStore>();
        services.AddSingleton<IBudgetControlsConfigurationStore, JsonBudgetControlsConfigurationStore>();
        services.AddSingleton<IBudgetControlsSecretStore, BudgetControlsSecretStore>();
        services.AddSingleton<IModelCache, InMemoryModelCache>();
        services.AddSingleton<IConversationConfigurationAccessor, ConversationConfigurationAccessor>();
        services.AddSingleton<IConversationResponseMapper, OpenAiConversationResponseMapper>();
        services.AddSingleton(static serviceProvider =>
            ApplicationSettingsFactory.CreateModelSelectionSettings(
                serviceProvider.GetRequiredService<IOptions<ApplicationOptions>>().Value));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IPlatformCredentialStore>(CreatePlatformCredentialStore());
        services.AddSingleton<ILoggerProvider, DailyFileLoggerProvider>();
        services.AddHttpClient<PostHogTelemetryService>((serviceProvider, client) =>
        {
            ConfigureHttpClient(
                client,
                serviceProvider.GetRequiredService<ToolExecutionSettings>(),
                TimeSpan.FromSeconds(5));
            client.DefaultRequestHeaders.UserAgent.Clear();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("telemetry-client/1.0");
        });
        services.AddSingleton<IProductTelemetry>(serviceProvider =>
            serviceProvider.GetRequiredService<PostHogTelemetryService>());
        services.AddSingleton<IValidateOptions<ApplicationOptions>, ApplicationOptionsValidator>();
        services.AddHttpClient<IConversationProviderClient, OpenAiCompatibleConversationProviderClient>((serviceProvider, client) =>
        {
            client.Timeout = ResolveHttpClientTimeout(
                serviceProvider.GetRequiredService<ToolExecutionSettings>(),
                Timeout.InfiniteTimeSpan);
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromSeconds(90),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        });
        services.AddHttpClient<IModelProviderClient, OpenAiCompatibleModelProviderClient>((serviceProvider, client) =>
        {
            client.Timeout = ResolveHttpClientTimeout(
                serviceProvider.GetRequiredService<ToolExecutionSettings>(),
                Timeout.InfiniteTimeSpan);
        });
        services.AddSingleton<IWindowsSandboxPlatform, WindowsSandboxPlatform>();
        services.AddSingleton<IWindowsSandboxSetupBootstrapper, WindowsSandboxSetupBootstrapper>();
        services.AddSingleton<IWindowsSandboxStartupService, WindowsSandboxStartupService>();
        services.AddSingleton<IWindowsSandboxProcessRunner, WindowsSandboxProcessRunnerAdapter>();

        services.AddSingleton<IStemCodeInstanceService, StemCodeInstanceService>();

        services
            .AddOptions<ApplicationOptions>()
            .BindConfiguration(ApplicationOptions.SectionName, binderOptions =>
            {
                binderOptions.ErrorOnUnknownConfiguration = true;
            })
            .ValidateOnStart();

        return services;
    }

    private static Func<IServiceProvider, IPlatformCredentialStore> CreatePlatformCredentialStore()
    {
        if (OperatingSystem.IsWindows())
        {
            return _ => new WindowsCredentialStore();
        }

        if (OperatingSystem.IsMacOS())
        {
            return _ => new MacOsKeychainCredentialStore();
        }

        if (OperatingSystem.IsLinux())
        {
            return serviceProvider => new LinuxSecretToolCredentialStore(
                serviceProvider.GetRequiredService<IProcessRunner>());
        }

        return _ => new UnsupportedPlatformCredentialStore();
    }

    private static void ConfigureHttpClient(
        HttpClient client,
        ToolExecutionSettings settings,
        TimeSpan defaultTimeout)
    {
        client.Timeout = ResolveHttpClientTimeout(settings, defaultTimeout);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("StemCode/1.0");
    }

    private static TimeSpan ResolveHttpClientTimeout(
        ToolExecutionSettings settings,
        TimeSpan defaultTimeout)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return settings.HttpClientTimeoutSeconds > 0
            ? TimeSpan.FromSeconds(settings.HttpClientTimeoutSeconds)
            : defaultTimeout;
    }
}
