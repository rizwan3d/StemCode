using FluentAssertions;
using System.Reflection;
using StemCode.Application.Profiles;
using StemCode.Domain.Models;
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
    public void BuildToolsAll_Should_MatchBuildProfileToolList()
    {
        StemCodeBuildTools.All.Should().BeEquivalentTo(BuiltInAgentProfiles.Build.EnabledTools);
    }

    private static string[] InvokeBuildArgs(StemCodeClientBuilder builder)
    {
        MethodInfo method = typeof(StemCodeClientBuilder).GetMethod(
            "BuildArgs",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        return (string[])method.Invoke(builder, [])!;
    }
}
