using StemCode.Application.Abstractions;
using StemCode.Application.Exceptions;
using StemCode.Application.Models;
using StemCode.Application.Utilities;
using StemCode.Infrastructure.Secrets;
using System.ComponentModel;
using System.Globalization;

namespace StemCode.Application.Commands;

internal sealed class ExportCommandHandler : IReplCommandHandler
{
    private const string HtmlFormat = "html";
    private const string JsonFormat = "json";
    private const string TrajectoryFormat = "trajectory";
    private readonly ISelectionPrompt _selectionPrompt;
    private readonly ISessionEventLogService _sessionEventLogService;

    public ExportCommandHandler(
        ISelectionPrompt selectionPrompt,
        ISessionEventLogService sessionEventLogService)
    {
        _selectionPrompt = selectionPrompt;
        _sessionEventLogService = sessionEventLogService;
    }

    public string CommandName => "export";

    public string Description => "Export the current session as JSON, HTML, or trajectory HTML.";

    public string Usage => "/export [json|html|trajectory] [path]";

    public async Task<ReplCommandResult> ExecuteAsync(
        ReplCommandContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        string format;
        string? requestedPath = null;
        if (context.Arguments.Count == 0)
        {
            try
            {
                format = await _selectionPrompt.PromptAsync(
                    new SelectionPromptRequest<string>(
                        "Export session",
                        [
                            new SelectionPromptOption<string>(
                                "JSON",
                                "json",
                                "Portable session backup that can be imported later."),
                            new SelectionPromptOption<string>(
                                "HTML",
                                HtmlFormat,
                                "Readable transcript for sharing or review."),
                            new SelectionPromptOption<string>(
                                "Trajectory HTML",
                                TrajectoryFormat,
                                "Full event stream with reasoning, tool calls, tool results, and output.")
                        ],
                        "Esc cancels export.",
                        DefaultIndex: 0,
                        AllowCancellation: true),
                    cancellationToken);
            }
            catch (PromptCancelledException)
            {
                return ReplCommandResult.Continue("Export cancelled.", ReplFeedbackKind.Warning);
            }
        }
        else
        {
            string firstArgument = context.Arguments[0].Trim();
            format = NormalizeFormat(firstArgument);
            if (format is JsonFormat or HtmlFormat or TrajectoryFormat)
            {
                if (context.ArgumentText.Length > firstArgument.Length)
                {
                    requestedPath = context.ArgumentText[firstArgument.Length..].Trim();
                }
            }
            else
            {
                requestedPath = context.ArgumentText;
                format = NormalizeFormat(Path.GetExtension(requestedPath).TrimStart('.'));
            }
        }

        if (format is not (JsonFormat or HtmlFormat or TrajectoryFormat))
        {
            return ReplCommandResult.Continue(
                "Usage: /export [json|html|trajectory] [path]",
                ReplFeedbackKind.Error);
        }

        string extension = format == JsonFormat
            ? "json"
            : format == TrajectoryFormat
                ? "trajectory.html"
                : "html";
        string filePath = string.IsNullOrWhiteSpace(requestedPath)
            ? SessionCommandSupport.CreateDefaultExportPath(context.Session, extension)
            : SessionCommandSupport.ResolvePath(requestedPath);
        ConversationSectionSnapshot snapshot = SessionCommandSupport.CreateSnapshot(
            context.Session,
            DateTimeOffset.UtcNow);

        if (format == JsonFormat)
        {
            await SessionCommandSupport.ExportJsonAsync(snapshot, filePath, cancellationToken);
        }
        else if (format == TrajectoryFormat)
        {
            string eventLogPath = _sessionEventLogService.GetStoragePath(context.Session.SectionId);
            await SessionCommandSupport.ExportTrajectoryHtmlAsync(
                snapshot,
                eventLogPath,
                filePath,
                cancellationToken);
        }
        else
        {
            await SessionCommandSupport.ExportHtmlAsync(snapshot, filePath, cancellationToken);
        }

        return ReplCommandResult.Continue(
            $"Exported session as {format.ToUpperInvariant()}:\n{filePath}");
    }

    private static string NormalizeFormat(string value)
    {
        string normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "traj" or "trajectory" or "trajecotry" => TrajectoryFormat,
            _ => normalized
        };
    }
}

internal sealed class ImportCommandHandler : IReplCommandHandler
{
    private readonly IConversationSectionStore _sectionStore;
    private readonly ISessionAppService _sessionAppService;
    private readonly ITextPrompt _textPrompt;

    public ImportCommandHandler(
        IConversationSectionStore sectionStore,
        ISessionAppService sessionAppService,
        ITextPrompt textPrompt)
    {
        _sectionStore = sectionStore;
        _sessionAppService = sessionAppService;
        _textPrompt = textPrompt;
    }

    public string CommandName => "import";

    public string Description => "Import a session from JSON and switch to the imported copy.";

    public string Usage => "/import <json-path>";

    public async Task<ReplCommandResult> ExecuteAsync(
        ReplCommandContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        string path = context.ArgumentText.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            try
            {
                path = await _textPrompt.PromptAsync(
                    new TextPromptRequest(
                        "Import JSON path",
                        "Enter the path to a session JSON export. Esc cancels import.",
                        AllowCancellation: true),
                    cancellationToken);
            }
            catch (PromptCancelledException)
            {
                return ReplCommandResult.Continue("Import cancelled.", ReplFeedbackKind.Warning);
            }
        }

        string filePath = SessionCommandSupport.ResolvePath(path);
        if (!string.Equals(Path.GetExtension(filePath), ".json", StringComparison.OrdinalIgnoreCase))
        {
            return ReplCommandResult.Continue(
                "Import only accepts JSON exports. Usage: /import <json-path>",
                ReplFeedbackKind.Error);
        }

        if (!File.Exists(filePath))
        {
            return ReplCommandResult.Continue(
                $"Import file was not found:\n{filePath}",
                ReplFeedbackKind.Error);
        }

        ConversationSectionSnapshot? imported = await SessionCommandSupport.LoadJsonAsync(
            filePath,
            cancellationToken);
        if (imported is null)
        {
            return ReplCommandResult.Continue(
                "Import failed because the file is not a valid session JSON export.",
                ReplFeedbackKind.Error);
        }

        ConversationSectionSnapshot snapshot = SessionCommandSupport.CreateImportedSnapshot(
            imported,
            context.Session);
        ReplSessionContext importedSession = await SessionCommandSupport.SaveAndResumeAsync(
            snapshot,
            _sectionStore,
            _sessionAppService,
            cancellationToken);

        return ReplCommandResult.SwitchSession(
            importedSession,
            $"Imported session from JSON.\nSession: {importedSession.SessionId}");
    }
}

internal sealed class ShareCommandHandler : IReplCommandHandler
{
    private readonly IProcessRunner _processRunner;

    public ShareCommandHandler(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public string CommandName => "share";

    public string Description => "Share the current session as a secret GitHub gist.";

    public string Usage => "/share";

    public async Task<ReplCommandResult> ExecuteAsync(
        ReplCommandContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        string directory = Path.Combine(Path.GetTempPath(), "stemcode-session-share-" + Guid.NewGuid().ToString("N"));
        string fileName = $"stemcode-session-{context.Session.SectionId[..8]}.json";
        string filePath = Path.Combine(directory, fileName);

        try
        {
            Directory.CreateDirectory(directory);
            ConversationSectionSnapshot snapshot = SessionCommandSupport.CreateSnapshot(
                context.Session,
                DateTimeOffset.UtcNow);
            await SessionCommandSupport.ExportJsonAsync(snapshot, filePath, cancellationToken);

            ProcessExecutionResult result = await _processRunner.RunAsync(
                new ProcessExecutionRequest(
                    "gh",
                    [
                        "gist",
                        "create",
                        "--secret",
                        "--desc",
                        $"StemCode session {context.Session.SectionId[..8]}",
                        filePath
                    ],
                    WorkingDirectory: Directory.GetCurrentDirectory(),
                    MaxOutputCharacters: 4000),
                cancellationToken);

            if (result.ExitCode != 0)
            {
                string details = string.IsNullOrWhiteSpace(result.StandardError)
                    ? result.StandardOutput
                    : result.StandardError;
                return ReplCommandResult.Continue(
                    "GitHub gist share failed.\n" + details.Trim(),
                    ReplFeedbackKind.Error);
            }

            string url = result.StandardOutput.Trim();
            return ReplCommandResult.Continue(
                string.IsNullOrWhiteSpace(url)
                    ? "Secret GitHub gist created."
                    : $"Secret GitHub gist created:\n{url}");
        }
        catch (Win32Exception)
        {
            return ReplCommandResult.Continue(
                "GitHub CLI (gh) is required for /share. Sign in with gh auth login, then run /share again.",
                ReplFeedbackKind.Error);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed class CopyCommandHandler : IReplCommandHandler
{
    private readonly IProcessRunner _processRunner;

    public CopyCommandHandler(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public string CommandName => "copy";

    public string Description => "Copy the last agent message to the clipboard.";

    public string Usage => "/copy";

    public async Task<ReplCommandResult> ExecuteAsync(
        ReplCommandContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        string? lastMessage = context.Session.ConversationTurns.LastOrDefault()?.AssistantResponse;
        if (string.IsNullOrWhiteSpace(lastMessage))
        {
            return ReplCommandResult.Continue(
                "No agent message is available to copy yet.",
                ReplFeedbackKind.Warning);
        }

        bool copied = await TryCopyAsync(lastMessage, cancellationToken);
        return copied
            ? ReplCommandResult.Continue("Copied the last agent message to the clipboard.")
            : ReplCommandResult.Continue(
                "Clipboard copy failed. Install clip, pbcopy, wl-copy, xclip, or xsel and try again.",
                ReplFeedbackKind.Error);
    }

    private async Task<bool> TryCopyAsync(string text, CancellationToken cancellationToken)
    {
        ClipboardCommand[] commands = OperatingSystem.IsWindows()
            ? [new ClipboardCommand("clip.exe", [])]
            : OperatingSystem.IsMacOS()
                ? [new ClipboardCommand("pbcopy", [])]
                : [
                    new ClipboardCommand("wl-copy", []),
                    new ClipboardCommand("xclip", ["-selection", "clipboard"]),
                    new ClipboardCommand("xsel", ["--clipboard", "--input"])
                ];

        foreach (ClipboardCommand command in commands)
        {
            try
            {
                ProcessExecutionResult result = await _processRunner.RunAsync(
                    new ProcessExecutionRequest(
                        command.FileName,
                        command.Arguments,
                        StandardInput: text,
                        WorkingDirectory: Directory.GetCurrentDirectory(),
                        MaxOutputCharacters: 1000),
                    cancellationToken);
                if (result.ExitCode == 0)
                {
                    return true;
                }
            }
            catch (Win32Exception)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        return false;
    }

    private sealed record ClipboardCommand(
        string FileName,
        IReadOnlyList<string> Arguments);
}

internal sealed class SessionInfoCommandHandler : IReplCommandHandler
{
    public string CommandName => "session";

    public string Description => "Show session info and stats.";

    public string Usage => "/session";

    public Task<ReplCommandResult> ExecuteAsync(
        ReplCommandContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        ReplSessionContext session = context.Session;
        int turnCount = session.ConversationTurns.Count;
        int toolCallCount = session.ConversationTurns.Sum(static turn => turn.ToolCalls.Count);
        int toolOutputCount = session.ConversationTurns.Sum(static turn => turn.ToolOutputMessages.Count);
        int messageCount = turnCount * 2 + toolOutputCount;

        string message =
            "Session info:\n" +
            $"Title: {session.SectionTitle}\n" +
            $"Session: {session.SessionId}\n" +
            $"Resume command: {session.SessionResumeCommand}\n" +
            $"Provider: {session.ProviderName}\n" +
            $"Model: {session.ActiveModelId.ToDisplayNameWithProvider(session.ProviderName)}\n" +
            $"Profile: {session.AgentProfile.Name}\n" +
            $"Thinking: {ThinkingModeOptions.Format(session.ThinkingMode)}\n" +
            $"Reasoning effort: {ReasoningEffortOptions.Format(session.ReasoningEffort)}\n" +
            $"Turns: {turnCount.ToString(CultureInfo.InvariantCulture)}\n" +
            $"Messages: {messageCount.ToString(CultureInfo.InvariantCulture)}\n" +
            $"Tool calls: {toolCallCount.ToString(CultureInfo.InvariantCulture)}\n" +
            $"Estimated output tokens: {session.TotalEstimatedOutputTokens.ToString(CultureInfo.InvariantCulture)}\n" +
            $"Created: {SessionCommandSupport.FormatTimestamp(session.SectionCreatedAtUtc)}\n" +
            $"Updated: {SessionCommandSupport.FormatTimestamp(session.SectionUpdatedAtUtc)}\n" +
            $"Workspace: {session.WorkspacePath}";

        return Task.FromResult(ReplCommandResult.Continue(message));
    }
}
