namespace StemCode.Application.Telemetry;

internal static class TelemetryFeatureNames
{
    private static readonly HashSet<string> KnownCommandNames =
    [
        "allow",
        "budget",
        "clear",
        "clone",
        "compact",
        "config",
        "copy",
        "deny",
        "exit",
        "export",
        "fork",
        "help",
        "import",
        "init",
        "ls",
        "mcp",
        "models",
        "new",
        "onboard",
        "permissions",
        "profile",
        "provider",
        "read",
        "reload",
        "redo",
        "resume",
        "rules",
        "session",
        "setting",
        "share",
        "terminals",
        "thinking",
        "tree",
        "undo",
        "update",
        "use",
        "worktree"
    ];

    public const string AgentMention = "agent_mention";
    public const string CustomCommand = "custom_command";
    public const string DirectShell = "direct_shell";
    public const string DirectShellBackground = "direct_shell_background";
    public const string Prompt = "prompt";
    public const string PromptWithAttachments = "prompt_with_attachments";

    public static string ForCommand(string? commandName)
    {
        if (string.IsNullOrWhiteSpace(commandName))
        {
            return "slash:other";
        }

        string normalized = commandName.Trim().ToLowerInvariant();
        return KnownCommandNames.Contains(normalized)
            ? $"slash:{normalized}"
            : "slash:other";
    }
}
