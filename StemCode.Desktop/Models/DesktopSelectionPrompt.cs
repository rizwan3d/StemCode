using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace StemCode.Desktop.Models;

public sealed partial class DesktopSelectionPrompt : ObservableObject
{
    private readonly Action _onCancelled;
    private readonly Action<int, bool> _onSelected;
    private bool _isResolved;

    [ObservableProperty]
    private int _remainingSeconds;

    [ObservableProperty]
    private int _selectedIndex;

    public DesktopSelectionPrompt(
        string title,
        string? description,
        IReadOnlyList<DesktopSelectionPromptOptionDescriptor> options,
        int defaultIndex,
        bool allowCancellation,
        TimeSpan? autoSelectAfter,
        Action<int, bool> onSelected,
        Action onCancelled)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(onSelected);
        ArgumentNullException.ThrowIfNull(onCancelled);

        Title = string.IsNullOrWhiteSpace(title) ? "Prompt" : title.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        AllowCancellation = allowCancellation;
        _onSelected = onSelected;
        _onCancelled = onCancelled;

        SelectedIndex = Math.Clamp(defaultIndex, 0, Math.Max(0, options.Count - 1));
        DeadlineUtc = autoSelectAfter.HasValue
            ? DateTimeOffset.UtcNow.Add(autoSelectAfter.Value)
            : null;
        RemainingSeconds = GetRemainingSeconds();

        for (int index = 0; index < options.Count; index++)
        {
            int optionIndex = index;
            DesktopSelectionPromptOptionDescriptor option = options[index];
            Options.Add(new DesktopSelectionPromptOption(
                option.Label,
                option.Description,
                ShouldShowSectionHeader(options, index) ? option.Section : null,
                optionIndex == SelectedIndex,
                new RelayCommand(
                    () => Select(optionIndex),
                    () => !_isResolved))
            {
                Number = optionIndex + 1
            });
        }

        CancelCommand = new RelayCommand(
            Cancel,
            () => AllowCancellation && !_isResolved);
    }

    public bool AllowCancellation { get; }

    public IRelayCommand CancelCommand { get; }

    public string CountdownText => HasCountdown
        ? $"Default in {RemainingSeconds}s"
        : "Waiting for selection";

    public DateTimeOffset? DeadlineUtc { get; }

    public string? Description { get; }

    public bool HasCountdown => DeadlineUtc is not null;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public ObservableCollection<DesktopSelectionPromptOption> Options { get; } = new();

    public string Title { get; }

    public event EventHandler? Dismissed;

    public void Dismiss()
    {
        if (_isResolved)
        {
            return;
        }

        _isResolved = true;
        NotifyCommandStatesChanged();
        Dismissed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Selects an option by its 1-based number (keyboard shortcut). Returns false when out of range.</summary>
    public bool TrySelectNumber(int number)
    {
        if (_isResolved || number < 1 || number > Options.Count)
        {
            return false;
        }

        Resolve(number - 1, isAutomatic: false);
        return true;
    }

    public bool TryCancel()
    {
        if (!AllowCancellation || _isResolved)
        {
            return false;
        }

        Cancel();
        return true;
    }

    public void SelectDefault()
    {
        Resolve(SelectedIndex, isAutomatic: false);
    }

    public void Tick()
    {
        if (_isResolved || DeadlineUtc is null)
        {
            return;
        }

        RemainingSeconds = GetRemainingSeconds();
        if (RemainingSeconds <= 0)
        {
            Resolve(SelectedIndex, isAutomatic: true);
        }
    }

    private void Cancel()
    {
        if (_isResolved)
        {
            return;
        }

        _isResolved = true;
        NotifyCommandStatesChanged();
        _onCancelled();
        Dismissed?.Invoke(this, EventArgs.Empty);
    }

    private int GetRemainingSeconds()
    {
        if (DeadlineUtc is null)
        {
            return 0;
        }

        TimeSpan remaining = DeadlineUtc.Value - DateTimeOffset.UtcNow;
        return Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds));
    }

    private static bool ShouldShowSectionHeader(
        IReadOnlyList<DesktopSelectionPromptOptionDescriptor> options,
        int index)
    {
        if (index < 0 ||
            index >= options.Count ||
            string.IsNullOrWhiteSpace(options[index].Section))
        {
            return false;
        }

        return index == 0 ||
            !string.Equals(
                options[index - 1].Section,
                options[index].Section,
                StringComparison.Ordinal);
    }

    private void NotifyCommandStatesChanged()
    {
        CancelCommand.NotifyCanExecuteChanged();
        foreach (DesktopSelectionPromptOption option in Options)
        {
            option.SelectCommand.NotifyCanExecuteChanged();
        }
    }

    private void Resolve(int index, bool isAutomatic)
    {
        if (_isResolved || Options.Count == 0)
        {
            return;
        }

        SelectedIndex = Math.Clamp(index, 0, Options.Count - 1);
        _isResolved = true;
        NotifyCommandStatesChanged();
        _onSelected(SelectedIndex, isAutomatic);
        Dismissed?.Invoke(this, EventArgs.Empty);
    }

    private void Select(int index)
    {
        Resolve(index, isAutomatic: false);
    }

    partial void OnRemainingSecondsChanged(int value)
    {
        OnPropertyChanged(nameof(CountdownText));
    }
}

public sealed record DesktopSelectionPromptOption(
    string Label,
    string? Description,
    string? Section,
    bool IsDefault,
    IRelayCommand SelectCommand)
{
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public int Number { get; init; }

    public string NumberText => Number is > 0 and < 10 ? Number.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;

    public bool HasSection => !string.IsNullOrWhiteSpace(Section);

    public bool IsNotDefault => !IsDefault;
}

public sealed record DesktopSelectionPromptOptionDescriptor(
    string Label,
    string? Description,
    string? Section = null);
