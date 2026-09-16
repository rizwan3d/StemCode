using StemCode.Application.Abstractions;
using StemCode.Application.Exceptions;
using StemCode.Application.Models;
using StemCode.Application.Utilities;
using System.Text;

namespace StemCode.Application.Commands;

internal sealed class InitCommandHandler : IReplCommandHandler
{
    private const string WorkspaceDirectoryName = ".stemcode";
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private readonly ISelectionPrompt _selectionPrompt;
    private readonly IConfirmationPrompt _confirmationPrompt;

    public InitCommandHandler(
        ISelectionPrompt selectionPrompt,
        IConfirmationPrompt confirmationPrompt)
    {
        _selectionPrompt = selectionPrompt;
        _confirmationPrompt = confirmationPrompt;
    }

    public string CommandName => "init";

    public string Description => "Choose and initialize workspace-local StemCode files.";

    public string Usage => "/init [recommended|minimal|custom]";

    public async Task<ReplCommandResult> ExecuteAsync(
        ReplCommandContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        InitScaffoldOptions options;
        try
        {
            if (!TryResolveOptions(context, out options))
            {
                return ReplCommandResult.Continue(
                    "Usage: /init [recommended|minimal|custom]",
                    ReplFeedbackKind.Error);
            }

            if (options.Preset == InitPreset.Prompt)
            {
                options = await PromptForOptionsAsync(cancellationToken);
            }
            else if (options.Preset == InitPreset.Custom)
            {
                options = await PromptForCustomOptionsAsync(cancellationToken);
            }
        }
        catch (PromptCancelledException)
        {
            return ReplCommandResult.Continue(
                "Workspace initialization cancelled.",
                ReplFeedbackKind.Warning);
        }

        string workspaceRoot = Path.GetFullPath(context.Session.WorkspacePath);
        string workspaceDirectory = Path.Combine(workspaceRoot, WorkspaceDirectoryName);
        InitSummary summary = new(workspaceRoot, options);

        try
        {
            EnsureDirectory(workspaceRoot, workspaceDirectory, summary);

            await EnsureFileAsync(
                workspaceRoot,
                Path.Combine(workspaceDirectory, "agent-profile.json"),
                AgentProfileTemplate,
                summary,
                cancellationToken);

            if (options.IncludeReadme)
            {
                await EnsureFileAsync(
                    workspaceRoot,
                    Path.Combine(workspaceDirectory, "README.md"),
                    ReadmeTemplate,
                    summary,
                    cancellationToken);
            }
            else
            {
                summary.Skipped.Add(".stemcode/README.md");
            }

            if (options.IncludeGitIgnore)
            {
                await EnsureFileAsync(
                    workspaceRoot,
                    Path.Combine(workspaceDirectory, ".gitignore"),
                    GitIgnoreTemplate,
                    summary,
                    cancellationToken);
            }
            else
            {
                summary.Skipped.Add(".stemcode/.gitignore");
            }

            if (options.IncludeStemCodeIgnore)
            {
                await EnsureFileAsync(
                    workspaceRoot,
                    Path.Combine(workspaceDirectory, ".stemcodeignore"),
                    StemCodeIgnoreTemplate,
                    summary,
                    cancellationToken);
            }
            else
            {
                summary.Skipped.Add(".stemcode/.stemcodeignore");
            }

            if (options.IncludeRuntimeDirectories)
            {
                EnsureDirectory(workspaceRoot, Path.Combine(workspaceDirectory, "cache"), summary);
                EnsureDirectory(workspaceRoot, Path.Combine(workspaceDirectory, "logs"), summary);
                await EnsureFileAsync(
                    workspaceRoot,
                    Path.Combine(workspaceDirectory, "logs", ".gitkeep"),
                    string.Empty,
                    summary,
                    cancellationToken);
            }
            else
            {
                summary.Skipped.Add(".stemcode/cache/");
                summary.Skipped.Add(".stemcode/logs/");
            }

            if (options.IncludeAgentTemplate)
            {
                EnsureDirectory(workspaceRoot, Path.Combine(workspaceDirectory, "agents"), summary);
                await EnsureFileAsync(
                    workspaceRoot,
                    Path.Combine(workspaceDirectory, "agents", "code-reviewer.md.template"),
                    AgentTemplate,
                    summary,
                    cancellationToken);
            }
            else
            {
                summary.Skipped.Add(".stemcode/agents/code-reviewer.md.template");
            }

            if (options.IncludeSkillTemplate)
            {
                EnsureDirectory(workspaceRoot, Path.Combine(workspaceDirectory, "skills"), summary);
                EnsureDirectory(workspaceRoot, Path.Combine(workspaceDirectory, "skills", "dotnet"), summary);
                await EnsureFileAsync(
                    workspaceRoot,
                    Path.Combine(workspaceDirectory, "skills", "dotnet", "SKILL.md.template"),
                    SkillTemplate,
                    summary,
                    cancellationToken);
            }
            else
            {
                summary.Skipped.Add(".stemcode/skills/dotnet/SKILL.md.template");
            }

            if (options.IncludeRepoMemory || options.IncludeLessonsJournal)
            {
                EnsureDirectory(workspaceRoot, Path.Combine(workspaceDirectory, "memory"), summary);
            }

            if (options.IncludeRepoMemory)
            {
                foreach (RepoMemoryDocumentDefinition document in RepoMemoryDocuments.All)
                {
                    await EnsureFileAsync(
                        workspaceRoot,
                        Path.Combine(workspaceDirectory, "memory", document.FileName),
                        RepoMemoryDocuments.CreateTemplate(document),
                        summary,
                        cancellationToken);
                }
            }
            else
            {
                summary.Skipped.Add(".stemcode/memory/*.md");
            }

            if (options.IncludeLessonsJournal)
            {
                await EnsureFileAsync(
                    workspaceRoot,
                    Path.Combine(workspaceDirectory, "memory", "lessons.jsonl"),
                    string.Empty,
                    summary,
                    cancellationToken);
            }
            else
            {
                summary.Skipped.Add(".stemcode/memory/lessons.jsonl");
            }

            if (options.IncludeSystemPromptTemplate)
            {
                await EnsureFileAsync(
                    workspaceRoot,
                    Path.Combine(workspaceDirectory, "SystemPrompt.md.template"),
                    SystemPromptTemplate,
                    summary,
                    cancellationToken);
            }
            else
            {
                summary.Skipped.Add(".stemcode/SystemPrompt.md.template");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ReplCommandResult.Continue(
                $"Could not initialize {WorkspaceDirectoryName}: {exception.Message}",
                ReplFeedbackKind.Error);
        }

        return ReplCommandResult.Continue(
            summary.Format(),
            ReplFeedbackKind.Info);
    }

    private static bool TryResolveOptions(
        ReplCommandContext context,
        out InitScaffoldOptions options)
    {
        options = InitScaffoldOptions.Prompt;
        if (context.Arguments.Count == 0)
        {
            return true;
        }

        if (context.Arguments.Count > 1)
        {
            return false;
        }

        string normalizedArgument = context.Arguments[0]
            .Trim()
            .TrimStart('-', '/');
        options = normalizedArgument.ToLowerInvariant() switch
        {
            "recommended" or "default" or "standard" => InitScaffoldOptions.Recommended,
            "minimal" or "core" => InitScaffoldOptions.Minimal,
            "custom" => InitScaffoldOptions.Custom,
            "help" or "h" or "?" => InitScaffoldOptions.Help,
            _ => InitScaffoldOptions.Invalid
        };

        return options.Preset is not InitPreset.Invalid and not InitPreset.Help;
    }

    private async Task<InitScaffoldOptions> PromptForOptionsAsync(CancellationToken cancellationToken)
    {
        InitPreset preset = await _selectionPrompt.PromptAsync(
            new SelectionPromptRequest<InitPreset>(
                "Choose workspace files to add",
                [
                    new SelectionPromptOption<InitPreset>(
                        "Recommended",
                        InitPreset.Recommended,
                        "Core config, ignores, repo memory, runtime folders, and inactive agent/skill templates. No SystemPrompt override."),
                    new SelectionPromptOption<InitPreset>(
                        "Minimal",
                        InitPreset.Minimal,
                        "Only core config, README, and ignore files."),
                    new SelectionPromptOption<InitPreset>(
                        "Custom",
                        InitPreset.Custom,
                        "Choose each optional file group, including the advanced SystemPrompt template.")
                ],
                "Most projects should use AGENTS.md or memory docs for instructions. SystemPrompt is an advanced base-prompt override and is skipped unless you choose it.",
                DefaultIndex: 0,
                AllowCancellation: true),
            cancellationToken);

        return preset == InitPreset.Custom
            ? await PromptForCustomOptionsAsync(cancellationToken)
            : InitScaffoldOptions.FromPreset(preset);
    }

    private async Task<InitScaffoldOptions> PromptForCustomOptionsAsync(CancellationToken cancellationToken)
    {
        bool includeReadme = await ConfirmAsync(
            "Add workspace README?",
            "Creates .stemcode/README.md with concise notes about workspace-local StemCode files.",
            defaultValue: true,
            cancellationToken);
        bool includeGitIgnore = await ConfirmAsync(
            "Add .stemcode/.gitignore?",
            "Keeps cache, logs, and local JSONL files out of source control.",
            defaultValue: true,
            cancellationToken);
        bool includeStemCodeIgnore = await ConfirmAsync(
            "Add .stemcode/.stemcodeignore?",
            "Excludes common secrets, build output, and local runtime files from StemCode file tools.",
            defaultValue: true,
            cancellationToken);
        bool includeRepoMemory = await ConfirmAsync(
            "Add repo memory markdown files?",
            "Creates architecture, conventions, decisions, known-issues, and test-strategy templates.",
            defaultValue: true,
            cancellationToken);
        bool includeLessonsJournal = await ConfirmAsync(
            "Add local lessons journal?",
            "Creates .stemcode/memory/lessons.jsonl for reusable local lessons. It is off by default, gitignored by default, and can later be injected automatically into prompts when enabled.",
            defaultValue: true,
            cancellationToken);
        bool includeAgentTemplate = await ConfirmAsync(
            "Add sample custom agent template?",
            "Creates an inactive code-reviewer template under .stemcode/agents/.",
            defaultValue: true,
            cancellationToken);
        bool includeSkillTemplate = await ConfirmAsync(
            "Add sample workspace skill template?",
            "Creates an inactive .NET skill template under .stemcode/skills/.",
            defaultValue: true,
            cancellationToken);
        bool includeRuntimeDirectories = await ConfirmAsync(
            "Add cache and log folders?",
            "Creates .stemcode/cache/ and .stemcode/logs/ for local runtime data.",
            defaultValue: true,
            cancellationToken);
        bool includeSystemPromptTemplate = await ConfirmAsync(
            "Add inactive SystemPrompt template?",
            "Creates .stemcode/SystemPrompt.md.template. Edit and rename it to SystemPrompt.md only when this workspace needs a custom base prompt.",
            defaultValue: false,
            cancellationToken);

        return new InitScaffoldOptions(
            InitPreset.Custom,
            "Custom",
            includeReadme,
            includeGitIgnore,
            includeStemCodeIgnore,
            includeRuntimeDirectories,
            includeAgentTemplate,
            includeSkillTemplate,
            includeRepoMemory,
            includeLessonsJournal,
            includeSystemPromptTemplate);
    }

    private Task<bool> ConfirmAsync(
        string title,
        string description,
        bool defaultValue,
        CancellationToken cancellationToken)
    {
        return _confirmationPrompt.PromptAsync(
            new ConfirmationPromptRequest(
                title,
                description,
                defaultValue,
                AllowCancellation: true),
            cancellationToken);
    }

    private static void EnsureDirectory(
        string workspaceRoot,
        string directoryPath,
        InitSummary summary)
    {
        if (Directory.Exists(directoryPath))
        {
            summary.Existing.Add(ToRelativePath(workspaceRoot, directoryPath) + "/");
            return;
        }

        Directory.CreateDirectory(directoryPath);
        summary.Created.Add(ToRelativePath(workspaceRoot, directoryPath) + "/");
    }

    private static async Task EnsureFileAsync(
        string workspaceRoot,
        string filePath,
        string content,
        InitSummary summary,
        CancellationToken cancellationToken)
    {
        if (File.Exists(filePath))
        {
            summary.Existing.Add(ToRelativePath(workspaceRoot, filePath));
            return;
        }

        string? parentDirectory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(parentDirectory))
        {
            Directory.CreateDirectory(parentDirectory);
        }

        await File.WriteAllTextAsync(
            filePath,
            content,
            Utf8NoBom,
            cancellationToken);
        summary.Created.Add(ToRelativePath(workspaceRoot, filePath));
    }

    private static string ToRelativePath(
        string workspaceRoot,
        string path)
    {
        return WorkspacePath.ToRelativePath(workspaceRoot, path);
    }

    private sealed class InitSummary
    {
        private readonly InitScaffoldOptions _options;

        public InitSummary(
            string workspaceRoot,
            InitScaffoldOptions options)
        {
            WorkspaceRoot = workspaceRoot;
            _options = options;
        }

        public List<string> Created { get; } = [];

        public List<string> Existing { get; } = [];

        public List<string> Skipped { get; } = [];

        public string WorkspaceRoot { get; }

        public string Format()
        {
            StringBuilder builder = new();
            builder.AppendLine($"Initialized StemCode workspace files in {WorkspaceDirectoryName}.");
            builder.AppendLine($"Workspace: {WorkspaceRoot}");
            builder.AppendLine($"Preset: {_options.DisplayName}");

            AppendSection(builder, "Created", Created);
            AppendSection(builder, "Already existed", Existing);
            AppendSection(builder, "Skipped", Skipped);

            builder.AppendLine();
            builder.AppendLine("Next steps:");
            builder.AppendLine("- Edit .stemcode/agent-profile.json for workspace memory, audit, MCP, and custom tool settings.");
            if (_options.IncludeRepoMemory)
            {
                builder.AppendLine("- Review .stemcode/memory/*.md for repo-scoped team memory your team can inspect, diff, and version-control.");
            }

            if (_options.IncludeAgentTemplate)
            {
                builder.AppendLine("- Rename .stemcode/agents/code-reviewer.md.template to .md when you want to enable that custom agent.");
            }

            if (_options.IncludeSkillTemplate)
            {
                builder.AppendLine("- Rename .stemcode/skills/dotnet/SKILL.md.template to SKILL.md when you want to enable that workspace skill.");
            }

            if (_options.IncludeSystemPromptTemplate)
            {
                builder.AppendLine("- Edit and rename .stemcode/SystemPrompt.md.template to SystemPrompt.md only when you need a custom base system prompt.");
            }

            builder.AppendLine("- Add a root AGENTS.md file for persistent workspace instructions.");
            builder.AppendLine("- Use SystemPrompt.md only for advanced base-prompt overrides; it is not needed for ordinary project instructions.");

            return builder.ToString().Trim();
        }

        private static void AppendSection(
            StringBuilder builder,
            string title,
            IReadOnlyList<string> items)
        {
            if (items.Count == 0)
            {
                return;
            }

            builder.AppendLine();
            builder.AppendLine(title + ":");
            foreach (string item in items.Order(StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine("- " + item);
            }
        }
    }

    private enum InitPreset
    {
        Invalid,
        Help,
        Prompt,
        Recommended,
        Minimal,
        Custom
    }

    private sealed record InitScaffoldOptions(
        InitPreset Preset,
        string DisplayName,
        bool IncludeReadme,
        bool IncludeGitIgnore,
        bool IncludeStemCodeIgnore,
        bool IncludeRuntimeDirectories,
        bool IncludeAgentTemplate,
        bool IncludeSkillTemplate,
        bool IncludeRepoMemory,
        bool IncludeLessonsJournal,
        bool IncludeSystemPromptTemplate)
    {
        public static InitScaffoldOptions Invalid { get; } = new(
            InitPreset.Invalid,
            "Invalid",
            IncludeReadme: false,
            IncludeGitIgnore: false,
            IncludeStemCodeIgnore: false,
            IncludeRuntimeDirectories: false,
            IncludeAgentTemplate: false,
            IncludeSkillTemplate: false,
            IncludeRepoMemory: false,
            IncludeLessonsJournal: false,
            IncludeSystemPromptTemplate: false);

        public static InitScaffoldOptions Help { get; } = Invalid with
        {
            Preset = InitPreset.Help,
            DisplayName = "Help"
        };

        public static InitScaffoldOptions Prompt { get; } = Invalid with
        {
            Preset = InitPreset.Prompt,
            DisplayName = "Prompt"
        };

        public static InitScaffoldOptions Custom { get; } = Invalid with
        {
            Preset = InitPreset.Custom,
            DisplayName = "Custom"
        };

        public static InitScaffoldOptions Recommended { get; } = new(
            InitPreset.Recommended,
            "Recommended",
            IncludeReadme: true,
            IncludeGitIgnore: true,
            IncludeStemCodeIgnore: true,
            IncludeRuntimeDirectories: true,
            IncludeAgentTemplate: true,
            IncludeSkillTemplate: true,
            IncludeRepoMemory: true,
            IncludeLessonsJournal: true,
            IncludeSystemPromptTemplate: false);

        public static InitScaffoldOptions Minimal { get; } = new(
            InitPreset.Minimal,
            "Minimal",
            IncludeReadme: true,
            IncludeGitIgnore: true,
            IncludeStemCodeIgnore: true,
            IncludeRuntimeDirectories: false,
            IncludeAgentTemplate: false,
            IncludeSkillTemplate: false,
            IncludeRepoMemory: false,
            IncludeLessonsJournal: false,
            IncludeSystemPromptTemplate: false);

        public static InitScaffoldOptions FromPreset(InitPreset preset)
        {
            return preset switch
            {
                InitPreset.Recommended => Recommended,
                InitPreset.Minimal => Minimal,
                _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unsupported init preset.")
            };
        }
    }

    private const string AgentProfileTemplate =
        """
        {
          "Application": {
            "Tools": {
              "defaultTimeoutSeconds": 180,
              "maxConcurrentBackgroundTerminalsPerSession": 4,
              "completedBackgroundTerminalTtlSeconds": 300
            }
          },
          "memory": {
            "requireApprovalForWrites": true,
            "allowAutoFailureObservation": false,
            "allowAutoManualLessons": false,
            "lessonsEnabled": false,
            "redactSecrets": true,
            "maxEntries": 500,
            "maxPromptChars": 12000,
            "disabled": false
          },
          "toolAudit": {
            "enabled": false,
            "redactSecrets": true,
            "maxArgumentsChars": 12000,
            "maxResultChars": 12000
          },
          "customTools": {},
          "mcpServers": {}
        }
        """;

    private const string ReadmeTemplate =
        """
        # StemCode Workspace Files

        This directory stores workspace-local StemCode configuration.

        - `agent-profile.json`: workspace memory, audit, custom tools, and MCP server settings.
        - `SystemPrompt.md`: optional custom base system prompt. StemCode prepends its identity header automatically when this file has content.
        - `SystemPrompt-Append.md`: optional workspace rules appended to the configured base system prompt without replacing it.
        - `SystemPrompt.md.template`: inactive starter for the advanced SystemPrompt override, when selected during `/init custom`.
        - `.stemcodeignore`: workspace paths excluded from StemCode file tools.
        - `agents/*.md`: custom agents and built-in profile prompt overrides. Files ending in `.template` are inactive until renamed to `.md`.
        - `skills/**/SKILL.md`: workspace skills. Template files are inactive until renamed to `SKILL.md`.
        - `cache/codebase-index.zvec`: local Zvec codebase index cache created by the `codebase_index` tool.
        - `memory/*.md`: repo-scoped team memory that can be inspected, diffed, and version-controlled.
        - `memory/lessons.jsonl`: reusable local lessons about mistakes, failures, and fixes. Off by default; when enabled, relevant lessons can be injected automatically into prompts.
        - `logs/tool-audit.jsonl`: optional tool audit log when enabled in `agent-profile.json`.

        Memory writes require approval by default. Keep team memory focused on durable architecture, convention, decision, known-issue, and test-strategy notes.

        Root-level `AGENTS.md` files are loaded as persistent workspace instructions. Use `.stemcode/SystemPrompt-Append.md` when you want to add workspace-specific base rules without replacing StemCode's default base prompt. Use `.stemcode/SystemPrompt.md` only when you want to replace StemCode's base system prompt for this workspace. Use `.stemcode/agents/build.md`, `plan.md`, `review.md`, `general.md`, or `explore.md` when you only want to replace a built-in profile prompt; StemCode keeps the built-in profile's tools and permissions.
        """;

    private const string SystemPromptTemplate =
        """
        # Workspace System Prompt

        Replace this template, then rename it to `SystemPrompt.md` only when this workspace needs a custom base system prompt.

        Use a root `AGENTS.md` file for ordinary repository instructions.
        """;

    private const string GitIgnoreTemplate =
        """
        logs/*.jsonl
        cache/
        memory/*.jsonl
        *.local.json
        """;

    private const string StemCodeIgnoreTemplate =
        """
        # Local secrets and environment files
        .env
        .env.*
        *.env
        *.local
        *.secret
        *.secrets
        secrets.*
        secret.*
        credentials.*
        credential.*
        appsettings.*.local.json
        appsettings.*.secrets.json
        appsettings.Production.json
        appsettings.Staging.json

        # Credential and signing material
        *.pem
        *.key
        *.p12
        *.pfx
        *.cer
        *.crt
        *.der
        *.keystore
        *.jks
        *.publishsettings
        *.pubxml
        PublishScripts/

        # User and IDE state
        .vs/
        .vscode/*.local.json
        *.suo
        *.user
        *.userosscache
        *.rsuser
        *.userprefs
        *.DotSettings.user
        .localhistory/

        # Build, test, and generated output
        [Bb]in/
        [Oo]bj/
        [Dd]ebug/
        [Rr]elease/
        [Rr]eleases/
        artifacts/
        publish/
        TestResults/
        [Tt]est[Rr]esult*/
        BenchmarkDotNet.Artifacts/
        coverage/
        coverage*.json
        coverage*.xml
        coverage*.info
        *.coverage
        *.coveragexml
        *.binlog
        *.log

        # Package and dependency caches
        node_modules/
        packages/
        **/[Pp]ackages/*
        *.nupkg
        *.snupkg
        .nuget/

        # StemCode local runtime data
        .stemcode/sessions/
        .stemcode/logs/
        .stemcode/cache/
        .stemcode/tmp/
        .stemcode/temp/
        .stemcode/memory/*.jsonl
        .stemcode/secrets/

        # VCS and OS metadata
        .git/
        .DS_Store
        Thumbs.db
        desktop.ini
        """;

    private const string AgentTemplate =
        """
        ---
        name: code-reviewer
        mode: subagent
        description: Read-only reviewer for bugs, regressions, edge cases, and missing tests.
        editMode: readOnly
        shellMode: safeInspectionOnly
        tools:
          - code_intelligence
          - directory_list
          - file_read
          - search_files
          - shell_command
          - text_search
        ---
        Review the requested code or change set with a findings-first posture.
        """;

    private const string SkillTemplate =
        """
        ---
        name: dotnet
        description: Use for .NET build, test, package, and project-file work.
        ---
        Prefer repo-native build and test commands.
        Inspect the relevant `.csproj` before changing package references.
        Keep package and target framework changes narrowly scoped.
        """;

}
