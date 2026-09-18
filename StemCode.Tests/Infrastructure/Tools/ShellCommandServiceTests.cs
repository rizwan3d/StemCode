using FluentAssertions;
using StemCode.Application.Models;
using StemCode.Application.Tools.Models;
using StemCode.Infrastructure.Secrets;
using StemCode.Infrastructure.Tools;
using StemCode.Infrastructure.WindowsSandbox;
using StemCode.Tests.Infrastructure.Secrets.TestDoubles;

namespace StemCode.Tests.Infrastructure.Tools;

public sealed class ShellCommandServiceTests : IDisposable
{
    private readonly string _workspaceRoot;

    public ShellCommandServiceTests()
    {
        _workspaceRoot = Path.Combine(
            Path.GetTempPath(),
            $"StemCode-Shell-{Guid.NewGuid():N}");

        Directory.CreateDirectory(_workspaceRoot);
        Directory.CreateDirectory(Path.Combine(_workspaceRoot, "src"));
    }

    [Fact]
    public async Task ExecuteAsync_Should_PreserveCompoundCommands_OnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        FakeProcessRunner processRunner = new();
        processRunner.EnqueueResult(new ProcessExecutionResult(0, "ok", string.Empty));
        ShellCommandService sut = new(
            processRunner,
            new StubWorkspaceRootProvider(_workspaceRoot));

        await sut.ExecuteAsync(
            new ShellCommandExecutionRequest("node -v && npm -v", "src"),
            CancellationToken.None);

        processRunner.Requests.Should().ContainSingle();
        ProcessExecutionRequest request = processRunner.Requests[0];
        processRunner.Requests[0].MaxOutputCharacters.Should().Be(8000);

        if (OperatingSystem.IsLinux())
        {
            request.FileName.Should().Be("bwrap");
            request.WorkingDirectory.Should().Be(Path.GetFullPath(_workspaceRoot));
            request.Arguments.Should().ContainInOrder(
                "--bind",
                Path.GetFullPath(_workspaceRoot),
                Path.GetFullPath(_workspaceRoot),
                "--chdir",
                Path.Combine(_workspaceRoot, "src"),
                "/bin/bash",
                "-lc",
                "node -v && npm -v");
            request.EnvironmentVariables!["STEMCODE_SANDBOX_ENFORCEMENT"].Should().Be("bubblewrap");
        }
        else if (OperatingSystem.IsMacOS())
        {
            request.FileName.Should().Be("sandbox-exec");
            request.WorkingDirectory.Should().Be(Path.Combine(_workspaceRoot, "src"));
            request.Arguments.Should().ContainInOrder("/bin/bash", "-lc", "node -v && npm -v");
            request.EnvironmentVariables!["STEMCODE_SANDBOX_ENFORCEMENT"].Should().Be("sandbox-exec");
        }
    }

    [Fact]
    public async Task ExecuteAsync_Should_TranslateCompoundCommands_ForWindowsPowerShell()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        FakeProcessRunner processRunner = new();
        processRunner.EnqueueResult(new ProcessExecutionResult(0, "ok", string.Empty));
        ShellCommandService sut = new(
            processRunner,
            new StubWorkspaceRootProvider(_workspaceRoot),
            new PermissionSettings
            {
                SandboxMode = ToolSandboxMode.DangerFullAccess
            });

        await sut.ExecuteAsync(
            new ShellCommandExecutionRequest("mkdir todo && cd todo && npm i", null),
            CancellationToken.None);

        processRunner.Requests.Should().ContainSingle();
        ProcessExecutionRequest request = processRunner.Requests[0];
        request.FileName.Should().Be("powershell");
        request.MaxOutputCharacters.Should().Be(8000);
        request.Arguments.Should().ContainInOrder("-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command");
        request.Arguments[^1].Should().Contain("$__stemcode_script_text");
        request.Arguments[^1].Should().Contain("FromBase64String");
        request.Arguments[^1].Should().Contain("$__stemcode_exit = $__stemcode_segment_exit");
        request.Arguments[^1].Should().Contain(". ([ScriptBlock]::Create($__stemcode_script_text))");
        request.Arguments[^1].Should().NotContain("&&");
    }

    [Fact]
    public async Task ExecuteAsync_Should_UseCmdFastPath_ForSimpleWindowsExternalCommands()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        FakeProcessRunner processRunner = new();
        processRunner.EnqueueResult(new ProcessExecutionResult(0, "ok", string.Empty));
        ShellCommandService sut = new(
            processRunner,
            new StubWorkspaceRootProvider(_workspaceRoot),
            new PermissionSettings
            {
                SandboxMode = ToolSandboxMode.DangerFullAccess
            });

        await sut.ExecuteAsync(
            new ShellCommandExecutionRequest("dotnet test", null),
            CancellationToken.None);

        processRunner.Requests.Should().ContainSingle();
        ProcessExecutionRequest request = processRunner.Requests[0];
        request.FileName.Should().EndWith("cmd.exe");
        request.Arguments.Should().Equal("/d", "/s", "/c", "dotnet test");
        request.MaxOutputCharacters.Should().Be(8000);
    }

    [Fact]
    public async Task ExecuteAsync_Should_UseExecutionPolicyBypass_ForWindowsPowerShell()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        FakeProcessRunner processRunner = new();
        processRunner.EnqueueResult(new ProcessExecutionResult(0, "ok", string.Empty));
        ShellCommandService sut = new(
            processRunner,
            new StubWorkspaceRootProvider(_workspaceRoot),
            new PermissionSettings
            {
                SandboxMode = ToolSandboxMode.DangerFullAccess
            });

        await sut.ExecuteAsync(
            new ShellCommandExecutionRequest("Write-Output ready", null),
            CancellationToken.None);

        processRunner.Requests.Should().ContainSingle();
        ProcessExecutionRequest request = processRunner.Requests[0];
        request.FileName.Should().Be("powershell");
        request.Arguments.Should().ContainInOrder("-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command");
        request.Arguments[^1].Should().Contain("Write-Output ready");
    }

    [Fact]
    public async Task ExecuteAsync_Should_RunPowerShellScriptShim_WhenExecutionPolicyIsBypassed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string shimDirectory = Path.Combine(_workspaceRoot, "shim-bin");
        Directory.CreateDirectory(shimDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(shimDirectory, "npm.ps1"),
            "param([string[]]$args)\r\nWrite-Output ('ps1 ' + ($args -join ' '))\r\n",
            CancellationToken.None);

        string shimPath = shimDirectory.Replace("'", "''", StringComparison.Ordinal);
        ShellCommandService sut = new(
            new ProcessRunner(),
            new StubWorkspaceRootProvider(_workspaceRoot),
            new PermissionSettings
            {
                SandboxMode = ToolSandboxMode.DangerFullAccess
            });

        ShellCommandExecutionResult result = await sut.ExecuteAsync(
            new ShellCommandExecutionRequest(
                "$env:PATH = '" + shimPath + ";' + $env:PATH; npm run dev",
                null),
            CancellationToken.None);

        result.ExitCode.Should().Be(0, result.StandardError);
        result.StandardError.Should().BeEmpty();
        result.StandardOutput.Should().Contain("ps1");
    }

    [Fact]
    public async Task ExecuteAsync_Should_RunRemainingWindowsSegments_When_FirstSegmentWritesOutput()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        ShellCommandService sut = new(
            new ProcessRunner(),
            new StubWorkspaceRootProvider(_workspaceRoot),
            new PermissionSettings
            {
                SandboxMode = ToolSandboxMode.DangerFullAccess
            });

        ShellCommandExecutionResult result = await sut.ExecuteAsync(
            new ShellCommandExecutionRequest(
                "mkdir todo && cd todo && Set-Content marker.txt ok",
                null),
            CancellationToken.None);

        result.ExitCode.Should().Be(0);
        File.ReadAllText(Path.Combine(_workspaceRoot, "todo", "marker.txt"))
            .Trim()
            .Should()
            .Be("ok");
    }

    [Fact]
    public async Task ExecuteAsync_Should_PreserveVariablesAcrossWindowsSemicolonSegments()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string filePath = Path.Combine(_workspaceRoot, "marker.txt");
        await File.WriteAllTextAsync(filePath, "before", CancellationToken.None);

        ShellCommandService sut = new(
            new ProcessRunner(),
            new StubWorkspaceRootProvider(_workspaceRoot),
            new PermissionSettings
            {
                SandboxMode = ToolSandboxMode.DangerFullAccess
            });

        ShellCommandExecutionResult result = await sut.ExecuteAsync(
            new ShellCommandExecutionRequest(
                "$p = 'marker.txt'; $c = [IO.File]::ReadAllText((Resolve-Path $p).Path); $c = $c.Replace('before', 'after'); [IO.File]::WriteAllText((Resolve-Path $p).Path, $c, [Text.Encoding]::UTF8)",
                null),
            CancellationToken.None);

        result.ExitCode.Should().Be(0);
        File.ReadAllText(filePath).Should().Be("after");
    }

    [Fact]
    public async Task ExecuteAsync_Should_ExposeSandboxMetadataToProcessEnvironment()
    {
        FakeProcessRunner processRunner = new();
        processRunner.EnqueueResult(new ProcessExecutionResult(0, "ok", string.Empty));
        ShellCommandService sut = new(
            processRunner,
            new StubWorkspaceRootProvider(_workspaceRoot),
            new PermissionSettings
            {
                SandboxMode = ToolSandboxMode.ReadOnly
            });

        ShellCommandExecutionResult result = await sut.ExecuteAsync(
            new ShellCommandExecutionRequest(
                "dotnet test",
                null,
                ShellCommandSandboxPermissions.RequireEscalated,
                "needs package cache access",
                ["dotnet", "test"]),
            CancellationToken.None);

        processRunner.Requests.Should().ContainSingle();
        ProcessExecutionRequest request = processRunner.Requests[0];
        request.EnvironmentVariables.Should().NotBeNull();
        request.EnvironmentVariables!["STEMCODE_SANDBOX_MODE"].Should().Be("read-only");
        request.EnvironmentVariables["STEMCODE_SANDBOX_EFFECTIVE_MODE"].Should().Be("danger-full-access");
        request.EnvironmentVariables["STEMCODE_SANDBOX_ENFORCEMENT"].Should().Be("none");
        request.EnvironmentVariables["STEMCODE_SANDBOX_PERMISSIONS"].Should().Be("require_escalated");
        request.EnvironmentVariables["STEMCODE_SANDBOX_JUSTIFICATION"].Should().Be("needs package cache access");
        request.EnvironmentVariables["STEMCODE_SANDBOX_PREFIX_RULE"].Should().Be("dotnet test");
        request.EnvironmentVariables["STEMCODE_WORKSPACE_ROOT"].Should().Be(Path.GetFullPath(_workspaceRoot));
        result.SandboxPermissions.Should().Be("require_escalated");
        result.Justification.Should().Be("needs package cache access");
        result.SandboxMode.Should().Be("danger-full-access");
        result.SandboxEnforcement.Should().Be("none");
    }

    [Fact]
    public async Task ExecuteAsync_Should_BypassSandboxWrapper_When_SandboxModeIsDangerFullAccess()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        FakeProcessRunner processRunner = new();
        processRunner.EnqueueResult(new ProcessExecutionResult(0, "ok", string.Empty));
        ShellCommandService sut = new(
            processRunner,
            new StubWorkspaceRootProvider(_workspaceRoot),
            new PermissionSettings
            {
                SandboxMode = ToolSandboxMode.DangerFullAccess
            });

        ShellCommandExecutionResult result = await sut.ExecuteAsync(
            new ShellCommandExecutionRequest("node -v && npm -v", "src"),
            CancellationToken.None);

        processRunner.Requests.Should().ContainSingle();
        ProcessExecutionRequest request = processRunner.Requests[0];
        request.FileName.Should().Be("/bin/bash");
        request.Arguments.Should().Equal("-lc", "node -v && npm -v");
        request.WorkingDirectory.Should().Be(Path.Combine(_workspaceRoot, "src"));
        request.EnvironmentVariables!["STEMCODE_SANDBOX_ENFORCEMENT"].Should().Be("none");
        result.SandboxMode.Should().Be("danger-full-access");
        result.SandboxEnforcement.Should().Be("none");
    }

    [Fact]
    public async Task ExecuteAsync_Should_ForwardPseudoTerminalToProcessRunner_When_Requested()
    {
        FakeProcessRunner processRunner = new();
        processRunner.EnqueueResult(new ProcessExecutionResult(0, "ok", string.Empty));
        ShellCommandService sut = new(
            processRunner,
            new StubWorkspaceRootProvider(_workspaceRoot),
            new PermissionSettings
            {
                SandboxMode = ToolSandboxMode.DangerFullAccess
            });

        ShellCommandExecutionResult result = await sut.ExecuteAsync(
            new ShellCommandExecutionRequest(
                "dotnet test",
                null,
                PseudoTerminal: true),
            CancellationToken.None);

        processRunner.Requests.Should().ContainSingle();
        ProcessExecutionRequest request = processRunner.Requests[0];
        request.UsePseudoTerminal.Should().BeTrue();
        request.EnvironmentVariables!["STEMCODE_SHELL_PTY"].Should().Be("1");
        result.PseudoTerminal.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_Should_UseReadOnlySandbox_When_SandboxModeIsReadOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        FakeProcessRunner processRunner = new();
        processRunner.EnqueueResult(new ProcessExecutionResult(0, "ok", string.Empty));
        ShellCommandService sut = new(
            processRunner,
            new StubWorkspaceRootProvider(_workspaceRoot),
            new PermissionSettings
            {
                SandboxMode = ToolSandboxMode.ReadOnly
            });

        ShellCommandExecutionResult result = await sut.ExecuteAsync(
            new ShellCommandExecutionRequest("git status --short", null),
            CancellationToken.None);

        processRunner.Requests.Should().ContainSingle();
        ProcessExecutionRequest request = processRunner.Requests[0];
        request.EnvironmentVariables!["STEMCODE_SANDBOX_EFFECTIVE_MODE"].Should().Be("read-only");
        result.SandboxMode.Should().Be("read-only");

        if (OperatingSystem.IsLinux())
        {
            request.FileName.Should().Be("bwrap");
            request.Arguments.Should().NotContain("--bind");
            request.EnvironmentVariables!["STEMCODE_SANDBOX_ENFORCEMENT"].Should().Be("bubblewrap");
        }
        else if (OperatingSystem.IsMacOS())
        {
            request.FileName.Should().Be("sandbox-exec");
            request.Arguments[1].Should().Contain("(deny file-write*)");
            request.Arguments[1].Should().NotContain("(allow file-write*");
            request.EnvironmentVariables!["STEMCODE_SANDBOX_ENFORCEMENT"].Should().Be("sandbox-exec");
        }
    }

    [Fact]
    public async Task ExecuteAsync_Should_RouteRestrictedWindowsCommands_ThroughWindowsSandboxRunner()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        FakeProcessRunner processRunner = new();
        FakeWindowsSandboxProcessRunner windowsSandboxProcessRunner = new();
        windowsSandboxProcessRunner.EnqueueResult(new ProcessExecutionResult(0, "10.0.103", string.Empty));
        ShellCommandService sut = new(
            processRunner,
            new StubWorkspaceRootProvider(_workspaceRoot),
            windowsSandboxProcessRunner: windowsSandboxProcessRunner);

        ShellCommandExecutionResult result = await sut.ExecuteAsync(
            new ShellCommandExecutionRequest("dotnet --version", null),
            CancellationToken.None);

        processRunner.Requests.Should().BeEmpty();
        windowsSandboxProcessRunner.Requests.Should().ContainSingle();
        ProcessExecutionRequest request = windowsSandboxProcessRunner.Requests[0].Request;
        WindowsSandboxExecutionContext context = windowsSandboxProcessRunner.Requests[0].Context;
        request.FileName.Should().EndWith("cmd.exe");
        request.Arguments.Should().Equal("/d", "/s", "/c", "dotnet --version");
        request.EnvironmentVariables!["STEMCODE_SANDBOX_ENFORCEMENT"].Should().Be("windows-sandbox");
        context.Mode.Should().Be(ToolSandboxMode.WorkspaceWrite);
        context.PolicyCwd.Should().Be(Path.GetFullPath(_workspaceRoot));
        context.CommandCwd.Should().Be(Path.GetFullPath(_workspaceRoot));
        context.WritableRoots.Should().ContainSingle().Which.Should().Be(Path.GetFullPath(_workspaceRoot));
        result.ExitCode.Should().Be(0);
        result.StandardOutput.Should().Be("10.0.103");
        result.SandboxMode.Should().Be("workspace-write");
        result.SandboxEnforcement.Should().Be("windows-sandbox");
    }

    [Fact]
    public async Task ExecuteAsync_Should_RejectPseudoTerminal_When_WindowsSandboxIsActive()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        FakeProcessRunner processRunner = new();
        FakeWindowsSandboxProcessRunner windowsSandboxProcessRunner = new();
        ShellCommandService sut = new(
            processRunner,
            new StubWorkspaceRootProvider(_workspaceRoot),
            windowsSandboxProcessRunner: windowsSandboxProcessRunner);

        ShellCommandExecutionResult result = await sut.ExecuteAsync(
            new ShellCommandExecutionRequest("npm test", null, PseudoTerminal: true),
            CancellationToken.None);

        processRunner.Requests.Should().BeEmpty();
        windowsSandboxProcessRunner.Requests.Should().BeEmpty();
        result.ExitCode.Should().Be(126);
        result.SandboxEnforcement.Should().Be("windows-sandbox");
        result.StandardError.Should().Contain("Pseudo-terminal mode is not supported");
    }

    [Fact]
    public async Task ExecuteAsync_Should_ReturnFailureResult_When_WindowsSandboxRunnerThrowsUnauthorizedAccess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        FakeProcessRunner processRunner = new();
        FakeWindowsSandboxProcessRunner windowsSandboxProcessRunner = new();
        windowsSandboxProcessRunner.EnqueueFailure(
            new UnauthorizedAccessException(
                "Unable to update sandbox ACLs for 'C:\\Users\\allga\\AppData\\Roaming\\StemCode'. Attempted to perform an unauthorized operation."));
        ShellCommandService sut = new(
            processRunner,
            new StubWorkspaceRootProvider(_workspaceRoot),
            windowsSandboxProcessRunner: windowsSandboxProcessRunner);

        ShellCommandExecutionResult result = await sut.ExecuteAsync(
            new ShellCommandExecutionRequest("npm run build 2>&1", null),
            CancellationToken.None);

        processRunner.Requests.Should().BeEmpty();
        windowsSandboxProcessRunner.Requests.Should().ContainSingle();
        result.ExitCode.Should().Be(126);
        result.SandboxEnforcement.Should().Be("windows-sandbox");
        result.StandardError.Should().Contain("Unable to start Windows OS sandbox shell execution:");
        result.StandardError.Should().Contain("Unable to update sandbox ACLs");
        result.StandardError.Should().Contain("C:\\Users\\allga\\AppData\\Roaming\\StemCode");
    }

    [Fact]
    public async Task StartBackgroundAsync_Should_RouteWindowsSandboxedBackgroundTerminals_ThroughSandboxRunner()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        FakeProcessRunner processRunner = new();
        FakeWindowsSandboxProcessRunner windowsSandboxProcessRunner = new();
        windowsSandboxProcessRunner.EnqueueBackgroundOutput("ready\n");
        ShellCommandService sut = new(
            processRunner,
            new StubWorkspaceRootProvider(_workspaceRoot),
            windowsSandboxProcessRunner: windowsSandboxProcessRunner);

        ShellCommandExecutionResult result = await sut.StartBackgroundAsync(
            new ShellCommandExecutionRequest("npm run dev", null),
            CancellationToken.None);

        processRunner.Requests.Should().BeEmpty();
        windowsSandboxProcessRunner.BackgroundRequests.Should().ContainSingle();
        ProcessExecutionRequest request = windowsSandboxProcessRunner.BackgroundRequests[0].Request;
        WindowsSandboxExecutionContext context = windowsSandboxProcessRunner.BackgroundRequests[0].Context;
        request.FileName.Should().EndWith("cmd.exe");
        request.Arguments.Should().Equal("/d", "/s", "/c", "npm run dev");
        request.EnvironmentVariables!["STEMCODE_SANDBOX_ENFORCEMENT"].Should().Be("windows-sandbox");
        context.Mode.Should().Be(ToolSandboxMode.WorkspaceWrite);

        result.Background.Should().BeTrue();
        result.TerminalStatus.Should().Be("running");
        result.SandboxEnforcement.Should().Be("windows-sandbox");
        result.TerminalId.Should().NotBeNullOrWhiteSpace();

        ShellCommandExecutionResult read = await sut.ReadBackgroundAsync(
            result.TerminalId!,
            sessionId: null,
            CancellationToken.None);
        read.StandardOutput.Should().Contain("ready");

        ShellCommandExecutionResult stopped = await sut.StopBackgroundAsync(
            result.TerminalId!,
            sessionId: null,
            CancellationToken.None);
        stopped.TerminalStatus.Should().Be("stopped");
    }

    [Fact]
    public async Task StartBackgroundAsync_Should_ReturnFailureResult_When_WindowsSandboxRunnerThrowsUnauthorizedAccess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        FakeProcessRunner processRunner = new();
        FakeWindowsSandboxProcessRunner windowsSandboxProcessRunner = new();
        windowsSandboxProcessRunner.EnqueueBackgroundFailure(
            new UnauthorizedAccessException(
                "Unable to update sandbox ACLs for 'C:\\Users\\allga\\AppData\\Roaming\\StemCode'. Attempted to perform an unauthorized operation."));
        ShellCommandService sut = new(
            processRunner,
            new StubWorkspaceRootProvider(_workspaceRoot),
            windowsSandboxProcessRunner: windowsSandboxProcessRunner);

        ShellCommandExecutionResult result = await sut.StartBackgroundAsync(
            new ShellCommandExecutionRequest("npm run dev", null),
            CancellationToken.None);

        processRunner.Requests.Should().BeEmpty();
        windowsSandboxProcessRunner.BackgroundRequests.Should().ContainSingle();
        result.ExitCode.Should().Be(126);
        result.Background.Should().BeTrue();
        result.TerminalStatus.Should().Be("failed");
        result.StandardError.Should().Contain("Unable to start Windows OS sandbox background terminal:");
        result.StandardError.Should().Contain("Unable to update sandbox ACLs");
    }

    [Fact]
    public async Task BackgroundTerminal_Should_StartReadAndStopCommand()
    {
        ShellCommandService sut = new(
            new ProcessRunner(),
            new StubWorkspaceRootProvider(_workspaceRoot),
            new PermissionSettings
            {
                SandboxMode = ToolSandboxMode.DangerFullAccess
            });
        string command = OperatingSystem.IsWindows()
            ? "Write-Output ready; Start-Sleep -Seconds 30"
            : "printf '%s\\n' ready; sleep 30";

        ShellCommandExecutionResult started = await sut.StartBackgroundAsync(
            new ShellCommandExecutionRequest(command, null),
            CancellationToken.None);

        started.Background.Should().BeTrue();
        started.TerminalId.Should().NotBeNullOrWhiteSpace();
        started.TerminalStatus.Should().Be("running");

        string terminalId = started.TerminalId!;
        try
        {
            string output = string.Empty;
            for (int attempt = 0; attempt < 50 && !output.Contains("ready", StringComparison.Ordinal); attempt++)
            {
                ShellCommandExecutionResult read = await sut.ReadBackgroundAsync(
                    terminalId,
                    sessionId: null,
                    CancellationToken.None);
                output += read.StandardOutput;
                await Task.Delay(100);
            }

            output.Should().Contain("ready");

            ShellCommandExecutionResult stopped = await sut.StopBackgroundAsync(
                terminalId,
                sessionId: null,
                CancellationToken.None);
            stopped.TerminalStatus.Should().Be("stopped");
            stopped.ExitCode.Should().Be(0);

            ShellCommandExecutionResult missing = await sut.ReadBackgroundAsync(
                terminalId,
                sessionId: null,
                CancellationToken.None);
            missing.TerminalStatus.Should().Be("not_found");
        }
        finally
        {
            await sut.StopBackgroundAsync(
                terminalId,
                sessionId: null,
                CancellationToken.None);
        }
    }

    [Fact]
    public async Task StartBackgroundAsync_Should_EnforceMaxConcurrentTerminalsPerSession()
    {
        ShellCommandService sut = new(
            new ProcessRunner(),
            new StubWorkspaceRootProvider(_workspaceRoot),
            new PermissionSettings
            {
                SandboxMode = ToolSandboxMode.DangerFullAccess
            },
            new ToolExecutionSettings
            {
                MaxConcurrentBackgroundTerminalsPerSession = 1
            });
        string command = CreateSleepCommand(seconds: 30);

        ShellCommandExecutionResult first = await sut.StartBackgroundAsync(
            new ShellCommandExecutionRequest(command, null, SessionId: "session-a"),
            CancellationToken.None);

        first.TerminalStatus.Should().Be("running");
        first.TerminalId.Should().NotBeNullOrWhiteSpace();

        try
        {
            IReadOnlyList<BackgroundTerminalInfo> listed = await sut.ListBackgroundAsync(
                "session-a",
                CancellationToken.None);
            listed.Should().ContainSingle(terminal =>
                terminal.Id == first.TerminalId &&
                terminal.Status == "running");

            ShellCommandExecutionResult rejected = await sut.StartBackgroundAsync(
                new ShellCommandExecutionRequest(command, null, SessionId: "session-a"),
                CancellationToken.None);

            rejected.TerminalStatus.Should().Be("failed");
            rejected.ExitCode.Should().Be(126);
            rejected.StandardError.Should().Contain("Maximum background terminals per session reached (1)");
        }
        finally
        {
            await sut.StopBackgroundAsync(
                first.TerminalId!,
                "session-a",
                CancellationToken.None);
        }
    }

    [Fact]
    public async Task CompletedBackgroundTerminal_Should_RemainReadableUntilTtlExpires()
    {
        ManualTimeProvider timeProvider = new(new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero));
        ShellCommandService sut = new(
            new ProcessRunner(),
            new StubWorkspaceRootProvider(_workspaceRoot),
            new PermissionSettings
            {
                SandboxMode = ToolSandboxMode.DangerFullAccess
            },
            new ToolExecutionSettings
            {
                CompletedBackgroundTerminalTtlSeconds = 5
            },
            timeProvider);
        string command = OperatingSystem.IsWindows()
            ? "Write-Output done"
            : "printf '%s\\n' done";

        ShellCommandExecutionResult started = await sut.StartBackgroundAsync(
            new ShellCommandExecutionRequest(command, null, SessionId: "session-a"),
            CancellationToken.None);

        started.TerminalId.Should().NotBeNullOrWhiteSpace();
        string terminalId = started.TerminalId!;

        ShellCommandExecutionResult completed = started;
        string output = string.Empty;
        for (int attempt = 0; attempt < 50; attempt++)
        {
            completed = await sut.ReadBackgroundAsync(
                terminalId,
                "session-a",
                CancellationToken.None);
            output += completed.StandardOutput;
            if (completed.TerminalStatus == "exited")
            {
                break;
            }

            await Task.Delay(100);
        }

        completed.TerminalStatus.Should().Be("exited");
        output.Should().Contain("done");

        IReadOnlyList<BackgroundTerminalInfo> retained = await sut.ListBackgroundAsync(
            "session-a",
            CancellationToken.None);
        retained.Should().ContainSingle(terminal =>
            terminal.Id == terminalId &&
            terminal.Status == "exited" &&
            terminal.ExpiresAtUtc == timeProvider.GetUtcNow().AddSeconds(5));

        ShellCommandExecutionResult secondRead = await sut.ReadBackgroundAsync(
            terminalId,
            "session-a",
            CancellationToken.None);
        secondRead.TerminalStatus.Should().Be("exited");

        timeProvider.Advance(TimeSpan.FromSeconds(6));

        IReadOnlyList<BackgroundTerminalInfo> expired = await sut.ListBackgroundAsync(
            "session-a",
            CancellationToken.None);
        expired.Should().BeEmpty();

        ShellCommandExecutionResult missing = await sut.ReadBackgroundAsync(
            terminalId,
            "session-a",
            CancellationToken.None);
        missing.TerminalStatus.Should().Be("not_found");
    }

    [Fact]
    public async Task BackgroundTerminal_Read_Should_PreserveBufferedOutputUpToBackgroundLimit()
    {
        ShellCommandService sut = new(
            new ProcessRunner(),
            new StubWorkspaceRootProvider(_workspaceRoot),
            new PermissionSettings
            {
                SandboxMode = ToolSandboxMode.DangerFullAccess
            });
        string expectedOutput = new('x', 12_000);
        string command = OperatingSystem.IsWindows()
            ? $"Write-Output '{expectedOutput}'"
            : $"printf '%s\n' '{expectedOutput}'";

        ShellCommandExecutionResult started = await sut.StartBackgroundAsync(
            new ShellCommandExecutionRequest(command, null),
            CancellationToken.None);

        started.TerminalId.Should().NotBeNullOrWhiteSpace();

        string terminalId = started.TerminalId!;
        try
        {
            ShellCommandExecutionResult read = started;
            string output = string.Empty;
            for (int attempt = 0; attempt < 50; attempt++)
            {
                read = await sut.ReadBackgroundAsync(
                    terminalId,
                    sessionId: null,
                    CancellationToken.None);
                output += read.StandardOutput;
                if (read.TerminalStatus == "exited")
                {
                    break;
                }

                await Task.Delay(100);
            }

            read.TerminalStatus.Should().Be("exited");
            output.Should().Be(expectedOutput);
        }
        finally
        {
            await sut.StopBackgroundAsync(
                terminalId,
                sessionId: null,
                CancellationToken.None);
        }
    }

    [Fact]
    public async Task BackgroundTerminal_Should_RejectReadAndStopFromAnotherSession()
    {
        ShellCommandService sut = new(
            new ProcessRunner(),
            new StubWorkspaceRootProvider(_workspaceRoot),
            new PermissionSettings
            {
                SandboxMode = ToolSandboxMode.DangerFullAccess
            });

        ShellCommandExecutionResult started = await sut.StartBackgroundAsync(
            new ShellCommandExecutionRequest(CreateSleepCommand(seconds: 30), null, SessionId: "session-a"),
            CancellationToken.None);

        started.TerminalStatus.Should().Be("running");
        string terminalId = started.TerminalId!;

        try
        {
            ShellCommandExecutionResult foreignRead = await sut.ReadBackgroundAsync(
                terminalId,
                "session-b",
                CancellationToken.None);
            foreignRead.TerminalStatus.Should().Be("not_found");

            ShellCommandExecutionResult foreignStop = await sut.StopBackgroundAsync(
                terminalId,
                "session-b",
                CancellationToken.None);
            foreignStop.TerminalStatus.Should().Be("not_found");

            // The foreign stop attempt must not have terminated the owner's terminal.
            IReadOnlyList<BackgroundTerminalInfo> ownerTerminals = await sut.ListBackgroundAsync(
                "session-a",
                CancellationToken.None);
            ownerTerminals.Should().ContainSingle(terminal =>
                terminal.Id == terminalId &&
                terminal.Status == "running");

            ShellCommandExecutionResult ownerRead = await sut.ReadBackgroundAsync(
                terminalId,
                "session-a",
                CancellationToken.None);
            ownerRead.TerminalStatus.Should().Be("running");
        }
        finally
        {
            await sut.StopBackgroundAsync(
                terminalId,
                "session-a",
                CancellationToken.None);
        }
    }

    [Fact]
    public async Task ExecuteAsync_Should_RejectWorkingDirectorySymlinkThatEscapesWorkspace()
    {
        string outsideRoot = Path.Combine(
            Path.GetTempPath(),
            $"StemCode-Shell-Outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideRoot);
        string linkPath = Path.Combine(_workspaceRoot, "outside-link");

        try
        {
            if (!TryCreateDirectorySymlink(linkPath, outsideRoot))
            {
                return;
            }

            FakeProcessRunner processRunner = new();
            ShellCommandService sut = new(
                processRunner,
                new StubWorkspaceRootProvider(_workspaceRoot),
                new PermissionSettings
                {
                    SandboxMode = ToolSandboxMode.DangerFullAccess
                });

            Func<Task> act = () => sut.ExecuteAsync(
                new ShellCommandExecutionRequest("pwd", "outside-link"),
                CancellationToken.None);

            await act.Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("*workspace*");
            processRunner.Requests.Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(outsideRoot))
            {
                Directory.Delete(outsideRoot, recursive: true);
            }
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspaceRoot))
        {
            Directory.Delete(_workspaceRoot, recursive: true);
        }
    }

    private sealed class StubWorkspaceRootProvider : StemCode.Application.Abstractions.IWorkspaceRootProvider
    {
        private readonly string _workspaceRoot;

        public StubWorkspaceRootProvider(string workspaceRoot)
        {
            _workspaceRoot = workspaceRoot;
        }

        public string GetWorkspaceRoot()
        {
            return _workspaceRoot;
        }
    }

    private static string CreateSleepCommand(int seconds)
    {
        return OperatingSystem.IsWindows()
            ? $"Start-Sleep -Seconds {seconds}"
            : $"sleep {seconds}";
    }

    private static bool TryCreateDirectorySymlink(
        string linkPath,
        string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException ||
                                         OperatingSystem.IsWindows() && exception is IOException)
        {
            return false;
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public ManualTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }

        public void Advance(TimeSpan value)
        {
            _utcNow += value;
        }
    }

    private sealed class FakeWindowsSandboxProcessRunner : IWindowsSandboxProcessRunner
    {
        private readonly Queue<ProcessExecutionResult> _results = new();
        private readonly Queue<string> _backgroundOutputs = new();
        private readonly Queue<Exception> _failures = new();
        private readonly Queue<Exception> _backgroundFailures = new();

        public List<(ProcessExecutionRequest Request, WindowsSandboxExecutionContext Context)> Requests { get; } = [];

        public List<(ProcessExecutionRequest Request, WindowsSandboxExecutionContext Context)> BackgroundRequests { get; } = [];

        public void EnqueueResult(ProcessExecutionResult result)
        {
            _results.Enqueue(result);
        }

        public void EnqueueBackgroundOutput(string standardOutput)
        {
            _backgroundOutputs.Enqueue(standardOutput);
        }

        public void EnqueueFailure(Exception exception)
        {
            _failures.Enqueue(exception);
        }

        public void EnqueueBackgroundFailure(Exception exception)
        {
            _backgroundFailures.Enqueue(exception);
        }

        public Task<ProcessExecutionResult> RunAsync(
            ProcessExecutionRequest request,
            WindowsSandboxExecutionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add((request, context));

            if (_failures.Count > 0)
            {
                throw _failures.Dequeue();
            }

            if (_results.Count == 0)
            {
                throw new InvalidOperationException("No queued process result is available.");
            }

            return Task.FromResult(_results.Dequeue());
        }

        public Task<IBackgroundProcessHandle> StartBackgroundAsync(
            ProcessExecutionRequest request,
            WindowsSandboxExecutionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BackgroundRequests.Add((request, context));

            if (_backgroundFailures.Count > 0)
            {
                throw _backgroundFailures.Dequeue();
            }

            string standardOutput = _backgroundOutputs.Count > 0
                ? _backgroundOutputs.Dequeue()
                : string.Empty;
            return Task.FromResult<IBackgroundProcessHandle>(new FakeBackgroundProcessHandle(standardOutput));
        }
    }

    private sealed class FakeBackgroundProcessHandle : IBackgroundProcessHandle
    {
        private readonly string _standardOutput;
        private bool _exited;

        public FakeBackgroundProcessHandle(string standardOutput)
        {
            _standardOutput = standardOutput;
        }

        public bool HasExited => _exited;

        public int ExitCode { get; private set; }

        public event EventHandler? Exited;

        public void StartStreaming(
            Action<string> onStandardOutput,
            Action<string> onStandardError)
        {
            if (!string.IsNullOrEmpty(_standardOutput))
            {
                onStandardOutput(_standardOutput);
            }
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            Complete();
            return Task.CompletedTask;
        }

        public bool WaitForExit(int milliseconds)
        {
            Complete();
            return true;
        }

        public Task CompleteStreamingAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public void Kill()
        {
            Complete();
        }

        public void Dispose()
        {
        }

        private void Complete()
        {
            if (_exited)
            {
                return;
            }

            _exited = true;
            ExitCode = 0;
            Exited?.Invoke(this, EventArgs.Empty);
        }
    }
}
