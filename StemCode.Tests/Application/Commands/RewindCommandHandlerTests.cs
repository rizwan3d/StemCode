using FluentAssertions;
using Moq;
using StemCode.Application.Abstractions;
using StemCode.Application.Commands;
using StemCode.Application.Models;
using StemCode.Domain.Models;

namespace StemCode.Tests.Application.Commands;

public sealed class RewindCommandHandlerTests
{
    private readonly Mock<IWorkspaceFileService> _files = new();
    private readonly Mock<IConversationSectionStore> _sectionStore = new();
    private readonly Mock<ISessionAppService> _sessionAppService = new();
    private readonly Mock<ISelectionPrompt> _selectionPrompt = new(MockBehavior.Strict);
    private readonly List<IReadOnlyList<WorkspaceFileEditState>> _appliedStates = [];
    private ConversationSectionSnapshot? _savedSnapshot;
    private readonly ReplSessionContext _rewoundSession = CreateSession();

    public RewindCommandHandlerTests()
    {
        _files
            .Setup(service => service.ApplyFileEditStatesAsync(It.IsAny<IReadOnlyList<WorkspaceFileEditState>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<WorkspaceFileEditState>, CancellationToken>((states, _) => _appliedStates.Add(states))
            .Returns(Task.CompletedTask);
        _sectionStore
            .Setup(store => store.SaveAsync(It.IsAny<ConversationSectionSnapshot>(), It.IsAny<CancellationToken>()))
            .Callback<ConversationSectionSnapshot, CancellationToken>((snapshot, _) => _savedSnapshot = snapshot)
            .Returns(Task.CompletedTask);
        _sessionAppService
            .Setup(service => service.ResumeAsync(It.IsAny<ResumeSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_rewoundSession);
    }

    [Fact]
    public async Task Rewind_Should_RollBackEditsSinceTurn_AndForkConversationBeforeIt()
    {
        ReplSessionContext session = CreateSession();
        session.CreatePendingConversationTurn("first");
        session.RecordFileEditTransaction(Edit("a.cs", "a0", "a1"));
        session.CreatePendingConversationTurn("second");
        session.RecordFileEditTransaction(Edit("b.cs", "b0", "b1"));
        session.RecordFileEditTransaction(Edit("a.cs", "a1", "a2"));
        session.CreatePendingConversationTurn("third");

        ReplCommandResult result = await CreateSut().ExecuteAsync(Context(session, "2"), CancellationToken.None);

        result.SessionOverride.Should().BeSameAs(_rewoundSession);
        result.Message.Should().Contain("Rewound code and conversation to before turn 2")
            .And.Contain("Rolled back 2 file edit(s) touching 2 file(s)")
            .And.Contain("Your message was: second");
        _appliedStates.Select(static states => states.Single().Content).Should().Equal("a1", "b0");
        _savedSnapshot!.Turns.Select(static turn => turn.UserInput).Should().Equal("first");

        // History moves to the new branch, so /undo and /redo keep working there.
        _rewoundSession.UndoFileEditDepth.Should().Be(1);
        _rewoundSession.TryGetPendingRedoFileEdit(out WorkspaceFileEditTransaction? redo).Should().BeTrue();
        redo!.AfterStates.Single().Content.Should().Be("b1");
    }

    [Fact]
    public async Task CodeOnly_Should_KeepConversation()
    {
        ReplSessionContext session = CreateSession();
        session.CreatePendingConversationTurn("first");
        session.RecordFileEditTransaction(Edit("a.cs", "a0", "a1"));

        ReplCommandResult result = await CreateSut().ExecuteAsync(Context(session, "1", "code"), CancellationToken.None);

        result.SessionOverride.Should().BeNull();
        result.Message.Should().Contain("the conversation is unchanged");
        _appliedStates.Should().ContainSingle();
        session.ConversationTurns.Should().ContainSingle();
        _savedSnapshot.Should().BeNull();
    }

    [Fact]
    public async Task ConversationOnly_Should_LeaveFilesAlone()
    {
        ReplSessionContext session = CreateSession();
        session.CreatePendingConversationTurn("first");
        session.RecordFileEditTransaction(Edit("a.cs", "a0", "a1"));

        ReplCommandResult result = await CreateSut().ExecuteAsync(Context(session, "1", "conversation"), CancellationToken.None);

        result.SessionOverride.Should().BeSameAs(_rewoundSession);
        result.Message.Should().Contain("files are unchanged");
        _appliedStates.Should().BeEmpty();
    }

    [Fact]
    public async Task Rewind_Should_WarnWhenTurnPredatesThisRun()
    {
        ReplSessionContext session = CreateSession();
        session.AddConversationTurn("restored from disk", "ok");

        ReplCommandResult codeOnly = await CreateSut().ExecuteAsync(Context(session, "1", "code"), CancellationToken.None);
        ReplCommandResult both = await CreateSut().ExecuteAsync(Context(session, "1"), CancellationToken.None);

        codeOnly.FeedbackKind.Should().Be(ReplFeedbackKind.Warning);
        codeOnly.Message.Should().Contain("can't be rewound");
        both.SessionOverride.Should().BeSameAs(_rewoundSession);
        both.Message.Should().Contain("can't be rewound");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("5")]
    [InlineData("sideways")]
    public async Task Rewind_Should_RejectInvalidArguments(string argument)
    {
        ReplSessionContext session = CreateSession();
        session.CreatePendingConversationTurn("first");

        ReplCommandResult result = await CreateSut().ExecuteAsync(Context(session, argument), CancellationToken.None);

        result.FeedbackKind.Should().Be(ReplFeedbackKind.Error);
    }

    private RewindCommandHandler CreateSut()
    {
        return new RewindCommandHandler(_sectionStore.Object, _selectionPrompt.Object, _sessionAppService.Object, _files.Object);
    }

    private static WorkspaceFileEditTransaction Edit(string path, string before, string after)
    {
        return new WorkspaceFileEditTransaction(
            $"edit {path}",
            [new WorkspaceFileEditState(path, true, before)],
            [new WorkspaceFileEditState(path, true, after)]);
    }

    private static ReplCommandContext Context(ReplSessionContext session, params string[] arguments)
    {
        string text = string.Join(' ', arguments);
        return new ReplCommandContext("rewind", text, arguments, "/rewind " + text, session);
    }

    private static ReplSessionContext CreateSession()
    {
        return new ReplSessionContext(
            new AgentProviderProfile(ProviderKind.OpenAiCompatible, "https://provider.example.com/v1"),
            "gpt-5-mini",
            ["gpt-5-mini"]);
    }
}
