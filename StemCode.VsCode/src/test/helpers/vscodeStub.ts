// Minimal `vscode` module stub so host-side modules with light vscode usage can load under plain mocha.
// Only the APIs touched at import time / by the code under test are provided.
import * as path from 'path';
// eslint-disable-next-line @typescript-eslint/no-var-requires
const nodeModule = require('module');

type StubUri = { fsPath: string; path: string; scheme: string; toString(): string };

function makeUri(fsPath: string): StubUri {
    const normalized = fsPath.replace(/\\/g, '/');
    return {
        fsPath,
        path: normalized,
        scheme: 'file',
        toString: () => `file://${normalized}`
    };
}

export const vscodeStub = {
    // eslint-disable-next-line @typescript-eslint/naming-convention -- mirrors the vscode API name
    Uri: {
        file: (fsPath: string) => makeUri(fsPath),
        joinPath: (base: StubUri, ...segments: string[]) => makeUri(path.join(base.fsPath, ...segments))
    }
};

let installed = false;

export function installVscodeStub() {
    if (installed) {
        return;
    }
    installed = true;
    const originalResolve = nodeModule._resolveFilename;
    nodeModule._resolveFilename = function (request: string, ...rest: unknown[]) {
        if (request === 'vscode') {
            return 'vscode';
        }
        return originalResolve.call(this, request, ...rest);
    };
    require.cache.vscode = { id: 'vscode', filename: 'vscode', loaded: true, exports: vscodeStub } as unknown as NodeJS.Module;
}

/** Fake webview: resource URIs are rewritten to a stable test scheme. */
export function createFakeWebview() {
    return {
        cspSource: 'https://test.vscode-cdn.net',
        asWebviewUri: (uri: StubUri) => ({ toString: () => `https://test.vscode-cdn.net${uri.path}` })
    };
}
