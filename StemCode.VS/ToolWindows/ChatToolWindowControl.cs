using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using EnvDTE;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Workspace.VSIntegration.Contracts;
using StemCode.VS.Services;
using Process = System.Diagnostics.Process;

namespace StemCode.VS.ToolWindows
{
    public sealed partial class ChatToolWindowControl : UserControl, IDisposable
    {
        private readonly LogService _log;
        private readonly VsAgentService _agentService;
        private readonly StemCodeProcessManager _processManager;
        private readonly System.Collections.ObjectModel.ObservableCollection<ChatMessage> _messages = new();
        private readonly System.Collections.ObjectModel.ObservableCollection<string> _promptQueue = new(); // prompts typed while a turn runs
        private bool _disposed;
        private CancellationTokenSource? _turnCts;
        private bool _promptRunning;
        private bool _hostInitialized;
        private AssistantMessage? _streamingAssistant;
        private ReasoningMessage? _streamingReasoning;
        private SystemMessage? _fileEditsSummaryMessage; // single running "files changed" tally, updated in place.
        private readonly Dictionary<string, ToolCallStatusMessage> _toolCallMessages = new();
        private string? _activeModalRequestId;
        private string _workingDirectory = string.Empty;
        private string? _sessionWorkingDirectory; // cwd the live ACP session was created with.
        private bool _syncingWorkingDirectory;
        private SolutionEvents? _solutionEvents; // kept alive; GC'd events stop firing.
        private IVsFolderWorkspaceService? _folderWorkspaceService; // kept alive for unsubscribe.
        private string? _authToken;
        private string[] _extraArgs = Array.Empty<string>();
        private bool _cliWasAutoResolved;
        private bool _setupDone;
        private Task? _startTask;          // in-flight start, shared by every caller that needs the agent.
        private bool _userStopped;         // true after Stop / /exit, so the exit isn't reported as a crash.

        // Slash commands for autocomplete: the CLI's live list once it arrives, else the static catalog.
        private IReadOnlyList<ChatCommandSuggestion> _commands = ChatCommands.All;
        private System.Text.StringBuilder? _capture; // collects agent text for /copy and /export.
        private string _lastAgentText = string.Empty;

        // Active permission request, for the 1-9 / Esc shortcuts.
        private readonly List<string> _activePermissionOptionIds = new();
        private bool _activeAllowCancel;

        // Latest session info, used by the model/settings pickers and status bar.
        private string? _modelId;
        private string? _profileName;
        private string? _thinkingMode;
        private string? _reasoningEffort;
        private string? _providerName;
        private readonly List<string> _availableModels = new();
        private readonly List<(string Name, string? Description)> _profiles = new();
        private long _contextWindow;
        private long _contextUsed;

        public ChatToolWindowControl()
        {
            InitializeComponent();

            _log = LogService.Instance;
            _processManager = new StemCodeProcessManager();
            _agentService = new VsAgentService(_processManager);

            _agentService.HostExited += OnHostExited;
            _agentService.HostError += OnHostError;
            _agentService.NotificationReceived += OnNotificationReceived;

            MessagesList.ItemsSource = _messages;
            PromptQueueList.ItemsSource = _promptQueue;
            _promptQueue.CollectionChanged += (_, _) =>
                PromptQueueList.Visibility = _promptQueue.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            InputTextBox.TextChanged += (_, _) => { UpdateInputState(); UpdateSuggestions(); };
            SendButton.IsEnabled = false;

            // Route all chat hyperlink clicks (file refs + URLs) here.
            AddHandler(Hyperlink.RequestNavigateEvent, new RequestNavigateEventHandler(OnRequestNavigate));

            // only Loaded — Unloaded fires on every dock/tab/auto-hide change,
            // not just close. Disposal is owned by ChatToolWindow.OnClose/Dispose.
            Loaded += OnLoadedAsync;
        }

        private bool _initialized;

        private async void OnLoadedAsync(object sender, RoutedEventArgs e)
        {
            if (_initialized || _disposed) return; // Loaded can fire repeatedly; init once.
            _initialized = true;
            try
            {
                if (GetOptionsPageOrNull()?.AutoStart == false)
                {
                    SetStatus("Not started", StatusKind.Idle);
                    return; // starts on the first message, or Tools → StemCode → Start.
                }
                if (!_userStopped) await StartAgentAsync();
            }
            catch (Exception ex)
            {
                _log.Error("Failed to initialize", ex);
                AddSystemMessage($"Failed to initialize: {ex.Message}");
            }
        }

        // ───── Process lifecycle (Start / Stop / Restart) ─────

        /// <summary>Starts the CLI if it isn't running. Concurrent callers share one start.</summary>
        public async Task StartAgentAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (_disposed) return;
            if (_hostInitialized && _agentService.IsRunning) return;

            if (_startTask is null || _startTask.IsCompleted)
            {
                _startTask = StartAgentCoreAsync();
            }
            await _startTask;
        }

        public async Task StopAgentAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _userStopped = true;
            _hostInitialized = false;
            try { await _agentService.StopAsync(); }
            catch (Exception ex) { _log.Warn("Stopping StemCode failed.", ex); }
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _promptRunning = false;
            CompletePendingToolCalls();
            UpdateInputState();
            HideBanner();
            SetStatus("Stopped", StatusKind.Idle);
            _log.Info("StemCode stopped.");
        }

        public async Task RestartAgentAsync()
        {
            await StopAgentAsync();
            await StartAgentAsync();
        }

        /// <summary>Starts the agent when needed; false (with a banner shown) when it can't run.</summary>
        private async Task<bool> EnsureStartedAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (_hostInitialized && _agentService.IsConnected) return true;

            await StartAgentAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (_hostInitialized && _agentService.IsConnected) return true;

            AddSystemMessage("StemCode isn't running. Use Retry in the banner above, or open the logs for details.");
            return false;
        }

        private async Task StartAgentCoreAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _userStopped = false;
            HideBanner();
            SetStatus("Starting...", StatusKind.Busy);
            // Output from a new process never continues a message from the previous one.
            _streamingAssistant = null;
            _streamingReasoning = null;

            EnsureSetup();

            // Re-read options on every start so changes apply after Restart.
            StemCodeOptionsPage? options = GetOptionsPageOrNull();
            _log.MinLevel = (options?.LogLevel ?? StemCodeLogLevel.Info).ToString().ToLowerInvariant();
            _authToken = options?.AcpAuthenticationToken?.Trim();
            _extraArgs = SplitArgs(options?.ExtraArguments);

            string? cliExePath = await ResolveOrInstallCliAsync(options);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (cliExePath is null)
            {
                SetStatus("Error", StatusKind.Error);
                ShowBanner("StemCode CLI was not found. Install it, or set its path in Tools → Options → StemCode, then retry.");
                return; // user declined install / not found; message already shown.
            }

            try
            {
                // A previous start may have left a process behind (e.g. initialize failed).
                if (_agentService.IsRunning) await _agentService.StopAsync();

                _agentService.Start(cliExePath, _extraArgs);
                _log.Info("StemCode ACP server started.");
                await InitializeAcpSessionAsync();

                if (_hostInitialized && (options?.CheckForUpdates ?? true) && _cliWasAutoResolved)
                {
                    _ = Task.Run(() => CheckForUpdateAsync(cliExePath));
                }
            }
            catch (Exception ex)
            {
                _log.Error("Failed to start StemCode ACP server", ex);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                SetStatus("Error", StatusKind.Error);
                ShowBanner($"StemCode failed to start: {ex.Message}");
            }
        }

        /// <summary>One-time wiring: working directory and solution/folder change events.</summary>
        private void EnsureSetup()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_setupDone) return;
            _setupDone = true;

            _workingDirectory = ResolveWorkingDirectory();

            // Tool window can restore before the solution loads (package auto-loads on NoSolution),
            // leaving _workingDirectory at the process cwd. Re-resolve when a solution opens.
            // DTE SolutionEvents over IVsSolutionEvents COM advise — fires on the UI thread.
            if (_solutionEvents is null && Package.GetGlobalService(typeof(SDTE)) is DTE dte)
            {
                _solutionEvents = dte.Events.SolutionEvents;
                _solutionEvents.Opened += OnSolutionOpened;
            }

            // Open Folder/CMake mode fires no SolutionEvents; the workspace can load after the
            // tool window restores, leaving _workingDirectory at the process cwd. Re-resolve when it changes.
            if (_folderWorkspaceService is null &&
                Package.GetGlobalService(typeof(SComponentModel)) is IComponentModel cm &&
                cm.GetService<IVsFolderWorkspaceService>() is IVsFolderWorkspaceService fws)
            {
                _folderWorkspaceService = fws;
                _folderWorkspaceService.OnActiveWorkspaceChanged += OnActiveWorkspaceChanged;
            }
        }

        private async Task<string?> ResolveOrInstallCliAsync(StemCodeOptionsPage? options)
        {
            string? configured = options?.StemCodeCliPath?.Trim();
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured; // explicit path — trust it.
            }

            _cliWasAutoResolved = true;
            // Resolution shells out to npm/where; keep it off the UI thread.
            (string command, bool available) = await Task.Run(() =>
            {
                string resolved = StemCodeCli.ResolveCommand("stemcode", _log);
                return (resolved, StemCodeCli.IsAvailable(resolved));
            });
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (available)
            {
                return command;
            }

            string manual = $"irm {StemCodeCli.InstallScriptUrl} | iex";
            if (!(options?.AutoInstall ?? true) || VSPackage.Instance?.Confirm(
                    "Install StemCode CLI",
                    "The StemCode CLI was not found on this machine.\n\n" +
                    "Download and install the latest release from GitHub now?\n\n" +
                    $"Runs in PowerShell: {manual}\n" +
                    $"Installs to: {StemCodeCli.InstallDirectory}") != true)
            {
                AddSystemMessage($"StemCode CLI was not found. Install it from PowerShell with `{manual}`, or set Command in Tools → Options → StemCode.");
                return null;
            }

            SetStatus("Installing...", StatusKind.Busy);
            AddSystemMessage("Installing the StemCode CLI from GitHub…");
            bool ok = await StemCodeCli.InstallFromGitHubAsync(_log);

            (command, available) = await Task.Run(() =>
            {
                string resolved = StemCodeCli.ResolveCommand("stemcode", _log);
                return (resolved, StemCodeCli.IsAvailable(resolved));
            });
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!ok || !available)
            {
                AddSystemMessage($"Installing the StemCode CLI failed; the log has the installer output. You can run `{manual}` in PowerShell yourself, then click Retry.");
                return null;
            }

            _log.Info("stemcode installed successfully.");
            AddSystemMessage($"StemCode CLI installed to {StemCodeCli.InstallDirectory}.");
            return command;
        }

        private async Task CheckForUpdateAsync(string command)
        {
            try
            {
                if (!StemCodeCli.ShouldCheckForUpdate()) return;
                StemCodeCli.RecordUpdateCheck();

                (string? current, string? latest) = StemCodeCli.CheckVersions(command, _log);
                if (current is null || latest is null || !StemCodeCli.IsNewer(latest, current)) return;

                _log.Info($"stemcode update available: {current} -> {latest}");
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                bool update = VSPackage.Instance?.Confirm("StemCode Update",
                    $"A new StemCode CLI is available ({current} → {latest}).\n\n" +
                    "Update now from GitHub? StemCode stops while the installer runs, then starts again.") == true;
                if (!update) return;

                // The installer replaces stemcode.exe in place, so the running CLI has to go first.
                await StopAgentAsync();
                SetStatus("Updating...", StatusKind.Busy);
                AddSystemMessage($"Updating the StemCode CLI to {latest}…");
                bool ok = await StemCodeCli.InstallFromGitHubAsync(_log);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                AddSystemMessage(ok
                    ? $"StemCode CLI updated to {latest}."
                    : "Updating the StemCode CLI failed; the log has the installer output. Starting the current version.");
                await StartAgentAsync();
            }
            catch (Exception ex)
            {
                _log.Debug("Update check skipped: " + ex.Message);
            }
        }

        private static string[] SplitArgs(string? args)
            => string.IsNullOrWhiteSpace(args)
                ? Array.Empty<string>()
                : args!.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

        private void OnHostExited()
        {
            _log.Warn("StemCode ACP server exited.");
            _hostInitialized = false;
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                SetStatus("Stopped", StatusKind.Idle);
                _promptRunning = false;
                CompletePendingToolCalls();
                UpdateInputState();
                if (!_userStopped && !_disposed)
                {
                    ShowBanner("StemCode stopped unexpectedly. Retry to start it again, or open the logs for details.");
                }
            });
        }

        private void OnHostError(string error)
        {
            _log.Error($"StemCode ACP error: {error}");
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                AddSystemMessage($"Backend error: {error}");
            });
        }

        private async void OnNotificationReceived(string method, Dictionary<string, object?>? parameters)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            try { await HandleNotificationAsync(method, parameters); }
            catch (Exception ex) { _log.Error($"Notification failed: {method}", ex); }
        }

        private async Task HandleNotificationAsync(string method, Dictionary<string, object?>? parameters)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            switch (method)
            {
                case VsProtocol.MessageChunk:
                    {
                        string? text = GetParamString(parameters, "text");
                        if (text is not null) AppendMessageChunk(text, "assistant");
                        break;
                    }

                case VsProtocol.ReasoningChunk:
                    {
                        string? text = GetParamString(parameters, "text");
                        if (text is not null) AppendMessageChunk(text, "reasoning");
                        break;
                    }

                case VsProtocol.UserMessageChunk:
                    {
                        // Only sent while the CLI replays a session's history (resume, fork, tree...).
                        // Each chunk is one whole user message.
                        string? text = GetParamString(parameters, "text");
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            _streamingAssistant = null;
                            _streamingReasoning = null;
                            _messages.Add(new UserMessage { Text = text! });
                            ScrollToBottom();
                        }
                        break;
                    }

                case VsProtocol.AvailableCommands:
                    {
                        if (parameters?.TryGetValue("availableCommands", out object? raw) == true && raw is JsonElement list)
                        {
                            List<AvailableCommandEntry> entries = ChatCommands.ParseAvailableCommands(list);
                            _commands = ChatCommands.Build(entries);
                            _log.Debug($"CLI advertised {entries.Count} slash commands.");
                        }
                        break;
                    }

                case VsProtocol.HistoryReset:
                    // /fork, /clone, /import, /resume or /tree switched sessions; the CLI replays
                    // the new session's history right after this.
                    ClearConversation();
                    _lastAgentText = string.Empty;
                    break;

                case VsProtocol.ToolCallStart:
                    {
                        string id = GetParamString(parameters, "toolCallId") ?? Guid.NewGuid().ToString("N");
                        if (!_toolCallMessages.TryGetValue(id, out ToolCallStatusMessage? msg))
                        {
                            // Text after a tool call starts a new bubble below it, keeping the transcript in order.
                            _streamingAssistant = null;
                            _streamingReasoning = null;
                            msg = new ToolCallStatusMessage { ToolCallId = id };
                            _toolCallMessages[id] = msg;
                            _messages.Add(msg);
                        }
                        string? newTitle = GetParamString(parameters, "title");
                        if (!string.IsNullOrEmpty(newTitle)) msg.Title = newTitle!;
                        else if (string.IsNullOrEmpty(msg.Title)) msg.Title = "Tool";
                        msg.Kind = GetParamString(parameters, "kind") ?? msg.Kind;
                        msg.Status = GetParamString(parameters, "status") ?? "running";
                        string? raw = GetRawInputJson(parameters);
                        if (!string.IsNullOrEmpty(raw)) msg.RawInput = raw!;
                        ScrollToBottom();
                        break;
                    }

                case VsProtocol.ToolCallEnd:
                    {
                        string? id = GetParamString(parameters, "toolCallId");
                        if (id is not null && _toolCallMessages.TryGetValue(id, out ToolCallStatusMessage? msg))
                        {
                            string? raw = GetRawInputJson(parameters);
                            if (!string.IsNullOrEmpty(raw)) msg.RawInput = raw!;
                            foreach (string line in GetParamArray(parameters, "content")) msg.ContentLines.Add(line);
                            msg.Status = GetParamString(parameters, "status") ?? "completed";
                        }
                        break;
                    }

                case VsProtocol.PlanUpdate:
                    {
                        string? planText = FormatPlanUpdate(parameters);
                        if (!string.IsNullOrEmpty(planText)) { _messages.Add(new SystemMessage { Text = $"Plan:\n{planText}" }); ScrollToBottom(); }
                        break;
                    }

                case VsProtocol.FileEditsSummary:
                    UpdateFileEditsSummary(parameters);
                    break;

                case VsProtocol.RequestPermission:
                    ShowModalRequest(parameters, "permission");
                    break;

                case VsProtocol.RequestText:
                    ShowModalRequest(parameters, "text");
                    break;

                case VsProtocol.SessionInfo:
                    ApplySessionInfo(parameters);
                    break;

                case VsProtocol.Ready:
                    SetStatus("Ready", StatusKind.Ready);
                    break;
            }
        }

        private void ApplySessionInfo(Dictionary<string, object?>? p)
        {
            if (p is null) return;

            _modelId = GetParamString(p, "modelId") ?? _modelId;
            _profileName = GetParamString(p, "agentProfileName") ?? _profileName;
            _thinkingMode = GetParamString(p, "thinkingMode") ?? _thinkingMode;
            _reasoningEffort = GetParamString(p, "reasoningEffort") ?? _reasoningEffort;
            _providerName = GetParamString(p, "providerName") ?? _providerName;

            List<string> models = GetParamStringArrayList(p, "availableModelIds");
            if (models.Count > 0) { _availableModels.Clear(); _availableModels.AddRange(models); }

            List<(string, string?)> profiles = GetProfiles(p, "availableAgentProfiles");
            if (profiles.Count > 0) { _profiles.Clear(); _profiles.AddRange(profiles); }

            _contextWindow = GetParamLong(p, "activeModelContextWindowTokens") ?? _contextWindow;
            _contextUsed = GetParamLong(p, "sectionEstimatedContextTokens") ?? _contextUsed;

            ModelText.Text = string.IsNullOrEmpty(_modelId)
                ? (string.IsNullOrEmpty(_profileName) ? "Model" : _profileName)
                : (string.IsNullOrEmpty(_profileName) ? _modelId : $"{_modelId} · {_profileName}");

            UpdateContextText();
        }

        private void UpdateContextText()
        {
            if (_contextWindow > 0)
            {
                int pct = (int)Math.Round(100.0 * _contextUsed / _contextWindow);
                ContextText.Text = $"{FormatTokens(_contextUsed)}/{FormatTokens(_contextWindow)} ({pct}%)";
                ContextText.Foreground = pct >= 80 ? ChatBrushes.Error : ChatBrushes.Dim;
            }
            else
            {
                ContextText.Text = "";
            }
        }

        private static string FormatTokens(long n) => n >= 1000 ? $"{n / 1000.0:0.#}k" : n.ToString();

        private void AppendMessageChunk(string text, string role)
        {
            if (role == "reasoning")
            {
                if (_streamingReasoning is null)
                {
                    _streamingReasoning = new ReasoningMessage { Text = text };
                    _messages.Add(_streamingReasoning);
                }
                else _streamingReasoning.Text += text;
                _streamingReasoning.NotifyDerived();
            }
            else
            {
                _capture?.Append(text);
                if (_streamingAssistant is null)
                {
                    _streamingAssistant = new AssistantMessage { Text = text };
                    _messages.Add(_streamingAssistant);
                }
                else _streamingAssistant.Text += text;
            }
            ScrollToBottom();
        }

        private void AddSystemMessage(string text)
        {
            _messages.Add(new SystemMessage { Text = text });
            ScrollToBottom();
        }

        // ───── Composer ─────

        private async void OnSendClick(object sender, RoutedEventArgs e)
        {
            if (_promptRunning) await CancelTurnAsync();
            else await SendCurrentInput();
        }

        private async void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            if (SuggestionPopup.IsOpen && SuggestionList.Items.Count > 0)
            {
                switch (e.Key)
                {
                    case Key.Down:
                        SuggestionList.SelectedIndex = Math.Min(SuggestionList.SelectedIndex + 1, SuggestionList.Items.Count - 1);
                        SuggestionList.ScrollIntoView(SuggestionList.SelectedItem);
                        e.Handled = true;
                        return;
                    case Key.Up:
                        SuggestionList.SelectedIndex = Math.Max(SuggestionList.SelectedIndex - 1, 0);
                        SuggestionList.ScrollIntoView(SuggestionList.SelectedItem);
                        e.Handled = true;
                        return;
                    case Key.Tab:
                        AcceptSuggestion();
                        e.Handled = true;
                        return;
                    case Key.Enter:
                        AcceptSuggestion();
                        e.Handled = true;
                        return;
                    case Key.Escape:
                        SuggestionPopup.IsOpen = false;
                        e.Handled = true;
                        return;
                }
            }

            if (e.Key == Key.Enter && !Keyboard.IsKeyDown(Key.LeftShift) && !Keyboard.IsKeyDown(Key.RightShift))
            {
                e.Handled = true;
                await SendCurrentInput();
            }
        }

        private async Task SendCurrentInput()
        {
            SuggestionPopup.IsOpen = false;
            string text = InputTextBox.Text.Trim();
            if (string.IsNullOrEmpty(text)) return;

            InputTextBox.Clear();
            await SubmitAsync(text);
        }

        /// <summary>
        /// Sends a prompt or slash command exactly as if it were typed: it is queued while a turn
        /// runs, terminal-only commands are handled locally, and the agent is started on demand.
        /// </summary>
        public async Task SubmitAsync(string text)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            text = text?.Trim() ?? string.Empty;
            if (text.Length == 0) return;

            // Busy? queue it; the queue drains automatically when the turn ends.
            if (_promptRunning)
            {
                _promptQueue.Add(text);
                return;
            }

            _messages.Add(new UserMessage { Text = text });
            ScrollToBottom();
            await DispatchAsync(text);
        }

        private async Task DispatchAsync(string text)
        {
            LocalChatCommand local = ChatCommands.ParseLocal(text);
            if (local != LocalChatCommand.None)
            {
                await RunLocalCommandAsync(local);
                return;
            }

            if (!await EnsureStartedAsync()) return;

            if (ChatCommands.IsExportCommand(text))
            {
                await RunExportAsync(text, openAutomatically: false);
                return;
            }

            if (text.StartsWith("/", StringComparison.Ordinal)) await SendHostCommandAsync(text);
            else await HandleUserPromptAsync(text);
        }

        /// <summary>Commands the terminal CLI handles in its own UI; mapped to Visual Studio equivalents.</summary>
        private async Task RunLocalCommandAsync(LocalChatCommand command)
        {
            switch (command)
            {
                case LocalChatCommand.Clear:
                    ClearConversation();
                    break;
                case LocalChatCommand.New:
                    if (await EnsureStartedAsync()) await StartNewSessionAsync();
                    break;
                case LocalChatCommand.Models:
                    ShowModelPicker();
                    break;
                case LocalChatCommand.Copy:
                    CopyLastAgentMessage();
                    break;
                case LocalChatCommand.Exit:
                    AddSystemMessage("Stopping StemCode. Send a message or use Tools → StemCode → Start to start it again.");
                    await StopAgentAsync();
                    break;
                case LocalChatCommand.Trajectory:
                    if (await EnsureStartedAsync()) await RunExportAsync("/export trajectory", openAutomatically: true);
                    break;
                case LocalChatCommand.Voice:
                    await RunVoiceDictationAsync();
                    break;
                case LocalChatCommand.VoiceSetup:
                    await RunVoiceSetupAsync();
                    break;
                case LocalChatCommand.VoiceUpdate:
                    await RunVoiceUpdateAsync();
                    break;
            }
        }

        private void CopyLastAgentMessage()
        {
            string text = _lastAgentText.Trim();
            if (text.Length == 0)
            {
                AddSystemMessage("There is no agent message to copy yet.");
                return;
            }

            try
            {
                Clipboard.SetText(text);
                AddSystemMessage("Copied the last agent message to the clipboard.");
            }
            catch (Exception ex)
            {
                _log.Warn("Clipboard write failed.", ex);
                AddSystemMessage("Couldn't write to the clipboard: " + ex.Message);
            }
        }

        /// <summary>
        /// Runs /export through the CLI, then opens the exported file: HTML in the browser,
        /// JSON in the editor. When openAutomatically is false the user is asked first.
        /// </summary>
        private async Task RunExportAsync(string command, bool openAutomatically)
        {
            string output = await RunCommandCapturingAsync(command);
            (string Format, string FilePath)? exported = ChatCommands.ParseExportedFile(output);
            if (exported is null) return;

            string path = exported.Value.FilePath;
            if (!openAutomatically &&
                VSPackage.Instance?.Confirm("StemCode", $"Exported the session as {exported.Value.Format}:\n{path}\n\nOpen it now?") != true)
            {
                return;
            }

            OpenExportedFile(path);
        }

        private void OpenExportedFile(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!File.Exists(path))
            {
                AddSystemMessage($"Exported file not found: {path}");
                return;
            }

            string ext = Path.GetExtension(path);
            if (ext.Equals(".html", StringComparison.OrdinalIgnoreCase) || ext.Equals(".htm", StringComparison.OrdinalIgnoreCase))
            {
                try { Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
                catch (Exception ex) { _log.Warn($"Failed to open {path}: {ex.Message}"); AddSystemMessage($"Couldn't open {path}: {ex.Message}"); }
            }
            else
            {
                OpenFileInEditor(path, 0);
            }
        }

        private async Task<string> RunCommandCapturingAsync(string command)
        {
            var capture = new System.Text.StringBuilder();
            _capture = capture;
            try { await SendHostCommandAsync(command); }
            finally { if (ReferenceEquals(_capture, capture)) _capture = null; }
            return capture.ToString();
        }

        private void OnRemoveQueuedClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string queued }) _promptQueue.Remove(queued);
        }

        private async Task DrainPromptQueueAsync()
        {
            if (_promptRunning || _promptQueue.Count == 0) return;

            string text = _promptQueue[0];
            _promptQueue.RemoveAt(0);
            await SubmitAsync(text);
        }

        private void ClearConversation()
        {
            _messages.Clear();
            _toolCallMessages.Clear();
            _streamingAssistant = null;
            _streamingReasoning = null;
            _fileEditsSummaryMessage = null;
            EmptyStateText.Visibility = Visibility.Visible;
        }

        private async Task HandleUserPromptAsync(string prompt)
        {
            _turnCts?.Cancel();
            _turnCts = new CancellationTokenSource();

            _promptRunning = true;
            _streamingAssistant = null;
            _streamingReasoning = null;
            _toolCallMessages.Clear();
            MetricsText.Text = "";
            UpdateInputState();

            // Agent replies (not command output) are remembered for /copy.
            var reply = new System.Text.StringBuilder();
            _capture = reply;
            try
            {
                VsAgentService.SessionPromptResponse? response = await _agentService.SendPromptAsync(prompt);
                ShowMetrics(response?.Metrics);
            }
            catch (Exception ex)
            {
                _log.Error("Prompt failed", ex);
                AddSystemMessage($"Error: {ex.Message}");
            }
            finally
            {
                if (ReferenceEquals(_capture, reply)) _capture = null;
                if (reply.ToString().Trim().Length > 0) _lastAgentText = reply.ToString();
                _promptRunning = false;
                CompletePendingToolCalls();
                _streamingAssistant = null;
                _streamingReasoning = null;
                UpdateInputState();
                _ = DrainPromptQueueAsync();
            }
        }

        private void ShowMetrics(VsAgentService.TurnMetrics? m)
        {
            if (m is null) { MetricsText.Text = ""; return; }
            var parts = new List<string>();
            if (m.ElapsedMilliseconds is > 0) parts.Add($"{m.ElapsedMilliseconds.Value / 1000.0:0.0}s");
            if (m.EstimatedOutputTokens is > 0) parts.Add($"{FormatTokens((long)m.EstimatedOutputTokens.Value)} tok");
            long rounds = (long)(m.ToolRoundCount ?? 0);
            if (rounds > 0) parts.Add($"{rounds} round{(rounds == 1 ? "" : "s")}");
            long retries = (long)(m.ProviderRetryCount ?? 0);
            if (retries > 0) parts.Add($"{retries} retr{(retries == 1 ? "y" : "ies")}");
            MetricsText.Text = string.Join(" · ", parts);
        }

        private async Task SendHostCommandAsync(string command)
        {
            _promptRunning = true;
            _streamingAssistant = null;
            _streamingReasoning = null;
            UpdateInputState();

            try { await _agentService.SendPromptAsync(command); }
            catch (Exception ex)
            {
                _log.Error($"Command failed: {command}", ex);
                AddSystemMessage($"Error: {ex.Message}");
            }
            finally
            {
                _promptRunning = false;
                UpdateInputState();
                _ = DrainPromptQueueAsync();
            }
        }

        private async Task CancelTurnAsync()
        {
            _turnCts?.Cancel();
            await _agentService.CancelPromptAsync();
            _promptRunning = false;
            _streamingAssistant = null;
            _streamingReasoning = null;
            UpdateInputState();
        }

        // ───── Autocomplete ─────

        private void UpdateSuggestions()
        {
            string text = InputTextBox.Text;
            int caret = InputTextBox.CaretIndex;

            // Suggest model ids after "/use " and profiles after "/profile ".
            if (text.StartsWith("/use ", StringComparison.OrdinalIgnoreCase))
            {
                string q = text.Substring(5).Trim().ToLowerInvariant();
                ShowSuggestionItems(_availableModels
                    .Where(m => m.ToLowerInvariant().Contains(q))
                    .Select(m => new ChatCommandSuggestion(m, "", "Switch model", "/use " + m)));
                return;
            }
            if (text.StartsWith("/profile ", StringComparison.OrdinalIgnoreCase))
            {
                string q = text.Substring(9).Trim().ToLowerInvariant();
                ShowSuggestionItems(_profiles
                    .Where(pr => pr.Name.ToLowerInvariant().Contains(q))
                    .Select(pr => new ChatCommandSuggestion(pr.Name, "", pr.Description ?? "Agent profile", "/profile " + pr.Name)));
                return;
            }

            // Slash-command catalog while typing the first token.
            if (text.StartsWith("/") && !text.TrimEnd().Contains(' ') && caret <= text.Length)
            {
                ShowSuggestionItems(ChatCommands.Match(_commands, text.Trim()));
                return;
            }

            SuggestionPopup.IsOpen = false;
        }

        private void ShowSuggestionItems(IEnumerable<ChatCommandSuggestion> items)
        {
            var list = items.Take(50).ToList();
            if (list.Count == 0) { SuggestionPopup.IsOpen = false; return; }
            SuggestionList.ItemsSource = list;
            SuggestionList.SelectedIndex = 0;
            SuggestionPopup.IsOpen = true;
        }

        private void AcceptSuggestion()
        {
            if (SuggestionList.SelectedItem is ChatCommandSuggestion s)
            {
                InputTextBox.Text = s.InsertText;
                InputTextBox.CaretIndex = InputTextBox.Text.Length;
                InputTextBox.Focus();
            }
            SuggestionPopup.IsOpen = false;
        }

        private void OnSuggestionClick(object sender, MouseButtonEventArgs e)
        {
            if (SuggestionList.SelectedItem is not null) AcceptSuggestion();
        }

        // ───── Toolbar ─────

        private async void OnNewChatClick(object sender, RoutedEventArgs e) => await StartNewSessionAsync();

        private void OnAddContextClick(object sender, RoutedEventArgs e)
        {
            PrefillInput("/read ");
        }

        private void OnModelClick(object sender, RoutedEventArgs e) => ShowModelPicker();

        private async void OnSessionsClick(object sender, RoutedEventArgs e) => await ShowSessionBrowserAsync();

        private void OnSettingsClick(object sender, RoutedEventArgs e) => ShowSettingsPanel();

        // ───── External entry points (editor commands) ─────

        public Task SubmitExternalPromptAsync(string text) => SubmitAsync(text);

        public void PrefillInput(string text)
        {
            ShowSettingsClose();
            InputTextBox.Text = text;
            InputTextBox.CaretIndex = text.Length;
            InputTextBox.Focus();
        }

        /// <summary>Opens the composer's slash-command list (the Visual Studio take on a command palette).</summary>
        public void ShowSlashCommandPicker()
        {
            PrefillInput("/");
            UpdateSuggestions();
        }

        public async Task NewChatAsync()
        {
            if (await EnsureStartedAsync()) await StartNewSessionAsync();
        }

        public void ShowSettings() => ShowSettingsPanel();

        /// <summary>Opens this session's extension log in the editor.</summary>
        public void OpenLogs()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string? path = _log.LogFilePath;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                // The editor can't open a file that's still being written, so open a snapshot.
                try
                {
                    string snapshot = Path.Combine(Path.GetTempPath(), "StemCode", Path.GetFileName(path));
                    Directory.CreateDirectory(Path.GetDirectoryName(snapshot)!);
                    using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var target = new FileStream(snapshot, FileMode.Create, FileAccess.Write))
                    {
                        source.CopyTo(target);
                    }
                    OpenFileInEditor(snapshot, 0);
                    return;
                }
                catch (Exception ex)
                {
                    _log.Warn("Couldn't snapshot the log; opening its folder instead.", ex);
                }
            }

            try { Process.Start(new System.Diagnostics.ProcessStartInfo(LogService.LogDirectory) { UseShellExecute = true }); }
            catch (Exception ex) { AddSystemMessage($"Couldn't open the log folder {LogService.LogDirectory}: {ex.Message}"); }
        }

        // ───── Status banner (shown when the CLI can't start or exits unexpectedly) ─────

        private void ShowBanner(string message)
        {
            BannerText.Text = message;
            Banner.Visibility = Visibility.Visible;
        }

        private void HideBanner() => Banner.Visibility = Visibility.Collapsed;

        private async void OnBannerRetryClick(object sender, RoutedEventArgs e)
        {
            try { await RestartAgentAsync(); }
            catch (Exception ex) { _log.Error("Retry failed", ex); ShowBanner($"StemCode failed to start: {ex.Message}"); }
        }

        private void OnBannerLogsClick(object sender, RoutedEventArgs e) => OpenLogs();

        // ───── Sessions ─────

        private async Task StartNewSessionAsync()
        {
            try
            {
                await _agentService.NewSessionAsync(_workingDirectory);
                ClearConversation();
                SetStatus("Ready", StatusKind.Ready);
            }
            catch (Exception ex)
            {
                _log.Error("Failed to start new ACP session", ex);
                AddSystemMessage($"Error: {ex.Message}");
            }
        }

        private async Task ResumeSessionAsync(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                AddSystemMessage("Usage: /resume <session-id>");
                return;
            }

            try
            {
                // Clear first: the CLI replays the loaded session's history while the request is in flight.
                ClearConversation();
                await _agentService.LoadSessionAsync(_workingDirectory, sessionId);
                AddSystemMessage($"Resumed session {sessionId}");
                SetStatus("Ready", StatusKind.Ready);
            }
            catch (Exception ex)
            {
                _log.Error($"Failed to resume ACP session: {sessionId}", ex);
                AddSystemMessage($"Error: {ex.Message}");
            }
        }

        private async Task ShowSessionBrowserAsync()
        {
            List<VsAgentService.SessionSummary> sessions;
            try { sessions = await _agentService.ListSessionsAsync(); }
            catch (Exception ex) { AddSystemMessage($"Failed to list sessions: {ex.Message}"); return; }

            ModalTitle.Text = "Sessions";
            ModalDescription.Text = $"{sessions.Count} saved session{(sessions.Count == 1 ? "" : "s")}.";
            ModalDescription.Visibility = Visibility.Visible;
            ModalBodyPanel.Children.Clear();
            ModalActionsPanel.Children.Clear();

            foreach (VsAgentService.SessionSummary s in sessions)
            {
                var btn = MenuButton($"{s.Title}", Subtitle(s));
                string id = s.SessionId;
                btn.Click += async (_, _) => { CloseModal(); await ResumeSessionAsync(id); };
                ModalBodyPanel.Children.Add(btn);
            }

            var fork = SecondaryButton("Fork current");
            fork.Click += async (_, _) => { CloseModal(); await SendHostCommandAsync("/fork"); };
            var export = SecondaryButton("Export (JSON)");
            export.Click += async (_, _) => { CloseModal(); await SendHostCommandAsync("/export json"); };
            ModalActionsPanel.Children.Add(fork);
            ModalActionsPanel.Children.Add(export);
            ModalActionsPanel.Children.Add(CloseButton());

            ModalOverlay.Visibility = Visibility.Visible;

            static string Subtitle(VsAgentService.SessionSummary s)
            {
                var bits = new List<string>();
                if (!string.IsNullOrEmpty(s.ModelId)) bits.Add(s.ModelId!);
                if (s.TurnCount is > 0) bits.Add($"{s.TurnCount} turns");
                if (!string.IsNullOrEmpty(s.ParentSessionId)) bits.Add("fork");
                if (!string.IsNullOrEmpty(s.UpdatedAtUtc)) bits.Add(s.UpdatedAtUtc!);
                return string.Join(" · ", bits);
            }
        }

        // ───── Model picker ─────

        private void ShowModelPicker()
        {
            ModalTitle.Text = "Model & reasoning";
            ModalDescription.Text = string.IsNullOrEmpty(_modelId) ? "" : $"Current: {_modelId}";
            ModalDescription.Visibility = string.IsNullOrEmpty(ModalDescription.Text) ? Visibility.Collapsed : Visibility.Visible;
            ModalBodyPanel.Children.Clear();
            ModalActionsPanel.Children.Clear();

            ModalBodyPanel.Children.Add(SectionLabel("Models"));
            if (_availableModels.Count == 0)
            {
                ModalBodyPanel.Children.Add(Hint("No models reported yet. Run /models to refresh."));
            }
            foreach (string model in _availableModels)
            {
                var btn = MenuButton(model, model == _modelId ? "current" : "");
                string m = model;
                btn.Click += async (_, _) => { CloseModal(); await SendHostCommandAsync("/use " + m); };
                ModalBodyPanel.Children.Add(btn);
            }

            ModalBodyPanel.Children.Add(SectionLabel("Reasoning effort"));
            foreach (string level in new[] { "none", "minimal", "low", "medium", "high", "xhigh", "max" })
            {
                var btn = MenuButton(level, level.Equals(_reasoningEffort, StringComparison.OrdinalIgnoreCase) ? "current" : "");
                string l = level;
                btn.Click += async (_, _) => { CloseModal(); await SendHostCommandAsync("/reasoning " + l); };
                ModalBodyPanel.Children.Add(btn);
            }

            ModalBodyPanel.Children.Add(SectionLabel("Thinking"));
            var on = MenuButton("on", string.Equals(_thinkingMode, "on", StringComparison.OrdinalIgnoreCase) ? "current" : "");
            on.Click += async (_, _) => { CloseModal(); await SendHostCommandAsync("/thinking on"); };
            var off = MenuButton("off", string.Equals(_thinkingMode, "off", StringComparison.OrdinalIgnoreCase) ? "current" : "");
            off.Click += async (_, _) => { CloseModal(); await SendHostCommandAsync("/thinking off"); };
            ModalBodyPanel.Children.Add(on);
            ModalBodyPanel.Children.Add(off);

            ModalActionsPanel.Children.Add(CloseButton());
            ModalOverlay.Visibility = Visibility.Visible;
        }

        // ───── Settings ─────

        private void ShowSettingsPanel()
        {
            ModalTitle.Text = "Settings";
            var summary = new List<string>();
            if (!string.IsNullOrEmpty(_providerName)) summary.Add("Provider: " + _providerName);
            if (!string.IsNullOrEmpty(_modelId)) summary.Add("Model: " + _modelId);
            if (!string.IsNullOrEmpty(_profileName)) summary.Add("Profile: " + _profileName);
            if (!string.IsNullOrEmpty(_thinkingMode)) summary.Add("Thinking: " + _thinkingMode);
            ModalDescription.Text = string.Join("\n", summary);
            ModalDescription.Visibility = summary.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ModalBodyPanel.Children.Clear();
            ModalActionsPanel.Children.Clear();

            AddSettingButton("Model & reasoning…", () => { CloseModal(); ShowModelPicker(); });
            AddSettingButton("Browse sessions…", async () => { CloseModal(); await ShowSessionBrowserAsync(); });
            AddSettingButton("Manage skills…", () => { CloseModal(); ShowPluginPanel(); });
            AddSettingButton("Show config (/config)", async () => { CloseModal(); await SendHostCommandAsync("/config"); });
            AddSettingButton("Re-run onboarding (/onboard)", async () => { CloseModal(); await SendHostCommandAsync("/onboard"); });
            AddSettingButton("Show MCP servers (/mcp)", async () => { CloseModal(); await SendHostCommandAsync("/mcp"); });
            AddSettingButton("Open Visual Studio options…", () =>
            {
                CloseModal();
                ThreadHelper.JoinableTaskFactory.Run(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    VSPackage.Instance?.ShowOptionPageInternal();
                });
            });

            ModalActionsPanel.Children.Add(CloseButton());
            ModalOverlay.Visibility = Visibility.Visible;
        }

        private void AddSettingButton(string label, Action onClick)
        {
            var btn = MenuButton(label, "");
            btn.Click += (_, _) => onClick();
            ModalBodyPanel.Children.Add(btn);
        }

        private void ShowSettingsClose() => CloseModal();

        // ───── Plugin marketplace panel ─────

        private sealed record MarketplaceEntry(string Alias, string Repository, string Ref);
        private sealed record InstalledEntry(string PluginId, string MarketplaceAlias, string Repository, string Ref, int FileCount);

        private void ShowPluginPanel()
        {
            (List<MarketplaceEntry> markets, List<InstalledEntry> installed) = ReadPluginState();

            ModalTitle.Text = "Skills";
            ModalDescription.Visibility = Visibility.Collapsed;
            ModalBodyPanel.Children.Clear();
            ModalActionsPanel.Children.Clear();

            // Installed skills
            ModalBodyPanel.Children.Add(SectionLabel("Installed skills"));
            if (installed.Count == 0)
            {
                ModalBodyPanel.Children.Add(Hint("No skills installed yet."));
            }
            foreach (InstalledEntry plugin in installed)
            {
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(plugin.Repository)) parts.Add($"{plugin.Repository}@{plugin.Ref}");
                if (!string.IsNullOrEmpty(plugin.MarketplaceAlias)) parts.Add("via " + plugin.MarketplaceAlias);
                parts.Add($"{plugin.FileCount} file{(plugin.FileCount == 1 ? "" : "s")}");

                var row = PluginRow(plugin.PluginId, string.Join(" · ", parts));
                var uninstall = DangerButton("Uninstall");
                string id = plugin.PluginId;
                uninstall.Click += async (_, _) => await RunPluginAsync($"/skill uninstall {id}");
                row.Children.Add(uninstall);
                ModalBodyPanel.Children.Add(row);
            }

            // Install a skill
            ModalBodyPanel.Children.Add(SectionLabel("Install a skill"));
            if (markets.Count == 0)
            {
                ModalBodyPanel.Children.Add(Hint("Add a marketplace below before installing."));
            }
            else
            {
                var idInput = ModalInput("skill-id");
                var select = new ComboBox { Margin = new Thickness(0, 0, 0, 6), MinHeight = 26 };
                select.SetResourceReference(StyleProperty, VsResourceKeys.ComboBoxStyleKey);
                foreach (MarketplaceEntry m in markets) select.Items.Add($"{m.Alias} ({m.Repository})");
                select.SelectedIndex = 0;
                var force = new CheckBox
                {
                    Content = "Overwrite existing files (--force)",
                    Margin = new Thickness(0, 0, 0, 8)
                };
                force.SetResourceReference(StyleProperty, VsResourceKeys.CheckBoxStyleKey);
                var installBtn = PrimaryButton("Install");
                installBtn.Click += async (_, _) =>
                {
                    string pid = idInput.Text.Trim();
                    if (string.IsNullOrEmpty(pid)) { idInput.Focus(); return; }
                    string alias = markets[Math.Max(0, select.SelectedIndex)].Alias;
                    await RunPluginAsync($"/skill install {pid}@{alias}{(force.IsChecked == true ? " --force" : "")}");
                };
                ModalBodyPanel.Children.Add(idInput);
                ModalBodyPanel.Children.Add(select);
                ModalBodyPanel.Children.Add(force);
                ModalBodyPanel.Children.Add(installBtn);
            }

            // Marketplaces
            ModalBodyPanel.Children.Add(SectionLabel("Marketplaces"));
            if (markets.Count == 0)
            {
                ModalBodyPanel.Children.Add(Hint("No marketplaces configured."));
            }
            foreach (MarketplaceEntry m in markets)
            {
                var row = PluginRow(m.Alias, $"{m.Repository}@{m.Ref}");
                var browse = SecondaryButton("Browse");
                string alias = m.Alias;
                browse.Click += async (_, _) => { CloseModal(); await SendHostCommandAsync($"/skill browse {alias}"); };
                var remove = DangerButton("Remove");
                remove.Click += async (_, _) => await RunPluginAsync($"/skill marketplace remove {alias}");
                row.Children.Add(browse);
                row.Children.Add(remove);
                ModalBodyPanel.Children.Add(row);
            }

            var repoInput = ModalInput("owner/repo");
            var addBtn = SecondaryButton("Add marketplace");
            addBtn.Click += async (_, _) =>
            {
                string repo = repoInput.Text.Trim();
                if (string.IsNullOrEmpty(repo)) { repoInput.Focus(); return; }
                await RunPluginAsync($"/skill marketplace add {repo}");
            };
            ModalBodyPanel.Children.Add(repoInput);
            ModalBodyPanel.Children.Add(addBtn);

            var refresh = SecondaryButton("Refresh");
            refresh.Click += (_, _) => ShowPluginPanel();
            ModalActionsPanel.Children.Add(refresh);
            ModalActionsPanel.Children.Add(CloseButton());

            ModalOverlay.Visibility = Visibility.Visible;
        }

        private async Task RunPluginAsync(string command)
        {
            CloseModal();
            await SendHostCommandAsync(command);
            ShowPluginPanel(); // re-read state from disk after the CLI applies the change.
        }

        private (List<MarketplaceEntry>, List<InstalledEntry>) ReadPluginState()
        {
            var markets = new List<MarketplaceEntry>();
            var installed = new List<InstalledEntry>();

            foreach (string root in PluginRoots())
            {
                string dir = Path.Combine(root, ".stemcode", "skills");
                if (markets.Count == 0) markets = ParseMarketplaces(Path.Combine(dir, "marketplaces.json"));
                if (installed.Count == 0) installed = ParseInstalled(Path.Combine(dir, "installed.json"));
            }
            return (markets, installed);
        }

        private IEnumerable<string> PluginRoots()
        {
            if (!string.IsNullOrEmpty(_workingDirectory)) yield return _workingDirectory;
            yield return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        private List<MarketplaceEntry> ParseMarketplaces(string path)
        {
            var result = new List<MarketplaceEntry>();
            try
            {
                if (!File.Exists(path)) return result;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("marketplaces", out JsonElement map) && map.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty p in map.EnumerateObject())
                    {
                        result.Add(new MarketplaceEntry(
                            p.Name,
                            TryGetJsonString(p.Value, "repository") ?? "",
                            TryGetJsonString(p.Value, "ref") ?? "main"));
                    }
                }
            }
            catch (Exception ex) { _log.Debug($"Failed to read marketplaces.json: {ex.Message}"); }
            return result;
        }

        private List<InstalledEntry> ParseInstalled(string path)
        {
            var result = new List<InstalledEntry>();
            try
            {
                if (!File.Exists(path)) return result;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("plugins", out JsonElement map) && map.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty p in map.EnumerateObject())
                    {
                        int files = p.Value.TryGetProperty("files", out JsonElement f) && f.ValueKind == JsonValueKind.Array ? f.GetArrayLength() : 0;
                        result.Add(new InstalledEntry(
                            p.Name,
                            TryGetJsonString(p.Value, "marketplaceAlias") ?? "",
                            TryGetJsonString(p.Value, "repository") ?? "",
                            TryGetJsonString(p.Value, "ref") ?? "main",
                            files));
                    }
                }
            }
            catch (Exception ex) { _log.Debug($"Failed to read installed.json: {ex.Message}"); }
            return result;
        }

        private static StackPanel PluginRow(string title, string subtitle)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            var info = new StackPanel { Width = 280 };
            info.Children.Add(new TextBlock { Text = title, Foreground = ChatBrushes.Text });
            info.Children.Add(new TextBlock { Text = subtitle, Foreground = ChatBrushes.Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap });
            row.Children.Add(info);
            return row;
        }

        private static TextBox ModalInput(string placeholder)
        {
            var box = new TextBox
            {
                Tag = placeholder,
                ToolTip = placeholder,
                MinWidth = 240, MinHeight = 28,
                Margin = new Thickness(0, 0, 0, 6),
                CaretBrush = ChatBrushes.InputText,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            box.SetResourceReference(StyleProperty, ChatStyles.Input);
            return box;
        }

        private static Button PrimaryButton(string text) => Styled(SecondaryButton(text), ChatStyles.PrimaryButton);

        private static Button DangerButton(string text) => Styled(SecondaryButton(text), ChatStyles.DangerButton);

        private static Button Styled(Button button, string styleKey)
        {
            button.SetResourceReference(StyleProperty, styleKey);
            return button;
        }

        // ───── Modal building blocks ─────

        private void CloseModal()
        {
            ModalOverlay.Visibility = Visibility.Collapsed;
            _activeModalRequestId = null;
            _activePermissionOptionIds.Clear();
            _activeAllowCancel = false;
        }

        private static TextBlock SectionLabel(string text) => new()
        {
            Text = text.ToUpperInvariant(),
            Foreground = ChatBrushes.Dim,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 10, 0, 6)
        };

        private static TextBlock Hint(string text) => new()
        {
            Text = text,
            Foreground = ChatBrushes.Dim,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4)
        };

        private static Button MenuButton(string title, string meta)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = title, Foreground = ChatBrushes.Text });
            if (!string.IsNullOrEmpty(meta))
            {
                content.Children.Add(new TextBlock
                {
                    Text = meta,
                    Foreground = ChatBrushes.Dim,
                    FontSize = 11,
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            var button = new Button { Content = content };
            button.SetResourceReference(StyleProperty, ChatStyles.MenuButton);
            return button;
        }

        private static Button SecondaryButton(string text)
        {
            var button = new Button
            {
                Content = text,
                MinWidth = 80, MinHeight = 28,
                Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(6, 0, 0, 0)
            };
            button.SetResourceReference(StyleProperty, ChatStyles.Button);
            return button;
        }

        private Button CloseButton()
        {
            var btn = SecondaryButton("Close");
            btn.Click += (_, _) => CloseModal();
            return btn;
        }

        // ───── Permission / text request modal ─────

        private void ShowModalRequest(Dictionary<string, object?>? parameters, string kind)
        {
            if (parameters is null) return;

            Dictionary<string, object?> request = GetNestedObject(parameters, "request") ?? parameters;

            string? requestId = GetParamString(request, "id");
            if (requestId is null) return;
            if (_activeModalRequestId == requestId) return;
            _activeModalRequestId = requestId;

            string? title = GetParamString(request, "title")
                ?? GetParamString(request, "label")
                ?? (kind == "text" ? "Input Required" : "Permission Required");
            string? description = GetParamString(request, "description") ?? "";
            bool allowCancel = GetParamBool(request, "allowCancellation", true);
            _activeAllowCancel = allowCancel;
            _activePermissionOptionIds.Clear();

            ModalTitle.Text = title;
            ModalDescription.Text = description;
            ModalDescription.Visibility = string.IsNullOrEmpty(description) ? Visibility.Collapsed : Visibility.Visible;
            ModalBodyPanel.Children.Clear();
            ModalActionsPanel.Children.Clear();

            if (kind == "text")
            {
                string? defaultValue = GetParamString(request, "defaultValue") ?? "";
                bool isSecret = GetParamBool(request, "isSecret", false);
                Control inputControl = isSecret
                    ? new PasswordBox
                    {
                        MinHeight = 30,
                        Padding = new Thickness(8),
                        Margin = new Thickness(0, 0, 0, 8),
                        CaretBrush = ChatBrushes.InputText
                    }
                    : new TextBox
                    {
                        AcceptsReturn = true,
                        TextWrapping = TextWrapping.Wrap,
                        Text = defaultValue,
                        MinHeight = 40, MaxHeight = 120,
                        Padding = new Thickness(8),
                        Margin = new Thickness(0, 0, 0, 8),
                        CaretBrush = ChatBrushes.InputText,
                        FontSize = 13
                    };
                inputControl.SetResourceReference(StyleProperty, ChatStyles.Input);
                ModalBodyPanel.Children.Add(inputControl);

                var submitBtn = PrimaryButton("Submit");
                submitBtn.Click += (_, _) =>
                {
                    string value = inputControl is PasswordBox pb ? pb.Password : ((TextBox)inputControl).Text;
                    _ = ResolveClientRequestAsync(requestId, "submitted", value, null);
                };

                if (allowCancel)
                {
                    var cancelBtn = SecondaryButton("Cancel");
                    cancelBtn.Click += (_, _) => { _ = ResolveClientRequestAsync(requestId, "cancelled", null, null); };
                    ModalActionsPanel.Children.Add(cancelBtn);
                }
                ModalActionsPanel.Children.Add(submitBtn);
            }
            else
            {
                _activePermissionOptionIds.Clear();
                _activeAllowCancel = allowCancel;
                Button? first = null;
                int index = 0;
                foreach (PermissionOptionInfo option in GetPermissionOptions(request))
                {
                    index++;
                    // Options 1-9 get a number-key shortcut, like the inline approval cards in VS Code.
                    var optionBtn = MenuButton(index <= 9 ? $"{index}.  {option.Name}" : option.Name, "");
                    string captured = option.OptionId;
                    optionBtn.Click += (_, _) => { _ = ResolveClientRequestAsync(requestId, "selected", null, captured); };
                    ModalBodyPanel.Children.Add(optionBtn);
                    _activePermissionOptionIds.Add(captured);
                    first ??= optionBtn;
                }

                ModalBodyPanel.Children.Add(Hint(allowCancel
                    ? "Press 1–9 to choose an option, or Esc to cancel."
                    : "Press 1–9 to choose an option."));

                if (allowCancel)
                {
                    var cancelBtn = SecondaryButton("Cancel");
                    cancelBtn.Click += (_, _) => { _ = ResolveClientRequestAsync(requestId, "cancelled", null, null); };
                    ModalActionsPanel.Children.Add(cancelBtn);
                }

                ModalOverlay.Visibility = Visibility.Visible;
                // Take keyboard focus so the shortcuts work straight away.
                first?.Focus();
                return;
            }
            ModalOverlay.Visibility = Visibility.Visible;
        }

        /// <summary>1-9 picks a permission option and Esc cancels a request or closes a panel.</summary>
        private bool TryHandleModalKey(KeyEventArgs e)
        {
            if (ModalOverlay.Visibility != Visibility.Visible) return false;

            string? requestId = _activeModalRequestId;
            if (e.Key == Key.Escape)
            {
                if (requestId is null) { CloseModal(); return true; }
                if (_activeAllowCancel) { _ = ResolveClientRequestAsync(requestId, "cancelled", null, null); return true; }
                return false;
            }

            // Digits only apply to permission choices, and never while typing in a text field.
            if (requestId is null || _activePermissionOptionIds.Count == 0 || Keyboard.FocusedElement is TextBox) return false;

            int digit = e.Key >= Key.D1 && e.Key <= Key.D9 ? e.Key - Key.D1 + 1
                : e.Key >= Key.NumPad1 && e.Key <= Key.NumPad9 ? e.Key - Key.NumPad1 + 1
                : 0;
            if (digit == 0 || digit > _activePermissionOptionIds.Count) return false;

            _ = ResolveClientRequestAsync(requestId, "selected", null, _activePermissionOptionIds[digit - 1]);
            return true;
        }

        private async Task ResolveClientRequestAsync(string requestId, string outcome, string? value, string? optionId)
        {
            CloseModal();
            try { await _agentService.ResolveClientRequestAsync(requestId, outcome, value, optionId); }
            catch (Exception ex) { _log.Error($"Failed to resolve request: {requestId}", ex); }
        }

        // ───── File navigation ─────

        private void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            e.Handled = true;
            Uri uri = e.Uri;
            if (uri.Scheme == "stemcodefile")
            {
                string query = uri.Query.TrimStart('?');
                string path = "";
                int line = 0;
                foreach (string pair in query.Split('&'))
                {
                    int eq = pair.IndexOf('=');
                    if (eq < 0) continue;
                    string key = pair.Substring(0, eq);
                    string val = Uri.UnescapeDataString(pair.Substring(eq + 1));
                    if (key == "p") path = val;
                    else if (key == "l") int.TryParse(val, out line);
                }
                OpenFileInEditor(path, line);
            }
            else
            {
                try { Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
                catch (Exception ex) { _log.Warn($"Failed to open URL {uri}: {ex.Message}"); }
            }
        }

        private void OpenFileInEditor(string path, int line)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                string full = path;
                if (!Path.IsPathRooted(full) && !string.IsNullOrEmpty(_workingDirectory))
                {
                    full = Path.Combine(_workingDirectory, path);
                }
                if (!File.Exists(full)) { AddSystemMessage($"File not found: {path}"); return; }

                if (Package.GetGlobalService(typeof(DTE)) is DTE dte)
                {
                    EnvDTE.Window window = dte.ItemOperations.OpenFile(full);
                    if (line > 0 && dte.ActiveDocument?.Selection is EnvDTE.TextSelection sel) sel.GotoLine(line, false);
                    window?.Activate();
                }
            }
            catch (Exception ex) { _log.Warn($"Failed to open file {path}: {ex.Message}"); }
        }

        // ───── Status / state ─────

        private void SetStatus(string text, StatusKind kind)
        {
            StatusText.Text = text;
            StatusDot.Fill = kind switch
            {
                StatusKind.Ready => ChatBrushes.Ok,
                StatusKind.Busy => ChatBrushes.Busy,
                StatusKind.Error => ChatBrushes.Error,
                _ => ChatBrushes.Dim
            };
        }

        private void UpdateInputState()
        {
            bool hasText = InputTextBox.Text.Trim().Length > 0;
            // Running: square = stop (always enabled). Idle: arrow = send (needs text).
            SendButton.IsEnabled = _promptRunning || hasText;
            InputTextBox.IsEnabled = true; // keep editable so prompts can be queued while busy
            SendButton.Content = _promptRunning ? "\uE71A" : "\uE724"; // Stop : Send
            SendButton.ToolTip = _promptRunning ? "Stop (Esc)" : "Send (Enter)";
            SendButton.Background = _promptRunning ? ChatBrushes.Error : ChatBrushes.Accent;
        }

        private void ScrollToBottom()
        {
            EmptyStateText.Visibility = Visibility.Collapsed;
            if (MessagesScrollViewer.ViewportHeight > 0) MessagesScrollViewer.ScrollToBottom();
        }

        private async void OnSolutionOpened() => await ReResolveWorkingDirectoryAsync();

        // IVsFolderWorkspaceService.OnActiveWorkspaceChanged is an AsyncEventHandler (returns Task).
        private async Task OnActiveWorkspaceChanged(object sender, EventArgs e) => await ReResolveWorkingDirectoryAsync();

        private async Task ReResolveWorkingDirectoryAsync()
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                string resolved = ResolveWorkingDirectory();
                if (string.Equals(resolved, _workingDirectory, StringComparison.OrdinalIgnoreCase)) return;

                _workingDirectory = resolved;
                // If the host is still starting, the post-init reconcile in
                // InitializeAcpSessionAsync picks this up; no-op until then.
                await EnsureSessionWorkingDirectoryAsync();
            }
            catch (Exception ex)
            {
                _log.Error("Failed to update working directory after workspace change", ex);
            }
        }

        // Recreate the ACP session if its cwd drifted from _workingDirectory (e.g. the
        // solution opened after the session was already created with the process cwd).
        private async Task EnsureSessionWorkingDirectoryAsync()
        {
            if (!_hostInitialized || _syncingWorkingDirectory) return;
            if (string.Equals(_sessionWorkingDirectory, _workingDirectory, StringComparison.OrdinalIgnoreCase)) return;

            _syncingWorkingDirectory = true;
            try
            {
                string dir = _workingDirectory;
                await _agentService.NewSessionAsync(dir);
                _sessionWorkingDirectory = dir;
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            }
            finally { _syncingWorkingDirectory = false; }
        }

        private async Task InitializeAcpSessionAsync()
        {
            if (_hostInitialized) return;

            try
            {
                string dir = _workingDirectory;
                await _agentService.InitializeAsync(dir, _authToken);
                _sessionWorkingDirectory = dir;
                _hostInitialized = true;
                // A solution may have opened while we were starting up; reconcile now.
                await EnsureSessionWorkingDirectoryAsync();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                SetStatus("Ready", StatusKind.Ready);
                HideBanner();
            }
            catch (Exception ex)
            {
                _log.Error("Failed to initialize backend session", ex);
                _hostInitialized = false;
                // Don't leave a half-initialized process behind; Retry starts a fresh one.
                try { await _agentService.StopAsync(); } catch (Exception stopEx) { _log.Debug("Stop after failed init: " + stopEx.Message); }
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                SetStatus("Error", StatusKind.Error);
                ShowBanner($"StemCode couldn't start a session: {ex.Message} If this is a first run, run 'stemcode' in a terminal once to finish provider setup, then retry.");
            }
        }

        private static string ResolveCliExecutable()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            StemCodeOptionsPage? options = GetOptionsPageOrNull();
            string? configuredPath = options?.StemCodeCliPath?.Trim();
            return !string.IsNullOrWhiteSpace(configuredPath) ? configuredPath! : "stemcode.exe";
        }

        private static string ResolveWorkingDirectory()
        {
            string dir = ResolveWorkingDirectoryCore();
            LogService.Instance.Info($"Resolved working directory: {dir}");
            return dir;
        }

        private static string ResolveWorkingDirectoryCore()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            StemCodeOptionsPage? options = GetOptionsPageOrNull();
            string? configured = options?.WorkingDirectory?.Trim();
            if (!string.IsNullOrWhiteSpace(configured)) return configured!;

            // Open Folder/CMake mode → the opened folder (IVsSolution doesn't report it reliably).
            string? folder = VSPackage.OpenFolderDirectory();
            LogService.Instance.Info($"OpenFolderDirectory probe: {folder ?? "<null>"}");
            if (!string.IsNullOrWhiteSpace(folder)) return folder!;

            // real .sln → the solution folder.
            if (Package.GetGlobalService(typeof(SVsSolution)) is IVsSolution solution &&
                solution.GetSolutionInfo(out string solutionDirectory, out string solutionFile, out string _) == 0 &&
                !string.IsNullOrWhiteSpace(solutionDirectory))
            {
                LogService.Instance.Info($"GetSolutionInfo dir: {solutionDirectory}, file: {solutionFile}");
                // Opening a bare project makes VS write a throwaway .sln under %TEMP%; prefer the project dir.
                bool tempSolution = !string.IsNullOrWhiteSpace(solutionFile) &&
                    solutionDirectory.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase);
                if (tempSolution && FirstProjectDirectory() is string projDir) return projDir;
                return solutionDirectory;
            }

            string? firstProject = FirstProjectDirectory();
            LogService.Instance.Info($"FirstProjectDirectory probe: {firstProject ?? "<null>"} (falling back to process cwd if null)");
            return firstProject ?? Directory.GetCurrentDirectory();
        }

        private static string? FirstProjectDirectory()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Package.GetGlobalService(typeof(SDTE)) is not DTE dte) return null;
            foreach (Project project in dte.Solution.Projects)
            {
                try
                {
                    string? file = project.FullName; // project file path; empty for virtual/solution-folder nodes
                    if (!string.IsNullOrWhiteSpace(file) && File.Exists(file))
                        return Path.GetDirectoryName(file);
                }
                catch { /* unloaded or non-file project node */ }
            }
            return null;
        }

        private static StemCodeOptionsPage? GetOptionsPageOrNull()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return VSPackage.Instance?.GetOptionsPage();
        }

        private void CompletePendingToolCalls()
        {
            foreach (ToolCallStatusMessage message in _toolCallMessages.Values.Where(static m => m.Status == "running"))
            {
                message.Status = "completed";
            }
        }

        // ───── Parsing helpers ─────

        // Renders the running per-file change tally as a single chat message, updated in place
        // each turn. File names are markdown links (stemcodefile scheme) so clicks open the editor.
        private void UpdateFileEditsSummary(Dictionary<string, object?>? parameters)
        {
            if (parameters?.TryGetValue("files", out object? value) != true ||
                value is not JsonElement files ||
                files.ValueKind != JsonValueKind.Array ||
                files.GetArrayLength() == 0)
            {
                return;
            }

            var lines = new List<string>();
            int totalAdded = 0;
            int totalRemoved = 0;
            foreach (JsonElement file in files.EnumerateArray())
            {
                string? path = TryGetJsonString(file, "displayPath");
                if (string.IsNullOrWhiteSpace(path)) continue;
                int added = file.TryGetProperty("addedLineCount", out JsonElement a) && a.TryGetInt32(out int ai) ? ai : 0;
                int removed = file.TryGetProperty("removedLineCount", out JsonElement r) && r.TryGetInt32(out int ri) ? ri : 0;
                string action = TryGetJsonString(file, "action") ?? "Edited";
                string actionColor = action switch
                {
                    "Created" => "green",
                    "Deleted" => "red",
                    _ => "yellow",
                };
                totalAdded += added;
                totalRemoved += removed;
                lines.Add($"- {{c:{actionColor}}}{action}{{/c}} · [{path}]({path}) ({{c:green}}+{added}{{/c}} {{c:red}}-{removed}{{/c}})");
            }

            if (lines.Count == 0) return;

            string text = $"**Files changed ({lines.Count})**  ·  +{totalAdded} -{totalRemoved}"
                + Environment.NewLine + Environment.NewLine
                + string.Join(Environment.NewLine, lines);

            if (_fileEditsSummaryMessage is null)
            {
                _fileEditsSummaryMessage = new SystemMessage { Text = text };
                _messages.Add(_fileEditsSummaryMessage);
            }
            else
            {
                _fileEditsSummaryMessage.Text = text;
            }
            ScrollToBottom();
        }

        private static string? FormatPlanUpdate(Dictionary<string, object?>? parameters)
        {
            if (parameters?.TryGetValue("entries", out object? value) != true || value is null)
            {
                return GetParamString(parameters, "text");
            }

            if (value is JsonElement element && element.ValueKind == JsonValueKind.Array)
            {
                var lines = new List<string>();
                foreach (JsonElement entry in element.EnumerateArray())
                {
                    string? content = TryGetJsonString(entry, "content") ?? TryGetJsonString(entry, "Content");
                    string? status = TryGetJsonString(entry, "status") ?? TryGetJsonString(entry, "Status");
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        string mark = status?.ToLowerInvariant() switch
                        {
                            "completed" => "✓",
                            "in_progress" => "▶",
                            "failed" => "✗",
                            "skipped" => "⊘",
                            _ => "○"
                        };
                        lines.Add($"{mark} {content}");
                    }
                }
                return lines.Count > 0 ? string.Join(Environment.NewLine, lines) : null;
            }

            return GetParamString(parameters, "text");
        }

        private static string? GetRawInputJson(Dictionary<string, object?>? p)
        {
            object? v = GetParamObject(p, "rawInput") ?? GetParamObject(p, "input");
            if (v is null) return null;
            if (v is JsonElement je) return je.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : je.GetRawText();
            return v.ToString();
        }

        private static List<PermissionOptionInfo> GetPermissionOptions(Dictionary<string, object?> request)
        {
            if (request.TryGetValue("options", out object? value) != true || value is null)
                return new List<PermissionOptionInfo>();

            if (value is JsonElement element && element.ValueKind == JsonValueKind.Array)
            {
                var result = new List<PermissionOptionInfo>();
                foreach (JsonElement option in element.EnumerateArray())
                {
                    if (option.ValueKind == JsonValueKind.String)
                    {
                        string optionText = option.GetString() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(optionText)) result.Add(new PermissionOptionInfo(optionText, optionText));
                    }
                    else if (option.ValueKind == JsonValueKind.Object)
                    {
                        string? optionId = TryGetJsonString(option, "optionId") ?? TryGetJsonString(option, "OptionId");
                        string? name = TryGetJsonString(option, "name") ?? TryGetJsonString(option, "Name") ?? optionId;
                        if (!string.IsNullOrWhiteSpace(optionId) && !string.IsNullOrWhiteSpace(name))
                            result.Add(new PermissionOptionInfo(optionId!, name!));
                    }
                }
                return result;
            }

            return new List<PermissionOptionInfo>();
        }

        private static string? TryGetJsonString(JsonElement element, string propertyName)
            => element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out JsonElement property) &&
               property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;

        private static Dictionary<string, object?>? GetNestedObject(Dictionary<string, object?>? p, string key)
        {
            if (p?.TryGetValue(key, out object? value) != true || value is null) return null;
            if (value is Dictionary<string, object?> dict) return dict;
            if (value is JsonElement element && element.ValueKind == JsonValueKind.Object)
                return JsonSerializer.Deserialize<Dictionary<string, object?>>(element.GetRawText());
            return null;
        }

        private static string? GetParamString(Dictionary<string, object?>? p, string key)
        {
            if (p?.TryGetValue(key, out object? v) != true || v is null) return null;
            if (v is JsonElement je) return je.ValueKind == JsonValueKind.String ? je.GetString() : (je.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : je.ToString());
            return v.ToString();
        }

        private static object? GetParamObject(Dictionary<string, object?>? p, string key) => p?.TryGetValue(key, out object? v) == true ? v : null;

        private static long? GetParamLong(Dictionary<string, object?>? p, string key)
        {
            if (p?.TryGetValue(key, out object? v) != true || v is null) return null;
            if (v is JsonElement je && je.ValueKind == JsonValueKind.Number && je.TryGetInt64(out long n)) return n;
            if (v is long l) return l;
            if (v is int i) return i;
            if (long.TryParse(v.ToString(), out long parsed)) return parsed;
            return null;
        }

        private static List<string> GetParamStringArrayList(Dictionary<string, object?>? p, string key)
        {
            var result = new List<string>();
            if (p?.TryGetValue(key, out object? v) != true || v is null) return result;
            if (v is JsonElement je && je.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in je.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String) result.Add(item.GetString()!);
            }
            return result;
        }

        private static List<(string, string?)> GetProfiles(Dictionary<string, object?>? p, string key)
        {
            var result = new List<(string, string?)>();
            if (p?.TryGetValue(key, out object? v) != true || v is null) return result;
            if (v is JsonElement je && je.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in je.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String) { result.Add((item.GetString()!, null)); }
                    else if (item.ValueKind == JsonValueKind.Object)
                    {
                        string? name = TryGetJsonString(item, "name");
                        if (!string.IsNullOrEmpty(name)) result.Add((name!, TryGetJsonString(item, "description")));
                    }
                }
            }
            return result;
        }

        private static string[] GetParamArray(Dictionary<string, object?>? p, string key)
        {
            if (p?.TryGetValue(key, out object? v) != true || v is null) return Array.Empty<string>();
            if (v is JsonElement je && je.ValueKind == JsonValueKind.Array)
            {
                var items = new List<string>();
                foreach (JsonElement item in je.EnumerateArray())
                {
                    string? text = ReadJsonContentText(item);
                    if (!string.IsNullOrWhiteSpace(text)) items.Add(text!);
                }
                return items.ToArray();
            }
            if (v is string[] arr) return arr;
            return Array.Empty<string>();
        }

        private static string? ReadJsonContentText(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String) return element.GetString();
            if (element.ValueKind != JsonValueKind.Object) return null;
            if (element.TryGetProperty("text", out JsonElement textElement) && textElement.ValueKind == JsonValueKind.String)
                return textElement.GetString();
            if (element.TryGetProperty("content", out JsonElement contentElement)) return ReadJsonContentText(contentElement);
            return null;
        }

        private static bool GetParamBool(Dictionary<string, object?>? p, string key, bool def)
        {
            if (p?.TryGetValue(key, out object? v) != true || v is null) return def;
            if (v is bool b) return b;
            if (v is JsonElement je) { if (je.ValueKind == JsonValueKind.True) return true; if (je.ValueKind == JsonValueKind.False) return false; }
            if (bool.TryParse(v.ToString(), out bool parsed)) return parsed;
            return def;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_solutionEvents != null) _solutionEvents.Opened -= OnSolutionOpened;
            if (_folderWorkspaceService != null) _folderWorkspaceService.OnActiveWorkspaceChanged -= OnActiveWorkspaceChanged;
            _turnCts?.Cancel();
            _turnCts?.Dispose();
            _agentService.HostExited -= OnHostExited;
            _agentService.HostError -= OnHostError;
            _agentService.NotificationReceived -= OnNotificationReceived;
            _agentService.Dispose();
            _processManager.Dispose();
        }

        private sealed record PermissionOptionInfo(string OptionId, string Name);
    }
}
