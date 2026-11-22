using FluentAssertions;
using Moq;
using StemCode.Application.Abstractions;
using StemCode.Application.Models;
using StemCode.Application.Profiles;
using StemCode.Application.Services;
using StemCode.Application.Tools.Models;
using StemCode.Domain.Models;

namespace StemCode.Tests.Application.Services;

public sealed class AgentTurnServiceTests
{
    [Fact]
    public async Task RunTurnAsync_Should_RunNormalPromptThroughConversationPipeline()
    {
        ReplSessionContext session = CreateSession();
        RecordingProgressSink progressSink = new();

        Mock<IConversationPipeline> conversationPipeline = new(MockBehavior.Strict);
        conversationPipeline
            .Setup(pipeline => pipeline.ProcessAsync(
                "inspect this",
                session,
                progressSink,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConversationTurnResult.AssistantMessage("Done."));

        AgentTurnService sut = new(
            conversationPipeline.Object,
            new BuiltInAgentProfileResolver());

        ConversationTurnResult result = await sut.RunTurnAsync(
            new AgentTurnRequest(session, "inspect this", progressSink),
            CancellationToken.None);

        result.ResponseText.Should().Be("Done.");
        session.AgentProfile.Name.Should().Be(BuiltInAgentProfiles.BuildName);
        conversationPipeline.VerifyAll();
    }

    [Fact]
    public async Task RunTurnAsync_Should_ExpandMcpPromptCommandBeforeRunningTurn()
    {
        ReplSessionContext session = CreateSession();
        RecordingProgressSink progressSink = new();
        Mock<IMcpPromptService> mcpPrompts = new(MockBehavior.Strict);
        mcpPrompts
            .Setup(service => service.TryExpandAsync("/mcp__github__review 12", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpPromptExpansion("Review pull request 12 carefully.", null));

        Mock<IConversationPipeline> conversationPipeline = new(MockBehavior.Strict);
        conversationPipeline
            .Setup(pipeline => pipeline.ProcessAsync(
                "Review pull request 12 carefully.",
                session,
                progressSink,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConversationTurnResult.AssistantMessage("Reviewed."));

        AgentTurnService sut = new(
            conversationPipeline.Object,
            new BuiltInAgentProfileResolver(),
            mcpPromptService: mcpPrompts.Object);

        ConversationTurnResult result = await sut.RunTurnAsync(
            new AgentTurnRequest(session, "/mcp__github__review 12", progressSink),
            CancellationToken.None);

        result.ResponseText.Should().Be("Reviewed.");
        conversationPipeline.VerifyAll();
    }

    [Fact]
    public async Task RunTurnAsync_Should_ReturnMcpPromptError_WithoutCallingModel()
    {
        ReplSessionContext session = CreateSession();
        Mock<IMcpPromptService> mcpPrompts = new(MockBehavior.Strict);
        mcpPrompts
            .Setup(service => service.TryExpandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpPromptExpansion(null, "Missing required argument(s) pr."));
        Mock<IConversationPipeline> conversationPipeline = new(MockBehavior.Strict);

        AgentTurnService sut = new(
            conversationPipeline.Object,
            new BuiltInAgentProfileResolver(),
            mcpPromptService: mcpPrompts.Object);

        ConversationTurnResult result = await sut.RunTurnAsync(
            new AgentTurnRequest(session, "/mcp__github__review", new RecordingProgressSink()),
            CancellationToken.None);

        result.ResponseText.Should().Be("Missing required argument(s) pr.");
        conversationPipeline.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RunTurnAsync_Should_ForwardAttachmentsToConversationPipeline()
    {
        ReplSessionContext session = CreateSession();
        RecordingProgressSink progressSink = new();
        ConversationAttachment[] attachments =
        [
            new ConversationAttachment("notes.txt", "text/plain", "bm90ZXM=", "notes")
        ];

        Mock<IConversationPipeline> conversationPipeline = new(MockBehavior.Strict);
        conversationPipeline
            .Setup(pipeline => pipeline.ProcessAsync(
                "inspect this",
                session,
                progressSink,
                It.Is<IReadOnlyList<ConversationAttachment>>(items =>
                    items.Count == 1 &&
                    items[0].Name == "notes.txt"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConversationTurnResult.AssistantMessage("Done."));

        AgentTurnService sut = new(
            conversationPipeline.Object,
            new BuiltInAgentProfileResolver());

        ConversationTurnResult result = await sut.RunTurnAsync(
            new AgentTurnRequest(session, "inspect this", progressSink, attachments),
            CancellationToken.None);

        result.ResponseText.Should().Be("Done.");
        conversationPipeline.VerifyAll();
    }

    [Fact]
    public async Task RunTurnAsync_Should_RunDirectShellCommand_When_InputStartsWithBang()
    {
        ReplSessionContext session = CreateSession();
        RecordingProgressSink progressSink = new();
        RecordingShellCommandService shellCommandService = new(
            new ShellCommandExecutionResult(
                "dotnet --info",
                ".",
                0,
                "SDK info",
                string.Empty));
        Mock<IConversationPipeline> conversationPipeline = new(MockBehavior.Strict);
        AgentTurnService sut = new(
            conversationPipeline.Object,
            new BuiltInAgentProfileResolver(),
            shellCommandService);

        ConversationTurnResult result = await sut.RunTurnAsync(
            new AgentTurnRequest(session, "! dotnet --info", progressSink),
            CancellationToken.None);

        result.Kind.Should().Be(ConversationTurnResultKind.ToolExecution);
        result.ResponseText.Should().Contain("Shell command: dotnet --info");
        result.ResponseText.Should().Contain("SDK info");
        shellCommandService.Requests.Should().ContainSingle();
        shellCommandService.Requests[0].Command.Should().Be("dotnet --info");
        shellCommandService.Requests[0].WorkingDirectory.Should().Be(".");
        shellCommandService.Requests[0].SandboxPermissions.Should().Be(ShellCommandSandboxPermissions.RequireEscalated);
        shellCommandService.Requests[0].Justification.Should().Be("User-entered direct shell command.");
        conversationPipeline.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RunTurnAsync_Should_ExpandCustomSlashCommand()
    {
        string workspaceRoot = Path.Combine(
            Path.GetTempPath(),
            "stemcode-custom-turn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(workspaceRoot, ".stemcode", "commands"));
        await File.WriteAllTextAsync(
            Path.Combine(workspaceRoot, ".stemcode", "commands", "security-review.md"),
            """
            ---
            name: security-review
            args: ["scope"]
            ---

            Review $scope.
            Full scope: $ARGUMENTS.
            """);

        try
        {
            ReplSessionContext session = CreateSession(workspaceRoot);
            RecordingProgressSink progressSink = new();
            Mock<IConversationPipeline> conversationPipeline = new(MockBehavior.Strict);
            conversationPipeline
                .Setup(pipeline => pipeline.ProcessAsync(
                    "Review latest.\nFull scope: latest diff.",
                    session,
                    progressSink,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(ConversationTurnResult.AssistantMessage("Done."));

            AgentTurnService sut = new(
                conversationPipeline.Object,
                new BuiltInAgentProfileResolver());

            ConversationTurnResult result = await sut.RunTurnAsync(
                new AgentTurnRequest(session, "/security-review latest diff", progressSink),
                CancellationToken.None);

            result.ResponseText.Should().Be("Done.");
            conversationPipeline.VerifyAll();
        }
        finally
        {
            if (Directory.Exists(workspaceRoot))
            {
                Directory.Delete(workspaceRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunTurnAsync_Should_ReturnHelpfulMessage_When_DirectShellCommandIsEmpty()
    {
        ReplSessionContext session = CreateSession();
        Mock<IConversationPipeline> conversationPipeline = new(MockBehavior.Strict);
        RecordingShellCommandService shellCommandService = new(
            new ShellCommandExecutionResult(
                string.Empty,
                ".",
                0,
                string.Empty,
                string.Empty));
        AgentTurnService sut = new(
            conversationPipeline.Object,
            new BuiltInAgentProfileResolver(),
            shellCommandService);

        ConversationTurnResult result = await sut.RunTurnAsync(
            new AgentTurnRequest(session, "!", new RecordingProgressSink()),
            CancellationToken.None);

        result.ResponseText.Should().Be("Enter a shell command after !.");
        shellCommandService.Requests.Should().BeEmpty();
        conversationPipeline.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RunTurnAsync_Should_UpdateWorkingDirectory_When_DirectShellCommandIsSuccessfulCd()
    {
        string workspaceRoot = Path.Combine(
            Path.GetTempPath(),
            "stemcode-direct-shell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(workspaceRoot, "src"));
        try
        {
            ReplSessionContext session = CreateSession(workspaceRoot);
            RecordingShellCommandService shellCommandService = new(
                new ShellCommandExecutionResult(
                    "cd src",
                    ".",
                    0,
                    string.Empty,
                    string.Empty));
            Mock<IConversationPipeline> conversationPipeline = new(MockBehavior.Strict);
            AgentTurnService sut = new(
                conversationPipeline.Object,
                new BuiltInAgentProfileResolver(),
                shellCommandService);

            ConversationTurnResult result = await sut.RunTurnAsync(
                new AgentTurnRequest(session, "!cd src", new RecordingProgressSink()),
                CancellationToken.None);

            session.WorkingDirectory.Should().Be("src");
            result.ResponseText.Should().Contain("Session working directory is now 'src'.");
            conversationPipeline.VerifyNoOtherCalls();
        }
        finally
        {
            if (Directory.Exists(workspaceRoot))
            {
                Directory.Delete(workspaceRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunTurnAsync_Should_InvokeMentionedSubagentForOneTurnAndRestoreProfile()
    {
        ReplSessionContext session = CreateSession();
        RecordingProgressSink progressSink = new();

        Mock<IConversationPipeline> conversationPipeline = new(MockBehavior.Strict);
        conversationPipeline
            .Setup(pipeline => pipeline.ProcessAsync(
                "find the parser",
                session,
                progressSink,
                It.IsAny<CancellationToken>()))
            .Returns<string, ReplSessionContext, IConversationProgressSink, CancellationToken>((_, activeSession, _, _) =>
            {
                activeSession.AgentProfile.Name.Should().Be(BuiltInAgentProfiles.ExploreName);
                return Task.FromResult(ConversationTurnResult.AssistantMessage("Parser is in ReplCommandParser.cs."));
            });

        AgentTurnService sut = new(
            conversationPipeline.Object,
            new BuiltInAgentProfileResolver());

        ConversationTurnResult result = await sut.RunTurnAsync(
            new AgentTurnRequest(session, "@explore find the parser", progressSink),
            CancellationToken.None);

        result.ResponseText.Should().Be("Parser is in ReplCommandParser.cs.");
        session.AgentProfile.Name.Should().Be(BuiltInAgentProfiles.BuildName);
        conversationPipeline.VerifyAll();
    }

    [Fact]
    public async Task RunTurnAsync_Should_ReturnHelpfulMessage_When_MentionedAgentIsPrimary()
    {
        ReplSessionContext session = CreateSession();
        Mock<IConversationPipeline> conversationPipeline = new(MockBehavior.Strict);

        AgentTurnService sut = new(
            conversationPipeline.Object,
            new BuiltInAgentProfileResolver());

        ConversationTurnResult result = await sut.RunTurnAsync(
            new AgentTurnRequest(session, "@plan inspect this", new RecordingProgressSink()),
            CancellationToken.None);

        result.ResponseText.Should().Contain("primary profile");
        result.ResponseText.Should().Contain("/profile plan");
        conversationPipeline.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RunTurnAsync_Should_ReturnHelpfulMessage_When_SubagentTaskIsMissing()
    {
        ReplSessionContext session = CreateSession();
        Mock<IConversationPipeline> conversationPipeline = new(MockBehavior.Strict);

        AgentTurnService sut = new(
            conversationPipeline.Object,
            new BuiltInAgentProfileResolver());

        ConversationTurnResult result = await sut.RunTurnAsync(
            new AgentTurnRequest(session, "@general", new RecordingProgressSink()),
            CancellationToken.None);

        result.ResponseText.Should().Contain("Tell '@general' what to do");
        conversationPipeline.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RunTurnAsync_Should_RunDirectShellBackgroundCommandAndShowOutput_When_InputStartsWithDoubleBang()
    {
        ReplSessionContext session = CreateSession();
        RecordingProgressSink progressSink = new();
        RecordingShellCommandService shellCommandService = new(
            new ShellCommandExecutionResult(
                "dotnet build",
                ".",
                0,
                "Build succeeded.",
                string.Empty));
        Mock<IConversationPipeline> conversationPipeline = new(MockBehavior.Strict);
        AgentTurnService sut = new(
            conversationPipeline.Object,
            new BuiltInAgentProfileResolver(),
            shellCommandService);

        ConversationTurnResult result = await sut.RunTurnAsync(
            new AgentTurnRequest(session, "!! dotnet build", progressSink),
            CancellationToken.None);

        result.Kind.Should().Be(ConversationTurnResultKind.ToolExecution);
        result.ResponseText.Should().Contain("Shell command (background): dotnet build");
        result.ResponseText.Should().Contain("Build succeeded.");
        result.ResponseText.Should().Contain("exit code 0");
        shellCommandService.BackgroundRequests.Should().ContainSingle();
        shellCommandService.BackgroundRequests[0].Command.Should().Be("dotnet build");
        conversationPipeline.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RunTurnAsync_Should_StreamBackgroundOutputChunks_When_InputStartsWithDoubleBang()
    {
        ReplSessionContext session = CreateSession();
        RecordingProgressSink progressSink = new();
        RecordingShellCommandService shellCommandService = new(
            new ShellCommandExecutionResult(
                "dotnet build",
                ".",
                0,
                "Build succeeded.",
                string.Empty));
        Mock<IConversationPipeline> conversationPipeline = new(MockBehavior.Strict);
        AgentTurnService sut = new(
            conversationPipeline.Object,
            new BuiltInAgentProfileResolver(),
            shellCommandService);

        await sut.RunTurnAsync(
            new AgentTurnRequest(session, "!! dotnet build", progressSink),
            CancellationToken.None);

        progressSink.Chunks.Should().Contain(chunk =>
            chunk.Contains("Build succeeded.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunTurnAsync_Should_DetachWithoutStoppingTerminal_When_BackgroundStreamCancelled()
    {
        ReplSessionContext session = CreateSession();
        RecordingProgressSink progressSink = new();
        using CancellationTokenSource cancellation = new();
        CancellingBackgroundShellCommandService shellCommandService = new(cancellation);
        Mock<IConversationPipeline> conversationPipeline = new(MockBehavior.Strict);
        AgentTurnService sut = new(
            conversationPipeline.Object,
            new BuiltInAgentProfileResolver(),
            shellCommandService);

        ConversationTurnResult result = await sut.RunTurnAsync(
            new AgentTurnRequest(session, "!! sleep 100", progressSink),
            cancellation.Token);

        result.Kind.Should().Be(ConversationTurnResultKind.AssistantMessage);
        result.ResponseText.Should().Contain("Detached");
        result.ResponseText.Should().Contain("terminal-1");
        shellCommandService.StopCalled.Should().BeFalse();
    }

    private static ReplSessionContext CreateSession(string? workspacePath = null)
    {
        return new ReplSessionContext(
            new AgentProviderProfile(ProviderKind.OpenAiCompatible, "https://provider.example.com/v1"),
            "gpt-5-mini",
            ["gpt-5-mini"],
            workspacePath: workspacePath);
    }

    private sealed class RecordingShellCommandService : IShellCommandService
    {
        private readonly ShellCommandExecutionResult _result;
        private readonly ShellCommandExecutionResult _backgroundResult;
        private int _backgroundReadCount;

        public RecordingShellCommandService(ShellCommandExecutionResult result)
        {
            _result = result;
            _backgroundResult = result with
            {
                Background = true,
                TerminalId = "terminal-1",
                TerminalStatus = "exited",
                TerminalAction = "read",
            };
        }

        public bool IsPseudoTerminalSupported => false;

        public List<ShellCommandExecutionRequest> Requests { get; } = [];

        public List<ShellCommandExecutionRequest> BackgroundRequests { get; } = [];

        public Task<ShellCommandExecutionResult> ExecuteAsync(
            ShellCommandExecutionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(_result);
        }

        public Task<ShellCommandExecutionResult> StartBackgroundAsync(
            ShellCommandExecutionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BackgroundRequests.Add(request);
            return Task.FromResult(new ShellCommandExecutionResult(
                request.Command,
                request.WorkingDirectory ?? ".",
                0,
                string.Empty,
                string.Empty,
                Background: true,
                TerminalId: "terminal-1",
                TerminalStatus: "running",
                TerminalAction: "start"));
        }

        public Task<ShellCommandExecutionResult> ReadBackgroundAsync(
            string terminalId,
            string? sessionId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _backgroundReadCount);
            return Task.FromResult(_backgroundResult);
        }

        public Task<ShellCommandExecutionResult> StopBackgroundAsync(
            string terminalId,
            string? sessionId,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<BackgroundTerminalInfo>> ListBackgroundAsync(
            string? sessionId,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    // Returns a still-running terminal on the first read, then cancels the token so
    // the poll loop is interrupted - simulating the user pressing Esc to detach.
    private sealed class CancellingBackgroundShellCommandService : IShellCommandService
    {
        private readonly CancellationTokenSource _cancellation;

        public CancellingBackgroundShellCommandService(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        public bool IsPseudoTerminalSupported => false;

        public bool StopCalled { get; private set; }

        public Task<ShellCommandExecutionResult> ExecuteAsync(
            ShellCommandExecutionRequest request,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<ShellCommandExecutionResult> StartBackgroundAsync(
            ShellCommandExecutionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ShellCommandExecutionResult(
                request.Command,
                request.WorkingDirectory ?? ".",
                0,
                string.Empty,
                string.Empty,
                Background: true,
                TerminalId: "terminal-1",
                TerminalStatus: "running",
                TerminalAction: "start"));
        }

        public Task<ShellCommandExecutionResult> ReadBackgroundAsync(
            string terminalId,
            string? sessionId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Trigger cancellation so the next poll-loop delay observes it.
            _cancellation.Cancel();
            return Task.FromResult(new ShellCommandExecutionResult(
                terminalId,
                ".",
                0,
                "partial output",
                string.Empty,
                Background: true,
                TerminalId: terminalId,
                TerminalStatus: "running",
                TerminalAction: "read"));
        }

        public Task<ShellCommandExecutionResult> StopBackgroundAsync(
            string terminalId,
            string? sessionId,
            CancellationToken cancellationToken)
        {
            StopCalled = true;
            return Task.FromResult(new ShellCommandExecutionResult(
                terminalId,
                ".",
                0,
                string.Empty,
                string.Empty,
                Background: true,
                TerminalId: terminalId,
                TerminalStatus: "stopped",
                TerminalAction: "stop"));
        }

        public Task<IReadOnlyList<BackgroundTerminalInfo>> ListBackgroundAsync(
            string? sessionId,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class RecordingProgressSink : IConversationProgressSink
    {
        public List<string> Chunks { get; } = [];

        public Task ReportAssistantMessageChunkAsync(
            string text,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Chunks.Add(text);
            return Task.CompletedTask;
        }

        public Task ReportAssistantReasoningAsync(
            string reasoningText,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task ReportExecutionPlanAsync(
            ExecutionPlanProgress executionPlanProgress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task ReportToolCallsStartedAsync(
            IReadOnlyList<ConversationToolCall> toolCalls,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task ReportToolResultsAsync(
            ToolExecutionBatchResult toolExecutionResult,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
