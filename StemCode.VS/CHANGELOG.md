# Changelog

## Unreleased

### Changed

- The StemCode CLI is no longer bundled in the VSIX (now about 390 KB). When the CLI is missing, the extension offers to install it with the official PowerShell installer from GitHub (`scripts/install.ps1`), into `%LOCALAPPDATA%\Programs\StemCode\bin`.
- CLI update checks use the latest GitHub release instead of npm. Accepting an update stops StemCode, runs the installer, and starts StemCode again.
- The chat window follows the Visual Studio color theme (Dark, Light, Blue, and custom themes) and repaints when you switch themes. It uses the Visual Studio UI font and themed scroll bars.
- New header with a status dot and icon buttons for New chat, Sessions, and Settings. The composer has a placeholder, a Context chip, a model picker that shows the current model, and a send/stop button.
- **Tools → Options → StemCode** is regrouped into CLI, Session, Install and Updates, and Diagnostics, with names that match the VS Code extension's `stemcode.*` settings. The ACP token is masked. Saved values are kept.

### Added

- Microphone button in the chat composer for voice dictation. While it listens, the button turns into a stop button; click it or press `Esc` to stop. Stopping ends the whole voice process tree, so the microphone is released.
- Dictated text goes in at the caret, replacing any selection, instead of always being appended to the end.
- **Auto Start** setting (`stemcode.autoStart`). When off, the CLI starts with your first message instead of when the chat window opens.

## 1.1.0

### Added

- **Tools → StemCode** menu with the same commands as the VS Code extension's command palette:
  - Run Slash Command
  - Editor actions
  - Session: undo and redo, compact, fork, clone, tree, resume, export, trajectory, import, share, and copy
  - Model and provider selection, and onboarding
  - Workspace and diagnostics: init, reload, index, doctor, session info, permissions, MCP, and language servers
  - Voice
  - Start, Stop, Restart, Open Logs, Settings, Check for CLI Updates, and Show CLI Version
- Keyboard shortcuts: `Ctrl+Shift+Alt+S` opens the chat, and `Ctrl+Shift+Alt+L` sends the current selection.
- A status banner with **Retry** and **Logs** buttons appears when the CLI can't start or stops unexpectedly.
- Commands that only work in the terminal are mapped to Visual Studio:
  - `/copy` copies the last answer to the clipboard.
  - `/exit` stops the CLI.
  - `/trajectory` exports the trajectory HTML and opens it.
  - `/voice`, `/voice setup`, and `/voice update` run voice dictation and setup.
  - `/export` offers to open the exported file.
- Permission requests now take `1`–`9` to choose an option, and `Esc` to cancel.
- Unit tests for the slash-command catalog and export parsing (`StemCode.VS.Tests`).

### Changed

- The `/` suggestions now come from the CLI over ACP (`available_commands_update`), so custom `.stemcode/commands` appear. The built-in list is only used until the CLI sends its own.
- `/fork`, `/clone`, `/import`, `/resume`, and `/tree` now clear the chat and replay the new session's history, including your own messages (`session_history_reset`).
- Agent text that follows a tool call now starts a new message below that call, so the transcript stays in order.
- Prompts sent from the editor menu are queued while a turn is running, just like typed messages.
- Sending a message after `/exit` or a crash starts StemCode again.
- Finding the CLI no longer blocks the Visual Studio UI thread.

### Fixed

- Stopping StemCode, or closing or crashing Visual Studio, no longer leaves orphaned CLI processes. An npm install starts `stemcode.cmd`, which starts node, which starts `StemCode.CLI.exe`. Before, only the first of these was killed. The whole tree now lives in a Windows job object and ends together.
- Resuming a session from the Sessions panel no longer clears the history the CLI just replayed.
- When the CLI exits mid-turn, the turn now ends with an error instead of hanging for up to 10 minutes.
- Restart no longer lets the previous read loop consume the new process's output.
- Concurrent writes to the CLI (prompt, cancel, and permission replies) are now serialized.
- The extension log can be opened while it's being written.

## 1.0.4

- Voice dictation in the chat composer (`Ctrl+R`).
- Marketplace publisher moved to `growbitlab`.
