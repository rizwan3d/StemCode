using Microsoft.Extensions.DependencyInjection;
using StemCode.Application.Abstractions;
using StemCode.Application.Conversation.Services;
using StemCode.Application.Formatting;
using StemCode.Application.Permissions;
using StemCode.Application.Profiles;
using StemCode.Application.Services;
using StemCode.Application.Tools;
using StemCode.Application.Tools.Services;
using StemCode.Domain.Abstractions;
using StemCode.Domain.Services;

namespace StemCode.Application.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IAgentProfileResolver, BuiltInAgentProfileResolver>();
        services.AddSingleton<IProductTelemetry, NoOpProductTelemetry>();
        services.AddSingleton<AgentTurnService>();
        services.AddSingleton<IOutputStyleService, OutputStyleService>();
        services.AddSingleton<ISessionAppService, SessionAppService>();
        services.AddSingleton<IConversationPipeline, AgentConversationPipeline>();
        services.AddSingleton<IToolOutputFormatter, ToolOutputFormatter>();
        services.AddSingleton<ILifecycleHookService, NoOpLifecycleHookService>();
        services.AddSingleton<ToolPermissionParser>();
        services.AddSingleton<ToolPermissionEvaluator>();
        services.AddSingleton<IPermissionApprovalPrompt, SelectionPermissionApprovalPrompt>();
        services.AddSingleton<IToolRegistry, ToolRegistry>();
        services.AddSingleton<IToolInvoker, RegistryBackedToolInvoker>();
        services.AddSingleton<IToolExecutionPipeline, ToolExecutionPipeline>();
        services.AddRegisteredTools();
        services.AddSingleton<IModelDiscoveryService, ModelDiscoveryService>();
        services.AddSingleton<IFirstRunOnboardingService, FirstRunOnboardingService>();
        services.AddSingleton<IProviderSetupService, ProviderSetupService>();
        services.AddSingleton<IOnboardingInputValidator, OnboardingInputValidator>();
        services.AddSingleton<IModelActivationService, ModelActivationService>();
        services.AddSingleton<IInteractiveReasoningSelectionService, InteractiveReasoningSelectionService>();
        services.AddSingleton<IInteractiveModelSelectionService, InteractiveModelSelectionService>();
        services.AddSingleton<IReplSectionService, ReplSectionService>();
        services.AddSingleton<ITokenEstimator, HeuristicTokenEstimator>();
        services.AddSingleton<IAgentProviderProfileFactory, AgentProviderProfileFactory>();
        services.AddSingleton<IModelSelectionPolicy, ConfiguredOrFirstModelSelectionPolicy>();

        return services;
    }

    private sealed class NoOpProductTelemetry : IProductTelemetry
    {
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
            Models.ConversationTurnMetrics? metrics = null,
            int attachmentCount = 0,
            Exception? exception = null)
        {
        }

        public void TrackToolInvoked(
            string toolName,
            Models.ToolResultStatus status,
            bool success,
            TimeSpan duration,
            Models.ConversationExecutionPhase executionPhase,
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
        {
        }
    }
}
