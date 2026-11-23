using StemCode.Application.Abstractions;
using StemCode.Application.Backend;
using StemCode.Application.Commands;
using StemCode.Application.Models;
using StemCode.Application.Tools.Models;
using StemCode.Desktop.Models;

namespace StemCode.Desktop.Services;

/// <summary>
/// Owns the in-process StemCode backend for the active workspace/section and serialises access to it.
/// </summary>
public sealed class AgentRunner : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IDesktopTranscriptSink? _sink;
    private IStemCodeBackend? _backend;
    private DesktopUiBridge? _bridge;
    private List<string>? _currentActivity;
    private string? _sectionId;
    private string? _workingDirectory;

    public AgentRunner(IDesktopTranscriptSink? sink = null)
    {
        _sink = sink;
    }

    public event EventHandler<DesktopSelectionPrompt?>? SelectionPromptChanged;

    public event EventHandler<DesktopTextPrompt?>? TextPromptChanged;

    public event EventHandler<string>? ActivityReported;

    /// <summary>Session id the backend is currently bound to (may change after /new, /fork, /resume, ...).</summary>
    public string? ActiveSessionId { get; private set; }

    public string? ActiveWorkingDirectory => _workingDirectory;

    public bool IsInitialized => _backend is not null;

    public async Task<AgentRunResult> RunAsync(
        string workingDirectory,
        string prompt,
        string? sectionId = null,
        IReadOnlyList<ConversationAttachment>? attachments = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            List<string> activity = [];
            _currentActivity = activity;
            string normalizedDirectory = Path.GetFullPath(workingDirectory);

            BackendSessionInfo sessionInfo = await EnsureBackendAsync(
                normalizedDirectory,
                sectionId,
                activity,
                cancellationToken);

            string originalDirectory = Directory.GetCurrentDirectory();
            Directory.SetCurrentDirectory(normalizedDirectory);
            try
            {
                AgentRunOutput output;
                if (CustomSlashCommandService.TryExpand(
                        normalizedDirectory,
                        prompt,
                        out CustomSlashCommandResolution? customCommand,
                        out string? customCommandError))
                {
                    output = customCommand is null
                        ? new AgentRunOutput(customCommandError ?? "Custom command could not be expanded.", IsCommand: true)
                        : await RunTurnAsync(customCommand.ExpandedPrompt, attachments, cancellationToken);
                }
                else
                {
                    output = IsBackendCommand(prompt)
                        ? await RunCommandAsync(prompt, activity, cancellationToken)
                        : await RunTurnAsync(prompt, attachments, cancellationToken);
                }

                BackendSessionInfo? finalSession = output.SessionInfo;
                if (finalSession is null && !output.IsCommand)
                {
                    // Refresh token/session stats after a turn without adding activity noise.
                    finalSession = await RunInitializedSessionCommandAsync("/config", activity, CancellationToken.None, addActivity: false);
                }

                TrackSession(finalSession ?? sessionInfo);

                return new AgentRunResult(
                    output.ResponseText,
                    activity,
                    output.Elapsed,
                    output.EstimatedTokens,
                    output.EstimatedContextWindowUsedTokens,
                    finalSession ?? sessionInfo,
                    output.IsCommand);
            }
            finally
            {
                Directory.SetCurrentDirectory(originalDirectory);
            }
        }
        finally
        {
            _currentActivity = null;
            SetSelectionPrompt(null);
            SetTextPrompt(null);
            _gate.Release();
        }
    }

    public Task<BackendSessionInfo> GetSessionAsync(
        string workingDirectory,
        string? sectionId = null,
        CancellationToken cancellationToken = default)
    {
        return WithWorkspaceAsync(
            workingDirectory,
            [],
            (normalizedDirectory, activity, token) => EnsureBackendAsync(normalizedDirectory, sectionId, activity, token),
            cancellationToken);
    }

    public Task<AgentRunResult> SetModelAsync(string workingDirectory, string modelId, string? sectionId = null, CancellationToken cancellationToken = default)
        => RunWorkspaceCommandAsync(workingDirectory, $"/use {modelId.Trim()}", $"Model: {modelId.Trim()}", sectionId, cancellationToken);

    public Task<AgentRunResult> SetThinkingAsync(string workingDirectory, string thinkingMode, string? sectionId = null, CancellationToken cancellationToken = default)
        => RunWorkspaceCommandAsync(workingDirectory, $"/thinking {thinkingMode.Trim()}", $"Thinking: {thinkingMode.Trim()}", sectionId, cancellationToken);

    public Task<AgentRunResult> SetReasoningAsync(string workingDirectory, string reasoningEffort, string? sectionId = null, CancellationToken cancellationToken = default)
        => RunWorkspaceCommandAsync(workingDirectory, $"/reasoning {reasoningEffort.Trim()}", $"Reasoning effort: {reasoningEffort.Trim()}", sectionId, cancellationToken);

    public Task<AgentRunResult> SetProfileAsync(string workingDirectory, string profileName, string? sectionId = null, CancellationToken cancellationToken = default)
        => RunWorkspaceCommandAsync(workingDirectory, $"/profile {profileName.Trim()}", $"Profile: {profileName.Trim()}", sectionId, cancellationToken);

    /// <summary>Per-file added/removed line totals for the current conversation (empty before init).</summary>
    public IReadOnlyList<FileEditSummary> GetFileEditSummary()
        => _backend?.GetFileEditSummary() ?? [];

    public async Task<IReadOnlyList<BackgroundTerminalInfo>> ListBackgroundTerminalsAsync(CancellationToken cancellationToken = default)
    {
        IStemCodeBackend? backend = _backend;
        if (backend is null)
        {
            return [];
        }

        try
        {
            return await backend.ListBackgroundTerminalsAsync(cancellationToken);
        }
        catch (NotSupportedException)
        {
            return [];
        }
    }

    public async Task<ShellCommandExecutionResult?> ReadBackgroundTerminalAsync(string terminalId, CancellationToken cancellationToken = default)
    {
        IStemCodeBackend? backend = _backend;
        if (backend is null)
        {
            return null;
        }

        try
        {
            return await backend.ReadBackgroundTerminalAsync(terminalId, cancellationToken);
        }
        catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>MCP prompts exposed as /mcp__server__prompt commands. May connect to MCP servers, so call off the UI thread.</summary>
    public IReadOnlyList<McpPromptDescriptor> ListMcpPrompts()
    {
        try
        {
            return _backend?.ListMcpPrompts() ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_backend is not null)
        {
            await _backend.DisposeAsync();
            _backend = null;
        }

        _gate.Dispose();
    }

    private static bool IsBackendCommand(string prompt)
    {
        return prompt.TrimStart().StartsWith("/", StringComparison.Ordinal) &&
            !McpPromptCommand.IsCandidate(prompt);
    }

    private void TrackSession(BackendSessionInfo? sessionInfo)
    {
        if (sessionInfo is null || string.IsNullOrWhiteSpace(sessionInfo.SessionId))
        {
            return;
        }

        ActiveSessionId = sessionInfo.SessionId;

        // Commands such as /new, /fork, /clone, /resume, /rewind and /import switch the
        // backend to another section in place; follow it so the next call reuses the backend.
        if (_backend is not null && Guid.TryParse(sessionInfo.SessionId, out Guid parsed))
        {
            _sectionId = parsed.ToString("D");
        }
    }

    private async Task<BackendSessionInfo> EnsureBackendAsync(
        string workingDirectory,
        string? sectionId,
        List<string> activity,
        CancellationToken cancellationToken)
    {
        string? normalizedSectionId = NormalizeSectionIdOrNull(sectionId);

        if (_backend is not null &&
            string.Equals(_workingDirectory, workingDirectory, StringComparison.OrdinalIgnoreCase) &&
            (normalizedSectionId is null || string.Equals(_sectionId, normalizedSectionId, StringComparison.OrdinalIgnoreCase)))
        {
            BackendSessionInfo current = await RunInitializedSessionCommandAsync("/config", activity, cancellationToken, addActivity: false);
            TrackSession(current);
            return current;
        }

        if (_backend is not null)
        {
            await _backend.DisposeAsync();
            _backend = null;
            _bridge = null;
            _sectionId = null;
        }

        string originalDirectory = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(workingDirectory);
        try
        {
            _bridge = new DesktopUiBridge(
                AddBridgeActivity,
                _sink,
                SetSelectionPrompt,
                SetTextPrompt);
            _backend = new StemCodeBackend(CreateBackendArgs(normalizedSectionId));

            BackendSessionInfo session = await _backend.InitializeAsync(_bridge, cancellationToken);
            _workingDirectory = workingDirectory;
            _sectionId = normalizedSectionId;
            TrackSession(session);
            activity.Add($"Ready: {session.ProviderName} / {session.ModelId}");
            return session;
        }
        catch
        {
            if (_backend is not null)
            {
                await _backend.DisposeAsync();
                _backend = null;
            }

            _bridge = null;
            _sectionId = null;
            _workingDirectory = null;
            ActiveSessionId = null;
            throw;
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
        }
    }

    /// <summary>Discards the backend so the next call starts a fresh section.</summary>
    public async Task ResetAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_backend is not null)
            {
                await _backend.DisposeAsync();
            }

            _backend = null;
            _bridge = null;
            _sectionId = null;
            _workingDirectory = null;
            ActiveSessionId = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AgentRunResult> RunWorkspaceCommandAsync(
        string workingDirectory,
        string command,
        string activityMessage,
        string? sectionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        List<string> activity = [];
        BackendSessionInfo sessionInfo = await WithWorkspaceAsync(
            workingDirectory,
            activity,
            async (_, commandActivity, token) =>
            {
                await EnsureBackendAsync(Path.GetFullPath(workingDirectory), sectionId, commandActivity, token);
                BackendSessionInfo info = await RunInitializedSessionCommandAsync(command, commandActivity, token, addActivity: true);
                TrackSession(info);
                return info;
            },
            cancellationToken);

        activity.Add(activityMessage);

        return new AgentRunResult("Updated.", activity, SessionInfo: sessionInfo, IsCommand: true);
    }

    private async Task<T> WithWorkspaceAsync<T>(
        string workingDirectory,
        List<string> activity,
        Func<string, List<string>, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _currentActivity = activity;
            string normalizedDirectory = Path.GetFullPath(workingDirectory);
            string originalDirectory = Directory.GetCurrentDirectory();
            Directory.SetCurrentDirectory(normalizedDirectory);
            try
            {
                return await action(normalizedDirectory, activity, cancellationToken);
            }
            finally
            {
                Directory.SetCurrentDirectory(originalDirectory);
            }
        }
        finally
        {
            _currentActivity = null;
            SetSelectionPrompt(null);
            SetTextPrompt(null);
            _gate.Release();
        }
    }

    private async Task<AgentRunOutput> RunTurnAsync(
        string prompt,
        IReadOnlyList<ConversationAttachment>? attachments,
        CancellationToken cancellationToken)
    {
        if (_backend is null || _bridge is null)
        {
            throw new InvalidOperationException("StemCode backend has not been initialized.");
        }

        ConversationTurnResult result = attachments is { Count: > 0 }
            ? await _backend.RunTurnAsync(prompt, attachments, _bridge, cancellationToken)
            : await _backend.RunTurnAsync(prompt, _bridge, cancellationToken);

        return new AgentRunOutput(
            result.ResponseText ?? string.Empty,
            IsCommand: false,
            result.Metrics?.Elapsed,
            result.Metrics?.DisplayedEstimatedOutputTokens,
            result.Metrics?.EstimatedTotalTokens);
    }

    private async Task<AgentRunOutput> RunCommandAsync(
        string command,
        List<string> activity,
        CancellationToken cancellationToken)
    {
        if (_backend is null)
        {
            throw new InvalidOperationException("StemCode backend has not been initialized.");
        }

        BackendCommandResult result = await _backend.RunCommandAsync(command, cancellationToken);
        activity.Add($"Command: {command}");

        if (result.CommandResult.ExitRequested)
        {
            await _backend.DisposeAsync();
            _backend = null;
            _bridge = null;
            _sectionId = null;
            _workingDirectory = null;
        }

        string responseText = string.IsNullOrWhiteSpace(result.CommandResult.Message)
            ? "Command completed."
            : result.CommandResult.Message;

        return new AgentRunOutput(responseText, IsCommand: true, SessionInfo: result.SessionInfo);
    }

    private async Task<BackendSessionInfo> RunInitializedSessionCommandAsync(
        string command,
        List<string> activity,
        CancellationToken cancellationToken,
        bool addActivity)
    {
        if (_backend is null)
        {
            throw new InvalidOperationException("StemCode backend has not been initialized.");
        }

        BackendCommandResult result = await _backend.RunCommandAsync(command, cancellationToken);
        if (addActivity && !string.IsNullOrWhiteSpace(result.CommandResult.Message))
        {
            activity.Add(result.CommandResult.Message);
        }

        return result.SessionInfo;
    }

    private void AddBridgeActivity(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        _currentActivity?.Add(message.Trim());
        ActivityReported?.Invoke(this, message.Trim());
    }

    private static string[] CreateBackendArgs(string? sectionId)
    {
        return string.IsNullOrWhiteSpace(sectionId)
            ? ["--surface", BackendRuntimeOptions.DesktopSurface, "--startup-prompts", "enabled"]
            : ["--section", sectionId, "--surface", BackendRuntimeOptions.DesktopSurface, "--startup-prompts", "enabled"];
    }

    private static string? NormalizeSectionIdOrNull(string? sectionId)
    {
        if (string.IsNullOrWhiteSpace(sectionId))
        {
            return null;
        }

        if (!Guid.TryParse(sectionId.Trim(), out Guid parsedSectionId))
        {
            throw new ArgumentException("Section id must be a valid GUID.", nameof(sectionId));
        }

        return parsedSectionId.ToString("D");
    }

    private void SetSelectionPrompt(DesktopSelectionPrompt? prompt)
    {
        SelectionPromptChanged?.Invoke(this, prompt);
    }

    private void SetTextPrompt(DesktopTextPrompt? prompt)
    {
        TextPromptChanged?.Invoke(this, prompt);
    }
}

internal sealed record AgentRunOutput(
    string ResponseText,
    bool IsCommand,
    TimeSpan? Elapsed = null,
    int? EstimatedTokens = null,
    int? EstimatedContextWindowUsedTokens = null,
    BackendSessionInfo? SessionInfo = null);

public sealed record AgentRunResult(
    string ResponseText,
    IReadOnlyList<string> Activity,
    TimeSpan? Elapsed = null,
    int? EstimatedTokens = null,
    int? EstimatedContextWindowUsedTokens = null,
    BackendSessionInfo? SessionInfo = null,
    bool IsCommand = false);
