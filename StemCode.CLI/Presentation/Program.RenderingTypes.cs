namespace StemCode.CLI;

internal readonly record struct ReaderViewLine(
    string Markup,
    string Plain,
    string? SelectionKey = null);

public static partial class Program
{
    // Markup: Spectre markup for on-screen rendering (includes role prefix + styling).
    // Plain: the markup-stripped on-screen text (still includes role prefix / gutters).
    // Copy: the clean underlying text for copy mode / clipboard, with no role prefix,
    //       code-block gutter, or scrollbar chrome. Defaults to empty for spacer lines.
    // ThinkingMessageId: when set, this line belongs to a thinking block and a mouse click
    // on it toggles that block's collapsed/expanded state.
    private readonly record struct ConversationLine(
    string Markup,
    string Plain,
    string Copy = "",
    int? ThinkingMessageId = null,
    int? ToolCallMessageId = null);

    private readonly record struct InlineRenderResult(
        string Markup,
        string Plain);

    private readonly record struct InputRenderLine(
        string Text,
        int? CursorColumn,
        int? SelectionStartColumn = null,
        int? SelectionEndColumn = null);

    private readonly record struct InputDisplayText(
        string Text,
        int CursorIndex,
        bool HasCollapsedPastes,
        int? SelectionAnchorIndex = null);

    private readonly record struct MarkdownFragment(
        string Text,
        string Style);

    private readonly record struct SlashCommandSuggestion(
        string Command,
        string Usage,
        string Description,
        bool RequiresArgument,
        SlashCommandSuggestionKind Kind = SlashCommandSuggestionKind.Command,
        string? CompletedInput = null,
        bool SubmitOnEnter = false);

    private enum SlashCommandSuggestionKind
    {
        Command,
        FilePath
    }
}
