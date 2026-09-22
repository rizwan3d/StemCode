using StemCode.Application.Backend;
using StemCode.Application.Commands;
using StemCode.Application.Models;

namespace StemCode.CLI;

public static partial class Program
{
    private static void UpdateModal(AppState state)
    {
        state.ActiveModal?.Update(state);
    }

    private static void SubmitInput(AppState state)
    {
        string text = state.Input.ToString().Trim();
        ConversationAttachment[] attachments = state.InputAttachments.ToArray();

        if (string.IsNullOrWhiteSpace(text) && attachments.Length == 0)
        {
            return;
        }

        bool isSlashCommand = attachments.Length == 0 && text.StartsWith('/');
        if (!state.IsReady && !isSlashCommand)
        {
            state.AddSystemMessage(
                state.HasFatalError
                    ? "StemCode backend failed to start. Use /exit and try again."
                    : "StemCode is still starting up. Please wait.");
            return;
        }

        ClearSubmittedInput(state);

        if (isSlashCommand)
        {
            HandleCommand(state, text);
            return;
        }

        PendingSubmission submission = new(
            PendingSubmissionKind.Prompt,
            string.IsNullOrWhiteSpace(text)
                ? $"Please inspect the {FormatAttachmentCount(attachments.Length)} I attached."
                : text,
            attachments);

        if (state.IsBusy || state.IsStreaming)
        {
            QueuePendingSubmission(state, submission);
            return;
        }

        ExecutePendingSubmission(state, submission);
    }

    private static void ClearSubmittedInput(AppState state)
    {
        state.Input.Clear();
        state.InputAttachments.Clear();
        state.CollapsedInputPastes.Clear();
        state.InputCursorIndex = 0;
        state.ClearInputSelection();
        ResetSlashCommandSuggestions(state);
    }

    private static void StartConversation(
        AppState state,
        string prompt,
        IReadOnlyList<ConversationAttachment>? attachments = null)
    {
        if (state.HasCompletedPlan)
        {
            state.ClearPlanState();
        }

        state.ResetTurnCancellation();
        state.TurnCancellation = CancellationTokenSource.CreateLinkedTokenSource(state.LifetimeCancellation.Token);
        long operationId = state.BeginTrackedOperation();
        state.UiBridge.SetActiveCliOperation(operationId);

        state.IsBusy = true;
        state.ClearBusyWhenStreamCompletes = false;
        state.CurrentTurnStartedAt = DateTimeOffset.UtcNow;
        state.PendingCompletionNote = null;
        state.ActivityText = "Thinking";
        state.UiBridge.ResetAssistantMessageChunkTracking();
        state.HasMadeFirstLlmCall = true;

        state.ActiveOperation = Task.Run(async () =>
        {
            try
            {
                ConversationTurnResult result = await state.Backend.RunTurnAsync(
                    prompt,
                    attachments ?? [],
                    state.UiBridge,
                    state.TurnCancellation.Token);

                state.UiBridge.Enqueue(appState =>
                {
                    if (!appState.IsTrackedOperationCurrent(operationId))
                    {
                        return;
                    }

                    string completionNote = result.Metrics is null
                        ? string.Empty
                        : FormatCompletionNote(
                            result.Metrics.Elapsed,
                            result.Metrics.DisplayedEstimatedOutputTokens,
                            appState.ActiveModelContextWindowTokens,
                            result.Metrics.EstimatedTotalTokens);

                    appState.PendingCompletionNote = string.IsNullOrWhiteSpace(completionNote)
                        ? null
                        : completionNote;
                    appState.CurrentTurnStartedAt = null;

                    if (!state.UiBridge.HasObservedAssistantMessageChunks() &&
                        !string.IsNullOrWhiteSpace(result.ResponseText))
                    {
                        appState.BeginAssistantStream(result.ResponseText);
                        appState.ActiveOperation = null;
                        appState.ResetTurnCancellation();
                        appState.IsTurnInterruptPending = false;
                        appState.ClearBusyWhenStreamCompletes = true;
                        appState.ActivityText = "Streaming response";
                        return;
                    }

                    appState.IsBusy = false;
                    appState.ActiveOperation = null;
                    appState.ResetTurnCancellation();
                    appState.IsTurnInterruptPending = false;
                    appState.ActivityText = appState.IsReady ? "Ready" : "Idle";
                    AppendTurnEditSummary(appState);
                    TryStartNextPendingSubmission(appState);
                });
            }
            catch (OperationCanceledException) when (state.LifetimeCancellation.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException) when (state.TurnCancellation?.IsCancellationRequested == true)
            {
                state.UiBridge.Enqueue(appState =>
                {
                    if (!appState.IsTrackedOperationCurrent(operationId))
                    {
                        return;
                    }

                    appState.IsBusy = false;
                    appState.ActiveOperation = null;
                    appState.ClearBusyWhenStreamCompletes = false;
                    appState.CurrentTurnStartedAt = null;
                    appState.PendingCompletionNote = null;
                    appState.ActivityText = appState.IsReady ? "Ready" : "Idle";
                    appState.ResetTurnCancellation();
                    appState.IsTurnInterruptPending = false;
                    appState.AddSystemMessage("Turn cancelled.");
                    TryStartNextPendingSubmission(appState);
                });
            }
            catch (Exception exception)
            {
                state.UiBridge.Enqueue(appState =>
                {
                    if (!appState.IsTrackedOperationCurrent(operationId))
                    {
                        return;
                    }

                    appState.IsBusy = false;
                    appState.ActiveOperation = null;
                    appState.ClearBusyWhenStreamCompletes = false;
                    appState.CurrentTurnStartedAt = null;
                    appState.PendingCompletionNote = null;
                    appState.ActivityText = appState.IsReady ? "Ready" : "Idle";
                    appState.ResetTurnCancellation();
                    appState.IsTurnInterruptPending = false;
                    appState.AddSystemMessage($"StemCode error: {exception.Message}");
                    TryStartNextPendingSubmission(appState);
                });
            }
        });
    }

    private static string FormatUserInputForDisplay(
        string prompt,
        IReadOnlyList<ConversationAttachment> attachments)
    {
        if (attachments.Count == 0)
        {
            return prompt;
        }

        return $"{prompt}{Environment.NewLine}{Environment.NewLine}[{FormatAttachmentCount(attachments.Count)} pasted/attached: {FormatAttachmentNames(attachments)}]";
    }

    private static string FormatAttachmentCount(int count)
    {
        return count == 1
            ? "1 file"
            : $"{count} files";
    }

    private static string FormatAttachmentNames(IReadOnlyList<ConversationAttachment> attachments)
    {
        string[] names = attachments
            .Take(4)
            .Select(static attachment => attachment.Name)
            .ToArray();
        string suffix = attachments.Count > names.Length
            ? $", +{attachments.Count - names.Length} more"
            : string.Empty;
        return string.Join(", ", names) + suffix;
    }

    private static void HandleCommand(AppState state, string command)
    {
        if (command == "/exit")
        {
            state.AddSystemMessage("Exiting StemCode.");
            state.Running = false;
            return;
        }

        if (command == "/clear")
        {
            state.Messages.Clear();
            state.FileEditsSummaryMessage = null;
            state.ResetConversationViewport();
            state.CurrentTurnStartedAt = null;
            state.PendingCompletionNote = null;
            state.AddSystemMessage("Screen cleared.");
            return;
        }

        if (command == "/ls")
        {
            ExecuteListFiles(state);
            return;
        }

        if (command.StartsWith("/read ", StringComparison.Ordinal))
        {
            string path = command["/read ".Length..].Trim();

            if (string.IsNullOrWhiteSpace(path))
            {
                state.AddSystemMessage("Usage: /read <file>");
                return;
            }

            RequestReadPermission(state, path);
            return;
        }

        if (state.IsBusy || state.IsStreaming)
        {
            state.AddSystemMessage("That command is unavailable while StemCode is working.");
            return;
        }

        if (!state.IsReady)
        {
            state.AddSystemMessage(
                state.HasFatalError
                    ? "StemCode backend failed to start. Use /exit and try again."
                    : "StemCode is still starting up. Please wait.");
            return;
        }

        if (TryHandleTrajectoryView(state, command))
        {
            return;
        }

        if (TryHandleTerminalView(state, command))
        {
            return;
        }

        if (TryStartCustomSlashCommand(state, command))
        {
            return;
        }

        StartCommand(state, command);
    }

    private static void RequestModelSelection(AppState state)
    {
        if (state.ActiveModal is not null)
        {
            return;
        }

        if (!state.IsReady)
        {
            state.AddSystemMessage(
                state.HasFatalError
                    ? "StemCode backend failed to start. Use /exit and try again."
                    : "StemCode is still starting up. Please wait.");
            return;
        }

        if (state.IsBusy || state.IsStreaming)
        {
            state.AddSystemMessage("Model selection is unavailable while StemCode is working.");
            return;
        }

        StartModelSelection(state);
    }

    private static void TogglePlanPanel(AppState state)
    {
        if (state.ActiveModal is not null)
        {
            return;
        }

        if (!state.IsPlanPinned && string.IsNullOrWhiteSpace(state.LatestPlanText))
        {
            state.AddSystemMessage("No plan is available yet.");
            return;
        }

        state.IsPlanPinned = !state.IsPlanPinned;
    }

    private static void RequestReadPermission(AppState state, string path)
    {
        state.ActiveModal = SelectionModalState<ReadPermissionChoice>.Create(
            new SelectionPromptRequest<ReadPermissionChoice>(
                "Allow local file read?",
                [
                    new SelectionPromptOption<ReadPermissionChoice>(
                        "Allow",
                        ReadPermissionChoice.Allow,
                        "Read the requested file from the current workspace."),
                    new SelectionPromptOption<ReadPermissionChoice>(
                        "Deny",
                        ReadPermissionChoice.Deny,
                        "Leave the file unread.")
                ],
                $"Read file '{path}'?",
                DefaultIndex: 1,
                AllowCancellation: true,
                AutoSelectAfter: TimeSpan.FromSeconds(10)),
            completionToken: new object(),
            onSelected: choice =>
            {
                if (choice == ReadPermissionChoice.Allow)
                {
                    ExecuteReadFile(state, path);
                }
                else
                {
                    state.AddSystemMessage($"Permission denied. Did not read file: {path}");
                }
            },
            onCancelled: _ => state.AddSystemMessage($"Permission denied. Did not read file: {path}"));

        state.AddSystemMessage($"Permission requested to read file: {path}");
    }

    private static void ExecuteReadFile(AppState state, string path)
    {
        try
        {
            string fullPath = GetSafePath(state, path);

            if (!File.Exists(fullPath))
            {
                state.AddSystemMessage($"File not found: {path}");
                return;
            }

            string content = File.ReadAllText(fullPath);
            string relativePath = Path.GetRelativePath(state.RootDirectory, fullPath);

            state.AddSystemMessage(
                $"""
                Permission granted.

                File: {relativePath}

                {content}
                """);
        }
        catch (Exception exception)
        {
            state.AddSystemMessage($"Error reading file: {exception.Message}");
        }
    }

    private static void ExecuteListFiles(AppState state)
    {
        try
        {
            List<string> files = Directory
                .EnumerateFiles(state.RootDirectory, "*", SearchOption.AllDirectories)
                .Where(path =>
                    !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                    !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                    !path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .Take(100)
                .Select(path => Path.GetRelativePath(state.RootDirectory, path))
                .ToList();

            if (files.Count == 0)
            {
                state.AddSystemMessage("No files found.");
                return;
            }

            state.AddSystemMessage("Files:\n\n" + string.Join('\n', files));
        }
        catch (Exception exception)
        {
            state.AddSystemMessage($"Error listing files: {exception.Message}");
        }
    }

    private static string GetSafePath(AppState state, string path)
    {
        string root = Path.GetFullPath(state.RootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        string normalizedPath = path
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        string fullPath = Path.GetFullPath(Path.Combine(root, normalizedPath));

        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!fullPath.StartsWith(root, comparison))
        {
            throw new InvalidOperationException("Path escapes workspace.");
        }

        return fullPath;
    }

    private static void StartCommand(AppState state, string command)
    {
        state.ResetTurnCancellation();
        state.TurnCancellation = CancellationTokenSource.CreateLinkedTokenSource(state.LifetimeCancellation.Token);
        long operationId = state.BeginTrackedOperation();
        state.UiBridge.SetActiveCliOperation(operationId);

        state.IsBusy = true;
        state.ActivityText = $"Running {FormatCommandActivity(command)}";

        state.ActiveOperation = Task.Run(async () =>
        {
            try
            {
                BackendCommandResult result = await state.Backend.RunCommandAsync(
                    command,
                    state.TurnCancellation.Token);

                state.UiBridge.Enqueue(appState =>
                {
                    if (!appState.IsTrackedOperationCurrent(operationId))
                    {
                        return;
                    }

                    appState.IsBusy = false;
                    appState.ActiveOperation = null;
                    appState.ResetTurnCancellation();
                    appState.IsTurnInterruptPending = false;
                    appState.ActivityText = appState.IsReady ? "Ready" : "Idle";
                    ApplySessionInfo(appState, result.SessionInfo);

                    if (result.CommandResult.ReplaySession)
                    {
                        RenderSessionView(
                            appState,
                            result.SessionInfo,
                            result.CommandResult.Message);
                    }
                    else
                    {
                        AddCommandFeedbackMessage(appState, result.CommandResult);
                    }

                    if (result.CommandResult.ExitRequested)
                    {
                        appState.Running = false;
                    }

                    TryStartNextPendingSubmission(appState);
                });
            }
            catch (OperationCanceledException) when (state.LifetimeCancellation.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException) when (state.TurnCancellation?.IsCancellationRequested == true)
            {
                state.UiBridge.Enqueue(appState =>
                {
                    if (!appState.IsTrackedOperationCurrent(operationId))
                    {
                        return;
                    }

                    appState.IsBusy = false;
                    appState.ActiveOperation = null;
                    appState.ActivityText = appState.IsReady ? "Ready" : "Idle";
                    appState.ResetTurnCancellation();
                    appState.IsTurnInterruptPending = false;
                    appState.AddSystemMessage("Turn cancelled.");
                    TryStartNextPendingSubmission(appState);
                });
            }
            catch (Exception exception)
            {
                state.UiBridge.Enqueue(appState =>
                {
                    if (!appState.IsTrackedOperationCurrent(operationId))
                    {
                        return;
                    }

                    appState.IsBusy = false;
                    appState.ActiveOperation = null;
                    appState.ActivityText = appState.IsReady ? "Ready" : "Idle";
                    appState.ResetTurnCancellation();
                    appState.IsTurnInterruptPending = false;
                    appState.AddSystemMessage($"Command failed: {exception.Message}");
                    TryStartNextPendingSubmission(appState);
                });
            }
        });
    }

    private static bool TryStartCustomSlashCommand(AppState state, string command)
    {
        if (!CustomSlashCommandService.TryExpand(
                state.RootDirectory,
                command,
                out CustomSlashCommandResolution? resolution,
                out string? error))
        {
            return false;
        }

        if (resolution is null)
        {
            state.AddSystemMessage(error ?? "Custom command could not be expanded.");
            return true;
        }

        state.JumpConversationToBottom();
        state.AddMessage(Role.User, command);
        StartConversation(state, resolution.ExpandedPrompt);
        return true;
    }

    private static void StartModelSelection(AppState state)
    {
        state.ResetTurnCancellation();
        state.TurnCancellation = CancellationTokenSource.CreateLinkedTokenSource(state.LifetimeCancellation.Token);
        long operationId = state.BeginTrackedOperation();
        state.UiBridge.SetActiveCliOperation(operationId);

        state.IsBusy = true;
        state.ActivityText = "Choosing model";

        state.ActiveOperation = Task.Run(async () =>
        {
            try
            {
                BackendCommandResult result = await state.Backend.SelectModelAsync(
                    state.TurnCancellation.Token);

                state.UiBridge.Enqueue(appState =>
                {
                    if (!appState.IsTrackedOperationCurrent(operationId))
                    {
                        return;
                    }

                    appState.IsBusy = false;
                    appState.ActiveOperation = null;
                    appState.ResetTurnCancellation();
                    appState.IsTurnInterruptPending = false;
                    appState.ActivityText = appState.IsReady ? "Ready" : "Idle";
                    ApplySessionInfo(appState, result.SessionInfo);

                    AddCommandFeedbackMessage(appState, result.CommandResult);
                    TryStartNextPendingSubmission(appState);
                });
            }
            catch (OperationCanceledException) when (state.LifetimeCancellation.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException) when (state.TurnCancellation?.IsCancellationRequested == true)
            {
                state.UiBridge.Enqueue(appState =>
                {
                    if (!appState.IsTrackedOperationCurrent(operationId))
                    {
                        return;
                    }

                    appState.IsBusy = false;
                    appState.ActiveOperation = null;
                    appState.ActivityText = appState.IsReady ? "Ready" : "Idle";
                    appState.ResetTurnCancellation();
                    appState.IsTurnInterruptPending = false;
                    appState.AddSystemMessage("Turn cancelled.");
                    TryStartNextPendingSubmission(appState);
                });
            }
            catch (Exception exception)
            {
                state.UiBridge.Enqueue(appState =>
                {
                    if (!appState.IsTrackedOperationCurrent(operationId))
                    {
                        return;
                    }

                    appState.IsBusy = false;
                    appState.ActiveOperation = null;
                    appState.ActivityText = appState.IsReady ? "Ready" : "Idle";
                    appState.ResetTurnCancellation();
                    appState.IsTurnInterruptPending = false;
                    appState.AddSystemMessage($"Model selection failed: {exception.Message}");
                    TryStartNextPendingSubmission(appState);
                });
            }
        });
    }

    private static void AddCommandFeedbackMessage(
        AppState state,
        ReplCommandResult result)
    {
        if (string.IsNullOrWhiteSpace(result.Message))
        {
            return;
        }

        string prefix = result.FeedbackKind switch
        {
            ReplFeedbackKind.Error => "Error: ",
            ReplFeedbackKind.Warning => "Warning: ",
            _ => string.Empty
        };

        state.AddSystemMessage(prefix + result.Message);
    }

    private static string FormatCommandActivity(string command)
    {
        string normalized = command.Trim();
        int firstSpaceIndex = normalized.IndexOf(' ');
        return firstSpaceIndex < 0
            ? normalized
            : normalized[..firstSpaceIndex];
    }

    // Running tally of files touched this conversation, updated in place after every turn
    // (one message, not a fresh one each turn — matches the VS surface). Rendered as a table
    // (action, clickable file, +/-) by AddFileEditsSummaryLines.
    private static void AppendTurnEditSummary(AppState state)
    {
        IReadOnlyList<FileEditSummary> files = state.Backend.GetFileEditSummary();
        if (files.Count == 0)
        {
            return;
        }

        int totalAdded = files.Sum(static f => f.AddedLineCount);
        int totalRemoved = files.Sum(static f => f.RemovedLineCount);
        // Plain text is the copy/fallback form; FileEdits drives the rich table render.
        string text = $"Files modified ({files.Count}) — +{totalAdded} / -{totalRemoved}";

        if (state.FileEditsSummaryMessage is null)
        {
            state.FileEditsSummaryMessage = state.AddMessage(Role.System, text);
        }
        else
        {
            state.FileEditsSummaryMessage.Text = text;
        }

        state.FileEditsSummaryMessage.FileEdits = files;
    }

    internal static void UpdateStreaming(AppState state)
    {
        state.SpinnerFrame++;

        if (!state.IsStreaming)
        {
            return;
        }

        ChatMessage? message = state.GetStreamingMessage();
        if (message is null)
        {
            state.IsStreaming = false;
            state.StreamingMessageId = null;
            state.StreamQueue.Clear();
            return;
        }

        if (state.StreamQueue.Count == 0)
        {
            state.IsStreaming = false;
            state.StreamingMessageId = null;

            if (state.ClearBusyWhenStreamCompletes)
            {
                state.IsBusy = false;
                state.ActiveOperation = null;
                state.ClearBusyWhenStreamCompletes = false;
                state.ResetTurnCancellation();
                state.IsTurnInterruptPending = false;
                state.ActivityText = state.IsReady ? "Ready" : "Idle";
                AppendTurnEditSummary(state);
                TryStartNextPendingSubmission(state);
            }

            return;
        }

        // Drain larger batches when a backlog builds up so completed tool output
        // and shell command results do not replay with an artificial slow-typing
        // effect.
        int charactersPerFrame = state.StreamQueue.Count switch
        {
            > 2048 => 512,
            > 512 => 256,
            > 128 => 96,
            _ => 48
        };

        for (int index = 0; index < charactersPerFrame && state.StreamQueue.Count > 0; index++)
        {
            message.Text += state.StreamQueue.Dequeue();
        }
    }

    internal static void TryStartNextPendingSubmission(AppState state)
    {
        if (!state.Running ||
            state.ActiveModal is not null ||
            state.IsBusy ||
            state.IsStreaming ||
            state.PendingSubmissions.Count == 0)
        {
            return;
        }

        PendingSubmission submission = state.PendingSubmissions.Dequeue();
        ExecutePendingSubmission(state, submission);
    }

    private static void ExecutePendingSubmission(
        AppState state,
        PendingSubmission submission)
    {
        switch (submission.Kind)
        {
            case PendingSubmissionKind.Command:
                HandleCommand(state, submission.Text);
                if (state.Running &&
                    state.ActiveModal is null &&
                    !state.IsBusy &&
                    !state.IsStreaming)
                {
                    TryStartNextPendingSubmission(state);
                }
                return;

            case PendingSubmissionKind.Prompt:
                IReadOnlyList<ConversationAttachment> attachments = submission.Attachments ?? [];
                state.JumpConversationToBottom();
                state.AddMessage(Role.User, FormatUserInputForDisplay(submission.Text, attachments));
                StartConversation(state, submission.Text, attachments);
                return;

            default:
                throw new InvalidOperationException($"Unsupported pending submission kind: {submission.Kind}");
        }
    }

    private static void QueuePendingSubmission(
        AppState state,
        PendingSubmission submission)
    {
        state.PendingSubmissions.Enqueue(submission);
        state.AddSystemMessage($"Queued {DescribePendingSubmission(submission)}.");
    }

    private static string DescribePendingSubmission(PendingSubmission submission)
    {
        string preview = TruncateSubmissionPreview(submission.Text, 48);

        return submission.Kind switch
        {
            PendingSubmissionKind.Command => $"command: {preview}",
            PendingSubmissionKind.Prompt when submission.Attachments is { Count: > 0 } attachments =>
                $"prompt with {FormatAttachmentCount(attachments.Count)}: {preview}",
            PendingSubmissionKind.Prompt => $"prompt: {preview}",
            _ => "request"
        };
    }

    private static string TruncateSubmissionPreview(string text, int maxLength)
    {
        string singleLine = text
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

        if (singleLine.Length <= maxLength)
        {
            return singleLine;
        }

        return singleLine[..Math.Max(0, maxLength - 1)] + "…";
    }
}
