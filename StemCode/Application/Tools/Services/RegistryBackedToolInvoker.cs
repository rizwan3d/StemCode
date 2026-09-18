using StemCode.Application.Abstractions;
using StemCode.Application.Exceptions;
using StemCode.Application.Models;
using StemCode.Application.Permissions;
using StemCode.Application.Tools.Models;
using StemCode.Application.Tools.Serialization;
using StemCode.Application.Utilities;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace StemCode.Application.Tools.Services;

internal sealed class RegistryBackedToolInvoker : IToolInvoker
{
    private static readonly TimeSpan AgentDelegateTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CacheEntryLifetime = TimeSpan.FromMinutes(10);
    private const int MaxCachedToolResults = 256;
    private const int FallbackDefaultTimeoutSeconds = 180;

    private readonly object _cacheSyncRoot = new();
    private readonly Dictionary<ToolResultCacheKey, ToolResultCacheEntry> _toolResultCache = new();
    private readonly TimeSpan _defaultTimeout;
    private readonly ILifecycleHookService _lifecycleHookService;
    private readonly IPermissionApprovalPrompt _permissionApprovalPrompt;
    private readonly ToolPermissionEvaluator _permissionEvaluator;
    private readonly SemaphoreSlim _permissionApprovalSemaphore = new(1, 1);
    private readonly IToolRegistry _toolRegistry;
    private long _cacheUseSequence;

    public RegistryBackedToolInvoker(
        IToolRegistry toolRegistry,
        ToolPermissionEvaluator permissionEvaluator,
        IPermissionApprovalPrompt permissionApprovalPrompt,
        TimeSpan? defaultTimeout = null,
        ILifecycleHookService? lifecycleHookService = null,
        ToolExecutionSettings? toolExecutionSettings = null)
    {
        _toolRegistry = toolRegistry;
        _permissionEvaluator = permissionEvaluator;
        _permissionApprovalPrompt = permissionApprovalPrompt;
        _lifecycleHookService = lifecycleHookService ?? DisabledLifecycleHookService.Instance;
        _defaultTimeout = defaultTimeout ?? TimeSpan.FromSeconds(
            Math.Max(1, toolExecutionSettings?.DefaultTimeoutSeconds ?? FallbackDefaultTimeoutSeconds));
    }

    public async Task<ToolInvocationResult> InvokeAsync(
        ConversationToolCall toolCall,
        ReplSessionContext session,
        ConversationExecutionPhase executionPhase,
        IReadOnlySet<string> allowedToolNames,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(allowedToolNames);
        cancellationToken.ThrowIfCancellationRequested();

        bool toolNameRecognized = _toolRegistry.TryResolve(toolCall.Name, out _);

        if (!allowedToolNames.Contains(toolCall.Name))
        {
            string phaseName = executionPhase == ConversationExecutionPhase.Planning
                ? "planning"
                : "execution";

            return new ToolInvocationResult(
                toolCall.Id,
                toolCall.Name,
                ToolResultFactory.PermissionDenied(
                    "tool_not_available_in_phase",
                    $"Tool '{toolCall.Name}' is not available during the {phaseName} phase.",
                    new ToolRenderPayload(
                        $"Tool unavailable: {toolCall.Name}",
                        $"'{toolCall.Name}' cannot be used during the {phaseName} phase.")),
                toolNameRecognized);
        }

        if (!_toolRegistry.TryResolve(toolCall.Name, out ToolRegistration? registration) || registration is null)
        {
            return new ToolInvocationResult(
                toolCall.Id,
                toolCall.Name,
                ToolResultFactory.NotFound(
                    "tool_not_found",
                    $"Tool '{toolCall.Name}' is not registered in this agent.",
                    new ToolRenderPayload(
                        $"Unknown tool: {toolCall.Name}",
                        $"The LLM requested '{toolCall.Name}', but this agent does not have that tool registered.")),
                toolNameRecognized: false);
        }

        JsonElement arguments;

        try
        {
            arguments = ParseArguments(toolCall);
            arguments = ToolArgumentRepairer.RepairIfNeeded(arguments, registration.Tool.Schema, session);
        }
        catch (JsonException exception)
        {
            return new ToolInvocationResult(
                toolCall.Id,
                toolCall.Name,
                ToolResultFactory.InvalidArguments(
                    "invalid_json_arguments",
                    $"Tool '{toolCall.Name}' received invalid JSON arguments: {exception.Message}",
                    new ToolRenderPayload(
                        $"Invalid tool arguments: {toolCall.Name}",
                        $"The LLM produced malformed JSON arguments for '{toolCall.Name}'.")));
        }
        catch (InvalidOperationException exception)
        {
            return new ToolInvocationResult(
                toolCall.Id,
                toolCall.Name,
                ToolResultFactory.InvalidArguments(
                    "invalid_tool_arguments",
                    exception.Message,
                    new ToolRenderPayload(
                        $"Invalid tool arguments: {toolCall.Name}",
                        exception.Message)));
        }

        ToolExecutionContext executionContext = new(
            toolCall.Id,
            toolCall.Name,
            arguments,
            session,
            executionPhase);

        PermissionEvaluationResult permissionResult = _permissionEvaluator.Evaluate(
            registration.PermissionPolicy,
            new PermissionEvaluationContext(executionContext));

        if (permissionResult.Decision == PermissionEvaluationDecision.RequiresApproval)
        {
            await _permissionApprovalSemaphore.WaitAsync(cancellationToken);
            try
            {
                permissionResult = await ResolveApprovalAsync(
                    registration.PermissionPolicy,
                    executionContext,
                    permissionResult,
                    cancellationToken);
            }
            finally
            {
                _permissionApprovalSemaphore.Release();
            }
        }

        if (!permissionResult.IsAllowed)
        {
            await RunHooksAsync(
                [LifecycleHookEvents.OnPermissionDenied],
                executionContext,
                result: null,
                cancellationToken);

            string reasonCode = permissionResult.ReasonCode!;
            string reason = permissionResult.Reason!;
            string title = permissionResult.Decision == PermissionEvaluationDecision.RequiresApproval
                ? $"Approval required: {toolCall.Name}"
                : $"Permission denied: {toolCall.Name}";

            return new ToolInvocationResult(
                toolCall.Id,
                toolCall.Name,
                ToolResultFactory.PermissionDenied(
                    reasonCode,
                    reason,
                    new ToolRenderPayload(
                        title,
                        reason)));
        }

        LifecycleHookRunResult beforeHookResult = await RunHooksAsync(
            CreateBeforeHookEvents(executionContext),
            executionContext,
            result: null,
            cancellationToken);
        if (!beforeHookResult.IsAllowed)
        {
            return CreateHookBlockedInvocationResult(toolCall, beforeHookResult);
        }

        ToolResult toolResult;
        bool executedTool = false;
        ToolResultCacheKey? cacheKey = TryCreateCacheKey(
            executionContext,
            out ToolResultCacheValidation? beforeValidation);
        if (cacheKey is not null &&
            TryGetCachedToolResult(cacheKey, out ToolResult? cachedResult))
        {
            toolResult = cachedResult;
        }
        else
        {
            executedTool = true;
            toolResult = await ExecuteToolAsync(
                registration.Tool,
                toolCall.Name,
                executionContext,
                cancellationToken);
        }

        LifecycleHookRunResult afterHookResult = await RunHooksAsync(
            CreateAfterHookEvents(executionContext, toolResult),
            executionContext,
            toolResult,
            cancellationToken);

        if (!afterHookResult.IsAllowed)
        {
            if (executedTool &&
                ShouldInvalidateCacheAfterToolResult(toolCall.Name, toolResult))
            {
                InvalidateSessionCache(session);
            }

            return CreateHookBlockedInvocationResult(toolCall, afterHookResult);
        }

        if (cacheKey is not null)
        {
            if (executedTool &&
                toolResult.IsSuccess)
            {
                TryStoreCachedToolResult(
                    cacheKey,
                    toolResult,
                    beforeValidation,
                    executionContext);
            }
        }
        else if (executedTool &&
                 ShouldInvalidateCacheAfterToolResult(toolCall.Name, toolResult))
        {
            InvalidateSessionCache(session);
        }

        return new ToolInvocationResult(toolCall.Id, toolCall.Name, toolResult);
    }

    private async Task<ToolResult> ExecuteToolAsync(
        ITool tool,
        string toolName,
        ToolExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        TimeSpan timeout = GetToolTimeout(toolName);
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            return await tool.ExecuteAsync(
                executionContext,
                timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutSource.IsCancellationRequested)
        {
            return ToolResultFactory.ExecutionError(
                "tool_timeout",
                $"Tool '{toolName}' timed out after {timeout.TotalSeconds:0} seconds.",
                new ToolRenderPayload(
                    $"Tool timed out: {toolName}",
                    $"'{toolName}' did not finish within {timeout.TotalSeconds:0} seconds."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ToolResultFactory.ExecutionError(
                "tool_execution_failed",
                $"Tool execution failed unexpectedly: {exception.Message}",
                new ToolRenderPayload(
                    $"Tool failed: {toolName}",
                    exception.Message));
        }
    }

    private ToolResultCacheKey? TryCreateCacheKey(
        ToolExecutionContext context,
        out ToolResultCacheValidation? validation)
    {
        validation = null;
        if (!IsCacheableToolCall(context))
        {
            return null;
        }

        validation = CreateCacheValidation(context);
        return new ToolResultCacheKey(
            context.Session.SessionId,
            Path.GetFullPath(context.Session.WorkspacePath),
            context.ExecutionPhase,
            context.ToolName,
            context.Session.WorkingDirectory,
            CreateCanonicalJson(context.Arguments));
    }

    private bool TryGetCachedToolResult(
        ToolResultCacheKey key,
        out ToolResult result)
    {
        result = null!;
        lock (_cacheSyncRoot)
        {
            if (!_toolResultCache.TryGetValue(key, out ToolResultCacheEntry? entry))
            {
                return false;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (now - entry.CachedAtUtc > CacheEntryLifetime ||
                !IsCacheValidationCurrent(entry.Validation))
            {
                _toolResultCache.Remove(key);
                return false;
            }

            entry.LastUsed = ++_cacheUseSequence;
            result = entry.Result;
            return true;
        }
    }

    private void TryStoreCachedToolResult(
        ToolResultCacheKey key,
        ToolResult result,
        ToolResultCacheValidation? beforeValidation,
        ToolExecutionContext context)
    {
        ToolResultCacheValidation? afterValidation = CreateCacheValidation(context);
        if (!CacheValidationMatches(beforeValidation, afterValidation))
        {
            return;
        }

        lock (_cacheSyncRoot)
        {
            _toolResultCache[key] = new ToolResultCacheEntry(
                result,
                afterValidation,
                DateTimeOffset.UtcNow,
                ++_cacheUseSequence);

            if (_toolResultCache.Count <= MaxCachedToolResults)
            {
                return;
            }

            foreach (ToolResultCacheKey staleKey in _toolResultCache
                         .OrderBy(static pair => pair.Value.LastUsed)
                         .Take(_toolResultCache.Count - MaxCachedToolResults)
                         .Select(static pair => pair.Key)
                         .ToArray())
            {
                _toolResultCache.Remove(staleKey);
            }
        }
    }

    private void InvalidateSessionCache(ReplSessionContext session)
    {
        lock (_cacheSyncRoot)
        {
            foreach (ToolResultCacheKey key in _toolResultCache.Keys
                         .Where(key => string.Equals(key.SessionId, session.SessionId, StringComparison.Ordinal))
                         .ToArray())
            {
                _toolResultCache.Remove(key);
            }
        }
    }

    private static bool ShouldInvalidateCacheAfterToolResult(
        string toolName,
        ToolResult result)
    {
        if (result.Status is ToolResultStatus.PermissionDenied or
            ToolResultStatus.InvalidArguments or
            ToolResultStatus.NotFound)
        {
            return false;
        }

        if (toolName.StartsWith(AgentToolNames.McpToolPrefix, StringComparison.Ordinal) ||
            toolName.StartsWith(AgentToolNames.CustomToolPrefix, StringComparison.Ordinal))
        {
            return true;
        }

        return toolName is AgentToolNames.ApplyPatch or
            AgentToolNames.FileWrite or
            AgentToolNames.InsertContent or
            AgentToolNames.FileDelete or
            AgentToolNames.SearchAndReplace or
            AgentToolNames.ShellCommand or
            AgentToolNames.AgentDelegate or
            AgentToolNames.AgentOrchestrate or
            AgentToolNames.CodebaseIndex;
    }

    private static bool IsCacheableToolCall(ToolExecutionContext context)
    {
        return context.ToolName switch
        {
            AgentToolNames.FileRead => true,
            AgentToolNames.TextSearch => true,
            AgentToolNames.SearchFiles => true,
            AgentToolNames.SkillLoad => true,
            AgentToolNames.DirectoryList => !ToolArguments.GetBoolean(context.Arguments, "recursive"),
            AgentToolNames.ShellCommand => IsCacheableShellCheck(context.Arguments),
            _ => false
        };
    }

    private static bool IsCacheableShellCheck(JsonElement arguments)
    {
        string? terminalAction = ToolArguments.GetOptionalString(arguments, "terminal_action");
        if (!string.IsNullOrWhiteSpace(terminalAction) &&
            !string.Equals(terminalAction, "run", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (ToolArguments.GetBoolean(arguments, "background") ||
            ToolArguments.GetBoolean(arguments, "pty"))
        {
            return false;
        }

        if (ShellCommandSandboxArguments.TryGetSandboxPermissions(
                arguments,
                "sandbox_permissions",
                out ShellCommandSandboxPermissions sandboxPermissions,
                out _) &&
            sandboxPermissions == ShellCommandSandboxPermissions.RequireEscalated)
        {
            return false;
        }

        if (!ToolArguments.TryGetNonEmptyString(arguments, "command", out string? command))
        {
            return false;
        }

        string normalizedCommand = ShellCommandText.NormalizeCommandText(command!);
        if (ShellCommandText.ContainsControlSyntax(normalizedCommand))
        {
            return false;
        }

        IReadOnlyList<ShellCommandSegment> segments = ShellCommandText.ParseSegments(normalizedCommand);
        if (segments.Count != 1)
        {
            return false;
        }

        string[] tokens = ShellCommandText.Tokenize(segments[0].CommandText);
        if (tokens.Length == 0)
        {
            return false;
        }

        string commandName = ShellCommandText.NormalizeCommandToken(tokens[0]).ToLowerInvariant();
        return commandName switch
        {
            "git" => IsCacheableGitCheck(tokens),
            "dotnet" or
            "node" or
            "npm" or
            "python" or
            "python3" or
            "py" => IsVersionProbe(tokens),
            "pwd" or
            "ls" or
            "dir" or
            "rg" or
            "grep" or
            "findstr" or
            "cat" or
            "type" or
            "get-content" or
            "head" or
            "tail" or
            "wc" => true,
            _ => false
        };
    }

    private static bool IsCacheableGitCheck(IReadOnlyList<string> tokens)
    {
        int index = 1;
        while (index < tokens.Count &&
               tokens[index].StartsWith("-", StringComparison.Ordinal))
        {
            if (string.Equals(tokens[index], "-C", StringComparison.Ordinal) &&
                index + 1 < tokens.Count)
            {
                index += 2;
                continue;
            }

            return false;
        }

        if (index >= tokens.Count)
        {
            return false;
        }

        string subcommand = tokens[index].ToLowerInvariant();
        return subcommand switch
        {
            "status" or
            "diff" or
            "rev-parse" or
            "ls-files" or
            "log" or
            "show" => true,
            "branch" => IsCacheableGitBranch(tokens, index + 1),
            _ => false
        };
    }

    private static bool IsCacheableGitBranch(
        IReadOnlyList<string> tokens,
        int firstArgumentIndex)
    {
        if (firstArgumentIndex >= tokens.Count)
        {
            return true;
        }

        for (int index = firstArgumentIndex; index < tokens.Count; index++)
        {
            if (tokens[index] is not ("--show-current" or "-a" or "-r" or "-v" or "-vv"))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsVersionProbe(IReadOnlyList<string> tokens)
    {
        return tokens.Count is >= 2 and <= 3 &&
               tokens.Skip(1).All(static token =>
                   token is "--version" or "-v" or "-V" or "--info");
    }

    private static ToolResultCacheValidation? CreateCacheValidation(
        ToolExecutionContext context)
    {
        return context.ToolName switch
        {
            AgentToolNames.FileRead => TryCreatePathValidation(context, fileRequired: true),
            AgentToolNames.DirectoryList => TryCreatePathValidation(context, fileRequired: false),
            _ => null
        };
    }

    private static ToolResultCacheValidation? TryCreatePathValidation(
        ToolExecutionContext context,
        bool fileRequired)
    {
        string? requestedPath = ToolArguments.GetOptionalString(context.Arguments, "path");
        if (fileRequired &&
            string.IsNullOrWhiteSpace(requestedPath))
        {
            return null;
        }

        try
        {
            string relativePath = context.Session.ResolvePathFromWorkingDirectory(requestedPath);
            string fullPath = WorkspaceResolvedPath.Resolve(
                context.Session.WorkspacePath,
                relativePath,
                ToolPathAccessKind.Read).CanonicalFullPath;
            return TryCreateFileSystemValidation(fullPath);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static ToolResultCacheValidation? TryCreateFileSystemValidation(string fullPath)
    {
        try
        {
            if (File.Exists(fullPath))
            {
                FileInfo info = new(fullPath);
                return new ToolResultCacheValidation(
                    Path.GetFullPath(fullPath),
                    Exists: true,
                    IsDirectory: false,
                    Length: info.Length,
                    LastWriteTimeUtc: info.LastWriteTimeUtc);
            }

            if (Directory.Exists(fullPath))
            {
                DirectoryInfo info = new(fullPath);
                return new ToolResultCacheValidation(
                    Path.GetFullPath(fullPath),
                    Exists: true,
                    IsDirectory: true,
                    Length: 0,
                    LastWriteTimeUtc: info.LastWriteTimeUtc);
            }

            return new ToolResultCacheValidation(
                Path.GetFullPath(fullPath),
                Exists: false,
                IsDirectory: false,
                Length: 0,
                LastWriteTimeUtc: DateTime.MinValue);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool CacheValidationMatches(
        ToolResultCacheValidation? before,
        ToolResultCacheValidation? after)
    {
        if (before is null || after is null)
        {
            return before is null && after is null;
        }

        return AreSameValidation(before, after);
    }

    private static bool IsCacheValidationCurrent(ToolResultCacheValidation? validation)
    {
        if (validation is null)
        {
            return true;
        }

        ToolResultCacheValidation? current = TryCreateFileSystemValidation(validation.FullPath);
        return current is not null &&
               AreSameValidation(validation, current);
    }

    private static bool AreSameValidation(
        ToolResultCacheValidation left,
        ToolResultCacheValidation right)
    {
        return WorkspacePath.PathEquals(left.FullPath, right.FullPath) &&
               left.Exists == right.Exists &&
               left.IsDirectory == right.IsDirectory &&
               left.Length == right.Length &&
               left.LastWriteTimeUtc == right.LastWriteTimeUtc;
    }

    private static string CreateCanonicalJson(JsonElement element)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            WriteCanonicalJson(element, writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonicalJson(
        JsonElement element,
        Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element
                             .EnumerateObject()
                             .OrderBy(static property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(property.Value, writer);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteCanonicalJson(item, writer);
                }

                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    private TimeSpan GetToolTimeout(string toolName)
    {
        if (toolName.StartsWith(AgentToolNames.McpToolPrefix, StringComparison.Ordinal) ||
            toolName.StartsWith(AgentToolNames.CustomToolPrefix, StringComparison.Ordinal))
        {
            return TimeSpan.FromMinutes(10);
        }

        return toolName is AgentToolNames.AgentDelegate or AgentToolNames.AgentOrchestrate
            ? AgentDelegateTimeout
            : _defaultTimeout;
    }

    private async Task<LifecycleHookRunResult> RunHooksAsync(
        IReadOnlyList<string> eventNames,
        ToolExecutionContext executionContext,
        ToolResult? result,
        CancellationToken cancellationToken)
    {
        foreach (string eventName in eventNames)
        {
            LifecycleHookRunResult hookResult = await _lifecycleHookService.RunAsync(
                CreateHookContext(eventName, executionContext, result),
                cancellationToken);
            if (!hookResult.IsAllowed)
            {
                return hookResult;
            }
        }

        return LifecycleHookRunResult.Allowed();
    }

    private static IReadOnlyList<string> CreateBeforeHookEvents(ToolExecutionContext context)
    {
        List<string> events = [LifecycleHookEvents.BeforeToolCall];
        AddSpecificHookEvents(context, result: null, events, before: true);
        return events.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<string> CreateAfterHookEvents(
        ToolExecutionContext context,
        ToolResult result)
    {
        List<string> events = [];
        AddSpecificHookEvents(context, result, events, before: false);
        events.Add(LifecycleHookEvents.AfterToolCall);

        if (!result.IsSuccess)
        {
            events.Add(LifecycleHookEvents.AfterToolFailure);
        }

        return events.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static void AddSpecificHookEvents(
        ToolExecutionContext context,
        ToolResult? result,
        List<string> events,
        bool before)
    {
        switch (context.ToolName)
        {
            case AgentToolNames.FileRead:
            case AgentToolNames.DirectoryList:
                events.Add(before ? LifecycleHookEvents.BeforeFileRead : LifecycleHookEvents.AfterFileRead);
                break;

            case AgentToolNames.FileWrite:
            case AgentToolNames.InsertContent:
            case AgentToolNames.ApplyPatch:
                events.Add(before ? LifecycleHookEvents.BeforeFileWrite : LifecycleHookEvents.AfterFileWrite);
                break;

            case AgentToolNames.FileDelete:
                events.Add(before ? LifecycleHookEvents.BeforeFileDelete : LifecycleHookEvents.AfterFileDelete);
                break;

            case AgentToolNames.SearchFiles:
            case AgentToolNames.TextSearch:
            case AgentToolNames.CodebaseIndex:
                events.Add(before ? LifecycleHookEvents.BeforeFileSearch : LifecycleHookEvents.AfterFileSearch);
                break;

            case AgentToolNames.ShellCommand:
                events.Add(before ? LifecycleHookEvents.BeforeShellCommand : LifecycleHookEvents.AfterShellCommand);
                if (!before &&
                    TryGetShellExitCode(result, out int shellExitCode) &&
                    shellExitCode != 0)
                {
                    events.Add(LifecycleHookEvents.AfterShellFailure);
                }

                break;

            case AgentToolNames.WebSearch:
            case AgentToolNames.HeadlessBrowser:
                events.Add(before ? LifecycleHookEvents.BeforeWebRequest : LifecycleHookEvents.AfterWebRequest);
                break;

            case AgentToolNames.LessonMemory:
            case AgentToolNames.RepoMemory:
                AddMemoryHookEvents(context, events, before);
                break;

            case AgentToolNames.AgentDelegate:
            case AgentToolNames.AgentOrchestrate:
                events.Add(before ? LifecycleHookEvents.BeforeAgentDelegate : LifecycleHookEvents.AfterAgentDelegate);
                break;
        }
    }

    private static void AddMemoryHookEvents(
        ToolExecutionContext context,
        List<string> events,
        bool before)
    {
        if (!ToolArguments.TryGetNonEmptyString(context.Arguments, "action", out string? action))
        {
            return;
        }

        if (string.Equals(action, "save", StringComparison.OrdinalIgnoreCase))
        {
            events.Add(before ? LifecycleHookEvents.BeforeMemorySave : LifecycleHookEvents.AfterMemorySave);
            events.Add(before ? LifecycleHookEvents.BeforeMemoryWrite : LifecycleHookEvents.AfterMemoryWrite);
            return;
        }

        if (string.Equals(action, "edit", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(action, "delete", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(action, "write", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(action, "update", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(action, "append", StringComparison.OrdinalIgnoreCase))
        {
            events.Add(before ? LifecycleHookEvents.BeforeMemoryWrite : LifecycleHookEvents.AfterMemoryWrite);
        }
    }

    private static LifecycleHookContext CreateHookContext(
        string eventName,
        ToolExecutionContext executionContext,
        ToolResult? result)
    {
        LifecycleHookContext context = new()
        {
            ApplicationName = executionContext.Session.ApplicationName,
            ArgumentsJson = executionContext.Arguments.GetRawText(),
            EventName = eventName,
            ExecutionPhase = executionContext.ExecutionPhase.ToString(),
            ModelId = executionContext.Session.ActiveModelId,
            ProviderName = executionContext.Session.ProviderName,
            ResultMessage = result?.Message,
            ResultStatus = result?.Status.ToString(),
            ResultSuccess = result?.IsSuccess,
            SessionId = executionContext.Session.SessionId,
            ToolCallId = executionContext.ToolCallId,
            ToolName = executionContext.ToolName
        };

        AddToolSpecificHookContext(executionContext, result, context);
        return context;
    }

    private static void AddToolSpecificHookContext(
        ToolExecutionContext executionContext,
        ToolResult? result,
        LifecycleHookContext context)
    {
        if (TryGetRequestedPath(executionContext, out string? path))
        {
            context.Path = path;
        }

        if (string.Equals(executionContext.ToolName, AgentToolNames.ShellCommand, StringComparison.Ordinal))
        {
            if (ToolArguments.TryGetNonEmptyString(executionContext.Arguments, "command", out string? command))
            {
                context.ShellCommand = command;
            }

            if (ToolArguments.TryGetNonEmptyString(executionContext.Arguments, "workingDirectory", out string? workingDirectory))
            {
                context.Metadata["workingDirectory"] = workingDirectory!;
            }

            if (TryGetShellExitCode(result, out int exitCode))
            {
                context.ShellExitCode = exitCode;
            }

            if (TryGetJsonString(result?.JsonResult, "Command", out string? executedCommand))
            {
                context.ShellCommand = executedCommand;
            }
        }

        if (string.Equals(executionContext.ToolName, AgentToolNames.LessonMemory, StringComparison.Ordinal) ||
            string.Equals(executionContext.ToolName, AgentToolNames.RepoMemory, StringComparison.Ordinal))
        {
            context.MemoryAction = ToolArguments.GetOptionalString(executionContext.Arguments, "action");
            context.MemoryTrigger = ToolArguments.GetOptionalString(executionContext.Arguments, "trigger");
            context.MemoryProblem = ToolArguments.GetOptionalString(executionContext.Arguments, "problem");
            if (ToolArguments.TryGetNonEmptyString(executionContext.Arguments, "document", out string? document))
            {
                context.Metadata["memoryDocument"] = document!;
            }
        }

        if (string.Equals(executionContext.ToolName, AgentToolNames.AgentDelegate, StringComparison.Ordinal) &&
            ToolArguments.TryGetNonEmptyString(executionContext.Arguments, "task", out string? delegatedTask))
        {
            context.Metadata["delegatedTask"] = delegatedTask!;
        }

        if (string.Equals(executionContext.ToolName, AgentToolNames.AgentOrchestrate, StringComparison.Ordinal) &&
            executionContext.Arguments.TryGetProperty("tasks", out JsonElement tasksElement) &&
            tasksElement.ValueKind == JsonValueKind.Array)
        {
            context.Metadata["delegatedTaskCount"] = tasksElement.GetArrayLength().ToString(CultureInfo.InvariantCulture);
        }
    }

    private static bool TryGetRequestedPath(
        ToolExecutionContext executionContext,
        out string? path)
    {
        path = null;
        string? requestedPath = executionContext.ToolName switch
        {
            AgentToolNames.FileRead or
            AgentToolNames.FileWrite or
            AgentToolNames.InsertContent or
            AgentToolNames.FileDelete or
            AgentToolNames.DirectoryList or
            AgentToolNames.SearchFiles or
            AgentToolNames.TextSearch or
            AgentToolNames.ShellCommand => ToolArguments.GetOptionalString(executionContext.Arguments, "path") ??
                                          ToolArguments.GetOptionalString(executionContext.Arguments, "workingDirectory"),
            _ => null
        };

        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return false;
        }

        try
        {
            path = executionContext.Session.ResolvePathFromWorkingDirectory(requestedPath);
            return true;
        }
        catch (InvalidOperationException)
        {
            path = requestedPath.Trim();
            return true;
        }
    }

    private static bool TryGetShellExitCode(
        ToolResult? result,
        out int exitCode)
    {
        exitCode = 0;
        return TryGetJsonInt(result?.JsonResult, "ExitCode", out exitCode) ||
               TryGetJsonInt(result?.JsonResult, "exitCode", out exitCode);
    }

    private static bool TryGetJsonInt(
        string? json,
        string propertyName,
        out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(propertyName, out JsonElement property) &&
                   property.TryGetInt32(out value);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetJsonString(
        string? json,
        string propertyName,
        out string? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty(propertyName, out JsonElement property) ||
                property.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(property.GetString()))
            {
                return false;
            }

            value = property.GetString();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static ToolInvocationResult CreateHookBlockedInvocationResult(
        ConversationToolCall toolCall,
        LifecycleHookRunResult hookResult)
    {
        string message = hookResult.Message ??
                         $"Lifecycle hook '{hookResult.FailedHookName}' blocked tool '{toolCall.Name}'.";
        return new ToolInvocationResult(
            toolCall.Id,
            toolCall.Name,
            ToolResultFactory.ExecutionError(
                "lifecycle_hook_blocked",
                message,
                new ToolRenderPayload(
                    $"Lifecycle hook blocked: {toolCall.Name}",
                    message)));
    }

    private async Task<PermissionEvaluationResult> ResolveApprovalAsync(
        ToolPermissionPolicy permissionPolicy,
        ToolExecutionContext executionContext,
        PermissionEvaluationResult permissionResult,
        CancellationToken cancellationToken)
    {
        PermissionRequestDescriptor request = permissionResult.Request ??
                                              new PermissionRequestDescriptor(
                                                  executionContext.ToolName,
                                                  executionContext.ToolName,
                                                  [executionContext.ToolName],
                                                  []);

        PermissionApprovalChoice decision;
        try
        {
            decision = await _permissionApprovalPrompt.PromptAsync(
                new PermissionApprovalRequest(
                    executionContext.Session.ApplicationName,
                    request,
                    permissionResult.Reason ?? $"Permission approval is required for '{executionContext.ToolName}'."),
                cancellationToken);
        }
        catch (PromptCancelledException)
        {
            return PermissionEvaluationResult.Denied(
                "permission_request_cancelled",
                $"Permission approval was cancelled for tool '{executionContext.ToolName}'.",
                PermissionMode.Deny,
                request);
        }

        switch (decision)
        {
            case PermissionApprovalChoice.AllowOnce:
                return _permissionEvaluator.Evaluate(
                    permissionPolicy,
                    new PermissionEvaluationContext(executionContext, approvalGranted: true));

            case PermissionApprovalChoice.AllowForAgent:
                executionContext.Session.AddPermissionOverride(CreateOverrideRule(
                    request,
                    PermissionMode.Allow));
                return _permissionEvaluator.Evaluate(
                    permissionPolicy,
                    new PermissionEvaluationContext(executionContext));

            case PermissionApprovalChoice.DenyForAgent:
                executionContext.Session.AddPermissionOverride(CreateOverrideRule(
                    request,
                    PermissionMode.Deny));
                return PermissionEvaluationResult.Denied(
                    "permission_denied_by_user",
                    $"Permission was denied for tool '{executionContext.ToolName}' on this agent.",
                    PermissionMode.Deny,
                    request);

            default:
                return PermissionEvaluationResult.Denied(
                    "permission_denied_by_user",
                    $"Permission was denied for tool '{executionContext.ToolName}'.",
                    PermissionMode.Deny,
                    request);
        }
    }

    private static PermissionRule CreateOverrideRule(
        PermissionRequestDescriptor request,
        PermissionMode mode)
    {
        return new PermissionRule
        {
            Mode = mode,
            Patterns = request.Subjects.ToArray(),
            Tools = [request.ToolKind]
        };
    }

    private static JsonElement ParseArguments(ConversationToolCall toolCall)
    {
        if (string.IsNullOrWhiteSpace(toolCall.ArgumentsJson))
        {
            throw new InvalidOperationException(
                $"Tool '{toolCall.Name}' must receive JSON-object arguments.");
        }

        using JsonDocument argumentsDocument = JsonDocument.Parse(toolCall.ArgumentsJson);
        if (argumentsDocument.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"Tool '{toolCall.Name}' must receive JSON-object arguments.");
        }

        return argumentsDocument.RootElement.Clone();
    }

    private sealed record ToolResultCacheKey(
        string SessionId,
        string WorkspacePath,
        ConversationExecutionPhase ExecutionPhase,
        string ToolName,
        string WorkingDirectory,
        string ArgumentsSignature);

    private sealed class ToolResultCacheEntry
    {
        public ToolResultCacheEntry(
            ToolResult result,
            ToolResultCacheValidation? validation,
            DateTimeOffset cachedAtUtc,
            long lastUsed)
        {
            Result = result;
            Validation = validation;
            CachedAtUtc = cachedAtUtc;
            LastUsed = lastUsed;
        }

        public DateTimeOffset CachedAtUtc { get; }

        public long LastUsed { get; set; }

        public ToolResult Result { get; }

        public ToolResultCacheValidation? Validation { get; }
    }

    private sealed record ToolResultCacheValidation(
        string FullPath,
        bool Exists,
        bool IsDirectory,
        long Length,
        DateTime LastWriteTimeUtc);

    private sealed class DisabledLifecycleHookService : ILifecycleHookService
    {
        public static DisabledLifecycleHookService Instance { get; } = new();

        public Task<LifecycleHookRunResult> RunAsync(
            LifecycleHookContext context,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(context);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(LifecycleHookRunResult.Allowed());
        }
    }
}
