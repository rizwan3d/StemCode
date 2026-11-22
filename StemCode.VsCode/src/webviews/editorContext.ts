// Pure helpers for attaching active-editor context to a prompt (no vscode dependency, unit-testable).

export const MAX_SELECTION_LINES = 400;
export const MAX_SELECTION_CHARS = 24000;

export type CapturedEditorContext = {
    relativePath: string;
    languageId?: string;
    startLine?: number;
    endLine?: number;
    selectionText?: string;
};

/** Short label used for the attachment chip on the user's message, e.g. `src/a.ts:10-20`. */
export function formatEditorContextLabel(context: CapturedEditorContext): string {
    if (!context.startLine) {
        return context.relativePath;
    }

    const range = context.endLine && context.endLine !== context.startLine
        ? `${context.startLine}-${context.endLine}`
        : String(context.startLine);
    return `${context.relativePath}:${range}`;
}

/**
 * Appends a compact, clearly delimited context section to the prompt. Selections are inlined
 * (bounded in size); otherwise only the file reference is sent and the agent can read it itself.
 */
export function formatPromptWithEditorContext(text: string, context: CapturedEditorContext | undefined): string {
    if (!context || !context.relativePath) {
        return text;
    }

    const selection = typeof context.selectionText === 'string' ? context.selectionText : '';
    const hasSelection = selection.trim().length > 0;
    const lines = [
        '',
        '',
        '<editor-context>',
        hasSelection
            ? `The user has selected lines ${context.startLine}-${context.endLine} of \`${context.relativePath}\` in VS Code:`
            : `The user's active file in VS Code is \`${context.relativePath}\`.`
    ];

    if (hasSelection) {
        const { text: bounded, truncated } = boundSelection(selection);
        const fence = pickFence(bounded);
        lines.push(`${fence}${context.languageId ?? ''}`);
        lines.push(bounded);
        lines.push(fence);
        if (truncated) {
            lines.push(`(Selection truncated; read \`${context.relativePath}\` for the full range.)`);
        }
    }

    lines.push('</editor-context>');
    return text + lines.join('\n');
}

function boundSelection(selection: string): { text: string; truncated: boolean } {
    let text = selection.replace(/\r\n/g, '\n');
    let truncated = false;
    const lines = text.split('\n');
    if (lines.length > MAX_SELECTION_LINES) {
        text = lines.slice(0, MAX_SELECTION_LINES).join('\n');
        truncated = true;
    }
    if (text.length > MAX_SELECTION_CHARS) {
        text = text.slice(0, MAX_SELECTION_CHARS);
        truncated = true;
    }
    return { text, truncated };
}

/** Uses a backtick fence longer than any run inside the content so it can't terminate early. */
function pickFence(content: string): string {
    const longest = (content.match(/`+/g) ?? []).reduce((max, run) => Math.max(max, run.length), 0);
    return '`'.repeat(Math.max(3, longest + 1));
}
