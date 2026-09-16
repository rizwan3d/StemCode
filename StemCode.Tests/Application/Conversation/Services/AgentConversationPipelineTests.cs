using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StemCode.Application.Abstractions;
using StemCode.Application.Conversation.Services;
using StemCode.Application.Exceptions;
using StemCode.Application.Models;
using StemCode.Application.Profiles;
using StemCode.Application.Services;
using StemCode.Application.Tools;
using StemCode.Application.Tools.Models;
using StemCode.Application.Tools.Serialization;
using StemCode.Domain.Models;
using System.Text.Json;

namespace StemCode.Tests.Application.Conversation.Services;

public sealed class AgentConversationPipelineTests
{
    [Fact]
    public async Task ProcessAsync_Should_RunSingleConversationPass_When_ResponseContainsNormalAssistantContent()
    {
        ReplSessionContext session = CreateSession();
        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings("Base prompt"));

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([
                CreateToolDefinition(AgentToolNames.PlanningMode),
                CreateToolDefinition(AgentToolNames.CodeIntelligence),
                CreateToolDefinition(AgentToolNames.FileRead),
                CreateToolDefinition(AgentToolNames.FileWrite),
                CreateToolDefinition(AgentToolNames.ShellCommand)
            ]);

        List<ConversationProviderRequest> requests = [];
        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        providerClient
            .Setup(client => client.SendAsync(
                It.IsAny<ConversationProviderRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<ConversationProviderRequest, CancellationToken>((request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(new ConversationProviderPayload(
                    ProviderKind.OpenAiCompatible,
                    """{ "choices": [] }""",
                    "resp_1"));
            });

        Mock<IConversationResponseMapper> responseMapper = new(MockBehavior.Strict);
        responseMapper
            .Setup(mapper => mapper.Map(It.IsAny<ConversationProviderPayload>()))
            .Returns(new ConversationResponse(
                "Implemented the refactor.",
                [],
                "resp_1"));

        Mock<IToolExecutionPipeline> toolExecutionPipeline = new(MockBehavior.Strict);

        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new HeuristicTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            responseMapper.Object,
            toolExecutionPipeline.Object,
            toolRegistry.Object,
            configurationAccessor.Object);

        ConversationTurnResult result = await ProcessAsync(
            sut,
            "Implement the next refactor.",
            session);

        result.Kind.Should().Be(ConversationTurnResultKind.AssistantMessage);
        result.ResponseText.Should().Be("Implemented the refactor.");
        result.Metrics.Should().NotBeNull();
        result.Metrics!.EstimatedInputTokens.Should().BeGreaterThan(0);
        result.Metrics.EstimatedOutputTokens.Should().BeGreaterThan(0);
        result.Metrics.EstimatedTotalTokens.Should().Be(
            result.Metrics.EstimatedInputTokens + result.Metrics.EstimatedOutputTokens);
        result.Metrics.ProviderRetryCount.Should().Be(0);
        result.Metrics.ToolRoundCount.Should().Be(0);
        requests.Should().HaveCount(1);
        requests[0].SystemPrompt.Should().Contain("Base prompt");
        requests[0].SystemPrompt.Should().Contain("Active agent profile: build.");
        requests[0].SystemPrompt.Should().Contain("planning_mode");
        requests[0].SystemPrompt.Should().Contain("plan-first pass");
        requests[0].SystemPrompt.Should().Contain("call `planning_mode`");
        requests[0].SystemPrompt.Should().Contain("freeform plan in assistant text");
        requests[0].SystemPrompt.Should().Contain("installed build tools");
        requests[0].SystemPrompt.Should().Contain("separate verified facts from assumptions or open questions");
        requests[0].SystemPrompt.Should().Contain("Verified facts, Assumptions / open questions");
        requests[0].SystemPrompt.Should().Contain("compare approaches");
        requests[0].SystemPrompt.Should().Contain("Avoid low-quality plans such as");
        requests[0].SystemPrompt.Should().Contain("For project scaffolding commands");
        requests[0].SystemPrompt.Should().Contain("fully specified, non-interactive commands");
        requests[0].SystemPrompt.Should().Contain("one task at a time");
        requests[0].SystemPrompt.Should().Contain("finish the requested implementation when practical");
        requests[0].SystemPrompt.Should().Contain("do not stop at analysis if you can safely continue");
        requests[0].SystemPrompt.Should().Contain("code_intelligence");
        requests[0].SystemPrompt.Should().Contain("semantic navigation");
        requests[0].SystemPrompt.Should().NotContain("Always use planning_mode for tasks.");
        requests[0].SystemPrompt.Should().NotContain("You are StemCode in Planning Mode.");
        requests[0].SystemPrompt.Should().NotContain("EXECUTION PHASE IS ACTIVE.");
        requests[0].AvailableTools.Select(static tool => tool.Name)
            .Should()
            .Equal(
                AgentToolNames.PlanningMode,
                AgentToolNames.CodeIntelligence,
                AgentToolNames.FileRead,
                AgentToolNames.FileWrite,
                AgentToolNames.ShellCommand);
        requests[0].Messages.Should().HaveCount(1);
        requests[0].Messages[0].Role.Should().Be("user");
        requests[0].Messages[0].Content.Should().Be("Implement the next refactor.");
        session.ConversationHistory.Should().HaveCount(2);
        session.ConversationHistory[0].Content.Should().Be("Implement the next refactor.");
        session.ConversationHistory[1].Content.Should().Be("Implemented the refactor.");
        session.PendingExecutionPlan.Should().BeNull();
        toolExecutionPipeline.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProcessAsync_Should_LoadProviderScopedSecret_When_SessionHasActiveProviderName()
    {
        ReplSessionContext session = CreateSession(activeProviderName: "OpenAI");
        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync("OpenAI", It.IsAny<CancellationToken>()))
            .ReturnsAsync("provider-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings());

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([]);

        List<ConversationProviderRequest> requests = [];
        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        providerClient
            .Setup(client => client.SendAsync(
                It.IsAny<ConversationProviderRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<ConversationProviderRequest, CancellationToken>((request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(new ConversationProviderPayload(
                    ProviderKind.OpenAiCompatible,
                    """{ "choices": [] }""",
                    "resp_1"));
            });

        Mock<IConversationResponseMapper> responseMapper = new(MockBehavior.Strict);
        responseMapper
            .Setup(mapper => mapper.Map(It.IsAny<ConversationProviderPayload>()))
            .Returns(new ConversationResponse("Done.", [], "resp_1"));

        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new HeuristicTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            responseMapper.Object,
            Mock.Of<IToolExecutionPipeline>(),
            toolRegistry.Object,
            configurationAccessor.Object);

        await ProcessAsync(sut, "Use the configured provider.", session);

        requests.Should().ContainSingle();
        requests[0].ApiKey.Should().Be("provider-key");
        secretStore.Verify(store => store.LoadAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_Should_RunTaskLifecycleHooks()
    {
        ReplSessionContext session = CreateSession();
        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings());

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([]);

        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        providerClient
            .Setup(client => client.SendAsync(
                It.IsAny<ConversationProviderRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationProviderPayload(
                ProviderKind.OpenAiCompatible,
                """{ "choices": [] }""",
                "resp_1"));

        Mock<IConversationResponseMapper> responseMapper = new(MockBehavior.Strict);
        responseMapper
            .Setup(mapper => mapper.Map(It.IsAny<ConversationProviderPayload>()))
            .Returns(new ConversationResponse(
                "Done.",
                [],
                "resp_1"));

        RecordingLifecycleHookService hookService = new();
        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new HeuristicTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            responseMapper.Object,
            Mock.Of<IToolExecutionPipeline>(),
            toolRegistry.Object,
            configurationAccessor.Object,
            lifecycleHookService: hookService);

        ConversationTurnResult result = await ProcessAsync(
            sut,
            "Ship it.",
            session);

        result.ResponseText.Should().Be("Done.");
        hookService.Contexts.Select(static context => context.EventName)
            .Should()
            .Equal(LifecycleHookEvents.BeforeTaskStart, LifecycleHookEvents.AfterTaskComplete);
        hookService.Contexts[0].TaskInput.Should().Be("Ship it.");
        hookService.Contexts[1].ResponseText.Should().Be("Done.");
    }

    [Fact]
    public async Task ProcessAsync_Should_NotMutateSessionHistory_When_AfterTaskCompleteHookRejects()
    {
        ReplSessionContext session = CreateSession();
        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings());

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([]);

        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        providerClient
            .Setup(client => client.SendAsync(
                It.IsAny<ConversationProviderRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationProviderPayload(
                ProviderKind.OpenAiCompatible,
                """{ "choices": [] }""",
                "resp_1"));

        Mock<IConversationResponseMapper> responseMapper = new(MockBehavior.Strict);
        responseMapper
            .Setup(mapper => mapper.Map(It.IsAny<ConversationProviderPayload>()))
            .Returns(new ConversationResponse(
                "Done.",
                [],
                "resp_1"));

        RejectingAfterTaskCompleteLifecycleHookService hookService = new();
        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new HeuristicTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            responseMapper.Object,
            Mock.Of<IToolExecutionPipeline>(),
            toolRegistry.Object,
            configurationAccessor.Object,
            lifecycleHookService: hookService);

        Func<Task> action = () => ProcessAsync(
            sut,
            "Ship it.",
            session);

        await action.Should().ThrowAsync<ConversationPipelineException>()
            .WithMessage("Rejected after completion.");
        session.ConversationHistory.Should().ContainSingle();
        session.ConversationTurns.Should().ContainSingle();
        session.ConversationTurns[0].Status.Should().Be(ConversationTurnStatus.Interrupted);
        session.PendingExecutionPlan.Should().BeNull();
        hookService.Contexts.Select(static context => context.EventName)
            .Should()
            .Equal(
                LifecycleHookEvents.BeforeTaskStart,
                LifecycleHookEvents.AfterTaskComplete,
                LifecycleHookEvents.AfterTaskFailed);
    }


    [Fact]
    public async Task ProcessAsync_Should_PassThinkingEffortToProviderRequest()
    {
        ReplSessionContext session = CreateSession();
        session.SetThinkingMode("on");
        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings());

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([]);

        List<ConversationProviderRequest> requests = [];
        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        providerClient
            .Setup(client => client.SendAsync(
                It.IsAny<ConversationProviderRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<ConversationProviderRequest, CancellationToken>((request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(new ConversationProviderPayload(
                    ProviderKind.OpenAiCompatible,
                    """{ "choices": [] }""",
                    "resp_1"));
            });

        Mock<IConversationResponseMapper> responseMapper = new(MockBehavior.Strict);
        responseMapper
            .Setup(mapper => mapper.Map(It.IsAny<ConversationProviderPayload>()))
            .Returns(new ConversationResponse(
                "Done.",
                [],
                "resp_1"));

        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new HeuristicTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            responseMapper.Object,
            Mock.Of<IToolExecutionPipeline>(),
            toolRegistry.Object,
            configurationAccessor.Object);

        await ProcessAsync(
            sut,
            "Use deeper thinking.",
            session);

        requests.Should().ContainSingle();
        requests[0].ThinkingMode.Should().Be("on");
        requests[0].ReasoningEffort.Should().BeNull();
    }

    [Fact]
    public async Task ProcessAsync_Should_IncludeWorkspaceInstructionsInSystemPrompt()
    {
        ReplSessionContext session = CreateSession();
        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings("Base prompt"));

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([]);

        List<ConversationProviderRequest> requests = [];
        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        providerClient
            .Setup(client => client.SendAsync(
                It.IsAny<ConversationProviderRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<ConversationProviderRequest, CancellationToken>((request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(new ConversationProviderPayload(
                    ProviderKind.OpenAiCompatible,
                    """{ "choices": [] }""",
                    "resp_1"));
            });

        Mock<IConversationResponseMapper> responseMapper = new(MockBehavior.Strict);
        responseMapper
            .Setup(mapper => mapper.Map(It.IsAny<ConversationProviderPayload>()))
            .Returns(new ConversationResponse(
                "Done.",
                [],
                "resp_1"));

        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new HeuristicTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            responseMapper.Object,
            Mock.Of<IToolExecutionPipeline>(),
            toolRegistry.Object,
            configurationAccessor.Object,
            workspaceInstructionsProvider: new FixedWorkspaceInstructionsProvider("Workspace instructions:\nFrom AGENTS.md:\nFollow repo rules."));

        await ProcessAsync(
            sut,
            "Do the thing.",
            session);

        requests.Should().ContainSingle();
        requests[0].SystemPrompt.Should().Contain("Base prompt");
        requests[0].SystemPrompt.Should().Contain("Active agent profile: build.");
        requests[0].SystemPrompt.Should().Contain("Workspace instructions:");
        requests[0].SystemPrompt.Should().Contain("Follow repo rules.");
    }

    [Fact]
    public async Task ProcessAsync_Should_KeepNewestConversationTurns_WithinModelTokenBudget()
    {
        ReplSessionContext session = CreateSession(
            activeModelId: "small-model",
            modelContextWindowTokens: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["small-model"] = 7_000
            });
        for (int index = 1; index <= 6; index++)
        {
            session.AddConversationTurn(
                $"Historic user turn {index}: {new string((char)('a' + index - 1), 900)}",
                $"Historic assistant turn {index}: {new string((char)('g' + index - 1), 900)}");
        }

        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings(new string('s', 8_000), maxHistoryTurns: 6));

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([]);

        List<ConversationProviderRequest> requests = [];
        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        providerClient
            .Setup(client => client.SendAsync(
                It.IsAny<ConversationProviderRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<ConversationProviderRequest, CancellationToken>((request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(new ConversationProviderPayload(
                    ProviderKind.OpenAiCompatible,
                    """{ "choices": [] }""",
                    "resp_1"));
            });

        Mock<IConversationResponseMapper> responseMapper = new(MockBehavior.Strict);
        responseMapper
            .Setup(mapper => mapper.Map(It.IsAny<ConversationProviderPayload>()))
            .Returns(new ConversationResponse("Done.", [], "resp_1"));

        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new HeuristicTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            responseMapper.Object,
            Mock.Of<IToolExecutionPipeline>(),
            toolRegistry.Object,
            configurationAccessor.Object);

        await ProcessAsync(
            sut,
            $"Current task: {new string('z', 1_400)}",
            session);

        requests.Should().ContainSingle();
        requests[0].Messages.Should().NotContain(message =>
            message.Content != null &&
            message.Content.Contains("Earlier conversation summary:", StringComparison.Ordinal));
        requests[0].Messages.Should().NotContain(message =>
            string.Equals(message.Content, session.ConversationTurns[0].UserInput, StringComparison.Ordinal));
        requests[0].Messages.Should().Contain(message =>
            message.Content != null &&
            message.Content.Contains("Historic user turn 6:", StringComparison.Ordinal));
        requests[0].Messages.Should().Contain(message =>
            message.Content != null &&
            message.Content.Contains("Historic assistant turn 6:", StringComparison.Ordinal));
        requests[0].SystemPrompt.Should().NotBeNull();
        requests[0].SystemPrompt!.Length.Should().BeLessThan(16_000);
    }

    [Fact]
    public async Task ProcessAsync_Should_KeepTailOfOlderTurn_When_RecentConversationBudgetRunsOut()
    {
        ReplSessionContext session = CreateSession(
            activeModelId: "small-model",
            modelContextWindowTokens: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["small-model"] = 5_000
            });
        session.AddConversationTurn(
            $"Historic user turn 1: {new string('a', 500)}",
            $"Historic assistant turn 1: {new string('b', 500)}");
        session.AddConversationTurn(
            $"Historic user turn 2: {new string('c', 1_300)}TAIL-KEEP-USER-2",
            $"Historic assistant turn 2: {new string('d', 200)}");
        session.AddConversationTurn(
            $"Historic user turn 3: {new string('e', 300)}",
            $"Historic assistant turn 3: {new string('f', 300)}");

        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings(maxHistoryTurns: 3));

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([]);

        List<ConversationProviderRequest> requests = [];
        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        providerClient
            .Setup(client => client.SendAsync(
                It.IsAny<ConversationProviderRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<ConversationProviderRequest, CancellationToken>((request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(new ConversationProviderPayload(
                    ProviderKind.OpenAiCompatible,
                    """{ "choices": [] }""",
                    "resp_tail"));
            });

        Mock<IConversationResponseMapper> responseMapper = new(MockBehavior.Strict);
        responseMapper
            .Setup(mapper => mapper.Map(It.IsAny<ConversationProviderPayload>()))
            .Returns(new ConversationResponse("Done.", [], "resp_tail"));

        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new CharacterTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            responseMapper.Object,
            Mock.Of<IToolExecutionPipeline>(),
            toolRegistry.Object,
            configurationAccessor.Object);

        await ProcessAsync(
            sut,
            "Current task.",
            session);

        requests.Should().ContainSingle();
        requests[0].Messages.Should().Contain(message =>
            string.Equals(message.Content, session.ConversationTurns[2].UserInput, StringComparison.Ordinal));
        requests[0].Messages.Should().Contain(message =>
            string.Equals(message.Content, session.ConversationTurns[2].AssistantResponse, StringComparison.Ordinal));
        requests[0].Messages.Should().Contain(message =>
            string.Equals(message.Role, "assistant", StringComparison.Ordinal) &&
            string.Equals(message.Content, session.ConversationTurns[1].AssistantResponse, StringComparison.Ordinal));
        requests[0].Messages.Should().Contain(message =>
            string.Equals(message.Role, "user", StringComparison.Ordinal) &&
            message.Content != null &&
            message.Content.StartsWith("...", StringComparison.Ordinal) &&
            message.Content.Contains("TAIL-KEEP-USER-2", StringComparison.Ordinal));
        requests[0].Messages.Should().NotContain(message =>
            string.Equals(message.Content, session.ConversationTurns[1].UserInput, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProcessAsync_Should_PruneOlderActiveTurnToolOutput_BeforeFurtherConversationCompression()
    {
        ReplSessionContext session = CreateSession(
            activeModelId: "small-model",
            modelContextWindowTokens: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["small-model"] = 12_000
            });
        for (int index = 1; index <= 4; index++)
        {
            session.AddConversationTurn(
                $"Historic user {index}: {new string((char)('a' + index - 1), 700)}",
                $"Historic assistant {index}: {new string((char)('k' + index - 1), 700)}");
        }

        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings("Base prompt", maxHistoryTurns: 4));

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([CreateToolDefinition(AgentToolNames.FileRead)]);

        List<ConversationProviderRequest> requests = [];
        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        providerClient
            .Setup(client => client.SendAsync(
                It.IsAny<ConversationProviderRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<ConversationProviderRequest, CancellationToken>((request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(new ConversationProviderPayload(
                    ProviderKind.OpenAiCompatible,
                    """{ "choices": [] }""",
                    $"resp_{requests.Count}"));
            });

        Mock<IConversationResponseMapper> responseMapper = new(MockBehavior.Strict);
        responseMapper
            .SetupSequence(mapper => mapper.Map(It.IsAny<ConversationProviderPayload>()))
            .Returns(new ConversationResponse(
                null,
                [new ConversationToolCall("call_1", AgentToolNames.FileRead, """{ "path": "one.txt" }""")],
                "resp_1"))
            .Returns(new ConversationResponse(
                null,
                [new ConversationToolCall("call_2", AgentToolNames.FileRead, """{ "path": "two.txt" }""")],
                "resp_2"))
            .Returns(new ConversationResponse(
                "Final answer.",
                [],
                "resp_3"));

        Mock<IToolExecutionPipeline> toolExecutionPipeline = new(MockBehavior.Strict);
        toolExecutionPipeline
            .SetupSequence(pipeline => pipeline.ExecuteAsync(
                It.IsAny<IReadOnlyList<ConversationToolCall>>(),
                session,
                ConversationExecutionPhase.Execution,
                It.IsAny<IReadOnlySet<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolExecutionBatchResult([
                new ToolInvocationResult(
                    "call_1",
                    AgentToolNames.FileRead,
                    ToolResult.Success(
                        "first result",
                        $$"""{"blob":"{{new string('x', 220_000)}}" }"""))
            ]))
            .ReturnsAsync(new ToolExecutionBatchResult([
                new ToolInvocationResult(
                    "call_2",
                    AgentToolNames.FileRead,
                    ToolResult.Success(
                        "second result",
                        $$"""{"blob":"{{new string('y', 2_000)}}" }"""))
            ]));

        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new HeuristicTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            responseMapper.Object,
            toolExecutionPipeline.Object,
            toolRegistry.Object,
            configurationAccessor.Object);

        await ProcessAsync(
            sut,
            "Inspect the generated files.",
            session);

        requests.Should().HaveCount(3);
        requests[2].Messages.Should().Contain(message =>
            string.Equals(message.Role, "tool", StringComparison.Ordinal) &&
            message.ToolCallId == "call_2");
        requests[2].Messages.Should().Contain(message =>
            string.Equals(message.Role, "tool", StringComparison.Ordinal) &&
            message.ToolCallId == "call_1" &&
            message.Content != null &&
            message.Content.Contains("\"ToolName\":\"file_read\"", StringComparison.Ordinal) &&
            !message.Content.Contains(new string('x', 1_000), StringComparison.Ordinal));
        requests[2].Messages.Should().NotContain(message =>
            message.Content != null &&
            message.Content.Contains("Earlier conversation summary:", StringComparison.Ordinal));
        requests[2].Messages.Should().Contain(message =>
            string.Equals(message.Role, "assistant", StringComparison.Ordinal) &&
            message.Content != null &&
            message.Content.Contains("Historic assistant 4:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProcessAsync_Should_ProtectSkillToolOutputs_FromPruning()
    {
        ReplSessionContext session = CreateSession(
            activeModelId: "small-model",
            modelContextWindowTokens: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["small-model"] = 100_000
            });

        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings("Base prompt"));

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([
                CreateToolDefinition(AgentToolNames.SkillLoad),
                CreateToolDefinition(AgentToolNames.FileRead)
            ]);

        List<ConversationProviderRequest> requests = [];
        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        providerClient
            .Setup(client => client.SendAsync(
                It.IsAny<ConversationProviderRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<ConversationProviderRequest, CancellationToken>((request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(new ConversationProviderPayload(
                    ProviderKind.OpenAiCompatible,
                    """{ "choices": [] }""",
                    $"resp_{requests.Count}"));
            });

        Mock<IConversationResponseMapper> responseMapper = new(MockBehavior.Strict);
        responseMapper
            .SetupSequence(mapper => mapper.Map(It.IsAny<ConversationProviderPayload>()))
            .Returns(new ConversationResponse(
                null,
                [new ConversationToolCall("call_1", AgentToolNames.SkillLoad, """{ "skill": "build" }""")],
                "resp_1"))
            .Returns(new ConversationResponse(
                null,
                [new ConversationToolCall("call_2", AgentToolNames.FileRead, """{ "path": "README.md" }""")],
                "resp_2"))
            .Returns(new ConversationResponse(
                "Final answer.",
                [],
                "resp_3"));

        Mock<IToolExecutionPipeline> toolExecutionPipeline = new(MockBehavior.Strict);
        toolExecutionPipeline
            .SetupSequence(pipeline => pipeline.ExecuteAsync(
                It.IsAny<IReadOnlyList<ConversationToolCall>>(),
                session,
                ConversationExecutionPhase.Execution,
                It.IsAny<IReadOnlySet<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolExecutionBatchResult([
                new ToolInvocationResult(
                    "call_1",
                    AgentToolNames.SkillLoad,
                    ToolResult.Success(
                        "skill result",
                        $$"""{"blob":"{{new string('p', 30_000)}}" }"""))
            ]))
            .ReturnsAsync(new ToolExecutionBatchResult([
                new ToolInvocationResult(
                    "call_2",
                    AgentToolNames.FileRead,
                    ToolResult.Success(
                        "read result",
                        $$"""{"blob":"{{new string('r', 30_000)}}" }"""))
            ]));

        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new CharacterTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            responseMapper.Object,
            toolExecutionPipeline.Object,
            toolRegistry.Object,
            configurationAccessor.Object);

        await ProcessAsync(
            sut,
            "Plan and inspect the file.",
            session);

        requests.Should().HaveCount(3);
        requests[2].Messages.Should().Contain(message =>
            string.Equals(message.Role, "tool", StringComparison.Ordinal) &&
            message.ToolCallId == "call_1" &&
            message.Content != null &&
            message.Content.Contains("\"ToolName\":\"skill_load\"", StringComparison.Ordinal));
        requests[2].Messages.Should().Contain(message =>
            string.Equals(message.Role, "tool", StringComparison.Ordinal) &&
            message.ToolCallId == "call_2");
    }

    [Fact]
    public async Task ProcessAsync_Should_CompactFileReadToolFeedback_BeforeSendingToProvider()
    {
        ReplSessionContext session = CreateSession();

        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings("Base prompt"));

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([CreateToolDefinition(AgentToolNames.FileRead)]);

        List<ConversationProviderRequest> requests = [];
        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        providerClient
            .Setup(client => client.SendAsync(
                It.IsAny<ConversationProviderRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<ConversationProviderRequest, CancellationToken>((request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(new ConversationProviderPayload(
                    ProviderKind.OpenAiCompatible,
                    """{ "choices": [] }""",
                    $"resp_{requests.Count}"));
            });

        Mock<IConversationResponseMapper> responseMapper = new(MockBehavior.Strict);
        responseMapper
            .SetupSequence(mapper => mapper.Map(It.IsAny<ConversationProviderPayload>()))
            .Returns(new ConversationResponse(
                null,
                [new ConversationToolCall("call_1", AgentToolNames.FileRead, """{ "path": "README.md" }""")],
                "resp_1"))
            .Returns(new ConversationResponse(
                "Final answer.",
                [],
                "resp_2"));

        Mock<IToolExecutionPipeline> toolExecutionPipeline = new(MockBehavior.Strict);
        toolExecutionPipeline
            .Setup(pipeline => pipeline.ExecuteAsync(
                It.IsAny<IReadOnlyList<ConversationToolCall>>(),
                session,
                ConversationExecutionPhase.Execution,
                It.IsAny<IReadOnlySet<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolExecutionBatchResult([
                new ToolInvocationResult(
                    "call_1",
                    AgentToolNames.FileRead,
                    ToolResult.Success(
                        "Read file 'README.md'.",
                        """
                        {
                          "Path": "README.md",
                          "RawContent": "1: raw line",
                          "DisplayContent": "1: raw line",
                          "StartLine": 1,
                          "EndLine": 1,
                          "TotalLines": 1,
                          "Truncated": false,
                          "NextOffset": null,
                          "Sha256": "abc",
                          "Encoding": "utf-8"
                        }
                        """))
            ]));

        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new HeuristicTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            responseMapper.Object,
            toolExecutionPipeline.Object,
            toolRegistry.Object,
            configurationAccessor.Object);

        await ProcessAsync(
            sut,
            "Read the file.",
            session);

        requests.Should().HaveCount(2);
        ConversationRequestMessage toolMessage = requests[1].Messages.Single(message =>
            string.Equals(message.Role, "tool", StringComparison.Ordinal));
        toolMessage.Content.Should().Contain("\"DisplayContent\":\"1: raw line\"");
        toolMessage.Content.Should().NotContain("\"RawContent\"");
    }

    [Fact]
    public async Task ProcessAsync_Should_CompactCodebaseIndexToolFeedback_BeforeSendingToProvider()
    {
        ReplSessionContext session = CreateSession();

        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings("Base prompt"));

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([CreateToolDefinition(AgentToolNames.CodebaseIndex)]);

        List<ConversationProviderRequest> requests = [];
        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        providerClient
            .Setup(client => client.SendAsync(
                It.IsAny<ConversationProviderRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<ConversationProviderRequest, CancellationToken>((request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(new ConversationProviderPayload(
                    ProviderKind.OpenAiCompatible,
                    """{ "choices": [] }""",
                    $"resp_{requests.Count}"));
            });

        Mock<IConversationResponseMapper> responseMapper = new(MockBehavior.Strict);
        responseMapper
            .SetupSequence(mapper => mapper.Map(It.IsAny<ConversationProviderPayload>()))
            .Returns(new ConversationResponse(
                null,
                [new ConversationToolCall("call_1", AgentToolNames.CodebaseIndex, """{ "action": "search", "query": "round winner" }""")],
                "resp_1"))
            .Returns(new ConversationResponse(
                "Final answer.",
                [],
                "resp_2"));

        Mock<IToolExecutionPipeline> toolExecutionPipeline = new(MockBehavior.Strict);
        toolExecutionPipeline
            .Setup(pipeline => pipeline.ExecuteAsync(
                It.IsAny<IReadOnlyList<ConversationToolCall>>(),
                session,
                ConversationExecutionPhase.Execution,
                It.IsAny<IReadOnlySet<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolExecutionBatchResult([
                new ToolInvocationResult(
                    "call_1",
                    AgentToolNames.CodebaseIndex,
                    ToolResult.Success(
                        "Found 1 indexed codebase match for 'round winner'.",
                        """
                        {
                          "Query": "round winner",
                          "IndexPath": ".stemcode/cache/codebase-index.zvec",
                          "IndexWasUpdated": false,
                          "IndexedFileCount": 123,
                          "Warnings": ["No CODEOWNERS file was found."],
                          "Matches": [
                            {
                              "Path": "Overlay/src/app/roundwiner/roundwiner.component.ts",
                              "Language": "typescript",
                              "Score": 48.0,
                              "Symbols": ["RoundWinerComponent", "ngOnChanges"],
                              "SemanticSymbols": [{ "Name": "verbose" }],
                              "Snippets": [
                                { "LineNumber": 12, "Text": "round winner snippet" }
                              ]
                            }
                          ]
                        }
                        """))
            ]));

        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new HeuristicTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            responseMapper.Object,
            toolExecutionPipeline.Object,
            toolRegistry.Object,
            configurationAccessor.Object);

        await ProcessAsync(
            sut,
            "Search the codebase.",
            session);

        requests.Should().HaveCount(2);
        ConversationRequestMessage toolMessage = requests[1].Messages.Single(message =>
            string.Equals(message.Role, "tool", StringComparison.Ordinal));
        toolMessage.Content.Should().Contain("\"Matches\"");
        toolMessage.Content.Should().Contain("round winner snippet");
        toolMessage.Content.Should().NotContain("\"SemanticSymbols\"");
    }

    [Fact]
    public async Task ProcessAsync_Should_UseWorkspaceSystemPromptWhenAvailable()
    {
        ReplSessionContext session = CreateSession();
        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings("Base prompt"));

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([]);

        List<ConversationProviderRequest> requests = [];
        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        providerClient
            .Setup(client => client.SendAsync(
                It.IsAny<ConversationProviderRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<ConversationProviderRequest, CancellationToken>((request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(new ConversationProviderPayload(
                    ProviderKind.OpenAiCompatible,
                    """{ "choices": [] }""",
                    "resp_workspace_prompt"));
            });

        Mock<IConversationResponseMapper> responseMapper = new(MockBehavior.Strict);
        responseMapper
            .Setup(mapper => mapper.Map(It.IsAny<ConversationProviderPayload>()))
            .Returns(new ConversationResponse(
                "Done.",
                [],
                "resp_workspace_prompt"));

        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new HeuristicTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            responseMapper.Object,
            Mock.Of<IToolExecutionPipeline>(),
            toolRegistry.Object,
            configurationAccessor.Object,
            workspaceSystemPromptProvider: new FixedWorkspaceSystemPromptProvider("Workspace custom system prompt."));

        await ProcessAsync(
            sut,
            "Do the thing.",
            session);

        requests.Should().ContainSingle();
        requests[0].SystemPrompt.Should().Contain("Workspace custom system prompt.");
        requests[0].SystemPrompt.Should().Contain("Active agent profile: build.");
        requests[0].SystemPrompt.Should().NotContain("Base prompt");
    }

    [Fact]
    public async Task ProcessAsync_Should_AppendWorkspaceSystemPromptWhenProviderExtendsConfiguredPrompt()
    {
        ReplSessionContext session = CreateSession();
        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings("Base prompt"));

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([]);

        List<ConversationProviderRequest> requests = [];
        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        providerClient
            .Setup(client => client.SendAsync(
                It.IsAny<ConversationProviderRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<ConversationProviderRequest, CancellationToken>((request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(new ConversationProviderPayload(
                    ProviderKind.OpenAiCompatible,
                    """{ "choices": [] }""",
                    "resp_workspace_prompt_append"));
            });

        Mock<IConversationResponseMapper> responseMapper = new(MockBehavior.Strict);
        responseMapper
            .Setup(mapper => mapper.Map(It.IsAny<ConversationProviderPayload>()))
            .Returns(new ConversationResponse(
                "Done.",
                [],
                "resp_workspace_prompt_append"));

        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new HeuristicTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            responseMapper.Object,
            Mock.Of<IToolExecutionPipeline>(),
            toolRegistry.Object,
            configurationAccessor.Object,
            workspaceSystemPromptProvider: new FixedWorkspaceSystemPromptProvider(
                configuredSystemPrompt => string.Join(
                    $"{Environment.NewLine}{Environment.NewLine}",
                    configuredSystemPrompt ?? string.Empty,
                    "Workspace append prompt.")));

        await ProcessAsync(
            sut,
            "Do the thing.",
            session);

        requests.Should().ContainSingle();
        requests[0].SystemPrompt.Should().Contain("Base prompt");
        requests[0].SystemPrompt.Should().Contain("Workspace append prompt.");
        requests[0].SystemPrompt.Should().Contain("Active agent profile: build.");
    }

    [Fact]
    public async Task ProcessAsync_Should_UseWorkspaceProfilePromptWhenAvailable()
    {
        ReplSessionContext session = CreateSession();
        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings("Base prompt"));

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([]);

        List<ConversationProviderRequest> requests = [];
        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        providerClient
            .Setup(client => client.SendAsync(
                It.IsAny<ConversationProviderRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<ConversationProviderRequest, CancellationToken>((request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(new ConversationProviderPayload(
                    ProviderKind.OpenAiCompatible,
                    """{ "choices": [] }""",
                    "resp_workspace_profile_prompt"));
            });

        Mock<IConversationResponseMapper> responseMapper = new(MockBehavior.Strict);
        responseMapper
            .Setup(mapper => mapper.Map(It.IsAny<ConversationProviderPayload>()))
            .Returns(new ConversationResponse(
                "Done.",
                [],
                "resp_workspace_profile_prompt"));

        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new HeuristicTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            responseMapper.Object,
            Mock.Of<IToolExecutionPipeline>(),
            toolRegistry.Object,
            configurationAccessor.Object,
            workspaceAgentProfilePromptProvider: new FixedWorkspaceAgentProfilePromptProvider(
                "Workspace build profile prompt."));

        await ProcessAsync(
            sut,
            "Do the thing.",
            session);

        requests.Should().ContainSingle();
        requests[0].SystemPrompt.Should().Contain("Base prompt");
        requests[0].SystemPrompt.Should().Contain("Workspace build profile prompt.");
        requests[0].SystemPrompt.Should().Contain("planning_mode");
        requests[0].SystemPrompt.Should().NotContain("Active agent profile: build.");
    }

    [Fact]
    public async Task ProcessAsync_Should_InjectLessonMemoryIntoSystemPrompt()
    {
        ReplSessionContext session = CreateSession();
        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings("Base prompt"));

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([]);

        List<ConversationProviderRequest> requests = [];
        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        providerClient
            .Setup(client => client.SendAsync(
                It.IsAny<ConversationProviderRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<ConversationProviderRequest, CancellationToken>((request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(new ConversationProviderPayload(
                    ProviderKind.OpenAiCompatible,
                    """{ "choices": [] }""",
                    "resp_lessons"));
            });

        Mock<IConversationResponseMapper> responseMapper = new(MockBehavior.Strict);
        responseMapper
            .Setup(mapper => mapper.Map(It.IsAny<ConversationProviderPayload>()))
            .Returns(new ConversationResponse(
                "Checked the remembered build failure.",
                [],
                "resp_lessons"));

        FixedLessonMemoryService lessonMemoryService = new(
            "Relevant lesson memory:\n- [les_123; lesson; active] Trigger: CS0246. Problem: DI registration was missing. Lesson: Check DI registration first.");

        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new HeuristicTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            responseMapper.Object,
            Mock.Of<IToolExecutionPipeline>(),
            toolRegistry.Object,
            configurationAccessor.Object,
            lessonMemoryService: lessonMemoryService);

        await ProcessAsync(
            sut,
            "Fix the build",
            session);

        requests.Should().ContainSingle();
        requests[0].SystemPrompt.Should().Contain("Relevant lesson memory:");
        requests[0].SystemPrompt.Should().Contain("Check DI registration first");
        lessonMemoryService.Queries.Should().Equal("Fix the build");
    }

    [Fact]
    public async Task ProcessAsync_Should_IncludeSkillRoutingPromptWithoutSkillBody()
    {
        ReplSessionContext session = CreateSession();
        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings("Base prompt"));

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([CreateToolDefinition(AgentToolNames.SkillLoad)]);

        List<ConversationProviderRequest> requests = [];
        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        providerClient
            .Setup(client => client.SendAsync(
                It.IsAny<ConversationProviderRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<ConversationProviderRequest, CancellationToken>((request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(new ConversationProviderPayload(
                    ProviderKind.OpenAiCompatible,
                    """{ "choices": [] }""",
                    "resp_skills"));
            });

        Mock<IConversationResponseMapper> responseMapper = new(MockBehavior.Strict);
        responseMapper
            .Setup(mapper => mapper.Map(It.IsAny<ConversationProviderPayload>()))
            .Returns(new ConversationResponse(
                "Loaded the right routing context.",
                [],
                "resp_skills"));

        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new HeuristicTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            responseMapper.Object,
            Mock.Of<IToolExecutionPipeline>(),
            toolRegistry.Object,
            configurationAccessor.Object,
            skillService: new FixedSkillService(
                "Workspace skills:\n<workspace_skill name=\"dotnet\" path=\".stemcode/skills/dotnet/SKILL.md\">\nUse for .NET tasks.\n</workspace_skill>"));

        await ProcessAsync(
            sut,
            "Fix the dotnet tests.",
            session);

        requests.Should().ContainSingle();
        requests[0].SystemPrompt.Should().Contain("Workspace skills:");
        requests[0].SystemPrompt.Should().Contain("Use for .NET tasks.");
        requests[0].SystemPrompt.Should().NotContain("Run dotnet test after every edit.");
        requests[0].AvailableTools.Select(static tool => tool.Name)
            .Should()
            .Equal(AgentToolNames.SkillLoad);
    }

    [Fact]
    public async Task ProcessAsync_Should_IncludeSessionStateInSystemPrompt_When_ToolContextExists()
    {
        ReplSessionContext session = CreateSession();
        DateTimeOffset observedAtUtc = new(2026, 4, 23, 9, 0, 0, TimeSpan.Zero);
        session.RecordFileContext(new SessionFileContext(
            "StemCode/Program.cs",
            "read",
            observedAtUtc,
            "Read 500 characters. Excerpt: Host.CreateApplicationBuilder(args)."));
        session.RecordEditContext(new SessionEditContext(
            observedAtUtc.AddMinutes(1),
            "apply_patch (1 file)",
            ["StemCode/Program.cs"],
            3,
            1));
        session.RecordTerminalCommand(new SessionTerminalCommand(
            observedAtUtc.AddMinutes(2),
            "dotnet test StemCode.slnx",
            ".",
            0,
            "Passed! Total: 292",
            null));

        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings("Base prompt"));

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([]);

        List<ConversationProviderRequest> requests = [];
        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        providerClient
            .Setup(client => client.SendAsync(
                It.IsAny<ConversationProviderRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<ConversationProviderRequest, CancellationToken>((request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(new ConversationProviderPayload(
                    ProviderKind.OpenAiCompatible,
                    """{ "choices": [] }""",
                    "resp_state"));
            });

        Mock<IConversationResponseMapper> responseMapper = new(MockBehavior.Strict);
        responseMapper
            .Setup(mapper => mapper.Map(It.IsAny<ConversationProviderPayload>()))
            .Returns(new ConversationResponse(
                "Used the remembered state.",
                [],
                "resp_state"));

        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new HeuristicTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            responseMapper.Object,
            Mock.Of<IToolExecutionPipeline>(),
            toolRegistry.Object,
            configurationAccessor.Object);

        await ProcessAsync(
            sut,
            "Continue from there.",
            session);

        requests.Should().ContainSingle();
        requests[0].SystemPrompt.Should().Contain("Session state:");
        requests[0].SystemPrompt.Should().Contain("StemCode/Program.cs");
        requests[0].SystemPrompt.Should().Contain("apply_patch (1 file)");
        requests[0].SystemPrompt.Should().Contain("dotnet test StemCode.slnx");
    }

    [Fact]
    public async Task ProcessAsync_Should_FilterAvailableTools_When_ProfileIsReadOnly()
    {
        ReplSessionContext session = CreateSession(BuiltInAgentProfiles.Plan);
        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings("Base prompt"));

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([
                CreateToolDefinition(AgentToolNames.ApplyPatch),
                CreateToolDefinition(AgentToolNames.DirectoryList),
                CreateToolDefinition(AgentToolNames.FileRead),
                CreateToolDefinition(AgentToolNames.FileWrite),
                CreateToolDefinition(AgentToolNames.ShellCommand),
                CreateToolDefinition(AgentToolNames.TextSearch)
            ]);

        List<ConversationProviderRequest> requests = [];
        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        providerClient
            .Setup(client => client.SendAsync(
                It.IsAny<ConversationProviderRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<ConversationProviderRequest, CancellationToken>((request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(new ConversationProviderPayload(
                    ProviderKind.OpenAiCompatible,
                    """{ "choices": [] }""",
                    "resp_1"));
            });

        Mock<IConversationResponseMapper> responseMapper = new(MockBehavior.Strict);
        responseMapper
            .Setup(mapper => mapper.Map(It.IsAny<ConversationProviderPayload>()))
            .Returns(new ConversationResponse(
                "Here is the plan.",
                [],
                "resp_1"));

        Mock<IToolExecutionPipeline> toolExecutionPipeline = new(MockBehavior.Strict);

        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new HeuristicTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            responseMapper.Object,
            toolExecutionPipeline.Object,
            toolRegistry.Object,
            configurationAccessor.Object);

        await ProcessAsync(
            sut,
            "Plan this safely.",
            session);

        requests.Should().ContainSingle();
        requests[0].SystemPrompt.Should().Contain("Active agent profile: plan.");
        requests[0].SystemPrompt.Should().Contain("evidence-based implementation plan");
        requests[0].SystemPrompt.Should().Contain("Do not patch, write files, install dependencies");
        requests[0].AvailableTools.Select(static tool => tool.Name)
            .Should()
            .Equal(
                AgentToolNames.DirectoryList,
                AgentToolNames.FileRead,
                AgentToolNames.ShellCommand,
                AgentToolNames.TextSearch);
        requests[0].AvailableTools.Select(static tool => tool.Name)
            .Should()
            .NotContain([AgentToolNames.ApplyPatch, AgentToolNames.FileWrite]);
    }

    [Fact]
    public async Task ProcessAsync_Should_IncludeMcpAndCustomTools_When_ProfileFiltersBuiltInTools()
    {
        ReplSessionContext session = CreateSession(BuiltInAgentProfiles.Plan);
        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings());

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([
                CreateToolDefinition(AgentToolNames.FileWrite),
                CreateToolDefinition("custom__word_count"),
                CreateToolDefinition("mcp__docs__search")
            ]);

        List<ConversationProviderRequest> requests = [];
        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        providerClient
            .Setup(client => client.SendAsync(
                It.IsAny<ConversationProviderRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<ConversationProviderRequest, CancellationToken>((request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(new ConversationProviderPayload(
                    ProviderKind.OpenAiCompatible,
                    """{ "choices": [] }""",
                    "resp_mcp"));
            });

        Mock<IConversationResponseMapper> responseMapper = new(MockBehavior.Strict);
        responseMapper
            .Setup(mapper => mapper.Map(It.IsAny<ConversationProviderPayload>()))
            .Returns(new ConversationResponse(
                "Done.",
                [],
                "resp_mcp"));

        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new HeuristicTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            responseMapper.Object,
            Mock.Of<IToolExecutionPipeline>(),
            toolRegistry.Object,
            configurationAccessor.Object);

        await ProcessAsync(
            sut,
            "Use MCP docs.",
            session);

        requests.Should().ContainSingle();
        requests[0].AvailableTools.Select(static tool => tool.Name)
            .Should()
            .Equal("custom__word_count", "mcp__docs__search");
    }

    [Fact]
    public async Task ProcessAsync_Should_RecoverEmptyStopResponse_When_MapperMarksResponseRetryable()
    {
        ReplSessionContext session = CreateSession();
        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings("Base prompt"));

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([CreateToolDefinition(AgentToolNames.PlanningMode)]);

        List<ConversationProviderRequest> requests = [];
        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        providerClient
            .Setup(client => client.SendAsync(
                It.IsAny<ConversationProviderRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<ConversationProviderRequest, CancellationToken>((request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(new ConversationProviderPayload(
                    ProviderKind.OpenAiCompatible,
                    """{ "choices": [] }""",
                    $"resp_{requests.Count}"));
            });

        Mock<IConversationResponseMapper> responseMapper = new(MockBehavior.Strict);
        responseMapper
            .SetupSequence(mapper => mapper.Map(It.IsAny<ConversationProviderPayload>()))
            .Throws(new ConversationResponseException(
                "The provider returned neither assistant content, a refusal, nor usable tool calls. Finish reason: stop.",
                isRetryableEmptyResponse: true))
            .Returns(new ConversationResponse(
                "Implemented the refactor.",
                [],
                "resp_2"));

        Mock<IToolExecutionPipeline> toolExecutionPipeline = new(MockBehavior.Strict);

        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new HeuristicTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            responseMapper.Object,
            toolExecutionPipeline.Object,
            toolRegistry.Object,
            configurationAccessor.Object);

        ConversationTurnResult result = await ProcessAsync(
            sut,
            "Implement the next refactor.",
            session);

        result.ResponseText.Should().Be("Implemented the refactor.");
        requests.Should().HaveCount(2);
        requests[0].SystemPrompt.Should().NotContain("previous provider response was empty");
        requests[1].SystemPrompt.Should().Contain("Recovery attempt 1 of 3");
        requests[1].SystemPrompt.Should().Contain("previous provider response was empty");
        requests[1].SystemPrompt.Should().Contain("materially advances the work");
        requests[1].SystemPrompt.Should().Contain("Do not return empty content");
        requests[1].SystemPrompt.Should().Contain("planning_mode");
        requests[1].Messages.Should().HaveCount(1);
        requests[1].Messages[0].Content.Should().Be("Implement the next refactor.");
        session.ConversationHistory.Should().HaveCount(2);
        session.ConversationHistory[1].Content.Should().Be("Implemented the refactor.");
        toolExecutionPipeline.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProcessAsync_Should_RecoverRawToolCallMarkupResponse_When_MapperMarksResponseRetryable()
    {
        ReplSessionContext session = CreateSession();
        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings("Base prompt"));

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([CreateToolDefinition(AgentToolNames.UpdatePlan)]);

        List<ConversationProviderRequest> requests = [];
        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        providerClient
            .Setup(client => client.SendAsync(
                It.IsAny<ConversationProviderRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<ConversationProviderRequest, CancellationToken>((request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(new ConversationProviderPayload(
                    ProviderKind.OpenAiCompatible,
                    """{ "choices": [] }""",
                    $"resp_{requests.Count}"));
            });

        Mock<IConversationResponseMapper> responseMapper = new(MockBehavior.Strict);
        responseMapper
            .SetupSequence(mapper => mapper.Map(It.IsAny<ConversationProviderPayload>()))
            .Throws(new ConversationResponseException(
                "The provider returned raw tool-call markup in assistant content instead of a structured tool call.",
                isRetryableRawToolCallResponse: true))
            .Returns(new ConversationResponse(
                "Continuing without raw protocol markers.",
                [],
                "resp_2"));

        Mock<IToolExecutionPipeline> toolExecutionPipeline = new(MockBehavior.Strict);

        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new HeuristicTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            responseMapper.Object,
            toolExecutionPipeline.Object,
            toolRegistry.Object,
            configurationAccessor.Object);

        ConversationTurnResult result = await ProcessAsync(
            sut,
            "Update the plan.",
            session);

        result.ResponseText.Should().Be("Continuing without raw protocol markers.");
        requests.Should().HaveCount(2);
        requests[0].SystemPrompt.Should().NotContain("raw tool-call protocol text");
        requests[1].SystemPrompt.Should().Contain("Recovery attempt 1 of 3");
        requests[1].SystemPrompt.Should().Contain("raw tool-call protocol text");
        requests[1].SystemPrompt.Should().Contain("<|channel>call:");
        requests[1].SystemPrompt.Should().Contain("<tool_call|>");
        requests[1].SystemPrompt.Should().Contain("valid structured tool call");
        toolExecutionPipeline.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProcessAsync_Should_RetryFinalResponse_When_LivePlanStillHasIncompleteWork()
    {
        ReplSessionContext session = CreateSession();
        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings("Base prompt"));

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([
                CreateToolDefinition(AgentToolNames.UpdatePlan),
                CreateToolDefinition(AgentToolNames.FileWrite)
            ]);

        List<ConversationProviderRequest> requests = [];
        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        providerClient
            .Setup(client => client.SendAsync(
                It.IsAny<ConversationProviderRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<ConversationProviderRequest, CancellationToken>((request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(new ConversationProviderPayload(
                    ProviderKind.OpenAiCompatible,
                    """{ "choices": [] }""",
                    $"resp_{requests.Count}"));
            });

        Mock<IConversationResponseMapper> responseMapper = new(MockBehavior.Strict);
        responseMapper
            .SetupSequence(mapper => mapper.Map(It.IsAny<ConversationProviderPayload>()))
            .Returns(new ConversationResponse(
                null,
                [new ConversationToolCall("call_plan_1", AgentToolNames.UpdatePlan, "{}")],
                "resp_1"))
            .Returns(new ConversationResponse(
                "This final-looking message should be retried because the plan is still incomplete.",
                [],
                "resp_2"))
            .Returns(new ConversationResponse(
                null,
                [new ConversationToolCall("call_plan_2", AgentToolNames.UpdatePlan, "{}")],
                "resp_3"))
            .Returns(new ConversationResponse(
                "All planned work is complete.",
                [],
                "resp_4"));

        Mock<IToolExecutionPipeline> toolExecutionPipeline = new(MockBehavior.Strict);
        toolExecutionPipeline
            .Setup(pipeline => pipeline.ExecuteAsync(
                It.Is<IReadOnlyList<ConversationToolCall>>(calls =>
                    calls.Count == 1 &&
                    calls[0].Name == AgentToolNames.UpdatePlan &&
                    calls[0].Id == "call_plan_1"),
                session,
                ConversationExecutionPhase.Execution,
                It.Is<IReadOnlySet<string>>(names =>
                    names.Contains(AgentToolNames.UpdatePlan) &&
                    names.Contains(AgentToolNames.FileWrite)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolExecutionBatchResult([
                new ToolInvocationResult(
                    "call_plan_1",
                    AgentToolNames.UpdatePlan,
                    ToolResultFactory.Success(
                        "Plan updated: 1 completed, 1 in progress, 1 pending.",
                        new PlanUpdateResult(
                            null,
                            [
                                new PlanUpdateItem("Inspect Program.cs", "completed"),
                                new PlanUpdateItem("Rewrite Program.cs", "in_progress"),
                                new PlanUpdateItem("Run build", "pending")
                            ],
                            1,
                            1,
                            1),
                        ToolJsonContext.Default.PlanUpdateResult))
            ]));
        toolExecutionPipeline
            .Setup(pipeline => pipeline.ExecuteAsync(
                It.Is<IReadOnlyList<ConversationToolCall>>(calls =>
                    calls.Count == 1 &&
                    calls[0].Name == AgentToolNames.UpdatePlan &&
                    calls[0].Id == "call_plan_2"),
                session,
                ConversationExecutionPhase.Execution,
                It.Is<IReadOnlySet<string>>(names =>
                    names.Contains(AgentToolNames.UpdatePlan) &&
                    names.Contains(AgentToolNames.FileWrite)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolExecutionBatchResult([
                new ToolInvocationResult(
                    "call_plan_2",
                    AgentToolNames.UpdatePlan,
                    ToolResultFactory.Success(
                        "Plan updated: 3 completed.",
                        new PlanUpdateResult(
                            null,
                            [
                                new PlanUpdateItem("Inspect Program.cs", "completed"),
                                new PlanUpdateItem("Rewrite Program.cs", "completed"),
                                new PlanUpdateItem("Run build", "completed")
                            ],
                            3,
                            0,
                            0),
                        ToolJsonContext.Default.PlanUpdateResult))
            ]));

        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new HeuristicTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            responseMapper.Object,
            toolExecutionPipeline.Object,
            toolRegistry.Object,
            configurationAccessor.Object);

        ConversationTurnResult result = await ProcessAsync(
            sut,
            "Fix Program.cs.",
            session);

        result.ResponseText.Should().Be("All planned work is complete.");
        requests.Should().HaveCount(4);
        requests[1].SystemPrompt.Should().NotContain("live update_plan still had");
        requests[2].SystemPrompt.Should().Contain("live update_plan still had");
        requests[2].SystemPrompt.Should().Contain("calling the appropriate available tools");
        requests[3].Messages.Should().Contain(message =>
            string.Equals(message.Role, "tool", StringComparison.Ordinal) &&
            message.ToolCallId == "call_plan_2");
        toolExecutionPipeline.VerifyAll();
    }

    [Fact]
    public async Task ProcessAsync_Should_AcceptFinalResponseAndCompleteLivePlan_When_PlanRepairIsIgnored()
    {
        ReplSessionContext session = CreateSession();
        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings("Base prompt"));

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([
                CreateToolDefinition(AgentToolNames.UpdatePlan),
                CreateToolDefinition(AgentToolNames.FileWrite)
            ]);

        List<ConversationProviderRequest> requests = [];
        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        providerClient
            .Setup(client => client.SendAsync(
                It.IsAny<ConversationProviderRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<ConversationProviderRequest, CancellationToken>((request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(new ConversationProviderPayload(
                    ProviderKind.OpenAiCompatible,
                    """{ "choices": [] }""",
                    $"resp_{requests.Count}"));
            });

        Mock<IConversationResponseMapper> responseMapper = new(MockBehavior.Strict);
        responseMapper
            .SetupSequence(mapper => mapper.Map(It.IsAny<ConversationProviderPayload>()))
            .Returns(new ConversationResponse(
                null,
                [new ConversationToolCall("call_plan_1", AgentToolNames.UpdatePlan, "{}")],
                "resp_1"))
            .Returns(new ConversationResponse(
                "First final text before the plan is synchronized.",
                [],
                "resp_2"))
            .Returns(new ConversationResponse(
                "Completed the implementation and validation.",
                [],
                "resp_3"));

        Mock<IToolExecutionPipeline> toolExecutionPipeline = new(MockBehavior.Strict);
        toolExecutionPipeline
            .Setup(pipeline => pipeline.ExecuteAsync(
                It.Is<IReadOnlyList<ConversationToolCall>>(calls =>
                    calls.Count == 1 &&
                    calls[0].Name == AgentToolNames.UpdatePlan &&
                    calls[0].Id == "call_plan_1"),
                session,
                ConversationExecutionPhase.Execution,
                It.Is<IReadOnlySet<string>>(names =>
                    names.Contains(AgentToolNames.UpdatePlan) &&
                    names.Contains(AgentToolNames.FileWrite)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolExecutionBatchResult([
                new ToolInvocationResult(
                    "call_plan_1",
                    AgentToolNames.UpdatePlan,
                    ToolResultFactory.Success(
                        "Plan updated: 1 completed, 1 in progress, 1 pending.",
                        new PlanUpdateResult(
                            null,
                            [
                                new PlanUpdateItem("Inspect Program.cs", "completed"),
                                new PlanUpdateItem("Rewrite Program.cs", "in_progress"),
                                new PlanUpdateItem("Run build", "pending")
                            ],
                            1,
                            1,
                            1),
                        ToolJsonContext.Default.PlanUpdateResult))
            ]));

        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new HeuristicTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            responseMapper.Object,
            toolExecutionPipeline.Object,
            toolRegistry.Object,
            configurationAccessor.Object);
        RecordingConversationProgressSink progressSink = new();

        ConversationTurnResult result = await sut.ProcessAsync(
            "Fix Program.cs.",
            session,
            progressSink,
            CancellationToken.None);

        result.ResponseText.Should().Be("Completed the implementation and validation.");
        requests.Should().HaveCount(3);
        requests[1].SystemPrompt.Should().NotContain("live update_plan still had");
        requests[2].SystemPrompt.Should().Contain("live update_plan still had");
        requests[2].SystemPrompt.Should().Contain("Recovery attempt 1 of 1");
        progressSink.PlanProgressUpdates.Should().HaveCount(2);
        progressSink.PlanProgressUpdates[0].CompletedTaskCount.Should().Be(1);
        progressSink.PlanProgressUpdates[1].Tasks.Should().Equal(
            "Inspect Program.cs",
            "Rewrite Program.cs",
            "Run build");
        progressSink.PlanProgressUpdates[1].CompletedTaskCount.Should().Be(3);
        progressSink.PlanProgressUpdates[1].CurrentTaskIndex.Should().Be(-1);
        session.ConversationHistory.Should().HaveCount(2);
        session.ConversationHistory[1].Content.Should().Be("Completed the implementation and validation.");
        toolExecutionPipeline.VerifyAll();
    }

    [Fact]
    public async Task ProcessAsync_Should_Throw_When_EmptyStopRecoveryIsExhausted()
    {
        ReplSessionContext session = CreateSession();
        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings("Base prompt"));

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([CreateToolDefinition(AgentToolNames.PlanningMode)]);

        List<ConversationProviderRequest> requests = [];
        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        providerClient
            .Setup(client => client.SendAsync(
                It.IsAny<ConversationProviderRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<ConversationProviderRequest, CancellationToken>((request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(new ConversationProviderPayload(
                    ProviderKind.OpenAiCompatible,
                    """{ "choices": [] }""",
                    $"resp_{requests.Count}"));
            });

        Mock<IConversationResponseMapper> responseMapper = new(MockBehavior.Strict);
        responseMapper
            .Setup(mapper => mapper.Map(It.IsAny<ConversationProviderPayload>()))
            .Throws(new ConversationResponseException(
                "The provider returned neither assistant content, a refusal, nor usable tool calls. Finish reason: stop.",
                isRetryableEmptyResponse: true));

        Mock<IToolExecutionPipeline> toolExecutionPipeline = new(MockBehavior.Strict);

        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new HeuristicTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            responseMapper.Object,
            toolExecutionPipeline.Object,
            toolRegistry.Object,
            configurationAccessor.Object);

        Func<Task> act = () => ProcessAsync(
            sut,
            "Help me with this refactor.",
            session);

        ConversationResponseException exception = (await act.Should()
                .ThrowAsync<ConversationResponseException>())
            .Which;
        exception.Message.Should().Contain("provider returned unusable output after 4 request(s)");
        exception.Message.Should().Contain("Last provider issue");
        exception.IsRetryableProviderOutput.Should().BeFalse();
        requests.Should().HaveCount(4);
        requests[0].SystemPrompt.Should().NotContain("Recovery attempt");
        requests[1].SystemPrompt.Should().Contain("previous provider response was empty");
        requests[1].SystemPrompt.Should().Contain("Recovery attempt 1 of 3");
        requests[2].SystemPrompt.Should().Contain("Recovery attempt 2 of 3");
        requests[3].SystemPrompt.Should().Contain("Recovery attempt 3 of 3");
        requests[3].SystemPrompt.Should().Contain("another tool-less empty response");
        session.ConversationHistory.Should().ContainSingle();
        session.ConversationTurns.Should().ContainSingle();
        session.ConversationTurns[0].Status.Should().Be(ConversationTurnStatus.Interrupted);
        session.PendingExecutionPlan.Should().BeNull();
        toolExecutionPipeline.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProcessAsync_Should_ReturnPlanResponseWithoutSavingPendingPlan_When_UserRequestsPlan()
    {
        ReplSessionContext session = CreateSession();
        const string planningSummary =
            """
            Objective
            - Plan the refactor first.

            Plan
            1. Inspect the affected files.
            2. Apply the refactor.
            3. Run validation.
            """;

        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings("Base prompt"));

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([
                CreateToolDefinition(AgentToolNames.PlanningMode),
                CreateToolDefinition(AgentToolNames.FileRead),
                CreateToolDefinition(AgentToolNames.ShellCommand)
            ]);

        List<ConversationProviderRequest> requests = [];
        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        providerClient
            .Setup(client => client.SendAsync(
                It.IsAny<ConversationProviderRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<ConversationProviderRequest, CancellationToken>((request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(new ConversationProviderPayload(
                    ProviderKind.OpenAiCompatible,
                    """{ "choices": [] }""",
                    "resp_1"));
            });

        Mock<IConversationResponseMapper> responseMapper = new(MockBehavior.Strict);
        responseMapper
            .Setup(mapper => mapper.Map(It.IsAny<ConversationProviderPayload>()))
            .Returns(new ConversationResponse(
                planningSummary,
                [],
                "resp_1"));

        Mock<IToolExecutionPipeline> toolExecutionPipeline = new(MockBehavior.Strict);

        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new HeuristicTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            responseMapper.Object,
            toolExecutionPipeline.Object,
            toolRegistry.Object,
            configurationAccessor.Object);

        ConversationTurnResult result = await ProcessAsync(
            sut,
            "Help me plan this refactor",
            session);

        result.ResponseText.Should().Be(planningSummary);
        requests.Should().HaveCount(1);
        requests[0].SystemPrompt.Should().Contain("planning_mode");
        requests[0].SystemPrompt.Should().Contain("immediate next step first");
        requests[0].SystemPrompt.Should().Contain("high-quality ordered task list");
        requests[0].SystemPrompt.Should().Contain("Verified facts, Assumptions / open questions");
        requests[0].SystemPrompt.Should().Contain("recommend the best path");
        requests[0].SystemPrompt.Should().Contain("Avoid low-quality plans such as");
        requests[0].SystemPrompt.Should().Contain("scaffold stays non-interactive");
        requests[0].SystemPrompt.Should().NotContain("You are StemCode in Planning Mode.");
        session.PendingExecutionPlan.Should().BeNull();
        session.ConversationHistory.Should().HaveCount(2);
        session.ConversationHistory[0].Content.Should().Be("Help me plan this refactor");
        session.ConversationHistory[1].Content.Should().Be(planningSummary);
        toolExecutionPipeline.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProcessAsync_Should_ExecuteSavedPlan_When_UserApprovesPendingPlan()
    {
        ReplSessionContext session = CreateSession();
        const string planningSummary =
            """
            Objective
            - Plan the refactor first.

            Plan
            1. Inspect the affected files.
            2. Apply the refactor.
            3. Run validation.
            """;

        session.AddConversationTurn("Help me plan this refactor", planningSummary);
        session.SetPendingExecutionPlan(new PendingExecutionPlan(
            "Help me plan this refactor",
            planningSummary,
            [
                "Inspect the affected files.",
                "Apply the refactor.",
                "Run validation."
            ]));

        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings("Base prompt"));

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([
                CreateToolDefinition(AgentToolNames.PlanningMode),
                CreateToolDefinition(AgentToolNames.FileRead),
                CreateToolDefinition(AgentToolNames.FileWrite),
                CreateToolDefinition(AgentToolNames.ShellCommand)
            ]);

        List<ConversationProviderRequest> requests = [];
        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        providerClient
            .Setup(client => client.SendAsync(
                It.IsAny<ConversationProviderRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<ConversationProviderRequest, CancellationToken>((request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(new ConversationProviderPayload(
                    ProviderKind.OpenAiCompatible,
                    """{ "choices": [] }""",
                    "resp_exec"));
            });

        Mock<IConversationResponseMapper> responseMapper = new(MockBehavior.Strict);
        responseMapper
            .Setup(mapper => mapper.Map(It.IsAny<ConversationProviderPayload>()))
            .Returns(new ConversationResponse(
                "Implemented the approved plan.",
                [],
                "resp_exec"));

        Mock<IToolExecutionPipeline> toolExecutionPipeline = new(MockBehavior.Strict);

        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new HeuristicTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            responseMapper.Object,
            toolExecutionPipeline.Object,
            toolRegistry.Object,
            configurationAccessor.Object);

        ConversationTurnResult result = await ProcessAsync(
            sut,
            "continue",
            session);

        result.ResponseText.Should().Be("Implemented the approved plan.");
        requests.Should().HaveCount(1);
        requests[0].SystemPrompt.Should().Contain("APPROVED EXECUTION PHASE IS ACTIVE.");
        requests[0].SystemPrompt.Should().Contain("one task at a time");
        requests[0].SystemPrompt.Should().Contain("Keep the immediate next step explicit");
        requests[0].SystemPrompt.Should().Contain("revise it deliberately instead of following it blindly");
        requests[0].SystemPrompt.Should().Contain("1. Inspect the affected files.");
        requests[0].SystemPrompt.Should().Contain("use fully specified, non-interactive commands");
        requests[0].Messages.Should().HaveCount(3);
        requests[0].Messages[0].Content.Should().Be("Help me plan this refactor");
        requests[0].Messages[1].Content.Should().Be(planningSummary);
        requests[0].Messages[2].Content.Should().Be("continue");
        session.PendingExecutionPlan.Should().BeNull();
        session.ConversationHistory.Should().HaveCount(4);
        session.ConversationHistory[2].Content.Should().Be("continue");
        session.ConversationHistory[3].Content.Should().Be("Implemented the approved plan.");
        toolExecutionPipeline.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProcessAsync_Should_PreservePendingPlanHistory_When_AfterTaskCompleteRejectsApprovedPlan()
    {
        ReplSessionContext session = CreateSession();
        const string planningSummary =
            """
            Objective
            - Plan the refactor first.

            Plan
            1. Inspect the affected files.
            2. Apply the refactor.
            3. Run validation.
            """;

        session.AddConversationTurn("Help me plan this refactor", planningSummary);
        session.SetPendingExecutionPlan(new PendingExecutionPlan(
            "Help me plan this refactor",
            planningSummary,
            [
                "Inspect the affected files.",
                "Apply the refactor.",
                "Run validation."
            ]));

        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings("Base prompt"));

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([
                CreateToolDefinition(AgentToolNames.PlanningMode),
                CreateToolDefinition(AgentToolNames.FileRead),
                CreateToolDefinition(AgentToolNames.FileWrite),
                CreateToolDefinition(AgentToolNames.ShellCommand)
            ]);

        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        providerClient
            .Setup(client => client.SendAsync(
                It.IsAny<ConversationProviderRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationProviderPayload(
                ProviderKind.OpenAiCompatible,
                """{ "choices": [] }""",
                "resp_exec"));

        Mock<IConversationResponseMapper> responseMapper = new(MockBehavior.Strict);
        responseMapper
            .Setup(mapper => mapper.Map(It.IsAny<ConversationProviderPayload>()))
            .Returns(new ConversationResponse(
                "Implemented the approved plan.",
                [],
                "resp_exec"));

        RejectingAfterTaskCompleteLifecycleHookService hookService = new();
        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new HeuristicTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            responseMapper.Object,
            Mock.Of<IToolExecutionPipeline>(),
            toolRegistry.Object,
            configurationAccessor.Object,
            lifecycleHookService: hookService);

        Func<Task> action = () => ProcessAsync(
            sut,
            "continue",
            session);

        await action.Should().ThrowAsync<ConversationPipelineException>()
            .WithMessage("Rejected after completion.");
        session.PendingExecutionPlan.Should().NotBeNull();
        session.ConversationHistory.Should().HaveCount(3);
        session.ConversationHistory[0].Content.Should().Be("Help me plan this refactor");
        session.ConversationHistory[1].Content.Should().Be(planningSummary);
        session.ConversationHistory[2].Content.Should().Be("continue");
        session.ConversationTurns[1].Status.Should().Be(ConversationTurnStatus.Interrupted);
    }

    [Fact]
    public async Task ProcessAsync_Should_ExecutePlanningModeToolAndWriteToolWithinSingleConversation()
    {
        ReplSessionContext session = CreateSession();
        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings());

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([
                CreateToolDefinition(AgentToolNames.PlanningMode),
                CreateToolDefinition(AgentToolNames.FileWrite)
            ]);

        List<ConversationProviderRequest> requests = [];
        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        providerClient
            .Setup(client => client.SendAsync(
                It.IsAny<ConversationProviderRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<ConversationProviderRequest, CancellationToken>((request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(new ConversationProviderPayload(
                    ProviderKind.OpenAiCompatible,
                    """{ "choices": [] }""",
                    $"resp_{requests.Count}"));
            });

        Mock<IConversationResponseMapper> responseMapper = new(MockBehavior.Strict);
        responseMapper
            .SetupSequence(mapper => mapper.Map(It.IsAny<ConversationProviderPayload>()))
            .Returns(new ConversationResponse(
                null,
                [new ConversationToolCall(
                    "plan_call_1",
                    AgentToolNames.PlanningMode,
                    """{ "objective": "Update the README." }""")],
                "resp_1",
                ReasoningContent: "The request needs a plan before writing files."))
            .Returns(new ConversationResponse(
                null,
                [new ConversationToolCall(
                    "exec_call_1",
                    AgentToolNames.FileWrite,
                    """{ "path": "README.md", "content": "hello" }""")],
                "resp_2",
                ReasoningDetailsJson: """
                    [
                      {
                        "type": "reasoning.text",
                        "text": "The plan is active, so the README write is the next tool call."
                      }
                    ]
                    """))
            .Returns(new ConversationResponse(
                "Implemented the requested change.",
                [],
                "resp_3",
                ReasoningContent: "The requested README update has been completed."));

        Mock<IToolExecutionPipeline> toolExecutionPipeline = new(MockBehavior.Strict);
        toolExecutionPipeline
            .Setup(pipeline => pipeline.ExecuteAsync(
                It.Is<IReadOnlyList<ConversationToolCall>>(calls =>
                    calls.Count == 1 &&
                    calls[0].Name == AgentToolNames.PlanningMode),
                session,
                ConversationExecutionPhase.Execution,
                It.Is<IReadOnlySet<string>>(names =>
                    names.Contains(AgentToolNames.PlanningMode) &&
                    names.Contains(AgentToolNames.FileWrite)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolExecutionBatchResult([
                new ToolInvocationResult(
                    "plan_call_1",
                    AgentToolNames.PlanningMode,
                    ToolResultFactory.Success(
                        "Planning mode activated for 'Update the README.'.",
                        new PlanningModeResult(
                            "Update the README.",
                            [
                                "Inspect relevant files and facts before editing.",
                                "Write a concise plan grounded in the current workspace."
                            ],
                            ["Objective", "Plan"]),
                        ToolJsonContext.Default.PlanningModeResult,
                        new ToolRenderPayload("Planning mode active", "Update the README.")))
            ]));
        toolExecutionPipeline
            .Setup(pipeline => pipeline.ExecuteAsync(
                It.Is<IReadOnlyList<ConversationToolCall>>(calls =>
                    calls.Count == 1 &&
                    calls[0].Name == AgentToolNames.FileWrite),
                session,
                ConversationExecutionPhase.Execution,
                It.Is<IReadOnlySet<string>>(names =>
                    names.Contains(AgentToolNames.PlanningMode) &&
                    names.Contains(AgentToolNames.FileWrite)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolExecutionBatchResult([
                new ToolInvocationResult(
                    "exec_call_1",
                    AgentToolNames.FileWrite,
                    ToolResultFactory.Success(
                        "Created README.md.",
                        new ToolErrorPayload("ok", "ok"),
                        ToolJsonContext.Default.ToolErrorPayload,
                        new ToolRenderPayload("File write complete", "README.md")))
            ]));

        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new HeuristicTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            responseMapper.Object,
            toolExecutionPipeline.Object,
            toolRegistry.Object,
            configurationAccessor.Object);

        RecordingConversationProgressSink progressSink = new();

        ConversationTurnResult result = await sut.ProcessAsync(
            "Update the README.",
            session,
            progressSink,
            CancellationToken.None);

        result.Kind.Should().Be(ConversationTurnResultKind.AssistantMessage);
        result.ResponseText.Should().Be("Implemented the requested change.");
        result.ToolExecutionResult.Should().NotBeNull();
        result.ToolExecutionResult!.Results.Select(static item => item.ToolName)
            .Should()
            .Equal(AgentToolNames.PlanningMode, AgentToolNames.FileWrite);
        result.ToolExecutionResult.Results[1].Result.RenderPayload.Should().NotBeNull();
        result.ToolExecutionResult.Results[1].Result.RenderPayload!.Title.Should().Be("File write complete");
        progressSink.PlanProgressUpdates.Should().BeEmpty();
        progressSink.StartedToolBatches.Should().HaveCount(2);
        progressSink.CompletedToolBatches.Should().HaveCount(2);
        requests.Should().HaveCount(3);
        requests[1].Messages.Should().HaveCount(3);
        requests[1].Messages[1].Role.Should().Be("assistant");
        requests[1].Messages[1].ReasoningContent.Should().Be("The request needs a plan before writing files.");
        requests[1].Messages[1].ToolCalls.Should().ContainSingle();
        requests[1].Messages[1].ToolCalls[0].Name.Should().Be(AgentToolNames.PlanningMode);
        requests[1].Messages[2].Role.Should().Be("tool");
        requests[2].Messages.Should().HaveCount(5);
        requests[2].Messages[3].ReasoningDetailsJson.Should().Contain("README write is the next tool call");

        using JsonDocument toolFeedbackDocument = JsonDocument.Parse(requests[2].Messages[^1].Content!);
        JsonElement toolFeedback = toolFeedbackDocument.RootElement;
        toolFeedback.GetProperty("ToolName").GetString().Should().Be(AgentToolNames.FileWrite);
        toolFeedback.GetProperty("Status").GetString().Should().Be("Success");
        toolFeedback.GetProperty("IsSuccess").GetBoolean().Should().BeTrue();
        toolFeedback.GetProperty("ConsecutiveFailureCount").GetInt32().Should().Be(0);
        toolFeedback.GetProperty("Message").GetString().Should().Be("Created README.md.");
        toolFeedback.TryGetProperty("Render", out _).Should().BeFalse();
        toolFeedback.GetProperty("Data").GetProperty("Code").GetString().Should().Be("ok");
        session.ConversationHistory.Should().HaveCount(2);
        session.ConversationHistory[1].Content.Should().Be("Implemented the requested change.");
        session.ConversationHistory[1].ReasoningContent.Should().Be("The requested README update has been completed.");

        ConversationSectionSnapshot snapshot = session.CreateSectionSnapshot(session.SectionCreatedAtUtc.AddMinutes(1));
        snapshot.Turns.Should().ContainSingle();
        snapshot.Turns[0].ToolCalls.Select(static call => call.Name)
            .Should()
            .Equal(AgentToolNames.PlanningMode, AgentToolNames.FileWrite);
        snapshot.Turns[0].ToolCalls[0].ArgumentsJson.Should().Contain("Update the README.");
        snapshot.Turns[0].ToolCalls[1].ArgumentsJson.Should().Contain("README.md");
        snapshot.Turns[0].ToolOutputMessages.Should().HaveCount(2);
        snapshot.Turns[0].ToolOutputMessages[0].Should().Contain("Planning mode active");
        snapshot.Turns[0].ToolOutputMessages[1].Should().Contain("File write complete");
    }

    [Fact]
    public async Task ProcessAsync_Should_ReportAssistantReasoningProgress()
    {
        ReplSessionContext session = CreateSession();
        session.SetThinkingMode("on");
        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings());

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([CreateToolDefinition(AgentToolNames.FileRead)]);

        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        providerClient
            .Setup(client => client.SendAsync(
                It.IsAny<ConversationProviderRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationProviderPayload(
                ProviderKind.OpenAiCompatible,
                """{ "choices": [] }""",
                "resp_reasoning_progress"));

        Mock<IConversationResponseMapper> responseMapper = new(MockBehavior.Strict);
        responseMapper
            .Setup(mapper => mapper.Map(It.IsAny<ConversationProviderPayload>()))
            .Returns(new ConversationResponse(
                "Done.",
                [],
                "resp_reasoning_progress",
                ReasoningContent: "I inspected the request and can answer directly."));

        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new HeuristicTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            responseMapper.Object,
            Mock.Of<IToolExecutionPipeline>(),
            toolRegistry.Object,
            configurationAccessor.Object);
        RecordingConversationProgressSink progressSink = new();

        ConversationTurnResult result = await sut.ProcessAsync(
            "Answer this.",
            session,
            progressSink,
            CancellationToken.None);

        result.ResponseText.Should().Be("Done.");
        result.ReasoningText.Should().Be("I inspected the request and can answer directly.");
        progressSink.AssistantReasoningUpdates.Should().ContainSingle()
            .Which.Should().Be("I inspected the request and can answer directly.");
        session.ConversationHistory[1].ReasoningContent.Should()
            .Be("I inspected the request and can answer directly.");
    }

    [Fact]
    public async Task ProcessAsync_Should_ReportReasoningDetailsText_When_ReasoningContentIsMissing()
    {
        ReplSessionContext session = CreateSession();
        session.SetThinkingMode("on");
        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings());

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([]);

        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        providerClient
            .Setup(client => client.SendAsync(
                It.IsAny<ConversationProviderRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationProviderPayload(
                ProviderKind.OpenAiCompatible,
                """{ "choices": [] }""",
                "resp_reasoning_details_progress"));

        Mock<IConversationResponseMapper> responseMapper = new(MockBehavior.Strict);
        responseMapper
            .Setup(mapper => mapper.Map(It.IsAny<ConversationProviderPayload>()))
            .Returns(new ConversationResponse(
                "Done.",
                [],
                "resp_reasoning_details_progress",
                ReasoningDetailsJson: """
                    [
                      { "type": "reasoning.text", "text": "First internal summary." },
                      { "type": "reasoning.summary", "summary": ["Second internal summary."] }
                    ]
                    """));

        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new HeuristicTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            responseMapper.Object,
            Mock.Of<IToolExecutionPipeline>(),
            toolRegistry.Object,
            configurationAccessor.Object);
        RecordingConversationProgressSink progressSink = new();

        ConversationTurnResult result = await sut.ProcessAsync(
            "Answer this.",
            session,
            progressSink,
            CancellationToken.None);

        result.ReasoningText.Should().Contain("First internal summary.");
        result.ReasoningText.Should().Contain("Second internal summary.");
        progressSink.AssistantReasoningUpdates.Should().ContainSingle()
            .Which.Should().Contain("First internal summary.");
        progressSink.AssistantReasoningUpdates[0].Should().Contain("Second internal summary.");
    }

    [Fact]
    public async Task ProcessAsync_Should_IncrementToolFailureCount_AndResetAfterSuccessfulToolResult()
    {
        ReplSessionContext session = CreateSession();
        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings("Base prompt"));

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([
                CreateToolDefinition(AgentToolNames.FileRead),
                CreateToolDefinition(AgentToolNames.FileWrite),
                CreateToolDefinition(AgentToolNames.ShellCommand),
                CreateToolDefinition(AgentToolNames.UpdatePlan)
            ]);

        List<ConversationProviderRequest> requests = [];
        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        providerClient
            .Setup(client => client.SendAsync(
                It.IsAny<ConversationProviderRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<ConversationProviderRequest, CancellationToken>((request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(new ConversationProviderPayload(
                    ProviderKind.OpenAiCompatible,
                    """{ "choices": [] }""",
                    $"resp_{requests.Count}"));
            });

        Mock<IConversationResponseMapper> responseMapper = new(MockBehavior.Strict);
        responseMapper
            .SetupSequence(mapper => mapper.Map(It.IsAny<ConversationProviderPayload>()))
            .Returns(new ConversationResponse(
                null,
                [
                    new ConversationToolCall("call_fail_1", AgentToolNames.FileRead, """{"path":""}"""),
                    new ConversationToolCall("call_fail_2", AgentToolNames.ShellCommand, "{}"),
                    new ConversationToolCall("call_success", AgentToolNames.UpdatePlan, """{"plan":[]}"""),
                    new ConversationToolCall("call_fail_3", AgentToolNames.FileWrite, "{}")
                ],
                "resp_1"))
            .Returns(new ConversationResponse(
                "Handled the tool results.",
                [],
                "resp_2"));

        Mock<IToolExecutionPipeline> toolExecutionPipeline = new(MockBehavior.Strict);
        toolExecutionPipeline
            .Setup(pipeline => pipeline.ExecuteAsync(
                It.Is<IReadOnlyList<ConversationToolCall>>(calls => calls.Count == 4),
                session,
                ConversationExecutionPhase.Execution,
                It.Is<IReadOnlySet<string>>(names =>
                    names.Contains(AgentToolNames.FileRead) &&
                    names.Contains(AgentToolNames.FileWrite) &&
                    names.Contains(AgentToolNames.ShellCommand) &&
                    names.Contains(AgentToolNames.UpdatePlan)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolExecutionBatchResult([
                new ToolInvocationResult(
                    "call_fail_1",
                    AgentToolNames.FileRead,
                    ToolResultFactory.InvalidArguments(
                        "missing_path",
                        "Path is required.",
                        new ToolRenderPayload("Invalid file_read arguments", "Provide a path."))),
                new ToolInvocationResult(
                    "call_fail_2",
                    AgentToolNames.ShellCommand,
                    ToolResultFactory.InvalidArguments(
                        "missing_command",
                        "Command is required.",
                        new ToolRenderPayload("Invalid shell_command arguments", "Provide a command."))),
                new ToolInvocationResult(
                    "call_success",
                    AgentToolNames.UpdatePlan,
                    ToolResultFactory.Success(
                        "Plan updated.",
                        new PlanUpdateResult(null, [], 0, 0, 0),
                        ToolJsonContext.Default.PlanUpdateResult)),
                new ToolInvocationResult(
                    "call_fail_3",
                    AgentToolNames.FileWrite,
                    ToolResultFactory.InvalidArguments(
                        "missing_content",
                        "Content is required.",
                        new ToolRenderPayload("Invalid file_write arguments", "Provide content.")))
            ]));

        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new HeuristicTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            responseMapper.Object,
            toolExecutionPipeline.Object,
            toolRegistry.Object,
            configurationAccessor.Object);

        ConversationTurnResult result = await ProcessAsync(
            sut,
            "Run several tools.",
            session);

        result.ResponseText.Should().Be("Handled the tool results.");
        requests.Should().HaveCount(2);
        JsonElement[] toolFeedback = requests[1].Messages
            .Where(static message => string.Equals(message.Role, "tool", StringComparison.Ordinal))
            .Select(static message => JsonDocument.Parse(message.Content!).RootElement.Clone())
            .ToArray();

        toolFeedback.Select(static feedback => feedback.GetProperty("ConsecutiveFailureCount").GetInt32())
            .Should()
            .Equal(1, 2, 0, 1);
        toolFeedback[2].GetProperty("IsSuccess").GetBoolean().Should().BeTrue();
        toolFeedback[3].GetProperty("IsSuccess").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task ProcessAsync_Should_ReportLivePlanProgress_When_UpdatePlanToolRuns()
    {
        ReplSessionContext session = CreateSession();
        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings("Base prompt"));

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([
                CreateToolDefinition(AgentToolNames.UpdatePlan),
                CreateToolDefinition(AgentToolNames.FileRead)
            ]);

        List<ConversationProviderRequest> requests = [];
        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        providerClient
            .Setup(client => client.SendAsync(
                It.IsAny<ConversationProviderRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<ConversationProviderRequest, CancellationToken>((request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(new ConversationProviderPayload(
                    ProviderKind.OpenAiCompatible,
                    """{ "choices": [] }""",
                    $"resp_{requests.Count}"));
            });

        Mock<IConversationResponseMapper> responseMapper = new(MockBehavior.Strict);
        responseMapper
            .SetupSequence(mapper => mapper.Map(It.IsAny<ConversationProviderPayload>()))
            .Returns(new ConversationResponse(
                null,
                [
                    new ConversationToolCall(
                        "plan_call_1",
                        AgentToolNames.UpdatePlan,
                        """
                        {
                          "plan": [
                            { "step": "Inspect planning flow", "status": "completed" },
                            { "step": "Wire live plan progress", "status": "in_progress" },
                            { "step": "Run validation", "status": "pending" }
                          ]
                        }
                        """)
                ],
                "resp_1"))
            .Returns(new ConversationResponse(
                "Updated the planning system.",
                [],
                "resp_2"));

        Mock<IToolExecutionPipeline> toolExecutionPipeline = new(MockBehavior.Strict);
        toolExecutionPipeline
            .Setup(pipeline => pipeline.ExecuteAsync(
                It.Is<IReadOnlyList<ConversationToolCall>>(calls =>
                    calls.Count == 1 &&
                    calls[0].Name == AgentToolNames.UpdatePlan),
                session,
                ConversationExecutionPhase.Execution,
                It.Is<IReadOnlySet<string>>(names =>
                    names.Contains(AgentToolNames.UpdatePlan) &&
                    names.Contains(AgentToolNames.FileRead)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolExecutionBatchResult([
                new ToolInvocationResult(
                    "plan_call_1",
                    AgentToolNames.UpdatePlan,
                    ToolResultFactory.Success(
                        "Plan updated: 3 completed.",
                        new PlanUpdateResult(
                            null,
                            [
                                new PlanUpdateItem("Inspect planning flow", "completed"),
                                new PlanUpdateItem("Wire live plan progress", "completed"),
                                new PlanUpdateItem("Run validation", "completed")
                            ],
                            3,
                            0,
                            0),
                        ToolJsonContext.Default.PlanUpdateResult,
                        new ToolRenderPayload("Plan updated", "plan")))
            ]));

        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new HeuristicTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            responseMapper.Object,
            toolExecutionPipeline.Object,
            toolRegistry.Object,
            configurationAccessor.Object);

        RecordingConversationProgressSink progressSink = new();

        ConversationTurnResult result = await sut.ProcessAsync(
            "Make planning more accurate.",
            session,
            progressSink,
            CancellationToken.None);

        result.ResponseText.Should().Be("Updated the planning system.");
        result.ToolExecutionResult.Should().NotBeNull();
        result.ToolExecutionResult!.Results.Should().ContainSingle();
        result.ToolExecutionResult.Results[0].ToolName.Should().Be(AgentToolNames.UpdatePlan);
        progressSink.PlanProgressUpdates.Should().ContainSingle();
        progressSink.PlanProgressUpdates[0].Tasks.Should().Equal(
            "Inspect planning flow",
            "Wire live plan progress",
            "Run validation");
        progressSink.PlanProgressUpdates[0].CompletedTaskCount.Should().Be(3);
        progressSink.PlanProgressUpdates[0].CurrentTaskIndex.Should().Be(-1);
        progressSink.CompletedToolBatches.Should().ContainSingle();
        requests.Should().HaveCount(2);
        requests[0].AvailableTools.Select(static tool => tool.Name)
            .Should()
            .Equal(AgentToolNames.UpdatePlan, AgentToolNames.FileRead);
        requests[1].Messages.Should().HaveCount(3);
        requests[1].Messages[2].Role.Should().Be("tool");
    }

    [Fact]
    public async Task ProcessAsync_Should_ThrowConversationPipelineException_When_ApiKeyIsMissing()
    {
        ReplSessionContext session = CreateSession();
        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new HeuristicTokenEstimator(),
            secretStore.Object,
            Mock.Of<IConversationProviderClient>(),
            Mock.Of<IConversationResponseMapper>(),
            Mock.Of<IToolExecutionPipeline>(),
            Mock.Of<IToolRegistry>(),
            Mock.Of<IConversationConfigurationAccessor>());

        Func<Task> action = () => ProcessAsync(sut, "hello", session);

        await action.Should().ThrowAsync<ConversationPipelineException>()
            .WithMessage("*API key is missing*");
    }

    [Fact]
    public async Task ProcessAsync_Should_ThrowConversationPipelineException_When_BudgetIsExceededBeforeProviderRequest()
    {
        ReplSessionContext session = CreateSession();
        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings("Base prompt"));

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([CreateToolDefinition(AgentToolNames.PlanningMode)]);

        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        Mock<IConversationResponseMapper> responseMapper = new(MockBehavior.Strict);
        Mock<IToolExecutionPipeline> toolExecutionPipeline = new(MockBehavior.Strict);
        Mock<IBudgetControlsUsageService> budgetControlsUsageService = new(MockBehavior.Strict);
        budgetControlsUsageService
            .Setup(service => service.GetStatusAsync(
                session,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BudgetControlsStatus(
                BudgetControlsSettings.LocalSource,
                MonthlyBudgetUsd: 10m,
                SpentUsd: 10m,
                AlertThresholdPercent: 80,
                BudgetControlsSettings.DefaultLocalPath,
                CloudApiUrl: null,
                HasCloudAuthKey: false));

        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new HeuristicTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            responseMapper.Object,
            toolExecutionPipeline.Object,
            toolRegistry.Object,
            configurationAccessor.Object,
            budgetControlsUsageService: budgetControlsUsageService.Object);

        Func<Task> action = () => ProcessAsync(sut, "hello", session);

        await action.Should().ThrowAsync<ConversationPipelineException>()
            .WithMessage("*Budget controls blocked the provider request*");
        providerClient.Verify(client => client.SendAsync(
            It.IsAny<ConversationProviderRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
        responseMapper.VerifyNoOtherCalls();
        toolExecutionPipeline.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProcessAsync_Should_PropagateConversationProviderException_When_ProviderFails()
    {
        ReplSessionContext session = CreateSession();
        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings());

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([CreateToolDefinition(AgentToolNames.ShellCommand)]);

        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        providerClient
            .Setup(client => client.SendAsync(
                It.IsAny<ConversationProviderRequest>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConversationProviderException("Provider unavailable."));

        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new HeuristicTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            Mock.Of<IConversationResponseMapper>(),
            Mock.Of<IToolExecutionPipeline>(),
            toolRegistry.Object,
            configurationAccessor.Object);

        Func<Task> action = () => ProcessAsync(sut, "hello", session);

        await action.Should().ThrowAsync<ConversationProviderException>()
            .WithMessage("Provider unavailable.");
        session.ConversationTurns.Should().ContainSingle();
        session.ConversationTurns[0].Status.Should().Be(ConversationTurnStatus.Interrupted);
    }

    [Fact]
    public async Task ProcessAsync_Should_PreserveUserInput_WhenProviderFails()
    {
        ReplSessionContext session = CreateSession();
        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings());

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([CreateToolDefinition(AgentToolNames.FileRead)]);

        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        providerClient
            .Setup(client => client.SendAsync(
                It.IsAny<ConversationProviderRequest>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConversationProviderException("Provider unavailable."));

        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new HeuristicTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            Mock.Of<IConversationResponseMapper>(),
            Mock.Of<IToolExecutionPipeline>(),
            toolRegistry.Object,
            configurationAccessor.Object);

        await FluentActions.Invoking(() => ProcessAsync(sut, "Implement the round winner component", session))
            .Should()
            .ThrowAsync<ConversationProviderException>();

        session.ConversationTurns.Should().ContainSingle();
        session.ConversationTurns[0].UserInput.Should().Be("Implement the round winner component");
        session.ConversationTurns[0].Status.Should().Be(ConversationTurnStatus.Interrupted);
        session.ConversationTurns[0].AssistantResponse.Should().BeNull();
        session.ConversationHistory.Should().ContainSingle();
        session.ConversationHistory[0].Content.Should().Be("Implement the round winner component");
    }

    [Fact]
    public async Task ProcessAsync_Should_IncludeInterruptedUserTurn_InNextRequest()
    {
        ReplSessionContext session = CreateSession();
        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings());

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([CreateToolDefinition(AgentToolNames.FileRead)]);

        List<ConversationProviderRequest> requests = [];
        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        providerClient
            .Setup(client => client.SendAsync(
                It.IsAny<ConversationProviderRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<ConversationProviderRequest, CancellationToken>((request, _) =>
            {
                if (requests.Count == 0)
                {
                    requests.Add(request);
                    throw new ConversationProviderException("Provider unavailable.");
                }

                requests.Add(request);
                return Task.FromResult(new ConversationProviderPayload(
                    ProviderKind.OpenAiCompatible,
                    """{ "choices": [] }""",
                    "resp_2"));
            });

        Mock<IConversationResponseMapper> responseMapper = new(MockBehavior.Strict);
        responseMapper
            .Setup(mapper => mapper.Map(It.IsAny<ConversationProviderPayload>()))
            .Returns(new ConversationResponse(
                "Completed the work.",
                [],
                "resp_2"));

        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new HeuristicTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            responseMapper.Object,
            Mock.Of<IToolExecutionPipeline>(),
            toolRegistry.Object,
            configurationAccessor.Object);

        await FluentActions.Invoking(() => ProcessAsync(sut, "Implement the round winner component", session))
            .Should()
            .ThrowAsync<ConversationProviderException>();

        session.ConversationTurns[0].Status.Should().Be(ConversationTurnStatus.Interrupted);

        await ProcessAsync(sut, "Complete it", session);

        requests.Should().HaveCount(2);
        string[] userMessages = requests[1].Messages
            .Where(static message => string.Equals(message.Role, "user", StringComparison.Ordinal))
            .Select(static message => message.Content!)
            .ToArray();
        userMessages.Count(static content => content.Contains("Implement the round winner component", StringComparison.Ordinal))
            .Should()
            .Be(1);
        userMessages.Count(static content => string.Equals(content, "Complete it", StringComparison.Ordinal))
            .Should()
            .Be(1);
        requests[1].SystemPrompt.Should().Contain("Recovery context:");
    }

    [Fact]
    public async Task ProcessAsync_Should_PreserveSuccessfulToolResults_WhenLaterRoundFails()
    {
        ReplSessionContext session = CreateSession();
        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings());

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([
                CreateToolDefinition(AgentToolNames.FileRead),
                CreateToolDefinition(AgentToolNames.ApplyPatch)
            ]);

        List<ConversationProviderRequest> requests = [];
        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        providerClient
            .Setup(client => client.SendAsync(
                It.IsAny<ConversationProviderRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<ConversationProviderRequest, CancellationToken>((request, _) =>
            {
                requests.Add(request);
                return requests.Count switch
                {
                    1 => Task.FromResult(new ConversationProviderPayload(
                        ProviderKind.OpenAiCompatible,
                        """{ "choices": [] }""",
                        "resp_1")),
                    2 => throw new ConversationProviderException("Second round failed."),
                    _ => Task.FromResult(new ConversationProviderPayload(
                        ProviderKind.OpenAiCompatible,
                        """{ "choices": [] }""",
                        "resp_3"))
                };
            });

        Mock<IConversationResponseMapper> responseMapper = new(MockBehavior.Strict);
        responseMapper
            .SetupSequence(mapper => mapper.Map(It.IsAny<ConversationProviderPayload>()))
            .Returns(new ConversationResponse(
                null,
                [
                    new ConversationToolCall("call_read", AgentToolNames.FileRead, """{ "path": "README.md" }"""),
                    new ConversationToolCall("call_patch", AgentToolNames.ApplyPatch, """{ "patch": "*** Begin Patch" }""")
                ],
                "resp_1"))
            .Returns(new ConversationResponse(
                "Finished recovering.",
                [],
                "resp_3"));

        Mock<IToolExecutionPipeline> toolExecutionPipeline = new(MockBehavior.Strict);
        toolExecutionPipeline
            .Setup(pipeline => pipeline.ExecuteAsync(
                It.Is<IReadOnlyList<ConversationToolCall>>(calls => calls.Count == 2),
                session,
                ConversationExecutionPhase.Execution,
                It.IsAny<IReadOnlySet<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolExecutionBatchResult([
                new ToolInvocationResult(
                    "call_read",
                    AgentToolNames.FileRead,
                    ToolResultFactory.Success(
                        "Read README.md",
                        new WorkspaceFileReadResult("README.md", "content", 1, 1, 1, false, null, "hash", "utf-8"),
                        ToolJsonContext.Default.WorkspaceFileReadResult)),
                new ToolInvocationResult(
                    "call_patch",
                    AgentToolNames.ApplyPatch,
                    ToolResultFactory.Success(
                        "Applied patch.",
                        new WorkspaceApplyPatchResult(1, 1, 0, []),
                        ToolJsonContext.Default.WorkspaceApplyPatchResult))
            ]));

        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new HeuristicTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            responseMapper.Object,
            toolExecutionPipeline.Object,
            toolRegistry.Object,
            configurationAccessor.Object);

        await FluentActions.Invoking(() => ProcessAsync(sut, "Implement the feature", session))
            .Should()
            .ThrowAsync<ConversationProviderException>();

        session.ConversationTurns.Should().ContainSingle();
        session.ConversationTurns[0].ToolCalls.Should().HaveCount(2);
        session.ConversationTurns[0].ToolOutputMessages.Should().NotBeEmpty();

        await ProcessAsync(sut, "Complete it", session);

        ConversationProviderRequest recoveryRequest = requests.Last();
        recoveryRequest.SystemPrompt.Should().Contain("Recovery context:");
        recoveryRequest.Messages.Should().Contain(message =>
            string.Equals(message.Role, "tool", StringComparison.Ordinal) &&
            message.Content!.Contains("Read README.md", StringComparison.Ordinal));
        recoveryRequest.Messages.Should().Contain(message =>
            string.Equals(message.Role, "tool", StringComparison.Ordinal) &&
            message.Content!.Contains("Edited 0 files", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProcessAsync_Should_NotPersistSecretsInFailureMetadata()
    {
        ReplSessionContext session = CreateSession();
        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings());

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([]);

        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        providerClient
            .Setup(client => client.SendAsync(
                It.IsAny<ConversationProviderRequest>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConversationProviderException("Bearer sk-abcdefghijklmnopqrstuvwxyz123456 failed."));

        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new HeuristicTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            Mock.Of<IConversationResponseMapper>(),
            Mock.Of<IToolExecutionPipeline>(),
            toolRegistry.Object,
            configurationAccessor.Object);

        await FluentActions.Invoking(() => ProcessAsync(sut, "hello", session))
            .Should()
            .ThrowAsync<ConversationProviderException>();

        ConversationFailureInfo? failureInfo = session.ConversationTurns[0].FailureInfo;
        failureInfo.Should().NotBeNull();
        failureInfo!.Category.Should().NotContain("sk-");
        failureInfo.Category.Should().NotContain("Bearer");
        failureInfo.ProviderName.Should().Be(session.ProviderName);
        failureInfo.ModelId.Should().Be(session.ActiveModelId);
    }

    [Fact]
    public async Task ProcessAsync_Should_PreserveProviderException_WhenPersistenceFails()
    {
        ReplSessionContext session = CreateSession();
        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings());

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([]);

        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        providerClient
            .Setup(client => client.SendAsync(
                It.IsAny<ConversationProviderRequest>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConversationProviderException("Provider unavailable."));

        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new HeuristicTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            Mock.Of<IConversationResponseMapper>(),
            Mock.Of<IToolExecutionPipeline>(),
            toolRegistry.Object,
            configurationAccessor.Object,
            sectionService: new ThrowingSectionService());

        ConversationProviderException exception = (await FluentActions
                .Invoking(() => ProcessAsync(sut, "hello", session))
                .Should()
                .ThrowAsync<ConversationProviderException>())
            .Which;

        exception.Message.Should().Be("Provider unavailable.");
    }

    [Fact]
    public async Task ProcessAsync_Should_SendPreviousTurns_When_SubsequentTurnRuns()
    {
        ReplSessionContext session = CreateSession();
        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings());

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([]);

        List<ConversationProviderRequest> requests = [];
        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        providerClient
            .Setup(client => client.SendAsync(
                It.IsAny<ConversationProviderRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<ConversationProviderRequest, CancellationToken>((request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(new ConversationProviderPayload(
                    ProviderKind.OpenAiCompatible,
                    """{ "choices": [] }""",
                    $"resp_{requests.Count}"));
            });

        Mock<IConversationResponseMapper> responseMapper = new(MockBehavior.Strict);
        responseMapper
            .SetupSequence(mapper => mapper.Map(It.IsAny<ConversationProviderPayload>()))
            .Returns(new ConversationResponse("First reply.", [], "resp_1"))
            .Returns(new ConversationResponse("Second reply.", [], "resp_2"));

        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new HeuristicTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            responseMapper.Object,
            Mock.Of<IToolExecutionPipeline>(),
            toolRegistry.Object,
            configurationAccessor.Object);

        await ProcessAsync(sut, "First question", session);
        await ProcessAsync(sut, "What did I just ask?", session);

        requests.Should().HaveCount(2);
        requests[1].Messages.Should().HaveCount(3);
        requests[1].Messages[0].Role.Should().Be("user");
        requests[1].Messages[0].Content.Should().Be("First question");
        requests[1].Messages[1].Role.Should().Be("assistant");
        requests[1].Messages[1].Content.Should().Be("First reply.");
        requests[1].Messages[2].Role.Should().Be("user");
        requests[1].Messages[2].Content.Should().Be("What did I just ask?");
    }

    [Fact]
    public async Task ProcessAsync_Should_NotUseMaxHistoryTurnsForNormalPromptConstruction()
    {
        ReplSessionContext session = CreateSession();
        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings(maxHistoryTurns: 1));

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([]);

        List<ConversationProviderRequest> requests = [];
        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        providerClient
            .Setup(client => client.SendAsync(
                It.IsAny<ConversationProviderRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<ConversationProviderRequest, CancellationToken>((request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(new ConversationProviderPayload(
                    ProviderKind.OpenAiCompatible,
                    """{ "choices": [] }""",
                    $"resp_{requests.Count}"));
            });

        Mock<IConversationResponseMapper> responseMapper = new(MockBehavior.Strict);
        responseMapper
            .SetupSequence(mapper => mapper.Map(It.IsAny<ConversationProviderPayload>()))
            .Returns(new ConversationResponse("Reply one.", [], "resp_1"))
            .Returns(new ConversationResponse("Reply two.", [], "resp_2"))
            .Returns(new ConversationResponse("Reply three.", [], "resp_3"));

        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new HeuristicTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            responseMapper.Object,
            Mock.Of<IToolExecutionPipeline>(),
            toolRegistry.Object,
            configurationAccessor.Object);

        await ProcessAsync(sut, "Question one", session);
        await ProcessAsync(sut, "Question two", session);
        await ProcessAsync(sut, "Question three", session);

        requests.Should().HaveCount(3);
        requests[2].Messages.Should().HaveCount(5);
        requests[2].Messages[0].Role.Should().Be("user");
        requests[2].Messages[0].Content.Should().Be("Question one");
        requests[2].Messages[1].Role.Should().Be("assistant");
        requests[2].Messages[1].Content.Should().Be("Reply one.");
        requests[2].Messages[2].Content.Should().Be("Question two");
        requests[2].Messages[3].Role.Should().Be("assistant");
        requests[2].Messages[3].Content.Should().Be("Reply two.");
        requests[2].Messages[4].Content.Should().Be("Question three");
    }

    [Fact]
    public async Task ProcessAsync_Should_AllowUnlimitedToolRounds_When_MaxToolRoundsPerTurnIsZero()
    {
        ReplSessionContext session = CreateSession();
        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings(maxToolRoundsPerTurn: 0));

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([CreateToolDefinition(AgentToolNames.FileWrite)]);

        List<ConversationProviderRequest> requests = [];
        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        providerClient
            .Setup(client => client.SendAsync(
                It.IsAny<ConversationProviderRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<ConversationProviderRequest, CancellationToken>((request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(new ConversationProviderPayload(
                    ProviderKind.OpenAiCompatible,
                    """{ "choices": [] }""",
                    $"resp_{requests.Count}",
                    requests.Count == 1 ? 2 : 0));
            });

        Mock<IConversationResponseMapper> responseMapper = new(MockBehavior.Strict);
        responseMapper
            .SetupSequence(mapper => mapper.Map(It.IsAny<ConversationProviderPayload>()))
            .Returns(new ConversationResponse(
                null,
                [new ConversationToolCall("call_unlimited_1", AgentToolNames.FileWrite, """{"path":"one.txt"}""")],
                "resp_1"))
            .Returns(new ConversationResponse(
                null,
                [new ConversationToolCall("call_unlimited_2", AgentToolNames.FileWrite, """{"path":"two.txt"}""")],
                "resp_2"))
            .Returns(new ConversationResponse(
                null,
                [new ConversationToolCall("call_unlimited_3", AgentToolNames.FileWrite, """{"path":"three.txt"}""")],
                "resp_3"))
            .Returns(new ConversationResponse(
                "Finished after multiple tool rounds.",
                [],
                "resp_4"));

        Mock<IToolExecutionPipeline> toolExecutionPipeline = new(MockBehavior.Strict);
        toolExecutionPipeline
            .Setup(pipeline => pipeline.ExecuteAsync(
                It.IsAny<IReadOnlyList<ConversationToolCall>>(),
                session,
                ConversationExecutionPhase.Execution,
                It.Is<IReadOnlySet<string>>(names => names.Contains(AgentToolNames.FileWrite)),
                It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<ConversationToolCall>, ReplSessionContext, ConversationExecutionPhase, IReadOnlySet<string>, CancellationToken>(
                (calls, _, _, _, _) => Task.FromResult(new ToolExecutionBatchResult([
                    new ToolInvocationResult(
                        calls[0].Id,
                        calls[0].Name,
                        ToolResultFactory.Success(
                            "Created file.",
                            new ToolErrorPayload("ok", "ok"),
                            ToolJsonContext.Default.ToolErrorPayload))
                ])));

        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new HeuristicTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            responseMapper.Object,
            toolExecutionPipeline.Object,
            toolRegistry.Object,
            configurationAccessor.Object);

        ConversationTurnResult result = await ProcessAsync(
            sut,
            "Keep using tools until finished.",
            session);

        result.ResponseText.Should().Be("Finished after multiple tool rounds.");
        result.Metrics.Should().NotBeNull();
        result.Metrics!.ProviderRetryCount.Should().Be(2);
        result.Metrics.ToolRoundCount.Should().Be(3);
        result.Metrics.EstimatedInputTokens.Should().BeGreaterThan(0);
        requests.Should().HaveCount(4);
        toolExecutionPipeline.Verify(
            pipeline => pipeline.ExecuteAsync(
                It.IsAny<IReadOnlyList<ConversationToolCall>>(),
                session,
                ConversationExecutionPhase.Execution,
                It.IsAny<IReadOnlySet<string>>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(3));
    }

    [Fact]
    public async Task ProcessAsync_Should_ThrowConfiguredLimit_When_ProviderExceedsMaxToolRoundsPerTurn()
    {
        ReplSessionContext session = CreateSession();
        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings(maxToolRoundsPerTurn: 2));

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([CreateToolDefinition(AgentToolNames.FileWrite)]);

        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        providerClient
            .Setup(client => client.SendAsync(
                It.IsAny<ConversationProviderRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationProviderPayload(
                ProviderKind.OpenAiCompatible,
                """{ "choices": [] }""",
                "resp_limit"));

        Mock<IConversationResponseMapper> responseMapper = new(MockBehavior.Strict);
        responseMapper
            .SetupSequence(mapper => mapper.Map(It.IsAny<ConversationProviderPayload>()))
            .Returns(new ConversationResponse(
                null,
                [new ConversationToolCall("call_limit_1", AgentToolNames.FileWrite, """{"path":"index.html"}""")],
                "resp_1"))
            .Returns(new ConversationResponse(
                null,
                [new ConversationToolCall("call_limit_2", AgentToolNames.FileWrite, """{"path":"index.html"}""")],
                "resp_2"));

        Mock<IToolExecutionPipeline> toolExecutionPipeline = new(MockBehavior.Strict);
        toolExecutionPipeline
            .Setup(pipeline => pipeline.ExecuteAsync(
                It.IsAny<IReadOnlyList<ConversationToolCall>>(),
                session,
                ConversationExecutionPhase.Execution,
                It.Is<IReadOnlySet<string>>(names => names.Contains(AgentToolNames.FileWrite)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolExecutionBatchResult([
                new ToolInvocationResult(
                    "call_limit",
                    AgentToolNames.FileWrite,
                    ToolResultFactory.Success(
                        "Created index.html.",
                        new ToolErrorPayload("ok", "ok"),
                        ToolJsonContext.Default.ToolErrorPayload))
            ]));

        AgentConversationPipeline sut = CreateSut(
            TimeProvider.System,
            new HeuristicTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            responseMapper.Object,
            toolExecutionPipeline.Object,
            toolRegistry.Object,
            configurationAccessor.Object);

        Func<Task> action = () => ProcessAsync(
            sut,
            "Keep writing files.",
            session);

        await action.Should().ThrowAsync<ConversationResponseException>()
            .WithMessage("*Configured limit: 2 round(s).*");
    }

    [Fact]
    public async Task ProcessAsync_Should_UpdateCodebaseIndex_When_AutoUpdateAfterTaskEnabled()
    {
        RecordingCodebaseIndexService codebaseIndexService = new();
        AgentConversationPipeline sut = CreateSuccessfulTurnSut(
            codebaseIndexService,
            new CodebaseIndexSettings { AutoUpdateAfterTask = true });

        ConversationTurnResult result = await ProcessAsync(
            sut,
            "Implement the next refactor.",
            CreateSession());

        result.Kind.Should().Be(ConversationTurnResultKind.AssistantMessage);
        codebaseIndexService.BuildCalls.Should().Equal(false);
    }

    [Fact]
    public async Task ProcessAsync_Should_NotUpdateCodebaseIndex_When_AutoUpdateAfterTaskDisabled()
    {
        RecordingCodebaseIndexService codebaseIndexService = new();
        AgentConversationPipeline sut = CreateSuccessfulTurnSut(
            codebaseIndexService,
            new CodebaseIndexSettings { AutoUpdateAfterTask = false });

        ConversationTurnResult result = await ProcessAsync(
            sut,
            "Implement the next refactor.",
            CreateSession());

        result.Kind.Should().Be(ConversationTurnResultKind.AssistantMessage);
        codebaseIndexService.BuildCalls.Should().BeEmpty();
    }

    private static AgentConversationPipeline CreateSuccessfulTurnSut(
        ICodebaseIndexService codebaseIndexService,
        CodebaseIndexSettings codebaseIndexSettings)
    {
        Mock<IApiKeySecretStore> secretStore = new(MockBehavior.Strict);
        secretStore
            .Setup(store => store.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-key");

        Mock<IConversationConfigurationAccessor> configurationAccessor = new(MockBehavior.Strict);
        configurationAccessor
            .Setup(accessor => accessor.GetSettings())
            .Returns(CreateSettings("Base prompt"));

        Mock<IToolRegistry> toolRegistry = new(MockBehavior.Strict);
        toolRegistry
            .Setup(registry => registry.GetToolDefinitions())
            .Returns([CreateToolDefinition(AgentToolNames.FileRead)]);

        Mock<IConversationProviderClient> providerClient = new(MockBehavior.Strict);
        providerClient
            .Setup(client => client.SendAsync(
                It.IsAny<ConversationProviderRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationProviderPayload(
                ProviderKind.OpenAiCompatible,
                """{ "choices": [] }""",
                "resp_1"));

        Mock<IConversationResponseMapper> responseMapper = new(MockBehavior.Strict);
        responseMapper
            .Setup(mapper => mapper.Map(It.IsAny<ConversationProviderPayload>()))
            .Returns(new ConversationResponse(
                "Implemented the refactor.",
                [],
                "resp_1"));

        Mock<IToolExecutionPipeline> toolExecutionPipeline = new(MockBehavior.Strict);

        return CreateSut(
            TimeProvider.System,
            new HeuristicTokenEstimator(),
            secretStore.Object,
            providerClient.Object,
            responseMapper.Object,
            toolExecutionPipeline.Object,
            toolRegistry.Object,
            configurationAccessor.Object,
            codebaseIndexService: codebaseIndexService,
            codebaseIndexSettings: codebaseIndexSettings);
    }

    private static AgentConversationPipeline CreateSut(
        TimeProvider timeProvider,
        ITokenEstimator tokenEstimator,
        IApiKeySecretStore secretStore,
        IConversationProviderClient providerClient,
        IConversationResponseMapper responseMapper,
        IToolExecutionPipeline toolExecutionPipeline,
        IToolRegistry toolRegistry,
        IConversationConfigurationAccessor configurationAccessor,
        IWorkspaceSystemPromptProvider? workspaceSystemPromptProvider = null,
        IWorkspaceAgentProfilePromptProvider? workspaceAgentProfilePromptProvider = null,
        IWorkspaceInstructionsProvider? workspaceInstructionsProvider = null,
        ILessonMemoryService? lessonMemoryService = null,
        ILifecycleHookService? lifecycleHookService = null,
        ISkillService? skillService = null,
        IBudgetControlsUsageService? budgetControlsUsageService = null,
        IReplSectionService? sectionService = null,
        ICodebaseIndexService? codebaseIndexService = null,
        CodebaseIndexSettings? codebaseIndexSettings = null)
    {
        return new AgentConversationPipeline(
            timeProvider,
            tokenEstimator,
            secretStore,
            providerClient,
            responseMapper,
            toolExecutionPipeline,
            toolRegistry,
            configurationAccessor,
            workspaceSystemPromptProvider ?? new EmptyWorkspaceSystemPromptProvider(),
            workspaceInstructionsProvider ?? new EmptyWorkspaceInstructionsProvider(),
            lessonMemoryService ?? new EmptyLessonMemoryService(),
            NullLogger<AgentConversationPipeline>.Instance,
            lifecycleHookService,
            skillService,
            budgetControlsUsageService: budgetControlsUsageService,
            workspaceAgentProfilePromptProvider: workspaceAgentProfilePromptProvider ?? new EmptyWorkspaceAgentProfilePromptProvider(),
            sectionService: sectionService,
            codebaseIndexService: codebaseIndexService,
            codebaseIndexSettings: codebaseIndexSettings);
    }

    private static Task<ConversationTurnResult> ProcessAsync(
        AgentConversationPipeline sut,
        string input,
        ReplSessionContext session)
    {
        return sut.ProcessAsync(
            input,
            session,
            new RecordingConversationProgressSink(),
            CancellationToken.None);
    }

    private static ToolDefinition CreateToolDefinition(string name)
    {
        using JsonDocument schemaDocument = JsonDocument.Parse(
            """{ "type": "object", "properties": {}, "additionalProperties": false }""");

        return new ToolDefinition(
            name,
            $"Description for {name}",
            schemaDocument.RootElement.Clone());
    }

    private static ReplSessionContext CreateSession(
        IAgentProfile? agentProfile = null,
        string? activeProviderName = null,
        string activeModelId = "gpt-5-mini",
        IReadOnlyDictionary<string, int>? modelContextWindowTokens = null)
    {
        return new ReplSessionContext(
            new AgentProviderProfile(ProviderKind.OpenAiCompatible, "https://provider.example.com/v1"),
            activeModelId,
            [activeModelId, "gpt-4.1"],
            agentProfile,
            modelContextWindowTokens: modelContextWindowTokens,
            activeProviderName: activeProviderName);
    }

    private static ConversationSettings CreateSettings(
        string? systemPrompt = null,
        int maxHistoryTurns = 12,
        int maxToolRoundsPerTurn = 32)
    {
        return new ConversationSettings(
            systemPrompt,
            TimeSpan.FromSeconds(30),
            maxHistoryTurns,
            maxToolRoundsPerTurn);
    }

    private sealed class CharacterTokenEstimator : ITokenEstimator
    {
        public int Estimate(string text)
        {
            return string.IsNullOrEmpty(text)
                ? 0
                : text.Length;
        }
    }

    private sealed class RecordingConversationProgressSink : IConversationProgressSink
    {
        public List<string> AssistantReasoningUpdates { get; } = [];

        public List<ExecutionPlanProgress> PlanProgressUpdates { get; } = [];

        public List<IReadOnlyList<ConversationToolCall>> StartedToolBatches { get; } = [];

        public List<ToolExecutionBatchResult> CompletedToolBatches { get; } = [];

        public Task ReportAssistantReasoningAsync(
            string reasoningText,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AssistantReasoningUpdates.Add(reasoningText);
            return Task.CompletedTask;
        }

        public Task ReportExecutionPlanAsync(
            ExecutionPlanProgress executionPlanProgress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PlanProgressUpdates.Add(executionPlanProgress);
            return Task.CompletedTask;
        }

        public Task ReportToolCallsStartedAsync(
            IReadOnlyList<ConversationToolCall> toolCalls,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartedToolBatches.Add(toolCalls);
            return Task.CompletedTask;
        }

        public Task ReportToolResultsAsync(
            ToolExecutionBatchResult toolExecutionResult,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CompletedToolBatches.Add(toolExecutionResult);
            return Task.CompletedTask;
        }
    }

    private sealed class EmptyWorkspaceInstructionsProvider : IWorkspaceInstructionsProvider
    {
        public Task<string?> LoadAsync(
            ReplSessionContext session,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<string?>(null);
        }
    }

    private sealed class EmptyWorkspaceSystemPromptProvider : IWorkspaceSystemPromptProvider
    {
        public Task<string?> LoadAsync(
            ReplSessionContext session,
            string? configuredSystemPrompt,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(configuredSystemPrompt);
        }
    }

    private sealed class EmptyWorkspaceAgentProfilePromptProvider : IWorkspaceAgentProfilePromptProvider
    {
        public Task<string?> LoadAsync(
            ReplSessionContext session,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<string?>(null);
        }
    }

    private sealed class FixedWorkspaceSystemPromptProvider : IWorkspaceSystemPromptProvider
    {
        private readonly Func<string?, string?> _systemPromptFactory;

        public FixedWorkspaceSystemPromptProvider(string? systemPrompt)
            : this(_ => systemPrompt)
        {
        }

        public FixedWorkspaceSystemPromptProvider(Func<string?, string?> systemPromptFactory)
        {
            _systemPromptFactory = systemPromptFactory;
        }

        public Task<string?> LoadAsync(
            ReplSessionContext session,
            string? configuredSystemPrompt,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_systemPromptFactory(configuredSystemPrompt));
        }
    }

    private sealed class FixedWorkspaceAgentProfilePromptProvider : IWorkspaceAgentProfilePromptProvider
    {
        private readonly string? _systemPrompt;

        public FixedWorkspaceAgentProfilePromptProvider(string? systemPrompt)
        {
            _systemPrompt = systemPrompt;
        }

        public Task<string?> LoadAsync(
            ReplSessionContext session,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_systemPrompt);
        }
    }

    private sealed class FixedWorkspaceInstructionsProvider : IWorkspaceInstructionsProvider
    {
        private readonly string? _instructions;

        public FixedWorkspaceInstructionsProvider(string? instructions)
        {
            _instructions = instructions;
        }

        public Task<string?> LoadAsync(
            ReplSessionContext session,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_instructions);
        }
    }

    private sealed class RecordingLifecycleHookService : ILifecycleHookService
    {
        public List<LifecycleHookContext> Contexts { get; } = [];

        public Task<LifecycleHookRunResult> RunAsync(
            LifecycleHookContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Contexts.Add(context);
            return Task.FromResult(LifecycleHookRunResult.Allowed());
        }
    }

    private sealed class RejectingAfterTaskCompleteLifecycleHookService : ILifecycleHookService
    {
        public List<LifecycleHookContext> Contexts { get; } = [];

        public Task<LifecycleHookRunResult> RunAsync(
            LifecycleHookContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Contexts.Add(context);

            return Task.FromResult(
                string.Equals(context.EventName, LifecycleHookEvents.AfterTaskComplete, StringComparison.Ordinal)
                    ? LifecycleHookRunResult.Blocked("reject_after_complete", "Rejected after completion.")
                    : LifecycleHookRunResult.Allowed());
        }
    }

    private sealed class RecordingCodebaseIndexService : ICodebaseIndexService
    {
        public List<bool> BuildCalls { get; } = [];

        public Task<CodebaseIndexBuildResult> BuildAsync(
            bool force,
            CancellationToken cancellationToken)
        {
            BuildCalls.Add(force);
            return Task.FromResult(new CodebaseIndexBuildResult(
                ".stemcode/cache/codebase-index.zvec",
                DateTimeOffset.UnixEpoch,
                IndexedFileCount: 0,
                AddedFileCount: 0,
                UpdatedFileCount: 0,
                RemovedFileCount: 0,
                ReusedFileCount: 0,
                SkippedFileCount: 0,
                DurationMilliseconds: 0,
                new CodebaseIndexStats(0, 0, 0, 0, 0),
                []));
        }

        public Task<CodebaseIndexStatusResult> GetStatusAsync(CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<CodebaseIndexSearchResult> SearchAsync(
            string query,
            int limit,
            bool includeSnippets,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<CodebaseIndexListResult> ListAsync(
            int limit,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class ThrowingSectionService : IReplSectionService
    {
        private int _saveCallCount;

        public Task<ReplSessionContext> CreateNewAsync(
            string applicationName,
            AgentProviderProfile providerProfile,
            string activeModelId,
            IReadOnlyList<string> availableModelIds,
            IAgentProfile agentProfile,
            IReadOnlyDictionary<string, int>? modelContextWindowTokens,
            IReadOnlyDictionary<string, ModelContextMetadata>? modelContextMetadata,
            string? activeProviderName,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<ReplSessionContext> CreateNewWithinSessionAsync(
            string applicationName,
            AgentProviderProfile providerProfile,
            string activeModelId,
            IReadOnlyList<string> availableModelIds,
            IAgentProfile agentProfile,
            IReadOnlyDictionary<string, int>? modelContextWindowTokens,
            IReadOnlyDictionary<string, ModelContextMetadata>? modelContextMetadata,
            string? activeProviderName,
            ReplSessionContext completedSection,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public void EnsureTitleGenerationStarted(
            ReplSessionContext session,
            string firstUserPrompt)
        {
        }

        public Task<ReplSessionContext> ResumeAsync(
            string applicationName,
            string sectionId,
            IAgentProfile? profileOverride,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task SaveIfDirtyAsync(
            ReplSessionContext session,
            CancellationToken cancellationToken)
        {
            _saveCallCount++;
            if (_saveCallCount > 1)
            {
                throw new InvalidOperationException("Persistence failed.");
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(
            ReplSessionContext session,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class EmptyLessonMemoryService : ILessonMemoryService
    {
        public Task<LessonMemoryEntry> SaveAsync(
            LessonMemorySaveRequest request,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<LessonMemoryEntry>> SearchAsync(
            string query,
            int limit,
            bool includeFixed,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<LessonMemoryEntry>>([]);
        }

        public Task<IReadOnlyList<LessonMemoryEntry>> ListAsync(
            int limit,
            bool includeFixed,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<LessonMemoryEntry>>([]);
        }

        public Task<LessonMemoryEntry?> EditAsync(
            LessonMemoryEditRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<LessonMemoryEntry?>(null);
        }

        public Task<bool> DeleteAsync(
            string id,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(false);
        }

        public Task<string?> CreatePromptAsync(
            string query,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<string?>(null);
        }

        public Task ObserveToolResultAsync(
            ConversationToolCall toolCall,
            ToolInvocationResult invocationResult,
            CancellationToken cancellationToken,
            ReplSessionContext? session = null)
        {
            return Task.CompletedTask;
        }

        public string GetStoragePath()
        {
            return ".stemcode/memory/lessons.jsonl";
        }
    }

    private sealed class FixedLessonMemoryService : ILessonMemoryService
    {
        private readonly string? _prompt;

        public FixedLessonMemoryService(string? prompt)
        {
            _prompt = prompt;
        }

        public List<string> Queries { get; } = [];

        public Task<LessonMemoryEntry> SaveAsync(
            LessonMemorySaveRequest request,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<LessonMemoryEntry>> SearchAsync(
            string query,
            int limit,
            bool includeFixed,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<LessonMemoryEntry>>([]);
        }

        public Task<IReadOnlyList<LessonMemoryEntry>> ListAsync(
            int limit,
            bool includeFixed,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<LessonMemoryEntry>>([]);
        }

        public Task<LessonMemoryEntry?> EditAsync(
            LessonMemoryEditRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<LessonMemoryEntry?>(null);
        }

        public Task<bool> DeleteAsync(
            string id,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(false);
        }

        public Task<string?> CreatePromptAsync(
            string query,
            CancellationToken cancellationToken)
        {
            Queries.Add(query);
            return Task.FromResult(_prompt);
        }

        public Task ObserveToolResultAsync(
            ConversationToolCall toolCall,
            ToolInvocationResult invocationResult,
            CancellationToken cancellationToken,
            ReplSessionContext? session = null)
        {
            return Task.CompletedTask;
        }

        public string GetStoragePath()
        {
            return ".stemcode/memory/lessons.jsonl";
        }
    }

    private sealed class FixedSkillService : ISkillService
    {
        private readonly string? _routingPrompt;

        public FixedSkillService(string? routingPrompt)
        {
            _routingPrompt = routingPrompt;
        }

        public Task<IReadOnlyList<WorkspaceSkillDescriptor>> ListAsync(
            ReplSessionContext session,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<WorkspaceSkillDescriptor>>([
                new WorkspaceSkillDescriptor(
                    "dotnet",
                    "Use for .NET tasks.",
                    ".stemcode/skills/dotnet/SKILL.md")
            ]);
        }

        public Task<string?> CreateRoutingPromptAsync(
            ReplSessionContext session,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(_routingPrompt);
        }

        public Task<WorkspaceSkillLoadResult?> LoadAsync(
            ReplSessionContext session,
            string name,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<WorkspaceSkillLoadResult?>(new WorkspaceSkillLoadResult(
                "dotnet",
                "Use for .NET tasks.",
                ".stemcode/skills/dotnet/SKILL.md",
                "Run dotnet test after every edit.",
                33,
                false));
        }
    }
}
