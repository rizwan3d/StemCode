using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StemCode.Application.Abstractions;
using StemCode.Sdk.Internal;
using StemCode.Application.Commands;
using StemCode.Application.Models;
using StemCode.Application.Services;
using StemCode.Application.Telemetry;
using StemCode.Application.UI;
using StemCode.Application.Tools;
using StemCode.Application.Tools.Models;
using StemCode.Domain.Models;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace StemCode.Application.Backend;

public sealed class StemCodeBackend : IStemCodeBackend
{
    private readonly BackendRuntimeArguments _runtimeArguments;
    private readonly bool _autoApproveAllTools;
    private readonly IReadOnlyList<BackendMcpServerConfiguration> _sessionMcpServers;
    private readonly Action<IServiceCollection>? _configureServices;
    private AgentTurnService? _agentTurnService;
    private IAgentProfileResolver? _profileResolver;
    private IHost? _host;
    private IProviderSetupService? _providerSetupService;
    private IAutoCommitService? _autoCommitService;
    private IShellCommandService? _shellCommandService;
    private IReplCommandDispatcher? _commandDispatcher;
    private IReplCommandParser? _commandParser;
    private IInteractiveModelSelectionService? _modelSelectionService;
    private ReplSessionContext? _session;
    private ISessionAppService? _sessionAppService;
    private ISessionEventLogService? _sessionEventLogService;
    private IApplicationUpdateService? _updateService;
    private IConfirmationPrompt? _confirmationPrompt;
    private IStatusMessageWriter? _statusMessageWriter;
    private IProductTelemetry? _telemetry;
    private IWindowsSandboxStartupService? _windowsSandboxStartupService;
    private bool _updatePromptShown;
    private int _editListStartIndex;

    public StemCodeBackend(string[] args)
        : this(
            BackendRuntimeArguments.Parse(args),
            [],
            autoApproveAllTools: false)
    {
    }

    public StemCodeBackend(
        string[] args,
        IReadOnlyList<BackendMcpServerConfiguration>? sessionMcpServers)
        : this(
            BackendRuntimeArguments.Parse(args),
            sessionMcpServers,
            autoApproveAllTools: false)
    {
    }

    public StemCodeBackend(
        string[] args,
        IReadOnlyList<BackendMcpServerConfiguration>? sessionMcpServers,
        bool autoApproveAllTools)
        : this(
            BackendRuntimeArguments.Parse(args),
            sessionMcpServers,
            autoApproveAllTools)
    {
    }

    // Pins the workspace root to an explicit directory instead of the process
    // current directory, so concurrent sessions in different workspaces don't
    // depend on (or clobber) global cwd. Mirrors StemCodeClientBuilder.WithWorkspace.
    public StemCodeBackend(
        string[] args,
        IReadOnlyList<BackendMcpServerConfiguration>? sessionMcpServers,
        bool autoApproveAllTools,
        string? workspaceRoot)
        : this(
            BackendRuntimeArguments.Parse(args),
            sessionMcpServers,
            autoApproveAllTools,
            CreateFixedWorkspaceRootConfiguration(workspaceRoot))
    {
    }

    private static Action<IServiceCollection>? CreateFixedWorkspaceRootConfiguration(string? workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return null;
        }

        string root = workspaceRoot.Trim();
        return services => services.AddSingleton<IWorkspaceRootProvider>(
            new FixedWorkspaceRootProvider(root));
    }

    internal StemCodeBackend(
        BackendRuntimeArguments runtimeArguments,
        IReadOnlyList<BackendMcpServerConfiguration>? sessionMcpServers,
        bool autoApproveAllTools,
        Action<IServiceCollection>? configureServices = null)
    {
        _runtimeArguments = runtimeArguments ?? throw new ArgumentNullException(nameof(runtimeArguments));
        _sessionMcpServers = sessionMcpServers ?? [];
        _autoApproveAllTools = autoApproveAllTools;
        _configureServices = configureServices;
    }

    public async Task<BackendSessionInfo> InitializeAsync(
        IUiBridge uiBridge,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uiBridge);

        if (_session is not null)
        {
            return CreateSessionInfo(_session, _profileResolver);
        }

        BackendRuntimeArguments options = _runtimeArguments;

        _host = StemCodeHostFactory.Create(
            uiBridge,
            options,
            _sessionMcpServers,
            _autoApproveAllTools,
            _configureServices);
        _providerSetupService = _host.Services.GetRequiredService<IProviderSetupService>();
        _autoCommitService = _host.Services.GetRequiredService<IAutoCommitService>();
        _shellCommandService = _host.Services.GetRequiredService<IShellCommandService>();
        _sessionAppService = _host.Services.GetRequiredService<ISessionAppService>();
        _sessionEventLogService = _host.Services.GetRequiredService<ISessionEventLogService>();
        _agentTurnService = _host.Services.GetRequiredService<AgentTurnService>();
        _profileResolver = _host.Services.GetRequiredService<IAgentProfileResolver>();
        _commandParser = _host.Services.GetRequiredService<IReplCommandParser>();
        _commandDispatcher = _host.Services.GetRequiredService<IReplCommandDispatcher>();
        _modelSelectionService = _host.Services.GetRequiredService<IInteractiveModelSelectionService>();
        _updateService = _host.Services.GetRequiredService<IApplicationUpdateService>();
        _confirmationPrompt = _host.Services.GetRequiredService<IConfirmationPrompt>();
        _statusMessageWriter = _host.Services.GetRequiredService<IStatusMessageWriter>();
        _telemetry = _host.Services.GetRequiredService<IProductTelemetry>();
        _windowsSandboxStartupService = _host.Services.GetRequiredService<IWindowsSandboxStartupService>();
        _telemetry.TrackAppStarted();
        await _windowsSandboxStartupService.EnsureReadyAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(options.SectionId))
        {
            _session = await _sessionAppService.ResumeAsync(
                new ResumeSessionRequest(
                    options.SectionId,
                    options.ProfileName,
                    ReasoningEffortOverride: null,
                    ThinkingModeOverride: options.ThinkingMode),
                cancellationToken);

            await _providerSetupService.EnsureOnboardedAsync(cancellationToken);
        }
        else
        {
            ProviderSetupResult startupResult = await _providerSetupService.EnsureConfiguredAsync(cancellationToken);
            OnboardingResult onboardingResult = startupResult.OnboardingResult;
            ModelDiscoveryResult modelResult = startupResult.ModelDiscoveryResult;

            _session = await _sessionAppService.CreateAsync(
                new CreateSessionRequest(
                    onboardingResult.Profile,
                    modelResult.SelectedModelId,
                    modelResult.AvailableModels.Select(static model => model.Id).ToArray(),
                    options.ProfileName,
                    onboardingResult.ReasoningEffort,
                    options.ThinkingMode ?? onboardingResult.ThinkingMode,
                    CreateModelContextWindowMap(modelResult.AvailableModels),
                    CreateModelContextMetadataMap(modelResult.AvailableModels),
                    onboardingResult.ActiveProviderName),
                cancellationToken);
        }

        ApplyRuntimeContextWindowOverride(_session);

        await PromptForUpdateIfAvailableAsync(options.SkipUpdateCheck, cancellationToken);
        _editListStartIndex = _session.GetRecordedEditCount();

        return CreateSessionInfo(_session, _profileResolver);
    }

    public async Task<BackendCommandResult> RunCommandAsync(
        string commandText,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandText);

        if (_session is null ||
            _sessionAppService is null ||
            _commandParser is null ||
            _commandDispatcher is null)
        {
            throw new InvalidOperationException("StemCode backend has not been initialized.");
        }

        ParsedReplCommand command = _commandParser.Parse(commandText);
        string featureName = TelemetryFeatureNames.ForCommand(command.CommandName);
        ReplCommandResult result;
        try
        {
            result = await _commandDispatcher.DispatchAsync(
                command,
                _session,
                cancellationToken);
        }
        catch (Exception exception)
        {
            _telemetry?.TrackFeatureUsed(
                featureName,
                "command",
                success: false,
                exception: exception);
            throw;
        }

        _telemetry?.TrackFeatureUsed(
            featureName,
            "command",
            success: result.FeedbackKind != ReplFeedbackKind.Error);

        if (result.SessionOverride is not null &&
            !ReferenceEquals(result.SessionOverride, _session))
        {
            await _sessionAppService.SaveIfDirtyAsync(_session, cancellationToken);
            _session = result.SessionOverride;
            ApplyRuntimeContextWindowOverride(_session);
            _editListStartIndex = _session.GetRecordedEditCount();
        }

        await _sessionAppService.SaveIfDirtyAsync(_session, cancellationToken);

        return new BackendCommandResult(
            result,
            CreateSessionInfo(_session, _profileResolver));
    }

    public async Task<BackendCommandResult> SelectModelAsync(
        CancellationToken cancellationToken)
    {
        if (_session is null ||
            _sessionAppService is null ||
            _modelSelectionService is null)
        {
            throw new InvalidOperationException("StemCode backend has not been initialized.");
        }

        ReplCommandResult result = await _modelSelectionService.SelectAsync(
            _session,
            cancellationToken);

        await _sessionAppService.SaveIfDirtyAsync(_session, cancellationToken);

        return new BackendCommandResult(
            result,
            CreateSessionInfo(_session, _profileResolver));
    }

    public async Task<ConversationTurnResult> RunTurnAsync(
        string input,
        IUiBridge uiBridge,
        CancellationToken cancellationToken)
    {
        return await RunTurnAsync(
            input,
            [],
            uiBridge,
            cancellationToken);
    }

    public async Task<ConversationTurnResult> RunTurnAsync(
        string input,
        IReadOnlyList<ConversationAttachment> attachments,
        IUiBridge uiBridge,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        ArgumentNullException.ThrowIfNull(attachments);
        ArgumentNullException.ThrowIfNull(uiBridge);

        if (_session is null ||
            _sessionAppService is null ||
            _agentTurnService is null)
        {
            throw new InvalidOperationException("StemCode backend has not been initialized.");
        }

        _sessionAppService.EnsureTitleGenerationStarted(_session, input);
        await RecordUserInputAsync(_session, input, cancellationToken);

        bool isDirectShellCommand = TryParseDirectShellCommand(input, out string? directShellCommand);
        string? directShellWorkingDirectory = null;
        if (isDirectShellCommand)
        {
            try
            {
                directShellWorkingDirectory = _session.ResolvePathFromWorkingDirectory(null);
            }
            catch (InvalidOperationException)
            {
                directShellWorkingDirectory = null;
            }
        }

        ConversationTurnResult result;
        try
        {
            result = await _agentTurnService.RunTurnAsync(
                new AgentTurnRequest(
                    _session,
                    input,
                    CreateProgressSink(_session, uiBridge),
                    attachments),
                cancellationToken);
        }
        catch (Exception exception)
        {
            await RecordTurnFailureAsync(_session, input, exception, cancellationToken);
            throw;
        }

        ConversationTurnMetrics? metrics = result.Metrics;
        if (!string.IsNullOrWhiteSpace(result.ResponseText) && metrics is not null)
        {
            int sessionTotal = _session.AddEstimatedOutputTokens(metrics.EstimatedOutputTokens);
            metrics = metrics.WithSessionEstimatedOutputTokens(sessionTotal);
        }

        if (isDirectShellCommand &&
            !string.IsNullOrWhiteSpace(directShellCommand) &&
            result.ToolExecutionResult is not null)
        {
            await RecordDirectShellEventsAsync(
                _session,
                directShellCommand!,
                directShellWorkingDirectory,
                result.ToolExecutionResult,
                cancellationToken);
        }

        if (result.Kind == ConversationTurnResultKind.AssistantMessage)
        {
            await RecordAssistantOutputAsync(_session, result.ResponseText, cancellationToken);
        }

        await _sessionAppService.SaveIfDirtyAsync(_session, cancellationToken);

        return new ConversationTurnResult(
            result.Kind,
            result.ResponseText,
            result.ToolExecutionResult,
            metrics,
            result.ReasoningText);
    }

    public Task<IReadOnlyList<BackgroundTerminalInfo>> ListBackgroundTerminalsAsync(
        CancellationToken cancellationToken)
    {
        if (_session is null || _shellCommandService is null)
        {
            throw new InvalidOperationException("StemCode backend has not been initialized.");
        }

        return _shellCommandService.ListBackgroundAsync(_session.SessionId, cancellationToken);
    }

    public Task<ShellCommandExecutionResult> ReadBackgroundTerminalAsync(
        string terminalId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(terminalId);

        if (_session is null || _shellCommandService is null)
        {
            throw new InvalidOperationException("StemCode backend has not been initialized.");
        }

        return _shellCommandService.ReadBackgroundAsync(
            terminalId.Trim(),
            _session.SessionId,
            cancellationToken);
    }

    public IReadOnlyList<FileEditSummary> GetFileEditSummary()
    {
        ReplSessionContext? session = _session;
        return session is null
            ? []
            : SessionEditSummary.Build(
                session.GetEditsSince(_editListStartIndex),
                path => StemCode.Application.Utilities.WorkspacePath.Resolve(
                    session.WorkspacePath,
                    session.ResolvePathFromWorkingDirectory(path)));
    }

    public async ValueTask DisposeAsync()
    {
        _telemetry?.TrackAppStopped();

        try
        {
            if (_sessionAppService is not null && _session is not null)
            {
                try
                {
                    if (_autoCommitService is not null)
                    {
                        try
                        {
                            await _autoCommitService.TryAutoCommitAsync(
                                _session,
                                _session.GetEditsSince(_editListStartIndex),
                                CancellationToken.None);
                        }
                        catch
                        {
                        }
                    }
                }
                finally
                {
                    await _sessionAppService.StopAsync(_session, CancellationToken.None);
                }
            }
        }
        finally
        {
            if (_host is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else
            {
                _host?.Dispose();
            }
        }
    }

    private static BackendSessionInfo CreateSessionInfo(
        ReplSessionContext session,
        IAgentProfileResolver? profileResolver)
    {
        return new BackendSessionInfo(
            session.SessionId,
            session.SessionResumeCommand,
            session.ProviderName,
            session.ActiveModelId,
            session.ActiveModelContextWindowTokens,
            session.AvailableModelIds,
            session.ThinkingMode,
            session.ReasoningEffort,
            session.ShowThinking,
            session.AgentProfileName,
            session.SectionTitle,
            session.IsResumedSection,
            BackendConversationHistoryFormatter.Create(session))
        {
            AvailableAgentProfiles = CreateAgentProfileInfos(session, profileResolver),
            SessionContentText = session.CreateStatefulContextPrompt(),
            TotalEstimatedOutputTokens = session.TotalEstimatedOutputTokens,
            SectionEstimatedContextTokens = EstimateSectionContextTokens(session)
        };
    }

    private static IReadOnlyList<BackendAgentProfileInfo> CreateAgentProfileInfos(
        ReplSessionContext session,
        IAgentProfileResolver? profileResolver)
    {
        IReadOnlyList<IAgentProfile> profiles = profileResolver?.List() ?? [session.AgentProfile];
        return profiles
            .Select(static profile => new BackendAgentProfileInfo(
                profile.Name,
                profile.Mode.ToString().ToLowerInvariant(),
                profile.Description))
            .ToArray();
    }

    private static int EstimateSectionContextTokens(ReplSessionContext session)
    {
        long characters = 0;
        foreach (ConversationSectionTurn turn in session.ConversationTurns)
        {
            characters += turn.UserInput.Length;
            characters += turn.AssistantResponse.Length;
            characters += turn.ToolOutputMessages.Sum(static message => message.Length);
            characters += turn.ToolCalls.Sum(static toolCall => toolCall.ArgumentsJson.Length);
        }

        long estimatedTokens = (characters + 3) / 4;
        return estimatedTokens > int.MaxValue
            ? int.MaxValue
            : Math.Max(session.TotalEstimatedOutputTokens, (int)estimatedTokens);
    }

    private void ApplyRuntimeContextWindowOverride(ReplSessionContext session)
    {
        if (_runtimeArguments.ContextWindowTokens is > 0)
        {
            session.SetContextWindowOverride(_runtimeArguments.ContextWindowTokens);
        }
    }

    private static IReadOnlyDictionary<string, int> CreateModelContextWindowMap(
        IEnumerable<AvailableModel> models)
    {
        Dictionary<string, int> contextWindowTokens = new(StringComparer.Ordinal);
        foreach (AvailableModel model in models)
        {
            if (string.IsNullOrWhiteSpace(model.Id) ||
                model.ContextWindowTokens is not > 0)
            {
                continue;
            }

            contextWindowTokens[model.Id.Trim()] = model.ContextWindowTokens.Value;
        }

        return contextWindowTokens;
    }

    private static IReadOnlyDictionary<string, ModelContextMetadata> CreateModelContextMetadataMap(
        IEnumerable<AvailableModel> models)
    {
        Dictionary<string, ModelContextMetadata> metadata = new(StringComparer.Ordinal);
        foreach (AvailableModel model in models)
        {
            if (string.IsNullOrWhiteSpace(model.Id))
            {
                continue;
            }

            string modelId = model.Id.Trim();
            ModelContextMetadata? modelMetadata = model.ContextMetadata;
            if (modelMetadata is null)
            {
                if (model.ContextWindowTokens is not > 0)
                {
                    continue;
                }

                modelMetadata = new ModelContextMetadata(model.ContextWindowTokens.Value);
            }

            metadata[modelId] = modelMetadata;
        }

        return metadata;
    }

    private async Task PromptForUpdateIfAvailableAsync(
        bool skipUpdateCheck,
        CancellationToken cancellationToken)
    {
        if (skipUpdateCheck ||
            _updatePromptShown ||
            _updateService is null ||
            _confirmationPrompt is null ||
            _statusMessageWriter is null)
        {
            return;
        }

        _updatePromptShown = true;

        ApplicationUpdateInfo updateInfo;
        try
        {
            updateInfo = await _updateService.CheckAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException)
        {
            return;
        }

        if (!updateInfo.IsUpdateAvailable)
        {
            return;
        }

        bool shouldUpdate = await _confirmationPrompt.PromptAsync(
            new ConfirmationPromptRequest(
                "StemCode update available. Update now?",
                $"Current: {updateInfo.CurrentVersion}. Latest: {updateInfo.LatestVersion}. Choose Yes to update now, or No to skip.",
                DefaultValue: false),
            cancellationToken);

        if (!shouldUpdate)
        {
            await _statusMessageWriter.ShowInfoAsync(
                $"Skipped StemCode {updateInfo.LatestVersion}.",
                cancellationToken);
            return;
        }

        await _statusMessageWriter.ShowInfoAsync(
            $"Installing StemCode {updateInfo.LatestVersion}...",
            cancellationToken);

        ApplicationUpdateInstallResult installResult;
        try
        {
            installResult = await _updateService.InstallAsync(
                updateInfo,
                new StatusMessageProgress(_statusMessageWriter),
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            HttpRequestException or
            PlatformNotSupportedException)
        {
            await _statusMessageWriter.ShowErrorAsync(exception.Message, cancellationToken);
            return;
        }

        if (installResult.IsSuccess)
        {
            await _statusMessageWriter.ShowSuccessAsync(installResult.Message, cancellationToken);
            return;
        }

        await _statusMessageWriter.ShowErrorAsync(installResult.Message, cancellationToken);
    }

    private IConversationProgressSink CreateProgressSink(
        ReplSessionContext session,
        IUiBridge uiBridge)
    {
        IConversationProgressSink innerSink = new UiConversationProgressSink(uiBridge);
        return _sessionEventLogService is null
            ? innerSink
            : new PersistingConversationProgressSink(
                innerSink,
                _sessionEventLogService,
                session);
    }

    private Task RecordUserInputAsync(
        ReplSessionContext session,
        string input,
        CancellationToken cancellationToken)
    {
        return _sessionEventLogService is null
            ? Task.CompletedTask
            : _sessionEventLogService.RecordUserInputAsync(
                session,
                input,
                cancellationToken);
    }

    private Task RecordAssistantOutputAsync(
        ReplSessionContext session,
        string output,
        CancellationToken cancellationToken)
    {
        return _sessionEventLogService is null
            ? Task.CompletedTask
            : _sessionEventLogService.RecordAssistantOutputAsync(
                session,
                output,
                cancellationToken);
    }

    private Task RecordTurnFailureAsync(
        ReplSessionContext session,
        string input,
        Exception exception,
        CancellationToken cancellationToken)
    {
        return _sessionEventLogService is null
            ? Task.CompletedTask
            : _sessionEventLogService.RecordTurnFailureAsync(
                session,
                input,
                exception,
                cancellationToken);
    }

    private async Task RecordDirectShellEventsAsync(
        ReplSessionContext session,
        string command,
        string? workingDirectory,
        ToolExecutionBatchResult toolExecutionResult,
        CancellationToken cancellationToken)
    {
        if (_sessionEventLogService is null ||
            toolExecutionResult.Results.Count == 0)
        {
            return;
        }

        ToolInvocationResult invocationResult = toolExecutionResult.Results[0];
        string argumentsJson = SerializeDirectShellArguments(
            command,
            workingDirectory ?? ".",
            "require_escalated",
            "User-entered direct shell command.");

        await _sessionEventLogService.RecordToolCallRequestedAsync(
            session,
            new ConversationToolCall(
                invocationResult.ToolCallId,
                AgentToolNames.ShellCommand,
                argumentsJson),
            cancellationToken);
        await _sessionEventLogService.RecordToolResultAsync(
            session,
            invocationResult,
            cancellationToken);
    }

    private static string SerializeDirectShellArguments(
        string command,
        string workingDirectory,
        string sandboxPermissions,
        string justification)
    {
        return
            "{" +
            $"\"command\":\"{EscapeJson(command)}\"," +
            $"\"workingDirectory\":\"{EscapeJson(workingDirectory)}\"," +
            $"\"sandbox_permissions\":\"{EscapeJson(sandboxPermissions)}\"," +
            $"\"justification\":\"{EscapeJson(justification)}\"" +
            "}";
    }

    private static bool TryParseDirectShellCommand(
       string input,
       out string? command)
   {
       command = null;
       string trimmedInput = input.Trim();
       if (!trimmedInput.StartsWith('!'))
       {
           return false;
       }

       command = trimmedInput[1..].Trim();
       return true;
   }

    private static string EscapeJson(string value)
    {
        return JsonEncodedText.Encode(value ?? string.Empty, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString();
    }
}
