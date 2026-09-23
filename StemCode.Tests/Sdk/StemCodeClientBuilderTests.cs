using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Reflection;
using StemCode.Application.Abstractions;
using StemCode.Application.Backend;
using StemCode.Application.Profiles;
using StemCode.Domain.Models;
using StemCode.Infrastructure.Configuration;
using StemCode.Sdk;

namespace StemCode.Tests.Sdk;

public sealed class StemCodeClientBuilderTests
{
    [Fact]
    public void Build_Should_Throw_When_NoProviderConfigured()
    {
        StemCodeClientBuilder builder = StemCodeClient.CreateBuilder();

        Action act = () => builder.Build();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*provider must be configured*");
    }

    [Fact]
    public void Build_Should_Throw_When_HostedProviderHasNoApiKey()
    {
        StemCodeClientBuilder builder = StemCodeClient.CreateBuilder()
            .UseOpenAi(string.Empty);

        Action act = () => builder.Build();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*API key is required*");
    }

    [Fact]
    public void Build_Should_Succeed_For_KeylessLocalProvider()
    {
        StemCodeClientBuilder builder = StemCodeClient.CreateBuilder()
            .UseOllama();

        StemCodeClient client = builder.Build();

        client.Should().NotBeNull();
    }

    [Fact]
    public void Build_Should_Succeed_For_HostedProviderWithApiKey()
    {
        StemCodeClientBuilder builder = StemCodeClient.CreateBuilder()
            .UseAnthropic("sk-test", "claude-opus-4-8")
            .WithWorkspace(Directory.GetCurrentDirectory())
            .AutoApproveTools();

        StemCodeClient client = builder.Build();

        client.Should().NotBeNull();
    }

    [Fact]
    public void WithThinkingMode_Should_RejectUnsupportedValue()
    {
        StemCodeClientBuilder builder = StemCodeClient.CreateBuilder();

        Action act = () => builder.WithThinkingMode("turbo");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void UseProvider_Should_AcceptExplicitProfile()
    {
        AgentProviderProfile profile = new(ProviderKind.OpenAiCompatible, "https://api.example.com/v1");

        StemCodeClient client = StemCodeClient.CreateBuilder()
            .UseProvider(profile, "sk-test")
            .Build();

        client.Should().NotBeNull();
    }

    [Fact]
    public void UseBuildTool_Should_SelectBuildProfile()
    {
        StemCodeClientBuilder builder = StemCodeClient.CreateBuilder()
            .UseOllama()
            .UseBuildTool();

        string[] args = InvokeBuildArgs(builder);

        args.Should().ContainInOrder("--profile", StemCodeBuildTools.ProfileName);
    }

    [Fact]
    public void Build_Should_DefaultSdkConversationToNoSystemPrompt()
    {
        ConversationOptions conversation = ResolveSdkConversationOptions(
            StemCodeClient.CreateBuilder()
                .UseOllama());

        conversation.SystemPromptMode.Should().Be(ConversationSystemPromptMode.None);
        conversation.SystemPrompt.Should().BeNull();
    }

    [Fact]
    public void Build_Should_DisableProductTelemetryByDefault()
    {
        ApplicationOptions options = ResolveSdkApplicationOptions(
            StemCodeClient.CreateBuilder()
                .UseOllama());

        options.Telemetry.Enabled.Should().BeFalse();
    }

    [Fact]
    public void EnableProductTelemetry_Should_EnableBuiltInProductTelemetry()
    {
        ApplicationOptions options = ResolveSdkApplicationOptions(
            StemCodeClient.CreateBuilder()
                .UseOllama()
                .EnableProductTelemetry());

        options.Telemetry.Enabled.Should().BeTrue();
    }

    [Fact]
    public void WithSystemPrompt_Should_ConfigureCustomSystemPrompt()
    {
        ConversationOptions conversation = ResolveSdkConversationOptions(
            StemCodeClient.CreateBuilder()
                .UseOllama()
                .WithSystemPrompt("  Follow SDK caller rules.  "));

        conversation.SystemPromptMode.Should().Be(ConversationSystemPromptMode.Custom);
        conversation.SystemPrompt.Should().Be("Follow SDK caller rules.");
    }

    [Fact]
    public void UseStemCodeSystemPrompt_Should_ConfigureBuiltInSystemPrompt()
    {
        ConversationOptions conversation = ResolveSdkConversationOptions(
            StemCodeClient.CreateBuilder()
                .UseOllama()
                .UseStemCodeSystemPrompt());

        conversation.SystemPromptMode.Should().Be(ConversationSystemPromptMode.StemCode);
        conversation.SystemPrompt.Should().BeNull();
    }

    [Fact]
    public void WithoutSystemPrompt_Should_ClearPreviouslyConfiguredSystemPrompt()
    {
        ConversationOptions conversation = ResolveSdkConversationOptions(
            StemCodeClient.CreateBuilder()
                .UseOllama()
                .WithSystemPrompt("Use custom prompt.")
                .WithoutSystemPrompt());

        conversation.SystemPromptMode.Should().Be(ConversationSystemPromptMode.None);
        conversation.SystemPrompt.Should().BeNull();
    }

    [Fact]
    public void BuildToolsAll_Should_MatchBuildProfileToolList()
    {
        StemCodeBuildTools.All.Should().BeEquivalentTo(BuiltInAgentProfiles.Build.EnabledTools);
    }

    [Fact]
    public void WithOpenTelemetryTracing_Should_EmitProviderRequestActivity()
    {
        List<Activity> stoppedActivities = [];
        using ActivityListener listener = new()
        {
            ShouldListenTo = source => source.Name == StemCodeObservability.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stoppedActivities.Add
        };
        ActivitySource.AddActivityListener(listener);

        InspectSdkServices(
            StemCodeClient.CreateBuilder()
                .UseOllama()
                .WithOpenTelemetryTracing(),
            serviceProvider =>
            {
                IProductTelemetry telemetry = serviceProvider.GetRequiredService<IProductTelemetry>();

                telemetry.TrackProviderRequest(
                    "OpenAi",
                    success: true,
                    TimeSpan.FromMilliseconds(12),
                    streamed: true,
                    TimeSpan.FromMilliseconds(3),
                    retryCount: 2);
            });

        Activity activity = stoppedActivities.Should()
            .ContainSingle(candidate => candidate.OperationName == "stemcode.provider_request")
            .Subject;
        activity.Source.Name.Should().Be(StemCodeObservability.ActivitySourceName);
        activity.TagObjects.Should().Contain(tag =>
            tag.Key == "stemcode.provider.name" &&
            Equals(tag.Value, "OpenAi"));
        activity.TagObjects.Should().Contain(tag =>
            tag.Key == "stemcode.retry_count" &&
            Equals(tag.Value, 2));
    }

    private static string[] InvokeBuildArgs(StemCodeClientBuilder builder)
    {
        MethodInfo method = typeof(StemCodeClientBuilder).GetMethod(
            "BuildArgs",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        return (string[])method.Invoke(builder, [])!;
    }

    private static ConversationOptions ResolveSdkConversationOptions(StemCodeClientBuilder builder)
    {
        return ResolveSdkApplicationOptions(builder).Conversation;
    }

    private static ApplicationOptions ResolveSdkApplicationOptions(StemCodeClientBuilder builder)
    {
        ApplicationOptions? options = null;
        InspectSdkServices(
            builder,
            serviceProvider => options = serviceProvider
                .GetRequiredService<IOptions<ApplicationOptions>>()
                .Value);

        return options!;
    }

    private static void InspectSdkServices(
        StemCodeClientBuilder builder,
        Action<IServiceProvider> inspect)
    {
        StemCodeClient client = builder.Build();
        try
        {
            StemCodeBackend backend = GetPrivateField<StemCodeBackend>(client, "_backend");
            Action<IServiceCollection> configureServices =
                GetPrivateField<Action<IServiceCollection>>(backend, "_configureServices");

            ServiceCollection services = new();
            services.AddLogging();
            services.AddOptions();
            services.AddHttpClient();
            configureServices(services);

            using ServiceProvider serviceProvider = services.BuildServiceProvider();
            inspect(serviceProvider);
        }
        finally
        {
            client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static T GetPrivateField<T>(object instance, string fieldName)
        where T : class
    {
        FieldInfo field = instance.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        return (T)field.GetValue(instance)!;
    }
}
