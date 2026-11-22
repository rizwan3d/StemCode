// Mocha entry point executed inside the VS Code extension host.
import * as path from 'path';
import * as fs from 'fs';
import Mocha = require('mocha');

export function run(): Promise<void> {
    const mocha = new Mocha({ ui: 'bdd', color: true, timeout: 30000 });
    const testsRoot = __dirname;
    for (const file of fs.readdirSync(testsRoot)) {
        if (file.endsWith('.itest.js')) {
            mocha.addFile(path.join(testsRoot, file));
        }
    }

    return new Promise((resolve, reject) => {
        mocha.run(failures => {
            if (failures > 0) {
                reject(new Error(`${failures} integration test(s) failed.`));
            } else {
                resolve();
            }
        });
    });
}
