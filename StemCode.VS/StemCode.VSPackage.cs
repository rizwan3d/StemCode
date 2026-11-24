using EnvDTE;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Workspace.VSIntegration.Contracts;
using StemCode.VS.Services;
using StemCode.VS.ToolWindows;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;

namespace StemCode.VS
{
    /// <summary>
    /// StemCode Visual Studio extension package.
    /// Manages the chat tool window and StemCode ACP lifecycle.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(VSPackage.PackageGuidString)]
    [ProvideAutoLoad(UIContextGuids80.NoSolution, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideToolWindow(typeof(ChatToolWindow),
        Style = VsDockStyle.Tabbed,
        Window = "3ae79031-e1bc-11d0-8f78-00a0c9110057")]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideOptionPage(typeof(StemCodeOptionsPage), "StemCode", "General", 0, 0, true)]
    public sealed class VSPackage : AsyncPackage
    {
        public const string PackageGuidString = "08b5699b-4adb-4433-9b60-bc4f5a3807df";
        internal static VSPackage? Instance { get; private set; }

        // Command set GUID and IDs, must match StemCodeVSPackage.vsct
        internal static readonly Guid CommandSetGuid = new Guid("0A1B2C3D-4E5F-6789-0ABC-DEF012345678");
        internal const int CmdidShowChatWindow = 0x0100;
        internal const int CmdidNewChat = 0x0101;
        internal const int CmdidSendSelection = 0x0102;
        internal const int CmdidExplainSelection = 0x0103;
        internal const int CmdidSendCurrentFile = 0x0104;
        internal const int CmdidReviewCurrentFile = 0x0105;
        internal const int CmdidReviewGitDiff = 0x0106;
        internal const int CmdidPlanChanges = 0x0107;
        internal const int CmdidApplySuggestedChanges = 0x0108;
        internal const int CmdidShowTerminals = 0x0109;
        internal const int CmdidStart = 0x0110;
        internal const int CmdidStop = 0x0111;
        internal const int CmdidRestart = 0x0112;
        internal const int CmdidOpenLogs = 0x0113;
        internal const int CmdidOpenSettings = 0x0114;
        internal const int CmdidRunSlashCommand = 0x0115;
        internal const int CmdidVoiceDictation = 0x0160;
        internal const int CmdidVoiceSetup = 0x0161;
        internal const int CmdidVoiceUpdate = 0x0162;

        /// <summary>
        /// Menu commands that map one-to-one onto a CLI slash command (same set as the VS Code
        /// extension's DIRECT_SLASH_COMMANDS). IDs must match StemCodeVSPackage.vsct.
        /// </summary>
        internal static readonly IReadOnlyList<(int Id, string SlashCommand)> DirectSlashCommands = new[]
        {
            (0x0120, "/undo"),
            (0x0121, "/redo"),
            (0x0122, "/compact"),
            (0x0123, "/fork"),
            (0x0124, "/clone"),
            (0x0125, "/tree"),
            (0x0126, "/resume"),
            (0x0127, "/export"),
            (0x0128, "/trajectory"),
            (0x0129, "/import"),
            (0x012A, "/share"),
            (0x012B, "/copy"),
            (0x0130, "/models"),
            (0x0131, "/provider"),
            (0x0132, "/onboard"),
            (0x0140, "/init"),
            (0x0141, "/doctor"),
            (0x0142, "/session"),
            (0x0143, "/permissions"),
            (0x0144, "/mcp"),
            (0x0145, "/index status"),
            (0x0146, "/index rebuild"),
            (0x0147, "/lsp"),
            (0x0148, "/reload"),
            (0x0150, "/update"),
            (0x0151, "/version"),
        };

        protected override async Task InitializeAsync(
            CancellationToken cancellationToken,
            IProgress<ServiceProgressData> progress)
        {
            Instance = this;
            await base.InitializeAsync(cancellationToken, progress);
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            // Register the menu command that shows the chat tool window.
            // The VSCT file places this command under View -> Other Windows, View, and Tools.
            if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService menuService)
            {
                var menuCommandId = new CommandID(CommandSetGuid, CmdidShowChatWindow);
                var menuCommand = new OleMenuCommand(
                    ShowChatWindowCallback,
                    menuCommandId)
                {
                    Enabled = true,
                    Visible = true
                };

                menuCommand.BeforeQueryStatus += (_, _) =>
                {
                    menuCommand.Enabled = true;
                    menuCommand.Visible = true;
                };

                menuService.AddCommand(menuCommand);

                // Editor-context / Tools-menu commands.
                Register(menuService, CmdidNewChat, (_, _) => RunOnChatAsync(c => c.NewChatAsync()));
                Register(menuService, CmdidSendSelection, (_, _) => SendEditorContextAsync("Use this selection as context:", selectionOnly: true, review: false));
                Register(menuService, CmdidExplainSelection, (_, _) => SendEditorContextAsync("Explain this selection:", selectionOnly: true, review: false));
                Register(menuService, CmdidSendCurrentFile, (_, _) => SendEditorContextAsync("Use this file as context:", selectionOnly: false, review: false));
                Register(menuService, CmdidReviewCurrentFile, (_, _) => SendEditorContextAsync("Review this file for bugs, regressions, and missing tests:", selectionOnly: false, review: true));
                Register(menuService, CmdidReviewGitDiff, (_, _) => ReviewGitDiffAsync());
                Register(menuService, CmdidPlanChanges, (_, _) => RunOnChatAsync(c => { c.PrefillInput("Plan the following change before editing:\n\n"); return Task.CompletedTask; }));
                Register(menuService, CmdidApplySuggestedChanges, (_, _) => RunOnChatAsync(c => c.SubmitExternalPromptAsync("Apply the suggested changes from the previous StemCode response.")));
                Register(menuService, CmdidShowTerminals, (_, _) => RunOnChatAsync(c => c.SubmitExternalPromptAsync("/terminals")));

                // Process and chat-view commands.
                Register(menuService, CmdidStart, (_, _) => RunOnChatAsync(c => c.StartAgentAsync()));
                Register(menuService, CmdidStop, (_, _) => RunOnChatAsync(c => c.StopAgentAsync()));
                Register(menuService, CmdidRestart, (_, _) => RunOnChatAsync(c => c.RestartAgentAsync()));
                Register(menuService, CmdidOpenLogs, (_, _) => RunOnChatAsync(c => { c.OpenLogs(); return Task.CompletedTask; }));
                Register(menuService, CmdidOpenSettings, (_, _) => RunOnChatAsync(c => { c.ShowSettings(); return Task.CompletedTask; }));
                Register(menuService, CmdidRunSlashCommand, (_, _) => RunOnChatAsync(c => { c.ShowSlashCommandPicker(); return Task.CompletedTask; }));
                Register(menuService, CmdidVoiceDictation, (_, _) => RunOnChatAsync(c => c.RunVoiceDictationAsync()));
                Register(menuService, CmdidVoiceSetup, (_, _) => RunOnChatAsync(c => c.RunVoiceSetupAsync()));
                Register(menuService, CmdidVoiceUpdate, (_, _) => RunOnChatAsync(c => c.RunVoiceUpdateAsync()));

                foreach ((int id, string slashCommand) in DirectSlashCommands)
                {
                    string text = slashCommand;
                    Register(menuService, id, (_, _) => RunOnChatAsync(c => c.SubmitAsync(text)));
                }
            }

            LogService.Instance.Info("StemCode.VS package initialized; commands registered.");
        }

        private static void Register(OleMenuCommandService menuService, int id, EventHandler handler)
            => menuService.AddCommand(new OleMenuCommand(handler, new CommandID(CommandSetGuid, id)));

        private async Task<ChatToolWindowControl?> GetChatControlAsync()
        {
            ToolWindowPane? pane = await ShowToolWindowAsync(typeof(ChatToolWindow), 0, create: true, cancellationToken: DisposalToken);
            return (pane as ChatToolWindow)?.Control;
        }

        private async void RunOnChatAsync(Func<ChatToolWindowControl, Task> action)
        {
            try
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
                ChatToolWindowControl? control = await GetChatControlAsync();
                if (control != null) await action(control);
            }
            catch (Exception ex)
            {
                LogService.Instance.Error("StemCode editor command failed.", ex);
            }
        }

        private async void SendEditorContextAsync(string instruction, bool selectionOnly, bool review)
        {
            try
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);

                if (await GetServiceAsync(typeof(DTE)) is not DTE dte || dte.ActiveDocument is not Document doc)
                {
                    Warn("Open a file in the editor first.");
                    return;
                }

                string fileName = doc.FullName;
                string lang = LanguageFromPath(fileName);
                string text;

                if (selectionOnly)
                {
                    if (doc.Selection is not TextSelection sel || string.IsNullOrWhiteSpace(sel.Text))
                    {
                        Warn("Select code or text before sending it to StemCode.");
                        return;
                    }
                    text = sel.Text;
                }
                else
                {
                    if (doc.Object("TextDocument") is not TextDocument td)
                    {
                        Warn("The active document is not a text file.");
                        return;
                    }
                    text = td.StartPoint.CreateEditPoint().GetText(td.EndPoint);
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        Warn("The active file is empty.");
                        return;
                    }
                }

                string prompt = $"{instruction}\n\nFile: {fileName}\n\n```{lang}\n{text}\n```";
                ChatToolWindowControl? control = await GetChatControlAsync();
                if (control != null) await control.SubmitExternalPromptAsync(prompt);
            }
            catch (Exception ex)
            {
                LogService.Instance.Error("StemCode editor-context command failed.", ex);
            }
        }

        private async void ReviewGitDiffAsync()
        {
            try
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
                string cwd = GetWorkingDirectory();
                string diff = await Task.Run(() => RunGitDiff(cwd));
                if (string.IsNullOrWhiteSpace(diff))
                {
                    Warn("No git diff found for StemCode to review.");
                    return;
                }

                string prompt = $"Review this git diff for bugs, regressions, and missing tests:\n\n```diff\n{diff}\n```";
                ChatToolWindowControl? control = await GetChatControlAsync();
                if (control != null) await control.SubmitExternalPromptAsync(prompt);
            }
            catch (Exception ex)
            {
                LogService.Instance.Error("StemCode git-diff review failed.", ex);
                Warn("Unable to read git diff: " + ex.Message);
            }
        }

        private static string RunGitDiff(string workingDirectory)
        {
            var psi = new ProcessStartInfo("git", "diff --no-ext-diff HEAD")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using Process? p = Process.Start(psi);
            if (p == null) return string.Empty;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(20000);
            return output;
        }

        private string GetWorkingDirectory()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string? configured = GetOptionsPage()?.WorkingDirectory?.Trim();
            if (!string.IsNullOrWhiteSpace(configured)) return configured!;

            if (OpenFolderDirectory() is string folder) return folder;

            if (GetGlobalService(typeof(SVsSolution)) is IVsSolution solution &&
                solution.GetSolutionInfo(out string dir, out _, out _) == 0 &&
                !string.IsNullOrWhiteSpace(dir))
            {
                return dir;
            }
            return Directory.GetCurrentDirectory();
        }

        /// <summary>The folder opened via Open Folder / CMake (no .sln). Null in classic solution mode.</summary>
        internal static string? OpenFolderDirectory()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var cm = GetGlobalService(typeof(SComponentModel)) as IComponentModel;
                string? loc = cm?.GetService<IVsFolderWorkspaceService>()?.CurrentWorkspace?.Location;
                return string.IsNullOrWhiteSpace(loc) ? null : loc;
            }
            catch { return null; }
        }

        private static string LanguageFromPath(string path)
        {
            string ext = Path.GetExtension(path)?.TrimStart('.').ToLowerInvariant() ?? "";
            return ext switch
            {
                "cs" => "csharp",
                "ts" => "typescript",
                "js" => "javascript",
                "py" => "python",
                "rs" => "rust",
                "go" => "go",
                "java" => "java",
                "cpp" or "cc" or "cxx" or "h" or "hpp" => "cpp",
                "xaml" or "xml" => "xml",
                _ => ext
            };
        }

        private void Warn(string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            VsShellUtilities.ShowMessageBox(this, message, "StemCode",
                OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }

        internal bool Confirm(string title, string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            int result = VsShellUtilities.ShowMessageBox(this, message, title,
                OLEMSGICON.OLEMSGICON_QUERY, OLEMSGBUTTON.OLEMSGBUTTON_YESNO, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            return result == 6; // IDYES
        }

        internal void Info(string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Warn(message);
        }

        internal void ShowOptionPageInternal()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ShowOptionPage(typeof(StemCodeOptionsPage));
        }

        private async void ShowChatWindowCallback(object sender, EventArgs e)
        {
            try
            {
                ToolWindowPane? window = await ShowToolWindowAsync(
                    typeof(ChatToolWindow),
                    0,
                    create: true,
                    cancellationToken: DisposalToken);

                if (window is null || window.Frame is null)
                {
                    throw new InvalidOperationException("StemCode Chat tool window could not be created.");
                }
            }
            catch (Exception ex)
            {
                LogService.Instance.Error("Failed to show StemCode Chat tool window.", ex);
                VsShellUtilities.ShowMessageBox(
                    this,
                    @"StemCode Chat failed to open. Details were written to %LOCALAPPDATA%\StemCode\Logs.",
                    "StemCode Chat",
                    OLEMSGICON.OLEMSGICON_CRITICAL,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            }
        }

        public override IVsAsyncToolWindowFactory GetAsyncToolWindowFactory(Guid toolWindowType)
        {
            if (toolWindowType == typeof(ChatToolWindow).GUID)
            {
                return this;
            }

            return base.GetAsyncToolWindowFactory(toolWindowType);
        }

        protected override string GetToolWindowTitle(Type toolWindowType, int id)
        {
            if (toolWindowType == typeof(ChatToolWindow))
            {
                return "StemCode Chat";
            }

            return base.GetToolWindowTitle(toolWindowType, id);
        }

        protected override async Task<object?> InitializeToolWindowAsync(
            Type toolWindowType,
            int id,
            CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            return null;
        }

        internal StemCodeOptionsPage GetOptionsPage()
        {
            return (StemCodeOptionsPage)GetDialogPage(typeof(StemCodeOptionsPage));
        }
    }

    public enum StemCodeLogLevel { Debug, Info, Warn, Error }

    /// <summary>
    /// Tools > Options > StemCode > General. Mirrors the VS Code extension's settings
    /// (stemcode.command, stemcode.args, stemcode.workingDirectory, stemcode.acpAuthenticationToken,
    /// stemcode.autoStart, stemcode.logLevel) plus CLI install and update. Property names are kept
    /// from earlier versions so saved values carry over.
    /// </summary>
    public class StemCodeOptionsPage : DialogPage
    {
        private const string CliCategory = "CLI";
        private const string SessionCategory = "Session";
        private const string UpdatesCategory = "Install and Updates";
        private const string DiagnosticsCategory = "Diagnostics";

        [Category(CliCategory)]
        [DisplayName("Command")]
        [Description("Command name or full path of the StemCode CLI (VS Code: stemcode.command). Leave empty to find 'stemcode' automatically in %LOCALAPPDATA%\\Programs\\StemCode\\bin, on PATH, and in the npm, pnpm, bun and dotnet global tool folders.")]
        [DefaultValue("")]
        public string StemCodeCliPath { get; set; } = "";

        [Category(CliCategory)]
        [DisplayName("Arguments")]
        [Description("Extra arguments for the CLI, separated by spaces (VS Code: stemcode.args). They are added after --acp --surface visual_studio.")]
        [DefaultValue("")]
        public string ExtraArguments { get; set; } = "";

        [Category(SessionCategory)]
        [DisplayName("Working Directory")]
        [Description("Folder StemCode works in (VS Code: stemcode.workingDirectory). Leave empty to use the open folder or the solution directory.")]
        [DefaultValue("")]
        public string WorkingDirectory { get; set; } = "";

        [Category(SessionCategory)]
        [DisplayName("ACP Authentication Token")]
        [Description("Token sent when the ACP server asks for token authentication (VS Code: stemcode.acpAuthenticationToken). If empty, the STEMCODE_ACP_AUTH_TOKEN environment variable is used.")]
        [PasswordPropertyText(true)]
        [DefaultValue("")]
        public string AcpAuthenticationToken { get; set; } = "";

        [Category(SessionCategory)]
        [DisplayName("Auto Start")]
        [Description("Start the StemCode CLI as soon as the chat window opens (VS Code: stemcode.autoStart). When off, it starts with your first message or the Start command.")]
        [DefaultValue(true)]
        public bool AutoStart { get; set; } = true;

        [Category(UpdatesCategory)]
        [DisplayName("Install CLI When Missing")]
        [Description("When the StemCode CLI isn't found, offer to install it with the official PowerShell installer from GitHub (scripts/install.ps1). The CLI is not bundled with the extension.")]
        [DefaultValue(true)]
        public bool AutoInstall { get; set; } = true;

        [Category(UpdatesCategory)]
        [DisplayName("Check for CLI Updates")]
        [Description("Check GitHub for a newer StemCode CLI release, at most once every 24 hours, and offer to install it.")]
        [DefaultValue(true)]
        public bool CheckForUpdates { get; set; } = true;

        [Category(DiagnosticsCategory)]
        [DisplayName("Log Level")]
        [Description("How much the extension writes to its log under %LOCALAPPDATA%\\StemCode\\Logs (VS Code: stemcode.logLevel).")]
        [DefaultValue(StemCodeLogLevel.Info)]
        public StemCodeLogLevel LogLevel { get; set; } = StemCodeLogLevel.Info;
    }
}
