using StemCode.Application.Abstractions;
using StemCode.Application.Commands;
using StemCode.Application.Models;
using StemCode.Application.Telemetry;
using StemCode.Application.Tools;
using StemCode.Application.Tools.Models;
using StemCode.Application.Tools.Serialization;
using StemCode.Application.Utilities;

namespace StemCode.Application.Services;

internal sealed class AgentTurnService
{
    private const string DirectShellPrefix = "!";
    private const string DirectShellBackgroundPrefix = "!!";

    private readonly IConversationPipeline _conversationPipeline;
    private readonly IAgentProfileResolver _profileResolver;
    private readonly IProductTelemetry _telemetry;
    private readonly IShellCommandService? _shellCommandService;
    private readonly IMcpPromptService? _mcpPromptService;

    public AgentTurnService(
        IConversationPipeline conversationPipeline,
        IAgentProfileResolver profileResolver,
        IProductTelemetry? telemetry = null,
        IShellCommandService? shellCommandService = null,
        IMcpPromptService? mcpPromptService = null)
    {
        _conversationPipeline = conversationPipeline;
        _profileResolver = profileResolver;
        _telemetry = telemetry ?? NoOpProductTelemetry.Instance;
        _shellCommandService = shellCommandService;
        _mcpPromptService = mcpPromptService;
    }

    public AgentTurnService(
        IConversationPipeline conversationPipeline,
        IAgentProfileResolver profileResolver,
        IShellCommandService? shellCommandService)
        : this(
            conversationPipeline,
            profileResolver,
            telemetry: null,
            shellCommandService)
    {
    }

    public async Task<ConversationTurnResult> RunTurnAsync(
        AgentTurnRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        int attachmentCount = request.Attachments.Count;
        string featureName = attachmentCount > 0
            ? TelemetryFeatureNames.PromptWithAttachments
            : TelemetryFeatureNames.Prompt;

        try
        {
            ConversationTurnResult result;
            McpPromptExpansion? mcpPrompt = _mcpPromptService is not null && McpPromptCommand.IsCandidate(request.UserInput)
                ? await _mcpPromptService.TryExpandAsync(request.UserInput, cancellationToken)
                : null;

            if (mcpPrompt is not null)
            {
                featureName = TelemetryFeatureNames.McpPrompt;
                result = mcpPrompt.Prompt is null
                    ? ConversationTurnResult.AssistantMessage(mcpPrompt.Error ?? "MCP prompt could not be expanded.")
                    : await ProcessWithOptionalAttachmentsAsync(
                        mcpPrompt.Prompt,
                        request,
                        cancellationToken);
            }
            else if (CustomSlashCommandService.TryExpand(
                    request.Session.WorkspacePath,
                    request.UserInput,
                    out CustomSlashCommandResolution? customCommand,
                    out string? customCommandError))
            {
                featureName = TelemetryFeatureNames.CustomCommand;
                result = customCommand is null
                    ? ConversationTurnResult.AssistantMessage(customCommandError ?? "Custom command could not be expanded.")
                    : await ProcessWithOptionalAttachmentsAsync(
                        customCommand.ExpandedPrompt,
                        request,
                        cancellationToken);
            }
            else if (TryParseDirectShellBackgroundCommand(request.UserInput, out string? bgCommand))
            {
                featureName = TelemetryFeatureNames.DirectShellBackground;
                result = await RunDirectShellBackgroundCommandAsync(
                    ShellCommandText.NormalizeCommandText(bgCommand),
                    request,
                    cancellationToken);
            }
            else if (TryParseDirectShellCommand(request.UserInput, out string? command))
            {
                featureName = TelemetryFeatureNames.DirectShell;
                result = await RunDirectShellCommandAsync(
                    ShellCommandText.NormalizeCommandText(command),
                    request,
                    cancellationToken);
            }
            else if (!TryParseLeadingAgentMention(
                         request.UserInput,
                         out string? agentName,
                         out string? delegatedInput))
            {
                result = await ProcessWithOptionalAttachmentsAsync(
                    request.UserInput,
                    request,
                    cancellationToken);
            }
            else
            {
                featureName = TelemetryFeatureNames.AgentMention;

                IAgentProfile mentionedProfile;
                try
                {
                    mentionedProfile = _profileResolver.Resolve(agentName);
                }
                catch (ArgumentException)
                {
                    result = ConversationTurnResult.AssistantMessage(
                        $"Unknown agent '@{agentName}'. Available subagents: {FormatProfileNames(_profileResolver.List().Where(static profile => profile.Mode == AgentProfileMode.Subagent))}.");
                    _telemetry.TrackFeatureUsed(featureName, "turn", success: true, result.Metrics, attachmentCount);
                    return result;
                }

                if (mentionedProfile.Mode != AgentProfileMode.Subagent)
                {
                    result = ConversationTurnResult.AssistantMessage(
                        $"Agent '@{mentionedProfile.Name}' is a primary profile. Use /profile {mentionedProfile.Name} to switch primary profiles.");
                    _telemetry.TrackFeatureUsed(featureName, "turn", success: true, result.Metrics, attachmentCount);
                    return result;
                }

                if (string.IsNullOrWhiteSpace(delegatedInput))
                {
                    result = ConversationTurnResult.AssistantMessage(
                        $"Tell '@{mentionedProfile.Name}' what to do, for example: @{mentionedProfile.Name} inspect the authentication flow.");
                    _telemetry.TrackFeatureUsed(featureName, "turn", success: true, result.Metrics, attachmentCount);
                    return result;
                }

                IAgentProfile originalProfile = request.Session.AgentProfile;
                request.Session.SetAgentProfile(mentionedProfile);

                try
                {
                    result = await ProcessWithOptionalAttachmentsAsync(
                        delegatedInput,
                        request,
                        cancellationToken);
                }
                finally
                {
                    request.Session.SetAgentProfile(originalProfile);
                }
            }

            _telemetry.TrackFeatureUsed(
                featureName,
                "turn",
                success: true,
                result.Metrics,
                attachmentCount);
            return result;
        }
        catch (Exception exception)
        {
            _telemetry.TrackFeatureUsed(
                featureName,
                "turn",
                success: false,
                attachmentCount: attachmentCount,
                exception: exception);
            throw;
        }
    }

    private Task<ConversationTurnResult> ProcessWithOptionalAttachmentsAsync(
        string input,
        AgentTurnRequest request,
        CancellationToken cancellationToken)
    {
        return request.Attachments.Count == 0
            ? _conversationPipeline.ProcessAsync(
                input,
                request.Session,
                request.ProgressSink,
                cancellationToken)
            : _conversationPipeline.ProcessAsync(
                input,
                request.Session,
                request.ProgressSink,
                request.Attachments,
                cancellationToken);
    }

    private async Task<ConversationTurnResult> RunDirectShellCommandAsync(
        string command,
        AgentTurnRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return ConversationTurnResult.AssistantMessage(
                "Enter a shell command after !.");
        }

        if (_shellCommandService is null)
        {
            return ConversationTurnResult.AssistantMessage(
                "Direct shell commands are unavailable in this session.");
        }

        string effectiveWorkingDirectory;
        try
        {
            effectiveWorkingDirectory = request.Session.ResolvePathFromWorkingDirectory(null);
        }
        catch (InvalidOperationException exception)
        {
            return ConversationTurnResult.AssistantMessage(exception.Message);
        }

        ShellCommandExecutionResult result = await _shellCommandService.ExecuteAsync(
            new ShellCommandExecutionRequest(
                command,
                effectiveWorkingDirectory,
                ShellCommandSandboxPermissions.RequireEscalated,
                Justification: "User-entered direct shell command."),
            cancellationToken);
        SessionStateToolRecorder.RecordShellCommand(request.Session, result);

        string? sessionDirectoryUpdate = UpdateSessionWorkingDirectoryAfterCd(
            request.Session,
            command,
            effectiveWorkingDirectory,
            result.ExitCode);
        ToolExecutionBatchResult batchResult = CreateDirectShellBatchResult(
            result,
            request.Session.WorkingDirectory,
            sessionDirectoryUpdate);
        return ConversationTurnResult.ToolExecution(batchResult);
    }

    private static ToolExecutionBatchResult CreateDirectShellBatchResult(
        ShellCommandExecutionResult result,
        string sessionWorkingDirectory,
        string? sessionDirectoryUpdate)
    {
        string sessionDirectoryLine = string.IsNullOrWhiteSpace(sessionDirectoryUpdate)
            ? string.Empty
            : sessionDirectoryUpdate + Environment.NewLine;
        string renderText =
            $"Working directory: {result.WorkingDirectory}{Environment.NewLine}" +
            $"Session working directory: {sessionWorkingDirectory}{Environment.NewLine}" +
            sessionDirectoryLine +
            $"Exit code: {result.ExitCode}{Environment.NewLine}" +
            $"STDOUT:{Environment.NewLine}{SuspiciousUnicodeText.RenderVisible(result.StandardOutput)}{Environment.NewLine}{Environment.NewLine}" +
            $"STDERR:{Environment.NewLine}{SuspiciousUnicodeText.RenderVisible(result.StandardError)}";
        string displayCommand = SuspiciousUnicodeText.RenderVisible(result.Command);
        string message = $"Ran shell command '{displayCommand}' with exit code {result.ExitCode}.";
        if (!string.IsNullOrWhiteSpace(sessionDirectoryUpdate))
        {
            message += " " + sessionDirectoryUpdate;
        }

        ToolResult toolResult = ToolResultFactory.Success(
            message,
            result,
            ToolJsonContext.Default.ShellCommandExecutionResult,
            new ToolRenderPayload(
                $"Shell command: {displayCommand}",
                renderText));

        return new ToolExecutionBatchResult(
            [
                new ToolInvocationResult(
                    "direct-shell-" + Guid.NewGuid().ToString("N"),
                    AgentToolNames.ShellCommand,
                    toolResult)
            ]);
    }

    private static string? UpdateSessionWorkingDirectoryAfterCd(
        ReplSessionContext session,
        string command,
        string commandWorkingDirectory,
        int exitCode)
    {
        if (exitCode != 0 ||
            !TryGetCdTarget(command, out string? targetPath))
        {
            return null;
        }

        return session.TrySetWorkingDirectory(targetPath!, commandWorkingDirectory, out string? error)
            ? $"Session working directory is now '{session.WorkingDirectory}'."
            : $"Session working directory stayed '{session.WorkingDirectory}': {error}";
    }

    private static bool TryGetCdTarget(
        string command,
        out string? targetPath)
    {
        targetPath = null;

        IReadOnlyList<ShellCommandSegment> segments = ShellCommandText.ParseSegments(command);
        if (segments.Count != 1 ||
            segments[0].Condition != ShellCommandSegmentCondition.Always)
        {
            return false;
        }

        string[] tokens = ShellCommandText.Tokenize(segments[0].CommandText);
        if (tokens.Length < 2)
        {
            return false;
        }

        string commandName = ShellCommandText.NormalizeCommandToken(tokens[0]);
        if (!string.Equals(commandName, "cd", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (tokens.Length == 2)
        {
            targetPath = tokens[1];
            return true;
        }

        if (tokens.Length == 3 &&
            string.Equals(tokens[1], "/d", StringComparison.OrdinalIgnoreCase))
        {
            targetPath = tokens[2];
            return true;
        }

        return false;
    }

    private async Task<ConversationTurnResult> RunDirectShellBackgroundCommandAsync(
        string command,
        AgentTurnRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return ConversationTurnResult.AssistantMessage(
                "Enter a shell command after !!.");
        }

        if (_shellCommandService is null)
        {
            return ConversationTurnResult.AssistantMessage(
                "Direct shell commands are unavailable in this session.");
        }

        string effectiveWorkingDirectory;
        try
        {
            effectiveWorkingDirectory = request.Session.ResolvePathFromWorkingDirectory(null);
        }
        catch (InvalidOperationException exception)
        {
            return ConversationTurnResult.AssistantMessage(exception.Message);
        }

       ShellCommandExecutionResult startResult = await _shellCommandService.StartBackgroundAsync(
            new ShellCommandExecutionRequest(
                command,
                effectiveWorkingDirectory,
                ShellCommandSandboxPermissions.RequireEscalated,
                Justification: "User-entered direct shell background command.",
                SessionId: request.Session.SessionId),
            cancellationToken);
       SessionStateToolRecorder.RecordShellCommand(request.Session, startResult);

        // If the background terminal failed to start, return the failure result immediately
        if (string.Equals(startResult.TerminalStatus, "failed", StringComparison.Ordinal) ||
            string.Equals(startResult.TerminalStatus, "not_found", StringComparison.Ordinal))
        {
            ToolExecutionBatchResult failureBatchResult = CreateDirectShellBackgroundBatchResult(
                startResult,
                request.Session.WorkingDirectory);
            return ConversationTurnResult.ToolExecution(failureBatchResult);
        }

        string? terminalId = startResult.TerminalId;
        if (string.IsNullOrWhiteSpace(terminalId))
        {
            // No terminal ID to poll; just return the start result as-is
            ToolExecutionBatchResult emptyBatchResult = CreateDirectShellBackgroundBatchResult(
                startResult,
                request.Session.WorkingDirectory);
            return ConversationTurnResult.ToolExecution(emptyBatchResult);
        }

        // Poll the background terminal until it completes, streaming new output
        // to the user as it arrives. Each ReadBackgroundAsync returns only the
        // output produced since the previous read, so the read result already
        // carries the incremental chunk for this iteration.
        string accumulatedStdout = string.Empty;
        string accumulatedStderr = string.Empty;
        ShellCommandExecutionResult? finalResult = null;
        bool detached = false;

        // Header streamed before each chunk of live output, to label which
        // background terminal and command the streamed text belongs to. It is
        // display-only and intentionally excluded from the accumulated output
        // that feeds the consolidated tool result.
        string streamHeader = $"{terminalId} - {command}{Environment.NewLine}";

        try
        {
            while (true)
            {
                await Task.Delay(200, cancellationToken);

                ShellCommandExecutionResult readResult = await _shellCommandService.ReadBackgroundAsync(
                    terminalId,
                    request.Session.SessionId,
                    cancellationToken);
                SessionStateToolRecorder.RecordShellCommand(request.Session, readResult);

                // Stream and accumulate any new output from this poll iteration.
                if (!string.IsNullOrEmpty(readResult.StandardOutput))
                {
                    accumulatedStdout += readResult.StandardOutput;
                    await request.ProgressSink.ReportAssistantMessageChunkAsync(
                        streamHeader + readResult.StandardOutput,
                        cancellationToken);
                }

                if (!string.IsNullOrEmpty(readResult.StandardError))
                {
                    accumulatedStderr += readResult.StandardError;
                    await request.ProgressSink.ReportAssistantMessageChunkAsync(
                        streamHeader + readResult.StandardError,
                        cancellationToken);
                }

                if (!string.Equals(readResult.TerminalStatus, "running", StringComparison.Ordinal))
                {
                    finalResult = readResult;
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Esc cancels streaming, not the terminal: detach and leave it running.
            detached = true;
        }

        if (detached)
        {
            string detachNote =
                $"{Environment.NewLine}[Detached — background terminal {terminalId} is still running. " +
                $"Re-attach with /terminals view {terminalId}]";
            await request.ProgressSink.ReportAssistantMessageChunkAsync(
                detachNote,
                CancellationToken.None);
            return ConversationTurnResult.AssistantMessage(detachNote);
        }

        // Build a consolidated result with the accumulated output from all reads
        ShellCommandExecutionResult consolidatedResult = finalResult! with
        {
            StandardOutput = accumulatedStdout,
            StandardError = accumulatedStderr,
        };

        ToolExecutionBatchResult batchResult = CreateDirectShellBackgroundBatchResult(
            consolidatedResult,
            request.Session.WorkingDirectory);
        return ConversationTurnResult.ToolExecution(batchResult);
    }

    private static ToolExecutionBatchResult CreateDirectShellBackgroundBatchResult(
        ShellCommandExecutionResult result,
        string sessionWorkingDirectory)
    {
        string displayCommand = SuspiciousUnicodeText.RenderVisible(result.Command);
        string renderText =
            $"Working directory: {result.WorkingDirectory}{Environment.NewLine}" +
            $"Session working directory: {sessionWorkingDirectory}{Environment.NewLine}" +
            $"Background terminal: {result.TerminalId}{Environment.NewLine}" +
            $"Terminal status: {result.TerminalStatus} (exit code {result.ExitCode}){Environment.NewLine}" +
            $"STDOUT:{Environment.NewLine}{SuspiciousUnicodeText.RenderVisible(result.StandardOutput)}{Environment.NewLine}{Environment.NewLine}" +
            $"STDERR:{Environment.NewLine}{SuspiciousUnicodeText.RenderVisible(result.StandardError)}";
        string message = $"Ran shell command '{displayCommand}' in background with exit code {result.ExitCode}.";

        ToolResult toolResult = ToolResultFactory.Success(
            message,
            result,
            ToolJsonContext.Default.ShellCommandExecutionResult,
            new ToolRenderPayload(
                $"Shell command (background): {displayCommand}",
                renderText));

        return new ToolExecutionBatchResult(
        [
            new ToolInvocationResult(
                "direct-shell-bg-" + Guid.NewGuid().ToString("N"),
                AgentToolNames.ShellCommand,
                toolResult)
        ]);
    }

    private static bool TryParseDirectShellBackgroundCommand(
        string input,
        out string command)
    {
        string trimmedInput = input.Trim();
        if (!trimmedInput.StartsWith(DirectShellBackgroundPrefix, StringComparison.Ordinal))
        {
            command = string.Empty;
            return false;
        }

        command = trimmedInput[DirectShellBackgroundPrefix.Length..].Trim();
        return true;
    }

    private static bool TryParseDirectShellCommand(
        string input,
        out string command)
    {
        string trimmedInput = input.Trim();
        if (!trimmedInput.StartsWith(DirectShellPrefix, StringComparison.Ordinal))
        {
            command = string.Empty;
            return false;
        }

        command = trimmedInput[DirectShellPrefix.Length..].Trim();
        return true;
    }

    private static bool TryParseLeadingAgentMention(
        string input,
        out string agentName,
        out string delegatedInput)
    {
        agentName = string.Empty;
        delegatedInput = string.Empty;

        string trimmedInput = input.Trim();
        if (!trimmedInput.StartsWith('@'))
        {
            return false;
        }

        int index = 1;
        while (index < trimmedInput.Length && IsAgentNameCharacter(trimmedInput[index]))
        {
            index++;
        }

        if (index == 1)
        {
            return false;
        }

        agentName = trimmedInput[1..index];
        delegatedInput = trimmedInput[index..].Trim();
        return true;
    }

    private static bool IsAgentNameCharacter(char value)
    {
        return char.IsLetterOrDigit(value) ||
               value is '-' or '_';
    }

    private static string FormatProfileNames(IEnumerable<IAgentProfile> profiles)
    {
        return string.Join(
            ", ",
            profiles.Select(static profile => profile.Name));
    }

    private sealed class NoOpProductTelemetry : IProductTelemetry
    {
        public static NoOpProductTelemetry Instance { get; } = new();

        public void TrackAppStarted()
        {
        }

        public void TrackAppStopped()
        {
        }

        public void TrackFeatureUsed(
            string featureName,
            string interactionKind,
            bool success,
            ConversationTurnMetrics? metrics = null,
            int attachmentCount = 0,
            Exception? exception = null)
        {
        }

        public void TrackToolInvoked(
            string toolName,
            ToolResultStatus status,
            bool success,
            TimeSpan duration,
            ConversationExecutionPhase executionPhase,
            string? modelId = null,
            string? providerName = null,
            string? errorMessage = null)
        {
        }

        public void TrackProviderRequest(
            string providerName,
            bool success,
            TimeSpan latency,
            bool streamed,
            TimeSpan streamLatency,
            int retryCount,
            string? errorMessage = null)
        {
        }
    }
}
