#!/usr/bin/env node
// Minimal fake StemCode ACP agent for VsAgentService tests. Speaks line-delimited JSON-RPC on
// stdio. The prompt text picks the scenario:
//   "crash"    - exits without answering, like a CLI that dies mid-turn
//   "/resume"  - session_history_reset, then replays one user and one agent message
//   "commands" - sends available_commands_update
//   "ask"      - sends session/request_permission and answers with the chosen option
//   "stubborn" - replies with its pid and then ignores stdin closing, so it has to be killed
//   anything else - streams "Echo: <text>"
'use strict';

const readline = require('readline');

const sessionId = 'fake-session-1';
let stubborn = false;
let nextRequestId = 1000;
const waiting = new Map();

function send(message) {
    process.stdout.write(JSON.stringify({ jsonrpc: '2.0', ...message }) + '\n');
}

function notify(update) {
    send({ method: 'session/update', params: { sessionId, update } });
}

function text(value) {
    return { type: 'text', text: value };
}

const handlers = {
    initialize: () => ({ protocolVersion: 1, authMethods: [] }),
    'session/new': () => ({ sessionId }),
    'session/close': () => ({}),
    'session/prompt': async params => {
        const prompt = (params.prompt || []).map(part => part.text || '').join('');
        switch (prompt) {
            case 'crash':
                process.exit(3);
                return undefined;
            case '/resume':
                notify({ sessionUpdate: 'session_history_reset' });
                notify({ sessionUpdate: 'user_message_chunk', content: text('earlier question') });
                notify({ sessionUpdate: 'agent_message_chunk', content: text('earlier answer') });
                return { stopReason: 'end_turn' };
            case 'stubborn':
                stubborn = true;
                setInterval(() => {}, 1000);
                notify({ sessionUpdate: 'agent_message_chunk', content: text(String(process.pid)) });
                return { stopReason: 'end_turn' };
            case 'commands':
                notify({
                    sessionUpdate: 'available_commands_update',
                    availableCommands: [
                        { name: 'undo', description: 'Roll back.', usage: '/undo', custom: false },
                        { name: 'deploy', description: 'Ship it.', usage: '/deploy', custom: true, input: { hint: '<env>' } }
                    ]
                });
                return { stopReason: 'end_turn' };
            case 'ask': {
                const id = nextRequestId++;
                const answer = new Promise(resolve => waiting.set(id, resolve));
                send({
                    id,
                    method: 'session/request_permission',
                    params: { sessionId, options: [{ optionId: 'allow', name: 'Allow' }, { optionId: 'deny', name: 'Deny' }] }
                });
                const result = await answer;
                notify({ sessionUpdate: 'agent_message_chunk', content: text('chose ' + result.outcome.optionId) });
                return { stopReason: 'end_turn' };
            }
            default:
                notify({ sessionUpdate: 'agent_message_chunk', content: text('Echo: ' + prompt) });
                return { stopReason: 'end_turn', metrics: { elapsedMilliseconds: 12.5 } };
        }
    }
};

const rl = readline.createInterface({ input: process.stdin });
rl.on('close', () => { if (!stubborn) process.exit(0); });
rl.on('line', async line => {
    if (!line.trim()) {
        return;
    }

    let message;
    try {
        message = JSON.parse(line);
    } catch {
        return;
    }

    // Reply to one of our own requests (permission answers).
    if (!message.method && waiting.has(message.id)) {
        waiting.get(message.id)(message.result);
        waiting.delete(message.id);
        return;
    }

    if (message.id === undefined || !message.method) {
        return;
    }

    const handler = handlers[message.method];
    if (!handler) {
        send({ id: message.id, error: { code: -32601, message: `Method not found: ${message.method}` } });
        return;
    }

    send({ id: message.id, result: await handler(message.params || {}) });
});
