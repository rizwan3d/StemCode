using System.Collections.Generic;

namespace StemCode.VS.ToolWindows
{
    /// <summary>One slash-command suggestion shown in the composer autocomplete.</summary>
    public sealed record ChatCommandSuggestion(string Command, string Usage, string Description, string InsertText)
    {
        // ItemTemplate binds these directly.
        public string Display => Command;
    }

    /// <summary>
    /// Catalog of built-in StemCode slash commands. Mirrors the VS Code extension's
    /// chatCommands.ts so the composer offers the same autocomplete.
    /// </summary>
    public static class ChatCommands
    {
        public static readonly IReadOnlyList<ChatCommandSuggestion> All = new[]
        {
            new ChatCommandSuggestion("/a", "/a", "Alias for /agent.", "/a"),
            new ChatCommandSuggestion("/allow", "/allow <tool-or-tag> [pattern]", "Add a session-scoped allow override.", "/allow "),
            new ChatCommandSuggestion("/agent", "/agent", "List available subagents.", "/agent"),
            new ChatCommandSuggestion("/budget", "/budget [status|local [path]|cloud]", "Show or configure budget controls.", "/budget "),
            new ChatCommandSuggestion("/clear", "/clear", "Clear the chat view.", "/clear"),
            new ChatCommandSuggestion("/clone", "/clone", "Duplicate the current session.", "/clone"),
            new ChatCommandSuggestion("/compact", "/compact [retained-turns]", "Manually compact the session context.", "/compact "),
            new ChatCommandSuggestion("/config", "/config", "Show provider, profile, thinking, and model details.", "/config"),
            new ChatCommandSuggestion("/context-size", "/context-size [show|auto|8k|32k|64k|125k|256k|<tokens>]", "Show, set, or clear the session context window cap.", "/context-size "),
            new ChatCommandSuggestion("/copy", "/copy", "Copy the last agent message to the clipboard.", "/copy"),
            new ChatCommandSuggestion("/deny", "/deny <tool-or-tag> [pattern]", "Add a session-scoped deny override.", "/deny "),
            new ChatCommandSuggestion("/exit", "/exit", "Exit the interactive shell.", "/exit"),
            new ChatCommandSuggestion("/export", "/export [json|html] [path]", "Export the current session.", "/export "),
            new ChatCommandSuggestion("/fork", "/fork [turn-number]", "Create a fork from a previous user message.", "/fork "),
            new ChatCommandSuggestion("/help", "/help", "List available commands.", "/help"),
            new ChatCommandSuggestion("/import", "/import <json-path>", "Import a session from JSON.", "/import "),
            new ChatCommandSuggestion("/init", "/init [recommended|minimal|custom]", "Create workspace-local StemCode files.", "/init "),
            new ChatCommandSuggestion("/ls", "/ls", "List files in the current workspace.", "/ls"),
            new ChatCommandSuggestion("/mcp", "/mcp", "Show MCP servers and dynamic tools.", "/mcp"),
            new ChatCommandSuggestion("/models", "/models", "Open the active model picker.", "/models"),
            new ChatCommandSuggestion("/new", "/new", "Start a new session.", "/new"),
            new ChatCommandSuggestion("/onboard", "/onboard", "Re-run provider onboarding.", "/onboard"),
            new ChatCommandSuggestion("/permissions", "/permissions", "Show permission policy and overrides.", "/permissions"),
            new ChatCommandSuggestion("/skill", "/skill [marketplace add|install|list|uninstall]", "Manage skill marketplaces and installs.", "/skill "),
            new ChatCommandSuggestion("/provider", "/provider [list|<name>]", "List or switch saved providers.", "/provider "),
            new ChatCommandSuggestion("/profile", "/profile <name>", "Switch the active agent profile.", "/profile "),
            new ChatCommandSuggestion("/read", "/read <file>", "Read a workspace file after confirmation.", "/read "),
            new ChatCommandSuggestion("/redo", "/redo", "Re-apply the most recently undone edit.", "/redo"),
            new ChatCommandSuggestion("/reload", "/reload", "Reload profiles, skills, prompts, and tools.", "/reload"),
            new ChatCommandSuggestion("/resume", "/resume [session-id]", "Resume a different session.", "/resume "),
            new ChatCommandSuggestion("/rules", "/rules", "List effective permission rules.", "/rules"),
            new ChatCommandSuggestion("/session", "/session", "Show session info and stats.", "/session"),
            new ChatCommandSuggestion("/setting", "/setting [area]", "Open configurable StemCode settings.", "/setting "),
            new ChatCommandSuggestion("/share", "/share", "Share the current session as a secret GitHub gist.", "/share"),
            new ChatCommandSuggestion("/terminals", "/terminals [stop <id>|stop all]", "List or stop background terminals.", "/terminals"),
            new ChatCommandSuggestion("/thinking", "/thinking [on|off]", "Show or set thinking mode.", "/thinking "),
            new ChatCommandSuggestion("/tree", "/tree", "Navigate saved sessions and forks.", "/tree"),
            new ChatCommandSuggestion("/undo", "/undo", "Roll back the most recent tracked edit.", "/undo"),
            new ChatCommandSuggestion("/update", "/update [now]", "Check for StemCode updates.", "/update "),
            new ChatCommandSuggestion("/use", "/use <model>", "Switch the active model directly.", "/use "),
        };

        /// <summary>Commands whose first token starts with the given (already lowercased) prefix.</summary>
        public static IEnumerable<ChatCommandSuggestion> Match(string prefix)
        {
            foreach (ChatCommandSuggestion c in All)
            {
                if (c.Command.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase))
                {
                    yield return c;
                }
            }
        }
    }
}
