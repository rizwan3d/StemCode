using FluentAssertions;
using StemCode.Application.Models;
using StemCode.Application.Profiles;
using StemCode.Application.Utilities;
using StemCode.Domain.Models;

namespace StemCode.Tests.Application.Models;

[Collection(global::StemCode.Tests.TestCollections.SecretRedactorState)]
public sealed class ReplSessionContextTests
{
    [Fact]
    public void SetContextWindowOverride_Should_CapProviderReportedWindow()
    {
        ReplSessionContext session = new(
            new AgentProviderProfile(ProviderKind.Ollama, null),
            "model-a",
            ["model-a"],
            modelContextWindowTokens: new Dictionary<string, int>
            {
                ["model-a"] = 128_000
            });

        session.SetContextWindowOverride(64_000);

        session.ContextWindowOverrideTokens.Should().Be(64_000);
        session.ActiveModelReportedContextWindowTokens.Should().Be(128_000);
        session.ActiveModelContextWindowTokens.Should().Be(64_000);
        session.ActiveModelContextMetadata!.ContextWindowTokens.Should().Be(128_000);
        session.IsPersistedStateDirty.Should().BeFalse();
    }

    [Fact]
    public void SetContextWindowOverride_Should_NotExceedProviderReportedWindow()
    {
        ReplSessionContext session = new(
            new AgentProviderProfile(ProviderKind.Ollama, null),
            "model-a",
            ["model-a"],
            modelContextWindowTokens: new Dictionary<string, int>
            {
                ["model-a"] = 128_000
            });

        session.SetContextWindowOverride(256_000);

        session.ActiveModelReportedContextWindowTokens.Should().Be(128_000);
        session.ActiveModelContextWindowTokens.Should().Be(128_000);
    }

    [Fact]
    public void SetContextWindowOverride_Should_WorkWhenProviderDoesNotReportWindow()
    {
        ReplSessionContext session = new(
            new AgentProviderProfile(ProviderKind.Ollama, null),
            "model-a",
            ["model-a"]);

        session.SetContextWindowOverride(32_000);

        session.ActiveModelReportedContextWindowTokens.Should().BeNull();
        session.ActiveModelContextWindowTokens.Should().Be(32_000);
    }

    [Fact]
    public void SessionResumeCommand_Should_UseCliExecutableName()
    {
        ReplSessionContext session = CreateSession();

        session.SessionResumeCommand.Should().Be($"stemcode --session {session.SectionId}");
        session.SectionResumeCommand.Should().Be(session.SessionResumeCommand);
    }

    [Fact]
    public void DeleteTemporaryArtifacts_Should_DeleteOnlyMatchingRetention()
    {
        ReplSessionContext session = CreateSession();
        string directory = Path.Combine(Path.GetTempPath(), "stemcode-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string turnFile = Path.Combine(directory, "turn.png");
        string sessionFile = Path.Combine(directory, "session.png");
        File.WriteAllText(turnFile, "turn");
        File.WriteAllText(sessionFile, "session");

        try
        {
            session.RegisterTemporaryArtifact(turnFile, TemporaryArtifactRetention.Turn);
            session.RegisterTemporaryArtifact(sessionFile, TemporaryArtifactRetention.Session);

            session.DeleteTemporaryArtifacts(TemporaryArtifactRetention.Turn);

            File.Exists(turnFile).Should().BeFalse();
            File.Exists(sessionFile).Should().BeTrue();

            session.DeleteTemporaryArtifacts(TemporaryArtifactRetention.Session);

            File.Exists(sessionFile).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void RecordFileEditTransaction_Should_ExposePendingUndo_And_CompleteUndoShouldMoveItToRedo()
    {
        ReplSessionContext session = CreateSession();
        WorkspaceFileEditTransaction transaction = CreateTransaction("file_write (README.md)");

        session.RecordFileEditTransaction(transaction);

        session.TryGetPendingUndoFileEdit(out WorkspaceFileEditTransaction? pendingUndo).Should().BeTrue();
        pendingUndo.Should().BeSameAs(transaction);

        session.CompleteUndoFileEdit();

        session.TryGetPendingUndoFileEdit(out _).Should().BeFalse();
        session.TryGetPendingRedoFileEdit(out WorkspaceFileEditTransaction? pendingRedo).Should().BeTrue();
        pendingRedo.Should().BeSameAs(transaction);
    }

    [Fact]
    public void CompleteRedoFileEdit_Should_MoveTransactionBackToUndoStack()
    {
        ReplSessionContext session = CreateSession();
        WorkspaceFileEditTransaction transaction = CreateTransaction("apply_patch (2 files)");
        session.RecordFileEditTransaction(transaction);
        session.CompleteUndoFileEdit();

        session.CompleteRedoFileEdit();

        session.TryGetPendingRedoFileEdit(out _).Should().BeFalse();
        session.TryGetPendingUndoFileEdit(out WorkspaceFileEditTransaction? pendingUndo).Should().BeTrue();
        pendingUndo.Should().BeSameAs(transaction);
    }

    [Fact]
    public void RecordFileEditTransaction_Should_ClearRedoStack_When_NewEditArrivesAfterUndo()
    {
        ReplSessionContext session = CreateSession();
        session.RecordFileEditTransaction(CreateTransaction("file_write (README.md)"));
        session.CompleteUndoFileEdit();

        session.RecordFileEditTransaction(CreateTransaction("apply_patch (2 files)"));

        session.TryGetPendingRedoFileEdit(out _).Should().BeFalse();
    }

    [Fact]
    public void RecordFileEditTransaction_Should_ExposeExpiredRedoTransactions_ForBackupCleanup()
    {
        ReplSessionContext session = CreateSession();
        WorkspaceFileEditTransaction expiredTransaction = CreateTransaction("file_write (README.md)");
        session.RecordFileEditTransaction(expiredTransaction);
        session.CompleteUndoFileEdit();

        session.RecordFileEditTransaction(CreateTransaction("apply_patch (2 files)"));

        session.DrainExpiredFileEditTransactions().Should().ContainSingle().Which.Should().BeSameAs(expiredTransaction);
        session.DrainExpiredFileEditTransactions().Should().BeEmpty();
    }

    [Fact]
    public void BeginFileEditTransactionBatch_Should_GroupMultipleEditsIntoOneUndoEntry()
    {
        ReplSessionContext session = CreateSession();

        using (session.BeginFileEditTransactionBatch())
        {
            session.RecordFileEditTransaction(new WorkspaceFileEditTransaction(
                "file_write (README.md)",
                [new WorkspaceFileEditState("README.md", exists: false, content: null)],
                [new WorkspaceFileEditState("README.md", exists: true, content: "hello")]));
            session.RecordFileEditTransaction(new WorkspaceFileEditTransaction(
                "file_write (src/App.js)",
                [new WorkspaceFileEditState("src/App.js", exists: true, content: "old")],
                [new WorkspaceFileEditState("src/App.js", exists: true, content: "new")]));
        }

        session.TryGetPendingUndoFileEdit(out WorkspaceFileEditTransaction? pendingUndo).Should().BeTrue();
        pendingUndo!.Description.Should().Be("tool round (2 edits across 2 files)");
        pendingUndo.BeforeStates.Should().HaveCount(2);
        pendingUndo.AfterStates.Should().HaveCount(2);
        pendingUndo.BeforeStates.Select(static state => state.Path).Should().Equal("README.md", "src/App.js");
        pendingUndo.AfterStates.Select(static state => state.Path).Should().Equal("README.md", "src/App.js");
    }

    [Fact]
    public void BeginFileEditTransactionBatch_Should_RespectPlatformPathComparison_ForCaseOnlyPaths()
    {
        ReplSessionContext session = CreateSession();

        using (session.BeginFileEditTransactionBatch())
        {
            session.RecordFileEditTransaction(new WorkspaceFileEditTransaction(
                "apply_patch (1 file)",
                [new WorkspaceFileEditState("Foo.txt", exists: true, content: "before")],
                [new WorkspaceFileEditState("Foo.txt", exists: false, content: null)]));
            session.RecordFileEditTransaction(new WorkspaceFileEditTransaction(
                "apply_patch (1 file)",
                [new WorkspaceFileEditState("foo.txt", exists: false, content: null)],
                [new WorkspaceFileEditState("foo.txt", exists: true, content: "after")]));
        }

        session.TryGetPendingUndoFileEdit(out WorkspaceFileEditTransaction? pendingUndo).Should().BeTrue();

        if (OperatingSystem.IsWindows())
        {
            pendingUndo!.BeforeStates.Should().ContainSingle().Which.Path.Should().Be("Foo.txt");
            pendingUndo.AfterStates.Should().ContainSingle().Which.Path.Should().Be("foo.txt");
        }
        else
        {
            pendingUndo!.BeforeStates.Select(static state => state.Path).Should().Equal("Foo.txt", "foo.txt");
            pendingUndo.AfterStates.Select(static state => state.Path).Should().Equal("Foo.txt", "foo.txt");
        }
    }

    [Fact]
    public void SetPendingExecutionPlan_Should_ExposePendingPlan_And_ClearShouldRemoveIt()
    {
        ReplSessionContext session = CreateSession();
        PendingExecutionPlan pendingPlan = new(
            "plan the refactor",
            "Plan\n1. Inspect\n2. Edit\n3. Validate",
            ["Inspect", "Edit", "Validate"]);

        session.SetPendingExecutionPlan(pendingPlan);

        session.HasPendingExecutionPlan.Should().BeTrue();
        session.PendingExecutionPlan.Should().NotBeNull();
        session.PendingExecutionPlan!.PlanningSummary.Should().Contain("Plan");
        session.PendingExecutionPlan.Tasks.Should().Equal("Inspect", "Edit", "Validate");

        session.ClearPendingExecutionPlan();

        session.HasPendingExecutionPlan.Should().BeFalse();
        session.PendingExecutionPlan.Should().BeNull();
    }

    [Fact]
    public void SetAgentProfile_Should_UpdateProfile_AndPersistUpdatedProfileName()
    {
        ReplSessionContext session = CreateSession();

        session.SetAgentProfile(BuiltInAgentProfiles.Plan);
        ConversationSectionSnapshot snapshot = session.CreateSectionSnapshot(DateTimeOffset.UtcNow.AddMinutes(1));

        session.AgentProfile.Name.Should().Be(BuiltInAgentProfiles.PlanName);
        session.IsPersistedStateDirty.Should().BeTrue();
        snapshot.AgentProfileName.Should().Be(BuiltInAgentProfiles.PlanName);
    }

    [Fact]
    public void SetReasoningEffort_Should_UpdateThinkingEffort_AndPersistItInSnapshot()
    {
        ReplSessionContext session = CreateSession();

        bool changed = session.SetReasoningEffort("high");
        ConversationSectionSnapshot snapshot = session.CreateSectionSnapshot(DateTimeOffset.UtcNow.AddMinutes(1));

        changed.Should().BeTrue();
        session.ReasoningEffort.Should().Be("high");
        session.IsPersistedStateDirty.Should().BeTrue();
        snapshot.ReasoningEffort.Should().Be("high");
    }

    [Fact]
    public void ClearReasoningEffort_Should_ClearStoredReasoningEffort()
    {
        ReplSessionContext session = CreateSession();
        session.SetReasoningEffort("high");

        bool changed = session.ClearReasoningEffort();

        changed.Should().BeTrue();
        session.ReasoningEffort.Should().BeNull();
    }

    [Fact]
    public void ReplaceProviderConfiguration_Should_UpdateProviderModelsAndPersistInSnapshot()
    {
        ReplSessionContext session = CreateSession();
        AgentProviderProfile providerProfile = new(ProviderKind.OpenRouter, null);

        session.ReplaceProviderConfiguration(
            providerProfile,
            "openai/gpt-5.4",
            ["openai/gpt-5.4", "anthropic/claude-sonnet-4.6"],
            new Dictionary<string, int>
            {
                ["openai/gpt-5.4"] = 400_000,
                ["anthropic/claude-sonnet-4.6"] = 200_000
            },
            null,
            "OpenRouter");

        ConversationSectionSnapshot snapshot = session.CreateSectionSnapshot(
            session.SectionCreatedAtUtc.AddMinutes(1));

        session.ProviderProfile.Should().Be(providerProfile);
        session.ProviderName.Should().Be("OpenRouter");
        session.ActiveProviderName.Should().Be("OpenRouter");
        session.ActiveModelId.Should().Be("openai/gpt-5.4");
        session.ActiveModelContextWindowTokens.Should().Be(400_000);
        session.AvailableModelIds.Should().Equal("openai/gpt-5.4", "anthropic/claude-sonnet-4.6");
        session.IsPersistedStateDirty.Should().BeTrue();
        snapshot.ProviderProfile.Should().Be(providerProfile);
        snapshot.ActiveProviderName.Should().Be("OpenRouter");
        snapshot.ActiveModelId.Should().Be("openai/gpt-5.4");
        snapshot.AvailableModelIds.Should().Equal("openai/gpt-5.4", "anthropic/claude-sonnet-4.6");
        snapshot.ModelContextWindowTokens.Should().Contain("openai/gpt-5.4", 400_000);
        snapshot.ModelContextWindowTokens.Should().Contain("anthropic/claude-sonnet-4.6", 200_000);
    }

    [Fact]
    public void CreateSectionSnapshot_Should_PersistToolCallsOnTurn()
    {
        ReplSessionContext session = CreateSession();

        session.AddConversationTurn(
            "read the README",
            "I read it.",
            [
                new ConversationToolCall(
                    "call_1",
                    "file_read",
                    """{ "path": "README.md" }""")
            ],
            ["\u2022 Read README.md (120 chars)"]);

        ConversationSectionSnapshot snapshot = session.CreateSectionSnapshot(
            session.SectionCreatedAtUtc.AddMinutes(1));

        snapshot.Turns.Should().ContainSingle();
        snapshot.Turns[0].ToolCalls.Should().ContainSingle();
        snapshot.Turns[0].ToolCalls[0].Id.Should().Be("call_1");
        snapshot.Turns[0].ToolCalls[0].Name.Should().Be("file_read");
        snapshot.Turns[0].ToolCalls[0].ArgumentsJson.Should().Be("""{ "path": "README.md" }""");
        snapshot.Turns[0].ToolOutputMessages.Should().ContainSingle();
        snapshot.Turns[0].ToolOutputMessages[0].Should().Be("\u2022 Read README.md (120 chars)");
    }

    [Fact]
    public void CreateSectionSnapshot_Should_PersistAssistantReasoningOnTurn()
    {
        ReplSessionContext session = CreateSession();

        session.AddConversationTurn(
            "update the README",
            "I updated it.",
            assistantReasoningContent: "The file needs a small edit.",
            assistantReasoningDetailsJson: """[{ "summary": "README edit chosen." }]""");

        ConversationSectionSnapshot snapshot = session.CreateSectionSnapshot(
            session.SectionCreatedAtUtc.AddMinutes(1));

        snapshot.Turns.Should().ContainSingle();
        snapshot.Turns[0].AssistantReasoningContent.Should().Be("The file needs a small edit.");
        snapshot.Turns[0].AssistantReasoningDetailsJson.Should().Contain("README edit chosen.");
        session.ConversationHistory[1].ReasoningContent.Should().Be("The file needs a small edit.");
    }

    [Fact]
    public void CreateSectionSnapshot_Should_NormalizeAssistantReasoningBeforePersistingHistory()
    {
        ReplSessionContext session = CreateSession();

        session.AddConversationTurn(
            "say hi",
            "Done.",
            assistantReasoningContent: "  \nProvider reasoning with padding.  \n",
            assistantReasoningDetailsJson: "  [{ \"summary\": \"Padded detail.\" }]  ");

        ConversationSectionSnapshot snapshot = session.CreateSectionSnapshot(
            session.SectionCreatedAtUtc.AddMinutes(1));

        snapshot.Turns.Should().ContainSingle();
        snapshot.Turns[0].AssistantReasoningContent.Should().Be("Provider reasoning with padding.");
        snapshot.Turns[0].AssistantReasoningDetailsJson.Should().Be("""[{ "summary": "Padded detail." }]""");
        session.ConversationHistory[1].ReasoningContent.Should().Be("Provider reasoning with padding.");
        session.ConversationHistory[1].ReasoningDetailsJson.Should().Be("""[{ "summary": "Padded detail." }]""");
    }

    [Fact]
    public void CreateSectionSnapshot_Should_RedactSecretsFromHistoryAndToolCalls()
    {
        bool originalValue = SecretRedactor.IsEnabled;
        SecretRedactor.IsEnabled = true;

        try
        {
            ReplSessionContext session = CreateSession();

            session.AddConversationTurn(
                "use api_key=secret-value",
                "saw Bearer abcdefghijklmnopqrstuvwxyz",
                [
                    new ConversationToolCall(
                        "call_1",
                        "shell_command",
                        """{ "command": "echo sk-abcdefghijklmnopqrstuvwxyz123456" }""")
                ],
                ["\u2022 Ran echo sk-abcdefghijklmnopqrstuvwxyz123456"]);

            ConversationSectionSnapshot snapshot = session.CreateSectionSnapshot(
                session.SectionCreatedAtUtc.AddMinutes(1));

            snapshot.Turns[0].UserInput.Should().Contain("api_key=<redacted>");
            snapshot.Turns[0].AssistantResponse.Should().Contain("Bearer <redacted>");
            snapshot.Turns[0].ToolCalls[0].ArgumentsJson.Should().Contain("<redacted>");
            snapshot.Turns[0].ToolCalls[0].ArgumentsJson.Should().NotContain("sk-abcdefghijklmnopqrstuvwxyz");
            snapshot.Turns[0].ToolOutputMessages[0].Should().Contain("<redacted>");
            snapshot.Turns[0].ToolOutputMessages[0].Should().NotContain("sk-abcdefghijklmnopqrstuvwxyz");
            session.ConversationHistory[0].Content.Should().Contain("api_key=<redacted>");
        }
        finally
        {
            SecretRedactor.IsEnabled = originalValue;
        }
    }

    [Fact]
    public void SessionState_Should_PersistFileEditAndTerminalContext_InSnapshot()
    {
        ReplSessionContext session = CreateSession();
        DateTimeOffset observedAtUtc = new(2026, 4, 23, 10, 0, 0, TimeSpan.Zero);

        session.RecordFileContext(new SessionFileContext(
            "README.md",
            "read",
            observedAtUtc,
            "Read 100 characters. Excerpt: StemCode helps with coding tasks."));
        session.RecordEditContext(new SessionEditContext(
            observedAtUtc.AddMinutes(1),
            "file_write (README.md)",
            ["README.md"],
            2,
            1));
        session.RecordTerminalCommand(new SessionTerminalCommand(
            observedAtUtc.AddMinutes(2),
            "dotnet test StemCode.slnx",
            ".",
            0,
            "Passed! Total: 292",
            null));

        ConversationSectionSnapshot snapshot = session.CreateSectionSnapshot(session.SectionCreatedAtUtc.AddMinutes(3));
        ReplSessionContext resumedSession = new(
            "StemCode",
            snapshot.ProviderProfile,
            snapshot.ActiveModelId,
            snapshot.AvailableModelIds,
            snapshot.SectionId,
            snapshot.Title,
            snapshot.CreatedAtUtc,
            snapshot.UpdatedAtUtc,
            snapshot.TotalEstimatedOutputTokens,
            snapshot.Turns,
            snapshot.PendingExecutionPlan,
            isResumedSection: true,
            agentProfile: BuiltInAgentProfiles.Resolve(snapshot.AgentProfileName),
            reasoningEffort: snapshot.ReasoningEffort,
            sessionState: snapshot.SessionState);

        snapshot.SessionState.Files.Should().ContainSingle();
        snapshot.SessionState.Edits.Should().ContainSingle();
        snapshot.SessionState.TerminalHistory.Should().ContainSingle();
        resumedSession.CreateStatefulContextPrompt().Should().Contain("README.md");
        resumedSession.CreateStatefulContextPrompt().Should().Contain("file_write (README.md)");
        resumedSession.CreateStatefulContextPrompt().Should().Contain("dotnet test StemCode.slnx");
    }

    [Fact]
    public void SessionState_Should_RedactSecretsFromTerminalHistory()
    {
        bool originalValue = SecretRedactor.IsEnabled;
        SecretRedactor.IsEnabled = true;

        try
        {
            ReplSessionContext session = CreateSession();
            session.RecordTerminalCommand(new SessionTerminalCommand(
                DateTimeOffset.UtcNow,
                "echo password=hunter2",
                ".",
                0,
                "github_pat_abcdefghijklmnopqrstuvwxyz1234567890",
                "AIzaabcdefghijklmnopqrstuvwxyz123456"));

            ConversationSectionSnapshot snapshot = session.CreateSectionSnapshot(
                session.SectionCreatedAtUtc.AddMinutes(1));

            snapshot.SessionState.TerminalHistory[0].Command.Should().Contain("password=<redacted>");
            snapshot.SessionState.TerminalHistory[0].StandardOutput.Should().Be("<redacted>");
            snapshot.SessionState.TerminalHistory[0].StandardError.Should().Be("<redacted>");
        }
        finally
        {
            SecretRedactor.IsEnabled = originalValue;
        }
    }

    [Fact]
    public void ResolvePathFromWorkingDirectory_Should_UseCurrentSessionDirectory()
    {
        string workspaceRoot = Path.Combine(
            Path.GetTempPath(),
            $"StemCode-SessionCwd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(workspaceRoot, "src", "api"));

        try
        {
            ReplSessionContext session = CreateSession(workspaceRoot);

            session.TrySetWorkingDirectory("src", out string? error).Should().BeTrue(error);

            session.WorkingDirectory.Should().Be("src");
            session.ResolvePathFromWorkingDirectory("Program.cs").Should().Be("src/Program.cs");
            session.ResolvePathFromWorkingDirectory("../README.md").Should().Be("README.md");

            session.TrySetWorkingDirectory("api", out error).Should().BeTrue(error);

            session.WorkingDirectory.Should().Be("src/api");
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
    public void SessionState_Should_PersistWorkingDirectory_InSnapshot()
    {
        string workspaceRoot = Path.Combine(
            Path.GetTempPath(),
            $"StemCode-SessionStateCwd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(workspaceRoot, "ToDoApp"));

        try
        {
            ReplSessionContext session = CreateSession(workspaceRoot);
            session.TrySetWorkingDirectory("ToDoApp", out string? error).Should().BeTrue(error);

            ConversationSectionSnapshot snapshot = session.CreateSectionSnapshot(
                session.SectionCreatedAtUtc.AddMinutes(1));
            ReplSessionContext resumedSession = new(
                "StemCode",
                snapshot.ProviderProfile,
                snapshot.ActiveModelId,
                snapshot.AvailableModelIds,
                snapshot.SectionId,
                snapshot.Title,
                snapshot.CreatedAtUtc,
                snapshot.UpdatedAtUtc,
                snapshot.TotalEstimatedOutputTokens,
                snapshot.Turns,
                snapshot.PendingExecutionPlan,
                isResumedSection: true,
                agentProfile: BuiltInAgentProfiles.Resolve(snapshot.AgentProfileName),
                reasoningEffort: snapshot.ReasoningEffort,
                sessionState: snapshot.SessionState,
                workspacePath: workspaceRoot);

            snapshot.SessionState.WorkingDirectory.Should().Be("ToDoApp");
            resumedSession.WorkingDirectory.Should().Be("ToDoApp");
            resumedSession.CreateStatefulContextPrompt().Should().Contain("Current working directory: ToDoApp");
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
    public void TrySetWorkingDirectory_Should_RejectSymlinkThatEscapesWorkspace()
    {
        string workspaceRoot = Path.Combine(
            Path.GetTempPath(),
            $"StemCode-SessionSymlinkCwd-{Guid.NewGuid():N}");
        string outsideRoot = Path.Combine(
            Path.GetTempPath(),
            $"StemCode-SessionSymlinkOutside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspaceRoot);
        Directory.CreateDirectory(outsideRoot);
        string linkPath = Path.Combine(workspaceRoot, "outside-link");

        try
        {
            if (!TryCreateDirectorySymlink(linkPath, outsideRoot))
            {
                return;
            }

            ReplSessionContext session = CreateSession(workspaceRoot);

            session.TrySetWorkingDirectory("outside-link", out string? error).Should().BeFalse();
            error.Should().Contain("workspace");
        }
        finally
        {
            if (Directory.Exists(workspaceRoot))
            {
                Directory.Delete(workspaceRoot, recursive: true);
            }

            if (Directory.Exists(outsideRoot))
            {
                Directory.Delete(outsideRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void GetConversationHistory_Should_CompactOlderTurns_When_HistoryExceedsLimit()
    {
        ReplSessionContext session = CreateSession();
        session.AddConversationTurn(
            "Question one\nwith extra whitespace",
            "Reply one.",
            [
                new ConversationToolCall(
                    "call_1",
                    "file_read",
                    """{ "path": "README.md" }""")
            ]);
        session.AddConversationTurn("Question two", "Reply two.");
        session.AddConversationTurn("Question three", "Reply three.");

        IReadOnlyList<ConversationRequestMessage> history = session.GetConversationHistory(1);

        history.Should().HaveCount(3);
        history[0].Role.Should().Be("user");
        history[0].Content.Should().Contain("Earlier conversation context");
        history[0].Content.Should().Contain("2 older turns");
        history[0].Content.Should().Contain("Question one with extra whitespace");
        history[0].Content.Should().Contain("Reply one.");
        history[0].Content.Should().Contain("tools: file_read");
        history[0].Content.Should().Contain("Question two");
        history[0].Content.Should().Contain("Reply two.");
        history[0].Content.Should().NotContain("Question three");
        history[1].Role.Should().Be("user");
        history[1].Content.Should().Be("Question three");
        history[2].Role.Should().Be("assistant");
        history[2].Content.Should().Be("Reply three.");
    }

    private static ReplSessionContext CreateSession(string? workspacePath = null)
    {
        return new ReplSessionContext(
            new AgentProviderProfile(ProviderKind.OpenAiCompatible, "https://provider.example.com/v1"),
            "gpt-5-mini",
            ["gpt-5-mini"],
            workspacePath: workspacePath);
    }

    private static bool TryCreateDirectorySymlink(
        string linkPath,
        string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException ||
                                         OperatingSystem.IsWindows() && exception is IOException)
        {
            return false;
        }
    }

    private static WorkspaceFileEditTransaction CreateTransaction(string description)
    {
        return new WorkspaceFileEditTransaction(
            description,
            [new WorkspaceFileEditState("README.md", exists: false, content: null)],
            [new WorkspaceFileEditState("README.md", exists: true, content: "hello")]);
    }
}
