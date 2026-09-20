using FluentAssertions;
using StemCode.Application.Models;

namespace StemCode.Tests.Application.Models;

public sealed class ContextSizeOptionsTests
{
    [Theory]
    [InlineData("8k", 8_000)]
    [InlineData("32k", 32_000)]
    [InlineData("64k", 64_000)]
    [InlineData("125k", 125_000)]
    [InlineData("256k", 256_000)]
    [InlineData("96K", 96_000)]
    [InlineData("96000", 96_000)]
    public void Parse_Should_AcceptPresetsAndManualValues(string value, int expected)
    {
        ContextSizeOptions.Parse(value).Should().Be(expected);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1k")]
    [InlineData("abc")]
    [InlineData("999999999999999k")]
    public void Parse_Should_RejectInvalidValues(string value)
    {
        Action act = () => ContextSizeOptions.Parse(value);

        act.Should().Throw<ArgumentException>();
    }
}
