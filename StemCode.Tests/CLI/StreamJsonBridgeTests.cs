using FluentAssertions;
using Moq;
using StemCode.Application.Models;
using StemCode.Application.UI;
using StemCode.CLI;
using System.Text.Json;

namespace StemCode.Tests.CLI;

public sealed class StreamJsonBridgeTests
{
    [Fact]
    public void Events_Should_BeWrittenAsOneJsonObjectPerLine()
    {
        StringWriter output = new();
        StreamJsonBridge sut = new(Mock.Of<IUiBridge>(), output);

        sut.ShowAssistantReasoning("Thinking about it");
        sut.ShowToolCalls([new ConversationToolCall("call_1", "file_read", """{"path":"a.txt"}""")]);
        sut.ShowToolResults(new ToolExecutionBatchResult([
            new ToolInvocationResult(
                "call_1",
                "file_read",
                new ToolResult(ToolResultStatus.PermissionDenied, "Denied.", """{"code":"denied"}"""))
        ]));
        sut.ShowExecutionPlan(new ExecutionPlanProgress(["Read", "Write"], 1));
        sut.ShowAssistantMessageChunk("Hel");
        sut.ShowProviderRetry(new ProviderRetryProgress(1, 3, "rate limited"));
        sut.ShowInfo("Note");

        string[] lines = output.ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        JsonElement[] events = lines.Select(static line => JsonDocument.Parse(line).RootElement).ToArray();

        events.Select(static e => e.GetProperty("type").GetString())
            .Should()
            .Equal("reasoning", "tool_use", "tool_result", "plan", "assistant_delta", "retry", "system");

        events[1].GetProperty("name").GetString().Should().Be("file_read");
        events[1].GetProperty("input").GetProperty("path").GetString().Should().Be("a.txt");

        events[2].GetProperty("status").GetString().Should().Be("permission_denied");
        events[2].GetProperty("isError").GetBoolean().Should().BeTrue();
        events[2].GetProperty("result").GetProperty("code").GetString().Should().Be("denied");

        events[3].GetProperty("tasks")[0].GetProperty("status").GetString().Should().Be("completed");
        events[3].GetProperty("tasks")[1].GetProperty("status").GetString().Should().Be("in_progress");

        events[4].GetProperty("text").GetString().Should().Be("Hel");
        events[6].GetProperty("subtype").GetString().Should().Be("info");
    }

    [Fact]
    public void ToolUse_Should_FallBackToString_When_ArgumentsAreNotJson()
    {
        StringWriter output = new();
        StreamJsonBridge sut = new(Mock.Of<IUiBridge>(), output);

        sut.ShowToolCalls([new ConversationToolCall("call_1", "shell_command", "not json")]);

        JsonElement element = JsonDocument.Parse(output.ToString().Trim()).RootElement;
        element.GetProperty("input").GetString().Should().Be("not json");
    }
}
