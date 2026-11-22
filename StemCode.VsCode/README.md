# StemCode for VS Code

StemCode for VS Code brings StemCode chat, code review, planning, and editor context into Visual Studio Code.

The extension does not bundle the StemCode engine. It starts the local terminal command `stemcode --acp`, so the StemCode CLI must be installed and configured before the VS Code extension can work.

## Requirements

- Visual Studio Code 1.80.0 or newer.
- StemCode CLI installed and available as `stemcode` in your terminal.
- A completed first run of `stemcode` so provider credentials, model selection, and onboarding are ready.

## Install StemCode CLI First

macOS / Linux:

```bash
curl -fsSL https://raw.githubusercontent.com/rizwan3d/StemCode/master/scripts/install.sh | bash
```

Windows PowerShell:

```powershell
irm https://raw.githubusercontent.com/rizwan3d/StemCode/master/scripts/install.ps1 | iex
```

After installation, restart your terminal if `stemcode` is not found, then run:

```bash
stemcode
```

Finish provider setup in the terminal before using the VS Code extension.

## Install The Extension

Install from the Visual Studio Marketplace:

```text
ext install growbitlab.stemcode
```

Then open the StemCode view in VS Code or run `StemCode: Open Chat` from the Command Palette.

## Features

**Chat that works like a coding agent**

- Streaming answers with rendered Markdown, tables, and syntax-highlighted code blocks. Each code block has **Copy**, **Insert at cursor**, and **Open in new editor** actions.
- Tool calls appear as compact rows (Read, Search, Edit, Run, ...) with live status: a spinner while running, a check on success, an error mark on failure. Edits expand to an inline diff with `+/-` counts.
- **Inline approvals:** permission requests show up as cards in the chat. Press `1`–`9` to choose an option, or `Esc` to cancel. Auto-select countdowns are shown on the card.
- A thinking section that stays collapsed, a live progress line ("Running npm test…") with elapsed time, and per-turn stats.
- A working-set strip above the input that summarizes plan progress, subagents, and changed files. Click it to expand and open any file.

**Composer**

- Auto-growing input. `Enter` sends, `Shift+Enter` adds a new line, `Esc` stops the response, and `↑`/`↓` move through prompt history (kept across reloads).
- A **context chip** for the active editor. Your current file or selection is attached to prompts automatically; click the chip to turn this off.
- `/` command and `@` file suggestions, model / reasoning / thinking menu, profile picker, and a context-usage ring.
- Messages typed while StemCode is busy are queued and sent when the current turn ends.

**Editor integration**

- Right-click → **StemCode** to explain or send a selection, send or review the current file, or review your Git diff.
- `Ctrl+Shift+Alt+S` (`Cmd+Shift+Alt+S` on macOS) opens the chat. `Ctrl+Shift+Alt+L` sends the current selection.
- File paths in answers are clickable and open at the referenced line.
- A status banner with **Retry** and **Logs** buttons appears if the CLI fails to start.
- Session history (resume, fork, export), a settings page, a skills marketplace, and process controls (start, stop, restart, logs).
- Follows your VS Code theme, including light and high-contrast themes.

**Every CLI command**

- All `stemcode` CLI slash commands work in the chat. The `/` suggestion list comes from the CLI you're running, so it includes custom commands from `.stemcode/commands`.
- **StemCode: Run Slash Command...** in the Command Palette lists every command. Commands that take arguments are prefilled in the composer.
- Common actions also have their own palette entries: undo/redo AI edits, compact, fork, clone, session tree, resume, export, import, share, trajectory, model/provider selection, onboarding, `/init`, doctor, permissions, MCP, codebase index, language servers, reload, update, and version.
- The terminal-only commands map to VS Code: `/voice` (and `/voice setup`, `/voice update`) runs voice dictation, `/trajectory` exports the trajectory HTML and opens it, `/copy` uses the VS Code clipboard (this also works in remote windows), and `/exit` stops the CLI process. `/export` offers to open the exported file.
- Commands that switch sessions (`/fork`, `/clone`, `/import`, `/resume`, `/tree`) reload the chat with the new session's history.

## Skills

Open **Settings (gear) → Workspace → Skills** in the StemCode chat view to manage data-only skills without leaving VS Code:

- **Browse** a marketplace to list the skills it offers (read from the repo's `stemcode-marketplace.json` index; output shows in chat).
- **Install** a skill by id from a configured marketplace, optionally overwriting existing files.
- **Uninstall** an installed skill, removing only its tracked files.
- **Add** a marketplace by `owner/repo`, or **Remove** a configured one.

The panel reads state from `.stemcode/skills/marketplaces.json` and `.stemcode/skills/installed.json`, and runs the same `/skill` commands available in the CLI.

## Settings

| Setting | Default | Description |
| --- | --- | --- |
| `stemcode.command` | `stemcode` | Command used to start StemCode. |
| `stemcode.args` | `["--acp"]` | Arguments passed to the StemCode CLI. |
| `stemcode.workingDirectory` | workspace root | Working directory for the StemCode process. |
| `stemcode.acpAuthenticationToken` | empty | Optional ACP auth token. Used only when the server advertises `authMethods: ["token"]`. |
| `stemcode.autoStart` | `false` | Start StemCode automatically when VS Code starts. |
| `stemcode.logLevel` | `info` | Extension log level. |

When ACP authentication is enabled, the extension resolves the token in this order:

1. VS Code `SecretStorage` key `stemcode.acpAuthToken`
2. `stemcode.acpAuthenticationToken` setting
3. `STEMCODE_ACP_AUTH_TOKEN` environment variable

## Troubleshooting

### `stemcode` is not found

Install the StemCode CLI, restart your terminal, and make sure `stemcode` is available on `PATH`.

### The extension starts but cannot connect

Run `stemcode` once in a terminal and finish provider onboarding. The extension starts StemCode in ACP mode and expects local configuration to already exist.

## Development

```bash
npm ci
npm test                 # typecheck, build, lint, then unit + webview (jsdom) tests
npm run test:integration # runs the extension in a real VS Code against a fake ACP agent
npm run package:vsix     # build a .vsix
```

## License

Apache License 2.0. See `LICENSE.txt`.
