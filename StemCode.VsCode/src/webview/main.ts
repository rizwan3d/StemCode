import { buildDiffModel } from './diffModel';
import { CHAT_COMMANDS } from './chatCommands';
import { ICONS, iconElement, setIconContent } from './icons';

declare function acquireVsCodeApi(): { postMessage(message: unknown): void; getState(): unknown; setState(state: unknown): void };

const api = acquireVsCodeApi();
(window as unknown as Record<string, unknown>).__stemCodeVsCodeApi = api;

window.addEventListener('error', event => {
    api.postMessage({
        command: 'webviewLog',
        level: 'error',
        message: 'Window error event',
        details: event && event.error && event.error.stack ? String(event.error.stack) : String(event && event.message ? event.message : 'Unknown error')
    });
});

window.addEventListener('unhandledrejection', event => {
    const reason = event ? event.reason : undefined;
    api.postMessage({
        command: 'webviewLog',
        level: 'error',
        message: 'Unhandled promise rejection',
        details: reason && reason.stack ? String(reason.stack) : String(reason ?? 'Unknown rejection')
    });
});

api.postMessage({ command: 'webviewLog', level: 'info', message: 'Main script starting' });
const commandSuggestions = CHAT_COMMANDS;
        const messagesDiv = document.getElementById('messages');
        const emptyState = document.getElementById('empty-state');
        const settingsPage = document.getElementById('settings-page');
        const settingsSummary = document.getElementById('settings-summary');
        const settingsCloseButton = document.getElementById('settings-close-button');
        const settingsModelValue = document.getElementById('settings-model-value');
        const settingsProfileValue = document.getElementById('settings-profile-value');
        const settingsThinkingValue = document.getElementById('settings-thinking-value');
        const settingsProviderValue = document.getElementById('settings-provider-value');
        const inputField = document.getElementById('chat-input') as HTMLTextAreaElement;
        const mentionButton = document.getElementById('mention-button');
        const newChatButton = document.getElementById('new-chat-button');
        const settingsButton = document.getElementById('settings-button');
        const sessionsButton = document.getElementById('sessions-button');
        const sendButton = document.getElementById('send-button') as HTMLButtonElement;
        const modelButton = document.getElementById('model-button') as HTMLButtonElement;
        const modelButtonLabel = document.getElementById('model-button-label');
        const agentMenu = document.getElementById('agent-menu');
        const composerArea = document.querySelector('.composer-area') as HTMLElement;
        const jumpToLatestButton = document.getElementById('jump-to-latest');
        const statusBanner = document.getElementById('status-banner');
        const statusBannerIcon = document.getElementById('status-banner-icon');
        const statusBannerText = document.getElementById('status-banner-text');
        const statusRetryButton = document.getElementById('status-retry-button');
        const statusLogsButton = document.getElementById('status-logs-button');
        const editorContextChip = document.getElementById('editor-context-chip');
        const editorContextLabel = document.getElementById('editor-context-label');
        const editorContextToggle = document.getElementById('editor-context-toggle');
        const toastDiv = document.getElementById('toast');
        const workingSet = document.getElementById('working-set');
        const workingSetToggle = document.getElementById('working-set-toggle');
        const workingSetSummary = document.getElementById('working-set-summary');
        const workingSetBody = document.getElementById('working-set-body');
        const planSection = document.getElementById('plan-section');
        const agentsSection = document.getElementById('agents-section');
        const filesSection = document.getElementById('files-section');
        const contextRing = document.getElementById('context-ring-fg');
        const contextMeterLabel = document.getElementById('context-meter-label');
        const REASONING_LEVELS = [
            { label: 'None', value: 'none' },
            { label: 'Minimal', value: 'minimal' },
            { label: 'Low', value: 'low' },
            { label: 'Medium', value: 'medium' },
            { label: 'High', value: 'high' },
            { label: 'Extra High', value: 'xhigh' },
            { label: 'Max', value: 'max' }
        ];
        const profileSelect = document.getElementById('profile-select') as HTMLSelectElement;
        const sectionTitle = document.getElementById('section-title');
        const statusText = document.getElementById('status-text');
        const contextMeter = document.getElementById('context-meter');
        const suggestionsDiv = document.getElementById('suggestions');
        const promptQueueDiv = document.getElementById('prompt-queue');
        const promptQueue = [];
        const planList = document.getElementById('plan-list');
        const planCount = document.getElementById('plan-count');
        const changedFilesList = document.getElementById('changed-files-list');
        const changedFilesCount = document.getElementById('changed-files-count');
        const agentThreadList = document.getElementById('agent-thread-list');
        const agentCount = document.getElementById('agent-count');
        const modalBackdrop = document.getElementById('modal-backdrop');
        const modalTitle = document.getElementById('modal-title');
        const modalDescription = document.getElementById('modal-description');
        const modalBody = document.getElementById('modal-body');
        const modalActions = document.getElementById('modal-actions');

        const persistedState = readPersistedState();
        let sessionInfo = null;
        let workingDirectory = '';
        let processStatus = 'stopped';
        let promptState = { isRunning: false, isCancelling: false, input: '', lastStopReason: '' };
        let visibleSuggestions = [];
        let activeSuggestionIndex = 0;
        let fileMentionQuery = null;
        let fileMentionResults = [];
        let activeAssistantMessage = null;
        let activeReasoningMessage = null;
        let activeView = 'chat';
        let activeModalRequest = null;
        let activeAutoSelectTimer = null;
        let progressIndicator = null;
        let progressTimer = null;
        let turnStartedAt = 0;
        let hasPlanActivity = false;
        let hasChangedFiles = false;
        let activeAgentThreadId = '';
        let stickToBottom = true;
        let workingSetExpanded = false;
        let activeEditor = null;
        let includeEditorContext = persistedState.includeEditorContext !== false;
        const promptHistory: string[] = Array.isArray(persistedState.promptHistory) ? persistedState.promptHistory.slice(-50) : [];
        let historyIndex = -1;
        let historyDraft = '';
        const pendingRenders = new Map();
        let renderFrame = 0;
        let toastTimer = null;
        const queuedClientRequests = [];
        const inlinePermissionCards = new Map();
        const toolCalls = new Map();
        const toolMessageElements = new Map();
        const agentThreads = new Map();
        const fallbackProfiles = [
            { name: 'build', mode: 'primary', description: 'Default coding agent profile' },
            { name: 'plan', mode: 'primary', description: 'Read-only planning profile' },
            { name: 'review', mode: 'primary', description: 'Read-only review profile' },
            { name: 'general', mode: 'subagent', description: 'Implementation-capable subagent profile' },
            { name: 'explore', mode: 'subagent', description: 'Read-only subagent profile' }
        ];

        hydrateIcons(document);

        function hydrateIcons(root) {
            root.querySelectorAll('[data-ico]').forEach(element => {
                const name = element.getAttribute('data-ico');
                if (ICONS[name] && !element.querySelector('svg')) {
                    element.insertBefore(iconElement(name), element.firstChild);
                }
            });
        }

        function readPersistedState(): { promptHistory?: string[]; includeEditorContext?: boolean } {
            try {
                const state = api.getState();
                return state && typeof state === 'object' ? state : {};
            } catch {
                return {};
            }
        }

        function persistState() {
            try {
                api.setState({ promptHistory: promptHistory.slice(-50), includeEditorContext });
            } catch {
                // Best effort only.
            }
        }

        function showToast(text) {
            toastDiv.textContent = text;
            toastDiv.classList.remove('hidden');
            if (toastTimer) {
                clearTimeout(toastTimer);
            }
            toastTimer = setTimeout(() => toastDiv.classList.add('hidden'), 1600);
        }

        // ---- Scrolling -------------------------------------------------------------------------
        // Only follow new output while the user is at (or near) the bottom of the transcript.
        function isNearBottom() {
            return messagesDiv.scrollHeight - messagesDiv.scrollTop - messagesDiv.clientHeight < 48;
        }

        function scrollToBottom(force?: boolean) {
            if (force) {
                stickToBottom = true;
            }
            if (stickToBottom) {
                messagesDiv.scrollTop = messagesDiv.scrollHeight;
            }
            updateJumpToLatest();
        }

        function updateJumpToLatest() {
            const show = activeView === 'chat' && !stickToBottom && messagesDiv.scrollHeight > messagesDiv.clientHeight + 48;
            jumpToLatestButton.classList.toggle('hidden', !show);
        }

        messagesDiv.addEventListener('scroll', () => {
            stickToBottom = isNearBottom();
            updateJumpToLatest();
        }, { passive: true });

        jumpToLatestButton.addEventListener('click', () => {
            scrollToBottom(true);
            inputField.focus();
        });

        // ---- Streaming render throttle --------------------------------------------------------
        // Markdown re-rendering on every token is O(n^2); coalesce into one render per frame.
        function scheduleRender(key, render) {
            pendingRenders.set(key, render);
            if (renderFrame) {
                return;
            }
            const raf = typeof window.requestAnimationFrame === 'function'
                ? window.requestAnimationFrame.bind(window)
                : (callback) => setTimeout(callback, 16);
            renderFrame = raf(flushRenders);
        }

        function flushRenders() {
            renderFrame = 0;
            const renders = Array.from(pendingRenders.values());
            pendingRenders.clear();
            renders.forEach(render => render());
            moveProgressIndicatorToEnd();
            scrollToBottom();
        }

        function flushPendingRendersNow() {
            if (pendingRenders.size > 0) {
                flushRenders();
            }
        }

        function post(command) {
            api.postMessage(command);
        }

        function appendMessage(text, role, attachments?: string[]) {
            if (typeof text !== 'string') {
                return null;
            }

            emptyState.hidden = true;
            const article = document.createElement('article');
            const variant = role === 'system' ? classifySystemMessage(text) : '';
            article.className = 'message-card ' + role + (variant ? ' notice-' + variant : '');

            if (role === 'system') {
                const icon = iconElement(variant === 'error' ? 'error' : variant === 'warning' ? 'warning' : variant === 'success' ? 'check' : 'info', 'notice-icon');
                article.appendChild(icon);
            }

            const body = document.createElement('div');
            body.className = 'message-text';
            renderMessageText(body, text, role);
            article.appendChild(body);

            if (role === 'user' && Array.isArray(attachments) && attachments.length > 0) {
                const list = document.createElement('div');
                list.className = 'message-attachments';
                attachments.forEach(attachment => {
                    const label = String(attachment || '');
                    const match = /^(.*?)(?::(\d+)(?:-\d+)?)?$/.exec(label);
                    const link = createFileReferenceLink(label, match ? match[1] : label, match && match[2] ? Number(match[2]) : undefined);
                    link.className = 'attachment-chip';
                    link.insertBefore(iconElement('file'), link.firstChild);
                    list.appendChild(link);
                });
                article.appendChild(list);
            }

            if (role === 'user' || role === 'assistant') {
                article.appendChild(createMessageActions(article, role));
            }

            article.dataset.text = text;
            messagesDiv.appendChild(article);
            if (role === 'user') {
                scrollToBottom(true);
            }
            moveProgressIndicatorToEnd();
            scrollToBottom();
            updateStatusRail();
            return article;
        }

        function classifySystemMessage(text) {
            const value = String(text || '').trimStart();
            if (/^(error|failed|unable|cannot|could not)\b/i.test(value)) {
                return 'error';
            }
            if (/^(warning|permission denied|cancelled)\b/i.test(value)) {
                return 'warning';
            }
            if (/^(started|resumed|permission granted|done|screen cleared)\b/i.test(value)) {
                return 'success';
            }
            return '';
        }

        function createMessageActions(article, role) {
            const actions = document.createElement('div');
            actions.className = 'message-actions';

            const copy = document.createElement('button');
            copy.type = 'button';
            copy.className = 'message-action';
            copy.title = 'Copy';
            copy.setAttribute('aria-label', 'Copy message');
            setIconContent(copy, 'copy');
            copy.addEventListener('click', () => {
                copyTextToClipboard(article.dataset.text || '');
                flashCopied(copy);
            });
            actions.appendChild(copy);

            if (role === 'user') {
                const edit = document.createElement('button');
                edit.type = 'button';
                edit.className = 'message-action';
                edit.title = 'Edit and resend';
                edit.setAttribute('aria-label', 'Edit and resend');
                setIconContent(edit, 'edit');
                edit.addEventListener('click', () => {
                    inputField.value = article.dataset.text || '';
                    showChatPage();
                    autoResizeInput();
                    inputField.focus();
                    inputField.setSelectionRange(inputField.value.length, inputField.value.length);
                    updateComposerState();
                });
                actions.appendChild(edit);
            }

            return actions;
        }

        function flashCopied(button) {
            setIconContent(button, 'check');
            button.classList.add('copied');
            setTimeout(() => {
                setIconContent(button, 'copy');
                button.classList.remove('copied');
            }, 1200);
        }

        const fileReferencePattern = /(^|[\s("'<>\[])([A-Za-z]:[\\/][^\s"'<>|]+|(?:\.{1,2}[\\/])?(?:(?:[^\s"'<>:|\\/]+)[\\/])+[^\s"'<>:|\\/]+\.[A-Za-z][A-Za-z0-9]{0,15}|[^\s"'<>:|\\/]+\.[A-Za-z][A-Za-z0-9]{0,15})(?::(\d{1,7})(?::(\d{1,5}))?)?/g;

        function renderLinkifiedText(container, text) {
            container.textContent = '';
            appendLinkifiedText(container, String(text || ''));
        }

        function renderMessageText(container, text, role) {
            if (role === 'assistant' || role === 'reasoning') {
                renderMarkdownText(container, text);
                return;
            }

            renderLinkifiedText(container, text);
        }

        function renderMarkdownText(container, text) {
            container.textContent = '';
            const markdown = document.createElement('div');
            markdown.className = 'markdown-rendered';
            appendMarkdownBlocks(markdown, String(text || ''));
            container.appendChild(markdown);
        }

        function appendMarkdownBlocks(container, text) {
            const newline = getMarkdownNewline();
            const lines = String(text || '').replaceAll(String.fromCharCode(13) + newline, newline).split(newline);
            let index = 0;

            while (index < lines.length) {
                const line = lines[index];
                if (!line || line.trim().length === 0) {
                    index += 1;
                    continue;
                }

                const fence = getMarkdownFence(line);
                if (fence) {
                    const block = readMarkdownCodeFence(lines, index, fence);
                    container.appendChild(createMarkdownCodeBlock(block.content, block.language));
                    index = block.nextIndex;
                    continue;
                }

                if (/^\s*([-*_])(\s*\1){2,}\s*$/.test(line)) {
                    container.appendChild(document.createElement('hr'));
                    index += 1;
                    continue;
                }

                if (isMarkdownTableStart(lines, index)) {
                    const table = readMarkdownTable(lines, index);
                    container.appendChild(table.element);
                    index = table.nextIndex;
                    continue;
                }

                const heading = line.match(/^(#{1,6})\s+(.+)$/);
                if (heading) {
                    const level = Math.min(6, heading[1].length);
                    const element = document.createElement('h' + level);
                    appendMarkdownInline(element, heading[2].replace(/\s+#+\s*$/, ''));
                    container.appendChild(element);
                    index += 1;
                    continue;
                }

                if (/^\s*>\s?/.test(line)) {
                    const quoteLines = [];
                    while (index < lines.length && /^\s*>\s?/.test(lines[index])) {
                        quoteLines.push(lines[index].replace(/^\s*>\s?/, ''));
                        index += 1;
                    }
                    const quote = document.createElement('blockquote');
                    appendMarkdownBlocks(quote, quoteLines.join(newline));
                    container.appendChild(quote);
                    continue;
                }

                const listInfo = getMarkdownListInfo(line);
                if (listInfo) {
                    const list = document.createElement(listInfo.ordered ? 'ol' : 'ul');
                    while (index < lines.length) {
                        const itemInfo = getMarkdownListInfo(lines[index]);
                        if (!itemInfo || itemInfo.ordered !== listInfo.ordered) {
                            break;
                        }
                        const item = document.createElement('li');
                        appendMarkdownInline(item, itemInfo.text);
                        list.appendChild(item);
                        index += 1;
                    }
                    container.appendChild(list);
                    continue;
                }

                const paragraph = [];
                while (index < lines.length) {
                    const current = lines[index];
                    if (!current || current.trim().length === 0) {
                        break;
                    }
                    if (getMarkdownFence(current) || /^(#{1,6})\s+/.test(current) || /^\s*>\s?/.test(current) || getMarkdownListInfo(current) || isMarkdownTableStart(lines, index)) {
                        break;
                    }
                    paragraph.push(current);
                    index += 1;
                }

                const p = document.createElement('p');
                appendMarkdownInline(p, paragraph.join(newline));
                container.appendChild(p);
            }
        }

        function getMarkdownNewline() {
            return String.fromCharCode(10);
        }

        function getMarkdownFence(line) {
            const trimmed = String(line || '').trim();
            const backtickFence = String.fromCharCode(96).repeat(3);
            if (trimmed.startsWith(backtickFence)) {
                return { marker: backtickFence, language: trimmed.slice(backtickFence.length).trim().split(/\s+/)[0] || '' };
            }
            if (trimmed.startsWith('~~~')) {
                return { marker: '~~~', language: trimmed.slice(3).trim().split(/\s+/)[0] || '' };
            }
            return null;
        }

        function readMarkdownCodeFence(lines, startIndex, fence) {
            const newline = getMarkdownNewline();
            const codeLines = [];
            let index = startIndex + 1;
            while (index < lines.length) {
                const current = String(lines[index] || '');
                if (current.trim().startsWith(fence.marker)) {
                    return {
                        content: codeLines.join(newline),
                        language: fence.language,
                        nextIndex: index + 1
                    };
                }
                codeLines.push(current);
                index += 1;
            }
            return {
                content: codeLines.join(newline),
                language: fence.language,
                nextIndex: index
            };
        }

        function isMarkdownTableStart(lines, index) {
            const header = String(lines[index] || '');
            const separator = String(lines[index + 1] || '');
            return header.includes('|') &&
                /^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$/.test(separator) &&
                separator.includes('-');
        }

        function splitMarkdownTableRow(line) {
            let value = String(line || '').trim();
            if (value.startsWith('|')) {
                value = value.slice(1);
            }
            if (value.endsWith('|') && !value.endsWith('\\|')) {
                value = value.slice(0, -1);
            }
            return value.split(/(?<!\\)\|/).map(cell => cell.trim().replace(/\\\|/g, '|'));
        }

        function readMarkdownTable(lines, startIndex) {
            const headers = splitMarkdownTableRow(lines[startIndex]);
            const alignments = splitMarkdownTableRow(lines[startIndex + 1]).map(cell => {
                const left = cell.startsWith(':');
                const right = cell.endsWith(':');
                return left && right ? 'center' : right ? 'right' : left ? 'left' : '';
            });

            const wrap = document.createElement('div');
            wrap.className = 'markdown-table-wrap';
            const table = document.createElement('table');
            const thead = document.createElement('thead');
            const headRow = document.createElement('tr');
            headers.forEach((cell, column) => {
                const th = document.createElement('th');
                if (alignments[column]) {
                    th.style.textAlign = alignments[column];
                }
                appendMarkdownInline(th, cell);
                headRow.appendChild(th);
            });
            thead.appendChild(headRow);
            table.appendChild(thead);

            const tbody = document.createElement('tbody');
            let index = startIndex + 2;
            while (index < lines.length && String(lines[index] || '').includes('|') && String(lines[index]).trim().length > 0) {
                const row = document.createElement('tr');
                const cells = splitMarkdownTableRow(lines[index]);
                headers.forEach((_, column) => {
                    const td = document.createElement('td');
                    if (alignments[column]) {
                        td.style.textAlign = alignments[column];
                    }
                    appendMarkdownInline(td, cells[column] || '');
                    row.appendChild(td);
                });
                tbody.appendChild(row);
                index += 1;
            }
            table.appendChild(tbody);
            wrap.appendChild(table);
            return { element: wrap, nextIndex: index };
        }

        function createMarkdownCodeBlock(content, language) {
            const code = String(content || '');
            const wrap = document.createElement('div');
            wrap.className = 'markdown-code-block';
            if (language) {
                wrap.dataset.language = language;
            }

            const header = document.createElement('div');
            header.className = 'code-block-header';
            const lang = document.createElement('span');
            lang.className = 'code-block-language';
            lang.textContent = language || 'text';
            header.appendChild(lang);

            const actions = document.createElement('div');
            actions.className = 'code-block-actions';
            actions.appendChild(createCodeAction('copy', 'Copy', button => {
                copyTextToClipboard(code);
                flashCopied(button);
            }));
            actions.appendChild(createCodeAction('insert', 'Insert at cursor', () => {
                post({ command: 'insertCode', text: code });
            }));
            actions.appendChild(createCodeAction('newFile', 'Open in new editor', () => {
                post({ command: 'openInEditor', text: code, language: normalizeLanguageName(language) });
            }));
            header.appendChild(actions);
            wrap.appendChild(header);

            const pre = document.createElement('pre');
            const codeElement = document.createElement('code');
            const normalizedLanguage = normalizeLanguageName(language);
            if (normalizedLanguage && normalizedLanguage !== 'text' && code.length < 40000) {
                splitCodeLines(code).forEach((line, index, all) => {
                    appendSyntaxHighlightedLine(codeElement, line, normalizedLanguage);
                    if (index < all.length - 1) {
                        codeElement.appendChild(document.createTextNode('\n'));
                    }
                });
            } else {
                codeElement.textContent = code;
            }
            pre.appendChild(codeElement);
            wrap.appendChild(pre);
            return wrap;
        }

        function createCodeAction(icon, title, onClick) {
            const button = document.createElement('button');
            button.type = 'button';
            button.className = 'code-action';
            button.title = title;
            button.setAttribute('aria-label', title);
            setIconContent(button, icon);
            button.addEventListener('click', event => {
                event.preventDefault();
                event.stopPropagation();
                onClick(button);
            });
            return button;
        }

        function getMarkdownListInfo(line) {
            const unordered = String(line || '').match(/^\s*[-+*]\s+(.+)$/);
            if (unordered) {
                return { ordered: false, text: unordered[1] };
            }
            const ordered = String(line || '').match(/^\s*\d+[.)]\s+(.+)$/);
            if (ordered) {
                return { ordered: true, text: ordered[1] };
            }
            return null;
        }

        function appendMarkdownInline(container, text) {
            const value = String(text || '');
            let index = 0;
            const tick = String.fromCharCode(96);
            const specials = '[' + tick + '*_~';

            while (index < value.length) {
                const char = value[index];

                if (char === tick) {
                    const end = value.indexOf(tick, index + 1);
                    if (end > index + 1) {
                        const code = document.createElement('code');
                        code.textContent = value.slice(index + 1, end);
                        container.appendChild(code);
                        index = end + 1;
                        continue;
                    }
                }

                if (value.startsWith('~~', index)) {
                    const end = value.indexOf('~~', index + 2);
                    if (end > index + 2) {
                        const del = document.createElement('del');
                        appendMarkdownInline(del, value.slice(index + 2, end));
                        container.appendChild(del);
                        index = end + 2;
                        continue;
                    }
                }

                if (value.startsWith('**', index) || value.startsWith('__', index)) {
                    const marker = value.slice(index, index + 2);
                    const end = value.indexOf(marker, index + 2);
                    if (end > index + 2) {
                        const strong = document.createElement('strong');
                        appendMarkdownInline(strong, value.slice(index + 2, end));
                        container.appendChild(strong);
                        index = end + 2;
                        continue;
                    }
                }

                if ((char === '*' || char === '_') && value[index + 1] !== char) {
                    const end = value.indexOf(char, index + 1);
                    if (end > index + 1) {
                        const em = document.createElement('em');
                        appendMarkdownInline(em, value.slice(index + 1, end));
                        container.appendChild(em);
                        index = end + 1;
                        continue;
                    }
                }

                if (char === '[') {
                    const closeLabel = value.indexOf(']', index + 1);
                    const openTarget = closeLabel >= 0 && value[closeLabel + 1] === '(' ? closeLabel + 1 : -1;
                    const closeTarget = openTarget >= 0 ? value.indexOf(')', openTarget + 1) : -1;
                    if (closeLabel > index + 1 && openTarget >= 0 && closeTarget > openTarget + 1) {
                        container.appendChild(createMarkdownLink(value.slice(index + 1, closeLabel), value.slice(openTarget + 1, closeTarget)));
                        index = closeTarget + 1;
                        continue;
                    }
                }

                let next = value.length;
                for (const special of specials) {
                    const found = value.indexOf(special, index + 1);
                    if (found >= 0 && found < next) {
                        next = found;
                    }
                }
                appendLinkifiedText(container, value.slice(index, next));
                index = next;
            }
        }

        function createMarkdownLink(label, target) {
            const cleanTarget = String(target || '').trim();
            const fileTarget = trimFileReference(cleanTarget);
            if (fileTarget && isLikelyFileReference(fileTarget, cleanTarget, 0)) {
                return createFileReferenceLink(label || fileTarget, fileTarget);
            }

            // URL parser is the sanitizer (CodeQL recognizes it); allowlist scheme.
            let safeHref = '';
            try {
                const parsed = new URL(cleanTarget);
                if (parsed.protocol === 'http:' || parsed.protocol === 'https:' || parsed.protocol === 'mailto:') {
                    safeHref = parsed.href;
                }
            } catch {
                safeHref = '';
            }

            const link = document.createElement('a');
            link.href = safeHref || '#';
            link.textContent = label || cleanTarget;
            link.title = cleanTarget;
            if (safeHref) {
                link.target = '_blank';
                link.rel = 'noopener noreferrer';
            } else {
                link.addEventListener('click', event => event.preventDefault());
            }
            return link;
        }

        function appendLinkifiedText(container, text) {
            let lastIndex = 0;
            fileReferencePattern.lastIndex = 0;
            let match = fileReferencePattern.exec(text);

            while (match) {
                const prefix = match[1] || '';
                const candidate = match[2] || '';
                const candidateStart = match.index + prefix.length;
                const line = parsePositiveInteger(match[3]);
                const column = parsePositiveInteger(match[4]);
                const locationText = line ? ':' + line + (column ? ':' + column : '') : '';
                const normalized = trimFileReference(candidate);
                const displayText = normalized + locationText;

                if (normalized && isLikelyFileReference(normalized, text, candidateStart)) {
                    container.appendChild(document.createTextNode(text.slice(lastIndex, candidateStart)));
                    container.appendChild(createFileReferenceLink(displayText, normalized, line, column));
                    const consumedEnd = candidateStart + candidate.length + locationText.length;
                    const trailingText = candidate.slice(normalized.length);
                    if (trailingText) {
                        container.appendChild(document.createTextNode(trailingText));
                    }
                    lastIndex = consumedEnd;
                }

                match = fileReferencePattern.exec(text);
            }

            if (lastIndex < text.length) {
                container.appendChild(document.createTextNode(text.slice(lastIndex)));
            }
        }

        function trimFileReference(value) {
            return String(value || '').replace(/[),.;\]}]+$/, '');
        }

        function isLikelyFileReference(candidate, sourceText, startIndex) {
            if (!candidate || candidate.length > 320) {
                return false;
            }

            const before = sourceText.slice(Math.max(0, startIndex - 8), startIndex).toLowerCase();
            if (before.includes('://')) {
                return false;
            }

            const hasSeparator = candidate.includes('/') || candidate.includes('\\');
            if (!hasSeparator) {
                // Bare names ("e.g", "Node.js", "v1.x") are only links when they carry a real source extension.
                const extension = candidate.split('.').pop().toLowerCase();
                if (/^(node|next|nuxt|vue|react|express|three|d3|chart|moment|ember|backbone|alpine|solid|nest|deno|bun)\.js$/i.test(candidate)) {
                    return false;
                }
                return candidate.indexOf('.') > 0 && KNOWN_FILE_EXTENSIONS.has(extension);
            }

            return candidate.includes('.') || hasSeparator;
        }

        const KNOWN_FILE_EXTENSIONS = new Set([
            'ts', 'tsx', 'js', 'jsx', 'mjs', 'cjs', 'json', 'jsonc', 'md', 'mdx', 'txt', 'yml', 'yaml', 'toml', 'ini',
            'cfg', 'conf', 'env', 'xml', 'html', 'htm', 'css', 'scss', 'less', 'svg', 'py', 'pyi', 'rb', 'go', 'rs',
            'java', 'kt', 'kts', 'swift', 'c', 'h', 'cc', 'cpp', 'cxx', 'hpp', 'cs', 'csproj', 'sln', 'slnx', 'props',
            'targets', 'fs', 'fsproj', 'vb', 'php', 'sh', 'bash', 'zsh', 'ps1', 'psm1', 'bat', 'cmd', 'sql', 'graphql',
            'proto', 'lock', 'gradle', 'dart', 'lua', 'r', 'scala', 'vue', 'svelte', 'astro', 'razor', 'cshtml',
            'xaml', 'axaml', 'resx', 'config', 'editorconfig', 'gitignore', 'dockerfile', 'tf', 'hcl', 'log', 'csv', 'ipynb'
        ]);

        function createFileReferenceLink(displayText, filePath, line?: number, column?: number) {
            const link = document.createElement('a');
            link.href = '#';
            link.className = 'file-link';
            link.textContent = displayText;
            link.title = 'Open ' + displayText;
            link.addEventListener('click', event => {
                event.preventDefault();
                event.stopPropagation();
                post({
                    command: 'openFile',
                    filePath,
                    line,
                    column
                });
            });
            return link;
        }

        function parsePositiveInteger(value) {
            const number = Number.parseInt(value || '', 10);
            return Number.isFinite(number) && number > 0 ? number : undefined;
        }

        function appendMessageChunk(chunk) {
            const role = chunk.role === 'user'
                ? 'user'
                : chunk.role === 'reasoning'
                    ? 'reasoning'
                    : 'assistant';
            if (role === 'user') {
                activeAssistantMessage = null;
                activeReasoningMessage = null;
                appendMessage(chunk.text, 'user');
                return;
            }

            if (role === 'reasoning') {
                appendReasoningChunk(chunk.text);
                return;
            }

            if (activeReasoningMessage) {
                finalizeReasoning(activeReasoningMessage);
            }
            activeReasoningMessage = null;
            if (!activeAssistantMessage) {
                activeAssistantMessage = appendMessage('', 'assistant');
                activeAssistantMessage.classList.add('streaming');
            }

            if (!activeAssistantMessage) {
                return;
            }

            emptyState.hidden = true;
            const article = activeAssistantMessage;
            article.dataset.text = (article.dataset.text || '') + chunk.text;
            scheduleRender(article, () => {
                const body = article.querySelector('.message-text');
                if (body) {
                    renderMarkdownText(body, article.dataset.text || '');
                }
            });
            setProgressLabel('Writing');
        }

        function appendReasoningChunk(text) {
            if (!activeReasoningMessage) {
                activeReasoningMessage = appendMessage('', 'reasoning');
                activeReasoningMessage.classList.add('streaming');
            }

            if (!activeReasoningMessage) {
                return;
            }

            emptyState.hidden = true;
            const article = activeReasoningMessage;
            article.dataset.text = (article.dataset.text || '') + text;
            scheduleRender(article, () => renderReasoning(article));
            setProgressLabel('Thinking');
        }

        function renderReasoning(article) {
            const body = article.querySelector('.message-text');
            if (!body) {
                return;
            }

            const previousDetails = body.querySelector('.thinking-details');
            const shouldOpen = previousDetails ? previousDetails.open : false;
            body.textContent = '';
            body.appendChild(createReasoningDetails(article.dataset.text || '', shouldOpen, article.classList.contains('streaming')));
        }

        function finalizeReasoning(article) {
            if (!article || !article.classList.contains('streaming')) {
                return;
            }
            article.classList.remove('streaming');
            pendingRenders.delete(article);
            renderReasoning(article);
        }

        function finalizeStreamingMessages() {
            flushPendingRendersNow();
            if (activeReasoningMessage) {
                finalizeReasoning(activeReasoningMessage);
            }
            messagesDiv.querySelectorAll('.message-card.streaming').forEach(article => {
                article.classList.remove('streaming');
                if (article.classList.contains('reasoning')) {
                    renderReasoning(article);
                }
            });
            activeAssistantMessage = null;
            activeReasoningMessage = null;
        }

        function buildPromptPayload(text) {
            const payload: { command: string; text: string; editorContext?: unknown } = { command: 'sendMessage', text };
            if (includeEditorContext && activeEditor && !isSlashCommandText(text)) {
                payload.editorContext = activeEditor;
            }
            return payload;
        }

        function rememberPrompt(text) {
            if (promptHistory[promptHistory.length - 1] !== text) {
                promptHistory.push(text);
                while (promptHistory.length > 50) {
                    promptHistory.shift();
                }
                persistState();
            }
            historyIndex = -1;
            historyDraft = '';
        }

        function sendCurrentInput() {
            const text = inputField.value.trim();
            if (!text) {
                return;
            }

            rememberPrompt(text);

            // Busy? queue it; the queue drains automatically when the turn ends.
            if (promptState.isRunning) {
                promptQueue.push(buildPromptPayload(text));
                renderPromptQueue();
            } else {
                post(buildPromptPayload(text));
            }

            inputField.value = '';
            autoResizeInput();
            hideSuggestions();
            updateComposerState();
            scrollToBottom(true);
        }

        function renderPromptQueue() {
            promptQueueDiv.textContent = '';
            promptQueueDiv.classList.toggle('hidden', promptQueue.length === 0);
            promptQueue.forEach((payload, index) => {
                const item = document.createElement('div');
                item.className = 'prompt-queue-item';

                const badge = document.createElement('span');
                badge.className = 'prompt-queue-badge';
                badge.textContent = index === 0 ? 'Next' : 'Queued';
                item.appendChild(badge);

                const label = document.createElement('span');
                label.className = 'prompt-queue-text';
                label.textContent = payload.text;
                label.title = payload.text;

                const remove = document.createElement('button');
                remove.type = 'button';
                remove.className = 'prompt-queue-remove';
                remove.title = 'Remove from queue';
                remove.setAttribute('aria-label', 'Remove from queue');
                setIconContent(remove, 'close');
                remove.addEventListener('click', () => {
                    promptQueue.splice(index, 1);
                    renderPromptQueue();
                });

                item.appendChild(label);
                item.appendChild(remove);
                promptQueueDiv.appendChild(item);
            });
        }

        function drainPromptQueue() {
            if (promptState.isRunning || promptQueue.length === 0) {
                return;
            }

            const next = promptQueue.shift();
            renderPromptQueue();
            post(next);
        }

        function handleSendButtonClick() {
            if (promptState.isRunning === true && inputField.value.trim().length === 0) {
                if (promptState.isCancelling !== true) {
                    post({ command: 'cancelPrompt' });
                }
                return;
            }

            sendCurrentInput();
        }

        function autoResizeInput() {
            inputField.style.height = 'auto';
            const max = Math.max(120, Math.floor(window.innerHeight * 0.4));
            const next = Math.min(inputField.scrollHeight, max);
            inputField.style.height = next + 'px';
            inputField.style.overflowY = inputField.scrollHeight > max ? 'auto' : 'hidden';
        }

        function setComposerText(text) {
            inputField.value = text;
            autoResizeInput();
            inputField.focus();
            inputField.setSelectionRange(inputField.value.length, inputField.value.length);
            updateSuggestions();
            updateComposerState();
        }

        function recallHistory(direction) {
            if (promptHistory.length === 0) {
                return false;
            }

            if (historyIndex === -1) {
                if (direction > 0) {
                    return false;
                }
                historyDraft = inputField.value;
                historyIndex = promptHistory.length - 1;
            } else {
                historyIndex += direction;
            }

            if (historyIndex >= promptHistory.length) {
                historyIndex = -1;
                setComposerText(historyDraft);
                return true;
            }

            historyIndex = Math.max(0, historyIndex);
            setComposerText(promptHistory[historyIndex]);
            return true;
        }

        function renderEditorContextChip() {
            if (!activeEditor || !activeEditor.relativePath) {
                editorContextChip.classList.add('hidden');
                return;
            }

            editorContextChip.classList.remove('hidden');
            const name = String(activeEditor.relativePath).split('/').pop();
            const range = activeEditor.selection
                ? ':' + activeEditor.selection.startLine + (activeEditor.selection.endLine !== activeEditor.selection.startLine ? '-' + activeEditor.selection.endLine : '')
                : '';
            editorContextLabel.textContent = name + range;
            editorContextChip.classList.toggle('disabled', !includeEditorContext);
            editorContextChip.setAttribute('aria-pressed', includeEditorContext ? 'true' : 'false');
            editorContextChip.title = (includeEditorContext ? 'Included as context: ' : 'Not included (click to include): ') +
                activeEditor.relativePath + range;
            editorContextToggle.textContent = '';
            editorContextToggle.appendChild(iconElement(includeEditorContext ? 'eye' : 'eyeOff'));
        }

        sendButton.addEventListener('click', handleSendButtonClick);
        mentionButton.addEventListener('click', () => {
            const caret = typeof inputField.selectionStart === 'number' ? inputField.selectionStart : inputField.value.length;
            const before = inputField.value.slice(0, caret);
            const needsSpace = before.length > 0 && !/\s$/.test(before);
            const insert = (needsSpace ? ' ' : '') + '@';
            inputField.value = before + insert + inputField.value.slice(caret);
            inputField.focus();
            inputField.setSelectionRange(caret + insert.length, caret + insert.length);
            autoResizeInput();
            updateSuggestions();
            updateComposerState();
        });
        newChatButton.addEventListener('click', () => {
            if (promptState.isRunning) {
                showToast('Stop the current response before starting a new chat.');
                return;
            }
            post({ command: 'newChat' });
        });
        settingsButton.addEventListener('click', toggleSettingsPage);
        sessionsButton.addEventListener('click', () => post({ command: 'listSessions' }));
        settingsCloseButton.addEventListener('click', showChatPage);
        statusRetryButton.addEventListener('click', () => post({ command: 'restartProcess' }));
        statusLogsButton.addEventListener('click', () => post({ command: 'openLogs' }));
        editorContextChip.addEventListener('click', () => {
            includeEditorContext = !includeEditorContext;
            persistState();
            renderEditorContextChip();
        });
        workingSetToggle.addEventListener('click', () => {
            workingSetExpanded = !workingSetExpanded;
            updateActivityVisibility();
        });
        settingsPage.querySelectorAll('.settings-action').forEach(button => {
            button.addEventListener('click', () => runSettingsAction(button));
        });
        document.querySelectorAll('#starter-list .starter').forEach((button: HTMLElement) => {
            button.addEventListener('click', () => {
                const vscodeCommand = button.dataset.command;
                if (vscodeCommand) {
                    post({ command: 'runVsCodeCommand', id: vscodeCommand });
                    return;
                }
                const prompt = button.dataset.prompt || '';
                if (!prompt) {
                    return;
                }
                if (button.dataset.needsEditor === 'true' && !activeEditor) {
                    showToast('Open a file in the editor first.');
                    return;
                }
                inputField.value = prompt;
                sendCurrentInput();
            });
        });
        modelButton.addEventListener('click', toggleAgentMenu);
        profileSelect.addEventListener('change', () => {
            post({ command: 'changeProfile', profileName: profileSelect.value || '' });
        });

        inputField.addEventListener('keydown', (event) => {
            if (event.isComposing) {
                return;
            }

            if (visibleSuggestions.length > 0) {
                if (event.key === 'ArrowDown') {
                    event.preventDefault();
                    activeSuggestionIndex = (activeSuggestionIndex + 1) % visibleSuggestions.length;
                    renderSuggestions();
                    return;
                }

                if (event.key === 'ArrowUp') {
                    event.preventDefault();
                    activeSuggestionIndex = (activeSuggestionIndex - 1 + visibleSuggestions.length) % visibleSuggestions.length;
                    renderSuggestions();
                    return;
                }

                if (event.key === 'Tab' || (event.key === 'Enter' && !event.shiftKey)) {
                    event.preventDefault();
                    const suggestion = visibleSuggestions[activeSuggestionIndex];
                    if (event.key === 'Enter' && !suggestion.mention && suggestion.insertText.trim() === inputField.value.trim()) {
                        hideSuggestions();
                        sendCurrentInput();
                        return;
                    }
                    applySuggestion(suggestion);
                    return;
                }

                if (event.key === 'Escape') {
                    event.preventDefault();
                    hideSuggestions();
                    return;
                }
            }

            if (event.altKey && (event.key === 'ArrowDown' || event.key === 'ArrowUp') && agentThreads.size > 0) {
                event.preventDefault();
                switchAgentThread(event.key === 'ArrowDown' ? 1 : -1);
                return;
            }

            if (event.key === 'ArrowUp' && !event.shiftKey && !event.altKey && !event.ctrlKey && !event.metaKey &&
                (inputField.value.length === 0 || historyIndex !== -1) && inputField.selectionStart === inputField.selectionEnd &&
                inputField.value.lastIndexOf('\n', inputField.selectionStart - 1) === -1) {
                if (recallHistory(-1)) {
                    event.preventDefault();
                }
                return;
            }

            if (event.key === 'ArrowDown' && historyIndex !== -1 && !event.shiftKey &&
                inputField.value.indexOf('\n', inputField.selectionEnd) === -1) {
                if (recallHistory(1)) {
                    event.preventDefault();
                }
                return;
            }

            if (event.key === 'Escape') {
                if (promptState.isRunning && !promptState.isCancelling) {
                    event.preventDefault();
                    post({ command: 'cancelPrompt' });
                }
                return;
            }

            if (event.key === 'Enter' && !event.shiftKey) {
                event.preventDefault();
                sendCurrentInput();
            }
        });

        inputField.addEventListener('input', () => {
            if (historyIndex !== -1 && inputField.value !== promptHistory[historyIndex]) {
                historyIndex = -1;
            }
            autoResizeInput();
            updateSuggestions();
            updateComposerState();
        });
        inputField.addEventListener('blur', () => setTimeout(hideSuggestions, 150));
        window.addEventListener('resize', () => {
            autoResizeInput();
            if (!agentMenu.classList.contains('hidden')) {
                positionAgentMenu();
            }
            updateJumpToLatest();
        });

        document.addEventListener('keydown', event => {
            if (event.key === 'Escape') {
                if (!agentMenu.classList.contains('hidden')) {
                    event.preventDefault();
                    closeAgentMenu();
                    modelButton.focus();
                    return;
                }
                if (!modalBackdrop.classList.contains('hidden')) {
                    event.preventDefault();
                    if (activeModalRequest) {
                        if (activeModalRequest.allowCancellation !== false) {
                            cancelActiveRequest();
                        }
                    } else {
                        closeModal();
                    }
                    return;
                }
                if (activeView === 'settings') {
                    event.preventDefault();
                    showChatPage();
                    return;
                }
                if (promptState.isRunning && !promptState.isCancelling && document.activeElement !== inputField) {
                    event.preventDefault();
                    post({ command: 'cancelPrompt' });
                }
                return;
            }

            // Type anywhere to focus the composer (like a terminal-style agent).
            const target = event.target as HTMLElement;
            const typingTarget = target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.tagName === 'SELECT' || target.isContentEditable);
            if (!typingTarget && activeView === 'chat' && modalBackdrop.classList.contains('hidden') &&
                event.key.length === 1 && !event.ctrlKey && !event.metaKey && !event.altKey && !/^[0-9]$/.test(event.key)) {
                inputField.focus();
            }
        });

        modalBackdrop.addEventListener('mousedown', event => {
            if (event.target === modalBackdrop && !activeModalRequest) {
                closeModal();
            }
        });

        window.addEventListener('message', event => {
            const message = event.data;
            if (!message || typeof message.command !== 'string') {
                return;
            }
            if (message.command === 'appendMessage') {
                if (message.role !== 'metrics') {
                    flushPendingRendersNow();
                }
                if (message.role === 'metrics') {
                    appendTurnFooter(message.text);
                } else {
                    appendMessage(message.text, message.role, message.attachments);
                }
            } else if (message.command === 'appendMessageChunk') {
                appendMessageChunk(message.chunk);
            } else if (message.command === 'clearMessages') {
                pendingRenders.clear();
                messagesDiv.textContent = '';
                messagesDiv.appendChild(emptyState);
                emptyState.hidden = false;
                activeAssistantMessage = null;
                activeReasoningMessage = null;
                progressIndicator = null;
                inlinePermissionCards.clear();
                toolCalls.clear();
                toolMessageElements.clear();
                agentThreads.clear();
                activeAgentThreadId = '';
                stickToBottom = true;
                renderPlan(null);
                renderFileEditsSummary(null);
                renderAgentThreads();
                updateStatusRail();
                updateJumpToLatest();
            } else if (message.command === 'setSessionInfo') {
                sessionInfo = message.sessionInfo;
                workingDirectory = message.workingDirectory || '';
                renderContext();
                updateSuggestions();
            } else if (message.command === 'setProcessStatus') {
                processStatus = message.status;
                updateStatusRail();
                updateComposerState();
            } else if (message.command === 'setPromptState') {
                const wasRunning = promptState.isRunning;
                promptState = message.promptState || { isRunning: false, isCancelling: false };
                if (promptState.isRunning && !wasRunning) {
                    finalizeStreamingMessages();
                    turnStartedAt = Date.now();
                }
                if (!promptState.isRunning && wasRunning) {
                    finalizeStreamingMessages();
                    if (promptState.lastStopReason === 'cancelled') {
                        appendMessage('Cancelled.', 'system');
                    } else if (promptState.error) {
                        appendMessage('Error: ' + promptState.error, 'system');
                    }
                }
                updateStatusRail();
                updateComposerState();
                if (!promptState.isRunning && wasRunning) {
                    drainPromptQueue();
                }
            } else if (message.command === 'setToolCall') {
                flushPendingRendersNow();
                setToolCall(message.toolCall);
            } else if (message.command === 'clearToolCalls') {
                toolCalls.clear();
                toolMessageElements.clear();
                agentThreads.clear();
                activeAgentThreadId = '';
                renderTools();
                renderAgentThreads();
            } else if (message.command === 'setPlan') {
                renderPlan(message.plan);
            } else if (message.command === 'setFileEditsSummary') {
                renderFileEditsSummary(message.summary);
            } else if (message.command === 'showClientRequest') {
                flushPendingRendersNow();
                showClientRequest(message.request);
            } else if (message.command === 'resolveClientRequest') {
                clearClientRequest(message.requestId);
            } else if (message.command === 'prefillComposer') {
                showChatPage();
                setComposerText(message.text || '');
            } else if (message.command === 'focusInput') {
                showChatPage();
                inputField.focus();
            } else if (message.command === 'showSettings') {
                showSettingsPage();
            } else if (message.command === 'activeEditor') {
                activeEditor = message.editor && message.editor.relativePath ? message.editor : null;
                renderEditorContextChip();
            } else if (message.command === 'toast') {
                showToast(String(message.text || ''));
            } else if (message.command === 'sessionList') {
                showSessionBrowser(message.sessions || [], message.currentSessionId || '');
            } else if (message.command === 'pluginState') {
                showPluginPanel(message.marketplaces || [], message.installed || []);
            } else if (message.command === 'fileMentions') {
                if (message.query === fileMentionQuery) {
                    fileMentionResults = Array.isArray(message.files) ? message.files : [];
                    visibleSuggestions = buildFileSuggestions(fileMentionResults);
                    activeSuggestionIndex = 0;
                    renderSuggestions();
                }
            }
        });

        function appendTurnFooter(text) {
            const value = String(text || '').trim();
            if (!value) {
                return;
            }
            const footer = document.createElement('div');
            footer.className = 'turn-footer';
            footer.textContent = value.split(' / ').join(' · ');
            messagesDiv.appendChild(footer);
            moveProgressIndicatorToEnd();
            scrollToBottom();
        }

        function toggleSettingsPage() {
            if (activeView === 'settings') {
                showChatPage();
                return;
            }

            showSettingsPage();
        }

        function showSettingsPage() {
            activeView = 'settings';
            closeAgentMenu();
            messagesDiv.classList.add('hidden');
            composerArea.classList.add('hidden');
            settingsPage.classList.remove('hidden');
            settingsButton.classList.add('active');
            settingsButton.title = 'Back to chat';
            settingsButton.setAttribute('aria-label', 'Back to chat');
            hideSuggestions();
            renderSettingsSummary();
            updateActivityVisibility();
            updateJumpToLatest();
        }

        function showChatPage() {
            const wasSettings = activeView === 'settings';
            activeView = 'chat';
            settingsPage.classList.add('hidden');
            messagesDiv.classList.remove('hidden');
            composerArea.classList.remove('hidden');
            settingsButton.classList.remove('active');
            settingsButton.title = 'Settings';
            settingsButton.setAttribute('aria-label', 'Settings');
            updateActivityVisibility();
            if (wasSettings) {
                scrollToBottom();
            }
            updateJumpToLatest();
            inputField.focus();
        }

        function runSettingsAction(button) {
            const action = button.dataset.action || '';
            if (action === 'vscodeSettings') {
                post({ command: 'openVsCodeSettings' });
                return;
            }

            if (action === 'openLogs') {
                post({ command: 'openLogs' });
                return;
            }

            if (action === 'restartProcess') {
                post({ command: 'restartProcess' });
                return;
            }

            if (promptState.isRunning === true) {
                showToast('Wait for the current response to finish.');
                return;
            }

            if (action === 'model') {
                showModelPicker();
                return;
            }

            if (action === 'sessions') {
                post({ command: 'listSessions' });
                return;
            }

            if (action === 'plugins') {
                post({ command: 'loadPlugins' });
                return;
            }

            const commandText = button.dataset.command || '';
            if (commandText) {
                showChatPage();
                post({ command: 'runSessionCommand', text: commandText });
            }
        }

        function updateStatusRail() {
            const running = promptState.isRunning === true;
            statusText.textContent = running ? (promptState.isCancelling ? 'Stopping…' : '') : '';
            renderStatusBanner();
            updateProgressIndicator();
            updateSettingsActionState();
        }

        function renderStatusBanner() {
            let tone = '';
            let text = '';
            let showRetry = false;
            if (processStatus === 'starting') {
                tone = 'info';
                text = 'Starting StemCode…';
            } else if (processStatus === 'error') {
                tone = 'error';
                text = 'StemCode failed to start. Check that the CLI is installed and the "stemcode.command" setting is correct.';
                showRetry = true;
            } else if (processStatus === 'stopped' && sessionInfo === null && messagesDiv.querySelector('.message-card')) {
                tone = 'warning';
                text = 'StemCode is not running.';
                showRetry = true;
            }

            statusBanner.classList.toggle('hidden', !text);
            statusBanner.className = 'status-banner' + (text ? ' tone-' + tone : ' hidden');
            statusBannerText.textContent = text;
            statusBannerIcon.textContent = '';
            if (text) {
                statusBannerIcon.appendChild(iconElement(tone === 'info' ? 'spinner' : tone === 'error' ? 'error' : 'warning'));
            }
            statusRetryButton.hidden = !showRetry;
            statusLogsButton.hidden = tone === 'info' || !text;
        }

        function formatStatusText(running) {
            if (running) {
                return promptState.isCancelling ? 'Cancelling' : 'Running';
            }

            return formatProcessStatus(processStatus);
        }

        function formatProcessStatus(status) {
            if (status === 'starting') {
                return 'Starting';
            }

            if (status === 'running') {
                return '';
            }

            if (status === 'error') {
                return 'Error';
            }

            return 'Stopped';
        }

        function updateProgressIndicator() {
            const shouldShow = promptState.isRunning === true && !isSlashCommandText(promptState.input);
            if (!shouldShow) {
                if (progressIndicator) {
                    progressIndicator.remove();
                    progressIndicator = null;
                }
                if (progressTimer) {
                    clearInterval(progressTimer);
                    progressTimer = null;
                }
                return;
            }

            if (!progressIndicator) {
                progressIndicator = createProgressIndicator();
                setProgressLabel(promptState.isCancelling ? 'Stopping' : 'Working');
                progressTimer = setInterval(updateProgressElapsed, 1000);
            }
            if (promptState.isCancelling) {
                setProgressLabel('Stopping');
            }

            moveProgressIndicatorToEnd();
        }

        function createProgressIndicator() {
            const indicator = document.createElement('div');
            indicator.className = 'progress-indicator';
            indicator.setAttribute('role', 'status');
            indicator.setAttribute('aria-live', 'polite');
            indicator.appendChild(iconElement('spinner', 'progress-icon'));

            const label = document.createElement('span');
            label.className = 'progress-label';
            indicator.appendChild(label);

            const elapsed = document.createElement('span');
            elapsed.className = 'progress-elapsed';
            indicator.appendChild(elapsed);

            const hint = document.createElement('span');
            hint.className = 'progress-hint';
            hint.textContent = 'Esc to stop';
            indicator.appendChild(hint);
            return indicator;
        }

        function setProgressLabel(text) {
            if (!progressIndicator) {
                return;
            }
            const label = progressIndicator.querySelector('.progress-label');
            if (label && label.textContent !== text + '…') {
                label.textContent = text + '…';
            }
            updateProgressElapsed();
        }

        function updateProgressElapsed() {
            if (!progressIndicator) {
                return;
            }
            const elapsed = progressIndicator.querySelector('.progress-elapsed');
            const seconds = turnStartedAt ? Math.floor((Date.now() - turnStartedAt) / 1000) : 0;
            elapsed.textContent = seconds > 0 ? formatElapsed(seconds) : '';
        }

        function formatElapsed(seconds) {
            if (seconds < 60) {
                return seconds + 's';
            }
            return Math.floor(seconds / 60) + 'm ' + (seconds % 60) + 's';
        }

        function moveProgressIndicatorToEnd() {
            if (progressIndicator && messagesDiv.lastChild !== progressIndicator) {
                messagesDiv.appendChild(progressIndicator);
            }
        }

        function isSlashCommandText(value) {
            return String(value || '').trimStart().startsWith('/');
        }

        function updateComposerState() {
            const running = promptState.isRunning === true;
            const cancelling = promptState.isCancelling === true;
            const hasText = inputField.value.trim().length > 0;
            const stopMode = running && !hasText;
            inputField.disabled = false; // keep editable so prompts can be queued while busy
            sendButton.disabled = stopMode ? cancelling : !hasText;
            sendButton.classList.toggle('stop-mode', stopMode);
            const mode = stopMode ? 'stop' : running ? 'queue' : 'send';
            if (sendButton.dataset.mode !== mode) {
                sendButton.dataset.mode = mode;
                setIconContent(sendButton, stopMode ? 'stop' : 'send');
            }
            const label = stopMode
                ? cancelling ? 'Stopping response' : 'Stop response (Esc)'
                : running ? 'Queue message (Enter)' : 'Send message (Enter)';
            sendButton.title = label;
            sendButton.setAttribute('aria-label', label);
            inputField.placeholder = running
                ? 'Queue a follow-up…'
                : processStatus === 'error'
                    ? 'StemCode is not running — use Retry above'
                    : 'Ask StemCode to build, fix, or explain…';
            modelButton.disabled = running || !sessionInfo;
            profileSelect.disabled = running;
            updateSettingsActionState();
        }

        function renderContext() {
            const models = sessionInfo && Array.isArray(sessionInfo.availableModelIds)
                ? sessionInfo.availableModelIds
                : [];

            const title = sessionInfo && sessionInfo.sectionTitle ? sessionInfo.sectionTitle : 'New chat';
            sectionTitle.textContent = title;
            sectionTitle.title = title;
            renderProfileSelect();
            renderModelSelect(models);
            renderSettingsSummary();
            renderContextMeter();

            updateStatusRail();
            updateComposerState();
        }

        function renderContextMeter() {
            const used = sessionInfo && sessionInfo.sectionEstimatedContextTokens;
            const windowTokens = sessionInfo && sessionInfo.activeModelContextWindowTokens;
            if (!used) {
                contextMeter.hidden = true;
                return;
            }

            contextMeter.hidden = false;
            const circumference = 50.27;
            if (windowTokens) {
                const pct = Math.min(100, Math.round((used / windowTokens) * 100));
                contextRing.setAttribute('stroke-dashoffset', String(circumference * (1 - pct / 100)));
                contextMeterLabel.textContent = pct + '%';
                contextMeter.classList.toggle('context-meter-warn', pct >= 80 && pct < 95);
                contextMeter.classList.toggle('context-meter-danger', pct >= 95);
                contextMeter.title = 'Context: ' + formatNumber(used) + ' of ' + formatNumber(windowTokens) + ' tokens (' + pct + '%)' +
                    (pct >= 80 ? ' — consider /compact' : '');
            } else {
                contextRing.setAttribute('stroke-dashoffset', String(circumference));
                contextMeterLabel.textContent = formatTokens(used);
                contextMeter.classList.remove('context-meter-warn', 'context-meter-danger');
                contextMeter.title = 'Context: ' + formatNumber(used) + ' tokens';
            }
        }

        function formatTokens(value) {
            if (value >= 1000) {
                return (value / 1000).toFixed(value >= 10000 ? 0 : 1) + 'k';
            }
            return String(value);
        }

        function renderSettingsSummary() {
            const provider = sessionInfo && sessionInfo.providerName ? sessionInfo.providerName : 'No provider';
            const model = sessionInfo && sessionInfo.modelId ? sessionInfo.modelId : 'No model';
            const profile = sessionInfo && sessionInfo.agentProfileName ? sessionInfo.agentProfileName : 'No profile';
            const thinking = sessionInfo && sessionInfo.thinkingMode ? sessionInfo.thinkingMode : 'default';

            settingsSummary.textContent = provider + ' / ' + model + ' / ' + profile + ' / thinking ' + thinking;
            settingsProviderValue.textContent = provider;
            settingsModelValue.textContent = model;
            settingsProfileValue.textContent = profile;
            settingsThinkingValue.textContent = thinking;
        }

        function updateSettingsActionState() {
            const disabled = promptState.isRunning === true;
            settingsPage.querySelectorAll('.settings-action').forEach(button => {
                button.disabled = disabled;
            });
        }

        function renderProfileSelect() {
            const profiles = sessionInfo && Array.isArray(sessionInfo.availableAgentProfiles)
                ? sessionInfo.availableAgentProfiles
                : [];
            const renderedProfiles = profiles.length > 0 ? profiles : fallbackProfiles;
            const profileName = sessionInfo && sessionInfo.agentProfileName
                ? sessionInfo.agentProfileName
                : getDefaultProfileName(renderedProfiles);
            profileSelect.textContent = '';
            renderedProfiles.forEach(profile => {
                const option = document.createElement('option');
                option.value = profile.name;
                option.textContent = profile.name;
                if (profile.description) {
                    option.title = profile.description;
                }
                option.selected = profile.name.toLowerCase() === profileName.toLowerCase();
                profileSelect.appendChild(option);
            });

            if (!renderedProfiles.some(profile => profile.name.toLowerCase() === profileName.toLowerCase())) {
                const option = document.createElement('option');
                option.value = profileName;
                option.textContent = profileName;
                option.selected = true;
                profileSelect.insertBefore(option, profileSelect.firstChild);
            }

            profileSelect.value = profileName;
            const activeProfile = renderedProfiles.find(profile =>
                profile.name.toLowerCase() === profileName.toLowerCase());
            profileSelect.title = activeProfile && activeProfile.description
                ? profileName + ' - ' + activeProfile.description
                : 'Profile: ' + profileName;
        }

        function renderModelSelect(models) {
            const activeModelId = sessionInfo && sessionInfo.modelId ? sessionInfo.modelId : '';
            const effort = activeReasoningLevel();
            modelButtonLabel.textContent = (activeModelId || 'Model') + (effort ? ' · ' + effort.label : '');
            modelButton.title = models.length > 0
                ? 'Model, reasoning & thinking. Current: ' + (activeModelId || models[0])
                : 'Model, reasoning & thinking';
            if (!agentMenu.classList.contains('hidden')) {
                renderAgentMenu();
            }
        }

        function activeReasoningLevel() {
            const effort = sessionInfo && sessionInfo.reasoningEffort
                ? String(sessionInfo.reasoningEffort).toLowerCase()
                : '';
            return REASONING_LEVELS.find(level => level.value === effort) || null;
        }

        function thinkingOn() {
            return String(sessionInfo && sessionInfo.thinkingMode || '').toLowerCase() === 'on';
        }

        function toggleAgentMenu() {
            if (agentMenu.classList.contains('hidden')) {
                openAgentMenu();
            } else {
                closeAgentMenu();
            }
        }

        function openAgentMenu() {
            if (!sessionInfo) {
                post({ command: 'selectModel' });
                return;
            }
            renderAgentMenu();
            agentMenu.classList.remove('hidden');
            modelButton.setAttribute('aria-expanded', 'true');
            positionAgentMenu();
            const checked = agentMenu.querySelector('.agent-menu-item.checked') as HTMLElement || agentMenu.querySelector('.agent-menu-item') as HTMLElement;
            if (checked) {
                checked.focus();
            }
            setTimeout(() => document.addEventListener('mousedown', handleAgentMenuOutside), 0);
        }

        function positionAgentMenu() {
            const rect = modelButton.getBoundingClientRect();
            const width = Math.min(280, window.innerWidth - 16);
            agentMenu.style.width = width + 'px';
            agentMenu.style.left = Math.max(8, Math.min(rect.left, window.innerWidth - width - 8)) + 'px';
            agentMenu.style.bottom = (window.innerHeight - rect.top + 6) + 'px';
            agentMenu.style.maxHeight = Math.max(160, rect.top - 16) + 'px';
        }

        function closeAgentMenu() {
            agentMenu.classList.add('hidden');
            modelButton.setAttribute('aria-expanded', 'false');
            document.removeEventListener('mousedown', handleAgentMenuOutside);
        }

        function handleAgentMenuOutside(event) {
            if (!agentMenu.contains(event.target) && event.target !== modelButton && !modelButton.contains(event.target)) {
                closeAgentMenu();
            }
        }

        agentMenu.addEventListener('keydown', event => {
            const items = Array.from(agentMenu.querySelectorAll('.agent-menu-item')) as HTMLElement[];
            const index = items.indexOf(document.activeElement as HTMLElement);
            if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
                event.preventDefault();
                const next = items[(index + (event.key === 'ArrowDown' ? 1 : -1) + items.length) % items.length];
                if (next) {
                    next.focus();
                }
            } else if (event.key === 'Tab') {
                closeAgentMenu();
            }
        });

        function addMenuHeader(text) {
            const header = document.createElement('div');
            header.className = 'agent-menu-header';
            header.textContent = text;
            agentMenu.appendChild(header);
        }

        function addMenuItem(label, checked, onClick, description?: string) {
            const item = document.createElement('button');
            item.type = 'button';
            item.className = 'agent-menu-item' + (checked ? ' checked' : '');
            item.setAttribute('role', 'menuitemradio');
            item.setAttribute('aria-checked', checked ? 'true' : 'false');
            const check = document.createElement('span');
            check.className = 'agent-menu-check';
            if (checked) {
                check.appendChild(iconElement('check'));
            }
            const text = document.createElement('span');
            text.className = 'agent-menu-label';
            text.textContent = label;
            item.appendChild(check);
            item.appendChild(text);
            if (description) {
                const desc = document.createElement('span');
                desc.className = 'agent-menu-description';
                desc.textContent = description;
                item.appendChild(desc);
            }
            item.addEventListener('click', () => {
                closeAgentMenu();
                inputField.focus();
                if (!checked) {
                    onClick();
                }
            });
            return item;
        }

        function addMenuSeparator() {
            const sep = document.createElement('div');
            sep.className = 'agent-menu-sep';
            agentMenu.appendChild(sep);
        }

        function renderAgentMenu() {
            agentMenu.textContent = '';

            addMenuHeader('Reasoning');
            const activeEffort = activeReasoningLevel();
            REASONING_LEVELS.forEach(level => {
                agentMenu.appendChild(addMenuItem(
                    level.label,
                    activeEffort && activeEffort.value === level.value,
                    () => post({ command: 'runSessionCommand', text: '/reasoning ' + level.value })
                ));
            });

            addMenuSeparator();
            addMenuHeader('Thinking');
            const isOn = thinkingOn();
            agentMenu.appendChild(addMenuItem('Thinking on', isOn,
                () => post({ command: 'runSessionCommand', text: '/thinking on' })));
            agentMenu.appendChild(addMenuItem('Thinking off', !isOn,
                () => post({ command: 'runSessionCommand', text: '/thinking off' })));

            const models = getAvailableModelIds();
            if (models.length > 0) {
                addMenuSeparator();
                addMenuHeader('Model');
                const activeModelId = sessionInfo && sessionInfo.modelId ? sessionInfo.modelId : '';
                const modelGroup = document.createElement('div');
                modelGroup.className = 'agent-menu-models';
                models.forEach(modelId => {
                    modelGroup.appendChild(addMenuItem(modelId, modelId === activeModelId,
                        () => post({ command: 'changeModel', modelId })));
                });
                agentMenu.appendChild(modelGroup);
            }
        }

        function getDefaultProfileName(profiles) {
            const buildProfile = profiles.find(profile =>
                String(profile.name || '').toLowerCase() === 'build');
            return buildProfile ? buildProfile.name : 'build';
        }

        function formatNumber(value) {
            return new Intl.NumberFormat('en-US').format(value);
        }

        let planEntries = [];
        let changedFiles = [];

        function renderPlan(plan) {
            const entries = plan && Array.isArray(plan.entries) ? plan.entries : [];
            planEntries = entries;
            hasPlanActivity = entries.length > 0;
            planList.textContent = '';
            const done = entries.filter(entry => normalizeClass(entry.status) === 'completed').length;
            planCount.textContent = entries.length > 0 ? done + ' / ' + entries.length : '';

            entries.forEach(entry => {
                const status = normalizeClass(entry.status || 'pending');
                const item = document.createElement('div');
                item.className = 'plan-item ' + status;
                item.appendChild(iconElement(
                    status === 'completed' ? 'circleCheck' : status === 'in_progress' ? 'circleDot' : 'circle',
                    'plan-dot'));

                const text = document.createElement('div');
                text.className = 'plan-content';
                text.textContent = entry.content;
                text.title = (entry.status || 'pending') + ': ' + entry.content;
                item.appendChild(text);
                planList.appendChild(item);
            });

            updateActivityVisibility();
        }

        function renderFileEditsSummary(summary) {
            const files = summary && Array.isArray(summary.files) ? summary.files : [];
            changedFiles = files;
            hasChangedFiles = files.length > 0;
            changedFilesList.textContent = '';

            let totalAdded = 0;
            let totalRemoved = 0;
            files.forEach(file => {
                const added = file.addedLineCount || 0;
                const removed = file.removedLineCount || 0;
                totalAdded += added;
                totalRemoved += removed;

                const row = document.createElement('div');
                row.className = 'changed-file-row';
                const action = String(file.action || 'Edited');
                const badge = document.createElement('span');
                badge.className = 'changed-file-badge changed-file-action-' + normalizeClass(action);
                badge.textContent = action.charAt(0).toUpperCase();
                badge.title = action;
                row.appendChild(badge);

                const display = String(file.displayPath || file.absolutePath || '');
                const parts = display.split(/[\\/]/);
                const name = parts.pop() || display;
                const link = createFileReferenceLink(name, file.absolutePath || display);
                link.classList.add('changed-file-name');
                link.title = 'Open ' + display;
                row.appendChild(link);

                const dir = document.createElement('span');
                dir.className = 'changed-file-dir';
                dir.textContent = parts.join('/');
                row.appendChild(dir);

                const stats = document.createElement('span');
                stats.className = 'changed-file-stats';
                const add = document.createElement('span');
                add.className = 'stat-added';
                add.textContent = '+' + added;
                const del = document.createElement('span');
                del.className = 'stat-removed';
                del.textContent = '−' + removed;
                stats.appendChild(add);
                stats.appendChild(del);
                row.appendChild(stats);

                changedFilesList.appendChild(row);
            });

            changedFilesCount.textContent = files.length > 0 ? '+' + totalAdded + ' −' + totalRemoved : '';
            updateActivityVisibility();
        }

        function setToolCall(toolCall) {
            if (!toolCall || !toolCall.toolCallId) {
                return;
            }

            const current = toolCalls.get(toolCall.toolCallId) || {};
            const merged = Object.assign({}, current, toolCall);
            if (!toolCall.content || toolCall.content.length === 0) {
                merged.content = current.content;
            }
            if (typeof toolCall.rawInput === 'undefined') {
                merged.rawInput = current.rawInput;
            }
            toolCalls.set(toolCall.toolCallId, merged);
            trackAgentThreads(merged);
            renderToolMessage(merged);
            renderAgentThreads();
            updateStatusRail();
        }

        function renderTools() {
            const calls = Array.from(toolCalls.values());
            calls.forEach(renderToolMessage);
            updateStatusRail();
        }

        function renderToolMessage(call) {
            if (!call || !call.toolCallId) {
                return;
            }

            if (activeReasoningMessage) {
                finalizeReasoning(activeReasoningMessage);
            }
            activeReasoningMessage = null;
            activeAssistantMessage = null;
            let article = toolMessageElements.get(call.toolCallId);
            if (!article) {
                article = appendMessage('', 'tool');
                toolMessageElements.set(call.toolCallId, article);
            }

            const text = formatToolMessage(call);
            const body = article.querySelector('.message-text');
            if (!body) {
                return;
            }

            const diffModel = getToolDiffModel(call);
            const previousDetails = body.querySelector('.tool-call-details');
            const shouldOpen = previousDetails ? previousDetails.open : shouldOpenToolCallByDefault(call, diffModel);
            article.className = 'message-card tool ' + normalizeClass(call.status || 'pending');
            article.dataset.text = text;
            body.textContent = '';
            body.appendChild(createToolMessageView(call, shouldOpen, diffModel));
            const status = normalizeClass(call.status || 'pending');
            if (status === 'pending' || status === 'in_progress') {
                setProgressLabel(describeToolProgress(getToolCallPresentation(call, diffModel)));
            } else if (!Array.from(toolCalls.values()).some(other => /^(pending|in_progress)$/.test(normalizeClass(other.status || 'pending')))) {
                setProgressLabel('Working');
            }
            moveProgressIndicatorToEnd();
            scrollToBottom();
        }

        function formatToolMessage(call) {
            const lines = [];
            const title = call.title || call.toolCallId;
            const status = call.status || 'pending';
            lines.push('Tool - ' + status);
            lines.push(title);

            if (call.kind) {
                lines.push('Kind: ' + call.kind);
            }

            if (typeof call.rawInput !== 'undefined') {
                lines.push('');
                lines.push('Arguments');
                lines.push(formatPayload(call.rawInput));
            }

            const output = Array.isArray(call.content) ? call.content.join('\n') : '';
            if (output) {
                lines.push('');
                lines.push('Output');
                lines.push(output);
            }

            return lines.join('\n');
        }

        function createReasoningDetails(text, open, streaming?: boolean) {
            const view = createInlineDisclosure({
                title: streaming ? 'Thinking…' : 'Thought process',
                meta: text ? summarizeLineCount(text) : '',
                icon: streaming ? 'spinner' : 'brain',
                className: 'thinking-details',
                open
            });

            const markdown = document.createElement('div');
            markdown.className = 'thinking-markdown';
            renderMarkdownText(markdown, text);
            view.content.appendChild(markdown);
            return view.details;
        }

        const PROGRESS_VERBS = {
            Read: 'Reading', Search: 'Searching', Edit: 'Editing', Write: 'Writing', Create: 'Creating', Delete: 'Deleting',
            Move: 'Moving', Run: 'Running', Fetch: 'Fetching', List: 'Listing', Think: 'Thinking', Agent: 'Delegating'
        };

        function describeToolProgress(presentation) {
            const verb = PROGRESS_VERBS[presentation.title];
            if (!verb) {
                return 'Working';
            }
            const target = String(presentation.target || '').split('\n')[0];
            const short = target.length > 48 ? target.slice(0, 47) + '…' : target;
            return short ? verb + ' ' + short : verb;
        }

        function getToolStatusIcon(status) {
            const normalized = normalizeClass(status || 'pending');
            if (normalized === 'completed' || normalized === 'success' || normalized === 'succeeded') {
                return 'check';
            }
            if (normalized === 'failed' || normalized === 'error' || normalized === 'rejected' || normalized === 'cancelled') {
                return 'error';
            }
            return 'spinner';
        }

        function createToolMessageView(call, open, diffModel) {
            const presentation = getToolCallPresentation(call, diffModel);
            const totals = diffModel.length > 0 ? countDiffTotals(diffModel) : null;
            const view = createInlineDisclosure({
                title: presentation.title,
                target: presentation.target,
                targetPath: presentation.targetPath,
                stats: totals,
                statusIcon: getToolStatusIcon(call.status),
                status: call.status,
                icon: presentation.icon,
                className: 'tool-call-details',
                open
            });
            const container = document.createElement('div');
            container.className = 'tool-message';

            const output = Array.isArray(call.content) ? call.content.join('\n') : '';
            if (diffModel.length > 0) {
                diffModel.forEach(file => container.appendChild(createDiffView(file)));
                const extraOutput = stripDiffLikeOutput(output);
                if (extraOutput) {
                    container.appendChild(createVsCodeToolOutputBlock(call, extraOutput));
                }
            } else {
                if (output) {
                    container.appendChild(createToolOutputView(call, output));
                } else {
                    const pending = document.createElement('div');
                    pending.className = 'tool-pending';
                    const status = normalizeClass(call.status || 'pending');
                    pending.textContent = status === 'pending' || status === 'in_progress' ? 'Running…' : 'No output.';
                    container.appendChild(pending);
                }

                if (typeof call.rawInput !== 'undefined') {
                    const details = createDetails('Input', formatPayload(call.rawInput), false);
                    details.className = 'tool-arguments';
                    container.appendChild(details);
                }
            }

            view.content.appendChild(container);
            return view.details;
        }

        function getToolDiffModel(call) {
            let model = [];
            try {
                model = normalizeDiffModel(buildDiffModel(call));
            } catch (error) {
                model = [];
            }

            if (model.length > 0) {
                return model;
            }

            return createFallbackFileDiffModel(call);
        }

        function createToolOutputView(call, output) {
            if (isReadFileToolCall(call)) {
                const readOutput = normalizeReadFileToolOutput(call, output);
                return createVsCodeCodeBlock({
                    path: readOutput.path,
                    title: readOutput.title,
                    content: readOutput.content,
                    language: readOutput.language,
                    className: 'read-file-output',
                    copyTitle: 'Copy file contents'
                });
            }

            return createVsCodeToolOutputBlock(call, output);
        }

        function normalizeReadFileToolOutput(call, output) {
            const extracted = extractFencedCodePayload(output);
            const path = inferToolFilePath(call) || inferReadOutputPath(output);
            const content = extracted ? extracted.content : String(output || '');
            const language = extracted && extracted.language
                ? extracted.language
                : inferLanguageFromPath(path);

            return {
                path,
                title: path ? path : 'Read file output',
                content,
                language: language || 'text'
            };
        }

        function extractFencedCodePayload(output) {
            const text = String(output || '').replace(/\r\n/g, '\n');
            const trimmed = text.trim();
            if (!trimmed) {
                return null;
            }

            const backtickFence = String.fromCharCode(96).repeat(3);
            const exactFence = readDelimitedCodeFence(trimmed, backtickFence) || readDelimitedCodeFence(trimmed, '~~~');
            if (exactFence) {
                return exactFence;
            }

            const labelMatch = trimmed.match(/^(?:read|opened|contents?|content|file|output)[^\n]*:\s*\n([\s\S]+)$/i);
            if (labelMatch) {
                return readDelimitedCodeFence(labelMatch[1].trim(), backtickFence)
                    || readDelimitedCodeFence(labelMatch[1].trim(), '~~~');
            }

            return null;
        }

        function readDelimitedCodeFence(text, marker) {
            const value = String(text || '');
            if (!value.startsWith(marker)) {
                return null;
            }

            const firstLineEnd = value.indexOf('\n');
            if (firstLineEnd < 0) {
                return null;
            }

            const firstLine = value.slice(marker.length, firstLineEnd).trim();
            const rest = value.slice(firstLineEnd + 1);
            const closing = '\n' + marker;
            const closingIndex = rest.lastIndexOf(closing);
            if (closingIndex < 0 || rest.slice(closingIndex + closing.length).trim()) {
                return null;
            }

            return {
                language: normalizeLanguageName(firstLine.split(/\s+/)[0] || ''),
                content: rest.slice(0, closingIndex)
            };
        }

        function inferReadOutputPath(output) {
            const text = String(output || '');
            const patterns = [
                /(?:read|opened|contents? of|file|path):\s*([^\n]+)/i,
                /^\s*[-•]?\s*file:\s+([^\n]+)/im
            ];

            for (const pattern of patterns) {
                const match = text.match(pattern);
                if (match) {
                    const path = trimFileReference(match[1].trim());
                    if (path && isLikelyFileReference(path, path, 0)) {
                        return path;
                    }
                }
            }

            return '';
        }

        function createVsCodeToolOutputBlock(call, output) {
            const presentation = getToolOutputBlockPresentation(call, output);
            return createVsCodeCodeBlock({
                title: presentation.title,
                content: output,
                language: presentation.language,
                className: 'tool-output ' + presentation.className,
                lineNumbers: false,
                syntaxHighlight: false,
                linkify: true,
                copyTitle: 'Copy tool output'
            });
        }

        function getToolOutputBlockPresentation(call, output) {
            const haystack = getToolCallHaystack(call);

            if (isListToolCall(call) || looksLikeDirectoryListOutput(output)) {
                return { title: 'File listing', language: 'list', className: 'listing-output' };
            }

            if (/search|grep|find|match|rg|ripgrep/.test(haystack)) {
                return { title: 'Search results', language: 'results', className: 'search-output' };
            }

            if (/terminal|shell|bash|zsh|powershell|exec|spawn|run command|command/.test(haystack)) {
                return { title: 'Terminal output', language: 'shell', className: 'terminal-output' };
            }

            if (/write|create|edit|patch|update|delete|rename|move|copy/.test(haystack)) {
                return { title: 'Operation output', language: 'output', className: 'operation-output' };
            }

            return { title: 'Tool output', language: 'output', className: 'generic-output' };
        }

        function isListToolCall(call) {
            const haystack = getToolCallHaystack(call);
            return /(^|\b)(list|ls|tree|directory|workspace files|files)(\b|$)/.test(haystack);
        }

        function looksLikeDirectoryListOutput(output) {
            const text = String(output || '');
            return /(^|\n)\s*[-•]\s*(file|directory):\s+/i.test(text)
                || /(^|\n)\s*[-•]?\s*listed\b.*\(\d+\s+entr(?:y|ies)\)/i.test(text);
        }

        function getToolCallHaystack(call) {
            if (!call) {
                return '';
            }

            return [
                call.title,
                call.kind,
                formatPayload(call.rawInput || '')
            ].map(value => String(value || '').toLowerCase()).join(' ');
        }

        function createToolOutputPre(text) {
            const pre = document.createElement('pre');
            pre.className = 'tool-output-pre';
            renderLinkifiedText(pre, text);
            return pre;
        }

        function isReadFileToolCall(call) {
            if (!call) {
                return false;
            }

            const haystack = getToolCallHaystack(call);

            if (/\b(list|ls|tree|search|grep|find|scan|glob)\b/.test(haystack)) {
                return false;
            }

            if (/\b(read_file|read-file|readfile|fs_read|fs\.read|read_file_system|filesystem[._:-]read|filesystem.*readfile)\b/.test(haystack)) {
                return true;
            }

            if (/\b(read|open|cat|show|view)\s+(?:a\s+)?(?:workspace\s+)?file\b/.test(haystack)) {
                return true;
            }

            if (/\bcat\s+[^\n]+/.test(haystack) && hasLikelyToolFilePath(call)) {
                return true;
            }

            return /\bread\b/.test(haystack) && hasLikelyToolFilePath(call);
        }

        function hasLikelyToolFilePath(call) {
            return Boolean(inferToolFilePath(call));
        }

        function inferToolFilePath(call) {
            const rawInput = call && call.rawInput && typeof call.rawInput === 'object'
                ? call.rawInput
                : null;

            if (rawInput) {
                const directPath = firstStringValueDeep(rawInput, [
                    'path',
                    'file',
                    'filename',
                    'fileName',
                    'file_name',
                    'filePath',
                    'filepath',
                    'file_path',
                    'absolutePath',
                    'absolute_path',
                    'relativePath',
                    'relative_path',
                    'target',
                    'targetFile',
                    'target_file',
                    'uri'
                ]);
                if (directPath) {
                    return trimFileReference(directPath);
                }
            }

            const values = [call && call.title, call && call.kind, formatPayload(rawInput || '')];
            for (const value of values) {
                const path = firstFileReferenceInText(value);
                if (path) {
                    return trimFileReference(path);
                }
            }

            return '';
        }

        function firstFileReferenceInText(text) {
            fileReferencePattern.lastIndex = 0;
            const match = fileReferencePattern.exec(String(text || ''));
            fileReferencePattern.lastIndex = 0;
            return match ? match[2] : '';
        }

        function createVsCodeCodeBlock(options) {
            const path = options && options.path ? String(options.path) : '';
            const titleText = options && options.title ? String(options.title) : 'Read file output';
            const content = String((options && options.content) || '');
            const language = options && options.language ? String(options.language) : inferLanguageFromPath(path);
            const lines = splitCodeLines(content);
            const showLineNumbers = !options || options.lineNumbers !== false;
            const syntaxHighlight = !options || options.syntaxHighlight !== false;
            const linkify = Boolean(options && options.linkify);
            const extraClassName = options && options.className ? ' ' + String(options.className).trim() : '';

            const wrap = document.createElement('div');
            wrap.className = ('vscode-code-block' + extraClassName).trim();

            const header = document.createElement('div');
            header.className = 'vscode-code-header';

            const headerMain = document.createElement('div');
            headerMain.className = 'vscode-code-header-main';
            if (path) {
                headerMain.appendChild(createFileReferenceLink(path, path));
            } else {
                const title = document.createElement('span');
                title.className = 'vscode-code-title';
                title.textContent = titleText;
                headerMain.appendChild(title);
            }
            header.appendChild(headerMain);

            const actions = document.createElement('div');
            actions.className = 'vscode-code-actions';

            if (language) {
                const lang = document.createElement('span');
                lang.className = 'vscode-code-language';
                lang.textContent = language;
                actions.appendChild(lang);
            }

            const lineCount = document.createElement('span');
            lineCount.className = 'vscode-code-line-count';
            lineCount.textContent = lines.length + (lines.length === 1 ? ' line' : ' lines');
            actions.appendChild(lineCount);

            const copyButton = document.createElement('button');
            copyButton.type = 'button';
            copyButton.className = 'vscode-code-copy-button';
            copyButton.title = options && options.copyTitle ? String(options.copyTitle) : 'Copy output';
            copyButton.setAttribute('aria-label', copyButton.title);
            setIconContent(copyButton, 'copy');
            copyButton.addEventListener('click', event => {
                event.preventDefault();
                event.stopPropagation();
                copyTextToClipboard(content);
                flashCopied(copyButton);
            });
            actions.appendChild(copyButton);
            header.appendChild(actions);
            wrap.appendChild(header);

            const pre = document.createElement('pre');
            pre.className = 'vscode-code-body';
            const renderCodeRow = (line, index) => {
                const row = document.createElement('div');
                row.className = showLineNumbers ? 'vscode-code-line' : 'vscode-code-line no-line-number';

                if (showLineNumbers) {
                    const lineNumber = document.createElement('span');
                    lineNumber.className = 'vscode-code-line-number';
                    lineNumber.textContent = String(index + 1);
                    row.appendChild(lineNumber);
                }

                const code = document.createElement('span');
                code.className = 'vscode-code-line-content';
                if (linkify) {
                    appendToolOutputLine(code, line);
                } else if (syntaxHighlight) {
                    appendSyntaxHighlightedLine(code, line, language);
                } else {
                    code.textContent = line;
                }
                row.appendChild(code);

                pre.appendChild(row);
            };
            appendCappedRows(wrap, pre, lines, renderCodeRow);
            return wrap;
        }

        const MAX_INITIAL_ROWS = 300;

        // Renders up to MAX_INITIAL_ROWS rows, then a "show more" button that renders the rest on demand.
        function appendCappedRows(wrap, pre, items, renderRow) {
            const initial = Math.min(items.length, MAX_INITIAL_ROWS);
            for (let index = 0; index < initial; index += 1) {
                renderRow(items[index], index);
            }
            wrap.appendChild(pre);
            if (items.length > initial) {
                const more = document.createElement('button');
                more.type = 'button';
                more.className = 'show-more-button';
                more.textContent = 'Show ' + (items.length - initial) + ' more lines';
                more.addEventListener('click', event => {
                    event.preventDefault();
                    for (let index = initial; index < items.length; index += 1) {
                        renderRow(items[index], index);
                    }
                    more.remove();
                });
                wrap.appendChild(more);
            }
        }

        function appendToolOutputLine(container, line) {
            const text = String(line || '');
            const entryMatch = text.match(/^(\s*[-•]\s*)(directory|file):\s+(.+)$/i);
            if (entryMatch) {
                appendSyntaxSpan(container, entryMatch[1], 'syntax-muted');
                appendSyntaxSpan(container, entryMatch[2], 'syntax-property');
                container.appendChild(document.createTextNode(': '));
                const path = trimFileReference(entryMatch[3].trim());
                if (path) {
                    container.appendChild(createFileReferenceLink(path, path));
                }
                return;
            }

            const summaryMatch = text.match(/^(\s*[-•]?\s*)(listed|found|matched|created|updated|deleted|read|wrote)(\b.*)$/i);
            if (summaryMatch) {
                appendSyntaxSpan(container, summaryMatch[1], 'syntax-muted');
                appendSyntaxSpan(container, summaryMatch[2], 'syntax-keyword');
                appendLinkifiedText(container, summaryMatch[3]);
                return;
            }

            const moreMatch = text.match(/^(\s*\.{3}\s*\+\d+\s+.+)$/);
            if (moreMatch) {
                appendSyntaxSpan(container, moreMatch[1], 'syntax-muted');
                return;
            }

            appendLinkifiedText(container, text);
        }

        function splitCodeLines(text) {
            const lines = String(text || '').replace(/\r\n/g, '\n').split('\n');
            if (lines.length > 1 && lines[lines.length - 1] === '') {
                lines.pop();
            }
            return lines.length > 0 ? lines : [''];
        }

        function normalizeLanguageName(language) {
            const value = String(language || '').trim().toLowerCase();
            const aliases = {
                js: 'javascript',
                jsx: 'javascript',
                mjs: 'javascript',
                cjs: 'javascript',
                ts: 'typescript',
                tsx: 'typescript',
                py: 'python',
                rb: 'ruby',
                cs: 'csharp',
                sh: 'shell',
                bash: 'shell',
                zsh: 'shell',
                ps1: 'powershell',
                yml: 'yaml',
                md: 'markdown'
            };
            return aliases[value] || value;
        }

        function inferLanguageFromPath(path) {
            const ext = String(path || '').split(/[?#]/)[0].split('.').pop().toLowerCase();
            const languages = {
                js: 'javascript',
                jsx: 'javascript',
                mjs: 'javascript',
                cjs: 'javascript',
                ts: 'typescript',
                tsx: 'typescript',
                json: 'json',
                jsonc: 'json',
                py: 'python',
                rb: 'ruby',
                go: 'go',
                rs: 'rust',
                java: 'java',
                c: 'c',
                h: 'c',
                cpp: 'cpp',
                cc: 'cpp',
                cxx: 'cpp',
                hpp: 'cpp',
                cs: 'csharp',
                php: 'php',
                html: 'html',
                htm: 'html',
                xml: 'xml',
                css: 'css',
                scss: 'scss',
                less: 'less',
                md: 'markdown',
                sh: 'shell',
                bash: 'shell',
                zsh: 'shell',
                fish: 'shell',
                ps1: 'powershell',
                yml: 'yaml',
                yaml: 'yaml',
                toml: 'toml',
                ini: 'ini',
                sql: 'sql'
            };
            return normalizeLanguageName(languages[ext] || ext || 'text');
        }

        function appendSyntaxHighlightedLine(container, line, language) {
            const text = String(line || '');
            const normalizedLanguage = normalizeLanguageName(language);

            if (normalizedLanguage === 'markdown') {
                appendMarkdownSyntaxHighlightedLine(container, text);
                return;
            }

            const commentStart = findSyntaxCommentStart(text, normalizedLanguage);
            const codeText = commentStart >= 0 ? text.slice(0, commentStart) : text;
            const commentText = commentStart >= 0 ? text.slice(commentStart) : '';

            appendSyntaxCodeTokens(container, codeText, normalizedLanguage);
            if (commentText) {
                appendSyntaxSpan(container, commentText, 'syntax-comment');
            }
        }

        function appendMarkdownSyntaxHighlightedLine(container, line) {
            const text = String(line || '');
            const headingMatch = text.match(/^(\s{0,3}#{1,6}\s+)(.*)$/);
            if (headingMatch) {
                appendSyntaxSpan(container, headingMatch[1], 'syntax-keyword');
                appendSyntaxCodeTokens(container, headingMatch[2], 'markdown');
                return;
            }

            const quoteMatch = text.match(/^(\s*>+\s?)(.*)$/);
            if (quoteMatch) {
                appendSyntaxSpan(container, quoteMatch[1], 'syntax-comment');
                appendSyntaxCodeTokens(container, quoteMatch[2], 'markdown');
                return;
            }

            const listMatch = text.match(/^(\s*)([-*+]\s+|\d+\.\s+)(.*)$/);
            if (listMatch) {
                container.appendChild(document.createTextNode(listMatch[1]));
                appendSyntaxSpan(container, listMatch[2], 'syntax-keyword');
                appendSyntaxCodeTokens(container, listMatch[3], 'markdown');
                return;
            }

            const backtickFence = String.fromCharCode(96).repeat(3);
            if (text.trim().startsWith(backtickFence) || text.trim().startsWith('~~~')) {
                appendSyntaxSpan(container, text, 'syntax-keyword');
                return;
            }

            appendSyntaxCodeTokens(container, text, 'markdown');
        }

        function findSyntaxCommentStart(text, language) {
            const value = String(text || '');
            if (!value) {
                return -1;
            }

            if (language === 'html' || language === 'xml' || language === 'markdown') {
                return value.indexOf('<!--');
            }

            if (/python|ruby|shell|powershell|yaml|toml|ini/.test(language)) {
                return value.indexOf('#');
            }

            if (/css|scss|less/.test(language)) {
                return value.indexOf('/*');
            }

            return value.indexOf('//');
        }

        function appendSyntaxCodeTokens(container, text, language) {
            const tokenPattern = /("(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'|\b[A-Za-z_$][\w$]*\b|\b\d+(?:\.\d+)?\b)/g;
            let lastIndex = 0;
            let match = tokenPattern.exec(text);

            while (match) {
                if (match.index > lastIndex) {
                    container.appendChild(document.createTextNode(text.slice(lastIndex, match.index)));
                }

                const token = match[0];
                const className = getSyntaxTokenClass(token, language, text.slice(match.index + token.length));
                if (className) {
                    appendSyntaxSpan(container, token, className);
                } else {
                    container.appendChild(document.createTextNode(token));
                }

                lastIndex = match.index + token.length;
                match = tokenPattern.exec(text);
            }

            if (lastIndex < text.length) {
                container.appendChild(document.createTextNode(text.slice(lastIndex)));
            }
        }

        function getSyntaxTokenClass(token, language, tail) {
            if (/^['"]/.test(token)) {
                return 'syntax-string';
            }

            if (/^\d/.test(token)) {
                return 'syntax-number';
            }

            const word = token.toLowerCase();
            const keywords = syntaxKeywordsForLanguage(language);
            if (keywords.has(word)) {
                return 'syntax-keyword';
            }

            if (/^(true|false|null|undefined|none|nil)$/i.test(token)) {
                return 'syntax-constant';
            }

            if (/^\s*\(/.test(tail || '')) {
                return 'syntax-function';
            }

            if (/^\s*:/.test(tail || '') || /^\s*=/.test(tail || '')) {
                return 'syntax-property';
            }

            return '';
        }

        function syntaxKeywordsForLanguage(language) {
            const base = ['break', 'case', 'catch', 'class', 'const', 'continue', 'default', 'delete', 'do', 'else', 'enum', 'export', 'extends', 'finally', 'for', 'from', 'function', 'if', 'import', 'in', 'instanceof', 'let', 'new', 'return', 'static', 'super', 'switch', 'this', 'throw', 'try', 'typeof', 'var', 'void', 'while', 'with', 'yield', 'async', 'await'];
            const python = ['and', 'as', 'assert', 'async', 'await', 'break', 'class', 'continue', 'def', 'del', 'elif', 'else', 'except', 'finally', 'for', 'from', 'global', 'if', 'import', 'in', 'is', 'lambda', 'nonlocal', 'not', 'or', 'pass', 'raise', 'return', 'try', 'while', 'with', 'yield'];
            const csharp = ['abstract', 'as', 'base', 'bool', 'byte', 'char', 'checked', 'decimal', 'delegate', 'double', 'event', 'explicit', 'extern', 'false', 'fixed', 'float', 'foreach', 'implicit', 'int', 'interface', 'internal', 'is', 'lock', 'long', 'namespace', 'object', 'operator', 'out', 'override', 'params', 'private', 'protected', 'public', 'readonly', 'ref', 'sbyte', 'sealed', 'short', 'sizeof', 'stackalloc', 'string', 'struct', 'true', 'uint', 'ulong', 'unchecked', 'unsafe', 'ushort', 'using', 'virtual'];
            const java = ['abstract', 'boolean', 'byte', 'char', 'double', 'final', 'float', 'implements', 'int', 'interface', 'long', 'native', 'package', 'private', 'protected', 'public', 'short', 'strictfp', 'synchronized', 'throws', 'transient', 'volatile'];
            const go = ['chan', 'defer', 'fallthrough', 'func', 'go', 'import', 'interface', 'map', 'package', 'range', 'select', 'struct', 'type'];
            const rust = ['as', 'crate', 'dyn', 'extern', 'fn', 'impl', 'loop', 'match', 'mod', 'move', 'mut', 'pub', 'ref', 'self', 'Self', 'trait', 'type', 'unsafe', 'use', 'where'];
            const css = ['align-items', 'background', 'border', 'color', 'display', 'flex', 'font', 'gap', 'grid', 'height', 'margin', 'overflow', 'padding', 'position', 'width'];
            const sql = ['select', 'from', 'where', 'join', 'left', 'right', 'inner', 'outer', 'insert', 'update', 'delete', 'create', 'alter', 'drop', 'table', 'group', 'order', 'by', 'limit', 'having', 'and', 'or', 'not', 'null'];

            if (/python/.test(language)) {
                return new Set(python);
            }
            if (/csharp/.test(language)) {
                return new Set(base.concat(csharp));
            }
            if (/java/.test(language)) {
                return new Set(base.concat(java));
            }
            if (/go/.test(language)) {
                return new Set(base.concat(go));
            }
            if (/rust/.test(language)) {
                return new Set(base.concat(rust));
            }
            if (/css|scss|less/.test(language)) {
                return new Set(css);
            }
            if (/sql/.test(language)) {
                return new Set(sql);
            }
            return new Set(base);
        }

        function appendSyntaxSpan(container, text, className) {
            const span = document.createElement('span');
            span.className = className;
            span.textContent = text;
            container.appendChild(span);
        }

        function normalizeDiffModel(model) {
            if (Array.isArray(model)) {
                return model
                    .map(normalizeDiffFile)
                    .filter(file => file && file.lines.length > 0);
            }

            if (model && Array.isArray(model.files)) {
                return model.files
                    .map(normalizeDiffFile)
                    .filter(file => file && file.lines.length > 0);
            }

            return [];
        }

        function normalizeDiffFile(file) {
            if (!file || !Array.isArray(file.lines)) {
                return null;
            }

            const path = String(file.path || file.file || file.name || 'Changed file');
            const lines = file.lines.map(line => {
                if (typeof line === 'string') {
                    return normalizeDiffLine(line);
                }

                if (!line || typeof line !== 'object') {
                    return null;
                }

                const type = normalizeDiffLineType(line.type || line.kind || line.status);
                const text = typeof line.text === 'string'
                    ? line.text
                    : typeof line.content === 'string'
                        ? line.content
                        : '';
                return Object.assign({}, line, { type, text });
            }).filter(Boolean);

            return { path, lines };
        }

        function normalizeDiffLine(line) {
            const text = String(line || '');
            if (text.startsWith('+') && !text.startsWith('+++')) {
                return { type: 'add', text: text.slice(1) };
            }
            if (text.startsWith('-') && !text.startsWith('---')) {
                return { type: 'del', text: text.slice(1) };
            }
            if (text.startsWith('@@')) {
                return { type: 'meta', text };
            }
            return { type: 'context', text: text.startsWith(' ') ? text.slice(1) : text };
        }

        function normalizeDiffLineType(type) {
            const value = String(type || '').toLowerCase();
            if (value === 'add' || value === 'added' || value === 'insert' || value === 'inserted' || value === '+') {
                return 'add';
            }
            if (value === 'del' || value === 'delete' || value === 'deleted' || value === 'remove' || value === 'removed' || value === '-') {
                return 'del';
            }
            if (value === 'meta' || value === 'header' || value === 'hunk') {
                return 'meta';
            }
            return 'context';
        }

        function createFallbackFileDiffModel(call) {
            const rawInput = call && call.rawInput && typeof call.rawInput === 'object'
                ? call.rawInput
                : null;
            const output = Array.isArray(call && call.content) ? call.content.join('\n') : '';
            const fallback = [];

            if (rawInput) {
                collectRawInputDiffs(rawInput, fallback);
            }

            if (fallback.length > 0) {
                return fallback;
            }

            const payloads = [];
            if (rawInput) {
                ['patch', 'diff', 'edits', 'input'].forEach(key => {
                    if (typeof rawInput[key] === 'string') {
                        payloads.push(rawInput[key]);
                    }
                });
            }
            if (output) {
                payloads.push(output);
            }

            for (const payload of payloads) {
                const parsed = parsePatchText(payload);
                if (parsed.length > 0) {
                    return parsed;
                }
            }

            return [];
        }

        function collectRawInputDiffs(rawInput, fallback) {
            if (!rawInput || typeof rawInput !== 'object') {
                return;
            }

            if (Array.isArray(rawInput.edits)) {
                rawInput.edits.forEach(edit => collectRawInputDiffs(edit, fallback));
            }

            const path = firstStringValue(rawInput, ['path', 'file', 'filePath', 'file_path', 'target', 'targetFile', 'target_file']);
            const content = firstStringValue(rawInput, ['content', 'text', 'newContent', 'new_content', 'body']);
            const patch = firstStringValue(rawInput, ['patch', 'diff']);

            if (patch) {
                parsePatchText(patch).forEach(file => fallback.push(file));
                return;
            }

            if (path && content && looksLikeFileWrite(rawInput)) {
                fallback.push({
                    path,
                    lines: splitDiffContent(content).map(line => ({ type: 'add', text: line }))
                });
            }
        }

        function firstStringValue(source, keys) {
            for (const key of keys) {
                if (typeof source[key] === 'string' && source[key].trim()) {
                    return source[key];
                }
            }
            return '';
        }

        function firstStringValueDeep(source, keys, depth) {
            if (!source || depth > 4) {
                return '';
            }

            const direct = !Array.isArray(source) && typeof source === 'object'
                ? firstStringValue(source, keys)
                : '';
            if (direct) {
                return direct;
            }

            const values = Array.isArray(source)
                ? source
                : typeof source === 'object'
                    ? Object.keys(source).map(key => source[key])
                    : [];

            for (const value of values) {
                if (!value || typeof value !== 'object') {
                    continue;
                }
                const found = firstStringValueDeep(value, keys, (depth || 0) + 1);
                if (found) {
                    return found;
                }
            }

            return '';
        }

        function looksLikeFileWrite(rawInput) {
            const op = String(rawInput.operation || rawInput.action || rawInput.command || rawInput.kind || '').toLowerCase();
            return !op || /write|create|add|save|replace|edit|update|patch/.test(op);
        }

        function parsePatchText(text) {
            const value = String(text || '');
            if (!value || (!value.includes('*** ') && !value.includes('@@') && !value.includes('diff --git'))) {
                return [];
            }

            if (value.includes('*** Begin Patch') || value.includes('*** Add File:') || value.includes('*** Update File:') || value.includes('*** Delete File:')) {
                return parseApplyPatchText(value);
            }

            return parseUnifiedDiffText(value);
        }

        function parseApplyPatchText(text) {
            const files = [];
            let current = null;
            String(text || '').split(/\r?\n/).forEach(line => {
                const fileMatch = line.match(/^\*\*\*\s+(Add|Update|Delete) File:\s+(.+)$/);
                if (fileMatch) {
                    current = {
                        path: fileMatch[2].trim(),
                        operation: fileMatch[1].toLowerCase(),
                        lines: []
                    };
                    files.push(current);
                    return;
                }

                if (!current || line === '*** Begin Patch' || line === '*** End Patch') {
                    return;
                }

                if (line.startsWith('@@')) {
                    current.lines.push({ type: 'meta', text: line });
                    return;
                }

                current.lines.push(normalizeDiffLine(line));
            });

            return files.filter(file => file.lines.length > 0);
        }

        function parseUnifiedDiffText(text) {
            const files = [];
            let current = null;
            String(text || '').split(/\r?\n/).forEach(line => {
                const gitMatch = line.match(/^diff --git\s+a\/(.+?)\s+b\/(.+)$/);
                if (gitMatch) {
                    current = { path: gitMatch[2].trim(), lines: [] };
                    files.push(current);
                    return;
                }

                const newFileMatch = line.match(/^\+\+\+\s+(?:b\/)?(.+)$/);
                if (newFileMatch && !line.includes('/dev/null')) {
                    if (!current) {
                        current = { path: newFileMatch[1].trim(), lines: [] };
                        files.push(current);
                    } else {
                        current.path = newFileMatch[1].trim();
                    }
                    return;
                }

                const oldFileMatch = line.match(/^---\s+(?:a\/)?(.+)$/);
                if (oldFileMatch && !line.includes('/dev/null') && !current) {
                    current = { path: oldFileMatch[1].trim(), lines: [] };
                    files.push(current);
                    return;
                }

                if (!current) {
                    return;
                }

                if (line.startsWith('@@')) {
                    current.lines.push({ type: 'meta', text: line });
                } else if (line.startsWith('+') && !line.startsWith('+++')) {
                    current.lines.push({ type: 'add', text: line.slice(1) });
                } else if (line.startsWith('-') && !line.startsWith('---')) {
                    current.lines.push({ type: 'del', text: line.slice(1) });
                } else if (line.startsWith(' ')) {
                    current.lines.push({ type: 'context', text: line.slice(1) });
                }
            });

            return files.filter(file => file.lines.length > 0);
        }

        function splitDiffContent(content) {
            const lines = String(content || '').replace(/\r\n/g, '\n').split('\n');
            if (lines.length > 1 && lines[lines.length - 1] === '') {
                lines.pop();
            }
            return lines;
        }

        function getToolCallPresentation(call, diffModel) {
            const title = String((call && call.title) || '').trim();
            const kind = String((call && call.kind) || '').trim().toLowerCase();
            const titleKind = (title + ' ' + kind).toLowerCase();
            const rawInput = call && call.rawInput && typeof call.rawInput === 'object' ? call.rawInput : null;
            const count = diffModel.length;
            const filePath = count === 1 ? diffModel[0].path : inferToolFilePath(call);
            const command = rawInput ? firstStringValue(rawInput, ['command', 'cmd', 'script', 'commandLine']) : '';
            const query = rawInput ? firstStringValue(rawInput, ['pattern', 'query', 'regex', 'glob', 'search']) : '';
            const url = rawInput ? firstStringValue(rawInput, ['url', 'uri', 'href']) : '';
            const fileTarget = filePath ? { target: shortenPath(filePath), targetPath: filePath } : { target: '', targetPath: '' };

            if (count > 0) {
                const totals = countDiffTotals(diffModel);
                const many = count > 1 ? { target: count + ' files', targetPath: '' } : fileTarget;
                if (totals.added > 0 && totals.removed === 0 && !/patch|update|edit|replace/.test(titleKind)) {
                    return Object.assign({ title: count === 1 ? 'Create' : 'Create', icon: 'newFile' }, many);
                }
                if (totals.removed > 0 && totals.added === 0 && /delete|remove/.test(titleKind)) {
                    return Object.assign({ title: 'Delete', icon: 'edit' }, many);
                }
                return Object.assign({ title: 'Edit', icon: 'edit' }, many);
            }

            if (kind === 'execute' || (command && /terminal|shell|bash|powershell|exec|run|command/.test(titleKind))) {
                return { title: 'Run', icon: 'terminal', target: command || title, targetPath: '' };
            }
            if (kind === 'search' || /search|grep|glob|find|ripgrep/.test(titleKind)) {
                return { title: 'Search', icon: 'search', target: query || title, targetPath: '' };
            }
            if (kind === 'fetch' || url) {
                return { title: 'Fetch', icon: 'globe', target: url || title, targetPath: '' };
            }
            if (kind === 'delete' || /delete|remove/.test(titleKind)) {
                return Object.assign({ title: 'Delete', icon: 'edit' }, fileTarget.target ? fileTarget : { target: title, targetPath: '' });
            }
            if (kind === 'move' || /\b(move|rename)\b/.test(titleKind)) {
                return Object.assign({ title: 'Move', icon: 'file' }, fileTarget.target ? fileTarget : { target: title, targetPath: '' });
            }
            if (kind === 'edit' || /write|create|edit|replace|patch|update/.test(titleKind)) {
                return Object.assign({ title: /create|write|new/.test(titleKind) ? 'Write' : 'Edit', icon: 'edit' }, fileTarget.target ? fileTarget : { target: title, targetPath: '' });
            }
            if (isListToolCall(call)) {
                return Object.assign({ title: 'List', icon: 'folder' }, fileTarget.target ? fileTarget : { target: title, targetPath: '' });
            }
            if (kind === 'read' || isReadFileToolCall(call) || /\bread\b|\bopen\b/.test(titleKind)) {
                return Object.assign({ title: 'Read', icon: 'file' }, fileTarget.target ? fileTarget : { target: title, targetPath: '' });
            }
            if (kind === 'think') {
                return { title: 'Think', icon: 'brain', target: title, targetPath: '' };
            }
            if (rawInput && typeof rawInput.agent === 'string') {
                return { title: 'Agent', icon: 'agent', target: '@' + rawInput.agent + (typeof rawInput.task === 'string' ? ' — ' + rawInput.task : ''), targetPath: '' };
            }

            return { title: title || (call && call.toolCallId) || 'Tool', icon: 'tool', target: '', targetPath: '' };
        }

        function shortenPath(value) {
            const normalized = String(value || '').replace(/\\/g, '/');
            const root = String(workingDirectory || '').replace(/\\/g, '/').replace(/\/+$/, '');
            if (root && normalized.toLowerCase().startsWith(root.toLowerCase() + '/')) {
                return normalized.slice(root.length + 1);
            }
            return normalized;
        }

        function shouldOpenToolCallByDefault(call, diffModel) {
            const status = normalizeClass((call && call.status) || '');
            if (status === 'failed' || status === 'error') {
                return true;
            }

            if (diffModel.length > 0) {
                const lineCount = diffModel.reduce((total, file) => total + file.lines.length, 0);
                return lineCount <= 400;
            }

            return false;
        }

        function countDiffTotals(files) {
            return files.reduce((totals, file) => {
                file.lines.forEach(line => {
                    if (line.type === 'add') { totals.added += 1; }
                    if (line.type === 'del') { totals.removed += 1; }
                });
                return totals;
            }, { added: 0, removed: 0 });
        }

        function stripDiffLikeOutput(output) {
            const text = String(output || '').trim();
            if (!text) {
                return '';
            }

            if (text.includes('*** Begin Patch') || text.includes('diff --git') || /^@@\s/m.test(text)) {
                return '';
            }

            return text;
        }

        function createInlineDisclosure(options) {
            const details = document.createElement('details');
            details.className = options.className || '';
            details.open = Boolean(options.open);

            const summary = document.createElement('summary');
            summary.className = 'inline-call-summary';

            if (options.statusIcon) {
                const status = iconElement(options.statusIcon, 'inline-call-status status-' + options.statusIcon);
                status.title = String(options.status || '');
                summary.appendChild(status);
            }

            summary.appendChild(iconElement(ICONS[options.icon] ? options.icon : 'tool', 'inline-call-icon'));

            const title = document.createElement('span');
            title.className = 'inline-call-title';
            title.textContent = options.title || 'Details';
            summary.appendChild(title);

            if (options.target) {
                if (options.targetPath) {
                    const link = createFileReferenceLink(options.target, options.targetPath);
                    link.classList.add('inline-call-target');
                    summary.appendChild(link);
                } else {
                    const target = document.createElement('span');
                    target.className = 'inline-call-target';
                    target.textContent = String(options.target).split('\n')[0];
                    target.title = String(options.target);
                    summary.appendChild(target);
                }
            }

            if (options.stats) {
                const stats = document.createElement('span');
                stats.className = 'inline-call-stats';
                const add = document.createElement('span');
                add.className = 'stat-added';
                add.textContent = '+' + options.stats.added;
                const del = document.createElement('span');
                del.className = 'stat-removed';
                del.textContent = '−' + options.stats.removed;
                stats.appendChild(add);
                stats.appendChild(del);
                summary.appendChild(stats);
            }

            if (options.meta) {
                const meta = document.createElement('span');
                meta.className = 'inline-call-meta';
                meta.textContent = options.meta;
                summary.appendChild(meta);
            }

            summary.appendChild(iconElement('chevronRight', 'inline-call-chevron'));
            details.appendChild(summary);

            const content = document.createElement('div');
            content.className = 'inline-call-content';
            details.appendChild(content);
            return { details, content };
        }

        function summarizeLineCount(text) {
            const normalized = String(text || '').trim();
            if (!normalized) {
                return '';
            }

            const lineCount = normalized.split(/\r?\n/).filter(Boolean).length;
            return lineCount === 1 ? '1 line' : lineCount + ' lines';
        }

        function createDiffView(file) {
            const wrap = document.createElement('div');
            let added = 0;
            let removed = 0;
            file.lines.forEach(line => {
                if (line.type === 'add') { added += 1; }
                if (line.type === 'del') { removed += 1; }
            });
            wrap.className = 'diff-view' + (added > 0 && removed === 0 ? ' diff-created diff-add-only' : '') + (removed > 0 && added === 0 ? ' diff-deleted diff-del-only' : '');

            const header = document.createElement('div');
            header.className = 'diff-header';

            const headerMain = document.createElement('div');
            headerMain.className = 'diff-header-main';
            const pathLink = createFileReferenceLink(file.path, file.path);
            headerMain.appendChild(pathLink);
            header.appendChild(headerMain);

            const actions = document.createElement('div');
            actions.className = 'diff-header-actions';
            const stat = document.createElement('span');
            stat.className = 'diff-stat';
            stat.textContent = '+' + added + ' −' + removed;
            actions.appendChild(stat);
            const copyButton = document.createElement('button');
            copyButton.type = 'button';
            copyButton.className = 'diff-copy-button';
            copyButton.title = 'Copy diff';
            copyButton.setAttribute('aria-label', 'Copy diff');
            setIconContent(copyButton, 'copy');
            copyButton.addEventListener('click', event => {
                event.preventDefault();
                event.stopPropagation();
                copyTextToClipboard(formatDiffForCopy(file));
                flashCopied(copyButton);
            });
            actions.appendChild(copyButton);
            header.appendChild(actions);
            wrap.appendChild(header);

            const pre = document.createElement('pre');
            pre.className = 'diff-body';
            const state = createDiffLineNumberState();
            const renderDiffRow = line => {
                const row = document.createElement('div');
                row.className = 'diff-line diff-' + line.type;

                const lineNumber = document.createElement('span');
                lineNumber.className = 'diff-line-number';
                lineNumber.textContent = getDiffLineNumber(line, state);
                row.appendChild(lineNumber);

                const sign = document.createElement('span');
                sign.className = 'diff-line-sign';
                sign.textContent = line.type === 'add' ? '+' : line.type === 'del' ? '-' : line.type === 'meta' ? '' : ' ';
                row.appendChild(sign);

                const code = document.createElement('span');
                code.className = 'diff-line-code';
                code.textContent = line.text;
                row.appendChild(code);

                pre.appendChild(row);
            };
            appendCappedRows(wrap, pre, file.lines, renderDiffRow);
            return wrap;
        }

        function createDiffLineNumberState() {
            return { oldLine: 1, newLine: 1 };
        }

        function getDiffLineNumber(line, state) {
            if (typeof line.newLine === 'number' && line.newLine > 0) {
                return String(line.newLine);
            }
            if (typeof line.lineNumber === 'number' && line.lineNumber > 0) {
                return String(line.lineNumber);
            }

            const text = String(line.text || '');
            const hunk = text.match(/@@\s+-(\d+)(?:,\d+)?\s+\+(\d+)(?:,\d+)?\s+@@/);
            if (hunk) {
                state.oldLine = Number(hunk[1]);
                state.newLine = Number(hunk[2]);
                return '';
            }

            if (line.type === 'add') {
                const value = state.newLine;
                state.newLine += 1;
                return String(value);
            }

            if (line.type === 'del') {
                const value = state.oldLine;
                state.oldLine += 1;
                return String(value);
            }

            if (line.type === 'context') {
                const value = state.newLine;
                state.oldLine += 1;
                state.newLine += 1;
                return String(value);
            }

            return '';
        }

        function formatDiffForCopy(file) {
            const lines = ['--- ' + file.path, '+++ ' + file.path];
            file.lines.forEach(line => {
                const sign = line.type === 'add' ? '+' : line.type === 'del' ? '-' : line.type === 'meta' ? '' : ' ';
                lines.push(sign + line.text);
            });
            return lines.join('\n');
        }

        function copyTextToClipboard(text) {
            if (navigator.clipboard && typeof navigator.clipboard.writeText === 'function') {
                navigator.clipboard.writeText(text).catch(() => copyTextFallback(text));
                return;
            }

            copyTextFallback(text);
        }

        function copyTextFallback(text) {
            const textarea = document.createElement('textarea');
            textarea.value = text;
            textarea.setAttribute('readonly', 'true');
            textarea.style.position = 'fixed';
            textarea.style.opacity = '0';
            textarea.style.pointerEvents = 'none';
            document.body.appendChild(textarea);
            textarea.select();
            try {
                document.execCommand('copy');
            } catch (error) {
                // Best effort only.
            }
            textarea.remove();
        }

        function createDetails(summaryText, bodyText, open) {
            const details = document.createElement('details');
            details.open = open;

            const summary = document.createElement('summary');
            summary.textContent = summaryText;
            details.appendChild(summary);

            const pre = document.createElement('pre');
            renderLinkifiedText(pre, bodyText);
            details.appendChild(pre);
            return details;
        }

        function updateActivityVisibility() {
            const hasAgents = agentThreads.size > 0;
            const visible = activeView === 'chat' && (hasPlanActivity || hasAgents || hasChangedFiles);
            workingSet.classList.toggle('hidden', !visible);
            if (!visible) {
                return;
            }

            planSection.classList.toggle('hidden', !hasPlanActivity);
            agentsSection.classList.toggle('hidden', !hasAgents);
            filesSection.classList.toggle('hidden', !hasChangedFiles);
            workingSetBody.classList.toggle('hidden', !workingSetExpanded);
            workingSet.classList.toggle('expanded', workingSetExpanded);
            workingSetToggle.setAttribute('aria-expanded', workingSetExpanded ? 'true' : 'false');

            workingSetSummary.textContent = '';
            const parts = [];
            if (hasPlanActivity) {
                const done = planEntries.filter(entry => normalizeClass(entry.status) === 'completed').length;
                const current = planEntries.find(entry => normalizeClass(entry.status) === 'in_progress');
                parts.push({ icon: 'list', text: 'Plan ' + done + '/' + planEntries.length + (current && !workingSetExpanded ? ' · ' + current.content : '') });
            }
            if (hasAgents) {
                const running = Array.from(agentThreads.values()).filter(thread => /pending|in_progress/.test(normalizeClass(thread.status))).length;
                parts.push({ icon: 'agent', text: agentThreads.size + (agentThreads.size === 1 ? ' agent' : ' agents') + (running ? ' (' + running + ' running)' : '') });
            }
            if (hasChangedFiles) {
                parts.push({ icon: 'file', text: changedFiles.length + (changedFiles.length === 1 ? ' file changed' : ' files changed'), stats: changedFilesCount.textContent });
            }
            parts.forEach(part => {
                const item = document.createElement('span');
                item.className = 'ws-summary-item';
                item.appendChild(iconElement(part.icon));
                const text = document.createElement('span');
                text.className = 'ws-summary-text';
                text.textContent = part.text;
                item.appendChild(text);
                if (part.stats) {
                    const stats = document.createElement('span');
                    stats.className = 'ws-summary-stats';
                    const [added, removed] = String(part.stats).split(' ');
                    const add = document.createElement('span');
                    add.className = 'stat-added';
                    add.textContent = added || '';
                    const del = document.createElement('span');
                    del.className = 'stat-removed';
                    del.textContent = removed || '';
                    stats.appendChild(add);
                    stats.appendChild(del);
                    item.appendChild(stats);
                }
                workingSetSummary.appendChild(item);
            });
        }

        function trackAgentThreads(call) {
            const descriptors = createAgentThreadDescriptors(call);
            if (descriptors.length === 0) {
                return;
            }

            descriptors.forEach(descriptor => {
                const current = agentThreads.get(descriptor.id) || {};
                const merged = Object.assign({}, current, descriptor, {
                    updatedAt: Date.now()
                });
                agentThreads.set(descriptor.id, merged);
            });

            if (!activeAgentThreadId || !agentThreads.has(activeAgentThreadId)) {
                activeAgentThreadId = descriptors[0].id;
            }
        }

        function createAgentThreadDescriptors(call) {
            if (!call || !call.toolCallId) {
                return [];
            }

            const rawInput = call.rawInput && typeof call.rawInput === 'object'
                ? call.rawInput
                : null;
            const output = Array.isArray(call.content) ? call.content.join('\n').trim() : '';
            const status = String(call.status || 'pending');

            if (rawInput && typeof rawInput.agent === 'string' && rawInput.agent.trim()) {
                return [{
                    id: call.toolCallId,
                    toolCallId: call.toolCallId,
                    agent: rawInput.agent.trim(),
                    task: typeof rawInput.task === 'string' ? rawInput.task.trim() : '',
                    context: typeof rawInput.context === 'string' ? rawInput.context.trim() : '',
                    status,
                    output,
                    source: 'delegate'
                }];
            }

            if (rawInput && Array.isArray(rawInput.tasks)) {
                return rawInput.tasks
                    .map((task, index) => {
                        if (!task || typeof task !== 'object' || typeof task.agent !== 'string' || !task.agent.trim()) {
                            return null;
                        }

                        return {
                            id: call.toolCallId + ':' + index,
                            toolCallId: call.toolCallId,
                            agent: task.agent.trim(),
                            task: typeof task.task === 'string' ? task.task.trim() : '',
                            context: typeof task.context === 'string' ? task.context.trim() : '',
                            ownership: typeof task.ownership === 'string' ? task.ownership.trim() : '',
                            status,
                            output,
                            source: 'orchestrate'
                        };
                    })
                    .filter(Boolean);
            }

            return [];
        }

        function renderAgentThreads() {
            const threads = getOrderedAgentThreads();
            agentThreadList.textContent = '';
            agentCount.textContent = threads.length + (threads.length === 1 ? ' thread' : ' threads');
            updateActivityVisibility();

            if (threads.length === 0) {
                activeAgentThreadId = '';
                return;
            }

            if (!activeAgentThreadId || !agentThreads.has(activeAgentThreadId)) {
                activeAgentThreadId = threads[0].id;
            }

            threads.forEach(thread => {
                const item = document.createElement('button');
                item.type = 'button';
                item.className = 'agent-thread-item' + (thread.id === activeAgentThreadId ? ' active' : '');
                item.addEventListener('click', () => {
                    activeAgentThreadId = thread.id;
                    renderAgentThreads();
                });

                const topLine = document.createElement('div');
                topLine.className = 'agent-thread-topline';

                const name = document.createElement('div');
                name.className = 'agent-thread-name';
                name.textContent = '@' + thread.agent;
                topLine.appendChild(name);

                const status = document.createElement('div');
                status.className = 'agent-thread-status';
                status.textContent = thread.status || 'pending';
                topLine.appendChild(status);

                item.appendChild(topLine);

                if (thread.task) {
                    const task = document.createElement('div');
                    task.className = 'agent-thread-task';
                    task.textContent = thread.task;
                    item.appendChild(task);
                }

                const metaParts = [];
                if (thread.source === 'orchestrate') {
                    metaParts.push('orchestrated');
                }
                if (thread.ownership) {
                    metaParts.push(thread.ownership);
                }
                if (thread.context) {
                    metaParts.push(thread.context);
                }
                if (metaParts.length > 0) {
                    const meta = document.createElement('div');
                    meta.className = 'agent-thread-meta';
                    meta.textContent = metaParts.join(' | ');
                    item.appendChild(meta);
                }

                if (thread.id === activeAgentThreadId && thread.output) {
                    const output = document.createElement('div');
                    output.className = 'agent-thread-output';
                    renderLinkifiedText(output, truncateForAgentInspector(thread.output));
                    item.appendChild(output);
                }

                agentThreadList.appendChild(item);
            });
        }

        function switchAgentThread(delta) {
            const threads = getOrderedAgentThreads();
            if (threads.length === 0) {
                return;
            }

            const currentIndex = Math.max(0, threads.findIndex(thread => thread.id === activeAgentThreadId));
            const nextIndex = (currentIndex + delta + threads.length) % threads.length;
            activeAgentThreadId = threads[nextIndex].id;
            renderAgentThreads();
        }

        function getOrderedAgentThreads() {
            return Array.from(agentThreads.values()).sort((left, right) => {
                return Number(right.updatedAt || 0) - Number(left.updatedAt || 0);
            });
        }

        function truncateForAgentInspector(text) {
            const normalized = String(text || '').trim();
            if (normalized.length <= 500) {
                return normalized;
            }

            return normalized.slice(0, 497) + '...';
        }

        function formatPayload(value) {
            if (typeof value === 'string') {
                return value;
            }

            try {
                return JSON.stringify(value, null, 2);
            } catch {
                return String(value);
            }
        }

        function normalizeClass(value) {
            return String(value || '').toLowerCase().replace(/[^a-z0-9_-]+/g, '_');
        }

        function showModelPicker() {
            if (activeModalRequest) {
                return;
            }

            const models = getAvailableModelIds();
            if (models.length === 0) {
                post({ command: 'selectModel' });
                return;
            }

            modalTitle.textContent = 'Choose model';
            const activeModelId = sessionInfo && sessionInfo.modelId ? sessionInfo.modelId : '';
            modalDescription.textContent = activeModelId ? 'Current: ' + activeModelId : '';
            modalDescription.hidden = !activeModelId;
            modalBody.textContent = '';
            modalActions.textContent = '';

            const options = document.createElement('div');
            options.className = 'modal-options';
            modalBody.appendChild(options);

            models.forEach(modelId => {
                const button = document.createElement('button');
                button.className = 'modal-option' + (modelId === activeModelId ? ' active' : '');
                button.addEventListener('click', () => {
                    if (modelId !== activeModelId) {
                        post({ command: 'changeModel', modelId });
                    }

                    closeModal();
                });

                const name = document.createElement('strong');
                name.textContent = modelId;
                button.appendChild(name);

                if (modelId === activeModelId) {
                    const current = document.createElement('span');
                    current.textContent = 'Current';
                    button.appendChild(current);
                }

                options.appendChild(button);
            });

            const close = document.createElement('button');
            close.className = 'ghost-button';
            close.textContent = 'Close';
            close.addEventListener('click', closeModal);
            modalActions.appendChild(close);
            modalBackdrop.classList.remove('hidden');
        }

        function showSessionBrowser(sessions, currentSessionId) {
            if (activeModalRequest) {
                return;
            }

            modalTitle.textContent = 'Sessions';
            modalDescription.textContent = sessions.length === 1 ? '1 saved session' : sessions.length + ' saved sessions';
            modalDescription.hidden = false;
            modalBody.textContent = '';
            modalActions.textContent = '';

            const options = document.createElement('div');
            options.className = 'modal-options';
            modalBody.appendChild(options);

            if (sessions.length === 0) {
                const empty = document.createElement('div');
                empty.className = 'empty-panel';
                empty.textContent = 'No saved sessions yet.';
                options.appendChild(empty);
            }

            sessions.forEach(session => {
                const button = document.createElement('button');
                button.className = 'modal-option' + (session.sessionId === currentSessionId ? ' active' : '');
                button.addEventListener('click', () => {
                    if (session.sessionId !== currentSessionId) {
                        post({ command: 'resumeSession', sessionId: session.sessionId });
                    }
                    closeModal();
                });

                const name = document.createElement('strong');
                name.textContent = session.title || 'Untitled session';
                button.appendChild(name);

                const metaParts = [];
                if (session.modelId) {
                    metaParts.push(session.modelId);
                }
                if (typeof session.turnCount === 'number') {
                    metaParts.push(session.turnCount + (session.turnCount === 1 ? ' turn' : ' turns'));
                }
                if (session.parentSessionId) {
                    metaParts.push('fork');
                }
                const when = formatRelativeTime(session.updatedAtUtc);
                if (when) {
                    metaParts.push(when);
                }
                if (session.sessionId === currentSessionId) {
                    metaParts.push('current');
                }

                const meta = document.createElement('span');
                meta.textContent = metaParts.join(' · ');
                button.appendChild(meta);

                options.appendChild(button);
            });

            const fork = document.createElement('button');
            fork.className = 'ghost-button';
            fork.textContent = 'Fork current';
            fork.title = 'Fork the active session';
            fork.addEventListener('click', () => {
                post({ command: 'runSessionCommand', text: '/fork' });
                closeModal();
            });

            const exportButton = document.createElement('button');
            exportButton.className = 'ghost-button';
            exportButton.textContent = 'Export current';
            exportButton.title = 'Export the active session to JSON';
            exportButton.addEventListener('click', () => {
                post({ command: 'runSessionCommand', text: '/export json' });
                closeModal();
            });

            const close = document.createElement('button');
            close.className = 'ghost-button';
            close.textContent = 'Close';
            close.addEventListener('click', closeModal);

            modalActions.appendChild(fork);
            modalActions.appendChild(exportButton);
            modalActions.appendChild(close);
            modalBackdrop.classList.remove('hidden');
        }

        function showPluginPanel(marketplaces, installed) {
            if (activeModalRequest) {
                return;
            }

            const busy = promptState.isRunning === true;

            modalTitle.textContent = 'Skills';
            modalDescription.hidden = false;
            modalDescription.textContent =
                marketplaces.length + (marketplaces.length === 1 ? ' marketplace · ' : ' marketplaces · ') +
                installed.length + (installed.length === 1 ? ' installed' : ' installed');
            modalBody.textContent = '';
            modalActions.textContent = '';

            const panel = document.createElement('div');
            panel.className = 'plugin-panel';
            modalBody.appendChild(panel);

            function runPlugin(text) {
                if (promptState.isRunning === true) {
                    return;
                }
                post({ command: 'pluginAction', text: text });
            }

            function sectionLabel(text) {
                const label = document.createElement('div');
                label.className = 'context-label';
                label.textContent = text;
                return label;
            }

            function infoOption(name, metaText) {
                const info = document.createElement('div');
                info.className = 'modal-option';
                const strong = document.createElement('strong');
                strong.textContent = name;
                info.appendChild(strong);
                const meta = document.createElement('span');
                meta.textContent = metaText;
                info.appendChild(meta);
                return info;
            }

            // Installed skills (with uninstall buttons)
            const installedSection = document.createElement('div');
            installedSection.className = 'plugin-section';
            installedSection.appendChild(sectionLabel('Installed skills'));
            if (installed.length === 0) {
                const empty = document.createElement('div');
                empty.className = 'empty-panel';
                empty.textContent = 'No skills installed yet.';
                installedSection.appendChild(empty);
            } else {
                installed.forEach(plugin => {
                    const row = document.createElement('div');
                    row.className = 'plugin-row';

                    const parts = [];
                    if (plugin.repository) {
                        parts.push(plugin.repository + '@' + plugin.ref);
                    }
                    if (plugin.marketplaceAlias) {
                        parts.push('via ' + plugin.marketplaceAlias);
                    }
                    const fileCount = Array.isArray(plugin.files) ? plugin.files.length : 0;
                    parts.push(fileCount + (fileCount === 1 ? ' file' : ' files'));
                    row.appendChild(infoOption(plugin.pluginId, parts.join(' · ')));

                    const uninstall = document.createElement('button');
                    uninstall.className = 'danger-button';
                    uninstall.textContent = 'Uninstall';
                    uninstall.disabled = busy;
                    uninstall.addEventListener('click', () => runPlugin('/skill uninstall ' + plugin.pluginId));
                    row.appendChild(uninstall);

                    installedSection.appendChild(row);
                });
            }
            panel.appendChild(installedSection);

            // Install a skill (id + marketplace + force + button)
            const installSection = document.createElement('div');
            installSection.className = 'plugin-section';
            installSection.appendChild(sectionLabel('Install a skill'));
            if (marketplaces.length === 0) {
                const note = document.createElement('div');
                note.className = 'empty-panel';
                note.textContent = 'Add a marketplace below before installing.';
                installSection.appendChild(note);
            } else {
                const idInput = document.createElement('input');
                idInput.className = 'modal-input';
                idInput.placeholder = 'skill-id';
                idInput.disabled = busy;
                installSection.appendChild(idInput);

                const select = document.createElement('select');
                select.className = 'modal-input';
                select.disabled = busy;
                marketplaces.forEach(market => {
                    const option = document.createElement('option');
                    option.value = market.alias;
                    option.textContent = market.alias + ' (' + market.repository + ')';
                    select.appendChild(option);
                });
                installSection.appendChild(select);

                const forceLabel = document.createElement('label');
                forceLabel.className = 'plugin-check';
                const force = document.createElement('input');
                force.type = 'checkbox';
                force.disabled = busy;
                forceLabel.appendChild(force);
                forceLabel.appendChild(document.createTextNode(' Overwrite existing files (--force)'));
                installSection.appendChild(forceLabel);

                const installButton = document.createElement('button');
                installButton.className = 'primary-button';
                installButton.textContent = 'Install';
                installButton.disabled = busy;
                installButton.addEventListener('click', () => {
                    const id = idInput.value.trim();
                    if (!id) {
                        idInput.focus();
                        return;
                    }
                    runPlugin('/skill install ' + id + '@' + select.value + (force.checked ? ' --force' : ''));
                });
                installSection.appendChild(installButton);
            }
            panel.appendChild(installSection);

            // Marketplaces (list + add form)
            const marketSection = document.createElement('div');
            marketSection.className = 'plugin-section';
            marketSection.appendChild(sectionLabel('Marketplaces'));
            if (marketplaces.length === 0) {
                const empty = document.createElement('div');
                empty.className = 'empty-panel';
                empty.textContent = 'No marketplaces configured.';
                marketSection.appendChild(empty);
            } else {
                marketplaces.forEach(market => {
                    const row = document.createElement('div');
                    row.className = 'plugin-row';
                    row.appendChild(infoOption(market.alias, market.repository + '@' + market.ref));

                    const browse = document.createElement('button');
                    browse.className = 'ghost-button';
                    browse.textContent = 'Browse';
                    browse.disabled = busy;
                    browse.title = 'List skills this marketplace offers (output in chat)';
                    browse.addEventListener('click', () => {
                        if (promptState.isRunning === true) {
                            return;
                        }
                        post({ command: 'runSessionCommand', text: '/skill browse ' + market.alias });
                        closeModal();
                    });
                    row.appendChild(browse);

                    const remove = document.createElement('button');
                    remove.className = 'danger-button';
                    remove.textContent = 'Remove';
                    remove.disabled = busy;
                    remove.addEventListener('click', () => runPlugin('/skill marketplace remove ' + market.alias));
                    row.appendChild(remove);

                    marketSection.appendChild(row);
                });
            }

            const addForm = document.createElement('div');
            addForm.className = 'plugin-form';
            const repoInput = document.createElement('input');
            repoInput.className = 'modal-input';
            repoInput.placeholder = 'owner/repo';
            repoInput.disabled = busy;
            addForm.appendChild(repoInput);
            const addButton = document.createElement('button');
            addButton.className = 'ghost-button';
            addButton.textContent = 'Add';
            addButton.disabled = busy;
            addButton.addEventListener('click', () => {
                const repo = repoInput.value.trim();
                if (!repo) {
                    repoInput.focus();
                    return;
                }
                runPlugin('/skill marketplace add ' + repo);
            });
            addForm.appendChild(addButton);
            marketSection.appendChild(addForm);
            panel.appendChild(marketSection);

            const refresh = document.createElement('button');
            refresh.className = 'ghost-button';
            refresh.textContent = 'Refresh';
            refresh.addEventListener('click', () => post({ command: 'loadPlugins' }));

            const close = document.createElement('button');
            close.className = 'ghost-button';
            close.textContent = 'Close';
            close.addEventListener('click', closeModal);

            modalActions.appendChild(refresh);
            modalActions.appendChild(close);
            modalBackdrop.classList.remove('hidden');
        }

        function formatRelativeTime(iso) {
            if (!iso) {
                return '';
            }

            const then = Date.parse(iso);
            if (!Number.isFinite(then)) {
                return '';
            }

            const diff = Date.now() - then;
            if (diff < 0) {
                return '';
            }

            const minutes = Math.floor(diff / 60000);
            if (minutes < 1) {
                return 'just now';
            }
            if (minutes < 60) {
                return minutes + 'm ago';
            }
            const hours = Math.floor(minutes / 60);
            if (hours < 24) {
                return hours + 'h ago';
            }
            const days = Math.floor(hours / 24);
            if (days < 30) {
                return days + 'd ago';
            }
            return Math.floor(days / 30) + 'mo ago';
        }

        function getAvailableModelIds() {
            const activeModelId = sessionInfo && sessionInfo.modelId ? sessionInfo.modelId : '';
            const models = sessionInfo && Array.isArray(sessionInfo.availableModelIds)
                ? sessionInfo.availableModelIds
                : [];
            const unique = [];
            const seen = new Set();

            if (activeModelId) {
                unique.push(activeModelId);
                seen.add(activeModelId);
            }

            models.forEach(modelId => {
                if (typeof modelId !== 'string') {
                    return;
                }

                const normalizedModelId = modelId.trim();
                if (!normalizedModelId || seen.has(normalizedModelId)) {
                    return;
                }

                unique.push(normalizedModelId);
                seen.add(normalizedModelId);
            });

            return unique;
        }

        function closeModal() {
            clearAutoSelectTimer();
            activeModalRequest = null;
            modalBackdrop.classList.add('hidden');
            modalTitle.textContent = '';
            modalDescription.textContent = '';
            modalDescription.hidden = true;
            modalBody.textContent = '';
            modalActions.textContent = '';
            inputField.focus();
        }

        function showClientRequest(request) {
            if (!request || !request.id) {
                return;
            }

            if (request.kind === 'permission') {
                showInlinePermission(request);
                return;
            }

            if (activeModalRequest && activeModalRequest.id === request.id) {
                return;
            }

            if (queuedClientRequests.some(queuedRequest => queuedRequest.id === request.id)) {
                return;
            }

            if (activeModalRequest) {
                queuedClientRequests.push(request);
                return;
            }

            openClientRequest(request);
        }

        // ---- Inline permission cards ----------------------------------------------------------
        function showInlinePermission(request) {
            if (inlinePermissionCards.has(request.id)) {
                return;
            }

            if (!modalBackdrop.classList.contains('hidden') && !activeModalRequest) {
                closeModal();
            }
            showChatPage();
            emptyState.hidden = true;

            const requestOptions = Array.isArray(request.options) ? request.options : [];
            const defaultOptionId = getDefaultOptionId(request, requestOptions);
            const card = document.createElement('article');
            card.className = 'message-card permission pending';
            card.setAttribute('role', 'group');
            card.setAttribute('aria-label', request.title || 'Permission request');

            const header = document.createElement('div');
            header.className = 'permission-header';
            header.appendChild(iconElement('shield', 'permission-icon'));
            const title = document.createElement('span');
            title.className = 'permission-title';
            title.textContent = request.title || 'Permission required';
            header.appendChild(title);
            card.appendChild(header);

            if (request.description) {
                const description = document.createElement('div');
                description.className = 'permission-description';
                renderLinkifiedText(description, request.description);
                card.appendChild(description);
            }

            const options = document.createElement('div');
            options.className = 'permission-options';
            const entry = { request, element: card, timer: null, selected: '' };

            requestOptions.forEach((option, index) => {
                const button = document.createElement('button');
                button.type = 'button';
                const kind = String(option.kind || '');
                const isReject = kind.startsWith('reject');
                button.className = 'permission-option' +
                    (option.optionId === defaultOptionId ? ' default' : '') +
                    (isReject ? ' reject' : '');
                button.dataset.optionId = option.optionId;

                if (index < 9) {
                    const key = document.createElement('span');
                    key.className = 'permission-key';
                    key.textContent = String(index + 1);
                    button.appendChild(key);
                }
                const label = document.createElement('span');
                label.className = 'permission-option-label';
                label.textContent = option.name || option.optionId;
                button.appendChild(label);
                const optionKind = formatOptionKind(option.kind);
                if (optionKind) {
                    const kindLabel = document.createElement('span');
                    kindLabel.className = 'permission-option-kind';
                    kindLabel.textContent = optionKind;
                    button.appendChild(kindLabel);
                }

                button.addEventListener('click', () => {
                    resolveInlinePermission(request.id, { outcome: 'selected', optionId: option.optionId }, option.name || option.optionId);
                });
                options.appendChild(button);
            });

            if (request.allowCancellation !== false) {
                const cancel = document.createElement('button');
                cancel.type = 'button';
                cancel.className = 'permission-option subtle';
                cancel.textContent = 'Cancel';
                cancel.title = 'Cancel (Esc)';
                cancel.addEventListener('click', () => resolveInlinePermission(request.id, { outcome: 'cancelled' }, 'Cancelled'));
                options.appendChild(cancel);
            }

            card.appendChild(options);
            const footer = document.createElement('div');
            footer.className = 'permission-footer';
            card.appendChild(footer);

            card.addEventListener('keydown', event => {
                if (/^[1-9]$/.test(event.key)) {
                    const option = requestOptions[Number(event.key) - 1];
                    if (option) {
                        event.preventDefault();
                        event.stopPropagation();
                        resolveInlinePermission(request.id, { outcome: 'selected', optionId: option.optionId }, option.name || option.optionId);
                    }
                } else if (event.key === 'Escape' && request.allowCancellation !== false) {
                    event.preventDefault();
                    event.stopPropagation();
                    resolveInlinePermission(request.id, { outcome: 'cancelled' }, 'Cancelled');
                } else if (event.key === 'ArrowRight' || event.key === 'ArrowLeft') {
                    const buttons = Array.from(options.querySelectorAll('button')) as HTMLElement[];
                    const index = buttons.indexOf(document.activeElement as HTMLElement);
                    const next = buttons[(index + (event.key === 'ArrowRight' ? 1 : -1) + buttons.length) % buttons.length];
                    if (next) {
                        event.preventDefault();
                        next.focus();
                    }
                }
            });

            inlinePermissionCards.set(request.id, entry);
            messagesDiv.appendChild(card);
            moveProgressIndicatorToEnd();
            scrollToBottom(true);

            if (inputField.value.trim().length === 0) {
                const focusTarget = options.querySelector('.permission-option.default') as HTMLElement || options.querySelector('button') as HTMLElement;
                if (focusTarget) {
                    focusTarget.focus({ preventScroll: true });
                }
            }

            startInlineAutoSelect(entry, defaultOptionId, requestOptions, footer);
        }

        function startInlineAutoSelect(entry, defaultOptionId, options, footer) {
            const delayMilliseconds = Number(entry.request.autoSelectAfterMilliseconds);
            if (!defaultOptionId || !Number.isFinite(delayMilliseconds) || delayMilliseconds <= 0) {
                footer.textContent = '1–' + Math.min(9, options.length) + ' to choose' + (entry.request.allowCancellation !== false ? ' · Esc to cancel' : '');
                return;
            }

            const defaultOption = options.find(option => option.optionId === defaultOptionId);
            const defaultLabel = defaultOption ? (defaultOption.name || defaultOption.optionId) : defaultOptionId;
            const deadline = Date.now() + delayMilliseconds;
            const update = () => {
                if (!inlinePermissionCards.has(entry.request.id) || entry.selected) {
                    clearInterval(entry.timer);
                    return;
                }
                const remaining = deadline - Date.now();
                footer.textContent = 'Auto-selecting "' + defaultLabel + '" in ' + Math.max(0, Math.ceil(remaining / 1000)) + 's';
                if (remaining <= 0) {
                    resolveInlinePermission(entry.request.id, { outcome: 'selected', optionId: defaultOptionId }, defaultLabel);
                }
            };
            entry.timer = setInterval(update, 250);
            update();
        }

        function resolveInlinePermission(requestId, resolution, label) {
            const entry = inlinePermissionCards.get(requestId);
            if (!entry || entry.selected) {
                return;
            }
            entry.selected = label || 'Resolved';
            post({ command: 'resolveClientRequest', requestId, resolution });
            finalizeInlinePermission(entry, resolution.outcome === 'cancelled' ? 'cancelled' : isRejectOption(entry.request, resolution.optionId) ? 'rejected' : 'approved');
            inputField.focus();
        }

        function isRejectOption(request, optionId) {
            const option = (request.options || []).find(item => item.optionId === optionId);
            return Boolean(option && String(option.kind || '').startsWith('reject'));
        }

        function finalizeInlinePermission(entry, outcome) {
            if (entry.timer) {
                clearInterval(entry.timer);
                entry.timer = null;
            }
            const card = entry.element;
            card.classList.remove('pending');
            card.classList.add('resolved', 'outcome-' + outcome);
            const options = card.querySelector('.permission-options');
            const footer = card.querySelector('.permission-footer');
            if (options) {
                options.remove();
            }
            if (footer) {
                footer.textContent = '';
                footer.appendChild(iconElement(outcome === 'approved' ? 'check' : outcome === 'resolved' ? 'info' : 'close'));
                const text = document.createElement('span');
                text.textContent = entry.selected || (outcome === 'cancelled' ? 'Cancelled' : 'Resolved');
                footer.appendChild(text);
            }
        }

        document.addEventListener('keydown', event => {
            if (!/^[1-9]$/.test(event.key) || event.ctrlKey || event.metaKey || event.altKey) {
                return;
            }
            const target = event.target as HTMLElement;
            if (target && (target.tagName === 'TEXTAREA' || target.tagName === 'INPUT' || target.tagName === 'SELECT')) {
                return;
            }
            const pending = Array.from(inlinePermissionCards.values()).find(entry => !entry.selected);
            if (!pending || pending.element.contains(target)) {
                return;
            }
            const option = (pending.request.options || [])[Number(event.key) - 1];
            if (option) {
                event.preventDefault();
                resolveInlinePermission(pending.request.id, { outcome: 'selected', optionId: option.optionId }, option.name || option.optionId);
            }
        });

        function openClientRequest(request) {
            clearAutoSelectTimer();
            activeModalRequest = request;
            modalTitle.textContent = request.kind === 'text' ? request.label : request.title;
            modalDescription.textContent = request.description || '';
            modalDescription.hidden = !request.description;
            modalBody.textContent = '';
            modalActions.textContent = '';

            if (request.kind === 'text') {
                renderTextRequest(request);
            } else {
                renderPermissionRequest(request);
            }

            modalBackdrop.classList.remove('hidden');
        }

        function renderTextRequest(request) {
            const input = document.createElement('input');
            input.className = 'modal-input';
            input.type = request.isSecret ? 'password' : 'text';
            input.value = request.defaultValue || '';
            input.autocomplete = request.isSecret ? 'off' : 'on';
            modalBody.appendChild(input);

            const submit = document.createElement('button');
            submit.className = 'primary-button';
            submit.textContent = 'Submit';
            submit.addEventListener('click', () => {
                const value = input.value;
                if (request.isSecret) {
                    input.value = '';
                }
                resolveActiveRequest({ outcome: 'submitted', value });
            });

            if (request.allowCancellation && shouldShowBackButton(request)) {
                modalActions.appendChild(createBackButton());
            }

            modalActions.appendChild(submit);

            if (request.allowCancellation && !shouldShowBackButton(request)) {
                modalActions.appendChild(createCancelButton());
            }

            input.addEventListener('keydown', event => {
                if (event.key === 'Enter') {
                    event.preventDefault();
                    submit.click();
                }
                if (event.key === 'Escape' && request.allowCancellation) {
                    event.preventDefault();
                    cancelActiveRequest();
                }
            });

            setTimeout(() => input.focus(), 0);
        }

        function renderPermissionRequest(request) {
            const requestOptions = Array.isArray(request.options) ? request.options : [];
            const defaultOptionId = getDefaultOptionId(request, requestOptions);
            const options = document.createElement('div');
            options.className = 'modal-options';
            modalBody.appendChild(options);

            requestOptions.forEach(option => {
                const button = document.createElement('button');
                button.className = 'modal-option' + (option.optionId === defaultOptionId ? ' active' : '');
                button.addEventListener('click', () => {
                    resolveActiveRequest({ outcome: 'selected', optionId: option.optionId });
                });

                const name = document.createElement('strong');
                name.textContent = option.name || option.optionId;
                button.appendChild(name);

                const optionKind = formatOptionKind(option.kind);
                if (optionKind) {
                    const kind = document.createElement('span');
                    kind.textContent = optionKind;
                    button.appendChild(kind);
                }

                options.appendChild(button);
            });

            if (request.allowCancellation !== false) {
                modalActions.appendChild(shouldShowBackButton(request) ? createBackButton() : createCancelButton());
            }

            startPermissionAutoSelectCountdown(request, defaultOptionId, requestOptions);
        }

        function getDefaultOptionId(request, options) {
            const requestedDefault = String(request.defaultOptionId || '').trim();
            if (requestedDefault && options.some(option => option.optionId === requestedDefault)) {
                return requestedDefault;
            }

            return options.length > 0 ? options[0].optionId : '';
        }

        function startPermissionAutoSelectCountdown(request, defaultOptionId, options) {
            const delayMilliseconds = Number(request.autoSelectAfterMilliseconds);
            if (!defaultOptionId ||
                !Number.isFinite(delayMilliseconds) ||
                delayMilliseconds <= 0) {
                return;
            }

            const defaultOption = options.find(option => option.optionId === defaultOptionId);
            const defaultLabel = defaultOption ? (defaultOption.name || defaultOption.optionId) : defaultOptionId;
            const deadline = Date.now() + delayMilliseconds;
            const countdown = document.createElement('div');
            countdown.className = 'modal-countdown';
            modalActions.insertBefore(countdown, modalActions.firstChild);

            const updateCountdown = () => {
                if (!activeModalRequest || activeModalRequest.id !== request.id) {
                    clearAutoSelectTimer();
                    return;
                }

                const remainingMilliseconds = deadline - Date.now();
                const remainingSeconds = Math.max(0, Math.ceil(remainingMilliseconds / 1000));
                countdown.textContent = 'Auto-selecting ' + defaultLabel + ' in ' + remainingSeconds + 's';

                if (remainingMilliseconds <= 0) {
                    resolveActiveRequest({ outcome: 'selected', optionId: defaultOptionId });
                }
            };

            activeAutoSelectTimer = setInterval(updateCountdown, 250);
            updateCountdown();
        }

        function clearAutoSelectTimer() {
            if (activeAutoSelectTimer) {
                clearInterval(activeAutoSelectTimer);
                activeAutoSelectTimer = null;
            }
        }

        function createBackButton() {
            const back = document.createElement('button');
            back.className = 'ghost-button';
            back.textContent = 'Back';
            back.addEventListener('click', cancelActiveRequest);
            return back;
        }

        function createCancelButton() {
            const cancel = document.createElement('button');
            cancel.className = 'ghost-button';
            cancel.textContent = 'Cancel';
            cancel.addEventListener('click', cancelActiveRequest);
            return cancel;
        }

        function shouldShowBackButton(request) {
            const title = String(request.title || request.label || '').trim().toLowerCase();
            const description = String(request.description || '').trim().toLowerCase();
            return title === 'api key' ||
                title === 'base url' ||
                title === 'choose subscription provider' ||
                title === 'choose api key provider' ||
                title === 'choose local provider' ||
                description.includes('returns to provider setup type');
        }

        function formatOptionKind(kind) {
            const normalized = String(kind || '').trim();
            if (!normalized || ['allow_once', 'allow_always', 'reject_once', 'reject_always'].includes(normalized)) {
                return '';
            }

            return normalized.replace(/_/g, ' ');
        }

        function resolveActiveRequest(resolution) {
            if (!activeModalRequest) {
                return;
            }

            const requestId = activeModalRequest.id;
            post({ command: 'resolveClientRequest', requestId, resolution });
            clearClientRequest(requestId);
        }

        function cancelActiveRequest() {
            resolveActiveRequest({ outcome: 'cancelled' });
        }

        function clearClientRequest(requestId) {
            const inline = inlinePermissionCards.get(requestId);
            if (inline) {
                if (!inline.selected) {
                    inline.selected = 'Resolved';
                    finalizeInlinePermission(inline, 'resolved');
                }
                return;
            }

            if (!activeModalRequest || activeModalRequest.id !== requestId) {
                const queuedIndex = queuedClientRequests.findIndex(request => request.id === requestId);
                if (queuedIndex >= 0) {
                    queuedClientRequests.splice(queuedIndex, 1);
                }
                return;
            }

            modalBody.querySelectorAll('input').forEach(input => {
                input.value = '';
            });
            closeModal();
            showNextClientRequest();
        }

        function showNextClientRequest() {
            const nextRequest = queuedClientRequests.shift();
            if (nextRequest) {
                openClientRequest(nextRequest);
            }
        }

        function updateSuggestions() {
            const value = inputField.value;
            const mention = detectMentionQuery(value, inputField.selectionStart);
            if (mention !== null) {
                if (mention !== fileMentionQuery) {
                    fileMentionQuery = mention;
                    fileMentionResults = [];
                    post({ command: 'searchFiles', query: mention });
                }
                visibleSuggestions = buildFileSuggestions(fileMentionResults);
                activeSuggestionIndex = 0;
                renderSuggestions();
                return;
            }

            fileMentionQuery = null;
            visibleSuggestions = createSuggestions(value);
            activeSuggestionIndex = 0;
            renderSuggestions();
        }

        function detectMentionQuery(value, caret) {
            const cursor = typeof caret === 'number' ? caret : value.length;
            const before = value.slice(0, cursor);
            const match = /(^|\s)@([^\s@]*)$/.exec(before);
            return match ? match[2] : null;
        }

        function buildFileSuggestions(files) {
            return files.map(file => ({
                usage: file,
                description: 'Insert file path',
                insertText: file,
                mention: true
            }));
        }

        function createSuggestions(value) {
            if (!value.startsWith('/')) {
                return [];
            }

            if (value.startsWith('/use ')) {
                const query = value.slice('/use '.length).trim().toLowerCase();
                const models = sessionInfo && Array.isArray(sessionInfo.availableModelIds)
                    ? sessionInfo.availableModelIds
                    : [];

                return models
                    .filter(modelId => !query || modelId.toLowerCase().includes(query))
                    .slice(0, 8)
                    .map(modelId => ({
                        usage: modelId,
                        description: modelId === (sessionInfo && sessionInfo.modelId) ? 'Current model' : 'Switch model',
                        insertText: '/use ' + modelId
                    }));
            }

            if (value.startsWith('/profile ')) {
                const query = value.slice('/profile '.length).trim().toLowerCase();
                const profiles = sessionInfo && Array.isArray(sessionInfo.availableAgentProfiles)
                    ? sessionInfo.availableAgentProfiles
                    : fallbackProfiles;

                return profiles
                    .filter(profile => !query || profile.name.toLowerCase().includes(query))
                    .slice(0, 8)
                    .map(profile => ({
                        usage: profile.name,
                        description: profile.name === (sessionInfo && sessionInfo.agentProfileName)
                            ? 'Current profile'
                            : (profile.description || 'Switch profile'),
                        insertText: '/profile ' + profile.name
                    }));
            }

            // Once arguments are being typed, stop suggesting so Enter/Tab can't clobber them.
            if (/\s/.test(value)) {
                return [];
            }

            const token = value.split(/\s+/, 1)[0].toLowerCase();
            return commandSuggestions
                .filter(command => command.command.startsWith(token) || command.usage.toLowerCase().startsWith(token))
                .slice(0, 10);
        }

        function renderSuggestions() {
            suggestionsDiv.textContent = '';
            if (visibleSuggestions.length === 0) {
                hideSuggestions();
                return;
            }

            suggestionsDiv.classList.remove('hidden');
            visibleSuggestions.forEach((suggestion, index) => {
                const item = document.createElement('div');
                item.className = 'suggestion' + (index === activeSuggestionIndex ? ' active' : '');
                item.setAttribute('role', 'option');
                item.setAttribute('aria-selected', index === activeSuggestionIndex ? 'true' : 'false');
                item.addEventListener('mousedown', event => {
                    event.preventDefault();
                    applySuggestion(suggestion);
                });
                item.addEventListener('mousemove', () => {
                    if (activeSuggestionIndex !== index) {
                        activeSuggestionIndex = index;
                        suggestionsDiv.querySelectorAll('.suggestion').forEach((element, elementIndex) => {
                            element.classList.toggle('active', elementIndex === index);
                            element.setAttribute('aria-selected', elementIndex === index ? 'true' : 'false');
                        });
                    }
                });

                item.appendChild(iconElement(suggestion.mention ? 'file' : 'slash', 'suggestion-icon'));

                const usage = document.createElement('div');
                usage.className = 'suggestion-usage';
                usage.textContent = suggestion.usage;
                item.appendChild(usage);

                const description = document.createElement('div');
                description.className = 'suggestion-description';
                description.textContent = suggestion.description;
                item.appendChild(description);

                suggestionsDiv.appendChild(item);
            });

            const active = suggestionsDiv.querySelector('.suggestion.active') as HTMLElement;
            if (active && typeof active.scrollIntoView === 'function') {
                active.scrollIntoView({ block: 'nearest' });
            }
        }

        function applySuggestion(suggestion) {
            if (suggestion.mention) {
                const caret = typeof inputField.selectionStart === 'number'
                    ? inputField.selectionStart
                    : inputField.value.length;
                const before = inputField.value.slice(0, caret).replace(/@[^\s@]*$/, suggestion.insertText + ' ');
                const after = inputField.value.slice(caret);
                inputField.value = before + after;
                inputField.focus();
                inputField.setSelectionRange(before.length, before.length);
                fileMentionQuery = null;
                hideSuggestions();
                updateComposerState();
                return;
            }

            inputField.value = suggestion.insertText;
            inputField.focus();
            inputField.setSelectionRange(inputField.value.length, inputField.value.length);
            autoResizeInput();
            updateSuggestions();
            updateComposerState();
        }

        function hideSuggestions() {
            visibleSuggestions = [];
            suggestionsDiv.classList.add('hidden');
            suggestionsDiv.textContent = '';
        }

        renderContext();
        renderPlan(null);
        renderFileEditsSummary(null);
        renderAgentThreads();
        renderTools();
        renderEditorContextChip();
        updateStatusRail();
        updateComposerState();
        autoResizeInput();
        api.postMessage({ command: 'webviewLog', level: 'info', message: 'Posting ready from main script' });
        post({ command: 'ready' });
