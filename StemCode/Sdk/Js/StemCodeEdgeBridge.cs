using System.Collections.Concurrent;
using StemCode.Application.Backend;
using StemCode.Application.Models;
using StemCode.Sdk.Events;

namespace StemCode.Sdk.Js;

/// <summary>
/// Edge.js-compatible entry point used by the JavaScript/TypeScript SDK wrapper.
/// </summary>
public sealed class StemCodeEdgeBridge
{
    private static readonly ConcurrentDictionary<string, StemCodeClient> Clients = new();

    public async Task<object?> Invoke(dynamic input)
    {
        IDictionary<string, object?> request = ToDictionary((object)input);
        string operation = GetRequiredString(request, "operation");

        return operation switch
        {
            "create" => CreateClient(request),
            "initialize" => ToSession(await GetClient(request).InitializeAsync().ConfigureAwait(false)),
            "runTurn" => ToTurnResult(await GetClient(request)
                .RunTurnAsync(GetRequiredString(request, "prompt")).ConfigureAwait(false)),
            "runCommand" => ToCommandResult(await GetClient(request)
                .RunCommandAsync(GetRequiredString(request, "commandText")).ConfigureAwait(false)),
            "dispose" => await DisposeClientAsync(request).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported StemCode SDK operation '{operation}'.")
        };
    }

    private static object CreateClient(IDictionary<string, object?> request)
    {
        StemCodeClientBuilder builder = StemCodeClient.CreateBuilder();
        IDictionary<string, object?> options = GetDictionary(request, "options");
        string provider = GetRequiredString(options, "provider").ToLowerInvariant();
        string? model = GetString(options, "model");
        string? apiKey = GetString(options, "apiKey");
        string? baseUrl = GetString(options, "baseUrl");

        switch (provider)
        {
            case "anthropic":
                builder.UseAnthropic(RequireProviderApiKey(provider, apiKey), model);
                break;
            case "openai":
                builder.UseOpenAi(RequireProviderApiKey(provider, apiKey), model);
                break;
            case "googleaistudio":
            case "google-ai-studio":
            case "google_ai_studio":
                builder.UseGoogleAiStudio(RequireProviderApiKey(provider, apiKey), model);
                break;
            case "openrouter":
                builder.UseOpenRouter(RequireProviderApiKey(provider, apiKey), model);
                break;
            case "deepseek":
                builder.UseDeepSeek(RequireProviderApiKey(provider, apiKey), model);
                break;
            case "groq":
                builder.UseGroq(RequireProviderApiKey(provider, apiKey), model);
                break;
            case "cerebras":
                builder.UseCerebras(RequireProviderApiKey(provider, apiKey), model);
                break;
            case "ollama":
                builder.UseOllama(baseUrl, model);
                break;
            case "lmstudio":
            case "lm-studio":
            case "lm_studio":
                builder.UseLmStudio(baseUrl, model);
                break;
            case "openai-compatible":
            case "openai_compatible":
            case "openaicompatible":
                builder.UseOpenAiCompatible(
                    GetRequiredString(options, "baseUrl"),
                    RequireProviderApiKey(provider, apiKey),
                    model);
                break;
            default:
                throw new InvalidOperationException($"Unsupported provider '{provider}'.");
        }

        ApplyOptionalBuilderSettings(builder, options);

        StemCodeClient client = builder.Build();
        AttachEventSink(client, GetEventSink(request));

        string clientId = Guid.NewGuid().ToString("N");
        Clients[clientId] = client;
        return new { clientId };
    }

    private static void ApplyOptionalBuilderSettings(
        StemCodeClientBuilder builder,
        IDictionary<string, object?> options)
    {
        if (GetString(options, "workspace") is { } workspace)
        {
            builder.WithWorkspace(workspace);
        }

        if (GetString(options, "profile") is { } profile)
        {
            builder.WithProfile(profile);
        }

        if (GetBoolean(options, "useBuildTool"))
        {
            builder.UseBuildTool();
        }

        if (GetString(options, "thinkingMode") is { } thinkingMode)
        {
            builder.WithThinkingMode(thinkingMode);
        }

        if (GetString(options, "sectionId") is { } sectionId)
        {
            builder.ResumeSession(sectionId);
        }

        if (GetString(options, "systemPrompt") is { } systemPrompt)
        {
            builder.WithSystemPrompt(systemPrompt);
        }
        else if (GetBoolean(options, "useStemCodeSystemPrompt"))
        {
            builder.UseStemCodeSystemPrompt();
        }

        if (GetBoolean(options, "autoApproveTools"))
        {
            builder.AutoApproveTools();
        }

        if (GetBoolean(options, "enableProductTelemetry"))
        {
            builder.EnableProductTelemetry();
        }

        if (GetBoolean(options, "enableOpenTelemetryTracing"))
        {
            builder.WithOpenTelemetryTracing();
        }
    }

    private static void AttachEventSink(StemCodeClient client, Func<object, Task<object>>? eventSink)
    {
        if (eventSink is null)
        {
            return;
        }

        client.ReasoningReceived += (_, e) => _ = EmitAsync(eventSink, "reasoning", new
        {
            reasoningText = e.ReasoningText
        });
        client.AssistantMessageChunkReceived += (_, e) => _ = EmitAsync(eventSink, "assistantMessageChunk", new
        {
            text = e.Text
        });
        client.ToolCallsStarted += (_, e) => _ = EmitAsync(eventSink, "toolCallsStarted", new
        {
            toolCalls = e.ToolCalls.Select(ToToolCall).ToArray()
        });
        client.ToolResultsReceived += (_, e) => _ = EmitAsync(eventSink, "toolResults", new
        {
            hasFailures = e.Results.HasFailures,
            displayText = e.Results.ToDisplayText(),
            results = e.Results.Results.Select(ToToolInvocationResult).ToArray()
        });
        client.ExecutionPlanUpdated += (_, e) => _ = EmitAsync(eventSink, "executionPlanUpdated", new
        {
            displayText = e.Progress.ToString()
        });
        client.ProviderRetry += (_, e) => _ = EmitAsync(eventSink, "providerRetry", new
        {
            displayText = e.Progress.ToString()
        });
        client.StatusMessage += (_, e) => _ = EmitAsync(eventSink, "statusMessage", new
        {
            severity = e.Severity.ToString(),
            message = e.Message
        });
    }

    private static async Task<object?> DisposeClientAsync(IDictionary<string, object?> request)
    {
        string clientId = GetRequiredString(request, "clientId");
        if (Clients.TryRemove(clientId, out StemCodeClient? client))
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }

        return new { disposed = true };
    }

    private static StemCodeClient GetClient(IDictionary<string, object?> request)
    {
        string clientId = GetRequiredString(request, "clientId");
        return Clients.TryGetValue(clientId, out StemCodeClient? client)
            ? client
            : throw new InvalidOperationException($"StemCode SDK client '{clientId}' does not exist or was disposed.");
    }

    private static Task<object> EmitAsync(
        Func<object, Task<object>> eventSink,
        string eventName,
        object payload)
    {
        return eventSink(new
        {
            @event = eventName,
            payload
        });
    }

    private static object ToSession(StemCodeSession session)
    {
        return new
        {
            sessionId = session.SessionId,
            providerName = session.ProviderName,
            modelId = session.ModelId,
            agentProfileName = session.AgentProfileName,
            thinkingMode = session.ThinkingMode,
            reasoningEffort = session.ReasoningEffort,
            showThinking = session.ShowThinking,
            sectionTitle = session.SectionTitle,
            isResumedSection = session.IsResumedSection,
            availableModelIds = session.AvailableModelIds.ToArray(),
            conversationHistory = session.ConversationHistory.Select(static message => new
            {
                role = message.Role,
                content = message.Content,
                reasoningContent = message.ReasoningContent,
                reasoningDetailsJson = message.ReasoningDetailsJson
            }).ToArray()
        };
    }

    private static object ToTurnResult(ConversationTurnResult result)
    {
        return new
        {
            kind = result.Kind.ToString(),
            responseText = result.ResponseText,
            reasoningText = result.ReasoningText,
            toolExecutionResult = result.ToolExecutionResult is null
                ? null
                : new
                {
                    hasFailures = result.ToolExecutionResult.HasFailures,
                    displayText = result.ToolExecutionResult.ToDisplayText(),
                    results = result.ToolExecutionResult.Results.Select(ToToolInvocationResult).ToArray()
                }
        };
    }

    private static object ToCommandResult(BackendCommandResult result)
    {
        return new
        {
            command = new
            {
                exitRequested = result.CommandResult.ExitRequested,
                message = result.CommandResult.Message,
                feedbackKind = result.CommandResult.FeedbackKind.ToString(),
                replaySession = result.CommandResult.ReplaySession
            },
            session = ToSession(StemCodeSession.FromBackend(result.SessionInfo))
        };
    }

    private static object ToToolCall(ConversationToolCall toolCall)
    {
        return new
        {
            id = toolCall.Id,
            name = toolCall.Name,
            argumentsJson = toolCall.ArgumentsJson
        };
    }

    private static object ToToolInvocationResult(ToolInvocationResult result)
    {
        return new
        {
            toolCallId = result.ToolCallId,
            toolName = result.ToolName,
            toolNameRecognized = result.ToolNameRecognized,
            status = result.Result.Status.ToString(),
            isSuccess = result.Result.IsSuccess,
            message = result.Result.Message,
            jsonResult = result.Result.JsonResult
        };
    }

    private static IDictionary<string, object?> GetDictionary(
        IDictionary<string, object?> source,
        string name)
    {
        if (!source.TryGetValue(name, out object? value) || value is null)
        {
            return new Dictionary<string, object?>();
        }

        return ToDictionary(value);
    }

    private static IDictionary<string, object?> ToDictionary(object value)
    {
        if (value is IDictionary<string, object?> nullableDictionary)
        {
            return nullableDictionary;
        }

        if (value is IDictionary<string, object> dictionary)
        {
            return dictionary.ToDictionary(static pair => pair.Key, static pair => (object?)pair.Value);
        }

        throw new ArgumentException("Expected a JavaScript object payload.", nameof(value));
    }

    private static Func<object, Task<object>>? GetEventSink(IDictionary<string, object?> request)
    {
        return request.TryGetValue("eventSink", out object? value)
            ? value as Func<object, Task<object>>
            : null;
    }

    private static string GetRequiredString(IDictionary<string, object?> source, string name)
    {
        return GetString(source, name)
            ?? throw new ArgumentException($"A non-empty '{name}' value is required.");
    }

    private static string? GetString(IDictionary<string, object?> source, string name)
    {
        return source.TryGetValue(name, out object? value) && value is not null
            ? value.ToString()
            : null;
    }

    private static bool GetBoolean(IDictionary<string, object?> source, string name)
    {
        if (!source.TryGetValue(name, out object? value) || value is null)
        {
            return false;
        }

        return value switch
        {
            bool boolean => boolean,
            string text => bool.TryParse(text, out bool parsed) && parsed,
            _ => Convert.ToBoolean(value)
        };
    }

    private static string RequireProviderApiKey(string provider, string? apiKey)
    {
        return string.IsNullOrWhiteSpace(apiKey)
            ? throw new ArgumentException($"An apiKey is required for provider '{provider}'.")
            : apiKey;
    }
}
