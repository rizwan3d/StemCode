using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StemCode.Desktop.Models;
using StemCode.Desktop.Services;

namespace StemCode.Desktop.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IAsyncDisposable
{
    private bool _isDisposed;

    [ObservableProperty]
    private bool _isSidebarOpen = true;

    [ObservableProperty]
    private bool _isInspectorOpen;

    [ObservableProperty]
    private bool _isSettingsOpen;

    public MainWindowViewModel()
    {
        SettingsService settingsService = new();
        SectionHistoryService sectionHistoryService = new();

        Project = new ProjectViewModel(settingsService, sectionHistoryService);
        Chat = new ChatViewModel(null, settingsService)
        {
            ActiveProject = Project.SelectedProject
        };
        Inspector = new InspectorViewModel(Chat);
        Settings = new SettingsViewModel(Chat, settingsService);
        Palette = new CommandPaletteViewModel(BuildPaletteEntries);

        NewSessionCommand = new AsyncRelayCommand(StartNewSessionAsync, () => !Chat.IsRunning && Project.SelectedProject is not null);
        ToggleSidebarCommand = new RelayCommand(() => IsSidebarOpen = !IsSidebarOpen);
        ToggleInspectorCommand = new RelayCommand(() => IsInspectorOpen = !IsInspectorOpen);
        OpenSettingsCommand = new RelayCommand<string?>(OpenSettings);
        CloseSettingsCommand = new RelayCommand(() => IsSettingsOpen = false);
        OpenPaletteCommand = new RelayCommand(() => Palette.Open());
        DeleteSessionCommand = new AsyncRelayCommand<SessionListItem?>(DeleteSessionAsync);
        ShowInspectorTabCommand = new RelayCommand<string?>(tab =>
        {
            IsInspectorOpen = true;
            Inspector.SelectTabCommand.Execute(tab);
        });

        Chat.RunCompleted += async (_, _) => await OnRunCompletedAsync();
        Chat.SessionSwitched += async (_, sessionId) =>
        {
            _hasAutoOpenedInspector = false;
            await SyncSidebarToSessionAsync(sessionId);
        };
        Chat.SettingsRequested += (_, _) => OpenSettings(null);
        Chat.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ChatViewModel.IsRunning))
            {
                NewSessionCommand.NotifyCanExecuteChanged();
            }
            else if (args.PropertyName is nameof(ChatViewModel.HasInlineSelectionPrompt) or nameof(ChatViewModel.HasInlineTextPrompt))
            {
                OnPropertyChanged(nameof(ShowModalPrompt));
            }
        };
        Chat.ChangedFiles.CollectionChanged += (_, _) =>
        {
            // Surface the diff panel the first time the agent edits something.
            if (Chat.ChangedFiles.Count > 0 && !_hasAutoOpenedInspector)
            {
                _hasAutoOpenedInspector = true;
                // Deferred: never mutate state that re-enters ChangedFiles inside its own change event.
                Avalonia.Threading.Dispatcher.UIThread.Post(() => IsInspectorOpen = true);
            }
        };

        Project.PropertyChanged += async (_, args) =>
        {
            if (args.PropertyName == nameof(ProjectViewModel.SelectedProject))
            {
                Chat.ActiveProject = Project.SelectedProject;
                NewSessionCommand.NotifyCanExecuteChanged();
                _hasAutoOpenedInspector = false;
                await Project.RefreshSectionsAsync();
                Chat.SelectedSection = Project.SelectedSection;
                await Chat.LoadSessionAsync(Project.SelectedProject);
            }
            else if (args.PropertyName == nameof(ProjectViewModel.SelectedSection) && !Project.IsApplyingSelection)
            {
                if (Project.SelectedSection is null ||
                    string.Equals(Project.SelectedSection.SectionId, Chat.ActiveSessionId, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _hasAutoOpenedInspector = false;
                Chat.SelectedSection = Project.SelectedSection;
                await Chat.LoadSessionAsync(Project.SelectedProject);
            }
        };

        _ = InitializeAsync();
    }

    private bool _hasAutoOpenedInspector;

    public ProjectViewModel Project { get; }

    public ChatViewModel Chat { get; }

    public InspectorViewModel Inspector { get; }

    public SettingsViewModel Settings { get; }

    public CommandPaletteViewModel Palette { get; }

    public IAsyncRelayCommand NewSessionCommand { get; }

    public IRelayCommand ToggleSidebarCommand { get; }

    public IRelayCommand ToggleInspectorCommand { get; }

    public IRelayCommand<string?> OpenSettingsCommand { get; }

    public IRelayCommand CloseSettingsCommand { get; }

    public IRelayCommand OpenPaletteCommand { get; }

    public IAsyncRelayCommand<SessionListItem?> DeleteSessionCommand { get; }

    public IRelayCommand<string?> ShowInspectorTabCommand { get; }

    /// <summary>Raised when the view should show the folder picker.</summary>
    public event EventHandler? OpenFolderRequested;

    /// <summary>Raised when the view should focus the composer.</summary>
    public event EventHandler? FocusComposerRequested;

    /// <summary>Prompts raised while settings is open are shown as a modal above it.</summary>
    public bool ShowModalPrompt => IsSettingsOpen && (Chat.HasInlineSelectionPrompt || Chat.HasInlineTextPrompt);

    public bool ShowInlinePrompts => !IsSettingsOpen;

    partial void OnIsSidebarOpenChanged(bool value) => OnPropertyChanged(nameof(SidebarWidth));

    partial void OnIsInspectorOpenChanged(bool value)
    {
        Inspector.IsVisible = value;
    }

    partial void OnIsSettingsOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowModalPrompt));
        OnPropertyChanged(nameof(ShowInlinePrompts));
        if (!value)
        {
            FocusComposerRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    public double SidebarWidth => IsSidebarOpen ? 268 : 0;

    public void RequestOpenFolder() => OpenFolderRequested?.Invoke(this, EventArgs.Empty);

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        await Chat.DisposeAsync();
    }

    private void OpenSettings(string? sectionId)
    {
        IsSettingsOpen = true;
        Settings.Open(sectionId);
    }

    private async Task StartNewSessionAsync()
    {
        _hasAutoOpenedInspector = false;
        await Chat.StartNewSessionAsync(Project.SelectedProject);
        FocusComposerRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task DeleteSessionAsync(SessionListItem? item)
    {
        if (item is null || Chat.IsRunning)
        {
            return;
        }

        bool wasActive = string.Equals(item.Section.SectionId, Chat.ActiveSessionId, StringComparison.OrdinalIgnoreCase);
        if (await Project.DeleteSessionAsync(item) && wasActive)
        {
            await Chat.StartNewSessionAsync(Project.SelectedProject);
        }
    }

    private async Task OnRunCompletedAsync()
    {
        Inspector.NotifyChangesUpdated();
        await SyncSidebarToSessionAsync(Chat.ActiveSessionId);
    }

    private async Task SyncSidebarToSessionAsync(string? sessionId)
    {
        await Project.RefreshSectionsAsync(sessionId, preferMostRecent: false);
        Chat.SelectedSection = Project.SelectedSection;
    }

    private async Task InitializeAsync()
    {
        if (Project.SelectedProject is not null)
        {
            await Project.RefreshSectionsAsync();
            Chat.SelectedSection = Project.SelectedSection;
            await Chat.LoadSessionAsync(Project.SelectedProject);
            return;
        }

        await Chat.EnsureProviderSetupAsync();
    }

    private IReadOnlyList<PaletteEntry> BuildPaletteEntries()
    {
        List<PaletteEntry> entries =
        [
            new("App", "New session", "Start a fresh conversation in this workspace", "Ctrl+N", () => _ = NewSessionCommand.ExecuteAsync(null)),
            new("App", "Open folder…", "Add a workspace folder", "Ctrl+O", RequestOpenFolder),
            new("App", "Settings", "Providers, permissions, MCP, memory and more", "Ctrl+,", () => OpenSettings(null)),
            new("App", "Toggle sidebar", null, "Ctrl+B", () => IsSidebarOpen = !IsSidebarOpen),
            new("App", "Toggle inspector", "Changes, plan, terminals and activity", "Ctrl+J", () => IsInspectorOpen = !IsInspectorOpen),
            new("App", "Show changes", "Files edited in this session with diffs", null, () => ShowInspectorTabCommand.Execute("Changes")),
            new("App", "Show all uncommitted changes", "git diff against HEAD", null, () => { IsInspectorOpen = true; _ = Inspector.ShowWorkspaceDiffCommand.ExecuteAsync(null); }),
            new("App", "Show plan", null, null, () => ShowInspectorTabCommand.Execute("Plan")),
            new("App", "Show background terminals", null, null, () => ShowInspectorTabCommand.Execute("Terminals")),
            new("App", "Show activity log", null, null, () => ShowInspectorTabCommand.Execute("Activity")),
            new("App", "Undo last edit", "Roll back the most recent tracked file edit", null, () => _ = Chat.UndoCommand.ExecuteAsync(null)),
            new("App", "Redo edit", null, null, () => _ = Chat.RedoCommand.ExecuteAsync(null)),
            new("App", "Voice dictation", null, "Ctrl+R", () => _ = Chat.DictateVoiceAsync()),
            new("App", "Change provider", "Run provider onboarding", null, () => _ = Chat.ConfigureProviderCommand.ExecuteAsync(null))
        ];

        foreach (SettingsSection section in Settings.Sections)
        {
            SettingsSection captured = section;
            entries.Add(new PaletteEntry("Settings", captured.Title, captured.Description, null, () => OpenSettings(captured.Id)));
        }

        foreach (DesktopCommandInfo command in Chat.GetCommandCatalog())
        {
            entries.Add(CommandPaletteViewModel.FromCommand(command, RunPaletteCommand));
        }

        foreach (SessionListItem session in Project.SessionItems.Take(30))
        {
            SessionListItem captured = session;
            entries.Add(new PaletteEntry("Sessions", captured.Title, captured.Detail, null, () => Project.SelectedSessionItem = captured));
        }

        foreach (ProjectInfo project in Project.Projects)
        {
            ProjectInfo captured = project;
            entries.Add(new PaletteEntry("Workspaces", captured.Name, captured.Path, null, () => Project.SelectedProject = captured));
        }

        return entries;
    }

    private void RunPaletteCommand(DesktopCommandInfo command)
    {
        if (command.RequiresArgument || command.Usage.Contains('<'))
        {
            Chat.Prompt = command.Command + " ";
            FocusComposerRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        _ = Chat.RunSlashCommand.ExecuteAsync(command.Command);
    }
}
