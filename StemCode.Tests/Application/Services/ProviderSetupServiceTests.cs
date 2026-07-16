using FluentAssertions;
using Moq;
using StemCode.Application.Abstractions;
using StemCode.Application.Exceptions;
using StemCode.Application.Models;
using StemCode.Application.Services;
using StemCode.Domain.Models;

namespace StemCode.Tests.Application.Services;

public sealed class ProviderSetupServiceTests
{
    [Fact]
    public async Task EnsureConfiguredAsync_Should_OfferSavedProviders_When_ActiveProviderValidationFails()
    {
        AgentProviderProfile activeProfile = new(ProviderKind.Anthropic, null);
        AgentProviderProfile fallbackProfile = new(ProviderKind.OpenAi, null);
        SavedProviderConfiguration activeProvider = new("Anthropic", activeProfile, "claude-sonnet-4-6");
        SavedProviderConfiguration fallbackProvider = new("OpenAI", fallbackProfile, "gpt-5.4");
        OnboardingResult activeOnboarding = new(
            activeProfile,
            WasOnboardedDuringCurrentRun: false,
            ReasoningEffort: "medium",
            ActiveProviderName: "Anthropic",
            ThinkingMode: "on");
        ModelDiscoveryResult fallbackModelResult = new(
            [new AvailableModel("gpt-5.4", 400_000)],
            "gpt-5.4",
            ModelSelectionSource.ConfiguredDefault,
            ConfiguredDefaultModelStatus.Matched,
            "gpt-5.4",
            HadDuplicateModelIds: false);

        Mock<IFirstRunOnboardingService> onboardingService = new(MockBehavior.Strict);
        onboardingService
            .Setup(service => service.EnsureOnboardedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(activeOnboarding);

        Mock<IModelDiscoveryService> modelDiscoveryService = new(MockBehavior.Strict);
        modelDiscoveryService
            .SetupSequence(service => service.DiscoverAndSelectAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ModelDiscoveryException("active provider is unavailable"))
            .ReturnsAsync(fallbackModelResult);

        Mock<IConfirmationPrompt> confirmationPrompt = new(MockBehavior.Strict);

        Mock<IStatusMessageWriter> statusMessageWriter = new(MockBehavior.Strict);
        statusMessageWriter
            .Setup(writer => writer.ShowErrorAsync(
                "Provider setup could not be validated: active provider is unavailable",
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        statusMessageWriter
            .Setup(writer => writer.ShowInfoAsync(
                "Using saved provider configuration: OpenAI.",
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Mock<IAgentConfigurationStore> configurationStore = new(MockBehavior.Strict);
        configurationStore
            .Setup(store => store.ListProvidersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([activeProvider, fallbackProvider]);
        configurationStore
            .Setup(store => store.SetActiveProviderAsync("OpenAI", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync("OpenAI", It.IsAny<CancellationToken>()))
            .ReturnsAsync("openai-key");
        secretStore
            .Setup(store => store.SaveAsync("openai-key", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Mock<ISelectionPrompt> selectionPrompt = new(MockBehavior.Strict);
        selectionPrompt
            .Setup(prompt => prompt.PromptAsync(
                It.IsAny<SelectionPromptRequest<ProviderSetupRecoveryChoice>>(),
                It.IsAny<CancellationToken>()))
            .Returns<SelectionPromptRequest<ProviderSetupRecoveryChoice>, CancellationToken>((request, _) =>
            {
                request.Title.Should().Be("Provider setup failed. Choose another provider");
                request.Description.Should().Be(
                    "The active provider failed validation. Select a saved provider or configure a new one.");
                request.Options.Should().HaveCount(2);
                request.Options[0].Label.Should().Be("OpenAI");
                request.Options[0].Value.Provider.Should().Be(fallbackProvider);
                request.Options[0].Section.Should().Be("Saved providers");
                request.Options[1].Label.Should().Be("Configure a new provider");
                request.Options[1].Value.IsConfigureNewProvider.Should().BeTrue();
                request.Options[1].Section.Should().Be("Setup");

                return Task.FromResult(new ProviderSetupRecoveryChoice(fallbackProvider));
            });

        ProviderSetupService sut = CreateSut(
            onboardingService.Object,
            modelDiscoveryService.Object,
            confirmationPrompt.Object,
            statusMessageWriter.Object,
            configurationStore.Object,
            secretStore.Object,
            selectionPrompt.Object);

        ProviderSetupResult result = await sut.EnsureConfiguredAsync(CancellationToken.None);

        result.OnboardingResult.Should().Be(new OnboardingResult(
            fallbackProfile,
            WasOnboardedDuringCurrentRun: false,
            ReasoningEffort: "medium",
            ActiveProviderName: "OpenAI",
            ThinkingMode: "on"));
        result.ModelDiscoveryResult.Should().Be(fallbackModelResult);
        onboardingService.Verify(service => service.ReconfigureAsync(It.IsAny<CancellationToken>()), Times.Never);
        confirmationPrompt.VerifyNoOtherCalls();
        configurationStore.VerifyAll();
        secretStore.VerifyAll();
        selectionPrompt.VerifyAll();
        statusMessageWriter.VerifyAll();
    }

    [Fact]
    public async Task EnsureConfiguredAsync_Should_Reconfigure_When_ConfigureNewProviderIsSelected()
    {
        AgentProviderProfile activeProfile = new(ProviderKind.Anthropic, null);
        AgentProviderProfile savedFallbackProfile = new(ProviderKind.OpenAi, null);
        AgentProviderProfile newProfile = new(ProviderKind.OpenRouter, null);
        SavedProviderConfiguration savedFallbackProvider = new("OpenAI", savedFallbackProfile, "gpt-5.4");
        OnboardingResult activeOnboarding = new(
            activeProfile,
            WasOnboardedDuringCurrentRun: false,
            ActiveProviderName: "Anthropic");
        OnboardingResult newOnboarding = new(
            newProfile,
            WasOnboardedDuringCurrentRun: true,
            ActiveProviderName: "OpenRouter");
        ModelDiscoveryResult newModelResult = new(
            [new AvailableModel("openai/gpt-5.4")],
            "openai/gpt-5.4",
            ModelSelectionSource.FirstReturnedModel,
            ConfiguredDefaultModelStatus.NotConfigured,
            ConfiguredDefaultModel: null,
            HadDuplicateModelIds: false);

        Mock<IFirstRunOnboardingService> onboardingService = new(MockBehavior.Strict);
        onboardingService
            .Setup(service => service.EnsureOnboardedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(activeOnboarding);
        onboardingService
            .Setup(service => service.ReconfigureAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(newOnboarding);

        Mock<IModelDiscoveryService> modelDiscoveryService = new(MockBehavior.Strict);
        modelDiscoveryService
            .SetupSequence(service => service.DiscoverAndSelectAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ModelDiscoveryException("active provider is unavailable"))
            .ReturnsAsync(newModelResult);

        Mock<IConfirmationPrompt> confirmationPrompt = new(MockBehavior.Strict);

        Mock<IStatusMessageWriter> statusMessageWriter = new(MockBehavior.Strict);
        statusMessageWriter
            .Setup(writer => writer.ShowErrorAsync(
                "Provider setup could not be validated: active provider is unavailable",
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Mock<IAgentConfigurationStore> configurationStore = new(MockBehavior.Strict);
        configurationStore
            .Setup(store => store.ListProvidersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([savedFallbackProvider]);

        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);

        Mock<ISelectionPrompt> selectionPrompt = new(MockBehavior.Strict);
        selectionPrompt
            .Setup(prompt => prompt.PromptAsync(
                It.IsAny<SelectionPromptRequest<ProviderSetupRecoveryChoice>>(),
                It.IsAny<CancellationToken>()))
            .Returns<SelectionPromptRequest<ProviderSetupRecoveryChoice>, CancellationToken>((request, _) =>
            {
                request.Options.Should().Contain(option =>
                    option.Label == "Configure a new provider" &&
                    option.Value.IsConfigureNewProvider);
                return Task.FromResult(ProviderSetupRecoveryChoice.ConfigureNewProvider);
            });

        ProviderSetupService sut = CreateSut(
            onboardingService.Object,
            modelDiscoveryService.Object,
            confirmationPrompt.Object,
            statusMessageWriter.Object,
            configurationStore.Object,
            secretStore.Object,
            selectionPrompt.Object);

        ProviderSetupResult result = await sut.EnsureConfiguredAsync(CancellationToken.None);

        result.OnboardingResult.Should().Be(newOnboarding);
        result.ModelDiscoveryResult.Should().Be(newModelResult);
        confirmationPrompt.VerifyNoOtherCalls();
        configurationStore.Verify(store => store.SetActiveProviderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        secretStore.VerifyNoOtherCalls();
        selectionPrompt.VerifyAll();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task EnsureConfiguredAsync_Should_OfferRecoveryAgain_When_FallbackFails(
        bool missingCredentials,
        bool cancelRecovery)
    {
        SavedProviderConfiguration active = new("Anthropic", new(ProviderKind.Anthropic, null), null);
        SavedProviderConfiguration fallback = new("OpenAI", new(ProviderKind.OpenAi, null), null);
        SavedProviderConfiguration working = new("OpenRouter", new(ProviderKind.OpenRouter, null), null);
        ModelDiscoveryResult models = new(
            [new AvailableModel("model")], "model", ModelSelectionSource.FirstReturnedModel,
            ConfiguredDefaultModelStatus.NotConfigured, null, false);
        Mock<IFirstRunOnboardingService> onboarding = new(MockBehavior.Strict);
        onboarding.Setup(service => service.EnsureOnboardedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OnboardingResult(active.ProviderProfile, false, ActiveProviderName: active.Name));
        Mock<IModelDiscoveryService> discovery = new(MockBehavior.Strict);
        var discoverySequence = discovery.SetupSequence(service => service.DiscoverAndSelectAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ModelDiscoveryException("initial failure"));
        if (!missingCredentials)
        {
            discoverySequence.ThrowsAsync(new ModelDiscoveryException("fallback failure"));
        }
        discoverySequence.ReturnsAsync(models);
        Mock<IAgentConfigurationStore> configuration = new(MockBehavior.Strict);
        configuration.Setup(store => store.ListProvidersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([active, fallback, working]);
        configuration.Setup(store => store.SetActiveProviderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Mock<IApiKeySecretStore> secrets = new(MockBehavior.Strict);
        secrets.Setup(store => store.LoadAsync(fallback.Name, It.IsAny<CancellationToken>()))
            .ReturnsAsync(missingCredentials ? null : "fallback-key");
        secrets.Setup(store => store.LoadAsync(working.Name, It.IsAny<CancellationToken>()))
            .ReturnsAsync("working-key");
        secrets.Setup(store => store.SaveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Mock<IStatusMessageWriter> status = new(MockBehavior.Strict);
        status.Setup(writer => writer.ShowErrorAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        status.Setup(writer => writer.ShowInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Mock<ISelectionPrompt> selection = new(MockBehavior.Strict);
        int promptCount = 0;
        selection.Setup(prompt => prompt.PromptAsync(
                It.IsAny<SelectionPromptRequest<ProviderSetupRecoveryChoice>>(), It.IsAny<CancellationToken>()))
            .Returns<SelectionPromptRequest<ProviderSetupRecoveryChoice>, CancellationToken>((request, _) =>
            {
                promptCount++;
                if (promptCount == 1)
                {
                    return Task.FromResult(new ProviderSetupRecoveryChoice(fallback));
                }

                promptCount.Should().Be(2);
                request.AllowCancellation.Should().BeTrue();
                string currentProvider = missingCredentials ? active.Name : fallback.Name;
                request.Options.Should().NotContain(option => option.Value.Provider != null && option.Value.Provider.Name == currentProvider);
                request.Options.Should().Contain(option => option.Value.Provider == working);
                if (cancelRecovery)
                {
                    throw new PromptCancelledException("cancel recovery");
                }
                return Task.FromResult(new ProviderSetupRecoveryChoice(working));
            });
        Mock<IConfirmationPrompt> confirmation = new(MockBehavior.Strict);
        ProviderSetupService sut = CreateSut(onboarding.Object, discovery.Object, confirmation.Object,
            status.Object, configuration.Object, secrets.Object, selection.Object);

        if (cancelRecovery)
        {
            Func<Task> action = () => sut.EnsureConfiguredAsync(CancellationToken.None);
            await action.Should().ThrowAsync<PromptCancelledException>();
        }
        else
        {
            ProviderSetupResult result = await sut.EnsureConfiguredAsync(CancellationToken.None);
            result.OnboardingResult.ActiveProviderName.Should().Be(working.Name);
            result.ModelDiscoveryResult.Should().Be(models);
        }

        promptCount.Should().Be(2);
        configuration.Verify(store => store.SetActiveProviderAsync(fallback.Name, It.IsAny<CancellationToken>()),
            missingCredentials ? Times.Never() : Times.Once());
        discovery.Verify(service => service.DiscoverAndSelectAsync(It.IsAny<CancellationToken>()),
            Times.Exactly((missingCredentials ? 1 : 2) + (cancelRecovery ? 0 : 1)));
        status.Verify(writer => writer.ShowErrorAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    private static ProviderSetupService CreateSut(
        IFirstRunOnboardingService onboardingService,
        IModelDiscoveryService modelDiscoveryService,
        IConfirmationPrompt confirmationPrompt,
        IStatusMessageWriter statusMessageWriter,
        IAgentConfigurationStore configurationStore,
        IApiKeySecretStore secretStore,
        ISelectionPrompt selectionPrompt)
    {
        return new ProviderSetupService(
            onboardingService,
            modelDiscoveryService,
            confirmationPrompt,
            statusMessageWriter,
            configurationStore,
            secretStore,
            selectionPrompt);
    }
}
