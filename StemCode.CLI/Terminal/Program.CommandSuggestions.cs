using StemCode.Application.Commands;
using Spectre.Console;

namespace StemCode.CLI;

public static partial class Program
{
    private static bool TryGetSlashCommandSuggestions(
        AppState state,
        out IReadOnlyList<SlashCommandSuggestion> suggestions)
    {
        suggestions = [];

        if (state.ActiveModal is not null ||
            state.SlashCommandSuggestionsDismissed)
        {
            return false;
        }

        string fullInput = state.Input.ToString();
        // Suggestions are derived from the text before the cursor so completion
        // works while editing anywhere in the input, not only when the caret sits
        // at the very end.
        string input = fullInput[..Math.Clamp(state.InputCursorIndex, 0, fullInput.Length)];
        bool isCommandSuggestionInput = IsSlashCommandSuggestionInput(state.RootDirectory, input);
        if (!isCommandSuggestionInput &&
            FilePathSuggestionProvider.GetSuggestions(
                state.RootDirectory,
                input,
                MaxSlashCommandSuggestionCount).Count == 0)
        {
            return false;
        }

        suggestions = isCommandSuggestionInput
            ? GetSlashCommandSuggestions(state.RootDirectory)
                .Where(suggestion => suggestion.Command.StartsWith(input, StringComparison.OrdinalIgnoreCase))
                .ToArray()
            : GetFilePathSuggestions(state, input);

        if (suggestions.Count == 0)
        {
            state.SlashCommandSuggestionIndex = 0;
            return false;
        }

        state.SlashCommandSuggestionIndex = Math.Clamp(
            state.SlashCommandSuggestionIndex,
            0,
            suggestions.Count - 1);
        return true;
    }

    private static bool IsSlashCommandSuggestionInput(string rootDirectory, string input)
    {
        if (string.IsNullOrEmpty(input) ||
            !input.StartsWith("/", StringComparison.Ordinal) ||
            input.Any(char.IsWhiteSpace))
        {
            return false;
        }

        return input.Length == 1 ||
            GetSlashCommandSuggestions(rootDirectory).Any(
                suggestion => suggestion.Command.StartsWith(input, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryHandleSlashCommandSuggestionInput(
        AppState state,
        ConsoleKeyInfo key)
    {
        if (!TryGetSlashCommandSuggestions(state, out IReadOnlyList<SlashCommandSuggestion> suggestions))
        {
            return false;
        }

        // When the caret is in the middle of the input, suggestions act as a passive
        // hint: Tab completes the token under the caret, but arrow/Home/End keys fall
        // through to normal text editing so the user can move around freely.
        bool cursorAtEnd = state.InputCursorIndex == state.Input.Length;

        if (IsEnterKey(key))
        {
            if (!cursorAtEnd)
            {
                return false;
            }

            AcceptSlashCommandSuggestion(state, suggestions, submitCommand: true);
            return true;
        }

        if (key.Key == ConsoleKey.Tab)
        {
            AcceptSlashCommandSuggestion(state, suggestions, submitCommand: false);
            return true;
        }

        if (!cursorAtEnd)
        {
            return false;
        }

        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
            case ConsoleKey.LeftArrow:
                MoveSlashCommandSuggestion(state, suggestions, -1);
                return true;

            case ConsoleKey.DownArrow:
            case ConsoleKey.RightArrow:
                if (key.Key == ConsoleKey.RightArrow)
                {
                    AcceptSlashCommandSuggestion(state, suggestions, submitCommand: false);
                }
                else
                {
                    MoveSlashCommandSuggestion(state, suggestions, 1);
                }

                return true;

            case ConsoleKey.PageUp:
                MoveSlashCommandSuggestion(state, suggestions, -MaxSlashCommandSuggestionCount);
                return true;

            case ConsoleKey.PageDown:
                MoveSlashCommandSuggestion(state, suggestions, MaxSlashCommandSuggestionCount);
                return true;

            case ConsoleKey.Home:
                state.SlashCommandSuggestionIndex = 0;
                return true;

            case ConsoleKey.End:
                state.SlashCommandSuggestionIndex = suggestions.Count - 1;
                return true;

            case ConsoleKey.Tab:
                AcceptSlashCommandSuggestion(state, suggestions, submitCommand: false);
                return true;

            default:
                return false;
        }
    }

    private static bool TryDismissSlashCommandSuggestions(AppState state)
    {
        if (!TryGetSlashCommandSuggestions(state, out _))
        {
            return false;
        }

        state.SlashCommandSuggestionsDismissed = true;
        return true;
    }

    private static bool TryHandleSlashCommandSuggestionSequence(
        AppState state,
        string sequence)
    {
        if (!TryGetSlashCommandSuggestions(state, out IReadOnlyList<SlashCommandSuggestion> suggestions))
        {
            return false;
        }

        // Mid-input arrow sequences must move the caret, not navigate suggestions.
        if (state.InputCursorIndex != state.Input.Length)
        {
            return false;
        }

        switch (sequence)
        {
            case "A":
                MoveSlashCommandSuggestion(state, suggestions, -1);
                return true;

            case "D":
                MoveSlashCommandSuggestion(state, suggestions, -1);
                return true;

            case "B":
                MoveSlashCommandSuggestion(state, suggestions, 1);
                return true;

            case "C":
                AcceptSlashCommandSuggestion(state, suggestions, submitCommand: false);
                return true;

            case "5~":
                MoveSlashCommandSuggestion(state, suggestions, -MaxSlashCommandSuggestionCount);
                return true;

            case "6~":
                MoveSlashCommandSuggestion(state, suggestions, MaxSlashCommandSuggestionCount);
                return true;

            case "H":
            case "1~":
                state.SlashCommandSuggestionIndex = 0;
                return true;

            case "F":
            case "4~":
                state.SlashCommandSuggestionIndex = suggestions.Count - 1;
                return true;

            default:
                return false;
        }
    }

    private static void MoveSlashCommandSuggestion(
        AppState state,
        IReadOnlyList<SlashCommandSuggestion> suggestions,
        int delta)
    {
        if (suggestions.Count == 0)
        {
            state.SlashCommandSuggestionIndex = 0;
            return;
        }

        int nextIndex = state.SlashCommandSuggestionIndex + delta;
        while (nextIndex < 0)
        {
            nextIndex += suggestions.Count;
        }

        state.SlashCommandSuggestionIndex = nextIndex % suggestions.Count;
    }

    private static void AcceptSlashCommandSuggestion(
        AppState state,
        IReadOnlyList<SlashCommandSuggestion> suggestions,
        bool submitCommand)
    {
        if (suggestions.Count == 0)
        {
            return;
        }

        SlashCommandSuggestion suggestion = suggestions[state.SlashCommandSuggestionIndex];
        string completedInput = suggestion.CompletedInput ??
            (suggestion.RequiresArgument
                ? suggestion.Command + " "
                : suggestion.Command);

        (string updatedInput, int updatedCursorIndex) = ApplySuggestionAtCursor(
            state.Input.ToString(),
            state.InputCursorIndex,
            completedInput);

        state.Input.Clear();
        state.CollapsedInputPastes.Clear();
        state.Input.Append(updatedInput);
        state.InputCursorIndex = updatedCursorIndex;
        state.SlashCommandSuggestionsDismissed = true;

        // Submission only happens from the end-of-input position (the historical
        // behaviour); when completing a token in the middle of the text we keep the
        // remaining input editable instead of sending the whole line.
        bool cursorAtEnd = updatedCursorIndex == updatedInput.Length;
        if (submitCommand && cursorAtEnd &&
            (suggestion.SubmitOnEnter || !suggestion.RequiresArgument))
        {
            SubmitInput(state);
        }
    }

    // Replaces the text from the start of the input up to the caret with the
    // completed value, preserving everything after the caret, and returns the
    // resulting input and caret index. Pure and therefore unit-testable.
    internal static (string Input, int CursorIndex) ApplySuggestionAtCursor(
        string originalInput,
        int cursorIndex,
        string completedInput)
    {
        int clampedCursorIndex = Math.Clamp(cursorIndex, 0, originalInput.Length);
        string afterCursor = originalInput[clampedCursorIndex..];
        return (completedInput + afterCursor, completedInput.Length);
    }

    private static void ResetSlashCommandSuggestions(AppState state)
    {
        state.SlashCommandSuggestionIndex = 0;
        state.SlashCommandSuggestionsDismissed = false;
    }

    private static IReadOnlyList<SlashCommandSuggestion> GetVisibleSlashCommandSuggestions(
        AppState state,
        IReadOnlyList<SlashCommandSuggestion> suggestions)
    {
        if (suggestions.Count <= MaxSlashCommandSuggestionCount)
        {
            return suggestions;
        }

        int startIndex = Math.Clamp(
            state.SlashCommandSuggestionIndex - (MaxSlashCommandSuggestionCount / 2),
            0,
            suggestions.Count - MaxSlashCommandSuggestionCount);

        return suggestions
            .Skip(startIndex)
            .Take(MaxSlashCommandSuggestionCount)
            .ToArray();
    }

    private static string BuildSlashCommandSuggestionsMarkup(
        AppState state,
        IReadOnlyList<SlashCommandSuggestion> suggestions)
    {
        int contentWidth = GetInputContentWidth(state);
        IReadOnlyList<SlashCommandSuggestion> visibleSuggestions = GetVisibleSlashCommandSuggestions(
            state,
            suggestions);
        int firstVisibleIndex = GetSlashCommandSuggestionIndex(
            suggestions,
            visibleSuggestions[0]);
        string matchedInput = state.Input.ToString()
            [..Math.Clamp(state.InputCursorIndex, 0, state.Input.Length)];
        List<string> lines =
        [
            suggestions[0].Kind == SlashCommandSuggestionKind.FilePath
                ? $"[grey]Files matching [/][green]{Markup.Escape(matchedInput)}[/][grey]:[/]"
                : $"[grey]Commands matching [/][green]{Markup.Escape(matchedInput)}[/][grey]:[/]"
        ];

        for (int visibleIndex = 0; visibleIndex < visibleSuggestions.Count; visibleIndex++)
        {
            int suggestionIndex = firstVisibleIndex + visibleIndex;
            SlashCommandSuggestion suggestion = visibleSuggestions[visibleIndex];
            bool selected = suggestionIndex == state.SlashCommandSuggestionIndex;
            string prefix = selected ? "❯ " : "  ";
            string usageText = TruncateFromRight(prefix + suggestion.Usage, Math.Min(34, contentWidth));
            int descriptionWidth = Math.Max(0, contentWidth - usageText.Length - 3);
            string description = descriptionWidth == 0
                ? string.Empty
                : TruncateFromRight(suggestion.Description, descriptionWidth);
            string plainLine = description.Length == 0
                ? usageText
                : $"{usageText} - {description}";

            lines.Add(selected
                ? $"[#163A42 on #7FE7F2]{Markup.Escape(plainLine)}[/]"
                : $"[#B8C2CC]{Markup.Escape(usageText)}[/][grey]{Markup.Escape(description.Length == 0 ? string.Empty : " - " + description)}[/]");
        }

        if (suggestions.Count > MaxSlashCommandSuggestionCount)
        {
            lines.Add($"[#68758A]{suggestions.Count} matches. Keep typing to narrow.[/]");
        }

        return string.Join('\n', lines);
    }

    private static int GetSlashCommandSuggestionLineCount(
        IReadOnlyList<SlashCommandSuggestion> suggestions)
    {
        if (suggestions.Count == 0)
        {
            return 0;
        }

        int count = 1 + Math.Min(suggestions.Count, MaxSlashCommandSuggestionCount);
        return suggestions.Count > MaxSlashCommandSuggestionCount
            ? count + 1
            : count;
    }

    private static int GetSlashCommandSuggestionIndex(
        IReadOnlyList<SlashCommandSuggestion> suggestions,
        SlashCommandSuggestion suggestion)
    {
        for (int index = 0; index < suggestions.Count; index++)
        {
            if (string.Equals(suggestions[index].Command, suggestion.Command, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return 0;
    }

    private static SlashCommandSuggestion[] GetFilePathSuggestions(
        AppState state,
        string input)
    {
        // Shell commands (!/!!) often take further arguments after a path, so completing a
        // file should not immediately submit the command the way /read or /import do.
        bool isShellCommand = input.StartsWith('!');
        // Plain path input (./, ../, ~/ typed at the start of a chat message) behaves like a
        // shell argument: selecting a file should complete the token without submitting.
        bool isPlainPathInput = !isShellCommand &&
            (input.StartsWith("./", StringComparison.Ordinal) ||
                input.StartsWith("../", StringComparison.Ordinal) ||
                input.StartsWith("~", StringComparison.Ordinal));
        return FilePathSuggestionProvider
            .GetSuggestions(
                state.RootDirectory,
                input,
                MaxSlashCommandSuggestionCount)
            .Select(suggestion => new SlashCommandSuggestion(
                suggestion.CompletedInput,
                suggestion.DisplayPath,
                suggestion.Description,
                suggestion.IsDirectory,
                SlashCommandSuggestionKind.FilePath,
                suggestion.CompletedInput,
                SubmitOnEnter: !suggestion.IsDirectory && !isShellCommand && !isPlainPathInput))
            .ToArray();
    }

    private static SlashCommandSuggestion[] GetSlashCommandSuggestions(string rootDirectory)
    {
        SlashCommandSuggestion[] builtInSuggestions = ReplCommandCatalog.All
            .Select(static metadata => new SlashCommandSuggestion(
                "/" + metadata.CommandName,
                metadata.Usage,
                metadata.Description,
                metadata.RequiresArgument))
            .Concat(
            [
                new SlashCommandSuggestion("/clear", "/clear", "Clear the terminal conversation view.", false),
                new SlashCommandSuggestion("/voice", "/voice", "Start voice dictation (Ctrl+R).", false),
                new SlashCommandSuggestion("/voice setup", "/voice setup", "Configure the voice model and microphone.", true),
                new SlashCommandSuggestion("/voice update", "/voice update", "Update the local voice models.", false),
                new SlashCommandSuggestion("/ls", "/ls", "List files in the current workspace.", false),
                new SlashCommandSuggestion("/read", "/read <file>", "Read a workspace file after confirmation.", true),
                new SlashCommandSuggestion("/trajectory", "/trajectory", "Open the current session trajectory event stream.", false)
            ])
            .ToArray();

        SlashCommandSuggestion[] customSuggestions = CustomSlashCommandService
            .List(rootDirectory)
            .Select(static suggestion => new SlashCommandSuggestion(
                suggestion.Command,
                suggestion.Usage,
                suggestion.Description,
                suggestion.RequiresArgument))
            .ToArray();

        return builtInSuggestions
            .Concat(customSuggestions)
            .OrderBy(static suggestion => suggestion.Command, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string TruncateFromRight(string value, int maxLength)
    {
        if (maxLength <= 0)
        {
            return string.Empty;
        }

        if (value.Length <= maxLength)
        {
            return value;
        }

        return maxLength <= 3
            ? value[..maxLength]
            : value[..(maxLength - 3)] + "...";
    }
}
