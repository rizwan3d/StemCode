# Changelog

## 0.1.0

- Redesigned the chat view to work like a coding agent: user prompts in boxed messages, compact tool-call rows with live status, inline diffs, a collapsible thinking section, and per-turn stats.
- Permission requests are now inline cards with number-key shortcuts and an auto-select countdown, replacing the blocking modal.
- New composer: auto-growing input, prompt history, `Esc` to stop, queued follow-ups, an active-editor/selection context chip, and a context-usage ring.
- Code blocks now have Copy / Insert at cursor / Open in new editor actions. Markdown tables and horizontal rules render.
- Added a working-set strip for plan progress, subagents, and changed files.
- Added a status banner with Retry and Logs, an empty state with starter prompts, and an editor context menu and keybindings.
- The chat now scrolls to new output only while you're at the bottom, with a "jump to latest" button otherwise. Streaming renders are throttled, and huge outputs are truncated with a "show more" button.
- The view follows light, dark, and high-contrast themes, with SVG icons.
- Added unit, jsdom webview, and VS Code integration test suites.

## 0.0.1

- Initial VS Code extension package for StemCode ACP mode.
- Added chat view, process controls, editor-context commands, Git diff review, logs, and settings commands.
