# StemCode for Visual Studio

StemCode for Visual Studio brings StemCode chat, code review, planning, and editor context into Visual Studio 2022 and later.

The extension runs the StemCode CLI on your machine (`stemcode --acp`). The CLI is not bundled with the extension: if it is missing, the extension offers to install it for you from GitHub.

## Requirements

- Visual Studio 2022 (17.0) or later, on 64-bit Windows.
- The StemCode CLI. The extension can install it for you (see below).
- One completed run of `stemcode` in a terminal, so that provider credentials, model selection, and onboarding are set up.

## Install the StemCode CLI

The first time the chat opens without a CLI, StemCode offers to install it. Choosing **Yes** runs the official installer from GitHub in Windows PowerShell and puts `stemcode.exe` in `%LOCALAPPDATA%\Programs\StemCode\bin`. Nothing is downloaded without your OK.

To install it yourself, run this in Windows PowerShell:

```powershell
irm https://raw.githubusercontent.com/rizwan3d/StemCode/master/scripts/install.ps1 | iex
```

If `stemcode` isn't found afterwards, restart your terminal. Then run:

```powershell
stemcode
```

Finish provider setup in the terminal. Then open **View → Other Windows → StemCode Chat**, or press `Ctrl+Shift+Alt+S`.

To use a CLI somewhere else, set **Command** in **Tools → Options → StemCode**. Once a day the extension checks GitHub for a newer CLI release and offers to install it; StemCode restarts after the update.

## Features

**Chat that works like a coding agent**

- Answers stream in with rendered Markdown, tables, and code blocks.
- Tool calls (Read, Search, Edit, Run, ...) show as compact rows with live status. File edits expand to an inline diff.
- **Approvals.** A permission request opens a card in the chat. Press `1`–`9` to choose an option, or `Esc` to cancel.
- Thinking stays in a collapsed section. Each turn ends with its stats: time, tokens, tool rounds, and retries.
- A **Files changed** summary links to each edited file.

**Composer**

- `Enter` sends and `Shift+Enter` adds a new line. `Ctrl+R` or the microphone button starts voice dictation, and `Esc` stops it.
- Type `/` for suggestions. The list comes from the CLI you're running, so it includes your custom `.stemcode/commands`.
- Messages you type while StemCode is busy are queued. They're sent in order when the current turn ends.
- Pickers for model, reasoning effort, thinking, and session history, plus a settings panel and a skills marketplace.

**Editor integration**

- Right-click → **StemCode** to explain or send a selection, send or review the current file, or review your Git diff.
- `Ctrl+Shift+Alt+S` opens the chat. `Ctrl+Shift+Alt+L` sends the current selection.
- File paths in answers are links. Click one to open the file at the referenced line.
- If the CLI can't start or stops unexpectedly, a banner offers **Retry** and **Logs**.

**Every CLI command**

- Every `stemcode` slash command works in the chat.
- **Tools → StemCode** lists the common actions:
  - Run Slash Command
  - **Editor:** explain or send a selection, send or review the current file, review the Git diff, plan or apply changes
  - **Session:** undo and redo AI edits, compact, copy the last answer, resume, tree, fork, clone, export, trajectory, import, share
  - **Model and Provider:** select a model or provider, re-run onboarding
  - **Workspace and Diagnostics:** init, reload, index, doctor, session info, permissions, MCP, language servers, background terminals
  - **Voice:** dictation, setup, model update
  - **Process:** start, stop, restart, open logs, settings, check for CLI updates, show the CLI version
- Commands that only work in the terminal are mapped to Visual Studio:
  - `/copy` copies the last answer to the clipboard.
  - `/exit` stops the CLI process.
  - `/trajectory` exports the trajectory HTML and opens it.
  - `/voice`, `/voice setup`, and `/voice update` run voice dictation and setup.
  - After `/export`, you're offered the exported file to open.
- `/fork`, `/clone`, `/import`, `/resume`, and `/tree` switch sessions. The chat then reloads with the new session's history.

## Settings

Find these in **Tools → Options → StemCode**.

The names match the VS Code extension's `stemcode.*` settings.

| Setting | Default | Description |
| --- | --- | --- |
| **CLI** › Command | empty | Command or full path of the CLI (`stemcode.command`). If empty, `stemcode` is looked up in `%LOCALAPPDATA%\Programs\StemCode\bin`, on `PATH`, and in the npm, pnpm, bun, and dotnet global tool folders. |
| **CLI** › Arguments | empty | Extra arguments, added after `--acp --surface visual_studio` (`stemcode.args`). |
| **Session** › Working Directory | open folder or solution folder | Folder StemCode works in (`stemcode.workingDirectory`). |
| **Session** › ACP Authentication Token | empty | Sent only when the ACP server asks for token authentication (`stemcode.acpAuthenticationToken`). If empty, `STEMCODE_ACP_AUTH_TOKEN` is used. |
| **Session** › Auto Start | `true` | Start the CLI when the chat window opens (`stemcode.autoStart`). When off, it starts with your first message. |
| **Install and Updates** › Install CLI When Missing | `true` | Offer to install the CLI with the PowerShell installer from GitHub. |
| **Install and Updates** › Check for CLI Updates | `true` | Check GitHub releases for a newer CLI, at most once every 24 hours. |
| **Diagnostics** › Log Level | `Info` | Detail level of the extension log (`stemcode.logLevel`). |

Most settings take effect after **Tools → StemCode → Restart**.

## Troubleshooting

### `stemcode` isn't found

Click **Retry** in the banner and accept the install prompt, or run the PowerShell command above and then click **Retry**. You can also set **Command** to the CLI's full path in **Tools → Options → StemCode**.

### The chat shows "couldn't start a session"

Run `stemcode` once in a terminal and finish provider onboarding. Then click **Retry** in the banner.

### Anything else

Use **Tools → StemCode → Open Logs**, or the **Logs** button on the banner. Logs are written to `%LOCALAPPDATA%\StemCode\Logs`.

## Source and license

Source code: [github.com/rizwan3d/StemCode](https://github.com/rizwan3d/StemCode). Licensed under Apache License 2.0.
