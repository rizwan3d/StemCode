using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using StemCode.Application.Models;
using StemCode.Application.Utilities;
using Spectre.Console;

namespace StemCode.CLI;

public static partial class Program
{
    internal static void HandleInput(AppState state)
    {
        bool appendedInputInBatch = false;
        int pastedLineBreaksInBatch = 0;
        int inputBatchStartIndex = 0;
        int inputBatchEndIndex = 0;
        bool insertedInputInBatch = false;
        bool likelyPastedInputInBatch = false;

        while (HasPendingOrBufferedInput(state))
        {
            if (!TryReadNextInputKey(state, out ConsoleKeyInfo key))
            {
                break;
            }

            if (IsEscapeKey(key) &&
                TryHandleTerminalEscapeInput(state))
            {
                continue;
            }

            if (state.ActiveModal is not null)
            {
                state.ActiveModal.HandleKey(state, key);
                return;
            }

            // While a voice dictation is running, any key press stops it
            // (the runtime process is cancelled and the mic is released).
            if (IsVoiceOperationActive(state))
            {
                StopVoiceOperation(state);
                continue;
            }

            if (TrySkipLineFeedAfterCarriageReturn(state, key))
            {
                continue;
            }

            if (key.Key == ConsoleKey.C &&
                key.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                state.Running = false;
                return;
            }

            if (state.IsReaderViewActive)
            {
                HandleReaderViewKey(state, key);
                continue;
            }

            if (state.IsCopyModeActive)
            {
                HandleCopyModeKey(state, key);
                continue;
            }

            if (key.Key == ConsoleKey.F5)
            {
                EnterReaderView(state);
                return;
            }

            if (key.Key == ConsoleKey.F6)
            {
                EnterCopyMode(state);
                return;
            }

            if (key.Key == ConsoleKey.F7)
            {
                ToggleGitSidebar(state);
                return;
            }

            if (state.IsGitSidebarVisible && TryHandleGitSidebarScrollKey(state, key))
            {
                continue;
            }

            if (TryHandleGitSidebarKey(state, key))
            {
                continue;
            }

            if (IsVoiceDictationKey(key))
            {
                StartVoiceDictation(state);
                return;
            }

            if (key.Key == ConsoleKey.V &&
                key.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                PasteFromClipboard(state);
                continue;
            }

            if (IsToggleThinkingKey(key))
            {
                ToggleThinkingExpansion(state);
                continue;
            }

            if (state.VimModeEnabled && TryHandleVimKey(state, key))
            {
                continue;
            }

            if (IsEscapeKey(key))
            {
                if (TryDismissSlashCommandSuggestions(state))
                {
                    continue;
                }

                if (TryCancelActiveTurn(state))
                {
                    continue;
                }

                continue;
            }

            if (key.Key == ConsoleKey.F2)
            {
                RequestModelSelection(state);
                return;
            }

            if (key.Key == ConsoleKey.F3)
            {
                TogglePlanPanel(state);
                return;
            }

            if (key.Key == ConsoleKey.F4)
            {
                RemovePendingInputItem(state);
                return;
            }

            if (TryHandleSlashCommandSuggestionInput(state, key))
            {
                return;
            }

            if (HandlePlanScrollInput(state, key))
            {
                continue;
            }

            if (HandleInputEditingKey(state, key))
            {
                continue;
            }

            if (HandleConversationScrollInput(state, key))
            {
                continue;
            }

            if (IsEnterKey(key))
            {
                if (IsMultilineEnterKey(key))
                {
                    int cursorIndexBeforeInsert = state.InputCursorIndex;
                    AppendInputLineBreak(state, key);
                    TrackInputInsertedInBatch(
                        ref insertedInputInBatch,
                        ref inputBatchStartIndex,
                        ref inputBatchEndIndex,
                        cursorIndexBeforeInsert,
                        state.InputCursorIndex);
                    appendedInputInBatch = true;
                    continue;
                }

                if (IsLikelyPastedLineBreak(key, appendedInputInBatch, pastedLineBreaksInBatch))
                {
                    int cursorIndexBeforeInsert = state.InputCursorIndex;
                    AppendInputLineBreak(state, key);
                    TrackInputInsertedInBatch(
                        ref insertedInputInBatch,
                        ref inputBatchStartIndex,
                        ref inputBatchEndIndex,
                        cursorIndexBeforeInsert,
                        state.InputCursorIndex);
                    appendedInputInBatch = true;
                    likelyPastedInputInBatch = true;
                    pastedLineBreaksInBatch++;
                    continue;
                }

                if (TryHandleVoiceInputCommand(state))
                {
                    return;
                }

                if (IsVoiceOperationActive(state))
                {
                    state.AddSystemMessage("Voice dictation is still in progress.");
                    return;
                }

                SubmitInput(state);
                return;
            }

            if (!char.IsControl(key.KeyChar))
            {
                int cursorIndexBeforeInsert = state.InputCursorIndex;
                InsertInputText(state, key.KeyChar.ToString());
                TrackInputInsertedInBatch(
                    ref insertedInputInBatch,
                    ref inputBatchStartIndex,
                    ref inputBatchEndIndex,
                    cursorIndexBeforeInsert,
                    state.InputCursorIndex);
                appendedInputInBatch = true;
            }
        }

        if (likelyPastedInputInBatch && insertedInputInBatch)
        {
            TryAddCollapsedInputPaste(
                state,
                inputBatchStartIndex,
                inputBatchEndIndex - inputBatchStartIndex);
        }
    }

    private static bool TryCancelActiveTurn(AppState state)
    {
        if (!state.IsBusy && !state.IsStreaming)
        {
            return false;
        }

        if (state.ActiveOperation is { IsCompleted: false })
        {
            if (!state.IsTurnInterruptPending)
            {
                state.IsTurnInterruptPending = true;
                state.ActivityText = "Interrupting";
                state.CancelTurn();
                state.AddSystemMessage(
                    "Interrupt requested. Press Esc again to abandon this turn locally if it does not stop.");
                return true;
            }

            AbandonActiveTurn(state);
            return true;
        }

        if (state.IsStreaming)
        {
            state.IsStreaming = false;
            state.StreamingMessageId = null;
            state.StreamQueue.Clear();
            state.ClearBusyWhenStreamCompletes = false;
            state.IsBusy = false;
            state.CurrentTurnStartedAt = null;
            state.PendingCompletionNote = null;
            state.ActivityText = state.IsReady ? "Ready" : "Idle";
            state.ResetTurnCancellation();
            state.IsTurnInterruptPending = false;
            state.AddSystemMessage("Turn cancelled.");
            return true;
        }

        if (state.IsBusy)
        {
            if (!state.IsTurnInterruptPending)
            {
                state.IsTurnInterruptPending = true;
                state.ActivityText = "Interrupting";
                state.CancelTurn();
                return true;
            }

            state.CancelTurn();
            return true;
        }

        return false;
    }

    private static void AbandonActiveTurn(AppState state)
    {
        state.CancelTurn();
        state.IsStreaming = false;
        state.StreamingMessageId = null;
        state.StreamQueue.Clear();
        state.ClearBusyWhenStreamCompletes = false;
        state.IsBusy = false;
        state.ActiveOperation = null;
        state.CurrentTurnStartedAt = null;
        state.PendingCompletionNote = null;
        state.ActivityText = state.IsReady ? "Ready" : "Idle";
        state.ResetTurnCancellation();
        state.AbandonTrackedOperation();
        state.AddSystemMessage("Turn abandoned locally. Late output from the interrupted turn will be ignored.");
        TryStartNextPendingSubmission(state);
    }

    /// <summary>
    /// vi-style modal editing. In insert mode only Escape is intercepted (to enter
    /// normal mode, unless it is needed to dismiss suggestions or cancel a turn);
    /// in normal mode printable keys are commands. Enter, arrows and modified keys
    /// always fall through to the regular handlers.
    /// </summary>
    private static bool TryHandleVimKey(AppState state, ConsoleKeyInfo key)
    {
        if (state.Vim.Mode == VimMode.Insert)
        {
            if (!IsEscapeKey(key) ||
                TryDismissSlashCommandSuggestions(state) ||
                state.IsBusy ||
                state.IsStreaming)
            {
                return false;
            }

            ApplyVimResult(state, state.Vim.EnterNormalMode(state.Input.ToString(), ClampInputCursor(state)));
            return true;
        }

        if (IsEscapeKey(key))
        {
            return false;
        }

        if (IsEnterKey(key))
        {
            // Submitting returns to insert mode for the next prompt.
            state.Vim.Reset();
            return false;
        }

        if (key.Modifiers.HasFlag(ConsoleModifiers.Control) ||
            key.Modifiers.HasFlag(ConsoleModifiers.Alt) ||
            char.IsControl(key.KeyChar))
        {
            return false;
        }

        VimInputEngine.Result result = state.Vim.HandleNormalKey(key.KeyChar, state.Input.ToString(), ClampInputCursor(state));
        ApplyVimResult(state, result);
        return result.Handled;
    }

    private static void ApplyVimResult(AppState state, VimInputEngine.Result result)
    {
        if (!string.Equals(state.Input.ToString(), result.Text, StringComparison.Ordinal))
        {
            state.Input.Clear();
            state.Input.Append(result.Text);
            state.CollapsedInputPastes.Clear();
            ResetSlashCommandSuggestions(state);
        }

        state.InputCursorIndex = Math.Clamp(result.Cursor, 0, state.Input.Length);
        state.ClearInputSelection();
    }

    private static bool HandleInputEditingKey(AppState state, ConsoleKeyInfo key)
    {
        if (IsSelectAllKey(key))
        {
            SelectAllInput(state);
            return true;
        }

        if (IsBackspaceKey(key))
        {
            if (DeleteSelectedInput(state))
            {
                return true;
            }

            DeleteInputBeforeCursor(state);
            return true;
        }

        if (IsDeleteKey(key))
        {
            if (DeleteSelectedInput(state))
            {
                return true;
            }

            DeleteInputAtCursor(state);
            return true;
        }

        if (key.Key == ConsoleKey.LeftArrow && state.Input.Length > 0)
        {
            if (state.HasInputSelection)
            {
                CollapseInputSelection(state, collapseToStart: true);
                return true;
            }

            MoveInputCursor(state, -1);
            return true;
        }

        if (key.Key == ConsoleKey.RightArrow && state.Input.Length > 0)
        {
            if (state.HasInputSelection)
            {
                CollapseInputSelection(state, collapseToStart: false);
                return true;
            }

            MoveInputCursor(state, 1);
            return true;
        }

        if (key.Key == ConsoleKey.Home && state.Input.Length > 0)
        {
            MoveInputCursorToStart(state);
            return true;
        }

        if (key.Key == ConsoleKey.End && state.Input.Length > 0)
        {
            MoveInputCursorToEnd(state);
            return true;
        }

        return false;
    }

    private static bool TrySkipLineFeedAfterCarriageReturn(AppState state, ConsoleKeyInfo key)
    {
        if (!state.SkipNextInputLineFeed)
        {
            return false;
        }

        state.SkipNextInputLineFeed = false;
        return key.KeyChar == '\n';
    }

    private static bool IsBackspaceKey(ConsoleKeyInfo key)
    {
        return key.Key == ConsoleKey.Backspace ||
            key.KeyChar is '\b' or '\u007f';
    }

    private static bool IsDeleteKey(ConsoleKeyInfo key)
    {
        return key.Key == ConsoleKey.Delete;
    }

    private static bool IsSelectAllKey(ConsoleKeyInfo key)
    {
        return (key.Key == ConsoleKey.A && key.Modifiers.HasFlag(ConsoleModifiers.Control)) ||
            key.KeyChar == '\u0001';
    }

    private static bool IsToggleThinkingKey(ConsoleKeyInfo key)
    {
        return (key.Key == ConsoleKey.T && key.Modifiers.HasFlag(ConsoleModifiers.Control)) ||
            key.KeyChar == '\u0014';
    }

    private static void ToggleThinkingExpansion(AppState state)
    {
        bool hasThinking = state.Messages.Any(m => m.Role == Role.Thinking);
        bool hasToolCalls = state.Messages.Any(m => m.IsCollapsibleToolMessage);
        bool allExpanded = (!hasThinking || state.AreAllThinkingExpanded()) &&
                           (!hasToolCalls || state.AreAllToolCallsExpanded());

        if (allExpanded)
        {
            state.ExpandedThinkingMessageIds.Clear();
            state.ExpandedToolMessageIds.Clear();
        }
        else
        {
            state.ExpandAllThinking();
            state.ExpandAllToolCalls();
        }

        state.SkipNextInputLineFeed = false;
    }

    private static bool IsEnterKey(ConsoleKeyInfo key)
    {
        return key.Key == ConsoleKey.Enter ||
            key.KeyChar is '\r' or '\n';
    }

    private static bool IsMultilineEnterKey(ConsoleKeyInfo key)
    {
        return IsEnterKey(key) &&
            (key.Modifiers.HasFlag(ConsoleModifiers.Shift) ||
                key.Modifiers.HasFlag(ConsoleModifiers.Control) ||
                IsShiftKeyPressed() ||
                IsControlKeyPressed());
    }

    private static bool IsLikelyPastedLineBreak(
        ConsoleKeyInfo key,
        bool appendedInputInBatch,
        int pastedLineBreaksInBatch)
    {
        return IsEnterKey(key) &&
            (HasBufferedInputAfterDelay(PasteContinuationReadTimeoutMilliseconds) ||
                pastedLineBreaksInBatch > 0 ||
                (appendedInputInBatch && key.KeyChar == '\n'));
    }

    private static bool IsEscapeKey(ConsoleKeyInfo key)
    {
        return key.Key == ConsoleKey.Escape ||
            key.KeyChar == '\u001b';
    }

    private static bool HandlePlanScrollInput(AppState state, ConsoleKeyInfo key)
    {
        if (!HasPinnedPlan(state) || key.Modifiers != 0)
        {
            return false;
        }

        switch (key.KeyChar)
        {
            case '[':
                ScrollPinnedPlan(state, -MouseWheelScrollLineCount);
                return true;

            case ']':
                ScrollPinnedPlan(state, MouseWheelScrollLineCount);
                return true;

            default:
                return false;
        }
    }

    private static bool HandleConversationScrollInput(AppState state, ConsoleKeyInfo key)
    {
        int viewportLineCount = GetMessageViewportLineCount(state);
        int pageSize = Math.Max(1, viewportLineCount - 1);

        switch (key.Key)
        {
            case ConsoleKey.PageUp:
                ScrollConversation(state, pageSize);
                return true;

            case ConsoleKey.PageDown:
                ScrollConversation(state, -pageSize);
                return true;

            case ConsoleKey.UpArrow:
                ScrollConversation(state, MouseWheelScrollLineCount);
                return true;

            case ConsoleKey.DownArrow:
                ScrollConversation(state, -MouseWheelScrollLineCount);
                return true;

            case ConsoleKey.Home:
                state.ConversationScrollOffset = GetMaxConversationScrollOffset(state);
                state.SyncConversationAutoScrollPreference();
                return true;

            case ConsoleKey.End:
                state.JumpConversationToBottom();
                return true;

            default:
                return false;
        }
    }

    private static bool TryHandleTerminalEscapeInput(AppState state)
    {
        if (!TryReadBufferedKey(state, out ConsoleKeyInfo prefixKey))
        {
            return false;
        }

        return TryHandleTerminalEscapeFollowupKey(state, prefixKey);
    }

    private static bool TryHandleTerminalEscapeFollowupKey(
        AppState state,
        ConsoleKeyInfo prefixKey)
    {
        if (prefixKey.KeyChar == '[')
        {
            return ConsumeCsiInput(state);
        }

        if (prefixKey.KeyChar == 'O')
        {
            ConsumeSs3Input(state);
            return true;
        }

        if (TryHandleAltModifiedKey(state, prefixKey))
        {
            return true;
        }

        state.PendingInputKeys.Enqueue(prefixKey);
        return false;
    }

    private static bool TryHandleAltModifiedKey(AppState state, ConsoleKeyInfo key)
    {
        ConsoleKeyInfo? altKey = TryCreateAltModifiedKey(key);
        return altKey is ConsoleKeyInfo normalizedKey &&
            TryHandleGitSidebarKey(state, normalizedKey);
    }

    private static ConsoleKeyInfo? TryCreateAltModifiedKey(ConsoleKeyInfo key)
    {
        if (key.KeyChar == ' ')
        {
            return new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, shift: false, alt: true, control: false);
        }

        if (!char.IsLetter(key.KeyChar))
        {
            return null;
        }

        char normalizedChar = char.ToUpperInvariant(key.KeyChar);
        if (!Enum.TryParse(normalizedChar.ToString(), ignoreCase: true, out ConsoleKey consoleKey))
        {
            return null;
        }

        return new ConsoleKeyInfo(key.KeyChar, consoleKey, shift: false, alt: true, control: false);
    }

    private static bool ConsumeCsiInput(AppState state)
    {
        if (!TryReadBufferedKey(state, out ConsoleKeyInfo modeKey))
        {
            return true;
        }

        if (modeKey.KeyChar == '<')
        {
            ConsumeSgrMouseInput(state);
            return true;
        }

        if (modeKey.KeyChar == 'M')
        {
            ConsumeX10MouseInput(state);
            return true;
        }

        StringBuilder sequence = new();
        sequence.Append(modeKey.KeyChar);

        if (!IsAnsiFinalByte(modeKey.KeyChar))
        {
            while (TryReadBufferedKey(state, out ConsoleKeyInfo sequenceKey))
            {
                sequence.Append(sequenceKey.KeyChar);
                if (IsAnsiFinalByte(sequenceKey.KeyChar))
                {
                    break;
                }
            }
        }

        HandleCsiKeySequence(state, sequence.ToString());
        return true;
    }

    private static void ConsumeSgrMouseInput(AppState state)
    {
        StringBuilder sequence = new();

        while (TryReadBufferedKey(state, out ConsoleKeyInfo key))
        {
            char character = key.KeyChar;
            if (!char.IsDigit(character) &&
                character is not (';' or 'M' or 'm'))
            {
                return;
            }

            sequence.Append(character);

            if (character is 'M' or 'm')
            {
                HandleSgrMouseSequence(state, sequence.ToString());
                return;
            }
        }
    }

    private static void HandleSgrMouseSequence(AppState state, string sequence)
    {
        if (sequence.Length == 0 ||
            sequence[^1] is not ('M' or 'm'))
        {
            return;
        }

        bool isPress = sequence[^1] == 'M';
        string[] parts = sequence[..^1].Split(';');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], out int buttonCode))
        {
            return;
        }

        int normalizedButtonCode = buttonCode & ~0b1_1100;
        bool isMotion = (buttonCode & 0b10_0000) != 0;
        if (normalizedButtonCode is 64 or 65)
        {
            if (state.ActiveModal is not null &&
                int.TryParse(parts[1], out int modalWheelColumn) &&
                int.TryParse(parts[2], out int modalWheelRow))
            {
                state.ActiveModal.HandleMouse(
                    state,
                    modalWheelColumn,
                    modalWheelRow,
                    buttonCode,
                    isPress);
                return;
            }

            if (state.IsGitSidebarVisible &&
                int.TryParse(parts[1], out int wheelColumn) &&
                wheelColumn <= state.GitSidebarWidth)
            {
                ScrollGitSidebar(state, normalizedButtonCode == 64 ? -MouseWheelScrollLineCount : MouseWheelScrollLineCount);
                return;
            }

            HandleMouseButtonCode(state, buttonCode);
            return;
        }

        if (state.ActiveModal is not null &&
            int.TryParse(parts[1], out int modalColumn) &&
            int.TryParse(parts[2], out int modalRow))
        {
            if (isMotion || (isPress && normalizedButtonCode == 0))
            {
                state.ActiveModal.HandleMouse(
                    state,
                    modalColumn,
                    modalRow,
                    buttonCode,
                    isPress);
            }

            return;
        }

        if (isPress &&
            normalizedButtonCode == 0 &&
            int.TryParse(parts[1], out int column) &&
            int.TryParse(parts[2], out int row))
        {
            if (state.IsGitSidebarVisible &&
                state.GitSidebarContentTopRow > 0 &&
                column <= state.GitSidebarWidth)
            {
                HandleGitSidebarClick(state, row);
                return;
            }

            if (state.HeaderWorkingDirectoryClickRow > 0 && row == state.HeaderWorkingDirectoryClickRow)
            {
                HandleWorkingDirectoryClick(state);
                return;
            }

            if (state.InputPanelHeaderRow > 0 && row == state.InputPanelHeaderRow)
            {
                HandleInputPanelHeaderClick(state, column);
                return;
            }

            bool showFullOutput = (buttonCode & 0b1_1000) != 0;
            HandleConversationClick(state, row, showFullOutput);
        }
    }

    private static void HandleConversationClick(AppState state, int row, bool showFullOutput)
    {
        if (state.ActiveModal is not null ||
            state.IsReaderViewActive ||
            state.IsCopyModeActive ||
            state.MessagesContentTopRow <= 0)
        {
            return;
        }

        int index = row - state.MessagesContentTopRow;
        int?[] visibleIds = state.VisibleThinkingMessageIds;
        if (index < 0 || index >= visibleIds.Length)
        {
            return;
        }

        if (visibleIds[index] is int messageId)
        {
            state.ToggleThinkingMessage(messageId);
            return;
        }

        int?[] toolCallVisibleIds = state.VisibleToolCallMessageIds;
        if (index >= 0 && index < toolCallVisibleIds.Length &&
            toolCallVisibleIds[index] is int toolCallMessageId)
        {
            if (showFullOutput)
            {
                state.ToggleToolCallFullOutput(toolCallMessageId);
            }
            else
            {
                state.ToggleToolCallMessage(toolCallMessageId);
            }
        }
    }

    private static void ConsumeX10MouseInput(AppState state)
    {
        if (!TryReadBufferedKey(state, out ConsoleKeyInfo buttonKey))
        {
            return;
        }

        TryReadBufferedKey(state, out _);
        TryReadBufferedKey(state, out _);

        HandleMouseButtonCode(state, buttonKey.KeyChar - 32);
    }

    private static void ConsumeSs3Input(AppState state)
    {
        if (!TryReadBufferedKey(state, out ConsoleKeyInfo key))
        {
            return;
        }

        HandleTerminalKeySequence(state, key.KeyChar.ToString());
    }

    private static void HandleCsiKeySequence(AppState state, string sequence)
    {
        if (sequence == "200~")
        {
            ConsumeBracketedPasteInput(state);
            return;
        }

        if (TryDispatchModalTerminalKeySequence(state, sequence))
        {
            return;
        }

        if (state.ActiveModal is not null)
        {
            return;
        }

        HandleTerminalKeySequence(state, sequence);
    }

    private static void HandleTerminalKeySequence(AppState state, string sequence)
    {
        if (HandleSelectionTerminalSequence(state, sequence))
        {
            return;
        }

        if (TryHandleGitSidebarTerminalSequence(state, sequence))
        {
            return;
        }

        if (IsMultilineEnterTerminalSequence(sequence))
        {
            AppendInputLineBreak(state);
            return;
        }

        if (TryHandleSlashCommandSuggestionSequence(state, sequence))
        {
            return;
        }

        if (TryHandleInputEditingTerminalSequence(state, sequence))
        {
            return;
        }

        switch (sequence)
        {
            case "A":
                ScrollConversation(state, MouseWheelScrollLineCount);
                return;

            case "B":
                ScrollConversation(state, -MouseWheelScrollLineCount);
                return;

            case "Q":
            case "12~":
                RequestModelSelection(state);
                return;

            case "R":
            case "13~":
                TogglePlanPanel(state);
                return;

            case "S":
            case "14~":
                RemovePendingInputItem(state);
                return;

            case "18~":
                ToggleGitSidebar(state);
                return;

            case "1;5A":
                ScrollGitSidebar(state, -MouseWheelScrollLineCount);
                return;

            case "1;5B":
                ScrollGitSidebar(state, MouseWheelScrollLineCount);
                return;

            case "5;5~":
                ScrollGitSidebar(state, -Math.Max(1, state.GitSidebarViewportHeight - 1));
                return;

            case "6;5~":
                ScrollGitSidebar(state, Math.Max(1, state.GitSidebarViewportHeight - 1));
                return;

            case "5~":
                ScrollConversation(state, Math.Max(1, GetMessageViewportLineCount(state) - 1));
                return;

            case "6~":
                ScrollConversation(state, -Math.Max(1, GetMessageViewportLineCount(state) - 1));
                return;

            case "H":
            case "1~":
                state.ConversationScrollOffset = GetMaxConversationScrollOffset(state);
                state.SyncConversationAutoScrollPreference();
                return;

            case "F":
            case "4~":
                state.JumpConversationToBottom();
                return;
        }
    }

    private static bool TryHandleGitSidebarTerminalSequence(AppState state, string sequence)
    {
        if (!state.IsGitSidebarVisible || state.ActiveModal is not null || state.IsReaderViewActive || state.IsCopyModeActive)
        {
            return false;
        }

        IReadOnlyList<GitSidebarLine> lines = GetGitSidebarLines(state);
        EnsureGitSidebarSelection(state, lines);
        int pageSize = Math.Max(1, state.GitSidebarViewportHeight - 1);

        switch (sequence)
        {
            case "A":
                MoveGitSidebarSelection(state, lines, -1);
                return true;
            case "B":
                MoveGitSidebarSelection(state, lines, 1);
                return true;
            case "H":
            case "1~":
                MoveGitSidebarSelectionToEdge(state, lines, forward: true);
                return true;
            case "F":
            case "4~":
                MoveGitSidebarSelectionToEdge(state, lines, forward: false);
                return true;
            case "5~":
                MoveGitSidebarSelectionByPage(state, lines, -pageSize);
                return true;
            case "6~":
                MoveGitSidebarSelectionByPage(state, lines, pageSize);
                return true;
            default:
                return false;
        }
    }

    private static bool TryHandleInputEditingTerminalSequence(AppState state, string sequence)
    {
        switch (sequence)
        {
            case "D":
                if (state.Input.Length == 0)
                {
                    return false;
                }

                MoveInputCursor(state, -1);
                return true;

            case "C":
                if (state.Input.Length == 0)
                {
                    return false;
                }

                MoveInputCursor(state, 1);
                return true;

            case "H":
            case "1~":
                if (state.Input.Length == 0)
                {
                    return false;
                }

                MoveInputCursorToStart(state);
                return true;

            case "F":
            case "4~":
                if (state.Input.Length == 0)
                {
                    return false;
                }

                MoveInputCursorToEnd(state);
                return true;

            default:
                if (IsDeleteTerminalSequence(sequence))
                {
                    DeleteInputAtCursor(state);
                    return true;
                }

                return false;
        }
    }

    private static bool IsDeleteTerminalSequence(string sequence)
    {
        return sequence == "3~" ||
            (sequence.StartsWith("3;", StringComparison.Ordinal) &&
                sequence.EndsWith('~'));
    }

    private static bool IsMultilineEnterTerminalSequence(string sequence)
    {
        return sequence is "13;2u" or "13;4u" or "13;5u" or "13;6u" or "13;7u" or "13;8u";
    }

    private static void AppendInputLineBreak(AppState state)
    {
        AppendInputLineBreak(state, default);
    }

    private static void AppendInputLineBreak(AppState state, ConsoleKeyInfo key)
    {
        if (state.ActiveModal is null)
        {
            InsertInputText(state, "\n");
        }

        state.SkipNextInputLineFeed = key.KeyChar == '\r';
    }

    private static void AppendInputText(
        AppState state,
        string text,
        bool collapseLargePaste = false)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (state.ActiveModal is TextModalState textModal)
        {
            textModal.AppendText(text);
            state.SkipNextInputLineFeed = false;
            return;
        }

        if (state.ActiveModal is not null)
        {
            return;
        }

        string normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        if (TryAttachFilesFromDroppedOrPastedText(state, normalized))
        {
            state.SkipNextInputLineFeed = false;
            return;
        }

        InsertInputText(
            state,
            normalized,
            collapseLargePaste);
    }

    private static void InsertInputText(
        AppState state,
        string text,
        bool collapseLargePaste = false)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        DeleteSelectedInput(state);

        int cursorIndex = ClampInputCursor(state);
        AdjustCollapsedInputPastesForInsertion(state, cursorIndex, text.Length);
        state.Input.Insert(cursorIndex, text);
        state.InputCursorIndex = cursorIndex + text.Length;

        if (collapseLargePaste &&
            TryGetLargePasteLineCount(text, out int lineCount))
        {
            state.CollapsedInputPastes.Add(new CollapsedInputPaste(
                cursorIndex,
                text.Length,
                lineCount));
        }

        state.SkipNextInputLineFeed = false;
        state.ClearInputSelection();
        ResetSlashCommandSuggestions(state);
    }

    private static void DeleteInputBeforeCursor(AppState state)
    {
        int cursorIndex = ClampInputCursor(state);
        if (cursorIndex <= 0)
        {
            if (state.Input.Length == 0 && state.InputAttachments.Count > 0)
            {
                TryRemoveLastInputAttachment(state);
            }

            state.SkipNextInputLineFeed = false;
            return;
        }

        int deleteIndex = cursorIndex - 1;
        if (TryRemoveCollapsedPasteContainingIndex(state, deleteIndex))
        {
            return;
        }

        AdjustCollapsedInputPastesForDeletion(state, deleteIndex, length: 1);
        state.Input.Remove(deleteIndex, 1);
        state.InputCursorIndex = cursorIndex - 1;
        state.SkipNextInputLineFeed = false;
        ResetSlashCommandSuggestions(state);
    }

    private static void DeleteInputAtCursor(AppState state)
    {
        int cursorIndex = ClampInputCursor(state);
        if (cursorIndex >= state.Input.Length)
        {
            state.SkipNextInputLineFeed = false;
            return;
        }

        if (TryRemoveCollapsedPasteContainingIndex(state, cursorIndex))
        {
            return;
        }

        AdjustCollapsedInputPastesForDeletion(state, cursorIndex, length: 1);
        state.Input.Remove(cursorIndex, 1);
        state.SkipNextInputLineFeed = false;
        ResetSlashCommandSuggestions(state);
    }

    private static void MoveInputCursor(AppState state, int delta)
    {
        int cursorIndex = ClampInputCursor(state);
        int nextIndex = Math.Clamp(
            cursorIndex + delta,
            0,
            state.Input.Length);

        state.InputCursorIndex = MoveInputCursorAroundCollapsedPastes(
            state,
            cursorIndex,
            nextIndex,
            delta);
        state.ClearInputSelection();
        state.SkipNextInputLineFeed = false;
    }

    private static int MoveInputCursorAroundCollapsedPastes(
        AppState state,
        int cursorIndex,
        int nextIndex,
        int delta)
    {
        if (delta < 0)
        {
            foreach (CollapsedInputPaste paste in state.CollapsedInputPastes
                .OrderByDescending(static paste => paste.StartIndex))
            {
                int startIndex = Math.Clamp(paste.StartIndex, 0, state.Input.Length);
                int endIndex = Math.Clamp(paste.EndIndex, startIndex, state.Input.Length);
                if (startIndex >= endIndex)
                {
                    continue;
                }

                if (cursorIndex > startIndex && cursorIndex <= endIndex)
                {
                    return startIndex;
                }
            }
        }
        else if (delta > 0)
        {
            foreach (CollapsedInputPaste paste in state.CollapsedInputPastes
                .OrderBy(static paste => paste.StartIndex))
            {
                int startIndex = Math.Clamp(paste.StartIndex, 0, state.Input.Length);
                int endIndex = Math.Clamp(paste.EndIndex, startIndex, state.Input.Length);
                if (startIndex >= endIndex)
                {
                    continue;
                }

                if (cursorIndex >= startIndex && cursorIndex < endIndex)
                {
                    return endIndex;
                }
            }
        }

        return nextIndex;
    }

    private static void MoveInputCursorToStart(AppState state)
    {
        state.InputCursorIndex = 0;
        state.ClearInputSelection();
        state.SkipNextInputLineFeed = false;
    }

    private static void MoveInputCursorToEnd(AppState state)
    {
        state.InputCursorIndex = state.Input.Length;
        state.ClearInputSelection();
        state.SkipNextInputLineFeed = false;
    }

    private static int ClampInputCursor(AppState state)
    {
        state.InputCursorIndex = Math.Clamp(
            state.InputCursorIndex,
            0,
            state.Input.Length);
        return state.InputCursorIndex;
    }

    private static void SelectAllInput(AppState state)
    {
        if (state.Input.Length == 0)
        {
            return;
        }

        state.InputSelectionAnchor = 0;
        state.InputCursorIndex = state.Input.Length;
        state.SkipNextInputLineFeed = false;
    }

    private static void CollapseInputSelection(AppState state, bool collapseToStart)
    {
        if (!state.TryGetInputSelectionRange(out int startIndex, out int length))
        {
            return;
        }

        state.InputCursorIndex = collapseToStart
            ? startIndex
            : startIndex + length;
        state.ClearInputSelection();
        state.SkipNextInputLineFeed = false;
    }

    private static bool DeleteSelectedInput(AppState state)
    {
        if (!state.TryGetInputSelectionRange(out int startIndex, out int length))
        {
            return false;
        }

        AdjustCollapsedInputPastesForDeletion(state, startIndex, length);
        state.Input.Remove(startIndex, length);
        state.InputCursorIndex = startIndex;
        state.ClearInputSelection();
        state.SkipNextInputLineFeed = false;
        ResetSlashCommandSuggestions(state);
        return true;
    }

    private static void TrackInputInsertedInBatch(
        ref bool insertedInputInBatch,
        ref int inputBatchStartIndex,
        ref int inputBatchEndIndex,
        int startIndex,
        int endIndex)
    {
        if (endIndex < startIndex)
        {
            return;
        }

        if (!insertedInputInBatch)
        {
            inputBatchStartIndex = startIndex;
            inputBatchEndIndex = endIndex;
            insertedInputInBatch = true;
            return;
        }

        inputBatchStartIndex = Math.Min(inputBatchStartIndex, startIndex);
        inputBatchEndIndex = Math.Max(inputBatchEndIndex, endIndex);
    }

    private static bool TryGetLargePasteLineCount(string text, out int lineCount)
    {
        lineCount = GetInputLogicalLineCount(text);
        return lineCount > MultilinePastePreviewLineThreshold;
    }

    private static bool TryAddCollapsedInputPaste(
        AppState state,
        int startIndex,
        int length)
    {
        if (length <= 0 ||
            startIndex < 0 ||
            startIndex + length > state.Input.Length)
        {
            return false;
        }

        string text = state.Input.ToString(startIndex, length);
        if (!TryGetLargePasteLineCount(text, out int lineCount))
        {
            return false;
        }

        int endIndex = startIndex + length;
        bool overlapsExistingPaste = state.CollapsedInputPastes.Any(
            paste => startIndex < paste.EndIndex && endIndex > paste.StartIndex);
        if (overlapsExistingPaste)
        {
            return false;
        }

        state.CollapsedInputPastes.Add(new CollapsedInputPaste(
            startIndex,
            length,
            lineCount));
        return true;
    }

    private static void RemovePendingInputItem(AppState state)
    {
        if (TryRequestInputAttachmentRemoval(state))
        {
            return;
        }

        if (TryRemoveCollapsedPasteNearCursor(state) ||
            TryClearLargePastedInput(state))
        {
            state.SkipNextInputLineFeed = false;
            return;
        }

        if (TryRemoveLastPendingSubmission(state))
        {
            state.SkipNextInputLineFeed = false;
        }
    }

    private static bool TryRequestInputAttachmentRemoval(AppState state)
    {
        if (state.ActiveModal is not null ||
            state.InputAttachments.Count == 0)
        {
            return false;
        }

        SelectionPromptOption<int>[] options = state.InputAttachments
            .Select((attachment, index) => new SelectionPromptOption<int>(
                attachment.Name,
                index,
                attachment.MediaType))
            .ToArray();

        state.ActiveModal = SelectionModalState<int>.Create(
            new SelectionPromptRequest<int>(
                "Remove attached file",
                options,
                "Choose an attached file to remove from this input.",
                DefaultIndex: Math.Max(0, options.Length - 1),
                AllowCancellation: true),
            completionToken: new object(),
            onSelected: index =>
            {
                if (index < 0 || index >= state.InputAttachments.Count)
                {
                    return;
                }

                string name = state.InputAttachments[index].Name;
                state.InputAttachments.RemoveAt(index);
                state.SkipNextInputLineFeed = false;
                ResetSlashCommandSuggestions(state);
                state.AddSystemMessage($"Removed attached file: {name}");
            });

        state.SkipNextInputLineFeed = false;
        return true;
    }

    private static bool TryRemoveCollapsedPasteContainingIndex(
        AppState state,
        int inputIndex)
    {
        CollapsedInputPaste? paste = state.CollapsedInputPastes
            .Where(paste => paste.Length > 0 &&
                inputIndex >= paste.StartIndex &&
                inputIndex < paste.EndIndex)
            .OrderByDescending(paste => paste.StartIndex)
            .FirstOrDefault();

        return TryRemoveCollapsedPaste(state, paste);
    }

    private static bool TryRemoveCollapsedPasteNearCursor(AppState state)
    {
        int cursorIndex = ClampInputCursor(state);
        CollapsedInputPaste? paste = state.CollapsedInputPastes
            .Where(paste => paste.Length > 0 &&
                cursorIndex >= paste.StartIndex &&
                cursorIndex <= paste.EndIndex)
            .OrderByDescending(paste => paste.StartIndex)
            .FirstOrDefault();

        paste ??= state.CollapsedInputPastes
            .Where(paste => paste.Length > 0 &&
                paste.EndIndex <= cursorIndex)
            .OrderByDescending(paste => paste.EndIndex)
            .FirstOrDefault();

        paste ??= state.CollapsedInputPastes
            .Where(paste => paste.Length > 0)
            .OrderBy(paste => paste.StartIndex)
            .FirstOrDefault();

        return TryRemoveCollapsedPaste(state, paste);
    }

    private static bool TryRemoveCollapsedPaste(
        AppState state,
        CollapsedInputPaste? paste)
    {
        if (paste is null)
        {
            return false;
        }

        int startIndex = Math.Clamp(paste.StartIndex, 0, state.Input.Length);
        int endIndex = Math.Clamp(paste.EndIndex, startIndex, state.Input.Length);
        int length = endIndex - startIndex;
        state.CollapsedInputPastes.Remove(paste);

        if (length <= 0)
        {
            return false;
        }

        state.Input.Remove(startIndex, length);
        state.InputCursorIndex = startIndex;
        state.SkipNextInputLineFeed = false;
        AdjustCollapsedInputPastesForDeletion(state, startIndex, length);
        ResetSlashCommandSuggestions(state);
        return true;
    }

    private static bool TryClearLargePastedInput(AppState state)
    {
        if (state.Input.Length == 0 ||
            state.CollapsedInputPastes.Count > 0 ||
            !TryGetLargePasteLineCount(state.Input.ToString(), out _))
        {
            return false;
        }

        state.Input.Clear();
        state.InputCursorIndex = 0;
        state.SkipNextInputLineFeed = false;
        ResetSlashCommandSuggestions(state);
        return true;
    }

    private static bool TryRemoveLastInputAttachment(AppState state)
    {
        if (state.InputAttachments.Count == 0)
        {
            return false;
        }

        state.InputAttachments.RemoveAt(state.InputAttachments.Count - 1);
        state.SkipNextInputLineFeed = false;
        ResetSlashCommandSuggestions(state);
        return true;
    }

    private static bool TryRemoveLastPendingSubmission(AppState state)
    {
        if (state.PendingSubmissions.Count == 0)
        {
            return false;
        }

        PendingSubmission[] submissions = state.PendingSubmissions.ToArray();
        PendingSubmission removed = submissions[^1];
        state.PendingSubmissions.Clear();

        for (int index = 0; index < submissions.Length - 1; index++)
        {
            state.PendingSubmissions.Enqueue(submissions[index]);
        }

        state.AddSystemMessage($"Removed queued {DescribePendingSubmission(removed)}.");
        return true;
    }

    private static void AdjustCollapsedInputPastesForInsertion(
        AppState state,
        int insertIndex,
        int length)
    {
        if (length <= 0 ||
            state.CollapsedInputPastes.Count == 0)
        {
            return;
        }

        for (int index = state.CollapsedInputPastes.Count - 1; index >= 0; index--)
        {
            CollapsedInputPaste paste = state.CollapsedInputPastes[index];

            if (insertIndex <= paste.StartIndex)
            {
                paste.StartIndex += length;
            }
            else if (insertIndex < paste.EndIndex)
            {
                state.CollapsedInputPastes.RemoveAt(index);
            }
        }
    }

    private static void AdjustCollapsedInputPastesForDeletion(
        AppState state,
        int deleteIndex,
        int length)
    {
        if (length <= 0 ||
            state.CollapsedInputPastes.Count == 0)
        {
            return;
        }

        int deleteEndIndex = deleteIndex + length;
        for (int index = state.CollapsedInputPastes.Count - 1; index >= 0; index--)
        {
            CollapsedInputPaste paste = state.CollapsedInputPastes[index];

            if (deleteEndIndex <= paste.StartIndex)
            {
                paste.StartIndex -= length;
            }
            else if (deleteIndex < paste.EndIndex &&
                deleteEndIndex > paste.StartIndex)
            {
                state.CollapsedInputPastes.RemoveAt(index);
            }
        }
    }

    private static void PasteFromClipboard(AppState state)
    {
        string? text = TryReadClipboardText();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        AppendInputText(state, text, collapseLargePaste: true);
    }

    private static string? TryReadClipboardText()
    {
        ClipboardReadCommand[] commands = OperatingSystem.IsWindows()
            ? [new ClipboardReadCommand("powershell.exe", ["-NoProfile", "-Command", "Get-Clipboard -Raw"], TrimTrailingNewline: true)]
            : OperatingSystem.IsMacOS()
                ? [new ClipboardReadCommand("pbpaste", [], TrimTrailingNewline: false)]
                : [
                    new ClipboardReadCommand("wl-paste", ["--no-newline"], TrimTrailingNewline: false),
                    new ClipboardReadCommand("xclip", ["-selection", "clipboard", "-o"], TrimTrailingNewline: false),
                    new ClipboardReadCommand("xsel", ["--clipboard", "--output"], TrimTrailingNewline: false)
                ];

        foreach (ClipboardReadCommand command in commands)
        {
            try
            {
                ProcessStartInfo startInfo = new()
                {
                    FileName = command.FileName,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                foreach (string argument in command.Arguments)
                {
                    startInfo.ArgumentList.Add(argument);
                }

                using Process? process = Process.Start(startInfo);
                if (process is null)
                {
                    continue;
                }

                Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                Task<string> errorTask = process.StandardError.ReadToEndAsync();

                if (!TryWaitForProcessExit(process, ClipboardReadTimeoutMilliseconds))
                {
                    continue;
                }

                string output = outputTask.GetAwaiter().GetResult();
                _ = errorTask.GetAwaiter().GetResult();

                if (process.ExitCode != 0 || output.Length == 0)
                {
                    continue;
                }

                return command.TrimTrailingNewline
                    ? TrimSingleTrailingNewline(output)
                    : output;
            }
            catch (Win32Exception)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        return null;
    }

    private static string TrimSingleTrailingNewline(string text)
    {
        if (text.EndsWith("\r\n", StringComparison.Ordinal))
        {
            return text[..^2];
        }

        if (text.Length > 0 && text[^1] is '\n' or '\r')
        {
            return text[..^1];
        }

        return text;
    }

    private sealed record ClipboardReadCommand(
        string FileName,
        IReadOnlyList<string> Arguments,
        bool TrimTrailingNewline);

    private static bool TryWriteClipboardText(string text)
    {
        ClipboardWriteCommand[] commands = OperatingSystem.IsWindows()
            ? [new ClipboardWriteCommand("powershell.exe", ["-NoProfile", "-Command", "$input | Set-Clipboard"])]
            : OperatingSystem.IsMacOS()
                ? [new ClipboardWriteCommand("pbcopy", [])]
                : [
                    new ClipboardWriteCommand("wl-copy", []),
                    new ClipboardWriteCommand("xclip", ["-selection", "clipboard"]),
                    new ClipboardWriteCommand("xsel", ["--clipboard", "--input"])
                ];

        foreach (ClipboardWriteCommand command in commands)
        {
            try
            {
                ProcessStartInfo startInfo = new()
                {
                    FileName = command.FileName,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                };

                foreach (string argument in command.Arguments)
                {
                    startInfo.ArgumentList.Add(argument);
                }

                using Process? process = Process.Start(startInfo);
                if (process is null)
                {
                    continue;
                }

                process.StandardInput.Write(text);
                process.StandardInput.Close();

                Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                Task<string> errorTask = process.StandardError.ReadToEndAsync();

                if (!TryWaitForProcessExit(process, ClipboardReadTimeoutMilliseconds))
                {
                    continue;
                }

                _ = outputTask.GetAwaiter().GetResult();
                _ = errorTask.GetAwaiter().GetResult();

                if (process.ExitCode == 0)
                {
                    return true;
                }
            }
            catch (Win32Exception)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (IOException)
            {
            }
        }

        return false;
    }

    private sealed record ClipboardWriteCommand(
        string FileName,
        IReadOnlyList<string> Arguments);

    private static void ConsumeBracketedPasteInput(AppState state)
    {
        StringBuilder pastedText = new();

        while (TryReadBufferedKey(state, out ConsoleKeyInfo key))
        {
            if (key.KeyChar == '\u001b' &&
                TryConsumeBracketedPasteTerminator(state, pastedText))
            {
                AppendInputText(
                    state,
                    pastedText.ToString(),
                    collapseLargePaste: true);
                return;
            }

            pastedText.Append(key.KeyChar);
        }

        AppendInputText(
            state,
            pastedText.ToString(),
            collapseLargePaste: true);
    }

    private static bool TryConsumeBracketedPasteTerminator(
        AppState state,
        StringBuilder pastedText)
    {
        if (!TryReadBufferedKey(state, out ConsoleKeyInfo prefixKey))
        {
            pastedText.Append('\u001b');
            return false;
        }

        if (prefixKey.KeyChar != '[')
        {
            pastedText.Append('\u001b');
            pastedText.Append(prefixKey.KeyChar);
            return false;
        }

        StringBuilder sequence = new();
        while (TryReadBufferedKey(state, out ConsoleKeyInfo sequenceKey))
        {
            sequence.Append(sequenceKey.KeyChar);
            if (IsAnsiFinalByte(sequenceKey.KeyChar))
            {
                break;
            }
        }

        if (sequence.ToString() == "201~")
        {
            return true;
        }

        pastedText.Append('\u001b');
        pastedText.Append('[');
        pastedText.Append(sequence);
        return false;
    }

    private static bool TryDispatchModalTerminalKeySequence(AppState state, string sequence)
    {
        if (state.ActiveModal is null)
        {
            return false;
        }

        ConsoleKey? key = IsDeleteTerminalSequence(sequence)
            ? ConsoleKey.Delete
            : sequence switch
            {
                "A" => ConsoleKey.UpArrow,
                "B" => ConsoleKey.DownArrow,
                "C" => ConsoleKey.RightArrow,
                "D" => ConsoleKey.LeftArrow,
                "H" or "1~" => ConsoleKey.Home,
                "F" or "4~" => ConsoleKey.End,
                "5~" => ConsoleKey.PageUp,
                "6~" => ConsoleKey.PageDown,
                _ => null
            };

        if (key is null)
        {
            return false;
        }

        state.ActiveModal.HandleKey(
            state,
            new ConsoleKeyInfo('\0', key.Value, false, false, false));
        return true;
    }

    private static bool TryReadBufferedKey(AppState state, out ConsoleKeyInfo key)
    {
        if (state.PendingInputKeys.Count > 0)
        {
            key = state.PendingInputKeys.Dequeue();
            return true;
        }

        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMilliseconds(TerminalSequenceReadTimeoutMilliseconds);

        while (!HasPendingOrBufferedInput(state))
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                key = default;
                return false;
            }

            Thread.Sleep(1);
        }

        if (state.PendingInputKeys.Count > 0)
        {
            key = state.PendingInputKeys.Dequeue();
            return true;
        }

        key = Console.ReadKey(intercept: true);
        return true;
    }

    private static bool TryReadNextInputKey(AppState state, out ConsoleKeyInfo key)
    {
        if (state.PendingInputKeys.Count > 0)
        {
            key = state.PendingInputKeys.Dequeue();
            return true;
        }

        if (!Console.KeyAvailable)
        {
            key = default;
            return false;
        }

        key = Console.ReadKey(intercept: true);
        return true;
    }

    private static bool HasPendingOrBufferedInput(AppState state)
    {
        return state.PendingInputKeys.Count > 0 || Console.KeyAvailable;
    }

    private static bool TryWaitForProcessExit(Process process, int timeoutMilliseconds)
    {
        if (process.WaitForExit(timeoutMilliseconds))
        {
            return true;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            process.WaitForExit();
        }
        catch (InvalidOperationException)
        {
        }

        return false;
    }

    private static bool HasBufferedInputAfterDelay(int timeoutMilliseconds)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMilliseconds);

        while (!Console.KeyAvailable)
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                return false;
            }

            Thread.Sleep(1);
        }

        return true;
    }

    private static bool IsAnsiFinalByte(char character)
    {
        return character is >= '@' and <= '~';
    }

    private static void HandleMouseButtonCode(AppState state, int buttonCode)
    {
        int normalizedButtonCode = buttonCode & ~0b1_1100;

        if (normalizedButtonCode == 64)
        {
            ScrollConversation(state, MouseWheelScrollLineCount);
        }
        else if (normalizedButtonCode == 65)
        {
            ScrollConversation(state, -MouseWheelScrollLineCount);
        }
    }

    private static void HandleWorkingDirectoryClick(AppState state)
    {
        if (state.ActiveModal is not null ||
            state.IsReaderViewActive ||
            state.IsCopyModeActive)
        {
            return;
        }

        string rootDirectory = state.RootDirectory ?? Directory.GetCurrentDirectory();
        string displayPath = TruncateFromRight(rootDirectory, 60);

        state.ActiveModal = SelectionModalState<string>.Create(
            new SelectionPromptRequest<string>(
                "Working directory actions",
                [
                    new SelectionPromptOption<string>(
                        "Open in Explorer",
                        "explorer",
                        $"Open {displayPath} in the system file manager."),
                    new SelectionPromptOption<string>(
                        "Start Terminal",
                        "terminal",
                        $"Open a new terminal window in {displayPath}.")
                ],
                $"Choose an action for [underline aqua]{Markup.Escape(rootDirectory)}[/]. Enter confirms, Esc cancels.",
                DefaultIndex: 0,
                AllowCancellation: true,
                DescriptionSupportsMarkup: true),
            completionToken: new object(),
            onSelected: action =>
            {
                switch (action)
                {
                    case "explorer":
                        OpenDirectoryInExplorer(state);
                        break;
                    case "terminal":
                        OpenTerminalInDirectory(state);
                        break;
                }
            },
            onCancelled: _ => state.AddSystemMessage("Working directory action cancelled."));
    }

    private static void HandleInputPanelHeaderClick(AppState state, int column)
    {
        if (state.ActiveModal is not null ||
            state.IsReaderViewActive ||
            state.IsCopyModeActive)
        {
            return;
        }

        if (IsWithinClickZone(column, state.InputProfileClickStartColumn, state.InputProfileClickEndColumn))
        {
            HandleCommand(state, "/setting profile");
            return;
        }

        if (IsWithinClickZone(column, state.InputModelClickStartColumn, state.InputModelClickEndColumn))
        {
            RequestModelSelection(state);
            return;
        }

        if (IsWithinClickZone(column, state.InputReasoningClickStartColumn, state.InputReasoningClickEndColumn))
        {
            HandleCommand(state, "/reasoning");
            return;
        }

        if (IsWithinClickZone(column, state.InputProviderClickStartColumn, state.InputProviderClickEndColumn))
        {
            HandleCommand(state, "/setting provider");
            return;
        }

        if (IsWithinClickZone(column, state.InputThinkingClickStartColumn, state.InputThinkingClickEndColumn))
        {
            OpenThinkingModeSelection(state);
            return;
        }
    }

    private static bool IsWithinClickZone(int column, int startColumn, int endColumn)
    {
        return startColumn > 0 &&
            endColumn >= startColumn &&
            column >= startColumn &&
            column <= endColumn;
    }

    private static void OpenThinkingModeSelection(AppState state)
    {
        if (state.ActiveModal is not null ||
            state.IsReaderViewActive ||
            state.IsCopyModeActive)
        {
            return;
        }

        string currentMode = ThinkingModeOptions.Format(state.ThinkingMode);
        bool isCurrentlyOn = string.Equals(state.ThinkingMode, ThinkingModeOptions.On, StringComparison.OrdinalIgnoreCase);

        state.ActiveModal = SelectionModalState<string>.Create(
            new SelectionPromptRequest<string>(
                "Thinking Mode",
                [
                    new SelectionPromptOption<string>(
                        "On",
                        "on",
                        "Enable thinking. Currently: " + currentMode + "."),
                    new SelectionPromptOption<string>(
                        "Off",
                        "off",
                        "Disable thinking. Currently: " + currentMode + ".")
                ],
                "Choose thinking mode. Enter confirms, Esc cancels.",
                DefaultIndex: isCurrentlyOn ? 0 : 1,
                AllowCancellation: true,
                DescriptionSupportsMarkup: true),
            completionToken: new object(),
            onSelected: mode =>
            {
                if (string.Equals(mode, ThinkingModeOptions.On, StringComparison.OrdinalIgnoreCase))
                {
                    HandleCommand(state, "/reasoning");
                }
                else
                {
                    HandleCommand(state, "/reasoning none");
                }
            },
            onCancelled: _ => state.AddSystemMessage("Thinking mode selection cancelled."));
    }

    private static void OpenDirectoryInExplorer(AppState state)
    {
        string directory = state.RootDirectory ?? Directory.GetCurrentDirectory();
        bool opened = false;

        if (OperatingSystem.IsWindows())
        {
            opened = TryStartProcess("explorer.exe", $"\"{directory}\"", useShell: false);
        }
        else if (OperatingSystem.IsMacOS())
        {
            opened = TryStartProcess("open", $"\"{directory}\"", useShell: false);
        }
        else
        {
            opened = TryStartProcess("xdg-open", $"\"{directory}\"", useShell: false);
        }

        if (opened)
        {
            state.AddSystemMessage($"Opened {Markup.Escape(directory)} in the file manager.");
        }
        else
        {
            state.AddSystemMessage($"Could not open {Markup.Escape(directory)}: no suitable file manager was found.");
        }
    }

    private static void OpenTerminalInDirectory(AppState state)
    {
        string directory = state.RootDirectory ?? Directory.GetCurrentDirectory();
        bool opened = false;

        if (OperatingSystem.IsWindows())
        {
            opened = TryStartProcess("wt.exe", $"-d \"{directory}\"", useShell: false) ||
                     TryStartProcess("cmd.exe", $"/K cd /d \"{directory}\"", useShell: false);
        }
        else if (OperatingSystem.IsMacOS())
        {
            opened = TryStartProcess("open", $"-a Terminal \"{directory}\"", useShell: false);
        }
        else
        {
            opened = TryStartProcess("x-terminal-emulator", $"--working-directory=\"{directory}\"", useShell: false) ||
                     TryStartProcess("gnome-terminal", $"--working-directory=\"{directory}\"", useShell: false) ||
                     TryStartProcess("xterm", $"-e \"cd {directory} && exec bash\"", useShell: false);
        }

        if (opened)
        {
            state.AddSystemMessage($"Opened a terminal at {Markup.Escape(directory)}.");
        }
        else
        {
            state.AddSystemMessage($"Could not open a terminal at {Markup.Escape(directory)}: no suitable terminal was found.");
        }
    }
}
