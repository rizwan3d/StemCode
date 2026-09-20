# StemCode Documentation

StemCode is an AI coding agent for people who want an assistant that can work directly inside a repository while still respecting local permissions, approval prompts, and workspace policy. It runs as a desktop app, the `stemcode` terminal command, a VS Code extension, a Visual Studio extension, and an ACP-compatible editor server. It also includes Language Server Protocol (LSP) powered code intelligence for semantic navigation and diagnostics.

This guide contains the setup, reference, and technical material for StemCode. The root README is the product overview; this document is the handbook for installation, daily use, LSP-powered code intelligence, safety controls, integration, automation, and advanced workspace customization.

## Contents

- [Install](#install)
- [First Run](#first-run)
- [Desktop Workflow](#desktop-workflow)
- [Terminal Workflow](#terminal-workflow)
- [Voice Input](#voice-input)
- [VS Code Extension](#vs-code-extension)
- [Visual Studio Extension](#visual-studio-extension)
- [JetBrains Extension](#jetbrains-extension)
- [ACP Editor Integration](#acp-editor-integration)
- [Review Automation](#review-automation)
- [Code Intelligence](#code-intelligence)
- [Codebase Indexing](#codebase-indexing)
- [Providers and Models](#providers-and-models)
- [Profiles and Subagents](#profiles-and-subagents)
- [Permissions and Sandboxing](#permissions-and-sandboxing)
- [Workspace Files](#workspace-files)
- [Team Memory](#team-memory)
- [Skills and Custom Agents](#skills-and-custom-agents)
- [MCP Servers](#mcp-servers)
- [Memory, Audit, and Hooks](#memory-audit-and-hooks)
- [Privacy and Local Data](#privacy-and-local-data)
- [Troubleshooting](#troubleshooting)
- [Build From Source](#build-from-source)

## Install

### Desktop App

Download the latest release for your platform:

| Platform | Download |
| --- | --- |
| Windows x64 | [Installer](https://github.com/rizwan3d/StemCode/releases/latest/download/StemCode.Desktop-win-x64-setup.exe) |
| Linux x64 | [Zip](https://github.com/rizwan3d/StemCode/releases/latest/download/StemCode.Desktop-linux-x64.zip) |
| Linux arm64 | [Zip](https://github.com/rizwan3d/StemCode/releases/latest/download/StemCode.Desktop-linux-arm64.zip) |
| macOS x64 | [Zip](https://github.com/rizwan3d/StemCode/releases/latest/download/StemCode.Desktop-osx-x64.zip) |
| macOS arm64 | [Zip](https://github.com/rizwan3d/StemCode/releases/latest/download/StemCode.Desktop-osx-arm64.zip) |

Release downloads are published at:

```text
https://github.com/rizwan3d/StemCode/releases/latest
```

New release assets include `SHA256SUMS` beside the downloads. The release pipeline verifies every checksum matches its asset before publishing, and GitHub release workflows also generate artifact attestations that establish SLSA build provenance for the checksummed assets. For manual downloads, compare the published SHA256 hash with your downloaded file before running it. To verify provenance with GitHub CLI, run `gh attestation verify path/to/asset -R rizwan3d/StemCode`.

### CLI

The CLI ships as a self-contained, AOT-compiled binary exposed as the `stemcode`
command. Choose whichever installer fits your environment — they all install the
same binary from the same release assets.

#### Install script

Curl:

```bash
curl -fsSL https://raw.githubusercontent.com/rizwan3d/StemCode/master/scripts/install.sh | bash
```

Windows PowerShell:

```powershell
irm https://raw.githubusercontent.com/rizwan3d/StemCode/master/scripts/install.ps1 | iex
```

The installers show step status and download progress when run in an interactive terminal. Set `STEMCODE_NO_PROGRESS=1` to keep output compact in CI logs. Restart your terminal if `stemcode` is not found immediately after installation.

#### npm, bun, and pnpm

```bash
npm install -g stemcode
# or
bun add -g stemcode
# or
pnpm add -g stemcode
```

The [`stemcode`](packaging/npm) package is a thin installer. On `postinstall` (or on the first `stemcode` run) it downloads the matching `StemCode.CLI-<rid>.zip` release asset, verifies it against the published `SHA256SUMS`, and unpacks the binary — no .NET toolchain required. Because `bun install` skips `postinstall` scripts by default, bun fetches the binary lazily the first time you run `stemcode`. Supported targets: `win-x64`, `osx-x64`, `osx-arm64`, `linux-x64`, `linux-arm64`. Useful overrides: `STEMCODE_SKIP_DOWNLOAD=1`, `STEMCODE_CLI_TAG`, `STEMCODE_CLI_BASE_URL`.

#### NuGet library

The release workflows also pack the `StemCode` library and publish it to NuGet.org for every `v*` tag release. The CLI itself is distributed through the installers above, not as a NuGet package.

#### Checksum verification

Every installer verifies the downloaded archive against `SHA256SUMS` (the install scripts also fall back to the SHA256 digest from GitHub release metadata) before extraction. Checksum verification is mandatory — installation fails if the checksum cannot be validated.

## First Run

Start StemCode:

```bash
stemcode
```

StemCode will guide you through provider setup:

1. Choose a setup type: subscription account, API key provider, OpenAI-compatible provider, or local provider.
2. Choose a provider from the matching submenu when needed.
3. Enter an API key, sign in with ChatGPT Plus/Pro, Claude Pro/Max, or GitHub Copilot, enter a custom compatible base URL, or use a local provider default.
4. Let StemCode discover available models.
5. Open a desktop workspace or use the current terminal directory.
6. Start a new section or resume an existing one.

In terminal runs, `--provider-auth-key <key>` can supply the provider API key when onboarding asks for it.

If you already know the provider settings you want, you can skip the interactive onboarding prompts by setting `STEMCODE_PROVIDER`, `STEMCODE_MODEL`, `STEMCODE_THINKING`, optional `STEMCODE_REASONING`, optional `STEMCODE_PROJECT_NAME`, and `STEMCODE_API_KEY` before the first run. StemCode treats that as a complete headless setup and saves it as the active provider profile. When `STEMCODE_PROJECT_NAME` is set, StemCode sends that value as the `X-Project` header on provider requests.

PowerShell example:

```powershell
$env:STEMCODE_PROVIDER="openrouter"
$env:STEMCODE_MODEL="poolside/laguna-m.1:free"
$env:STEMCODE_THINKING="on"
$env:STEMCODE_REASONING="high"
$env:STEMCODE_PROJECT_NAME="customer-portal"
$env:STEMCODE_API_KEY="PASTE_NEW_ROTATED_KEY_HERE"

stemcode -p "Say hello in one short line"
```

Bash example:

```bash
export STEMCODE_PROVIDER="openrouter"
export STEMCODE_MODEL="poolside/laguna-m.1:free"
export STEMCODE_THINKING="on"
export STEMCODE_REASONING="high"
export STEMCODE_PROJECT_NAME="customer-portal"
export STEMCODE_API_KEY="PASTE_NEW_ROTATED_KEY_HERE"

stemcode -p "Say hello in one short line"
```

If StemCode detects incomplete local provider setup, it asks whether to reconfigure. Choose reconfigure when a previous setup was interrupted or credentials were not saved. If provider validation fails after setup, StemCode offers to run onboarding again.

Use `/onboard` in an active desktop or terminal session to re-run provider setup later. You can also use `/setting provider` or the `/setting` picker. The command opens setup-type and provider submenus, supports every provider listed below, and switches the active session to the validated provider and selected default model.

When a newer StemCode release is available, startup can ask whether to update now or skip. One-shot prompt runs do not show the startup update prompt.

### Provider Options

| Group | Provider | Credential method | Notes |
| --- | --- | --- | --- |
| Subscription based | OpenAI ChatGPT Plus/Pro | Browser sign-in | Uses OAuth with local callback port `1455`. |
| Subscription based | Anthropic Claude Pro/Max | Browser sign-in | Uses OAuth with local callback port `53692`. |
| Subscription based | GitHub Copilot | Browser device sign-in | Uses GitHub device-code login. Leave the Enterprise URL/domain prompt blank for `github.com`. |
| API key | OpenAI | API key | Uses the OpenAI API. |
| API key | Anthropic | API key | Uses the Anthropic OpenAI-compatible endpoint. |
| API key | Google AI Studio | API key | Uses the OpenAI-compatible Gemini endpoint. |
| API key | OpenRouter | API key | Uses the OpenRouter OpenAI-compatible endpoint. |
| API key | OpenCode Zen | API key | Uses the OpenCode Zen OpenAI-compatible endpoint at `https://opencode.ai/zen/v1`. |
| API key | Kilo Code | API key | Uses Kilo's OpenRouter-compatible gateway. |
| API key | Cerebras | API key | Uses the Cerebras OpenAI-compatible endpoint. |
| API key | Groq | API key | Uses the Groq OpenAI-compatible endpoint. |
| API key | DeepSeek | API key | Uses the DeepSeek OpenAI-compatible endpoint at `https://api.deepseek.com/`. |
| API key | Ollama Cloud | API key | Uses Ollama's hosted native chat and tags APIs. |
| OpenAI-compatible provider | OpenAI-compatible provider | Base URL and API key | Use for local or third-party compatible APIs. |
| Local provider | Ollama | None | Uses Ollama's local OpenAI-compatible endpoint at `http://127.0.0.1:11434/v1`. |
| Local provider | LM Studio | Base URL and API key | Uses LM Studio's local OpenAI-compatible endpoint. Leave the base URL empty to use `http://127.0.0.1:1234/v1`. |

Secrets are stored through platform credential storage where supported. ChatGPT Plus/Pro, Claude Pro/Max, and GitHub Copilot sign-in store refreshable account credentials locally.

## Desktop Workflow

The desktop app is built around workspaces, sections, chat, and controls.

### Workspaces

Open a local folder to make it the active workspace. StemCode remembers recent workspaces so you can return later.

### Sections

A section is a saved local conversation thread tied to a workspace. Sections preserve conversation history, active model, profile, thinking mode, plan state, and session state when available.

Use sections for separate tasks:

- One section for a feature.
- One section for a bug fix.
- One section for a review.
- One section for planning.

### Conversation

Type a prompt and let StemCode inspect, plan, edit, run commands, or ask for approval depending on the active profile and permissions.
Type `/` in the desktop prompt to open command suggestions. Use Up/Down and Enter to choose a command, or Shift+Enter for multiline input.
Start input with `!` to run the rest as a local shell command directly, for example `!dotnet test`. Direct shell input is treated as user-entered terminal work and does not ask the agent for a tool approval.
Start input with `!!` to run the rest as a background terminal whose output streams live, for example `!!dotnet watch`. Manage these background terminals with `/terminals`.

### Controls

The desktop controls expose common actions:

- Refresh session state.
- Switch model.
- Configure budget controls from a local workspace file or a cloud API.
- Toggle thinking mode.
- Switch profile.
- View help, model picker, permissions, and rules.
- Add permission overrides.
- Undo or redo tracked file edits.

Budget controls are disabled by default. They become active only after you enable them with `/budget local` or `/budget cloud`, or when a `.stemcode/budget-controls.*.json` file already exists in the active workspace. While disabled, no usage is recorded, no tracking file is created, and provider requests are never blocked; `/budget status` reports `Disabled`.

Budget controls can run in local mode or cloud mode. Local mode asks for the monthly budget USD, alert threshold percent, and input, cached-input, and output prices per 1M tokens, then creates and updates `.stemcode/budget-controls.local.json` in the active workspace. Cloud mode asks for the budget API URL and auth key; the URL is saved with user settings and the key is stored through the platform credential store. In the terminal, use `/budget`, `/budget local`, `/budget cloud`, or `/budget status`.

Cloud budget APIs use `Authorization: Bearer <auth-key>`.

GET returns the current budget state:

```json
{
  "monthlyBudgetUsd": 100,
  "spentUsd": 25.5,
  "alertThresholdPercent": 80
}
```

GET response JSON Schema:

```json
{
  "type": "object",
  "additionalProperties": false,
  "required": ["monthlyBudgetUsd", "spentUsd", "alertThresholdPercent"],
  "properties": {
    "monthlyBudgetUsd": {
      "type": ["number", "null"],
      "minimum": 0
    },
    "spentUsd": {
      "type": "number",
      "minimum": 0
    },
    "alertThresholdPercent": {
      "type": "integer",
      "minimum": 1,
      "maximum": 100
    }
  }
}
```

POST receives only the tokens consumed by the last LLM call:

```json
{
  "inputTokens": 1234,
  "cachedInputTokens": 250,
  "outputTokens": 600
}
```

POST request JSON Schema:

```json
{
  "type": "object",
  "additionalProperties": false,
  "required": ["inputTokens", "cachedInputTokens", "outputTokens"],
  "properties": {
    "inputTokens": {
      "type": "integer",
      "minimum": 0
    },
    "cachedInputTokens": {
      "type": "integer",
      "minimum": 0
    },
    "outputTokens": {
      "type": "integer",
      "minimum": 0
    }
  }
}
```

POST should add that delta to the backend usage database and return the updated budget state with the same JSON shape as GET.

## Terminal Workflow

### Interactive Mode

```bash
stemcode
```

Interactive mode opens the terminal UI with conversation history, live activity, prompts, and status.

### One-Shot Prompt

```bash
stemcode "Find risky changes in this branch"
```

To override the sandbox policy for a specific run, pass `--sandbox-mode`:

```bash
stemcode --sandbox-mode danger-full-access "Apply the requested refactor"
```

### Prompt From Standard Input

```bash
git diff --stat | stemcode --stdin --profile review
```

### Resume a Session

When you exit, StemCode prints a session resume command. You can also resume directly:

```bash
stemcode --session <session-guid>
```

### CLI Options

| Option | Description |
| --- | --- |
| `--acp` | Run an Agent Client Protocol server over stdin/stdout for compatible editors and tools. |
| `--interactive` | Start the terminal UI explicitly. |
| `--stdin` | Read one-shot prompt text from standard input. |
| `--json` | Write one-shot prompt or command output as a JSON object. |
| `-y, --yes` | Approve promptable tool requests for this run while preserving explicit deny rules. |
| `-p, --prompt <text>` | Run one prompt and print the response. |
| `--sandbox-mode <mode>` | Override sandbox mode for this run. Values: `read-only`, `workspace-write`, `danger-full-access`. |
| `--provider-auth-key <key>` | Use this key when provider API-key onboarding asks for a credential. |
| `--session <id>` | Resume an existing session. |
| `--section <id>` | Compatibility alias for `--session`. |
| `--profile <name>` | Start with a profile. |
| `--thinking <on\|off>` | Start with thinking on or off. |
| `--context-size <size>` | Cap the context window for this run. Presets include `8k`, `32k`, `64k`, `125k`, and `256k`; custom values such as `96k` or `96000` are also accepted. The effective size never exceeds the context window reported by the provider API. Omit this option to keep the existing behavior and defaults. |
| `-v, --version` | Show the StemCode CLI version. |
| `--doctor` | Run system diagnostics and print the doctor report. |
| `--no-update-check` | Skip checking for application updates on startup. |
| `--no-old-reader` | Resume a section without replaying old messages to the screen. |
| `-h, --help` | Show CLI help. |

## Terminal Commands

| Command | Description |
| --- | --- |
| `/a` | Alias for `/agent`. |
| `/agent` | List available subagents for delegated work. |
| `/allow <tool-or-tag> [pattern]` | Add a session-scoped allow override for a tool/tag and optional target pattern. |
| `/autocommit [on\|off\|status]` | Show or toggle automatic git commits for AI-made workspace changes. |
| `/budget [status\|local [path]\|cloud]` | Show or configure budget controls from local or cloud settings. |
| `/clone` | Duplicate the current session at the current position. |
| `/compact [retained-turns]` | Manually compact the session context. |
| `/config` | Show provider, config path, active profile, thinking, and active model details. |
| `/copy` | Copy the last agent message to the clipboard. |
| `/disableanalytics` | Disable product analytics for this workspace. |
| `/doctor` | Show comprehensive system diagnostics for StemCode. |
| `/deny <tool-or-tag> [pattern]` | Add a session-scoped deny override for a tool/tag and optional target pattern. |
| `/exit` | Exit the interactive shell. |
| `/export [json\|html] [path]` | Export the current session as JSON or HTML. |
| `/fork [turn-number]` | Create a new fork from a previous user message. |
| `/help` | List the available shell commands and their usage. |
| `/import <json-path>` | Import a session from JSON and switch to the imported copy. |
| `/index [update\|status\|rebuild\|list] [limit]` | Update, rebuild, inspect, or list the local codebase index. See [Manual Index Updates](#manual-index-updates). |
| `/init [recommended\|minimal\|custom]` | Choose and initialize workspace-local StemCode files. |
| `/lessons [status\|on\|off\|list [limit]\|search <query>\|save <trigger> \| <problem> \| <lesson>\|edit <id> ...\|delete <id>]` | Manage local lesson memory; off by default and can inject relevant lessons when enabled. |
| `/lsp [status\|refresh\|file <path> [refresh]]` | Show discovered language servers, or inspect which ones apply to a file. See [Code Intelligence](#code-intelligence). |
| `/mcp` | Show configured MCP servers, custom tool providers, and discovered dynamic tools. |
| `/models` | Open the active model picker. |
| `/new` | Start a fresh section without carrying over prior context. |
| `/onboard` | Re-run provider onboarding and switch the active session to the new provider. |
| `/permissions` | Show the current permission summary and session override guidance. |
| `/profile <name>` | Switch the active agent profile for subsequent prompts. |
| `/provider [list\|<name>]` | List saved providers or switch the active session to another saved provider. |
| `/reasoning [show\|<none\|minimal\|low\|medium\|high\|xhigh\|max>]` | Show or set provider reasoning effort for subsequent prompts. |
| `/redact [on\|off]` | Show or toggle secret redaction for session output. |
| `/redo` | Re-apply the most recently undone file edit transaction. |
| `/reload` | Reload keybindings, extensions, skills, prompts, and themes. |
| `/resume [session-id]` | Resume a different session. |
| `/rules` | List the effective permission rules in evaluation order. |
| `/session` | Show session info and stats. |
| `/setting [model\|profile\|thinking\|provider\|budget\|workspace\|permissions\|tools\|summary]` | Open the settings picker for configurable session and workspace options. |
| `/setup-sandbox` | Set up Windows sandbox support for restricted shell commands. |
| `/share` | Share the current session as a secret GitHub gist. |
| `/skill [marketplace add <owner/repo> [--ref <ref>] [--alias <alias>]\|marketplace remove <alias>\|browse <marketplaceAlias>\|install <skillId>@<marketplaceAlias> [--force]\|list\|uninstall <skillId>]` | Manage data-only skill marketplaces and installs. |
| `/terminals [view [<terminal-id>]\|stop <terminal-id>\|stop all]` | List, view, or stop background terminals for the current session. |
| /thinking [on\|off] | Show or set thinking mode for subsequent prompts. |
| `/tooloutput [compact\|full\|auto]` | Show or toggle whether tool results print their complete output or a compact preview. `auto` follows the active agent profile. |
| `/tree` | Navigate the session tree and switch branches. |
| `/update [now]` | Check for StemCode updates and install the latest release. |
| `/undo` | Roll back the most recent tracked file edit transaction. |
| `/use <model>` | Switch the active model for subsequent prompts. |
| `/version` | Show the current StemCode CLI version. |

The terminal also recognizes `!` and `!!` shell prefixes and `/` command suggestions, described earlier in [Terminal Workflow](#terminal-workflow). See [Code Intelligence](#code-intelligence) for `/lsp` and [Codebase Indexing](#codebase-indexing) for `/index`.

## Tracked File Edits

StemCode tracks edit transactions so `/undo` and `/redo` can revert or re-apply AI-made file changes when the edit happened through a tracked file tool.

Built-in tracked edit tools include:

- `file_write` for replacing or creating a whole file.
- `apply_patch` for patch-style multi-file edits.
- `insert_content` for inserting UTF-8 text before a specific 1-based line in an existing file. Use `totalLines + 1` to append at the end.
- `search_and_replace` for single-file literal or regex replacements, with optional `caseSensitive: false` and `.NET` replacement groups such as `$1` in regex mode.

`insert_content` and `search_and_replace` both preserve the normal tracked-edit flow: they record before/after file state, show compact diff previews, and participate in `/undo`, `/redo`, edit summaries, and automatic AI git commits.

### Custom Slash Commands

Project commands live in `.stemcode/commands/*.md`. User commands live in `~/.stemcode/commands/*.md`. Subdirectories create namespaces with `:`, so `.stemcode/commands/review/security.md` is available as `/review:security`.

Each command file can include front matter:

```markdown
---
name: security-review
description: Review changed files for security risks
args: ["scope"]
---

Review $scope for authentication, injection, secrets, unsafe deserialization, and permission bypasses.
Return findings by severity.
```

Run commands with any arguments after the name:

```text
/security-review latest diff
/fix-tests StemCode.Tests
/release-check v0.0.16
```

Use `$ARGUMENTS` for the full argument string, or name positional arguments in `args` and reference them as `$scope` or `${scope}`. Project commands override user commands with the same name. Built-in command names are reserved.

`/setting` is a keyboard-friendly settings hub. Use it with no arguments to pick a settings area, or jump directly with commands such as `/setting model`, `/setting profile`, `/setting thinking`, `/setting budget status`, `/setting workspace custom`, `/setting permissions`, `/setting tools`, and `/setting summary`. Setting submenus use picker-style rows; Esc returns to the settings menu. `/setting permissions` writes default and sandbox changes to `.stemcode/agent-profile.json`; direct commands like `/permissions` and `/rules` still keep their original text output.

Press F2 in the terminal UI to choose the active model with the same arrow-key picker.
Type `/` in the terminal input to open command suggestions, then use Up/Down and Enter to choose a command.
Start input with `!` to run the rest as a local shell command directly, for example `!git status --short`.
Start input with `!!` to run the rest as a background terminal whose output streams live, for example `!!dotnet watch`. Manage these background terminals with `/terminals`.

### Terminal Input and Keys

The interactive terminal UI adds keyboard controls for editing, navigating history, queueing work, and interrupting a running turn.

#### Queue prompts and commands

You no longer have to wait for the current turn to finish before lining up the next request. When StemCode is busy or streaming, pressing Enter queues the prompt or slash command instead of rejecting it. Queued items run automatically, in order, as soon as the active turn completes.

- The busy status line shows how many requests are waiting, for example `… - 2 queued`.
- A summary line under the input reports the queue depth and reminds you that F4 removes the newest queued item.
- Press F4 while busy to drop the most recently queued submission.
- Queued slash commands run only when StemCode is ready; commands that are unavailable while working stay queued until the turn finishes.

#### Interrupt a running or stuck turn

While a turn is running, the footer shows `Esc: interrupt` and `Esc again: abandon`.

- Press Esc once to request a graceful interrupt. The status changes to `Interrupting` and StemCode cancels the active turn so the backend can stop cleanly.
- Press Esc again if the turn does not stop promptly to abandon it locally. StemCode detaches from the turn, returns to Ready, and ignores any late output that arrives from the abandoned turn. Any queued submissions then start.

#### Edit and scroll

- Ctrl+A selects all input text so you can replace or delete a draft in one step. Selected text is highlighted, and typing, Backspace, or Delete replaces the selection.
- Auto-scroll pauses automatically when you scroll up to read earlier conversation history, so new streamed output does not yank you back to the bottom. Scrolling back to the bottom resumes auto-scroll.

#### Tab-complete file and directory paths

After a `!` or `!!` shell command and a space, Tab completes file and directory paths from the workspace, the same way a normal terminal does. For example, `!cat ./sr` then Tab completes the typed path component while preserving the directory portion exactly as you typed it. Because shell commands usually take more arguments after a path, completing a path does not submit the command — it only fills in the path so you can keep typing. Completing a directory appends a trailing `/` so you can drill in further.

#### Git sidebar

Press F7 to toggle a VS Code-style left panel with the workspace's git state. It shows:

- The current branch.
- Any queued prompts or commands (mirrors the busy-line `… - N queued` count) while StemCode is busy.
- The last 10 commits as `short-hash · message`.
- Staged and changed files, grouped and colored by status (`M`, `A`, `D`, `?` untracked), rendered as `filename (relative/path)`.

Click a file row to open it in VS Code (`code`); if VS Code is not installed, it opens in the operating system's default editor for that file type. The git state refreshes automatically about every two seconds while the panel is open.

Scroll the sidebar when its lists are longer than the window with the mouse wheel over the panel, or with Ctrl+Up/Ctrl+Down (Ctrl+PgUp/Ctrl+PgDn for a page). The header shows the visible line range, for example `12-31/48`. The panel is hidden automatically on very narrow terminals.

When the sidebar is focused, use Up/Down to move the selection and Enter to open the selected file. Git actions are available directly from the keyboard:

- `Alt+S` stages or unstages the selected file.
- `Alt+P` runs `git pull`.
- `Alt+O` runs `git push`.
- `Alt+D` discards the selected file's worktree changes.
- `Alt+C` commits staged changes after prompting for a commit message.
- `Alt+B` opens branch actions to switch branches or create a new one.

### Tool Runtime Settings

Workspace `agent-profile.json` can tune tool timeouts and background terminal retention:

```json
{
  "Application": {
    "Tools": {
      "httpClientTimeoutSeconds": 0,
      "mcpRequestTimeoutSeconds": 0,
      "acpRequestTimeoutSeconds": 0,
      "agentOrchestrationTimeoutSeconds": 0,
      "defaultTimeoutSeconds": 180,
      "maxConcurrentBackgroundTerminalsPerSession": 4,
      "completedBackgroundTerminalTtlSeconds": 300,
      "toolOutput": "compact"
    }
  }
}
```

Set `httpClientTimeoutSeconds` to override the default timeout used by StemCode-managed `HttpClient` instances. Set `mcpRequestTimeoutSeconds` to cap individual MCP request/response cycles for both stdio and HTTP MCP servers. Set `acpRequestTimeoutSeconds` to cap ACP editor prompt requests such as permission or text-entry requests. Set `agentOrchestrationTimeoutSeconds` to add an orchestration-wide timeout for `agent_orchestrate`. A value of `0` keeps the existing default behavior for each setting.

Set `toolOutput` to choose how tool results render in session output: `full` (or `complete`) prints the complete output and `compact` (or `preview`) prints the capped preview. Omit it or leave it unrecognized to keep the compact default. This is the lowest-priority source — a per-agent markdown profile's `toolOutput` front-matter key overrides it for that profile, and the `/tooloutput` command overrides both for the current session (`/tooloutput auto` reverts to the profile/configured default).

Completed background terminals remain readable until `completedBackgroundTerminalTtlSeconds` expires. Running background terminals are stopped when the StemCode process exits.

## Voice Input

StemCode includes a local, on-device voice dictation runtime (`StemCode.Voice`, published as `stemcode-voice`). It uses Whisper.net for speech recognition and PortAudio for cross-platform microphone capture (WASAPI on Windows, CoreAudio on macOS, ALSA/PulseAudio on Linux), so prompts can be dictated instead of typed. The voice runtime is intentionally not AOT-compiled because it hosts the native Whisper library and PortAudio; it is published separately next to the AOT-compiled `stemcode` host. Build it from the `StemCode.Voice` project, which is included in `StemCode.slnx`.

## VS Code Extension

StemCode includes a VS Code extension in `StemCode.VsCode`. It opens a StemCode chat view in the auxiliary bar and starts the local StemCode ACP process with:

```bash
stemcode --acp
```

Run `stemcode` once before using the extension so provider onboarding, credentials, and the default model are already configured.

Install from the Visual Studio Marketplace:

```text
ext install rizwan3d.stemcode
```

The Marketplace item is:

```text
https://marketplace.visualstudio.com/items?itemName=rizwan3d.stemcode
```

GitHub releases also publish an installable VSIX asset:

```text
StemCode.VsCode-<version>.vsix
```

### Extension Commands

| Command | Purpose |
| --- | --- |
| `StemCode: Open Chat` | Open the StemCode chat view. |
| `StemCode: New Chat` | Focus the chat view for a new prompt. |
| `StemCode: Start` | Start the local StemCode ACP process. |
| `StemCode: Stop` | Stop the local StemCode ACP process. |
| `StemCode: Restart` | Restart the local StemCode ACP process. |
| `StemCode: Send Selection` | Send the active editor selection as context. |
| `StemCode: Explain Selection` | Ask StemCode to explain the active selection. |
| `StemCode: Send Current File` | Send the full current editor file as context. |
| `StemCode: Review Current File` | Ask for a review of the current file. |
| `StemCode: Review Git Diff` | Ask for a review of the current workspace Git diff. |
| `StemCode: Plan Changes` | Prefill a planning prompt. |
| `StemCode: Apply Suggested Changes` | Ask StemCode to apply the previous suggested change. |
| `StemCode: Open Logs` | Show extension logs. |
| `StemCode: Open Settings` | Open the extension settings surface. |

### Extension Settings

| Setting | Default | Purpose |
| --- | --- | --- |
| `stemcode.command` | `stemcode` | Command used to start StemCode. |
| `stemcode.args` | `["--acp"]` | Arguments passed to the StemCode CLI. |
| `stemcode.workingDirectory` | workspace root | Working directory for the StemCode process. |
| `stemcode.autoStart` | `false` | Start StemCode automatically when VS Code starts. |
| `stemcode.logLevel` | `info` | Extension log level. |

### Extension Development

Build and package locally:

```bash
cd StemCode.VsCode
npm ci
npm run lint
npm run package
npm run package:vsix
```

The package command creates an installable `.vsix`. Install a local package with:

```bash
code --install-extension stemcode-<version>.vsix
```

### Extension Publishing

The release workflow `.github/workflows/release.yml` packages the extension as `StemCode.VsCode-<version>.vsix` and publishes it to GitHub Releases with the CLI, desktop, and NuGet assets. The signed release variant `.github/workflows/release-signing.yml` does the same when that workflow is used. Both workflows publish `SHA256SUMS`, push the `StemCode` library package to NuGet.org, and generate GitHub artifact attestations for the generated release assets.

The Marketplace CD workflow `.github/workflows/vscode-extension-cd.yml` publishes the extension to the Visual Studio Marketplace. It runs for `v*` tags and manual dispatch. For tag builds, the workflow removes the leading `v` and applies that value to `StemCode.VsCode/package.json` with `npm version --no-git-tag-version` before packaging.

Required repository configuration:

```text
NUGET_USER
VSCE_PAT
```

Create `NUGET_USER` in GitHub repository secrets or variables with the NuGet.org profile name that owns the target packages when it differs from the GitHub repository owner. If it is unset, the release workflows default to `github.repository_owner`. In NuGet.org, create a Trusted Publishing policy for this repository and workflow file name (`release.yml` and `release-signing.yml`; include `production-release` as the environment if you want the policy restricted to that GitHub Actions environment). The release workflow requests `id-token: write`, uses `NuGet/login@v1` to exchange the GitHub Actions OIDC token for a short-lived NuGet API key, and then publishes with `dotnet nuget push`. Create `VSCE_PAT` in Azure DevOps with Marketplace Manage scope and access to the `rizwan3d` Visual Studio Marketplace publisher. The Marketplace workflow publishes through `@vscode/vsce`, uploads the generated `.vsix` artifact, and uses the `vscode-marketplace` GitHub environment for deployment approval or environment-level protection rules if configured.

## Visual Studio Extension

StemCode includes a Visual Studio extension in `StemCode.VS`. It opens a tool window inside Visual Studio and starts the local StemCode CLI over ACP.

Before first use:

- Install the StemCode CLI so `stemcode.exe` is available on `PATH`, or set an explicit CLI path in the StemCode Visual Studio options page.
- Run `stemcode` once and complete provider onboarding.

### Local Build

Build the VSIX from a Developer PowerShell for Visual Studio:

```powershell
msbuild StemCode.VS/StemCode.VS.csproj /restore /p:Configuration=Release /p:DeployExtension=false
```

The package is written to:

```text
StemCode.VS/bin/Release/StemCode.VS.vsix
```

### CI and CD

The CI workflow `.github/workflows/visual-studio-extension-ci.yml` builds the extension on `windows-2022` with MSBuild from the installed Visual Studio toolchain, disables experimental-instance deployment with `/p:DeployExtension=false`, and uploads the built `.vsix` as a workflow artifact.

The CD workflow `.github/workflows/visual-studio-extension-cd.yml` packages and publishes the extension for `v*` tags and manual dispatch. It resolves the version from the tag or workflow input, updates `StemCode.VS/source.extension.vsixmanifest`, builds `StemCode.VS-<version>.vsix`, uploads that artifact, and publishes through `VsixPublisher.exe` to the Visual Studio Marketplace.

Required repository secret:

```text
VS_MARKETPLACE_PAT
```

Optional repository variables:

```text
VS_MARKETPLACE_PUBLISHER
VS_MARKETPLACE_EXTENSION_NAME
```

If the optional variables are unset, the workflow defaults to publisher `rizwan3d` and extension internal name `stemcode-vs`. The publish job uses the `visual-studio-marketplace` GitHub environment so approval or environment protection rules can be applied separately from the VS Code marketplace flow.

## JetBrains Extension

StemCode includes a JetBrains IntelliJ-platform plugin in `StemCode.JetBrains`. It opens a tool window inside IntelliJ-based IDEs and drives the local StemCode CLI over ACP, the same protocol used by the Visual Studio and VS Code integrations. The plugin is a Kotlin/Gradle project (`build.gradle.kts`) with an `AcpClient` that manages the local `stemcode --acp` process, a `ChatPanel` for the conversation, and a `SessionManager`.

Before first use, install the StemCode CLI so `stemcode` is available on `PATH` (or point the plugin at an explicit CLI path), and run `stemcode` once to complete provider onboarding.

## ACP Editor Integration

StemCode can run as an Agent Client Protocol server:

```bash
stemcode --acp
```

ACP mode speaks line-delimited JSON-RPC on stdin/stdout, so compatible editors and tools can create StemCode sessions, send prompts, cancel active turns, and receive assistant message, plan, and tool progress updates.

ACP does not open a network listener. It communicates only over the local child process stdin/stdout streams created by the host editor or tool.

Example editor server configuration:

```json
{
  "agent_servers": {
    "StemCode": {
      "command": "stemcode",
      "args": ["--acp"]
    }
  }
}
```

To require ACP authentication, set a process-level token with either `STEMCODE_ACP_AUTH_TOKEN` or a workspace profile:

```json
{
  "Application": {
    "Acp": {
      "authenticationToken": "replace-with-a-long-random-token"
    }
  }
}
```

When an ACP authentication token is configured, `initialize` returns `"authMethods": ["token"]`. The client must then call `authenticate` with `{"token":"..."}` before sending `session/new`, `session/load`, `session/prompt`, or `session/close`. If no token is configured, `authMethods` is empty and `authenticate` is rejected instead of returning a misleading success response.

Run `stemcode` once before ACP use so provider onboarding, credentials, and the default model are already configured. ACP mode currently supports one active StemCode session per process. It merges ACP client `mcpServers` with StemCode's user and workspace MCP configuration for that ACP session only, so editor-provided MCP tools do not become global configuration.

## Review Automation

StemCode includes copy-paste CI examples for GitHub, GitLab, and Bitbucket. Each example installs stemcode from the latest release using the same curl installer command shown in the CLI install section, computes the pull request or merge request diff, runs the workspace `pr-reviewer` profile in read-only mode, stores review artifacts, and posts a top-level review comment when platform credentials are configured.

- Always copy `.stemcode/agents/pr-reviewer.md` with the CI files so the review profile is available.
- GitHub: copy `.github/workflows/stemcode-review.yml` and `.github/stemcode-github-review.sh`.
- GitLab: copy `.gitlab-ci.yml` and `.gitlab/stemcode-gitlab-review.sh`.
- Bitbucket: copy `bitbucket-pipelines.yml` and `.bitbucket/stemcode-bitbucket-review.sh`.
- GitHub and GitLab draft pull requests are skipped.
- Review artifacts are uploaded from `artifacts/stemcode-review` and retained for 14 days.

Required repository secret:

```text
STEMCODE_API_KEY
```

Platform posting credentials:

| Platform | Variable |
| --- | --- |
| GitHub Actions | Uses the built-in `GITHUB_TOKEN` through `GH_TOKEN`. |
| GitLab CI | `GITLAB_TOKEN` or `stemcode_GITLAB_TOKEN` with permission to create merge request notes. |
| Bitbucket Pipelines | `BITBUCKET_ACCESS_TOKEN`, or `BITBUCKET_USERNAME` plus `BITBUCKET_APP_PASSWORD`. |

Optional repository variables:

| Variable | Default | Purpose |
| --- | --- | --- |
| `STEMCODE_PROVIDER` | `openai` | `openai`, `openai-compatible`, `google-ai-studio`, `anthropic`, `anthropic-claude-account`, `github-copilot`, `openrouter`, `kilo-code`, `cerebras`, `groq`, `ollama`, or `ollama-cloud`. |
| `STEMCODE_MODEL` | `gpt-5.4` | Preferred model id for the review run. |
| `STEMCODE_BASE_URL` | empty | Required only when `STEMCODE_PROVIDER` is `openai-compatible`. |
| `STEMCODE_THINKING` | `off` | `on` or `off`. |
| `STEMCODE_REASONING` | empty | Reasoning effort: `none`, `minimal`, `low`, `medium`, `high`, `xhigh`, or `max`. |
| `STEMCODE_PROJECT_NAME` | empty | Optional override for the `X-Project` header StemCode sends on provider requests. |

The GitHub workflow uses `pull_request_target` so it can comment with the repository token. It checks out the trusted base branch version of StemCode, fetches the PR head only to compute a diff, and runs the CLI from trusted code. GitLab and Bitbucket examples run in their native merge request or pull request pipeline contexts and post comments through their REST APIs.

## Codebase Indexing

StemCode includes a local codebase index for repository-wide discovery. The `codebase_index` tool can:

- `status`: show whether the index exists, when it was built, and whether files are new, changed, or deleted.
- `build`: refresh the index, reusing unchanged files and updating changed files incrementally.
- `search`: rank likely relevant files for a natural-language, symbol, path, or behavior query.
- `list`: show indexed file paths.

StemCode implements codebase indexing locally by running the TinyE5 INT8 ONNX model and building a richer repository map for each indexed file. The embedding profile follows [`GrowBitLabs/tinye5`](https://huggingface.co/GrowBitLabs/tinye5/tree/int8-onnx), specifically `onnx/model_int8.onnx`: 384-dimensional normalized vectors with E5-style `query:` and `passage:` prefixes, stored as signed 8-bit values in the local cache. Alongside path and language metadata, the index stores:

- semantic symbols with kind, container, signature, and line ranges,
- dependency edges such as imports, usings, project references, and relative module links,
- call edges between best-effort resolved workspace functions or methods,
- ownership data from the first available `CODEOWNERS` file (`.github/CODEOWNERS`, `CODEOWNERS`, or `docs/CODEOWNERS`),
- and stale-index warnings when the workspace has drifted since the last build.

The index still refreshes incrementally when searched or rebuilt and still respects ignore files such as `.gitignore`.

```text
.stemcode/cache/codebase-index.zvec
```

The cache does not store full file contents. It stores a single-user local Zvec database with per-file metadata such as path, length, language, line count, legacy symbol strings, semantic symbol entries, dependency links, call edges, ownership matches, TinyE5 model metadata, and the local int8 embedding vector used for ranking. Search snippets are read from current workspace files when results are returned.

On first use, StemCode downloads `onnx/model_int8.onnx` and `tokenizer.json` from the `int8-onnx` branch into `.stemcode/cache/embedding-models/tinye5-int8-onnx/`. The older built-in hash embedding path is not used for new codebase indexes.

Indexing respects `.gitignore`, `.stemcode/.stemcodeignore`, and built-in exclusions for generated or local runtime directories such as `.git/`, `node_modules/`, `bin/`, `obj/`, `.stemcode/cache/`, `.stemcode/logs/`, and `.stemcode/sessions/`.

Use `codebase_index` for broad discovery first, especially when you want to trace ownership, dependencies, callers, or likely symbol definitions across a repo. Then use `file_read`, `text_search`, or `code_intelligence` to verify exact behavior before editing.

`file_read` now returns two content fields in its JSON result:

- `RawContent`: the exact numbered lines returned by the read operation.
- `DisplayContent`: the same numbered lines with unsafe control and bidi characters rendered visibly for safer display.

The older `Content` JSON property is no longer emitted. Consumers that render previews should prefer `DisplayContent` and use `RawContent` when exact text is required.

### Manual Index Updates

Use the `/index` REPL command to refresh the local codebase index from a terminal or desktop session:

```text
/index
/index update
/index status
/index rebuild
/index list
/index list 50
```

### Automatic Index Updates

StemCode can refresh the local codebase index automatically after each conversation turn completes, so the next prompt sees an up-to-date index. This runs after the assistant response and all tool calls finish, reuses unchanged files, and updates changed files incrementally. A failed refresh is logged and never fails the completed turn.

This is **disabled by default**. Enable it in user-level or workspace-level `.stemcode/agent-profile.json`:

```json
{
  "codebaseIndex": {
    "autoUpdateAfterTask": true
  }
}
```

## Providers and Models

StemCode stores a provider profile locally and discovers models from that provider when possible.

Use the terminal F2 or `/models` picker, `/use <model>`, or the desktop model control to switch models. The active model is stored with the local configuration and section state. If a preferred model is unavailable, StemCode falls back to a discovered model when possible.

### DeepSeek Tool-Argument Repair

When the active provider is DeepSeek (or the active model id contains `deepseek`), StemCode runs a provider-specific repair pass over tool-call arguments before executing the tool. DeepSeek models sometimes emit arguments that do not match the tool schema — for example wrapping a plain path or value in Markdown auto-link syntax such as `[text](path)`. The repair layer normalizes these against the tool's JSON schema so the call still runs correctly. The pass is a no-op for other providers and only rewrites arguments when a repair is actually needed.

### Thinking Mode

StemCode supports simple thinking mode:

```text
/thinking on
/thinking off
```

### Reasoning Effort vs Thinking Mode

`thinking` controls whether StemCode enables provider reasoning behavior where supported and whether provider-approved reasoning summaries are shown in the UI.

`reasoningEffort` controls how much reasoning work StemCode asks the model to spend.

Examples:

- `/thinking on` shows supported reasoning output.
- `/thinking off` hides reasoning output.
- `/reasoning high` asks the provider for deeper reasoning.
- `/reasoning none` disables reasoning where the provider supports disabling it.

Supported normalized reasoning effort values:

```text
none
minimal
low
medium
high
xhigh
max
```

If thinking is on and no explicit reasoning effort is set, StemCode asks supported providers for their default reasoning depth, which is usually mapped to `medium`.

### Reasoning Controls

Use these commands from the terminal:

```text
/thinking on
/thinking off
/reasoning
/reasoning show
/reasoning low
/reasoning high
/reasoning none
```

The desktop app keeps the existing thinking toggle and also exposes a reasoning effort picker. Providers that do not honor explicit effort settings may continue with provider defaults.

### Provider Reasoning Mapping

| Provider | Request shape | Notes |
| --- | --- | --- |
| OpenCode Zen | `reasoning.effort` for Responses, `reasoningEffort` in OpenCode config | CamelCase in OpenCode config files; StemCode uses Responses-style payloads for Zen Responses models. |
| OpenAI | `reasoning_effort` for chat-completions style, `reasoning: { effort, summary }` for Responses-style | Raw reasoning stays hidden; StemCode only shows provider-approved summaries. |
| Anthropic Claude | `thinking` plus `output_config.effort`, or manual `budget_tokens` fallback | Adaptive thinking for Claude 4 families, manual budget fallback for older Claude models. |
| DeepSeek | `reasoning_content` in responses; optional `reasoning_effort` where supported | StemCode never replays prior `reasoning_content` back to DeepSeek. |
| Gemini | `thinkingConfig.thinkingLevel` or `thinkingConfig.thinkingBudget` | Gemini 3-style and Gemini 2.5-style models differ. |
| xAI Grok and other OpenAI-compatible providers | `reasoning_effort` or Responses-compatible `reasoning.effort` when supported | Capability depends on the upstream provider and model. |
| OpenRouter | Unified `reasoning` object | Supports effort mapping and safe replay of provider-approved reasoning metadata. |

StemCode keeps final answers separate from reasoning output. When thinking output is disabled, StemCode still shows the final answer and suppresses reasoning blocks.

## Profiles and Subagents

Profiles shape how StemCode behaves.

| Profile | Mode | Edit behavior | Best for |
| --- | --- | --- | --- |
| `build` | Primary | Allows edits under permissions | Implementation, fixes, tests, build loops. |
| `plan` | Primary | Read-only | Investigation and implementation plans. |
| `review` | Primary | Read-only | Findings-first code review. |
| `general` | Subagent | Allows edits under permissions | Bounded delegated implementation work. |
| `explore` | Subagent | Read-only | Fast codebase discovery. |

Switch profiles:

```text
/profile build
/profile plan
/profile review
```

Invoke a subagent for one turn:

```text
@explore How does authentication work?
@general Update the parser tests for this narrow case.
```

Primary agents can also use `agent_delegate` for one focused handoff or `agent_orchestrate` for several coordinated subtasks. Orchestration is useful when multiple read-only investigations can run independently or when implementation tasks can be split into clear file scopes.
Primary profiles (`build`, `plan`, and `review`) can use the `ask_question` tool to ask you a question and wait for your answer before continuing. It supports multiple-choice options, multi-select, and free-form text, and it works in the interactive terminal, one-shot CLI runs, and ACP editors that support permission or text prompts. In `plan` mode the agent uses it to clarify genuinely ambiguous requirements or choose between approaches before finalizing a plan. In a non-interactive run with no available user, the tool returns gracefully so the agent continues with its best judgment instead of failing the turn.

### Built-in Profile Prompt Overrides

Create one of these files to replace only that built-in profile's prompt for a workspace:

```text
.stemcode/agents/build.md
.stemcode/agents/plan.md
.stemcode/agents/review.md
.stemcode/agents/general.md
.stemcode/agents/explore.md
```

StemCode reads the markdown body as the active profile prompt, redacts secret-looking values, and reloads it for conversation turns like `.stemcode/SystemPrompt.md`. For built-in profile names, StemCode keeps the built-in mode, enabled tools, and permission behavior, so a custom `plan.md` prompt still stays read-only.

## Permissions and Sandboxing

StemCode evaluates every sensitive action through permission policy.

### Permission Modes

| Mode | Meaning |
| --- | --- |
| `Allow` | The action can proceed. |
| `Ask` | StemCode prompts for approval. |
| `Deny` | The action is blocked. |

### Sandbox Modes

| Mode | Meaning |
| --- | --- |
| `ReadOnly` | No file writes or unsafe shell mutation. |
| `WorkspaceWrite` | Workspace-scoped writes are allowed under policy. |
| `DangerFullAccess` | Unrestricted execution when explicitly configured or approved. |

Shell sandboxing depends on the operating system. Linux uses `bubblewrap` when available. macOS uses `sandbox-exec`. Platforms without a supported OS sandbox runner fail closed for restricted shell modes unless the user approves escalation or configures full access.

### Session Overrides

Use overrides for temporary decisions:

```text
/allow bash "<command-pattern>"
/deny bash "<command-pattern>"
```

Overrides are session-scoped. For durable policy, edit configuration.

### Example Permission Policy

```json
{
  "Application": {
    "Permissions": {
      "auto_approve_all_tools": false,
      "file_read": "Allow",
      "file_write": "Ask",
      "file_delete": "Ask",
      "shell_default": "Ask",
      "shell_safe": "Allow",
      "network": "Ask",
      "memory_write": "Ask",
      "mcp_tools": "Ask",
      "shell": {
        "allow": {
          "commands": [
            "your-build-command",
            "your-test-command"
          ]
        },
        "deny": {
          "commands": [
            "dangerous-command-pattern",
            "network-installer-pattern"
          ]
        }
      }
    }
  }
}
```

`shell_safe` controls the mode applied to the command patterns you list under `shell.allow.commands`; StemCode does not ship a built-in shell command allow catalog.

The `network` shortcut applies to built-in `webfetch` tools, including `web_search` and `headless_browser`. `headless_browser` renders pages through an installed Chromium-family browser such as Microsoft Edge, Google Chrome, or Chromium.

### Auto-Approve All Tools

For trusted workspaces, you can disable approval prompts for all tools:

```json
{
  "Application": {
    "Permissions": {
      "auto_approve_all_tools": true
    }
  }
}
```

This keeps workspace path checks, profile restrictions, sandbox-mode restrictions, and built-in deny rules active. Use explicit `rules` or shortcut settings when you need to override a specific deny policy.

Memory writes still require approval by default through the memory policy, even in workspaces that auto-approve general tools.

## Git Automation

StemCode can automatically create a git commit for AI-made workspace edits.

- Auto-commit is enabled by default for a workspace unless `.stemcode/agent-profile.json` turns it off.
- Commits are attempted at session shutdown after new tracked edits have been recorded.
- StemCode skips auto-commit if the workspace is not a git repository or if you already have staged changes.
- Staging is scoped to the files StemCode changed, including both sides of renames when needed.
- StemCode uses a temporary git index and verifies the repo state and tracked file contents before committing, so concurrent changes do not get swept into the commit accidentally.
- If commit-message generation cannot use the current provider credentials, StemCode falls back to `chore: apply StemCode changes`.

Toggle the feature interactively:

```text
/autocommit status
/autocommit off
/autocommit on
```

Workspace config:

```json
{
  "Application": {
    "Git": {
      "AutoCommitAfterAiChanges": true
    }
  }
}
```

## Workspace Files

Run:

```text
/init
```

StemCode asks which starter files to add:

- `Recommended`: core config, ignores, repo memory templates, runtime folders, and inactive agent/skill templates.
- `Minimal`: core config, README, and ignore files only.
- `Custom`: asks for each optional group, including the advanced inactive `SystemPrompt.md.template`.

You can skip the picker with `/init recommended`, `/init minimal`, or `/init custom`.

The recommended preset creates:

```text
.stemcode/
  agent-profile.json
  README.md
  .gitignore
  .stemcodeignore
  agents/
  skills/
  cache/
  memory/
    architecture.md
    conventions.md
    decisions.md
    known-issues.md
    test-strategy.md
    lessons.jsonl
  logs/
```

### `AGENTS.md`

Place `AGENTS.md` or `.agent/AGENTS.md` in the workspace for persistent project instructions. StemCode adds them to the model context after secret redaction.

### `.stemcode/SystemPrompt-Append.md`

Create `.stemcode/SystemPrompt-Append.md` when you want to append workspace-specific base rules to StemCode's configured default system prompt. This keeps the normal base behavior intact and adds your extra instructions before the active profile prompt, workspace instructions, skills, memory, and session state.

Use `AGENTS.md` for ordinary repository instructions. Use `SystemPrompt-Append.md` when you only need to layer a few durable workspace rules onto the default base behavior.

If both `SystemPrompt.md` and `SystemPrompt-Append.md` exist, `SystemPrompt.md` wins and the append file is ignored.

### `.stemcode/SystemPrompt.md`

Create `.stemcode/SystemPrompt.md` to replace StemCode's base system prompt for that workspace. StemCode always prepends its identity header before the custom file content, then appends the active profile prompt, workspace instructions, skills, memory, and session state as usual.

Use `AGENTS.md` for ordinary repository instructions. Use `SystemPrompt.md` only when the workspace needs a different base behavior than both the default prompt and the append-only option.

`/init custom` can create `.stemcode/SystemPrompt.md.template` as an inactive starter. Edit and rename it to `SystemPrompt.md` only when you intentionally want the override.

Use `.stemcode/agents/<profile>.md` when you want to replace the active profile prompt while keeping the same base system prompt. Built-in profile names are `build`, `plan`, `review`, `general`, and `explore`.

### `.stemcode/.stemcodeignore`

Use `.stemcodeignore` to exclude paths from StemCode file tools. It supports gitignore-style patterns including comments, negation, directory rules, `*`, `?`, `**`, and character classes.

Common exclusions:

```text
.env
.env.*
secrets.*
[Bb]in/
[Oo]bj/
node_modules/
.git/
.stemcode/cache/
.stemcode/logs/
.stemcode/memory/*.jsonl
```

## Team Memory

StemCode stores structured team memory as ordinary markdown files:

```text
.stemcode/memory/
  architecture.md
  conventions.md
  decisions.md
  known-issues.md
  test-strategy.md
```

These files are repo-scoped memory that your team can inspect, diff, and version-control. That is much safer than hidden memory because every durable note can go through normal code review and repository history.

StemCode loads non-empty team memory files into the model context as durable project context, skipping untouched scaffold templates. Treat them as starting context, then verify against current files and fresh tool output when correctness matters.

Use the `repo_memory` tool to list, read, or update these documents. Writes require memory approval by default and are blocked in read-only profiles, planning phase, and read-only sandbox mode. Direct writes to `.stemcode/memory/*` through file editing tools also receive the `memory_write` permission tag so they cannot silently bypass memory approval.

## Skills and Custom Agents

### Workspace Skills

Skills are task-specific playbooks loaded only when relevant.

Supported layouts:

```text
.stemcode/skills/dotnet/SKILL.md
.stemcode/skills/code-review.md
```

Example:

```markdown
---
name: dotnet
description: Use for .NET build, test, package, and project-file work.
---
Prefer repo-native `dotnet build` and `dotnet test` commands.
Inspect the relevant `.csproj` before changing package references.
Keep package and target framework changes narrowly scoped.
```

### Custom Agents

Custom agents live in:

```text
.stemcode/agents/*.md
```

Example:

```markdown
---
name: code-reviewer
mode: subagent
description: Read-only reviewer for bugs, regressions, edge cases, and missing tests.
editMode: readOnly
shellMode: safeInspectionOnly
toolOutput: full
tools:
  - code_intelligence
  - directory_list
  - file_read
  - search_files
  - shell_command
  - text_search
---
Review the requested code or change set with a findings-first posture.
```

The optional `toolOutput` key sets the default rendering for tool results while the profile is active: `full`/`complete` prints the complete output and `compact`/`preview` prints the capped preview. Omit it to fall back to the `Application.Tools.toolOutput` default in `agent-profile.json` (or the compact default if that is also unset). `/tooloutput` overrides this for the current session, and `/tooloutput auto` reverts to the profile or configured default. (The legacy `fileOutput` key is still accepted as an alias.)

If front matter is omitted, StemCode derives the name from the file name and uses conservative defaults.

If a workspace agent file uses a built-in profile name such as `build` or `review`, StemCode treats it as a prompt override for that built-in profile rather than adding a duplicate profile. The markdown body is customizable, but the built-in profile's mode, tool set, and permission behavior are preserved.

## MCP Servers

StemCode can load MCP servers from user-level and workspace-level `agent-profile.json` files. ACP clients can also supply session-scoped `mcpServers`; those entries are merged after user and workspace config and are visible in `/mcp` only for that ACP session.

Example:

```json
{
  "mcpServers": {
    "context7": {
      "command": "npx",
      "args": ["-y", "@upstash/context7-mcp"],
      "startupTimeoutSeconds": 20,
      "toolTimeoutSeconds": 45,
      "defaultToolsApprovalMode": "prompt",
      "env": {
        "MY_ENV_VAR": "MY_ENV_VALUE"
      }
    }
  }
}
```

Supported transports:

- Stdio: `command`, `args`, `env`, `envVars`, `cwd`.
- Streamable HTTP: `url`, `bearerTokenEnvVar`, `httpHeaders`, `envHttpHeaders`.

Use `enabledTools` and `disabledTools` to filter exposed tools. Use `/mcp` to inspect loaded MCP servers, custom tool providers, and dynamic tools.

## Code Intelligence

StemCode supports Language Server Protocol (LSP) integrations through the `code_intelligence` tool and the `/lsp` CLI command family.

`code_intelligence` now discovers language servers from built-in definitions, the current workspace, and optional user or workspace profile overrides.

- Run `code_intelligence` with `action: "servers_status"` to inspect supported languages, detected servers, missing servers, cached health, and install hints.
- In the interactive CLI, use `/lsp` for the same registry view. Use `/lsp refresh` to bypass cached detection, or `/lsp file <path>` to inspect candidates for one file.
- Built-in detection checks workspace-local bins such as `node_modules/.bin` and common Python virtualenv script folders before falling back to `PATH`.
- Server selection is deterministic: higher `priority` wins, then StemCode falls back through remaining detected servers in stable key order.
- Rename stays preview-only. Code-intelligence actions remain read-only.

Example status request:

```json
{
  "action": "servers_status",
  "refresh": true
}
```

Profile overrides live in user-level or workspace-level `.stemcode/agent-profile.json` under `languageServers`.

Example override:

```json
{
  "languageServers": {
    "python-pyright": {
      "language": "Python",
      "name": "Pyright",
      "command": ".stemcode/tools/pyright-langserver.cmd",
      "args": ["--stdio"],
      "languageId": "python",
      "fileExtensions": [".py"],
      "priority": 250
    }
  }
}
```

Supported `languageServers` fields:

- `command`
- `args`
- `enabled`
- `fileExtensions`
- `initializationOptions`
- `installHint`
- `language`
- `languageId`
- `name`
- `priority`

Setup examples:

- TypeScript/JavaScript:
  Install `vtsls` or `typescript-language-server`.
  Example: `npm install -g @vtsls/language-server typescript`
- Python:
  Install `basedpyright-langserver`, `pyright-langserver`, or `pylsp`.
  Examples: `pip install basedpyright` or `pip install python-lsp-server`
- C#:
  Install `csharp-ls`.
  Example: `dotnet tool install --global csharp-ls`
- Rust:
  Install `rust-analyzer`.
  Example: `rustup component add rust-analyzer`
- Go:
  Install `gopls`.
  Example: `go install golang.org/x/tools/gopls@latest`
- C/C++:
  Install `clangd`.
  Example: use your platform package manager or LLVM distribution so `clangd` is on `PATH`

## Custom Tools

StemCode can expose user-defined process tools from `agent-profile.json`. A custom tool can be written in any language that can read JSON from stdin and write text or JSON to stdout. Configured tools are exposed to the model as `custom__<name>`.
`mcpServers` and `customTools` can be configured in the same profile; StemCode loads both sets together and exposes MCP tools as `mcp__*` plus custom tools as `custom__*`.

Example:

```json
{
  "customTools": {
    "word_count": {
      "description": "Count words in provided text.",
      "command": "python",
      "args": [".stemcode/tools/word_count.py"],
      "cwd": ".",
      "approvalMode": "prompt",
      "timeoutSeconds": 15,
      "schema": {
        "type": "object",
        "properties": {
          "text": {
            "type": "string",
            "description": "Text to count."
          }
        },
        "required": ["text"],
        "additionalProperties": false
      }
    }
  }
}
```

StemCode sends this JSON to the process on stdin:

```json
{
  "toolName": "custom__word_count",
  "configuredName": "word_count",
  "arguments": {
    "text": "hello world"
  },
  "session": {
    "id": "session-id",
    "workspacePath": "/path/to/workspace",
    "workingDirectory": "."
  }
}
```

The process can print plain stdout, which is treated as a successful text result, or a structured response:

```json
{
  "status": "success",
  "message": "Counted words.",
  "data": {
    "words": 2
  },
  "renderText": "2 words"
}
```

Use `status: "error"` for execution errors or `status: "invalid_arguments"` for argument validation failures. Relative `cwd` and relative command paths are resolved against the workspace root. Custom tools default to approval prompts; use permission rules or `approvalMode: "auto"` only for tools you trust.

## Memory, Audit, and Hooks

### Team Memory Files

Team memory is stored in reviewable markdown files under `.stemcode/memory/`:

- `architecture.md`: major components, boundaries, data flow, and integration points.
- `conventions.md`: coding, naming, formatting, review, and workflow conventions.
- `decisions.md`: durable technical decisions, context, and consequences.
- `known-issues.md`: known bugs, limitations, risky areas, and workarounds.
- `test-strategy.md`: expected test layers, important commands, and validation guidance.

These files are intended to be committed with the repository when the team wants shared agent context. Memory writes require approval by default.

### Lesson Memory

StemCode stores reusable workspace lessons in:

```text
.stemcode/memory/lessons.jsonl
```

Lessons help StemCode avoid repeating local mistakes. When lesson memory is enabled for a workspace, StemCode can inject relevant lessons into prompts automatically, and automatic tool-failure observation can turn repeated failures and their later fixes into reusable lessons. Memory is local, redacted by default, and write operations require approval unless policy is changed.

### Tool Audit

Tool audit logging is disabled by default. When enabled, StemCode writes completed tool-call records to:

```text
.stemcode/logs/tool-audit.jsonl
```

### Workspace Policy

Configure memory and audit behavior in `.stemcode/agent-profile.json`:

```json
{
  "memory": {
    "requireApprovalForWrites": true,
    "allowAutoFailureObservation": true,
    "allowAutoManualLessons": false,
    "redactSecrets": true,
    "maxEntries": 500,
    "maxPromptChars": 12000,
    "disabled": false
  },
  "toolAudit": {
    "enabled": false,
    "redactSecrets": true,
    "maxArgumentsChars": 12000,
    "maxResultChars": 12000
  }
}
```

### Lifecycle Hooks

Hooks run local automation around StemCode actions. A hook receives JSON on standard input and selected `STEMCODE_*` environment variables.

Example:

```json
{
  "Application": {
    "Hooks": {
      "enabled": true,
      "defaultTimeoutSeconds": 30,
      "maxOutputCharacters": 12000,
      "rules": [
        {
          "name": "check-write",
          "events": ["before_file_write", "after_file_write"],
          "command": "scripts/check-write.ps1",
          "pathPatterns": ["src/**", "StemCode/**"]
        },
        {
          "name": "shell-failure",
          "event": "after_shell_failure",
          "command": "scripts/on-shell-failure.ps1",
          "shellCommandPatterns": ["dotnet test*", "npm test*"]
        }
      ]
    }
  }
}
```

Supported hook events include task, tool, file, shell, web, memory, permission, and delegation lifecycle events.

## Privacy and Local Data

Local:

- Workspace files stay on your machine.
- Configuration is local.
- Sections are stored locally.
- Codebase index cache is stored locally.
- Team memory and lesson memory are stored locally.
- Optional audit logs are stored locally.
- Secrets are stored through platform credential storage where supported.

Sent to the configured provider when needed:

- User prompts.
- System and workspace instructions.
- Relevant file excerpts.
- Tool outputs.
- Conversation context.
- Model and tool schemas.

StemCode redacts common secret patterns before storing or displaying tool output, memory, audit records, logs, conversation history, session state, workspace instructions, and errors. Redaction is pattern-based and should not be treated as a full data-loss-prevention system.

## Troubleshooting

### `stemcode` is not found

Restart the terminal after installation. If it still fails, verify that the install directory is on `PATH`.

### Provider setup is incomplete

Run `stemcode` and choose to reconfigure. This can happen when setup was cancelled after provider config was saved but before the secret was stored.

### Provider validation fails after onboarding

Choose to re-run onboarding when StemCode offers it. If the same provider still fails, check the credential, account access, selected provider base URL, and network connectivity.

### Updating StemCode

Run `/update` to check for a newer release. Run `/update now` to install the latest release immediately, then restart StemCode.

### ChatGPT Plus/Pro sign-in does not complete

Check that port `1455` is available and that the browser callback URL opens locally. Sign-in requires network access and a valid account with access to the selected model.

### Claude Pro/Max sign-in does not complete

Check that port `53692` is available and that the browser callback URL opens locally. Sign-in requires network access and a valid Claude Pro or Max account.

### GitHub Copilot sign-in does not complete

Check that the device-code page opened, enter the displayed code, and verify that your GitHub account has Copilot access. For GitHub Enterprise, enter only the Enterprise URL or domain when prompted; leave it blank for `github.com`.

### No models are listed

Check the provider credential, provider account access, network connectivity, and custom provider base URL. For compatible providers, the base URL must be absolute and use HTTP or HTTPS.

For Ollama, make sure `ollama serve` is running and at least one model is installed. For LM Studio, make sure the local server is started, at least one model is loaded, and the API key matches your LM Studio server settings. For Ollama Cloud, check that the API key has access to the hosted models you expect to use.

### A command is denied

Run `/permissions` and `/rules` to see active policy. You can approve the prompt, add a session override with `/allow`, or update configuration.

### Shell sandboxing fails on Windows

Foreground shell commands and background terminals in `read-only` and `workspace-write` modes use the Windows sandbox runner. If a restricted command still fails, inspect `%APPDATA%\StemCode\.sandbox\sandbox.log`, rerun the Windows sandbox setup if prompted, and verify the working directory still exists.

Restricted pseudo-terminal sessions are not supported by the Windows sandbox runner. Those requests fail closed; rerun without `pty`, use a non-PTY foreground command or background terminal, or approve sandbox escalation only when you trust the command.

### The agent cannot read a file

Check that the path is inside the workspace and not excluded by `.stemcode/.stemcodeignore` or default secret-protection rules.

### Undo did not revert a shell side effect

Undo/redo only covers tracked file edit transactions. It does not revert arbitrary shell command side effects, package installs, generated files, external tools, or network actions.

## Build From Source

Requirements:

- .NET SDK compatible with `net10.0`.
- Node.js 20 or newer for the VS Code extension.
- Visual Studio 2022 or newer on Windows for `StemCode.VS`.
- Platform toolchains needed by your target desktop/CLI build.

Commands:

```bash
dotnet restore StemCode.CrossPlatform.slnx
dotnet build StemCode.CrossPlatform.slnx
dotnet test StemCode.Tests/StemCode.Tests.csproj
dotnet pack StemCode/StemCode.csproj -c Release
```

VS Code extension commands:

```bash
cd StemCode.VsCode
npm ci
npm run lint
npm run package
npm run package:vsix
```

Visual Studio extension command:

```powershell
msbuild StemCode.VS/StemCode.VS.csproj /restore /p:Configuration=Release /p:DeployExtension=false
```

The main projects are:

| Project | Purpose |
| --- | --- |
| `StemCode` | Core application, domain, infrastructure, tools, providers, storage. |
| `StemCode.CLI` | Terminal UI and one-shot CLI. |
| `StemCode.Desktop` | Desktop app. |
| `StemCode.VS` | Visual Studio extension that hosts StemCode inside a Visual Studio tool window. |
| `StemCode.VsCode` | VS Code extension that drives StemCode through ACP mode. |
| `StemCode.Voice` | Local Whisper-powered voice dictation runtime (`stemcode-voice`) used for speech-to-text prompts. |
| `StemCode.JetBrains` | JetBrains IntelliJ-platform plugin that hosts StemCode inside the IDE through ACP. |
| `benchmarks` | Benchmark fixtures, tasks, and result scripts (not a buildable project). |
| `StemCode.Tests` | Test suite. |

Two solution files exist: `StemCode.CrossPlatform.slnx` (core, CLI, desktop, and tests) is used by the cross-platform build commands above, while `StemCode.slnx` additionally includes `StemCode.Voice` and the Windows-only `StemCode.VS` extension. To build or pack the voice runtime or the Visual Studio extension, use `StemCode.slnx`.

## License

StemCode is licensed under the Apache License 2.0. See [../LICENSE](../LICENSE).

