using StemCode.Application.Abstractions;
using StemCode.Application.Models;
using StemCode.Application.Tools;
using StemCode.Application.Tools.Models;
using StemCode.Application.Utilities;
using StemCode.Infrastructure.Secrets;
using StemCode.Infrastructure.WindowsSandbox;
using StemCode.Infrastructure.Workspaces;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace StemCode.Infrastructure.Tools;

internal sealed class ShellCommandService : IShellCommandService, IDisposable
{
    private const int MaxOutputCharacters = 8_000;
    private const int MaxBackgroundOutputCharacters = 16_000;
    private const int DefaultCompletedBackgroundTerminalTtlSeconds = 300;
    private const int DefaultMaxConcurrentBackgroundTerminalsPerSession = 4;
    private static readonly HashSet<string> WindowsCmdFastPathCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "bun",
        "cargo",
        "deno",
        "dotnet",
        "fd",
        "git",
        "go",
        "gradle",
        "java",
        "javac",
        "mvn",
        "node",
        "npm",
        "npx",
        "nuget",
        "pip",
        "pip3",
        "pnpm",
        "python",
        "python3",
        "rg",
        "rustc",
        "tsc",
        "tsx",
        "yarn"
    };
    private const string RunningStatus = "running";
    private const string ExitedStatus = "exited";
    private const string FailedStatus = "failed";
    private const string NotFoundStatus = "not_found";
    private const string StoppedStatus = "stopped";

    private readonly ConcurrentDictionary<string, BackgroundTerminal> _backgroundTerminals = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _backgroundTerminalGate = new();
    private readonly TimeSpan _completedBackgroundTerminalTtl;
    private readonly EventHandler _processExitHandler;
    private readonly int _maxConcurrentBackgroundTerminalsPerSession;
    private readonly IProcessRunner _processRunner;
    private readonly PermissionSettings _permissionSettings;
    private readonly TimeProvider _timeProvider;
    private readonly IWindowsSandboxProcessRunner? _windowsSandboxProcessRunner;
    private readonly IWorkspaceRootProvider _workspaceRootProvider;
    private int _backgroundTerminalSequence;
    private bool _disposed;

    public ShellCommandService(
        IProcessRunner processRunner,
        IWorkspaceRootProvider workspaceRootProvider,
        PermissionSettings? permissionSettings = null,
        ToolExecutionSettings? toolExecutionSettings = null,
        TimeProvider? timeProvider = null,
        IWindowsSandboxProcessRunner? windowsSandboxProcessRunner = null)
    {
        _processRunner = processRunner;
        _workspaceRootProvider = workspaceRootProvider;
        _permissionSettings = permissionSettings ?? new PermissionSettings();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _windowsSandboxProcessRunner = windowsSandboxProcessRunner;
        _maxConcurrentBackgroundTerminalsPerSession = Math.Max(
            1,
            toolExecutionSettings?.MaxConcurrentBackgroundTerminalsPerSession ??
            DefaultMaxConcurrentBackgroundTerminalsPerSession);
        _completedBackgroundTerminalTtl = TimeSpan.FromSeconds(Math.Max(
            1,
            toolExecutionSettings?.CompletedBackgroundTerminalTtlSeconds ??
            DefaultCompletedBackgroundTerminalTtlSeconds));
        _processExitHandler = (_, _) => StopAllBackgroundTerminals();
        AppDomain.CurrentDomain.ProcessExit += _processExitHandler;
    }

    public bool IsPseudoTerminalSupported => _isPseudoTerminalSupported.Value;

    private static readonly Lazy<bool> _isPseudoTerminalSupported = new(DetectPseudoTerminalSupport);

    private static bool DetectPseudoTerminalSupport()
    {
        if (OperatingSystem.IsWindows())
            return true;
        if (OperatingSystem.IsLinux())
            return File.Exists("/usr/bin/script") || File.Exists("/bin/script");
        return false;
    }

    public async Task<ShellCommandExecutionResult> ExecuteAsync(
        ShellCommandExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        ShellCommandExecutionRequest normalizedRequest = NormalizeRequest(request);

        if (string.IsNullOrWhiteSpace(normalizedRequest.Command))
        {
            throw new ArgumentException(
                "Shell command must be provided.",
                nameof(request));
        }

        PreparedShellCommand prepared = PrepareShellCommand(normalizedRequest);

        ProcessExecutionResult result;
        if (IsWindowsSandboxEnforcement(prepared.SandboxPlan.Enforcement))
        {
            if (prepared.ProcessRequest.UsePseudoTerminal)
            {
                return CreateExecutionFailureResult(
                    normalizedRequest,
                    prepared.WorkingDirectory,
                    prepared.SandboxPlan.Enforcement,
                    "Pseudo-terminal mode is not supported when Windows OS sandboxing is active. Re-run without 'pty' or request sandbox escalation.");
            }

            if (_windowsSandboxProcessRunner is null)
            {
                return CreateExecutionFailureResult(
                    normalizedRequest,
                    prepared.WorkingDirectory,
                    prepared.SandboxPlan.Enforcement,
                    "Windows OS sandboxing is not configured for shell execution.");
            }

            try
            {
                result = await _windowsSandboxProcessRunner.RunAsync(
                    prepared.ProcessRequest,
                    CreateWindowsSandboxExecutionContext(
                        prepared.WorkingDirectory,
                        prepared.EffectiveSandboxMode),
                    cancellationToken);
            }
            catch (Exception exception) when (
                exception is PlatformNotSupportedException or
                Win32Exception or
                UnauthorizedAccessException or
                InvalidOperationException)
            {
                return CreateExecutionFailureResult(
                    normalizedRequest,
                    prepared.WorkingDirectory,
                    prepared.SandboxPlan.Enforcement,
                    $"Unable to start Windows OS sandbox shell execution: {exception.Message}");
            }
        }
        else
        {
            try
            {
                result = await _processRunner.RunAsync(
                    prepared.ProcessRequest,
                    cancellationToken);
            }
            catch (PlatformNotSupportedException exception) when (prepared.ProcessRequest.UsePseudoTerminal)
            {
                return CreateExecutionFailureResult(
                    normalizedRequest,
                    prepared.WorkingDirectory,
                    prepared.SandboxPlan.Enforcement,
                    $"Unable to start PTY shell execution: {exception.Message}");
            }
            catch (Win32Exception exception) when (prepared.ProcessRequest.UsePseudoTerminal)
            {
                return CreateExecutionFailureResult(
                    normalizedRequest,
                    prepared.WorkingDirectory,
                    prepared.SandboxPlan.Enforcement,
                    $"Unable to start PTY shell execution: {exception.Message}");
            }
            catch (Win32Exception exception) when (IsSandboxRunnerEnforcement(prepared.SandboxPlan.Enforcement))
            {
                return CreateExecutionFailureResult(
                    normalizedRequest,
                    prepared.WorkingDirectory,
                    prepared.SandboxPlan.Enforcement,
                    $"Unable to start OS-level shell sandbox runner '{prepared.ProcessRequest.FileName}': {exception.Message}");
            }
            catch (Win32Exception exception)
            {
                return CreateExecutionFailureResult(
                    normalizedRequest,
                    prepared.WorkingDirectory,
                    prepared.SandboxPlan.Enforcement,
                    $"Unable to start shell '{prepared.ProcessRequest.FileName}': {exception.Message}");
            }
        }

        return new ShellCommandExecutionResult(
            normalizedRequest.Command,
            ToWorkspaceRelativePath(prepared.WorkingDirectory),
            result.ExitCode,
            TrimOutput(result.StandardOutput),
            TrimOutput(result.StandardError),
            ShellCommandSandboxArguments.ToWireValue(normalizedRequest.SandboxPermissions),
            string.IsNullOrWhiteSpace(normalizedRequest.Justification)
                ? null
                : normalizedRequest.Justification.Trim(),
            ToWireValue(prepared.EffectiveSandboxMode),
            prepared.SandboxPlan.Enforcement,
            normalizedRequest.PseudoTerminal);
    }

    public async Task<ShellCommandExecutionResult> StartBackgroundAsync(
        ShellCommandExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        ShellCommandExecutionRequest normalizedRequest = NormalizeRequest(request);

        if (string.IsNullOrWhiteSpace(normalizedRequest.Command))
        {
            throw new ArgumentException(
                "Shell command must be provided.",
                nameof(request));
        }

        PreparedShellCommand prepared = PrepareShellCommand(normalizedRequest);

        if (prepared.ProcessRequest.UsePseudoTerminal)
        {
            return CreateExecutionFailureResult(
                normalizedRequest,
                prepared.WorkingDirectory,
                prepared.SandboxPlan.Enforcement,
                "Background terminals do not support pseudo-terminal mode.",
                background: true,
                terminalAction: "start");
        }

        if (IsWindowsSandboxEnforcement(prepared.SandboxPlan.Enforcement))
        {
            return await StartWindowsSandboxBackgroundTerminalAsync(
                normalizedRequest,
                prepared,
                cancellationToken);
        }

        try
        {
            lock (_backgroundTerminalGate)
            {
                RemoveExpiredCompletedTerminals();
                string sessionId = NormalizeSessionId(normalizedRequest.SessionId);
                if (CountRunningTerminals(sessionId) >= _maxConcurrentBackgroundTerminalsPerSession)
                {
                    return CreateBackgroundCapReachedResult(normalizedRequest, prepared);
                }

                BackgroundTerminal terminal = StartBackgroundTerminal(
                    normalizedRequest,
                    prepared);
                _backgroundTerminals[terminal.Id] = terminal;

                return CreateBackgroundResult(
                    terminal,
                    terminalAction: "start",
                    terminalStatus: terminal.Status,
                    exitCode: 0,
                    standardOutput: string.Empty,
                    standardError: string.Empty);
            }
        }
        catch (Win32Exception exception)
        {
            return CreateExecutionFailureResult(
                normalizedRequest,
                prepared.WorkingDirectory,
                prepared.SandboxPlan.Enforcement,
                $"Unable to start shell '{prepared.ProcessRequest.FileName}': {exception.Message}",
                background: true,
                terminalAction: "start");
        }
    }

    private async Task<ShellCommandExecutionResult> StartWindowsSandboxBackgroundTerminalAsync(
        ShellCommandExecutionRequest request,
        PreparedShellCommand prepared,
        CancellationToken cancellationToken)
    {
        if (_windowsSandboxProcessRunner is null)
        {
            return CreateExecutionFailureResult(
                request,
                prepared.WorkingDirectory,
                prepared.SandboxPlan.Enforcement,
                "Windows OS sandboxing is not configured for shell execution.",
                background: true,
                terminalAction: "start");
        }

        string sessionId = NormalizeSessionId(request.SessionId);
        lock (_backgroundTerminalGate)
        {
            RemoveExpiredCompletedTerminals();
            if (CountRunningTerminals(sessionId) >= _maxConcurrentBackgroundTerminalsPerSession)
            {
                return CreateBackgroundCapReachedResult(request, prepared);
            }
        }

        IBackgroundProcessHandle handle;
        try
        {
            handle = await _windowsSandboxProcessRunner.StartBackgroundAsync(
                prepared.ProcessRequest,
                CreateWindowsSandboxExecutionContext(
                    prepared.WorkingDirectory,
                    prepared.EffectiveSandboxMode),
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is PlatformNotSupportedException or
            Win32Exception or
            UnauthorizedAccessException or
            InvalidOperationException)
        {
            return CreateExecutionFailureResult(
                request,
                prepared.WorkingDirectory,
                prepared.SandboxPlan.Enforcement,
                $"Unable to start Windows OS sandbox background terminal: {exception.Message}",
                background: true,
                terminalAction: "start");
        }

        BackgroundTerminal terminal = CreateBackgroundTerminal(request, prepared, handle);
        terminal.StartReaders();

        bool registered;
        lock (_backgroundTerminalGate)
        {
            RemoveExpiredCompletedTerminals();
            registered = CountRunningTerminals(sessionId) < _maxConcurrentBackgroundTerminalsPerSession;
            if (registered)
            {
                _backgroundTerminals[terminal.Id] = terminal;
            }
        }

        if (!registered)
        {
            // Lost the concurrency race after launching; tear the sandboxed terminal back down.
            await terminal.StopAsync(cancellationToken);
            terminal.Dispose();
            return CreateBackgroundCapReachedResult(request, prepared);
        }

        return CreateBackgroundResult(
            terminal,
            terminalAction: "start",
            terminalStatus: terminal.Status,
            exitCode: 0,
            standardOutput: string.Empty,
            standardError: string.Empty);
    }

    private int CountRunningTerminals(string sessionId)
    {
        return _backgroundTerminals.Values.Count(terminal =>
            string.Equals(terminal.SessionId, sessionId, StringComparison.Ordinal) &&
            terminal.IsRunning);
    }

    private ShellCommandExecutionResult CreateBackgroundCapReachedResult(
        ShellCommandExecutionRequest request,
        PreparedShellCommand prepared)
    {
        return CreateExecutionFailureResult(
            request,
            prepared.WorkingDirectory,
            prepared.SandboxPlan.Enforcement,
            $"Maximum background terminals per session reached ({_maxConcurrentBackgroundTerminalsPerSession}). Stop one with /terminals stop <id> before starting another.",
            background: true,
            terminalAction: "start");
    }

    public async Task<ShellCommandExecutionResult> ReadBackgroundAsync(
        string terminalId,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        RemoveExpiredCompletedTerminals();

        if (!TryGetBackgroundTerminal(terminalId, out BackgroundTerminal? terminal) ||
            !IsOwnedBySession(terminal!, sessionId))
        {
            return CreateBackgroundNotFoundResult(terminalId, "read");
        }

        BackgroundTerminal activeTerminal = terminal!;
        if (!activeTerminal.IsRunning)
        {
            await activeTerminal.CompleteReadersAsync(cancellationToken);
        }

        (string standardOutput, string standardError) = activeTerminal.ReadNewOutput();
        string status = activeTerminal.Status;
        int exitCode = string.Equals(status, ExitedStatus, StringComparison.Ordinal)
            ? activeTerminal.ExitCodeOrDefault()
            : 0;
        ShellCommandExecutionResult result = CreateBackgroundResult(
            activeTerminal,
            terminalAction: "read",
            terminalStatus: status,
            exitCode,
            standardOutput,
            standardError);

        return result;
    }

    public async Task<ShellCommandExecutionResult> StopBackgroundAsync(
        string terminalId,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        RemoveExpiredCompletedTerminals();

        string normalizedTerminalId = NormalizeTerminalId(terminalId);
        if (!_backgroundTerminals.TryGetValue(normalizedTerminalId, out BackgroundTerminal? terminal) ||
            !IsOwnedBySession(terminal, sessionId) ||
            !_backgroundTerminals.TryRemove(normalizedTerminalId, out terminal))
        {
            return CreateBackgroundNotFoundResult(terminalId, "stop");
        }

        await terminal.StopAsync(cancellationToken);
        (string standardOutput, string standardError) = terminal.ReadNewOutput();
        ShellCommandExecutionResult result = CreateBackgroundResult(
            terminal,
            terminalAction: "stop",
            terminalStatus: StoppedStatus,
            exitCode: 0,
            standardOutput,
            standardError);
        terminal.Dispose();
        return result;
    }

    public Task<IReadOnlyList<BackgroundTerminalInfo>> ListBackgroundAsync(
        string? sessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        RemoveExpiredCompletedTerminals();

        string normalizedSessionId = NormalizeSessionId(sessionId);
        IReadOnlyList<BackgroundTerminalInfo> terminals = _backgroundTerminals.Values
            .Where(terminal => string.Equals(terminal.SessionId, normalizedSessionId, StringComparison.Ordinal))
            .Select(CreateBackgroundTerminalInfo)
            .OrderBy(static terminal => terminal.StartedAtUtc)
            .ThenBy(static terminal => terminal.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.FromResult(terminals);
    }

    private static ShellCommandExecutionRequest NormalizeRequest(ShellCommandExecutionRequest request)
    {
        string normalizedCommand = ShellCommandText.NormalizeCommandText(request.Command).Trim();
        return string.Equals(normalizedCommand, request.Command, StringComparison.Ordinal)
            ? request
            : request with { Command = normalizedCommand };
    }

    private PreparedShellCommand PrepareShellCommand(ShellCommandExecutionRequest request)
    {
        string workingDirectory = ResolveWorkspacePath(request.WorkingDirectory, directoryRequired: true);
        string workspaceRoot = Path.GetFullPath(_workspaceRootProvider.GetWorkspaceRoot());
        ToolSandboxMode effectiveSandboxMode = GetEffectiveSandboxMode(request);
        ProcessExecutionRequest shellRequest = OperatingSystem.IsWindows()
            ? CreateWindowsShellRequest(request, workingDirectory)
            : new ProcessExecutionRequest(
                "/bin/bash",
                ["-lc", request.Command],
                WorkingDirectory: workingDirectory,
                MaxOutputCharacters: MaxOutputCharacters,
                UsePseudoTerminal: request.PseudoTerminal);

        ShellCommandSandboxPlan sandboxPlan = ShellCommandSandboxPlanner.Create(
            shellRequest,
            effectiveSandboxMode,
            workspaceRoot,
            workingDirectory,
            WorkspaceRestrictedPathPolicy.LoadForSandbox(workspaceRoot));
        IReadOnlyDictionary<string, string> sandboxEnvironment = BuildSandboxEnvironment(
            request,
            workspaceRoot,
            effectiveSandboxMode,
            sandboxPlan.Enforcement);
        ProcessExecutionRequest processRequest = sandboxPlan.Request with
        {
            EnvironmentVariables = sandboxEnvironment
        };

        return new PreparedShellCommand(
            workingDirectory,
            effectiveSandboxMode,
            sandboxPlan,
            processRequest);
    }

    private static ProcessExecutionRequest CreateWindowsShellRequest(
        ShellCommandExecutionRequest request,
        string workingDirectory)
    {
        if (TryCreateWindowsCmdFastPathRequest(request, workingDirectory, out ProcessExecutionRequest? fastPathRequest))
        {
            return fastPathRequest!;
        }

        string commandText = BuildWindowsCommandText(request.Command);
        return new ProcessExecutionRequest(
            "powershell",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", commandText],
            WorkingDirectory: workingDirectory,
            MaxOutputCharacters: MaxOutputCharacters,
            UsePseudoTerminal: request.PseudoTerminal);
    }

    private static bool TryCreateWindowsCmdFastPathRequest(
        ShellCommandExecutionRequest request,
        string workingDirectory,
        out ProcessExecutionRequest? processRequest)
    {
        processRequest = null;

        if (request.PseudoTerminal ||
            ShellCommandText.ContainsControlSyntax(request.Command) ||
            !ShellCommandText.TryGetCommandName(request.Command, out string commandName) ||
            !IsWindowsCmdFastPathCommand(commandName))
        {
            return false;
        }

        processRequest = new ProcessExecutionRequest(
            GetWindowsCmdPath(),
            ["/d", "/s", "/c", request.Command],
            WorkingDirectory: workingDirectory,
            MaxOutputCharacters: MaxOutputCharacters);
        return true;
    }

    private static bool IsWindowsCmdFastPathCommand(string commandName)
    {
        if (WindowsCmdFastPathCommands.Contains(commandName))
        {
            return true;
        }

        string extension = Path.GetExtension(commandName);
        return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetWindowsCmdPath()
    {
        string systemDirectory = Environment.SystemDirectory;
        if (!string.IsNullOrWhiteSpace(systemDirectory))
        {
            string cmdPath = Path.Combine(systemDirectory, "cmd.exe");
            if (File.Exists(cmdPath))
            {
                return cmdPath;
            }
        }

        return "cmd.exe";
    }

    private ToolSandboxMode GetEffectiveSandboxMode(ShellCommandExecutionRequest request)
    {
        if (request.SandboxPermissions == ShellCommandSandboxPermissions.RequireEscalated)
        {
            return ToolSandboxMode.DangerFullAccess;
        }

        return _permissionSettings.SandboxMode;
    }

    private WindowsSandboxExecutionContext CreateWindowsSandboxExecutionContext(
        string workingDirectory,
        ToolSandboxMode effectiveSandboxMode)
    {
        string workspaceRoot = Path.GetFullPath(_workspaceRootProvider.GetWorkspaceRoot());
        IReadOnlyList<string> writableRoots = effectiveSandboxMode == ToolSandboxMode.WorkspaceWrite
            ? [workspaceRoot]
            : [];

        return new WindowsSandboxExecutionContext(
            effectiveSandboxMode,
            WindowsSandboxPaths.ResolveAppHome(),
            workspaceRoot,
            workingDirectory,
            writableRoots,
            IncludeTempEnvironmentVariables: effectiveSandboxMode == ToolSandboxMode.WorkspaceWrite,
            RestrictedPathPolicy: WorkspaceRestrictedPathPolicy.LoadForSandbox(workspaceRoot));
    }

    private ShellCommandExecutionResult CreateExecutionFailureResult(
        ShellCommandExecutionRequest request,
        string workingDirectory,
        string sandboxEnforcement,
        string standardError,
        bool background = false,
        string terminalAction = "run")
    {
        return new ShellCommandExecutionResult(
            request.Command,
            ToWorkspaceRelativePath(workingDirectory),
            126,
            string.Empty,
            TrimOutput(standardError),
            ShellCommandSandboxArguments.ToWireValue(request.SandboxPermissions),
            string.IsNullOrWhiteSpace(request.Justification)
                ? null
                : request.Justification.Trim(),
            ToWireValue(GetEffectiveSandboxMode(request)),
            sandboxEnforcement,
            request.PseudoTerminal,
            background,
            null,
            FailedStatus,
            terminalAction);
    }

    private BackgroundTerminal StartBackgroundTerminal(
        ShellCommandExecutionRequest request,
        PreparedShellCommand prepared)
    {
        ProcessStartInfo startInfo = CreateBackgroundStartInfo(prepared.ProcessRequest);
        Process process = new()
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        LocalProcessBackgroundHandle handle = new(process);
        BackgroundTerminal terminal = CreateBackgroundTerminal(request, prepared, handle);
        try
        {
            process.Start();
        }
        catch
        {
            terminal.Dispose();
            throw;
        }

        terminal.StartReaders();
        return terminal;
    }

    private BackgroundTerminal CreateBackgroundTerminal(
        ShellCommandExecutionRequest request,
        PreparedShellCommand prepared,
        IBackgroundProcessHandle handle)
    {
        string terminalId = "terminal-" + Interlocked.Increment(ref _backgroundTerminalSequence)
            .ToString("D", System.Globalization.CultureInfo.InvariantCulture);
        return new BackgroundTerminal(
            terminalId,
            NormalizeSessionId(request.SessionId),
            request.Command,
            ToWorkspaceRelativePath(prepared.WorkingDirectory),
            ShellCommandSandboxArguments.ToWireValue(request.SandboxPermissions),
            string.IsNullOrWhiteSpace(request.Justification)
                ? null
                : request.Justification.Trim(),
            ToWireValue(prepared.EffectiveSandboxMode),
            prepared.SandboxPlan.Enforcement,
            handle,
            _timeProvider);
    }

    private static ProcessStartInfo CreateBackgroundStartInfo(ProcessExecutionRequest request)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = request.FileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            startInfo.WorkingDirectory = request.WorkingDirectory;
        }

        foreach (string argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (request.EnvironmentVariables is not null)
        {
            foreach (KeyValuePair<string, string> environmentVariable in request.EnvironmentVariables)
            {
                if (!string.IsNullOrWhiteSpace(environmentVariable.Key))
                {
                    startInfo.Environment[environmentVariable.Key] = environmentVariable.Value;
                }
            }
        }

        return startInfo;
    }

    private static ShellCommandExecutionResult CreateBackgroundResult(
        BackgroundTerminal terminal,
        string terminalAction,
        string terminalStatus,
        int exitCode,
        string standardOutput,
        string standardError)
    {
        return new ShellCommandExecutionResult(
            terminal.Command,
            terminal.WorkingDirectory,
            exitCode,
            TrimBackgroundOutput(standardOutput),
            TrimBackgroundOutput(standardError),
            terminal.SandboxPermissions,
            terminal.Justification,
            terminal.SandboxMode,
            terminal.SandboxEnforcement,
            PseudoTerminal: false,
            Background: true,
            TerminalId: terminal.Id,
            TerminalStatus: terminalStatus,
            TerminalAction: terminalAction);
    }

    private static ShellCommandExecutionResult CreateBackgroundNotFoundResult(
        string terminalId,
        string terminalAction)
    {
        string normalizedTerminalId = NormalizeTerminalId(terminalId);
        return new ShellCommandExecutionResult(
            string.Empty,
            ".",
            127,
            string.Empty,
            $"Background terminal '{normalizedTerminalId}' was not found.",
            Background: true,
            TerminalId: normalizedTerminalId,
            TerminalStatus: NotFoundStatus,
            TerminalAction: terminalAction);
    }

    private BackgroundTerminalInfo CreateBackgroundTerminalInfo(BackgroundTerminal terminal)
    {
        string status = terminal.Status;
        int? exitCode = string.Equals(status, ExitedStatus, StringComparison.Ordinal)
            ? terminal.ExitCodeOrDefault()
            : null;
        DateTimeOffset? completedAtUtc = terminal.CompletedAtUtc;
        DateTimeOffset? expiresAtUtc = string.Equals(status, ExitedStatus, StringComparison.Ordinal) &&
                                       completedAtUtc is not null
            ? completedAtUtc.Value + _completedBackgroundTerminalTtl
            : null;

        return new BackgroundTerminalInfo(
            terminal.Id,
            terminal.SessionId,
            terminal.Command,
            terminal.WorkingDirectory,
            status,
            exitCode,
            terminal.StartedAtUtc,
            completedAtUtc,
            expiresAtUtc);
    }

    private bool TryGetBackgroundTerminal(
        string terminalId,
        out BackgroundTerminal? terminal)
    {
        return _backgroundTerminals.TryGetValue(
            NormalizeTerminalId(terminalId),
            out terminal);
    }

    private static bool IsOwnedBySession(BackgroundTerminal terminal, string? sessionId)
    {
        return string.Equals(
            terminal.SessionId,
            NormalizeSessionId(sessionId),
            StringComparison.Ordinal);
    }

    private void RemoveExpiredCompletedTerminals()
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        foreach (BackgroundTerminal terminal in _backgroundTerminals.Values)
        {
            if (!terminal.IsExpired(now, _completedBackgroundTerminalTtl))
            {
                continue;
            }

            if (_backgroundTerminals.TryRemove(terminal.Id, out BackgroundTerminal? removedTerminal))
            {
                removedTerminal.Dispose();
            }
        }
    }

    private void StopAllBackgroundTerminals()
    {
        foreach (KeyValuePair<string, BackgroundTerminal> item in _backgroundTerminals.ToArray())
        {
            if (!_backgroundTerminals.TryRemove(item.Key, out BackgroundTerminal? terminal))
            {
                continue;
            }

            try
            {
                terminal.Stop();
            }
            catch
            {
            }
            finally
            {
                terminal.Dispose();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        AppDomain.CurrentDomain.ProcessExit -= _processExitHandler;
        StopAllBackgroundTerminals();
    }

    private IReadOnlyDictionary<string, string> BuildSandboxEnvironment(
        ShellCommandExecutionRequest request,
        string workspaceRoot,
        ToolSandboxMode effectiveSandboxMode,
        string sandboxEnforcement)
    {
        Dictionary<string, string> environment = new(StringComparer.Ordinal)
        {
            ["STEMCODE_SANDBOX_MODE"] = ToWireValue(_permissionSettings.SandboxMode),
            ["STEMCODE_SANDBOX_EFFECTIVE_MODE"] = ToWireValue(effectiveSandboxMode),
            ["STEMCODE_SANDBOX_ENFORCEMENT"] = sandboxEnforcement,
            ["STEMCODE_SANDBOX_PERMISSIONS"] = ShellCommandSandboxArguments.ToWireValue(request.SandboxPermissions),
            ["STEMCODE_WORKSPACE_ROOT"] = workspaceRoot
        };

        if (!string.IsNullOrWhiteSpace(request.Justification))
        {
            environment["STEMCODE_SANDBOX_JUSTIFICATION"] = request.Justification.Trim();
        }

        if (request.PrefixRule is { Count: > 0 })
        {
            environment["STEMCODE_SANDBOX_PREFIX_RULE"] = string.Join(" ", request.PrefixRule);
        }

        if (request.PseudoTerminal)
        {
            environment["STEMCODE_SHELL_PTY"] = "1";
        }

        return environment;
    }

    private static string ToWireValue(ToolSandboxMode sandboxMode)
    {
        return sandboxMode switch
        {
            ToolSandboxMode.ReadOnly => "read-only",
            ToolSandboxMode.DangerFullAccess => "danger-full-access",
            _ => "workspace-write"
        };
    }

    private static string BuildWindowsCommandText(string commandText)
    {
        IReadOnlyList<ShellCommandSegment> segments = ShellCommandText.ParseSegments(commandText);
        if (segments.Count <= 1)
        {
            return commandText;
        }

        StringBuilder scriptBuilder = new();
        scriptBuilder.AppendLine("$ErrorActionPreference = 'Continue'");
        scriptBuilder.AppendLine("$__stemcode_exit = 0");
        scriptBuilder.AppendLine("$__stemcode_segment_exit = 0");

        for (int index = 0; index < segments.Count; index++)
        {
            ShellCommandSegment segment = segments[index];
            string encodedSegment = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(segment.CommandText));
            string invocation = CreateWindowsSegmentInvocation(encodedSegment);

            if (index == 0 || segment.Condition == ShellCommandSegmentCondition.Always)
            {
                scriptBuilder.AppendLine(invocation);
                continue;
            }

            if (segment.Condition == ShellCommandSegmentCondition.OnSuccess)
            {
                scriptBuilder.AppendLine($"if ($__stemcode_exit -eq 0) {{ {invocation} }}");
                continue;
            }

            scriptBuilder.AppendLine($"if ($__stemcode_exit -ne 0) {{ {invocation} }}");
        }

        scriptBuilder.AppendLine("exit $__stemcode_exit");
        return scriptBuilder.ToString();
    }

    private static string CreateWindowsSegmentInvocation(string encodedSegment)
    {
        StringBuilder invocation = new();
        invocation.AppendLine("$__stemcode_segment_exit = 0");
        invocation.AppendLine("Set-Variable -Name LASTEXITCODE -Scope Global -Value 0 -Force");
        invocation.AppendLine(
            $"$__stemcode_script_text = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('{encodedSegment}'))");
        invocation.AppendLine("try {");
        invocation.AppendLine("  . ([ScriptBlock]::Create($__stemcode_script_text))");
        invocation.AppendLine("  if (-not $?) { $__stemcode_segment_exit = 1 }");
        invocation.AppendLine("  else { $__stemcode_segment_exit = [int]$global:LASTEXITCODE }");
        invocation.AppendLine("}");
        invocation.AppendLine("catch {");
        invocation.AppendLine("  Write-Error $_");
        invocation.AppendLine("  $__stemcode_segment_exit = 1");
        invocation.AppendLine("}");
        invocation.Append("$__stemcode_exit = $__stemcode_segment_exit");
        return invocation.ToString();
    }

    private string ResolveWorkspacePath(
        string? requestedPath,
        bool directoryRequired)
    {
        string workspaceRoot = Path.GetFullPath(_workspaceRootProvider.GetWorkspaceRoot());
        string fullPath = WorkspaceResolvedPath.Resolve(
            workspaceRoot,
            requestedPath,
            ToolPathAccessKind.Write).CanonicalFullPath;

        if (directoryRequired && !Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException(
                $"Directory '{ToWorkspaceRelativePath(fullPath)}' does not exist.");
        }

        return fullPath;
    }

    private string ToWorkspaceRelativePath(string fullPath)
    {
        string workspaceRoot = Path.GetFullPath(_workspaceRootProvider.GetWorkspaceRoot());
        return WorkspacePath.ToRelativePath(workspaceRoot, fullPath);
    }

    private static string TrimOutput(string value)
    {
        return TrimOutput(value, MaxOutputCharacters);
    }

    private static string TrimBackgroundOutput(string value)
    {
        return TrimOutput(value, MaxBackgroundOutputCharacters);
    }

    private static string TrimOutput(
        string value,
        int maxCharacters)
    {
        string normalizedValue = value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (normalizedValue.Length <= maxCharacters)
        {
            return normalizedValue;
        }

        return normalizedValue[..maxCharacters] + "...";
    }

    private static string NormalizeTerminalId(string? terminalId)
    {
        return string.IsNullOrWhiteSpace(terminalId)
            ? string.Empty
            : terminalId.Trim();
    }

    private static string NormalizeSessionId(string? sessionId)
    {
        return string.IsNullOrWhiteSpace(sessionId)
            ? string.Empty
            : sessionId.Trim();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static bool IsSandboxRunnerEnforcement(string enforcement)
    {
        return string.Equals(
                   enforcement,
                   ShellCommandSandboxPlanner.BubblewrapEnforcement,
                   StringComparison.Ordinal) ||
               string.Equals(
                   enforcement,
                   ShellCommandSandboxPlanner.SandboxExecEnforcement,
                   StringComparison.Ordinal) ||
               string.Equals(
                   enforcement,
                   ShellCommandSandboxPlanner.WindowsSandboxEnforcement,
                   StringComparison.Ordinal);
    }

    private static bool IsWindowsSandboxEnforcement(string enforcement)
    {
        return string.Equals(
            enforcement,
            ShellCommandSandboxPlanner.WindowsSandboxEnforcement,
            StringComparison.Ordinal);
    }

    private sealed record PreparedShellCommand(
        string WorkingDirectory,
        ToolSandboxMode EffectiveSandboxMode,
        ShellCommandSandboxPlan SandboxPlan,
        ProcessExecutionRequest ProcessRequest);

    private sealed class BackgroundTerminal : IDisposable
    {
        private readonly StringBuilder _standardError = new();
        private readonly StringBuilder _standardOutput = new();
        private readonly object _syncRoot = new();
        private readonly TimeProvider _timeProvider;
        private readonly IBackgroundProcessHandle _handle;
        private DateTimeOffset? _completedAtUtc;
        private bool _stopped;
        private int _standardErrorCursor;
        private int _standardOutputCursor;

        public BackgroundTerminal(
            string id,
            string sessionId,
            string command,
            string workingDirectory,
            string sandboxPermissions,
            string? justification,
            string sandboxMode,
            string sandboxEnforcement,
            IBackgroundProcessHandle handle,
            TimeProvider timeProvider)
        {
            Id = id;
            SessionId = sessionId;
            Command = command;
            WorkingDirectory = workingDirectory;
            SandboxPermissions = sandboxPermissions;
            Justification = justification;
            SandboxMode = sandboxMode;
            SandboxEnforcement = sandboxEnforcement;
            _handle = handle;
            _timeProvider = timeProvider;
            StartedAtUtc = timeProvider.GetUtcNow();
            handle.Exited += (_, _) => MarkCompleted();
        }

        public string Command { get; }

        public DateTimeOffset? CompletedAtUtc
        {
            get
            {
                _ = Status;
                return _completedAtUtc;
            }
        }

        public string Id { get; }

        public bool IsRunning => string.Equals(Status, RunningStatus, StringComparison.Ordinal);

        public string? Justification { get; }

        public string SandboxEnforcement { get; }

        public string SandboxMode { get; }

        public string SandboxPermissions { get; }

        public string SessionId { get; }

        public DateTimeOffset StartedAtUtc { get; }

        public string Status
        {
            get
            {
                if (_stopped)
                {
                    return StoppedStatus;
                }

                if (HasExited())
                {
                    MarkCompleted();
                    return ExitedStatus;
                }

                return RunningStatus;
            }
        }

        public string WorkingDirectory { get; }

        public void StartReaders()
        {
            _handle.StartStreaming(
                AppendStandardOutput,
                AppendStandardError);
        }

        public (string StandardOutput, string StandardError) ReadNewOutput()
        {
            lock (_syncRoot)
            {
                string standardOutput = _standardOutputCursor >= _standardOutput.Length
                    ? string.Empty
                    : _standardOutput.ToString(_standardOutputCursor, _standardOutput.Length - _standardOutputCursor);
                string standardError = _standardErrorCursor >= _standardError.Length
                    ? string.Empty
                    : _standardError.ToString(_standardErrorCursor, _standardError.Length - _standardErrorCursor);
                _standardOutputCursor = _standardOutput.Length;
                _standardErrorCursor = _standardError.Length;
                return (standardOutput, standardError);
            }
        }

        public int ExitCodeOrDefault()
        {
            return HasExited()
                ? _handle.ExitCode
                : 0;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _stopped = true;
            _handle.Kill();
            await _handle.WaitForExitAsync(cancellationToken);
            await CompleteReadersAsync(cancellationToken);
        }

        public void Stop()
        {
            _stopped = true;
            _handle.Kill();
            _handle.WaitForExit(2_000);
        }

        public async Task CompleteReadersAsync(CancellationToken cancellationToken)
        {
            await _handle.CompleteStreamingAsync(cancellationToken);
        }

        public bool IsExpired(
            DateTimeOffset now,
            TimeSpan ttl)
        {
            if (string.Equals(Status, RunningStatus, StringComparison.Ordinal) ||
                _completedAtUtc is null)
            {
                return false;
            }

            return now - _completedAtUtc.Value >= ttl;
        }

        public void Dispose()
        {
            _handle.Dispose();
        }

        private bool HasExited()
        {
            return _handle.HasExited;
        }

        private void MarkCompleted()
        {
            if (_completedAtUtc is not null)
            {
                return;
            }

            lock (_syncRoot)
            {
                _completedAtUtc ??= _timeProvider.GetUtcNow();
            }
        }

        private void AppendStandardOutput(string value)
        {
            Append(_standardOutput, ref _standardOutputCursor, value);
        }

        private void AppendStandardError(string value)
        {
            Append(_standardError, ref _standardErrorCursor, value);
        }

        private void Append(
            StringBuilder builder,
            ref int cursor,
            string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            lock (_syncRoot)
            {
                builder.Append(value);
                if (builder.Length <= MaxBackgroundOutputCharacters)
                {
                    return;
                }

                int overflow = builder.Length - MaxBackgroundOutputCharacters;
                builder.Remove(0, overflow);
                cursor = Math.Max(0, cursor - overflow);
            }
        }
    }
}
