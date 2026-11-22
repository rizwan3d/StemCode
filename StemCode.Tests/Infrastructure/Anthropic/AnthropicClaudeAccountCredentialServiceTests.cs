using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StemCode.Application.Abstractions;
using StemCode.Infrastructure.Anthropic;
using StemCode.Infrastructure.Secrets;

namespace StemCode.Tests.Infrastructure.Anthropic;

public sealed class AnthropicClaudeAccountCredentialServiceTests
{
    [Fact]
    public async Task AuthenticateAsync_Should_UseClaudeCodeAuthStatusWithoutReadingOAuthTokens()
    {
        Mock<IProcessRunner> processRunner = new(MockBehavior.Strict);
        processRunner
            .Setup(runner => runner.RunAsync(
                It.Is<ProcessExecutionRequest>(request =>
                    request.FileName == "claude" &&
                    request.Arguments.SequenceEqual(new[] { "auth", "status", "--json" })),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(
                0,
                """{"loggedIn":true,"authMethod":"claude.ai","subscriptionType":"max"}""",
                string.Empty));

        Mock<IStatusMessageWriter> statusWriter = new(MockBehavior.Strict);
        statusWriter
            .Setup(writer => writer.ShowInfoAsync(
                "Checking the local Claude Code subscription sign-in.",
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        statusWriter
            .Setup(writer => writer.ShowSuccessAsync(
                "Claude Code subscription sign-in detected.",
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        AnthropicClaudeAccountCredentialService sut = new(
            processRunner.Object,
            statusWriter.Object,
            NullLogger<AnthropicClaudeAccountCredentialService>.Instance);

        string result = await sut.AuthenticateAsync(CancellationToken.None);

        result.Should().Be(AnthropicClaudeAccountCredentialService.ClaudeCodeCliCredential);
        processRunner.VerifyAll();
        statusWriter.VerifyAll();
    }

    [Fact]
    public async Task ResolveAsync_Should_RejectLegacyStoredOAuthCredentials()
    {
        Mock<IProcessRunner> processRunner = new(MockBehavior.Strict);
        Mock<IStatusMessageWriter> statusWriter = new(MockBehavior.Strict);
        AnthropicClaudeAccountCredentialService sut = new(
            processRunner.Object,
            statusWriter.Object,
            NullLogger<AnthropicClaudeAccountCredentialService>.Instance);

        Func<Task> act = () => sut.ResolveAsync(
            "{\"access_token\":\"legacy\"}",
            forceRefresh: false,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*legacy credential*onboarding again*");
        processRunner.VerifyNoOtherCalls();
        statusWriter.VerifyNoOtherCalls();
    }
}
