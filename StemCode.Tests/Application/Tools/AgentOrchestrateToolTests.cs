using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using StemCode.Application.Abstractions;
using StemCode.Application.Models;
using StemCode.Application.Profiles;
using StemCode.Application.Services;
using StemCode.Application.Tools;
using StemCode.Application.Tools.Models;
using StemCode.Application.Tools.Serialization;
using StemCode.Domain.Models;
using System.Text.Json;

namespace StemCode.Tests.Application.Tools;

public sealed class AgentOrchestrateToolTests
{
    [Fact]
    public void Schema_Should_ListWorkspaceSubagents()
    {
        using TempWorkspace workspace = TempWorkspace.Create();
        string agentsDirectory = Path.Combine(workspace.Path, ".stemcode", "agents");
        Directory.CreateDirectory(agentsDirectory);
        File.WriteAllText(
            Path.Combine(agentsDirectory, "qa_agent.md"),
            """
            ---
            name: qa-agent
            mode: subagent
            description: Validates focused changes.
            ---
            Inspect validation risk and report the relevant commands.
            """);

        using ServiceProvider serviceProvider = new ServiceCollection().BuildServiceProvider();
        AgentOrchestrateTool sut = new(
            serviceProvider,
            new BuiltInAgentProfileResolver(new FixedWorkspaceRootProvider(workspace.Path)),
            new HeuristicTokenEstimator(),
            new ToolExecutionSettings());

        using JsonDocument schema = JsonDocument.Parse(sut.Schema);
        string[] agentNames = schema.RootElement
            .GetProperty("properties")
            .GetProperty("tasks")
            .GetProperty("items")
            .GetProperty("properties")
            .GetProperty("agent")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(static value => value.GetString())
            .Where(static value => value is not null)
            .Select(static value => value!)
            .ToArray();

        agentNames.Should().Contain("general");
        agentNames.Should().Contain("explore");
        agentNames.Should().Contain("qa-agent");
    }

    [Fact]
    public async Task ExecuteAsync_Should_RunReadOnlyTasksInParallel()
    {
        ReplSessionContext parentSession = CreateSession(BuiltInAgentProfiles.Build);
        TaskCompletionSource bothStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int startedCount = 0;

        Mock<IConversationPipeline> conversationPipeline = new(MockBehavior.Strict);
        conversationPipeline
            .Setup(pipeline => pipeline.ProcessAsync(
                It.IsAny<string>(),
                It.IsAny<ReplSessionContext>(),
                It.IsAny<IConversationProgressSink>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, ReplSessionContext, IConversationProgressSink, CancellationToken>(async (input, _, _, token) =>
            {
                if (Interlocked.Increment(ref startedCount) == 2)
                {
                    bothStarted.TrySetResult();
                }

                await release.Task.WaitAsync(token);
                return ConversationTurnResult.AssistantMessage(
                    input.Contains("routing", StringComparison.Ordinal)
                        ? "Routing lives in AgentConversationPipeline."
                        : "Profiles live in BuiltInAgentProfiles.",
                    metrics: new ConversationTurnMetrics(TimeSpan.FromMilliseconds(20), 4));
            });

        using ServiceProvider serviceProvider = new ServiceCollection()
            .AddSingleton(conversationPipeline.Object)
            .AddSingleton(Mock.Of<IWorkspaceFileService>())
            .BuildServiceProvider();
        AgentOrchestrateTool sut = new(
            serviceProvider,
            new BuiltInAgentProfileResolver(),
            new HeuristicTokenEstimator(),
            new ToolExecutionSettings());

        Task<ToolResult> runTask = sut.ExecuteAsync(
            CreateContext(
                parentSession,
                """
                {
                  "strategy": "parallel_readonly",
                  "tasks": [
                    { "agent": "explore", "task": "Find routing code" },
                    { "agent": "explore", "task": "Find profile code" }
                  ]
                }
                """),
            CancellationToken.None);

        await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        release.SetResult();
        ToolResult result = await runTask;

        result.Status.Should().Be(ToolResultStatus.Success);
        AgentOrchestrationResult payload = JsonSerializer.Deserialize(
            result.JsonResult,
            ToolJsonContext.Default.AgentOrchestrationResult)!;
        payload.Strategy.Should().Be("parallel_readonly");
        payload.Tasks.Should().HaveCount(2);
        payload.SucceededTaskCount.Should().Be(2);
        payload.Tasks.Select(static task => task.AgentName).Should().Equal("explore", "explore");
    }

    [Fact]
    public async Task ExecuteAsync_Should_RunWorktreeIsolatedEditingTasksInParallel()
    {
        ReplSessionContext parentSession = CreateSession(BuiltInAgentProfiles.Build);
        TaskCompletionSource bothStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int startedCount = 0;
        int worktreeCount = 0;
        IWorkspaceRootProvider rootProvider = new StemCode.Infrastructure.Tools.CurrentDirectoryWorkspaceRootProvider();
        System.Collections.Concurrent.ConcurrentBag<string> rootsSeen = [];

        Mock<IGitWorktreeService> worktreeService = new(MockBehavior.Strict);
        worktreeService
            .Setup(service => service.CreateAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                int number = Interlocked.Increment(ref worktreeCount);
                string path = Path.Combine(Path.GetTempPath(), $"stemcode-orchestrate-wt-{number}");
                return new GitWorktreeHandle($"wt-{number}", Path.GetTempPath(), path, path, $"stemcode/worktree/wt-{number}", "abc");
            });
        worktreeService
            .Setup(service => service.FinishAsync(It.IsAny<GitWorktreeHandle>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GitWorktreeHandle handle, CancellationToken _) => new GitWorktreeOutcome(handle, Kept: false, [], 0));

        Mock<IConversationPipeline> conversationPipeline = new(MockBehavior.Strict);
        conversationPipeline
            .Setup(pipeline => pipeline.ProcessAsync(
                It.IsAny<string>(),
                It.IsAny<ReplSessionContext>(),
                It.IsAny<IConversationProgressSink>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, ReplSessionContext, IConversationProgressSink, CancellationToken>(async (_, _, _, token) =>
            {
                rootsSeen.Add(rootProvider.GetWorkspaceRoot());
                if (Interlocked.Increment(ref startedCount) == 2)
                {
                    bothStarted.TrySetResult();
                }

                await release.Task.WaitAsync(token);
                rootsSeen.Add(rootProvider.GetWorkspaceRoot());
                return ConversationTurnResult.AssistantMessage("Done.");
            });

        using ServiceProvider serviceProvider = new ServiceCollection()
            .AddSingleton(conversationPipeline.Object)
            .AddSingleton(Mock.Of<IWorkspaceFileService>())
            .AddSingleton(worktreeService.Object)
            .AddSingleton(rootProvider)
            .BuildServiceProvider();
        AgentOrchestrateTool sut = new(
            serviceProvider,
            new BuiltInAgentProfileResolver(),
            new HeuristicTokenEstimator(),
            new ToolExecutionSettings());

        Task<ToolResult> runTask = sut.ExecuteAsync(
            CreateContext(
                parentSession,
                """
                {
                  "tasks": [
                    { "agent": "general", "task": "Fix module A", "isolation": "worktree" },
                    { "agent": "general", "task": "Fix module B", "isolation": "worktree" }
                  ]
                }
                """),
            CancellationToken.None);

        await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        release.SetResult();
        ToolResult result = await runTask;

        result.Status.Should().Be(ToolResultStatus.Success);
        rootsSeen.Distinct().Should().HaveCount(2, "each isolated task keeps its own workspace root, even while running concurrently");
        rootsSeen.Should().OnlyContain(static root => root.Contains("stemcode-orchestrate-wt-"));
        AgentOrchestrationResult payload = JsonSerializer.Deserialize(
            result.JsonResult,
            ToolJsonContext.Default.AgentOrchestrationResult)!;
        payload.Tasks.Should().OnlyContain(static task => task.Worktree != null && !task.Worktree.Kept);
    }

    [Fact]
    public async Task ExecuteAsync_Should_RunEditingCapableTasksSequentiallyInAutoMode()
    {
        ReplSessionContext parentSession = CreateSession(BuiltInAgentProfiles.Build);
        int activeCount = 0;
        int maxActiveCount = 0;
        List<string> observedInputs = [];

        Mock<IConversationPipeline> conversationPipeline = new(MockBehavior.Strict);
        conversationPipeline
            .Setup(pipeline => pipeline.ProcessAsync(
                It.IsAny<string>(),
                It.IsAny<ReplSessionContext>(),
                It.IsAny<IConversationProgressSink>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, ReplSessionContext, IConversationProgressSink, CancellationToken>(async (input, childSession, _, token) =>
            {
                int active = Interlocked.Increment(ref activeCount);
                maxActiveCount = Math.Max(maxActiveCount, active);
                observedInputs.Add(input);

                try
                {
                    childSession.RecordFileEditTransaction(new WorkspaceFileEditTransaction(
                        "child edit",
                        [new WorkspaceFileEditState($"src/{observedInputs.Count}.cs", true, "before")],
                        [new WorkspaceFileEditState($"src/{observedInputs.Count}.cs", true, "after")]));
                    await Task.Delay(25, token);
                    return ConversationTurnResult.AssistantMessage("Completed focused edit.");
                }
                finally
                {
                    Interlocked.Decrement(ref activeCount);
                }
            });

        using ServiceProvider serviceProvider = new ServiceCollection()
            .AddSingleton(conversationPipeline.Object)
            .AddSingleton(Mock.Of<IWorkspaceFileService>())
            .BuildServiceProvider();
        AgentOrchestrateTool sut = new(
            serviceProvider,
            new BuiltInAgentProfileResolver(),
            new HeuristicTokenEstimator(),
            new ToolExecutionSettings());

        ToolResult result = await sut.ExecuteAsync(
            CreateContext(
                parentSession,
                """
                {
                  "tasks": [
                    { "agent": "general", "task": "Edit parser", "writeScope": "StemCode/Application/Commands" },
                    { "agent": "general", "task": "Edit tests", "writeScope": "StemCode.Tests/Application/Commands" }
                  ]
                }
                """),
            CancellationToken.None);

        result.Status.Should().Be(ToolResultStatus.Success);
        maxActiveCount.Should().Be(1);
        observedInputs[0].Should().Contain("Edit parser");
        observedInputs[0].Should().Contain("Write scope:");
        observedInputs[1].Should().Contain("Edit tests");
        parentSession.TryGetPendingUndoFileEdit(out WorkspaceFileEditTransaction? transaction)
            .Should()
            .BeTrue();
        transaction.Should().NotBeNull();

        AgentOrchestrationResult payload = JsonSerializer.Deserialize(
            result.JsonResult,
            ToolJsonContext.Default.AgentOrchestrationResult)!;
        payload.RecordedFileEdits.Should().BeTrue();
        payload.Tasks.Should().OnlyContain(static task => task.RecordedFileEdits);
    }

    [Fact]
    public async Task ExecuteAsync_Should_DenyEditingSubagent_When_ParentProfileIsReadOnly()
    {
        ReplSessionContext parentSession = CreateSession(BuiltInAgentProfiles.Plan);
        Mock<IConversationPipeline> conversationPipeline = new(MockBehavior.Strict);

        using ServiceProvider serviceProvider = new ServiceCollection()
            .AddSingleton(conversationPipeline.Object)
            .AddSingleton(Mock.Of<IWorkspaceFileService>())
            .BuildServiceProvider();
        AgentOrchestrateTool sut = new(
            serviceProvider,
            new BuiltInAgentProfileResolver(),
            new HeuristicTokenEstimator(),
            new ToolExecutionSettings());

        ToolResult result = await sut.ExecuteAsync(
            CreateContext(
                parentSession,
                """
                {
                  "tasks": [
                    { "agent": "general", "task": "Implement a fix" }
                  ]
                }
                """),
            CancellationToken.None);

        result.Status.Should().Be(ToolResultStatus.PermissionDenied);
        result.JsonResult.Should().Contain("readonly_profile_cannot_orchestrate_edits");
        conversationPipeline.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_Should_RespectConfiguredOrchestrationTimeout()
    {
        ReplSessionContext parentSession = CreateSession(BuiltInAgentProfiles.Build);

        Mock<IConversationPipeline> conversationPipeline = new(MockBehavior.Strict);
        conversationPipeline
            .Setup(pipeline => pipeline.ProcessAsync(
                It.IsAny<string>(),
                It.IsAny<ReplSessionContext>(),
                It.IsAny<IConversationProgressSink>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, ReplSessionContext, IConversationProgressSink, CancellationToken>(async (_, _, _, token) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token);
                return ConversationTurnResult.AssistantMessage("Completed late.");
            });

        using ServiceProvider serviceProvider = new ServiceCollection()
            .AddSingleton(conversationPipeline.Object)
            .AddSingleton(Mock.Of<IWorkspaceFileService>())
            .BuildServiceProvider();
        AgentOrchestrateTool sut = new(
            serviceProvider,
            new BuiltInAgentProfileResolver(),
            new HeuristicTokenEstimator(),
            new ToolExecutionSettings
            {
                AgentOrchestrationTimeoutSeconds = 1
            });

        ToolResult result = await sut.ExecuteAsync(
            CreateContext(
                parentSession,
                """
                {
                  "tasks": [
                    { "agent": "general", "task": "Edit parser" }
                  ]
                }
                """),
            CancellationToken.None);

        result.Status.Should().Be(ToolResultStatus.ExecutionError);
        result.JsonResult.Should().Contain("agent_orchestration_timeout");
        result.Message.Should().Contain("timed out after 1 seconds");
    }

    private static ToolExecutionContext CreateContext(
        ReplSessionContext session,
        string argumentsJson)
    {
        using JsonDocument document = JsonDocument.Parse(argumentsJson);
        return new ToolExecutionContext(
            "call_agents",
            AgentToolNames.AgentOrchestrate,
            document.RootElement.Clone(),
            session);
    }

    private static ReplSessionContext CreateSession(IAgentProfile profile)
    {
        return new ReplSessionContext(
            new AgentProviderProfile(ProviderKind.OpenAiCompatible, "https://provider.example.com/v1"),
            "gpt-5-mini",
            ["gpt-5-mini", "gpt-4.1"],
            agentProfile: profile);
    }

    private sealed class FixedWorkspaceRootProvider : IWorkspaceRootProvider
    {
        private readonly string _workspaceRoot;

        public FixedWorkspaceRootProvider(string workspaceRoot)
        {
            _workspaceRoot = workspaceRoot;
        }

        public string GetWorkspaceRoot()
        {
            return _workspaceRoot;
        }
    }

    private sealed class TempWorkspace : IDisposable
    {
        private TempWorkspace(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TempWorkspace Create()
        {
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "stemcode-agent-orchestrate-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempWorkspace(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
