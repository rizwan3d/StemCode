using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StemCode.CLI;

/// <summary>
/// Runs the user's <c>Application:Terminal:StatusLine:Command</c> and keeps the first
/// line it prints, which replaces the built-in status line. The command gets the
/// session details as JSON on standard input, runs in the background, and is
/// re-run when those details change or the refresh interval passes.
/// </summary>
internal sealed partial class CustomStatusLine
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(3);

    private readonly string _command;
    private readonly TimeSpan _refreshInterval;
    private readonly object _gate = new();
    private string? _lastInput;
    private DateTimeOffset _lastRun = DateTimeOffset.MinValue;
    private bool _running;

    public CustomStatusLine(string command, int refreshSeconds)
    {
        _command = command;
        _refreshInterval = TimeSpan.FromSeconds(Math.Max(1, refreshSeconds));
    }

    /// <summary>The latest output line, or null before the first run or when the command fails.</summary>
    public string? Text { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Starts a background refresh if the input changed or the interval elapsed.</summary>
    public void RefreshIfStale(string inputJson)
    {
        lock (_gate)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (_running ||
                (string.Equals(inputJson, _lastInput, StringComparison.Ordinal) && now - _lastRun < _refreshInterval))
            {
                return;
            }

            _running = true;
            _lastInput = inputJson;
            _lastRun = now;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                (Text, LastError) = await RunAsync(_command, inputJson);
            }
            finally
            {
                lock (_gate)
                {
                    _running = false;
                }
            }
        });
    }

    internal static string BuildInput(
        string? sessionId,
        string cwd,
        string? modelId,
        string? providerName,
        string? profileName,
        string? thinkingMode,
        string? reasoningEffort,
        int? contextWindowTokens,
        string version)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("session_id", sessionId);
            writer.WriteString("cwd", cwd);
            writer.WriteStartObject("workspace");
            writer.WriteString("current_dir", cwd);
            writer.WriteEndObject();
            writer.WriteStartObject("model");
            writer.WriteString("id", modelId);
            writer.WriteString("display_name", modelId);
            writer.WriteEndObject();
            writer.WriteString("provider", providerName);
            writer.WriteString("profile", profileName);
            writer.WriteString("thinking", thinkingMode);
            writer.WriteString("reasoning_effort", reasoningEffort);
            if (contextWindowTokens is int tokens)
            {
                writer.WriteNumber("context_window_tokens", tokens);
            }

            writer.WriteString("version", version);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static async Task<(string? Text, string? Error)> RunAsync(string command, string inputJson)
    {
        ProcessStartInfo startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/d", "/s", "/c", command } }
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", command } };
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.StandardOutputEncoding = Encoding.UTF8;

        try
        {
            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The status line command did not start.");
            await process.StandardInput.WriteAsync(inputJson);
            process.StandardInput.Close();

            using CancellationTokenSource timeout = new(CommandTimeout);
            Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return (null, "status line command timed out");
            }

            string firstLine = (await output)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? string.Empty;
            string clean = AnsiEscape().Replace(firstLine, string.Empty);
            return (clean.Length == 0 ? null : clean, null);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (null, exception.Message);
        }
    }

    [GeneratedRegex(@"\x1B\[[0-9;?]*[A-Za-z]")]
    private static partial Regex AnsiEscape();
}
