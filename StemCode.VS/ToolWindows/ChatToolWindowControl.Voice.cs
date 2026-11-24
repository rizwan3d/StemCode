using StemCode.VS.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

namespace StemCode.VS.ToolWindows
{
    public sealed partial class ChatToolWindowControl
    {
        private bool _voiceBusy; // one `stemcode --voice-*` run at a time.
        private bool _voiceDictating;
        private bool _voiceCancelled;
        private Process? _voiceProcess;
        private ProcessTree? _voiceTree;

        protected override async void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (TryHandleModalKey(e))
            {
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape && _voiceBusy)
            {
                e.Handled = true;
                CancelVoice();
                return;
            }

            if (e.Key == Key.R &&
                (Keyboard.Modifiers & ModifierKeys.Control) != 0 &&
                InputTextBox.IsKeyboardFocusWithin)
            {
                e.Handled = true;
                await ToggleVoiceDictationAsync();
                return;
            }

            base.OnPreviewKeyDown(e);
        }

        private async void OnVoiceClick(object sender, RoutedEventArgs e) => await ToggleVoiceDictationAsync();

        /// <summary>Starts dictation, or stops listening when it is already running.</summary>
        private async Task ToggleVoiceDictationAsync()
        {
            if (_voiceDictating)
            {
                CancelVoice();
                return;
            }

            try { await RunVoiceDictationAsync(); }
            catch (Exception ex) { _log.Error("Voice dictation failed.", ex); }
        }

        /// <summary>
        /// Stops the running voice command. The whole process tree goes, because an npm `stemcode`
        /// is a cmd shim over node over the CLI, and only the CLI holds the microphone.
        /// </summary>
        public void CancelVoice()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Process? process = _voiceProcess;
            if (process is null || _voiceCancelled) return;

            _voiceCancelled = true;
            StatusText.Text = "Voice: stopping";
            try
            {
                if (_voiceTree is not null) _voiceTree.Kill();
                else if (!process.HasExited) process.Kill();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                _log.Warn("Couldn't stop the voice process: " + ex.Message);
            }
        }

        private void UpdateVoiceButton()
        {
            VoiceButton.Content = _voiceDictating ? "" : ""; // Stop : Microphone
            VoiceButton.ToolTip = _voiceDictating ? "Stop listening (Esc)" : "Voice dictation (Ctrl+R)";
            if (_voiceDictating) VoiceButton.Foreground = ChatBrushes.Accent;
            else VoiceButton.ClearValue(ForegroundProperty); // back to the style, hover included.
        }

        /// <summary>Records speech with the local voice model and inserts the transcript at the composer caret.</summary>
        public async Task RunVoiceDictationAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (_voiceBusy)
            {
                AddSystemMessage("A voice command is already running.");
                return;
            }

            string? transcript;
            _voiceDictating = true;
            UpdateVoiceButton();
            try
            {
                transcript = await RunVoiceCommandAsync("--voice-dictate", "preparing");
            }
            finally
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                _voiceDictating = false;
                UpdateVoiceButton();
            }

            if (transcript is null) return;

            transcript = transcript.Trim();
            if (transcript.Length == 0)
            {
                StatusText.Text = "Voice: no speech detected";
                return;
            }

            InsertDictation(transcript);
        }

        /// <summary>Puts dictated text at the caret (replacing any selection), spaced from its neighbours.</summary>
        private void InsertDictation(string transcript)
        {
            string current = InputTextBox.Text ?? string.Empty;
            int start = Math.Min(InputTextBox.SelectionStart, current.Length);
            int end = Math.Min(start + InputTextBox.SelectionLength, current.Length);

            (string joined, int caret) = VoiceText.JoinDictation(current.Substring(0, start), transcript, current.Substring(end));
            InputTextBox.Text = joined;
            InputTextBox.CaretIndex = caret;
            InputTextBox.Focus();
        }

        public async Task RunVoiceUpdateAsync()
        {
            if (await RunVoiceCommandAsync("--voice-update", "updating models") is not null)
            {
                AddSystemMessage("Voice models are up to date.");
            }
        }

        /// <summary>Picks the speech model, then the microphone, and saves them with --voice-configure.</summary>
        public async Task RunVoiceSetupAsync()
        {
            string? modelJson = await RunVoiceCommandAsync("--voice-models", "loading models");
            if (modelJson is null) return;

            var models = new List<(string Id, string Label, string? Description, bool Recommended)>();
            try
            {
                using JsonDocument doc = JsonDocument.Parse(modelJson);
                foreach (JsonElement m in doc.RootElement.EnumerateArray())
                {
                    string? id = TryGetJsonString(m, "Id");
                    if (string.IsNullOrEmpty(id)) continue;
                    models.Add((id!, TryGetJsonString(m, "Label") ?? id!, TryGetJsonString(m, "Description"),
                        m.TryGetProperty("IsRecommended", out JsonElement r) && r.ValueKind == JsonValueKind.True));
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                AddSystemMessage("Voice setup could not read the available models.");
                return;
            }

            if (models.Count == 0)
            {
                AddSystemMessage("No voice models are available.");
                return;
            }

            ShowPicker("Voice model", "Choose the local speech model used for dictation.", models,
                m => (m.Label, m.Recommended ? "recommended" : m.Description ?? ""),
                async model => await PickVoiceDeviceAsync(model.Id));
        }

        private async Task PickVoiceDeviceAsync(string modelId)
        {
            string? deviceJson = await RunVoiceCommandAsync("--voice-devices", "finding microphones");
            if (deviceJson is null) return;

            var devices = new List<(string Id, string Name, bool IsDefault)>();
            try
            {
                using JsonDocument doc = JsonDocument.Parse(deviceJson);
                foreach (JsonElement d in doc.RootElement.EnumerateArray())
                {
                    string? id = TryGetJsonString(d, "Id");
                    devices.Add((id ?? "", TryGetJsonString(d, "Name") ?? id ?? "Microphone",
                        d.TryGetProperty("IsDefault", out JsonElement def) && def.ValueKind == JsonValueKind.True));
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                AddSystemMessage("Voice setup could not read microphone devices.");
                return;
            }

            if (devices.Count > 1)
            {
                ShowPicker("Voice microphone", "Choose the microphone used for dictation.", devices,
                    d => (d.Name, d.IsDefault ? "system default" : ""),
                    async device => await SaveVoiceSetupAsync(modelId, device.Id));
                return;
            }

            await SaveVoiceSetupAsync(modelId, devices.Count == 1 ? devices[0].Id : null);
        }

        private async Task SaveVoiceSetupAsync(string modelId, string? deviceId)
        {
            string args = "--voice-configure --model " + QuoteArg(modelId)
                + (string.IsNullOrEmpty(deviceId) ? "" : " --device " + QuoteArg(deviceId!));
            if (await RunVoiceCommandAsync(args, "saving setup") is not null)
            {
                AddSystemMessage("Voice setup saved.");
            }
        }

        private void ShowPicker<T>(string title, string description, IReadOnlyList<T> items,
            Func<T, (string Title, string Meta)> describe, Func<T, Task> onPick)
        {
            CloseModal();
            ModalTitle.Text = title;
            ModalDescription.Text = description;
            ModalDescription.Visibility = Visibility.Visible;
            ModalBodyPanel.Children.Clear();
            ModalActionsPanel.Children.Clear();

            foreach (T item in items)
            {
                (string itemTitle, string meta) = describe(item);
                var btn = MenuButton(itemTitle, meta);
                T captured = item;
                btn.Click += async (_, _) =>
                {
                    CloseModal();
                    try { await onPick(captured); }
                    catch (Exception ex) { _log.Error($"{title} failed.", ex); AddSystemMessage($"{title} failed: {ex.Message}"); }
                };
                ModalBodyPanel.Children.Add(btn);
            }

            ModalActionsPanel.Children.Add(CloseButton());
            ModalOverlay.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Runs one `stemcode --voice-*` command, mirroring its stderr progress in the status pill.
        /// Returns stdout, or null when it failed (the error is shown in the chat).
        /// </summary>
        private async Task<string?> RunVoiceCommandAsync(string arguments, string startingStatus)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (_voiceBusy)
            {
                AddSystemMessage("A voice command is already running.");
                return null;
            }

            _voiceBusy = true;
            _voiceCancelled = false;
            string previousStatus = StatusText.Text;
            StatusText.Text = "Voice: " + startingStatus;
            try
            {
                return await RunVoiceProcessAsync(arguments);
            }
            catch (Exception) when (_voiceCancelled)
            {
                _log.Info($"Voice command cancelled: {arguments}");
                return null;
            }
            catch (Exception ex)
            {
                _log.Error($"Voice command failed: {arguments}", ex);
                AddSystemMessage("Voice: " + ex.Message);
                return null;
            }
            finally
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                _voiceBusy = false;
                _voiceCancelled = false;
                StatusText.Text = previousStatus;
            }
        }

        private async Task<string> RunVoiceProcessAsync(string arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = await ResolveVoiceCliCommandAsync(),
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Unable to start the voice process.");
            // Kill-on-close job: cancelling (or VS exiting) releases the microphone with the whole tree.
            using ProcessTree? tree = ProcessTree.TryAttach(process, _log);
            _voiceProcess = process;
            _voiceTree = tree;
            string output;
            string error;
            int exitCode;
            try
            {
                Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                Task<string> errorTask = ReadVoiceProgressAsync(process);
                await Task.Run(() => process.WaitForExit());

                output = await outputTask;
                error = await errorTask;
                exitCode = process.ExitCode;
            }
            finally
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                _voiceProcess = null;
                _voiceTree = null;
            }

            if (_voiceCancelled) throw new OperationCanceledException("Voice command cancelled.");
            if (exitCode != 0)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(error) ? "The voice command failed." : error.Trim());
            }

            return output;
        }

        private async Task<string> ReadVoiceProgressAsync(Process process)
        {
            var error = new StringBuilder();
            string? line;
            while ((line = await process.StandardError.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (error.Length > 0) error.AppendLine();
                error.Append(line);
                string progress = VoiceText.DescribeProgress(line);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!_voiceCancelled) StatusText.Text = "Voice: " + progress;
                await TaskScheduler.Default;
            }

            return error.ToString();
        }

        private static string QuoteArg(string value)
            => value.IndexOfAny(new[] { ' ', '\t', '"' }) >= 0 ? "\"" + value.Replace("\"", "\\\"") + "\"" : value;

        /// <summary>The same CLI the chat uses: the configured path, else the resolved `stemcode`.</summary>
        private static async Task<string> ResolveVoiceCliCommandAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            string? configured = GetOptionsPageOrNull()?.StemCodeCliPath?.Trim();
            if (!string.IsNullOrWhiteSpace(configured)) return configured!;

            // Resolution shells out to npm/where; keep it off the UI thread.
            return await Task.Run(() => StemCodeCli.ResolveCommand("stemcode", LogService.Instance));
        }
    }
}
