import { EventEmitter } from 'events';
import * as path from 'path';
import * as vscode from 'vscode';
import { AcpClient } from './AcpClient';
import { LogService } from './LogService';
import { StemCodeProcessManager, StemCodeProcessStatus } from './StemCodeProcessManager';
import { AcpNotification, AcpRequest } from '../types/acp';
import type {
    SessionPromptResult,
    AgentProfileInfo,
    SessionInfo,
    SessionSummaryInfo,
    PromptState,
    SessionMessageChunk,
    ToolCallUpdate,
    PlanEntry,
    PlanUpdate,
    FileEditSummaryItem,
    FileEditsSummary,
    ClientRequest,
    PermissionClientRequest,
    TextClientRequest,
    PermissionOption,
    ClientRequestResolution
} from './sessionTypes';

export * from './sessionTypes';

type SessionNewResult = {
    sessionId: string;
};

type InitializeResult = {
    authMethods?: unknown;
};

type PendingClientRequest = {
    request: ClientRequest;
    resolve: (resolution: ClientRequestResolution) => void;
    reject: (error: Error) => void;
};

export class SessionManager extends EventEmitter {
    private static readonly acpAuthSecretStorageKey = 'stemcode.acpAuthToken';
    private static readonly acpAuthEnvVarName = 'STEMCODE_ACP_AUTH_TOKEN';

    private acpClient: AcpClient | null = null;
    private currentSessionInfo: SessionInfo | null = null;
    private currentFileEditsSummary: FileEditsSummary | null = null;
    private currentPromptState: PromptState = {
        isRunning: false,
        isCancelling: false
    };
    private initializePromise: Promise<void> | null = null;
    private pendingClientRequests = new Map<string, PendingClientRequest>();
    private promptTail: Promise<void> = Promise.resolve();
    private sessionId: string | null = null;
    private sessionPromise: Promise<string> | null = null;
    private readonly logService: LogService;
    private readonly secretStorage: vscode.SecretStorage;

    constructor(
        private readonly processManager: StemCodeProcessManager,
        secretStorage: vscode.SecretStorage) {
        super();
        this.logService = LogService.getInstance();
        this.secretStorage = secretStorage;

        this.processManager.on('status', (status: StemCodeProcessStatus) => {
            this.emit('processStatusChanged', status);
            if (status === 'running') {
                this.initializeSession();
            } else if (status === 'stopped' || status === 'error') {
                this.terminateSession();
            }
        });

        if (this.processManager.getProcess()) {
            this.initializeSession();
        }
    }

    public async ensureStarted(): Promise<void> {
        if (!this.processManager.getProcess()) {
            await this.processManager.start();
        }

        if (!this.acpClient) {
            this.initializeSession();
        }

        if (!this.acpClient) {
            throw new Error('StemCode ACP server did not expose stdio.');
        }
    }

    /** Restarts the CLI process; the 'running' status transition re-initializes the ACP session. */
    public async restart(): Promise<void> {
        if (this.processManager.getProcess()) {
            await this.processManager.restart();
        } else {
            await this.processManager.start();
        }

        if (!this.acpClient && this.processManager.getProcess()) {
            this.initializeSession();
        }
    }

    public async ensureSessionReady(): Promise<void> {
        const client = await this.getReadyClient();
        await this.ensureSession(client);
    }

    public async startNewSession(): Promise<string> {
        const client = await this.getReadyClient();
        await this.closeCurrentSession(client);
        return this.createSession(client);
    }

    public async loadSession(sessionId: string): Promise<void> {
        const normalizedSessionId = sessionId.trim();
        if (!normalizedSessionId) {
            throw new Error('Session id is required.');
        }

        const client = await this.getReadyClient();
        await this.closeCurrentSession(client);

        this.sessionPromise = client
            .sendRequest<void>('session/load', {
                cwd: this.getWorkingDirectory(),
                sessionId: normalizedSessionId
            })
            .then(() => {
                this.sessionId = normalizedSessionId;
                return normalizedSessionId;
            })
            .catch((error) => {
                this.sessionPromise = null;
                throw this.normalizeError(error);
            });

        await this.sessionPromise;
    }

    public async listSessions(): Promise<SessionSummaryInfo[]> {
        const client = await this.getReadyClient();
        const result = await client.sendRequest<{ sessions?: unknown[] }>('session/list', {});
        const sessions = Array.isArray(result?.sessions) ? result.sessions : [];
        return sessions
            .map((entry) => this.readSessionSummary(entry))
            .filter((summary): summary is SessionSummaryInfo => summary !== null);
    }

    private readSessionSummary(value: unknown): SessionSummaryInfo | null {
        if (!value || typeof value !== 'object') {
            return null;
        }

        const record = value as Record<string, unknown>;
        const sessionId = this.optionalString(record.sessionId);
        if (!sessionId) {
            return null;
        }

        return {
            sessionId,
            title: this.optionalString(record.title) ?? 'Untitled session',
            updatedAtUtc: this.optionalString(record.updatedAtUtc),
            modelId: this.optionalString(record.modelId),
            profileName: this.optionalString(record.profileName),
            turnCount: this.optionalNonNegativeNumber(record.turnCount),
            parentSessionId: this.optionalString(record.parentSessionId)
        };
    }

    public async sendPrompt(text: string): Promise<SessionPromptResult> {
        const runPrompt = this.promptTail.then(
            () => this.sendPromptCore(text),
            () => this.sendPromptCore(text)
        );
        this.promptTail = runPrompt.then(
            () => undefined,
            () => undefined
        );

        return runPrompt;
    }

    public cancelPrompt(): void {
        const client = this.acpClient;
        const sessionId = this.sessionId ?? this.currentSessionInfo?.sessionId;
        if (!client || !sessionId) {
            return;
        }

        if (this.currentPromptState.isRunning) {
            this.setPromptState({
                ...this.currentPromptState,
                isCancelling: true
            });
        }

        client.sendNotification('session/cancel', { sessionId });
    }

    public getSessionInfo(): SessionInfo | null {
        return this.currentSessionInfo;
    }

    public getProcessStatus(): StemCodeProcessStatus {
        return this.processManager.getStatus();
    }

    public getPromptState(): PromptState {
        return this.currentPromptState;
    }

    public getPendingClientRequests(): ClientRequest[] {
        return Array.from(this.pendingClientRequests.values(), pending => pending.request);
    }

    public getWorkingDirectory(): string {
        const config = vscode.workspace.getConfiguration('stemcode');
        const configuredDirectory = config.get<string>('workingDirectory');
        if (configuredDirectory && configuredDirectory.trim()) {
            const trimmedDirectory = configuredDirectory.trim();
            if (path.isAbsolute(trimmedDirectory)) {
                return trimmedDirectory;
            }

            const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
            if (workspaceFolder) {
                return path.resolve(workspaceFolder.uri.fsPath, trimmedDirectory);
            }

            return path.resolve(trimmedDirectory);
        }

        const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
        if (workspaceFolder) {
            return workspaceFolder.uri.fsPath;
        }

        return process.cwd();
    }

    public resolveClientRequest(id: string, resolution: ClientRequestResolution): boolean {
        const pending = this.pendingClientRequests.get(id);
        if (!pending) {
            return false;
        }

        pending.resolve(resolution);
        this.pendingClientRequests.delete(id);
        this.emit('clientRequestResolved', id);
        return true;
    }

    private async sendPromptCore(text: string): Promise<SessionPromptResult> {
        const client = await this.getReadyClient();
        const sessionId = await this.ensureSession(client);

        this.setPromptState({
            isRunning: true,
            isCancelling: false,
            input: text
        });

        try {
            const result = await client.sendRequest<SessionPromptResult>('session/prompt', {
                sessionId,
                prompt: [
                    {
                        type: 'text',
                        text
                    }
                ]
            });

            this.setPromptState({
                isRunning: false,
                isCancelling: false,
                lastStopReason: result?.stopReason
            });

            return result ?? {};
        } catch (error) {
            const normalized = this.normalizeError(error);
            this.setPromptState({
                isRunning: false,
                isCancelling: false,
                error: normalized.message
            });
            throw normalized;
        }
    }

    private initializeSession() {
        if (this.acpClient) {
            return;
        }

        const process = this.processManager.getProcess();
        if (!process || !process.stdout || !process.stdin) {
            this.logService.error('Cannot initialize session: Process or stdio is not available');
            return;
        }

        this.acpClient = new AcpClient();

        process.stdout.on('data', (data) => {
            if (this.acpClient) {
                this.acpClient.handleData(data);
            }
        });

        this.acpClient.on('send', (data: string) => {
            if (process && process.stdin) {
                process.stdin.write(data);
            }
        });

        this.acpClient.on('notification', (msg: AcpNotification) => this.handleNotification(msg));
        this.acpClient.on('request', (msg: AcpRequest) => {
            void this.handleClientRequest(msg);
        });

        this.logService.info('ACP Session initialized');
    }

    private terminateSession() {
        if (this.acpClient) {
            this.acpClient.rejectAllPendingRequests();
            this.acpClient.removeAllListeners();
            this.acpClient = null;
            this.currentSessionInfo = null;
            this.currentFileEditsSummary = null;
            this.initializePromise = null;
            this.promptTail = Promise.resolve();
            this.sessionId = null;
            this.sessionPromise = null;
            this.rejectPendingClientRequests(new Error('StemCode process stopped.'));
            this.setPromptState({
                isRunning: false,
                isCancelling: false
            });
            this.emit('sessionInfoChanged', null);
            this.logService.info('ACP Session terminated');
        }
    }

    private async getReadyClient(): Promise<AcpClient> {
        await this.ensureStarted();

        const client = this.acpClient;
        if (!client) {
            throw new Error('StemCode ACP client is not initialized.');
        }

        await this.ensureInitialized(client);
        return client;
    }

    private async ensureInitialized(client: AcpClient): Promise<void> {
        if (!this.initializePromise) {
            this.initializePromise = client
                .sendRequest<InitializeResult>('initialize', { protocolVersion: 1 })
                .then((result) => this.authenticateIfRequired(client, result))
                .then(() => undefined)
                .catch((error) => {
                    this.initializePromise = null;
                    throw this.normalizeError(error);
                });
        }

        await this.initializePromise;
    }

    private async authenticateIfRequired(client: AcpClient, result: InitializeResult | undefined): Promise<void> {
        const authMethods = Array.isArray(result?.authMethods)
            ? result.authMethods.filter((method): method is string => typeof method === 'string')
            : [];

        if (!authMethods.includes('token')) {
            return;
        }

        const token = await this.resolveAcpAuthenticationToken();
        if (!token) {
            throw new Error(
                'StemCode ACP authentication is enabled, but no token was found in VS Code SecretStorage, the ' +
                '`stemcode.acpAuthenticationToken` setting, or the `STEMCODE_ACP_AUTH_TOKEN` environment variable.');
        }

        await client.sendRequest('authenticate', { token });
    }

    private async resolveAcpAuthenticationToken(): Promise<string | undefined> {
        const secretToken = this.normalizeToken(
            await this.secretStorage.get(SessionManager.acpAuthSecretStorageKey));
        if (secretToken) {
            return secretToken;
        }

        const configuration = vscode.workspace.getConfiguration('stemcode');
        const configuredToken = this.normalizeToken(
            configuration.get<string>('acpAuthenticationToken'));
        if (configuredToken) {
            return configuredToken;
        }

        return this.normalizeToken(process.env[SessionManager.acpAuthEnvVarName]);
    }

    private normalizeToken(value: string | undefined): string | undefined {
        return typeof value === 'string' && value.trim()
            ? value.trim()
            : undefined;
    }

    private async ensureSession(client: AcpClient): Promise<string> {
        if (this.sessionId) {
            return this.sessionId;
        }

        return this.createSession(client);
    }

    private createSession(client: AcpClient): Promise<string> {
        if (!this.sessionPromise) {
            this.sessionPromise = client
                .sendRequest<SessionNewResult>('session/new', {
                    cwd: this.getWorkingDirectory()
                })
                .then((result) => {
                    if (!result || typeof result.sessionId !== 'string' || !result.sessionId.trim()) {
                        throw new Error('StemCode ACP server returned an invalid session.');
                    }

                    this.sessionId = result.sessionId;
                    return this.sessionId;
                })
                .catch((error) => {
                    this.sessionPromise = null;
                    throw this.normalizeError(error);
                });
        }

        return this.sessionPromise;
    }

    private async closeCurrentSession(client: AcpClient): Promise<void> {
        const sessionId = this.sessionId;
        this.sessionId = null;
        this.sessionPromise = null;
        this.currentSessionInfo = null;
        this.currentFileEditsSummary = null;
        this.currentPromptState = {
            isRunning: false,
            isCancelling: false
        };
        this.emit('sessionInfoChanged', null);
        this.emit('promptStateChanged', this.currentPromptState);
        this.rejectPendingClientRequests(new Error('StemCode session closed.'));

        if (!sessionId) {
            return;
        }

        try {
            await client.sendRequest('session/close', { sessionId });
        } catch (error) {
            this.logService.warn('Failed to close StemCode ACP session', error);
        }
    }

    private async handleClientRequest(message: AcpRequest): Promise<void> {
        const client = this.acpClient;
        if (!client) {
            return;
        }

        switch (message.method) {
            case 'session/request_permission':
                await this.handlePermissionRequest(client, message);
                return;

            case 'session/request_text':
                await this.handleTextRequest(client, message);
                return;

            default:
                client.sendError(message.id, -32601, `Method '${message.method}' is not supported.`);
        }
    }

    private handleNotification(message: AcpNotification): void {
        this.logService.debug('SessionManager received notification', message);

        const sessionInfo = this.tryReadSessionInfo(message);
        if (sessionInfo) {
            this.currentSessionInfo = sessionInfo;
            this.emit('sessionInfoChanged', sessionInfo);
        }

        const chunk = this.tryReadMessageChunk(message);
        if (chunk) {
            this.emit('messageChunk', chunk);
        }

        const toolCall = this.tryReadToolCallUpdate(message);
        if (toolCall) {
            this.emit('toolCallUpdated', toolCall);
        }

        const plan = this.tryReadPlanUpdate(message);
        if (plan) {
            this.emit('planUpdated', plan);
        }

        const fileEdits = this.tryReadFileEditsSummary(message);
        if (fileEdits) {
            this.currentFileEditsSummary = fileEdits;
            this.emit('fileEditsSummaryChanged', fileEdits);
        }
    }

    public getFileEditsSummary(): FileEditsSummary | null {
        return this.currentFileEditsSummary;
    }

    private tryReadFileEditsSummary(message: AcpNotification): FileEditsSummary | null {
        const sessionUpdate = this.tryReadSessionUpdate(message);
        if (!sessionUpdate ||
            this.optionalString(sessionUpdate.update.sessionUpdate) !== 'file_edits_summary' ||
            !Array.isArray(sessionUpdate.update.files)) {
            return null;
        }

        const files = sessionUpdate.update.files
            .map((file) => this.readFileEditSummaryItem(file))
            .filter((file): file is FileEditSummaryItem => file !== null);

        return { sessionId: sessionUpdate.sessionId, files };
    }

    private readFileEditSummaryItem(value: unknown): FileEditSummaryItem | null {
        if (!value || typeof value !== 'object') {
            return null;
        }

        const item = value as {
            displayPath?: unknown;
            absolutePath?: unknown;
            addedLineCount?: unknown;
            removedLineCount?: unknown;
            editCount?: unknown;
            action?: unknown;
        };
        const displayPath = this.optionalString(item.displayPath);
        if (!displayPath) {
            return null;
        }

        return {
            displayPath,
            absolutePath: this.optionalString(item.absolutePath) ?? displayPath,
            addedLineCount: this.optionalNonNegativeNumber(item.addedLineCount) ?? 0,
            removedLineCount: this.optionalNonNegativeNumber(item.removedLineCount) ?? 0,
            editCount: this.optionalNonNegativeNumber(item.editCount) ?? 0,
            action: this.optionalString(item.action) ?? 'Edited'
        };
    }

    private async handlePermissionRequest(client: AcpClient, message: AcpRequest): Promise<void> {
        const request = this.readPermissionRequest(message);
        try {
            const resolution = await this.requestClientInput(request);
            client.sendResponse(message.id, {
                outcome: resolution.outcome === 'selected'
                    ? {
                        outcome: 'selected',
                        optionId: resolution.optionId
                    }
                    : {
                        outcome: 'cancelled'
                    }
            });
        } catch (error) {
            const normalized = this.normalizeError(error);
            this.logService.error('Permission request failed', normalized);
            client.sendResponse(message.id, {
                outcome: {
                    outcome: 'cancelled'
                }
            });
        }
    }

    private async handleTextRequest(client: AcpClient, message: AcpRequest): Promise<void> {
        const request = this.readTextRequest(message);
        try {
            const resolution = await this.requestClientInput(request);
            client.sendResponse(message.id, {
                outcome: resolution.outcome === 'submitted'
                    ? {
                        outcome: 'submitted',
                        value: resolution.value
                    }
                    : {
                        outcome: 'cancelled'
                    }
            });
        } catch (error) {
            const normalized = this.normalizeError(error);
            this.logService.error('Text request failed', normalized);
            client.sendResponse(message.id, {
                outcome: {
                    outcome: 'cancelled'
                }
            });
        }
    }

    private requestClientInput(request: ClientRequest): Promise<ClientRequestResolution> {
        return new Promise((resolve, reject) => {
            this.pendingClientRequests.set(request.id, {
                request,
                resolve,
                reject
            });
            this.emit('clientRequest', request);
        });
    }

    private rejectPendingClientRequests(error: Error): void {
        for (const [id, pending] of this.pendingClientRequests) {
            pending.reject(error);
            this.pendingClientRequests.delete(id);
            this.emit('clientRequestResolved', id);
        }
    }

    private readPermissionRequest(message: AcpRequest): PermissionClientRequest {
        const id = String(message.id);
        if (!message.params || typeof message.params !== 'object') {
            return {
                id,
                kind: 'permission',
                title: 'StemCode wants permission to continue.',
                options: [],
                allowCancellation: true
            };
        }

        const request = message.params as {
            toolCall?: {
                title?: unknown;
                content?: unknown;
            };
            options?: unknown;
            allowCancellation?: unknown;
            defaultOptionId?: unknown;
            autoSelectAfterMilliseconds?: unknown;
        };

        const title = this.optionalString(request.toolCall?.title) ?? 'StemCode wants permission to continue.';
        const description = this.readContentItems(request.toolCall?.content).join('\n').trim() || undefined;
        const options = Array.isArray(request.options)
            ? request.options
                .map((option) => this.readPermissionOption(option))
                .filter((option): option is PermissionOption => option !== null)
            : [];

        return {
            id,
            kind: 'permission',
            title,
            description,
            options,
            allowCancellation: request.allowCancellation !== false,
            defaultOptionId: this.readDefaultOptionId(request.defaultOptionId, options),
            autoSelectAfterMilliseconds: this.optionalNonNegativeNumber(request.autoSelectAfterMilliseconds)
        };
    }

    private readPermissionOption(value: unknown): PermissionOption | null {
        if (!value || typeof value !== 'object') {
            return null;
        }

        const option = value as {
            optionId?: unknown;
            name?: unknown;
            kind?: unknown;
        };

        const optionId = this.optionalString(option.optionId);
        if (!optionId) {
            return null;
        }

        return {
            optionId,
            name: this.optionalString(option.name) ?? optionId,
            kind: this.optionalString(option.kind) ?? ''
        };
    }

    private readTextRequest(message: AcpRequest): TextClientRequest {
        const id = String(message.id);
        if (!message.params || typeof message.params !== 'object') {
            return {
                id,
                kind: 'text',
                label: 'StemCode input',
                isSecret: false,
                allowCancellation: true
            };
        }

        const request = message.params as {
            label?: unknown;
            description?: unknown;
            defaultValue?: unknown;
            isSecret?: unknown;
            allowCancellation?: unknown;
        };

        return {
            id,
            kind: 'text',
            label: this.optionalString(request.label) ?? 'StemCode input',
            description: this.optionalString(request.description),
            defaultValue: typeof request.defaultValue === 'string'
                ? request.defaultValue
                : undefined,
            isSecret: request.isSecret === true,
            allowCancellation: request.allowCancellation !== false
        };
    }

    private tryReadSessionUpdate(message: AcpNotification): { sessionId: string; update: Record<string, unknown> } | null {
        if (message.method !== 'session/update' || !message.params || typeof message.params !== 'object') {
            return null;
        }

        const params = message.params as {
            sessionId?: unknown;
            update?: unknown;
        };

        if (typeof params.sessionId !== 'string' ||
            !params.update ||
            typeof params.update !== 'object' ||
            Array.isArray(params.update)) {
            return null;
        }

        return {
            sessionId: params.sessionId,
            update: params.update as Record<string, unknown>
        };
    }

    private tryReadMessageChunk(message: AcpNotification): SessionMessageChunk | null {
        const sessionUpdate = this.tryReadSessionUpdate(message);
        if (!sessionUpdate) {
            return null;
        }

        const updateKind = this.optionalString(sessionUpdate.update.sessionUpdate);
        if (!this.isMessageChunkUpdateKind(updateKind)) {
            return null;
        }

        const content = sessionUpdate.update.content;
        const text = this.readContentText(content);
        if (text === null) {
            return null;
        }

        if (this.isReasoningMessageChunk(updateKind, content)) {
            return {
                sessionId: sessionUpdate.sessionId,
                role: 'reasoning',
                text
            };
        }

        if (this.isAgentMessageChunkUpdateKind(updateKind)) {
            const reasoningText = this.readThinkingMessageText(
                text,
                this.isPromptRunningForAssistantReasoning());
            if (reasoningText !== null) {
                return {
                    sessionId: sessionUpdate.sessionId,
                    role: 'reasoning',
                    text: reasoningText
                };
            }
        }

        return {
            sessionId: sessionUpdate.sessionId,
            role: updateKind === 'user_message_chunk' ? 'user' : 'assistant',
            text
        };
    }

    private tryReadToolCallUpdate(message: AcpNotification): ToolCallUpdate | null {
        const sessionUpdate = this.tryReadSessionUpdate(message);
        if (!sessionUpdate) {
            return null;
        }

        const updateKind = this.optionalString(sessionUpdate.update.sessionUpdate);
        if (updateKind !== 'tool_call' && updateKind !== 'tool_call_update') {
            return null;
        }

        const toolCallId = this.optionalString(sessionUpdate.update.toolCallId);
        if (!toolCallId) {
            return null;
        }

        return {
            sessionId: sessionUpdate.sessionId,
            toolCallId,
            title: this.optionalString(sessionUpdate.update.title),
            kind: this.optionalString(sessionUpdate.update.kind),
            status: this.optionalString(sessionUpdate.update.status) ?? 'pending',
            rawInput: sessionUpdate.update.rawInput,
            content: this.readContentItems(sessionUpdate.update.content)
        };
    }

    private tryReadPlanUpdate(message: AcpNotification): PlanUpdate | null {
        const sessionUpdate = this.tryReadSessionUpdate(message);
        if (!sessionUpdate ||
            this.optionalString(sessionUpdate.update.sessionUpdate) !== 'plan' ||
            !Array.isArray(sessionUpdate.update.entries)) {
            return null;
        }

        const entries = sessionUpdate.update.entries
            .map((entry) => this.readPlanEntry(entry))
            .filter((entry): entry is PlanEntry => entry !== null);

        return {
            sessionId: sessionUpdate.sessionId,
            entries
        };
    }

    private readPlanEntry(value: unknown): PlanEntry | null {
        if (!value || typeof value !== 'object') {
            return null;
        }

        const entry = value as {
            content?: unknown;
            status?: unknown;
            priority?: unknown;
        };
        const content = this.optionalString(entry.content);
        if (!content) {
            return null;
        }

        return {
            content,
            status: this.optionalString(entry.status) ?? 'pending',
            priority: this.optionalString(entry.priority)
        };
    }

    private tryReadSessionInfo(message: AcpNotification): SessionInfo | null {
        const sessionUpdate = this.tryReadSessionUpdate(message);
        if (!sessionUpdate ||
            this.optionalString(sessionUpdate.update.sessionUpdate) !== 'session_info_update') {
            return null;
        }

        return {
            sessionId: sessionUpdate.sessionId,
            sectionResumeCommand: this.optionalString(sessionUpdate.update.sectionResumeCommand),
            providerName: this.optionalString(sessionUpdate.update.providerName),
            modelId: this.optionalString(sessionUpdate.update.modelId),
            availableModelIds: Array.isArray(sessionUpdate.update.availableModelIds)
                ? sessionUpdate.update.availableModelIds.filter((modelId): modelId is string => typeof modelId === 'string')
                : [],
            thinkingMode: this.optionalString(sessionUpdate.update.thinkingMode),
            reasoningEffort: this.optionalString(sessionUpdate.update.reasoningEffort),
            agentProfileName: this.optionalString(sessionUpdate.update.agentProfileName),
            availableAgentProfiles: Array.isArray(sessionUpdate.update.availableAgentProfiles)
                ? sessionUpdate.update.availableAgentProfiles
                    .map((profile) => this.readAgentProfileInfo(profile))
                    .filter((profile): profile is AgentProfileInfo => profile !== null)
                : [],
            sectionTitle: this.optionalString(sessionUpdate.update.sectionTitle),
            activeModelContextWindowTokens: this.optionalNonNegativeNumber(sessionUpdate.update.activeModelContextWindowTokens),
            sectionEstimatedContextTokens: this.optionalNonNegativeNumber(sessionUpdate.update.sectionEstimatedContextTokens),
            totalEstimatedOutputTokens: this.optionalNonNegativeNumber(sessionUpdate.update.totalEstimatedOutputTokens)
        };
    }

    private readAgentProfileInfo(value: unknown): AgentProfileInfo | null {
        if (!value || typeof value !== 'object') {
            return null;
        }

        const profile = value as {
            name?: unknown;
            mode?: unknown;
            description?: unknown;
        };
        const name = this.optionalString(profile.name);
        if (!name) {
            return null;
        }

        return {
            name,
            mode: this.optionalString(profile.mode),
            description: this.optionalString(profile.description)
        };
    }

    private readContentItems(value: unknown): string[] {
        if (!value) {
            return [];
        }

        if (Array.isArray(value)) {
            return value
                .map((item) => this.readContentText(item))
                .filter((text): text is string => text !== null);
        }

        const text = this.readContentText(value);
        return text === null ? [] : [text];
    }

    private readContentText(value: unknown): string | null {
        if (typeof value === 'string') {
            return value;
        }

        if (!value || typeof value !== 'object') {
            return null;
        }

        const content = value as Record<string, unknown>;

        if ((content.type === 'text' || this.isReasoningMarker(content.type)) &&
            typeof content.text === 'string') {
            return content.text;
        }

        if (content.type === 'content') {
            return this.readContentText(content.content);
        }

        const nestedText = this.readContentText(content.content);
        if (nestedText !== null) {
            return nestedText;
        }

        return null;
    }

    private readThinkingMessageText(text: string, allowSingleLine: boolean): string | null {
        const pattern = allowSingleLine
            ? /^\s*(?:Thinking|Reasoning):[ \t]*(?:\r?\n)*([\s\S]*)$/i
            : /^\s*(?:Thinking|Reasoning):[ \t]*(?:\r?\n)+([\s\S]*)$/i;
        const match = pattern.exec(text);
        return match && match[1].trim() ? match[1] : null;
    }

    private isPromptRunningForAssistantReasoning(): boolean {
        return this.currentPromptState.isRunning &&
            !this.isSlashCommandText(this.currentPromptState.input);
    }

    private isSlashCommandText(value: string | undefined): boolean {
        return typeof value === 'string' &&
            value.trimStart().startsWith('/');
    }

    private isMessageChunkUpdateKind(updateKind: string | undefined): updateKind is string {
        return updateKind === 'agent_message_chunk' ||
            updateKind === 'user_message_chunk' ||
            this.isReasoningMessageChunkUpdateKind(updateKind);
    }

    private isAgentMessageChunkUpdateKind(updateKind: string | undefined): boolean {
        return updateKind === 'agent_message_chunk' ||
            this.isReasoningMessageChunkUpdateKind(updateKind);
    }

    private isReasoningMessageChunkUpdateKind(updateKind: string | undefined): boolean {
        return updateKind === 'agent_reasoning_chunk' ||
            updateKind === 'reasoning_message_chunk' ||
            updateKind === 'thinking_message_chunk';
    }

    private isReasoningMessageChunk(updateKind: string, content: unknown): boolean {
        return this.isReasoningMessageChunkUpdateKind(updateKind) ||
            this.hasReasoningContentMarker(content);
    }

    private hasReasoningContentMarker(value: unknown): boolean {
        if (!value || typeof value !== 'object') {
            return false;
        }

        if (Array.isArray(value)) {
            return value.some(item => this.hasReasoningContentMarker(item));
        }

        const content = value as Record<string, unknown>;
        return this.isReasoningMarker(content.type) ||
            this.isReasoningMarker(content.role) ||
            this.isReasoningMarker(content.kind) ||
            this.isReasoningMarker(content.channel) ||
            this.hasReasoningContentMarker(content.content);
    }

    private isReasoningMarker(value: unknown): boolean {
        if (typeof value !== 'string') {
            return false;
        }

        const normalized = value.trim().toLowerCase();
        return normalized === 'reasoning' ||
            normalized === 'thinking' ||
            normalized === 'thought';
    }

    private readDefaultOptionId(
        value: unknown,
        options: ReadonlyArray<PermissionOption>): string | undefined {
        const optionId = typeof value === 'number' && Number.isFinite(value)
            ? String(Math.trunc(value))
            : this.optionalString(value);
        if (!optionId) {
            return options[0]?.optionId;
        }

        return options.some(option => option.optionId === optionId)
            ? optionId
            : options[0]?.optionId;
    }

    private optionalString(value: unknown): string | undefined {
        return typeof value === 'string' && value.trim()
            ? value.trim()
            : undefined;
    }

    private optionalNonNegativeNumber(value: unknown): number | undefined {
        return typeof value === 'number' && Number.isFinite(value) && value >= 0
            ? Math.round(value)
            : undefined;
    }

    private setPromptState(promptState: PromptState): void {
        this.currentPromptState = promptState;
        this.emit('promptStateChanged', this.currentPromptState);
    }

    private normalizeError(error: unknown): Error {
        if (error instanceof Error) {
            return error;
        }

        if (error && typeof error === 'object' && 'message' in error) {
            const message = (error as { message?: unknown }).message;
            if (typeof message === 'string' && message.trim()) {
                return new Error(message);
            }
        }

        return new Error('StemCode ACP request failed.');
    }
}
