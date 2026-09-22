using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics.CodeAnalysis;

namespace StemCode.Application.Commands;

internal static class ReplCommandCatalog
{
    private static readonly ReplCommandRegistration[] Registrations =
    [
        Create<AgentCommandHandler>("agent", "List available subagents for delegated work.", "/agent"),
        Create<AgentAliasCommandHandler>("a", "Alias for /agent.", "/a"),
        Create<AllowCommandHandler>("allow", "Add a session-scoped allow override for a tool/tag and optional target pattern.", "/allow <tool-or-tag> [pattern]", requiresArgument: true),
        Create<AutoCommitCommandHandler>("autocommit", "Show or toggle automatic git commits for AI-made workspace changes.", "/autocommit [on|off|status]"),
        Create<BudgetCommandHandler>("budget", "Show or configure budget controls from local or cloud settings.", "/budget [status|local [path]|cloud]"),
        Create<CloneCommandHandler>("clone", "Duplicate the current session at the current position.", "/clone"),
        Create<CodebaseIndexCommandHandler>("index", "Update, rebuild, inspect, or list the local codebase index.", "/index [update|status|rebuild|list] [limit]"),
        Create<CompactCommandHandler>("compact", "Manually compact the session context.", "/compact [retained-turns]"),
        Create<ConfigCommandHandler>("config", "Show provider, config-path, active-profile, thinking, and active-model details.", "/config"),
        Create<ContextSizeCommandHandler>("context-size", "Show, set, or clear the session context window cap.", "/context-size [show|auto|8k|32k|64k|125k|256k|<tokens>]"),
        Create<CopyCommandHandler>("copy", "Copy the last agent message to the clipboard.", "/copy"),
        Create<DisableAnalyticsCommandHandler>("disableanalytics", "Disable product analytics for this workspace.", "/disableanalytics"),
        Create<DoctorCommandHandler>("doctor", "Show comprehensive system diagnostics for StemCode.", "/doctor"),
        Create<DenyCommandHandler>("deny", "Add a session-scoped deny override for a tool/tag and optional target pattern.", "/deny <tool-or-tag> [pattern]", requiresArgument: true),
        Create<ExitCommandHandler>("exit", "Exit the interactive shell.", "/exit"),
        Create<ExportCommandHandler>("export", "Export the current session as JSON, HTML, or trajectory HTML.", "/export [json|html|trajectory] [path]"),
        Create<ForkCommandHandler>("fork", "Create a new fork from a previous user message.", "/fork [turn-number]"),
        Create<HelpCommandHandler>("help", "List the available shell commands and their usage.", "/help"),
        Create<ImportCommandHandler>("import", "Import a session from JSON and switch to the imported copy.", "/import <json-path>", requiresArgument: true),
        Create<InitCommandHandler>("init", "Choose and initialize workspace-local StemCode files.", "/init [recommended|minimal|custom]"),
        Create<LessonsCommandHandler>("lessons", "Manage local lesson memory from the shell. It is off by default and can inject relevant lessons into prompts when enabled.", "/lessons [status|on|off|list [limit]|search <query>|save <trigger> | <problem> | <lesson>|edit <id> <trigger> | <problem> | <lesson>|delete <id>]"),
        Create<LspCommandHandler>("lsp", "Show discovered language servers, or inspect which ones apply to a file.", "/lsp [status|refresh|file <path> [refresh]]"),
        Create<McpCommandHandler>("mcp", "Show configured MCP servers, custom tool providers, and discovered dynamic tools.", "/mcp"),
        Create<ModelsCommandHandler>("models", "Open the active model picker.", "/models"),
        Create<NewSessionCommandHandler>("new", "Start a fresh section without carrying over prior context.", "/new"),
        Create<OnboardCommandHandler>("onboard", "Re-run provider onboarding and switch the active session to the new provider.", "/onboard"),
        Create<PermissionsCommandHandler>("permissions", "Show the current permission summary and session override guidance.", "/permissions"),
        Create<PluginCommandHandler>("skill", "Manage data-only skill marketplaces and installs.", "/skill [marketplace add <owner/repo> [--ref <ref>] [--alias <alias>]|marketplace remove <alias>|browse <marketplaceAlias>|install <skillId>@<marketplaceAlias> [--force]|list|uninstall <skillId>]"),
        Create<ProfileCommandHandler>("profile", "Switch the active agent profile for subsequent prompts.", "/profile <name>", requiresArgument: true),
        Create<ProviderCommandHandler>("provider", "List saved providers or switch the active session to another saved provider.", "/provider [list|<name>]"),
        Create<ReasoningCommandHandler>("reasoning", "Show or set provider reasoning effort for subsequent prompts.", "/reasoning [show|<none|minimal|low|medium|high|xhigh|max>]"),
        Create<RedactCommandHandler>("redact", "Show or toggle secret redaction for session output.", "/redact [on|off]"),
        Create<RedoCommandHandler>("redo", "Re-apply the most recently undone file edit transaction.", "/redo"),
        Create<ReloadCommandHandler>("reload", "Reload keybindings, extensions, skills, prompts, and themes.", "/reload"),
        Create<ResumeCommandHandler>("resume", "Resume a different session.", "/resume [session-id]", exposeConcreteType: true),
        Create<RulesCommandHandler>("rules", "List the effective permission rules in evaluation order.", "/rules"),
        Create<SessionInfoCommandHandler>("session", "Show session info and stats.", "/session"),
        Create<SettingCommandHandler>("setting", "Open the StemCode settings picker for configurable session and workspace options.", "/setting [model|profile|thinking|provider|budget|workspace|permissions|tools|summary]"),
        Create<SetupSandboxCommandHandler>("setup-sandbox", "Set up Windows sandbox support for restricted shell commands.", "/setup-sandbox"),
        Create<ShareCommandHandler>("share", "Share the current session as a secret GitHub gist.", "/share"),
        Create<TerminalsCommandHandler>("terminals", "List, view, or stop background terminals for the current session.", "/terminals [view [<terminal-id>]|stop <terminal-id>|stop all]"),
        Create<ThinkingCommandHandler>("thinking", "Show or set thinking mode for subsequent prompts.", "/thinking [on|off]"),
        Create<ToolOutputCommandHandler>("tooloutput", "Show or toggle whether tool results print their complete output or a compact preview.", "/tooloutput [compact|full|auto]"),
        Create<TreeCommandHandler>("tree", "Navigate the session tree and switch branches.", "/tree"),
        Create<UpdateCommandHandler>("update", "Check for StemCode updates and install the latest release.", "/update [now]"),
        Create<UndoCommandHandler>("undo", "Roll back the most recent tracked file edit transaction.", "/undo"),
        Create<UseModelCommandHandler>("use", "Switch the active model for subsequent prompts.", "/use <model>", requiresArgument: true),
        Create<VersionCommandHandler>("version", "Show the current StemCode CLI version.", "/version")
    ];

    public static IReadOnlyList<ReplCommandMetadata> All { get; } = Registrations
        .Select(static registration => registration.Metadata)
        .OrderBy(static metadata => metadata.CommandName, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static ReplCommandMetadata Get<THandler>()
        where THandler : IReplCommandHandler
    {
        Type handlerType = typeof(THandler);
        return Registrations.First(registration => registration.HandlerType == handlerType).Metadata;
    }

    public static IServiceCollection AddRegisteredReplCommandHandlers(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        foreach (ReplCommandRegistration registration in Registrations)
        {
            registration.Register(services);
        }

        return services;
    }

    private static ReplCommandRegistration Create<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(
        string commandName,
        string description,
        string usage,
        bool requiresArgument = false,
        bool exposeConcreteType = false)
        where THandler : class, IReplCommandHandler
    {
        return new ReplCommandRegistration(
            typeof(THandler),
            new ReplCommandMetadata(commandName, description, usage, requiresArgument),
            services => Register<THandler>(services, exposeConcreteType));
    }

    private sealed record ReplCommandRegistration(
        Type HandlerType,
        ReplCommandMetadata Metadata,
        Action<IServiceCollection> Register);

    private static void Register<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(
        IServiceCollection services,
        bool exposeConcreteType)
        where THandler : class, IReplCommandHandler
    {
        if (exposeConcreteType)
        {
            services.AddSingleton<THandler>();
            services.AddSingleton<IReplCommandHandler>(static serviceProvider =>
                serviceProvider.GetRequiredService<THandler>());
            return;
        }

        services.AddSingleton<IReplCommandHandler, THandler>();
    }
}
