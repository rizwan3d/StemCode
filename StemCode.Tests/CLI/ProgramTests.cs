using FluentAssertions;
using StemCode.Application.Backend;
using StemCode.Application.Models;
using StemCode.CLI;
using Moq;
using StemCode.Infrastructure.WindowsSandbox;
using Spectre.Console;
using System.Diagnostics;
using System.Reflection;
using System.Collections;

namespace StemCode.Tests.CLI;

public sealed class ProgramTests
{
    [Fact]
    public void TryHandleWindowsSandboxSpecialInvocation_Should_ReturnFalse_ForRegularCliArgs()
    {
        bool handled = Program.TryHandleWindowsSandboxSpecialInvocation(
            ["--interactive"],
            out int exitCode);

        handled.Should().BeFalse();
        exitCode.Should().Be(0);
    }

    [Fact]
    public void TryHandleWindowsSandboxSpecialInvocation_Should_ReturnUsageError_WhenSetupPayloadIsMissing()
    {
        bool handled = Program.TryHandleWindowsSandboxSpecialInvocation(
            [WindowsSandboxSetupOrchestrator.SetupCommandArgument],
            out int exitCode);

        handled.Should().BeTrue();
        exitCode.Should().Be(2);
    }

    [Fact]
    public void RenderSessionView_Should_ClearPinnedPlanState()
    {
        AppState state = new(
            new UiBridge(),
            new Mock<IStemCodeBackend>(MockBehavior.Strict).Object)
        {
            IsPlanPinned = true,
            LatestPlanProgress = new ExecutionPlanProgress(["Inspect context"], 1),
            LatestPlanText = "Plan progress: 1/3"
        };

        BackendSessionInfo sessionInfo = new(
            SessionId: "session-id",
            SectionResumeCommand: "/resume session-id",
            ProviderName: "provider",
            ModelId: "model",
            ActiveModelContextWindowTokens: 1234,
            AvailableModelIds: [],
            ThinkingMode: "default",
            ReasoningEffort: null,
            ShowThinking: false,
            AgentProfileName: "agent",
            SectionTitle: "title",
            IsResumedSection: false,
            ConversationHistory: []);

        MethodInfo renderSessionView = typeof(Program).GetMethod(
            "RenderSessionView",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        renderSessionView.Invoke(null, [state, sessionInfo, null]);

        state.IsPlanPinned.Should().BeFalse();
        state.LatestPlanProgress.Should().BeNull();
        state.LatestPlanText.Should().BeNull();
    }

    [Fact]
    public void StartConversation_Should_ClearCompletedPlanState()
    {
        AppState state = new(new UiBridge(), CreateConversationBackend().Object)
        {
            IsReady = true,
            IsPlanPinned = true,
            LatestPlanProgress = new ExecutionPlanProgress(["Inspect", "Patch"], 2),
            LatestPlanText = "Plan progress: 2/2"
        };

        MethodInfo startConversation = typeof(Program).GetMethod(
            "StartConversation",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        startConversation.Invoke(null, [state, "Ship it", null]);

        state.IsPlanPinned.Should().BeFalse();
        state.LatestPlanProgress.Should().BeNull();
        state.LatestPlanText.Should().BeNull();
        state.IsBusy.Should().BeTrue();
        state.ActiveOperation.Should().NotBeNull();
    }

    [Fact]
    public void StartConversation_Should_KeepIncompletePlanState()
    {
        AppState state = new(new UiBridge(), CreateConversationBackend().Object)
        {
            IsReady = true,
            IsPlanPinned = true,
            LatestPlanProgress = new ExecutionPlanProgress(["Inspect", "Patch"], 1),
            LatestPlanText = "Plan progress: 1/2"
        };

        MethodInfo startConversation = typeof(Program).GetMethod(
            "StartConversation",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        startConversation.Invoke(null, [state, "Continue", null]);

        state.IsPlanPinned.Should().BeTrue();
        state.LatestPlanProgress.Should().NotBeNull();
        state.LatestPlanText.Should().Be("Plan progress: 1/2");
        state.IsBusy.Should().BeTrue();
        state.ActiveOperation.Should().NotBeNull();
    }

    [Fact]
    public void BuildUi_Should_FallBackToCompactPanel_When_TerminalIsTooSmall()
    {
        AppState state = new(
            new UiBridge(),
            new Mock<IStemCodeBackend>(MockBehavior.Strict).Object);

        MethodInfo buildUi = typeof(Program).GetMethod(
            "BuildUi",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            [typeof(AppState), typeof(int), typeof(int)],
            modifiers: null)!;

        object? renderable = buildUi.Invoke(null, [state, 18, 9]);

        renderable.Should().BeOfType<Panel>();
    }

    [Fact]
    public void BuildUi_Should_FallBackToCompactPanel_When_PinnedPlanCannotFit()
    {
        AppState state = new(
            new UiBridge(),
            new Mock<IStemCodeBackend>(MockBehavior.Strict).Object)
        {
            IsPlanPinned = true,
            LatestPlanText = "Plan progress: 1/3"
        };

        MethodInfo buildUi = typeof(Program).GetMethod(
            "BuildUi",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            [typeof(AppState), typeof(int), typeof(int)],
            modifiers: null)!;

        object? renderable = buildUi.Invoke(null, [state, 80, 12]);

        renderable.Should().BeOfType<Panel>();
    }

    [Fact]
    public void SubmitInput_Should_QueuePrompt_When_Busy()
    {
        AppState state = new(new UiBridge(), CreateConversationBackend().Object)
        {
            IsReady = true,
            IsBusy = true,
            InputCursorIndex = "Queued prompt".Length
        };
        state.Input.Append("Queued prompt");

        MethodInfo submitInput = typeof(Program).GetMethod(
            "SubmitInput",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        submitInput.Invoke(null, [state]);

        state.PendingSubmissions.Should().ContainSingle();
        state.PendingSubmissions.Peek().Kind.Should().Be(PendingSubmissionKind.Prompt);
        state.PendingSubmissions.Peek().Text.Should().Be("Queued prompt");
        state.Messages.Should().ContainSingle(message =>
            message.Role == Role.System &&
            message.Text.Contains("Queued prompt: Queued prompt"));
        state.Input.ToString().Should().BeEmpty();
    }

    [Fact]
    public void SubmitInput_Should_NotQueueCommand_When_Busy()
    {
        AppState state = new(new UiBridge(), CreateConversationBackend().Object)
        {
            IsReady = true,
            IsBusy = true,
            InputCursorIndex = "/help".Length
        };
        state.Input.Append("/help");

        MethodInfo submitInput = typeof(Program).GetMethod(
            "SubmitInput",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        submitInput.Invoke(null, [state]);

        state.PendingSubmissions.Should().BeEmpty();
        state.Messages.Should().ContainSingle(message =>
            message.Role == Role.System &&
            message.Text.Contains("That command is unavailable while StemCode is working."));
        state.Input.ToString().Should().BeEmpty();
        state.InputCursorIndex.Should().Be(0);
    }

    [Fact]
    public void SubmitInput_Should_PreserveInput_When_BackendIsNotReady()
    {
        AppState state = new(new UiBridge(), CreateConversationBackend().Object)
        {
            InputCursorIndex = "Wait for startup".Length
        };
        state.Input.Append("Wait for startup");

        MethodInfo submitInput = typeof(Program).GetMethod(
            "SubmitInput",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        submitInput.Invoke(null, [state]);

        state.Input.ToString().Should().Be("Wait for startup");
        state.InputCursorIndex.Should().Be("Wait for startup".Length);
        state.Messages.Should().ContainSingle(message =>
            message.Role == Role.System &&
            message.Text == "StemCode is still starting up. Please wait.");
    }

    [Fact]
    public void SubmitInput_Should_ClearSlashCommand_When_BackendIsNotReady()
    {
        AppState state = new(new UiBridge(), CreateConversationBackend().Object)
        {
            InputCursorIndex = "/help".Length
        };
        state.Input.Append("/help");

        MethodInfo submitInput = typeof(Program).GetMethod(
            "SubmitInput",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        submitInput.Invoke(null, [state]);

        state.Input.ToString().Should().BeEmpty();
        state.InputCursorIndex.Should().Be(0);
        state.Messages.Should().ContainSingle(message =>
            message.Role == Role.System &&
            message.Text == "StemCode is still starting up. Please wait.");
    }

    [Fact]
    public async Task StartInitialization_Should_StopInteractiveSession_When_BackendStartupFails()
    {
        Mock<IStemCodeBackend> backend = new(MockBehavior.Strict);
        backend
            .Setup(static value => value.InitializeAsync(
                It.IsAny<StemCode.Application.UI.IUiBridge>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        AppState state = new(new UiBridge(), backend.Object);

        MethodInfo startInitialization = typeof(Program).GetMethod(
            "StartInitialization",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        startInitialization.Invoke(null, [state, false]);
        await state.ActiveOperation!;
        state.UiBridge.ApplyPending(state);

        state.HasFatalError.Should().BeTrue();
        state.Running.Should().BeFalse();
        state.FatalExitMessage.Should().Be("Failed to start StemCode: boom");
        state.Messages.Should().ContainSingle(message =>
            message.Role == Role.System &&
            message.Text == "Failed to start StemCode: boom");
    }

    [Fact]
    public async Task StartInitialization_Should_SkipRenderingResumedSection_WhenNoOldReaderIsEnabled()
    {
        Mock<IStemCodeBackend> backend = new(MockBehavior.Strict);
        backend
            .Setup(static value => value.InitializeAsync(
                It.IsAny<StemCode.Application.UI.IUiBridge>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendSessionInfo(
                SessionId: "session-id",
                SectionResumeCommand: "/resume session-id",
                ProviderName: "provider",
                ModelId: "model",
                ActiveModelContextWindowTokens: 1234,
                AvailableModelIds: [],
                ThinkingMode: "default",
                ReasoningEffort: null,
                ShowThinking: false,
                AgentProfileName: "agent",
                SectionTitle: "title",
                IsResumedSection: true,
                ConversationHistory:
                [
                    new BackendConversationMessage("user", "hello"),
                    new BackendConversationMessage("assistant", "world")
                ]));

        AppState state = new(new UiBridge(), backend.Object);

        MethodInfo startInitialization = typeof(Program).GetMethod(
            "StartInitialization",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        startInitialization.Invoke(null, [state, true]);
        await state.ActiveOperation!;
        state.UiBridge.ApplyPending(state);

        state.IsReady.Should().BeTrue();
        state.SessionId.Should().Be("session-id");
        state.Messages.Should().BeEmpty();
    }

    [Fact]
    public void GetSafePath_Should_RejectSiblingDirectoryPrefixEscape()
    {
        AppState state = new(
            new UiBridge(),
            new Mock<IStemCodeBackend>(MockBehavior.Strict).Object);
        string rootName = new DirectoryInfo(state.RootDirectory).Name;

        MethodInfo getSafePath = typeof(Program).GetMethod(
            "GetSafePath",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        Action act = () => getSafePath.Invoke(
            null,
            [state, $@"..\{rootName}-copy\secret.txt"]);

        TargetInvocationException exception = act.Should().Throw<TargetInvocationException>().Which;
        exception.InnerException.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be("Path escapes workspace.");
    }

    [Fact]
    public void TryHandleTerminalEscapeFollowupKey_Should_RequeueNonAnsiInput()
    {
        AppState state = new(
            new UiBridge(),
            new Mock<IStemCodeBackend>(MockBehavior.Strict).Object);
        MethodInfo tryHandleTerminalEscapeFollowupKey = typeof(Program).GetMethod(
            "TryHandleTerminalEscapeFollowupKey",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        object? handled = tryHandleTerminalEscapeFollowupKey.Invoke(
            null,
            [state, new ConsoleKeyInfo('1', ConsoleKey.D1, false, false, false)]);

        handled.Should().Be(false);
        state.PendingInputKeys.Should().ContainSingle();
        state.PendingInputKeys.Peek().KeyChar.Should().Be('1');
    }

    [Fact]
    public void HandleInputEditingKey_Should_SelectAndReplaceAllInput_WhenCtrlAIsPressed()
    {
        AppState state = new(
            new UiBridge(),
            new Mock<IStemCodeBackend>(MockBehavior.Strict).Object)
        {
            InputCursorIndex = "Replace me".Length
        };
        state.Input.Append("Replace me");

        MethodInfo handleInputEditingKey = typeof(Program).GetMethod(
            "HandleInputEditingKey",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        MethodInfo insertInputText = typeof(Program).GetMethod(
            "InsertInputText",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        object? handled = handleInputEditingKey.Invoke(
            null,
            [state, new ConsoleKeyInfo('\u0001', ConsoleKey.A, false, false, true)]);

        handled.Should().Be(true);
        state.HasInputSelection.Should().BeTrue();
        state.InputSelectionAnchor.Should().Be(0);
        state.InputCursorIndex.Should().Be("Replace me".Length);

        insertInputText.Invoke(null, [state, "Updated", false]);

        state.Input.ToString().Should().Be("Updated");
        state.InputCursorIndex.Should().Be("Updated".Length);
        state.HasInputSelection.Should().BeFalse();
    }

    [Fact]
    public void UpdateConversationViewportAfterContentChange_Should_PreserveViewport_WhenAutoScrollIsPaused()
    {
        AppState state = new(
            new UiBridge(),
            new Mock<IStemCodeBackend>(MockBehavior.Strict).Object)
        {
            ConversationScrollOffset = 4,
            IsConversationAutoScrollPaused = true,
            LastConversationLineCount = 20
        };

        MethodInfo updateConversationViewportAfterContentChange = typeof(Program).GetMethod(
            "UpdateConversationViewportAfterContentChange",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        updateConversationViewportAfterContentChange.Invoke(null, [state, 27]);

        state.ConversationScrollOffset.Should().Be(11);
        state.LastConversationLineCount.Should().Be(27);
    }

    [Fact]
    public void TryCancelActiveTurn_Should_AbandonTurn_OnSecondEscape()
    {
        AppState state = new(new UiBridge(), CreateConversationBackend().Object)
        {
            IsReady = true,
            IsBusy = true,
            ActivityText = "Thinking",
            TurnCancellation = new CancellationTokenSource(),
            ActiveOperation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously).Task
        };
        long operationId = state.BeginTrackedOperation();

        MethodInfo tryCancelActiveTurn = typeof(Program).GetMethod(
            "TryCancelActiveTurn",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        object? firstHandled = tryCancelActiveTurn.Invoke(null, [state]);

        firstHandled.Should().Be(true);
        state.IsBusy.Should().BeTrue();
        state.IsTurnInterruptPending.Should().BeTrue();
        state.ActivityText.Should().Be("Interrupting");
        state.TurnCancellation!.IsCancellationRequested.Should().BeTrue();
        state.Messages.Should().ContainSingle(message =>
            message.Role == Role.System &&
            message.Text.Contains("Interrupt requested."));

        object? secondHandled = tryCancelActiveTurn.Invoke(null, [state]);

        secondHandled.Should().Be(true);
        state.IsBusy.Should().BeFalse();
        state.ActiveOperation.Should().BeNull();
        state.TurnCancellation.Should().BeNull();
        state.IsTurnInterruptPending.Should().BeFalse();
        state.IsTrackedOperationCurrent(operationId).Should().BeFalse();
        state.Messages.Should().Contain(message =>
            message.Role == Role.System &&
            message.Text.Contains("Turn abandoned locally."));
    }

    [Fact]
    public void TryStartNextPendingSubmission_Should_RunQueuedPrompt_When_Idle()
    {
        AppState state = new(new UiBridge(), CreateConversationBackend().Object)
        {
            IsReady = true
        };
        state.PendingSubmissions.Enqueue(new PendingSubmission(
            PendingSubmissionKind.Prompt,
            "Continue with queued work"));

        MethodInfo tryStartNextPendingSubmission = typeof(Program).GetMethod(
            "TryStartNextPendingSubmission",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        tryStartNextPendingSubmission.Invoke(null, [state]);

        state.PendingSubmissions.Should().BeEmpty();
        state.IsBusy.Should().BeTrue();
        state.Messages.Should().ContainSingle(message =>
            message.Role == Role.User &&
            message.Text == "Continue with queued work");
    }

    [Fact]
    public void HandleCommand_Should_AllowClearWhileBusy()
    {
        AppState state = new(new UiBridge(), CreateConversationBackend().Object)
        {
            IsBusy = true
        };
        state.AddMessage(Role.User, "Earlier message");

        MethodInfo handleCommand = typeof(Program).GetMethod(
            "HandleCommand",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        handleCommand.Invoke(null, [state, "/clear"]);

        state.Messages.Should().ContainSingle(message =>
            message.Role == Role.System &&
            message.Text == "Screen cleared.");
    }

    [Fact]
    public void HandleCommand_Should_BlockBackendCommandWhileBusy()
    {
        AppState state = new(new UiBridge(), CreateConversationBackend().Object)
        {
            IsBusy = true
        };

        MethodInfo handleCommand = typeof(Program).GetMethod(
            "HandleCommand",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        handleCommand.Invoke(null, [state, "/help"]);

        state.Messages.Should().ContainSingle(message =>
            message.Role == Role.System &&
            message.Text == "That command is unavailable while StemCode is working.");
    }

    [Fact]
    public void UpdateStreaming_Should_DrainLargeBufferedResponsesInBiggerChunks()
    {
        AppState state = new(
            new UiBridge(),
            new Mock<IStemCodeBackend>(MockBehavior.Strict).Object);
        string bufferedText = new('x', 200);
        state.BeginAssistantStream(bufferedText);

        MethodInfo updateStreaming = typeof(Program).GetMethod(
            "UpdateStreaming",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        updateStreaming.Invoke(null, [state]);

        ChatMessage? message = state.GetStreamingMessage();
        message.Should().NotBeNull();
        message!.Text.Length.Should().BeGreaterThan(6);
        message.Text.Length.Should().BeLessThan(bufferedText.Length);
        state.StreamQueue.Count.Should().Be(bufferedText.Length - message.Text.Length);
    }

    [Fact]
    public void BuildConversationLines_Should_RenderFileEditPreviewAsSideBySideDiff()
    {
        AppState state = new(
            new UiBridge(),
            new Mock<IStemCodeBackend>(MockBehavior.Strict).Object);
        state.AddSystemMessage(
            "\u2022 Edited 1 file (+1 -1)\n" +
            "  - src/app.cs (+1 -1)\n" +
            "      10 -return 0;\n" +
            "      10 +return 1;\n" +
            "    ... +2 lines");

        MethodInfo buildConversationLines = typeof(Program).GetMethod(
            "BuildConversationLines",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        IEnumerable renderedLines = (IEnumerable)buildConversationLines.Invoke(null, [state, 90])!;
        string[] plainLines = GetConversationLinePropertyValues(renderedLines, "Plain");
        string[] markupLines = GetConversationLinePropertyValues(renderedLines, "Markup");

       plainLines.Should().Contain(line => line.Contains("before") && line.Contains("|") && line.Contains("after"));
       plainLines.Should().Contain(line => line.Contains("10 - return 0;") && line.Contains("|") && line.Contains("10 + return 1;"));
       plainLines.Should().Contain(line => line.Contains("... +2 lines"));
        markupLines.Should().Contain(line => line.Contains("white on #37222c") && line.Contains("deepskyblue1") && line.Contains("return"));
        markupLines.Should().Contain(line => line.Contains("white on #203b37") && line.Contains("deepskyblue1") && line.Contains("return"));
    }

    [Fact]
    public void BuildConversationLines_Should_HighlightStandaloneCssCodeBlock()
    {
        AppState state = new(
            new UiBridge(),
            new Mock<IStemCodeBackend>(MockBehavior.Strict).Object);
        state.AddMessage(
            Role.Assistant,
            "```css\n" +
            "/* theme */\n" +
            "body { color: red; }\n" +
            "```");

        string[] markupLines = RenderConversationMarkup(state);

        // /* */ comments are a CSS construct the generic highlighter would not color.
        markupLines.Should().Contain(line => line.Contains("[grey]/* theme */"));
        // "color" is a CSS keyword, absent from the generic keyword set, so coloring it
        // proves the css language mapping is in effect.
        markupLines.Should().Contain(line => line.Contains("[deepskyblue1]color"));
    }

    [Fact]
    public void BuildConversationLines_Should_HighlightEmbeddedCssAndJsInsideHtmlCodeBlock()
    {
        AppState state = new(
            new UiBridge(),
            new Mock<IStemCodeBackend>(MockBehavior.Strict).Object);
        state.AddMessage(
            Role.Assistant,
            "```html\n" +
            "<style>\n" +
            "/* heading */\n" +
            "h1 { color: red; }\n" +
            "</style>\n" +
            "<script>\n" +
            "// run\n" +
            "const value = 1;\n" +
            "</script>\n" +
            "```");

        string[] markupLines = RenderConversationMarkup(state);

        // Inside <style> the content is CSS: block comments and property names get colored,
        // neither of which the flat markup highlighter produces.
        markupLines.Should().Contain(line => line.Contains("[grey]/* heading */"));
        markupLines.Should().Contain(line => line.Contains("[deepskyblue1]color"));
        // Inside <script> the content is JS: line comments and keywords get colored.
        markupLines.Should().Contain(line => line.Contains("[grey]// run"));
        markupLines.Should().Contain(line => line.Contains("[deepskyblue1]const"));
    }

    [Fact]
    public void BuildConversationLines_Should_PersistBlockCommentHighlightingAcrossFileReadPreviewLines()
    {
        AppState state = new(
            new UiBridge(),
            new Mock<IStemCodeBackend>(MockBehavior.Strict).Object);
        state.AddSystemMessage(
            "• Read src/widget.cs (80 chars)\n" +
            "  - preview:\n" +
            $"      {1,4} /* block comment start\n" +
            $"      {2,4} still inside the comment\n" +
            $"      {3,4} end of comment */\n" +
            $"      {4,4} var visible = 1;");

        string[] markupLines = RenderConversationMarkup(state);

        // The middle line carries no comment markers of its own, so it can only be grey if
        // the block-comment state survived from the first preview line.
        markupLines.Should().Contain(line => line.Contains("[grey]still inside the comment"));
        // Once the comment closes, later lines highlight as code again.
        markupLines.Should().Contain(line => line.Contains("[deepskyblue1]var"));
    }

    [Fact]
    public void HandleConversationClick_Should_ToggleFullToolOutputAndExpand_OnCtrlClick()
    {
        AppState state = new(
            new UiBridge(),
            new Mock<IStemCodeBackend>(MockBehavior.Strict).Object)
        {
            MessagesContentTopRow = 12,
            VisibleThinkingMessageIds = [null],
            VisibleToolCallMessageIds = [null]
        };

        ChatMessage message = state.AddSystemMessage("Preview", isCollapsibleToolMessage: true);
        state.ExpandedToolMessageIds.Remove(message.Id);
        state.VisibleToolCallMessageIds = [message.Id];

        MethodInfo handleConversationClick = typeof(Program).GetMethod(
            "HandleConversationClick",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        handleConversationClick.Invoke(null, [state, 12, true]);

        state.FullToolOutputMessageIds.Should().Contain(message.Id);
        state.ExpandedToolMessageIds.Should().Contain(message.Id);
    }

    [Fact]
    public void BuildConversationLines_Should_RenderCompactToolOutputByDefault()
    {
        AppState state = new(
            new UiBridge(),
            new Mock<IStemCodeBackend>(MockBehavior.Strict).Object);
        ChatMessage message = state.AddSystemMessage("Default", isCollapsibleToolMessage: true);
        message.CompactToolOutputText = "Compact preview";
        message.FullToolOutputText = "Full output line 1\nFull output line 2";

        string[] plainLines = RenderConversationPlain(state);

        plainLines.Should().Contain(line => line.Contains("Compact preview"));
        plainLines.Should().NotContain(line => line.Contains("Full output line 2"));
    }

    [Fact]
    public void BuildConversationLines_Should_RenderFullToolOutput_WhenPerMessageToggleEnabled()
    {
        AppState state = new(
            new UiBridge(),
            new Mock<IStemCodeBackend>(MockBehavior.Strict).Object);
        ChatMessage message = state.AddSystemMessage("Default", isCollapsibleToolMessage: true);
        message.CompactToolOutputText = "Compact preview";
        message.FullToolOutputText = "Full output line 1\nFull output line 2";
        state.FullToolOutputMessageIds.Add(message.Id);

        string[] plainLines = RenderConversationPlain(state);

        plainLines.Should().Contain(line => line.Contains("Full output line 1"));
        plainLines.Should().Contain(line => line.Contains("Full output line 2"));
        plainLines.Should().NotContain(line => line.Contains("Compact preview"));
    }

    [Fact]
    public void BuildConversationLines_Should_ShowCtrlClickHint_ForCollapsedToolOutput()
    {
        AppState state = new(
            new UiBridge(),
            new Mock<IStemCodeBackend>(MockBehavior.Strict).Object);
        ChatMessage message = state.AddSystemMessage("Default", isCollapsibleToolMessage: true);
        message.CompactToolOutputText = "Compact preview";
        state.ExpandedToolMessageIds.Remove(message.Id);

        string[] plainLines = RenderConversationPlain(state);

        plainLines.Should().Contain(line => line.Contains("Ctrl+click full/preview"));
    }

    [Fact]
    public void BuildStickyLatestUserLines_Should_PinMostRecentSubmittedInput()
    {
        AppState state = new(
            new UiBridge(),
            new Mock<IStemCodeBackend>(MockBehavior.Strict).Object);
        state.AddMessage(Role.User, "Earlier prompt");
        state.AddMessage(Role.Assistant, "Working on it");
        ChatMessage latestUserMessage = state.AddMessage(Role.User, "Latest prompt stays pinned");

        MethodInfo buildStickyLatestUserLines = typeof(Program).GetMethod(
            "BuildStickyLatestUserLines",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            [typeof(AppState), typeof(ChatMessage), typeof(int), typeof(int)],
            modifiers: null)!;

        IEnumerable renderedLines = (IEnumerable)buildStickyLatestUserLines.Invoke(
            null,
            [state, latestUserMessage, 60, 10])!;
        string[] plainLines = GetConversationLinePropertyValues(renderedLines, "Plain");
        string[] markupLines = GetConversationLinePropertyValues(renderedLines, "Markup");

        plainLines.Should().HaveCount(3);
        plainLines[0].Trim().Should().BeEmpty();
        plainLines.Should().Contain(line => line.Contains("Latest prompt stays pinned"));
        plainLines.Should().NotContain(line => line.Contains("Earlier prompt"));
#if false
        plainLines[^1].Should().Contain("─");
#endif
        plainLines[^1].Trim().Should().BeEmpty();
        markupLines.Should().Contain(line => line.Contains("white on #1c1c1c"));
    }

    [Fact]
    public void SafeHeaderMarkup_Should_EscapeInvalidMarkup()
    {
        MethodInfo safeHeaderMarkup = typeof(Program).GetMethod(
            "SafeHeaderMarkup",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        string result = (string)safeHeaderMarkup.Invoke(null, ["[bold]Session[/] [oops"])!;

        result.Should().Be("[[bold]]Session[[/]] [[oops");
    }

    [Fact]
    public void BuildInputPanel_Should_NotThrow_WhenProviderContainsMarkupBrackets()
    {
        AppState state = new(
            new UiBridge(),
            new Mock<IStemCodeBackend>(MockBehavior.Strict).Object)
        {
            ActiveModelId = "gpt-5",
            ProviderName = "Provider [beta",
            HasMadeFirstLlmCall = true
        };

        MethodInfo buildInputPanel = typeof(Program).GetMethod(
            "BuildInputPanel",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        Action act = () => buildInputPanel.Invoke(null, [state]);

        act.Should().NotThrow();
    }

    [Fact]
    public void BuildInputLineMarkup_Should_Render_OneBlankPromptRow_Above_And_Below_Input()
    {
        AppState state = new(
            new UiBridge(),
            new Mock<IStemCodeBackend>(MockBehavior.Strict).Object);

        MethodInfo buildInputLineMarkup = typeof(Program).GetMethod(
            "BuildInputLineMarkup",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        string markup = (string)buildInputLineMarkup.Invoke(
            null,
            [state, "hello", 5, null, "Enter a prompt and press Enter"])!;

        string[] lines = markup.Split('\n');

        lines.Should().HaveCount(3);
        Markup.Remove(lines[0]).Should().Be("    ");
        Markup.Remove(lines[1]).Should().Contain("hello");
        Markup.Remove(lines[2]).Should().Be("    ");
    }

    [Fact]
    public void BuildInputLineMarkup_Should_Render_Placeholder_With_OneBlankPromptRow_Above_And_Below()
    {
        AppState state = new(
            new UiBridge(),
            new Mock<IStemCodeBackend>(MockBehavior.Strict).Object);

        MethodInfo buildInputLineMarkup = typeof(Program).GetMethod(
            "BuildInputLineMarkup",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        string markup = (string)buildInputLineMarkup.Invoke(
            null,
            [state, string.Empty, 0, null, "Enter a prompt and press Enter"])!;

        string[] lines = markup.Split('\n');

        lines.Should().HaveCount(3);
        Markup.Remove(lines[0]).Should().Be("    ");
        Markup.Remove(lines[1]).Should().Contain("Enter a prompt and press Enter");
        Markup.Remove(lines[2]).Should().Be("    ");
    }

    [Fact]
    public void GetInputPanelSize_Should_Match_RenderedInputMarkupLineCount()
    {
        AppState state = new(
            new UiBridge(),
            new Mock<IStemCodeBackend>(MockBehavior.Strict).Object)
        {
            InputCursorIndex = "hello".Length
        };
        state.Input.Append("hello");

        MethodInfo buildInputMarkup = typeof(Program).GetMethod(
            "BuildInputMarkup",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        MethodInfo getInputPanelSize = typeof(Program).GetMethod(
            "GetInputPanelSize",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        string markup = (string)buildInputMarkup.Invoke(null, [state])!;
        int panelSize = (int)getInputPanelSize.Invoke(null, [state])!;
        int expectedLineCount = markup
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Length;

        panelSize.Should().Be(expectedLineCount);
    }

    [Fact]
    public async Task BuildTrajectoryReaderLines_Should_RenderEscapedEventStream()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "stemcode-trajectory-view-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string eventLogPath = Path.Combine(directory, "session.events.jsonl");
        await File.WriteAllTextAsync(
            eventLogPath,
            """
            {"timestampUtc":"2026-05-20T01:00:00Z","sectionId":"session-id","eventType":"user_input","agentProfileName":"build","modelId":"model-a","workingDirectory":".","text":"fix it"}
            {"timestampUtc":"2026-05-20T01:00:02Z","sectionId":"session-id","eventType":"assistant_reasoning","agentProfileName":"build","modelId":"model-a","workingDirectory":".","text":"inspect <repo>"}
            {"timestampUtc":"2026-05-20T01:01:00Z","sectionId":"session-id","eventType":"tool_call_response","agentProfileName":"build","modelId":"model-a","workingDirectory":".","toolCallId":"call-1","toolName":"shell_command","toolStatus":"Success","toolResultJson":"{\"exitCode\":0,\"output\":\"done\"}"}
            """);

        try
        {
            IReadOnlyList<ReaderViewLine> lines = Program.BuildTrajectoryReaderLines(
                "session-id",
                eventLogPath,
                width: 100);

            lines.Select(static line => line.Plain)
                .Should()
                .Contain(line => line.Contains("Events: 3") && line.Contains("Turns: 1"));
            lines.Select(static line => line.Plain)
                .Should()
                .Contain(line => line.Contains("-- Turn 1"));
            lines.Select(static line => line.Plain)
                .Should()
                .Contain(line => line.Contains("#002 assistant reasoning") && line.Contains("turn+2.0s"));
            lines.Select(static line => line.Plain)
                .Should()
                .Contain(line => line.Contains("toolCall=call-1"));
            lines.Select(static line => line.Plain)
                .Should()
                .Contain(line => line.Contains("model=model-a") && line.Contains("profile=build"));
            lines.Select(static line => line.Plain)
                .Should()
                .Contain(line => line.Contains("Tools: shell_command"));
            lines.Select(static line => line.Plain)
                .Should()
                .Contain(line => line.Contains("Working directories: ."));
            lines.Select(static line => line.Plain)
                .Should()
                .Contain(line => line.Contains("Duration: 1m 0s"));
            lines.Select(static line => line.Plain)
                .Should()
                .Contain(line => line.Contains("#003 tool call response"));
            lines.Select(static line => line.Plain)
                .Should()
                .Contain(line => line.Contains("shell_command"));
            lines.Select(static line => line.Plain)
                .Should()
                .Contain(line => line.Contains("Success"));
            lines.Select(static line => line.Plain)
                .Should()
                .Contain(line => line.Contains("fix it"));
            lines.Select(static line => line.Plain)
                .Should()
                .Contain(line => line.Contains("inspect <repo>"));
            lines.Select(static line => line.Plain)
                .Should()
                .Contain(line => line.Contains("\"exitCode\": 0"));
            lines.Select(static line => line.Markup)
                .Should()
                .Contain(line => line.Contains("inspect <repo>"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task BuildTrajectoryReaderLines_Should_ShowEmptyLogMessage()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "stemcode-trajectory-view-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string eventLogPath = Path.Combine(directory, "session.events.jsonl");
        await File.WriteAllTextAsync(eventLogPath, string.Empty);

        try
        {
            IReadOnlyList<ReaderViewLine> lines = Program.BuildTrajectoryReaderLines(
                "session-id",
                eventLogPath,
                width: 100);

            lines.Select(static line => line.Plain)
                .Should()
                .Contain(line => line.Contains("Events: 0"));
            lines.Select(static line => line.Plain)
                .Should()
                .Contain(line => line.Contains("exists but is empty"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task BuildTrajectoryStepReaderLines_Should_RenderSelectableStepsAndDetails()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "stemcode-trajectory-view-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string eventLogPath = Path.Combine(directory, "session.events.jsonl");
        await File.WriteAllTextAsync(
            eventLogPath,
            """
            {"timestampUtc":"2026-05-20T01:00:00Z","sectionId":"session-id","eventType":"user_input","text":"fix it"}
            {"timestampUtc":"2026-05-20T01:00:02Z","sectionId":"session-id","eventType":"assistant_tool_call_request","toolName":"shell_command","toolArgumentsJson":"{\"cmd\":\"dotnet test\"}"}
            """);

        try
        {
            IReadOnlyList<ReaderViewLine> steps = Program.BuildTrajectoryStepReaderLines(
                "session-id",
                eventLogPath,
                width: 100);

            steps.Where(static line => line.SelectionKey is not null)
                .Select(static line => line.SelectionKey)
                .Should()
                .Equal("1", "2");
            steps.Select(static line => line.Plain)
                .Should()
                .Contain(line => line.Contains("#002 assistant tool call request") && line.Contains("shell_command"));

            IReadOnlyList<ReaderViewLine> details = Program.BuildTrajectoryEventDetailLines(
                "session-id",
                eventLogPath,
                width: 100,
                selectedEventIndex: 2);

            details.Select(static line => line.Plain)
                .Should()
                .Contain(line => line.Contains("#002 assistant tool call request"));
            details.Select(static line => line.Plain)
                .Should()
                .Contain(line => line.Contains("Tool arguments"));
            details.Select(static line => line.Plain)
                .Should()
                .Contain(line => line.Contains("\"cmd\": \"dotnet test\""));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task HandleReaderViewKey_Should_OpenTrajectoryDetails_ForCarriageReturnEnter()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "stemcode-trajectory-view-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string eventLogPath = Path.Combine(directory, "session.events.jsonl");
        await File.WriteAllTextAsync(
            eventLogPath,
            """
            {"timestampUtc":"2026-05-20T01:00:00Z","sectionId":"session-id","eventType":"user_input","text":"fix it"}
            """);

        try
        {
            AppState state = new(
                new UiBridge(),
                new Mock<IStemCodeBackend>(MockBehavior.Strict).Object)
            {
                SessionId = "session-id",
                IsReaderViewActive = true,
                ReaderViewKind = "trajectory",
                ReaderViewDataPath = eventLogPath,
                ReaderViewStyledLines = Program.BuildTrajectoryStepReaderLines(
                    "session-id",
                    eventLogPath,
                    width: 100)
            };

            MethodInfo handleReaderViewKey = typeof(Program).GetMethod(
                "HandleReaderViewKey",
                BindingFlags.NonPublic | BindingFlags.Static)!;
            handleReaderViewKey.Invoke(
                null,
                [state, new ConsoleKeyInfo('\r', 0, shift: false, alt: false, control: false)]);

            state.ReaderViewTitle.Should().Be("TRAJECTORY STEP");
            state.ReaderViewParentStyledLines.Should().NotBeNull();
            state.ReaderViewStyledLines!.Select(static line => line.Plain)
                .Should()
                .Contain(line => line.Contains("#001 user input"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task HandleReaderViewKey_Should_ReturnFromTrajectoryDetails_ForRawEscape()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "stemcode-trajectory-view-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string eventLogPath = Path.Combine(directory, "session.events.jsonl");
        await File.WriteAllTextAsync(
            eventLogPath,
            """
            {"timestampUtc":"2026-05-20T01:00:00Z","sectionId":"session-id","eventType":"user_input","text":"fix it"}
            """);

        try
        {
            IReadOnlyList<ReaderViewLine> parentLines = Program.BuildTrajectoryStepReaderLines(
                "session-id",
                eventLogPath,
                width: 100);
            AppState state = new(
                new UiBridge(),
                new Mock<IStemCodeBackend>(MockBehavior.Strict).Object)
            {
                SessionId = "session-id",
                IsReaderViewActive = true,
                ReaderViewTitle = "TRAJECTORY STEP",
                ReaderViewKind = "trajectory",
                ReaderViewDataPath = eventLogPath,
                ReaderViewStyledLines = Program.BuildTrajectoryEventDetailLines(
                    "session-id",
                    eventLogPath,
                    width: 100,
                    selectedEventIndex: 1),
                ReaderViewParentStyledLines = parentLines,
                ReaderViewParentTitle = "TRAJECTORY",
                ReaderViewParentInstructions = "select a step"
            };

            MethodInfo handleReaderViewKey = typeof(Program).GetMethod(
                "HandleReaderViewKey",
                BindingFlags.NonPublic | BindingFlags.Static)!;
            handleReaderViewKey.Invoke(
                null,
                [state, new ConsoleKeyInfo('\u001b', 0, shift: false, alt: false, control: false)]);

            state.IsReaderViewActive.Should().BeTrue();
            state.ReaderViewTitle.Should().Be("TRAJECTORY");
            state.ReaderViewStyledLines.Should().BeSameAs(parentLines);
            state.ReaderViewParentStyledLines.Should().BeNull();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task HandleReaderViewKey_Should_ExitTrajectoryList_ForRawEscape()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "stemcode-trajectory-view-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string eventLogPath = Path.Combine(directory, "session.events.jsonl");
        await File.WriteAllTextAsync(
            eventLogPath,
            """
            {"timestampUtc":"2026-05-20T01:00:00Z","sectionId":"session-id","eventType":"user_input","text":"fix it"}
            """);

        try
        {
            AppState state = new(
                new UiBridge(),
                new Mock<IStemCodeBackend>(MockBehavior.Strict).Object)
            {
                SessionId = "session-id",
                IsReaderViewActive = true,
                ReaderViewTitle = "TRAJECTORY",
                ReaderViewKind = "trajectory",
                ReaderViewDataPath = eventLogPath,
                ReaderViewStyledLines = Program.BuildTrajectoryStepReaderLines(
                    "session-id",
                    eventLogPath,
                    width: 100)
            };

            MethodInfo handleReaderViewKey = typeof(Program).GetMethod(
                "HandleReaderViewKey",
                BindingFlags.NonPublic | BindingFlags.Static)!;
            handleReaderViewKey.Invoke(
                null,
                [state, new ConsoleKeyInfo('\u001b', 0, shift: false, alt: false, control: false)]);

            state.IsReaderViewActive.Should().BeFalse();
            state.ReaderViewStyledLines.Should().BeNull();
            state.ReaderViewKind.Should().BeNull();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SanitizeCommitMessageSuggestion_Should_KeepSingleCleanSubjectLine()
    {
        MethodInfo sanitizeCommitMessageSuggestion = typeof(Program).GetMethod(
            "SanitizeCommitMessageSuggestion",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        string result = (string)sanitizeCommitMessageSuggestion.Invoke(
            null,
            ["```text\n- feat: add AI generated commit message\n\nextra details\n```"])!;

        result.Should().Be("feat: add AI generated commit message");
    }

    [Fact]
    public void ShowCommitMessagePrompt_Should_PrefillExistingCommitModalWithSuggestion()
    {
        AppState state = new(
            new UiBridge(),
            new Mock<IStemCodeBackend>(MockBehavior.Strict).Object);
        MethodInfo showCommitMessagePrompt = typeof(Program).GetMethod(
            "ShowCommitMessagePrompt",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        showCommitMessagePrompt.Invoke(null, [state, "feat: add AI commit suggestions"]);

        state.ActiveModal.Should().BeOfType<TextModalState>();
        TextModalState modal = (TextModalState)state.ActiveModal!;
        modal.Value.ToString().Should().Be("feat: add AI commit suggestions");
    }

    [Fact]
    public void ReadGitStatus_Should_KeepWorktreeOnlyChangesOutOfStagedList()
    {
        string repositoryPath = Path.Combine(
            Path.GetTempPath(),
            "stemcode-git-sidebar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repositoryPath);

        try
        {
            RunGitCommand(repositoryPath, "init");
            RunGitCommand(repositoryPath, "config", "user.name", "StemCode Tests");
            RunGitCommand(repositoryPath, "config", "user.email", "tests@stemcode.local");

            string filePath = Path.Combine(repositoryPath, "sidebar.txt");
            File.WriteAllText(filePath, "line 1\n");
            RunGitCommand(repositoryPath, "add", "sidebar.txt");
            RunGitCommand(repositoryPath, "commit", "-m", "initial");

            File.WriteAllText(filePath, "line 1\nline 2\n");

            MethodInfo readGitStatus = typeof(Program).GetMethod(
                "ReadGitStatus",
                BindingFlags.NonPublic | BindingFlags.Static)!;

            (List<(string Code, string Rel)> Staged, List<(string Code, string Rel)> Changes) result =
                ((List<(string Code, string Rel)> Staged, List<(string Code, string Rel)> Changes))
                readGitStatus.Invoke(null, [repositoryPath])!;

            result.Staged.Should().BeEmpty();
            result.Changes
                .Should()
                .ContainSingle()
                .Which
                .Should()
                .Be(("M", "sidebar.txt"));
        }
        finally
        {
            if (Directory.Exists(repositoryPath))
            {
                foreach (string path in Directory.EnumerateFileSystemEntries(repositoryPath, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                }

                File.SetAttributes(repositoryPath, FileAttributes.Normal);
                Directory.Delete(repositoryPath, recursive: true);
            }
        }
    }

    private static string[] RenderConversationMarkup(AppState state)
    {
        MethodInfo buildConversationLines = typeof(Program).GetMethod(
            "BuildConversationLines",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        IEnumerable renderedLines = (IEnumerable)buildConversationLines.Invoke(null, [state, 90])!;
        return GetConversationLinePropertyValues(renderedLines, "Markup");
    }

    private static string[] RenderConversationPlain(AppState state)
    {
        MethodInfo buildConversationLines = typeof(Program).GetMethod(
            "BuildConversationLines",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        IEnumerable renderedLines = (IEnumerable)buildConversationLines.Invoke(null, [state, 90])!;
        return GetConversationLinePropertyValues(renderedLines, "Plain");
    }

    private static Mock<IStemCodeBackend> CreateConversationBackend()
    {
        Mock<IStemCodeBackend> backend = new(MockBehavior.Strict);
        backend
            .Setup(static value => value.RunTurnAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<ConversationAttachment>>(),
                It.IsAny<StemCode.Application.UI.IUiBridge>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConversationTurnResult.AssistantMessage("Done."));

        return backend;
    }

    private static string[] GetConversationLinePropertyValues(
        IEnumerable lines,
        string propertyName)
    {
        List<string> values = [];

        foreach (object line in lines)
        {
            PropertyInfo property = line.GetType().GetProperty(propertyName)!;
            values.Add((string)property.GetValue(line)!);
        }

        return values.ToArray();
    }

    private static void RunGitCommand(string workingDirectory, params string[] arguments)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)!;
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        process.ExitCode.Should().Be(
            0,
            $"git {string.Join(' ', arguments)} should succeed but failed with:{Environment.NewLine}{output}{error}");
    }
}
