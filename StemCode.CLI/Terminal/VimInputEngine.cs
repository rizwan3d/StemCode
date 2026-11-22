namespace StemCode.CLI;

internal enum VimMode
{
    Insert,
    Normal
}

/// <summary>
/// Modal (vi-style) editing for the prompt input. It works on plain text and a
/// cursor index so it can be tested without a terminal; the input loop applies
/// the result to <see cref="AppState"/>.
/// </summary>
/// <remarks>
/// Supported in normal mode: <c>i a I A o O</c> to insert; <c>h l 0 ^ $ w b e j k</c>
/// to move; <c>x X D C s S r</c>; operators <c>d c y</c> with motions <c>w b e $ 0 ^</c>
/// or doubled for the whole input (<c>dd cc yy</c>); <c>p P</c> to put; <c>u</c> to
/// undo the last change; an optional count prefix (for example <c>3w</c>, <c>2dd</c>).
/// </remarks>
internal sealed class VimInputEngine
{
    private char? _pendingOperator;
    private bool _pendingReplace;
    private int _count;
    private string _register = string.Empty;
    private (string Text, int Cursor)? _undo;

    public VimMode Mode { get; private set; } = VimMode.Insert;

    public void Reset()
    {
        Mode = VimMode.Insert;
        ClearPending();
    }

    public readonly record struct Result(bool Handled, string Text, int Cursor);

    /// <summary>Escape from insert mode: switch to normal mode and step the cursor back one, like vi.</summary>
    public Result EnterNormalMode(string text, int cursor)
    {
        Mode = VimMode.Normal;
        ClearPending();
        return new Result(true, text, Math.Max(0, Math.Min(cursor - 1, LastIndex(text))));
    }

    /// <summary>Handles one key typed in normal mode. Unhandled keys (Enter, arrows) fall through.</summary>
    public Result HandleNormalKey(char key, string text, int cursor)
    {
        cursor = Math.Clamp(cursor, 0, text.Length);

        if (_pendingReplace)
        {
            _pendingReplace = false;
            if (text.Length == 0 || cursor >= text.Length)
            {
                return new Result(true, text, cursor);
            }

            SaveUndo(text, cursor);
            return new Result(true, text[..cursor] + key + text[(cursor + 1)..], cursor);
        }

        if (char.IsAsciiDigit(key) && (key != '0' || _count > 0))
        {
            _count = Math.Min(_count * 10 + (key - '0'), 999);
            return new Result(true, text, cursor);
        }

        int count = Math.Max(1, _count);

        if (_pendingOperator is char op)
        {
            return ApplyOperator(op, key, text, cursor, count);
        }

        _count = 0;
        switch (key)
        {
            case 'i':
                return Insert(text, cursor);
            case 'a':
                return Insert(text, Math.Min(cursor + 1, text.Length));
            case 'I':
                return Insert(text, LineStart(text, cursor));
            case 'A':
                return Insert(text, LineEnd(text, cursor));
            case 'o':
            {
                SaveUndo(text, cursor);
                int end = LineEnd(text, cursor);
                Mode = VimMode.Insert;
                return new Result(true, text.Insert(end, "\n"), end + 1);
            }
            case 'O':
            {
                SaveUndo(text, cursor);
                int start = LineStart(text, cursor);
                Mode = VimMode.Insert;
                return new Result(true, text.Insert(start, "\n"), start);
            }
            case 'h':
                return Move(text, Math.Max(LineStart(text, cursor), cursor - count));
            case 'l':
                return Move(text, Math.Min(LastIndexOnLine(text, cursor), cursor + count));
            case '0':
                return Move(text, LineStart(text, cursor));
            case '^':
                return Move(text, FirstNonBlank(text, cursor));
            case '$':
                return Move(text, LastIndexOnLine(text, cursor));
            case 'w':
                return Move(text, Math.Min(Repeat(count, cursor, position => NextWordStart(text, position)), LastIndex(text)));
            case 'b':
                return Move(text, Repeat(count, cursor, position => PreviousWordStart(text, position)));
            case 'e':
                return Move(text, Repeat(count, cursor, position => WordEnd(text, position)));
            case 'j':
                return Move(text, Repeat(count, cursor, position => VerticalMove(text, position, down: true)));
            case 'k':
                return Move(text, Repeat(count, cursor, position => VerticalMove(text, position, down: false)));
            case 'x':
                return Delete(text, cursor, cursor, Math.Min(cursor + count, LineEnd(text, cursor)), insertAfter: false);
            case 'X':
                return Delete(text, cursor, Math.Max(LineStart(text, cursor), cursor - count), cursor, insertAfter: false);
            case 'D':
                return Delete(text, cursor, cursor, LineEnd(text, cursor), insertAfter: false);
            case 'C':
                return Delete(text, cursor, cursor, LineEnd(text, cursor), insertAfter: true);
            case 's':
                return Delete(text, cursor, cursor, Math.Min(cursor + count, LineEnd(text, cursor)), insertAfter: true);
            case 'S':
                return Delete(text, cursor, 0, text.Length, insertAfter: true);
            case 'r':
                _pendingReplace = true;
                return new Result(true, text, cursor);
            case 'd':
            case 'c':
            case 'y':
                _pendingOperator = key;
                _count = count == 1 ? 0 : count;
                return new Result(true, text, cursor);
            case 'p':
                return Put(text, cursor, after: true, count);
            case 'P':
                return Put(text, cursor, after: false, count);
            case 'u':
                if (_undo is (string undoText, int undoCursor))
                {
                    _undo = (text, cursor);
                    return new Result(true, undoText, Math.Min(undoCursor, undoText.Length));
                }

                return new Result(true, text, cursor);
            default:
                // Any other printable key is swallowed in normal mode so it never
                // ends up typed into the prompt.
                return new Result(!char.IsControl(key), text, cursor);
        }
    }

    private Result ApplyOperator(char op, char motion, string text, int cursor, int count)
    {
        ClearPending();

        int start;
        int end;
        if (motion == op)
        {
            start = 0;
            end = text.Length;
        }
        else
        {
            int target = motion switch
            {
                'w' => op == 'c'
                    ? Repeat(count, cursor, position => WordEnd(text, position)) + 1
                    : Repeat(count, cursor, position => NextWordStart(text, position)),
                'e' => Repeat(count, cursor, position => WordEnd(text, position)) + 1,
                'b' => Repeat(count, cursor, position => PreviousWordStart(text, position)),
                '$' => LineEnd(text, cursor),
                '0' => LineStart(text, cursor),
                '^' => FirstNonBlank(text, cursor),
                _ => -1
            };

            if (target < 0)
            {
                return new Result(true, text, cursor);
            }

            target = Math.Clamp(target, 0, text.Length);
            start = Math.Min(cursor, target);
            end = Math.Max(cursor, target);
        }

        if (op == 'y')
        {
            _register = text[start..end];
            return new Result(true, text, motion == op ? cursor : start);
        }

        return Delete(text, cursor, start, end, insertAfter: op == 'c');
    }

    private Result Insert(string text, int cursor)
    {
        Mode = VimMode.Insert;
        return new Result(true, text, cursor);
    }

    private static Result Move(string text, int cursor)
    {
        return new Result(true, text, Math.Clamp(cursor, 0, text.Length));
    }

    private Result Delete(string text, int cursor, int start, int end, bool insertAfter)
    {
        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, start, text.Length);
        if (end > start)
        {
            SaveUndo(text, cursor);
            _register = text[start..end];
            text = text.Remove(start, end - start);
        }

        if (insertAfter)
        {
            Mode = VimMode.Insert;
            return new Result(true, text, start);
        }

        return new Result(true, text, Math.Max(0, Math.Min(start, LastIndex(text))));
    }

    private Result Put(string text, int cursor, bool after, int count)
    {
        if (_register.Length == 0)
        {
            return new Result(true, text, cursor);
        }

        SaveUndo(text, cursor);
        string inserted = string.Concat(Enumerable.Repeat(_register, count));
        int position = after && text.Length > 0 ? Math.Min(cursor + 1, text.Length) : cursor;
        return new Result(true, text.Insert(position, inserted), position + inserted.Length - 1);
    }

    private void SaveUndo(string text, int cursor)
    {
        _undo = (text, cursor);
    }

    private void ClearPending()
    {
        _pendingOperator = null;
        _pendingReplace = false;
        _count = 0;
    }

    private static int Repeat(int count, int start, Func<int, int> step)
    {
        int position = start;
        for (int index = 0; index < count; index++)
        {
            position = step(position);
        }

        return position;
    }

    private static int LastIndex(string text) => Math.Max(0, text.Length - 1);

    private static int LineStart(string text, int cursor)
    {
        int newline = cursor == 0 ? -1 : text.LastIndexOf('\n', Math.Min(cursor, text.Length) - 1);
        return newline + 1;
    }

    private static int LineEnd(string text, int cursor)
    {
        int newline = text.IndexOf('\n', Math.Min(cursor, text.Length));
        return newline < 0 ? text.Length : newline;
    }

    private static int LastIndexOnLine(string text, int cursor)
    {
        return Math.Max(LineStart(text, cursor), LineEnd(text, cursor) - 1);
    }

    private static int FirstNonBlank(string text, int cursor)
    {
        int position = LineStart(text, cursor);
        int end = LineEnd(text, cursor);
        while (position < end && char.IsWhiteSpace(text[position]))
        {
            position++;
        }

        return position;
    }

    private static int CharClass(char character)
    {
        if (char.IsWhiteSpace(character))
        {
            return 0;
        }

        return char.IsLetterOrDigit(character) || character == '_' ? 1 : 2;
    }

    private static int NextWordStart(string text, int position)
    {
        if (position >= text.Length)
        {
            return text.Length;
        }

        int startClass = CharClass(text[position]);
        while (position < text.Length && CharClass(text[position]) == startClass && startClass != 0)
        {
            position++;
        }

        while (position < text.Length && char.IsWhiteSpace(text[position]))
        {
            position++;
        }

        return position;
    }

    private static int PreviousWordStart(string text, int position)
    {
        position = Math.Min(position, text.Length);
        if (position == 0)
        {
            return 0;
        }

        position--;
        while (position > 0 && char.IsWhiteSpace(text[position]))
        {
            position--;
        }

        int wordClass = CharClass(text[position]);
        while (position > 0 && CharClass(text[position - 1]) == wordClass)
        {
            position--;
        }

        return position;
    }

    private static int WordEnd(string text, int position)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        position = Math.Min(position + 1, text.Length - 1);
        while (position < text.Length - 1 && char.IsWhiteSpace(text[position]))
        {
            position++;
        }

        int wordClass = CharClass(text[position]);
        while (position < text.Length - 1 && CharClass(text[position + 1]) == wordClass)
        {
            position++;
        }

        return position;
    }

    private static int VerticalMove(string text, int cursor, bool down)
    {
        int lineStart = LineStart(text, cursor);
        int column = cursor - lineStart;
        if (down)
        {
            int lineEnd = LineEnd(text, cursor);
            if (lineEnd >= text.Length)
            {
                return cursor;
            }

            int nextStart = lineEnd + 1;
            int nextEnd = LineEnd(text, nextStart);
            return Math.Min(nextStart + column, Math.Max(nextStart, nextEnd - 1));
        }

        if (lineStart == 0)
        {
            return cursor;
        }

        int previousStart = LineStart(text, lineStart - 1);
        int previousEnd = lineStart - 1;
        return Math.Min(previousStart + column, Math.Max(previousStart, previousEnd - 1));
    }
}
