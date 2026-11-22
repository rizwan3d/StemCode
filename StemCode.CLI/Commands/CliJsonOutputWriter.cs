using StemCode.Application.Backend;
using StemCode.Application.Models;
using System.Text;
using System.Text.Json;

namespace StemCode.CLI;

internal static class CliJsonOutputWriter
{
    public static string FormatCommand(BackendCommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return WriteJson(writer =>
        {
            writer.WriteString("status", IsError(result.CommandResult.FeedbackKind) ? "error" : "ok");
            writer.WriteString("type", "command");
            writer.WriteString("feedbackKind", result.CommandResult.FeedbackKind.ToString());
            WriteStringOrNull(writer, "message", result.CommandResult.Message);
            writer.WriteBoolean("exitRequested", result.CommandResult.ExitRequested);
            WriteSession(writer, result.SessionInfo);
        });
    }

    public static string FormatError(string errorCode, string message)
    {
        return WriteJson(writer =>
        {
            writer.WriteString("status", "error");
            writer.WriteString("type", "error");
            writer.WriteString("errorCode", errorCode);
            writer.WriteString("message", message);
        });
    }

    public static string FormatTurn(
        ConversationTurnResult result,
        BackendSessionInfo sessionInfo)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(sessionInfo);

        return WriteJson(writer =>
        {
            writer.WriteString("status", "ok");
            writer.WriteString("type", "turn");
            writer.WriteString("turnKind", result.Kind.ToString());
            writer.WriteString("response", result.ResponseText ?? string.Empty);
            WriteSession(writer, sessionInfo);

            if (result.Metrics is not null)
            {
                writer.WritePropertyName("metrics");
                writer.WriteStartObject();
                writer.WriteNumber("elapsedMilliseconds", result.Metrics.Elapsed.TotalMilliseconds);
                writer.WriteNumber("estimatedInputTokens", result.Metrics.EstimatedInputTokens);
                writer.WriteNumber("cachedInputTokens", result.Metrics.CachedInputTokens);
                writer.WriteNumber("estimatedOutputTokens", result.Metrics.EstimatedOutputTokens);
                writer.WriteNumber("estimatedTotalTokens", result.Metrics.EstimatedTotalTokens);
                if (result.Metrics.SessionEstimatedOutputTokens is null)
                {
                    writer.WriteNull("sessionEstimatedOutputTokens");
                }
                else
                {
                    writer.WriteNumber(
                        "sessionEstimatedOutputTokens",
                        result.Metrics.SessionEstimatedOutputTokens.Value);
                }

                writer.WriteNumber("providerRetryCount", result.Metrics.ProviderRetryCount);
                writer.WriteNumber("toolRoundCount", result.Metrics.ToolRoundCount);
                writer.WriteEndObject();
            }
        });
    }

    public static string FormatStreamInit(BackendSessionInfo sessionInfo)
    {
        ArgumentNullException.ThrowIfNull(sessionInfo);

        return WriteJson(writer =>
        {
            writer.WriteString("type", "system");
            writer.WriteString("subtype", "init");
            writer.WriteString("cwd", Directory.GetCurrentDirectory());
            WriteSession(writer, sessionInfo);
        });
    }

    public static string FormatStreamEvent(
        string type,
        Action<Utf8JsonWriter> writeProperties)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(writeProperties);

        return WriteJson(writer =>
        {
            writer.WriteString("type", type);
            writeProperties(writer);
        });
    }

    private static bool IsError(ReplFeedbackKind feedbackKind)
    {
        return feedbackKind == ReplFeedbackKind.Error;
    }

    private static string WriteJson(Action<Utf8JsonWriter> writeProperties)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writeProperties(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteSession(
        Utf8JsonWriter writer,
        BackendSessionInfo sessionInfo)
    {
        writer.WritePropertyName("session");
        writer.WriteStartObject();
        writer.WriteString("id", sessionInfo.SessionId);
        writer.WriteString("resumeCommand", sessionInfo.SectionResumeCommand);
        writer.WriteString("provider", sessionInfo.ProviderName);
        writer.WriteString("model", sessionInfo.ModelId);
        writer.WriteString("thinking", sessionInfo.ThinkingMode);
        WriteStringOrNull(writer, "reasoningEffort", sessionInfo.ReasoningEffort);
        writer.WriteBoolean("showThinking", sessionInfo.ShowThinking);
        writer.WriteString("profile", sessionInfo.AgentProfileName);
        writer.WriteString("title", sessionInfo.SectionTitle);
        writer.WriteBoolean("resumed", sessionInfo.IsResumedSection);
        if (sessionInfo.ActiveModelContextWindowTokens is null)
        {
            writer.WriteNull("activeModelContextWindowTokens");
        }
        else
        {
            writer.WriteNumber(
                "activeModelContextWindowTokens",
                sessionInfo.ActiveModelContextWindowTokens.Value);
        }

        writer.WriteEndObject();
    }

    private static void WriteStringOrNull(
        Utf8JsonWriter writer,
        string propertyName,
        string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
            return;
        }

        writer.WriteString(propertyName, value);
    }
}
