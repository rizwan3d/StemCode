export type ChatCommandSuggestion = {
    command: string;
    usage: string;
    description: string;
    insertText: string;
};

/** Shape of one entry in the CLI's ACP `available_commands_update`. */
export type AvailableCommandEntry = {
    name: string;
    description: string;
    usage: string;
    inputHint?: string;
    custom: boolean;
};

function entry(usage: string, description: string): ChatCommandSuggestion {
    const command = usage.split(/\s+/, 1)[0];
    const takesArguments = usage.length > command.length;
    return { command, usage, description, insertText: takesArguments ? command + ' ' : command };
}

/**
 * Commands the extension implements itself instead of forwarding to the CLI.
 * They mirror the terminal-only commands of the interactive CLI.
 */
export const LOCAL_CHAT_COMMANDS: ChatCommandSuggestion[] = [
    entry('/clear', 'Clear the chat view.'),
    entry('/copy', 'Copy the last agent message to the clipboard.'),
    entry('/exit', 'Stop the StemCode process.'),
    entry('/ls', 'List files in the current workspace.'),
    entry('/read <file>', 'Read a workspace file after confirmation.'),
    entry('/trajectory', 'Export the session trajectory as HTML and open it.'),
    { command: '/voice', usage: '/voice', description: 'Start voice dictation.', insertText: '/voice' },
    { command: '/voice setup', usage: '/voice setup', description: 'Configure the voice model and microphone.', insertText: '/voice setup' },
    { command: '/voice update', usage: '/voice update', description: 'Update the local voice models.', insertText: '/voice update' }
];

/**
 * Fallback for the CLI's built-in command catalog (ReplCommandCatalog), used until
 * the CLI advertises its live list via ACP. Keep in sync with
 * StemCode/Application/Commands/ReplCommandCatalog.cs.
 */
export const CLI_COMMANDS: ChatCommandSuggestion[] = [
    entry('/a', 'Alias for /agent.'),
    entry('/agent', 'List available subagents for delegated work.'),
    entry('/allow <tool-or-tag> [pattern]', 'Add a session-scoped allow override for a tool/tag and optional target pattern.'),
    entry('/autocommit [on|off|status]', 'Show or toggle automatic git commits for AI-made workspace changes.'),
    entry('/budget [status|local [path]|cloud]', 'Show or configure budget controls from local or cloud settings.'),
    entry('/clone', 'Duplicate the current session at the current position.'),
    entry('/compact [retained-turns]', 'Manually compact the session context.'),
    entry('/config', 'Show provider, config-path, active-profile, thinking, and active-model details.'),
    entry('/copy', 'Copy the last agent message to the clipboard.'),
    entry('/deny <tool-or-tag> [pattern]', 'Add a session-scoped deny override for a tool/tag and optional target pattern.'),
    entry('/disableanalytics', 'Disable product analytics for this workspace.'),
    entry('/doctor', 'Show comprehensive system diagnostics for StemCode.'),
    entry('/export [json|html|trajectory] [path]', 'Export the current session as JSON, HTML, or trajectory HTML.'),
    entry('/fork [turn-number]', 'Create a new fork from a previous user message.'),
    entry('/help', 'List the available shell commands and their usage.'),
    entry('/import <json-path>', 'Import a session from JSON and switch to the imported copy.'),
    entry('/index [update|status|rebuild|list] [limit]', 'Update, rebuild, inspect, or list the local codebase index.'),
    entry('/init [recommended|minimal|custom]', 'Choose and initialize workspace-local StemCode files.'),
    entry('/lessons [status|on|off|list|search|save|edit|delete]', 'Manage local lesson memory.'),
    entry('/lsp [status|refresh|file <path> [refresh]]', 'Show discovered language servers, or inspect which ones apply to a file.'),
    entry('/mcp', 'Show configured MCP servers, custom tool providers, and discovered dynamic tools.'),
    entry('/models', 'Open the active model picker.'),
    entry('/new', 'Start a fresh session without carrying over prior context.'),
    entry('/onboard', 'Re-run provider onboarding and switch the active session to the new provider.'),
    entry('/permissions', 'Show the current permission summary and session override guidance.'),
    entry('/profile <name>', 'Switch the active agent profile for subsequent prompts.'),
    entry('/provider [list|<name>]', 'List saved providers or switch the active session to another saved provider.'),
    entry('/reasoning [show|<none|minimal|low|medium|high|xhigh|max>]', 'Show or set provider reasoning effort for subsequent prompts.'),
    entry('/redact [on|off]', 'Show or toggle secret redaction for session output.'),
    entry('/redo', 'Re-apply the most recently undone file edit transaction.'),
    entry('/reload', 'Reload keybindings, extensions, skills, prompts, and themes.'),
    entry('/resume [session-id]', 'Resume a different session.'),
    entry('/rules', 'List the effective permission rules in evaluation order.'),
    entry('/session', 'Show session info and stats.'),
    entry('/setting [model|profile|thinking|provider|budget|workspace|permissions|tools|summary]', 'Open the StemCode settings picker.'),
    entry('/setup-sandbox', 'Set up Windows sandbox support for restricted shell commands.'),
    entry('/share', 'Share the current session as a secret GitHub gist.'),
    entry('/skill [marketplace add|marketplace remove|browse|install|list|uninstall]', 'Manage data-only skill marketplaces and installs.'),
    entry('/terminals [view [<terminal-id>]|stop <terminal-id>|stop all]', 'List, view, or stop background terminals for the current session.'),
    entry('/thinking [on|off]', 'Show or set thinking mode for subsequent prompts.'),
    entry('/tooloutput [compact|full|auto]', 'Show or toggle whether tool results print complete output or a compact preview.'),
    entry('/tree', 'Navigate the session tree and switch branches.'),
    entry('/undo', 'Roll back the most recent tracked file edit transaction.'),
    entry('/update [now]', 'Check for StemCode updates and install the latest release.'),
    entry('/use <model>', 'Switch the active model for subsequent prompts.'),
    entry('/version', 'Show the current StemCode CLI version.')
];

/** Default list shown before the CLI has advertised its commands. */
export const CHAT_COMMANDS: ChatCommandSuggestion[] = buildChatCommands([]);

/**
 * Merges the CLI-advertised commands (including custom `.stemcode/commands`) with the
 * locally handled ones. Local commands win on name clashes because the extension
 * intercepts them before they reach the CLI. With no server list, the static catalog
 * is used.
 */
export function buildChatCommands(serverCommands: readonly AvailableCommandEntry[]): ChatCommandSuggestion[] {
    const fromServer = serverCommands.map((command): ChatCommandSuggestion => {
        const slashName = '/' + command.name;
        const usage = command.usage && command.usage.startsWith(slashName) ? command.usage : slashName;
        const takesArguments = Boolean(command.inputHint) || usage.length > slashName.length;
        const description = command.custom && command.description
            ? command.description + ' (custom)'
            : command.description || (command.custom ? 'Custom command.' : '');
        return {
            command: slashName,
            usage,
            description,
            insertText: takesArguments ? slashName + ' ' : slashName
        };
    });

    const remote = fromServer.length > 0 ? fromServer : CLI_COMMANDS;
    const merged = new Map<string, ChatCommandSuggestion>();
    for (const command of remote) {
        merged.set(command.command.toLowerCase(), command);
    }

    for (const command of LOCAL_CHAT_COMMANDS) {
        merged.set(command.command.toLowerCase(), command);
    }

    return Array.from(merged.values())
        .sort((left, right) => left.command.localeCompare(right.command));
}

/** Parses one `availableCommands` entry from an ACP `available_commands_update`. */
export function readAvailableCommand(value: unknown): AvailableCommandEntry | null {
    if (!value || typeof value !== 'object') {
        return null;
    }

    const record = value as Record<string, unknown>;
    const rawName = typeof record.name === 'string' ? record.name.trim().replace(/^\/+/, '') : '';
    if (!rawName || /\s/.test(rawName)) {
        return null;
    }

    const input = record.input && typeof record.input === 'object'
        ? record.input as Record<string, unknown>
        : undefined;
    const inputHint = typeof input?.hint === 'string' && input.hint.trim() ? input.hint.trim() : undefined;
    const usage = typeof record.usage === 'string' && record.usage.trim()
        ? record.usage.trim()
        : '/' + rawName + (inputHint ? ' ' + inputHint : '');

    return {
        name: rawName,
        description: typeof record.description === 'string' ? record.description.trim() : '',
        usage,
        inputHint,
        custom: record.custom === true
    };
}

/** Reads the file path from the CLI's "Exported session as FORMAT:\n<path>" message. */
export function parseExportedFile(output: string): { format: string; filePath: string } | null {
    const match = /Exported session as ([A-Za-z]+):\s*\r?\n\s*(.+?)\s*$/m.exec(output || '');
    if (!match) {
        return null;
    }

    return { format: match[1].toUpperCase(), filePath: match[2].trim() };
}
