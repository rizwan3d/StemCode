import { ChildProcess, spawn } from 'child_process';
import * as vscode from 'vscode';
import { LogService } from '../services/LogService';
import { StemCodeProcessManager } from '../services/StemCodeProcessManager';
import { ChatViewProvider } from '../webviews/ChatViewProvider';
import { ChatWebviewController } from '../webviews/ChatWebviewController';

interface VoiceModelOption {
    Id: string;
    Label: string;
    Description: string;
    IsRecommended: boolean;
}

interface VoiceInputDevice {
    Id: string;
    Name: string;
    IsDefault: boolean;
}

type VoiceRunResult =
    | { ok: true; stdout: string }
    | { ok: false; cancelled: boolean; error?: string };

/** Context key that is true while a voice CLI process is running (drives the mic/stop icons). */
const VOICE_BUSY_CONTEXT = 'stemcode.voiceDictating';
/** How long to wait for the sidebar chat to load after revealing it. */
const CHAT_VIEW_RESOLVE_TIMEOUT_MS = 3000;

/**
 * Quotes one argument for cmd.exe. Node does not quote arguments when `shell: true`,
 * so values with spaces or shell metacharacters (e.g. microphone IDs) must be quoted here.
 */
export function quoteWindowsShellArg(arg: string): string {
    if (arg && !/[\s"&|<>^%()!]/.test(arg)) {
        return arg;
    }

    return `"${arg.replace(/"/g, '""')}"`;
}

/** Joins dictated text onto existing text with a single separating space. */
export function joinDictation(before: string, transcript: string, after = ''): string {
    const text = transcript.trim();
    if (!text) {
        return before + after;
    }

    const lead = before.length > 0 && !/\s$/.test(before) ? ' ' : '';
    const trail = after.length > 0 && !/^\s/.test(after) ? ' ' : '';
    return before + lead + text + trail + after;
}

/** Turns the CLI's stderr progress line into a friendlier notification message. */
export function describeVoiceProgress(line: string): string {
    return /^listening\b/i.test(line.trim())
        ? 'Listening… speak now (stops after a short pause)'
        : line.trim();
}

/**
 * Runs the `stemcode --voice-*` one-shot CLI commands. Only one runs at a time, because
 * dictation holds the microphone; cancelling kills the whole process tree so the voice
 * runtime releases the microphone too.
 */
class VoiceRunner {
    private active: { child: ChildProcess; cancelled: boolean } | undefined;

    constructor(private readonly processManager: StemCodeProcessManager) {
    }

    public get isBusy(): boolean {
        return this.active !== undefined;
    }

    public cancel(): void {
        const active = this.active;
        if (!active || active.cancelled) {
            return;
        }

        active.cancelled = true;
        killProcessTree(active.child);
    }

    public run(
        args: string[],
        onProgress?: (line: string) => void
    ): Promise<VoiceRunResult> {
        if (this.active) {
            return Promise.resolve({ ok: false, cancelled: false, error: 'A voice operation is already in progress.' });
        }

        const isWindows = process.platform === 'win32';
        const command = this.processManager.getCliSpawnCommand();
        const spawnArgs = isWindows ? args.map(quoteWindowsShellArg) : args;
        const cwd = getWorkingDirectory();
        LogService.getInstance().info(`Voice: running ${command} ${spawnArgs.join(' ')}`);

        return new Promise<VoiceRunResult>(resolve => {
            let child: ChildProcess;
            try {
                child = spawn(command, spawnArgs, {
                    cwd,
                    env: process.env,
                    shell: isWindows,
                    windowsHide: true,
                    // Own process group on POSIX so cancel can signal the voice runtime as well.
                    detached: !isWindows
                });
            } catch (error) {
                resolve({ ok: false, cancelled: false, error: error instanceof Error ? error.message : String(error) });
                return;
            }

            const active = { child, cancelled: false };
            this.active = active;
            void vscode.commands.executeCommand('setContext', VOICE_BUSY_CONTEXT, true);

            let stdout = '';
            let stderr = '';
            let settled = false;
            const finish = (result: VoiceRunResult) => {
                if (settled) {
                    return;
                }
                settled = true;
                if (this.active === active) {
                    this.active = undefined;
                }
                void vscode.commands.executeCommand('setContext', VOICE_BUSY_CONTEXT, false);
                resolve(result);
            };

            child.stdout?.on('data', data => {
                stdout += data.toString();
            });
            child.stderr?.on('data', data => {
                const text = data.toString().trim();
                if (!text) {
                    return;
                }
                stderr = text;
                const lines = text.split(/\r?\n/);
                const lastLine = lines[lines.length - 1].trim();
                if (lastLine) {
                    onProgress?.(lastLine);
                }
            });
            child.on('error', error => {
                LogService.getInstance().error('Voice: failed to start the StemCode CLI', error);
                finish({ ok: false, cancelled: active.cancelled, error: `Could not start StemCode: ${error.message}` });
            });
            child.on('close', code => {
                if (active.cancelled) {
                    finish({ ok: false, cancelled: true });
                    return;
                }
                if (code !== 0) {
                    const message = stderr || `Voice command exited with code ${code ?? 'unknown'}.`;
                    LogService.getInstance().warn(`Voice: ${message}`);
                    finish({ ok: false, cancelled: false, error: message });
                    return;
                }
                finish({ ok: true, stdout: stdout.trim() });
            });
        });
    }
}

export function registerVoiceCommand(
    context: vscode.ExtensionContext,
    processManager: StemCodeProcessManager,
    chatViewProvider: ChatViewProvider
): void {
    const runner = new VoiceRunner(processManager);
    void vscode.commands.executeCommand('setContext', VOICE_BUSY_CONTEXT, false);

    context.subscriptions.push(
        vscode.commands.registerCommand('stemcode.voiceDictation', async () => {
            if (runner.isBusy) {
                // The mic button and keybinding act as a toggle: a second press cancels.
                runner.cancel();
                return;
            }

            const target = ChatWebviewController.getLastActive();
            const transcript = await dictate(runner);
            if (transcript) {
                await deliverToChat(transcript, target, chatViewProvider);
            }
        }),
        vscode.commands.registerCommand('stemcode.voiceDictateToEditor', async () => {
            if (runner.isBusy) {
                runner.cancel();
                return;
            }

            const editor = vscode.window.activeTextEditor;
            if (!editor) {
                vscode.window.showInformationMessage('Open a file to dictate into, or use StemCode: Voice Dictation for the chat.');
                return;
            }

            const transcript = await dictate(runner);
            if (transcript) {
                await deliverToEditor(transcript, editor);
            }
        }),
        vscode.commands.registerCommand('stemcode.voiceCancel', () => {
            runner.cancel();
        }),
        vscode.commands.registerCommand('stemcode.voiceSetup', async () => {
            await setupVoice(runner);
        }),
        vscode.commands.registerCommand('stemcode.voiceUpdate', async () => {
            const result = await runWithProgress(runner, ['--voice-update'], 'Updating voice models');
            if (result !== undefined) {
                vscode.window.showInformationMessage('Voice models are up to date.');
            }
        }),
        { dispose: () => runner.cancel() }
    );
}

async function dictate(runner: VoiceRunner): Promise<string | undefined> {
    const transcript = await runWithProgress(runner, ['--voice-dictate'], 'StemCode voice', describeVoiceProgress);
    if (transcript === undefined) {
        return undefined;
    }

    if (!transcript.trim()) {
        vscode.window.showInformationMessage('No speech was detected.');
        return undefined;
    }

    return transcript.trim();
}

async function deliverToChat(
    transcript: string,
    target: ChatWebviewController | undefined,
    chatViewProvider: ChatViewProvider
): Promise<void> {
    let controller = target && !target.isDisposed ? target : undefined;
    if (!controller || chatViewProvider.isController(controller)) {
        await vscode.commands.executeCommand('stemcode.openChat');
        controller = controller ?? await chatViewProvider.waitForController(CHAT_VIEW_RESOLVE_TIMEOUT_MS);
    }

    if (controller && !controller.isDisposed) {
        controller.insertDictation(transcript);
        return;
    }

    await vscode.env.clipboard.writeText(transcript);
    vscode.window.showInformationMessage('Voice transcript copied to the clipboard.');
}

async function deliverToEditor(transcript: string, editor: vscode.TextEditor): Promise<void> {
    if (editor.document.isClosed) {
        await vscode.env.clipboard.writeText(transcript);
        vscode.window.showInformationMessage('The editor was closed; voice transcript copied to the clipboard.');
        return;
    }

    const document = editor.document;
    const applied = await editor.edit(builder => {
        for (const selection of editor.selections) {
            const lineText = document.lineAt(selection.start.line).text;
            const before = lineText.slice(0, selection.start.character);
            const after = document.lineAt(selection.end.line).text.slice(selection.end.character);
            const joined = joinDictation(before, transcript, after);
            builder.replace(selection, joined.slice(before.length, joined.length - after.length));
        }
    });

    if (!applied) {
        await vscode.env.clipboard.writeText(transcript);
        vscode.window.showInformationMessage('Could not insert the transcript; it was copied to the clipboard.');
    }
}

async function setupVoice(runner: VoiceRunner): Promise<void> {
    const modelJson = await runWithProgress(runner, ['--voice-models'], 'Loading voice models');
    if (modelJson === undefined) {
        return;
    }

    let models: VoiceModelOption[];
    try {
        models = JSON.parse(modelJson) as VoiceModelOption[];
    } catch {
        vscode.window.showErrorMessage('Voice setup could not read the available models.');
        return;
    }

    if (!models.length) {
        vscode.window.showInformationMessage('No voice models are available.');
        return;
    }

    const selectedModel = await vscode.window.showQuickPick(
        models.map(model => ({
            label: model.Label,
            description: model.IsRecommended ? 'Recommended' : undefined,
            detail: model.Description,
            model
        })),
        {
            title: 'Voice model',
            placeHolder: 'Choose the local speech model used for dictation'
        }
    );
    if (!selectedModel) {
        return;
    }

    const deviceJson = await runWithProgress(runner, ['--voice-devices'], 'Finding microphones');
    if (deviceJson === undefined) {
        return;
    }

    let devices: VoiceInputDevice[];
    try {
        devices = JSON.parse(deviceJson) as VoiceInputDevice[];
    } catch {
        vscode.window.showErrorMessage('Voice setup could not read microphone devices.');
        return;
    }

    let selectedDevice: VoiceInputDevice | undefined;
    if (devices.length > 1) {
        const devicePick = await vscode.window.showQuickPick(
            devices.map(device => ({
                label: device.Name,
                description: device.IsDefault ? 'System default' : undefined,
                device
            })),
            {
                title: 'Voice microphone',
                placeHolder: 'Choose the microphone used for dictation'
            }
        );
        if (!devicePick) {
            return;
        }
        selectedDevice = devicePick.device;
    } else if (devices.length === 1) {
        selectedDevice = devices[0];
    }

    const args = ['--voice-configure', '--model', selectedModel.model.Id];
    if (selectedDevice?.Id) {
        args.push('--device', selectedDevice.Id);
    }

    const configured = await runWithProgress(runner, args, 'Saving voice setup');
    if (configured !== undefined) {
        vscode.window.showInformationMessage('Voice setup saved.');
    }
}

/**
 * Runs a voice CLI command behind a cancellable notification and mirrors its progress on the
 * chat mic buttons. Returns stdout, or undefined when it failed (error already shown) or was cancelled.
 */
async function runWithProgress(
    runner: VoiceRunner,
    args: string[],
    title: string,
    describe: (line: string) => string = line => line
): Promise<string | undefined> {
    if (runner.isBusy) {
        vscode.window.showInformationMessage('A voice operation is already in progress.');
        return undefined;
    }

    ChatWebviewController.setVoiceState({ state: 'busy', message: title });
    try {
        const result = await vscode.window.withProgress(
            {
                location: vscode.ProgressLocation.Notification,
                title,
                cancellable: true
            },
            (progress, token) => {
                const subscription = token.onCancellationRequested(() => runner.cancel());
                progress.report({ message: 'Starting…' });
                return runner
                    .run(args, line => {
                        const message = describe(line);
                        progress.report({ message });
                        ChatWebviewController.setVoiceState({ state: 'busy', message });
                    })
                    .finally(() => subscription.dispose());
            }
        );

        if (result.ok) {
            return result.stdout;
        }

        if (!result.cancelled) {
            vscode.window.showErrorMessage(result.error || 'Voice command failed.', 'Voice Setup', 'Show Logs')
                .then(choice => {
                    if (choice === 'Voice Setup') {
                        void vscode.commands.executeCommand('stemcode.voiceSetup');
                    } else if (choice === 'Show Logs') {
                        LogService.getInstance().show();
                    }
                });
        }
        return undefined;
    } finally {
        ChatWebviewController.setVoiceState({ state: 'idle' });
    }
}

function killProcessTree(child: ChildProcess): void {
    if (child.pid === undefined || child.exitCode !== null) {
        return;
    }

    try {
        if (process.platform === 'win32') {
            // shell: true means `child` is cmd.exe; /T takes the CLI and the voice runtime with it.
            spawn('taskkill', ['/pid', String(child.pid), '/T', '/F'], { windowsHide: true })
                .on('error', () => child.kill());
        } else {
            process.kill(-child.pid, 'SIGTERM');
        }
    } catch {
        child.kill('SIGTERM');
    }
}

function getWorkingDirectory(): string | undefined {
    const configured = vscode.workspace.getConfiguration('stemcode').get<string>('workingDirectory');
    return configured || vscode.workspace.workspaceFolders?.[0]?.uri.fsPath || undefined;
}
