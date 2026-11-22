// Runs inside a real VS Code extension host against a fake ACP agent (test-fixtures/fake-acp-agent.js).
import * as assert from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as vscode from 'vscode';

const EXTENSION_ID = 'growbitlab.stemcode-vscode';

type LoggedRequest = { method?: string; params?: { prompt?: { text?: string }[] } };

function readAgentLog(logFile: string): LoggedRequest[] {
    if (!fs.existsSync(logFile)) {
        return [];
    }
    return fs.readFileSync(logFile, 'utf8')
        .split('\n')
        .filter(Boolean)
        .map(line => JSON.parse(line) as LoggedRequest);
}

async function waitFor<T>(probe: () => T | undefined | false, description: string, timeoutMs = 15000): Promise<T> {
    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
        const value = probe();
        if (value) {
            return value;
        }
        await new Promise(resolve => setTimeout(resolve, 100));
    }
    throw new Error(`Timed out waiting for ${description}`);
}

describe('StemCode extension (integration)', () => {
    const extensionRoot = path.resolve(__dirname, '..', '..', '..');
    const agentLog = path.join(os.tmpdir(), `stemcode-fake-agent-${process.pid}.log`);

    before(async () => {
        if (fs.existsSync(agentLog)) {
            fs.rmSync(agentLog);
        }
        const config = vscode.workspace.getConfiguration('stemcode');
        await config.update('command', 'node', vscode.ConfigurationTarget.Workspace);
        await config.update('args', [path.join(extensionRoot, 'test-fixtures', 'fake-acp-agent.js'), agentLog], vscode.ConfigurationTarget.Workspace);
    });

    after(async () => {
        await vscode.commands.executeCommand('stemcode.stop');
        const config = vscode.workspace.getConfiguration('stemcode');
        await config.update('command', undefined, vscode.ConfigurationTarget.Workspace);
        await config.update('args', undefined, vscode.ConfigurationTarget.Workspace);
    });

    it('activates', async () => {
        const extension = vscode.extensions.getExtension(EXTENSION_ID);
        assert.ok(extension, `${EXTENSION_ID} is installed`);
        await extension.activate();
        assert.strictEqual(extension.isActive, true);
    });

    it('registers every contributed command', async () => {
        const extension = vscode.extensions.getExtension(EXTENSION_ID);
        assert.ok(extension);
        const contributed: { command: string }[] = extension.packageJSON.contributes.commands;
        const registered = new Set(await vscode.commands.getCommands(true));
        const missing = contributed.map(command => command.command).filter(command => !registered.has(command));
        assert.deepStrictEqual(missing, []);
    });

    it('opens the chat view and creates an ACP session', async () => {
        await vscode.commands.executeCommand('stemcode.openChat');
        await waitFor(() => readAgentLog(agentLog).some(entry => entry.method === 'session/new'), 'session/new');
        const methods = readAgentLog(agentLog).map(entry => entry.method);
        assert.ok(methods.indexOf('initialize') < methods.indexOf('session/new'), methods.join(','));
    });

    it('sends editor selections to the agent as prompts', async () => {
        const folder = vscode.workspace.workspaceFolders?.[0];
        assert.ok(folder, 'fixture workspace is open');
        const document = await vscode.workspace.openTextDocument(vscode.Uri.joinPath(folder.uri, 'sample.ts'));
        const editor = await vscode.window.showTextDocument(document);
        editor.selection = new vscode.Selection(0, 0, 0, 20);

        await vscode.commands.executeCommand('stemcode.explainSelection');
        const prompt = await waitFor(
            () => readAgentLog(agentLog).find(entry => entry.method === 'session/prompt'),
            'session/prompt');
        const text = (prompt.params?.prompt ?? []).map(part => part.text ?? '').join('');
        assert.ok(text.startsWith('Explain this selection:'), text);
        assert.ok(text.includes('export function add'), text);
    });

    it('restarts the agent process on demand', async () => {
        const before = readAgentLog(agentLog).filter(entry => entry.method === 'initialize').length;
        await vscode.commands.executeCommand('stemcode.restart');
        await waitFor(
            () => {
                void vscode.commands.executeCommand('stemcode.openChat');
                return readAgentLog(agentLog).filter(entry => entry.method === 'initialize').length > before;
            },
            'a fresh initialize after restart');
    });
});
