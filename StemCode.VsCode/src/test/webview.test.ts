// End-to-end tests for the chat webview: loads the real HTML and the built dist/webview.js bundle
// into jsdom, drives it with host messages and user input, and asserts on DOM + posted messages.
import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import { JSDOM } from 'jsdom';
import { createFakeWebview, installVscodeStub, vscodeStub } from './helpers/vscodeStub';

installVscodeStub();
// eslint-disable-next-line @typescript-eslint/no-var-requires
const { getChatWebviewContent } = require('../webviews/ChatWebviewContent');

const bundlePath = path.resolve(__dirname, '..', '..', 'dist', 'webview.js');

type Posted = { command: string; [key: string]: unknown };

type Harness = {
    dom: JSDOM;
    window: JSDOM['window'];
    document: Document;
    posted: Posted[];
    send(message: Record<string, unknown>): void;
    frame(): Promise<void>;
    type(text: string): void;
    key(target: Element, key: string, init?: Record<string, unknown>): KeyboardEvent;
    $(selector: string): HTMLElement;
    $$(selector: string): HTMLElement[];
    lastPosted(command: string): Posted | undefined;
};

function createHarness(state?: unknown): Harness {
    assert.ok(fs.existsSync(bundlePath), 'dist/webview.js missing — run `npm run compile` first');
    const html: string = getChatWebviewContent(createFakeWebview(), vscodeStub.Uri.file('/ext'), 'testnonce');
    const dom = new JSDOM(html, { runScripts: 'outside-only', pretendToBeVisual: true, url: 'https://test.vscode-cdn.net/' });
    const window = dom.window;
    const posted: Posted[] = [];
    let savedState = state;
    (window as unknown as Record<string, unknown>).acquireVsCodeApi = () => ({
        // Round-trip through JSON: mirrors real webview->host serialization and moves objects into this realm.
        postMessage: (message: Posted) => posted.push(JSON.parse(JSON.stringify(message))),
        getState: () => savedState,
        setState: (next: unknown) => { savedState = next; }
    });
    window.eval(fs.readFileSync(bundlePath, 'utf8'));

    const document = window.document;
    const harness: Harness = {
        dom,
        window,
        document,
        posted,
        send(message) {
            window.dispatchEvent(new window.MessageEvent('message', { data: message }));
        },
        frame() {
            return new Promise(resolve => setTimeout(resolve, 40));
        },
        type(text) {
            const input = document.getElementById('chat-input') as HTMLTextAreaElement;
            input.focus();
            input.value = text;
            input.setSelectionRange(text.length, text.length);
            input.dispatchEvent(new window.Event('input', { bubbles: true }));
        },
        key(target, key, init = {}) {
            const event = new window.KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...init });
            target.dispatchEvent(event);
            return event;
        },
        $(selector) {
            const element = document.querySelector(selector);
            assert.ok(element, `missing element ${selector}`);
            return element as HTMLElement;
        },
        $$(selector) {
            return Array.from(document.querySelectorAll(selector)) as HTMLElement[];
        },
        lastPosted(command) {
            return posted.filter(message => message.command === command).pop();
        }
    };
    return harness;
}

const sessionInfo = {
    sessionId: 's1',
    providerName: 'anthropic',
    modelId: 'claude-sonnet',
    availableModelIds: ['claude-sonnet', 'claude-opus'],
    thinkingMode: 'off',
    reasoningEffort: 'medium',
    agentProfileName: 'build',
    availableAgentProfiles: [{ name: 'build' }, { name: 'plan' }],
    sectionTitle: 'Refactor parser',
    activeModelContextWindowTokens: 200000,
    sectionEstimatedContextTokens: 170000
};

describe('chat webview', function () {
    this.timeout(10000);
    let h: Harness;

    beforeEach(() => {
        h = createHarness();
    });

    afterEach(() => {
        h.window.close();
    });

    it('boots, posts ready, and hydrates icons', () => {
        assert.ok(h.posted.some(message => message.command === 'ready'));
        assert.ok(h.$('#new-chat-button svg'));
        assert.ok(h.$('#send-button svg'));
        assert.strictEqual(h.$('#empty-state').hidden, false);
        assert.ok(!h.posted.some(message => message.command === 'webviewLog' && message.level === 'error'),
            JSON.stringify(h.posted.filter(message => message.level === 'error')));
    });

    it('renders session info: title, model pill, and context meter warning', () => {
        h.send({ command: 'setSessionInfo', sessionInfo, workingDirectory: '/repo' });
        assert.strictEqual(h.$('#section-title').textContent, 'Refactor parser');
        assert.ok(h.$('#model-button-label').textContent?.includes('claude-sonnet'));
        assert.ok(h.$('#model-button-label').textContent?.includes('Medium'));
        const meter = h.$('#context-meter');
        assert.strictEqual(meter.hidden, false);
        assert.strictEqual(h.$('#context-meter-label').textContent, '85%');
        assert.ok(meter.classList.contains('context-meter-warn'));
    });

    it('sends on Enter, keeps Shift+Enter for new lines, and recalls history with ArrowUp', () => {
        const input = h.$('#chat-input') as HTMLTextAreaElement;
        h.type('first prompt');
        const shiftEnter = h.key(input, 'Enter', { shiftKey: true });
        assert.strictEqual(shiftEnter.defaultPrevented, false);
        h.key(input, 'Enter');
        assert.deepStrictEqual(h.lastPosted('sendMessage'), { command: 'sendMessage', text: 'first prompt' });
        assert.strictEqual(input.value, '');

        h.key(input, 'ArrowUp');
        assert.strictEqual(input.value, 'first prompt');
        h.key(input, 'ArrowDown');
        assert.strictEqual(input.value, '');
    });

    it('persists prompt history across reloads via webview state', () => {
        h.type('remember me');
        h.key(h.$('#chat-input'), 'Enter');
        const stateCalls = h.posted.length;
        assert.ok(stateCalls > 0);
        h.window.close();

        h = createHarness({ promptHistory: ['remember me'], includeEditorContext: true });
        const input = h.$('#chat-input') as HTMLTextAreaElement;
        input.focus();
        h.key(input, 'ArrowUp');
        assert.strictEqual(input.value, 'remember me');
    });

    it('attaches active editor context and lets the user turn it off', () => {
        h.send({ command: 'activeEditor', editor: { relativePath: 'src/parser.ts', languageId: 'typescript', selection: { startLine: 4, endLine: 9 } } });
        const chip = h.$('#editor-context-chip');
        assert.ok(!chip.classList.contains('hidden'));
        assert.strictEqual(h.$('#editor-context-label').textContent, 'parser.ts:4-9');

        h.type('explain this');
        h.key(h.$('#chat-input'), 'Enter');
        const withContext = h.lastPosted('sendMessage');
        assert.deepStrictEqual(withContext?.editorContext, { relativePath: 'src/parser.ts', languageId: 'typescript', selection: { startLine: 4, endLine: 9 } });

        chip.click();
        assert.strictEqual(chip.getAttribute('aria-pressed'), 'false');
        h.type('no context please');
        h.key(h.$('#chat-input'), 'Enter');
        assert.strictEqual(h.lastPosted('sendMessage')?.editorContext, undefined);

        h.type('/help');
        chip.click();
        h.key(h.$('#chat-input'), 'Enter');
        assert.strictEqual(h.lastPosted('sendMessage')?.editorContext, undefined, 'slash commands never carry context');
    });

    it('streams assistant markdown with code blocks, tables, and safe text', async () => {
        h.send({ command: 'appendMessage', role: 'user', text: 'hello <img src=x onerror=alert(1)>', attachments: ['src/a.ts:3-5'] });
        const user = h.$('.message-card.user');
        assert.strictEqual(user.querySelector('img'), null, 'user text must not be parsed as HTML');
        assert.ok(user.textContent?.includes('<img src=x onerror=alert(1)>'));
        assert.strictEqual(h.$('.attachment-chip').textContent, 'src/a.ts:3-5');

        const chunks = ['Here is **bold** and `code`.\n\n', '```ts\nconst x = 1;\n```\n\n', '| a | b |\n|---|---|\n| 1 | 2 |\n'];
        for (const text of chunks) {
            h.send({ command: 'appendMessageChunk', chunk: { sessionId: 's1', role: 'assistant', text } });
        }
        await h.frame();

        const assistant = h.$('.message-card.assistant');
        assert.strictEqual(h.$$('.message-card.assistant').length, 1, 'chunks coalesce into one message');
        assert.strictEqual(assistant.querySelector('strong')?.textContent, 'bold');
        assert.strictEqual(assistant.querySelector('p code')?.textContent, 'code');
        assert.strictEqual(assistant.querySelector('.code-block-language')?.textContent, 'ts');
        assert.strictEqual(assistant.querySelector('.markdown-code-block pre code')?.textContent, 'const x = 1;');
        assert.strictEqual(assistant.querySelectorAll('.markdown-table-wrap td').length, 2);

        (assistant.querySelector('.code-action[title="Insert at cursor"]') as HTMLElement).click();
        assert.deepStrictEqual(h.lastPosted('insertCode'), { command: 'insertCode', text: 'const x = 1;' });
        (assistant.querySelector('.code-action[title="Open in new editor"]') as HTMLElement).click();
        assert.strictEqual(h.lastPosted('openInEditor')?.language, 'typescript');
    });

    it('links real file references but not abbreviations', async () => {
        h.send({ command: 'appendMessage', role: 'assistant', text: 'See src/app/main.ts:12 and README.md, e.g. Node.js docs.' });
        const links = h.$$('.message-card.assistant a.file-link');
        assert.deepStrictEqual(links.map(link => link.textContent), ['src/app/main.ts:12', 'README.md']);
        links[0].click();
        assert.deepStrictEqual(h.lastPosted('openFile'), { command: 'openFile', filePath: 'src/app/main.ts', line: 12 });
    });

    it('renders thinking as a collapsed disclosure that finalizes when the turn ends', async () => {
        h.send({ command: 'setPromptState', promptState: { isRunning: true, isCancelling: false, input: 'go' } });
        h.send({ command: 'appendMessageChunk', chunk: { sessionId: 's1', role: 'reasoning', text: 'pondering' } });
        await h.frame();
        const details = h.$('.message-card.reasoning details.thinking-details') as HTMLDetailsElement;
        assert.strictEqual(details.open, false);
        assert.strictEqual(h.$('.message-card.reasoning .inline-call-title').textContent, 'Thinking…');

        h.send({ command: 'setPromptState', promptState: { isRunning: false, isCancelling: false } });
        assert.strictEqual(h.$('.message-card.reasoning .inline-call-title').textContent, 'Thought process');
    });

    it('shows tool calls as compact rows that update in place', () => {
        h.send({ command: 'setSessionInfo', sessionInfo, workingDirectory: '/repo' });
        h.send({ command: 'setToolCall', toolCall: { sessionId: 's1', toolCallId: 't1', title: 'shell', kind: 'execute', status: 'in_progress', rawInput: { command: 'npm test' } } });
        let row = h.$('.message-card.tool');
        assert.strictEqual(row.querySelector('.inline-call-title')?.textContent, 'Run');
        assert.strictEqual(row.querySelector('.inline-call-target')?.textContent, 'npm test');
        assert.ok(row.querySelector('.status-spinner'));

        h.send({ command: 'setToolCall', toolCall: { sessionId: 's1', toolCallId: 't1', status: 'completed', content: ['ok: 12 passing'] } });
        assert.strictEqual(h.$$('.message-card.tool').length, 1);
        row = h.$('.message-card.tool');
        assert.ok(row.querySelector('.status-check'));
        assert.ok(row.textContent?.includes('ok: 12 passing'));
        assert.strictEqual((row.querySelector('details') as HTMLDetailsElement).open, false, 'non-edit tools start collapsed');
    });

    it('opens small diffs inline with +/- stats', () => {
        const patch = '*** Begin Patch\n*** Update File: src/a.ts\n@@\n-old\n+new\n+extra\n*** End Patch';
        h.send({ command: 'setToolCall', toolCall: { sessionId: 's1', toolCallId: 'e1', title: 'apply_patch', kind: 'edit', status: 'completed', rawInput: { patch } } });
        const row = h.$('.message-card.tool');
        assert.strictEqual(row.querySelector('.inline-call-title')?.textContent, 'Edit');
        assert.strictEqual(row.querySelector('.inline-call-stats .stat-added')?.textContent, '+2');
        assert.strictEqual(row.querySelector('.inline-call-stats .stat-removed')?.textContent, '−1');
        assert.strictEqual((row.querySelector('details') as HTMLDetailsElement).open, true);
        assert.strictEqual(row.querySelectorAll('.diff-line.diff-add').length, 2);
    });

    it('handles permission requests inline with number-key shortcuts', () => {
        h.send({
            command: 'showClientRequest',
            request: {
                id: 'p1',
                kind: 'permission',
                title: 'Run shell command?',
                description: 'npm install',
                allowCancellation: true,
                options: [
                    { optionId: 'allow', name: 'Allow once', kind: 'allow_once' },
                    { optionId: 'always', name: 'Always allow', kind: 'allow_always' },
                    { optionId: 'deny', name: 'Deny', kind: 'reject_once' }
                ]
            }
        });
        assert.ok(h.$('#modal-backdrop').classList.contains('hidden'), 'permission prompts do not open the modal');
        const card = h.$('.message-card.permission.pending');
        assert.strictEqual(card.querySelector('.permission-title')?.textContent, 'Run shell command?');
        assert.strictEqual(card.querySelectorAll('.permission-option').length, 4);
        assert.strictEqual(h.document.activeElement?.textContent?.includes('Allow once'), true, 'default option is focused');

        h.key(h.document.activeElement as Element, '2');
        assert.deepStrictEqual(h.lastPosted('resolveClientRequest'), {
            command: 'resolveClientRequest',
            requestId: 'p1',
            resolution: { outcome: 'selected', optionId: 'always' }
        });
        assert.ok(card.classList.contains('resolved'));
        assert.ok(card.classList.contains('outcome-approved'));
        assert.strictEqual(card.querySelector('.permission-options'), null);

        h.send({ command: 'resolveClientRequest', requestId: 'p1' });
        assert.strictEqual(h.posted.filter(message => message.command === 'resolveClientRequest').length, 1, 'no double resolution');
    });

    it('auto-selects the default permission option after the countdown', async () => {
        h.send({
            command: 'showClientRequest',
            request: {
                id: 'p2', kind: 'permission', title: 'Edit file?', allowCancellation: false, defaultOptionId: 'yes',
                autoSelectAfterMilliseconds: 60,
                options: [{ optionId: 'no', name: 'No', kind: 'reject_once' }, { optionId: 'yes', name: 'Yes', kind: 'allow_once' }]
            }
        });
        await new Promise(resolve => setTimeout(resolve, 400));
        assert.deepStrictEqual(h.lastPosted('resolveClientRequest')?.resolution, { outcome: 'selected', optionId: 'yes' });
    });

    it('uses a modal for text requests and clears secrets on submit', () => {
        h.send({ command: 'showClientRequest', request: { id: 'k1', kind: 'text', label: 'API key', isSecret: true, allowCancellation: true } });
        assert.ok(!h.$('#modal-backdrop').classList.contains('hidden'));
        const input = h.$('#modal-body input') as HTMLInputElement;
        assert.strictEqual(input.type, 'password');
        input.value = 'sk-secret';
        h.key(input, 'Enter');
        assert.deepStrictEqual(h.lastPosted('resolveClientRequest')?.resolution, { outcome: 'submitted', value: 'sk-secret' });
        assert.ok(h.$('#modal-backdrop').classList.contains('hidden'));
        assert.strictEqual(input.value, '');
    });

    it('turns send into stop while running, cancels on Escape, and queues follow-ups', () => {
        const input = h.$('#chat-input') as HTMLTextAreaElement;
        const send = h.$('#send-button') as HTMLButtonElement;
        h.send({ command: 'setPromptState', promptState: { isRunning: true, isCancelling: false, input: 'work' } });
        assert.ok(send.classList.contains('stop-mode'));
        assert.ok(h.$('.progress-indicator'));

        h.key(input, 'Escape');
        assert.ok(h.lastPosted('cancelPrompt'));

        h.type('follow up');
        assert.ok(!send.classList.contains('stop-mode'), 'with text, the button queues instead of stopping');
        h.key(input, 'Enter');
        assert.strictEqual(h.lastPosted('sendMessage'), undefined);
        assert.strictEqual(h.$$('.prompt-queue-item').length, 1);

        h.send({ command: 'setPromptState', promptState: { isRunning: false, isCancelling: false } });
        assert.strictEqual(h.lastPosted('sendMessage')?.text, 'follow up');
        assert.strictEqual(h.$$('.prompt-queue-item').length, 0);
        assert.strictEqual(h.document.querySelector('.progress-indicator'), null);
    });

    it('suggests slash commands without clobbering typed arguments', () => {
        const input = h.$('#chat-input') as HTMLTextAreaElement;
        h.type('/hel');
        assert.ok(!h.$('#suggestions').classList.contains('hidden'));
        assert.strictEqual(h.$('.suggestion.active .suggestion-usage').textContent, '/help');
        h.key(input, 'Enter');
        assert.strictEqual(input.value, '/help');
        assert.strictEqual(h.lastPosted('sendMessage'), undefined);
        h.key(input, 'Enter');
        assert.strictEqual(h.lastPosted('sendMessage')?.text, '/help');

        h.type('/compact 5');
        assert.ok(h.$('#suggestions').classList.contains('hidden'));
        h.key(input, 'Enter');
        assert.strictEqual(h.lastPosted('sendMessage')?.text, '/compact 5');
    });

    it('replaces the slash command list with the CLI-advertised commands', () => {
        const input = h.$('#chat-input') as HTMLTextAreaElement;
        h.send({
            command: 'setAvailableCommands',
            commands: [
                { name: 'review-pr', description: 'Review a PR', usage: '/review-pr <number>', input: { hint: '<number>' }, custom: true },
                { name: 'help', description: 'List commands.', usage: '/help', custom: false }
            ]
        });
        h.type('/review');
        assert.strictEqual(h.$('.suggestion.active .suggestion-usage').textContent, '/review-pr <number>');
        h.key(input, 'Tab');
        assert.strictEqual(input.value, '/review-pr ');

        // Locally handled commands stay available alongside the server list.
        h.type('/voi');
        assert.ok(h.$$('.suggestion').some(item => item.textContent?.includes('/voice setup')));
    });

    it('requests @file mentions and inserts the chosen path', () => {
        const input = h.$('#chat-input') as HTMLTextAreaElement;
        h.type('look at @pars');
        assert.deepStrictEqual(h.lastPosted('searchFiles'), { command: 'searchFiles', query: 'pars' });
        h.send({ command: 'fileMentions', query: 'pars', files: ['src/parser.ts', 'src/parse-utils.ts'] });
        assert.strictEqual(h.$$('.suggestion').length, 2);
        h.key(input, 'Tab');
        assert.strictEqual(input.value, 'look at src/parser.ts ');
    });

    it('shows a retry banner when the process fails', () => {
        h.send({ command: 'setProcessStatus', status: 'error' });
        const banner = h.$('#status-banner');
        assert.ok(!banner.classList.contains('hidden'));
        assert.ok(banner.classList.contains('tone-error'));
        h.$('#status-retry-button').click();
        assert.ok(h.lastPosted('restartProcess'));
        h.send({ command: 'setProcessStatus', status: 'running' });
        assert.ok(banner.classList.contains('hidden'));
    });

    it('summarizes plan and changed files in the working set strip', () => {
        assert.ok(h.$('#working-set').classList.contains('hidden'));
        h.send({ command: 'setPlan', plan: { sessionId: 's1', entries: [
            { content: 'Read code', status: 'completed' },
            { content: 'Fix bug', status: 'in_progress' },
            { content: 'Add tests', status: 'pending' }
        ] } });
        h.send({ command: 'setFileEditsSummary', summary: { sessionId: 's1', files: [
            { displayPath: 'src/a.ts', absolutePath: '/repo/src/a.ts', addedLineCount: 5, removedLineCount: 2, editCount: 1, action: 'Edited' }
        ] } });
        const strip = h.$('#working-set');
        assert.ok(!strip.classList.contains('hidden'));
        const summary = h.$('#working-set-summary').textContent ?? '';
        assert.ok(summary.includes('Plan 1/3'), summary);
        assert.ok(summary.includes('Fix bug'), summary);
        assert.ok(summary.includes('1 file changed'), summary);

        h.$('#working-set-toggle').click();
        assert.ok(!h.$('#working-set-body').classList.contains('hidden'));
        assert.strictEqual(h.$$('.plan-item').length, 3);
        assert.strictEqual(h.$('.changed-file-name').textContent, 'a.ts');
        h.$('.changed-file-name').click();
        assert.strictEqual(h.lastPosted('openFile')?.filePath, '/repo/src/a.ts');
    });

    it('wires header, starter, and settings actions to host commands', () => {
        h.$('#new-chat-button').click();
        assert.ok(h.lastPosted('newChat'));
        h.$('#sessions-button').click();
        assert.ok(h.lastPosted('listSessions'));

        h.$('.starter[data-command]').click();
        assert.deepStrictEqual(h.lastPosted('runVsCodeCommand'), { command: 'runVsCodeCommand', id: 'stemcode.reviewGitDiff' });
        h.$('.starter').click();
        assert.ok(String(h.lastPosted('sendMessage')?.text).startsWith('Give me a high-level overview'));

        h.$('#settings-button').click();
        assert.ok(!h.$('#settings-page').classList.contains('hidden'));
        assert.ok(h.$('.composer-area').classList.contains('hidden'));
        h.$('.settings-action[data-action="restartProcess"]').click();
        assert.ok(h.lastPosted('restartProcess'));
        h.key(h.document.body, 'Escape');
        assert.ok(h.$('#settings-page').classList.contains('hidden'));
    });

    it('opens the model menu and switches models', () => {
        h.send({ command: 'setSessionInfo', sessionInfo, workingDirectory: '/repo' });
        h.$('#model-button').click();
        const menu = h.$('#agent-menu');
        assert.ok(!menu.classList.contains('hidden'));
        const opus = h.$$('.agent-menu-item').find(item => item.textContent === 'claude-opus');
        assert.ok(opus);
        opus.click();
        assert.deepStrictEqual(h.lastPosted('changeModel'), { command: 'changeModel', modelId: 'claude-opus' });
        assert.ok(menu.classList.contains('hidden'));
    });

    it('lists sessions in a modal that Escape closes', () => {
        h.send({ command: 'sessionList', currentSessionId: 's1', sessions: [
            { sessionId: 's1', title: 'Current one', turnCount: 3 },
            { sessionId: 's2', title: 'Older one', turnCount: 1 }
        ] });
        assert.ok(!h.$('#modal-backdrop').classList.contains('hidden'));
        assert.strictEqual(h.$$('.modal-option').length, 2);
        h.$$('.modal-option')[1].click();
        assert.deepStrictEqual(h.lastPosted('resumeSession'), { command: 'resumeSession', sessionId: 's2' });

        h.send({ command: 'sessionList', currentSessionId: 's1', sessions: [] });
        h.key(h.document.body, 'Escape');
        assert.ok(h.$('#modal-backdrop').classList.contains('hidden'));
    });

    it('clears the transcript and restores the empty state', async () => {
        h.send({ command: 'appendMessage', role: 'user', text: 'hi' });
        h.send({ command: 'appendMessage', role: 'system', text: 'Error: provider unavailable' });
        assert.ok(h.$('.message-card.system').classList.contains('notice-error'));
        h.send({ command: 'clearMessages' });
        assert.strictEqual(h.$$('.message-card').length, 0);
        assert.strictEqual(h.$('#empty-state').hidden, false);
    });

    it('caps very large tool output and expands on demand', () => {
        const content = [Array.from({ length: 1000 }, (_, i) => `row ${i}`).join('\n')];
        h.send({ command: 'setToolCall', toolCall: { sessionId: 's1', toolCallId: 'big', title: 'search', kind: 'search', status: 'completed', rawInput: { pattern: 'row' }, content } });
        const row = h.$('.message-card.tool');
        assert.strictEqual(row.querySelectorAll('.vscode-code-line').length, 300);
        (row.querySelector('.show-more-button') as HTMLElement).click();
        assert.strictEqual(row.querySelectorAll('.vscode-code-line').length, 1000);
    });
});
