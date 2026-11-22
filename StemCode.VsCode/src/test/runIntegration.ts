// Launches a real VS Code instance with the extension under development and runs the integration suite.
// Usage: npm run test:integration (downloads VS Code into .vscode-test on first run).
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { runTests } from '@vscode/test-electron';

async function main() {
    // When launched from a terminal inside VS Code this is inherited and would make Code.exe run as plain Node.
    delete process.env.ELECTRON_RUN_AS_NODE;

    const extensionDevelopmentPath = path.resolve(__dirname, '..', '..');
    const extensionTestsPath = path.resolve(__dirname, 'integration', 'index');
    const fixtureWorkspace = path.resolve(extensionDevelopmentPath, 'test-fixtures', 'workspace');
    const userDataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'stemcode-vscode-test-'));

    try {
        await runTests({
            extensionDevelopmentPath,
            extensionTestsPath,
            launchArgs: [
                fixtureWorkspace,
                '--disable-extensions',
                '--disable-workspace-trust',
                '--skip-welcome',
                '--skip-release-notes',
                `--user-data-dir=${userDataDir}`
            ]
        });
    } finally {
        fs.rmSync(userDataDir, { recursive: true, force: true });
    }
}

main().catch(error => {
    console.error('Integration tests failed', error);
    process.exit(1);
});
