<p align="center">
  <img src=".github/stemcode.jpg" alt="StemCode" width="800">
</p>

<h1 align="center">StemCode</h1>

<p align="center">
  Local AI coding agent for desktop, terminal, editor, and CI workflows.
</p>

<p align="center">
  StemCode helps you understand a repository, plan a change, edit files, run validation, review diffs, and automate pull request feedback without giving up local control.
</p>

<p align="center">
  <a href="https://github.com/rizwan3d/StemCode/actions/workflows/ci.yml"><img src="https://img.shields.io/github/actions/workflow/status/rizwan3d/StemCode/ci.yml?branch=master&amp;label=build" alt="Build"></a>
  <a href="https://github.com/rizwan3d/StemCode/actions/workflows/ci.yml"><img src="https://img.shields.io/github/actions/workflow/status/rizwan3d/StemCode/ci.yml?branch=master&amp;label=tests" alt="Tests"></a>
  <a href="https://github.com/rizwan3d/StemCode/actions/workflows/release.yml"><img src="https://img.shields.io/github/actions/workflow/status/rizwan3d/StemCode/release.yml?label=release" alt="Release"></a>
  <a href="https://github.com/rizwan3d/StemCode/blob/master/LICENSE.txt"><img src="https://img.shields.io/github/license/rizwan3d/StemCode" alt="License"></a>
  <a href="https://github.com/rizwan3d/StemCode"><img src="https://img.shields.io/github/v/release/rizwan3d/StemCode" alt="Version"></a>
  <a href="https://github.com/rizwan3d/StemCode/stargazers"><img src="https://img.shields.io/github/stars/rizwan3d/StemCode" alt="Stars"></a>
  <a href="https://github.com/rizwan3d/StemCode/issues"><img src="https://img.shields.io/github/issues/rizwan3d/StemCode" alt="Issues"></a>
  <a href="https://github.com/rizwan3d/StemCode/releases"><img src="https://img.shields.io/github/downloads/rizwan3d/StemCode/total?label=downloads" alt="Downloads"></a>
  <a href="https://github.com/rizwan3d/StemCode/forks"><img src="https://img.shields.io/github/forks/rizwan3d/StemCode" alt="Forks"></a>
</p>

<p align="center">
  <a href="https://github.com/rizwan3d/StemCode/releases/latest">
    <img src="https://img.shields.io/badge/Get-Releases-0969da?style=for-the-badge" alt="Get StemCode releases">
  </a>
  <a href="#cli-install">
    <img src="https://img.shields.io/badge/Install-CLI-0969da?style=for-the-badge" alt="Install StemCode CLI">
  </a>
   <a href="#desktop-app">
    <img src="https://img.shields.io/badge/Install-Desktop-0969da?style=for-the-badge" alt="Install StemCode Desktop">
  </a>
  <a href="https://marketplace.visualstudio.com/items?itemName=growbitlab.stemcode-vscode">
    <img src="https://img.shields.io/badge/Install-VS_Code-0969da?style=for-the-badge" alt="Install StemCode VS Code extension">
  </a>
  <a href="https://marketplace.visualstudio.com/items?itemName=growbitlab.stemcode-vs">
    <img src="https://img.shields.io/badge/Install-Visual_Studio-0969da?style=for-the-badge" alt="Install StemCode Visual Studio extension">
  </a>
  <a href="https://www.npmjs.com/package/stemcode">
    <img src="https://img.shields.io/badge/Install-npm-0969da?style=for-the-badge" alt="Install StemCode from npm">
  </a>
  <a href="https://www.nuget.org/packages/StemCode/">
    <img src="https://img.shields.io/badge/Install-NuGet-0969da?style=for-the-badge" alt="Install StemCode NuGet">
  </a>
  <a href="docs/documentation.md">
    <img src="https://img.shields.io/badge/Read-Docs-0969da?style=for-the-badge" alt="Read StemCode documentation">
  </a>
</p>

---
StemCode is built for practical engineering work. It runs against a real local repository, uses real shells and tools, keeps workspace memory in versionable files, and asks for approval when an action should stay under human control.

Use it when you want one agent experience across:

- interactive implementation in a terminal
- desktop chat with activity, controls, and undo/redo
- VS Code and Visual Studio editor workflows
- ACP-compatible editor integrations
- CI review automation for pull requests and merge requests

## Table of Contents

- [Why StemCode](#why-stemcode)
- [What You Can Do](#what-you-can-do)
- [Choose Your Surface](#choose-your-surface)
- [Get Started](#get-started)
- [Quick Start](#quick-start)
- [Providers](#providers)
- [Built For Control](#built-for-control)
- [Documentation](#documentation)
- [Contributing](#contributing)
- [Support](#support)
- [License](#license)

## Why StemCode

- Works inside a real repository instead of a detached chat sandbox.
- Keeps the human in control with approval prompts, permissions, and profiles.
- Reuses the same agent across desktop, CLI, IDE, and CI workflows.
- Stores reusable commands and team memory in `.stemcode/` files you can review and commit.
- Supports both subscription-style sign-in and API-key or local-model setups.
- Local voice dictation
- Subagents for delegated tasks, with independent contexts that keep the main conversation focused
- Interactive user questions for clarification, multiple-choice, multi-select, and free-form input
- ! for local shell commands and !! for background terminal commands
- Tab-based path autocomplete for files and directories in shell commands

## What You Can Do

- Understand unfamiliar code with repository-aware search, file inspection, and focused summaries.
- Use built-in LSP-powered code intelligence for symbols, definitions, references, diagnostics, and rename previews.
- Turn feature requests and bug reports into concrete implementation plans.
- Edit files, run checks, and iterate on a change without leaving your working tree.
- Make surgical tracked edits with whole-file writes, patch application, line-based insertion, and single-file search/replace.
- Review local diffs, files, pull requests, and merge requests with a findings-first workflow.
- Switch between implementation, planning, review, exploration, and delegated work profiles.
- Save repeatable prompts as slash commands in `.stemcode/commands`.
- Keep long-lived project knowledge in `.stemcode/memory` instead of hidden agent state.

## Choose Your Surface

| Surface | Best for |
| --- | --- |
| Desktop app | Visual workspace with chat, model controls, profile switching, activity output, permission prompts, and undo/redo for tracked edits. |
| `stemcode` CLI | Keyboard-first work, one-shot prompts, piped input, quick reviews, and automation-friendly output. |
| VS Code extension | Chat, selected-context prompts, file review, diff review, and applying suggestions without leaving the editor. |
| Visual Studio extension | Docked StemCode tool window powered by the local CLI over ACP. |
| JetBrains plugin | IntelliJ-based IDE tool window powered by the local CLI over ACP. |
| CI automation | Running StemCode in GitHub Actions, GitLab CI, and Bitbucket Pipelines to review proposed changes automatically. |

## Get Started

Download the latest desktop build from [GitHub Releases](https://github.com/rizwan3d/StemCode/releases/latest), or install the CLI with the method that fits your environment.

Release assets publish `SHA256SUMS` and GitHub artifact attestations so you can verify both checksums and build provenance.

### Desktop App

| Platform | Architecture | Download |
| --- | --- | --- |
| Windows | x64 | [Setup `.exe`](https://github.com/rizwan3d/StemCode/releases/latest/download/StemCode.Desktop-win-x64-setup.exe) |
| Windows | x64 | [Portable `.zip`](https://github.com/rizwan3d/StemCode/releases/latest/download/StemCode.Desktop-win-x64.zip) |
| macOS | arm64 | [Download `.zip`](https://github.com/rizwan3d/StemCode/releases/latest/download/StemCode.Desktop-osx-arm64.zip) |
| macOS | x64 | [Download `.zip`](https://github.com/rizwan3d/StemCode/releases/latest/download/StemCode.Desktop-osx-x64.zip) |
| Linux | x64 | [Download `.zip`](https://github.com/rizwan3d/StemCode/releases/latest/download/StemCode.Desktop-linux-x64.zip) |
| Linux | arm64 | [Download `.zip`](https://github.com/rizwan3d/StemCode/releases/latest/download/StemCode.Desktop-linux-arm64.zip) |

### CLI Install

Every installer exposes the same `stemcode` command and downloads the same self-contained release binary.

#### Install script

Curl:

```bash
curl -fsSL https://raw.githubusercontent.com/rizwan3d/StemCode/master/scripts/install.sh | bash
```

Windows PowerShell:

```powershell
irm https://raw.githubusercontent.com/rizwan3d/StemCode/master/scripts/install.ps1 | iex
```

On Windows, both scripts install the same `stemcode` CLI. The Bash installer downloads the `win-x64` release, installs `stemcode.exe` into `%LOCALAPPDATA%\Programs\StemCode\bin` by default, and adds that directory to your user `PATH`.

#### npm / pnpm / bun
```bash
npm install -g stemcode
# or
pnpm add -g stemcode
# or
bun add -g stemcode
```

The npm package downloads the matching release binary and verifies it against published `SHA256SUMS`. If `postinstall` is skipped or the download fails, the binary is fetched automatically the first time you run `stemcode`.

Start StemCode:

```bash
stemcode
```

The release workflow also publishes the `StemCode` library to [NuGet.org](https://www.nuget.org/packages/StemCode/).

### Python SDK Wrapper

The `python/` folder contains a thin [`pythonnet`](https://pythonnet.github.io/)
wrapper around the .NET `StemCode.Sdk` API. It loads a published `StemCode.dll`
and exposes a Pythonic builder/client facade for automation scripts.

```powershell
cd python
dotnet publish ..\StemCode\StemCode.csproj -c Release -f net10.0 -o .\artifacts\stemcode-sdk
py -3.12 -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -e .

$env:STEMCODE_DOTNET_PATH = "$PWD\artifacts\stemcode-sdk"
$env:STEMCODE_PROVIDER = "openai"
$env:STEMCODE_API_KEY = "PASTE_NEW_ROTATED_KEY_HERE"
$env:STEMCODE_MODEL = "gpt-5"

python .\examples\basic_chat.py "Explain this repository in one paragraph."
```

See [`python/README.md`](python/README.md) for the full setup and usage example.

## Quick Start

On first launch, StemCode walks you through provider setup. Choose a subscription account, an API-key provider, an OpenAI-compatible endpoint, or a local provider, then let StemCode discover the models that are available to that setup.

If you already know the provider configuration you want, you can preseed it before first run and skip interactive onboarding.

PowerShell:

```powershell
$env:STEMCODE_PROVIDER="openrouter"
$env:STEMCODE_MODEL="poolside/laguna-m.1:free"
$env:STEMCODE_THINKING="on"
$env:STEMCODE_API_KEY="PASTE_NEW_ROTATED_KEY_HERE"

stemcode -p "Say hello in one short line"
```

Bash:

```bash
export STEMCODE_PROVIDER="openrouter"
export STEMCODE_MODEL="poolside/laguna-m.1:free"
export STEMCODE_THINKING="on"
export STEMCODE_API_KEY="PASTE_NEW_ROTATED_KEY_HERE"

stemcode -p "Say hello in one short line"
```

Common ways to use StemCode:

```bash
# Start an interactive session in the current repository
stemcode

# Ask one question and print the result
stemcode "Find risky changes in this branch"

# Review piped input with the review profile
git diff --stat | stemcode --stdin --profile review

# Resume a previous session
stemcode --session <session-guid>
```

Inside a session, a few useful commands are:

| Command | What it does |
| --- | --- |
| `/help` | List commands and usage. |
| `/models` | Pick the active model. |
| `/profile <name>` | Switch profiles such as implementation, planning, and review. |
| `/permissions` | Review what runs automatically, asks first, or is denied. |
| `/autocommit [on\|off\|status]` | Show or toggle automatic AI git commits for the current workspace. |
| `/init` | Scaffold workspace-local `.stemcode` files. |
| `/undo` / `/redo` | Roll back or re-apply the most recent tracked edit. |

The interactive terminal also keeps you moving while StemCode works:

- Queue the next prompt or slash command with Enter while a turn is running; queued items run in order as soon as it finishes (F4 removes the newest).
- Press Esc to interrupt the current turn, or Esc again to abandon a stuck turn locally.
- Use Ctrl+A to select all input, and Tab to complete file and directory paths after a `!` or `!!` shell command.
- Scrolling up to read history pauses auto-scroll until you return to the bottom.

DeepSeek models get an automatic tool-argument repair pass so malformed tool calls still run. See the [documentation](docs/documentation.md#terminal-input-and-keys) for details.

## Providers

StemCode supports:

- OpenAI
- ChatGPT Plus/Pro sign-in
- Anthropic Claude Pro/Max sign-in
- GitHub Copilot sign-in
- OpenRouter
- OpenCode Zen
- Kilo Code
- Cerebras
- Groq
- DeepSeek
- Anthropic
- Google AI Studio
- Ollama
- LM Studio
- Ollama Cloud
- OpenAI-compatible providers

## Built For Control

StemCode is designed for useful automation without silent surprises.

- Profiles separate implementation, planning, review, exploration, and delegated work.
- Permission rules control what runs automatically, what asks first, and what is denied.
- Sensitive actions can require approval, including file edits, shell commands, network access, MCP tools, memory writes, and elevated operations.
- Tracked file edits can be undone and redone, including targeted insertions and search/replace operations.
- Automatic AI git commits are enabled by default, happen at session end, skip workspaces with existing staged changes, and stay scoped to files StemCode actually changed.
- Secret redaction is off by default; when enabled, secret-looking values are redacted before logs, memory, audit records, and displayed tool output.
- Your workspace stays local. Only the prompt and selected context needed for a request are sent to the provider you configure.

## Documentation

The technical guide lives in [docs/documentation.md](docs/documentation.md). It covers installation details, first-run onboarding, desktop and terminal workflows, tracked edit tools, automatic git commits, VS Code and Visual Studio setup, ACP integration, CI review automation, LSP-powered code intelligence, graph-aware local codebase indexing, providers, permissions, MCP, memory, hooks, troubleshooting, release automation, and source builds.

## Contributing

Contributions are welcome. To work on StemCode from source:

1. Fork and clone the repository.
2. Restore, build, and run the CLI:

   ```bash
   dotnet restore StemCode.CrossPlatform.slnx
   dotnet build StemCode.CrossPlatform.slnx
   dotnet run --project StemCode.CLI/StemCode.CLI.csproj
   ```

3. Run the test suite before opening a pull request:

   ```bash
   dotnet test StemCode.Tests/StemCode.Tests.csproj
   ```

Open an [issue](https://github.com/rizwan3d/StemCode/issues) to report bugs or propose features, and keep pull requests focused with a clear description of the change. See [docs/documentation.md](docs/documentation.md#build-from-source) for full source-build details.

## Support

- Browse the [documentation](docs/documentation.md) for setup, workflows, and troubleshooting.
- Report bugs or request features via [GitHub Issues](https://github.com/rizwan3d/StemCode/issues).
- Find the latest builds on the [Releases](https://github.com/rizwan3d/StemCode/releases/latest) page.

## License

Apache License 2.0. See [LICENSE.txt](LICENSE.txt).
