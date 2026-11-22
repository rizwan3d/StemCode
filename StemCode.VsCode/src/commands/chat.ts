import * as vscode from 'vscode';
import { execFile } from 'child_process';
import { ChatPanel } from '../webviews/ChatPanel';
import { SessionManager } from '../services/SessionManager';
import { ChatViewProvider } from '../webviews/ChatViewProvider';
import { LogService } from '../services/LogService';
import { buildChatCommands, ChatCommandSuggestion } from '../webview/chatCommands';

export function registerChatCommands(
    context: vscode.ExtensionContext,
    sessionManager: SessionManager,
    chatViewProvider: ChatViewProvider
) {
    const extensionUri = context.extensionUri;

    const openChat = async () => {
        try {
            await vscode.commands.executeCommand('workbench.view.extension.stemcode');
            await vscode.commands.executeCommand(`${ChatViewProvider.viewType}.focus`);
        } catch (error) {
            LogService.getInstance().warn('Falling back to StemCode chat webview panel', error);
            ChatPanel.createOrShow(sessionManager, extensionUri);
        }

        await sessionManager.ensureStarted();
    };

    const newChat = async () => {
        await openChat();

        if (await chatViewProvider.startNewSession()) {
            return;
        }

        ChatPanel.createOrShow(sessionManager, extensionUri);
        await ChatPanel.currentPanel?.startNewSession();
    };

    const submitPrompt = async (prompt: string) => {
        await openChat();

        if (await chatViewProvider.submitMessage(prompt)) {
            return;
        }

        ChatPanel.createOrShow(sessionManager, extensionUri);
        await ChatPanel.currentPanel?.submitMessage(prompt);
    };

    const prefillPrompt = async (prompt: string) => {
        await openChat();

        if (chatViewProvider.prefillMessage(prompt)) {
            return;
        }

        ChatPanel.createOrShow(sessionManager, extensionUri);
        ChatPanel.currentPanel?.prefillMessage(prompt);
    };

    const openSettings = async () => {
        await openChat();

        if (chatViewProvider.showSettings()) {
            return;
        }

        ChatPanel.createOrShow(sessionManager, extensionUri);
        ChatPanel.currentPanel?.showSettings();
    };

    context.subscriptions.push(
        vscode.commands.registerCommand('stemcode.openChat', openChat),
        vscode.commands.registerCommand('stemcode.newChat', newChat),
        vscode.commands.registerCommand('stemcode.openSettings', openSettings),
        vscode.commands.registerCommand('stemcode.sendSelection', async () => {
            const selectedText = getSelectedText();
            if (!selectedText) {
                vscode.window.showWarningMessage('Select code or text before sending it to StemCode.');
                return;
            }

            await submitPrompt(formatEditorPrompt('Use this selection as context:', selectedText));
        }),
        vscode.commands.registerCommand('stemcode.explainSelection', async () => {
            const selectedText = getSelectedText();
            if (!selectedText) {
                vscode.window.showWarningMessage('Select code or text before asking StemCode to explain it.');
                return;
            }

            await submitPrompt(formatEditorPrompt('Explain this selection:', selectedText));
        }),
        vscode.commands.registerCommand('stemcode.sendCurrentFile', async () => {
            const editorText = getCurrentEditorText();
            if (!editorText) {
                vscode.window.showWarningMessage('Open a file before sending it to StemCode.');
                return;
            }

            await submitPrompt(formatEditorPrompt('Use this file as context:', editorText));
        }),
        vscode.commands.registerCommand('stemcode.reviewCurrentFile', async () => {
            const editorText = getCurrentEditorText();
            if (!editorText) {
                vscode.window.showWarningMessage('Open a file before asking StemCode to review it.');
                return;
            }

            await submitPrompt(formatEditorPrompt('Review this file for bugs, regressions, and missing tests:', editorText));
        }),
        vscode.commands.registerCommand('stemcode.reviewGitDiff', async () => {
            try {
                const diff = await readGitDiff();
                if (!diff.trim()) {
                    vscode.window.showInformationMessage('No git diff found for StemCode to review.');
                    return;
                }

                await submitPrompt(`Review this git diff for bugs, regressions, and missing tests:\n\n\`\`\`diff\n${diff}\n\`\`\``);
            } catch (error) {
                const message = error instanceof Error ? error.message : 'Unable to read git diff.';
                vscode.window.showErrorMessage(message);
            }
        }),
        vscode.commands.registerCommand('stemcode.planChanges', async () => {
            await prefillPrompt('Plan the following change before editing:\n\n');
        }),
        vscode.commands.registerCommand('stemcode.applySuggestedChanges', async () => {
            await submitPrompt('Apply the suggested changes from the previous StemCode response.');
        }),
        vscode.commands.registerCommand('stemcode.showTerminals', async () => {
            await submitPrompt('/terminals');
        }),
        vscode.commands.registerCommand('stemcode.runSlashCommand', async () => {
            const command = await pickSlashCommand(sessionManager);
            if (!command) {
                return;
            }

            // Commands with arguments are prefilled so the user can complete them.
            if (command.insertText.endsWith(' ')) {
                await prefillPrompt(command.insertText);
            } else {
                await submitPrompt(command.command);
            }
        }),
        ...DIRECT_SLASH_COMMANDS.map(([commandId, text]) =>
            vscode.commands.registerCommand(commandId, async () => {
                await submitPrompt(text);
            }))
    );
}

/** Palette commands that map one-to-one onto a CLI slash command. */
export const DIRECT_SLASH_COMMANDS: ReadonlyArray<readonly [commandId: string, slashCommand: string]> = [
    ['stemcode.undo', '/undo'],
    ['stemcode.redo', '/redo'],
    ['stemcode.compact', '/compact'],
    ['stemcode.fork', '/fork'],
    ['stemcode.clone', '/clone'],
    ['stemcode.sessionTree', '/tree'],
    ['stemcode.resumeSession', '/resume'],
    ['stemcode.exportSession', '/export'],
    ['stemcode.exportTrajectory', '/trajectory'],
    ['stemcode.importSession', '/import'],
    ['stemcode.shareSession', '/share'],
    ['stemcode.selectModel', '/models'],
    ['stemcode.selectProvider', '/provider'],
    ['stemcode.onboard', '/onboard'],
    ['stemcode.initWorkspace', '/init'],
    ['stemcode.doctor', '/doctor'],
    ['stemcode.sessionInfo', '/session'],
    ['stemcode.permissions', '/permissions'],
    ['stemcode.mcpStatus', '/mcp'],
    ['stemcode.indexStatus', '/index status'],
    ['stemcode.indexRebuild', '/index rebuild'],
    ['stemcode.lspStatus', '/lsp'],
    ['stemcode.reload', '/reload'],
    ['stemcode.checkForUpdates', '/update'],
    ['stemcode.version', '/version'],
    ['stemcode.copyLastMessage', '/copy']
];

async function pickSlashCommand(sessionManager: SessionManager): Promise<ChatCommandSuggestion | undefined> {
    const commands = buildChatCommands(sessionManager.getAvailableCommands());
    const picked = await vscode.window.showQuickPick(
        commands.map((command) => ({
            label: command.command,
            description: command.usage !== command.command ? command.usage.slice(command.command.length).trim() : '',
            detail: command.description,
            command
        })),
        {
            title: 'StemCode: Run Slash Command',
            placeHolder: 'Type to filter StemCode CLI commands',
            matchOnDescription: true,
            matchOnDetail: true
        });

    return picked?.command;
}

function getSelectedText(): EditorText | undefined {
    const editor = vscode.window.activeTextEditor;
    if (!editor || editor.selection.isEmpty) {
        return undefined;
    }

    return {
        fileName: editor.document.fileName,
        languageId: editor.document.languageId,
        text: editor.document.getText(editor.selection)
    };
}

function getCurrentEditorText(): EditorText | undefined {
    const editor = vscode.window.activeTextEditor;
    if (!editor) {
        return undefined;
    }

    return {
        fileName: editor.document.fileName,
        languageId: editor.document.languageId,
        text: editor.document.getText()
    };
}

function formatEditorPrompt(instruction: string, editorText: EditorText): string {
    return `${instruction}\n\nFile: ${editorText.fileName}\n\n\`\`\`${editorText.languageId}\n${editorText.text}\n\`\`\``;
}

function readGitDiff(): Promise<string> {
    const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
    if (!workspaceFolder) {
        return Promise.reject(new Error('Open a workspace before asking StemCode to review a git diff.'));
    }

    return new Promise((resolve, reject) => {
        execFile(
            'git',
            ['diff', '--no-ext-diff', 'HEAD'],
            {
                cwd: workspaceFolder.uri.fsPath,
                maxBuffer: 1024 * 1024 * 20
            },
            (error, stdout, stderr) => {
                if (error && !stdout) {
                    reject(new Error(stderr.trim() || error.message));
                    return;
                }

                resolve(stdout);
            }
        );
    });
}

type EditorText = {
    fileName: string;
    languageId: string;
    text: string;
};
