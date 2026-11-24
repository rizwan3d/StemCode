using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StemCode.VS.ToolWindows
{
    /// <summary>One slash-command suggestion shown in the composer autocomplete.</summary>
    public sealed record ChatCommandSuggestion(string Command, string Usage, string Description, string InsertText)
    {
        // ItemTemplate binds these directly.
        public string Display => Command;

        /// <summary>True when the command expects arguments, so it should be prefilled rather than sent.</summary>
        public bool TakesArguments => InsertText.EndsWith(" ", StringComparison.Ordinal);
    }

    /// <summary>One entry of the CLI's ACP <c>available_commands_update</c>.</summary>
    public sealed record AvailableCommandEntry(string Name, string Description, string Usage, string? InputHint, bool Custom);

    /// <summary>A terminal-only command the extension implements itself instead of forwarding to the CLI.</summary>
    public enum LocalChatCommand
    {
        None,
        Clear,
        New,
        Models,
        Copy,
        Exit,
        Trajectory,
        Voice,
        VoiceSetup,
        VoiceUpdate
    }

    /// <summary>
    /// Slash-command catalog for the composer. Mirrors the VS Code extension's chatCommands.ts:
    /// the CLI's live list (sent over ACP) wins, the static catalog is only a fallback, and the
    /// locally handled commands are always merged in.
    /// </summary>
    public static class ChatCommands
    {
        private static ChatCommandSuggestion Entry(string usage, string description)
        {
            string command = usage.Split(' ')[0];
            bool takesArguments = usage.Length > command.Length;
            return new ChatCommandSuggestion(command, usage, description, takesArguments ? command + " " : command);
        }

        private static ChatCommandSuggestion Exact(string command, string description)
            => new(command, command, description, command);

        /// <summary>Commands the extension implements itself (terminal-only in the CLI).</summary>
        public static readonly IReadOnlyList<ChatCommandSuggestion> Local = new[]
        {
            Entry("/clear", "Clear the chat view."),
            Entry("/copy", "Copy the last agent message to the clipboard."),
            Entry("/exit", "Stop the StemCode process."),
            Entry("/trajectory", "Export the session trajectory as HTML and open it."),
            Exact("/voice", "Start voice dictation into the composer."),
            Exact("/voice setup", "Configure the voice model and microphone."),
            Exact("/voice update", "Update the local voice models."),
        };

        /// <summary>
        /// Fallback for the CLI's built-in catalog (ReplCommandCatalog), used until the CLI
        /// advertises its live list over ACP. Keep in sync with StemCode.VsCode/src/webview/chatCommands.ts.
        /// </summary>
        public static readonly IReadOnlyList<ChatCommandSuggestion> Cli = new[]
        {
            Entry("/a", "Alias for /agent."),
            Entry("/agent", "List available subagents for delegated work."),
            Entry("/allow <tool-or-tag> [pattern]", "Add a session-scoped allow override for a tool/tag and optional target pattern."),
            Entry("/autocommit [on|off|status]", "Show or toggle automatic git commits for AI-made workspace changes."),
            Entry("/budget [status|local [path]|cloud]", "Show or configure budget controls from local or cloud settings."),
            Entry("/clone", "Duplicate the current session at the current position."),
            Entry("/compact [retained-turns]", "Manually compact the session context."),
            Entry("/config", "Show provider, config-path, active-profile, thinking, and active-model details."),
            Entry("/copy", "Copy the last agent message to the clipboard."),
            Entry("/deny <tool-or-tag> [pattern]", "Add a session-scoped deny override for a tool/tag and optional target pattern."),
            Entry("/disableanalytics", "Disable product analytics for this workspace."),
            Entry("/doctor", "Show comprehensive system diagnostics for StemCode."),
            Entry("/export [json|html|trajectory] [path]", "Export the current session as JSON, HTML, or trajectory HTML."),
            Entry("/fork [turn-number]", "Create a new fork from a previous user message."),
            Entry("/help", "List the available shell commands and their usage."),
            Entry("/import <json-path>", "Import a session from JSON and switch to the imported copy."),
            Entry("/index [update|status|rebuild|list] [limit]", "Update, rebuild, inspect, or list the local codebase index."),
            Entry("/init [recommended|minimal|custom]", "Choose and initialize workspace-local StemCode files."),
            Entry("/lessons [status|on|off|list|search|save|edit|delete]", "Manage local lesson memory."),
            Entry("/lsp [status|refresh|file <path> [refresh]]", "Show discovered language servers, or inspect which ones apply to a file."),
            Entry("/mcp [login <server>|logout <server>]", "Show MCP servers, tools, and prompts, or sign in to and out of remote MCP servers."),
            Entry("/models", "Open the active model picker."),
            Entry("/new", "Start a fresh session without carrying over prior context."),
            Entry("/onboard", "Re-run provider onboarding and switch the active session to the new provider."),
            Entry("/output-style [list|<style>]", "Show or switch the output style that shapes how the agent writes its answers."),
            Entry("/permissions", "Show the current permission summary and session override guidance."),
            Entry("/profile <name>", "Switch the active agent profile for subsequent prompts."),
            Entry("/provider [list|<name>]", "List saved providers or switch the active session to another saved provider."),
            Entry("/reasoning [show|<none|minimal|low|medium|high|xhigh|max>]", "Show or set provider reasoning effort for subsequent prompts."),
            Entry("/redact [on|off]", "Show or toggle secret redaction for session output."),
            Entry("/redo", "Re-apply the most recently undone file edit transaction."),
            Entry("/reload", "Reload keybindings, extensions, skills, prompts, and themes."),
            Entry("/resume [session-id]", "Resume a different session."),
            Entry("/rewind [turn-number] [both|code|conversation]", "Rewind file edits and the conversation together to before an earlier message."),
            Entry("/rules", "List the effective permission rules in evaluation order."),
            Entry("/session", "Show session info and stats."),
            Entry("/setting [model|profile|thinking|provider|budget|workspace|permissions|tools|summary]", "Open the StemCode settings picker."),
            Entry("/setup-sandbox", "Set up Windows sandbox support for restricted shell commands."),
            Entry("/share", "Share the current session as a secret GitHub gist."),
            Entry("/skill [marketplace add|marketplace remove|browse|install|list|uninstall]", "Manage data-only skill marketplaces and installs."),
            Entry("/terminals [view [<terminal-id>]|stop <terminal-id>|stop all]", "List, view, or stop background terminals for the current session."),
            Entry("/thinking [on|off]", "Show or set thinking mode for subsequent prompts."),
            Entry("/tooloutput [compact|full|auto]", "Show or toggle whether tool results print complete output or a compact preview."),
            Entry("/tree", "Navigate the session tree and switch branches."),
            Entry("/undo", "Roll back the most recent tracked file edit transaction."),
            Entry("/update [now]", "Check for StemCode updates and install the latest release."),
            Entry("/use <model>", "Switch the active model for subsequent prompts."),
            Entry("/version", "Show the current StemCode CLI version."),
            Entry("/worktree [list|remove <name>|remove --all]", "List or remove isolated git worktrees created for subagents and parallel sessions."),
        };

        /// <summary>Default list shown before the CLI has advertised its commands.</summary>
        public static IReadOnlyList<ChatCommandSuggestion> All { get; } = Build(Array.Empty<AvailableCommandEntry>());

        /// <summary>
        /// Merges the CLI-advertised commands (including custom <c>.stemcode/commands</c>) with the
        /// locally handled ones. Local commands win on name clashes because the extension intercepts
        /// them before they reach the CLI. With no server list, the static catalog is used.
        /// </summary>
        public static IReadOnlyList<ChatCommandSuggestion> Build(IReadOnlyList<AvailableCommandEntry> serverCommands)
        {
            IEnumerable<ChatCommandSuggestion> remote = serverCommands.Count > 0
                ? serverCommands.Select(FromServer)
                : Cli;

            var merged = new Dictionary<string, ChatCommandSuggestion>(StringComparer.OrdinalIgnoreCase);
            foreach (ChatCommandSuggestion command in remote) merged[command.Command] = command;
            foreach (ChatCommandSuggestion command in Local) merged[command.Command] = command;

            return merged.Values
                .OrderBy(static c => c.Command, StringComparer.Ordinal)
                .ToList();
        }

        private static ChatCommandSuggestion FromServer(AvailableCommandEntry command)
        {
            string slashName = "/" + command.Name;
            string usage = command.Usage.StartsWith(slashName, StringComparison.Ordinal) ? command.Usage : slashName;
            bool takesArguments = !string.IsNullOrEmpty(command.InputHint) || usage.Length > slashName.Length;
            string description = command.Custom
                ? (string.IsNullOrEmpty(command.Description) ? "Custom command." : command.Description + " (custom)")
                : command.Description;
            return new ChatCommandSuggestion(slashName, usage, description, takesArguments ? slashName + " " : slashName);
        }

        /// <summary>Commands from <paramref name="commands"/> whose name starts with the given prefix.</summary>
        public static IEnumerable<ChatCommandSuggestion> Match(IEnumerable<ChatCommandSuggestion> commands, string prefix)
            => commands.Where(c => c.Command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        /// <summary>Parses the <c>availableCommands</c> array of an ACP <c>available_commands_update</c>.</summary>
        public static List<AvailableCommandEntry> ParseAvailableCommands(JsonElement availableCommands)
        {
            var result = new List<AvailableCommandEntry>();
            if (availableCommands.ValueKind != JsonValueKind.Array) return result;

            foreach (JsonElement item in availableCommands.EnumerateArray())
            {
                AvailableCommandEntry? entry = ParseAvailableCommand(item);
                if (entry is not null) result.Add(entry);
            }
            return result;
        }

        /// <summary>Parses one <c>availableCommands</c> entry; null when it has no usable name.</summary>
        public static AvailableCommandEntry? ParseAvailableCommand(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object) return null;

            string name = (GetString(value, "name") ?? string.Empty).Trim().TrimStart('/');
            if (name.Length == 0 || name.Any(char.IsWhiteSpace)) return null;

            string? inputHint = null;
            if (value.TryGetProperty("input", out JsonElement input) && input.ValueKind == JsonValueKind.Object)
            {
                string? hint = GetString(input, "hint")?.Trim();
                if (!string.IsNullOrEmpty(hint)) inputHint = hint;
            }

            string? rawUsage = GetString(value, "usage")?.Trim();
            string usage = !string.IsNullOrEmpty(rawUsage)
                ? rawUsage!
                : "/" + name + (inputHint is null ? string.Empty : " " + inputHint);

            bool custom = value.TryGetProperty("custom", out JsonElement c) && c.ValueKind == JsonValueKind.True;
            return new AvailableCommandEntry(name, GetString(value, "description")?.Trim() ?? string.Empty, usage, inputHint, custom);
        }

        /// <summary>Maps a typed command to the local handler that replaces the CLI's terminal-only behaviour.</summary>
        public static LocalChatCommand ParseLocal(string text)
        {
            string normalized = Regex.Replace(text.Trim(), @"\s+", " ").ToLowerInvariant();
            return normalized switch
            {
                "/clear" => LocalChatCommand.Clear,
                "/new" => LocalChatCommand.New,
                "/models" => LocalChatCommand.Models,
                "/copy" => LocalChatCommand.Copy,
                "/exit" => LocalChatCommand.Exit,
                "/trajectory" or "/traj" => LocalChatCommand.Trajectory,
                "/voice" => LocalChatCommand.Voice,
                "/voice setup" => LocalChatCommand.VoiceSetup,
                "/voice update" => LocalChatCommand.VoiceUpdate,
                _ => LocalChatCommand.None
            };
        }

        /// <summary>True for <c>/export</c> with or without arguments.</summary>
        public static bool IsExportCommand(string text)
            => Regex.IsMatch(text.Trim(), @"^/export(\s|$)", RegexOptions.IgnoreCase);

        /// <summary>
        /// Reads the file the CLI reports after <c>/export</c> ("Exported session as HTML:\n&lt;path&gt;").
        /// Returns null when the output has no such line (for example, the export failed).
        /// </summary>
        public static (string Format, string FilePath)? ParseExportedFile(string? output)
        {
            Match match = Regex.Match(output ?? string.Empty, @"Exported session as ([A-Za-z]+):\s*\r?\n\s*(.+?)\s*$", RegexOptions.Multiline);
            if (!match.Success) return null;
            return (match.Groups[1].Value.ToUpperInvariant(), match.Groups[2].Value.Trim());
        }

        private static string? GetString(JsonElement element, string propertyName)
            => element.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }
}
