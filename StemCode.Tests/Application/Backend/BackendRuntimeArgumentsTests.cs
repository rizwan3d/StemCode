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
                "--context-size", "125k",
                "--surface", "VSCode",
                "--no-update-check",
                "--sandbox-mode", "danger-full-access"
            ]);

        arguments.RawArgs.Should().Equal(
            "--profile", "review",
            "--section=section-1",
            "--thinking", "off",
            "--context-size", "125k",
            "--surface", "VSCode",
            "--no-update-check",
            "--Application:Permissions:SandboxMode=DangerFullAccess");
        arguments.SectionId.Should().Be("section-1");
        arguments.ProfileName.Should().Be("review");
        arguments.ThinkingMode.Should().Be("off");
        arguments.ContextWindowTokens.Should().Be(125_000);
        arguments.AppSurface.Should().Be(BackendRuntimeOptions.VsCodeSurface);
        arguments.SkipUpdateCheck.Should().BeTrue();
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
    public void Parse_Should_ParseManualContextSize()
    {
        BackendRuntimeArguments arguments = BackendRuntimeArguments.Parse(["--context-size=96000"]);

        arguments.ContextWindowTokens.Should().Be(96_000);
        arguments.RawArgs.Should().Equal("--context-size=96000");
    }

    [Fact]
    public void Parse_Should_RejectInvalidContextSize()
    {
        Action act = () => BackendRuntimeArguments.Parse(["--context-size", "1k"]);

        act.Should().Throw<ArgumentException>()
            .WithMessage("Invalid context size*");
    }

    [Fact]
    public void Parse_Should_RejectInvalidSandboxModeValue()
    {
        Action act = () => BackendRuntimeArguments.Parse(["--sandbox-mode", "unsafe"]);

        act.Should().Throw<ArgumentException>()
            .WithMessage("Invalid value for --sandbox-mode. Expected one of: read-only, workspace-write, danger-full-access.");
    }
}
