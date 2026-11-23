using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StemCode.Application.Models;
using StemCode.Desktop.Controls;
using StemCode.Desktop.Services;
using System.Collections.ObjectModel;

namespace StemCode.Desktop.ViewModels;

/// <summary>
/// Settings centre. Each section maps a StemCode feature area onto the backend commands that
/// configure it, so every capability of the CLI is reachable from the desktop app.
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private const string ArgumentToken = "{arg}";

    private readonly ChatViewModel _chat;
    private readonly SettingsService _settingsService;
    private int _requestVersion;

    [ObservableProperty]
    private SettingsSection? _selectedSection;

    [ObservableProperty]
    private string _output = string.Empty;

    [ObservableProperty]
    private string _outputTitle = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _argumentText = string.Empty;

    [ObservableProperty]
    private string _customCommand = string.Empty;

    public SettingsViewModel(ChatViewModel chat, SettingsService settingsService)
    {
        _chat = chat;
        _settingsService = settingsService;
        Sections = new ObservableCollection<SettingsSection>(BuildSections());
        RunActionCommand = new AsyncRelayCommand<SettingsAction?>(RunActionAsync, CanRunAction);
        RunCustomCommand = new AsyncRelayCommand(RunCustomAsync, () => !IsBusy && CustomCommand.Trim().StartsWith('/'));
        _chat.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ChatViewModel.IsRunning) or nameof(ChatViewModel.ActiveProject))
            {
                RunActionCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(WorkspaceHint));
                OnPropertyChanged(nameof(HasWorkspaceHint));
            }
        };
    }

    public ObservableCollection<SettingsSection> Sections { get; }

    public IAsyncRelayCommand<SettingsAction?> RunActionCommand { get; }

    public IAsyncRelayCommand RunCustomCommand { get; }

    public bool HasOutput => !string.IsNullOrWhiteSpace(Output);

    public bool HasArgument => SelectedSection?.ArgumentLabel is not null;

    public string WorkspaceHint => _chat.ActiveProject is null
        ? "Open a workspace first — settings commands run against the active workspace session."
        : _chat.IsRunning ? "StemCode is busy with a task. Actions are available again when it finishes." : string.Empty;

    public bool HasWorkspaceHint => !string.IsNullOrEmpty(WorkspaceHint);

    public string? WorkspacePath => _chat.ActiveProject?.Path;

    public void Open(string? sectionId = null)
    {
        SettingsSection? target = sectionId is null
            ? SelectedSection ?? Sections.FirstOrDefault()
            : Sections.FirstOrDefault(section => string.Equals(section.Id, sectionId, StringComparison.OrdinalIgnoreCase));
        if (ReferenceEquals(target, SelectedSection) && target is not null)
        {
            _ = RunAutoAsync(target);
            return;
        }

        SelectedSection = target;
    }

    partial void OnSelectedSectionChanged(SettingsSection? value)
    {
        foreach (SettingsSection section in Sections)
        {
            section.IsSelected = ReferenceEquals(section, value);
        }

        ArgumentText = string.Empty;
        Output = string.Empty;
        OutputTitle = string.Empty;
        OnPropertyChanged(nameof(HasArgument));
        if (value is not null)
        {
            CustomCommand = value.CustomCommandPrefix;
            _ = RunAutoAsync(value);
        }
    }

    partial void OnOutputChanged(string value) => OnPropertyChanged(nameof(HasOutput));

    partial void OnIsBusyChanged(bool value)
    {
        RunActionCommand.NotifyCanExecuteChanged();
        RunCustomCommand.NotifyCanExecuteChanged();
    }

    partial void OnArgumentTextChanged(string value) => RunActionCommand.NotifyCanExecuteChanged();

    partial void OnCustomCommandChanged(string value) => RunCustomCommand.NotifyCanExecuteChanged();

    private bool CanRunAction(SettingsAction? action)
    {
        if (action is null || IsBusy)
        {
            return false;
        }

        if (action.Command.Contains(ArgumentToken, StringComparison.Ordinal) && string.IsNullOrWhiteSpace(ArgumentText))
        {
            return false;
        }

        return action.IsLocal || (_chat.ActiveProject is not null && !_chat.IsRunning);
    }

    private async Task RunAutoAsync(SettingsSection section)
    {
        SettingsAction? auto = section.Actions.FirstOrDefault(static action => action.AutoRun);
        if (auto is not null && CanRunAction(auto))
        {
            await RunActionAsync(auto);
        }
    }

    private async Task RunActionAsync(SettingsAction? action)
    {
        if (action is null)
        {
            return;
        }

        if (action.IsLocal)
        {
            await RunLocalAsync(action);
            return;
        }

        string command = action.Command.Replace(ArgumentToken, ArgumentText.Trim(), StringComparison.Ordinal);
        await RunAndShowAsync(command, action.Label);

        if (action.Command.StartsWith("/budget", StringComparison.OrdinalIgnoreCase))
        {
            _settingsService.LoadBudgetControls();
        }
    }

    private Task RunCustomAsync()
    {
        string command = CustomCommand.Trim();
        return RunAndShowAsync(command, command);
    }

    private async Task RunAndShowAsync(string command, string title)
    {
        int version = ++_requestVersion;
        IsBusy = true;
        OutputTitle = title;
        Output = "Running…";
        try
        {
            string result = await _chat.RunCommandCaptureAsync(command);
            if (version == _requestVersion)
            {
                Output = string.IsNullOrWhiteSpace(result) ? "Done." : result.Trim();
            }
        }
        catch (Exception exception)
        {
            Output = exception.Message;
        }
        finally
        {
            if (version == _requestVersion)
            {
                IsBusy = false;
            }
        }
    }

    private async Task RunLocalAsync(SettingsAction action)
    {
        OutputTitle = action.Label;
        switch (action.Command)
        {
            case "app:voice-setup":
                Output = "Follow the prompts to choose a voice model and microphone.";
                _chat.Prompt = "/voice setup";
                await _chat.TryHandleVoiceCommandAsync();
                Output = "Voice setup finished.";
                break;
            case "app:voice-update":
                Output = "Updating voice models…";
                await _chat.UpdateVoiceModelsAsync();
                Output = "Voice models are up to date.";
                break;
            case "app:open-stemcode-folder":
                OpenWorkspaceFolder(".stemcode");
                break;
            case "app:open-memory-folder":
                OpenWorkspaceFolder(Path.Combine(".stemcode", "memory"));
                break;
            case "app:open-commands-folder":
                OpenWorkspaceFolder(Path.Combine(".stemcode", "commands"));
                break;
            case "app:open-config-folder":
                string configDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StemCode");
                Directory.CreateDirectory(configDirectory);
                MarkdownMessageView.OpenPath(configDirectory);
                Output = $"Opened {configDirectory}";
                break;
            case "app:provider":
                await _chat.ConfigureProviderCommand.ExecuteAsync(null);
                Output = "Provider onboarding finished.";
                break;
        }
    }

    private void OpenWorkspaceFolder(string relative)
    {
        string? root = _chat.ActiveProject?.Path;
        if (root is null)
        {
            Output = "Open a workspace first.";
            return;
        }

        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(path);
        MarkdownMessageView.OpenPath(path);
        Output = $"Opened {path}";
    }

    private static IEnumerable<SettingsSection> BuildSections()
    {
        yield return new SettingsSection(
            "provider",
            "Model & provider",
            "Active provider, model, thinking and reasoning. Switch saved providers or sign in to a new one.",
            [
                new("Show configuration", "/config", AutoRun: true),
                new("Pick model", "/models"),
                new("Use model", "/use {arg}"),
                new("Saved providers", "/provider list"),
                new("Switch to provider", "/provider {arg}"),
                new("Add or change provider", "app:provider"),
                new("Show reasoning", "/reasoning show"),
                new("Thinking on", "/thinking on"),
                new("Thinking off", "/thinking off")
            ],
            argumentLabel: "Model id or saved provider name",
            customPrefix: "/use ");

        yield return new SettingsSection(
            "behaviour",
            "Agent behaviour",
            "Output style, tool output detail, secret redaction, auto-commit and subagents.",
            [
                new("Output styles", "/output-style list", AutoRun: true),
                new("Use output style", "/output-style {arg}"),
                new("Tool output: compact", "/tooloutput compact"),
                new("Tool output: full", "/tooloutput full"),
                new("Tool output: auto", "/tooloutput auto"),
                new("Redaction on", "/redact on"),
                new("Redaction off", "/redact off"),
                new("Auto-commit status", "/autocommit status"),
                new("Auto-commit on", "/autocommit on"),
                new("Auto-commit off", "/autocommit off"),
                new("Subagents", "/agent"),
                new("All settings", "/setting")
            ],
            argumentLabel: "Output style name",
            customPrefix: "/setting ");

        yield return new SettingsSection(
            "permissions",
            "Permissions & sandbox",
            "Permission policy, effective rules, session allow/deny overrides and Windows sandbox setup. Pattern: <tool-or-tag> [target pattern].",
            [
                new("Permission summary", "/permissions", AutoRun: true),
                new("Effective rules", "/rules"),
                new("Allow", "/allow {arg}"),
                new("Deny", "/deny {arg}"),
                new("Permission settings", "/setting permissions"),
                new("Set up Windows sandbox", "/setup-sandbox")
            ],
            argumentLabel: "Tool or tag, then optional pattern (e.g. shell_command git *)",
            customPrefix: "/allow ");

        yield return new SettingsSection(
            "mcp",
            "MCP servers",
            "Configured MCP servers with their tools and prompts. Sign in to or out of remote servers that use OAuth.",
            [
                new("Servers & tools", "/mcp", AutoRun: true),
                new("Sign in", "/mcp login {arg}"),
                new("Sign out", "/mcp logout {arg}"),
                new("Reload", "/reload")
            ],
            argumentLabel: "Server name",
            customPrefix: "/mcp ");

        yield return new SettingsSection(
            "skills",
            "Skills & commands",
            "Install skills from marketplaces and manage custom slash commands in .stemcode/commands.",
            [
                new("Installed skills", "/skill list", AutoRun: true),
                new("Add marketplace", "/skill marketplace add {arg}"),
                new("Browse marketplace", "/skill browse {arg}"),
                new("Install skill", "/skill install {arg}"),
                new("Uninstall skill", "/skill uninstall {arg}"),
                new("Reload", "/reload"),
                new("Open commands folder", "app:open-commands-folder")
            ],
            argumentLabel: "owner/repo · marketplace alias · skillId@alias",
            customPrefix: "/skill ");

        yield return new SettingsSection(
            "memory",
            "Memory & workspace",
            "Team memory in .stemcode/memory, local lesson memory, and workspace initialisation (AGENTS.md, system prompt, ignore file).",
            [
                new("Lessons status", "/lessons status", AutoRun: true),
                new("Lessons on", "/lessons on"),
                new("Lessons off", "/lessons off"),
                new("List lessons", "/lessons list"),
                new("Search lessons", "/lessons search {arg}"),
                new("Initialise workspace files", "/init"),
                new("Open .stemcode folder", "app:open-stemcode-folder"),
                new("Open memory folder", "app:open-memory-folder")
            ],
            argumentLabel: "Search query",
            customPrefix: "/lessons ");

        yield return new SettingsSection(
            "code",
            "Code intelligence",
            "Local codebase index for semantic search and the language servers used for symbols, references and diagnostics.",
            [
                new("Index status", "/index status", AutoRun: true),
                new("Update index", "/index update"),
                new("Rebuild index", "/index rebuild"),
                new("List indexed files", "/index list 50"),
                new("Language servers", "/lsp status"),
                new("Refresh language servers", "/lsp refresh"),
                new("Servers for file", "/lsp file {arg}")
            ],
            argumentLabel: "File path",
            customPrefix: "/index ");

        yield return new SettingsSection(
            "sessions",
            "Sessions",
            "Session stats, export, import, sharing, compaction, branching and git worktrees.",
            [
                new("Session info", "/session", AutoRun: true),
                new("Compact context", "/compact"),
                new("Export JSON", "/export json"),
                new("Export HTML", "/export html"),
                new("Export trajectory", "/export trajectory"),
                new("Import JSON", "/import {arg}"),
                new("Share as gist", "/share"),
                new("Session tree", "/tree"),
                new("Clone session", "/clone"),
                new("Worktrees", "/worktree list"),
                new("Remove all worktrees", "/worktree remove --all")
            ],
            argumentLabel: "Path to session JSON",
            customPrefix: "/export ");

        yield return new SettingsSection(
            "budget",
            "Budget",
            "Spending limits from a local budget file or StemCode cloud budget controls.",
            [
                new("Budget status", "/budget status", AutoRun: true),
                new("Use local budget", "/budget local"),
                new("Use local file", "/budget local {arg}"),
                new("Connect cloud budget", "/budget cloud")
            ],
            argumentLabel: "Local budget file path",
            customPrefix: "/budget ");

        yield return new SettingsSection(
            "voice",
            "Voice",
            "Local, offline voice dictation. Press Ctrl+R in the composer or click the microphone to dictate.",
            [
                new("Choose model & microphone", "app:voice-setup"),
                new("Update voice models", "app:voice-update")
            ],
            customPrefix: string.Empty);

        yield return new SettingsSection(
            "about",
            "About & diagnostics",
            "Version, updates, diagnostics and analytics.",
            [
                new("Version", "/version", AutoRun: true),
                new("Run diagnostics", "/doctor"),
                new("Check for updates", "/update"),
                new("Install update", "/update now"),
                new("Disable analytics", "/disableanalytics"),
                new("Open config folder", "app:open-config-folder")
            ],
            customPrefix: "/");
    }
}

public sealed partial class SettingsSection : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public SettingsSection(
        string id,
        string title,
        string description,
        IReadOnlyList<SettingsAction> actions,
        string? argumentLabel = null,
        string customPrefix = "/")
    {
        Id = id;
        Title = title;
        Description = description;
        Actions = actions;
        ArgumentLabel = argumentLabel;
        CustomCommandPrefix = customPrefix;
    }

    public string Id { get; }

    public string Title { get; }

    public string Description { get; }

    public IReadOnlyList<SettingsAction> Actions { get; }

    public string? ArgumentLabel { get; }

    public string CustomCommandPrefix { get; }

    public Avalonia.Media.IBrush Background => IsSelected
        ? Avalonia.Media.Brush.Parse("#232327")
        : Avalonia.Media.Brushes.Transparent;

    public Avalonia.Media.IBrush Foreground => IsSelected
        ? Avalonia.Media.Brush.Parse("#ECECEE")
        : Avalonia.Media.Brush.Parse("#A1A1AA");

    partial void OnIsSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(Background));
        OnPropertyChanged(nameof(Foreground));
    }
}

public sealed record SettingsAction(string Label, string Command, bool AutoRun = false)
{
    public bool IsLocal => Command.StartsWith("app:", StringComparison.Ordinal);

    public string Tooltip => IsLocal ? Label : Command;
}
