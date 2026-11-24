using FluentAssertions;
using StemCode.VS.ToolWindows;

namespace StemCode.VS.Tests;

public sealed class VoiceTextTests
{
    [Theory]
    [InlineData("", "hello", "", "hello", 5)]
    [InlineData("fix", "the bug", "", "fix the bug", 11)]
    [InlineData("fix ", "the bug", "", "fix the bug", 11)]
    [InlineData("", "please", "fix it", "please fix it", 7)]
    [InlineData("a", "b", "c", "a b c", 4)]
    [InlineData("a ", " b ", " c", "a b c", 3)]
    public void JoinDictation_SpacesTranscriptFromNeighbours(string before, string transcript, string after, string text, int caret)
    {
        (string joined, int position) = VoiceText.JoinDictation(before, transcript, after);

        joined.Should().Be(text);
        position.Should().Be(caret);
    }

    [Fact]
    public void JoinDictation_BlankTranscript_LeavesTextUnchanged()
    {
        VoiceText.JoinDictation("abc", "   ", "def").Should().Be(("abcdef", 3));
    }

    [Fact]
    public void DescribeProgress_ListeningPromptsToSpeak()
    {
        VoiceText.DescribeProgress("Listening\r").Should().Be("listening… speak now (Esc to stop)");
        VoiceText.DescribeProgress(" Downloading voice model 40% ").Should().Be("Downloading voice model 40%");
    }
}
