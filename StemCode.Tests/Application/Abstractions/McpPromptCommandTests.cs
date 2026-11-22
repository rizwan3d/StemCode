using FluentAssertions;
using StemCode.Application.Abstractions;

namespace StemCode.Tests.Application.Abstractions;

public sealed class McpPromptCommandTests
{
    private static readonly McpPromptDescriptor ReviewPrompt = new(
        "mcp__github__review",
        "github",
        "review",
        "Review a PR",
        [
            new McpPromptArgumentDescriptor("pr", null, Required: true),
            new McpPromptArgumentDescriptor("focus", null, Required: false)
        ]);

    [Theory]
    [InlineData("/mcp__github__review 12", true)]
    [InlineData("  /MCP__x__y", true)]
    [InlineData("/mcp", false)]
    [InlineData("mcp__x__y", false)]
    [InlineData("/help", false)]
    public void IsCandidate_Should_MatchOnlySlashMcpPrefix(string input, bool expected)
    {
        McpPromptCommand.IsCandidate(input).Should().Be(expected);
    }

    [Fact]
    public void Split_Should_SeparateCommandAndArguments()
    {
        McpPromptCommand.Split("/mcp__github__review 12 security only")
            .Should()
            .Be(("mcp__github__review", "12 security only"));
    }

    [Fact]
    public void TryBindArguments_Should_FillPositionalArguments_AndAppendExtraWordsToLast()
    {
        bool bound = McpPromptCommand.TryBindArguments(ReviewPrompt, "12 security and perf", out Dictionary<string, string> values, out string? error);

        bound.Should().BeTrue();
        error.Should().BeNull();
        values.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["pr"] = "12",
            ["focus"] = "security and perf"
        });
    }

    [Fact]
    public void TryBindArguments_Should_AcceptNamedAndQuotedValues()
    {
        bool bound = McpPromptCommand.TryBindArguments(ReviewPrompt, "focus=\"error handling\" 99", out Dictionary<string, string> values, out _);

        bound.Should().BeTrue();
        values["focus"].Should().Be("error handling");
        values["pr"].Should().Be("99");
    }

    [Fact]
    public void TryBindArguments_Should_ReportMissingRequiredArguments()
    {
        bool bound = McpPromptCommand.TryBindArguments(ReviewPrompt, string.Empty, out _, out string? error);

        bound.Should().BeFalse();
        error.Should().Contain("pr").And.Contain("/mcp__github__review <pr> [focus]");
    }

    [Fact]
    public void TryBindArguments_Should_RejectArgumentsForPromptWithoutParameters()
    {
        McpPromptDescriptor noArgs = new("mcp__s__p", "s", "p", null, []);

        McpPromptCommand.TryBindArguments(noArgs, "extra", out _, out string? error).Should().BeFalse();
        error.Should().Contain("takes no more arguments");
    }
}
