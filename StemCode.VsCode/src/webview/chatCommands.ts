export type ChatCommandSuggestion = {
    command: string;
    usage: string;
    description: string;
    insertText: string;
};

export const CHAT_COMMANDS: ChatCommandSuggestion[] = [
    { command: '/a', usage: '/a', description: 'Alias for /agent.', insertText: '/a' },
    { command: '/allow', usage: '/allow <tool-or-tag> [pattern]', description: 'Add a session-scoped allow override.', insertText: '/allow ' },
    { command: '/agent', usage: '/agent', description: 'List available subagents.', insertText: '/agent' },
    { command: '/budget', usage: '/budget [status|local [path]|cloud]', description: 'Show or configure budget controls.', insertText: '/budget ' },
    { command: '/clear', usage: '/clear', description: 'Clear the chat view.', insertText: '/clear' },
    { command: '/clone', usage: '/clone', description: 'Duplicate the current session.', insertText: '/clone' },
    { command: '/compact', usage: '/compact [retained-turns]', description: 'Manually compact the session context.', insertText: '/compact ' },
    { command: '/config', usage: '/config', description: 'Show provider, profile, thinking, and model details.', insertText: '/config' },
    { command: '/context-size', usage: '/context-size [show|auto|8k|32k|64k|125k|256k|<tokens>]', description: 'Show, set, or clear the session context window cap.', insertText: '/context-size ' },
    { command: '/copy', usage: '/copy', description: 'Copy the last agent message to the clipboard.', insertText: '/copy' },
    { command: '/deny', usage: '/deny <tool-or-tag> [pattern]', description: 'Add a session-scoped deny override.', insertText: '/deny ' },
    { command: '/exit', usage: '/exit', description: 'Exit the interactive shell.', insertText: '/exit' },
    { command: '/export', usage: '/export [json|html] [path]', description: 'Export the current session.', insertText: '/export ' },
    { command: '/fork', usage: '/fork [turn-number]', description: 'Create a fork from a previous user message.', insertText: '/fork ' },
    { command: '/help', usage: '/help', description: 'List available commands.', insertText: '/help' },
    { command: '/import', usage: '/import <json-path>', description: 'Import a session from JSON.', insertText: '/import ' },
    { command: '/init', usage: '/init [recommended|minimal|custom]', description: 'Create workspace-local StemCode files.', insertText: '/init ' },
    { command: '/ls', usage: '/ls', description: 'List files in the current workspace.', insertText: '/ls' },
    { command: '/mcp', usage: '/mcp', description: 'Show MCP servers and dynamic tools.', insertText: '/mcp' },
    { command: '/models', usage: '/models', description: 'Open the active model picker.', insertText: '/models' },
    { command: '/new', usage: '/new', description: 'Start a new session.', insertText: '/new' },
    { command: '/onboard', usage: '/onboard', description: 'Re-run provider onboarding.', insertText: '/onboard' },
    { command: '/permissions', usage: '/permissions', description: 'Show permission policy and overrides.', insertText: '/permissions' },
    { command: '/skill', usage: '/skill [marketplace add|install|list|uninstall]', description: 'Manage skill marketplaces and installs.', insertText: '/skill ' },
    { command: '/provider', usage: '/provider [list|<name>]', description: 'List or switch saved providers.', insertText: '/provider ' },
    { command: '/profile', usage: '/profile <name>', description: 'Switch the active agent profile.', insertText: '/profile ' },
    { command: '/read', usage: '/read <file>', description: 'Read a workspace file after confirmation.', insertText: '/read ' },
    { command: '/redo', usage: '/redo', description: 'Re-apply the most recently undone edit.', insertText: '/redo' },
    { command: '/reload', usage: '/reload', description: 'Reload profiles, skills, prompts, and tools.', insertText: '/reload' },
    { command: '/resume', usage: '/resume [session-id]', description: 'Resume a different session.', insertText: '/resume ' },
    { command: '/rules', usage: '/rules', description: 'List effective permission rules.', insertText: '/rules' },
    { command: '/session', usage: '/session', description: 'Show session info and stats.', insertText: '/session' },
    { command: '/setting', usage: '/setting [area]', description: 'Open configurable StemCode settings.', insertText: '/setting ' },
    { command: '/share', usage: '/share', description: 'Share the current session as a secret GitHub gist.', insertText: '/share' },
    { command: '/terminals', usage: '/terminals [stop <id>|stop all]', description: 'List or stop background terminals.', insertText: '/terminals' },
    { command: '/thinking', usage: '/thinking [on|off]', description: 'Show or set thinking mode.', insertText: '/thinking ' },
    { command: '/tree', usage: '/tree', description: 'Navigate saved sessions and forks.', insertText: '/tree' },
    { command: '/undo', usage: '/undo', description: 'Roll back the most recent tracked edit.', insertText: '/undo' },
    { command: '/update', usage: '/update [now]', description: 'Check for StemCode updates.', insertText: '/update ' },
    { command: '/use', usage: '/use <model>', description: 'Switch the active model directly.', insertText: '/use ' }
];
