using FluentAssertions;
using StemCode.Application.Commands;
using StemCode.Application.Models;
using StemCode.Domain.Models;

namespace StemCode.Tests.Application.Commands;

public sealed class ContextSizeCommandHandlerTests
{
    [Fact]
    public async Task ExecuteAsync_Should_SetRequestedCap_AndRespectProviderLimit()
    {
        ReplSessionContext session = CreateSession(reportedContextWindowTokens: 128_000);
        ContextSizeCommandHandler sut = new();

        ReplCommandResult result = await sut.ExecuteAsync(
            CreateContext(session, "256k"),
            CancellationToken.None);

        session.ContextWindowOverrideTokens.Should().Be(256_000);
        session.ActiveModelReportedContextWindowTokens.Should().Be(128_000);
        session.ActiveModelContextWindowTokens.Should().Be(128_000);
        result.Message.Should().Contain("provider reports 128,000 tokens");
        result.Message.Should().Contain("effective context window is 128,000 tokens");
    }

    [Fact]
    public async Task ExecuteAsync_Should_AcceptManualTokenCount()
    {
        ReplSessionContext session = CreateSession(reportedContextWindowTokens: null);
        ContextSizeCommandHandler sut = new();

        await sut.ExecuteAsync(
            CreateContext(session, "96000"),
            CancellationToken.None);

        session.ContextWindowOverrideTokens.Should().Be(96_000);
        session.ActiveModelContextWindowTokens.Should().Be(96_000);
    }

    [Fact]
    public async Task ExecuteAsync_Auto_Should_ClearCap()
    {
        ReplSessionContext session = CreateSession(reportedContextWindowTokens: 64_000);
        session.SetContextWindowOverride(32_000);
        ContextSizeCommandHandler sut = new();

        await sut.ExecuteAsync(
            CreateContext(session, "auto"),
            CancellationToken.None);

        session.ContextWindowOverrideTokens.Should().BeNull();
        session.ActiveModelContextWindowTokens.Should().Be(64_000);
    }

    [Fact]
    public async Task ExecuteAsync_Show_Should_ReportRequestedProviderAndEffectiveSizes()
    {
        ReplSessionContext session = CreateSession(reportedContextWindowTokens: 125_000);
        session.SetContextWindowOverride(64_000);
        ContextSizeCommandHandler sut = new();

        ReplCommandResult result = await sut.ExecuteAsync(
            CreateContext(session, "show"),
            CancellationToken.None);

        result.Message.Should().Contain("requested 64,000 tokens");
        result.Message.Should().Contain("provider 125,000 tokens");
        result.Message.Should().Contain("effective 64,000 tokens");
    }

    private static ReplCommandContext CreateContext(
        ReplSessionContext session,
        string argument)
    {
        return new ReplCommandContext(
            "context-size",
            argument,
            [argument],
            $"/context-size {argument}",
            session);
    }

    private static ReplSessionContext CreateSession(int? reportedContextWindowTokens)
    {
        IReadOnlyDictionary<string, int>? contextWindows = reportedContextWindowTokens is > 0
            ? new Dictionary<string, int>
            {
                ["model-a"] = reportedContextWindowTokens.Value
            }
            : null;

        return new ReplSessionContext(
            new AgentProviderProfile(ProviderKind.Ollama, null),
            "model-a",
            ["model-a"],
            modelContextWindowTokens: contextWindows);
    }
}
