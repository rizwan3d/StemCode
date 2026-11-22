using FluentAssertions;
using StemCode.CLI;

namespace StemCode.Tests.CLI;

public sealed class CliInvocationTests
{
    [Fact]
    public void Parse_Should_Default_To_Interactive_Mode_When_NoPromptIsProvided()
    {
        CliInvocation invocation = CliInvocation.Parse(
            [],
            stdinRedirected: false,
            () => throw new InvalidOperationException());

        invocation.Mode.Should().Be(CliMode.Interactive);
        invocation.Prompt.Should().BeNull();
    }

    [Theory]
    [InlineData(new[] { "--worktree" }, "")]
    [InlineData(new[] { "-w" }, "")]
    [InlineData(new[] { "--worktree=fix-login" }, "fix-login")]
    public void Parse_Should_ReadWorktreeOption(string[] args, string expectedName)
    {
        CliInvocation invocation = CliInvocation.Parse(
            args,
            stdinRedirected: false,
            () => throw new InvalidOperationException());

        invocation.Mode.Should().Be(CliMode.Interactive);
        invocation.WorktreeName.Should().Be(expectedName);
    }

    [Fact]
    public void Parse_Should_KeepPromptWordAfterWorktreeFlag()
    {
        CliInvocation invocation = CliInvocation.Parse(
            ["--worktree", "refactor"],
            stdinRedirected: false,
            () => throw new InvalidOperationException());

        invocation.Mode.Should().Be(CliMode.SingleTurn);
        invocation.Prompt.Should().Be("refactor");
        invocation.WorktreeName.Should().BeEmpty();
    }

    [Fact]
    public void Parse_Should_LeaveWorktreeUnset_ByDefault()
    {
        CliInvocation invocation = CliInvocation.Parse(
            ["Hello"],
            stdinRedirected: false,
            () => throw new InvalidOperationException());

        invocation.WorktreeName.Should().BeNull();
    }

    [Fact]
    public void Parse_Should_EnableJsonOutputForOneShotPrompt()
    {
        CliInvocation invocation = CliInvocation.Parse(
            ["--json", "--prompt", "Hello"],
            stdinRedirected: false,
            () => throw new InvalidOperationException());

        invocation.Mode.Should().Be(CliMode.SingleTurn);
        invocation.JsonOutput.Should().BeTrue();
        invocation.Prompt.Should().Be("Hello");
    }

    [Fact]
    public void Parse_Should_EnableAutoApprovalForOneShotPrompt()
    {
        CliInvocation invocation = CliInvocation.Parse(
            ["--yes", "Hello"],
            stdinRedirected: false,
            () => throw new InvalidOperationException());

        invocation.Mode.Should().Be(CliMode.SingleTurn);
        invocation.AutoApproveAllTools.Should().BeTrue();
    }

    [Fact]
    public void Parse_Should_EnableAutoApprovalForAcpMode()
    {
        CliInvocation invocation = CliInvocation.Parse(
            ["--acp", "-y"],
            stdinRedirected: false,
            () => throw new InvalidOperationException());

        invocation.Mode.Should().Be(CliMode.Acp);
        invocation.AutoApproveAllTools.Should().BeTrue();
    }

    [Fact]
    public void Parse_Should_ReadPromptFromRedirectedStandardInput_ByDefault()
    {
        bool readCalled = false;

        CliInvocation invocation = CliInvocation.Parse(
            [],
            stdinRedirected: true,
            () =>
            {
                readCalled = true;
                return "prompt from stdin";
            });

        invocation.Mode.Should().Be(CliMode.SingleTurn);
        invocation.Prompt.Should().Be("prompt from stdin");
        readCalled.Should().BeTrue();
    }

    [Fact]
    public void Parse_Should_ReadPromptFromExplicitStdinOption()
    {
        bool readCalled = false;

        CliInvocation invocation = CliInvocation.Parse(
            ["--stdin"],
            stdinRedirected: true,
            () =>
            {
                readCalled = true;
                return "prompt from explicit stdin";
            });

        invocation.Mode.Should().Be(CliMode.SingleTurn);
        invocation.Prompt.Should().Be("prompt from explicit stdin");
        readCalled.Should().BeTrue();
    }

    [Fact]
    public void Parse_Should_RespectDoubleDashPromptSeparator()
    {
        CliInvocation invocation = CliInvocation.Parse(
            ["--profile", "review", "--", "--not-an-option", "follow up"],
            stdinRedirected: false,
            () => throw new InvalidOperationException());

        invocation.Mode.Should().Be(CliMode.SingleTurn);
        invocation.Prompt.Should().Be("--not-an-option follow up");
        invocation.BackendArgs.Should().Equal("--profile", "review");
    }

    [Fact]
    public void Parse_Should_ForwardBackendRuntimeOptions()
    {
        CliInvocation invocation = CliInvocation.Parse(
            [
                "--surface", "desktop",
                "--section", "section-1",
                "--session", "section-2",
                "--profile", "review",
                "--thinking", "on",
                "--no-update-check",
                "--sandbox-mode", "danger-full-access"
            ],
            stdinRedirected: false,
            () => throw new InvalidOperationException());

        invocation.Mode.Should().Be(CliMode.Interactive);
        invocation.BackendArgs.Should().Equal(
            "--surface", "desktop",
            "--section", "section-1",
            "--session", "section-2",
            "--profile", "review",
            "--thinking", "on",
            "--no-update-check",
            "--Application:Permissions:SandboxMode=DangerFullAccess");
        invocation.RuntimeArguments.SectionId.Should().Be("section-2");
        invocation.RuntimeArguments.ProfileName.Should().Be("review");
        invocation.RuntimeArguments.ThinkingMode.Should().Be("on");
        invocation.RuntimeArguments.AppSurface.Should().Be("desktop");
        invocation.RuntimeArguments.SkipUpdateCheck.Should().BeTrue();
    }

    [Fact]
    public void Parse_Should_EnableNoOldReader_ForInteractiveSessionResume()
    {
        CliInvocation invocation = CliInvocation.Parse(
            ["--session", "section-1", "--no-old-reader"],
            stdinRedirected: false,
            () => throw new InvalidOperationException());

        invocation.Mode.Should().Be(CliMode.Interactive);
        invocation.NoOldReader.Should().BeTrue();
    }

    [Fact]
    public void Parse_Should_EnableNoOldReader_ForAcpMode()
    {
        CliInvocation invocation = CliInvocation.Parse(
            ["--acp", "--no-old-reader"],
            stdinRedirected: false,
            () => throw new InvalidOperationException());

        invocation.Mode.Should().Be(CliMode.Acp);
        invocation.NoOldReader.Should().BeTrue();
    }

    [Fact]
    public void Parse_Should_ParseProviderAuthKey()
    {
        CliInvocation invocation = CliInvocation.Parse(
            ["--provider-auth-key", "sk-test", "--prompt", "Hello"],
            stdinRedirected: false,
            () => throw new InvalidOperationException());

        invocation.ProviderAuthKey.Should().Be("sk-test");
        invocation.Prompt.Should().Be("Hello");
    }

    [Fact]
    public void Parse_Should_RejectJsonOutputWithoutOneShotPrompt()
    {
        Action act = () => CliInvocation.Parse(
            ["--json"],
            stdinRedirected: false,
            () => throw new InvalidOperationException());

        act.Should().Throw<ArgumentException>()
            .WithMessage("--json requires a one-shot prompt.");
    }

    [Fact]
    public void Parse_Should_RejectJsonOutputWithAcpMode()
    {
        Action act = () => CliInvocation.Parse(
            ["--acp", "--json"],
            stdinRedirected: false,
            () => throw new InvalidOperationException());

        act.Should().Throw<ArgumentException>()
            .WithMessage("--json cannot be combined with --acp.");
    }

    [Fact]
    public void Parse_Should_RejectUnknownOption()
    {
        Action act = () => CliInvocation.Parse(
            ["--unknown"],
            stdinRedirected: false,
            () => throw new InvalidOperationException());

        act.Should().Throw<ArgumentException>()
            .WithMessage("Unknown option '--unknown'.");
    }

    [Fact]
    public void Parse_Should_RejectBlankRedirectedInput()
    {
        Action act = () => CliInvocation.Parse(
            [],
            stdinRedirected: true,
            () => "   ");

        act.Should().Throw<ArgumentException>()
            .WithMessage("No prompt was provided.");
    }

    [Fact]
    public void Parse_Should_RejectConflictingModes()
    {
        Action act = () => CliInvocation.Parse(
            ["--acp", "--interactive"],
            stdinRedirected: false,
            () => throw new InvalidOperationException());

        act.Should().Throw<ArgumentException>()
            .WithMessage("--acp cannot be combined with --interactive.");
    }

    [Fact]
    public void Parse_Should_RejectInteractiveModeWhenJsonIsRequested()
    {
        Action act = () => CliInvocation.Parse(
            ["--interactive", "--json"],
            stdinRedirected: false,
            () => throw new InvalidOperationException());

        act.Should().Throw<ArgumentException>()
            .WithMessage("--json requires a one-shot prompt.");
    }

    [Fact]
    public void Parse_Should_RejectMissingBackendRuntimeOptionValue()
    {
        Action act = () => CliInvocation.Parse(
            ["--profile"],
            stdinRedirected: false,
            () => throw new InvalidOperationException());

        act.Should().Throw<ArgumentException>()
            .WithMessage("Missing value for --profile.");
    }

    [Fact]
    public void Parse_Should_RejectNoOldReaderWithoutSessionResume()
    {
        Action act = () => CliInvocation.Parse(
            ["--no-old-reader"],
            stdinRedirected: false,
            () => throw new InvalidOperationException());

        act.Should().Throw<ArgumentException>()
            .WithMessage("--no-old-reader requires --session or --section in interactive mode.");
    }

    [Fact]
    public void Parse_Should_RejectNoOldReaderForOneShotPrompt()
    {
        Action act = () => CliInvocation.Parse(
            ["--session", "section-1", "--no-old-reader", "--prompt", "hello"],
            stdinRedirected: false,
            () => throw new InvalidOperationException());

        act.Should().Throw<ArgumentException>()
            .WithMessage("--no-old-reader requires --session or --section in interactive mode.");
    }
}
