using FluentAssertions;
using StemCode.Application.Backend;

namespace StemCode.Tests.Application.Backend;

public sealed class BackendRuntimeArgumentsTests
{
    [Fact]
    public void Parse_Should_ExtractSessionOptions_AndPreserveRawArgs()
    {
        BackendRuntimeArguments arguments = BackendRuntimeArguments.Parse(
            [
                "--profile", "review",
                "--section=section-1",
                "--thinking", "off",
                "--surface", "VSCode",
                "--no-update-check",
                "--sandbox-mode", "danger-full-access"
            ]);

        arguments.RawArgs.Should().Equal(
            "--profile", "review",
            "--section=section-1",
            "--thinking", "off",
            "--surface", "VSCode",
            "--no-update-check",
            "--Application:Permissions:SandboxMode=DangerFullAccess");
        arguments.SectionId.Should().Be("section-1");
        arguments.ProfileName.Should().Be("review");
        arguments.ThinkingMode.Should().Be("off");
        arguments.AppSurface.Should().Be(BackendRuntimeOptions.VsCodeSurface);
        arguments.SkipUpdateCheck.Should().BeTrue();
    }

    [Fact]
    public void Parse_Should_ExtractHeadlessOverrides()
    {
        BackendRuntimeArguments arguments = BackendRuntimeArguments.Parse(
            [
                "--model", "gpt-5-mini",
                "--max-turns", "7",
                "--allowedTools", "file_write shell_command(git *)",
                "--allowed-tools=file_read",
                "--disallowedTools", "web_search, file_delete(docs/**)",
                "--append-system-prompt", "First.",
                "--append-system-prompt=Second."
            ]);

        arguments.RawArgs.Should().Equal("--Application:Conversation:MaxToolRoundsPerTurn=7");
        arguments.Overrides.ModelId.Should().Be("gpt-5-mini");
        arguments.Overrides.AllowedTools.Select(static spec => spec.ToString())
            .Should()
            .Equal("file_write", "shell_command(git *)", "file_read");
        arguments.Overrides.DisallowedTools.Select(static spec => spec.ToString())
            .Should()
            .Equal("web_search", "file_delete(docs/**)");
        arguments.Overrides.HiddenToolNames.Should().BeEquivalentTo(["web_search"]);
        arguments.Overrides.AppendSystemPrompt.Should().Be($"First.{Environment.NewLine}{Environment.NewLine}Second.");

        IReadOnlyList<StemCode.Application.Models.PermissionRule> rules = arguments.Overrides.CreatePermissionRules();
        rules.Select(static rule => rule.Mode).Should().Equal(
            StemCode.Application.Models.PermissionMode.Allow,
            StemCode.Application.Models.PermissionMode.Allow,
            StemCode.Application.Models.PermissionMode.Allow,
            StemCode.Application.Models.PermissionMode.Deny,
            StemCode.Application.Models.PermissionMode.Deny);
        rules[1].Tools.Should().Equal("shell_command");
        rules[1].Patterns.Should().Equal("git *");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("many")]
    public void Parse_Should_RejectInvalidMaxTurns(string value)
    {
        Action act = () => BackendRuntimeArguments.Parse(["--max-turns", value]);

        act.Should().Throw<ArgumentException>().WithMessage("*--max-turns*");
    }

    [Fact]
    public void Parse_Should_RejectUnbalancedToolRule()
    {
        Action act = () => BackendRuntimeArguments.Parse(["--allowedTools", "shell_command(git"]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void WithDefaults_Should_KeepOverrides()
    {
        BackendRuntimeArguments arguments = BackendRuntimeArguments.Parse(["--model", "m1"])
            .WithDefaults(BackendRuntimeOptions.CliSurface);

        arguments.Overrides.ModelId.Should().Be("m1");
    }

    [Fact]
    public void Parse_Should_UseLastSessionValue_ButPreserveFirstSurfaceBehavior()
    {
        BackendRuntimeArguments arguments = BackendRuntimeArguments.Parse(
            [
                "--session", "section-1",
                "--section", "section-2",
                "--surface", "desktop",
                "--surface", "jetbrains"
            ]);

        arguments.SectionId.Should().Be("section-2");
        arguments.AppSurface.Should().Be(BackendRuntimeOptions.DesktopSurface);
    }

    [Fact]
    public void WithDefaults_Should_ApplyFallbackSurface_AndSkipUpdateCheck()
    {
        BackendRuntimeArguments arguments = BackendRuntimeArguments.Empty.WithDefaults(
            BackendRuntimeOptions.DesktopSurface,
            skipUpdateCheck: true);

        arguments.RawArgs.Should().BeEmpty();
        arguments.AppSurface.Should().Be(BackendRuntimeOptions.DesktopSurface);
        arguments.SkipUpdateCheck.Should().BeTrue();
    }

    [Fact]
    public void Parse_Should_ThrowForMissingRecognizedOptionValue()
    {
        Action act = () => BackendRuntimeArguments.Parse(["--thinking"]);

        act.Should().Throw<ArgumentException>()
            .WithMessage("Missing value for --thinking.");
    }

    [Fact]
    public void Parse_Should_RejectInvalidSandboxModeValue()
    {
        Action act = () => BackendRuntimeArguments.Parse(["--sandbox-mode", "unsafe"]);

        act.Should().Throw<ArgumentException>()
            .WithMessage("Invalid value for --sandbox-mode. Expected one of: read-only, workspace-write, danger-full-access.");
    }
}
