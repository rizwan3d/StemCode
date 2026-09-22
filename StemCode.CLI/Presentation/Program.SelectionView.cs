using Spectre.Console;
using Spectre.Console.Rendering;

namespace StemCode.CLI;

public static partial class Program
{
    private const string CopySelectionBackground = "grey30";
    private const string CopyCursorBackground = "grey42";

    // ---- Reader view (chrome-free, natively selectable transcript) ----

    // Renders the whole screen as a plain, border-free transcript so the terminal's
    // own mouse selection grabs clean text (no panels, padding, gutters or scrollbar).
    private static IRenderable BuildReaderView(AppState state)
    {
        int width = Math.Max(20, GetWindowWidth() - 1);
        List<ReaderViewLine> readerLines = BuildReaderDisplayLines(state, width);
        int viewportLineCount = GetReaderViewportLineCount();
        int maxScrollOffset = Math.Max(0, readerLines.Count - viewportLineCount);
        state.ReaderScrollOffset = Math.Clamp(state.ReaderScrollOffset, 0, maxScrollOffset);

        int startLine = state.ReaderScrollOffset;
        int endLine = Math.Min(readerLines.Count, startLine + viewportLineCount);

        List<string> rendered =
        [
            BuildReaderHeaderMarkup(state, startLine, endLine, readerLines.Count),
            string.Empty
        ];

        for (int index = startLine; index < endLine; index++)
        {
            ReaderViewLine line = readerLines[index];
            rendered.Add(IsSelectedReaderLine(state, readerLines, index)
                ? $"[black on grey85]{Markup.Escape(line.Plain)}[/]"
                : line.Markup);
        }

        return new Markup(string.Join('\n', rendered));
    }

    private static bool IsSelectedReaderLine(
        AppState state,
        IReadOnlyList<ReaderViewLine> lines,
        int lineIndex)
    {
        if (lines[lineIndex].SelectionKey is null)
        {
            return false;
        }

        int selectableIndex = 0;
        for (int index = 0; index <= lineIndex; index++)
        {
            if (lines[index].SelectionKey is null)
            {
                continue;
            }

            if (index == lineIndex)
            {
                return selectableIndex == state.ReaderSelectedSelectableIndex;
            }

            selectableIndex++;
        }

        return false;
    }

    private static string BuildReaderHeaderMarkup(AppState state, int startLine, int endLine, int totalLines)
    {
        string range = totalLines == 0
            ? "0/0"
            : $"{startLine + 1}-{endLine}/{totalLines}";

        string title = string.IsNullOrWhiteSpace(state.ReaderViewTitle)
            ? "READER VIEW"
            : state.ReaderViewTitle!;
        string instructions = string.IsNullOrWhiteSpace(state.ReaderViewInstructions)
            ? "select with the mouse to copy | Up/Down PgUp/PgDn Home/End scroll | F5 exit"
            : state.ReaderViewInstructions!;

        return $"[grey]-- {Markup.Escape(title)} -- {Markup.Escape(instructions)} | {range} --[/]";
    }

    // Flattens the conversation into clean, wrapped plain-text lines with a role label
    // before each message. Uses ChatMessage.Text verbatim (no markup, glyphs or boxes).
    private static List<string> BuildReaderLines(AppState state, int width)
    {
        if (state.ReaderViewLines is { } customLines)
        {
            return BuildReaderLines(customLines, width);
        }

        List<string> lines = [];

        foreach (ChatMessage message in state.Messages)
        {
            string label = message.Role switch
            {
                Role.User => "user",
                Role.Assistant => "assistant",
                Role.Thinking => "thinking",
                Role.System => "system",
                _ => "message"
            };

            lines.Add($"{label}:");

            string[] rawLines = message.Text
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n');

            foreach (string rawLine in rawLines)
            {
                if (rawLine.Length == 0)
                {
                    lines.Add(string.Empty);
                    continue;
                }

                foreach (string wrapped in WrapText(rawLine, width))
                {
                    lines.Add(wrapped);
                }
            }

            lines.Add(string.Empty);
        }

        if (lines.Count == 0)
        {
            lines.Add("No messages yet.");
        }

        return lines;
    }

    private static List<ReaderViewLine> BuildReaderDisplayLines(AppState state, int width)
    {
        if (state.ReaderViewStyledLines is { } styledLines)
        {
            return [.. styledLines];
        }

        return BuildReaderLines(state, width)
            .Select(static line => new ReaderViewLine(Markup.Escape(line), line))
            .ToList();
    }

    private static List<string> BuildReaderLines(IReadOnlyList<string> sourceLines, int width)
    {
        List<string> lines = [];

        foreach (string sourceLine in sourceLines)
        {
            string normalized = sourceLine
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');

            foreach (string rawLine in normalized.Split('\n'))
            {
                if (rawLine.Length == 0)
                {
                    lines.Add(string.Empty);
                    continue;
                }

                foreach (string wrapped in WrapText(rawLine, width))
                {
                    lines.Add(wrapped);
                }
            }
        }

        if (lines.Count == 0)
        {
            lines.Add("Nothing to show.");
        }

        return lines;
    }

    private static int GetReaderViewportLineCount()
    {
        // Two lines are reserved for the reader header and the blank line below it.
        return Math.Max(1, GetWindowHeight() - 2);
    }

    private static int GetMaxReaderScrollOffset(AppState state)
    {
        int width = Math.Max(20, GetWindowWidth() - 1);
        int lineCount = BuildReaderDisplayLines(state, width).Count;
        return Math.Max(0, lineCount - GetReaderViewportLineCount());
    }

    private static int GetReaderSelectableCount(AppState state)
    {
        int width = Math.Max(20, GetWindowWidth() - 1);
        return BuildReaderDisplayLines(state, width)
            .Count(static line => line.SelectionKey is not null);
    }

    private static int GetReaderSelectedLineIndex(AppState state)
    {
        int width = Math.Max(20, GetWindowWidth() - 1);
        List<ReaderViewLine> lines = BuildReaderDisplayLines(state, width);
        int selectableIndex = 0;

        for (int index = 0; index < lines.Count; index++)
        {
            if (lines[index].SelectionKey is null)
            {
                continue;
            }

            if (selectableIndex == state.ReaderSelectedSelectableIndex)
            {
                return index;
            }

            selectableIndex++;
        }

        return -1;
    }

    // ---- Copy mode (in-app keyboard selection) ----

    // Wraps each visible line whose global index falls in the current selection with a
    // background highlight, and gives the cursor line a brighter one. startIndex is the
    // global index of the first visible line.
    private static List<ConversationLine> ApplyCopyModeHighlight(
        AppState state,
        IReadOnlyList<ConversationLine> visibleLines,
        int startIndex)
    {
        int cursor = state.CopyCursorLine;
        int anchor = state.CopyAnchorLine ?? cursor;
        int selectionStart = Math.Min(anchor, cursor);
        int selectionEnd = Math.Max(anchor, cursor);

        List<ConversationLine> highlighted = new(visibleLines.Count);

        for (int index = 0; index < visibleLines.Count; index++)
        {
            ConversationLine line = visibleLines[index];
            int globalIndex = startIndex + index;

            string markup = line.Markup;
            if (globalIndex == cursor)
            {
                markup = $"[on {CopyCursorBackground}]{markup}[/]";
            }
            else if (globalIndex >= selectionStart && globalIndex <= selectionEnd)
            {
                markup = $"[on {CopySelectionBackground}]{markup}[/]";
            }

            highlighted.Add(line with { Markup = markup });
        }

        return highlighted;
    }

    // Adjusts the conversation scroll so the copy cursor line stays within the viewport.
    private static void EnsureCopyCursorVisible(
        AppState state,
        int totalLineCount,
        int viewportLineCount)
    {
        if (totalLineCount <= viewportLineCount)
        {
            state.ConversationScrollOffset = 0;
            return;
        }

        int start = Math.Max(
            0,
            totalLineCount - viewportLineCount - state.ConversationScrollOffset);

        if (state.CopyCursorLine < start)
        {
            start = state.CopyCursorLine;
        }
        else if (state.CopyCursorLine > start + viewportLineCount - 1)
        {
            start = state.CopyCursorLine - viewportLineCount + 1;
        }

        int maxScrollOffset = Math.Max(0, totalLineCount - viewportLineCount);
        state.ConversationScrollOffset = Math.Clamp(
            totalLineCount - viewportLineCount - start,
            0,
            maxScrollOffset);
    }

    // The clean text for the current copy selection (or the cursor line when nothing is
    // anchored), joined by newlines with trailing blank lines trimmed.
    private static string BuildCopySelectionText(
        AppState state,
        IReadOnlyList<ConversationLine> lines)
    {
        if (lines.Count == 0)
        {
            return string.Empty;
        }

        int cursor = Math.Clamp(state.CopyCursorLine, 0, lines.Count - 1);
        int anchor = Math.Clamp(state.CopyAnchorLine ?? cursor, 0, lines.Count - 1);
        int selectionStart = Math.Min(anchor, cursor);
        int selectionEnd = Math.Max(anchor, cursor);

        List<string> selected = [];
        for (int index = selectionStart; index <= selectionEnd; index++)
        {
            selected.Add(lines[index].Copy);
        }

        while (selected.Count > 0 && selected[^1].Length == 0)
        {
            selected.RemoveAt(selected.Count - 1);
        }

        while (selected.Count > 0 && selected[0].Length == 0)
        {
            selected.RemoveAt(0);
        }

        return string.Join(Environment.NewLine, selected);
    }
}
