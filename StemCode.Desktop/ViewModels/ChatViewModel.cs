using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StemCode.Application.Abstractions;
using StemCode.Application.Backend;
using StemCode.Application.Exceptions;
using StemCode.Application.Models;
using StemCode.Desktop.Controls;
using StemCode.Desktop.Models;
using StemCode.Desktop.Services;
using System.Collections.ObjectModel;
using System.Globalization;

namespace StemCode.Desktop.ViewModels;

public partial class ChatViewModel : ViewModelBase, IAsyncDisposable, IDesktopTranscriptSink
{
    private const int MaxActivityEntries = 600;
    private static readonly string[] DefaultProfiles = ["build", "plan", "review"];

    private readonly AgentRunner _agentRunner;
    private readonly ProviderSetupRunner _providerSetupRunner;
    private readonly SettingsService _settingsService;
    private readonly DispatcherTimer _progressTimer;
    private readonly DispatcherTimer _streamTimer;
    private readonly DispatcherTimer _selectionPromptTimer;
    private readonly Dictionary<string, TranscriptItem> _toolItems = new(StringComparer.Ordinal);
    private bool _isDisposed;
    private DateTimeOffset? _currentRunStartedAt;
    private int? _activeModelContextWindowTokens;
    private bool _isApplyingSessionInfo;
    private CancellationTokenSource? _runCts;
    private TranscriptItem? _streamingItem;
    private TranscriptItem? _currentPlanItem;
    private int _turnStartIndex;
    private string? _loadedSessionId;
    private int _userTurnCount;
    private string? _targetSectionId;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isPromptRunning;

    [ObservableProperty]
    private bool _isProviderSetupActive;

    [ObservableProperty]
    private bool _isDedicatedProviderSetup;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private string? _retryText;

    [ObservableProperty]
    private string _workingLabel = "Working";

    [ObservableProperty]
    private string? _selectedModelId;

    [ObservableProperty]
    private bool _isThinkingEnabled;

    [ObservableProperty]
    private string? _selectedReasoningEffort;

    [ObservableProperty]
    private string? _selectedProfileName = "build";

    [ObservableProperty]
    private bool _hasSessionOptions;

    [ObservableProperty]
    private DesktopSelectionPrompt? _activeSelectionPrompt;

    [ObservableProperty]
    private DesktopTextPrompt? _activeTextPrompt;

    [ObservableProperty]
    private WorkspaceSectionInfo? _selectedSection;

    [ObservableProperty]
    private ProjectInfo? _activeProject;

    [ObservableProperty]
    private string _sessionTitle = "New session";

    [ObservableProperty]
    private string _providerName = string.Empty;

    [ObservableProperty]
    private int _contextUsedTokens;

    [ObservableProperty]
    private int _sessionOutputTokens;

    public ChatViewModel(AgentRunner? agentRunner, SettingsService settingsService)
    {
        _agentRunner = agentRunner ?? new AgentRunner(this);
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _providerSetupRunner = new ProviderSetupRunner();
        _agentRunner.SelectionPromptChanged += OnSelectionPromptChanged;
        _agentRunner.TextPromptChanged += OnTextPromptChanged;
        _agentRunner.ActivityReported += OnActivityReported;
        _providerSetupRunner.SelectionPromptChanged += OnSelectionPromptChanged;
        _providerSetupRunner.TextPromptChanged += OnTextPromptChanged;

        SendCommand = new AsyncRelayCommand(SendAsync, CanSend);
        StopCommand = new RelayCommand(Stop, () => IsRunning && _runCts is not null);
        ConfigureProviderCommand = new AsyncRelayCommand(ConfigureProviderAsync, () => !IsRunning);
        UndoCommand = new AsyncRelayCommand(() => RunSlashCommandAsync("/undo"), CanRunWorkspaceCommand);
        RedoCommand = new AsyncRelayCommand(() => RunSlashCommandAsync("/redo"), CanRunWorkspaceCommand);
        RunSlashCommand = new AsyncRelayCommand<string?>(command => RunSlashCommandAsync(command), _ => CanRunWorkspaceCommand());
        ForkFromCommand = new AsyncRelayCommand<TranscriptItem?>(ForkFromAsync, item => item?.TurnNumber is not null && CanRunWorkspaceCommand());
        RewindToCommand = new AsyncRelayCommand<TranscriptItem?>(RewindToAsync, item => item?.TurnNumber is not null && CanRunWorkspaceCommand());
        OpenChangedFileCommand = new RelayCommand<FileEditSummary?>(OpenChangedFile);

        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _progressTimer.Tick += (_, _) => UpdateProgressText();
        _streamTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _streamTimer.Tick += (_, _) => FlushStreaming();
        _selectionPromptTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _selectionPromptTimer.Tick += (_, _) => ActiveSelectionPrompt?.Tick();

        InitializeComposer();
    }

    public AgentRunner Runner => _agentRunner;

    public ObservableCollection<TranscriptItem> Transcript { get; } = new();

    public ObservableCollection<AgentEvent> Activity { get; } = new();

    public ObservableCollection<FileEditSummary> ChangedFiles { get; } = new();

    public ObservableCollection<PlanStep> PlanSteps { get; } = new();

    public ObservableCollection<string> AvailableModels { get; } = new();

    public ObservableCollection<string> ReasoningEfforts { get; } = new(ReasoningEffortOptions.SupportedValues);

    public ObservableCollection<string> ProfileOptions { get; } = new(DefaultProfiles);

    public IAsyncRelayCommand SendCommand { get; }

    public IRelayCommand StopCommand { get; }

    public IAsyncRelayCommand ConfigureProviderCommand { get; }

    public IAsyncRelayCommand UndoCommand { get; }

    public IAsyncRelayCommand RedoCommand { get; }

    public IAsyncRelayCommand<string?> RunSlashCommand { get; }

    public IAsyncRelayCommand<TranscriptItem?> ForkFromCommand { get; }

    public IAsyncRelayCommand<TranscriptItem?> RewindToCommand { get; }

    public IRelayCommand<FileEditSummary?> OpenChangedFileCommand { get; }

    /// <summary>Raised after any run so the shell can refresh sessions, changes and terminals.</summary>
    public event EventHandler? RunCompleted;

    /// <summary>Raised when a command switched the backend to a different session id.</summary>
    public event EventHandler<string>? SessionSwitched;

    /// <summary>Raised when the desktop-only /settings command is used.</summary>
    public event EventHandler? SettingsRequested;

    public bool HasChangedFiles => ChangedFiles.Count > 0;

    public bool HasPlan => PlanSteps.Count > 0;

    public bool HasTranscript => Transcript.Count > 0;

    public bool IsTranscriptEmpty => Transcript.Count == 0;

    public bool HasProject => ActiveProject is not null;

    public bool HasNoProject => ActiveProject is null;

    public bool HasActiveSelectionPrompt => ActiveSelectionPrompt is not null;

    public bool HasActiveTextPrompt => ActiveTextPrompt is not null;

    public bool HasInlineSelectionPrompt => ActiveSelectionPrompt is not null && !IsProviderSetupActive;

    public bool HasInlineTextPrompt => ActiveTextPrompt is not null && !IsProviderSetupActive;

    public bool HasOnboardingOverlay => IsProviderSetupActive;

    public bool HasOnboardingSelectionPrompt => IsProviderSetupActive && ActiveSelectionPrompt is not null;

    public bool HasOnboardingTextPrompt => IsProviderSetupActive && ActiveTextPrompt is not null;

    public bool HasOnboardingCountdown => ActiveSelectionPrompt?.HasCountdown == true;

    public bool CanCancelOnboardingSelectionPrompt => ActiveSelectionPrompt?.AllowCancellation == true;

    public bool CanCancelOnboardingTextPrompt => ActiveTextPrompt?.AllowCancellation == true;

    public bool IsOnboardingWorking => IsProviderSetupActive && ActiveSelectionPrompt is null && ActiveTextPrompt is null;

    public string OnboardingHeading => IsDedicatedProviderSetup ? "Set up StemCode" : "Starting session";

    public string OnboardingBlurb => IsDedicatedProviderSetup
        ? "Connect a model provider. Credentials stay in your platform's secret store."
        : "StemCode needs an answer before the session can start.";

    public string OnboardingStageText => !IsDedicatedProviderSetup ? "Startup" : ActiveSelectionPrompt is not null
        ? "Step 1 of 3"
        : ActiveTextPrompt is not null
            ? ActiveTextPrompt.IsSecret ? "Step 2 of 3" : "Step 1 of 3"
            : "Step 3 of 3";

    public string OnboardingTitle => ActiveSelectionPrompt?.Title ?? ActiveTextPrompt?.Title ?? "Checking provider setup";

    public string OnboardingDescription => ActiveSelectionPrompt?.Description ??
        ActiveTextPrompt?.Description ??
        "StemCode is validating the saved provider configuration and model list.";

    public string OnboardingInputKind => ActiveTextPrompt?.IsSecret == true ? "Secret" : "Input";

    public string StatusText => IsRunning
        ? IsPromptRunning ? WorkingLabel : "Busy"
        : HasSessionOptions ? "Ready" : "Not connected";

    public bool SessionOptionsEnabled => HasSessionOptions && !IsRunning;

    public bool HasRetryText => !string.IsNullOrWhiteSpace(RetryText);

    public string ModelDisplayText => string.IsNullOrWhiteSpace(SelectedModelId) ? "No model" : SelectedModelId!;

    public string ThinkingToggleText => IsThinkingEnabled ? "Thinking" : "No thinking";

    public bool HasContextWindow => _activeModelContextWindowTokens is > 0;

    public double ContextUsageFraction => _activeModelContextWindowTokens is > 0
        ? Math.Clamp(ContextUsedTokens / (double)_activeModelContextWindowTokens.Value, 0d, 1d)
        : 0d;

    public double ContextUsagePercent => Math.Round(ContextUsageFraction * 100d);

    public string ContextUsageText => _activeModelContextWindowTokens is > 0
        ? $"{FormatTokens(ContextUsedTokens)} / {FormatTokens(_activeModelContextWindowTokens.Value)} context ({ContextUsagePercent:0}%)"
        : $"{FormatTokens(ContextUsedTokens)} context tokens";

    public string ContextShortText => _activeModelContextWindowTokens is > 0 ? $"{ContextUsagePercent:0}%" : FormatTokens(ContextUsedTokens);

    /// <summary>Header line under the session title: workspace · provider · model · tokens.</summary>
    public string HeaderSubtitle
    {
        get
        {
            string meta = SessionMetaText;
            string workspace = ActiveProject?.Name ?? "No workspace";
            return meta.Length == 0 ? workspace : workspace + "  ·  " + meta;
        }
    }

    public string SessionMetaText
    {
        get
        {
            List<string> parts = [];
            if (!string.IsNullOrWhiteSpace(ProviderName))
            {
                parts.Add(ProviderName);
            }

            if (!string.IsNullOrWhiteSpace(SelectedModelId))
            {
                parts.Add(SelectedModelId!);
            }

            if (SessionOutputTokens > 0)
            {
                parts.Add($"{FormatTokens(SessionOutputTokens)} output tokens");
            }

            return string.Join("  ·  ", parts);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _runCts?.Cancel();
        _agentRunner.SelectionPromptChanged -= OnSelectionPromptChanged;
        _agentRunner.TextPromptChanged -= OnTextPromptChanged;
        _agentRunner.ActivityReported -= OnActivityReported;
        _providerSetupRunner.SelectionPromptChanged -= OnSelectionPromptChanged;
        _providerSetupRunner.TextPromptChanged -= OnTextPromptChanged;
        _progressTimer.Stop();
        _streamTimer.Stop();
        _selectionPromptTimer.Stop();

        await _agentRunner.DisposeAsync();
        await _providerSetupRunner.DisposeAsync();
    }

    // ---------------------------------------------------------------- property hooks

    partial void OnIsRunningChanged(bool value)
    {
        NotifyCommandStatesChanged();
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(SessionOptionsEnabled));
        NotifyOnboardingStateChanged();
    }

    partial void OnIsPromptRunningChanged(bool value) => OnPropertyChanged(nameof(StatusText));

    partial void OnWorkingLabelChanged(string value) => OnPropertyChanged(nameof(StatusText));

    partial void OnIsProviderSetupActiveChanged(bool value) => NotifyOnboardingStateChanged();

    partial void OnIsDedicatedProviderSetupChanged(bool value)
    {
        OnPropertyChanged(nameof(OnboardingHeading));
        OnPropertyChanged(nameof(OnboardingBlurb));
        OnPropertyChanged(nameof(OnboardingStageText));
    }

    partial void OnRetryTextChanged(string? value) => OnPropertyChanged(nameof(HasRetryText));

    partial void OnSelectedModelIdChanged(string? value)
    {
        OnPropertyChanged(nameof(ModelDisplayText));
        NotifyHeaderChanged();
        if (!_isApplyingSessionInfo && CanRunWorkspaceCommand() && !string.IsNullOrWhiteSpace(value))
        {
            _ = RunControlOperationAsync("Changing model", async () => await _agentRunner.SetModelAsync(ActiveProject!.Path, value, _targetSectionId));
        }
    }

    partial void OnSelectedReasoningEffortChanged(string? value)
    {
        if (!_isApplyingSessionInfo && CanRunWorkspaceCommand() && !string.IsNullOrWhiteSpace(value))
        {
            _ = RunControlOperationAsync("Changing reasoning effort", async () => await _agentRunner.SetReasoningAsync(ActiveProject!.Path, value, _targetSectionId));
        }
    }

    partial void OnIsThinkingEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(ThinkingToggleText));
        if (!_isApplyingSessionInfo && CanRunWorkspaceCommand())
        {
            _ = RunControlOperationAsync("Changing thinking", async () => await _agentRunner.SetThinkingAsync(ActiveProject!.Path, value ? "on" : "off", _targetSectionId));
        }
    }

    partial void OnSelectedProfileNameChanged(string? value)
    {
        if (!_isApplyingSessionInfo && CanRunWorkspaceCommand() && !string.IsNullOrWhiteSpace(value))
        {
            _ = RunControlOperationAsync("Changing profile", async () => await _agentRunner.SetProfileAsync(ActiveProject!.Path, value, _targetSectionId));
        }
    }

    partial void OnHasSessionOptionsChanged(bool value)
    {
        OnPropertyChanged(nameof(SessionOptionsEnabled));
        OnPropertyChanged(nameof(StatusText));
    }

    partial void OnActiveProjectChanged(ProjectInfo? value)
    {
        OnPropertyChanged(nameof(HasProject));
        OnPropertyChanged(nameof(HasNoProject));
        OnPropertyChanged(nameof(HeaderSubtitle));
        NotifyCommandStatesChanged();
        OnComposerProjectChanged();
    }

    partial void OnActiveSelectionPromptChanged(DesktopSelectionPrompt? value)
    {
        OnPropertyChanged(nameof(HasActiveSelectionPrompt));
        NotifyOnboardingStateChanged();

        if (value is not null && value.HasCountdown)
        {
            value.Tick();
            _selectionPromptTimer.Start();
            return;
        }

        _selectionPromptTimer.Stop();
    }

    partial void OnActiveTextPromptChanged(DesktopTextPrompt? value)
    {
        OnPropertyChanged(nameof(HasActiveTextPrompt));
        NotifyOnboardingStateChanged();
    }

    partial void OnProviderNameChanged(string value) => NotifyHeaderChanged();

    partial void OnSessionOutputTokensChanged(int value) => NotifyHeaderChanged();

    private void NotifyHeaderChanged()
    {
        OnPropertyChanged(nameof(SessionMetaText));
        OnPropertyChanged(nameof(HeaderSubtitle));
    }

    partial void OnContextUsedTokensChanged(int value) => NotifyContextChanged();

    private bool CanRunWorkspaceCommand() => !IsRunning && ActiveProject is not null;

    // ---------------------------------------------------------------- sessions

    public async Task LoadSessionAsync(ProjectInfo? project)
    {
        if (project is null || IsRunning)
        {
            return;
        }

        bool showSetupOverlay = !HasSessionOptions;
        if (showSetupOverlay)
        {
            IsProviderSetupActive = true;
        }

        try
        {
            _targetSectionId = SelectedSection?.SectionId;
            await RunControlOperationAsync("Loading session", async () =>
            {
                BackendSessionInfo sessionInfo = await _agentRunner.GetSessionAsync(project.Path, _targetSectionId);
                ApplySessionInfo(sessionInfo, replaceConversation: true, workspacePath: project.Path);
                AddActivity("session", $"Session loaded: {sessionInfo.SectionTitle}");
                return null;
            });
        }
        finally
        {
            if (showSetupOverlay)
            {
                IsProviderSetupActive = false;
            }
        }

        RefreshChangedFiles();
        _ = RefreshMcpPromptsAsync();
    }

    /// <summary>Starts a fresh section in the active workspace.</summary>
    public async Task StartNewSessionAsync(ProjectInfo? project)
    {
        if (project is null || IsRunning)
        {
            return;
        }

        SelectedSection = null;
        if (_agentRunner.IsInitialized &&
            string.Equals(_agentRunner.ActiveWorkingDirectory, Path.GetFullPath(project.Path), StringComparison.OrdinalIgnoreCase))
        {
            await RunCommandSilentlyAsync("/new");
        }
        else
        {
            await _agentRunner.ResetAsync();
            _targetSectionId = null;
            await LoadSessionAsync(project);
        }

        Transcript.Clear();
        PlanSteps.Clear();
        _userTurnCount = 0;
        NotifyTranscriptChanged();
        OnPropertyChanged(nameof(HasPlan));
        RefreshChangedFiles();
        RunCompleted?.Invoke(this, EventArgs.Empty);
    }

    public async Task EnsureProviderSetupAsync()
    {
        if (IsRunning)
        {
            return;
        }

        IsProviderSetupActive = true;
        IsDedicatedProviderSetup = true;
        try
        {
            await RunControlOperationAsync("Checking provider setup", async () =>
            {
                ProviderSetupRunResult result = await _providerSetupRunner.RunAsync();
                ProviderName = result.ProviderName;
                AddActivity("settings", $"Provider ready: {result.ProviderName} / {result.ModelId}");
                return null;
            });
        }
        finally
        {
            IsProviderSetupActive = false;
            IsDedicatedProviderSetup = false;
        }
    }

    private async Task ConfigureProviderAsync()
    {
        if (ActiveProject is null)
        {
            await EnsureProviderSetupAsync();
            return;
        }

        IsProviderSetupActive = true;
        IsDedicatedProviderSetup = true;
        try
        {
            await RunSlashCommandAsync("/onboard");
        }
        finally
        {
            IsProviderSetupActive = false;
            IsDedicatedProviderSetup = false;
        }
    }

    // ---------------------------------------------------------------- running

    private bool CanSend()
    {
        return ActiveProject is not null &&
            (!string.IsNullOrWhiteSpace(Prompt) || ComposerAttachments.Count > 0);
    }

    private async Task SendAsync()
    {
        ProjectInfo? project = ActiveProject;
        if (project is null)
        {
            return;
        }

        string text = Prompt.Trim();
        List<ComposerAttachment> attachments = ComposerAttachments.ToList();
        if (text.Length == 0 && attachments.Count == 0)
        {
            return;
        }

        if (text.Length == 0)
        {
            text = "See the attached file(s).";
        }

        RememberPrompt(text);
        Prompt = string.Empty;
        ComposerAttachments.Clear();
        DismissSuggestions();

        if (DesktopCommandCatalog.IsDesktopCommand(text, out string desktopCommand))
        {
            await RunDesktopCommandAsync(desktopCommand, text);
            return;
        }

        if (IsRunning)
        {
            QueuedPrompts.Add(new QueuedPrompt(text, attachments, prompt => QueuedPrompts.Remove(prompt)));
            OnPropertyChanged(nameof(HasQueuedPrompts));
            return;
        }

        await RunPromptAsync(project, text, attachments);
        await DrainQueueAsync();
    }

    private async Task DrainQueueAsync()
    {
        while (!IsRunning && QueuedPrompts.Count > 0 && ActiveProject is not null)
        {
            QueuedPrompt next = QueuedPrompts[0];
            QueuedPrompts.RemoveAt(0);
            OnPropertyChanged(nameof(HasQueuedPrompts));
            await RunPromptAsync(ActiveProject, next.Text, next.Attachments);
        }
    }

    private async Task RunDesktopCommandAsync(string command, string fullText)
    {
        switch (command.ToLowerInvariant())
        {
            case "/clear":
                Transcript.Clear();
                NotifyTranscriptChanged();
                return;
            case "/settings":
                SettingsRequested?.Invoke(this, EventArgs.Empty);
                return;
            default:
                Prompt = fullText;
                await TryHandleVoiceCommandAsync();
                return;
        }
    }

    /// <summary>Runs a slash command as if typed, showing it and its output in the transcript.</summary>
    public Task RunSlashCommandAsync(string? command)
    {
        if (string.IsNullOrWhiteSpace(command) || ActiveProject is null)
        {
            return Task.CompletedTask;
        }

        if (DesktopCommandCatalog.IsDesktopCommand(command, out string desktopCommand))
        {
            return RunDesktopCommandAsync(desktopCommand, command);
        }

        return IsRunning ? Task.CompletedTask : RunPromptAsync(ActiveProject, command.Trim(), []);
    }

    /// <summary>Runs a backend command without touching the transcript and returns its output (settings panels).</summary>
    public async Task<string> RunCommandCaptureAsync(string command)
    {
        if (ActiveProject is null)
        {
            return "Open a workspace first. Settings commands run against the active workspace session.";
        }

        if (IsRunning)
        {
            return "StemCode is busy. Wait for the current task to finish, or stop it.";
        }

        string output = string.Empty;
        await RunControlOperationAsync($"Running {command}", async () =>
        {
            AgentRunResult result = await _agentRunner.RunAsync(ActiveProject.Path, command, _targetSectionId);
            output = result.ResponseText;
            return result;
        });

        return output;
    }

    private Task RunCommandSilentlyAsync(string command)
    {
        return RunCommandCaptureAsync(command);
    }

    private async Task RunPromptAsync(ProjectInfo project, string text, IReadOnlyList<ComposerAttachment> attachments)
    {
        bool isCommand = IsBackendCommandText(project.Path, text);
        string? previousSessionId = _agentRunner.ActiveSessionId ?? _loadedSessionId;

        TranscriptItem userItem = new(
            TranscriptKind.User,
            text,
            project.Path,
            attachmentNames: attachments.Select(static attachment => attachment.Name).ToArray());
        if (!isCommand)
        {
            userItem.TurnNumber = ++_userTurnCount;
        }

        AddTranscript(userItem);
        _turnStartIndex = Transcript.Count;
        _streamingItem = null;
        _currentPlanItem = null;
        _toolItems.Clear();
        RetryText = null;
        WorkingLabel = isCommand ? "Running command" : "Thinking";

        _runCts = new CancellationTokenSource();
        _currentRunStartedAt = DateTimeOffset.UtcNow;
        IsPromptRunning = true;
        IsRunning = true;
        UpdateProgressText();
        _progressTimer.Start();
        _streamTimer.Start();
        AddActivity("task", isCommand ? $"Command {text}" : $"Prompt in {project.Name}");

        try
        {
            AgentRunResult result = await _agentRunner.RunAsync(
                project.Path,
                text,
                _targetSectionId,
                attachments.Select(static attachment => attachment.Attachment).ToArray(),
                _runCts.Token);

            FlushStreaming();
            CloseStreamingItem();

            bool sessionSwitched = result.SessionInfo is not null &&
                previousSessionId is not null &&
                !string.Equals(result.SessionInfo.SessionId, previousSessionId, StringComparison.OrdinalIgnoreCase);

            string? metrics = isCommand ? null : FormatRunMetrics(result);
            if (sessionSwitched)
            {
                ApplySessionInfo(result.SessionInfo, replaceConversation: true, workspacePath: project.Path);
                if (!string.IsNullOrWhiteSpace(result.ResponseText))
                {
                    AddTranscript(new TranscriptItem(TranscriptKind.Notice, result.ResponseText.Trim(), project.Path));
                }

                SessionSwitched?.Invoke(this, result.SessionInfo!.SessionId);
            }
            else
            {
                CompleteTurnOutput(result, isCommand, metrics, project.Path);
                ApplySessionInfo(result.SessionInfo, replaceConversation: false, workspacePath: project.Path);
            }

            AddActivity("done", isCommand ? "Command finished." : "Task finished.");
        }
        catch (PromptCancelledException)
        {
            FlushStreaming();
            CloseStreamingItem();
            MarkRunningToolsStopped();
            AddTranscript(new TranscriptItem(TranscriptKind.Notice, "Cancelled.", project.Path));
        }
        catch (OperationCanceledException)
        {
            FlushStreaming();
            CloseStreamingItem();
            MarkRunningToolsStopped();
            AddTranscript(new TranscriptItem(TranscriptKind.Notice, "Stopped.", project.Path));
            AddActivity("stopped", "Run stopped by user.");
        }
        catch (Exception exception)
        {
            FlushStreaming();
            CloseStreamingItem();
            MarkRunningToolsStopped();
            AddTranscript(new TranscriptItem(TranscriptKind.Error, exception.Message, project.Path));
            AddActivity("error", exception.ToString());
        }
        finally
        {
            _progressTimer.Stop();
            _streamTimer.Stop();
            _currentRunStartedAt = null;
            _runCts?.Dispose();
            _runCts = null;
            FollowBackendSession();
            RetryText = null;
            IsPromptRunning = false;
            IsRunning = false;
            try
            {
                RefreshChangedFiles();
            }
            catch (Exception exception)
            {
                AddActivity("error", $"Could not refresh changed files: {exception.Message}");
            }

            RunCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    private void CompleteTurnOutput(AgentRunResult result, bool isCommand, string? metrics, string workspacePath)
    {
        string finalText = result.ResponseText?.Trim() ?? string.Empty;

        TranscriptItem? lastAssistant = null;
        for (int index = Transcript.Count - 1; index >= _turnStartIndex && index >= 0; index--)
        {
            if (Transcript[index].IsAssistant)
            {
                lastAssistant = Transcript[index];
                break;
            }
        }

        if (lastAssistant is not null && !isCommand)
        {
            if (finalText.Length > 0)
            {
                lastAssistant.ReplaceContent(finalText);
            }

            lastAssistant.IsStreaming = false;
            lastAssistant.StatusNote = metrics;
            return;
        }

        if (finalText.Length == 0)
        {
            finalText = isCommand ? "Command completed." : "Done.";
        }

        AddTranscript(new TranscriptItem(isCommand ? TranscriptKind.Notice : TranscriptKind.Assistant, finalText, workspacePath)
        {
            StatusNote = metrics
        });
    }

    private void Stop()
    {
        if (_runCts is null)
        {
            return;
        }

        WorkingLabel = "Stopping";
        ActiveSelectionPrompt?.Dismiss();
        ActiveTextPrompt?.Dismiss();
        _runCts.Cancel();
    }

    /// <summary>Esc behaviour: dismiss popups first, then stop a running task.</summary>
    public bool HandleEscape()
    {
        if (HasSuggestions)
        {
            DismissSuggestions();
            return true;
        }

        if (ActiveSelectionPrompt?.TryCancel() == true)
        {
            return true;
        }

        if (IsRunning && _runCts is not null)
        {
            Stop();
            return true;
        }

        return false;
    }

    private async Task<AgentRunResult?> RunControlOperationAsync(string description, Func<Task<AgentRunResult?>> operation)
    {
        IsPromptRunning = false;
        IsRunning = true;
        AddActivity("settings", description);

        try
        {
            AgentRunResult? result = await operation();
            if (result is not null)
            {
                ApplySessionInfo(result.SessionInfo, workspacePath: ActiveProject?.Path);
            }

            return result;
        }
        catch (PromptCancelledException)
        {
            AddActivity("settings", $"Cancelled: {description}");
            AddNotice(IsProviderSetupActive
                ? "Provider setup was cancelled. Open Settings → Model & provider to connect one later."
                : $"Cancelled: {description.ToLowerInvariant()}.");
            return null;
        }
        catch (Exception exception)
        {
            AddTranscript(new TranscriptItem(TranscriptKind.Error, exception.Message, ActiveProject?.Path));
            AddActivity("error", exception.ToString());
            return null;
        }
        finally
        {
            FollowBackendSession();
            IsRunning = false;
        }
    }

    /// <summary>Session id the backend is bound to; the sidebar selects this after runs.</summary>
    public string? ActiveSessionId => _agentRunner.ActiveSessionId;

    private void FollowBackendSession()
    {
        if (!string.IsNullOrWhiteSpace(_agentRunner.ActiveSessionId))
        {
            _targetSectionId = _agentRunner.ActiveSessionId;
        }
    }

    private Task ForkFromAsync(TranscriptItem? item)
    {
        return item?.TurnNumber is int turn ? RunSlashCommandAsync($"/fork {turn}") : Task.CompletedTask;
    }

    private async Task RewindToAsync(TranscriptItem? item)
    {
        if (item?.TurnNumber is not int turn)
        {
            return;
        }

        string restoredPrompt = item.Content;
        await RunSlashCommandAsync($"/rewind {turn} both");
        if (string.IsNullOrWhiteSpace(Prompt))
        {
            Prompt = restoredPrompt;
        }
    }

    private static bool IsBackendCommandText(string workspacePath, string text)
    {
        if (!text.StartsWith('/') || McpPromptCommand.IsCandidate(text))
        {
            return false;
        }

        string name = text.Split(' ', 2)[0];
        try
        {
            return !StemCode.Application.Commands.CustomSlashCommandService.List(workspacePath)
                .Any(command => string.Equals(command.Command, name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return true;
        }
    }

    // ---------------------------------------------------------------- transcript sink (worker threads)

    void IDesktopTranscriptSink.OnAssistantChunk(string text) => Post(() =>
    {
        if (!IsPromptRunning)
        {
            return;
        }

        if (_streamingItem is null)
        {
            _streamingItem = new TranscriptItem(TranscriptKind.Assistant, string.Empty, ActiveProject?.Path) { IsStreaming = true };
            AddTranscript(_streamingItem);
            WorkingLabel = "Writing";
        }

        _streamingItem.AppendStream(text);
    });

    void IDesktopTranscriptSink.OnReasoning(string text) => Post(() =>
    {
        if (!IsPromptRunning)
        {
            return;
        }

        FlushStreaming();
        CloseStreamingItem();
        AddTranscript(new TranscriptItem(TranscriptKind.Thinking, text, ActiveProject?.Path));
    });

    void IDesktopTranscriptSink.OnToolCallsStarted(IReadOnlyList<DesktopToolCall> calls) => Post(() =>
    {
        if (!IsPromptRunning)
        {
            return;
        }

        FlushStreaming();
        CloseStreamingItem();
        foreach (DesktopToolCall call in calls)
        {
            if (string.Equals(call.Name, "update_plan", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            TranscriptItem item = new(TranscriptKind.Tool, string.Empty, ActiveProject?.Path, call.Title)
            {
                ToolCallId = call.Id,
                Status = ToolRunStatus.Running
            };
            _toolItems[call.Id] = item;
            AddTranscript(item);
        }

        WorkingLabel = calls.Count == 1 ? $"Running {Truncate(calls[0].Title, 60)}" : $"Running {calls.Count} tools";
    });

    void IDesktopTranscriptSink.OnToolCompleted(DesktopToolResult result) => Post(() =>
    {
        if (!IsPromptRunning)
        {
            return;
        }

        if (_toolItems.TryGetValue(result.Id, out TranscriptItem? item))
        {
            if (result.IsPlanUpdate)
            {
                Transcript.Remove(item);
                NotifyTranscriptChanged();
            }
            else
            {
                item.ReplaceContent(result.Text);
                item.Status = result.Succeeded ? ToolRunStatus.Succeeded : ToolRunStatus.Failed;
                item.IsExpanded = !result.Succeeded || ToolOutputLine.LooksLikeDiff(result.Text);
            }

            _toolItems.Remove(result.Id);
        }
        else if (!result.IsPlanUpdate && !string.IsNullOrWhiteSpace(result.Text))
        {
            AddTranscript(new TranscriptItem(TranscriptKind.Tool, result.Text, ActiveProject?.Path, result.Name)
            {
                Status = result.Succeeded ? ToolRunStatus.Succeeded : ToolRunStatus.Failed
            });
        }

        WorkingLabel = "Thinking";
    });

    void IDesktopTranscriptSink.OnPlan(ExecutionPlanProgress progress) => Post(() =>
    {
        IReadOnlyList<PlanStep> steps = PlanStep.From(progress);
        PlanSteps.Clear();
        foreach (PlanStep step in steps)
        {
            PlanSteps.Add(step);
        }

        OnPropertyChanged(nameof(HasPlan));

        if (!IsPromptRunning)
        {
            return;
        }

        if (_currentPlanItem is null)
        {
            FlushStreaming();
            CloseStreamingItem();
            _currentPlanItem = new TranscriptItem(TranscriptKind.Plan, string.Empty, ActiveProject?.Path);
            AddTranscript(_currentPlanItem);
        }

        _currentPlanItem.Steps = steps;
    });

    void IDesktopTranscriptSink.OnNotice(DesktopNoticeLevel level, string message) => Post(() =>
    {
        if (string.IsNullOrWhiteSpace(message) || !IsPromptRunning)
        {
            return;
        }

        AddTranscript(new TranscriptItem(
            level == DesktopNoticeLevel.Error ? TranscriptKind.Error : TranscriptKind.Notice,
            message.Trim(),
            ActiveProject?.Path));
    });

    void IDesktopTranscriptSink.OnProviderRetry(ProviderRetryProgress progress) => Post(() =>
    {
        RetryText = $"Retrying {progress.Attempt}/{progress.MaxAttempts} · {progress.Reason}";
    });

    private static void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.UIThread.Post(action);
    }

    private void FlushStreaming()
    {
        _streamingItem?.FlushStream();
    }

    private void CloseStreamingItem()
    {
        if (_streamingItem is not null)
        {
            _streamingItem.FlushStream();
            _streamingItem.IsStreaming = false;
            _streamingItem = null;
        }
    }

    private void MarkRunningToolsStopped()
    {
        foreach (TranscriptItem item in _toolItems.Values)
        {
            item.Status = ToolRunStatus.Failed;
            if (string.IsNullOrWhiteSpace(item.Content))
            {
                item.ReplaceContent("Interrupted before a result was returned.");
            }
        }

        _toolItems.Clear();
    }

    private void AddTranscript(TranscriptItem item)
    {
        Transcript.Add(item);
        NotifyTranscriptChanged();
    }

    private void NotifyTranscriptChanged()
    {
        OnPropertyChanged(nameof(HasTranscript));
        OnPropertyChanged(nameof(IsTranscriptEmpty));
    }

    // ---------------------------------------------------------------- session info

    private void ApplySessionInfo(BackendSessionInfo? sessionInfo, bool replaceConversation = false, string? workspacePath = null)
    {
        if (sessionInfo is null)
        {
            return;
        }

        _isApplyingSessionInfo = true;
        try
        {
            AvailableModels.Clear();
            foreach (string modelId in sessionInfo.AvailableModelIds)
            {
                AvailableModels.Add(modelId);
            }

            if (!string.IsNullOrWhiteSpace(sessionInfo.ModelId) && !AvailableModels.Contains(sessionInfo.ModelId))
            {
                AvailableModels.Insert(0, sessionInfo.ModelId);
            }

            string[] profiles = sessionInfo.AvailableAgentProfiles
                .Where(static profile => !string.Equals(profile.Mode, "subagent", StringComparison.OrdinalIgnoreCase))
                .Select(static profile => profile.Name)
                .ToArray();
            if (profiles.Length == 0)
            {
                profiles = DefaultProfiles;
            }

            if (!profiles.SequenceEqual(ProfileOptions, StringComparer.OrdinalIgnoreCase))
            {
                ProfileOptions.Clear();
                foreach (string profile in profiles)
                {
                    ProfileOptions.Add(profile);
                }
            }

            if (!ProfileOptions.Contains(sessionInfo.AgentProfileName))
            {
                ProfileOptions.Add(sessionInfo.AgentProfileName);
            }

            SelectedModelId = sessionInfo.ModelId;
            _activeModelContextWindowTokens = sessionInfo.ActiveModelContextWindowTokens;
            IsThinkingEnabled = string.Equals(sessionInfo.ThinkingMode, "on", StringComparison.OrdinalIgnoreCase);
            SelectedReasoningEffort = sessionInfo.ReasoningEffort;
            SelectedProfileName = sessionInfo.AgentProfileName;
            ProviderName = sessionInfo.ProviderName;
            SessionTitle = string.IsNullOrWhiteSpace(sessionInfo.SectionTitle) ? "New session" : sessionInfo.SectionTitle;
            ContextUsedTokens = sessionInfo.SectionEstimatedContextTokens;
            SessionOutputTokens = sessionInfo.TotalEstimatedOutputTokens;
            HasSessionOptions = true;
            NotifyContextChanged();
        }
        finally
        {
            _isApplyingSessionInfo = false;
        }

        if (replaceConversation)
        {
            ReplaceConversation(sessionInfo, workspacePath);
        }

        _loadedSessionId = sessionInfo.SessionId;
        NotifyCommandStatesChanged();
    }

    private void ReplaceConversation(BackendSessionInfo sessionInfo, string? workspacePath)
    {
        Transcript.Clear();
        PlanSteps.Clear();
        OnPropertyChanged(nameof(HasPlan));
        _userTurnCount = 0;

        foreach (BackendConversationMessage message in sessionInfo.ConversationHistory)
        {
            switch (message.Role)
            {
                case "user":
                    Transcript.Add(new TranscriptItem(TranscriptKind.User, message.Content, workspacePath)
                    {
                        TurnNumber = ++_userTurnCount
                    });
                    break;
                case "tool":
                    (string title, _) = SplitToolMessage(message.Content);
                    Transcript.Add(new TranscriptItem(TranscriptKind.Tool, message.Content.Trim(), workspacePath, title)
                    {
                        Status = ToolRunStatus.Succeeded
                    });
                    break;
                default:
                    if (!string.IsNullOrWhiteSpace(message.ReasoningContent) && sessionInfo.ShowThinking)
                    {
                        Transcript.Add(new TranscriptItem(TranscriptKind.Thinking, message.ReasoningContent!, workspacePath));
                    }

                    if (!string.IsNullOrWhiteSpace(message.Content))
                    {
                        Transcript.Add(new TranscriptItem(TranscriptKind.Assistant, message.Content, workspacePath));
                    }

                    break;
            }
        }

        NotifyTranscriptChanged();
    }

    private static (string Title, string Body) SplitToolMessage(string content)
    {
        string trimmed = content.Trim();
        int newline = trimmed.IndexOf('\n');
        if (newline < 0)
        {
            return (Truncate(trimmed.TrimStart('•', ' '), 120), string.Empty);
        }

        return (Truncate(trimmed[..newline].TrimStart('•', ' ').Trim(), 120), trimmed[(newline + 1)..]);
    }

    public void RefreshChangedFiles()
    {
        ChangedFiles.Clear();
        foreach (FileEditSummary file in _agentRunner.GetFileEditSummary())
        {
            ChangedFiles.Add(file);
        }

        OnPropertyChanged(nameof(HasChangedFiles));
    }

    private static void OpenChangedFile(FileEditSummary? file)
    {
        if (file is not null && !string.IsNullOrWhiteSpace(file.AbsolutePath))
        {
            MarkdownMessageView.OpenPath(file.AbsolutePath);
        }
    }

    private void NotifyCommandStatesChanged()
    {
        SendCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        ConfigureProviderCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        RunSlashCommand.NotifyCanExecuteChanged();
        ForkFromCommand.NotifyCanExecuteChanged();
        RewindToCommand.NotifyCanExecuteChanged();
    }

    private void NotifyOnboardingStateChanged()
    {
        OnPropertyChanged(nameof(HasInlineSelectionPrompt));
        OnPropertyChanged(nameof(HasInlineTextPrompt));
        OnPropertyChanged(nameof(HasOnboardingOverlay));
        OnPropertyChanged(nameof(HasOnboardingSelectionPrompt));
        OnPropertyChanged(nameof(HasOnboardingTextPrompt));
        OnPropertyChanged(nameof(HasOnboardingCountdown));
        OnPropertyChanged(nameof(CanCancelOnboardingSelectionPrompt));
        OnPropertyChanged(nameof(CanCancelOnboardingTextPrompt));
        OnPropertyChanged(nameof(IsOnboardingWorking));
        OnPropertyChanged(nameof(OnboardingStageText));
        OnPropertyChanged(nameof(OnboardingTitle));
        OnPropertyChanged(nameof(OnboardingDescription));
        OnPropertyChanged(nameof(OnboardingInputKind));
    }

    private void NotifyContextChanged()
    {
        OnPropertyChanged(nameof(HasContextWindow));
        OnPropertyChanged(nameof(ContextUsageFraction));
        OnPropertyChanged(nameof(ContextUsagePercent));
        OnPropertyChanged(nameof(ContextUsageText));
        OnPropertyChanged(nameof(ContextShortText));
    }

    private void OnSelectionPromptChanged(object? sender, DesktopSelectionPrompt? prompt) => Post(() => ActiveSelectionPrompt = prompt);

    private void OnTextPromptChanged(object? sender, DesktopTextPrompt? prompt) => Post(() => ActiveTextPrompt = prompt);

    private void OnActivityReported(object? sender, string message) => Post(() => AddActivity("agent", message));

    public void AddActivity(string kind, string message)
    {
        Activity.Add(new AgentEvent(kind, message, ActiveProject?.Path));
        while (Activity.Count > MaxActivityEntries)
        {
            Activity.RemoveAt(0);
        }
    }

    /// <summary>Adds a short app-level notice to the transcript (voice, attachments, ...).</summary>
    public void AddNotice(string message, bool isError = false)
    {
        AddTranscript(new TranscriptItem(isError ? TranscriptKind.Error : TranscriptKind.Notice, message, ActiveProject?.Path));
    }

    // ---------------------------------------------------------------- progress text

    private void UpdateProgressText()
    {
        if (_currentRunStartedAt is null)
        {
            ProgressText = string.Empty;
            return;
        }

        ProgressText = FormatElapsed(DateTimeOffset.UtcNow - _currentRunStartedAt.Value);
    }

    private string? FormatRunMetrics(AgentRunResult result)
    {
        List<string> parts = [];
        if (result.Elapsed is { } elapsed)
        {
            parts.Add(FormatElapsed(elapsed));
        }

        if (result.EstimatedTokens is int tokens && tokens > 0)
        {
            parts.Add($"{FormatTokens(tokens)} tokens");
        }

        if (result.EstimatedContextWindowUsedTokens is int used && _activeModelContextWindowTokens is > 0)
        {
            parts.Add($"{Math.Round(used * 100d / _activeModelContextWindowTokens.Value):0}% context");
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.FromSeconds(1))
        {
            return "<1s";
        }

        int seconds = Math.Max(0, (int)Math.Floor(elapsed.TotalSeconds));
        TimeSpan normalized = TimeSpan.FromSeconds(seconds);
        if (normalized.TotalHours >= 1d)
        {
            return $"{(int)normalized.TotalHours}h {normalized.Minutes}m";
        }

        return normalized.TotalMinutes >= 1d
            ? $"{(int)normalized.TotalMinutes}m {normalized.Seconds}s"
            : $"{normalized.Seconds}s";
    }

    internal static string FormatTokens(int tokens)
    {
        int safe = Math.Max(0, tokens);
        if (safe < 1_000)
        {
            return safe.ToString(CultureInfo.InvariantCulture);
        }

        if (safe < 1_000_000)
        {
            double thousands = safe / 1_000d;
            return thousands.ToString(thousands >= 10d ? "0" : "0.#", CultureInfo.InvariantCulture) + "k";
        }

        double millions = safe / 1_000_000d;
        return millions.ToString(millions >= 10d ? "0" : "0.#", CultureInfo.InvariantCulture) + "M";
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}
