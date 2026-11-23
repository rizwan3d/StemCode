using StemCode.Application.Abstractions;
using StemCode.Application.Commands;

namespace StemCode.Desktop.Services;

public enum DesktopCommandSource
{
    BuiltIn,
    Desktop,
    Custom,
    Mcp
}

public sealed record DesktopCommandInfo(
    string Command,
    string Usage,
    string Description,
    bool RequiresArgument,
    DesktopCommandSource Source)
{
    public string SourceLabel => Source switch
    {
        DesktopCommandSource.Custom => "custom",
        DesktopCommandSource.Mcp => "mcp",
        DesktopCommandSource.Desktop => "app",
        _ => string.Empty
    };
}

/// <summary>
/// Everything the composer can complete after "/": core REPL commands, desktop-only commands,
/// workspace custom commands from .stemcode/commands, and MCP prompts.
/// </summary>
public static class DesktopCommandCatalog
{
    /// <summary>Commands handled by the desktop app itself rather than the backend.</summary>
    public static readonly DesktopCommandInfo[] DesktopCommands =
    [
        new("/clear", "/clear", "Clear the conversation view (the session is kept).", false, DesktopCommandSource.Desktop),
        new("/voice", "/voice", "Start voice dictation (Ctrl+R).", false, DesktopCommandSource.Desktop),
        new("/voice setup", "/voice setup", "Choose the voice model and microphone.", false, DesktopCommandSource.Desktop),
        new("/voice update", "/voice update", "Update the local voice models.", false, DesktopCommandSource.Desktop),
        new("/settings", "/settings", "Open StemCode settings (Ctrl+,).", false, DesktopCommandSource.Desktop)
    ];

    // Terminal-only commands that make no sense in the desktop app.
    private static readonly HashSet<string> HiddenBuiltIns = new(StringComparer.OrdinalIgnoreCase) { "exit", "a" };

    public static IReadOnlyList<DesktopCommandInfo> GetAll(
        string? workspaceRoot,
        IReadOnlyList<McpPromptDescriptor> mcpPrompts)
    {
        List<DesktopCommandInfo> commands = ReplCommandCatalog.All
            .Where(static metadata => !HiddenBuiltIns.Contains(metadata.CommandName))
            .Select(static metadata => new DesktopCommandInfo(
                "/" + metadata.CommandName,
                metadata.Usage,
                metadata.Description,
                metadata.RequiresArgument,
                DesktopCommandSource.BuiltIn))
            .ToList();

        commands.AddRange(DesktopCommands);

        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            try
            {
                commands.AddRange(CustomSlashCommandService
                    .List(workspaceRoot)
                    .Select(static command => new DesktopCommandInfo(
                        command.Command,
                        command.Usage,
                        command.Description,
                        command.RequiresArgument,
                        DesktopCommandSource.Custom)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A broken .stemcode/commands folder should not break completion.
            }
        }

        commands.AddRange(mcpPrompts.Select(static prompt => new DesktopCommandInfo(
            "/" + prompt.CommandName.TrimStart('/'),
            prompt.Usage,
            string.IsNullOrWhiteSpace(prompt.Description) ? $"MCP prompt from {prompt.ServerName}" : prompt.Description!,
            prompt.Arguments.Count > 0,
            DesktopCommandSource.Mcp)));

        return commands
            .GroupBy(static command => command.Command, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .OrderBy(static command => command.Command, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool IsDesktopCommand(string text, out string command)
    {
        command = text.Trim();
        foreach (DesktopCommandInfo desktopCommand in DesktopCommands)
        {
            if (string.Equals(command, desktopCommand.Command, StringComparison.OrdinalIgnoreCase))
            {
                command = desktopCommand.Command;
                return true;
            }
        }

        return command.StartsWith("/voice ", StringComparison.OrdinalIgnoreCase);
    }
}
