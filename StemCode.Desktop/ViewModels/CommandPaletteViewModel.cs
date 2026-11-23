using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StemCode.Desktop.Services;
using System.Collections.ObjectModel;

namespace StemCode.Desktop.ViewModels;

/// <summary>Ctrl+K palette over app actions, slash commands and sessions.</summary>
public partial class CommandPaletteViewModel : ViewModelBase
{
    private const int MaxResults = 60;

    private readonly Func<IReadOnlyList<PaletteEntry>> _entrySource;
    private IReadOnlyList<PaletteEntry> _entries = [];
    private int _selectedIndex;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _query = string.Empty;

    public CommandPaletteViewModel(Func<IReadOnlyList<PaletteEntry>> entrySource)
    {
        _entrySource = entrySource;
        CloseCommand = new RelayCommand(Close);
    }

    public ObservableCollection<PaletteEntry> Results { get; } = new();

    public IRelayCommand CloseCommand { get; }

    public event EventHandler<PaletteEntry>? SelectionChanged;

    public void Open(string initialQuery = "")
    {
        _entries = _entrySource();
        foreach (PaletteEntry entry in _entries)
        {
            entry.Executor = Execute;
        }

        Query = initialQuery;
        Refresh();
        IsOpen = true;
    }

    public void Close()
    {
        IsOpen = false;
        Query = string.Empty;
    }

    partial void OnQueryChanged(string value) => Refresh();

    public void Move(int delta)
    {
        if (Results.Count == 0)
        {
            return;
        }

        Results[_selectedIndex].IsSelected = false;
        _selectedIndex = (_selectedIndex + delta + Results.Count) % Results.Count;
        Results[_selectedIndex].IsSelected = true;
        SelectionChanged?.Invoke(this, Results[_selectedIndex]);
    }

    public void ExecuteSelected()
    {
        if (Results.Count > 0)
        {
            Execute(Results[_selectedIndex]);
        }
    }

    public void Execute(PaletteEntry entry)
    {
        Close();
        entry.Run();
    }

    private void Refresh()
    {
        string query = Query.Trim();
        IEnumerable<PaletteEntry> matches = query.Length == 0
            ? _entries
            : _entries
                .Select(entry => (entry, score: Score(entry, query)))
                .Where(static match => match.score > 0)
                .OrderByDescending(static match => match.score)
                .Select(static match => match.entry);

        foreach (PaletteEntry entry in Results)
        {
            entry.IsSelected = false;
        }

        Results.Clear();
        foreach (PaletteEntry entry in matches.Take(MaxResults))
        {
            Results.Add(entry);
        }

        _selectedIndex = 0;
        if (Results.Count > 0)
        {
            Results[0].IsSelected = true;
        }
    }

    private static int Score(PaletteEntry entry, string query)
    {
        if (entry.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 400;
        }

        if (entry.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 300;
        }

        if (entry.Group.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 150;
        }

        return entry.Detail?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ? 100 : 0;
    }

    public static PaletteEntry FromCommand(DesktopCommandInfo command, Action<DesktopCommandInfo> run)
    {
        return new PaletteEntry(
            "Commands",
            command.Usage,
            command.Description,
            command.SourceLabel.Length == 0 ? null : command.SourceLabel,
            () => run(command));
    }
}

public sealed partial class PaletteEntry : ObservableObject
{
    private static readonly Avalonia.Media.IBrush SelectedBackground = Avalonia.Media.Brush.Parse("#26262B");

    [ObservableProperty]
    private bool _isSelected;

    public PaletteEntry(string group, string title, string? detail, string? shortcut, Action run)
    {
        Group = group;
        Title = title;
        Detail = detail;
        Shortcut = shortcut;
        Run = run;
        ExecuteCommand = new RelayCommand(() => Executor?.Invoke(this));
    }

    public string Group { get; }

    public string Title { get; }

    public string? Detail { get; }

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    public string? Shortcut { get; }

    public bool HasShortcut => !string.IsNullOrWhiteSpace(Shortcut);

    public Action Run { get; }

    public IRelayCommand ExecuteCommand { get; }

    internal Action<PaletteEntry>? Executor { get; set; }

    public Avalonia.Media.IBrush Background => IsSelected ? SelectedBackground : Avalonia.Media.Brushes.Transparent;

    partial void OnIsSelectedChanged(bool value) => OnPropertyChanged(nameof(Background));
}
