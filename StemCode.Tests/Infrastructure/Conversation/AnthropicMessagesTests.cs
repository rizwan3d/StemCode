using FluentAssertions;
using StemCode.Application.Models;
using StemCode.Domain.Models;
using StemCode.Infrastructure.Conversation;
using System.Text;
using System.Text.Json;

namespace StemCode.Tests.Infrastructure.Conversation;

public sealed class AnthropicMessagesTests
{
    private const string Signature = "EqQBCgIYAhIM1gbcDa9GJwZA2b3hGgxBdjrkzLoky3dl1pkiMOYds";

    private readonly ConversationProviderRequestPayloadFactory _factory = new();
    private readonly ConversationProviderResponseNormalizer _normalizer = new();

    [Theory]
    [InlineData("claude-opus-5-5", "Adaptive", false, true, 64_000)]
    [InlineData("claude-sonnet-5-5", "Adaptive", false, true, 64_000)]
    [InlineData("claude-haiku-5-5", "Adaptive", true, true, 64_000)]
    [InlineData("claude-opus-5", "Adaptive", true, true, 64_000)]
    [InlineData("claude-opus-4-8", "Adaptive", true, true, 64_000)]
    [InlineData("claude-sonnet-4-6", "Adaptive", true, false, 64_000)]
    [InlineData("claude-fable-5-2", "Adaptive", false, true, 64_000)]
    [InlineData("claude-sonnet-4-5-20250929", "Manual", true, false, 64_000)]
    [InlineData("claude-haiku-4-5", "Manual", true, false, 64_000)]
    [InlineData("claude-opus-4-1", "Manual", true, false, 32_000)]
    [InlineData("claude-opus-4-20250514", "Manual", true, false, 32_000)]
    [InlineData("claude-3-7-sonnet-latest", "Manual", true, false, 64_000)]
    [InlineData("claude-3-5-haiku-20241022", "None", false, false, 8_192)]
    [InlineData("claude-3-opus-20240229", "None", false, false, 4_096)]
    public void Resolve_Should_MapModelLimits(
        string modelId,
        string thinking,
        bool canDisable,
        bool xhigh,
        int maxTokens)
    {
        AnthropicModelCapabilities capabilities = AnthropicModelCapabilities.Resolve(modelId);

        capabilities.Thinking.ToString().Should().Be(thinking);
        capabilities.CanDisableThinking.Should().Be(canDisable);
        capabilities.SupportsXHighEffort.Should().Be(xhigh);
        capabilities.MaxOutputTokens.Should().Be(maxTokens);
    }

    [Fact]
    public void BuildAnthropicMessagesRequest_Should_UseAdaptiveThinking_ForCurrentModels()
    {
        AnthropicMessagesRequest request = _factory.BuildAnthropicMessagesRequest(
            CreateRequest("claude-opus-5-5", thinkingMode: "on", reasoningEffort: "max"));

        request.Thinking!.Type.Should().Be("adaptive");
        request.Thinking.BudgetTokens.Should().BeNull();
        request.OutputConfig!.Effort.Should().Be("max");
        request.MaxTokens.Should().Be(64_000);
        request.Stream.Should().BeTrue();
    }

    [Fact]
    public void BuildAnthropicMessagesRequest_Should_LowerEffortInsteadOfDisabling_WhenModelCannotDisableThinking()
    {
        AnthropicMessagesRequest request = _factory.BuildAnthropicMessagesRequest(
            CreateRequest("claude-opus-5-5", thinkingMode: "off", reasoningEffort: "high"));

        request.Thinking.Should().BeNull();
        request.OutputConfig!.Effort.Should().Be("low");
    }

    [Fact]
    public void BuildAnthropicMessagesRequest_Should_DisableThinking_WhenModelAllowsIt()
    {
        AnthropicMessagesRequest request = _factory.BuildAnthropicMessagesRequest(
            CreateRequest("claude-opus-4-8", thinkingMode: "off", reasoningEffort: "high"));

        request.Thinking!.Type.Should().Be("disabled");
        request.OutputConfig.Should().BeNull();
    }

    [Fact]
    public void BuildAnthropicMessagesRequest_Should_KeepThinkingBudgetBelowMaxTokens_ForManualModels()
    {
        AnthropicMessagesRequest request = _factory.BuildAnthropicMessagesRequest(
            CreateRequest("claude-opus-4-1", thinkingMode: "on", reasoningEffort: "max"));

        request.MaxTokens.Should().Be(32_000);
        request.Thinking!.Type.Should().Be("enabled");
        request.Thinking.BudgetTokens.Should().Be(16_000);
        request.Thinking.BudgetTokens.Should().BeLessThan(request.MaxTokens);
        request.Thinking.Display.Should().BeNull();
        request.OutputConfig.Should().BeNull();
    }

    [Fact]
    public void BuildAnthropicMessagesRequest_Should_OmitThinking_ForModelsWithoutThinking()
    {
        AnthropicMessagesRequest request = _factory.BuildAnthropicMessagesRequest(
            CreateRequest("claude-3-5-haiku-20241022", thinkingMode: "on", reasoningEffort: "high"));

        request.Thinking.Should().BeNull();
        request.OutputConfig.Should().BeNull();
        request.MaxTokens.Should().Be(8_192);
    }

    [Fact]
    public void BuildAnthropicMessagesRequest_Should_PlaceCacheBreakpoints()
    {
        ConversationProviderRequest providerRequest = CreateRequest(
            "claude-opus-5-5",
            messages:
            [
                ConversationRequestMessage.User("Read the file."),
                ConversationRequestMessage.AssistantToolCalls(
                    [new ConversationToolCall("toolu_1", "file_read", """{"path":"a.cs"}""")]),
                ConversationRequestMessage.ToolResult("toolu_1", "file contents")
            ],
            tools: [CreateTool("file_read"), CreateTool("file_write")]);

        AnthropicMessagesRequest request = _factory.BuildAnthropicMessagesRequest(providerRequest);

        request.Tools!.Select(static tool => tool.CacheControl is not null)
            .Should().Equal(false, true);
        request.System!.Single().CacheControl.Should().NotBeNull();
        request.Messages[0].Content[^1].CacheControl.Should().NotBeNull("it ends the previous request's prefix");
        request.Messages[1].Content.Should().OnlyContain(static block => block.CacheControl == null);
        request.Messages[2].Content[^1].CacheControl.Should().NotBeNull("it ends this request's prefix");

        string json = JsonSerializer.Serialize(request, OpenAiConversationJsonContext.Default.AnthropicMessagesRequest);
        CountOccurrences(json, "\"cache_control\":{\"type\":\"ephemeral\"}").Should().Be(4);
        json.Should().Contain("\"stream\":true");
    }

    [Fact]
    public void BuildAnthropicMessagesRequest_Should_ReplaySignedThinkingBlocks_BeforeToolUse()
    {
        string reasoningDetails = $$"""
            [
              {"type":"thinking","thinking":"I should read the file.","signature":"{{Signature}}"},
              {"type":"redacted_thinking","data":"opaque-data"},
              {"type":"reasoning.text","text":"OpenRouter-shaped detail that must be skipped"}
            ]
            """;
        ConversationProviderRequest providerRequest = CreateRequest(
            "claude-opus-5-5",
            thinkingMode: "on",
            messages:
            [
                ConversationRequestMessage.User("Read the file."),
                ConversationRequestMessage.AssistantToolCalls(
                    [new ConversationToolCall("toolu_1", "file_read", """{"path":"a.cs"}""")],
                    reasoningDetailsJson: reasoningDetails),
                ConversationRequestMessage.ToolResult("toolu_1", "file contents")
            ]);

        AnthropicMessagesRequest request = _factory.BuildAnthropicMessagesRequest(providerRequest);

        IReadOnlyList<AnthropicContentBlock> assistant = request.Messages[1].Content;
        assistant.Select(static block => block.Type)
            .Should().Equal("thinking", "redacted_thinking", "tool_use");
        assistant[0].Thinking.Should().Be("I should read the file.");
        assistant[0].Signature.Should().Be(Signature);
        assistant[1].Data.Should().Be("opaque-data");
    }

    [Fact]
    public void ConvertAnthropicMessagesResponse_Should_PreserveThinkingSignatures_ForReplay()
    {
        string responseBody = $$$"""
            {
              "id": "msg_1",
              "content": [
                {"type":"thinking","thinking":"","signature":"{{{Signature}}}"},
                {"type":"tool_use","id":"toolu_1","name":"file_read","input":{"path":"a.cs"}}
              ],
              "stop_reason": "tool_use",
              "usage": {"input_tokens": 10, "output_tokens": 5, "cache_read_input_tokens": 900}
            }
            """;

        string normalized = _normalizer.ConvertAnthropicMessagesResponseToChatCompletion(responseBody);
        ConversationResponse response = new OpenAiConversationResponseMapper().Map(
            new ConversationProviderPayload(ProviderKind.Anthropic, normalized, "msg_1"));

        response.ToolCalls.Should().ContainSingle();
        response.ReasoningContent.Should().BeNull("an omitted thinking display has no text to show");
        response.ReasoningDetailsJson.Should().Contain(Signature);
        response.CachedPromptTokens.Should().Be(900);

        // The saved details go back into the next request unchanged.
        AnthropicMessagesRequest replay = _factory.BuildAnthropicMessagesRequest(CreateRequest(
            "claude-opus-5-5",
            thinkingMode: "on",
            messages:
            [
                ConversationRequestMessage.User("Read the file."),
                ConversationRequestMessage.AssistantToolCalls(
                    response.ToolCalls,
                    reasoningDetailsJson: response.ReasoningDetailsJson),
                ConversationRequestMessage.ToolResult("toolu_1", "file contents")
            ]));
        replay.Messages[1].Content[0].Signature.Should().Be(Signature);
        replay.Messages[1].Content[0].Thinking.Should().BeEmpty();
    }

    [Fact]
    public async Task EventStreamParser_Should_StreamTextAndRebuildFinalMessage()
    {
        string sse = string.Join("\n", [
            "event: message_start",
            """data: {"type":"message_start","message":{"id":"msg_s","usage":{"input_tokens":12,"cache_read_input_tokens":3000,"cache_creation_input_tokens":0,"output_tokens":1}}}""",
            "",
            "event: content_block_start",
            """data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}""",
            "",
            """data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Plan the edit."}}""",
            "",
            $$$"""data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"{{{Signature}}}"}}""",
            "",
            """data: {"type":"content_block_stop","index":0}""",
            "",
            """data: {"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}""",
            "",
            """data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Reading "}}""",
            "",
            """data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"the file."}}""",
            "",
            """data: {"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"toolu_9","name":"file_read","input":{}}}""",
            "",
            """data: {"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"{\"path\":"}}""",
            "",
            """data: {"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"\"a.cs\"}"}}""",
            "",
            "event: ping",
            """data: {"type":"ping"}""",
            "",
            """data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":42}}""",
            "",
            """data: {"type":"message_stop"}""",
            ""
        ]);
        List<string> chunks = [];

        StreamingResponseReadResult result = await AnthropicMessagesEventStreamParser.ReadResponseBodyAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(sse)),
            (chunk, _) =>
            {
                chunks.Add(chunk);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        result.AssistantMessageWasStreamed.Should().BeTrue();
        chunks.Should().Equal("Reading ", "the file.");

        ConversationResponse response = new OpenAiConversationResponseMapper().Map(new ConversationProviderPayload(
            ProviderKind.Anthropic,
            _normalizer.ConvertAnthropicMessagesResponseToChatCompletion(result.ResponseBody),
            "msg_s"));
        response.AssistantMessage.Should().Be("Reading the file.");
        response.ToolCalls.Should().ContainSingle()
            .Which.Should().Be(new ConversationToolCall("toolu_9", "file_read", """{"path":"a.cs"}"""));
        response.ReasoningContent.Should().Be("Plan the edit.");
        response.ReasoningDetailsJson.Should().Contain(Signature);
        response.CompletionTokens.Should().Be(42);
        response.PromptTokens.Should().Be(3012);
        response.CachedPromptTokens.Should().Be(3000);
    }

    [Fact]
    public async Task EventStreamParser_Should_PassThroughPlainJsonBodies()
    {
        const string json = """{"id":"msg_plain","content":[{"type":"text","text":"Hi"}],"stop_reason":"end_turn"}""";

        StreamingResponseReadResult result = await AnthropicMessagesEventStreamParser.ReadResponseBodyAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(json)),
            null,
            CancellationToken.None);

        result.AssistantMessageWasStreamed.Should().BeFalse();
        result.ResponseBody.Trim().Should().Be(json);
    }

    [Fact]
    public async Task EventStreamParser_Should_ThrowOnStreamError()
    {
        const string sse = """
            event: error
            data: {"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}

            """;

        Func<Task> act = () => AnthropicMessagesEventStreamParser.ReadResponseBodyAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(sse)),
            null,
            CancellationToken.None);

        await act.Should().ThrowAsync<StemCode.Application.Exceptions.ConversationProviderException>()
            .WithMessage("*overloaded_error*Overloaded*");
    }

    private static int CountOccurrences(string value, string pattern)
    {
        int count = 0;
        int index = 0;
        while ((index = value.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }

        return count;
    }

    private static ToolDefinition CreateTool(string name)
    {
        using JsonDocument schema = JsonDocument.Parse("""{"type":"object","properties":{}}""");
        return new ToolDefinition(name, $"Description for {name}", schema.RootElement.Clone());
    }

    private static ConversationProviderRequest CreateRequest(
        string modelId,
        string? thinkingMode = null,
        string? reasoningEffort = null,
        IReadOnlyList<ConversationRequestMessage>? messages = null,
        IReadOnlyList<ToolDefinition>? tools = null)
    {
        return new ConversationProviderRequest(
            new AgentProviderProfile(ProviderKind.Anthropic, null),
            "test-key",
            modelId,
            messages ?? [ConversationRequestMessage.User("Hello.")],
            "You are helpful.",
            tools ?? [],
            ReasoningEffort: reasoningEffort,
            ThinkingMode: thinkingMode);
    }
}
