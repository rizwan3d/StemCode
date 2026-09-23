namespace StemCode.Sdk;

/// <summary>
/// Public SDK names for the full StemCode build-tool preset. These tools form
/// the repository-aware agent bundle exposed by <see cref="StemCodeClientBuilder.UseBuildTool"/>.
/// </summary>
public static class StemCodeBuildTools
{
    /// <summary>The built-in profile selected by <see cref="StemCodeClientBuilder.UseBuildTool"/>.</summary>
    public const string ProfileName = "build";

    /// <summary>Delegates a focused task to a subagent.</summary>
    public const string AgentDelegate = "agent_delegate";

    /// <summary>Runs several delegated subagent tasks as one coordinated handoff.</summary>
    public const string AgentOrchestrate = "agent_orchestrate";

    /// <summary>Applies unified patches to workspace files.</summary>
    public const string ApplyPatch = "apply_patch";

    /// <summary>Asks the host application or user a clarification question.</summary>
    public const string AskQuestion = "ask_question";

    /// <summary>Indexes and searches codebase concepts across the repository.</summary>
    public const string CodebaseIndex = "codebase_index";

    /// <summary>Uses language-server-backed code intelligence.</summary>
    public const string CodeIntelligence = "code_intelligence";

    /// <summary>Lists workspace directories.</summary>
    public const string DirectoryList = "directory_list";

    /// <summary>Deletes workspace files.</summary>
    public const string FileDelete = "file_delete";

    /// <summary>Reads workspace files.</summary>
    public const string FileRead = "file_read";

    /// <summary>Writes content into a workspace file at a target location.</summary>
    public const string InsertContent = "insert_content";

    /// <summary>Writes workspace files.</summary>
    public const string FileWrite = "file_write";

    /// <summary>Runs a headless browser for page inspection and automation.</summary>
    public const string HeadlessBrowser = "headless_browser";

    /// <summary>Produces and updates structured execution plans.</summary>
    public const string PlanningMode = "planning_mode";

    /// <summary>Reads and writes repository memory.</summary>
    public const string RepoMemory = "repo_memory";

    /// <summary>Searches files by name or path.</summary>
    public const string SearchFiles = "search_files";

    /// <summary>Searches and replaces text in workspace files.</summary>
    public const string SearchAndReplace = "search_and_replace";

    /// <summary>Runs shell commands through the configured permission model.</summary>
    public const string ShellCommand = "shell_command";

    /// <summary>Loads workspace skills and routing instructions.</summary>
    public const string SkillLoad = "skill_load";

    /// <summary>Searches text across the workspace.</summary>
    public const string TextSearch = "text_search";

    /// <summary>Updates the active plan.</summary>
    public const string UpdatePlan = "update_plan";

    /// <summary>Searches the web when enabled by the host configuration.</summary>
    public const string WebSearch = "web_search";

    /// <summary>All built-in tool names enabled by the build-tool preset.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        AgentDelegate,
        AgentOrchestrate,
        ApplyPatch,
        AskQuestion,
        CodebaseIndex,
        CodeIntelligence,
        DirectoryList,
        FileDelete,
        FileRead,
        InsertContent,
        FileWrite,
        HeadlessBrowser,
        PlanningMode,
        RepoMemory,
        SearchFiles,
        SearchAndReplace,
        ShellCommand,
        SkillLoad,
        TextSearch,
        UpdatePlan,
        WebSearch
    ];
}
