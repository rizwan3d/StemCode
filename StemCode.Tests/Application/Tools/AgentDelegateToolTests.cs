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

public sealed class AgentDelegateToolTests
{
    [Fact]
    public void Schema_Should_ListWorkspaceSubagents()
    {
        using TempWorkspace workspace = TempWorkspace.Create();
        string agentsDirectory = Path.Combine(workspace.Path, ".stemcode", "agents");
        Directory.CreateDirectory(agentsDirectory);
        File.WriteAllText(
            Path.Combine(agentsDirectory, "code-reviewer.md"),
            """
            ---
            name: code-reviewer
            mode: subagent
            description: Reviews code changes for regressions.
            ---
            Inspect changes and report findings first.
            """);

        using ServiceProvider serviceProvider = new ServiceCollection().BuildServiceProvider();
        AgentDelegateTool sut = new(
            serviceProvider,
            new BuiltInAgentProfileResolver(new FixedWorkspaceRootProvider(workspace.Path)),
            new HeuristicTokenEstimator());

        using JsonDocument schema = JsonDocument.Parse(sut.Schema);
        string[] agentNames = schema.RootElement
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
        agentNames.Should().Contain("code-reviewer");
        schema.RootElement
            .GetProperty("properties")
            .GetProperty("agent")
            .GetProperty("description")
            .GetString()
            .Should()
            .Contain("code-reviewer - Reviews code changes for regressions.");
    }

    [Fact]
    public async Task ExecuteAsync_Should_RunRequestedSubagentAndReturnHandoff()
    {
        ReplSessionContext parentSession = CreateSession(BuiltInAgentProfiles.Build);
        ReplSessionContext? childSession = null;
        string? delegatedInput = null;

        Mock<IConversationPipeline> conversationPipeline = new(MockBehavior.Strict);
        conversationPipeline
            .Setup(pipeline => pipeline.ProcessAsync(
                It.IsAny<string>(),
                It.IsAny<ReplSessionContext>(),
                It.IsAny<IConversationProgressSink>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, ReplSessionContext, IConversationProgressSink, CancellationToken>((input, session, _, _) =>
            {
                delegatedInput = input;
                childSession = session;

                ToolExecutionBatchResult toolResult = new([
                    new ToolInvocationResult(
                        "call_1",
                        AgentToolNames.FileRead,
                        ToolResultFactory.Success(
                            "Read file.",
                            new ToolErrorPayload("ok", "ok"),
                            ToolJsonContext.Default.ToolErrorPayload))
                ]);

                return Task.FromResult(ConversationTurnResult.AssistantMessage(
                    "Parser lives in ReplCommandParser.cs.",
                    toolResult,
                    new ConversationTurnMetrics(TimeSpan.FromSeconds(2), 7)));
            });

        using ServiceProvider serviceProvider = new ServiceCollection()
            .AddSingleton(conversationPipeline.Object)
            .AddSingleton(Mock.Of<IWorkspaceFileService>())
            .BuildServiceProvider();
        AgentDelegateTool sut = new(
            serviceProvider,
            new BuiltInAgentProfileResolver(),
            new HeuristicTokenEstimator());

        ToolResult result = await sut.ExecuteAsync(
            CreateContext(
                parentSession,
                """{ "agent": "explore", "task": "Find the parser", "context": "Focus on REPL commands." }"""),
            CancellationToken.None);

        result.Status.Should().Be(ToolResultStatus.Success);
        AgentDelegationResult payload = JsonSerializer.Deserialize(
            result.JsonResult,
            ToolJsonContext.Default.AgentDelegationResult)!;
        payload.AgentName.Should().Be(BuiltInAgentProfiles.ExploreName);
        payload.Task.Should().Be("Find the parser");
        payload.Response.Should().Be("Parser lives in ReplCommandParser.cs.");
        payload.ExecutedTools.Should().Equal(AgentToolNames.FileRead);
        payload.EstimatedOutputTokens.Should().Be(7);
        payload.RecordedFileEdits.Should().BeFalse();

        childSession.Should().NotBeNull();
        childSession!.AgentProfile.Name.Should().Be(BuiltInAgentProfiles.ExploreName);
        delegatedInput.Should().Contain("Delegated task from parent agent 'build'");
        delegatedInput.Should().Contain("Find the parser");
        delegatedInput.Should().Contain("Focus on REPL commands.");
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
        AgentDelegateTool sut = new(
            serviceProvider,
            new BuiltInAgentProfileResolver(),
            new HeuristicTokenEstimator());

        ToolResult result = await sut.ExecuteAsync(
            CreateContext(
                parentSession,
                """{ "agent": "general", "task": "Implement a fix" }"""),
            CancellationToken.None);

        result.Status.Should().Be(ToolResultStatus.PermissionDenied);
        result.JsonResult.Should().Contain("readonly_profile_cannot_delegate_edits");
        conversationPipeline.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_Should_RecordChildFileEditsOnParentUndoStack()
    {
        ReplSessionContext parentSession = CreateSession(BuiltInAgentProfiles.Build);

        Mock<IConversationPipeline> conversationPipeline = new(MockBehavior.Strict);
        conversationPipeline
            .Setup(pipeline => pipeline.ProcessAsync(
                It.IsAny<string>(),
                It.IsAny<ReplSessionContext>(),
                It.IsAny<IConversationProgressSink>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, ReplSessionContext, IConversationProgressSink, CancellationToken>((_, childSession, _, _) =>
            {
                childSession.RecordFileEditTransaction(new WorkspaceFileEditTransaction(
                    "child edit",
                    [new WorkspaceFileEditState("src/app.cs", true, "before")],
                    [new WorkspaceFileEditState("src/app.cs", true, "after")]));

                return Task.FromResult(ConversationTurnResult.AssistantMessage("Implemented the focused fix."));
            });

        using ServiceProvider serviceProvider = new ServiceCollection()
            .AddSingleton(conversationPipeline.Object)
            .AddSingleton(Mock.Of<IWorkspaceFileService>())
            .BuildServiceProvider();
        AgentDelegateTool sut = new(
            serviceProvider,
            new BuiltInAgentProfileResolver(),
            new HeuristicTokenEstimator());

        ToolResult result = await sut.ExecuteAsync(
            CreateContext(
                parentSession,
                """{ "agent": "general", "task": "Implement a focused fix" }"""),
            CancellationToken.None);

        result.Status.Should().Be(ToolResultStatus.Success);
        parentSession.TryGetPendingUndoFileEdit(out WorkspaceFileEditTransaction? transaction)
            .Should()
            .BeTrue();
        transaction.Should().NotBeNull();
        transaction!.Description.Should().Be("subagent general: Implement a focused fix");
        transaction.BeforeStates.Should().ContainSingle().Which.Content.Should().Be("before");
        transaction.AfterStates.Should().ContainSingle().Which.Content.Should().Be("after");

        AgentDelegationResult payload = JsonSerializer.Deserialize(
            result.JsonResult,
            ToolJsonContext.Default.AgentDelegationResult)!;
        payload.RecordedFileEdits.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_Should_ReturnInvalidArguments_When_ProfileIsNotSubagent()
    {
        ReplSessionContext parentSession = CreateSession(BuiltInAgentProfiles.Build);
        Mock<IConversationPipeline> conversationPipeline = new(MockBehavior.Strict);

        using ServiceProvider serviceProvider = new ServiceCollection()
            .AddSingleton(conversationPipeline.Object)
            .AddSingleton(Mock.Of<IWorkspaceFileService>())
            .BuildServiceProvider();
        AgentDelegateTool sut = new(
            serviceProvider,
            new BuiltInAgentProfileResolver(),
            new HeuristicTokenEstimator());

        ToolResult result = await sut.ExecuteAsync(
            CreateContext(
                parentSession,
                """{ "agent": "build", "task": "Do work" }"""),
            CancellationToken.None);

        result.Status.Should().Be(ToolResultStatus.InvalidArguments);
        result.JsonResult.Should().Contain("profile_is_not_subagent");
        conversationPipeline.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_Should_RunSubagentInsideWorktree_When_IsolationIsWorktree()
    {
        using TempWorkspace parentWorkspace = TempWorkspace.Create();
        using TempWorkspace worktreeWorkspace = TempWorkspace.Create();
        ReplSessionContext parentSession = CreateSession(BuiltInAgentProfiles.Build);
        IWorkspaceRootProvider rootProvider = new StemCode.Infrastructure.Tools.CurrentDirectoryWorkspaceRootProvider();
        GitWorktreeHandle handle = new(
            "agent-test",
            parentWorkspace.Path,
            worktreeWorkspace.Path,
            worktreeWorkspace.Path,
            "stemcode/worktree/agent-test",
            "abc123");

        Mock<IGitWorktreeService> worktreeService = new(MockBehavior.Strict);
        worktreeService
            .Setup(service => service.CreateAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle);
        worktreeService
            .Setup(service => service.FinishAsync(handle, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitWorktreeOutcome(handle, Kept: true, ["src/app.cs"], 0));

        string? rootSeenByChild = null;
        string? childWorkspacePath = null;
        string? delegatedInput = null;
        Mock<IConversationPipeline> conversationPipeline = new(MockBehavior.Strict);
        conversationPipeline
            .Setup(pipeline => pipeline.ProcessAsync(
                It.IsAny<string>(),
                It.IsAny<ReplSessionContext>(),
                It.IsAny<IConversationProgressSink>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, ReplSessionContext, IConversationProgressSink, CancellationToken>((input, childSession, _, _) =>
            {
                delegatedInput = input;
                rootSeenByChild = rootProvider.GetWorkspaceRoot();
                childWorkspacePath = childSession.WorkspacePath;
                childSession.RecordFileEditTransaction(new WorkspaceFileEditTransaction(
                    "child edit",
                    [new WorkspaceFileEditState("src/app.cs", true, "before")],
                    [new WorkspaceFileEditState("src/app.cs", true, "after")]));
                return Task.FromResult(ConversationTurnResult.AssistantMessage("Changed src/app.cs."));
            });

        using ServiceProvider serviceProvider = new ServiceCollection()
            .AddSingleton(conversationPipeline.Object)
            .AddSingleton(Mock.Of<IWorkspaceFileService>())
            .AddSingleton(worktreeService.Object)
            .AddSingleton(rootProvider)
            .BuildServiceProvider();
        AgentDelegateTool sut = new(
            serviceProvider,
            new BuiltInAgentProfileResolver(),
            new HeuristicTokenEstimator());

        ToolResult result = await sut.ExecuteAsync(
            CreateContext(
                parentSession,
                """{ "agent": "general", "task": "Implement a focused fix", "isolation": "worktree" }"""),
            CancellationToken.None);

        result.Status.Should().Be(ToolResultStatus.Success);
        rootSeenByChild.Should().Be(Path.GetFullPath(worktreeWorkspace.Path));
        childWorkspacePath.Should().Be(Path.GetFullPath(worktreeWorkspace.Path));
        rootProvider.GetWorkspaceRoot().Should().NotBe(Path.GetFullPath(worktreeWorkspace.Path));
        delegatedInput.Should().Contain("isolated git worktree on branch 'stemcode/worktree/agent-test'");
        parentSession.TryGetPendingUndoFileEdit(out _).Should().BeFalse();

        AgentDelegationResult payload = JsonSerializer.Deserialize(
            result.JsonResult,
            ToolJsonContext.Default.AgentDelegationResult)!;
        payload.RecordedFileEdits.Should().BeFalse();
        payload.Worktree.Should().NotBeNull();
        payload.Worktree!.Kept.Should().BeTrue();
        payload.Worktree.Branch.Should().Be("stemcode/worktree/agent-test");
        payload.Worktree.ChangedFiles.Should().Equal("src/app.cs");
        result.RenderPayload.Text.Should().Contain("git merge stemcode/worktree/agent-test");
        worktreeService.Verify(service => service.FinishAsync(handle, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_Should_ReturnExecutionError_When_WorktreeCannotBeCreated()
    {
        ReplSessionContext parentSession = CreateSession(BuiltInAgentProfiles.Build);
        Mock<IGitWorktreeService> worktreeService = new(MockBehavior.Strict);
        worktreeService
            .Setup(service => service.CreateAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Worktree isolation needs a git repository."));
        Mock<IConversationPipeline> conversationPipeline = new(MockBehavior.Strict);

        using ServiceProvider serviceProvider = new ServiceCollection()
            .AddSingleton(conversationPipeline.Object)
            .AddSingleton(Mock.Of<IWorkspaceFileService>())
            .AddSingleton(worktreeService.Object)
            .BuildServiceProvider();
        AgentDelegateTool sut = new(
            serviceProvider,
            new BuiltInAgentProfileResolver(),
            new HeuristicTokenEstimator());

        ToolResult result = await sut.ExecuteAsync(
            CreateContext(
                parentSession,
                """{ "agent": "general", "task": "Implement a focused fix", "isolation": "worktree" }"""),
            CancellationToken.None);

        result.Status.Should().Be(ToolResultStatus.ExecutionError);
        result.JsonResult.Should().Contain("worktree_unavailable");
        conversationPipeline.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_Should_RejectUnknownIsolation()
    {
        ReplSessionContext parentSession = CreateSession(BuiltInAgentProfiles.Build);
        Mock<IConversationPipeline> conversationPipeline = new(MockBehavior.Strict);
        using ServiceProvider serviceProvider = new ServiceCollection()
            .AddSingleton(conversationPipeline.Object)
            .BuildServiceProvider();
        AgentDelegateTool sut = new(
            serviceProvider,
            new BuiltInAgentProfileResolver(),
            new HeuristicTokenEstimator());

        ToolResult result = await sut.ExecuteAsync(
            CreateContext(
                parentSession,
                """{ "agent": "general", "task": "Do work", "isolation": "container" }"""),
            CancellationToken.None);

        result.Status.Should().Be(ToolResultStatus.InvalidArguments);
        result.JsonResult.Should().Contain("invalid_isolation");
    }

    private static ToolExecutionContext CreateContext(
        ReplSessionContext session,
        string argumentsJson)
    {
        using JsonDocument document = JsonDocument.Parse(argumentsJson);
        return new ToolExecutionContext(
            "call_agent",
            AgentToolNames.AgentDelegate,
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
                "stemcode-agent-delegate-tests-" + Guid.NewGuid().ToString("N"));
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
