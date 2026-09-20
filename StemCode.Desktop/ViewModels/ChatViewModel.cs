using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StemCode.Application.Backend;
using StemCode.Application.Commands;
using StemCode.Application.Models;
using StemCode.Desktop.Controls;
using StemCode.Desktop.Models;
using StemCode.Desktop.Services;
using System.Collections.ObjectModel;
using System.Globalization;

namespace StemCode.Desktop.ViewModels;

public partial class ChatViewModel : ViewModelBase, IAsyncDisposable
{
    private const string BudgetControlsLocalPath = BudgetControlsSettings.DefaultLocalPath;
    private const double EstimatedLiveTokensPerSecond = 4d;
    private const int MaxPromptCommandSuggestionCount = 8;

    private static readonly DesktopCommandSuggestionDescriptor[] PromptCommandSuggestionDescriptors =
    [
        new("/allow", "/allow <tool-or-tag> [pattern]", "Add a session-scoped allow override.", true),
        new("/budget", "/budget [status|local|cloud]", "Show or configure budget controls.", false),
        new("/config", "/config", "Show provider, session, profile, thinking, and model details.", false),
        new("/context-size", "/context-size [show|auto|8k|32k|64k|125k|256k|<tokens>]", "Show, set, or clear the session context window cap.", true),
        new("/deny", "/deny <tool-or-tag> [pattern]", "Add a session-scoped deny override.", true),
        new("/help", "/help", "List available commands and usage.", false),
        new("/init", "/init [recommended|minimal|custom]", "Choose workspace-local StemCode files.", false),
        new("/mcp", "/mcp", "Show configured MCP servers and dynamic tools.", false),
        new("/onboard", "/onboard", "Re-run provider onboarding.", false),
        new("/permissions", "/permissions", "Show permission policy and override guidance.", false),
        new("/profile", "/profile <name>", "Switch the active agent profile.", true),
        new("/redo", "/redo", "Re-apply the most recently undone file edit.", false),
        new("/reasoning", "/reasoning [show|<none|minimal|low|medium|high|xhigh|max>]", "Show or set provider reasoning effort.", false),
        new("/rules", "/rules", "List effective permission rules.", false),
        new("/thinking", "/thinking [on|off]", "Show or set simple thinking mode.", false),
        new("/undo", "/undo", "Roll back the most recent tracked file edit.", false),
        new("/update", "/update [now]", "Check for updates.", false),
        new("/use", "/use <model>", "Switch the active model directly.", true)
    ];

    private readonly AgentRunner _agentRunner;
    private readonly ProviderSetupRunner _providerSetupRunner;
    private readonly SettingsService _settingsService;
    private readonly DispatcherTimer _progressTimer;
    private readonly DispatcherTimer _selectionPromptTimer;
    private readonly EventHandler _progressTimerTick;
    private readonly EventHandler _selectionPromptTimerTick;
    private bool _isDisposed;
    private DateTimeOffset? _currentRunStartedAt;
    private int? _activeModelContextWindowTokens;
    private bool _isApplyingSessionInfo;
    private bool _isApplyingPromptCommandSuggestion;
    private bool _promptCommandSuggestionsDismissed;
    private int _selectedPromptCommandSuggestionIndex;

    [ObservableProperty]
    private string _prompt = string.Empty;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isPromptRunning;

    [ObservableProperty]
    private bool _isProviderSetupActive;

    [ObservableProperty]
    private string _progressText = "(0s \u00B7 0 tokens)";

    [ObservableProperty]
    private string? _selectedModelId;

    [ObservableProperty]
    private string? _selectedThinkingMode = "off";

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
    private string _permissionToolPattern = string.Empty;

    [ObservableProperty]
    private string _permissionSubjectPattern = string.Empty;

    [ObservableProperty]
    private string? _selectedBudgetControlSource;

    [ObservableProperty]
    private string _budgetControlStatusText = "Local budget controls";

    [ObservableProperty]
    private string _budgetControlDetailText = BudgetControlsLocalPath;

    public ChatViewModel(
        AgentRunner agentRunner,
        SettingsService settingsService)
    {
        _agentRunner = agentRunner ?? throw new ArgumentNullException(nameof(agentRunner));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _providerSetupRunner = new ProviderSetupRunner();
        _agentRunner.ConversationMessageReceived += OnConversationMessageReceived;
        _agentRunner.SelectionPromptChanged += OnSelectionPromptChanged;
        _agentRunner.TextPromptChanged += OnTextPromptChanged;
        _providerSetupRunner.ConversationMessageReceived += OnConversationMessageReceived;
        _providerSetupRunner.SelectionPromptChanged += OnSelectionPromptChanged;
        _providerSetupRunner.TextPromptChanged += OnTextPromptChanged;
        RunPromptCommand = new AsyncRelayCommand<ProjectInfo?>(RunPromptAsync, CanRunPrompt);
        LoadSessionCommand = new AsyncRelayCommand<ProjectInfo?>(LoadSessionAsync, CanRunWorkspaceCommand);
        ApplyModelCommand = new AsyncRelayCommand<ProjectInfo?>(ApplyModelAsync, CanApplyModel);
        ApplyThinkingCommand = new AsyncRelayCommand<ProjectInfo?>(ApplyThinkingAsync, CanApplyThinking);
        ApplyReasoningCommand = new AsyncRelayCommand<ProjectInfo?>(ApplyReasoningAsync, CanApplyReasoning);
        ApplyProfileCommand = new AsyncRelayCommand<ProjectInfo?>(ApplyProfileAsync, CanApplyProfile);
        ConfigureProviderCommand = new AsyncRelayCommand<ProjectInfo?>(ConfigureProviderAsync, CanConfigureProvider);
        ShowHelpCommand = new AsyncRelayCommand<ProjectInfo?>(ShowHelpAsync, CanRunWorkspaceCommand);
        ShowPermissionsCommand = new AsyncRelayCommand<ProjectInfo?>(ShowPermissionsAsync, CanRunWorkspaceCommand);
        ShowRulesCommand = new AsyncRelayCommand<ProjectInfo?>(ShowRulesAsync, CanRunWorkspaceCommand);
        UndoCommand = new AsyncRelayCommand<ProjectInfo?>(UndoAsync, CanRunWorkspaceCommand);
        RedoCommand = new AsyncRelayCommand<ProjectInfo?>(RedoAsync, CanRunWorkspaceCommand);
        AllowPermissionCommand = new AsyncRelayCommand<ProjectInfo?>(AllowPermissionAsync, CanApplyPermissionOverride);
        DenyPermissionCommand = new AsyncRelayCommand<ProjectInfo?>(DenyPermissionAsync, CanApplyPermissionOverride);
        ConfigureBudgetControlsCommand = new AsyncRelayCommand<ProjectInfo?>(ConfigureBudgetControlsAsync, CanConfigureBudgetControls);
        OpenChangedFileCommand = new RelayCommand<FileEditSummary?>(OpenChangedFile);
        _progressTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _progressTimerTick = (_, _) => UpdateProgressText();
        _progressTimer.Tick += _progressTimerTick;
        _selectionPromptTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _selectionPromptTimerTick = (_, _) => ActiveSelectionPrompt?.Tick();
        _selectionPromptTimer.Tick += _selectionPromptTimerTick;

        ApplyBudgetControlsSettings(_settingsService.LoadBudgetControls());
        Messages.Add(new ChatMessage("StemCode", "Ready."));
        Activity.Add(new AgentEvent("idle", "Idle"));
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        _agentRunner.ConversationMessageReceived -= OnConversationMessageReceived;
        _agentRunner.SelectionPromptChanged -= OnSelectionPromptChanged;
        _agentRunner.TextPromptChanged -= OnTextPromptChanged;
        _providerSetupRunner.ConversationMessageReceived -= OnConversationMessageReceived;
        _providerSetupRunner.SelectionPromptChanged -= OnSelectionPromptChanged;
        _providerSetupRunner.TextPromptChanged -= OnTextPromptChanged;

        _progressTimer.Stop();
        _progressTimer.Tick -= _progressTimerTick;
        _selectionPromptTimer.Stop();
        _selectionPromptTimer.Tick -= _selectionPromptTimerTick;

        await _agentRunner.DisposeAsync();
        await _providerSetupRunner.DisposeAsync();
    }

    public ObservableCollection<ChatMessage> Messages { get; } = new();

    public ObservableCollection<AgentEvent> Activity { get; } = new();

    public ObservableCollection<FileEditSummary> ChangedFiles { get; } = new();

    public bool HasChangedFiles => ChangedFiles.Count > 0;

    public ObservableCollection<string> AvailableModels { get; } = new();

    public ObservableCollection<DesktopCommandSuggestion> PromptCommandSuggestions { get; } = new();

    public ObservableCollection<string> ThinkingModes { get; } = new(["off", "on"]);

    public ObservableCollection<string> ReasoningEfforts { get; } = new(ReasoningEffortOptions.SupportedValues);

    public ObservableCollection<string> ProfileOptions { get; } = new(["build", "plan", "review"]);

    public ObservableCollection<string> BudgetControlSources { get; } = new(
    [
        BudgetControlsSettings.LocalSource,
        BudgetControlsSettings.CloudSource
    ]);

    public IAsyncRelayCommand<ProjectInfo?> RunPromptCommand { get; }

    public IAsyncRelayCommand<ProjectInfo?> LoadSessionCommand { get; }

    public IAsyncRelayCommand<ProjectInfo?> ApplyModelCommand { get; }

    public IAsyncRelayCommand<ProjectInfo?> ApplyThinkingCommand { get; }

    public IAsyncRelayCommand<ProjectInfo?> ApplyReasoningCommand { get; }

    public IAsyncRelayCommand<ProjectInfo?> ApplyProfileCommand { get; }

    public IAsyncRelayCommand<ProjectInfo?> ConfigureProviderCommand { get; }

    public IAsyncRelayCommand<ProjectInfo?> ShowHelpCommand { get; }

    public IAsyncRelayCommand<ProjectInfo?> ShowPermissionsCommand { get; }

    public IAsyncRelayCommand<ProjectInfo?> ShowRulesCommand { get; }

    public IAsyncRelayCommand<ProjectInfo?> UndoCommand { get; }

    public IAsyncRelayCommand<ProjectInfo?> RedoCommand { get; }

    public IAsyncRelayCommand<ProjectInfo?> AllowPermissionCommand { get; }

    public IAsyncRelayCommand<ProjectInfo?> DenyPermissionCommand { get; }

    public IAsyncRelayCommand<ProjectInfo?> ConfigureBudgetControlsCommand { get; }

    public IRelayCommand<FileEditSummary?> OpenChangedFileCommand { get; }

    public event EventHandler? RunCompleted;

    public bool HasActiveSelectionPrompt => ActiveSelectionPrompt is not null;

    public bool HasActiveTextPrompt => ActiveTextPrompt is not null;

    public bool HasFloatingSelectionPrompt => ActiveSelectionPrompt is not null && !IsProviderSetupActive;

    public bool HasFloatingTextPrompt => ActiveTextPrompt is not null && !IsProviderSetupActive;

    public bool HasOnboardingOverlay => IsProviderSetupActive;

    public bool HasOnboardingSelectionPrompt => IsProviderSetupActive && ActiveSelectionPrompt is not null;

    public bool HasOnboardingTextPrompt => IsProviderSetupActive && ActiveTextPrompt is not null;

    public bool HasOnboardingCountdown => ActiveSelectionPrompt?.HasCountdown == true;

    public bool BudgetControlsEnabled => !IsRunning;

    public bool HasBudgetControlDetail => !string.IsNullOrWhiteSpace(BudgetControlDetailText);

    public string BudgetControlActionText => string.Equals(
        SelectedBudgetControlSource,
        BudgetControlsSettings.CloudSource,
        StringComparison.OrdinalIgnoreCase)
            ? "Connect"
            : "Use local";

    public bool CanCancelOnboardingSelectionPrompt => ActiveSelectionPrompt?.AllowCancellation == true;

    public bool CanCancelOnboardingTextPrompt => ActiveTextPrompt?.AllowCancellation == true;

    public string OnboardingStageText
    {
        get
        {
            if (ActiveSelectionPrompt is not null)
            {
                return "Step 1 of 3";
            }

            if (ActiveTextPrompt is not null)
            {
                return ActiveTextPrompt.IsSecret ? "Step 2 of 3" : "Step 1 of 3";
            }

            return IsRunning ? "Step 3 of 3" : "Provider setup";
        }
    }

    public string OnboardingTitle => ActiveSelectionPrompt?.Title ??
        ActiveTextPrompt?.Title ??
        "Checking provider setup";

    public string OnboardingDescription => ActiveSelectionPrompt?.Description ??
        ActiveTextPrompt?.Description ??
        "StemCode is validating the saved provider configuration and model list.";

    public string OnboardingInputKind => ActiveTextPrompt?.IsSecret == true
        ? "Secret"
        : "Input";

    public string OnboardingStatusText => IsRunning ? "Working" : "Provider setup";

    public bool HasPromptProgress => IsPromptRunning;

    public bool HasPromptCommandSuggestions => PromptCommandSuggestions.Count > 0;

    public string StatusText => IsRunning
        ? IsPromptRunning ? $"Working {ProgressText}" : "Working"
        : "Ready";

    public bool SessionOptionsEnabled => HasSessionOptions && !IsRunning;

    public string ThinkingToggleText => IsThinkingEnabled ? "Thinking On" : "Thinking Off";

    public string ReasoningStatusText => string.IsNullOrWhiteSpace(SelectedReasoningEffort)
        ? IsThinkingEnabled
            ? "Provider default reasoning effort. Explicit effort support depends on the provider and model."
            : "Thinking is off. StemCode will hide reasoning output even if the provider still reasons internally."
        : $"Reasoning effort: {SelectedReasoningEffort}. Explicit effort support depends on the provider and model.";

    partial void OnPromptChanged(string value)
    {
        RunPromptCommand.NotifyCanExecuteChanged();

        if (!_isApplyingPromptCommandSuggestion)
        {
            _promptCommandSuggestionsDismissed = false;
            _selectedPromptCommandSuggestionIndex = 0;
        }

        RefreshPromptCommandSuggestions();
    }

    partial void OnIsRunningChanged(bool value)
    {
        NotifyCommandStatesChanged();
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(OnboardingStatusText));
        OnPropertyChanged(nameof(SessionOptionsEnabled));
        OnPropertyChanged(nameof(BudgetControlsEnabled));
        NotifyOnboardingStateChanged();
        RefreshPromptCommandSuggestions();
    }

    partial void OnIsPromptRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(HasPromptProgress));
        OnPropertyChanged(nameof(StatusText));
    }

    partial void OnIsProviderSetupActiveChanged(bool value)
    {
        NotifyOnboardingStateChanged();
    }

    partial void OnProgressTextChanged(string value)
    {
        OnPropertyChanged(nameof(StatusText));
    }

    partial void OnSelectedModelIdChanged(string? value)
    {
        ApplyModelCommand.NotifyCanExecuteChanged();
        _ = AutoApplyModelAsync();
    }

    partial void OnSelectedThinkingModeChanged(string? value)
    {
        bool shouldBeEnabled = string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
        if (IsThinkingEnabled != shouldBeEnabled)
        {
            IsThinkingEnabled = shouldBeEnabled;
        }

        ApplyThinkingCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedReasoningEffortChanged(string? value)
    {
        ApplyReasoningCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ReasoningStatusText));
        _ = AutoApplyReasoningAsync();
    }

    partial void OnIsThinkingEnabledChanged(bool value)
    {
        string mode = value ? "on" : "off";
        if (!string.Equals(SelectedThinkingMode, mode, StringComparison.OrdinalIgnoreCase))
        {
            SelectedThinkingMode = mode;
        }

        OnPropertyChanged(nameof(ThinkingToggleText));
        ApplyThinkingCommand.NotifyCanExecuteChanged();
        _ = AutoApplyThinkingAsync();
        OnPropertyChanged(nameof(ReasoningStatusText));
    }

    partial void OnSelectedProfileNameChanged(string? value)
    {
        ApplyProfileCommand.NotifyCanExecuteChanged();
        _ = AutoApplyProfileAsync();
    }

    partial void OnHasSessionOptionsChanged(bool value)
    {
        OnPropertyChanged(nameof(SessionOptionsEnabled));
    }

    partial void OnActiveProjectChanged(ProjectInfo? value)
    {
        NotifyCommandStatesChanged();
        RefreshPromptCommandSuggestions();
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

    partial void OnPermissionToolPatternChanged(string value)
    {
        AllowPermissionCommand.NotifyCanExecuteChanged();
        DenyPermissionCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedBudgetControlSourceChanged(string? value)
    {
        OnPropertyChanged(nameof(BudgetControlActionText));
        ConfigureBudgetControlsCommand.NotifyCanExecuteChanged();
    }

    partial void OnBudgetControlDetailTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasBudgetControlDetail));
    }

    private bool CanRunPrompt(ProjectInfo? project)
    {
        return !IsRunning && project is not null && !string.IsNullOrWhiteSpace(Prompt);
    }

    private bool CanRunWorkspaceCommand(ProjectInfo? project)
    {
        return !IsRunning && project is not null;
    }

    private bool CanConfigureProvider(ProjectInfo? project)
    {
        return !IsRunning;
    }

    private bool CanApplyModel(ProjectInfo? project)
    {
        return CanRunWorkspaceCommand(project) && !string.IsNullOrWhiteSpace(SelectedModelId);
    }

    private bool CanApplyThinking(ProjectInfo? project)
    {
        return CanRunWorkspaceCommand(project) && !string.IsNullOrWhiteSpace(SelectedThinkingMode);
    }

    private bool CanApplyReasoning(ProjectInfo? project)
    {
        return CanRunWorkspaceCommand(project) && !string.IsNullOrWhiteSpace(SelectedReasoningEffort);
    }

    private bool CanApplyProfile(ProjectInfo? project)
    {
        return CanRunWorkspaceCommand(project) && !string.IsNullOrWhiteSpace(SelectedProfileName);
    }

    private bool CanApplyPermissionOverride(ProjectInfo? project)
    {
        return CanRunWorkspaceCommand(project) && !string.IsNullOrWhiteSpace(PermissionToolPattern);
    }

    private bool CanConfigureBudgetControls(ProjectInfo? project)
    {
        return !IsRunning && project is not null;
    }

    public async Task<bool> HandlePromptKeyAsync(
        Key key,
        KeyModifiers modifiers,
        ProjectInfo? project)
    {
        if (HasPromptCommandSuggestions)
        {
            switch (key)
            {
                case Key.Up:
                case Key.Left:
                    MovePromptCommandSuggestion(-1);
                    return true;

                case Key.Down:
                    MovePromptCommandSuggestion(1);
                    return true;

                case Key.Right:
                case Key.Tab:
                    CompleteSelectedPromptCommandSuggestion(submitCommand: false);
                    return true;

                case Key.PageUp:
                    MovePromptCommandSuggestion(-MaxPromptCommandSuggestionCount);
                    return true;

                case Key.PageDown:
                    MovePromptCommandSuggestion(MaxPromptCommandSuggestionCount);
                    return true;

                case Key.Home:
                    SelectPromptCommandSuggestion(0);
                    return true;

                case Key.End:
                    SelectPromptCommandSuggestion(GetMatchingPromptCommandSuggestions().Count - 1);
                    return true;

                case Key.Escape:
                    DismissPromptCommandSuggestions();
                    return true;

                case Key.Enter:
                    if (modifiers.HasFlag(KeyModifiers.Shift) ||
                        modifiers.HasFlag(KeyModifiers.Control))
                    {
                        return false;
                    }

                    await CompleteSelectedPromptCommandSuggestionAsync(
                        submitCommand: true,
                        project);
                    return true;
            }
        }

        if (key == Key.Enter &&
            !modifiers.HasFlag(KeyModifiers.Shift) &&
            !modifiers.HasFlag(KeyModifiers.Control))
        {
            await RunPromptAsync(project);
            return true;
        }

        return false;
    }

    public bool ShouldHandlePromptKey(
        Key key,
        KeyModifiers modifiers)
    {
        bool isPlainEnter = key == Key.Enter &&
            !modifiers.HasFlag(KeyModifiers.Shift) &&
            !modifiers.HasFlag(KeyModifiers.Control);

        if (HasPromptCommandSuggestions)
        {
            return key is Key.Up or Key.Left or Key.Down or Key.Right or Key.Tab or
                Key.PageUp or Key.PageDown or Key.Home or Key.End or Key.Escape ||
                isPlainEnter;
        }

        return isPlainEnter;
    }

    private Task AutoApplyModelAsync()
    {
        if (_isApplyingSessionInfo ||
            !CanApplyModel(ActiveProject))
        {
            return Task.CompletedTask;
        }

        return ApplyModelAsync(ActiveProject);
    }

    private Task AutoApplyThinkingAsync()
    {
        if (_isApplyingSessionInfo ||
            !CanApplyThinking(ActiveProject))
        {
            return Task.CompletedTask;
        }

        return ApplyThinkingAsync(ActiveProject);
    }

    private Task AutoApplyReasoningAsync()
    {
        if (_isApplyingSessionInfo ||
            !CanApplyReasoning(ActiveProject))
        {
            return Task.CompletedTask;
        }

        return ApplyReasoningAsync(ActiveProject);
    }

    private Task AutoApplyProfileAsync()
    {
        if (_isApplyingSessionInfo ||
            !CanApplyProfile(ActiveProject))
        {
            return Task.CompletedTask;
        }

        return ApplyProfileAsync(ActiveProject);
    }

    public async Task LoadSessionAsync(ProjectInfo? project)
    {
        if (project is null || IsRunning)
        {
            return;
        }

        bool shouldShowSetupOverlay = !HasSessionOptions;
        if (shouldShowSetupOverlay)
        {
            IsProviderSetupActive = true;
        }

        try
        {
            await RunControlOperationAsync(
                "Checking provider setup",
                project.Path,
                async () =>
                {
                    BackendSessionInfo sessionInfo = await _agentRunner.GetSessionAsync(project.Path, SelectedSection?.SectionId);
                    ApplySessionInfo(sessionInfo, replaceConversation: true, workspacePath: project.Path);
                    Activity.Add(new AgentEvent("settings", "Controls loaded.", project.Path));
                });
        }
        finally
        {
            if (shouldShowSetupOverlay)
            {
                IsProviderSetupActive = false;
            }
        }
    }

    public async Task EnsureProviderSetupAsync()
    {
        if (IsRunning)
        {
            return;
        }

        IsProviderSetupActive = true;
        try
        {
            await RunControlOperationAsync(
                "Checking provider setup",
                workspacePath: null,
                async () =>
                {
                    ProviderSetupRunResult result = await _providerSetupRunner.RunAsync();
                    Messages.Add(new ChatMessage(
                        "StemCode",
                        "Provider setup complete.\n" +
                        $"Provider: {result.ProviderName}\n" +
                        $"Active model: {result.ModelId}\n" +
                        "Open a workspace to start a section.",
                        statusNote: null,
                        workspacePath: null));

                    foreach (string activity in result.Activity)
                    {
                        Activity.Add(new AgentEvent("settings", activity));
                    }
                });
        }
        finally
        {
            IsProviderSetupActive = false;
        }
    }

    private async Task ApplyModelAsync(ProjectInfo? project)
    {
        if (project is null || string.IsNullOrWhiteSpace(SelectedModelId))
        {
            return;
        }

        await RunControlOperationAsync(
            "Changing model",
            project.Path,
            async () =>
            {
                AgentRunResult result = await _agentRunner.SetModelAsync(
                    project.Path,
                    SelectedModelId,
                    SelectedSection?.SectionId);
                ApplyRunResult(result, project.Path);
            });
    }

    private async Task ApplyThinkingAsync(ProjectInfo? project)
    {
        if (project is null || string.IsNullOrWhiteSpace(SelectedThinkingMode))
        {
            return;
        }

        await RunControlOperationAsync(
            "Changing thinking",
            project.Path,
            async () =>
            {
                AgentRunResult result = await _agentRunner.SetThinkingAsync(
                    project.Path,
                    SelectedThinkingMode,
                    SelectedSection?.SectionId);
                ApplyRunResult(result, project.Path);
            });
    }

    private async Task ApplyReasoningAsync(ProjectInfo? project)
    {
        if (project is null || string.IsNullOrWhiteSpace(SelectedReasoningEffort))
        {
            return;
        }

        await RunControlOperationAsync(
            "Changing reasoning effort",
            project.Path,
            async () =>
            {
                AgentRunResult result = await _agentRunner.SetReasoningAsync(
                    project.Path,
                    SelectedReasoningEffort,
                    SelectedSection?.SectionId);
                ApplyRunResult(result, project.Path);
            });
    }

    private async Task ApplyProfileAsync(ProjectInfo? project)
    {
        if (project is null || string.IsNullOrWhiteSpace(SelectedProfileName))
        {
            return;
        }

        await RunControlOperationAsync(
            "Changing profile",
            project.Path,
            async () =>
            {
                AgentRunResult result = await _agentRunner.SetProfileAsync(
                    project.Path,
                    SelectedProfileName,
                    SelectedSection?.SectionId);
                ApplyRunResult(result, project.Path);
            });
    }

    private async Task ConfigureProviderAsync(ProjectInfo? project)
    {
        if (project is null)
        {
            await EnsureProviderSetupAsync();
            return;
        }

        IsProviderSetupActive = true;
        try
        {
            await RunSessionCommandAsync(project, "/onboard", "Configuring provider");
        }
        finally
        {
            IsProviderSetupActive = false;
        }
    }

    private Task ShowHelpAsync(ProjectInfo? project)
    {
        return RunSessionCommandAsync(project, "/help", "Opening help");
    }

    private Task ShowPermissionsAsync(ProjectInfo? project)
    {
        return RunSessionCommandAsync(project, "/permissions", "Loading permissions");
    }

    private Task ShowRulesAsync(ProjectInfo? project)
    {
        return RunSessionCommandAsync(project, "/rules", "Loading permission rules");
    }

    private Task UndoAsync(ProjectInfo? project)
    {
        return RunSessionCommandAsync(project, "/undo", "Undoing last edit");
    }

    private Task RedoAsync(ProjectInfo? project)
    {
        return RunSessionCommandAsync(project, "/redo", "Redoing last edit");
    }

    private Task AllowPermissionAsync(ProjectInfo? project)
    {
        return RunPermissionOverrideAsync(project, "/allow", "Adding allow rule");
    }

    private Task DenyPermissionAsync(ProjectInfo? project)
    {
        return RunPermissionOverrideAsync(project, "/deny", "Adding deny rule");
    }

    private async Task ConfigureBudgetControlsAsync(ProjectInfo? project)
    {
        if (project is null)
        {
            return;
        }

        string source = string.IsNullOrWhiteSpace(SelectedBudgetControlSource)
            ? BudgetControlsSettings.LocalSource
            : SelectedBudgetControlSource.Trim();

        string command = string.Equals(source, BudgetControlsSettings.CloudSource, StringComparison.OrdinalIgnoreCase)
            ? "/budget cloud"
            : "/budget local";

        await RunSessionCommandAsync(
            project,
            command,
            "Configuring budget controls");
        ApplyBudgetControlsSettings(_settingsService.LoadBudgetControls());
    }

    private async Task RunPromptAsync(ProjectInfo? project)
    {
        if (project is null || string.IsNullOrWhiteSpace(Prompt))
        {
            return;
        }

        var prompt = Prompt.Trim();
        Prompt = string.Empty;
        bool isCommand = IsSlashCommand(prompt);
        bool isOnboardingCommand = IsOnboardingCommand(prompt);

        Messages.Add(new ChatMessage("You", prompt, statusNote: null, workspacePath: project.Path));
        Activity.Add(new AgentEvent("task", $"Running in {project.Name}", project.Path));

        if (isOnboardingCommand)
        {
            IsProviderSetupActive = true;
        }

        if (!isCommand)
        {
            _currentRunStartedAt = DateTimeOffset.UtcNow;
            UpdateProgressText();
            _progressTimer.Start();
            IsPromptRunning = true;
        }

        IsRunning = true;

        try
        {
            AgentRunResult result = await _agentRunner.RunAsync(
                project.Path,
                prompt,
                SelectedSection?.SectionId);
            string? finalProgressText = FormatFinalProgressText(result, allowLiveFallback: !isCommand);
            ApplySessionInfo(result.SessionInfo, workspacePath: project.Path);

            AddToolOutputMessages(result, project.Path);

            Messages.Add(new ChatMessage(
                "StemCode",
                string.IsNullOrWhiteSpace(result.ResponseText) ? "Task completed with no output." : result.ResponseText.Trim(),
                finalProgressText,
                project.Path));

            foreach (string activity in result.Activity)
            {
                Activity.Add(new AgentEvent("agent", activity, project.Path));
            }

            Activity.Add(new AgentEvent("done", "Task finished.", project.Path));
            RefreshChangedFiles();
            RunCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Messages.Add(new ChatMessage("StemCode", ex.Message, statusNote: null, workspacePath: project.Path));
            Activity.Add(new AgentEvent("error", ex.Message, project.Path));
        }
        finally
        {
            _progressTimer.Stop();
            _currentRunStartedAt = null;
            IsPromptRunning = false;
            IsRunning = false;
            if (isOnboardingCommand)
            {
                IsProviderSetupActive = false;
            }
        }
    }

    private async Task RunControlOperationAsync(
        string description,
        string? workspacePath,
        Func<Task> operation)
    {
        IsPromptRunning = false;
        IsRunning = true;
        Activity.Add(new AgentEvent("settings", description, workspacePath));

        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            Messages.Add(new ChatMessage("StemCode", ex.Message, statusNote: null, workspacePath: workspacePath));
            Activity.Add(new AgentEvent("error", ex.Message, workspacePath));
        }
        finally
        {
            IsRunning = false;
        }
    }

    private async Task RunPermissionOverrideAsync(
        ProjectInfo? project,
        string commandName,
        string description)
    {
        if (project is null || string.IsNullOrWhiteSpace(PermissionToolPattern))
        {
            return;
        }

        string command = string.IsNullOrWhiteSpace(PermissionSubjectPattern)
            ? $"{commandName} {PermissionToolPattern.Trim()}"
            : $"{commandName} {PermissionToolPattern.Trim()} {PermissionSubjectPattern.Trim()}";

        await RunSessionCommandAsync(project, command, description);
        PermissionSubjectPattern = string.Empty;
    }

    private async Task RunSessionCommandAsync(
        ProjectInfo? project,
        string command,
        string description)
    {
        if (project is null)
        {
            return;
        }

        await RunControlOperationAsync(
            description,
            project.Path,
            async () =>
            {
                Messages.Add(new ChatMessage("You", command, statusNote: null, workspacePath: project.Path));
                AgentRunResult result = await _agentRunner.RunAsync(
                    project.Path,
                    command,
                    SelectedSection?.SectionId);
                ApplySessionInfo(result.SessionInfo, workspacePath: project.Path);
                AddToolOutputMessages(result, project.Path);
                string? finalProgressText = FormatFinalProgressText(result, allowLiveFallback: false);
                Messages.Add(new ChatMessage(
                    "StemCode",
                    string.IsNullOrWhiteSpace(result.ResponseText)
                        ? "Command completed."
                        : result.ResponseText.Trim(),
                    statusNote: finalProgressText,
                    workspacePath: project.Path));

                foreach (string activity in result.Activity)
                {
                    Activity.Add(new AgentEvent("command", activity, project.Path));
                }

                RefreshChangedFiles();
                RunCompleted?.Invoke(this, EventArgs.Empty);
            });
    }

    private void CompleteSelectedPromptCommandSuggestion(bool submitCommand)
    {
        _ = CompleteSelectedPromptCommandSuggestionAsync(submitCommand, ActiveProject);
    }

    private async Task CompleteSelectedPromptCommandSuggestionAsync(
        bool submitCommand,
        ProjectInfo? project)
    {
        IReadOnlyList<DesktopCommandSuggestionDescriptor> suggestions = GetMatchingPromptCommandSuggestions();
        if (suggestions.Count == 0)
        {
            RefreshPromptCommandSuggestions();
            return;
        }

        _selectedPromptCommandSuggestionIndex = Math.Clamp(
            _selectedPromptCommandSuggestionIndex,
            0,
            suggestions.Count - 1);
        DesktopCommandSuggestionDescriptor suggestion = suggestions[_selectedPromptCommandSuggestionIndex];

        _isApplyingPromptCommandSuggestion = true;
        _promptCommandSuggestionsDismissed = true;
        try
        {
            Prompt = suggestion.RequiresArgument
                ? suggestion.Command + " "
                : suggestion.Command;
        }
        finally
        {
            _isApplyingPromptCommandSuggestion = false;
        }

        RefreshPromptCommandSuggestions();

        if (submitCommand && !suggestion.RequiresArgument)
        {
            await RunPromptAsync(project);
        }
    }

    private void DismissPromptCommandSuggestions()
    {
        _promptCommandSuggestionsDismissed = true;
        RefreshPromptCommandSuggestions();
    }

    private IReadOnlyList<DesktopCommandSuggestionDescriptor> GetMatchingPromptCommandSuggestions()
    {
        string input = Prompt ?? string.Empty;
        IReadOnlyList<DesktopCommandSuggestionDescriptor> availableSuggestions = GetPromptCommandSuggestionDescriptors();
        if (!IsPromptCommandSuggestionInput(input, availableSuggestions))
        {
            return [];
        }

        return availableSuggestions
            .Where(suggestion => suggestion.Command.StartsWith(input, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static IReadOnlyList<DesktopCommandSuggestionDescriptor> GetVisiblePromptCommandSuggestions(
        IReadOnlyList<DesktopCommandSuggestionDescriptor> suggestions,
        int selectedIndex)
    {
        if (suggestions.Count <= MaxPromptCommandSuggestionCount)
        {
            return suggestions;
        }

        int startIndex = Math.Clamp(
            selectedIndex - (MaxPromptCommandSuggestionCount / 2),
            0,
            suggestions.Count - MaxPromptCommandSuggestionCount);

        return suggestions
            .Skip(startIndex)
            .Take(MaxPromptCommandSuggestionCount)
            .ToArray();
    }

    private int GetVisiblePromptCommandStartIndex(
        IReadOnlyList<DesktopCommandSuggestionDescriptor> suggestions,
        IReadOnlyList<DesktopCommandSuggestionDescriptor> visibleSuggestions)
    {
        if (visibleSuggestions.Count == 0)
        {
            return 0;
        }

        for (int index = 0; index < suggestions.Count; index++)
        {
            if (string.Equals(
                    suggestions[index].Command,
                    visibleSuggestions[0].Command,
                    StringComparison.Ordinal))
            {
                return index;
            }
        }

        return 0;
    }

    private IReadOnlyList<DesktopCommandSuggestionDescriptor> GetPromptCommandSuggestionDescriptors()
    {
        if (ActiveProject is null)
        {
            return PromptCommandSuggestionDescriptors;
        }

        DesktopCommandSuggestionDescriptor[] customSuggestions = CustomSlashCommandService
            .List(ActiveProject.Path)
            .Select(static suggestion => new DesktopCommandSuggestionDescriptor(
                suggestion.Command,
                suggestion.Usage,
                suggestion.Description,
                suggestion.RequiresArgument))
            .ToArray();

        return PromptCommandSuggestionDescriptors
            .Concat(customSuggestions)
            .OrderBy(static suggestion => suggestion.Command, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsPromptCommandSuggestionInput(
        string input,
        IReadOnlyList<DesktopCommandSuggestionDescriptor> availableSuggestions)
    {
        if (string.IsNullOrEmpty(input) ||
            !input.StartsWith("/", StringComparison.Ordinal) ||
            input.Any(char.IsWhiteSpace))
        {
            return false;
        }

        return input.Length == 1 ||
            availableSuggestions.Any(
                suggestion => suggestion.Command.StartsWith(input, StringComparison.OrdinalIgnoreCase));
    }

    private void MovePromptCommandSuggestion(int delta)
    {
        IReadOnlyList<DesktopCommandSuggestionDescriptor> suggestions = GetMatchingPromptCommandSuggestions();
        if (suggestions.Count == 0)
        {
            _selectedPromptCommandSuggestionIndex = 0;
            RefreshPromptCommandSuggestions();
            return;
        }

        int nextIndex = _selectedPromptCommandSuggestionIndex + delta;
        while (nextIndex < 0)
        {
            nextIndex += suggestions.Count;
        }

        _selectedPromptCommandSuggestionIndex = nextIndex % suggestions.Count;
        RefreshPromptCommandSuggestions();
    }

    private void SelectPromptCommandSuggestion(int index)
    {
        IReadOnlyList<DesktopCommandSuggestionDescriptor> suggestions = GetMatchingPromptCommandSuggestions();
        _selectedPromptCommandSuggestionIndex = Math.Clamp(
            index,
            0,
            Math.Max(0, suggestions.Count - 1));
        RefreshPromptCommandSuggestions();
    }

    private void SelectPromptCommandSuggestionByCommand(string command)
    {
        IReadOnlyList<DesktopCommandSuggestionDescriptor> suggestions = GetMatchingPromptCommandSuggestions();
        int index = suggestions
            .ToList()
            .FindIndex(suggestion => string.Equals(suggestion.Command, command, StringComparison.Ordinal));

        if (index >= 0)
        {
            _selectedPromptCommandSuggestionIndex = index;
        }

        CompleteSelectedPromptCommandSuggestion(submitCommand: false);
    }

    private void RefreshPromptCommandSuggestions()
    {
        PromptCommandSuggestions.Clear();

        if (_promptCommandSuggestionsDismissed || IsRunning)
        {
            OnPropertyChanged(nameof(HasPromptCommandSuggestions));
            return;
        }

        IReadOnlyList<DesktopCommandSuggestionDescriptor> suggestions = GetMatchingPromptCommandSuggestions();
        if (suggestions.Count == 0)
        {
            _selectedPromptCommandSuggestionIndex = 0;
            OnPropertyChanged(nameof(HasPromptCommandSuggestions));
            return;
        }

        _selectedPromptCommandSuggestionIndex = Math.Clamp(
            _selectedPromptCommandSuggestionIndex,
            0,
            suggestions.Count - 1);
        IReadOnlyList<DesktopCommandSuggestionDescriptor> visibleSuggestions = GetVisiblePromptCommandSuggestions(
            suggestions,
            _selectedPromptCommandSuggestionIndex);
        int startIndex = GetVisiblePromptCommandStartIndex(suggestions, visibleSuggestions);

        for (int visibleIndex = 0; visibleIndex < visibleSuggestions.Count; visibleIndex++)
        {
            DesktopCommandSuggestionDescriptor suggestion = visibleSuggestions[visibleIndex];
            int suggestionIndex = startIndex + visibleIndex;
            PromptCommandSuggestions.Add(new DesktopCommandSuggestion(
                suggestion.Command,
                suggestion.Usage,
                suggestion.Description,
                suggestion.RequiresArgument,
                suggestionIndex == _selectedPromptCommandSuggestionIndex,
                new RelayCommand(() => SelectPromptCommandSuggestionByCommand(suggestion.Command))));
        }

        OnPropertyChanged(nameof(HasPromptCommandSuggestions));
    }

    private static bool IsSlashCommand(string prompt)
    {
        return prompt.TrimStart().StartsWith("/", StringComparison.Ordinal);
    }

    private static bool IsOnboardingCommand(string prompt)
    {
        string trimmed = prompt.Trim();
        return string.Equals(trimmed, "/onboard", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("/onboard ", StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshChangedFiles()
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

    private void ApplyRunResult(AgentRunResult result, string? workspacePath)
    {
        ApplySessionInfo(result.SessionInfo, workspacePath: workspacePath);

        foreach (string activity in result.Activity)
        {
            Activity.Add(new AgentEvent("settings", activity, workspacePath));
        }
    }

    private void ApplySessionInfo(
        BackendSessionInfo? sessionInfo,
        bool replaceConversation = false,
        string? workspacePath = null)
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

            SelectedModelId = sessionInfo.ModelId;
            _activeModelContextWindowTokens = sessionInfo.ActiveModelContextWindowTokens;
            SelectedThinkingMode = sessionInfo.ThinkingMode;
            SelectedReasoningEffort = sessionInfo.ReasoningEffort;
            SelectedProfileName = sessionInfo.AgentProfileName;
            HasSessionOptions = true;
            UpdateProgressText();
        }
        finally
        {
            _isApplyingSessionInfo = false;
        }

        if (replaceConversation)
        {
            ReplaceConversationMessages(sessionInfo, workspacePath);
        }

        NotifyCommandStatesChanged();
    }

    private void NotifyCommandStatesChanged()
    {
        RunPromptCommand.NotifyCanExecuteChanged();
        LoadSessionCommand.NotifyCanExecuteChanged();
        ApplyModelCommand.NotifyCanExecuteChanged();
        ApplyThinkingCommand.NotifyCanExecuteChanged();
        ApplyReasoningCommand.NotifyCanExecuteChanged();
        ApplyProfileCommand.NotifyCanExecuteChanged();
        ConfigureProviderCommand.NotifyCanExecuteChanged();
        ShowHelpCommand.NotifyCanExecuteChanged();
        ShowPermissionsCommand.NotifyCanExecuteChanged();
        ShowRulesCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        AllowPermissionCommand.NotifyCanExecuteChanged();
        DenyPermissionCommand.NotifyCanExecuteChanged();
        ConfigureBudgetControlsCommand.NotifyCanExecuteChanged();
    }

    private void NotifyOnboardingStateChanged()
    {
        OnPropertyChanged(nameof(HasFloatingSelectionPrompt));
        OnPropertyChanged(nameof(HasFloatingTextPrompt));
        OnPropertyChanged(nameof(HasOnboardingOverlay));
        OnPropertyChanged(nameof(HasOnboardingSelectionPrompt));
        OnPropertyChanged(nameof(HasOnboardingTextPrompt));
        OnPropertyChanged(nameof(HasOnboardingCountdown));
        OnPropertyChanged(nameof(CanCancelOnboardingSelectionPrompt));
        OnPropertyChanged(nameof(CanCancelOnboardingTextPrompt));
        OnPropertyChanged(nameof(OnboardingStageText));
        OnPropertyChanged(nameof(OnboardingTitle));
        OnPropertyChanged(nameof(OnboardingDescription));
        OnPropertyChanged(nameof(OnboardingInputKind));
    }

    private void ApplyBudgetControlsSettings(BudgetControlsSettings settings)
    {
        SelectedBudgetControlSource = settings.Source;

        if (string.Equals(settings.Source, BudgetControlsSettings.CloudSource, StringComparison.OrdinalIgnoreCase))
        {
            BudgetControlStatusText = settings.HasCloudAuthKey
                ? "Cloud budget controls"
                : "Cloud budget controls need an auth key";
            BudgetControlDetailText = string.IsNullOrWhiteSpace(settings.CloudApiUrl)
                ? "Cloud API URL not set"
                : settings.CloudApiUrl;
        }
        else
        {
            BudgetControlStatusText = "Local budget controls";
            BudgetControlDetailText = string.IsNullOrWhiteSpace(settings.LocalPath)
                ? BudgetControlsLocalPath
                : settings.LocalPath;
        }

        OnPropertyChanged(nameof(BudgetControlActionText));
    }

    private void AddToolOutputMessages(AgentRunResult result, string? workspacePath)
    {
        if (result.ToolOutput is not { Count: > 0 })
        {
            return;
        }

        foreach (string toolOutput in result.ToolOutput)
        {
            Messages.Add(new ChatMessage("Tool", toolOutput, statusNote: null, workspacePath: workspacePath));
        }
    }

    private void ReplaceConversationMessages(BackendSessionInfo sessionInfo, string? workspacePath)
    {
        Messages.Clear();

        if (sessionInfo.ConversationHistory.Count == 0)
        {
            string label = string.IsNullOrWhiteSpace(sessionInfo.SectionTitle)
                ? "Ready."
                : $"Ready. Section: {sessionInfo.SectionTitle}";
            Messages.Add(new ChatMessage("StemCode", label, statusNote: null, workspacePath: workspacePath));
            return;
        }

        foreach (BackendConversationMessage message in sessionInfo.ConversationHistory)
        {
            string role = message.Role switch
            {
                "user" => "You",
                "tool" => "Tool",
                _ => "StemCode"
            };
            Messages.Add(new ChatMessage(role, message.Content, statusNote: null, workspacePath: workspacePath));
        }
    }

    private void OnConversationMessageReceived(object? sender, ChatMessage message)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Messages.Add(message);
            return;
        }

        Dispatcher.UIThread.Post(() => Messages.Add(message));
    }

    private void OnSelectionPromptChanged(object? sender, DesktopSelectionPrompt? prompt)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ActiveSelectionPrompt = prompt;
            return;
        }

        Dispatcher.UIThread.Post(() => ActiveSelectionPrompt = prompt);
    }

    private void OnTextPromptChanged(object? sender, DesktopTextPrompt? prompt)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ActiveTextPrompt = prompt;
            return;
        }

        Dispatcher.UIThread.Post(() => ActiveTextPrompt = prompt);
    }

    private void UpdateProgressText()
    {
        if (_currentRunStartedAt is null)
        {
            ProgressText = FormatProgressText(
                TimeSpan.Zero,
                0,
                _activeModelContextWindowTokens,
                0);
            return;
        }

        ProgressText = FormatProgressText(
            GetLiveElapsed(),
            GetLiveEstimatedTokens(),
            _activeModelContextWindowTokens,
            GetLiveEstimatedTokens());
    }

    private string? FormatFinalProgressText(AgentRunResult result, bool allowLiveFallback)
    {
        if (result.Elapsed is { } elapsed && result.EstimatedTokens is { } estimatedTokens)
        {
            return FormatProgressText(
                elapsed,
                estimatedTokens,
                _activeModelContextWindowTokens,
                result.EstimatedContextWindowUsedTokens);
        }

        return allowLiveFallback ? ProgressText : null;
    }

    private static string FormatProgressText(
        TimeSpan elapsed,
        int estimatedTokens,
        int? contextWindowTokens,
        int? contextWindowUsedTokens)
    {
        string baseText = $"{FormatElapsed(elapsed)} \u00B7 {FormatTokens(estimatedTokens)} tokens";
        return contextWindowTokens is > 0
            ? $"({baseText} \u00B7 {FormatContextWindowUsage(contextWindowUsedTokens ?? 0, contextWindowTokens.Value)} \u00B7 {FormatContextWindowTokens(contextWindowTokens.Value)} context)"
            : $"({baseText})";
    }

    private TimeSpan GetLiveElapsed()
    {
        return _currentRunStartedAt is null
            ? TimeSpan.Zero
            : DateTimeOffset.UtcNow - _currentRunStartedAt.Value;
    }

    private int GetLiveEstimatedTokens()
    {
        TimeSpan elapsed = GetLiveElapsed();
        return (int)Math.Floor(Math.Max(0d, elapsed.TotalSeconds) * EstimatedLiveTokensPerSecond);
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        int seconds = Math.Max(0, (int)Math.Floor(elapsed.TotalSeconds));
        TimeSpan normalized = TimeSpan.FromSeconds(seconds);

        if (normalized.TotalHours >= 1d)
        {
            return $"{(int)normalized.TotalHours}h {normalized.Minutes}m {normalized.Seconds}s";
        }

        if (normalized.TotalMinutes >= 1d)
        {
            return $"{(int)normalized.TotalMinutes}m {normalized.Seconds}s";
        }

        return $"{normalized.Seconds}s";
    }

    private static string FormatTokens(int estimatedTokens)
    {
        int safeValue = Math.Max(0, estimatedTokens);
        if (safeValue < 1_000)
        {
            return safeValue.ToString(CultureInfo.InvariantCulture);
        }

        double thousands = safeValue / 1_000d;
        string format = thousands >= 10d ? "0" : "0.#";
        double rounded = Math.Round(
            thousands,
            thousands >= 10d ? 0 : 1,
            MidpointRounding.AwayFromZero);

        return $"{rounded.ToString(format, CultureInfo.InvariantCulture)}k";
    }

    private static string FormatContextWindowUsage(
        int contextWindowUsedTokens,
        int contextWindowTokens)
    {
        int safeUsedTokens = Math.Max(0, contextWindowUsedTokens);
        int safeContextWindowTokens = Math.Max(1, contextWindowTokens);
        int percentage = (int)Math.Round(
            safeUsedTokens / (double)safeContextWindowTokens * 100d,
            MidpointRounding.AwayFromZero);

        return $"({percentage}%) {FormatTokens(safeUsedTokens)} Used";
    }

    private static string FormatContextWindowTokens(int contextWindowTokens)
    {
        int safeValue = Math.Max(0, contextWindowTokens);
        if (safeValue < 1_000)
        {
            return safeValue.ToString(CultureInfo.InvariantCulture);
        }

        if (safeValue < 1_000_000)
        {
            return FormatScaledMetric(safeValue / 1_000d, "k");
        }

        return FormatScaledMetric(safeValue / 1_000_000d, "m");
    }

    private static string FormatScaledMetric(double value, string suffix)
    {
        string format = value >= 10d ? "0" : "0.#";
        double rounded = Math.Round(
            value,
            value >= 10d ? 0 : 1,
            MidpointRounding.AwayFromZero);

        return $"{rounded.ToString(format, CultureInfo.InvariantCulture)}{suffix}";
    }

}
