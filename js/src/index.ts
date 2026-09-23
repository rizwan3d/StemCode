import { EventEmitter } from "events";
import fs from "fs";
import path from "path";

export type StemCodeProvider =
  | "anthropic"
  | "openai"
  | "google-ai-studio"
  | "openrouter"
  | "deepseek"
  | "groq"
  | "cerebras"
  | "ollama"
  | "lm-studio"
  | "openai-compatible";

export interface StemCodeClientOptions {
  provider: StemCodeProvider;
  apiKey?: string;
  model?: string;
  baseUrl?: string;
  workspace?: string;
  profile?: string;
  useBuildTool?: boolean;
  thinkingMode?: string;
  sectionId?: string;
  systemPrompt?: string;
  useStemCodeSystemPrompt?: boolean;
  autoApproveTools?: boolean;
  enableProductTelemetry?: boolean;
  enableOpenTelemetryTracing?: boolean;
  runtimePath?: string;
}

export interface StemCodeSession {
  sessionId: string;
  providerName: string;
  modelId: string;
  agentProfileName: string;
  thinkingMode: string;
  reasoningEffort?: string | null;
  showThinking: boolean;
  sectionTitle: string;
  isResumedSection: boolean;
  availableModelIds: string[];
  conversationHistory: StemCodeConversationMessage[];
}

export interface StemCodeConversationMessage {
  role: string;
  content: string;
  reasoningContent?: string | null;
  reasoningDetailsJson?: string | null;
}

export interface StemCodeTurnResult {
  kind: string;
  responseText: string;
  reasoningText?: string | null;
  toolExecutionResult?: StemCodeToolExecutionBatchResult | null;
}

export interface StemCodeCommandResult {
  command: {
    exitRequested: boolean;
    message?: string | null;
    feedbackKind: string;
    replaySession: boolean;
  };
  session: StemCodeSession;
}

export interface StemCodeToolCall {
  id: string;
  name: string;
  argumentsJson: string;
}

export interface StemCodeToolInvocationResult {
  toolCallId: string;
  toolName: string;
  toolNameRecognized: boolean;
  status: string;
  isSuccess: boolean;
  message: string;
  jsonResult: string;
}

export interface StemCodeToolExecutionBatchResult {
  hasFailures: boolean;
  displayText: string;
  results: StemCodeToolInvocationResult[];
}

export interface StemCodeEvents {
  reasoning: [{ reasoningText: string }];
  assistantMessageChunk: [{ text: string }];
  toolCallsStarted: [{ toolCalls: StemCodeToolCall[] }];
  toolResults: [StemCodeToolExecutionBatchResult];
  executionPlanUpdated: [{ displayText: string }];
  providerRetry: [{ displayText: string }];
  statusMessage: [{ severity: string; message: string }];
}

type EdgeCallback = (error: Error | null, result?: unknown) => void;
type EdgeFunction = (input: unknown, callback: EdgeCallback) => void;
type EdgeModule = {
  func(options: { assemblyFile: string; typeName: string; methodName: string }): EdgeFunction;
};

let bridgeFunction: EdgeFunction | undefined;

export class StemCodeClient extends EventEmitter {
  private clientId: string | undefined;
  private readonly options: StemCodeClientOptions;

  public constructor(options: StemCodeClientOptions) {
    super();
    this.options = { ...options };
  }

  public static create(options: StemCodeClientOptions): StemCodeClient {
    return new StemCodeClient(options);
  }

  public override on<EventName extends keyof StemCodeEvents>(
    eventName: EventName,
    listener: (...args: StemCodeEvents[EventName]) => void,
  ): this {
    return super.on(eventName, listener);
  }

  public override once<EventName extends keyof StemCodeEvents>(
    eventName: EventName,
    listener: (...args: StemCodeEvents[EventName]) => void,
  ): this {
    return super.once(eventName, listener);
  }

  public async initialize(): Promise<StemCodeSession> {
    await this.ensureCreated();
    return this.invoke<StemCodeSession>({ operation: "initialize", clientId: this.clientId });
  }

  public async runTurn(prompt: string): Promise<StemCodeTurnResult> {
    await this.ensureCreated();
    return this.invoke<StemCodeTurnResult>({
      operation: "runTurn",
      clientId: this.clientId,
      prompt,
    });
  }

  public async runCommand(commandText: string): Promise<StemCodeCommandResult> {
    await this.ensureCreated();
    return this.invoke<StemCodeCommandResult>({
      operation: "runCommand",
      clientId: this.clientId,
      commandText,
    });
  }

  public async dispose(): Promise<void> {
    if (!this.clientId) {
      return;
    }

    const clientId = this.clientId;
    this.clientId = undefined;
    await this.invoke({ operation: "dispose", clientId });
  }

  private async ensureCreated(): Promise<void> {
    if (this.clientId) {
      return;
    }

    const result = await this.invoke<{ clientId: string }>({
      operation: "create",
      options: normalizeOptions(this.options),
      eventSink: (event: { event: keyof StemCodeEvents; payload: unknown }, callback: EdgeCallback) => {
        try {
          this.emit(event.event, event.payload);
          callback(null, null);
        } catch (error) {
          callback(error instanceof Error ? error : new Error(String(error)));
        }
      },
    });

    this.clientId = result.clientId;
  }

  private invoke<TResult = unknown>(input: unknown): Promise<TResult> {
    const bridge = getBridgeFunction(this.options.runtimePath);
    return new Promise<TResult>((resolve, reject) => {
      bridge(input, (error, result) => {
        if (error) {
          reject(error);
          return;
        }

        resolve(result as TResult);
      });
    });
  }
}

export async function createStemCodeClient(options: StemCodeClientOptions): Promise<StemCodeClient> {
  const client = new StemCodeClient(options);
  await client.initialize();
  return client;
}

function normalizeOptions(options: StemCodeClientOptions): Omit<StemCodeClientOptions, "runtimePath"> {
  const { runtimePath: _runtimePath, ...runtimeOptions } = options;
  return runtimeOptions;
}

function getBridgeFunction(runtimePath?: string): EdgeFunction {
  if (bridgeFunction) {
    return bridgeFunction;
  }

  const runtimeDirectory = resolveRuntimeDirectory(runtimePath);
  process.env.EDGE_USE_CORECLR = process.env.EDGE_USE_CORECLR || "1";
  process.env.EDGE_APP_ROOT = process.env.EDGE_APP_ROOT || runtimeDirectory;

  const edge = require("edge-js") as EdgeModule;
  bridgeFunction = edge.func({
    assemblyFile: path.join(runtimeDirectory, "StemCode.dll"),
    typeName: "StemCode.Sdk.Js.StemCodeEdgeBridge",
    methodName: "Invoke",
  });

  return bridgeFunction;
}

function resolveRuntimeDirectory(runtimePath?: string): string {
  const candidate = runtimePath || process.env.STEMCODE_DOTNET_PATH || path.join(__dirname, "..", "dotnet");
  const resolved = path.resolve(candidate);
  const stats = fs.existsSync(resolved) ? fs.statSync(resolved) : undefined;
  const runtimeDirectory = stats?.isFile() ? path.dirname(resolved) : resolved;
  const assemblyPath = path.join(runtimeDirectory, "StemCode.dll");

  if (!fs.existsSync(assemblyPath)) {
    throw new Error(
      `StemCode.dll was not found at ${assemblyPath}. Run "npm run build:dotnet" from the js package, ` +
        "install a packaged build, or set STEMCODE_DOTNET_PATH to a published StemCode SDK directory.",
    );
  }

  return runtimeDirectory;
}

export default StemCodeClient;
