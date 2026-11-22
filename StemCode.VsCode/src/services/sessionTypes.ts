// Public session domain types. Re-exported from SessionManager for back-compat.
export type TurnMetrics = {
    elapsedMilliseconds?: number;
    estimatedOutputTokens?: number;
    displayedEstimatedOutputTokens?: number;
    estimatedTotalTokens?: number;
    cachedInputTokens?: number;
    toolRoundCount?: number;
    providerRetryCount?: number;
};

export type SessionPromptResult = {
    stopReason?: string;
    metrics?: TurnMetrics;
};

export type AgentProfileInfo = {
    name: string;
    mode?: string;
    description?: string;
};

export type SessionInfo = {
    sessionId: string;
    sectionResumeCommand?: string;
    providerName?: string;
    modelId?: string;
    availableModelIds: string[];
    thinkingMode?: string;
    reasoningEffort?: string;
    agentProfileName?: string;
    availableAgentProfiles: AgentProfileInfo[];
    sectionTitle?: string;
    activeModelContextWindowTokens?: number;
    sectionEstimatedContextTokens?: number;
    totalEstimatedOutputTokens?: number;
};

export type SessionSummaryInfo = {
    sessionId: string;
    title: string;
    updatedAtUtc?: string;
    modelId?: string;
    profileName?: string;
    turnCount?: number;
    parentSessionId?: string;
};

export type PromptState = {
    isRunning: boolean;
    isCancelling: boolean;
    input?: string;
    lastStopReason?: string;
    error?: string;
};

export type SessionMessageChunk = {
    sessionId: string;
    role: 'assistant' | 'reasoning' | 'user';
    text: string;
};

export type ToolCallUpdate = {
    sessionId: string;
    toolCallId: string;
    title?: string;
    kind?: string;
    status: string;
    rawInput?: unknown;
    content?: string[];
};

export type FileEditSummaryItem = {
    displayPath: string;
    absolutePath: string;
    addedLineCount: number;
    removedLineCount: number;
    editCount: number;
    action: string;
};

export type FileEditsSummary = {
    sessionId: string;
    files: FileEditSummaryItem[];
};

export type PlanEntry = {
    content: string;
    status: string;
    priority?: string;
};

export type PlanUpdate = {
    sessionId: string;
    entries: PlanEntry[];
};

export type ClientRequest = PermissionClientRequest | TextClientRequest;

export type PermissionClientRequest = {
    id: string;
    kind: 'permission';
    title: string;
    description?: string;
    options: PermissionOption[];
    allowCancellation: boolean;
    defaultOptionId?: string;
    autoSelectAfterMilliseconds?: number;
};

export type TextClientRequest = {
    id: string;
    kind: 'text';
    label: string;
    description?: string;
    defaultValue?: string;
    isSecret: boolean;
    allowCancellation: boolean;
};

/** A slash command advertised by the CLI through ACP `available_commands_update`. */
export type { AvailableCommandEntry as AvailableCommand } from '../webview/chatCommands';

export type PermissionOption = {
    optionId: string;
    name: string;
    kind: string;
};

export type ClientRequestResolution =
    | {
        outcome: 'selected';
        optionId: string;
    }
    | {
        outcome: 'submitted';
        value: string;
    }
    | {
        outcome: 'cancelled';
    };
