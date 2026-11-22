// Webview -> extension message protocol for the chat view.
import type { ClientRequestResolution } from '../services/sessionTypes';

export type ChatMessage =
    | SendMessage
    | RunSessionCommandMessage
    | SelectModelMessage
    | ChangeModelMessage
    | ChangeProfileMessage
    | OpenVsCodeSettingsMessage
    | ReadyMessage
    | ResolveClientRequestMessage
    | CancelPromptMessage
    | OpenFileMessage
    | SearchFilesMessage
    | ListSessionsMessage
    | ResumeSessionMessage
    | LoadPluginsMessage
    | PluginActionMessage
    | WebviewLogMessage
    | NewChatMessage
    | RestartProcessMessage
    | OpenLogsMessage
    | InsertCodeMessage
    | OpenInEditorMessage
    | RunVsCodeCommandMessage;

/** Active editor snapshot shared with the webview for the context chip. */
export type EditorContextInfo = {
    relativePath: string;
    languageId?: string;
    selection?: { startLine: number; endLine: number } | null;
};

type SendMessage = {
    command: 'sendMessage';
    text: string;
    editorContext?: EditorContextInfo;
};

type NewChatMessage = {
    command: 'newChat';
};

type RestartProcessMessage = {
    command: 'restartProcess';
};

type OpenLogsMessage = {
    command: 'openLogs';
};

type InsertCodeMessage = {
    command: 'insertCode';
    text: string;
};

type OpenInEditorMessage = {
    command: 'openInEditor';
    text: string;
    language?: string;
};

type RunVsCodeCommandMessage = {
    command: 'runVsCodeCommand';
    id: string;
};

type RunSessionCommandMessage = {
    command: 'runSessionCommand';
    text: string;
};

type SelectModelMessage = {
    command: 'selectModel';
};

type ChangeModelMessage = {
    command: 'changeModel';
    modelId: string;
};

type ChangeProfileMessage = {
    command: 'changeProfile';
    profileName: string;
};

type OpenVsCodeSettingsMessage = {
    command: 'openVsCodeSettings';
};

type ReadyMessage = {
    command: 'ready';
};

type ResolveClientRequestMessage = {
    command: 'resolveClientRequest';
    requestId: string;
    resolution: ClientRequestResolution;
};

type CancelPromptMessage = {
    command: 'cancelPrompt';
};

type OpenFileMessage = {
    command: 'openFile';
    filePath: string;
    line?: number;
    column?: number;
};

type SearchFilesMessage = {
    command: 'searchFiles';
    query: string;
};

type ListSessionsMessage = {
    command: 'listSessions';
};

type ResumeSessionMessage = {
    command: 'resumeSession';
    sessionId: string;
};

type LoadPluginsMessage = {
    command: 'loadPlugins';
};

type PluginActionMessage = {
    command: 'pluginAction';
    text: string;
};

type WebviewLogMessage = {
    command: 'webviewLog';
    level?: string;
    message: string;
    details?: string;
};
