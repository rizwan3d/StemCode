using StemCode.Application.Abstractions;
using StemCode.Application.Formatting;
using StemCode.Application.Profiles;
using StemCode.Application.Utilities;
using StemCode.Domain.Models;
using System.Globalization;
using System.Text;

namespace StemCode.Application.Models;

public sealed class ReplSessionContext
{
    private const string DefaultApplicationName = "StemCode";
    private const int MaxFileContextEntries = 40;
    private const int MaxEditContextEntries = 40;
    private const int MaxTerminalHistoryEntries = 40;
    private const int MaxStateTextCharacters = 1_500;
    private const int MaxPromptFileContextEntries = 16;
    private const int MaxPromptEditContextEntries = 12;
    private const int MaxPromptTerminalHistoryEntries = 12;
    private const int MaxPromptFieldCharacters = 600;
    private const int MaxCompactedHistoryTurns = 8;
    private const int MaxCompactedHistoryCharacters = 4_000;
    private const int MaxCompactedHistoryFieldCharacters = 500;
    private readonly object _syncRoot = new();
    public const string DefaultSectionTitle = "Untitled section";
    private readonly HashSet<string> _availableModelIds;
    private Dictionary<string, int> _modelContextWindowTokens;
    private Dictionary<string, ModelContextMetadata> _modelContextMetadata;
    private int? _contextWindowOverrideTokens;
    private List<WorkspaceFileEditTransaction>? _batchedFileEditTransactions;
    private readonly List<ConversationRequestMessage> _conversationHistory = [];
    private readonly List<ConversationSectionTurn> _conversationTurns = [];
    private readonly List<SessionEditContext> _editContexts = [];
    private readonly List<SessionEditContext> _recordedEdits = [];
    private readonly List<SessionFileContext> _fileContexts = [];
    private readonly List<WorkspaceFileEditTransaction> _expiredFileEditTransactions = [];
    private readonly Stack<WorkspaceFileEditTransaction> _redoFileEditTransactions = new();
    private readonly List<SessionTerminalCommand> _terminalHistory = [];
    private readonly List<TemporaryArtifactReference> _temporaryArtifacts = [];
    private int _totalRecordedEditCount;
    private bool _sectionTitleGenerationStarted;
    private readonly Stack<WorkspaceFileEditTransaction> _undoFileEditTransactions = new();
    private readonly List<PermissionRule> _permissionOverrides = [];
    private SessionContext _sessionContext = SessionContext.Empty;
    private ReasoningOptions _reasoningOptions = ReasoningOptions.Create();

    public ReplSessionContext(
        AgentProviderProfile providerProfile,
        string activeModelId,
        IReadOnlyList<string> availableModelIds,
        IAgentProfile? agentProfile = null,
        string? reasoningEffort = null,
        string? thinkingMode = null,
        string? workspacePath = null,
        IReadOnlyDictionary<string, int>? modelContextWindowTokens = null,
        IReadOnlyDictionary<string, ModelContextMetadata>? modelContextMetadata = null,
        string? activeProviderName = null,
        string? parentSessionId = null,
        SessionContext? sessionContext = null)
        : this(
            DefaultApplicationName,
            providerProfile,
            activeModelId,
            availableModelIds,
            agentProfile: agentProfile,
            reasoningEffort: reasoningEffort,
            thinkingMode: thinkingMode,
            workspacePath: workspacePath,
            modelContextWindowTokens: modelContextWindowTokens,
            modelContextMetadata: modelContextMetadata,
            activeProviderName: activeProviderName,
            parentSessionId: parentSessionId,
            sessionContext: sessionContext)
    {
    }

    public ReplSessionContext(
        string applicationName,
        AgentProviderProfile providerProfile,
        string activeModelId,
        IReadOnlyList<string> availableModelIds,
        string? sectionId = null,
        string? sectionTitle = null,
        DateTimeOffset? sectionCreatedAtUtc = null,
        DateTimeOffset? sectionUpdatedAtUtc = null,
        int totalEstimatedOutputTokens = 0,
        IReadOnlyList<ConversationSectionTurn>? conversationTurns = null,
        PendingExecutionPlan? pendingExecutionPlan = null,
        bool isResumedSection = false,
        IAgentProfile? agentProfile = null,
        string? reasoningEffort = null,
        string? thinkingMode = null,
        SessionStateSnapshot? sessionState = null,
        string? workspacePath = null,
        IReadOnlyDictionary<string, int>? modelContextWindowTokens = null,
        IReadOnlyDictionary<string, ModelContextMetadata>? modelContextMetadata = null,
        string? activeProviderName = null,
        string? parentSessionId = null,
        SessionContext? sessionContext = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);
        ArgumentNullException.ThrowIfNull(providerProfile);
        ArgumentException.ThrowIfNullOrWhiteSpace(activeModelId);
        ArgumentNullException.ThrowIfNull(availableModelIds);

        if (totalEstimatedOutputTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalEstimatedOutputTokens));
        }

        ApplicationName = applicationName.Trim();
        AgentProfile = agentProfile ?? BuiltInAgentProfiles.Build;
        ToolOutputDisplay.ProfileFullToolOutput = AgentProfile.FullToolOutput;
        ActiveProviderName = NormalizeProviderName(activeProviderName);
        ProviderProfile = providerProfile;
        AvailableModelIds = NormalizeAvailableModelIds(availableModelIds);
        _modelContextMetadata = NormalizeModelContextMetadata(
            modelContextMetadata,
            modelContextWindowTokens,
            AvailableModelIds);
        _modelContextWindowTokens = CreateModelContextWindowTokens(_modelContextMetadata);

        if (AvailableModelIds.Count == 0)
        {
            throw new ArgumentException(
                "At least one available model must be provided.",
                nameof(availableModelIds));
        }

        _availableModelIds = new HashSet<string>(AvailableModelIds, StringComparer.Ordinal);

        string normalizedActiveModelId = activeModelId.Trim();
        if (!_availableModelIds.Contains(normalizedActiveModelId))
        {
            throw new ArgumentException(
                "The active model must exist in the available model set.",
                nameof(activeModelId));
        }

        ActiveModelId = normalizedActiveModelId;
        _reasoningOptions = ReasoningOptions.Create(
            thinkingMode,
            reasoningEffort);
        WorkspacePath = NormalizeWorkspacePath(workspacePath);
        ParentSessionId = NormalizeOptionalSessionId(parentSessionId);
        SectionId = NormalizeSectionId(sectionId);
        SectionTitle = NormalizeSectionTitle(sectionTitle);
        SectionCreatedAtUtc = sectionCreatedAtUtc ?? DateTimeOffset.UtcNow;
        SectionUpdatedAtUtc = sectionUpdatedAtUtc ?? SectionCreatedAtUtc;
        TotalEstimatedOutputTokens = totalEstimatedOutputTokens;
        IsResumedSection = isResumedSection;
        PendingExecutionPlan = pendingExecutionPlan;
        RestoreSessionState(sessionState);
        _sessionContext = sessionContext ?? SessionContext.Empty;

        if (SectionUpdatedAtUtc < SectionCreatedAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(sectionUpdatedAtUtc));
        }

        if (conversationTurns is null)
        {
            return;
        }

        foreach (ConversationSectionTurn turn in conversationTurns.Where(static turn => turn is not null))
        {
            AddStoredTurn(turn);
        }
    }

    public string ApplicationName { get; }

    public string ActiveModelId { get; private set; }

    public IAgentProfile AgentProfile { get; private set; }

    public string AgentProfileName => AgentProfile.Name;

    public string? ActiveProviderName { get; private set; }

    public IReadOnlyList<string> AvailableModelIds { get; private set; }

    public AgentProviderProfile ProviderProfile { get; private set; }

    public string ProviderName => ProviderProfile.ProviderKind.ToDisplayName();

    public IReadOnlyDictionary<string, int> ModelContextWindowTokens => _modelContextWindowTokens;

    public IReadOnlyDictionary<string, ModelContextMetadata> ModelContextMetadata => _modelContextMetadata;

    public int? ContextWindowOverrideTokens => _contextWindowOverrideTokens;

    public int? ActiveModelReportedContextWindowTokens => _modelContextWindowTokens.TryGetValue(
        ActiveModelId,
        out int contextWindowTokens)
            ? contextWindowTokens
            : null;

    public int? ActiveModelContextWindowTokens
    {
        get
        {
            int? reportedContextWindowTokens = ActiveModelReportedContextWindowTokens;
            if (_contextWindowOverrideTokens is not > 0)
            {
                return reportedContextWindowTokens;
            }

            return reportedContextWindowTokens is > 0
                ? Math.Min(reportedContextWindowTokens.Value, _contextWindowOverrideTokens.Value)
                : _contextWindowOverrideTokens.Value;
        }
    }

    public ModelContextMetadata? ActiveModelContextMetadata => _modelContextMetadata.TryGetValue(
        ActiveModelId,
        out ModelContextMetadata? metadata)
            ? metadata
            : null;

    public ReasoningOptions Reasoning => _reasoningOptions;

    public string ThinkingMode => _reasoningOptions.ThinkingMode;

    public bool ShowThinking => _reasoningOptions.ShowThinking;

    public string? ReasoningEffort => _reasoningOptions.ReasoningEffort;

    public bool HasGeneratedSectionTitle =>
        !string.Equals(SectionTitle, DefaultSectionTitle, StringComparison.Ordinal);

    public bool IsPersistedStateDirty { get; private set; }

    public bool IsResumedSection { get; }

    public bool HasPendingExecutionPlan => PendingExecutionPlan is not null;

    public IReadOnlyList<ConversationRequestMessage> ConversationHistory => _conversationHistory;

    public IReadOnlyList<ConversationSectionTurn> ConversationTurns => _conversationTurns.ToArray();

    public PendingExecutionPlan? PendingExecutionPlan { get; private set; }

    public IReadOnlyList<PermissionRule> PermissionOverrides
    {
        get
        {
            lock (_syncRoot)
            {
                return _permissionOverrides.ToArray();
            }
        }
    }

    public DateTimeOffset SectionCreatedAtUtc { get; }

    public string SectionId { get; }

    /// <summary>
    /// For backward compatibility, SessionId returns SectionId.
    /// Use ParentSessionId to get the parent session ID for multi-section sessions.
    /// </summary>
    public string SessionId => SectionId;

    /// <summary>
    /// The ID of the parent session that contains this section.
    /// Null when the section is standalone (not grouped under a multi-section session).
    /// </summary>
    public string? ParentSessionId { get; }

    /// <summary>
    /// Accumulated context from completed sections in the same parent session.
    /// Empty when this is a standalone section or the first section in a session.
    /// </summary>
    public SessionContext SessionContext => _sessionContext;

    public string WorkspacePath { get; }

    public string WorkingDirectory { get; private set; } = ".";

    public SessionStateSnapshot SessionState
    {
        get
        {
            lock (_syncRoot)
            {
                return new SessionStateSnapshot(
                    _fileContexts.ToArray(),
                    _editContexts.ToArray(),
                    _terminalHistory.ToArray(),
                    WorkingDirectory);
            }
        }
    }

    public string SectionTitle { get; private set; }

    public DateTimeOffset SectionUpdatedAtUtc { get; private set; }

    public string SessionResumeCommand => $"stemcode --session {SessionId}";

    public string SectionResumeCommand => SessionResumeCommand;

    public int TotalEstimatedOutputTokens { get; private set; }

    /// <summary>
    /// Creates a <see cref="SessionContext"/> from the current section's completed state
    /// (file contexts, edit contexts, terminal history, and conversation summary).
    /// This is used to accumulate context when completing a section within a session.
    /// </summary>
    public SessionContext CreateCompletedSectionContext()
    {
        lock (_syncRoot)
        {
            string summary = CreateSectionSummary();
            return new SessionContext(
                _fileContexts.ToArray(),
                _editContexts.ToArray(),
                _terminalHistory.ToArray(),
                string.IsNullOrWhiteSpace(summary) ? [] : [summary]);
        }
    }

    /// <summary>
    /// Sets the accumulated session context from completed sections
    /// in the same parent session.
    /// </summary>
    public void SetSessionContext(SessionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _sessionContext = context;
    }

    public IDisposable BeginFileEditTransactionBatch()
    {
        if (_batchedFileEditTransactions is not null)
        {
            throw new InvalidOperationException("A file edit transaction batch is already active.");
        }

        _batchedFileEditTransactions = [];
        return new FileEditTransactionBatchScope(this);
    }

    public void AddPermissionOverride(PermissionRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        lock (_syncRoot)
        {
            _permissionOverrides.Add(rule);
        }
    }

    public void ClearPermissionOverrides()
    {
        lock (_syncRoot)
        {
            if (_permissionOverrides.Count == 0)
            {
                return;
            }

            _permissionOverrides.Clear();
            IsPersistedStateDirty = true;
        }
    }

    public void RegisterTemporaryArtifact(
        string path,
        TemporaryArtifactRetention retention)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string fullPath = Path.GetFullPath(path.Trim());
        lock (_syncRoot)
        {
            _temporaryArtifacts.Add(new TemporaryArtifactReference(fullPath, retention));
        }
    }

    public void DeleteTemporaryArtifacts(TemporaryArtifactRetention retention)
    {
        TemporaryArtifactReference[] artifacts;
        lock (_syncRoot)
        {
            artifacts = _temporaryArtifacts
                .Where(artifact => artifact.Retention == retention)
                .ToArray();

            if (artifacts.Length == 0)
            {
                return;
            }

            _temporaryArtifacts.RemoveAll(artifact => artifact.Retention == retention);
        }

        foreach (TemporaryArtifactReference artifact in artifacts)
        {
            TryDeleteTemporaryArtifact(artifact.Path);
        }
    }

    public void ClearPendingExecutionPlan()
    {
        if (PendingExecutionPlan is null)
        {
            return;
        }

        PendingExecutionPlan = null;
        IsPersistedStateDirty = true;
    }

    public void SetContextWindowOverride(int? contextWindowTokens)
    {
        if (contextWindowTokens is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contextWindowTokens));
        }

        _contextWindowOverrideTokens = contextWindowTokens;
    }

    public void SetActiveModel(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        string normalizedModelId = modelId.Trim();
        if (!_availableModelIds.Contains(normalizedModelId))
        {
            throw new InvalidOperationException(
                $"Model '{normalizedModelId}' is not available in the current session.");
        }

        if (string.Equals(ActiveModelId, normalizedModelId, StringComparison.Ordinal))
        {
            return;
        }

        ActiveModelId = normalizedModelId;
        IsPersistedStateDirty = true;
    }

    public void ReplaceProviderConfiguration(
        AgentProviderProfile providerProfile,
        string activeModelId,
        IReadOnlyList<string> availableModelIds,
        IReadOnlyDictionary<string, int>? modelContextWindowTokens = null,
        IReadOnlyDictionary<string, ModelContextMetadata>? modelContextMetadata = null,
        string? activeProviderName = null)
    {
        ArgumentNullException.ThrowIfNull(providerProfile);
        ArgumentException.ThrowIfNullOrWhiteSpace(activeModelId);
        ArgumentNullException.ThrowIfNull(availableModelIds);

        string[] normalizedAvailableModelIds = NormalizeAvailableModelIds(availableModelIds);
        if (normalizedAvailableModelIds.Length == 0)
        {
            throw new ArgumentException(
                "At least one available model must be provided.",
                nameof(availableModelIds));
        }

        string normalizedActiveModelId = activeModelId.Trim();
        if (!normalizedAvailableModelIds.Contains(normalizedActiveModelId, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "The active model must exist in the available model set.",
                nameof(activeModelId));
        }

        Dictionary<string, ModelContextMetadata> normalizedModelContextMetadata =
            NormalizeModelContextMetadata(
                modelContextMetadata,
                modelContextWindowTokens,
                normalizedAvailableModelIds);
        Dictionary<string, int> normalizedModelContextWindowTokens =
            CreateModelContextWindowTokens(normalizedModelContextMetadata);
        string? normalizedActiveProviderName = NormalizeProviderName(activeProviderName);
        if (normalizedActiveProviderName is null && Equals(ProviderProfile, providerProfile))
        {
            normalizedActiveProviderName = ActiveProviderName;
        }

        bool changed =
            !Equals(ProviderProfile, providerProfile) ||
            !string.Equals(ActiveProviderName, normalizedActiveProviderName, StringComparison.Ordinal) ||
            !string.Equals(ActiveModelId, normalizedActiveModelId, StringComparison.Ordinal) ||
            !AvailableModelIds.SequenceEqual(normalizedAvailableModelIds, StringComparer.Ordinal) ||
            !ModelContextMetadataEqual(_modelContextMetadata, normalizedModelContextMetadata);

        ActiveProviderName = normalizedActiveProviderName;
        ProviderProfile = providerProfile;
        AvailableModelIds = normalizedAvailableModelIds;
        _modelContextMetadata = normalizedModelContextMetadata;
        _modelContextWindowTokens = normalizedModelContextWindowTokens;
        _availableModelIds.Clear();
        foreach (string modelId in normalizedAvailableModelIds)
        {
            _availableModelIds.Add(modelId);
        }

        ActiveModelId = normalizedActiveModelId;

        if (changed)
        {
            IsPersistedStateDirty = true;
        }
    }

    public bool SetReasoningEffort(string? reasoningEffort)
    {
        string? legacyThinkingMode = ReasoningEffortOptions.TryNormalizeLegacyThinkingMode(reasoningEffort);
        if (legacyThinkingMode is not null)
        {
            bool thinkingChanged = SetThinkingMode(legacyThinkingMode);
            bool effortChanged = SetReasoningEffort(null);
            return thinkingChanged || effortChanged;
        }

        string? normalizedReasoningEffort = ReasoningEffortOptions.NormalizeOrThrow(reasoningEffort);
        if (string.Equals(ReasoningEffort, normalizedReasoningEffort, StringComparison.Ordinal))
        {
            return false;
        }

        _reasoningOptions = _reasoningOptions.WithReasoningEffort(normalizedReasoningEffort);
        IsPersistedStateDirty = true;
        return true;
    }

    public bool SetThinkingMode(string? thinkingMode)
    {
        string normalizedThinkingMode = ThinkingModeOptions.Format(thinkingMode);
        if (string.Equals(ThinkingMode, normalizedThinkingMode, StringComparison.Ordinal))
        {
            return false;
        }

        _reasoningOptions = _reasoningOptions.WithThinkingMode(normalizedThinkingMode);
        IsPersistedStateDirty = true;
        return true;
    }

    public bool SetActiveProviderName(string? activeProviderName)
    {
        string? normalizedActiveProviderName = NormalizeProviderName(activeProviderName);
        if (string.Equals(ActiveProviderName, normalizedActiveProviderName, StringComparison.Ordinal))
        {
            return false;
        }

        ActiveProviderName = normalizedActiveProviderName;
        IsPersistedStateDirty = true;
        return true;
    }

    public bool ClearReasoningEffort()
    {
        return SetReasoningEffort(null);
    }

    public string ResolvePathFromWorkingDirectory(string? requestedPath)
    {
        string workingDirectory;
        lock (_syncRoot)
        {
            workingDirectory = WorkingDirectory;
        }

        return ResolvePathFromWorkingDirectory(requestedPath, workingDirectory);
    }

    public string ResolvePathFromWorkingDirectory(
        string? requestedPath,
        string baseWorkingDirectory)
    {
        string normalizedBaseDirectory = string.IsNullOrWhiteSpace(baseWorkingDirectory)
            ? "."
            : baseWorkingDirectory.Trim();
        string baseFullPath = StemCode.Application.Utilities.WorkspaceResolvedPath.Resolve(
            WorkspacePath,
            normalizedBaseDirectory,
            ToolPathAccessKind.Read).CanonicalFullPath;
        string normalizedRequestedPath = string.IsNullOrWhiteSpace(requestedPath)
            ? "."
            : requestedPath.Trim();

        string candidatePath = Path.GetFullPath(
            Path.IsPathRooted(normalizedRequestedPath)
                ? normalizedRequestedPath
                : Path.Combine(baseFullPath, normalizedRequestedPath));
        string canonicalFullPath = StemCode.Application.Utilities.WorkspaceResolvedPath.Resolve(
            WorkspacePath,
            candidatePath,
            ToolPathAccessKind.Read).CanonicalFullPath;

        return StemCode.Application.Utilities.WorkspacePath.ToRelativePath(WorkspacePath, canonicalFullPath);
    }

    public bool TrySetWorkingDirectory(
        string requestedPath,
        out string? error)
    {
        return TrySetWorkingDirectory(requestedPath, WorkingDirectory, out error);
    }

    public bool TrySetWorkingDirectory(
        string requestedPath,
        string baseWorkingDirectory,
        out string? error)
    {
        error = null;

        string resolvedPath;
        try
        {
            resolvedPath = ResolvePathFromWorkingDirectory(requestedPath, baseWorkingDirectory);
        }
        catch (InvalidOperationException exception)
        {
            error = exception.Message;
            return false;
        }

        string fullPath = StemCode.Application.Utilities.WorkspaceResolvedPath.Resolve(
            WorkspacePath,
            resolvedPath,
            ToolPathAccessKind.Write).CanonicalFullPath;
        if (!Directory.Exists(fullPath))
        {
            error = $"Directory '{resolvedPath}' does not exist.";
            return false;
        }

        if (string.Equals(WorkingDirectory, resolvedPath, StringComparison.Ordinal))
        {
            return true;
        }

        WorkingDirectory = resolvedPath;
        IsPersistedStateDirty = true;
        return true;
    }

    public void SetAgentProfile(IAgentProfile agentProfile)
    {
        ArgumentNullException.ThrowIfNull(agentProfile);

        ToolOutputDisplay.ProfileFullToolOutput = agentProfile.FullToolOutput;

        if (string.Equals(AgentProfile.Name, agentProfile.Name, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        AgentProfile = agentProfile;
        IsPersistedStateDirty = true;
    }

    public int AddEstimatedOutputTokens(int estimatedOutputTokens)
    {
        if (estimatedOutputTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(estimatedOutputTokens));
        }

        TotalEstimatedOutputTokens += estimatedOutputTokens;
        IsPersistedStateDirty = true;
        return TotalEstimatedOutputTokens;
    }

    public void AddConversationTurn(
        string userInput,
        string assistantResponse,
        IReadOnlyList<ConversationToolCall>? toolCalls = null,
        IReadOnlyList<string>? toolOutputMessages = null,
        string? assistantReasoningContent = null,
        string? assistantReasoningDetailsJson = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userInput);
        ArgumentException.ThrowIfNullOrWhiteSpace(assistantResponse);

        ConversationSectionTurn turn = new(
            userInput,
            assistantResponse,
            toolCalls,
            toolOutputMessages,
            assistantReasoningContent,
            assistantReasoningDetailsJson,
            ConversationTurnStatus.Completed);

        AddStoredTurn(turn);
        IsPersistedStateDirty = true;
    }

    public ConversationSectionTurn CreatePendingConversationTurn(
        string userInput,
        IReadOnlyList<ConversationAttachment>? attachments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userInput);

        ConversationSectionTurn turn = new(
            userInput,
            attachments: attachments,
            status: ConversationTurnStatus.Pending);

        AddStoredTurn(turn);
        IsPersistedStateDirty = true;
        return turn;
    }

    public bool TryCompleteConversationTurn(
        string turnId,
        string assistantResponse,
        IReadOnlyList<ConversationToolCall>? toolCalls = null,
        IReadOnlyList<string>? toolOutputMessages = null,
        string? assistantReasoningContent = null,
        string? assistantReasoningDetailsJson = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(turnId);
        ArgumentException.ThrowIfNullOrWhiteSpace(assistantResponse);

        int turnIndex = FindTurnIndex(turnId);
        if (turnIndex < 0)
        {
            return false;
        }

        ConversationSectionTurn existingTurn = _conversationTurns[turnIndex];
        if (existingTurn.Status == ConversationTurnStatus.Completed)
        {
            return true;
        }

        if (existingTurn.Status is ConversationTurnStatus.Interrupted or ConversationTurnStatus.Cancelled)
        {
            return false;
        }

        _conversationTurns[turnIndex] = new ConversationSectionTurn(
            userInput: existingTurn.UserInput,
            assistantResponse: assistantResponse,
            toolCalls: toolCalls,
            toolOutputMessages: toolOutputMessages,
            assistantReasoningContent: assistantReasoningContent,
            assistantReasoningDetailsJson: assistantReasoningDetailsJson,
            status: ConversationTurnStatus.Completed,
            turnId: existingTurn.TurnId,
            attachments: existingTurn.Attachments);
        RebuildConversationHistory();
        IsPersistedStateDirty = true;
        return true;
    }

    public bool TryInterruptConversationTurn(
        string turnId,
        IReadOnlyList<ConversationToolCall>? toolCalls = null,
        IReadOnlyList<string>? toolOutputMessages = null,
        ConversationFailureInfo? failureInfo = null)
    {
        return TryFinalizeIncompleteConversationTurn(
            turnId,
            ConversationTurnStatus.Interrupted,
            toolCalls,
            toolOutputMessages,
            failureInfo);
    }

    public bool TryCancelConversationTurn(string turnId)
    {
        return TryFinalizeIncompleteConversationTurn(
            turnId,
            ConversationTurnStatus.Cancelled,
            null,
            null,
            null);
    }

    public bool TryAppendConversationTurnToolProgress(
        string turnId,
        IReadOnlyList<ConversationToolCall>? toolCalls,
        IReadOnlyList<string>? toolOutputMessages)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(turnId);

        int turnIndex = FindTurnIndex(turnId);
        if (turnIndex < 0)
        {
            return false;
        }

        ConversationSectionTurn existingTurn = _conversationTurns[turnIndex];
        if (existingTurn.Status != ConversationTurnStatus.Pending)
        {
            return existingTurn.Status == ConversationTurnStatus.Interrupted;
        }

        ConversationToolCall[] mergedToolCalls =
            [.. existingTurn.ToolCalls, .. (toolCalls ?? []).Where(static toolCall => toolCall is not null)];
        string[] mergedToolOutputMessages =
            [.. existingTurn.ToolOutputMessages, .. (toolOutputMessages ?? []).Where(static message => !string.IsNullOrWhiteSpace(message))];

        _conversationTurns[turnIndex] = new ConversationSectionTurn(
            userInput: existingTurn.UserInput,
            assistantResponse: null,
            toolCalls: mergedToolCalls,
            toolOutputMessages: mergedToolOutputMessages,
            assistantReasoningContent: null,
            assistantReasoningDetailsJson: null,
            status: existingTurn.Status,
            turnId: existingTurn.TurnId,
            attachments: existingTurn.Attachments,
            failureInfo: existingTurn.FailureInfo);
        IsPersistedStateDirty = true;
        return true;
    }

    public int CompactConversationHistory(int retainedHistoryTurns)
    {
        if (retainedHistoryTurns < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retainedHistoryTurns));
        }

        int compactedTurnCount = _conversationTurns.Count - retainedHistoryTurns;
        if (compactedTurnCount <= 0)
        {
            return 0;
        }

        string? compactedHistory = CreateCompactedConversationHistory(retainedHistoryTurns);
        if (string.IsNullOrWhiteSpace(compactedHistory))
        {
            return 0;
        }

        ConversationSectionTurn compactedTurn = new(
            "Compacted previous conversation",
            compactedHistory);
        ConversationSectionTurn[] retainedTurns = _conversationTurns
            .Skip(compactedTurnCount)
            .ToArray();

        _conversationTurns.Clear();
        _conversationHistory.Clear();
        AddStoredTurn(compactedTurn);

        foreach (ConversationSectionTurn retainedTurn in retainedTurns)
        {
            AddStoredTurn(retainedTurn);
        }

        IsPersistedStateDirty = true;
        return compactedTurnCount;
    }

    public void RecordFileContext(SessionFileContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(context.Path) ||
            string.IsNullOrWhiteSpace(context.Activity) ||
            string.IsNullOrWhiteSpace(context.Summary))
        {
            return;
        }

        SessionFileContext normalizedContext = context with
        {
            Path = NormalizeStateText(context.Path, MaxPromptFieldCharacters),
            Activity = NormalizeStateText(context.Activity, MaxPromptFieldCharacters),
            Summary = NormalizeStateText(context.Summary, MaxStateTextCharacters)
        };

        lock (_syncRoot)
        {
            int existingIndex = _fileContexts.FindIndex(existing =>
                string.Equals(existing.Path, normalizedContext.Path, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.Activity, normalizedContext.Activity, StringComparison.Ordinal));

            if (existingIndex >= 0)
            {
                _fileContexts.RemoveAt(existingIndex);
            }

            _fileContexts.Add(normalizedContext);
            TrimOldest(_fileContexts, MaxFileContextEntries);
            IsPersistedStateDirty = true;
        }
    }

    public void RecordEditContext(SessionEditContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(context.Description))
        {
            return;
        }

        string[] normalizedPaths = (context.Paths ?? [])
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => NormalizeStateText(path, MaxPromptFieldCharacters))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        SessionEditContext normalizedContext = context with
        {
            Description = NormalizeStateText(context.Description, MaxPromptFieldCharacters),
            Paths = normalizedPaths,
            AddedLineCount = Math.Max(0, context.AddedLineCount),
            RemovedLineCount = Math.Max(0, context.RemovedLineCount)
        };

        lock (_syncRoot)
        {
            _recordedEdits.Add(normalizedContext);
            _editContexts.Add(normalizedContext);
            _totalRecordedEditCount++;
            TrimOldest(_editContexts, MaxEditContextEntries);
            IsPersistedStateDirty = true;
        }
    }

    internal int GetRecordedEditCount()
    {
        lock (_syncRoot)
        {
            return _totalRecordedEditCount;
        }
    }

    internal IReadOnlyList<SessionEditContext> GetEditsSince(int startingEditIndex)
    {
        lock (_syncRoot)
        {
            int normalizedStartingIndex = Math.Max(0, startingEditIndex);
            if (normalizedStartingIndex >= _recordedEdits.Count)
            {
                return [];
            }

            return _recordedEdits
                .Skip(normalizedStartingIndex)
                .ToArray();
        }
    }

    public void RecordTerminalCommand(SessionTerminalCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.Command) ||
            string.IsNullOrWhiteSpace(command.WorkingDirectory))
        {
            return;
        }

        SessionTerminalCommand normalizedCommand = command with
        {
            Command = NormalizeStateText(command.Command, MaxPromptFieldCharacters),
            WorkingDirectory = NormalizeStateText(command.WorkingDirectory, MaxPromptFieldCharacters),
            StandardOutput = NormalizeOptionalStateText(command.StandardOutput, MaxStateTextCharacters),
            StandardError = NormalizeOptionalStateText(command.StandardError, MaxStateTextCharacters),
            TerminalId = NormalizeOptionalStateText(command.TerminalId, MaxPromptFieldCharacters),
            TerminalStatus = NormalizeOptionalStateText(command.TerminalStatus, MaxPromptFieldCharacters)
        };

        lock (_syncRoot)
        {
            _terminalHistory.Add(normalizedCommand);
            TrimOldest(_terminalHistory, MaxTerminalHistoryEntries);
            IsPersistedStateDirty = true;
        }
    }

    public string? CreateStatefulContextPrompt()
    {
        if (_fileContexts.Count == 0 &&
            _editContexts.Count == 0 &&
            _terminalHistory.Count == 0 &&
            IsWorkspaceRootWorkingDirectory() &&
            _sessionContext.IsEmpty)
        {
            return null;
        }

        StringBuilder builder = new();
        builder.AppendLine("Session state:");
        builder.AppendLine("Compact memory from previous tool use in this section. Use it to maintain continuity across turns; re-read files or rerun commands when exact current contents or fresh output matter.");
        AppendWorkingDirectoryPrompt(builder);

        AppendFileContextPrompt(builder);
        AppendEditContextPrompt(builder);
        AppendTerminalHistoryPrompt(builder);

        AppendSessionContextPrompt(builder);

        return builder.ToString().Trim();
    }

    public ConversationSectionSnapshot CreateSectionSnapshot(DateTimeOffset updatedAtUtc)
    {
        if (updatedAtUtc < SectionCreatedAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(updatedAtUtc));
        }

        ValidateConversationHistoryConsistency();

        return new ConversationSectionSnapshot(
            SectionId,
            SectionTitle,
            SectionCreatedAtUtc,
            updatedAtUtc,
            ProviderProfile,
            ActiveModelId,
            AvailableModelIds,
            _conversationTurns.ToArray(),
            TotalEstimatedOutputTokens,
            PendingExecutionPlan,
            AgentProfile.Name,
            ReasoningEffort,
            ThinkingMode,
            SessionState,
            WorkspacePath,
            _modelContextWindowTokens,
            _modelContextMetadata,
            ActiveProviderName,
            ParentSessionId);
    }

    public IReadOnlyList<ConversationRequestMessage> GetConversationHistory(int maxHistoryTurns)
    {
        if (maxHistoryTurns <= 0 || _conversationHistory.Count == 0)
        {
            return [];
        }

        int maxMessageCount = checked(maxHistoryTurns * 2);
        if (_conversationHistory.Count <= maxMessageCount)
        {
            return _conversationHistory.ToArray();
        }

        ConversationRequestMessage[] recentHistory = _conversationHistory
            .Skip(_conversationHistory.Count - maxMessageCount)
            .ToArray();
        string? compactedHistory = CreateCompactedConversationHistory(maxHistoryTurns);

        return string.IsNullOrWhiteSpace(compactedHistory)
            ? recentHistory
            : [ConversationRequestMessage.User(compactedHistory), .. recentHistory];
    }

    private string? CreateCompactedConversationHistory(int retainedHistoryTurns)
    {
        int compactedTurnCount = _conversationTurns.Count - retainedHistoryTurns;
        if (compactedTurnCount <= 0)
        {
            return null;
        }

        int skippedCompactedTurns = Math.Max(0, compactedTurnCount - MaxCompactedHistoryTurns);
        ConversationSectionTurn[] compactedTurns = _conversationTurns
            .Skip(skippedCompactedTurns)
            .Take(compactedTurnCount - skippedCompactedTurns)
            .ToArray();
        if (compactedTurns.Length == 0)
        {
            return null;
        }

        StringBuilder builder = new();
        builder.Append("Earlier conversation context (compacted; ");
        builder.Append(compactedTurnCount.ToString(CultureInfo.InvariantCulture));
        builder.Append(compactedTurnCount == 1 ? " older turn" : " older turns");
        if (skippedCompactedTurns > 0)
        {
            builder.Append(", showing the most recent ");
            builder.Append(compactedTurns.Length.ToString(CultureInfo.InvariantCulture));
        }
        builder.AppendLine("; full transcript omitted):");
        builder.AppendLine("Use this for continuity only; inspect files or rerun commands when exact current state matters.");

        for (int index = 0; index < compactedTurns.Length; index++)
        {
            ConversationSectionTurn turn = compactedTurns[index];
            int originalTurnNumber = skippedCompactedTurns + index + 1;
            builder.Append("- Turn ");
            builder.Append(originalTurnNumber.ToString(CultureInfo.InvariantCulture));
            builder.Append(" user: ");
            builder.AppendLine(CompactHistoryField(turn.UserInput));
            builder.Append("  status: ");
            builder.AppendLine(turn.Status.ToString());

            if (!string.IsNullOrWhiteSpace(turn.AssistantResponse))
            {
                builder.Append("  assistant: ");
                builder.AppendLine(CompactHistoryField(turn.AssistantResponse));
            }

            if (turn.ToolCalls.Count > 0)
            {
                string toolNames = string.Join(
                    ", ",
                    turn.ToolCalls
                        .Select(static toolCall => toolCall.Name)
                        .Where(static name => !string.IsNullOrWhiteSpace(name))
                        .Distinct(StringComparer.Ordinal)
                        .Take(8));

                if (!string.IsNullOrWhiteSpace(toolNames))
                {
                    builder.Append("  tools: ");
                    builder.AppendLine(toolNames);
                }
            }

            if (builder.Length >= MaxCompactedHistoryCharacters)
            {
                break;
            }
        }

        string compactedHistory = builder.ToString().Trim();
        return compactedHistory.Length <= MaxCompactedHistoryCharacters
            ? compactedHistory
            : compactedHistory[..Math.Max(0, MaxCompactedHistoryCharacters - 3)].TrimEnd() + "...";
    }

    private void AddStoredTurn(ConversationSectionTurn turn)
    {
        _conversationTurns.Add(turn);
        AddTurnMessages(_conversationHistory, turn);
    }

    private void AddTurnMessages(
        List<ConversationRequestMessage> history,
        ConversationSectionTurn turn)
    {
        history.Add(ConversationRequestMessage.User(turn.UserInput, turn.Attachments));

        if (!string.IsNullOrWhiteSpace(turn.AssistantResponse))
        {
            history.Add(ConversationRequestMessage.AssistantMessage(
                turn.AssistantResponse,
                turn.AssistantReasoningContent,
                turn.AssistantReasoningDetailsJson));
        }
    }

    private int FindTurnIndex(string turnId)
    {
        return _conversationTurns.FindIndex(turn =>
            string.Equals(turn.TurnId, turnId.Trim(), StringComparison.Ordinal));
    }

    private bool TryFinalizeIncompleteConversationTurn(
        string turnId,
        ConversationTurnStatus finalStatus,
        IReadOnlyList<ConversationToolCall>? toolCalls,
        IReadOnlyList<string>? toolOutputMessages,
        ConversationFailureInfo? failureInfo)
    {
        if (finalStatus is not ConversationTurnStatus.Interrupted and not ConversationTurnStatus.Cancelled)
        {
            throw new ArgumentOutOfRangeException(nameof(finalStatus));
        }

        int turnIndex = FindTurnIndex(turnId);
        if (turnIndex < 0)
        {
            return false;
        }

        ConversationSectionTurn existingTurn = _conversationTurns[turnIndex];
        if (existingTurn.Status == ConversationTurnStatus.Completed)
        {
            return false;
        }

        if (existingTurn.Status == finalStatus)
        {
            return true;
        }

        if (existingTurn.Status is ConversationTurnStatus.Interrupted or ConversationTurnStatus.Cancelled)
        {
            return false;
        }

        _conversationTurns[turnIndex] = new ConversationSectionTurn(
            userInput: existingTurn.UserInput,
            assistantResponse: null,
            toolCalls: toolCalls ?? existingTurn.ToolCalls,
            toolOutputMessages: toolOutputMessages ?? existingTurn.ToolOutputMessages,
            assistantReasoningContent: null,
            assistantReasoningDetailsJson: null,
            status: finalStatus,
            turnId: existingTurn.TurnId,
            attachments: existingTurn.Attachments,
            failureInfo: failureInfo);
        RebuildConversationHistory();
        IsPersistedStateDirty = true;
        return true;
    }

    private void RebuildConversationHistory()
    {
        _conversationHistory.Clear();
        foreach (ConversationSectionTurn turn in _conversationTurns)
        {
            AddTurnMessages(_conversationHistory, turn);
        }
    }

    private void ValidateConversationHistoryConsistency()
    {
        List<ConversationRequestMessage> expectedHistory = [];
        foreach (ConversationSectionTurn turn in _conversationTurns)
        {
            AddTurnMessages(expectedHistory, turn);
        }

        if (_conversationHistory.Count != expectedHistory.Count)
        {
            throw new InvalidOperationException(
                "Conversation turn metadata must match conversation history before it can be persisted.");
        }

        for (int index = 0; index < expectedHistory.Count; index++)
        {
            ConversationRequestMessage actual = _conversationHistory[index];
            ConversationRequestMessage expected = expectedHistory[index];
            if (!string.Equals(actual.Role, expected.Role, StringComparison.Ordinal) ||
                !string.Equals(actual.Content, expected.Content, StringComparison.Ordinal) ||
                !string.Equals(actual.ReasoningContent, expected.ReasoningContent, StringComparison.Ordinal) ||
                !string.Equals(actual.ReasoningDetailsJson, expected.ReasoningDetailsJson, StringComparison.Ordinal) ||
                actual.Attachments.Count != expected.Attachments.Count)
            {
                throw new InvalidOperationException(
                    "Conversation history contains an unsupported message layout for section persistence.");
            }
        }
    }

    private static string CompactHistoryField(string value)
    {
        string normalized = NormalizeWhitespace(value);
        return normalized.Length <= MaxCompactedHistoryFieldCharacters
            ? normalized
            : normalized[..Math.Max(0, MaxCompactedHistoryFieldCharacters - 3)].TrimEnd() + "...";
    }

    private static string NormalizeWhitespace(string value)
    {
        StringBuilder builder = new(value.Length);
        bool previousWasWhitespace = false;
        foreach (char character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                if (!previousWasWhitespace)
                {
                    builder.Append(' ');
                    previousWasWhitespace = true;
                }

                continue;
            }

            builder.Append(character);
            previousWasWhitespace = false;
        }

        return builder.ToString().Trim();
    }

    private static string[] NormalizeAvailableModelIds(IReadOnlyList<string> availableModelIds)
    {
        return availableModelIds
            .Where(static modelId => !string.IsNullOrWhiteSpace(modelId))
            .Select(static modelId => modelId.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static Dictionary<string, ModelContextMetadata> NormalizeModelContextMetadata(
        IReadOnlyDictionary<string, ModelContextMetadata>? modelContextMetadata,
        IReadOnlyDictionary<string, int>? modelContextWindowTokens,
        IReadOnlyList<string> availableModelIds)
    {
        Dictionary<string, ModelContextMetadata> normalized = new(StringComparer.Ordinal);
        HashSet<string> available = new(availableModelIds, StringComparer.Ordinal);

        if (modelContextMetadata is not null)
        {
            foreach ((string modelId, ModelContextMetadata metadata) in modelContextMetadata)
            {
                if (string.IsNullOrWhiteSpace(modelId) ||
                    metadata is null ||
                    metadata.ContextWindowTokens <= 0)
                {
                    continue;
                }

                string normalizedModelId = modelId.Trim();
                if (available.Contains(normalizedModelId))
                {
                    normalized[normalizedModelId] = metadata;
                }
            }
        }

        if (modelContextWindowTokens is null)
        {
            return normalized;
        }

        foreach ((string modelId, int contextWindowTokens) in modelContextWindowTokens)
        {
            if (string.IsNullOrWhiteSpace(modelId) || contextWindowTokens <= 0)
            {
                continue;
            }

            string normalizedModelId = modelId.Trim();
            if (available.Contains(normalizedModelId) &&
                !normalized.ContainsKey(normalizedModelId))
            {
                normalized[normalizedModelId] = new ModelContextMetadata(contextWindowTokens);
            }
        }

        return normalized;
    }

    private static Dictionary<string, int> CreateModelContextWindowTokens(
        IReadOnlyDictionary<string, ModelContextMetadata> modelContextMetadata)
    {
        Dictionary<string, int> contextWindowTokens = new(StringComparer.Ordinal);
        foreach ((string modelId, ModelContextMetadata metadata) in modelContextMetadata)
        {
            if (metadata.ContextWindowTokens > 0)
            {
                contextWindowTokens[modelId] = metadata.ContextWindowTokens;
            }
        }

        return contextWindowTokens;
    }

    private static bool ModelContextMetadataEqual(
        IReadOnlyDictionary<string, ModelContextMetadata> first,
        IReadOnlyDictionary<string, ModelContextMetadata> second)
    {
        if (first.Count != second.Count)
        {
            return false;
        }

        foreach ((string modelId, ModelContextMetadata metadata) in first)
        {
            if (!second.TryGetValue(modelId, out ModelContextMetadata? otherMetadata) ||
                !Equals(otherMetadata, metadata))
            {
                return false;
            }
        }

        return true;
    }

    public void RecordFileEditTransaction(WorkspaceFileEditTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        if (_batchedFileEditTransactions is not null)
        {
            _batchedFileEditTransactions.Add(transaction);
            return;
        }

        ExpireRedoFileEditTransactions();
        _undoFileEditTransactions.Push(transaction);
    }

    public bool TryCreateFileEditTransactionSnapshot(
        string description,
        out WorkspaceFileEditTransaction? transaction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        if (_batchedFileEditTransactions is not null)
        {
            throw new InvalidOperationException(
                "File edit transactions cannot be snapshotted while a transaction batch is active.");
        }

        WorkspaceFileEditTransaction[] transactions = _undoFileEditTransactions
            .Reverse()
            .ToArray();

        if (transactions.Length == 0)
        {
            transaction = null;
            return false;
        }

        transaction = transactions.Length == 1
            ? new WorkspaceFileEditTransaction(
                description,
                transactions[0].BeforeStates,
                transactions[0].AfterStates)
            : MergeTransactions(transactions, description);
        return true;
    }

    public bool TryGetPendingUndoFileEdit(out WorkspaceFileEditTransaction? transaction)
    {
        if (_undoFileEditTransactions.Count == 0)
        {
            transaction = null;
            return false;
        }

        transaction = _undoFileEditTransactions.Peek();
        return true;
    }

    public void CompleteUndoFileEdit()
    {
        if (_undoFileEditTransactions.Count == 0)
        {
            throw new InvalidOperationException("There is no file edit transaction to undo.");
        }

        WorkspaceFileEditTransaction transaction = _undoFileEditTransactions.Pop();
        _redoFileEditTransactions.Push(transaction);
    }

    public bool TryGetPendingRedoFileEdit(out WorkspaceFileEditTransaction? transaction)
    {
        if (_redoFileEditTransactions.Count == 0)
        {
            transaction = null;
            return false;
        }

        transaction = _redoFileEditTransactions.Peek();
        return true;
    }

    public void CompleteRedoFileEdit()
    {
        if (_redoFileEditTransactions.Count == 0)
        {
            throw new InvalidOperationException("There is no file edit transaction to redo.");
        }

        WorkspaceFileEditTransaction transaction = _redoFileEditTransactions.Pop();
        _undoFileEditTransactions.Push(transaction);
    }

    public IReadOnlyList<WorkspaceFileEditTransaction> DrainExpiredFileEditTransactions()
    {
        if (_expiredFileEditTransactions.Count == 0)
        {
            return [];
        }

        WorkspaceFileEditTransaction[] expired = _expiredFileEditTransactions.ToArray();
        _expiredFileEditTransactions.Clear();
        return expired;
    }

    public void MarkSectionPersisted(DateTimeOffset updatedAtUtc)
    {
        if (updatedAtUtc < SectionCreatedAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(updatedAtUtc));
        }

        SectionUpdatedAtUtc = updatedAtUtc;
        IsPersistedStateDirty = false;
    }

    public void SetPendingExecutionPlan(PendingExecutionPlan pendingExecutionPlan)
    {
        ArgumentNullException.ThrowIfNull(pendingExecutionPlan);

        if (PendingExecutionPlan is not null &&
            string.Equals(PendingExecutionPlan.SourceUserInput, pendingExecutionPlan.SourceUserInput, StringComparison.Ordinal) &&
            string.Equals(PendingExecutionPlan.PlanningSummary, pendingExecutionPlan.PlanningSummary, StringComparison.Ordinal) &&
            PendingExecutionPlan.Tasks.SequenceEqual(pendingExecutionPlan.Tasks, StringComparer.Ordinal))
        {
            return;
        }

        PendingExecutionPlan = pendingExecutionPlan;
        IsPersistedStateDirty = true;
    }

    public void RenameSection(
        string title,
        DateTimeOffset updatedAtUtc)
    {
        if (updatedAtUtc < SectionCreatedAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(updatedAtUtc));
        }

        string normalizedTitle = NormalizeSectionTitle(title);
        if (string.Equals(SectionTitle, normalizedTitle, StringComparison.Ordinal))
        {
            return;
        }

        SectionTitle = normalizedTitle;
        SectionUpdatedAtUtc = updatedAtUtc;
        IsPersistedStateDirty = true;
    }

    public bool TryGetFirstUserPrompt(out string? prompt)
    {
        prompt = _conversationHistory
            .FirstOrDefault(static message => string.Equals(message.Role, "user", StringComparison.Ordinal))
            ?.Content;

        return !string.IsNullOrWhiteSpace(prompt);
    }

    public bool TryStartSectionTitleGeneration()
    {
        if (HasGeneratedSectionTitle || _sectionTitleGenerationStarted)
        {
            return false;
        }

        _sectionTitleGenerationStarted = true;
        return true;
    }

    private void CompleteFileEditTransactionBatch()
    {
        List<WorkspaceFileEditTransaction>? batch = _batchedFileEditTransactions;
        _batchedFileEditTransactions = null;

        if (batch is null || batch.Count == 0)
        {
            return;
        }

        WorkspaceFileEditTransaction transaction = batch.Count == 1
            ? batch[0]
            : MergeTransactions(batch);

        ExpireRedoFileEditTransactions();
        _undoFileEditTransactions.Push(transaction);
    }

    private void ExpireRedoFileEditTransactions()
    {
        if (_redoFileEditTransactions.Count == 0)
        {
            return;
        }

        _expiredFileEditTransactions.AddRange(_redoFileEditTransactions);
        _redoFileEditTransactions.Clear();
    }

    private void RestoreSessionState(SessionStateSnapshot? sessionState)
    {
        if (sessionState is null)
        {
            return;
        }

        RestoreWorkingDirectory(sessionState.WorkingDirectory);

        if (sessionState.IsEmpty)
        {
            return;
        }

        foreach (SessionFileContext context in (sessionState.Files ?? []).Where(static context => context is not null))
        {
            if (string.IsNullOrWhiteSpace(context.Path) ||
                string.IsNullOrWhiteSpace(context.Activity) ||
                string.IsNullOrWhiteSpace(context.Summary))
            {
                continue;
            }

            _fileContexts.Add(context with
            {
                Path = NormalizeStateText(context.Path, MaxPromptFieldCharacters),
                Activity = NormalizeStateText(context.Activity, MaxPromptFieldCharacters),
                Summary = NormalizeStateText(context.Summary, MaxStateTextCharacters)
            });
        }

        foreach (SessionEditContext context in (sessionState.Edits ?? []).Where(static context => context is not null))
        {
            if (string.IsNullOrWhiteSpace(context.Description))
            {
                continue;
            }

            SessionEditContext normalizedContext = context with
            {
                Description = NormalizeStateText(context.Description, MaxPromptFieldCharacters),
                Paths = (context.Paths ?? [])
                    .Where(static path => !string.IsNullOrWhiteSpace(path))
                    .Select(static path => NormalizeStateText(path, MaxPromptFieldCharacters))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                AddedLineCount = Math.Max(0, context.AddedLineCount),
                RemovedLineCount = Math.Max(0, context.RemovedLineCount)
            };
            _editContexts.Add(normalizedContext);
            _recordedEdits.Add(normalizedContext);
        }

        _totalRecordedEditCount = _recordedEdits.Count;

        foreach (SessionTerminalCommand command in (sessionState.TerminalHistory ?? []).Where(static command => command is not null))
        {
            if (string.IsNullOrWhiteSpace(command.Command) ||
                string.IsNullOrWhiteSpace(command.WorkingDirectory))
            {
                continue;
            }

            _terminalHistory.Add(command with
            {
                Command = NormalizeStateText(command.Command, MaxPromptFieldCharacters),
                WorkingDirectory = NormalizeStateText(command.WorkingDirectory, MaxPromptFieldCharacters),
                StandardOutput = NormalizeOptionalStateText(command.StandardOutput, MaxStateTextCharacters),
                StandardError = NormalizeOptionalStateText(command.StandardError, MaxStateTextCharacters),
                TerminalId = NormalizeOptionalStateText(command.TerminalId, MaxPromptFieldCharacters),
                TerminalStatus = NormalizeOptionalStateText(command.TerminalStatus, MaxPromptFieldCharacters)
            });
        }

        TrimOldest(_fileContexts, MaxFileContextEntries);
        TrimOldest(_editContexts, MaxEditContextEntries);
        TrimOldest(_terminalHistory, MaxTerminalHistoryEntries);
    }

    private void RestoreWorkingDirectory(string? workingDirectory)
    {
        string resolvedPath;
        try
        {
            resolvedPath = ResolvePathFromWorkingDirectory(workingDirectory, ".");
        }
        catch (InvalidOperationException)
        {
            WorkingDirectory = ".";
            return;
        }

        string fullPath = StemCode.Application.Utilities.WorkspaceResolvedPath.Resolve(
            WorkspacePath,
            resolvedPath,
            ToolPathAccessKind.Write).CanonicalFullPath;
        WorkingDirectory = Directory.Exists(fullPath)
            ? resolvedPath
            : ".";
    }

    private void AppendWorkingDirectoryPrompt(StringBuilder builder)
    {
        if (IsWorkspaceRootWorkingDirectory())
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine($"Current working directory: {WorkingDirectory}");
        builder.AppendLine("Relative file paths and shell commands default to this directory unless an explicit path says otherwise.");
    }

    private void AppendFileContextPrompt(StringBuilder builder)
    {
        if (_fileContexts.Count == 0)
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine("Known files and workspace observations:");
        foreach (SessionFileContext context in TakeLatest(_fileContexts, MaxPromptFileContextEntries))
        {
            builder
                .Append("- ")
                .Append(context.Path)
                .Append(" [")
                .Append(context.Activity)
                .Append(", ")
                .Append(FormatTimestamp(context.ObservedAtUtc))
                .Append("]: ")
                .AppendLine(FormatPromptField(context.Summary));
        }
    }

    private void AppendEditContextPrompt(StringBuilder builder)
    {
        if (_editContexts.Count == 0)
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine("Previous edits:");
        foreach (SessionEditContext context in TakeLatest(_editContexts, MaxPromptEditContextEntries))
        {
            string paths = context.Paths.Count == 0
                ? "(paths unavailable)"
                : string.Join(", ", context.Paths);

            builder
                .Append("- ")
                .Append(FormatTimestamp(context.EditedAtUtc))
                .Append(": ")
                .Append(context.Description)
                .Append(" on ")
                .Append(paths)
                .Append(" (+")
                .Append(context.AddedLineCount.ToString(CultureInfo.InvariantCulture))
                .Append(" -")
                .Append(context.RemovedLineCount.ToString(CultureInfo.InvariantCulture))
                .AppendLine(").");
        }
    }

    private void AppendTerminalHistoryPrompt(StringBuilder builder)
    {
        if (_terminalHistory.Count == 0)
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine("Terminal history:");
        foreach (SessionTerminalCommand command in TakeLatest(_terminalHistory, MaxPromptTerminalHistoryEntries))
        {
            builder
                .Append("- ")
                .Append(FormatTimestamp(command.ExecutedAtUtc))
                .Append(": `")
                .Append(command.Command)
                .Append("` in ")
                .Append(command.WorkingDirectory);

            if (command.Background)
            {
                builder
                    .Append(" background terminal ")
                    .Append(string.IsNullOrWhiteSpace(command.TerminalId)
                        ? "(unknown)"
                        : command.TerminalId)
                    .Append(" status ")
                    .Append(string.IsNullOrWhiteSpace(command.TerminalStatus)
                        ? "unknown"
                        : command.TerminalStatus);

                if (command.ExitCode >= 0)
                {
                    builder
                        .Append(" exit ")
                        .Append(command.ExitCode.ToString(CultureInfo.InvariantCulture));
                }
            }
            else
            {
                builder
                    .Append(" exited ")
                    .Append(command.ExitCode.ToString(CultureInfo.InvariantCulture));
            }

            if (!string.IsNullOrWhiteSpace(command.StandardOutput))
            {
                builder
                    .Append("; stdout: ")
                    .Append(FormatPromptField(command.StandardOutput));
            }

            if (!string.IsNullOrWhiteSpace(command.StandardError))
            {
                builder
                    .Append("; stderr: ")
                    .Append(FormatPromptField(command.StandardError));
            }

            builder.AppendLine();
        }
    }

    private void AppendSessionContextPrompt(StringBuilder builder)
    {
        if (_sessionContext.IsEmpty)
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine("Completed section context (from previous sections in this session):");
        builder.AppendLine("Use this for continuity across sections; files or terminal state may have changed since the previous section ended.");

        if (_sessionContext.CompletedSectionSummaries.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Completed section summaries:");
            foreach (string summary in _sessionContext.CompletedSectionSummaries)
            {
                builder.Append("- ").AppendLine(summary);
            }
        }

        if (_sessionContext.Files.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Files known from previous sections:");
            foreach (SessionFileContext file in TakeLatest(_sessionContext.Files, MaxPromptFileContextEntries))
            {
                builder
                    .Append("- ")
                    .Append(file.Path)
                    .Append(" [")
                    .Append(file.Activity)
                    .Append(", ")
                    .Append(FormatTimestamp(file.ObservedAtUtc))
                    .Append("]: ")
                    .AppendLine(FormatPromptField(file.Summary));
            }
        }

        if (_sessionContext.Edits.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Edits from previous sections:");
            foreach (SessionEditContext edit in TakeLatest(_sessionContext.Edits, MaxPromptEditContextEntries))
            {
                string paths = edit.Paths.Count == 0
                    ? "(paths unavailable)"
                    : string.Join(", ", edit.Paths);

                builder
                    .Append("- ")
                    .Append(FormatTimestamp(edit.EditedAtUtc))
                    .Append(": ")
                    .Append(edit.Description)
                    .Append(" on ")
                    .Append(paths)
                    .Append(" (+")
                    .Append(edit.AddedLineCount.ToString(CultureInfo.InvariantCulture))
                    .Append(" -")
                    .Append(edit.RemovedLineCount.ToString(CultureInfo.InvariantCulture))
                    .AppendLine(").");
            }
        }

        if (_sessionContext.TerminalHistory.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Terminal history from previous sections:");
            foreach (SessionTerminalCommand command in TakeLatest(_sessionContext.TerminalHistory, MaxPromptTerminalHistoryEntries))
            {
                builder
                    .Append("- ")
                    .Append(FormatTimestamp(command.ExecutedAtUtc))
                    .Append(": `")
                    .Append(command.Command)
                    .Append("` in ")
                    .Append(command.WorkingDirectory)
                    .Append(" exited ")
                    .Append(command.ExitCode.ToString(CultureInfo.InvariantCulture));
                builder.AppendLine();
            }
        }
    }

    private string CreateSectionSummary()
    {
        int turnCount = _conversationTurns.Count;
        if (turnCount == 0)
        {
            return string.Empty;
        }

        string firstUserPrompt = _conversationTurns[0].UserInput;
        string normalizedPrompt = firstUserPrompt.Length > 120
            ? firstUserPrompt[..117].TrimEnd() + "..."
            : firstUserPrompt;

        return $"Section \"{SectionTitle}\": {turnCount} turn(s), started with \"{normalizedPrompt}\".";
    }

    private static IReadOnlyList<T> TakeLatest<T>(
        IReadOnlyList<T> values,
        int maxCount)
    {
        if (values.Count <= maxCount)
        {
            return values.ToArray();
        }

        return values
            .Skip(values.Count - maxCount)
            .ToArray();
    }

    private static string FormatTimestamp(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture);
    }

    private static string FormatPromptField(string value)
    {
        return NormalizeStateText(value, MaxPromptFieldCharacters)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }

    private static string? NormalizeOptionalStateText(
        string? value,
        int maxCharacters)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : NormalizeStateText(value, maxCharacters);
    }

    private static string NormalizeStateText(
        string value,
        int maxCharacters)
    {
        string normalized = SecretRedactor.Redact(value)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();

        if (normalized.Length <= maxCharacters)
        {
            return normalized;
        }

        return normalized[..Math.Max(0, maxCharacters - 3)].TrimEnd() + "...";
    }

    private static void TrimOldest<T>(
        List<T> values,
        int maxCount)
    {
        if (values.Count <= maxCount)
        {
            return;
        }

        values.RemoveRange(0, values.Count - maxCount);
    }

    private bool IsWorkspaceRootWorkingDirectory()
    {
        return string.Equals(WorkingDirectory, ".", StringComparison.Ordinal);
    }

    private static WorkspaceFileEditTransaction MergeTransactions(
        IReadOnlyList<WorkspaceFileEditTransaction> transactions,
        string? description = null)
    {
        StringComparer pathComparer = StemCode.Application.Utilities.WorkspacePath.GetPathComparer();
        Dictionary<string, WorkspaceFileEditState> firstBeforeStates = new(pathComparer);
        Dictionary<string, WorkspaceFileEditState> lastAfterStates = new(pathComparer);
        List<string> orderedPaths = [];

        foreach (WorkspaceFileEditTransaction transaction in transactions)
        {
            foreach (WorkspaceFileEditState state in transaction.BeforeStates)
            {
                if (firstBeforeStates.TryAdd(state.Path, state))
                {
                    orderedPaths.Add(state.Path);
                }
            }

            foreach (WorkspaceFileEditState state in transaction.AfterStates)
            {
                if (!lastAfterStates.ContainsKey(state.Path) &&
                    !orderedPaths.Contains(state.Path, pathComparer))
                {
                    orderedPaths.Add(state.Path);
                }

                lastAfterStates[state.Path] = state;
            }
        }

        WorkspaceFileEditState[] beforeStates = orderedPaths
            .Where(firstBeforeStates.ContainsKey)
            .Select(path => firstBeforeStates[path])
            .ToArray();
        WorkspaceFileEditState[] afterStates = orderedPaths
            .Where(lastAfterStates.ContainsKey)
            .Select(path => lastAfterStates[path])
            .ToArray();
        int fileCount = orderedPaths.Count;
        string transactionDescription = string.IsNullOrWhiteSpace(description)
            ? $"tool round ({transactions.Count} edits across {fileCount} {(fileCount == 1 ? "file" : "files")})"
            : description.Trim();

        return new WorkspaceFileEditTransaction(
            transactionDescription,
            beforeStates,
            afterStates);
    }

    private static string NormalizeSectionId(string? sectionId)
    {
        if (string.IsNullOrWhiteSpace(sectionId))
        {
            return Guid.NewGuid().ToString("D");
        }

        if (!Guid.TryParse(sectionId.Trim(), out Guid parsedSectionId))
        {
            throw new ArgumentException(
                "Section id must be a valid GUID.",
                nameof(sectionId));
        }

        return parsedSectionId.ToString("D");
    }

    private static string NormalizeSectionTitle(string? sectionTitle)
    {
        return string.IsNullOrWhiteSpace(sectionTitle)
            ? DefaultSectionTitle
            : sectionTitle.Trim();
    }

    private static string? NormalizeOptionalSessionId(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        if (!Guid.TryParse(sessionId.Trim(), out Guid parsedSessionId))
        {
            throw new ArgumentException(
                "Session id must be a valid GUID.",
                nameof(sessionId));
        }

        return parsedSessionId.ToString("D");
    }

    private static string NormalizeWorkspacePath(string? workspacePath)
    {
        string normalized = string.IsNullOrWhiteSpace(workspacePath)
            ? Directory.GetCurrentDirectory()
            : workspacePath.Trim();

        return Path.GetFullPath(normalized);
    }

    private static string? NormalizeProviderName(string? providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName))
        {
            return null;
        }

        string normalized = new(
            providerName
                .Trim()
                .Where(static character => !char.IsControl(character))
                .ToArray());

        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : normalized;
    }

    private sealed class FileEditTransactionBatchScope : IDisposable
    {
        private ReplSessionContext? _session;

        public FileEditTransactionBatchScope(ReplSessionContext session)
        {
            _session = session;
        }

        public void Dispose()
        {
            ReplSessionContext? session = _session;
            if (session is null)
            {
                return;
            }

            _session = null;
            session.CompleteFileEditTransactionBatch();
        }
    }

    private static void TryDeleteTemporaryArtifact(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                TryDeleteEmptyParentDirectory(path);
                return;
            }

            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteEmptyParentDirectory(string path)
    {
        try
        {
            string? parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(parent) &&
                Directory.Exists(parent) &&
                !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record TemporaryArtifactReference(
        string Path,
        TemporaryArtifactRetention Retention);
}
