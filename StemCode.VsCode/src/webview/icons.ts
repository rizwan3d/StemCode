// Inline SVG icon set (codicon-style, 16px, currentColor). Constants only — never interpolate user data.
const svg = (body: string, extraClass = '') =>
    `<svg class="icon${extraClass ? ' ' + extraClass : ''}" viewBox="0 0 16 16" width="16" height="16" aria-hidden="true" focusable="false" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round">${body}</svg>`;

export const ICONS = {
    add: svg('<path d="M8 3v10M3 8h10"/>'),
    history: svg('<path d="M2.5 8a5.5 5.5 0 1 0 1.6-3.9"/><path d="M2.5 2.5v2.2h2.2"/><path d="M8 5v3.2l2 1.3"/>'),
    gear: '<svg class="icon" viewBox="0 0 24 24" width="16" height="16" aria-hidden="true" focusable="false" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2z"/><circle cx="12" cy="12" r="3"/></svg>',
    send: svg('<path d="M8 13V3M3.8 7.2 8 3l4.2 4.2"/>'),
    stop: '<svg class="icon" viewBox="0 0 16 16" width="16" height="16" aria-hidden="true" focusable="false"><rect x="4" y="4" width="8" height="8" rx="1.5" fill="currentColor"/></svg>',
    copy: svg('<rect x="5.5" y="5.5" width="8" height="8" rx="1.5"/><path d="M10.5 5.5V3.8A1.3 1.3 0 0 0 9.2 2.5H3.8a1.3 1.3 0 0 0-1.3 1.3v5.4a1.3 1.3 0 0 0 1.3 1.3h1.7"/>'),
    check: svg('<path d="m3.2 8.4 3 3 6.6-6.8"/>'),
    close: svg('<path d="m4 4 8 8M12 4l-8 8"/>'),
    chevronRight: svg('<path d="m6 3.5 4.5 4.5L6 12.5"/>'),
    chevronDown: svg('<path d="M3.5 6 8 10.5 12.5 6"/>'),
    arrowDown: svg('<path d="M8 3v10M3.8 8.8 8 13l4.2-4.2"/>'),
    file: svg('<path d="M9.5 1.8H4.3a1.3 1.3 0 0 0-1.3 1.3v9.8a1.3 1.3 0 0 0 1.3 1.3h7.4a1.3 1.3 0 0 0 1.3-1.3V5.3z"/><path d="M9.5 1.8v3.5H13"/>'),
    edit: svg('<path d="M10.8 2.7a1.6 1.6 0 0 1 2.3 2.3L5.6 12.5l-3 .8.8-3z"/>'),
    terminal: svg('<rect x="1.8" y="2.8" width="12.4" height="10.4" rx="1.5"/><path d="m4.5 6.3 2 1.7-2 1.7M8.3 10h3"/>'),
    search: svg('<circle cx="7" cy="7" r="4.2"/><path d="m10.2 10.2 3.3 3.3"/>'),
    folder: svg('<path d="M1.8 4.3a1.3 1.3 0 0 1 1.3-1.3h3l1.5 1.6h5.3a1.3 1.3 0 0 1 1.3 1.3v6.1a1.3 1.3 0 0 1-1.3 1.3H3.1a1.3 1.3 0 0 1-1.3-1.3z"/>'),
    globe: svg('<circle cx="8" cy="8" r="6"/><path d="M2 8h12M8 2c1.8 1.7 2.6 3.7 2.6 6S9.8 12.3 8 14C6.2 12.3 5.4 10.3 5.4 8S6.2 3.7 8 2"/>'),
    tool: svg('<path d="M9.7 2.3a3.2 3.2 0 0 0-3.9 4.1L2.4 9.8a1.4 1.4 0 0 0 2 2l3.4-3.4a3.2 3.2 0 0 0 4.1-3.9L10 6.4 8.6 5l1.9-1.9z"/>'),
    agent: svg('<rect x="3" y="5" width="10" height="8" rx="2"/><path d="M8 2.5V5M6 8.8h.01M10 8.8h.01"/>'),
    sparkle: svg('<path d="M8 1.8 9.3 6.7l4.9 1.3-4.9 1.3L8 14.2 6.7 9.3 1.8 8l4.9-1.3z"/>'),
    brain: svg('<path d="M5.6 10.6A4.5 4.5 0 1 1 10.4 10.6c-.5.4-.9 1-.9 1.6v.3h-3v-.3c0-.6-.4-1.2-.9-1.6z"/><path d="M6.5 14.3h3"/>'),
    insert: svg('<path d="M3 3h10M3 13h10M8 5.5v5M5.8 8.3 8 10.5l2.2-2.2"/>'),
    newFile: svg('<path d="M9.5 1.8H4.3a1.3 1.3 0 0 0-1.3 1.3v9.8a1.3 1.3 0 0 0 1.3 1.3h7.4a1.3 1.3 0 0 0 1.3-1.3V5.3z"/><path d="M8 7.5v4M6 9.5h4"/>'),
    warning: svg('<path d="M8 2.3 14.2 13H1.8z"/><path d="M8 6.5v3M8 11.3h.01"/>'),
    error: svg('<circle cx="8" cy="8" r="6"/><path d="m5.8 5.8 4.4 4.4M10.2 5.8l-4.4 4.4"/>'),
    info: svg('<circle cx="8" cy="8" r="6"/><path d="M8 7.3v3.7M8 5h.01"/>'),
    shield: svg('<path d="M8 1.8 13 3.7v4c0 3-2.1 5.4-5 6.5-2.9-1.1-5-3.5-5-6.5v-4z"/>'),
    list: svg('<path d="M5.5 4h8M5.5 8h8M5.5 12h8M2.5 4h.01M2.5 8h.01M2.5 12h.01"/>'),
    eye: svg('<path d="M1.5 8S4 3.5 8 3.5 14.5 8 14.5 8 12 12.5 8 12.5 1.5 8 1.5 8z"/><circle cx="8" cy="8" r="2"/>'),
    eyeOff: svg('<path d="M2 2l12 12M6.5 4a6.6 6.6 0 0 1 1.5-.5c4 0 6.5 4.5 6.5 4.5a11 11 0 0 1-1.8 2.3M10.4 11.9a5.6 5.6 0 0 1-2.4.6C4 12.5 1.5 8 1.5 8a11.3 11.3 0 0 1 2.7-3"/>'),
    mention: svg('<circle cx="8" cy="8" r="2.5"/><path d="M10.5 8v1a2 2 0 0 0 3.5 1.3A6 6 0 1 0 11 13.2"/>'),
    slash: svg('<path d="m10.5 2.5-5 11"/>'),
    mic: svg('<rect x="5.8" y="1.8" width="4.4" height="7.4" rx="2.2"/><path d="M3.5 7.5a4.5 4.5 0 0 0 9 0M8 12v2.2"/>'),
    refresh: svg('<path d="M13.5 8a5.5 5.5 0 1 1-1.6-3.9"/><path d="M13.5 2.5v2.2h-2.2"/>'),
    output: svg('<path d="M2.5 3.5h11M2.5 6.5h11M2.5 9.5h7M2.5 12.5h5"/>'),
    spinner: '<svg class="icon spin" viewBox="0 0 16 16" width="16" height="16" aria-hidden="true" focusable="false" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"><path d="M8 2a6 6 0 1 1-6 6" /></svg>',
    circle: svg('<circle cx="8" cy="8" r="5"/>'),
    circleDot: '<svg class="icon" viewBox="0 0 16 16" width="16" height="16" aria-hidden="true" focusable="false" fill="none" stroke="currentColor" stroke-width="1.3"><circle cx="8" cy="8" r="5"/><circle cx="8" cy="8" r="2" fill="currentColor" stroke="none"/></svg>',
    circleCheck: svg('<circle cx="8" cy="8" r="6"/><path d="m5.3 8.2 1.9 1.9 3.6-3.8"/>')
} as const;

export type IconName = keyof typeof ICONS;

/** Returns a detached element wrapping the icon markup (trusted constant SVG). */
export function iconElement(name: IconName, className = 'icon-wrap'): HTMLSpanElement {
    const span = document.createElement('span');
    span.className = className;
    span.innerHTML = ICONS[name];
    return span;
}

/** Replaces an element's children with an icon plus optional text label. */
export function setIconContent(element: HTMLElement, name: IconName, label?: string) {
    element.textContent = '';
    element.appendChild(iconElement(name));
    if (label) {
        const text = document.createElement('span');
        text.className = 'label';
        text.textContent = label;
        element.appendChild(text);
    }
}
