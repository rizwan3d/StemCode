using StemCode.Application.Abstractions;
using StemCode.Application.Backend;
using StemCode.Application.Commands;
using StemCode.Application.Models;
using StemCode.Desktop.Models;
using DesktopChatMessage = StemCode.Desktop.Models.ChatMessage;

namespace StemCode.Desktop.Services;

public sealed class AgentRunner : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IStemCodeBackend? _backend;
    private DesktopUiBridge? _bridge;
    private List<string>? _currentActivity;
    private string? _sectionId;
    private string? _workingDirectory;

    public event EventHandler<DesktopChatMessage>? ConversationMessageReceived;

    public event EventHandler<DesktopSelectionPrompt?>? SelectionPromptChanged;

    public event EventHandler<DesktopTextPrompt?>? TextPromptChanged;

    public async Task<AgentRunResult> RunAsync(
        string workingDirectory,
        string prompt,
        string? sectionId = null,
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
                        ? new AgentRunOutput(customCommandError ?? "Custom command could not be expanded.")
                        : await RunTurnAsync(customCommand.ExpandedPrompt, cancellationToken);
                }
                else
                {
                    output = prompt.TrimStart().StartsWith("/", StringComparison.Ordinal) &&
                             !McpPromptCommand.IsCandidate(prompt)
                        ? await RunCommandAsync(prompt, activity, cancellationToken)
                        : await RunTurnAsync(prompt, cancellationToken);
                }

                return new AgentRunResult(
                    output.ResponseText,
                    activity,
                    output.ToolOutput,
                    output.Elapsed,
                    output.EstimatedTokens,
                    output.EstimatedContextWindowUsedTokens,
                    output.SessionInfo ?? sessionInfo);
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

    public Task<AgentRunResult> SetModelAsync(
        string workingDirectory,
        string modelId,
        string? sectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        return RunWorkspaceCommandAsync(
            workingDirectory,
            $"/use {modelId.Trim()}",
            $"Model: {modelId.Trim()}",
            sectionId,
            cancellationToken);
    }

    public Task<AgentRunResult> SetThinkingAsync(
        string workingDirectory,
        string thinkingMode,
        string? sectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(thinkingMode);
        return RunWorkspaceCommandAsync(
            workingDirectory,
            $"/thinking {thinkingMode.Trim()}",
            $"Thinking: {thinkingMode.Trim()}",
            sectionId,
            cancellationToken);
    }

    public Task<AgentRunResult> SetReasoningAsync(
        string workingDirectory,
        string reasoningEffort,
        string? sectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasoningEffort);
        return RunWorkspaceCommandAsync(
            workingDirectory,
            $"/reasoning {reasoningEffort.Trim()}",
            $"Reasoning effort: {reasoningEffort.Trim()}",
            sectionId,
            cancellationToken);
    }

    public Task<AgentRunResult> SetProfileAsync(
        string workingDirectory,
        string profileName,
        string? sectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        return RunWorkspaceCommandAsync(
            workingDirectory,
            $"/profile {profileName.Trim()}",
            $"Profile: {profileName.Trim()}",
            sectionId,
            cancellationToken);
    }

    /// <summary>Per-file added/removed line totals for the current conversation (empty before init).</summary>
    public IReadOnlyList<FileEditSummary> GetFileEditSummary()
        => _backend?.GetFileEditSummary() ?? [];

    public async ValueTask DisposeAsync()
    {
        if (_backend is not null)
        {
            await _backend.DisposeAsync();
            _backend = null;
        }

        _gate.Dispose();
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
            string.Equals(_sectionId, normalizedSectionId, StringComparison.OrdinalIgnoreCase))
        {
            return await RunInitializedSessionCommandAsync("/config", activity, cancellationToken, addActivity: false);
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
                AddBridgeConversationMessage,
                SetSelectionPrompt,
                SetTextPrompt);
            _backend = new StemCodeBackend(CreateBackendArgs(normalizedSectionId));

            BackendSessionInfo session = await _backend.InitializeAsync(_bridge, cancellationToken);
            _workingDirectory = workingDirectory;
            _sectionId = normalizedSectionId;
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
            throw;
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
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
                return await RunInitializedSessionCommandAsync(command, commandActivity, token, addActivity: true);
            },
            cancellationToken);

        activity.Add(activityMessage);

        return new AgentRunResult(
            "Updated.",
            activity,
            SessionInfo: sessionInfo);
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

    private async Task<AgentRunOutput> RunTurnAsync(string prompt, CancellationToken cancellationToken)
    {
        if (_backend is null || _bridge is null)
        {
            throw new InvalidOperationException("StemCode backend has not been initialized.");
        }

        ConversationTurnResult result = await _backend.RunTurnAsync(prompt, _bridge, cancellationToken);
        string responseText = string.IsNullOrWhiteSpace(result.ResponseText)
            ? "Task completed with no output."
            : result.ResponseText;

        return new AgentRunOutput(
            responseText,
            ToolOutput: [],
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

        return new AgentRunOutput(
            responseText,
            SessionInfo: result.SessionInfo);
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
        if (!string.IsNullOrWhiteSpace(message))
        {
            _currentActivity?.Add(message.Trim());
        }
    }

    private void AddBridgeConversationMessage(string role, string message)
    {
        if (!string.IsNullOrWhiteSpace(role) &&
            !string.IsNullOrWhiteSpace(message))
        {
            ConversationMessageReceived?.Invoke(
                this,
                new DesktopChatMessage(
                    role.Trim(),
                    message.Trim(),
                    statusNote: null,
                    workspacePath: _workingDirectory));
        }
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
            throw new ArgumentException(
                "Section id must be a valid GUID.",
                nameof(sectionId));
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
    IReadOnlyList<string>? ToolOutput = null,
    TimeSpan? Elapsed = null,
    int? EstimatedTokens = null,
    int? EstimatedContextWindowUsedTokens = null,
    BackendSessionInfo? SessionInfo = null);

public sealed record AgentRunResult(
    string ResponseText,
    IReadOnlyList<string> Activity,
    IReadOnlyList<string>? ToolOutput = null,
    TimeSpan? Elapsed = null,
    int? EstimatedTokens = null,
    int? EstimatedContextWindowUsedTokens = null,
    BackendSessionInfo? SessionInfo = null);
