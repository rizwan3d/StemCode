using FluentAssertions;
using StemCode.Application.Trajectory;

namespace StemCode.Tests.Application.Trajectory;

public sealed class TrajectoryLogReaderTests
{
    [Fact]
    public void TryParseLine_Should_ParseTrajectoryEventFields()
    {
        bool parsed = TrajectoryLogReader.TryParseLine(
            """
            {"timestampUtc":"2026-05-20T01:00:00Z","sectionId":"session-id","eventType":"assistant_tool_call_request","agentProfileName":"build","modelId":"model-a","workingDirectory":".","toolName":"shell_command","inputTokens":"12","tokensPerSecond":4.5}
            """,
            out var record);

        parsed.Should().BeTrue();
        record.SectionId.Should().Be("session-id");
        record.EventType.Should().Be("assistant_tool_call_request");
        record.ToolName.Should().Be("shell_command");
        record.InputTokens.Should().Be(12);
        record.TokensPerSecond.Should().Be(4.5);
    }

    [Fact]
    public void SummaryBuilder_Should_CountAndFormatTimeline()
    {
        TrajectoryLogReader.TryParseLine(
            """{"timestampUtc":"2026-05-20T01:00:00Z","sectionId":"session-id","eventType":"user_input","workingDirectory":"."}""",
            out var userInput).Should().BeTrue();
        TrajectoryLogReader.TryParseLine(
            """{"timestampUtc":"2026-05-20T01:00:01Z","sectionId":"session-id","eventType":"tool_call_response","workingDirectory":".","toolName":"shell_command"}""",
            out var toolResult).Should().BeTrue();

        var summary = TrajectorySummaryBuilder.Build([userInput, toolResult]);

        summary.EventCount.Should().Be(2);
        summary.UserTurnCount.Should().Be(1);
        summary.ToolResultCount.Should().Be(1);
        summary.ToolNames.Should().Equal("shell_command");
        summary.Timeline.Should().Be("UT");
    }
}
