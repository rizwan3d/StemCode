using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StemCode.Desktop.Models;
using StemCode.Desktop.Services;
using System.Collections.ObjectModel;
using System.Globalization;

namespace StemCode.Desktop.ViewModels;

public partial class ProjectViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly SectionHistoryService _sectionHistoryService;
    private List<WorkspaceSectionInfo> _allSections = [];

    [ObservableProperty]
    private ProjectInfo? _selectedProject;

    [ObservableProperty]
    private WorkspaceSectionInfo? _selectedSection;

    [ObservableProperty]
    private SessionListItem? _selectedSessionItem;

    [ObservableProperty]
    private string _sessionSearch = string.Empty;

    public ProjectViewModel(SettingsService settingsService, SectionHistoryService sectionHistoryService)
    {
        _settingsService = settingsService;
        _sectionHistoryService = sectionHistoryService;
        RemoveProjectCommand = new AsyncRelayCommand<ProjectInfo?>(RemoveProjectAsync);
        LoadProjects();
    }

    public ObservableCollection<ProjectInfo> Projects { get; } = new();

    public ObservableCollection<SessionListItem> SessionItems { get; } = new();

    public IAsyncRelayCommand<ProjectInfo?> RemoveProjectCommand { get; }

    public bool IsApplyingSelection { get; private set; }

    public string SelectedProjectName => SelectedProject?.Name ?? "No workspace";

    public string SelectedProjectPath => SelectedProject?.Path ?? "Open a folder to begin";

    public bool HasSessions => SessionItems.Count > 0;

    public bool HasNoSessions => SessionItems.Count == 0;

    public string SessionsEmptyText => SelectedProject is null
        ? "Open a workspace to see its sessions."
        : string.IsNullOrWhiteSpace(SessionSearch) ? "No sessions yet. Start one below." : "No sessions match your search.";

    public async Task AddProjectAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        string normalizedPath = Path.GetFullPath(path);
        ProjectInfo? existing = Projects.FirstOrDefault(project => string.Equals(project.Path, normalizedPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            Projects.Move(Projects.IndexOf(existing), 0);
            SelectedProject = existing;
            await _settingsService.SaveRecentProjectsAsync(Projects);
            return;
        }

        string name = Path.GetFileName(normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        ProjectInfo project = new(string.IsNullOrWhiteSpace(name) ? normalizedPath : name, normalizedPath, DateTimeOffset.Now);
        Projects.Insert(0, project);
        SelectedProject = project;
        await _settingsService.SaveRecentProjectsAsync(Projects);
    }

    private async Task RemoveProjectAsync(ProjectInfo? project)
    {
        if (project is null)
        {
            return;
        }

        bool wasSelected = SelectedProject is not null &&
            string.Equals(SelectedProject.Path, project.Path, StringComparison.OrdinalIgnoreCase);
        Projects.Remove(project);
        await _settingsService.SaveRecentProjectsAsync(Projects);
        if (wasSelected)
        {
            SelectedProject = Projects.FirstOrDefault();
        }
    }

    public async Task<bool> DeleteSessionAsync(SessionListItem? item)
    {
        if (item is null || SelectedProject is null)
        {
            return false;
        }

        bool removed = await _sectionHistoryService.DeleteSectionAsync(SelectedProject.Path, item.Section.SectionId);
        if (removed)
        {
            _allSections.RemoveAll(section => string.Equals(section.SectionId, item.Section.SectionId, StringComparison.OrdinalIgnoreCase));
            ApplyFilter();
        }

        return removed;
    }

    /// <summary>
    /// Reloads the session list. With <paramref name="selectSessionId"/> the matching session is selected
    /// (or nothing, if it is not persisted yet); without it the most recent session is selected.
    /// Selection changes made here are flagged with <see cref="IsApplyingSelection"/>.
    /// </summary>
    public async Task RefreshSectionsAsync(string? selectSessionId = null, bool preferMostRecent = true)
    {
        _allSections = SelectedProject is null
            ? []
            : (await _sectionHistoryService.ListSectionsAsync(SelectedProject.Path)).ToList();

        IsApplyingSelection = true;
        try
        {
            WorkspaceSectionInfo? selected = null;
            if (!string.IsNullOrWhiteSpace(selectSessionId))
            {
                selected = _allSections.FirstOrDefault(section => string.Equals(section.SectionId, selectSessionId, StringComparison.OrdinalIgnoreCase));
            }
            else if (preferMostRecent)
            {
                selected = _allSections.FirstOrDefault();
            }

            SelectedSection = selected;
            ApplyFilter();
        }
        finally
        {
            IsApplyingSelection = false;
        }
    }

    partial void OnSelectedProjectChanged(ProjectInfo? value)
    {
        OnPropertyChanged(nameof(SelectedProjectName));
        OnPropertyChanged(nameof(SelectedProjectPath));
        OnPropertyChanged(nameof(SessionsEmptyText));
    }

    partial void OnSessionSearchChanged(string value)
    {
        bool wasApplying = IsApplyingSelection;
        IsApplyingSelection = true;
        try
        {
            ApplyFilter();
        }
        finally
        {
            IsApplyingSelection = wasApplying;
        }
    }

    partial void OnSelectedSessionItemChanged(SessionListItem? value)
    {
        if (IsApplyingSelection || value is null)
        {
            return;
        }

        SelectedSection = value.Section;
    }

    partial void OnSelectedSectionChanged(WorkspaceSectionInfo? value)
    {
        foreach (SessionListItem item in SessionItems)
        {
            item.IsActive = value is not null && string.Equals(item.Section.SectionId, value.SectionId, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void ApplyFilter()
    {
        string query = SessionSearch.Trim();
        IEnumerable<WorkspaceSectionInfo> matches = string.IsNullOrEmpty(query)
            ? _allSections
            : _allSections.Where(section =>
                section.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                section.ActiveModelId.Contains(query, StringComparison.OrdinalIgnoreCase));

        SessionItems.Clear();
        foreach (WorkspaceSectionInfo section in matches)
        {
            SessionItems.Add(new SessionListItem(section)
            {
                IsActive = SelectedSection is not null && string.Equals(section.SectionId, SelectedSection.SectionId, StringComparison.OrdinalIgnoreCase)
            });
        }

        SelectedSessionItem = SessionItems.FirstOrDefault(static item => item.IsActive);
        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(HasNoSessions));
        OnPropertyChanged(nameof(SessionsEmptyText));
    }

    private void LoadProjects()
    {
        foreach (ProjectInfo project in _settingsService.LoadRecentProjects())
        {
            Projects.Add(project);
        }

        SelectedProject = Projects.FirstOrDefault();
    }
}

public sealed partial class SessionListItem : ObservableObject
{
    [ObservableProperty]
    private bool _isActive;

    public SessionListItem(WorkspaceSectionInfo section)
    {
        Section = section;
    }

    public WorkspaceSectionInfo Section { get; }

    public string Title => string.IsNullOrWhiteSpace(Section.Title) ? "Untitled session" : Section.Title;

    public string Detail => $"{RelativeTime(Section.UpdatedAtUtc)} · {Section.TurnCount} {(Section.TurnCount == 1 ? "turn" : "turns")}";

    public string Tooltip => $"{Title}\n{Section.ActiveModelId}\n{Section.UpdatedAtUtc.ToLocalTime():f}\n{Section.SectionId}";

    private static string RelativeTime(DateTimeOffset timestamp)
    {
        TimeSpan age = DateTimeOffset.UtcNow - timestamp;
        if (age < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{(int)age.TotalMinutes}m ago";
        }

        if (age < TimeSpan.FromDays(1))
        {
            return $"{(int)age.TotalHours}h ago";
        }

        if (age < TimeSpan.FromDays(7))
        {
            return $"{(int)age.TotalDays}d ago";
        }

        return timestamp.ToLocalTime().ToString("MMM d", CultureInfo.CurrentCulture);
    }
}
