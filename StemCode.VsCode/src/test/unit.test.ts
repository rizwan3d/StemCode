import * as assert from 'assert';
import { buildDiffModel } from '../webview/diffModel';
import { CHAT_COMMANDS } from '../webview/chatCommands';
import {
    formatEditorContextLabel,
    formatPromptWithEditorContext,
    MAX_SELECTION_LINES
} from '../webviews/editorContext';
import { createFakeWebview, installVscodeStub, vscodeStub } from './helpers/vscodeStub';

installVscodeStub();
// eslint-disable-next-line @typescript-eslint/no-var-requires
const { getChatWebviewContent, getNonce } = require('../webviews/ChatWebviewContent');

describe('diffModel', () => {
    it('parses apply_patch blocks into per-file diffs', () => {
        const patch = [
            '*** Begin Patch',
            '*** Update File: src/a.ts',
            '@@ foo',
            ' keep',
            '-old',
            '+new',
            '*** Add File: src/b.ts',
            '+hello',
            '*** End Patch'
        ].join('\n');

        const model = buildDiffModel({ kind: 'edit', rawInput: { patch } });
        assert.ok(model);
        assert.strictEqual(model.length, 2);
        assert.strictEqual(model[0].path, 'src/a.ts');
        assert.deepStrictEqual(model[0].lines, [
            { type: 'meta', text: '@@ foo' },
            { type: 'ctx', text: 'keep' },
            { type: 'del', text: 'old' },
            { type: 'add', text: 'new' }
        ]);
        assert.deepStrictEqual(model[1].lines, [{ type: 'add', text: 'hello' }]);
    });

    it('treats file_write input as all-added lines', () => {
        const model = buildDiffModel({ kind: 'edit', title: 'file_write', rawInput: { path: 'x.txt', content: 'a\nb' } });
        assert.ok(model);
        assert.strictEqual(model.length, 1);
        assert.strictEqual(model[0].lines.length, 2);
        assert.ok(model[0].lines.every(line => line.type === 'add'));
    });

    it('returns null for non-edit tools', () => {
        assert.strictEqual(buildDiffModel({ kind: 'read', rawInput: { path: 'x' } }), null);
    });
});

describe('chatCommands', () => {
    it('has unique, slash-prefixed commands with descriptions', () => {
        const seen = new Set<string>();
        for (const command of CHAT_COMMANDS) {
            assert.ok(command.command.startsWith('/'), command.command);
            assert.ok(command.description.length > 0, command.command);
            assert.ok(command.insertText.startsWith(command.command), command.command);
            assert.ok(!seen.has(command.command), `duplicate ${command.command}`);
            seen.add(command.command);
        }
    });
});

describe('editorContext', () => {
    it('labels files and ranges', () => {
        assert.strictEqual(formatEditorContextLabel({ relativePath: 'src/a.ts' }), 'src/a.ts');
        assert.strictEqual(formatEditorContextLabel({ relativePath: 'src/a.ts', startLine: 3, endLine: 3 }), 'src/a.ts:3');
        assert.strictEqual(formatEditorContextLabel({ relativePath: 'src/a.ts', startLine: 3, endLine: 9 }), 'src/a.ts:3-9');
    });

    it('leaves the prompt untouched without context', () => {
        assert.strictEqual(formatPromptWithEditorContext('hi', undefined), 'hi');
        assert.strictEqual(formatPromptWithEditorContext('hi', { relativePath: '' }), 'hi');
    });

    it('references the active file without inlining it', () => {
        const prompt = formatPromptWithEditorContext('fix it', { relativePath: 'src/a.ts', languageId: 'typescript' });
        assert.ok(prompt.startsWith('fix it\n\n<editor-context>'));
        assert.ok(prompt.includes("active file in VS Code is `src/a.ts`"));
        assert.ok(!prompt.includes('```'));
        assert.ok(prompt.trimEnd().endsWith('</editor-context>'));
    });

    it('inlines selections in a fence that cannot be broken by the content', () => {
        const prompt = formatPromptWithEditorContext('explain', {
            relativePath: 'README.md',
            languageId: 'markdown',
            startLine: 2,
            endLine: 4,
            selectionText: 'a\n```js\ncode\n```'
        });
        assert.ok(prompt.includes('lines 2-4 of `README.md`'));
        assert.ok(prompt.includes('````markdown\na\n```js\ncode\n```\n````'));
    });

    it('truncates very large selections', () => {
        const selectionText = Array.from({ length: MAX_SELECTION_LINES + 50 }, (_, i) => `line ${i}`).join('\n');
        const prompt = formatPromptWithEditorContext('x', { relativePath: 'big.txt', startLine: 1, endLine: MAX_SELECTION_LINES + 50, selectionText });
        assert.ok(prompt.includes(`line ${MAX_SELECTION_LINES - 1}`));
        assert.ok(!prompt.includes(`line ${MAX_SELECTION_LINES + 1}\n`));
        assert.ok(prompt.includes('Selection truncated'));
    });
});

describe('ChatWebviewContent', () => {
    const html: string = getChatWebviewContent(createFakeWebview(), vscodeStub.Uri.file('/ext'), 'NONCE123');

    it('locks the page down with a nonce-based CSP', () => {
        assert.ok(html.includes("default-src 'none'"));
        assert.ok(html.includes("script-src 'nonce-NONCE123'"));
        const scripts = html.match(/<script\b[^>]*>/g) ?? [];
        assert.strictEqual(scripts.length, 1);
        assert.ok(scripts[0].includes('nonce="NONCE123"'));
        assert.ok(!/\son[a-z]+=/i.test(html), 'no inline event handlers');
    });

    it('loads assets through webview URIs', () => {
        assert.ok(html.includes('https://test.vscode-cdn.net/ext/media/chat.css'));
        assert.ok(html.includes('https://test.vscode-cdn.net/ext/dist/webview.js'));
        assert.ok(html.includes('https://test.vscode-cdn.net/ext/resources/logo.png'));
    });

    it('gives every settings row an action or a command', () => {
        const rows = html.match(/<button type="button" class="settings-action"[^>]*>/g) ?? [];
        assert.ok(rows.length >= 10);
        for (const row of rows) {
            assert.ok(/data-(action|command)="/.test(row), row);
        }
    });

    it('generates random nonces', () => {
        const a = getNonce();
        const b = getNonce();
        assert.strictEqual(a.length, 32);
        assert.notStrictEqual(a, b);
        assert.ok(/^[A-Za-z0-9]+$/.test(a));
    });
});
