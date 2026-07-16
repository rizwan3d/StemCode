using StemCode.Application.Abstractions;
using StemCode.Application.Exceptions;
using StemCode.Application.Models;
using StemCode.Domain.Models;

namespace StemCode.Application.Services;

internal sealed class ProviderSetupService : IProviderSetupService
{
    private readonly IAgentConfigurationStore _configurationStore;
    private readonly IApiKeySecretStore _secretStore;
    private readonly IConfirmationPrompt _confirmationPrompt;
    private readonly IFirstRunOnboardingService _onboardingService;
    private readonly IModelDiscoveryService _modelDiscoveryService;
    private readonly ISelectionPrompt _selectionPrompt;
    private readonly IStatusMessageWriter _statusMessageWriter;

    public ProviderSetupService(
        IFirstRunOnboardingService onboardingService,
        IModelDiscoveryService modelDiscoveryService,
        IConfirmationPrompt confirmationPrompt,
        IStatusMessageWriter statusMessageWriter,
        IAgentConfigurationStore configurationStore,
        IApiKeySecretStore secretStore,
        ISelectionPrompt selectionPrompt)
    {
        _onboardingService = onboardingService;
        _modelDiscoveryService = modelDiscoveryService;
        _confirmationPrompt = confirmationPrompt;
        _statusMessageWriter = statusMessageWriter;
        _configurationStore = configurationStore;
        _secretStore = secretStore;
        _selectionPrompt = selectionPrompt;
    }

    public async Task<OnboardingResult> EnsureOnboardedAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await _onboardingService.EnsureOnboardedAsync(cancellationToken);
        }
        catch (Exception exception) when (ShouldOfferOnboardingRetry(exception))
        {
            await _statusMessageWriter.ShowErrorAsync(
                $"Provider setup could not be completed: {exception.Message}",
                cancellationToken);

            bool shouldReconfigure = await _confirmationPrompt.PromptAsync(
                new ConfirmationPromptRequest(
                    "Provider setup failed. Re-run onboarding?",
                    "Choose Yes to try provider setup again, or No to stop startup.",
                    DefaultValue: true),
                cancellationToken);

            if (!shouldReconfigure)
            {
                throw;
            }

            return await _onboardingService.ReconfigureAsync(cancellationToken);
        }
    }

    public async Task<ProviderSetupResult> EnsureConfiguredAsync(
        CancellationToken cancellationToken)
    {
        OnboardingResult onboardingResult = await EnsureOnboardedAsync(cancellationToken);
        ProviderSetupRecoveryChoice? recoveryChoice = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (recoveryChoice is not null)
                {
                    onboardingResult = recoveryChoice.Provider is null
                        ? await _onboardingService.ReconfigureAsync(cancellationToken)
                        : await UseSavedProviderAsync(
                            recoveryChoice.Provider,
                            onboardingResult,
                            cancellationToken);
                }

                return new ProviderSetupResult(
                    onboardingResult,
                    await _modelDiscoveryService.DiscoverAndSelectAsync(cancellationToken));
            }
            catch (Exception exception) when (ShouldOfferModelValidationRetry(exception))
            {
                await _statusMessageWriter.ShowErrorAsync(
                    $"Provider setup could not be validated: {exception.Message}",
                    cancellationToken);

                recoveryChoice = await PromptForProviderRecoveryAsync(
                    onboardingResult,
                    exception,
                    cancellationToken);
            }
        }
    }

    private async Task<ProviderSetupRecoveryChoice> PromptForProviderRecoveryAsync(
        OnboardingResult onboardingResult,
        Exception validationException,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SavedProviderConfiguration> savedProviders =
            await _configurationStore.ListProvidersAsync(cancellationToken);
        SavedProviderConfiguration[] alternateProviders = savedProviders
            .Where(provider => !IsActiveProvider(provider, onboardingResult))
            .ToArray();

        if (alternateProviders.Length == 0)
        {
            bool shouldReconfigure = await _confirmationPrompt.PromptAsync(
                new ConfirmationPromptRequest(
                    "Provider setup failed. Re-run onboarding?",
                    "Choose Yes to reconfigure provider credentials now, or No to stop startup.",
                    DefaultValue: true),
                cancellationToken);

            if (!shouldReconfigure)
            {
                throw validationException;
            }

            return ProviderSetupRecoveryChoice.ConfigureNewProvider;
        }

        SelectionPromptOption<ProviderSetupRecoveryChoice>[] options =
        [
            .. alternateProviders.Select(static provider =>
                new SelectionPromptOption<ProviderSetupRecoveryChoice>(
                    provider.Name,
                    new ProviderSetupRecoveryChoice(provider),
                    $"{provider.ProviderProfile.ProviderKind.ToDisplayName()} - use this saved provider.",
                    "Saved providers")),
            new(
                "Configure a new provider",
                ProviderSetupRecoveryChoice.ConfigureNewProvider,
                "Enter new provider credentials instead.",
                "Setup")
        ];

        return await _selectionPrompt.PromptAsync(
            new SelectionPromptRequest<ProviderSetupRecoveryChoice>(
                "Provider setup failed. Choose another provider",
                options,
                "The active provider failed validation. Select a saved provider or configure a new one.",
                AllowCancellation: true),
            cancellationToken);
    }

    private async Task<OnboardingResult> UseSavedProviderAsync(
        SavedProviderConfiguration provider,
        OnboardingResult previousResult,
        CancellationToken cancellationToken)
    {
        string? providerSecret = await _secretStore.LoadAsync(provider.Name, cancellationToken);
        providerSecret ??= provider.ProviderProfile.ProviderKind.GetDefaultApiKey();
        if (string.IsNullOrWhiteSpace(providerSecret))
        {
            throw new ModelDiscoveryException(
                $"Provider '{provider.Name}' is missing credentials. Configure it again or choose another provider.");
        }

        await _secretStore.SaveAsync(providerSecret, cancellationToken);
        await _configurationStore.SetActiveProviderAsync(provider.Name, cancellationToken);
        await _statusMessageWriter.ShowInfoAsync(
            $"Using saved provider configuration: {provider.Name}.",
            cancellationToken);

        return new OnboardingResult(
            provider.ProviderProfile,
            WasOnboardedDuringCurrentRun: false,
            previousResult.ReasoningEffort,
            provider.Name,
            previousResult.ThinkingMode);
    }

    private static bool ShouldOfferOnboardingRetry(Exception exception)
    {
        return exception is not OperationCanceledException and not PromptCancelledException;
    }

    private static bool ShouldOfferModelValidationRetry(Exception exception)
    {
        return exception is not OperationCanceledException &&
            (exception is ModelDiscoveryException ||
                exception is HttpRequestException ||
                exception is InvalidOperationException);
    }

    private static bool IsActiveProvider(
        SavedProviderConfiguration provider,
        OnboardingResult onboardingResult)
    {
        return !string.IsNullOrWhiteSpace(onboardingResult.ActiveProviderName)
            ? string.Equals(provider.Name, onboardingResult.ActiveProviderName, StringComparison.OrdinalIgnoreCase)
            : Equals(provider.ProviderProfile, onboardingResult.Profile);
    }
}

internal sealed record ProviderSetupRecoveryChoice(SavedProviderConfiguration? Provider)
{
    public static ProviderSetupRecoveryChoice ConfigureNewProvider { get; } = new(Provider: null);

    public bool IsConfigureNewProvider => Provider is null;
}
