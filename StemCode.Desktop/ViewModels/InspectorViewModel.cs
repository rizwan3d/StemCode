using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StemCode.Application.Models;
using StemCode.Application.Tools.Models;
using StemCode.Desktop.Controls;
using StemCode.Desktop.Models;
using StemCode.Desktop.Services;
using System.Collections.ObjectModel;

namespace StemCode.Desktop.ViewModels;

public enum InspectorTab
{
    Changes,
    Plan,
    Terminals,
    Activity
}

/// <summary>Right-hand panel: session file changes with diffs, plan, background terminals, activity log.</summary>
public partial class InspectorViewModel : ViewModelBase
{
    private readonly ChatViewModel _chat;
    private readonly DispatcherTimer _terminalTimer;
    private CancellationTokenSource? _diffCts;

    [ObservableProperty]
    private InspectorTab _selectedTab = InspectorTab.Changes;

    [ObservableProperty]
    private FileEditSummary? _selectedChange;

    [ObservableProperty]
    private string _diffTitle = string.Empty;

    [ObservableProperty]
    private bool _isLoadingDiff;

    [ObservableProperty]
    private TerminalRow? _selectedTerminal;

    [ObservableProperty]
    private string _terminalOutput = string.Empty;

    [ObservableProperty]
    private bool _isVisible;

    public InspectorViewModel(ChatViewModel chat)
    {
        _chat = chat;
        SelectTabCommand = new RelayCommand<string?>(tab =>
        {
            if (Enum.TryParse(tab, ignoreCase: true, out InspectorTab parsed))
            {
                SelectedTab = parsed;
            }
        });
        ShowWorkspaceDiffCommand = new AsyncRelayCommand(ShowWorkspaceDiffAsync);
        OpenSelectedChangeCommand = new RelayCommand(() =>
        {
            if (SelectedChange is not null)
            {
                MarkdownMessageView.OpenPath(SelectedChange.AbsolutePath);
            }
        });
        RefreshTerminalsCommand = new AsyncRelayCommand(RefreshTerminalsAsync);
        StopTerminalCommand = new AsyncRelayCommand<TerminalRow?>(StopTerminalAsync);
        StopAllTerminalsCommand = new AsyncRelayCommand(async () =>
        {
            await _chat.RunCommandCaptureAsync("/terminals stop all");
            await RefreshTerminalsAsync();
        });

        _terminalTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _terminalTimer.Tick += async (_, _) => await RefreshTerminalsAsync();

        _chat.RunCompleted += async (_, _) =>
        {
            // The plan tab is useful mid-run; once edits exist, the diff is what people want next.
            if (SelectedTab == InspectorTab.Plan && _chat.ChangedFiles.Count > 0)
            {
                SelectedTab = InspectorTab.Changes;
            }

            if (SelectedChange is not null && _chat.ChangedFiles.All(file => file.AbsolutePath != SelectedChange.AbsolutePath))
            {
                SelectedChange = null;
            }
            else if (SelectedChange is not null)
            {
                await LoadDiffAsync(SelectedChange);
            }

            await RefreshTerminalsAsync();
        };
        _chat.PlanSteps.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(PlanBadge));
            if (_chat.PlanSteps.Count > 0 && _chat.IsPromptRunning && SelectedTab == InspectorTab.Changes && _chat.ChangedFiles.Count == 0)
            {
                SelectedTab = InspectorTab.Plan;
            }
        };
        _chat.ChangedFiles.CollectionChanged += (_, _) => NotifyChangesUpdated();
    }

    public ChatViewModel Chat => _chat;

    public ObservableCollection<ToolOutputLine> DiffLines { get; } = new();

    public ObservableCollection<TerminalRow> Terminals { get; } = new();

    public IRelayCommand<string?> SelectTabCommand { get; }

    public IAsyncRelayCommand ShowWorkspaceDiffCommand { get; }

    public IRelayCommand OpenSelectedChangeCommand { get; }

    public IAsyncRelayCommand RefreshTerminalsCommand { get; }

    public IAsyncRelayCommand<TerminalRow?> StopTerminalCommand { get; }

    public IAsyncRelayCommand StopAllTerminalsCommand { get; }

    public bool IsChangesTab => SelectedTab == InspectorTab.Changes;

    public bool IsPlanTab => SelectedTab == InspectorTab.Plan;

    public bool IsTerminalsTab => SelectedTab == InspectorTab.Terminals;

    public bool IsActivityTab => SelectedTab == InspectorTab.Activity;

    public bool HasDiff => DiffLines.Count > 0;

    public bool HasSelectedChange => SelectedChange is not null;

    public bool HasTerminals => Terminals.Count > 0;

    public bool HasNoTerminals => Terminals.Count == 0;

    public bool HasSelectedTerminal => SelectedTerminal is not null;

    public string ChangesBadge => _chat.ChangedFiles.Count == 0 ? string.Empty : _chat.ChangedFiles.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string PlanBadge => _chat.PlanSteps.Count == 0
        ? string.Empty
        : $"{_chat.PlanSteps.Count(static step => step.State == PlanStepState.Done)}/{_chat.PlanSteps.Count}";

    public string TerminalsBadge => Terminals.Count(static row => row.IsRunning) is int running and > 0 ? running.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;

    public string ChangesTotalsText
    {
        get
        {
            int added = _chat.ChangedFiles.Sum(static file => file.AddedLineCount);
            int removed = _chat.ChangedFiles.Sum(static file => file.RemovedLineCount);
            return _chat.ChangedFiles.Count == 0
                ? "No files changed in this session yet."
                : $"{_chat.ChangedFiles.Count} {(_chat.ChangedFiles.Count == 1 ? "file" : "files")} · +{added} −{removed}";
        }
    }

    partial void OnSelectedTabChanged(InspectorTab value)
    {
        OnPropertyChanged(nameof(IsChangesTab));
        OnPropertyChanged(nameof(IsPlanTab));
        OnPropertyChanged(nameof(IsTerminalsTab));
        OnPropertyChanged(nameof(IsActivityTab));
        UpdateTerminalPolling();
        if (value == InspectorTab.Terminals)
        {
            _ = RefreshTerminalsAsync();
        }
    }

    partial void OnIsVisibleChanged(bool value) => UpdateTerminalPolling();

    partial void OnSelectedChangeChanged(FileEditSummary? value)
    {
        OnPropertyChanged(nameof(HasSelectedChange));
        if (value is null)
        {
            DiffLines.Clear();
            DiffTitle = string.Empty;
            OnPropertyChanged(nameof(HasDiff));
            return;
        }

        _ = LoadDiffAsync(value);
    }

    partial void OnSelectedTerminalChanged(TerminalRow? value)
    {
        OnPropertyChanged(nameof(HasSelectedTerminal));
        _ = LoadTerminalOutputAsync();
    }

    public void NotifyChangesUpdated()
    {
        OnPropertyChanged(nameof(ChangesBadge));
        OnPropertyChanged(nameof(ChangesTotalsText));
    }

    private void UpdateTerminalPolling()
    {
        if (IsVisible && SelectedTab == InspectorTab.Terminals)
        {
            _terminalTimer.Start();
        }
        else
        {
            _terminalTimer.Stop();
        }
    }

    private async Task LoadDiffAsync(FileEditSummary change)
    {
        string? root = _chat.ActiveProject?.Path;
        if (root is null)
        {
            return;
        }

        _diffCts?.Cancel();
        CancellationTokenSource cts = new();
        _diffCts = cts;
        DiffTitle = change.DisplayPath;
        IsLoadingDiff = true;
        try
        {
            string diff = await Task.Run(() => GitDiffService.GetFileDiffAsync(root, change.AbsolutePath, cts.Token), cts.Token);
            if (!cts.IsCancellationRequested)
            {
                SetDiffText(diff);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_diffCts, cts))
            {
                IsLoadingDiff = false;
            }
        }
    }

    private async Task ShowWorkspaceDiffAsync()
    {
        string? root = _chat.ActiveProject?.Path;
        if (root is null)
        {
            return;
        }

        SelectedTab = InspectorTab.Changes;
        _diffCts?.Cancel();
        DiffTitle = "All uncommitted changes";
        IsLoadingDiff = true;
        try
        {
            SetDiffText(await Task.Run(() => GitDiffService.GetWorkspaceDiffAsync(root)));
        }
        finally
        {
            IsLoadingDiff = false;
        }
    }

    private void SetDiffText(string diff)
    {
        DiffLines.Clear();
        bool isDiff = ToolOutputLine.LooksLikeDiff(diff);
        foreach (ToolOutputLine line in ToolOutputLine.Parse(diff, 5_000))
        {
            DiffLines.Add(isDiff ? line : line with { Kind = ToolOutputLineKind.Plain });
        }

        OnPropertyChanged(nameof(HasDiff));
    }

    public async Task RefreshTerminalsAsync()
    {
        IReadOnlyList<BackgroundTerminalInfo> terminals = await _chat.Runner.ListBackgroundTerminalsAsync();
        string? selectedId = SelectedTerminal?.Info.Id;

        Terminals.Clear();
        foreach (BackgroundTerminalInfo terminal in terminals.OrderByDescending(static terminal => terminal.StartedAtUtc))
        {
            Terminals.Add(new TerminalRow(terminal));
        }

        SelectedTerminal = Terminals.FirstOrDefault(row => row.Info.Id == selectedId) ?? (selectedId is null ? null : Terminals.FirstOrDefault());
        OnPropertyChanged(nameof(HasTerminals));
        OnPropertyChanged(nameof(HasNoTerminals));
        OnPropertyChanged(nameof(TerminalsBadge));
        await LoadTerminalOutputAsync();
    }

    private async Task LoadTerminalOutputAsync()
    {
        TerminalRow? row = SelectedTerminal;
        if (row is null)
        {
            TerminalOutput = string.Empty;
            return;
        }

        ShellCommandExecutionResult? result = await _chat.Runner.ReadBackgroundTerminalAsync(row.Info.Id);
        if (result is null)
        {
            TerminalOutput = "Output is not available for this terminal.";
            return;
        }

        string output = result.StandardOutput;
        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            output += (output.Length > 0 ? "\n" : string.Empty) + result.StandardError;
        }

        TerminalOutput = string.IsNullOrWhiteSpace(output) ? "(no output yet)" : output.TrimEnd();
    }

    private async Task StopTerminalAsync(TerminalRow? row)
    {
        if (row is null)
        {
            return;
        }

        await _chat.RunCommandCaptureAsync($"/terminals stop {row.Info.Id}");
        await RefreshTerminalsAsync();
    }
}

public sealed class TerminalRow
{
    public TerminalRow(BackgroundTerminalInfo info)
    {
        Info = info;
    }

    public BackgroundTerminalInfo Info { get; }

    public string Command => Info.Command;

    public bool IsRunning => string.Equals(Info.Status, "running", StringComparison.OrdinalIgnoreCase);

    public string StatusText => Info.ExitCode is int code && !IsRunning ? $"{Info.Status} · exit {code}" : Info.Status;

    public Avalonia.Media.IBrush StatusBrush => IsRunning
        ? Avalonia.Media.Brush.Parse("#FBBF24")
        : Info.ExitCode is 0 or null ? Avalonia.Media.Brush.Parse("#4ADE80") : Avalonia.Media.Brush.Parse("#F87171");

    public string StartedText => Info.StartedAtUtc.ToLocalTime().ToString("h:mm:ss tt");
}
