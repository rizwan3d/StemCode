import * as vscode from 'vscode';

type SettingsRow = {
    icon: string;
    title: string;
    description: string;
    value?: string;
    valueId?: string;
    action?: string;
    command?: string;
};

type SettingsGroup = {
    title: string;
    hint: string;
    rows: SettingsRow[];
};

const SETTINGS_GROUPS: SettingsGroup[] = [
    {
        title: 'Session',
        hint: 'Model, profile, reasoning, and saved chats.',
        rows: [
            { icon: 'sparkle', title: 'Model', description: 'Choose the active model for this session.', valueId: 'settings-model-value', action: 'model' },
            { icon: 'agent', title: 'Profile', description: 'Switch build, plan, review, or subagent profile.', valueId: 'settings-profile-value', command: '/setting profile' },
            { icon: 'brain', title: 'Thinking', description: 'Set provider reasoning effort for later prompts.', valueId: 'settings-thinking-value', command: '/setting thinking' },
            { icon: 'info', title: 'Summary', description: 'Provider, model, profile, thinking, and session details.', value: 'Open', command: '/setting summary' },
            { icon: 'history', title: 'Sessions', description: 'Browse, resume, fork, or export saved sessions.', value: 'Browse', action: 'sessions' }
        ]
    },
    {
        title: 'Provider',
        hint: 'Switch providers or repair credentials.',
        rows: [
            { icon: 'globe', title: 'Provider', description: 'Switch the saved provider for this session.', valueId: 'settings-provider-value', command: '/setting provider' },
            { icon: 'shield', title: 'Onboarding', description: 'Add or repair provider credentials.', value: 'Set up', command: '/setting onboarding' }
        ]
    },
    {
        title: 'Workspace',
        hint: 'Project files, permissions, budget, tools, terminals, and skills.',
        rows: [
            { icon: 'folder', title: 'Workspace Files', description: 'Create or review .stemcode project files.', value: 'Init', command: '/setting workspace' },
            { icon: 'output', title: 'Budget', description: 'Configure local or cloud budget controls.', value: 'Open', command: '/setting budget' },
            { icon: 'shield', title: 'Permissions', description: 'Edit modes, sandbox, and session overrides.', value: 'Policy', command: '/setting permissions' },
            { icon: 'list', title: 'Rules', description: 'Inspect effective tool permission rules.', value: 'View', command: '/setting rules' },
            { icon: 'tool', title: 'Tools', description: 'MCP servers, custom tools, and dynamic tool status.', value: 'View', command: '/setting tools' },
            { icon: 'terminal', title: 'Background Terminals', description: 'List running terminals and stop them.', value: 'View', command: '/terminals' },
            { icon: 'add', title: 'Skills', description: 'Browse marketplaces and install or remove skills.', value: 'Manage', action: 'plugins' }
        ]
    },
    {
        title: 'Extension',
        hint: 'Native VS Code settings and diagnostics.',
        rows: [
            { icon: 'gear', title: 'VS Code Extension Settings', description: 'Command, args, working directory, auto-start, and log level.', value: 'Open', action: 'vscodeSettings' },
            { icon: 'output', title: 'Extension Logs', description: 'Open the StemCode output channel.', value: 'Open', action: 'openLogs' },
            { icon: 'refresh', title: 'Restart StemCode', description: 'Restart the StemCode ACP process.', value: 'Restart', action: 'restartProcess' }
        ]
    }
];

function escapeHtml(value: string): string {
    return value
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#39;');
}

function renderSettingsGroups(): string {
    return SETTINGS_GROUPS.map(group => `
                    <section class="settings-group">
                        <header class="settings-group-header">
                            <h2>${escapeHtml(group.title)}</h2>
                            <p>${escapeHtml(group.hint)}</p>
                        </header>
                        <div class="settings-list">${group.rows.map(row => {
        const dataAttrs = [
            row.action ? `data-action="${escapeHtml(row.action)}"` : '',
            row.command ? `data-command="${escapeHtml(row.command)}"` : ''
        ].filter(Boolean).join(' ');
        const value = row.valueId
            ? `<span id="${escapeHtml(row.valueId)}" class="settings-action-value">-</span>`
            : `<span class="settings-action-value">${escapeHtml(row.value ?? '')}</span>`;
        return `
                            <button type="button" class="settings-action" ${dataAttrs}>
                                <span class="settings-action-icon" data-ico="${escapeHtml(row.icon)}"></span>
                                <span class="settings-action-main">
                                    <span class="settings-action-title">${escapeHtml(row.title)}</span>
                                    <span class="settings-action-description">${escapeHtml(row.description)}</span>
                                </span>
                                ${value}
                                <span class="settings-action-chevron" data-ico="chevronRight"></span>
                            </button>`;
    }).join('')}
                        </div>
                    </section>`).join('');
}

export function getChatWebviewContent(webview: vscode.Webview, extensionUri: vscode.Uri, nonce: string) {
    const styleUri = webview.asWebviewUri(vscode.Uri.joinPath(extensionUri, 'media', 'chat.css'));
    const scriptUri = webview.asWebviewUri(vscode.Uri.joinPath(extensionUri, 'dist', 'webview.js'));
    const logoUri = webview.asWebviewUri(vscode.Uri.joinPath(extensionUri, 'resources', 'logo.png'));

    return `<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <meta http-equiv="Content-Security-Policy"
          content="default-src 'none'; style-src ${webview.cspSource}; script-src 'nonce-${nonce}'; img-src ${webview.cspSource} data:; font-src ${webview.cspSource}; connect-src 'none'; form-action 'none'; frame-ancestors 'none'; base-uri 'none';">
    <title>StemCode</title>
    <link href="${styleUri}" rel="stylesheet">
</head>
<body>
    <div class="app" id="app">
        <header class="topbar">
            <div class="topbar-title">
                <span id="section-title" class="session-title" title="Current session">New chat</span>
            </div>
            <div class="topbar-actions">
                <button type="button" id="new-chat-button" class="icon-button" title="New chat" aria-label="New chat" data-ico="add"></button>
                <button type="button" id="sessions-button" class="icon-button" title="Session history" aria-label="Session history" data-ico="history"></button>
                <button type="button" id="settings-button" class="icon-button" title="Settings" aria-label="Settings" data-ico="gear"></button>
            </div>
        </header>

        <div id="status-banner" class="status-banner hidden" role="status" aria-live="polite">
            <span id="status-banner-icon" class="status-banner-icon"></span>
            <span id="status-banner-text" class="status-banner-text"></span>
            <span class="status-banner-actions">
                <button type="button" id="status-retry-button" class="link-button">Retry</button>
                <button type="button" id="status-logs-button" class="link-button">Logs</button>
            </span>
        </div>

        <main id="messages" class="transcript" role="log" aria-live="polite" aria-relevant="additions" tabindex="0">
            <div id="empty-state" class="empty-state">
                <img class="empty-logo" src="${logoUri}" alt="" width="44" height="44">
                <h1 class="empty-title">What should we build?</h1>
                <p class="empty-hint">StemCode reads your workspace, edits files, runs tools, and asks before doing anything risky.</p>
                <div class="starter-list" id="starter-list">
                    <button type="button" class="starter" data-prompt="Give me a high-level overview of this codebase: structure, key modules, and how they fit together.">
                        <span class="starter-icon" data-ico="search"></span><span>Explain this codebase</span>
                    </button>
                    <button type="button" class="starter" data-prompt="Find bugs and risky code in the active file and suggest fixes." data-needs-editor="true">
                        <span class="starter-icon" data-ico="warning"></span><span>Find bugs in the current file</span>
                    </button>
                    <button type="button" class="starter" data-prompt="Write unit tests for the active file, following the project's existing test conventions." data-needs-editor="true">
                        <span class="starter-icon" data-ico="check"></span><span>Write tests for the current file</span>
                    </button>
                    <button type="button" class="starter" data-command="stemcode.reviewGitDiff">
                        <span class="starter-icon" data-ico="edit"></span><span>Review my uncommitted changes</span>
                    </button>
                </div>
                <div class="empty-keys"><kbd>/</kbd> commands <span class="dot">·</span> <kbd>@</kbd> files <span class="dot">·</span> <kbd>Shift</kbd>+<kbd>Enter</kbd> new line</div>
            </div>
        </main>

        <section id="settings-page" class="settings-page hidden" aria-label="StemCode settings">
            <div class="settings-shell">
                <header class="settings-header">
                    <button type="button" id="settings-close-button" class="icon-button" title="Back to chat" aria-label="Back to chat" data-ico="chevronRight"></button>
                    <div>
                        <h1>Settings</h1>
                        <div id="settings-summary" class="settings-summary">No active session.</div>
                    </div>
                </header>
                ${renderSettingsGroups()}
            </div>
        </section>

        <button type="button" id="jump-to-latest" class="jump-to-latest hidden" title="Scroll to latest" aria-label="Scroll to latest" data-ico="arrowDown"></button>

        <footer class="composer-area">
            <div id="working-set" class="working-set hidden">
                <button type="button" id="working-set-toggle" class="working-set-toggle" aria-expanded="false">
                    <span class="working-set-chevron" data-ico="chevronRight"></span>
                    <span id="working-set-summary" class="working-set-summary"></span>
                </button>
                <div id="working-set-body" class="working-set-body hidden">
                    <section id="plan-section" class="ws-section hidden">
                        <div class="ws-section-header"><span>Plan</span><span id="plan-count" class="ws-count"></span></div>
                        <div id="plan-list" class="plan-list"></div>
                    </section>
                    <section id="agents-section" class="ws-section hidden">
                        <div class="ws-section-header"><span>Subagents</span><span id="agent-count" class="ws-count"></span></div>
                        <div id="agent-thread-list" class="agent-thread-list"></div>
                    </section>
                    <section id="files-section" class="ws-section hidden">
                        <div class="ws-section-header"><span>Changed files</span><span id="changed-files-count" class="ws-count"></span></div>
                        <div id="changed-files-list" class="changed-files-list"></div>
                    </section>
                </div>
            </div>

            <div id="prompt-queue" class="prompt-queue hidden" aria-label="Queued prompts"></div>

            <div class="composer" id="composer">
                <div id="suggestions" class="suggestions hidden" role="listbox" aria-label="Suggestions"></div>
                <div class="composer-context" id="composer-context">
                    <button type="button" id="mention-button" class="chip-button" title="Mention a file (@)" aria-label="Mention a file" data-ico="mention"></button>
                    <button type="button" id="editor-context-chip" class="context-chip hidden" aria-pressed="true" title="Include the active editor as context">
                        <span class="context-chip-icon" data-ico="file"></span>
                        <span id="editor-context-label" class="context-chip-label"></span>
                        <span id="editor-context-toggle" class="context-chip-toggle" data-ico="eye"></span>
                    </button>
                </div>
                <textarea id="chat-input" class="chat-input" rows="1" placeholder="Ask StemCode to build, fix, or explain…" aria-label="Message StemCode"></textarea>
                <div class="composer-toolbar">
                    <div class="toolbar-left">
                        <select id="profile-select" class="pill-select" title="Agent profile" aria-label="Agent profile"></select>
                        <button type="button" id="model-button" class="pill-button" title="Model and reasoning" aria-label="Model and reasoning" aria-haspopup="menu">
                            <span id="model-button-label" class="pill-label">Model</span>
                            <span class="pill-chevron" data-ico="chevronDown"></span>
                        </button>
                    </div>
                    <div class="toolbar-right">
                        <span id="status-text" class="status-text" aria-live="polite"></span>
                        <span id="context-meter" class="context-meter" title="Context usage" hidden>
                            <svg class="context-ring" viewBox="0 0 20 20" width="16" height="16" aria-hidden="true"><circle class="ring-bg" cx="10" cy="10" r="8"/><circle id="context-ring-fg" class="ring-fg" cx="10" cy="10" r="8" stroke-dasharray="50.27" stroke-dashoffset="50.27"/></svg>
                            <span id="context-meter-label" class="context-meter-label"></span>
                        </span>
                        <button type="button" id="send-button" class="send-button" title="Send (Enter)" aria-label="Send message" data-ico="send"></button>
                    </div>
                </div>
            </div>
            <div class="composer-hint" id="composer-hint"><kbd>Enter</kbd> send · <kbd>Shift</kbd>+<kbd>Enter</kbd> new line · <kbd>Esc</kbd> stop · <kbd>↑</kbd> history</div>
        </footer>
    </div>

    <div id="agent-menu" class="popup-menu hidden" role="menu"></div>

    <div id="modal-backdrop" class="modal-backdrop hidden">
        <div class="modal" role="dialog" aria-modal="true" aria-labelledby="modal-title">
            <h2 id="modal-title"></h2>
            <p id="modal-description" class="modal-description"></p>
            <div id="modal-body" class="modal-body"></div>
            <div id="modal-actions" class="modal-actions"></div>
        </div>
    </div>
    <div id="toast" class="toast hidden" role="status" aria-live="polite"></div>
    <script nonce="${nonce}" src="${scriptUri}"></script>
</body>
</html>`;
}

export function getNonce(): string {
    const chars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
    let result = '';
    const array = new Uint8Array(32);
    crypto.getRandomValues(array);
    for (let i = 0; i < array.length; i++) {
        result += chars[array[i] % chars.length];
    }
    return result;
}
