using FluentAssertions;
using StemCode.Application.Models;
using StemCode.Domain.Models;
using StemCode.Infrastructure.Conversation;

namespace StemCode.Tests.Infrastructure.Conversation;

public sealed class ConversationProviderRequestPayloadFactoryTests
{
    private readonly ConversationProviderRequestPayloadFactory _sut = new();

    [Fact]
    public void BuildChatCompletionRequest_Should_MapOpenAiReasoningEffort()
    {
        OpenAiChatCompletionRequest request = _sut.BuildChatCompletionRequest(CreateRequest(
            ProviderKind.OpenAi,
            "gpt-5.4",
            thinkingMode: "on",
            reasoningEffort: "high"));

        request.ReasoningEffort.Should().Be("high");
        request.Reasoning.Should().BeNull();
    }

    [Fact]
    public void BuildResponsesRequest_Should_MapOpenCodeZenReasoning()
    {
        OpenAiResponsesRequest request = _sut.BuildResponsesRequest(CreateRequest(
            ProviderKind.OpenCodeZen,
            "gpt-5.4",
            thinkingMode: "on",
            reasoningEffort: "high"));

        request.Reasoning.Should().NotBeNull();
        request.Reasoning!.Effort.Should().Be("high");
        request.Reasoning.Summary.Should().Be("auto");
    }

    [Fact]
    public void BuildAnthropicMessagesRequest_Should_UseAdaptiveThinking_ForClaude4Models()
    {
        AnthropicMessagesRequest request = _sut.BuildAnthropicMessagesRequest(CreateRequest(
            ProviderKind.Anthropic,
            "claude-sonnet-4-6",
            thinkingMode: "on",
            reasoningEffort: "xhigh"));

        request.Thinking.Should().NotBeNull();
        request.Thinking!.Type.Should().Be("adaptive");
        request.Thinking.Display.Should().Be("summarized");
        request.OutputConfig.Should().NotBeNull();
        // xhigh arrived with Opus 4.7; the 4.6 models accept low/medium/high/max only.
        request.OutputConfig!.Effort.Should().Be("high");
    }

    [Fact]
    public void BuildAnthropicMessagesRequest_Should_FallBackToManualThinking_ForOlderClaudeModels()
    {
        AnthropicMessagesRequest request = _sut.BuildAnthropicMessagesRequest(CreateRequest(
            ProviderKind.Anthropic,
            "claude-3-7-sonnet-latest",
            thinkingMode: "on",
            reasoningEffort: "medium"));

        request.Thinking.Should().NotBeNull();
        request.Thinking!.Type.Should().Be("enabled");
        request.Thinking.BudgetTokens.Should().Be(8192);
        request.OutputConfig.Should().BeNull();
    }

    [Fact]
    public void BuildChatCompletionRequest_Should_RemoveDeepSeekReasoningContent_FromReplayMessages()
    {
        ConversationProviderRequest request = new(
            new AgentProviderProfile(ProviderKind.DeepSeek, null),
            "test-key",
            "deepseek-reasoner",
            [
                ConversationRequestMessage.AssistantMessage(
                    "I inspected the file.",
                    reasoningContent: "Internal reasoning that should not be replayed."),
                ConversationRequestMessage.User("Continue.")
            ],
            null,
            [],
            ThinkingMode: "on");

        OpenAiChatCompletionRequest payload = _sut.BuildChatCompletionRequest(request);

        payload.Messages[0].Role.Should().Be("assistant");
        payload.Messages[0].ReasoningContent.Should().BeNull();
    }

    [Fact]
    public void BuildChatCompletionRequest_Should_MapGeminiThinkingBudget_ForGemini25Models()
    {
        OpenAiChatCompletionRequest request = _sut.BuildChatCompletionRequest(CreateRequest(
            ProviderKind.GoogleAiStudio,
            "gemini-2.5-pro",
            thinkingMode: "on",
            reasoningEffort: "high",
            showThinking: false));

        request.ThinkingConfig.Should().NotBeNull();
        request.ThinkingConfig!.ThinkingBudget.Should().Be(8192);
        request.ReasoningEffort.Should().BeNull();
    }

    [Fact]
    public void BuildChatCompletionRequest_Should_MapOpenRouterUnifiedReasoningObject()
    {
        OpenAiChatCompletionRequest request = _sut.BuildChatCompletionRequest(CreateRequest(
            ProviderKind.OpenRouter,
            "openai/gpt-5.4",
            thinkingMode: "on",
            reasoningEffort: "max"));

        request.ReasoningEffort.Should().BeNull();
        request.Reasoning.Should().NotBeNull();
        request.Reasoning!.Effort.Should().Be("xhigh");
        request.Reasoning.Exclude.Should().BeFalse();
    }

    [Theory]
    [InlineData("gpt-5.4", "on", "max", "xhigh")]
    [InlineData("gpt-5.4", "off", "high", "none")]
    [InlineData("gpt-5.4", "on", "minimal", "low")]
    [InlineData("gpt-5", "off", "high", "minimal")]
    [InlineData("gpt-5-mini", "on", "minimal", "minimal")]
    [InlineData("gpt-5", "on", "max", "high")]
    [InlineData("gpt-5.1-codex-max", "on", "max", "xhigh")]
    [InlineData("o3", "on", "max", "high")]
    [InlineData("o4-mini", "off", "high", "low")]
    [InlineData("gpt-4.1", "on", "high", null)]
    [InlineData("gpt-4o", "off", "high", null)]
    public void BuildChatCompletionRequest_Should_MapOpenAiEffort_PerModelFamily(
        string modelId,
        string thinkingMode,
        string effort,
        string? expected)
    {
        OpenAiChatCompletionRequest request = _sut.BuildChatCompletionRequest(CreateRequest(
            ProviderKind.OpenAi,
            modelId,
            thinkingMode,
            effort));

        request.ReasoningEffort.Should().Be(expected);
        request.Thinking.Should().BeNull();
    }

    [Theory]
    [InlineData("on", "low", "low", "enabled")]
    [InlineData("on", "medium", "high", "enabled")]
    [InlineData("on", "xhigh", "max", "enabled")]
    [InlineData("on", "none", "none", "disabled")]
    [InlineData("off", "high", "none", "disabled")]
    public void BuildChatCompletionRequest_Should_MapDeepSeekEffortAndThinking(
        string thinkingMode,
        string effort,
        string expectedEffort,
        string expectedThinking)
    {
        OpenAiChatCompletionRequest request = _sut.BuildChatCompletionRequest(CreateRequest(
            ProviderKind.DeepSeek,
            "deepseek-v4-pro",
            thinkingMode,
            effort));

        request.ReasoningEffort.Should().Be(expectedEffort);
        request.Thinking!.Type.Should().Be(expectedThinking);
    }

    [Theory]
    [InlineData("glm-4.6", "on", "high", null, "enabled")]
    [InlineData("glm-4.6", "off", "high", null, "disabled")]
    [InlineData("z-ai/glm-4.7", "on", "max", null, "enabled")]
    [InlineData("glm-5.2", "on", "medium", "high", "enabled")]
    [InlineData("glm-5.2", "on", "minimal", "low", "enabled")]
    [InlineData("glm-5.2", "off", "high", null, "disabled")]
    [InlineData("glm-5.3", "off", "high", "low", null)]
    public void BuildChatCompletionRequest_Should_MapGlmThinking(
        string modelId,
        string thinkingMode,
        string effort,
        string? expectedEffort,
        string? expectedThinking)
    {
        OpenAiChatCompletionRequest request = _sut.BuildChatCompletionRequest(CreateRequest(
            ProviderKind.OpenAiCompatible,
            modelId,
            thinkingMode,
            effort));

        request.ReasoningEffort.Should().Be(expectedEffort);
        request.Thinking?.Type.Should().Be(expectedThinking);
        if (expectedThinking is null)
        {
            request.Thinking.Should().BeNull();
        }
    }

    [Theory]
    [InlineData(ProviderKind.Groq, "openai/gpt-oss-120b", "on", "xhigh", "high")]
    [InlineData(ProviderKind.Groq, "openai/gpt-oss-20b", "off", "high", "low")]
    [InlineData(ProviderKind.Groq, "qwen/qwen3-32b", "off", "high", "none")]
    [InlineData(ProviderKind.Groq, "qwen/qwen3-32b", "on", "high", "default")]
    [InlineData(ProviderKind.Groq, "llama-3.3-70b-versatile", "on", "high", null)]
    [InlineData(ProviderKind.Cerebras, "zai-glm-4.7", "off", "high", "none")]
    [InlineData(ProviderKind.Cerebras, "zai-glm-4.7", "on", "high", null)]
    [InlineData(ProviderKind.Cerebras, "gpt-oss-120b", "on", "minimal", "low")]
    [InlineData(ProviderKind.Cerebras, "llama3.1-8b", "on", "high", null)]
    [InlineData(ProviderKind.OpenAiCompatible, "some-local-model", "off", "high", null)]
    [InlineData(ProviderKind.OpenAiCompatible, "some-local-model", "on", "max", "high")]
    public void BuildChatCompletionRequest_Should_MapHostSpecificEffort(
        ProviderKind providerKind,
        string modelId,
        string thinkingMode,
        string effort,
        string? expected)
    {
        OpenAiChatCompletionRequest request = _sut.BuildChatCompletionRequest(CreateRequest(
            providerKind,
            modelId,
            thinkingMode,
            effort));

        request.ReasoningEffort.Should().Be(expected);
        request.Thinking.Should().BeNull();
    }

    [Theory]
    [InlineData("kimi-k2.6", "off", null, "disabled")]
    [InlineData("moonshotai/kimi-k2.5", "on", null, "enabled")]
    [InlineData("kimi-k3", "on", "high", null)]
    [InlineData("kimi-k2-0905-preview", "on", null, null)]
    public void BuildChatCompletionRequest_Should_MapKimiThinking(
        string modelId,
        string thinkingMode,
        string? expectedEffort,
        string? expectedThinking)
    {
        OpenAiChatCompletionRequest request = _sut.BuildChatCompletionRequest(CreateRequest(
            ProviderKind.OpenAiCompatible,
            modelId,
            thinkingMode,
            "xhigh"));

        request.ReasoningEffort.Should().Be(expectedEffort);
        (request.Thinking?.Type).Should().Be(expectedThinking);
    }

    [Theory]
    [InlineData("qwen3-235b-a22b", "off", false)]
    [InlineData("qwen-plus", "on", true)]
    [InlineData("qwen3-235b-a22b-instruct-2507", "on", null)]
    public void BuildChatCompletionRequest_Should_MapQwenEnableThinking(
        string modelId,
        string thinkingMode,
        bool? expected)
    {
        OpenAiChatCompletionRequest request = _sut.BuildChatCompletionRequest(CreateRequest(
            ProviderKind.OpenAiCompatible,
            modelId,
            thinkingMode,
            "high"));

        request.EnableThinking.Should().Be(expected);
        request.ReasoningEffort.Should().BeNull();
    }

    [Theory]
    [InlineData("glm-4.6:cloud", "on", "high", "true")]
    [InlineData("deepseek-v3.1:671b-cloud", "off", "high", "false")]
    [InlineData("gpt-oss:120b-cloud", "on", "xhigh", "\"high\"")]
    [InlineData("gpt-oss:20b-cloud", "off", "high", "\"low\"")]
    public void MapOllamaThink_Should_UseLevelsForGptOss_AndBoolOtherwise(
        string modelId,
        string thinkingMode,
        string effort,
        string expectedJson)
    {
        System.Text.Json.JsonElement think = _sut.MapOllamaThink(CreateRequest(
            ProviderKind.OllamaCloud,
            modelId,
            thinkingMode,
            effort));

        think.GetRawText().Should().Be(expectedJson);
    }

    private static ConversationProviderRequest CreateRequest(
        ProviderKind providerKind,
        string modelId,
        string? thinkingMode = null,
        string? reasoningEffort = null,
        bool showThinking = true)
    {
        return new ConversationProviderRequest(
            new AgentProviderProfile(providerKind, null),
            "test-key",
            modelId,
            [ConversationRequestMessage.User("Hello.")],
            null,
            [],
            ReasoningEffort: reasoningEffort,
            ThinkingMode: thinkingMode,
            ShowThinking: showThinking);
    }
}
