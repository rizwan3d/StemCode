using StemCode.Application.Abstractions;
using StemCode.Application.Exceptions;
using StemCode.Application.Models;
using System.Globalization;

namespace StemCode.Application.Commands;

/// <summary>
/// Rewinds code and conversation together to the point just before an earlier
/// user message: tracked file edits made since then are rolled back (and stay
/// available to /redo), and the conversation continues on a new branch without
/// that message and everything after it (the old branch remains in /tree).
/// </summary>
internal sealed class RewindCommandHandler : IReplCommandHandler
{
    public const string UsageText = "/rewind [turn-number] [both|code|conversation]";

    private enum RewindMode
    {
        Both,
        Code,
        Conversation
    }

    private readonly IConversationSectionStore _sectionStore;
    private readonly ISelectionPrompt _selectionPrompt;
    private readonly ISessionAppService _sessionAppService;
    private readonly IWorkspaceFileService _workspaceFileService;

    public RewindCommandHandler(
        IConversationSectionStore sectionStore,
        ISelectionPrompt selectionPrompt,
        ISessionAppService sessionAppService,
        IWorkspaceFileService workspaceFileService)
    {
        _sectionStore = sectionStore;
        _selectionPrompt = selectionPrompt;
        _sessionAppService = sessionAppService;
        _workspaceFileService = workspaceFileService;
    }

    public string CommandName => "rewind";

    public string Description => "Rewind file edits and the conversation together to before an earlier message.";

    public string Usage => UsageText;

    public async Task<ReplCommandResult> ExecuteAsync(
        ReplCommandContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        ReplSessionContext session = context.Session;
        IReadOnlyList<ConversationSectionTurn> turns = session.ConversationTurns;
        if (turns.Count == 0)
        {
            return ReplCommandResult.Continue("There are no earlier messages to rewind to.", ReplFeedbackKind.Warning);
        }

        int? turnNumber = null;
        RewindMode? mode = null;
        foreach (string argument in context.Arguments)
        {
            if (int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                if (parsed < 1 || parsed > turns.Count)
                {
                    return ReplCommandResult.Continue(
                        $"Turn number must be between 1 and {turns.Count.ToString(CultureInfo.InvariantCulture)}.",
                        ReplFeedbackKind.Error);
                }

                turnNumber = parsed;
                continue;
            }

            RewindMode? parsedMode = ParseMode(argument);
            if (parsedMode is null)
            {
                return ReplCommandResult.Continue($"Usage: {UsageText}", ReplFeedbackKind.Error);
            }

            mode = parsedMode;
        }

        try
        {
            turnNumber ??= await PromptForTurnAsync(session, turns, cancellationToken);
            if (turnNumber is null)
            {
                return ReplCommandResult.Continue("Rewind cancelled.", ReplFeedbackKind.Warning);
            }

            ConversationSectionTurn target = turns[turnNumber.Value - 1];
            bool hasCheckpoint = session.TryGetFileEditCheckpoint(target.TurnId, out int checkpointDepth);
            mode ??= context.Arguments.Count == 0
                ? await PromptForModeAsync(hasCheckpoint, session.UndoFileEditDepth - checkpointDepth, cancellationToken)
                : RewindMode.Both;
            if (mode is null)
            {
                return ReplCommandResult.Continue("Rewind cancelled.", ReplFeedbackKind.Warning);
            }

            return await RewindAsync(session, turnNumber.Value, target, hasCheckpoint, checkpointDepth, mode.Value, cancellationToken);
        }
        catch (PromptCancelledException)
        {
            return ReplCommandResult.Continue("Rewind cancelled.", ReplFeedbackKind.Warning);
        }
    }

    private async Task<ReplCommandResult> RewindAsync(
        ReplSessionContext session,
        int turnNumber,
        ConversationSectionTurn target,
        bool hasCheckpoint,
        int checkpointDepth,
        RewindMode mode,
        CancellationToken cancellationToken)
    {
        string turnText = turnNumber.ToString(CultureInfo.InvariantCulture);
        List<string> messages = [];

        if (mode is RewindMode.Both or RewindMode.Code)
        {
            if (!hasCheckpoint)
            {
                const string noCheckpoint =
                    "File edits can't be rewound for that message: it was sent before this StemCode run started, and file edit history isn't kept between runs.";
                if (mode == RewindMode.Code)
                {
                    return ReplCommandResult.Continue(noCheckpoint, ReplFeedbackKind.Warning);
                }

                messages.Add(noCheckpoint);
            }
            else
            {
                (int transactions, int files) = await RollBackFileEditsAsync(session, checkpointDepth, cancellationToken);
                messages.Add(transactions == 0
                    ? "No tracked file edits were made after that message."
                    : $"Rolled back {transactions} file edit(s) touching {files} file(s). Use /redo to reapply them one at a time.");
            }
        }

        if (mode == RewindMode.Code)
        {
            return ReplCommandResult.Continue(
                $"Rewound code to before turn {turnText}; the conversation is unchanged.\n{string.Join("\n", messages)}",
                ReplFeedbackKind.Info);
        }

        ConversationSectionTurn[] retainedTurns = session.ConversationTurns.Take(turnNumber - 1).ToArray();
        ConversationSectionSnapshot snapshot = SessionCommandSupport.CreateCopySnapshot(
            session,
            SessionCommandSupport.CreateTitleWithSuffix(session.SectionTitle, "rewind"),
            retainedTurns,
            totalEstimatedOutputTokens: 0,
            includeState: false);
        ReplSessionContext rewound = await SessionCommandSupport.SaveAndResumeAsync(
            snapshot,
            _sectionStore,
            _sessionAppService,
            cancellationToken);
        session.TransferFileEditHistoryTo(rewound);

        messages.Insert(0, mode == RewindMode.Both
            ? $"Rewound code and conversation to before turn {turnText}."
            : $"Rewound the conversation to before turn {turnText}; files are unchanged.");
        messages.Add($"Your message was: {SessionCommandSupport.CreatePreview(target.UserInput, 200)}");
        messages.Add("The previous branch is kept; use /tree to switch back.");
        messages.Add($"Session: {rewound.SessionId}");

        return ReplCommandResult.SwitchSession(rewound, string.Join("\n", messages));
    }

    private async Task<(int Transactions, int Files)> RollBackFileEditsAsync(
        ReplSessionContext session,
        int checkpointDepth,
        CancellationToken cancellationToken)
    {
        int transactions = 0;
        HashSet<string> files = new(StringComparer.OrdinalIgnoreCase);
        while (session.UndoFileEditDepth > checkpointDepth &&
               session.TryGetPendingUndoFileEdit(out WorkspaceFileEditTransaction? transaction) &&
               transaction is not null)
        {
            await _workspaceFileService.ApplyFileEditStatesAsync(transaction.BeforeStates, cancellationToken);
            session.CompleteUndoFileEdit();
            FileEditCommandStateRecorder.Record(session, "rewind", "Rewound file edit", transaction);
            foreach (WorkspaceFileEditState state in transaction.BeforeStates)
            {
                files.Add(state.Path);
            }

            transactions++;
        }

        return (transactions, files.Count);
    }

    private async Task<int?> PromptForTurnAsync(
        ReplSessionContext session,
        IReadOnlyList<ConversationSectionTurn> turns,
        CancellationToken cancellationToken)
    {
        return await _selectionPrompt.PromptAsync(
            new SelectionPromptRequest<int>(
                "Rewind to before which message?",
                turns
                    .Select((turn, index) =>
                    {
                        string detail = session.TryGetFileEditCheckpoint(turn.TurnId, out int depth)
                            ? FormatEditCount(session.UndoFileEditDepth - depth)
                            : "File edits from before this run can't be rewound.";
                        return new SelectionPromptOption<int>(
                            (index + 1).ToString(CultureInfo.InvariantCulture) + ". " +
                                SessionCommandSupport.CreatePreview(turn.UserInput),
                            index + 1,
                            detail);
                    })
                    .ToArray(),
                "Esc cancels rewind.",
                DefaultIndex: turns.Count - 1,
                AllowCancellation: true),
            cancellationToken);
    }

    private async Task<RewindMode?> PromptForModeAsync(
        bool hasCheckpoint,
        int editsSince,
        CancellationToken cancellationToken)
    {
        List<SelectionPromptOption<RewindMode>> options =
        [
            new("Code and conversation", RewindMode.Both, hasCheckpoint
                ? $"Roll back {FormatEditCount(editsSince).ToLowerInvariant()} and drop the conversation from this message on."
                : "Only the conversation can be rewound for this message."),
            new("Conversation only", RewindMode.Conversation, "Drop the conversation from this message on; keep files as they are.")
        ];
        if (hasCheckpoint)
        {
            options.Add(new("Code only", RewindMode.Code, "Roll back file edits made since this message; keep the conversation."));
        }

        return await _selectionPrompt.PromptAsync(
            new SelectionPromptRequest<RewindMode>(
                "What should be rewound?",
                options,
                "Esc cancels rewind.",
                DefaultIndex: 0,
                AllowCancellation: true),
            cancellationToken);
    }

    private static string FormatEditCount(int count)
    {
        return count switch
        {
            <= 0 => "No tracked file edits since this message.",
            1 => "1 tracked file edit since this message.",
            _ => $"{count.ToString(CultureInfo.InvariantCulture)} tracked file edits since this message."
        };
    }

    private static RewindMode? ParseMode(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "both" or "all" or "--both" => RewindMode.Both,
            "code" or "files" or "--code" => RewindMode.Code,
            "conversation" or "chat" or "--conversation" => RewindMode.Conversation,
            _ => null
        };
    }
}
