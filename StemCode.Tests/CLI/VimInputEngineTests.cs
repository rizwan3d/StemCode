using FluentAssertions;
using StemCode.CLI;

namespace StemCode.Tests.CLI;

public sealed class VimInputEngineTests
{
    private readonly VimInputEngine _vim = new();
    private string _text = string.Empty;
    private int _cursor;

    private void Start(string text, int cursor)
    {
        _text = text;
        _cursor = cursor;
        VimInputEngine.Result result = _vim.EnterNormalMode(text, cursor + 1);
        _text = result.Text;
        _cursor = result.Cursor;
    }

    private void Keys(string keys)
    {
        foreach (char key in keys)
        {
            VimInputEngine.Result result = _vim.HandleNormalKey(key, _text, _cursor);
            _text = result.Text;
            _cursor = result.Cursor;
        }
    }

    [Fact]
    public void Escape_Should_EnterNormalMode_AndStepCursorBack()
    {
        VimInputEngine.Result result = _vim.EnterNormalMode("hello", 5);

        _vim.Mode.Should().Be(VimMode.Normal);
        result.Cursor.Should().Be(4);
    }

    [Fact]
    public void Motions_Should_MoveByCharacterWordAndLine()
    {
        Start("one two three", 0);

        Keys("w");
        _cursor.Should().Be(4);
        Keys("e");
        _cursor.Should().Be(6);
        Keys("$");
        _cursor.Should().Be(12);
        Keys("b");
        _cursor.Should().Be(8);
        Keys("0");
        _cursor.Should().Be(0);
        Keys("2w");
        _cursor.Should().Be(8);
        Keys("hh");
        _cursor.Should().Be(6);
    }

    [Fact]
    public void Operators_Should_DeleteChangeAndYank()
    {
        Start("one two three", 0);

        Keys("dw");
        _text.Should().Be("two three");

        Keys("p");
        _text.Should().Be("tone wo three");

        Keys("u");
        _text.Should().Be("two three");

        Keys("cw");
        _text.Should().Be(" three");
        _vim.Mode.Should().Be(VimMode.Insert);
    }

    [Fact]
    public void DoubledOperator_Should_ApplyToWholeInput()
    {
        Start("delete everything", 3);

        Keys("dd");

        _text.Should().BeEmpty();
        _cursor.Should().Be(0);
    }

    [Fact]
    public void Editing_Should_SupportXDReplaceAndInsertCommands()
    {
        Start("abcdef", 2);

        Keys("x");
        _text.Should().Be("abdef");

        Keys("rZ");
        _text.Should().Be("abZef");

        Keys("D");
        _text.Should().Be("ab");

        Keys("A");
        _vim.Mode.Should().Be(VimMode.Insert);
        _cursor.Should().Be(2);
    }

    [Fact]
    public void JK_Should_MoveBetweenLines_AndOOpensLine()
    {
        Start("first\nsecond", 2);

        Keys("j");
        _cursor.Should().Be(8);
        Keys("k");
        _cursor.Should().Be(2);

        Keys("o");
        _text.Should().Be("first\n\nsecond");
        _cursor.Should().Be(6);
        _vim.Mode.Should().Be(VimMode.Insert);
    }

    [Fact]
    public void UnknownPrintableKeys_Should_BeSwallowed_InNormalMode()
    {
        Start("text", 1);

        VimInputEngine.Result result = _vim.HandleNormalKey('Q', _text, _cursor);

        result.Handled.Should().BeTrue();
        result.Text.Should().Be("text");
    }
}
