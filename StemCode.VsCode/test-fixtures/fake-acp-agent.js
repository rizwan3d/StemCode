#!/usr/bin/env node
// Minimal fake StemCode ACP agent for integration tests. Speaks line-delimited JSON-RPC on stdio,
// records every request it receives to the log file given as argv[2], and streams a short reply.
'use strict';

const fs = require('fs');
const readline = require('readline');

const logFile = process.argv[2];
const sessionId = 'fake-session-1';

function log(entry) {
    if (logFile) {
        fs.appendFileSync(logFile, JSON.stringify(entry) + '\n');
    }
}

function send(message) {
    process.stdout.write(JSON.stringify({ jsonrpc: '2.0', ...message }) + '\n');
}

function notify(update) {
    send({ method: 'session/update', params: { sessionId, update } });
}

const handlers = {
    initialize: () => ({ protocolVersion: 1, authMethods: [] }),
    'session/new': () => ({ sessionId }),
    'session/list': () => ({ sessions: [] }),
    'session/close': () => ({}),
    'session/prompt': params => {
        const text = (params.prompt || []).map(part => part.text || '').join('');
        notify({ sessionUpdate: 'agent_message_chunk', content: { type: 'text', text: 'Echo: ' } });
        notify({ sessionUpdate: 'agent_message_chunk', content: { type: 'text', text: text.slice(0, 40) } });
        return { stopReason: 'end_turn' };
    }
};

readline.createInterface({ input: process.stdin }).on('line', line => {
    if (!line.trim()) {
        return;
    }

    let message;
    try {
        message = JSON.parse(line);
    } catch {
        return;
    }

    log({ method: message.method, params: message.params });
    if (message.id === undefined || !message.method) {
        return;
    }

    const handler = handlers[message.method];
    if (!handler) {
        send({ id: message.id, error: { code: -32601, message: `Method not found: ${message.method}` } });
        return;
    }

    send({ id: message.id, result: handler(message.params || {}) });
});
