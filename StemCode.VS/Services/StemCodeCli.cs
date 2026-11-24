using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace StemCode.VS.Services
{
    /// <summary>
    /// Finds the StemCode CLI on this machine and installs or updates it. The extension never
    /// ships the CLI: when it is missing, the official PowerShell installer is downloaded from
    /// GitHub and run, which puts stemcode.exe in %LOCALAPPDATA%\Programs\StemCode\bin.
    /// GUI-launched VS doesn't inherit the shell PATH, so that folder and the npm / pnpm / bun /
    /// dotnet-tool global bin dirs are searched directly.
    /// </summary>
    internal static class StemCodeCli
    {
        private static readonly bool IsWindows = Path.DirectorySeparatorChar == '\\';

        private const string GitHubOwner = "rizwan3d";
        private const string GitHubRepo = "StemCode";

        /// <summary>The installer the README documents: <c>irm .../install.ps1 | iex</c>.</summary>
        public const string InstallScriptUrl =
            "https://raw.githubusercontent.com/" + GitHubOwner + "/" + GitHubRepo + "/master/scripts/install.ps1";

        private const string LatestReleaseApiUrl =
            "https://api.github.com/repos/" + GitHubOwner + "/" + GitHubRepo + "/releases/latest";

        /// <summary>Where install.ps1 puts stemcode.exe (its default install directory).</summary>
        public static string InstallDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "StemCode", "bin");

        /// <summary>stemcode.exe as install.ps1 lays it out.</summary>
        public static string InstalledExecutablePath => Path.Combine(InstallDirectory, "stemcode.exe");

        private static string[] CommandNames => IsWindows
            ? new[] { "stemcode.cmd", "stemcode.exe", "stemcode" }
            : new[] { "stemcode" };

        /// <summary>Returns an absolute path to stemcode if found in a known dir, else the bare command.</summary>
        public static string ResolveCommand(string command, LogService log)
        {
            if (command != "stemcode" && command != "stemcode.exe"
                || command.IndexOf('/') >= 0 || command.IndexOf('\\') >= 0)
            {
                return command; // explicit path/command: trust it.
            }

            foreach (string dir in GlobalSearchDirs())
            {
                foreach (string name in CommandNames)
                {
                    string candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate))
                    {
                        log.Info($"Resolved stemcode binary: {candidate}");
                        return candidate;
                    }
                }
            }

            return "stemcode"; // fall back to PATH lookup.
        }

        /// <summary>True if the command is an absolute file or bare "stemcode" is on PATH.</summary>
        public static bool IsAvailable(string command)
        {
            if (command != "stemcode") return File.Exists(command) || OnPath(command);
            return OnPath("stemcode");
        }

        private static bool OnPath(string name)
        {
            try
            {
                var psi = new ProcessStartInfo(IsWindows ? "where" : "which", name)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using Process? p = Process.Start(psi);
                if (p == null) return false;
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit(5000);
                return p.ExitCode == 0;
            }
            catch { return false; }
        }

        public static IEnumerable<string> GlobalSearchDirs()
        {
            var dirs = new List<string>();
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            void Add(string? d) { if (!string.IsNullOrWhiteSpace(d) && !dirs.Contains(d!)) dirs.Add(d!); }

            if (IsWindows) Add(InstallDirectory); // install.ps1 target; checked first so it wins.
            Add(Environment.GetEnvironmentVariable("PNPM_HOME"));
            string? bun = Environment.GetEnvironmentVariable("BUN_INSTALL");
            if (!string.IsNullOrEmpty(bun)) Add(Path.Combine(bun!, "bin"));
            string? appData = Environment.GetEnvironmentVariable("APPDATA");
            if (!string.IsNullOrEmpty(appData)) Add(Path.Combine(appData!, "npm"));
            string? localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (!string.IsNullOrEmpty(localAppData)) Add(Path.Combine(localAppData!, "pnpm"));
            Add(Path.Combine(home, ".bun", "bin"));
            Add(Path.Combine(home, ".dotnet", "tools"));          // dotnet tool install -g
            Add(Path.Combine(home, "Library", "pnpm"));
            Add(Path.Combine(home, ".local", "share", "pnpm"));
            Add(Path.Combine(home, ".npm-global", "bin"));
            Add("/usr/local/bin");
            Add("/opt/homebrew/bin");

            string? npmPrefix = NpmGlobalPrefix();
            if (npmPrefix != null) Add(IsWindows ? npmPrefix : Path.Combine(npmPrefix, "bin"));

            return dirs;
        }

        private static string? NpmGlobalPrefix()
        {
            try
            {
                string output = RunCaptured(IsWindows ? "cmd.exe" : "npm",
                    IsWindows ? "/c npm prefix -g" : "prefix -g", out int code);
                return code == 0 && !string.IsNullOrWhiteSpace(output) ? output.Trim() : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Downloads install.ps1 from GitHub and runs it in Windows PowerShell, streaming its
        /// output to the log. Installs the latest release, or replaces an older one in place, so
        /// any CLI running from the install folder must be stopped first. Returns true on success.
        /// </summary>
        public static Task<bool> InstallFromGitHubAsync(LogService log, CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                if (!IsWindows)
                {
                    log.Error("The StemCode installer for Visual Studio needs Windows.");
                    return false;
                }

                // TLS 1.2 for Windows PowerShell 5.1 on older .NET defaults, then run the script as a
                // script block so its param() block and $ErrorActionPreference behave as in a file.
                string script =
                    "$ErrorActionPreference = 'Stop'; " +
                    "[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12; " +
                    "$installer = Invoke-RestMethod -UseBasicParsing -Uri '" + InstallScriptUrl + "'; " +
                    "& ([scriptblock]::Create($installer))";

                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = PowerShellPath(),
                        Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " +
                                    Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                    psi.EnvironmentVariables["STEMCODE_NO_PROGRESS"] = "1";

                    log.Info($"Installing StemCode CLI with {InstallScriptUrl}");
                    using Process? p = Process.Start(psi);
                    if (p == null) return false;
                    p.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log.Info($"install: {e.Data}"); };
                    p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log.Warn($"install: {e.Data}"); };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();

                    using (cancellationToken.Register(() => { try { if (!p.HasExited) p.Kill(); } catch { } }))
                    {
                        if (!p.WaitForExit(10 * 60 * 1000))
                        {
                            log.Error("The StemCode installer timed out after 10 minutes.");
                            try { p.Kill(); } catch { }
                            return false;
                        }
                    }
                    p.WaitForExit(); // flush redirected output
                    log.Info($"StemCode installer exited with code {p.ExitCode}.");
                    return p.ExitCode == 0 && File.Exists(InstalledExecutablePath);
                }
                catch (Exception ex)
                {
                    log.Error("The StemCode installer failed to start.", ex);
                    return false;
                }
            }, cancellationToken);
        }

        private static string PowerShellPath()
        {
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string inbox = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
            return File.Exists(inbox) ? inbox : "powershell.exe";
        }

        /// <summary>Returns (current, latest) versions, or nulls when undetermined.</summary>
        public static (string? Current, string? Latest) CheckVersions(string command, LogService log)
        {
            try
            {
                string current = ParseVersion(RunCaptured(command, "--version", out _));
                string latest = ParseVersion(LatestReleaseTag(log));
                return (string.IsNullOrEmpty(current) ? null : current,
                        string.IsNullOrEmpty(latest) ? null : latest);
            }
            catch (Exception ex)
            {
                log.Debug("Update check skipped: " + ex.Message);
                return (null, null);
            }
        }

        /// <summary>The tag of the latest GitHub release, e.g. "v1.4.0"; null when unreachable.</summary>
        private static string? LatestReleaseTag(LogService log)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(LatestReleaseApiUrl);
                request.UserAgent = "StemCode-VisualStudio";
                request.Accept = "application/vnd.github+json";
                request.Timeout = 15000;
                using var response = (HttpWebResponse)request.GetResponse();
                using var reader = new StreamReader(response.GetResponseStream()!);
                using JsonDocument doc = JsonDocument.Parse(reader.ReadToEnd());
                return doc.RootElement.TryGetProperty("tag_name", out JsonElement tag) ? tag.GetString() : null;
            }
            catch (Exception ex)
            {
                log.Debug("Latest-release lookup failed: " + ex.Message);
                return null;
            }
        }

        public static bool IsNewer(string latest, string current)
        {
            int[] a = ParseTriple(latest), b = ParseTriple(current);
            for (int i = 0; i < 3; i++) if (a[i] != b[i]) return a[i] > b[i];
            return false;
        }

        private static int[] ParseTriple(string v)
        {
            string[] parts = (v ?? "0.0.0").Split('.');
            var n = new int[3];
            for (int i = 0; i < 3 && i < parts.Length; i++) int.TryParse(parts[i], out n[i]);
            return n;
        }

        private static string ParseVersion(string? text)
        {
            Match m = Regex.Match(text ?? string.Empty, @"(\d+)\.(\d+)\.(\d+)");
            return m.Success ? m.Value : string.Empty;
        }

        private static string RunCaptured(string fileName, string arguments, out int exitCode)
        {
            exitCode = -1;
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using Process? p = Process.Start(psi);
            if (p == null) return string.Empty;
            string output = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            if (p.HasExited) exitCode = p.ExitCode;
            return output;
        }

        // 24h update-check throttle (no Memento in VS; persisted under LocalAppData).

        private static string CheckStampPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StemCode", "last-update-check");

        public static bool ShouldCheckForUpdate()
        {
            try
            {
                if (!File.Exists(CheckStampPath)) return true;
                string text = File.ReadAllText(CheckStampPath).Trim();
                return !long.TryParse(text, out long last)
                       || (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - last) > 24L * 60 * 60 * 1000;
            }
            catch { return true; }
        }

        public static void RecordUpdateCheck()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CheckStampPath)!);
                File.WriteAllText(CheckStampPath, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
            }
            catch { /* best effort */ }
        }
    }
}
