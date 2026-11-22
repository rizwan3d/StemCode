import * as vscode from 'vscode';
import { SessionManager } from '../services/SessionManager';
import { ChatWebviewController } from './ChatWebviewController';

export class ChatViewProvider implements vscode.WebviewViewProvider {
    public static readonly viewType = 'stemcode.chatView';

    private controller: ChatWebviewController | undefined;
    private readonly resolvedEmitter = new vscode.EventEmitter<ChatWebviewController>();

    constructor(
        private readonly sessionManager: SessionManager,
        private readonly extensionUri: vscode.Uri
    ) {
    }

    public resolveWebviewView(webviewView: vscode.WebviewView) {
        this.controller?.dispose();
        this.controller = new ChatWebviewController(webviewView.webview, this.sessionManager, this.extensionUri);
        void this.sessionManager.ensureStarted().catch((error) => {
            const message = error instanceof Error ? error.message : 'Unable to start StemCode.';
            vscode.window.showErrorMessage(message);
        });

        webviewView.onDidDispose(() => {
            this.controller?.dispose();
            this.controller = undefined;
        });

        this.resolvedEmitter.fire(this.controller);
    }

    public isController(controller: ChatWebviewController): boolean {
        return this.controller === controller;
    }

    /**
     * Returns the sidebar chat controller, waiting briefly for VS Code to resolve the view
     * when it was only just revealed (e.g. right after `stemcode.openChat`).
     */
    public waitForController(timeoutMs: number): Promise<ChatWebviewController | undefined> {
        if (this.controller) {
            return Promise.resolve(this.controller);
        }

        return new Promise(resolve => {
            const timer = setTimeout(() => {
                subscription.dispose();
                resolve(this.controller);
            }, timeoutMs);
            const subscription = this.resolvedEmitter.event(controller => {
                clearTimeout(timer);
                subscription.dispose();
                resolve(controller);
            });
        });
    }

    public async submitMessage(text: string): Promise<boolean> {
        if (!this.controller) {
            return false;
        }

        await this.controller.submitMessage(text);
        return true;
    }

    public prefillMessage(text: string): boolean {
        if (!this.controller) {
            return false;
        }

        this.controller.prefillMessage(text);
        return true;
    }

    public showSettings(): boolean {
        if (!this.controller) {
            return false;
        }

        this.controller.showSettings();
        return true;
    }

    public async startNewSession(): Promise<boolean> {
        if (!this.controller) {
            return false;
        }

        await this.controller.startNewSession();
        return true;
    }
}
