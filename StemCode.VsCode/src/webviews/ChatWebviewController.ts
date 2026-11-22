import * as path from 'path';
import * as vscode from 'vscode';
import { LogService } from '../services/LogService';
import {
    ClientRequest,
    ClientRequestResolution,
    FileEditsSummary,
    PermissionClientRequest,
    PlanUpdate,
    PromptState,
    SessionInfo,
    SessionManager,
    SessionMessageChunk,
    SessionSummaryInfo,
    ToolCallUpdate,
    TurnMetrics,
    AvailableCommand
} from '../services/SessionManager';
import { StemCodeProcessStatus } from '../services/StemCodeProcessManager';
import { getChatWebviewContent, getNonce } from './ChatWebviewContent';
import type { ChatMessage, EditorContextInfo } from './chatMessages';
import { CapturedEditorContext, formatEditorContextLabel, formatPromptWithEditorContext } from './editorContext';
import { parseExportedFile } from '../webview/chatCommands';

/** VS Code commands the webview is allowed to trigger directly (starter buttons). */
const ALLOWED_WEBVIEW_COMMANDS = new Set(['stemcode.reviewGitDiff']);

export class ChatWebviewController {
    private readonly disposables: vscode.Disposable[] = [];
    private readonly localRequestResolvers = new Map<string, (resolution: ClientRequestResolution) => void>();
    private readonly toolCalls = new Map<string, ToolCallUpdate>();
    private currentPlan: PlanUpdate | null = null;
    private currentFileEditsSummary: FileEditsSummary | null = null;
    private currentSessionInfo: SessionInfo | null;
    private localRequestCounter = 0;
    private bootstrapTimer: NodeJS.Timeout | null = null;
    private webviewReady = false;
    private bootstrapAttempt = 0;
    private editorContextTimer: NodeJS.Timeout | null = null;
    /** Text of the most recent agent message, for /copy. */
    private lastAssistantText = '';
    /** Active captures of agent output, used to read slash-command results such as export paths. */
    private readonly outputCaptures = new Set<{ text: string }>();

    constructor(
        private readonly webview: vscode.Webview,
        private readonly sessionManager: SessionManager,
        private readonly extensionUri: vscode.Uri
    ) {
        this.currentSessionInfo = this.sessionManager.getSessionInfo();
        this.webview.options = {
            enableScripts: true,
            localResourceRoots: [
                vscode.Uri.joinPath(extensionUri, 'media'),
                vscode.Uri.joinPath(extensionUri, 'dist'),
                vscode.Uri.joinPath(extensionUri, 'resources')
            ]
        };
        LogService.getInstance().info('Chat webview controller created');

        this.registerSessionListeners();
        this.registerEditorListeners();

        this.disposables.push(
            this.webview.onDidReceiveMessage(async (message: ChatMessage) => {
                LogService.getInstance().info(`Chat webview message received: ${message.command}`);
                if (message.command === 'sendMessage') {
                    await this.handleUserMessage(message.text, message.editorContext);
                } else if (message.command === 'newChat') {
                    await this.startNewChatSession();
                } else if (message.command === 'restartProcess') {
                    await this.restartProcess();
                } else if (message.command === 'openLogs') {
                    LogService.getInstance().show();
                } else if (message.command === 'insertCode') {
                    await this.insertCodeIntoEditor(message.text);
                } else if (message.command === 'openInEditor') {
                    await this.openCodeInEditor(message.text, message.language);
                } else if (message.command === 'runVsCodeCommand') {
                    if (ALLOWED_WEBVIEW_COMMANDS.has(message.id)) {
                        await vscode.commands.executeCommand(message.id);
                    }
                } else if (message.command === 'runSessionCommand') {
                    await this.runSettingsCommand(message.text);
                } else if (message.command === 'selectModel') {
                    await this.handleModelSelection();
                } else if (message.command === 'changeModel') {
                    await this.handleModelChange(message.modelId);
                } else if (message.command === 'changeProfile') {
                    await this.handleProfileChange(message.profileName);
                } else if (message.command === 'openVsCodeSettings') {
                    await vscode.commands.executeCommand('workbench.action.openSettings', 'stemcode');
                } else if (message.command === 'resolveClientRequest') {
                    this.resolveClientRequest(message.requestId, message.resolution);
                } else if (message.command === 'cancelPrompt') {
                    this.sessionManager.cancelPrompt();
                } else if (message.command === 'openFile') {
                    await this.openFileFromChat(message.filePath, message.line, message.column);
                } else if (message.command === 'searchFiles') {
                    await this.searchFileMentions(message.query);
                } else if (message.command === 'listSessions') {
                    await this.handleListSessions();
                } else if (message.command === 'resumeSession') {
                    await this.resumeChatSession(message.sessionId);
                } else if (message.command === 'loadPlugins') {
                    await this.postPluginState();
                } else if (message.command === 'pluginAction') {
                    await this.handlePluginAction(message.text);
                } else if (message.command === 'webviewLog') {
                    const details = typeof message.details === 'string' ? message.details : undefined;
                    const formatted = details ? `${message.message}\n${details}` : message.message;
                    if (message.level === 'error') {
                        LogService.getInstance().error(`Chat webview: ${formatted}`);
                    } else if (message.level === 'warn') {
                        LogService.getInstance().warn(`Chat webview: ${formatted}`);
                    } else {
                        LogService.getInstance().info(`Chat webview: ${formatted}`);
                    }
                } else if (message.command === 'ready') {
                    this.webviewReady = true;
                    LogService.getInstance().info('Chat webview reported ready');
                    this.postActiveEditor();
                    await this.postInitialState();
                    await this.ensureSessionReady();
                }
            })
        );

        const nonce = getNonce();
        this.webview.html = getChatWebviewContent(this.webview, this.extensionUri, nonce);
        this.scheduleBootstrap();
    }

    public dispose() {
        if (this.bootstrapTimer) {
            clearTimeout(this.bootstrapTimer);
            this.bootstrapTimer = null;
        }

        if (this.editorContextTimer) {
            clearTimeout(this.editorContextTimer);
            this.editorContextTimer = null;
        }

        for (const resolve of this.localRequestResolvers.values()) {
            resolve({ outcome: 'cancelled' });
        }

        this.localRequestResolvers.clear();

        while (this.disposables.length) {
            this.disposables.pop()?.dispose();
        }
    }

    public async submitMessage(text: string) {
        const trimmedText = text.trim();
        if (trimmedText) {
            await this.handleUserMessage(trimmedText);
        }
    }

    public async startNewSession() {
        await this.startNewChatSession();
    }

    public prefillMessage(text: string) {
        this.webview.postMessage({ command: 'prefillComposer', text });
    }

    public showSettings() {
        this.webview.postMessage({ command: 'showSettings' });
    }

    public focusInput() {
        this.webview.postMessage({ command: 'focusInput' });
    }

    private registerEditorListeners() {
        const schedule = () => {
            if (this.editorContextTimer) {
                clearTimeout(this.editorContextTimer);
            }
            this.editorContextTimer = setTimeout(() => {
                this.editorContextTimer = null;
                this.postActiveEditor();
            }, 150);
        };

        this.disposables.push(
            vscode.window.onDidChangeActiveTextEditor(schedule),
            vscode.window.onDidChangeTextEditorSelection(event => {
                if (event.textEditor === vscode.window.activeTextEditor) {
                    schedule();
                }
            })
        );
    }

    private getContextEditor(): vscode.TextEditor | undefined {
        const active = vscode.window.activeTextEditor;
        if (active && isContextEditor(active)) {
            return active;
        }
        return vscode.window.visibleTextEditors.find(isContextEditor);
    }

    private postActiveEditor() {
        const editor = this.getContextEditor();
        let info: EditorContextInfo | null = null;
        if (editor) {
            const selection = editor.selection;
            info = {
                relativePath: toRelativePath(editor.document.uri),
                languageId: editor.document.languageId,
                selection: selection.isEmpty
                    ? null
                    : { startLine: selection.start.line + 1, endLine: getSelectionEndLine(selection) }
            };
        }
        void this.postToWebview({ command: 'activeEditor', editor: info }, 'active editor');
    }

    /** Re-reads the editor at send time so the prompt reflects the current selection, not a stale snapshot. */
    private captureEditorContext(requested: EditorContextInfo | undefined): CapturedEditorContext | undefined {
        if (!requested) {
            return undefined;
        }

        const editor = this.getContextEditor();
        if (!editor) {
            return undefined;
        }

        const relativePath = toRelativePath(editor.document.uri);
        const selection = editor.selection;
        if (selection.isEmpty) {
            return { relativePath, languageId: editor.document.languageId };
        }

        return {
            relativePath,
            languageId: editor.document.languageId,
            startLine: selection.start.line + 1,
            endLine: getSelectionEndLine(selection),
            selectionText: editor.document.getText(selection)
        };
    }

    private async restartProcess() {
        try {
            await this.sessionManager.restart();
            await this.ensureSessionReady();
        } catch (error) {
            const message = error instanceof Error ? error.message : 'Unable to restart StemCode.';
            LogService.getInstance().error('Restart failed', error);
            this.postSystemMessage(`Error: ${message}`);
        }
    }

    private async insertCodeIntoEditor(text: string) {
        const editor = this.getContextEditor();
        if (!editor) {
            await this.openCodeInEditor(text);
            return;
        }

        const applied = await editor.edit(builder => {
            for (const selection of editor.selections) {
                builder.replace(selection, text);
            }
        });
        if (applied) {
            await vscode.window.showTextDocument(editor.document, { viewColumn: editor.viewColumn, preserveFocus: false });
        } else {
            void this.postToWebview({ command: 'toast', text: 'Could not insert into the active editor.' }, 'toast');
        }
    }

    private async openCodeInEditor(text: string, language?: string) {
        const document = await vscode.workspace.openTextDocument({
            content: text,
            language: language && /^[a-z0-9#+_.-]{1,40}$/i.test(language) ? mapLanguageId(language) : undefined
        });
        await vscode.window.showTextDocument(document, { preview: false, viewColumn: vscode.ViewColumn.One });
    }

    private registerSessionListeners() {
        const processStatusListener = (status: StemCodeProcessStatus) => {
            this.postProcessStatus(status);
        };
        const promptStateListener = (state: PromptState) => {
            this.postPromptState(state);
        };
        const sessionInfoListener = (sessionInfo: SessionInfo | null) => {
            this.currentSessionInfo = sessionInfo;
            this.postSessionInfo(sessionInfo);
        };
        const messageChunkListener = (chunk: SessionMessageChunk) => {
            if (chunk.role === 'assistant') {
                for (const capture of this.outputCaptures) {
                    capture.text += chunk.text;
                }
            }

            this.webview.postMessage({ command: 'appendMessageChunk', chunk });
        };
        const toolCallListener = (update: ToolCallUpdate) => {
            const merged = this.mergeToolCall(update);
            this.webview.postMessage({ command: 'setToolCall', toolCall: merged });
        };
        const planListener = (plan: PlanUpdate) => {
            this.currentPlan = plan;
            this.webview.postMessage({ command: 'setPlan', plan });
        };
        const fileEditsSummaryListener = (summary: FileEditsSummary) => {
            this.currentFileEditsSummary = summary;
            this.webview.postMessage({ command: 'setFileEditsSummary', summary });
        };
        const clientRequestListener = (request: ClientRequest) => {
            this.postClientRequest(request);
        };
        const clientRequestResolvedListener = (requestId: string) => {
            this.webview.postMessage({ command: 'resolveClientRequest', requestId });
        };
        const availableCommandsListener = (commands: AvailableCommand[]) => {
            this.webview.postMessage({ command: 'setAvailableCommands', commands });
        };
        const sessionHistoryResetListener = () => {
            // The CLI switched sections (/fork, /clone, /import, /resume, /tree) and is
            // about to replay the new section's history.
            this.clearSessionActivity();
            this.lastAssistantText = '';
            this.webview.postMessage({ command: 'clearMessages' });
        };

        this.sessionManager.on('processStatusChanged', processStatusListener);
        this.sessionManager.on('promptStateChanged', promptStateListener);
        this.sessionManager.on('sessionInfoChanged', sessionInfoListener);
        this.sessionManager.on('messageChunk', messageChunkListener);
        this.sessionManager.on('toolCallUpdated', toolCallListener);
        this.sessionManager.on('planUpdated', planListener);
        this.sessionManager.on('fileEditsSummaryChanged', fileEditsSummaryListener);
        this.sessionManager.on('clientRequest', clientRequestListener);
        this.sessionManager.on('clientRequestResolved', clientRequestResolvedListener);
        this.sessionManager.on('availableCommandsChanged', availableCommandsListener);
        this.sessionManager.on('sessionHistoryReset', sessionHistoryResetListener);

        this.disposables.push(
            new vscode.Disposable(() => this.sessionManager.off('availableCommandsChanged', availableCommandsListener)),
            new vscode.Disposable(() => this.sessionManager.off('sessionHistoryReset', sessionHistoryResetListener)),
            new vscode.Disposable(() => this.sessionManager.off('processStatusChanged', processStatusListener)),
            new vscode.Disposable(() => this.sessionManager.off('promptStateChanged', promptStateListener)),
            new vscode.Disposable(() => this.sessionManager.off('sessionInfoChanged', sessionInfoListener)),
            new vscode.Disposable(() => this.sessionManager.off('messageChunk', messageChunkListener)),
            new vscode.Disposable(() => this.sessionManager.off('toolCallUpdated', toolCallListener)),
            new vscode.Disposable(() => this.sessionManager.off('planUpdated', planListener)),
            new vscode.Disposable(() => this.sessionManager.off('fileEditsSummaryChanged', fileEditsSummaryListener)),
            new vscode.Disposable(() => this.sessionManager.off('clientRequest', clientRequestListener)),
            new vscode.Disposable(() => this.sessionManager.off('clientRequestResolved', clientRequestResolvedListener))
        );
    }

    private postInitialState() {
        LogService.getInstance().debug('Posting initial state to chat webview', {
            processStatus: this.sessionManager.getProcessStatus(),
            hasSessionInfo: this.currentSessionInfo !== null,
            pendingClientRequestCount: this.sessionManager.getPendingClientRequests().length
        });
        void this.postProcessStatus(this.sessionManager.getProcessStatus());
        void this.postPromptState(this.sessionManager.getPromptState());
        void this.postSessionInfo(this.currentSessionInfo);

        const commands = this.sessionManager.getAvailableCommands();
        if (commands.length > 0) {
            void this.postToWebview({ command: 'setAvailableCommands', commands }, 'available commands');
        }

        if (this.currentPlan) {
            void this.postToWebview({ command: 'setPlan', plan: this.currentPlan }, 'plan');
        }

        const summary = this.currentFileEditsSummary ?? this.sessionManager.getFileEditsSummary();
        if (summary) {
            this.currentFileEditsSummary = summary;
            void this.postToWebview({ command: 'setFileEditsSummary', summary }, 'file edits summary');
        }

        for (const toolCall of this.toolCalls.values()) {
            void this.postToWebview({ command: 'setToolCall', toolCall }, `tool call '${toolCall.toolCallId}'`);
        }

        for (const request of this.sessionManager.getPendingClientRequests()) {
            this.postClientRequest(request);
        }
    }

    private scheduleBootstrap() {
        if (this.bootstrapTimer) {
            clearTimeout(this.bootstrapTimer);
        }

        this.bootstrapTimer = setTimeout(() => {
            this.bootstrapTimer = null;
            void this.bootstrapWebview();
        }, 150);
    }

    private async bootstrapWebview() {
        try {
            this.bootstrapAttempt += 1;
            LogService.getInstance().info(`Bootstrapping chat webview (attempt ${this.bootstrapAttempt})`);
            this.postInitialState();
            await this.ensureSessionReady();
            this.postInitialState();

            if (!this.webviewReady && this.bootstrapAttempt < 5) {
                this.bootstrapTimer = setTimeout(() => {
                    this.bootstrapTimer = null;
                    void this.bootstrapWebview();
                }, this.bootstrapAttempt * 500);
            }
        } catch (error) {
            const message = error instanceof Error ? error.message : 'Unable to initialize StemCode session.';
            LogService.getInstance().error('Deferred chat webview bootstrap failed', error);
            this.postSystemMessage(`Error: ${message}`);
        }
    }

    private async handleUserMessage(text: string, editorContext?: EditorContextInfo) {
        const trimmedText = text.trim();
        if (!trimmedText) {
            return;
        }

        if (trimmedText === '/clear') {
            this.webview.postMessage({ command: 'clearMessages' });
            this.postSystemMessage('Screen cleared.');
            return;
        }

        if (trimmedText === '/models') {
            await this.handleModelSelection();
            return;
        }

        if (trimmedText === '/new') {
            await this.startNewChatSession();
            return;
        }

        if (trimmedText.startsWith('/resume ')) {
            await this.resumeChatSession(trimmedText.slice('/resume '.length).trim());
            return;
        }

        const context = trimmedText.startsWith('/') ? undefined : this.captureEditorContext(editorContext);
        this.postChatMessage(trimmedText, 'user', context ? [formatEditorContextLabel(context)] : undefined);

        if (trimmedText === '/ls') {
            await this.listWorkspaceFiles();
            return;
        }

        if (trimmedText.startsWith('/read ')) {
            await this.readWorkspaceFile(trimmedText.slice('/read '.length).trim());
            return;
        }

        if (await this.tryHandleLocalCommand(trimmedText)) {
            return;
        }

        if (/^\/export(\s|$)/i.test(trimmedText)) {
            await this.runExportCommand(trimmedText, false);
            return;
        }

        const isSlashCommand = trimmedText.startsWith('/');
        // Agent replies (not command output) are remembered for /copy.
        const reply = { text: '' };
        if (!isSlashCommand) {
            this.outputCaptures.add(reply);
        }

        try {
            const result = await this.sessionManager.sendPrompt(formatPromptWithEditorContext(trimmedText, context));
            if (!isSlashCommand) {
                this.postTurnMetrics(result?.metrics);
            }
        } catch (error) {
            const message = error instanceof Error ? error.message : 'Unknown error';
            LogService.getInstance().error('Chat request failed', error);
            this.postSystemMessage(`Error: ${message}`);
        } finally {
            this.outputCaptures.delete(reply);
            if (reply.text.trim()) {
                this.lastAssistantText = reply.text;
            }
        }
    }

    private postTurnMetrics(metrics: TurnMetrics | undefined) {
        if (!metrics) {
            return;
        }

        const parts: string[] = [];
        if (typeof metrics.elapsedMilliseconds === 'number' && metrics.elapsedMilliseconds > 0) {
            const seconds = metrics.elapsedMilliseconds / 1000;
            parts.push(seconds >= 10 ? `${Math.round(seconds)}s` : `${seconds.toFixed(1)}s`);
        }

        const outputTokens = metrics.displayedEstimatedOutputTokens ?? metrics.estimatedOutputTokens;
        if (typeof outputTokens === 'number' && outputTokens > 0) {
            parts.push(`${formatTokenCount(outputTokens)} tokens`);
        }

        if (typeof metrics.toolRoundCount === 'number' && metrics.toolRoundCount > 0) {
            parts.push(`${metrics.toolRoundCount} tool ${metrics.toolRoundCount === 1 ? 'round' : 'rounds'}`);
        }

        if (typeof metrics.providerRetryCount === 'number' && metrics.providerRetryCount > 0) {
            parts.push(`${metrics.providerRetryCount} retries`);
        }

        if (parts.length === 0) {
            return;
        }

        this.postChatMessage(parts.join(' / '), 'metrics');
    }

    /**
     * Commands the terminal CLI handles in its own UI rather than in the backend.
     * The extension maps them to VS Code equivalents. Returns true when handled.
     */
    private async tryHandleLocalCommand(trimmedText: string): Promise<boolean> {
        const normalized = trimmedText.replace(/\s+/g, ' ').toLowerCase();
        switch (normalized) {
            case '/voice':
                await this.executeVsCodeCommand('stemcode.voiceDictation');
                return true;
            case '/voice setup':
                await this.executeVsCodeCommand('stemcode.voiceSetup');
                return true;
            case '/voice update':
                await this.executeVsCodeCommand('stemcode.voiceUpdate');
                return true;
            case '/trajectory':
            case '/traj':
                await this.runExportCommand('/export trajectory', true);
                return true;
            case '/copy':
                await this.copyLastAgentMessage();
                return true;
            case '/exit':
                this.postSystemMessage('Stopping StemCode. Send a message or run "StemCode: Start" to start it again.');
                await this.executeVsCodeCommand('stemcode.stop');
                return true;
            default:
                return false;
        }
    }

    private async executeVsCodeCommand(commandId: string) {
        try {
            await vscode.commands.executeCommand(commandId);
        } catch (error) {
            const message = error instanceof Error ? error.message : 'Unknown error';
            LogService.getInstance().error(`Command '${commandId}' failed`, error);
            this.postSystemMessage(`Error: ${message}`);
        }
    }

    private async copyLastAgentMessage() {
        const text = this.lastAssistantText.trim();
        if (!text) {
            this.postSystemMessage('There is no agent message to copy yet.');
            return;
        }

        await vscode.env.clipboard.writeText(text);
        this.postSystemMessage('Copied the last agent message to the clipboard.');
    }

    /**
     * Runs /export through the CLI, then opens the exported file: HTML in the browser,
     * JSON in an editor. When openAutomatically is false the user is offered a button.
     */
    private async runExportCommand(commandText: string, openAutomatically: boolean) {
        const output = await this.runSessionCommandCapturingOutput(commandText, 'Export failed');
        const exported = parseExportedFile(output);
        if (!exported) {
            return;
        }

        const uri = vscode.Uri.file(exported.filePath);
        const isHtml = /\.html?$/i.test(exported.filePath);
        if (!openAutomatically) {
            const choice = await vscode.window.showInformationMessage(
                `Exported StemCode session as ${exported.format}.`,
                isHtml ? 'Open in Browser' : 'Open',
                'Reveal File');
            if (choice === 'Reveal File') {
                await vscode.commands.executeCommand('revealFileInOS', uri);
                return;
            }

            if (!choice) {
                return;
            }
        }

        if (isHtml) {
            await vscode.env.openExternal(uri);
        } else {
            await vscode.window.showTextDocument(uri, { preview: false });
        }
    }

    private async runSessionCommandCapturingOutput(commandText: string, logContext: string): Promise<string> {
        const capture = { text: '' };
        this.outputCaptures.add(capture);
        try {
            await this.sessionManager.sendPrompt(commandText);
        } catch (error) {
            const message = error instanceof Error ? error.message : 'Unknown error';
            LogService.getInstance().error(logContext, error);
            this.postSystemMessage(`Error: ${message}`);
        } finally {
            this.outputCaptures.delete(capture);
        }

        return capture.text;
    }

    private async handleModelSelection() {
        await this.runSessionCommand('/models', 'Model selection failed');
    }

    private async runSettingsCommand(text: string) {
        const trimmedText = text.trim();
        if (!trimmedText.startsWith('/')) {
            return;
        }

        await this.runSessionCommand(trimmedText, 'Settings command failed');
    }

    private async ensureSessionReady() {
        try {
            await this.sessionManager.ensureSessionReady();
            this.currentSessionInfo = this.sessionManager.getSessionInfo();
            this.postInitialState();
        } catch (error) {
            const message = error instanceof Error ? error.message : 'Unable to initialize StemCode session.';
            LogService.getInstance().error('Session initialization failed', error);
            this.postSystemMessage(`Error: ${message}`);
        }
    }

    private async handleModelChange(modelId: string) {
        const trimmedModelId = modelId.trim();
        const sessionInfo = this.currentSessionInfo ?? this.sessionManager.getSessionInfo();
        const models = sessionInfo?.availableModelIds ?? [];
        if (!trimmedModelId || !models.includes(trimmedModelId) || trimmedModelId === sessionInfo?.modelId) {
            return;
        }

        try {
            await this.sessionManager.sendPrompt(`/use ${trimmedModelId}`);
        } catch (error) {
            const message = error instanceof Error ? error.message : 'Unknown error';
            LogService.getInstance().error('Model change failed', error);
            this.postSystemMessage(`Error: ${message}`);
        }
    }

    private async handleProfileChange(profileName: string) {
        const trimmedProfileName = profileName.trim();
        const sessionInfo = this.currentSessionInfo ?? this.sessionManager.getSessionInfo();
        if (!trimmedProfileName ||
            (sessionInfo?.agentProfileName &&
                trimmedProfileName.toLowerCase() === sessionInfo.agentProfileName.toLowerCase())) {
            return;
        }

        const profiles = sessionInfo?.availableAgentProfiles ?? [];
        if (profiles.length > 0 &&
            !profiles.some(profile => profile.name.toLowerCase() === trimmedProfileName.toLowerCase())) {
            return;
        }

        await this.runSessionCommand(`/profile ${trimmedProfileName}`, 'Profile change failed');
    }

    private async runSessionCommand(commandText: string, logContext: string) {
        try {
            await this.sessionManager.sendPrompt(commandText);
        } catch (error) {
            const message = error instanceof Error ? error.message : 'Unknown error';
            LogService.getInstance().error(logContext, error);
            this.postSystemMessage(`Error: ${message}`);
        }
    }

    private async listWorkspaceFiles() {
        const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
        if (!workspaceFolder) {
            this.postSystemMessage('Open a workspace before using /ls.');
            return;
        }

        try {
            const files = await vscode.workspace.findFiles(
                new vscode.RelativePattern(workspaceFolder, '**/*'),
                '{**/.git/**,**/node_modules/**,**/bin/**,**/obj/**}',
                100
            );

            if (files.length === 0) {
                this.postSystemMessage('No files found.');
                return;
            }

            const relativeFiles = files
                .map((file) => path.relative(workspaceFolder.uri.fsPath, file.fsPath))
                .sort((left, right) => left.localeCompare(right));

            this.postSystemMessage('Files:\n\n' + relativeFiles.join('\n'));
        } catch (error) {
            const message = error instanceof Error ? error.message : 'Unable to list files.';
            this.postSystemMessage(`Error listing files: ${message}`);
        }
    }

    private async searchFileMentions(query: string) {
        const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
        if (!workspaceFolder) {
            this.webview.postMessage({ command: 'fileMentions', query, files: [] });
            return;
        }

        try {
            //  re-scans (capped) per query. Cache the file list if it lags on huge repos.
            const files = await vscode.workspace.findFiles(
                new vscode.RelativePattern(workspaceFolder, '**/*'),
                '{**/.git/**,**/node_modules/**,**/bin/**,**/obj/**,**/dist/**,**/out/**}',
                2000
            );

            const normalizedQuery = query.trim().toLowerCase();
            const relativeFiles = files
                .map((file) => path.relative(workspaceFolder.uri.fsPath, file.fsPath).replace(/\\/g, '/'))
                .filter((file) => !normalizedQuery || file.toLowerCase().includes(normalizedQuery))
                .sort((left, right) => left.localeCompare(right))
                .slice(0, 20);

            this.webview.postMessage({ command: 'fileMentions', query, files: relativeFiles });
        } catch (error) {
            LogService.getInstance().error('File mention search failed', error);
            this.webview.postMessage({ command: 'fileMentions', query, files: [] });
        }
    }

    private async handleListSessions() {
        try {
            const sessions: SessionSummaryInfo[] = await this.sessionManager.listSessions();
            this.webview.postMessage({
                command: 'sessionList',
                sessions,
                currentSessionId: this.currentSessionInfo?.sessionId ?? this.sessionManager.getSessionInfo()?.sessionId ?? ''
            });
        } catch (error) {
            const message = error instanceof Error ? error.message : 'Unable to list sessions.';
            LogService.getInstance().error('List sessions failed', error);
            this.postSystemMessage(`Error: ${message}`);
        }
    }

    private async handlePluginAction(text: string) {
        const trimmedText = text.trim();
        if (!trimmedText.startsWith('/skill')) {
            return;
        }

        // Runs the same /skill command the CLI uses, then re-reads state from disk.
        await this.runSessionCommand(trimmedText, 'Skill command failed');
        await this.postPluginState();
    }

    private async postPluginState() {
        const root = this.getOpenFileRoots()[0];
        const marketplaces: unknown = await this.readPluginJson(root, 'marketplaces.json');
        const installed: unknown = await this.readPluginJson(root, 'installed.json');

        const marketplaceMap = (marketplaces as { marketplaces?: Record<string, { type?: string; repository?: string; ref?: string }> })?.marketplaces ?? {};
        const installedMap = (installed as { plugins?: Record<string, { marketplaceAlias?: string; repository?: string; ref?: string; files?: string[] }> })?.plugins ?? {};

        this.webview.postMessage({
            command: 'pluginState',
            marketplaces: Object.entries(marketplaceMap).map(([alias, entry]) => ({
                alias,
                type: entry?.type ?? 'github',
                repository: entry?.repository ?? '',
                ref: entry?.ref ?? 'main'
            })),
            installed: Object.entries(installedMap).map(([pluginId, entry]) => ({
                pluginId,
                marketplaceAlias: entry?.marketplaceAlias ?? '',
                repository: entry?.repository ?? '',
                ref: entry?.ref ?? 'main',
                files: Array.isArray(entry?.files) ? entry.files : []
            }))
        });
    }

    private async readPluginJson(root: string | undefined, fileName: string): Promise<unknown> {
        if (!root) {
            return null;
        }

        try {
            const uri = vscode.Uri.file(path.join(root, '.stemcode', 'skills', fileName));
            const bytes = await vscode.workspace.fs.readFile(uri);
            return JSON.parse(new TextDecoder().decode(bytes));
        } catch {
            return null; // missing or invalid file -> empty state
        }
    }

    private async startNewChatSession() {
        this.clearSessionActivity();

        try {
            const sessionId = await this.sessionManager.startNewSession();
            this.currentSessionInfo = this.sessionManager.getSessionInfo();
            this.webview.postMessage({ command: 'clearMessages' });
            this.postSystemMessage(`Started new StemCode session: ${sessionId}`);
            this.postInitialState();
        } catch (error) {
            const message = error instanceof Error ? error.message : 'Unable to start a new StemCode session.';
            LogService.getInstance().error('New session failed', error);
            this.postSystemMessage(`Error: ${message}`);
        }
    }

    private async resumeChatSession(sessionId: string) {
        if (!sessionId) {
            this.postSystemMessage('Usage: /resume <session-id>');
            return;
        }

        this.clearSessionActivity();

        try {
            await this.sessionManager.loadSession(sessionId);
            this.currentSessionInfo = this.sessionManager.getSessionInfo();
            this.webview.postMessage({ command: 'clearMessages' });
            this.postSystemMessage(`Resumed StemCode session: ${sessionId}`);
            this.postInitialState();
        } catch (error) {
            const message = error instanceof Error ? error.message : 'Unable to resume StemCode session.';
            LogService.getInstance().error('Resume session failed', error);
            this.postSystemMessage(`Error: ${message}`);
        }
    }

    private clearSessionActivity() {
        this.currentPlan = null;
        this.currentFileEditsSummary = null;
        this.toolCalls.clear();
        this.webview.postMessage({ command: 'setPlan', plan: null });
        this.webview.postMessage({ command: 'clearToolCalls' });
        this.webview.postMessage({ command: 'setFileEditsSummary', summary: null });
    }

    private async readWorkspaceFile(requestedPath: string) {
        if (!requestedPath) {
            this.postSystemMessage('Usage: /read <file>');
            return;
        }

        const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
        if (!workspaceFolder) {
            this.postSystemMessage('Open a workspace before using /read.');
            return;
        }

        try {
            const root = workspaceFolder.uri.fsPath;
            const fullPath = path.resolve(root, requestedPath);
            const relativePath = path.relative(root, fullPath);
            if (relativePath.startsWith('..') || path.isAbsolute(relativePath)) {
                this.postSystemMessage('Path escapes workspace.');
                return;
            }

            const permissionRequest: PermissionClientRequest = {
                id: this.createLocalRequestId('read'),
                kind: 'permission',
                title: 'Allow local file read?',
                description: `Read file '${relativePath}'?`,
                options: [
                    { optionId: 'allow', name: 'Allow', kind: 'allow_once' },
                    { optionId: 'deny', name: 'Deny', kind: 'reject_once' }
                ],
                allowCancellation: true
            };
            const resolution = await this.requestFromWebview(permissionRequest);

            if (resolution.outcome !== 'selected' || resolution.optionId !== 'allow') {
                this.postSystemMessage(`Permission denied. Did not read file: ${relativePath}`);
                return;
            }

            const content = new TextDecoder().decode(
                await vscode.workspace.fs.readFile(vscode.Uri.file(fullPath))
            );

            this.postSystemMessage(`Permission granted.\n\nFile: ${relativePath}\n\n${content}`);
        } catch (error) {
            const message = error instanceof Error ? error.message : 'Unable to read file.';
            this.postSystemMessage(`Error reading file: ${message}`);
        }
    }

    private async openFileFromChat(filePath: string, line?: number, column?: number) {
        const parsed = this.parseOpenFileRequest(filePath, line, column);
        if (!parsed.filePath) {
            return;
        }

        const candidates = this.resolveOpenFileCandidates(parsed.filePath);
        for (const candidate of candidates) {
            const uri = vscode.Uri.file(candidate);
            try {
                const stat = await vscode.workspace.fs.stat(uri);
                if (stat.type === vscode.FileType.Directory) {
                    continue;
                }

                const document = await vscode.workspace.openTextDocument(uri);
                const position = typeof parsed.line === 'number'
                    ? new vscode.Position(
                        Math.max(0, parsed.line - 1),
                        Math.max(0, (parsed.column ?? 1) - 1))
                    : undefined;
                await vscode.window.showTextDocument(document, {
                    preview: true,
                    selection: position
                        ? new vscode.Range(position, position)
                        : undefined
                });
                return;
            } catch {
                continue;
            }
        }

        vscode.window.showWarningMessage(`Could not open '${parsed.filePath}' from the current workspace.`);
    }

    private parseOpenFileRequest(
        filePath: string,
        line?: number,
        column?: number
    ): { filePath: string; line?: number; column?: number } {
        let normalizedPath = String(filePath || '')
            .trim()
            .replace(/^[`"'(<[]+/, '')
            .replace(/[>`'"),.;\]]+$/, '');

        const location = /^(.*?)(?::(\d{1,7})(?::(\d{1,5}))?)$/.exec(normalizedPath);
        if (location && location[1]) {
            normalizedPath = location[1];
            line ??= Number.parseInt(location[2], 10);
            if (location[3]) {
                column ??= Number.parseInt(location[3], 10);
            }
        }

        return {
            filePath: normalizedPath,
            line: this.normalizePositiveInteger(line),
            column: this.normalizePositiveInteger(column)
        };
    }

    private resolveOpenFileCandidates(filePath: string): string[] {
        const roots = this.getOpenFileRoots();
        const relativeCandidates = this.createRelativeOpenFileCandidates(filePath);
        const resolvedCandidates: string[] = [];

        for (const candidate of relativeCandidates) {
            if (path.isAbsolute(candidate)) {
                this.pushOpenFileCandidate(resolvedCandidates, candidate, roots);
                continue;
            }

            for (const root of roots) {
                this.pushOpenFileCandidate(resolvedCandidates, path.resolve(root, candidate), roots);
            }
        }

        return resolvedCandidates;
    }

    private createRelativeOpenFileCandidates(filePath: string): string[] {
        const normalizedPath = filePath.replace(/\\/g, path.sep).replace(/\//g, path.sep);
        const candidates = [normalizedPath];
        const diffPrefixMatch = /^(?:a|b)[\\/](.+)$/.exec(filePath);
        if (diffPrefixMatch?.[1]) {
            candidates.push(diffPrefixMatch[1].replace(/\\/g, path.sep).replace(/\//g, path.sep));
        }

        return Array.from(new Set(candidates));
    }

    private getOpenFileRoots(): string[] {
        const roots = [
            ...(vscode.workspace.workspaceFolders?.map(folder => folder.uri.fsPath) ?? []),
            this.sessionManager.getWorkingDirectory()
        ]
            .filter((root): root is string => typeof root === 'string' && root.trim().length > 0)
            .map(root => path.resolve(root));

        return Array.from(new Set(roots));
    }

    private pushOpenFileCandidate(candidates: string[], candidate: string, roots: string[]) {
        const resolved = path.resolve(candidate);
        if (!roots.some(root => this.isPathInsideRoot(resolved, root))) {
            return;
        }

        if (!candidates.includes(resolved)) {
            candidates.push(resolved);
        }
    }

    private isPathInsideRoot(candidate: string, root: string): boolean {
        const relative = path.relative(root, candidate);
        return relative === '' || (!!relative && !relative.startsWith('..') && !path.isAbsolute(relative));
    }

    private normalizePositiveInteger(value: number | undefined): number | undefined {
        return typeof value === 'number' && Number.isFinite(value) && value > 0
            ? Math.floor(value)
            : undefined;
    }

    private requestFromWebview(request: ClientRequest): Promise<ClientRequestResolution> {
        this.postClientRequest(request);
        return new Promise((resolve) => {
            this.localRequestResolvers.set(request.id, resolve);
        });
    }

    private resolveClientRequest(requestId: string, resolution: ClientRequestResolution) {
        const localResolve = this.localRequestResolvers.get(requestId);
        if (localResolve) {
            this.localRequestResolvers.delete(requestId);
            localResolve(resolution);
            this.webview.postMessage({ command: 'resolveClientRequest', requestId });
            return;
        }

        this.sessionManager.resolveClientRequest(requestId, resolution);
    }

    private mergeToolCall(update: ToolCallUpdate): ToolCallUpdate {
        const current = this.toolCalls.get(update.toolCallId);
        const merged: ToolCallUpdate = {
            ...current,
            ...update,
            rawInput: update.rawInput ?? current?.rawInput,
            content: update.content && update.content.length > 0
                ? update.content
                : current?.content
        };
        this.toolCalls.set(update.toolCallId, merged);
        return merged;
    }

    private createLocalRequestId(kind: string): string {
        this.localRequestCounter += 1;
        return `local-${kind}-${this.localRequestCounter}`;
    }

    private postClientRequest(request: ClientRequest) {
        void this.postToWebview({ command: 'showClientRequest', request }, 'client request');
    }

    private async postProcessStatus(status: StemCodeProcessStatus) {
        await this.postToWebview({ command: 'setProcessStatus', status }, `process status '${status}'`);
    }

    private async postPromptState(promptState: PromptState) {
        await this.postToWebview({ command: 'setPromptState', promptState }, 'prompt state');
    }

    private postChatMessage(
        text: string,
        role: 'assistant' | 'system' | 'user' | 'metrics',
        attachments?: string[]
    ) {
        void this.postToWebview({ command: 'appendMessage', text, role, attachments }, `chat message '${role}'`);
    }

    private postSystemMessage(text: string) {
        this.postChatMessage(text, 'system');
    }

    private async postSessionInfo(sessionInfo: SessionInfo | null) {
        await this.postToWebview({
            command: 'setSessionInfo',
            sessionInfo,
            workingDirectory: this.sessionManager.getWorkingDirectory()
        }, 'session info');
    }

    private async postToWebview(message: unknown, purpose: string): Promise<void> {
        try {
            const delivered = await this.webview.postMessage(message);
            LogService.getInstance().debug(`Posted ${purpose} to chat webview`, { delivered });
        } catch (error) {
            LogService.getInstance().error(`Failed to post ${purpose} to chat webview`, error);
        }
    }
}

function formatTokenCount(value: number): string {
    if (value >= 1000) {
        return `${(value / 1000).toFixed(value >= 10000 ? 0 : 1)}k`;
    }
    return String(Math.round(value));
}

function isContextEditor(editor: vscode.TextEditor): boolean {
    const scheme = editor.document.uri.scheme;
    return scheme === 'file' || scheme === 'vscode-remote';
}

function toRelativePath(uri: vscode.Uri): string {
    return vscode.workspace.asRelativePath(uri, false).replace(/\\/g, '/');
}

/** A selection ending at column 0 of a later line (full-line select) shouldn't count that line. */
function getSelectionEndLine(selection: vscode.Selection): number {
    return selection.end.character === 0 && selection.end.line > selection.start.line
        ? selection.end.line
        : selection.end.line + 1;
}

function mapLanguageId(language: string): string {
    const aliases: Record<string, string> = { shell: 'shellscript', text: 'plaintext' };
    return aliases[language.toLowerCase()] ?? language.toLowerCase();
}
